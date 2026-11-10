using ModernImageViewer.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    internal sealed class ParallaxLoadContext : AssemblyLoadContext
    {
        private readonly string _extensionDir;
        private readonly AssemblyDependencyResolver _resolver;

        public ParallaxLoadContext(string extensionDir) : base(isCollectible: true)
        {
            _extensionDir = extensionDir;
            string mainAssembly = Path.Combine(extensionDir, "ModernImageViewer.Extensions.Parallax.dll");
            _resolver = new AssemblyDependencyResolver(mainAssembly);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Contracts assembly is shared from the default context
            if (string.Equals(assemblyName.Name, "ModernImageViewer.Contracts", StringComparison.OrdinalIgnoreCase))
                return typeof(IParallaxService).Assembly;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path == null && assemblyName.Name is "Microsoft.ML.OnnxRuntime" or "System.Numerics.Tensors")
            {
                foreach (string root in new[] { _extensionDir, AppContext.BaseDirectory })
                {
                    string candidate = Path.Combine(root, assemblyName.Name + ".dll");
                    if (File.Exists(candidate)) { path = candidate; break; }
                }
            }
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path != null) return LoadUnmanagedDllFromPath(path);

            string file = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : unmanagedDllName + ".dll";
            string localDll = Path.Combine(_extensionDir, file);
            if (File.Exists(localDll)) return LoadUnmanagedDllFromPath(localDll);

            return base.LoadUnmanagedDll(unmanagedDllName);
        }
    }

    internal static class ParallaxHost
    {
        private static IParallaxService? _service;
        private static ParallaxLoadContext? _context;
        private static readonly object _sync = new();

        public static string ExtensionDir =>
            Path.Combine(AppPaths.LocalDirectory, "Extensions", "3D");

        public static string ExtensionDllPath =>
            Path.Combine(ExtensionDir, "ModernImageViewer.Extensions.Parallax.dll");

        public static bool IsInstalled =>
            ExtensionManager.Is3DInstalled;

        public static IParallaxService Service
        {
            get
            {
                if (_service != null) return _service;
                lock (_sync)
                {
                    if (_service != null) return _service;
                    _service = TryLoadExtension() ?? NoOpParallaxService.Instance;
                    return _service;
                }
            }
        }

        public static bool Available => IsInstalled && Service.IsAvailable;

        private static IParallaxService? TryLoadExtension()
        {
            if (!IsInstalled) return null;
            string dir = ExtensionDir;
            string dllPath = ExtensionDllPath;
            if (!File.Exists(dllPath)) return null;

            try
            {
                _context = new ParallaxLoadContext(dir);
                var assembly = _context.LoadFromAssemblyPath(dllPath);
                var serviceType = assembly.GetType("ModernImageViewer.Extensions.Parallax.ParallaxService");
                if (serviceType != null && typeof(IParallaxService).IsAssignableFrom(serviceType))
                {
                    var instance = (IParallaxService?)Activator.CreateInstance(serviceType);
                    var logProp = serviceType.GetProperty("Logger", BindingFlags.Public | BindingFlags.Static);
                    if (logProp != null)
                    {
                        Action<string> logger = PlayTiming.Write;
                        logProp.SetValue(null, logger);
                    }
                    return instance;
                }
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"3D Extension load failed: {ex.Message}");
            }
            return null;
        }

        public static void ReloadExtension()
        {
            lock (_sync)
            {
                UnloadExtension();
                _service = TryLoadExtension() ?? NoOpParallaxService.Instance;
            }
        }

        public static void UnloadExtension()
        {
            lock (_sync)
            {
                _service?.DisposeSession();
                _service = null;
                _context?.Unload();
                _context = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    internal static class DepthMaps
    {
        public static bool Available => ParallaxHost.Available;

        public static Task<IParallaxMap?> ForAsync(string path) =>
            ParallaxHost.Service.GetDepthMapAsync(path);

        // Wall time of the last 3D filter check for a path, including any wait behind the picture on screen.
        private static readonly object CheckTimesGate = new();
        private static readonly Dictionary<string, long> CheckMilliseconds = new(StringComparer.OrdinalIgnoreCase);

        public static bool TryCheckMilliseconds(string path, out long milliseconds)
        {
            if (string.IsNullOrEmpty(path))
            {
                milliseconds = 0;
                return false;
            }
            lock (CheckTimesGate)
                return CheckMilliseconds.TryGetValue(path, out milliseconds);
        }

        public static async Task<ParallaxFit.Verdict?> VerdictAsync(string path)
        {
            if (!Available || string.IsNullOrEmpty(path)) return null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (PhotoFactsReader.TryGetCached(path, out var facts) && facts?.ScreenLike == true)
                    return ParallaxFit.Verdict.Exclude;
                var judged = await ParallaxJudge.ForAsync(path).ConfigureAwait(false);
                if (judged == null) return null;
                return judged.Fit == ParallaxJudge.Fit.Out ? ParallaxFit.Verdict.Exclude : ParallaxFit.Verdict.Pass;
            }
            finally
            {
                lock (CheckTimesGate)
                    CheckMilliseconds[path] = clock.ElapsedMilliseconds;
            }
        }

        public static ParallaxFit.Verdict? CachedVerdict(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (PhotoFactsReader.TryGetCached(path, out var facts) && facts?.ScreenLike == true)
                return ParallaxFit.Verdict.Exclude;
            if (ParallaxJudge.TryGetCached(path, out var judged) && judged != null)
                return judged.Fit == ParallaxJudge.Fit.Out ? ParallaxFit.Verdict.Exclude : ParallaxFit.Verdict.Pass;
            return null;
        }

        public static void DisposeSession() =>
            ParallaxHost.Service.DisposeSession();
    }

    internal static class SubjectMasks
    {
        public static bool Available => ParallaxHost.Available;

        public static Task<IParallaxMask?> ForAsync(string path) =>
            ParallaxHost.Service.GetSubjectMaskAsync(path);

        public static void DisposeSession() =>
            ParallaxHost.Service.DisposeSession();
    }
}

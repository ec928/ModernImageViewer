using ModernImageViewer.Contracts;
using System;
using System.IO;
using System.Reflection;

namespace ModernImageViewer
{
    /// <summary>Loads the Upscale extension from its install folder, the same way the 3D extension is loaded.</summary>
    internal static class UpscaleHost
    {
        public const string DllName = "ModernImageViewer.Extensions.Upscale.dll";

        private static IUpscaleService? _service;
        private static ParallaxLoadContext? _context;
        private static readonly object _sync = new();

        public static bool IsInstalled => ExtensionManager.IsUpscaleInstalled;

        public static IUpscaleService Service
        {
            get
            {
                if (_service != null) return _service;
                lock (_sync)
                {
                    if (_service != null) return _service;
                    _service = TryLoadExtension() ?? NoOpUpscaleService.Instance;
                    return _service;
                }
            }
        }

        public static bool Available => IsInstalled && Service.IsAvailable;

        private static IUpscaleService? TryLoadExtension()
        {
            if (!IsInstalled) return null;
            string dir = ExtensionManager.ExtensionUpscaleDir;
            string dllPath = Path.Combine(dir, DllName);
            if (!File.Exists(dllPath)) return null;

            try
            {
                _context = new ParallaxLoadContext(dir, DllName);
                var assembly = _context.LoadFromAssemblyPath(dllPath);
                var serviceType = assembly.GetType("ModernImageViewer.Extensions.Upscale.UpscaleService");
                if (serviceType != null && typeof(IUpscaleService).IsAssignableFrom(serviceType))
                {
                    var instance = (IUpscaleService?)Activator.CreateInstance(serviceType);
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
                PlayTiming.Write($"Upscale extension load failed: {ex.Message}");
            }
            return null;
        }

        public static void ReloadExtension()
        {
            lock (_sync)
            {
                UnloadExtension();
                _service = TryLoadExtension() ?? NoOpUpscaleService.Instance;
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
}

using Microsoft.ML.OnnxRuntime;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;

namespace ModernImageViewer.Extensions.Upscale
{
    /// <summary>
    /// The prototype kept onnxruntime, DirectML and the managed wrapper beside the exe, so the
    /// loader found that runtime before the one Windows ships in System32. This viewer removes
    /// the copy beside the exe. Load the extension's runtime by path, and bind the managed
    /// wrapper to it, before a session asks for onnxruntime by name.
    /// </summary>
    internal static class NativeRuntime
    {
        private static int _once;
        public static string Which { get; private set; } = "not loaded";
        // Same loader as the 3D extension's: this extension carries its own runtime copy, so it installs and
        // removes on its own. One upscale runs on the card at a time.
        private static readonly SemaphoreSlim Gpu = new(1, 1);

        /// <summary>
        /// DirectML rejects memory-pattern planning and parallel execution. Windows ML also leaves
        /// the runtime's worker threads asleep unless spinning is turned back on.
        /// </summary>
        public static SessionOptions GpuSession()
        {
            var options = new SessionOptions
            {
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "1");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "1");
            return options;
        }

        /// <summary>False when DirectML could not be added. The caller decides whether to try again.</summary>
        public static bool TryUseDirectMl(SessionOptions options, out string? error)
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                error = ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        public static InferenceSession CreateSession(string model, SessionOptions options)
        {
            Gpu.Wait();
            try { return new InferenceSession(model, options); }
            finally { Gpu.Release(); }
        }

        public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(InferenceSession session, NamedOnnxValue[] inputs)
        {
            Gpu.Wait();
            try { return session.Run(inputs); }
            finally { Gpu.Release(); }
        }

        public static void Ensure()
        {
            if (System.Threading.Interlocked.Exchange(ref _once, 1) != 0) return;
            AllowFullSpeed();
            string dir = Path.GetDirectoryName(typeof(NativeRuntime).Assembly.Location) ?? "";
            IntPtr runtimeHandle = IntPtr.Zero;
            try
            {
                string directMl = Path.Combine(dir, "DirectML.dll");
                string runtime = Path.Combine(dir, "onnxruntime.dll");
                if (File.Exists(directMl)) NativeLibrary.Load(directMl);
                if (File.Exists(runtime)) runtimeHandle = NativeLibrary.Load(runtime);
            }
            catch (Exception ex)
            {
                Which = "load failed " + ex.GetType().Name + " " + ex.Message;
                return;
            }

            try
            {
                Assembly? ort = LoadManaged(dir);
                if (ort != null && runtimeHandle != IntPtr.Zero)
                {
                    NativeLibrary.SetDllImportResolver(ort, (libraryName, assembly, searchPath) =>
                    {
                        if (libraryName.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase)
                            || libraryName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                            return runtimeHandle;
                        return IntPtr.Zero;
                    });
                }
            }
            catch (Exception ex)
            {
                Which = Describe() + " (resolver " + ex.GetType().Name + " " + ex.Message + ")";
                return;
            }

            Which = Describe();
        }

        private static void AllowFullSpeed()
        {
            try
            {
                var state = new PowerThrottle { Version = 1, ControlMask = 0x1, StateMask = 0 };
                SetProcessInformation(Process.GetCurrentProcess().Handle, 4, ref state, (uint)Marshal.SizeOf<PowerThrottle>());
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottle
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int cls, ref PowerThrottle state, uint size);

        private static Assembly? LoadManaged(string dir)
        {
            var alc = AssemblyLoadContext.GetLoadContext(typeof(NativeRuntime).Assembly);
            if (alc != null)
            {
                try { return alc.LoadFromAssemblyName(new AssemblyName("Microsoft.ML.OnnxRuntime")); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                }
            }

            string managed = Path.Combine(dir, "Microsoft.ML.OnnxRuntime.dll");
            if (!File.Exists(managed)) return null;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(managed);
        }

        private static string Describe()
        {
            string found = "";
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                string name = module.ModuleName;
                if (!name.Equals("DirectML.dll", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (found.Length > 0) found += " | ";
                found += module.FileName;
            }
            return found.Length == 0 ? "runtime not in process" : found;
        }
    }
}

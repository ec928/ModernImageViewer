using ModernImageViewer.Contracts;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    internal static class ExtensionManager
    {
        public const string DepthAnythingUrl = "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx";
        public const string BiRefNetUrl = "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx";

        public const long DepthModelExpectedBytes = 99060839L;
        public const string DepthModelSha256 = "AFB6A5C28F3B6BF1618C6E43F02073EF9DFDC70E937502D51603E57B0A1DF10C";

        public const long MaskModelExpectedBytes = 114538221L;
        public const string MaskModelSha256 = "D39B897CEB16AE654C1731F3DBA0CF9B368D9CAE74B5A57459B455CC8BFEC402";

        public static string Extension3DDir =>
            Path.Combine(AppPaths.LocalDirectory, "Extensions", "3D");

        public static string DllPath =>
            Path.Combine(Extension3DDir, "ModernImageViewer.Extensions.Parallax.dll");

        public static string DepthModelPath =>
            Path.Combine(Extension3DDir, "Models", "depth-anything-v2-small.onnx");

        public static string MaskModelPath =>
            Path.Combine(Extension3DDir, "Models", "birefnet-lite.onnx");

        public static bool Is3DInstalled =>
            File.Exists(DllPath) && File.Exists(DepthModelPath) && File.Exists(MaskModelPath);

        static ExtensionManager()
        {
            CleanupPendingDeletes();
        }

        public static void CleanupPendingDeletes()
        {
            try
            {
                if (!Directory.Exists(Extension3DDir)) return;
                foreach (string f in Directory.GetFiles(Extension3DDir, "*.pending-delete", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
                if (Directory.GetFileSystemEntries(Extension3DDir).Length == 0)
                {
                    try { Directory.Delete(Extension3DDir, recursive: true); } catch { }
                }
            }
            catch { }
        }

        public static long Get3DDiskSizeBytes()
        {
            try
            {
                if (!Is3DInstalled || !Directory.Exists(Extension3DDir)) return 0;
                long total = 0;
                var dirInfo = new DirectoryInfo(Extension3DDir);
                foreach (var f in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!f.Name.EndsWith(".pending-delete", StringComparison.OrdinalIgnoreCase))
                        total += f.Length;
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        public static string Get3DDiskSizeFormatted()
        {
            long bytes = Get3DDiskSizeBytes();
            if (bytes == 0) return "0 MB";
            double mb = bytes / (1024.0 * 1024.0);
            return $"{mb:0.#} MB";
        }

        private static bool VerifyModelFile(string path, long expectedBytes, string expectedSha256)
        {
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.Length != expectedBytes) return false;

            try
            {
                using var stream = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] hash = sha.ComputeHash(stream);
                string hex = Convert.ToHexString(hash);
                return string.Equals(hex, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static async Task Install3DAsync(IProgress<(double Progress, string Status)>? progress = null, CancellationToken ct = default)
        {
            string targetDir = Extension3DDir;
            string modelsDir = Path.Combine(targetDir, "Models");
            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(modelsDir);

            progress?.Report((0.05, "Locating extension binaries..."));

            // 1. Install DLLs - verify bundle exists and contains all required DLLs
            string localBundle = Path.Combine(AppContext.BaseDirectory, "Extensions", "Parallax", "bundle");
            if (!Directory.Exists(localBundle))
            {
                // Also check project structure relative to BaseDirectory
                localBundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "extensions", "Parallax", "bundle"));
            }
            if (!Directory.Exists(localBundle))
            {
                localBundle = Path.Combine(AppContext.BaseDirectory, "Extensions", "3D");
            }

            string[] requiredDlls = new[]
            {
                "ModernImageViewer.Extensions.Parallax.dll",
                "Microsoft.ML.OnnxRuntime.dll",
                "onnxruntime.dll",
                "DirectML.dll"
            };

            foreach (var dll in requiredDlls)
            {
                string src = Path.Combine(localBundle, dll);
                if (!File.Exists(src))
                {
                    throw new FileNotFoundException($"3D extension binary '{dll}' was not found in '{localBundle}'. Please ensure the extension bundle is published.");
                }
            }

            foreach (var dll in requiredDlls)
            {
                string dest = Path.Combine(targetDir, dll);
                string src = Path.Combine(localBundle, dll);
                File.Copy(src, dest, overwrite: true);
            }

            progress?.Report((0.20, "Preparing AI depth models..."));

            // 2. Install Models
            // Check local staging models first
            string localModels = Path.Combine(AppContext.BaseDirectory, "Models");
            if (!Directory.Exists(localModels))
            {
                localModels = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bin", "Models"));
            }

            // Depth Anything V2 Small
            string localDepth = Path.Combine(localModels, "depth-anything-v2-small.onnx");
            if (File.Exists(localDepth) && new FileInfo(localDepth).Length == DepthModelExpectedBytes)
            {
                progress?.Report((0.35, "Copying depth estimation model..."));
                await CopyFileAsync(localDepth, DepthModelPath, ct).ConfigureAwait(false);
            }
            else if (!File.Exists(DepthModelPath) || new FileInfo(DepthModelPath).Length != DepthModelExpectedBytes)
            {
                progress?.Report((0.25, "Downloading depth model (99 MB)..."));
                await DownloadFileWithProgressAsync(DepthAnythingUrl, DepthModelPath, 0.25, 0.55, progress, ct).ConfigureAwait(false);
            }

            progress?.Report((0.55, "Verifying depth model integrity..."));
            if (!VerifyModelFile(DepthModelPath, DepthModelExpectedBytes, DepthModelSha256))
            {
                try { File.Delete(DepthModelPath); } catch { }
                throw new InvalidOperationException("Depth estimation model verification failed (SHA-256 or size mismatch).");
            }

            // BiRefNet Lite
            string localMask = Path.Combine(localModels, "birefnet-lite.onnx");
            if (File.Exists(localMask) && new FileInfo(localMask).Length == MaskModelExpectedBytes)
            {
                progress?.Report((0.70, "Copying subject segmentation model..."));
                await CopyFileAsync(localMask, MaskModelPath, ct).ConfigureAwait(false);
            }
            else if (!File.Exists(MaskModelPath) || new FileInfo(MaskModelPath).Length != MaskModelExpectedBytes)
            {
                progress?.Report((0.60, "Downloading subject mask model (115 MB)..."));
                await DownloadFileWithProgressAsync(BiRefNetUrl, MaskModelPath, 0.60, 0.90, progress, ct).ConfigureAwait(false);
            }

            progress?.Report((0.92, "Verifying subject mask model integrity..."));
            if (!VerifyModelFile(MaskModelPath, MaskModelExpectedBytes, MaskModelSha256))
            {
                try { File.Delete(MaskModelPath); } catch { }
                throw new InvalidOperationException("Subject segmentation model verification failed (SHA-256 or size mismatch).");
            }

            if (!Is3DInstalled)
            {
                throw new InvalidOperationException("3D Parallax installation could not be verified.");
            }

            progress?.Report((0.98, "Initializing 3D Parallax engine..."));
            ParallaxHost.ReloadExtension();

            progress?.Report((1.0, "Installed"));
        }

        public static bool Uninstall3D()
        {
            try
            {
                ParallaxHost.UnloadExtension();
                if (!Directory.Exists(Extension3DDir))
                {
                    ParallaxHost.ReloadExtension();
                    return true;
                }

                // Delete models and files with retries (TypoZen pattern)
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(Extension3DDir, "*", SearchOption.AllDirectories))
                        {
                            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                            try { File.Delete(f); }
                            catch
                            {
                                // If locked by OS native module mapping (DirectML/onnxruntime), rename to .pending-delete
                                try
                                {
                                    string pending = f + ".pending-delete";
                                    if (File.Exists(pending)) File.Delete(pending);
                                    File.Move(f, pending);
                                }
                                catch { }
                            }
                        }

                        foreach (string d in Directory.GetDirectories(Extension3DDir))
                        {
                            try { Directory.Delete(d, recursive: true); } catch { }
                        }

                        try { Directory.Delete(Extension3DDir, recursive: false); } catch { }
                    }
                    catch { }

                    if (!Directory.Exists(Extension3DDir) || Directory.GetFileSystemEntries(Extension3DDir).Length == 0)
                        break;

                    Thread.Sleep(100);
                }

                ParallaxHost.ReloadExtension();
                return true;
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"Uninstall3D failed: {ex.Message}");
                return false;
            }
        }

        private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
        {
            using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await sourceStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
        }

        private static async Task DownloadFileWithProgressAsync(
            string url, string destination, double progressStart, double progressEnd,
            IProgress<(double Progress, string Status)>? progress, CancellationToken ct)
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            string tempFile = destination + ".tmp";

            using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var dest = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    totalRead += read;
                    if (totalBytes > 0 && progress != null)
                    {
                        double fileProgress = (double)totalRead / totalBytes;
                        double overall = progressStart + (fileProgress * (progressEnd - progressStart));
                        string mb = $"{totalRead / 1048576.0:0.#} / {totalBytes / 1048576.0:0.#} MB";
                        progress.Report((overall, $"Downloading {Path.GetFileName(destination)} ({mb})..."));
                    }
                }
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempFile, destination);
        }
    }
}

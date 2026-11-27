using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    internal static partial class ExtensionManager
    {
        // Real-ESRGAN x4plus (BSD-3-Clause), as published in ONNX form by Qualcomm AI Hub (qualcomm/Real-ESRGAN-x4plus).
        public const string UpscaleModelZipUrl = "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_x4plus/releases/v0.67.0/real_esrgan_x4plus-onnx-float.zip";
        public const long UpscaleZipExpectedBytes = 62171800L;
        public const string UpscaleZipSha256 = "2AC78E1073A67A7577116AC5C44AF78F6CCE9642EFCA3C8E62675D68255FA91C";

        private const string UpscaleZipFolder = "real_esrgan_x4plus-onnx-float/real_esrgan_x4plus_onnx/";
        public const string UpscaleModelFile = "real_esrgan_x4plus.onnx";
        public const long UpscaleModelExpectedBytes = 3060406L;
        public const string UpscaleModelSha256 = "31188DBF6464F7B82A38DF0359DF25AD0C3A55E5D075D1D9CBFB61EE635BFD5D";
        // The model's weights; the .onnx names this file, so it keeps this name beside it.
        public const string UpscaleDataFile = "real_esrgan_x4plus.data";
        public const long UpscaleDataExpectedBytes = 66737664L;
        public const string UpscaleDataSha256 = "28FADA125730D3C87D504D48DD8332837F65811EC431A570EA4919BA3EEE3287";

        public static string ExtensionUpscaleDir =>
            Path.Combine(AppPaths.LocalDirectory, "Extensions", "Upscale");

        private static string UpscaleModelsDir => Path.Combine(ExtensionUpscaleDir, "Models");
        public static string UpscaleModelPath => Path.Combine(UpscaleModelsDir, UpscaleModelFile);
        public static string UpscaleDataPath => Path.Combine(UpscaleModelsDir, UpscaleDataFile);

        public static bool IsUpscaleInstalled =>
            File.Exists(Path.Combine(ExtensionUpscaleDir, UpscaleHost.DllName))
            && File.Exists(UpscaleModelPath) && File.Exists(UpscaleDataPath);

        public static long GetUpscaleDiskSizeBytes() => DiskSizeBytes(ExtensionUpscaleDir);

        public static string GetUpscaleDiskSizeFormatted() => FormatSize(GetUpscaleDiskSizeBytes());

        public static async Task InstallUpscaleAsync(IProgress<(double Progress, string Status)>? progress = null, CancellationToken ct = default)
        {
            string targetDir = ExtensionUpscaleDir;
            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(UpscaleModelsDir);

            progress?.Report((0.05, "Locating extension binaries..."));

            string localBundle = Path.Combine(AppContext.BaseDirectory, "Extensions", "Upscale", "bundle");
            if (!Directory.Exists(localBundle))
                localBundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "extensions", "Upscale", "bundle"));

            string[] requiredDlls =
            {
                UpscaleHost.DllName,
                "Microsoft.ML.OnnxRuntime.dll",
                "onnxruntime.dll",
                "DirectML.dll"
            };
            foreach (var dll in requiredDlls)
            {
                if (!File.Exists(Path.Combine(localBundle, dll)))
                    throw new FileNotFoundException($"Upscale extension binary '{dll}' was not found in '{localBundle}'. Please ensure the extension bundle is published.");
            }
            foreach (var dll in requiredDlls)
                File.Copy(Path.Combine(localBundle, dll), Path.Combine(targetDir, dll), overwrite: true);

            bool haveModel = VerifyModelFile(UpscaleModelPath, UpscaleModelExpectedBytes, UpscaleModelSha256)
                && VerifyModelFile(UpscaleDataPath, UpscaleDataExpectedBytes, UpscaleDataSha256);
            if (!haveModel)
            {
                string zipPath = Path.Combine(UpscaleModelsDir, "real_esrgan_x4plus-onnx-float.zip");
                string localZip = Path.Combine(AppContext.BaseDirectory, "Models", "real_esrgan_x4plus-onnx-float.zip");
                try
                {
                    if (File.Exists(localZip) && new FileInfo(localZip).Length == UpscaleZipExpectedBytes)
                    {
                        progress?.Report((0.30, "Copying upscale model..."));
                        await CopyFileAsync(localZip, zipPath, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        progress?.Report((0.15, "Downloading upscale model (62 MB)..."));
                        await DownloadFileWithProgressAsync(UpscaleModelZipUrl, zipPath, 0.15, 0.85, progress, ct).ConfigureAwait(false);
                    }

                    progress?.Report((0.87, "Verifying upscale model..."));
                    if (!VerifyModelFile(zipPath, UpscaleZipExpectedBytes, UpscaleZipSha256))
                        throw new InvalidOperationException("Upscale model download verification failed (SHA-256 or size mismatch).");

                    progress?.Report((0.90, "Unpacking upscale model..."));
                    using (var zip = ZipFile.OpenRead(zipPath))
                    {
                        foreach (var (name, dest) in new[] { (UpscaleModelFile, UpscaleModelPath), (UpscaleDataFile, UpscaleDataPath) })
                        {
                            var entry = zip.GetEntry(UpscaleZipFolder + name)
                                ?? throw new InvalidOperationException($"Upscale model archive has no {name}.");
                            entry.ExtractToFile(dest, overwrite: true);
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                }

                if (!VerifyModelFile(UpscaleModelPath, UpscaleModelExpectedBytes, UpscaleModelSha256)
                    || !VerifyModelFile(UpscaleDataPath, UpscaleDataExpectedBytes, UpscaleDataSha256))
                {
                    try { File.Delete(UpscaleModelPath); } catch { }
                    try { File.Delete(UpscaleDataPath); } catch { }
                    throw new InvalidOperationException("Upscale model verification failed (SHA-256 or size mismatch).");
                }
            }

            if (!IsUpscaleInstalled)
                throw new InvalidOperationException("Upscale installation could not be verified.");

            progress?.Report((0.98, "Initializing upscale engine..."));
            UpscaleHost.ReloadExtension();
            progress?.Report((1.0, "Installed"));
        }

        public static bool UninstallUpscale()
        {
            try
            {
                UpscaleHost.UnloadExtension();
                bool clean = WipeExtensionDir(ExtensionUpscaleDir, "UninstallUpscale");
                UpscaleHost.ReloadExtension();
                return clean;
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"UninstallUpscale failed: {ex.Message}");
                return false;
            }
        }
    }
}

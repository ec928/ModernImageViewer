using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    /// <summary>Loads the compiled forward-warp Sway pixel shader and decodes crisp-outline reference pixels on MTA threads.</summary>
    internal static class SwayShader
    {
        private static readonly object Sync = new();
        private static byte[]? _splatBytes;
        private static bool _splatMissing;
        private static byte[]? _splat2DBytes;
        private static bool _splat2DMissing;
        private static byte[]? _depthLensBytes;
        private static bool _depthLensMissing;

        private static byte[]? TryLoadShaderBytes(string resourceName, ref byte[]? cache, ref bool missingFlag)
        {
            lock (Sync)
            {
                if (missingFlag) return null;
                if (cache == null)
                {
                    using var stream = typeof(SwayShader).Assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        missingFlag = true;
                        PlayTiming.Write($"3D: shader missing ({resourceName})");
                        return null;
                    }
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    cache = copy.ToArray();
                }
                return cache;
            }
        }

        private static PixelShaderEffect? InstantiateTwoInputSplat(string resourceName, ref byte[]? cache, ref bool missingFlag, ICanvasImage photo, ICanvasImage depthAtSize, float maxOffset)
        {
            byte[]? bytes = TryLoadShaderBytes(resourceName, ref cache, ref missingFlag);
            if (bytes == null) return null;

            try
            {
                return new PixelShaderEffect(bytes)
                {
                    Source1 = photo,
                    Source2 = depthAtSize,
                    Source1Mapping = SamplerCoordinateMapping.Offset,
                    Source2Mapping = SamplerCoordinateMapping.Offset,
                    MaxSamplerOffset = (int)Math.Ceiling(maxOffset)
                };
            }
            catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException or IOException)
            {
                lock (Sync)
                {
                    if (resourceName == "ModernImageViewer.Shaders.Sway2DSplat.bin") _splat2DMissing = true;
                    else _splatMissing = true;
                }
                PlayTiming.Write($"3D: shader not made ({resourceName}): {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        public static PixelShaderEffect? CreateSplat(ICanvasImage photo, ICanvasImage depthAtSize, float maxOffset) =>
            InstantiateTwoInputSplat("ModernImageViewer.Shaders.SwaySplat.bin", ref _splatBytes, ref _splatMissing, photo, depthAtSize, maxOffset);

        public static PixelShaderEffect? CreateSplat2D(ICanvasImage photo, ICanvasImage depthAtSize, float maxOffset) =>
            InstantiateTwoInputSplat("ModernImageViewer.Shaders.Sway2DSplat.bin", ref _splat2DBytes, ref _splat2DMissing, photo, depthAtSize, maxOffset);

        public static PixelShaderEffect? CreateDepthLens(ICanvasImage photo, ICanvasImage depthAtSize, ICanvasImage maskAtSize, int maxSamplerOffset = 40)
        {
            byte[]? bytes = TryLoadShaderBytes("ModernImageViewer.Shaders.DepthLens.bin", ref _depthLensBytes, ref _depthLensMissing);
            if (bytes == null) return null;

            try
            {
                return new PixelShaderEffect(bytes)
                {
                    Source1 = photo,
                    Source2 = depthAtSize,
                    Source3 = maskAtSize,
                    Source1Mapping = SamplerCoordinateMapping.Offset,
                    Source2Mapping = SamplerCoordinateMapping.Offset,
                    Source3Mapping = SamplerCoordinateMapping.Offset,
                    MaxSamplerOffset = maxSamplerOffset
                };
            }
            catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException or IOException)
            {
                lock (Sync) _depthLensMissing = true;
                PlayTiming.Write($"3D: depth lens shader not made: {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        public static async Task<(byte[] Pixels, int Width, int Height)?> LoadCrispReferencePixelsAsync(
            string path, int targetWidth, int targetHeight, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path) || targetWidth <= 0 || targetHeight <= 0 || ct.IsCancellationRequested)
                return null;

            return await Task.Run(async () =>
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    using IRandomAccessStream ras = fs.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
                    bool turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = (uint)(turned ? targetHeight : targetWidth),
                        ScaledHeight = (uint)(turned ? targetWidth : targetHeight),
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };
                    var data = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
                    return ((byte[] Pixels, int Width, int Height)?)(data.DetachPixelData(), targetWidth, targetHeight);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
                {
                    return null;
                }
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Loads, saves, and broadcasts shared Sway depth/swing preferences across Multi-View and single-image viewers.</summary>
    internal static class SwayPreferences
    {
        private static readonly object Sync = new();
        private static string OptionsPath => Path.Combine(AppPaths.LocalDirectory, "surprise-options.json");

        public static event Action<double, double>? Changed;

        public static (double Depth, double Swing) Load()
        {
            lock (Sync)
            {
                try
                {
                    string path = OptionsPath;
                    if (File.Exists(path))
                    {
                        var options = SurpriseOptions.FromJson(File.ReadAllText(path));
                        return (SurpriseOptions.ClampedSwayRotation(options.SwayRotation), SurpriseOptions.ClampedSwaySwing(options.SwaySwing));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return (SurpriseOptions.SwayRotationDefault, SurpriseOptions.SwaySwingDefault);
        }

        public static void Save(double depth, double swing)
        {
            double clampedDepth = SurpriseOptions.ClampedSwayRotation(depth);
            double clampedSwing = SurpriseOptions.ClampedSwaySwing(swing);
            lock (Sync)
            {
                SurpriseOptions options;
                try
                {
                    string path = OptionsPath;
                    options = File.Exists(path) ? SurpriseOptions.FromJson(File.ReadAllText(path)) : new SurpriseOptions();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    options = new SurpriseOptions();
                }

                options.SwayRotation = clampedDepth;
                options.SwaySwing = clampedSwing;
                try
                {
                    Directory.CreateDirectory(AppPaths.LocalDirectory);
                    AtomicFile.WriteAllText(OptionsPath, options.ToJson());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            Changed?.Invoke(clampedDepth, clampedSwing);
        }

        public static void NotifyChanged(double depth, double swing) =>
            Changed?.Invoke(SurpriseOptions.ClampedSwayRotation(depth), SurpriseOptions.ClampedSwaySwing(swing));
    }
}

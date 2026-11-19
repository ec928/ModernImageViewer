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
        private static byte[]? _bytes;
        private static bool _missing;

        public static PixelShaderEffect? CreateSplat(ICanvasImage photo, ICanvasImage depthAtSize, float maxOffset)
        {
            byte[]? bytes;
            lock (Sync)
            {
                if (_missing) return null;
                if (_bytes == null)
                {
                    using var stream = typeof(SwayShader).Assembly.GetManifestResourceStream("ModernImageViewer.Shaders.SwaySplat.bin");
                    if (stream == null)
                    {
                        _missing = true;
                        PlayTiming.Write("3D: sway splat shader missing");
                        return null;
                    }
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    _bytes = copy.ToArray();
                }
                bytes = _bytes;
            }

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
                lock (Sync) _missing = true;
                PlayTiming.Write($"3D: sway splat not made: {ex.GetType().Name} {ex.Message}");
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

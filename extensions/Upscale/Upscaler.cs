using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ModernImageViewer.Contracts;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ModernImageViewer.Extensions.Upscale
{
    /// <summary>
    /// Real-ESRGAN x4plus over the whole photo in overlapping tiles. The model takes a fixed 128 x 128 RGB tile in
    /// [0, 1] and returns it at 4x. Each tile keeps only its middle; the 16 px border on every side is context that is
    /// thrown away, so tile edges do not show. 2x runs the same model and averages each 2 x 2 block of its output.
    /// </summary>
    internal static class Upscaler
    {
        public static Action<string>? Logger { get; set; }
        private static void Log(string msg) => Logger?.Invoke(msg);

        public const string ModelFile = "real_esrgan_x4plus.onnx";
        private const int ModelScale = 4;
        private const int Overlap = 16;

        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static InferenceSession? _session;
        private static bool _unavailable;
        private static string _device = "not loaded";

        public sealed record Upscaled(byte[] Bgra, int Width, int Height) : IUpscaledImage;

        public static string? ModelPath
        {
            get
            {
                string extDir = Path.GetDirectoryName(typeof(Upscaler).Assembly.Location) ?? AppContext.BaseDirectory;
                string path = Path.Combine(extDir, "Models", ModelFile);
                return File.Exists(path) ? path : null;
            }
        }

        public static bool Available => !_unavailable && ModelPath != null;

        public static void DisposeSession()
        {
            _unavailable = true;
            bool acquired = false;
            try { acquired = OneAtATime.Wait(2000); } catch { }
            // A run still inside the session has the queue. Dropping the session under it faults the process.
            if (!acquired) return;
            try
            {
                try { _session?.Dispose(); } catch { }
                _session = null;
            }
            finally
            {
                try { OneAtATime.Release(); } catch { }
            }
        }

        public static async Task<IUpscaledImage?> UpscaleAsync(string path, int scale, IProgress<double>? progress, CancellationToken ct)
        {
            if (scale != 2 && scale != 4) throw new ArgumentOutOfRangeException(nameof(scale));
            if (!Available) return null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_unavailable) return null;
                var session = Session();
                if (session == null) return null;
                ct.ThrowIfCancellationRequested();

                var (src, w, h) = await DecodeAsync(path, ct).ConfigureAwait(false);
                long decoded = clock.ElapsedMilliseconds;
                var result = await Task.Run(() => Run(session, src, w, h, scale, progress, ct), ct).ConfigureAwait(false);
                Log($"Upscale: {Path.GetFileName(path)} {w}x{h} x{scale} -> {result.Width}x{result.Height} {_device} decode {decoded} ms total {clock.ElapsedMilliseconds} ms");
                return result;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OutOfMemoryException)
            {
                Log($"Upscale: failed for {Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
                return null;
            }
            finally
            {
                OneAtATime.Release();
            }
        }

        private static Upscaled Run(InferenceSession session, byte[] src, int w, int h, int scale, IProgress<double>? progress, CancellationToken ct)
        {
            string inputName = session.InputMetadata.Keys.First();
            int[] dims = session.InputMetadata[inputName].Dimensions;
            int t = dims.Length == 4 && dims[2] > 0 ? dims[2] : 128;
            int o = t * ModelScale;
            int stride = t - 2 * Overlap;

            int outW = w * scale, outH = h * scale;
            var dst = new byte[(long)outW * outH * 4];
            bool hasAlpha = false;
            for (int i = 3; i < src.Length; i += 4)
                if (src[i] != 255) { hasAlpha = true; break; }

            var input = new DenseTensor<float>(new[] { 1, 3, t, t });
            var buf = input.Buffer.Span;
            int plane = t * t;
            int tilesX = (w + stride - 1) / stride, tilesY = (h + stride - 1) / stride;
            int total = tilesX * tilesY, done = 0, lastPercent = -1;
            int step = ModelScale / scale; // output pixels of the model per output pixel of the result, each way

            for (int ty = 0; ty < h; ty += stride)
            {
                int oy = Math.Clamp(ty - Overlap, 0, Math.Max(0, h - t));
                for (int tx = 0; tx < w; tx += stride)
                {
                    ct.ThrowIfCancellationRequested();
                    int ox = Math.Clamp(tx - Overlap, 0, Math.Max(0, w - t));

                    // Fill the tile. Past the picture's edge (a picture smaller than a tile) the edge pixel repeats.
                    buf = input.Buffer.Span;
                    for (int y = 0; y < t; y++)
                    {
                        int sy = Math.Min(h - 1, oy + y);
                        for (int x = 0; x < t; x++)
                        {
                            int sx = Math.Min(w - 1, ox + x);
                            int si = (sy * w + sx) * 4;
                            int ti = y * t + x;
                            buf[ti] = src[si + 2] / 255f;
                            buf[plane + ti] = src[si + 1] / 255f;
                            buf[2 * plane + ti] = src[si] / 255f;
                        }
                    }

                    using var results = NativeRuntime.Run(session, new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
                    var outTensor = results.First().AsTensor<float>();
                    ReadOnlySpan<float> model = outTensor is DenseTensor<float> dense ? dense.Buffer.Span : outTensor.ToArray();
                    int oplane = o * o;

                    // Keep the middle of the tile: [tx, tx + stride) x [ty, ty + stride) in picture pixels.
                    int x1 = Math.Min(w, tx + stride), y1 = Math.Min(h, ty + stride);
                    for (int y = ty; y < y1; y++)
                    {
                        int ly = (y - oy) * ModelScale;
                        for (int x = tx; x < x1; x++)
                        {
                            int lx = (x - ox) * ModelScale;
                            for (int dy = 0; dy < scale; dy++)
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    float r = 0, g = 0, b = 0;
                                    for (int my = 0; my < step; my++)
                                        for (int mx = 0; mx < step; mx++)
                                        {
                                            int mi = (ly + dy * step + my) * o + (lx + dx * step + mx);
                                            r += model[mi]; g += model[oplane + mi]; b += model[2 * oplane + mi];
                                        }
                                    float n = step * step;
                                    int px = x * scale + dx, py = y * scale + dy;
                                    float a = hasAlpha ? AlphaAt(src, w, h, (px + 0.5f) / scale - 0.5f, (py + 0.5f) / scale - 0.5f) : 1f;
                                    long di = ((long)py * outW + px) * 4;
                                    dst[di] = To8(b / n * a);
                                    dst[di + 1] = To8(g / n * a);
                                    dst[di + 2] = To8(r / n * a);
                                    dst[di + 3] = To8(a);
                                }
                        }
                    }

                    done++;
                    int percent = done * 100 / total;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(done / (double)total);
                    }
                }
            }
            return new Upscaled(dst, outW, outH);
        }

        /// <summary>The source alpha at a fractional position, bilinear, in [0, 1].</summary>
        private static float AlphaAt(byte[] src, int w, int h, float fx, float fy)
        {
            fx = Math.Clamp(fx, 0, w - 1); fy = Math.Clamp(fy, 0, h - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float ax = fx - x0, ay = fy - y0;
            float a00 = src[(y0 * w + x0) * 4 + 3], a10 = src[(y0 * w + x1) * 4 + 3];
            float a01 = src[(y1 * w + x0) * 4 + 3], a11 = src[(y1 * w + x1) * 4 + 3];
            float top = a00 + (a10 - a00) * ax, bottom = a01 + (a11 - a01) * ax;
            return (top + (bottom - top) * ay) / 255f;
        }

        private static byte To8(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

        private static InferenceSession? Session()
        {
            if (_session != null || _unavailable) return _session;
            NativeRuntime.Ensure();
            string? model = ModelPath;
            if (model == null) { _unavailable = true; return null; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var options = NativeRuntime.GpuSession();
                // No processor fallback: at about a second a tile, a photo would take most of an hour.
                if (!NativeRuntime.TryUseDirectMl(options, out string? whyNot))
                {
                    Log($"Upscale: DirectML did not attach: {whyNot}");
                    return null;
                }
                _device = "DirectML";
                _session = NativeRuntime.CreateSession(model, options);
                Log($"Upscale: session {_device} {clock.ElapsedMilliseconds} ms [{NativeRuntime.Which}]");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or IOException or BadImageFormatException)
            {
                Log($"Upscale: model did not load: {ex.GetType().Name} {ex.Message}");
            }
            return _session;
        }

        /// <summary>The whole photo, oriented and colour-managed to sRGB, straight-alpha BGRA8.</summary>
        private static async Task<(byte[] Pixels, int Width, int Height)> DecodeAsync(string path, CancellationToken ct)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using IRandomAccessStream stream = fs.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
            return (data.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
    }

    public sealed class UpscaleService : IUpscaleService
    {
        static UpscaleService()
        {
            // Before any session type is touched, so the import binds the extension runtime.
            NativeRuntime.Ensure();
        }

        public static Action<string>? Logger
        {
            get => Upscaler.Logger;
            set => Upscaler.Logger = value;
        }

        public bool IsAvailable => Upscaler.Available;

        public Task<IUpscaledImage?> UpscaleAsync(string photoPath, int scale, IProgress<double>? progress, CancellationToken ct) =>
            Upscaler.UpscaleAsync(photoPath, scale, progress, ct);

        public void DisposeSession() => Upscaler.DisposeSession();
    }
}

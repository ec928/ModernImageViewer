using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    /// <summary>
    /// Prototype (branch prototype-titles-3d): how near each part of a photo is, from Depth Anything V2 Small (Apache-2.0)
    /// run on the graphics card through the ONNX Runtime the Windows App SDK ships. A map is a small grey picture, 1 the
    /// nearest; the near things are grown a little, so in 3D an edge stretches what is behind it and not a face.
    /// </summary>
    internal static class DepthMaps
    {
        /// <param name="Raw">The depth as the model gave it, scaled 0 to 255 but not grown or blurred: where the parallax judge
        /// measures the step at a subject's edge.</param>
        /// <param name="Light">The photo's brightness at the map's size: where the judge measures the background's texture.</param>
        public sealed record Map(byte[] Grey, int Width, int Height, float Middle, float RawSpan, byte[]? Raw = null, byte[]? Light = null);

        private static readonly ConcurrentDictionary<string, ParallaxFit.Verdict> Verdicts = new(StringComparer.OrdinalIgnoreCase);

        private const string ModelFile = "depth-anything-v2-small.onnx";
        private const int LongSide = 518;
        private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
        private static readonly float[] Spread = { 0.229f, 0.224f, 0.225f };
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static readonly ConcurrentDictionary<string, Task<Map?>> Made = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> Order = new();
        private const int Kept = 6;
        private static InferenceSession? _session;
        private static bool _unavailable;

        /// <summary>Where the model is looked for: beside the app, then in the app's data folder.</summary>
        public static string? ModelPath =>
            new[] { Path.Combine(AppContext.BaseDirectory, "Models", ModelFile), Path.Combine(AppPaths.LocalDirectory, "Models", ModelFile) }
                .FirstOrDefault(File.Exists);

        public static bool Available => !_unavailable && ModelPath != null;

        public static void DisposeSession()
        {
            _unavailable = true;
            bool acquired = false;
            try
            {
                acquired = OneAtATime.Wait(2000);
            }
            catch { }

            if (acquired)
            {
                try { _session?.Dispose(); } catch { }
                _session = null;
                try { OneAtATime.Release(); } catch { }
            }
            else
            {
                _session = null;
            }
        }

        /// <summary>
        /// Pass or exclude for a Parallax show, by <see cref="ParallaxJudge"/>: a clear subject with a real depth step at its
        /// own edge passes (it plays Slide or Rock); anything else is left out. Unreadable is null. The depth-only judgement
        /// (ParallaxFit) passed floors, landscapes, people against a near wall and buildings against a plain sky.
        /// </summary>
        public static ParallaxFit.Verdict? CachedVerdict(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (PhotoFactsReader.TryGetCached(path, out var facts) && facts?.ScreenLike == true)
                return ParallaxFit.Verdict.Exclude;
            if (ParallaxJudge.TryGetCached(path, out var judged) && judged != null)
                return judged.Fit == ParallaxJudge.Fit.Out ? ParallaxFit.Verdict.Exclude : ParallaxFit.Verdict.Pass;
            if (Verdicts.TryGetValue(path, out var verdict))
                return verdict;
            return null;
        }

        public static async Task<ParallaxFit.Verdict?> VerdictAsync(string path)
        {
            if (!Available || string.IsNullOrEmpty(path)) return null;
            if (PhotoFactsReader.TryGetCached(path, out var facts) && facts?.ScreenLike == true)
                return ParallaxFit.Verdict.Exclude;
            var judged = await ParallaxJudge.ForAsync(path).ConfigureAwait(false);
            if (judged == null) return null;
            return judged.Fit == ParallaxJudge.Fit.Out ? ParallaxFit.Verdict.Exclude : ParallaxFit.Verdict.Pass;
        }

        private static float[] ToNear(Map map)
        {
            var near = new float[map.Grey.Length];
            for (int i = 0; i < near.Length; i++) near[i] = map.Grey[i] / 255f;
            return near;
        }

        /// <summary>The map for a photo, made once and kept for the last few.</summary>
        public static Task<Map?> ForAsync(string path)
        {
            if (!Available) return Task.FromResult<Map?>(null);
            var task = Made.GetOrAdd(path, p => Task.Run(() => MakeAsync(p)));
            lock (Order)
            {
                if (!Order.Contains(path)) Order.Enqueue(path);
                while (Order.Count > Kept) Made.TryRemove(Order.Dequeue(), out _);
            }
            return task;
        }

        private static async Task<Map?> MakeAsync(string path)
        {
            await OneAtATime.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_unavailable) return null;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var session = Session();
                if (session == null) return null;
                var (pixels, width, height) = await DecodeAsync(path).ConfigureAwait(false);
                if (LooksLikeScreen(path, pixels, width, height))
                    return new Map(Array.Empty<byte>(), width, height, 0.5f, 0f, null, null);
                var input = new DenseTensor<float>(new[] { 1, 3, height, width });
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        // BGRA in, RGB planes out.
                        input[0, 0, y, x] = (pixels[i + 2] / 255f - Mean[0]) / Spread[0];
                        input[0, 1, y, x] = (pixels[i + 1] / 255f - Mean[1]) / Spread[1];
                        input[0, 2, y, x] = (pixels[i] / 255f - Mean[2]) / Spread[2];
                    }
                string name = session.InputMetadata.Keys.First();
                using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, input) });
                var output = results.First().AsTensor<float>().ToArray();
                if (output.Length != width * height) return null;
                float least = output.Min(), most = output.Max();
                float range = Math.Max(1e-6f, most - least);
                var near = new float[output.Length];
                for (int i = 0; i < near.Length; i++) near[i] = (output[i] - least) / range;
                var raw = new byte[near.Length];
                var light = new byte[near.Length];
                for (int i = 0; i < near.Length; i++)
                {
                    raw[i] = (byte)Math.Clamp(near[i] * 255f + 0.5f, 0, 255);
                    light[i] = (byte)Math.Clamp(0.114f * pixels[i * 4] + 0.587f * pixels[i * 4 + 1] + 0.299f * pixels[i * 4 + 2] + 0.5f, 0, 255);
                }
                near = Blur(Grow(Grow(near, width, height), width, height), width, height);
                float span = most - least;
                Verdicts[path] = ParallaxFit.Judge(near, width, height, span);
                // The page shows this map at 820px. On a full-size photo the same map is coarse enough to crawl.
                var shown = Blur(near, width, height, 3);
                var grey = new byte[shown.Length];
                for (int i = 0; i < grey.Length; i++) grey[i] = (byte)Math.Clamp(shown[i] * 255f + 0.5f, 0, 255);
                var sorted = (float[])shown.Clone();
                Array.Sort(sorted);
                PlayTiming.Write($"3D: depth for {Path.GetFileName(path)} {width}x{height} in {clock.ElapsedMilliseconds} ms, {Verdicts[path]}");
                return new Map(grey, width, height, sorted[sorted.Length / 2], span, raw, light);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                PlayTiming.Write($"3D: no depth for {Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
                return null;
            }
            finally
            {
                OneAtATime.Release();
            }
        }

        private static InferenceSession? Session()
        {
            if (_session != null || _unavailable) return _session;
            string? model = ModelPath;
            if (model == null) { _unavailable = true; return null; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var options = new SessionOptions();
                try
                {
                    options.AppendExecutionProvider_DML(0);
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
                {
                    // No DirectML: the processor does it, slower.
                    PlayTiming.Write($"3D: no DirectML ({ex.Message}), on the processor");
                }
                _session = new InferenceSession(model, options);
                PlayTiming.Write($"3D: model loaded in {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or IOException or BadImageFormatException)
            {
                _unavailable = true;
                PlayTiming.Write($"3D: model did not load: {ex.GetType().Name} {ex.Message}");
            }
            return _session;
        }

        /// <summary>The photo turned as it is shown, at the model's size: long side 518, both sides multiples of 14.</summary>
        private static async Task<(byte[] Pixels, int Width, int Height)> DecodeAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            bool turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
            double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
            double scale = LongSide / Math.Max(w, h);
            int width = Math.Max(14, (int)Math.Round(w * scale / 14) * 14);
            int height = Math.Max(14, (int)Math.Round(h * scale / 14) * 14);
            var transform = new BitmapTransform
            {
                // The transform's size is before the turn.
                ScaledWidth = (uint)(turned ? height : width),
                ScaledHeight = (uint)(turned ? width : height),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return (data.DetachPixelData(), width, height);
        }

        private static bool LooksLikeScreen(string path, byte[] pixels, int w, int h)
        {
            string name = Path.GetFileName(path);
            if (name.Contains("screenshot", StringComparison.OrdinalIgnoreCase) || name.Contains("screen shot", StringComparison.OrdinalIgnoreCase))
                return true;
            if (w < 16 || h < 16) return false;
            int flatX = 0, totalX = (w - 1) * h;
            int flatY = 0, totalY = w * (h - 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = (row + x) * 4;
                    int g = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
                    if (x + 1 < w)
                    {
                        int j = (row + x + 1) * 4;
                        int gx = (pixels[j] * 114 + pixels[j + 1] * 587 + pixels[j + 2] * 299) / 1000;
                        if (g == gx) flatX++;
                    }
                    if (y + 1 < h)
                    {
                        int j = (row + w + x) * 4;
                        int gy = (pixels[j] * 114 + pixels[j + 1] * 587 + pixels[j + 2] * 299) / 1000;
                        if (g == gy) flatY++;
                    }
                }
            }
            double flat = 0.5 * (flatX / (double)Math.Max(1, totalX) + flatY / (double)Math.Max(1, totalY));
            return flat > 0.38;
        }

        private static float[] Grow(float[] a, int w, int h)
        {
            var b = new float[a.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float most = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        for (int dx = -1; dx <= 1; dx++)
                            most = Math.Max(most, a[yy * w + Math.Clamp(x + dx, 0, w - 1)]);
                    }
                    b[y * w + x] = most;
                }
            return b;
        }

        private static float[] Blur(float[] a, int w, int h, int radius = 1)
        {
            var b = new float[a.Length];
            int span = radius * 2 + 1;
            float samples = span * span;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        for (int dx = -radius; dx <= radius; dx++)
                            sum += a[yy * w + Math.Clamp(x + dx, 0, w - 1)];
                    }
                    b[y * w + x] = sum / samples;
                }
            return b;
        }
    }
}

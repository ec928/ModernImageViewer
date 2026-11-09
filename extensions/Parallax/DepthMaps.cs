using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ModernImageViewer.Contracts;
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

namespace ModernImageViewer.Extensions.Parallax
{
    internal static class DepthMaps
    {
        public static Action<string>? Logger { get; set; }
        private static void Log(string msg) => Logger?.Invoke(msg);

        public sealed record Map(byte[] Grey, int Width, int Height, float Middle, float RawSpan, byte[]? Raw = null, byte[]? Light = null) : IParallaxMap
        {
            public float SubjectDepth => Middle;
        }


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

        public static string? ModelPath
        {
            get
            {
                string extDir = Path.GetDirectoryName(typeof(DepthMaps).Assembly.Location) ?? AppContext.BaseDirectory;
                string[] candidates = new[]
                {
                    Path.Combine(extDir, "Models", ModelFile),
                    Path.Combine(extDir, ModelFile),
                    Path.Combine(AppContext.BaseDirectory, "Models", ModelFile),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernImageViewer", "Extensions", "3D", "Models", ModelFile)
                };
                return candidates.FirstOrDefault(File.Exists);
            }
        }

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
                var input = new DenseTensor<float>(new[] { 1, 3, height, width });
                var light = new byte[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        float b = pixels[i] / 255f, g = pixels[i + 1] / 255f, r = pixels[i + 2] / 255f;
                        input[0, 0, y, x] = (r - Mean[0]) / Spread[0];
                        input[0, 1, y, x] = (g - Mean[1]) / Spread[1];
                        input[0, 2, y, x] = (b - Mean[2]) / Spread[2];
                        light[y * width + x] = (byte)Math.Clamp((int)Math.Round(0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i]), 0, 255);
                    }
                var namedInput = NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input);
                using var results = session.Run(new[] { namedInput });
                var output = results.First().AsTensor<float>();

                float least = float.MaxValue, most = float.MinValue;
                foreach (float v in output)
                {
                    if (v < least) least = v;
                    if (v > most) most = v;
                }
                float span = most > least ? most - least : 1f;

                int count = width * height;
                var raw = new float[count];
                var rawBytes = new byte[count];
                int at = 0;
                foreach (float v in output)
                {
                    if (at >= count) break;
                    float n = Math.Clamp((v - least) / span, 0f, 1f);
                    raw[at] = n;
                    rawBytes[at] = (byte)(n * 255);
                    at++;
                }

                var grown = Grow(raw, width, height);
                var near = Blur(grown, width, height);

                var grey = new byte[near.Length];
                for (int i = 0; i < near.Length; i++) grey[i] = (byte)Math.Clamp((int)Math.Round(near[i] * 255), 0, 255);
                var sorted = (float[])near.Clone();
                Array.Sort(sorted);
                Log($"3D: depth for {Path.GetFileName(path)} {width}x{height} in {clock.ElapsedMilliseconds} ms");
                return new Map(grey, width, height, sorted[sorted.Length / 2], span, rawBytes, light);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log($"3D: no depth for {Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
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
                    Log($"3D: no DirectML ({ex.Message}), on the processor");
                }
                _session = new InferenceSession(model, options);
                Log($"3D: model loaded in {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or IOException or BadImageFormatException)
            {
                _unavailable = true;
                Log($"3D: model did not load: {ex.GetType().Name} {ex.Message}");
            }
            return _session;
        }

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
                ScaledWidth = (uint)(turned ? height : width),
                ScaledHeight = (uint)(turned ? width : height),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return (data.DetachPixelData(), width, height);
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

        private static float[] Blur(float[] a, int w, int h)
        {
            var b = new float[a.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        for (int dx = -1; dx <= 1; dx++)
                            sum += a[yy * w + Math.Clamp(x + dx, 0, w - 1)];
                    }
                    b[y * w + x] = sum / 9;
                }
            return b;
        }
    }
}

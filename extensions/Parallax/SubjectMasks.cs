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
    internal static class SubjectMasks
    {
        public static Action<string>? Logger { get; set; }
        private static void Log(string msg) => Logger?.Invoke(msg);

        public sealed record Mask(byte[] Alpha, int Size, bool Clear, string Why, float Area = 0, float Sides = 0, float Vague = 0, float Top = 0, float Bottom = 0) : IParallaxMask;

        private const string ModelFile = "birefnet-lite.onnx";
        private const int Side = 1024;
        private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
        private static readonly float[] Spread = { 0.229f, 0.224f, 0.225f };
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static readonly ConcurrentDictionary<string, Task<Mask?>> Made = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> Order = new();
        private const int Kept = 4;
        private static InferenceSession? _session;
        private static bool _unavailable;
        private static string _device = "not loaded";

        public static string? ModelPath
        {
            get
            {
                string extDir = Path.GetDirectoryName(typeof(SubjectMasks).Assembly.Location) ?? AppContext.BaseDirectory;
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

        public static Task<Mask?> ForAsync(string path)
        {
            if (!Available) return Task.FromResult<Mask?>(null);
            var task = Made.GetOrAdd(path, p => Task.Run(() => MakeAsync(p)));
            lock (Order)
            {
                if (!Order.Contains(path)) Order.Enqueue(path);
                while (Order.Count > Kept) Made.TryRemove(Order.Dequeue(), out _);
            }
            return task;
        }

        private static async Task<Mask?> MakeAsync(string path)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await OneAtATime.WaitAsync().ConfigureAwait(false);
            long waited = clock.ElapsedMilliseconds;
            try
            {
                if (_unavailable) return null;
                var session = Session();
                if (session == null) return null;
                var pixels = await DecodeAsync(path).ConfigureAwait(false);
                long decoded = clock.ElapsedMilliseconds;
                var input = new DenseTensor<float>(new[] { 1, 3, Side, Side });
                for (int y = 0; y < Side; y++)
                    for (int x = 0; x < Side; x++)
                    {
                        int i = (y * Side + x) * 4;
                        input[0, 0, y, x] = (pixels[i + 2] / 255f - Mean[0]) / Spread[0];
                        input[0, 1, y, x] = (pixels[i + 1] / 255f - Mean[1]) / Spread[1];
                        input[0, 2, y, x] = (pixels[i] / 255f - Mean[2]) / Spread[2];
                    }
                long prepared = clock.ElapsedMilliseconds;
                var namedInput = NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input);
                using var results = session.Run(new[] { namedInput });
                long ran = clock.ElapsedMilliseconds;
                var output = results.First().AsTensor<float>();

                var alpha = new byte[Side * Side];
                int at = 0, subject = 0, vague = 0, top = 0, left = 0, right = 0, bottom = 0;
                float half = 0.5f;
                for (int y = 0; y < Side; y++)
                    for (int x = 0; x < Side; x++)
                    {
                        float raw = output[0, 0, y, x];
                        float p = raw <= -80f ? 0f : raw >= 80f ? 1f : 1f / (1f + (float)Math.Exp(-raw));
                        byte b = (byte)Math.Clamp((int)Math.Round(p * 255), 0, 255);
                        alpha[at++] = b;
                        if (p >= half)
                        {
                            subject++;
                            if (y == 0) top++;
                            if (y == Side - 1) bottom++;
                            if (x == 0) left++;
                            if (x == Side - 1) right++;
                        }
                        if (p >= 0.2f && p <= 0.8f) vague++;
                    }

                int total = Side * Side;
                float area = subject / (float)total;
                float edgeTouching = Math.Max(top, Math.Max(left, right)) / (float)Side;
                float vagueness = subject == 0 ? 1f : vague / (float)subject;
                float topShare = top / (float)Side;
                float bottomShare = bottom / (float)Side;

                bool bigEnough = area >= 0.04f && area <= 0.65f;
                bool withinEdges = edgeTouching < 0.70f;
                bool sharpEnough = vagueness <= 0.35f;
                bool clear = bigEnough && withinEdges && sharpEnough;
                string why = !bigEnough ? (area < 0.04f ? "too small" : "covers the whole photo")
                    : !withinEdges ? "touches too much of an edge"
                    : !sharpEnough ? "outline is unsure" : "clear subject";

                long done = clock.ElapsedMilliseconds;
                Log($"3D: subject {Path.GetFileName(path)} {_device} {why} wait {waited} decode {decoded - waited} prepare {prepared - decoded} run {ran - prepared} after {done - ran} total {done}");
                return new Mask(alpha, Side, clear, why, area, edgeTouching, vagueness, topShare, bottomShare);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Log($"3D: no subject for {Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
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
            NativeRuntime.Ensure();
            string? model = ModelPath;
            if (model == null) { _unavailable = true; return null; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var options = NativeRuntime.GpuSession();
                string? whyNot = null;
                try
                {
                    options.AppendExecutionProvider_DML(0);
                    _device = "DirectML";
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
                {
                    _device = "processor";
                    whyNot = ex.GetType().Name + " " + ex.Message;
                }
                _session = new InferenceSession(model, options);
                Log(whyNot == null
                    ? $"3D: subject session {_device} {clock.ElapsedMilliseconds} ms [{NativeRuntime.Which}]"
                    : $"3D: subject session {_device} {clock.ElapsedMilliseconds} ms ({whyNot}) [{NativeRuntime.Which}]");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or IOException or BadImageFormatException)
            {
                _unavailable = true;
                Log($"3D: subject model did not load: {ex.GetType().Name} {ex.Message}");
            }
            return _session;
        }

        private static async Task<byte[]> DecodeAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            bool turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)(turned ? Side : Side),
                ScaledHeight = (uint)(turned ? Side : Side),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return data.DetachPixelData();
        }
    }
}

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
    /// Prototype (branch prototype-titles-3d): the photo's subject, cut out whole by BiRefNet lite (MIT), for Slide. A
    /// split by depth cut a statue in two, its head with the sky and its chest with the hand, and the fill showed through
    /// its chin as the near part moved. A subject model keeps the object whole. Its answer is also whether the photo
    /// has a clear subject at all: Slide plays only on one that does.
    /// </summary>
    internal static class SubjectMasks
    {
        /// <summary>The subject, 0 to 255, at the model's 1024 x 1024 (stretched over the photo), and whether it is clear.</summary>
        /// <param name="Clear">A subject big enough to see, with a sure outline. Whether it suits Slide or Rock is ParallaxJudge's call.</param>
        /// <param name="Area">The subject's share of the picture.</param>
        /// <param name="Sides">How much of the top, left and right edges it covers.</param>
        /// <param name="Vague">How much of the outline is unsure, against the subject's size: lower is a sharper cut.</param>
        public sealed record Mask(byte[] Alpha, int Size, bool Clear, string Why, float Area = 0, float Sides = 0, float Vague = 0, float Top = 0, float Bottom = 0);

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

        /// <summary>The mask for a photo, made once and kept for the last few. The depth map says whether the subject is in front.</summary>
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
            await OneAtATime.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_unavailable) return null;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var session = Session();
                if (session == null) return null;
                var pixels = await DecodeAsync(path).ConfigureAwait(false);
                var input = new DenseTensor<float>(new[] { 1, 3, Side, Side });
                for (int y = 0; y < Side; y++)
                    for (int x = 0; x < Side; x++)
                    {
                        int i = (y * Side + x) * 4;
                        input[0, 0, y, x] = (pixels[i + 2] / 255f - Mean[0]) / Spread[0];
                        input[0, 1, y, x] = (pixels[i + 1] / 255f - Mean[1]) / Spread[1];
                        input[0, 2, y, x] = (pixels[i] / 255f - Mean[2]) / Spread[2];
                    }
                string name = session.InputMetadata.Keys.First();
                using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, input) });
                // The last output is the finished mask, as logits.
                var logits = results.Last().AsTensor<float>().ToArray();
                if (logits.Length != Side * Side) return null;
                var alpha = new byte[logits.Length];
                int subject = 0, unsure = 0;
                for (int i = 0; i < logits.Length; i++)
                {
                    float m = 1f / (1f + MathF.Exp(-logits[i]));
                    alpha[i] = (byte)Math.Clamp(m * 255f + 0.5f, 0, 255);
                    if (m > 0.5f) subject++;
                    if (m > 0.1f && m < 0.9f) unsure++;
                }
                float area = subject / (float)logits.Length;
                float vague = unsure / Math.Max(1f, subject);
                // How much of the top, left and right edges the subject covers: one that spans them is ground or skyline.
                float top = 0, bottom = 0, left = 0, right = 0;
                for (int k = 0; k < Side; k++)
                {
                    if (alpha[k] > 127) top++;
                    if (alpha[(Side - 1) * Side + k] > 127) bottom++;
                    if (alpha[k * Side] > 127) left++;
                    if (alpha[k * Side + Side - 1] > 127) right++;
                }
                float sides = (top + left + right) / (3f * Side);
                string why = area < 0.04f ? "subject too small" : vague > 0.35f ? "outline unsure" : "";
                bool clear = why.Length == 0;
                PlayTiming.Write($"3D: subject for {Path.GetFileName(path)} in {clock.ElapsedMilliseconds} ms, {area:P0} of the picture, {(clear ? "clear" : why)}");
                return new Mask(alpha, Side, clear, clear ? "clear" : why, area, sides, vague, top / Side, bottom / Side);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                PlayTiming.Write($"3D: no subject for {Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
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
                    PlayTiming.Write($"3D: subject model without DirectML ({ex.Message})");
                }
                _session = new InferenceSession(model, options);
                PlayTiming.Write($"3D: subject model loaded in {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or IOException or BadImageFormatException)
            {
                _unavailable = true;
                PlayTiming.Write($"3D: subject model did not load: {ex.GetType().Name} {ex.Message}");
            }
            return _session;
        }

        /// <summary>The photo turned as it is shown, stretched to 1024 x 1024 as the model was trained.</summary>
        private static async Task<byte[]> DecodeAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = Side, ScaledHeight = Side, InterpolationMode = BitmapInterpolationMode.Fant };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return data.DetachPixelData();
        }
    }
}

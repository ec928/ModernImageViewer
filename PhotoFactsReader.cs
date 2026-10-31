using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    /// <summary>Reads what the composer needs from a picture, from a small decode so that sixteen of them stay quick.</summary>
    public static class PhotoFactsReader
    {
        // Found faces in 18 of 24 family photos at about 75 ms each, against 13 at 720.
        private const uint FaceEdge = 1280;
        private const int GridCells = 10;
        private const int CellPixels = 16;

        public static async Task<FaceDetector?> CreateDetectorAsync() =>
            FaceDetector.IsSupported ? await FaceDetector.CreateAsync() : null;

        public static async Task<PhotoFacts?> ReadAsync(string path, FaceDetector? detector, CancellationToken token = default)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using IRandomAccessStream stream = await file.OpenReadAsync().AsTask(token);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                double width = decoder.OrientedPixelWidth;
                double height = decoder.OrientedPixelHeight;
                if (width < 1 || height < 1) return null;

                var faces = new List<UnitRect>();
                if (detector != null)
                {
                    double ratio = Math.Min(1.0, (double)FaceEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * ratio),
                        ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * ratio),
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };
                    using var color = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);
                    using var small = SoftwareBitmap.Convert(color, BitmapPixelFormat.Gray8);
                    foreach (var face in await detector.DetectFacesAsync(small).AsTask(token))
                    {
                        faces.Add(new UnitRect(
                            face.FaceBox.X / (double)small.PixelWidth,
                            face.FaceBox.Y / (double)small.PixelHeight,
                            face.FaceBox.Width / (double)small.PixelWidth,
                            face.FaceBox.Height / (double)small.PixelHeight));
                    }
                }

                int side = GridCells * CellPixels;
                var proxy = new BitmapTransform { ScaledWidth = (uint)side, ScaledHeight = (uint)side };
                using var tiny = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, proxy, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);
                var pixels = new byte[4 * side * side];
                tiny.CopyToBuffer(pixels.AsBuffer());
                var (focusX, focusY) = FindFocus(pixels, side);
                return new PhotoFacts(path, width, height, faces, focusX, focusY, MainColor(pixels));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoFacts] {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>With no face to go on, the busiest part of the picture, leaning toward the upper part where subjects tend to be.</summary>
        private static (double X, double Y) FindFocus(byte[] pixels, int side)
        {
            int best = 30;
            double x = 0.5, y = 0.42;
            for (int row = 0; row < GridCells; row++)
            {
                for (int col = 0; col < GridCells; col++)
                {
                    int low = 255, high = 0;
                    for (int py = row * CellPixels; py < (row + 1) * CellPixels; py++)
                    {
                        for (int px = col * CellPixels; px < (col + 1) * CellPixels; px++)
                        {
                            int i = (py * side + px) * 4;
                            int luma = (pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000;
                            low = Math.Min(low, luma);
                            high = Math.Max(high, luma);
                        }
                    }
                    double lean = row <= 2 ? 1.5 : row <= 5 ? 1.25 : 1;
                    int score = (int)((high - low) * lean);
                    if (score > best)
                    {
                        best = score;
                        x = (col + 0.5) / GridCells;
                        y = (row + 0.5) / GridCells;
                    }
                }
            }
            return (x, y);
        }

        /// <summary>The picture's color, counting vivid pixels most, so a grey sky does not outvote a red coat.</summary>
        private static string MainColor(byte[] pixels)
        {
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                double weight = 0.05 + (max - min) / 255.0;
                b += pixels[i] * weight;
                g += pixels[i + 1] * weight;
                r += pixels[i + 2] * weight;
                total += weight;
            }
            if (total <= 0) return "#FF808080";
            return $"#FF{(byte)(r / total):X2}{(byte)(g / total):X2}{(byte)(b / total):X2}";
        }
    }
}

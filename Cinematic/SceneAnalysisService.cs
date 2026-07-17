using ModernImageViewer.Cinematic;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ModernImageViewer.Cinematic.Services
{
    public static class SceneAnalysisService
    {
        public static async Task<(SoftwareBitmap? Bitmap, SceneIntelligence Intel)> AnalyzeImageAsync(
    string filePath, FaceDetector? faceDetector, bool useFaceAware, CancellationToken token)
        {
            SoftwareBitmap? softwareBitmap = null;
            var intel = new SceneIntelligence();

            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                token.ThrowIfCancellationRequested();

                // === IMPROVED: Full color-managed decode ===
                softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                token.ThrowIfCancellationRequested();

                double dipRatioX = softwareBitmap.DpiX > 0 ? 96.0 / softwareBitmap.DpiX : 1.0;
                double dipRatioY = softwareBitmap.DpiY > 0 ? 96.0 / softwareBitmap.DpiY : 1.0;
                intel.ImageSize = new Size(softwareBitmap.PixelWidth * dipRatioX, softwareBitmap.PixelHeight * dipRatioY);

                bool targetFound = false;

                if (faceDetector != null && useFaceAware)
                {
                    using var grayBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Gray8);
                    var faces = await faceDetector.DetectFacesAsync(grayBitmap);

                    token.ThrowIfCancellationRequested();

                    foreach (var face in faces)
                    {
                        intel.Faces.Add(new Rect(
                            face.FaceBox.X * dipRatioX,
                            face.FaceBox.Y * dipRatioY,
                            face.FaceBox.Width * dipRatioX,
                            face.FaceBox.Height * dipRatioY));
                    }

                    if (intel.Faces.Any())
                    {
                        var bestFace = intel.Faces.OrderByDescending(f => f.Width * f.Height).First();
                        float cx = (float)(bestFace.X + (bestFace.Width / 2f));
                        float cy = (float)(bestFace.Y + (bestFace.Height / 2f));
                        float headroom = (float)(bestFace.Height * 0.30f);
                        intel.SaliencyTarget = new Vector2(cx, cy - headroom);
                        targetFound = true;
                    }
                }

                if (!targetFound && useFaceAware)
                {
                    // (The rest of the fallback saliency logic stays exactly the same)
                    stream.Seek(0);
                    BitmapTransform proxyTransform = new BitmapTransform { ScaledWidth = 160, ScaledHeight = 160 };
                    using SoftwareBitmap proxyBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore,
                        proxyTransform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    token.ThrowIfCancellationRequested();

                    byte[] pixels = new byte[4 * 160 * 160];
                    proxyBitmap.CopyToBuffer(pixels.AsBuffer());

                    int bestScore = 0;
                    float bestRelX = 0.5f;
                    float bestRelY = 0.38f;

                    for (int row = 0; row < 10; row++)
                    {
                        for (int col = 0; col < 10; col++)
                        {
                            int minL = 255, maxL = 0;
                            for (int y = 0; y < 16; y++)
                            {
                                for (int x = 0; x < 16; x++)
                                {
                                    int px = (col * 16) + x;
                                    int py = (row * 16) + y;
                                    int idx = (py * 160 + px) * 4;
                                    int luma = (pixels[idx + 2] * 299 + pixels[idx + 1] * 587 + pixels[idx] * 114) / 1000;
                                    if (luma < minL) minL = luma;
                                    if (luma > maxL) maxL = luma;
                                }
                            }

                            int variance = maxL - minL;
                            float rowFactor = (row <= 2) ? 2.4f : (row <= 4) ? 1.7f : 1.0f;
                            int score = (int)(variance * rowFactor);

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestRelX = (col * 16 + 8) / 160f;
                                bestRelY = (row * 16 + 8) / 160f;
                            }
                        }
                    }

                    if (bestScore > 30)
                    {
                        intel.SaliencyTarget = new Vector2(
                            (float)(softwareBitmap.PixelWidth * bestRelX * dipRatioX),
                            (float)(softwareBitmap.PixelHeight * bestRelY * dipRatioY));
                        targetFound = true;
                    }
                }

                if (!targetFound)
                {
                    intel.SaliencyTarget = new Vector2(
                        (float)(softwareBitmap.PixelWidth * dipRatioX / 2f),
                        (float)(softwareBitmap.PixelHeight * dipRatioY * 0.38f));
                }

                return (softwareBitmap, intel);
            }
            catch (OperationCanceledException)
            {
                softwareBitmap?.Dispose();
                return (null, intel);
            }
            catch
            {
                softwareBitmap?.Dispose();
                return (null, intel);
            }
        }
    }
}
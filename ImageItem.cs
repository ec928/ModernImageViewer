using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Graphics.Imaging;

namespace ModernImageViewer
{
    public static class ViewerMath
    {
        public static float CalculateFitZoom(double viewW, double viewH, double logicalW, double logicalH, float dpiScale, float minZoom, float maxZoom)
        {
            if (viewW <= 0 || viewH <= 0 || logicalW <= 0 || logicalH <= 0) return 1.0f;
            double dipsW = logicalW / dpiScale;
            double dipsH = logicalH / dpiScale;
            if (dipsW <= 0 || dipsH <= 0) return 1.0f;

            double fitW = viewW / dipsW;
            double fitH = viewH / dipsH;
            return Math.Clamp((float)Math.Min(fitW, fitH), minZoom, maxZoom);
        }

        public static (double PanX, double PanY) ClampPan(double currentPanX, double currentPanY, double deltaX, double deltaY, double horizOffset, double vertOffset, double scrollWidth, double scrollHeight)
        {
            double tempX = Math.Clamp(currentPanX + deltaX, -(scrollWidth - horizOffset), horizOffset);
            double tempY = Math.Clamp(currentPanY + deltaY, -(scrollHeight - vertOffset), vertOffset);
            return (tempX, tempY);
        }

        public static (float NewZoom, double NewOffsetX, double NewOffsetY) CalculateWheelZoom(
            float targetZoomBase, float currentNativeZoom, float minZoom, float maxZoom, int wheelDelta,
            double pointerViewportX, double pointerViewportY,
            double currentOffsetX, double currentOffsetY,
            double logicalW, double logicalH, float dpiScale,
            double viewportW, double viewportH)
        {
            float zoomDelta = wheelDelta > 0 ? 1.15f : 0.85f;
            float newTargetZoom = Math.Clamp(targetZoomBase * zoomDelta, minZoom, maxZoom);

            double currentDisplayedW = (logicalW / dpiScale) * currentNativeZoom;
            double currentDisplayedH = (logicalH / dpiScale) * currentNativeZoom;

            double currentBlankOffsetX = currentDisplayedW < viewportW ? (viewportW - currentDisplayedW) / 2.0 : 0;
            double currentBlankOffsetY = currentDisplayedH < viewportH ? (viewportH - currentDisplayedH) / 2.0 : 0;

            double absoluteX = currentOffsetX + pointerViewportX;
            double absoluteY = currentOffsetY + pointerViewportY;

            double logicalX = (absoluteX - currentBlankOffsetX) / currentNativeZoom;
            double logicalY = (absoluteY - currentBlankOffsetY) / currentNativeZoom;

            double newDisplayedW = (logicalW / dpiScale) * newTargetZoom;
            double newDisplayedH = (logicalH / dpiScale) * newTargetZoom;

            double newBlankOffsetX = newDisplayedW < viewportW ? (viewportW - newDisplayedW) / 2.0 : 0;
            double newBlankOffsetY = newDisplayedH < viewportH ? (viewportH - newDisplayedH) / 2.0 : 0;

            double newOffsetX = (logicalX * newTargetZoom) + newBlankOffsetX - pointerViewportX;
            double newOffsetY = (logicalY * newTargetZoom) + newBlankOffsetY - pointerViewportY;

            return (newTargetZoom, newOffsetX, newOffsetY);
        }

        public static (double TempPanX, double TempPanY) CalculateDragPan(
            double currentTempPanX, double currentTempPanY,
            double pointerX, double pointerY, double lastPointerX, double lastPointerY,
            double horizontalOffset, double scrollableWidth,
            double verticalOffset, double scrollableHeight)
        {
            double tempX = currentTempPanX + (pointerX - lastPointerX);
            double tempY = currentTempPanY + (pointerY - lastPointerY);

            double maxPanX = horizontalOffset;
            double minPanX = -(scrollableWidth - horizontalOffset);
            tempX = Math.Clamp(tempX, minPanX, maxPanX);

            double maxPanY = verticalOffset;
            double minPanY = -(scrollableHeight - verticalOffset);
            tempY = Math.Clamp(tempY, minPanY, maxPanY);

            return (tempX, tempY);
        }

        public static void DrawMappedImage(
    CanvasDrawingSession session,
    double viewW, double viewH,
    double logicalW, double logicalH,
    double panX, double panY,
    float zoom, float gamma, float dpiScale,
    bool isHighFidelity,
    CanvasBitmap bitmap,
    ColorManagementProfile? profile,
    CropEffect crop,
    ColorManagementEffect colorMgmt, // Kept in signature to avoid breaking callers
    GammaTransferEffect decode,
    GammaTransferEffect userGamma,
    Transform2DEffect transform,
    GammaTransferEffect encode)
        {
            if (zoom <= 0) zoom = 1.0f;
            double imgW = (logicalW / dpiScale) * zoom;
            double imgH = (logicalH / dpiScale) * zoom;

            if (imgW <= 0 || imgH <= 0) return;

            double drawX = imgW <= viewW + 1.0 ? (viewW - imgW) / 2.0 : panX;
            double drawY = imgH <= viewH + 1.0 ? (viewH - imgH) / 2.0 : panY;

            // Both LF and HF now arrive perfectly color-managed by WIC.
            // We bypass Win2D's ColorManagementEffect completely.
            decode.Source = crop;

            userGamma.Source = decode;
            userGamma.RedExponent = gamma / 2.2f;
            userGamma.GreenExponent = gamma / 2.2f;
            userGamma.BlueExponent = gamma / 2.2f;

            transform.Source = userGamma;
            transform.TransformMatrix = Matrix3x2.CreateScale((float)(imgW / bitmap.SizeInPixels.Width), (float)(imgH / bitmap.SizeInPixels.Height));

            encode.Source = transform;
            session.DrawImage(encode, new Vector2((float)drawX, (float)drawY));
        }
    }

    public partial class ImageItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTime DateModified { get; set; }
        public string SizeString { get; set; } = string.Empty;
        public bool HasExifData { get; set; } = false;

        public DispatcherQueue? Dispatcher { get; set; }

        private string _cameraModel = string.Empty;
        public string CameraModel { get => _cameraModel; set { _cameraModel = value; OnPropertyChanged(); } }

        private string _aperture = string.Empty;
        public string Aperture { get => _aperture; set { _aperture = value; OnPropertyChanged(); } }

        private string _shutterSpeed = string.Empty;
        public string ShutterSpeed { get => _shutterSpeed; set { _shutterSpeed = value; OnPropertyChanged(); } }

        private string _iso = string.Empty;
        public string Iso { get => _iso; set { _iso = value; OnPropertyChanged(); } }

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        private bool _isLoadingThumb = false;
        private static readonly SemaphoreSlim _thumbSemaphore = new(4);
        private CancellationTokenSource? _cts;

        // Task tracking for deterministic file locks
        private Task? _thumbnailTask;
        private Task? _exifTask;

        private static readonly string[] ExifProperties = ["System.Photo.ISOSpeed", "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.CameraManufacturer", "System.Photo.CameraModel"];

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public async Task CancelAndAwaitTasksAsync()
        {
            _cts?.Cancel();
            var tasks = new List<Task>();
            if (_thumbnailTask != null && !_thumbnailTask.IsCompleted) tasks.Add(_thumbnailTask);
            if (_exifTask != null && !_exifTask.IsCompleted) tasks.Add(_exifTask);

            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); } catch { }
            }
        }

        public Task LoadExifAsync(CancellationToken token = default)
        {
            _exifTask = InternalLoadExifAsync(token);
            return _exifTask;
        }

        private async Task InternalLoadExifAsync(CancellationToken token)
        {
            if (HasExifData || string.IsNullOrEmpty(Path)) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(Path).AsTask(token);
                var extraProps = await file.Properties.RetrievePropertiesAsync(ExifProperties).AsTask(token);
                if (token.IsCancellationRequested) return;

                string newCameraModel = string.Empty;
                if (extraProps.TryGetValue("System.Photo.CameraModel", out object? modelObj))
                {
                    string mfg = extraProps.TryGetValue("System.Photo.CameraManufacturer", out object? mfgObj) ? (mfgObj?.ToString() ?? "") : "";
                    newCameraModel = $"{mfg} {modelObj}".Trim();
                }

                string newIso = string.Empty;
                if (extraProps.TryGetValue("System.Photo.ISOSpeed", out object? iso)) newIso = $"ISO {iso}";

                string newAperture = string.Empty;
                if (extraProps.TryGetValue("System.Photo.FNumber", out object? f)) newAperture = $"f/{Convert.ToDouble(f):F1}";

                string newShutterSpeed = string.Empty;
                if (extraProps.TryGetValue("System.Photo.ExposureTime", out object? exp))
                {
                    double expVal = Convert.ToDouble(exp);
                    newShutterSpeed = expVal < 1 ? $"1/{(int)Math.Round(1.0 / expVal)}s" : $"{expVal}s";
                }

                Dispatcher?.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    CameraModel = newCameraModel;
                    Iso = newIso;
                    Aperture = newAperture;
                    ShutterSpeed = newShutterSpeed;
                    HasExifData = true;
                });
            }
            catch { }
        }

        public string GetHudDisplayString(double logicalW, double logicalH)
        {
            return $"{(int)logicalW} × {(int)logicalH} • {SizeString} • {DateModified:yyyy-MM-dd HH:mm}";
        }

        public string GetZoomString(float zoom)
        {
            return $"Zoom: {(int)Math.Round(zoom * 100)}%";
        }

        public Task LoadThumbnailAsync()
        {
            _thumbnailTask = InternalLoadThumbnailAsync();
            return _thumbnailTask;
        }

        private async Task InternalLoadThumbnailAsync()
        {
            if (Thumbnail != null || string.IsNullOrEmpty(Path) || _isLoadingThumb) return;
            if (Dispatcher == null) return;

            _isLoadingThumb = true;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            bool enteredSemaphore = false;

            try
            {
                await _thumbSemaphore.WaitAsync(token);
                enteredSemaphore = true;
                await Task.Delay(50, token);
                if (token.IsCancellationRequested) return;

                ImageSource? newThumb = null;

                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(Path).AsTask(token);
                    using StorageItemThumbnail storageThumbnail = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 250, ThumbnailOptions.UseCurrentScale).AsTask(token);

                    if (storageThumbnail != null && !token.IsCancellationRequested)
                    {
                        TaskCompletionSource<ImageSource?> tcs = new();
                        using var registration = token.Register(() => tcs.TrySetCanceled());
                        var clonedStream = storageThumbnail.CloneStream();

                        bool enqueued = Dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                BitmapImage bmp = new();
                                var asyncOp = bmp.SetSourceAsync(clonedStream);
                                asyncOp.Completed = (info, status) =>
                                {
                                    tcs.TrySetResult(bmp);
                                    clonedStream.Dispose();
                                };
                            }
                            catch { tcs.TrySetResult(null); clonedStream.Dispose(); }
                        });

                        if (!enqueued)
                        {
                            clonedStream.Dispose();
                            tcs.TrySetResult(null);
                        }
                        newThumb = await tcs.Task;
                    }
                }
                catch { }

                if (newThumb == null && !token.IsCancellationRequested)
                {
                    TaskCompletionSource<ImageSource?> tcs = new();
                    using var registration = token.Register(() => tcs.TrySetCanceled());

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var file = await StorageFile.GetFileFromPathAsync(Path).AsTask(token);
                            using var stream = await file.OpenReadAsync().AsTask(token);
                            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                            if (token.IsCancellationRequested) { tcs.TrySetResult(null); return; }

                            uint w = decoder.PixelWidth;
                            uint h = decoder.PixelHeight;
                            double ratio = Math.Min(250.0 / w, 250.0 / h);
                            uint scaledW = (uint)Math.Max(1, w * ratio);
                            uint scaledH = (uint)Math.Max(1, h * ratio);

                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                                BitmapPixelFormat.Bgra8,
                                BitmapAlphaMode.Premultiplied,
                                new BitmapTransform { ScaledWidth = scaledW, ScaledHeight = scaledH, InterpolationMode = BitmapInterpolationMode.Fant },
                                ExifOrientationMode.RespectExifOrientation,
                                ColorManagementMode.ColorManageToSRgb).AsTask(token);

                            bool enqueued = Dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    SoftwareBitmapSource source = new();
                                    var asyncOp = source.SetBitmapAsync(softwareBitmap);
                                    asyncOp.Completed = (info, status) =>
                                    {
                                        tcs.TrySetResult(source);
                                        softwareBitmap.Dispose();
                                    };
                                }
                                catch
                                {
                                    softwareBitmap?.Dispose();
                                    tcs.TrySetResult(null);
                                }
                            });

                            if (!enqueued)
                            {
                                softwareBitmap.Dispose();
                                tcs.TrySetResult(null);
                            }
                        }
                        catch { tcs.TrySetResult(null); }
                    });
                    newThumb = await tcs.Task;
                }

                if (newThumb != null && !token.IsCancellationRequested)
                {
                    Dispatcher.TryEnqueue(() => Thumbnail = newThumb);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Thumbnail load failed for {Path}: {ex.Message}");
            }
            finally
            {
                _isLoadingThumb = false;
                if (enteredSemaphore) _thumbSemaphore.Release();
            }
        }

        public void ClearThumbnail()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (Thumbnail is SoftwareBitmapSource softwareSource)
            {
                softwareSource.Dispose();
            }

            Thumbnail = null;
            _isLoadingThumb = false;
        }
    }
}
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    public sealed partial class MainWindow
    {
        private DetachedWindow? _tearingOffWindow;
        private Point _tearOffStartPos;
        private static readonly SemaphoreSlim _cacheSemaphore = new(4);

        // Store active decode tasks for safe cancellation and awaiting
        private Task<ImageCacheEntry?>? _fastDecodeTask;
        private Task<ImageCacheEntry?>? _hfTask;

        private void TryDisposeRawGpuBitmap()
        {
            // Replaced manual raw disposal with cache entry release
            _rawGpuBitmap = null;
        }

        public async void LaunchDetachedWindow(ImageItem targetItem, Point? cursorSpawnPosition = null, Windows.Graphics.PointInt32? screenSpawnPosition = null)
        {
            if (targetItem == null) return;

            var displayArea = screenSpawnPosition.HasValue
                ? Microsoft.UI.Windowing.DisplayArea.GetFromPoint(screenSpawnPosition.Value, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)
                : Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            double workAreaW = displayArea.WorkArea.Width;
            double workAreaH = displayArea.WorkArea.Height;

            double currentLogicalW = 0;
            double currentLogicalH = 0;
            float targetZoom = 1.0f;
            bool shouldFitToWindow = !cursorSpawnPosition.HasValue;

            if (cursorSpawnPosition.HasValue)
            {
                currentLogicalW = ViewerControl.LogicalWidth > 0 ? ViewerControl.LogicalWidth : 1000;
                currentLogicalH = ViewerControl.LogicalHeight > 0 ? ViewerControl.LogicalHeight : 1000;
                targetZoom = ViewerControl.CurrentZoom > 0 ? ViewerControl.CurrentZoom : 1.0f;
            }
            else
            {
                if (ViewerControl.TargetImage == targetItem && ViewerControl.LogicalWidth > 0 && ViewerControl.LogicalHeight > 0)
                {
                    currentLogicalW = ViewerControl.LogicalWidth;
                    currentLogicalH = ViewerControl.LogicalHeight;
                }
                else if (ImageCache.TryGet(targetItem.Path, out var cachedEntry) && cachedEntry != null && cachedEntry.NativeWidth > 0 && cachedEntry.NativeHeight > 0)
                {
                    currentLogicalW = cachedEntry.NativeWidth;
                    currentLogicalH = cachedEntry.NativeHeight;
                }
                else
                {
                    try
                    {
                        using var stream = await ViewerEngine.OpenImageStreamAsync(targetItem.Path);
                        if (stream != null)
                        {
                            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                            currentLogicalW = decoder.OrientedPixelWidth;
                            currentLogicalH = decoder.OrientedPixelHeight;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Detach] Failed to read dimensions for {targetItem.Path}: {ex.Message}");
                    }
                }

                if (currentLogicalW <= 0 || currentLogicalH <= 0)
                {
                    currentLogicalW = 1200;
                    currentLogicalH = 800;
                }
            }

            double targetW;
            double targetH;

            if (cursorSpawnPosition.HasValue)
            {
                if (CurrentDetachLimit != DetachLimit.CurrentZoom)
                {
                    double targetBoxH = workAreaH;

                    if (CurrentDetachLimit == DetachLimit.Target1080p) targetBoxH = 1080;
                    else if (CurrentDetachLimit == DetachLimit.Target1200p) targetBoxH = 1200;
                    else if (CurrentDetachLimit == DetachLimit.Target800p) targetBoxH = 800;

                    targetBoxH = Math.Min(targetBoxH, workAreaH);
                    targetZoom = (float)(targetBoxH / currentLogicalH);

                    if (currentLogicalW * targetZoom > workAreaW)
                    {
                        targetZoom = (float)(workAreaW / currentLogicalW);
                    }
                }

                targetW = currentLogicalW * targetZoom;
                targetH = currentLogicalH * targetZoom;

                if (targetW > workAreaW || targetH > workAreaH)
                {
                    double scaleX = workAreaW / currentLogicalW;
                    double scaleY = workAreaH / currentLogicalH;
                    targetZoom = (float)Math.Min(scaleX, scaleY);
                    targetW = currentLogicalW * targetZoom;
                    targetH = currentLogicalH * targetZoom;
                }
            }
            else
            {
                // Sensible default floating window size: comfortable bounds instead of taking over the screen
                double maxBoxW = Math.Min(1280, workAreaW * 0.60);
                double maxBoxH = Math.Min(800, workAreaH * 0.65);

                if (CurrentDetachLimit == DetachLimit.Target1080p)
                {
                    maxBoxW = Math.Min(1920, maxBoxW);
                    maxBoxH = Math.Min(1080, maxBoxH);
                }
                else if (CurrentDetachLimit == DetachLimit.Target1200p)
                {
                    maxBoxW = Math.Min(1920, maxBoxW);
                    maxBoxH = Math.Min(1200, maxBoxH);
                }
                else if (CurrentDetachLimit == DetachLimit.Target800p)
                {
                    maxBoxW = Math.Min(1280, maxBoxW);
                    maxBoxH = Math.Min(800, maxBoxH);
                }

                double scale = Math.Min(maxBoxW / currentLogicalW, maxBoxH / currentLogicalH);
                if (currentLogicalW <= maxBoxW && currentLogicalH <= maxBoxH)
                {
                    scale = 1.0;
                }

                targetW = currentLogicalW * scale;
                targetH = currentLogicalH * scale;
                targetZoom = (float)scale;
            }

            int finalW = Math.Clamp((int)Math.Round(targetW), 320, (int)workAreaW);
            int finalH = Math.Clamp((int)Math.Round(targetH), 240, (int)workAreaH);

            var detachedWindow = new DetachedWindow(targetItem, targetZoom, finalW, finalH, _currentGamma, shouldFitToWindow);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(detachedWindow);
            var appWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(appWindowId);

            NativeMethods.GetCursorPos(out var cursorPos);

            if (cursorSpawnPosition.HasValue)
            {
                double percentX = Math.Clamp(cursorSpawnPosition.Value.X / Math.Max(1, ViewerControl.ActualWidth), 0, 1);
                double percentY = Math.Clamp(cursorSpawnPosition.Value.Y / Math.Max(1, ViewerControl.ActualHeight), 0, 1);
                int offsetX = (int)(finalW * percentX);
                int offsetY = (int)(finalH * percentY);
                appWindow.Move(new Windows.Graphics.PointInt32(cursorPos.X - offsetX, cursorPos.Y - offsetY));

                _tearingOffWindow = detachedWindow;
                _tearOffStartPos = cursorSpawnPosition.Value;
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }
            else if (screenSpawnPosition.HasValue)
            {
                int spawnX = screenSpawnPosition.Value.X - (finalW / 2);
                int spawnY = screenSpawnPosition.Value.Y - (finalH / 2);

                spawnX = Math.Clamp(spawnX, displayArea.WorkArea.X, Math.Max(displayArea.WorkArea.X, displayArea.WorkArea.X + displayArea.WorkArea.Width - finalW));
                spawnY = Math.Clamp(spawnY, displayArea.WorkArea.Y, Math.Max(displayArea.WorkArea.Y, displayArea.WorkArea.Y + displayArea.WorkArea.Height - finalH));

                appWindow.Move(new Windows.Graphics.PointInt32(spawnX, spawnY));
                detachedWindow.Activate();
            }
            else
            {
                int spawnX = Math.Max(displayArea.WorkArea.X, _appWindow.Position.X + 60);
                int spawnY = Math.Max(displayArea.WorkArea.Y, _appWindow.Position.Y + 60);

                if (spawnX + finalW > displayArea.WorkArea.X + displayArea.WorkArea.Width)
                    spawnX = Math.Max(displayArea.WorkArea.X, displayArea.WorkArea.X + displayArea.WorkArea.Width - finalW);

                if (spawnY + finalH > displayArea.WorkArea.Y + displayArea.WorkArea.Height)
                    spawnY = Math.Max(displayArea.WorkArea.Y, displayArea.WorkArea.Y + displayArea.WorkArea.Height - finalH);

                appWindow.Move(new Windows.Graphics.PointInt32(spawnX, spawnY));
                detachedWindow.Activate();
            }
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isFullscreen || DragZone == null) return;
            var position = e.GetCurrentPoint(RootGrid).Position;

            if (position.Y <= 45) DragZone.Opacity = 1.0;
            else DragZone.Opacity = 0.0;
        }

        private async void LoadFullImage(int index, int stepDirection = 1)
        {
            if (index < 0 || index >= Images.Count || _canvasDevice == null) return;

            if (Images[index].IsComicArchive)
            {
                _ = ScanFolder(Images[index].Path, openFirstPage: true);
                return;
            }

            _currentIndex = index;
            if (_listMode) PlaceListCursorOnCurrentImage();
            // Visible before the cursor sync so the list preview hides instead of flashing on top of the photo.
            ViewerControl.Visibility = Visibility.Visible;
            SyncTileCursor(scroll: false);
            int myLoadId = ++_currentImageLoadId;

            _hfCts?.Cancel();
            _fastDecodeCts?.Cancel();
            _fastDecodeCts?.Dispose();
            _fastDecodeCts = new CancellationTokenSource();
            var fastToken = _fastDecodeCts.Token;

            _exifCts?.Cancel();
            _exifCts?.Dispose();
            _exifCts = new CancellationTokenSource();
            var exifToken = _exifCts.Token;

            _isHighFidelityActive = false;
            _promoteEdge = 0;
            _hfPromotionTimer?.Stop();

            var currentItem = Images[index];

            ViewerControl.Visibility = Visibility.Visible;
            SyncTileChrome();
            if (HoverTriggerZone != null) HoverTriggerZone.Visibility = Visibility.Collapsed;

            ViewerControl.PrepareForNewImage(currentItem.Thumbnail);
            ViewerControl.TargetImage = currentItem;
            ViewerControl.Focus(FocusState.Programmatic);

            bool isCached = ImageCache.TryGet(currentItem.Path, out var cachedEntry) &&
                            cachedEntry != null &&
                            (cachedEntry.GpuBitmap != null || cachedEntry.Bitmap != null || cachedEntry.AnimationFrames != null);

            _ = currentItem.LoadExifAsync(exifToken);

            try
            {
                ImageCacheEntry? targetEntry = null;

                if (isCached && cachedEntry != null)
                {
                    if (_canvasDevice != null && !cachedEntry.DeviceMatches(_canvasDevice))
                    {
                        ImageCache.Evict(currentItem.Path);
                        cachedEntry = null;
                    }
                    else if (cachedEntry.GpuBitmap == null && cachedEntry.AnimationFrames == null && cachedEntry.Bitmap != null)
                    {
                        cachedEntry.GpuBitmap = ViewerEngine.CreateGpuBitmap(cachedEntry.Bitmap);
                        cachedEntry.Bitmap.Dispose();
                        cachedEntry.Bitmap = null;
                    }
                    targetEntry = cachedEntry;
                }

                if (targetEntry == null || (targetEntry.GpuBitmap == null && targetEntry.AnimationFrames == null))
                {
                    _fastDecodeTask = ViewerEngine.DecodeFastPreviewAsync(currentItem.Path, fastToken);
                    var result = await _fastDecodeTask;

                    if (fastToken.IsCancellationRequested || myLoadId != _currentImageLoadId) { result?.Release(); return; }

                    if (result != null && result.Bitmap != null)
                    {
                        result.GpuBitmap = ViewerEngine.CreateGpuBitmap(result.Bitmap);
                        result.Bitmap.Dispose();
                        result.Bitmap = null;
                    }

                    if (result != null && (result.GpuBitmap != null || result.AnimationFrames != null))
                        ImageCache.Publish(currentItem.Path, result);
                    targetEntry = result;
                }

                if (targetEntry == null || (targetEntry.GpuBitmap == null && targetEntry.AnimationFrames == null))
                {
                    try
                    {
                        var recoveryEntry = await ViewerEngine.DecodeFastPreviewAsync(currentItem.Path, fastToken);
                        if (recoveryEntry?.Bitmap != null && !fastToken.IsCancellationRequested)
                        {
                            recoveryEntry.GpuBitmap = ViewerEngine.CreateGpuBitmap(recoveryEntry.Bitmap);
                            recoveryEntry.Bitmap.Dispose();
                            recoveryEntry.Bitmap = null;
                            ImageCache.Publish(currentItem.Path, recoveryEntry);
                            targetEntry = recoveryEntry;
                        }
                        else { recoveryEntry?.Release(); }
                    }
                    catch (Exception ex) { Debug.WriteLine($"LoadFullImage recovery: {ex.Message}"); }
                }

                if (myLoadId != _currentImageLoadId) return;
                if (targetEntry == null || (targetEntry.GpuBitmap == null && targetEntry.AnimationFrames == null))
                {
                    ViewerControl?.ShowNotification("Could not decode this image");
                    return;
                }

                TryDisposeRawGpuBitmap();

                _rawGpuBitmap = targetEntry.GpuBitmap;
                _rawGpuProfile = targetEntry.Profile;

                _logicalImageWidth = targetEntry.NativeWidth;
                _logicalImageHeight = targetEntry.NativeHeight;

                if (_logicalImageWidth == 0)
                {
                    var size = await GetNativeImageSizeAsync(currentItem.Path);
                    _logicalImageWidth = size.Width > 0 ? size.Width : (_rawGpuBitmap?.SizeInPixels.Width ?? 1);
                    _logicalImageHeight = size.Height > 0 ? size.Height : (_rawGpuBitmap?.SizeInPixels.Height ?? 1);
                    targetEntry.NativeWidth = _logicalImageWidth;
                    targetEntry.NativeHeight = _logicalImageHeight;
                }

                _currentRenderedItem = currentItem;
                _isHighFidelityActive = targetEntry.IsHighFidelity;

                ViewerControl.InjectImage(targetEntry, true);

                if (ViewerControl.BookSpreadMode && targetEntry.AnimationFrames == null && CanPairWithNextPage(index, _logicalImageWidth, _logicalImageHeight))
                {
                    var nextItem = Images[index + 1];
                    var secondEntry = await LoadOrDecodeSpreadEntryAsync(nextItem.Path, fastToken);
                    if (myLoadId != _currentImageLoadId) return;
                    if (secondEntry != null && secondEntry.GpuBitmap != null && secondEntry.AnimationFrames == null && IsPortraitPage(secondEntry.NativeWidth, secondEntry.NativeHeight))
                    {
                        ViewerControl.InjectSpreadSecondPage(secondEntry, nextItem, resetView: true);
                    }
                    else
                    {
                        ViewerControl.ClearSecondPage();
                    }
                }
                else
                {
                    ViewerControl.ClearSecondPage();
                }

                ManageCache(index, stepDirection);

                QueueSharperDecode();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadFullImage error: {ex.Message}");
            }
        }

        private static bool IsPortraitPage(double width, double height)
        {
            return width > 0 && height > 0 && (width / height) <= 1.05;
        }

        private bool CanPairWithNextPage(int index, double width, double height)
        {
            if (index < 0 || index + 1 >= Images.Count) return false;
            if (ViewerControl != null && ViewerControl.CoverPageSolo && index == 0) return false;
            if (Images[index].IsComicArchive || Images[index + 1].IsComicArchive) return false;
            return IsPortraitPage(width, height);
        }

        private async Task<ImageCacheEntry?> LoadOrDecodeSpreadEntryAsync(string path, CancellationToken token)
        {
            if (ImageCache.TryGet(path, out var cached) && cached != null)
            {
                if (_canvasDevice != null && !cached.DeviceMatches(_canvasDevice))
                {
                    ImageCache.Evict(path);
                }
                else
                {
                    if (cached.GpuBitmap == null && cached.AnimationFrames == null && cached.Bitmap != null)
                    {
                        cached.GpuBitmap = ViewerEngine.CreateGpuBitmap(cached.Bitmap);
                        cached.Bitmap.Dispose();
                        cached.Bitmap = null;
                    }
                    if (cached.GpuBitmap != null || cached.AnimationFrames != null)
                        return cached;
                }
            }

            var result = await ViewerEngine.DecodeFastPreviewAsync(path, token);
            if (token.IsCancellationRequested)
            {
                result?.Release();
                return null;
            }
            if (result != null && result.Bitmap != null)
            {
                result.GpuBitmap = ViewerEngine.CreateGpuBitmap(result.Bitmap);
                result.Bitmap.Dispose();
                result.Bitmap = null;
            }
            if (result != null && (result.GpuBitmap != null || result.AnimationFrames != null))
            {
                ImageCache.Publish(path, result);
                return result;
            }
            result?.Release();
            return null;
        }

        private void QueueSharperDecode()
        {
            if (ViewerControl == null || ViewerControl.Visibility != Visibility.Visible) return;
            _hfPromotionTimer?.Stop();
            _hfPromotionTimer?.Start();
        }

        private async Task PromoteDisplayedAsync()
        {
            if (ViewerControl == null || ViewerControl.Visibility != Visibility.Visible) return;
            if (_currentIndex < 0 || _currentIndex >= Images.Count) return;

            string path = Images[_currentIndex].Path;
            double nativeW = ViewerControl.LogicalWidth;
            double nativeH = ViewerControl.LogicalHeight;
            if (nativeW <= 1 || nativeH <= 1) return;

            int have = 0;
            if (ImageCache.TryGet(path, out var cached) && cached != null)
                have = cached.DecodedLongEdge;

            int edge = ViewerMath.NextDecodeEdge(nativeW, nativeH, ViewerControl.CurrentZoom, ViewerControl.FitZoom, have, (int)ViewerEngine.HighFidelityMaxEdge);
            if (edge <= 0) return;
            if (_promoteEdge >= edge && _hfTask is { IsCompleted: false }) return;

            int promoteId = _currentImageLoadId;
            _hfCts?.Cancel();
            _hfCts?.Dispose();
            _hfCts = new CancellationTokenSource();
            var token = _hfCts.Token;
            _promoteEdge = edge;

            _hfTask = ViewerEngine.DecodeHighFidelityAsync(path, token, (uint)edge);
            var decoded = await _hfTask;

            if (token.IsCancellationRequested || promoteId != _currentImageLoadId || decoded == null)
            {
                decoded?.Release();
                if (promoteId == _currentImageLoadId && _promoteEdge == edge) _promoteEdge = 0;
                return;
            }

            bool success = ViewerEngine.FinalizeHighFidelityGpuResources(decoded);
            if (!success || token.IsCancellationRequested || promoteId != _currentImageLoadId)
            {
                decoded.Release();
                if (promoteId == _currentImageLoadId && _promoteEdge == edge) _promoteEdge = 0;
                return;
            }

            ImageCache.Publish(path, decoded);
            if (promoteId != _currentImageLoadId) return;
            if (!ImageCache.TryGet(path, out var published) || published == null) return;
            if (published.GpuBitmap == null && published.AnimationFrames == null) return;

            TryDisposeRawGpuBitmap();
            _rawGpuBitmap = published.GpuBitmap;
            _rawGpuProfile = published.Profile;
            _isHighFidelityActive = published.IsHighFidelity;

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                ViewerControl.InjectImage(published, false);

                if (ViewerControl.IsShowingTwoPages && _currentIndex + 1 < Images.Count)
                {
                    var nextItem = Images[_currentIndex + 1];
                    string nextPath = nextItem.Path;
                    int haveNext = 0;
                    if (ImageCache.TryGet(nextPath, out var cachedNext) && cachedNext != null)
                        haveNext = cachedNext.DecodedLongEdge;
                    if (haveNext < edge)
                    {
                        var decodedNext = await ViewerEngine.DecodeHighFidelityAsync(nextPath, token, (uint)edge);
                        if (decodedNext != null && !token.IsCancellationRequested && promoteId == _currentImageLoadId)
                        {
                            if (ViewerEngine.FinalizeHighFidelityGpuResources(decodedNext))
                            {
                                ImageCache.Publish(nextPath, decodedNext);
                                if (promoteId == _currentImageLoadId && ImageCache.TryGet(nextPath, out var pubNext) && pubNext?.GpuBitmap != null && ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
                                    ViewerControl.InjectSpreadSecondPage(pubNext, nextItem, resetView: false);
                            }
                            else decodedNext.Release();
                        }
                        else decodedNext?.Release();
                    }
                }
            }
        }

        private static async Task<Size> GetNativeImageSizeAsync(string path, CancellationToken token = default)
        {
            try
            {
                using var stream = await ViewerEngine.OpenImageStreamAsync(path, token);
                if (stream == null) return new Size(0, 0);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                return new Size(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Size(0, 0); }
        }

        private void ManageCache(int centerIndex, int stepDirection)
        {
            const int prefetchRadiusAhead = 15;
            const int prefetchRadiusBehind = 8;
            const int keepAliveRadius = 20;

            _activeCachePaths.Clear();

            int ahead = stepDirection > 0 ? prefetchRadiusAhead : prefetchRadiusBehind;
            int behind = stepDirection > 0 ? prefetchRadiusBehind : prefetchRadiusAhead;

            var keepAlivePaths = BuildKeepAlivePaths(centerIndex, keepAliveRadius);
            BuildPrefetchPaths(centerIndex, ahead, behind);
            var prefetchWave = new HashSet<string>(_activeCachePaths, StringComparer.OrdinalIgnoreCase);

            ImageCache.EvictWhere(k => !keepAlivePaths.Contains(k));

            var decodesToCancel = _activeDecodes.Keys.Where(k => !keepAlivePaths.Contains(k)).ToList();
            foreach (var key in decodesToCancel)
            {
                if (_activeDecodes.TryGetValue(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    _activeDecodes.Remove(key);
                }
            }

            foreach (var path in prefetchWave)
            {
                if (ImageCache.Contains(path) || _activeDecodes.ContainsKey(path))
                    continue;

                var cts = new CancellationTokenSource();
                _activeDecodes[path] = cts;
                var token = cts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _cacheSemaphore.WaitAsync(token);
                        try
                        {
                            if (token.IsCancellationRequested || _canvasDevice == null || !prefetchWave.Contains(path))
                                return;

                            var entry = await ViewerEngine.DecodeFastPreviewAsync(path, token);
                            if (token.IsCancellationRequested || entry?.Bitmap == null)
                            {
                                entry?.Release();
                                return;
                            }

                            bool enqueued = this.DispatcherQueue.TryEnqueue(() =>
                            {
                                if (token.IsCancellationRequested) { entry.Release(); return; }

                                try
                                {
                                    entry.GpuBitmap = ViewerEngine.CreateGpuBitmap(entry.Bitmap);
                                    entry.Bitmap?.Dispose();
                                    entry.Bitmap = null;

                                    bool shouldKeep = keepAlivePaths.Contains(path);
                                    if (shouldKeep && entry.GpuBitmap != null)
                                        ImageCache.Publish(path, entry);
                                    else
                                        entry.Release();
                                }
                                catch { entry.Release(); }
                            });

                            if (!enqueued) entry.Release();
                        }
                        finally { _cacheSemaphore.Release(); }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Debug.WriteLine($"[Prefetch] {path}: {ex.Message}"); }
                    finally
                    {
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_activeDecodes.TryGetValue(path, out var current) && ReferenceEquals(current, cts))
                                _activeDecodes.Remove(path);
                        });
                    }
                }, token);
            }
        }

        private HashSet<string> BuildKeepAlivePaths(int centerIndex, int radius)
        {
            var keepAlive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (centerIndex >= 0 && centerIndex < Images.Count)
                keepAlive.Add(Images[centerIndex].Path);

            for (int i = 1; i <= radius; i++)
            {
                if (centerIndex + i < Images.Count) keepAlive.Add(Images[centerIndex + i].Path);
                if (centerIndex - i >= 0) keepAlive.Add(Images[centerIndex - i].Path);
            }
            return keepAlive;
        }

        private void BuildPrefetchPaths(int centerIndex, int ahead, int behind)
        {
            if (centerIndex >= 0 && centerIndex < Images.Count)
                _activeCachePaths.Add(Images[centerIndex].Path);

            for (int i = 1; i <= ahead; i++)
            {
                if (centerIndex + i < Images.Count) _activeCachePaths.Add(Images[centerIndex + i].Path);
            }

            for (int i = 1; i <= behind; i++)
            {
                if (centerIndex - i >= 0) _activeCachePaths.Add(Images[centerIndex - i].Path);
            }
        }

        private void ClearImageCache()
        {
            _activeCachePaths.Clear();

            foreach (var cts in _activeDecodes.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _activeDecodes.Clear();

            string currentPath = (ViewerControl?.Visibility == Visibility.Visible && _currentRenderedItem != null)
                ? _currentRenderedItem.Path
                : string.Empty;

            ImageCache.EvictWhere(k => !string.Equals(k, currentPath, StringComparison.OrdinalIgnoreCase));
        }

        private void Navigate(int step)
        {
            if (Images.Count == 0 || _isScanning || DateTime.Now - _lastNavigationTime < TimeSpan.FromMilliseconds(50)) return;

            _hfCts?.Cancel();
            _lastNavigationTime = DateTime.Now;

            if (step == int.MinValue)
            {
                LoadFullImage(0, 1);
                return;
            }
            if (step == int.MaxValue)
            {
                LoadFullImage(Images.Count - 1, -1);
                return;
            }

            if (step == 1 && ViewerControl != null && ViewerControl.IsShowingTwoPages)
            {
                step = 2;
            }
            else if (step == -1 && ViewerControl != null && ViewerControl.BookSpreadMode && ViewerControl.IsWideEnoughForSpread && CanPairSpreadAt(_currentIndex - 2))
            {
                step = -2;
            }

            int nextIndex = _currentIndex + step;
            int stepDirection = step > 0 ? 1 : -1;

            if (nextIndex >= Images.Count && step > 1 && _currentIndex + 1 < Images.Count)
            {
                nextIndex = _currentIndex + 1;
            }
            else if (nextIndex < 0 && step < -1 && _currentIndex - 1 >= 0)
            {
                nextIndex = 0;
            }

            if (nextIndex >= Images.Count || nextIndex < 0)
            {
                if (_currentIndex < 0 || _currentIndex >= Images.Count) return;

                string currentFolderPath = ComicArchive.TrySplitEntryPath(Images[_currentIndex].Path, out string archivePath, out _)
                    ? archivePath.TrimEnd('\\', '/')
                    : (Path.GetDirectoryName(Images[_currentIndex].Path)?.TrimEnd('\\', '/') ?? string.Empty);
                int folderIdx = _hopFolders.FindIndex(f => f.Path.TrimEnd('\\', '/').Equals(currentFolderPath, StringComparison.OrdinalIgnoreCase));
                int nextFolderIdx = folderIdx + stepDirection;

                if (folderIdx != -1 && nextFolderIdx >= 0 && nextFolderIdx < _hopFolders.Count)
                {
                    string targetFolder = _hopFolders[nextFolderIdx].Path;

                    if (HasSupportedImages(targetFolder))
                    {
                        _ = ScanFolder(targetFolder).ContinueWith(_ =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!string.Equals(_currentDirectory, targetFolder, StringComparison.OrdinalIgnoreCase))
                                    return;
                                if (Images.Count > 0)
                                    LoadFullImage(stepDirection > 0 ? 0 : Images.Count - 1, stepDirection);
                                else
                                    ClosePreviewInternal();
                            });
                        });
                    }
                    else
                    {
                        ViewerControl?.ShowNotification($"Directory Empty: {Path.GetFileName(targetFolder)}");
                    }
                    return;
                }
            }

            if (nextIndex >= 0 && nextIndex < Images.Count)
            {
                LoadFullImage(nextIndex, stepDirection);
            }
        }

        private bool CanPairSpreadAt(int index)
        {
            if (index < 0 || index + 1 >= Images.Count) return false;
            if (ViewerControl != null && ViewerControl.CoverPageSolo && index == 0) return false;
            if (Images[index].IsComicArchive || Images[index + 1].IsComicArchive) return false;
            if (ImageCache.TryGet(Images[index].Path, out var e1) && e1 != null && e1.NativeWidth > 0 && !IsPortraitPage(e1.NativeWidth, e1.NativeHeight))
                return false;
            if (ImageCache.TryGet(Images[index + 1].Path, out var e2) && e2 != null && e2.NativeWidth > 0 && !IsPortraitPage(e2.NativeWidth, e2.NativeHeight))
                return false;
            return true;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        public async void SummonMainPreview(string targetPath)
        {
            if (_isMainWindowHidden)
            {
                _isMainWindowHidden = false;
                _appWindow.Show();
            }

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
            }

            this.Activate();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
            NativeMethods.SetForegroundWindow(hwnd);

            if (string.IsNullOrEmpty(targetPath)) return;

            // 1. If already loaded in current Images collection
            var existingItem = Images.FirstOrDefault(x => x.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
            if (existingItem != null)
            {
                int idx = Images.IndexOf(existingItem);
                LoadFullImage(idx);
                return;
            }

            // 2. If it's a comic archive entry (e.g. archive.cbz|page.jpg)
            if (ComicArchive.TrySplitEntryPath(targetPath, out string archivePath, out _))
            {
                if (File.Exists(archivePath))
                {
                    await ScanFolder(archivePath, openFirstPage: false);
                    var comicItem = Images.FirstOrDefault(x => x.Path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
                    if (comicItem != null)
                    {
                        LoadFullImage(Images.IndexOf(comicItem));
                    }
                    else if (Images.Count > 0)
                    {
                        LoadFullImage(0);
                    }
                }
                return;
            }

            // 3. Regular file on disk
            if (File.Exists(targetPath))
            {
                HandleFileActivation(targetPath);
            }
        }

        public void OpenLayoutFromFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                string json = File.ReadAllText(path);
                var layoutFile = MultiViewLayoutFile.FromJson(json);
                if (layoutFile == null) return;
                string? openedDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(openedDir)) AddMultiViewSaveLocation(openedDir);

                if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
                {
                    ClosePreviewInternal();
                }

                if (MultiViewControl != null)
                {
                    _multiViewParked = false;
                    _multiViewKept = false;
                    SetMultiModeTip();
                    BeginMultiViewCanvas();
                    MultiViewControl.ApplyLayoutFile(layoutFile);
                    MultiViewControl.Visibility = Visibility.Visible;
                    MultiViewControl.FocusView();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open layout file: {ex.Message}");
            }
        }

        public void OpenMultiView(IList<ImageItem> items)
        {
            if (_multiViewParked)
            {
                ApplyParkedPhotos();
                return;
            }
            items ??= Array.Empty<ImageItem>();
            // New photos chosen on purpose replace a session left waiting.
            if (_multiViewKept)
            {
                _multiViewKept = false;
                MultiViewControl?.Clear();
                SetMultiModeTip();
            }

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                ClosePreviewInternal();
            }

            if (MultiViewControl != null)
            {
                bool firstCanvas = BeginMultiViewCanvas();
                MultiViewControl.LoadImages(items);
                MultiViewControl.Visibility = Visibility.Visible;
                MultiViewControl.FocusView();
                if (firstCanvas)
                {
                    void SnapToWide(object sender, SizeChangedEventArgs args)
                    {
                        if (args.NewSize.Width < 50 || args.NewSize.Height < 50) return;
                        MultiViewControl.SizeChanged -= SnapToWide;
                        ApplyMultiViewAspect(16.0 / 9.0);
                    }
                    MultiViewControl.SizeChanged += SnapToWide;
                }
            }
        }

        /// <summary>Leaving keeps the session, so a stray Esc cannot lose a collage. The Multi-View button returns to it.</summary>
        public void CloseMultiView()
        {
            if (MultiViewControl == null) return;
            if (_multiViewParked)
            {
                _multiViewParked = false;
                ShowAddingMode(false);
            }
            else if (MultiViewControl.Visibility == Visibility.Visible)
            {
                EndMultiViewCanvas();
            }
            else
            {
                return;
            }
            MultiViewControl.PrepareToLeave();
            MultiViewControl.Visibility = Visibility.Collapsed;
            _multiViewKept = MultiViewControl.ShownSlots > 0;
            SetMultiModeTip();
            FocusGrid();
        }

        private void ResumeKeptMultiView()
        {
            _multiViewKept = false;
            SetMultiModeTip();
            BeginMultiViewCanvas();
            MultiViewControl.Visibility = Visibility.Visible;
            MultiViewControl.FocusView();
            MultiViewControl.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                MultiViewControl.ReflowFrames);
        }

        private void SetMultiModeTip() =>
            ToolTipService.SetToolTip(MultiModeButton, _multiViewKept ? "Back to your Multi-View layout" : "Multi-View / Layout (Enter)");

        /// <summary>The session waits while the user finds pictures with the app's own navigation.</summary>
        private void MultiViewControl_AddImagesRequested(object? sender, EventArgs e)
        {
            if (MultiViewControl == null || !_multiViewCanvasActive) return;
            MultiViewControl.PrepareToLeave();
            MultiViewControl.Visibility = Visibility.Collapsed;
            EndMultiViewCanvas();
            _multiViewParked = true;
            ImageGrid.SelectedItems.Clear();
            ShowAddingMode(true);
            NoteParkedSelection();
            FocusGrid();
        }

        private void MultiViewControl_StartOverRequested(object? sender, EventArgs e)
        {
            if (MultiViewControl == null) return;
            EndMultiViewCanvas();
            MultiViewControl.Visibility = Visibility.Collapsed;
            MultiViewControl.Clear();
            _multiViewKept = false;
            SetMultiModeTip();
            FocusGrid();
        }

        /// <summary>Back to the session, with whatever pictures were selected as new floating cells.</summary>
        private void ApplyParkedPhotos()
        {
            if (MultiViewControl == null) return;
            var picked = Images
                .Where(img => img.IsGridSelected && !img.IsDirectory && !img.IsComicEntry && IsPictureFile(img.Path))
                .Select(img => img.Path)
                .ToList();
            ResumeParkedMultiView();
            if (picked.Count > 0)
            {
                MultiViewControl.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => _ = MultiViewControl.AddPicturesAsync(picked));
            }
        }

        private void ResumeParkedMultiView()
        {
            if (!_multiViewParked || MultiViewControl == null) return;
            _multiViewParked = false;
            ImageGrid.SelectedItems.Clear();
            ShowAddingMode(false);
            BeginMultiViewCanvas();
            MultiViewControl.Visibility = Visibility.Visible;
            MultiViewControl.FocusView();
            MultiViewControl.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                MultiViewControl.ReflowFrames);
        }

        /// <summary>While the session waits the bar stays up, so it is clear what the selection is for.</summary>
        private void NoteParkedSelection()
        {
            if (!_multiViewParked || MultiSelectCountText == null) return;
            int count = Images.Count(img => img.IsGridSelected && !img.IsDirectory);
            MultiSelectActionBar.Visibility = Visibility.Visible;
            MultiSelectCountText.Text = count > 0 ? count + " selected" : "Click pictures to add";
            ViewTogetherButton.IsEnabled = count > 0;
        }

        private bool CanViewTogether(int selected) => selected > 1 || (_multiViewParked && selected > 0);

        /// <summary>While adding, a plain click selects a picture, and the bar offers only what applies.</summary>
        private void ShowAddingMode(bool adding)
        {
            ImageGrid.IsItemClickEnabled = !adding;
            ImageGrid.IsMultiSelectCheckBoxEnabled = !adding;
            ImageGrid.SelectionMode = adding ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
            ToolTipService.SetToolTip(ViewTogetherButton, adding ? "Add these to the layout (Enter)" : "View Together (Enter)");
            ViewTogetherText.Text = adding ? "Add to layout" : "View Together";
            ViewTogetherButton.IsEnabled = true;
            var other = adding ? Visibility.Collapsed : Visibility.Visible;
            MultiSelectPlayRule.Visibility = other;
            MultiSelectPlayButton.Visibility = other;
            MultiSelectDeleteRule.Visibility = other;
            MultiSelectDeleteButton.Visibility = other;
            ToolTipService.SetToolTip(MultiSelectCloseButton, adding ? "Back to the layout (Esc)" : "Clear Selection (Esc)");
            if (!adding) MultiSelectActionBar.Visibility = Visibility.Collapsed;
        }

        private bool BeginMultiViewCanvas()
        {
            if (_multiViewCanvasActive || _appWindow == null) return false;
            _stashedGridWidth = _appWindow.Size.Width;
            _stashedGridHeight = _appWindow.Size.Height;
            _multiViewCanvasActive = true;
            SyncViewModeButtons();

            int width = _currentSettings.MultiViewWindowWidth;
            int height = _currentSettings.MultiViewWindowHeight;
            bool first = width < 300 || height < 300;
            if (first)
                (width, height) = DefaultMultiViewSize();
            else if (TryGetWorkArea(out var work))
                (width, height) = GridCanvasMath.Fit(width, height, work.Width, work.Height);

            _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            return first;
        }

        private void EndMultiViewCanvas()
        {
            if (!_multiViewCanvasActive || _appWindow == null) return;
            _currentSettings.MultiViewWindowWidth = _appWindow.Size.Width;
            _currentSettings.MultiViewWindowHeight = _appWindow.Size.Height;
            _multiViewCanvasActive = false;
            SyncViewModeButtons();
            if (_stashedGridWidth >= 300 && _stashedGridHeight >= 300)
                _appWindow.Resize(new Windows.Graphics.SizeInt32(_stashedGridWidth, _stashedGridHeight));
            SaveAllSettings();
        }

        private void ApplyMultiViewAspect(double widthOverHeight)
        {
            if (!_multiViewCanvasActive || _appWindow == null || widthOverHeight <= 0) return;
            if (_appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore();

            double scale = MultiViewControl.XamlRoot?.RasterizationScale ?? 1;
            if (scale <= 0) scale = 1;
            int clientW = (int)Math.Round(MultiViewControl.ActualWidth * scale);
            int clientH = (int)Math.Round(MultiViewControl.ActualHeight * scale);
            if (clientW < 50 || clientH < 50)
            {
                clientW = _appWindow.Size.Width;
                clientH = _appWindow.Size.Height;
            }
            int frameW = Math.Max(0, _appWindow.Size.Width - clientW);
            int frameH = Math.Max(0, _appWindow.Size.Height - clientH);
            int newH = Math.Max(300, clientH);
            int newW = Math.Max(1, (int)Math.Round(newH * widthOverHeight));
            int maxW = newW;
            int maxH = newH;
            Windows.Graphics.RectInt32 work = default;
            bool haveWork = TryGetWorkArea(out work);
            if (haveWork)
            {
                maxW = Math.Max(1, work.Width - frameW);
                maxH = Math.Max(1, work.Height - frameH);
            }
            var fitted = GridCanvasMath.Fit(newW, newH, maxW, maxH);
            int outerW = fitted.Width + frameW;
            int outerH = fitted.Height + frameH;
            int x = _appWindow.Position.X;
            int y = _appWindow.Position.Y;
            if (haveWork)
            {
                if (x + outerW > work.X + work.Width) x = work.X + work.Width - outerW;
                if (y + outerH > work.Y + work.Height) y = work.Y + work.Height - outerH;
                if (x < work.X) x = work.X;
                if (y < work.Y) y = work.Y;
            }
            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, outerW, outerH));
        }

        private (int Width, int Height) DefaultMultiViewSize()
        {
            if (TryGetWorkArea(out var work))
            {
                int height = (int)(work.Height * 0.9375);
                int width = (int)Math.Round(height * (16.0 / 9.0));
                return GridCanvasMath.Fit(width, height, work.Width, work.Height);
            }
            return (1920, 1080);
        }

        private bool TryGetWorkArea(out Windows.Graphics.RectInt32 work)
        {
            work = default;
            try
            {
                var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                work = area.WorkArea;
                return work.Width > 0 && work.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        private void ClosePreviewInternal(bool isTearOff = false)
        {
            _hfPromotionTimer?.Stop();
            _hfCts?.Cancel();
            ClearImageCache();

            if (_slideshowTimer != null && _slideshowTimer.IsEnabled) _slideshowTimer.Stop();

            ViewerControl.PrepareForNewImage(null);

            ViewerControl.Visibility = Visibility.Collapsed;
            SyncTileChrome();
            if (HoverTriggerZone != null) HoverTriggerZone.Visibility = Visibility.Visible;
            UpdateListPreview(CursorItem());

            TryDisposeRawGpuBitmap();
            FlushDeferredFolderRefresh();

            if (_currentIndex >= 0 && _currentIndex < Images.Count && ImageGrid != null)
            {
                var targetItem = Images[_currentIndex];
                ImageGrid.ScrollIntoView(targetItem);

                _ = AnimateCloseAsync(targetItem, isTearOff);
            }
        }

        private async Task AnimateCloseAsync(ImageItem targetItem, bool isTearOff)
        {
            try
            {
                await Task.Delay(50);

                if (ImageGrid.ContainerFromItem(targetItem) is GridViewItem container)
                {
                    var scrollViewer = FindScrollViewer(ImageGrid);
                    if (scrollViewer != null)
                    {
                        var transform = container.TransformToVisual(scrollViewer);
                        var positionInScrollViewer = transform.TransformPoint(new Point(0, 0));

                        double centerOffsetY = scrollViewer.VerticalOffset + positionInScrollViewer.Y
                                             - (scrollViewer.ViewportHeight / 2.0)
                                             + (container.ActualHeight / 2.0);

                        scrollViewer.ChangeView(null, centerOffsetY, null, false);
                    }

                    if (!isTearOff) RootGrid.Focus(FocusState.Programmatic);
                }
            }
            catch { }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) FocusGrid();
        }

        private void FocusGrid()
        {
            if (RootGrid == null || _windowClosed) return;
            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                ViewerControl.Focus(FocusState.Programmatic);
                return;
            }
            if (TileKeyFocusStays()) return;
            if (SidebarView != null && SidebarView.IsPaneOpen && FocusIsInside(SidebarPane)) return;
            RootGrid.Focus(FocusState.Programmatic);
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (RootGrid == null || _windowClosed) return;
                if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible) return;
                if (TileKeyFocusStays()) return;
                if (SidebarView != null && SidebarView.IsPaneOpen && FocusIsInside(SidebarPane)) return;
                RootGrid.Focus(FocusState.Programmatic);
            });
        }

        private void HeaderFlyout_Closed(object sender, object e)
        {
            FocusGrid();
        }

        private void SidebarView_PaneClosed(SplitView sender, object args)
        {
            FocusGrid();
        }

        private ImageItem? _tileCursorItem;
        private Grid? _hoveredCard;
        private static readonly Vector3 TilePopScale = new(1.035f, 1.035f, 1f);
        private static readonly Vector3 TileRestScale = new(1f, 1f, 1f);

        private void SyncTileCursor(bool scroll)
        {
            ImageItem? previous = _tileCursorItem;
            ImageItem? next = CursorItem();
            if (!ReferenceEquals(previous, next))
            {
                if (previous != null) previous.IsKeyboardCursor = false;
                _tileCursorItem = next;
                if (next != null) next.IsKeyboardCursor = true;
            }
            if (scroll && next != null) RevealTile(DisplayIndex());
            UpdateListPreview(next);
            if (_listMode) return;
            // The container for a tile that just scrolled into view may not exist until layout.
            UpdateTilePop(previous);
            UpdateTilePop(next);
            ImageGrid?.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_listMode) return;
                UpdateTilePop(previous);
                UpdateTilePop(_tileCursorItem);
            });
        }

        // Same raise the pointer uses. A tile stays raised while the pointer is on it or it is the keyboard cursor.
        private void UpdateTilePop(ImageItem? item)
        {
            if (item == null || ImageGrid == null || _listMode) return;
            if (ImageGrid.ContainerFromItem(item) is not GridViewItem container) return;
            if (TileCard(container) is not Grid card) return;
            SetTileRaised(card, item.IsKeyboardCursor || ReferenceEquals(card, _hoveredCard));
        }

        private static Grid? TileCard(GridViewItem container)
        {
            if (container.ContentTemplateRoot is not Grid root || root.Children.Count == 0) return null;
            return root.Children[0] as Grid;
        }

        private static void SetTileRaised(Grid card, bool raised)
        {
            Vector3 scale = raised ? TilePopScale : TileRestScale;
            if (card.Scale != scale) card.Scale = scale;
            string key = raised ? "TextFillColorSecondaryBrush" : "CardStrokeColorDefaultBrush";
            if (Application.Current.Resources.TryGetValue(key, out object brush) && brush is Brush stroke && !ReferenceEquals(card.BorderBrush, stroke))
                card.BorderBrush = stroke;
        }

        private int DisplayCount() => _listMode ? _listEntries.Count : Images.Count;

        private int DisplayIndex() => _listMode ? _listCursor : _currentIndex;

        private ImageItem? CursorItem()
        {
            int index = DisplayIndex();
            if (_listMode)
                return index >= 0 && index < _listEntries.Count ? _listEntries[index] : null;
            return index >= 0 && index < Images.Count ? Images[index] : null;
        }

        // The photo index stays the source of truth. The list cursor sits on that same photo, after the folder cells.
        private void PlaceListCursorOnCurrentImage()
        {
            int prefix = _listEntries.Count - Images.Count;
            if (prefix < 0) prefix = 0;
            if (_currentIndex >= 0 && _currentIndex < Images.Count)
            {
                int at = prefix + _currentIndex;
                if (at >= 0 && at < _listEntries.Count && ReferenceEquals(_listEntries[at], Images[_currentIndex]))
                {
                    _listCursor = at;
                    return;
                }
                int found = _listEntries.IndexOf(Images[_currentIndex]);
                if (found >= 0)
                {
                    _listCursor = found;
                    return;
                }
            }
            _listCursor = _listEntries.Count > 0 ? 0 : -1;
        }

        // Going up one level puts the cursor back on the folder or comic archive we just left, so Right reaches the next one.
        private bool TryPlaceListCursorOnReturnedFolder()
        {
            if (string.IsNullOrEmpty(_listReturnPath)) return false;
            for (int i = 0; i < _listEntries.Count; i++)
            {
                var item = _listEntries[i];
                if (!SameDirectory(item.Path, _listReturnPath)) continue;
                _listCursor = i;
                if (!item.IsDirectory)
                {
                    int photoIdx = Images.IndexOf(item);
                    if (photoIdx >= 0) _currentIndex = photoIdx;
                }
                return true;
            }
            return false;
        }

        private bool TryPlaceTileCursorOnReturnedItem()
        {
            if (string.IsNullOrEmpty(_listReturnPath)) return false;
            for (int i = 0; i < Images.Count; i++)
            {
                if (!SameDirectory(Images[i].Path, _listReturnPath)) continue;
                _currentIndex = i;
                return true;
            }
            return false;
        }

        private static bool IsChildOf(string childDirectory, string parentDirectory)
        {
            if (string.IsNullOrEmpty(childDirectory) || string.IsNullOrEmpty(parentDirectory)) return false;
            DirectoryInfo? parent;
            try { parent = Directory.GetParent(childDirectory); }
            catch { return false; }
            return parent != null && SameDirectory(parent.FullName, parentDirectory);
        }

        private static bool SameDirectory(string a, string b)
        {
            static string Norm(string path) => path.TrimEnd('\\', '/');
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDriveRoot(string path)
        {
            if (string.IsNullOrEmpty(path) || path == DriveRootPath) return false;
            try
            {
                if (Directory.GetParent(path) != null) return false;
            }
            catch { return false; }
            string trimmed = path.TrimEnd('\\', '/');
            return trimmed.Length == 2 && trimmed[1] == ':';
        }

        private string CurrentLocationText()
        {
            if (AtDriveRoot) return DriveRootName;
            if (string.IsNullOrEmpty(_currentDirectory)) return "";
            string trimmed = _currentDirectory.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            return trimmed;
        }

        private void RebuildListEntries()
        {
            var entries = new List<ImageItem>(_childFolders.Count + Images.Count + 1);
            if (!AtDriveRoot && !string.IsNullOrEmpty(_currentDirectory))
            {
                DirectoryInfo? parent = null;
                try { parent = Directory.GetParent(_currentDirectory); } catch { parent = null; }
                if (parent != null)
                {
                    string name = string.IsNullOrEmpty(parent.Name) ? parent.FullName : parent.Name;
                    entries.Add(DirectoryCell(name, parent.FullName, true));
                }
                else if (IsDriveRoot(_currentDirectory))
                    entries.Add(DirectoryCell(DriveRootName, DriveRootPath, true));
            }
            foreach (var folder in _childFolders)
                entries.Add(DirectoryCell(folder.Name, folder.Path, false));
            entries.AddRange(Images);
            _listEntries.ResetWith(entries);
            _listEntriesValid = true;
        }

        private ImageItem DirectoryCell(string name, string path, bool parent)
        {
            var cell = ImageItem.CreateDirectory(name, path, parent, "\uE8B7");
            if (parent)
            {
                // The parent is "up", so it does not wear the same folder picture as a child.
                if (_parentUpIcon != null) cell.DirectoryIcon = _parentUpIcon;
                else _ = LoadParentUpIconAsync(cell);
                return cell;
            }
            if (!string.IsNullOrEmpty(path) && _folderIcons.TryGetValue(path, out var icon))
                cell.DirectoryIcon = icon;
            else
                _ = LoadDirectoryIconAsync(cell);
            return cell;
        }

        private async Task LoadParentUpIconAsync(ImageItem item)
        {
            try
            {
                var icon = await (_parentUpIconTask ??= DecodeParentUpIconAsync());
                if (icon == null) return;
                _parentUpIcon = icon;
                item.DirectoryIcon = icon;
                ShowFolderIconInPreview(item, icon);
            }
            catch { }
        }

        private async Task<BitmapImage?> DecodeParentUpIconAsync()
        {
            byte[]? png = await Task.Run(NativeMethods.FolderBackPng);
            if (png == null || png.Length == 0) return null;
            var done = new TaskCompletionSource<BitmapImage?>();
            bool queued = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var stream = new InMemoryRandomAccessStream();
                    var writer = new DataWriter(stream);
                    writer.WriteBytes(png);
                    var store = writer.StoreAsync();
                    store.Completed = (_, storeStatus) =>
                    {
                        if (storeStatus != Windows.Foundation.AsyncStatus.Completed)
                        {
                            done.TrySetResult(null);
                            return;
                        }
                        stream.Seek(0);
                        var bmp = new BitmapImage();
                        var op = bmp.SetSourceAsync(stream);
                        op.Completed = (_, status) =>
                        {
                            if (status == Windows.Foundation.AsyncStatus.Completed && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                                done.TrySetResult(bmp);
                            else done.TrySetResult(null);
                        };
                    };
                }
                catch { done.TrySetResult(null); }
            });
            if (!queued) return null;
            return await done.Task;
        }

        private void ShowFolderIconInPreview(ImageItem item, ImageSource icon)
        {
            if (PreviewFolderImage == null) return;
            if (!string.Equals(_previewPath, item.Path, StringComparison.OrdinalIgnoreCase)) return;
            PreviewFolderImage.Source = icon;
            PreviewFolderImage.Visibility = Visibility.Visible;
            if (PreviewFolderIcon != null) PreviewFolderIcon.Visibility = Visibility.Collapsed;
        }

        private async Task LoadDirectoryIconAsync(ImageItem item)
        {
            if (string.IsNullOrEmpty(item.Path)) return;
            await _folderIconGate.WaitAsync();
            try
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(item.Path);
                using var thumb = await folder.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 128, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb == null || thumb.Size == 0) return;
                var cloned = thumb.CloneStream();
                var done = new TaskCompletionSource<BitmapImage?>();
                bool queued = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        var op = bmp.SetSourceAsync(cloned);
                        op.Completed = (_, status) =>
                        {
                            try { cloned.Dispose(); } catch { }
                            if (status == Windows.Foundation.AsyncStatus.Completed && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                                done.TrySetResult(bmp);
                            else done.TrySetResult(null);
                        };
                    }
                    catch
                    {
                        try { cloned.Dispose(); } catch { }
                        done.TrySetResult(null);
                    }
                });
                if (!queued)
                {
                    try { cloned.Dispose(); } catch { }
                    return;
                }
                var ready = await done.Task;
                if (ready == null) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    RememberFolderIcon(item.Path, ready);
                    item.DirectoryIcon = ready;
                    ShowFolderIconInPreview(item, ready);
                });
            }
            catch { }
            finally { _folderIconGate.Release(); }
        }

        private void RememberFolderIcon(string path, ImageSource icon)
        {
            if (_folderIcons.Count > 200) _folderIcons.Clear();
            _folderIcons[path] = icon;
        }

        private void RemoveDisplayedImage(ImageItem item)
        {
            int listIndex = _listEntries.IndexOf(item);
            Images.Remove(item);
            if (listIndex < 0) return;
            _listEntries.RemoveAt(listIndex);
            if (_listCursor > listIndex) _listCursor--;
            if (_listCursor >= _listEntries.Count) _listCursor = _listEntries.Count - 1;
        }

        private void RemoveDisplayedImages(IReadOnlyCollection<ImageItem> items)
        {
            if (items.Count == 0) return;
            if (items.Count == 1)
            {
                RemoveDisplayedImage(items.First());
                if (_currentIndex >= Images.Count) _currentIndex = Images.Count - 1;
                SyncTileCursor(scroll: false);
                return;
            }

            var removeSet = new HashSet<ImageItem>(items);
            var remainingImages = new List<ImageItem>(Math.Max(0, Images.Count - removeSet.Count));
            int removedBeforeCurrent = 0;
            for (int i = 0; i < Images.Count; i++)
            {
                var img = Images[i];
                if (removeSet.Contains(img))
                {
                    if (i < _currentIndex) removedBeforeCurrent++;
                }
                else
                {
                    remainingImages.Add(img);
                }
            }
            Images.ResetWith(remainingImages);
            _currentIndex -= removedBeforeCurrent;
            if (_currentIndex >= Images.Count) _currentIndex = Images.Count - 1;

            if (_listEntries.Count > 0)
            {
                var remainingList = new List<ImageItem>(Math.Max(0, _listEntries.Count - removeSet.Count));
                int removedBeforeCursor = 0;
                for (int i = 0; i < _listEntries.Count; i++)
                {
                    var entry = _listEntries[i];
                    if (removeSet.Contains(entry))
                    {
                        if (i < _listCursor) removedBeforeCursor++;
                    }
                    else
                    {
                        remainingList.Add(entry);
                    }
                }
                _listEntries.ResetWith(remainingList);
                _listCursor -= removedBeforeCursor;
                if (_listCursor >= _listEntries.Count) _listCursor = _listEntries.Count - 1;
            }
            SyncTileCursor(scroll: false);
        }

        private string? _previewPath;
        private int _previewGeneration;
        private CancellationTokenSource? _previewCts;

        // The list keeps its own rows. This band is a fixed strip under them, picture on the right.
        private void UpdateListPreview(ImageItem? item)
        {
            bool photoOpen = ViewerControl != null && ViewerControl.Visibility == Visibility.Visible;
            if (!_listMode || item == null || photoOpen)
            {
                CancelPreviewLoad();
                return;
            }
            if (ListPreviewBar != null) ListPreviewBar.Visibility = Visibility.Visible;
            if (item.IsDirectory)
            {
                ShowDirectoryPreview(item);
                return;
            }
            if (PreviewFolderIcon != null) PreviewFolderIcon.Visibility = Visibility.Collapsed;
            if (PreviewFolderImage != null) PreviewFolderImage.Visibility = Visibility.Collapsed;
            if (PreviewSizeRow != null) PreviewSizeRow.Visibility = Visibility.Visible;
            if (PreviewModifiedRow != null) PreviewModifiedRow.Visibility = Visibility.Visible;
            if (PreviewPhotoRightColumn != null) PreviewPhotoRightColumn.Visibility = Visibility.Visible;
            if (string.Equals(_previewPath, item.Path, StringComparison.OrdinalIgnoreCase)
                && PreviewImage.Source != null
                && PreviewImage.Visibility == Visibility.Visible)
                return;

            _previewPath = item.Path;
            PreviewNameText.Text = item.Name;
            PreviewTypeText.Text = FileTypeLabel(item.Name);
            PreviewSizeText.Text = item.SizeString;
            PreviewModifiedText.Text = item.DateModified.ToString("yyyy-MM-dd HH:mm");
            PreviewDimensionsText.Text = "\u2014";
            PreviewTakenText.Text = "\u2014";
            string folder = ComicArchive.TrySplitEntryPath(item.Path, out string archivePath, out _)
                ? archivePath
                : (Path.GetDirectoryName(item.Path) ?? "");
            PreviewLocationText.Text = folder;
            ToolTipService.SetToolTip(PreviewLocationButton, folder);

            if (PreviewThumbnailReady(item.Thumbnail))
            {
                PreviewImage.Source = item.Thumbnail;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }

            int generation = ++_previewGeneration;
            CancelPreviewLoad();
            _previewCts = new CancellationTokenSource();
            _ = LoadListPreviewAsync(item, generation, _previewCts.Token);
        }

        private void ShowDirectoryPreview(ImageItem item)
        {
            _previewPath = item.Path;
            _previewGeneration++;
            CancelPreviewLoad();
            PreviewNameText.Text = item.Name;
            PreviewTypeText.Text = item.IsParentDirectory ? "Parent folder" : IsDriveRoot(item.Path) ? "Drive" : "Folder";
            if (PreviewSizeRow != null) PreviewSizeRow.Visibility = Visibility.Collapsed;
            if (PreviewModifiedRow != null) PreviewModifiedRow.Visibility = Visibility.Collapsed;
            if (PreviewPhotoRightColumn != null) PreviewPhotoRightColumn.Visibility = Visibility.Collapsed;
            // A folder cell is not the folder you are in. The location matches the sidebar.
            PreviewLocationText.Text = CurrentLocationText();
            ToolTipService.SetToolTip(PreviewLocationButton, item.Path == DriveRootPath ? CurrentLocationText() : item.Path);
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            if (item.DirectoryIcon != null && PreviewFolderImage != null)
            {
                PreviewFolderImage.Source = item.DirectoryIcon;
                PreviewFolderImage.Visibility = Visibility.Visible;
                if (PreviewFolderIcon != null) PreviewFolderIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (PreviewFolderImage != null)
                {
                    PreviewFolderImage.Source = null;
                    PreviewFolderImage.Visibility = Visibility.Collapsed;
                }
                if (PreviewFolderIcon != null)
                {
                    PreviewFolderIcon.Glyph = "\uE8B7";
                    PreviewFolderIcon.Visibility = Visibility.Visible;
                }
            }
        }

        private static bool PreviewThumbnailReady(ImageSource? source)
        {
            if (source is BitmapImage bmp) return bmp.PixelWidth > 0 && bmp.PixelHeight > 0;
            return source is SoftwareBitmapSource;
        }

        private static string FileTypeLabel(string name)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 1) ext = ext.Substring(1).ToUpperInvariant();
            if (ext == "JPEG") ext = "JPG";
            if (ext == "CBZ") return "Comic Book ZIP (.cbz)";
            if (ext == "CBR") return "Comic Book RAR (.cbr)";
            return string.IsNullOrEmpty(ext) ? "File" : ext + " File";
        }

        private void CancelPreviewLoad()
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }

        private async Task LoadListPreviewAsync(ImageItem item, int generation, CancellationToken token)
        {
            try
            {
                var sizeTask = ReadPreviewSizeAsync(item.Path, token);
                var takenTask = ReadDateTakenAsync(item.Path, token);
                ImageSource? thumb = await ReadShellThumbnailAsync(item.Path, token);
                if (thumb == null && !token.IsCancellationRequested)
                    thumb = await ReadScaledPreviewAsync(item.Path, token);
                Size size = await sizeTask;
                DateTime? taken = await takenTask;
                if (generation != _previewGeneration || token.IsCancellationRequested) return;

                void Apply()
                {
                    if (generation != _previewGeneration || token.IsCancellationRequested) return;
                    if (thumb == null && PreviewThumbnailReady(item.Thumbnail))
                        thumb = item.Thumbnail;
                    if (thumb != null)
                    {
                    if (item.Thumbnail == null) item.Thumbnail = thumb;
                        PreviewImage.Source = thumb;
                        PreviewImage.Visibility = Visibility.Visible;
                        PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    }
                    if (size.Width >= 1 && size.Height >= 1)
                        PreviewDimensionsText.Text = $"{(int)size.Width} \u00D7 {(int)size.Height}";
                    PreviewTakenText.Text = taken.HasValue ? taken.Value.ToString("yyyy-MM-dd HH:mm") : "\u2014";
                }

                if (DispatcherQueue.HasThreadAccess) Apply();
                else DispatcherQueue.TryEnqueue(Apply);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private static async Task<Size> ReadPreviewSizeAsync(string path, CancellationToken token)
        {
            try
            {
                using var stream = await ViewerEngine.OpenImageStreamAsync(path, token);
                if (stream == null) return new Size(0, 0);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                return new Size(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Size(0, 0); }
        }

        private static async Task<DateTime?> ReadDateTakenAsync(string path, CancellationToken token)
        {
            try
            {
                if (ComicArchive.IsEntryPath(path) || ComicArchive.IsComicArchivePath(path))
                    return null;
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                var props = await file.Properties.RetrievePropertiesAsync(new[] { "System.Photo.DateTaken" }).AsTask(token);
                if (props.TryGetValue("System.Photo.DateTaken", out object? value))
                {
                    if (value is DateTimeOffset dto) return dto.LocalDateTime;
                    if (value is DateTime dt) return dt;
                }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<ImageSource?> ReadShellThumbnailAsync(string path, CancellationToken token)
        {
            try
            {
                if (ComicArchive.IsEntryPath(path) || ComicArchive.IsComicArchivePath(path))
                    return null;
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var shell = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.PicturesView, 640, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale).AsTask(token);
                if (shell == null) return null;
                var cloned = shell.CloneStream();
                return await SetBitmapSourceAsync(cloned, token);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<ImageSource?> ReadScaledPreviewAsync(string path, CancellationToken token)
        {
            const uint longest = 640;
            SoftwareBitmap? softwareBitmap = null;
            try
            {
                using var stream = await ViewerEngine.OpenImageStreamAsync(path, token);
                if (stream == null) return null;
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                uint w = decoder.PixelWidth;
                uint h = decoder.PixelHeight;
                if (w < 1 || h < 1) return null;
                double ratio = Math.Min(longest / (double)w, longest / (double)h);
                if (ratio > 1) ratio = 1;
                softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, Math.Round(w * ratio)),
                        ScaledHeight = (uint)Math.Max(1, Math.Round(h * ratio)),
                        InterpolationMode = BitmapInterpolationMode.Fant
                    },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask(token);

                var done = new TaskCompletionSource<ImageSource?>();
                var bitmap = softwareBitmap;
                softwareBitmap = null;
                bool enqueued = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (token.IsCancellationRequested)
                        {
                            bitmap.Dispose();
                            done.TrySetCanceled(token);
                            return;
                        }
                        var source = new SoftwareBitmapSource();
                        var op = source.SetBitmapAsync(bitmap);
                        op.Completed = (_, status) =>
                        {
                            // Never closed: doing so crashed the app inside WinUI. It goes with the source.
                            if (status == Windows.Foundation.AsyncStatus.Completed) done.TrySetResult(source);
                            else done.TrySetResult(null);
                        };
                    }
                    catch
                    {
                        try { bitmap.Dispose(); } catch { }
                        done.TrySetResult(null);
                    }
                });
                if (!enqueued)
                {
                    bitmap.Dispose();
                    return null;
                }
                return await done.Task;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            finally
            {
                softwareBitmap?.Dispose();
            }
        }

        private Task<ImageSource?> SetBitmapSourceAsync(IRandomAccessStream stream, CancellationToken token)
        {
            var done = new TaskCompletionSource<ImageSource?>();
            bool enqueued = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        stream.Dispose();
                        done.TrySetCanceled(token);
                        return;
                    }
                    var bmp = new BitmapImage();
                    var op = bmp.SetSourceAsync(stream);
                    op.Completed = (_, status) =>
                    {
                        try { stream.Dispose(); } catch { }
                        if (status == Windows.Foundation.AsyncStatus.Completed && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                            done.TrySetResult(bmp);
                        else done.TrySetResult(null);
                    };
                }
                catch
                {
                    try { stream.Dispose(); } catch { }
                    done.TrySetResult(null);
                }
            });
            if (!enqueued)
            {
                stream.Dispose();
                done.TrySetResult(null);
            }
            return done.Task;
        }

        private int TileColumnCount()
        {
            double itemWidth = _imageWrapGrid?.ItemWidth ?? 164;
            if (itemWidth < 1) itemWidth = 164;
            double width = _imageWrapGrid?.ActualWidth ?? 0;
            if (width < 1 && ImageGrid != null)
                width = Math.Max(0, ImageGrid.ActualWidth - ImageGrid.Padding.Left - ImageGrid.Padding.Right);
            if (width < 1) return 1;
            return Math.Max(1, (int)(width / itemWidth));
        }

        private int VisibleTileRows()
        {
            double itemHeight = _imageWrapGrid?.ItemHeight ?? 164;
            if (itemHeight < 1) itemHeight = 164;
            var sv = ImageGrid != null ? FindScrollViewer(ImageGrid) : null;
            double viewport = sv?.ViewportHeight ?? 0;
            if (viewport < 1 && ImageGrid != null) viewport = ImageGrid.ActualHeight;
            if (viewport < 1) return 1;
            return Math.Max(1, (int)(viewport / itemHeight));
        }

        private int TopLeftVisibleTile()
        {
            var scroll = ImageGrid != null ? FindScrollViewer(ImageGrid) : null;
            return ViewerMath.VisibleTileOrigin(
                scroll?.VerticalOffset ?? 0,
                ImageGrid?.Padding.Top ?? 0,
                _imageWrapGrid?.ItemHeight ?? 164,
                TileColumnCount(),
                DisplayCount());
        }

        private void RevealTile(int index)
        {
            if (ImageGrid == null || index < 0 || index >= DisplayCount()) return;
            ImageItem item = _listMode ? _listEntries[index] : Images[index];
            var sv = GetGridScrollViewer();
            if (sv == null)
            {
                ImageGrid.ScrollIntoView(item);
                return;
            }

            int cols = TileColumnCount();
            double itemHeight = _imageWrapGrid?.ItemHeight ?? 164;
            if (itemHeight < 1) itemHeight = 164;
            double itemStart = ImageGrid.Padding.Top + (index / cols) * itemHeight;
            double next = ViewerMath.ScrollOffsetToReveal(sv.VerticalOffset, sv.ViewportHeight, itemStart, itemHeight);
            if (Math.Abs(next - sv.VerticalOffset) > 0.5)
                sv.ChangeView(null, next, null, true);
        }

        private void MoveTileCursor(Windows.System.VirtualKey key)
        {
            int count = DisplayCount();
            if (count == 0 || _isScanning) return;
            if (!TryTileCursorKey(key, out var cursorKey)) return;

            int next = ViewerMath.MoveTileCursor(
                DisplayIndex(), count, TileColumnCount(), VisibleTileRows(), TopLeftVisibleTile(), cursorKey);
            if (next < 0) return;
            if (_listMode)
            {
                _listCursor = next;
                var item = _listEntries[next];
                if (!item.IsDirectory)
                {
                    int photo = Images.IndexOf(item);
                    if (photo >= 0) _currentIndex = photo;
                }
            }
            else _currentIndex = next;
            SyncTileCursor(scroll: true);
        }

        private void ImageGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var selected = Images.Where(img => img.IsGridSelected && !img.IsDirectory).ToList();
                if (selected.Count < 2)
                {
                    selected = ImageGrid.SelectedItems.OfType<ImageItem>().Where(img => !img.IsDirectory).ToList();
                }
                if (CanViewTogether(selected.Count))
                {
                    OpenMultiView(selected);
                    e.Handled = true;
                }
            }
        }

        private void OpenCursorTile()
        {
            if (_isScanning) return;
            var selected = Images.Where(img => img.IsGridSelected && !img.IsDirectory).ToList();
            if (selected.Count < 2)
            {
                selected = ImageGrid.SelectedItems.OfType<ImageItem>().Where(img => !img.IsDirectory).ToList();
            }
            if (CanViewTogether(selected.Count))
            {
                OpenMultiView(selected);
                return;
            }
            if (_listMode)
            {
                if (_listEntries.Count == 0) return;
                if (_listCursor < 0 || _listCursor >= _listEntries.Count)
                {
                    int origin = TopLeftVisibleTile();
                    if (origin < 0 || origin >= _listEntries.Count) return;
                    _listCursor = origin;
                }
                var item = _listEntries[_listCursor];
                if (item.IsDirectory)
                {
                    if (item.Path == DriveRootPath)
                        ShowDriveList();
                    else if (!string.IsNullOrEmpty(item.Path) && Directory.Exists(item.Path))
                        _ = ScanFolder(item.Path);
                    return;
                }
                int photo = Images.IndexOf(item);
                if (photo >= 0) LoadFullImage(photo);
                return;
            }
            if (Images.Count == 0) return;
            if (_currentIndex < 0 || _currentIndex >= Images.Count)
            {
                int origin = TopLeftVisibleTile();
                if (origin < 0) return;
                _currentIndex = origin;
            }
            LoadFullImage(_currentIndex);
        }

        private static bool TryTileCursorKey(Windows.System.VirtualKey key, out ViewerMath.TileCursorKey cursorKey)
        {
            switch (key)
            {
                case Windows.System.VirtualKey.Left: cursorKey = ViewerMath.TileCursorKey.Left; return true;
                case Windows.System.VirtualKey.Right: cursorKey = ViewerMath.TileCursorKey.Right; return true;
                case Windows.System.VirtualKey.Up: cursorKey = ViewerMath.TileCursorKey.Up; return true;
                case Windows.System.VirtualKey.Down: cursorKey = ViewerMath.TileCursorKey.Down; return true;
                case Windows.System.VirtualKey.PageUp: cursorKey = ViewerMath.TileCursorKey.PageUp; return true;
                case Windows.System.VirtualKey.PageDown: cursorKey = ViewerMath.TileCursorKey.PageDown; return true;
                case Windows.System.VirtualKey.Home: cursorKey = ViewerMath.TileCursorKey.Home; return true;
                case Windows.System.VirtualKey.End: cursorKey = ViewerMath.TileCursorKey.End; return true;
                default:
                    cursorKey = default;
                    return false;
            }
        }

        private bool TileKeyFocusStays()
        {
            if (_windowClosed) return false;
            var xamlRoot = this.Content?.XamlRoot;
            if (xamlRoot == null) return false;
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject node)
                return false;
            if (node is TextBox or PasswordBox or RichEditBox or Slider or ComboBox or ComboBoxItem or MenuFlyoutItem)
                return true;
            while (node != null)
            {
                if (node is ContentDialog || node.GetType().Name == "MenuFlyoutPresenter")
                    return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private bool FocusIsInside(DependencyObject? root)
        {
            if (root == null) return false;
            var xamlRoot = this.Content?.XamlRoot;
            if (xamlRoot == null) return false;
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject node)
                return false;
            while (node != null)
            {
                if (ReferenceEquals(node, root)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (TileKeyFocusStays()) return;

            if (MultiViewControl != null && MultiViewControl.Visibility == Visibility.Visible)
            {
                if (MultiViewControl.HandleKeyDown(e.Key))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    ClosePreviewInternal();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Tab)
            {
                ToggleSidebar();
                e.Handled = true;
                return;
            }

            bool isCursorKey = e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space || TryTileCursorKey(e.Key, out _);
            if (!isCursorKey) return;

            bool sidebarOpen = SidebarView != null && SidebarView.IsPaneOpen;
            if (sidebarOpen && FocusIsInside(SidebarPane)) return;

            if (sidebarOpen && e.Key != Windows.System.VirtualKey.Enter)
            {
                FocusSidebarTree();
                e.Handled = true;
                return;
            }

            if (FocusIsInside(TileHeader) || FocusIsInside(ListPreviewBar))
            {
                RootGrid.Focus(FocusState.Programmatic);
            }

            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var selected = Images.Where(img => img.IsGridSelected && !img.IsDirectory).ToList();
                if (selected.Count < 2)
                {
                    selected = ImageGrid.SelectedItems.OfType<ImageItem>().Where(img => !img.IsDirectory).ToList();
                }
                if (CanViewTogether(selected.Count))
                {
                    OpenMultiView(selected);
                    e.Handled = true;
                    return;
                }
                OpenCursorTile();
                e.Handled = true;
                return;
            }
            else if (e.Key == Windows.System.VirtualKey.Space)
            {
                var item = CursorItem();
                if (item != null && !item.IsDirectory) ToggleGridItemSelection(item);
            }
            else MoveTileCursor(e.Key);
            e.Handled = true;
        }

        private void Global_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Menu)
                e.Handled = true;
        }

        private void Global_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // This handler also receives keys PreviewKeyDown already handled.
            // A second pass would close the grid after Esc has returned from solo.
            if (e.Handled) return;

            if (e.Key == Windows.System.VirtualKey.Menu)
            {
                // Handled so a bare Alt does not move focus to the window's system menu.
                // Alt+key combinations are unaffected: the system tracks the modifier itself.
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            bool isCtrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isCtrlDown && e.Key == Windows.System.VirtualKey.B)
            {
                ToggleSidebar();
                e.Handled = true;
                return;
            }
            if (isCtrlDown && (e.Key == Windows.System.VirtualKey.Number1 || e.Key == Windows.System.VirtualKey.Number2 || e.Key == Windows.System.VirtualKey.Number3))
            {
                int slot = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
                ApplyPresetSlot(slot);
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Delete || e.Key == Windows.System.VirtualKey.F2 || e.Key == Windows.System.VirtualKey.F5 || e.Key == Windows.System.VirtualKey.Escape || e.Key == Windows.System.VirtualKey.Back)
            {
                var focusedElement = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
                if (focusedElement is TextBox || focusedElement is PasswordBox || focusedElement is RichEditBox)
                    return;
            }

            if (MultiViewControl != null && MultiViewControl.Visibility == Visibility.Visible)
            {
                if (MultiViewControl.HandleKeyDown(e.Key))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                if (_isStartupIndexing)
                {
                    if (e.Key == Windows.System.VirtualKey.Right || e.Key == Windows.System.VirtualKey.Left ||
                        e.Key == Windows.System.VirtualKey.Home || e.Key == Windows.System.VirtualKey.End ||
                        e.Key == Windows.System.VirtualKey.Delete)
                    {
                        e.Handled = true;
                    }
                    return;
                }

                if (ViewerControl.HandleAnimationKeystroke(e.Key))
                {
                    e.Handled = true;
                    return;
                }

                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Right: Navigate(1); break;
                    case Windows.System.VirtualKey.Left: Navigate(-1); break;
                    case Windows.System.VirtualKey.Home: Navigate(int.MinValue); break;
                    case Windows.System.VirtualKey.End: Navigate(int.MaxValue); break;
                    case Windows.System.VirtualKey.B:
                        if (_currentIndex >= 0 && _currentIndex < Images.Count && Images[_currentIndex].IsComicEntry)
                        {
                            ViewerControl.SetBookSpreadMode(!ViewerControl.BookSpreadMode, notify: true);
                        }
                        break;
                    case Windows.System.VirtualKey.Escape: ClosePreviewInternal(); break;
                    case Windows.System.VirtualKey.Delete: ViewerControl_DeleteRequested(this, EventArgs.Empty); break;
                }
            }
            else if (!TileKeyFocusStays())
            {
                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Enter:
                        if (SidebarView != null && SidebarView.IsPaneOpen && FocusIsInside(SidebarPane))
                        {
                            break;
                        }
                        {
                            var selectedNonDirs = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
                            if (CanViewTogether(selectedNonDirs.Count))
                            {
                                OpenMultiView(selectedNonDirs);
                                e.Handled = true;
                            }
                            else
                            {
                                OpenCursorTile();
                                e.Handled = true;
                            }
                        }
                        break;
                    case Windows.System.VirtualKey.F5:
                        RefreshFolder_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.Escape:
                        if (SidebarView != null && SidebarView.IsPaneOpen)
                        {
                            SidebarView.IsPaneOpen = false;
                            FocusGrid();
                            e.Handled = true;
                            break;
                        }
                        if (_multiViewParked)
                        {
                            ResumeParkedMultiView();
                            e.Handled = true;
                            break;
                        }
                        if (ImageGrid != null && ImageGrid.SelectedItems.Count > 0)
                        {
                            ImageGrid.SelectedItems.Clear();
                            e.Handled = true;
                        }
                        break;
                    case Windows.System.VirtualKey.Back:
                        GoUp_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.F2:
                        {
                            var item = CursorItem();
                            if (item != null && !item.IsDirectory)
                            {
                                _ = RenameGridItemAsync(item);
                                e.Handled = true;
                            }
                        }
                        break;
                    case Windows.System.VirtualKey.Delete:
                        if (ImageGrid != null && ImageGrid.SelectedItems.Count > 0)
                        {
                            MultiSelectDelete_Click(this, new RoutedEventArgs());
                            e.Handled = true;
                        }
                        else
                        {
                            var item = CursorItem();
                            if (item != null && !item.IsDirectory)
                            {
                                _ = DeleteGridItemAsync(item);
                                e.Handled = true;
                            }
                        }
                        break;
                }
            }
        }

        private void ToggleFullscreen()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appW = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

            if (appW != null)
            {
                if (_isFullscreen)
                {
                    appW.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
                    this.ExtendsContentIntoTitleBar = true;
                }
                else
                {
                    this.ExtendsContentIntoTitleBar = false;
                    appW.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                }
            }
            _isFullscreen = !_isFullscreen;
            SyncTileChrome();
        }

        // The header is the tile screen's title bar. A photo covers it, and the old drag strip comes back for that window.
        private void SyncTileChrome()
        {
            bool grid = !_isFullscreen && (ViewerControl == null || ViewerControl.Visibility != Visibility.Visible);
            if (TileHeader != null)
                TileHeader.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
            if (DragZone != null)
            {
                DragZone.Visibility = (!_isFullscreen && !grid) ? Visibility.Visible : Visibility.Collapsed;
                if (grid) DragZone.Opacity = 0;
            }
            if (_isFullscreen) return;
            UIElement? bar = grid ? ((UIElement?)HeaderDragRegion ?? TileHeader) : DragZone;
            if (bar != null) this.SetTitleBar(bar);
        }

        public void ToggleSidebar()
        {
            if (SidebarView == null) return;
            if (SidebarView.IsPaneOpen)
            {
                SidebarView.IsPaneOpen = false;
                FocusGrid();
            }
            else
            {
                SidebarView.IsPaneOpen = true;
                FocusSidebarTree();
            }
        }

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleSidebar();
        }

        private void UpdateHeaderFolderInfo()
        {
            if (HeaderFolderText == null || HeaderCountText == null) return;
            if (AtDriveRoot)
            {
                if (HeaderFolderIcon != null) HeaderFolderIcon.Glyph = "\uE8B7";
                HeaderFolderText.Text = DriveRootName;
                int drives = _childFolders.Count;
                HeaderCountText.Text = drives == 1 ? "1 drive" : $"{drives} drives";
                if (HeaderPathFlyoutText != null) HeaderPathFlyoutText.Text = DriveRootName;
                UpdateEmptyFolderState();
                return;
            }
            bool inArchive = ComicArchive.IsComicArchivePath(_currentDirectory);
            if (HeaderFolderIcon != null)
                HeaderFolderIcon.Glyph = inArchive ? "\uE736" : "\uE8B7";
            string name = inArchive
                ? Path.GetFileName(_currentDirectory)
                : (_breadcrumbs.Count > 0 ? _breadcrumbs[^1] : CurrentLocationText());
            if (string.IsNullOrEmpty(name)) name = "Pictures";
            HeaderFolderText.Text = name;
            int photos = Images.Count;
            HeaderCountText.Text = inArchive
                ? (photos == 1 ? "1 page" : $"{photos} pages")
                : (photos == 1 ? "1 photo" : $"{photos} photos");
            if (HeaderPathFlyoutText != null) HeaderPathFlyoutText.Text = CurrentLocationText();
            UpdateEmptyFolderState();
        }

        private void UpdateEmptyFolderState()
        {
            if (EmptyFolderOverlay == null) return;
            bool showEmpty = !_listMode && !_isScanning && Images.Count == 0;
            EmptyFolderOverlay.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
            if (!showEmpty) return;

            if (AtDriveRoot)
            {
                if (EmptyFolderTitle != null) EmptyFolderTitle.Text = "Select a drive in List Mode or Sidebar";
                if (EmptyFolderSubfoldersText != null) EmptyFolderSubfoldersText.Text = $"View {_childFolders.Count} Drive(s) in List Mode";
                if (EmptyFolderSubfoldersButton != null) EmptyFolderSubfoldersButton.Visibility = Visibility.Visible;
            }
            else
            {
                if (EmptyFolderTitle != null) EmptyFolderTitle.Text = "No photos in this folder";
                if (EmptyFolderSubfoldersButton != null)
                {
                    if (_childFolders.Count > 0)
                    {
                        if (EmptyFolderSubfoldersText != null)
                            EmptyFolderSubfoldersText.Text = _childFolders.Count == 1 ? "View 1 Subfolder in List Mode" : $"View {_childFolders.Count} Subfolders in List Mode";
                        EmptyFolderSubfoldersButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        EmptyFolderSubfoldersButton.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _headerPathShowTimer;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _headerPathHideTimer;
        private bool _pointerInHeaderFolderButton;
        private bool _pointerInHeaderPathFlyout;

        private void HeaderFolderButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _pointerInHeaderFolderButton = true;
            _headerPathHideTimer?.Stop();
            if (HeaderPathFlyout != null && HeaderPathFlyout.IsOpen) return;
            if (_headerPathShowTimer == null)
            {
                _headerPathShowTimer = DispatcherQueue.CreateTimer();
                _headerPathShowTimer.Interval = TimeSpan.FromMilliseconds(220);
                _headerPathShowTimer.IsRepeating = false;
                _headerPathShowTimer.Tick += (_, _) =>
                {
                    _headerPathShowTimer.Stop();
                    if (_pointerInHeaderFolderButton && HeaderFolderButton != null && HeaderPathFlyout != null && !HeaderPathFlyout.IsOpen)
                    {
                        if (HeaderPathFlyoutText != null) HeaderPathFlyoutText.Text = CurrentLocationText();
                        FlyoutBase.ShowAttachedFlyout(HeaderFolderButton);
                    }
                };
            }
            _headerPathShowTimer.Stop();
            _headerPathShowTimer.Start();
        }

        private void HeaderFolderButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerInHeaderFolderButton = false;
            _headerPathShowTimer?.Stop();
            ScheduleHeaderPathFlyoutHide();
        }

        private void HeaderPathFlyout_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _pointerInHeaderPathFlyout = true;
            _headerPathHideTimer?.Stop();
        }

        private void HeaderPathFlyout_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerInHeaderPathFlyout = false;
            ScheduleHeaderPathFlyoutHide();
        }

        private void ScheduleHeaderPathFlyoutHide()
        {
            if (_headerPathHideTimer == null)
            {
                _headerPathHideTimer = DispatcherQueue.CreateTimer();
                _headerPathHideTimer.Interval = TimeSpan.FromMilliseconds(240);
                _headerPathHideTimer.IsRepeating = false;
                _headerPathHideTimer.Tick += (_, _) =>
                {
                    _headerPathHideTimer.Stop();
                    if (!_pointerInHeaderFolderButton && !_pointerInHeaderPathFlyout && HeaderPathFlyout != null && HeaderPathFlyout.IsOpen)
                        HeaderPathFlyout.Hide();
                };
            }
            _headerPathHideTimer.Stop();
            _headerPathHideTimer.Start();
        }

        private void OpenCurrentDirectoryInExplorer_Click(object sender, RoutedEventArgs e)
        {
            HeaderPathFlyout?.Hide();
            try
            {
                if (AtDriveRoot)
                {
                    Process.Start("explorer.exe", "shell:MyComputerFolder");
                }
                else if (!string.IsNullOrEmpty(_currentDirectory) && ComicArchive.IsComicArchivePath(_currentDirectory) && File.Exists(_currentDirectory))
                {
                    Process.Start("explorer.exe", $"/select,\"{_currentDirectory}\"");
                }
                else if (!string.IsNullOrEmpty(_currentDirectory) && Directory.Exists(_currentDirectory))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _currentDirectory,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenCurrentDirectory] {ex.Message}");
            }
            FocusGrid();
        }

        private void ApplyCaptionInset()
        {
            if (CaptionInset == null) return;
            double scale = 1;
            if (Content?.XamlRoot != null && Content.XamlRoot.RasterizationScale > 0)
                scale = Content.XamlRoot.RasterizationScale;
            double inset = _appWindow.TitleBar.RightInset / scale;
            if (inset > 1) CaptionInset.Width = new GridLength(inset);
        }

        private bool _listMode;
        private bool _applyingViewMode;
        private bool _listEntriesValid;
        private ScrollViewer? _gridScrollViewer;
        private ScrollViewer? GetGridScrollViewer()
        {
            if (_gridScrollViewer != null) return _gridScrollViewer;
            if (ImageGrid != null) _gridScrollViewer = FindScrollViewer(ImageGrid);
            return _gridScrollViewer;
        }
        private const double ListRowHeight = 26;
        private const double ListColumnWidth = 200;
        // Same wrap as the photos. Folders are only the first cells, and only while the list is showing.
        private readonly ObservableRangeCollection<ImageItem> _listEntries = [];
        private List<FolderItem> _childFolders = [];
        private int _listCursor = -1;
        // Set only when the folder being opened is the parent of the one we are leaving.
        private string? _listReturnPath;
        private readonly Dictionary<string, ImageSource> _folderIcons = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _folderIconGate = new(4);
        private BitmapImage? _parentUpIcon;
        private Task<BitmapImage?>? _parentUpIconTask;

        private void TilesMode_Click(object sender, RoutedEventArgs e)
        {
            if (_multiViewCanvasActive)
            {
                CloseMultiView();
            }
            if (_listMode)
            {
                _listMode = false;
                _currentSettings.ListMode = false;
                ApplyViewMode();
                SaveAllSettings();
            }
            else
            {
                SyncViewModeButtons();
            }
        }

        private void ListMode_Click(object sender, RoutedEventArgs e)
        {
            if (_multiViewCanvasActive)
            {
                CloseMultiView();
            }
            if (!_listMode)
            {
                _listMode = true;
                _currentSettings.ListMode = true;
                ApplyViewMode();
                SaveAllSettings();
            }
            else
            {
                SyncViewModeButtons();
            }
        }

        private void MultiMode_Click(object sender, RoutedEventArgs e)
        {
            if (_multiViewCanvasActive) return;

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                ClosePreviewInternal();
            }

            if (_multiViewParked)
            {
                ApplyParkedPhotos();
                return;
            }

            if (_multiViewKept)
            {
                ResumeKeptMultiView();
                return;
            }

            var selected = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
            if (selected.Count > 0)
            {
                OpenMultiView(selected);
                return;
            }

            var candidates = Images.Where(i => !i.IsDirectory).ToList();
            if (candidates.Count > 0)
            {
                int start = (_currentIndex >= 0 && _currentIndex < candidates.Count) ? _currentIndex : 0;
                var slice = candidates.Skip(start).Take(4).ToList();
                if (slice.Count < 2 && candidates.Count >= 2)
                    slice = candidates.Take(4).ToList();
                OpenMultiView(slice);
            }
            else
            {
                OpenMultiView(Array.Empty<ImageItem>());
            }
        }

        private static Brush ResolveThemeBrush(string key, Windows.UI.Color color)
        {
            return new SolidColorBrush(color);
        }

        private void SyncViewModeButtons()
        {
            if (TilesModeButton == null || ListModeButton == null || MultiModeButton == null) return;
            bool isDark = RootGrid?.ActualTheme == ElementTheme.Dark;
            var activeBg = ResolveThemeBrush("CardBackgroundFillColorDefaultBrush", isDark ? Windows.UI.Color.FromArgb(18, 255, 255, 255) : Windows.UI.Color.FromArgb(179, 255, 255, 255));
            var activeFg = ResolveThemeBrush("AccentTextFillColorPrimaryBrush", isDark ? Windows.UI.Color.FromArgb(255, 96, 205, 255) : Windows.UI.Color.FromArgb(255, 0, 95, 184));
            var inactiveFg = ResolveThemeBrush("TextFillColorSecondaryBrush", isDark ? Windows.UI.Color.FromArgb(197, 255, 255, 255) : Windows.UI.Color.FromArgb(158, 0, 0, 0));
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            bool isMulti = _multiViewCanvasActive;
            bool isList = !_multiViewCanvasActive && _listMode;
            bool isTiles = !_multiViewCanvasActive && !_listMode;

            TilesModeButton.Background = isTiles ? activeBg : transparent;
            ListModeButton.Background = isList ? activeBg : transparent;
            MultiModeButton.Background = isMulti ? activeBg : transparent;

            if (TilesModeIcon != null) TilesModeIcon.Foreground = isTiles ? activeFg : inactiveFg;
            if (ListModeIcon != null) ListModeIcon.Foreground = isList ? activeFg : inactiveFg;
            if (MultiModeIcon != null) MultiModeIcon.Foreground = isMulti ? activeFg : inactiveFg;
        }

        private void ApplyViewMode()
        {
            if (ImageGrid == null || _applyingViewMode) return;
            _applyingViewMode = true;
            try
            {
                ImageItem.ListRows = _listMode;
                SyncViewModeButtons();
                if (_listMode)
                    ImageGrid.Padding = new Thickness(0, 4, 0, 0);
                else
                    ImageGrid.Padding = new Thickness(10, 8, 10, 12);
                if (ListPreviewBar != null)
                    ListPreviewBar.Visibility = _listMode ? Visibility.Visible : Visibility.Collapsed;



                if (_listMode)
                {
                    if (!_listEntriesValid || _listEntries.Count == 0)
                        RebuildListEntries();
                    if (!ReferenceEquals(ImageGrid.ItemsSource, _listEntries))
                        ImageGrid.ItemsSource = _listEntries;
                    if (Images.Count > 0 && (_currentIndex < 0 || _currentIndex >= Images.Count))
                        _currentIndex = 0;
                    PlaceListCursorOnCurrentImage();
                }
                else
                {
                    if (_listCursor >= 0 && _listCursor < _listEntries.Count && !_listEntries[_listCursor].IsDirectory)
                    {
                        int prefix = _listEntries.Count - Images.Count;
                        int photo = _listCursor - prefix;
                        if (photo >= 0 && photo < Images.Count && ReferenceEquals(Images[photo], _listEntries[_listCursor]))
                            _currentIndex = photo;
                        else
                        {
                            int found = Images.IndexOf(_listEntries[_listCursor]);
                            if (found >= 0) _currentIndex = found;
                        }
                    }
                    if (!ReferenceEquals(ImageGrid.ItemsSource, Images))
                        ImageGrid.ItemsSource = Images;
                }
                ApplyItemSize();
                UpdateEmptyFolderState();
                SyncTileCursor(scroll: false);
                KeepCursorInView();
                // The click leaves focus on the header button, which then swallows the arrow keys.
                // The wrap grid also resets its scroll offset while the new cell size is applied.
                int index = _currentIndex;
                ImageGrid.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (index != _currentIndex) return;
                    SyncTileCursor(scroll: false);
                    KeepCursorInView();
                    RootGrid.Focus(FocusState.Programmatic);
                });
            }
            finally
            {
                _applyingViewMode = false;
            }
        }

        // The refresh button sits in the header. A focused header button swallows arrow keys.
        private void RestoreGridKeyFocus(string? keepPath)
        {
            if (_isScanning) return;
            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible) return;
            bool found = false;
            if (!string.IsNullOrEmpty(keepPath) && _listMode)
            {
                for (int i = 0; i < _listEntries.Count; i++)
                {
                    if (!string.Equals(_listEntries[i].Path, keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                    _listCursor = i;
                    if (!_listEntries[i].IsDirectory)
                        _currentIndex = Images.IndexOf(_listEntries[i]);
                    found = true;
                    break;
                }
            }
            if (!found && !string.IsNullOrEmpty(keepPath))
            {
                for (int i = 0; i < Images.Count; i++)
                {
                    if (string.Equals(Images[i].Path, keepPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentIndex = i;
                        if (_listMode) PlaceListCursorOnCurrentImage();
                        break;
                    }
                }
            }
            if (Images.Count > 0 && (_currentIndex < 0 || _currentIndex >= Images.Count))
            {
                if (_listMode) _currentIndex = 0;
                else
                {
                    int origin = TopLeftVisibleTile();
                    _currentIndex = origin >= 0 && origin < Images.Count ? origin : 0;
                }
                if (_listMode) PlaceListCursorOnCurrentImage();
            }
            else if (_listMode && (_listCursor < 0 || _listCursor >= _listEntries.Count))
                PlaceListCursorOnCurrentImage();
            SyncTileCursor(scroll: false);
            KeepCursorInView();
            int index = _currentIndex;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (index != _currentIndex) return;
                if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible) return;
                RootGrid.Focus(FocusState.Programmatic);
            });
        }

        private void KeepCursorInView()
        {
            int index = DisplayIndex();
            if (index < 0 || index >= DisplayCount()) return;
            RevealTile(index);
            if (_listMode) PinListToLeftEdge();
        }

        private void ApplyItemSize()
        {
            if (_imageWrapGrid == null) return;
            if (_listMode)
            {
                double width = ListViewportWidth();
                if (width > 1)
                {
                    int columns = Math.Max(2, (int)(width / ListColumnWidth));
                    double itemWidth = Math.Floor(width / columns);
                    if (Math.Abs(_imageWrapGrid.ItemWidth - itemWidth) > 1)
                        _imageWrapGrid.ItemWidth = itemWidth;
                }
                _imageWrapGrid.ItemHeight = ListRowHeight;
            }
            else if (ThumbSizeSlider != null)
            {
                _imageWrapGrid.ItemWidth = _imageWrapGrid.ItemHeight = ThumbSizeSlider.Value;
            }
        }

        private double ListViewportWidth()
        {
            double pad = ImageGrid != null ? (ImageGrid.Padding.Left + ImageGrid.Padding.Right) : 0;
            var sv = GetGridScrollViewer();
            if (sv != null && sv.ViewportWidth > 1) return Math.Max(0, sv.ViewportWidth - pad);
            if (_imageWrapGrid != null && _imageWrapGrid.ActualWidth > 1) return _imageWrapGrid.ActualWidth;
            if (ImageGrid == null) return 0;
            return Math.Max(0, ImageGrid.ActualWidth - pad);
        }

        // The first list layout can leave the grid scrolled sideways, so the left column is cut off until a photo is opened.
        private void PinListToLeftEdge()
        {
            var sv = GetGridScrollViewer();
            if (sv == null || sv.HorizontalOffset < 0.5) return;
            sv.ChangeView(0, null, null, true);
        }

        private void ImageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_listMode)
            {
                ApplyItemSize();
                PinListToLeftEdge();
            }
            ApplyCaptionInset();
        }

        private bool _presetSaveMode;

        private void HeaderSettingsFlyout_Opening(object sender, object e)
        {
            _presetSaveMode = false;
            SyncPresetButtons();
            SyncThemeButtons();
        }

        private void ThemeMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is string mode)
                ApplyTheme(mode, save: true);
        }

        public ElementTheme CurrentRequestedTheme => RootGrid?.RequestedTheme ?? ElementTheme.Default;

        private void ApplyTheme(string? mode, bool save)
        {
            string normalized = mode switch
            {
                "Light" => "Light",
                "Dark" => "Dark",
                _ => "System"
            };
            _currentSettings.ThemeMode = normalized;

            if (RootGrid != null)
            {
                RootGrid.RequestedTheme = normalized switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }

            SyncTitleBarTheme();
            SyncViewModeButtons();
            SyncPresetButtons();
            SyncThemeButtons();

            if (save)
                SaveAllSettings();
        }

        private void SyncTitleBarTheme()
        {
            if (_appWindow == null || !Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;
            var titleBar = _appWindow.TitleBar;
            bool isDark = (RootGrid?.ActualTheme ?? ElementTheme.Default) == ElementTheme.Dark;

            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            if (isDark)
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(240, 255, 255, 255);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 255, 255, 255);
                titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(200, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(16, 255, 255, 255);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(110, 255, 255, 255);
            }
            else
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(228, 24, 24, 24);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(18, 0, 0, 0);
                titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(180, 24, 24, 24);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(12, 0, 0, 0);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(110, 24, 24, 24);
            }
        }

        private void SyncThemeButtons()
        {
            if (ThemeLightButton == null || ThemeDarkButton == null) return;
            bool isDark = RootGrid?.ActualTheme == ElementTheme.Dark;
            var activeBg = ResolveThemeBrush("CardBackgroundFillColorDefaultBrush", isDark ? Windows.UI.Color.FromArgb(18, 255, 255, 255) : Windows.UI.Color.FromArgb(179, 255, 255, 255));
            var activeFg = ResolveThemeBrush("AccentTextFillColorPrimaryBrush", isDark ? Windows.UI.Color.FromArgb(255, 96, 205, 255) : Windows.UI.Color.FromArgb(255, 0, 95, 184));
            var inactiveFg = ResolveThemeBrush("TextFillColorSecondaryBrush", isDark ? Windows.UI.Color.FromArgb(197, 255, 255, 255) : Windows.UI.Color.FromArgb(158, 0, 0, 0));
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            string mode = _currentSettings.ThemeMode ?? "System";
            bool lightSelected = mode == "Light" || (mode == "System" && !isDark);
            bool darkSelected = mode == "Dark" || (mode == "System" && isDark);
            bool systemSelected = mode != "Light" && mode != "Dark";

            ThemeLightButton.Background = lightSelected ? activeBg : transparent;
            if (ThemeLightIcon != null) ThemeLightIcon.Foreground = lightSelected ? activeFg : inactiveFg;
            if (ThemeLightText != null)
            {
                ThemeLightText.Foreground = lightSelected ? activeFg : inactiveFg;
                ThemeLightText.FontWeight = lightSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }

            ThemeDarkButton.Background = darkSelected ? activeBg : transparent;
            if (ThemeDarkIcon != null) ThemeDarkIcon.Foreground = darkSelected ? activeFg : inactiveFg;
            if (ThemeDarkText != null)
            {
                ThemeDarkText.Foreground = darkSelected ? activeFg : inactiveFg;
                ThemeDarkText.FontWeight = darkSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }

            if (ThemeSystemButton != null && ThemeSystemIcon != null)
            {
                ThemeSystemButton.Background = systemSelected ? activeBg : transparent;
                ThemeSystemIcon.Foreground = systemSelected ? activeFg : inactiveFg;
            }
        }


        private void PresetSaveMode_Click(object sender, RoutedEventArgs e)
        {
            _presetSaveMode = !_presetSaveMode;
            SyncPresetButtons();
        }

        private void PresetSlot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string tagStr || !int.TryParse(tagStr, out int slot))
                return;
            while (_currentSettings.WindowPresets.Count < 3)
                _currentSettings.WindowPresets.Add(null);

            bool isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (_presetSaveMode || isCtrl || _currentSettings.WindowPresets[slot] == null)
            {
                SavePresetSlot(slot);
            }
            else
            {
                HeaderSettingsFlyout?.Hide();
                ApplyPresetSlot(slot);
            }
        }

        private void PresetSlot_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string tagStr || !int.TryParse(tagStr, out int slot))
                return;
            SavePresetSlot(slot);
            e.Handled = true;
        }

        private void SavePresetSlot(int slot)
        {
            if (_appWindow == null || slot < 0 || slot >= 3) return;
            while (_currentSettings.WindowPresets.Count < 3)
                _currentSettings.WindowPresets.Add(null);

            double tileSize = ThumbSizeSlider != null ? Math.Round(ThumbSizeSlider.Value) : _currentSettings.TileSize;
            _currentSettings.WindowPresets[slot] = new WindowPreset
            {
                WindowWidth = _appWindow.Size.Width,
                WindowHeight = _appWindow.Size.Height,
                WindowX = _appWindow.Position.X,
                WindowY = _appWindow.Position.Y,
                TileSize = tileSize
            };
            _presetSaveMode = false;
            SaveAllSettings();
            SyncPresetButtons();
        }

        private void ApplyPresetSlot(int slot)
        {
            while (_currentSettings.WindowPresets.Count < 3)
                _currentSettings.WindowPresets.Add(null);
            if (slot < 0 || slot >= 3) return;
            var preset = _currentSettings.WindowPresets[slot];
            if (preset == null || preset.WindowWidth < 300 || preset.WindowHeight < 300) return;

            if (_isFullscreen)
                ToggleFullscreen();

            if (_appWindow != null)
            {
                if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p && p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                    p.Restore();

                int w = preset.WindowWidth;
                int h = preset.WindowHeight;
                int x = preset.WindowX;
                int y = preset.WindowY;

                try
                {
                    var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                    var work = displayArea.WorkArea;
                    w = Math.Min(w, work.Width);
                    h = Math.Min(h, work.Height);
                    if (x != -1 && y != -1)
                    {
                        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - w));
                        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
                    }
                }
                catch { }

                _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
                if (x != -1 && y != -1)
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }

            if (preset.TileSize >= 100 && preset.TileSize <= 400)
            {
                _currentSettings.TileSize = preset.TileSize;
                if (ThumbSizeSlider != null) ThumbSizeSlider.Value = preset.TileSize;
                if (!_listMode && _imageWrapGrid != null)
                    _imageWrapGrid.ItemWidth = _imageWrapGrid.ItemHeight = preset.TileSize;
            }

            SaveAllSettings();
            SyncPresetButtons();
            FocusGrid();
        }

        private void SyncPresetButtons()
        {
            if (Preset1Button == null || Preset2Button == null || Preset3Button == null) return;
            while (_currentSettings.WindowPresets.Count < 3)
                _currentSettings.WindowPresets.Add(null);

            bool isDark = RootGrid?.ActualTheme == ElementTheme.Dark;
            var activeBg = ResolveThemeBrush("CardBackgroundFillColorDefaultBrush", isDark ? Windows.UI.Color.FromArgb(18, 255, 255, 255) : Windows.UI.Color.FromArgb(179, 255, 255, 255));
            var activeFg = ResolveThemeBrush("AccentTextFillColorPrimaryBrush", isDark ? Windows.UI.Color.FromArgb(255, 96, 205, 255) : Windows.UI.Color.FromArgb(255, 0, 95, 184));
            var primaryFg = ResolveThemeBrush("TextFillColorPrimaryBrush", isDark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(228, 0, 0, 0));
            var secondaryFg = ResolveThemeBrush("TextFillColorSecondaryBrush", isDark ? Windows.UI.Color.FromArgb(197, 255, 255, 255) : Windows.UI.Color.FromArgb(158, 0, 0, 0));
            var tertiaryFg = ResolveThemeBrush("TextFillColorTertiaryBrush", isDark ? Windows.UI.Color.FromArgb(135, 255, 255, 255) : Windows.UI.Color.FromArgb(114, 0, 0, 0));

            if (PresetSaveModeButton != null && PresetSaveModeIcon != null)
            {
                PresetSaveModeButton.Background = _presetSaveMode ? activeBg : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                PresetSaveModeIcon.Foreground = _presetSaveMode ? activeFg : secondaryFg;
                ToolTipService.SetToolTip(PresetSaveModeButton, _presetSaveMode
                    ? "Select slot 1, 2, or 3 to save current window & tile size (Click again to cancel)"
                    : "Save Current Window & Tile Size to a Preset Slot");
            }

            Button[] buttons = [Preset1Button, Preset2Button, Preset3Button];
            TextBlock?[] texts = [Preset1Text, Preset2Text, Preset3Text];
            Microsoft.UI.Xaml.Shapes.Ellipse?[] dots = [Preset1Dot, Preset2Dot, Preset3Dot];

            int curW = _appWindow?.Size.Width ?? 0;
            int curH = _appWindow?.Size.Height ?? 0;
            double curTile = ThumbSizeSlider != null ? ThumbSizeSlider.Value : _currentSettings.TileSize;

            for (int i = 0; i < 3; i++)
            {
                var preset = _currentSettings.WindowPresets[i];
                bool hasPreset = preset != null && preset.WindowWidth >= 300 && preset.WindowHeight >= 300;
                bool isActive = hasPreset && !_presetSaveMode
                    && Math.Abs(curW - preset!.WindowWidth) <= 8
                    && Math.Abs(curH - preset.WindowHeight) <= 8
                    && Math.Abs(curTile - preset.TileSize) <= 2;

                var btn = buttons[i];
                var txt = texts[i];
                var dot = dots[i];

                if (_presetSaveMode)
                {
                    btn.Background = activeBg;
                    if (txt != null)
                    {
                        txt.Text = $"\U0001F4BE{i + 1}";
                        txt.Foreground = activeFg;
                        txt.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    }
                    if (dot != null) dot.Visibility = Visibility.Collapsed;
                    ToolTipService.SetToolTip(btn, hasPreset
                        ? $"Overwrite Preset {i + 1} (currently {preset!.WindowWidth}\u00D7{preset.WindowHeight}, Tile {(int)Math.Round(preset.TileSize)}px)"
                        : $"Save current window & tile size to Preset {i + 1}");
                }
                else
                {
                    btn.Background = isActive ? activeBg : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    if (txt != null)
                    {
                        txt.Text = $"{i + 1}";
                        txt.Foreground = isActive ? activeFg : (hasPreset ? primaryFg : tertiaryFg);
                        txt.FontWeight = hasPreset ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                    }
                    if (dot != null)
                    {
                        dot.Visibility = hasPreset ? Visibility.Visible : Visibility.Collapsed;
                        dot.Opacity = isActive ? 1.0 : 0.65;
                    }
                    ToolTipService.SetToolTip(btn, hasPreset
                        ? $"Preset {i + 1}: {preset!.WindowWidth}\u00D7{preset.WindowHeight}, Tile {(int)Math.Round(preset.TileSize)}px (Ctrl+{i + 1})\nClick to apply \u2022 Right-click or Save icon to overwrite"
                        : $"Preset {i + 1} (Empty)\nClick to save current window & tile size");
                }
            }
        }

        private void ResetWindow_Click(object s, RoutedEventArgs e)
        {
            HeaderSettingsFlyout?.Hide();
            ConfigureWindow(true);
            if (ThumbSizeSlider != null) ThumbSizeSlider.Value = 164;
            SaveAllSettings();
            FocusGrid();
        }

        private void OpenGridFolder(ImageItem folder)
        {
            if (folder.Path == DriveRootPath)
                ShowDriveList();
            else if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
                _ = ScanFolder(folder.Path);
        }

        private void ImageGrid_ItemClick(object s, ItemClickEventArgs e)
        {
            bool isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            
            if (isCtrl || isShift) return;

            var selected = Images.Where(img => img.IsGridSelected && !img.IsDirectory).ToList();
            if (selected.Count < 2)
            {
                selected = ImageGrid.SelectedItems.OfType<ImageItem>().Where(img => !img.IsDirectory).ToList();
            }
            if (CanViewTogether(selected.Count))
            {
                OpenMultiView(selected);
                return;
            }

            if (e.ClickedItem is ImageItem i)
            {
                if (i.IsDirectory)
                {
                    OpenGridFolder(i);
                }
                else
                {
                    int photo = Images.IndexOf(i);
                    if (photo >= 0) LoadFullImage(photo);
                }
            }

            // The grid selects whatever was clicked. A plain click only opens the photo,
            // and the grid applies that selection around this call, so clear it once the
            // click has finished or the border and the "1 selected" bar survive the viewer.
            ImageGrid.SelectedItems.Clear();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (ImageGrid.SelectedItems.Count > 0)
                    ImageGrid.SelectedItems.Clear();
            });
        }

        private void DetachImage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex >= 0 && _currentIndex < Images.Count)
            {
                LaunchDetachedWindow(Images[_currentIndex], null);
            }
        }

        private void ImageWrapGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _imageWrapGrid = sender as ItemsWrapGrid;
            if (!_applyingViewMode) ApplyViewMode();
        }

        private void ThumbSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_listMode && _imageWrapGrid != null)
                _imageWrapGrid.ItemWidth = _imageWrapGrid.ItemHeight = e.NewValue;
            if (_currentSettings != null)
                _currentSettings.TileSize = Math.Round(e.NewValue);
            if (HeaderSettingsFlyout != null && HeaderSettingsFlyout.IsOpen)
                SyncPresetButtons();
        }

        private void HoverZone_PointerEntered(object s, PointerRoutedEventArgs e)
        {
            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible) return;
            if (SidebarView != null) SidebarView.IsPaneOpen = true;
        }

        private void Sidebar_PointerExited(object s, PointerRoutedEventArgs e)
        {
            if (s is FrameworkElement el)
            {
                var p = e.GetCurrentPoint(el).Position;
                if (p.X >= el.ActualWidth || p.X <= 0 || p.Y < 0 || p.Y >= el.ActualHeight)
                {
                    if (SidebarView != null) SidebarView.IsPaneOpen = false;
                }
            }
        }

        private void SplitViewContent_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (SidebarView != null && SidebarView.IsPaneOpen)
            {
                SidebarView.IsPaneOpen = false;
            }
        }

        private void ToggleFullscreen_Click(object s, RoutedEventArgs e) => ToggleFullscreen();

        private void ImageGrid_ContainerContentChanging(ListViewBase s, ContainerContentChangingEventArgs a)
        {
            if (a.ItemContainer is GridViewItem container && container.ContentTemplateRoot is Grid root && root.Children.Count >= 2)
            {
                if (root.Children[0] is FrameworkElement tileCard)
                    tileCard.Visibility = _listMode ? Visibility.Collapsed : Visibility.Visible;
                if (root.Children[1] is FrameworkElement listRow)
                    listRow.Visibility = _listMode ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!_listMode && a.ItemContainer is GridViewItem gvi && TileCard(gvi) is Grid card)
            {
                if (a.InRecycleQueue)
                {
                    if (ReferenceEquals(_hoveredCard, card)) _hoveredCard = null;
                    SetTileRaised(card, false);
                }
                else if (a.Item is ImageItem shown)
                    SetTileRaised(card, shown.IsKeyboardCursor || ReferenceEquals(card, _hoveredCard));
            }
            if (a.Item is ImageItem i)
            {
                if (a.InRecycleQueue) i.IsPointerHovered = false;
                if (i.IsDirectory) return;
                // List rows do not own a thumbnail. Clearing here would drop pictures the tile view already loaded.
                if (a.InRecycleQueue)
                {
                    if (!_listMode && !_applyingViewMode) i.ClearThumbnail();
                }
                else if (!_listMode)
                {
                    if (a.Phase == 2) _ = i.LoadThumbnailAsync();
                    else a.RegisterUpdateCallback(ImageGrid_ContainerContentChanging);
                }
            }
        }

        private void Card_PointerEntered(object s, PointerRoutedEventArgs e)
        {
            if (s is not Grid card) return;
            _hoveredCard = card;
            if (card.DataContext is ImageItem item) item.IsPointerHovered = true;
            SetTileRaised(card, true);
        }
        private void Card_PointerExited(object s, PointerRoutedEventArgs e)
        {
            if (s is not Grid card) return;
            if (ReferenceEquals(_hoveredCard, card)) _hoveredCard = null;
            if (card.DataContext is ImageItem item) item.IsPointerHovered = false;
            bool keep = !_listMode && card.DataContext is ImageItem cur && cur.IsKeyboardCursor;
            SetTileRaised(card, keep);
        }

        private void ListRow_PointerEntered(object s, PointerRoutedEventArgs e)
        {
            if (s is FrameworkElement el && el.DataContext is ImageItem item)
                item.IsPointerHovered = true;
        }

        private void ListRow_PointerExited(object s, PointerRoutedEventArgs e)
        {
            if (s is FrameworkElement el && el.DataContext is ImageItem item)
                item.IsPointerHovered = false;
        }

        private ImageItem? _contextMenuTargetItem;

        private void ImageGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            ImageItem? item = null;
            while (node != null && !ReferenceEquals(node, ImageGrid))
            {
                if (node is FrameworkElement fe && fe.DataContext is ImageItem found)
                {
                    item = found;
                    break;
                }
                node = VisualTreeHelper.GetParent(node);
            }

            _contextMenuTargetItem = item;
            if (item == null)
            {
                e.Handled = true;
                return;
            }

            if (_listMode)
            {
                int listIdx = _listEntries.IndexOf(item);
                if (listIdx >= 0) _listCursor = listIdx;
                if (!item.IsDirectory)
                {
                    int photoIdx = Images.IndexOf(item);
                    if (photoIdx >= 0) _currentIndex = photoIdx;
                }
            }
            else
            {
                int photoIdx = Images.IndexOf(item);
                if (photoIdx >= 0) _currentIndex = photoIdx;
            }
            SyncTileCursor(scroll: false);

            bool isPhoto = !item.IsDirectory;
            bool isEditablePhoto = isPhoto && !item.IsComicEntry && !item.IsComicArchive;
            Visibility photoVis = isPhoto && !item.IsComicArchive ? Visibility.Visible : Visibility.Collapsed;
            Visibility fileMutateVis = isPhoto && !item.IsComicEntry ? Visibility.Visible : Visibility.Collapsed;
            if (GridMenuOpenItem != null)
                GridMenuOpenItem.Text = item.IsDirectory ? "Open Folder" : (item.IsComicArchive ? "Open Comic Book" : "Open Photo");
            if (GridMenuSelectItem != null)
            {
                GridMenuSelectItem.Visibility = fileMutateVis;
                GridMenuSelectItem.Text = item.IsGridSelected ? "Deselect" : "Select";
            }
            if (GridMenuDetachItem != null) GridMenuDetachItem.Visibility = photoVis;
            if (GridMenuPhotoSep1 != null) GridMenuPhotoSep1.Visibility = photoVis;
            if (GridMenuCinematicItem != null) GridMenuCinematicItem.Visibility = photoVis;
            if (GridMenuPhotoSep2 != null) GridMenuPhotoSep2.Visibility = fileMutateVis;
            if (GridMenuEditItem != null) GridMenuEditItem.Visibility = isEditablePhoto ? Visibility.Visible : Visibility.Collapsed;
            if (GridMenuRenameItem != null) GridMenuRenameItem.Visibility = fileMutateVis;
            if (GridMenuDeleteItem != null) GridMenuDeleteItem.Visibility = fileMutateVis;

            int multiSelectCount = ImageGrid.SelectedItems.OfType<ImageItem>().Count(img => !img.IsDirectory);
            if (GridMenuMultiViewItem != null)
            {
                GridMenuMultiViewItem.Visibility = multiSelectCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void GridMenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var item = _contextMenuTargetItem ?? CursorItem();
            if (item == null) return;
            if (item.IsDirectory)
            {
                if (item.Path == DriveRootPath) ShowDriveList();
                else if (!string.IsNullOrEmpty(item.Path) && Directory.Exists(item.Path)) _ = ScanFolder(item.Path);
            }
            else
            {
                int idx = Images.IndexOf(item);
                if (idx >= 0) LoadFullImage(idx);
            }
        }

        private void GridMenuSelect_Click(object sender, RoutedEventArgs e)
        {
            var item = _contextMenuTargetItem ?? CursorItem();
            if (item != null && !item.IsDirectory) ToggleGridItemSelection(item);
        }

        private void GridMenuDetach_Click(object sender, RoutedEventArgs e)
        {
            var item = _contextMenuTargetItem ?? CursorItem();
            if (item != null && !item.IsDirectory) LaunchDetachedWindow(item);
        }

        private void GridMenuCinematic_Click(object sender, RoutedEventArgs e)
        {
            if (ImageGrid.SelectedItems.Count > 0)
            {
                MultiSelectCinematic_Click(sender, e);
                return;
            }
            var item = _contextMenuTargetItem ?? CursorItem();
            int startIdx = item != null ? Math.Max(0, Images.IndexOf(item)) : 0;
            if (Images.Count > 0) OpenCinematic(Images.Select(i => i.Path).ToList(), startIdx);
        }

        private void GridMenuEdit_Click(object sender, RoutedEventArgs e)
        {
            var item = _contextMenuTargetItem ?? CursorItem();
            if (item == null || item.IsDirectory) return;
            int idx = Images.IndexOf(item);
            if (idx >= 0)
            {
                _currentIndex = idx;
                ViewerControl_EditRequested(this, EventArgs.Empty);
            }
        }

        private async void GridMenuRename_Click(object sender, RoutedEventArgs e)
        {
            var item = _contextMenuTargetItem ?? CursorItem();
            if (item != null && !item.IsDirectory) await RenameGridItemAsync(item);
        }

        private async void GridMenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ImageGrid.SelectedItems.Count > 1)
            {
                MultiSelectDelete_Click(sender, e);
                return;
            }
            var item = _contextMenuTargetItem ?? CursorItem();
            if (item != null && !item.IsDirectory) await DeleteGridItemAsync(item);
        }

        private void GridMenuExplorer_Click(object sender, RoutedEventArgs e)
        {
            ShowInExplorer_Click(sender, e);
        }

        private void PreviewThumbnail_Tapped(object sender, TappedRoutedEventArgs e)
        {
            OpenCursorTile();
        }

        private async Task RenameGridItemAsync(ImageItem item)
        {
            if (_isStartupIndexing || item.IsDirectory) return;
            await item.CancelAndAwaitTasksAsync();
            string oldPath = item.Path;
            bool watcherWasEnabled = _folderWatcher?.EnableRaisingEvents == true;
            if (_folderWatcher != null) _folderWatcher.EnableRaisingEvents = false;
            try
            {
                if (await ViewerEngine.RenameImageAsync(item, RootGrid.XamlRoot))
                {
                    NotifyImageRenamed(oldPath, item.Path);
                    UpdateListPreview(CursorItem());
                }
            }
            finally
            {
                if (_folderWatcher != null && watcherWasEnabled)
                    _folderWatcher.EnableRaisingEvents = true;
            }
            FocusGrid();
        }

        private async Task DeleteGridItemAsync(ImageItem item)
        {
            if (_isStartupIndexing || item.IsDirectory) return;
            await item.CancelAndAwaitTasksAsync();
            bool watcherWasEnabled = _folderWatcher?.EnableRaisingEvents == true;
            if (_folderWatcher != null) _folderWatcher.EnableRaisingEvents = false;
            try
            {
                if (await ViewerEngine.DeleteImageAsync(item, RootGrid.XamlRoot))
                {
                    ImageCache.Evict(item.Path);
                    RemoveDisplayedImage(item);
                    if (_currentIndex >= Images.Count) _currentIndex = Images.Count - 1;
                    SyncTileCursor(scroll: false);
                    UpdateHeaderFolderInfo();
                }
            }
            finally
            {
                if (_folderWatcher != null && watcherWasEnabled)
                    _folderWatcher.EnableRaisingEvents = true;
            }
            FocusGrid();
        }

        private void ClosePreview_Click(object? s, RoutedEventArgs? e) => ClosePreviewInternal();

        // --- Routed ViewerControl Events ---
        private void ViewerControl_CloseRequested(object sender, EventArgs e) => ClosePreviewInternal();
        private void ViewerControl_ToggleFullscreenRequested(object sender, EventArgs e) => ToggleFullscreen();
        private void ViewerControl_NavigateRequested(object sender, int step) => Navigate(step);
        private void ViewerControl_ShowInExplorerRequested(object sender, EventArgs e) => ShowInExplorer_Click(sender, new RoutedEventArgs());

        public void NotifyImageDeleted(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            ImageCache.Evict(path);
            var item = Images.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            bool wasCurrent = _currentRenderedItem == item || (_currentIndex >= 0 && _currentIndex < Images.Count && Images[_currentIndex] == item);
            RemoveDisplayedImage(item);
            if (wasCurrent)
            {
                if (Images.Count == 0) ClosePreviewInternal();
                else LoadFullImage(Math.Min(_currentIndex, Images.Count - 1));
            }
        }

        public void NotifyImageRenamed(string oldPath, string newPath)
        {
            if (DetachedPaths.TryGetValue(oldPath, out int count))
            {
                DetachedPaths.Remove(oldPath);
                DetachedPaths[newPath] = count;
            }
        }

        private void ViewerControl_DeviceLostRestoring(object sender, EventArgs e)
        {
            _canvasDevice = CanvasDevice.GetSharedDevice();
            ImageCache.Clear();
            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible && _currentIndex >= 0)
            {
                LoadFullImage(_currentIndex, 1);
            }
        }

        private void ViewerControl_DetachLimitRequested(object sender, int limit)
        {
            CurrentDetachLimit = (DetachLimit)limit;
            ViewerControl.SetDetachLimitState(limit);
        }

        private void ViewerControl_DetachRequested(object sender, Point e)
        {
            if (_currentIndex >= 0 && _currentIndex < Images.Count)
            {
                LaunchDetachedWindow(Images[_currentIndex], e);
                ClosePreviewInternal(isTearOff: true);
            }
        }

        private void ViewerControl_TearOffInitiated(object sender, Point e)
        {
            if (_currentIndex >= 0 && _currentIndex < Images.Count)
            {
                LaunchDetachedWindow(Images[_currentIndex], e);
            }
        }

        private void ViewerControl_TearOffMoved(object sender, EventArgs e)
        {
            if (_tearingOffWindow != null)
            {
                NativeMethods.GetCursorPos(out var cursorPos);
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_tearingOffWindow);
                var appWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(appWindowId);

                double percentX = Math.Clamp(_tearOffStartPos.X / Math.Max(1, ViewerControl.ActualWidth), 0, 1);
                double percentY = Math.Clamp(_tearOffStartPos.Y / Math.Max(1, ViewerControl.ActualHeight), 0, 1);

                int targetW = appWindow.Size.Width;
                int targetH = appWindow.Size.Height;
                int offsetX = (int)(targetW * percentX);
                int offsetY = (int)(targetH * percentY);

                appWindow.Move(new Windows.Graphics.PointInt32(cursorPos.X - offsetX, cursorPos.Y - offsetY));
            }
        }

        private void ViewerControl_TearOffCompleted(object sender, EventArgs e)
        {
            if (_tearingOffWindow != null)
            {
                _tearingOffWindow.Activate();
                _tearingOffWindow = null;
            }
            ClosePreviewInternal(isTearOff: true);
        }

        public void ViewerControl_EditRequested(object? sender, EventArgs e)
        {
            if (_isStartupIndexing) return;

            if (_currentIndex >= 0 && _currentIndex < Images.Count)
            {
                string targetPath = Images[_currentIndex].Path;
                string editor = string.IsNullOrWhiteSpace(ImageEditorPath) ? "mspaint.exe" : ImageEditorPath;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = $"\"{targetPath}\"",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to start editor: {ex.Message}");
                }
            }
        }

        private async void ViewerControl_RenameRequested(object sender, EventArgs e)
        {
            if (_isStartupIndexing) return;

            if (_currentRenderedItem != null)
            {
                _fastDecodeCts?.Cancel();
                _hfCts?.Cancel();

                // Explicitly await the running tasks to clear file handles deterministically
                if (_fastDecodeTask != null && !_fastDecodeTask.IsCompleted) try { await _fastDecodeTask; } catch { }
                if (_hfTask != null && !_hfTask.IsCompleted) try { await _hfTask; } catch { }
                await _currentRenderedItem.CancelAndAwaitTasksAsync();

                string oldPath = _currentRenderedItem.Path;
                bool watcherWasEnabled = _folderWatcher?.EnableRaisingEvents == true;
                if (_folderWatcher != null) _folderWatcher.EnableRaisingEvents = false;
                try
                {
                    if (await ViewerEngine.RenameImageAsync(_currentRenderedItem, this.Content.XamlRoot))
                    {
                        NotifyImageRenamed(oldPath, _currentRenderedItem.Path);
                        LoadFullImage(_currentIndex);
                    }
                }
                finally
                {
                    if (_folderWatcher != null && watcherWasEnabled)
                        _folderWatcher.EnableRaisingEvents = true;
                }
            }
        }

        private async void ViewerControl_DeleteRequested(object sender, EventArgs e)
        {
            if (_isStartupIndexing) return;

            if (_currentRenderedItem != null)
            {
                _fastDecodeCts?.Cancel();
                _hfCts?.Cancel();

                // Explicitly await the running tasks to clear file handles deterministically
                if (_fastDecodeTask != null && !_fastDecodeTask.IsCompleted) try { await _fastDecodeTask; } catch { }
                if (_hfTask != null && !_hfTask.IsCompleted) try { await _hfTask; } catch { }
                await _currentRenderedItem.CancelAndAwaitTasksAsync();

                bool watcherWasEnabled = _folderWatcher?.EnableRaisingEvents == true;
                if (_folderWatcher != null) _folderWatcher.EnableRaisingEvents = false;
                try
                {
                    if (await ViewerEngine.DeleteImageAsync(_currentRenderedItem, this.Content.XamlRoot))
                    {
                        ImageCache.Evict(_currentRenderedItem.Path);
                        RemoveDisplayedImage(_currentRenderedItem);
                        UpdateHeaderFolderInfo();
                        if (Images.Count == 0) ClosePreviewInternal();
                        else LoadFullImage(Math.Min(_currentIndex, Images.Count - 1));
                    }
                }
                finally
                {
                    if (_folderWatcher != null && watcherWasEnabled)
                        _folderWatcher.EnableRaisingEvents = true;
                }
            }
        }
    }
}

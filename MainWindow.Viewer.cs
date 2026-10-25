using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        public void LaunchDetachedWindow(ImageItem targetItem, Point? cursorSpawnPosition = null)
        {
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

            double currentLogicalW = ViewerControl.LogicalWidth > 0 ? ViewerControl.LogicalWidth : 1000;
            double currentLogicalH = ViewerControl.LogicalHeight > 0 ? ViewerControl.LogicalHeight : 1000;
            float targetZoom = ViewerControl.CurrentZoom > 0 ? ViewerControl.CurrentZoom : 1.0f;

            if (CurrentDetachLimit != DetachLimit.CurrentZoom)
            {
                double targetBoxH = displayArea.WorkArea.Height;

                if (CurrentDetachLimit == DetachLimit.Target1080p) targetBoxH = 1080;
                else if (CurrentDetachLimit == DetachLimit.Target1200p) targetBoxH = 1200;
                else if (CurrentDetachLimit == DetachLimit.Target800p) targetBoxH = 800;

                targetBoxH = Math.Min(targetBoxH, displayArea.WorkArea.Height);
                targetZoom = (float)(targetBoxH / currentLogicalH);

                if (currentLogicalW * targetZoom > displayArea.WorkArea.Width)
                {
                    targetZoom = (float)(displayArea.WorkArea.Width / currentLogicalW);
                }
            }

            double targetW = currentLogicalW * targetZoom;
            double targetH = currentLogicalH * targetZoom;

            if (targetW > displayArea.WorkArea.Width || targetH > displayArea.WorkArea.Height)
            {
                double scaleX = displayArea.WorkArea.Width / currentLogicalW;
                double scaleY = displayArea.WorkArea.Height / currentLogicalH;
                targetZoom = (float)Math.Min(scaleX, scaleY);
                targetW = currentLogicalW * targetZoom;
                targetH = currentLogicalH * targetZoom;
            }

            int finalW = Math.Max(300, (int)Math.Round(targetW));
            int finalH = Math.Max(300, (int)Math.Round(targetH));

            var detachedWindow = new DetachedWindow(targetItem, targetZoom, finalW, finalH, _currentGamma);
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
            }
            else
            {
                int spawnX = _appWindow.Position.X + 50;
                int spawnY = _appWindow.Position.Y + 50;

                if (spawnX + finalW > displayArea.WorkArea.X + displayArea.WorkArea.Width)
                    spawnX = Math.Max(displayArea.WorkArea.X, displayArea.WorkArea.X + displayArea.WorkArea.Width - finalW);

                if (spawnY + finalH > displayArea.WorkArea.Y + displayArea.WorkArea.Height)
                    spawnY = Math.Max(displayArea.WorkArea.Y, displayArea.WorkArea.Y + displayArea.WorkArea.Height - finalH);

                appWindow.Move(new Windows.Graphics.PointInt32(spawnX, spawnY));
            }

            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

            if (!cursorSpawnPosition.HasValue)
            {
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

            _currentIndex = index;
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

                ManageCache(index, stepDirection);

                QueueSharperDecode();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadFullImage error: {ex.Message}");
            }
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
                ViewerControl.InjectImage(published, false);
        }

        private static async Task<Size> GetNativeImageSizeAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                return new Size(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            }
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

            int nextIndex = _currentIndex + step;
            int stepDirection = step > 0 ? 1 : -1;

            if (nextIndex >= Images.Count || nextIndex < 0)
            {
                if (_currentIndex < 0 || _currentIndex >= Images.Count) return;

                string currentFolderPath = Path.GetDirectoryName(Images[_currentIndex].Path)?.TrimEnd('\\', '/') ?? string.Empty;
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

        private void ClosePreviewInternal(bool isTearOff = false)
        {
            _hfPromotionTimer?.Stop();
            _hfCts?.Cancel();
            ClearImageCache();

            if (_slideshowTimer != null && _slideshowTimer.IsEnabled) _slideshowTimer.Stop();

            ViewerControl.PrepareForNewImage(null);

            ViewerControl.Visibility = Visibility.Collapsed;
            if (HoverTriggerZone != null) HoverTriggerZone.Visibility = Visibility.Visible;

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

                    if (container.ContentTemplateRoot is Grid rootElement)
                    {
                        if (!isTearOff) RootGrid.Focus(FocusState.Programmatic);

                        rootElement.Scale = new Vector3(1.15f, 1.15f, 1.0f);
                        rootElement.Opacity = 0.5;

                        await Task.Delay(350);

                        rootElement.Scale = new Vector3(1.0f, 1.0f, 1.0f);
                        rootElement.Opacity = 1.0;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Hold Alt to peek at the sidebar; release to put it away. Unlike the hover zone,
        /// this works while an image is open: reaching for a key is deliberate, whereas the
        /// hover guard exists to stop the pointer drifting left and opening the pane by
        /// accident.
        /// </summary>
        private void BeginAltPeek()
        {
            if (SidebarView == null || _altPeekOpenedSidebar) return;
            // Already open by hover, pin or click: leave it alone so the release does not
            // close something Alt did not open.
            if (SidebarView.IsPaneOpen) return;

            SidebarView.IsPaneOpen = true;
            _altPeekOpenedSidebar = true;
        }

        private void EndAltPeek()
        {
            if (!_altPeekOpenedSidebar) return;
            _altPeekOpenedSidebar = false;
            if (SidebarView != null) SidebarView.IsPaneOpen = false;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                EndAltPeek();
                return;
            }

            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot) == null)
                RootGrid.Focus(FocusState.Programmatic);
        }

        private ImageItem? _tileCursorItem;

        private void SyncTileCursor(bool scroll)
        {
            ImageItem? next = (_currentIndex >= 0 && _currentIndex < Images.Count) ? Images[_currentIndex] : null;
            if (!ReferenceEquals(_tileCursorItem, next))
            {
                if (_tileCursorItem != null) _tileCursorItem.IsKeyboardCursor = false;
                _tileCursorItem = next;
                if (next != null) next.IsKeyboardCursor = true;
            }
            if (scroll && next != null) RevealTile(_currentIndex);
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
            var sv = ImageGrid != null ? FindScrollViewer(ImageGrid) : null;
            return ViewerMath.VisibleTileOrigin(
                sv?.VerticalOffset ?? 0,
                ImageGrid?.Padding.Top ?? 0,
                _imageWrapGrid?.ItemHeight ?? 164,
                TileColumnCount(),
                Images.Count);
        }

        private void RevealTile(int index)
        {
            if (ImageGrid == null || index < 0 || index >= Images.Count) return;
            var sv = FindScrollViewer(ImageGrid);
            if (sv == null)
            {
                ImageGrid.ScrollIntoView(Images[index]);
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
            if (Images.Count == 0 || _isScanning) return;
            if (!TryTileCursorKey(key, out var cursorKey)) return;

            int next = ViewerMath.MoveTileCursor(
                _currentIndex, Images.Count, TileColumnCount(), VisibleTileRows(), TopLeftVisibleTile(), cursorKey);
            if (next < 0) return;
            _currentIndex = next;
            SyncTileCursor(scroll: true);
        }

        private void OpenCursorTile()
        {
            if (Images.Count == 0 || _isScanning) return;
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
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot) is not DependencyObject node)
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
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot) is not DependencyObject node)
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

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    ClosePreviewInternal();
                    e.Handled = true;
                }
                return;
            }

            bool isCursorKey = e.Key == Windows.System.VirtualKey.Enter || TryTileCursorKey(e.Key, out _);
            if (!isCursorKey) return;

            bool sidebarOpen = SidebarView != null && SidebarView.IsPaneOpen;
            if (sidebarOpen && FocusIsInside(SidebarPane)) return;

            if (sidebarOpen && e.Key != Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Enter) OpenCursorTile();
            else MoveTileCursor(e.Key);
            e.Handled = true;
        }

        private void Global_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Menu)
            {
                EndAltPeek();
                e.Handled = true;
            }
        }

        private void Global_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Menu)
            {
                // WasKeyDown filters the auto-repeat that follows a held key.
                if (!e.KeyStatus.WasKeyDown) BeginAltPeek();
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

            if (e.Key == Windows.System.VirtualKey.Delete)
            {
                var focusedElement = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
                if (focusedElement is TextBox || focusedElement is PasswordBox || focusedElement is RichEditBox)
                    return;
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
                    case Windows.System.VirtualKey.Escape: ClosePreviewInternal(); break;
                    case Windows.System.VirtualKey.Delete: ViewerControl_DeleteRequested(this, EventArgs.Empty); break;
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
                    this.SetTitleBar(DragZone);
                    if (DragZone != null) DragZone.Visibility = Visibility.Visible;
                }
                else
                {
                    this.ExtendsContentIntoTitleBar = false;
                    if (DragZone != null) DragZone.Visibility = Visibility.Collapsed;
                    appW.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                }
            }
            _isFullscreen = !_isFullscreen;
        }

        private void ResetWindow_Click(object s, RoutedEventArgs e)
        {
            ConfigureWindow(true);
            if (ThumbSizeSlider != null) ThumbSizeSlider.Value = 164;
        }

        private void ImageGrid_ItemClick(object s, ItemClickEventArgs e)
        {
            bool isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            
            if (isCtrl || isShift) return;

            if (e.ClickedItem is ImageItem i) LoadFullImage(Images.IndexOf(i));

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
            if (_imageWrapGrid != null && ThumbSizeSlider != null)
            {
                _imageWrapGrid.ItemWidth = _imageWrapGrid.ItemHeight = ThumbSizeSlider.Value;
            }
        }

        private void ThumbSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_imageWrapGrid != null) _imageWrapGrid.ItemWidth = _imageWrapGrid.ItemHeight = e.NewValue;
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
                if (p.X >= el.ActualWidth - 10 || p.X <= 10)
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
            if (a.Item is ImageItem i)
            {
                if (a.InRecycleQueue) i.ClearThumbnail();
                else if (a.Phase == 2) _ = i.LoadThumbnailAsync();
                else a.RegisterUpdateCallback(ImageGrid_ContainerContentChanging);
            }
        }

        private void Card_PointerEntered(object s, PointerRoutedEventArgs e) { if (s is UIElement el) el.Scale = new Vector3(1.05f, 1.05f, 1.0f); }
        private void Card_PointerExited(object s, PointerRoutedEventArgs e) { if (s is UIElement el) el.Scale = new Vector3(1.0f, 1.0f, 1.0f); }
        private void ClosePreview_Click(object? s, RoutedEventArgs? e) => ClosePreviewInternal();

        // --- Routed ViewerControl Events ---
        private void ViewerControl_CloseRequested(object sender, EventArgs e) => ClosePreviewInternal();
        private void ViewerControl_ToggleFullscreenRequested(object sender, EventArgs e) => ToggleFullscreen();
        private void ViewerControl_NavigateRequested(object sender, int step) => Navigate(step);
        private void ViewerControl_ShowInExplorerRequested(object sender, EventArgs e) => ShowInExplorer_Click(sender, new RoutedEventArgs());

        private void ViewerControl_AddToCollageRequested(object? sender, EventArgs e)
        {
            if (_currentIndex >= 0 && _currentIndex < Images.Count)
                AddImageToCollage(Images[_currentIndex].Path);
        }

        public void AddImageToCollage(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            TestCollage_Click(this, new RoutedEventArgs());
            _collageEditorWindow?.AddExternalImage(path);
        }

        public void NotifyImageDeleted(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            ImageCache.Evict(path);
            var item = Images.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            bool wasCurrent = _currentRenderedItem == item || (_currentIndex >= 0 && _currentIndex < Images.Count && Images[_currentIndex] == item);
            Images.Remove(item);
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
                if (await ViewerEngine.RenameImageAsync(_currentRenderedItem, this.Content.XamlRoot))
                {
                    NotifyImageRenamed(oldPath, _currentRenderedItem.Path);
                    LoadFullImage(_currentIndex);
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

                if (await ViewerEngine.DeleteImageAsync(_currentRenderedItem, this.Content.XamlRoot))
                {
                    ImageCache.Evict(_currentRenderedItem.Path);
                    Images.Remove(_currentRenderedItem);
                    if (Images.Count == 0) ClosePreviewInternal();
                    else LoadFullImage(Math.Min(_currentIndex, Images.Count - 1));
                }
            }
        }
    }
}
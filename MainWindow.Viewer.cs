using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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

        private static async Task<Size> GetNativeImageSizeAsync(string path, CancellationToken token = default)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var stream = await file.OpenReadAsync().AsTask(token);
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
            if (args.WindowActivationState == WindowActivationState.Deactivated) EndAltPeek();
        }

        private ImageItem? _tileCursorItem;
        private Grid? _hoveredCard;
        private static readonly Vector3 TilePopScale = new(1.05f, 1.05f, 1f);
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

        // Going up one level puts the cursor back on the folder we just left, so Right reaches the next one.
        private bool TryPlaceListCursorOnReturnedFolder()
        {
            if (string.IsNullOrEmpty(_listReturnPath)) return false;
            for (int i = 0; i < _listEntries.Count; i++)
            {
                var item = _listEntries[i];
                if (!item.IsDirectory || !SameDirectory(item.Path, _listReturnPath)) continue;
                _listCursor = i;
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
            foreach (var image in Images)
                entries.Add(image);
            _listEntries.ResetWith(entries);
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
            if (string.Equals(_previewPath, item.Path, StringComparison.OrdinalIgnoreCase)
                && PreviewImage.Source != null
                && PreviewImage.Visibility == Visibility.Visible)
                return;

            _previewPath = item.Path;
            PreviewNameText.Text = item.Name;
            PreviewTypeText.Text = FileTypeLabel(item.Name);
            PreviewSizeText.Text = item.SizeString;
            PreviewModifiedText.Text = item.DateModified.ToString("yyyy-MM-dd HH:mm");
            PreviewDimensionsText.Text = "";
            PreviewTakenText.Text = "None";
            string folder = Path.GetDirectoryName(item.Path) ?? "";
            PreviewLocationText.Text = folder;
            ToolTipService.SetToolTip(PreviewLocationButton, item.Path);

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
                PreviewPlaceholder.Visibility = Visibility.Visible;
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
            PreviewSizeText.Text = "None";
            PreviewModifiedText.Text = "None";
            PreviewDimensionsText.Text = "None";
            PreviewTakenText.Text = "None";
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
                ImageSource? thumb = PreviewThumbnailReady(item.Thumbnail) ? item.Thumbnail : null;
                if (thumb == null)
                    thumb = await ReadShellThumbnailAsync(item.Path, token);
                if (thumb == null && !token.IsCancellationRequested)
                    thumb = await ReadScaledPreviewAsync(item.Path, token);
                Size size = await sizeTask;
                DateTime? taken = await takenTask;
                if (generation != _previewGeneration || token.IsCancellationRequested) return;

                void Apply()
                {
                    if (generation != _previewGeneration || token.IsCancellationRequested) return;
                    if (thumb != null)
                    {
                        item.Thumbnail = thumb;
                        PreviewImage.Source = thumb;
                        PreviewImage.Visibility = Visibility.Visible;
                        PreviewPlaceholder.Visibility = Visibility.Collapsed;
                    }
                    if (size.Width >= 1 && size.Height >= 1)
                        PreviewDimensionsText.Text = $"{(int)size.Width} × {(int)size.Height}";
                    PreviewTakenText.Text = taken.HasValue ? taken.Value.ToString("yyyy-MM-dd HH:mm") : "None";
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
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var stream = await file.OpenReadAsync().AsTask(token);
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
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var shell = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.PicturesView, 400, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale).AsTask(token);
                if (shell == null) return null;
                var cloned = shell.CloneStream();
                return await SetBitmapSourceAsync(cloned, token);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<ImageSource?> ReadScaledPreviewAsync(string path, CancellationToken token)
        {
            const uint longest = 400;
            SoftwareBitmap? softwareBitmap = null;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var stream = await file.OpenReadAsync().AsTask(token);
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
                            try { bitmap.Dispose(); } catch { }
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
            var sv = FindScrollViewer(ImageGrid);
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

        private void OpenCursorTile()
        {
            if (_isScanning) return;
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

            // A focused header button keeps Enter. Arrows stay off the grid so they do not select.
            if (FocusIsInside(TileHeader))
            {
                if (e.Key != Windows.System.VirtualKey.Enter) e.Handled = true;
                return;
            }

            // Enter on the location link opens Explorer. Arrows still move the list cursor.
            if (e.Key == Windows.System.VirtualKey.Enter && FocusIsInside(ListPreviewBar)) return;

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
            UIElement? bar = grid ? TileHeader : DragZone;
            if (bar != null) this.SetTitleBar(bar);
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
        private const double ListRowHeight = 22;
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

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            _listMode = !_listMode;
            _currentSettings.ListMode = _listMode;
            ApplyViewMode();
            SaveAllSettings();
        }

        private void ApplyViewMode()
        {
            if (ImageGrid == null || _applyingViewMode) return;
            _applyingViewMode = true;
            try
            {
            ImageItem.ListRows = _listMode;
            foreach (var item in Images) item.IsListRow = _listMode;
            if (ViewModeIcon != null) ViewModeIcon.Glyph = _listMode ? "\uE8A9" : "\uE8FD";
            if (ViewModeButton != null) ToolTipService.SetToolTip(ViewModeButton, _listMode ? "Tiles" : "List");
            ImageGrid.ItemContainerStyle = _listMode && ImageGrid.Resources["DenseListItemStyle"] is Style dense ? dense : null;
            if (_listMode)
                ImageGrid.Padding = new Thickness(0, 4, 0, 0);
            else
                ImageGrid.Padding = new Thickness(10, 8, 10, 0);
            if (ListPreviewBar != null)
                ListPreviewBar.Visibility = _listMode ? Visibility.Visible : Visibility.Collapsed;
            if (_listMode)
            {
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
                    int photo = Images.IndexOf(_listEntries[_listCursor]);
                    if (photo >= 0) _currentIndex = photo;
                }
                if (!ReferenceEquals(ImageGrid.ItemsSource, Images))
                    ImageGrid.ItemsSource = Images;
            }
            ApplyItemSize();
            ImageGrid.UpdateLayout();
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
            var sv = ImageGrid != null ? FindScrollViewer(ImageGrid) : null;
            if (sv != null && sv.ViewportWidth > 1) return sv.ViewportWidth;
            if (_imageWrapGrid != null && _imageWrapGrid.ActualWidth > 1) return _imageWrapGrid.ActualWidth;
            if (ImageGrid == null) return 0;
            return Math.Max(0, ImageGrid.ActualWidth - ImageGrid.Padding.Left - ImageGrid.Padding.Right);
        }

        // The first list layout can leave the grid scrolled sideways, so the left column is cut off until a photo is opened.
        private void PinListToLeftEdge()
        {
            var sv = FindScrollViewer(ImageGrid);
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

            if (e.ClickedItem is ImageItem i)
            {
                if (i.IsDirectory)
                {
                    if (i.Path == DriveRootPath)
                        ShowDriveList();
                    else if (!string.IsNullOrEmpty(i.Path) && Directory.Exists(i.Path))
                        _ = ScanFolder(i.Path);
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
            if (a.ItemContainer is GridViewItem container && TileCard(container) is Grid card)
            {
                if (a.InRecycleQueue)
                {
                    if (ReferenceEquals(_hoveredCard, card)) _hoveredCard = null;
                    SetTileRaised(card, false);
                }
                else if (!_listMode && a.Item is ImageItem shown)
                    SetTileRaised(card, shown.IsKeyboardCursor || ReferenceEquals(card, _hoveredCard));
            }
            if (a.Item is ImageItem i)
            {
                if (i.IsDirectory) return;
                // List rows do not own a thumbnail. Clearing here would drop pictures the tile view already loaded.
                if (a.InRecycleQueue)
                {
                    if (!_listMode) i.ClearThumbnail();
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
            SetTileRaised(card, true);
        }
        private void Card_PointerExited(object s, PointerRoutedEventArgs e)
        {
            if (s is not Grid card) return;
            if (ReferenceEquals(_hoveredCard, card)) _hoveredCard = null;
            bool keep = !_listMode && card.DataContext is ImageItem item && item.IsKeyboardCursor;
            SetTileRaised(card, keep);
        }
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
                    RemoveDisplayedImage(_currentRenderedItem);
                    if (Images.Count == 0) ClosePreviewInternal();
                    else LoadFullImage(Math.Min(_currentIndex, Images.Count - 1));
                }
            }
        }
    }
}
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

        private void TryDisposeRawGpuBitmap()
        {
            if (_rawGpuBitmap != null && !App.GlobalImageCache.Values.Any(v => v.GpuBitmap == _rawGpuBitmap))
            {
                if (_currentRenderedItem == null || !DetachedPaths.ContainsKey(_currentRenderedItem.Path))
                {
                    _rawGpuBitmap.Dispose();
                }
            }
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
                ViewerControl.Visibility = Visibility.Collapsed;
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
            _hfPromotionTimer?.Stop();

            var currentItem = Images[index];

            ViewerControl.Visibility = Visibility.Visible;
            if (HoverTriggerZone != null) HoverTriggerZone.Visibility = Visibility.Collapsed;

            ViewerControl.PrepareForNewImage(currentItem.Thumbnail);

            ViewerControl.TargetImage = currentItem;
            ViewerControl.Focus(FocusState.Programmatic);

            bool isCached = App.GlobalImageCache.TryGetValue(currentItem.Path, out var cachedEntry) && (cachedEntry.GpuBitmap != null || cachedEntry.Bitmap != null);

            _ = currentItem.LoadExifAsync(exifToken);

            try
            {
                CanvasBitmap? newGpuBitmap = null;
                ColorManagementProfile? newGpuProfile = null;
                double targetLogicalW = 0;
                double targetLogicalH = 0;

                if (isCached && cachedEntry != null)
                {
                    if (cachedEntry.GpuBitmap == null || cachedEntry.GpuBitmap.Device != _canvasDevice)
                    {
                        cachedEntry.GpuBitmap?.Dispose();
                        if (cachedEntry.Bitmap != null)
                        {
                            cachedEntry.GpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, cachedEntry.Bitmap);
                            cachedEntry.Bitmap.Dispose();
                            cachedEntry.Bitmap = null;
                        }
                    }
                    newGpuBitmap = cachedEntry.GpuBitmap;
                    newGpuProfile = cachedEntry.Profile;
                    targetLogicalW = cachedEntry.NativeWidth;
                    targetLogicalH = cachedEntry.NativeHeight;
                    _isHighFidelityActive = cachedEntry.IsHighFidelity;
                }
                else
                {
                    var result = await Task.Run(async () =>
                    {
                        var entry = await ViewerEngine.DecodeFastPreviewAsync(currentItem.Path, fastToken);
                        if (entry != null && entry.Bitmap != null && _canvasDevice != null && !fastToken.IsCancellationRequested)
                        {
                            try { entry.GpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, entry.Bitmap); entry.Bitmap.Dispose(); entry.Bitmap = null; } catch { }
                        }
                        return entry;
                    });

                    if (fastToken.IsCancellationRequested || myLoadId != _currentImageLoadId) { result?.Dispose(); return; }

                    if (result != null && (result.GpuBitmap != null || result.Bitmap != null))
                    {
                        if (result.GpuBitmap == null && result.Bitmap != null && _canvasDevice != null)
                        {
                            try { result.GpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, result.Bitmap); result.Bitmap.Dispose(); result.Bitmap = null; } catch { }
                        }

                        if (App.GlobalImageCache.TryGetValue(currentItem.Path, out var existing))
                        {
                            if (!existing.IsHighFidelity && existing.GpuBitmap != _rawGpuBitmap && !DetachedPaths.ContainsKey(currentItem.Path))
                            {
                                existing.Dispose();
                                App.GlobalImageCache[currentItem.Path] = result;
                            }
                            else { result.Dispose(); result = existing; }
                        }
                        else { App.GlobalImageCache[currentItem.Path] = result; }
                    }

                    if (result != null)
                    {
                        newGpuBitmap = result.GpuBitmap;
                        newGpuProfile = result.Profile;
                        targetLogicalW = result.NativeWidth;
                        targetLogicalH = result.NativeHeight;
                    }
                }

                if (newGpuBitmap == null)
                {
                    try
                    {
                        var recoveryEntry = await ViewerEngine.DecodeFastPreviewAsync(currentItem.Path, fastToken);
                        if (recoveryEntry?.Bitmap != null && _canvasDevice != null && !fastToken.IsCancellationRequested)
                        {
                            newGpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, recoveryEntry.Bitmap);
                            recoveryEntry.Bitmap.Dispose();
                            recoveryEntry.Bitmap = null;
                            recoveryEntry.GpuBitmap = newGpuBitmap;

                            if (App.GlobalImageCache.TryGetValue(currentItem.Path, out var oldEntry) && !DetachedPaths.ContainsKey(currentItem.Path))
                            {
                                oldEntry.Dispose();
                            }
                            App.GlobalImageCache[currentItem.Path] = recoveryEntry;
                            targetLogicalW = recoveryEntry.NativeWidth;
                            targetLogicalH = recoveryEntry.NativeHeight;
                        }
                        else { recoveryEntry?.Dispose(); }
                    }
                    catch { }
                }

                if (myLoadId != _currentImageLoadId || newGpuBitmap == null) return;

                TryDisposeRawGpuBitmap();

                _rawGpuBitmap = newGpuBitmap;
                _rawGpuProfile = newGpuProfile;

                if (targetLogicalW == 0)
                {
                    var size = await GetNativeImageSizeAsync(currentItem.Path);
                    targetLogicalW = size.Width > 0 ? size.Width : _rawGpuBitmap.SizeInPixels.Width;
                    targetLogicalH = size.Height > 0 ? size.Height : _rawGpuBitmap.SizeInPixels.Height;
                }

                _logicalImageWidth = targetLogicalW;
                _logicalImageHeight = targetLogicalH;
                _currentRenderedItem = currentItem;

                ViewerControl.InjectGpuBitmap(_rawGpuBitmap, _rawGpuProfile, targetLogicalW, targetLogicalH, _isHighFidelityActive, true);

                ManageCache(index, stepDirection);

                if (!_isHighFidelityActive) _hfPromotionTimer?.Start();
            }
            catch { }
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
            int prefetchRadius = 8;
            int keepAliveRadius = 15;

            _activeCachePaths.Clear();
            HashSet<string> keepAlivePaths = [];

            if (centerIndex >= 0 && centerIndex < Images.Count)
            {
                _activeCachePaths.Add(Images[centerIndex].Path);
                keepAlivePaths.Add(Images[centerIndex].Path);
            }

            for (int i = 1; i <= prefetchRadius; i++)
            {
                if (centerIndex + i < Images.Count) _activeCachePaths.Add(Images[centerIndex + i].Path);
                if (centerIndex - i >= 0) _activeCachePaths.Add(Images[centerIndex - i].Path);
            }

            for (int i = 1; i <= keepAliveRadius; i++)
            {
                if (centerIndex + i < Images.Count) keepAlivePaths.Add(Images[centerIndex + i].Path);
                if (centerIndex - i >= 0) keepAlivePaths.Add(Images[centerIndex - i].Path);
            }

            var keysToRemove = App.GlobalImageCache.Keys.Where(k => !keepAlivePaths.Contains(k) && !DetachedPaths.ContainsKey(k)).ToList();
            foreach (var key in keysToRemove)
            {
                if (App.GlobalImageCache.TryGetValue(key, out var entry))
                {
                    entry.Dispose();
                    App.GlobalImageCache.TryRemove(key, out _);
                }
            }

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

            foreach (var path in _activeCachePaths)
            {
                if (!App.GlobalImageCache.ContainsKey(path) && !_activeDecodes.ContainsKey(path))
                {
                    var cts = new CancellationTokenSource();
                    _activeDecodes[path] = cts;
                    var token = cts.Token;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await App.GlobalCacheSemaphore.WaitAsync(token);
                            try
                            {
                                if (token.IsCancellationRequested) return;
                                var entry = await ViewerEngine.DecodeFastPreviewAsync(path, token);

                                if (entry != null && entry.Bitmap != null && _canvasDevice != null && !token.IsCancellationRequested)
                                {
                                    try
                                    {
                                        entry.GpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, entry.Bitmap);
                                        entry.Bitmap.Dispose();
                                        entry.Bitmap = null;
                                    }
                                    catch { }
                                }

                                bool enqueued = this.DispatcherQueue.TryEnqueue(() =>
                                {
                                    _activeDecodes.Remove(path);

                                    if (token.IsCancellationRequested)
                                    {
                                        entry?.Dispose();
                                        return;
                                    }

                                    if ((keepAlivePaths.Contains(path) || DetachedPaths.ContainsKey(path)) && entry != null && (entry.GpuBitmap != null || entry.Bitmap != null))
                                    {
                                        if (App.GlobalImageCache.TryGetValue(path, out var oldEntry))
                                        {
                                            if (oldEntry.IsHighFidelity || oldEntry.GpuBitmap == _rawGpuBitmap || DetachedPaths.ContainsKey(path))
                                            {
                                                entry.Dispose();
                                            }
                                            else
                                            {
                                                oldEntry.Dispose();
                                                App.GlobalImageCache[path] = entry;
                                            }
                                        }
                                        else
                                        {
                                            App.GlobalImageCache[path] = entry;
                                        }
                                    }
                                    else { entry?.Dispose(); }
                                });

                                if (!enqueued) entry?.Dispose();
                            }
                            finally { App.GlobalCacheSemaphore.Release(); }
                        }
                        catch (OperationCanceledException)
                        {
                            this.DispatcherQueue.TryEnqueue(() => _activeDecodes.Remove(path));
                        }
                        catch
                        {
                            this.DispatcherQueue.TryEnqueue(() => _activeDecodes.Remove(path));
                        }
                    }, token);
                }
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

            var keysToRemove = App.GlobalImageCache.Keys.Where(k => !DetachedPaths.ContainsKey(k)).ToList();
            foreach (var key in keysToRemove)
            {
                if (App.GlobalImageCache.TryGetValue(key, out var entry))
                {
                    entry.Dispose();
                    App.GlobalImageCache.TryRemove(key, out _);
                }
            }
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
                    _ = ScanFolder(_hopFolders[nextFolderIdx].Path).ContinueWith(t =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (Images.Count > 0)
                            {
                                LoadFullImage(stepDirection > 0 ? 0 : Images.Count - 1, stepDirection);
                            }
                        });
                    });
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
            _mouseWheelAccumulator = 0;
            _hfPromotionTimer?.Stop();
            _hfCts?.Cancel();
            ClearImageCache();

            if (_slideshowTimer != null && _slideshowTimer.IsEnabled) _slideshowTimer.Stop();

            ViewerControl.Visibility = Visibility.Collapsed;
            if (HoverTriggerZone != null) HoverTriggerZone.Visibility = Visibility.Visible;

            TryDisposeRawGpuBitmap();
            _rawGpuBitmap = null;

            if (_currentIndex >= 0 && _currentIndex < Images.Count && ImageGrid != null)
            {
                var targetItem = Images[_currentIndex];

                ImageGrid.ScrollIntoView(targetItem);

                DispatcherQueue.TryEnqueue(async () =>
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
                            if (!isTearOff)
                            {
                                RootGrid.Focus(FocusState.Programmatic);
                            }

                            rootElement.Scale = new Vector3(1.15f, 1.15f, 1.0f);
                            rootElement.Opacity = 0.5;

                            await Task.Delay(350);

                            rootElement.Scale = new Vector3(1.0f, 1.0f, 1.0f);
                            rootElement.Opacity = 1.0;
                        }
                    }
                });
            }
        }

        private void Global_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.F11) ToggleFullscreen();
            else if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
            {
                _mouseWheelAccumulator = 0;
                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Right: Navigate(1); break;
                    case Windows.System.VirtualKey.Left: Navigate(-1); break;
                    case Windows.System.VirtualKey.Home: Navigate(int.MinValue); break;
                    case Windows.System.VirtualKey.End: Navigate(int.MaxValue); break;
                    case Windows.System.VirtualKey.Escape: ClosePreviewInternal(); break;
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

        private void ToggleSlideshow_Click(object s, RoutedEventArgs e)
        {
            if (_slideshowTimer != null)
            {
                if (_slideshowTimer.IsEnabled) _slideshowTimer.Stop();
                else _slideshowTimer.Start();
            }
        }

        private void ResetWindow_Click(object s, RoutedEventArgs e)
        {
            ConfigureWindow(true);
            if (ThumbSizeSlider != null) ThumbSizeSlider.Value = 164;
        }

        private void ImageGrid_ItemClick(object s, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ImageItem i) LoadFullImage(Images.IndexOf(i));
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

        private void ViewerControl_DeviceLostRestoring(object sender, EventArgs e)
        {
            _canvasDevice = CanvasDevice.GetSharedDevice();
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
    }
}
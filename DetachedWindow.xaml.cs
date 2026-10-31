using Microsoft.Graphics.Canvas;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;

namespace ModernImageViewer
{
    public sealed partial class DetachedWindow : Window
    {
        private readonly AppWindow _appWindow;
        private readonly ImageItem _imageItem;
        private bool _isFullscreen = false;
        private bool _wasMaximizedOrFullscreen = false;
        private bool _hasAdjustedChrome = false;

        private KeyEventHandler _globalKeyDownHandler;
        private int _currentImageLoadId = 0;
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _promoteCts;
        private DispatcherQueueTimer? _promoteTimer;
        private int _promoteEdge;
        private bool _closed;

        private bool _isWindowDragging = false;
        private bool _isDragThresholdPassed = false;
        private NativeMethods.POINT _dragStartCursorPos;
        private Windows.Graphics.PointInt32 _dragStartWindowPos;

        public DetachedWindow(ImageItem imageItem, int targetWidth, int targetHeight, float initialGamma)
        {
            this.InitializeComponent();
            _imageItem = imageItem;
            this.Title = string.IsNullOrEmpty(_imageItem.Name) ? "Detached Image" : _imageItem.Name;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }

            _appWindow.Changed += AppWindow_Changed;
            _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            _globalKeyDownHandler = new KeyEventHandler(Global_KeyDown);
            RootGrid.AddHandler(UIElement.KeyDownEvent, _globalKeyDownHandler, true);
            this.Closed += DetachedWindow_Closed;

            MainWindow.Instance?.RegisterDetachedWindow();
            if (MainWindow.Instance != null)
                MainWindow.Instance.GammaApplied += OnHostGammaChanged;

            ViewerControl.TargetImage = _imageItem;
            ViewerControl.IsStandaloneMode = true;
            ViewerControl.SetGammaLevel(initialGamma);
            ViewerControl.GammaChanged += (_, g) => MainWindow.Instance?.ApplyGamma(g);
            ViewerControl.ZoomChanged += (_, _) => QueuePromote();

            LoadImageAsync();
        }

        private void OnHostGammaChanged(float gamma) => ViewerControl.SetGammaLevel(gamma);

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange && sender.ClientSize.Width > 0 && !_hasAdjustedChrome)
            {
                _hasAdjustedChrome = true;
                _appWindow.Changed -= AppWindow_Changed;

                int chromeW = sender.Size.Width - sender.ClientSize.Width;
                int chromeH = sender.Size.Height - sender.ClientSize.Height;

                if (chromeW > 0 || chromeH > 0)
                {
                    var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(sender.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                    int totalW = Math.Min(sender.Size.Width + chromeW, displayArea.WorkArea.Width);
                    int totalH = Math.Min(sender.Size.Height + chromeH, displayArea.WorkArea.Height);

                    sender.Resize(new Windows.Graphics.SizeInt32(totalW, totalH));
                }
            }
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Focus(FocusState.Programmatic);
            ViewerControl.FitToWindow();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool isMaxOrFullscreen = (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ||
                                     (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized);

            // Maximizing and coming back both show the whole picture. Otherwise the viewer keeps a fitted picture fitted and leaves a zoomed one alone.
            if (isMaxOrFullscreen != _wasMaximizedOrFullscreen)
            {
                ViewerControl.FitToWindow();
            }
            _wasMaximizedOrFullscreen = isMaxOrFullscreen;
            ViewerControl.SetIsMaximizedOrFullscreen(isMaxOrFullscreen);
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;

            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (props.IsLeftButtonPressed && !e.Handled)
            {
                _isWindowDragging = true;
                _isDragThresholdPassed = false;
                NativeMethods.GetCursorPos(out _dragStartCursorPos);
                _dragStartWindowPos = _appWindow.Position;
            }
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isWindowDragging)
            {
                if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) return;

                NativeMethods.GetCursorPos(out NativeMethods.POINT currentCursorPos);
                int dx = currentCursorPos.X - _dragStartCursorPos.X;
                int dy = currentCursorPos.Y - _dragStartCursorPos.Y;

                if (!_isDragThresholdPassed)
                {
                    if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                    {
                        if (RootGrid.CapturePointer(e.Pointer))
                        {
                            _isDragThresholdPassed = true;

                            if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized)
                            {
                                p.Restore();

                                int targetW = (int)Math.Max(300, ViewerControl.LogicalWidth * ViewerControl.CurrentZoom);
                                int newX = currentCursorPos.X - (targetW / 2);
                                int newY = currentCursorPos.Y - 20;

                                _dragStartWindowPos = new Windows.Graphics.PointInt32(newX, newY);
                                _dragStartCursorPos = currentCursorPos;
                                dx = 0;
                                dy = 0;
                                _appWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                            }
                        }
                        else
                        {
                            _isWindowDragging = false;
                            return;
                        }
                    }
                }

                if (_isDragThresholdPassed)
                {
                    _appWindow.Move(new Windows.Graphics.PointInt32(_dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy));
                }
            }
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isWindowDragging)
            {
                _isWindowDragging = false;
                if (_isDragThresholdPassed)
                {
                    RootGrid.ReleasePointerCapture(e.Pointer);
                }
                _isDragThresholdPassed = false;
            }
        }

        private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_isWindowDragging)
            {
                _isWindowDragging = false;
                if (_isDragThresholdPassed)
                {
                    RootGrid.ReleasePointerCapture(e.Pointer);
                }
                _isDragThresholdPassed = false;
            }
        }

        private void Global_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // This handler also receives keys already dealt with, such as the Esc that closes a menu or a box.
            if (e.Handled) return;
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox || focused is PasswordBox || focused is RichEditBox) return;

            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                this.Close();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Delete)
            {
                ViewerControl_DeleteRequested(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void DetachedWindow_Closed(object sender, WindowEventArgs args)
        {
            _appWindow.Changed -= AppWindow_Changed;
            RootGrid.RemoveHandler(UIElement.KeyDownEvent, _globalKeyDownHandler);
            if (MainWindow.Instance != null)
                MainWindow.Instance.GammaApplied -= OnHostGammaChanged;
            MainWindow.Instance?.UnregisterDetachedWindow(_imageItem.Path);

            _closed = true;
            _promoteTimer?.Stop();
            _currentImageLoadId++;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
            _promoteCts?.Cancel();
            _promoteCts?.Dispose();
            _promoteCts = null;

            // Do not call ViewerControl.PrepareForNewImage(null) here because scheduling RequestRender() /
            // PreviewCanvas.Invalidate() during window shutdown causes a WinUI 3 fail-fast crash (0xc000027b).
            // ImageViewerControl_Unloaded handles canvas and effect cleanup safely.

            ViewerControl.CloseRequested -= ViewerControl_CloseRequested;
            ViewerControl.ToggleFullscreenRequested -= ViewerControl_ToggleFullscreenRequested;
            ViewerControl.SizeToImageRequested -= ViewerControl_SizeToImageRequested;
            ViewerControl.MinimizeRequested -= ViewerControl_MinimizeRequested;
            ViewerControl.DeviceLostRestoring -= ViewerControl_DeviceLostRestoring;
            ViewerControl.EditRequested -= ViewerControl_EditRequested;
            ViewerControl.RenameRequested -= ViewerControl_RenameRequested;
            ViewerControl.DeleteRequested -= ViewerControl_DeleteRequested;
            ViewerControl.ShowInExplorerRequested -= ViewerControl_ShowInExplorerRequested;
            ViewerControl.SwitchToMainRequested -= ViewerControl_SwitchToMainRequested;
        }

        private void QueuePromote()
        {
            if (_closed) return;
            if (_promoteTimer == null)
            {
                _promoteTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _promoteTimer.Interval = TimeSpan.FromMilliseconds(150);
                _promoteTimer.IsRepeating = false;
                _promoteTimer.Tick += (_, _) =>
                {
                    _promoteTimer?.Stop();
                    _ = PromoteDisplayedAsync();
                };
            }
            _promoteTimer.Stop();
            _promoteTimer.Start();
        }

        private async void LoadImageAsync()
        {
            if (_closed || string.IsNullOrEmpty(_imageItem.Path)) return;

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            int myLoadId = ++_currentImageLoadId;
            _promoteEdge = 0;

            try
            {
                var sharedDevice = CanvasDevice.GetSharedDevice();
                ImageCache.TryGet(_imageItem.Path, out var sharedEntry);
                if (sharedEntry != null && sharedEntry.HasDecodableContent
                    && (sharedDevice == null || sharedEntry.DeviceMatches(sharedDevice)))
                {
                    if (sharedEntry.GpuBitmap == null && sharedEntry.AnimationFrames == null && sharedEntry.Bitmap != null)
                    {
                        sharedEntry.GpuBitmap = ViewerEngine.CreateGpuBitmap(sharedEntry.Bitmap);
                        sharedEntry.Bitmap.Dispose();
                        sharedEntry.Bitmap = null;
                    }
                    if (sharedEntry.GpuBitmap != null || sharedEntry.AnimationFrames != null)
                    {
                        ViewerControl.InjectImage(sharedEntry, false);
                        ViewerControl.FitToWindow();
                        QueuePromote();
                        return;
                    }
                }

                var preview = await ViewerEngine.DecodeFastPreviewAsync(_imageItem.Path, token);
                if (_closed || token.IsCancellationRequested || myLoadId != _currentImageLoadId)
                {
                    preview?.Release();
                    return;
                }

                if (preview?.Bitmap != null)
                {
                    preview.GpuBitmap = ViewerEngine.CreateGpuBitmap(preview.Bitmap);
                    preview.Bitmap.Dispose();
                    preview.Bitmap = null;
                }

                if (preview != null && (preview.GpuBitmap != null || preview.AnimationFrames != null))
                {
                    ImageCache.Publish(_imageItem.Path, preview);
                    if (ImageCache.TryGet(_imageItem.Path, out var shown) && shown != null
                        && (shown.GpuBitmap != null || shown.AnimationFrames != null))
                    {
                        ViewerControl.InjectImage(shown, false);
                        ViewerControl.FitToWindow();
                    }
                }
                else
                {
                    ViewerControl.ShowNotification("Could not decode this image");
                }

                if (!_closed && myLoadId == _currentImageLoadId)
                    QueuePromote();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Detached Load Failed: {ex.Message}");
                if (!_closed) ViewerControl.ShowNotification("Could not decode this image");
            }
        }

        private async Task PromoteDisplayedAsync()
        {
            if (_closed) return;

            double nativeW = ViewerControl.LogicalWidth;
            double nativeH = ViewerControl.LogicalHeight;
            if (nativeW <= 1 || nativeH <= 1) return;

            int have = 0;
            if (ImageCache.TryGet(_imageItem.Path, out var cached) && cached != null)
            {
                if (cached.AnimationCapped) return;
                have = cached.DecodedLongEdge;
            }

            int edge = ViewerMath.NextDecodeEdge(nativeW, nativeH, ViewerControl.CurrentZoom, ViewerControl.FitZoom, have, (int)ViewerEngine.HighFidelityMaxEdge);
            if (edge <= 0) return;
            if (_promoteEdge >= edge && _promoteCts != null && !_promoteCts.IsCancellationRequested) return;

            _promoteCts?.Cancel();
            _promoteCts?.Dispose();
            _promoteCts = new CancellationTokenSource();
            var token = _promoteCts.Token;
            int myLoadId = _currentImageLoadId;
            _promoteEdge = edge;

            var decoded = await ViewerEngine.DecodeHighFidelityAsync(_imageItem.Path, token, (uint)edge);
            if (_closed || token.IsCancellationRequested || myLoadId != _currentImageLoadId || decoded == null)
            {
                decoded?.Release();
                if (myLoadId == _currentImageLoadId && _promoteEdge == edge) _promoteEdge = 0;
                return;
            }

            bool success = ViewerEngine.FinalizeHighFidelityGpuResources(decoded);
            if (!success || _closed || token.IsCancellationRequested || myLoadId != _currentImageLoadId)
            {
                decoded.Release();
                if (myLoadId == _currentImageLoadId && _promoteEdge == edge) _promoteEdge = 0;
                return;
            }

            ImageCache.Publish(_imageItem.Path, decoded);
            if (_closed || myLoadId != _currentImageLoadId) return;
            if (!ImageCache.TryGet(_imageItem.Path, out var published) || published == null) return;
            if (published.GpuBitmap == null && published.AnimationFrames == null) return;

            ViewerControl.InjectImage(published, false);
            if (published.AnimationNote != null) ViewerControl.ShowNotification(published.AnimationNote);
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.Default);
                if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
            else
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            _isFullscreen = !_isFullscreen;
        }

        // Nullable signatures updated for CS8622
        private void ViewerControl_CloseRequested(object? sender, EventArgs e) => this.Close();
        private void ViewerControl_ToggleFullscreenRequested(object? sender, EventArgs e)
        {
            if (_isFullscreen)
            {
                ToggleFullscreen();
                return;
            }

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                }
                else
                {
                    presenter.Maximize();
                }
            }
        }
        private void ViewerControl_DeviceLostRestoring(object? sender, EventArgs e) => LoadImageAsync();

        private void ViewerControl_ShowInExplorerRequested(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_imageItem.Path))
            {
                string path = ComicArchive.TrySplitEntryPath(_imageItem.Path, out string archivePath, out _) ? archivePath : _imageItem.Path;
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }

        private void ViewerControl_MinimizeRequested(object? sender, EventArgs e)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
            else
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                NativeMethods.ShowWindow(hwnd, 6);
            }
        }

        private async void ViewerControl_SizeToImageRequested(object? sender, EventArgs e)
        {
            if (ViewerControl.LogicalWidth <= 0 || ViewerControl.LogicalHeight <= 0) return;

            if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized)
            {
                p.Restore();
                await Task.Delay(50);
            }

            int chromeW = _appWindow.Size.Width - _appWindow.ClientSize.Width;
            int chromeH = _appWindow.Size.Height - _appWindow.ClientSize.Height;

            double reqClientW = ViewerControl.LogicalWidth * ViewerControl.CurrentZoom;
            double reqClientH = ViewerControl.LogicalHeight * ViewerControl.CurrentZoom;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

            double totalW = Math.Min(reqClientW + chromeW, displayArea.WorkArea.Width);
            double totalH = Math.Min(reqClientH + chromeH, displayArea.WorkArea.Height);

            _appWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(totalW), (int)Math.Round(totalH)));
        }

        private void ViewerControl_EditRequested(object? sender, EventArgs e)
        {
            string? problem = ViewerEngine.OpenInEditor(_imageItem.Path, MainWindow.Instance?.ImageEditorPath);
            if (problem != null) ViewerControl.ShowNotification(problem);
        }

        private async void ViewerControl_RenameRequested(object? sender, EventArgs e)
        {
            if (await ViewerEngine.RenameImageAsync(_imageItem, this.Content.XamlRoot))
            {
                this.Title = _imageItem.Name;
                LoadImageAsync();
            }
        }

        private async void ViewerControl_DeleteRequested(object? sender, EventArgs e)
        {
            if (await ViewerEngine.DeleteImageAsync(_imageItem, this.Content.XamlRoot))
            {
                MainWindow.Instance?.NotifyImageDeleted(_imageItem.Path);
                this.Close();
            }
        }

        private void ViewerControl_SwitchToMainRequested(object? sender, EventArgs e)
        {
            if (MainWindow.Instance != null && !string.IsNullOrEmpty(_imageItem.Path))
            {
                MainWindow.Instance.SummonMainPreview(_imageItem.Path);
            }
            else if (!string.IsNullOrEmpty(_imageItem.Path))
            {
                var newMain = new MainWindow();
                newMain.Activate();
                newMain.SummonMainPreview(_imageItem.Path);
            }
        }
    }
}
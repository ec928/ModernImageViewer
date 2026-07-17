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
        private ImageCacheEntry? _localCacheEntry;
        private float _initialZoom;

        private bool _isWindowDragging = false;
        private bool _isDragThresholdPassed = false;
        private NativeMethods.POINT _dragStartCursorPos;
        private Windows.Graphics.PointInt32 _dragStartWindowPos;

        public DetachedWindow(ImageItem imageItem, float initialZoom, int targetWidth, int targetHeight, float initialGamma)
        {
            this.InitializeComponent();
            _imageItem = imageItem;
            _initialZoom = initialZoom;
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

            MainWindow.Instance?.RegisterDetachedWindow(_imageItem.Path);

            ViewerControl.TargetImage = _imageItem;
            ViewerControl.IsStandaloneMode = true;
            ViewerControl.SetGammaLevel(initialGamma);

            LoadImageAsync();
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange && sender.ClientSize.Width > 0 && !_hasAdjustedChrome)
            {
                _hasAdjustedChrome = true;
                _appWindow.Changed -= AppWindow_Changed;

                int chromeW = sender.Size.Width - sender.ClientSize.Width;
                int chromeH = sender.Size.Height - sender.ClientSize.Height;

                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(sender.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                int totalW = Math.Min(sender.Size.Width + chromeW, displayArea.WorkArea.Width);
                int totalH = Math.Min(sender.Size.Height + chromeH, displayArea.WorkArea.Height);

                sender.Resize(new Windows.Graphics.SizeInt32(totalW, totalH));
            }
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool isMaxOrFullscreen = (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ||
                                     (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized);

            if (isMaxOrFullscreen && !_wasMaximizedOrFullscreen)
            {
                ViewerControl.FitToWindow();
            }
            _wasMaximizedOrFullscreen = isMaxOrFullscreen;
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
                var focusedElement = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
                if (focusedElement is TextBox || focusedElement is PasswordBox || focusedElement is RichEditBox)
                    return;

                ViewerControl_DeleteRequested(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void DetachedWindow_Closed(object sender, WindowEventArgs args)
        {
            _appWindow.Changed -= AppWindow_Changed;
            RootGrid.RemoveHandler(UIElement.KeyDownEvent, _globalKeyDownHandler);
            MainWindow.Instance?.UnregisterDetachedWindow(_imageItem.Path);

            // Clean release of the reference back to the app pool
            _localCacheEntry?.Release();

            // Sever XAML-wired event handlers to allow garbage collection
            ViewerControl.CloseRequested -= ViewerControl_CloseRequested;
            ViewerControl.ToggleFullscreenRequested -= ViewerControl_ToggleFullscreenRequested;
            ViewerControl.SizeToImageRequested -= ViewerControl_SizeToImageRequested;
            ViewerControl.MinimizeRequested -= ViewerControl_MinimizeRequested;
            ViewerControl.DeviceLostRestoring -= ViewerControl_DeviceLostRestoring;
            ViewerControl.EditRequested -= ViewerControl_EditRequested;
            ViewerControl.RenameRequested -= ViewerControl_RenameRequested;
            ViewerControl.DeleteRequested -= ViewerControl_DeleteRequested;
            ViewerControl.ShowInExplorerRequested -= ViewerControl_ShowInExplorerRequested;
        }

        private void ApplyInitialZoomOrFit()
        {
            bool isMaxOrFullscreen = (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ||
                                     (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized);

            if (isMaxOrFullscreen) ViewerControl.FitToWindow();
            else ViewerControl.SetZoom(_initialZoom);
        }

        private async void LoadImageAsync()
        {
            if (string.IsNullOrEmpty(_imageItem.Path)) return;
            int myLoadId = ++_currentImageLoadId;

            try
            {
                App.GlobalImageCache.TryGetValue(_imageItem.Path, out var sharedEntry);
                if (sharedEntry != null && (sharedEntry.GpuBitmap != null || sharedEntry.AnimationFrames != null) && sharedEntry.IsHighFidelity)
                {
                    // Checkout a reference from the global cache
                    sharedEntry.AddRef();
                    _localCacheEntry?.Release();
                    _localCacheEntry = sharedEntry;

                    ViewerControl.InjectImage(sharedEntry, false);
                    ApplyInitialZoomOrFit();
                    return;
                }

                var hfEntry = await ViewerEngine.DecodeHighFidelityAsync(_imageItem.Path);
                if (myLoadId != _currentImageLoadId || hfEntry == null)
                {
                    hfEntry?.Release();
                    return;
                }

                bool success = ViewerEngine.FinalizeHighFidelityGpuResources(hfEntry);

                if (!success || myLoadId != _currentImageLoadId)
                {
                    hfEntry.Release();
                    return;
                }

                _localCacheEntry?.Release();
                _localCacheEntry = hfEntry;

                ViewerControl.InjectImage(hfEntry, false);
                ApplyInitialZoomOrFit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Detached Load Failed: {ex.Message}");
            }
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
        private void ViewerControl_ToggleFullscreenRequested(object? sender, EventArgs e) => ToggleFullscreen();
        private void ViewerControl_DeviceLostRestoring(object? sender, EventArgs e) => LoadImageAsync();

        private void ViewerControl_ShowInExplorerRequested(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_imageItem.Path))
            {
                Process.Start("explorer.exe", $"/select,\"{_imageItem.Path}\"");
            }
        }

        private void ViewerControl_MinimizeRequested(object? sender, EventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.ShowWindow(hwnd, 6);
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
            if (!string.IsNullOrEmpty(_imageItem.Path))
            {
                try
                {
                    string editor = "mspaint.exe";
                    if (MainWindow.Instance != null && !string.IsNullOrWhiteSpace(MainWindow.Instance.ImageEditorPath))
                    {
                        editor = MainWindow.Instance.ImageEditorPath;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = $"\"{_imageItem.Path}\"",
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
                this.Close();
            }
        }
    }
}
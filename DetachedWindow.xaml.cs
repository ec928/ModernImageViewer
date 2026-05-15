using Microsoft.Graphics.Canvas;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

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

        // Manual drag state
        private bool _isWindowDragging = false;
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

            // Force WinUI 3 to consume the system title bar space to eliminate phantom chrome
            this.ExtendsContentIntoTitleBar = true;

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

        // --- Manual Dragging Logic Restored ---
        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (props.IsLeftButtonPressed && !e.Handled)
            {
                _isWindowDragging = true;
                NativeMethods.GetCursorPos(out _dragStartCursorPos);
                _dragStartWindowPos = _appWindow.Position;
                RootGrid.CapturePointer(e.Pointer);
            }
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isWindowDragging)
            {
                NativeMethods.GetCursorPos(out NativeMethods.POINT currentCursorPos);
                int dx = currentCursorPos.X - _dragStartCursorPos.X;
                int dy = currentCursorPos.Y - _dragStartCursorPos.Y;
                _appWindow.Move(new Windows.Graphics.PointInt32(_dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy));
            }
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isWindowDragging)
            {
                _isWindowDragging = false;
                RootGrid.ReleasePointerCapture(e.Pointer);
            }
        }

        private void Global_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { this.Close(); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.F11) { ToggleFullscreen(); e.Handled = true; }
        }

        private void DetachedWindow_Closed(object sender, WindowEventArgs args)
        {
            RootGrid.RemoveHandler(UIElement.KeyDownEvent, _globalKeyDownHandler);
            MainWindow.Instance?.UnregisterDetachedWindow(_imageItem.Path);
            _localCacheEntry?.Dispose();
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
                if (sharedEntry != null && sharedEntry.GpuBitmap != null && sharedEntry.IsHighFidelity)
                {
                    ViewerControl.InjectGpuBitmap(sharedEntry.GpuBitmap, sharedEntry.Profile, sharedEntry.NativeWidth, sharedEntry.NativeHeight, true, false);
                    ApplyInitialZoomOrFit();
                    return;
                }

                var hfEntry = await ViewerEngine.DecodeHighFidelityAsync(_imageItem.Path);
                if (myLoadId != _currentImageLoadId) { hfEntry?.Dispose(); return; }

                if (hfEntry != null && hfEntry.Bitmap != null)
                {
                    var canvasDevice = CanvasDevice.GetSharedDevice();
                    if (canvasDevice == null) return;

                    var newBitmap = CanvasBitmap.CreateFromSoftwareBitmap(canvasDevice, hfEntry.Bitmap);
                    hfEntry.Bitmap.Dispose();
                    hfEntry.Bitmap = null;
                    hfEntry.GpuBitmap = newBitmap;

                    _localCacheEntry?.Dispose();
                    _localCacheEntry = hfEntry;

                    ViewerControl.InjectGpuBitmap(newBitmap, hfEntry.Profile, hfEntry.NativeWidth, hfEntry.NativeHeight, true, false);
                    ApplyInitialZoomOrFit();
                }
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

        private void ViewerControl_CloseRequested(object sender, EventArgs e) => this.Close();
        private void ViewerControl_ToggleFullscreenRequested(object sender, EventArgs e) => ToggleFullscreen();
        private void ViewerControl_DeviceLostRestoring(object sender, EventArgs e) => LoadImageAsync();

        private void ViewerControl_MinimizeRequested(object sender, EventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.ShowWindow(hwnd, 6); // 6 = SW_MINIMIZE
        }

        private void ViewerControl_SizeToImageRequested(object sender, EventArgs e)
        {
            if (ViewerControl.LogicalWidth <= 0 || ViewerControl.LogicalHeight <= 0) return;

            int chromeW = _appWindow.Size.Width - _appWindow.ClientSize.Width;
            int chromeH = _appWindow.Size.Height - _appWindow.ClientSize.Height;

            double reqClientW = ViewerControl.LogicalWidth * ViewerControl.CurrentZoom;
            double reqClientH = ViewerControl.LogicalHeight * ViewerControl.CurrentZoom;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

            double totalW = Math.Min(reqClientW + chromeW, displayArea.WorkArea.Width);
            double totalH = Math.Min(reqClientH + chromeH, displayArea.WorkArea.Height);

            _appWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(totalW), (int)Math.Round(totalH)));
        }
    }
}
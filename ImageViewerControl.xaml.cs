using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;

namespace ModernImageViewer
{
    public sealed partial class ImageViewerControl : UserControl
    {
        // --- Dependency Properties ---
        public static readonly DependencyProperty TargetImageProperty =
            DependencyProperty.Register("TargetImage", typeof(ImageItem), typeof(ImageViewerControl), new PropertyMetadata(null, OnTargetImageChanged));

        public ImageItem TargetImage
        {
            get => (ImageItem)GetValue(TargetImageProperty);
            set => SetValue(TargetImageProperty, value);
        }

        public static readonly DependencyProperty AllowDetachProperty =
            DependencyProperty.Register("AllowDetach", typeof(bool), typeof(ImageViewerControl), new PropertyMetadata(true, OnAllowDetachChanged));

        public bool AllowDetach
        {
            get => (bool)GetValue(AllowDetachProperty);
            set => SetValue(AllowDetachProperty, value);
        }

        public static readonly DependencyProperty IsStandaloneModeProperty =
            DependencyProperty.Register("IsStandaloneMode", typeof(bool), typeof(ImageViewerControl), new PropertyMetadata(false, OnIsStandaloneModeChanged));

        public bool IsStandaloneMode
        {
            get => (bool)GetValue(IsStandaloneModeProperty);
            set => SetValue(IsStandaloneModeProperty, value);
        }

        // --- Exposing Internal State for Host Windows ---
        public double LogicalWidth => _logicalImageWidth;
        public double LogicalHeight => _logicalImageHeight;
        public float CurrentZoom => _targetZoom;

        // --- Events for Parent Windows ---
        public event EventHandler? CloseRequested;
        public event EventHandler<Point>? DetachRequested;
        public event EventHandler? ToggleFullscreenRequested;
        public event EventHandler? MinimizeRequested;
        public event EventHandler<int>? NavigateRequested;
        public event EventHandler? SizeToImageRequested;
        public event EventHandler? ShowInExplorerRequested;
        public event EventHandler? DeviceLostRestoring;
        public event EventHandler<int>? DetachLimitRequested;

        // Tear-Off Pipeline
        public event EventHandler<Point>? TearOffInitiated;
        public event EventHandler? TearOffMoved;
        public event EventHandler? TearOffCompleted;

        // --- Internal State ---
        private CanvasDevice? _canvasDevice;
        private CanvasBitmap? _rawGpuBitmap;
        private ColorManagementProfile? _rawGpuProfile;
        private EffectStack _effectStack = new();

        private float _targetZoom = 1.0f;
        private float _currentFitFactor = 1.0f;
        private float _currentGamma = 2.2f;
        private int _currentDetachLimitInt = 0;
        private double _logicalImageWidth = 1;
        private double _logicalImageHeight = 1;

        private bool _isZoomMode = false;
        private bool _isDragging = false;
        private bool _isHighFidelityActive = false;
        private bool _previewResourcesLoaded = false;
        private bool _isFitToWindow = true;

        // Tear-Off State
        private bool _isPotentialTearOff = false;
        private bool _isTearingOffActive = false;
        private Point _tearOffStartPoint;
        private uint _tearOffPointerId;

        private double _tempPanX = 0;
        private double _tempPanY = 0;
        private Point _lastPoint;
        private uint _dragPointerId;
        private Point _lastHudTriggerPos;
        private int _mouseWheelAccumulator = 0;

        private DispatcherTimer? _hudTimer;
        private DispatcherTimer? _scrollQualityTimer;

        public ImageViewerControl()
        {
            this.InitializeComponent();

            _canvasDevice = CanvasDevice.GetSharedDevice();
            if (_canvasDevice != null) _canvasDevice.DeviceLost += CanvasDevice_DeviceLost;
            _effectStack.Initialize();

            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hudTimer.Tick += (s, e) =>
            {
                UnifiedHud.IsHitTestVisible = false;
                FadeOutStoryboard.Begin();
                _hudTimer.Stop();
            };

            _scrollQualityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _scrollQualityTimer.Tick += (s, e) =>
            {
                _scrollQualityTimer.Stop();
                if (_effectStack.Transform != null) _effectStack.Transform.InterpolationMode = CanvasImageInterpolation.HighQualityCubic;
                PreviewCanvas?.Invalidate();
            };

            this.Unloaded += ImageViewerControl_Unloaded;
            this.Loaded += (s, e) => SyncContextMenus();
        }

        private void SyncContextMenus()
        {
            SetGammaLevel(_currentGamma);
            SetDetachLimitState(_currentDetachLimitInt);
        }

        private static void OnTargetImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewerControl control) control.ResetPanState();
        }

        private static void OnAllowDetachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewerControl control)
            {
                bool allow = (bool)e.NewValue;
                control.DetachButton.Visibility = allow ? Visibility.Visible : Visibility.Collapsed;
                control.DetachMenuItem.Visibility = allow ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static void OnIsStandaloneModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewerControl control)
            {
                bool standalone = (bool)e.NewValue;
                control.SizeToImageMenuItem.Visibility = standalone ? Visibility.Visible : Visibility.Collapsed;
                control.MinimizeButton.Visibility = standalone ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ImageViewerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_canvasDevice != null) _canvasDevice.DeviceLost -= CanvasDevice_DeviceLost;
            _hudTimer?.Stop();
            _scrollQualityTimer?.Stop();
            _effectStack.Dispose();
            PreviewCanvas?.RemoveFromVisualTree();
        }

        private void CanvasDevice_DeviceLost(CanvasDevice sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_canvasDevice != null) _canvasDevice.DeviceLost -= CanvasDevice_DeviceLost;
                _canvasDevice = CanvasDevice.GetSharedDevice();
                if (_canvasDevice != null) _canvasDevice.DeviceLost += CanvasDevice_DeviceLost;
                _effectStack.Dispose();
                _effectStack.Initialize();

                DeviceLostRestoring?.Invoke(this, EventArgs.Empty);
            });
        }

        private void ResetPanState()
        {
            _tempPanX = 0;
            _tempPanY = 0;
            ShowHud();
        }

        // --- TIERED RENDERING METHOD WITH STATE SYNC FIX ---
        public void PrepareForNewImage(Microsoft.UI.Xaml.Media.ImageSource? thumbnail)
        {
            _rawGpuBitmap = null;
            _rawGpuProfile = null;
            _previewResourcesLoaded = false;

            // Reset the fidelity state before the TargetImage property updates the HUD
            _isHighFidelityActive = false;

            PreviewCanvas?.Invalidate(); // Instantly clear the old Win2D frame

            // Instantly show the thumbnail in the XAML image layer behind the canvas
            if (thumbnail != null)
            {
                if (PlaceholderImage != null)
                {
                    PlaceholderImage.Source = thumbnail;
                    PlaceholderImage.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (PlaceholderImage != null)
                {
                    PlaceholderImage.Source = null;
                    PlaceholderImage.Visibility = Visibility.Collapsed;
                }
            }
        }

        public void InjectGpuBitmap(CanvasBitmap bitmap, ColorManagementProfile? profile, double nativeW, double nativeH, bool isHighFidelity, bool resetView = true)
        {
            _rawGpuBitmap = bitmap;
            _rawGpuProfile = profile;
            _logicalImageWidth = nativeW > 0 ? nativeW : (bitmap?.SizeInPixels.Width ?? 1);
            _logicalImageHeight = nativeH > 0 ? nativeH : (bitmap?.SizeInPixels.Height ?? 1);
            _isHighFidelityActive = isHighFidelity;
            _previewResourcesLoaded = true;

            if (_effectStack.Crop != null && _rawGpuBitmap != null)
            {
                _effectStack.Crop.Source = _rawGpuBitmap;
                _effectStack.Crop.SourceRectangle = _rawGpuBitmap.Bounds;
            }

            UpdateGridSize();
            if (resetView)
            {
                FitToWindow();
            }

            // Hide the thumbnail once Win2D is ready to take over
            if (PlaceholderImage != null)
            {
                PlaceholderImage.Visibility = Visibility.Collapsed;
            }

            PreviewCanvas?.Invalidate();
            ShowHud();
        }

        public void SetGammaLevel(float gamma)
        {
            _currentGamma = gamma;
            if (Gamma16 != null) Gamma16.IsChecked = Math.Abs(gamma - 1.6f) < 0.01f;
            if (Gamma18 != null) Gamma18.IsChecked = Math.Abs(gamma - 1.8f) < 0.01f;
            if (Gamma20 != null) Gamma20.IsChecked = Math.Abs(gamma - 2.0f) < 0.01f;
            if (Gamma22 != null) Gamma22.IsChecked = Math.Abs(gamma - 2.2f) < 0.01f;
            if (Gamma24 != null) Gamma24.IsChecked = Math.Abs(gamma - 2.4f) < 0.01f;
            PreviewCanvas?.Invalidate();
        }

        public void SetZoom(float targetZoom)
        {
            _isFitToWindow = false;
            _targetZoom = targetZoom;
            if (ImageScroll != null)
            {
                ImageScroll.ChangeView(null, null, _targetZoom, true);
            }
            ShowHud();
        }

        private void UpdateGridSize()
        {
            if (PreviewCanvas != null && ScrollViewportGrid != null)
            {
                float dpiScale = PreviewCanvas.Dpi / 96.0f;
                ScrollViewportGrid.Width = Math.Max(1, _logicalImageWidth / dpiScale);
                ScrollViewportGrid.Height = Math.Max(1, _logicalImageHeight / dpiScale);
            }
        }

        public void FitToWindow()
        {
            if (_logicalImageWidth > 0 && _logicalImageHeight > 0 && PreviewCanvas != null && ImageScroll != null && RootGrid != null)
            {
                _isFitToWindow = true;

                double viewW = RootGrid.ActualWidth;
                double viewH = RootGrid.ActualHeight;

                if (viewW > 0 && viewH > 0)
                {
                    float dpiScale = PreviewCanvas.Dpi / 96.0f;
                    double fitW = viewW / (_logicalImageWidth / dpiScale);
                    double fitH = viewH / (_logicalImageHeight / dpiScale);

                    _currentFitFactor = Math.Clamp((float)Math.Min(fitW, fitH), (float)ImageScroll.MinZoomFactor, (float)ImageScroll.MaxZoomFactor);
                    _targetZoom = _currentFitFactor;
                    ImageScroll.ChangeView(0, 0, _currentFitFactor, true);
                }
            }
        }

        private void PreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_rawGpuBitmap == null || !_previewResourcesLoaded || _effectStack.Crop == null || _effectStack.ColorManagement == null || _effectStack.DecodeToLinear == null || _effectStack.UserGamma == null || _effectStack.Transform == null || _effectStack.EncodeToSrgb == null) return;
            try
            {
                ViewerMath.DrawMappedImage(args.DrawingSession, sender.ActualWidth, sender.ActualHeight, _logicalImageWidth, _logicalImageHeight, -(ImageScroll?.HorizontalOffset ?? 0) + _tempPanX, -(ImageScroll?.VerticalOffset ?? 0) + _tempPanY, _targetZoom, _currentGamma, sender.Dpi / 96.0f, _isHighFidelityActive, _rawGpuBitmap, _rawGpuProfile, _effectStack.Crop, _effectStack.ColorManagement, _effectStack.DecodeToLinear, _effectStack.UserGamma, _effectStack.Transform, _effectStack.EncodeToSrgb);
            }
            catch (Exception ex) { Debug.WriteLine($"[Win2D Draw Error]: {ex.Message}"); }
        }

        public void ShowHud()
        {
            if (TargetImage == null) return;
            if (HudFileName != null) HudFileName.Text = !string.IsNullOrWhiteSpace(TargetImage.Name) ? TargetImage.Name : " ";
            if (HudSecondaryInfo != null) HudSecondaryInfo.Text = TargetImage.GetHudDisplayString(_logicalImageWidth, _logicalImageHeight);
            if (HudZoomInfo != null) HudZoomInfo.Text = TargetImage.GetZoomString(_targetZoom);
            if (HudFidelity != null) HudFidelity.Text = _isHighFidelityActive ? "HF" : "LF";

            if (TopLeftInfoOverlay != null) TopLeftInfoOverlay.IsHitTestVisible = false;
            if (UnifiedHud != null) UnifiedHud.IsHitTestVisible = true;
            if (FadeInStoryboard != null) FadeInStoryboard.Begin();
            _hudTimer?.Stop();
            _hudTimer?.Start();
        }

        // --- Interaction Handlers ---
        private void ToggleZoomMode(bool? forceState = null)
        {
            _isZoomMode = forceState ?? !_isZoomMode;
            if (ZoomModeIcon != null) ZoomModeIcon.Glyph = _isZoomMode ? "\uE71F" : "\uE71E";
            if (ZoomModeButton != null) ZoomModeButton.Background = _isZoomMode ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ShowHud();
        }

        private void ToggleZoomMode_Click(object sender, RoutedEventArgs e) => ToggleZoomMode();
        private void SizeToWindow_Click(object sender, RoutedEventArgs e) { ToggleZoomMode(false); FitToWindow(); }
        private void ZoomToActualSize_Click(object sender, RoutedEventArgs e)
        {
            ToggleZoomMode(true);
            _isFitToWindow = false;
            _targetZoom = 1.0f;
            ImageScroll?.ChangeView(ImageScroll.ScrollableWidth / 2, ImageScroll.ScrollableHeight / 2, _targetZoom, true);
        }

        private void InputOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (InputOverlay == null) return;
            var prop = e.GetCurrentPoint(InputOverlay).Properties;
            bool isCtrlHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (prop.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed || prop.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed)
            {
                ToggleZoomMode();
                e.Handled = true;
                return;
            }

            if (prop.IsMiddleButtonPressed)
            {
                ToggleZoomMode(false);
                FitToWindow();
                e.Handled = true;
                return;
            }

            if ((_isZoomMode || isCtrlHeld) && prop.IsLeftButtonPressed)
            {
                _isDragging = true;
                _lastPoint = e.GetCurrentPoint(InputOverlay).Position;
                _dragPointerId = e.Pointer.PointerId;
                InputOverlay.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            else if (!_isZoomMode && prop.IsLeftButtonPressed && AllowDetach && !IsStandaloneMode)
            {
                _isPotentialTearOff = true;
                _isTearingOffActive = false;
                _tearOffStartPoint = e.GetCurrentPoint(InputOverlay).Position;
                _tearOffPointerId = e.Pointer.PointerId;
                InputOverlay.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void InputOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (InputOverlay == null) return;
            var currentPos = e.GetCurrentPoint(InputOverlay).Position;
            if (Math.Abs(currentPos.X - _lastHudTriggerPos.X) > 10 || Math.Abs(currentPos.Y - _lastHudTriggerPos.Y) > 10)
            {
                _lastHudTriggerPos = currentPos;
                ShowHud();
            }

            if (_isDragging && e.Pointer.PointerId == _dragPointerId && ImageScroll != null)
            {
                var panResult = ViewerMath.CalculateDragPan(_tempPanX, _tempPanY, currentPos.X, currentPos.Y, _lastPoint.X, _lastPoint.Y, ImageScroll.HorizontalOffset, ImageScroll.ScrollableWidth, ImageScroll.VerticalOffset, ImageScroll.ScrollableHeight);
                _tempPanX = panResult.TempPanX;
                _tempPanY = panResult.TempPanY;
                _lastPoint = currentPos;

                if (_effectStack.Transform != null) _effectStack.Transform.InterpolationMode = CanvasImageInterpolation.Linear;
                _scrollQualityTimer?.Stop();
                _scrollQualityTimer?.Start();
                PreviewCanvas?.Invalidate();
            }
            else if (_isPotentialTearOff && !_isTearingOffActive && e.Pointer.PointerId == _tearOffPointerId)
            {
                if (Math.Abs(currentPos.X - _tearOffStartPoint.X) > 15 || Math.Abs(currentPos.Y - _tearOffStartPoint.Y) > 15)
                {
                    _isPotentialTearOff = false;
                    _isTearingOffActive = true;
                    TearOffInitiated?.Invoke(this, _tearOffStartPoint);
                }
            }
            else if (_isTearingOffActive && e.Pointer.PointerId == _tearOffPointerId)
            {
                TearOffMoved?.Invoke(this, EventArgs.Empty);
            }
        }

        private void InputOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (InputOverlay == null) return;
            if (_isDragging && ImageScroll != null)
            {
                _isDragging = false;
                InputOverlay.ReleasePointerCapture(e.Pointer);
                ImageScroll.ChangeView(ImageScroll.HorizontalOffset - _tempPanX, ImageScroll.VerticalOffset - _tempPanY, null, true);
                _tempPanX = 0; _tempPanY = 0;
                if (_effectStack.Transform != null) _effectStack.Transform.InterpolationMode = _isHighFidelityActive ? CanvasImageInterpolation.HighQualityCubic : CanvasImageInterpolation.Linear;
                PreviewCanvas?.Invalidate();
            }
            else if (_isTearingOffActive && e.Pointer.PointerId == _tearOffPointerId)
            {
                _isTearingOffActive = false;
                InputOverlay.ReleasePointerCapture(e.Pointer);
                TearOffCompleted?.Invoke(this, EventArgs.Empty);
            }
            else if (_isPotentialTearOff && e.Pointer.PointerId == _tearOffPointerId)
            {
                _isPotentialTearOff = false;
                InputOverlay.ReleasePointerCapture(e.Pointer);
                var pointerUpdate = e.GetCurrentPoint(InputOverlay).Properties.PointerUpdateKind;
                if (pointerUpdate == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void InputOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (ImageScroll == null) return;
            var ptr = e.GetCurrentPoint(ImageScroll);
            bool isCtrlHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (!IsStandaloneMode && !_isZoomMode && !isCtrlHeld)
            {
                e.Handled = true;
                _mouseWheelAccumulator += ptr.Properties.MouseWheelDelta;
                if (_mouseWheelAccumulator >= 40) { NavigateRequested?.Invoke(this, -1); _mouseWheelAccumulator = 0; }
                else if (_mouseWheelAccumulator <= -40) { NavigateRequested?.Invoke(this, 1); _mouseWheelAccumulator = 0; }
                return;
            }

            if (!_isZoomMode && (isCtrlHeld || IsStandaloneMode)) ToggleZoomMode(true);

            if (!IsStandaloneMode && ptr.Properties.MouseWheelDelta < 0 && _targetZoom <= _currentFitFactor + 0.05f)
            {
                ToggleZoomMode(false);
                NavigateRequested?.Invoke(this, 1);
                e.Handled = true;
                return;
            }

            var result = ViewerMath.CalculateWheelZoom(_targetZoom, (float)ImageScroll.MinZoomFactor, (float)ImageScroll.MaxZoomFactor, ptr.Properties.MouseWheelDelta, ptr.Position.X, ptr.Position.Y, ImageScroll.HorizontalOffset, ImageScroll.VerticalOffset);

            _isFitToWindow = false;
            _targetZoom = result.NewZoom;

            ImageScroll.ChangeView(result.NewOffsetX, result.NewOffsetY, _targetZoom, true);
            ShowHud();
            e.Handled = true;
        }

        private void ImageScroll_ViewChanged(object s, ScrollViewerViewChangedEventArgs e) => PreviewCanvas?.Invalidate();

        private void ImageScroll_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (_isFitToWindow) FitToWindow();
            PreviewCanvas?.Invalidate();
        }

        private void SetGamma_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem i && float.TryParse(i.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gamma))
            {
                SetGammaLevel(gamma);
            }
        }

        private void SetDetachLimit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem i && int.TryParse(i.Tag?.ToString(), out int limit))
            {
                DetachLimitRequested?.Invoke(this, limit);
            }
        }

        public void SetDetachLimitState(int limit)
        {
            _currentDetachLimitInt = limit;
            if (LimitZoom != null) LimitZoom.IsChecked = limit == 0;
            if (Limit1080 != null) Limit1080.IsChecked = limit == 1;
            if (Limit1200 != null) Limit1200.IsChecked = limit == 2;
            if (Limit800 != null) Limit800.IsChecked = limit == 3;
        }

        // Broadcast Events to Parent
        private void ShowInExplorer_Click(object sender, RoutedEventArgs e) => ShowInExplorerRequested?.Invoke(this, EventArgs.Empty);
        private void ClosePreview_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
        private void DetachImage_Click(object sender, RoutedEventArgs e) => DetachRequested?.Invoke(this, _lastHudTriggerPos);
        private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);
        private void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

        private void SizeToImage_Click(object sender, RoutedEventArgs e)
        {
            ToggleZoomMode(false);
            SizeToImageRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
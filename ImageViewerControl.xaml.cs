using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Linq;
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

        // --- File Operation Events ---
        public event EventHandler? EditRequested;
        public event EventHandler? RenameRequested;
        public event EventHandler? DeleteRequested;
        public event EventHandler? AddToCollageRequested;

        // Tear-Off Pipeline
        public event EventHandler<Point>? TearOffInitiated;
        public event EventHandler? TearOffMoved;
        public event EventHandler? TearOffCompleted;

        // --- Internal State ---
        private CanvasDevice? _canvasDevice;
        private CanvasBitmap? _rawGpuBitmap;
        private ColorManagementProfile? _rawGpuProfile;
        private EffectStack _effectStack = new();

        // --- Animation State ---
        private DispatcherTimer? _playbackTimer;
        private AnimationFrame[]? _animationFrames;
        private int _currentFrameIndex = 0;
        private bool _isPlayingAnimation = true;

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
        private bool _isRenderQueued = false;
        private bool _isPointerOverHud = false;

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

            _playbackTimer = new DispatcherTimer();
            _playbackTimer.Tick += PlaybackTimer_Tick;

            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hudTimer.Tick += (s, e) =>
            {
                if (_isPointerOverHud) return;

                UnifiedHud.IsHitTestVisible = false;
                TopLeftInfoOverlay.IsHitTestVisible = false;
                if (AnimationHud != null) AnimationHud.IsHitTestVisible = false;
                FadeOutStoryboard.Begin();
                _hudTimer.Stop();
            };

            _scrollQualityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _scrollQualityTimer.Tick += (s, e) =>
            {
                _scrollQualityTimer.Stop();
                if (_effectStack.Transform != null) _effectStack.Transform.InterpolationMode = CanvasImageInterpolation.HighQualityCubic;
                RequestRender();
            };

            this.Unloaded += ImageViewerControl_Unloaded;
            this.Loaded += (s, e) => SyncContextMenus();
        }

        private void SyncContextMenus()
        {
            SetGammaLevel(_currentGamma);
            SetDetachLimitState(_currentDetachLimitInt);
        }

        private void RequestRender()
        {
            if (!_isRenderQueued && PreviewCanvas != null)
            {
                _isRenderQueued = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    _isRenderQueued = false;
                    PreviewCanvas.Invalidate();
                });
            }
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

                var separator = control.FindName("SizeToImageSeparator") as MenuFlyoutSeparator;
                if (separator != null) separator.Visibility = standalone ? Visibility.Visible : Visibility.Collapsed;

                control.MinimizeButton.Visibility = standalone ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ImageViewerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_canvasDevice != null) _canvasDevice.DeviceLost -= CanvasDevice_DeviceLost;
            _playbackTimer?.Stop();
            _hudTimer?.Stop();
            _scrollQualityTimer?.Stop();
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
            _mouseWheelAccumulator = 0;
            ShowHud();
        }

        public void PrepareForNewImage(Microsoft.UI.Xaml.Media.ImageSource? thumbnail)
        {
            _playbackTimer?.Stop();
            _animationFrames = null;
            _rawGpuBitmap = null;
            _rawGpuProfile = null;
            _previewResourcesLoaded = false;
            _isHighFidelityActive = false;

            _isPlayingAnimation = true;
            if (AnimationHud != null) AnimationHud.Visibility = Visibility.Collapsed;

            if (_effectStack.Crop != null) _effectStack.Crop.Source = null;

            RequestRender();

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

        private void PlaybackTimer_Tick(object? sender, object e)
        {
            if (_animationFrames == null || _animationFrames.Length == 0) return;

            _currentFrameIndex = (_currentFrameIndex + 1) % _animationFrames.Length;
            if (_playbackTimer != null)
            {
                var nextDelay = _animationFrames[_currentFrameIndex].Delay;
                if (_playbackTimer.Interval != nextDelay)
                {
                    _playbackTimer.Interval = nextDelay;
                }
            }
            RequestRender();
            UpdateAnimCounter();
        }

        public void InjectImage(ImageCacheEntry entry, bool resetView = true)
        {
            _rawGpuBitmap = entry.GpuBitmap;
            _animationFrames = entry.AnimationFrames;
            _rawGpuProfile = entry.Profile;
            _logicalImageWidth = entry.NativeWidth > 0 ? entry.NativeWidth : 1;
            _logicalImageHeight = entry.NativeHeight > 0 ? entry.NativeHeight : 1;
            _isHighFidelityActive = entry.IsHighFidelity;
            _previewResourcesLoaded = true;

            if (_animationFrames != null && _animationFrames.Length > 1)
            {
                _currentFrameIndex = 0;

                if (AnimationHud != null) AnimationHud.Visibility = Visibility.Visible;
                UpdateAnimCounter();
                if (AnimPlayPauseIcon != null) AnimPlayPauseIcon.Symbol = Symbol.Pause;

                if (_playbackTimer != null)
                {
                    _playbackTimer.Interval = _animationFrames[0].Delay;
                    _playbackTimer.Start();
                }

                var firstGpuBitmap = _animationFrames[0].GpuBitmap;
                if (_effectStack.Crop != null && firstGpuBitmap != null)
                {
                    _effectStack.Crop.Source = firstGpuBitmap;
                    _effectStack.Crop.SourceRectangle = firstGpuBitmap.Bounds;
                }
            }
            else
            {
                _playbackTimer?.Stop();
                if (_effectStack.Crop != null && _rawGpuBitmap != null)
                {
                    _effectStack.Crop.Source = _rawGpuBitmap;
                    _effectStack.Crop.SourceRectangle = _rawGpuBitmap.Bounds;
                }
            }

            UpdateGridSize();
            if (resetView)
            {
                FitToWindow();
            }

            if (PlaceholderImage != null)
            {
                PlaceholderImage.Visibility = Visibility.Collapsed;
            }

            RequestRender();
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
            RequestRender();
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
            if (!_previewResourcesLoaded) return;

            var crop = _effectStack.Crop;
            var colorMgmt = _effectStack.ColorManagement;
            var decode = _effectStack.DecodeToLinear;
            var userGamma = _effectStack.UserGamma;
            var transform = _effectStack.Transform;
            var encode = _effectStack.EncodeToSrgb;

            if (crop == null || colorMgmt == null || decode == null || userGamma == null || transform == null || encode == null) return;

            CanvasBitmap? currentTarget = _rawGpuBitmap;

            if (_animationFrames != null && _currentFrameIndex >= 0 && _currentFrameIndex < _animationFrames.Length)
            {
                var frame = _animationFrames[_currentFrameIndex];
                if (frame != null) currentTarget = frame.GpuBitmap;
            }

            if (currentTarget == null) return;

            crop.Source = currentTarget;

            try
            {
                ViewerMath.DrawMappedImage(args.DrawingSession, sender.ActualWidth, sender.ActualHeight, _logicalImageWidth, _logicalImageHeight, -(ImageScroll?.HorizontalOffset ?? 0) + _tempPanX, -(ImageScroll?.VerticalOffset ?? 0) + _tempPanY, _targetZoom, _currentGamma, sender.Dpi / 96.0f, _isHighFidelityActive, currentTarget, _rawGpuProfile, crop, colorMgmt, decode, userGamma, transform, encode);
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
            if (AnimationHud != null && AnimationHud.Visibility == Visibility.Visible) AnimationHud.IsHitTestVisible = true;

            if (FadeInStoryboard != null) FadeInStoryboard.Begin();
            _hudTimer?.Stop();
            _hudTimer?.Start();
        }

        public void ShowNotification(string message)
        {
            if (HudFileName != null) HudFileName.Text = message;
            if (HudSecondaryInfo != null) HudSecondaryInfo.Text = string.Empty;
            if (HudZoomInfo != null) HudZoomInfo.Text = string.Empty;
            if (HudFidelity != null) HudFidelity.Text = string.Empty;

            if (TopLeftInfoOverlay != null) TopLeftInfoOverlay.IsHitTestVisible = false;
            if (UnifiedHud != null) UnifiedHud.IsHitTestVisible = true;

            if (FadeInStoryboard != null) FadeInStoryboard.Begin();
            _hudTimer?.Stop();
            _hudTimer?.Start();
        }

        // --- Interaction Handlers ---
        private void Hud_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOverHud = true;
            _hudTimer?.Stop();
        }

        private void Hud_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOverHud = false;
            _hudTimer?.Start();
        }

        public bool HandleAnimationKeystroke(Windows.System.VirtualKey key)
        {
            if (_animationFrames == null || _animationFrames.Length <= 1) return false;

            if (key == Windows.System.VirtualKey.Space)
            {
                AnimPlayPause_Click(this, null!);
                return true;
            }

            int keyCode = (int)key;

            if (keyCode == 188) // Comma / <
            {
                AnimPrev_Click(this, null!);
                return true;
            }
            if (keyCode == 190) // Period / >
            {
                AnimNext_Click(this, null!);
                return true;
            }

            return false;
        }

        private void ToggleZoomMode(bool? forceState = null)
        {
            _isZoomMode = forceState ?? !_isZoomMode;
            if (ZoomModeIcon != null) ZoomModeIcon.Glyph = _isZoomMode ? "\uE71F" : "\uE71E";
            if (ZoomModeButton != null) ZoomModeButton.Background = _isZoomMode ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ShowHud();
        }

        private void ToggleZoomMode_Click(object sender, RoutedEventArgs e) => ToggleZoomMode();

        private void SizeToWindow_Click(object sender, RoutedEventArgs e)
        {
            ToggleZoomMode(false);
            FitToWindow();
        }

        private void ZoomToActualSize_Click(object sender, RoutedEventArgs e)
        {
            ToggleZoomMode(true);
            _isFitToWindow = false;
            _targetZoom = 1.0f;
            ImageScroll?.ChangeView(ImageScroll.ScrollableWidth / 2, ImageScroll.ScrollableHeight / 2, _targetZoom, true);
        }

        private bool IsPointerOverBackground(Point ptr)
        {
            if (ImageScroll == null || PreviewCanvas == null) return true;

            float dpiScale = PreviewCanvas.Dpi / 96.0f;
            double displayedImgW = (_logicalImageWidth / dpiScale) * _targetZoom;
            double displayedImgH = (_logicalImageHeight / dpiScale) * _targetZoom;

            double viewW = ImageScroll.ActualWidth;
            double viewH = ImageScroll.ActualHeight;

            double startX = displayedImgW < viewW ? (viewW - displayedImgW) / 2.0 : 0;
            double endX = displayedImgW < viewW ? startX + displayedImgW : viewW;

            double startY = displayedImgH < viewH ? (viewH - displayedImgH) / 2.0 : 0;
            double endY = displayedImgH < viewH ? startY + displayedImgH : viewH;

            return ptr.X < startX || ptr.X > endX || ptr.Y < startY || ptr.Y > endY;
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
                bool isBackgroundClick = ImageScroll != null && IsPointerOverBackground(e.GetCurrentPoint(ImageScroll).Position);
                if (isBackgroundClick && IsStandaloneMode) return;

                _isDragging = true;
                _lastPoint = e.GetCurrentPoint(InputOverlay).Position;
                _dragPointerId = e.Pointer.PointerId;
                InputOverlay.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            else if (!_isZoomMode && prop.IsLeftButtonPressed)
            {
                if (AllowDetach && !IsStandaloneMode)
                {
                    _isPotentialTearOff = true;
                    _isTearingOffActive = false;
                    _tearOffStartPoint = e.GetCurrentPoint(InputOverlay).Position;
                    _tearOffPointerId = e.Pointer.PointerId;
                    InputOverlay.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
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
                RequestRender();
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
                _tempPanX = 0;
                _tempPanY = 0;
                if (_effectStack.Transform != null) _effectStack.Transform.InterpolationMode = _isHighFidelityActive ? CanvasImageInterpolation.HighQualityCubic : CanvasImageInterpolation.Linear;
                RequestRender();
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

                if (pointerUpdate == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased && ImageScroll != null)
                {
                    bool isBackgroundClick = IsPointerOverBackground(e.GetCurrentPoint(ImageScroll).Position);

                    if (isBackgroundClick)
                    {
                        CloseRequested?.Invoke(this, EventArgs.Empty);
                        e.Handled = true;
                    }
                }
            }
        }

        private void InputOverlay_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (InputOverlay == null) return;
            if (_isDragging)
            {
                _isDragging = false;
                InputOverlay.ReleasePointerCapture(e.Pointer);
                _tempPanX = 0;
                _tempPanY = 0;
                if (_effectStack.Transform != null) _effectStack.Transform.InterpolationMode = _isHighFidelityActive ? CanvasImageInterpolation.HighQualityCubic : CanvasImageInterpolation.Linear;
                RequestRender();
            }
            if (_isTearingOffActive)
            {
                _isTearingOffActive = false;
                InputOverlay.ReleasePointerCapture(e.Pointer);
                TearOffCompleted?.Invoke(this, EventArgs.Empty);
            }
            else if (_isPotentialTearOff)
            {
                _isPotentialTearOff = false;
                InputOverlay.ReleasePointerCapture(e.Pointer);
            }
        }

        private void InputOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (UnifiedHud != null)
            {
                if (UnifiedHud.Opacity > 0.5)
                {
                    UnifiedHud.IsHitTestVisible = false;
                    TopLeftInfoOverlay.IsHitTestVisible = false;
                    if (AnimationHud != null) AnimationHud.IsHitTestVisible = false;
                    FadeOutStoryboard.Begin();
                }
                else
                {
                    ShowHud();
                }
            }
            e.Handled = true;
        }

        private void InputOverlay_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ImageScroll == null) return;

            float zoomInTarget = 1.0f;
            if (_currentFitFactor >= 0.75f)
            {
                zoomInTarget = _currentFitFactor * 2.0f;
            }
            zoomInTarget = (float)Math.Min(ImageScroll.MaxZoomFactor, zoomInTarget);

            if (_targetZoom < zoomInTarget - 0.01f)
            {
                ToggleZoomMode(true);
                _isFitToWindow = false;

                var ptr = e.GetPosition(ImageScroll);
                float newZoom = zoomInTarget;

                float currentNativeZoom = ImageScroll.ZoomFactor;
                float dpiScale = PreviewCanvas != null ? PreviewCanvas.Dpi / 96.0f : 1.0f;

                double currentDisplayedW = (_logicalImageWidth / dpiScale) * currentNativeZoom;
                double currentDisplayedH = (_logicalImageHeight / dpiScale) * currentNativeZoom;

                double currentBlankOffsetX = currentDisplayedW < ImageScroll.ViewportWidth ? (ImageScroll.ViewportWidth - currentDisplayedW) / 2.0 : 0;
                double currentBlankOffsetY = currentDisplayedH < ImageScroll.ViewportHeight ? (ImageScroll.ViewportHeight - currentDisplayedH) / 2.0 : 0;

                double absoluteX = ptr.X + ImageScroll.HorizontalOffset;
                double absoluteY = ptr.Y + ImageScroll.VerticalOffset;

                double logicalClickX = (absoluteX - currentBlankOffsetX) / currentNativeZoom;
                double logicalClickY = (absoluteY - currentBlankOffsetY) / currentNativeZoom;

                double newDisplayedW = (_logicalImageWidth / dpiScale) * newZoom;
                double newDisplayedH = (_logicalImageHeight / dpiScale) * newZoom;

                double newBlankOffsetX = newDisplayedW < ImageScroll.ViewportWidth ? (ImageScroll.ViewportWidth - newDisplayedW) / 2.0 : 0;
                double newBlankOffsetY = newDisplayedH < ImageScroll.ViewportHeight ? (ImageScroll.ViewportHeight - newDisplayedH) / 2.0 : 0;

                double targetX = (logicalClickX * newZoom) + newBlankOffsetX - ptr.X;
                double targetY = (logicalClickY * newZoom) + newBlankOffsetY - ptr.Y;

                _targetZoom = newZoom;
                ImageScroll.ChangeView(targetX, targetY, _targetZoom, true);
            }
            else
            {
                ToggleZoomMode(false);
                FitToWindow();
            }

            e.Handled = true;
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

            _isFitToWindow = false;
            float dpiScale = PreviewCanvas != null ? PreviewCanvas.Dpi / 96.0f : 1.0f;

            // CS8602 fix: Enforced ImageScroll! to assure the compiler of the prior null-check
            var result = ViewerMath.CalculateWheelZoom(
                _targetZoom, ImageScroll!.ZoomFactor,
                (float)ImageScroll.MinZoomFactor, (float)ImageScroll.MaxZoomFactor,
                ptr.Properties.MouseWheelDelta, ptr.Position.X, ptr.Position.Y,
                ImageScroll.HorizontalOffset, ImageScroll.VerticalOffset,
                _logicalImageWidth, _logicalImageHeight, dpiScale,
                ImageScroll.ViewportWidth, ImageScroll.ViewportHeight);

            _targetZoom = result.NewZoom;
            ImageScroll.ChangeView(result.NewOffsetX, result.NewOffsetY, _targetZoom, true);

            if (_targetZoom <= _currentFitFactor + 0.01f)
            {
                ToggleZoomMode(false);
                _isFitToWindow = true;
            }

            ShowHud();
            e.Handled = true;
        }

        private void ImageScroll_ViewChanged(object s, ScrollViewerViewChangedEventArgs e) => RequestRender();

        private void ImageScroll_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (_isFitToWindow) FitToWindow();
            RequestRender();
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

        // --- File Operations Handlers ---
        private void AddToCollage_Click(object sender, RoutedEventArgs e)
        {
            AddToCollageRequested?.Invoke(this, EventArgs.Empty);
        }

        private void PlayCinematic_Click(object sender, RoutedEventArgs e)
        {
            var main = MainWindow.Instance;
            if (main == null || TargetImage == null) return;

            var currentFiles = main.Images.Select(img => img.Path).ToList();
            int startIndex = main.Images.IndexOf(TargetImage);

            var cinematicWindow = new ModernImageViewer.Cinematic.CinematicWindow(currentFiles, startIndex);
        }

        private void EditImage_Click(object sender, RoutedEventArgs e)
        {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RenameImage_Click(object sender, RoutedEventArgs e)
        {
            RenameRequested?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteImage_Click(object sender, RoutedEventArgs e)
        {
            DeleteRequested?.Invoke(this, EventArgs.Empty);
        }

        // --- Animation HUD Handlers ---
        private void UpdateAnimCounter()
        {
            if (_animationFrames != null && AnimFrameCounter != null)
            {
                AnimFrameCounter.Text = $"{_currentFrameIndex + 1} / {_animationFrames.Length}";
            }
        }

        private void AnimPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_animationFrames == null) return;
            PauseAnimation();
            _currentFrameIndex = _currentFrameIndex - 1 < 0 ? _animationFrames.Length - 1 : _currentFrameIndex - 1;
            RequestRender();
            UpdateAnimCounter();
        }

        private void AnimNext_Click(object sender, RoutedEventArgs e)
        {
            if (_animationFrames == null) return;
            PauseAnimation();
            _currentFrameIndex = (_currentFrameIndex + 1) % _animationFrames.Length;
            RequestRender();
            UpdateAnimCounter();
        }

        private void AnimPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlayingAnimation) PauseAnimation();
            else PlayAnimation();
        }

        private void PauseAnimation()
        {
            _isPlayingAnimation = false;
            _playbackTimer?.Stop();
            if (AnimPlayPauseIcon != null) AnimPlayPauseIcon.Symbol = Symbol.Play;
        }

        private void PlayAnimation()
        {
            _isPlayingAnimation = true;
            if (_animationFrames != null && _playbackTimer != null)
            {
                _playbackTimer.Interval = _animationFrames[_currentFrameIndex].Delay;
                _playbackTimer.Start();
            }
            if (AnimPlayPauseIcon != null) AnimPlayPauseIcon.Symbol = Symbol.Pause;
        }
    }
}
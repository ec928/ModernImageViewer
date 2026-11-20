using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ModernImageViewer.Contracts;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;

namespace ModernImageViewer
{
    public enum Viewer3DMotionMode
    {
        Sway,
        Slide,
        Orbit,
        Breathe,
        Tilt
    }

    public sealed record ViewerEffectSnapshot(
        bool SwayEnabled,
        Viewer3DMotionMode MotionMode,
        bool Paused,
        double FocusBlur,
        double FocusDepth,
        bool HasCustomFocusDepth,
        double BgDesat,
        double BgDim,
        double Relight,
        int RotationQuarterTurns,
        bool MirrorHorizontal,
        bool FlipVertical,
        bool Grayscale,
        bool Invert,
        bool SlideLoop = true,
        bool SlideReverse = false);

    public sealed partial class ImageViewerControl
    {
        private enum StudioTab
        {
            Motion,
            Focus,
            Pop
        }

        private const int MaxSwayLongestSide = 3072;
        private const double SwayRampSeconds = 0.6;
        private const int SwayFaultsBeforeStop = 45;
        private const float DepthLensMaxBlurOffset = 40f;

        private sealed class ViewerSwayState : IDisposable
        {
            public required string Path { get; init; }
            public required IParallaxMap DepthMap { get; init; }
            public IParallaxMask? SubjectMask { get; set; }
            public byte[]? CrispBytes { get; set; }
            public int CrispWidth { get; set; }
            public int CrispHeight { get; set; }
            public float Middle => DepthMap.Middle;

            public double Phase;
            public double Clock = -1;
            public float PointerCurrentX;
            public float PointerTargetX;
            public float PointerCurrentY;
            public float PointerTargetY;
            public double PointerClock = -1;
            public Stopwatch Shown { get; } = Stopwatch.StartNew();

            public bool LensDirty = true;
            public float LastLensLightX = float.NaN;
            public float LastLensLightY = float.NaN;

            public CanvasBitmap? SourceBitmapRef;
            public CanvasRenderTarget? PhotoSurface;
            public CanvasRenderTarget? LensSurface;
            public CanvasRenderTarget? OutputSurface;
            public CanvasBitmap? DepthBitmap;
            public CanvasBitmap? CrispBitmap;
            public CanvasBitmap? MaskBitmap;
            public ScaleEffect? DepthScale;
            public BorderEffect? DepthAtSize;
            public ScaleEffect? CrispScale;
            public BorderEffect? CrispAtSize;
            public ScaleEffect? MaskScale;
            public BorderEffect? MaskAtSize;
            public BorderEffect? PhotoAtEdges;
            public BorderEffect? LensAtEdges;
            public PixelShaderEffect? DepthLens;
            public ColorMatrixEffect? Shift;
            public DisplacementMapEffect? Moved;
            public PixelShaderEffect? Splat2D;
            public float SplatStep;
            public bool HasMask;

            public ParallaxLayers.Layers? SlideLayers { get; set; }
            public int SlideLayerWidth { get; set; }
            public int SlideLayerHeight { get; set; }
            public bool SlideLayersAttempted { get; set; }
            public bool SlideSupported { get; set; }
            public bool SlideDone { get; set; }
            public float SlideWay { get; set; } = 1f;
            public CanvasBitmap? SlidePlateBitmap { get; set; }
            public BorderEffect? SlidePlateBorder { get; set; }
            public CanvasBitmap? SlideMaskBitmap { get; set; }
            public AlphaMaskEffect? SlideNearEffect { get; set; }
            public GaussianBlurEffect? SlidePlateBlur { get; set; }
            public SaturationEffect? SlidePlateDesat { get; set; }
            public ColorMatrixEffect? SlidePlateDim { get; set; }

            public void DisposeSlideGpuResources()
            {
                SlidePlateDim?.Dispose(); SlidePlateDim = null;
                SlidePlateDesat?.Dispose(); SlidePlateDesat = null;
                SlidePlateBlur?.Dispose(); SlidePlateBlur = null;
                SlideNearEffect?.Dispose(); SlideNearEffect = null;
                SlideMaskBitmap?.Dispose(); SlideMaskBitmap = null;
                SlidePlateBorder?.Dispose(); SlidePlateBorder = null;
                SlidePlateBitmap?.Dispose(); SlidePlateBitmap = null;
            }

            public void DisposeGpuPipeline()
            {
                DisposeSlideGpuResources();
                Splat2D?.Dispose(); Splat2D = null;
                Moved?.Dispose(); Moved = null;
                Shift?.Dispose(); Shift = null;
                DepthLens?.Dispose(); DepthLens = null;
                LensAtEdges?.Dispose(); LensAtEdges = null;
                PhotoAtEdges?.Dispose(); PhotoAtEdges = null;
                MaskAtSize?.Dispose(); MaskAtSize = null;
                MaskScale?.Dispose(); MaskScale = null;
                CrispAtSize?.Dispose(); CrispAtSize = null;
                CrispScale?.Dispose(); CrispScale = null;
                DepthAtSize?.Dispose(); DepthAtSize = null;
                DepthScale?.Dispose(); DepthScale = null;
                MaskBitmap?.Dispose(); MaskBitmap = null;
                CrispBitmap?.Dispose(); CrispBitmap = null;
                DepthBitmap?.Dispose(); DepthBitmap = null;
                OutputSurface?.Dispose(); OutputSurface = null;
                LensSurface?.Dispose(); LensSurface = null;
                PhotoSurface?.Dispose(); PhotoSurface = null;
                SourceBitmapRef = null;
                LensDirty = true;
                LastLensLightX = float.NaN;
                LastLensLightY = float.NaN;
            }

            public void Dispose() => DisposeGpuPipeline();
        }

        private bool _isSwayEnabled;
        private bool _swayPaused;
        private bool _swayPreparing;
        private bool _swayUnloaded;
        private bool _swayRenderingSubscribed;
        private bool _updatingStudioSliders;
        private bool _swayPrefsDirty;
        private bool _swayPickingFocus;
        private bool _hasCustomFocusDepth;
        private bool _swayRefiningMask;
        private TaskCompletionSource<bool>? _swayMaskWake;
        private bool _isComparingOriginal;
        private bool _isCopyingImage;
        private bool _swayBuildingSlide;
        private bool _slideLoop = true;
        private bool _slideReverse;

        private Viewer3DMotionMode _motionMode = Viewer3DMotionMode.Sway;
        private StudioTab _studioTab = StudioTab.Motion;

        private double _swayDepth = SurpriseOptions.SwayRotationDefault;
        private double _swaySwing = SurpriseOptions.SwaySwingDefault;
        private double _focusBlur;
        private double _focusDepth = 0.5;
        private double _bgDesat;
        private double _bgDim;
        private double _relight;

        private int _swayTicket;
        private int _swayDrawFaults;
        private double _lastHudWidth;
        private CancellationTokenSource? _swayCts;
        private DispatcherTimer? _swaySaveTimer;
        private ViewerSwayState? _swayState;
        private readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);
        private readonly SolidColorBrush _subtlePillBrush = new(Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

        public bool IsSwayEnabled => _isSwayEnabled;
        public bool IsSwayFollowPointer => _motionMode == Viewer3DMotionMode.Tilt;
        public bool IsComparingOriginal => _isComparingOriginal;
        private bool IsPickFocusModeActive => _swayPickingFocus && _isSwayEnabled && CanUseSway && _swayState != null;
        private bool IsLensActive => _focusBlur > 0.001 || _bgDesat > 0.001 || _bgDim > 0.001 || _relight > 0.001;

        private bool CanUseSway =>
            DepthMaps.Available
            && TargetImage != null
            && !TargetImage.IsComicEntry
            && !string.IsNullOrEmpty(TargetImage.Path)
            && (_animationFrames == null || _animationFrames.Length <= 1)
            && !IsShowingTwoPages;

        private void InitializeSway()
        {
            (_swayDepth, _swaySwing) = SwayPreferences.Load();
            SwayPreferences.Changed += OnExternalSwayPreferencesChanged;
            _swaySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _swaySaveTimer.Tick += (_, _) =>
            {
                _swaySaveTimer?.Stop();
                FlushSwayPreferences();
            };
            if (SwayCompareButton != null)
            {
                SwayCompareButton.AddHandler(PointerPressedEvent, new PointerEventHandler(SwayCompareButton_PointerPressed), true);
                SwayCompareButton.AddHandler(PointerReleasedEvent, new PointerEventHandler(SwayCompareButton_PointerReleased), true);
                SwayCompareButton.PointerCaptureLost += SwayCompareButton_PointerReleased;
                SwayCompareButton.PointerCanceled += SwayCompareButton_PointerReleased;
            }
            SyncStudioSliders();
        }

        private void OnExternalSwayPreferencesChanged(double depth, double swing)
        {
            void Apply()
            {
                if (_swayUnloaded) return;
                if (Math.Abs(_swayDepth - depth) <= 1e-6 && Math.Abs(_swaySwing - swing) <= 1e-6) return;
                _swayDepth = depth;
                _swaySwing = swing;
                SyncStudioSliders();
                if (_swayState != null && !_swayRenderingSubscribed)
                {
                    DrawStaticSwayFrame();
                }
            }

            if (DispatcherQueue == null) return;
            if (DispatcherQueue.HasThreadAccess) Apply();
            else DispatcherQueue.TryEnqueue(Apply);
        }

        private void ScheduleSwayPreferencesSave()
        {
            _swayPrefsDirty = true;
            _swaySaveTimer?.Stop();
            _swaySaveTimer?.Start();
        }

        private void FlushSwayPreferences()
        {
            if (!_swayPrefsDirty) return;
            _swayPrefsDirty = false;
            SwayPreferences.Save(_swayDepth, _swaySwing);
        }

        private void CleanupSwayOnUnload()
        {
            _swayUnloaded = true;
            _isComparingOriginal = false;
            SwayPreferences.Changed -= OnExternalSwayPreferencesChanged;
            _swaySaveTimer?.Stop();
            FlushSwayPreferences();
            StopSwayRuntime(requestRender: false);
        }

        private void HandleSwayDeviceLost()
        {
            StopSwayRuntime(requestRender: false);
        }

        public ViewerEffectSnapshot CaptureEffectSnapshot() => new(
            _isSwayEnabled,
            _motionMode,
            _swayPaused,
            _focusBlur,
            _focusDepth,
            _hasCustomFocusDepth,
            _bgDesat,
            _bgDim,
            _relight,
            ViewRotationQuarterTurns,
            ViewMirrorHorizontal,
            ViewFlipVertical,
            ViewGrayscale,
            ViewInvert,
            _slideLoop,
            _slideReverse);

        public void ApplyInitialEffects(ViewerEffectSnapshot? snapshot)
        {
            if (snapshot == null) return;
            _isSwayEnabled = snapshot.SwayEnabled;
            _motionMode = snapshot.MotionMode;
            _swayPaused = snapshot.Paused;
            _slideLoop = snapshot.SlideLoop;
            _slideReverse = snapshot.SlideReverse;
            _focusBlur = Math.Clamp(snapshot.FocusBlur, 0.0, 1.0);
            _focusDepth = Math.Clamp(snapshot.FocusDepth, 0.0, 1.0);
            _hasCustomFocusDepth = snapshot.HasCustomFocusDepth;
            _bgDesat = Math.Clamp(snapshot.BgDesat, 0.0, 1.0);
            _bgDim = Math.Clamp(snapshot.BgDim, 0.0, 1.0);
            _relight = Math.Clamp(snapshot.Relight, 0.0, 1.0);
            ApplyViewTransformAndTone(
                snapshot.RotationQuarterTurns,
                snapshot.MirrorHorizontal,
                snapshot.FlipVertical,
                snapshot.Grayscale,
                snapshot.Invert);
            SyncSwayChrome();
        }

        public void ResetSwayForClose()
        {
            _isSwayEnabled = false;
            _swayPaused = false;
            _swayPickingFocus = false;
            _hasCustomFocusDepth = false;
            _isComparingOriginal = false;
            _swayBuildingSlide = false;
            _slideLoop = true;
            _slideReverse = false;
            _motionMode = Viewer3DMotionMode.Sway;
            _studioTab = StudioTab.Motion;
            _focusBlur = 0.0;
            _bgDesat = 0.0;
            _bgDim = 0.0;
            _relight = 0.0;
            ResetViewTransformAndTone(requestRender: false);
            StopSwayRuntime(requestRender: false);
            SyncSwayChrome();
        }

        public void SetInitialSway(bool enabled, bool followPointer = false)
        {
            _isSwayEnabled = enabled;
            _motionMode = followPointer ? Viewer3DMotionMode.Tilt : Viewer3DMotionMode.Sway;
            _swayPaused = false;
            SyncSwayChrome();
        }

        public void On3DExtensionChanged()
        {
            if (!DepthMaps.Available && _isSwayEnabled)
            {
                _isSwayEnabled = false;
                _swayPaused = false;
                _swayPickingFocus = false;
                StopSwayRuntime(requestRender: true);
            }
            UpdateSwayAvailability();
        }

        private void UpdateSwayAvailability()
        {
            bool available = CanUseSway;
            Visibility vis = available ? Visibility.Visible : Visibility.Collapsed;
            if (SwayButton != null) SwayButton.Visibility = vis;
            if (SwayMenuItem != null) SwayMenuItem.Visibility = vis;
            SyncSwayChrome();
        }

        private void SyncStudioSliders()
        {
            _updatingStudioSliders = true;
            try
            {
                if (SwayDepthSlider != null) SwayDepthSlider.Value = Math.Round(_swayDepth * 100.0, 1);
                if (SwayDepthValue != null) SwayDepthValue.Text = $"{_swayDepth * 100.0:0.0}%";
                if (SwaySwingSlider != null) SwaySwingSlider.Value = Math.Round(_swaySwing * 4.0) / 4.0;
                if (SwaySwingValue != null) SwaySwingValue.Text = $"{_swaySwing:0.##} s";

                if (FocusBlurSlider != null) FocusBlurSlider.Value = Math.Round(_focusBlur * 100.0);
                if (FocusBlurValue != null) FocusBlurValue.Text = $"{Math.Round(_focusBlur * 100.0):0}%";
                if (FocusPlaneSlider != null) FocusPlaneSlider.Value = Math.Round(_focusDepth * 100.0);
                if (FocusPlaneValue != null) FocusPlaneValue.Text = $"{Math.Round(_focusDepth * 100.0):0}%";

                if (PopColorSlider != null) PopColorSlider.Value = Math.Round(_bgDesat * 100.0);
                if (PopColorValue != null) PopColorValue.Text = $"{Math.Round(_bgDesat * 100.0):0}%";
                if (PopDimSlider != null) PopDimSlider.Value = Math.Round(_bgDim * 100.0);
                if (PopDimValue != null) PopDimValue.Text = $"{Math.Round(_bgDim * 100.0):0}%";
                if (PopLightSlider != null) PopLightSlider.Value = Math.Round(_relight * 100.0);
                if (PopLightValue != null) PopLightValue.Text = $"{Math.Round(_relight * 100.0):0}%";
            }
            finally
            {
                _updatingStudioSliders = false;
            }
        }

        private void ApplyPillStyle(Button? btn, bool active, bool subtleDot = false)
        {
            if (btn == null) return;
            if (active)
            {
                btn.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                btn.Opacity = 1.0;
            }
            else if (subtleDot)
            {
                btn.Background = _subtlePillBrush;
                btn.Opacity = 1.0;
            }
            else
            {
                btn.Background = _transparentBrush;
                btn.Opacity = 0.82;
            }
        }

        private void SyncSwayChrome()
        {
            bool active = _isSwayEnabled && CanUseSway;
            var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            var transparent = _transparentBrush;

            if (SwayButton != null) SwayButton.Background = active ? accentBrush : transparent;
            if (SwayMenuItem != null) SwayMenuItem.IsChecked = active;

            bool showHud = active && (_swayPreparing || _swayState != null);
            if (SwayHud != null)
            {
                SwayHud.Visibility = showHud ? Visibility.Visible : Visibility.Collapsed;
                SwayHud.IsHitTestVisible = showHud && UnifiedHud != null && UnifiedHud.Opacity > 0.5;
            }

            if (SwayLoadingPanel != null) SwayLoadingPanel.Visibility = _swayPreparing ? Visibility.Visible : Visibility.Collapsed;
            if (SwayProgressRing != null) SwayProgressRing.IsActive = _swayPreparing;
            if (SwayControlsPanel != null) SwayControlsPanel.Visibility = (!_swayPreparing && _swayState != null) ? Visibility.Visible : Visibility.Collapsed;

            bool hasFocus = _focusBlur > 0.001;
            bool hasPop = _bgDesat > 0.001 || _bgDim > 0.001 || _relight > 0.001;
            ApplyPillStyle(TabMotionButton, _studioTab == StudioTab.Motion);
            ApplyPillStyle(TabFocusButton, _studioTab == StudioTab.Focus, hasFocus);
            ApplyPillStyle(TabPopButton, _studioTab == StudioTab.Pop, hasPop);

            if (MotionTabPanel != null) MotionTabPanel.Visibility = _studioTab == StudioTab.Motion ? Visibility.Visible : Visibility.Collapsed;
            if (FocusTabPanel != null) FocusTabPanel.Visibility = _studioTab == StudioTab.Focus ? Visibility.Visible : Visibility.Collapsed;
            if (PopTabPanel != null) PopTabPanel.Visibility = _studioTab == StudioTab.Pop ? Visibility.Visible : Visibility.Collapsed;
            if (SwayResetEffectsButton != null) SwayResetEffectsButton.Visibility = (hasFocus || hasPop) ? Visibility.Visible : Visibility.Collapsed;
            if (SwayMaskRefiningText != null)
            {
                if (_swayBuildingSlide && _motionMode == Viewer3DMotionMode.Slide && _swayState != null)
                {
                    SwayMaskRefiningText.Text = "Preparing slide...";
                    SwayMaskRefiningText.Visibility = Visibility.Visible;
                }
                else if (_studioTab == StudioTab.Pop && _swayRefiningMask && _swayState != null && !_swayState.HasMask)
                {
                    SwayMaskRefiningText.Text = "Refining cutout...";
                    SwayMaskRefiningText.Visibility = Visibility.Visible;
                }
                else
                {
                    SwayMaskRefiningText.Visibility = Visibility.Collapsed;
                }
            }
            if (SwayCompareButton != null)
                SwayCompareButton.Background = _isComparingOriginal ? accentBrush : transparent;

            ApplyPillStyle(ModeSwayButton, _motionMode == Viewer3DMotionMode.Sway);
            ApplyPillStyle(ModeSlideButton, _motionMode == Viewer3DMotionMode.Slide);
            ApplyPillStyle(ModeOrbitButton, _motionMode == Viewer3DMotionMode.Orbit);
            ApplyPillStyle(ModeBreatheButton, _motionMode == Viewer3DMotionMode.Breathe);
            ApplyPillStyle(ModeTiltButton, _motionMode == Viewer3DMotionMode.Tilt);

            bool isSlide = _motionMode == Viewer3DMotionMode.Slide;
            bool isSlideOneWay = isSlide && !_slideLoop;
            if (SlideOptionsPanel != null)
                SlideOptionsPanel.Visibility = (_studioTab == StudioTab.Motion && isSlide && _swayState != null) ? Visibility.Visible : Visibility.Collapsed;
            if (SlideLoopButton != null)
            {
                SlideLoopButton.Content = _slideLoop ? "Loop" : "Once";
                string loopName = _slideLoop ? "Slide loop" : "Slide once";
                ToolTipService.SetToolTip(SlideLoopButton, _slideLoop ? "Looping one-way slide (click to glide once)" : "Single one-way glide (click to loop continuously)");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SlideLoopButton, loopName);
                ApplyPillStyle(SlideLoopButton, _slideLoop, subtleDot: !_slideLoop);
            }
            if (SlideReverseButton != null)
                SlideReverseButton.Background = _slideReverse ? accentBrush : transparent;
            if (SwaySwingLabel != null)
                SwaySwingLabel.Text = isSlide ? "Time" : "Swing";
            if (SwaySwingGroup != null)
                ToolTipService.SetToolTip(SwaySwingGroup, isSlide ? "Slide pass duration in seconds (default 4.25 s)" : "Each swing: seconds for one full cycle (default 4.25 s)");

            bool isTilt = _motionMode == Viewer3DMotionMode.Tilt;
            bool slideFinished = isSlideOneWay && _swayState?.SlideDone == true && _swayState.SlideNearEffect != null;
            bool showPlay = _swayPaused || isTilt || slideFinished;
            if (SwayPlayPauseIcon != null) SwayPlayPauseIcon.Symbol = showPlay ? Symbol.Play : Symbol.Pause;
            if (SwayPlayPauseButton != null)
            {
                string label = slideFinished
                    ? "Replay slide (Space)"
                    : (showPlay ? "Play motion (Space)" : "Pause motion (Space)");
                ToolTipService.SetToolTip(SwayPlayPauseButton, label);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SwayPlayPauseButton, label);
            }

            if (SwaySwingGroup != null) SwaySwingGroup.Opacity = isTilt ? 0.4 : 1.0;
            if (SwaySwingSlider != null) SwaySwingSlider.IsEnabled = !isTilt;
            if (PickFocusButton != null) PickFocusButton.Background = _swayPickingFocus ? accentBrush : transparent;

            SyncStudioSliders();
        }

        private void UpdateSwayHudLayout(double availableWidth)
        {
            if (availableWidth > 0) _lastHudWidth = availableWidth;
            double w = _lastHudWidth > 0 ? _lastHudWidth : availableWidth;
            if (SwayHudBorder == null || MotionTabPanel == null) return;

            bool compactMotion = w > 0 && w < 620;
            bool ultraNarrow = w > 0 && w < 410;
            if (SwayDepthGroup != null && SwaySwingGroup != null && MotionModeGroup != null)
            {
                if (compactMotion)
                {
                    Grid.SetColumnSpan(MotionModeGroup, 2);
                    SwayDivider1.Visibility = Visibility.Collapsed;
                    SwayDivider2.Visibility = Visibility.Collapsed;
                    if (ultraNarrow)
                    {
                        Grid.SetRow(SwayDepthGroup, 1); Grid.SetColumn(SwayDepthGroup, 0); Grid.SetColumnSpan(SwayDepthGroup, 2);
                        SwayDepthGroup.HorizontalAlignment = HorizontalAlignment.Center;
                        Grid.SetRow(SwaySwingGroup, 2); Grid.SetColumn(SwaySwingGroup, 0); Grid.SetColumnSpan(SwaySwingGroup, 2);
                        SwaySwingGroup.HorizontalAlignment = HorizontalAlignment.Center;
                        SwayDepthSlider.Width = 100;
                        SwaySwingSlider.Width = 100;
                    }
                    else
                    {
                        Grid.SetRow(SwayDepthGroup, 1); Grid.SetColumn(SwayDepthGroup, 0); Grid.SetColumnSpan(SwayDepthGroup, 1);
                        SwayDepthGroup.HorizontalAlignment = HorizontalAlignment.Left;
                        Grid.SetRow(SwaySwingGroup, 1); Grid.SetColumn(SwaySwingGroup, 1); Grid.SetColumnSpan(SwaySwingGroup, 1);
                        SwaySwingGroup.HorizontalAlignment = HorizontalAlignment.Left;
                        SwayDepthSlider.Width = 72;
                        SwaySwingSlider.Width = 66;
                    }
                }
                else
                {
                    Grid.SetColumnSpan(MotionModeGroup, 1);
                    Grid.SetRow(SwayDepthGroup, 0); Grid.SetColumn(SwayDepthGroup, 1); Grid.SetColumnSpan(SwayDepthGroup, 1);
                    SwayDepthGroup.HorizontalAlignment = HorizontalAlignment.Left;
                    Grid.SetRow(SwaySwingGroup, 0); Grid.SetColumn(SwaySwingGroup, 2); Grid.SetColumnSpan(SwaySwingGroup, 1);
                    SwaySwingGroup.HorizontalAlignment = HorizontalAlignment.Left;
                    SwayDivider1.Visibility = Visibility.Visible;
                    SwayDivider2.Visibility = Visibility.Visible;
                    SwayDepthSlider.Width = 80;
                    SwaySwingSlider.Width = 70;
                }
            }

            if (FocusPlaneGroup != null && FocusDivider2 != null && FocusBlurSlider != null && FocusPlaneSlider != null)
            {
                bool compactFocus = w > 0 && w < 480;
                if (compactFocus)
                {
                    Grid.SetRow(FocusPlaneGroup, 1);
                    Grid.SetColumn(FocusPlaneGroup, 0);
                    Grid.SetColumnSpan(FocusPlaneGroup, 2);
                    FocusPlaneGroup.HorizontalAlignment = HorizontalAlignment.Center;
                    FocusDivider2.Visibility = Visibility.Collapsed;
                    FocusBlurSlider.Width = 84;
                    FocusPlaneSlider.Width = 110;
                }
                else
                {
                    Grid.SetRow(FocusPlaneGroup, 0);
                    Grid.SetColumn(FocusPlaneGroup, 2);
                    Grid.SetColumnSpan(FocusPlaneGroup, 1);
                    FocusPlaneGroup.HorizontalAlignment = HorizontalAlignment.Left;
                    FocusDivider2.Visibility = Visibility.Visible;
                    FocusBlurSlider.Width = 96;
                    FocusPlaneSlider.Width = 96;
                }
            }

            if (PopLightGroup != null && PopDivider2 != null && PopColorSlider != null && PopDimSlider != null && PopLightSlider != null)
            {
                bool compactPop = w > 0 && w < 520;
                if (compactPop)
                {
                    Grid.SetRow(PopLightGroup, 1);
                    Grid.SetColumn(PopLightGroup, 0);
                    Grid.SetColumnSpan(PopLightGroup, 3);
                    PopLightGroup.HorizontalAlignment = HorizontalAlignment.Center;
                    PopDivider2.Visibility = Visibility.Collapsed;
                    double sliderW = ultraNarrow ? 58 : 74;
                    PopColorSlider.Width = sliderW;
                    PopDimSlider.Width = sliderW;
                    PopLightSlider.Width = 110;
                }
                else
                {
                    Grid.SetRow(PopLightGroup, 0);
                    Grid.SetColumn(PopLightGroup, 2);
                    Grid.SetColumnSpan(PopLightGroup, 1);
                    PopLightGroup.HorizontalAlignment = HorizontalAlignment.Left;
                    PopDivider2.Visibility = Visibility.Visible;
                    PopColorSlider.Width = 78;
                    PopDimSlider.Width = 78;
                    PopLightSlider.Width = 78;
                }
            }
        }

        private void OnSwayPrepareForNewImage()
        {
            ++_swayTicket;
            _swayCts?.Cancel();
            _swayCts?.Dispose();
            _swayCts = null;
            _swayPreparing = false;
            _swayRefiningMask = false;
            _swayBuildingSlide = false;
            _swayMaskWake = null;
            _swayPickingFocus = false;
            _hasCustomFocusDepth = false;
            _isComparingOriginal = false;
            StopSwayRuntime(requestRender: false);
            UpdateSwayAvailability();
        }

        private void OnSwayImageInjected()
        {
            UpdateSwayAvailability();
            if (!_isSwayEnabled || !CanUseSway || TargetImage == null)
            {
                if (_swayState != null || _swayPreparing)
                {
                    StopSwayRuntime(requestRender: false);
                    SyncSwayChrome();
                }
                return;
            }

            if (_swayState != null && string.Equals(_swayState.Path, TargetImage.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (_rawGpuBitmap != null && !ReferenceEquals(_swayState.SourceBitmapRef, _rawGpuBitmap))
                {
                    var device = _canvasDevice ?? CanvasDevice.GetSharedDevice();
                    if (device != null && RebuildSwayGpuPipeline(_swayState, device, _rawGpuBitmap))
                    {
                        DrawSwayToSurface(_swayState, advanceClock: false);
                        RequestRender();
                        if (_motionMode == Viewer3DMotionMode.Slide && _swayState.SlideNearEffect == null && !_swayBuildingSlide && _swayCts != null)
                            _ = EnsureSlideLayersAsync(_swayState, _swayTicket, _swayCts.Token);
                    }
                }
                return;
            }

            if (!_swayPreparing)
            {
                _ = StartSwayForCurrentImageAsync();
            }
        }

        private CanvasBitmap? TryGetActiveSwayBitmap() =>
            (!_isComparingOriginal && _isSwayEnabled && CanUseSway && _swayState?.OutputSurface != null) ? _swayState.OutputSurface : null;

        private void ToggleSway()
        {
            if (!CanUseSway) return;
            _isSwayEnabled = !_isSwayEnabled;
            if (!_isSwayEnabled)
            {
                _swayPaused = false;
                _swayPickingFocus = false;
                StopSwayRuntime(requestRender: true);
                SyncSwayChrome();
                ShowHud();
                return;
            }

            _swayPaused = false;
            SyncSwayChrome();
            ShowHud();
            if (_previewResourcesLoaded && _rawGpuBitmap != null)
            {
                _ = StartSwayForCurrentImageAsync();
            }
        }

        private async Task StartSwayForCurrentImageAsync()
        {
            if (!_isSwayEnabled || !CanUseSway || TargetImage == null) return;

            // Stop any earlier run first: StopSwayRuntime bumps the ticket and cancels the token, so taking them before
            // it would make this run stale before its first await.
            StopSwayRuntime(requestRender: false);
            int ticket = ++_swayTicket;
            _swayCts = new CancellationTokenSource();
            var token = _swayCts.Token;
            string path = TargetImage.Path;
            var prepClock = Stopwatch.StartNew();

            _swayPreparing = true;
            SyncSwayChrome();
            ShowHud();

            try
            {
                // Stage 1 (Depth-first): await depth before queuing BiRefNet so Depth Anything gets NativeRuntime.Gpu immediately.
                var map = await DepthMaps.ForAsync(path);
                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested)
                {
                    if (ticket == _swayTicket)
                    {
                        _swayPreparing = false;
                        SyncSwayChrome();
                    }
                    return;
                }

                if (map == null || map.Grey.Length == 0)
                {
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    SyncSwayChrome();
                    ShowNotification("Could not build 3D depth for this image");
                    return;
                }

                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || _rawGpuBitmap == null || !CanUseSway)
                {
                    if (ticket == _swayTicket)
                    {
                        _swayPreparing = false;
                        SyncSwayChrome();
                    }
                    return;
                }

                var device = _canvasDevice ?? CanvasDevice.GetSharedDevice();
                if (device == null)
                {
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    SyncSwayChrome();
                    return;
                }

                if (!_hasCustomFocusDepth)
                    _focusDepth = Math.Clamp(map.Middle, 0.0, 1.0);

                var state = new ViewerSwayState
                {
                    Path = path,
                    DepthMap = map
                };

                if (!RebuildSwayGpuPipeline(state, device, _rawGpuBitmap))
                {
                    state.Dispose();
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    SyncSwayChrome();
                    ShowNotification("Could not initialize 3D effects");
                    return;
                }

                DrawSwayToSurface(state, advanceClock: false);
                _swayPreparing = false;
                _swayState = state;
                _swayDrawFaults = 0;
                SyncSwayChrome();
                UpdateSwayHudLayout(RootGrid?.ActualWidth ?? 0);
                UpdateSwayRenderingSubscription();
                RequestRender();
                ShowHud();

                PlayTiming.Write($"[ViewerSway] Live in {prepClock.ElapsedMilliseconds} ms (splat2D={state.Splat2D != null}, depthLens={state.DepthLens != null})");

                // Stage 2 (Non-blocking): refine foreground/background edges & subject mask while 3D is already live.
                _ = RefineSwaySubjectMaskAsync(state, ticket, path, token);
                if (_motionMode == Viewer3DMotionMode.Slide)
                    _ = EnsureSlideLayersAsync(state, ticket, token);
            }
            catch (OperationCanceledException)
            {
                if (ticket == _swayTicket && !_swayUnloaded && _swayPreparing)
                {
                    _swayPreparing = false;
                    SyncSwayChrome();
                }
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"[ViewerSway] Prep failed: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[ViewerSway] Prep failed: {ex.GetType().Name}: {ex.Message}");
                if (ticket == _swayTicket && !_swayUnloaded)
                {
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    StopSwayRuntime(requestRender: false);
                    SyncSwayChrome();
                    ShowNotification("Could not initialize 3D effects");
                }
            }
        }

        private void WakeSubjectMaskRefine() => _swayMaskWake?.TrySetResult(true);

        private async Task RefineSwaySubjectMaskAsync(ViewerSwayState state, int ticket, string path, CancellationToken token)
        {
            if (ticket == _swayTicket && !_swayUnloaded)
            {
                _swayRefiningMask = true;
                SyncSwayChrome();
            }

            try
            {
                if (_studioTab != StudioTab.Pop && _bgDesat <= 0.001 && _bgDim <= 0.001)
                {
                    var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _swayMaskWake = wake;
                    await Task.WhenAny(Task.Delay(700, token), wake.Task);
                    if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state))
                        return;
                }

                var outline = await SubjectMasks.ForAsync(path);
                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state))
                    return;

                if (outline == null || outline.Size <= 0 || outline.Alpha.Length < outline.Size * outline.Size || state.PhotoSurface == null)
                    return;

                int swayW = (int)state.PhotoSurface.SizeInPixels.Width;
                int swayH = (int)state.PhotoSurface.SizeInPixels.Height;
                int crispW = Math.Min(swayW, SwayDepth.MostWidth);
                int crispH = Math.Max(1, (int)Math.Round(swayH * (double)crispW / Math.Max(1, swayW)));

                var refPx = await SwayShader.LoadCrispReferencePixelsAsync(path, crispW, crispH, token);
                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state) || state.PhotoSurface == null)
                    return;

                byte[] greyMap = state.DepthMap.Grey;
                int gw = state.DepthMap.Width, gh = state.DepthMap.Height;
                byte[] alpha = outline.Alpha;
                int maskSize = outline.Size;
                int tw = crispW, th = crispH;
                byte[]? crispBytes = await Task.Run(() =>
                    SwayDepth.Crisp(greyMap, gw, gh, alpha, maskSize, tw, th, refPx?.Pixels, refPx?.Width ?? 0, refPx?.Height ?? 0), token);

                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state) || state.PhotoSurface == null)
                    return;

                ApplySubjectMaskToLiveState(state, outline, crispBytes, crispW, crispH);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"[ViewerSway] Mask refine skipped: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (ticket == _swayTicket && !_swayUnloaded)
                {
                    _swayRefiningMask = false;
                    _swayMaskWake = null;
                    SyncSwayChrome();
                }
            }
        }

        private void ApplySubjectMaskToLiveState(ViewerSwayState state, IParallaxMask outline, byte[]? crispBytes, int crispW, int crispH)
        {
            var device = state.PhotoSurface?.Device;
            if (device == null || state.PhotoSurface == null) return;

            float w = state.PhotoSurface.SizeInPixels.Width;
            float h = state.PhotoSurface.SizeInPixels.Height;

            CanvasBitmap? crispBitmap = null;
            ScaleEffect? crispScale = null;
            BorderEffect? crispAtSize = null;
            if (crispBytes != null && crispW > 0 && crispH > 0)
            {
                crispBitmap = CanvasBitmap.CreateFromBytes(device, crispBytes, crispW, crispH, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                crispScale = new ScaleEffect
                {
                    Source = crispBitmap,
                    Scale = new Vector2(w / crispW, h / crispH),
                    InterpolationMode = CanvasImageInterpolation.Linear
                };
                crispAtSize = new BorderEffect
                {
                    Source = crispScale,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };
            }

            int ms = outline.Size;
            var maskBgra = new byte[ms * ms * 4];
            for (int i = 0; i < ms * ms; i++)
            {
                byte a = outline.Alpha[i];
                int o = i * 4;
                maskBgra[o] = a; maskBgra[o + 1] = a; maskBgra[o + 2] = a; maskBgra[o + 3] = 255;
            }
            var maskBitmap = CanvasBitmap.CreateFromBytes(device, maskBgra, ms, ms, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            var maskScale = new ScaleEffect
            {
                Source = maskBitmap,
                Scale = new Vector2(w / ms, h / ms),
                InterpolationMode = CanvasImageInterpolation.Linear
            };
            var maskAtSize = new BorderEffect
            {
                Source = maskScale,
                ExtendX = CanvasEdgeBehavior.Clamp,
                ExtendY = CanvasEdgeBehavior.Clamp
            };

            var oldCrispAtSize = state.CrispAtSize;
            var oldCrispScale = state.CrispScale;
            var oldCrispBitmap = state.CrispBitmap;
            var oldMaskAtSize = state.MaskAtSize;
            var oldMaskScale = state.MaskScale;
            var oldMaskBitmap = state.MaskBitmap;

            state.SubjectMask = outline;
            state.CrispBytes = crispBytes;
            state.CrispWidth = crispW;
            state.CrispHeight = crispH;
            state.HasMask = true;

            state.CrispBitmap = crispBitmap;
            state.CrispScale = crispScale;
            state.CrispAtSize = crispAtSize;
            state.MaskBitmap = maskBitmap;
            state.MaskScale = maskScale;
            state.MaskAtSize = maskAtSize;

            ICanvasImage splatDepth = (ICanvasImage?)crispAtSize ?? state.DepthAtSize!;
            if (state.DepthLens != null)
            {
                state.DepthLens.Source2 = splatDepth;
                state.DepthLens.Source3 = maskAtSize;
            }
            if (state.Shift != null)
                state.Shift.Source = splatDepth;
            if (state.Splat2D != null)
                state.Splat2D.Source2 = splatDepth;

            oldCrispAtSize?.Dispose();
            oldCrispScale?.Dispose();
            oldCrispBitmap?.Dispose();
            oldMaskAtSize?.Dispose();
            oldMaskScale?.Dispose();
            oldMaskBitmap?.Dispose();

            state.LensDirty = true;
            PlayTiming.Write($"[ViewerSway] Subject mask applied ({outline.Why})");
            if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
        }

        private bool RebuildSwayGpuPipeline(ViewerSwayState state, CanvasDevice device, CanvasBitmap sourceBitmap)
        {
            state.DisposeGpuPipeline();
            try
            {
                var srcSize = sourceBitmap.SizeInPixels;
                var (w, h) = ViewerMath.SwaySurfaceSize((int)srcSize.Width, (int)srcSize.Height, MaxSwayLongestSide);

                var photoSurface = new CanvasRenderTarget(device, w, h, 96f);
                using (var ds = photoSurface.CreateDrawingSession())
                {
                    ds.Clear(Microsoft.UI.Colors.Transparent);
                    ds.DrawImage(sourceBitmap, new Rect(0, 0, w, h), sourceBitmap.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
                }

                var lensSurface = new CanvasRenderTarget(device, w, h, 96f);
                var outputSurface = new CanvasRenderTarget(device, w, h, 96f);

                var map = state.DepthMap;
                var grey = new byte[map.Width * map.Height * 4];
                for (int i = 0; i < map.Grey.Length; i++)
                {
                    byte v = map.Grey[i];
                    int o = i * 4;
                    grey[o] = v; grey[o + 1] = v; grey[o + 2] = v; grey[o + 3] = 255;
                }
                var depthBitmap = CanvasBitmap.CreateFromBytes(device, grey, map.Width, map.Height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                var depthScale = new ScaleEffect
                {
                    Source = depthBitmap,
                    Scale = new Vector2(w / (float)map.Width, h / (float)map.Height),
                    InterpolationMode = CanvasImageInterpolation.HighQualityCubic
                };
                var depthAtSize = new BorderEffect
                {
                    Source = depthScale,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };

                CanvasBitmap? crispBitmap = null;
                ScaleEffect? crispScale = null;
                BorderEffect? crispAtSize = null;
                ICanvasImage splatDepth = depthAtSize;
                if (state.CrispBytes != null && state.CrispWidth > 0 && state.CrispHeight > 0)
                {
                    crispBitmap = CanvasBitmap.CreateFromBytes(device, state.CrispBytes, state.CrispWidth, state.CrispHeight, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    crispScale = new ScaleEffect
                    {
                        Source = crispBitmap,
                        Scale = new Vector2(w / (float)state.CrispWidth, h / (float)state.CrispHeight),
                        InterpolationMode = CanvasImageInterpolation.Linear
                    };
                    crispAtSize = new BorderEffect
                    {
                        Source = crispScale,
                        ExtendX = CanvasEdgeBehavior.Clamp,
                        ExtendY = CanvasEdgeBehavior.Clamp
                    };
                    splatDepth = crispAtSize;
                }

                bool hasMask = state.SubjectMask != null && state.SubjectMask.Size > 0 && state.SubjectMask.Alpha.Length >= state.SubjectMask.Size * state.SubjectMask.Size;
                CanvasBitmap maskBitmap;
                ScaleEffect? maskScale = null;
                BorderEffect maskAtSize;
                if (hasMask)
                {
                    int ms = state.SubjectMask!.Size;
                    var maskBgra = new byte[ms * ms * 4];
                    for (int i = 0; i < ms * ms; i++)
                    {
                        byte a = state.SubjectMask.Alpha[i];
                        int o = i * 4;
                        maskBgra[o] = a; maskBgra[o + 1] = a; maskBgra[o + 2] = a; maskBgra[o + 3] = 255;
                    }
                    maskBitmap = CanvasBitmap.CreateFromBytes(device, maskBgra, ms, ms, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    maskScale = new ScaleEffect
                    {
                        Source = maskBitmap,
                        Scale = new Vector2(w / (float)ms, h / (float)ms),
                        InterpolationMode = CanvasImageInterpolation.Linear
                    };
                    maskAtSize = new BorderEffect
                    {
                        Source = maskScale,
                        ExtendX = CanvasEdgeBehavior.Clamp,
                        ExtendY = CanvasEdgeBehavior.Clamp
                    };
                }
                else
                {
                    maskBitmap = CanvasBitmap.CreateFromBytes(device, new byte[] { 255, 255, 255, 255 }, 1, 1, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    maskAtSize = new BorderEffect
                    {
                        Source = maskBitmap,
                        ExtendX = CanvasEdgeBehavior.Clamp,
                        ExtendY = CanvasEdgeBehavior.Clamp
                    };
                }

                var photoAtEdges = new BorderEffect
                {
                    Source = photoSurface,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };
                var lensAtEdges = new BorderEffect
                {
                    Source = lensSurface,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };

                var depthLens = SwayShader.CreateDepthLens(photoAtEdges, splatDepth, maskAtSize, (int)DepthLensMaxBlurOffset);

                ICanvasImage activeSource = IsLensActive ? lensAtEdges : photoAtEdges;
                var shift = new ColorMatrixEffect
                {
                    Source = splatDepth,
                    ClampOutput = true,
                    AlphaMode = CanvasAlphaMode.Straight
                };
                var moved = new DisplacementMapEffect
                {
                    Source = activeSource,
                    Displacement = shift,
                    XChannelSelect = EffectChannelSelect.Red,
                    YChannelSelect = EffectChannelSelect.Green,
                    Amount = (float)(Math.Max(w, h) * SurpriseOptions.SwayRotationMost * 2.0)
                };
                float splatStep = Math.Max(1f, Math.Max(w, h) / (float)Math.Max(map.Width, map.Height) / 2f);
                float maxSplatOffset = MathF.Sqrt(w * w + h * h) * (float)SurpriseOptions.SwayRotationMost + splatStep * 3f + 4f;
                var splat2D = SwayShader.CreateSplat2D(activeSource, splatDepth, maxSplatOffset);

                state.SourceBitmapRef = sourceBitmap;
                state.PhotoSurface = photoSurface;
                state.LensSurface = lensSurface;
                state.OutputSurface = outputSurface;
                state.DepthBitmap = depthBitmap;
                state.DepthScale = depthScale;
                state.DepthAtSize = depthAtSize;
                state.CrispBitmap = crispBitmap;
                state.CrispScale = crispScale;
                state.CrispAtSize = crispAtSize;
                state.MaskBitmap = maskBitmap;
                state.MaskScale = maskScale;
                state.MaskAtSize = maskAtSize;
                state.PhotoAtEdges = photoAtEdges;
                state.LensAtEdges = lensAtEdges;
                state.DepthLens = depthLens;
                state.Shift = shift;
                state.Moved = moved;
                state.Splat2D = splat2D;
                state.SplatStep = splatStep;
                state.HasMask = hasMask;
                state.LensDirty = true;

                if (state.SlideLayers != null && state.SlideSupported && state.SlideLayerWidth == w && state.SlideLayerHeight == h)
                {
                    AttachSlideGpuResources(state, device, activeSource);
                }
                else if (state.SlideLayers != null && (state.SlideLayerWidth != w || state.SlideLayerHeight != h))
                {
                    state.SlideLayers = null;
                    state.SlideLayersAttempted = false;
                    state.SlideSupported = false;
                }
                return true;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                state.DisposeGpuPipeline();
                return false;
            }
        }

        private void RenderLensSurface(ViewerSwayState state, float w, float h, float lightX, float lightY)
        {
            if (state.LensSurface == null || state.PhotoSurface == null) return;

            if (state.DepthLens is { } lens)
            {
                float maxBlur = Math.Clamp(Math.Max(w, h) * 0.015f, 8f, DepthLensMaxBlurOffset - 2f);
                lens.Properties["blurPx"] = (float)(_focusBlur * maxBlur);
                lens.Properties["focusDepth"] = (float)Math.Clamp(_focusDepth, 0.0, 1.0);
                lens.Properties["focusBand"] = 0.06f;
                lens.Properties["bgDesat"] = (float)Math.Clamp(_bgDesat, 0.0, 1.0);
                lens.Properties["bgDim"] = (float)Math.Clamp(_bgDim, 0.0, 1.0);
                lens.Properties["relight"] = (float)Math.Clamp(_relight, 0.0, 1.0);
                lens.Properties["lightX"] = lightX;
                lens.Properties["lightY"] = lightY;
                lens.Properties["widthPx"] = w;
                lens.Properties["heightPx"] = h;
                lens.Properties["hasMask"] = state.HasMask ? 1f : 0f;
                lens.Properties["normalStepPx"] = Math.Clamp(Math.Max(w, h) / 280f, 3f, 10f);

                using var ls = state.LensSurface.CreateDrawingSession();
                ls.Clear(Microsoft.UI.Colors.Transparent);
                ls.DrawImage(lens, new Rect(0, 0, w, h), new Rect(0, 0, w, h));
            }
            else
            {
                using var ls = state.LensSurface.CreateDrawingSession();
                ls.Clear(Microsoft.UI.Colors.Transparent);
                ls.DrawImage(state.PhotoSurface);
            }

            state.LensDirty = false;
            state.LastLensLightX = lightX;
            state.LastLensLightY = lightY;
        }

        private void DrawSwayToSurface(ViewerSwayState state, bool advanceClock)
        {
            if (state.OutputSurface == null || state.PhotoSurface == null || state.Shift == null || state.Moved == null)
                return;

            double rotation = SurpriseOptions.ClampedSwayRotation(_swayDepth);
            double swing = SurpriseOptions.ClampedSwaySwing(_swaySwing);
            double shownSeconds = state.Shown.Elapsed.TotalSeconds;
            double ramp = Math.Clamp(shownSeconds / SwayRampSeconds, 0.0, 1.0);

            float angleX = 0f, angleY = 0f, angleD = 0f;
            float slideOffsetA = 0f, slideOffsetB = 0f, slideBlendB = 0f;
            if (_motionMode == Viewer3DMotionMode.Tilt)
            {
                if (advanceClock)
                {
                    double dt = state.PointerClock < 0 ? 0.0 : Math.Max(0.0, shownSeconds - state.PointerClock);
                    state.PointerClock = shownSeconds;
                    state.PointerCurrentX = ViewerMath.SmoothSwayAngle(state.PointerCurrentX, state.PointerTargetX, dt);
                    state.PointerCurrentY = ViewerMath.SmoothSwayAngle(state.PointerCurrentY, state.PointerTargetY, dt);
                }
                angleX = state.PointerCurrentX;
                angleY = state.PointerCurrentY * 0.65f;
            }
            else if (_motionMode == Viewer3DMotionMode.Slide && state.SlideNearEffect != null)
            {
                if (advanceClock && !_swayPaused && (_slideLoop || !state.SlideDone))
                {
                    if (state.Clock < 0)
                        state.Clock = shownSeconds;
                    else
                    {
                        double dt = Math.Max(0.0, shownSeconds - state.Clock);
                        state.Clock = shownSeconds;
                        state.Phase = ViewerMath.NextOneWaySlidePhase(state.Phase, dt, swing, _slideLoop);
                        if (!_slideLoop && state.Phase >= 1.0)
                            state.SlideDone = true;
                    }
                }
                (slideOffsetA, slideOffsetB, slideBlendB) = ViewerMath.SlideOneWayLayers(state.Phase, _slideLoop);
                angleD = slideOffsetA;
            }
            else
            {
                if (advanceClock && !_swayPaused)
                {
                    state.Phase = SurpriseOptions.NextSwayPhase(state.Phase, shownSeconds, ref state.Clock, swing);
                }
                double rad = state.Phase * Math.PI * 2.0;
                switch (_motionMode)
                {
                    case Viewer3DMotionMode.Orbit:
                        angleX = (float)Math.Sin(rad);
                        angleY = 0.55f * (float)Math.Cos(rad);
                        break;
                    case Viewer3DMotionMode.Breathe:
                        angleD = (float)Math.Sin(rad);
                        break;
                    default:
                        angleX = (float)Math.Sin(rad);
                        break;
                }
            }

            float w = state.PhotoSurface.SizeInPixels.Width;
            float h = state.PhotoSurface.SizeInPixels.Height;

            float lightX = _motionMode == Viewer3DMotionMode.Tilt
                ? state.PointerCurrentX
                : (_motionMode == Viewer3DMotionMode.Breathe
                    ? angleD * 0.7f
                    : (_motionMode == Viewer3DMotionMode.Slide
                        ? ViewerMath.SlideLightX(state.Phase, _slideLoop, slideOffsetA)
                        : angleX * 0.85f));
            float lightY = _motionMode == Viewer3DMotionMode.Tilt
                ? state.PointerCurrentY
                : (_motionMode == Viewer3DMotionMode.Orbit ? angleY * 0.85f : -0.35f);

            bool lensActive = IsLensActive;
            if (lensActive)
            {
                bool needLens = state.LensDirty
                    || (_relight > 0.001 && (!float.IsFinite(state.LastLensLightX)
                        || Math.Abs(state.LastLensLightX - lightX) > 0.002f
                        || Math.Abs(state.LastLensLightY - lightY) > 0.002f));
                if (needLens)
                    RenderLensSurface(state, w, h, lightX, lightY);
            }

            ICanvasImage activeInput = lensActive && state.LensAtEdges != null ? state.LensAtEdges : state.PhotoAtEdges!;
            if (_motionMode == Viewer3DMotionMode.Slide && state.SlidePlateBitmap != null && state.SlidePlateBorder != null && state.SlideNearEffect != null)
            {
                if (!ReferenceEquals(state.SlideNearEffect.Source, activeInput))
                    state.SlideNearEffect.Source = activeInput;

                ICanvasImage plateImg = state.SlidePlateBitmap;
                if (_focusBlur > 0.001 && state.SlidePlateBlur != null)
                {
                    float bgBlurFactor = (float)Math.Clamp((_focusDepth - 0.12) / 0.55, 0.0, 1.0);
                    float blurAmt = Math.Clamp((float)(_focusBlur * bgBlurFactor * Math.Max(w, h) * 0.012f), 0f, 28f);
                    if (blurAmt > 0.1f)
                    {
                        state.SlidePlateBlur.Source = plateImg;
                        state.SlidePlateBlur.BlurAmount = blurAmt;
                        plateImg = state.SlidePlateBlur;
                    }
                }
                if (_bgDesat > 0.001 && state.SlidePlateDesat != null)
                {
                    state.SlidePlateDesat.Source = plateImg;
                    state.SlidePlateDesat.Saturation = (float)Math.Clamp(1.0 - _bgDesat, 0.0, 1.0);
                    plateImg = state.SlidePlateDesat;
                }
                if (_bgDim > 0.001 && state.SlidePlateDim != null)
                {
                    float dimScale = (float)(1.0 - Math.Clamp(_bgDim, 0.0, 1.0) * 0.68);
                    state.SlidePlateDim.Source = plateImg;
                    state.SlidePlateDim.ColorMatrix = new Matrix5x4
                    {
                        M11 = dimScale, M22 = dimScale, M33 = dimScale, M44 = 1f
                    };
                    plateImg = state.SlidePlateDim;
                }
                if (!ReferenceEquals(state.SlidePlateBorder.Source, plateImg))
                    state.SlidePlateBorder.Source = plateImg;

                float travel = (float)(w * rotation * 1.15);
                float dxA = -state.SlideWay * travel * slideOffsetA;
                float dxB = -state.SlideWay * travel * slideOffsetB;
                using var slideSession = state.OutputSurface.CreateDrawingSession();
                slideSession.Clear(Microsoft.UI.Colors.Black);
                slideSession.DrawImage(state.SlidePlateBorder, new Rect(0, 0, w, h), new Rect(-dxA, 0, w, h), 1f, CanvasImageInterpolation.Linear);
                if (slideBlendB > 0.001f)
                    slideSession.DrawImage(state.SlidePlateBorder, new Rect(0, 0, w, h), new Rect(-dxB, 0, w, h), slideBlendB, CanvasImageInterpolation.Linear);
                slideSession.DrawImage(state.SlideNearEffect);
                return;
            }
            if (!ReferenceEquals(state.Moved.Source, activeInput)) state.Moved.Source = activeInput;
            if (state.Splat2D != null && !ReferenceEquals(state.Splat2D.Source1, activeInput)) state.Splat2D.Source1 = activeInput;

            float shiftX = (float)(angleX * w * rotation * ramp);
            float shiftY = (float)(angleY * h * rotation * ramp);
            float dolly = _motionMode == Viewer3DMotionMode.Breathe ? (float)(angleD * rotation * 1.25 * ramp) : 0f;
            float pivot = state.Middle;

            float amount = state.Moved.Amount;
            float aX = -shiftX / amount;
            float aY = -shiftY / amount;
            state.Shift.ColorMatrix = new Matrix5x4
            {
                M11 = aX, M12 = aY, M13 = 0, M14 = 0,
                M21 = 0, M22 = 0, M23 = 0, M24 = 0,
                M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                M41 = 0, M42 = 0, M43 = 0, M44 = 0,
                M51 = 0.5f - aX * pivot, M52 = 0.5f - aY * pivot, M53 = 0, M54 = 1
            };

            ICanvasImage drawn = state.Moved;
            if (state.Splat2D is { } splat)
            {
                float maxShiftMag = MathF.Sqrt(shiftX * shiftX + shiftY * shiftY)
                    + MathF.Abs(dolly) * MathF.Sqrt(w * w * 0.25f + h * h * 0.25f);
                splat.Properties["shiftX"] = shiftX;
                splat.Properties["shiftY"] = shiftY;
                splat.Properties["dolly"] = dolly;
                splat.Properties["centerX"] = w * 0.5f;
                splat.Properties["centerY"] = h * 0.5f;
                splat.Properties["pivot"] = pivot;
                splat.Properties["stepPx"] = state.SplatStep;
                float reach = Math.Max(0f, Math.Min(splat.MaxSamplerOffset - state.SplatStep - 2f, maxShiftMag * Math.Max(pivot, 1f - pivot) + state.SplatStep * 2f));
                splat.Properties["reach"] = reach;
                drawn = splat;
            }

            float targetOver = ViewerMath.SwayOverscanFor(rotation, _motionMode == Viewer3DMotionMode.Breathe);
            float over = 1f + (targetOver - 1f) * (float)ramp;
            float cropW = w / over, cropH = h / over;
            using var session = state.OutputSurface.CreateDrawingSession();
            session.Clear(Microsoft.UI.Colors.Transparent);
            session.DrawImage(drawn, new Rect(0, 0, w, h), new Rect((w - cropW) / 2f, (h - cropH) / 2f, cropW, cropH));
        }

        private void DrawStaticSwayFrame()
        {
            if (_swayState == null) return;
            try
            {
                DrawSwayToSurface(_swayState, advanceClock: false);
                RequestRender();
            }
            catch (Exception ex) when (IsSwayDeviceLost(ex))
            {
                StopSwayRuntime(requestRender: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewerSway] Static redraw missed: {ex.Message}");
            }
        }

        private bool IsSwayRamped() =>
            _swayState != null && _swayState.Shown.Elapsed.TotalSeconds >= SwayRampSeconds;

        private bool NeedSwayPointerSettle() =>
            _motionMode == Viewer3DMotionMode.Tilt
            && _swayState != null
            && (Math.Abs(_swayState.PointerTargetX - _swayState.PointerCurrentX) >= 0.0005f
                || Math.Abs(_swayState.PointerTargetY - _swayState.PointerCurrentY) >= 0.0005f);

        private bool ShouldRenderSwayFrames() =>
            _swayState != null
            && !_swayUnloaded
            && !_swayPaused
            && !_isComparingOriginal
            && (_motionMode != Viewer3DMotionMode.Tilt || NeedSwayPointerSettle() || !IsSwayRamped())
            && !(_motionMode == Viewer3DMotionMode.Slide && !_slideLoop && _swayState.SlideNearEffect != null && _swayState.SlideDone);

        private void UpdateSwayRenderingSubscription()
        {
            bool want = ShouldRenderSwayFrames();
            if (want == _swayRenderingSubscribed) return;

            if (want)
            {
                if (_swayState != null)
                {
                    _swayState.Clock = -1;
                    _swayState.PointerClock = -1;
                }
                CompositionTarget.Rendering += OnSwayRendering;
                _swayRenderingSubscribed = true;
            }
            else
            {
                CompositionTarget.Rendering -= OnSwayRendering;
                _swayRenderingSubscribed = false;
            }
        }

        private void OnSwayRendering(object? sender, object e)
        {
            var state = _swayState;
            if (state == null || _swayUnloaded)
            {
                UpdateSwayRenderingSubscription();
                return;
            }

            try
            {
                bool wasSlideDone = state.SlideDone;
                DrawSwayToSurface(state, advanceClock: true);
                _swayDrawFaults = 0;
                PreviewCanvas?.Invalidate();

                if (!wasSlideDone && state.SlideDone)
                    SyncSwayChrome();

                if (!ShouldRenderSwayFrames())
                    UpdateSwayRenderingSubscription();
            }
            catch (Exception ex)
            {
                if (IsSwayDeviceLost(ex))
                {
                    StopSwayRuntime(requestRender: false);
                    return;
                }
                if (++_swayDrawFaults >= SwayFaultsBeforeStop)
                {
                    PlayTiming.Write($"[ViewerSway] Stopped after faults: {ex.GetType().Name}: {ex.Message}");
                    Debug.WriteLine($"[ViewerSway] Stopped after faults: {ex.Message}");
                    StopSwayRuntime(requestRender: true);
                }
            }
        }

        private static bool IsSwayDeviceLost(Exception ex)
        {
            for (Exception? cursor = ex; cursor != null; cursor = cursor.InnerException)
            {
                int hr = cursor.HResult;
                if (hr == unchecked((int)0x887A0005) || hr == unchecked((int)0x887A0006) || hr == unchecked((int)0x887A0007))
                    return true;
            }
            return false;
        }

        private void StopSwayRuntime(bool requestRender)
        {
            ++_swayTicket;
            _swayCts?.Cancel();
            _swayCts?.Dispose();
            _swayCts = null;
            _swayPreparing = false;
            _swayRefiningMask = false;
            _swayBuildingSlide = false;
            _swayMaskWake = null;
            _swayDrawFaults = 0;

            if (_swayRenderingSubscribed)
            {
                CompositionTarget.Rendering -= OnSwayRendering;
                _swayRenderingSubscribed = false;
            }

            var old = _swayState;
            _swayState = null;
            if (old != null && _effectStack.Crop != null && ReferenceEquals(_effectStack.Crop.Source, old.OutputSurface))
                _effectStack.Crop.Source = _rawGpuBitmap;
            old?.Dispose();

            if (requestRender && !_swayUnloaded)
                RequestRender();
        }

        private void OnSwayPointerMoved(Point ptr)
        {
            if (!_isSwayEnabled || _motionMode != Viewer3DMotionMode.Tilt || _swayState == null || PreviewCanvas == null || InputOverlay == null)
                return;

            float dpiScale = PreviewCanvas.Dpi / 96.0f;
            double displayedImgW = (EffectiveLogicalWidth / dpiScale) * _targetZoom;
            double displayedImgH = (EffectiveLogicalHeight / dpiScale) * _targetZoom;
            double viewW = InputOverlay.ActualWidth > 0 ? InputOverlay.ActualWidth : RootGrid.ActualWidth;
            double viewH = InputOverlay.ActualHeight > 0 ? InputOverlay.ActualHeight : RootGrid.ActualHeight;
            var (vx, vy) = ViewerMath.CalculateSwayPointerVector(ptr.X, ptr.Y, viewW, viewH, displayedImgW, displayedImgH);
            int q = ((ViewRotationQuarterTurns % 4) + 4) % 4;
            (float sx, float sy) = q switch
            {
                1 => (vy, -vx),
                2 => (-vx, -vy),
                3 => (-vy, vx),
                _ => (vx, vy)
            };
            if (ViewMirrorHorizontal) sx = -sx;
            if (ViewFlipVertical) sy = -sy;
            _swayState.PointerTargetX = sx;
            _swayState.PointerTargetY = sy;
            UpdateSwayRenderingSubscription();
        }

        private bool TryPickFocusAt(Point overlayPos)
        {
            if (!_isSwayEnabled || _swayState == null || PreviewCanvas == null || InputOverlay == null)
                return false;

            float dpiScale = PreviewCanvas.Dpi / 96.0f;
            double displayedW = (EffectiveLogicalWidth / dpiScale) * _targetZoom;
            double displayedH = (EffectiveLogicalHeight / dpiScale) * _targetZoom;
            double viewW = InputOverlay.ActualWidth > 0 ? InputOverlay.ActualWidth : RootGrid.ActualWidth;
            double viewH = InputOverlay.ActualHeight > 0 ? InputOverlay.ActualHeight : RootGrid.ActualHeight;
            double panX = -(ImageScroll?.HorizontalOffset ?? 0) + _tempPanX;
            double panY = -(ImageScroll?.VerticalOffset ?? 0) + _tempPanY;

            var (inside, u, v) = ViewerMath.ScreenToSourceNormalized(
                overlayPos.X, overlayPos.Y,
                viewW, viewH, displayedW, displayedH, panX, panY,
                ViewRotationQuarterTurns, ViewMirrorHorizontal, ViewFlipVertical);
            if (!inside) return false;

            var map = _swayState.DepthMap;
            int gw = Math.Max(1, map.Width);
            int gh = Math.Max(1, map.Height);
            int cx = Math.Clamp((int)Math.Round(u * (gw - 1)), 0, gw - 1);
            int cy = Math.Clamp((int)Math.Round(v * (gh - 1)), 0, gh - 1);
            int sum = 0, count = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int yy = Math.Clamp(cy + dy, 0, gh - 1);
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = Math.Clamp(cx + dx, 0, gw - 1);
                    sum += map.Grey[yy * gw + xx];
                    count++;
                }
            }

            _focusDepth = Math.Clamp(sum / (255.0 * Math.Max(1, count)), 0.0, 1.0);
            _hasCustomFocusDepth = true;
            if (_focusBlur < 0.05) _focusBlur = 0.45;
            _swayPickingFocus = false;
            _swayState.LensDirty = true;

            if (FocusReticle != null)
            {
                Canvas.SetLeft(FocusReticle, overlayPos.X - 22.0);
                Canvas.SetTop(FocusReticle, overlayPos.Y - 22.0);
                FocusReticle.Visibility = Visibility.Visible;
                FocusReticleStoryboard?.Stop();
                FocusReticleStoryboard?.Begin();
            }

            SyncSwayChrome();
            if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
            ShowHud();
            return true;
        }

        private void AttachSlideGpuResources(ViewerSwayState state, CanvasDevice device, ICanvasImage nearSource)
        {
            if (state.SlideLayers == null || state.SlideLayerWidth <= 0 || state.SlideLayerHeight <= 0) return;

            state.DisposeSlideGpuResources();

            int w = state.SlideLayerWidth;
            int h = state.SlideLayerHeight;
            var plateBitmap = CanvasBitmap.CreateFromBytes(device, state.SlideLayers.Plate, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            var plateBorder = new BorderEffect
            {
                Source = plateBitmap,
                ExtendX = CanvasEdgeBehavior.Mirror,
                ExtendY = CanvasEdgeBehavior.Clamp
            };
            var maskBitmap = CanvasBitmap.CreateFromBytes(device, state.SlideLayers.Mask, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            var nearEffect = new AlphaMaskEffect
            {
                Source = nearSource,
                AlphaMask = maskBitmap
            };
            state.SlidePlateBitmap = plateBitmap;
            state.SlidePlateBorder = plateBorder;
            state.SlideMaskBitmap = maskBitmap;
            state.SlideNearEffect = nearEffect;
            state.SlidePlateBlur = new GaussianBlurEffect
            {
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Speed
            };
            state.SlidePlateDesat = new SaturationEffect();
            state.SlidePlateDim = new ColorMatrixEffect();
        }

        private async Task EnsureSlideLayersAsync(ViewerSwayState state, int ticket, CancellationToken token)
        {
            if (_swayBuildingSlide || state.PhotoSurface == null) return;
            if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state))
                return;

            _swayBuildingSlide = true;
            SyncSwayChrome();
            bool buildReverse = _slideReverse;
            bool retryBuild = false;
            try
            {
                var outline = state.SubjectMask;
                if (outline == null)
                {
                    WakeSubjectMaskRefine();
                    outline = await SubjectMasks.ForAsync(state.Path);
                    if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state) || state.PhotoSurface == null)
                        return;
                }

                int w = (int)state.PhotoSurface.SizeInPixels.Width;
                int h = (int)state.PhotoSurface.SizeInPixels.Height;
                byte[] photoBytes = state.PhotoSurface.GetPixelBytes();

                float autoWay = 1f;
                if (outline != null && outline.Size > 0 && outline.Alpha.Length > 0)
                {
                    double sum = 0, weight = 0;
                    for (int i = 0; i < outline.Alpha.Length; i++)
                    {
                        float a = outline.Alpha[i];
                        sum += a * (i % outline.Size);
                        weight += a;
                    }
                    float subjectX = weight > 0 ? (float)(sum / weight / outline.Size) : 0.5f;
                    autoWay = subjectX < 0.5f ? 1f : -1f;
                }
                float way = buildReverse ? -autoWay : autoWay;

                byte[] depthBytes = state.DepthMap.Raw ?? state.DepthMap.Grey;
                int dw = state.DepthMap.Width, dh = state.DepthMap.Height;
                byte[]? alpha = outline?.Alpha;
                int maskSize = outline?.Size ?? 0;

                var layers = await Task.Run(() =>
                    ParallaxLayers.Build(photoBytes, w, h, depthBytes, dw, dh, alpha, maskSize, way > 0 ? 1 : -1), token);

                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || !ReferenceEquals(_swayState, state) || state.PhotoSurface == null)
                    return;
                if ((int)state.PhotoSurface.SizeInPixels.Width != w || (int)state.PhotoSurface.SizeInPixels.Height != h)
                {
                    retryBuild = true;
                    return;
                }

                state.SlideLayersAttempted = true;
                if (layers.InFront < 0.08f)
                {
                    state.SlideSupported = false;
                    if (_motionMode == Viewer3DMotionMode.Slide)
                    {
                        _motionMode = Viewer3DMotionMode.Sway;
                        SyncSwayChrome();
                        ShowNotification("No clear foreground subject for Slide \u2014 using Sway");
                    }
                    return;
                }

                state.SlideLayers = layers;
                state.SlideLayerWidth = w;
                state.SlideLayerHeight = h;
                state.SlideWay = way;
                state.SlideSupported = true;
                ICanvasImage nearSource = IsLensActive && state.LensAtEdges != null ? state.LensAtEdges : state.PhotoAtEdges!;
                AttachSlideGpuResources(state, state.PhotoSurface.Device, nearSource);
                if (_slideReverse != buildReverse)
                    retryBuild = true;
                state.Phase = 0;
                state.Clock = -1;
                state.SlideDone = false;
                SyncSwayChrome();
                UpdateSwayRenderingSubscription();
                if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"[ViewerSway] Slide layers failed: {ex.GetType().Name}: {ex.Message}");
                if (ticket == _swayTicket && ReferenceEquals(_swayState, state))
                {
                    state.SlideLayersAttempted = true;
                    state.SlideSupported = false;
                    if (_motionMode == Viewer3DMotionMode.Slide)
                    {
                        _motionMode = Viewer3DMotionMode.Sway;
                        SyncSwayChrome();
                        ShowNotification("Could not build Slide layers \u2014 using Sway");
                    }
                }
            }
            finally
            {
                if (ticket == _swayTicket && !_swayUnloaded)
                {
                    _swayBuildingSlide = false;
                    SyncSwayChrome();
                    if (retryBuild && _isSwayEnabled && _motionMode == Viewer3DMotionMode.Slide && ReferenceEquals(_swayState, state) && _swayCts != null && !token.IsCancellationRequested)
                    {
                        state.SlideLayers = null;
                        state.SlideLayersAttempted = false;
                        state.SlideSupported = false;
                        state.DisposeSlideGpuResources();
                        _ = EnsureSlideLayersAsync(state, ticket, token);
                    }
                }
            }
        }

        private void SetMotionMode(Viewer3DMotionMode mode)
        {
            if (_motionMode == Viewer3DMotionMode.Slide && mode == Viewer3DMotionMode.Slide && _swayState != null && !_slideLoop && (_swayState.SlideDone || _swayPaused))
            {
                _swayState.Phase = 0;
                _swayState.Clock = -1;
                _swayState.SlideDone = false;
                _swayPaused = false;
                SyncSwayChrome();
                UpdateSwayRenderingSubscription();
                ShowHud();
                return;
            }

            if (_motionMode == mode && !_swayPaused) return;

            if (mode == Viewer3DMotionMode.Slide && _swayState != null)
            {
                if (_swayState.SlideLayersAttempted && !_swayState.SlideSupported)
                {
                    ShowNotification("No clear foreground subject for Slide \u2014 using Sway");
                    if (_motionMode == Viewer3DMotionMode.Slide) _motionMode = Viewer3DMotionMode.Sway;
                    SyncSwayChrome();
                    return;
                }
                if (_swayState.SlideNearEffect == null && !_swayBuildingSlide && _swayCts != null)
                {
                    _motionMode = mode;
                    _swayPaused = false;
                    _swayState.Phase = 0;
                    _swayState.Clock = -1;
                    _swayState.SlideDone = false;
                    SyncSwayChrome();
                    UpdateSwayRenderingSubscription();
                    ShowHud();
                    _ = EnsureSlideLayersAsync(_swayState, _swayTicket, _swayCts.Token);
                    return;
                }
                _swayState.Phase = 0;
                _swayState.Clock = -1;
                _swayState.SlideDone = false;
            }

            if (_swayState != null)
            {
                if (_motionMode != Viewer3DMotionMode.Tilt && mode == Viewer3DMotionMode.Tilt)
                {
                    double rad = _swayState.Phase * Math.PI * 2.0;
                    _swayState.PointerCurrentX = (float)Math.Sin(rad);
                    _swayState.PointerTargetX = _swayState.PointerCurrentX;
                    _swayState.PointerCurrentY = _motionMode == Viewer3DMotionMode.Orbit ? 0.55f * (float)Math.Cos(rad) : 0f;
                    _swayState.PointerTargetY = _swayState.PointerCurrentY;
                    _swayState.PointerClock = -1;
                }
                else if (_motionMode == Viewer3DMotionMode.Tilt && mode != Viewer3DMotionMode.Tilt)
                {
                    _swayState.Phase = ViewerMath.SwayPhaseFromAngle(_swayState.PointerCurrentX);
                    _swayState.Clock = -1;
                }
            }

            _motionMode = mode;
            _swayPaused = false;
            SyncSwayChrome();
            UpdateSwayRenderingSubscription();
            if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
            ShowHud();
        }

        public void SetCompareOriginal(bool active)
        {
            if (_isComparingOriginal == active) return;
            if (active && !(_isSwayEnabled && _swayState != null) && !HasViewTransformOrTone)
                return;

            _isComparingOriginal = active;
            SyncSwayChrome();
            UpdateSwayRenderingSubscription();
            ShowHud();
            RequestRender();
        }

        public bool HandleViewerKeyUp(VirtualKey key)
        {
            if ((int)key == 220 && _isComparingOriginal)
            {
                SetCompareOriginal(false);
                return true;
            }
            return false;
        }

        private void SwayCompareButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(SwayCompareButton).Properties.IsLeftButtonPressed) return;
            SwayCompareButton?.CapturePointer(e.Pointer);
            e.Handled = true;
            SetCompareOriginal(true);
        }

        private void SwayCompareButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            SwayCompareButton?.ReleasePointerCapture(e.Pointer);
            SetCompareOriginal(false);
        }

        public bool HandleViewerKeystroke(VirtualKey key)
        {
            if (HandleAnimationKeystroke(key))
                return true;

            bool isCtrlHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool isAltHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (isCtrlHeld && !isAltHeld && key == VirtualKey.C
                && (_rawGpuBitmap != null || (_animationFrames != null && _animationFrames.Length > 0)))
            {
                _ = CopyRenderedViewToClipboardAsync();
                return true;
            }

            if (isCtrlHeld || isAltHeld)
                return false;

            if ((int)key == 220 && ((_isSwayEnabled && _swayState != null) || HasViewTransformOrTone))
            {
                SetCompareOriginal(true);
                return true;
            }

            if (key == VirtualKey.S && CanUseSway)
            {
                ToggleSway();
                return true;
            }

            if (key == VirtualKey.Space && _isSwayEnabled && _swayState != null)
            {
                SwayPlayPause_Click(this, null!);
                return true;
            }

            if (key == VirtualKey.M)
            {
                ToggleMirrorHorizontal();
                return true;
            }

            if (key == VirtualKey.G)
            {
                ToggleGrayscale();
                return true;
            }

            if ((int)key == 221) // ']'
            {
                RotateView(1);
                return true;
            }

            if ((int)key == 219) // '['
            {
                RotateView(-1);
                return true;
            }

            if (key == VirtualKey.T && IsStandaloneMode)
            {
                ToggleAlwaysOnTopRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            return false;
        }

        private void ContextFlyout_Opening(object sender, object e)
        {
            UpdateSwayAvailability();
            SyncViewChrome();
        }

        private void ToggleSway_Click(object sender, RoutedEventArgs e) => ToggleSway();

        private void StudioTabMotion_Click(object sender, RoutedEventArgs e)
        {
            _studioTab = StudioTab.Motion;
            _swayPickingFocus = false;
            SyncSwayChrome();
            ShowHud();
        }

        private void StudioTabFocus_Click(object sender, RoutedEventArgs e)
        {
            _studioTab = StudioTab.Focus;
            SyncSwayChrome();
            ShowHud();
        }

        private void StudioTabPop_Click(object sender, RoutedEventArgs e)
        {
            _studioTab = StudioTab.Pop;
            _swayPickingFocus = false;
            WakeSubjectMaskRefine();
            SyncSwayChrome();
            ShowHud();
        }

        private void SwayResetEffects_Click(object sender, RoutedEventArgs e)
        {
            _focusBlur = 0.0;
            _bgDesat = 0.0;
            _bgDim = 0.0;
            _relight = 0.0;
            _swayPickingFocus = false;
            _hasCustomFocusDepth = false;
            if (_swayState != null)
            {
                _focusDepth = Math.Clamp(_swayState.Middle, 0.0, 1.0);
                _swayState.LensDirty = true;
            }
            SyncSwayChrome();
            if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
            ShowHud();
        }

        private void ModeSway_Click(object sender, RoutedEventArgs e) => SetMotionMode(Viewer3DMotionMode.Sway);
        private void ModeSlide_Click(object sender, RoutedEventArgs e) => SetMotionMode(Viewer3DMotionMode.Slide);
        private void ModeOrbit_Click(object sender, RoutedEventArgs e) => SetMotionMode(Viewer3DMotionMode.Orbit);
        private void ModeBreathe_Click(object sender, RoutedEventArgs e) => SetMotionMode(Viewer3DMotionMode.Breathe);
        private void ModeTilt_Click(object sender, RoutedEventArgs e) => SetMotionMode(Viewer3DMotionMode.Tilt);

        private void SwayPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_swayState == null) return;

            if (_motionMode == Viewer3DMotionMode.Tilt)
            {
                SetMotionMode(Viewer3DMotionMode.Sway);
                return;
            }

            if (_motionMode == Viewer3DMotionMode.Slide && !_slideLoop && _swayState.SlideDone)
            {
                _swayState.Phase = 0;
                _swayState.Clock = -1;
                _swayState.SlideDone = false;
                _swayPaused = false;
                SyncSwayChrome();
                UpdateSwayRenderingSubscription();
                ShowHud();
                return;
            }

            _swayPaused = !_swayPaused;
            if (!_swayPaused) _swayState.Clock = -1;

            SyncSwayChrome();
            UpdateSwayRenderingSubscription();
            ShowHud();
        }

        private void SlideLoopToggle_Click(object sender, RoutedEventArgs e)
        {
            _slideLoop = !_slideLoop;
            if (_swayState != null)
            {
                _swayState.Phase = 0;
                _swayState.Clock = -1;
                _swayState.SlideDone = false;
                _swayPaused = false;
            }
            SyncSwayChrome();
            UpdateSwayRenderingSubscription();
            if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
            ShowHud();
        }

        private void SlideReverse_Click(object sender, RoutedEventArgs e)
        {
            _slideReverse = !_slideReverse;
            if (_swayState != null && _swayCts != null && !_swayBuildingSlide)
            {
                _swayState.SlideLayers = null;
                _swayState.SlideLayersAttempted = false;
                _swayState.SlideSupported = false;
                _swayState.DisposeSlideGpuResources();
                _swayState.SlideDone = false;
                _swayState.Phase = 0;
                _swayState.Clock = -1;
                _swayPaused = false;
                SyncSwayChrome();
                UpdateSwayRenderingSubscription();
                ShowHud();
                _ = EnsureSlideLayersAsync(_swayState, _swayTicket, _swayCts.Token);
                return;
            }
            SyncSwayChrome();
            ShowHud();
        }

        private void PickFocusToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_swayState == null) return;
            _swayPickingFocus = !_swayPickingFocus;
            SyncSwayChrome();
            ShowHud();
        }

        private void SwayDepthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _swayDepth = SurpriseOptions.ClampedSwayRotation(Math.Round(e.NewValue, 1) / 100.0);
            if (SwayDepthValue != null) SwayDepthValue.Text = $"{_swayDepth * 100.0:0.0}%";
            ResetHudHideTimer();
            ScheduleSwayPreferencesSave();
            if (_swayState != null && !_swayRenderingSubscribed)
                DrawStaticSwayFrame();
        }

        private void SwaySwingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _swaySwing = SurpriseOptions.ClampedSwaySwing(Math.Round(e.NewValue * 4.0) / 4.0);
            if (SwaySwingValue != null) SwaySwingValue.Text = $"{_swaySwing:0.##} s";
            ResetHudHideTimer();
            ScheduleSwayPreferencesSave();
        }

        private void MarkLensChanged()
        {
            ResetHudHideTimer();
            if (_swayState != null)
            {
                _swayState.LensDirty = true;
                if (!_swayRenderingSubscribed) DrawStaticSwayFrame();
            }
            SyncSwayChrome();
        }

        private void FocusBlurSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _focusBlur = Math.Clamp(Math.Round(e.NewValue) / 100.0, 0.0, 1.0);
            if (FocusBlurValue != null) FocusBlurValue.Text = $"{Math.Round(_focusBlur * 100.0):0}%";
            MarkLensChanged();
        }

        private void FocusPlaneSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _focusDepth = Math.Clamp(Math.Round(e.NewValue) / 100.0, 0.0, 1.0);
            _hasCustomFocusDepth = true;
            if (FocusPlaneValue != null) FocusPlaneValue.Text = $"{Math.Round(_focusDepth * 100.0):0}%";
            MarkLensChanged();
        }

        private void PopColorSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _bgDesat = Math.Clamp(Math.Round(e.NewValue) / 100.0, 0.0, 1.0);
            if (PopColorValue != null) PopColorValue.Text = $"{Math.Round(_bgDesat * 100.0):0}%";
            if (_bgDesat > 0.001) WakeSubjectMaskRefine();
            MarkLensChanged();
        }

        private void PopDimSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _bgDim = Math.Clamp(Math.Round(e.NewValue) / 100.0, 0.0, 1.0);
            if (PopDimValue != null) PopDimValue.Text = $"{Math.Round(_bgDim * 100.0):0}%";
            if (_bgDim > 0.001) WakeSubjectMaskRefine();
            MarkLensChanged();
        }

        private void PopLightSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingStudioSliders) return;
            _relight = Math.Clamp(Math.Round(e.NewValue) / 100.0, 0.0, 1.0);
            if (PopLightValue != null) PopLightValue.Text = $"{Math.Round(_relight * 100.0):0}%";
            MarkLensChanged();
        }

        private void SwayDepthSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _swayDepth = SurpriseOptions.SwayRotationDefault;
            SyncStudioSliders();
            ResetHudHideTimer();
            ScheduleSwayPreferencesSave();
            if (_swayState != null && !_swayRenderingSubscribed) DrawStaticSwayFrame();
        }

        private void SwaySwingSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _swaySwing = SurpriseOptions.SwaySwingDefault;
            SyncStudioSliders();
            ResetHudHideTimer();
            ScheduleSwayPreferencesSave();
        }

        private void FocusBlurSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _focusBlur = 0.0;
            MarkLensChanged();
        }

        private void FocusPlaneSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _hasCustomFocusDepth = false;
            _focusDepth = Math.Clamp(_swayState?.Middle ?? 0.5, 0.0, 1.0);
            MarkLensChanged();
        }

        private void PopColorSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _bgDesat = 0.0;
            MarkLensChanged();
        }

        private void PopDimSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _bgDim = 0.0;
            MarkLensChanged();
        }

        private void PopLightSlider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _relight = 0.0;
            MarkLensChanged();
        }

        private void CopyRenderedImage_Click(object sender, RoutedEventArgs e) => _ = CopyRenderedViewToClipboardAsync();

        public async Task CopyRenderedViewToClipboardAsync()
        {
            if (_isCopyingImage || _swayUnloaded) return;

            CanvasBitmap? currentTarget = _rawGpuBitmap;
            if (_animationFrames != null && _currentFrameIndex >= 0 && _currentFrameIndex < _animationFrames.Length)
                currentTarget = _animationFrames[_currentFrameIndex]?.GpuBitmap ?? currentTarget;

            CanvasBitmap? drawTarget = TryGetActiveSwayBitmap() ?? currentTarget;
            if (drawTarget == null) return;

            var device = _canvasDevice ?? CanvasDevice.GetSharedDevice();
            if (device == null) return;

            _isCopyingImage = true;
            try
            {
                bool showOriginal = _isComparingOriginal;
                bool applyGray = ViewGrayscale && !showOriginal;
                bool applyInvert = ViewInvert && !showOriginal;
                bool twoPages = IsShowingTwoPages && _secondGpuBitmap != null && currentTarget != null;

                CanvasRenderTarget target;
                if (twoPages)
                {
                    double effW = EffectiveLogicalWidth > 1 ? EffectiveLogicalWidth : (currentTarget!.SizeInPixels.Width + _secondGpuBitmap!.SizeInPixels.Width);
                    double effH = EffectiveLogicalHeight > 1 ? EffectiveLogicalHeight : Math.Max(currentTarget!.SizeInPixels.Height, _secondGpuBitmap!.SizeInPixels.Height);
                    var (outW, outH) = ViewerMath.SwaySurfaceSize((int)Math.Round(effW), (int)Math.Round(effH), 4096);
                    float spreadZoom = (float)(outH / Math.Max(1.0, effH));

                    target = new CanvasRenderTarget(device, outW, outH, 96f);
                    using (var crop = new CropEffect())
                    using (var decode = new GammaTransferEffect { RedExponent = 2.2f, GreenExponent = 2.2f, BlueExponent = 2.2f })
                    using (var userGamma = new GammaTransferEffect())
                    using (var transform = new Transform2DEffect { InterpolationMode = CanvasImageInterpolation.HighQualityCubic })
                    using (var encode = new GammaTransferEffect { RedExponent = 1.0f / 2.2f, GreenExponent = 1.0f / 2.2f, BlueExponent = 1.0f / 2.2f })
                    using (var grayscale = applyGray ? new SaturationEffect { Saturation = 0f } : null)
                    using (var invert = applyInvert ? new InvertEffect() : null)
                    using (var ds = target.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ViewerMath.DrawDualMappedImages(
                            ds, outW, outH,
                            _logicalImageWidth, _logicalImageHeight,
                            _secondLogicalWidth, _secondLogicalHeight,
                            0, 0, spreadZoom, _currentGamma, 1f,
                            currentTarget!, _secondGpuBitmap!,
                            crop, decode, userGamma, transform, encode,
                            grayscale, applyGray, invert, applyInvert);
                    }
                }
                else
                {
                    int rotTurns = showOriginal ? 0 : ViewRotationQuarterTurns;
                    bool mirrorH = !showOriginal && ViewMirrorHorizontal;
                    bool flipV = !showOriginal && ViewFlipVertical;
                    double baseLogicalW = showOriginal ? _logicalImageWidth : LogicalWidth;
                    double baseLogicalH = showOriginal ? _logicalImageHeight : LogicalHeight;
                    double srcLogicalW = baseLogicalW > 1
                        ? baseLogicalW
                        : ((rotTurns & 1) != 0 ? drawTarget.SizeInPixels.Height : drawTarget.SizeInPixels.Width);
                    double srcLogicalH = baseLogicalH > 1
                        ? baseLogicalH
                        : ((rotTurns & 1) != 0 ? drawTarget.SizeInPixels.Width : drawTarget.SizeInPixels.Height);
                    var (outW, outH) = ViewerMath.SwaySurfaceSize((int)Math.Round(srcLogicalW), (int)Math.Round(srcLogicalH), 4096);

                    target = new CanvasRenderTarget(device, outW, outH, 96f);
                    using (var crop = new CropEffect { Source = drawTarget, SourceRectangle = drawTarget.Bounds })
                    using (var decode = new GammaTransferEffect { Source = crop, RedExponent = 2.2f, GreenExponent = 2.2f, BlueExponent = 2.2f })
                    using (var userGamma = new GammaTransferEffect
                    {
                        Source = decode,
                        RedExponent = _currentGamma / 2.2f,
                        GreenExponent = _currentGamma / 2.2f,
                        BlueExponent = _currentGamma / 2.2f
                    })
                    using (var transform = new Transform2DEffect
                    {
                        Source = userGamma,
                        InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
                        TransformMatrix = ViewerMath.BuildViewTransformMatrix(
                            drawTarget.SizeInPixels.Width, drawTarget.SizeInPixels.Height,
                            outW, outH,
                            rotTurns, mirrorH, flipV)
                    })
                    using (var encode = new GammaTransferEffect
                    {
                        Source = transform,
                        RedExponent = 1.0f / 2.2f,
                        GreenExponent = 1.0f / 2.2f,
                        BlueExponent = 1.0f / 2.2f
                    })
                    using (var grayscale = applyGray ? new SaturationEffect { Source = encode, Saturation = 0f } : null)
                    using (var invert = applyInvert ? new InvertEffect { Source = (ICanvasImage?)grayscale ?? encode } : null)
                    using (var ds = target.CreateDrawingSession())
                    {
                        ICanvasImage output = (ICanvasImage?)invert ?? (ICanvasImage?)grayscale ?? encode;
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ds.DrawImage(output);
                    }
                }

                using (target)
                {
                    var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                    stream.Seek(0);

                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage
                    {
                        RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                    };
                    package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                    ShowNotification("Copied image to clipboard");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CopyImage] Failed: {ex.Message}");
                ShowNotification("Could not copy image");
            }
            finally
            {
                _isCopyingImage = false;
            }
        }

        private void SwayHud_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

        private void ResetHudHideTimer()
        {
            if (_hudTimer != null && !_isPointerOverHud)
            {
                _hudTimer.Stop();
                _hudTimer.Start();
            }
        }
    }
}

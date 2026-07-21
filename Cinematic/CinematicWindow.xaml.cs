using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.FaceAnalysis;
using Windows.Storage;
using ModernImageViewer.Cinematic.Data;
using ModernImageViewer.Cinematic.ViewModels;
using ModernImageViewer.Cinematic.Services;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ModernImageViewer.Cinematic
{
    public sealed partial class CinematicWindow : Window
    {
        public CinematicViewModel ViewModel { get; }

        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _mathCts;

        private readonly SolidColorBrush _telemetryOverrideText = new SolidColorBrush(Microsoft.UI.Colors.Orange);
        private readonly SolidColorBrush _telemetryAutoText = new SolidColorBrush(Microsoft.UI.Colors.White);
        private readonly SolidColorBrush _telemetryOverrideBg = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 255, 165, 0));
        private readonly SolidColorBrush _telemetryAutoBg = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
        private sealed record PlaybackRequest(Size Bounds, SlideSettings Settings);

        private AppWindow _appWindow;
        private CanvasDevice? _canvasDevice;
        private FaceDetector? _faceDetector;
        private readonly CinematicDirector _director = new();

        private SlideFrame? _primaryFrame;
        private SlideFrame? _secondaryFrame;

        private bool _isPreloading = false;
        private TimeSpan _slideStartTime = TimeSpan.Zero;
        private float _crossfadeBlurAlpha = 0f;
        private float _crossfadeSubjectAlpha = 0f;
        private double _slideDurationSeconds = 12.0;

        private double _lastCanvasWidth;
        private double _lastCanvasHeight;

        private DispatcherQueueTimer _idleTimer;
        private bool _isManuallyNavigating = false;
        private int _currentTransitionType = -1;
        private int _trajectoryUpdateId = 0;

        // Freeze-Frame State Machine
        private bool _isTargetingFrozen = false;
        private TimeSpan _lastRenderTime = TimeSpan.Zero;
        private bool _injectFrozenCoordinates = false;
        private float _frozenScale;
        private Vector2 _frozenPan;
        private float _frozenRotation;

        private Queue<CameraTransform> _currentSequence = new();
        private Queue<CameraTransform> _nextSequence = new();

        private class SlideFrame : IDisposable
        {
            public CanvasBitmap GpuBitmap { get; }
            public SceneIntelligence Intelligence { get; }
            public CameraTransform Trajectory { get; set; } = new();
            public bool IsSharedResource { get; set; } = false;

            public float CurrentScale;
            public Vector2 CurrentPan;
            public float CurrentRotation;

            public ColorMatrixEffect BlurColorMatrix { get; }
            public GaussianBlurEffect BlurEffect { get; }

            public SlideFrame(CanvasBitmap bitmap, SceneIntelligence intel)
            {
                GpuBitmap = bitmap;
                Intelligence = intel;

                BlurColorMatrix = new ColorMatrixEffect { Source = GpuBitmap, ColorMatrix = new Matrix5x4 { M11 = 0.5f, M22 = 0.5f, M33 = 0.5f, M44 = 1.0f, M54 = 0 } };
                BlurEffect = new GaussianBlurEffect { Source = BlurColorMatrix, BlurAmount = 40.0f, BorderMode = EffectBorderMode.Hard };
            }

            public void Dispose()
            {
                if (!IsSharedResource)
                {
                    GpuBitmap?.Dispose();
                    BlurColorMatrix?.Dispose();
                    BlurEffect?.Dispose();
                }
            }
        }

        public CinematicWindow(List<string> imagePaths, int startIndex)
        {
            this.InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;

            ViewModel = new CinematicViewModel(imagePaths, startIndex);
            ViewModel.TrajectoryRefreshRequested += () => UpdateActiveTrajectoriesAsync();
            ViewModel.SlideNavigationRequested += () => _ = ManualNavigateAsync();
            _ = ViewModel.RestoreAutoSaveAsync();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = false;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
                presenter.IsResizable = true;

                // Standard .NET Persistence for Unpackaged WinUI 3 Apps
                string settingsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernImageViewer");
                System.IO.Directory.CreateDirectory(settingsDir);
                string settingsFile = System.IO.Path.Combine(settingsDir, "WindowBounds.json");

                try
                {
                    if (System.IO.File.Exists(settingsFile))
                    {
                        var json = System.IO.File.ReadAllText(settingsFile);
                        var bounds = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                        if (bounds != null)
                        {
                            if (bounds.TryGetValue("Width", out int w) && bounds.TryGetValue("Height", out int h))
                                _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

                            if (bounds.TryGetValue("X", out int x) && bounds.TryGetValue("Y", out int y))
                                _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                        }
                    }
                    else
                    {
                        SetDefaultCinematicBounds();
                    }
                }
                catch
                {
                    SetDefaultCinematicBounds();
                }
            }

            _idleTimer = DispatcherQueue.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromSeconds(3);
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            this.Closed += CinematicWindow_Closed;
            this.Activate();
        }

        private void SetDefaultCinematicBounds()
        {
            try
            {
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                int targetHeight = (int)(displayArea.WorkArea.Height * 0.9375);
                int targetWidth = (int)(targetHeight * (16.0 / 9.0));
                _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
            }
            catch
            {
                _appWindow.Resize(new Windows.Graphics.SizeInt32(2400, 1350));
            }
        }

        // --------------------------------------------------------
        // KEYBOARD ACCELERATORS
        // --------------------------------------------------------
        private void Accelerator_PlayPause(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            ViewModel.TogglePlay();
            SyncPlayPauseUI();
            args.Handled = true;
        }

        private void Accelerator_Prev(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            ViewModel.MovePrevious();
            args.Handled = true;
        }

        private void Accelerator_Next(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            ViewModel.MoveNext();
            args.Handled = true;
        }

        private void Accelerator_Escape(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (DirectorSidebar.IsPaneOpen)
            {
                DirectorSidebar.IsPaneOpen = false;
                args.Handled = true;
            }
        }

        // --------------------------------------------------------
        // ENGINE PIPELINE
        // --------------------------------------------------------
        private async Task InitializeFaceDetectorAsync()
        {
            if (FaceDetector.IsSupported)
                _faceDetector = await FaceDetector.CreateAsync();
        }

        private async void UpdateActiveTrajectoriesAsync()
        {
            if (_primaryFrame == null || SlideshowCanvas.ActualWidth <= 0) return;

            int currentId = ++_trajectoryUpdateId;
            await Task.Delay(50);
            if (currentId != _trajectoryUpdateId) return;

            _mathCts?.Cancel();
            _mathCts = new CancellationTokenSource();
            var token = _mathCts.Token;

            var settings = ViewModel.GetCurrentEffectiveSettings();
            var request = new PlaybackRequest(SlideshowCanvas.Size, settings);

            try
            {
                var newSequence = await Task.Run(() => _director.GetSequence(_primaryFrame.Intelligence, request.Bounds, request.Settings, token), token);

                if (!token.IsCancellationRequested)
                {
                    _currentSequence = newSequence;
                    var newTrajectory = _currentSequence.Dequeue();

                    // Seamless Resume Injection
                    if (_injectFrozenCoordinates && _primaryFrame != null)
                    {
                        newTrajectory.StartScale = _frozenScale;
                        newTrajectory.StartPan = _frozenPan;
                        newTrajectory.StartRotation = _frozenRotation;

                        _slideStartTime = TimeSpan.Zero;
                        _injectFrozenCoordinates = false;
                    }

                    _primaryFrame.Trajectory = newTrajectory;
                    _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                    SlideshowCanvas.Invalidate();
                    DispatcherQueue.TryEnqueue(() => UpdateGhostViewport());
                }
            }
            catch (TaskCanceledException) { }
        }

        private async Task ManualNavigateAsync()
        {
            if (_isManuallyNavigating || ViewModel.ImagePaths.Count == 0) return;
            _isManuallyNavigating = true;

            try
            {
                _sessionCts?.Cancel();
                _sessionCts = new CancellationTokenSource();
                var token = _sessionCts.Token;

                _mathCts?.Cancel();

                _isPreloading = false;
                _currentSequence.Clear();
                _nextSequence.Clear();

                if (_secondaryFrame != null && _primaryFrame != null && _secondaryFrame.GpuBitmap == _primaryFrame.GpuBitmap)
                {
                    _secondaryFrame = null;
                }
                else
                {
                    _secondaryFrame?.Dispose();
                    _secondaryFrame = null;
                }

                var newFrame = await DecodeAndMarshalImageAsync(ViewModel.CurrentIndex, token);
                if (newFrame == null || token.IsCancellationRequested)
                {
                    if (newFrame != null) DispatcherQueue.TryEnqueue(() => newFrame.Dispose());
                    return;
                }

                var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetCurrentEffectiveSettings());
                var queue = await Task.Run(() => _director.GetSequence(newFrame.Intelligence, request.Bounds, request.Settings, token), token);

                if (token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() => newFrame.Dispose());
                    return;
                }

                _primaryFrame?.Dispose();
                _primaryFrame = newFrame;
                _currentSequence = queue;
                _primaryFrame.Trajectory = _currentSequence.Dequeue();

                _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                _slideStartTime = TimeSpan.Zero;
                _crossfadeBlurAlpha = 0f;
                _crossfadeSubjectAlpha = 0f;

                if (!ViewModel.IsPlaying)
                {
                    ViewModel.TogglePlay();
                }

                ResetTargetBoxVisuals();
            }
            finally
            {
                _isManuallyNavigating = false;
            }
        }

        private async Task PreloadNextImageAsync(int indexToLoad, CancellationToken token)
        {
            var frame = await DecodeAndMarshalImageAsync(indexToLoad, token);
            if (frame == null || token.IsCancellationRequested)
            {
                // Marshaled Thread Affinity for unmanaged GPU resource disposal
                if (frame != null) DispatcherQueue.TryEnqueue(() => frame.Dispose());
                _isPreloading = false;
                return;
            }

            string nextFileName = System.IO.Path.GetFileName(ViewModel.ImagePaths[indexToLoad]);
            var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetEffectiveSettingsFor(nextFileName));

            try
            {
                var queue = await Task.Run(() => _director.GetSequence(frame.Intelligence, request.Bounds, request.Settings, token), token);

                if (!token.IsCancellationRequested)
                {
                    _secondaryFrame = frame;
                    _nextSequence = queue;
                    _secondaryFrame.Trajectory = _nextSequence.Dequeue();
                }
                else DispatcherQueue.TryEnqueue(() => frame.Dispose());
            }
            catch (Exception) { DispatcherQueue.TryEnqueue(() => frame.Dispose()); }
            finally { _isPreloading = false; }
        }

        private async Task InitialLoadAsync()
        {
            _sessionCts?.Cancel();
            _sessionCts = new CancellationTokenSource();
            var token = _sessionCts.Token;

            var frame = await DecodeAndMarshalImageAsync(ViewModel.CurrentIndex, token);
            if (frame != null && !token.IsCancellationRequested)
            {
                var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetCurrentEffectiveSettings());
                var queue = await Task.Run(() => _director.GetSequence(frame.Intelligence, request.Bounds, request.Settings, token), token);

                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _primaryFrame = frame;
                        _currentSequence = queue;
                        _primaryFrame.Trajectory = _currentSequence.Dequeue();
                        _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                        _slideStartTime = TimeSpan.Zero;
                        ResetTargetBoxVisuals();
                    });
                }
                else DispatcherQueue.TryEnqueue(() => frame.Dispose());
            }
            else if (frame != null) DispatcherQueue.TryEnqueue(() => frame.Dispose());
        }

        private async Task<SlideFrame?> DecodeAndMarshalImageAsync(int index, CancellationToken token)
        {
            if (index < 0 || index >= ViewModel.ImagePaths.Count || _canvasDevice == null) return null;

            string filePath = ViewModel.ImagePaths[index];

            var result = await Task.Run(() => SceneAnalysisService.AnalyzeImageAsync(filePath, _faceDetector, true, token), token);

            if (result.Bitmap == null) return null;

            SlideFrame? newFrame = null;
            var tcs = new TaskCompletionSource<bool>();

            bool isEnqueued = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!token.IsCancellationRequested && _canvasDevice != null)
                    {
                        var gpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, result.Bitmap);
                        newFrame = new SlideFrame(gpuBitmap, result.Intel);
                    }
                }
                finally
                {
                    result.Bitmap.Dispose();
                    tcs.SetResult(true);
                }
            });

            if (!isEnqueued)
            {
                result.Bitmap.Dispose();
                tcs.SetResult(false);
            }

            await tcs.Task;
            return newFrame;
        }

        private void CompositionTarget_Rendering(object sender, object e)
        {
            if (_primaryFrame == null || SlideshowCanvas == null) return;

            var args = e as RenderingEventArgs;
            TimeSpan totalTime = args?.RenderingTime ?? TimeSpan.Zero;

            if (TelemetryText != null && TelemetryOverlay.Visibility == Visibility.Visible)
            {
                if (StateText != null && StateBadge != null)
                {
                    if (ViewModel.IsOverrideMode)
                    {
                        StateText.Text = "ENGINE STATE: CUSTOM OVERRIDE";
                        StateText.Foreground = _telemetryOverrideText;
                        StateBadge.Background = _telemetryOverrideBg;
                    }
                    else
                    {
                        StateText.Text = "ENGINE STATE: AUTO (Global Default)";
                        StateText.Foreground = _telemetryAutoText;
                        StateBadge.Background = _telemetryAutoBg;
                    }
                }

                string easing = _primaryFrame.Trajectory.IsSnapZoom ? "Snap-Zoom Curve" : "Cubic";
                string displayTechnique = string.IsNullOrEmpty(_primaryFrame.Trajectory.Technique) ? "None" : _primaryFrame.Trajectory.Technique;
                var effective = ViewModel.GetCurrentEffectiveSettings();
                string targetStatus = effective.FocusTargetRect != null ? $"Manual Target Lock" : "Auto (Engine Choice)";

                double elapsedForTelemetry = ViewModel.IsPlaying ? (totalTime - _slideStartTime).TotalSeconds : 0;
                double remainingForTelemetry = Math.Max(0, _slideDurationSeconds - elapsedForTelemetry);

                TelemetryText.Text =
                    $"Slide: {ViewModel.CurrentIndex + 1} / {ViewModel.ImagePaths.Count}\n" +
                    $"File: {ViewModel.CurrentFileName}\n" +
                    $"Target: {targetStatus}\n" +
                    $"Technique: {displayTechnique}\n" +
                    $"Strategy: {_primaryFrame.Trajectory.StrategyName}\n" +
                    $"Easing: {easing} | Int: {effective.IntensityPercent}%\n" +
                    $"Zoom: {_primaryFrame.Trajectory.StartScale:F2}x → {_primaryFrame.Trajectory.EndScale:F2}x\n" +
                    $"Pan Cur:  ({_primaryFrame.CurrentPan.X:F0}, {_primaryFrame.CurrentPan.Y:F0})\n" +
                    $"Pan Dest: ({_primaryFrame.Trajectory.EndPan.X:F0}, {_primaryFrame.Trajectory.EndPan.Y:F0})\n" +
                    $"Remaining: {remainingForTelemetry:F1}s";
            }

            if (!ViewModel.IsPlaying || _isManuallyNavigating)
            {
                SlideshowCanvas.Invalidate();
                return;
            }

            if (_lastRenderTime == TimeSpan.Zero) _lastRenderTime = totalTime;
            TimeSpan delta = totalTime - _lastRenderTime;
            _lastRenderTime = totalTime;

            if (_isTargetingFrozen)
            {
                if (_slideStartTime != TimeSpan.Zero)
                {
                    _slideStartTime += delta;
                }
                SlideshowCanvas.Invalidate();
            }

            if (_slideStartTime == TimeSpan.Zero) _slideStartTime = totalTime;

            double elapsedSeconds = (totalTime - _slideStartTime).TotalSeconds;
            double progress = elapsedSeconds / _slideDurationSeconds;

            double crossfadeDuration = (_primaryFrame?.Trajectory.CrossfadeDurationSeconds ?? 2.5);
            double cfStartElapsed = Math.Max(0, _slideDurationSeconds - crossfadeDuration);

            double preloadTrigger = Math.Min(0.70, (cfStartElapsed - 1.0) / _slideDurationSeconds);
            if (progress >= preloadTrigger && _secondaryFrame == null && !_isPreloading)
            {
                _isPreloading = true;

                if (_currentSequence.Count > 0)
                {
                    var nextTransform = _currentSequence.Peek();
                    if (nextTransform.RequiresCut) { }
                    _isPreloading = false;
                }
                else
                {
                    int nextIndex = ViewModel.CurrentIndex;
                    if (ViewModel.IsShuffleEnabled)
                    {
                        nextIndex = Random.Shared.Next(ViewModel.ImagePaths.Count);
                    }
                    else if (ViewModel.IsLoopEnabled || ViewModel.CurrentIndex < ViewModel.ImagePaths.Count - 1)
                    {
                        nextIndex = (ViewModel.CurrentIndex + 1) % ViewModel.ImagePaths.Count;
                    }

                    if (nextIndex != ViewModel.CurrentIndex && _sessionCts != null)
                    {
                        _ = PreloadNextImageAsync(nextIndex, _sessionCts.Token);
                    }
                }
            }

            if (progress >= 1.0)
            {
                if (_secondaryFrame != null)
                {
                    var oldPrimary = _primaryFrame;
                    _primaryFrame = _secondaryFrame;
                    _secondaryFrame = null;
                    oldPrimary?.Dispose();

                    if (_currentSequence.Count > 0 && _primaryFrame.Trajectory == _currentSequence.Peek())
                    {
                        _currentSequence.Dequeue();
                    }
                    else
                    {
                        _currentSequence = new Queue<CameraTransform>(_nextSequence);
                        _nextSequence.Clear();
                        ViewModel.AdvanceToNextSlideSilently();
                    }

                    _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                    _slideStartTime = totalTime;
                    elapsedSeconds = 0;
                    progress = 0;
                    _crossfadeBlurAlpha = 0f;
                    _crossfadeSubjectAlpha = 0f;
                }
                else if (_currentSequence.Count > 0)
                {
                    _primaryFrame.Trajectory = _currentSequence.Dequeue();
                    _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                    _slideStartTime = totalTime;
                    elapsedSeconds = 0;
                    progress = 0;
                }
                else
                {
                    progress = 1.0;
                }
            }

            float mainT = (float)Math.Clamp(progress, 0.0, 1.0);
            UpdateFrameVectors(_primaryFrame, mainT, ViewModel.GetCurrentEffectiveSettings().IntensityPercent);

            if (elapsedSeconds >= cfStartElapsed && _secondaryFrame != null)
            {
                double cfElapsed = elapsedSeconds - cfStartElapsed;
                double blendWindow = _secondaryFrame.Trajectory.RequiresCut ? 0.8 : 1.5;

                _crossfadeBlurAlpha = (float)Math.Clamp(cfElapsed / blendWindow, 0.0, 1.0);
                _crossfadeSubjectAlpha = _crossfadeBlurAlpha;

                if (_crossfadeBlurAlpha <= 0.01f || _currentTransitionType == -1)
                    _currentTransitionType = Random.Shared.Next(3);

                UpdateFrameVectors(_secondaryFrame, 0f, ViewModel.GetCurrentEffectiveSettings().IntensityPercent);
            }
            else
            {
                _crossfadeBlurAlpha = 0f;
                _crossfadeSubjectAlpha = 0f;
                _currentTransitionType = -1;
            }

            SlideProgressBar.Value = mainT;
            SlideshowCanvas.Invalidate();
        }

        private void SlideshowCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            try
            {
                Vector2 pOffset = Vector2.Zero;
                Vector2 sOffset = Vector2.Zero;

                if (_crossfadeSubjectAlpha > 0f && _currentTransitionType > 0)
                {
                    float width = (float)sender.Size.Width;
                    float t = _crossfadeSubjectAlpha;
                    float easedT = t * t * (3f - 2f * t);

                    if (_currentTransitionType == 1)
                    {
                        pOffset = new Vector2(-width * easedT, 0);
                        sOffset = new Vector2(width * (1f - easedT), 0);
                    }
                    else if (_currentTransitionType == 2)
                    {
                        pOffset = new Vector2(width * easedT, 0);
                        sOffset = new Vector2(-width * (1f - easedT), 0);
                    }
                }

                if (_primaryFrame != null && _primaryFrame.GpuBitmap.Device == sender.Device)
                    DrawFrame(args, _primaryFrame, sender.Size, 1.0f, 1.0f, pOffset);

                if (_secondaryFrame != null && _secondaryFrame.GpuBitmap.Device == sender.Device && (_crossfadeBlurAlpha > 0f || _crossfadeSubjectAlpha > 0f))
                {
                    float sAlpha = _currentTransitionType > 0 ? 1.0f : _crossfadeSubjectAlpha;
                    float bAlpha = _currentTransitionType > 0 ? 1.0f : _crossfadeBlurAlpha;
                    DrawFrame(args, _secondaryFrame, sender.Size, bAlpha, sAlpha, sOffset);
                }
            }
            catch (ObjectDisposedException) { }
        }

        private void DrawFrame(CanvasDrawEventArgs args, SlideFrame frame, Size bounds, float blurAlpha, float subjectAlpha, Vector2 transitionOffset = default)
        {
            var imgSize = frame.Intelligence.ImageSize;
            var traj = frame.Trajectory;
            bool isEditMode = DirectorSidebar.IsPaneOpen && !ViewModel.IsPlaying;

            if (isEditMode)
            {
                float scaleToFit = (float)Math.Min(bounds.Width / imgSize.Width, bounds.Height / imgSize.Height);
                float drawW = (float)(imgSize.Width * scaleToFit);
                float drawH = (float)(imgSize.Height * scaleToFit);
                float offsetX = (float)(bounds.Width - drawW) / 2f;
                float offsetY = (float)(bounds.Height - drawH) / 2f;

                var editTransform = Matrix3x2.CreateScale(scaleToFit) * Matrix3x2.CreateTranslation(offsetX, offsetY);
                args.DrawingSession.Transform = editTransform;
                args.DrawingSession.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, 1.0f, CanvasImageInterpolation.HighQualityCubic);
                return;
            }

            if (traj.Mode == LayoutMode.Portrait)
            {
                if (blurAlpha > 0f)
                {
                    float bw = (float)bounds.Width;
                    float bh = (float)bounds.Height;
                    float iw = (float)imgSize.Width;
                    float ih = (float)imgSize.Height;

                    float bgScale = Math.Max(bw / iw, bh / ih);
                    Vector2 bgCenter = new Vector2(bw / 2f, bh / 2f);

                    float bufferX = Math.Max(0f, ((iw * bgScale * 1.15f) - bw) / 2.0f);
                    float bufferY = Math.Max(0f, ((ih * bgScale * 1.15f) - bh) / 2.0f);

                    float rawPanX = frame.CurrentPan.X * -0.1f;
                    float rawPanY = frame.CurrentPan.Y * -0.1f;

                    var bgTransform = Matrix3x2.CreateScale(bgScale * 1.15f)
                                    * Matrix3x2.CreateTranslation(
                                        Math.Clamp(rawPanX, -bufferX, bufferX),
                                        Math.Clamp(rawPanY, -bufferY, bufferY))
                                    * Matrix3x2.CreateRotation(frame.CurrentRotation * -0.5f, bgCenter)
                                    * Matrix3x2.CreateTranslation(transitionOffset);

                    args.DrawingSession.Transform = bgTransform;

                    args.DrawingSession.DrawImage(frame.BlurEffect, 0, 0, frame.GpuBitmap.Bounds, blurAlpha, CanvasImageInterpolation.Linear);
                }

                if (subjectAlpha > 0f)
                {
                    var fgCenter = new Vector2((float)bounds.Width / 2f, (float)bounds.Height / 2f);
                    var fgTransform = Matrix3x2.CreateScale(frame.CurrentScale) * Matrix3x2.CreateTranslation(frame.CurrentPan) * Matrix3x2.CreateRotation(frame.CurrentRotation, fgCenter) * Matrix3x2.CreateTranslation(transitionOffset);

                    args.DrawingSession.Transform = fgTransform;
                    args.DrawingSession.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, subjectAlpha, CanvasImageInterpolation.HighQualityCubic);
                }
            }
            else
            {
                if (subjectAlpha > 0f)
                {
                    var centerPoint = new Vector2((float)bounds.Width / 2f, (float)bounds.Height / 2f);
                    var transform = Matrix3x2.CreateScale(frame.CurrentScale) * Matrix3x2.CreateTranslation(frame.CurrentPan) * Matrix3x2.CreateRotation(frame.CurrentRotation, centerPoint) * Matrix3x2.CreateTranslation(transitionOffset);

                    args.DrawingSession.Transform = transform;
                    args.DrawingSession.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, subjectAlpha, CanvasImageInterpolation.HighQualityCubic);
                }
            }
        }

        // --------------------------------------------------------
        // UI SYNC & WYSIWYG
        // --------------------------------------------------------
        private void SyncPlayPauseUI()
        {
            if (DirectorSidebar.IsPaneOpen)
            {
                WysiwygOverlay.Visibility = Visibility.Visible;
                WysiwygHint.Visibility = Visibility.Visible;
                ResetTargetBoxVisuals();
            }
            else
            {
                WysiwygOverlay.Visibility = Visibility.Collapsed;
                WysiwygHint.Visibility = Visibility.Collapsed;
            }

            if (ViewModel.IsPlaying)
            {
                _slideStartTime = TimeSpan.Zero;
            }
            SlideshowCanvas.Invalidate();
        }

        private void WysiwygOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DirectorSidebar.IsPaneOpen && !ViewModel.IsPlaying)
            {
                ResetTargetBoxVisuals();
            }
        }

        private void WysiwygOverlay_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_primaryFrame == null || WysiwygOverlay.ActualWidth == 0) return;

            var delta = e.GetCurrentPoint(WysiwygOverlay).Properties.MouseWheelDelta;
            double aspectRatio = WysiwygOverlay.ActualWidth / WysiwygOverlay.ActualHeight;

            double oldWidth = FocusTargetBox.Width;
            double oldHeight = FocusTargetBox.Height;

            bool isFineIncrement = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                                   Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            double step = isFineIncrement ? 2.0 : 30.0;
            double newWidth = delta > 0 ? oldWidth - step : oldWidth + step;

            newWidth = Math.Clamp(newWidth, 80, WysiwygOverlay.ActualWidth);
            double newHeight = newWidth / aspectRatio;

            double widthDiff = oldWidth - newWidth;
            double heightDiff = oldHeight - newHeight;

            double newX = Math.Clamp(FocusBoxTransform.X + (widthDiff / 2.0), 0, WysiwygOverlay.ActualWidth - newWidth);
            double newY = Math.Clamp(FocusBoxTransform.Y + (heightDiff / 2.0), 0, WysiwygOverlay.ActualHeight - newHeight);

            FocusTargetBox.Width = newWidth;
            FocusTargetBox.Height = newHeight;
            FocusBoxTransform.X = newX;
            FocusBoxTransform.Y = newY;

            UpdateAreaReadout();

            _isTargetingFrozen = true;
            CommitTargetToViewModel();
            _isTargetingFrozen = false;

            e.Handled = true;
        }

        private void UpdateGhostViewport()
        {
            if (_primaryFrame?.Trajectory == null || WysiwygOverlay.ActualWidth == 0 || WysiwygOverlay.ActualHeight == 0)
            {
                GhostViewport.Visibility = Visibility.Collapsed;
                return;
            }

            var bounds = new Size(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight);
            GhostViewport.Width = bounds.Width;
            GhostViewport.Height = bounds.Height;
            GhostViewport.Visibility = Visibility.Visible;

            float scaleToFit = (float)Math.Min(bounds.Width / _primaryFrame.Intelligence.ImageSize.Width, bounds.Height / _primaryFrame.Intelligence.ImageSize.Height);
            float offsetX = (float)(bounds.Width - (_primaryFrame.Intelligence.ImageSize.Width * scaleToFit)) / 2f;
            float offsetY = (float)(bounds.Height - (_primaryFrame.Intelligence.ImageSize.Height * scaleToFit)) / 2f;
            var editTransform = Matrix3x2.CreateScale(scaleToFit) * Matrix3x2.CreateTranslation(offsetX, offsetY);

            var traj = _primaryFrame.Trajectory;
            var centerPoint = new Vector2((float)bounds.Width / 2f, (float)bounds.Height / 2f);
            
            var playbackTransform = Matrix3x2.CreateScale(traj.EndScale) * Matrix3x2.CreateTranslation(traj.EndPan) * Matrix3x2.CreateRotation(traj.EndRotation, centerPoint);

            if (Matrix3x2.Invert(playbackTransform, out Matrix3x2 invPlayback))
            {
                var combined = invPlayback * editTransform;
                GhostTransform.Matrix = new Microsoft.UI.Xaml.Media.Matrix(combined.M11, combined.M12, combined.M21, combined.M22, combined.M31, combined.M32);
            }
        }

        private void ResetTargetBoxVisuals()
        {
            if (WysiwygOverlay.ActualWidth == 0 || WysiwygOverlay.ActualHeight == 0) return;

            if (ViewModel.CurrentDraft.FocusTargetRect != null && _primaryFrame != null)
            {
                double scaleToFit = Math.Min(WysiwygOverlay.ActualWidth / _primaryFrame.Intelligence.ImageSize.Width, WysiwygOverlay.ActualHeight / _primaryFrame.Intelligence.ImageSize.Height);
                double drawW = _primaryFrame.Intelligence.ImageSize.Width * scaleToFit;
                double drawH = _primaryFrame.Intelligence.ImageSize.Height * scaleToFit;
                double offsetX = (WysiwygOverlay.ActualWidth - drawW) / 2.0;
                double offsetY = (WysiwygOverlay.ActualHeight - drawH) / 2.0;

                FocusTargetBox.Width = ViewModel.CurrentDraft.FocusTargetRect.Width * drawW;
                FocusTargetBox.Height = ViewModel.CurrentDraft.FocusTargetRect.Height * drawH;
                FocusBoxTransform.X = (ViewModel.CurrentDraft.FocusTargetRect.X * drawW) + offsetX;
                FocusBoxTransform.Y = (ViewModel.CurrentDraft.FocusTargetRect.Y * drawH) + offsetY;
            }
            else
            {
                double aspectRatio = WysiwygOverlay.ActualWidth / WysiwygOverlay.ActualHeight;
                double defaultWidth = Math.Max(120, WysiwygOverlay.ActualWidth * 0.25);
                double defaultHeight = defaultWidth / aspectRatio;

                FocusTargetBox.Width = defaultWidth;
                FocusTargetBox.Height = defaultHeight;
                FocusBoxTransform.X = (WysiwygOverlay.ActualWidth - defaultWidth) / 2.0;
                FocusBoxTransform.Y = (WysiwygOverlay.ActualHeight - defaultHeight) / 2.0;
            }

            UpdateAreaReadout();
        }

        private bool _isDraggingBox = false;
        private Windows.Foundation.Point _dragStartPoint;

        private void FocusTargetBox_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isDraggingBox = true;
            _isTargetingFrozen = true;
            FocusTargetBox.CapturePointer(e.Pointer);
            _dragStartPoint = e.GetCurrentPoint(WysiwygOverlay).Position;
            e.Handled = true;

            ViewModel.CaptureUndoSnapshot();
        }

        private void FocusTargetBox_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isDraggingBox || _primaryFrame == null) return;

            var currentPoint = e.GetCurrentPoint(WysiwygOverlay).Position;
            double deltaX = currentPoint.X - _dragStartPoint.X;
            double deltaY = currentPoint.Y - _dragStartPoint.Y;

            FocusBoxTransform.X = Math.Clamp(FocusBoxTransform.X + deltaX, 0, WysiwygOverlay.ActualWidth - FocusTargetBox.Width);
            FocusBoxTransform.Y = Math.Clamp(FocusBoxTransform.Y + deltaY, 0, WysiwygOverlay.ActualHeight - FocusTargetBox.Height);
            _dragStartPoint = currentPoint;
        }

        private void FocusTargetBox_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isDraggingBox = false;
            FocusTargetBox.ReleasePointerCapture(e.Pointer);

            if (_primaryFrame != null)
            {
                _frozenScale = _primaryFrame.CurrentScale;
                _frozenPan = _primaryFrame.CurrentPan;
                _frozenRotation = _primaryFrame.CurrentRotation;
                _injectFrozenCoordinates = true;

                double scaleToFit = Math.Min(WysiwygOverlay.ActualWidth / _primaryFrame.Intelligence.ImageSize.Width, WysiwygOverlay.ActualHeight / _primaryFrame.Intelligence.ImageSize.Height);
                double drawW = _primaryFrame.Intelligence.ImageSize.Width * scaleToFit;
                double drawH = _primaryFrame.Intelligence.ImageSize.Height * scaleToFit;
                double offsetX = (WysiwygOverlay.ActualWidth - drawW) / 2.0;
                double offsetY = (WysiwygOverlay.ActualHeight - drawH) / 2.0;

                double imgX = (FocusBoxTransform.X - offsetX) / drawW;
                double imgY = (FocusBoxTransform.Y - offsetY) / drawH;
                double imgW = FocusTargetBox.Width / drawW;
                double imgH = FocusTargetBox.Height / drawH;

                var rect = new NormalizedRect
                {
                    X = Math.Clamp(imgX, 0.0, 1.0),
                    Y = Math.Clamp(imgY, 0.0, 1.0),
                    Width = Math.Clamp(imgW, 0.01, 1.0),
                    Height = Math.Clamp(imgH, 0.01, 1.0)
                };

                ViewModel.RegisterFocusTarget(rect);
            }

            _isTargetingFrozen = false;
            e.Handled = true;
        }

        private void BtnClearTarget_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.CurrentDraft.FocusTargetRect = null;
            ViewModel.IsOverrideMode = false;
            ResetTargetBoxVisuals();
        }

        private void UpdateAreaReadout()
        {
            double boxArea = FocusTargetBox.Width * FocusTargetBox.Height;
            double totalArea = WysiwygOverlay.ActualWidth * WysiwygOverlay.ActualHeight;
            double percent = (boxArea / totalArea) * 100.0;
            if (AreaReadoutText != null) AreaReadoutText.Text = $"{Math.Round(percent)}%";
        }

        private void CommitTargetToViewModel()
        {
            if (_primaryFrame == null) return;

            _frozenScale = _primaryFrame.CurrentScale;
            _frozenPan = _primaryFrame.CurrentPan;
            _frozenRotation = _primaryFrame.CurrentRotation;
            _injectFrozenCoordinates = true;

            double scaleToFit = Math.Min(WysiwygOverlay.ActualWidth / _primaryFrame.Intelligence.ImageSize.Width, WysiwygOverlay.ActualHeight / _primaryFrame.Intelligence.ImageSize.Height);
            double drawW = _primaryFrame.Intelligence.ImageSize.Width * scaleToFit;
            double drawH = _primaryFrame.Intelligence.ImageSize.Height * scaleToFit;
            double offsetX = (WysiwygOverlay.ActualWidth - drawW) / 2.0;
            double offsetY = (WysiwygOverlay.ActualHeight - drawH) / 2.0;

            double imgX = (FocusBoxTransform.X - offsetX) / drawW;
            double imgY = (FocusBoxTransform.Y - offsetY) / drawH;
            double imgW = FocusTargetBox.Width / drawW;
            double imgH = FocusTargetBox.Height / drawH;

            var rect = new NormalizedRect
            {
                X = Math.Clamp(imgX, 0.0, 1.0),
                Y = Math.Clamp(imgY, 0.0, 1.0),
                Width = Math.Clamp(imgW, 0.01, 1.0),
                Height = Math.Clamp(imgH, 0.01, 1.0)
            };

            ViewModel.RegisterFocusTarget(rect);
            UpdateGhostViewport();
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isTargetingFrozen = true;
            ViewModel.CaptureUndoSnapshot();
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            CommitTargetToViewModel();
            _isTargetingFrozen = false;
        }

        private void ThumbTopLeft_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Math.Max(80, FocusTargetBox.Width - e.HorizontalChange);
            double aspectRatio = WysiwygOverlay.ActualWidth / WysiwygOverlay.ActualHeight;
            double newHeight = newWidth / aspectRatio;

            double widthDiff = FocusTargetBox.Width - newWidth;
            double heightDiff = FocusTargetBox.Height - newHeight;

            double newX = Math.Clamp(FocusBoxTransform.X + widthDiff, 0, FocusBoxTransform.X + FocusTargetBox.Width - 80);
            double newY = Math.Clamp(FocusBoxTransform.Y + heightDiff, 0, FocusBoxTransform.Y + FocusTargetBox.Height - (80 / aspectRatio));

            FocusTargetBox.Width = newWidth;
            FocusTargetBox.Height = newHeight;
            FocusBoxTransform.X = newX;
            FocusBoxTransform.Y = newY;
            UpdateAreaReadout();
        }

        private void ThumbTopRight_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Math.Max(80, FocusTargetBox.Width + e.HorizontalChange);
            double aspectRatio = WysiwygOverlay.ActualWidth / WysiwygOverlay.ActualHeight;
            double newHeight = newWidth / aspectRatio;

            double heightDiff = FocusTargetBox.Height - newHeight;

            double newX = FocusBoxTransform.X;
            double newY = Math.Clamp(FocusBoxTransform.Y + heightDiff, 0, FocusBoxTransform.Y + FocusTargetBox.Height - (80 / aspectRatio));

            if (newX + newWidth <= WysiwygOverlay.ActualWidth)
            {
                FocusTargetBox.Width = newWidth;
                FocusTargetBox.Height = newHeight;
                FocusBoxTransform.Y = newY;
                UpdateAreaReadout();
            }
        }

        private void ThumbBottomLeft_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Math.Max(80, FocusTargetBox.Width - e.HorizontalChange);
            double aspectRatio = WysiwygOverlay.ActualWidth / WysiwygOverlay.ActualHeight;
            double newHeight = newWidth / aspectRatio;

            double widthDiff = FocusTargetBox.Width - newWidth;

            double newX = Math.Clamp(FocusBoxTransform.X + widthDiff, 0, FocusBoxTransform.X + FocusTargetBox.Width - 80);
            double newY = FocusBoxTransform.Y;

            if (newY + newHeight <= WysiwygOverlay.ActualHeight)
            {
                FocusTargetBox.Width = newWidth;
                FocusTargetBox.Height = newHeight;
                FocusBoxTransform.X = newX;
                UpdateAreaReadout();
            }
        }

        private void ThumbBottomRight_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Math.Max(80, FocusTargetBox.Width + e.HorizontalChange);
            double aspectRatio = WysiwygOverlay.ActualWidth / WysiwygOverlay.ActualHeight;
            double newHeight = newWidth / aspectRatio;

            if (FocusBoxTransform.X + newWidth <= WysiwygOverlay.ActualWidth && FocusBoxTransform.Y + newHeight <= WysiwygOverlay.ActualHeight)
            {
                FocusTargetBox.Width = newWidth;
                FocusTargetBox.Height = newHeight;
                UpdateAreaReadout();
            }
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ActionBar != null) ActionBar.Opacity = 1.0;
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ActionBar.Opacity = 0;
            DirectorSidebar.IsPaneOpen = false;
        }

        private void Scrubber_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is int index)
            {
                ViewModel.NavigateTo(index);
            }
        }

        private void IdleTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (ActionBar != null) ActionBar.Opacity = 0.0;
            _idleTimer.Stop();
        }

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.TogglePlay();
            SyncPlayPauseUI();
        }

        private void BtnDirectorToggle_Click(object sender, RoutedEventArgs e) => DirectorSidebar.IsPaneOpen = !DirectorSidebar.IsPaneOpen;
        private void BtnBack_Click(object sender, RoutedEventArgs e) => ViewModel.MovePrevious();
        private void BtnForward_Click(object sender, RoutedEventArgs e) => ViewModel.MoveNext();
        private void BtnUndo_Click(object sender, RoutedEventArgs e) => ViewModel.ExecuteUndo();

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            savePicker.FileTypeChoices.Add("Cinematic Project", new List<string>() { ".json" });
            savePicker.SuggestedFileName = "DirectorProject";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null) await ViewModel.ExportProjectAsync(file);
        }

        private async void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            var openPicker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            openPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            openPicker.FileTypeFilter.Add(".json");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null) await ViewModel.LoadProjectAsync(file);
        }

        private void DirectorSidebar_PaneOpening(Microsoft.UI.Xaml.Controls.SplitView sender, object args)
        {
            if (ViewModel.IsPlaying) ViewModel.TogglePlay();
            WysiwygOverlay.Visibility = Visibility.Visible;
            WysiwygHint.Visibility = Visibility.Visible;
            TelemetryOverlay.Visibility = Visibility.Visible;
            ResetTargetBoxVisuals();
        }

        private void DirectorSidebar_PaneClosed(Microsoft.UI.Xaml.Controls.SplitView sender, object args)
        {
            WysiwygOverlay.Visibility = Visibility.Collapsed;
            WysiwygHint.Visibility = Visibility.Collapsed;
            TelemetryOverlay.Visibility = Visibility.Collapsed;
            if (!ViewModel.IsPlaying)
            {
                ViewModel.TogglePlay();
                _slideStartTime = TimeSpan.Zero;
            }
        }

        private float StandardEase(float t) => t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3f) / 2f;

        private float SnapZoomEase(float t)
        {
            if (t < 0.15f) return t * 0.2f;
            else if (t < 0.45f)
            {
                float p = (t - 0.15f) / 0.30f;
                float snap = p < 0.5f ? 16f * p * p * p * p * p : 1f - (float)Math.Pow(-2f * p + 2f, 5f) / 2f;
                return 0.03f + (snap * 0.92f);
            }
            else
            {
                float p = (t - 0.45f) / 0.55f;
                float settle = 1f - (float)Math.Pow(1f - p, 3f);
                return 0.95f + (settle * 0.05f);
            }
        }

        private void UpdateFrameVectors(SlideFrame frame, float rawProgress, double intensityPercent)
        {
            var traj = frame.Trajectory;
            float t = traj.IsSnapZoom ? SnapZoomEase(rawProgress) : StandardEase(rawProgress);

            frame.CurrentScale = traj.StartScale + (traj.EndScale - traj.StartScale) * t;
            frame.CurrentRotation = traj.StartRotation + (traj.EndRotation - traj.StartRotation) * t;

            Vector2 linearPan = traj.StartPan + (traj.EndPan - traj.StartPan) * t;
            Vector2 delta = traj.EndPan - traj.StartPan;
            float travel = delta.Length();

            if (travel > 5f && !traj.IsSnapZoom)
            {
                Vector2 perp = new Vector2(-delta.Y, delta.X);
                if (perp.LengthSquared() > 0.0001f)
                {
                    perp = Vector2.Normalize(perp) * traj.CurveSign;
                    float curveMag = (float)Math.Min(28f + (float)(intensityPercent * 0.35f), travel * 0.13f);
                    float curveT = (float)Math.Sin(Math.PI * t);
                    linearPan += perp * (curveMag * curveT);
                }
            }
            frame.CurrentPan = linearPan;
        }

        private void CinematicWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                string settingsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernImageViewer");
                System.IO.Directory.CreateDirectory(settingsDir);
                string settingsFile = System.IO.Path.Combine(settingsDir, "WindowBounds.json");

                var bounds = new Dictionary<string, int>
                {
                    { "Width", _appWindow.Size.Width },
                    { "Height", _appWindow.Size.Height },
                    { "X", _appWindow.Position.X },
                    { "Y", _appWindow.Position.Y }
                };

                System.IO.File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(bounds));

                _idleTimer?.Stop();
                _sessionCts?.Cancel();
                _mathCts?.Cancel();
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
                if (SlideshowCanvas != null) SlideshowCanvas.RemoveFromVisualTree();
                _primaryFrame?.Dispose();
                _secondaryFrame?.Dispose();
            }
            catch { }
        }

        private void SlideshowCanvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            _canvasDevice = sender.Device;
            _primaryFrame?.Dispose();
            _secondaryFrame?.Dispose();
            _primaryFrame = null;
            _secondaryFrame = null;

            args.TrackAsyncAction(Task.Run(async () =>
            {
                await InitializeFaceDetectorAsync();
                DispatcherQueue.TryEnqueue(() => { _ = InitialLoadAsync(); });
            }).AsAsyncAction());
        }

        private void SlideshowCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Width - _lastCanvasWidth) > 1 || Math.Abs(e.NewSize.Height - _lastCanvasHeight) > 1)
            {
                _lastCanvasWidth = e.NewSize.Width;
                _lastCanvasHeight = e.NewSize.Height;
            }
        }
    }
}
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media.FaceAnalysis;
using Windows.Storage;
using ModernImageViewer.Cinematic.Data;
using ModernImageViewer.Cinematic.ViewModels;
using ModernImageViewer.Cinematic.Services;


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
        private Windows.Graphics.SizeInt32 _restoreSize;
        private Windows.Graphics.PointInt32 _restorePos;
        private bool _haveRestoreBounds;
        private CanvasDevice? _canvasDevice;
        private FaceDetector? _faceDetector;
        private readonly CinematicDirector _director = new();

        private SlideFrame? _primaryFrame;
        private SlideFrame? _secondaryFrame;

        private bool _isPreloading = false;
        private bool _previewing = false;
        private Border? _activeKeyframeBox;
        private bool _selectedIsStart = true;
        private bool _isDraggingBox;
        private bool _isResizing;
        private string _resizeCorner = "";
        private Windows.Foundation.Point _dragStartPoint;
        private double _boxStartLeft, _boxStartTop, _boxStartW, _boxStartH;
        private TimeSpan _slideStartTime = TimeSpan.Zero;
        private float _crossfadeBlurAlpha = 0f;
        private float _crossfadeSubjectAlpha = 0f;
        private double _slideDurationSeconds = 12.0;

        private double _lastCanvasWidth;
        private double _lastCanvasHeight;

        private DispatcherQueueTimer _idleTimer;
        private bool _pointerOverChrome;
        private int _seqScrollGeneration;
        private bool _sequenceWanted;
        private bool _isManuallyNavigating = false;
        private int _currentTransitionType = -1;
        private int _trajectoryUpdateId = 0;

        private Queue<CameraTransform> _currentSequence = new();
        private Queue<CameraTransform> _nextSequence = new();

        private class SlideFrame : IDisposable
        {
            public CanvasBitmap GpuBitmap { get; }
            public SceneIntelligence Intelligence { get; }
            public CameraTransform Trajectory { get; set; } = new();
            public bool IsSharedResource { get; set; } = false;
            public Rect EngineStartCrop;
            public Rect EngineEndCrop;
            public bool HasEngineCrops;

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
            ApplyPlaybarToggle(BtnDirectorToggle, false);
            ApplyPlaybarToggle(BtnSequenceToggle, false);

            ViewModel = new CinematicViewModel(imagePaths, startIndex);
            ViewModel.TrajectoryRefreshRequested += () => UpdateActiveTrajectoriesAsync();
            ViewModel.SlideNavigationRequested += () => _ = ManualNavigateAsync();
            ViewModel.SequenceChanged += () => DiscardPreload();
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.CurrentIndex))
                    DispatcherQueue.TryEnqueue(ScrollSequenceToCurrent);
            };
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

                RestoreCinematicWindowPlacement(presenter);
                _appWindow.Changed += CinematicAppWindow_Changed;
            }

            _idleTimer = DispatcherQueue.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromSeconds(3);
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            this.Closed += CinematicWindow_Closed;
            this.Activate();
        }

        private void RestoreCinematicWindowPlacement(OverlappedPresenter presenter)
        {
            string settingsFile = System.IO.Path.Combine(AppPaths.LocalDirectory, "WindowBounds.json");
            try
            {
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                var work = displayArea.WorkArea;

                int w = 0, h = 0, x = int.MinValue, y = int.MinValue;
                bool maximized = false;
                if (System.IO.File.Exists(settingsFile))
                {
                    var json = System.IO.File.ReadAllText(settingsFile);
                    var bounds = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (bounds != null)
                    {
                        bounds.TryGetValue("Width", out w);
                        bounds.TryGetValue("Height", out h);
                        if (bounds.TryGetValue("X", out int sx)) x = sx;
                        if (bounds.TryGetValue("Y", out int sy)) y = sy;
                        maximized = bounds.TryGetValue("Maximized", out int mx) && mx == 1;
                    }
                }

                bool fillsWork = w >= work.Width - 32 && h >= work.Height - 32;
                if (w < 400 || h < 300 || fillsWork)
                {
                    if (fillsWork) maximized = true;
                    h = (int)(work.Height * 0.9375);
                    w = (int)(h * (16.0 / 9.0));
                    x = int.MinValue;
                    y = int.MinValue;
                }

                _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
                if (x != int.MinValue && y != int.MinValue)
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));

                _restoreSize = new Windows.Graphics.SizeInt32(w, h);
                _restorePos = _appWindow.Position;
                _haveRestoreBounds = true;

                if (maximized) presenter.Maximize();
            }
            catch
            {
                SetDefaultCinematicBounds();
            }
        }

        private void CinematicAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange) return;
            if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Restored
                && _appWindow.Size.Width > 0 && _appWindow.Size.Height > 0)
            {
                _restoreSize = _appWindow.Size;
                _restorePos = _appWindow.Position;
                _haveRestoreBounds = true;
            }
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

        private void DiscardPreload()
        {
            _isPreloading = false;
            _nextSequence.Clear();
            if (_secondaryFrame != null)
            {
                if (_primaryFrame == null || _secondaryFrame.GpuBitmap != _primaryFrame.GpuBitmap)
                    _secondaryFrame.Dispose();
                _secondaryFrame = null;
            }
        }

        private void SnapshotEngineCrops(SlideFrame frame, CameraTransform traj, Size canvas)
        {
            var img = frame.Intelligence.ImageSize;
            frame.EngineStartCrop = CameraMath.CropFromCamera(traj.StartScale, traj.StartPan, canvas, img);
            frame.EngineEndCrop = CameraMath.CropFromCamera(traj.EndScale, traj.EndPan, canvas, img);
            frame.HasEngineCrops = true;
        }

        private void ApplyCropsToTrajectory(Rect startCrop, Rect endCrop)
        {
            if (_primaryFrame == null) return;
            var canvas = SlideshowCanvas.Size;
            var img = _primaryFrame.Intelligence.ImageSize;
            var s = CameraMath.CameraFromCrop(startCrop, canvas, img);
            var e = CameraMath.CameraFromCrop(endCrop, canvas, img);
            _primaryFrame.Trajectory.StartScale = s.Scale;
            _primaryFrame.Trajectory.StartPan = s.Pan;
            _primaryFrame.Trajectory.EndScale = e.Scale;
            _primaryFrame.Trajectory.EndPan = e.Pan;
            var curve = ViewModel.CurveOverride;
            _primaryFrame.Trajectory.EaseKind = string.IsNullOrEmpty(curve) ? "EaseInOut" : curve;
            _primaryFrame.Trajectory.IsSnapZoom = curve == "SnapZoom";
            _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
        }

        private async void UpdateActiveTrajectoriesAsync(bool forceEngineReroll = false)
        {
            if (_primaryFrame == null || SlideshowCanvas.ActualWidth <= 0) return;

            var settings = ViewModel.GetCurrentEffectiveSettings();
            bool customCrops = settings.StartCrop is Rect && settings.EndCrop is Rect;

            if (!forceEngineReroll && !customCrops && _primaryFrame.HasEngineCrops && !ViewModel.HasUnsavedZoomPan)
            {
                ApplyCropsToTrajectory(_primaryFrame.EngineStartCrop, _primaryFrame.EngineEndCrop);
                if (DirectorSidebar.IsPaneOpen && !_previewing) ResetKeyframeBoxes();
                UpdateStatusUi();
                SlideshowCanvas.Invalidate();
                return;
            }

            if (!forceEngineReroll && customCrops && !ViewModel.HasUnsavedZoomPan)
            {
                ApplyCropsToTrajectory(settings.StartCrop!.Value, settings.EndCrop!.Value);
                if (DirectorSidebar.IsPaneOpen && !_previewing) ResetKeyframeBoxes();
                UpdateStatusUi();
                SlideshowCanvas.Invalidate();
                return;
            }

            int currentId = ++_trajectoryUpdateId;
            await Task.Delay(50);
            if (currentId != _trajectoryUpdateId) return;

            _mathCts?.Cancel();
            _mathCts = new CancellationTokenSource();
            var token = _mathCts.Token;
            var request = new PlaybackRequest(SlideshowCanvas.Size, settings);

            try
            {
                var newSequence = await Task.Run(() => _director.GetSequence(_primaryFrame.Intelligence, request.Bounds, request.Settings, token), token);

                if (!token.IsCancellationRequested && newSequence.Count > 0)
                {
                    _currentSequence = newSequence;
                    var newTrajectory = _currentSequence.Dequeue();
                    _primaryFrame!.Trajectory = newTrajectory;
                    _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                    if (!customCrops)
                        SnapshotEngineCrops(_primaryFrame, newTrajectory, request.Bounds);
                    SlideshowCanvas.Invalidate();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DirectorSidebar.IsPaneOpen && !_previewing && !ViewModel.HasUnsavedZoomPan)
                            ResetKeyframeBoxes();
                        UpdateStatusUi();
                    });
                }
            }
            catch (TaskCanceledException) { }
        }

        private async Task ManualNavigateAsync()
        {
            if (_isManuallyNavigating || ViewModel.ImagePaths.Count == 0) return;
            _isManuallyNavigating = true;
            ViewModel.ClearPendingNextIndex();

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
                _primaryFrame.Trajectory = _currentSequence.Count > 0 ? _currentSequence.Dequeue() : new CameraTransform();
                if (request.Settings.StartCrop == null || request.Settings.EndCrop == null)
                    SnapshotEngineCrops(_primaryFrame, _primaryFrame.Trajectory, request.Bounds);

                _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                _slideStartTime = TimeSpan.Zero;
                _crossfadeBlurAlpha = 0f;
                _crossfadeSubjectAlpha = 0f;

                if (!ViewModel.IsPlaying && !DirectorSidebar.IsPaneOpen)
                {
                    ViewModel.TogglePlay();
                }

                ResetKeyframeBoxes();
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

            var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetEffectiveSettingsForIndex(indexToLoad));

            try
            {
                var queue = await Task.Run(() => _director.GetSequence(frame.Intelligence, request.Bounds, request.Settings, token), token);

                if (!token.IsCancellationRequested)
                {
                    _secondaryFrame = frame;
                    _nextSequence = queue;
                    _secondaryFrame.Trajectory = _nextSequence.Count > 0 ? _nextSequence.Dequeue() : new CameraTransform();
                    if (request.Settings.StartCrop == null || request.Settings.EndCrop == null)
                        SnapshotEngineCrops(_secondaryFrame, _secondaryFrame.Trajectory, request.Bounds);
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
                        _primaryFrame.Trajectory = _currentSequence.Count > 0 ? _currentSequence.Dequeue() : new CameraTransform();
                        if (request.Settings.StartCrop == null || request.Settings.EndCrop == null)
                            SnapshotEngineCrops(_primaryFrame, _primaryFrame.Trajectory, request.Bounds);
                        _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                        _slideStartTime = TimeSpan.Zero;
                        ResetKeyframeBoxes();
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

        private void CompositionTarget_Rendering(object? sender, object e)
        {
            if (_primaryFrame == null || SlideshowCanvas == null) return;

            var args = e as RenderingEventArgs;
            TimeSpan totalTime = args?.RenderingTime ?? TimeSpan.Zero;

            if (DirectorSidebar.IsPaneOpen)
                UpdateStatusUi();

            if (!ViewModel.IsPlaying || _isManuallyNavigating)
            {
                if (DirectorSidebar.IsPaneOpen && !_previewing)
                    SlideshowCanvas.Invalidate();
                return;
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
                    _isPreloading = false;
                }
                else
                {
                    int nextIndex = ViewModel.PeekNextIndex();
                    if (nextIndex != ViewModel.CurrentIndex && _sessionCts != null)
                    {
                        _ = PreloadNextImageAsync(nextIndex, _sessionCts.Token);
                    }
                    else
                    {
                        _isPreloading = false;
                    }
                }
            }

            if (progress >= 1.0)
            {
                if (_previewing)
                {
                    _previewing = false;
                    if (ViewModel.IsPlaying) ViewModel.TogglePlay();
                    DiscardPreload();
                    SlideProgressBar.Value = 0;
                    SyncPlayPauseUI();
                    SlideshowCanvas.Invalidate();
                    return;
                }
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
                    // Non-null: guarded by the _primaryFrame == null early-return at the top of this method.
                    _primaryFrame!.Trajectory = _currentSequence.Dequeue();
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
            UpdateFrameVectors(_primaryFrame!, mainT, ViewModel.GetCurrentEffectiveSettings().IntensityPercent);

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
            bool isEditMode = DirectorSidebar.IsPaneOpen && !_previewing;

            if (isEditMode)
            {
                DrawEditWorkspace(args, frame, bounds);
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
            if (DirectorSidebar.IsPaneOpen && ViewModel.IsPlaying && !_previewing)
                ViewModel.TogglePlay();

            bool showEditOverlay = DirectorSidebar.IsPaneOpen && !_previewing;
            WysiwygOverlay.Visibility = showEditOverlay ? Visibility.Visible : Visibility.Collapsed;
            WysiwygHint.Visibility = Visibility.Collapsed;
            StatusBanner.Visibility = DirectorSidebar.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            PreviewBanner.Visibility = _previewing ? Visibility.Visible : Visibility.Collapsed;
            if (showEditOverlay && !ViewModel.HasUnsavedZoomPan) ResetKeyframeBoxes();
            UpdateStatusUi();

            if (ViewModel.IsPlaying)
            {
                _slideStartTime = TimeSpan.Zero;
            }
            SlideshowCanvas.Invalidate();
        }

        private void UpdateStatusUi()
        {
            if (StateText == null || _primaryFrame == null) return;
            var traj = _primaryFrame.Trajectory;
            bool draft = ViewModel.HasUnsavedZoomPan;
            bool custom = ViewModel.IsOverrideMode;

            if (draft)
            {
                StateText.Text = "DRAFT — not saved";
                StateText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 210, 70));
                StateBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 200, 0));
            }
            else if (custom)
            {
                StateText.Text = "CUSTOM — you saved this zoom/pan";
                StateText.Foreground = _telemetryOverrideText;
                StateBadge.Background = _telemetryOverrideBg;
            }
            else
            {
                StateText.Text = "AUTO — engine zoom/pan, not customized";
                StateText.Foreground = _telemetryAutoText;
                StateBadge.Background = _telemetryAutoBg;
            }

            string zoom = $"Zoom/pan {traj.StartScale:F2}× → {traj.EndScale:F2}×";
            string who = draft
                ? "Show still uses the last saved state until you Save."
                : custom
                    ? "This slide uses your saved rectangles, not the engine."
                    : "These rectangles are the engine's. Different start/end is normal Auto Ken Burns — not your edit.";
            if (StatusDetailText != null) StatusDetailText.Text = who;
            if (ZoomPanReadout != null) ZoomPanReadout.Text = zoom;
            if (TelemetryText != null)
            {
                TelemetryText.Text =
                    $"{zoom}\n" +
                    $"Slide {ViewModel.CurrentIndex + 1} / {ViewModel.Track.Count}  ·  {ViewModel.CurrentFileName}\n" +
                    $"Saved: {(custom ? "Custom zoom/pan" : "Auto (engine)")}  ·  Unsaved draft: {(draft ? "yes" : "no")}";
            }
        }

        private void DrawEditWorkspace(CanvasDrawEventArgs args, SlideFrame frame, Size bounds)
        {
            var imgSize = frame.Intelligence.ImageSize;
            if (!TryEditLayout(bounds.Width, bounds.Height, imgSize, out float scale, out float drawW, out float drawH, out float ox, out float oy))
                return;

            var ds = args.DrawingSession;
            ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(ox, oy);
            ds.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, 1.0f, CanvasImageInterpolation.HighQualityCubic);
            ds.Transform = Matrix3x2.Identity;

            var selected = SelectedBox();
            if (selected != null)
            {
                float l = (float)Canvas.GetLeft(selected);
                float t = (float)Canvas.GetTop(selected);
                float w = (float)selected.Width;
                float h = (float)selected.Height;
                var dim = Windows.UI.Color.FromArgb(150, 0, 0, 0);
                ds.FillRectangle(0, 0, (float)bounds.Width, t, dim);
                ds.FillRectangle(0, t + h, (float)bounds.Width, (float)bounds.Height - (t + h), dim);
                ds.FillRectangle(0, t, l, h, dim);
                ds.FillRectangle(l + w, t, (float)bounds.Width - (l + w), h, dim);
            }

            DrawFramedPreview(ds, frame, bounds);
        }

        private void DrawFramedPreview(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, SlideFrame frame, Size bounds)
        {
            if (SelectedBox() == null || bounds.Width < 1) return;

            float pipW = 280f;
            float pipH = pipW * (float)(bounds.Height / Math.Max(1, bounds.Width));
            float pipX = 16f;
            float pipY = (float)bounds.Height - pipH - 16f;
            var pipRect = new Rect(pipX, pipY, pipW, pipH);
            float k = pipW / (float)Math.Max(1, bounds.Width);
            float scale = _selectedIsStart ? frame.Trajectory.StartScale : frame.Trajectory.EndScale;
            var pan = _selectedIsStart ? frame.Trajectory.StartPan : frame.Trajectory.EndPan;

            ds.FillRectangle(pipRect, Windows.UI.Color.FromArgb(240, 0, 0, 0));
            using (ds.CreateLayer(1f, pipRect))
            {
                ds.Transform = Matrix3x2.CreateScale(scale * k) * Matrix3x2.CreateTranslation(pan.X * k + pipX, pan.Y * k + pipY);
                ds.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, 1.0f, CanvasImageInterpolation.HighQualityCubic);
                ds.Transform = Matrix3x2.Identity;
            }

            var accent = _selectedIsStart
                ? Windows.UI.Color.FromArgb(255, 0, 212, 255)
                : Windows.UI.Color.FromArgb(255, 255, 138, 0);
            ds.DrawRectangle(pipRect, accent, 2.5f);
            using var fmt = new CanvasTextFormat { FontSize = 13, FontFamily = "Segoe UI" };
            ds.DrawText(_selectedIsStart ? "Zoom/pan start, filling the screen" : "Zoom/pan end, filling the screen",
                pipX + 8, pipY + 6, accent, fmt);
        }

        private void WysiwygOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DirectorSidebar.IsPaneOpen && !ViewModel.IsPlaying && !_isDraggingBox && !_isResizing)
                ResetKeyframeBoxes();
        }

        private void WysiwygOverlay_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var box = SelectedBox();
            if (box == null || _primaryFrame == null) return;
            if (!TryEditLayout(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight, _primaryFrame.Intelligence.ImageSize, out _, out double drawW, out double drawH, out double ox, out double oy))
                return;

            double aspect = OutputAspect();
            double oldW = box.Width, oldH = box.Height;
            double oldL = Canvas.GetLeft(box), oldT = Canvas.GetTop(box);
            double delta = e.GetCurrentPoint(WysiwygOverlay).Properties.MouseWheelDelta;
            double newW = oldW + (delta > 0 ? -28 : 28);
            ApplyBoxRect(box, oldL + (oldW - newW) / 2, oldT + (oldH - newW / aspect) / 2, newW, newW / aspect);
            ApplyChrome();
            CommitKeyframeCrops(persist: false);
            SlideshowCanvas.Invalidate();
            e.Handled = true;
        }

        private bool TryEditLayout(double viewW, double viewH, Size img, out float scale, out float drawW, out float drawH, out float ox, out float oy)
        {
            scale = drawW = drawH = ox = oy = 0;
            if (viewW <= 1 || viewH <= 1) return false;
            double iw = Math.Max(1, img.Width);
            double ih = Math.Max(1, img.Height);
            double pad = Math.Clamp(Math.Min(viewW, viewH) * 0.09, 52, 96);
            double availW = Math.Max(32, viewW - 2 * pad);
            double availH = Math.Max(32, viewH - 2 * pad);
            scale = (float)Math.Min(availW / iw, availH / ih);
            drawW = (float)(iw * scale);
            drawH = (float)(ih * scale);
            ox = (float)((viewW - drawW) / 2);
            oy = (float)((viewH - drawH) / 2);
            return drawW > 1 && drawH > 1;
        }

        private bool TryEditLayout(double viewW, double viewH, Size img, out float scale, out double drawW, out double drawH, out double ox, out double oy)
        {
            bool ok = TryEditLayout(viewW, viewH, img, out scale, out float fw, out float fh, out float fox, out float foy);
            drawW = fw; drawH = fh; ox = fox; oy = foy;
            return ok;
        }

        private double OutputAspect() => SlideshowCanvas.ActualWidth / Math.Max(1, SlideshowCanvas.ActualHeight);

        private Border? SelectedBox() => _selectedIsStart ? StartFrameBox : EndFrameBox;

        private void PlaceCrop(Border box, Rect crop, double drawW, double drawH, double ox, double oy)
        {
            box.Width = Math.Max(8, crop.Width * drawW);
            box.Height = Math.Max(8, crop.Height * drawH);
            Canvas.SetLeft(box, ox + crop.X * drawW);
            Canvas.SetTop(box, oy + crop.Y * drawH);
        }

        private void ApplyBoxRect(Border box, double left, double top, double width, double height)
        {
            double aspect = OutputAspect();
            double viewW = WysiwygOverlay.ActualWidth;
            double viewH = WysiwygOverlay.ActualHeight;
            width = Math.Clamp(width, 32, viewW);
            height = width / aspect;
            if (height > viewH) { height = viewH; width = height * aspect; }
            left = Math.Clamp(left, 0, viewW - width);
            top = Math.Clamp(top, 0, viewH - height);
            box.Width = width;
            box.Height = height;
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
        }

        private Rect CropFromBox(Border box, double drawW, double drawH, double ox, double oy)
        {
            double x = (Canvas.GetLeft(box) - ox) / drawW;
            double y = (Canvas.GetTop(box) - oy) / drawH;
            double w = Math.Max(0.02, box.Width / drawW);
            double h = Math.Max(0.02, box.Height / drawH);
            return new Rect(x, y, w, h);
        }

        private void ResetKeyframeBoxes()
        {
            if (StartFrameBox == null || EndFrameBox == null || _primaryFrame == null) return;
            if (_isDraggingBox || _isResizing) return;
            if (ViewModel.HasUnsavedZoomPan) { ApplyChrome(); UpdateStatusUi(); return; }
            if (!TryEditLayout(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight, _primaryFrame.Intelligence.ImageSize, out _, out double drawW, out double drawH, out double ox, out double oy))
                return;

            var canvas = new Size(SlideshowCanvas.ActualWidth, SlideshowCanvas.ActualHeight);
            var img = _primaryFrame.Intelligence.ImageSize;
            var traj = _primaryFrame.Trajectory;

            Rect start, end;
            if (ViewModel.IsOverrideMode && ViewModel.CurrentDraft.StartCrop != null && ViewModel.CurrentDraft.EndCrop != null)
            {
                start = new Rect(ViewModel.CurrentDraft.StartCrop.X, ViewModel.CurrentDraft.StartCrop.Y, ViewModel.CurrentDraft.StartCrop.Width, ViewModel.CurrentDraft.StartCrop.Height);
                end = new Rect(ViewModel.CurrentDraft.EndCrop.X, ViewModel.CurrentDraft.EndCrop.Y, ViewModel.CurrentDraft.EndCrop.Width, ViewModel.CurrentDraft.EndCrop.Height);
            }
            else if (_primaryFrame.HasEngineCrops)
            {
                start = _primaryFrame.EngineStartCrop;
                end = _primaryFrame.EngineEndCrop;
            }
            else
            {
                start = CameraMath.CropFromCamera(traj.StartScale, traj.StartPan, canvas, img);
                end = CameraMath.CropFromCamera(traj.EndScale, traj.EndPan, canvas, img);
                SnapshotEngineCrops(_primaryFrame, traj, canvas);
            }

            PlaceCrop(StartFrameBox, start, drawW, drawH, ox, oy);
            PlaceCrop(EndFrameBox, end, drawW, drawH, ox, oy);
            ApplyCropsToTrajectory(start, end);
            SelectKeyframe(_selectedIsStart);
        }

        private void ApplyChrome()
        {
            if (StartFrameBox == null) return;

            var box = SelectedBox();
            if (box == null) return;
            double l = Canvas.GetLeft(box), t = Canvas.GetTop(box), w = box.Width, h = box.Height, hs = 16;
            Canvas.SetLeft(HandleTL, l - hs / 2); Canvas.SetTop(HandleTL, t - hs / 2);
            Canvas.SetLeft(HandleTR, l + w - hs / 2); Canvas.SetTop(HandleTR, t - hs / 2);
            Canvas.SetLeft(HandleBL, l - hs / 2); Canvas.SetTop(HandleBL, t + h - hs / 2);
            Canvas.SetLeft(HandleBR, l + w - hs / 2); Canvas.SetTop(HandleBR, t + h - hs / 2);

            var stroke = _selectedIsStart
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 212, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 138, 0));
            HandleTL.Stroke = stroke; HandleTR.Stroke = stroke; HandleBL.Stroke = stroke; HandleBR.Stroke = stroke;
            Canvas.SetZIndex(HandleTL, 30); Canvas.SetZIndex(HandleTR, 30);
            Canvas.SetZIndex(HandleBL, 30); Canvas.SetZIndex(HandleBR, 30);

            MotionPath.X1 = Canvas.GetLeft(StartFrameBox) + StartFrameBox.Width / 2;
            MotionPath.Y1 = Canvas.GetTop(StartFrameBox) + StartFrameBox.Height / 2;
            MotionPath.X2 = Canvas.GetLeft(EndFrameBox) + EndFrameBox.Width / 2;
            MotionPath.Y2 = Canvas.GetTop(EndFrameBox) + EndFrameBox.Height / 2;
        }

        private void SelectKeyframe(bool start)
        {
            _selectedIsStart = start;
            _activeKeyframeBox = start ? StartFrameBox : EndFrameBox;
            if (StartFrameBox != null)
            {
                StartFrameBox.BorderThickness = new Thickness(start ? 4 : 2);
                EndFrameBox.BorderThickness = new Thickness(start ? 2 : 4);
                Canvas.SetZIndex(StartFrameBox, start ? 20 : 10);
                Canvas.SetZIndex(EndFrameBox, start ? 10 : 20);
            }
            ApplyChrome();
            SlideshowCanvas.Invalidate();
        }

        private bool TryReadCrops(out Rect start, out Rect end)
        {
            start = end = default;
            if (_primaryFrame == null) return false;
            if (!TryEditLayout(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight, _primaryFrame.Intelligence.ImageSize, out _, out double drawW, out double drawH, out double ox, out double oy))
                return false;
            start = CropFromBox(StartFrameBox, drawW, drawH, ox, oy);
            end = CropFromBox(EndFrameBox, drawW, drawH, ox, oy);
            return true;
        }

        private void ApplyBoxesToTrajectory()
        {
            if (_primaryFrame == null || !TryReadCrops(out var start, out var end)) return;
            var canvas = new Size(SlideshowCanvas.ActualWidth, SlideshowCanvas.ActualHeight);
            var img = _primaryFrame.Intelligence.ImageSize;
            var s = CameraMath.CameraFromCrop(start, canvas, img);
            var e = CameraMath.CameraFromCrop(end, canvas, img);
            _primaryFrame.Trajectory.StartScale = s.Scale;
            _primaryFrame.Trajectory.StartPan = s.Pan;
            _primaryFrame.Trajectory.EndScale = e.Scale;
            _primaryFrame.Trajectory.EndPan = e.Pan;
            _primaryFrame.Trajectory.StrategyName = ViewModel.HasUnsavedZoomPan || ViewModel.IsOverrideMode ? "Custom" : _primaryFrame.Trajectory.StrategyName;
        }

        private void CommitKeyframeCrops(bool persist)
        {
            if (!TryReadCrops(out var start, out var end)) return;
            ApplyBoxesToTrajectory();
            if (persist)
            {
                ViewModel.SaveKeyframeCrops(
                    new NormalizedRect { X = start.X, Y = start.Y, Width = start.Width, Height = start.Height },
                    new NormalizedRect { X = end.X, Y = end.Y, Width = end.Width, Height = end.Height });
            }
            else
            {
                ViewModel.MarkZoomPanDraft();
            }
            UpdateStatusUi();
        }

        private void BeginBoxInteraction(Border box, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, bool resize, string corner)
        {
            SelectKeyframe(box == StartFrameBox);
            _isDraggingBox = !resize;
            _isResizing = resize;
            _resizeCorner = corner;
            _dragStartPoint = e.GetCurrentPoint(WysiwygOverlay).Position;
            _boxStartLeft = Canvas.GetLeft(box);
            _boxStartTop = Canvas.GetTop(box);
            _boxStartW = box.Width;
            _boxStartH = box.Height;
            WysiwygOverlay.CapturePointer(e.Pointer);
            ViewModel.CaptureUndoSnapshot();
            e.Handled = true;
        }

        private static bool BoxContains(Border box, Windows.Foundation.Point p)
        {
            double l = Canvas.GetLeft(box), t = Canvas.GetTop(box);
            return p.X >= l && p.X <= l + box.Width && p.Y >= t && p.Y <= t + box.Height;
        }

        private Border? SmallestBoxAt(Windows.Foundation.Point p)
        {
            bool inStart = StartFrameBox != null && BoxContains(StartFrameBox, p);
            bool inEnd = EndFrameBox != null && BoxContains(EndFrameBox, p);
            if (inStart && inEnd)
            {
                double sa = StartFrameBox!.Width * StartFrameBox.Height;
                double ea = EndFrameBox!.Width * EndFrameBox.Height;
                return sa <= ea ? StartFrameBox : EndFrameBox;
            }
            if (inEnd) return EndFrameBox;
            if (inStart) return StartFrameBox;
            return null;
        }

        private void WysiwygOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingBox || _isResizing) return;
            var box = SmallestBoxAt(e.GetCurrentPoint(WysiwygOverlay).Position);
            if (box == null) return;
            BeginBoxInteraction(box, e, resize: false, corner: "");
        }

        private void Handle_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement handle || SelectedBox() is not Border box) return;
            BeginBoxInteraction(box, e, resize: true, corner: handle.Tag as string ?? "br");
        }

        private void WysiwygOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if ((!_isDraggingBox && !_isResizing) || SelectedBox() is not Border box || _primaryFrame == null) return;
            if (WysiwygOverlay.ActualWidth <= 1) return;

            var now = e.GetCurrentPoint(WysiwygOverlay).Position;
            double dx = now.X - _dragStartPoint.X;
            double dy = now.Y - _dragStartPoint.Y;
            double aspect = OutputAspect();

            if (_isResizing)
            {
                double newW = _resizeCorner is "tl" or "bl" ? _boxStartW - dx : _boxStartW + dx;
                double newL = _resizeCorner is "tl" or "bl" ? _boxStartLeft + (_boxStartW - newW) : _boxStartLeft;
                double newH = newW / aspect;
                double newT = _resizeCorner is "tl" or "tr" ? _boxStartTop + _boxStartH - newH : _boxStartTop;
                ApplyBoxRect(box, newL, newT, newW, newH);
            }
            else
            {
                ApplyBoxRect(box, _boxStartLeft + dx, _boxStartTop + dy, _boxStartW, _boxStartH);
            }

            ApplyChrome();
            SlideshowCanvas.Invalidate();
        }

        private void WysiwygOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isDraggingBox && !_isResizing) return;
            _isDraggingBox = false;
            _isResizing = false;
            try { WysiwygOverlay.ReleasePointerCapture(e.Pointer); } catch { }
            CommitKeyframeCrops(persist: false);
            SlideshowCanvas.Invalidate();
            e.Handled = true;
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_primaryFrame == null) return;
            if (ViewModel.HasUnsavedZoomPan || ViewModel.IsOverrideMode)
                ApplyBoxesToTrajectory();
            _previewing = true;
            WysiwygOverlay.Visibility = Visibility.Collapsed;
            WysiwygHint.Visibility = Visibility.Collapsed;
            PreviewBanner.Visibility = Visibility.Visible;
            StatusBanner.Visibility = Visibility.Visible;
            _slideStartTime = TimeSpan.Zero;
            if (!ViewModel.IsPlaying) ViewModel.TogglePlay();
            UpdateStatusUi();
            SlideshowCanvas.Invalidate();
        }

        private void BtnSaveZoomPan_Click(object sender, RoutedEventArgs e)
        {
            CommitKeyframeCrops(persist: true);
            SlideshowCanvas.Invalidate();
        }

        private void BtnClearTarget_Click(object sender, RoutedEventArgs e)
        {
            _previewing = false;
            if (ViewModel.IsPlaying) ViewModel.TogglePlay();
            DiscardPreload();
            ViewModel.RevertToAuto();
            if (_primaryFrame != null && _primaryFrame.HasEngineCrops)
            {
                ApplyCropsToTrajectory(_primaryFrame.EngineStartCrop, _primaryFrame.EngineEndCrop);
                ResetKeyframeBoxes();
            }
            else
            {
                UpdateActiveTrajectoriesAsync(forceEngineReroll: true);
            }
            UpdateStatusUi();
            SlideshowCanvas.Invalidate();
        }

        private void PinChromeForDrag()
        {
            _pointerOverChrome = true;
            if (ActionBar != null) ActionBar.Opacity = 1.0;
            _idleTimer.Stop();
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            PinChromeForDrag();
            e.AcceptedOperation = SequenceBar.Visibility == Visibility.Visible && e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        private void RootGrid_Drop(object sender, DragEventArgs e)
        {
            if (SequenceBar.Visibility == Visibility.Visible)
                SequenceBar_Drop(sender, e);
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ActionBar != null) ActionBar.Opacity = 1.0;
            _idleTimer.Stop();
            if (!_pointerOverChrome) _idleTimer.Start();
        }

        private void Chrome_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _pointerOverChrome = true;
            if (ActionBar != null) ActionBar.Opacity = 1.0;
            _idleTimer.Stop();
        }

        private void Chrome_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _pointerOverChrome = false;
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ActionBar.Opacity = 0;
        }

        private void SequenceStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SequenceStrip.SelectedIndex >= 0 && SequenceStrip.SelectedIndex != ViewModel.CurrentIndex)
                ViewModel.NavigateTo(SequenceStrip.SelectedIndex);
            ScrollSequenceToCurrent();
        }

        private void ScrollSequenceToCurrent()
        {
            if (SequenceBar == null || SequenceBar.Visibility != Visibility.Visible || SequenceStrip == null) return;
            int index = ViewModel.CurrentIndex;
            if (index < 0 || index >= ViewModel.Track.Count) return;

            int gen = ++_seqScrollGeneration;
            if (SequenceStrip.ContainerFromIndex(index) is FrameworkElement)
            {
                CenterSequenceItem(index);
                return;
            }
            SequenceStrip.ScrollIntoView(ViewModel.Track[index]);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _seqScrollGeneration) return;
                CenterSequenceItem(index);
            });
        }

        private void CenterSequenceItem(int index)
        {
            if (SequenceStrip.ContainerFromIndex(index) is not FrameworkElement container) return;
            var sv = FindScrollViewer(SequenceStrip);
            if (sv == null || sv.ActualWidth <= 1) return;
            var origin = container.TransformToVisual(sv).TransformPoint(new Windows.Foundation.Point(0, 0));
            double itemCenter = origin.X + container.ActualWidth / 2.0;
            double target = sv.HorizontalOffset + itemCenter - sv.ViewportWidth / 2.0;
            target = Math.Clamp(target, 0, Math.Max(0, sv.ExtentWidth - sv.ViewportWidth));
            sv.ChangeView(target, null, null, disableAnimation: true);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                var nested = FindScrollViewer(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void SequenceStrip_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.NotifyTrackReordered();
            DiscardPreload();
        }

        private void BtnSeqNew_Click(object sender, RoutedEventArgs e)
        {
            DiscardPreload();
            ViewModel.ResetToLaunchSet();
        }

        private void BtnSeqHide_Click(object sender, RoutedEventArgs e) => ViewModel.HideCurrentClip();
        private void BtnSeqRemove_Click(object sender, RoutedEventArgs e)
        {
            DiscardPreload();
            ViewModel.RemoveCurrentClip();
        }
        private void BtnSeqDuplicate_Click(object sender, RoutedEventArgs e) => ViewModel.DuplicateCurrentClip();

        private async void BtnSeqAddFromFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            DiscardPreload();
            ViewModel.AddFromFolder(folder.Path);
        }

        private void SequenceBar_DragOver(object sender, DragEventArgs e)
        {
            PinChromeForDrag();
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.Handled = true;
        }

        private async void SequenceBar_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
            {
                if (item is StorageFile file)
                    paths.Add(file.Path);
                else if (item is StorageFolder folder)
                {
                    foreach (var f in await folder.GetFilesAsync())
                        paths.Add(f.Path);
                }
            }
            if (paths.Count > 0)
            {
                DiscardPreload();
                ViewModel.AddPaths(paths);
            }
        }

        private void IdleTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_pointerOverChrome)
            {
                _idleTimer.Start();
                return;
            }
            if (ActionBar != null) ActionBar.Opacity = 0.0;
            _idleTimer.Stop();
        }

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            if (DirectorSidebar.IsPaneOpen && !_previewing)
            {
                BtnPreview_Click(sender, e);
                return;
            }
            if (_previewing && ViewModel.IsPlaying)
            {
                _previewing = false;
                ViewModel.TogglePlay();
                SyncPlayPauseUI();
                return;
            }
            ViewModel.TogglePlay();
            SyncPlayPauseUI();
        }

        private void BtnSequenceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (DirectorSidebar.IsPaneOpen)
            {
                BtnSequenceToggle.IsChecked = false;
                ApplyPlaybarToggle(BtnSequenceToggle, false);
                SequenceBar.Visibility = Visibility.Collapsed;
                return;
            }
            _sequenceWanted = BtnSequenceToggle.IsChecked == true;
            SequenceBar.Visibility = _sequenceWanted ? Visibility.Visible : Visibility.Collapsed;
            ApplyPlaybarToggle(BtnSequenceToggle, _sequenceWanted);
            if (_sequenceWanted) DispatcherQueue.TryEnqueue(ScrollSequenceToCurrent);
        }

        private void HideSequenceForKenBurns()
        {
            _sequenceWanted = BtnSequenceToggle.IsChecked == true || SequenceBar.Visibility == Visibility.Visible;
            SequenceBar.Visibility = Visibility.Collapsed;
            BtnSequenceToggle.IsChecked = false;
            ApplyPlaybarToggle(BtnSequenceToggle, false);
        }

        private void RestoreSequenceAfterKenBurns()
        {
            BtnSequenceToggle.IsChecked = _sequenceWanted;
            SequenceBar.Visibility = _sequenceWanted ? Visibility.Visible : Visibility.Collapsed;
            ApplyPlaybarToggle(BtnSequenceToggle, _sequenceWanted);
            if (_sequenceWanted) DispatcherQueue.TryEnqueue(ScrollSequenceToCurrent);
        }

        private void BtnDirectorToggle_Click(object sender, RoutedEventArgs e)
        {
            bool on = BtnDirectorToggle.IsChecked == true;
            DirectorSidebar.IsPaneOpen = on;
            ApplyPlaybarToggle(BtnDirectorToggle, on);
        }

        private static void ApplyPlaybarToggle(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton btn, bool on)
        {
            btn.Background = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var fg = on
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180));
            btn.Foreground = fg;
            if (btn.Content is SymbolIcon icon)
                icon.Foreground = fg;
        }
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
            _previewing = false;
            DiscardPreload();
            if (ViewModel.IsPlaying) ViewModel.TogglePlay();
            SyncPlayPauseUI();
            BtnDirectorToggle.IsChecked = true;
            ApplyPlaybarToggle(BtnDirectorToggle, true);
            HideSequenceForKenBurns();
        }

        private void DirectorSidebar_PaneClosed(Microsoft.UI.Xaml.Controls.SplitView sender, object args)
        {
            bool wasPreviewing = _previewing;
            _previewing = false;
            WysiwygOverlay.Visibility = Visibility.Collapsed;
            WysiwygHint.Visibility = Visibility.Collapsed;
            if (StatusBanner != null) StatusBanner.Visibility = Visibility.Collapsed;
            if (PreviewBanner != null) PreviewBanner.Visibility = Visibility.Collapsed;
            DiscardPreload();
            if (wasPreviewing)
            {
                if (ViewModel.IsPlaying) ViewModel.TogglePlay();
                _slideStartTime = TimeSpan.Zero;
            }
            else if (!ViewModel.IsPlaying)
            {
                ViewModel.TogglePlay();
                _slideStartTime = TimeSpan.Zero;
            }
            BtnDirectorToggle.IsChecked = false;
            ApplyPlaybarToggle(BtnDirectorToggle, false);
            RestoreSequenceAfterKenBurns();
        }

        private float ApplyEase(CameraTransform traj, float raw)
        {
            float t = Math.Clamp(raw, 0f, 1f);
            string kind = string.IsNullOrEmpty(traj.EaseKind)
                ? (traj.IsSnapZoom ? "SnapZoom" : "EaseInOut")
                : traj.EaseKind;
            return kind switch
            {
                "Linear" => t,
                "EaseIn" => t * t * t,
                "EaseOut" => 1f - (float)Math.Pow(1f - t, 3),
                "SnapZoom" => SnapZoomEase(t),
                _ => StandardEase(t)
            };
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
            float t = ApplyEase(traj, rawProgress);

            frame.CurrentScale = traj.StartScale + (traj.EndScale - traj.StartScale) * t;
            frame.CurrentRotation = traj.StartRotation + (traj.EndRotation - traj.StartRotation) * t;

            Vector2 linearPan = traj.StartPan + (traj.EndPan - traj.StartPan) * t;
            Vector2 delta = traj.EndPan - traj.StartPan;
            float travel = delta.Length();

            if (travel > 5f && !traj.IsSnapZoom && traj.EaseKind != "Linear" && traj.EaseKind != "SnapZoom")
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
                string settingsFile = System.IO.Path.Combine(AppPaths.LocalDirectory, "WindowBounds.json");
                bool maximized = _appWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Maximized;
                var size = _haveRestoreBounds ? _restoreSize : _appWindow.Size;
                var pos = _haveRestoreBounds ? _restorePos : _appWindow.Position;

                var bounds = new Dictionary<string, int>
                {
                    { "Width", size.Width },
                    { "Height", size.Height },
                    { "X", pos.X },
                    { "Y", pos.Y },
                    { "Maximized", maximized ? 1 : 0 }
                };

                System.IO.File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(bounds));
                _appWindow.Changed -= CinematicAppWindow_Changed;

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
                UpdateActiveTrajectoriesAsync();
            }
        }
    }
}
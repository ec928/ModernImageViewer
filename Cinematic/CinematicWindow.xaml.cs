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
        private int _openedShowMissing;
        private bool _openedAShow;
        private bool _pointerOverChrome;
        private int _seqScrollGeneration;
        private bool _editing;
        private bool _isManuallyNavigating = false;
        private TimeSpan _fadeStartTime = TimeSpan.Zero;
        private TimeSpan _lastRenderTime = TimeSpan.Zero;
        private bool _nothingToPlayShown;
        private bool _isFullscreen;
        private bool _maximizedBeforeFullscreen;
        private int _trajectoryUpdateId = 0;

        private Queue<CameraTransform> _currentSequence = new();
        private Queue<CameraTransform> _nextSequence = new();
        private readonly TaskCompletionSource<bool> _restoreGate = new();

        public CinematicWindow(List<string> imagePaths, int startIndex, CinematicProject? show = null, int missingPictures = 0)
        {
            this.InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            ApplyPlaybarToggle(BtnEditShow, false);

            ViewModel = new CinematicViewModel(imagePaths, startIndex);
            ViewModel.TrajectoryRefreshRequested += () => UpdateActiveTrajectoriesAsync();
            ViewModel.SlideMotionChanged += () =>
            {
                _chosenMoves.Clear();
                DiscardPreload();
                UpdateActiveTrajectoriesAsync(forceEngineReroll: true);
            };
            ViewModel.MusicChanged += ApplyMusic;
            ViewModel.MusicVolumeChanged += ApplyMusicVolume;
            ViewModel.SlideNavigationRequested += () => _ = ManualNavigateAsync();
            ViewModel.SequenceChanged += () =>
            {
                ViewModel.ClearPendingNextIndex();
                DiscardPreload();
            };
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.CurrentIndex))
                    DispatcherQueue.TryEnqueue(ScrollSequenceToCurrent);
                else if (e.PropertyName == nameof(ViewModel.IsPlaying))
                    SyncMusicPlayback();
            };
            if (show != null) ViewModel.ApplyShow(show);
            _openedShowMissing = missingPictures;
            _openedAShow = show != null;
            bool otherShowOpen = (MainWindow.Instance?.CinematicWindowCount ?? 0) > 0;
            if (_openedAShow || otherShowOpen || !CinematicViewModel.CrashAutoSaveExists())
                _restoreGate.TrySetResult(true);
            RootGrid.Loaded += CinematicRoot_Loaded;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = false;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
                presenter.IsResizable = true;

                bool startFullscreen = RestoreCinematicWindowPlacement(presenter);
                _appWindow.Changed += CinematicAppWindow_Changed;
                if (startFullscreen) SetFullscreen(true);
            }

            _idleTimer = DispatcherQueue.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromSeconds(3);
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            this.Closed += CinematicWindow_Closed;
            this.Activate();
        }

        private async void CinematicRoot_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= CinematicRoot_Loaded;
            if (_openedAShow)
            {
                await ReportShowProblemsAsync(_openedShowMissing);
                return;
            }
            try
            {
                bool otherShowOpen = (MainWindow.Instance?.CinematicWindowCount ?? 1) > 1;
                if (!otherShowOpen && CinematicViewModel.CrashAutoSaveExists() && RootGrid.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Restore unsaved show?",
                        Content = "A previous cinematic session did not close cleanly. Restore it, or start this Play from the stills you just opened?",
                        PrimaryButtonText = "Restore",
                        SecondaryButtonText = "Start fresh",
                        XamlRoot = RootGrid.XamlRoot
                    };
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                        await ViewModel.RestoreAutoSaveAsync();
                    else
                        await ViewModel.DiscardAutoSaveAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cinematic] Restore prompt: {ex.Message}");
            }
            _restoreGate.TrySetResult(true);
        }

        private bool RestoreCinematicWindowPlacement(OverlappedPresenter presenter)
        {
            string settingsFile = System.IO.Path.Combine(AppPaths.LocalDirectory, "WindowBounds.json");
            try
            {
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                var work = displayArea.WorkArea;

                int w = 0, h = 0, x = int.MinValue, y = int.MinValue;
                bool maximized = false;
                bool fullscreen = false;
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
                        fullscreen = bounds.TryGetValue("Fullscreen", out int fs) && fs == 1;
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
                return fullscreen;
            }
            catch
            {
                SetDefaultCinematicBounds();
                return false;
            }
        }

        private void SetFullscreen(bool on)
        {
            if (on == _isFullscreen) return;
            if (on)
            {
                _maximizedBeforeFullscreen = _appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
                _isFullscreen = true;
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.Default);
                _isFullscreen = false;
            }
            IconFullscreen.Symbol = on ? Symbol.BackToWindow : Symbol.FullScreen;
            string name = on ? "Leave full screen (F11 or Esc)" : "Full screen (F11)";
            ToolTipService.SetToolTip(BtnFullscreen, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BtnFullscreen, name);
            // What was loaded next was framed for the old screen shape.
            DiscardPreload();
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_isFullscreen);

        private void Accelerator_Fullscreen(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            SetFullscreen(!_isFullscreen);
            args.Handled = true;
        }

        private void CinematicAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange) return;
            if (_isFullscreen) return;
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
            args.Handled = true;
            if (_previewing) EndPreview();
            else if (_editing) SetEditing(false);
            else if (_isFullscreen) SetFullscreen(false);
            else this.Close();
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ActionBar != null) ActionBar.Opacity = 1.0;
            _idleTimer.Stop();
            if (!_pointerOverChrome && !_editing) _idleTimer.Start();
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
            if (!_editing) _idleTimer.Start();
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_editing) ActionBar.Opacity = 0;
        }

        private void IdleTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_pointerOverChrome || _editing)
            {
                _idleTimer.Start();
                return;
            }
            if (ActionBar != null) ActionBar.Opacity = 0.0;
            _idleTimer.Stop();
        }

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            if (_editing && !_previewing)
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

        private void CinematicWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                string settingsFile = System.IO.Path.Combine(AppPaths.LocalDirectory, "WindowBounds.json");
                bool maximized = _isFullscreen
                    ? _maximizedBeforeFullscreen
                    : _appWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Maximized;
                var size = _haveRestoreBounds ? _restoreSize : _appWindow.Size;
                var pos = _haveRestoreBounds ? _restorePos : _appWindow.Position;

                var bounds = new Dictionary<string, int>
                {
                    { "Width", size.Width },
                    { "Height", size.Height },
                    { "X", pos.X },
                    { "Y", pos.Y },
                    { "Maximized", maximized ? 1 : 0 },
                    { "Fullscreen", _isFullscreen ? 1 : 0 }
                };

                System.IO.File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(bounds));
                _appWindow.Changed -= CinematicAppWindow_Changed;

                _idleTimer?.Stop();
                DisposeMusic();
                _sessionCts?.Cancel();
                _mathCts?.Cancel();
                _restoreGate.TrySetResult(true);
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
                if (SlideshowCanvas != null) SlideshowCanvas.RemoveFromVisualTree();
                _primaryFrame?.Dispose();
                _secondaryFrame?.Dispose();
                _primaryFrame = null;
                _secondaryFrame = null;
                _canvasDevice = null;

                if (MainWindow.Instance == null || MainWindow.Instance.CinematicWindowCount <= 1)
                    _ = ViewModel.DiscardAutoSaveAsync();
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
                var before = new Size(_lastCanvasWidth, _lastCanvasHeight);
                _lastCanvasWidth = e.NewSize.Width;
                _lastCanvasHeight = e.NewSize.Height;
                if (_primaryFrame != null && before.Width > 0 && before.Height > 0)
                {
                    foreach (var shot in _primaryFrame.Shots)
                        if (shot != _primaryFrame.Trajectory) ReAim(shot, before, e.NewSize, _primaryFrame.Intelligence.ImageSize);
                }
                UpdateActiveTrajectoriesAsync();
            }
        }
    }
}
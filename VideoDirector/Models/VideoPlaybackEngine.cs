using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using ModernImageViewer.VideoDirector.ViewModels;
using Microsoft.UI.Dispatching;

namespace ModernImageViewer.VideoDirector.Models
{
    public class VideoPlaybackEngine
    {
        private readonly Views.DirectorPlayerControl _playerControl;
        private readonly MediaPlayerElement _playerA;
        private readonly MediaPlayerElement _playerB;
        private readonly DirectorViewModel _viewModel;
        private readonly DispatcherQueue _dispatcher;

        private MediaPlayer _mediaPlayerA;
        private MediaPlayer _mediaPlayerB;

        private bool _isPlayerAActive = true;
        private CancellationTokenSource _playbackCts;
        private bool _isPaused = false;
        private DateTime _pauseStartTime;
        private TaskCompletionSource<bool> _skipTcs;

        // Animation state
        private bool _isAnimating = false;
        private bool _isPreparingTransition = false;
        private CinematicOperation _opA;
        private CinematicOperation _opB;
        private DateTime _opAStartTime;
        private DateTime _opBStartTime;
        private TimeSpan _opADuration;
        private TimeSpan _opBDuration;
        private bool _inTransition = false;
        private DateTime _transitionStartTime;
        
        // Target state for rendering loop
        private TimeSpan _renderDuration;
        private MediaPlayerElement _fadeOutElement;
        private MediaPlayerElement _fadeInElement;

        public CinematicOperation? CurrentPlayingOperation { get; private set; }

        // Overlay state
        private MediaPlayer _overlayMediaPlayer1;
        private MediaPlayer _overlayMediaPlayer2;
        private CinematicOperation _activeOverlay1;
        private CinematicOperation _activeOverlay2;
        private bool _isEditingOverlay = false;
        // Story time as of the start of the currently-playing clip; CurrentStoryTime is
        // derived from this plus the active player's real position every render frame.
        private TimeSpan _storyTimeAtClipStart = TimeSpan.Zero;

        public VideoPlaybackEngine(Views.DirectorPlayerControl playerControl, DirectorViewModel viewModel)
        {
            _playerControl = playerControl;
            _playerA = playerControl.PlayerA;
            _playerB = playerControl.PlayerB;
            _viewModel = viewModel;
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            InitializePlayers();
            InitializeOverlayPlayers();

            _viewModel.PlaybackSpeedChanged += ViewModel_PlaybackSpeedChanged;
            _viewModel.OperationSeekRequested += ViewModel_OperationSeekRequested;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_OperationSeekRequested(object sender, TimeSpan e)
        {
            SeekActiveOperation(e);
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectorViewModel.IsTelemetryVisible))
            {
                _dispatcher.TryEnqueue(() => UpdateTelemetryOverlay());
            }
        }

        private void ViewModel_PlaybackSpeedChanged(object sender, double speed)
        {
            double speedA = _opA != null ? speed * _opA.PlaybackSpeed : speed;
            double speedB = _opB != null ? speed * _opB.PlaybackSpeed : speed;

            _mediaPlayerA.PlaybackSession.PlaybackRate = speedA;
            _mediaPlayerB.PlaybackSession.PlaybackRate = speedB;
            
            if (speed == 0.0)
            {
                _mediaPlayerA.Pause();
                _mediaPlayerB.Pause();
            }
            else if (_isAnimating && !_isPaused)
            {
                if (_isPlayerAActive && speedA > 0) _mediaPlayerA.Play();
                else if (!_isPlayerAActive && speedB > 0) _mediaPlayerB.Play();
            }
        }

        public void SeekActiveOperation(TimeSpan position)
        {
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            activePlayer.PlaybackSession.Position = position;
        }

        private void InitializePlayers()
        {
            _mediaPlayerA = new MediaPlayer { IsLoopingEnabled = false, AutoPlay = false };
            _mediaPlayerB = new MediaPlayer { IsLoopingEnabled = false, AutoPlay = false };

            // Each MediaPlayer auto-registers with the OS's System Media Transport Controls
            // (lock-screen/media-key "Now Playing" session) unless disabled. With multiple
            // MediaPlayer instances playing concurrently (main track + overlays), that
            // background negotiation overhead is a known cause of stutter. This app has no
            // use for OS transport-control integration, so turn it off on every player.
            _mediaPlayerA.CommandManager.IsEnabled = false;
            _mediaPlayerB.CommandManager.IsEnabled = false;

            _playerA.SetMediaPlayer(_mediaPlayerA);
            _playerB.SetMediaPlayer(_mediaPlayerB);

            _playerControl.ActiveTransform = _playerControl.TransformA;
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_playbackCts == null || _playbackCts.IsCancellationRequested)
            {
                int startIdx;
                if (_viewModel.SelectedTimelineNode != null)
                {
                    startIdx = _viewModel.TimelineNodes.IndexOf(_viewModel.SelectedTimelineNode as CinematicOperation);
                    if (startIdx < 0) startIdx = 0;
                }
                else
                {
                    // No Track 1 clip selected (e.g. an overlay is selected instead) — resume
                    // from wherever the playhead currently is instead of restarting at clip 0.
                    startIdx = _viewModel.GetTimelineIndexForStoryTime(_viewModel.CurrentStoryTime);
                }
                await StartPlaybackAsync(startIdx);
                return;
            }

            if (_isPaused)
            {
                ResumePlayback();
            }
            else
            {
                PausePlayback();
            }
        }

        private void PausePlayback()
        {
            _isPaused = true;
            _pauseStartTime = DateTime.Now;
            _viewModel.IsPlaying = false;
            
            if (_viewModel.IsRecordingMotion)
            {
                _dispatcher.TryEnqueue(() => _viewModel.IsRecordingMotion = false);
            }
            
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            activePlayer.Pause();
            if (_inTransition || _isPreparingTransition)
            {
                var otherPlayer = _isPlayerAActive ? _mediaPlayerB : _mediaPlayerA;
                otherPlayer.Pause();
            }

            // Overlay players are driven from the per-frame render loop, which stops running
            // entirely while paused — so they must be paused explicitly here rather than
            // relying on the render loop to catch up to the paused state.
            _overlayMediaPlayer1.Pause();
            _overlayMediaPlayer2.Pause();

            _dispatcher.TryEnqueue(() => UpdateWysiwygOverlay());
        }

        private void ResumePlayback()
        {
            _isPaused = false;
            var pauseDuration = DateTime.Now - _pauseStartTime;
            _opAStartTime += pauseDuration;
            _opBStartTime += pauseDuration;
            _transitionStartTime += pauseDuration;
            _viewModel.IsPlaying = true;
            
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            activePlayer.Play();
            if (_inTransition || _isPreparingTransition)
            {
                var otherPlayer = _isPlayerAActive ? _mediaPlayerB : _mediaPlayerA;
                otherPlayer.Play();
            }

            // Resume whichever overlay slots are currently occupied; EvaluateOverlays will
            // re-sync their exact position on the next render tick via drift correction.
            if (_activeOverlay1 != null && _viewModel.PlaybackSpeed > 0)
            {
                _overlayMediaPlayer1.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
                _overlayMediaPlayer1.Play();
            }
            if (_activeOverlay2 != null && _viewModel.PlaybackSpeed > 0)
            {
                _overlayMediaPlayer2.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
                _overlayMediaPlayer2.Play();
            }
        }

        public async Task StartPlaybackAsync(int startIndex = 0)
        {
            if (_viewModel.TimelineNodes.Count == 0) return;
            
            StopPlayback(); // Ensure we stop cleanly first
            var myCts = new CancellationTokenSource();
            _playbackCts = myCts;
            var token = myCts.Token;

            _viewModel.IsPlaying = true;
            _isPaused = false;
            
            // Calculate CurrentStoryTime based on startIndex
            _viewModel.CurrentStoryTime = TimeSpan.Zero;
            for(int i=0; i<startIndex; i++)
            {
                _viewModel.CurrentStoryTime += _viewModel.TimelineNodes[i].OpDuration + _viewModel.TimelineNodes[i].TransitionDuration;
            }
            _storyTimeAtClipStart = _viewModel.CurrentStoryTime;

            _isAnimating = true;
            CompositionTarget.Rendering += CompositionTarget_Rendering;

            try
            {
                await PlaybackLoopAsync(startIndex, token);
            }
            catch (OperationCanceledException)
            {
                // Playback stopped or skipped
            }
            finally
            {
                if (_playbackCts == myCts)
                {
                    bool wasCancelled = myCts.IsCancellationRequested;
                    StopPlayback(false);

                    if (!wasCancelled)
                    {
                        _dispatcher.TryEnqueue(() => 
                        {
                            _playerA.Opacity = 0;
                            _playerB.Opacity = 0;
                            
                            if (_viewModel.SelectedTimelineNode != null)
                            {
                                EnterEditMode(_viewModel.SelectedTimelineNode as CinematicOperation, _viewModel.CurrentEditTarget);
                            }
                        });
                    }
                }
            }
        }

        public void StopPlayback(bool cancelRecording = true)
        {
            _playbackCts?.Cancel();
            _isAnimating = false;
            _isPreparingTransition = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering -= RecordMotion_Rendering;
            
            _mediaPlayerA?.Pause();
            _mediaPlayerB?.Pause();
            HideAllOverlays();
            
            if (_viewModel != null)
            {
                _viewModel.IsPlaying = false;
                _isPaused = false;
                if (cancelRecording && _viewModel.IsRecordingMotion)
                {
                    _dispatcher.TryEnqueue(() => _viewModel.IsRecordingMotion = false);
                }
            }
        }

        public void SkipNext()
        {
            if (_viewModel.SelectedTimelineNode != null)
            {
                int idx = _viewModel.TimelineNodes.IndexOf(_viewModel.SelectedTimelineNode as CinematicOperation);
                if (idx < _viewModel.TimelineNodes.Count - 1)
                {
                    _ = StartPlaybackAsync(idx + 1);
                }
            }
        }

        public void SkipPrevious()
        {
            if (_viewModel.SelectedTimelineNode != null)
            {
                int idx = _viewModel.TimelineNodes.IndexOf(_viewModel.SelectedTimelineNode as CinematicOperation);
                if (idx > 0) idx--;
                _ = StartPlaybackAsync(idx);
            }
        }

        private async Task PlaybackLoopAsync(int startIndex, CancellationToken token)
        {
            bool startedByTransition = false;
            TimeSpan previousTransitionDuration = TimeSpan.Zero;
            int currentIndex = startIndex;

            while (true)
            {
                for (int i = currentIndex; i < _viewModel.TimelineNodes.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var op = _viewModel.TimelineNodes[i] as CinematicOperation;
                    
                    CurrentPlayingOperation = op;
                    _dispatcher.TryEnqueue(() => 
                    {
                        _viewModel.SelectedTimelineNode = op;
                    });

                    
                    var nextOp = i + 1 < _viewModel.TimelineNodes.Count ? _viewModel.TimelineNodes[i + 1] as CinematicOperation : null;
                    if (nextOp == null && _viewModel.IsLooping && _viewModel.TimelineNodes.Count > 0)
                    {
                        nextOp = _viewModel.TimelineNodes[0] as CinematicOperation;
                    }
                    bool hasNextTransition = nextOp != null;

                    _skipTcs = new TaskCompletionSource<bool>();
                    
                    // 1. Play the main portion of the clip
                    await PlayOperationAsync(op, nextOp, startedByTransition, hasNextTransition, previousTransitionDuration, token);
                    
                    // Advance the clip-start baseline by exactly this clip's story contribution.
                    // The render loop drives CurrentStoryTime continuously off this baseline, so
                    // accumulate into the baseline (not CurrentStoryTime) — adding to
                    // CurrentStoryTime here would double-count, since the render loop has already
                    // advanced it to this clip's end.
                    if (_skipTcs.Task.IsCompleted)
                    {
                        // Skipped!
                        startedByTransition = false;
                        _storyTimeAtClipStart += op.OpDuration + op.TransitionDuration;
                        _viewModel.CurrentStoryTime = _storyTimeAtClipStart;
                        continue;
                    }
                    _storyTimeAtClipStart += op.OpDuration;
                    _viewModel.CurrentStoryTime = _storyTimeAtClipStart;

                    // 2. Play the transition into the next clip if applicable
                    if (hasNextTransition)
                    {
                        await PlayTransitionAsync(op, nextOp, token);
                        startedByTransition = true;
                        previousTransitionDuration = op.TransitionDuration;
                        _storyTimeAtClipStart += op.TransitionDuration;
                        _viewModel.CurrentStoryTime = _storyTimeAtClipStart;
                    }
                    else
                    {
                        startedByTransition = false;
                    }
                }

                if (_viewModel.IsLooping)
                {
                    currentIndex = 0;
                    _viewModel.CurrentStoryTime = TimeSpan.Zero;
                    _storyTimeAtClipStart = TimeSpan.Zero;
                }
                else
                {
                    break;
                }
            }
        }

        private async Task PlayOperationAsync(CinematicOperation op, CinematicOperation nextOp, bool startedByTransition, bool hasNextTransition, TimeSpan previousTransitionDuration, CancellationToken token)
        {
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var activeElement = _isPlayerAActive ? _playerA : _playerB;
            var standbyPlayer = _isPlayerAActive ? _mediaPlayerB : _mediaPlayerA;
            var standbyElement = _isPlayerAActive ? _playerB : _playerA;

            if (!startedByTransition)
            {
                if (!string.IsNullOrWhiteSpace(op.FilePath))
                {
                    if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        void OnMediaOpened(MediaPlayer sender, object args) => tcs.TrySetResult(true);
                        activePlayer.MediaOpened += OnMediaOpened;

                        activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                        
                        await Task.WhenAny(tcs.Task, WaitWithPauseAsync(TimeSpan.FromSeconds(2), token));
                        activePlayer.MediaOpened -= OnMediaOpened;
                    }

                    activePlayer.PlaybackSession.Position = op.VideoStartTime;
                    
                    double combinedSpeed = _viewModel.PlaybackSpeed * op.PlaybackSpeed;
                    activePlayer.PlaybackSession.PlaybackRate = combinedSpeed;
                    activePlayer.Volume = 1.0;
                    
                    if (!_isPaused && combinedSpeed > 0) activePlayer.Play();
                    else if (combinedSpeed == 0) activePlayer.Pause();
                }

                _dispatcher.TryEnqueue(() =>
                {
                    activeElement.Opacity = 1;
                    standbyElement.Opacity = 0;
                });
            }
            
            // Preload next operation into standby player to ensure gapless playback
            if (hasNextTransition && nextOp != null && !string.IsNullOrWhiteSpace(nextOp.FilePath))
            {
                if (standbyPlayer.Source == null || !string.Equals((standbyPlayer.Source as MediaSource)?.Uri?.LocalPath, nextOp.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    standbyPlayer.Source = MediaSource.CreateFromUri(new Uri(nextOp.FilePath));
                    standbyPlayer.PlaybackSession.Position = nextOp.VideoStartTime;
                    standbyPlayer.Volume = 0.0;
                    standbyPlayer.Pause();
                }
            }
            
            var opStartTime = startedByTransition ? DateTime.Now - previousTransitionDuration : DateTime.Now;
            var totalVisibleDuration = op.OpDuration + previousTransitionDuration + (hasNextTransition ? op.TransitionDuration : TimeSpan.Zero);
            
            double globalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
            if (globalSpeed != 1.0)
            {
                totalVisibleDuration = TimeSpan.FromSeconds(totalVisibleDuration.TotalSeconds / globalSpeed);
            }
            
            if (_isPlayerAActive)
            {
                _opA = op;
                _opADuration = totalVisibleDuration;
                _opAStartTime = opStartTime;
            }
            else
            {
                _opB = op;
                _opBDuration = totalVisibleDuration;
                _opBStartTime = opStartTime;
            }
            
            _inTransition = false;

            TimeSpan elapsed = TimeSpan.Zero;
            DateTime lastTick = DateTime.Now;

            bool isVideo = !string.IsNullOrWhiteSpace(op.FilePath) && 
                (op.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || op.FilePath.EndsWith(".wmv", StringComparison.OrdinalIgnoreCase));

            while (true)
            {
                token.ThrowIfCancellationRequested();

                var now = DateTime.Now;
                if (!_isPaused)
                {
                    double currentSpeed = globalSpeed == 0 ? 1.0 : globalSpeed;
                    elapsed += TimeSpan.FromSeconds((now - lastTick).TotalSeconds * currentSpeed);
                    
                    if (isVideo)
                    {
                        if (activePlayer.PlaybackSession.Position >= op.VideoEndTime) break;
                    }
                    else
                    {
                        if (elapsed >= op.OpDuration) break;
                    }
                }
                lastTick = now;

                // Polled at ~1 frame instead of 50ms so a clip can't keep playing noticeably
                // past its nominal end before the transition machinery notices — that overshoot
                // window was a source of transient story-time inaccuracy at clip boundaries.
                await Task.Delay(15, token);
            }

            if (!hasNextTransition)
            {
                activePlayer.Pause();
                _isPlayerAActive = !_isPlayerAActive;
                _playerControl.ActiveTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;
            }
        }

        private async Task PlayTransitionAsync(CinematicOperation op, CinematicOperation nextOp, CancellationToken token)
        {
            var fadingOutPlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var fadingOutElement = _isPlayerAActive ? _playerA : _playerB;
            var fadingOutGrid = _isPlayerAActive ? _playerControl.GridA : _playerControl.GridB;
            
            _isPreparingTransition = true;
            _isPlayerAActive = !_isPlayerAActive; // Swap to next
            _playerControl.ActiveTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            // Update _opA/_opB atomically with the _isPlayerAActive flip above (not after the
            // awaits below) — otherwise there's a window where _isPlayerAActive already points
            // at the next clip's player but _opA/_opB still reference the previous clip, which
            // the per-frame story-time calc reads and briefly computes a garbage value from.
            if (nextOp != null)
            {
                var nextTotalDuration = nextOp.OpDuration + op.TransitionDuration;

                double opGlobalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
                if (opGlobalSpeed != 1.0)
                {
                    nextTotalDuration = TimeSpan.FromSeconds(nextTotalDuration.TotalSeconds / opGlobalSpeed);
                }

                if (!_isPlayerAActive)
                {
                    _opB = nextOp;
                    _opBDuration = nextTotalDuration;
                    _opBStartTime = DateTime.Now;
                }
                else
                {
                    _opA = nextOp;
                    _opADuration = nextTotalDuration;
                    _opAStartTime = DateTime.Now;
                }
            }

            var fadingInPlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var fadingInElement = _isPlayerAActive ? _playerA : _playerB;
            var fadingInGrid = _isPlayerAActive ? _playerControl.GridA : _playerControl.GridB;

            if (nextOp != null && !string.IsNullOrWhiteSpace(nextOp.FilePath))
            {
                if (fadingInPlayer.Source == null || !string.Equals((fadingInPlayer.Source as MediaSource)?.Uri?.LocalPath, nextOp.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var tcs = new TaskCompletionSource<bool>();
                    Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                    fadingInPlayer.MediaOpened += handler;
                    
                    fadingInPlayer.Source = MediaSource.CreateFromUri(new Uri(nextOp.FilePath));
                    
                    await Task.WhenAny(tcs.Task, Task.Delay(1500));
                    fadingInPlayer.MediaOpened -= handler;
                }

                fadingInPlayer.PlaybackSession.Position = nextOp.VideoStartTime;
                
                double combinedNextSpeed = _viewModel.PlaybackSpeed * nextOp.PlaybackSpeed;
                fadingInPlayer.PlaybackSession.PlaybackRate = combinedNextSpeed;
                fadingInPlayer.Volume = 0.0;
                
                if (!_isPaused && combinedNextSpeed > 0) fadingInPlayer.Play();
                else if (combinedNextSpeed == 0) fadingInPlayer.Pause();
            }
            
            if (op.TransitionDuration <= TimeSpan.Zero || op.TransitionStyle == TransitionStyle.HardSnap)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    Canvas.SetZIndex(fadingInGrid, 1);
                    Canvas.SetZIndex(fadingOutGrid, 0);
                    fadingInElement.Opacity = 1.0;
                });
                
                fadingInPlayer.Volume = 1.0;
                
                _ = Task.Delay(150).ContinueWith(_ =>
                {
                    _dispatcher.TryEnqueue(() => fadingOutElement.Opacity = 0.0);
                    fadingOutPlayer.Pause();
                });
                _isPreparingTransition = false;
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                Canvas.SetZIndex(fadingOutGrid, 1); // Current clip on top for Transition Out
                Canvas.SetZIndex(fadingInGrid, 0);  // Next clip underneath
            });

            _fadeOutElement = fadingOutElement;
            _fadeInElement = fadingInElement;
            _isPreparingTransition = false;
            _inTransition = true;
            _transitionStyle = op.TransitionStyle;
            _renderDuration = op.TransitionDuration;
            _transitionStartTime = DateTime.Now;

            double activeGlobalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
            TimeSpan realTransitionDuration = op.TransitionDuration;
            if (activeGlobalSpeed != 1.0)
            {
                realTransitionDuration = TimeSpan.FromSeconds(realTransitionDuration.TotalSeconds / activeGlobalSpeed);
            }

            await WaitWithPauseAsync(realTransitionDuration, token);

            fadingOutPlayer.Pause();
            fadingOutPlayer.Volume = 1.0;
            
            _dispatcher.TryEnqueue(() =>
            {
                fadingInElement.Opacity = 1.0;
                fadingInPlayer.Volume = 1.0;
                fadingOutElement.Opacity = 0.0;
            });
            _inTransition = false;
        }

        private async Task WaitWithPauseAsync(TimeSpan duration, CancellationToken token)
        {
            var targetTime = DateTime.Now + duration;
            while (DateTime.Now < targetTime)
            {
                token.ThrowIfCancellationRequested();
                if (_isPaused)
                {
                    var pauseStart = DateTime.Now;
                    await Task.Delay(50, token);
                    targetTime += (DateTime.Now - pauseStart);
                    continue;
                }
                
                var remaining = targetTime - DateTime.Now;
                if (remaining <= TimeSpan.Zero) break;
                
                var delay = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
                await Task.Delay(delay, token);
            }
        }

        private TransitionStyle _transitionStyle;

        private void CompositionTarget_Rendering(object sender, object e)
        {
            if (!_isAnimating || _isPaused) return;
            
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            if (activePlayer != null && activePlayer.PlaybackSession != null)
            {
                _viewModel.CurrentOperationTime = activePlayer.PlaybackSession.Position;
                _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
            }

            if (_inTransition)
            {
                var transElapsed = DateTime.Now - _transitionStartTime;
                var transProgress = _renderDuration.TotalMilliseconds > 0 
                    ? Math.Clamp(transElapsed.TotalMilliseconds / _renderDuration.TotalMilliseconds, 0, 1) 
                    : 1;

                if (_transitionStyle == TransitionStyle.DipToColor)
                {
                    if (transProgress < 0.5)
                    {
                        _fadeOutElement.Opacity = 1.0 - (transProgress * 2.0);
                        _fadeInElement.Opacity = 0.0;
                    }
                    else
                    {
                        _fadeOutElement.Opacity = 0.0;
                        _fadeInElement.Opacity = (transProgress - 0.5) * 2.0;
                    }
                }
                else if (_transitionStyle == TransitionStyle.CinematicBridge)
                {
                    double smoothProgress = transProgress * transProgress * (3.0 - 2.0 * transProgress);
                    _fadeOutElement.Opacity = 1.0 - smoothProgress;
                    _fadeInElement.Opacity = 1.0; // Keep bottom opaque
                }
                else
                {
                    _fadeOutElement.Opacity = 1.0 - transProgress; // Fade out top clip
                    _fadeInElement.Opacity = 1.0; // Keep bottom clip fully opaque
                }

                // Audio Crossfade
                var fadingOutPlayer = _fadeOutElement == _playerA ? _mediaPlayerA : _mediaPlayerB;
                var fadingInPlayer = _fadeInElement == _playerA ? _mediaPlayerA : _mediaPlayerB;
                
                fadingOutPlayer.Volume = 1.0 - transProgress;
                fadingInPlayer.Volume = transProgress;
            }

            void UpdateSpatial(CinematicOperation op, DateTime startTime, TimeSpan duration, Microsoft.UI.Xaml.Media.CompositeTransform transform)
            {
                if (op == null || transform == null) return;
                var spatialElapsed = DateTime.Now - startTime;
                var spatialProgress = duration.TotalMilliseconds > 0 
                    ? Math.Clamp(spatialElapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1) 
                    : 1;

                double easedProgress = spatialProgress;
                if (op.CurveProfile == CurveProfile.Bezier)
                    easedProgress = spatialProgress < 0.5 ? 2 * spatialProgress * spatialProgress : 1 - Math.Pow(-2 * spatialProgress + 2, 2) / 2;
                else if (op.CurveProfile == CurveProfile.DirectorsArc)
                    easedProgress = 1 - Math.Pow(1 - spatialProgress, 3);

                if (op.MidMark != null)
                {
                    if (easedProgress < 0.5)
                    {
                        double p = easedProgress * 2;
                        transform.ScaleX = op.StartMark.Scale + (op.MidMark.Scale - op.StartMark.Scale) * p;
                        transform.TranslateX = op.StartMark.X + (op.MidMark.X - op.StartMark.X) * p;
                        transform.TranslateY = op.StartMark.Y + (op.MidMark.Y - op.StartMark.Y) * p;
                    }
                    else
                    {
                        double p = (easedProgress - 0.5) * 2;
                        transform.ScaleX = op.MidMark.Scale + (op.EndMark.Scale - op.MidMark.Scale) * p;
                        transform.TranslateX = op.MidMark.X + (op.EndMark.X - op.MidMark.X) * p;
                        transform.TranslateY = op.MidMark.Y + (op.EndMark.Y - op.MidMark.Y) * p;
                    }
                    transform.ScaleY = transform.ScaleX;
                }
                else
                {
                    transform.ScaleX = op.StartMark.Scale + (op.EndMark.Scale - op.StartMark.Scale) * easedProgress;
                    transform.ScaleY = transform.ScaleX;
                    transform.TranslateX = op.StartMark.X + (op.EndMark.X - op.StartMark.X) * easedProgress;
                    transform.TranslateY = op.StartMark.Y + (op.EndMark.Y - op.StartMark.Y) * easedProgress;
                }
            }

            UpdateSpatial(_opA, _opAStartTime, _opADuration, _playerControl.TransformA);
            UpdateSpatial(_opB, _opBStartTime, _opBDuration, _playerControl.TransformB);

            // CurrentStoryTime only gets bumped at clip boundaries elsewhere in this class —
            // it does not tick on its own. Advance it continuously here from the active
            // player's real decode position, or overlay drift-correction (which compares
            // against this value every frame) sees a stale target and fights the overlay's
            // real playback, which is what caused the overlay stutter.
            var storyTimePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var storyTimeOp = _isPlayerAActive ? _opA : _opB;
            if (storyTimeOp != null && storyTimePlayer?.PlaybackSession != null)
            {
                // Video position advances at the clip's own playback rate, but story time is
                // measured in real sequence seconds — a clip at 2x contributes half as much
                // story time as video watched. Divide by the clip speed so the per-frame
                // advance maxes out at exactly OpDuration, matching the boundary accounting.
                double clipSpeed = storyTimeOp.PlaybackSpeed > 0 ? storyTimeOp.PlaybackSpeed : 1.0;
                double videoElapsed = (storyTimePlayer.PlaybackSession.Position - storyTimeOp.VideoStartTime).TotalSeconds;
                if (videoElapsed < 0) videoElapsed = 0;
                _viewModel.CurrentStoryTime = _storyTimeAtClipStart + TimeSpan.FromSeconds(videoElapsed / clipSpeed);
            }

            // Evaluate overlay clips against master story time
            EvaluateOverlays(_viewModel.CurrentStoryTime);

            var activeOp = _isPlayerAActive ? _opA : _opB;
            
            UpdateTimelineNodesIsPlayingState(activeOp);
        }

        private void UpdateTimelineNodesIsPlayingState(CinematicOperation activeOp)
        {
            if (_viewModel.TimelineNodes != null)
            {
                foreach (var node in _viewModel.TimelineNodes)
                {
                    bool isPlaying = (node == activeOp);
                    if (node.IsPlaying != isPlaying)
                    {
                        node.IsPlaying = isPlaying;
                    }
                }
            }

            // The telemetry HUD is diagnostic text a human can't usefully read at 60fps —
            // throttle it to ~10/sec instead of recomputing/re-laying-out text every frame,
            // which otherwise competes with video decode for CPU during overlay playback.
            if ((DateTime.Now - _lastTelemetryUpdate).TotalMilliseconds >= 100)
            {
                _lastTelemetryUpdate = DateTime.Now;
                UpdateTelemetryOverlay();
            }
        }

        private DateTime _lastTelemetryUpdate = DateTime.MinValue;

        private void UpdateTelemetryOverlay(bool isEditMode = false)
        {
            if (_viewModel.IsTelemetryVisible)
            {
                var activeTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;
                
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                var currentActivePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
                var activeOp = _isPlayerAActive ? _opA : _opB;
                if (isEditMode)
                {
                    activeOp = _viewModel.SelectedTimelineNode as CinematicOperation;
                }

                string currentFileName = activeOp != null ? System.IO.Path.GetFileName(activeOp.FilePath) : "Transition";
                
                var currentStoryTime = _viewModel.CurrentStoryTime;
                var clipEndTime = activeOp != null ? (activeOp.VideoStartTime + activeOp.OpDuration) : TimeSpan.Zero;
                _playerControl.TelemetryStoryTime.Text = $"Story Time: {currentStoryTime:hh\\:mm\\:ss\\.ff} / {_viewModel.TotalStoryTime:hh\\:mm\\:ss\\.ff}";
                
                if (currentActivePlayer?.PlaybackSession != null)
                {
                    _playerControl.TelemetryClipTime.Text = $"Clip Pos  : {currentActivePlayer.PlaybackSession.Position:hh\\:mm\\:ss\\.ff} / {clipEndTime:hh\\:mm\\:ss\\.ff} [{currentFileName}]";
                }
                
                if (activeTransform != null) {
                    _playerControl.TelemetryOperationInfo.Text = $"Transform : Z:{activeTransform.ScaleX:F2} X:{activeTransform.TranslateX:F0} Y:{activeTransform.TranslateY:F0}";
                }
                
                if (activeOp != null && activeOp.StartMark != null && activeOp.EndMark != null && _playerControl.ActualWidth > 0) {
                    double W = _playerControl.ActualWidth;
                    double H = _playerControl.ActualHeight;
                    
                    double Sc = activeTransform != null ? activeTransform.ScaleX : 1.0;
                    double txc = activeTransform != null ? activeTransform.TranslateX : 0.0;
                    double tyc = activeTransform != null ? activeTransform.TranslateY : 0.0;

                    // Start Mark Box
                    double St_s = activeOp.StartMark.Scale;
                    double txt_s = activeOp.StartMark.X;
                    double tyt_s = activeOp.StartMark.Y;
                    double startLeft = (-W / 2 - txt_s) * (Sc / St_s) + W / 2 + txc;
                    double startTop = (-H / 2 - tyt_s) * (Sc / St_s) + H / 2 + tyc;
                    double startWidth = W * (Sc / St_s);
                    double startHeight = H * (Sc / St_s);

                    // End Mark Box
                    double St_e = activeOp.EndMark.Scale;
                    double txt_e = activeOp.EndMark.X;
                    double tyt_e = activeOp.EndMark.Y;
                    double endLeft = (-W / 2 - txt_e) * (Sc / St_e) + W / 2 + txc;
                    double endTop = (-H / 2 - tyt_e) * (Sc / St_e) + H / 2 + tyc;
                    double endWidth = W * (Sc / St_e);
                    double endHeight = H * (Sc / St_e);

                    _playerControl.TelemetryStartMarkInfo.Text = $"StartBox : L:{startLeft:F0} T:{startTop:F0} W:{startWidth:F0} H:{startHeight:F0} (Z:{activeOp.StartMark.Scale:F2})";
                    
                    if (activeOp.MidMark != null) {
                        double St_m = activeOp.MidMark.Scale;
                        double txt_m = activeOp.MidMark.X;
                        double tyt_m = activeOp.MidMark.Y;
                        double midLeft = (-W / 2 - txt_m) * (Sc / St_m) + W / 2 + txc;
                        double midTop = (-H / 2 - tyt_m) * (Sc / St_m) + H / 2 + tyc;
                        double midWidth = W * (Sc / St_m);
                        double midHeight = H * (Sc / St_m);
                        _playerControl.TelemetryMidMarkInfo.Text   = $"MidBox   : L:{midLeft:F0} T:{midTop:F0} W:{midWidth:F0} H:{midHeight:F0} (Z:{activeOp.MidMark.Scale:F2})";
                        _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    } else {
                        _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    }

                    _playerControl.TelemetryEndMarkInfo.Text   = $"EndBox   : L:{endLeft:F0} T:{endTop:F0} W:{endWidth:F0} H:{endHeight:F0} (Z:{activeOp.EndMark.Scale:F2})";
                    _playerControl.TelemetryStartMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    _playerControl.TelemetryEndMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else {
                    _playerControl.TelemetryStartMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _playerControl.TelemetryMidMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    _playerControl.TelemetryEndMarkInfo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
            }
            else
            {
                _playerControl.TelemetryOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        public void UpdateWysiwygOverlay()
        {
            if (_viewModel.IsPlaying || _viewModel.SelectedTimelineNode == null)
            {
                _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            var op = _viewModel.SelectedTimelineNode as CinematicOperation;
            var transform = _playerControl.ActiveTransform;
            if (op == null || transform == null) return;

            _playerControl.WysiwygCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            UpdateTelemetryOverlay(true);

            double W = _playerControl.ActualWidth > 0 ? _playerControl.ActualWidth : 1920;
            double H = _playerControl.ActualHeight > 0 ? _playerControl.ActualHeight : 1080;

            void DrawRect(Microsoft.UI.Xaml.Shapes.Rectangle rect, SpatialMark targetMark, bool show)
            {
                if (!show || targetMark == null)
                {
                    rect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    return;
                }

                rect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

                double Sc = transform.ScaleX;
                double txc = transform.TranslateX;
                double tyc = transform.TranslateY;

                double St = targetMark.Scale;
                double txt = targetMark.X;
                double tyt = targetMark.Y;

                if (St <= 0) St = 1;

                double currentLeft = (-W / 2 - txt) * (Sc / St) + W / 2 + txc;
                double currentTop = (-H / 2 - tyt) * (Sc / St) + H / 2 + tyc;
                double currentWidth = W * (Sc / St);
                double currentHeight = H * (Sc / St);

                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, currentLeft);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, currentTop);
                rect.Width = Math.Max(0, currentWidth);
                rect.Height = Math.Max(0, currentHeight);
            }

            DrawRect(_playerControl.WysiwygStartRect, op.StartMark, true);
            DrawRect(_playerControl.WysiwygMidRect, op.MidMark, true);
            DrawRect(_playerControl.WysiwygEndRect, op.EndMark, true);

            // Draw Full Frame representation (Scale=1, Tx=0, Ty=0)
            DrawRect(_playerControl.WysiwygFullFrameRect, new SpatialMark(1f, 0f, 0f), true);
        }

        public async void EnterEditMode(CinematicOperation op, EditTarget target)
        {
            StopPlayback();
            UpdateTimelineNodesIsPlayingState(op);
            
            if (string.IsNullOrWhiteSpace(op.FilePath)) return;

            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var activeElement = _isPlayerAActive ? _playerA : _playerB;
            var standbyElement = _isPlayerAActive ? _playerB : _playerA;
            var activeTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                activePlayer.MediaOpened += handler;
                
                activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                activePlayer.MediaOpened -= handler;
            }
            
            _dispatcher.TryEnqueue(() =>
            {
                activeElement.Opacity = 1;
                standbyElement.Opacity = 0;
            });
            
            activePlayer.Pause();
            
            TimeSpan globalStartTime = TimeSpan.Zero;
            foreach (var node in _viewModel.TimelineNodes)
            {
                if (node == op) break;
                if (node is CinematicOperation prevOp)
                {
                    globalStartTime += prevOp.OpDuration + prevOp.TransitionDuration;
                }
            }
            
            _dispatcher.TryEnqueue(() =>
            {
                _viewModel.CurrentStoryTime = globalStartTime;
            });

            SpatialMark markToEdit;
            if (target == EditTarget.Start)
            {
                activePlayer.PlaybackSession.Position = op.VideoStartTime;
                markToEdit = op.StartMark;
            }
            else if (target == EditTarget.Mid && op.MidMark != null)
            {
                activePlayer.PlaybackSession.Position = op.VideoStartTime + TimeSpan.FromSeconds(op.OpDuration.TotalSeconds / 2);
                markToEdit = op.MidMark;
            }
            else
            {
                activePlayer.PlaybackSession.Position = op.VideoStartTime + op.OpDuration;
                markToEdit = op.EndMark;
            }
            
            _dispatcher.TryEnqueue(() =>
            {
                if (activePlayer.PlaybackSession != null)
                {
                    _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
                    _viewModel.CurrentOperationTime = activePlayer.PlaybackSession.Position;
                }
                activeTransform.ScaleX = markToEdit.Scale;
                activeTransform.ScaleY = markToEdit.Scale;
                activeTransform.TranslateX = markToEdit.X;
                activeTransform.TranslateY = markToEdit.Y;
                _playerControl.ActiveTransform = activeTransform;
                UpdateWysiwygOverlay();
            });
        }

        private DateTime _recordStartTime;

        public async void StartRecordingMotion(CinematicOperation op)
        {
            if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) return;
            
            _playbackCts?.Cancel();
            _playbackCts = null;
            _isAnimating = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering -= RecordMotion_Rendering;
            
            _mediaPlayerB?.Pause();
            
            op.RecordedPath.Clear();
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            var activeElement = _isPlayerAActive ? _playerA : _playerB;
            var activeTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            if (activePlayer.Source == null || !string.Equals((activePlayer.Source as MediaSource)?.Uri?.LocalPath, op.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                activePlayer.MediaOpened += handler;
                activePlayer.Source = MediaSource.CreateFromUri(new Uri(op.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                activePlayer.MediaOpened -= handler;
            }

            _dispatcher.TryEnqueue(() =>
            {
                var standbyElement = _isPlayerAActive ? _playerB : _playerA;
                activeElement.Opacity = 1;
                standbyElement.Opacity = 0;
                _playerControl.ActiveTransform = activeTransform;
            });

            activePlayer.PlaybackSession.Position = op.VideoStartTime;
            activePlayer.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
            if (_viewModel.PlaybackSpeed == 0.0)
            {
                activePlayer.Pause();
            }
            else
            {
                activePlayer.Play();
                _dispatcher.TryEnqueue(() => _viewModel.IsPlaying = true);
            }
            
            _isAnimating = true;
            _opA = op;
            _recordStartTime = DateTime.Now;
            
            // CRITICAL: Initialize ActiveTransform so input handlers and the recording loop can function
            _playerControl.ActiveTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;

            CompositionTarget.Rendering += RecordMotion_Rendering;
        }

        public void StopRecordingMotion(CinematicOperation op)
        {
            if (op == null) return;
            _isAnimating = false;
            CompositionTarget.Rendering -= RecordMotion_Rendering;
            
            DistillRecordedPath(op);

            EnterEditMode(op, EditTarget.Start);
        }

        private void RecordMotion_Rendering(object sender, object e)
        {
            if (_opA == null || _playerControl.ActiveTransform == null) return;
            
            // CRITICAL: Use the correct active player, not a hardcoded _mediaPlayerA
            var activePlayer = _isPlayerAActive ? _mediaPlayerA : _mediaPlayerB;
            
            var activeTransform = _playerControl.ActiveTransform;
            var mark = new SpatialMark((float)activeTransform.ScaleX, (float)activeTransform.TranslateX, (float)activeTransform.TranslateY);
            
            var realTimeElapsed = DateTime.Now - _recordStartTime;
            var speed = _viewModel.PlaybackSpeed;
            if (speed == 0) speed = 1.0; // Prevent freeze if playback speed is 0
            
            var time = TimeSpan.FromSeconds(realTimeElapsed.TotalSeconds * speed);
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            _opA.RecordedPath.Add(new TransformKeyframe(time, mark));
            
            _viewModel.CurrentOperationTime = _opA.VideoStartTime + time;
            if (activePlayer.PlaybackSession != null)
            {
                activePlayer.PlaybackSession.Position = _viewModel.CurrentOperationTime;
                _viewModel.CurrentOperationDuration = activePlayer.PlaybackSession.NaturalDuration;
            }

            // Update UI
            _dispatcher.TryEnqueue(() => 
            {
                UpdateTelemetryOverlay(false);
                UpdateWysiwygOverlay();
            });

            // Automatically stop recording when we reach the end of the operation's duration
            if (time >= _opA.OpDuration)
            {
                _dispatcher.TryEnqueue(() => 
                {
                    if (_viewModel.IsRecordingMotion)
                    {
                        _viewModel.IsRecordingMotion = false;
                    }
                });
            }
        }

        private void DistillRecordedPath(CinematicOperation op)
        {
            if (op.RecordedPath.Count == 0)
            {
                _dispatcher.TryEnqueue(() => { _playerControl.TelemetryOperationInfo.Text = "Distill: RecordedPath is empty!"; });
                return;
            }
            // Distillation Algorithm (Step 2)
            // Convert raw gesture capture into smooth start/end cinematic keyframes
            var first = op.RecordedPath.First();
            var last = op.RecordedPath.Last();
            var mid = op.RecordedPath[op.RecordedPath.Count / 2];
            
            _dispatcher.TryEnqueue(() => { _playerControl.TelemetryOperationInfo.Text = $"Distill: frames={op.RecordedPath.Count}, firstS={first.Transform.Scale:F2}, lastS={last.Transform.Scale:F2}"; });

            op.StartMark = new SpatialMark(first.Transform.Scale, first.Transform.X, first.Transform.Y);
            op.MidMark = new SpatialMark(mid.Transform.Scale, mid.Transform.X, mid.Transform.Y);
            op.EndMark = new SpatialMark(last.Transform.Scale, last.Transform.X, last.Transform.Y);
            op.CurveProfile = CurveProfile.DirectorsArc; // Automatic smoothing curve

            UpdateWysiwygOverlay();
        }

        // ==================== Overlay Playback ====================

        private void InitializeOverlayPlayers()
        {
            _overlayMediaPlayer1 = new MediaPlayer();
            _overlayMediaPlayer1.IsLoopingEnabled = false;
            _overlayMediaPlayer1.AutoPlay = false;
            _overlayMediaPlayer1.CommandManager.IsEnabled = false;
            _playerControl.OverlayPlayer1.SetMediaPlayer(_overlayMediaPlayer1);

            _overlayMediaPlayer2 = new MediaPlayer();
            _overlayMediaPlayer2.IsLoopingEnabled = false;
            _overlayMediaPlayer2.AutoPlay = false;
            _overlayMediaPlayer2.CommandManager.IsEnabled = false;
            _playerControl.OverlayPlayer2.SetMediaPlayer(_overlayMediaPlayer2);

            // Upper-track audio is muted by default — Track 1 is the audio bed. Per-clip
            // unmute is a later addition. Without this, a close-up overlaid on the same scene
            // as Track 1 doubles/echoes the audio.
            _overlayMediaPlayer1.IsMuted = true;
            _overlayMediaPlayer2.IsMuted = true;
        }

        private void EvaluateOverlays(TimeSpan currentStoryTime)
        {
            var overlays = _viewModel.OverlayClips;
            if (overlays.Count == 0)
            {
                // Fast path: no overlays configured, ensure both slots are hidden
                if (_activeOverlay1 != null) ReleaseOverlaySlot(1);
                if (_activeOverlay2 != null) ReleaseOverlaySlot(2);
                return;
            }

            // Determine which overlays should be active right now (max 2). Ordered by
            // collection position; per-clip z-order is superseded by track-level z-order
            // in the multi-track model.
            CinematicOperation desired1 = null;
            CinematicOperation desired2 = null;

            foreach (var overlay in overlays)
            {
                if (overlay.IsActiveAt(currentStoryTime))
                {
                    if (desired1 == null) desired1 = overlay;
                    else if (desired2 == null) { desired2 = overlay; break; }
                }
            }

            // Slot 1
            if (_activeOverlay1 != desired1)
            {
                if (desired1 != null)
                    ActivateOverlaySlot(1, desired1, currentStoryTime);
                else
                    ReleaseOverlaySlot(1);
            }
            else if (_activeOverlay1 != null)
            {
                // Drift correction: re-seek if overlay player drifts > 200ms
                ApplyOverlayDriftCorrection(1, _activeOverlay1, currentStoryTime);
            }

            // Slot 2
            if (_activeOverlay2 != desired2)
            {
                if (desired2 != null)
                    ActivateOverlaySlot(2, desired2, currentStoryTime);
                else
                    ReleaseOverlaySlot(2);
            }
            else if (_activeOverlay2 != null)
            {
                ApplyOverlayDriftCorrection(2, _activeOverlay2, currentStoryTime);
            }

            // Apply transforms for active overlays
            if (_activeOverlay1 != null) ApplyOverlayTransform(1, _activeOverlay1);
            if (_activeOverlay2 != null) ApplyOverlayTransform(2, _activeOverlay2);
        }

        private void ActivateOverlaySlot(int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            var player = slot == 1 ? _overlayMediaPlayer1 : _overlayMediaPlayer2;
            var grid = slot == 1 ? _playerControl.OverlayGrid1 : _playerControl.OverlayGrid2;

            // Mark active immediately so repeated per-frame EvaluateOverlays ticks don't
            // re-trigger this while the media is still opening asynchronously.
            if (slot == 1) _activeOverlay1 = overlay;
            else _activeOverlay2 = overlay;

            grid.Opacity = overlay.Opacity;

            bool needsNewSource = player.Source == null ||
                !string.Equals((player.Source as MediaSource)?.Uri?.LocalPath, overlay.FilePath, StringComparison.OrdinalIgnoreCase);

            if (needsNewSource)
            {
                // MediaPlayer.PlaybackSession isn't seekable until MediaOpened fires — seeking
                // (or even touching PlaybackSession) before then throws. Defer the seek/play
                // until the media actually finishes opening instead of doing it synchronously.
                void OnOpened(MediaPlayer sender, object args)
                {
                    sender.MediaOpened -= OnOpened;

                    // The overlay this slot wants may have changed while we were waiting
                    // (e.g. playback moved past it, or it got released) — bail if so.
                    var currentSlotOverlay = slot == 1 ? _activeOverlay1 : _activeOverlay2;
                    if (currentSlotOverlay != overlay) return;

                    SeekAndPlayOverlay(sender, overlay, _viewModel.CurrentStoryTime);
                    _dispatcher.TryEnqueue(() => SizeOverlayToVideo(slot, sender));
                }

                player.MediaOpened += OnOpened;
                player.Source = MediaSource.CreateFromUri(new Uri(overlay.FilePath));
            }
            else
            {
                // Source is already correct and open (e.g. re-entering this slot for the same
                // clip) — safe to seek immediately.
                SeekAndPlayOverlay(player, overlay, currentStoryTime);
                SizeOverlayToVideo(slot, player);
            }
        }

        // Sizes the overlay grid to the video's native aspect ratio so the video fills it with
        // no black letterbox bars. Scale=1.0 == the video fit (contained) within the viewport;
        // the clip's Scale transform then shrinks/enlarges from there. Without this the grid is
        // full-viewport and a non-16:9 video gets pillar/letterboxed inside it.
        private void SizeOverlayToVideo(int slot, MediaPlayer player)
        {
            var grid = slot == 1 ? _playerControl.OverlayGrid1 : _playerControl.OverlayGrid2;
            if (player?.PlaybackSession == null) return;

            uint vw = player.PlaybackSession.NaturalVideoWidth;
            uint vh = player.PlaybackSession.NaturalVideoHeight;
            double vpW = _playerControl.ActualWidth;
            double vpH = _playerControl.ActualHeight;
            if (vw == 0 || vh == 0 || vpW <= 0 || vpH <= 0) return;

            double aspect = (double)vw / vh;
            double baseW, baseH;
            if (aspect >= vpW / vpH) { baseW = vpW; baseH = vpW / aspect; }
            else { baseH = vpH; baseW = vpH * aspect; }

            grid.Width = baseW;
            grid.Height = baseH;
        }

        private void SeekAndPlayOverlay(MediaPlayer player, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            if (player.PlaybackSession == null) return;

            TimeSpan offsetIntoOverlay = currentStoryTime - overlay.StartTime;
            TimeSpan targetPosition = overlay.VideoStartTime + offsetIntoOverlay;

            // The overlay's on-screen Duration is independent of the source clip's actual
            // length — if Duration outlasts the media, hold on the last frame instead of
            // seeking past end-of-media (which the player can't reach).
            bool pastEnd = TryClampToMediaLength(player, ref targetPosition);

            player.PlaybackSession.Position = targetPosition;

            if (pastEnd)
            {
                player.Pause();
            }
            else if (_isAnimating && !_isPaused && _viewModel.PlaybackSpeed > 0)
            {
                player.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
                player.Play();
            }
            else
            {
                player.Pause();
            }
        }

        // Clamps a target seek position to the media's actual playable length. Returns true
        // if the target was past end-of-media (i.e. the caller should hold, not keep seeking).
        private bool TryClampToMediaLength(MediaPlayer player, ref TimeSpan targetPosition)
        {
            var natural = player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
            if (natural <= TimeSpan.Zero || targetPosition < natural) return false;

            var holdPosition = natural - TimeSpan.FromMilliseconds(50);
            targetPosition = holdPosition > TimeSpan.Zero ? holdPosition : TimeSpan.Zero;
            return true;
        }

        private void ReleaseOverlaySlot(int slot)
        {
            var player = slot == 1 ? _overlayMediaPlayer1 : _overlayMediaPlayer2;
            var grid = slot == 1 ? _playerControl.OverlayGrid1 : _playerControl.OverlayGrid2;

            player.Pause();
            player.Source = null; // Release GPU decode pipeline
            grid.Opacity = 0;

            // Reset transform
            var transform = slot == 1 ? _playerControl.OverlayTransform1 : _playerControl.OverlayTransform2;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            transform.TranslateX = 0;
            transform.TranslateY = 0;

            if (slot == 1) _activeOverlay1 = null;
            else _activeOverlay2 = null;
        }

        private void ApplyOverlayTransform(int slot, CinematicOperation overlay)
        {
            // Static placement for now: the upper-track clip's StartMark holds its
            // scale/position (StartMark == EndMark = no motion). Mark interpolation over the
            // clip's duration comes in the content/placement phase.
            var transform = slot == 1 ? _playerControl.OverlayTransform1 : _playerControl.OverlayTransform2;
            transform.ScaleX = overlay.StartMark.Scale;
            transform.ScaleY = overlay.StartMark.Scale;
            transform.TranslateX = overlay.StartMark.X;
            transform.TranslateY = overlay.StartMark.Y;
        }

        private void ApplyOverlayDriftCorrection(int slot, CinematicOperation overlay, TimeSpan currentStoryTime)
        {
            var player = slot == 1 ? _overlayMediaPlayer1 : _overlayMediaPlayer2;
            if (player.PlaybackSession == null) return;

            TimeSpan expectedPosition = overlay.VideoStartTime + (currentStoryTime - overlay.StartTime);

            if (TryClampToMediaLength(player, ref expectedPosition))
            {
                // Past end-of-media — hold the last frame instead of chasing an unreachable
                // position every frame (this was the cause of visible stutter).
                if (player.PlaybackSession.Position < expectedPosition)
                {
                    player.PlaybackSession.Position = expectedPosition;
                }
                player.Pause();
                return;
            }

            TimeSpan actualPosition = player.PlaybackSession.Position;
            TimeSpan drift = (expectedPosition - actualPosition).Duration();

            if (drift > TimeSpan.FromMilliseconds(200))
            {
                player.PlaybackSession.Position = expectedPosition;
            }

            // We're back in-bounds (not past end-of-media) — make sure the player is actually
            // playing. Without this, a transient overshoot that triggered the past-end-of-media
            // Pause() above on some earlier frame would leave the overlay frozen forever, since
            // nothing else in this correction path ever resumes it.
            if (_isAnimating && !_isPaused && _viewModel.PlaybackSpeed > 0)
            {
                if (player.PlaybackSession.PlaybackRate != _viewModel.PlaybackSpeed)
                {
                    player.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
                }
                if (player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing)
                {
                    player.Play();
                }
            }
        }

        private void HideAllOverlays()
        {
            if (_activeOverlay1 != null) ReleaseOverlaySlot(1);
            if (_activeOverlay2 != null) ReleaseOverlaySlot(2);
        }

        // ==================== Overlay Editing (WYSIWYG) ====================

        // Overlay editing always borrows slot 1. Playback is stopped first, so slot 1
        // can't be in use for actual overlay playback at the same time.
        public async void EnterOverlayEditMode(CinematicOperation overlay)
        {
            StopPlayback();
            UpdateWysiwygOverlay(); // Collapse any stale main-track rectangles

            if (overlay == null || string.IsNullOrWhiteSpace(overlay.FilePath)) return;

            _isEditingOverlay = true;
            _activeOverlay1 = overlay;

            var player = _overlayMediaPlayer1;
            var grid = _playerControl.OverlayGrid1;
            var transform = _playerControl.OverlayTransform1;

            if (player.Source == null || !string.Equals((player.Source as MediaSource)?.Uri?.LocalPath, overlay.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var tcs = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<MediaPlayer, object> handler = (s, e) => tcs.TrySetResult(true);
                player.MediaOpened += handler;
                player.Source = MediaSource.CreateFromUri(new Uri(overlay.FilePath));
                await Task.WhenAny(tcs.Task, Task.Delay(1500));
                player.MediaOpened -= handler;
            }

            if (player.PlaybackSession != null)
            {
                player.PlaybackSession.Position = overlay.VideoStartTime;
            }
            player.Pause();

            _dispatcher.TryEnqueue(() =>
            {
                transform.ScaleX = overlay.StartMark.Scale;
                transform.ScaleY = overlay.StartMark.Scale;
                transform.TranslateX = overlay.StartMark.X;
                transform.TranslateY = overlay.StartMark.Y;
                SizeOverlayToVideo(1, player); // Match the overlay grid to the video aspect (no black bars)
                grid.Opacity = 1.0; // Full opacity while editing regardless of the clip's playback opacity
                _playerControl.ActiveTransform = transform;
            });
        }

        public void ClearOverlayEditMode()
        {
            if (!_isEditingOverlay) return;
            _isEditingOverlay = false;
            ReleaseOverlaySlot(1);
            _playerControl.ActiveTransform = _isPlayerAActive ? _playerControl.TransformA : _playerControl.TransformB;
        }
    }
}

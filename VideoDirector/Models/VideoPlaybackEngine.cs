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
        private OverlayClip _activeOverlay1;
        private OverlayClip _activeOverlay2;

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

            _playerA.SetMediaPlayer(_mediaPlayerA);
            _playerB.SetMediaPlayer(_mediaPlayerB);
            
            _playerControl.ActiveTransform = _playerControl.TransformA;
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_playbackCts == null || _playbackCts.IsCancellationRequested)
            {
                int startIdx = 0;
                if (_viewModel.SelectedTimelineNode != null)
                {
                    startIdx = _viewModel.TimelineNodes.IndexOf(_viewModel.SelectedTimelineNode as CinematicOperation);
                    if (startIdx < 0) startIdx = 0;
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
                    
                    if (_skipTcs.Task.IsCompleted)
                    {
                        // Skipped!
                        startedByTransition = false;
                        _viewModel.CurrentStoryTime += op.OpDuration + op.TransitionDuration;
                        continue;
                    }
                    _viewModel.CurrentStoryTime += op.OpDuration;
                    
                    // 2. Play the transition into the next clip if applicable
                    if (hasNextTransition)
                    {
                        await PlayTransitionAsync(op, nextOp, token);
                        startedByTransition = true;
                        previousTransitionDuration = op.TransitionDuration;
                        _viewModel.CurrentStoryTime += op.TransitionDuration;
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

                await Task.Delay(50, token);
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
            
            if (nextOp != null)
            {
                var nextTotalDuration = nextOp.OpDuration + op.TransitionDuration;
                
                double globalSpeed = _viewModel.PlaybackSpeed == 0 ? 1.0 : _viewModel.PlaybackSpeed;
                if (globalSpeed != 1.0)
                {
                    nextTotalDuration = TimeSpan.FromSeconds(nextTotalDuration.TotalSeconds / globalSpeed);
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

            UpdateTelemetryOverlay();
        }

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
            _playerControl.OverlayPlayer1.SetMediaPlayer(_overlayMediaPlayer1);

            _overlayMediaPlayer2 = new MediaPlayer();
            _overlayMediaPlayer2.IsLoopingEnabled = false;
            _overlayMediaPlayer2.AutoPlay = false;
            _playerControl.OverlayPlayer2.SetMediaPlayer(_overlayMediaPlayer2);
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

            // Determine which overlays should be active right now (max 2)
            OverlayClip desired1 = null;
            OverlayClip desired2 = null;

            foreach (var overlay in overlays.OrderBy(o => o.ZOrder))
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

        private void ActivateOverlaySlot(int slot, OverlayClip overlay, TimeSpan currentStoryTime)
        {
            var player = slot == 1 ? _overlayMediaPlayer1 : _overlayMediaPlayer2;
            var grid = slot == 1 ? _playerControl.OverlayGrid1 : _playerControl.OverlayGrid2;

            // Set source (this is where GPU resources are allocated — only when needed)
            player.Source = MediaSource.CreateFromUri(new Uri(overlay.FilePath));
            
            // Seek to the correct position within the overlay's source video
            TimeSpan offsetIntoOverlay = currentStoryTime - overlay.StartTime;
            player.PlaybackSession.Position = overlay.VideoStartTime + offsetIntoOverlay;
            
            if (_isAnimating && !_isPaused)
            {
                player.PlaybackSession.PlaybackRate = _viewModel.PlaybackSpeed;
                player.Play();
            }
            else
            {
                player.Pause();
            }

            grid.Opacity = overlay.Opacity;

            if (slot == 1) _activeOverlay1 = overlay;
            else _activeOverlay2 = overlay;
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

        private void ApplyOverlayTransform(int slot, OverlayClip overlay)
        {
            var transform = slot == 1 ? _playerControl.OverlayTransform1 : _playerControl.OverlayTransform2;
            transform.ScaleX = overlay.Scale;
            transform.ScaleY = overlay.Scale;
            transform.TranslateX = overlay.X;
            transform.TranslateY = overlay.Y;
        }

        private void ApplyOverlayDriftCorrection(int slot, OverlayClip overlay, TimeSpan currentStoryTime)
        {
            var player = slot == 1 ? _overlayMediaPlayer1 : _overlayMediaPlayer2;
            if (player.PlaybackSession == null) return;

            TimeSpan expectedPosition = overlay.VideoStartTime + (currentStoryTime - overlay.StartTime);
            TimeSpan actualPosition = player.PlaybackSession.Position;
            TimeSpan drift = (expectedPosition - actualPosition).Duration();

            if (drift > TimeSpan.FromMilliseconds(200))
            {
                player.PlaybackSession.Position = expectedPosition;
            }
        }

        private void HideAllOverlays()
        {
            if (_activeOverlay1 != null) ReleaseOverlaySlot(1);
            if (_activeOverlay2 != null) ReleaseOverlaySlot(2);
        }
    }
}

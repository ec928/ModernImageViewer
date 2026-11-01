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
using System.Linq;
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
    public sealed partial class CinematicWindow
    {
        private class SlideFrame : IDisposable
        {
            public CanvasBitmap GpuBitmap { get; }
            public SceneIntelligence Intelligence { get; }
            public CameraTransform Trajectory { get; set; } = new();
            /// <summary>Every move this slide makes, in order. Trajectory is the one on screen.</summary>
            public List<CameraTransform> Shots { get; set; } = new();
            public bool IsSharedResource { get; set; } = false;
            public Rect EngineStartCrop;
            public Rect EngineEndCrop;
            public bool HasEngineCrops;

            public float CurrentScale;
            public Vector2 CurrentPan;
            public float CurrentRotation;

            public ColorMatrixEffect? BlurColorMatrix { get; private set; }
            public GaussianBlurEffect? BlurEffect { get; private set; }
            public CanvasBitmap? BlurredBackground { get; private set; }

            public SlideFrame(CanvasBitmap bitmap, SceneIntelligence intel)
            {
                GpuBitmap = bitmap;
                Intelligence = intel;

                BlurColorMatrix = new ColorMatrixEffect { Source = GpuBitmap, ColorMatrix = new Matrix5x4 { M11 = 0.5f, M22 = 0.5f, M33 = 0.5f, M44 = 1.0f, M54 = 0 } };
                BlurEffect = new GaussianBlurEffect { Source = BlurColorMatrix, BlurAmount = 40.0f, BorderMode = EffectBorderMode.Hard };
                BakeBlurredBackground();
            }

            /// <summary>
            /// Blur a small copy once. Radius scales with the copy so the background matches a 40px blur of the full bitmap.
            /// </summary>
            private void BakeBlurredBackground()
            {
                if (BlurColorMatrix == null || BlurEffect == null) return;
                CanvasRenderTarget? baked = null;
                try
                {
                    float srcW = GpuBitmap.SizeInPixels.Width;
                    float srcH = GpuBitmap.SizeInPixels.Height;
                    float srcLong = Math.Max(srcW, srcH);
                    if (srcLong < 1) return;

                    const float proxyLong = 480f;
                    float scale = srcLong <= proxyLong ? 1f : proxyLong / srcLong;
                    float pw = Math.Max(1, MathF.Round(srcW * scale));
                    float ph = Math.Max(1, MathF.Round(srcH * scale));

                    using var down = new CanvasRenderTarget(GpuBitmap.Device, pw, ph, 96);
                    using (var ds = down.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ds.DrawImage(BlurColorMatrix, new Rect(0, 0, pw, ph), GpuBitmap.Bounds, 1f, CanvasImageInterpolation.Linear);
                    }

                    using var smallBlur = new GaussianBlurEffect
                    {
                        Source = down,
                        BlurAmount = 40f * scale,
                        BorderMode = EffectBorderMode.Hard
                    };
                    baked = new CanvasRenderTarget(GpuBitmap.Device, pw, ph, 96);
                    using (var ds = baked.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ds.DrawImage(smallBlur);
                    }

                    BlurredBackground = baked;
                    baked = null;

                    BlurEffect.Dispose();
                    BlurEffect = null;
                    BlurColorMatrix.Dispose();
                    BlurColorMatrix = null;
                }
                catch (Exception ex)
                {
                    baked?.Dispose();
                    System.Diagnostics.Debug.WriteLine($"[Cinematic] blur bake: {ex.Message}");
                }
            }

            public void Dispose()
            {
                BlurredBackground?.Dispose();
                BlurredBackground = null;
                if (!IsSharedResource)
                {
                    GpuBitmap?.Dispose();
                    BlurColorMatrix?.Dispose();
                    BlurEffect?.Dispose();
                }
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
            _fadeStartTime = TimeSpan.Zero;
            _crossfadeBlurAlpha = 0f;
            _crossfadeSubjectAlpha = 0f;
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
            double duration = ViewModel.GetCurrentEffectiveSettings().DurationSeconds;
            if (duration > 0)
            {
                double perMove = duration / Math.Max(1, _primaryFrame.Shots.Count);
                _primaryFrame.Trajectory.RecommendedDurationSeconds = perMove;
                _slideDurationSeconds = perMove;
            }
            else
            {
                _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
            }
        }

        private async void UpdateActiveTrajectoriesAsync(bool forceEngineReroll = false)
        {
            if (_primaryFrame == null || SlideshowCanvas.ActualWidth <= 0) return;

            var settings = ViewModel.GetCurrentEffectiveSettings();
            bool customCrops = settings.StartCrop is Rect && settings.EndCrop is Rect;

            if (!forceEngineReroll && !customCrops && _primaryFrame.HasEngineCrops && !ViewModel.HasUnsavedZoomPan)
            {
                ApplyCropsToTrajectory(_primaryFrame.EngineStartCrop, _primaryFrame.EngineEndCrop);
                SnapshotEngineCrops(_primaryFrame, _primaryFrame.Trajectory, SlideshowCanvas.Size);
                if (_editing && !_previewing) ResetKeyframeBoxes();
                SlideshowCanvas.Invalidate();
                return;
            }

            if (!forceEngineReroll && customCrops && !ViewModel.HasUnsavedZoomPan)
            {
                ApplyCropsToTrajectory(settings.StartCrop!.Value, settings.EndCrop!.Value);
                if (_editing && !_previewing) ResetKeyframeBoxes();
                SlideshowCanvas.Invalidate();
                return;
            }

            int currentId = ++_trajectoryUpdateId;
            await Task.Delay(50);
            if (currentId != _trajectoryUpdateId || _primaryFrame == null) return;

            _mathCts?.Cancel();
            _mathCts = new CancellationTokenSource();
            var token = _mathCts.Token;
            var request = new PlaybackRequest(SlideshowCanvas.Size, settings);
            var intel = _primaryFrame.Intelligence;

            try
            {
                if (forceEngineReroll) _chosenMoves.Remove(ViewModel.CurrentClipId);
                var newSequence = await MovesForAsync(ViewModel.CurrentClipId, intel, request, token);

                if (!token.IsCancellationRequested && newSequence.Count > 0 && _primaryFrame != null)
                {
                    _currentSequence = newSequence;
                    _primaryFrame.Shots = newSequence.ToList();
                    var newTrajectory = _currentSequence.Dequeue();
                    _primaryFrame.Trajectory = newTrajectory;
                    _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                    if (!customCrops)
                        SnapshotEngineCrops(_primaryFrame, newTrajectory, request.Bounds);
                    SlideshowCanvas.Invalidate();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_editing && !_previewing && !ViewModel.HasUnsavedZoomPan)
                            ResetKeyframeBoxes();
                    });
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                // async void: nothing awaits this, and App_UnhandledException sets
                // Handled = false, so an escaping camera-math fault would terminate the
                // whole viewer rather than just fail to re-aim this slide.
                System.Diagnostics.Debug.WriteLine($"[Cinematic] Trajectory update: {ex.Message}");
            }
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
                _fadeStartTime = TimeSpan.Zero;
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

                int loadIndex = ViewModel.CurrentIndex;
                var newFrame = await DecodeAndMarshalImageAsync(loadIndex, token);
                if (newFrame == null || token.IsCancellationRequested)
                {
                    if (newFrame != null) DispatcherQueue.TryEnqueue(() => newFrame.Dispose());
                    if (newFrame == null && !token.IsCancellationRequested)
                    {
                        ViewModel.MarkClipFailed(loadIndex);
                        SkipFailedPicture(loadIndex);
                    }
                    return;
                }

                var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetCurrentEffectiveSettings());
                var queue = await MovesForAsync(ClipIdAt(loadIndex), newFrame.Intelligence, request, token);

                if (token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() => newFrame.Dispose());
                    return;
                }

                _primaryFrame?.Dispose();
                _primaryFrame = newFrame;
                _currentSequence = queue;
                _primaryFrame.Shots = queue.ToList();
                _primaryFrame.Trajectory = _currentSequence.Count > 0 ? _currentSequence.Dequeue() : new CameraTransform();
                if (request.Settings.StartCrop == null || request.Settings.EndCrop == null)
                    SnapshotEngineCrops(_primaryFrame, _primaryFrame.Trajectory, request.Bounds);

                _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                _slideStartTime = TimeSpan.Zero;
                _fadeStartTime = TimeSpan.Zero;
                _crossfadeBlurAlpha = 0f;
                _crossfadeSubjectAlpha = 0f;

                if (!ViewModel.IsPlaying && !_editing)
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
                if (frame != null) DispatcherQueue.TryEnqueue(() => frame.Dispose());
                if (frame == null && !token.IsCancellationRequested)
                    ViewModel.MarkClipFailed(indexToLoad);
                _isPreloading = false;
                return;
            }

            var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetEffectiveSettingsForIndex(indexToLoad));

            try
            {
                var queue = await MovesForAsync(ClipIdAt(indexToLoad), frame.Intelligence, request, token);

                if (!token.IsCancellationRequested)
                {
                    _secondaryFrame = frame;
                    _nextSequence = queue;
                    _secondaryFrame.Shots = queue.ToList();
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
            await _restoreGate.Task;
            if (_canvasDevice == null) return;

            _sessionCts?.Cancel();
            _sessionCts = new CancellationTokenSource();
            var token = _sessionCts.Token;

            int firstIndex = ViewModel.CurrentIndex;
            var frame = await DecodeAndMarshalImageAsync(firstIndex, token);
            if (frame == null && !token.IsCancellationRequested)
            {
                ViewModel.MarkClipFailed(firstIndex);
                SkipFailedPicture(firstIndex);
                return;
            }
            if (frame != null && !token.IsCancellationRequested)
            {
                var request = new PlaybackRequest(SlideshowCanvas.Size, ViewModel.GetCurrentEffectiveSettings());
                var queue = await MovesForAsync(ClipIdAt(firstIndex), frame.Intelligence, request, token);

                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _primaryFrame = frame;
                        _currentSequence = queue;
                        _primaryFrame.Shots = queue.ToList();
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

        private void SkipFailedPicture(int failedIndex)
        {
            int next = ViewModel.PeekNextIndex();
            if (next != failedIndex)
            {
                DispatcherQueue.TryEnqueue(() => ViewModel.NavigateTo(next));
                return;
            }
            if (_nothingToPlayShown || RootGrid.XamlRoot == null) return;
            _nothingToPlayShown = true;
            _ = ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Nothing to play", "None of these pictures could be opened.");
        }

        // Auto chooses a slide's moves once, so what Edit show displays is what plays, however many times the show comes round.
        private readonly Dictionary<string, (List<CameraTransform> Shots, Size Bounds)> _chosenMoves = new();

        private static void ReAim(CameraTransform shot, Size from, Size to, Size image)
        {
            var start = CameraMath.CameraFromCrop(CameraMath.CropFromCamera(shot.StartScale, shot.StartPan, from, image), to, image);
            var end = CameraMath.CameraFromCrop(CameraMath.CropFromCamera(shot.EndScale, shot.EndPan, from, image), to, image);
            (shot.StartScale, shot.StartPan, shot.EndScale, shot.EndPan) = (start.Scale, start.Pan, end.Scale, end.Pan);
        }

        private string ClipIdAt(int index) => index >= 0 && index < ViewModel.Track.Count ? ViewModel.Track[index].Id : string.Empty;

        private async Task<Queue<CameraTransform>> MovesForAsync(string clipId, SceneIntelligence intel, PlaybackRequest request, CancellationToken token)
        {
            bool customFraming = request.Settings.StartCrop is Rect && request.Settings.EndCrop is Rect;
            if (!customFraming && _chosenMoves.TryGetValue(clipId, out var chosen))
            {
                var shots = chosen.Shots.Select(s => s.Copy()).ToList();
                if (chosen.Bounds != request.Bounds)
                {
                    foreach (var shot in shots) ReAim(shot, chosen.Bounds, request.Bounds, intel.ImageSize);
                }
                if (request.Settings.DurationSeconds > 0)
                    foreach (var shot in shots) shot.RecommendedDurationSeconds = request.Settings.DurationSeconds / shots.Count;
                return new Queue<CameraTransform>(shots);
            }

            var moves = await Task.Run(() => _director.GetSequence(intel, request.Bounds, request.Settings, token), token);
            if (!customFraming && moves.Count > 0 && !string.IsNullOrEmpty(clipId))
                _chosenMoves[clipId] = (moves.Select(s => s.Copy()).ToList(), request.Bounds);
            return moves;
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
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Cinematic] GPU upload: {ex.Message}");
                }
                finally
                {
                    result.Bitmap.Dispose();
                    tcs.TrySetResult(true);
                }
            });

            if (!isEnqueued)
            {
                result.Bitmap.Dispose();
                tcs.TrySetResult(false);
            }

            using (token.Register(() => tcs.TrySetResult(false)))
                await tcs.Task;

            if (token.IsCancellationRequested && newFrame != null)
            {
                var leak = newFrame;
                newFrame = null;
                DispatcherQueue.TryEnqueue(() => leak.Dispose());
            }
            return newFrame;
        }

        private void CompositionTarget_Rendering(object? sender, object e)
        {
            if (_primaryFrame == null || SlideshowCanvas == null) return;

            var args = e as RenderingEventArgs;
            TimeSpan totalTime = args?.RenderingTime ?? TimeSpan.Zero;
            TimeSpan sinceLastFrame = _lastRenderTime == TimeSpan.Zero ? TimeSpan.Zero : totalTime - _lastRenderTime;
            _lastRenderTime = totalTime;

            if (!ViewModel.IsPlaying || _isManuallyNavigating)
            {
                // Pause holds the picture: the clocks wait with it, so Play carries on from here.
                if (_slideStartTime != TimeSpan.Zero) _slideStartTime += sinceLastFrame;
                if (_fadeStartTime != TimeSpan.Zero) _fadeStartTime += sinceLastFrame;
                if (_editing && !_previewing)
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

            // The fade starts when the next picture is due, or when it arrives if it is late, and the next slide's clock starts with it.
            if (_secondaryFrame != null && _fadeStartTime == TimeSpan.Zero && elapsedSeconds >= cfStartElapsed)
                _fadeStartTime = totalTime;
            double fadeElapsed = _fadeStartTime == TimeSpan.Zero ? -1 : (totalTime - _fadeStartTime).TotalSeconds;
            bool fadeFinished = _secondaryFrame != null && fadeElapsed >= crossfadeDuration;

            if (progress >= 1.0 && _previewing)
            {
                EndPreview();
                return;
            }

            if (fadeFinished || (progress >= 1.0 && _secondaryFrame == null))
            {
                if (fadeFinished)
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
                    _slideStartTime = _fadeStartTime;
                    _fadeStartTime = TimeSpan.Zero;
                    fadeElapsed = -1;
                    elapsedSeconds = (totalTime - _slideStartTime).TotalSeconds;
                    progress = elapsedSeconds / _slideDurationSeconds;
                    _crossfadeBlurAlpha = 0f;
                    _crossfadeSubjectAlpha = 0f;
                }
                else if (_currentSequence.Count > 0)
                {
                    _primaryFrame!.Trajectory = _currentSequence.Dequeue();
                    _slideDurationSeconds = _primaryFrame.Trajectory.RecommendedDurationSeconds;
                    _slideStartTime = totalTime;
                    elapsedSeconds = 0;
                    progress = 0;
                }
                else
                {
                    int next = ViewModel.PeekNextIndex();
                    if (next == ViewModel.CurrentIndex)
                    {
                        if (!ViewModel.IsLoopEnabled)
                        {
                            ViewModel.IsPlaying = false;
                            SyncPlayPauseUI();
                            progress = 1.0;
                        }
                        else
                        {
                            _slideStartTime = totalTime;
                            elapsedSeconds = 0;
                            progress = 0;
                        }
                    }
                    else
                    {
                        progress = 1.0;
                    }
                }
            }

            float mainT = (float)Math.Clamp(progress, 0.0, 1.0);
            UpdateFrameVectors(_primaryFrame!, mainT, ViewModel.GetCurrentEffectiveSettings().IntensityPercent);

            if (fadeElapsed >= 0 && _secondaryFrame != null)
            {
                float fade = (float)Math.Clamp(fadeElapsed / crossfadeDuration, 0.0, 1.0);
                _crossfadeBlurAlpha = fade * fade * (3f - 2f * fade);
                _crossfadeSubjectAlpha = _crossfadeBlurAlpha;

                // Already moving as it fades in, so nothing stops or starts when the old picture goes.
                float incomingT = (float)Math.Clamp(fadeElapsed / _secondaryFrame.Trajectory.RecommendedDurationSeconds, 0.0, 1.0);
                UpdateFrameVectors(_secondaryFrame, incomingT, ViewModel.GetCurrentEffectiveSettings().IntensityPercent);
            }
            else
            {
                _crossfadeBlurAlpha = 0f;
                _crossfadeSubjectAlpha = 0f;
            }

            SlideProgressBar.Value = mainT;
            SlideshowCanvas.Invalidate();
        }

        private void SlideshowCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            try
            {
                if (_primaryFrame != null && _primaryFrame.GpuBitmap.Device == sender.Device)
                    DrawFrame(args, _primaryFrame, sender.Size, 1.0f, 1.0f);

                if (_secondaryFrame != null && _secondaryFrame.GpuBitmap.Device == sender.Device && (_crossfadeBlurAlpha > 0f || _crossfadeSubjectAlpha > 0f))
                {
                    DrawFrame(args, _secondaryFrame, sender.Size, _crossfadeBlurAlpha, _crossfadeSubjectAlpha);
                }
            }
            catch (ObjectDisposedException) { }
        }

        private void DrawFrame(CanvasDrawEventArgs args, SlideFrame frame, Size bounds, float blurAlpha, float subjectAlpha)
        {
            var imgSize = frame.Intelligence.ImageSize;
            var traj = frame.Trajectory;
            bool isEditMode = _editing && !_previewing;

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
                                        Math.Clamp(rawPanX, -bufferX, bufferX) - bufferX,
                                        Math.Clamp(rawPanY, -bufferY, bufferY) - bufferY)
                                    * Matrix3x2.CreateRotation(frame.CurrentRotation * -0.5f, bgCenter);

                    args.DrawingSession.Transform = bgTransform;

                    if (frame.BlurredBackground != null)
                    {
                        args.DrawingSession.DrawImage(
                            frame.BlurredBackground,
                            new Rect(0, 0, frame.GpuBitmap.Size.Width, frame.GpuBitmap.Size.Height),
                            frame.BlurredBackground.Bounds,
                            blurAlpha,
                            CanvasImageInterpolation.Linear);
                    }
                    else if (frame.BlurEffect != null)
                    {
                        args.DrawingSession.DrawImage(frame.BlurEffect, 0, 0, frame.GpuBitmap.Bounds, blurAlpha, CanvasImageInterpolation.Linear);
                    }
                }

                if (subjectAlpha > 0f)
                {
                    var fgCenter = new Vector2((float)bounds.Width / 2f, (float)bounds.Height / 2f);
                    var fgTransform = Matrix3x2.CreateScale(frame.CurrentScale) * Matrix3x2.CreateTranslation(frame.CurrentPan) * Matrix3x2.CreateRotation(frame.CurrentRotation, fgCenter);

                    args.DrawingSession.Transform = fgTransform;
                    args.DrawingSession.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, subjectAlpha, CanvasImageInterpolation.HighQualityCubic);
                }
            }
            else
            {
                if (subjectAlpha > 0f)
                {
                    var centerPoint = new Vector2((float)bounds.Width / 2f, (float)bounds.Height / 2f);
                    var transform = Matrix3x2.CreateScale(frame.CurrentScale) * Matrix3x2.CreateTranslation(frame.CurrentPan) * Matrix3x2.CreateRotation(frame.CurrentRotation, centerPoint);

                    args.DrawingSession.Transform = transform;
                    args.DrawingSession.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, subjectAlpha, CanvasImageInterpolation.HighQualityCubic);
                }
            }
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

            if (traj.Mode == LayoutMode.Fullscreen && traj.Technique != "Custom" && SlideshowCanvas.ActualWidth > 0)
            {
                var img = frame.Intelligence.ImageSize;
                (frame.CurrentPan, frame.CurrentRotation) = CameraFraming.KeepCovered(frame.CurrentScale, frame.CurrentPan, frame.CurrentRotation,
                    (float)SlideshowCanvas.ActualWidth, (float)SlideshowCanvas.ActualHeight, (float)img.Width, (float)img.Height);
            }
        }
    }
}

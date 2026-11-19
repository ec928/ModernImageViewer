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
    public sealed partial class ImageViewerControl
    {
        private const int MaxSwayLongestSide = 3072;
        private const float SwayOverscan = 1.025f;
        private const double SwayRampSeconds = 0.6;
        private const int SwayFaultsBeforeStop = 45;

        private sealed class ViewerSwayState : IDisposable
        {
            public required string Path { get; init; }
            public required IParallaxMap DepthMap { get; init; }
            public byte[]? CrispBytes { get; init; }
            public int CrispWidth { get; init; }
            public int CrispHeight { get; init; }
            public float Middle => DepthMap.Middle;
            public double Phase;
            public double Clock = -1;
            public float PointerCurrent;
            public float PointerTarget;
            public double PointerClock = -1;
            public Stopwatch Shown { get; } = Stopwatch.StartNew();

            public CanvasBitmap? SourceBitmapRef;
            public CanvasRenderTarget? PhotoSurface;
            public CanvasRenderTarget? OutputSurface;
            public CanvasBitmap? DepthBitmap;
            public CanvasBitmap? CrispBitmap;
            public ScaleEffect? DepthScale;
            public BorderEffect? DepthAtSize;
            public ScaleEffect? CrispScale;
            public BorderEffect? CrispAtSize;
            public BorderEffect? PhotoAtEdges;
            public ColorMatrixEffect? Shift;
            public DisplacementMapEffect? Moved;
            public PixelShaderEffect? Splat;
            public float SplatStep;

            public void DisposeGpuPipeline()
            {
                Splat?.Dispose(); Splat = null;
                Moved?.Dispose(); Moved = null;
                Shift?.Dispose(); Shift = null;
                PhotoAtEdges?.Dispose(); PhotoAtEdges = null;
                CrispAtSize?.Dispose(); CrispAtSize = null;
                CrispScale?.Dispose(); CrispScale = null;
                DepthAtSize?.Dispose(); DepthAtSize = null;
                DepthScale?.Dispose(); DepthScale = null;
                CrispBitmap?.Dispose(); CrispBitmap = null;
                DepthBitmap?.Dispose(); DepthBitmap = null;
                OutputSurface?.Dispose(); OutputSurface = null;
                PhotoSurface?.Dispose(); PhotoSurface = null;
                SourceBitmapRef = null;
            }

            public void Dispose() => DisposeGpuPipeline();
        }

        private bool _isSwayEnabled;
        private bool _swayPaused;
        private bool _swayFollowPointer;
        private bool _swayPreparing;
        private bool _swayUnloaded;
        private bool _swayRenderingSubscribed;
        private bool _updatingSwaySliders;
        private bool _swayPrefsDirty;
        private double _swayDepth = SurpriseOptions.SwayRotationDefault;
        private double _swaySwing = SurpriseOptions.SwaySwingDefault;
        private int _swayTicket;
        private int _swayDrawFaults;
        private CancellationTokenSource? _swayCts;
        private DispatcherTimer? _swaySaveTimer;
        private ViewerSwayState? _swayState;

        public bool IsSwayEnabled => _isSwayEnabled;
        public bool IsSwayFollowPointer => _swayFollowPointer;

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
            SyncSwaySliders();
        }

        private void OnExternalSwayPreferencesChanged(double depth, double swing)
        {
            void Apply()
            {
                if (_swayUnloaded) return;
                if (Math.Abs(_swayDepth - depth) <= 1e-6 && Math.Abs(_swaySwing - swing) <= 1e-6) return;
                _swayDepth = depth;
                _swaySwing = swing;
                SyncSwaySliders();
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
            SwayPreferences.Changed -= OnExternalSwayPreferencesChanged;
            _swaySaveTimer?.Stop();
            FlushSwayPreferences();
            StopSwayRuntime(requestRender: false);
        }

        private void HandleSwayDeviceLost()
        {
            StopSwayRuntime(requestRender: false);
        }

        public void ResetSwayForClose()
        {
            _isSwayEnabled = false;
            _swayPaused = false;
            _swayFollowPointer = false;
            StopSwayRuntime(requestRender: false);
            SyncSwayChrome();
        }

        public void SetInitialSway(bool enabled, bool followPointer = false)
        {
            _isSwayEnabled = enabled;
            _swayFollowPointer = followPointer;
            _swayPaused = false;
            SyncSwayChrome();
        }

        public void On3DExtensionChanged()
        {
            if (!DepthMaps.Available && _isSwayEnabled)
            {
                _isSwayEnabled = false;
                _swayPaused = false;
                _swayFollowPointer = false;
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

        private void SyncSwaySliders()
        {
            _updatingSwaySliders = true;
            try
            {
                if (SwayDepthSlider != null) SwayDepthSlider.Value = Math.Round(_swayDepth * 100.0, 1);
                if (SwayDepthValue != null) SwayDepthValue.Text = $"{_swayDepth * 100.0:0.0}%";
                if (SwaySwingSlider != null) SwaySwingSlider.Value = Math.Round(_swaySwing * 4.0) / 4.0;
                if (SwaySwingValue != null) SwaySwingValue.Text = $"{_swaySwing:0.##} s";
            }
            finally
            {
                _updatingSwaySliders = false;
            }
        }

        private void SyncSwayChrome()
        {
            bool active = _isSwayEnabled && CanUseSway;
            if (SwayButton != null)
            {
                SwayButton.Background = active
                    ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
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

            bool showPlay = _swayPaused || _swayFollowPointer;
            if (SwayPlayPauseIcon != null) SwayPlayPauseIcon.Symbol = showPlay ? Symbol.Play : Symbol.Pause;
            if (SwayPlayPauseButton != null)
            {
                string label = showPlay ? "Play sway (Space)" : "Pause sway (Space)";
                ToolTipService.SetToolTip(SwayPlayPauseButton, label);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SwayPlayPauseButton, label);
            }

            if (SwayPointerButton != null)
            {
                SwayPointerButton.Background = _swayFollowPointer
                    ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            if (SwaySwingGroup != null) SwaySwingGroup.Opacity = _swayFollowPointer ? 0.4 : 1.0;
            if (SwaySwingSlider != null) SwaySwingSlider.IsEnabled = !_swayFollowPointer;

            SyncSwaySliders();
        }

        private void UpdateSwayHudLayout(double availableWidth)
        {
            if (SwayHudBorder == null || SwaySwingGroup == null || SwayDivider2 == null || SwayDepthSlider == null || SwaySwingSlider == null)
                return;

            bool compact = availableWidth > 0 && availableWidth < 480;
            if (compact)
            {
                SwayHudBorder.CornerRadius = new CornerRadius(14);
                Grid.SetRow(SwaySwingGroup, 1);
                Grid.SetColumn(SwaySwingGroup, 0);
                Grid.SetColumnSpan(SwaySwingGroup, 2);
                SwaySwingGroup.HorizontalAlignment = HorizontalAlignment.Center;
                SwayDivider2.Visibility = Visibility.Collapsed;
                SwayDepthSlider.Width = 80;
                SwaySwingSlider.Width = 110;
            }
            else
            {
                SwayHudBorder.CornerRadius = new CornerRadius(24);
                Grid.SetRow(SwaySwingGroup, 0);
                Grid.SetColumn(SwaySwingGroup, 2);
                Grid.SetColumnSpan(SwaySwingGroup, 1);
                SwaySwingGroup.HorizontalAlignment = HorizontalAlignment.Left;
                SwayDivider2.Visibility = Visibility.Visible;
                SwayDepthSlider.Width = 88;
                SwaySwingSlider.Width = 76;
            }
        }

        private void OnSwayPrepareForNewImage()
        {
            ++_swayTicket;
            _swayCts?.Cancel();
            _swayCts?.Dispose();
            _swayCts = null;
            _swayPreparing = false;
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
            (_isSwayEnabled && CanUseSway && _swayState?.OutputSurface != null) ? _swayState.OutputSurface : null;

        private void ToggleSway()
        {
            if (!CanUseSway) return;
            _isSwayEnabled = !_isSwayEnabled;
            if (!_isSwayEnabled)
            {
                _swayPaused = false;
                _swayFollowPointer = false;
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

            _swayPreparing = true;
            SyncSwayChrome();
            ShowHud();

            try
            {
                var mapTask = DepthMaps.ForAsync(path);
                var maskTask = SubjectMasks.ForAsync(path);

                var map = await mapTask;
                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested)
                    return;

                if (map == null || map.Grey.Length == 0)
                {
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    SyncSwayChrome();
                    ShowNotification("Could not build 3D depth for this image");
                    return;
                }

                byte[]? crispBytes = null;
                int crispW = 0, crispH = 0;
                var outline = await maskTask;
                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested)
                    return;

                if (outline != null && outline.Size > 0 && _rawGpuBitmap != null)
                {
                    int baseW = _logicalImageWidth > 1 ? (int)Math.Round(_logicalImageWidth) : (int)_rawGpuBitmap.SizeInPixels.Width;
                    int baseH = _logicalImageHeight > 1 ? (int)Math.Round(_logicalImageHeight) : (int)_rawGpuBitmap.SizeInPixels.Height;
                    var (swayW, swayH) = ViewerMath.SwaySurfaceSize(baseW, baseH, MaxSwayLongestSide);
                    crispW = Math.Min(swayW, SwayDepth.MostWidth);
                    crispH = Math.Max(1, (int)Math.Round(swayH * (double)crispW / swayW));

                    var refPx = await SwayShader.LoadCrispReferencePixelsAsync(path, crispW, crispH, token);
                    if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested)
                        return;

                    byte[] greyMap = map.Grey;
                    int gw = map.Width, gh = map.Height;
                    byte[] alpha = outline.Alpha;
                    int maskSize = outline.Size;
                    int tw = crispW, th = crispH;
                    crispBytes = await Task.Run(() =>
                        SwayDepth.Crisp(greyMap, gw, gh, alpha, maskSize, tw, th, refPx?.Pixels, refPx?.Width ?? 0, refPx?.Height ?? 0), token);
                }

                if (ticket != _swayTicket || !_isSwayEnabled || _swayUnloaded || token.IsCancellationRequested || _rawGpuBitmap == null || !CanUseSway)
                    return;

                var device = _canvasDevice ?? CanvasDevice.GetSharedDevice();
                if (device == null)
                {
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    SyncSwayChrome();
                    return;
                }

                var state = new ViewerSwayState
                {
                    Path = path,
                    DepthMap = map,
                    CrispBytes = crispBytes,
                    CrispWidth = crispW,
                    CrispHeight = crispH
                };

                if (!RebuildSwayGpuPipeline(state, device, _rawGpuBitmap))
                {
                    state.Dispose();
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    SyncSwayChrome();
                    ShowNotification("Could not initialize 3D sway");
                    return;
                }

                DrawSwayToSurface(state, advanceClock: false);
                _swayPreparing = false;
                _swayState = state;
                _swayDrawFaults = 0;
                SyncSwayChrome();
                UpdateSwayRenderingSubscription();
                RequestRender();
                ShowHud();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
            {
                if (ticket == _swayTicket && !_swayUnloaded)
                {
                    _swayPreparing = false;
                    _isSwayEnabled = false;
                    StopSwayRuntime(requestRender: false);
                    SyncSwayChrome();
                    Debug.WriteLine($"[ViewerSway] Prep failed: {ex.Message}");
                }
            }
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

                var photoAtEdges = new BorderEffect
                {
                    Source = photoSurface,
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };
                var shift = new ColorMatrixEffect
                {
                    Source = depthAtSize,
                    ClampOutput = true,
                    AlphaMode = CanvasAlphaMode.Straight
                };
                var moved = new DisplacementMapEffect
                {
                    Source = photoAtEdges,
                    Displacement = shift,
                    XChannelSelect = EffectChannelSelect.Red,
                    YChannelSelect = EffectChannelSelect.Green,
                    Amount = (float)(w * SurpriseOptions.SwayRotationMost * 2.0)
                };
                float splatStep = Math.Max(1f, w / (float)map.Width / 2f);
                var splat = SwayShader.CreateSplat(photoAtEdges, splatDepth, (float)(w * SurpriseOptions.SwayRotationMost) + splatStep * 2f + 2f);

                state.SourceBitmapRef = sourceBitmap;
                state.PhotoSurface = photoSurface;
                state.OutputSurface = outputSurface;
                state.DepthBitmap = depthBitmap;
                state.DepthScale = depthScale;
                state.DepthAtSize = depthAtSize;
                state.CrispBitmap = crispBitmap;
                state.CrispScale = crispScale;
                state.CrispAtSize = crispAtSize;
                state.PhotoAtEdges = photoAtEdges;
                state.Shift = shift;
                state.Moved = moved;
                state.Splat = splat;
                state.SplatStep = splatStep;
                return true;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                state.DisposeGpuPipeline();
                return false;
            }
        }

        private void DrawSwayToSurface(ViewerSwayState state, bool advanceClock)
        {
            if (state.OutputSurface == null || state.PhotoSurface == null || state.Shift == null || state.Moved == null)
                return;

            double rotation = SurpriseOptions.ClampedSwayRotation(_swayDepth);
            double swing = SurpriseOptions.ClampedSwaySwing(_swaySwing);
            double shownSeconds = state.Shown.Elapsed.TotalSeconds;
            double ramp = Math.Clamp(shownSeconds / SwayRampSeconds, 0.0, 1.0);

            float angle;
            if (_swayFollowPointer)
            {
                if (advanceClock)
                {
                    double dt = state.PointerClock < 0 ? 0.0 : Math.Max(0.0, shownSeconds - state.PointerClock);
                    state.PointerClock = shownSeconds;
                    state.PointerCurrent = ViewerMath.SmoothSwayAngle(state.PointerCurrent, state.PointerTarget, dt);
                }
                angle = state.PointerCurrent;
            }
            else
            {
                if (advanceClock && !_swayPaused)
                {
                    state.Phase = SurpriseOptions.NextSwayPhase(state.Phase, shownSeconds, ref state.Clock, swing);
                }
                angle = (float)Math.Sin(state.Phase * Math.PI * 2.0);
            }

            float w = state.PhotoSurface.SizeInPixels.Width;
            float h = state.PhotoSurface.SizeInPixels.Height;
            float s = (float)(angle * w * rotation * ramp);
            float pivot = state.Middle;

            float amount = state.Moved.Amount;
            float a = -s / amount;
            state.Shift.ColorMatrix = new Matrix5x4
            {
                M11 = a, M12 = 0, M13 = 0, M14 = 0,
                M21 = 0, M22 = 0, M23 = 0, M24 = 0,
                M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                M41 = 0, M42 = 0, M43 = 0, M44 = 0,
                M51 = 0.5f - a * pivot, M52 = 0.5f, M53 = 0, M54 = 1
            };

            ICanvasImage drawn = state.Moved;
            if (state.Splat is { } splat)
            {
                splat.Properties["shift"] = s;
                splat.Properties["pivot"] = pivot;
                splat.Properties["stepPx"] = state.SplatStep;
                float reach = Math.Min(splat.MaxSamplerOffset - 1, Math.Abs(s) * Math.Max(pivot, 1f - pivot) + state.SplatStep * 2f);
                splat.Properties["reach"] = reach;
                drawn = splat;
            }

            float over = 1f + (SwayOverscan - 1f) * (float)ramp;
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
            _swayFollowPointer && _swayState != null && Math.Abs(_swayState.PointerTarget - _swayState.PointerCurrent) >= 0.0005f;

        private bool ShouldRenderSwayFrames() =>
            _swayState != null
            && !_swayUnloaded
            && !_swayPaused
            && (!_swayFollowPointer || NeedSwayPointerSettle() || !IsSwayRamped());

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
                DrawSwayToSurface(state, advanceClock: true);
                _swayDrawFaults = 0;
                PreviewCanvas?.Invalidate();

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
            if (!_isSwayEnabled || !_swayFollowPointer || _swayState == null || PreviewCanvas == null || InputOverlay == null)
                return;

            float dpiScale = PreviewCanvas.Dpi / 96.0f;
            double displayedImgW = (EffectiveLogicalWidth / dpiScale) * _targetZoom;
            double viewW = InputOverlay.ActualWidth > 0 ? InputOverlay.ActualWidth : RootGrid.ActualWidth;
            double panX = -(ImageScroll?.HorizontalOffset ?? 0) + _tempPanX;
            _swayState.PointerTarget = ViewerMath.CalculateSwayPointerAngle(ptr.X, viewW, displayedImgW, panX);
            UpdateSwayRenderingSubscription();
        }

        public bool HandleViewerKeystroke(VirtualKey key)
        {
            if (HandleAnimationKeystroke(key))
                return true;

            bool isCtrlHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool isAltHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isCtrlHeld || isAltHeld)
                return false;

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

            return false;
        }

        private void ContextFlyout_Opening(object sender, object e) => UpdateSwayAvailability();

        private void ToggleSway_Click(object sender, RoutedEventArgs e) => ToggleSway();

        private void SwayPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_swayState == null) return;

            if (_swayFollowPointer)
            {
                _swayState.Phase = ViewerMath.SwayPhaseFromAngle(_swayState.PointerCurrent);
                _swayState.Clock = -1;
                _swayFollowPointer = false;
                _swayPaused = false;
            }
            else
            {
                _swayPaused = !_swayPaused;
                if (!_swayPaused) _swayState.Clock = -1;
            }

            SyncSwayChrome();
            UpdateSwayRenderingSubscription();
            ShowHud();
        }

        private void SwayPointerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_swayState == null) return;

            _swayFollowPointer = !_swayFollowPointer;
            if (_swayFollowPointer)
            {
                float currentAngle = (float)Math.Sin(_swayState.Phase * Math.PI * 2.0);
                _swayState.PointerCurrent = currentAngle;
                _swayState.PointerTarget = currentAngle;
                _swayState.PointerClock = -1;
                _swayPaused = false;
            }
            else
            {
                _swayState.Phase = ViewerMath.SwayPhaseFromAngle(_swayState.PointerCurrent);
                _swayState.Clock = -1;
                _swayPaused = false;
            }

            SyncSwayChrome();
            UpdateSwayRenderingSubscription();
            ShowHud();
        }

        private void SwayDepthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingSwaySliders) return;
            _swayDepth = SurpriseOptions.ClampedSwayRotation(Math.Round(e.NewValue, 1) / 100.0);
            if (SwayDepthValue != null) SwayDepthValue.Text = $"{_swayDepth * 100.0:0.0}%";
            ResetHudHideTimer();
            ScheduleSwayPreferencesSave();
            if (_swayState != null && !_swayRenderingSubscribed)
                DrawStaticSwayFrame();
        }

        private void SwaySwingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingSwaySliders) return;
            _swaySwing = SurpriseOptions.ClampedSwaySwing(Math.Round(e.NewValue * 4.0) / 4.0);
            if (SwaySwingValue != null) SwaySwingValue.Text = $"{_swaySwing:0.##} s";
            ResetHudHideTimer();
            ScheduleSwayPreferencesSave();
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

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    /// <summary>
    /// Prototype (branch prototype-titles-3d): a scene with one picture played in 3D. The cell keeps its own picture
    /// element, so its crop, frame and the camera's move are all as before; only what the element shows changes, to the
    /// photo drawn each frame with its near things moved against the far, by the depth map, as the camera moves. A pan
    /// moves the near things further the same way; a zoom, which has no sideways move of its own, gets a slow one.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        // The most the nearest thing moves against the middle distance over a whole move, as a share of the photo's width.
        private const double DepthTravel = 0.032;
        // A little in from the edges, so what is drawn past an edge never shows.
        private const float DepthOverscan = 1.025f;
        private const double DepthRampSeconds = 0.6;
        private const int DepthLongestSide = 3072;

        private sealed class DepthScene
        {
            public required Image Target { get; init; }
            public required ImageSource Original { get; init; }
            public required CanvasImageSource Surface { get; init; }
            public required CanvasBitmap Photo { get; init; }
            public required CanvasBitmap Depth { get; init; }
            public required ColorMatrixEffect Shift { get; init; }
            public required DisplacementMapEffect Moved { get; init; }
            /// <summary>Sway drawn as a forward warp (Shaders\SwaySplat.hlsl). Null if the shader could not be made: Sway then uses <see cref="Moved"/>.</summary>
            public PixelShaderEffect? Splat { get; init; }
            /// <summary>Pixels between the sources the splat tries.</summary>
            public float SplatStep { get; init; }
            public required CameraMove Move { get; init; }
            public required float Middle { get; init; }
            public required Vector2 Drift { get; init; }
            public System.Diagnostics.Stopwatch Shown { get; } = System.Diagnostics.Stopwatch.StartNew();
            // Slide: the background with what the near layer hides filled in, and the near layer cut out by its mask.
            public CanvasBitmap? Plate { get; init; }
            public BorderEffect? PlateBorder { get; init; }
            public CanvasBitmap? Mask { get; init; }
            public AlphaMaskEffect? Near { get; init; }
            /// <summary>Where the camera's move was when Slide began, so its one move fills the rest of the scene.</summary>
            public double FromSeconds { get; init; }
            /// <summary>Slide's way across: 1 right, -1 left.</summary>
            public float Way { get; init; }
            /// <summary>Slide: the subject's centre in pixels, which the background grows about.</summary>
            public Vector2 SubjectCentre { get; init; }
            /// <summary>Slide was chosen but the photo has no clear subject in front: it plays Sway.</summary>
            public bool SwayInstead { get; init; }
            public bool RockInstead => SwayInstead;
            /// <summary>Sway's place in the cycle, in turns. 0 is the center. Kept when the swing length changes.</summary>
            public double SwayPhase;
            /// <summary>Shown time of the last sway sample. Negative until the first.</summary>
            public double SwayClock = -1;
            /// <summary>Sway for this depth map. At most 4% of the width, and never under 0.4%.</summary>
            public double SafeRotation { get; init; } = SurpriseOptions.SwayFormulaMost;
            /// <summary>How long the 3D filter took to select this picture, when that check was timed.</summary>
            public long? FilterMilliseconds { get; init; }
            /// <summary>The picture's file, and the judge's edge and gap, for sway-marks.log.</summary>
            public string Path { get; init; } = "";
            public float? Edge { get; init; }
            public float? Gap { get; init; }
            /// <summary>The slider's sway on the last frame drawn. Null until a Sway frame draws.</summary>
            public double? SwayPlayed;
        }

        // Slide: how far the background travels behind the foreground over the scene, as a share of the photo's width.
        private const float SlideTravel = 0.042f;

        private DepthScene? _depthScene;
        private int _depthTicket;
        private int _depthDrawFaults;
        // About three quarters of a second at 60 frames. One missed frame is a stall, not the end of the effect.
        private const int DepthDrawFaultsBeforeStop = 45;
        private CanvasDevice? _depthDevice;
        // The folder check waits while the picture on screen is still asking for its depth or subject.
        private bool _depthPrepWaiting;
        // On-screen sway and filter time. The main-menu wheel turns it off. On until a saved choice says otherwise.
        private bool _depthDebugReadout = true;
        private Border? _depthReadout;
        private TextBlock? _depthReadoutText;
        private int _depthReadoutTenths = int.MinValue;
        private long _depthReadoutFilter = long.MinValue;
        private string? _depthReadoutKind;

        /// <summary>While a 3D picture plays, show the sway rotation in use and how long the 3D filter took. The gear menu turns this off.</summary>
        public bool DepthDebugReadout
        {
            get => _depthDebugReadout;
            set
            {
                if (_depthDebugReadout == value) return;
                _depthDebugReadout = value;
                if (!value) HideDepthReadout();
            }
        }

        // DXGI device removed, hung, or reset. Win2D surfaces from that device cannot be drawn.
        private static bool DepthDeviceWasLost(Exception ex)
        {
            for (Exception? cursor = ex; cursor != null; cursor = cursor.InnerException)
            {
                int hr = cursor.HResult;
                if (hr == unchecked((int)0x887A0005) || hr == unchecked((int)0x887A0006) || hr == unchecked((int)0x887A0007))
                    return true;
            }
            return false;
        }

        private void NoteDepthDevice(CanvasDevice device)
        {
            if (ReferenceEquals(_depthDevice, device)) return;
            if (_depthDevice != null) _depthDevice.DeviceLost -= DepthDeviceLost;
            _depthDevice = device;
            _depthDevice.DeviceLost += DepthDeviceLost;
        }

        private void DepthDeviceLost(CanvasDevice sender, object args)
        {
            DispatcherQueue.TryEnqueue(() => ReplaceLostDepthDevice(sender));
        }

        /// <summary>
        /// Win2D keeps handing out the lost device until this handler asks for a new one.
        /// The bitmaps from the lost device cannot be drawn, so this scene goes back to its own picture.
        /// The next scene takes the new device.
        /// </summary>
        private void ReplaceLostDepthDevice(CanvasDevice lost)
        {
            if (!ReferenceEquals(_depthDevice, lost))
            {
                if (_depthScene != null) StopSceneDepth();
                return;
            }
            _depthDevice.DeviceLost -= DepthDeviceLost;
            _depthDevice = null;
            try
            {
                var fresh = CanvasDevice.GetSharedDevice();
                if (!ReferenceEquals(fresh, lost)) NoteDepthDevice(fresh);
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"3D: graphics device lost: {ex.GetType().Name}");
            }
            if (_depthScene != null) StopSceneDepth();
        }

        /// <summary>Called as a scene comes on, once its camera move has started.</summary>
        private async Task<bool> TryStartSceneDepthAsync(ShowScene scene, ShowScene? next)
        {
            if (_autoPlaySeconds == 0 || !DepthMaps.Available) return false;
            var options = GeneratorSettingsFor(_activeShow);
            if (options.DepthChance <= 0) return false;

            int ticket = ++_depthTicket;
            // The next scene's map is made while this one plays, so it is ready when it comes on.
            // Queued after this picture's own models: a preload must not take the card first.
            void PreloadNext()
            {
                if (next != null && SinglePhoto(next) is string nextPath)
                {
                    _ = DepthMaps.ForAsync(nextPath);
                    if (LiveDepthMotion() == SurpriseOptions.DepthSlide)
                    {
                        _ = SubjectMasks.ForAsync(nextPath);
                        _ = ParallaxJudge.ForAsync(nextPath);
                    }
                }
            }
            if (SinglePhoto(scene) is not string path) { PreloadNext(); return false; }
            if (Random.Shared.NextDouble() >= options.DepthChance) { PlayTiming.Write("3D: not this scene (odds)"); PreloadNext(); return false; }
            var move = _cameraMove;
            if (move == null || _cameraGoingHome) { PlayTiming.Write("3D: not this scene (no camera move)"); PreloadNext(); return false; }
            var parts = Enumerable.Range(0, _items.Count).Select(GetCellParts)
                .Where(p => p != null && !IsEmptyCell(p.Bound.Item)).ToList();
            if (parts.Count != 1 || parts[0] is not { } cell) { PlayTiming.Write($"3D: not this scene ({parts.Count} cells)"); PreloadNext(); return false; }
            if (!string.Equals(cell.Bound.Item.Path, path, StringComparison.OrdinalIgnoreCase)) { PlayTiming.Write("3D: not this scene (cell shows another photo)"); PreloadNext(); return false; }

            var mapTask = DepthMaps.ForAsync(path);
            var judgeTask = ParallaxJudge.ForAsync(path);
            if (LiveDepthMotion() == SurpriseOptions.DepthSlide)
                _ = SubjectMasks.ForAsync(path);
            PreloadNext();
            _depthPrepWaiting = true;
            try
            {
            var map = await mapTask;
            if (map == null || map.Grey.Length == 0 || ticket != _depthTicket || _autoPlaySeconds == 0) { PlayTiming.Write($"3D: not this scene (map {(map == null ? "missing" : "late")})"); return false; }
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                NoteDepthDevice(device);
                var (pixels, photoWidth, photoHeight) = await LoadDepthPixelsAsync(path);
                var photo = CanvasBitmap.CreateFromBytes(device, pixels, photoWidth, photoHeight, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                // Slide moves the near part as one piece over a background that holds, so it is cut and filled once, here.
                bool slide = LiveDepthMotion() == SurpriseOptions.DepthSlide;
                // Chosen before the layers are made: the fill goes on the side the subject uncovers.
                float way = Random.Shared.Next(2) == 0 ? -1f : 1f;
                ParallaxLayers.Layers? layers = null;
                var judged = await judgeTask;
                if (slide)
                {
                    // Only a photo with a clear subject in front of its background slides. Any other plays Rock, which
                    // needs no subject (Ed, 2026-11-07: fall back on the 3D movement that works).
                    var subject = await SubjectMasks.ForAsync(path);
                    if (subject == null || judged == null || judged.Fit != ParallaxJudge.Fit.Slide)
                    {
                        PlayTiming.Write($"3D: Sway instead of Slide ({(judged == null ? "not judged" : judged.Why)})");
                        slide = false;
                    }
                    else
                    {
                        // The background slides toward the side away from the subject: the edge it uncovers is then the one
                        // with the most room behind the subject to cover it.
                        double sum = 0, weight = 0;
                        for (int i = 0; i < subject.Alpha.Length; i++) { float a = subject.Alpha[i]; sum += a * (i % subject.Size); weight += a; }
                        float subjectX = weight > 0 ? (float)(sum / weight / subject.Size) : 0.5f;
                        way = subjectX < 0.5f ? 1f : -1f;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        layers = await Task.Run(() => ParallaxLayers.Build(pixels, photoWidth, photoHeight, map.Raw ?? map.Grey, map.Width, map.Height, subject.Alpha, subject.Size, way > 0 ? 1 : -1));
                        PlayTiming.Write($"3D: Slide layers in {clock.ElapsedMilliseconds} ms, subject {layers.NearShare:P0}, in front by {layers.InFront:0.00}");
                        if (layers.InFront < 0.08f)
                        {
                            PlayTiming.Write("3D: Sway instead of Slide (the subject is not in front)");
                            layers = null;
                            slide = false;
                        }
                    }
                }
                if (ticket != _depthTicket || _autoPlaySeconds == 0 || cell.Photo.Source is not ImageSource original) { photo.Dispose(); return false; }
                var grey = new byte[map.Width * map.Height * 4];
                for (int i = 0; i < map.Grey.Length; i++)
                {
                    byte v = map.Grey[i];
                    grey[i * 4] = v; grey[i * 4 + 1] = v; grey[i * 4 + 2] = v; grey[i * 4 + 3] = 255;
                }
                var depth = CanvasBitmap.CreateFromBytes(device, grey, map.Width, map.Height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                var size = photo.SizeInPixels;
                var depthAtSize = new BorderEffect
                {
                    Source = new ScaleEffect
                    {
                        Source = depth,
                        Scale = new Vector2(size.Width / (float)map.Width, size.Height / (float)map.Height),
                        InterpolationMode = CanvasImageInterpolation.HighQualityCubic
                    },
                    ExtendX = CanvasEdgeBehavior.Clamp,
                    ExtendY = CanvasEdgeBehavior.Clamp
                };
                var shift = new ColorMatrixEffect { Source = depthAtSize, ClampOutput = true, AlphaMode = CanvasAlphaMode.Straight };
                var moved = new DisplacementMapEffect
                {
                    Source = new BorderEffect { Source = photo, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp },
                    Displacement = shift,
                    XChannelSelect = EffectChannelSelect.Red,
                    YChannelSelect = EffectChannelSelect.Green,
                    Amount = (float)(size.Width * DepthTravel * 4)
                };
                var photoAtEdges = new BorderEffect { Source = photo, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp };
                float splatStep = Math.Max(1f, size.Width / (float)map.Width / 2f);
                var splat = MakeSwaySplat(photoAtEdges, depthAtSize, (float)(size.Width * SurpriseOptions.SwayRotationMost) + splatStep * 2 + 2);
                double angle = Random.Shared.NextDouble() * Math.PI * 2;
                CanvasBitmap? plate = null, mask = null;
                BorderEffect? plateBorder = null;
                AlphaMaskEffect? near = null;
                if (layers != null)
                {
                    plate = CanvasBitmap.CreateFromBytes(device, layers.Plate, photoWidth, photoHeight, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    plateBorder = new BorderEffect { Source = plate, ExtendX = CanvasEdgeBehavior.Mirror, ExtendY = CanvasEdgeBehavior.Clamp };
                    mask = CanvasBitmap.CreateFromBytes(device, layers.Mask, photoWidth, photoHeight, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    near = new AlphaMaskEffect { Source = photo, AlphaMask = mask };
                }
                double safeRotation = SurpriseOptions.SwayRotationForPicture(map.Grey, map.Width, map.Height, judged?.Gap ?? double.NaN, SurpriseOptions.SwayFormulaMost);
                long? filterMs = DepthMaps.TryCheckMilliseconds(path, out long timedMs) ? timedMs : null;
                var state = new DepthScene
                {
                    Target = cell.Photo,
                    Original = original,
                    Surface = new CanvasImageSource(device, size.Width, size.Height, 96),
                    Photo = photo,
                    Depth = depth,
                    Shift = shift,
                    Moved = moved,
                    Splat = splat,
                    SplatStep = splatStep,
                    Move = move,
                    Middle = map.Middle,
                    Drift = new Vector2((float)Math.Cos(angle), (float)(Math.Sin(angle) * 0.5)),
                    Plate = plate,
                    PlateBorder = plateBorder,
                    Mask = mask,
                    Near = near,
                    FromSeconds = ReferenceEquals(_cameraMove, move) ? _cameraClock.Elapsed.TotalSeconds : 0,
                    Way = way,
                    SubjectCentre = layers == null ? Vector2.Zero : new Vector2(layers.CentreX * photoWidth, layers.CentreY * photoHeight),
                    SwayInstead = LiveDepthMotion() == SurpriseOptions.DepthSlide && layers == null,
                    SafeRotation = safeRotation,
                    FilterMilliseconds = filterMs,
                    Path = path,
                    Edge = judged?.Edge,
                    Gap = judged?.Gap
                };
                // Drawn once before it is shown, so the swap shows the same picture.
                // One failed frame used to abandon the scene. A lost device is not retried: those bitmaps are dead.
                Exception? drawFailed = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        DrawDepth(state);
                        drawFailed = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        drawFailed = ex;
                        if (DepthDeviceWasLost(ex))
                        {
                            ReplaceLostDepthDevice(device);
                            break;
                        }
                    }
                }
                if (drawFailed != null)
                {
                    ReleaseDepth(state);
                    // Device loss was already handed to the new device above. Throwing it again would retire that device.
                    if (!DepthDeviceWasLost(drawFailed)) throw drawFailed;
                    PlayTiming.Write($"3D: not shown: {drawFailed.GetType().Name} {drawFailed.Message}");
                    return false;
                }
                StopSceneDepth();
                _depthScene = state;
                state.Target.Source = state.Surface;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += DepthFrame;
                PlayTiming.Write($"3D: on for {System.IO.Path.GetFileName(path)}, {size.Width}x{size.Height}");
                return true;
            }
            catch (Exception ex)
            {
                if (DepthDeviceWasLost(ex) && _depthDevice != null)
                    ReplaceLostDepthDevice(_depthDevice);
                PlayTiming.Write($"3D: not shown: {ex.GetType().Name} {ex.Message}");
            }

            return false;
            }
            finally
            {
                _depthPrepWaiting = false;
            }
        }

        /// <summary>
        /// Called as a scene comes on, once its camera move has started. Slide plays on a photo with a clear subject in
        /// front, Rock on any other. A photo fit for no 3D is left out of a Parallax show when it is made (ParallaxFit),
        /// so no scene there should fall back to a plain pan or zoom (Ed, 2026-11-07).
        /// </summary>
        private async Task StartSceneDepthAsync(ShowScene scene, ShowScene? next)
        {
            try
            {
                await TryStartSceneDepthAsync(scene, next);
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"StartSceneDepthAsync error: {ex.Message}");
            }
        }

        /// <summary>The scene's one photo, when it has exactly one.</summary>
        private static string? SinglePhoto(ShowScene scene)
        {
            var paths = scene.GetPhotoPaths().Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            return paths.Count == 1 ? paths[0] : null;
        }

        private static async Task<(byte[] Pixels, int Width, int Height)> LoadDepthPixelsAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            bool turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
            double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
            double scale = Math.Min(1, DepthLongestSide / Math.Max(w, h));
            int width = Math.Max(1, (int)Math.Round(w * scale)), height = Math.Max(1, (int)Math.Round(h * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)(turned ? height : width),
                ScaledHeight = (uint)(turned ? width : height),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            return (data.DetachPixelData(), width, height);
        }

        private void DepthFrame(object? sender, object e)
        {
            var state = _depthScene;
            if (state == null) return;
            try
            {
                DrawDepth(state);
                _depthDrawFaults = 0;
            }
            catch (Exception ex)
            {
                if (DepthDeviceWasLost(ex))
                {
                    PlayTiming.Write("3D: graphics device lost");
                    if (_depthDevice != null) ReplaceLostDepthDevice(_depthDevice);
                    else StopSceneDepth();
                    return;
                }
                // The first failed draw used to put the cell's own picture back for the rest of the scene. Moving a
                // sway slider writes the settings file on this thread, and the draw after that stall can fail once.
                if (_depthDrawFaults == 0)
                    PlayTiming.Write($"3D: draw missed: {ex.GetType().Name} {ex.Message}");
                if (++_depthDrawFaults < DepthDrawFaultsBeforeStop) return;
                PlayTiming.Write($"3D: stopped drawing: {ex.Message}");
                StopSceneDepth();
            }
        }

        /// <summary>The motion playing now. A show that names one uses it. Otherwise the engine's, and a missing name is Slide.</summary>
        private string LiveDepthMotion()
        {
            string? held = _activeShow?.GlobalDefaults.DepthMotion;
            if (_activeShow?.GlobalDefaults.HoldsValues == true && !string.IsNullOrEmpty(held))
                return SurpriseOptions.CanonicalDepthMotion(held);
            return SurpriseOptions.CanonicalDepthMotion(SurpriseChoices.DepthMotion);
        }

        private void DrawDepth(DepthScene state)
        {
            if (state.Near != null && state.Plate != null)
            {
                DrawSlide(state);
                ShowDepthReadout(state, "Slide", null);
                return;
            }
            var size = state.Photo.SizeInPixels;
            float width = size.Width, height = size.Height;
            // Where the camera is in its move. Once it has gone on to another move (a pause going home), the 3D holds.
            double seconds = ReferenceEquals(_cameraMove, state.Move) ? _cameraClock.Elapsed.TotalSeconds : state.Move.Seconds;
            var start = state.Move.Start;
            var end = state.Move.End;
            var now = state.Move.At(Math.Min(seconds, state.Move.Seconds));
            double panX = end.X - start.X, panY = end.Y - start.Y;
            double pan = Math.Sqrt(panX * panX + panY * panY);
            double ramp = Math.Clamp(state.Shown.Elapsed.TotalSeconds / DepthRampSeconds, 0, 1);
            ramp = ramp * ramp * (3 - 2 * ramp);
            Vector2 along;
            float scale;
            string motion = "Pan";
            double? rotationUsed = null;
            if (pan > 0.03)
            {
                // The near things move further the way the view moves the picture, centred on the middle of the move.
                along = new Vector2((float)(-((now.X - start.X) / pan - panX / pan * 0.5)), (float)(-((now.Y - start.Y) / pan - panY / pan * 0.5)));
                scale = (float)(width * DepthTravel * ramp);
            }
            else
            {
                // One swing: center, one side, the other side, center. The swing slider sets the length.
                // The Sway rotation slider sets the amount, read every frame so a drag shows on the playing picture.
                // The computed per-picture amount (SafeRotation) is only recorded beside it in sway-marks.log.
                // It returns through the photograph: a depth map has no unseen side of a person to turn into.
                // Starts at the center. A new length keeps this place and changes the speed.
                // The scene's own clock, so a replaced camera move cannot freeze it.
                double swing = SurpriseOptions.ClampedSwaySwing(SurpriseChoices.SwaySwing);
                double rotation = SurpriseOptions.ClampedSwayRotation(SurpriseChoices.SwayRotation);
                state.SwayPlayed = rotation;
                state.SwayPhase = SurpriseOptions.NextSwayPhase(state.SwayPhase, state.Shown.Elapsed.TotalSeconds, ref state.SwayClock, swing);
                along = new Vector2((float)Math.Sin(state.SwayPhase * Math.PI * 2), 0);
                scale = (float)(width * rotation * ramp);
                motion = "Sway";
                rotationUsed = rotation;
            }
            var shift = along * scale;
            float amount = state.Moved.Amount;
            float a = -shift.X / amount, b = -shift.Y / amount;
            // Sway holds the median, so the two depths move opposite ways. Slide holds the far plane.
            float pivot = state.SwayInstead ? state.Middle : SurpriseOptions.DepthPivot(LiveDepthMotion(), state.Middle);
            state.Shift.ColorMatrix = new Matrix5x4
            {
                M11 = a, M12 = b, M13 = 0, M14 = 0,
                M21 = 0, M22 = 0, M23 = 0, M24 = 0,
                M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                M41 = 0, M42 = 0, M43 = 0, M44 = 0,
                M51 = 0.5f - a * pivot, M52 = 0.5f - b * pivot, M53 = 0, M54 = 1
            };
            using var session = state.Surface.CreateDrawingSession(Microsoft.UI.Colors.Black);
            float over = 1f + (DepthOverscan - 1f) * (float)ramp;
            float cropW = width / over, cropH = height / over;
            ICanvasImage drawn = state.Moved;
            if (motion == "Sway" && state.Splat is { } splat)
            {
                // The splat reads its shift as where a source lands; the displacement above reads where an output looks.
                // Same motion: an output at x shows the source s with s + shift*(depth(s) - pivot) = x.
                float s = shift.X;
                splat.Properties["shift"] = s;
                splat.Properties["pivot"] = pivot;
                splat.Properties["stepPx"] = state.SplatStep;
                splat.Properties["reach"] = Math.Min(splat.MaxSamplerOffset - 1, Math.Abs(s) * Math.Max(pivot, 1 - pivot) + state.SplatStep * 2);
                drawn = splat;
            }
            session.DrawImage(drawn, new Rect(0, 0, width, height),
                new Rect((width - cropW) / 2, (height - cropH) / 2, cropW, cropH));
            ShowDepthReadout(state, motion, rotationUsed);
        }

        /// <summary>
        /// Slide: the foreground (subject + connected scene above the far plane) holds completely still at its original
        /// position and scale (so swapping in the 3D surface at ~1.5s produces zero jerk), and the distant background
        /// translates smoothly behind it with mirrored edge extension.
        /// </summary>
        private void DrawSlide(DepthScene state)
        {
            var size = state.Photo.SizeInPixels;
            float width = size.Width;
            double seconds = ReferenceEquals(_cameraMove, state.Move) ? _cameraClock.Elapsed.TotalSeconds : state.Move.Seconds;
            double span = Math.Max(1, state.Move.Seconds - state.FromSeconds);
            double p = Math.Clamp((seconds - state.FromSeconds) / span, 0, 1);
            float eased = (float)(0.5 - 0.5 * Math.Cos(p * Math.PI));
            float dx = -state.Way * SlideTravel * width * eased;
            using var session = state.Surface.CreateDrawingSession(Microsoft.UI.Colors.Black);
            session.Transform = Matrix3x2.CreateTranslation(dx, 0);
            session.DrawImage((ICanvasImage?)state.PlateBorder ?? state.Plate!);
            session.Transform = Matrix3x2.Identity;
            session.DrawImage(state.Near!);
        }

        /// <summary>The cell shows its own picture again. As a scene goes, and when Play stops.</summary>
        private void ShowDepthReadout(DepthScene state, string kind, double? rotation)
        {
            if (!ReferenceEquals(_depthScene, state) || !_depthDebugReadout) return;
            try
            {
                int tenths = rotation is double fraction ? (int)Math.Round(fraction * 1000) : -1;
                long filter = state.FilterMilliseconds ?? -1;
                if (tenths == _depthReadoutTenths && filter == _depthReadoutFilter && kind == _depthReadoutKind
                    && _depthReadout != null && _depthReadout.Visibility == Visibility.Visible)
                    return;
                string line = tenths < 0
                    ? kind
                    : kind + " " + (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";
                string text = line + "\n" + FormatDepthFilter(state.FilterMilliseconds);
                if (_depthReadout == null)
                {
                    var block = new TextBlock
                    {
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        FontSize = 13
                    };
                    _depthReadoutText = block;
                    _depthReadout = new Border
                    {
                        Child = block,
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD8, 0x18, 0x18, 0x18)),
                        Padding = new Thickness(12, 8, 12, 8),
                        CornerRadius = new CornerRadius(8),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(16, 16, 0, 0),
                        IsHitTestVisible = false
                    };
                    AddOverCanvas(_depthReadout, 6400);
                }
                _depthReadoutText!.Text = text;
                _depthReadout.Visibility = Visibility.Visible;
                _depthReadoutTenths = tenths;
                _depthReadoutFilter = filter;
                _depthReadoutKind = kind;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
            }
        }

        private static string FormatDepthFilter(long? milliseconds)
        {
            if (milliseconds == null) return "Filter not timed";
            if (milliseconds < 100) return "Filter " + milliseconds.Value.ToString(CultureInfo.InvariantCulture) + " ms";
            return "Filter " + (milliseconds.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        private void HideDepthReadout()
        {
            _depthReadoutTenths = int.MinValue;
            _depthReadoutFilter = long.MinValue;
            _depthReadoutKind = null;
            if (_depthReadout != null) _depthReadout.Visibility = Visibility.Collapsed;
        }

        private void StopSceneDepth()
        {
            _depthDrawFaults = 0;
            HideDepthReadout();
            var state = _depthScene;
            if (state == null) return;
            _depthScene = null;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= DepthFrame;
            if (_depthDebugReadout) WriteSwayMark(state);
            // The source getter returns a new wrapper, so a comparison missed and the cell kept the surface.
            // Disposing the bitmaps under it then crashed when Play stopped. The picture goes back first.
            try
            {
                state.Target.Source = state.Original;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
            }
            // A later turn, so a frame can draw the picture before the surface it was sampling is released.
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ReleaseDepth(state)))
                ReleaseDepth(state);
        }

        /// <summary>
        /// With the 3D debug readout on, each Sway scene adds one line to sway-marks.log beside the app: the file, the
        /// sway the slider was on when the scene ended, the computed amount, and the judge's edge and gap. Tab-separated.
        /// Slide and Pan scenes write nothing.
        /// </summary>
        private static void WriteSwayMark(DepthScene state)
        {
            if (state.SwayPlayed is not double played) return;
            try
            {
                string log = System.IO.Path.Combine(AppContext.BaseDirectory, "sway-marks.log");
                static string N(double? v, string f) => v is double d && double.IsFinite(d) ? d.ToString(f, CultureInfo.InvariantCulture) : "";
                if (!System.IO.File.Exists(log))
                    System.IO.File.AppendAllText(log, "time\tfile\tslider %\tcomputed %\tedge\tgap" + Environment.NewLine);
                System.IO.File.AppendAllText(log, string.Join("\t",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    System.IO.Path.GetFileName(state.Path),
                    N(played * 100, "0.0"),
                    N(state.SafeRotation * 100, "0.0"),
                    N(state.Edge, "0.00"),
                    N(state.Gap, "0.00")) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }

        private static byte[]? _swaySplatBytes;
        private static bool _swaySplatMissing;

        /// <summary>
        /// The Sway forward warp for one scene, or null if the shader cannot be loaded or made (Sway then draws with the
        /// displacement warp as before). Offsets up to <paramref name="maxOffset"/> pixels are sampled.
        /// </summary>
        private static PixelShaderEffect? MakeSwaySplat(ICanvasImage photo, ICanvasImage depthAtSize, float maxOffset)
        {
            if (_swaySplatMissing) return null;
            try
            {
                if (_swaySplatBytes == null)
                {
                    using var stream = typeof(MultiViewControl).Assembly.GetManifestResourceStream("ModernImageViewer.Shaders.SwaySplat.bin");
                    if (stream == null) { _swaySplatMissing = true; PlayTiming.Write("3D: sway splat shader missing"); return null; }
                    using var copy = new System.IO.MemoryStream();
                    stream.CopyTo(copy);
                    _swaySplatBytes = copy.ToArray();
                }
                return new PixelShaderEffect(_swaySplatBytes)
                {
                    Source1 = photo,
                    Source2 = depthAtSize,
                    Source1Mapping = SamplerCoordinateMapping.Offset,
                    Source2Mapping = SamplerCoordinateMapping.Offset,
                    MaxSamplerOffset = (int)Math.Ceiling(maxOffset)
                };
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException or System.IO.IOException)
            {
                _swaySplatMissing = true;
                PlayTiming.Write($"3D: sway splat not made: {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        private static void ReleaseDepth(DepthScene state)
        {
            try
            {
                state.Moved.Dispose();
                state.Splat?.Dispose();
                state.Shift.Dispose();
                state.Near?.Dispose();
                state.PlateBorder?.Dispose();
                state.Mask?.Dispose();
                state.Plate?.Dispose();
                state.Depth.Dispose();
                state.Photo.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
            }
        }
    }
}

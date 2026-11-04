using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace ModernImageViewer
{
    /// <summary>
    /// During auto-play the finished layout moves as one picture. Only the drawing moves: nothing in the layout changes,
    /// so saving, Undo and the framing of each cell never see it. The bar and the cards stay where they are.
    /// It moves the composition visuals, under XAML. A RenderTransform that changes every frame also changes the size XAML wants each
    /// image decoded at, which left images soft, and Rotation or Scale properties stop the app on a cell that has a RotateTransform.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        // Allows for the next layout being prepared and faded in, so this one is still moving when it goes.
        private const double CameraOverrunSeconds = 2.5;
        private const double CameraHomeSeconds = 0.45;

        private CameraMove? _cameraMove;
        private CameraKind? _lastCameraKind;
        private CameraView _cameraView = CameraView.Whole;
        private readonly Stopwatch _cameraClock = new();
        private bool _cameraRunning;
        private bool _cameraGoingHome;
        private int _layoutSeconds;
        // A layout made during a show is not the user doing something, so it does not bring the bar back over the pictures.
        private bool _quietBar;
        private readonly Stopwatch _floatClock = new();
        private readonly List<(FreeformCard Card, CellFloat Float)> _floats = new();

        private FrameworkElement[] CameraLayers => new FrameworkElement[] { CellsGrid, FreeformCanvas, CanvasFrameMat, CanvasFrameStroke, CanvasFrameDashes, CanvasFrameRim };

        private void StartCanvasCamera(Composition composition)
        {
            var options = SurpriseChoices;
            double amount = options.PickMotion(Random.Shared);
            var move = CameraMove.Plan(composition.CameraSubjects(), amount, options.MotionZoomChance, options.MotionFaceChance, _layoutSeconds + CameraOverrunSeconds, _lastCameraKind, Random.Shared, options.ForcedCameraKind, options.CameraStyle());
            if (move == null)
            {
                ResetCanvasCamera();
                return;
            }
            _lastCameraKind = move.Kind;
            var cards = _freeformCards.Where(c => !IsEmptyCell(c.Item)).OrderBy(c => c.ZIndex).ToList();
            _floats.Clear();
            for (int i = 0; i < cards.Count; i++)
                _floats.Add((cards[i], new CellFloat(i, cards.Count, amount, Random.Shared)));
            _floatClock.Restart();
            RunCamera(move, goingHome: false);
        }

        /// <summary>Back to the whole layout, gently, so a pause does not jump.</summary>
        private void SendCanvasCameraHome()
        {
            if (!_cameraRunning) return;
            // Where the camera is now, from the clock: the compositor moves it, so the last UI frame can be behind.
            var now = _cameraMove != null ? _cameraMove.At(_cameraClock.Elapsed.TotalSeconds) : _cameraView;
            RunCamera(new CameraMove(CameraKind.ZoomOut, now, CameraView.Whole, CameraHomeSeconds), goingHome: true);
        }

        private void RunCamera(CameraMove move, bool goingHome)
        {
            _cameraMove = move;
            _cameraGoingHome = goingHome;
            _cameraClock.Restart();
            AnimateCamera(move, 0);
            if (_cameraRunning) return;
            _cameraRunning = true;
            ClipRootToCanvas();
            RootGrid.SizeChanged += CameraRoot_SizeChanged;
            CompositionTarget.Rendering += CameraFrame;
        }

        // The camera as values the compositor animates, and each layer's transform worked out from them there. The
        // compositor runs on its own thread, so work on the UI thread (a scene being built, a layout being applied) no
        // longer stops the camera: moved here, once a frame, any of that held it still for those frames.
        private Microsoft.UI.Composition.CompositionPropertySet? _cameraProps;
        private const double CameraSamplesPerSecond = 30;

        private const string LayerExpression =
            "Matrix4x4.CreateFromScale(Vector3(cam.Zoom, cam.Zoom, 1))"
            + " * Matrix4x4.CreateFromTranslation(Vector3(atX * cam.Zoom + cam.PanX - atX, atY * cam.Zoom + cam.PanY - atY, 0))"
            + " * Matrix4x4.CreateFromTranslation(Vector3(-midX, -midY, 0))"
            + " * Matrix4x4.CreateFromAxisAngle(Vector3(0, 0, 1), cam.Angle)"
            + " * Matrix4x4.CreateFromTranslation(Vector3(midX, midY, 0))";

        /// <summary>
        /// The move from this many seconds in, sampled into keyframes the compositor plays: zoom (with the extra a turn
        /// needs), the pan that puts the view's centre in the middle, and the turn, about the middle of the canvas. Each
        /// layer follows by an expression that holds its own place in the canvas, such as the grid inside a frame, so the
        /// zoom is worked out from the canvas and not from the layer.
        /// </summary>
        private void AnimateCamera(CameraMove move, double fromSeconds)
        {
            double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
            if (width < 1 || height < 1) return;
            var compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
            if (_cameraProps == null)
            {
                _cameraProps = compositor.CreatePropertySet();
                _cameraProps.InsertScalar("Zoom", 1);
                _cameraProps.InsertScalar("PanX", 0);
                _cameraProps.InsertScalar("PanY", 0);
                _cameraProps.InsertScalar("Angle", 0);
            }

            double remaining = Math.Max(0.05, move.Seconds - fromSeconds);
            int samples = Math.Max(2, (int)Math.Ceiling(remaining * CameraSamplesPerSecond));
            var linear = compositor.CreateLinearEasingFunction();
            var zoomKeys = compositor.CreateScalarKeyFrameAnimation();
            var panXKeys = compositor.CreateScalarKeyFrameAnimation();
            var panYKeys = compositor.CreateScalarKeyFrameAnimation();
            var angleKeys = compositor.CreateScalarKeyFrameAnimation();
            for (int i = 0; i <= samples; i++)
            {
                float progress = i / (float)samples;
                var view = move.At(fromSeconds + remaining * progress);
                double zoom = view.Zoom * CameraView.TurnCover(view.Angle, width, height);
                zoomKeys.InsertKeyFrame(progress, (float)zoom, linear);
                panXKeys.InsertKeyFrame(progress, (float)(width / 2 - view.X * width * zoom), linear);
                panYKeys.InsertKeyFrame(progress, (float)(height / 2 - view.Y * height * zoom), linear);
                angleKeys.InsertKeyFrame(progress, (float)(view.Angle * Math.PI / 180), linear);
            }
            foreach (var keys in new[] { zoomKeys, panXKeys, panYKeys, angleKeys })
                keys.Duration = TimeSpan.FromSeconds(remaining);

            // Each layer's own place in the canvas, as it is now: a new layout can move the grid inside its frame.
            foreach (var layer in CameraLayers)
            {
                var at = layer.ActualOffset;
                var follow = compositor.CreateExpressionAnimation(LayerExpression);
                follow.SetReferenceParameter("cam", _cameraProps);
                follow.SetScalarParameter("atX", at.X);
                follow.SetScalarParameter("atY", at.Y);
                follow.SetScalarParameter("midX", (float)(width / 2 - at.X));
                follow.SetScalarParameter("midY", (float)(height / 2 - at.Y));
                ElementCompositionPreview.GetElementVisual(layer).StartAnimation("TransformMatrix", follow);
            }
            _cameraProps.StartAnimation("Zoom", zoomKeys);
            _cameraProps.StartAnimation("PanX", panXKeys);
            _cameraProps.StartAnimation("PanY", panYKeys);
            _cameraProps.StartAnimation("Angle", angleKeys);
        }

        private void StopCameraAnimations()
        {
            if (_cameraProps != null)
            {
                foreach (string name in new[] { "Zoom", "PanX", "PanY", "Angle" }) _cameraProps.StopAnimation(name);
                _cameraProps.InsertScalar("Zoom", 1);
                _cameraProps.InsertScalar("PanX", 0);
                _cameraProps.InsertScalar("PanY", 0);
                _cameraProps.InsertScalar("Angle", 0);
            }
            foreach (var layer in CameraLayers)
            {
                var visual = ElementCompositionPreview.GetElementVisual(layer);
                visual.StopAnimation("TransformMatrix");
                visual.TransformMatrix = Matrix4x4.Identity;
            }
        }

        /// <summary>Where the cover of the outgoing layout would have moved over the fade, kept inside the screen so no edge shows.</summary>
        private (double Scale, double X, double Y) OutgoingCameraStep(double fadeSeconds)
        {
            if (!_cameraRunning || _cameraGoingHome || _cameraMove == null) return (1, 0, 0);
            double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
            if (width < 1 || height < 1) return (1, 0, 0);
            double now = _cameraClock.Elapsed.TotalSeconds;
            var v0 = _cameraMove.At(now);
            var v1 = _cameraMove.At(now + fadeSeconds);
            double rawX = (v0.X - v1.X) * width * v1.Zoom;
            double rawY = (v0.Y - v1.Y) * height * v1.Zoom;
            double minScale = 1 + 2 * Math.Max(Math.Abs(rawX) / width, Math.Abs(rawY) / height);
            double scale = Math.Clamp(Math.Max(v1.Zoom / Math.Max(1, v0.Zoom), minScale), 1, 1.08);
            double maxX = (scale - 1) * width / 2;
            double maxY = (scale - 1) * height / 2;
            return (scale, Math.Clamp(rawX, -maxX, maxX), Math.Clamp(rawY, -maxY, maxY));
        }

        private void ResetCanvasCamera()
        {
            _cameraMove = null;
            _cameraClock.Reset();
            _cameraView = CameraView.Whole;
            RestFloatingCells();
            if (!_cameraRunning) return;
            _cameraRunning = false;
            CompositionTarget.Rendering -= CameraFrame;
            RootGrid.SizeChanged -= CameraRoot_SizeChanged;
            StopCameraAnimations();
            RootGrid.ClearValue(UIElement.ClipProperty);
        }

        private void CameraFrame(object? sender, object e)
        {
            if (_cameraMove == null) return;
            double elapsed = _cameraClock.Elapsed.TotalSeconds;
            if (_cameraGoingHome && elapsed >= _cameraMove.Seconds)
            {
                ResetCanvasCamera();
                return;
            }
            _cameraView = _cameraMove.At(elapsed);
            // The canvas moves on the compositor (AnimateCamera); here only the floating cells' small drift.
            ShowFloatingCells(_cameraView, _cameraGoingHome ? 1 - elapsed / _cameraMove.Seconds : 1);
        }

        /// <summary>
        /// On top of the cell's saved place and turn, which stay as they are. XAML keeps a cell's RotateTransform in this same matrix,
        /// so it has to include the saved turn, or a tilted cell would show straight.
        /// </summary>
        private void ShowFloatingCells(CameraView view, double strength)
        {
            double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
            double seconds = _floatClock.Elapsed.TotalSeconds;
            foreach (var (card, motion) in _floats)
            {
                var drift = motion.At(seconds, view, width, height, strength);
                PlaceCard(card, GetGeometry(card.Item).WindowRotation + drift.Turn, drift.Scale, drift.X, drift.Y);
            }
        }

        private static void PlaceCard(FreeformCard card, double turn, double scale, double x, double y)
        {
            var window = card.Container;
            var centre = new Vector3((float)(window.ActualWidth / 2), (float)(window.ActualHeight / 2), 0);
            ElementCompositionPreview.GetElementVisual(window).TransformMatrix =
                Matrix4x4.CreateTranslation(-centre)
                * Matrix4x4.CreateRotationZ((float)(turn * Math.PI / 180))
                * Matrix4x4.CreateScale((float)scale)
                * Matrix4x4.CreateTranslation(centre + new Vector3((float)x, (float)y, 0));
        }

        private void RestFloatingCells()
        {
            foreach (var (card, _) in _floats)
            {
                PlaceCard(card, GetGeometry(card.Item).WindowRotation, 1, 0, 0);
                ApplyWindowTurn(card);
            }
            _floats.Clear();
            _floatClock.Reset();
        }

        private void CameraRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ClipRootToCanvas();
            // The keyframes hold pixels for the old size: carry on from where the move is, for the new one.
            if (_cameraMove != null) AnimateCamera(_cameraMove, _cameraClock.Elapsed.TotalSeconds);
        }

        // A zoomed layer reaches past the canvas, which must not draw over the rest of the window.
        private void ClipRootToCanvas() =>
            RootGrid.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight) };
    }
}

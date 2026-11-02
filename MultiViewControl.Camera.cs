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
            var move = CameraMove.Plan(composition.CameraSubjects(), options.MotionAmount, options.MotionZoomChance, options.MotionFaceChance, _layoutSeconds + CameraOverrunSeconds, _lastCameraKind, Random.Shared, options.ForcedCameraKind);
            if (move == null)
            {
                ResetCanvasCamera();
                return;
            }
            _lastCameraKind = move.Kind;
            var cards = _freeformCards.Where(c => !IsEmptyCell(c.Item)).OrderBy(c => c.ZIndex).ToList();
            _floats.Clear();
            for (int i = 0; i < cards.Count; i++)
                _floats.Add((cards[i], new CellFloat(i, cards.Count, options.MotionAmount, Random.Shared)));
            _floatClock.Restart();
            RunCamera(move, goingHome: false);
        }

        /// <summary>Back to the whole layout, gently, so a pause does not jump.</summary>
        private void SendCanvasCameraHome()
        {
            if (!_cameraRunning) return;
            RunCamera(new CameraMove(CameraKind.ZoomOut, _cameraView, CameraView.Whole, CameraHomeSeconds), goingHome: true);
        }

        private void RunCamera(CameraMove move, bool goingHome)
        {
            _cameraMove = move;
            _cameraGoingHome = goingHome;
            _cameraClock.Restart();
            if (_cameraRunning) return;
            _cameraRunning = true;
            ClipRootToCanvas();
            RootGrid.SizeChanged += CameraRoot_SizeChanged;
            CompositionTarget.Rendering += CameraFrame;
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
            foreach (var layer in CameraLayers) ElementCompositionPreview.GetElementVisual(layer).TransformMatrix = Matrix4x4.Identity;
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
            ShowCameraView(_cameraView);
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

        /// <summary>Each layer has its own place in the canvas, such as the grid inside a frame, so the zoom is worked out from the canvas and not from the layer.</summary>
        private void ShowCameraView(CameraView view)
        {
            double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
            double panX = width / 2 - view.X * width * view.Zoom;
            double panY = height / 2 - view.Y * height * view.Zoom;
            foreach (var layer in CameraLayers)
            {
                var at = layer.ActualOffset;
                ElementCompositionPreview.GetElementVisual(layer).TransformMatrix =
                    Matrix4x4.CreateScale((float)view.Zoom)
                    * Matrix4x4.CreateTranslation((float)(at.X * view.Zoom + panX - at.X), (float)(at.Y * view.Zoom + panY - at.Y), 0);
            }
        }

        private void CameraRoot_SizeChanged(object sender, SizeChangedEventArgs e) => ClipRootToCanvas();

        // A zoomed layer reaches past the canvas, which must not draw over the rest of the window.
        private void ClipRootToCanvas() =>
            RootGrid.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight) };
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;

namespace ModernImageViewer
{
    /// <summary>
    /// During auto-play the finished layout moves as one picture. Only the drawing moves: nothing in the layout changes,
    /// so saving, Undo and the framing of each cell never see it. The bar and the cards stay where they are.
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

        private FrameworkElement[] CameraLayers => new FrameworkElement[] { CellsGrid, FreeformCanvas, CanvasFrameMat, CanvasFrameStroke, CanvasFrameDashes, CanvasFrameRim };

        private void StartCanvasCamera(Composition composition)
        {
            var options = SurpriseChoices;
            var move = CameraMove.Plan(composition.CameraSubjects(), options.MotionAmount, options.MotionZoomChance, options.MotionFaceChance, _layoutSeconds + CameraOverrunSeconds, _lastCameraKind, Random.Shared);
            if (move == null)
            {
                ResetCanvasCamera();
                return;
            }
            _lastCameraKind = move.Kind;
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

        private void ResetCanvasCamera()
        {
            _cameraMove = null;
            _cameraClock.Reset();
            _cameraView = CameraView.Whole;
            if (!_cameraRunning) return;
            _cameraRunning = false;
            CompositionTarget.Rendering -= CameraFrame;
            RootGrid.SizeChanged -= CameraRoot_SizeChanged;
            foreach (var layer in CameraLayers) layer.ClearValue(UIElement.RenderTransformProperty);
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
                if (layer.RenderTransform is not CompositeTransform transform)
                    layer.RenderTransform = transform = new CompositeTransform();
                transform.ScaleX = view.Zoom;
                transform.ScaleY = view.Zoom;
                transform.TranslateX = at.X * view.Zoom + panX - at.X;
                transform.TranslateY = at.Y * view.Zoom + panY - at.Y;
            }
        }

        private void CameraRoot_SizeChanged(object sender, SizeChangedEventArgs e) => ClipRootToCanvas();

        // A zoomed layer reaches past the canvas, which must not draw over the rest of the window.
        private void ClipRootToCanvas() =>
            RootGrid.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight) };
    }
}

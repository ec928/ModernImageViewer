using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace ModernImageViewer.VideoDirector.Views
{
    public sealed partial class DirectorPlayerControl : UserControl
    {
        private bool _isDragging = false;
        private Point _lastPointerPosition;

        public event EventHandler ViewportTransformChanged;
        public Microsoft.UI.Xaml.Media.CompositeTransform ActiveTransform { get; set; }

        // Raised from the full-screen InputLayer (the only reliable pointer catcher — the PiP's
        // MediaPlayerElement video surface does not raise its own pointer events). In placement
        // mode these target the arranged overlay (slot 1).
        public event EventHandler<(int slot, double dx, double dy)> OverlayBoxMoved;
        public event EventHandler<(int slot, int delta)> OverlayBoxWheel;
        public event EventHandler<int> OverlayBoxEditRequested;

        // When true, drag/wheel/double-tap manipulate the arranged PiP's placement rather than
        // panning/zooming the content transform.
        public bool PlacementMode { get; set; }

        public DirectorPlayerControl()
        {
            this.InitializeComponent();
        }

        private void InputLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = true;
            _lastPointerPosition = e.GetCurrentPoint(InputLayer).Position;
            InputLayer.CapturePointer(e.Pointer);
        }

        private void InputLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;

            var p = e.GetCurrentPoint(InputLayer).Position;
            var deltaX = p.X - _lastPointerPosition.X;
            var deltaY = p.Y - _lastPointerPosition.Y;
            _lastPointerPosition = p;

            if (PlacementMode)
            {
                OverlayBoxMoved?.Invoke(this, (1, deltaX, deltaY));
                return;
            }

            if (ActiveTransform == null) return;
            ActiveTransform.TranslateX += deltaX;
            ActiveTransform.TranslateY += deltaY;
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        }

        private void InputLayer_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(InputLayer).Properties.MouseWheelDelta;

            if (PlacementMode)
            {
                OverlayBoxWheel?.Invoke(this, (1, delta));
                return;
            }

            if (ActiveTransform == null) return;
            double zoomFactor = delta > 0 ? 1.1 : (1.0 / 1.1);
            double newScale = Math.Clamp(ActiveTransform.ScaleX * zoomFactor, 0.1, 10.0);
            ActiveTransform.ScaleX = newScale;
            ActiveTransform.ScaleY = newScale;
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        }

        private void InputLayer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (PlacementMode) OverlayBoxEditRequested?.Invoke(this, 1);
        }
    }
}

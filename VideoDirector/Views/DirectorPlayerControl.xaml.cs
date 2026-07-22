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

        // Canvas-arrange events — raised from the InputLayer by hit-testing the PiP boxes.
        // (The boxes contain a MediaPlayerElement video surface, which does not reliably raise
        // its own pointer events, so all input goes through the full-screen InputLayer.)
        public event EventHandler<int> OverlayBoxSelected;                       // slot
        public event EventHandler<(int slot, double dx, double dy)> OverlayBoxMoved;
        public event EventHandler<(int slot, int delta)> OverlayBoxWheel;
        public event EventHandler<int> OverlayBoxEditRequested;                  // slot (double-tap)

        private bool _canvasMode;
        public bool CanvasMode { get => _canvasMode; set => _canvasMode = value; }

        private int _dragSlot; // >0 while dragging a PiP box in canvas mode

        public DirectorPlayerControl()
        {
            this.InitializeComponent();
        }

        // Which PiP box (if any) is under the given InputLayer-space point; topmost (slot 2) wins.
        // The overlay grids are positioned via Margin + Width/Height in the same coordinate space
        // as the full-screen InputLayer, so a simple bounds test is valid.
        private int HitTestOverlaySlot(Point p)
        {
            if (IsInsideBox(OverlayGrid2, p)) return 2;
            if (IsInsideBox(OverlayGrid1, p)) return 1;
            return 0;
        }

        private static bool IsInsideBox(Grid g, Point p)
        {
            if (g == null || g.Opacity <= 0.01 || double.IsNaN(g.Width) || g.Width <= 0 || g.Height <= 0) return false;
            double left = g.Margin.Left, top = g.Margin.Top;
            return p.X >= left && p.X <= left + g.Width && p.Y >= top && p.Y <= top + g.Height;
        }

        private void InputLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(InputLayer).Position;

            if (_canvasMode)
            {
                _dragSlot = HitTestOverlaySlot(p);
                if (_dragSlot > 0)
                {
                    OverlayBoxSelected?.Invoke(this, _dragSlot);
                    _isDragging = true;
                    _lastPointerPosition = p;
                    InputLayer.CapturePointer(e.Pointer);
                }
                return;
            }

            _isDragging = true;
            _lastPointerPosition = p;
            InputLayer.CapturePointer(e.Pointer);
        }

        private void InputLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;

            var p = e.GetCurrentPoint(InputLayer).Position;
            var deltaX = p.X - _lastPointerPosition.X;
            var deltaY = p.Y - _lastPointerPosition.Y;
            _lastPointerPosition = p;

            if (_canvasMode)
            {
                if (_dragSlot > 0) OverlayBoxMoved?.Invoke(this, (_dragSlot, deltaX, deltaY));
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
            _dragSlot = 0;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            _dragSlot = 0;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(InputLayer);
            int delta = pt.Properties.MouseWheelDelta;

            if (_canvasMode)
            {
                int slot = HitTestOverlaySlot(pt.Position);
                if (slot > 0) OverlayBoxWheel?.Invoke(this, (slot, delta));
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
            if (!_canvasMode) return;
            int slot = HitTestOverlaySlot(e.GetPosition(InputLayer));
            if (slot > 0) OverlayBoxEditRequested?.Invoke(this, slot);
        }
    }
}

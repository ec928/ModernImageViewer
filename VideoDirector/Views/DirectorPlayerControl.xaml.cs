using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace ModernImageViewer.VideoDirector.Views
{
    // The single switch that decides what all pointer input does. Set by the engine from the
    // editor mode — nothing else influences input routing (strict mode segregation).
    public enum PlayerInputMode
    {
        Content,     // Edit mode: drag = pan the clip's content, wheel = zoom it.
        ArrangePips  // Arrange mode: drag = move the PiP under the cursor, wheel = resize it.
    }

    // What grabbing the PiP box does: move it, or resize it from a specific edge/corner.
    // Determined at pointer-press from where in the box the cursor is (interior = move,
    // near an edge = one-dimension resize, near a corner = two-dimension resize).
    public enum BoxGrab { Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public sealed partial class DirectorPlayerControl : UserControl
    {
        private bool _isDragging = false;
        private Point _lastPointerPosition;
        private int _dragSlot;
        private BoxGrab _dragGrab;

        // How close (px) to an edge counts as grabbing that edge for a resize.
        private const double HandleThreshold = 20.0;

        public event EventHandler ViewportTransformChanged;
        public Microsoft.UI.Xaml.Media.CompositeTransform ActiveTransform { get; set; }

        // PiP manipulation events (Arrange mode). Raised from the full-screen InputLayer — the
        // only reliable pointer catcher, since the PiP's MediaPlayerElement video surface does
        // not raise its own pointer events. Move and resize share one channel; the grab mode
        // tells the engine whether to translate the box or reshape it from an edge/corner.
        public event EventHandler<(int slot, BoxGrab grab, double dx, double dy)> OverlayBoxDragged;
        public event EventHandler<(int slot, int delta)> OverlayBoxWheel;

        public PlayerInputMode InputMode { get; set; } = PlayerInputMode.Content;

        public DirectorPlayerControl()
        {
            this.InitializeComponent();
        }

        // Which PiP box (if any) is under the given InputLayer-space point; topmost (slot 2) wins.
        // The overlay grids are positioned via Margin + Width/Height in the same coordinate space
        // as the full-screen InputLayer, so a plain bounds test is valid.
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

        // Classify where in the box the cursor is: near an edge/corner (resize) or interior (move).
        private BoxGrab ClassifyGrab(int slot, Point p)
        {
            var g = slot == 2 ? OverlayGrid2 : OverlayGrid1;
            double relX = p.X - g.Margin.Left;
            double relY = p.Y - g.Margin.Top;
            // Keep the threshold below half the box so a tiny box still has a movable interior.
            double t = Math.Min(HandleThreshold, Math.Min(g.Width, g.Height) / 3.0);
            bool nearLeft = relX <= t, nearRight = relX >= g.Width - t;
            bool nearTop = relY <= t, nearBottom = relY >= g.Height - t;

            if (nearTop && nearLeft) return BoxGrab.TopLeft;
            if (nearTop && nearRight) return BoxGrab.TopRight;
            if (nearBottom && nearLeft) return BoxGrab.BottomLeft;
            if (nearBottom && nearRight) return BoxGrab.BottomRight;
            if (nearLeft) return BoxGrab.Left;
            if (nearRight) return BoxGrab.Right;
            if (nearTop) return BoxGrab.Top;
            if (nearBottom) return BoxGrab.Bottom;
            return BoxGrab.Move;
        }

        private void InputLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(InputLayer).Position;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                _dragSlot = HitTestOverlaySlot(p);
                if (_dragSlot == 0) return; // clicked empty canvas — nothing to arrange
                _dragGrab = ClassifyGrab(_dragSlot, p);
                _isDragging = true;
                _lastPointerPosition = p;
                InputLayer.CapturePointer(e.Pointer);
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

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                if (_dragSlot > 0) OverlayBoxDragged?.Invoke(this, (_dragSlot, _dragGrab, deltaX, deltaY));
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

            if (InputMode == PlayerInputMode.ArrangePips)
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
            // Reserved (entry to Edit is via the dock for now).
        }
    }
}

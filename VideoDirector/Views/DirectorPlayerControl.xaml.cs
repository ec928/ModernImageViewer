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

    public enum DragHandleType { None, Center, NW, N, NE, E, SE, S, SW, W }

    public sealed partial class DirectorPlayerControl : UserControl
    {
        private bool _isDragging = false;
        private Point _lastPointerPosition;
        private int _dragSlot;
        private DragHandleType _dragHandle = DragHandleType.None;

        public event EventHandler ViewportTransformChanged;
        public Microsoft.UI.Xaml.Media.CompositeTransform ActiveTransform { get; set; }

        public event EventHandler<(int slot, DragHandleType handle, double dx, double dy, bool proportional)> OverlayBoxManipulated;
        public event EventHandler<(int slot, int delta)> OverlayBoxWheel;

        public PlayerInputMode InputMode { get; set; } = PlayerInputMode.Content;

        public DirectorPlayerControl()
        {
            this.InitializeComponent();
        }

        public void UpdateWysiwygHandles(int slot, bool isVisible)
        {
            if (!isVisible || slot == 0)
            {
                ArrangePipHandles.Visibility = Visibility.Collapsed;
                return;
            }
            ArrangePipHandles.Visibility = Visibility.Visible;
            var grid = slot == 1 ? OverlayGrid1 : OverlayGrid2;
            ArrangePipHandles.Margin = grid.Margin;
            ArrangePipHandles.Width = grid.Width;
            ArrangePipHandles.Height = grid.Height;
        }

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

        private DragHandleType HitTestHandles(Point p)
        {
            if (ArrangePipHandles.Visibility != Visibility.Visible) return DragHandleType.None;
            double left = ArrangePipHandles.Margin.Left;
            double top = ArrangePipHandles.Margin.Top;
            double right = left + ArrangePipHandles.Width;
            double bottom = top + ArrangePipHandles.Height;
            double cx = left + ArrangePipHandles.Width / 2;
            double cy = top + ArrangePipHandles.Height / 2;
            double hitRadius = 15;

            bool Hit(double hx, double hy) => Math.Abs(p.X - hx) < hitRadius && Math.Abs(p.Y - hy) < hitRadius;

            if (Hit(left, top)) return DragHandleType.NW;
            if (Hit(right, top)) return DragHandleType.NE;
            if (Hit(left, bottom)) return DragHandleType.SW;
            if (Hit(right, bottom)) return DragHandleType.SE;
            if (Hit(cx, top)) return DragHandleType.N;
            if (Hit(cx, bottom)) return DragHandleType.S;
            if (Hit(right, cy)) return DragHandleType.E;
            if (Hit(left, cy)) return DragHandleType.W;

            return DragHandleType.None;
        }

        private void InputLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(InputLayer).Position;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                _dragHandle = HitTestHandles(p);
                if (_dragHandle != DragHandleType.None)
                {
                    _dragSlot = HitTestOverlaySlot(new Point(ArrangePipHandles.Margin.Left + ArrangePipHandles.Width/2, ArrangePipHandles.Margin.Top + ArrangePipHandles.Height/2));
                    if (_dragSlot == 0) return;
                }
                else
                {
                    _dragSlot = HitTestOverlaySlot(p);
                    if (_dragSlot == 0) 
                    {
                        UpdateWysiwygHandles(0, false);
                        return;
                    }
                    _dragHandle = DragHandleType.Center;
                    UpdateWysiwygHandles(_dragSlot, true);
                }

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

            var pt = e.GetCurrentPoint(InputLayer);
            var p = pt.Position;
            var deltaX = p.X - _lastPointerPosition.X;
            var deltaY = p.Y - _lastPointerPosition.Y;
            _lastPointerPosition = p;

            if (InputMode == PlayerInputMode.ArrangePips)
            {
                if (_dragSlot > 0 && _dragHandle != DragHandleType.None)
                {
                    bool isShiftPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == Windows.System.VirtualKeyModifiers.Shift;
                    // Proportional scaling for corners normally, freeform if shift is pressed. Edges are always freeform.
                    bool proportional = !isShiftPressed && (_dragHandle == DragHandleType.NW || _dragHandle == DragHandleType.NE || _dragHandle == DragHandleType.SW || _dragHandle == DragHandleType.SE);
                    OverlayBoxManipulated?.Invoke(this, (_dragSlot, _dragHandle, deltaX, deltaY, proportional));
                }
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
            _dragHandle = DragHandleType.None;
            InputLayer.ReleasePointerCapture(e.Pointer);
        }

        private void InputLayer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            _dragSlot = 0;
            _dragHandle = DragHandleType.None;
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
        }
    }
}

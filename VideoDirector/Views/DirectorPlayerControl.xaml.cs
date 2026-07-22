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

        // Canvas-arrange mode: the overlay boxes become interactive (move/resize/edit their
        // placement directly on the composite) and the full-screen content InputLayer yields.
        public event EventHandler<int> OverlayBoxSelected;             // slot
        public event EventHandler<(int slot, double dx, double dy)> OverlayBoxMoved;
        public event EventHandler<(int slot, int delta)> OverlayBoxWheel;
        public event EventHandler<int> OverlayBoxEditRequested;        // slot (double-tap)

        private bool _canvasMode;
        public bool CanvasMode
        {
            get => _canvasMode;
            set
            {
                _canvasMode = value;
                // In canvas mode the boxes receive pointer input and the content InputLayer is off.
                InputLayer.IsHitTestVisible = !value;
                OverlayGrid1.IsHitTestVisible = value;
                OverlayGrid2.IsHitTestVisible = value;
            }
        }

        private bool _boxDragging;
        private Point _lastBoxPointer;

        private static int SlotOf(object sender) => sender == null ? 0 : (((FrameworkElement)sender).Name == "OverlayGrid1" ? 1 : 2);

        private void OverlayBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            int slot = SlotOf(sender);
            OverlayBoxSelected?.Invoke(this, slot);
            _boxDragging = true;
            _lastBoxPointer = e.GetCurrentPoint(this).Position;
            ((FrameworkElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OverlayBox_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_boxDragging) return;
            var p = e.GetCurrentPoint(this).Position;
            double dx = p.X - _lastBoxPointer.X;
            double dy = p.Y - _lastBoxPointer.Y;
            _lastBoxPointer = p;
            OverlayBoxMoved?.Invoke(this, (SlotOf(sender), dx, dy));
            e.Handled = true;
        }

        private void OverlayBox_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _boxDragging = false;
            ((FrameworkElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void OverlayBox_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            OverlayBoxWheel?.Invoke(this, (SlotOf(sender), delta));
            e.Handled = true;
        }

        private void OverlayBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            OverlayBoxEditRequested?.Invoke(this, SlotOf(sender));
            e.Handled = true;
        }

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
            if (!_isDragging || ActiveTransform == null) return;

            var currentPosition = e.GetCurrentPoint(InputLayer).Position;
            var deltaX = currentPosition.X - _lastPointerPosition.X;
            var deltaY = currentPosition.Y - _lastPointerPosition.Y;

            ActiveTransform.TranslateX += deltaX;
            ActiveTransform.TranslateY += deltaY;

            _lastPointerPosition = currentPosition;
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
            if (ActiveTransform == null) return;
            
            var properties = e.GetCurrentPoint(InputLayer).Properties;
            var delta = properties.MouseWheelDelta;

            double zoomFactor = delta > 0 ? 1.1 : (1.0 / 1.1);
            double newScale = Math.Clamp(ActiveTransform.ScaleX * zoomFactor, 0.1, 10.0);

            ActiveTransform.ScaleX = newScale;
            ActiveTransform.ScaleY = newScale;
            
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

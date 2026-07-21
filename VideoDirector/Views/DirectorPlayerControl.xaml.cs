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

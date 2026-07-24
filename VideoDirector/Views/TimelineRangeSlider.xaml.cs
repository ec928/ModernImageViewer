using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace ModernImageViewer.VideoDirector.Views
{
    public sealed partial class TimelineRangeSlider : UserControl
    {
        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TimelineRangeSlider), new PropertyMetadata(100.0, OnDependencyPropertyChanged));

        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.Register(nameof(Position), typeof(double), typeof(TimelineRangeSlider), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public static readonly DependencyProperty TrimStartProperty =
            DependencyProperty.Register(nameof(TrimStart), typeof(double), typeof(TimelineRangeSlider), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public static readonly DependencyProperty TrimEndProperty =
            DependencyProperty.Register(nameof(TrimEnd), typeof(double), typeof(TimelineRangeSlider), new PropertyMetadata(100.0, OnDependencyPropertyChanged));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double Position
        {
            get => (double)GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        public double TrimStart
        {
            get => (double)GetValue(TrimStartProperty);
            set => SetValue(TrimStartProperty, value);
        }

        public double TrimEnd
        {
            get => (double)GetValue(TrimEndProperty);
            set => SetValue(TrimEndProperty, value);
        }

        public event EventHandler InteractionStarted;
        public event EventHandler InteractionCompleted;

        public TimelineRangeSlider()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateUI();
        }

        private static void OnDependencyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TimelineRangeSlider slider)
            {
                // A new clip (Maximum changes) shows the WHOLE source: the track spans [0, source],
                // so the right edge is always the source end and dragging can reach any position.
                // Zooming is explicit (scroll) so the scale is never secretly a sub-window.
                if (e.Property == MaximumProperty)
                {
                    slider._viewStart = 0;
                    slider._viewSpan = Math.Max(0.01, slider.Maximum);
                }
                if (!slider._isDragging) slider.UpdateUI();
            }
        }

        // The visible window into [0, Maximum], in value (seconds) units. Zooming shrinks _viewSpan
        // so a drag covers fewer seconds per pixel — that's what makes trimming a long source precise.
        private double _viewStart;
        private double _viewSpan;

        private double Max => Math.Max(0.01, Maximum);

        private void EnsureView()
        {
            if (_viewSpan <= 0 || _viewSpan > Max || _viewStart < 0 || _viewStart + _viewSpan > Max + 0.001)
            {
                _viewStart = 0;
                _viewSpan = Max;
            }
        }

        // value -> 0..1 position within the visible window (clamped so off-window thumbs sit at an edge).
        private double Ratio(double value) => Math.Clamp((value - _viewStart) / _viewSpan, 0, 1);

        // pixel position on the track -> value within the visible window.
        private double PixelToValue(double px, double trackWidth) => _viewStart + Math.Clamp((px - 12) / trackWidth, 0, 1) * _viewSpan;

        // Which handle a drag is moving. Dragging tracks the cursor's ABSOLUTE position within the
        // visible window, so the handle sits exactly under the pointer and is always on screen —
        // it can never move invisibly off-window or "jump". To place a handle outside the current
        // window, zoom out / pan first, then drag.
        private enum DragTarget { None, Start, End, Playhead }
        private DragTarget _drag = DragTarget.None;

        private double ValueAtCursor(PointerRoutedEventArgs e)
        {
            double trackWidth = Math.Max(0.01, RootGrid.ActualWidth - 24);
            return Math.Clamp(PixelToValue(e.GetCurrentPoint(RootGrid).Position.X, trackWidth), 0, Max);
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateUI();
        }

        private bool _isDragging = false;

        private void UpdateUI()
        {
            if (RootGrid.ActualWidth == 0) return;

            EnsureView();

            double width = RootGrid.ActualWidth;
            double trackWidth = Math.Max(0, width - 24);

            double startRatio = Ratio(TrimStart);
            double endRatio = Ratio(TrimEnd);
            double posRatio = Ratio(Position);

            double startX = 12 + startRatio * trackWidth - (StartThumb.ActualWidth / 2);
            double endX = 12 + endRatio * trackWidth - (EndThumb.ActualWidth / 2);
            double posX = 12 + posRatio * trackWidth - (PlayheadThumb.ActualWidth / 2);

            Canvas.SetLeft(StartThumb, startX);
            Canvas.SetLeft(EndThumb, endX);
            Canvas.SetLeft(PlayheadThumb, posX);

            ActiveTrack.Margin = new Thickness(12 + startRatio * trackWidth, 0, 0, 0);
            ActiveTrack.Width = Math.Max(0, (endRatio - startRatio) * trackWidth);
        }

        protected override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            base.OnPointerReleased(e);
            EndDrag();
        }

        protected override void OnPointerCanceled(PointerRoutedEventArgs e)
        {
            base.OnPointerCanceled(e);
            EndDrag();
        }

        private void EndDrag()
        {
            if (_drag == DragTarget.None && !_isDragging) return;
            _drag = DragTarget.None;
            _isDragging = false;
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // A press on a thumb starts a drag that tracks the cursor; a press on the bare track
            // scrubs the playhead there.
            if (e.OriginalSource is FrameworkElement el)
            {
                if (el == StartThumb || el.Parent == StartThumb) _drag = DragTarget.Start;
                else if (el == EndThumb || el.Parent == EndThumb) _drag = DragTarget.End;
                else if (el == PlayheadThumb || el.Parent == PlayheadThumb) _drag = DragTarget.Playhead;
            }

            if (_drag != DragTarget.None)
            {
                _isDragging = true;
                RootGrid.CapturePointer(e.Pointer);
                InteractionStarted?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            Position = ValueAtCursor(e);
            UpdateUI();
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_drag == DragTarget.None) return;
            double v = ValueAtCursor(e);
            switch (_drag)
            {
                case DragTarget.Start:
                    TrimStart = Math.Clamp(v, 0, TrimEnd);
                    Position = TrimStart; // scrub playhead to the in-point while trimming
                    break;
                case DragTarget.End:
                    TrimEnd = Math.Clamp(v, TrimStart, Max);
                    Position = TrimEnd;
                    break;
                case DragTarget.Playhead:
                    Position = v;
                    break;
            }
            UpdateUI();
            e.Handled = true;
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_drag != DragTarget.None)
            {
                RootGrid.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
            EndDrag();
        }

        // Scroll to zoom around the CENTRE of the view (both edges move equally, no sideways slide);
        // Shift+scroll pans the window to reach any region.
        private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            EnsureView();

            int delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
            bool shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

            if (shift)
            {
                // Shift+scroll pans the visible window left/right so you can reach any region.
                double panBy = (delta > 0 ? -1 : 1) * _viewSpan * 0.2;
                _viewStart = Math.Clamp(_viewStart + panBy, 0, Math.Max(0, Max - _viewSpan));
            }
            else
            {
                // Zoom magnifies around the PLAYHEAD, holding it centred — the needle is the point
                // you place and care about, so it stays put while everything magnifies around it and
                // the trim brackets spread apart. (Only this lets you zoom into the start/middle/end
                // of a long clip: a fixed geometric centre could only ever zoom into the middle.)
                double pivot = (Position >= _viewStart && Position <= _viewStart + _viewSpan)
                    ? Position
                    : _viewStart + _viewSpan * 0.5; // playhead off-window: fall back to view centre
                double factor = delta > 0 ? 0.8 : 1.25; // in : out
                double minSpan = Math.Min(0.2, Max);    // allow zooming to sub-second (frame) precision
                double newSpan = Math.Clamp(_viewSpan * factor, minSpan, Max);
                _viewStart = Math.Clamp(pivot - newSpan / 2, 0, Math.Max(0, Max - newSpan));
                _viewSpan = newSpan;
            }

            UpdateUI();
            e.Handled = true;
        }

        // Double-click fits the whole source back into view.
        private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            _viewStart = 0;
            _viewSpan = Max;
            UpdateUI();
        }
    }
}

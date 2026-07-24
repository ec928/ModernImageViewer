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
                // A new clip (Maximum changes) resets the zoom to fit the whole source.
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

        // pixel position on the track -> value, and a pixel delta -> value delta, both in the window.
        private double PixelToValue(double px, double trackWidth) => _viewStart + Math.Clamp((px - 12) / trackWidth, 0, 1) * _viewSpan;
        private double DeltaToValue(double dxPixels, double trackWidth) => (dxPixels / trackWidth) * _viewSpan;

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateUI();
        }

        private bool _isDragging = false;
        private double _dragStartPixel;
        private double _dragStartValue;

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

        private void StartThumb_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _isDragging = true;
            _dragStartValue = TrimStart;
            InteractionStarted?.Invoke(this, EventArgs.Empty);
        }

        private void StartThumb_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            double trackWidth = Math.Max(0.01, RootGrid.ActualWidth - 24);
            double deltaValue = DeltaToValue(e.Cumulative.Translation.X, trackWidth);
            double maxAllowed = Math.Max(0, TrimEnd);
            double newValue = Math.Clamp(_dragStartValue + deltaValue, 0, maxAllowed);
            
            TrimStart = newValue;
            Position = newValue; // Scrub playhead while trimming
            UpdateUI();
        }

        private void StartThumb_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            _isDragging = false;
            UpdateUI();
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void EndThumb_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _isDragging = true;
            _dragStartValue = TrimEnd;
            InteractionStarted?.Invoke(this, EventArgs.Empty);
        }

        private void EndThumb_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            double trackWidth = Math.Max(0.01, RootGrid.ActualWidth - 24);
            double deltaValue = DeltaToValue(e.Cumulative.Translation.X, trackWidth);

            double effectiveMax = Math.Max(0, Maximum);
            double minAllowed = Math.Min(TrimStart, effectiveMax);
            double newValue = Math.Clamp(_dragStartValue + deltaValue, minAllowed, effectiveMax);
            
            TrimEnd = newValue;
            Position = newValue; // Scrub playhead while trimming
            UpdateUI();
        }

        private void EndThumb_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            _isDragging = false;
            UpdateUI();
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void PlayheadThumb_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _isDragging = true;
            _dragStartValue = Position;
            InteractionStarted?.Invoke(this, EventArgs.Empty);
        }

        private void PlayheadThumb_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            double trackWidth = Math.Max(0.01, RootGrid.ActualWidth - 24);
            double deltaValue = DeltaToValue(e.Cumulative.Translation.X, trackWidth);

            double effectiveMax = Math.Max(0, Maximum);
            double newValue = Math.Clamp(_dragStartValue + deltaValue, 0, effectiveMax);
            
            Position = newValue;
            UpdateUI();
        }

        protected override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            base.OnPointerReleased(e);
            _isDragging = false;
        }

        protected override void OnPointerCanceled(PointerRoutedEventArgs e)
        {
            base.OnPointerCanceled(e);
            _isDragging = false;
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Ignore if they clicked on a thumb (thumbs handle their own events)
            if (e.OriginalSource is FrameworkElement el && (el == StartThumb || el == EndThumb || el == PlayheadThumb || el.Parent == StartThumb || el.Parent == EndThumb || el.Parent == PlayheadThumb))
                return;

            var point = e.GetCurrentPoint(RootGrid).Position;
            double trackWidth = Math.Max(0.01, RootGrid.ActualWidth - 24);
            double newValue = Math.Clamp(PixelToValue(point.X, trackWidth), 0, Max);

            Position = newValue;
            UpdateUI();
        }

        // Scroll to zoom. Anchored on the PLAYHEAD (a visible landmark you're working around) so it
        // stays put while the window tightens around it — far less disorienting than anchoring on an
        // unmarked cursor. If the playhead is off-window, keep the current centre instead.
        private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            EnsureView();

            // The playhead is the pivot: keep it dead centre and grow/shrink both sides equally, so
            // zooming never slides the timeline sideways. (If the playhead is off-window, pivot on
            // the current centre instead.)
            bool playheadVisible = Position >= _viewStart && Position <= _viewStart + _viewSpan;
            double pivot = playheadVisible ? Position : _viewStart + _viewSpan * 0.5;

            double factor = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta > 0 ? 0.8 : 1.25; // in : out
            double minSpan = Math.Min(2.0, Max); // can't zoom past ~2s (or the whole clip if shorter)
            double newSpan = Math.Clamp(_viewSpan * factor, minSpan, Max);

            _viewStart = Math.Clamp(pivot - newSpan / 2, 0, Math.Max(0, Max - newSpan));
            _viewSpan = newSpan;
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

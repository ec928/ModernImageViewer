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
            if (d is TimelineRangeSlider slider && !slider._isDragging)
            {
                slider.UpdateUI();
            }
        }

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

            double width = RootGrid.ActualWidth;
            double trackWidth = Math.Max(0, width - 24);

            double max = Math.Max(0.01, Maximum);

            double startRatio = Math.Clamp(TrimStart / max, 0, 1);
            double endRatio = Math.Clamp(TrimEnd / max, 0, 1);
            double posRatio = Math.Clamp(Position / max, 0, 1);

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
            double deltaValue = (e.Cumulative.Translation.X / trackWidth) * Maximum;
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
            double deltaValue = (e.Cumulative.Translation.X / trackWidth) * Maximum;
            
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
            double deltaValue = (e.Cumulative.Translation.X / trackWidth) * Maximum;
            
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
            double deltaX = point.X - 12;
            
            double effectiveMax = Math.Max(0, Maximum);
            double newValue = Math.Clamp((deltaX / trackWidth) * Maximum, 0, effectiveMax);

            Position = newValue;
            UpdateUI();
        }
    }
}

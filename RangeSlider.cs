using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace ModernImageViewer
{
    /// <summary>
    /// A single-row dual-thumb range slider conforming to Fluent WinUI 3 styling.
    /// Supports dragging start/end thumbs, sliding the range window, jumping via track click,
    /// and double-clicking to instantly collapse into a single fixed value.
    /// </summary>
    public sealed class RangeSlider : Grid
    {
        private const double ThumbDiameter = 16.0;
        private const double ThumbRadius = 8.0;
        private const double InnerDotDiameter = 6.0;
        private const double TrackHeight = 4.0;

        private readonly Canvas _canvas;
        private readonly Rectangle _track;
        private readonly Rectangle _rangeSpan;
        private readonly (Grid Container, Ellipse Outer, Ellipse Inner) _startThumb;
        private readonly (Grid Container, Ellipse Outer, Ellipse Inner) _endThumb;

        private double _minimum = 3;
        private double _maximum = 30;
        private double _stepFrequency = 1;
        private double _rangeStart = 5;
        private double _rangeEnd = 12;

        private enum DragTarget { None, Start, End, Span, Coalesced }
        private DragTarget _activeDrag = DragTarget.None;
        private Point _dragStartPt;
        private double _initialStart;
        private double _initialEnd;

        public event EventHandler<(double Start, double End)>? RangeChanged;

        public double Minimum
        {
            get => _minimum;
            set { _minimum = value; UpdateVisuals(); }
        }

        public double Maximum
        {
            get => _maximum;
            set { _maximum = Math.Max(_minimum, value); UpdateVisuals(); }
        }

        public double StepFrequency
        {
            get => _stepFrequency;
            set { _stepFrequency = Math.Max(0.001, value); }
        }

        public double RangeStart
        {
            get => _rangeStart;
            set => SetRange(value, Math.Max(value, _rangeEnd));
        }

        public double RangeEnd
        {
            get => _rangeEnd;
            set => SetRange(Math.Min(_rangeStart, value), value);
        }

        public RangeSlider()
        {
            Height = 32;
            MinHeight = 32;
            Background = new SolidColorBrush(Colors.Transparent); // Full hit-test surface across the row
            IsTabStop = true;

            _canvas = new Canvas
            {
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false
            };

            _track = new Rectangle
            {
                Height = TrackHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
            };

            _rangeSpan = new Rectangle
            {
                Height = TrackHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = GetAccentBrush()
            };

            _startThumb = CreateThumb();
            _endThumb = CreateThumb();

            Canvas.SetZIndex(_track, 0);
            Canvas.SetZIndex(_rangeSpan, 1);
            Canvas.SetZIndex(_startThumb.Container, 2);
            Canvas.SetZIndex(_endThumb.Container, 3);

            _canvas.Children.Add(_track);
            _canvas.Children.Add(_rangeSpan);
            _canvas.Children.Add(_startThumb.Container);
            _canvas.Children.Add(_endThumb.Container);

            Children.Add(_canvas);

            SizeChanged += (_, _) => UpdateVisuals();
            Loaded += (_, _) =>
            {
                RefreshAccentBrushes();
                UpdateVisuals();
            };
            ActualThemeChanged += (_, _) =>
            {
                RefreshAccentBrushes();
                UpdateVisuals();
            };

            DoubleTapped += OnDoubleTapped;
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerCaptureLost += OnPointerCaptureLost;
            KeyDown += OnKeyDown;
        }

        public static readonly Color WindowsBlue = Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
        public static readonly Color WindowsBlueDeep = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

        private static Brush GetAccentBrush() => new SolidColorBrush(WindowsBlue);

        private void RefreshAccentBrushes()
        {
            var accent = GetAccentBrush();
            _rangeSpan.Fill = accent;
            _startThumb.Outer.Fill = accent;
            _endThumb.Outer.Fill = accent;
        }

        private static (Grid Container, Ellipse Outer, Ellipse Inner) CreateThumb()
        {
            var container = new Grid
            {
                Width = ThumbDiameter,
                Height = ThumbDiameter,
                IsHitTestVisible = false
            };
            var outer = new Ellipse
            {
                Width = ThumbDiameter,
                Height = ThumbDiameter,
                Fill = GetAccentBrush(),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
                StrokeThickness = 1.5
            };
            var inner = new Ellipse
            {
                Width = InnerDotDiameter,
                Height = InnerDotDiameter,
                Fill = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.95
            };
            container.Children.Add(outer);
            container.Children.Add(inner);
            return (container, outer, inner);
        }

        public void SetRange(double start, double end)
        {
            start = Math.Clamp(Snap(start), Minimum, Maximum);
            end = Math.Clamp(Snap(end), start, Maximum);
            bool changed = Math.Abs(_rangeStart - start) > 0.0001 || Math.Abs(_rangeEnd - end) > 0.0001;
            _rangeStart = start;
            _rangeEnd = end;
            UpdateVisuals();
            if (changed)
            {
                RangeChanged?.Invoke(this, (_rangeStart, _rangeEnd));
            }
        }

        private double Snap(double value)
        {
            double stepped = Math.Round((value - Minimum) / StepFrequency) * StepFrequency + Minimum;
            return Math.Clamp(stepped, Minimum, Maximum);
        }

        private void UpdateVisuals()
        {
            double width = ActualWidth;
            if (width <= 0) return;

            double trackTop = (ActualHeight - TrackHeight) / 2.0;
            double thumbTop = (ActualHeight - ThumbDiameter) / 2.0;

            _track.Width = width;
            Canvas.SetLeft(_track, 0);
            Canvas.SetTop(_track, trackTop);

            double usableLength = Math.Max(0, width - ThumbDiameter);
            double span = Math.Max(0.001, Maximum - Minimum);

            double tStart = Math.Clamp((RangeStart - Minimum) / span, 0, 1);
            double tEnd = Math.Clamp((RangeEnd - Minimum) / span, 0, 1);

            double xStart = tStart * usableLength;
            double xEnd = tEnd * usableLength;

            Canvas.SetLeft(_startThumb.Container, xStart);
            Canvas.SetTop(_startThumb.Container, thumbTop);

            Canvas.SetLeft(_endThumb.Container, xEnd);
            Canvas.SetTop(_endThumb.Container, thumbTop);

            double barLeft = xStart + ThumbRadius;
            double barWidth = Math.Max(0, xEnd - xStart);

            _rangeSpan.Width = barWidth;
            Canvas.SetLeft(_rangeSpan, barLeft);
            Canvas.SetTop(_rangeSpan, trackTop);
            _rangeSpan.Visibility = barWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var pt = e.GetPosition(this);
            double usableLength = Math.Max(1, ActualWidth - ThumbDiameter);
            double span = Math.Max(0.001, Maximum - Minimum);
            double clickVal = Snap(Minimum + ((pt.X - ThumbRadius) / usableLength) * span);
            clickVal = Math.Clamp(clickVal, Minimum, Maximum);
            SetRange(clickVal, clickVal);
            e.Handled = true;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Focus(FocusState.Programmatic);
            CapturePointer(e.Pointer);

            var pt = e.GetCurrentPoint(this).Position;
            _dragStartPt = pt;
            _initialStart = RangeStart;
            _initialEnd = RangeEnd;

            double usableLength = Math.Max(1, ActualWidth - ThumbDiameter);
            double span = Math.Max(0.001, Maximum - Minimum);
            double startCenterX = ((RangeStart - Minimum) / span) * usableLength + ThumbRadius;
            double endCenterX = ((RangeEnd - Minimum) / span) * usableLength + ThumbRadius;

            bool isCoalesced = Math.Abs(startCenterX - endCenterX) < 1.0;
            double distStart = Math.Abs(pt.X - startCenterX);
            double distEnd = Math.Abs(pt.X - endCenterX);

            if (isCoalesced)
            {
                if (distStart <= 16.0)
                {
                    _activeDrag = DragTarget.Coalesced;
                }
                else if (pt.X < startCenterX)
                {
                    double clickVal = Snap(Minimum + ((pt.X - ThumbRadius) / usableLength) * span);
                    RangeStart = Math.Clamp(clickVal, Minimum, RangeEnd);
                    _initialStart = RangeStart;
                    _activeDrag = DragTarget.Start;
                }
                else
                {
                    double clickVal = Snap(Minimum + ((pt.X - ThumbRadius) / usableLength) * span);
                    RangeEnd = Math.Clamp(clickVal, RangeStart, Maximum);
                    _initialEnd = RangeEnd;
                    _activeDrag = DragTarget.End;
                }
            }
            else
            {
                if (distStart <= 14.0 && distStart <= distEnd)
                {
                    _activeDrag = DragTarget.Start;
                }
                else if (distEnd <= 14.0)
                {
                    _activeDrag = DragTarget.End;
                }
                else if (pt.X > startCenterX && pt.X < endCenterX)
                {
                    _activeDrag = DragTarget.Span;
                }
                else if (pt.X < startCenterX)
                {
                    double clickVal = Snap(Minimum + ((pt.X - ThumbRadius) / usableLength) * span);
                    RangeStart = Math.Clamp(clickVal, Minimum, RangeEnd);
                    _initialStart = RangeStart;
                    _activeDrag = DragTarget.Start;
                }
                else
                {
                    double clickVal = Snap(Minimum + ((pt.X - ThumbRadius) / usableLength) * span);
                    RangeEnd = Math.Clamp(clickVal, RangeStart, Maximum);
                    _initialEnd = RangeEnd;
                    _activeDrag = DragTarget.End;
                }
            }
            HighlightThumbForDrag();
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this).Position;

            if (_activeDrag == DragTarget.None)
            {
                UpdateHoverState(pt);
                return;
            }

            double dx = pt.X - _dragStartPt.X;
            double usableLength = Math.Max(1, ActualWidth - ThumbDiameter);
            double span = Math.Max(0.001, Maximum - Minimum);
            double dVal = (dx / usableLength) * span;

            switch (_activeDrag)
            {
                case DragTarget.Coalesced:
                    if (dx < -3)
                    {
                        _activeDrag = DragTarget.Start;
                        double target = Snap(_initialStart + dVal);
                        RangeStart = Math.Clamp(target, Minimum, RangeEnd);
                    }
                    else if (dx > 3)
                    {
                        _activeDrag = DragTarget.End;
                        double target = Snap(_initialEnd + dVal);
                        RangeEnd = Math.Clamp(target, RangeStart, Maximum);
                    }
                    break;

                case DragTarget.Start:
                    {
                        double target = Snap(_initialStart + dVal);
                        RangeStart = Math.Clamp(target, Minimum, RangeEnd);
                    }
                    break;

                case DragTarget.End:
                    {
                        double target = Snap(_initialEnd + dVal);
                        RangeEnd = Math.Clamp(target, RangeStart, Maximum);
                    }
                    break;

                case DragTarget.Span:
                    {
                        double shift = Math.Round(dVal / StepFrequency) * StepFrequency;
                        double curSpan = _initialEnd - _initialStart;
                        double newStart = Math.Clamp(_initialStart + shift, Minimum, Maximum - curSpan);
                        double newEnd = newStart + curSpan;
                        SetRange(newStart, newEnd);
                    }
                    break;
            }
            HighlightThumbForDrag();
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            ReleasePointerCapture(e.Pointer);
            _activeDrag = DragTarget.None;
            ResetThumbVisuals();
            e.Handled = true;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _activeDrag = DragTarget.None;
            ResetThumbVisuals();
        }

        private void UpdateHoverState(Point pt)
        {
            double usableLength = Math.Max(1, ActualWidth - ThumbDiameter);
            double span = Math.Max(0.001, Maximum - Minimum);
            double startCenterX = ((RangeStart - Minimum) / span) * usableLength + ThumbRadius;
            double endCenterX = ((RangeEnd - Minimum) / span) * usableLength + ThumbRadius;

            bool nearStart = Math.Abs(pt.X - startCenterX) <= 12.0;
            bool nearEnd = Math.Abs(pt.X - endCenterX) <= 12.0;

            var defaultStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
            var hoverStroke = new SolidColorBrush(Colors.White);

            _startThumb.Outer.Stroke = nearStart ? hoverStroke : defaultStroke;
            _endThumb.Outer.Stroke = nearEnd ? hoverStroke : defaultStroke;
        }

        private void HighlightThumbForDrag()
        {
            var activeStroke = new SolidColorBrush(Colors.White);
            var defaultStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));

            _startThumb.Outer.Stroke = (_activeDrag is DragTarget.Start or DragTarget.Span or DragTarget.Coalesced) ? activeStroke : defaultStroke;
            _endThumb.Outer.Stroke = (_activeDrag is DragTarget.End or DragTarget.Span or DragTarget.Coalesced) ? activeStroke : defaultStroke;
        }

        private void ResetThumbVisuals()
        {
            var defaultStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
            _startThumb.Outer.Stroke = defaultStroke;
            _endThumb.Outer.Stroke = defaultStroke;
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            double step = StepFrequency;
            if (e.Key is VirtualKey.Left or VirtualKey.Down)
            {
                if (RangeEnd > RangeStart)
                {
                    RangeEnd = Math.Clamp(RangeEnd - step, RangeStart, Maximum);
                }
                else
                {
                    SetRange(Math.Clamp(RangeStart - step, Minimum, Maximum), Math.Clamp(RangeEnd - step, Minimum, Maximum));
                }
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Right or VirtualKey.Up)
            {
                if (RangeEnd < Maximum)
                {
                    RangeEnd = Math.Clamp(RangeEnd + step, RangeStart, Maximum);
                }
                else if (RangeStart < RangeEnd)
                {
                    RangeStart = Math.Clamp(RangeStart + step, Minimum, RangeEnd);
                }
                e.Handled = true;
            }
        }
    }
}

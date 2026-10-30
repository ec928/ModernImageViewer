using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.System;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl : UserControl
    {
        public event EventHandler? CloseRequested;
        public event EventHandler? ChangePhotosRequested;

        // When set, this many frames stay on screen and later photos wait off screen.
        private int _framedSlots;

        /// <summary>The window should become this width/height ratio. Dragging the window is the custom shape.</summary>
        public event Action<double>? CanvasAspectChosen;

        private readonly List<ImageItem> _items = new();
        private readonly List<CompositeTransform> _cellTransforms = new();
        private readonly List<Action> _layoutUpdaters = new();
        private readonly List<UIElement> _cellTopControls = new();
        private bool _isFillMode = true; // Default to Fill for zero wasted space

        private readonly DispatcherTimer _hudTimer;
        private bool _isHudVisible = true;
        private bool _isPointerOverHud = false;

        private int _soloIndex = -1;
        private int _lines = 1;
        private int[]? _lineCounts;
        private ScrollViewer? _countsScroll;
        private double _countsScrollOffset;
        private double _countsScrollAcross;
        private readonly List<LayoutLineSlot> _lineSlots = new();
        private TextBlock? _layoutSubtitle;
        private RepeatButton? _lineLess;
        private RepeatButton? _lineMore;
        private Button? _evenButton;
        private bool _openLayoutIsRows;
        private FrameworkElement? _columnChartHost;
        private double _columnChartWidth;

        private sealed class LayoutLineSlot
        {
            public Border Strip = null!;
            public TextBlock Value = null!;
            public RepeatButton Less = null!;
            public RepeatButton More = null!;
            public string Name = "";
            public bool Vertical;
        }
        private bool _flipped;
        private bool _heroActive;
        private HeroKind _heroKind = HeroKind.Banner;
        private int _heroPosition;

        // Solo drag tracking. A click on the black around the photo is not a drag.
        private bool _isSoloDragging = false;
        private bool _soloBackgroundClick = false;
        private Point _lastSoloPoint = default;
        private Point _soloBackgroundStart = default;
        private uint _soloPointerId = 0;

        public MultiViewControl()
        {
            this.InitializeComponent();
            ActualThemeChanged += (_, _) => ApplyGridLayoutTheme();

            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _hudTimer.Tick += HudTimer_Tick;

            RootGrid.PointerMoved += SegmentGesture_PointerMoved;
            RootGrid.PointerReleased += SegmentGesture_PointerReleased;
            RootGrid.PointerCaptureLost += SegmentGesture_PointerReleased;
            SizeChanged += (_, _) => RefreshCanvasLabel();

            SetupSoloInteractions();
        }

        public bool IsSoloActive => SoloContainer.Visibility == Visibility.Visible;

        /// <summary>Hold keyboard focus on the grid itself, so the first photo's caption button does not draw a focus box.</summary>
        public void FocusView()
        {
            if (!RootGrid.Focus(FocusState.Programmatic))
                Focus(FocusState.Programmatic);
        }

        public void LoadImages(IEnumerable<ImageItem> items)
        {
            _items.Clear();
            _items.AddRange(items.Where(i => i != null && !i.IsDirectory));
            _framedSlots = 0;

            _isFillMode = true; // Always default to Fill (crop to fill) to eliminate black bars
            _lines = MosaicLayout.DefaultLines(_items.Count);
            _lineCounts = null;
            _flipped = false;
            _heroActive = false;
            _heroKind = HeroKind.Banner;
            _heroPosition = 0;

            ExitSolo();
            BuildLayout();
            UpdateFillFitButton();
            ShowHudAndOverlays();
        }

        public void Clear()
        {
            _hudTimer.Stop();
            _items.Clear();
            _cellTransforms.Clear();
            _layoutUpdaters.Clear();
            _cellTopControls.Clear();
            ResetMosaicFields();
            CellsGrid.Children.Clear();
            CellsGrid.RowDefinitions.Clear();
            CellsGrid.ColumnDefinitions.Clear();
            SoloImage.Source = null;
            _soloIndex = -1;
        }

        /// <summary>More or fewer rows or columns. Counts return to an even split, and a rectangle hero turns off.</summary>
        public void ChangeLines(int delta)
        {
            int total = LineTotal();
            if (VisibleCount(_items.Count) < 2 || total < 1) return;
            int current = Math.Clamp(_lines, 1, total);
            int next = Math.Clamp(current + delta, 1, total);
            bool linesInUse = !_heroActive || HeroUsesLines;
            if (next == current && linesInUse && !_mosaicCustom) return;
            _lines = next;
            _lineCounts = null;
            if (!HeroUsesLines) _heroActive = false;
            RelayoutKeepingCells();
            SyncOpenLayout();
            ShowHudAndOverlays();
        }

        /// <summary>A banner or pair keeps the line controls for the other photos. Rectangle does not.</summary>
        private bool HeroUsesLines => _heroActive && _heroKind != HeroKind.Rectangle;

        /// <summary>Photos the line controls arrange: all of them, or the ones beside a banner or pair.</summary>
        private int LineTotal()
        {
            int shown = VisibleCount(_items.Count);
            if (HeroUsesLines) return Math.Max(1, shown - MosaicLayout.HeroFeatured(_heroKind));
            return shown;
        }

        private int EffectiveLines() => Math.Clamp(_lines, 1, Math.Max(1, LineTotal()));

        /// <summary>Photos in each line. Even until the user changes one line.</summary>
        private int[] CurrentLineCounts()
        {
            int total = LineTotal();
            int lines = EffectiveLines();
            if (_lineCounts != null && _lineCounts.Length == lines && _lineCounts.Sum() == total && _lineCounts.All(n => n >= 1))
                return _lineCounts;
            return MosaicLayout.LineCounts(total, lines);
        }

        private void AdjustLine(int line, int delta)
        {
            var next = MosaicLayout.MovePhoto(CurrentLineCounts(), line, delta);
            if (next == null) return;
            _lineCounts = next;
            if (!HeroUsesLines) _heroActive = false;
            RelayoutKeepingCells();
            SyncOpenLayout();
            ShowHudAndOverlays();
        }

        private void CountsFlyout_Opening(object sender, object e)
        {
            ApplyGridLayoutTheme();
            FillCountsPanel();
        }

        private void CountsFlyout_Opened(object sender, object e)
        {
            HudMenu_Opened(sender, e);
            HoldFlyoutSource(CountsButton, true);
            DispatcherQueue.TryEnqueue(ApplyGridLayoutTheme);
        }

        private void CountsFlyout_Closed(object sender, object e)
        {
            HoldFlyoutSource(CountsButton, false);
            HudMenu_Closed(sender, e);
        }

        /// <summary>
        /// WinUI drops a button back to Normal once its flyout is up. Hold a pressed
        /// plate until the card closes. The platform wash is a few percent of white
        /// and does not read on this glass.
        /// </summary>
        private static void HoldFlyoutSource(Button? button, bool open)
        {
            if (button == null) return;
            if (!open)
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
                button.ClearValue(Control.BorderThicknessProperty);
                return;
            }

            button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            button.BorderThickness = new Thickness(1);
        }

        /// <summary>
        /// The photo canvas forces Dark, and this card lives under that grid, so an
        /// unset theme comes out dark. The card follows the window instead.
        /// </summary>
        private void ApplyGridLayoutTheme()
        {
            // Premium HUD: The photo canvas and floating controls are an immersive dark glass surface.
            // Pin the layout inspector to Dark to prevent glare over media and match the playbar.
            ElementTheme theme = ElementTheme.Dark;
            if (CountsCard != null)
                CountsCard.RequestedTheme = theme;
            if (CountsPanel != null)
                CountsPanel.RequestedTheme = theme;

            FlyoutPresenter? presenter = FindFlyoutPresenter(CountsCard);
            if (presenter != null)
                presenter.RequestedTheme = theme;
        }

        private ElementTheme WindowTheme()
        {
            DependencyObject? node = VisualTreeHelper.GetParent(this);
            while (node != null)
            {
                if (node is FrameworkElement el && el.RequestedTheme != ElementTheme.Default)
                    return el.RequestedTheme;
                node = VisualTreeHelper.GetParent(node);
            }

            return Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }

        private static FlyoutPresenter? FindFlyoutPresenter(DependencyObject? start)
        {
            DependencyObject? node = start;
            while (node != null)
            {
                if (node is FlyoutPresenter presenter) return presenter;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private bool IsLayoutFlyoutOpen => CountsButton?.Flyout?.IsOpen == true;

        /// <summary>
        /// The steppers are RepeatButtons. Rebuilding the card on a click destroys the
        /// button under the pointer, so a hold never repeats. Keep that button and
        /// update the open card in place. A new line count replaces only the diagram.
        /// </summary>
        private void SyncOpenLayout()
        {
            if (!IsLayoutFlyoutOpen)
                return;
            bool rows = !_flipped;
            if (_layoutSubtitle == null || _countsScroll == null || _lineLess == null || rows != _openLayoutIsRows)
            {
                FillCountsPanel();
                return;
            }

            var counts = CurrentLineCounts();
            int lines = EffectiveLines();
            bool isEven = counts.SequenceEqual(MosaicLayout.LineCounts(LineTotal(), lines));
            _layoutSubtitle.Text = LayoutSubtitle(lines, rows);
            SyncEvenButton(isEven);
            SyncLineCountStepper(lines, rows);
            if (_lineSlots.Count != counts.Length)
                ReplaceDiagram(counts, rows);
            else
                UpdateLineSlots(counts);
        }

        private void RefreshOpenLayout()
        {
            if (IsLayoutFlyoutOpen)
                FillCountsPanel();
        }

        private void ReplaceDiagram(int[] counts, bool rows)
        {
            if (_countsScroll == null)
            {
                FillCountsPanel();
                return;
            }
            double across = _countsScroll.HorizontalOffset;
            double down = _countsScroll.VerticalOffset;
            _countsScroll.Content = rows ? BuildRowDiagram(counts) : BuildColumnDiagram(counts);
            _countsScroll.UpdateLayout();
            CenterColumnChart();
            _countsScroll.ChangeView(across, down, null, true);
        }

        private void UpdateLineSlots(int[] counts)
        {
            for (int i = 0; i < counts.Length; i++)
            {
                var slot = _lineSlots[i];
                FillStrip(slot.Strip, counts[i], slot.Vertical);
                slot.Value.Text = counts[i].ToString();
                AutomationProperties.SetName(slot.Value, counts[i] + " photos in " + slot.Name.ToLowerInvariant());
                string spoken = slot.Name.ToLowerInvariant();
                bool canLess = MosaicLayout.MovePhoto(counts, i, -1) != null;
                bool canMore = MosaicLayout.MovePhoto(counts, i, 1) != null;
                ApplyStepState(slot.Less, canLess, canLess ? "One fewer in " + spoken : slot.Name + " already has one photo");
                ApplyStepState(slot.More, canMore, canMore ? "One more in " + spoken : "No photo left to move");
            }
        }

        private void SyncEvenButton(bool isEven)
        {
            if (_evenButton == null) return;
            _evenButton.IsEnabled = !isEven;
            ToolTipService.SetToolTip(_evenButton, isEven
                ? "The lines already have the same number of photos."
                : "Give every line the same number of photos.");
        }

        private void SyncLineCountStepper(int lines, bool rows)
        {
            if (_lineLess == null || _lineMore == null) return;
            int total = LineTotal();
            string noun = rows ? "rows" : "columns";
            bool canLess = lines > 1;
            bool canMore = lines < total;
            ApplyStepState(_lineLess, canLess, canLess ? "Fewer " + noun : "Minimum reached");
            ApplyStepState(_lineMore, canMore, canMore ? "More " + noun : "Maximum reached");
        }

        private static void ApplyStepState(RepeatButton button, bool enabled, string tip)
        {
            if (button.IsEnabled != enabled)
                button.IsEnabled = enabled;
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetName(button, tip);
        }

        private Style LayoutStyle(string key) => (Style)Resources[key];

        private void FillCountsPanel()
        {
            if (_countsScroll != null)
            {
                _countsScrollOffset = _countsScroll.VerticalOffset;
                _countsScrollAcross = _countsScroll.HorizontalOffset;
            }

            CountsPanel.Children.Clear();
            ApplyGridLayoutTheme();

            var counts = CurrentLineCounts();
            bool rows = !_flipped;
            int lines = EffectiveLines();
            bool isEven = counts.SequenceEqual(MosaicLayout.LineCounts(LineTotal(), lines));

            CountsPanel.Children.Add(BuildLayoutHeader(lines, rows, isEven));
            CountsPanel.Children.Add(BuildLayoutToolbar(lines, rows));
            CountsPanel.Children.Add(new Rectangle
            {
                Style = LayoutStyle("LayoutDividerStyle"),
                Margin = new Thickness(0, 8, 0, 2)
            });
            CountsPanel.Children.Add(BuildLayoutLines(counts, rows));
        }

        private string LayoutSubtitle(int lines, bool rows)
        {
            int selected = _items.Count;
            int shown = VisibleCount(selected);
            int arranged = LineTotal();
            string noun = rows
                ? (lines == 1 ? "row" : "rows")
                : (lines == 1 ? "column" : "columns");
            string across = $"{arranged} {(arranged == 1 ? "photo" : "photos")} across {lines} {noun}";

            if (_heroActive && !HeroUsesLines)
                return "Editing the lines turns the hero off";

            if (_heroActive && HeroUsesLines)
            {
                string kind = MosaicLayout.HeroTitle(_heroKind).ToLowerInvariant();
                string beside = across + ", beside the " + kind;
                if (selected > shown)
                    beside += ". " + shown + " of " + selected + " shown";
                return beside;
            }

            if (selected > shown)
                return $"{shown} of {selected} photos across {lines} {noun}";
            return across;
        }

        private FrameworkElement BuildLayoutHeader(int lines, bool rows, bool isEven)
        {
            var header = new Grid();
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = "Grid layout",
                Style = LayoutStyle("LayoutTitleStyle"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var even = new Button
            {
                Style = LayoutStyle("LayoutEvenButtonStyle"),
                Content = "Even out"
            };
            _evenButton = even;
            SyncEvenButton(isEven);
            even.Click += (_, _) =>
            {
                _lineCounts = null;
                RelayoutKeepingCells();
                SyncOpenLayout();
                ShowHudAndOverlays();
            };
            Grid.SetColumn(even, 1);
            header.Children.Add(even);

            var subtitle = new TextBlock
            {
                Text = LayoutSubtitle(lines, rows),
                Style = LayoutStyle("LayoutSubtitleStyle"),
                Margin = new Thickness(0, 2, 0, 0)
            };
            _layoutSubtitle = subtitle;
            Grid.SetRow(subtitle, 1);
            Grid.SetColumnSpan(subtitle, 2);
            header.Children.Add(subtitle);
            return header;
        }

        private FrameworkElement BuildLayoutToolbar(int lines, bool rows)
        {
            int total = LineTotal();
            bool canLess = lines > 1;
            bool canMore = lines < total;
            string noun = rows ? "rows" : "columns";
            var host = new Border
            {
                Style = LayoutStyle("LayoutChoiceHostStyle"),
                Margin = new Thickness(0, 10, 0, 2)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var less = MakeStepperButton("\uE738", canLess ? "Fewer " + noun : "Minimum reached", canLess, () => ChangeLines(-1), new CornerRadius(4));
            var more = MakeStepperButton("\uE710", canMore ? "More " + noun : "Maximum reached", canMore, () => ChangeLines(1), new CornerRadius(4));
            Grid.SetColumn(less, 0);
            Grid.SetColumn(more, 4);
            grid.Children.Add(less);
            grid.Children.Add(more);

            var leftRule = StepperRule(vertical: true);
            var rightRule = StepperRule(vertical: true);
            leftRule.Margin = new Thickness(0, 8, 0, 8);
            rightRule.Margin = new Thickness(0, 8, 0, 8);
            Grid.SetColumn(leftRule, 1);
            Grid.SetColumn(rightRule, 3);
            grid.Children.Add(leftRule);
            grid.Children.Add(rightRule);

            var choice = BuildOrientationChoice(rows);
            Grid.SetColumn(choice, 2);
            grid.Children.Add(choice);

            _lineLess = less;
            _lineMore = more;
            host.Child = grid;
            return host;
        }

        /// <summary>
        /// The Rows | Columns switch, inset between the steppers. The selected half is its own
        /// rounded box. Minus and plus stay outside this track so they do not eat that box.
        /// </summary>
        private FrameworkElement BuildOrientationChoice(bool rows)
        {
            var track = new Border { Style = LayoutStyle("LayoutChoiceTrackStyle") };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var selected = new Border { Style = LayoutStyle("LayoutChoiceSelectedStyle") };
            Grid.SetColumn(selected, rows ? 0 : 1);
            grid.Children.Add(selected);

            grid.Children.Add(MakeChoiceButton("Rows", "\uEA37", rows, 0, () =>
            {
                if (!rows)
                    ToggleRowsColumns();
            }));
            grid.Children.Add(MakeChoiceButton("Columns", "\uE8C0", !rows, 1, () =>
            {
                if (rows)
                    ToggleRowsColumns();
            }));

            track.Child = grid;
            return track;
        }

        /// <summary>
        /// One strip per line, cut into that many photos. Rows are full-width bands.
        /// Columns are full-height stacks, left to right, matching the grid.
        /// </summary>
        private Border MakeCellStrip(int count, bool vertical)
        {
            var strip = new Border { Style = LayoutStyle("LayoutStripStyle") };
            FillStrip(strip, count, vertical);
            return strip;
        }

        private void FillStrip(Border strip, int count, bool vertical)
        {
            count = Math.Max(count, 1);
            if (strip.Child is not Grid layers)
            {
                layers = new Grid();
                strip.Child = layers;
            }

            Grid cells;
            if (layers.Children.Count == 0 || layers.Children[0] is not Grid existing)
            {
                cells = new Grid();
                layers.Children.Insert(0, cells);
            }
            else
            {
                cells = existing;
                cells.Children.Clear();
                cells.RowDefinitions.Clear();
                cells.ColumnDefinitions.Clear();
            }

            for (int i = 0; i < count; i++)
            {
                if (vertical)
                    cells.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                else
                    cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var cell = new Border
                {
                    Style = LayoutStyle("LayoutCellStyle"),
                    Margin = vertical
                        ? new Thickness(0, 0, 0, i < count - 1 ? 1 : 0)
                        : new Thickness(0, 0, i < count - 1 ? 1 : 0, 0)
                };
                if (vertical)
                    Grid.SetRow(cell, i);
                else
                    Grid.SetColumn(cell, i);
                cells.Children.Add(cell);
            }
        }

        private (Border Well, RepeatButton Less, RepeatButton More) MakeLineStepper(int line, string name, bool vertical)
        {
            bool canLess = MosaicLayout.MovePhoto(CurrentLineCounts(), line, -1) != null;
            bool canMore = MosaicLayout.MovePhoto(CurrentLineCounts(), line, 1) != null;
            string spoken = name.ToLowerInvariant();
            return MakeStepperWell(
                canLess ? $"One fewer in {spoken}" : $"{name} already has one photo",
                canMore ? $"One more in {spoken}" : "No photo left to move",
                canLess,
                canMore,
                () => AdjustLine(line, -1),
                () => AdjustLine(line, 1),
                vertical);
        }

        /// <summary>
        /// The photo count sits on the bar, only as tall as the digits, so the
        /// segment gaps stay visible above and below it.
        /// </summary>
        private (Grid Host, Border Strip, TextBlock Badge) MakeStripHost(int count, bool vertical, string name)
        {
            var strip = MakeCellStrip(count, vertical);
            strip.Height = vertical ? 96 : 30;
            var badge = new TextBlock
            {
                Text = count.ToString(),
                Style = LayoutStyle("LayoutCountChipTextStyle")
            };
            AutomationProperties.SetName(badge, count + " photos in " + name.ToLowerInvariant());
            var chip = new Border
            {
                Style = LayoutStyle("LayoutCountChipStyle"),
                Child = badge,
                Padding = vertical ? new Thickness(2, 3, 2, 3) : new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var layers = (Grid)strip.Child;
            layers.Children.Add(chip);
            layers.SizeChanged += (_, e) =>
            {
                layers.Clip = new RectangleGeometry
                {
                    Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
                };
            };
            var host = new Grid { Height = strip.Height };
            host.Children.Add(strip);
            return (host, strip, badge);
        }

        private (Border Well, RepeatButton Less, RepeatButton More) MakeStepperWell(
            string lessTip,
            string moreTip,
            bool canLess,
            bool canMore,
            Action less,
            Action more,
            bool vertical)
        {
            // Segoe Fluent Icons Remove and Add. A text minus stays a hairline at this size.
            var lessButton = MakeStepperButton("\uE738", lessTip, canLess, less, vertical ? new CornerRadius(0, 0, 4, 4) : new CornerRadius(4, 0, 0, 4));
            var moreButton = MakeStepperButton("\uE710", moreTip, canMore, more, vertical ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(0, 4, 4, 0));
            var grid = new Grid();
            if (vertical)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(moreButton, 0);
                Grid.SetRow(lessButton, 2);
                var topRule = StepperRule(vertical: false);
                Grid.SetRow(topRule, 1);
                grid.Children.Add(moreButton);
                grid.Children.Add(topRule);
                grid.Children.Add(lessButton);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(lessButton, 0);
                Grid.SetColumn(moreButton, 2);
                var leftRule = StepperRule(vertical: true);
                Grid.SetColumn(leftRule, 1);
                grid.Children.Add(lessButton);
                grid.Children.Add(leftRule);
                grid.Children.Add(moreButton);
            }

            var well = new Border
            {
                Style = LayoutStyle("LayoutStepperWellStyle"),
                HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                Child = grid
            };
            return (well, lessButton, moreButton);
        }

        private Rectangle StepperRule(bool vertical)
        {
            return new Rectangle
            {
                Style = LayoutStyle("LayoutStepperDividerStyle"),
                Width = vertical ? 1 : double.NaN,
                Height = vertical ? double.NaN : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
        }

        private RepeatButton MakeStepperButton(string glyph, string tip, bool enabled, Action onClick, CornerRadius corner)
        {
            var button = new RepeatButton
            {
                Style = LayoutStyle("LayoutStepperButtonStyle"),
                Content = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                Delay = 400,
                Interval = 80,
                CornerRadius = corner
            };
            ApplyStepState(button, enabled, tip);
            button.Click += (_, _) => onClick();
            return button;
        }

        private void RememberLine(Border strip, (Border Well, RepeatButton Less, RepeatButton More) steps, TextBlock badge, string name, bool vertical)
        {
            _lineSlots.Add(new LayoutLineSlot
            {
                Strip = strip,
                Value = badge,
                Less = steps.Less,
                More = steps.More,
                Name = name,
                Vertical = vertical
            });
        }

        private FrameworkElement BuildLayoutLines(int[] counts, bool rows)
        {
            FrameworkElement chart = rows ? BuildRowDiagram(counts) : BuildColumnDiagram(counts);
            var scroll = new ScrollViewer
            {
                Content = chart,
                MaxHeight = rows ? 576 : 280,
                MaxWidth = rows ? double.PositiveInfinity : 600,
                VerticalScrollBarVisibility = rows ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = rows ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto
            };
            _countsScroll = scroll;
            if (!rows)
            {
                scroll.SizeChanged += (_, _) => CenterColumnChart();
                scroll.Loaded += (_, _) => CenterColumnChart();
            }
            if (_countsScrollOffset > 0 || _countsScrollAcross > 0)
            {
                scroll.Loaded += (_, _) => scroll.ChangeView(_countsScrollAcross, _countsScrollOffset, null, true);
            }
            return scroll;
        }

        private FrameworkElement BuildRowDiagram(int[] counts)
        {
            _lineSlots.Clear();
            _openLayoutIsRows = true;
            _columnChartHost = null;
            var panel = new StackPanel();
            for (int i = 0; i < counts.Length; i++)
            {
                int line = i;
                string name = $"Row {i + 1}";
                var row = new Grid { Height = 36 };
                // Two digits, held against the bar. The word stays in the spoken name.
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Style = LayoutStyle("LayoutLineLabelStyle")
                };
                AutomationProperties.SetName(label, name);
                ToolTipService.SetToolTip(label, name);
                row.Children.Add(label);

                var visual = MakeStripHost(counts[i], vertical: false, name);
                visual.Host.Margin = new Thickness(0, 0, 8, 0);
                visual.Host.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(visual.Host, 1);
                row.Children.Add(visual.Host);

                var steps = MakeLineStepper(line, name, vertical: false);
                RememberLine(visual.Strip, steps, visual.Badge, name, vertical: false);
                Grid.SetColumn(steps.Well, 2);
                row.Children.Add(steps.Well);
                panel.Children.Add(row);
            }
            return panel;
        }

        private FrameworkElement BuildColumnDiagram(int[] counts)
        {
            _lineSlots.Clear();
            _openLayoutIsRows = false;
            int n = Math.Max(counts.Length, 1);
            int slot = n >= 12 ? 34 : n >= 8 ? 42 : 52;
            int gap = n >= 12 ? 2 : 4;
            _columnChartWidth = counts.Length * slot + Math.Max(0, counts.Length - 1) * gap;
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = gap,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            for (int i = 0; i < counts.Length; i++)
            {
                int line = i;
                string name = $"Column {i + 1}";
                var column = new StackPanel { Width = slot };
                AutomationProperties.SetName(column, name);
                ToolTipService.SetToolTip(column, name);
                column.Children.Add(new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Style = LayoutStyle("LayoutColumnLabelStyle")
                });

                var visual = MakeStripHost(counts[i], vertical: true, name);
                visual.Host.Margin = new Thickness(0, 2, 0, 4);
                column.Children.Add(visual.Host);
                var steps = MakeLineStepper(line, name, vertical: true);
                RememberLine(visual.Strip, steps, visual.Badge, name, vertical: true);
                column.Children.Add(steps.Well);
                panel.Children.Add(column);
            }
            var host = new Grid();
            host.Children.Add(panel);
            _columnChartHost = host;
            return host;
        }

        /// <summary>
        /// The column chart is only as wide as its stacks. Stretch a host to the
        /// card and center the chart in it. A chart wider than the card scrolls.
        /// </summary>
        private void CenterColumnChart()
        {
            if (_countsScroll == null || _columnChartHost == null) return;
            double view = _countsScroll.ViewportWidth;
            if (view <= 0) return;
            bool fits = _columnChartWidth <= view;
            _countsScroll.HorizontalScrollBarVisibility = fits
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
            double target = fits ? view : 0;
            if (Math.Abs(_columnChartHost.MinWidth - target) > 0.5)
                _columnChartHost.MinWidth = target;
        }

        private Button MakeChoiceButton(string label, string glyph, bool selected, int column, Action onClick)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(new FontIcon
            {
                Glyph = glyph,
                Style = LayoutStyle(selected ? "LayoutChoiceIconSelectedStyle" : "LayoutChoiceIconStyle")
            });
            content.Children.Add(new TextBlock
            {
                Text = label,
                Style = LayoutStyle(selected ? "LayoutChoiceTextSelectedStyle" : "LayoutChoiceTextStyle")
            });
            var button = new Button
            {
                Style = LayoutStyle("LayoutChoiceButtonStyle"),
                Content = content
            };
            Grid.SetColumn(button, column);
            button.Click += (_, _) => onClick();
            return button;
        }

        /// <summary>
        /// Rows become columns and back. A plain grid or a Rectangle turns a quarter, dragged sizes kept.
        /// Beside a banner or pair only the other photos turn, so their sizes go back to even like
        /// any other line change.
        /// </summary>
        public void ToggleRowsColumns()
        {
            if (VisibleCount(_items.Count) < 2) return;
            _flipped = !_flipped;
            if (!HeroUsesLines && _mosaic != null && CellRootsReady())
            {
                _mosaic = MosaicLayout.Transpose(_mosaic);
                _keepMosaic = true;
                try { BuildLayout(); }
                finally { _keepMosaic = false; }
            }
            else
            {
                RelayoutKeepingCells();
            }
            RefreshOpenLayout();
            ShowHudAndOverlays();
        }

        /// <summary>A new layout for the same photos. The cells move into it; no photo decodes again.</summary>
        private void RelayoutKeepingCells()
        {
            if (!CellRootsReady())
            {
                BuildLayout();
                return;
            }
            _relayoutKeepingCells = true;
            try { BuildLayout(); }
            finally { _relayoutKeepingCells = false; }
        }

        private void HeroButton_Click(object sender, RoutedEventArgs e)
        {
            var kinds = MosaicLayout.HeroKinds(VisibleCount(_items.Count));
            if (kinds.Length == 0) return;
            if (!_heroActive)
            {
                _heroActive = true;
                _heroKind = kinds[0];
                _heroPosition = _heroKind == HeroKind.Banner
                    ? MosaicLayout.DefaultBannerPosition(CellsGrid.ActualWidth, CellsGrid.ActualHeight)
                    : 0;
            }
            else
            {
                int index = Array.IndexOf(kinds, _heroKind);
                if (index < 0) index = 0;
                int nextIndex = index + 1;
                if (nextIndex >= kinds.Length)
                {
                    _heroActive = false;
                }
                else
                {
                    _heroKind = kinds[nextIndex];
                    _heroPosition = 0;
                }
            }
            RelayoutKeepingCells();
            RefreshOpenLayout();
            ShowHudAndOverlays();
        }

        private void RotateHero_Click(object sender, RoutedEventArgs e)
        {
            if (!_heroActive) return;
            int positions = MosaicLayout.HeroPositions(VisibleCount(_items.Count), _heroKind);
            _heroPosition = (_heroPosition + 1) % positions;
            RelayoutKeepingCells();
            ShowHudAndOverlays();
        }

        private void NormalizeHero()
        {
            var kinds = MosaicLayout.HeroKinds(VisibleCount(_items.Count));
            if (kinds.Length == 0)
            {
                _heroActive = false;
                return;
            }
            if (Array.IndexOf(kinds, _heroKind) < 0)
            {
                _heroKind = kinds[0];
                _heroPosition = 0;
            }
            else
            {
                int positions = MosaicLayout.HeroPositions(VisibleCount(_items.Count), _heroKind);
                if (_heroPosition >= positions) _heroPosition = 0;
            }
        }

        private void RefreshHeroChrome(int count)
        {
            if (HeroButton == null || RotateHeroButton == null || HeroText == null) return;
            bool show = count > 1;
            HeroButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            RotateHeroButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            RotateHeroButton.IsEnabled = _heroActive;
            string kind = MosaicLayout.HeroTitle(_heroKind);
            HeroText.Text = _heroActive ? "Hero: " + kind : "Hero";

            if (_heroActive)
            {
                HeroButton.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                    ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215));
                HeroText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                HeroText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                if (HeroIcon != null)
                {
                    HeroIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                    HeroIcon.Opacity = 1.0;
                }
            }
            else
            {
                HeroButton.ClearValue(Button.BackgroundProperty);
                HeroText.ClearValue(TextBlock.ForegroundProperty);
                HeroText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                if (HeroIcon != null)
                {
                    HeroIcon.ClearValue(FontIcon.ForegroundProperty);
                    HeroIcon.Opacity = 0.85;
                }
            }

            ToolTipService.SetToolTip(HeroButton, _heroActive
                ? "Hero: " + kind + ". Click for the next kind, or off (H)"
                : "Hero (H). Click for a large photo.");
            ToolTipService.SetToolTip(RotateHeroButton, _heroActive
                ? "Next position"
                : "Turn the hero on first");
        }

        public bool HandleKeyDown(VirtualKey key)
        {
            ShowHudAndOverlays();

            if (IsSoloActive)
            {
                switch (key)
                {
                    case VirtualKey.Escape:
                    case VirtualKey.Enter:
                        ExitSolo();
                        return true;
                    case VirtualKey.Left:
                        PrevSolo();
                        return true;
                    case VirtualKey.Right:
                        NextSolo();
                        return true;
                }
                return false;
            }

            switch (key)
            {
                case VirtualKey.Escape:
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    return true;
                case VirtualKey.Add:
                case (VirtualKey)187: // = / +
                    ChangeLines(1);
                    return true;
                case VirtualKey.Subtract:
                case (VirtualKey)189: // - / _
                    ChangeLines(-1);
                    return true;
                case VirtualKey.F:
                    ToggleFillMode();
                    return true;
                case VirtualKey.R:
                    ToggleRowsColumns();
                    return true;
                case VirtualKey.H:
                    HeroButton_Click(this, new RoutedEventArgs());
                    return true;
                case VirtualKey.S:
                    if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    {
                        SaveButton?.Flyout?.ShowAt(SaveButton);
                        return true;
                    }
                    break;
            }

            return false;
        }

        #region HUD & Auto-Hide

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ShowHudAndOverlays();
        }

        private void Hud_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOverHud = true;
            ShowHudAndOverlays();
        }

        private void Hud_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOverHud = false;
            _hudTimer.Stop();
            _hudTimer.Start();
        }

        private void ShowHudAndOverlays()
        {
            _hudTimer.Stop();
            _hudTimer.Start();

            if (!_isHudVisible)
            {
                _isHudVisible = true;
                UnifiedHud.Opacity = 1.0;
                UnifiedHud.IsHitTestVisible = true;
                HudTranslate.Y = 0;
                if (HudScrim != null) HudScrim.Opacity = 1.0;

                SoloInfoOverlay.Opacity = 1.0;
                SoloBottomBar.Opacity = 1.0;
                SoloBottomBar.IsHitTestVisible = true;
                SoloTopRightControls.Opacity = 1.0;
                SoloTopRightControls.IsHitTestVisible = true;
            }
        }

        private int _hudMenusOpen;

        private void HudMenu_Opened(object? sender, object e)
        {
            _hudMenusOpen++;
            ShowHudAndOverlays();
        }

        private void HudMenu_Closed(object? sender, object e)
        {
            _hudMenusOpen = Math.Max(0, _hudMenusOpen - 1);
            ShowHudAndOverlays();
        }

        private void HideHudAndOverlays()
        {
            if (_isPointerOverHud || _hudMenusOpen > 0) return;

            _hudTimer.Stop();
            _isHudVisible = false;

            UnifiedHud.Opacity = 0.0;
            UnifiedHud.IsHitTestVisible = false;
            HudTranslate.Y = 15;
            if (HudScrim != null) HudScrim.Opacity = 0.0;

            SoloInfoOverlay.Opacity = 0.0;
            SoloBottomBar.Opacity = 0.0;
            SoloBottomBar.IsHitTestVisible = false;
            SoloTopRightControls.Opacity = 0.0;
            SoloTopRightControls.IsHitTestVisible = false;

            foreach (var ctrl in _cellTopControls)
            {
                ctrl.Opacity = 0.0;
                ctrl.IsHitTestVisible = false;
            }
        }

        private void HudTimer_Tick(object? sender, object e)
        {
            HideHudAndOverlays();
        }

        #endregion

        #region Layout & Cell Construction

        private void AddCell(ImageItem item, int row, int col, int rowSpan, int colSpan, int index)
        {
            AddCell(CellsGrid, item, row, col, rowSpan, colSpan, index);
        }

        private void AddCell(Grid targetGrid, ImageItem item, int row, int col, int rowSpan, int colSpan, int index)
        {
            var cell = CreateCell(item, index);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            if (rowSpan > 1) Grid.SetRowSpan(cell, rowSpan);
            if (colSpan > 1) Grid.SetColumnSpan(cell, colSpan);
            targetGrid.Children.Add(cell);
            if ((uint)index < (uint)_cellRoots.Length)
                _cellRoots[index] = cell;
        }

        private FrameworkElement CreateCell(ImageItem item, int index)
        {
            var cellBorder = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 10, 10)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 28, 28)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0)
            };

            var cellGrid = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)), // Hit-testable
                ManipulationMode = ManipulationModes.None
            };

            var transform = new CompositeTransform();
            _cellTransforms.Add(transform);

            var imgContainer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform,
                IsHitTestVisible = false // Ensure all pointer events go directly to cellGrid
            };

            double naturalWidth = 16.0;
            double naturalHeight = 9.0;

            if (item.Thumbnail is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
            {
                naturalWidth = bs.PixelWidth;
                naturalHeight = bs.PixelHeight;
            }

            Image? thumb = null;
            if (item.Thumbnail != null)
            {
                thumb = new Image
                {
                    Source = item.Thumbnail,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                imgContainer.Children.Add(thumb);
            }

            var fullImg = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            // Layout & Clamping helpers for this cell
            void ClampTranslation()
            {
                double cellW = cellGrid.ActualWidth;
                double cellH = cellGrid.ActualHeight;
                if (cellW <= 0 || cellH <= 0 || imgContainer.Width <= 0 || imgContainer.Height <= 0) return;

                double effW = imgContainer.Width * transform.ScaleX;
                double effH = imgContainer.Height * transform.ScaleY;

                if (effW > cellW)
                {
                    double maxPanX = (effW - cellW) / 2.0;
                    transform.TranslateX = Math.Clamp(transform.TranslateX, -maxPanX, maxPanX);
                }
                else
                {
                    transform.TranslateX = 0;
                }

                if (effH > cellH)
                {
                    double maxPanY = (effH - cellH) / 2.0;
                    transform.TranslateY = Math.Clamp(transform.TranslateY, -maxPanY, maxPanY);
                }
                else
                {
                    transform.TranslateY = 0;
                }
            }

            void UpdateImageLayout()
            {
                double cellW = cellGrid.ActualWidth;
                double cellH = cellGrid.ActualHeight;
                if (cellW <= 0 || cellH <= 0 || naturalWidth <= 0 || naturalHeight <= 0) return;

                cellGrid.Clip = new RectangleGeometry { Rect = new Rect(0, 0, cellW, cellH) };

                double imgAspect = naturalWidth / naturalHeight;
                double cellAspect = cellW / cellH;

                double targetW, targetH;

                if (_isFillMode)
                {
                    // Crop to Fill: cover entire cell with zero black bars
                    if (imgAspect > cellAspect)
                    {
                        // Image is wider than cell -> fill cell height, let width expand proportionally
                        targetH = cellH;
                        targetW = cellH * imgAspect;
                    }
                    else
                    {
                        // Image is taller than cell -> fill cell width, let height expand proportionally
                        targetW = cellW;
                        targetH = cellW / imgAspect;
                    }
                }
                else
                {
                    // Fit: fit whole image inside cell
                    if (imgAspect > cellAspect)
                    {
                        targetW = cellW;
                        targetH = cellW / imgAspect;
                    }
                    else
                    {
                        targetH = cellH;
                        targetW = cellH * imgAspect;
                    }
                }

                imgContainer.Width = targetW;
                imgContainer.Height = targetH;
                ClampTranslation();
            }

            Action updateLayout = UpdateImageLayout;
            _layoutUpdaters.Add(updateLayout);

            cellGrid.SizeChanged += (s, e) =>
            {
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    UpdateImageLayout();
                }
            };

            var photo = new CellPhoto(item);
            void LoadPhoto()
            {
                var current = photo.Item;
                if (current.Thumbnail is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
                {
                    naturalWidth = bs.PixelWidth;
                    naturalHeight = bs.PixelHeight;
                    UpdateImageLayout();
                }
                try
                {
                    var bmp = new BitmapImage();
                    bmp.UriSource = new Uri(current.Path, UriKind.Absolute);
                    bmp.ImageOpened += (_, _) =>
                    {
                        if (!ReferenceEquals(photo.Item, current)) return;
                        if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                        {
                            naturalWidth = bmp.PixelWidth;
                            naturalHeight = bmp.PixelHeight;
                            UpdateImageLayout();
                        }
                        // The thumbnail would show through a transparent photo, and is no longer needed.
                        if (thumb != null && imgContainer.Children.Contains(thumb))
                        {
                            imgContainer.Children.Remove(thumb);
                            thumb = null;
                        }
                    };
                    fullImg.Source = bmp;
                }
                catch { }
            }
            photo.Reload = LoadPhoto;
            LoadPhoto();

            imgContainer.Children.Add(fullImg);
            cellGrid.Children.Add(imgContainer);

            // Per-cell Pan & Zoom handling
            bool isDragging = false;
            Point lastPoint = default;
            uint dragPointerId = 0;

            cellGrid.PointerPressed += (s, e) =>
            {
                ShowHudAndOverlays();
                var prop = e.GetCurrentPoint(cellGrid).Properties;

                if (prop.IsMiddleButtonPressed)
                {
                    // Middle-click: Reset zoom & pan
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                    ClampTranslation();
                    e.Handled = true;
                    return;
                }

                if (prop.IsLeftButtonPressed)
                {
                    bool isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (isAlt)
                    {
                        e.Handled = true;
                        _ = StartSwapDragAsync(cellGrid, e.GetCurrentPoint(cellGrid), Slot(photo.Item));
                        return;
                    }
                    isDragging = true;

                    lastPoint = e.GetCurrentPoint(cellGrid).Position;
                    dragPointerId = e.Pointer.PointerId;
                    cellGrid.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            cellGrid.PointerMoved += (s, e) =>
            {
                ShowHudAndOverlays();

                if (isDragging && e.Pointer.PointerId == dragPointerId)
                {
                    var currentPoint = e.GetCurrentPoint(cellGrid).Position;
                    double dx = currentPoint.X - lastPoint.X;
                    double dy = currentPoint.Y - lastPoint.Y;

                    transform.TranslateX += dx;
                    transform.TranslateY += dy;
                    ClampTranslation();

                    lastPoint = currentPoint;
                    e.Handled = true;
                }
            };

            cellGrid.PointerReleased += (s, e) =>
            {
                if (e.Pointer.PointerId == dragPointerId)
                {
                    if (isDragging)
                    {
                        isDragging = false;
                        cellGrid.ReleasePointerCapture(e.Pointer);
                        e.Handled = true;
                    }
                }
            };

            cellGrid.PointerCanceled += (s, e) =>
            {
                if (isDragging)
                {
                    isDragging = false;
                    cellGrid.ReleasePointerCapture(e.Pointer);
                }
            };

            cellGrid.PointerWheelChanged += (s, e) =>
            {
                ShowHudAndOverlays();
                var ptr = e.GetCurrentPoint(cellGrid);
                int delta = ptr.Properties.MouseWheelDelta;
                double zoomFactor = delta > 0 ? 1.15 : (1.0 / 1.15);

                double cellW = cellGrid.ActualWidth;
                double cellH = cellGrid.ActualHeight;
                double baseW = imgContainer.Width;
                double baseH = imgContainer.Height;

                // Scale factor where whole picture fits into cell (unlock zoom out in fill mode!)
                double fitScale = 1.0;
                if (baseW > 0 && baseH > 0 && cellW > 0 && cellH > 0)
                {
                    fitScale = Math.Min(cellW / baseW, cellH / baseH);
                }
                double minScale = Math.Min(fitScale * 0.8, 0.15);
                double maxScale = 10.0;

                double oldScale = transform.ScaleX;
                double newScale = Math.Clamp(oldScale * zoomFactor, minScale, maxScale);

                Point pos = ptr.Position;
                double cx = cellW / 2.0;
                double cy = cellH / 2.0;

                // Cursor-anchored zoom with RenderTransformOrigin = (0.5, 0.5)
                transform.TranslateX = (pos.X - cx) - (pos.X - cx - transform.TranslateX) * (newScale / oldScale);
                transform.TranslateY = (pos.Y - cy) - (pos.Y - cy - transform.TranslateY) * (newScale / oldScale);
                transform.ScaleX = newScale;
                transform.ScaleY = newScale;

                ClampTranslation();
                e.Handled = true;
            };

            cellGrid.DoubleTapped += (s, e) =>
            {
                ShowHudAndOverlays();
                e.Handled = true;

                if (Math.Abs(transform.ScaleX - 1.0) > 0.05 || Math.Abs(transform.TranslateX) > 1 || Math.Abs(transform.TranslateY) > 1)
                {
                    // Reset to default fill/fit
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                }
                else
                {
                    // Zoom to 2.0x centered at click position
                    Point pos = e.GetPosition(cellGrid);
                    double cx = cellGrid.ActualWidth / 2.0;
                    double cy = cellGrid.ActualHeight / 2.0;

                    double oldScale = transform.ScaleX;
                    double newScale = 2.0;
                    transform.TranslateX = (pos.X - cx) - (pos.X - cx - transform.TranslateX) * (newScale / oldScale);
                    transform.TranslateY = (pos.Y - cy) - (pos.Y - cy - transform.TranslateY) * (newScale / oldScale);
                    transform.ScaleX = newScale;
                    transform.ScaleY = newScale;
                }

                ClampTranslation();
            };

            // Top-Right Auto-Hiding Caption Controls (Matches app's window caption style: Maximize and Close)
            var topControls = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0),
                Opacity = 0.0,
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(topControls, 60);
            topControls.PointerEntered += Hud_PointerEntered;
            topControls.PointerExited += Hud_PointerExited;
            _cellTopControls.Add(topControls);

            bool isCellHovered = false;
            void SetCellHover(bool hovered)
            {
                isCellHovered = hovered;
                bool show = _isHudVisible && isCellHovered;
                topControls.Opacity = show ? 1.0 : 0.0;
                topControls.IsHitTestVisible = show;
            }
            cellGrid.PointerEntered += (s, e) => SetCellHover(true);
            cellGrid.PointerExited += (s, e) => SetCellHover(false);
            cellGrid.PointerMoved += (s, e) =>
            {
                if (_isHudVisible && !topControls.IsHitTestVisible)
                    SetCellHover(true);
            };

            var topStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };

            Button? moveLeftBtn = null;
            Button? moveRightBtn = null;
            if (_items.Count > 1)
            {
                moveLeftBtn = CreateNudgeButton("\uE76B",
                    () => (Slot(photo.Item) == 0 ? "Move to last position" : "Move to previous position") + " (or Alt+drag onto another photo to swap)",
                    () => MovePhoto(Slot(photo.Item), -1));
                moveRightBtn = CreateNudgeButton("\uE76C",
                    () => (Slot(photo.Item) == VisibleCount(_items.Count) - 1 ? "Move to first position" : "Move to next position") + " (or Alt+drag onto another photo to swap)",
                    () => MovePhoto(Slot(photo.Item), 1));
                topStack.Children.Add(moveLeftBtn);
                topStack.Children.Add(moveRightBtn);
            }

            void UpdateTopControlsDensity()
            {
                bool compact = cellGrid.ActualHeight < 80 || cellGrid.ActualWidth < 150;
                if (moveLeftBtn != null) moveLeftBtn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (moveRightBtn != null) moveRightBtn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
            cellGrid.SizeChanged += (s, e) => UpdateTopControlsDensity();

            // Maximize / Solo Button (Matches app's TopMaxButton style)
            var maxBtn = new Button
            {
                Width = 46,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                FocusVisualPrimaryThickness = new Thickness(0),
                FocusVisualSecondaryThickness = new Thickness(0),
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE922", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)) }
            };
            maxBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255));
            maxBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(16, 255, 255, 255));
            maxBtn.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            maxBtn.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255));
            ToolTipService.SetToolTip(maxBtn, "Solo / Maximize");
            maxBtn.Click += (s, e) => EnterSolo(Slot(photo.Item));
            topStack.Children.Add(maxBtn);

            // Close / Remove Button (Matches app's TopCloseButton style with red hover)
            var closeBtn = new Button
            {
                Width = 46,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                FocusVisualPrimaryThickness = new Thickness(0),
                FocusVisualSecondaryThickness = new Thickness(0),
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE8BB", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)) }
            };
            closeBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
            closeBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 153, 27, 16));
            closeBtn.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            closeBtn.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            ToolTipService.SetToolTip(closeBtn, "Remove Photo");
            closeBtn.Click += (s, e) => RemoveItem(photo.Item);
            topStack.Children.Add(closeBtn);

            topControls.Children.Add(topStack);
            cellGrid.Children.Add(topControls);

            // Top-Left Position Badge (for right-click Swap / Move target indication)
            var positionBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10),
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Opacity = 0.0,
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(positionBadge, 70);

            var badgeText = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            positionBadge.Child = badgeText;
            cellGrid.Children.Add(positionBadge);

            // Right-Click Context Menu, filled when it opens so it names the photo's current place
            var menu = new MenuFlyout();
            FillCellMenu(menu, photo.Item);
            menu.Opening += (_, _) => FillCellMenu(menu, photo.Item);
            menu.Opened += (_, _) => ShowPositionBadges(Slot(photo.Item));
            menu.Closed += (_, _) => HidePositionBadges();

            cellGrid.ContextFlyout = menu;

            AttachSwapDrop(cellBorder, cellGrid, photo);
            cellBorder.Tag = new CellParts(transform, updateLayout, topControls, fullImg, photo, positionBadge, badgeText);

            cellBorder.Child = cellGrid;
            return cellBorder;
        }

        private Button CreateNudgeButton(string glyph, Func<string> tip, Action move)
        {
            var btn = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                FocusVisualPrimaryThickness = new Thickness(0),
                FocusVisualSecondaryThickness = new Thickness(0),
                Content = new FontIcon
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Glyph = glyph,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255))
                }
            };
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(16, 255, 255, 255));
            btn.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            btn.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255));
            var toolTip = new ToolTip { Content = tip() };
            toolTip.Opened += (_, _) => toolTip.Content = tip();
            ToolTipService.SetToolTip(btn, toolTip);
            btn.Click += (s, e) => move();
            return btn;
        }

        private int Slot(ImageItem item) => _items.IndexOf(item);

        private void FillCellMenu(MenuFlyout menu, ImageItem item)
        {
            menu.Items.Clear();
            int index = Slot(item);
            if (index < 0) return;

            if (_items.Count > 1)
            {
                int visible = VisibleCount(_items.Count);
                int curSlot = Slot(item);

                var moveLeftItem = new MenuFlyoutItem { Text = "Move Left / Up", Icon = new FontIcon { Glyph = "\uE76B" } };
                moveLeftItem.Click += (s, e) => MovePhoto(Slot(item), -1);
                if (visible > 1)
                {
                    int prevSlot = ((curSlot - 1) % visible + visible) % visible;
                    moveLeftItem.PointerEntered += (s, e) => HighlightPositionBadge(prevSlot);
                    moveLeftItem.PointerExited += (s, e) => ClearHighlightPositionBadge(prevSlot, curSlot);
                }
                menu.Items.Add(moveLeftItem);

                var moveRightItem = new MenuFlyoutItem { Text = "Move Right / Down", Icon = new FontIcon { Glyph = "\uE76C" } };
                moveRightItem.Click += (s, e) => MovePhoto(Slot(item), 1);
                if (visible > 1)
                {
                    int nextSlot = ((curSlot + 1) % visible + visible) % visible;
                    moveRightItem.PointerEntered += (s, e) => HighlightPositionBadge(nextSlot);
                    moveRightItem.PointerExited += (s, e) => ClearHighlightPositionBadge(nextSlot, curSlot);
                }
                menu.Items.Add(moveRightItem);

                if (_items.Count == 2)
                {
                    var swapItem = new MenuFlyoutItem { Text = "Swap with Other Photo", Icon = new FontIcon { Glyph = "\uE8AB" } };
                    int otherSlot = 1 - curSlot;
                    swapItem.Click += (s, e) => SwapItems(Slot(item), otherSlot);
                    swapItem.PointerEntered += (s, e) => HighlightPositionBadge(otherSlot);
                    swapItem.PointerExited += (s, e) => ClearHighlightPositionBadge(otherSlot, curSlot);
                    menu.Items.Add(swapItem);
                }
                else
                {
                    var swapSub = new MenuFlyoutSubItem { Text = "Swap with...", Icon = new FontIcon { Glyph = "\uE8AB" } };
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i == index) continue;
                        var target = _items[i];
                        int targetSlot = i;
                        var targetItem = new MenuFlyoutItem { Text = $"Position {i + 1} ({target.Name})" };
                        targetItem.Click += (s, e) => SwapItems(Slot(item), Slot(target));
                        if (targetSlot < visible)
                        {
                            targetItem.PointerEntered += (s, e) => HighlightPositionBadge(targetSlot);
                            targetItem.PointerExited += (s, e) => ClearHighlightPositionBadge(targetSlot, curSlot);
                        }
                        swapSub.Items.Add(targetItem);
                    }
                    menu.Items.Add(swapSub);
                }

                menu.Items.Add(new MenuFlyoutItem { Text = "Tip: Alt+drag a photo onto another to swap", IsEnabled = false });
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var soloItem = new MenuFlyoutItem { Text = "Solo / Maximize", Icon = new FontIcon { Glyph = "\uE922" } };
            soloItem.Click += (s, e) => EnterSolo(Slot(item));
            menu.Items.Add(soloItem);

            var removeItem = new MenuFlyoutItem { Text = "Remove from View", Icon = new FontIcon { Glyph = "\uE8BB" } };
            removeItem.Click += (s, e) => RemoveItem(item);
            menu.Items.Add(removeItem);
        }

        /// <summary>Swap this photo with the neighbor in the layout order, wrapping around the photos on screen.</summary>
        private void MovePhoto(int index, int delta)
        {
            int visible = VisibleCount(_items.Count);
            if (visible < 2 || (uint)index >= (uint)visible) return;
            int target = index + delta;
            target = ((target % visible) + visible) % visible;
            SwapItems(index, target);
        }

        /// <summary>What a cell registered in the shared lists, so removing the cell can take it back out.</summary>
        private sealed class CellPhoto
        {
            public ImageItem Item;
            public Action? Reload;
            public CellPhoto(ImageItem item) => Item = item;
        }

        private sealed record CellParts(CompositeTransform Transform, Action UpdateLayout, UIElement TopControls, Image Photo, CellPhoto Bound, Border PositionBadge, TextBlock BadgeText);

        /// <summary>
        /// Remove a photo and keep the other cells, so none of them decodes again. The layout is rebuilt
        /// for the new count, and a photo past the limit that moves into view gets a new cell.
        /// </summary>
        private void RemoveItem(ImageItem item)
        {
            int index = _items.IndexOf(item);
            if (index < 0) return;
            int visibleBefore = VisibleCount(_items.Count);
            bool cellsReady = CellRootsReady();
            _items.RemoveAt(index);
            if (_items.Count == 0)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (cellsReady && index >= visibleBefore && _mosaic != null)
            {
                // A photo that was not on screen: only the count changes.
                _keepMosaic = true;
                try { BuildLayout(); }
                finally { _keepMosaic = false; }
            }
            else if (cellsReady && _items.Count > 1)
            {
                var roots = new List<FrameworkElement?>(_cellRoots);
                ForgetCell(roots[index]);
                roots.RemoveAt(index);
                int visible = VisibleCount(_items.Count);
                while (roots.Count < visible) roots.Add(null);
                _cellRoots = roots.ToArray();
                _relayoutKeepingCells = true;
                try { BuildLayout(); }
                finally { _relayoutKeepingCells = false; }
            }
            else
            {
                BuildLayout();
            }
            ShowHudAndOverlays();
        }

        private void ForgetCell(FrameworkElement? cell)
        {
            if (cell == null) return;
            if (cell.Tag is CellParts parts)
            {
                _cellTransforms.Remove(parts.Transform);
                _layoutUpdaters.Remove(parts.UpdateLayout);
                _cellTopControls.Remove(parts.TopControls);
            }
            if (cell.Parent is Panel parent) parent.Children.Remove(cell);
        }

        #endregion


        #region Position Badges for Swap / Move Indication

        private Windows.UI.Color GetMultiViewAccentColor()
        {
            Windows.UI.Color accent = Windows.UI.Color.FromArgb(255, 0, 120, 212);
            try
            {
                if (Application.Current.Resources["SystemAccentColor"] is Windows.UI.Color c) accent = c;
                else if (Application.Current.Resources["SystemAccentColorLight2"] is Windows.UI.Color c2) accent = c2;
            }
            catch { }
            return accent;
        }

        private void ShowPositionBadges(int sourceIndex)
        {
            int visible = VisibleCount(_items.Count);
            var accent = GetMultiViewAccentColor();

            for (int i = 0; i < visible; i++)
            {
                if (i < _cellRoots.Length && _cellRoots[i]?.Tag is CellParts parts)
                {
                    bool isSource = (i == sourceIndex);
                    bool compact = (_cellRoots[i] is FrameworkElement fe && fe.ActualWidth < 140);

                    if (isSource)
                    {
                        parts.BadgeText.Text = compact ? $"#{i + 1} (This)" : $"Position {i + 1} (This Photo)";
                        parts.PositionBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 36, 36, 36));
                        parts.PositionBadge.BorderBrush = new SolidColorBrush(accent);
                        parts.PositionBadge.BorderThickness = new Thickness(1.5);
                    }
                    else
                    {
                        parts.BadgeText.Text = compact ? $"#{i + 1}" : $"Position {i + 1}";
                        parts.PositionBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 20, 20, 20));
                        parts.PositionBadge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 255, 255, 255));
                        parts.PositionBadge.BorderThickness = new Thickness(1);
                    }
                    parts.PositionBadge.Opacity = 1.0;
                }
            }
        }

        private void HighlightPositionBadge(int targetIndex)
        {
            int visible = VisibleCount(_items.Count);
            if (targetIndex < 0 || targetIndex >= visible || targetIndex >= _cellRoots.Length) return;
            if (_cellRoots[targetIndex]?.Tag is CellParts parts)
            {
                var accent = GetMultiViewAccentColor();
                bool compact = (_cellRoots[targetIndex] is FrameworkElement fe && fe.ActualWidth < 140);
                parts.BadgeText.Text = compact ? $"➜ #{targetIndex + 1}" : $"➜ Swap with Position {targetIndex + 1}";
                parts.PositionBadge.Background = new SolidColorBrush(accent);
                parts.PositionBadge.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                parts.PositionBadge.BorderThickness = new Thickness(2);
                parts.PositionBadge.Opacity = 1.0;
            }
        }

        private void ClearHighlightPositionBadge(int targetIndex, int sourceIndex)
        {
            int visible = VisibleCount(_items.Count);
            if (targetIndex < 0 || targetIndex >= visible || targetIndex >= _cellRoots.Length) return;
            if (_cellRoots[targetIndex]?.Tag is CellParts parts)
            {
                bool isSource = (targetIndex == sourceIndex);
                bool compact = (_cellRoots[targetIndex] is FrameworkElement fe && fe.ActualWidth < 140);
                if (isSource)
                {
                    var accent = GetMultiViewAccentColor();
                    parts.BadgeText.Text = compact ? $"#{targetIndex + 1} (This)" : $"Position {targetIndex + 1} (This Photo)";
                    parts.PositionBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 36, 36, 36));
                    parts.PositionBadge.BorderBrush = new SolidColorBrush(accent);
                    parts.PositionBadge.BorderThickness = new Thickness(1.5);
                }
                else
                {
                    parts.BadgeText.Text = compact ? $"#{targetIndex + 1}" : $"Position {targetIndex + 1}";
                    parts.PositionBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 20, 20, 20));
                    parts.PositionBadge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 255, 255, 255));
                    parts.PositionBadge.BorderThickness = new Thickness(1);
                }
            }
        }

        private void HidePositionBadges()
        {
            int visible = VisibleCount(_items.Count);
            for (int i = 0; i < visible; i++)
            {
                if (i < _cellRoots.Length && _cellRoots[i]?.Tag is CellParts parts)
                {
                    parts.PositionBadge.Opacity = 0.0;
                }
            }
        }

        #endregion

        #region Solo View Mode

        private void SetupSoloInteractions()
        {
            SoloImageContainer.PointerPressed += (s, e) =>
            {
                ShowHudAndOverlays();
                var prop = e.GetCurrentPoint(SoloImageContainer).Properties;

                if (prop.IsMiddleButtonPressed)
                {
                    ResetSoloZoom();
                    e.Handled = true;
                    return;
                }

                if (prop.IsLeftButtonPressed)
                {
                    var point = e.GetCurrentPoint(SoloImageContainer).Position;
                    _soloPointerId = e.Pointer.PointerId;
                    _lastSoloPoint = point;
                    if (IsSoloBackground(point))
                    {
                        _soloBackgroundClick = true;
                        _soloBackgroundStart = point;
                    }
                    else
                    {
                        _isSoloDragging = true;
                    }
                    SoloImageContainer.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            SoloImageContainer.PointerMoved += (s, e) =>
            {
                ShowHudAndOverlays();
                if (e.Pointer.PointerId != _soloPointerId) return;

                var currentPoint = e.GetCurrentPoint(SoloImageContainer).Position;
                if (_soloBackgroundClick)
                {
                    double dx = currentPoint.X - _soloBackgroundStart.X;
                    double dy = currentPoint.Y - _soloBackgroundStart.Y;
                    if (dx * dx + dy * dy > 15 * 15)
                        _soloBackgroundClick = false;
                    e.Handled = true;
                    return;
                }

                if (_isSoloDragging)
                {
                    double dx = currentPoint.X - _lastSoloPoint.X;
                    double dy = currentPoint.Y - _lastSoloPoint.Y;
                    SoloTransform.TranslateX += dx;
                    SoloTransform.TranslateY += dy;
                    _lastSoloPoint = currentPoint;
                    e.Handled = true;
                }
            };

            SoloImageContainer.PointerReleased += (s, e) =>
            {
                if (e.Pointer.PointerId != _soloPointerId) return;
                bool close = _soloBackgroundClick;
                _soloBackgroundClick = false;
                if (_isSoloDragging)
                {
                    _isSoloDragging = false;
                }
                try { SoloImageContainer.ReleasePointerCapture(e.Pointer); }
                catch { }
                e.Handled = true;
                if (close)
                    ExitSolo();
            };

            SoloImageContainer.PointerCanceled += (s, e) =>
            {
                _soloBackgroundClick = false;
                if (_isSoloDragging || e.Pointer.PointerId == _soloPointerId)
                {
                    _isSoloDragging = false;
                    try { SoloImageContainer.ReleasePointerCapture(e.Pointer); }
                    catch { }
                }
            };

            SoloImageContainer.PointerWheelChanged += (s, e) =>
            {
                ShowHudAndOverlays();
                var ptr = e.GetCurrentPoint(SoloImageContainer);
                int delta = ptr.Properties.MouseWheelDelta;
                double zoomFactor = delta > 0 ? 1.15 : (1.0 / 1.15);

                double oldScale = SoloTransform.ScaleX;
                double newScale = Math.Clamp(oldScale * zoomFactor, 1.0, 10.0);

                if (newScale <= 1.02)
                {
                    ResetSoloZoom();
                }
                else
                {
                    Point pos = ptr.Position;
                    SoloTransform.TranslateX = pos.X - (pos.X - SoloTransform.TranslateX) * (newScale / oldScale);
                    SoloTransform.TranslateY = pos.Y - (pos.Y - SoloTransform.TranslateY) * (newScale / oldScale);
                    SoloTransform.ScaleX = newScale;
                    SoloTransform.ScaleY = newScale;
                }

                e.Handled = true;
            };

            SoloImageContainer.DoubleTapped += (s, e) =>
            {
                ShowHudAndOverlays();
                e.Handled = true;
                if (IsSoloBackground(e.GetPosition(SoloImageContainer)))
                {
                    ExitSolo();
                    return;
                }

                if (SoloTransform.ScaleX > 1.05)
                {
                    ResetSoloZoom();
                }
                else
                {
                    Point pos = e.GetPosition(SoloImageContainer);
                    double newScale = 2.5;
                    double oldScale = SoloTransform.ScaleX;
                    SoloTransform.TranslateX = pos.X - (pos.X - SoloTransform.TranslateX) * (newScale / oldScale);
                    SoloTransform.TranslateY = pos.Y - (pos.Y - SoloTransform.TranslateY) * (newScale / oldScale);
                    SoloTransform.ScaleX = newScale;
                    SoloTransform.ScaleY = newScale;
                }
            };
        }

        private bool IsSoloBackground(Point point)
        {
            double viewW = SoloImageContainer.ActualWidth;
            double viewH = SoloImageContainer.ActualHeight;
            if (viewW <= 1 || viewH <= 1) return false;

            // The photo element is only as large as the picture. The black around it
            // is this container, so a point outside the element's box is the margin.
            if (SoloImage.ActualWidth > 1 && SoloImage.ActualHeight > 1
                && (SoloImage.ActualWidth < viewW - 2 || SoloImage.ActualHeight < viewH - 2))
            {
                try
                {
                    var bounds = SoloImage.TransformToVisual(SoloImageContainer)
                        .TransformBounds(new Rect(0, 0, SoloImage.ActualWidth, SoloImage.ActualHeight));
                    return point.X < bounds.X || point.X > bounds.Right || point.Y < bounds.Y || point.Y > bounds.Bottom;
                }
                catch
                {
                    return false;
                }
            }

            if (SoloImage.Source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                return false;

            double fit = Math.Min(viewW / bitmap.PixelWidth, viewH / bitmap.PixelHeight);
            double displayedW = bitmap.PixelWidth * fit * SoloTransform.ScaleX;
            double displayedH = bitmap.PixelHeight * fit * SoloTransform.ScaleY;
            return ViewerMath.IsPointerOverLetterbox(
                point.X - SoloTransform.TranslateX,
                point.Y - SoloTransform.TranslateY,
                viewW,
                viewH,
                displayedW,
                displayedH);
        }

        private void EnterSolo(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            _soloIndex = index;
            var item = _items[index];

            SoloTitleText.Text = item.Name;
            SoloSecondaryText.Text = !string.IsNullOrEmpty(item.SizeString) ? item.SizeString : item.DateText;
            SoloCounterText.Text = $"{index + 1} of {_items.Count}";

            try
            {
                SoloImage.Source = new BitmapImage(new Uri(item.Path, UriKind.Absolute));
            }
            catch { }

            ResetSoloZoom();
            SoloContainer.Visibility = Visibility.Visible;
            UnifiedHud.Visibility = Visibility.Collapsed;
            ShowHudAndOverlays();
        }

        private void ExitSolo()
        {
            SoloContainer.Visibility = Visibility.Collapsed;
            UnifiedHud.Visibility = Visibility.Visible;
            SoloImage.Source = null;
            _soloIndex = -1;
            ResetSoloZoom();
            ShowHudAndOverlays();
        }

        private void ResetSoloZoom()
        {
            SoloTransform.ScaleX = 1.0;
            SoloTransform.ScaleY = 1.0;
            SoloTransform.TranslateX = 0;
            SoloTransform.TranslateY = 0;
        }

        private void PrevSolo()
        {
            if (_items.Count == 0) return;
            int prev = _soloIndex - 1;
            if (prev < 0) prev = _items.Count - 1;
            EnterSolo(prev);
        }

        private void NextSolo()
        {
            if (_items.Count == 0) return;
            int next = _soloIndex + 1;
            if (next >= _items.Count) next = 0;
            EnterSolo(next);
        }

        private void ExitSolo_Click(object sender, RoutedEventArgs e) => ExitSolo();
        private void PrevSolo_Click(object sender, RoutedEventArgs e) => PrevSolo();
        private void NextSolo_Click(object sender, RoutedEventArgs e) => NextSolo();
        private void ResetSoloZoom_Click(object sender, RoutedEventArgs e) => ResetSoloZoom();

        #endregion

        #region Actions

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ChangePhotosButton_Click(object sender, RoutedEventArgs e)
        {
            ChangePhotosRequested?.Invoke(this, EventArgs.Empty);
        }

        public int ShownSlots => _items.Count == 0 ? 0 : VisibleCount(_items.Count);

        public IReadOnlyList<string> SessionPaths()
        {
            var paths = new List<string>(_items.Count);
            foreach (var item in _items)
            {
                if (!string.IsNullOrEmpty(item.Path))
                    paths.Add(item.Path);
            }
            return paths;
        }

        /// <summary>
        /// Put these photos into the frames already on screen. The first frames keep
        /// their edges, hero, and zoom. Later photos wait off screen.
        /// </summary>
        public bool TryReplacePhotos(IReadOnlyList<ImageItem> incoming)
        {
            int slots = ShownSlots;
            if (slots < 2 || !CellRootsReady()) return false;
            var photos = new List<ImageItem>(incoming.Count);
            foreach (var item in incoming)
            {
                if (item != null && !item.IsDirectory)
                    photos.Add(item);
            }
            if (photos.Count < slots) return false;

            ExitSolo();
            _framedSlots = slots;
            _items.Clear();
            _items.AddRange(photos);
            for (int i = 0; i < slots; i++)
            {
                if (_cellRoots[i]?.Tag is CellParts parts)
                {
                    parts.Bound.Item = _items[i];
                    parts.Bound.Reload?.Invoke();
                }
            }
            RefreshLinesChrome(_items.Count);
            RefreshHeroChrome(_items.Count);
            ShowHudAndOverlays();
            return true;
        }

        private int _swapSource = -1;

        /// <summary>Alt-drag: a Windows drag of this photo. Dropping on another photo swaps the two.</summary>
        private async System.Threading.Tasks.Task StartSwapDragAsync(UIElement source, Microsoft.UI.Input.PointerPoint point, int index)
        {
            _swapSource = index;
            try { await source.StartDragAsync(point); }
            catch { }
            finally { _swapSource = -1; }
        }

        private void AttachSwapDrop(Border cellBorder, Grid cellGrid, CellPhoto photo)
        {
            Windows.UI.Color accent = Windows.UI.Color.FromArgb(255, 96, 205, 255);
            try { if (Application.Current.Resources["SystemAccentColorLight2"] is Windows.UI.Color c) accent = c; }
            catch { }
            var ring = new Rectangle
            {
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 4,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, accent.R, accent.G, accent.B)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetZIndex(ring, 100);
            cellGrid.Children.Add(ring);
            cellGrid.AllowDrop = true;

            cellGrid.DragStarting += async (s, e) =>
            {
                ShowPositionBadges(Slot(photo.Item));
                e.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.Data.SetText(photo.Item.Name);
                cellBorder.Opacity = 0.4;
                var deferral = e.GetDeferral();
                try { await SetDragPictureAsync(cellGrid, e.DragUI, e.GetPosition(cellGrid)); }
                catch { }
                finally { deferral.Complete(); }
            };
            cellGrid.DropCompleted += (s, e) =>
            {
                HidePositionBadges();
                cellBorder.Opacity = 1;
            };

            void Over(object s, DragEventArgs e)
            {
                if (_swapSource < 0 || _swapSource == Slot(photo.Item))
                {
                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                    return;
                }
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.Caption = "Swap";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
                ring.Visibility = Visibility.Visible;
                e.Handled = true;
            }
            cellGrid.DragEnter += Over;
            cellGrid.DragOver += Over;
            cellGrid.DragLeave += (s, e) => ring.Visibility = Visibility.Collapsed;
            cellGrid.Drop += (s, e) =>
            {
                ring.Visibility = Visibility.Collapsed;
                int from = _swapSource;
                int index = Slot(photo.Item);
                if (from < 0 || index < 0 || from == index) return;
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.Handled = true;
                // Swap after the drag has finished with these elements.
                DispatcherQueue.TryEnqueue(() => SwapItems(from, index));
            };
        }

        /// <summary>A small picture of the photo under the pointer, held where it was grabbed.</summary>
        private static async System.Threading.Tasks.Task SetDragPictureAsync(FrameworkElement element, DragUI ui, Point grab)
        {
            double w = element.ActualWidth, h = element.ActualHeight;
            if (w < 1 || h < 1) return;
            double scale = Math.Min(1.0, 240.0 / Math.Max(w, h));
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element, (int)Math.Max(1, w * scale), (int)Math.Max(1, h * scale));
            if (rtb.PixelWidth < 1 || rtb.PixelHeight < 1) return;
            var pixels = await rtb.GetPixelsAsync();
            var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                pixels,
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                rtb.PixelWidth,
                rtb.PixelHeight,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            ui.SetContentFromSoftwareBitmap(bitmap, new Point(grab.X * rtb.PixelWidth / w, grab.Y * rtb.PixelHeight / h));
        }

        public void SwapItems(int idxA, int idxB)
        {
            if (idxA < 0 || idxA >= _items.Count || idxB < 0 || idxB >= _items.Count || idxA == idxB) return;
            (_items[idxA], _items[idxB]) = (_items[idxB], _items[idxA]);

            int visible = VisibleCount(_items.Count);
            if (idxA < visible && idxB < visible && _mosaic != null && CellRootsReady())
            {
                (_cellRoots[idxA], _cellRoots[idxB]) = (_cellRoots[idxB], _cellRoots[idxA]);
                // The dragged photo is dimmed while it travels; it is the same element afterwards.
                _cellRoots[idxA]!.Opacity = 1;
                _cellRoots[idxB]!.Opacity = 1;
                _keepMosaic = true;
                try { BuildLayout(); }
                finally { _keepMosaic = false; }
            }
            else
            {
                BuildLayout();
            }
            ShowHudAndOverlays();
        }

        private void FillFitButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFillMode();
        }

        public void ReflowFrames()
        {
            foreach (var update in _layoutUpdaters)
                update();
        }

        public void ToggleFillMode()
        {
            _isFillMode = !_isFillMode;
            UpdateFillFitButton();
            ResetAllZooms();
            foreach (var update in _layoutUpdaters)
            {
                update();
            }
            ShowHudAndOverlays();
        }

        private void UpdateFillFitButton()
        {
            if (FillFitText != null)
            {
                FillFitText.Text = _isFillMode ? "Fill" : "Fit";
            }
            if (FillFitIcon != null)
            {
                FillFitIcon.Glyph = _isFillMode ? "\uE740" : "\uE73F";
            }
            if (FillFitButton != null)
            {
                ToolTipService.SetToolTip(FillFitButton, _isFillMode 
                    ? "Fill Mode (F) - Zero wasted space (Click for Fit Entire Photo)" 
                    : "Fit Mode (F) - Whole photo visible (Click for Fill)");
            }
        }

        private void ResetAllZooms()
        {
            foreach (var tr in _cellTransforms)
            {
                tr.ScaleX = 1.0;
                tr.ScaleY = 1.0;
                tr.TranslateX = 0;
                tr.TranslateY = 0;
            }
        }

        private void ResetZoom_Click(object sender, RoutedEventArgs e)
        {
            ResetAllZooms();
        }

        #endregion
    }
}

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        /// <summary>More or fewer rows (or columns). Leaves the hero and any dragged sizes.</summary>
        public void ChangeLines(int delta)
        {
            int total = LineTotal();
            if (MosaicLayout.ShownCount(_items.Count) < 2 || total < 1) return;
            int current = Math.Clamp(_lines, 1, total);
            int next = Math.Clamp(current + delta, 1, total);
            bool linesInUse = !_heroActive || HeroUsesLines;
            if (next == current && linesInUse && !_mosaicCustom) return;
            _lines = next;
            _lineCounts = null;
            if (!HeroUsesLines) _heroActive = false;
            RelayoutKeepingCells();
            ShowHudAndOverlays();
        }

        /// <summary>A banner or pair keeps the line controls for the other photos. Rectangle does not.</summary>
        private bool HeroUsesLines => _heroActive && _heroKind != HeroKind.Rectangle;

        /// <summary>Photos the line controls arrange: all of them, or the ones beside a banner or pair.</summary>
        private int LineTotal()
        {
            int shown = MosaicLayout.ShownCount(_items.Count);
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
            FillCountsPanel();
            ShowHudAndOverlays();
        }

        private void CountsFlyout_Opening(object sender, object e) => FillCountsPanel();

        private void FillCountsPanel()
        {
            CountsPanel.Children.Clear();
            var counts = CurrentLineCounts();
            bool rows = !_flipped;
            int total = LineTotal();
            int lines = EffectiveLines();
            var evenCounts = MosaicLayout.LineCounts(total, lines);
            bool isCustom = !counts.SequenceEqual(evenCounts);

            var accent = Windows.UI.Color.FromArgb(255, 0, 120, 212);
            try { if (Application.Current.Resources["SystemAccentColor"] is Windows.UI.Color c) accent = c; }
            catch { }

            // 1. Header: Title, subtitle and status badge
            var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 1 };
            titleStack.Children.Add(new TextBlock
            {
                Text = rows ? "Row Distribution" : "Column Distribution",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            });
            string lineNoun = rows ? (counts.Length == 1 ? "row" : "rows") : (counts.Length == 1 ? "column" : "columns");
            titleStack.Children.Add(new TextBlock
            {
                Text = $"{total} photos across {counts.Length} {lineNoun}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 255, 255, 255))
            });
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            var statusBadge = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = isCustom
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(45, accent.R, accent.G, accent.B))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = isCustom
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(160, accent.R, accent.G, accent.B))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = isCustom ? "Custom" : "Balanced",
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = isCustom
                        ? new SolidColorBrush(accent)
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255))
                }
            };
            Grid.SetColumn(statusBadge, 1);
            header.Children.Add(statusBadge);

            CountsPanel.Children.Add(header);

            // 2. Interactive Visual Canvas & Steppers
            CountsPanel.Children.Add(BuildCountsCanvas(counts, rows, accent));

            // 3. Footer: Distribute Evenly (always present in fixed spot to keep box size completely stable)
            CountsPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255)),
                Margin = new Thickness(0, 6, 0, 4)
            });

            var resetBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(isCustom
                    ? Windows.UI.Color.FromArgb(30, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(15, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(isCustom
                    ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                IsEnabled = isCustom,
                Opacity = isCustom ? 1.0 : 0.38
            };
            ToolTipService.SetToolTip(resetBtn, isCustom
                ? "Reset photos to an even distribution across lines"
                : "Photos are currently evenly distributed");

            var btnContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnContent.Children.Add(new FontIcon
            {
                Glyph = "\uE8A9",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = isCustom
                    ? new SolidColorBrush(accent)
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255))
            });
            btnContent.Children.Add(new TextBlock
            {
                Text = "Distribute Evenly",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = isCustom
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255))
            });
            resetBtn.Content = btnContent;
            if (isCustom)
            {
                resetBtn.Click += (_, _) =>
                {
                    _lineCounts = null;
                    RelayoutKeepingCells();
                    FillCountsPanel();
                    ShowHudAndOverlays();
                };
            }
            CountsPanel.Children.Add(resetBtn);
        }

        /// <summary>
        /// Renders an interactive miniature layout preview showing photos in rows or columns,
        /// with integrated stepper capsules for shifting photos between lines.
        /// </summary>
        private FrameworkElement BuildCountsCanvas(int[] counts, bool rows, Windows.UI.Color accent)
        {
            var cellFill = new SolidColorBrush(Windows.UI.Color.FromArgb(170, accent.R, accent.G, accent.B));
            var cellStroke = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 255, 255, 255));
            var trayFill = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255));
            var trayStroke = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255));
            var iconBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255));

            Border CreateStepper(int line, int count)
            {
                bool canLess = MosaicLayout.MovePhoto(counts, line, -1) != null;
                bool canMore = MosaicLayout.MovePhoto(counts, line, 1) != null;

                var capsule = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Height = 26
                };

                var pillGrid = new Grid();
                pillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                pillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                pillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

                var minusBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE738", FontSize = 9, FontFamily = new FontFamily("Segoe Fluent Icons") },
                    Width = 20,
                    Height = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(5, 0, 0, 5),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    IsEnabled = canLess,
                    Opacity = canLess ? 1.0 : 0.3
                };
                ToolTipService.SetToolTip(minusBtn, canLess ? $"One fewer in {(rows ? "row" : "column")} {line + 1}" : "Cannot reduce further");
                minusBtn.Click += (_, _) => AdjustLine(line, -1);
                Grid.SetColumn(minusBtn, 0);
                pillGrid.Children.Add(minusBtn);

                var countText = new TextBlock
                {
                    Text = count.ToString(),
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(countText, 1);
                pillGrid.Children.Add(countText);

                var plusBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE710", FontSize = 9, FontFamily = new FontFamily("Segoe Fluent Icons") },
                    Width = 20,
                    Height = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(0, 5, 5, 0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    IsEnabled = canMore,
                    Opacity = canMore ? 1.0 : 0.3
                };
                ToolTipService.SetToolTip(plusBtn, canMore ? $"One more in {(rows ? "row" : "column")} {line + 1}" : "Cannot increase further");
                plusBtn.Click += (_, _) => AdjustLine(line, 1);
                Grid.SetColumn(plusBtn, 2);
                pillGrid.Children.Add(plusBtn);

                capsule.Child = pillGrid;
                return capsule;
            }

            Border CreatePhotoCell(double width, double height)
            {
                var cell = new Border
                {
                    Width = Math.Max(4, width),
                    Height = Math.Max(4, height),
                    Background = cellFill,
                    BorderBrush = cellStroke,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3)
                };
                if (width >= 22 && height >= 16)
                {
                    cell.Child = new FontIcon
                    {
                        Glyph = "\uEB9F",
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = Math.Clamp(Math.Min(width, height) * 0.45, 8, 11),
                        Foreground = iconBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                return cell;
            }

            if (rows)
            {
                // Rows mode: stacked horizontal lanes with labels on left, tracks in center, steppers on right
                const double trackWidth = 175;
                double trackHeight = Math.Clamp((140.0 - (counts.Length - 1) * 6) / counts.Length, 24.0, 32.0);

                var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
                for (int i = 0; i < counts.Length; i++)
                {
                    int line = i;
                    int count = counts[i];

                    var rowLane = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

                    // Line label
                    rowLane.Children.Add(new TextBlock
                    {
                        Text = $"Row {i + 1}",
                        Width = 42,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(190, 255, 255, 255)),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    // Track containing photo cells
                    var track = new Border
                    {
                        Width = trackWidth,
                        Height = trackHeight,
                        Background = trayFill,
                        BorderBrush = trayStroke,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(2)
                    };
                    ToolTipService.SetToolTip(track, $"Row {i + 1}: {count} {(count == 1 ? "photo" : "photos")}");

                    var cellContainer = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    double usableWidth = trackWidth - 4 - (count - 1) * 2;
                    double cellW = Math.Max(4, usableWidth / count);
                    double cellH = trackHeight - 4;

                    for (int c = 0; c < count; c++)
                    {
                        cellContainer.Children.Add(CreatePhotoCell(cellW, cellH));
                    }
                    track.Child = cellContainer;
                    rowLane.Children.Add(track);

                    // Stepper
                    rowLane.Children.Add(CreateStepper(line, count));

                    panel.Children.Add(rowLane);
                }
                return panel;
            }
            else
            {
                // Columns mode: vertical lanes side-by-side with labels on top, tracks in center, steppers on bottom
                const double trackHeight = 130;
                int nCols = counts.Length;
                double colWidth = Math.Clamp((270.0 - (nCols - 1) * 8) / nCols, 46.0, 68.0);

                var columnsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                for (int i = 0; i < counts.Length; i++)
                {
                    int line = i;
                    int count = counts[i];

                    var colLane = new StackPanel
                    {
                        Width = Math.Max(colWidth, 62),
                        Spacing = 5,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };

                    // Label
                    colLane.Children.Add(new TextBlock
                    {
                        Text = $"Col {i + 1}",
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(190, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });

                    // Track
                    var track = new Border
                    {
                        Width = colWidth,
                        Height = trackHeight,
                        Background = trayFill,
                        BorderBrush = trayStroke,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(2),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    ToolTipService.SetToolTip(track, $"Column {i + 1}: {count} {(count == 1 ? "photo" : "photos")}");

                    var cellContainer = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 2,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    double usableHeight = trackHeight - 4 - (count - 1) * 2;
                    double cellH = Math.Max(4, usableHeight / count);
                    double cellW = colWidth - 4;

                    for (int c = 0; c < count; c++)
                    {
                        cellContainer.Children.Add(CreatePhotoCell(cellW, cellH));
                    }
                    track.Child = cellContainer;
                    colLane.Children.Add(track);

                    // Stepper
                    var stepper = CreateStepper(line, count);
                    stepper.HorizontalAlignment = HorizontalAlignment.Center;
                    colLane.Children.Add(stepper);

                    columnsPanel.Children.Add(colLane);
                }
                return columnsPanel;
            }
        }

        /// <summary>
        /// Rows become columns and back. A plain grid or a Rectangle turns a quarter, dragged sizes kept.
        /// Beside a banner or pair only the other photos turn, so their sizes go back to even like
        /// any other line change.
        /// </summary>
        public void ToggleRowsColumns()
        {
            if (MosaicLayout.ShownCount(_items.Count) < 2) return;
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

        private void LinesLess_Click(object sender, RoutedEventArgs e) => ChangeLines(-1);
        private void LinesMore_Click(object sender, RoutedEventArgs e) => ChangeLines(1);
        private void RowsColumns_Click(object sender, RoutedEventArgs e) => ToggleRowsColumns();

        private void HeroButton_Click(object sender, RoutedEventArgs e)
        {
            var kinds = MosaicLayout.HeroKinds(_items.Count);
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
            ShowHudAndOverlays();
        }

        private void RotateHero_Click(object sender, RoutedEventArgs e)
        {
            if (!_heroActive) return;
            int positions = MosaicLayout.HeroPositions(_items.Count, _heroKind);
            _heroPosition = (_heroPosition + 1) % positions;
            RelayoutKeepingCells();
            ShowHudAndOverlays();
        }

        private void NormalizeHero()
        {
            var kinds = MosaicLayout.HeroKinds(_items.Count);
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
                int positions = MosaicLayout.HeroPositions(_items.Count, _heroKind);
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
            HeroText.Text = _heroActive ? MosaicLayout.HeroTitle(_heroKind) : "Hero";

            if (_heroActive)
            {
                HeroButton.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush
                    ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215));
                HeroText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                HeroText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                if (HeroIcon != null)
                {
                    HeroIcon.Glyph = "\uE735";
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
                    HeroIcon.Glyph = "\uE734";
                    HeroIcon.ClearValue(FontIcon.ForegroundProperty);
                    HeroIcon.Opacity = 0.85;
                }
            }

            ToolTipService.SetToolTip(HeroButton, _heroActive
                ? "Hero: " + HeroText.Text + ". Click to advance or turn off (H)"
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

            try
            {
                var bmp = new BitmapImage();
                bmp.UriSource = new Uri(item.Path, UriKind.Absolute);
                bmp.ImageOpened += (s, e) =>
                {
                    if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                    {
                        naturalWidth = bmp.PixelWidth;
                        naturalHeight = bmp.PixelHeight;
                        UpdateImageLayout();
                    }
                    // The thumbnail would show through a transparent photo, and is no longer needed.
                    if (thumb != null)
                    {
                        imgContainer.Children.Remove(thumb);
                        thumb = null;
                    }
                };
                fullImg.Source = bmp;
            }
            catch { }

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
                        _ = StartSwapDragAsync(cellGrid, e.GetCurrentPoint(cellGrid), Slot(item));
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
                    () => (Slot(item) == 0 ? "Move to last position" : "Move to previous position") + " (or Alt+drag onto another photo to swap)",
                    () => MovePhoto(Slot(item), -1));
                moveRightBtn = CreateNudgeButton("\uE76C",
                    () => (Slot(item) == VisibleCount(_items.Count) - 1 ? "Move to first position" : "Move to next position") + " (or Alt+drag onto another photo to swap)",
                    () => MovePhoto(Slot(item), 1));
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
            maxBtn.Click += (s, e) => EnterSolo(Slot(item));
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
            closeBtn.Click += (s, e) => RemoveItem(item);
            topStack.Children.Add(closeBtn);

            topControls.Children.Add(topStack);
            cellGrid.Children.Add(topControls);

            // Right-Click Context Menu, filled when it opens so it names the photo's current place
            var menu = new MenuFlyout();
            FillCellMenu(menu, item);
            menu.Opening += (_, _) => FillCellMenu(menu, item);

            cellGrid.ContextFlyout = menu;

            AttachSwapDrop(cellBorder, cellGrid, item);
            cellBorder.Tag = new CellParts(transform, updateLayout, topControls, fullImg);

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
                var moveLeftItem = new MenuFlyoutItem { Text = "Move Left / Up", Icon = new FontIcon { Glyph = "\uE76B" } };
                moveLeftItem.Click += (s, e) => MovePhoto(Slot(item), -1);
                menu.Items.Add(moveLeftItem);

                var moveRightItem = new MenuFlyoutItem { Text = "Move Right / Down", Icon = new FontIcon { Glyph = "\uE76C" } };
                moveRightItem.Click += (s, e) => MovePhoto(Slot(item), 1);
                menu.Items.Add(moveRightItem);

                if (_items.Count == 2)
                {
                    var swapItem = new MenuFlyoutItem { Text = "Swap with Other Photo", Icon = new FontIcon { Glyph = "\uE8AB" } };
                    swapItem.Click += (s, e) => SwapItems(Slot(item), 1 - Slot(item));
                    menu.Items.Add(swapItem);
                }
                else
                {
                    var swapSub = new MenuFlyoutSubItem { Text = "Swap with...", Icon = new FontIcon { Glyph = "\uE8AB" } };
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i == index) continue;
                        var target = _items[i];
                        var targetItem = new MenuFlyoutItem { Text = $"Position {i + 1} ({target.Name})" };
                        targetItem.Click += (s, e) => SwapItems(Slot(item), Slot(target));
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
        private sealed record CellParts(CompositeTransform Transform, Action UpdateLayout, UIElement TopControls, Image Photo);

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

        private int _swapSource = -1;

        /// <summary>Alt-drag: a Windows drag of this photo. Dropping on another photo swaps the two.</summary>
        private async System.Threading.Tasks.Task StartSwapDragAsync(UIElement source, Microsoft.UI.Input.PointerPoint point, int index)
        {
            _swapSource = index;
            try { await source.StartDragAsync(point); }
            catch { }
            finally { _swapSource = -1; }
        }

        private void AttachSwapDrop(Border cellBorder, Grid cellGrid, ImageItem item)
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
                e.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.Data.SetText(item.Name);
                cellBorder.Opacity = 0.4;
                var deferral = e.GetDeferral();
                try { await SetDragPictureAsync(cellGrid, e.DragUI, e.GetPosition(cellGrid)); }
                catch { }
                finally { deferral.Complete(); }
            };
            cellGrid.DropCompleted += (s, e) => cellBorder.Opacity = 1;

            void Over(object s, DragEventArgs e)
            {
                if (_swapSource < 0 || _swapSource == Slot(item))
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
                int index = Slot(item);
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

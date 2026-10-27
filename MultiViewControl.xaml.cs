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

        private readonly List<ImageItem> _items = new();
        private readonly List<CompositeTransform> _cellTransforms = new();
        private readonly List<Action> _layoutUpdaters = new();
        private readonly List<UIElement> _cellTopControls = new();
        private readonly List<FrameworkElement> _cellElements = new();
        private bool _isFillMode = true; // Default to Fill for zero wasted space

        private readonly DispatcherTimer _hudTimer;
        private bool _isHudVisible = true;
        private bool _isPointerOverHud = false;

        private int _soloIndex = -1;
        private int _layoutMode = 0;

        // Solo drag tracking
        private bool _isSoloDragging = false;
        private Point _lastSoloPoint = default;
        private uint _soloPointerId = 0;

        public MultiViewControl()
        {
            this.InitializeComponent();

            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _hudTimer.Tick += HudTimer_Tick;

            SetupSoloInteractions();
        }

        public bool IsSoloActive => SoloContainer.Visibility == Visibility.Visible;

        public void LoadImages(IEnumerable<ImageItem> items)
        {
            _items.Clear();
            _items.AddRange(items.Where(i => i != null && !i.IsDirectory));
            
            _isFillMode = true; // Always default to Fill (crop to fill) to eliminate black bars
            _layoutMode = ChooseDefaultLayoutMode();

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
            _cellElements.Clear();
            CellsGrid.Children.Clear();
            CellsGrid.RowDefinitions.Clear();
            CellsGrid.ColumnDefinitions.Clear();
            SoloImage.Source = null;
            _soloIndex = -1;
        }

        private int ChooseDefaultLayoutMode()
        {
            if (_items.Count == 3)
            {
                // If the first image is a portrait (taller than wide), Left+2Right is great.
                // Otherwise for landscape, Top+2Bottom is vastly superior.
                if (_items.Count > 0 && _items[0].Thumbnail is BitmapSource bs && bs.PixelHeight > bs.PixelWidth)
                {
                    return 1; // Left + 2 Right (Columns-First)
                }
                return 0; // Top + 2 Bottom (Rows-First)
            }
            return 0; // Default to Columns-First (0)
        }

        private int GetMaxModesForCount(int count)
        {
            if (count == 2) return 2; // Side-by-Side (0), Top-and-Bottom (1)
            if (count == 3) return 4; // Top+2Bottom (0), Left+2Right (1), 3 Rows (2), 3 Cols (3)
            if (count == 4) return 5; // Columns-First (0), Rows-First (1), 2x2 Grid (2), 4 Rows (3), 4 Cols (4)
            if (count <= 6) return 2; // 2 Rows, Split (0), 3 Rows, Split (1)
            return 1;
        }

        private string GetLayoutModeTitle(int count, int mode)
        {
            if (count == 2)
            {
                return mode == 0 ? "Side-by-Side (Columns)" : "Top-and-Bottom (Rows)";
            }
            if (count == 3)
            {
                return mode switch
                {
                    0 => "Top + 2 Bottom (Rows-First)",
                    1 => "Left + 2 Right (Columns-First)",
                    2 => "3 Rows",
                    _ => "3 Columns"
                };
            }
            if (count == 4)
            {
                return mode switch
                {
                    0 => "Columns-First (Vertical Master, Split Rows)",
                    1 => "Rows-First (Horizontal Master, Split Columns)",
                    2 => "2×2 Grid (Synchronized Crosshair)",
                    3 => "4 Rows",
                    _ => "4 Columns"
                };
            }
            if (count <= 6)
            {
                return mode == 0 ? "2 Rows, Split" : "3 Rows, Split";
            }
            return "Grid";
        }

        private void UpdateOrientationFlyout(int count)
        {
            if (OrientationMenuFlyout == null) return;
            OrientationMenuFlyout.Items.Clear();
            int maxModes = GetMaxModesForCount(count);
            for (int m = 0; m < maxModes; m++)
            {
                int modeIndex = m;
                string title = GetLayoutModeTitle(count, m);
                var item = new MenuFlyoutItem
                {
                    Text = (modeIndex == _layoutMode ? "✓  " : "    ") + title
                };
                item.Click += (s, e) =>
                {
                    _layoutMode = modeIndex;
                    BuildLayout();
                    ShowHudAndOverlays();
                };
                OrientationMenuFlyout.Items.Add(item);
            }
        }

        public void ToggleOrientation()
        {
            int maxModes = GetMaxModesForCount(_items.Count);
            if (maxModes > 1)
            {
                _layoutMode = (_layoutMode + 1) % maxModes;
                BuildLayout();
                ShowHudAndOverlays();
            }
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
                case VirtualKey.Tab:
                    ToggleOrientation();
                    return true;
                case VirtualKey.F:
                    ToggleFillMode();
                    return true;
                case VirtualKey.S:
                    SwapOrRotateItems();
                    return true;
                case VirtualKey.Number1:
                case VirtualKey.Number2:
                case VirtualKey.Number3:
                case VirtualKey.Number4:
                case VirtualKey.Number5:
                case VirtualKey.Number6:
                case VirtualKey.Number7:
                case VirtualKey.Number8:
                case VirtualKey.Number9:
                    int idx = (int)key - (int)VirtualKey.Number1;
                    if (idx >= 0 && idx < _items.Count)
                    {
                        EnterSolo(idx);
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

                SoloInfoOverlay.Opacity = 1.0;
                SoloBottomBar.Opacity = 1.0;
                SoloBottomBar.IsHitTestVisible = true;
                SoloTopRightControls.Opacity = 1.0;
                SoloTopRightControls.IsHitTestVisible = true;

                foreach (var ctrl in _cellTopControls)
                {
                    ctrl.Opacity = 1.0;
                    ctrl.IsHitTestVisible = true;
                }
            }
        }

        private void HideHudAndOverlays()
        {
            if (_isPointerOverHud) return;

            _hudTimer.Stop();
            _isHudVisible = false;

            UnifiedHud.Opacity = 0.0;
            UnifiedHud.IsHitTestVisible = false;
            HudTranslate.Y = 15;

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

        private void BuildLayout()
        {
            CellsGrid.Children.Clear();
            CellsGrid.RowDefinitions.Clear();
            CellsGrid.ColumnDefinitions.Clear();
            _cellTransforms.Clear();
            _layoutUpdaters.Clear();
            _cellTopControls.Clear();
            _cellElements.Clear();

            int count = _items.Count;
            if (count == 0)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            TitleTextBlock.Text = $"{count} {(count == 1 ? "Photo" : "Photos")}";
            OrientationToggleButton.Visibility = GetMaxModesForCount(count) > 1 ? Visibility.Visible : Visibility.Collapsed;
            SwapButton.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            UpdateFillFitButton();

            if (count == 1)
            {
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                AddCell(_items[0], 0, 0, 1, 1, 0);
            }
            else if (count == 2)
            {
                if (_layoutMode == 0)
                {
                    OrientationText.Text = "Side-by-Side";
                    OrientationIcon.Glyph = "\uE749";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: Side-by-Side (Columns) — Click or Tab to switch to Top-and-Bottom. Right-click for menu.");
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    AddCell(_items[0], 0, 0, 1, 1, 0);
                    AddCell(_items[1], 0, 1, 1, 1, 1);
                    AddColumnSplitter(CellsGrid, 0, 1, 0, 1);
                }
                else
                {
                    OrientationText.Text = "Top-and-Bottom";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: Top-and-Bottom (Rows) — Click or Tab to switch to Side-by-Side. Right-click for menu.");
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    AddCell(_items[0], 0, 0, 1, 1, 0);
                    AddCell(_items[1], 1, 0, 1, 1, 1);
                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                }
            }
            else if (count == 3)
            {
                if (_layoutMode == 0)
                {
                    // Top (full width) + 2 Bottom (side by side with independent vertical divider)
                    OrientationText.Text = "Top + 2 Bottom";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: Top + 2 Bottom (Rows-First) — Click or Tab to switch layout. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

                    AddCell(CellsGrid, _items[0], 0, 0, 1, 1, 0);

                    var bottomGrid = new Grid();
                    bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                    bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                    bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
                    AddCell(bottomGrid, _items[1], 0, 0, 1, 1, 1);
                    AddCell(bottomGrid, _items[2], 0, 1, 1, 1, 2);
                    AddColumnSplitter(bottomGrid, 0, 1, 0, 1);

                    Grid.SetRow(bottomGrid, 1);
                    CellsGrid.Children.Add(bottomGrid);

                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                }
                else if (_layoutMode == 1)
                {
                    // 1 Left (full height) + 2 Right (stacked with independent horizontal divider)
                    OrientationText.Text = "Left + 2 Right";
                    OrientationIcon.Glyph = "\uE749";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: Left + 2 Right (Columns-First) — Click or Tab to switch layout. Right-click for menu.");

                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });

                    AddCell(CellsGrid, _items[0], 0, 0, 1, 1, 0);

                    var rightGrid = new Grid();
                    rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
                    rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
                    rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                    AddCell(rightGrid, _items[1], 0, 0, 1, 1, 1);
                    AddCell(rightGrid, _items[2], 1, 0, 1, 1, 2);
                    AddRowSplitter(rightGrid, 0, 1, 0, 1);

                    Grid.SetColumn(rightGrid, 1);
                    CellsGrid.Children.Add(rightGrid);

                    AddColumnSplitter(CellsGrid, 0, 1, 0, 1);
                }
                else if (_layoutMode == 2)
                {
                    // 3 Horizontal Rows
                    OrientationText.Text = "3 Rows";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 3 Rows — Click or Tab to switch layout. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    AddCell(_items[0], 0, 0, 1, 1, 0);
                    AddCell(_items[1], 1, 0, 1, 1, 1);
                    AddCell(_items[2], 2, 0, 1, 1, 2);

                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                    AddRowSplitter(CellsGrid, 1, 2, 0, 1);
                }
                else
                {
                    // 3 Columns side-by-side
                    OrientationText.Text = "3 Columns";
                    OrientationIcon.Glyph = "\uE749";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 3 Columns — Click or Tab to switch layout. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    AddCell(_items[0], 0, 0, 1, 1, 0);
                    AddCell(_items[1], 0, 1, 1, 1, 1);
                    AddCell(_items[2], 0, 2, 1, 1, 2);

                    AddColumnSplitter(CellsGrid, 0, 1, 0, 1);
                    AddColumnSplitter(CellsGrid, 1, 2, 0, 1);
                }
            }
            else if (count == 4)
            {
                if (_layoutMode == 0)
                {
                    // Columns-First: Master vertical divider, independent horizontal divider per column!
                    OrientationText.Text = "Columns-First";
                    OrientationIcon.Glyph = "\uE749";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: Columns-First (Vertical master divider, independent horizontal cuts) — Click or Tab to switch to Rows-First. Right-click for menu.");

                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                    var leftGrid = new Grid();
                    leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    AddCell(leftGrid, _items[0], 0, 0, 1, 1, 0); // Top-Left
                    AddCell(leftGrid, _items[2], 1, 0, 1, 1, 2); // Bottom-Left
                    AddRowSplitter(leftGrid, 0, 1, 0, 1);

                    var rightGrid = new Grid();
                    rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    AddCell(rightGrid, _items[1], 0, 0, 1, 1, 1); // Top-Right
                    AddCell(rightGrid, _items[3], 1, 0, 1, 1, 3); // Bottom-Right
                    AddRowSplitter(rightGrid, 0, 1, 0, 1);

                    Grid.SetColumn(leftGrid, 0);
                    CellsGrid.Children.Add(leftGrid);
                    Grid.SetColumn(rightGrid, 1);
                    CellsGrid.Children.Add(rightGrid);

                    AddColumnSplitter(CellsGrid, 0, 1, 0, 1);
                }
                else if (_layoutMode == 1)
                {
                    // Rows-First: Master horizontal divider, independent vertical divider per row!
                    OrientationText.Text = "Rows-First";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: Rows-First (Horizontal master divider, independent vertical cuts) — Click or Tab to switch to Columns-First. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var topGrid = new Grid();
                    topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    topGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    AddCell(topGrid, _items[0], 0, 0, 1, 1, 0); // Top-Left
                    AddCell(topGrid, _items[1], 0, 1, 1, 1, 1); // Top-Right
                    AddColumnSplitter(topGrid, 0, 1, 0, 1);

                    var bottomGrid = new Grid();
                    bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    AddCell(bottomGrid, _items[2], 0, 0, 1, 1, 2); // Bottom-Left
                    AddCell(bottomGrid, _items[3], 0, 1, 1, 1, 3); // Bottom-Right
                    AddColumnSplitter(bottomGrid, 0, 1, 0, 1);

                    Grid.SetRow(topGrid, 0);
                    CellsGrid.Children.Add(topGrid);
                    Grid.SetRow(bottomGrid, 1);
                    CellsGrid.Children.Add(bottomGrid);

                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                }
                else if (_layoutMode == 2)
                {
                    // Synchronized 2x2 Grid (unified crosshair)
                    OrientationText.Text = "2×2 Grid";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 2×2 Grid (Synchronized crosshair) — Click or Tab to switch layout. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    AddCell(_items[0], 0, 0, 1, 1, 0);
                    AddCell(_items[1], 0, 1, 1, 1, 1);
                    AddCell(_items[2], 1, 0, 1, 1, 2);
                    AddCell(_items[3], 1, 1, 1, 1, 3);

                    AddColumnSplitter(CellsGrid, 0, 1, 0, 2);
                    AddRowSplitter(CellsGrid, 0, 1, 0, 2);
                }
                else if (_layoutMode == 3)
                {
                    OrientationText.Text = "4 Rows";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 4 Rows — Click or Tab to switch layout. Right-click for menu.");

                    for (int i = 0; i < 4; i++)
                        CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int i = 0; i < 4; i++)
                        AddCell(_items[i], i, 0, 1, 1, i);

                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                    AddRowSplitter(CellsGrid, 1, 2, 0, 1);
                    AddRowSplitter(CellsGrid, 2, 3, 0, 1);
                }
                else
                {
                    OrientationText.Text = "4 Columns";
                    OrientationIcon.Glyph = "\uE749";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 4 Columns — Click or Tab to switch layout. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    for (int i = 0; i < 4; i++)
                        CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int i = 0; i < 4; i++)
                        AddCell(_items[i], 0, i, 1, 1, i);

                    AddColumnSplitter(CellsGrid, 0, 1, 0, 1);
                    AddColumnSplitter(CellsGrid, 1, 2, 0, 1);
                    AddColumnSplitter(CellsGrid, 2, 3, 0, 1);
                }
            }
            else if (count <= 6)
            {
                if (_layoutMode == 0)
                {
                    OrientationText.Text = "2 Rows, Split";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 2 Rows, Split — Click or Tab to switch to 3 Rows. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    int colsTop = (count + 1) / 2;
                    int colsBottom = count - colsTop;

                    var topGrid = new Grid();
                    topGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    for (int c = 0; c < colsTop; c++)
                        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int c = 0; c < colsTop; c++)
                        AddCell(topGrid, _items[c], 0, c, 1, 1, c);
                    for (int c = 0; c < colsTop - 1; c++)
                        AddColumnSplitter(topGrid, c, c + 1, 0, 1);

                    var bottomGrid = new Grid();
                    bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    for (int c = 0; c < colsBottom; c++)
                        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int c = 0; c < colsBottom; c++)
                        AddCell(bottomGrid, _items[colsTop + c], 0, c, 1, 1, colsTop + c);
                    for (int c = 0; c < colsBottom - 1; c++)
                        AddColumnSplitter(bottomGrid, c, c + 1, 0, 1);

                    Grid.SetRow(topGrid, 0);
                    CellsGrid.Children.Add(topGrid);
                    Grid.SetRow(bottomGrid, 1);
                    CellsGrid.Children.Add(bottomGrid);

                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                }
                else
                {
                    OrientationText.Text = "3 Rows, Split";
                    OrientationIcon.Glyph = "\uE748";
                    ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 3 Rows, Split — Click or Tab to switch to 2 Rows. Right-click for menu.");

                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    int perRow = (count + 2) / 3;
                    int itemIdx = 0;
                    for (int r = 0; r < 3; r++)
                    {
                        int rowCols = Math.Min(perRow, count - itemIdx);
                        if (rowCols <= 0) break;

                        var rowGrid = new Grid();
                        rowGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                        for (int c = 0; c < rowCols; c++)
                            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        for (int c = 0; c < rowCols; c++)
                        {
                            AddCell(rowGrid, _items[itemIdx], 0, c, 1, 1, itemIdx);
                            itemIdx++;
                        }
                        for (int c = 0; c < rowCols - 1; c++)
                            AddColumnSplitter(rowGrid, c, c + 1, 0, 1);

                        Grid.SetRow(rowGrid, r);
                        CellsGrid.Children.Add(rowGrid);
                    }

                    AddRowSplitter(CellsGrid, 0, 1, 0, 1);
                    AddRowSplitter(CellsGrid, 1, 2, 0, 1);
                }
            }
            else
            {
                OrientationText.Text = "3×3 Grid";
                OrientationIcon.Glyph = "\uE748";
                ToolTipService.SetToolTip(OrientationToggleButton, "Layout: 3×3 Grid");

                int max = Math.Min(count, 9);
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int i = 0; i < max; i++)
                {
                    int r = i / 3;
                    int c = i % 3;
                    AddCell(_items[i], r, c, 1, 1, i);
                }

                AddColumnSplitter(CellsGrid, 0, 1, 0, 3);
                AddColumnSplitter(CellsGrid, 1, 2, 0, 3);
                AddRowSplitter(CellsGrid, 0, 1, 0, 3);
                AddRowSplitter(CellsGrid, 1, 2, 0, 3);
            }

            UpdateOrientationFlyout(count);
        }

        private sealed class SplitterElement : Grid
        {
            public void SetCursor(InputSystemCursorShape shape)
            {
                try
                {
                    ProtectedCursor = InputSystemCursor.Create(shape);
                }
                catch { }
            }
        }

        private void AddColumnSplitter(Grid targetGrid, int leftCol, int rightCol, int startRow = 0, int rowSpan = 1)
        {
            var splitter = new SplitterElement
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 14,
                Margin = new Thickness(0, 0, -7, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            Canvas.SetZIndex(splitter, 40);
            Grid.SetColumn(splitter, leftCol);
            Grid.SetRow(splitter, startRow);
            if (rowSpan > 1) Grid.SetRowSpan(splitter, rowSpan);

            splitter.SetCursor(InputSystemCursorShape.SizeWestEast);

            var line = new Rectangle
            {
                Width = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 162, 237)), // Modern Accent Blue
                Opacity = 0.0
            };
            splitter.Children.Add(line);

            bool isDragging = false;
            double startX = 0;
            double startW0 = 0;
            double startW1 = 0;
            DateTime lastClickTime = DateTime.MinValue;

            splitter.PointerEntered += (s, e) =>
            {
                ShowHudAndOverlays();
                line.Opacity = 0.85;
            };

            splitter.PointerExited += (s, e) =>
            {
                if (!isDragging) line.Opacity = 0.0;
            };

            splitter.PointerPressed += (s, e) =>
            {
                var ptr = e.GetCurrentPoint(splitter);
                if (!ptr.Properties.IsLeftButtonPressed) return;

                var now = DateTime.Now;
                if ((now - lastClickTime).TotalMilliseconds < 350)
                {
                    // Double-click resets columns in this grid to equal 1.0 Star
                    if (leftCol >= 0 && leftCol < targetGrid.ColumnDefinitions.Count)
                        targetGrid.ColumnDefinitions[leftCol].Width = new GridLength(1.0, GridUnitType.Star);
                    if (rightCol >= 0 && rightCol < targetGrid.ColumnDefinitions.Count)
                        targetGrid.ColumnDefinitions[rightCol].Width = new GridLength(1.0, GridUnitType.Star);

                    line.Opacity = 0.0;
                    e.Handled = true;
                    return;
                }
                lastClickTime = now;

                // Normalize all columns in targetGrid to current actual pixel widths as Star values
                foreach (var col in targetGrid.ColumnDefinitions)
                {
                    double w = col.ActualWidth > 0 ? col.ActualWidth : 100.0;
                    col.Width = new GridLength(w, GridUnitType.Star);
                }

                if (leftCol >= 0 && leftCol < targetGrid.ColumnDefinitions.Count)
                    startW0 = targetGrid.ColumnDefinitions[leftCol].Width.Value;
                if (rightCol >= 0 && rightCol < targetGrid.ColumnDefinitions.Count)
                    startW1 = targetGrid.ColumnDefinitions[rightCol].Width.Value;

                startX = e.GetCurrentPoint(targetGrid).Position.X;
                isDragging = true;
                _hudTimer.Stop();
                line.Opacity = 1.0;
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            splitter.PointerMoved += (s, e) =>
            {
                ShowHudAndOverlays();
                if (!isDragging) return;

                double currentX = e.GetCurrentPoint(targetGrid).Position.X;
                double dx = currentX - startX;
                double totalW = startW0 + startW1;
                if (totalW <= 0) return;

                double minW = Math.Max(80.0, totalW * 0.15);
                double newW0 = Math.Clamp(startW0 + dx, minW, totalW - minW);
                double newW1 = totalW - newW0;

                if (leftCol >= 0 && leftCol < targetGrid.ColumnDefinitions.Count)
                    targetGrid.ColumnDefinitions[leftCol].Width = new GridLength(newW0, GridUnitType.Star);
                if (rightCol >= 0 && rightCol < targetGrid.ColumnDefinitions.Count)
                    targetGrid.ColumnDefinitions[rightCol].Width = new GridLength(newW1, GridUnitType.Star);

                e.Handled = true;
            };

            void EndDrag(PointerRoutedEventArgs e)
            {
                if (!isDragging) return;
                isDragging = false;
                splitter.ReleasePointerCapture(e.Pointer);
                line.Opacity = 0.0;
                _hudTimer.Start();
                e.Handled = true;
            }

            splitter.PointerReleased += (s, e) => EndDrag(e);
            splitter.PointerCaptureLost += (s, e) => EndDrag(e);

            targetGrid.Children.Add(splitter);
        }

        private void AddRowSplitter(Grid targetGrid, int topRow, int bottomRow, int startCol = 0, int colSpan = 1)
        {
            var splitter = new SplitterElement
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 14,
                Margin = new Thickness(0, 0, 0, -7),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            Canvas.SetZIndex(splitter, 40);
            Grid.SetRow(splitter, topRow);
            Grid.SetColumn(splitter, startCol);
            if (colSpan > 1) Grid.SetColumnSpan(splitter, colSpan);

            splitter.SetCursor(InputSystemCursorShape.SizeNorthSouth);

            var line = new Rectangle
            {
                Height = 2,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 162, 237)), // Modern Accent Blue
                Opacity = 0.0
            };
            splitter.Children.Add(line);

            bool isDragging = false;
            double startY = 0;
            double startH0 = 0;
            double startH1 = 0;
            DateTime lastClickTime = DateTime.MinValue;

            splitter.PointerEntered += (s, e) =>
            {
                ShowHudAndOverlays();
                line.Opacity = 0.85;
            };

            splitter.PointerExited += (s, e) =>
            {
                if (!isDragging) line.Opacity = 0.0;
            };

            splitter.PointerPressed += (s, e) =>
            {
                var ptr = e.GetCurrentPoint(splitter);
                if (!ptr.Properties.IsLeftButtonPressed) return;

                var now = DateTime.Now;
                if ((now - lastClickTime).TotalMilliseconds < 350)
                {
                    // Double-click resets rows in this grid to equal 1.0 Star
                    if (topRow >= 0 && topRow < targetGrid.RowDefinitions.Count)
                        targetGrid.RowDefinitions[topRow].Height = new GridLength(1.0, GridUnitType.Star);
                    if (bottomRow >= 0 && bottomRow < targetGrid.RowDefinitions.Count)
                        targetGrid.RowDefinitions[bottomRow].Height = new GridLength(1.0, GridUnitType.Star);

                    line.Opacity = 0.0;
                    e.Handled = true;
                    return;
                }
                lastClickTime = now;

                // Normalize all rows in targetGrid to current actual pixel heights as Star values
                foreach (var row in targetGrid.RowDefinitions)
                {
                    double h = row.ActualHeight > 0 ? row.ActualHeight : 100.0;
                    row.Height = new GridLength(h, GridUnitType.Star);
                }

                if (topRow >= 0 && topRow < targetGrid.RowDefinitions.Count)
                    startH0 = targetGrid.RowDefinitions[topRow].Height.Value;
                if (bottomRow >= 0 && bottomRow < targetGrid.RowDefinitions.Count)
                    startH1 = targetGrid.RowDefinitions[bottomRow].Height.Value;

                startY = e.GetCurrentPoint(targetGrid).Position.Y;
                isDragging = true;
                _hudTimer.Stop();
                line.Opacity = 1.0;
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            splitter.PointerMoved += (s, e) =>
            {
                ShowHudAndOverlays();
                if (!isDragging) return;

                double currentY = e.GetCurrentPoint(targetGrid).Position.Y;
                double dy = currentY - startY;
                double totalH = startH0 + startH1;
                if (totalH <= 0) return;

                double minH = Math.Max(80.0, totalH * 0.15);
                double newH0 = Math.Clamp(startH0 + dy, minH, totalH - minH);
                double newH1 = totalH - newH0;

                if (topRow >= 0 && topRow < targetGrid.RowDefinitions.Count)
                    targetGrid.RowDefinitions[topRow].Height = new GridLength(newH0, GridUnitType.Star);
                if (bottomRow >= 0 && bottomRow < targetGrid.RowDefinitions.Count)
                    targetGrid.RowDefinitions[bottomRow].Height = new GridLength(newH1, GridUnitType.Star);

                e.Handled = true;
            };

            void EndDrag(PointerRoutedEventArgs e)
            {
                if (!isDragging) return;
                isDragging = false;
                splitter.ReleasePointerCapture(e.Pointer);
                line.Opacity = 0.0;
                _hudTimer.Start();
                e.Handled = true;
            }

            splitter.PointerReleased += (s, e) => EndDrag(e);
            splitter.PointerCaptureLost += (s, e) => EndDrag(e);

            targetGrid.Children.Add(splitter);
        }



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
            _cellElements.Add(cellGrid);

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

            if (item.Thumbnail != null)
            {
                var thumb = new Image
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

            _layoutUpdaters.Add(UpdateImageLayout);

            cellGrid.SizeChanged += (s, e) =>
            {
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    UpdateImageLayout();
                }
            };

            fullImg.ImageOpened += (s, e) =>
            {
                if (fullImg.Source is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
                {
                    naturalWidth = bs.PixelWidth;
                    naturalHeight = bs.PixelHeight;
                    UpdateImageLayout();
                }
            };

            try
            {
                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 2560;
                bmp.UriSource = new Uri(item.Path, UriKind.Absolute);
                bmp.ImageOpened += (s, e) =>
                {
                    if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                    {
                        naturalWidth = bmp.PixelWidth;
                        naturalHeight = bmp.PixelHeight;
                        UpdateImageLayout();
                    }
                };
                fullImg.Source = bmp;
            }
            catch { }

            imgContainer.Children.Add(fullImg);
            cellGrid.Children.Add(imgContainer);

            // Per-cell Pan & Zoom handling
            bool isDragging = false;
            bool isAltSwapDragging = false;
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
                        isAltSwapDragging = true;
                    }
                    else
                    {
                        isDragging = true;
                    }

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
                    if (isAltSwapDragging)
                    {
                        isAltSwapDragging = false;
                        cellGrid.ReleasePointerCapture(e.Pointer);
                        e.Handled = true;

                        Point releaseInGrid = e.GetCurrentPoint(CellsGrid).Position;
                        for (int i = 0; i < _cellElements.Count; i++)
                        {
                            var elem = _cellElements[i];
                            var t = elem.TransformToVisual(CellsGrid);
                            Point topLeft = t.TransformPoint(new Point(0, 0));
                            Rect cellRect = new Rect(topLeft.X, topLeft.Y, elem.ActualWidth, elem.ActualHeight);
                            if (cellRect.Contains(releaseInGrid) && i != index)
                            {
                                SwapItems(index, i);
                                break;
                            }
                        }
                        return;
                    }

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
                if (isDragging || isAltSwapDragging)
                {
                    isDragging = false;
                    isAltSwapDragging = false;
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
                Opacity = _isHudVisible ? 1.0 : 0.0,
                IsHitTestVisible = _isHudVisible
            };
            Canvas.SetZIndex(topControls, 60);
            topControls.PointerEntered += Hud_PointerEntered;
            topControls.PointerExited += Hud_PointerExited;
            _cellTopControls.Add(topControls);

            var topStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };

            // Maximize / Solo Button (Matches app's TopMaxButton style)
            var maxBtn = new Button
            {
                Width = 46,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE922", FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)) }
            };
            maxBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255));
            maxBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(16, 255, 255, 255));
            maxBtn.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            maxBtn.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255));
            ToolTipService.SetToolTip(maxBtn, $"Solo / Maximize (Key {index + 1})");
            maxBtn.Click += (s, e) => EnterSolo(index);
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

            // Right-Click Context Menu
            var menu = new MenuFlyout();

            if (_items.Count > 1)
            {
                if (index > 0)
                {
                    var moveLeftItem = new MenuFlyoutItem { Text = "Move Left / Up", Icon = new FontIcon { Glyph = "\uE76B" } };
                    moveLeftItem.Click += (s, e) => SwapItems(index, index - 1);
                    menu.Items.Add(moveLeftItem);
                }

                if (index < _items.Count - 1)
                {
                    var moveRightItem = new MenuFlyoutItem { Text = "Move Right / Down", Icon = new FontIcon { Glyph = "\uE76C" } };
                    moveRightItem.Click += (s, e) => SwapItems(index, index + 1);
                    menu.Items.Add(moveRightItem);
                }

                if (_items.Count == 2)
                {
                    var swapItem = new MenuFlyoutItem { Text = "Swap with Other Photo (S)", Icon = new FontIcon { Glyph = "\uE8AB" } };
                    swapItem.Click += (s, e) => SwapOrRotateItems();
                    menu.Items.Add(swapItem);
                }
                else
                {
                    var swapSub = new MenuFlyoutSubItem { Text = "Swap with...", Icon = new FontIcon { Glyph = "\uE8AB" } };
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i != index)
                        {
                            int targetIdx = i;
                            var targetItem = new MenuFlyoutItem { Text = $"Position {i + 1} ({_items[i].Name})" };
                            targetItem.Click += (s, e) => SwapItems(index, targetIdx);
                            swapSub.Items.Add(targetItem);
                        }
                    }
                    menu.Items.Add(swapSub);
                }

                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var soloItem = new MenuFlyoutItem { Text = $"Solo / Maximize (Key {index + 1})", Icon = new FontIcon { Glyph = "\uE922" } };
            soloItem.Click += (s, e) => EnterSolo(index);
            menu.Items.Add(soloItem);

            var removeItem = new MenuFlyoutItem { Text = "Remove from View", Icon = new FontIcon { Glyph = "\uE8BB" } };
            removeItem.Click += (s, e) => RemoveItem(item);
            menu.Items.Add(removeItem);

            cellGrid.ContextFlyout = menu;

            cellBorder.Child = cellGrid;
            return cellBorder;
        }

        private void RemoveItem(ImageItem item)
        {
            _items.Remove(item);
            if (_items.Count == 0)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                BuildLayout();
                ShowHudAndOverlays();
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
                    _isSoloDragging = true;
                    _lastSoloPoint = e.GetCurrentPoint(SoloImageContainer).Position;
                    _soloPointerId = e.Pointer.PointerId;
                    SoloImageContainer.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            SoloImageContainer.PointerMoved += (s, e) =>
            {
                ShowHudAndOverlays();

                if (_isSoloDragging && e.Pointer.PointerId == _soloPointerId)
                {
                    var currentPoint = e.GetCurrentPoint(SoloImageContainer).Position;
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
                if (_isSoloDragging && e.Pointer.PointerId == _soloPointerId)
                {
                    _isSoloDragging = false;
                    SoloImageContainer.ReleasePointerCapture(e.Pointer);
                    e.Handled = true;
                }
            };

            SoloImageContainer.PointerCanceled += (s, e) =>
            {
                if (_isSoloDragging)
                {
                    _isSoloDragging = false;
                    SoloImageContainer.ReleasePointerCapture(e.Pointer);
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

        private void OrientationToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleOrientation();
        }

        private void SwapButton_Click(object sender, RoutedEventArgs e)
        {
            SwapOrRotateItems();
        }

        public void SwapOrRotateItems()
        {
            if (_items.Count < 2) return;
            if (_items.Count == 2)
            {
                (_items[0], _items[1]) = (_items[1], _items[0]);
            }
            else
            {
                // Rotate: 0 -> 1 -> 2 -> ... -> 0
                var first = _items[0];
                _items.RemoveAt(0);
                _items.Add(first);
            }
            BuildLayout();
            ShowHudAndOverlays();
        }

        public void SwapItems(int idxA, int idxB)
        {
            if (idxA < 0 || idxA >= _items.Count || idxB < 0 || idxB >= _items.Count || idxA == idxB) return;
            (_items[idxA], _items[idxB]) = (_items[idxB], _items[idxA]);
            BuildLayout();
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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        private readonly Dictionary<string, CellBorderDto> _itemBorders = new(StringComparer.OrdinalIgnoreCase);
        private Flyout? _cellBorderFlyout;

        public CellBorderDto GetBorderForItem(ImageItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.Path))
                return new CellBorderDto { Color = "#FFFFFFFF", Thickness = 0, Padding = 0, CornerRadius = 0 };

            if (!_itemBorders.TryGetValue(item.Path, out var border))
            {
                border = new CellBorderDto { Color = "#FFFFFFFF", Thickness = 0, Padding = 0, CornerRadius = 0 };
                _itemBorders[item.Path] = border;
            }
            return border;
        }

        public void SetBorderForItem(ImageItem item, CellBorderDto border)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;
            _itemBorders[item.Path] = border.Clone();
            int slot = Slot(item);
            if (slot >= 0) ApplyBorderToCell(slot);
        }

        public static Color ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.FromArgb(0, 0, 0, 0);
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    return Color.FromArgb(255, r, g, b);
                }
            }
            else if (hex.Length == 8)
            {
                if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte a) &&
                    byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    return Color.FromArgb(a, r, g, b);
                }
            }
            return Color.FromArgb(255, 255, 255, 255);
        }

        public static SolidColorBrush ParseBorderBrush(string? hex)
        {
            return new SolidColorBrush(ParseColor(hex));
        }

        public void ApplyBorderToCell(int index)
        {
            if ((uint)index >= (uint)_cellRoots.Length) return;
            if (_cellRoots[index] is not Border cellBorder) return;
            if ((uint)index >= (uint)_items.Count) return;

            var item = _items[index];
            ApplyBorderToCellBorder(cellBorder, cellBorder.Child as Grid, item);
        }

        public void ApplyBorderToCellBorder(Border cellBorder, Grid? cellGrid, ImageItem item)
        {
            var border = GetBorderForItem(item);

            cellBorder.Margin = new Thickness(border.Padding);
            cellBorder.BorderThickness = new Thickness(border.Thickness);
            cellBorder.CornerRadius = new CornerRadius(border.CornerRadius);
            cellBorder.BorderBrush = ParseBorderBrush(border.Color);

            if (cellGrid != null)
            {
                cellGrid.Clip = new RectangleGeometry
                {
                    Rect = new Rect(0, 0, Math.Max(0, cellGrid.ActualWidth), Math.Max(0, cellGrid.ActualHeight))
                };
            }
        }

        public void ApplyBorderToAllCells()
        {
            int visible = VisibleCount(_items.Count);
            for (int i = 0; i < visible; i++)
            {
                ApplyBorderToCell(i);
            }
        }

        public void OpenCellBorderFlyout(ImageItem item)
        {
            int slot = Slot(item);
            if (slot < 0 || (uint)slot >= (uint)_cellRoots.Length) return;
            var targetElement = _cellRoots[slot] ?? (FrameworkElement)this;

            var border = GetBorderForItem(item);

            var panel = new StackPanel
            {
                Width = 320,
                Spacing = 12
            };

            // 1. Title Header
            var header = new TextBlock
            {
                Text = $"Frame & Border — Photo {slot + 1}",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _menuWhitePrimary
            };
            panel.Children.Add(header);

            // 2. Color Swatches
            var colorLabel = new TextBlock
            {
                Text = "Border Color",
                FontSize = 12,
                Foreground = _menuWhiteSecondary
            };
            panel.Children.Add(colorLabel);

            var swatchesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            (string label, string hex, Color previewColor)[] swatches = new[]
            {
                ("Clear", "#00000000", Color.FromArgb(0, 0, 0, 0)),
                ("White", "#FFFFFFFF", Color.FromArgb(255, 255, 255, 255)),
                ("Black", "#FF000000", Color.FromArgb(255, 0, 0, 0)),
                ("Charcoal", "#FF252525", Color.FromArgb(255, 37, 37, 37)),
                ("Gold", "#FFFFC83B", Color.FromArgb(255, 255, 200, 59)),
                ("Blue", "#FF0078D4", Color.FromArgb(255, 0, 120, 212)),
                ("Red", "#FFD13438", Color.FromArgb(255, 209, 52, 56))
            };

            var swatchButtons = new List<Button>();
            void UpdateSwatchSelection()
            {
                string currentHex = border.Color ?? "#FFFFFFFF";
                for (int i = 0; i < swatches.Length; i++)
                {
                    bool isMatch = string.Equals(swatches[i].hex, currentHex, StringComparison.OrdinalIgnoreCase);
                    swatchButtons[i].BorderBrush = isMatch ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                    swatchButtons[i].BorderThickness = isMatch ? new Thickness(2) : new Thickness(1);
                }
            }

            // References to sliders so presets can update them
            Slider? thicknessSliderRef = null;
            Slider? gapSliderRef = null;
            Slider? radiusSliderRef = null;

            foreach (var s in swatches)
            {
                var swatchBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(0),
                    Background = s.hex == "#00000000" ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) : new SolidColorBrush(s.previewColor),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    BorderThickness = new Thickness(1)
                };
                ToolTipService.SetToolTip(swatchBtn, s.label);
                string chosenHex = s.hex;
                swatchBtn.Click += (_, _) =>
                {
                    border.Color = chosenHex;
                    if (border.Thickness == 0 && chosenHex != "#00000000")
                    {
                        border.Thickness = 2;
                        if (thicknessSliderRef != null) thicknessSliderRef.Value = 2;
                    }
                    ApplyBorderToCell(slot);
                    UpdateSwatchSelection();
                };
                swatchButtons.Add(swatchBtn);
                swatchesPanel.Children.Add(swatchBtn);
            }
            UpdateSwatchSelection();
            panel.Children.Add(swatchesPanel);

            // 3. Thickness Slider
            var thicknessRow = CreateSliderRow("Thickness", 0, 24, border.Thickness, val =>
            {
                border.Thickness = val;
                ApplyBorderToCell(slot);
            }, out thicknessSliderRef);
            panel.Children.Add(thicknessRow);

            // 4. Spacing / Gap Slider
            var gapRow = CreateSliderRow("Spacing (Gap)", 0, 32, border.Padding, val =>
            {
                border.Padding = val;
                ApplyBorderToCell(slot);
            }, out gapSliderRef);
            panel.Children.Add(gapRow);

            // 5. Corner Radius Slider
            var radiusRow = CreateSliderRow("Corner Radius", 0, 32, border.CornerRadius, val =>
            {
                border.CornerRadius = val;
                ApplyBorderToCell(slot);
            }, out radiusSliderRef);
            panel.Children.Add(radiusRow);

            // 6. Quick Presets
            var presetsLabel = new TextBlock
            {
                Text = "Quick Presets",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            panel.Children.Add(presetsLabel);

            var presetsGrid = new Grid { ColumnSpacing = 6 };
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var flushBtn = CreateSmallPresetButton("Flush", () =>
            {
                border.Thickness = 0;
                border.Padding = 0;
                border.CornerRadius = 0;
                if (thicknessSliderRef != null) thicknessSliderRef.Value = 0;
                if (gapSliderRef != null) gapSliderRef.Value = 0;
                if (radiusSliderRef != null) radiusSliderRef.Value = 0;
                ApplyBorderToCell(slot);
            });
            Grid.SetColumn(flushBtn, 0);
            presetsGrid.Children.Add(flushBtn);

            var cardBtn = CreateSmallPresetButton("Card", () =>
            {
                border.Thickness = 1;
                border.Padding = 8;
                border.CornerRadius = 12;
                border.Color = "#FFFFFFFF";
                if (thicknessSliderRef != null) thicknessSliderRef.Value = 1;
                if (gapSliderRef != null) gapSliderRef.Value = 8;
                if (radiusSliderRef != null) radiusSliderRef.Value = 12;
                UpdateSwatchSelection();
                ApplyBorderToCell(slot);
            });
            Grid.SetColumn(cardBtn, 1);
            presetsGrid.Children.Add(cardBtn);

            var matBtn = CreateSmallPresetButton("Mat", () =>
            {
                border.Thickness = 8;
                border.Padding = 10;
                border.CornerRadius = 0;
                border.Color = "#FFFFFFFF";
                if (thicknessSliderRef != null) thicknessSliderRef.Value = 8;
                if (gapSliderRef != null) gapSliderRef.Value = 10;
                if (radiusSliderRef != null) radiusSliderRef.Value = 0;
                UpdateSwatchSelection();
                ApplyBorderToCell(slot);
            });
            Grid.SetColumn(matBtn, 2);
            presetsGrid.Children.Add(matBtn);

            var roundBtn = CreateSmallPresetButton("Round", () =>
            {
                border.Thickness = 3;
                border.Padding = 6;
                border.CornerRadius = 16;
                border.Color = "#FFFFFFFF";
                if (thicknessSliderRef != null) thicknessSliderRef.Value = 3;
                if (gapSliderRef != null) gapSliderRef.Value = 6;
                if (radiusSliderRef != null) radiusSliderRef.Value = 16;
                UpdateSwatchSelection();
                ApplyBorderToCell(slot);
            });
            Grid.SetColumn(roundBtn, 3);
            presetsGrid.Children.Add(roundBtn);

            panel.Children.Add(presetsGrid);

            // 7. Action Buttons
            panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = _menuDividerBrush, Margin = new Thickness(0, 4, 0, 4) });

            var applyAllBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE738", FontSize = 13, Foreground = _menuWhitePrimary },
                        new TextBlock { Text = "Apply to All Photos", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _menuWhitePrimary }
                    }
                }
            };
            applyAllBtn.Click += (_, _) =>
            {
                foreach (var it in _items)
                {
                    if (!string.IsNullOrEmpty(it.Path))
                    {
                        _itemBorders[it.Path] = border.Clone();
                    }
                }
                ApplyBorderToAllCells();
            };
            panel.Children.Add(applyAllBtn);

            _cellBorderFlyout = new Flyout
            {
                Content = panel,
                Placement = FlyoutPlacementMode.Top
            };
            _cellBorderFlyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(10)),
                    new Setter(FlyoutPresenter.PaddingProperty, new Thickness(14, 12, 14, 12)),
                    new Setter(FlyoutPresenter.WidthProperty, 348.0),
                    new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(242, 30, 30, 30))),
                    new Setter(FlyoutPresenter.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(64, 255, 255, 255))),
                    new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)),
                    new Setter(FlyoutPresenter.RequestedThemeProperty, ElementTheme.Dark)
                }
            };

            _cellBorderFlyout.ShowAt(targetElement);
        }

        private FrameworkElement CreateSliderRow(string label, double min, double max, double initialValue, Action<double> onValueChanged, out Slider sliderRef)
        {
            var panel = new StackPanel { Spacing = 2 };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = _menuWhiteSecondary
            };
            Grid.SetColumn(title, 0);
            headerGrid.Children.Add(title);

            var valueLabel = new TextBlock
            {
                Text = $"{(int)initialValue} px",
                FontSize = 12,
                Foreground = _menuWhitePrimary,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            Grid.SetColumn(valueLabel, 1);
            headerGrid.Children.Add(valueLabel);

            panel.Children.Add(headerGrid);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = initialValue,
                StepFrequency = 1,
                SnapsTo = SliderSnapsTo.StepValues,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, -2, 0, 0)
            };
            slider.ValueChanged += (_, e) =>
            {
                valueLabel.Text = $"{(int)e.NewValue} px";
                onValueChanged(e.NewValue);
            };

            sliderRef = slider;
            panel.Children.Add(slider);
            return panel;
        }

        private Button CreateSmallPresetButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = text, FontSize = 11, Foreground = _menuWhitePrimary },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(4, 3, 4, 3),
                MinHeight = 26,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}

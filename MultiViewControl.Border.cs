using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        private readonly Dictionary<string, CellBorderDto> _itemBorders = new(StringComparer.OrdinalIgnoreCase);
        private static CellBorderDto? _lastBorderSettings;
        private Flyout? _cellBorderFlyout;

        public CellBorderDto GetBorderForItem(ImageItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.Path))
                return new CellBorderDto { Color = "#FFFFFFFF", Thickness = 0, Padding = 0, CornerRadius = 0, StrokeStyle = "Solid", ShadowIntensity = 0 };

            if (!_itemBorders.TryGetValue(item.Path, out var border))
            {
                border = new CellBorderDto { Color = "#FFFFFFFF", Thickness = 0, Padding = 0, CornerRadius = 0, StrokeStyle = "Solid", ShadowIntensity = 0 };
                _itemBorders[item.Path] = border;
            }
            return border;
        }

        public void SetBorderForItem(ImageItem item, CellBorderDto border)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;
            _itemBorders[item.Path] = border.Clone();
            _lastBorderSettings = border.Clone();
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
            var root = _cellRoots[index];
            if (root == null) return;
            Border? cellBorder = root as Border;
            if (cellBorder == null && root is Grid slotGrid)
            {
                foreach (var child in slotGrid.Children)
                {
                    if (child is Border b && b.Child is Grid)
                    {
                        cellBorder = b;
                        break;
                    }
                }
            }
            if (cellBorder == null) return;
            if ((uint)index >= (uint)_items.Count) return;

            var item = _items[index];
            ApplyBorderToCellBorder(cellBorder, cellBorder.Child as Grid, item);
        }

        public void ApplyBorderToCellBorder(Border cellBorder, Grid? cellGrid, ImageItem item)
        {
            var border = GetBorderForItem(item);

            cellBorder.Margin = new Thickness(border.Padding);
            cellBorder.CornerRadius = new CornerRadius(border.CornerRadius);

            // Dashed overlay rectangle, Rim Glow overlay, & Gutter Host reference
            Microsoft.UI.Xaml.Shapes.Rectangle? dashedRect = null;
            Microsoft.UI.Xaml.Shapes.Rectangle? rimGlowRect = null;
            Border? gutterHost = null;
            if (cellBorder.Tag is CellParts parts)
            {
                dashedRect = parts.DashedStroke;
                rimGlowRect = parts.RimGlowStroke;
                gutterHost = parts.GutterHost;
            }

            // Gutter Host Material & Color styling
            if (gutterHost != null)
            {
                string mat = border.GutterMaterial ?? "None";
                if (string.Equals(mat, "Acrylic", StringComparison.OrdinalIgnoreCase))
                {
                    gutterHost.Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
                    gutterHost.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                    gutterHost.BorderThickness = new Thickness(1);
                    gutterHost.CornerRadius = new CornerRadius(Math.Max(4, border.CornerRadius + 2));
                }
                else if (string.Equals(mat, "Solid", StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(border.GutterColor) && border.GutterColor != "#00000000"))
                {
                    Color matColor = ParseColor(border.GutterColor ?? "#FFF8F8F8");
                    gutterHost.Background = new SolidColorBrush(matColor);
                    gutterHost.BorderBrush = null;
                    gutterHost.BorderThickness = new Thickness(0);
                    gutterHost.CornerRadius = new CornerRadius(border.CornerRadius);
                }
                else // None / Void
                {
                    gutterHost.Background = null;
                    gutterHost.BorderBrush = null;
                    gutterHost.BorderThickness = new Thickness(0);
                }
            }

            // Shadow & Rim Glow
            if (border.ShadowIntensity > 0)
            {
                if (border.RimGlow)
                {
                    cellBorder.Shadow = null;
                    cellBorder.Translation = Vector3.Zero;
                    if (rimGlowRect != null)
                    {
                        rimGlowRect.Visibility = Visibility.Visible;
                        byte alpha = (byte)Math.Clamp(border.ShadowIntensity * 2.1, 30, 240);
                        rimGlowRect.Stroke = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
                        rimGlowRect.StrokeThickness = Math.Max(1.0, border.ShadowIntensity * 0.035 + 1.0);
                        rimGlowRect.RadiusX = border.CornerRadius;
                        rimGlowRect.RadiusY = border.CornerRadius;
                    }
                }
                else
                {
                    if (rimGlowRect != null) rimGlowRect.Visibility = Visibility.Collapsed;
                    cellBorder.Shadow = new ThemeShadow();
                    cellBorder.Translation = new Vector3(0, 0, (float)(border.ShadowIntensity * 0.32f));
                }
            }
            else
            {
                if (rimGlowRect != null) rimGlowRect.Visibility = Visibility.Collapsed;
                cellBorder.Shadow = null;
                cellBorder.Translation = Vector3.Zero;
            }

            // Stroke style application
            string style = border.StrokeStyle ?? "Solid";
            if (string.Equals(style, "Dashed", StringComparison.OrdinalIgnoreCase))
            {
                cellBorder.BorderThickness = new Thickness(0);
                cellBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

                if (dashedRect != null)
                {
                    dashedRect.Visibility = Visibility.Visible;
                    dashedRect.Stroke = ParseBorderBrush(border.Color);
                    dashedRect.StrokeThickness = Math.Max(1, border.Thickness);
                    dashedRect.StrokeDashArray = new DoubleCollection { 3, 2 };
                    dashedRect.RadiusX = border.CornerRadius;
                    dashedRect.RadiusY = border.CornerRadius;
                }
            }
            else
            {
                if (dashedRect != null) dashedRect.Visibility = Visibility.Collapsed;

                if (string.Equals(style, "InnerMuted", StringComparison.OrdinalIgnoreCase))
                {
                    cellBorder.BorderThickness = new Thickness(1);
                    if (Application.Current.Resources.TryGetValue("ControlElevationBorderBrush", out var brushObj) && brushObj is Brush b)
                    {
                        cellBorder.BorderBrush = b;
                    }
                    else
                    {
                        var lgb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                        lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(36, 255, 255, 255), Offset = 0.0 });
                        lgb.GradientStops.Add(new GradientStop { Color = Color.FromArgb(14, 255, 255, 255), Offset = 1.0 });
                        cellBorder.BorderBrush = lgb;
                    }
                }
                else if (string.Equals(style, "Gradient", StringComparison.OrdinalIgnoreCase))
                {
                    cellBorder.BorderThickness = new Thickness(Math.Max(1, border.Thickness));
                    var primary = ParseColor(border.Color);
                    Color secondary;
                    if (!string.IsNullOrEmpty(border.SecondaryColor) && border.SecondaryColor != "#00000000")
                    {
                        secondary = ParseColor(border.SecondaryColor);
                    }
                    else
                    {
                        secondary = Color.FromArgb((byte)(primary.A * 0.40f), primary.R, primary.G, primary.B);
                    }
                    var lgb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                    lgb.GradientStops.Add(new GradientStop { Color = primary, Offset = 0.0 });
                    lgb.GradientStops.Add(new GradientStop { Color = secondary, Offset = 1.0 });
                    cellBorder.BorderBrush = lgb;
                }
                else // Solid
                {
                    cellBorder.BorderThickness = new Thickness(border.Thickness);
                    cellBorder.BorderBrush = ParseBorderBrush(border.Color);
                }
            }

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
                Width = 330,
                Spacing = 10
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

            // 2. Stroke Style Selector
            var strokeLabel = new TextBlock
            {
                Text = "Stroke Style",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            panel.Children.Add(strokeLabel);

            var strokeGrid = new Grid { ColumnSpacing = 4 };
            strokeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            strokeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            strokeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            strokeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string[] strokeStyles = new[] { "Solid", "Dashed", "InnerMuted", "Gradient" };
            string[] strokeDisplay = new[] { "Solid", "Dashed", "Muted", "Gradient" };
            var strokeButtons = new List<Button>();

            // Sliders reference declaration
            Slider? thicknessSliderRef = null;
            Slider? gapSliderRef = null;
            Slider? radiusSliderRef = null;
            Slider? shadowSliderRef = null;
            TextBlock? color1LabelRef = null;
            StackPanel? color2ContainerRef = null;
            Action? updateSwatchesAction = null;
            Action? updateSecondarySwatchesAction = null;

            void UpdateStrokeSelection()
            {
                string cur = border.StrokeStyle ?? "Solid";
                bool isGrad = string.Equals(cur, "Gradient", StringComparison.OrdinalIgnoreCase);

                for (int i = 0; i < strokeStyles.Length; i++)
                {
                    bool sel = string.Equals(cur, strokeStyles[i], StringComparison.OrdinalIgnoreCase);
                    strokeButtons[i].Background = sel ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                    strokeButtons[i].BorderBrush = sel ? new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                }

                if (color1LabelRef != null) color1LabelRef.Text = isGrad ? "Gradient Color 1" : "Border Color";
                if (color2ContainerRef != null) color2ContainerRef.Visibility = isGrad ? Visibility.Visible : Visibility.Collapsed;
            }

            for (int i = 0; i < strokeStyles.Length; i++)
            {
                string sName = strokeStyles[i];
                var sBtn = new Button
                {
                    Content = new TextBlock { Text = strokeDisplay[i], FontSize = 11, Foreground = _menuWhitePrimary },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(2, 4, 2, 4),
                    MinHeight = 28,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1)
                };
                ToolTipService.SetToolTip(sBtn, sName switch
                {
                    "Solid" => "Solid line border",
                    "Dashed" => "Dashed vector border",
                    "InnerMuted" => "1px semi-transparent Fluent elevation border",
                    "Gradient" => "Two-stop gradient frame effect",
                    _ => sName
                });
                sBtn.Click += (_, _) =>
                {
                    border.StrokeStyle = sName;
                    if (sName == "InnerMuted")
                    {
                        border.Thickness = 1;
                        if (thicknessSliderRef != null) thicknessSliderRef.Value = 1;
                    }
                    else if (border.Thickness == 0)
                    {
                        border.Thickness = 2;
                        if (thicknessSliderRef != null) thicknessSliderRef.Value = 2;
                    }
                    _lastBorderSettings = border.Clone();
                    ApplyBorderToCell(slot);
                    UpdateStrokeSelection();
                };
                Grid.SetColumn(sBtn, i);
                strokeGrid.Children.Add(sBtn);
                strokeButtons.Add(sBtn);
            }
            panel.Children.Add(strokeGrid);

            // 3. General Color & Surface Palette (Line / Stroke vs Gutter / Mat)
            var paletteHeader = new Grid();
            paletteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            paletteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var paletteTitle = new TextBlock
            {
                Text = "Palette Target",
                FontSize = 11,
                Foreground = _menuWhiteSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(paletteTitle, 0);
            paletteHeader.Children.Add(paletteTitle);

            var targetSelector = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            var lineTargetBtn = new Button
            {
                Content = new TextBlock { Text = "Line / Stroke", FontSize = 10.5 },
                Padding = new Thickness(7, 3, 7, 3),
                MinHeight = 24,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1)
            };
            var gutterTargetBtn = new Button
            {
                Content = new TextBlock { Text = "Gutter / Mat", FontSize = 10.5 },
                Padding = new Thickness(7, 3, 7, 3),
                MinHeight = 24,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1)
            };
            targetSelector.Children.Add(lineTargetBtn);
            targetSelector.Children.Add(gutterTargetBtn);
            Grid.SetColumn(targetSelector, 1);
            paletteHeader.Children.Add(targetSelector);
            panel.Children.Add(paletteHeader);

            var lineContainer = new StackPanel { Spacing = 6 };
            var gutterContainer = new StackPanel { Spacing = 6 };

            Action? updateMatSelectionAction = null;
            Action? updateGutterSwatchesAction = null;
            Button? matIndicatorBtnRef = null;

            void UpdateMatIndicator()
            {
                if (matIndicatorBtnRef == null) return;
                string m = border.GutterMaterial ?? "None";
                string summary = m switch
                {
                    "Acrylic" => "Mat: Frosted Acrylic",
                    "Solid" => string.Equals(border.GutterColor, "#FFF8F8F8", StringComparison.OrdinalIgnoreCase) ? "Mat: Gallery White" :
                               string.Equals(border.GutterColor, "#FFF3EFE6", StringComparison.OrdinalIgnoreCase) ? "Mat: Warm Cream" :
                               string.Equals(border.GutterColor, "#FFE0E0E0", StringComparison.OrdinalIgnoreCase) ? "Mat: Studio Gray" :
                               string.Equals(border.GutterColor, "#FF262626", StringComparison.OrdinalIgnoreCase) ? "Mat: Charcoal" :
                               string.Equals(border.GutterColor, "#FF000000", StringComparison.OrdinalIgnoreCase) ? "Mat: Black" :
                               string.Equals(border.GutterColor, "#FF182230", StringComparison.OrdinalIgnoreCase) ? "Mat: Navy" :
                               string.Equals(border.GutterColor, "#FFFFC83B", StringComparison.OrdinalIgnoreCase) ? "Mat: Gold" : "Mat: Solid Color",
                    _ => "Mat: Void (Canvas)"
                };
                matIndicatorBtnRef.Content = new TextBlock { Text = summary, FontSize = 10.5, Foreground = _menuWhitePrimary };
            }

            void SwitchPaletteTarget(bool toLine)
            {
                lineContainer.Visibility = toLine ? Visibility.Visible : Visibility.Collapsed;
                gutterContainer.Visibility = toLine ? Visibility.Collapsed : Visibility.Visible;

                lineTargetBtn.Background = toLine ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
                lineTargetBtn.BorderBrush = toLine ? new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                lineTargetBtn.Foreground = toLine ? _menuWhitePrimary : _menuWhiteSecondary;

                gutterTargetBtn.Background = !toLine ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
                gutterTargetBtn.BorderBrush = !toLine ? new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                gutterTargetBtn.Foreground = !toLine ? _menuWhitePrimary : _menuWhiteSecondary;
            }

            lineTargetBtn.Click += (_, _) => SwitchPaletteTarget(true);
            gutterTargetBtn.Click += (_, _) => SwitchPaletteTarget(false);

            // --- 3a. Line / Stroke Controls ---
            var colorLabel = new TextBlock
            {
                Text = "Border Color",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            color1LabelRef = colorLabel;
            lineContainer.Children.Add(colorLabel);

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
                ("Red", "#FFD13438", Color.FromArgb(255, 209, 52, 56)),
                ("Cyan", "#FF00B7C3", Color.FromArgb(255, 0, 183, 195))
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
            updateSwatchesAction = UpdateSwatchSelection;

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
                if (s.hex == "#00000000")
                {
                    swatchBtn.Content = new FontIcon
                    {
                        Glyph = "",
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        Foreground = _menuWhiteSecondary
                    };
                }
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
                    _lastBorderSettings = border.Clone();
                    ApplyBorderToCell(slot);
                    UpdateSwatchSelection();
                };
                swatchButtons.Add(swatchBtn);
                swatchesPanel.Children.Add(swatchBtn);
            }
            UpdateSwatchSelection();
            lineContainer.Children.Add(swatchesPanel);

            // Gradient Color 2 (Secondary Color)
            var color2Container = new StackPanel
            {
                Spacing = 4,
                Visibility = string.Equals(border.StrokeStyle, "Gradient", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed
            };
            color2ContainerRef = color2Container;

            var color2Label = new TextBlock
            {
                Text = "Gradient Color 2",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            color2Container.Children.Add(color2Label);

            var swatches2Panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            (string label, string hex, Color previewColor)[] swatches2 = new[]
            {
                ("Auto (Fade)", "#00000000", Color.FromArgb(0, 0, 0, 0)),
                ("White", "#FFFFFFFF", Color.FromArgb(255, 255, 255, 255)),
                ("Black", "#FF000000", Color.FromArgb(255, 0, 0, 0)),
                ("Charcoal", "#FF252525", Color.FromArgb(255, 37, 37, 37)),
                ("Gold", "#FFFFC83B", Color.FromArgb(255, 255, 200, 59)),
                ("Blue", "#FF0078D4", Color.FromArgb(255, 0, 120, 212)),
                ("Red", "#FFD13438", Color.FromArgb(255, 209, 52, 56)),
                ("Cyan", "#FF00B7C3", Color.FromArgb(255, 0, 183, 195))
            };

            var swatch2Buttons = new List<Button>();
            void UpdateSecondarySwatchSelection()
            {
                string current2 = border.SecondaryColor ?? "#00000000";
                for (int i = 0; i < swatches2.Length; i++)
                {
                    bool isMatch = string.Equals(swatches2[i].hex, current2, StringComparison.OrdinalIgnoreCase);
                    swatch2Buttons[i].BorderBrush = isMatch ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                    swatch2Buttons[i].BorderThickness = isMatch ? new Thickness(2) : new Thickness(1);
                }
            }
            updateSecondarySwatchesAction = UpdateSecondarySwatchSelection;

            foreach (var s in swatches2)
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
                if (s.hex == "#00000000")
                {
                    swatchBtn.Content = new FontIcon
                    {
                        Glyph = "",
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        Foreground = _menuWhiteSecondary
                    };
                }
                ToolTipService.SetToolTip(swatchBtn, s.label);
                string chosenHex = s.hex;
                swatchBtn.Click += (_, _) =>
                {
                    border.SecondaryColor = chosenHex;
                    _lastBorderSettings = border.Clone();
                    ApplyBorderToCell(slot);
                    UpdateSecondarySwatchSelection();
                };
                swatch2Buttons.Add(swatchBtn);
                swatches2Panel.Children.Add(swatchBtn);
            }
            UpdateSecondarySwatchSelection();
            color2Container.Children.Add(swatches2Panel);
            lineContainer.Children.Add(color2Container);
            panel.Children.Add(lineContainer);

            // --- 3b. Gutter / Mat Surface Controls ---
            var matLabel = new TextBlock
            {
                Text = "Mat Material",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            gutterContainer.Children.Add(matLabel);

            var matGrid = new Grid { ColumnSpacing = 4 };
            matGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            matGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            matGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string[] matTypes = new[] { "None", "Solid", "Acrylic" };
            string[] matDisplay = new[] { "None / Void", "Solid Mat", "Frosted Acrylic" };
            var matButtons = new List<Button>();

            void UpdateMatSelection()
            {
                string curMat = border.GutterMaterial ?? "None";
                for (int i = 0; i < matTypes.Length; i++)
                {
                    bool sel = string.Equals(curMat, matTypes[i], StringComparison.OrdinalIgnoreCase);
                    matButtons[i].Background = sel ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                    matButtons[i].BorderBrush = sel ? new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                }
                UpdateMatIndicator();
            }
            updateMatSelectionAction = UpdateMatSelection;

            for (int i = 0; i < matTypes.Length; i++)
            {
                string mName = matTypes[i];
                var mBtn = new Button
                {
                    Content = new TextBlock { Text = matDisplay[i], FontSize = 10.5, Foreground = _menuWhitePrimary },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(2, 4, 2, 4),
                    MinHeight = 26,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1)
                };
                mBtn.Click += (_, _) =>
                {
                    border.GutterMaterial = mName;
                    if (mName == "Solid" && (string.IsNullOrEmpty(border.GutterColor) || border.GutterColor == "#00000000"))
                    {
                        border.GutterColor = "#FFF8F8F8";
                    }
                    if (border.Padding == 0 && mName != "None")
                    {
                        border.Padding = 16;
                        if (gapSliderRef != null) gapSliderRef.Value = 16;
                    }
                    _lastBorderSettings = border.Clone();
                    ApplyBorderToCell(slot);
                    UpdateMatSelection();
                    updateGutterSwatchesAction?.Invoke();
                };
                Grid.SetColumn(mBtn, i);
                matGrid.Children.Add(mBtn);
                matButtons.Add(mBtn);
            }
            gutterContainer.Children.Add(matGrid);

            var gutterColorLabel = new TextBlock
            {
                Text = "Mat Color",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            gutterContainer.Children.Add(gutterColorLabel);

            var gutterSwatchesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            (string label, string hex, Color previewColor)[] gutterSwatches = new[]
            {
                ("Void", "#00000000", Color.FromArgb(0, 0, 0, 0)),
                ("Gallery White", "#FFF8F8F8", Color.FromArgb(255, 248, 248, 248)),
                ("Warm Cream", "#FFF3EFE6", Color.FromArgb(255, 243, 239, 230)),
                ("Studio Gray", "#FFE0E0E0", Color.FromArgb(255, 224, 224, 224)),
                ("Charcoal", "#FF262626", Color.FromArgb(255, 38, 38, 38)),
                ("Pure Black", "#FF000000", Color.FromArgb(255, 0, 0, 0)),
                ("Deep Navy", "#FF182230", Color.FromArgb(255, 24, 34, 48)),
                ("Gold Accent", "#FFFFC83B", Color.FromArgb(255, 255, 200, 59))
            };

            var gutterSwatchButtons = new List<Button>();
            void UpdateGutterSwatchSelection()
            {
                string currentMatHex = border.GutterColor ?? "#00000000";
                for (int i = 0; i < gutterSwatches.Length; i++)
                {
                    bool isMatch = string.Equals(gutterSwatches[i].hex, currentMatHex, StringComparison.OrdinalIgnoreCase);
                    gutterSwatchButtons[i].BorderBrush = isMatch ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                    gutterSwatchButtons[i].BorderThickness = isMatch ? new Thickness(2) : new Thickness(1);
                }
                UpdateMatIndicator();
            }
            updateGutterSwatchesAction = UpdateGutterSwatchSelection;

            foreach (var s in gutterSwatches)
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
                if (s.hex == "#00000000")
                {
                    swatchBtn.Content = new FontIcon
                    {
                        Glyph = "",
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        Foreground = _menuWhiteSecondary
                    };
                }
                ToolTipService.SetToolTip(swatchBtn, s.label);
                string chosenHex = s.hex;
                swatchBtn.Click += (_, _) =>
                {
                    border.GutterColor = chosenHex;
                    if (chosenHex == "#00000000")
                    {
                        border.GutterMaterial = "None";
                    }
                    else if (border.GutterMaterial == "None")
                    {
                        border.GutterMaterial = "Solid";
                    }
                    if (border.Padding == 0 && chosenHex != "#00000000")
                    {
                        border.Padding = 16;
                        if (gapSliderRef != null) gapSliderRef.Value = 16;
                    }
                    _lastBorderSettings = border.Clone();
                    ApplyBorderToCell(slot);
                    UpdateMatSelection();
                    UpdateGutterSwatchSelection();
                };
                gutterSwatchButtons.Add(swatchBtn);
                gutterSwatchesPanel.Children.Add(swatchBtn);
            }
            UpdateGutterSwatchSelection();
            gutterContainer.Children.Add(gutterSwatchesPanel);
            panel.Children.Add(gutterContainer);

            UpdateMatSelection();
            SwitchPaletteTarget(true);
            UpdateStrokeSelection();

            // 4. Sliders (Thickness, Gutter, Corner Radius, Shadow Intensity + Rim Glow Toggle)
            var thicknessRow = CreateSliderRow("Thickness", 0, 24, border.Thickness, "px", val =>
            {
                border.Thickness = val;
                _lastBorderSettings = border.Clone();
                ApplyBorderToCell(slot);
            }, out thicknessSliderRef);
            panel.Children.Add(thicknessRow);

            var gutterRow = CreateSliderRow("Gutter", 0, 48, border.Padding, "px", val =>
            {
                border.Padding = val;
                _lastBorderSettings = border.Clone();
                ApplyBorderToCell(slot);
            }, out gapSliderRef);
            ToolTipService.SetToolTip(gutterRow, "Gutter spacing around this photo card (reveals mat/background)");

            // Mat indicator pill under Gutter slider
            var matIndicatorBtn = new Button
            {
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(3),
                MinHeight = 22,
                Margin = new Thickness(0, -2, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            matIndicatorBtnRef = matIndicatorBtn;
            UpdateMatIndicator();
            ToolTipService.SetToolTip(matIndicatorBtn, "Click to switch palette to Gutter / Mat settings");
            matIndicatorBtn.Click += (_, _) => SwitchPaletteTarget(false);
            if (gutterRow is Panel gp) gp.Children.Add(matIndicatorBtn);
            panel.Children.Add(gutterRow);

            var radiusRow = CreateSliderRow("Corner Radius", 0, 32, border.CornerRadius, "px", val =>
            {
                border.CornerRadius = val;
                _lastBorderSettings = border.Clone();
                ApplyBorderToCell(slot);
            }, out radiusSliderRef);
            panel.Children.Add(radiusRow);

            // 4b. Shadow Intensity with Ambient Rim Glow Toggle
            var shadowPanel = new StackPanel { Spacing = 4 };
            var shadowHeader = new Grid();
            shadowHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shadowHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var shadowLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var shadowTitle = new TextBlock { Text = "Shadow Intensity", FontSize = 11.5, Foreground = _menuWhiteSecondary, VerticalAlignment = VerticalAlignment.Center };
            var shadowVal = new TextBlock { Text = $"{(int)border.ShadowIntensity} %", FontSize = 11.5, Foreground = _menuWhitePrimary, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            shadowLeft.Children.Add(shadowTitle);
            shadowLeft.Children.Add(shadowVal);
            Grid.SetColumn(shadowLeft, 0);
            shadowHeader.Children.Add(shadowLeft);

            var glowToggle = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                IsChecked = border.RimGlow,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 24,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            var glowStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var glowIcon = new FontIcon { Glyph = "", FontSize = 11, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
            var glowText = new TextBlock { Text = "Rim Glow", FontSize = 11 };
            glowStack.Children.Add(glowIcon);
            glowStack.Children.Add(glowText);
            glowToggle.Content = glowStack;

            void SyncGlowToggle()
            {
                bool on = glowToggle.IsChecked == true;
                glowToggle.Background = on ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
                glowToggle.BorderBrush = on ? new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                glowIcon.Foreground = on ? new SolidColorBrush(Color.FromArgb(255, 255, 200, 59)) : _menuWhiteSecondary;
                glowText.Foreground = on ? _menuWhitePrimary : _menuWhiteSecondary;
            }
            SyncGlowToggle();

            glowToggle.Click += (_, _) =>
            {
                border.RimGlow = glowToggle.IsChecked == true;
                SyncGlowToggle();
                _lastBorderSettings = border.Clone();
                ApplyBorderToCell(slot);
            };
            ToolTipService.SetToolTip(glowToggle, "Toggle ambient luminous rim glow (ideal on dark backgrounds)");
            Grid.SetColumn(glowToggle, 1);
            shadowHeader.Children.Add(glowToggle);
            shadowPanel.Children.Add(shadowHeader);

            var shadowSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = border.ShadowIntensity,
                StepFrequency = 1,
                SnapsTo = SliderSnapsTo.StepValues,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, -2, 0, 0)
            };
            shadowSlider.ValueChanged += (_, e) =>
            {
                shadowVal.Text = $"{(int)e.NewValue} %";
                border.ShadowIntensity = e.NewValue;
                border.Shadow = e.NewValue > 0;
                _lastBorderSettings = border.Clone();
                ApplyBorderToCell(slot);
            };
            shadowSliderRef = shadowSlider;
            shadowPanel.Children.Add(shadowSlider);
            panel.Children.Add(shadowPanel);

            // 5. Quick Presets
            var presetsLabel = new TextBlock
            {
                Text = "Quick Presets",
                FontSize = 11,
                Foreground = _menuWhiteSecondary
            };
            panel.Children.Add(presetsLabel);

            var presetsGrid = new Grid { ColumnSpacing = 4 };
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            void ApplyPresetValues(CellBorderDto targetValues)
            {
                border.Thickness = targetValues.Thickness;
                border.Padding = targetValues.Padding;
                border.CornerRadius = targetValues.CornerRadius;
                border.Color = targetValues.Color;
                border.SecondaryColor = targetValues.SecondaryColor;
                border.StrokeStyle = targetValues.StrokeStyle ?? "Solid";
                border.ShadowIntensity = targetValues.ShadowIntensity;
                border.Shadow = targetValues.ShadowIntensity > 0;
                border.RimGlow = targetValues.RimGlow;
                border.GutterColor = targetValues.GutterColor ?? "#00000000";
                border.GutterMaterial = targetValues.GutterMaterial ?? "None";

                if (thicknessSliderRef != null) thicknessSliderRef.Value = border.Thickness;
                if (gapSliderRef != null) gapSliderRef.Value = border.Padding;
                if (radiusSliderRef != null) radiusSliderRef.Value = border.CornerRadius;
                if (shadowSliderRef != null) shadowSliderRef.Value = border.ShadowIntensity;

                glowToggle.IsChecked = border.RimGlow;
                SyncGlowToggle();

                UpdateStrokeSelection();
                updateMatSelectionAction?.Invoke();
                updateSwatchesAction?.Invoke();
                updateSecondarySwatchesAction?.Invoke();
                updateGutterSwatchesAction?.Invoke();
                UpdateMatIndicator();
                _lastBorderSettings = border.Clone();
                ApplyBorderToCell(slot);
            }

            var lastBtn = CreateSmallPresetButton(" Last", () =>
            {
                if (_lastBorderSettings != null)
                {
                    ApplyPresetValues(_lastBorderSettings);
                }
                else
                {
                    ApplyPresetValues(new CellBorderDto { Thickness = 1, Padding = 8, CornerRadius = 12, Color = "#FFFFFFFF", StrokeStyle = "InnerMuted", ShadowIntensity = 40, RimGlow = true });
                }
            }, isAccent: true);
            ToolTipService.SetToolTip(lastBtn, _lastBorderSettings != null
                ? $"Apply last used frame ({_lastBorderSettings.StrokeStyle}, {_lastBorderSettings.Thickness}px, Gutter {_lastBorderSettings.Padding}px)"
                : "Apply last used settings");
            Grid.SetColumn(lastBtn, 0);
            presetsGrid.Children.Add(lastBtn);

            var cardBtn = CreateSmallPresetButton("Card", () =>
            {
                ApplyPresetValues(new CellBorderDto { Thickness = 1, Padding = 8, CornerRadius = 12, Color = "#FFFFFFFF", StrokeStyle = "InnerMuted", ShadowIntensity = 40, RimGlow = true, GutterMaterial = "None", GutterColor = "#00000000" });
            });
            Grid.SetColumn(cardBtn, 1);
            presetsGrid.Children.Add(cardBtn);

            var matBtn = CreateSmallPresetButton("Mat", () =>
            {
                ApplyPresetValues(new CellBorderDto { Thickness = 1, Padding = 18, CornerRadius = 0, Color = "#FF000000", StrokeStyle = "Solid", ShadowIntensity = 45, RimGlow = false, GutterMaterial = "Solid", GutterColor = "#FFF8F8F8" });
            });
            Grid.SetColumn(matBtn, 2);
            presetsGrid.Children.Add(matBtn);

            var roundBtn = CreateSmallPresetButton("Round", () =>
            {
                ApplyPresetValues(new CellBorderDto { Thickness = 2, Padding = 8, CornerRadius = 16, Color = "#FFFFFFFF", SecondaryColor = "#FFFFC83B", StrokeStyle = "Gradient", ShadowIntensity = 30, RimGlow = true, GutterMaterial = "Acrylic", GutterColor = "#FFF8F8F8" });
            });
            Grid.SetColumn(roundBtn, 3);
            presetsGrid.Children.Add(roundBtn);

            var flushBtn = CreateSmallPresetButton("Flush", () =>
            {
                ApplyPresetValues(new CellBorderDto { Thickness = 0, Padding = 0, CornerRadius = 0, Color = "#00000000", StrokeStyle = "Solid", ShadowIntensity = 0, RimGlow = false, GutterMaterial = "None", GutterColor = "#00000000" });
            });
            Grid.SetColumn(flushBtn, 4);
            presetsGrid.Children.Add(flushBtn);

            panel.Children.Add(presetsGrid);            // 6. Action Buttons
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
                        new FontIcon { Glyph = "\uE8B7", FontSize = 13, Foreground = _menuWhitePrimary },
                        new TextBlock { Text = "Apply to All Photos", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _menuWhitePrimary }
                    }
                }
            };
            applyAllBtn.Click += (_, _) =>
            {
                var proto = border.Clone();
                _lastBorderSettings = proto.Clone();
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it != null && !string.IsNullOrEmpty(it.Path))
                    {
                        _itemBorders[it.Path] = proto.Clone();
                    }
                }
                ApplyBorderToAllCells();
                _cellBorderFlyout?.Hide();
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
                    new Setter(FlyoutPresenter.WidthProperty, 356.0),
                    new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(242, 30, 30, 30))),
                    new Setter(FlyoutPresenter.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(64, 255, 255, 255))),
                    new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)),
                    new Setter(FlyoutPresenter.RequestedThemeProperty, ElementTheme.Dark)
                }
            };

            _cellBorderFlyout.ShowAt(targetElement);
        }

        private FrameworkElement CreateSliderRow(string label, double min, double max, double initialValue, string unit, Action<double> onValueChanged, out Slider sliderRef)
        {
            var panel = new StackPanel { Spacing = 2 };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = _menuWhiteSecondary
            };
            Grid.SetColumn(title, 0);
            headerGrid.Children.Add(title);

            var valueLabel = new TextBlock
            {
                Text = $"{(int)initialValue} {unit}",
                FontSize = 11.5,
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
                valueLabel.Text = $"{(int)e.NewValue} {unit}";
                onValueChanged(e.NewValue);
            };

            sliderRef = slider;
            panel.Children.Add(slider);
            return panel;
        }

        private Button CreateSmallPresetButton(string text, Action onClick, bool isAccent = false)
        {
            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = isAccent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = isAccent ? new SolidColorBrush(Color.FromArgb(255, 255, 200, 59)) : _menuWhitePrimary
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(2, 4, 2, 4),
                MinHeight = 26,
                CornerRadius = new CornerRadius(4),
                Background = isAccent ? new SolidColorBrush(Color.FromArgb(40, 255, 200, 59)) : new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                BorderBrush = isAccent ? new SolidColorBrush(Color.FromArgb(120, 255, 200, 59)) : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}

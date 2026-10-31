using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Automation;
using Windows.Foundation;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        // Each cell has its own ImageItem, so two cells showing one file keep separate frames.
        private readonly Dictionary<ImageItem, CellBorderDto> _itemBorders = new();
        private static CellBorderDto? _lastBorderSettings;
        private CellBorderDto? _openFrameBorder;
        private int _frameFlyoutTicket;
        private Flyout? _cellBorderFlyout;

        public CellBorderDto GetBorderForItem(ImageItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.Path))
                return new CellBorderDto { Color = "#FFFFFFFF", Thickness = 0, Padding = 0, CornerRadius = 0, StrokeStyle = "Solid", ShadowIntensity = 0 };

            if (!_itemBorders.TryGetValue(item, out var border))
            {
                border = new CellBorderDto { Color = "#FFFFFFFF", Thickness = 0, Padding = 0, CornerRadius = 0, StrokeStyle = "Solid", ShadowIntensity = 0 };
                _itemBorders[item] = border;
            }
            return border;
        }

        public void SetBorderForItem(ImageItem item, CellBorderDto border)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;
            _itemBorders[item] = border.Clone();
            _lastBorderSettings = border.Clone();
            int slot = Slot(item);
            if (slot >= 0) ApplyBorderToCell(slot);
        }

        /// <summary>A cell's content is changing from one item to another. The cell keeps its frame and hidden state.</summary>
        private void MoveCellParameters(ImageItem from, ImageItem to)
        {
            if (ReferenceEquals(from, to)) return;
            if (_itemBorders.Remove(from, out var border))
                _itemBorders[to] = border;
            else
                _itemBorders.Remove(to);
            if (_hiddenItems.Remove(from))
                _hiddenItems.Add(to);
            else
                _hiddenItems.Remove(to);
            if (_cellGeometry.Remove(from, out var geometry))
                _cellGeometry[to] = geometry;
            else
                _cellGeometry.Remove(to);
        }

        /// <summary>Two cells are exchanging content. Each keeps its own frame and hidden state.</summary>
        private void ExchangeCellParameters(ImageItem a, ImageItem b)
        {
            bool hasA = _itemBorders.Remove(a, out var borderA);
            bool hasB = _itemBorders.Remove(b, out var borderB);
            if (hasA) _itemBorders[b] = borderA!;
            if (hasB) _itemBorders[a] = borderB!;
            bool hiddenA = _hiddenItems.Remove(a);
            bool hiddenB = _hiddenItems.Remove(b);
            if (hiddenA) _hiddenItems.Add(b);
            if (hiddenB) _hiddenItems.Add(a);
            bool turnedA = _cellGeometry.Remove(a, out var geometryA);
            bool turnedB = _cellGeometry.Remove(b, out var geometryB);
            if (turnedA) _cellGeometry[b] = geometryA!;
            if (turnedB) _cellGeometry[a] = geometryB!;
        }

        /// <summary>Show this cell's hidden state: the placeholder alone, or the image with its frame.</summary>
        private void ShowHiddenState(int slot)
        {
            if ((uint)slot >= (uint)_cellRoots.Length || (uint)slot >= (uint)_items.Count) return;
            if (_cellRoots[slot]?.Tag is not CellParts parts) return;
            bool hidden = _hiddenItems.Contains(_items[slot]);
            if (parts.ImageContainer != null)
                parts.ImageContainer.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            if (parts.HiddenPlaceholder != null)
                parts.HiddenPlaceholder.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
            parts.Bound.ApplyGeometry?.Invoke();
            ApplyBorderToCell(slot);
        }

        public static Color ParseColor(string? hex)
        {
            var c = FrameColor.Parse(hex);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        private static Color ToColor(FrameColor.Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        public static SolidColorBrush ParseBorderBrush(string? hex)
        {
            return new SolidColorBrush(ParseColor(hex));
        }

        public void ApplyBorderToCell(int index)
        {
            if ((uint)index >= (uint)_cellRoots.Length) return;
            var root = _cellRoots[index];
            if (root == null) return;
            Border? cellBorder = CardBorder(root);
            if (cellBorder == null) return;
            if ((uint)index >= (uint)_items.Count) return;

            var item = _items[index];
            ApplyBorderToCellBorder(cellBorder, cellBorder.Child as Grid, item);
        }

        public void ApplyBorderToCellBorder(Border cellBorder, Grid? cellGrid, ImageItem item)
        {
            var border = GetBorderForItem(item);

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

            bool isHidden = _hiddenItems.Contains(item);
            if (isHidden)
            {
                cellBorder.Margin = new Thickness(0);
                cellBorder.BorderThickness = new Thickness(0);
                cellBorder.BorderBrush = null;
                cellBorder.CornerRadius = new CornerRadius(0);
                cellBorder.Shadow = null;

                if (dashedRect != null) dashedRect.Visibility = Visibility.Collapsed;
                if (rimGlowRect != null) rimGlowRect.Visibility = Visibility.Collapsed;

                if (gutterHost != null)
                {
                    gutterHost.Margin = new Thickness(0);
                    gutterHost.Background = null;
                    gutterHost.BorderThickness = new Thickness(0);
                    gutterHost.BorderBrush = null;
                    gutterHost.CornerRadius = new CornerRadius(0);
                    gutterHost.Shadow = null;
                    if (gutterHost.Child is Image shadowImage)
                    {
                        shadowImage.Source = null;
                        shadowImage.Visibility = Visibility.Collapsed;
                    }
                }
                return;
            }

            cellBorder.Margin = new Thickness(border.Padding);
            cellBorder.CornerRadius = new CornerRadius(border.CornerRadius);

            // The mat fills the rectangular cell. The card above it is what rounds.
            if (gutterHost != null)
            {
                gutterHost.Margin = new Thickness(0);
                var mat = FrameColor.MatFill(border.GutterMaterial, border.GutterColor);
                gutterHost.BorderBrush = null;
                gutterHost.BorderThickness = new Thickness(0);
                gutterHost.CornerRadius = new CornerRadius(0);
                if (mat.A > 0)
                    gutterHost.Background = new SolidColorBrush(ToColor(mat));
                else
                    gutterHost.Background = null;
            }

            cellBorder.Shadow = null;
            ApplyDropShadow(gutterHost, border);

            if (border.ShadowIntensity > 0 && border.RimGlow)
            {
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
            else if (rimGlowRect != null)
            {
                rimGlowRect.Visibility = Visibility.Collapsed;
            }

            // Stroke style application
            string style = border.StrokeStyle ?? "Solid";
            if (string.Equals(style, "Dashed", StringComparison.OrdinalIgnoreCase))
            {
                cellBorder.BorderThickness = new Thickness(0);
                cellBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

                if (dashedRect != null)
                {
                    var dash = FrameColor.Parse(border.Color);
                    if (border.Thickness <= 0 || dash.A == 0)
                    {
                        dashedRect.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        dashedRect.Visibility = Visibility.Visible;
                        dashedRect.Stroke = new SolidColorBrush(ToColor(dash));
                        dashedRect.StrokeThickness = border.Thickness;
                        dashedRect.StrokeDashArray = new DoubleCollection { 3, 2 };
                        dashedRect.RadiusX = border.CornerRadius;
                        dashedRect.RadiusY = border.CornerRadius;
                    }
                }
            }
            else
            {
                if (dashedRect != null) dashedRect.Visibility = Visibility.Collapsed;

                if (string.Equals(style, "InnerMuted", StringComparison.OrdinalIgnoreCase))
                {
                    cellBorder.BorderThickness = new Thickness(Math.Max(0, border.Thickness));
                    cellBorder.BorderBrush = new SolidColorBrush(ToColor(FrameColor.MutedStroke(border.Color)));
                }
                else if (string.Equals(style, "Gradient", StringComparison.OrdinalIgnoreCase))
                {
                    cellBorder.BorderThickness = new Thickness(Math.Max(0, border.Thickness));
                    var primary = FrameColor.Parse(border.Color);
                    var secondary = FrameColor.GradientEnd(border.SecondaryColor, primary);
                    var lgb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                    lgb.GradientStops.Add(new GradientStop { Color = ToColor(primary), Offset = 0.0 });
                    lgb.GradientStops.Add(new GradientStop { Color = ToColor(secondary), Offset = 1.0 });
                    cellBorder.BorderBrush = lgb;
                }
                else // Solid
                {
                    cellBorder.BorderThickness = new Thickness(border.Thickness);
                    cellBorder.BorderBrush = ParseBorderBrush(border.Color);
                }
            }

        }

        public void ApplyBorderToAllCells()
        {
            for (int i = 0; i < _cellRoots.Length; i++)
            {
                ApplyBorderToCell(i);
            }
        }

        public void OpenCellBorderFlyout(ImageItem item)
        {
            int slot = Slot(item);
            if (slot < 0 || (uint)slot >= (uint)_cellRoots.Length) return;
            var targetElement = _cellRoots[slot] ?? (FrameworkElement)this;

            // Remember the frame that was open, then ignore that panel's close so it cannot overwrite this one.
            if (_openFrameBorder != null)
                _lastBorderSettings = _openFrameBorder.Clone();
            int ticket = ++_frameFlyoutTicket;
            _cellBorderFlyout?.Hide();

            var border = GetBorderForItem(item);
            _openFrameBorder = border;
            CellBorderDto? remembered = _lastBorderSettings?.Clone();
            bool syncing = false;

            Action? refreshColor = null;
            Action? refreshEnd = null;
            Action? refreshMatColor = null;
            Action<int>? showStroke = null;
            Action<int>? showPreset = null;
            Action<int>? showMat = null;
            TextBlock? colorLabel = null;
            StackPanel? endBlock = null;
            ToggleButton? glowToggle = null;
            FontIcon? glowIcon = null;
            TextBlock? glowText = null;

            var thicknessRow = CreateSliderRow("Thickness", 0, 24, border.Thickness, "px", value =>
            {
                if (syncing) return;
                border.Thickness = value;
                Touch();
            }, out Slider thicknessSlider);
            var radiusRow = CreateSliderRow("Corner radius", 0, 32, border.CornerRadius, "px", value =>
            {
                if (syncing) return;
                border.CornerRadius = value;
                Touch();
            }, out Slider radiusSlider);
            var gutterRow = CreateSliderRow("Gutter", 0, 48, border.Padding, "px", value =>
            {
                if (syncing) return;
                border.Padding = value;
                Touch();
            }, out Slider gutterSlider);
            Slider shadowSlider = null!;
            TextBlock shadowValue = null!;
            ToolTipService.SetToolTip(gutterRow, "How far this photo sits inside its cell. The photo beside it keeps its own inset.");

            void StyleGlow()
            {
                if (glowToggle == null || glowIcon == null || glowText == null) return;
                bool on = glowToggle.IsChecked == true;
                var fill = new SolidColorBrush(on ? Color.FromArgb(255, 62, 62, 62) : Color.FromArgb(0, 0, 0, 0));
                var edge = new SolidColorBrush(on ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(48, 255, 255, 255));
                glowToggle.Background = fill;
                glowToggle.BorderBrush = edge;
                glowToggle.Resources["ToggleButtonBackground"] = fill;
                glowToggle.Resources["ToggleButtonBackgroundChecked"] = fill;
                glowToggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = fill;
                glowToggle.Resources["ToggleButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
                glowToggle.Resources["ToggleButtonBorderBrush"] = edge;
                glowToggle.Resources["ToggleButtonBorderBrushChecked"] = edge;
                glowIcon.Foreground = on ? _menuWhitePrimary : _menuWhiteSecondary;
                glowText.Foreground = on ? _menuWhitePrimary : _menuWhiteSecondary;
                glowText.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }

            void RefreshPanel()
            {
                showStroke?.Invoke(FrameIndex(new[] { "Solid", "Dashed", "InnerMuted", "Gradient" }, border.StrokeStyle));
                showPreset?.Invoke(FrameLooks.Match(border));
                showMat?.Invoke(FrameIndex(new[] { "None", "Solid", "Acrylic" }, border.GutterMaterial));
                refreshColor?.Invoke();
                refreshEnd?.Invoke();
                refreshMatColor?.Invoke();
                bool gradient = string.Equals(border.StrokeStyle, "Gradient", StringComparison.OrdinalIgnoreCase);
                if (colorLabel != null) colorLabel.Text = gradient ? "Start color" : "Color";
                if (endBlock != null) endBlock.Visibility = gradient ? Visibility.Visible : Visibility.Collapsed;
                if (glowToggle != null)
                {
                    glowToggle.IsChecked = border.RimGlow;
                    StyleGlow();
                }
            }

            void Touch()
            {
                ApplyBorderToCell(slot);
                RefreshPanel();
            }

            void ApplyLook(CellBorderDto source)
            {
                syncing = true;
                CopyFrame(border, source);
                thicknessSlider.Value = border.Thickness;
                radiusSlider.Value = border.CornerRadius;
                gutterSlider.Value = border.Padding;
                shadowSlider.Value = border.ShadowIntensity;
                syncing = false;
                Touch();
            }

            var body = new StackPanel { Spacing = 10, MinWidth = 300 };

            var presetHeader = new Grid { VerticalAlignment = VerticalAlignment.Center };
            presetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var presetLabel = SectionLabel("Presets");
            presetLabel.Margin = new Thickness(0);
            presetHeader.Children.Add(presetLabel);
            var lastButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "\uE823",
                            FontSize = 12,
                            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                            Foreground = _menuWhitePrimary
                        },
                        new TextBlock { Text = "Last", FontSize = 12, Foreground = _menuWhitePrimary, VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                IsEnabled = remembered != null,
                Padding = new Thickness(8, 0, 8, 0),
                MinWidth = 0,
                MinHeight = 28,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            PaintButton(lastButton, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(28, 255, 255, 255), Color.FromArgb(16, 255, 255, 255));
            ToolTipService.SetToolTip(lastButton, remembered == null
                ? "Available after you close a frame"
                : "Put back the frame from the last time this panel closed");
            lastButton.Click += (_, _) =>
            {
                if (remembered != null) ApplyLook(remembered);
            };
            Grid.SetColumn(lastButton, 1);
            presetHeader.Children.Add(lastButton);
            body.Children.Add(presetHeader);

            string[] presetNames = { "Card", "Mat", "Round", "Flush" };
            var presetTrack = MakeSegment(presetNames, new[]
            {
                "Soft card, slight radius, rim light",
                "Gallery mat with a square black line",
                "Rounded gradient on a frosted mat",
                "No line, mat, or shadow"
            }, index =>
            {
                CellBorderDto source = index switch
                {
                    0 => FrameLooks.Card(),
                    1 => FrameLooks.Mat(),
                    2 => FrameLooks.Round(),
                    _ => FrameLooks.Flush()
                };
                ApplyLook(source);
            }, out var presetShow);
            showPreset = presetShow;
            body.Children.Add(presetTrack);

            body.Children.Add(SectionLabel("Stroke"));
            string[] strokeStyles = { "Solid", "Dashed", "InnerMuted", "Gradient" };
            var strokeTrack = MakeSegment(new[] { "Solid", "Dashed", "Muted", "Gradient" }, new[]
            {
                "Solid line",
                "Dashed line",
                "Soft line in the chosen colour",
                "Line that fades into a second colour"
            }, index =>
            {
                border.StrokeStyle = strokeStyles[index];
                if (border.Thickness <= 0)
                {
                    border.Thickness = strokeStyles[index] == "InnerMuted" ? 1 : 2;
                    syncing = true;
                    thicknessSlider.Value = border.Thickness;
                    syncing = false;
                }
                Touch();
            }, out var strokeShow);
            showStroke = strokeShow;
            body.Children.Add(strokeTrack);

            colorLabel = SectionLabel("Color");
            body.Children.Add(colorLabel);
            (string Label, string Hex)[] lineColors =
            {
                ("Clear", "#00000000"),
                ("White", "#FFFFFFFF"),
                ("Black", "#FF000000"),
                ("Charcoal", "#FF252525"),
                ("Gold", "#FFFFC83B"),
                ("Blue", "#FF0078D4"),
                ("Red", "#FFD13438"),
                ("Cyan", "#FF00B7C3")
            };
            var colorRow = MakeSwatches(lineColors, () => border.Color, hex =>
            {
                border.Color = hex;
                if (border.Thickness <= 0 && !string.Equals(hex, "#00000000", StringComparison.OrdinalIgnoreCase))
                {
                    border.Thickness = 2;
                    syncing = true;
                    thicknessSlider.Value = 2;
                    syncing = false;
                }
                Touch();
            }, out var colorRefresh);
            refreshColor = colorRefresh;
            body.Children.Add(colorRow);

            endBlock = new StackPanel { Spacing = 6 };
            endBlock.Children.Add(SectionLabel("End color"));
            (string Label, string Hex)[] endColors =
            {
                ("Fade", "#00000000"),
                ("White", "#FFFFFFFF"),
                ("Black", "#FF000000"),
                ("Charcoal", "#FF252525"),
                ("Gold", "#FFFFC83B"),
                ("Blue", "#FF0078D4"),
                ("Red", "#FFD13438"),
                ("Cyan", "#FF00B7C3")
            };
            var endRow = MakeSwatches(endColors, () => border.SecondaryColor, hex =>
            {
                border.SecondaryColor = hex;
                Touch();
            }, out var endRefresh);
            refreshEnd = endRefresh;
            endBlock.Children.Add(endRow);
            body.Children.Add(endBlock);

            body.Children.Add(thicknessRow);
            body.Children.Add(radiusRow);
            body.Children.Add(gutterRow);

            body.Children.Add(SectionLabel("Mat"));
            string[] matStyles = { "None", "Solid", "Acrylic" };
            var matTrack = MakeSegment(new[] { "None", "Solid", "Frosted" }, new[]
            {
                "No mat. The gutter shows the dark canvas",
                "Flat colour in the gutter",
                "The mat colour, translucent"
            }, index =>
            {
                border.GutterMaterial = matStyles[index];
                bool bare = string.IsNullOrEmpty(border.GutterColor) || string.Equals(border.GutterColor, "#00000000", StringComparison.OrdinalIgnoreCase);
                if (matStyles[index] != "None" && bare)
                    border.GutterColor = "#FFF8F8F8";
                if (border.Padding <= 0 && matStyles[index] != "None")
                {
                    border.Padding = 16;
                    syncing = true;
                    gutterSlider.Value = 16;
                    syncing = false;
                }
                Touch();
            }, out var matShow);
            showMat = matShow;
            body.Children.Add(matTrack);

            (string Label, string Hex)[] matColors =
            {
                ("None", "#00000000"),
                ("Gallery white", "#FFF8F8F8"),
                ("Warm cream", "#FFF3EFE6"),
                ("Studio gray", "#FFE0E0E0"),
                ("Charcoal", "#FF262626"),
                ("Black", "#FF000000"),
                ("Navy", "#FF182230"),
                ("Gold", "#FFFFC83B")
            };
            var matRow = MakeSwatches(matColors, () => border.GutterColor, hex =>
            {
                border.GutterColor = hex;
                if (string.Equals(hex, "#00000000", StringComparison.OrdinalIgnoreCase))
                    border.GutterMaterial = "None";
                else if (string.Equals(border.GutterMaterial, "None", StringComparison.OrdinalIgnoreCase))
                    border.GutterMaterial = "Solid";
                if (border.Padding <= 0 && !string.Equals(hex, "#00000000", StringComparison.OrdinalIgnoreCase))
                {
                    border.Padding = 16;
                    syncing = true;
                    gutterSlider.Value = 16;
                    syncing = false;
                }
                Touch();
            }, out var matColorRefresh);
            refreshMatColor = matColorRefresh;
            body.Children.Add(matRow);

            var shadowHeader = new Grid { VerticalAlignment = VerticalAlignment.Center };
            shadowHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            shadowHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            shadowHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shadowHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var shadowTitle = new TextBlock
            {
                Text = "Shadow",
                FontSize = 12,
                Foreground = _menuWhiteSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            shadowValue = new TextBlock
            {
                Text = $"{(int)border.ShadowIntensity} %",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _menuWhitePrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            shadowHeader.Children.Add(shadowTitle);
            Grid.SetColumn(shadowValue, 1);
            shadowHeader.Children.Add(shadowValue);

            glowIcon = new FontIcon
            {
                Glyph = "\uE706",
                FontSize = 12,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                VerticalAlignment = VerticalAlignment.Center
            };
            glowText = new TextBlock { Text = "Rim", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            glowToggle = new ToggleButton
            {
                IsChecked = border.RimGlow,
                Padding = new Thickness(8, 0, 8, 0),
                MinHeight = 28,
                MinWidth = 0,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { glowIcon, glowText }
                }
            };
            ToolTipService.SetToolTip(glowToggle, "A light edge instead of a drop shadow");
            glowToggle.Click += (_, _) =>
            {
                border.RimGlow = glowToggle.IsChecked == true;
                if (border.RimGlow && border.ShadowIntensity <= 0)
                {
                    border.ShadowIntensity = 40;
                    syncing = true;
                    shadowSlider.Value = 40;
                    syncing = false;
                }
                Touch();
            };
            Grid.SetColumn(glowToggle, 3);
            shadowHeader.Children.Add(glowToggle);
            body.Children.Add(shadowHeader);

            shadowSlider = new Slider
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
                shadowValue.Text = $"{(int)e.NewValue} %";
                if (syncing) return;
                border.ShadowIntensity = e.NewValue;
                border.Shadow = e.NewValue > 0;
                Touch();
            };
            body.Children.Add(shadowSlider);

            RefreshPanel();

            double scrollMax = 560;
            if (XamlRoot != null && XamlRoot.Size.Height > 200)
                scrollMax = Math.Clamp(XamlRoot.Size.Height - 240, 280, 640);
            var scroll = new ScrollViewer
            {
                Content = body,
                MaxHeight = scrollMax,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var apply = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "\uE74C",
                            FontSize = 13,
                            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                            Foreground = _menuWhitePrimary
                        },
                        new TextBlock
                        {
                            Text = "Apply to all photos",
                            FontSize = 12,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = _menuWhitePrimary,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };
            PaintButton(apply, Color.FromArgb(36, 255, 255, 255), Color.FromArgb(56, 255, 255, 255), Color.FromArgb(24, 255, 255, 255));
            ToolTipService.SetToolTip(apply, "Copy this frame onto every photo in the comparison");
            apply.Click += (_, _) =>
            {
                var proto = border.Clone();
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it != null && !string.IsNullOrEmpty(it.Path))
                        _itemBorders[it] = proto.Clone();
                }
                if (_itemBorders.TryGetValue(item, out var stored))
                {
                    border = stored;
                    _openFrameBorder = stored;
                }
                _lastBorderSettings = border.Clone();
                ApplyBorderToAllCells();
                _cellBorderFlyout?.Hide();
            };

            // A finite width is required. Star columns and the sliders are measured while the
            // flyout opens, and an unbounded width makes ShowAt reject the popup.
            var root = new StackPanel { Spacing = 10, Width = 328 };
            var title = new TextBlock
            {
                Text = "Frame & Border \u2014 Photo " + (slot + 1),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _menuWhitePrimary
            };
            root.Children.Add(title);
            root.Children.Add(scroll);
            root.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = _menuDividerBrush });
            root.Children.Add(apply);
            AutomationProperties.SetName(root, "Frame and border for photo " + (slot + 1));

            var flyout = new Flyout
            {
                Content = root,
                Placement = FlyoutPlacementMode.Top
            };
            flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(10)),
                    new Setter(FlyoutPresenter.PaddingProperty, new Thickness(14, 12, 14, 12)),
                    new Setter(FlyoutPresenter.WidthProperty, 356.0),
                    new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(242, 32, 32, 32))),
                    new Setter(FlyoutPresenter.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(51, 255, 255, 255))),
                    new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)),
                    new Setter(FlyoutPresenter.RequestedThemeProperty, ElementTheme.Dark)
                }
            };
            flyout.Closed += (_, _) =>
            {
                if (ticket != _frameFlyoutTicket) return;
                _lastBorderSettings = border.Clone();
                _openFrameBorder = border;
            };
            _cellBorderFlyout = flyout;
            int showTicket = ticket;
            var anchor = targetElement;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (showTicket != _frameFlyoutTicket) return;
                if (!ReferenceEquals(_cellBorderFlyout, flyout)) return;
                ShowFrameFlyout(flyout, anchor);
            });
        }

        private void ShowFrameFlyout(Flyout flyout, FrameworkElement anchor)
        {
            try
            {
                var target = anchor;
                if (!target.IsLoaded || target.XamlRoot == null)
                    target = this;
                flyout.ShowAt(target);
            }
            catch (Exception ex)
            {
                try
                {
                    flyout.ShowAt(this);
                }
                catch (Exception fallback)
                {
                    try
                    {
                        string logFile = System.IO.Path.Combine(AppPaths.LocalDirectory, "crash.log");
                        System.IO.File.AppendAllText(logFile,
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Frame flyout did not open: {ex.Message} / {fallback.Message}{Environment.NewLine}");
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static Border? CardBorder(FrameworkElement? root)
        {
            if (root is Border direct && direct.Tag is CellParts) return direct;
            if (root is Grid slot)
            {
                foreach (var child in slot.Children)
                {
                    if (child is Border card && card.Tag is CellParts) return card;
                }
            }
            return null;
        }

        private static void ApplyDropShadow(Border? gutterHost, CellBorderDto border)
        {
            if (gutterHost?.Child is not Image image) return;
            bool drop = border.ShadowIntensity > 0 && !border.RimGlow;
            int width = (int)Math.Round(gutterHost.ActualWidth);
            int height = (int)Math.Round(gutterHost.ActualHeight);
            if (!drop || width < 2 || height < 2)
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                return;
            }

            double pad = Math.Max(0, border.Padding);
            double cardW = width - (pad * 2);
            double cardH = height - (pad * 2);
            if (cardW < 1 || cardH < 1)
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                return;
            }

            var alpha = FrameShadow.RenderAlpha(width, height, pad, pad, cardW, cardH, border.CornerRadius, border.ShadowIntensity);
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < alpha.Length; i++)
            {
                byte a = alpha[i];
                if (a == 0) continue;
                pixels[(i * 4) + 3] = a;
            }

            var bitmap = new WriteableBitmap(width, height);
            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(pixels, 0, pixels.Length);
            bitmap.Invalidate();
            image.Source = bitmap;
            image.Visibility = Visibility.Visible;
        }

        private static void CopyFrame(CellBorderDto target, CellBorderDto source)
        {
            target.Thickness = source.Thickness;
            target.Padding = source.Padding;
            target.CornerRadius = source.CornerRadius;
            target.Color = source.Color;
            target.SecondaryColor = source.SecondaryColor;
            target.StrokeStyle = string.IsNullOrWhiteSpace(source.StrokeStyle) ? "Solid" : source.StrokeStyle;
            target.ShadowIntensity = source.ShadowIntensity;
            target.Shadow = source.ShadowIntensity > 0;
            target.RimGlow = source.RimGlow;
            target.GutterColor = string.IsNullOrWhiteSpace(source.GutterColor) ? "#00000000" : source.GutterColor;
            target.GutterMaterial = string.IsNullOrWhiteSpace(source.GutterMaterial) ? "None" : source.GutterMaterial;
        }

        private static int FrameIndex(string[] names, string? value)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        private TextBlock SectionLabel(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = _menuWhiteSecondary
        };

        private void PaintButton(Button button, Color fill, Color hover, Color pressed)
        {
            button.Background = new SolidColorBrush(fill);
            button.Foreground = _menuWhitePrimary;
            button.Resources["ButtonBackground"] = new SolidColorBrush(fill);
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
            button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressed);
            button.Resources["ButtonBorderBrush"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            button.Resources["ButtonForeground"] = _menuWhitePrimary;
            button.Resources["ButtonForegroundPointerOver"] = _menuWhitePrimary;
            button.Resources["ButtonForegroundPressed"] = _menuWhitePrimary;
            button.Resources["ButtonForegroundDisabled"] = _menuWhiteTertiary;
        }

        private Border MakeSegment(string[] labels, string[] tips, Action<int> onPick, out Action<int> show)
        {
            var grid = new Grid { Height = 32 };
            for (int i = 0; i < labels.Length; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var selection = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 62, 62, 62)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            grid.Children.Add(selection);

            var caption = new TextBlock[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                int captured = i;
                var text = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _menuWhiteSecondary
                };
                caption[i] = text;
                var button = new Button
                {
                    Content = text,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(2, 0, 2, 0),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4)
                };
                PaintButton(button, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(28, 255, 255, 255), Color.FromArgb(16, 255, 255, 255));
                if (i < tips.Length && !string.IsNullOrEmpty(tips[i]))
                    ToolTipService.SetToolTip(button, tips[i]);
                AutomationProperties.SetName(button, labels[i]);
                button.Click += (_, _) => onPick(captured);
                Grid.SetColumn(button, i);
                grid.Children.Add(button);
            }

            show = index =>
            {
                if ((uint)index >= (uint)labels.Length)
                    selection.Visibility = Visibility.Collapsed;
                else
                {
                    selection.Visibility = Visibility.Visible;
                    Grid.SetColumn(selection, index);
                }
                for (int i = 0; i < caption.Length; i++)
                {
                    bool on = i == index;
                    caption[i].FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                    caption[i].Foreground = on ? _menuWhitePrimary : _menuWhiteSecondary;
                }
            };

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Child = grid
            };
        }

        private StackPanel MakeSwatches((string Label, string Hex)[] items, Func<string?> current, Action<string> pick, out Action refresh)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var shells = new Border[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                string hex = items[i].Hex;
                bool clear = string.Equals(hex, "#00000000", StringComparison.OrdinalIgnoreCase);
                var dot = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (clear)
                {
                    dot.Children.Add(new FontIcon
                    {
                        Glyph = "\uE711",
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        Foreground = _menuWhiteSecondary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                else
                {
                    dot.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Fill = new SolidColorBrush(ParseColor(hex)),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    });
                }
                var shell = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                    Background = new SolidColorBrush(clear ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0)),
                    Child = dot,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                shells[i] = shell;
                var button = new Button
                {
                    Width = 28,
                    Height = 28,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Content = shell,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(14)
                };
                PaintButton(button, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(20, 255, 255, 255), Color.FromArgb(12, 255, 255, 255));
                ToolTipService.SetToolTip(button, items[i].Label);
                AutomationProperties.SetName(button, items[i].Label);
                string chosen = hex;
                button.Click += (_, _) => pick(chosen);
                row.Children.Add(button);
            }

            refresh = () =>
            {
                string cur = FrameColor.Canon(current(), "#00000000");
                for (int i = 0; i < items.Length; i++)
                {
                    bool on = string.Equals(FrameColor.Canon(items[i].Hex, "#00000000"), cur, StringComparison.OrdinalIgnoreCase);
                    shells[i].BorderBrush = new SolidColorBrush(on ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(70, 255, 255, 255));
                    shells[i].BorderThickness = new Thickness(on ? 2 : 1);
                }
            };
            refresh();
            return row;
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
                FontSize = 12,
                Foreground = _menuWhiteSecondary
            };
            headerGrid.Children.Add(title);

            var valueLabel = new TextBlock
            {
                Text = $"{(int)initialValue} {unit}",
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
                valueLabel.Text = $"{(int)e.NewValue} {unit}";
                onValueChanged(e.NewValue);
            };
            sliderRef = slider;
            panel.Children.Add(slider);
            return panel;
        }
    }
}


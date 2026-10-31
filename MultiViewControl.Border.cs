using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private int _frameFlyoutTicket;
        private Flyout? _cellBorderFlyout;
        private FrameworkElement? _cellBorderFlyoutAnchor;

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
            RefreshGridLayoutEnabled();
            if ((uint)slot >= (uint)_cellRoots.Length || (uint)slot >= (uint)_items.Count) return;
            if (_cellRoots[slot]?.Tag is not CellParts parts) return;
            bool hidden = _hiddenItems.Contains(_items[slot]);
            bool failed = parts.Bound.Failed;
            if (parts.ImageContainer != null)
                parts.ImageContainer.Visibility = hidden || failed ? Visibility.Collapsed : Visibility.Visible;
            if (parts.HiddenPlaceholder is StackPanel placeholder)
            {
                placeholder.Visibility = hidden || failed ? Visibility.Visible : Visibility.Collapsed;
                if (placeholder.Children.Count >= 3
                    && placeholder.Children[0] is FontIcon icon
                    && placeholder.Children[1] is TextBlock title
                    && placeholder.Children[2] is TextBlock detail)
                {
                    icon.Glyph = failed && !hidden ? "" : "";
                    title.Text = failed && !hidden ? "File not found" : "Image hidden";
                    detail.Text = failed && !hidden ? _items[slot].Name ?? string.Empty : "Right-click to show";
                }
            }
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

            // A shaped window draws its frame along its outline, so the outline follows every frame change.
            var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Container, root));
            if (card != null) ApplyWindowGeometry(card);
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
            // A shaped window casts its shadow from its outline, in ApplyWindowGeometry.
            if (GetGeometry(item).Shape == WindowShapes.Rectangle)
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

            // Ignore the open panel's close, so it cannot overwrite this one.
            int ticket = ++_frameFlyoutTicket;
            _cellBorderFlyout?.Hide();

            var border = GetBorderForItem(item);
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
                showPreset?.Invoke(PresetIndexFor(border));
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

            // One line: nothing, and three frames the user keeps. Hold a preset to save, click to recall.
            var presetButtons = new Button[4];
            var heldPreset = new bool[4];
            var presetTrack = MakeSegment(new[] { "Flush", "Preset 1", "Preset 2", "Preset 3" }, new[]
            {
                "No line, mat, or shadow",
                "",
                "",
                ""
            }, index =>
            {
                // The click that ends a long press has already saved.
                if (heldPreset[index])
                {
                    heldPreset[index] = false;
                    return;
                }
                var source = index == 0 ? FrameLooks.Flush() : MainWindow.Instance?.MultiViewFramePreset(index - 1);
                if (source != null) ApplyLook(source);
            }, out var presetShow, presetButtons);
            showPreset = presetShow;
            for (int p = 0; p < 3; p++)
            {
                int presetIndex = p;
                var button = presetButtons[p + 1];
                void ShowPresetTip()
                {
                    bool saved = MainWindow.Instance?.MultiViewFramePreset(presetIndex) != null;
                    ToolTipService.SetToolTip(button, saved
                        ? "Click to use this frame. Hold to save the current frame here."
                        : "Empty. Hold to save the current frame here.");
                    // An empty preset reads as empty before anyone tries it.
                    if (button.Content is TextBlock caption) caption.Opacity = saved ? 1 : 0.5;
                }
                ShowPresetTip();
                AttachLongPress(button, () =>
                {
                    heldPreset[presetIndex + 1] = true;
                    MainWindow.Instance?.SaveMultiViewFramePreset(presetIndex, border);
                    ShowPresetTip();
                    RefreshPanel();
                    if (button.Content is TextBlock caption)
                    {
                        string label = caption.Text;
                        caption.Text = "Saved";
                        var restore = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        restore.Tick += (_, _) =>
                        {
                            restore.Stop();
                            caption.Text = label;
                        };
                        restore.Start();
                    }
                });
            }
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
            var lineColors = FramePalette.WithClear("Clear", FramePalette.Colors);
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
            var endColors = FramePalette.WithClear("Fade", FramePalette.Colors);
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
                "Flat color in the gutter",
                "The mat color, translucent"
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

            // None above already clears the mat, so this row is colors only.
            var matRow = MakeSwatches(FramePalette.MatColors, () => border.GutterColor, hex =>
            {
                border.GutterColor = hex;
                if (string.Equals(border.GutterMaterial, "None", StringComparison.OrdinalIgnoreCase))
                    border.GutterMaterial = "Solid";
                if (border.Padding <= 0)
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

            // Tall enough for every control. It scrolls only when the window is too short to hold them.
            double scrollMax = 800;
            if (XamlRoot != null && XamlRoot.Size.Height > 200)
                scrollMax = Math.Max(280, XamlRoot.Size.Height - 150);
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
                            Text = "Apply to all cells",
                            FontSize = 12,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = _menuWhitePrimary,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };
            PaintButton(apply, Color.FromArgb(36, 255, 255, 255), Color.FromArgb(56, 255, 255, 255), Color.FromArgb(24, 255, 255, 255));
            ToolTipService.SetToolTip(apply, "Copy this frame onto every cell");
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
                }
                ApplyBorderToAllCells();
                _cellBorderFlyout?.Hide();
            };

            // A finite width is required. Star columns and the sliders are measured while the
            // flyout opens, and an unbounded width makes ShowAt reject the popup.
            var root = new StackPanel { Spacing = 10, Width = 328 };
            var title = new TextBlock
            {
                Text = "Frame & Border \u2014 Position " + (slot + 1),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _menuWhitePrimary
            };
            root.Children.Add(title);
            root.Children.Add(scroll);
            root.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = _menuDividerBrush });
            root.Children.Add(apply);
            AutomationProperties.SetName(root, "Frame and border for position " + (slot + 1));

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
                    new Setter(FlyoutPresenter.MaxHeightProperty, 4000.0),
                    new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(242, 32, 32, 32))),
                    new Setter(FlyoutPresenter.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(51, 255, 255, 255))),
                    new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)),
                    new Setter(FlyoutPresenter.RequestedThemeProperty, ElementTheme.Dark)
                }
            };
            flyout.Closed += (_, _) =>
            {
                if (ticket != _frameFlyoutTicket) return;
                NoteUserAction();
            };
            _cellBorderFlyout = flyout;
            int showTicket = ticket;
            var anchor = targetElement;
            _cellBorderFlyoutAnchor = anchor;
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

            int previewW = Math.Max(1, width / ShadowPreviewDivisor);
            int previewH = Math.Max(1, height / ShadowPreviewDivisor);
            double scale = 1.0 / ShadowPreviewDivisor;
            var alpha = FrameShadow.RenderAlpha(previewW, previewH, pad * scale, pad * scale, cardW * scale, cardH * scale, border.CornerRadius * scale, border.ShadowIntensity, scale, scale);
            WriteShadow(image, alpha, previewW, previewH);
        }

        private static void WriteShadow(Image image, byte[] alpha, int width, int height)
        {
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

        /// <summary>Which of the one-line presets this frame matches: 0 Flush, 1 to 3 the saved presets, or -1.</summary>
        private static int PresetIndexFor(CellBorderDto frame)
        {
            if (frame.SameLook(FrameLooks.Flush())) return 0;
            for (int i = 0; i < 3; i++)
            {
                if (frame.SameLook(MainWindow.Instance?.MultiViewFramePreset(i))) return i + 1;
            }
            return -1;
        }

        /// <summary>Runs the action when the pointer stays down on the button. Moving off it or letting go cancels.</summary>
        private static void AttachLongPress(Button button, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            // A button handles its own press, so these listen for handled events too.
            button.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => timer.Start()), true);
            var stop = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => timer.Stop());
            button.AddHandler(PointerReleasedEvent, stop, true);
            button.AddHandler(PointerExitedEvent, stop, true);
            button.AddHandler(PointerCanceledEvent, stop, true);
            button.AddHandler(PointerCaptureLostEvent, stop, true);
        }

        private Border MakeSegment(string[] labels, string[] tips, Action<int> onPick, out Action<int> show, Button[]? buttons = null)
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
                if (buttons != null) buttons[i] = button;
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
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

            // The last swatch opens the Windows colour dialog, and shows the colour once one is chosen.
            var customDot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Visibility = Visibility.Collapsed
            };
            var customIcon = new FontIcon
            {
                Glyph = "",
                FontSize = 13,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = _menuWhiteSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var customFace = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            customFace.Children.Add(customIcon);
            customFace.Children.Add(customDot);
            var customShell = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Child = customFace,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var customButton = new Button
            {
                Width = 28,
                Height = 28,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Content = customShell,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14)
            };
            PaintButton(customButton, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(20, 255, 255, 255), Color.FromArgb(12, 255, 255, 255));
            ToolTipService.SetToolTip(customButton, "Custom color…");
            AutomationProperties.SetName(customButton, "Custom color");
            row.Children.Add(customButton);

            Action refreshRow = () =>
            {
                string cur = FrameColor.Canon(current(), "#00000000");
                bool known = false;
                for (int i = 0; i < items.Length; i++)
                {
                    bool on = string.Equals(FrameColor.Canon(items[i].Hex, "#00000000"), cur, StringComparison.OrdinalIgnoreCase);
                    known |= on;
                    shells[i].BorderBrush = new SolidColorBrush(on ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(70, 255, 255, 255));
                    shells[i].BorderThickness = new Thickness(on ? 2 : 1);
                }
                customDot.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
                customIcon.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
                if (!known) customDot.Fill = new SolidColorBrush(ParseColor(current()));
                customShell.BorderBrush = new SolidColorBrush(known ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(255, 255, 255, 255));
                customShell.BorderThickness = new Thickness(known ? 1 : 2);
            };
            customButton.Click += (_, _) => PickCustomColor(current, hex =>
            {
                pick(hex);
                refreshRow();
                NoteUserAction();
            }, reopenFrameCard: true);

            refresh = refreshRow;
            refresh();
            return row;
        }

        /// <summary>Opens the dialog after the click has finished, because it runs its own message loop until it closes.</summary>
        private void PickCustomColor(Func<string?> current, Action<string> chosen, bool reopenFrameCard = false)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (MainWindow.Instance == null) return;
                var owner = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                string? hex = WindowsColorDialog.Pick(owner, current());
                if (hex != null) chosen(hex);
                if (reopenFrameCard) ReopenFrameCard();
            });
        }

        /// <summary>The dialog takes the window's focus, which closes the frame card. The same card comes back with its settings.</summary>
        private void ReopenFrameCard()
        {
            var flyout = _cellBorderFlyout;
            var anchor = _cellBorderFlyoutAnchor;
            if (flyout == null || anchor == null) return;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (ReferenceEquals(_cellBorderFlyout, flyout) && !flyout.IsOpen)
                    ShowFrameFlyout(flyout, anchor);
            });
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


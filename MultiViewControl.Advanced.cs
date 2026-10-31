using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;

namespace ModernImageViewer
{
    /// <summary>The Advanced menus: image rotation and mirroring on every cell, window shape on freeform cells, and the canvas.</summary>
    public sealed partial class MultiViewControl
    {
        private const string SettingsGlyph = "";
        private const double WindowRotationStep = 15;

        private readonly Dictionary<ImageItem, CellGeometryDto> _cellGeometry = new();
        private string _canvasColor = CanvasLook.DefaultColor;
        private string _canvasSurface = CanvasLook.Solid;

        private CellGeometryDto GetGeometry(ImageItem item)
        {
            if (!_cellGeometry.TryGetValue(item, out var geometry))
            {
                geometry = new CellGeometryDto();
                _cellGeometry[item] = geometry;
            }
            return geometry;
        }

        private static MenuFlyoutItem MakeAdvancedItem(string text, string glyph, Action click)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => click();
            return item;
        }

        /// <summary>Marks the current choice the way the canvas shape menu does.</summary>
        private static MenuFlyoutItem MakeChoiceItem(string text, bool chosen, Action click)
        {
            var item = new MenuFlyoutItem { Text = (chosen ? "✓  " : "    ") + text };
            item.Click += (_, _) => click();
            return item;
        }

        private void AddCellAdvancedMenu(MenuFlyout menu, ImageItem item)
        {
            var advanced = new MenuFlyoutSubItem { Text = "Advanced", Icon = new FontIcon { Glyph = SettingsGlyph } };

            void ChangeImage(Action<CellGeometryDto> change)
            {
                change(GetGeometry(item));
                ShowHiddenState(Slot(item));
            }

            advanced.Items.Add(MakeAdvancedItem("Rotate image left", "", () => ChangeImage(g => g.TurnImage(-90))));
            advanced.Items.Add(MakeAdvancedItem("Rotate image right", "", () => ChangeImage(g => g.TurnImage(90))));
            advanced.Items.Add(MakeAdvancedItem("Mirror image left to right", "", () => ChangeImage(g => g.ImageFlipH = !g.ImageFlipH)));
            advanced.Items.Add(MakeAdvancedItem("Flip image top to bottom", "", () => ChangeImage(g => g.ImageFlipV = !g.ImageFlipV)));

            var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Item, item));
            if (card != null)
            {
                advanced.Items.Add(new MenuFlyoutSeparator());

                void ChangeWindow(Action<CellGeometryDto> change)
                {
                    change(GetGeometry(item));
                    ApplyWindowGeometry(card);
                }

                var geometry = GetGeometry(item);
                var shapes = new MenuFlyoutSubItem { Text = "Window shape", Icon = new FontIcon { Glyph = "" } };
                foreach (string shape in WindowShapes.All)
                {
                    string chosen = shape;
                    shapes.Items.Add(MakeChoiceItem(shape, geometry.Shape == shape, () => ChangeWindow(g => g.Shape = chosen)));
                }
                advanced.Items.Add(shapes);

                advanced.Items.Add(MakeAdvancedItem("Mirror window left to right", "", () => ChangeWindow(g => g.ShapeFlipH = !g.ShapeFlipH)));
                advanced.Items.Add(MakeAdvancedItem("Flip window top to bottom", "", () => ChangeWindow(g => g.ShapeFlipV = !g.ShapeFlipV)));
                advanced.Items.Add(MakeAdvancedItem("Rotate window left", "", () => ChangeWindow(g => g.WindowRotation -= WindowRotationStep)));
                advanced.Items.Add(MakeAdvancedItem("Rotate window right", "", () => ChangeWindow(g => g.WindowRotation += WindowRotationStep)));
                advanced.Items.Add(MakeAdvancedItem("Reset window", "", () => ChangeWindow(g =>
                {
                    g.Shape = WindowShapes.Rectangle;
                    g.ShapeFlipH = false;
                    g.ShapeFlipV = false;
                    g.WindowRotation = 0;
                    g.NotchX = 0.5;
                    g.NotchY = 0.5;
                })));

                advanced.Items.Add(new MenuFlyoutSeparator());
                advanced.Items.Add(MakeAdvancedItem("Bring to front", "", () => MoveCardLayer(card, int.MaxValue)));
                advanced.Items.Add(MakeAdvancedItem("Bring forward", "", () => MoveCardLayer(card, 1)));
                advanced.Items.Add(MakeAdvancedItem("Send backward", "", () => MoveCardLayer(card, -1)));
                advanced.Items.Add(MakeAdvancedItem("Send to back", "", () => MoveCardLayer(card, int.MinValue)));
            }

            menu.Items.Add(advanced);
        }

        /// <summary>Turn and clip the floating window to its shape. Called again when the window changes size.</summary>
        private void ApplyWindowGeometry(FreeformCard card)
        {
            if (card.Container is not Grid window) return;
            var geometry = GetGeometry(card.Item);

            if (geometry.WindowRotation != 0 || window.RenderTransform is RotateTransform)
            {
                window.RenderTransformOrigin = new Point(0.5, 0.5);
                window.RenderTransform = new RotateTransform { Angle = geometry.WindowRotation };
            }

            var outlineElement = window.Children.OfType<Polygon>().FirstOrDefault();
            var outline = WindowShapes.Outline(geometry);
            double width = window.Width > 0 ? window.Width : window.ActualWidth;
            double height = window.Height > 0 ? window.Height : window.ActualHeight;
            var photoGrid = CardBorder(window)?.Child as Grid;
            var hitArea = photoGrid?.Children.OfType<Polygon>().FirstOrDefault();
            if (card.NotchHandle != null)
                card.NotchHandle.Visibility = geometry.Shape == WindowShapes.LShape ? Visibility.Visible : Visibility.Collapsed;
            if (outline == null || width < 1 || height < 1)
            {
                // A plain window never takes a composition clip, so it needs no composition visual.
                if (outlineElement != null)
                {
                    foreach (var part in ShapedParts(window))
                        ElementCompositionPreview.GetElementVisual(part).Clip = null;
                    outlineElement.Visibility = Visibility.Collapsed;
                }
                bool wasShaped = hitArea != null && hitArea.Visibility == Visibility.Visible;
                if (hitArea != null) hitArea.Visibility = Visibility.Collapsed;
                if (photoGrid != null)
                    photoGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
                if (wasShaped && CardBorder(window) is Border plainCard)
                    ApplyBorderToCellBorder(plainCard, photoGrid, card.Item);
                return;
            }

            // Pointers follow the outline: the empty part of the window has nothing to hit, so clicks reach what is behind.
            if (photoGrid != null)
            {
                var inset = CardBorder(window)!.Margin;
                photoGrid.Background = null;
                if (hitArea == null)
                {
                    hitArea = new Polygon { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)) };
                    photoGrid.Children.Insert(0, hitArea);
                }
                hitArea.Visibility = Visibility.Visible;
                var hitPoints = new PointCollection();
                foreach (var p in outline) hitPoints.Add(new Point(p.X * width - inset.Left, p.Y * height - inset.Top));
                hitArea.Points = hitPoints;
            }

            if (card.NotchHandle != null)
            {
                double nx = geometry.ShapeFlipH ? 1 - geometry.NotchX : geometry.NotchX;
                double ny = geometry.ShapeFlipV ? 1 - geometry.NotchY : geometry.NotchY;
                card.NotchHandle.Margin = new Thickness(nx * width - card.NotchHandle.Width / 2, ny * height - card.NotchHandle.Height / 2, 0, 0);
            }

            // The clip goes on the parts inside the window. A composition visual on the window itself stops it taking pointer input.
            var points = outline.Select(p => new Vector2((float)(p.X * width), (float)(p.Y * height))).ToArray();
            foreach (var part in ShapedParts(window))
            {
                var visual = ElementCompositionPreview.GetElementVisual(part);
                var partInset = new Vector2((float)part.Margin.Left, (float)part.Margin.Top);
                using var shape = CanvasGeometry.CreatePolygon(CanvasDevice.GetSharedDevice(), points.Select(p => p - partInset).ToArray());
                var compositor = visual.Compositor;
                visual.Clip = compositor.CreateGeometricClip(compositor.CreatePathGeometry(new CompositionPath(shape)));
            }

            if (outlineElement == null)
            {
                outlineElement = new Polygon { IsHitTestVisible = false };
                Canvas.SetZIndex(outlineElement, 90);
                window.Children.Add(outlineElement);
            }
            // The frame follows the outline. The card's own rectangular stroke would only show where the shape meets it.
            var frame = GetBorderForItem(card.Item);
            bool dashed = string.Equals(frame.StrokeStyle, "Dashed", StringComparison.OrdinalIgnoreCase);
            var shapedCard = CardBorder(window);
            outlineElement.Stroke = dashed ? ParseBorderBrush(frame.Color) : shapedCard?.BorderBrush ?? ParseBorderBrush(frame.Color);
            outlineElement.StrokeDashArray = dashed ? new DoubleCollection { 3, 2 } : new DoubleCollection();
            outlineElement.Visibility = frame.Thickness > 0 && !_hiddenItems.Contains(card.Item) ? Visibility.Visible : Visibility.Collapsed;
            outlineElement.StrokeThickness = frame.Thickness;
            if (shapedCard != null) shapedCard.BorderThickness = new Thickness(0);
            if (window.Tag is CellParts shapedParts)
            {
                shapedParts.DashedStroke.Visibility = Visibility.Collapsed;
                if (shapedParts.RimGlowStroke != null) shapedParts.RimGlowStroke.Visibility = Visibility.Collapsed;
            }
            // Pulled in by half the stroke, so its outer edge meets the window's edge and is not cut off there.
            double insetX = (width - frame.Thickness) / width;
            double insetY = (height - frame.Thickness) / height;
            var drawn = new PointCollection();
            foreach (var p in points)
                drawn.Add(new Point(width / 2 + (p.X - width / 2) * insetX, height / 2 + (p.Y - height / 2) * insetY));
            outlineElement.Points = drawn;
        }

        /// <summary>Move a floating cell among the floating cells. They all stay above the grid.</summary>
        private void MoveCardLayer(FreeformCard card, int steps)
        {
            var order = _freeformCards.OrderBy(c => c.ZIndex).ToList();
            int from = order.IndexOf(card);
            if (from < 0) return;
            long wanted = (long)from + steps;
            int to = (int)Math.Clamp(wanted, 0, order.Count - 1);
            order.RemoveAt(from);
            order.Insert(to, card);
            for (int i = 0; i < order.Count; i++)
            {
                order[i].ZIndex = 5001 + i;
                Canvas.SetZIndex(order[i].Container, order[i].ZIndex);
                order[i].Container.Translation = new Vector3(0, 0, 256 + ((order[i].ZIndex - 5000) * 8));
            }
            _maxFreeformZIndex = 5000 + order.Count;
        }

        private static Border MakeWindowHandle(double size, string? glyph)
        {
            var handle = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(225, 32, 32, 32)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
                BorderThickness = new Thickness(1.5),
                Opacity = 0
            };
            if (glyph != null)
            {
                handle.Child = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                };
            }
            Canvas.SetZIndex(handle, 210);
            return handle;
        }

        /// <summary>The rotate handle and the L's inner-corner handle. Both show while the pointer is over the window.</summary>
        private void AttachWindowHandles(FreeformCard card)
        {
            if (card.Container is not Grid window) return;

            var rotate = MakeWindowHandle(28, "");
            rotate.HorizontalAlignment = HorizontalAlignment.Center;
            rotate.VerticalAlignment = VerticalAlignment.Top;
            rotate.Margin = new Thickness(0, 12, 0, 0);
            ToolTipService.SetToolTip(rotate, "Drag to rotate the window. Hold Shift for 15 degree steps.");

            bool turning = false;
            double startPointer = 0;
            double startRotation = 0;
            double PointerAngle(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            {
                var p = e.GetCurrentPoint(RootGrid).Position;
                double cx = Canvas.GetLeft(window) + window.Width / 2;
                double cy = Canvas.GetTop(window) + window.Height / 2;
                return Math.Atan2(p.Y - cy, p.X - cx) * 180 / Math.PI;
            }
            rotate.PointerPressed += (_, e) =>
            {
                turning = true;
                startPointer = PointerAngle(e);
                startRotation = GetGeometry(card.Item).WindowRotation;
                rotate.CapturePointer(e.Pointer);
                e.Handled = true;
            };
            rotate.PointerMoved += (_, e) =>
            {
                if (!turning) return;
                double angle = startRotation + PointerAngle(e) - startPointer;
                bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (shift) angle = Math.Round(angle / WindowRotationStep) * WindowRotationStep;
                GetGeometry(card.Item).WindowRotation = angle;
                ApplyWindowGeometry(card);
                e.Handled = true;
            };
            void EndTurn(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            {
                if (!turning) return;
                turning = false;
                rotate.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
            rotate.PointerReleased += EndTurn;
            rotate.PointerCanceled += EndTurn;
            window.Children.Add(rotate);

            var notch = MakeWindowHandle(16, null);
            notch.HorizontalAlignment = HorizontalAlignment.Left;
            notch.VerticalAlignment = VerticalAlignment.Top;
            notch.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(notch, "Drag to change the shape of the L");
            bool moving = false;
            notch.PointerPressed += (_, e) =>
            {
                moving = true;
                notch.CapturePointer(e.Pointer);
                e.Handled = true;
            };
            notch.PointerMoved += (_, e) =>
            {
                if (!moving) return;
                // Measured against the window, so its rotation is already taken out.
                var p = e.GetCurrentPoint(window).Position;
                var geometry = GetGeometry(card.Item);
                double fx = Math.Clamp(p.X / window.Width, 0.15, 0.85);
                double fy = Math.Clamp(p.Y / window.Height, 0.15, 0.85);
                geometry.NotchX = geometry.ShapeFlipH ? 1 - fx : fx;
                geometry.NotchY = geometry.ShapeFlipV ? 1 - fy : fy;
                ApplyWindowGeometry(card);
                e.Handled = true;
            };
            void EndNotch(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            {
                if (!moving) return;
                moving = false;
                notch.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
            notch.PointerReleased += EndNotch;
            notch.PointerCanceled += EndNotch;
            window.Children.Add(notch);
            card.NotchHandle = notch;

            window.PointerEntered += (_, _) => { rotate.Opacity = 1; notch.Opacity = 1; };
            window.PointerExited += (_, _) =>
            {
                if (turning || moving) return;
                rotate.Opacity = 0;
                notch.Opacity = 0;
            };
        }

        private Microsoft.UI.Xaml.Shapes.Rectangle? _guideX;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _guideY;

        /// <summary>Pull a dragged cell onto the canvas, the other floating cells and the showing grid cells. Alt turns it off.</summary>
        private Point SnapDraggedCard(Grid window, double x, double y, double width, double height)
        {
            bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (alt)
            {
                HideSnapGuides();
                return new Point(x, y);
            }

            double canvasW = RootGrid.ActualWidth;
            double canvasH = RootGrid.ActualHeight;
            var linesX = new List<double> { 0, canvasW / 2, canvasW };
            var linesY = new List<double> { 0, canvasH / 2, canvasH };
            foreach (var other in _freeformCards)
            {
                if (ReferenceEquals(other.Container, window)) continue;
                double l = Canvas.GetLeft(other.Container), t = Canvas.GetTop(other.Container);
                double w = other.Container.Width, h = other.Container.Height;
                linesX.AddRange(new[] { l, l + w / 2, l + w });
                linesY.AddRange(new[] { t, t + h / 2, t + h });
            }
            int visible = VisibleCount(GridCellCount);
            for (int i = 0; i < visible && i < _cellRoots.Length; i++)
            {
                if (_cellRoots[i] is not FrameworkElement cell || _hiddenItems.Contains(_items[i]) || cell.ActualWidth < 1) continue;
                var at = cell.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
                linesX.AddRange(new[] { at.X, at.X + cell.ActualWidth });
                linesY.AddRange(new[] { at.Y, at.Y + cell.ActualHeight });
            }

            var snappedX = FreeformSnap.Snap(x, width, linesX);
            var snappedY = FreeformSnap.Snap(y, height, linesY);
            ShowSnapGuide(ref _guideX, snappedX.Guide, vertical: true);
            ShowSnapGuide(ref _guideY, snappedY.Guide, vertical: false);
            return new Point(snappedX.Start, snappedY.Start);
        }

        private void ShowSnapGuide(ref Microsoft.UI.Xaml.Shapes.Rectangle? guide, double? at, bool vertical)
        {
            if (at == null)
            {
                if (guide != null) guide.Visibility = Visibility.Collapsed;
                return;
            }
            if (guide == null)
            {
                guide = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 0, 162, 237)),
                    IsHitTestVisible = false,
                    HorizontalAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                    VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Top,
                    Width = vertical ? 1 : double.NaN,
                    Height = vertical ? double.NaN : 1
                };
                Canvas.SetZIndex(guide, 5500);
                RootGrid.Children.Add(guide);
            }
            guide.Visibility = Visibility.Visible;
            guide.Margin = vertical ? new Thickness(at.Value, 0, 0, 0) : new Thickness(0, at.Value, 0, 0);
        }

        private void HideSnapGuides()
        {
            if (_guideX != null) _guideX.Visibility = Visibility.Collapsed;
            if (_guideY != null) _guideY.Visibility = Visibility.Collapsed;
        }

        /// <summary>What draws inside a floating window: the mat behind, and the card with the photo.</summary>
        private static IEnumerable<FrameworkElement> ShapedParts(Grid window)
        {
            if (window.Tag is CellParts { GutterHost: not null } parts) yield return parts.GutterHost;
            if (CardBorder(window) is Border card) yield return card;
        }

        private void AdvancedFlyout_Opening(object sender, object e)
        {
            AdvancedMenuFlyout.Items.Clear();
            CaptureUndoPoint();

            var undo = MakeAdvancedItem("Undo", "", Undo);
            undo.KeyboardAcceleratorTextOverride = "Ctrl+Z";
            undo.IsEnabled = CanUndo;
            ToolTipService.SetToolTip(undo, "Undo (Ctrl+Z)");
            AdvancedMenuFlyout.Items.Add(undo);

            var redo = MakeAdvancedItem("Redo", "", Redo);
            redo.KeyboardAcceleratorTextOverride = "Ctrl+Y";
            redo.IsEnabled = CanRedo;
            ToolTipService.SetToolTip(redo, "Redo (Ctrl+Y)");
            AdvancedMenuFlyout.Items.Add(redo);

            AdvancedMenuFlyout.Items.Add(new MenuFlyoutSeparator());

            var copy = MakeAdvancedItem("Copy to clipboard", "", () => _ = CopyCanvasToClipboardAsync());
            copy.KeyboardAcceleratorTextOverride = "Ctrl+C";
            ToolTipService.SetToolTip(copy, "Copy the canvas as an image (Ctrl+C)");
            AdvancedMenuFlyout.Items.Add(copy);

            AdvancedMenuFlyout.Items.Add(MakeAdvancedItem("Save as PNG…", "", () =>
            {
                var (width, height) = CanvasPixels();
                _ = SavePixelsAsync(width, height, preferPng: true);
            }));

            AdvancedMenuFlyout.Items.Add(new MenuFlyoutSeparator());

            var canvas = new MenuFlyoutSubItem { Text = "Canvas", Icon = new FontIcon { Glyph = "" } };

            var surface = new MenuFlyoutSubItem { Text = "Surface" };
            foreach (string name in CanvasLook.Surfaces)
            {
                string chosen = name;
                var choice = MakeChoiceItem(name, _canvasSurface == name, () => SetCanvas(_canvasColor, chosen));
                ToolTipService.SetToolTip(choice, CanvasLook.Describe(name));
                surface.Items.Add(choice);
            }
            ToolTipService.SetToolTip(surface, "Shows where no cell covers the canvas: hidden cells, and around photos in Fit");
            canvas.Items.Add(surface);

            var color = new MenuFlyoutSubItem { Text = "Colour" };
            foreach (var (name, hex) in CanvasLook.Colors)
            {
                string chosen = hex;
                color.Items.Add(MakeChoiceItem(name, string.Equals(_canvasColor, hex, StringComparison.OrdinalIgnoreCase),
                    () => SetCanvas(chosen, _canvasSurface)));
            }
            canvas.Items.Add(color);

            AdvancedMenuFlyout.Items.Add(canvas);
        }

        private void SetCanvas(string? color, string? surface)
        {
            _canvasColor = string.IsNullOrWhiteSpace(color) ? CanvasLook.DefaultColor : color;
            _canvasSurface = CanvasLook.Surfaces.Contains(surface) ? surface! : CanvasLook.Solid;

            var near = ToColor(FrameColor.Parse(_canvasColor));
            var far = ToColor(CanvasLook.FarColor(_canvasColor));
            if (_canvasSurface == CanvasLook.Gradient)
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                brush.GradientStops.Add(new GradientStop { Color = near, Offset = 0 });
                brush.GradientStops.Add(new GradientStop { Color = far, Offset = 1 });
                RootGrid.Background = brush;
            }
            else if (_canvasSurface == CanvasLook.Vignette)
            {
                var brush = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 };
                brush.GradientStops.Add(new GradientStop { Color = near, Offset = 0 });
                brush.GradientStops.Add(new GradientStop { Color = far, Offset = 1 });
                RootGrid.Background = brush;
            }
            else
            {
                RootGrid.Background = new SolidColorBrush(near);
            }
        }
    }
}

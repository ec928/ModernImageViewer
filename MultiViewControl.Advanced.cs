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
                })));
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
            var outline = WindowShapes.Outline(geometry.Shape, geometry.ShapeFlipH, geometry.ShapeFlipV);
            double width = window.Width > 0 ? window.Width : window.ActualWidth;
            double height = window.Height > 0 ? window.Height : window.ActualHeight;
            if (outline == null || width < 1 || height < 1)
            {
                // A plain window never takes a composition clip, so it needs no composition visual.
                if (outlineElement != null)
                {
                    foreach (var part in ShapedParts(window))
                        ElementCompositionPreview.GetElementVisual(part).Clip = null;
                    outlineElement.Visibility = Visibility.Collapsed;
                }
                return;
            }

            // The clip goes on the parts inside the window. A composition visual on the window itself stops it taking pointer input.
            var points = outline.Select(p => new Vector2((float)(p.X * width), (float)(p.Y * height))).ToArray();
            foreach (var part in ShapedParts(window))
            {
                var visual = ElementCompositionPreview.GetElementVisual(part);
                var inset = new Vector2((float)part.Margin.Left, (float)part.Margin.Top);
                using var shape = CanvasGeometry.CreatePolygon(CanvasDevice.GetSharedDevice(), points.Select(p => p - inset).ToArray());
                var compositor = visual.Compositor;
                visual.Clip = compositor.CreateGeometricClip(compositor.CreatePathGeometry(new CompositionPath(shape)));
            }

            if (outlineElement == null)
            {
                outlineElement = new Polygon { IsHitTestVisible = false };
                Canvas.SetZIndex(outlineElement, 90);
                window.Children.Add(outlineElement);
            }
            var frame = GetBorderForItem(card.Item);
            outlineElement.Visibility = frame.Thickness > 0 ? Visibility.Visible : Visibility.Collapsed;
            outlineElement.Stroke = ParseBorderBrush(frame.Color);
            outlineElement.StrokeThickness = frame.Thickness;
            var drawn = new PointCollection();
            foreach (var p in points) drawn.Add(new Point(p.X, p.Y));
            outlineElement.Points = drawn;
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
            var canvas = new MenuFlyoutSubItem { Text = "Canvas", Icon = new FontIcon { Glyph = "" } };

            var surface = new MenuFlyoutSubItem { Text = "Surface" };
            foreach (string name in CanvasLook.Surfaces)
            {
                string chosen = name;
                surface.Items.Add(MakeChoiceItem(name, _canvasSurface == name, () => SetCanvas(_canvasColor, chosen)));
            }
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

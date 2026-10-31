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
using Windows.Storage.Pickers;

namespace ModernImageViewer
{
    /// <summary>The Advanced menus: image rotation and mirroring on every cell, window shape on freeform cells, and the canvas.</summary>
    public sealed partial class MultiViewControl
    {
        private const string SettingsGlyph = "\uE713";
        private const double WindowRotationStep = 15;
        private const int WindowHoverGraceMs = 400;
        // The shadow is soft, so half size looks the same on screen at about an eighth of the cost.
        private const int ShadowPreviewDivisor = 2;

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

        private static MenuFlyoutItem MakeAdvancedItem(string text, string glyph, Action click, bool mirrorIcon = false)
        {
            var icon = new FontIcon { Glyph = glyph };
            if (mirrorIcon)
            {
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
            }
            var item = new MenuFlyoutItem { Text = text, Icon = icon };
            item.Click += (_, _) => click();
            return item;
        }

        /// <summary>Marks the current choice the way the canvas shape menu does.</summary>
        private static MenuFlyoutItem MakeChoiceItem(string text, bool chosen, Action click)
        {
            var item = new MenuFlyoutItem { Text = (chosen ? "\u2713  " : "    ") + text };
            item.Click += (_, _) => click();
            return item;
        }

        private void AddCellAdvancedMenu(MenuFlyout menu, ImageItem item)
        {
            var advanced = new MenuFlyoutSubItem { Text = "Advanced", Icon = new FontIcon { Glyph = SettingsGlyph } };

            void ChangeImage(Action<CellGeometryDto> change)
            {
                change(GetGeometry(item));
                ShowEmptyState(Slot(item));
            }

            if (!IsEmptyCell(item))
            {
                advanced.Items.Add(MakeAdvancedItem("Rotate image left", "\uE7AD", () => ChangeImage(g => g.TurnImage(-90)), mirrorIcon: true));
                advanced.Items.Add(MakeAdvancedItem("Rotate image right", "\uE7AD", () => ChangeImage(g => g.TurnImage(90))));
                advanced.Items.Add(MakeAdvancedItem("Mirror image", "\uE8AB", () => ChangeImage(g => g.ImageFlipH = !g.ImageFlipH)));
                advanced.Items.Add(MakeAdvancedItem("Flip image", "\uE8CB", () => ChangeImage(g => g.ImageFlipV = !g.ImageFlipV)));
            }

            var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Item, item));
            if (card != null)
            {
                if (advanced.Items.Count > 0) advanced.Items.Add(new MenuFlyoutSeparator());
                advanced.Items.Add(MakeWindowMenu(card));
                advanced.Items.Add(MakeLayerMenu(card));
            }

            if (advanced.Items.Count > 0) menu.Items.Add(advanced);
        }

        /// <summary>Choose a file to show in this cell. The cell keeps its parameters, as with any change of content.</summary>
        private async void PickReplacementImage(ImageItem item)
        {
            if (MainWindow.Instance == null) return;
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (string extension in MainWindow.PictureExtensions)
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            // The session may have moved on while the dialog was open.
            int slot = Slot(item);
            if (slot >= 0) ReplaceCellImage(slot, file.Path);
        }

        private void ReplaceCellImage(int slot, string path)
        {
            var replacement = new ImageItem { Path = path, Name = System.IO.Path.GetFileName(path), Dispatcher = DispatcherQueue };
            FillFileDetails(replacement);
            var root = (uint)slot < (uint)_cellRoots.Length ? _cellRoots[slot] : null;

            MoveCellParameters(_items[slot], replacement);
            _items[slot] = replacement;
            var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(root, c.Container));
            if (card != null) card.Item = replacement;

            RebindCell(slot);
            ShowEmptyState(slot);
            ShowHudAndOverlays();
            NoteUserAction();
        }

        private MenuFlyoutSubItem MakeWindowMenu(FreeformCard card)
        {
            void Change(Action<CellGeometryDto> change)
            {
                change(GetGeometry(card.Item));
                ShowEmptyState(Slot(card.Item));
            }

            var window = new MenuFlyoutSubItem { Text = "Window", Icon = new FontIcon { Glyph = "\uE7C5" } };
            string current = GetGeometry(card.Item).Shape;
            var shapes = new MenuFlyoutSubItem { Text = "Shape" };
            foreach (string shape in WindowShapes.All)
            {
                string chosen = shape;
                shapes.Items.Add(MakeChoiceItem(shape, current == shape, () => Change(g => g.Shape = chosen)));
            }
            window.Items.Add(shapes);

            window.Items.Add(new MenuFlyoutSeparator());
            window.Items.Add(MakeAdvancedItem("Mirror shape", "\uE8AB", () => Change(g => g.ShapeFlipH = !g.ShapeFlipH)));
            window.Items.Add(MakeAdvancedItem("Flip shape", "\uE8CB", () => Change(g => g.ShapeFlipV = !g.ShapeFlipV)));

            window.Items.Add(new MenuFlyoutSeparator());
            window.Items.Add(MakeAdvancedItem("Reset window", "\uE72C", () => Change(g =>
            {
                g.Shape = WindowShapes.Rectangle;
                g.ShapeFlipH = false;
                g.ShapeFlipV = false;
                g.WindowRotation = 0;
                g.ImageCounter = 0;
                g.NotchX = 0.5;
                g.NotchY = 0.5;
            })));
            return window;
        }

        private MenuFlyoutSubItem MakeLayerMenu(FreeformCard card)
        {
            var layer = new MenuFlyoutSubItem { Text = "Layer", Icon = new FontIcon { Glyph = "\uE81E" } };
            layer.Items.Add(MakeAdvancedItem("Bring to front", "\uE74A", () => MoveCardLayer(card, int.MaxValue)));
            layer.Items.Add(MakeAdvancedItem("Bring forward", "\uE74A", () => MoveCardLayer(card, 1)));
            layer.Items.Add(MakeAdvancedItem("Send backward", "\uE74B", () => MoveCardLayer(card, -1)));
            layer.Items.Add(MakeAdvancedItem("Send to back", "\uE74B", () => MoveCardLayer(card, int.MinValue)));
            return layer;
        }

        /// <summary>Turn the window. Its position label stays upright, so it can always be read.</summary>
        private void ApplyWindowTurn(FreeformCard card)
        {
            if (card.Container is not Grid window) return;
            double angle = GetGeometry(card.Item).WindowRotation;
            if (angle != 0 || window.RenderTransform is RotateTransform)
            {
                window.RenderTransformOrigin = new Point(0.5, 0.5);
                window.RenderTransform = new RotateTransform { Angle = angle };
            }
            if (window.Tag is CellParts parts)
                parts.PositionBadge.RenderTransform = new RotateTransform { Angle = -angle };
        }

        /// <summary>Turn and clip the floating window to its shape. Called again when the window changes size.</summary>
        private void ApplyWindowGeometry(FreeformCard card)
        {
            if (card.Container is not Grid window) return;
            var geometry = GetGeometry(card.Item);

            ApplyWindowTurn(card);

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

            // The gutter insets the whole shape, as it insets a rectangle: the mat fills the outer outline, the photo and frame sit on the inner one.
            var shapedCard = CardBorder(window);
            var frame = GetBorderForItem(card.Item);
            double gutter = shapedCard?.Margin.Left ?? 0;
            double radius = frame.CornerRadius;
            // In the card's own coordinates, which start one gutter inside the window.
            var cardOutline = WindowShapes.Placed(geometry, -gutter, -gutter, width, height, radius, gutter)!;
            var matOutline = WindowShapes.Placed(geometry, 0, 0, width, height, radius > 0 ? radius + gutter : 0,
                concaveRadius: Math.Max(0, radius - gutter))!;
            ApplyShapedShadow(card, window, geometry, frame, width, height, gutter);

            // Pointers follow the outline: the empty part of the window has nothing to hit, so clicks reach what is behind.
            if (photoGrid != null)
            {
                photoGrid.Background = null;
                if (hitArea == null)
                {
                    hitArea = new Polygon { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)) };
                    photoGrid.Children.Insert(0, hitArea);
                }
                hitArea.Visibility = Visibility.Visible;
                var hitPoints = new PointCollection();
                foreach (var p in cardOutline) hitPoints.Add(new Point(p.X, p.Y));
                hitArea.Points = hitPoints;
            }

            if (card.NotchHandle != null)
            {
                double nx = geometry.ShapeFlipH ? 1 - geometry.NotchX : geometry.NotchX;
                double ny = geometry.ShapeFlipV ? 1 - geometry.NotchY : geometry.NotchY;
                card.NotchHandle.Margin = new Thickness(nx * width - card.NotchHandle.Width / 2, ny * height - card.NotchHandle.Height / 2, 0, 0);
            }

            // The clip goes on the parts inside the window. A composition visual on the window itself stops it taking pointer input.
            if (window.Tag is CellParts { GutterHost: not null } clippedParts)
                ClipToOutline(clippedParts.GutterHost, matOutline);
            if (shapedCard != null)
            {
                ClipToOutline(shapedCard, cardOutline);
                shapedCard.CornerRadius = new CornerRadius(0);
            }

            if (outlineElement == null)
            {
                outlineElement = new Polygon { IsHitTestVisible = false };
                Canvas.SetZIndex(outlineElement, 90);
                window.Children.Add(outlineElement);
            }
            // The frame follows the outline. The card's own rectangular stroke would only show where the shape meets it.
            bool dashed = string.Equals(frame.StrokeStyle, "Dashed", StringComparison.OrdinalIgnoreCase);
            outlineElement.Stroke = dashed ? ParseBorderBrush(frame.Color) : shapedCard?.BorderBrush ?? ParseBorderBrush(frame.Color);
            outlineElement.StrokeDashArray = dashed ? new DoubleCollection { 3, 2 } : new DoubleCollection();
            outlineElement.Visibility = frame.Thickness > 0 && !IsEmptyCell(card.Item) ? Visibility.Visible : Visibility.Collapsed;
            outlineElement.StrokeThickness = frame.Thickness;
            if (shapedCard != null) shapedCard.BorderThickness = new Thickness(0);
            if (window.Tag is CellParts shapedParts)
            {
                shapedParts.DashedStroke.Visibility = Visibility.Collapsed;
                if (shapedParts.RimGlowStroke != null) shapedParts.RimGlowStroke.Visibility = Visibility.Collapsed;
            }
            // Pulled in by half the stroke, so its outer edge meets the photo's edge and is not cut off there.
            double half = frame.Thickness / 2;
            var strokeOutline = WindowShapes.Placed(geometry, 0, 0, width, height, Math.Max(0, radius - half), gutter + half, radius + half)!;
            var drawn = new PointCollection();
            foreach (var p in strokeOutline) drawn.Add(new Point(p.X, p.Y));
            outlineElement.Points = drawn;
        }

        /// <summary>The shadow is cast by the photo's outline onto the mat. Rendered only when something it depends on has changed.</summary>
        private void ApplyShapedShadow(FreeformCard card, Grid window, CellGeometryDto geometry, CellBorderDto frame, double width, double height, double gutter)
        {
            if (window.Tag is not CellParts { GutterHost: not null } parts) return;
            if (parts.GutterHost.Child is not Image image) return;

            int w = (int)Math.Round(width);
            int h = (int)Math.Round(height);
            // Rim glow is a rectangle's effect, so a shape keeps its shadow whichever is chosen.
            bool drop = frame.ShadowIntensity > 0 && !IsEmptyCell(card.Item) && w >= 2 && h >= 2;
            if (!drop)
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                card.ShadowKey = null;
                return;
            }

            string key = string.Join("|", w, h, gutter, frame.ShadowIntensity, frame.CornerRadius,
                geometry.Shape, geometry.ShapeFlipH, geometry.ShapeFlipV, geometry.NotchX, geometry.NotchY);
            if (key == card.ShadowKey && image.Source != null) return;
            card.ShadowKey = key;

            int previewW = Math.Max(1, w / ShadowPreviewDivisor);
            int previewH = Math.Max(1, h / ShadowPreviewDivisor);
            double scale = 1.0 / ShadowPreviewDivisor;
            var outline = WindowShapes.Placed(geometry, 0, 0, previewW, previewH, frame.CornerRadius * scale, gutter * scale);
            if (outline == null) return;
            WriteShadow(image, FrameShadow.RenderAlpha(previewW, previewH, outline, frame.ShadowIntensity, scale, scale), previewW, previewH);
        }

        private static void ClipToOutline(FrameworkElement part, (double X, double Y)[] outline)
        {
            var visual = ElementCompositionPreview.GetElementVisual(part);
            var points = outline.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
            using var shape = CanvasGeometry.CreatePolygon(CanvasDevice.GetSharedDevice(), points);
            var compositor = visual.Compositor;
            visual.Clip = compositor.CreateGeometricClip(compositor.CreatePathGeometry(new CompositionPath(shape)));
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

        /// <summary>The rotate handle, the L's inner-corner handle, and the resize corners and outline. All show while the pointer is over the window.</summary>
        private void AttachWindowHandles(FreeformCard card)
        {
            if (card.Container is not Grid window) return;

            var rotate = MakeWindowHandle(28, "\uE7AD");
            rotate.HorizontalAlignment = HorizontalAlignment.Center;
            rotate.VerticalAlignment = VerticalAlignment.Top;
            rotate.Margin = new Thickness(0, 12, 0, 0);
            rotate.IsHitTestVisible = false;
            ToolTipService.SetToolTip(rotate, "Drag to rotate the window. Hold Ctrl to rotate the shape and keep the photo still. Hold Shift for 15 degree steps.");

            bool turning = false;
            double startPointer = 0;
            double startRotation = 0;
            double startCounter = 0;
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
                startCounter = GetGeometry(card.Item).ImageCounter;
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
                bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var geometry = GetGeometry(card.Item);
                geometry.WindowRotation = angle;
                // With Ctrl the photo turns back by the same amount, so only the shape moves on screen.
                geometry.ImageCounter = ctrl ? startCounter - (angle - startRotation) : startCounter;
                // Only the turn changes during the drag, so the frame and its shadow are not drawn again.
                ApplyWindowTurn(card);
                if (window.Tag is CellParts parts) parts.Bound.ApplyGeometry?.Invoke();
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
            notch.IsHitTestVisible = false;
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

            // The window's rectangle, which is what resizes. Dark under light dashes, to read on any photo.
            Microsoft.UI.Xaml.Shapes.Rectangle MakeBounds(Windows.UI.Color color, DoubleCollection? dashes)
            {
                var bounds = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1,
                    StrokeDashArray = dashes,
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                Canvas.SetZIndex(bounds, 195);
                window.Children.Add(bounds);
                return bounds;
            }
            var boundsDark = MakeBounds(Windows.UI.Color.FromArgb(170, 0, 0, 0), null);
            var boundsLight = MakeBounds(Windows.UI.Color.FromArgb(240, 255, 255, 255), new DoubleCollection { 4, 3 });
            var grips = window.Children.OfType<FreeformResizeHandle>().Select(h => h.Grip).OfType<Border>().ToList();

            // The handles can sit outside a shape, so they stay a moment for the pointer to reach them.
            void ShowHandles(bool show)
            {
                rotate.Opacity = show ? 1 : 0;
                notch.Opacity = show ? 1 : 0;
                boundsDark.Opacity = show ? 1 : 0;
                boundsLight.Opacity = show ? 1 : 0;
                foreach (var grip in grips) grip.Opacity = show ? 1 : 0;
                rotate.IsHitTestVisible = show;
                notch.IsHitTestVisible = show;
            }
            var hideHandles = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WindowHoverGraceMs) };
            hideHandles.Tick += (_, _) =>
            {
                if (turning || moving) return;
                hideHandles.Stop();
                ShowHandles(false);
            };
            window.PointerEntered += (_, _) =>
            {
                hideHandles.Stop();
                ShowHandles(true);
            };
            window.PointerExited += (_, _) => hideHandles.Start();
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
                if (_cellRoots[i] is not FrameworkElement cell || IsEmptyCell(_items[i]) || cell.ActualWidth < 1) continue;
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

            var undo = MakeAdvancedItem("Undo", "\uE7A7", Undo);
            undo.KeyboardAcceleratorTextOverride = "Ctrl+Z";
            undo.IsEnabled = CanUndo;
            ToolTipService.SetToolTip(undo, "Undo (Ctrl+Z)");
            AdvancedMenuFlyout.Items.Add(undo);

            var redo = MakeAdvancedItem("Redo", "\uE7A6", Redo);
            redo.KeyboardAcceleratorTextOverride = "Ctrl+Y";
            redo.IsEnabled = CanRedo;
            ToolTipService.SetToolTip(redo, "Redo (Ctrl+Y)");
            AdvancedMenuFlyout.Items.Add(redo);

            AdvancedMenuFlyout.Items.Add(new MenuFlyoutSeparator());
            var fillFit = MakeAdvancedItem(_isFillMode ? "Fit whole photos" : "Fill cells", _isFillMode ? "\uE73F" : "\uE740", () =>
            {
                ToggleFillMode();
                NoteUserAction();
            });
            ToolTipService.SetToolTip(fillFit, _isFillMode ? "Show the whole of each photo" : "Crop each photo to fill its cell");
            AdvancedMenuFlyout.Items.Add(fillFit);
        }

        /// <summary>The surface and color behind the cells, under the canvas shape.</summary>
        private void AddCanvasLookMenus(MenuFlyout menu)
        {
            var surface = new MenuFlyoutSubItem { Text = "Surface" };
            foreach (string name in CanvasLook.Surfaces)
            {
                string chosen = name;
                var choice = MakeChoiceItem(name, _canvasSurface == name, () => SetCanvas(_canvasColor, chosen));
                ToolTipService.SetToolTip(choice, CanvasLook.Describe(name));
                surface.Items.Add(choice);
            }
            ToolTipService.SetToolTip(surface, "Shows where no cell covers the canvas: empty cells, and around photos in Fit");
            menu.Items.Add(surface);

            var color = new MenuFlyoutSubItem { Text = "Color" };
            foreach (var (name, hex) in CanvasLook.Colors)
            {
                string chosen = hex;
                color.Items.Add(MakeChoiceItem(name, string.Equals(_canvasColor, hex, StringComparison.OrdinalIgnoreCase),
                    () => SetCanvas(chosen, _canvasSurface)));
            }
            bool customCanvas = !CanvasLook.Colors.Any(c => string.Equals(c.Hex, _canvasColor, StringComparison.OrdinalIgnoreCase));
            color.Items.Add(new MenuFlyoutSeparator());
            color.Items.Add(MakeChoiceItem("Custom…", customCanvas, () => PickCustomColor(() => _canvasColor, hex =>
            {
                SetCanvas(hex, _canvasSurface);
                NoteUserAction();
            })));
            menu.Items.Add(color);

            var frame = new MenuFlyoutItem { Text = "Frame & Border\u2026" };
            frame.Click += (_, _) => OpenCanvasFrameCard();
            ToolTipService.SetToolTip(frame, "A line, rounded corners and a margin around the whole canvas");
            menu.Items.Add(frame);
        }

        /// <summary>The same order as a cell, from the outside in: gutter and mat, then the line, then the grid. The line never covers a photo.</summary>
        private void ApplyCanvasFrame()
        {
            var frame = _canvasFrame;
            double gutter = Math.Max(0, frame.Padding);
            double line = Math.Max(0, frame.Thickness);
            CellsGrid.Margin = new Thickness(gutter + line);

            var mat = FrameColor.MatFill(frame.GutterMaterial, frame.GutterColor);
            CanvasFrameMat.Background = mat.A > 0 ? new SolidColorBrush(ToColor(mat)) : null;

            string style = frame.StrokeStyle ?? "Solid";
            bool dashed = string.Equals(style, "Dashed", StringComparison.OrdinalIgnoreCase);
            CanvasFrameStroke.Margin = new Thickness(gutter);
            CanvasFrameStroke.CornerRadius = new CornerRadius(frame.CornerRadius);
            CanvasFrameStroke.BorderThickness = new Thickness(dashed ? 0 : line);
            if (string.Equals(style, "InnerMuted", StringComparison.OrdinalIgnoreCase))
            {
                CanvasFrameStroke.BorderBrush = new SolidColorBrush(ToColor(FrameColor.MutedStroke(frame.Color)));
            }
            else if (string.Equals(style, "Gradient", StringComparison.OrdinalIgnoreCase))
            {
                var primary = FrameColor.Parse(frame.Color);
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                brush.GradientStops.Add(new GradientStop { Color = ToColor(primary), Offset = 0 });
                brush.GradientStops.Add(new GradientStop { Color = ToColor(FrameColor.GradientEnd(frame.SecondaryColor, primary)), Offset = 1 });
                CanvasFrameStroke.BorderBrush = brush;
            }
            else
            {
                CanvasFrameStroke.BorderBrush = ParseBorderBrush(frame.Color);
            }

            var dash = FrameColor.Parse(frame.Color);
            CanvasFrameDashes.Margin = new Thickness(gutter);
            CanvasFrameDashes.Visibility = dashed && line > 0 && dash.A > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (dashed)
            {
                CanvasFrameDashes.Stroke = new SolidColorBrush(ToColor(dash));
                CanvasFrameDashes.StrokeThickness = line;
                CanvasFrameDashes.StrokeDashArray = new DoubleCollection { 3, 2 };
                CanvasFrameDashes.RadiusX = frame.CornerRadius;
                CanvasFrameDashes.RadiusY = frame.CornerRadius;
            }

            bool rim = frame.ShadowIntensity > 0 && frame.RimGlow;
            CanvasFrameRim.Margin = new Thickness(gutter);
            CanvasFrameRim.Visibility = rim ? Visibility.Visible : Visibility.Collapsed;
            if (rim)
            {
                CanvasFrameRim.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Clamp(frame.ShadowIntensity * 2.1, 30, 240), 255, 255, 255));
                CanvasFrameRim.StrokeThickness = Math.Max(1.0, frame.ShadowIntensity * 0.035 + 1.0);
                CanvasFrameRim.RadiusX = frame.CornerRadius;
                CanvasFrameRim.RadiusY = frame.CornerRadius;
            }

            ApplyCanvasCorners();
        }

        /// <summary>
        /// As in a cell, the canvas and its mat stay square and only the line and what is inside it round.
        /// The grid is cut to the inside of the line, and the mat is a whole-canvas fill with the line's shape cut out.
        /// </summary>
        private void ApplyCanvasCorners()
        {
            var frame = _canvasFrame;
            double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
            var gridVisual = ElementCompositionPreview.GetElementVisual(CellsGrid);
            var matVisual = ElementCompositionPreview.GetElementVisual(CanvasFrameMat);
            if (width < 1 || height < 1)
            {
                gridVisual.Clip = null;
                matVisual.Clip = null;
                return;
            }
            var compositor = gridVisual.Compositor;
            double radius = Math.Max(0, frame.CornerRadius);
            double gutter = Math.Max(0, frame.Padding);
            double line = Math.Max(0, frame.Thickness);

            CompositionClip Rounded(double w, double h, double r)
            {
                var geometry = compositor.CreateRoundedRectangleGeometry();
                geometry.Size = new Vector2((float)Math.Max(1, w), (float)Math.Max(1, h));
                geometry.CornerRadius = new Vector2((float)r, (float)r);
                return compositor.CreateGeometricClip(geometry);
            }

            double inset = gutter + line;
            gridVisual.Clip = radius > line ? Rounded(width - 2 * inset, height - 2 * inset, radius - line) : null;

            using var ring = CanvasFrameRing(CanvasDevice.GetSharedDevice(), width, height, gutter + line / 2, Math.Max(0, radius - line / 2));
            matVisual.Clip = compositor.CreateGeometricClip(compositor.CreatePathGeometry(new CompositionPath(ring)));

            // The grid and line cast it onto the gutter, as a cell's card does onto its mat. It is on the mat's layer, under the line.
            ApplyDropShadow(CanvasFrameMat, frame);
        }

        private void SetCanvasFrame(CellBorderDto? frame)
        {
            _canvasFrame = frame?.Clone() ?? FrameLooks.Flush();
            ApplyCanvasFrame();
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

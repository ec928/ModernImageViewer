using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        private readonly List<FreeformCard> _freeformCards = new();
        private int _maxFreeformZIndex = 10;

        public IReadOnlyList<FreeformCard> ActiveFreeformCards => _freeformCards;

        public sealed class FreeformCard
        {
            public ImageItem Item { get; set; } = null!;
            public int SourceSlot { get; set; }
            public Border Container { get; set; } = null!;
            public Grid Header { get; set; } = null!;
            public Image Photo { get; set; } = null!;
            public CompositeTransform Transform { get; set; } = null!;
            public CellBorderDto? Border { get; set; }
            public int ZIndex { get; set; } = 1;
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        public void SpawnFreeformCell(ImageItem item, FreeformCellDto? restoreDto = null)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;

            // If this item is already open in Freeform, bring its card to front
            var existing = _freeformCards.FirstOrDefault(c => string.Equals(c.Item.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                BringFreeformCardToFront(existing);
                return;
            }

            int sourceSlot = restoreDto?.SourceSlot ?? Slot(item);

            // Hide the photo in the underlying grid cell (exact same hide mechanism)
            if (!_hiddenItems.Contains(item))
            {
                ToggleHideImage(item);
            }

            // Calculate initial geometry
            double w = 400;
            double h = 300;
            double x = 40;
            double y = 40;

            double maxW = Math.Max(320, RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1280);
            double maxH = Math.Max(240, RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720);

            if (restoreDto != null)
            {
                w = restoreDto.Width > 0 ? restoreDto.Width : 400;
                h = restoreDto.Height > 0 ? restoreDto.Height : 300;
                x = restoreDto.X;
                y = restoreDto.Y;
            }
            else if (sourceSlot >= 0 && sourceSlot < _cellRoots.Length && _cellRoots[sourceSlot] is FrameworkElement cellRoot && cellRoot.ActualWidth > 50)
            {
                try
                {
                    var cellBounds = cellRoot.TransformToVisual(FreeformCanvas).TransformBounds(new Rect(0, 0, cellRoot.ActualWidth, cellRoot.ActualHeight));
                    w = Math.Max(200, cellBounds.Width);
                    h = Math.Max(150, cellBounds.Height);
                    x = cellBounds.X;
                    y = cellBounds.Y;
                }
                catch
                {
                    x = 60 + (_freeformCards.Count * 28);
                    y = 60 + (_freeformCards.Count * 28);
                }
            }
            else
            {
                x = 60 + (_freeformCards.Count * 28);
                y = 60 + (_freeformCards.Count * 28);
            }

            w = Math.Clamp(w, 180, maxW);
            h = Math.Clamp(h, 140, maxH);
            x = Math.Clamp(x, 0, Math.Max(0, maxW - w));
            y = Math.Clamp(y, 0, Math.Max(0, maxH - h));

            int zIndex = restoreDto?.ZIndex ?? (++_maxFreeformZIndex);
            if (zIndex > _maxFreeformZIndex) _maxFreeformZIndex = zIndex;

            // 1. Outer Floating Card Container
            var cardContainer = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromArgb(246, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Translation = new Vector3(0, 0, 32)
            };
            try { cardContainer.Shadow = new ThemeShadow(); } catch { }

            Canvas.SetLeft(cardContainer, x);
            Canvas.SetTop(cardContainer, y);
            Canvas.SetZIndex(cardContainer, zIndex);

            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32, GridUnitType.Pixel) });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 2. Header Bar (Drag Handle)
            var header = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Padding = new Thickness(10, 0, 4, 0),
                CornerRadius = new CornerRadius(9, 9, 0, 0)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBadge = new FontIcon
            {
                Glyph = "\uEE49",
                FontSize = 13,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 200, 59)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(iconBadge, 0);
            header.Children.Add(iconBadge);

            var titleText = new TextBlock
            {
                Text = item.Name ?? "Photo",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _menuWhitePrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 1);
            header.Children.Add(titleText);

            var headerButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            var card = new FreeformCard
            {
                Item = item,
                SourceSlot = sourceSlot,
                Container = cardContainer,
                Header = header,
                Border = GetBorderForItem(item).Clone(),
                ZIndex = zIndex,
                X = x,
                Y = y,
                Width = w,
                Height = h
            };

            // Button: Return / Dock to Grid
            var dockBtn = CreateCardHeaderButton("\uE8CA", "Return / Dock to Grid Cell", () => DockFreeformCard(card));
            headerButtons.Children.Add(dockBtn);

            // Button: Solo
            var soloBtn = CreateCardHeaderButton("\uE922", "Solo / Maximize", () =>
            {
                int curSlot = Slot(item);
                if (curSlot >= 0) EnterSolo(curSlot);
            });
            headerButtons.Children.Add(soloBtn);

            // Button: Close
            var closeBtn = CreateCardHeaderButton("\uE711", "Close Freeform View", () => CloseFreeformCard(card));
            headerButtons.Children.Add(closeBtn);

            Grid.SetColumn(headerButtons, 2);
            header.Children.Add(headerButtons);
            Grid.SetRow(header, 0);
            cardGrid.Children.Add(header);

            // 3. Image Viewport (Row 1)
            var viewport = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 12, 12, 12)),
                CornerRadius = new CornerRadius(0, 0, 9, 9)
            };

            var transform = new CompositeTransform();
            card.Transform = transform;

            var freeformImg = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform
            };
            card.Photo = freeformImg;

            try
            {
                var bmp = new BitmapImage(new Uri(item.Path, UriKind.Absolute));
                freeformImg.Source = bmp;
            }
            catch { }

            viewport.Children.Add(freeformImg);

            // Restore zoom/pan if present
            if (restoreDto?.Zoom.HasValue == true && restoreDto.Zoom.Value > 0)
            {
                transform.ScaleX = restoreDto.Zoom.Value;
                transform.ScaleY = restoreDto.Zoom.Value;
            }
            if (restoreDto?.PanX.HasValue == true) transform.TranslateX = restoreDto.PanX.Value;
            if (restoreDto?.PanY.HasValue == true) transform.TranslateY = restoreDto.PanY.Value;

            // Pan & Zoom inside Freeform viewport
            bool isPanning = false;
            Point panStart = default;

            viewport.PointerWheelChanged += (_, e) =>
            {
                int delta = e.GetCurrentPoint(viewport).Properties.MouseWheelDelta;
                double factor = delta > 0 ? 1.15 : 0.87;
                double newScale = Math.Clamp(transform.ScaleX * factor, 0.4, 12.0);
                transform.ScaleX = newScale;
                transform.ScaleY = newScale;
                e.Handled = true;
            };

            viewport.PointerPressed += (_, e) =>
            {
                BringFreeformCardToFront(card);
                var prop = e.GetCurrentPoint(viewport).Properties;
                if (prop.IsMiddleButtonPressed)
                {
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                    e.Handled = true;
                }
                else if (prop.IsLeftButtonPressed)
                {
                    isPanning = true;
                    panStart = e.GetCurrentPoint(viewport).Position;
                    viewport.CapturePointer(e.Pointer);
                }
            };

            viewport.PointerMoved += (_, e) =>
            {
                if (isPanning)
                {
                    Point cur = e.GetCurrentPoint(viewport).Position;
                    transform.TranslateX += cur.X - panStart.X;
                    transform.TranslateY += cur.Y - panStart.Y;
                    panStart = cur;
                }
            };

            viewport.PointerReleased += (_, e) =>
            {
                if (isPanning)
                {
                    isPanning = false;
                    viewport.ReleasePointerCapture(e.Pointer);
                }
            };

            Grid.SetRow(viewport, 1);
            cardGrid.Children.Add(viewport);

            // 4. Resize Grip (Bottom-Right Corner)
            var resizeGrip = new Border
            {
                Width = 20,
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Child = new FontIcon
                {
                    Glyph = "\uE7C5",
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 3, 3)
                }
            };
            Grid.SetRow(resizeGrip, 1);
            cardGrid.Children.Add(resizeGrip);

            // Drag to Move
            bool isMoving = false;
            Point dragStart = default;
            double startLeft = x;
            double startTop = y;

            header.PointerPressed += (_, e) =>
            {
                BringFreeformCardToFront(card);
                var prop = e.GetCurrentPoint(header).Properties;
                if (prop.IsLeftButtonPressed)
                {
                    isMoving = true;
                    dragStart = e.GetCurrentPoint(FreeformCanvas).Position;
                    startLeft = Canvas.GetLeft(cardContainer);
                    startTop = Canvas.GetTop(cardContainer);
                    header.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            header.PointerMoved += (_, e) =>
            {
                if (isMoving)
                {
                    Point cur = e.GetCurrentPoint(FreeformCanvas).Position;
                    double dx = cur.X - dragStart.X;
                    double dy = cur.Y - dragStart.Y;

                    double boundW = Math.Max(200, RootGrid.ActualWidth);
                    double boundH = Math.Max(150, RootGrid.ActualHeight);

                    double newX = Math.Clamp(startLeft + dx, 0, Math.Max(0, boundW - cardContainer.ActualWidth));
                    double newY = Math.Clamp(startTop + dy, 0, Math.Max(0, boundH - cardContainer.ActualHeight));

                    Canvas.SetLeft(cardContainer, newX);
                    Canvas.SetTop(cardContainer, newY);
                    card.X = newX;
                    card.Y = newY;
                }
            };

            header.PointerReleased += (_, e) =>
            {
                if (isMoving)
                {
                    isMoving = false;
                    header.ReleasePointerCapture(e.Pointer);
                }
            };

            // Resize Drag
            bool isResizing = false;
            Point resizeStart = default;
            double startW = w;
            double startH = h;

            resizeGrip.PointerPressed += (_, e) =>
            {
                BringFreeformCardToFront(card);
                var prop = e.GetCurrentPoint(resizeGrip).Properties;
                if (prop.IsLeftButtonPressed)
                {
                    isResizing = true;
                    resizeStart = e.GetCurrentPoint(FreeformCanvas).Position;
                    startW = cardContainer.ActualWidth;
                    startH = cardContainer.ActualHeight;
                    resizeGrip.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            resizeGrip.PointerMoved += (_, e) =>
            {
                if (isResizing)
                {
                    Point cur = e.GetCurrentPoint(FreeformCanvas).Position;
                    double dx = cur.X - resizeStart.X;
                    double dy = cur.Y - resizeStart.Y;

                    double maxAvailW = Math.Max(200, RootGrid.ActualWidth - Canvas.GetLeft(cardContainer));
                    double maxAvailH = Math.Max(150, RootGrid.ActualHeight - Canvas.GetTop(cardContainer));

                    double newW = Math.Clamp(startW + dx, 180, maxAvailW);
                    double newH = Math.Clamp(startH + dy, 140, maxAvailH);

                    cardContainer.Width = newW;
                    cardContainer.Height = newH;
                    card.Width = newW;
                    card.Height = newH;
                }
            };

            resizeGrip.PointerReleased += (_, e) =>
            {
                if (isResizing)
                {
                    isResizing = false;
                    resizeGrip.ReleasePointerCapture(e.Pointer);
                }
            };

            cardContainer.PointerPressed += (_, _) => BringFreeformCardToFront(card);

            cardContainer.Child = cardGrid;
            FreeformCanvas.Children.Add(cardContainer);
            _freeformCards.Add(card);
        }

        public void BringFreeformCardToFront(FreeformCard card)
        {
            if (card?.Container == null) return;
            card.ZIndex = ++_maxFreeformZIndex;
            Canvas.SetZIndex(card.Container, card.ZIndex);
        }

        public void DockFreeformCard(FreeformCard card)
        {
            if (card == null) return;
            FreeformCanvas.Children.Remove(card.Container);
            _freeformCards.Remove(card);

            // Unhide the underlying photo cell in the grid
            if (_hiddenItems.Contains(card.Item))
            {
                ToggleHideImage(card.Item);
            }
        }

        public void CloseFreeformCard(FreeformCard card)
        {
            if (card == null) return;
            FreeformCanvas.Children.Remove(card.Container);
            _freeformCards.Remove(card);
        }

        public void ClearAllFreeformCards()
        {
            FreeformCanvas.Children.Clear();
            _freeformCards.Clear();
            _maxFreeformZIndex = 10;
        }

        private Button CreateCardHeaderButton(string glyph, string tip, Action onClick)
        {
            var btn = new Button
            {
                Width = 26,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = _menuWhiteSecondary
                }
            };
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}

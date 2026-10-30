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
        private int _maxFreeformZIndex = 1000;

        public IReadOnlyList<FreeformCard> ActiveFreeformCards => _freeformCards;

        public sealed class FreeformCard
        {
            public ImageItem Item { get; set; } = null!;
            public int SourceSlot { get; set; }
            public Border Container { get; set; } = null!;
            public Grid Header { get; set; } = null!;
            public Grid Viewport { get; set; } = null!;
            public Image Photo { get; set; } = null!;
            public CompositeTransform Transform { get; set; } = null!;
            public CellBorderDto? Border { get; set; }
            public int ZIndex { get; set; } = 1000;
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

            // Calculate geometry
            double maxW = Math.Max(320, RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1280);
            double maxH = Math.Max(240, RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720);

            double w = 480;
            double h = 360;
            double x = 40;
            double y = 40;

            if (restoreDto != null)
            {
                w = restoreDto.Width > 0 ? restoreDto.Width : 480;
                h = restoreDto.Height > 0 ? restoreDto.Height : 360;
                x = restoreDto.X;
                y = restoreDto.Y;
            }
            else if (sourceSlot >= 0 && sourceSlot < _cellRoots.Length && _cellRoots[sourceSlot] is FrameworkElement cellRoot && cellRoot.ActualWidth > 50)
            {
                try
                {
                    // Transform cell bounds to RootGrid coordinate space
                    var cellBounds = cellRoot.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, cellRoot.ActualWidth, cellRoot.ActualHeight));
                    
                    // Sized gracefully over the cell slot with a subtle reveal of the underlying hidden card
                    w = Math.Max(260, Math.Min(cellBounds.Width * 0.90, maxW * 0.90));
                    h = Math.Max(200, Math.Min(cellBounds.Height * 0.90, maxH * 0.90));
                    x = cellBounds.X + (cellBounds.Width - w) / 2.0;
                    y = cellBounds.Y + (cellBounds.Height - h) / 2.0;
                }
                catch
                {
                    w = Math.Min(520, maxW * 0.65);
                    h = Math.Min(380, maxH * 0.65);
                    x = 60 + (_freeformCards.Count * 28);
                    y = 60 + (_freeformCards.Count * 28);
                }
            }
            else
            {
                w = Math.Min(520, maxW * 0.65);
                h = Math.Min(380, maxH * 0.65);
                x = 60 + (_freeformCards.Count * 28);
                y = 60 + (_freeformCards.Count * 28);
            }

            w = Math.Clamp(w, 200, maxW);
            h = Math.Clamp(h, 150, maxH);
            x = Math.Clamp(x, 0, Math.Max(0, maxW - w));
            y = Math.Clamp(y, 0, Math.Max(0, maxH - h));

            int zIndex = restoreDto?.ZIndex ?? (++_maxFreeformZIndex);
            if (zIndex > _maxFreeformZIndex) _maxFreeformZIndex = zIndex;

            var borderSettings = GetBorderForItem(item).Clone();

            // 1. Outer Floating Card Container (Maximum Z-Index & 3D Elevation Layer)
            var cardContainer = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromArgb(246, 18, 18, 18)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Translation = new Vector3(0, 0, 256 + ((zIndex - 1000) * 4))
            };
            try { cardContainer.Shadow = new ThemeShadow(); } catch { }

            Canvas.SetLeft(cardContainer, x);
            Canvas.SetTop(cardContainer, y);
            Canvas.SetZIndex(cardContainer, zIndex);

            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32, GridUnitType.Pixel) });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 2. Header Bar (Drag Handle & Window Controls)
            var header = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
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
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 204, 0)),
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

            // 3. Image Viewport (Row 1)
            var viewport = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 12, 12, 12)),
                CornerRadius = new CornerRadius(0, 0, 9, 9)
            };

            var transform = new CompositeTransform();

            var freeformImg = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform
            };

            try
            {
                var bmp = new BitmapImage();
                bmp.UriSource = new Uri(item.Path, UriKind.Absolute);
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

            var card = new FreeformCard
            {
                Item = item,
                SourceSlot = sourceSlot,
                Container = cardContainer,
                Header = header,
                Viewport = viewport,
                Photo = freeformImg,
                Transform = transform,
                Border = borderSettings,
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
            var closeBtn = CreateCardHeaderButton("\uE8BB", "Close Freeform View", () => CloseFreeformCard(card));
            closeBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
            headerButtons.Children.Add(closeBtn);

            Grid.SetColumn(headerButtons, 2);
            header.Children.Add(headerButtons);
            Grid.SetRow(header, 0);
            cardGrid.Children.Add(header);

            Grid.SetRow(viewport, 1);
            cardGrid.Children.Add(viewport);

            // 4. Resize Grip (Bottom-Right Corner)
            var resizeGrip = new Border
            {
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Child = new FontIcon
                {
                    Glyph = "\uE7C5",
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 3, 3)
                }
            };
            Grid.SetRow(resizeGrip, 1);
            cardGrid.Children.Add(resizeGrip);

            // ---------------------------------------------------------------------------------
            // 5. DRAG-TO-MOVE (Operates like a Detach image across the entire app window)
            // ---------------------------------------------------------------------------------
            bool isMoving = false;
            Point dragStartInRoot = default;
            double startLeft = x;
            double startTop = y;
            uint movePointerId = 0;

            void StartCardMove(object sender, PointerRoutedEventArgs e)
            {
                BringFreeformCardToFront(card);
                var prop = e.GetCurrentPoint(cardContainer).Properties;
                if (!prop.IsLeftButtonPressed) return;

                // Check for Alt modifier: Alt+drag is reserved for panning the photo inside the viewport
                bool isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (isAlt && ReferenceEquals(sender, viewport)) return;

                isMoving = true;
                movePointerId = e.Pointer.PointerId;
                dragStartInRoot = e.GetCurrentPoint(RootGrid).Position;
                startLeft = Canvas.GetLeft(cardContainer);
                if (double.IsNaN(startLeft)) startLeft = card.X;
                startTop = Canvas.GetTop(cardContainer);
                if (double.IsNaN(startTop)) startTop = card.Y;

                if (sender is UIElement uie)
                {
                    uie.CapturePointer(e.Pointer);
                }
                e.Handled = true;
            }

            void UpdateCardMove(object sender, PointerRoutedEventArgs e)
            {
                if (isMoving && e.Pointer.PointerId == movePointerId)
                {
                    Point cur = e.GetCurrentPoint(RootGrid).Position;
                    double dx = cur.X - dragStartInRoot.X;
                    double dy = cur.Y - dragStartInRoot.Y;

                    double boundW = Math.Max(200, RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1280);
                    double boundH = Math.Max(150, RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720);

                    double cardW = cardContainer.ActualWidth > 0 ? cardContainer.ActualWidth : card.Width;
                    double cardH = cardContainer.ActualHeight > 0 ? cardContainer.ActualHeight : card.Height;

                    // Clamped within app boundaries
                    double newX = Math.Clamp(startLeft + dx, 0, Math.Max(0, boundW - cardW));
                    double newY = Math.Clamp(startTop + dy, 0, Math.Max(0, boundH - cardH));

                    Canvas.SetLeft(cardContainer, newX);
                    Canvas.SetTop(cardContainer, newY);
                    card.X = newX;
                    card.Y = newY;
                    e.Handled = true;
                }
            }

            void EndCardMove(object sender, PointerRoutedEventArgs e)
            {
                if (isMoving && e.Pointer.PointerId == movePointerId)
                {
                    isMoving = false;
                    if (sender is UIElement uie)
                    {
                        try { uie.ReleasePointerCapture(e.Pointer); } catch { }
                    }
                    e.Handled = true;
                }
            }

            header.PointerPressed += StartCardMove;
            header.PointerMoved += UpdateCardMove;
            header.PointerReleased += EndCardMove;
            header.PointerCanceled += EndCardMove;

            viewport.PointerPressed += StartCardMove;
            viewport.PointerMoved += UpdateCardMove;
            viewport.PointerReleased += EndCardMove;
            viewport.PointerCanceled += EndCardMove;

            // Double tap header to toggle Maximize / Restore
            bool isCardMaximized = false;
            double preMaxX = x, preMaxY = y, preMaxW = w, preMaxH = h;
            header.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                if (!isCardMaximized)
                {
                    preMaxX = Canvas.GetLeft(cardContainer);
                    preMaxY = Canvas.GetTop(cardContainer);
                    preMaxW = cardContainer.Width;
                    preMaxH = cardContainer.Height;

                    Canvas.SetLeft(cardContainer, 0);
                    Canvas.SetTop(cardContainer, 0);
                    cardContainer.Width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : maxW;
                    cardContainer.Height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : maxH;
                    isCardMaximized = true;
                }
                else
                {
                    Canvas.SetLeft(cardContainer, preMaxX);
                    Canvas.SetTop(cardContainer, preMaxY);
                    cardContainer.Width = preMaxW;
                    cardContainer.Height = preMaxH;
                    isCardMaximized = false;
                }
                card.X = Canvas.GetLeft(cardContainer);
                card.Y = Canvas.GetTop(cardContainer);
                card.Width = cardContainer.Width;
                card.Height = cardContainer.Height;
            };

            // ---------------------------------------------------------------------------------
            // 6. PHOTO PAN & ZOOM (Middle-click reset/drag, Alt+Left drag, Mouse Wheel, Double tap)
            // ---------------------------------------------------------------------------------
            bool isAltPanning = false;
            Point panStart = default;
            uint panPointerId = 0;

            viewport.PointerWheelChanged += (_, e) =>
            {
                int delta = e.GetCurrentPoint(viewport).Properties.MouseWheelDelta;
                double factor = delta > 0 ? 1.15 : 0.87;
                double newScale = Math.Clamp(transform.ScaleX * factor, 0.4, 12.0);
                transform.ScaleX = newScale;
                transform.ScaleY = newScale;
                e.Handled = true;
            };

            viewport.PointerPressed += (s, e) =>
            {
                var prop = e.GetCurrentPoint(viewport).Properties;
                if (prop.IsMiddleButtonPressed)
                {
                    // Middle-click reset
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                    e.Handled = true;
                    return;
                }

                bool isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (isAlt && prop.IsLeftButtonPressed)
                {
                    isAltPanning = true;
                    panPointerId = e.Pointer.PointerId;
                    panStart = e.GetCurrentPoint(viewport).Position;
                    viewport.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            viewport.PointerMoved += (s, e) =>
            {
                if (isAltPanning && e.Pointer.PointerId == panPointerId)
                {
                    Point cur = e.GetCurrentPoint(viewport).Position;
                    transform.TranslateX += cur.X - panStart.X;
                    transform.TranslateY += cur.Y - panStart.Y;
                    panStart = cur;
                    e.Handled = true;
                }
            };

            viewport.PointerReleased += (s, e) =>
            {
                if (isAltPanning && e.Pointer.PointerId == panPointerId)
                {
                    isAltPanning = false;
                    try { viewport.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            viewport.PointerCanceled += (s, e) =>
            {
                if (isAltPanning && e.Pointer.PointerId == panPointerId)
                {
                    isAltPanning = false;
                    try { viewport.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            viewport.DoubleTapped += (_, e) =>
            {
                if (Math.Abs(transform.ScaleX - 1.0) > 0.05 || Math.Abs(transform.TranslateX) > 1 || Math.Abs(transform.TranslateY) > 1)
                {
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                }
                else
                {
                    transform.ScaleX = 2.0;
                    transform.ScaleY = 2.0;
                }
                e.Handled = true;
            };

            // ---------------------------------------------------------------------------------
            // 7. CARD RESIZE DRAG
            // ---------------------------------------------------------------------------------
            bool isResizing = false;
            Point resizeStartInRoot = default;
            double startW = w;
            double startH = h;
            uint resizePointerId = 0;

            resizeGrip.PointerPressed += (s, e) =>
            {
                BringFreeformCardToFront(card);
                var prop = e.GetCurrentPoint(resizeGrip).Properties;
                if (prop.IsLeftButtonPressed)
                {
                    isResizing = true;
                    resizePointerId = e.Pointer.PointerId;
                    resizeStartInRoot = e.GetCurrentPoint(RootGrid).Position;
                    startW = cardContainer.ActualWidth > 0 ? cardContainer.ActualWidth : card.Width;
                    startH = cardContainer.ActualHeight > 0 ? cardContainer.ActualHeight : card.Height;
                    resizeGrip.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            resizeGrip.PointerMoved += (s, e) =>
            {
                if (isResizing && e.Pointer.PointerId == resizePointerId)
                {
                    Point cur = e.GetCurrentPoint(RootGrid).Position;
                    double dx = cur.X - resizeStartInRoot.X;
                    double dy = cur.Y - resizeStartInRoot.Y;

                    double maxAvailW = Math.Max(200, RootGrid.ActualWidth - Canvas.GetLeft(cardContainer));
                    double maxAvailH = Math.Max(150, RootGrid.ActualHeight - Canvas.GetTop(cardContainer));

                    double newW = Math.Clamp(startW + dx, 200, maxAvailW);
                    double newH = Math.Clamp(startH + dy, 150, maxAvailH);

                    cardContainer.Width = newW;
                    cardContainer.Height = newH;
                    card.Width = newW;
                    card.Height = newH;
                    e.Handled = true;
                }
            };

            resizeGrip.PointerReleased += (s, e) =>
            {
                if (isResizing && e.Pointer.PointerId == resizePointerId)
                {
                    isResizing = false;
                    try { resizeGrip.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            resizeGrip.PointerCanceled += (s, e) =>
            {
                if (isResizing && e.Pointer.PointerId == resizePointerId)
                {
                    isResizing = false;
                    try { resizeGrip.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            // ---------------------------------------------------------------------------------
            // 8. RIGHT-CLICK CONTEXT MENU
            // ---------------------------------------------------------------------------------
            var cardMenu = new MenuFlyout
            {
                MenuFlyoutPresenterStyle = (Style)Resources["MultiViewMenuFlyoutPresenterStyle"]
            };
            FillCellMenu(cardMenu, item);
            cardMenu.Opening += (_, _) =>
            {
                FillCellMenu(cardMenu, item);
                EnforceDarkFlyoutPresenter();
            };
            cardContainer.ContextFlyout = cardMenu;

            cardContainer.Child = cardGrid;
            FreeformCanvas.Children.Add(cardContainer);
            _freeformCards.Add(card);
        }

        public void BringFreeformCardToFront(FreeformCard card)
        {
            if (card?.Container == null) return;
            card.ZIndex = ++_maxFreeformZIndex;
            Canvas.SetZIndex(card.Container, card.ZIndex);
            card.Container.Translation = new Vector3(0, 0, 256 + ((_maxFreeformZIndex - 1000) * 4));
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
            _maxFreeformZIndex = 1000;
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
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}

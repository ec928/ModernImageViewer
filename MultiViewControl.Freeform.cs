using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            public Grid ContentGrid { get; set; } = null!;
            public Image Photo { get; set; } = null!;
            public CompositeTransform Transform { get; set; } = null!;
            public Grid TopLeftInfo { get; set; } = null!;
            public Grid TopRightControls { get; set; } = null!;
            public Grid BottomHud { get; set; } = null!;
            public TextBlock ZoomInfoText { get; set; } = null!;
            public TextBlock SecondaryInfoText { get; set; } = null!;
            public DispatcherTimer HudTimer { get; set; } = null!;
            public CellBorderDto? Border { get; set; }
            public int ZIndex { get; set; } = 1000;
            public double NaturalW { get; set; } = 1920;
            public double NaturalH { get; set; } = 1080;
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        public void SpawnFreeformCell(ImageItem item, FreeformCellDto? restoreDto = null)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;

            // If already open in Freeform, bring its card to front
            var existing = _freeformCards.FirstOrDefault(c => string.Equals(c.Item.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                BringFreeformCardToFront(existing);
                return;
            }

            int sourceSlot = restoreDto?.SourceSlot ?? Slot(item);

            // Hide the photo in the underlying grid cell
            if (!_hiddenItems.Contains(item))
            {
                ToggleHideImage(item);
            }

            // Determine natural image dimensions for zero-letterbox aspect ratio
            double naturalW = 1920;
            double naturalH = 1080;

            if (ImageCache.TryGet(item.Path, out var cachedEntry) && cachedEntry != null && cachedEntry.NativeWidth > 0 && cachedEntry.NativeHeight > 0)
            {
                naturalW = cachedEntry.NativeWidth;
                naturalH = cachedEntry.NativeHeight;
            }
            else if (item.Thumbnail is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
            {
                naturalW = bs.PixelWidth;
                naturalH = bs.PixelHeight;
            }

            double aspect = naturalW > 0 && naturalH > 0 ? (naturalW / naturalH) : (16.0 / 9.0);

            double maxBoundW = Math.Max(320, RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1280);
            double maxBoundH = Math.Max(240, RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720);

            double w, h, x, y;

            if (restoreDto != null && restoreDto.Width > 0 && restoreDto.Height > 0)
            {
                w = restoreDto.Width;
                h = restoreDto.Height;
                x = restoreDto.X;
                y = restoreDto.Y;
            }
            else
            {
                // Size precisely to the photo's natural aspect ratio with ZERO black bars / letterboxing
                double idealW = Math.Min(maxBoundW * 0.60, 880);
                double idealH = idealW / aspect;

                if (idealH > maxBoundH * 0.65)
                {
                    idealH = maxBoundH * 0.65;
                    idealW = idealH * aspect;
                }

                w = Math.Max(260, idealW);
                h = Math.Max(160, idealH);

                if (sourceSlot >= 0 && sourceSlot < _cellRoots.Length && _cellRoots[sourceSlot] is FrameworkElement cellRoot && cellRoot.ActualWidth > 50)
                {
                    try
                    {
                        var cellBounds = cellRoot.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, cellRoot.ActualWidth, cellRoot.ActualHeight));
                        x = cellBounds.X + (cellBounds.Width - w) / 2.0;
                        y = cellBounds.Y + (cellBounds.Height - h) / 2.0;
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
            }

            w = Math.Clamp(w, 200, maxBoundW);
            h = Math.Clamp(h, 140, maxBoundH);
            x = Math.Clamp(x, 0, Math.Max(0, maxBoundW - w));
            y = Math.Clamp(y, 0, Math.Max(0, maxBoundH - h));

            int zIndex = restoreDto?.ZIndex ?? (++_maxFreeformZIndex);
            if (zIndex > _maxFreeformZIndex) _maxFreeformZIndex = zIndex;

            var borderSettings = GetBorderForItem(item).Clone();

            // 1. Outer Floating Card Container (Clean Fluent rounded card with shadow)
            var cardContainer = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromArgb(255, 8, 8, 8)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Translation = new Vector3(0, 0, 256 + ((zIndex - 1000) * 4))
            };
            try { cardContainer.Shadow = new ThemeShadow(); } catch { }

            Canvas.SetLeft(cardContainer, x);
            Canvas.SetTop(cardContainer, y);
            Canvas.SetZIndex(cardContainer, zIndex);

            // Container layout grid (holds photo edge-to-edge + floating acrylic overlays)
            var contentGrid = new Grid();

            // 2. Full Edge-to-Edge Photo (NO solid header bar! Fills entire card boundary)
            var transform = new CompositeTransform();
            var freeformImg = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform
            };

            void LoadPhotoSource()
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.UriSource = new Uri(item.Path, UriKind.Absolute);
                    bmp.ImageOpened += (_, _) =>
                    {
                        if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                        {
                            naturalW = bmp.PixelWidth;
                            naturalH = bmp.PixelHeight;
                        }
                    };
                    freeformImg.Source = bmp;
                }
                catch { }
            }
            LoadPhotoSource();

            // Image clipping to match card rounded corners
            var clipRect = new RectangleGeometry();
            void UpdateCardClip()
            {
                double cw = cardContainer.Width > 0 ? cardContainer.Width : w;
                double ch = cardContainer.Height > 0 ? cardContainer.Height : h;
                clipRect.Rect = new Rect(0, 0, cw, ch);
            }
            UpdateCardClip();
            contentGrid.Clip = clipRect;
            contentGrid.Children.Add(freeformImg);

            // Restore zoom / pan
            if (restoreDto?.Zoom.HasValue == true && restoreDto.Zoom.Value > 0)
            {
                transform.ScaleX = restoreDto.Zoom.Value;
                transform.ScaleY = restoreDto.Zoom.Value;
            }
            if (restoreDto?.PanX.HasValue == true) transform.TranslateX = restoreDto.PanX.Value;
            if (restoreDto?.PanY.HasValue == true) transform.TranslateY = restoreDto.PanY.Value;

            // ---------------------------------------------------------------------------------
            // 3. FLOATING TOP-LEFT INFO CARD (Exact Detach Mode Pill)
            // ---------------------------------------------------------------------------------
            var topLeftOverlay = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 14, 0, 0),
                Opacity = 1.0,
                IsHitTestVisible = false
            };

            var topLeftBorder = new Border
            {
                Background = new AcrylicBrush
                {
                    TintColor = Color.FromArgb(255, 20, 20, 20),
                    TintOpacity = 0.82,
                    FallbackColor = Color.FromArgb(240, 24, 24, 24)
                },
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                Translation = new Vector3(0, 0, 24)
            };
            try { topLeftBorder.Shadow = new ThemeShadow(); } catch { }

            var infoStack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };

            var titleText = new TextBlock
            {
                Text = item.Name ?? "Photo",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Foreground = _menuWhitePrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 350,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            infoStack.Children.Add(titleText);

            string secondaryString = item.GetHudDisplayString(naturalW, naturalH);
            if (string.IsNullOrWhiteSpace(secondaryString))
            {
                secondaryString = $"{(int)naturalW} \u00D7 {(int)naturalH} \u2022 {item.DateText}";
            }
            var secondaryInfoText = new TextBlock
            {
                Text = secondaryString,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = _menuWhiteSecondary,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            infoStack.Children.Add(secondaryInfoText);

            var zoomRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var zoomInfoText = new TextBlock
            {
                Text = $"Zoom: {(int)Math.Round(transform.ScaleX * 100)}%",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = _menuWhiteSecondary
            };
            zoomRow.Children.Add(zoomInfoText);
            zoomRow.Children.Add(new TextBlock
            {
                Text = "\u2022",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = _menuWhiteSecondary,
                Opacity = 0.5
            });
            zoomRow.Children.Add(new TextBlock
            {
                Text = "HF",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = _menuWhitePrimary,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });
            infoStack.Children.Add(zoomRow);

            topLeftBorder.Child = infoStack;
            topLeftOverlay.Children.Add(topLeftBorder);
            Canvas.SetZIndex(topLeftOverlay, 60);
            contentGrid.Children.Add(topLeftOverlay);

            // ---------------------------------------------------------------------------------
            // 4. FLOATING TOP-RIGHT CAPTION CONTROLS (Exact Detach Mode Pill: Min/Dock, Max, Close)
            // ---------------------------------------------------------------------------------
            var topRightControls = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                Opacity = 1.0
            };

            var topRightBorder = new Border
            {
                Background = new AcrylicBrush
                {
                    TintColor = Color.FromArgb(255, 20, 20, 20),
                    TintOpacity = 0.82,
                    FallbackColor = Color.FromArgb(240, 24, 24, 24)
                },
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                Translation = new Vector3(0, 0, 24)
            };
            try { topRightBorder.Shadow = new ThemeShadow(); } catch { }

            var captionStack = new StackPanel { Orientation = Orientation.Horizontal };

            var card = new FreeformCard
            {
                Item = item,
                SourceSlot = sourceSlot,
                Container = cardContainer,
                ContentGrid = contentGrid,
                Photo = freeformImg,
                Transform = transform,
                TopLeftInfo = topLeftOverlay,
                TopRightControls = topRightControls,
                ZoomInfoText = zoomInfoText,
                SecondaryInfoText = secondaryInfoText,
                Border = borderSettings,
                ZIndex = zIndex,
                NaturalW = naturalW,
                NaturalH = naturalH,
                X = x,
                Y = y,
                Width = w,
                Height = h
            };

            // Caption Button: Minimize / Return / Dock to Grid
            var minBtn = CreateCaptionButton("\uE921", "Dock / Return to Grid (Esc)", () => DockFreeformCard(card));
            captionStack.Children.Add(minBtn);

            // Caption Button: Maximize / Solo
            var maxBtn = CreateCaptionButton("\uE922", "Solo / Maximize", () =>
            {
                int curSlot = Slot(item);
                if (curSlot >= 0) EnterSolo(curSlot);
            });
            captionStack.Children.Add(maxBtn);

            // Caption Button: Close
            var closeBtn = CreateCaptionButton("\uE8BB", "Close Freeform View", () => CloseFreeformCard(card));
            closeBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
            closeBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 153, 27, 16));
            captionStack.Children.Add(closeBtn);

            topRightBorder.Child = captionStack;
            topRightControls.Children.Add(topRightBorder);
            Canvas.SetZIndex(topRightControls, 60);
            contentGrid.Children.Add(topRightControls);

            // ---------------------------------------------------------------------------------
            // 5. FLOATING BOTTOM PLAYBAR HUD (Exact Detach Mode Bottom Capsule)
            // ---------------------------------------------------------------------------------
            var bottomHud = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 20),
                Opacity = 1.0
            };
            card.BottomHud = bottomHud;

            var bottomHudBorder = new Border
            {
                Background = new AcrylicBrush
                {
                    TintColor = Color.FromArgb(255, 20, 20, 20),
                    TintOpacity = 0.82,
                    FallbackColor = Color.FromArgb(240, 24, 24, 24)
                },
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(12, 5, 12, 5),
                Translation = new Vector3(0, 0, 32)
            };
            try { bottomHudBorder.Shadow = new ThemeShadow(); } catch { }

            var hudStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            // HUD Button 1: Reset Pan & Zoom
            var zoomBtn = CreateHudIconButton("\uE71E", "Reset Zoom (Middle-Click)", () =>
            {
                transform.ScaleX = 1.0;
                transform.ScaleY = 1.0;
                transform.TranslateX = 0;
                transform.TranslateY = 0;
                zoomInfoText.Text = "Zoom: 100%";
            });
            hudStack.Children.Add(zoomBtn);

            // HUD Button 2: Solo / Maximize
            var soloHudBtn = CreateHudIconButton("\uE768", "Solo / View Fullscreen", () =>
            {
                int curSlot = Slot(item);
                if (curSlot >= 0) EnterSolo(curSlot);
            });
            hudStack.Children.Add(soloHudBtn);

            // HUD Button 3: Return to Grid / Photos
            var albumBtn = CreateHudIconButton("\uE8B9", "Return / Dock to Grid Cell", () => DockFreeformCard(card));
            hudStack.Children.Add(albumBtn);

            // HUD Button 4: Show in Explorer
            var folderBtn = CreateHudIconButton("\uE838", "Show in File Explorer", () =>
            {
                if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                {
                    Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
                }
            });
            hudStack.Children.Add(folderBtn);

            // HUD Button 5: Size Window to Image (Aspect ratio match)
            var sizeToImageBtn = CreateHudIconButton("\uE7C4", "Size to Image", () =>
            {
                double targetW = Math.Min(RootGrid.ActualWidth * 0.70, 960);
                double targetH = targetW / aspect;
                if (targetH > RootGrid.ActualHeight * 0.70)
                {
                    targetH = RootGrid.ActualHeight * 0.70;
                    targetW = targetH * aspect;
                }
                cardContainer.Width = targetW;
                cardContainer.Height = targetH;
                card.Width = targetW;
                card.Height = targetH;
                UpdateCardClip();
            });
            hudStack.Children.Add(sizeToImageBtn);

            bottomHudBorder.Child = hudStack;
            bottomHud.Children.Add(bottomHudBorder);
            Canvas.SetZIndex(bottomHud, 60);
            contentGrid.Children.Add(bottomHud);

            // ---------------------------------------------------------------------------------
            // 6. AUTO-HIDING HUD & OVERLAYS (Fades out when pointer idle or moving)
            // ---------------------------------------------------------------------------------
            var hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            card.HudTimer = hudTimer;

            void SetOverlaysVisible(bool visible)
            {
                double targetOpacity = visible ? 1.0 : 0.0;
                topLeftOverlay.Opacity = targetOpacity;
                topRightControls.Opacity = targetOpacity;
                bottomHud.Opacity = targetOpacity;
                topRightControls.IsHitTestVisible = visible;
                bottomHud.IsHitTestVisible = visible;
            }

            void ShowOverlays()
            {
                SetOverlaysVisible(true);
                hudTimer.Stop();
                hudTimer.Start();
            }

            hudTimer.Tick += (_, _) =>
            {
                hudTimer.Stop();
                SetOverlaysVisible(false);
            };

            // ---------------------------------------------------------------------------------
            // 7. DRAG-TO-MOVE (Edge-to-Edge: Click and drag anywhere on photo to move across app)
            // ---------------------------------------------------------------------------------
            bool isMoving = false;
            Point dragStartInRoot = default;
            double startLeft = x;
            double startTop = y;
            uint movePointerId = 0;

            contentGrid.PointerPressed += (s, e) =>
            {
                BringFreeformCardToFront(card);
                ShowOverlays();

                var prop = e.GetCurrentPoint(contentGrid).Properties;
                if (!prop.IsLeftButtonPressed) return;

                // Check for Alt modifier: Alt+Left drag pans photo inside the card
                bool isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (isAlt) return;

                isMoving = true;
                movePointerId = e.Pointer.PointerId;
                dragStartInRoot = e.GetCurrentPoint(RootGrid).Position;
                startLeft = Canvas.GetLeft(cardContainer);
                if (double.IsNaN(startLeft)) startLeft = card.X;
                startTop = Canvas.GetTop(cardContainer);
                if (double.IsNaN(startTop)) startTop = card.Y;

                contentGrid.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            contentGrid.PointerMoved += (s, e) =>
            {
                ShowOverlays();

                if (isMoving && e.Pointer.PointerId == movePointerId)
                {
                    Point cur = e.GetCurrentPoint(RootGrid).Position;
                    double dx = cur.X - dragStartInRoot.X;
                    double dy = cur.Y - dragStartInRoot.Y;

                    double boundW = Math.Max(200, RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1280);
                    double boundH = Math.Max(150, RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 720);

                    double cardW = cardContainer.ActualWidth > 0 ? cardContainer.ActualWidth : card.Width;
                    double cardH = cardContainer.ActualHeight > 0 ? cardContainer.ActualHeight : card.Height;

                    // Clamped within app window boundaries
                    double newX = Math.Clamp(startLeft + dx, 0, Math.Max(0, boundW - cardW));
                    double newY = Math.Clamp(startTop + dy, 0, Math.Max(0, boundH - cardH));

                    Canvas.SetLeft(cardContainer, newX);
                    Canvas.SetTop(cardContainer, newY);
                    card.X = newX;
                    card.Y = newY;
                    e.Handled = true;
                }
            };

            contentGrid.PointerReleased += (s, e) =>
            {
                if (isMoving && e.Pointer.PointerId == movePointerId)
                {
                    isMoving = false;
                    try { contentGrid.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            contentGrid.PointerCanceled += (s, e) =>
            {
                if (isMoving && e.Pointer.PointerId == movePointerId)
                {
                    isMoving = false;
                    try { contentGrid.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            // ---------------------------------------------------------------------------------
            // 8. PHOTO PAN & ZOOM (Wheel zoom, Middle-click reset/drag, Alt+Left drag, Double-tap)
            // ---------------------------------------------------------------------------------
            bool isAltPanning = false;
            Point panStart = default;
            uint panPointerId = 0;

            contentGrid.PointerWheelChanged += (_, e) =>
            {
                ShowOverlays();
                int delta = e.GetCurrentPoint(contentGrid).Properties.MouseWheelDelta;
                double factor = delta > 0 ? 1.15 : 0.87;
                double newScale = Math.Clamp(transform.ScaleX * factor, 0.4, 12.0);
                transform.ScaleX = newScale;
                transform.ScaleY = newScale;
                zoomInfoText.Text = $"Zoom: {(int)Math.Round(newScale * 100)}%";
                e.Handled = true;
            };

            contentGrid.PointerPressed += (s, e) =>
            {
                var prop = e.GetCurrentPoint(contentGrid).Properties;
                if (prop.IsMiddleButtonPressed)
                {
                    // Middle-click reset
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                    zoomInfoText.Text = "Zoom: 100%";
                    e.Handled = true;
                    return;
                }

                bool isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (isAlt && prop.IsLeftButtonPressed)
                {
                    isAltPanning = true;
                    panPointerId = e.Pointer.PointerId;
                    panStart = e.GetCurrentPoint(contentGrid).Position;
                    contentGrid.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            };

            contentGrid.PointerMoved += (s, e) =>
            {
                if (isAltPanning && e.Pointer.PointerId == panPointerId)
                {
                    Point cur = e.GetCurrentPoint(contentGrid).Position;
                    transform.TranslateX += cur.X - panStart.X;
                    transform.TranslateY += cur.Y - panStart.Y;
                    panStart = cur;
                    e.Handled = true;
                }
            };

            contentGrid.PointerReleased += (s, e) =>
            {
                if (isAltPanning && e.Pointer.PointerId == panPointerId)
                {
                    isAltPanning = false;
                    try { contentGrid.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            contentGrid.DoubleTapped += (_, e) =>
            {
                ShowOverlays();
                if (Math.Abs(transform.ScaleX - 1.0) > 0.05 || Math.Abs(transform.TranslateX) > 1 || Math.Abs(transform.TranslateY) > 1)
                {
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                    transform.TranslateX = 0;
                    transform.TranslateY = 0;
                    zoomInfoText.Text = "Zoom: 100%";
                }
                else
                {
                    transform.ScaleX = 2.0;
                    transform.ScaleY = 2.0;
                    zoomInfoText.Text = "Zoom: 200%";
                }
                e.Handled = true;
            };

            // ---------------------------------------------------------------------------------
            // 9. RESIZE DRAG (Bottom-Right Grip)
            // ---------------------------------------------------------------------------------
            var resizeGrip = new Border
            {
                Width = 24,
                Height = 24,
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
                    Margin = new Thickness(0, 0, 4, 4)
                }
            };

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
                    double newH = Math.Clamp(startH + dy, 140, maxAvailH);

                    cardContainer.Width = newW;
                    cardContainer.Height = newH;
                    card.Width = newW;
                    card.Height = newH;
                    UpdateCardClip();
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

            Canvas.SetZIndex(resizeGrip, 70);
            contentGrid.Children.Add(resizeGrip);

            // ---------------------------------------------------------------------------------
            // 10. RIGHT-CLICK CONTEXT MENU
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

            cardContainer.Child = contentGrid;
            FreeformCanvas.Children.Add(cardContainer);
            _freeformCards.Add(card);

            ShowOverlays();
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
            card.HudTimer?.Stop();
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
            card.HudTimer?.Stop();
            FreeformCanvas.Children.Remove(card.Container);
            _freeformCards.Remove(card);
        }

        public void ClearAllFreeformCards()
        {
            foreach (var card in _freeformCards)
            {
                card.HudTimer?.Stop();
            }
            FreeformCanvas.Children.Clear();
            _freeformCards.Clear();
            _maxFreeformZIndex = 1000;
        }

        private Button CreateCaptionButton(string glyph, string tip, Action onClick)
        {
            var btn = new Button
            {
                Width = 46,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255))
                }
            };
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255));
            btn.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            btn.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, _) => onClick();
            return btn;
        }

        private Button CreateHudIconButton(string glyph, string tip, Action onClick)
        {
            var btn = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 13,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255))
                }
            };
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, _) => onClick();
            return btn;
        }
    }
}

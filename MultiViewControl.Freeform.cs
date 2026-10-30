using Microsoft.UI.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
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
        private int _maxFreeformZIndex = 5000;

        public IReadOnlyList<FreeformCard> ActiveFreeformCards => _freeformCards;

        public sealed class FreeformCard
        {
            public ImageItem Item { get; set; } = null!;
            public int SourceSlot { get; set; }
            public FrameworkElement Container { get; set; } = null!;
            public CellBorderDto? Border { get; set; }
            public int ZIndex { get; set; } = 5000;
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        public void SpawnFreeformCell(ImageItem item, FreeformCellDto? restoreDto = null)
        {
            if (item == null || string.IsNullOrEmpty(item.Path)) return;

            int sourceSlot = restoreDto?.SourceSlot ?? Slot(item);

            // Hide the photo in the underlying grid cell (exact same hide mechanism)
            if (sourceSlot >= 0 && sourceSlot < _items.Count && !_hiddenItems.Contains(_items[sourceSlot]))
            {
                ToggleHideImage(_items[sourceSlot]);
            }

            // Create a new ImageItem for this Freeform Cell so it becomes Position N+1 (e.g. Position 5)
            var freeformItem = new ImageItem
            {
                Path = item.Path,
                Name = item.Name ?? Path.GetFileName(item.Path),
                DateModified = item.DateModified,
                SizeString = item.SizeString
            };

            // Calculate geometry (matching natural photo aspect ratio for zero letterboxing)
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

            w = Math.Clamp(w, 100, maxBoundW);
            h = Math.Clamp(h, 80, maxBoundH);
            x = Math.Clamp(x, 0, Math.Max(0, maxBoundW - w));
            y = Math.Clamp(y, 0, Math.Max(0, maxBoundH - h));

            int zIndex = restoreDto?.ZIndex ?? (++_maxFreeformZIndex);
            if (zIndex > _maxFreeformZIndex) _maxFreeformZIndex = zIndex;

            // Add freeformItem to _items (so if there were 4 images, total cells increases to 5!)
            _items.Add(freeformItem);
            int newSlot = _items.Count - 1;

            // Create the cell using the unified CreateCell architecture
            var freeformCell = CreateCell(freeformItem, newSlot, isFreeform: true);
            freeformCell.Width = w;
            freeformCell.Height = h;

            Canvas.SetLeft(freeformCell, x);
            Canvas.SetTop(freeformCell, y);
            Canvas.SetZIndex(freeformCell, zIndex);
            freeformCell.Translation = new Vector3(0, 0, 256 + ((zIndex - 5000) * 8));

            // Restore zoom / pan if specified
            if (freeformCell.Tag is CellParts parts)
            {
                if (restoreDto?.Zoom.HasValue == true && restoreDto.Zoom.Value > 0)
                {
                    parts.Transform.ScaleX = restoreDto.Zoom.Value;
                    parts.Transform.ScaleY = restoreDto.Zoom.Value;
                }
                if (restoreDto?.PanX.HasValue == true) parts.Transform.TranslateX = restoreDto.PanX.Value;
                if (restoreDto?.PanY.HasValue == true) parts.Transform.TranslateY = restoreDto.PanY.Value;
            }

            var borderSettings = (restoreDto?.Border ?? GetBorderForItem(item)).Clone();
            _itemBorders[freeformItem.Path] = borderSettings;
            if (CardBorder(freeformCell) is Border cellBorder)
            {
                ApplyBorderToCellBorder(cellBorder, cellBorder.Child as Grid, freeformItem);
            }

            var card = new FreeformCard
            {
                Item = freeformItem,
                SourceSlot = sourceSlot,
                Container = freeformCell,
                Border = borderSettings,
                ZIndex = zIndex,
                X = x,
                Y = y,
                Width = w,
                Height = h
            };

            _freeformCards.Add(card);
            FreeformCanvas.Children.Add(freeformCell);

            EnsureCellRootsCapacity(_items.Count);
            _cellRoots[newSlot] = freeformCell;

            ShowHudAndOverlays();
        }

        public void BringFreeformCardToFront(FreeformCard card)
        {
            if (card?.Container == null) return;
            card.ZIndex = ++_maxFreeformZIndex;
            Canvas.SetZIndex(card.Container, card.ZIndex);
            card.Container.Translation = new Vector3(0, 0, 256 + ((_maxFreeformZIndex - 5000) * 8));
        }

        public void DockFreeformCard(FreeformCard card)
        {
            if (card == null) return;
            FreeformCanvas.Children.Remove(card.Container);
            _freeformCards.Remove(card);

            // Unhide the source grid cell if it was hidden
            if (card.SourceSlot >= 0 && card.SourceSlot < _items.Count && _hiddenItems.Contains(_items[card.SourceSlot]))
            {
                ToggleHideImage(_items[card.SourceSlot]);
            }

            int index = _items.IndexOf(card.Item);
            if (index >= 0)
            {
                _hiddenItems.Remove(card.Item);
                ForgetCell(card.Container);
                _items.RemoveAt(index);
                var roots = new List<FrameworkElement?>(_cellRoots);
                if (index < roots.Count) roots.RemoveAt(index);
                _cellRoots = roots.ToArray();
            }

            ShowHudAndOverlays();
        }

        public void CloseFreeformCard(FreeformCard card)
        {
            if (card == null) return;
            FreeformCanvas.Children.Remove(card.Container);
            _freeformCards.Remove(card);

            int index = _items.IndexOf(card.Item);
            if (index >= 0)
            {
                _hiddenItems.Remove(card.Item);
                ForgetCell(card.Container);
                _items.RemoveAt(index);
                var roots = new List<FrameworkElement?>(_cellRoots);
                if (index < roots.Count) roots.RemoveAt(index);
                _cellRoots = roots.ToArray();
            }

            ShowHudAndOverlays();
        }

        public void ClearAllFreeformCards()
        {
            foreach (var card in _freeformCards.ToList())
            {
                CloseFreeformCard(card);
            }
            FreeformCanvas.Children.Clear();
            _freeformCards.Clear();
            _maxFreeformZIndex = 5000;
        }
        private enum WindowResizeDirection
        {
            Left,
            Right,
            Top,
            Bottom,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private void AttachWindowResizeHandles(Grid slotGrid, Action updateLayout)
        {
            var dirs = new[]
            {
                WindowResizeDirection.Top,
                WindowResizeDirection.Bottom,
                WindowResizeDirection.Left,
                WindowResizeDirection.Right,
                WindowResizeDirection.TopLeft,
                WindowResizeDirection.TopRight,
                WindowResizeDirection.BottomLeft,
                WindowResizeDirection.BottomRight
            };

            foreach (var dir in dirs)
            {
                var handle = new FreeformResizeHandle
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
                    IsHitTestVisible = true
                };
                Canvas.SetZIndex(handle, 200);

                InputSystemCursorShape cursorShape = dir switch
                {
                    WindowResizeDirection.Top or WindowResizeDirection.Bottom => InputSystemCursorShape.SizeNorthSouth,
                    WindowResizeDirection.Left or WindowResizeDirection.Right => InputSystemCursorShape.SizeWestEast,
                    WindowResizeDirection.TopLeft or WindowResizeDirection.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
                    WindowResizeDirection.TopRight or WindowResizeDirection.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
                    _ => InputSystemCursorShape.Arrow
                };
                handle.SetCursor(cursorShape);

                switch (dir)
                {
                    case WindowResizeDirection.Top:
                        handle.Height = 8;
                        handle.VerticalAlignment = VerticalAlignment.Top;
                        handle.HorizontalAlignment = HorizontalAlignment.Stretch;
                        handle.Margin = new Thickness(14, 0, 14, 0);
                        break;
                    case WindowResizeDirection.Bottom:
                        handle.Height = 8;
                        handle.VerticalAlignment = VerticalAlignment.Bottom;
                        handle.HorizontalAlignment = HorizontalAlignment.Stretch;
                        handle.Margin = new Thickness(14, 0, 14, 0);
                        break;
                    case WindowResizeDirection.Left:
                        handle.Width = 8;
                        handle.HorizontalAlignment = HorizontalAlignment.Left;
                        handle.VerticalAlignment = VerticalAlignment.Stretch;
                        handle.Margin = new Thickness(0, 14, 0, 14);
                        break;
                    case WindowResizeDirection.Right:
                        handle.Width = 8;
                        handle.HorizontalAlignment = HorizontalAlignment.Right;
                        handle.VerticalAlignment = VerticalAlignment.Stretch;
                        handle.Margin = new Thickness(0, 14, 0, 14);
                        break;
                    case WindowResizeDirection.TopLeft:
                        handle.Width = 14; handle.Height = 14;
                        handle.HorizontalAlignment = HorizontalAlignment.Left;
                        handle.VerticalAlignment = VerticalAlignment.Top;
                        break;
                    case WindowResizeDirection.TopRight:
                        handle.Width = 14; handle.Height = 14;
                        handle.HorizontalAlignment = HorizontalAlignment.Right;
                        handle.VerticalAlignment = VerticalAlignment.Top;
                        break;
                    case WindowResizeDirection.BottomLeft:
                        handle.Width = 14; handle.Height = 14;
                        handle.HorizontalAlignment = HorizontalAlignment.Left;
                        handle.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                    case WindowResizeDirection.BottomRight:
                        handle.Width = 14; handle.Height = 14;
                        handle.HorizontalAlignment = HorizontalAlignment.Right;
                        handle.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                }

                bool isResizing = false;
                Point startPoint = default;
                double initX = 0, initY = 0, initW = 0, initH = 0;
                uint resizePointerId = 0;

                handle.PointerPressed += (s, e) =>
                {
                    var prop = e.GetCurrentPoint(handle).Properties;
                    if (prop.IsLeftButtonPressed)
                    {
                        var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Container, slotGrid));
                        if (card != null) BringFreeformCardToFront(card);

                        isResizing = true;
                        resizePointerId = e.Pointer.PointerId;
                        startPoint = e.GetCurrentPoint(RootGrid).Position;

                        initX = Canvas.GetLeft(slotGrid);
                        if (double.IsNaN(initX)) initX = 0;
                        initY = Canvas.GetTop(slotGrid);
                        if (double.IsNaN(initY)) initY = 0;

                        initW = slotGrid.Width > 0 ? slotGrid.Width : (slotGrid.ActualWidth > 0 ? slotGrid.ActualWidth : 300);
                        initH = slotGrid.Height > 0 ? slotGrid.Height : (slotGrid.ActualHeight > 0 ? slotGrid.ActualHeight : 200);

                        handle.CapturePointer(e.Pointer);
                        e.Handled = true;
                    }
                };

                handle.PointerMoved += (s, e) =>
                {
                    if (isResizing && e.Pointer.PointerId == resizePointerId)
                    {
                        Point cur = e.GetCurrentPoint(RootGrid).Position;
                        double dx = cur.X - startPoint.X;
                        double dy = cur.Y - startPoint.Y;

                        double boundW = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1920;
                        double boundH = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 1080;
                        const double minW = 120;
                        const double minH = 90;

                        double curX = initX;
                        double curY = initY;
                        double curW = initW;
                        double curH = initH;

                        if (dir is WindowResizeDirection.Right or WindowResizeDirection.TopRight or WindowResizeDirection.BottomRight)
                        {
                            curW = Math.Clamp(initW + dx, minW, Math.Max(minW, boundW - initX));
                        }
                        else if (dir is WindowResizeDirection.Left or WindowResizeDirection.TopLeft or WindowResizeDirection.BottomLeft)
                        {
                            double proposedW = initW - dx;
                            if (proposedW < minW) proposedW = minW;
                            if (initX + (initW - proposedW) < 0) proposedW = initX + initW;
                            curW = proposedW;
                            curX = initX + (initW - curW);
                        }

                        if (dir is WindowResizeDirection.Bottom or WindowResizeDirection.BottomLeft or WindowResizeDirection.BottomRight)
                        {
                            curH = Math.Clamp(initH + dy, minH, Math.Max(minH, boundH - initY));
                        }
                        else if (dir is WindowResizeDirection.Top or WindowResizeDirection.TopLeft or WindowResizeDirection.TopRight)
                        {
                            double proposedH = initH - dy;
                            if (proposedH < minH) proposedH = minH;
                            if (initY + (initH - proposedH) < 0) proposedH = initY + initH;
                            curH = proposedH;
                            curY = initY + (initH - curH);
                        }

                        slotGrid.Width = curW;
                        slotGrid.Height = curH;
                        Canvas.SetLeft(slotGrid, curX);
                        Canvas.SetTop(slotGrid, curY);

                        var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Container, slotGrid));
                        if (card != null)
                        {
                            card.X = curX;
                            card.Y = curY;
                            card.Width = curW;
                            card.Height = curH;
                        }

                        updateLayout();
                        e.Handled = true;
                    }
                };

                handle.PointerReleased += (s, e) =>
                {
                    if (isResizing && e.Pointer.PointerId == resizePointerId)
                    {
                        isResizing = false;
                        try { handle.ReleasePointerCapture(e.Pointer); } catch { }
                        e.Handled = true;
                    }
                };

                handle.PointerCanceled += (s, e) =>
                {
                    if (isResizing && e.Pointer.PointerId == resizePointerId)
                    {
                        isResizing = false;
                        try { handle.ReleasePointerCapture(e.Pointer); } catch { }
                    }
                };

                handle.PointerCaptureLost += (s, e) =>
                {
                    isResizing = false;
                };

                slotGrid.Children.Add(handle);
            }
        }
        public sealed class FreeformResizeHandle : Grid
        {
            public void SetCursor(InputSystemCursorShape shape)
            {
                try { ProtectedCursor = InputSystemCursor.Create(shape); } catch { }
            }
        }

        public sealed class FreeformCellGrid : Grid
        {
            public void SetCursor(InputSystemCursorShape shape)
            {
                try { ProtectedCursor = InputSystemCursor.Create(shape); } catch { }
            }
        }
    }
}

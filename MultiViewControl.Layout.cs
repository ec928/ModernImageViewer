using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        // Photos stay in these borders across a segment rebuild so a drag does not decode them again.
        private FrameworkElement?[] _cellRoots = Array.Empty<FrameworkElement?>();
        public int GridCellCount => Math.Max(0, _items.Count - _freeformCards.Count);

        public void EnsureCellRootsCapacity(int minCapacity)
        {
            if (_cellRoots.Length < minCapacity)
            {
                Array.Resize(ref _cellRoots, minCapacity);
            }
        }
        private readonly Dictionary<string, MosaicHost> _mosaicHosts = new();
        private MosaicLayout? _mosaic;
        private bool _mosaicCustom;
        private bool _keepMosaic;
        // Keep the photo cells but build a new layout, for a change in the number of photos.
        private bool _relayoutKeepingCells;
        private SegmentGesture? _segmentGesture;
        private bool _applyingSegmentUnlock;
        private bool _layoutFromTemplate;

        private void ResetMosaicFields()
        {
            EndSegmentGesture();
            _cellRoots = Array.Empty<FrameworkElement?>();
            _mosaicHosts.Clear();
            _mosaic = null;
            _mosaicCustom = false;
            _keepMosaic = false;
            _layoutFromTemplate = false;
            _framedSlots = 0;
        }

        private void BuildLayout()
        {
            if (!_applyingSegmentUnlock)
                EndSegmentGesture();

            bool keepCells = !_layoutFromTemplate && ((_keepMosaic && CellRootsReady())
                || (_relayoutKeepingCells && _cellRoots.Length >= VisibleCount(GridCellCount)));
            if (keepCells)
            {
                int gridCount = GridCellCount;
                for (int i = 0; i < gridCount && i < _cellRoots.Length; i++)
                {
                    var cell = _cellRoots[i];
                    if (cell?.Parent is Panel parent)
                        parent.Children.Remove(cell);
                }
            }

            CellsGrid.Children.Clear();
            CellsGrid.RowDefinitions.Clear();
            CellsGrid.ColumnDefinitions.Clear();
            _mosaicHosts.Clear();

            if (!keepCells)
            {
                _cellTransforms.Clear();
                _layoutUpdaters.Clear();
                _cellTopControls.Clear();
                _cellRoots = Array.Empty<FrameworkElement?>();
                if (!_layoutFromTemplate)
                {
                    _mosaic = null;
                    _mosaicCustom = false;
                }
            }
            else if (_relayoutKeepingCells)
            {
                _mosaic = null;
                _mosaicCustom = false;
            }

            NormalizeHero();
            int stored = GridCellCount;
            if (stored == 0)
            {
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var emptyPanel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 12
                };
                emptyPanel.Children.Add(new FontIcon
                {
                    Glyph = "",
                    FontSize = 40,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = "No photos in layout",
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                });
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = "Open a saved layout (.mivlayout) or drag photos here",
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
                var openBtn = new Button
                {
                    Content = "Open Layout…",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                openBtn.Click += async (_, _) => await OpenLayoutFileAsync();
                emptyPanel.Children.Add(openBtn);
                CellsGrid.Children.Add(emptyPanel);

                RelinkFreeformCells();
                RefreshLinesChrome(0);
                RefreshHeroChrome(0);
                return;
            }

            UpdateFillFitButton();
            int count = VisibleCount(stored);

            if (count == 1)
            {
                CellsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (!keepCells)
                    _cellRoots = new FrameworkElement?[Math.Max(1, _items.Count)];
                PlaceCell(CellsGrid, 0, 0, 0);
            }
            else
            {
                int visible = VisibleCount(count);
                if (!keepCells)
                    _cellRoots = new FrameworkElement?[Math.Max(visible, _items.Count)];

                var mosaic = _mosaic;
                if (mosaic == null)
                {
                    if (HeroUsesLines)
                    {
                        mosaic = MosaicLayout.CreateHeroLines(count, _heroKind, _heroPosition, CurrentLineCounts(), !_flipped);
                    }
                    else if (_heroActive)
                    {
                        mosaic = MosaicLayout.CreateRectangleHero(count, _heroPosition);
                        if (_flipped) mosaic = MosaicLayout.Transpose(mosaic);
                    }
                    else
                    {
                        mosaic = MosaicLayout.FromCounts(CurrentLineCounts(), !_flipped);
                    }
                }
                _mosaic = mosaic;
                RenderSplit(mosaic, CellsGrid, Array.Empty<int>());
            }

            RelinkFreeformCells();
            RefreshLinesChrome(stored);
            RefreshHeroChrome(stored);
        }

        /// <summary>A rebuild can start the shared lists again. Freeform cells live outside the grid and carry on.</summary>
        private void RelinkFreeformCells()
        {
            EnsureCellRootsCapacity(_items.Count);
            for (int fc = 0; fc < _freeformCards.Count; fc++)
            {
                var card = _freeformCards[fc];
                int cardSlot = Slot(card.Item);
                if (cardSlot >= 0 && cardSlot < _cellRoots.Length)
                {
                    _cellRoots[cardSlot] = card.Container;
                }
                if (!FreeformCanvas.Children.Contains(card.Container))
                {
                    FreeformCanvas.Children.Add(card.Container);
                }
                if (card.Container?.Tag is CellParts cardParts)
                {
                    if (!_cellTransforms.Contains(cardParts.Transform))
                        _cellTransforms.Add(cardParts.Transform);
                    if (!_layoutUpdaters.Contains(cardParts.UpdateLayout))
                        _layoutUpdaters.Add(cardParts.UpdateLayout);
                    if (!_cellTopControls.Contains(cardParts.TopControls))
                        _cellTopControls.Add(cardParts.TopControls);
                }
            }
        }

        private int VisibleCount(int count)
        {
            if (count <= 0) return 0;
            int cap = MosaicLayout.ShownCount(count);
            if (_framedSlots > 0)
                return Math.Min(_framedSlots, cap);
            return cap;
        }

        private bool CellRootsReady()
        {
            int visible = VisibleCount(GridCellCount);
            if (visible <= 0 || _cellRoots.Length < visible) return false;
            for (int i = 0; i < visible; i++)
            {
                if (_cellRoots[i] == null) return false;
            }
            return true;
        }

        private void RenderSplit(MosaicLayout node, Grid host, int[] path)
        {
            if (node.IsLeaf) return;
            _mosaicHosts[PathKey(path)] = new MosaicHost { Grid = host, Path = path };

            if (node.Vertical)
            {
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < node.Children.Count; i++)
                    host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WeightAt(node, i), GridUnitType.Star) });
            }
            else
            {
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < node.Children.Count; i++)
                    host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WeightAt(node, i), GridUnitType.Star) });
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                int row = node.Vertical ? 0 : i;
                int col = node.Vertical ? i : 0;
                var child = node.Children[i];
                if (child.IsLeaf)
                {
                    PlaceCell(host, child.Leaf, row, col);
                }
                else
                {
                    var inner = new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    Grid.SetRow(inner, row);
                    Grid.SetColumn(inner, col);
                    host.Children.Add(inner);
                    RenderSplit(child, inner, ChildPath(path, i));
                }
            }

            for (int i = 0; i < node.Children.Count - 1; i++)
            {
                AddSplitter(host, node.Vertical, i, 0, 1, new SplitBinding
                {
                    Path = path,
                    Boundary = i,
                    VerticalCut = node.Vertical
                });
            }
        }

        private void PlaceCell(Grid host, int index, int row, int col)
        {
            FrameworkElement? existing = (uint)index < (uint)_cellRoots.Length ? _cellRoots[index] : null;
            if (existing != null)
            {
                if (existing.Parent is Panel parent)
                    parent.Children.Remove(existing);
                Grid.SetRow(existing, row);
                Grid.SetColumn(existing, col);
                Grid.SetRowSpan(existing, 1);
                Grid.SetColumnSpan(existing, 1);
                host.Children.Add(existing);
                if ((uint)index < (uint)_items.Count && existing.Tag is CellParts parts)
                {
                    parts.Bound.Item = _items[index];
                }
                return;
            }

            if ((uint)index >= (uint)_items.Count) return;
            AddCell(host, _items[index], row, col, 1, 1, index);
        }

        private void RefreshLinesChrome(int count)
        {
            int shown = VisibleCount(count);
            ShownBadge.Visibility = count > shown ? Visibility.Visible : Visibility.Collapsed;
            ShownText.Text = shown + " of " + count + " shown";
            bool show = shown > 1;
            if (!show)
            {
                CloseCountsCard();
                CountsSwitch.Visibility = Visibility.Collapsed;
                return;
            }

            int lines = EffectiveLines();
            CountsSwitch.Visibility = Visibility.Visible;
            RowsSideText.Text = _flipped ? "Rows" : lines + (lines == 1 ? " Row" : " Rows");
            ColumnsSideText.Text = _flipped ? lines + (lines == 1 ? " Column" : " Columns") : "Columns";
            ShowCountsSides();
            RefreshGridLayoutEnabled();
        }

        /// <summary>Lines need at least two grid cells that show an image. Hidden cells and freeform cells do not count.</summary>
        private bool CanEditGridLayout
        {
            get
            {
                int visible = VisibleCount(GridCellCount);
                int filled = 0;
                for (int i = 0; i < visible; i++)
                {
                    if (!ShowsNoImage(_items[i])) filled++;
                }
                return filled >= 2;
            }
        }

        private void RefreshGridLayoutEnabled()
        {
            bool enabled = CanEditGridLayout;
            RowsSide.IsEnabled = enabled;
            ColumnsSide.IsEnabled = enabled;
            if (!enabled) CloseCountsCard();
        }

        private static double WeightAt(MosaicLayout node, int index)
        {
            if ((uint)index >= (uint)node.Weights.Count) return 1;
            double weight = node.Weights[index];
            return weight > 0 && !double.IsNaN(weight) ? weight : 1;
        }

        private static int[] ChildPath(int[] path, int index)
        {
            var next = new int[path.Length + 1];
            Array.Copy(path, next, path.Length);
            next[path.Length] = index;
            return next;
        }

        private static string PathKey(IReadOnlyList<int> path)
        {
            return path.Count == 0 ? "" : string.Join(".", path);
        }

        private void AddSplitter(Grid target, bool verticalCut, int index, int crossStart, int crossSpan, SplitBinding? binding)
        {
            var splitter = new SplitterElement
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Boundary = binding?.Boundary ?? index,
                VerticalCut = verticalCut
            };
            ToolTipService.SetToolTip(splitter, "Drag to resize \u00B7 Double-click to equalize");
            Canvas.SetZIndex(splitter, 40);

            var line = new Rectangle
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 162, 237)),
                Opacity = 0.0
            };
            splitter.Line = line;

            if (verticalCut)
            {
                splitter.HorizontalAlignment = HorizontalAlignment.Right;
                splitter.VerticalAlignment = VerticalAlignment.Stretch;
                splitter.Width = 14;
                splitter.Margin = new Thickness(0, 0, -7, 0);
                Grid.SetColumn(splitter, index);
                Grid.SetRow(splitter, crossStart);
                if (crossSpan > 1) Grid.SetRowSpan(splitter, crossSpan);
                splitter.SetCursor(InputSystemCursorShape.SizeWestEast);
                line.Width = 2;
                line.HorizontalAlignment = HorizontalAlignment.Center;
                line.VerticalAlignment = VerticalAlignment.Stretch;
            }
            else
            {
                splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                splitter.VerticalAlignment = VerticalAlignment.Bottom;
                splitter.Height = 14;
                splitter.Margin = new Thickness(0, 0, 0, -7);
                Grid.SetRow(splitter, index);
                Grid.SetColumn(splitter, crossStart);
                if (crossSpan > 1) Grid.SetColumnSpan(splitter, crossSpan);
                splitter.SetCursor(InputSystemCursorShape.SizeNorthSouth);
                line.Height = 2;
                line.HorizontalAlignment = HorizontalAlignment.Stretch;
                line.VerticalAlignment = VerticalAlignment.Center;
            }
            splitter.Children.Add(line);

            bool isDragging = false;
            double startAlong = 0;
            double start0 = 0;
            double start1 = 0;
            DateTime lastClickTime = DateTime.MinValue;
            int pair = index;
            int next = index + 1;

            splitter.PointerEntered += (s, e) =>
            {
                if (_segmentGesture != null) return;
                ShowHudAndOverlays();
                UpdateSplitHover(line, e, splitter, binding, verticalCut);
                line.Opacity = 0.85;
            };

            splitter.PointerExited += (s, e) =>
            {
                if (isDragging || _segmentGesture != null) return;
                line.Opacity = 0.0;
                ShowSpan(line, verticalCut, 0, 0, 0);
            };

            splitter.PointerPressed += (s, e) =>
            {
                var ptr = e.GetCurrentPoint(splitter);
                if (!ptr.Properties.IsLeftButtonPressed) return;

                var now = DateTime.Now;
                if ((now - lastClickTime).TotalMilliseconds < 350)
                {
                    EqualizeBoundary(target, verticalCut, pair, next, binding);
                    line.Opacity = 0.0;
                    e.Handled = true;
                    return;
                }
                lastClickTime = now;

                if (TryBeginSegmentGesture(splitter, line, e, target, binding, verticalCut))
                    return;

                NormalizeTracks(target, verticalCut);
                start0 = TrackValue(target, verticalCut, pair);
                start1 = TrackValue(target, verticalCut, next);
                var origin = e.GetCurrentPoint(target).Position;
                startAlong = verticalCut ? origin.X : origin.Y;
                isDragging = true;
                _hudTimer.Stop();
                line.Opacity = 1.0;
                ShowSpan(line, verticalCut, 0, 0, 0);
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            splitter.PointerMoved += (s, e) =>
            {
                if (_segmentGesture != null) return;
                ShowHudAndOverlays();
                if (!isDragging)
                {
                    UpdateSplitHover(line, e, splitter, binding, verticalCut);
                    return;
                }

                var point = e.GetCurrentPoint(target).Position;
                double delta = (verticalCut ? point.X : point.Y) - startAlong;
                var (a, b) = ViewerMath.ResizePair(start0, start1, delta);
                SetTrack(target, verticalCut, pair, a);
                SetTrack(target, verticalCut, next, b);
                e.Handled = true;
            };

            void EndDrag(PointerRoutedEventArgs e)
            {
                if (!isDragging) return;
                isDragging = false;
                try { splitter.ReleasePointerCapture(e.Pointer); }
                catch { }
                line.Opacity = 0.0;
                ShowSpan(line, verticalCut, 0, 0, 0);
                if (binding != null && _mosaic != null)
                {
                    var node = MosaicMath.At(_mosaic, binding.Path);
                    if (node != null)
                        ReadVisual(target, node);
                }
                _hudTimer.Start();
                e.Handled = true;
            }

            splitter.PointerReleased += (s, e) => EndDrag(e);
            splitter.PointerCaptureLost += (s, e) => EndDrag(e);
            target.Children.Add(splitter);
        }

        private static void NormalizeTracks(Grid grid, bool verticalCut)
        {
            if (verticalCut)
            {
                foreach (var col in grid.ColumnDefinitions)
                {
                    double w = col.ActualWidth > 0 ? col.ActualWidth : 100.0;
                    col.Width = new GridLength(w, GridUnitType.Star);
                }
            }
            else
            {
                foreach (var row in grid.RowDefinitions)
                {
                    double h = row.ActualHeight > 0 ? row.ActualHeight : 100.0;
                    row.Height = new GridLength(h, GridUnitType.Star);
                }
            }
        }

        private static double TrackValue(Grid grid, bool verticalCut, int index)
        {
            if (verticalCut)
            {
                if ((uint)index >= (uint)grid.ColumnDefinitions.Count) return 1;
                return grid.ColumnDefinitions[index].Width.Value;
            }
            if ((uint)index >= (uint)grid.RowDefinitions.Count) return 1;
            return grid.RowDefinitions[index].Height.Value;
        }

        private static void SetTrack(Grid grid, bool verticalCut, int index, double value)
        {
            if (value <= 0 || double.IsNaN(value)) return;
            if (verticalCut)
            {
                if ((uint)index < (uint)grid.ColumnDefinitions.Count)
                    grid.ColumnDefinitions[index].Width = new GridLength(value, GridUnitType.Star);
            }
            else if ((uint)index < (uint)grid.RowDefinitions.Count)
            {
                grid.RowDefinitions[index].Height = new GridLength(value, GridUnitType.Star);
            }
        }

        private void EqualizeBoundary(Grid grid, bool verticalCut, int a, int b, SplitBinding? binding)
        {
            double sum = TrackValue(grid, verticalCut, a) + TrackValue(grid, verticalCut, b);
            double half = sum > 0 ? sum / 2.0 : 1.0;
            SetTrack(grid, verticalCut, a, half);
            SetTrack(grid, verticalCut, b, half);
            if (binding != null && _mosaic != null)
            {
                var node = MosaicMath.At(_mosaic, binding.Path);
                if (node != null)
                    ReadVisual(grid, node);
            }
        }

        private static void ReadVisual(Grid grid, MosaicLayout node)
        {
            int count = node.Vertical ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count;
            for (int i = 0; i < node.Weights.Count && i < count; i++)
            {
                double value = node.Vertical
                    ? grid.ColumnDefinitions[i].Width.Value
                    : grid.RowDefinitions[i].Height.Value;
                if (value > 0 && !double.IsNaN(value))
                    node.Weights[i] = value;
            }
        }

        private void PushWeights(int[] path)
        {
            if (_mosaic == null || !_mosaicHosts.TryGetValue(PathKey(path), out var host)) return;
            var node = MosaicMath.At(_mosaic, path);
            if (node == null || node.IsLeaf) return;
            for (int i = 0; i < node.Weights.Count; i++)
                SetTrack(host.Grid, node.Vertical, i, node.Weights[i]);
        }

        private void WriteAllGridWeights()
        {
            if (_mosaic == null) return;
            foreach (var host in _mosaicHosts.Values)
            {
                var node = MosaicMath.At(_mosaic, host.Path);
                if (node != null && !node.IsLeaf)
                    ReadVisual(host.Grid, node);
            }
        }

        private BoundaryHit ClassifySplit(SplitBinding binding, SplitterElement splitter, PointerRoutedEventArgs e)
        {
            double length = binding.VerticalCut ? splitter.ActualHeight : splitter.ActualWidth;
            var local = e.GetCurrentPoint(splitter).Position;
            double along = binding.VerticalCut ? local.Y : local.X;
            var node = _mosaic == null ? null : MosaicMath.At(_mosaic, binding.Path);
            if (node == null)
            {
                return new BoundaryHit { Whole = true, SegStart = 0, SegEnd = length };
            }
            return MosaicMath.ClassifyBoundary(node, binding.Boundary, length, along);
        }

        private void UpdateSplitHover(Rectangle line, PointerRoutedEventArgs e, SplitterElement splitter, SplitBinding? binding, bool verticalCut)
        {
            if (binding == null) return;
            var hit = ClassifySplit(binding, splitter, e);
            double length = verticalCut ? splitter.ActualHeight : splitter.ActualWidth;
            ShowSpan(line, verticalCut, hit.SegStart, hit.SegEnd, length);
        }

        private static void ShowSpan(Rectangle line, bool verticalCut, double start, double end, double length)
        {
            bool full = length <= 0 || (start <= 0.5 && end >= length - 0.5);
            double size = Math.Max(0, end - start);
            if (verticalCut)
            {
                if (full)
                {
                    line.VerticalAlignment = VerticalAlignment.Stretch;
                    line.Height = double.NaN;
                    line.Margin = new Thickness(0);
                }
                else
                {
                    line.VerticalAlignment = VerticalAlignment.Top;
                    line.Height = size;
                    line.Margin = new Thickness(0, Math.Max(0, start), 0, 0);
                }
            }
            else if (full)
            {
                line.HorizontalAlignment = HorizontalAlignment.Stretch;
                line.Width = double.NaN;
                line.Margin = new Thickness(0);
            }
            else
            {
                line.HorizontalAlignment = HorizontalAlignment.Left;
                line.Width = size;
                line.Margin = new Thickness(Math.Max(0, start), 0, 0, 0);
            }
        }

        private bool TryBeginSegmentGesture(SplitterElement splitter, Rectangle line, PointerRoutedEventArgs e, Grid host, SplitBinding? binding, bool verticalCut)
        {
            if (binding == null || _segmentGesture != null || _mosaic == null) return false;
            if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return false;

            WriteAllGridWeights();
            var hit = ClassifySplit(binding, splitter, e);
            if (!hit.NeedsFlip) return false;

            var node = MosaicMath.At(_mosaic, binding.Path);
            if (node == null) return false;
            double band = BandPixels(host, node, binding.Boundary);
            if (band <= 1) return false;
            if (!RootGrid.CapturePointer(e.Pointer)) return false;

            double length = verticalCut ? splitter.ActualHeight : splitter.ActualWidth;
            ShowSpan(line, verticalCut, hit.SegStart, hit.SegEnd, length);
            line.Opacity = 1;
            _hudTimer.Stop();

            var origin = e.GetCurrentPoint(CellsGrid).Position;
            _segmentGesture = new SegmentGesture
            {
                Pointer = e.Pointer,
                PointerId = e.Pointer.PointerId,
                VerticalCut = verticalCut,
                Path = binding.Path,
                Boundary = binding.Boundary,
                Lane = hit.Lane,
                NeedsFlip = true,
                BandPixels = band,
                StartAlong = verticalCut ? origin.X : origin.Y,
                Origin = _mosaic.Clone()
            };
            e.Handled = true;
            return true;
        }

        private static double BandPixels(Grid grid, MosaicLayout node, int boundary)
        {
            if (boundary < 0) return 0;
            if (node.Vertical)
            {
                if (boundary + 1 >= grid.ColumnDefinitions.Count) return 0;
                return grid.ColumnDefinitions[boundary].ActualWidth + grid.ColumnDefinitions[boundary + 1].ActualWidth;
            }
            if (boundary + 1 >= grid.RowDefinitions.Count) return 0;
            return grid.RowDefinitions[boundary].ActualHeight + grid.RowDefinitions[boundary + 1].ActualHeight;
        }

        private void SegmentGesture_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var gesture = _segmentGesture;
            if (gesture == null || e.Pointer.PointerId != gesture.PointerId || gesture.Origin == null) return;

            ShowHudAndOverlays();
            var pos = e.GetCurrentPoint(CellsGrid).Position;
            double along = gesture.VerticalCut ? pos.X : pos.Y;
            double delta = along - gesture.StartAlong;
            if (!gesture.Committed && Math.Abs(delta) < ViewerMath.ThroughLineUnlockSlop) return;

            var dragged = MosaicMath.DragSegment(
                gesture.Origin,
                gesture.Path,
                gesture.Boundary,
                gesture.NeedsFlip,
                gesture.Lane,
                gesture.BandPixels,
                delta,
                out bool flipped,
                out int[] resizePath,
                out int resizeBoundary);

            if (!gesture.Committed && !flipped) return;

            _mosaic = dragged;
            if (!gesture.Committed)
            {
                gesture.Committed = true;
                _mosaicCustom = true;
                _keepMosaic = true;
                _applyingSegmentUnlock = true;
                try
                {
                    BuildLayout();
                    HighlightSplit(resizePath, resizeBoundary);
                    if (gesture.Pointer != null)
                    {
                        try { RootGrid.CapturePointer(gesture.Pointer); }
                        catch { }
                    }
                }
                finally
                {
                    _applyingSegmentUnlock = false;
                    _keepMosaic = false;
                }
            }
            else
            {
                PushWeights(resizePath);
            }
            e.Handled = true;
        }

        private void SegmentGesture_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_applyingSegmentUnlock) return;
            if (_segmentGesture == null || e.Pointer.PointerId != _segmentGesture.PointerId) return;
            EndSegmentGesture();
        }

        private void HighlightSplit(int[] path, int boundary)
        {
            if (!_mosaicHosts.TryGetValue(PathKey(path), out var host)) return;
            foreach (var child in host.Grid.Children)
            {
                if (child is SplitterElement splitter && splitter.Boundary == boundary && splitter.Line != null)
                {
                    splitter.Line.Opacity = 1;
                    return;
                }
            }
        }

        private void EndSegmentGesture()
        {
            var gesture = _segmentGesture;
            if (gesture == null) return;
            _segmentGesture = null;
            FadeSplitterLines();
            if (gesture.Pointer != null)
            {
                try { RootGrid.ReleasePointerCapture(gesture.Pointer); }
                catch { }
            }
            _hudTimer.Start();
        }

        private void FadeSplitterLines()
        {
            foreach (var host in _mosaicHosts.Values)
            {
                foreach (var child in host.Grid.Children)
                {
                    if (child is SplitterElement splitter && splitter.Line != null)
                    {
                        splitter.Line.Opacity = 0;
                        ShowSpan(splitter.Line, splitter.VerticalCut, 0, 0, 0);
                    }
                }
            }
        }

        private sealed class MosaicHost
        {
            public Grid Grid { get; init; } = null!;
            public int[] Path { get; init; } = Array.Empty<int>();
        }

        private sealed class SplitBinding
        {
            public int[] Path { get; init; } = Array.Empty<int>();
            public int Boundary { get; init; }
            public bool VerticalCut { get; init; }
        }

        private sealed class SegmentGesture
        {
            public Pointer? Pointer;
            public uint PointerId;
            public bool VerticalCut;
            public int[] Path = Array.Empty<int>();
            public int Boundary;
            public int Lane;
            public bool NeedsFlip;
            public double BandPixels;
            public double StartAlong;
            public MosaicLayout? Origin;
            public bool Committed;
        }

        private sealed class SplitterElement : Grid
        {
            public Rectangle? Line;
            public int Boundary;
            public bool VerticalCut;

            public void SetCursor(InputSystemCursorShape shape)
            {
                try
                {
                    ProtectedCursor = InputSystemCursor.Create(shape);
                }
                catch { }
            }
        }
    }
}

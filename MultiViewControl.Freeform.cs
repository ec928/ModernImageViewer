using Microsoft.UI.Input;
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
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        public const string AddRuleText = "Images fill the canvas up to 16, and the rest go in the image tray. On a full canvas, the first 4 go on it as freeform images.";

        private const double DroppedCellShare = 0.45;
        private const double DroppedCellCascade = 28;
        private const double ResizeCornerSize = 22;
        private const double ResizeGripSize = 12;

        private readonly List<FreeformCard> _freeformCards = new();
        private int _maxFreeformZIndex = 5000;
        // The canvas size the floating cells are currently laid out for.
        private Size _freeformBasis;

        /// <summary>Floating cells keep their place and share of the canvas when it changes size, as grid cells do.</summary>
        private void RescaleFreeformCards(double width, double height)
        {
            if (width < 1 || height < 1) return;
            if (_freeformBasis.Width >= 1e-6 && _freeformBasis.Height >= 1e-6
                && (width != _freeformBasis.Width || height != _freeformBasis.Height))
            {
                double scaleX = width / _freeformBasis.Width;
                double scaleY = height / _freeformBasis.Height;
                foreach (var card in _freeformCards)
                {
                    var window = card.Container;
                    Canvas.SetLeft(window, Canvas.GetLeft(window) * scaleX);
                    Canvas.SetTop(window, Canvas.GetTop(window) * scaleY);
                    window.Width *= scaleX;
                    window.Height *= scaleY;
                }
            }
            _freeformBasis = new Size(width, height);
        }

        public sealed class FreeformCard
        {
            public ImageItem Item { get; set; } = null!;
            public int SourceSlot { get; set; }
            public FrameworkElement Container { get; set; } = null!;
            public Border? NotchHandle { get; set; }
            public int ZIndex { get; set; } = 5000;
        }

        public void SpawnFreeformCell(ImageItem item, FreeformCellDto? restoreDto = null)
        {
            if (item == null || (restoreDto == null && IsEmptyCell(item))) return;

            int sourceSlot = restoreDto?.SourceSlot ?? Slot(item);

            // A saved freeform cell may hold a different image from its source cell, after a swap or a copy.
            bool sameAsSource = restoreDto == null
                || (!string.IsNullOrEmpty(restoreDto.Path) && string.Equals(restoreDto.Path, item.Path, StringComparison.OrdinalIgnoreCase));

            string restoredPath = restoreDto?.Path ?? string.Empty;
            // Create a new ImageItem for this Freeform Cell so it becomes Position N+1 (e.g. Position 5)
            var freeformItem = new ImageItem
            {
                Path = sameAsSource ? item.Path : restoredPath,
                Name = sameAsSource ? (item.Name ?? Path.GetFileName(item.Path))
                    : string.IsNullOrEmpty(restoredPath) ? string.Empty
                    : (restoreDto!.Name ?? Path.GetFileName(restoredPath)),
                DateModified = sameAsSource ? item.DateModified : default,
                SizeString = sameAsSource ? item.SizeString : string.Empty,
                Thumbnail = sameAsSource ? item.Thumbnail : null,
                Dispatcher = item.Dispatcher
            };
            if (!sameAsSource && !string.IsNullOrEmpty(freeformItem.Path)) FillFileDetails(freeformItem);
            // Layouts saved when cells could be hidden: those cells are now empty.
            if (restoreDto?.Hidden == true)
            {
                freeformItem.Path = string.Empty;
                freeformItem.Name = string.Empty;
            }

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

            // A saved cell is in the units of the canvas it was saved on, and is scaled to this one after loading.
            if (restoreDto == null)
            {
                w = Math.Clamp(w, 100, maxBoundW);
                h = Math.Clamp(h, 80, maxBoundH);
                x = Math.Clamp(x, 0, Math.Max(0, maxBoundW - w));
                y = Math.Clamp(y, 0, Math.Max(0, maxBoundH - h));
            }

            int zIndex = restoreDto?.ZIndex ?? (++_maxFreeformZIndex);
            if (zIndex > _maxFreeformZIndex) _maxFreeformZIndex = zIndex;

            // The new cell clones the source cell's image turn and shape. A grid cell has no window rotation to clone.
            var geometry = restoreDto?.Geometry?.Clone() ?? GetGeometry(item).Clone();
            _cellGeometry[freeformItem] = geometry;

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

            _itemBorders[freeformItem] = (restoreDto?.Border ?? GetBorderForItem(item)).Clone();
            if (CardBorder(freeformCell) is Border cellBorder)
            {
                ApplyBorderToCellBorder(cellBorder, cellBorder.Child as Grid, freeformItem);
            }

            var card = new FreeformCard
            {
                Item = freeformItem,
                SourceSlot = sourceSlot,
                Container = freeformCell,
                ZIndex = zIndex
            };

            _freeformCards.Add(card);
            FreeformCanvas.Children.Add(freeformCell);
            AttachWindowHandles(card);
            ApplyWindowGeometry(card);
            freeformCell.SizeChanged += (_, _) => ApplyWindowGeometry(card);

            EnsureCellRootsCapacity(_items.Count);
            _cellRoots[newSlot] = freeformCell;

            // The image has moved to the new cell, so the source cell is left empty. A restore already has its cells as saved.
            if (restoreDto == null && sourceSlot >= 0 && sourceSlot < GridCellCount)
                EmptyCell(sourceSlot);

            ShowHudAndOverlays();
        }

        /// <summary>What hangs over the canvas edge is cut there, as in an export, so it cannot cover the title bar or take its clicks.</summary>
        private void AttachCanvasClip()
        {
            RootGrid.SizeChanged += (_, e) =>
            {
                FreeformCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
                ApplyCanvasCorners();
            };
        }

        /// <summary>
        /// Pictures dragged in from outside each become a new floating cell where they land, except that an empty cell under the
        /// pointer takes the first. A drop with no pictures in it, such as a folder or a saved layout, goes on to the main window
        /// as it would from anywhere else.
        /// </summary>
        private void AttachFileDrop()
        {
            bool Takes(DragEventArgs e) => _swapSource < 0 && _soloIndex < 0 && e.DataView.Contains(StandardDataFormats.StorageItems);
            // Known once the drag has been read. A folder or saved layout opens as usual, so it gets no caption about cells.
            bool hasPictures = false;
            void Over(object _, DragEventArgs e)
            {
                if (!Takes(e)) return;
                e.AcceptedOperation = DataPackageOperation.Copy;
                if (hasPictures)
                    e.DragUIOverride.Caption = EmptyCellAt(e.GetPosition(FreeformCanvas)) >= 0 ? "Add to this cell" : "Add images";
                e.Handled = true;
            }

            RootGrid.AllowDrop = true;
            RootGrid.DragEnter += async (_, e) =>
            {
                hasPictures = false;
                if (!Takes(e)) return;
                Over(RootGrid, e);
                var deferral = e.GetDeferral();
                try
                {
                    var dragged = await e.DataView.GetStorageItemsAsync();
                    hasPictures = dragged.OfType<StorageFile>().Any(f => MainWindow.IsPictureFile(f.Path));
                    Over(RootGrid, e);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    // The drop itself still works. It just goes without the caption.
                }
                finally { deferral.Complete(); }
            };
            RootGrid.DragOver += Over;
            RootGrid.Drop += async (_, e) =>
            {
                if (!Takes(e)) return;
                e.Handled = true;
                var where = e.GetPosition(FreeformCanvas);
                var deferral = e.GetDeferral();
                IReadOnlyList<IStorageItem> items;
                try { items = await e.DataView.GetStorageItemsAsync(); }
                finally { deferral.Complete(); }

                var pictures = items.OfType<StorageFile>().Select(f => f.Path).Where(MainWindow.IsPictureFile).ToList();
                if (pictures.Count > 0)
                {
                    int emptyCell = EmptyCellAt(where);
                    if (emptyCell >= 0)
                    {
                        ReplaceCellImage(emptyCell, pictures[0]);
                        pictures.RemoveAt(0);
                    }
                    if (pictures.Count > 0) await AddDroppedPicturesAsync(pictures, where);
                }
                else if (MainWindow.Instance != null)
                    await MainWindow.Instance.OpenDroppedItemsAsync(items);
            };
        }

        /// <summary>The empty cell at this point on the canvas, or -1. A floating cell covers the grid, so only the top cell there counts.</summary>
        private int EmptyCellAt(Point point)
        {
            bool Over(FrameworkElement? root) => root != null && root.ActualWidth >= 1 && root.ActualHeight >= 1 && root.IsLoaded
                && root.TransformToVisual(FreeformCanvas).TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight)).Contains(point);

            foreach (var card in _freeformCards.OrderByDescending(c => c.ZIndex))
            {
                if (Over(card.Container)) return IsEmptyCell(card.Item) ? Slot(card.Item) : -1;
            }
            for (int slot = 0; slot < Math.Min(GridCellCount, _cellRoots.Length); slot++)
            {
                if (Over(_cellRoots[slot])) return IsEmptyCell(_items[slot]) && !IsBareCanvas(slot) ? slot : -1;
            }
            return -1;
        }

        /// <summary>Pictures chosen in the app arrive like dropped ones, around the middle of the canvas.</summary>
        public Task AddPicturesAsync(IReadOnlyList<string> paths) =>
            AddDroppedPicturesAsync(paths, new Point(RootGrid.ActualWidth / 2, RootGrid.ActualHeight / 2));

        /// <summary>What adding these would do, for the picker to say before it happens.</summary>
        public string AddingNote(IReadOnlyList<string> selected)
        {
            int onCanvas = ImagesOnCanvas();
            if (selected.Count == 0)
            {
                return onCanvas < MosaicLayout.MaxShown
                    ? $"Room for {MosaicLayout.MaxShown - onCanvas} more on the canvas. The rest go in the image tray."
                    : $"The canvas is full. The first {MosaicLayout.FreeformOnFullCanvas} go on it as freeform images, and the rest in the image tray.";
            }
            var fresh = NotYetHere(selected);
            if (fresh.Count == 0) return $"{selected.Count} selected, already here";
            var (cells, freeform, tray) = MosaicLayout.PlaceAdded(onCanvas, fresh.Count);
            string canvas = freeform > 0 ? $"{freeform} to the canvas as freeform images" : $"{cells} to the canvas";
            return tray > 0 ? $"{selected.Count} selected: {canvas}, {tray} to the image tray" : $"{selected.Count} selected: {canvas}";
        }

        public string? FirstImageFolder =>
            _items.Where(i => !IsEmptyCell(i)).Select(i => Path.GetDirectoryName(i.Path)).FirstOrDefault();

        private List<string> NotYetHere(IReadOnlyList<string> paths)
        {
            var have = new HashSet<string>(_items.Select(i => i.Path ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            return paths.Where(have.Add).ToList();
        }

        private int ImagesOnCanvas() => _items.Count(i => !IsEmptyCell(i)) - TrayImages().Count;

        private async Task AddDroppedPicturesAsync(IReadOnlyList<string> paths, Point where, bool allowExisting = false)
        {
            var fresh = allowExisting ? paths.ToList() : NotYetHere(paths);
            if (fresh.Count == 0)
            {
                ShowAutoPlayToast("Those images are already here");
                return;
            }
            var (cells, freeform, tray) = MosaicLayout.PlaceAdded(ImagesOnCanvas(), fresh.Count);
            if (cells + tray > 0)
                AddToCellsAndTray(fresh.Take(cells).ToList(), fresh.Skip(cells + freeform).ToList());

            double canvasW = Math.Max(320, RootGrid.ActualWidth);
            double canvasH = Math.Max(240, RootGrid.ActualHeight);
            int placed = 0;
            foreach (string path in fresh.Skip(cells).Take(freeform))
            {
                double aspect = await ReadAspectAsync(path);
                double h = canvasH * DroppedCellShare;
                double w = h * aspect;
                if (w > canvasW * DroppedCellShare)
                {
                    w = canvasW * DroppedCellShare;
                    h = w / aspect;
                }
                double x = Math.Clamp(where.X - w / 2 + placed * DroppedCellCascade, 0, Math.Max(0, canvasW - w));
                double y = Math.Clamp(where.Y - h / 2 + placed * DroppedCellCascade, 0, Math.Max(0, canvasH - h));

                var dropped = new ImageItem { Path = path, Name = Path.GetFileName(path), Dispatcher = DispatcherQueue };
                FillFileDetails(dropped);
                // No source: no other cell changes, and Dock gives it a new grid cell. A plain frame, in front of the others.
                SpawnFreeformCell(dropped, new FreeformCellDto
                {
                    SourceSlot = -1,
                    Path = path,
                    Name = dropped.Name,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    ZIndex = _maxFreeformZIndex + 1,
                    Border = GetBorderForItem(null),
                    Geometry = new CellGeometryDto()
                });
                placed++;
            }
            NoteUserAction();
            if (tray > 0)
                ShowAutoPlayToast($"Added {fresh.Count} images: {cells + freeform} on the canvas, {tray} in the tray");
            ShowShownBadge();
            RefreshTrayCardIfOpen();
        }

        /// <summary>
        /// Cells fill the empty places first, then join the grid, which evens out its lines for the new number. The tray starts past them.
        /// A saved layout and Undo keep the tray, Surprise me takes turns through it, and Swap with can bring an image to the canvas.
        /// </summary>
        private void AddToCellsAndTray(IReadOnlyList<string> forCells, IReadOnlyList<string> forTray)
        {
            CaptureUndoPoint();
            int shown = VisibleCount(GridCellCount);
            var file = BuildLayoutFile();
            int onShow = PlaceInCells(file, forCells, shown);
            AppendToTray(file, forTray, onShow);
            ApplyKeepingBitmaps(file);
            RefreshTrayCardIfOpen();
            CaptureUndoPoint();
        }

        /// <summary>Into the empty places first, then new cells after those on show. Returns how many cells are then on show.</summary>
        private static int PlaceInCells(MultiViewLayoutFile file, IReadOnlyList<string> forCells, int shown)
        {
            int filled = 0;
            for (int i = 0; i < shown && filled < forCells.Count; i++)
            {
                if (!string.IsNullOrEmpty(file.Photos[i].Path)) continue;
                file.Photos[i].Path = forCells[filled];
                file.Photos[i].Name = Path.GetFileName(forCells[filled]);
                filled++;
            }
            int newCells = forCells.Count - filled;
            if (newCells == 0) return shown;
            file.Photos.InsertRange(shown, forCells.Skip(filled).Select(path => new PhotoSlotDto { Path = path, Name = Path.GetFileName(path) }));
            for (int i = 0; i < file.Photos.Count; i++)
                file.Photos[i].Slot = i;
            foreach (var card in file.Freeform ?? new List<FreeformCellDto>())
            {
                if (card.SourceSlot >= shown) card.SourceSlot += newCells;
            }
            file.Mosaic = null;
            file.State!.Lines = MosaicLayout.DefaultLines(shown + newCells);
            file.State.LineCounts = null;
            return shown + newCells;
        }

        /// <summary>The same rule as adding one image: a cell while the canvas has room, floating once it has 16.</summary>
        private void BringFromTray(string path)
        {
            int slot = TraySlotOf(path);
            if (slot < 0) return;
            bool room = ImagesOnCanvas() < MosaicLayout.MaxShown;
            CaptureUndoPoint();
            int shown = VisibleCount(GridCellCount);
            var file = BuildLayoutFile();
            file.Photos.RemoveAt(slot);
            for (int i = 0; i < file.Photos.Count; i++)
                file.Photos[i].Slot = i;
            foreach (var card in file.Freeform ?? new List<FreeformCellDto>())
            {
                if (card.SourceSlot == slot) card.SourceSlot = -1;
                else if (card.SourceSlot > slot) card.SourceSlot--;
            }
            AppendToTray(file, Array.Empty<string>(), room ? PlaceInCells(file, new[] { path }, shown) : shown);
            ApplyKeepingBitmaps(file);
            if (room) CaptureUndoPoint();
            else _ = AddDroppedPicturesAsync(new[] { path }, new Point(RootGrid.ActualWidth / 2, RootGrid.ActualHeight / 2), allowExisting: true);
        }

        private void ApplyKeepingBitmaps(MultiViewLayoutFile file)
        {
            _cellBorderFlyout?.Hide();
            _restoringUndo = true;
            _reuseBitmaps = DecodedBitmaps();
            try
            {
                ApplyLayoutFile(file, restoreAspect: false);
            }
            finally
            {
                _reuseBitmaps = null;
                _restoringUndo = false;
            }
        }

        /// <summary>Where docking puts this image: its own cell if still empty, or with the grid at its most, any empty cell. -1 is a new cell.</summary>
        private int DockTarget(FreeformCard card)
        {
            bool Usable(int slot) => slot >= 0 && slot < GridCellCount && IsEmptyCell(_items[slot])
                && (uint)slot < (uint)_cellRoots.Length && _cellRoots[slot]?.Tag is CellParts;

            if (Usable(card.SourceSlot)) return card.SourceSlot;
            int shown = VisibleCount(GridCellCount);
            if (shown < MosaicLayout.MaxShown) return -1;
            for (int slot = 0; slot < shown; slot++)
            {
                if (Usable(slot)) return slot;
            }
            return -1;
        }

        /// <summary>A grid at its most has no new cell to give, and one more would push an image off the canvas.</summary>
        private bool GridHasNoRoomFor(FreeformCard card) =>
            VisibleCount(GridCellCount) >= MosaicLayout.MaxShown && DockTarget(card) < 0;

        /// <summary>The image stays in the session and the canvas gets one smaller. The last cell on show stays, empty, as Remove cell leaves it.</summary>
        private void MoveCellToTray(ImageItem item)
        {
            if (IsEmptyCell(item)) return;
            string path = item.Path;
            int cardAt = _freeformCards.FindIndex(c => ReferenceEquals(c.Item, item));
            int slot = _items.IndexOf(item);
            if (cardAt < 0 && slot < 0) return;
            CaptureUndoPoint();
            int shown = VisibleCount(GridCellCount);
            var file = BuildLayoutFile();
            if (cardAt >= 0)
            {
                file.Freeform!.RemoveAt(cardAt);
            }
            else if (shown <= 1)
            {
                file.Photos[slot].Path = string.Empty;
                file.Photos[slot].Name = string.Empty;
            }
            else
            {
                file.Photos.RemoveAt(slot);
                for (int i = 0; i < file.Photos.Count; i++)
                    file.Photos[i].Slot = i;
                foreach (var card in file.Freeform ?? new List<FreeformCellDto>())
                {
                    if (card.SourceSlot == slot) card.SourceSlot = -1;
                    else if (card.SourceSlot > slot) card.SourceSlot--;
                }
                file.Mosaic = null;
                file.State!.LineCounts = null;
                shown--;
            }
            AppendToTray(file, new[] { path }, shown);
            ApplyKeepingBitmaps(file);
            CaptureUndoPoint();
        }

        private static void AppendToTray(MultiViewLayoutFile file, IEnumerable<string> paths, int shown)
        {
            file.State ??= new LayoutStateDto();
            file.State.FramedSlots = Math.Max(1, shown);
            foreach (string path in paths)
                file.Photos.Add(new PhotoSlotDto { Slot = file.Photos.Count, Path = path, Name = Path.GetFileName(path) });
        }

        /// <summary>The file's shape as it displays, read from its header. A file that will not open counts as 16:9, and its cell says so.</summary>
        private static async Task<double> ReadAspectAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if (decoder.OrientedPixelWidth > 0 && decoder.OrientedPixelHeight > 0)
                    return decoder.OrientedPixelWidth / (double)decoder.OrientedPixelHeight;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
            {
            }
            return 16.0 / 9.0;
        }

        public void DockFreeformCard(FreeformCard card)
        {
            if (card == null) return;
            if (GridHasNoRoomFor(card))
            {
                MoveCellToTray(card.Item);
                return;
            }

            // The floating cell's own slot, taken before its image moves into the grid.
            int cardSlot = _items.IndexOf(card.Item);
            int source = DockTarget(card);
            bool backToSource = source >= 0;

            FreeformCanvas.Children.Remove(card.Container);
            _freeformCards.Remove(card);

            if (backToSource)
            {
                // The image returns to its source cell, which keeps its own parameters.
                var gridParts = (CellParts)_cellRoots[source]!.Tag;
                var oldItem = _items[source];
                _items[source] = card.Item;
                MoveCellParameters(oldItem, card.Item);
                if (card.Container.Tag is CellParts freeformParts && CellImageReady(freeformParts))
                {
                    gridParts.Photo.Source = freeformParts.Photo.Source;
                    var dims = freeformParts.Bound.GetDimensions?.Invoke() ?? (16, 9);
                    gridParts.Bound.SetDimensions?.Invoke(dims.Width, dims.Height);
                    gridParts.Bound.Item = card.Item;
                    gridParts.UpdateLayout?.Invoke();
                }
                else
                {
                    RebindCell(source);
                }
                ShowEmptyState(source);
            }

            if (cardSlot >= 0)
            {
                ForgetCell(card.Container);
                _items.RemoveAt(cardSlot);
                var roots = new List<FrameworkElement?>(_cellRoots);
                if (cardSlot < roots.Count) roots.RemoveAt(cardSlot);
                _cellRoots = roots.ToArray();
            }

            if (!backToSource && cardSlot >= 0)
            {
                // The source cell is gone or shows something else, so the image gets a new grid cell, which cannot turn.
                GetGeometry(card.Item).ClearWindowTurn();
                // After the cells on show, so it does not take the place of an image in the tray.
                int gridEnd = _framedSlots > 0 ? Math.Min(_framedSlots, GridCellCount) : GridCellCount;
                _items.Insert(gridEnd, card.Item);
                var roots = new List<FrameworkElement?>(_cellRoots);
                roots.Insert(Math.Min(gridEnd, roots.Count), null);
                _cellRoots = roots.ToArray();
                if (_framedSlots > 0) _framedSlots++;
                RelayoutKeepingCells();
                RefreshOpenLayout();
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
                _itemBorders.Remove(card.Item);
                _cellGeometry.Remove(card.Item);
                ForgetCell(card.Container);
                _items.RemoveAt(index);
                var roots = new List<FrameworkElement?>(_cellRoots);
                if (index < roots.Count) roots.RemoveAt(index);
                _cellRoots = roots.ToArray();
            }

            // The last floating cell gone, the bare canvas is an empty cell again.
            ShowEmptyState(0);
            ShowShownBadge();
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
                        handle.Margin = new Thickness(ResizeCornerSize, 0, ResizeCornerSize, 0);
                        break;
                    case WindowResizeDirection.Bottom:
                        handle.Height = 8;
                        handle.VerticalAlignment = VerticalAlignment.Bottom;
                        handle.HorizontalAlignment = HorizontalAlignment.Stretch;
                        handle.Margin = new Thickness(ResizeCornerSize, 0, ResizeCornerSize, 0);
                        break;
                    case WindowResizeDirection.Left:
                        handle.Width = 8;
                        handle.HorizontalAlignment = HorizontalAlignment.Left;
                        handle.VerticalAlignment = VerticalAlignment.Stretch;
                        handle.Margin = new Thickness(0, ResizeCornerSize, 0, ResizeCornerSize);
                        break;
                    case WindowResizeDirection.Right:
                        handle.Width = 8;
                        handle.HorizontalAlignment = HorizontalAlignment.Right;
                        handle.VerticalAlignment = VerticalAlignment.Stretch;
                        handle.Margin = new Thickness(0, ResizeCornerSize, 0, ResizeCornerSize);
                        break;
                    case WindowResizeDirection.TopLeft:
                        handle.Width = ResizeCornerSize; handle.Height = ResizeCornerSize;
                        handle.HorizontalAlignment = HorizontalAlignment.Left;
                        handle.VerticalAlignment = VerticalAlignment.Top;
                        break;
                    case WindowResizeDirection.TopRight:
                        handle.Width = ResizeCornerSize; handle.Height = ResizeCornerSize;
                        handle.HorizontalAlignment = HorizontalAlignment.Right;
                        handle.VerticalAlignment = VerticalAlignment.Top;
                        break;
                    case WindowResizeDirection.BottomLeft:
                        handle.Width = ResizeCornerSize; handle.Height = ResizeCornerSize;
                        handle.HorizontalAlignment = HorizontalAlignment.Left;
                        handle.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                    case WindowResizeDirection.BottomRight:
                        handle.Width = ResizeCornerSize; handle.Height = ResizeCornerSize;
                        handle.HorizontalAlignment = HorizontalAlignment.Right;
                        handle.VerticalAlignment = VerticalAlignment.Bottom;
                        break;
                }

                if (dir is WindowResizeDirection.TopLeft or WindowResizeDirection.TopRight or WindowResizeDirection.BottomLeft or WindowResizeDirection.BottomRight)
                {
                    handle.Grip = MakeResizeGrip(handle.HorizontalAlignment, handle.VerticalAlignment);
                    handle.Children.Add(handle.Grip);
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

                        var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Container, slotGrid));
                        double degrees = card != null ? GetGeometry(card.Item).WindowRotation : 0;
                        double radians = degrees * Math.PI / 180;
                        double cos = Math.Cos(radians);
                        double sin = Math.Sin(radians);

                        // The pointer's movement along the window's own axes.
                        double localX = dx * cos + dy * sin;
                        double localY = -dx * sin + dy * cos;
                        bool right = dir is WindowResizeDirection.Right or WindowResizeDirection.TopRight or WindowResizeDirection.BottomRight;
                        bool left = dir is WindowResizeDirection.Left or WindowResizeDirection.TopLeft or WindowResizeDirection.BottomLeft;
                        bool bottom = dir is WindowResizeDirection.Bottom or WindowResizeDirection.BottomLeft or WindowResizeDirection.BottomRight;
                        bool top = dir is WindowResizeDirection.Top or WindowResizeDirection.TopLeft or WindowResizeDirection.TopRight;

                        double curW = initW;
                        double curH = initH;
                        if (right) curW = Math.Max(minW, initW + localX);
                        else if (left) curW = Math.Max(minW, initW - localX);
                        if (bottom) curH = Math.Max(minH, initH + localY);
                        else if (top) curH = Math.Max(minH, initH - localY);

                        // The window turns about its centre, so the centre moves to hold the far edge still.
                        double shiftX = right ? (curW - initW) / 2 : left ? -(curW - initW) / 2 : 0;
                        double shiftY = bottom ? (curH - initH) / 2 : top ? -(curH - initH) / 2 : 0;
                        double centreX = initX + initW / 2;
                        double centreY = initY + initH / 2;
                        double moveX = shiftX * cos - shiftY * sin;
                        double moveY = shiftX * sin + shiftY * cos;

                        // The resize stops where the centre would leave the canvas, as a move does.
                        double share = Math.Min(FreeformBounds.ResizeShare(centreX, moveX, boundW), FreeformBounds.ResizeShare(centreY, moveY, boundH));
                        curW = initW + (curW - initW) * share;
                        curH = initH + (curH - initH) * share;
                        double curX = centreX + moveX * share - curW / 2;
                        double curY = centreY + moveY * share - curH / 2;

                        slotGrid.Width = curW;
                        slotGrid.Height = curH;
                        Canvas.SetLeft(slotGrid, curX);
                        Canvas.SetTop(slotGrid, curY);

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
        /// <summary>The square that shows where a corner can be dragged. A shape can leave its corners in empty space, so the grip marks them.</summary>
        private static Border MakeResizeGrip(HorizontalAlignment horizontal, VerticalAlignment vertical) => new()
        {
            Width = ResizeGripSize,
            Height = ResizeGripSize,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 20, 20, 20)),
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
            Opacity = 0
        };

        public sealed class FreeformResizeHandle : Grid
        {
            public Border? Grip { get; set; }

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

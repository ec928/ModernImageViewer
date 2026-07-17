using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Windows.Foundation;

namespace ModernImageViewer.Collage
{
    public partial class CollageProject : INotifyPropertyChanged
    {
        private string _name = "Untitled Collage";
        private string _backgroundColor = "#FFFFFFFF";
        private double _defaultCellSpacing = 0.0;
        private double _canvasWidth = 1920;
        private double _canvasHeight = 1080;
        private DateTime _lastModified = DateTime.Now;

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string BackgroundColor
        {
            get => _backgroundColor;
            set { if (_backgroundColor != value) { _backgroundColor = value; OnPropertyChanged(); } }
        }

        public double DefaultCellSpacing
        {
            get => _defaultCellSpacing;
            set { if (Math.Abs(_defaultCellSpacing - value) > 0.1) { _defaultCellSpacing = value; OnPropertyChanged(); } }
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            set { if (Math.Abs(_canvasWidth - value) > 1) { _canvasWidth = Math.Max(100, value); OnPropertyChanged(); } }
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            set { if (Math.Abs(_canvasHeight - value) > 1) { _canvasHeight = Math.Max(100, value); OnPropertyChanged(); } }
        }

        public DateTime LastModified
        {
            get => _lastModified;
            set { if (_lastModified != value) { _lastModified = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<CollageElement> Elements { get; set; } = new();

        [JsonIgnore]
        public bool HasUnsavedChanges { get; set; } = false;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public CollageProject()
        {
            Elements.CollectionChanged += (s, e) =>
            {
                HasUnsavedChanges = true;
                LastModified = DateTime.Now;
            };
        }

        public void ApplyDefaultTwoCellTemplate()
        {
            Elements.Clear();
            double w = CanvasWidth;
            double h = CanvasHeight;
            double gap = DefaultCellSpacing;

            Elements.Add(new CollageElement { Layout = new Rect(gap, gap, (w - gap * 3) / 2, h - gap * 2) });
            Elements.Add(new CollageElement { Layout = new Rect(gap * 2 + (w - gap * 3) / 2, gap, (w - gap * 3) / 2, h - gap * 2) });
            HasUnsavedChanges = true;
        }

        public Rect GetContentBounds()
        {
            if (Elements.Count == 0) return new Rect(0, 0, CanvasWidth, CanvasHeight);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

            foreach (var el in Elements)
            {
                var r = el.Layout;
                if (r.Width <= 0 || r.Height <= 0) continue;
                minX = Math.Min(minX, r.X); minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width); maxY = Math.Max(maxY, r.Y + r.Height);
            }
            if (minX == double.MaxValue) return new Rect(0, 0, CanvasWidth, CanvasHeight);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // ----------------------------------------------------------------------
        // CORE GEOMETRY ENGINE: Space Partitioning for Obstacle Avoidance
        // ----------------------------------------------------------------------

        private List<Rect> SubtractRect(Rect source, Rect hole)
        {
            List<Rect> result = new();

            if (source.Left >= hole.Right || source.Right <= hole.Left ||
                source.Top >= hole.Bottom || source.Bottom <= hole.Top)
            {
                result.Add(source);
                return result;
            }

            if (hole.Top > source.Top)
                result.Add(new Rect(source.Left, source.Top, source.Width, hole.Top - source.Top));

            if (hole.Bottom < source.Bottom)
                result.Add(new Rect(source.Left, hole.Bottom, source.Width, source.Bottom - hole.Bottom));

            double middleTop = Math.Max(source.Top, hole.Top);
            double middleBottom = Math.Min(source.Bottom, hole.Bottom);

            if (hole.Left > source.Left)
                result.Add(new Rect(source.Left, middleTop, hole.Left - source.Left, middleBottom - middleTop));

            if (hole.Right < source.Right)
                result.Add(new Rect(hole.Right, middleTop, source.Right - hole.Right, middleBottom - middleTop));

            return result;
        }

        private List<Rect> GetFreeCanvasRegions()
        {
            var obstacles = Elements.Where(e => e.IsLocked || e.IsAnchored || e.IsContentLocked).ToList();
            List<Rect> freeSpaces = new List<Rect> { new Rect(0, 0, CanvasWidth, CanvasHeight) };

            foreach (var obstacle in obstacles)
            {
                List<Rect> newFreeSpaces = new();
                foreach (var space in freeSpaces)
                {
                    newFreeSpaces.AddRange(SubtractRect(space, obstacle.Layout));
                }
                freeSpaces = newFreeSpaces;
            }

            return freeSpaces.OrderByDescending(r => r.Width * r.Height).ToList();
        }

        // ----------------------------------------------------------------------
        // AUTO-LAYOUT ALGORITHMS
        // ----------------------------------------------------------------------

        public void ApplyBentoLayout()
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int n = movable.Count;
            if (n == 0) return;

            List<Rect> freeSpaces = GetFreeCanvasRegions();
            if (freeSpaces.Count == 0) freeSpaces.Add(new Rect(0, 0, CanvasWidth, CanvasHeight));

            while (freeSpaces.Count < n)
            {
                int biggestIdx = 0;
                double maxArea = 0;
                for (int i = 0; i < freeSpaces.Count; i++)
                {
                    double area = freeSpaces[i].Width * freeSpaces[i].Height;
                    if (area > maxArea) { maxArea = area; biggestIdx = i; }
                }

                Rect toSplit = freeSpaces[biggestIdx];
                freeSpaces.RemoveAt(biggestIdx);

                if (toSplit.Width > toSplit.Height)
                {
                    double w1 = toSplit.Width / 2;
                    freeSpaces.Add(new Rect(toSplit.X, toSplit.Y, w1, toSplit.Height));
                    freeSpaces.Add(new Rect(toSplit.X + w1, toSplit.Y, toSplit.Width - w1, toSplit.Height));
                }
                else
                {
                    double h1 = toSplit.Height / 2;
                    freeSpaces.Add(new Rect(toSplit.X, toSplit.Y, toSplit.Width, h1));
                    freeSpaces.Add(new Rect(toSplit.X, toSplit.Y + h1, toSplit.Width, toSplit.Height - h1));
                }
            }

            freeSpaces = freeSpaces.OrderByDescending(r => r.Width * r.Height).ToList();

            double gap = DefaultCellSpacing;

            for (int i = 0; i < n; i++)
            {
                var r = freeSpaces[i];
                movable[i].Layout = new Rect(r.X + gap / 2, r.Y + gap / 2, r.Width - gap, r.Height - gap);
                movable[i].NeedsContentFill = true;
            }
            HasUnsavedChanges = true;
        }

        private void ApplyOrganicLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int n = movable.Count;
            if (n == 0) return;

            double gap = DefaultCellSpacing;

            // Seed organic generation with safe free space rather than full canvas
            var regions = GetFreeCanvasRegions();
            if (regions.Count == 0)
            {
                regions.Add(new Rect(gap / 2, gap / 2, width - gap, height - gap));
            }
            else
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    var r = regions[i];
                    regions[i] = new Rect(r.X + gap / 2, r.Y + gap / 2, Math.Max(10, r.Width - gap), Math.Max(10, r.Height - gap));
                }
            }

            var rand = new Random();
            while (regions.Count < n)
            {
                int largestIdx = 0;
                double maxArea = 0;
                for (int i = 0; i < regions.Count; i++)
                {
                    double area = regions[i].Width * regions[i].Height;
                    if (area > maxArea) { maxArea = area; largestIdx = i; }
                }

                Rect big = regions[largestIdx];
                regions.RemoveAt(largestIdx);

                bool splitVertical = big.Width > big.Height * 1.3 || (big.Width > big.Height && rand.NextDouble() > 0.4);

                if (splitVertical)
                {
                    double splitX = big.X + big.Width * (0.35 + rand.NextDouble() * 0.3);
                    regions.Add(new Rect(big.X, big.Y, splitX - big.X, big.Height));
                    regions.Add(new Rect(splitX, big.Y, big.X + big.Width - splitX, big.Height));
                }
                else
                {
                    double splitY = big.Y + big.Height * (0.35 + rand.NextDouble() * 0.3);
                    regions.Add(new Rect(big.X, big.Y, big.Width, splitY - big.Y));
                    regions.Add(new Rect(big.X, splitY, big.Width, big.Y + big.Height - splitY));
                }
            }

            regions = regions.OrderByDescending(r => r.Width * r.Height).ToList();

            for (int i = 0; i < n; i++)
            {
                var r = regions[i];
                movable[i].Layout = new Rect(r.X, r.Y, r.Width, r.Height);
                movable[i].NeedsContentFill = true;
            }
            HasUnsavedChanges = true;
        }

        private void ApplyGridLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int count = movable.Count;
            if (count == 0 || width <= 10 || height <= 10) return;

            double aspect = width / height;
            int cols = Math.Max(1, (int)Math.Round(Math.Sqrt(count * aspect)));
            int rows = Math.Max(1, (int)Math.Ceiling((double)count / cols));

            double cellWidth = width / cols;
            double cellHeight = height / rows;
            double padding = DefaultCellSpacing;

            for (int i = 0; i < count; i++)
            {
                int row = i / cols;
                int col = i % cols;

                int itemsInThisRow = (row == rows - 1 && count % cols != 0) ? (count % cols) : cols;
                double emptySpaceInRow = width - (itemsInThisRow * cellWidth);
                double rowOffsetX = emptySpaceInRow / 2.0;

                movable[i].Layout = new Rect(
                    rowOffsetX + (col * cellWidth) + (padding / 2),
                    (row * cellHeight) + (padding / 2),
                    Math.Max(1.0, cellWidth - padding),
                    Math.Max(1.0, cellHeight - padding));

                movable[i].NeedsContentFill = true;
            }
        }

        private void ApplyColumnLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int count = movable.Count;
            if (count == 0 || width <= 10 || height <= 10) return;

            double padding = DefaultCellSpacing;
            double cellWidth = width / count;

            for (int i = 0; i < count; i++)
            {
                movable[i].Layout = new Rect(
                    (i * cellWidth) + (padding / 2),
                    padding / 2,
                    Math.Max(1.0, cellWidth - padding),
                    Math.Max(1.0, height - padding));

                movable[i].NeedsContentFill = true;
            }
        }

        private void ApplyRowLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int count = movable.Count;
            if (count == 0 || width <= 10 || height <= 10) return;

            double padding = DefaultCellSpacing;
            double cellHeight = height / count;

            for (int i = 0; i < count; i++)
            {
                movable[i].Layout = new Rect(
                    padding / 2,
                    (i * cellHeight) + (padding / 2),
                    Math.Max(1.0, width - padding),
                    Math.Max(1.0, cellHeight - padding));

                movable[i].NeedsContentFill = true;
            }
        }

        private void ApplyMasonryLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            if (movable.Count == 0 || width <= 10) return;

            double padding = DefaultCellSpacing;

            // Dynamically calculate columns based on width, 
            // but cap it at the actual number of elements to prevent empty column gaps
            int maxCols = Math.Clamp((int)(width / 260), 2, 6);
            int cols = Math.Min(movable.Count, maxCols);

            double columnWidth = (width - (padding * (cols + 1))) / cols;
            columnWidth = Math.Max(80, columnWidth);

            double[] colHeights = new double[cols];
            for (int i = 0; i < cols; i++) colHeights[i] = padding;

            foreach (var el in movable)
            {
                int shortestCol = 0;
                for (int i = 1; i < cols; i++)
                    if (colHeights[i] < colHeights[shortestCol]) shortestCol = i;

                double aspect = 1.0;
                // Business logic check: Use NativeWidth/Height which are CPU-side 
                // and unaffected by the GPU resource localization changes
                if (el.CachedEntry != null)
                {
                    double w = el.CachedEntry.NativeWidth > 0 ? el.CachedEntry.NativeWidth : 1.0;
                    double h = el.CachedEntry.NativeHeight > 0 ? el.CachedEntry.NativeHeight : 1.0;
                    aspect = w / h;
                }

                double elHeight = Math.Max(90, columnWidth / aspect);

                el.Layout = new Rect(
                    padding + (shortestCol * (columnWidth + padding)),
                    colHeights[shortestCol],
                    columnWidth,
                    elHeight);

                colHeights[shortestCol] += elHeight + padding;
                el.NeedsContentFill = true;
            }

            double maxBottom = colHeights.Max() + padding * 2;
            if (maxBottom > CanvasHeight)
                CanvasHeight = maxBottom;
        }

        private void ApplyTiledLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int count = movable.Count;
            if (count == 0 || width <= 10 || height <= 10) return;

            double aspect = width / height;

            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count * aspect)));
            int rows = Math.Max(1, (int)Math.Ceiling((double)count / cols));

            double padding = DefaultCellSpacing;
            double cellSize = Math.Min(width / cols, height / rows);

            double gridHeight = rows * cellSize;
            double startY = (height - gridHeight) / 2.0;

            for (int i = 0; i < count; i++)
            {
                int r = i / cols;
                int c = i % cols;

                int itemsInThisRow = (r == rows - 1 && count % cols != 0) ? (count % cols) : cols;
                double rowWidth = itemsInThisRow * cellSize;
                double startX = (width - rowWidth) / 2.0;

                movable[i].Layout = new Rect(
                    startX + (c * cellSize) + (padding / 2),
                    startY + (r * cellSize) + (padding / 2),
                    Math.Max(1.0, cellSize - padding),
                    Math.Max(1.0, cellSize - padding));

                movable[i].NeedsContentFill = true;
            }
        }

        private void ApplyBrickLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            int n = movable.Count;
            if (n == 0) return;

            double gap = DefaultCellSpacing;

            int rows = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * (height / Math.Max(1, width)) * 0.9)));
            double availableH = height - gap * (rows + 1);
            double rowH = availableH / rows;

            int cols = (int)Math.Ceiling((double)n / rows);
            double availableW = width - gap * (cols + 1);
            double cellW = availableW / cols;

            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                bool offset = r % 2 == 1;
                int itemsThisRow = Math.Min(cols, n - idx);
                double startX = offset ? gap / 2 + cellW * 0.5 : gap / 2;

                for (int c = 0; c < itemsThisRow; c++)
                {
                    double x = startX + c * (cellW + gap);
                    double w = cellW;

                    if (offset && c == itemsThisRow - 1 && x + w > width - gap / 2)
                        w = width - gap / 2 - x;

                    movable[idx].Layout = new Rect(x, gap / 2 + r * (rowH + gap), Math.Max(20, w), rowH);
                    movable[idx].NeedsContentFill = true;
                    idx++;
                }
            }
            HasUnsavedChanges = true;
        }

        private void ApplyHeroLayout(double width, double height)
        {
            var movable = Elements.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            if (movable.Count == 0) return;

            double gap = DefaultCellSpacing;

            var hero = movable[0];
            double heroWidth = Math.Max(300, width * 0.58);
            hero.Layout = new Rect(gap / 2, gap / 2, heroWidth - gap, height - gap);
            hero.NeedsContentFill = true;

            var remaining = movable.Skip(1).ToList();
            if (remaining.Count == 0)
            {
                HasUnsavedChanges = true;
                return;
            }

            double rightX = heroWidth + gap / 2;
            double rightWidth = width - heroWidth - gap;
            double cellHeight = (height - gap * (remaining.Count + 1)) / remaining.Count;

            for (int i = 0; i < remaining.Count; i++)
            {
                remaining[i].Layout = new Rect(
                    rightX,
                    gap / 2 + i * (cellHeight + gap),
                    rightWidth - gap / 2,
                    cellHeight);
                remaining[i].NeedsContentFill = true;
            }
            HasUnsavedChanges = true;
        }

        public void DistributeElements(List<CollageElement> selected, bool horizontal)
        {
            if (selected == null || selected.Count < 2) return;

            var active = selected.Where(e => !e.IsLocked && !e.IsAnchored && !e.IsContentLocked).ToList();
            if (active.Count < 2) return;

            double gap = DefaultCellSpacing;

            if (horizontal)
            {
                var sorted = active.OrderBy(e => e.Layout.Left).ToList();
                double currentX = sorted.First().Layout.Left;

                foreach (var el in sorted)
                {
                    el.Layout = new Rect(currentX, el.Layout.Top, el.Layout.Width, el.Layout.Height);
                    currentX += el.Layout.Width + gap;
                }
            }
            else
            {
                var sorted = active.OrderBy(e => e.Layout.Top).ToList();
                double currentY = sorted.First().Layout.Top;

                foreach (var el in sorted)
                {
                    el.Layout = new Rect(el.Layout.Left, currentY, el.Layout.Width, el.Layout.Height);
                    currentY += el.Layout.Height + gap;
                }
            }
            HasUnsavedChanges = true;
        }

        public CollageProject DeepClone()
        {
            var clone = new CollageProject
            {
                Name = this.Name + " (Copy)",
                BackgroundColor = this.BackgroundColor,
                DefaultCellSpacing = this.DefaultCellSpacing,
                CanvasWidth = this.CanvasWidth,
                CanvasHeight = this.CanvasHeight
            };
            foreach (var el in this.Elements) clone.Elements.Add(el.Clone());
            return clone;
        }

        public void ApplyLayout(string layoutName, double canvasWidth, double canvasHeight)
        {
            if (Elements.Count == 0 || canvasWidth <= 0 || canvasHeight <= 0) return;

            switch (layoutName)
            {
                case "Grid":
                    ApplyGridLayout(canvasWidth, canvasHeight);
                    break;
                case "Columns":
                    ApplyColumnLayout(canvasWidth, canvasHeight);
                    break;
                case "Rows":
                    ApplyRowLayout(canvasWidth, canvasHeight);
                    break;
                case "Masonry":
                    ApplyMasonryLayout(canvasWidth, canvasHeight);
                    break;
                case "Tiled":
                    ApplyTiledLayout(canvasWidth, canvasHeight);
                    break;
                case "Bento":
                    ApplyBentoLayout();
                    break;
                case "Brick":
                    ApplyBrickLayout(canvasWidth, canvasHeight);
                    break;
                case "Organic":
                    ApplyOrganicLayout(canvasWidth, canvasHeight);
                    break;
                case "Hero":
                    ApplyHeroLayout(canvasWidth, canvasHeight);
                    break;
                case "Freeform":
                    return;
                default:
                    return;
            }
        }
    }
}
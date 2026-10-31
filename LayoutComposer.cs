using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    /// <summary>A share of a picture: 0 to 1 across and down.</summary>
    public readonly record struct UnitRect(double X, double Y, double W, double H)
    {
        public double Area => W * H;
    }

    /// <summary>What the composer needs to know about a picture. Faces and the focus are shares of the picture.</summary>
    public sealed record PhotoFacts(string Path, double Width, double Height, IReadOnlyList<UnitRect> Faces, double FocusX, double FocusY, string Color)
    {
        public double Aspect => Width / Math.Max(1, Height);
    }

    /// <summary>One cell of a composition, in canvas units, before it is written out as a layout.</summary>
    public sealed class ComposedCell
    {
        public PhotoFacts Photo { get; init; } = null!;
        public double X { get; init; }
        public double Y { get; init; }
        public double W { get; init; }
        public double H { get; init; }
        public double Rotation { get; init; }
        public bool Floating { get; init; }
        public CellBorderDto Frame { get; init; } = FrameLooks.Flush();
        public CellGeometryDto Geometry { get; init; } = new();
        public double PanX { get; set; }
        public double PanY { get; set; }
        public double ImageWidth { get; set; }
        public double ImageHeight { get; set; }
    }

    public sealed class Composition
    {
        public string Recipe { get; init; } = "";
        /// <summary>Back to front.</summary>
        public List<ComposedCell> Cells { get; init; } = new();
        public MosaicLayout? Mosaic { get; init; }
        public string CanvasColor { get; init; } = CanvasLook.DefaultColor;
        public string CanvasSurface { get; init; } = CanvasLook.Solid;
        public CellBorderDto CanvasFrame { get; init; } = FrameLooks.Flush();
        public double Width { get; init; }
        public double Height { get; init; }
        public double Score { get; set; }
        /// <summary>Faces that a shape, a crop or another cell cuts into. The search avoids any it can.</summary>
        public int FacesCut { get; set; }
        /// <summary>Pictures mostly hidden by others on top, so they were placed for nothing.</summary>
        public int PicturesBuried { get; set; }

        public MultiViewLayoutFile ToLayoutFile()
        {
            var file = new MultiViewLayoutFile
            {
                Canvas = new CanvasLayoutDto
                {
                    BackgroundColor = CanvasColor,
                    Surface = CanvasSurface,
                    Frame = CanvasFrame.Clone(),
                    Width = Width,
                    Height = Height
                },
                State = new LayoutStateDto { IsFillMode = true }
            };

            var grid = Cells.Where(c => !c.Floating).ToList();
            for (int i = 0; i < grid.Count; i++)
            {
                var cell = grid[i];
                file.Photos.Add(new PhotoSlotDto
                {
                    Slot = i,
                    Path = cell.Photo.Path,
                    Name = System.IO.Path.GetFileName(cell.Photo.Path),
                    PanX = cell.PanX,
                    PanY = cell.PanY,
                    Border = cell.Frame.Clone(),
                    Geometry = cell.Geometry.Clone()
                });
            }
            // A session always has a grid. Scattered prints lie on the bare canvas, over one empty cell.
            if (grid.Count == 0) file.Photos.Add(new PhotoSlotDto { Slot = 0, Path = string.Empty });
            file.State.Lines = Math.Max(1, MosaicLayout.DefaultLines(Math.Max(1, grid.Count)));
            if (Mosaic != null) file.Mosaic = MosaicNodeDto.FromMosaicLayout(Mosaic);

            var floating = Cells.Where(c => c.Floating).ToList();
            if (floating.Count > 0)
            {
                file.Freeform = new List<FreeformCellDto>();
                for (int i = 0; i < floating.Count; i++)
                {
                    var cell = floating[i];
                    file.Freeform.Add(new FreeformCellDto
                    {
                        SourceSlot = -1,
                        Path = cell.Photo.Path,
                        Name = System.IO.Path.GetFileName(cell.Photo.Path),
                        X = cell.X,
                        Y = cell.Y,
                        Width = cell.W,
                        Height = cell.H,
                        ZIndex = 5001 + i,
                        PanX = cell.PanX,
                        PanY = cell.PanY,
                        Border = cell.Frame.Clone(),
                        Geometry = cell.Geometry.Clone()
                    });
                }
            }
            return file;
        }
    }

    /// <summary>
    /// Makes something worth looking at from a set of pictures. Nothing here picks settings at random: each recipe is one
    /// designed idea with its own rules, the random part only decides which version of it, and every version is scored
    /// against the actual pictures, so a face is not cut by a shape or covered by another cell. The result is an ordinary layout.
    /// </summary>
    public static class LayoutComposer
    {
        public const string EdgeToEdge = "Edge to edge";
        public const string GalleryWall = "Gallery wall";
        public const string HeroSpotlight = "Hero spotlight";
        public const string ScatteredPrints = "Scattered prints";
        public static readonly string[] Recipes = { EdgeToEdge, GalleryWall, HeroSpotlight, ScatteredPrints };

        private const int Versions = 48;
        private const int TreesPerVersion = 120;
        private const int MaxScattered = 9;

        public static Composition? Compose(IReadOnlyList<PhotoFacts> photos, double width, double height, int seed, string? avoid = null, double rasterScale = 1, string? only = null)
        {
            var usable = photos.Where(p => p.Width >= 1 && p.Height >= 1).Take(MosaicLayout.MaxShown).ToList();
            if (usable.Count < 2 || width < 50 || height < 50) return null;

            var random = new Random(seed);
            var recipes = Recipes.Where(r => CanMake(r, usable.Count) && (only == null || r == only)).OrderBy(_ => random.Next()).ToList();
            // The one just shown comes last, so clicking again gives something different.
            if (recipes.Count > 1 && recipes.Remove(avoid ?? "")) recipes.Add(avoid!);

            Composition? fallback = null;
            foreach (string recipe in recipes)
            {
                // The look is settled before the search, or the score would pick the same shape every time.
                int look = random.Next(int.MaxValue);
                for (int pass = 0; pass < 2; pass++)
                {
                    var found = new List<Composition>();
                    for (int i = 0; i < Versions; i++)
                    {
                        var version = Build(recipe, usable, width, height, random, pass == 0 ? look : random.Next(int.MaxValue));
                        if (version == null) continue;
                        Judge(version, rasterScale);
                        found.Add(version);
                    }
                    var ranked = found.OrderByDescending(c => c.Score).ToList();
                    if (ranked.Count == 0) break;
                    if (fallback == null || ranked[0].Score > fallback.Score) fallback = ranked[0];
                    var clean = ranked.Where(c => c.FacesCut == 0 && c.PicturesBuried == 0).ToList();
                    if (clean.Count > 0) return clean[random.Next(Math.Min(3, clean.Count))];
                    if (recipe != HeroSpotlight) break;
                }
            }
            return fallback;
        }

        public static bool CanMake(string recipe, int photos) => recipe switch
        {
            HeroSpotlight => photos >= 3,
            ScatteredPrints => photos >= 3 && photos <= MaxScattered,
            _ => photos >= 2
        };

        private static Composition? Build(string recipe, List<PhotoFacts> photos, double w, double h, Random random, int look) => recipe switch
        {
            EdgeToEdge => BuildEdgeToEdge(photos, w, h, random),
            GalleryWall => BuildGalleryWall(photos, w, h, random),
            HeroSpotlight => BuildHeroSpotlight(photos, w, h, random, look),
            _ => BuildScatteredPrints(photos, w, h, random)
        };

        private static Composition? BuildEdgeToEdge(List<PhotoFacts> photos, double w, double h, Random random)
        {
            double seam = random.Next(3) == 0 ? 0 : 1.5;
            var frame = seam > 0 ? new CellBorderDto { Color = "#00000000", Padding = seam, GutterMaterial = "None", GutterColor = "#00000000" } : FrameLooks.Flush();
            var tree = FindTree(photos, w / h, random);
            if (tree == null) return null;
            var cells = new List<ComposedCell>();
            var mosaic = Place(tree, 0, 0, w, h, frame, cells);
            return Finish(EdgeToEdge, cells, mosaic, "#FF0A0A0A", CanvasLook.Solid, FrameLooks.Flush(), w, h);
        }

        private static Composition? BuildGalleryWall(List<PhotoFacts> photos, double w, double h, Random random)
        {
            // One sheet of card with the pictures set into it: the margin all round matches the gaps between them.
            string card = random.Next(3) switch
            {
                0 => "#FFF8F8F8",
                1 => "#FF141414",
                _ => Tone(Boldest(photos, random).Color, 0.22, 0.16)
            };
            double gap = Math.Clamp(Math.Min(w, h) * (0.012 + random.NextDouble() * 0.012), 6, 20);
            double radius = random.Next(3) == 0 ? gap * 0.6 : 0;
            var cellFrame = new CellBorderDto { Color = "#00000000", Padding = gap / 2, CornerRadius = radius, GutterMaterial = "Solid", GutterColor = card };
            var canvasFrame = new CellBorderDto { Color = "#00000000", Padding = gap / 2, GutterMaterial = "Solid", GutterColor = card };
            var tree = FindTree(photos, (w - gap) / (h - gap), random);
            if (tree == null) return null;
            var cells = new List<ComposedCell>();
            var mosaic = Place(tree, gap / 2, gap / 2, w - gap, h - gap, cellFrame, cells);
            return Finish(GalleryWall, cells, mosaic, card, CanvasLook.Solid, canvasFrame, w, h);
        }

        private static Composition? BuildHeroSpotlight(List<PhotoFacts> photos, double w, double h, Random random, int look)
        {
            var hero = photos.OrderByDescending(Presence).Take(2).OrderBy(_ => random.Next()).First();
            var rest = photos.Where(p => !ReferenceEquals(p, hero)).ToList();
            var tree = FindTree(rest, w / h, random);
            if (tree == null) return null;
            var cells = new List<ComposedCell>();
            var flush = new CellBorderDto { Color = "#00000000", Padding = 1, GutterMaterial = "None", GutterColor = "#00000000" };
            var mosaic = Place(tree, 0, 0, w, h, flush, cells);

            string[] shapes = { WindowShapes.Arch, WindowShapes.Oval, WindowShapes.Squircle, WindowShapes.Hexagon };
            string shape = shapes[look % shapes.Length];
            double cardH = h * (0.62 + random.NextDouble() * 0.2);
            double aspect = shape switch
            {
                WindowShapes.Arch => 0.72 + random.NextDouble() * 0.12,
                WindowShapes.Oval => 0.95 + random.NextDouble() * 0.15,
                WindowShapes.Hexagon => 1.12,
                _ => Math.Clamp(hero.Aspect, 0.8, 1.45)
            };
            double cardW = Math.Min(cardH * aspect, w * 0.7);
            cardH = cardW / aspect;
            double[] across = { 0.5, 0.33, 0.67 };
            double cx = w * across[random.Next(across.Length)];
            double cy = h * (0.46 + random.NextDouble() * 0.08);
            double shadowRoom = Math.Min(w, h) * 0.035;
            double line = Math.Clamp(Math.Min(w, h) * 0.007, 3, 8);
            cells.Add(new ComposedCell
            {
                Photo = hero,
                Floating = true,
                X = cx - cardW / 2 - shadowRoom,
                Y = cy - cardH / 2 - shadowRoom,
                W = cardW + 2 * shadowRoom,
                H = cardH + 2 * shadowRoom,
                Geometry = new CellGeometryDto { Shape = shape },
                Frame = new CellBorderDto
                {
                    Color = "#FFFFFFFF",
                    Thickness = line,
                    Padding = shadowRoom,
                    CornerRadius = shape == WindowShapes.Squircle || shape == WindowShapes.Oval ? 0 : line * 2,
                    ShadowIntensity = 70,
                    Shadow = true,
                    GutterMaterial = "None",
                    GutterColor = "#00000000"
                }
            });
            return Finish(HeroSpotlight, cells, mosaic, Tone(hero.Color, 0.25, 0.1), CanvasLook.Solid, FrameLooks.Flush(), w, h);
        }

        private static Composition? BuildScatteredPrints(List<PhotoFacts> photos, double w, double h, Random random)
        {
            int count = photos.Count;
            int columns = Math.Max(1, (int)Math.Round(Math.Sqrt(count * w / h)));
            int rows = (int)Math.Ceiling(count / (double)columns);
            var spots = new List<(double X, double Y)>();
            for (int r = 0; r < rows; r++)
            {
                int inRow = r == rows - 1 ? count - columns * (rows - 1) : columns;
                for (int c = 0; c < inRow; c++)
                    spots.Add(((c + 0.5) / inRow * w, (r + 0.5) / rows * h));
            }

            double share = 1.05 + random.NextDouble() * 0.25;
            double border = Math.Clamp(Math.Min(w, h) * 0.012, 5, 12);
            double shadowRoom = Math.Min(w, h) * 0.03;
            var order = photos.OrderBy(_ => random.Next()).ToList();
            var cells = new List<ComposedCell>();
            for (int i = 0; i < count; i++)
            {
                var photo = order[i];
                double area = w * h * share / count;
                double printH = Math.Sqrt(area / photo.Aspect);
                double printW = printH * photo.Aspect;
                double fit = Math.Min(1, Math.Min(w * 0.62 / printW, h * 0.72 / printH));
                printW *= fit;
                printH *= fit;
                double jitterX = (random.NextDouble() - 0.5) * w / columns * 0.3;
                double jitterY = (random.NextDouble() - 0.5) * h / rows * 0.3;
                double cx = Math.Clamp(spots[i].X + jitterX, printW * 0.46, w - printW * 0.46);
                double cy = Math.Clamp(spots[i].Y + jitterY, printH * 0.46, h - printH * 0.46);
                double turn = (random.NextDouble() < 0.5 ? -1 : 1) * (1.5 + random.NextDouble() * 5);
                cells.Add(new ComposedCell
                {
                    Photo = photo,
                    Floating = true,
                    X = cx - printW / 2 - shadowRoom,
                    Y = cy - printH / 2 - shadowRoom,
                    W = printW + 2 * shadowRoom,
                    H = printH + 2 * shadowRoom,
                    Rotation = turn,
                    Geometry = new CellGeometryDto { WindowRotation = turn },
                    Frame = new CellBorderDto
                    {
                        Color = "#FFFAFAFA",
                        Thickness = border,
                        Padding = shadowRoom,
                        ShadowIntensity = 55,
                        Shadow = true,
                        GutterMaterial = "None",
                        GutterColor = "#00000000"
                    }
                });
            }
            string surface = random.Next(2) == 0 ? CanvasLook.Vignette : CanvasLook.Gradient;
            return Finish(ScatteredPrints, cells, null, Tone(Boldest(photos, random).Color, 0.28, 0.3), surface, FrameLooks.Flush(), w, h);
        }

        // ---- Fitting pictures to a rectangle

        private sealed class Node
        {
            public Node? A, B;
            public bool SideBySide;
            public PhotoFacts? Photo;
            public double Aspect;
        }

        private static Node Leaf(PhotoFacts photo) => new() { Photo = photo, Aspect = photo.Aspect };

        private static Node Join(Node a, Node b, bool sideBySide) => new()
        {
            A = a,
            B = b,
            SideBySide = sideBySide,
            Aspect = sideBySide ? a.Aspect + b.Aspect : 1 / (1 / a.Aspect + 1 / b.Aspect)
        };

        /// <summary>
        /// A tree of cuts whose shape comes out close to the canvas when each picture keeps its own shape. Then the pictures
        /// barely need cropping, however mixed they are, which an even grid cannot do.
        /// </summary>
        private static Node? FindTree(List<PhotoFacts> photos, double target, Random random)
        {
            if (photos.Count == 0) return null;
            Node? best = null;
            double bestCost = double.MaxValue;
            for (int attempt = 0; attempt < TreesPerVersion; attempt++)
            {
                var shuffled = photos.OrderBy(_ => random.Next()).ToList();
                var tree = RandomTree(shuffled, 0, shuffled.Count, random);
                double cost = Math.Abs(Math.Log(tree.Aspect / target)) + SmallestCellCost(tree, target) + random.NextDouble() * 0.04;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = tree;
                }
            }
            return best;
        }

        private static Node RandomTree(List<PhotoFacts> photos, int start, int count, Random random)
        {
            if (count == 1) return Leaf(photos[start]);
            int first = count == 2 ? 1 : 1 + random.Next(count - 1);
            return Join(RandomTree(photos, start, first, random), RandomTree(photos, start + first, count - first, random), random.Next(2) == 0);
        }

        /// <summary>A tree can leave one picture tiny. The share of the canvas held by its smallest cell, compared with an even share.</summary>
        private static double SmallestCellCost(Node tree, double target)
        {
            double smallest = 1;
            int count = 0;
            void Walk(Node n, double w, double h)
            {
                if (n.Photo != null)
                {
                    count++;
                    smallest = Math.Min(smallest, w * h);
                    return;
                }
                if (n.SideBySide)
                {
                    double a = w * n.A!.Aspect / (n.A.Aspect + n.B!.Aspect);
                    Walk(n.A, a, h);
                    Walk(n.B, w - a, h);
                }
                else
                {
                    double ia = 1 / n.A!.Aspect, ib = 1 / n.B!.Aspect;
                    double a = h * ia / (ia + ib);
                    Walk(n.A, w, a);
                    Walk(n.B, w, h - a);
                }
            }
            Walk(tree, 1, 1);
            double even = 1.0 / Math.Max(1, count);
            return smallest < even * 0.3 ? (even * 0.3 - smallest) / even * 3 : 0;
        }

        private static MosaicLayout Place(Node node, double x, double y, double w, double h, CellBorderDto frame, List<ComposedCell> cells)
        {
            if (node.Photo != null)
            {
                int slot = cells.Count;
                cells.Add(new ComposedCell { Photo = node.Photo, X = x, Y = y, W = w, H = h, Frame = frame.Clone() });
                return MosaicLayout.Cell(slot);
            }
            double shareA = node.SideBySide
                ? node.A!.Aspect / (node.A.Aspect + node.B!.Aspect)
                : (1 / node.A!.Aspect) / (1 / node.A.Aspect + 1 / node.B!.Aspect);
            MosaicLayout first, second;
            if (node.SideBySide)
            {
                first = Place(node.A, x, y, w * shareA, h, frame, cells);
                second = Place(node.B!, x + w * shareA, y, w * (1 - shareA), h, frame, cells);
            }
            else
            {
                first = Place(node.A, x, y, w, h * shareA, frame, cells);
                second = Place(node.B!, x, y + h * shareA, w, h * (1 - shareA), frame, cells);
            }
            var cut = MosaicLayout.Cut(node.SideBySide, first, second);
            cut.Weights[0] = shareA;
            cut.Weights[1] = 1 - shareA;
            return cut;
        }

        private static Composition Finish(string recipe, List<ComposedCell> cells, MosaicLayout? mosaic, string color, string surface, CellBorderDto frame, double w, double h)
        {
            foreach (var cell in cells) AimAtSubject(cell);
            return new Composition { Recipe = recipe, Cells = cells, Mosaic = mosaic, CanvasColor = color, CanvasSurface = surface, CanvasFrame = frame, Width = w, Height = h };
        }

        /// <summary>The picture covers the cell, as Fill does, and shifts so its subject sits a little above the middle.</summary>
        private static void AimAtSubject(ComposedCell cell)
        {
            double innerW = Math.Max(1, cell.W - 2 * cell.Frame.Padding);
            double innerH = Math.Max(1, cell.H - 2 * cell.Frame.Padding);
            var cover = WindowShapes.CoverBox(cell.Geometry, innerW, innerH);
            double scale = Math.Max(cover.Width / cell.Photo.Width, cover.Height / cell.Photo.Height);
            double imageW = cell.Photo.Width * scale;
            double imageH = cell.Photo.Height * scale;
            cell.ImageWidth = imageW;
            cell.ImageHeight = imageH;

            double fx = cell.Photo.FocusX, fy = cell.Photo.FocusY;
            if (cell.Photo.Faces.Count > 0)
            {
                double left = cell.Photo.Faces.Min(f => f.X);
                double top = cell.Photo.Faces.Min(f => f.Y);
                double right = cell.Photo.Faces.Max(f => f.X + f.W);
                double bottom = cell.Photo.Faces.Max(f => f.Y + f.H);
                fx = (left + right) / 2;
                fy = (top + bottom) / 2;
            }
            double maxX = Math.Max(0, (imageW - innerW) / 2);
            double maxY = Math.Max(0, (imageH - innerH) / 2);
            cell.PanX = Math.Clamp(-(fx - 0.5) * imageW, -maxX, maxX);
            cell.PanY = Math.Clamp(-0.08 * innerH - (fy - 0.5) * imageH, -maxY, maxY);
        }

        // ---- Judging a version

        /// <summary>
        /// Faces matter most. A face a shape cuts into, or another cell covers, is what makes a random layout look careless.
        /// Then how much of each picture shows, whether any is enlarged past its detail, and how well prints cover the canvas.
        /// </summary>
        private static void Judge(Composition version, double rasterScale)
        {
            double faceScore = 1;
            int facesCut = 0;
            double shown = 0, sharp = 0;
            for (int i = 0; i < version.Cells.Count; i++)
            {
                var cell = version.Cells[i];
                foreach (var face in cell.Photo.Faces)
                {
                    double visible = VisibleShare(version, i, face);
                    if (visible < 0.85)
                    {
                        // A tiny face in a crowd costs less than a person's own.
                        double weight = Math.Min(1, Math.Sqrt(face.Area) * 6);
                        faceScore -= weight * (1 - visible);
                        if (face.Area > 0.0015) facesCut++;
                    }
                }
                double innerW = Math.Max(1, cell.W - 2 * cell.Frame.Padding);
                double innerH = Math.Max(1, cell.H - 2 * cell.Frame.Padding);
                shown += innerW * innerH / Math.Max(1, cell.ImageWidth * cell.ImageHeight);
                double enlarged = cell.ImageWidth * rasterScale / cell.Photo.Width;
                sharp += enlarged <= 1.3 ? 1 : Math.Max(0, 1 - (enlarged - 1.3));
            }
            int count = Math.Max(1, version.Cells.Count);
            double score = 4 * faceScore + shown / count + 0.5 * sharp / count;

            // The largest cell should hold the picture with the most in it.
            var largest = version.Cells.OrderByDescending(c => c.W * c.H).First();
            double most = version.Cells.Max(c => Presence(c.Photo));
            if (most > 0) score += 0.4 * Presence(largest.Photo) / most;
            score += 0.5 * CanvasCovered(version);

            version.Score = score;
            version.FacesCut = facesCut;
            version.PicturesBuried = CountBuried(version);
        }

        private static double VisibleShare(Composition version, int index, UnitRect face)
        {
            var cell = version.Cells[index];
            const int samples = 5;
            int visible = 0;
            for (int a = 0; a < samples; a++)
            {
                for (int b = 0; b < samples; b++)
                {
                    double u = face.X + face.W * (a + 0.5) / samples;
                    double v = face.Y + face.H * (b + 0.5) / samples;
                    var point = PointOnCanvas(cell, u, v);
                    if (point.X < 0 || point.Y < 0 || point.X > version.Width || point.Y > version.Height) continue;
                    if (!Shows(cell, point)) continue;
                    bool covered = false;
                    for (int above = index + 1; above < version.Cells.Count && !covered; above++)
                        covered = Shows(version.Cells[above], point);
                    if (!covered) visible++;
                }
            }
            return visible / (double)(samples * samples);
        }

        private static int CountBuried(Composition version)
        {
            const int samples = 8;
            const double mostlyHidden = 0.6;
            int buried = 0;
            for (int i = 0; i < version.Cells.Count - 1; i++)
            {
                var cell = version.Cells[i];
                double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
                double pad = cell.Frame.Padding;
                int hidden = 0;
                for (int a = 0; a < samples; a++)
                {
                    for (int b = 0; b < samples; b++)
                    {
                        double x = cell.X + pad + (cell.W - 2 * pad) * (a + 0.5) / samples;
                        double y = cell.Y + pad + (cell.H - 2 * pad) * (b + 0.5) / samples;
                        var point = Turn(x, y, cx, cy, cell.Rotation);
                        for (int above = i + 1; above < version.Cells.Count; above++)
                        {
                            if (!Shows(version.Cells[above], point)) continue;
                            hidden++;
                            break;
                        }
                    }
                }
                if (hidden > mostlyHidden * samples * samples) buried++;
            }
            return buried;
        }

        private static (double X, double Y) PointOnCanvas(ComposedCell cell, double u, double v)
        {
            double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
            double x = cx + cell.PanX + (u - 0.5) * cell.ImageWidth;
            double y = cy + cell.PanY + (v - 0.5) * cell.ImageHeight;
            return Turn(x, y, cx, cy, cell.Rotation);
        }

        private static (double X, double Y) Turn(double x, double y, double cx, double cy, double degrees)
        {
            if (degrees == 0) return (x, y);
            double radians = degrees * Math.PI / 180;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            return (cx + (x - cx) * cos - (y - cy) * sin, cy + (x - cx) * sin + (y - cy) * cos);
        }

        /// <summary>Whether this cell draws at this point of the canvas. Its gutter is clear unless it has a mat, which still hides nothing of a face behind it only when clear.</summary>
        private static bool Shows(ComposedCell cell, (double X, double Y) point)
        {
            double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
            var (x, y) = Turn(point.X, point.Y, cx, cy, -cell.Rotation);
            double pad = cell.Frame.Padding;
            double left = cell.X + pad, top = cell.Y + pad, right = cell.X + cell.W - pad, bottom = cell.Y + cell.H - pad;
            if (x < left || x > right || y < top || y > bottom) return false;
            var outline = WindowShapes.Outline(cell.Geometry);
            if (outline == null) return true;
            return Inside(outline, (x - left) / (right - left), (y - top) / (bottom - top));
        }

        private static bool Inside((double X, double Y)[] polygon, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Only scattered prints can leave bare canvas, and a loose pile looks better than a sparse one.</summary>
        private static double CanvasCovered(Composition version)
        {
            if (version.Cells.Any(c => !c.Floating)) return 1;
            const int steps = 16;
            int hit = 0;
            for (int a = 0; a < steps; a++)
            {
                for (int b = 0; b < steps; b++)
                {
                    var point = ((a + 0.5) / steps * version.Width, (b + 0.5) / steps * version.Height);
                    if (version.Cells.Any(c => Shows(c, point))) hit++;
                }
            }
            return hit / (double)(steps * steps);
        }

        /// <summary>How much a picture holds: its faces most, then its size.</summary>
        private static double Presence(PhotoFacts photo)
        {
            double faces = photo.Faces.Sum(f => Math.Sqrt(f.Area));
            double aspect = photo.Aspect;
            double comfortable = aspect > 2.2 || aspect < 0.45 ? 0.5 : 1;
            return (1 + 3 * Math.Min(1, faces)) * comfortable * (1 + Math.Log10(Math.Max(10, photo.Width * photo.Height)) / 10);
        }

        private static PhotoFacts Boldest(List<PhotoFacts> photos, Random random) => photos.OrderByDescending(Presence).Take(3).OrderBy(_ => random.Next()).First();

        /// <summary>A color from a picture, kept to its hue. Saturation and lightness are set so it stays quiet behind the pictures.</summary>
        public static string Tone(string hex, double saturation, double lightness)
        {
            var c = FrameColor.Parse(hex);
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double hue = 0;
            if (max > min)
            {
                double d = max - min;
                hue = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                hue /= 6;
            }
            double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            double p = 2 * lightness - q;
            byte Channel(double t)
            {
                t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
                double v = t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
                return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            }
            return $"#FF{Channel(hue + 1.0 / 3):X2}{Channel(hue):X2}{Channel(hue - 1.0 / 3):X2}";
        }
    }
}

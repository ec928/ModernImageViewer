using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    public enum HeroKind
    {
        Banner,
        Rectangle,
        Pair
    }

    /// <summary>
    /// A rectangular photo layout. A split is a stack of rows or a row of columns.
    /// Dragging the edge between two photos moves only that edge when both photos
    /// keep a rectangular shape. A photo that continues past the pointer keeps its full edge.
    /// </summary>
    public sealed class MosaicLayout
    {
        public bool Vertical { get; set; }
        public int Leaf { get; set; } = -1;
        public List<MosaicLayout> Children { get; } = new();
        public List<double> Weights { get; } = new();

        public bool IsLeaf => Leaf >= 0;

        public static MosaicLayout Cell(int index) => new() { Leaf = index };

        public static MosaicLayout Cut(bool vertical, params MosaicLayout[] children)
        {
            var node = new MosaicLayout { Vertical = vertical, Leaf = -1 };
            foreach (var child in children)
            {
                node.Children.Add(child);
                node.Weights.Add(1);
            }
            return node;
        }

        public MosaicLayout Clone()
        {
            var copy = new MosaicLayout { Vertical = Vertical, Leaf = Leaf };
            for (int i = 0; i < Children.Count; i++)
            {
                copy.Children.Add(Children[i].Clone());
                copy.Weights.Add(Weights[i]);
            }
            return copy;
        }

        /// <summary>Photos actually placed. Seven or more stop at nine.</summary>
        public static int ShownCount(int count)
        {
            if (count >= 7) return Math.Min(count, 9);
            return count;
        }

        public static int ModeCount(int count)
        {
            return ShownCount(count) switch
            {
                <= 1 => 1,
                2 => 2,
                3 => 2,
                4 => 3,
                5 => 3,
                6 => 2,
                7 => 3,
                8 => 2,
                _ => 3
            };
        }

        public static string ModeTitle(int count, int mode)
        {
            int shown = ShownCount(count);
            if (mode < 0 || mode >= ModeCount(shown)) mode = 0;
            if (shown == 2)
                return mode == 0 ? "Side-by-Side (Columns)" : "Top-and-Bottom (Rows)";
            if (shown == 3)
                return mode == 0 ? "3 Rows" : "3 Columns";
            if (shown == 4)
            {
                return mode switch
                {
                    0 => "2×2",
                    1 => "4 Rows",
                    _ => "4 Columns"
                };
            }
            if (shown == 5)
            {
                return mode switch
                {
                    0 => "3 over 2",
                    1 => "2 over 3",
                    _ => "2 / 2 / 1"
                };
            }
            if (shown == 6)
                return mode == 0 ? "3 over 3" : "2 / 2 / 2";
            if (shown == 7)
            {
                return mode switch
                {
                    0 => "2 / 3 / 2",
                    1 => "4 over 3",
                    _ => "3 over 4"
                };
            }
            if (shown == 8)
                return mode == 0 ? "4 + 4" : "3 + 3 + 2";
            return mode switch
            {
                0 => "3×3",
                1 => "5 over 4",
                _ => "4 over 5"
            };
        }

        public static MosaicLayout Create(int count, int mode)
        {
            if (count <= 1) return Cell(0);
            int shown = ShownCount(count);
            if (mode < 0 || mode >= ModeCount(shown)) mode = 0;

            if (shown == 2)
                return mode == 0 ? Cut(true, Cell(0), Cell(1)) : Cut(false, Cell(0), Cell(1));
            if (shown == 3)
                return mode == 0
                    ? Cut(false, Cell(0), Cell(1), Cell(2))
                    : Cut(true, Cell(0), Cell(1), Cell(2));
            if (shown == 4)
            {
                if (mode == 1) return Cut(false, Cell(0), Cell(1), Cell(2), Cell(3));
                if (mode == 2) return Cut(true, Cell(0), Cell(1), Cell(2), Cell(3));
                return Cut(true, Cut(false, Cell(0), Cell(2)), Cut(false, Cell(1), Cell(3)));
            }
            if (shown == 5)
            {
                if (mode == 0) return Cut(false, Row(0, 3), Row(3, 2));
                if (mode == 1) return Cut(false, Row(0, 2), Row(2, 3));
                return Cut(false, Row(0, 2), Row(2, 2), Cell(4));
            }
            if (shown == 6)
                return mode == 0
                    ? Cut(false, Row(0, 3), Row(3, 3))
                    : Cut(false, Row(0, 2), Row(2, 2), Row(4, 2));
            if (shown == 7)
            {
                if (mode == 1) return Cut(false, Row(0, 4), Row(4, 3));
                if (mode == 2) return Cut(false, Row(0, 3), Row(3, 4));
                return Split(false, 1, 1.5, 1, Row(0, 2), Row(2, 3), Row(5, 2));
            }
            if (shown == 8)
                return mode == 0
                    ? Cut(false, Row(0, 4), Row(4, 4))
                    : Cut(false, Row(0, 3), Row(3, 3), Row(6, 2));
            if (mode == 1) return Cut(false, Row(0, 5), Row(5, 4));
            if (mode == 2) return Cut(false, Row(0, 4), Row(4, 5));
            return Cut(false, Row(0, 3), Row(3, 3), Row(6, 3));
        }

        public static HeroKind[] HeroKinds(int count)
        {
            int shown = ShownCount(count);
            if (shown < 2) return Array.Empty<HeroKind>();
            if (shown < 3) return new[] { HeroKind.Banner };
            if (shown == 4 || shown == 5 || shown == 6 || shown == 7 || shown >= 9)
                return new[] { HeroKind.Banner, HeroKind.Rectangle, HeroKind.Pair };
            return new[] { HeroKind.Banner, HeroKind.Pair };
        }

        /// <summary>Banner position for the first press. 0 is the top, 3 is the left.</summary>
        public static int DefaultBannerPosition(double width, double height)
        {
            return height > width ? 3 : 0;
        }

        public static int HeroPositions(int count, HeroKind kind)
        {
            int shown = ShownCount(count);
            if (kind == HeroKind.Rectangle && (shown == 5 || shown == 7)) return 2;
            return 4;
        }

        public static string HeroTitle(HeroKind kind)
        {
            return kind switch
            {
                HeroKind.Banner => "Banner",
                HeroKind.Rectangle => "Rectangle",
                _ => "Pair"
            };
        }

        /// <summary>
        /// Photo 0 stays the large one. A pair keeps photos 0 and 1 large.
        /// A banner walks clockwise: top, right, bottom, left.
        /// </summary>
        public static MosaicLayout CreateHero(int count, HeroKind kind, int position)
        {
            int shown = ShownCount(count);
            if (shown < 2) return Cell(0);
            var kinds = HeroKinds(shown);
            if (Array.IndexOf(kinds, kind) < 0) kind = kinds[0];
            int positions = HeroPositions(shown, kind);
            if (position < 0) position = 0;
            position %= positions;

            if (kind == HeroKind.Rectangle) return RectangleHero(shown, position);
            if (kind == HeroKind.Pair) return PairHero(shown, position);
            bool vertical = position == 1 || position == 3;
            return BandHero(shown, vertical, atStart: position == 0 || position == 3, pair: false);
        }

        private static MosaicLayout Split(bool vertical, double w0, double w1, MosaicLayout a, MosaicLayout b)
        {
            var node = Cut(vertical, a, b);
            node.Weights[0] = w0;
            node.Weights[1] = w1;
            return node;
        }

        private static MosaicLayout Split(bool vertical, double w0, double w1, double w2, MosaicLayout a, MosaicLayout b, MosaicLayout c)
        {
            var node = Cut(vertical, a, b, c);
            node.Weights[0] = w0;
            node.Weights[1] = w1;
            node.Weights[2] = w2;
            return node;
        }

        private static MosaicLayout Row(int start, int count)
        {
            if (count <= 1) return Cell(start);
            var cells = new MosaicLayout[count];
            for (int i = 0; i < count; i++)
                cells[i] = Cell(start + i);
            return Cut(true, cells);
        }

        private static MosaicLayout Stack(int start, int count)
        {
            if (count <= 1) return Cell(start);
            var cells = new MosaicLayout[count];
            for (int i = 0; i < count; i++)
                cells[i] = Cell(start + i);
            return Cut(false, cells);
        }

        private static MosaicLayout BandHero(int shown, bool vertical, bool atStart, bool pair)
        {
            int featured = pair ? 2 : 1;
            int smalls = shown - featured;
            BandShare(smalls, pair, out double heroW, out double restW);
            MosaicLayout feature = pair ? (vertical ? Stack(0, 2) : Row(0, 2)) : Cell(0);
            MosaicLayout rest = Pack(featured, smalls, rows: !vertical);
            if (atStart) return Split(vertical, heroW, restW, feature, rest);
            return Split(vertical, restW, heroW, rest, feature);
        }

        private static MosaicLayout PairHero(int shown, int position)
        {
            bool vertical = position == 1 || position == 3;
            bool atStart = position == 0 || position == 3;
            return BandHero(shown, vertical, atStart, pair: true);
        }

        private static void BandShare(int smalls, bool pair, out double heroW, out double restW)
        {
            if (!pair)
            {
                if (smalls <= 4) { heroW = 3; restW = 2; }
                else { heroW = 1; restW = 1; }
                return;
            }
            if (smalls <= 1) { heroW = 3; restW = 1; }
            else if (smalls <= 3) { heroW = 3; restW = 2; }
            else { heroW = 1; restW = 1; }
        }

        /// <summary>One strip until five photos, then several lines with at most four photos each.</summary>
        private static MosaicLayout Pack(int start, int count, bool rows)
        {
            if (count <= 1) return Cell(start);
            if (count <= 4) return rows ? Row(start, count) : Stack(start, count);

            int lines = (count + 3) / 4;
            int baseCount = count / lines;
            int extra = count % lines;
            var parts = new MosaicLayout[lines];
            int index = start;
            for (int i = 0; i < lines; i++)
            {
                int n = baseCount + (i < extra ? 1 : 0);
                parts[i] = rows ? Row(index, n) : Stack(index, n);
                index += n;
            }
            return rows ? Cut(false, parts) : Cut(true, parts);
        }

        private static MosaicLayout RectangleHero(int shown, int position)
        {
            if (shown == 4) return Rectangle4(position);
            if (shown == 5) return position == 0
                ? Cut(true, Cell(0), Cut(false, Row(1, 2), Row(3, 2)))
                : Cut(true, Cut(false, Row(1, 2), Row(3, 2)), Cell(0));
            if (shown == 6) return Rectangle6(position);
            if (shown == 7) return position == 0
                ? Split(true, 2, 3, Cell(0), Cut(false, Row(1, 3), Row(4, 3)))
                : Split(true, 3, 2, Cut(false, Row(1, 3), Row(4, 3)), Cell(0));
            return Rectangle9(position);
        }

        private static MosaicLayout Rectangle4(int position)
        {
            if (position == 0) return Split(false, 2, 1, Split(true, 2, 1, Cell(0), Stack(1, 2)), Cell(3));
            if (position == 1) return Split(false, 2, 1, Split(true, 1, 2, Stack(1, 2), Cell(0)), Cell(3));
            if (position == 2) return Split(false, 1, 2, Cell(1), Split(true, 1, 2, Stack(2, 2), Cell(0)));
            return Split(false, 1, 2, Cell(1), Split(true, 2, 1, Cell(0), Stack(2, 2)));
        }

        private static MosaicLayout Rectangle6(int position)
        {
            if (position == 0) return Split(false, 2, 1, Split(true, 2, 1, Cell(0), Stack(1, 2)), Row(3, 3));
            if (position == 1) return Split(false, 2, 1, Split(true, 1, 2, Stack(1, 2), Cell(0)), Row(3, 3));
            if (position == 2) return Split(false, 1, 2, Row(1, 3), Split(true, 1, 2, Stack(4, 2), Cell(0)));
            return Split(false, 1, 2, Row(1, 3), Split(true, 2, 1, Cell(0), Stack(4, 2)));
        }

        private static MosaicLayout Rectangle9(int position)
        {
            if (position == 0)
                return Split(false, 2, 1, Split(true, 1, 1, Cell(0), Cut(false, Row(1, 2), Row(3, 2))), Row(5, 4));
            if (position == 1)
                return Split(false, 2, 1, Split(true, 1, 1, Cut(false, Row(1, 2), Row(3, 2)), Cell(0)), Row(5, 4));
            if (position == 2)
                return Split(false, 1, 2, Row(1, 4), Split(true, 1, 1, Cut(false, Row(5, 2), Row(7, 2)), Cell(0)));
            return Split(false, 1, 2, Row(1, 4), Split(true, 1, 1, Cell(0), Cut(false, Row(5, 2), Row(7, 2))));
        }
    }

    public readonly struct MosaicBox
    {
        public MosaicBox(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public double X { get; }
        public double Y { get; }
        public double W { get; }
        public double H { get; }
    }

    public readonly struct BoundaryHit
    {
        public bool NeedsFlip { get; init; }
        public bool Whole { get; init; }
        public int Lane { get; init; }
        public int BeforeLeaf { get; init; }
        public int AfterLeaf { get; init; }
        public double SegStart { get; init; }
        public double SegEnd { get; init; }
    }

    public static class MosaicMath
    {
        public static Dictionary<int, MosaicBox> Boxes(MosaicLayout root, double width, double height)
        {
            var boxes = new Dictionary<int, MosaicBox>();
            Walk(root, 0, 0, width, height, boxes);
            return boxes;
        }

        public static BoundaryHit ClassifyBoundary(
            MosaicLayout node,
            int boundary,
            double cutLength,
            double pointerAlong,
            double junctionSlop = ViewerMath.ThroughLineJunctionSlop,
            double alignTolerance = ViewerMath.ThroughLineAlignTolerance)
        {
            var whole = new BoundaryHit
            {
                Whole = true,
                NeedsFlip = false,
                Lane = -1,
                BeforeLeaf = -1,
                AfterLeaf = -1,
                SegStart = 0,
                SegEnd = cutLength
            };
            if (node == null || node.IsLeaf || boundary < 0 || boundary >= node.Children.Count - 1)
                return whole;
            if (double.IsNaN(cutLength) || double.IsNaN(pointerAlong) || cutLength <= 0)
                return whole;

            var before = node.Children[boundary];
            var after = node.Children[boundary + 1];
            if (before.Vertical == node.Vertical || after.Vertical == node.Vertical)
                return whole;
            if (!LanesAligned(before, after, cutLength, alignTolerance))
                return whole;

            int lanes = before.Children.Count;
            double weightSum = before.Weights.Sum();
            if (weightSum <= 0) return whole;

            double acc = 0;
            int lane = -1;
            double segStart = 0;
            double segEnd = cutLength;
            for (int k = 0; k < lanes; k++)
            {
                double len = before.Weights[k] / weightSum * cutLength;
                double edge = acc + len;
                if (k < lanes - 1 && Math.Abs(pointerAlong - edge) <= junctionSlop)
                    return whole;
                if (lane < 0 && pointerAlong < edge)
                {
                    lane = k;
                    segStart = acc;
                    segEnd = edge;
                }
                acc = edge;
            }
            if (lane < 0)
            {
                lane = lanes - 1;
                segStart = acc - (before.Weights[lanes - 1] / weightSum * cutLength);
                segEnd = cutLength;
            }

            return new BoundaryHit
            {
                NeedsFlip = true,
                Whole = false,
                Lane = lane,
                BeforeLeaf = SoleLeaf(before.Children[lane]),
                AfterLeaf = SoleLeaf(after.Children[lane]),
                SegStart = segStart,
                SegEnd = segEnd
            };
        }

        /// <summary>
        /// Move the boundary. When <paramref name="needsFlip"/> is set, the two bands
        /// are turned so the lane under the pointer has its own edge and the other lanes stay.
        /// </summary>
        public static MosaicLayout DragSegment(
            MosaicLayout origin,
            IReadOnlyList<int> path,
            int boundary,
            bool needsFlip,
            int lane,
            double bandPixels,
            double delta,
            out bool flipped,
            out int[] resizePath,
            out int resizeBoundary)
        {
            var root = origin.Clone();
            flipped = false;
            var node = At(root, path);
            if (needsFlip && node != null && CanFlip(node, boundary, lane))
            {
                Flip(node, boundary);
                flipped = true;
                resizePath = Append(path, boundary, lane);
                resizeBoundary = 0;
                node = At(root, resizePath);
                boundary = 0;
            }
            else
            {
                resizePath = path as int[] ?? path.ToArray();
                resizeBoundary = boundary;
            }

            if (node != null)
                Apply(node, boundary, bandPixels, delta);
            return root;
        }

        private static void Walk(MosaicLayout node, double x, double y, double w, double h, Dictionary<int, MosaicBox> boxes)
        {
            if (node.IsLeaf)
            {
                boxes[node.Leaf] = new MosaicBox(x, y, w, h);
                return;
            }
            double sum = node.Weights.Sum();
            if (sum <= 0) sum = node.Children.Count;
            double offset = 0;
            for (int i = 0; i < node.Children.Count; i++)
            {
                double share = node.Weights[i] / sum;
                if (node.Vertical)
                {
                    double width = w * share;
                    Walk(node.Children[i], x + offset, y, width, h, boxes);
                    offset += width;
                }
                else
                {
                    double height = h * share;
                    Walk(node.Children[i], x, y + offset, w, height, boxes);
                    offset += height;
                }
            }
        }

        private static bool LanesAligned(MosaicLayout before, MosaicLayout after, double cutLength, double tolerance)
        {
            if (before == null || after == null || before.IsLeaf || after.IsLeaf)
                return false;
            if (before.Vertical != after.Vertical)
                return false;
            if (before.Children.Count < 2 || before.Children.Count != after.Children.Count)
                return false;
            if (before.Weights.Count != before.Children.Count || after.Weights.Count != after.Children.Count)
                return false;

            double sumA = before.Weights.Sum();
            double sumB = after.Weights.Sum();
            if (sumA <= 0 || sumB <= 0 || cutLength <= 0) return false;

            double accA = 0;
            double accB = 0;
            for (int i = 0; i < before.Children.Count - 1; i++)
            {
                accA += before.Weights[i];
                accB += after.Weights[i];
                double posA = accA / sumA * cutLength;
                double posB = accB / sumB * cutLength;
                if (Math.Abs(posA - posB) > tolerance) return false;
            }
            return true;
        }

        private static bool CanFlip(MosaicLayout node, int boundary, int lane)
        {
            if (node.IsLeaf || boundary < 0 || boundary >= node.Children.Count - 1 || lane < 0)
                return false;
            var before = node.Children[boundary];
            var after = node.Children[boundary + 1];
            if (before.IsLeaf || after.IsLeaf || before.Vertical == node.Vertical || before.Vertical != after.Vertical)
                return false;
            return lane < before.Children.Count && before.Children.Count == after.Children.Count;
        }

        private static int SoleLeaf(MosaicLayout node)
        {
            return node != null && node.IsLeaf ? node.Leaf : -1;
        }

        private static void Flip(MosaicLayout node, int boundary)
        {
            var before = node.Children[boundary];
            var after = node.Children[boundary + 1];
            double weightBefore = node.Weights[boundary];
            double weightAfter = node.Weights[boundary + 1];
            var flipped = new MosaicLayout { Vertical = !node.Vertical, Leaf = -1 };
            for (int lane = 0; lane < before.Children.Count; lane++)
            {
                var pair = new MosaicLayout { Vertical = node.Vertical, Leaf = -1 };
                pair.Children.Add(before.Children[lane]);
                pair.Children.Add(after.Children[lane]);
                pair.Weights.Add(weightBefore);
                pair.Weights.Add(weightAfter);
                flipped.Children.Add(pair);
                flipped.Weights.Add(before.Weights[lane]);
            }

            node.Children.RemoveAt(boundary + 1);
            node.Children.RemoveAt(boundary);
            node.Children.Insert(boundary, flipped);
            node.Weights.RemoveAt(boundary + 1);
            node.Weights.RemoveAt(boundary);
            node.Weights.Insert(boundary, weightBefore + weightAfter);
        }

        private static void Apply(MosaicLayout node, int boundary, double bandPixels, double delta)
        {
            if (node == null || boundary < 0 || boundary >= node.Weights.Count - 1) return;
            double w0 = node.Weights[boundary];
            double w1 = node.Weights[boundary + 1];
            double sum = w0 + w1;
            if (sum <= 0 || bandPixels <= 0 || double.IsNaN(delta)) return;
            double start0 = w0 / sum * bandPixels;
            double start1 = bandPixels - start0;
            var (next0, next1) = ViewerMath.ResizePair(start0, start1, delta);
            double nextSum = next0 + next1;
            if (nextSum <= 0) return;
            node.Weights[boundary] = sum * next0 / nextSum;
            node.Weights[boundary + 1] = sum * next1 / nextSum;
        }

        public static MosaicLayout? At(MosaicLayout? root, IReadOnlyList<int>? path)
        {
            if (root == null) return null;
            var node = root;
            if (path == null) return node;
            foreach (int index in path)
            {
                if (index < 0 || index >= node.Children.Count) return null;
                node = node.Children[index];
            }
            return node;
        }

        private static int[] Append(IReadOnlyList<int> path, int boundary, int lane)
        {
            var next = new int[path.Count + 2];
            for (int i = 0; i < path.Count; i++)
                next[i] = path[i];
            next[path.Count] = boundary;
            next[path.Count + 1] = lane;
            return next;
        }
    }
}

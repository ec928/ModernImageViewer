using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
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

        public static MosaicLayout Create(int count, int mode)
        {
            if (count <= 1) return Cell(0);
            if (count == 2)
                return mode == 0 ? Cut(true, Cell(0), Cell(1)) : Cut(false, Cell(0), Cell(1));
            if (count == 3)
            {
                if (mode == 0)
                {
                    var node = Cut(false, Cell(0), Cut(true, Cell(1), Cell(2)));
                    node.Weights[0] = 1.1;
                    node.Weights[1] = 1.0;
                    return node;
                }
                if (mode == 1)
                {
                    var node = Cut(true, Cell(0), Cut(false, Cell(1), Cell(2)));
                    node.Weights[0] = 1.2;
                    node.Weights[1] = 1.0;
                    return node;
                }
                if (mode == 2) return Cut(false, Cell(0), Cell(1), Cell(2));
                return Cut(true, Cell(0), Cell(1), Cell(2));
            }
            if (count == 4)
            {
                if (mode == 1)
                    return Cut(false, Cell(0), Cell(1), Cell(2), Cell(3));
                if (mode == 2)
                    return Cut(true, Cell(0), Cell(1), Cell(2), Cell(3));
                return Cut(true, Cut(false, Cell(0), Cell(2)), Cut(false, Cell(1), Cell(3)));
            }
            if (count <= 6)
            {
                if (mode == 0)
                {
                    int colsTop = (count + 1) / 2;
                    return Cut(false, Row(0, colsTop), Row(colsTop, count - colsTop));
                }
                int perRow = (count + 2) / 3;
                var rows = new List<MosaicLayout>();
                for (int index = 0; index < count;)
                {
                    int n = Math.Min(perRow, count - index);
                    rows.Add(Row(index, n));
                    index += n;
                }
                return Cut(false, rows.ToArray());
            }

            int max = Math.Min(count, 9);
            var gridRows = new List<MosaicLayout>();
            for (int index = 0; index < max;)
            {
                int n = Math.Min(3, max - index);
                gridRows.Add(Row(index, n));
                index += n;
            }
            return Cut(false, gridRows.ToArray());
        }

        private static MosaicLayout Row(int start, int count)
        {
            if (count <= 1) return Cell(start);
            var cells = new MosaicLayout[count];
            for (int i = 0; i < count; i++)
                cells[i] = Cell(start + i);
            return Cut(true, cells);
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

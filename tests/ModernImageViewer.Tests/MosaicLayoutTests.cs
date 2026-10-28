using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class MosaicLayoutTests
    {
        // ---- Dragging the edge between photos

        [Fact]
        public void Five_photos_left_edge_of_the_top_band_moves_only_that_pair()
        {
            var origin = Rows(2, 2, 1);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 1000, pointerAlong: 200);
            Assert.True(hit.NeedsFlip);
            Assert.Equal(0, hit.BeforeLeaf);
            Assert.Equal(2, hit.AfterLeaf);

            var dragged = MosaicMath.DragSegment(
                origin, path: new int[0], boundary: 0, needsFlip: true, lane: hit.Lane,
                bandPixels: 600, delta: 60,
                out bool flipped, out int[] resizePath, out int resizeBoundary);

            Assert.True(flipped);
            Assert.Equal(0, resizeBoundary);
            Assert.Equal(new[] { 0, 0 }, resizePath);

            var boxes = MosaicMath.Boxes(dragged, 1000, 900);
            Assert.Equal(360, boxes[0].H, 3);
            Assert.Equal(300, boxes[1].H, 3);
            Assert.Equal(240, boxes[2].H, 3);
            Assert.Equal(300, boxes[3].H, 3);
            Assert.Equal(300, boxes[4].H, 3);
            Assert.Equal(600, boxes[4].Y, 3);
        }

        [Fact]
        public void Five_photos_edge_above_the_full_width_photo_moves_the_whole_line()
        {
            var origin = Rows(2, 2, 1);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 1, cutLength: 1000, pointerAlong: 200);
            Assert.False(hit.NeedsFlip);
            Assert.True(hit.Whole);

            var dragged = MosaicMath.DragSegment(
                origin, path: new int[0], boundary: 1, needsFlip: false, lane: -1,
                bandPixels: 600, delta: 60,
                out bool flipped, out _, out _);

            Assert.False(flipped);
            var boxes = MosaicMath.Boxes(dragged, 1000, 900);
            Assert.Equal(300, boxes[0].H, 3);
            Assert.Equal(300, boxes[1].H, 3);
            Assert.Equal(360, boxes[2].H, 3);
            Assert.Equal(360, boxes[3].H, 3);
            Assert.Equal(240, boxes[4].H, 3);
        }

        [Fact]
        public void Five_photos_cross_keeps_the_whole_top_band()
        {
            var origin = Rows(2, 2, 1);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 1000, pointerAlong: 500);
            Assert.False(hit.NeedsFlip);
            Assert.True(hit.Whole);
        }

        [Fact]
        public void Unaligned_columns_do_not_split_the_band()
        {
            var origin = Rows(2, 2, 1);
            origin.Children[0].Weights[0] = 1;
            origin.Children[0].Weights[1] = 1;
            origin.Children[1].Weights[0] = 3;
            origin.Children[1].Weights[1] = 1;

            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 1000, pointerAlong: 200);
            Assert.False(hit.NeedsFlip);
        }

        [Fact]
        public void Four_photos_top_of_the_vertical_line_moves_only_the_top_pair()
        {
            // Two columns, photos 0 and 2 on the left, 1 and 3 on the right.
            var origin = MosaicLayout.Cut(true,
                MosaicLayout.Cut(false, MosaicLayout.Cell(0), MosaicLayout.Cell(2)),
                MosaicLayout.Cut(false, MosaicLayout.Cell(1), MosaicLayout.Cell(3)));
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 800, pointerAlong: 80);
            Assert.True(hit.NeedsFlip);
            Assert.Equal(0, hit.BeforeLeaf);
            Assert.Equal(1, hit.AfterLeaf);

            var dragged = MosaicMath.DragSegment(
                origin, path: new int[0], boundary: 0, needsFlip: true, lane: hit.Lane,
                bandPixels: 1000, delta: 80,
                out _, out _, out _);

            var boxes = MosaicMath.Boxes(dragged, 1000, 800);
            Assert.Equal(580, boxes[0].W, 3);
            Assert.Equal(420, boxes[1].W, 3);
            Assert.Equal(500, boxes[2].W, 3);
            Assert.Equal(500, boxes[3].W, 3);
        }

        [Fact]
        public void Six_photos_middle_column_edge_stays_in_that_column()
        {
            var origin = Rows(3, 3);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 900, pointerAlong: 450);
            Assert.True(hit.NeedsFlip);
            Assert.Equal(1, hit.BeforeLeaf);
            Assert.Equal(4, hit.AfterLeaf);

            var dragged = MosaicMath.DragSegment(
                origin, path: new int[0], boundary: 0, needsFlip: true, lane: hit.Lane,
                bandPixels: 600, delta: 40,
                out _, out _, out _);

            var boxes = MosaicMath.Boxes(dragged, 900, 600);
            Assert.Equal(300, boxes[0].H, 3);
            Assert.Equal(340, boxes[1].H, 3);
            Assert.Equal(300, boxes[2].H, 3);
            Assert.Equal(300, boxes[3].H, 3);
            Assert.Equal(260, boxes[4].H, 3);
            Assert.Equal(300, boxes[5].H, 3);
        }

        [Fact]
        public void Short_last_row_keeps_one_edge_and_aligned_rows_split()
        {
            var origin = Rows(3, 3, 2);
            var top = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 900, pointerAlong: 100);
            Assert.True(top.NeedsFlip);
            Assert.Equal(0, top.BeforeLeaf);
            Assert.Equal(3, top.AfterLeaf);
            Assert.InRange(top.SegEnd, 290, 310);

            var aboveShort = MosaicMath.ClassifyBoundary(origin, boundary: 1, cutLength: 900, pointerAlong: 100);
            Assert.False(aboveShort.NeedsFlip);
            Assert.True(aboveShort.Whole);
        }

        [Fact]
        public void Rows_of_different_lengths_move_the_whole_edge()
        {
            var between = MosaicMath.ClassifyBoundary(Rows(4, 3), boundary: 0, cutLength: 840, pointerAlong: 100);
            Assert.True(between.Whole);
        }

        [Fact]
        public void Three_photos_edge_under_the_wide_photo_stays_whole()
        {
            var origin = MosaicLayout.CreateHeroLines(3, HeroKind.Banner, 0, new[] { 2 }, rows: true);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 800, pointerAlong: 100);
            Assert.False(hit.NeedsFlip);
            Assert.True(hit.Whole);
        }

        // ---- Rows and columns

        [Fact]
        public void Photos_stop_at_sixteen()
        {
            Assert.Equal(16, MosaicLayout.MaxShown);
            Assert.Equal(1, MosaicLayout.ShownCount(1));
            Assert.Equal(9, MosaicLayout.ShownCount(9));
            Assert.Equal(12, MosaicLayout.ShownCount(12));
            Assert.Equal(16, MosaicLayout.ShownCount(16));
            Assert.Equal(16, MosaicLayout.ShownCount(23));

            var counts = MosaicLayout.LineCounts(23, 4);
            Assert.Equal(new[] { 4, 4, 4, 4 }, counts);
            Assert.Equal(16, MosaicMath.Boxes(MosaicLayout.FromCounts(counts, rows: true), 1600, 900).Count);
        }

        [Fact]
        public void A_grid_opens_close_to_square()
        {
            Assert.Equal(1, MosaicLayout.DefaultLines(2));
            Assert.Equal(2, MosaicLayout.DefaultLines(4));
            Assert.Equal(2, MosaicLayout.DefaultLines(6));
            Assert.Equal(3, MosaicLayout.DefaultLines(9));
            Assert.Equal(4, MosaicLayout.DefaultLines(16));
            Assert.Equal(4, MosaicLayout.DefaultLines(40));
        }

        [Fact]
        public void Photos_spread_evenly_with_the_longer_lines_first()
        {
            Assert.Equal(new[] { 3, 2 }, MosaicLayout.LineCounts(5, 2));
            Assert.Equal(new[] { 3, 2, 2 }, MosaicLayout.LineCounts(7, 3));
            Assert.Equal(new[] { 4, 3, 3, 3, 3 }, MosaicLayout.LineCounts(16, 5));
            Assert.Equal(Enumerable.Repeat(1, 16).ToArray(), MosaicLayout.LineCounts(16, 16));
            Assert.Equal(new[] { 5 }, MosaicLayout.LineCounts(5, 0));
            Assert.Equal(new[] { 1, 1, 1, 1, 1 }, MosaicLayout.LineCounts(5, 9));
        }

        [Fact]
        public void Rows_lay_photos_side_by_side_and_columns_stack_them()
        {
            var rows = MosaicMath.Boxes(MosaicLayout.FromCounts(new[] { 3, 2 }, rows: true), 600, 200);
            Near(rows[0], 0, 0, 200, 100);
            Near(rows[2], 400, 0, 200, 100);
            Near(rows[3], 0, 100, 300, 100);
            Near(rows[4], 300, 100, 300, 100);

            var columns = MosaicMath.Boxes(MosaicLayout.FromCounts(new[] { 3, 2 }, rows: false), 200, 600);
            Near(columns[0], 0, 0, 100, 200);
            Near(columns[2], 0, 400, 100, 200);
            Near(columns[3], 100, 0, 100, 300);
            Near(columns[4], 100, 300, 100, 300);
        }

        [Fact]
        public void Turning_rows_a_quarter_gives_the_same_columns()
        {
            var turned = MosaicMath.Boxes(MosaicLayout.Transpose(MosaicLayout.FromCounts(new[] { 3, 2, 2 }, rows: true)), 900, 600);
            var columns = MosaicMath.Boxes(MosaicLayout.FromCounts(new[] { 3, 2, 2 }, rows: false), 900, 600);
            for (int i = 0; i < 7; i++)
                Near(turned[i], columns[i].X, columns[i].Y, columns[i].W, columns[i].H);
        }

        public static IEnumerable<object[]> PhotoCounts()
        {
            for (int count = 2; count <= MosaicLayout.MaxShown; count++)
                yield return new object[] { count };
        }

        [Theory]
        [MemberData(nameof(PhotoCounts))]
        public void Every_row_and_column_count_covers_the_canvas_once(int count)
        {
            for (int lines = 1; lines <= count; lines++)
            {
                foreach (bool rows in new[] { true, false })
                {
                    var layout = MosaicLayout.FromCounts(MosaicLayout.LineCounts(count, lines), rows);
                    Covers(MosaicMath.Boxes(layout, 1600, 900), count, $"{lines} {(rows ? "rows" : "columns")}");
                }
            }
        }

        // ---- Moving a photo between lines

        [Fact]
        public void One_more_in_a_line_takes_it_from_the_last_line_that_can_spare_one()
        {
            Assert.Equal(new[] { 3, 2, 3, 1 }, MosaicLayout.MovePhoto(new[] { 3, 2, 2, 2 }, 2, 1));
            Assert.Equal(new[] { 4, 2, 1, 1 }, MosaicLayout.MovePhoto(new[] { 3, 2, 2, 1 }, 0, 1));
            Assert.Null(MosaicLayout.MovePhoto(new[] { 4, 1, 1 }, 0, 1));
        }

        [Fact]
        public void One_fewer_in_a_line_gives_it_to_the_last_line()
        {
            Assert.Equal(new[] { 2, 2, 3 }, MosaicLayout.MovePhoto(new[] { 3, 2, 2 }, 0, -1));
            Assert.Equal(new[] { 3, 3, 1 }, MosaicLayout.MovePhoto(new[] { 3, 2, 2 }, 2, -1));
            Assert.Null(MosaicLayout.MovePhoto(new[] { 3, 1 }, 1, -1));
            Assert.Null(MosaicLayout.MovePhoto(new[] { 5 }, 0, 1));
        }

        // ---- Heroes

        [Fact]
        public void Hero_kinds_follow_the_photo_count()
        {
            Assert.Equal(new[] { HeroKind.Banner }, MosaicLayout.HeroKinds(2));
            Assert.Equal(new[] { HeroKind.Banner, HeroKind.Pair }, MosaicLayout.HeroKinds(3));
            Assert.Equal(new[] { HeroKind.Banner, HeroKind.Rectangle, HeroKind.Pair }, MosaicLayout.HeroKinds(4));
            Assert.DoesNotContain(HeroKind.Rectangle, MosaicLayout.HeroKinds(8));
            Assert.Contains(HeroKind.Rectangle, MosaicLayout.HeroKinds(9));
            for (int count = 10; count <= 16; count++)
                Assert.Equal(new[] { HeroKind.Banner, HeroKind.Pair }, MosaicLayout.HeroKinds(count));
            Assert.Equal("Banner", MosaicLayout.HeroTitle(HeroKind.Banner));
            Assert.Equal(0, MosaicLayout.DefaultBannerPosition(1920, 1080));
            Assert.Equal(3, MosaicLayout.DefaultBannerPosition(1080, 1920));
            Assert.Equal(4, MosaicLayout.HeroPositions(2, HeroKind.Banner));
            Assert.Equal(2, MosaicLayout.HeroPositions(5, HeroKind.Rectangle));
            Assert.Equal(2, MosaicLayout.HeroPositions(7, HeroKind.Rectangle));
            Assert.Equal(4, MosaicLayout.HeroPositions(6, HeroKind.Rectangle));
            Assert.Equal(4, MosaicLayout.HeroPositions(3, HeroKind.Pair));
            Assert.Equal(1, MosaicLayout.HeroFeatured(HeroKind.Banner));
            Assert.Equal(2, MosaicLayout.HeroFeatured(HeroKind.Pair));
        }

        [Fact]
        public void A_banner_takes_sixty_percent_with_up_to_four_photos_beside_it()
        {
            var top = Banner(4, 0, new[] { 3 }, rows: true, 300, 100);
            Near(top[0], 0, 0, 300, 60);
            Near(top[1], 0, 60, 100, 40);
            Near(top[2], 100, 60, 100, 40);
            Near(top[3], 200, 60, 100, 40);

            var left = Banner(4, 3, new[] { 3 }, rows: false, 100, 300);
            Near(left[0], 0, 0, 60, 300);
            Near(left[1], 60, 0, 40, 100);
            Near(left[2], 60, 100, 40, 100);
            Near(left[3], 60, 200, 40, 100);
        }

        [Fact]
        public void A_banner_halves_the_canvas_when_more_photos_sit_beside_it()
        {
            var top = Banner(9, 0, new[] { 4, 4 }, rows: true, 400, 400);
            Near(top[0], 0, 0, 400, 200);
            Near(top[1], 0, 200, 100, 100);
            Near(top[4], 300, 200, 100, 100);
            Near(top[5], 0, 300, 100, 100);
            Near(top[8], 300, 300, 100, 100);
        }

        [Fact]
        public void The_photos_beside_a_banner_follow_the_rows_and_columns()
        {
            // Banner on top, the other six in three columns of 3, 2, 1.
            var boxes = Banner(7, 0, new[] { 3, 2, 1 }, rows: false, 300, 500);
            Near(boxes[0], 0, 0, 300, 250);
            Near(boxes[1], 0, 250, 100, 250.0 / 3);
            Near(boxes[3], 0, 250 + 2 * 250.0 / 3, 100, 250.0 / 3);
            Near(boxes[4], 100, 250, 100, 125);
            Near(boxes[6], 200, 250, 100, 250);
        }

        [Fact]
        public void Rotate_moves_the_large_area_and_keeps_the_same_photos_large()
        {
            var right = Banner(2, 1, new[] { 1 }, rows: true, 100, 100);
            Near(right[0], 40, 0, 60, 100);
            Near(right[1], 0, 0, 40, 100);

            var bottom = Banner(2, 2, new[] { 1 }, rows: true, 100, 100);
            Near(bottom[0], 0, 40, 100, 60);
            Near(bottom[1], 0, 0, 100, 40);

            var pairTop = Pair(3, 0, new[] { 1 }, rows: true, 400, 400);
            Near(pairTop[0], 0, 0, 200, 300);
            Near(pairTop[1], 200, 0, 200, 300);
            Near(pairTop[2], 0, 300, 400, 100);

            var pairRight = Pair(3, 1, new[] { 1 }, rows: true, 400, 400);
            Near(pairRight[0], 100, 0, 300, 200);
            Near(pairRight[1], 100, 200, 300, 200);
            Near(pairRight[2], 0, 0, 100, 400);

            var corner = Rectangle(4, 2, 300, 300);
            Near(corner[0], 100, 100, 200, 200);
            var opposite = Rectangle(4, 3, 300, 300);
            Near(opposite[0], 0, 100, 200, 200);
        }

        [Fact]
        public void Rectangle_heroes_put_the_first_photo_in_a_corner_block()
        {
            var five = Rectangle(5, 0, 400, 200);
            Near(five[0], 0, 0, 200, 200);
            Near(five[1], 200, 0, 100, 100);
            Near(five[4], 300, 100, 100, 100);
            Near(Rectangle(5, 1, 400, 200)[0], 200, 0, 200, 200);

            var six = Rectangle(6, 0, 300, 300);
            Near(six[0], 0, 0, 200, 200);
            Near(six[2], 200, 100, 100, 100);
            Near(six[5], 200, 200, 100, 100);

            var seven = Rectangle(7, 0, 500, 200);
            Near(seven[0], 0, 0, 200, 200);
            Near(seven[3], 400, 0, 100, 100);
            Near(seven[6], 400, 100, 100, 100);

            var nine = Rectangle(9, 0, 400, 300);
            Near(nine[0], 0, 0, 200, 200);
            Near(nine[4], 300, 100, 100, 100);
            Near(nine[5], 0, 200, 100, 100);
            Near(nine[8], 300, 200, 100, 100);
        }

        [Fact]
        public void A_count_without_a_rectangle_gets_the_even_grid()
        {
            Covers(Rectangle(3, 0, 300, 100), 3, "rectangle 3", 300, 100);
            Covers(Rectangle(12, 0, 1600, 900), 12, "rectangle 12");
        }

        [Theory]
        [MemberData(nameof(PhotoCounts))]
        public void Every_hero_covers_the_canvas_once(int count)
        {
            foreach (var kind in MosaicLayout.HeroKinds(count))
            {
                int positions = MosaicLayout.HeroPositions(count, kind);
                for (int position = 0; position < positions; position++)
                {
                    if (kind == HeroKind.Rectangle)
                    {
                        Covers(Rectangle(count, position, 1600, 900), count, $"rectangle {position}");
                        continue;
                    }
                    int rest = count - MosaicLayout.HeroFeatured(kind);
                    for (int lines = 1; lines <= rest; lines++)
                    {
                        foreach (bool rows in new[] { true, false })
                        {
                            var layout = MosaicLayout.CreateHeroLines(count, kind, position, MosaicLayout.LineCounts(rest, lines), rows);
                            Covers(MosaicMath.Boxes(layout, 1600, 900), count, $"{kind} {position}, {lines} {(rows ? "rows" : "columns")}");
                        }
                    }
                }
            }
        }

        // ---- Helpers

        private static MosaicLayout Rows(params int[] counts) => MosaicLayout.FromCounts(counts, rows: true);

        private static Dictionary<int, MosaicBox> Banner(int count, int position, int[] rest, bool rows, double width, double height)
        {
            return MosaicMath.Boxes(MosaicLayout.CreateHeroLines(count, HeroKind.Banner, position, rest, rows), width, height);
        }

        private static Dictionary<int, MosaicBox> Pair(int count, int position, int[] rest, bool rows, double width, double height)
        {
            return MosaicMath.Boxes(MosaicLayout.CreateHeroLines(count, HeroKind.Pair, position, rest, rows), width, height);
        }

        private static Dictionary<int, MosaicBox> Rectangle(int count, int position, double width, double height)
        {
            return MosaicMath.Boxes(MosaicLayout.CreateRectangleHero(count, position), width, height);
        }

        /// <summary>Every photo is placed once, inside the canvas, and together they fill it.</summary>
        private static void Covers(Dictionary<int, MosaicBox> boxes, int count, string name, double width = 1600, double height = 900)
        {
            Assert.Equal(count, boxes.Count);
            double area = 0;
            for (int i = 0; i < count; i++)
            {
                Assert.True(boxes.ContainsKey(i), name);
                Assert.True(boxes[i].W > 1, name);
                Assert.True(boxes[i].H > 1, name);
                Assert.InRange(boxes[i].X, -0.5, width);
                Assert.InRange(boxes[i].Y, -0.5, height);
                Assert.InRange(boxes[i].X + boxes[i].W, -0.5, width + 0.5);
                Assert.InRange(boxes[i].Y + boxes[i].H, -0.5, height + 0.5);
                area += boxes[i].W * boxes[i].H;
            }
            Assert.Equal(width * height, area, 0);
        }

        private static void Near(MosaicBox box, double x, double y, double w, double h)
        {
            Assert.Equal(x, box.X, 2);
            Assert.Equal(y, box.Y, 2);
            Assert.Equal(w, box.W, 2);
            Assert.Equal(h, box.H, 2);
        }
    }
}

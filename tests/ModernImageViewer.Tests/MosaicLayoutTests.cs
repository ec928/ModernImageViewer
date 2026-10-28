using Xunit;

namespace ModernImageViewer.Tests
{
    public class MosaicLayoutTests
    {
        [Fact]
        public void Five_photos_left_edge_of_the_top_band_moves_only_that_pair()
        {
            var origin = MosaicLayout.Create(5, mode: 2);
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
            var origin = MosaicLayout.Create(5, mode: 2);
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
            var origin = MosaicLayout.Create(5, mode: 2);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 1000, pointerAlong: 500);
            Assert.False(hit.NeedsFlip);
            Assert.True(hit.Whole);
        }

        [Fact]
        public void Unaligned_columns_do_not_split_the_band()
        {
            var origin = MosaicLayout.Create(5, mode: 2);
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
            var origin = MosaicLayout.Create(4, mode: 0);
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
            var origin = MosaicLayout.Create(6, mode: 0);
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
            var origin = MosaicLayout.Create(8, mode: 1);
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
        public void Three_photos_edge_under_the_wide_photo_stays_whole()
        {
            var origin = MosaicLayout.Create(3, mode: 0);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 800, pointerAlong: 100);
            Assert.False(hit.NeedsFlip);
            Assert.True(hit.Whole);
        }

        [Fact]
        public void Four_photo_heroes_put_the_first_photo_at_sixty_percent()
        {
            var top = Placed(4, 1, 300, 100);
            Near(top[0], 0, 0, 300, 60);
            Near(top[1], 0, 60, 100, 40);
            Near(top[2], 100, 60, 100, 40);
            Near(top[3], 200, 60, 100, 40);

            var left = Placed(4, 2, 100, 300);
            Near(left[0], 0, 0, 60, 300);
            Near(left[1], 60, 0, 40, 100);
            Near(left[2], 60, 100, 40, 100);
            Near(left[3], 60, 200, 40, 100);
        }

        [Fact]
        public void Five_photo_rows_and_heroes()
        {
            var threeOverTwo = Placed(5, 0, 600, 200);
            Near(threeOverTwo[0], 0, 0, 200, 100);
            Near(threeOverTwo[2], 400, 0, 200, 100);
            Near(threeOverTwo[3], 0, 100, 300, 100);
            Near(threeOverTwo[4], 300, 100, 300, 100);

            var twoOverThree = Placed(5, 1, 600, 200);
            Near(twoOverThree[0], 0, 0, 300, 100);
            Near(twoOverThree[1], 300, 0, 300, 100);
            Near(twoOverThree[2], 0, 100, 200, 100);
            Near(twoOverThree[4], 400, 100, 200, 100);

            var heroLeft = Placed(5, 3, 400, 200);
            Near(heroLeft[0], 0, 0, 200, 200);
            Near(heroLeft[1], 200, 0, 100, 100);
            Near(heroLeft[2], 300, 0, 100, 100);
            Near(heroLeft[3], 200, 100, 100, 100);
            Near(heroLeft[4], 300, 100, 100, 100);

            var heroTop = Placed(5, 4, 400, 200);
            Near(heroTop[0], 0, 0, 400, 100);
            Near(heroTop[1], 0, 100, 100, 100);
            Near(heroTop[4], 300, 100, 100, 100);
        }

        [Fact]
        public void Six_photo_hero_is_the_top_left_block()
        {
            var boxes = Placed(6, 2, 300, 300);
            Near(boxes[0], 0, 0, 200, 200);
            Near(boxes[1], 200, 0, 100, 100);
            Near(boxes[2], 200, 100, 100, 100);
            Near(boxes[3], 0, 200, 100, 100);
            Near(boxes[4], 100, 200, 100, 100);
            Near(boxes[5], 200, 200, 100, 100);
        }

        [Fact]
        public void Seven_photos_match_in_area_and_the_hero_is_on_the_left()
        {
            var even = Placed(7, 0, 210, 350);
            Near(even[0], 0, 0, 105, 100);
            Near(even[1], 105, 0, 105, 100);
            Near(even[2], 0, 100, 70, 150);
            Near(even[4], 140, 100, 70, 150);
            Near(even[5], 0, 250, 105, 100);
            Near(even[6], 105, 250, 105, 100);
            Assert.Equal(even[0].W * even[0].H, even[2].W * even[2].H, 0);

            var hero = Placed(7, 1, 500, 200);
            Near(hero[0], 0, 0, 200, 200);
            Near(hero[1], 200, 0, 100, 100);
            Near(hero[3], 400, 0, 100, 100);
            Near(hero[4], 200, 100, 100, 100);
            Near(hero[6], 400, 100, 100, 100);
        }

        [Fact]
        public void Eight_photos_open_as_two_rows_of_four()
        {
            var rows = Placed(8, 0, 400, 200);
            Near(rows[0], 0, 0, 100, 100);
            Near(rows[3], 300, 0, 100, 100);
            Near(rows[4], 0, 100, 100, 100);
            Near(rows[7], 300, 100, 100, 100);

            var grid = Placed(8, 1, 600, 300);
            Near(grid[0], 0, 0, 200, 100);
            Near(grid[5], 400, 100, 200, 100);
            Near(grid[6], 0, 200, 300, 100);
            Near(grid[7], 300, 200, 300, 100);

            var hero = Placed(8, 2, 840, 400);
            Near(hero[0], 0, 0, 840, 200);
            Near(hero[1], 0, 200, 210, 100);
            Near(hero[4], 630, 200, 210, 100);
            Near(hero[5], 0, 300, 280, 100);
            Near(hero[7], 560, 300, 280, 100);
        }

        [Fact]
        public void Nine_photo_heroes()
        {
            var corner = Placed(9, 1, 400, 300);
            Near(corner[0], 0, 0, 200, 200);
            Near(corner[1], 200, 0, 100, 100);
            Near(corner[2], 300, 0, 100, 100);
            Near(corner[3], 200, 100, 100, 100);
            Near(corner[4], 300, 100, 100, 100);
            Near(corner[5], 0, 200, 100, 100);
            Near(corner[8], 300, 200, 100, 100);

            var top = Placed(9, 2, 400, 400);
            Near(top[0], 0, 0, 400, 200);
            Near(top[1], 0, 200, 100, 100);
            Near(top[4], 300, 200, 100, 100);
            Near(top[5], 0, 300, 100, 100);
            Near(top[8], 300, 300, 100, 100);
        }

        [Fact]
        public void Extra_photos_past_nine_keep_the_nine_photo_layout()
        {
            var nine = Placed(9, 0, 300, 300);
            var twelve = Placed(12, 0, 300, 300);
            Assert.Equal(9, twelve.Count);
            Near(twelve[0], nine[0].X, nine[0].Y, nine[0].W, nine[0].H);
            Near(twelve[8], nine[8].X, nine[8].Y, nine[8].W, nine[8].H);
            Assert.Equal(3, MosaicLayout.ModeCount(12));
            Assert.Equal("3×3", MosaicLayout.ModeTitle(12, 0));
            Assert.Equal("Hero", MosaicLayout.ModeTitle(9, 1));
            Assert.Equal("Hero Top", MosaicLayout.ModeTitle(4, 1));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void Every_layout_covers_the_canvas_once(int count)
        {
            for (int mode = 0; mode < MosaicLayout.ModeCount(count); mode++)
            {
                var boxes = Placed(count, mode, 1600, 900);
                Assert.Equal(count, boxes.Count);
                double area = 0;
                for (int i = 0; i < count; i++)
                {
                    Assert.True(boxes[i].W > 1, MosaicLayout.ModeTitle(count, mode));
                    Assert.True(boxes[i].H > 1, MosaicLayout.ModeTitle(count, mode));
                    Assert.InRange(boxes[i].X, -0.5, 1600);
                    Assert.InRange(boxes[i].Y, -0.5, 900);
                    Assert.InRange(boxes[i].X + boxes[i].W, -0.5, 1600.5);
                    Assert.InRange(boxes[i].Y + boxes[i].H, -0.5, 900.5);
                    area += boxes[i].W * boxes[i].H;
                }
                Assert.Equal(1600d * 900d, area, 0);
            }
        }

        private static Dictionary<int, MosaicBox> Placed(int count, int mode, double width, double height)
        {
            return MosaicMath.Boxes(MosaicLayout.Create(count, mode), width, height);
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

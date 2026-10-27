using Xunit;

namespace ModernImageViewer.Tests
{
    public class MosaicLayoutTests
    {
        [Fact]
        public void Five_photos_left_edge_of_the_top_band_moves_only_that_pair()
        {
            var origin = MosaicLayout.Create(5, mode: 1);
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
            var origin = MosaicLayout.Create(5, mode: 1);
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
            var origin = MosaicLayout.Create(5, mode: 1);
            var hit = MosaicMath.ClassifyBoundary(origin, boundary: 0, cutLength: 1000, pointerAlong: 500);
            Assert.False(hit.NeedsFlip);
            Assert.True(hit.Whole);
        }

        [Fact]
        public void Unaligned_columns_do_not_split_the_band()
        {
            var origin = MosaicLayout.Create(5, mode: 1);
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
            var origin = MosaicLayout.Create(8, mode: 0);
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
    }
}

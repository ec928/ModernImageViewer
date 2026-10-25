using Xunit;

namespace ModernImageViewer.Tests
{
    public class ViewerMathTests
    {
        [Fact]
        public void FitZoom_fills_the_shorter_axis()
        {
            float zoom = ViewerMath.CalculateFitZoom(1000, 500, 2000, 2000, dpiScale: 1f, minZoom: 0.05f, maxZoom: 8f);
            Assert.Equal(0.25f, zoom, precision: 4);
        }

        [Fact]
        public void FitZoom_returns_one_when_viewport_is_empty()
        {
            Assert.Equal(1.0f, ViewerMath.CalculateFitZoom(0, 500, 100, 100, 1f, 0.1f, 8f));
        }

        [Fact]
        public void FitZoom_can_go_below_the_wheel_floor()
        {
            float zoom = ViewerMath.CalculateFitZoom(1000, 800, 20000, 10000, dpiScale: 1f, minZoom: ViewerMath.AbsoluteFitMinZoom, maxZoom: 20f);
            Assert.Equal(0.05f, zoom, precision: 4);
        }

        [Fact]
        public void ScrollZoomFloor_stays_at_the_wheel_floor_for_an_ordinary_fit()
        {
            Assert.Equal(0.1f, ViewerMath.ScrollZoomFloor(0.4f), precision: 4);
        }

        [Fact]
        public void ScrollZoomFloor_drops_only_for_a_fit_below_the_wheel_floor()
        {
            Assert.Equal(0.05f, ViewerMath.ScrollZoomFloor(0.05f), precision: 4);
        }

        [Fact]
        public void NextDecodeEdge_at_fit_is_the_screen_and_not_the_file()
        {
            float fit = 1600f / 6000f;
            int edge = ViewerMath.NextDecodeEdge(6000, 4000, fit, fit, haveEdge: 1200, absoluteMax: 16384);
            Assert.Equal(1600, edge);
        }

        [Fact]
        public void NextDecodeEdge_past_fit_jumps_to_the_file()
        {
            float fit = 1600f / 6000f;
            int edge = ViewerMath.NextDecodeEdge(6000, 4000, zoom: 0.5f, fit, haveEdge: 1600, absoluteMax: 16384);
            Assert.Equal(6000, edge);
        }

        [Fact]
        public void NextDecodeEdge_is_zero_when_the_bitmap_already_covers_the_view()
        {
            float fit = 1600f / 6000f;
            int edge = ViewerMath.NextDecodeEdge(6000, 4000, fit, fit, haveEdge: 1600, absoluteMax: 16384);
            Assert.Equal(0, edge);
        }

        [Fact]
        public void NextDecodeEdge_growing_the_window_at_fit_asks_for_the_new_screen()
        {
            float fit = 2400f / 6000f;
            int edge = ViewerMath.NextDecodeEdge(6000, 4000, fit, fit, haveEdge: 1600, absoluteMax: 16384);
            Assert.Equal(2400, edge);
        }

        [Fact]
        public void ClampPan_stops_at_the_scroll_extent()
        {
            var (x, y) = ViewerMath.ClampPan(0, 0, 5000, -5000, horizOffset: 10, vertOffset: 10, scrollWidth: 100, scrollHeight: 100);
            Assert.Equal(10, x);
            Assert.Equal(-(100 - 10), y);
        }

        [Fact]
        public void WheelZoom_in_increases_zoom_and_stays_clamped()
        {
            var (zoom, _, _) = ViewerMath.CalculateWheelZoom(
                targetZoomBase: 1f, currentNativeZoom: 1f, minZoom: 0.1f, maxZoom: 8f, wheelDelta: 120,
                pointerViewportX: 50, pointerViewportY: 50,
                currentOffsetX: 0, currentOffsetY: 0,
                logicalW: 100, logicalH: 100, dpiScale: 1f,
                viewportW: 100, viewportH: 100);
            Assert.Equal(1.15f, zoom, precision: 3);
        }

        [Fact]
        public void DragPan_clamps_to_scrollable_range()
        {
            var (x, y) = ViewerMath.CalculateDragPan(
                0, 0, pointerX: 10, pointerY: 10, lastPointerX: 0, lastPointerY: 0,
                horizontalOffset: 0, scrollableWidth: 50,
                verticalOffset: 0, scrollableHeight: 50);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }
    }
}

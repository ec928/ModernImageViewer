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

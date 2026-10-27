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
        public void TileCursor_right_continues_onto_the_next_row()
        {
            int next = ViewerMath.MoveTileCursor(5, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Right);
            Assert.Equal(6, next);
        }

        [Fact]
        public void TileCursor_left_continues_onto_the_previous_row()
        {
            int next = ViewerMath.MoveTileCursor(6, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Left);
            Assert.Equal(5, next);
        }

        [Fact]
        public void TileCursor_left_of_the_first_tile_stays()
        {
            int next = ViewerMath.MoveTileCursor(0, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Left);
            Assert.Equal(0, next);
        }

        [Fact]
        public void TileCursor_right_of_the_last_tile_stays()
        {
            int next = ViewerMath.MoveTileCursor(19, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Right);
            Assert.Equal(19, next);
        }

        [Fact]
        public void TileCursor_up_on_the_first_row_stays()
        {
            int next = ViewerMath.MoveTileCursor(4, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Up);
            Assert.Equal(4, next);
        }

        [Fact]
        public void TileCursor_down_into_a_short_row_stays()
        {
            // 20 tiles, 6 columns. Index 16 is row 2, column 4. The last row has only two tiles.
            int next = ViewerMath.MoveTileCursor(16, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Down);
            Assert.Equal(16, next);
        }

        [Fact]
        public void TileCursor_down_moves_one_row_in_the_same_column()
        {
            int next = ViewerMath.MoveTileCursor(4, 20, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.Down);
            Assert.Equal(10, next);
        }

        [Fact]
        public void TileCursor_page_down_skips_a_screen_in_the_same_column()
        {
            int next = ViewerMath.MoveTileCursor(4, 40, columns: 6, pageRows: 3, visibleOrigin: 0, ViewerMath.TileCursorKey.PageDown);
            Assert.Equal(22, next);
        }

        [Fact]
        public void TileCursor_page_down_lands_on_the_last_tile_in_that_column()
        {
            int next = ViewerMath.MoveTileCursor(4, 20, columns: 6, pageRows: 4, visibleOrigin: 0, ViewerMath.TileCursorKey.PageDown);
            Assert.Equal(16, next);
        }

        [Fact]
        public void TileCursor_page_up_stops_on_the_first_row()
        {
            int next = ViewerMath.MoveTileCursor(16, 20, columns: 6, pageRows: 4, visibleOrigin: 0, ViewerMath.TileCursorKey.PageUp);
            Assert.Equal(4, next);
        }

        [Fact]
        public void TileCursor_home_and_end()
        {
            Assert.Equal(0, ViewerMath.MoveTileCursor(8, 20, 6, 3, 12, ViewerMath.TileCursorKey.Home));
            Assert.Equal(19, ViewerMath.MoveTileCursor(-1, 20, 6, 3, 12, ViewerMath.TileCursorKey.End));
        }

        [Fact]
        public void TileCursor_first_key_starts_at_the_visible_tile_then_moves()
        {
            int next = ViewerMath.MoveTileCursor(-1, 40, columns: 6, pageRows: 3, visibleOrigin: 12, ViewerMath.TileCursorKey.Right);
            Assert.Equal(13, next);
        }

        [Fact]
        public void TileCursor_first_up_on_the_top_row_lands_on_the_visible_tile()
        {
            int next = ViewerMath.MoveTileCursor(-1, 40, columns: 6, pageRows: 3, visibleOrigin: 2, ViewerMath.TileCursorKey.Up);
            Assert.Equal(2, next);
        }

        [Fact]
        public void TileCursor_empty_folder_has_nowhere_to_go()
        {
            Assert.Equal(-1, ViewerMath.MoveTileCursor(-1, 0, 6, 3, 0, ViewerMath.TileCursorKey.Right));
        }

        [Fact]
        public void VisibleTileOrigin_uses_the_row_at_the_top_of_the_screen()
        {
            Assert.Equal(0, ViewerMath.VisibleTileOrigin(0, paddingTop: 48, itemHeight: 164, columns: 6, count: 40));
            Assert.Equal(6, ViewerMath.VisibleTileOrigin(48 + 164, 48, 164, 6, 40));
            Assert.Equal(12, ViewerMath.VisibleTileOrigin(48 + 164 * 2 + 10, 48, 164, 6, 40));
        }

        [Fact]
        public void ScrollOffsetToReveal_moves_only_when_the_tile_is_cut_off()
        {
            Assert.Equal(0, ViewerMath.ScrollOffsetToReveal(0, viewport: 400, itemStart: 10, itemSize: 164));
            Assert.Equal(50, ViewerMath.ScrollOffsetToReveal(100, 400, itemStart: 50, itemSize: 164));
            Assert.Equal(264, ViewerMath.ScrollOffsetToReveal(0, 400, itemStart: 500, itemSize: 164));
        }

        [Fact]
        public void Letterbox_click_above_a_wide_photo_is_outside_the_picture()
        {
            // A wide photo in a tall view leaves empty bars above and below, as in the solo view.
            Assert.True(ViewerMath.IsPointerOverLetterbox(400, 40, viewW: 1000, viewH: 800, displayedW: 1000, displayedH: 400));
            Assert.False(ViewerMath.IsPointerOverLetterbox(400, 400, viewW: 1000, viewH: 800, displayedW: 1000, displayedH: 400));
            Assert.False(ViewerMath.IsPointerOverLetterbox(10, 10, viewW: 1000, viewH: 800, displayedW: 2000, displayedH: 1200));
        }

        [Fact]
        public void ResizePair_keeps_the_total_and_a_minimum()
        {
            var (a, b) = ViewerMath.ResizePair(500, 500, 40);
            Assert.Equal(1000, a + b);
            Assert.Equal(540, a);

            var (small, rest) = ViewerMath.ResizePair(500, 500, -10000);
            Assert.Equal(1000, small + rest);
            Assert.Equal(150, small);

            var (sameA, sameB) = ViewerMath.ResizePair(10, 10, 5);
            Assert.Equal(20, sameA + sameB);
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

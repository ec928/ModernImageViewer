using System;

namespace ModernImageViewer
{
    public static partial class ViewerMath
    {
        /// <summary>Lowest zoom the wheel can reach on an ordinary photo.</summary>
        public const float WheelMinZoom = 0.1f;

        /// <summary>Lowest zoom a fit is allowed to use. Below the wheel floor, so a very wide image can fit.</summary>
        public const float AbsoluteFitMinZoom = 0.01f;

        /// <summary>Long edge of the fast preview bitmap.</summary>
        public const int PreviewLongEdge = 1200;

        /// <summary>
        /// Wheel floor for this image. An ordinary fit stays at <see cref="WheelMinZoom"/>.
        /// A fit below that floor becomes the floor, so the whole image can be shown and the wheel cannot shrink it further.
        /// </summary>
        public static float ScrollZoomFloor(float fitZoom, float wheelMinZoom = WheelMinZoom)
        {
            if (float.IsNaN(fitZoom) || fitZoom <= 0) return wheelMinZoom;
            return Math.Min(wheelMinZoom, fitZoom);
        }

        /// <summary>
        /// Bitmap long edge that makes this zoom sharp. Zoom 1 is one image pixel per physical pixel.
        /// </summary>
        public static int RequiredLongEdge(double nativeW, double nativeH, float zoom, int absoluteMax)
        {
            double nativeLong = Math.Max(nativeW, nativeH);
            if (nativeLong <= 1 || zoom <= 0 || absoluteMax < 1) return 1;
            double need = Math.Min(nativeLong, nativeLong * zoom);
            need = Math.Min(need, absoluteMax);
            return Math.Max(1, (int)Math.Ceiling(need - 0.001));
        }

        /// <summary>
        /// Next decode long edge, or 0 when <paramref name="haveEdge"/> is already sharp enough.
        /// At fit, the next edge is only what the view needs. Zoomed past fit, it is the file, capped.
        /// </summary>
        public static int NextDecodeEdge(double nativeW, double nativeH, float zoom, float fitZoom, int haveEdge, int absoluteMax)
        {
            int need = RequiredLongEdge(nativeW, nativeH, zoom, absoluteMax);
            if (haveEdge >= need) return 0;

            bool atFit = fitZoom > 0 && zoom <= fitZoom * 1.02f + 0.001f;
            if (atFit) return need;

            double nativeLong = Math.Max(nativeW, nativeH);
            int full = (int)Math.Ceiling(Math.Min(nativeLong, absoluteMax));
            return Math.Max(need, Math.Max(1, full));
        }

        public static float CalculateFitZoom(double viewW, double viewH, double logicalW, double logicalH, float dpiScale, float minZoom, float maxZoom)
        {
            if (viewW <= 0 || viewH <= 0 || logicalW <= 0 || logicalH <= 0) return 1.0f;
            double dipsW = logicalW / dpiScale;
            double dipsH = logicalH / dpiScale;
            if (dipsW <= 0 || dipsH <= 0) return 1.0f;

            double fitW = viewW / dipsW;
            double fitH = viewH / dipsH;
            return Math.Clamp((float)Math.Min(fitW, fitH), minZoom, maxZoom);
        }

        public static (double PanX, double PanY) ClampPan(double currentPanX, double currentPanY, double deltaX, double deltaY, double horizOffset, double vertOffset, double scrollWidth, double scrollHeight)
        {
            double tempX = Math.Clamp(currentPanX + deltaX, -(scrollWidth - horizOffset), horizOffset);
            double tempY = Math.Clamp(currentPanY + deltaY, -(scrollHeight - vertOffset), vertOffset);
            return (tempX, tempY);
        }

        public static (float NewZoom, double NewOffsetX, double NewOffsetY) CalculateWheelZoom(
            float targetZoomBase, float currentNativeZoom, float minZoom, float maxZoom, int wheelDelta,
            double pointerViewportX, double pointerViewportY,
            double currentOffsetX, double currentOffsetY,
            double logicalW, double logicalH, float dpiScale,
            double viewportW, double viewportH)
        {
            float zoomDelta = wheelDelta > 0 ? 1.15f : 0.85f;
            float newTargetZoom = Math.Clamp(targetZoomBase * zoomDelta, minZoom, maxZoom);

            double currentDisplayedW = (logicalW / dpiScale) * currentNativeZoom;
            double currentDisplayedH = (logicalH / dpiScale) * currentNativeZoom;

            double currentBlankOffsetX = currentDisplayedW < viewportW ? (viewportW - currentDisplayedW) / 2.0 : 0;
            double currentBlankOffsetY = currentDisplayedH < viewportH ? (viewportH - currentDisplayedH) / 2.0 : 0;

            double absoluteX = currentOffsetX + pointerViewportX;
            double absoluteY = currentOffsetY + pointerViewportY;

            double logicalX = (absoluteX - currentBlankOffsetX) / currentNativeZoom;
            double logicalY = (absoluteY - currentBlankOffsetY) / currentNativeZoom;

            double newDisplayedW = (logicalW / dpiScale) * newTargetZoom;
            double newDisplayedH = (logicalH / dpiScale) * newTargetZoom;

            double newBlankOffsetX = newDisplayedW < viewportW ? (viewportW - newDisplayedW) / 2.0 : 0;
            double newBlankOffsetY = newDisplayedH < viewportH ? (viewportH - newDisplayedH) / 2.0 : 0;

            double newOffsetX = (logicalX * newTargetZoom) + newBlankOffsetX - pointerViewportX;
            double newOffsetY = (logicalY * newTargetZoom) + newBlankOffsetY - pointerViewportY;

            return (newTargetZoom, newOffsetX, newOffsetY);
        }

        public static (double TempPanX, double TempPanY) CalculateDragPan(
            double currentTempPanX, double currentTempPanY,
            double pointerX, double pointerY, double lastPointerX, double lastPointerY,
            double horizontalOffset, double scrollableWidth,
            double verticalOffset, double scrollableHeight)
        {
            double tempX = currentTempPanX + (pointerX - lastPointerX);
            double tempY = currentTempPanY + (pointerY - lastPointerY);

            double maxPanX = horizontalOffset;
            double minPanX = -(scrollableWidth - horizontalOffset);
            tempX = Math.Clamp(tempX, minPanX, maxPanX);

            double maxPanY = verticalOffset;
            double minPanY = -(scrollableHeight - verticalOffset);
            tempY = Math.Clamp(tempY, minPanY, maxPanY);

            return (tempX, tempY);
        }

        public enum TileCursorKey
        {
            Left,
            Right,
            Up,
            Down,
            PageUp,
            PageDown,
            Home,
            End
        }

        /// <summary>
        /// Tile under the keyboard cursor after one key.
        /// An index below zero means nothing is current yet: a direction key starts at
        /// <paramref name="visibleOrigin"/> and then moves. Home and End ignore that origin.
        /// Up and down stay in the column. A short last row has no cell past its last tile, so the cursor stays.
        /// Page up and page down move by <paramref name="pageRows"/> rows in that same column.
        /// </summary>
        public static int MoveTileCursor(int index, int count, int columns, int pageRows, int visibleOrigin, TileCursorKey key)
        {
            if (count <= 0) return -1;
            if (columns < 1) columns = 1;
            if (pageRows < 1) pageRows = 1;
            if (key == TileCursorKey.Home) return 0;
            if (key == TileCursorKey.End) return count - 1;

            if (index < 0 || index >= count)
            {
                index = visibleOrigin;
                if (index < 0 || index >= count) index = 0;
            }

            switch (key)
            {
                case TileCursorKey.Left:
                    return index == 0 ? 0 : index - 1;
                case TileCursorKey.Right:
                    return index == count - 1 ? index : index + 1;
                case TileCursorKey.Up:
                    return index < columns ? index : index - columns;
                case TileCursorKey.Down:
                    {
                        int next = index + columns;
                        return next >= count ? index : next;
                    }
                case TileCursorKey.PageUp:
                    return MoveTileRow(index, count, columns, -pageRows);
                case TileCursorKey.PageDown:
                    return MoveTileRow(index, count, columns, pageRows);
                default:
                    return index;
            }
        }

        /// <summary>Top-left tile currently on screen. The grid's top padding scrolls away with the tiles.</summary>
        public static int VisibleTileOrigin(double verticalOffset, double paddingTop, double itemHeight, int columns, int count)
        {
            if (count <= 0) return -1;
            if (columns < 1) columns = 1;
            if (itemHeight <= 0) itemHeight = 1;
            double y = verticalOffset - paddingTop;
            if (double.IsNaN(y) || y < 0) y = 0;
            int row = (int)(y / itemHeight);
            if (row < 0) row = 0;
            int origin = row * columns;
            if (origin >= count) origin = count - 1;
            return origin;
        }

        /// <summary>Pointer within this distance of the cross still drags the whole through-line.</summary>
        public const double ThroughLineJunctionSlop = 22;

        /// <summary>Perpendicular cuts closer than this count as one shared edge.</summary>
        public const double ThroughLineAlignTolerance = 8;

        /// <summary>Movement along the resize axis that commits a one-pair drag.</summary>
        public const double ThroughLineUnlockSlop = 2;

        /// <summary>
        /// True when the pointer is in the empty margin around a centered picture.
        /// A picture larger than the view covers the view, so the pointer is on it.
        /// </summary>
        public static bool IsPointerOverLetterbox(double pointerX, double pointerY, double viewW, double viewH, double displayedW, double displayedH)
        {
            if (double.IsNaN(pointerX) || double.IsNaN(pointerY) || viewW <= 0 || viewH <= 0) return true;
            if (double.IsNaN(displayedW) || double.IsNaN(displayedH) || displayedW <= 0 || displayedH <= 0) return true;

            double startX = displayedW < viewW ? (viewW - displayedW) / 2.0 : 0;
            double endX = displayedW < viewW ? startX + displayedW : viewW;
            double startY = displayedH < viewH ? (viewH - displayedH) / 2.0 : 0;
            double endY = displayedH < viewH ? startY + displayedH : viewH;
            return pointerX < startX || pointerX > endX || pointerY < startY || pointerY > endY;
        }

        /// <summary>Resize a pair of star tracks. The two results always add up to the start total.</summary>
        public static (double A, double B) ResizePair(double startA, double startB, double delta)
        {
            double total = startA + startB;
            if (total <= 0 || double.IsNaN(total) || double.IsNaN(delta)) return (startA, startB);
            double min = Math.Max(80.0, total * 0.15);
            if (min > total - min) min = total / 2.0;
            double a = Math.Clamp(startA + delta, min, total - min);
            return (a, total - a);
        }

        /// <summary>Scroll offset that brings the tile on screen and does not move when it is already fully visible.</summary>
        public static double ScrollOffsetToReveal(double offset, double viewport, double itemStart, double itemSize)
        {
            if (itemSize <= 0 || viewport <= 0 || double.IsNaN(offset)) return offset;
            if (offset < 0) offset = 0;
            if (itemSize >= viewport) return Math.Max(0, itemStart);
            double itemEnd = itemStart + itemSize;
            if (itemStart < offset) return Math.Max(0, itemStart);
            if (itemEnd > offset + viewport) return Math.Max(0, itemEnd - viewport);
            return offset;
        }

        private static int MoveTileRow(int index, int count, int columns, int rowDelta)
        {
            int col = index % columns;
            int row = index / columns;
            int maxRow = (count - 1) / columns;
            int targetRow = row + rowDelta;
            if (targetRow < 0) targetRow = 0;
            if (targetRow > maxRow) targetRow = maxRow;
            int target = targetRow * columns + col;
            if (target < count) return target;

            targetRow--;
            if (targetRow < 0) return index;
            target = targetRow * columns + col;
            return target < count ? target : index;
        }
    }
}

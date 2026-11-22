using System;
using System.Numerics;

namespace ModernImageViewer
{
    public static partial class ViewerMath
    {
        public const long AnimationBudgetBytes = 2L * 1024 * 1024 * 1024;
        private const double AnimationMinScale = 0.5;

        /// <summary>
        /// Every frame of an animation is held whole on the GPU, so a long one could take all of its memory.
        /// Past the budget it is drawn smaller, down to half size, and only then does it stop early.
        /// </summary>
        public static (double Scale, int Frames) AnimationBudget(int width, int height, int frames)
        {
            double perFrame = Math.Max(1.0, width) * Math.Max(1.0, height) * 4;
            if (perFrame * frames <= AnimationBudgetBytes) return (1, frames);
            double scale = Math.Sqrt(AnimationBudgetBytes / (perFrame * frames));
            if (scale >= AnimationMinScale) return (scale, frames);
            double smallest = perFrame * AnimationMinScale * AnimationMinScale;
            return (AnimationMinScale, Math.Max(1, (int)(AnimationBudgetBytes / smallest)));
        }

        /// <summary>A window saved on a monitor that has since gone is brought back to where its title bar can be reached.</summary>
        public static (int X, int Y) KeepOnScreen(int x, int y, int width, int height, int areaX, int areaY, int areaWidth, int areaHeight)
        {
            const int keepWide = 120, keepTall = 40;
            int minX = areaX - width + keepWide;
            int maxX = areaX + areaWidth - keepWide;
            int maxY = areaY + areaHeight - keepTall;
            return (Math.Clamp(x, Math.Min(minX, maxX), Math.Max(minX, maxX)), Math.Clamp(y, areaY, Math.Max(areaY, maxY)));
        }

        /// <summary>The thumbnail size for a tile on screen, in steps so a small change of tile size does not reload them all.</summary>
        public static int ThumbnailEdge(double tileSize, double rasterScale)
        {
            int wanted = (int)Math.Ceiling(Math.Max(1, tileSize) * Math.Max(1, rasterScale) / 50) * 50;
            return Math.Clamp(wanted, 250, 1024);
        }

        public static string FormatFileSize(long bytes) =>
            bytes / 1024.0 < 1024 ? $"{bytes / 1024} KB" : $"{bytes / 1048576.0:F2} MB";

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

        /// <summary>Capped dimensions for the single-image 3D sway surface, preserving aspect ratio.</summary>
        public static (int Width, int Height) SwaySurfaceSize(int sourceWidth, int sourceHeight, int maxLongestSide = 3072)
        {
            int w = Math.Max(1, sourceWidth);
            int h = Math.Max(1, sourceHeight);
            int cap = Math.Max(1, maxLongestSide);
            double scale = Math.Min(1.0, (double)cap / Math.Max(w, h));
            return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        }

        private static float MapPointerAxis(double ptr, double viewLen, double displayedLen)
        {
            if (!double.IsFinite(ptr) || viewLen <= 0 || displayedLen <= 0)
                return 0f;

            double t = displayedLen <= viewLen + 1.0
                ? (ptr - (viewLen - displayedLen) / 2.0) / displayedLen
                : ptr / viewLen;
            return (float)Math.Clamp(t * 2.0 - 1.0, -1.0, 1.0);
        }

        /// <summary>Maps horizontal pointer position across the displayed image to a normalized sway angle in [-1, 1].</summary>
        public static float CalculateSwayPointerAngle(double pointerX, double viewWidth, double displayedImageWidth, double panX) =>
            MapPointerAxis(pointerX, viewWidth, displayedImageWidth);

        /// <summary>Maps pointer X and Y across the displayed image to normalized [-1, 1] tilt coordinates.</summary>
        public static (float X, float Y) CalculateSwayPointerVector(
            double pointerX, double pointerY,
            double viewWidth, double viewHeight,
            double displayedWidth, double displayedHeight) =>
            (MapPointerAxis(pointerX, viewWidth, displayedWidth), MapPointerAxis(pointerY, viewHeight, displayedHeight));

        /// <summary>Exponentially smooths pointer-driven sway angle and snaps when very close to target.</summary>
        public static float SmoothSwayAngle(float current, float target, double dtSeconds)
        {
            if (!double.IsFinite(current)) current = 0f;
            if (!double.IsFinite(target)) target = 0f;
            target = Math.Clamp(target, -1f, 1f);
            if (Math.Abs(target - current) < 0.0005f) return target;
            double dt = double.IsFinite(dtSeconds) ? Math.Clamp(dtSeconds, 0.0, 0.1) : 0.0;
            float alpha = 1f - (float)Math.Exp(-dt * 12.0);
            return current + (target - current) * alpha;
        }

        /// <summary>Converts an angle in [-1, 1] back to a continuous sway cycle phase in turns.</summary>
        public static double SwayPhaseFromAngle(float angle) =>
            Math.Asin(Math.Clamp(double.IsFinite(angle) ? angle : 0.0, -1.0, 1.0)) / (Math.PI * 2.0);

        public const double SlideCrossfadeOverlap = 0.25;

        /// <summary>Advances phase for one-way Slide: looping wraps cleanly onto [overlap, 1) after the first pass; non-looping clamps at 1.</summary>
        public static double NextOneWaySlidePhase(double phase, double dtSeconds, double durationSeconds, bool loop)
        {
            double p = double.IsFinite(phase) && phase >= 0.0 ? phase : 0.0;
            double dt = double.IsFinite(dtSeconds) && dtSeconds > 0.0 ? dtSeconds : 0.0;
            double dur = double.IsFinite(durationSeconds) && durationSeconds >= 0.5 ? durationSeconds : 0.5;
            if (loop)
            {
                double span = 1.0 - SlideCrossfadeOverlap;
                p += dt * (span / dur);
                if (p >= 1.0)
                    p = SlideCrossfadeOverlap + ((p - SlideCrossfadeOverlap) % span);
                return p;
            }
            return Math.Min(1.0, p + dt / dur);
        }

        /// <summary>Returns background pass offsets (0..1) and Pass-B smoothstep blend (0..1) for unidirectional Slide.</summary>
        public static (float OffsetA, float OffsetB, float BlendB) SlideOneWayLayers(double phase, bool loop)
        {
            double p = double.IsFinite(phase) ? Math.Clamp(phase, 0.0, 1.0) : 0.0;
            if (!loop)
            {
                float eased = (float)(0.5 - 0.5 * Math.Cos(p * Math.PI));
                return (eased, 0f, 0f);
            }

            double fadeStart = 1.0 - SlideCrossfadeOverlap;
            if (p <= fadeStart)
                return ((float)p, 0f, 0f);

            double f = Math.Clamp((p - fadeStart) / SlideCrossfadeOverlap, 0.0, 1.0);
            float blend = (float)(f * f * (3.0 - 2.0 * f));
            return ((float)p, (float)(p - fadeStart), blend);
        }

        /// <summary>Returns horizontal 3D light position in Slide mode, continuous across the loop wrap boundary.</summary>
        public static float SlideLightX(double phase, bool loop, float slideOffsetA)
        {
            if (!loop)
                return (slideOffsetA * 2f - 1f) * 0.7f;

            double p = double.IsFinite(phase) ? phase : 0.0;
            double span = 1.0 - SlideCrossfadeOverlap;
            double rad = ((p - SlideCrossfadeOverlap) / span) * (Math.PI * 2.0);
            return (float)Math.Sin(rad) * 0.7f;
        }

        /// <summary>Overscan crop factor so higher sway depths and radial Breathe mode never reveal clamped edge streaks.</summary>
        public static float SwayOverscanFor(double depthRotation, bool isBreathe)
        {
            double d = double.IsFinite(depthRotation) && depthRotation > 0 ? depthRotation : 0.0;
            float over = 1.025f;
            if (d > 0.06) over += (float)((d - 0.06) * 0.65);
            if (isBreathe) over += Math.Max(0.01f, (float)(d * 0.35));
            return over;
        }

        /// <summary>Overscan crop factor accounting for asymmetric subject-anchored pivots at high sway depths.</summary>
        public static float SwayOverscanFor(double depthRotation, float pivot, bool isBreathe)
        {
            double d = double.IsFinite(depthRotation) && depthRotation > 0 ? depthRotation : 0.0;
            float p = float.IsFinite(pivot) ? Math.Clamp(pivot, 0.12f, 0.88f) : 0.5f;
            float worstSideShift = Math.Min(0.090f, (float)(d * Math.Max(p, 1f - p)));
            float over = Math.Max(1.025f, 1f / (1f - 2f * worstSideShift));
            if (isBreathe) over += Math.Max(0.012f, (float)(d * 0.35));
            return Math.Min(over, 1.24f);
        }

        /// <summary>Tight overscan crop factor when an artificial backdrop renders edge-to-edge behind the warped subject.</summary>
        public static float BackdropOverscanFor(double depthRotation, bool isBreathe)
        {
            double d = double.IsFinite(depthRotation) && depthRotation > 0 ? depthRotation : 0.0;
            float over = 1.015f;
            if (isBreathe) over += Math.Max(0.01f, (float)(d * 0.25));
            return Math.Min(over, 1.08f);
        }

        /// <summary>Builds a 2D view transform matrix combining mirror, flip, 90-degree rotations, and output scaling.</summary>
        public static Matrix3x2 BuildViewTransformMatrix(
            float bitmapWidth, float bitmapHeight,
            float drawWidth, float drawHeight,
            int quarterTurns, bool mirrorHorizontal, bool flipVertical)
        {
            int q = ((quarterTurns % 4) + 4) % 4;
            float bw = Math.Max(1f, bitmapWidth);
            float bh = Math.Max(1f, bitmapHeight);
            float outW = (q % 2 == 1) ? bh : bw;
            float outH = (q % 2 == 1) ? bw : bh;

            var m = Matrix3x2.CreateTranslation(-bw * 0.5f, -bh * 0.5f);
            if (mirrorHorizontal || flipVertical)
                m *= Matrix3x2.CreateScale(mirrorHorizontal ? -1f : 1f, flipVertical ? -1f : 1f);
            if (q != 0)
                m *= Matrix3x2.CreateRotation(q * (MathF.PI * 0.5f));
            m *= Matrix3x2.CreateScale(drawWidth / outW, drawHeight / outH);
            m *= Matrix3x2.CreateTranslation(drawWidth * 0.5f, drawHeight * 0.5f);
            return m;
        }

        /// <summary>Maps a pointer position in view coordinates back to normalized source image coordinates [0, 1] x [0, 1].</summary>
        public static (bool Inside, float U, float V) ScreenToSourceNormalized(
            double pointerX, double pointerY,
            double viewW, double viewH,
            double displayedW, double displayedH,
            double panX, double panY,
            int quarterTurns, bool mirrorHorizontal, bool flipVertical)
        {
            if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY)
                || viewW <= 0 || viewH <= 0 || displayedW <= 0 || displayedH <= 0)
                return (false, 0.5f, 0.5f);

            double drawX = displayedW <= viewW + 1.0 ? (viewW - displayedW) / 2.0 : panX;
            double drawY = displayedH <= viewH + 1.0 ? (viewH - displayedH) / 2.0 : panY;
            double dx = (pointerX - drawX) / displayedW;
            double dy = (pointerY - drawY) / displayedH;
            if (dx < 0.0 || dx > 1.0 || dy < 0.0 || dy > 1.0)
                return (false, 0.5f, 0.5f);

            int q = ((quarterTurns % 4) + 4) % 4;
            double u1, v1;
            switch (q)
            {
                case 1: u1 = dy; v1 = 1.0 - dx; break;
                case 2: u1 = 1.0 - dx; v1 = 1.0 - dy; break;
                case 3: u1 = 1.0 - dy; v1 = dx; break;
                default: u1 = dx; v1 = dy; break;
            }
            if (mirrorHorizontal) u1 = 1.0 - u1;
            if (flipVertical) v1 = 1.0 - v1;
            return (true, (float)Math.Clamp(u1, 0.0, 1.0), (float)Math.Clamp(v1, 0.0, 1.0));
        }
    }
}

using System;

namespace ModernImageViewer
{
    public static partial class ViewerMath
    {
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
    }
}

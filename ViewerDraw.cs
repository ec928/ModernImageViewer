using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;

namespace ModernImageViewer
{
    public static partial class ViewerMath
    {
        private static ICanvasImage ChainToneEffects(
            ICanvasImage source,
            SaturationEffect? grayscale, bool applyGrayscale,
            InvertEffect? invert, bool applyInvert)
        {
            ICanvasImage output = source;
            if (applyGrayscale && grayscale != null)
            {
                grayscale.Source = output;
                output = grayscale;
            }
            if (applyInvert && invert != null)
            {
                invert.Source = output;
                output = invert;
            }
            return output;
        }

        public static void DrawMappedImage(
    CanvasDrawingSession session,
    double viewW, double viewH,
    double logicalW, double logicalH,
    double panX, double panY,
    float zoom, float gamma, float dpiScale,
    CanvasBitmap bitmap,
    CropEffect crop,
    GammaTransferEffect decode,
    GammaTransferEffect userGamma,
    Transform2DEffect transform,
    GammaTransferEffect encode,
    int quarterTurns = 0,
    bool mirrorHorizontal = false,
    bool flipVertical = false,
    SaturationEffect? grayscale = null,
    bool applyGrayscale = false,
    InvertEffect? invert = null,
    bool applyInvert = false)
        {
            if (zoom <= 0) zoom = 1.0f;
            double imgW = (logicalW / dpiScale) * zoom;
            double imgH = (logicalH / dpiScale) * zoom;

            if (imgW <= 0 || imgH <= 0) return;

            double drawX = imgW <= viewW + 1.0 ? (viewW - imgW) / 2.0 : panX;
            double drawY = imgH <= viewH + 1.0 ? (viewH - imgH) / 2.0 : panY;

            // Both LF and HF now arrive perfectly color-managed by WIC.
            // We bypass Win2D's ColorManagementEffect completely.
            decode.Source = crop;

            userGamma.Source = decode;
            userGamma.RedExponent = gamma / 2.2f;
            userGamma.GreenExponent = gamma / 2.2f;
            userGamma.BlueExponent = gamma / 2.2f;

            transform.Source = userGamma;
            transform.TransformMatrix = BuildViewTransformMatrix(
                bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height,
                (float)imgW, (float)imgH,
                quarterTurns, mirrorHorizontal, flipVertical);

            encode.Source = transform;
            ICanvasImage output = ChainToneEffects(encode, grayscale, applyGrayscale, invert, applyInvert);
            session.DrawImage(output, new Vector2((float)drawX, (float)drawY));
        }

        public static void DrawDualMappedImages(
            CanvasDrawingSession session,
            double viewW, double viewH,
            double leftLogicalW, double leftLogicalH,
            double rightLogicalW, double rightLogicalH,
            double panX, double panY,
            float zoom, float gamma, float dpiScale,
            CanvasBitmap leftBitmap,
            CanvasBitmap rightBitmap,
            CropEffect crop,
            GammaTransferEffect decode,
            GammaTransferEffect userGamma,
            Transform2DEffect transform,
            GammaTransferEffect encode,
            SaturationEffect? grayscale = null,
            bool applyGrayscale = false,
            InvertEffect? invert = null,
            bool applyInvert = false)
        {
            if (zoom <= 0) zoom = 1.0f;
            if (leftLogicalW <= 0 || leftLogicalH <= 0 || rightLogicalW <= 0 || rightLogicalH <= 0) return;

            double spreadLogicalH = Math.Max(leftLogicalH, rightLogicalH);
            double leftNormW = leftLogicalW * (spreadLogicalH / leftLogicalH);
            double rightNormW = rightLogicalW * (spreadLogicalH / rightLogicalH);

            double leftDrawW = (leftNormW / dpiScale) * zoom;
            double rightDrawW = (rightNormW / dpiScale) * zoom;
            double totalW = leftDrawW + rightDrawW;
            double totalH = (spreadLogicalH / dpiScale) * zoom;

            if (totalW <= 0 || totalH <= 0) return;

            double startX = totalW <= viewW + 1.0 ? (viewW - totalW) / 2.0 : panX;
            double drawY = totalH <= viewH + 1.0 ? (viewH - totalH) / 2.0 : panY;

            userGamma.RedExponent = gamma / 2.2f;
            userGamma.GreenExponent = gamma / 2.2f;
            userGamma.BlueExponent = gamma / 2.2f;

            // Draw Left Page
            crop.Source = leftBitmap;
            crop.SourceRectangle = leftBitmap.Bounds;
            decode.Source = crop;
            userGamma.Source = decode;
            transform.Source = userGamma;
            transform.TransformMatrix = Matrix3x2.CreateScale(
                (float)(leftDrawW / leftBitmap.SizeInPixels.Width),
                (float)(totalH / leftBitmap.SizeInPixels.Height));
            encode.Source = transform;
            ICanvasImage output = ChainToneEffects(encode, grayscale, applyGrayscale, invert, applyInvert);
            session.DrawImage(output, new Vector2((float)startX, (float)drawY));

            // Draw Right Page
            crop.Source = rightBitmap;
            crop.SourceRectangle = rightBitmap.Bounds;
            transform.TransformMatrix = Matrix3x2.CreateScale(
                (float)(rightDrawW / rightBitmap.SizeInPixels.Width),
                (float)(totalH / rightBitmap.SizeInPixels.Height));
            session.DrawImage(output, new Vector2((float)(startX + leftDrawW), (float)drawY));
        }
    }
}

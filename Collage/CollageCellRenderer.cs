using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using System;
using System.Numerics;

namespace ModernImageViewer.Collage
{
    public static class CollageCellRenderer
    {
        private static EffectStack? _sharedEffectStack;

        public static void ResetSharedEffects()
        {
            _sharedEffectStack?.Dispose();
            _sharedEffectStack = null;
        }

        public static void DrawCell(
            CanvasDrawingSession session,
            CollageElement element,
            ImageCacheEntry? entry,
            CanvasBitmap? gpuBitmap,
            float gamma,
            CanvasGeometry clipGeometry)
        {
            if (element == null || element.Layout.Width <= 0 || element.Layout.Height <= 0)
                return;

            if (gpuBitmap == null)
            {
                if (element.CornerRadius > 0)
                    session.FillRoundedRectangle(element.Layout, element.CornerRadius, element.CornerRadius, Colors.DimGray);
                else
                    session.FillRectangle(element.Layout, Colors.DimGray);
                return;
            }

            if (_sharedEffectStack == null)
            {
                _sharedEffectStack = new EffectStack();
                _sharedEffectStack.Initialize();
            }

            var effects = _sharedEffectStack;
            if (effects.Crop == null || effects.DecodeToLinear == null ||
                effects.UserGamma == null || effects.Transform == null ||
                effects.EncodeToSrgb == null) return;

            effects.Crop.Source = gpuBitmap;
            effects.Crop.SourceRectangle = gpuBitmap.Bounds;

            // Always use DecodeToLinear path (unified pipeline)
            effects.DecodeToLinear.Source = effects.Crop;
            ICanvasImage linearImage = effects.DecodeToLinear;

            effects.UserGamma.Source = linearImage;
            effects.UserGamma.RedExponent = gamma / 2.2f;
            effects.UserGamma.GreenExponent = gamma / 2.2f;
            effects.UserGamma.BlueExponent = gamma / 2.2f;

            double logicalW = (entry != null && entry.NativeWidth > 0) ? entry.NativeWidth : gpuBitmap.SizeInPixels.Width;
            double logicalH = (entry != null && entry.NativeHeight > 0) ? entry.NativeHeight : gpuBitmap.SizeInPixels.Height;

            double scaledW = logicalW * element.Zoom;
            double scaledH = logicalH * element.Zoom;

            effects.Transform.Source = effects.UserGamma;
            effects.Transform.TransformMatrix = Matrix3x2.CreateScale(
                (float)(scaledW / gpuBitmap.SizeInPixels.Width),
                (float)(scaledH / gpuBitmap.SizeInPixels.Height));

            effects.Transform.InterpolationMode = CanvasImageInterpolation.HighQualityCubic;

            effects.EncodeToSrgb.Source = effects.Transform;

            double drawX = element.Layout.X;
            double drawY = element.Layout.Y;

            if (scaledW < element.Layout.Width + 1.0)
                drawX += (element.Layout.Width - scaledW) / 2.0;

            if (scaledH < element.Layout.Height + 1.0)
                drawY += (element.Layout.Height - scaledH) / 2.0;

            drawX += element.PanX * element.Zoom;
            drawY += element.PanY * element.Zoom;

            using (session.CreateLayer(1f, clipGeometry))
            {
                session.DrawImage(effects.EncodeToSrgb, new Vector2((float)drawX, (float)drawY));
            }
        }
    }
}
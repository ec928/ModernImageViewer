using System;

namespace ModernImageViewer
{
    /// <summary>
    /// Pixel sizes for the multi-view canvas. The window's shape is the picture's shape.
    /// A named height sets the height, and the width follows that shape.
    /// </summary>
    public static class GridCanvasMath
    {
        public const int MaxEdge = 16384;

        public static readonly (string Label, double WidthOverHeight)[] Aspects =
        {
            ("16:9", 16.0 / 9.0),
            ("21:9", 21.0 / 9.0),
            ("4:3", 4.0 / 3.0),
            ("1:1", 1.0),
            ("3:4", 3.0 / 4.0)
        };

        public static readonly int[] SaveHeights = { 720, 1080, 1200, 1440, 2160, 4320 };

        public static string MatchAspect(int width, int height)
        {
            if (width < 1 || height < 1) return "Custom";
            foreach (var aspect in Aspects)
            {
                int expected = (int)Math.Round(height * aspect.WidthOverHeight);
                if (Math.Abs(expected - width) <= 2) return aspect.Label;
            }
            return "Custom";
        }

        public static bool TryOutputSize(int height, double canvasWidth, double canvasHeight, out int width, out int outputHeight)
        {
            width = 0;
            outputHeight = 0;
            if (height < 1 || height > MaxEdge || canvasWidth <= 0 || canvasHeight <= 0) return false;
            long w = (long)Math.Round(height * (canvasWidth / canvasHeight));
            if (w < 1 || w > MaxEdge) return false;
            width = (int)w;
            outputHeight = height;
            return true;
        }

        public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
        {
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            if (maxWidth < 1) maxWidth = width;
            if (maxHeight < 1) maxHeight = height;
            double scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
            return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        public readonly struct PlacedImage
        {
            public PlacedImage(double left, double top, double width, double height)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            public double Left { get; }
            public double Top { get; }
            public double Width { get; }
            public double Height { get; }
        }

        /// <summary>
        /// Where the whole photo sits inside its cell. The container is centered, then scaled
        /// around its own center and shifted by the pan.
        /// </summary>
        public static PlacedImage Place(double cellW, double cellH, double imageW, double imageH, double scale, double translateX, double translateY)
        {
            double left = (cellW - imageW) / 2.0 + imageW / 2.0 * (1.0 - scale) + translateX;
            double top = (cellH - imageH) / 2.0 + imageH / 2.0 * (1.0 - scale) + translateY;
            return new PlacedImage(left, top, imageW * scale, imageH * scale);
        }

        public static (int Width, int Height) DecodeSize(int nativeWidth, int nativeHeight, double destWidth, double destHeight)
        {
            if (nativeWidth < 1 || nativeHeight < 1) return (1, 1);
            double scale = 1.0;
            if (destWidth > 0 && destWidth < nativeWidth) scale = destWidth / nativeWidth;
            if (destHeight > 0 && nativeHeight * scale > destHeight + 0.5)
                scale = destHeight / nativeHeight;
            int dw = Math.Max(1, (int)Math.Round(nativeWidth * scale));
            int dh = Math.Max(1, (int)Math.Round(nativeHeight * scale));
            int longEdge = Math.Max(dw, dh);
            if (longEdge > MaxEdge)
            {
                double cap = MaxEdge / (double)longEdge;
                dw = Math.Max(1, (int)Math.Floor(dw * cap));
                dh = Math.Max(1, (int)Math.Floor(dh * cap));
            }
            return (dw, dh);
        }
    }
}

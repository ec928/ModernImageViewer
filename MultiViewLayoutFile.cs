using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernImageViewer
{
    public sealed class MultiViewLayoutFile
    {
        [JsonPropertyName("$schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("created")]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("canvas")]
        public CanvasLayoutDto Canvas { get; set; } = new();

        [JsonPropertyName("state")]
        public LayoutStateDto State { get; set; } = new();

        [JsonPropertyName("mosaic")]
        public MosaicNodeDto? Mosaic { get; set; }

        [JsonPropertyName("photos")]
        public List<PhotoSlotDto> Photos { get; set; } = new();

        [JsonPropertyName("freeform")]
        public List<FreeformCellDto>? Freeform { get; set; }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        public static MultiViewLayoutFile? FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<MultiViewLayoutFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
    }

    public sealed class CanvasLayoutDto
    {
        [JsonPropertyName("aspect")]
        public string? Aspect { get; set; }

        [JsonPropertyName("backgroundColor")]
        public string? BackgroundColor { get; set; }

        [JsonPropertyName("surface")]
        public string? Surface { get; set; }
    }

    public sealed class LayoutStateDto
    {
        [JsonPropertyName("lines")]
        public int Lines { get; set; } = 1;

        [JsonPropertyName("lineCounts")]
        public List<int>? LineCounts { get; set; }

        [JsonPropertyName("flipped")]
        public bool Flipped { get; set; }

        [JsonPropertyName("heroActive")]
        public bool HeroActive { get; set; }

        [JsonPropertyName("heroKind")]
        public string HeroKind { get; set; } = "Banner";

        [JsonPropertyName("heroPosition")]
        public int HeroPosition { get; set; }

        [JsonPropertyName("isFillMode")]
        public bool IsFillMode { get; set; } = true;
    }

    public sealed class PhotoSlotDto
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("zoom")]
        public double? Zoom { get; set; }

        [JsonPropertyName("panX")]
        public double? PanX { get; set; }

        [JsonPropertyName("panY")]
        public double? PanY { get; set; }

        [JsonPropertyName("hidden")]
        public bool Hidden { get; set; }

        [JsonPropertyName("border")]
        public CellBorderDto? Border { get; set; }

        [JsonPropertyName("geometry")]
        public CellGeometryDto? Geometry { get; set; }
    }

    public sealed class FreeformCellDto
    {
        [JsonPropertyName("sourceSlot")]
        public int SourceSlot { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("height")]
        public double Height { get; set; }

        [JsonPropertyName("zIndex")]
        public int ZIndex { get; set; } = 1;

        [JsonPropertyName("zoom")]
        public double? Zoom { get; set; }

        [JsonPropertyName("panX")]
        public double? PanX { get; set; }

        [JsonPropertyName("panY")]
        public double? PanY { get; set; }

        [JsonPropertyName("hidden")]
        public bool Hidden { get; set; }

        [JsonPropertyName("border")]
        public CellBorderDto? Border { get; set; }

        [JsonPropertyName("geometry")]
        public CellGeometryDto? Geometry { get; set; }
    }

    public sealed class CellBorderDto
    {
        [JsonPropertyName("thickness")]
        public double Thickness { get; set; }

        [JsonPropertyName("color")]
        public string? Color { get; set; } = "#FFFFFFFF";

        [JsonPropertyName("padding")]
        public double Padding { get; set; }

        [JsonPropertyName("cornerRadius")]
        public double CornerRadius { get; set; }

        [JsonPropertyName("shadow")]
        public bool Shadow { get; set; }

        [JsonPropertyName("shadowIntensity")]
        public double ShadowIntensity { get; set; }

        [JsonPropertyName("strokeStyle")]
        public string StrokeStyle { get; set; } = "Solid";

        [JsonPropertyName("secondaryColor")]
        public string? SecondaryColor { get; set; }

        [JsonPropertyName("gutter")]
        public double Gutter
        {
            get => Padding;
            set => Padding = value;
        }

        [JsonPropertyName("rimGlow")]
        public bool RimGlow { get; set; }

        [JsonPropertyName("gutterColor")]
        public string? GutterColor { get; set; } = "#00000000";

        [JsonPropertyName("gutterMaterial")]
        public string GutterMaterial { get; set; } = "None";

        public CellBorderDto Clone() => new()
        {
            Thickness = Thickness,
            Color = Color,
            Padding = Padding,
            CornerRadius = CornerRadius,
            Shadow = Shadow,
            ShadowIntensity = ShadowIntensity,
            StrokeStyle = StrokeStyle,
            SecondaryColor = SecondaryColor,
            RimGlow = RimGlow,
            GutterColor = GutterColor,
            GutterMaterial = GutterMaterial
        };

        /// <summary>True when the drawn frame matches. The Shadow flag is ignored because the intensity is what draws.</summary>
        public bool SameLook(CellBorderDto? other)
        {
            if (other == null) return false;
            return Nearly(Thickness, other.Thickness)
                && Nearly(Padding, other.Padding)
                && Nearly(CornerRadius, other.CornerRadius)
                && Nearly(ShadowIntensity, other.ShadowIntensity)
                && RimGlow == other.RimGlow
                && string.Equals(FrameColor.Token(StrokeStyle, "Solid"), FrameColor.Token(other.StrokeStyle, "Solid"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(FrameColor.Canon(Color, "#FFFFFFFF"), FrameColor.Canon(other.Color, "#FFFFFFFF"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(FrameColor.Canon(SecondaryColor, "#00000000"), FrameColor.Canon(other.SecondaryColor, "#00000000"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(FrameColor.Token(GutterMaterial, "None"), FrameColor.Token(other.GutterMaterial, "None"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(FrameColor.Canon(GutterColor, "#00000000"), FrameColor.Canon(other.GutterColor, "#00000000"), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Nearly(double a, double b) => Math.Abs(a - b) < 0.01;
    }

    /// <summary>The four frames the multi-view menu applies in one click.</summary>
    public static class FrameLooks
    {
        public static CellBorderDto Card() => new()
        {
            Thickness = 1,
            Padding = 8,
            CornerRadius = 12,
            Color = "#FFFFFFFF",
            StrokeStyle = "InnerMuted",
            ShadowIntensity = 40,
            Shadow = true,
            RimGlow = true,
            GutterMaterial = "None",
            GutterColor = "#00000000"
        };

        public static CellBorderDto Mat() => new()
        {
            Thickness = 1,
            Padding = 18,
            CornerRadius = 0,
            Color = "#FF000000",
            StrokeStyle = "Solid",
            ShadowIntensity = 45,
            Shadow = true,
            RimGlow = false,
            GutterMaterial = "Solid",
            GutterColor = "#FFF8F8F8"
        };

        public static CellBorderDto Round() => new()
        {
            Thickness = 2,
            Padding = 8,
            CornerRadius = 16,
            Color = "#FFFFFFFF",
            SecondaryColor = "#FFFFC83B",
            StrokeStyle = "Gradient",
            ShadowIntensity = 30,
            Shadow = true,
            RimGlow = true,
            GutterMaterial = "Acrylic",
            GutterColor = "#FFF8F8F8"
        };

        public static CellBorderDto Flush() => new()
        {
            Thickness = 0,
            Padding = 0,
            CornerRadius = 0,
            Color = "#00000000",
            StrokeStyle = "Solid",
            ShadowIntensity = 0,
            Shadow = false,
            RimGlow = false,
            GutterMaterial = "None",
            GutterColor = "#00000000"
        };

        public static int Match(CellBorderDto? value)
        {
            if (value == null) return -1;
            if (value.SameLook(Card())) return 0;
            if (value.SameLook(Mat())) return 1;
            if (value.SameLook(Round())) return 2;
            if (value.SameLook(Flush())) return 3;
            return -1;
        }
    }

    /// <summary>Shared colour rules for the on-screen frame and the JPEG export.</summary>
    public static class FrameColor
    {
        public readonly record struct Rgba(byte A, byte R, byte G, byte B);

        public static Rgba Parse(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new Rgba(0, 0, 0, 0);
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 && TryByte(hex, 0, out byte r) && TryByte(hex, 2, out byte g) && TryByte(hex, 4, out byte b))
                return new Rgba(255, r, g, b);
            if (hex.Length == 8 && TryByte(hex, 0, out byte a) && TryByte(hex, 2, out byte r8) && TryByte(hex, 4, out byte g8) && TryByte(hex, 6, out byte b8))
                return new Rgba(a, r8, g8, b8);
            return new Rgba(255, 255, 255, 255);
        }

        public static string Canon(string? hex, string fallback)
        {
            string raw = string.IsNullOrWhiteSpace(hex) ? fallback : hex.Trim();
            if (raw.StartsWith("#", StringComparison.Ordinal)) raw = raw.Substring(1);
            if (raw.Length == 6) raw = "FF" + raw;
            return raw.ToUpperInvariant();
        }

        public static string Token(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        /// <summary>A hairline in the chosen colour. A clear colour becomes a white hairline so Muted still draws.</summary>
        public static Rgba MutedStroke(string? color)
        {
            var c = Parse(color);
            if (c.A == 0) c = new Rgba(255, 255, 255, 255);
            int a = (int)Math.Round(c.A * 0.55);
            if (a < 70) a = 70;
            if (a > 180) a = 180;
            return new Rgba((byte)a, c.R, c.G, c.B);
        }

        /// <summary>Clear secondary means fade the start colour. Any other stored colour is the second stop.</summary>
        public static Rgba GradientEnd(string? secondary, Rgba primary)
        {
            if (string.Equals(Canon(secondary, "#00000000"), "00000000", StringComparison.OrdinalIgnoreCase))
            {
                int a = (int)Math.Round(primary.A * 0.40);
                if (a < 0) a = 0;
                if (a > 255) a = 255;
                return new Rgba((byte)a, primary.R, primary.G, primary.B);
            }
            return Parse(secondary);
        }

        /// <summary>Frosted uses the mat colour at a fixed translucency. Solid uses it opaque. None draws nothing.</summary>
        public static Rgba MatFill(string? material, string? color)
        {
            if (string.Equals(Token(material, "None"), "Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                var tint = Parse(color);
                if (tint.A == 0) tint = new Rgba(255, 255, 255, 255);
                return new Rgba(48, tint.R, tint.G, tint.B);
            }
            if (!string.Equals(Token(material, "None"), "Solid", StringComparison.OrdinalIgnoreCase))
                return new Rgba(0, 0, 0, 0);
            return Parse(color);
        }

        private static bool TryByte(string hex, int index, out byte value) =>
            byte.TryParse(hex.Substring(index, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A drop shadow is the card's silhouette, shifted down and blurred. The card covers
    /// the middle, so only the fade on the mat is seen. Intensity 0 draws nothing.
    /// Sigma stays larger than the downward shift, so the fringe fades instead of forming a lip.
    /// </summary>
    public static class FrameShadow
    {
        public readonly record struct Spec(double Sigma, double OffsetX, double OffsetY, double Source);

        public static Spec At(double intensity)
        {
            if (intensity <= 0) return new Spec(0, 0, 0, 0);
            double t = intensity >= 100 ? 1 : intensity / 100.0;
            return new Spec(
                6 + (12 * t),
                0.6 * t,
                2 + (6 * t),
                0.45 + (0.45 * t));
        }

        /// <summary>Straight alpha, one byte per pixel, row-major. 0 is clear.</summary>
        public static byte[] RenderAlpha(
            int width,
            int height,
            double cardX,
            double cardY,
            double cardW,
            double cardH,
            double cornerRadius,
            double intensity,
            double scaleX = 1,
            double scaleY = 1)
        {
            int count = width > 0 && height > 0 ? width * height : 0;
            var alpha = new byte[count];
            if (count == 0 || intensity <= 0 || cardW < 1 || cardH < 1) return alpha;

            var spec = At(intensity);
            double sigma = spec.Sigma * Math.Max(scaleX, scaleY);
            if (sigma < 0.5 || spec.Source <= 0) return alpha;
            double ox = spec.OffsetX * scaleX;
            double oy = spec.OffsetY * scaleY;
            int radius = Math.Max(1, (int)Math.Ceiling(sigma * 2.8));
            var kernel = Kernel(sigma, radius);

            int x0 = Clamp((int)Math.Floor(cardX + ox) - radius, 0, width);
            int y0 = Clamp((int)Math.Floor(cardY + oy) - radius, 0, height);
            int x1 = Clamp((int)Math.Ceiling(cardX + ox + cardW) + radius, 0, width);
            int y1 = Clamp((int)Math.Ceiling(cardY + oy + cardH) + radius, 0, height);
            if (x1 <= x0 || y1 <= y0) return alpha;

            var src = new float[count];
            Fill(src, width, height, cardX + ox, cardY + oy, cardW, cardH, cornerRadius, (float)spec.Source, x0, y0, x1, y1);
            Blur(src, width, height, x0, y0, x1, y1, kernel);
            ZeroInside(src, width, height, cardX, cardY, cardW, cardH, cornerRadius);

            for (int i = 0; i < count; i++)
            {
                float a = src[i];
                if (a <= 0) continue;
                if (a > 1) a = 1;
                alpha[i] = (byte)Math.Round(a * 255f);
            }
            return alpha;
        }

        private static float[] Kernel(double sigma, int radius)
        {
            var kernel = new float[radius * 2 + 1];
            double denom = 2 * sigma * sigma;
            double sum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                double v = Math.Exp(-(i * i) / denom);
                kernel[i + radius] = (float)v;
                sum += v;
            }
            if (sum <= 0) sum = 1;
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;
            return kernel;
        }

        private static void Fill(float[] src, int width, int height, double x, double y, double w, double h, double radius, float alpha, int x0, int y0, int x1, int y1)
        {
            for (int py = y0; py < y1; py++)
            {
                int row = py * width;
                double cy = py + 0.5;
                for (int px = x0; px < x1; px++)
                {
                    if (Inside(px + 0.5, cy, x, y, w, h, radius))
                        src[row + px] = alpha;
                }
            }
        }

        private static void ZeroInside(float[] src, int width, int height, double x, double y, double w, double h, double radius)
        {
            int x0 = Clamp((int)Math.Floor(x), 0, width);
            int y0 = Clamp((int)Math.Floor(y), 0, height);
            int x1 = Clamp((int)Math.Ceiling(x + w), 0, width);
            int y1 = Clamp((int)Math.Ceiling(y + h), 0, height);
            for (int py = y0; py < y1; py++)
            {
                int row = py * width;
                double cy = py + 0.5;
                for (int px = x0; px < x1; px++)
                {
                    if (Inside(px + 0.5, cy, x, y, w, h, radius))
                        src[row + px] = 0;
                }
            }
        }

        private static bool Inside(double px, double py, double x, double y, double w, double h, double radius)
        {
            if (px < x || py < y || px >= x + w || py >= y + h) return false;
            if (radius <= 0) return true;
            double r = Math.Min(radius, Math.Min(w, h) * 0.5);
            double dx = 0;
            double dy = 0;
            if (px < x + r) dx = px - (x + r);
            else if (px > x + w - r) dx = px - (x + w - r);
            if (py < y + r) dy = py - (y + r);
            else if (py > y + h - r) dy = py - (y + h - r);
            if (dx == 0 && dy == 0) return true;
            return (dx * dx) + (dy * dy) <= r * r;
        }

        private static void Blur(float[] src, int width, int height, int x0, int y0, int x1, int y1, float[] kernel)
        {
            int radius = kernel.Length / 2;
            var tmp = new float[width * height];
            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    float sum = 0;
                    int start = x - radius;
                    for (int k = 0; k < kernel.Length; k++)
                    {
                        int xx = start + k;
                        if ((uint)xx >= (uint)width) continue;
                        sum += src[row + xx] * kernel[k];
                    }
                    tmp[row + x] = sum;
                }
            }
            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    float sum = 0;
                    int start = y - radius;
                    for (int k = 0; k < kernel.Length; k++)
                    {
                        int yy = start + k;
                        if ((uint)yy >= (uint)height) continue;
                        sum += tmp[(yy * width) + x] * kernel[k];
                    }
                    src[row + x] = sum;
                }
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// <summary>How a cell turns its image, and on a freeform cell the shape and turn of the window itself.</summary>
    public sealed class CellGeometryDto
    {
        [JsonPropertyName("imageRotation")]
        public int ImageRotation { get; set; }

        [JsonPropertyName("imageFlipH")]
        public bool ImageFlipH { get; set; }

        [JsonPropertyName("imageFlipV")]
        public bool ImageFlipV { get; set; }

        [JsonPropertyName("shape")]
        public string Shape { get; set; } = WindowShapes.Rectangle;

        [JsonPropertyName("shapeFlipH")]
        public bool ShapeFlipH { get; set; }

        [JsonPropertyName("shapeFlipV")]
        public bool ShapeFlipV { get; set; }

        [JsonPropertyName("windowRotation")]
        public double WindowRotation { get; set; }

        public void TurnImage(int degrees) => ImageRotation = ((ImageRotation + degrees) % 360 + 360) % 360;

        public CellGeometryDto Clone() => (CellGeometryDto)MemberwiseClone();
    }

    /// <summary>Freeform window outlines, as corners in a unit square. A rectangle has no outline.</summary>
    public static class WindowShapes
    {
        public const string Rectangle = "Rectangle";
        public const string LShape = "L-shape";
        public const string Oval = "Circle / Oval";
        public const string Triangle = "Triangle";

        public static readonly string[] All = { Rectangle, LShape, Oval, Triangle };

        public static (double X, double Y)[]? Outline(string? shape, bool flipH = false, bool flipV = false)
        {
            (double X, double Y)[]? points = shape switch
            {
                LShape => new (double, double)[] { (0, 0), (0.5, 0), (0.5, 0.5), (1, 0.5), (1, 1), (0, 1) },
                Triangle => new (double, double)[] { (0.5, 0), (1, 1), (0, 1) },
                Oval => OvalPoints(72),
                _ => null
            };
            if (points == null) return null;
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = (flipH ? 1 - points[i].X : points[i].X, flipV ? 1 - points[i].Y : points[i].Y);
            }
            return points;
        }

        private static (double X, double Y)[] OvalPoints(int count)
        {
            var points = new (double X, double Y)[count];
            for (int i = 0; i < count; i++)
            {
                double a = 2 * Math.PI * i / count;
                points[i] = (0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a));
            }
            return points;
        }
    }

    /// <summary>What the canvas shows behind the cells, on screen and in the export.</summary>
    public static class CanvasLook
    {
        public const string Solid = "Solid";
        public const string Gradient = "Gradient";
        public const string Vignette = "Vignette";
        public const string DefaultColor = "#FF0A0A0A";

        public static readonly string[] Surfaces = { Solid, Gradient, Vignette };

        public static readonly (string Name, string Hex)[] Colors =
        {
            ("Near black", DefaultColor),
            ("Black", "#FF000000"),
            ("White", "#FFFFFFFF"),
            ("Warm white", "#FFF4EFE6"),
            ("Grey", "#FF808080"),
            ("Slate", "#FF2F3B47"),
            ("Navy", "#FF14213D"),
            ("Forest", "#FF1F3A2D"),
            ("Burgundy", "#FF4A1525")
        };

        /// <summary>The far colour of a gradient or vignette: darker, or lighter when the colour is already black.</summary>
        public static FrameColor.Rgba FarColor(string? hex)
        {
            var c = FrameColor.Parse(hex);
            if (c.R < 24 && c.G < 24 && c.B < 24)
                return new FrameColor.Rgba(c.A, 48, 48, 48);
            return new FrameColor.Rgba(c.A, (byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
        }
    }

    public sealed class MosaicNodeDto
    {
        [JsonPropertyName("vertical")]
        public bool Vertical { get; set; }

        [JsonPropertyName("leaf")]
        public int Leaf { get; set; } = -1;

        [JsonPropertyName("weights")]
        public List<double>? Weights { get; set; }

        [JsonPropertyName("children")]
        public List<MosaicNodeDto>? Children { get; set; }

        public static MosaicNodeDto FromMosaicLayout(MosaicLayout node)
        {
            var dto = new MosaicNodeDto
            {
                Vertical = node.Vertical,
                Leaf = node.Leaf
            };

            if (node.Weights.Count > 0)
                dto.Weights = new List<double>(node.Weights);

            if (node.Children.Count > 0)
            {
                dto.Children = new List<MosaicNodeDto>(node.Children.Count);
                foreach (var child in node.Children)
                    dto.Children.Add(FromMosaicLayout(child));
            }

            return dto;
        }

        public MosaicLayout ToMosaicLayout()
        {
            var node = new MosaicLayout
            {
                Vertical = Vertical,
                Leaf = Leaf
            };

            if (Weights != null && Weights.Count > 0)
            {
                node.Weights.Clear();
                node.Weights.AddRange(Weights);
            }

            if (Children != null && Children.Count > 0)
            {
                node.Children.Clear();
                foreach (var child in Children)
                    node.Children.Add(child.ToMosaicLayout());
            }

            return node;
        }
    }
}

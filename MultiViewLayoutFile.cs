using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>The canvas size the floating cells were saved on, so they can be placed on one of another size.</summary>
        [JsonPropertyName("width")]
        public double? Width { get; set; }

        [JsonPropertyName("height")]
        public double? Height { get; set; }
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

        [JsonPropertyName("framedSlots")]
        public int FramedSlots { get; set; }
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

    public static class FrameLooks
    {
        /// <summary>No line, mat or shadow.</summary>
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

        /// <summary>The same shadow cast by any outline, for a shaped window.</summary>
        public static byte[] RenderAlpha(int width, int height, (double X, double Y)[] outline, double intensity, double scaleX = 1, double scaleY = 1)
        {
            int count = width > 0 && height > 0 ? width * height : 0;
            var alpha = new byte[count];
            if (count == 0 || intensity <= 0 || outline.Length < 3) return alpha;

            var spec = At(intensity);
            double sigma = spec.Sigma * Math.Max(scaleX, scaleY);
            if (sigma < 0.5 || spec.Source <= 0) return alpha;
            double ox = spec.OffsetX * scaleX;
            double oy = spec.OffsetY * scaleY;
            int radius = Math.Max(1, (int)Math.Ceiling(sigma * 2.8));
            var kernel = Kernel(sigma, radius);

            var src = new float[count];
            FillOutline(src, width, height, outline, ox, oy, (float)spec.Source);
            int x0 = Clamp((int)Math.Floor(outline.Min(p => p.X) + ox) - radius, 0, width);
            int y0 = Clamp((int)Math.Floor(outline.Min(p => p.Y) + oy) - radius, 0, height);
            int x1 = Clamp((int)Math.Ceiling(outline.Max(p => p.X) + ox) + radius, 0, width);
            int y1 = Clamp((int)Math.Ceiling(outline.Max(p => p.Y) + oy) + radius, 0, height);
            if (x1 <= x0 || y1 <= y0) return alpha;
            Blur(src, width, height, x0, y0, x1, y1, kernel);
            FillOutline(src, width, height, outline, 0, 0, 0);

            for (int i = 0; i < count; i++)
            {
                float a = src[i];
                if (a <= 0) continue;
                if (a > 1) a = 1;
                alpha[i] = (byte)Math.Round(a * 255f);
            }
            return alpha;
        }

        /// <summary>Sets every pixel inside the outline, one row at a time.</summary>
        private static void FillOutline(float[] target, int width, int height, (double X, double Y)[] outline, double shiftX, double shiftY, float value)
        {
            int top = Clamp((int)Math.Floor(outline.Min(p => p.Y) + shiftY), 0, height);
            int bottom = Clamp((int)Math.Ceiling(outline.Max(p => p.Y) + shiftY), 0, height);
            var crossings = new List<double>();
            for (int y = top; y < bottom; y++)
            {
                double row = y + 0.5 - shiftY;
                crossings.Clear();
                for (int i = 0; i < outline.Length; i++)
                {
                    var a = outline[i];
                    var b = outline[(i + 1) % outline.Length];
                    if ((a.Y <= row && b.Y > row) || (b.Y <= row && a.Y > row))
                        crossings.Add(a.X + (row - a.Y) / (b.Y - a.Y) * (b.X - a.X) + shiftX);
                }
                crossings.Sort();
                for (int c = 0; c + 1 < crossings.Count; c += 2)
                {
                    int from = Clamp((int)Math.Ceiling(crossings[c] - 0.5), 0, width);
                    int to = Clamp((int)Math.Floor(crossings[c + 1] - 0.5) + 1, 0, width);
                    for (int x = from; x < to; x++)
                        target[y * width + x] = value;
                }
            }
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

        /// <summary>Where the L's inner corner sits, as a share of the window, before any flip.</summary>
        [JsonPropertyName("imageCounter")]
        public double ImageCounter { get; set; }

        /// <summary>The photo's turn inside its window: its quarter turns, less any turn of the shape alone.</summary>
        [JsonIgnore]
        public double ImageAngle => ImageRotation + ImageCounter;

        [JsonPropertyName("notchX")]
        public double NotchX { get; set; } = 0.5;

        [JsonPropertyName("notchY")]
        public double NotchY { get; set; } = 0.5;

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
        public const string Arch = "Arch / Tombstone";
        public const string Hexagon = "Hexagon";
        public const string Trapezoid = "Trapezoid";
        public const string Rhombus = "Rhombus / Diamond";
        public const string RightTriangle = "Right triangle";
        public const string Squircle = "Squircle";
        public const string Blob = "Organic blob";

        public static readonly string[] All = { Rectangle, Squircle, Oval, Arch, LShape, Triangle, RightTriangle, Rhombus, Trapezoid, Hexagon, Blob };

        // Curves all the way round, so they have no corners for the corner radius to round.
        private static bool HasNoCorners(string? shape) => shape is Oval or Squircle or Blob;

        public static (double X, double Y)[]? Outline(CellGeometryDto? geometry) => geometry == null
            ? null
            : Outline(geometry.Shape, geometry.ShapeFlipH, geometry.ShapeFlipV, geometry.NotchX, geometry.NotchY);

        public static (double X, double Y)[]? Outline(string? shape, bool flipH = false, bool flipV = false, double notchX = 0.5, double notchY = 0.5)
        {
            (double X, double Y)[]? points = shape switch
            {
                LShape => new (double, double)[] { (0, 0), (notchX, 0), (notchX, notchY), (1, notchY), (1, 1), (0, 1) },
                Triangle => new (double, double)[] { (0.5, 0), (1, 1), (0, 1) },
                Oval => OvalPoints(72),
                Hexagon => new (double, double)[] { (0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5) },
                Trapezoid => new (double, double)[] { (0.2, 0), (0.8, 0), (1, 1), (0, 1) },
                Rhombus => new (double, double)[] { (0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5) },
                RightTriangle => new (double, double)[] { (0, 0), (1, 1), (0, 1) },
                Arch => ArchPoints(36),
                Squircle => SquirclePoints(96),
                Blob => BlobPoints(120),
                _ => null
            };
            if (points == null) return null;
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = (flipH ? 1 - points[i].X : points[i].X, flipV ? 1 - points[i].Y : points[i].Y);
            }
            return points;
        }

        /// <summary>The outline placed in a rectangle, with its corners rounded. An oval has no corners to round. Null for a rectangle.</summary>
        /// <param name="inset">Moves every edge inward by this distance, so a gutter is the same width all round.</param>
        /// <param name="concaveRadius">The radius of inward corners, such as the inner corner of an L. Defaults to <paramref name="radius"/>.</param>
        public static (double X, double Y)[]? Placed(CellGeometryDto? geometry, double x, double y, double width, double height,
            double radius = 0, double inset = 0, double? concaveRadius = null)
        {
            var unit = Outline(geometry);
            if (unit == null) return null;
            var points = unit.Select(p => (X: x + p.X * width, Y: y + p.Y * height)).ToArray();
            if (inset > 0) points = Inset(points, inset);
            double inward = concaveRadius ?? radius;
            return (radius > 0 || inward > 0) && !HasNoCorners(geometry!.Shape) ? Rounded(points, radius, inward) : points;
        }

        private static double SignedArea((double X, double Y)[] points)
        {
            double sum = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        }

        /// <summary>Each edge moved inward by the distance. A distance the shape cannot take is reduced until it can.</summary>
        private static (double X, double Y)[] Inset((double X, double Y)[] points, double distance)
        {
            double area = SignedArea(points);
            if (Math.Abs(area) < 1) return points;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var moved = MoveEdges(points, distance, area > 0 ? 1 : -1);
                double movedArea = SignedArea(moved);
                if (Math.Sign(movedArea) == Math.Sign(area) && Math.Abs(movedArea) >= 0.02 * Math.Abs(area) && !TurnedInsideOut(points, moved))
                    return moved;
                distance *= 0.7;
            }
            return points;
        }

        /// <summary>
        /// A shape inset past its middle comes out upside down, with the same area sign, so edges pointing backwards are
        /// the test. A tight curve may reverse a few tiny edges and still look right.
        /// </summary>
        private static bool TurnedInsideOut((double X, double Y)[] points, (double X, double Y)[] moved)
        {
            double total = 0, reversed = 0;
            for (int i = 0; i < points.Length; i++)
            {
                int next = (i + 1) % points.Length;
                double dx = points[next].X - points[i].X, dy = points[next].Y - points[i].Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                total += length;
                if (dx * (moved[next].X - moved[i].X) + dy * (moved[next].Y - moved[i].Y) < 0)
                    reversed += length;
            }
            return reversed > 0.1 * total;
        }

        private static (double X, double Y)[] MoveEdges((double X, double Y)[] points, double distance, int side)
        {
            var moved = new (double X, double Y)[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var prev = points[(i + points.Length - 1) % points.Length];
                var at = points[i];
                var next = points[(i + 1) % points.Length];
                var n1 = InwardNormal(prev, at, side);
                var n2 = InwardNormal(at, next, side);
                // The corner where the two moved edges meet.
                double denominator = 1 + n1.X * n2.X + n1.Y * n2.Y;
                if (denominator < 0.05) denominator = 0.05;
                moved[i] = (at.X + distance * (n1.X + n2.X) / denominator, at.Y + distance * (n1.Y + n2.Y) / denominator);
            }
            return moved;
        }

        private static (double X, double Y) InwardNormal((double X, double Y) from, (double X, double Y) to, int side)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) return (0, 0);
            return (-dy / length * side, dx / length * side);
        }

        /// <summary>
        /// The size the photo needs, along its own axes, to cover the window's shape.
        /// Only the shape has to be covered, so an oval needs less zoom than its rectangle would.
        /// </summary>
        public static (double Width, double Height) CoverBox(CellGeometryDto geometry, double width, double height)
        {
            var points = Outline(geometry) ?? new (double X, double Y)[] { (0, 0), (1, 0), (1, 1), (0, 1) };
            double radians = geometry.ImageAngle * Math.PI / 180;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double reachX = 0;
            double reachY = 0;
            foreach (var p in points)
            {
                double x = (p.X - 0.5) * width;
                double y = (p.Y - 0.5) * height;
                reachX = Math.Max(reachX, Math.Abs(x * cos + y * sin));
                reachY = Math.Max(reachY, Math.Abs(-x * sin + y * cos));
            }
            return (reachX * 2, reachY * 2);
        }

        private static (double X, double Y)[] Rounded((double X, double Y)[] points, double radius, double concaveRadius)
        {
            const int steps = 8;
            int winding = Math.Sign(SignedArea(points));
            var result = new List<(double X, double Y)>(points.Length * (steps + 1));
            for (int i = 0; i < points.Length; i++)
            {
                var prev = points[(i + points.Length - 1) % points.Length];
                var at = points[i];
                var next = points[(i + 1) % points.Length];
                double toPrev = Math.Sqrt((prev.X - at.X) * (prev.X - at.X) + (prev.Y - at.Y) * (prev.Y - at.Y));
                double toNext = Math.Sqrt((next.X - at.X) * (next.X - at.X) + (next.Y - at.Y) * (next.Y - at.Y));
                double turn = (at.X - prev.X) * (next.Y - at.Y) - (at.Y - prev.Y) * (next.X - at.X);
                bool inward = Math.Sign(turn) == -winding;
                double cut = Math.Min(inward ? concaveRadius : radius, Math.Min(toPrev, toNext) / 2);
                if (cut < 0.01)
                {
                    result.Add(at);
                    continue;
                }
                var start = (X: at.X + (prev.X - at.X) / toPrev * cut, Y: at.Y + (prev.Y - at.Y) / toPrev * cut);
                var end = (X: at.X + (next.X - at.X) / toNext * cut, Y: at.Y + (next.Y - at.Y) / toNext * cut);
                for (int s = 0; s <= steps; s++)
                {
                    double t = s / (double)steps;
                    double a = (1 - t) * (1 - t), b = 2 * (1 - t) * t, c = t * t;
                    result.Add((a * start.X + b * at.X + c * end.X, a * start.Y + b * at.Y + c * end.Y));
                }
            }
            return result.ToArray();
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

        /// <summary>A half oval across the top half of the window, on straight sides.</summary>
        private static (double X, double Y)[] ArchPoints(int arcSteps)
        {
            var points = new (double X, double Y)[arcSteps + 3];
            points[0] = (0, 1);
            for (int i = 0; i <= arcSteps; i++)
            {
                double a = Math.PI + Math.PI * i / arcSteps;
                points[i + 1] = (0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a));
            }
            points[arcSteps + 2] = (1, 1);
            return points;
        }

        /// <summary>A superellipse of power 4, the soft square of app icons.</summary>
        private static (double X, double Y)[] SquirclePoints(int count)
        {
            var points = new (double X, double Y)[count];
            for (int i = 0; i < count; i++)
            {
                double a = 2 * Math.PI * i / count;
                double cos = Math.Cos(a), sin = Math.Sin(a);
                points[i] = (0.5 + 0.5 * Math.Sign(cos) * Math.Sqrt(Math.Abs(cos)), 0.5 + 0.5 * Math.Sign(sin) * Math.Sqrt(Math.Abs(sin)));
            }
            return points;
        }

        /// <summary>A fixed pebble: a circle with three slow waves on its radius, stretched to touch every side of the window.</summary>
        private static (double X, double Y)[] BlobPoints(int count)
        {
            var points = new (double X, double Y)[count];
            for (int i = 0; i < count; i++)
            {
                double a = 2 * Math.PI * i / count;
                double r = 1 + 0.15 * Math.Sin(2 * a + 0.6) + 0.09 * Math.Sin(3 * a + 2.1) + 0.04 * Math.Sin(5 * a + 4.0);
                points[i] = (r * Math.Cos(a), r * Math.Sin(a));
            }
            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            for (int i = 0; i < count; i++)
                points[i] = ((points[i].X - minX) / (maxX - minX), (points[i].Y - minY) / (maxY - minY));
            return points;
        }
    }

    /// <summary>Pulls a dragged floating cell onto nearby edges and centres.</summary>
    public static class FreeformSnap
    {
        public const double Tolerance = 6;

        /// <summary>The start that lines an edge or the centre of this span up with the nearest line, and that line. Null when none is close.</summary>
        public static (double Start, double? Guide) Snap(double start, double size, IEnumerable<double> lines, double tolerance = Tolerance)
        {
            double best = tolerance;
            double result = start;
            double? guide = null;
            foreach (double line in lines)
            {
                foreach (double part in new[] { 0, size / 2, size })
                {
                    double gap = Math.Abs(start + part - line);
                    if (gap < best)
                    {
                        best = gap;
                        result = line - part;
                        guide = line;
                    }
                }
            }
            return (result, guide);
        }
    }

    /// <summary>The one set of swatches for strokes, gradient ends and mats. Each must read as its own colour.</summary>
    public static class FramePalette
    {
        public static readonly (string Label, string Hex)[] Colors =
        {
            ("White", "#FFFFFFFF"),
            ("Black", "#FF000000"),
            ("Grey", "#FF808080"),
            ("Gold", "#FFFFC83B"),
            ("Red", "#FFD13438"),
            ("Green", "#FF2E9E5B"),
            ("Blue", "#FF0078D4"),
            ("Purple", "#FF8764B8")
        };

        /// <summary>The same set, with the white a new mat starts on, so it shows as chosen.</summary>
        public static readonly (string Label, string Hex)[] MatColors =
            Colors.Select(c => c.Label == "White" ? (c.Label, "#FFF8F8F8") : c).ToArray();

        public static (string Label, string Hex)[] WithClear(string label, (string Label, string Hex)[] colors) =>
            new[] { (label, "#00000000") }.Concat(colors).ToArray();

        /// <summary>Straight-line distance between two colours. Black to white is about 442.</summary>
        public static double Distance(string a, string b)
        {
            var x = FrameColor.Parse(a);
            var y = FrameColor.Parse(b);
            double dr = x.R - y.R, dg = x.G - y.G, db = x.B - y.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
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
            ("Black", DefaultColor),
            ("White", "#FFFFFFFF"),
            ("Grey", "#FF808080"),
            ("Sand", "#FFC8B08A"),
            ("Slate", "#FF46586B"),
            ("Navy", "#FF14306B"),
            ("Forest", "#FF1F5A3D"),
            ("Burgundy", "#FF6E1F35")
        };

        public static string Describe(string surface) => surface switch
        {
            Gradient => "The colour fades from corner to corner",
            Vignette => "The colour fades from the middle to the edges",
            _ => "One flat colour"
        };

        /// <summary>The far colour of a gradient or vignette: much darker, or much lighter when the colour is already dark.</summary>
        public static FrameColor.Rgba FarColor(string? hex)
        {
            var c = FrameColor.Parse(hex);
            double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            if (luminance < 0.25)
            {
                return new FrameColor.Rgba(c.A, Mix(c.R, 255, 0.45), Mix(c.G, 255, 0.45), Mix(c.B, 255, 0.45));
            }
            return new FrameColor.Rgba(c.A, Mix(c.R, 0, 0.7), Mix(c.G, 0, 0.7), Mix(c.B, 0, 0.7));
        }

        private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + (to - from) * amount);
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

        /// <summary>True when the tree places each of these photos exactly once, and every split has a weight for each part.</summary>
        public bool FitsPhotos(int count)
        {
            var seen = new HashSet<int>();
            return Check(this, seen, count) && seen.Count == count;
        }

        private static bool Check(MosaicNodeDto node, HashSet<int> seen, int count)
        {
            if (node.Leaf >= 0) return node.Leaf < count && seen.Add(node.Leaf);
            if (node.Children == null || node.Children.Count == 0) return false;
            if (node.Weights == null || node.Weights.Count != node.Children.Count) return false;
            return node.Children.All(child => child != null && Check(child, seen, count));
        }

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

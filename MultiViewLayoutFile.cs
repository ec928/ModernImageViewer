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

        [JsonPropertyName("border")]
        public CellBorderDto? Border { get; set; }
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
            RimGlow = RimGlow
        };
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

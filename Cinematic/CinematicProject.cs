using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernImageViewer.Cinematic.Data
{
    public class CinematicProject
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("globalDefaults")]
        public GlobalDefaults GlobalDefaults { get; set; } = new();

        [JsonPropertyName("ledger")]
        public Dictionary<string, SlideSequenceDefinition> Ledger { get; set; } = new();

        public SlideSettings GetEffectiveSettings(string fileName)
        {
            if (Ledger.TryGetValue(fileName, out var overrideDef) && overrideDef.IsUserOverridden)
            {
                return new SlideSettings
                {
                    DurationSeconds = overrideDef.DurationSeconds ?? GlobalDefaults.DurationSeconds,
                    IntensityPercent = overrideDef.IntensityPercent ?? GlobalDefaults.IntensityPercent,
                    TechniqueOverride = overrideDef.TechniqueOverride ?? GlobalDefaults.TechniqueOverride,
                    BeatCount = overrideDef.BeatCount ?? GlobalDefaults.BeatCount,
                    DirectionMode = overrideDef.DirectionOverride ?? GlobalDefaults.DirectionOverride,
                    UseTilt = GlobalDefaults.UseTilt,
                    UseNarrativeArc = GlobalDefaults.UseNarrativeArc,
                    FocusTargetRect = overrideDef.FocusTargetRect != null
                        ? (Windows.Foundation.Rect?)new Windows.Foundation.Rect(
                            overrideDef.FocusTargetRect.X,
                            overrideDef.FocusTargetRect.Y,
                            overrideDef.FocusTargetRect.Width,
                            overrideDef.FocusTargetRect.Height)
                        : null
                };
            }

            return new SlideSettings
            {
                DurationSeconds = GlobalDefaults.DurationSeconds,
                IntensityPercent = GlobalDefaults.IntensityPercent,
                TechniqueOverride = GlobalDefaults.TechniqueOverride,
                BeatCount = GlobalDefaults.BeatCount,
                DirectionMode = GlobalDefaults.DirectionOverride,
                UseTilt = GlobalDefaults.UseTilt,
                UseNarrativeArc = GlobalDefaults.UseNarrativeArc,
                FocusTargetRect = null
            };
        }
    }

    public class GlobalDefaults
    {
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; } = 12.0;

        [JsonPropertyName("intensityPercent")]
        public double IntensityPercent { get; set; } = 6.0;

        [JsonPropertyName("techniqueOverride")]
        public string TechniqueOverride { get; set; } = "Auto";

        [JsonPropertyName("beatCount")]
        public int BeatCount { get; set; } = 1;

        [JsonPropertyName("directionOverride")]
        public int DirectionOverride { get; set; } = 0;

        [JsonPropertyName("useTilt")]
        public bool UseTilt { get; set; } = true;

        [JsonPropertyName("useNarrativeArc")]
        public bool UseNarrativeArc { get; set; } = true;
    }

    public class SlideSequenceDefinition
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("techniqueOverride")]
        public string? TechniqueOverride { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double? DurationSeconds { get; set; }

        [JsonPropertyName("intensityPercent")]
        public double? IntensityPercent { get; set; }

        [JsonPropertyName("focusTargetRect")]
        public NormalizedRect? FocusTargetRect { get; set; }

        [JsonPropertyName("directionOverride")]
        public int? DirectionOverride { get; set; }

        [JsonPropertyName("beatCount")]
        public int? BeatCount { get; set; }

        [JsonPropertyName("isUserOverridden")]
        public bool IsUserOverridden { get; set; } = false;
    }

    public class NormalizedRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
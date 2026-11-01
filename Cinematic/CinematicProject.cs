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

        [JsonPropertyName("sequence")]
        public List<TrackClipRecord> Sequence { get; set; } = new();

        [JsonPropertyName("musicPath")]
        public string? MusicPath { get; set; }

        [JsonPropertyName("musicVolume")]
        public double MusicVolume { get; set; } = 60.0;

        public SlideSettings GetEffectiveSettings(string fileName)
        {
            var settings = new SlideSettings
            {
                IntensityPercent = GlobalDefaults.IntensityPercent,
                DirectionMode = GlobalDefaults.DirectionOverride,
                UseTilt = GlobalDefaults.UseTilt,
                UseNarrativeArc = GlobalDefaults.UseNarrativeArc
            };
            if (Ledger.TryGetValue(fileName, out var slide) && slide.IsUserOverridden)
            {
                settings.DurationSeconds = slide.DurationSeconds ?? 0;
                settings.DirectionMode = slide.DirectionOverride ?? GlobalDefaults.DirectionOverride;
                settings.FocusTargetRect = ToRect(slide.FocusTargetRect);
                settings.StartCrop = ToRect(slide.StartCrop);
                settings.EndCrop = ToRect(slide.EndCrop);
            }
            return settings;
        }

        /// <summary>A slide marked custom for settings that no longer exist has nothing left to say.</summary>
        public void DropEmptySlides()
        {
            foreach (var key in new List<string>(Ledger.Keys))
                if (!Ledger[key].HasAnything) Ledger.Remove(key);
        }

        private static Windows.Foundation.Rect? ToRect(NormalizedRect? r)
        {
            if (r == null) return null;
            return new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height);
        }
    }

    public class GlobalDefaults
    {
        [JsonPropertyName("intensityPercent")]
        public double IntensityPercent { get; set; } = 16.0;

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

        [JsonPropertyName("durationSeconds")]
        public double? DurationSeconds { get; set; }

        [JsonPropertyName("focusTargetRect")]
        public NormalizedRect? FocusTargetRect { get; set; }

        [JsonPropertyName("startCrop")]
        public NormalizedRect? StartCrop { get; set; }

        [JsonPropertyName("endCrop")]
        public NormalizedRect? EndCrop { get; set; }

        [JsonPropertyName("directionOverride")]
        public int? DirectionOverride { get; set; }

        [JsonPropertyName("isUserOverridden")]
        public bool IsUserOverridden { get; set; } = false;

        [JsonIgnore]
        public bool HasAnything => StartCrop != null || EndCrop != null || FocusTargetRect != null || DurationSeconds != null || DirectionOverride != null;
    }

    public class TrackClipRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("isHidden")]
        public bool IsHidden { get; set; }
    }

    public class NormalizedRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
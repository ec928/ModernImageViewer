using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// A sequenced, replayable presentation of one or more scenes.
    /// Unifies single-image Cinematic slideshows (where each scene has CellCount == 1)
    /// and Multi-Image Auto-Play collages into a single deterministic format.
    /// </summary>
    public sealed class ShowProject
    {
        public const string FileExtension = ".mivshow";
        public const string DefaultTitle = "Untitled Show";

        [JsonPropertyName("$schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("created")]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("modified")]
        public DateTime Modified { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("title")]
        public string Title { get; set; } = DefaultTitle;

        [JsonPropertyName("globalDefaults")]
        public ShowGlobalDefaults GlobalDefaults { get; set; } = new();

        [JsonPropertyName("soundtrack")]
        public ShowSoundtrackSettings Soundtrack { get; set; } = new();

        [JsonPropertyName("scenes")]
        public List<ShowScene> Scenes { get; set; } = new();

        [JsonIgnore]
        public int VisibleSceneCount => Scenes.Count(s => !s.IsHidden);

        [JsonIgnore]
        public double TotalEstimatedDuration =>
            Scenes.Where(s => !s.IsHidden)
                  .Sum(s => s.DwellSeconds ?? GlobalDefaults.DefaultDwellSeconds);

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        public static ShowProject? FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<ShowProject>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        public async Task SaveAsync(string filePath)
        {
            Modified = DateTime.UtcNow;
            string json = ToJson();
            await File.WriteAllTextAsync(filePath, json);
        }

        public static async Task<ShowProject?> LoadAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            string json = await File.ReadAllTextAsync(filePath);
            return FromJson(json);
        }
        /// <summary>Returns all unique photo paths referenced across all scenes in this project.</summary>
        public IEnumerable<string> GetPhotoPaths()
        {
            return Scenes.SelectMany(s => s.GetPhotoPaths()).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a legacy CinematicProject into a unified ShowProject where each clip
        /// is a ShowScene with CellCount == 1.
        /// </summary>
        public static ShowProject FromCinematicProject(Cinematic.Data.CinematicProject legacy, string? showTitle = null)
        {
            var project = new ShowProject
            {
                Title = showTitle ?? "Imported Cinematic Show",
                Soundtrack = new ShowSoundtrackSettings
                {
                    MusicFolder = legacy.MusicFolder,
                    SpecificTrackPath = legacy.MusicPath,
                    MusicLowestStars = legacy.MusicLowestStars,
                    MusicIncludesUnrated = legacy.MusicIncludesUnrated,
                    MusicOff = legacy.MusicOff,
                    MusicVolume = legacy.MusicVolume
                },
                GlobalDefaults = new ShowGlobalDefaults
                {
                    DefaultDwellSeconds = 10.0,
                    SmartPace = legacy.GlobalDefaults.UseNarrativeArc,
                    CameraMotionAmount = Math.Clamp(legacy.GlobalDefaults.IntensityPercent / 32.0, 0.0, 1.0)
                }
            };

            foreach (var clip in legacy.Sequence)
            {
                double? duration = null;
                Cinematic.Data.NormalizedRect? startCrop = null;
                Cinematic.Data.NormalizedRect? endCrop = null;

                if (legacy.Ledger.TryGetValue(clip.FilePath, out var slide) && slide.IsUserOverridden)
                {
                    if (slide.DurationSeconds > 0) duration = slide.DurationSeconds;
                    startCrop = slide.StartCrop;
                    endCrop = slide.EndCrop;
                }

                var scene = new ShowScene
                {
                    Id = string.IsNullOrEmpty(clip.Id) ? Guid.NewGuid().ToString() : clip.Id,
                    IsHidden = clip.IsHidden,
                    DwellSeconds = duration,
                    Recipe = "Solo"
                };

                // Create a single-photo MultiViewLayoutFile for the slide
                var layout = new MultiViewLayoutFile();
                layout.Canvas.Aspect = "16:9";
                layout.State.Lines = 1;
                layout.Photos.Add(new PhotoSlotDto
                {
                    Slot = 0,
                    Path = clip.FilePath
                });
                scene.Layout = layout;

                // Map start/end crops to CameraKeyframeOverride
                if (startCrop != null || endCrop != null)
                {
                    scene.CameraOverride = new CameraKeyframeOverride();
                    if (startCrop != null)
                    {
                        scene.CameraOverride.StartCrop = new NormalizedRectDto(startCrop.X, startCrop.Y, startCrop.Width, startCrop.Height);
                        double zoom = startCrop.Width > 0 ? 1.0 / startCrop.Width : 1.0;
                        scene.CameraOverride.Start = new CameraViewDto(zoom, startCrop.X + startCrop.Width / 2.0, startCrop.Y + startCrop.Height / 2.0);
                    }
                    if (endCrop != null)
                    {
                        scene.CameraOverride.EndCrop = new NormalizedRectDto(endCrop.X, endCrop.Y, endCrop.Width, endCrop.Height);
                        double zoom = endCrop.Width > 0 ? 1.0 / endCrop.Width : 1.0;
                        scene.CameraOverride.End = new CameraViewDto(zoom, endCrop.X + endCrop.Width / 2.0, endCrop.Y + endCrop.Height / 2.0);
                    }
                }

                project.Scenes.Add(scene);
            }

            return project;
        }
    }

    public sealed class ShowGlobalDefaults
    {
        [JsonPropertyName("defaultDwellSeconds")]
        public double DefaultDwellSeconds { get; set; } = 12.0;

        [JsonPropertyName("smartPace")]
        public bool SmartPace { get; set; } = true;

        [JsonPropertyName("cameraMotionAmount")]
        public double CameraMotionAmount { get; set; } = 0.5;

        [JsonPropertyName("cameraMode")]
        public string CameraMode { get; set; } = "Auto";

        [JsonPropertyName("defaultTransition")]
        public string DefaultTransition { get; set; } = "Crossfade";
    }

    public sealed class ShowSoundtrackSettings
    {
        [JsonPropertyName("musicFolder")]
        public string? MusicFolder { get; set; }

        [JsonPropertyName("specificTrackPath")]
        public string? SpecificTrackPath { get; set; }

        [JsonPropertyName("musicLowestStars")]
        public double MusicLowestStars { get; set; } = 3.5;

        [JsonPropertyName("musicIncludesUnrated")]
        public bool MusicIncludesUnrated { get; set; }

        [JsonPropertyName("musicOff")]
        public bool MusicOff { get; set; }

        [JsonPropertyName("musicVolume")]
        public double MusicVolume { get; set; } = 78.0;
    }

    public sealed class ShowScene
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>The complete layout composition (1 photo for Solo, or up to 16 for collage/wall).</summary>
        [JsonPropertyName("layout")]
        public MultiViewLayoutFile Layout { get; set; } = new();

        /// <summary>Specific hold duration in seconds; null inherits global default or smart pace.</summary>
        [JsonPropertyName("dwellSeconds")]
        public double? DwellSeconds { get; set; }

        /// <summary>Transition entering this scene: Crossfade, ZoomThrough, ThroughDark, Slide.</summary>
        [JsonPropertyName("transition")]
        public string Transition { get; set; } = "Crossfade";

        /// <summary>Manual Ken Burns override (null uses procedural CameraMove.Plan).</summary>
        [JsonPropertyName("cameraOverride")]
        public CameraKeyframeOverride? CameraOverride { get; set; }

        [JsonPropertyName("isHidden")]
        public bool IsHidden { get; set; }

        /// <summary>Informational descriptor of the layout recipe (Solo, Edge to edge, Gallery wall, Hero spotlight, etc.).</summary>
        [JsonPropertyName("recipe")]
        public string? Recipe { get; set; }

        [JsonIgnore]
        public int PhotoCount => (Layout.Photos?.Count ?? 0) + (Layout.Freeform?.Count ?? 0);

        [JsonIgnore]
        public bool IsSolo => PhotoCount == 1;

        /// <summary>Returns all photo paths referenced in this scene's grid and floating cells.</summary>
        public IEnumerable<string> GetPhotoPaths()
        {
            if (Layout.Photos != null)
            {
                foreach (var p in Layout.Photos)
                    if (!string.IsNullOrEmpty(p.Path)) yield return p.Path;
            }
            if (Layout.Freeform != null)
            {
                foreach (var f in Layout.Freeform)
                    if (!string.IsNullOrEmpty(f.Path)) yield return f.Path;
            }
        }

        public ShowScene Clone()
        {
            return new ShowScene
            {
                Id = Guid.NewGuid().ToString(),
                Title = Title,
                Layout = MultiViewLayoutFile.FromJson(Layout.ToJson()) ?? new MultiViewLayoutFile(),
                DwellSeconds = DwellSeconds,
                Transition = Transition,
                CameraOverride = CameraOverride != null ? new CameraKeyframeOverride
                {
                    Start = new CameraViewDto(CameraOverride.Start.Zoom, CameraOverride.Start.X, CameraOverride.Start.Y),
                    End = new CameraViewDto(CameraOverride.End.Zoom, CameraOverride.End.X, CameraOverride.End.Y),
                    StartCrop = CameraOverride.StartCrop != null ? new NormalizedRectDto(CameraOverride.StartCrop.X, CameraOverride.StartCrop.Y, CameraOverride.StartCrop.Width, CameraOverride.StartCrop.Height) : null,
                    EndCrop = CameraOverride.EndCrop != null ? new NormalizedRectDto(CameraOverride.EndCrop.X, CameraOverride.EndCrop.Y, CameraOverride.EndCrop.Width, CameraOverride.EndCrop.Height) : null
                } : null,
                IsHidden = IsHidden,
                Recipe = Recipe
            };
        }
    }

    public sealed class CameraKeyframeOverride
    {
        [JsonPropertyName("start")]
        public CameraViewDto Start { get; set; } = new(1.0, 0.5, 0.5);

        [JsonPropertyName("end")]
        public CameraViewDto End { get; set; } = new(1.0, 0.5, 0.5);

        [JsonPropertyName("startCrop")]
        public NormalizedRectDto? StartCrop { get; set; }

        [JsonPropertyName("endCrop")]
        public NormalizedRectDto? EndCrop { get; set; }

        public CameraKeyframeOverride() { }

        public CameraKeyframeOverride(CameraViewDto start, CameraViewDto end)
        {
            Start = start;
            End = end;
        }

        public CameraMove ToCameraMove(double seconds)
        {
            var startView = new CameraView(Start.Zoom, Start.X, Start.Y).Covered();
            var endView = new CameraView(End.Zoom, End.X, End.Y).Covered();
            var kind = End.Zoom > Start.Zoom ? CameraKind.ZoomIn : End.Zoom < Start.Zoom ? CameraKind.ZoomOut : CameraKind.Drift;
            return new CameraMove(kind, startView, endView, seconds);
        }
    }

    public sealed class CameraViewDto
    {
        [JsonPropertyName("zoom")]
        public double Zoom { get; set; } = 1.0;

        [JsonPropertyName("x")]
        public double X { get; set; } = 0.5;

        [JsonPropertyName("y")]
        public double Y { get; set; } = 0.5;

        public CameraViewDto() { }

        public CameraViewDto(double zoom, double x, double y)
        {
            Zoom = zoom;
            X = x;
            Y = y;
        }
    }

    public sealed class NormalizedRectDto
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("height")]
        public double Height { get; set; }

        public NormalizedRectDto() { }

        public NormalizedRectDto(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}

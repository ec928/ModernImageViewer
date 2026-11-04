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

        /// <summary>
        /// The overrides the Layout Generator used for this show, by name ("Cinematic"), so a scene added to it later is made
        /// the same way. Null: the main Layout Generator settings.
        /// </summary>
        public string? GeneratorOverrides { get; set; }

        [JsonIgnore]
        public int VisibleSceneCount => Scenes.Count(s => !s.IsHidden);

        [JsonIgnore]
        public double TotalEstimatedDuration =>
            Scenes.Where(s => !s.IsHidden)
                  .Sum(s => s.DwellSeconds ?? GlobalDefaults.DefaultDwellSeconds);

        /// <summary>
        /// The canvas is stored into a scene when the user asked to, or when this scene is the one on the canvas.
        /// A selected scene that is not showing is left as it was.
        /// </summary>
        public static bool ShouldStoreCanvas(string? canvasSceneId, string? sceneId, bool explicitCommit)
        {
            if (string.IsNullOrEmpty(sceneId)) return false;
            if (explicitCommit) return true;
            return string.Equals(canvasSceneId, sceneId, StringComparison.Ordinal);
        }

        /// <summary>The transport button names the action. While a show or a mix is running, that action is pause.</summary>
        public static string PlayTransportText(bool playing, bool storyboard, int sceneNumber, int sceneCount)
        {
            if (playing) return "Pause";
            if (!storyboard) return "Play Mix";
            if (sceneNumber > 1 && sceneNumber <= sceneCount)
                return $"Play Show · {sceneNumber}/{sceneCount}";
            return "Play Show";
        }

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
        /// <summary>The images in <paramref name="pool"/> that no scene shows, in the pool's order, so a new scene can grow the show.</summary>
        public List<string> ImagesNotInShow(IEnumerable<string> pool)
        {
            var used = new HashSet<string>(GetPhotoPaths(), StringComparer.OrdinalIgnoreCase);
            return pool.Where(p => !string.IsNullOrEmpty(p) && !used.Contains(p))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
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

        /// <summary>What a scene card is read out as, rather than the type's name.</summary>
        public override string ToString() => Title ?? Recipe ?? "Scene";

        [JsonIgnore]
        public int PhotoCount
        {
            get
            {
                int framed = Layout?.State?.FramedSlots ?? 0;
                int slots = 0;
                if (Layout?.Photos != null)
                {
                    foreach (var photo in Layout.Photos)
                    {
                        // Slots at or past FramedSlots are the rest of the image pool, kept so the
                        // session can put them back. They are not photos in this scene.
                        if (framed > 0 && photo.Slot >= framed) continue;
                        slots++;
                    }
                }
                return slots + (Layout?.Freeform?.Count ?? 0);
            }
        }

        [JsonIgnore]
        public bool IsSolo => PhotoCount == 1;

        /// <summary>
        /// Photos this scene shows. A scene that has never been shown has FramedSlots 0, and every
        /// path is on the canvas. Later slots are the image pool beside that canvas.
        /// </summary>
        public IEnumerable<string> GetPhotoPaths()
        {
            int framed = Layout?.State?.FramedSlots ?? 0;
            if (Layout?.Photos != null)
            {
                foreach (var photo in Layout.Photos)
                {
                    if (string.IsNullOrEmpty(photo.Path)) continue;
                    if (framed > 0 && photo.Slot >= framed) continue;
                    yield return photo.Path;
                }
            }
            if (Layout?.Freeform != null)
            {
                foreach (var cell in Layout.Freeform)
                    if (!string.IsNullOrEmpty(cell.Path)) yield return cell.Path;
            }
        }

        /// <summary>
        /// Where each image of this scene sits, as shares of the canvas, so a scene card can draw the scene's own split.
        /// Grid images follow the saved split, or the line counts when none was saved. Floating images come last, on top.
        /// </summary>
        public IReadOnlyList<(string Path, double X, double Y, double W, double H)> ThumbnailBoxes()
        {
            var boxes = new List<(string Path, double X, double Y, double W, double H)>();
            var layout = Layout;
            if (layout?.Photos == null) return boxes;

            int framed = layout.State?.FramedSlots ?? 0;
            var grid = layout.Photos.Where(p => framed <= 0 || p.Slot < framed).OrderBy(p => p.Slot).ToList();
            int n = Math.Min(grid.Count, MosaicLayout.MaxShown);
            if (n > 0)
            {
                MosaicLayout tree;
                if (layout.Mosaic != null && layout.Mosaic.FitsPhotos(n))
                {
                    tree = layout.Mosaic.ToMosaicLayout();
                }
                else
                {
                    var saved = layout.State?.LineCounts;
                    int[] counts = saved != null && saved.Count > 0 && saved.All(c => c > 0) && saved.Sum() == n
                        ? saved.ToArray()
                        : MosaicLayout.LineCounts(n, layout.State?.Lines ?? MosaicLayout.DefaultLines(n));
                    tree = MosaicLayout.FromCounts(counts, rows: !(layout.State?.Flipped ?? false));
                }
                foreach (var (leaf, box) in MosaicMath.Boxes(tree, 1, 1).OrderBy(b => b.Key))
                {
                    if (leaf < 0 || leaf >= n || string.IsNullOrEmpty(grid[leaf].Path)) continue;
                    boxes.Add((grid[leaf].Path, box.X, box.Y, box.W, box.H));
                }
            }

            double width = layout.Canvas?.Width ?? 0;
            double height = layout.Canvas?.Height ?? 0;
            if (layout.Freeform != null && width > 0 && height > 0)
            {
                foreach (var cell in layout.Freeform.OrderBy(c => c.ZIndex))
                {
                    if (string.IsNullOrEmpty(cell.Path)) continue;
                    boxes.Add((cell.Path, cell.X / width, cell.Y / height, cell.Width / width, cell.Height / height));
                }
            }
            return boxes;
        }

        /// <summary>Drops image-pool slots that were written into this scene by showing it.</summary>
        public void KeepCanvasPhotos()
        {
            int framed = Layout?.State?.FramedSlots ?? 0;
            if (framed <= 0 || Layout?.Photos == null) return;
            Layout.Photos.RemoveAll(photo => photo.Slot >= framed);
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
                // The whole path, curve, bend, turn and later shots included: copied field by field, those were lost.
                CameraOverride = CameraOverride?.Copy(),
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

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        /// <summary>The first shot's curve, by name (<see cref="CameraCurve"/>). None, as in files from before, is Smooth.</summary>
        [JsonPropertyName("curve")]
        public string? Curve { get; set; }

        /// <summary>How far the first shot's path bends to one side, as a share of its length. 0 is straight.</summary>
        [JsonPropertyName("bow")]
        public double Bow { get; set; }

        /// <summary>The first shot's share of the scene's time, against the shots after it. Alone, it is all of it.</summary>
        [JsonPropertyName("share")]
        public double Share { get; set; } = 1;

        /// <summary>Shots after the first, in order. None, as in files from before, is a one-shot scene.</summary>
        [JsonPropertyName("shots")]
        public List<CameraShotDto>? Shots { get; set; }

        public CameraKeyframeOverride() { }

        public CameraKeyframeOverride(CameraViewDto start, CameraViewDto end, string? kind = null)
        {
            Start = start;
            End = end;
            Kind = kind;
        }

        public int ShotCount => 1 + (Shots?.Count ?? 0);

        /// <summary>Every shot, the first included, with the start of each: a shot that continues starts where the one before ended.</summary>
        public List<CameraShot> ToShots()
        {
            static CameraView View(CameraViewDto d) => new CameraView(d.Zoom, d.X, d.Y, d.Angle).Covered();
            var shots = new List<CameraShot> { new(View(Start), View(End), Share > 0 ? Share : 1, CameraCurves.Parse(Curve), Bow) };
            foreach (var s in Shots ?? new List<CameraShotDto>())
            {
                var from = s.From != null ? View(s.From) : shots[^1].To;
                shots.Add(new CameraShot(from, View(s.To), s.Share > 0 ? s.Share : 1, CameraCurves.Parse(s.Curve), s.Bow));
            }
            return shots;
        }

        public CameraMove ToCameraMove(double seconds)
        {
            var shots = ToShots();
            var startView = shots[0].From;
            var endView = shots[^1].To;
            var kind = endView.Zoom > startView.Zoom ? CameraKind.ZoomIn : endView.Zoom < startView.Zoom ? CameraKind.ZoomOut : CameraKind.Drift;
            return new CameraMove(kind, shots, seconds);
        }

        /// <summary>A planned move as a scene stores it, every shot kept. A shot that starts where the one before ended continues; any other is a cut.</summary>
        public static CameraKeyframeOverride FromMove(CameraMove move, string? kind)
        {
            static CameraViewDto Dto(CameraView v) => new(v.Zoom, v.X, v.Y) { Angle = v.Angle };
            var first = move.Shots[0];
            var made = new CameraKeyframeOverride(Dto(first.From), Dto(first.To), kind)
            {
                Curve = first.Curve.ToString(),
                Bow = first.Bow,
                Share = first.Share,
                StartCrop = Crop(first.From),
                EndCrop = Crop(move.Shots[^1].To)
            };
            for (int i = 1; i < move.Shots.Count; i++)
            {
                var s = move.Shots[i];
                bool cut = s.From != move.Shots[i - 1].To;
                (made.Shots ??= new List<CameraShotDto>()).Add(new CameraShotDto
                {
                    From = cut ? Dto(s.From) : null,
                    To = Dto(s.To),
                    Share = s.Share,
                    Curve = s.Curve.ToString(),
                    Bow = s.Bow
                });
            }
            return made;
        }

        /// <summary>The part of the canvas a view shows, as Cinematic shows describe a frame.</summary>
        public static NormalizedRectDto Crop(CameraView view)
        {
            double w = 1.0 / Math.Max(1.0, view.Zoom);
            return new NormalizedRectDto(Math.Clamp(view.X - w / 2, 0, 1 - w), Math.Clamp(view.Y - w / 2, 0, 1 - w), w, w);
        }

        /// <summary>A separate copy, every shot included, so editing one scene never changes another.</summary>
        public CameraKeyframeOverride Copy() =>
            System.Text.Json.JsonSerializer.Deserialize<CameraKeyframeOverride>(System.Text.Json.JsonSerializer.Serialize(this)) ?? new CameraKeyframeOverride();
    }

    /// <summary>A shot after a scene's first. With no From, it continues from where the shot before ended; with one, it is a cut.</summary>
    public sealed class CameraShotDto
    {
        [JsonPropertyName("from")]
        public CameraViewDto? From { get; set; }

        [JsonPropertyName("to")]
        public CameraViewDto To { get; set; } = new(1.0, 0.5, 0.5);

        [JsonPropertyName("share")]
        public double Share { get; set; } = 1;

        [JsonPropertyName("curve")]
        public string? Curve { get; set; }

        [JsonPropertyName("bow")]
        public double Bow { get; set; }
    }

    public sealed class CameraViewDto
    {
        [JsonPropertyName("zoom")]
        public double Zoom { get; set; } = 1.0;

        [JsonPropertyName("x")]
        public double X { get; set; } = 0.5;

        [JsonPropertyName("y")]
        public double Y { get; set; } = 0.5;

        /// <summary>How far the frame is turned, in degrees. 0, as in files from before, is straight.</summary>
        [JsonPropertyName("angle")]
        public double Angle { get; set; }

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

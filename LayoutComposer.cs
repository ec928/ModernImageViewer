using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    /// <summary>A share of a picture: 0 to 1 across and down.</summary>
    public readonly record struct UnitRect(double X, double Y, double W, double H)
    {
        public double Area => W * H;
    }

    /// <summary>What the composer needs to know about a picture. Faces and the focus are shares of the picture.</summary>
    public sealed record PhotoFacts(string Path, double Width, double Height, IReadOnlyList<UnitRect> Faces, double FocusX, double FocusY, string Color,
        ulong Look = 0, bool ScreenLike = false, double Sharpness = 1000, double Brightness = 0.5, bool Featured = false)
    {
        public double Aspect => Width / Math.Max(1, Height);
    }

    /// <summary>One cell of a composition, in canvas units, before it is written out as a layout.</summary>
    public sealed class ComposedCell
    {
        public PhotoFacts Photo { get; init; } = null!;
        public double X { get; init; }
        public double Y { get; init; }
        public double W { get; init; }
        public double H { get; init; }
        public double Rotation { get; init; }
        public bool Floating { get; init; }
        public CellBorderDto Frame { get; init; } = FrameLooks.Flush();
        public CellGeometryDto Geometry { get; init; } = new();
        public double PanX { get; set; }
        public double PanY { get; set; }
        public double Zoom { get; set; } = 1;
        public double ImageWidth { get; set; }
        public double ImageHeight { get; set; }
    }

    public sealed class Composition
    {
        public string Recipe { get; init; } = "";
        /// <summary>Back to front.</summary>
        public List<ComposedCell> Cells { get; init; } = new();
        public MosaicLayout? Mosaic { get; init; }
        public string CanvasColor { get; private set; } = CanvasLook.DefaultColor;
        public string CanvasSurface { get; private set; } = CanvasLook.Solid;
        public CellBorderDto CanvasFrame { get; private set; } = FrameLooks.Flush();
        public double Width { get; init; }
        public double Height { get; init; }
        public double Score { get; set; }
        /// <summary>Faces that a shape, a crop or another cell cuts into. The search avoids any it can.</summary>
        public int FacesCut { get; set; }
        /// <summary>Pictures mostly hidden by others on top, so they were placed for nothing.</summary>
        public int PicturesBuried { get; set; }

        public void SetCanvas(string color, string surface, CellBorderDto frame)
        {
            CanvasColor = color;
            CanvasSurface = surface;
            CanvasFrame = frame;
        }

        /// <summary>The user's own canvas in place of the design's. A card's color would hide it, so its gaps go clear.</summary>
        public void KeepCanvas(string color, string surface, CellBorderDto frame)
        {
            SetCanvas(color, surface, frame.Clone());
            foreach (var cell in Cells)
            {
                if (cell.Frame.GutterMaterial != "Solid") continue;
                cell.Frame.GutterMaterial = "None";
                cell.Frame.GutterColor = "#00000000";
            }
        }

        /// <summary>Where the camera may aim: the faces in the largest cells that have them, as shares of the canvas.</summary>
        public List<(double X, double Y)> CameraSubjects() => Cells
            .Where(c => c.Photo.Faces.Count > 0 && Width >= 1 && Height >= 1)
            .OrderByDescending(c => c.W * c.H)
            .Take(3)
            .Select(c => LayoutComposer.SubjectOnCanvas(c, Width, Height))
            .ToList();

        public MultiViewLayoutFile ToLayoutFile()
        {
            var file = new MultiViewLayoutFile
            {
                Canvas = new CanvasLayoutDto
                {
                    BackgroundColor = CanvasColor,
                    Surface = CanvasSurface,
                    Frame = CanvasFrame.Clone(),
                    Width = Width,
                    Height = Height
                },
                State = new LayoutStateDto { IsFillMode = true }
            };

            var grid = Cells.Where(c => !c.Floating).ToList();
            for (int i = 0; i < grid.Count; i++)
            {
                var cell = grid[i];
                file.Photos.Add(new PhotoSlotDto
                {
                    Slot = i,
                    Path = cell.Photo.Path,
                    Name = System.IO.Path.GetFileName(cell.Photo.Path),
                    PanX = cell.PanX,
                    PanY = cell.PanY,
                    Zoom = cell.Zoom > 1 ? cell.Zoom : null,
                    Border = cell.Frame.Clone(),
                    Geometry = cell.Geometry.Clone()
                });
            }
            // A session always has a grid. Scattered prints lie on the bare canvas, over one empty cell.
            if (grid.Count == 0) file.Photos.Add(new PhotoSlotDto { Slot = 0, Path = string.Empty });
            file.State.Lines = Math.Max(1, MosaicLayout.DefaultLines(Math.Max(1, grid.Count)));
            if (Mosaic != null) file.Mosaic = MosaicNodeDto.FromMosaicLayout(Mosaic);

            var floating = Cells.Where(c => c.Floating).ToList();
            if (floating.Count > 0)
            {
                file.Freeform = new List<FreeformCellDto>();
                for (int i = 0; i < floating.Count; i++)
                {
                    var cell = floating[i];
                    file.Freeform.Add(new FreeformCellDto
                    {
                        SourceSlot = -1,
                        Path = cell.Photo.Path,
                        Name = System.IO.Path.GetFileName(cell.Photo.Path),
                        X = cell.X,
                        Y = cell.Y,
                        Width = cell.W,
                        Height = cell.H,
                        ZIndex = 5001 + i,
                        PanX = cell.PanX,
                        PanY = cell.PanY,
                        Zoom = cell.Zoom > 1 ? cell.Zoom : null,
                        Border = cell.Frame.Clone(),
                        Geometry = cell.Geometry.Clone()
                    });
                }
            }
            return file;
        }
    }

    /// <summary>
    /// What the user has asked of Surprise me: ranges, thresholds and odds, none of which rules anything out. Saved between
    /// sessions. Anything missing or out of range in the saved file comes back as its default or its limit.
    /// </summary>
    public sealed class SurpriseOptions
    {
        public const string CardAny = "Any";
        public const string CardWhite = "White";
        public const string CardBlack = "Black";
        public const string CardFromPictures = "From the pictures";
        public static readonly string[] Cards = { CardAny, CardWhite, CardBlack, CardFromPictures };

        public const string CameraAuto = "Auto";
        public const string CameraZoomIn = "Zoom in";
        public const string CameraZoomOut = "Zoom out";
        public const string CameraDrift = "Drift";
        public static readonly string[] CameraModes = { CameraAuto, CameraZoomIn, CameraZoomOut, CameraDrift };

        public const double RarestDesign = 0.1, MostFavouredDesign = 5;
        public const double MostOfAnyZoom = 5;
        public const int FewestAllowed = 1;

        /// <summary>How often each design comes up compared with the others.</summary>
        public Dictionary<string, double> DesignOdds { get; set; } = new();
        public HashSet<string> DisabledDesigns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsDesignOff(string design) => DisabledDesigns != null && DisabledDesigns.Contains(design);
        /// <summary>
        /// Layout zoom: how far an image is zoomed into its cell when the layout is made, toward its faces or, with none
        /// found, its focus point. Each zoomed image picks within LeastZoom to MostZoom; ZoomChance is how often an image
        /// is zoomed. Only two things hold it back: it never cuts a face, and never shows the photo past twice its own pixels.
        /// </summary>
        public double LeastZoom { get; set; } = 1.5;
        public double MostZoom { get; set; } = 4;
        public double ZoomChance { get; set; } = 0.7;
        public bool Zooms(Random random) => MostZoom > 1 && random.NextDouble() < ZoomChance;
        public double PickZoom(Random random)
        {
            double least = Math.Min(LeastZoom, MostZoom), most = Math.Max(LeastZoom, MostZoom);
            return least + random.NextDouble() * (most - least);
        }

        /// <summary>Camera zoom: how far a zoom move goes while a layout plays, with or without faces, as a range each zoom picks within.</summary>
        public double CameraZoomLeast { get; set; } = 1.15;
        public double CameraZoomMost { get; set; } = 1.8;
        public const double LeastCameraZoom = 1.05, MostCameraZoom = 2.5;
        /// <summary>How many of the best versions the pick is made from: 1 is always the best, more is more varied.</summary>
        public int Variety { get; set; } = 3;
        /// <summary>How much of a face must show for it to count as uncut.</summary>
        public double FaceShown { get; set; } = 0.85;
        public double MostTilt { get; set; } = 6.5;
        /// <summary>The hero's height as a share of the canvas: each layout picks within this range.</summary>
        public double HeroSmallest { get; set; } = 0.62;
        public double HeroLargest { get; set; } = 0.82;
        public const double HeroLowest = 0.3, HeroHighest = 1.0;

        /// <summary>
        /// The single "Hero size" of earlier versions, which was always used as a tenth either side of it, read only to
        /// convert a saved file to that range.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? HeroSize { get; set; }
        /// <summary>Frame width, for every frame: Gallery wall's card and gaps, a hero's outline, a print's border, a circle's outline.</summary>
        public double GapScale { get; set; } = 1;
        /// <summary>Frame color, for every frame. Any: each layout picks white, black or a tone from its images.</summary>
        public string Card { get; set; } = CardAny;

        /// <summary>
        /// Backdrop odds, for every design with a backdrop (an image shown whole, a hero, prints, circles), on the designs'
        /// Off to Often scale, 0 being off. Vignette and gradient at Normal are today's even split; solid comes up rarely.
        /// </summary>
        public double BackdropVignetteOdds { get; set; } = 1;
        public double BackdropGradientOdds { get; set; } = 1;
        public double BackdropSolidOdds { get; set; } = 0.25;
        public double BackdropAmbientBlurOdds { get; set; } = 0.25;

        /// <summary>The backdrop this layout gets, by the odds. With every one off, a plain colour.</summary>
        public string PickBackdrop(Random random)
        {
            double vignette = Math.Max(0, BackdropVignetteOdds), gradient = Math.Max(0, BackdropGradientOdds), solid = Math.Max(0, BackdropSolidOdds);
            double blur = Math.Max(0, BackdropAmbientBlurOdds);
            double sum = vignette + gradient + solid + blur;
            if (sum <= 0) return CanvasLook.Solid;
            double roll = random.NextDouble() * sum;
            return roll < vignette ? CanvasLook.Vignette
                : roll < vignette + gradient ? CanvasLook.Gradient
                : roll < vignette + gradient + solid ? CanvasLook.Solid
                : CanvasLook.AmbientBlur;
        }
        public bool KeepCanvas { get; set; }
        /// <summary>
        /// How much of an image a cell may crop away to fill itself, 0 to 1, measured against the cell's shape. Past it,
        /// or past Face kept in frame, the image is shown whole where the design can show it whole; a design that cannot
        /// (a circle, a hero shape, a leaning panel) takes no part in that call, as a design that cannot hold the count.
        /// Applies to a layout of one image.
        /// </summary>
        public double CropAtMost { get; set; } = 0.35;

        public int FewestPictures { get; set; } = 1;
        public int MostPictures { get; set; } = 9;

        /// <summary>With more pictures than a click may use, the clicks take turns through them.</summary>
        public bool TakesTurns(int pictures) => pictures > Math.Min(MosaicLayout.MaxShown, MostPictures);
        /// <summary>The odds that a click from a large set takes pictures next to each other in folder and date.</summary>
        public double TogetherChance { get; set; } = 0.8;
        public bool SkipScreens { get; set; } = true;
        /// <summary>0 is off. A saved setting like the rest: pausing auto-play does not change it.</summary>
        public int AutoPlaySeconds { get; set; } = 10;
        public static readonly int[] AutoPlayChoices = { 0, 3, 5, 8, 10, 15, 20, 30 };
        public static readonly int[] RandomPlayChoices = { 3, 5, 8, 10, 15, 20, 30, 45, 60 };
        /// <summary>The shortest and longest hold a saved setting may have, in seconds.</summary>
        public const int FewestSeconds = 3, MostSeconds = 60;
        public bool AutoPlayRandom { get; set; } = true;
        public bool SmartPace { get; set; } = true;
        public int RandomFewestSeconds { get; set; } = 10;
        public int RandomMostSeconds { get; set; } = 15;
        public string CameraMode { get; set; } = CameraAuto;
        [System.Text.Json.Serialization.JsonIgnore]
        public CameraKind? ForcedCameraKind => CameraMode switch
        {
            CameraZoomIn => CameraKind.ZoomIn,
            CameraZoomOut => CameraKind.ZoomOut,
            CameraDrift => CameraKind.Drift,
            _ => null
        };
        /// <summary>How far the camera moves over each layout, 0 to 1, as a range: each layout picks within it. 0 is still.</summary>
        public double MotionLeast { get; set; } = 0.4;
        public double MotionMost { get; set; } = 0.6;

        /// <summary>The single "Motion" of earlier versions, read only to convert a saved file to a range of that one value.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? MotionAmount { get; set; }

        public bool HasMotion => Math.Max(MotionLeast, MotionMost) > 0.001;

        public double PickMotion(Random random)
        {
            double least = Math.Min(MotionLeast, MotionMost), most = Math.Max(MotionLeast, MotionMost);
            return least + random.NextDouble() * (most - least);
        }

        /// <summary>
        /// How each move eases, as odds against each other in <see cref="CameraCurves.All"/>'s order: Steady, Gentle, Smooth,
        /// Cinematic, Settle, Snap. Gentle is the most common; Snap, on zooms only, starts off.
        /// </summary>
        public double CurveSteadyOdds { get; set; } = 1;
        public double CurveGentleOdds { get; set; } = MostFavouredDesign;
        public double CurveSmoothOdds { get; set; } = 1;
        public double CurveCinematicOdds { get; set; } = 0.5;
        public double CurveSettleOdds { get; set; } = 0.25;
        public double CurveSnapOdds { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public double[] CurveOdds => new[] { CurveSteadyOdds, CurveGentleOdds, CurveSmoothOdds, CurveCinematicOdds, CurveSettleOdds, CurveSnapOdds };

        /// <summary>How far a pan's path bends, 0 to 1 of Cinematic Slideshow's bend, as a range.</summary>
        public double PathBendLeast { get; set; }
        public double PathBendMost { get; set; } = 0.5;

        /// <summary>How far the view turns over a move, in degrees, as a range. Off by default.</summary>
        public double TurnLeast { get; set; }
        public double TurnMost { get; set; }
        public const double MostTurn = 6;

        /// <summary>The chance that a scene with a face, held long enough, is two shots: a wide shot, then a snap zoom or a cut to the face.</summary>
        public double TwoShotChance { get; set; } = 0.25;

        /// <summary>The chance that a scene plays no move at all. Off by default: a still adds a little variety, not much.</summary>
        public double StillChance { get; set; }

        /// <summary>
        /// Sequence beats: the odds that a step of a show is more than one scene, each a closer view of the one before (a
        /// group, then one image from it, then its faces). 25% by default (Ed, 7 Nov 2026): about one step in four.
        /// </summary>
        public double SequenceBeatsChance { get; set; } = 0.25;
        /// <summary>How many scenes a step that rolls Sequence beats may be, as a range. It stops early when no closer view is left.</summary>
        public int FewestScenesPerStep { get; set; } = 2;
        public int MostScenesPerStep { get; set; } = 2;
        public const int MostScenesInAStep = 4;

        /// <summary>Temporary diagnostic: Play writes its timings to play-timing.log. Off by default.</summary>
        public bool LogPlayTiming { get; set; }

        /// <summary>The camera settings a planned move follows. Two shots only when the camera mode is Auto: a chosen mode is one kind of move.</summary>
        public CameraStyle CameraStyle() => new(CurveOdds, PathBendLeast, PathBendMost, TurnLeast, TurnMost,
            string.Equals(CameraMode, CameraAuto, StringComparison.OrdinalIgnoreCase) ? TwoShotChance : 0,
            CameraZoomLeast, CameraZoomMost);
        /// <summary>The odds that it zooms rather than drifts.</summary>
        public double MotionZoomChance { get; set; } = 0.5;
        /// <summary>The odds that it aims at a cell with faces, where there is one.</summary>
        public double MotionFaceChance { get; set; } = 0.7;
        /// <summary>
        /// How each change of layout is made, as odds against each other on the designs' Off to Often scale, 0 being off.
        /// All off is a crossfade. The defaults keep a crossfade the most common and a slide, which stands out most, the rarest.
        /// </summary>
        public double CrossfadeOdds { get; set; } = MostFavouredDesign;
        public double ZoomThroughOdds { get; set; } = 1;
        public double ThroughDarkOdds { get; set; } = 1;
        public double SlideOdds { get; set; } = 0.25;

        /// <summary>
        /// The single "Transition variety" of earlier versions, read only to convert a saved file to the four odds. It
        /// capped the other kinds at 7 in 10, so its percentage never meant what it said.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? TransitionVariety { get; set; }

        public LayoutTransition PickTransition(LayoutTransition? last, Random random) =>
            LayoutTransitions.Pick(CrossfadeOdds, ZoomThroughOdds, ThroughDarkOdds, SlideOdds, last, random);

        /// <summary>The share of changes each kind takes in a long run, the no-repeat rule included.</summary>
        public IReadOnlyDictionary<LayoutTransition, double> TransitionShares() =>
            LayoutTransitions.Shares(CrossfadeOdds, ZoomThroughOdds, ThroughDarkOdds, SlideOdds);

        public const string ProgressBottom = "Bottom";
        public const string ProgressTop = "Top";
        public const string ProgressLeft = "Left";
        public const string ProgressRight = "Right";
        public static readonly string[] ProgressEdges = { ProgressBottom, ProgressTop, ProgressLeft, ProgressRight };

        public const string ProgressAccent = "Accent";
        public const string ProgressWhite = "White";
        public const string ProgressAmber = "Amber";
        public const string ProgressAqua = "Aqua";
        public const string ProgressRose = "Rose";
        public static readonly string[] ProgressColors = { ProgressAccent, ProgressWhite, ProgressAmber, ProgressAqua, ProgressRose };

        /// <summary>The line along a screen edge that fills while a layout holds. Off leaves the pictures clear.</summary>
        public bool ProgressOn { get; set; } = true;
        public string ProgressEdge { get; set; } = ProgressBottom;
        public string ProgressColor { get; set; } = ProgressAccent;

        /// <summary>How long Play shows each layout. Not playing is not a setting, so there is always a speed.</summary>
        public int PlaySeconds => AutoPlaySeconds > 0 ? AutoPlaySeconds : 5;

        public int NextPlaySeconds(Random rnd) =>
            AutoPlayRandom ? rnd.Next(Math.Min(RandomFewestSeconds, RandomMostSeconds), Math.Max(RandomFewestSeconds, RandomMostSeconds) + 1) : PlaySeconds;

        public int NextPlaySeconds(Random rnd, int picturesOnCanvas, ref bool? lastWasLong)
        {
            if (!AutoPlayRandom) return PlaySeconds;
            if (!SmartPace) return NextPlaySeconds(rnd);
            int low = Math.Min(RandomFewestSeconds, RandomMostSeconds);
            int high = Math.Max(RandomFewestSeconds, RandomMostSeconds);
            if (low == high) return low;
            bool takeLong = lastWasLong.HasValue ? !lastWasLong.Value : rnd.Next(2) == 0;
            lastWasLong = takeLong;
            double mid = (low + high) / 2.0;
            double bandLow = takeLong ? mid : low;
            double bandHigh = takeLong ? high : mid;
            double density = Math.Clamp((picturesOnCanvas - FewestAllowed) / (double)Math.Max(1, MosaicLayout.MaxShown - FewestAllowed), 0, 1);
            double pick = bandLow + (bandHigh - bandLow) * (0.35 * density + 0.65 * rnd.NextDouble());
            return Math.Clamp((int)Math.Round(pick), low, high);
        }

        /// <summary>
        /// Whether a hold of this many seconds was the long half of the range, so the next scene drafted on its own can take
        /// the other half, as a whole show drafted at once does. Null when it cannot tell.
        /// </summary>
        public bool? WasLong(double? seconds)
        {
            if (seconds is not double s || !AutoPlayRandom || !SmartPace) return null;
            int low = Math.Min(RandomFewestSeconds, RandomMostSeconds);
            int high = Math.Max(RandomFewestSeconds, RandomMostSeconds);
            if (low == high) return null;
            return s > (low + high) / 2.0;
        }

        /// <summary>The Layout Generator settings that Cinematic storyboard uses in place of these. Everything not in it comes from here.</summary>
        public CinematicOverrides Cinematic { get; set; } = new();

        /// <summary>My presets 1, 2 and 3: whatever the user saved into them from a show, the same full set of values. Empty until saved.</summary>
        public List<CinematicOverrides?> MyPresets { get; set; } = new() { null, null, null };

        /// <summary>
        /// The Multi-Image preset as the user last left it. Null until they change it on the Show card: Play then follows
        /// the engine settings. Kept in the app settings, with Cinematic and My presets.
        /// </summary>
        public CinematicOverrides? MultiImage { get; set; }

        /// <summary>The preset last chosen or kept on the Show card ("Multi-Image", "Cinematic", "My preset 1" to "3"): a new show starts from it.</summary>
        public string? LastPreset { get; set; }

        /// <summary>
        /// These settings with a set of overrides applied, by its name; this same object when the name is none or unknown.
        /// A copy, so the saved settings are never changed by it.
        /// </summary>
        public SurpriseOptions WithOverrides(string? overrides)
        {
            if (!string.Equals(overrides, CinematicOverrides.Name, StringComparison.OrdinalIgnoreCase)) return this;
            var copy = FromJson(ToJson());
            (Cinematic ?? new CinematicOverrides()).ApplyTo(copy);
            return copy;
        }

        public string PlayLabel => AutoPlayRandom ? $"{RandomFewestSeconds}\u2013{RandomMostSeconds} s" : $"{PlaySeconds} s";
        public string PlayWords => AutoPlayRandom ? $"{RandomFewestSeconds} to {RandomMostSeconds} seconds" : $"{PlaySeconds} seconds";

        public double OddsOf(string design) => DesignOdds.TryGetValue(design, out double odds) ? Math.Clamp(odds, RarestDesign, MostFavouredDesign) : 1;

        public static SurpriseOptions FromJson(string? json)
        {
            try
            {
                var read = string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<SurpriseOptions>(json);
                if (read == null) return new SurpriseOptions();
                read.DesignOdds ??= new Dictionary<string, double>();
                read.DisabledDesigns = read.DisabledDesigns != null
                    ? new HashSet<string>(read.DisabledDesigns.Where(d => LayoutComposer.Recipes.Contains(d)), StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!CameraModes.Contains(read.CameraMode)) read.CameraMode = CameraAuto;
                read.MostZoom = Math.Clamp(read.MostZoom, 1, MostOfAnyZoom);
                read.LeastZoom = Math.Clamp(read.LeastZoom, 1, read.MostZoom);
                read.CameraZoomLeast = Math.Clamp(read.CameraZoomLeast, LeastCameraZoom, MostCameraZoom);
                read.CameraZoomMost = Math.Clamp(read.CameraZoomMost, read.CameraZoomLeast, MostCameraZoom);
                read.ZoomChance = Math.Clamp(read.ZoomChance, 0, 1);
                read.Variety = Math.Clamp(read.Variety, 1, 12);
                read.FaceShown = Math.Clamp(read.FaceShown, 0.5, 1);
                read.MostTilt = Math.Clamp(read.MostTilt, 0, 20);
                if (read.HeroSize is double hero)
                {
                    double centre = Math.Clamp(hero, 0.4, 0.9);
                    read.HeroSmallest = centre - 0.1;
                    read.HeroLargest = centre + 0.1;
                    read.HeroSize = null;
                }
                read.HeroSmallest = Math.Clamp(read.HeroSmallest, HeroLowest, HeroHighest);
                read.HeroLargest = Math.Clamp(read.HeroLargest, read.HeroSmallest, HeroHighest);
                read.GapScale = Math.Clamp(read.GapScale, 0.25, 3);
                read.BackdropVignetteOdds = Math.Clamp(read.BackdropVignetteOdds, 0, MostFavouredDesign);
                read.BackdropGradientOdds = Math.Clamp(read.BackdropGradientOdds, 0, MostFavouredDesign);
                read.BackdropSolidOdds = Math.Clamp(read.BackdropSolidOdds, 0, MostFavouredDesign);
                read.BackdropAmbientBlurOdds = Math.Clamp(read.BackdropAmbientBlurOdds, 0, MostFavouredDesign);
                read.FewestPictures = Math.Clamp(read.FewestPictures, FewestAllowed, MosaicLayout.MaxShown);
                read.MostPictures = Math.Clamp(read.MostPictures, read.FewestPictures, MosaicLayout.MaxShown);
                read.TogetherChance = Math.Clamp(read.TogetherChance, 0, 1);
                read.CropAtMost = Math.Clamp(read.CropAtMost, 0, 1);
                if (!Cards.Contains(read.Card)) read.Card = CardAny;
                // Any whole second in range: the Duration slider sets values off the old menu lists, such as 7 to 12, and
                // checking against those lists put 5 to 15 back on the next launch.
                read.AutoPlaySeconds = read.AutoPlaySeconds <= 0 ? 0 : Math.Clamp(read.AutoPlaySeconds, FewestSeconds, MostSeconds);
                read.RandomFewestSeconds = Math.Clamp(read.RandomFewestSeconds, FewestSeconds, MostSeconds);
                read.RandomMostSeconds = Math.Clamp(read.RandomMostSeconds, FewestSeconds, MostSeconds);
                if (read.RandomMostSeconds < read.RandomFewestSeconds) read.RandomMostSeconds = read.RandomFewestSeconds;
                read.Cinematic = CinematicOverrides.Checked(read.Cinematic);
                // Null stays null: the user has not changed Multi-Image, so Play follows the engine settings.
                read.MultiImage = read.MultiImage == null ? null : CinematicOverrides.Checked(read.MultiImage);
                // Three slots, whatever the file held; an empty one stays empty.
                var presets = read.MyPresets ?? new List<CinematicOverrides?>();
                read.MyPresets = Enumerable.Range(0, 3).Select(i => i < presets.Count && presets[i] != null ? CinematicOverrides.Checked(presets[i]) : null).ToList();
                if (read.MotionAmount is double motion)
                {
                    read.MotionLeast = read.MotionMost = motion;
                    read.MotionAmount = null;
                }
                read.MotionLeast = Math.Clamp(read.MotionLeast, 0, 1);
                read.MotionMost = Math.Clamp(read.MotionMost, read.MotionLeast, 1);
                read.CurveSteadyOdds = Math.Clamp(read.CurveSteadyOdds, 0, MostFavouredDesign);
                read.CurveGentleOdds = Math.Clamp(read.CurveGentleOdds, 0, MostFavouredDesign);
                read.CurveSmoothOdds = Math.Clamp(read.CurveSmoothOdds, 0, MostFavouredDesign);
                read.CurveCinematicOdds = Math.Clamp(read.CurveCinematicOdds, 0, MostFavouredDesign);
                read.CurveSettleOdds = Math.Clamp(read.CurveSettleOdds, 0, MostFavouredDesign);
                read.CurveSnapOdds = Math.Clamp(read.CurveSnapOdds, 0, MostFavouredDesign);
                read.PathBendLeast = Math.Clamp(read.PathBendLeast, 0, 1);
                read.PathBendMost = Math.Clamp(read.PathBendMost, read.PathBendLeast, 1);
                read.TurnLeast = Math.Clamp(read.TurnLeast, 0, MostTurn);
                read.TurnMost = Math.Clamp(read.TurnMost, read.TurnLeast, MostTurn);
                read.TwoShotChance = Math.Clamp(read.TwoShotChance, 0, 1);
                read.StillChance = Math.Clamp(read.StillChance, 0, 1);
                read.SequenceBeatsChance = Math.Clamp(read.SequenceBeatsChance, 0, 1);
                read.FewestScenesPerStep = Math.Clamp(read.FewestScenesPerStep, 2, MostScenesInAStep);
                read.MostScenesPerStep = Math.Clamp(read.MostScenesPerStep, read.FewestScenesPerStep, MostScenesInAStep);
                read.MotionZoomChance = Math.Clamp(read.MotionZoomChance, 0, 1);
                read.MotionFaceChance = Math.Clamp(read.MotionFaceChance, 0, 1);
                // A file from before the four odds: about the mix its single slider gave, so nobody's look changes.
                if (read.TransitionVariety is double variety)
                {
                    (read.CrossfadeOdds, read.ZoomThroughOdds, read.ThroughDarkOdds, read.SlideOdds) = LayoutTransitions.OddsFromVariety(variety);
                    read.TransitionVariety = null;
                }
                read.CrossfadeOdds = Math.Clamp(read.CrossfadeOdds, 0, MostFavouredDesign);
                read.ZoomThroughOdds = Math.Clamp(read.ZoomThroughOdds, 0, MostFavouredDesign);
                read.ThroughDarkOdds = Math.Clamp(read.ThroughDarkOdds, 0, MostFavouredDesign);
                read.SlideOdds = Math.Clamp(read.SlideOdds, 0, MostFavouredDesign);
                if (string.IsNullOrEmpty(read.ProgressEdge) || !ProgressEdges.Contains(read.ProgressEdge)) read.ProgressEdge = ProgressBottom;
                if (string.IsNullOrEmpty(read.ProgressColor) || !ProgressColors.Contains(read.ProgressColor)) read.ProgressColor = ProgressAccent;
                return read;
            }
            catch (System.Text.Json.JsonException)
            {
                return new SurpriseOptions();
            }
        }

        public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
    }

    /// <summary>
    /// The Layout Generator settings Cinematic storyboard puts in place of the main ones, so the one generator makes
    /// layouts like Cinematic Slideshow's: one image filling the screen, holds alternating short and long between 5 and
    /// 20 seconds, and the camera always moving. Defaults, not fixed: each is a setting.
    /// </summary>
    public sealed class CinematicOverrides
    {
        public const string Name = "Cinematic";

        public int FewestPictures { get; set; } = 1;
        public int MostPictures { get; set; } = 1;
        /// <summary>
        /// The designs' odds for a cinematic storyboard, 0 being off. Edge to edge Often, Hero spotlight Rare, the rest
        /// off: prints, circles, cards and panels are not a film frame, and odds add up, so even the rarest four would
        /// put about one scene in thirteen on one of them. The user can turn any back on.
        /// </summary>
        public Dictionary<string, double> DesignOdds { get; set; } = DefaultDesignOdds();

        public static Dictionary<string, double> DefaultDesignOdds() => new(StringComparer.OrdinalIgnoreCase)
        {
            [LayoutComposer.EdgeToEdge] = SurpriseOptions.MostFavouredDesign,
            [LayoutComposer.HeroSpotlight] = 0.25,
            [LayoutComposer.GalleryWall] = 0,
            [LayoutComposer.ScatteredPrints] = 0,
            [LayoutComposer.Circles] = 0,
            [LayoutComposer.SlantedPanels] = 0
        };

        public double OddsFor(string design) => DesignOdds.TryGetValue(design, out double odds) ? odds : 0;
        public int FewestSeconds { get; set; } = 10;
        public int MostSeconds { get; set; } = 15;
        public bool SmartPace { get; set; } = true;
        public double MotionLeast { get; set; } = 0.4;
        public double MotionMost { get; set; } = 0.6;
        /// <summary>Sequence beats: 25% in both built-in presets. A user may set any value, including 0.</summary>
        public double SequenceBeatsChance { get; set; } = 0.25;
        public int FewestScenesPerStep { get; set; } = 2;
        public int MostScenesPerStep { get; set; } = 2;

        /// <summary>A preset holding the values a show is played with now: what Save as My preset stores.</summary>
        public static CinematicOverrides From(SurpriseOptions o)
        {
            var preset = new CinematicOverrides
            {
                FewestPictures = o.FewestPictures,
                MostPictures = o.MostPictures,
                FewestSeconds = o.AutoPlayRandom ? o.RandomFewestSeconds : o.AutoPlaySeconds,
                MostSeconds = o.AutoPlayRandom ? o.RandomMostSeconds : o.AutoPlaySeconds,
                SmartPace = o.SmartPace,
                MotionLeast = o.MotionLeast,
                MotionMost = o.MotionMost,
                SequenceBeatsChance = o.SequenceBeatsChance,
                FewestScenesPerStep = o.FewestScenesPerStep,
                MostScenesPerStep = o.MostScenesPerStep,
                DesignOdds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            };
            foreach (string design in LayoutComposer.Recipes) preset.DesignOdds[design] = o.IsDesignOff(design) ? 0 : o.OddsOf(design);
            return preset;
        }
        /// <summary>The single Motion of earlier versions, read only to convert a saved file.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? MotionAmount { get; set; }
        // Transitions and movement curves are not overridden: a Cinematic storyboard uses the main Transitions settings (Ed, 2026-11-04).

        public void ApplyTo(SurpriseOptions options)
        {
            options.FewestPictures = FewestPictures;
            options.MostPictures = MostPictures;
            options.DesignOdds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            options.DisabledDesigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string design in LayoutComposer.Recipes)
            {
                double odds = OddsFor(design);
                if (odds <= 0) options.DisabledDesigns.Add(design);
                else options.DesignOdds[design] = odds;
            }
            options.AutoPlayRandom = FewestSeconds < MostSeconds;
            options.AutoPlaySeconds = FewestSeconds;
            options.RandomFewestSeconds = FewestSeconds;
            options.RandomMostSeconds = MostSeconds;
            options.SmartPace = SmartPace;
            options.MotionLeast = MotionLeast;
            options.MotionMost = MotionMost;
            options.SequenceBeatsChance = SequenceBeatsChance;
            options.FewestScenesPerStep = FewestScenesPerStep;
            options.MostScenesPerStep = MostScenesPerStep;
        }

        /// <summary>Anything missing or out of range in the saved file comes back as its default or its limit.</summary>
        public static CinematicOverrides Checked(CinematicOverrides? read)
        {
            var c = read ?? new CinematicOverrides();
            c.FewestPictures = Math.Clamp(c.FewestPictures, SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown);
            c.MostPictures = Math.Clamp(c.MostPictures, c.FewestPictures, MosaicLayout.MaxShown);
            var odds = DefaultDesignOdds();
            if (c.DesignOdds != null)
                foreach (string design in LayoutComposer.Recipes)
                    if (c.DesignOdds.TryGetValue(design, out double saved))
                        odds[design] = saved <= 0 ? 0 : Math.Clamp(saved, SurpriseOptions.RarestDesign, SurpriseOptions.MostFavouredDesign);
            c.DesignOdds = odds;
            c.FewestSeconds = Math.Clamp(c.FewestSeconds, SurpriseOptions.FewestSeconds, SurpriseOptions.MostSeconds);
            c.MostSeconds = Math.Clamp(c.MostSeconds, c.FewestSeconds, SurpriseOptions.MostSeconds);
            if (c.MotionAmount is double motion)
            {
                c.MotionLeast = c.MotionMost = motion;
                c.MotionAmount = null;
            }
            c.MotionLeast = Math.Clamp(c.MotionLeast, 0, 1);
            c.MotionMost = Math.Clamp(c.MotionMost, c.MotionLeast, 1);
            c.SequenceBeatsChance = Math.Clamp(c.SequenceBeatsChance, 0, 1);
            c.FewestScenesPerStep = Math.Clamp(c.FewestScenesPerStep, 2, SurpriseOptions.MostScenesInAStep);
            c.MostScenesPerStep = Math.Clamp(c.MostScenesPerStep, c.FewestScenesPerStep, SurpriseOptions.MostScenesInAStep);
            return c;
        }
    }

    /// <summary>Hands out a large set of pictures a few at a time, every one of them before any comes round again.</summary>
    public sealed class PictureDeck
    {
        // In the order given, so a hand can be taken from pictures next to each other.
        private readonly List<string> _order = new();
        private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _added = new();
        private readonly Random _random;

        public PictureDeck(IEnumerable<string> pictures, Random random)
        {
            _random = random;
            foreach (string picture in pictures)
            {
                if (!_known.Add(picture)) continue;
                _order.Add(picture);
                _waiting.Add(picture);
            }
        }

        public int Count => _known.Count;

        public IReadOnlyList<string> All => _order;

        public bool Holds(string picture) => _known.Contains(picture);

        public PictureDeck Clone()
        {
            var copy = new PictureDeck(Array.Empty<string>(), new Random(_random.Next()));
            copy._order.AddRange(_order);
            copy._known.UnionWith(_known);
            copy._waiting.UnionWith(_waiting);
            copy._added.AddRange(_added);
            return copy;
        }

        /// <summary>Pictures not seen before go first, so one just added shows up soon.</summary>
        public void Include(IEnumerable<string> pictures)
        {
            foreach (string picture in pictures)
            {
                if (!_known.Add(picture)) continue;
                _order.Add(picture);
                _added.Add(picture);
            }
        }

        /// <summary>The session decides what is in the deck: a picture removed from it does not come round again, and one added comes up soon.</summary>
        public void MatchSession(IReadOnlyCollection<string> pictures)
        {
            var kept = new HashSet<string>(pictures, StringComparer.OrdinalIgnoreCase);
            _known.IntersectWith(kept);
            _waiting.IntersectWith(kept);
            _order.RemoveAll(p => !kept.Contains(p));
            _added.RemoveAll(p => !kept.Contains(p));
            Include(pictures);
        }

        public List<string> Deal(int count, bool together = false)
        {
            var hand = _added.Take(count).ToList();
            _added.RemoveRange(0, hand.Count);
            while (hand.Count < count)
            {
                if (_waiting.Count == 0)
                {
                    // Round again, leaving out this hand so no picture is in it twice.
                    _waiting.UnionWith(_known.Except(hand, StringComparer.OrdinalIgnoreCase));
                    if (_waiting.Count == 0) break;
                }
                var open = Enumerable.Range(0, _order.Count).Where(i => _waiting.Contains(_order[i])).ToList();
                int anchor = open[_random.Next(open.Count)];
                var picks = together
                    ? open.OrderBy(i => Math.Abs(i - anchor)).Take(count - hand.Count).ToList()
                    : new List<int> { anchor };
                foreach (int i in picks)
                {
                    hand.Add(_order[i]);
                    _waiting.Remove(_order[i]);
                }
            }
            return hand;
        }
    }

    /// <summary>
    /// Makes something worth looking at from a set of pictures. Nothing here picks settings at random: each recipe is one
    /// designed idea with its own rules, the random part only decides which version of it, and every version is scored
    /// against the actual pictures, so a face is not cut by a shape or covered by another cell. The result is an ordinary layout.
    /// </summary>
    public static class LayoutComposer
    {
        public const string EdgeToEdge = "Edge to edge";
        public const string GalleryWall = "Gallery wall";
        public const string HeroSpotlight = "Hero spotlight";
        public const string ScatteredPrints = "Scattered prints";
        public const string SlantedPanels = "Slanted panels";
        public const string Circles = "Circles";
        public static readonly string[] Recipes = { EdgeToEdge, GalleryWall, HeroSpotlight, ScatteredPrints, SlantedPanels, Circles };

        private const int Versions = 48;
        private const int TreesPerVersion = 120;
        private const int MaxScattered = 9;
        private const int MaxPanels = 6;
        private const int NearlyTheSame = 5;
        private const double ScreenFlatShare = 0.15;
        // The face box runs from the eyes to the mouth, so this leaves room for the head and shoulders.
        // How much of the cell the faces may take at most when an image zooms: the rest is room round them.
        private const double FacesKept = 0.85;
        private const double MostEnlarged = 2;
        private const double FeaturedLarger = 1.5;
        // The middle of the faces in a sample of family photos, below which a face looks soft.
        private const double SharpEnough = 150;
        private const double TooDark = 0.2;

        public static Composition? Compose(IReadOnlyList<PhotoFacts> photos, double width, double height, int seed, string? avoid = null, double rasterScale = 1, string? only = null, SurpriseOptions? options = null)
        {
            var usable = photos.Where(p => p.Width >= 1 && p.Height >= 1).Take(MosaicLayout.MaxShown).ToList();
            options ??= new SurpriseOptions();
            int minRequired = options.FewestPictures == 1 ? 1 : 2;
            if (usable.Count < minRequired || width < 50 || height < 50) return null;

            var random = new Random(seed);

            // A design you turned off is never used while one you left on can be. When the ones left on cannot take this many
            // images, the layout takes as many as the roomiest of them can, featured images first, rather than bring back a
            // design that is off. Only with every design off are they all used.
            int roomiest = Recipes.Where(r => !options.IsDesignOff(r) && (only == null || r == only)).Select(MostFor).DefaultIfEmpty(0).Max();
            if (roomiest >= 2 && roomiest < usable.Count)
                usable = usable.Select((p, i) => (p, i)).OrderByDescending(x => x.p.Featured).ThenBy(x => x.i).Take(roomiest).Select(x => x.p).ToList();
            var eligible = Recipes.Where(r => CanMake(r, usable.Count) && (only == null || r == only)).ToList();
            var enabled = eligible.Where(r => !options.IsDesignOff(r)).ToList();
            var recipes = (enabled.Count > 0 ? enabled : eligible)
                .OrderBy(r => -Math.Log(1 - random.NextDouble()) / options.OddsOf(r)).ToList();
            // The one just shown comes last, so clicking again gives something different.
            if (recipes.Count > 1 && recipes.Remove(avoid ?? "")) recipes.Add(avoid!);

            Composition? fallback = null;
            foreach (string recipe in recipes)
            {
                // The look is settled before the search, or the score would pick the same shape every time.
                int look = random.Next(int.MaxValue);
                for (int pass = 0; pass < 2; pass++)
                {
                    var found = new List<Composition>();
                    for (int i = 0; i < Versions; i++)
                    {
                        var version = Build(recipe, usable, width, height, random, pass == 0 ? look : random.Next(int.MaxValue), options);
                        if (version != null && usable.Count == 1) version = WithinCropAtMost(version, rasterScale, options, random);
                        if (version == null) continue;
                        // One image rolls its zoom once, after it is chosen: ranked here, an unzoomed version always shows more and wins.
                        if (usable.Count > 1)
                            foreach (var cell in version.Cells)
                            {
                                if (options.Zooms(random)) AimAtSubject(cell, options.PickZoom(random), rasterScale);
                            }
                        Judge(version, rasterScale, options.FaceShown);
                        found.Add(version);
                    }
                    var ranked = found.OrderByDescending(c => c.Score).ToList();
                    if (ranked.Count == 0) break;
                    if (fallback == null || ranked[0].Score > fallback.Score) fallback = ranked[0];
                    var clean = ranked.Where(c => c.FacesCut == 0 && c.PicturesBuried == 0).ToList();
                    if (clean.Count > 0) return clean[random.Next(Math.Min(options.Variety, clean.Count))];
                    if (recipe != HeroSpotlight) break;
                }
            }
            return fallback;
        }

        /// <summary>
        /// Where a camera zoom on a one-image layout heads: its faces or, with none found, its focus point, as shares of the
        /// canvas.
        /// </summary>
        public static (double X, double Y) AimFor(ComposedCell cell, double canvasW, double canvasH) =>
            cell.Photo.Faces.Count > 0
                ? SubjectOnCanvas(cell, canvasW, canvasH)
                : PointForNormalized(cell, cell.Photo.FocusX, cell.Photo.FocusY, canvasW, canvasH);

        /// <summary>The faces of a cell's image worth a view of their own, largest first.</summary>
        public static List<UnitRect> FacesLargestFirst(ComposedCell cell) =>
            cell.Photo.Faces.Where(f => f.Area > 0.0015).OrderByDescending(f => f.Area).ToList();

        /// <summary>Where one face sits on the canvas, as shares, and how far a camera may zoom on it alone: it takes at most
        /// 85% of the view, and the photo is never shown past twice its own pixels.</summary>
        public static ((double X, double Y) Point, double MostZoom) OnFace(ComposedCell cell, UnitRect face, double canvasW, double canvasH)
        {
            var point = PointForNormalized(cell, face.X + face.W / 2, face.Y + face.H / 2, canvasW, canvasH);
            var a = PointForNormalized(cell, face.X, face.Y, canvasW, canvasH);
            var b = PointForNormalized(cell, face.X + face.W, face.Y + face.H, canvasW, canvasH);
            double share = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            double most = MostEnlarged * cell.Photo.Width / Math.Max(1, cell.ImageWidth);
            if (share > 0) most = Math.Min(most, FacesKept / share);
            return (point, Math.Clamp(most, 1, SurpriseOptions.MostOfAnyZoom));
        }

        /// <summary>
        /// How far a camera may zoom on a one-image layout: the faces take at most 85% of the view, all of them in, and the
        /// photo is never shown past twice its own pixels. The same limits the layout zoom keeps.
        /// </summary>
        public static double MostCameraZoom(ComposedCell cell, double canvasW, double canvasH)
        {
            double most = MostEnlarged * cell.Photo.Width / Math.Max(1, cell.ImageWidth);
            if (cell.Photo.Faces.Count > 0 && canvasW >= 1 && canvasH >= 1)
            {
                var a = PointForNormalized(cell, cell.Photo.Faces.Min(f => f.X), cell.Photo.Faces.Min(f => f.Y), canvasW, canvasH);
                var b = PointForNormalized(cell, cell.Photo.Faces.Max(f => f.X + f.W), cell.Photo.Faces.Max(f => f.Y + f.H), canvasW, canvasH);
                double share = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                if (share > 0) most = Math.Min(most, FacesKept / share);
            }
            return Math.Clamp(most, 1, SurpriseOptions.MostOfAnyZoom);
        }

        /// <summary>Every design takes one image or more; prints, circles and panels have a most, past which they crowd.</summary>
        public static bool CanMake(string recipe, int photos) => photos >= 1 && photos <= MostFor(recipe);

        public static int MostFor(string recipe) => recipe switch
        {
            ScatteredPrints or Circles => MaxScattered,
            SlantedPanels => MaxPanels,
            _ => MosaicLayout.MaxShown
        };

        /// <summary>
        /// A guess, used only to leave a picture out of a large set. Photos sent by message lose their camera details, so
        /// camera details or a large face mean a photo. Otherwise a screenshot's name or large flat areas give it away: in a sample of 300 pictures, photos from a camera reached 2% flat and captures started at 16%.
        /// </summary>
        public static bool LooksLikeScreen(string path, bool camera, double width, double height, IReadOnlyList<UnitRect> faces, double flatShare)
        {
            if (camera || faces.Any(f => f.Area >= 0.01)) return false;
            if (flatShare >= ScreenFlatShare) return true;
            // A PNG, or a tall image, is not on its own a screen: photos are saved as PNG and phones take tall ones.
            string name = System.IO.Path.GetFileName(path);
            return name.Contains("screenshot", StringComparison.OrdinalIgnoreCase) || name.Contains("screen shot", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>For a large set: what looks like a screen capture goes, and all but one of near-identical shots. Never a featured picture, and never so many that nothing is left to arrange.</summary>
        public static List<PhotoFacts> LeaveOutScreensAndRepeats(List<PhotoFacts> photos)
        {
            var kept = new List<PhotoFacts>();
            foreach (var photo in photos.OrderByDescending(p => p.Featured).ThenByDescending(p => p.Sharpness))
            {
                if (photo.Featured) { kept.Add(photo); continue; }
                if (photo.ScreenLike) continue;
                if (photo.Look != 0 && kept.Any(k => k.Look != 0 && System.Numerics.BitOperations.PopCount(k.Look ^ photo.Look) <= NearlyTheSame)) continue;
                kept.Add(photo);
            }
            return kept.Count >= 2 ? kept : photos;
        }

        /// <summary>
        /// A layout of one image, kept to Crop at most and Face kept in frame. Filling within both, it stays as built.
        /// Past either, Edge to edge shows the image whole as large as the screen allows, Gallery wall shows it whole in
        /// its card, Scattered prints already show it whole, and a design that can only crop (a circle, a hero shape, a
        /// leaning panel) gives no version, so it takes no part in this call.
        /// </summary>
        private static Composition? WithinCropAtMost(Composition version, double rasterScale, SurpriseOptions options, Random random)
        {
            var cell = version.Cells.FirstOrDefault(c => c.Photo != null);
            if (cell == null) return version;
            Judge(version, rasterScale, options.FaceShown);
            if (version.FacesCut == 0 && CropShare(cell) <= options.CropAtMost + 1e-9) return version;
            return version.Recipe switch
            {
                EdgeToEdge => ShownWhole(EdgeToEdge, cell.Photo, version.Width, version.Height, 0, null, random, options.PickBackdrop(random)),
                GalleryWall => ShownWhole(GalleryWall, cell.Photo, version.Width, version.Height, cell.Frame.Padding * 2, version.CanvasColor, random, CanvasLook.Solid),
                ScatteredPrints => version,
                _ => null
            };
        }

        /// <summary>
        /// The colour of every frame in one layout, from Frame color: white, black, or a tone from the boldest image; Any
        /// picks one of the three for each layout.
        /// </summary>
        public static string FrameColorFor(List<PhotoFacts> photos, Random random, SurpriseOptions options)
        {
            int kind = options.Card switch
            {
                SurpriseOptions.CardWhite => 0,
                SurpriseOptions.CardBlack => 1,
                SurpriseOptions.CardFromPictures => 2,
                _ => random.Next(3)
            };
            return kind switch
            {
                0 => "#FFF8F8F8",
                1 => "#FF141414",
                _ => Tone(Boldest(photos, random).Color, 0.22, 0.16)
            };
        }

        /// <summary>The share of the image a cell hides when it fills the cell, measured against the cell's shape, not its box.</summary>
        public static double CropShare(ComposedCell cell)
        {
            double innerW = Math.Max(1, cell.W - 2 * cell.Frame.Padding);
            double innerH = Math.Max(1, cell.H - 2 * cell.Frame.Padding);
            var outline = WindowShapes.Outline(cell.Geometry);
            double shapeArea = innerW * innerH * (outline == null ? 1 : PolygonArea(outline));
            double imageArea = Math.Max(1, cell.ImageWidth * cell.ImageHeight);
            return Math.Clamp(1 - shapeArea / imageArea, 0, 1);
        }

        private static double PolygonArea((double X, double Y)[] points)
        {
            double sum = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(sum) / 2;
        }

        /// <summary>
        /// The image whole, as large as the space allows, unframed: on a tinted vignette for Edge to edge (as Cinematic
        /// Slideshow shows a portrait), or set in its card for Gallery wall, the card's margin at least the given one.
        /// </summary>
        private static Composition ShownWhole(string recipe, PhotoFacts photo, double w, double h, double margin, string? card, Random random, string backdrop)
        {
            double availW = Math.Max(40, w - 2 * margin), availH = Math.Max(40, h - 2 * margin);
            double scale = Math.Min(availW / photo.Width, availH / photo.Height);
            double cellW = Math.Max(40, photo.Width * scale), cellH = Math.Max(40, photo.Height * scale);
            var cell = new ComposedCell
            {
                Photo = photo,
                Floating = true,
                X = (w - cellW) / 2,
                Y = (h - cellH) / 2,
                W = cellW,
                H = cellH,
                Frame = new CellBorderDto { Color = "#00000000", GutterMaterial = "None", GutterColor = "#00000000" },
                Geometry = new CellGeometryDto()
            };
            return card == null
                ? Finish(recipe, new List<ComposedCell> { cell }, null, Tone(photo.Color, 0.28, 0.14), backdrop, FrameLooks.Flush(), w, h)
                : Finish(recipe, new List<ComposedCell> { cell }, null, card, CanvasLook.Solid, FrameLooks.Flush(), w, h);
        }

        private static Composition? Build(string recipe, List<PhotoFacts> photos, double w, double h, Random random, int look, SurpriseOptions options) => recipe switch
        {
            EdgeToEdge => BuildEdgeToEdge(photos, w, h, random),
            GalleryWall => BuildGalleryWall(photos, w, h, random, options),
            SlantedPanels => BuildSlantedPanels(photos, w, h, random),
            Circles => BuildCircles(photos, w, h, random, options),
            HeroSpotlight => BuildHeroSpotlight(photos, w, h, random, look, options),
            _ => BuildScatteredPrints(photos, w, h, random, options)
        };

        private static Composition? BuildEdgeToEdge(List<PhotoFacts> photos, double w, double h, Random random)
        {
            double seam = random.Next(3) == 0 ? 0 : 1.5;
            var frame = seam > 0 ? new CellBorderDto { Color = "#00000000", Padding = seam, GutterMaterial = "None", GutterColor = "#00000000" } : FrameLooks.Flush();
            var tree = FindTree(photos, w / h, random);
            if (tree == null) return null;
            var cells = new List<ComposedCell>();
            var mosaic = Place(tree, 0, 0, w, h, frame, cells);
            return Finish(EdgeToEdge, cells, mosaic, "#FF0A0A0A", CanvasLook.Solid, FrameLooks.Flush(), w, h);
        }

        private static Composition? BuildGalleryWall(List<PhotoFacts> photos, double w, double h, Random random, SurpriseOptions options)
        {
            // One sheet of card with the pictures set into it: the margin all round matches the gaps between them.
            string card = FrameColorFor(photos, random, options);
            double gap = Math.Clamp(Math.Min(w, h) * (0.012 + random.NextDouble() * 0.012), 6, 20) * options.GapScale;
            double radius = random.Next(3) == 0 ? gap * 0.6 : 0;
            var cellFrame = new CellBorderDto { Color = "#00000000", Padding = gap / 2, CornerRadius = radius, GutterMaterial = "Solid", GutterColor = card };
            var canvasFrame = new CellBorderDto { Color = "#00000000", Padding = gap / 2, GutterMaterial = "Solid", GutterColor = card };
            var tree = FindTree(photos, (w - gap) / (h - gap), random);
            if (tree == null) return null;
            var cells = new List<ComposedCell>();
            var mosaic = Place(tree, gap / 2, gap / 2, w - gap, h - gap, cellFrame, cells);
            return Finish(GalleryWall, cells, mosaic, card, CanvasLook.Solid, canvasFrame, w, h);
        }

        private static Composition? BuildHeroSpotlight(List<PhotoFacts> photos, double w, double h, Random random, int look, SurpriseOptions options)
        {
            var hero = photos.Where(p => p.Featured).OrderBy(_ => random.Next()).FirstOrDefault()
                ?? photos.OrderByDescending(Presence).Take(2).OrderBy(_ => random.Next()).First();
            var rest = photos.Where(p => !ReferenceEquals(p, hero)).ToList();
            var cells = new List<ComposedCell>();
            MosaicLayout? mosaic = null;
            // The only image is the hero, alone on its backdrop.
            if (rest.Count > 0)
            {
                var tree = FindTree(rest, w / h, random);
                if (tree == null) return null;
                var flush = new CellBorderDto { Color = "#00000000", Padding = 1, GutterMaterial = "None", GutterColor = "#00000000" };
                mosaic = Place(tree, 0, 0, w, h, flush, cells);
            }

            string[] shapes = { WindowShapes.Arch, WindowShapes.Oval, WindowShapes.Squircle, WindowShapes.Hexagon };
            string shape = shapes[look % shapes.Length];
            double low = Math.Min(options.HeroSmallest, options.HeroLargest), high = Math.Max(options.HeroSmallest, options.HeroLargest);
            double cardH = h * (low + random.NextDouble() * (high - low));
            double aspect = shape switch
            {
                WindowShapes.Arch => 0.72 + random.NextDouble() * 0.12,
                WindowShapes.Oval => 0.95 + random.NextDouble() * 0.15,
                WindowShapes.Hexagon => 1.12,
                _ => Math.Clamp(hero.Aspect, 0.8, 1.45)
            };
            double cardW = Math.Min(cardH * aspect, w * 0.7);
            cardH = cardW / aspect;
            double[] across = { 0.5, 0.33, 0.67 };
            double cx = w * across[random.Next(across.Length)];
            double cy = h * (0.46 + random.NextDouble() * 0.08);
            double shadowRoom = Math.Min(w, h) * 0.035;
            double line = Math.Clamp(Math.Min(w, h) * 0.007, 3, 8) * options.GapScale;
            string outline = FrameColorFor(photos, random, options);
            cells.Add(new ComposedCell
            {
                Photo = hero,
                Floating = true,
                X = cx - cardW / 2 - shadowRoom,
                Y = cy - cardH / 2 - shadowRoom,
                W = cardW + 2 * shadowRoom,
                H = cardH + 2 * shadowRoom,
                Geometry = new CellGeometryDto { Shape = shape },
                Frame = new CellBorderDto
                {
                    Color = outline,
                    Thickness = line,
                    Padding = shadowRoom,
                    CornerRadius = shape == WindowShapes.Squircle || shape == WindowShapes.Oval ? 0 : line * 2,
                    ShadowIntensity = 70,
                    Shadow = true,
                    GutterMaterial = "None",
                    GutterColor = "#00000000"
                }
            });
            return Finish(HeroSpotlight, cells, mosaic, Tone(hero.Color, 0.25, 0.1), options.PickBackdrop(random), FrameLooks.Flush(), w, h);
        }

        private static Composition? BuildScatteredPrints(List<PhotoFacts> photos, double w, double h, Random random, SurpriseOptions options)
        {
            int count = photos.Count;
            int columns = Math.Max(1, (int)Math.Round(Math.Sqrt(count * w / h)));
            int rows = (int)Math.Ceiling(count / (double)columns);
            var spots = new List<(double X, double Y)>();
            for (int r = 0; r < rows; r++)
            {
                int inRow = r == rows - 1 ? count - columns * (rows - 1) : columns;
                for (int c = 0; c < inRow; c++)
                    spots.Add(((c + 0.5) / inRow * w, (r + 0.5) / rows * h));
            }

            double share = 1.05 + random.NextDouble() * 0.25;
            double border = Math.Clamp(Math.Min(w, h) * 0.012, 5, 12) * options.GapScale;
            string borderColor = FrameColorFor(photos, random, options);
            double shadowRoom = Math.Min(w, h) * 0.03;
            // A featured print is larger and lies on top.
            var order = photos.OrderBy(p => p.Featured ? 1 : 0).ThenBy(_ => random.Next()).ToList();
            var cells = new List<ComposedCell>();
            for (int i = 0; i < count; i++)
            {
                var photo = order[i];
                double area = w * h * share / count * (photo.Featured ? FeaturedLarger : 1);
                double printH = Math.Sqrt(area / photo.Aspect);
                double printW = printH * photo.Aspect;
                double fit = Math.Min(1, Math.Min(w * 0.62 / printW, h * 0.72 / printH));
                printW *= fit;
                printH *= fit;
                double jitterX = (random.NextDouble() - 0.5) * w / columns * 0.3;
                double jitterY = (random.NextDouble() - 0.5) * h / rows * 0.3;
                double cx = Math.Clamp(spots[i].X + jitterX, printW * 0.46, w - printW * 0.46);
                double cy = Math.Clamp(spots[i].Y + jitterY, printH * 0.46, h - printH * 0.46);
                // From under a quarter of the most tilt up to all of it, so a pile never looks squared up by accident.
                double turn = (random.NextDouble() < 0.5 ? -1 : 1) * options.MostTilt * (0.23 + random.NextDouble() * 0.77);
                cells.Add(new ComposedCell
                {
                    Photo = photo,
                    Floating = true,
                    X = cx - printW / 2 - shadowRoom,
                    Y = cy - printH / 2 - shadowRoom,
                    W = printW + 2 * shadowRoom,
                    H = printH + 2 * shadowRoom,
                    Rotation = turn,
                    Geometry = new CellGeometryDto { WindowRotation = turn },
                    Frame = new CellBorderDto
                    {
                        Color = borderColor,
                        Thickness = border,
                        Padding = shadowRoom,
                        ShadowIntensity = 55,
                        Shadow = true,
                        GutterMaterial = "None",
                        GutterColor = "#00000000"
                    }
                });
            }
            string surface = options.PickBackdrop(random);
            return Finish(ScatteredPrints, cells, null, Tone(Boldest(photos, random).Color, 0.28, 0.3), surface, FrameLooks.Flush(), w, h);
        }

        /// <summary>Tall panels that lean the same way, with even gaps along the slant, running off the canvas at each end so no corner is left bare.</summary>
        private static Composition? BuildSlantedPanels(List<PhotoFacts> photos, double w, double h, Random random)
        {
            int count = photos.Count;
            const double lean = 0.2;
            double gap = Math.Clamp(Math.Min(w, h) * 0.012, 5, 12);
            // One panel stands alone in the middle, at full height and about the image's own width.
            double panelW = count == 1
                ? Math.Clamp(h * photos[0].Aspect / (1 - lean), w * 0.25, w * 0.8)
                : (w - (count - 1) * gap) / ((1 - lean) * count - lean);
            double firstX = count == 1 ? (w - panelW) / 2 : -lean * panelW;
            bool mirrored = random.Next(2) == 0;
            var order = photos.OrderBy(_ => random.Next()).ToList();
            var cells = new List<ComposedCell>();
            for (int i = 0; i < count; i++)
            {
                cells.Add(new ComposedCell
                {
                    Photo = order[i],
                    Floating = true,
                    X = firstX + i * ((1 - lean) * panelW + gap),
                    Y = 0,
                    W = panelW,
                    H = h,
                    Geometry = new CellGeometryDto { Shape = WindowShapes.Parallelogram, ShapeFlipH = mirrored }
                });
            }
            return Finish(SlantedPanels, cells, null, "#FF0A0A0A", CanvasLook.Solid, FrameLooks.Flush(), w, h);
        }

        /// <summary>Round portraits of varied size on a quiet canvas. The boldest picture is the largest.</summary>
        private static Composition? BuildCircles(List<PhotoFacts> photos, double w, double h, Random random, SurpriseOptions options)
        {
            int count = photos.Count;
            int columns = Math.Max(1, (int)Math.Round(Math.Sqrt(count * w / h)));
            int rows = (int)Math.Ceiling(count / (double)columns);
            var order = photos.OrderBy(_ => random.Next()).ToList();
            var boldest = order.Where(p => p.Featured).FirstOrDefault() ?? Boldest(order, random);
            double line = Math.Clamp(Math.Min(w, h) * 0.006, 3, 7) * options.GapScale;
            string outline = FrameColorFor(photos, random, options);
            double shadowRoom = Math.Min(w, h) * 0.03;
            double evenArea = w * h * 0.8 / count;
            var cells = new List<ComposedCell>();
            for (int i = 0; i < count; i++)
            {
                int row = i / columns;
                int inRow = row == rows - 1 ? count - columns * (rows - 1) : columns;
                int column = i - row * columns;
                var photo = order[i];
                double scale = ReferenceEquals(photo, boldest) ? FeaturedLarger : 0.82 + random.NextDouble() * 0.2;
                double across = Math.Min(Math.Sqrt(evenArea * scale * 4 / Math.PI), Math.Min(w * 0.6, h * 0.85));
                double cx = (column + 0.5) / inRow * w + (random.NextDouble() - 0.5) * w / columns * 0.2;
                double cy = (row + 0.5) / rows * h + (random.NextDouble() - 0.5) * h / rows * 0.2;
                cx = Math.Clamp(cx, across * 0.52, w - across * 0.52);
                cy = Math.Clamp(cy, across * 0.52, h - across * 0.52);
                cells.Add(new ComposedCell
                {
                    Photo = photo,
                    Floating = true,
                    X = cx - across / 2 - shadowRoom,
                    Y = cy - across / 2 - shadowRoom,
                    W = across + 2 * shadowRoom,
                    H = across + 2 * shadowRoom,
                    Geometry = new CellGeometryDto { Shape = WindowShapes.Oval },
                    Frame = new CellBorderDto
                    {
                        Color = outline,
                        Thickness = line,
                        Padding = shadowRoom,
                        ShadowIntensity = 60,
                        Shadow = true,
                        GutterMaterial = "None",
                        GutterColor = "#00000000"
                    }
                });
            }
            string surface = options.PickBackdrop(random);
            return Finish(Circles, cells, null, Tone(boldest.Color, 0.25, 0.22), surface, FrameLooks.Flush(), w, h);
        }

        // ---- Fitting pictures to a rectangle

        private sealed class Node
        {
            public Node? A, B;
            public bool SideBySide;
            public PhotoFacts? Photo;
            public double Aspect;
        }

        private static Node Leaf(PhotoFacts photo) => new() { Photo = photo, Aspect = photo.Aspect };

        private static Node Join(Node a, Node b, bool sideBySide) => new()
        {
            A = a,
            B = b,
            SideBySide = sideBySide,
            Aspect = sideBySide ? a.Aspect + b.Aspect : 1 / (1 / a.Aspect + 1 / b.Aspect)
        };

        /// <summary>
        /// A tree of cuts whose shape comes out close to the canvas when each picture keeps its own shape. Then the pictures
        /// barely need cropping, however mixed they are, which an even grid cannot do.
        /// </summary>
        private static Node? FindTree(List<PhotoFacts> photos, double target, Random random)
        {
            if (photos.Count == 0) return null;
            Node? best = null;
            double bestCost = double.MaxValue;
            for (int attempt = 0; attempt < TreesPerVersion; attempt++)
            {
                var shuffled = photos.OrderBy(_ => random.Next()).ToList();
                var tree = RandomTree(shuffled, 0, shuffled.Count, random);
                double cost = Math.Abs(Math.Log(tree.Aspect / target)) + SmallestCellCost(tree, target) + random.NextDouble() * 0.04;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = tree;
                }
            }
            return best;
        }

        private static Node RandomTree(List<PhotoFacts> photos, int start, int count, Random random)
        {
            if (count == 1) return Leaf(photos[start]);
            int first = count == 2 ? 1 : 1 + random.Next(count - 1);
            return Join(RandomTree(photos, start, first, random), RandomTree(photos, start + first, count - first, random), random.Next(2) == 0);
        }

        /// <summary>A tree can leave one picture tiny. The share of the canvas held by its smallest cell, compared with an even share.</summary>
        private static double SmallestCellCost(Node tree, double target)
        {
            double smallest = 1;
            int count = 0;
            void Walk(Node n, double w, double h)
            {
                if (n.Photo != null)
                {
                    count++;
                    smallest = Math.Min(smallest, w * h);
                    return;
                }
                if (n.SideBySide)
                {
                    double a = w * n.A!.Aspect / (n.A.Aspect + n.B!.Aspect);
                    Walk(n.A, a, h);
                    Walk(n.B, w - a, h);
                }
                else
                {
                    double ia = 1 / n.A!.Aspect, ib = 1 / n.B!.Aspect;
                    double a = h * ia / (ia + ib);
                    Walk(n.A, w, a);
                    Walk(n.B, w, h - a);
                }
            }
            Walk(tree, 1, 1);
            double even = 1.0 / Math.Max(1, count);
            return smallest < even * 0.3 ? (even * 0.3 - smallest) / even * 3 : 0;
        }

        private static MosaicLayout Place(Node node, double x, double y, double w, double h, CellBorderDto frame, List<ComposedCell> cells)
        {
            if (node.Photo != null)
            {
                int slot = cells.Count;
                cells.Add(new ComposedCell { Photo = node.Photo, X = x, Y = y, W = w, H = h, Frame = frame.Clone() });
                return MosaicLayout.Cell(slot);
            }
            double shareA = node.SideBySide
                ? node.A!.Aspect / (node.A.Aspect + node.B!.Aspect)
                : (1 / node.A!.Aspect) / (1 / node.A.Aspect + 1 / node.B!.Aspect);
            MosaicLayout first, second;
            if (node.SideBySide)
            {
                first = Place(node.A, x, y, w * shareA, h, frame, cells);
                second = Place(node.B!, x + w * shareA, y, w * (1 - shareA), h, frame, cells);
            }
            else
            {
                first = Place(node.A, x, y, w, h * shareA, frame, cells);
                second = Place(node.B!, x, y + h * shareA, w, h * (1 - shareA), frame, cells);
            }
            var cut = MosaicLayout.Cut(node.SideBySide, first, second);
            cut.Weights[0] = shareA;
            cut.Weights[1] = 1 - shareA;
            return cut;
        }

        private static Composition Finish(string recipe, List<ComposedCell> cells, MosaicLayout? mosaic, string color, string surface, CellBorderDto frame, double w, double h)
        {
            foreach (var cell in cells) AimAtSubject(cell);
            var made = new Composition { Recipe = recipe, Cells = cells, Mosaic = mosaic, Width = w, Height = h };
            made.SetCanvas(color, surface, frame);
            return made;
        }

        /// <summary>The picture covers the cell, as Fill does, and shifts so its subject sits a little above the middle.</summary>
        /// <summary>
        /// Frames the cell on its faces or, with none, its focus point, and zooms toward them by <paramref name="zoomTo"/>.
        /// The zoom stops short where it would cut a face (all faces stay in, with a little room) or show the photo past
        /// twice its own pixels.
        /// </summary>
        private static void AimAtSubject(ComposedCell cell, double zoomTo = 1, double rasterScale = 1)
        {
            double innerW = Math.Max(1, cell.W - 2 * cell.Frame.Padding);
            double innerH = Math.Max(1, cell.H - 2 * cell.Frame.Padding);
            var cover = WindowShapes.CoverBox(cell.Geometry, innerW, innerH);
            double scale = Math.Max(cover.Width / cell.Photo.Width, cover.Height / cell.Photo.Height);
            double imageW = cell.Photo.Width * scale;
            double imageH = cell.Photo.Height * scale;

            double fx = cell.Photo.FocusX, fy = cell.Photo.FocusY;
            double limit = zoomTo;
            if (cell.Photo.Faces.Count > 0)
            {
                double left = cell.Photo.Faces.Min(f => f.X);
                double top = cell.Photo.Faces.Min(f => f.Y);
                double right = cell.Photo.Faces.Max(f => f.X + f.W);
                double bottom = cell.Photo.Faces.Max(f => f.Y + f.H);
                fx = (left + right) / 2;
                fy = (top + bottom) / 2;
                // Every face found stays in, with a little room round them.
                limit = Math.Min(limit, FacesKept * Math.Min(innerW / Math.Max(1, (right - left) * imageW), innerH / Math.Max(1, (bottom - top) * imageH)));
            }
            // No further than the picture has the detail for.
            limit = Math.Min(limit, MostEnlarged * cell.Photo.Width / Math.Max(1, imageW * rasterScale));
            double zoom = Math.Max(1, limit);
            imageW *= zoom;
            imageH *= zoom;
            cell.Zoom = zoom;
            cell.ImageWidth = imageW;
            cell.ImageHeight = imageH;
            double maxX = Math.Max(0, (imageW - innerW) / 2);
            double maxY = Math.Max(0, (imageH - innerH) / 2);
            cell.PanX = Math.Clamp(-(fx - 0.5) * imageW, -maxX, maxX);
            cell.PanY = Math.Clamp(-0.08 * innerH - (fy - 0.5) * imageH, -maxY, maxY);
        }

        // ---- Judging a version

        /// <summary>
        /// Faces matter most. A face a shape cuts into, or another cell covers, is what makes a random layout look careless.
        /// Then how much of each picture shows, whether any is enlarged past its detail, and how well prints cover the canvas.
        /// </summary>
        private static void Judge(Composition version, double rasterScale, double faceShown)
        {
            double faceScore = 1;
            int facesCut = 0;
            double shown = 0, sharp = 0;
            for (int i = 0; i < version.Cells.Count; i++)
            {
                var cell = version.Cells[i];
                foreach (var face in cell.Photo.Faces)
                {
                    double visible = VisibleShare(version, i, face);
                    if (visible < faceShown)
                    {
                        // A tiny face in a crowd costs less than a person's own.
                        double weight = Math.Min(1, Math.Sqrt(face.Area) * 6);
                        faceScore -= weight * (1 - visible);
                        if (face.Area > 0.0015) facesCut++;
                    }
                }
                double innerW = Math.Max(1, cell.W - 2 * cell.Frame.Padding);
                double innerH = Math.Max(1, cell.H - 2 * cell.Frame.Padding);
                shown += innerW * innerH / Math.Max(1, cell.ImageWidth * cell.ImageHeight);
                double enlarged = cell.ImageWidth * rasterScale / cell.Photo.Width;
                sharp += enlarged <= 1.3 ? 1 : Math.Max(0, 1 - (enlarged - 1.3));
            }
            int count = Math.Max(1, version.Cells.Count);
            double score = 4 * faceScore + shown / count + 0.5 * sharp / count;

            // The largest cell should hold the picture with the most in it.
            var largest = version.Cells.OrderByDescending(c => c.W * c.H).First();
            double most = version.Cells.Max(c => Presence(c.Photo));
            if (most > 0) score += 0.4 * Presence(largest.Photo) / most;
            score += 0.5 * CanvasCovered(version);

            version.Score = score;
            version.FacesCut = facesCut;
            version.PicturesBuried = CountBuried(version);
        }

        private static double VisibleShare(Composition version, int index, UnitRect face)
        {
            var cell = version.Cells[index];
            const int samples = 5;
            int visible = 0;
            for (int a = 0; a < samples; a++)
            {
                for (int b = 0; b < samples; b++)
                {
                    double u = face.X + face.W * (a + 0.5) / samples;
                    double v = face.Y + face.H * (b + 0.5) / samples;
                    var point = PointOnCanvas(cell, u, v);
                    if (point.X < 0 || point.Y < 0 || point.X > version.Width || point.Y > version.Height) continue;
                    if (!Shows(cell, point)) continue;
                    bool covered = false;
                    for (int above = index + 1; above < version.Cells.Count && !covered; above++)
                        covered = Shows(version.Cells[above], point);
                    if (!covered) visible++;
                }
            }
            return visible / (double)(samples * samples);
        }

        private static int CountBuried(Composition version)
        {
            const int samples = 8;
            const double mostlyHidden = 0.6;
            int buried = 0;
            for (int i = 0; i < version.Cells.Count - 1; i++)
            {
                var cell = version.Cells[i];
                double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
                double pad = cell.Frame.Padding;
                int hidden = 0;
                for (int a = 0; a < samples; a++)
                {
                    for (int b = 0; b < samples; b++)
                    {
                        double x = cell.X + pad + (cell.W - 2 * pad) * (a + 0.5) / samples;
                        double y = cell.Y + pad + (cell.H - 2 * pad) * (b + 0.5) / samples;
                        var point = Turn(x, y, cx, cy, cell.Rotation);
                        for (int above = i + 1; above < version.Cells.Count; above++)
                        {
                            if (!Shows(version.Cells[above], point)) continue;
                            hidden++;
                            break;
                        }
                    }
                }
                if (hidden > mostlyHidden * samples * samples) buried++;
            }
            return buried;
        }

        private static (double X, double Y) PointOnCanvas(ComposedCell cell, double u, double v)
        {
            double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
            double x = cx + cell.PanX + (u - 0.5) * cell.ImageWidth;
            double y = cy + cell.PanY + (v - 0.5) * cell.ImageHeight;
            return Turn(x, y, cx, cy, cell.Rotation);
        }

        /// <summary>Projects the faces inside a cell onto the canvas, accounting for crop pan, zoom, padding, and rotation.</summary>
        public static (double X, double Y) SubjectOnCanvas(ComposedCell cell, double canvasW, double canvasH)
        {
            if (canvasW < 1 || canvasH < 1) return (0.5, 0.5);
            if (cell.Photo.Faces.Count == 0)
                return (Math.Clamp((cell.X + cell.W / 2) / canvasW, 0, 1), Math.Clamp((cell.Y + cell.H / 2) / canvasH, 0, 1));
            double weightSum = 0, normX = 0, normY = 0;
            foreach (var face in cell.Photo.Faces)
            {
                double w = Math.Max(0.0001, Math.Sqrt(face.Area));
                normX += (face.X + face.W / 2) * w;
                normY += (face.Y + face.H / 2) * w;
                weightSum += w;
            }
            return PointForNormalized(cell, normX / weightSum, normY / weightSum, canvasW, canvasH);
        }

        public static (double X, double Y) PointForNormalized(ComposedCell cell, double normX, double normY, double canvasW, double canvasH)
        {
            double pad = cell.Frame.Padding;
            double innerW = Math.Max(1, cell.W - 2 * pad);
            double innerH = Math.Max(1, cell.H - 2 * pad);
            double px = cell.X + pad + cell.PanX + normX * cell.ImageWidth;
            double py = cell.Y + pad + cell.PanY + normY * cell.ImageHeight;
            px = Math.Clamp(px, cell.X + pad, cell.X + pad + innerW);
            py = Math.Clamp(py, cell.Y + pad, cell.Y + pad + innerH);
            if (Math.Abs(cell.Rotation) > 0.01)
                (px, py) = Turn(px, py, cell.X + cell.W / 2, cell.Y + cell.H / 2, cell.Rotation);
            return (Math.Clamp(px / canvasW, 0, 1), Math.Clamp(py / canvasH, 0, 1));
        }

        private static (double X, double Y) Turn(double x, double y, double cx, double cy, double degrees)
        {
            if (degrees == 0) return (x, y);
            double radians = degrees * Math.PI / 180;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            return (cx + (x - cx) * cos - (y - cy) * sin, cy + (x - cx) * sin + (y - cy) * cos);
        }

        /// <summary>Whether this cell's picture draws at this point of the canvas. Its gutter does not count.</summary>
        private static bool Shows(ComposedCell cell, (double X, double Y) point)
        {
            double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
            var (x, y) = Turn(point.X, point.Y, cx, cy, -cell.Rotation);
            double pad = cell.Frame.Padding;
            double left = cell.X + pad, top = cell.Y + pad, right = cell.X + cell.W - pad, bottom = cell.Y + cell.H - pad;
            if (x < left || x > right || y < top || y > bottom) return false;
            var outline = WindowShapes.Outline(cell.Geometry);
            if (outline == null) return true;
            return Inside(outline, (x - left) / (right - left), (y - top) / (bottom - top));
        }

        private static bool Inside((double X, double Y)[] polygon, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Only scattered prints can leave bare canvas, and a loose pile looks better than a sparse one.</summary>
        private static double CanvasCovered(Composition version)
        {
            if (version.Cells.Any(c => !c.Floating)) return 1;
            const int steps = 16;
            int hit = 0;
            for (int a = 0; a < steps; a++)
            {
                for (int b = 0; b < steps; b++)
                {
                    var point = ((a + 0.5) / steps * version.Width, (b + 0.5) / steps * version.Height);
                    if (version.Cells.Any(c => Shows(c, point))) hit++;
                }
            }
            return hit / (double)(steps * steps);
        }

        /// <summary>How much a picture holds: its faces most, then its size. A soft or dark one should not take the best place because of a big face, and a featured one always does.</summary>
        private static double Presence(PhotoFacts photo)
        {
            double faces = photo.Faces.Sum(f => Math.Sqrt(f.Area));
            double aspect = photo.Aspect;
            double comfortable = aspect > 2.2 || aspect < 0.45 ? 0.5 : 1;
            double quality = Math.Clamp(photo.Sharpness / SharpEnough, 0.4, 1) * (photo.Brightness < TooDark ? 0.4 : 1);
            double featured = photo.Featured ? 100 : 1;
            return (1 + 3 * Math.Min(1, faces)) * comfortable * quality * featured * (1 + Math.Log10(Math.Max(10, photo.Width * photo.Height)) / 10);
        }

        private static PhotoFacts Boldest(List<PhotoFacts> photos, Random random) => photos.OrderByDescending(Presence).Take(3).OrderBy(_ => random.Next()).First();

        /// <summary>A color from a picture, kept to its hue. Saturation and lightness are set so it stays quiet behind the pictures.</summary>
        public static string Tone(string hex, double saturation, double lightness)
        {
            var c = FrameColor.Parse(hex);
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double hue = 0;
            if (max > min)
            {
                double d = max - min;
                hue = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                hue /= 6;
            }
            double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            double p = 2 * lightness - q;
            byte Channel(double t)
            {
                t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
                double v = t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
                return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            }
            return $"#FF{Channel(hue + 1.0 / 3):X2}{Channel(hue):X2}{Channel(hue - 1.0 / 3):X2}";
        }
    }
}

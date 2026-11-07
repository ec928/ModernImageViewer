using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    public enum CameraKind { ZoomIn, ZoomOut, Drift }

    /// <summary>
    /// What the camera sees: how far in, the centre of the view as shares of the picture, and how far it is turned, in
    /// degrees. Independent of size, so a resized window keeps the move.
    /// </summary>
    public readonly record struct CameraView(double Zoom, double X, double Y, double Angle = 0)
    {
        public static readonly CameraView Whole = new(1, 0.5, 0.5);

        /// <summary>
        /// A view of a person framed by the eyes: the eye line a third of the way down the view, as a camera operator
        /// frames a face, not in the middle. Covered as any view is, so at the top of a picture the eyes sit higher.
        /// </summary>
        public static CameraView OnEyes(double zoom, double eyeX, double eyeY, double angle = 0) =>
            new CameraView(zoom, eyeX, eyeY + 1.0 / (6 * Math.Max(1, zoom)), angle).Covered();

        /// <summary>The nearest view with picture all the way to its edges. A turn is covered when it is drawn (<see cref="TurnCover"/>).</summary>
        public CameraView Covered()
        {
            double zoom = Math.Max(1, Zoom);
            double margin = 0.5 / zoom;
            return new CameraView(zoom, Math.Clamp(X, margin, 1 - margin), Math.Clamp(Y, margin, 1 - margin), Angle);
        }

        /// <summary>
        /// How much more to zoom so a view turned this far still has picture in every corner, on a canvas of this shape. A
        /// view the canvas covers unturned has its centre at least half its own size from every edge; turned, its outline
        /// reaches cos + sin times the longer side's share further, so the zoom grows by that.
        /// </summary>
        public static double TurnCover(double angleDegrees, double width, double height)
        {
            if (Math.Abs(angleDegrees) < 0.001 || width < 1 || height < 1) return 1;
            double a = Math.Abs(angleDegrees) * Math.PI / 180;
            return Math.Cos(a) + Math.Sin(a) * Math.Max(width / height, height / width);
        }
    }

    /// <summary>How a move gets going and comes to rest. Each has a use: see <see cref="CameraCurves.Ease"/>.</summary>
    public enum CameraCurve { Steady, Gentle, Smooth, Cinematic, Settle, Snap }

    public static class CameraCurves
    {
        public static readonly CameraCurve[] All = { CameraCurve.Steady, CameraCurve.Gentle, CameraCurve.Smooth, CameraCurve.Cinematic, CameraCurve.Settle, CameraCurve.Snap };

        /// <summary>
        /// Steady: the same speed throughout, so it is still moving through a crossfade. Gentle: a sine ease, the mildest
        /// start and stop. Smooth: smoothstep, what every move used before there was a choice. Cinematic: a cubic ease, as
        /// Cinematic Slideshow moves, lingering at both ends. Settle: under way at once, gliding to rest. Snap: slow, a quick
        /// push, then a long settle, Cinematic Slideshow's snap zoom.
        /// </summary>
        public static double Ease(CameraCurve curve, double t)
        {
            t = Math.Clamp(t, 0, 1);
            switch (curve)
            {
                case CameraCurve.Steady: return t;
                case CameraCurve.Gentle: return -(Math.Cos(Math.PI * t) - 1) / 2;
                case CameraCurve.Cinematic: return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
                case CameraCurve.Settle: return 1 - Math.Pow(1 - t, 3);
                case CameraCurve.Snap:
                    if (t < 0.15) return t * 0.2;
                    if (t < 0.45)
                    {
                        double p = (t - 0.15) / 0.30;
                        double snap = p < 0.5 ? 16 * Math.Pow(p, 5) : 1 - Math.Pow(-2 * p + 2, 5) / 2;
                        return 0.03 + snap * 0.92;
                    }
                    return 0.95 + (1 - Math.Pow(1 - (t - 0.45) / 0.55, 3)) * 0.05;
                default: return t * t * (3 - 2 * t);
            }
        }

        /// <summary>A stored curve's name; anything unknown, including none, is Smooth, as every move was before.</summary>
        public static CameraCurve Parse(string? name) => Enum.TryParse<CameraCurve>(name, out var c) ? c : CameraCurve.Smooth;

        /// <summary>
        /// One curve from odds against each other, in <see cref="All"/>'s order. Snap is only for a zoom: on a drift it
        /// reads as a jolt. With nothing left on, Smooth.
        /// </summary>
        public static CameraCurve Pick(IReadOnlyList<double> odds, bool zoom, Random random)
        {
            var w = All.Select((c, i) => i < odds.Count && (zoom || c != CameraCurve.Snap) ? Math.Max(0, odds[i]) : 0).ToArray();
            double sum = w.Sum();
            if (sum <= 0) return CameraCurve.Smooth;
            double roll = random.NextDouble() * sum;
            for (int i = 0; i < w.Length; i++)
            {
                if (roll < w[i]) return All[i];
                roll -= w[i];
            }
            return CameraCurve.Smooth;
        }

        /// <summary>The share each curve takes of zoom moves (Snap included) and of drifts (Snap left out).</summary>
        public static (IReadOnlyDictionary<CameraCurve, double> Zooms, IReadOnlyDictionary<CameraCurve, double> Drifts) Shares(IReadOnlyList<double> odds)
        {
            IReadOnlyDictionary<CameraCurve, double> For(bool zoom)
            {
                var w = All.Select((c, i) => i < odds.Count && (zoom || c != CameraCurve.Snap) ? Math.Max(0, odds[i]) : 0).ToArray();
                double sum = w.Sum();
                return All.Select((c, i) => (c, sum <= 0 ? (c == CameraCurve.Smooth ? 1 : 0) : w[i] / sum)).ToDictionary(p => p.c, p => p.Item2);
            }
            return (For(true), For(false));
        }
    }

    /// <summary>
    /// One shot of a move: from one view to another over its share of the time, eased by its curve. Bow bends a pan's
    /// path to one side, as a share of its length (positive one way, negative the other). A shot whose start is not where
    /// the shot before it ended is a cut.
    /// </summary>
    public sealed record CameraShot(CameraView From, CameraView To, double Share, CameraCurve Curve, double Bow);

    /// <summary>
    /// How a planned move may vary, from the Layout Generator settings: the odds of each curve, how far a pan bends, how
    /// far the view turns (degrees, a range), and the chance of a two-shot scene where there is a face. None is the plain
    /// move every scene had before these were settings.
    /// </summary>
    public sealed record CameraStyle(IReadOnlyList<double> CurveOdds, double BowLeast, double BowMost, double TurnLeast, double TurnMost, double TwoShotChance,
        double ZoomLeast = 0, double ZoomMost = 0)
    {
        public static readonly CameraStyle Plain = new(new double[] { 0, 0, 1, 0, 0, 0 }, 0, 0, 0, 0, 0);

        /// <summary>How far this zoom goes, picked within the Camera zoom range; null without one, when Motion sets it.</summary>
        public double? PickZoom(Random random)
        {
            if (ZoomMost <= 1) return null;
            double least = Math.Max(1, Math.Min(ZoomLeast, ZoomMost)), most = Math.Max(ZoomLeast, ZoomMost);
            return least + random.NextDouble() * (most - least);
        }
    }

    /// <summary>How a floating cell sits at a moment, on top of its place in the layout, which does not change.</summary>
    public readonly record struct CellDrift(double X, double Y, double Turn, double Scale);

    /// <summary>
    /// Mild motion for one floating cell. It sits nearer than the grid, so it follows the camera a little further, which gives depth,
    /// and it floats a few pixels and under a degree on slow paths of its own. Front cells sit nearest.
    /// </summary>
    public sealed class CellFloat
    {
        private const double NearestDepth = 0.3, FarthestDepth = 0.08;
        // At full amount, as a share of the canvas's shorter side, and in degrees.
        private const double MostDrift = 0.012, MostTurn = 1.2;
        private const double ShortestPeriod = 11, LongestPeriod = 21;

        private readonly double _depth, _driftX, _driftY, _turn, _periodX, _periodY, _periodTurn, _phaseX, _phaseY, _phaseTurn;

        public CellFloat(int backToFront, int count, double amount, Random random)
        {
            amount = Math.Clamp(amount, 0, 1);
            double place = count > 1 ? backToFront / (double)(count - 1) : 1;
            _depth = amount <= 0 ? 0 : FarthestDepth + (NearestDepth - FarthestDepth) * place;
            double Side() => random.Next(2) == 0 ? 1 : -1;
            _driftX = amount * MostDrift * (0.6 + random.NextDouble() * 0.4) * Side();
            _driftY = amount * MostDrift * (0.6 + random.NextDouble() * 0.4) * Side();
            _turn = amount * MostTurn * (0.5 + random.NextDouble() * 0.5) * Side();
            double Period() => ShortestPeriod + random.NextDouble() * (LongestPeriod - ShortestPeriod);
            _periodX = Period();
            _periodY = Period();
            _periodTurn = Period();
            _phaseX = random.NextDouble() * Math.Tau;
            _phaseY = random.NextDouble() * Math.Tau;
            _phaseTurn = random.NextDouble() * Math.Tau;
        }

        /// <summary>Strength takes the float itself down to nothing when going home. The depth follows the camera there by itself.</summary>
        public CellDrift At(double seconds, CameraView view, double width, double height, double strength = 1)
        {
            double side = Math.Min(width, height);
            double x = _driftX * side * Math.Sin(_phaseX + Math.Tau * seconds / _periodX) * strength;
            double y = _driftY * side * Math.Sin(_phaseY + Math.Tau * seconds / _periodY) * strength;
            double turn = _turn * Math.Sin(_phaseTurn + Math.Tau * seconds / _periodTurn) * strength;
            x -= (view.X - 0.5) * width * view.Zoom * _depth;
            y -= (view.Y - 0.5) * height * view.Zoom * _depth;
            return new CellDrift(x, y, turn, 1 + (view.Zoom - 1) * _depth);
        }
    }

    /// <summary>Cut is used only between two views of one image (sequence beats); it is never in the transition odds.</summary>
    public enum LayoutTransition { Crossfade, ZoomThrough, ThroughDark, Slide, Cut }

    public static class LayoutTransitions
    {
        private static readonly LayoutTransition[] Kinds = { LayoutTransition.Crossfade, LayoutTransition.ZoomThrough, LayoutTransition.ThroughDark, LayoutTransition.Slide };

        /// <summary>The odds of each kind for the next change: the one just used is left out, except a crossfade.</summary>
        private static double[] Weights(double crossfade, double zoomThrough, double throughDark, double slide, LayoutTransition? last)
        {
            var w = new[] { Math.Max(0, crossfade), Math.Max(0, zoomThrough), Math.Max(0, throughDark), Math.Max(0, slide) };
            if (last is LayoutTransition l && l != LayoutTransition.Crossfade && Array.IndexOf(Kinds, l) is int i && i >= 0) w[i] = 0;
            return w;
        }

        /// <summary>
        /// One change of layout, from four odds against each other. The same one is not used twice running, except a
        /// crossfade; with every odds off, or nothing else left, it is a crossfade.
        /// </summary>
        public static LayoutTransition Pick(double crossfade, double zoomThrough, double throughDark, double slide, LayoutTransition? last, Random random)
        {
            var w = Weights(crossfade, zoomThrough, throughDark, slide, last);
            double sum = w.Sum();
            if (sum <= 0) return LayoutTransition.Crossfade;
            double roll = random.NextDouble() * sum;
            for (int i = 0; i < w.Length; i++)
            {
                if (roll < w[i]) return Kinds[i];
                roll -= w[i];
            }
            return LayoutTransition.Crossfade;
        }

        /// <summary>The share of changes each kind takes over a long run of <see cref="Pick"/>, the no-repeat rule included.</summary>
        public static IReadOnlyDictionary<LayoutTransition, double> Shares(double crossfade, double zoomThrough, double throughDark, double slide)
        {
            // The kind just used decides the odds of the next, so the long run is where repeating that settles.
            var share = new double[] { 1, 0, 0, 0 };
            for (int step = 0; step < 400; step++)
            {
                var next = new double[4];
                for (int from = 0; from < 4; from++)
                {
                    if (share[from] == 0) continue;
                    var w = Weights(crossfade, zoomThrough, throughDark, slide, Kinds[from]);
                    double sum = w.Sum();
                    if (sum <= 0) { next[0] += share[from]; continue; }
                    for (int to = 0; to < 4; to++) next[to] += share[from] * w[to] / sum;
                }
                // Half a step each time: the same long run, but it settles even where kinds alternate
                // (one kind on, never twice running, so every other change is a crossfade).
                for (int i = 0; i < 4; i++) share[i] = (share[i] + next[i]) / 2;
            }
            return Kinds.Select((k, i) => (k, share[i])).ToDictionary(p => p.k, p => p.Item2);
        }

        /// <summary>
        /// The four odds giving about the mix the earlier single "Transition variety" slider gave: at most 7 in 10 not a
        /// crossfade, those split zoom through 2, through dark 2, slide 1.
        /// </summary>
        public static (double Crossfade, double ZoomThrough, double ThroughDark, double Slide) OddsFromVariety(double variety)
        {
            double other = Math.Clamp(variety, 0, 1) * 0.7;
            if (other <= 0) return (1, 0, 0, 0);
            const double zoom = 1, dark = 1, slide = 0.5;
            double Other(double c, double k) => 1 - Shares(c, zoom * k, dark * k, slide * k)[LayoutTransition.Crossfade];

            // Searched rather than worked out, so the no-repeat rule is allowed for: the mix shown is the mix given.
            double top = SurpriseOptions.MostFavouredDesign;
            if (Other(top, 1) <= other)
            {
                // Crossfade at its top odds is still too few: make it rarer.
                double lo = 0, hi = top;
                for (int i = 0; i < 40; i++) { double mid = (lo + hi) / 2; if (Other(mid, 1) > other) lo = mid; else hi = mid; }
                return ((lo + hi) / 2, zoom, dark, slide);
            }
            // Too many changes for crossfade's top odds: keep it at the top and make the others rarer instead.
            double klo = 0, khi = 1;
            for (int i = 0; i < 40; i++) { double mid = (klo + khi) / 2; if (Other(top, mid) < other) klo = mid; else khi = mid; }
            double k = (klo + khi) / 2;
            return (top, zoom * k, dark * k, slide * k);
        }
    }

    /// <summary>
    /// One slow camera move over a still picture: the Ken Burns effect. Its zoom, drift and pacing follow what reads well in
    /// Cinematic. It knows nothing of how the picture is drawn, so it can move anything that takes a zoom and a pan.
    /// </summary>
    public sealed class CameraMove
    {
        // At the middle of the amount, the same intensity as a Cinematic show's default.
        private const double MostIntensity = 0.32;
        private const double WidestZoom = 1.03;
        private const int TriesForADifferentMove = 8;

        // At a curved path's full amount, how far a pan bows to one side, as a share of its length: Cinematic Slideshow's.
        public const double MostBow = 0.13;
        // A two-shot scene needs time for both shots to read.
        public const double ShortestTwoShotSeconds = 8;

        public CameraKind Kind { get; }
        public IReadOnlyList<CameraShot> Shots { get; }
        public CameraView Start => Shots[0].From;
        public CameraView End => Shots[^1].To;
        public double Seconds { get; }

        /// <summary>One shot, Smooth and straight unless told otherwise: the move every scene had before there was a choice.</summary>
        public CameraMove(CameraKind kind, CameraView start, CameraView end, double seconds, CameraCurve curve = CameraCurve.Smooth, double bow = 0)
            : this(kind, new[] { new CameraShot(start, end, 1, curve, bow) }, seconds) { }

        public CameraMove(CameraKind kind, IReadOnlyList<CameraShot> shots, double seconds)
        {
            Kind = kind;
            Shots = shots.Count > 0 ? shots : new[] { new CameraShot(CameraView.Whole, CameraView.Whole, 1, CameraCurve.Smooth, 0) };
            Seconds = seconds;
        }

        /// <summary>
        /// Each shot takes its share of the time, eased by its curve. Zoom changes by the same ratio over time, which reads
        /// as steady where a straight line would seem to slow down. A bent path bows to one side most at its middle.
        /// </summary>
        public CameraView At(double elapsedSeconds)
        {
            double t = Seconds <= 0 ? 1 : Math.Clamp(elapsedSeconds / Seconds, 0, 1);
            double total = Shots.Sum(s => Math.Max(0, s.Share));
            if (total <= 0) total = 1;
            double reached = 0;
            var shot = Shots[^1];
            double local = 1;
            foreach (var s in Shots)
            {
                double length = Math.Max(0, s.Share) / total;
                if (t <= reached + length || ReferenceEquals(s, Shots[^1]))
                {
                    shot = s;
                    local = length <= 0 ? 1 : Math.Clamp((t - reached) / length, 0, 1);
                    break;
                }
                reached += length;
            }

            double eased = CameraCurves.Ease(shot.Curve, local);
            double zoom = Math.Exp(Lerp(Math.Log(Math.Max(1, shot.From.Zoom)), Math.Log(Math.Max(1, shot.To.Zoom)), eased));
            double x = Lerp(shot.From.X, shot.To.X, eased), y = Lerp(shot.From.Y, shot.To.Y, eased);
            double dx = shot.To.X - shot.From.X, dy = shot.To.Y - shot.From.Y;
            double travel = Math.Sqrt(dx * dx + dy * dy);
            if (shot.Bow != 0 && travel > 0.005)
            {
                double off = shot.Bow * travel * Math.Sin(Math.PI * eased);
                x += -dy / travel * off;
                y += dx / travel * off;
            }
            return new CameraView(zoom, x, y, Lerp(shot.From.Angle, shot.To.Angle, eased)).Covered();
        }

        /// <summary>
        /// A move for this long, or none with no amount. The amount is 0 to 1. The same zoom looks rushed on a short layout and timid on a long one, so it scales with time.
        /// Never the same kind twice running, while another is possible. The style gives its curve, bend, turn, and the chance
        /// that a scene with a face becomes two shots; without one, it is the plain move.
        /// </summary>
        /// <param name="mostZoom">How far any view of the move may zoom: past it a face would be cut or the photo shown past
        /// the detail it has. A two-shot that would have no room to move in is not played; a zoom with no room becomes a drift.</param>
        /// <param name="subjectsAreEyes">The subjects are the eye lines of faces, so views on them are framed by the eyes
        /// (<see cref="CameraView.OnEyes"/>); false for a focus point, which is centred.</param>
        /// <param name="holdSeconds">How long the scene is on screen before the next change, when <paramref name="seconds"/>
        /// also runs on past it. A two-shot needs <see cref="ShortestTwoShotSeconds"/> of that: counting the run-on let one
        /// into a 5.5 s scene, its close shot gone a second after it came (Cinematic Slideshow never chained two moves under
        /// 8 s; a flip-book).</param>
        public static CameraMove? Plan(IReadOnlyList<(double X, double Y)> subjects, double amount, double zoomChance, double aimChance, double seconds, CameraKind? last, Random random, CameraKind? forcedKind = null, CameraStyle? style = null, double mostZoom = double.PositiveInfinity, bool subjectsAreEyes = true, double? holdSeconds = null)
        {
            if (amount <= 0 || seconds <= 0) return null;
            style ??= CameraStyle.Plain;
            double intensity = Math.Min(amount, 1) * MostIntensity * Math.Clamp(seconds / 12.0, 0.6, 1.25);
            mostZoom = Math.Max(1, mostZoom);

            bool roomForTwoShots = mostZoom >= 1.06 + intensity * 0.3 + 0.15;
            if (subjects.Count > 0 && roomForTwoShots && (holdSeconds ?? seconds) >= ShortestTwoShotSeconds && style.TwoShotChance > 0 && random.NextDouble() < style.TwoShotChance)
                return PlanTwoShots(subjects[random.Next(subjects.Count)], intensity, seconds, random, style, mostZoom);

            bool onSubject = subjects.Count > 0 && random.NextDouble() < aimChance;
            (double X, double Y) aim = onSubject
                ? subjects[random.Next(subjects.Count)]
                : (0.5 + (random.NextDouble() - 0.5) * 0.3, 0.5 + (random.NextDouble() - 0.5) * 0.3);
            // A view on a face is framed by its eyes; any other aim is centred.
            bool eyes = onSubject && subjectsAreEyes;
            CameraView View(double zoom, double x, double y, double angle = 0) =>
                eyes ? CameraView.OnEyes(zoom, x, y, angle) : new CameraView(zoom, x, y, angle).Covered();

            CameraKind kind;
            if (mostZoom < WidestZoom + 0.02)
            {
                // No room to zoom without cutting a face or losing detail: the move drifts instead.
                kind = CameraKind.Drift;
            }
            else if (forcedKind.HasValue)
            {
                kind = forcedKind.Value;
            }
            else
            {
                kind = ChooseKind(zoomChance, random);
                for (int i = 0; kind == last && i < TriesForADifferentMove; i++)
                    kind = ChooseKind(zoomChance, random);
            }

            var curve = CameraCurves.Pick(style.CurveOdds, kind != CameraKind.Drift, random);
            (double From, double To) turn = Turn(style, random);

            if (kind == CameraKind.Drift)
            {
                double zoom = Math.Min(Math.Max(1.06, 1 + intensity * 0.75), mostZoom);
                double margin = 0.5 / zoom;
                double travel = Math.Min(1 - 2 * margin, 0.08 + intensity * 0.8);
                bool sideways = random.NextDouble() < 0.75;
                // Max because at full travel, rounding can put the upper limit a hair below the lower one.
                // A drift across a face keeps the eyes on the upper third; one up or down the picture travels through it.
                double frameY = sideways && eyes ? aim.Y + 1.0 / (6 * zoom) : aim.Y;
                double along = Math.Clamp((sideways ? aim.X : frameY) - travel / 2, margin, Math.Max(margin, 1 - margin - travel));
                double from = along, to = along + travel;
                if (random.Next(2) == 0) (from, to) = (to, from);
                var start = sideways ? new CameraView(zoom, from, frameY, turn.From) : new CameraView(zoom, aim.X, from, turn.From);
                var end = sideways ? new CameraView(zoom, to, frameY, turn.To) : new CameraView(zoom, aim.X, to, turn.To);
                return new CameraMove(kind, start.Covered(), end.Covered(), seconds, curve, Bow(style, random));
            }

            var wide = View(WidestZoom, aim.X, aim.Y);
            var close = View(Math.Min(mostZoom, Math.Max(WidestZoom + 0.02, style.PickZoom(random) ?? 1 + intensity * 1.5)), aim.X, aim.Y);
            return kind == CameraKind.ZoomIn
                ? new CameraMove(kind, wide with { Angle = turn.From }, close with { Angle = turn.To }, seconds, curve)
                : new CameraMove(kind, close with { Angle = turn.From }, wide with { Angle = turn.To }, seconds, curve);
        }

        /// <summary>
        /// Two shots, as Cinematic Slideshow makes them where there is a face: a slow wide shot, then either a snap zoom in to
        /// the face, or a cut to the face and a slow push in.
        /// </summary>
        private static CameraMove PlanTwoShots((double X, double Y) face, double intensity, double seconds, Random random, CameraStyle style, double mostZoom)
        {
            var calm = CameraCurves.Pick(style.CurveOdds, zoom: false, random);
            (double From, double To) turn = Turn(style, random);
            double wideZoom = 1.06 + intensity * 0.3;
            // The close-up is always nearer than the wide shot, so the second shot reads as moving in.
            double closeZoom = style.PickZoom(random) is double picked ? Math.Max(wideZoom + 0.15, picked) : Math.Max(1.35, 1 + intensity * 2.5);
            closeZoom = Math.Max(wideZoom + 0.15, Math.Min(closeZoom, mostZoom));
            // The wide shot drifts a little toward the face, so the second shot follows on from it.
            var wideFrom = new CameraView(wideZoom, 0.5 + (0.5 - face.X) * 0.15, 0.5 + (0.5 - face.Y) * 0.15, turn.From).Covered();
            var wideTo = new CameraView(wideZoom, 0.5 + (face.X - 0.5) * 0.15, 0.5 + (face.Y - 0.5) * 0.15, turn.From).Covered();
            var close = CameraView.OnEyes(closeZoom, face.X, face.Y, turn.To);

            if (random.Next(2) == 0)
            {
                // Discovery: the wide shot, then a snap zoom in on the face.
                return new CameraMove(CameraKind.ZoomIn, new[]
                {
                    new CameraShot(wideFrom, wideTo, 0.55, calm, Bow(style, random)),
                    new CameraShot(wideTo, close, 0.45, CameraCurve.Snap, 0)
                }, seconds);
            }
            // Reveal: the wide shot, then a cut to the face, pushing in slowly.
            var nearer = CameraView.OnEyes(Math.Min(closeZoom * 1.08, Math.Max(closeZoom, mostZoom)), face.X, face.Y, turn.To);
            return new CameraMove(CameraKind.ZoomIn, new[]
            {
                new CameraShot(wideFrom, wideTo, 0.5, calm, Bow(style, random)),
                new CameraShot(close, nearer, 0.5, CameraCurves.Pick(style.CurveOdds, zoom: false, random), 0)
            }, seconds);
        }

        private static double Bow(CameraStyle style, Random random)
        {
            double least = Math.Clamp(Math.Min(style.BowLeast, style.BowMost), 0, 1), most = Math.Clamp(Math.Max(style.BowLeast, style.BowMost), 0, 1);
            double amount = least + random.NextDouble() * (most - least);
            return amount * MostBow * (random.Next(2) == 0 ? 1 : -1);
        }

        /// <summary>A turn from the range, through the middle: from half of it one way to half the other.</summary>
        private static (double From, double To) Turn(CameraStyle style, Random random)
        {
            double least = Math.Max(0, Math.Min(style.TurnLeast, style.TurnMost)), most = Math.Max(0, Math.Max(style.TurnLeast, style.TurnMost));
            if (most <= 0) return (0, 0);
            double degrees = (least + random.NextDouble() * (most - least)) * (random.Next(2) == 0 ? 1 : -1);
            return (-degrees / 2, degrees / 2);
        }

        private static CameraKind ChooseKind(double zoomChance, Random random)
        {
            if (random.NextDouble() >= zoomChance) return CameraKind.Drift;
            return random.Next(2) == 0 ? CameraKind.ZoomIn : CameraKind.ZoomOut;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}

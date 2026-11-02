using System;
using System.Collections.Generic;

namespace ModernImageViewer
{
    public enum CameraKind { ZoomIn, ZoomOut, Drift }

    /// <summary>What the camera sees: how far in, and the centre of the view as shares of the picture. Independent of size, so a resized window keeps the move.</summary>
    public readonly record struct CameraView(double Zoom, double X, double Y)
    {
        public static readonly CameraView Whole = new(1, 0.5, 0.5);

        /// <summary>The nearest view with picture all the way to its edges.</summary>
        public CameraView Covered()
        {
            double zoom = Math.Max(1, Zoom);
            double margin = 0.5 / zoom;
            return new CameraView(zoom, Math.Clamp(X, margin, 1 - margin), Math.Clamp(Y, margin, 1 - margin));
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

    public enum LayoutTransition { Crossfade, ZoomThrough, ThroughDark, Slide }

    public static class LayoutTransitions
    {
        // The most that are anything other than a crossfade, which suits photographs best and stays common.
        private const double MostOther = 0.7;

        /// <summary>Variety is 0 to 1, and 0 is always a crossfade. Slide stands out most, so it comes up least. The same one is not used twice running, except a crossfade.</summary>
        public static LayoutTransition Choose(double variety, LayoutTransition? last, Random random)
        {
            if (random.NextDouble() >= Math.Clamp(variety, 0, 1) * MostOther) return LayoutTransition.Crossfade;
            for (int i = 0; i < 8; i++)
            {
                double pick = random.NextDouble();
                var choice = pick < 0.4 ? LayoutTransition.ZoomThrough : pick < 0.8 ? LayoutTransition.ThroughDark : LayoutTransition.Slide;
                if (choice != last) return choice;
            }
            return LayoutTransition.Crossfade;
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

        public CameraKind Kind { get; }
        public CameraView Start { get; }
        public CameraView End { get; }
        public double Seconds { get; }

        public CameraMove(CameraKind kind, CameraView start, CameraView end, double seconds)
        {
            Kind = kind;
            Start = start;
            End = end;
            Seconds = seconds;
        }

        /// <summary>Eased at both ends. Zoom changes by the same ratio over time, which reads as steady where a straight line would seem to slow down.</summary>
        public CameraView At(double elapsedSeconds)
        {
            double t = Seconds <= 0 ? 1 : Math.Clamp(elapsedSeconds / Seconds, 0, 1);
            double eased = t * t * (3 - 2 * t);
            double zoom = Math.Exp(Lerp(Math.Log(Math.Max(1, Start.Zoom)), Math.Log(Math.Max(1, End.Zoom)), eased));
            return new CameraView(zoom, Lerp(Start.X, End.X, eased), Lerp(Start.Y, End.Y, eased)).Covered();
        }

        /// <summary>
        /// A move for this long, or none with no amount. The amount is 0 to 1. The same zoom looks rushed on a short layout and timid on a long one, so it scales with time.
        /// Never the same kind twice running, while another is possible.
        /// </summary>
        public static CameraMove? Plan(IReadOnlyList<(double X, double Y)> subjects, double amount, double zoomChance, double aimChance, double seconds, CameraKind? last, Random random, CameraKind? forcedKind = null)
        {
            if (amount <= 0 || seconds <= 0) return null;
            double intensity = Math.Min(amount, 1) * MostIntensity * Math.Clamp(seconds / 12.0, 0.6, 1.25);
            (double X, double Y) aim = subjects.Count > 0 && random.NextDouble() < aimChance
                ? subjects[random.Next(subjects.Count)]
                : (0.5 + (random.NextDouble() - 0.5) * 0.3, 0.5 + (random.NextDouble() - 0.5) * 0.3);

            CameraKind kind;
            if (forcedKind.HasValue)
            {
                kind = forcedKind.Value;
            }
            else
            {
                kind = ChooseKind(zoomChance, random);
                for (int i = 0; kind == last && i < TriesForADifferentMove; i++)
                    kind = ChooseKind(zoomChance, random);
            }

            if (kind == CameraKind.Drift)
            {
                double zoom = Math.Max(1.06, 1 + intensity * 0.75);
                double margin = 0.5 / zoom;
                double travel = Math.Min(1 - 2 * margin, 0.08 + intensity * 0.8);
                bool sideways = random.NextDouble() < 0.75;
                // Max because at full travel, rounding can put the upper limit a hair below the lower one.
                double along = Math.Clamp((sideways ? aim.X : aim.Y) - travel / 2, margin, Math.Max(margin, 1 - margin - travel));
                double from = along, to = along + travel;
                if (random.Next(2) == 0) (from, to) = (to, from);
                var start = sideways ? new CameraView(zoom, from, aim.Y) : new CameraView(zoom, aim.X, from);
                var end = sideways ? new CameraView(zoom, to, aim.Y) : new CameraView(zoom, aim.X, to);
                return new CameraMove(kind, start.Covered(), end.Covered(), seconds);
            }

            var wide = new CameraView(WidestZoom, aim.X, aim.Y).Covered();
            var close = new CameraView(Math.Max(WidestZoom + 0.02, 1 + intensity * 1.5), aim.X, aim.Y).Covered();
            return kind == CameraKind.ZoomIn ? new CameraMove(kind, wide, close, seconds) : new CameraMove(kind, close, wide, seconds);
        }

        private static CameraKind ChooseKind(double zoomChance, Random random)
        {
            if (random.NextDouble() >= zoomChance) return CameraKind.Drift;
            return random.Next(2) == 0 ? CameraKind.ZoomIn : CameraKind.ZoomOut;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}

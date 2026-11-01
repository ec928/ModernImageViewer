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
        public static CameraMove? Plan(IReadOnlyList<(double X, double Y)> subjects, double amount, double zoomChance, double aimChance, double seconds, CameraKind? last, Random random)
        {
            if (amount <= 0 || seconds <= 0) return null;
            double intensity = Math.Min(amount, 1) * MostIntensity * Math.Clamp(seconds / 12.0, 0.6, 1.25);
            (double X, double Y) aim = subjects.Count > 0 && random.NextDouble() < aimChance
                ? subjects[random.Next(subjects.Count)]
                : (0.5 + (random.NextDouble() - 0.5) * 0.3, 0.5 + (random.NextDouble() - 0.5) * 0.3);

            CameraKind kind = ChooseKind(zoomChance, random);
            for (int i = 0; kind == last && i < TriesForADifferentMove; i++)
                kind = ChooseKind(zoomChance, random);

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

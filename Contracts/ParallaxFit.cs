using System;
using System.Collections.Generic;

namespace ModernImageViewer
{
    /// <summary>
    /// Whether a depth map is worth the parallax warp. The pass line is the six pictures that looked right
    /// (giraffes, sloth, impala, dunes, eclipse sea, temple): a near band and a far band in different places.
    /// A picture is excluded only when a measure falls short of that by a clear gap. One measure in the gap,
    /// and none excluded, is unsure, and the Parallax preset leaves those out too.
    /// A near mass with more near ground between it and the far mass (someone sitting on the floor) is excluded
    /// as well: both sit near the middle of the depth, so the warp barely moves them.
    /// </summary>
    public static class ParallaxFit
    {
        public enum Verdict { Pass, Unsure, Exclude }

        // Near is the closest part of the normalized map, far the farthest. 0.62 and 0.38 are the bands measured on the six.
        private const float NearAt = 0.62f, FarAt = 0.38f;
        // Past the near rim, toward the far mass, by this share of the short side. The eclipse sea's measured step landed at 0.34.
        private const double BackdropStep = 0.15, BackdropPass = 0.40, BackdropUnsure = 0.50;
        private const int BackdropSamples = 8;

        /// <summary>
        /// <paramref name="near"/> is one value per pixel, 1 the nearest, already grown and blurred.
        /// <paramref name="rawSpan"/> is the model's own range, before that normalization.
        /// </summary>
        public static Verdict Judge(float[] near, int width, int height, float rawSpan)
        {
            if (near == null || near.Length != width * height || width < 1 || height < 1)
                return Verdict.Exclude;

            Band(near, width, height, true, out double nearFrac, out double nearX, out double nearY);
            Band(near, width, height, false, out double farFrac, out double farX, out double farY);
            double separation = Math.Sqrt((nearX - farX) * (nearX - farX) + (nearY - farY) * (nearY - farY));

            int worst = 1;
            worst = Math.Min(worst, SpanBand(rawSpan));
            worst = Math.Min(worst, NearBand(nearFrac));
            worst = Math.Min(worst, FarBand(farFrac));
            worst = Math.Min(worst, SeparationBand(nearFrac > 0 && farFrac > 0 ? separation : 0));
            worst = Math.Min(worst, BackdropBand(near, width, height, nearX, nearY, farX, farY, nearFrac, farFrac));
            return worst < 0 ? Verdict.Exclude : worst == 0 ? Verdict.Unsure : Verdict.Pass;
        }

        // 1 pass, 0 unsure, -1 exclude. The eclipse sea's measured span was 3.79, so the pass line is 3.7.
        private static int SpanBand(float raw) => raw >= 3.7f ? 1 : raw >= 2.9f ? 0 : -1;

        private static int NearBand(double frac)
        {
            if (frac < 0.06 || frac > 0.55) return -1;
            if (frac < 0.10 || frac > 0.45) return 0;
            return 1;
        }

        private static int FarBand(double frac) => frac >= 0.45 ? 1 : frac >= 0.35 ? 0 : -1;

        private static int SeparationBand(double distance) => distance >= 0.20 ? 1 : distance >= 0.12 ? 0 : -1;

        // The rim of the near mass that faces the far mass. A step further out should already be far. Too few samples, and the measure is skipped.
        private static int BackdropBand(float[] near, int width, int height, double nearX, double nearY, double farX, double farY, double nearFrac, double farFrac)
        {
            if (nearFrac <= 0 || farFrac <= 0) return 1;
            double ncx = nearX * width, ncy = nearY * height;
            double dx = farX * width - ncx, dy = farY * height - ncy;
            double apart = Math.Sqrt(dx * dx + dy * dy);
            if (apart < 1) return 1;
            double ux = dx / apart, uy = dy / apart;
            int dist = Math.Max(1, (int)Math.Round(BackdropStep * Math.Min(width, height)));
            var samples = new List<float>();
            for (int row = 0; row < height; row++)
                for (int col = 0; col < width; col++)
                {
                    if (near[row * width + col] < NearAt || !IsRim(near, width, height, col, row)) continue;
                    double vx = col - ncx, vy = row - ncy;
                    double vlen = Math.Sqrt(vx * vx + vy * vy);
                    if (vlen < 1) continue;
                    double ox = vx / vlen, oy = vy / vlen;
                    if (ox * ux + oy * uy <= 0.5) continue;
                    int sc = (int)Math.Round(col + ox * dist);
                    int sr = (int)Math.Round(row + oy * dist);
                    if ((uint)sc >= (uint)width || (uint)sr >= (uint)height) continue;
                    samples.Add(near[sr * width + sc]);
                }
            if (samples.Count < BackdropSamples) return 1;
            samples.Sort();
            int mid = samples.Count / 2;
            double median = samples.Count % 2 == 1 ? samples[mid] : (samples[mid - 1] + samples[mid]) / 2.0;
            if (median <= BackdropPass) return 1;
            if (median <= BackdropUnsure) return 0;
            return -1;
        }

        private static bool IsRim(float[] near, int width, int height, int col, int row)
        {
            if (col == 0 || row == 0 || col == width - 1 || row == height - 1) return true;
            int i = row * width + col;
            return near[i - 1] < NearAt || near[i + 1] < NearAt || near[i - width] < NearAt || near[i + width] < NearAt;
        }

        private static void Band(float[] near, int width, int height, bool close, out double frac, out double x, out double y)
        {
            double sx = 0, sy = 0;
            int count = 0;
            for (int row = 0; row < height; row++)
                for (int col = 0; col < width; col++)
                {
                    float value = near[row * width + col];
                    if (close ? value < NearAt : value > FarAt) continue;
                    count++;
                    sx += col;
                    sy += row;
                }
            frac = count / (double)(width * height);
            if (count == 0) { x = 0; y = 0; return; }
            x = sx / count / width;
            y = sy / count / height;
        }
    }
}

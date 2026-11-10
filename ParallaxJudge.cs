using ModernImageViewer.Contracts;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// Prototype (branch prototype-titles-3d): whether a photo suits 3D, and which motion. Ed's rule (2026-11-07):
    ///   a clear subject with a clear, distant background behind it plays Slide;
    ///   a clear subject that can be rotated, without an ideal background, plays Rock;
    ///   anything else is left out of a Parallax show.
    /// Measured: the subject (SubjectMasks); the depth step at the subject's own edge, the subject's rim against a ring
    /// of background just around it; and how much texture that ring has. Calibrated on Ed's Pictures folder: the
    /// photos he called rubbish for both motions (a wheel on the floor, people against a near wall, landscapes, a
    /// building against plain sky) all step 0.36 or less at the edge; the subjects that play well step 0.40 or more.
    /// Sway (2026-11-10): the local depth step is the most a picture may take. The gap behind the subject may hold that down.
    /// The result is never under 1.6%, because 0.4% only vibrates. A small gap cannot turn a mild step up.
    /// Slide is kept only when that cut is one solid subject on a background whose depth stays in one band. Anything else that
    /// reached Slide is left out. One solid shape against a clean sky can still pass: the models do not know a building from an animal.
    /// </summary>
    internal static class ParallaxJudge
    {
        public enum Fit { Out, Rock, Slide }

        public sealed record Judgement(Fit Fit, string Why, float Area, float Edge, float Texture, float Sides, float Gap = 0);

        // Ed's marks on 73 clips (2026-11-07): Rock needs only a well-defined subject (13 of 14 passed; the fail was a
        // "subject" covering 92% of the picture and three of its edges). Slide's passes and fails are not separated by any
        // of these measures (the best, the outline's sureness, gets 46 of 59 right); its fails are mostly buildings and
        // trees, which only a model that knows what things are could tell. A depth step of 0.30 to 0.40 passed as often as
        // one above 0.40, so the step only has to reach 0.30.
        public const float LeastEdge = 0.30f;
        public const float LeastTexture = 0.03f;
        public const float MostSlideArea = 0.65f;
        public const float MostSlideSides = 0.3f;
        public const float MostSlideVague = 0.085f;
        public const float MostFarBackgroundP90 = 0.135f;
        // A subject this large, or touching this much of the edges, is the picture, not a subject in it.
        public const float MostArea = 0.88f;
        public const float MostSides = 0.7f;
        // The texture was calibrated on photos 768 across; measured on the 518-across depth map's brightness it reads about
        // 1.08 times higher (median over ten photos measured both ways, 2026-11-07).
        private const float TextureScale = 1f / 1.08f;

        private static readonly ConcurrentDictionary<string, Judgement> Made = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Threading.SemaphoreSlim FaceGate = new(1, 1);

        public static bool TryGetCached(string path, out Judgement? judgement) =>
            Made.TryGetValue(path, out judgement);
        private static Windows.Media.FaceAnalysis.FaceDetector? _detector;
        private static bool _detectorLoaded;

        private static async Task<PhotoFacts?> EnsureFactsAsync(string path)
        {
            if (PhotoFactsReader.TryGetCached(path, out var cached)) return cached;
            await FaceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (PhotoFactsReader.TryGetCached(path, out cached)) return cached;
                if (!_detectorLoaded)
                {
                    _detector = await PhotoFactsReader.CreateDetectorAsync().ConfigureAwait(false);
                    _detectorLoaded = true;
                }
                return await PhotoFactsReader.ReadAsync(path, _detector).ConfigureAwait(false);
            }
            finally
            {
                FaceGate.Release();
            }
        }

        /// <summary>The judgement for a photo, made once and kept. Null when a model is missing or the photo cannot be read.</summary>
        public static async Task<Judgement?> ForAsync(string path)
        {
            if (Made.TryGetValue(path, out var have)) return have;
            var map = await DepthMaps.ForAsync(path).ConfigureAwait(false);
            if (map == null) return null;
            if (map.Raw == null || map.Light == null)
            {
                var screen = new Judgement(Fit.Out, "looks like a screen capture", 0, 0, 0, 0);
                Made[path] = screen;
                return screen;
            }
            var subject = await SubjectMasks.ForAsync(path).ConfigureAwait(false);
            if (subject == null) return null;
            bool hasFaces = false;
            if (subject.Clear && subject.Area <= MostArea && subject.Sides < MostSides)
            {
                var facts = await EnsureFactsAsync(path).ConfigureAwait(false);
                if (facts?.ScreenLike == true)
                {
                    var screen = new Judgement(Fit.Out, "looks like a screen capture", subject.Area, 0, 0, subject.Sides);
                    Made[path] = screen;
                    return screen;
                }
                hasFaces = facts?.Faces.Count > 0;
            }
            var judged = await Task.Run(() => Judge(map, subject, hasFaces)).ConfigureAwait(false);
            Made[path] = judged;
            PlayTiming.Write($"3D: judged {Path.GetFileName(path)}: {judged.Fit} ({judged.Why}), subject {judged.Area:0.00}, edge {judged.Edge:0.00}, gap {judged.Gap:0.00}, texture {judged.Texture:0.000}, sides {judged.Sides:0.00}, faces {hasFaces}");
            return judged;
        }

        private static Judgement Judge(IParallaxMap map, IParallaxMask subject, bool hasFaces = false)
        {
            if (!subject.Clear) return new Judgement(Fit.Out, subject.Why, subject.Area, 0, 0, subject.Sides);
            if (subject.Area > MostArea || subject.Sides >= MostSides)
                return new Judgement(Fit.Out, "the subject is the whole picture", subject.Area, 0, 0, subject.Sides);
            int w = map.Width, h = map.Height, n = w * h;
            var fg = new bool[n];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(subject.Size - 1, (int)((y + 0.5f) * subject.Size / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(subject.Size - 1, (int)((x + 0.5f) * subject.Size / w));
                    fg[y * w + x] = subject.Alpha[sy * subject.Size + sx] > 127;
                }
            }
            int r1 = Math.Max(2, (int)(0.01 * w)), r2 = Math.Max(6, (int)(0.06 * w));
            var nearRing = Grow(fg, w, h, r1);
            var farRing = Grow(fg, w, h, r2);
            var notFg = new bool[n];
            for (int i = 0; i < n; i++) notFg[i] = !fg[i];
            var deepInside = Grow(notFg, w, h, r2); // everything within r2 of the background: its complement is the subject's core
            var rim = new System.Collections.Generic.List<byte>();
            var ring = new System.Collections.Generic.List<byte>();
            var allFg = new System.Collections.Generic.List<byte>(n / 3);
            var allBg = new System.Collections.Generic.List<byte>(n / 2);
            double texture = 0;
            int ringCount = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (fg[i]) allFg.Add(map.Raw![i]);
                    else allBg.Add(map.Raw![i]);
                    if (fg[i] && deepInside[i]) rim.Add(map.Raw![i]);
                    if (farRing[i] && !nearRing[i])
                    {
                        ring.Add(map.Raw![i]);
                        texture += Sobel(map.Light!, w, h, x, y);
                        ringCount++;
                    }
                }
            if (rim.Count == 0 || ring.Count == 0 || allFg.Count == 0 || allBg.Count == 0)
                return new Judgement(Fit.Out, "no background around the subject", subject.Area, 0, 0, subject.Sides);
            float edge = (Median(rim) - Median(ring)) / 255f;
            float gap = (Median(allFg) - Median(allBg)) / 255f;
            float tex = (float)(texture / ringCount / 255.0 * TextureScale);
            Judgement Made(Fit fit, string why) => new Judgement(fit, why, subject.Area, edge, tex, subject.Sides, gap);

            // Strict Rock filter (Ed, 2026-11-08):
            // - Photos with human faces play Rock when the people step slightly in front of their surroundings (edge >= 0.15, gap >= 0.12).
            // - Non-face photos without a strong 3D depth step (edge < 0.30 or gap < 0.25) are landscapes or occluded subjects in foliage and are excluded.
            if (hasFaces)
            {
                if (edge < 0.15f || gap < 0.12f)
                    return Made(Fit.Out, "people against a flat background");
            }
            else
            {
                if (edge < LeastEdge || gap < 0.25f)
                    return Made(Fit.Out, "small depth step at its edge");
            }

            Judgement FallbackOrOut(string why)
            {
                if (hasFaces)
                    return Made(Fit.Rock, why);
                // Non-face Rock requires a strong 3D close-up subject (edge >= 0.40, gap >= 0.40) that is either large (area >= 0.48),
                // against a plain backdrop (tex < 0.03), or a sharp indoor/close-up subject (area >= 0.15, vague <= 0.085, tex < 0.16).
                // Outdoor landscapes, cliffs, sand dunes, trees, umbrellas and foliage are excluded instead of distorting in Rock.
                bool cleanRock = edge >= 0.40f && gap >= 0.40f &&
                    (subject.Area >= 0.48f || tex < LeastTexture || (subject.Area >= 0.15f && subject.Vague <= MostSlideVague && tex < 0.16f));
                return Made(cleanRock ? Fit.Rock : Fit.Out, why);
            }

            if (edge < LeastEdge) return FallbackOrOut("small depth step at its edge");
            if (subject.Vague > MostSlideVague) return FallbackOrOut("outline not sharp enough to slide");
            if (tex < LeastTexture) return FallbackOrOut("plain background");
            if (subject.Area > MostSlideArea) return FallbackOrOut("subject fills the picture");
            if (subject.Sides >= MostSlideSides) return FallbackOrOut("subject spans the picture");
            if (subject.Top > 0.05f && subject.Bottom > 0.05f)
                return Made(hasFaces ? Fit.Rock : Fit.Out, "subject spans top to bottom");

            // Holistic distant-plane check: verify the remaining background sits on a distant plane (bgP90 <= 0.135) and that
            // connected foreground does not swallow >65% of the frame from a small subject (tabletop/floor/hillside/swamp).
            var lightF = new float[n];
            var depthF = new float[n];
            var subjF = new float[n];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(subject.Size - 1, (int)((y + 0.5f) * subject.Size / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(subject.Size - 1, (int)((x + 0.5f) * subject.Size / w));
                    int i = y * w + x;
                    lightF[i] = map.Light![i] / 255f;
                    depthF[i] = map.Raw![i] / 255f;
                    subjF[i] = subject.Alpha[sy * subject.Size + sx] / 255f;
                }
            }
            ParallaxLayers.HolisticAlpha(lightF, depthF, map.Raw!, subjF, w, h, out float unionArea, out float bgP90);
            if (bgP90 > MostFarBackgroundP90)
                return FallbackOrOut("background is not a distant plane");
            if (unionArea > 0.65f && subject.Area < 0.45f)
                return FallbackOrOut("subject sits on a continuous ground plane");
            // A Slide that is not one subject on a clean background is left out. It is not turned into Sway.
            if (!SurpriseOptions.SlideIsOneCleanCut(fg, map.Raw!, w, h))
                return Made(Fit.Out, "not one subject on a clean background");
            return Made(Fit.Slide, "clear subject, distant background");
        }

        private static float Median(System.Collections.Generic.List<byte> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>The gradient's size at a pixel, on 0 to 255 brightness, as OpenCV's 3x3 Sobel pair.</summary>
        private static double Sobel(byte[] a, int w, int h, int x, int y)
        {
            int P(int dx, int dy) => a[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
            int gx = P(1, -1) + 2 * P(1, 0) + P(1, 1) - P(-1, -1) - 2 * P(-1, 0) - P(-1, 1);
            int gy = P(-1, 1) + 2 * P(0, 1) + P(1, 1) - P(-1, -1) - 2 * P(0, -1) - P(1, -1);
            return Math.Sqrt(gx * gx + gy * gy);
        }

        /// <summary>Grown by a square of radius r, in two passes.</summary>
        private static bool[] Grow(bool[] mask, int w, int h, int r)
        {
            var row = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int last = -100000;
                for (int x = 0; x < w; x++) { if (mask[y * w + x]) last = x; row[y * w + x] = x - last <= r; }
                last = 100000;
                for (int x = w - 1; x >= 0; x--) { if (mask[y * w + x]) last = x; if (last - x <= r) row[y * w + x] = true; }
            }
            var grown = new bool[w * h];
            for (int x = 0; x < w; x++)
            {
                int last = -100000;
                for (int y = 0; y < h; y++) { if (row[y * w + x]) last = y; grown[y * w + x] = y - last <= r; }
                last = 100000;
                for (int y = h - 1; y >= 0; y--) { if (row[y * w + x]) last = y; if (last - y <= r) grown[y * w + x] = true; }
            }
            return grown;
        }
    }
}

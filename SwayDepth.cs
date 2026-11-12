using System;

namespace ModernImageViewer
{
    /// <summary>
    /// Sway's depth with a crisp edge at the subject's own outline. The depth map fades from subject to background,
    /// sometimes over a few of its pixels (ed.jpg: ears half in each depth, sliding behind the head at one peak and taken
    /// for uncovered wall at the other), sometimes over far more (IMGP0801: the outer hair ramps from 0.04 to 0.30 over
    /// about 25 px, so it tore at 6%). Within <see cref="RimShare"/> of the width from the mask's edge: inside the mask the
    /// nearest depth found within that distance inside the mask is taken (hair and ears join the head), outside it the
    /// farthest found outside (the halo joins the wall). Farther from the edge the depth is unchanged. The outline itself
    /// comes from the mask at full size. Checked off screen at 6% on ed.jpg and IMGP0801.JPG, 2026-11-11.
    /// </summary>
    internal static class SwayDepth
    {
        /// <summary>Most pixels across the result; the photo's own width if smaller.</summary>
        public const int MostWidth = 1536;

        /// <summary>How far from the mask's edge the depth is evened out, as a share of the width.</summary>
        public const float RimShare = 0.03f;

        /// <summary>How far stray hair may join the subject beyond the mask, as a share of the width.</summary>
        public const float JoinShare = 0.02f;

        /// <summary>Stray hair joins when its colour is nearer the hair's than this times its distance to the wall's.</summary>
        private const float JoinBias = 1.5f;

        /// <summary>Only beside hair: the subject next to it no brighter than this (0-255) ...</summary>
        private const float JoinDarkest = 40f;

        /// <summary>... and no more coloured than this (largest minus smallest channel, 0-255).</summary>
        private const float JoinGreyest = 15f;

        /// <summary>The smooth outline: the mask blurred over this many pixels (twice), cut at <see cref="EdgeCut"/>.</summary>
        private const int EdgeBlur = 1;

        /// <summary>See <see cref="EdgeBlur"/>.</summary>
        private const float EdgeCut = 0.1f;

        /// <summary>Background the subject fully encloses and under this share of the picture keeps the 0.35.1 depth.</summary>
        private const float HoleShare = 0.0025f;

        /// <summary>
        /// <paramref name="grey"/> is the depth map (gw x gh, bytes). <paramref name="alpha"/> is the subject mask, size x size,
        /// stretched over the whole photo. Returns BGRA bytes at tw x th, grey in every colour.
        /// <para>With the photo (<paramref name="photo"/>, BGRA pw x ph) the outline is also cleaned up (v0.35.4, built on
        /// IMGP0801, checked on IMGP0997 and ed.jpg, 2026-11-12): the hair's soft edge and stray hair move with the subject, so
        /// they are no longer left on the wall as a ghost of the outline, and the edge rises in depth in one step, so it does
        /// not shimmer. Without the photo the depth is as v0.35.1 made it.</para>
        /// </summary>
        public static byte[] Crisp(byte[] grey, int gw, int gh, byte[] alpha, int size, int tw, int th,
            byte[]? photo = null, int pw = 0, int ph = 0)
        {
            // At the depth map's own size: the mask, the nearest depth inside it and the farthest outside it within the rim,
            // and how much of each pixel lies within the rim.
            int gn = gw * gh;
            var d = new float[gn];
            var inside = new bool[gn];
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    int i = y * gw + x;
                    d[i] = grey[i] / 255f;
                    inside[i] = SampleMask(alpha, size, (x + 0.5f) / gw, (y + 0.5f) / gh) >= 0.5f;
                }
            int r = Math.Max(2, (int)Math.Round(gw * RimShare));
            var near = new float[gn];
            var far = new float[gn];
            var markIn = new float[gn];
            for (int i = 0; i < gn; i++)
            {
                near[i] = inside[i] ? d[i] : -1f;
                far[i] = inside[i] ? 2f : d[i];
                markIn[i] = inside[i] ? 1f : 0f;
            }
            near = FromInside(d, inside, gw, gh, r);
            far = Filter(far, gw, gh, r, false);
            var anyIn = Filter(markIn, gw, gh, r, true);
            var allIn = Filter(markIn, gw, gh, r, false);
            var rim = new float[gn];
            for (int i = 0; i < gn; i++)
            {
                rim[i] = anyIn[i] != allIn[i] ? 1f : 0f;
                if (near[i] < 0) near[i] = d[i];
                if (far[i] > 1.5f) far[i] = d[i];
            }
            rim = Blur(rim, gw, gh, Math.Max(1, r / 3));

            // The cleaned-up outline, at the result's size. Off without the photo.
            float[]? nearJoined = null, joinSoft = null, smoothEdge = null;
            bool[]? keepOld = null;
            if (photo != null && pw > 0 && ph > 0 && photo.Length >= pw * ph * 4)
            {
                var px = pw == tw && ph == th ? photo : Resample(photo, pw, ph, tw, th);
                var mf = new float[tw * th];
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                        mf[y * tw + x] = SampleMask(alpha, size, (x + 0.5f) / tw, (y + 0.5f) / th);

                // Stray hair: pixels just outside the mask, beside hair, that look like the hair rather than the wall.
                int reach = Math.Max(2, (int)Math.Round(tw * JoinShare));
                var join = JoinStrayHair(mf, px, tw, th, reach);

                // The subject's depth carried out past the mask, for whatever joins it. From FromInside afresh: the near map
                // above already has the wall's depth outside the mask. Edge cells can still hold the wall's depth, so each
                // takes the nearest within one cell (taking it any wider lifted lips to a shoulder, Let_Me_Tell_You).
                nearJoined = Spread(FromInside(d, inside, gw, gh, r), gw, gh, (int)Math.Ceiling(reach * (double)gw / tw) + 2);
                for (int i = 0; i < gn; i++) if (nearJoined[i] < 0) nearJoined[i] = d[i];
                nearJoined = Filter(nearJoined, gw, gh, 1, true);

                // Joined hair moves as one piece: the gaps between wisps (and between them and the mask) closed, the edge
                // softened, and only beside wisps, or closing would fill the mask's own corners. As a patchwork of hair and
                // wall depths it streaked.
                int close = Math.Max(2, (int)Math.Round(tw * 0.006));
                var memb = new float[tw * th];
                var joinF = new float[tw * th];
                for (int i = 0; i < memb.Length; i++)
                {
                    memb[i] = join[i] || mf[i] >= 0.5f ? 1f : 0f;
                    joinF[i] = join[i] ? 1f : 0f;
                }
                var closed = Filter(Filter(memb, tw, th, close, true), tw, th, close, false);
                var nearJoin = Filter(joinF, tw, th, close + 1, true);
                for (int i = 0; i < closed.Length; i++) closed[i] = nearJoin[i] > 0 ? closed[i] : memb[i];
                joinSoft = Blur(closed, tw, th, 1);
                for (int i = 0; i < joinSoft.Length; i++) if (nearJoin[i] <= 0) joinSoft[i] = 0;

                // The smooth outline: the mask blurred a little and cut at EdgeCut, 1-2 px outside the 0.5 line. The soft
                // edge inside it goes wholly with the subject. Left at the wall's depth (mask under 0.5) it stayed behind
                // as a dark ghost of the outline (IMGP0801); taken at the mask's own ragged 0.02 line it shimmered.
                smoothEdge = Blur(Blur(mf, tw, th, EdgeBlur), tw, th, EdgeBlur);

                // Small holes the subject fully encloses (cushion between a hand and a tummy, IMGP0997) keep the 0.35.1
                // depth: made crisp, the shader filled them from the hand, in pink specks. Larger enclosed gaps (between
                // two legs, an arm and a body, ed.jpg, 0.35-0.45% of the picture) do not; there it brought back a pale line.
                keepOld = SmallEnclosedHoles(mf, tw, th);
            }

            var bytes = new byte[tw * th * 4];
            for (int y = 0; y < th; y++)
            {
                float v = (y + 0.5f) / th;
                for (int x = 0; x < tw; x++)
                {
                    float u = (x + 0.5f) / tw;
                    float value = Sample(d, gw, gh, u, v);
                    float w = Sample(rim, gw, gh, u, v);
                    if (w > 0)
                    {
                        float a = SampleMask(alpha, size, u, v);
                        float[] nearMap = near;
                        int at = y * tw + x;
                        if (nearJoined != null && !keepOld![at])
                        {
                            if (joinSoft![at] > a) { a = joinSoft[at]; nearMap = nearJoined; }
                            // All in, on both sides of the old 0.5 edge: a pixel left half-way there was a dip in depth
                            // that was covered and uncovered every frame.
                            if (a < 1f && smoothEdge![at] >= EdgeCut) { a = 1f; nearMap = nearJoined; }
                        }
                        float crisp = a * Sample(nearMap, gw, gh, u, v) + (1 - a) * Sample(far, gw, gh, u, v);
                        value += (crisp - value) * w;
                    }
                    byte b = (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);
                    int o = (y * tw + x) * 4;
                    bytes[o] = b; bytes[o + 1] = b; bytes[o + 2] = b; bytes[o + 3] = 255;
                }
            }
            return bytes;
        }

        /// <summary>
        /// Stray hair the mask left out (IMGP0801: dark wisps on the leather, copied into the gap as a smear). Grows the
        /// subject out up to <paramref name="reach"/> pixels, one ring at a time, into pixels touching it whose colour is
        /// nearer the subject's beside them than the wall's beyond the reach, and only where that subject is hair: dark and
        /// nearly colourless. Without that, dark shadow beside an arm joined the arm (the wall beyond it was a white towel).
        /// </summary>
        private static bool[] JoinStrayHair(float[] a, byte[] px, int w, int h, int reach)
        {
            int n = w * h;
            // Ring distance from the mask (8 neighbours a step), up to 2 * reach, one ring of pixels at a time.
            var ring = new int[n];
            var layer = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
            {
                ring[i] = a[i] >= 0.5f ? 0 : int.MaxValue;
                if (ring[i] == 0) layer.Add(i);
            }
            var nextLayer = new System.Collections.Generic.List<int>();
            for (int k = 1; k <= reach * 2 && layer.Count > 0; k++)
            {
                nextLayer.Clear();
                foreach (int p in layer)
                {
                    int px0 = p % w, py0 = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = px0 + dx, yy = py0 + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = yy * w + xx;
                            if (ring[j] != int.MaxValue) continue;
                            ring[j] = k; nextLayer.Add(j);
                        }
                }
                (layer, nextLayer) = (nextLayer, layer);
            }
            // Subject colour: the photo well inside the mask, carried outward. Wall colour: beyond the reach, carried inward.
            var subj = new float[n * 3]; var subjKnown = new bool[n];
            var wall = new float[n * 3]; var wallKnown = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (ring[i] == 0 && a[i] >= 0.9f) { subjKnown[i] = true; for (int c = 0; c < 3; c++) subj[i * 3 + c] = px[i * 4 + c]; }
                if (ring[i] > reach && a[i] < 0.02f) { wallKnown[i] = true; for (int c = 0; c < 3; c++) wall[i * 3 + c] = px[i * 4 + c]; }
            }
            SpreadColour(subj, subjKnown, w, h, reach + 3);
            SpreadColour(wall, wallKnown, w, h, reach + 3);
            var join = new bool[n];
            var member = new bool[n];
            for (int i = 0; i < n; i++) member[i] = a[i] >= 0.5f;
            // Whether a pixel may join does not change from ring to ring, so each ring only the pixels touching the last
            // ring's additions (at first, the mask itself) are tried, and each pixel at most once.
            var tried = new bool[n];
            var added = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++) if (member[i]) added.Add(i);
            var add = new System.Collections.Generic.List<int>();
            for (int k = 1; k <= reach && added.Count > 0; k++)
            {
                add.Clear();
                foreach (int p in added)
                {
                    int px0 = p % w, py0 = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = px0 + dx, yy = py0 + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int i = yy * w + xx;
                            if (member[i] || tried[i]) continue;
                            tried[i] = true;
                            if (StrayHairJoins(i, px, subj, subjKnown, wall, wallKnown)) add.Add(i);
                        }
                }
                foreach (int i in add) { member[i] = true; join[i] = true; }
                (added, add) = (add, added);
            }
            return join;
        }

        private static bool StrayHairJoins(int i, byte[] px, float[] subj, bool[] subjKnown, float[] wall, bool[] wallKnown)
        {
            if (!subjKnown[i] || !wallKnown[i]) return false;
            float sb = subj[i * 3], sg = subj[i * 3 + 1], sr = subj[i * 3 + 2];
            float lum = 0.114f * sb + 0.587f * sg + 0.299f * sr;
            if (lum > JoinDarkest || Math.Max(sb, Math.Max(sg, sr)) - Math.Min(sb, Math.Min(sg, sr)) > JoinGreyest) return false;
            float ds = 0, db = 0;
            for (int c = 0; c < 3; c++)
            {
                float p = px[i * 4 + c];
                ds += (p - subj[i * 3 + c]) * (p - subj[i * 3 + c]);
                db += (p - wall[i * 3 + c]) * (p - wall[i * 3 + c]);
            }
            return ds < db * JoinBias * JoinBias;
        }

        /// <summary>Background (mask under 0.5) the subject fully encloses, in pieces under <see cref="HoleShare"/> of the
        /// picture, and 3 px round them so their own edge is also left alone.</summary>
        private static bool[] SmallEnclosedHoles(float[] mf, int w, int h)
        {
            int n = w * h;
            var bg = new bool[n];
            for (int i = 0; i < n; i++) bg[i] = mf[i] < 0.5f;
            // Background the border reaches is open; the rest is enclosed.
            var reached = new bool[n];
            var stack = new System.Collections.Generic.Stack<int>();
            for (int x = 0; x < w; x++) { stack.Push(x); stack.Push((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { stack.Push(y * w); stack.Push(y * w + w - 1); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (reached[i] || !bg[i]) continue;
                reached[i] = true;
                int x = i % w;
                if (x > 0) stack.Push(i - 1);
                if (x < w - 1) stack.Push(i + 1);
                if (i >= w) stack.Push(i - w);
                if (i < n - w) stack.Push(i + w);
            }
            var hole = new float[n];
            float most = HoleShare * n;
            var seen = new bool[n];
            var piece = new System.Collections.Generic.List<int>();
            for (int s = 0; s < n; s++)
            {
                if (!bg[s] || reached[s] || seen[s]) continue;
                piece.Clear();
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    if (seen[i] || !bg[i] || reached[i]) continue;
                    seen[i] = true; piece.Add(i);
                    int x = i % w;
                    if (x > 0) stack.Push(i - 1);
                    if (x < w - 1) stack.Push(i + 1);
                    if (i >= w) stack.Push(i - w);
                    if (i < n - w) stack.Push(i + w);
                }
                if (piece.Count < most) foreach (int i in piece) hole[i] = 1f;
            }
            var around = Filter(hole, w, h, 3, true);
            var result = new bool[n];
            for (int i = 0; i < n; i++) result[i] = around[i] > 0;
            return result;
        }

        /// <summary>Carries known colours into unknown neighbours, the mean of the known ones, <paramref name="rings"/> times.</summary>
        private static void SpreadColour(float[] col, bool[] known, int w, int h, int rings)
        {
            int n = w * h;
            // Each ring, only pixels beside the last ring's additions can gain a known neighbour (at first, beside any
            // known pixel), so only those are visited.
            var queued = new bool[n];
            var front = new System.Collections.Generic.List<int>();
            void QueueAround(int p)
            {
                int x0 = p % w, y0 = p / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x0 + dx, yy = y0 + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (known[j] || queued[j]) continue;
                        queued[j] = true; front.Add(j);
                    }
            }
            for (int i = 0; i < n; i++) if (known[i]) QueueAround(i);
            var add = new System.Collections.Generic.List<(int i, float r, float g, float b)>();
            for (int k = 0; k < rings && front.Count > 0; k++)
            {
                add.Clear();
                foreach (int i in front)
                {
                    int x = i % w, y = i / w;
                    float r = 0, g = 0, b = 0; int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = yy * w + xx;
                            if (!known[j]) continue;
                            r += col[j * 3]; g += col[j * 3 + 1]; b += col[j * 3 + 2]; count++;
                        }
                    if (count > 0) add.Add((i, r / count, g / count, b / count));
                }
                foreach (var q in add) { known[q.i] = true; col[q.i * 3] = q.r; col[q.i * 3 + 1] = q.g; col[q.i * 3 + 2] = q.b; }
                front.Clear();
                foreach (var q in add) QueueAround(q.i);
            }
        }

        /// <summary>Fills unset cells (below 0) next to set ones with the mean of those neighbours, <paramref name="rings"/> times.</summary>
        private static float[] Spread(float[] src, int w, int h, int rings)
        {
            var cur = (float[])src.Clone();
            for (int ring = 0; ring < rings; ring++)
            {
                var next = (float[])cur.Clone();
                bool grew = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (cur[i] >= 0) continue;
                        float sum = 0; int count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                float v = cur[yy * w + xx];
                                if (v >= 0) { sum += v; count++; }
                            }
                        if (count > 0) { next[i] = sum / count; grew = true; }
                    }
                cur = next;
                if (!grew) break;
            }
            return cur;
        }

        /// <summary>The photo (BGRA, pw x ph) at tw x th, each result pixel the mean of the photo pixels it covers.</summary>
        private static byte[] Resample(byte[] photo, int pw, int ph, int tw, int th)
        {
            var result = new byte[tw * th * 4];
            for (int y = 0; y < th; y++)
            {
                int y0 = (int)((long)y * ph / th), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * ph / th));
                for (int x = 0; x < tw; x++)
                {
                    int x0 = (int)((long)x * pw / tw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * pw / tw));
                    int b = 0, g = 0, r = 0, count = 0;
                    for (int yy = y0; yy < y1 && yy < ph; yy++)
                        for (int xx = x0; xx < x1 && xx < pw; xx++)
                        {
                            int o = (yy * pw + xx) * 4;
                            b += photo[o]; g += photo[o + 1]; r += photo[o + 2]; count++;
                        }
                    int t = (y * tw + x) * 4;
                    if (count > 0) { result[t] = (byte)(b / count); result[t + 1] = (byte)(g / count); result[t + 2] = (byte)(r / count); }
                    result[t + 3] = 255;
                }
            }
            return result;
        }

        /// <summary>Bilinear sample of a w x h map at (u, v) in 0..1 across the picture.</summary>
        private static float Sample(float[] map, int w, int h, float u, float v)
        {
            float gx = Math.Clamp(u * w - 0.5f, 0, w - 1), gy = Math.Clamp(v * h - 0.5f, 0, h - 1);
            int x0 = (int)gx, y0 = (int)gy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float fx = gx - x0, fy = gy - y0;
            float top = map[y0 * w + x0] + (map[y0 * w + x1] - map[y0 * w + x0]) * fx;
            float bottom = map[y1 * w + x0] + (map[y1 * w + x1] - map[y1 * w + x0]) * fx;
            return top + (bottom - top) * fy;
        }

        private static float SampleMask(byte[] alpha, int size, float u, float v)
        {
            float gx = Math.Clamp(u * size - 0.5f, 0, size - 1), gy = Math.Clamp(v * size - 0.5f, 0, size - 1);
            int x0 = (int)gx, y0 = (int)gy, x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
            float fx = gx - x0, fy = gy - y0;
            float top = alpha[y0 * size + x0] + (alpha[y0 * size + x1] - alpha[y0 * size + x0]) * fx;
            float bottom = alpha[y1 * size + x0] + (alpha[y1 * size + x1] - alpha[y1 * size + x0]) * fx;
            return (top + (bottom - top) * fy) / 255f;
        }

        /// <summary>
        /// Inside the mask within r of its edge, the depth of the subject just inside, if that is nearer than the pixel's own;
        /// -1 outside the mask. "Just inside" is the mask more than r from its edge, grown back outward one ring at a time,
        /// each ring taking the mean of the rings before it. Taking the nearest depth anywhere within r instead lifted a
        /// profile's lips to the shoulder in front of them (Let_Me_Tell_You, 2026-11-11).
        /// </summary>
        private static float[] FromInside(float[] d, bool[] inside, int w, int h, int r)
        {
            int n = w * h;
            var markIn = new float[n];
            for (int i = 0; i < n; i++) markIn[i] = inside[i] ? 1f : 0f;
            var deep = Filter(markIn, w, h, r, false);
            var value = new float[n];
            var known = new bool[n];
            for (int i = 0; i < n; i++)
            {
                known[i] = deep[i] > 0.5f;
                value[i] = known[i] ? d[i] : 0f;
            }
            var next = new float[n];
            var nextKnown = new bool[n];
            for (int ring = 0; ring <= r + 1; ring++)
            {
                bool grew = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        next[i] = value[i]; nextKnown[i] = known[i];
                        if (known[i] || !inside[i]) continue;
                        float sum = 0; int count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                int j = yy * w + xx;
                                if (known[j]) { sum += value[j]; count++; }
                            }
                        if (count > 0) { next[i] = sum / count; nextKnown[i] = true; grew = true; }
                    }
                (value, next) = (next, value);
                (known, nextKnown) = (nextKnown, known);
                if (!grew) break;
            }
            var result = new float[n];
            for (int i = 0; i < n; i++)
                result[i] = !inside[i] ? -1f : known[i] ? Math.Max(d[i], value[i]) : d[i];
            return result;
        }

        /// <summary>Square max (or min) over radius r, as two passes.</summary>
        private static float[] Filter(float[] src, int w, int h, int r, bool max)
        {
            // Each line with a running window (a queue of the candidates still in it), so the cost does not grow with r.
            var across = new float[src.Length];
            var queue = new int[Math.Max(w, h)];
            for (int y = 0; y < h; y++) FilterLine(src, across, y * w, 1, w, r, max, queue);
            var result = new float[src.Length];
            for (int x = 0; x < w; x++) FilterLine(across, result, x, w, h, r, max, queue);
            return result;
        }

        /// <summary>dst at each of len points (start + k * stride) = the max (or min) of src over k-r .. k+r, within the line.</summary>
        private static void FilterLine(float[] src, float[] dst, int start, int stride, int len, int r, bool max, int[] queue)
        {
            int head = 0, tail = 0, next = 0;
            for (int k = 0; k < len; k++)
            {
                int hi = Math.Min(len - 1, k + r);
                while (next <= hi)
                {
                    float v = src[start + next * stride];
                    while (tail > head && (max ? src[start + queue[tail - 1] * stride] <= v : src[start + queue[tail - 1] * stride] >= v)) tail--;
                    queue[tail++] = next++;
                }
                while (queue[head] < k - r) head++;
                dst[start + k * stride] = src[start + queue[head] * stride];
            }
        }

        /// <summary>Box blur over radius r, two passes.</summary>
        private static float[] Blur(float[] src, int w, int h, int r)
        {
            var across = new float[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0; int c = 0;
                    for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) { s += src[y * w + k]; c++; }
                    across[y * w + x] = s / c;
                }
            var result = new float[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0; int c = 0;
                    for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) { s += across[k * w + x]; c++; }
                    result[y * w + x] = s / c;
                }
            return result;
        }
    }
}

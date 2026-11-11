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

        /// <summary>
        /// <paramref name="grey"/> is the depth map (gw x gh, bytes). <paramref name="alpha"/> is the subject mask, size x size,
        /// stretched over the whole photo. Returns BGRA bytes at tw x th, grey in every colour.
        /// </summary>
        public static byte[] Crisp(byte[] grey, int gw, int gh, byte[] alpha, int size, int tw, int th)
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
                        float crisp = a * Sample(near, gw, gh, u, v) + (1 - a) * Sample(far, gw, gh, u, v);
                        value += (crisp - value) * w;
                    }
                    byte b = (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);
                    int o = (y * tw + x) * 4;
                    bytes[o] = b; bytes[o + 1] = b; bytes[o + 2] = b; bytes[o + 3] = 255;
                }
            }
            return bytes;
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
            var across = new float[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = src[y * w + x];
                    for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++)
                        v = max ? Math.Max(v, src[y * w + k]) : Math.Min(v, src[y * w + k]);
                    across[y * w + x] = v;
                }
            var result = new float[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = across[y * w + x];
                    for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++)
                        v = max ? Math.Max(v, across[k * w + x]) : Math.Min(v, across[k * w + x]);
                    result[y * w + x] = v;
                }
            return result;
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

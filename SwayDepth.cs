using System;

namespace ModernImageViewer
{
    /// <summary>
    /// Sway's depth with a crisp edge at the subject's own outline. The depth map fades from subject to background over
    /// a few of its pixels; anything that thin at the outline (ears, a hand, hair) sat half in each depth, so at one
    /// peak it slid behind the head and at the other it was taken for uncovered background. Near that fade only:
    /// inside the subject mask the nearest depth around is taken (the ears join the head), outside it the farthest (the
    /// halo joins the wall). Elsewhere the depth is unchanged. Checked off screen on ed.jpg at 6%, 2026-11-11.
    /// </summary>
    internal static class SwayDepth
    {
        /// <summary>Most pixels across the result; the photo's own width if smaller.</summary>
        public const int MostWidth = 1536;

        /// <summary>
        /// <paramref name="grey"/> is the depth map (gw x gh, bytes). <paramref name="alpha"/> is the subject mask, size x size,
        /// stretched over the whole photo. Returns BGRA bytes at tw x th, grey in every colour.
        /// </summary>
        public static byte[] Crisp(byte[] grey, int gw, int gh, byte[] alpha, int size, int tw, int th)
        {
            int n = tw * th;
            var d = new float[n];
            for (int y = 0; y < th; y++)
            {
                float gy = Math.Clamp((y + 0.5f) * gh / th - 0.5f, 0, gh - 1);
                int y0 = (int)gy, y1 = Math.Min(y0 + 1, gh - 1); float fy = gy - y0;
                for (int x = 0; x < tw; x++)
                {
                    float gx = Math.Clamp((x + 0.5f) * gw / tw - 0.5f, 0, gw - 1);
                    int x0 = (int)gx, x1 = Math.Min(x0 + 1, gw - 1); float fx = gx - x0;
                    float top = grey[y0 * gw + x0] + (grey[y0 * gw + x1] - grey[y0 * gw + x0]) * fx;
                    float bottom = grey[y1 * gw + x0] + (grey[y1 * gw + x1] - grey[y1 * gw + x0]) * fx;
                    d[y * tw + x] = (top + (bottom - top) * fy) / 255f;
                }
            }
            // About three depth-map pixels: the width of the fade at the outline.
            int r = Math.Max(1, (int)Math.Round(tw / (float)gw * 3 / 2));
            var high = Filter(d, tw, th, r, true);
            var low = Filter(d, tw, th, r, false);
            var bytes = new byte[n * 4];
            for (int y = 0; y < th; y++)
            {
                float my = Math.Clamp((y + 0.5f) * size / th - 0.5f, 0, size - 1);
                int my0 = (int)my, my1 = Math.Min(my0 + 1, size - 1); float fmy = my - my0;
                for (int x = 0; x < tw; x++)
                {
                    int i = y * tw + x;
                    float value = d[i];
                    float band = Math.Clamp((high[i] - low[i] - 0.03f) / 0.05f, 0, 1);
                    if (band > 0)
                    {
                        float mx = Math.Clamp((x + 0.5f) * size / tw - 0.5f, 0, size - 1);
                        int mx0 = (int)mx, mx1 = Math.Min(mx0 + 1, size - 1); float fmx = mx - mx0;
                        float top = alpha[my0 * size + mx0] + (alpha[my0 * size + mx1] - alpha[my0 * size + mx0]) * fmx;
                        float bottom = alpha[my1 * size + mx0] + (alpha[my1 * size + mx1] - alpha[my1 * size + mx0]) * fmx;
                        float a = (top + (bottom - top) * fmy) / 255f;
                        float crisp = a * high[i] + (1 - a) * low[i];
                        value += (crisp - value) * band;
                    }
                    byte b = (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);
                    bytes[i * 4] = b; bytes[i * 4 + 1] = b; bytes[i * 4 + 2] = b; bytes[i * 4 + 3] = 255;
                }
            }
            return bytes;
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
    }
}

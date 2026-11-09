using System;

namespace ModernImageViewer
{
    /// <summary>
    /// Prototype (branch prototype-titles-3d): a photo split into two layers for the Slide motion, so the near part moves
    /// as one piece over a background that holds still. Warping each pixel by its own depth bent poles, vines and edges
    /// once the move was big enough to read; a rigid layer cannot bend.
    /// The cut is the depth map's own split (Otsu), put onto the photo's edges by a guided filter; what the near layer
    /// hides is filled from around it (push-pull), so the background has something to show as the near layer moves off.
    /// </summary>
    internal static class ParallaxLayers
    {
        /// <summary>The background, with what the near layer hides filled in, and the near layer's alpha, both at the photo's size.</summary>
        /// <param name="InFront">How much nearer the near layer is than the rest, by the depth map (0 to 1): a subject behind its surroundings is no foreground.</param>
        /// <param name="CentreX">The subject's centre, as a share of the width: the background grows about it, so the subject holds still.</param>
        public sealed record Layers(byte[] Plate, byte[] Mask, float NearShare, float InFront, float CentreX = 0.5f, float CentreY = 0.5f);

        // The work is done at about this size and the result scaled up: the cut is a soft edge and the fill is smooth.
        private const int WorkLongSide = 1280;

        /// <param name="photo">BGRA, premultiplied, opaque.</param>
        /// <param name="grey">The depth map, 0 far to 255 near, at its own size.</param>
        /// <param name="subject">The subject cut out by <see cref="SubjectMasks"/>, square, stretched over the photo. When given, it is
        /// the near layer; without it the depth map is split.</param>
        /// <param name="way">The way the near layer will move, 1 right or -1 left: the strip it uncovers is on the other side.</param>
        public static Layers Build(byte[] photo, int width, int height, byte[] grey, int depthWidth, int depthHeight, byte[]? subject = null, int subjectSide = 0, int way = 1)
        {
            double s = Math.Min(1, WorkLongSide / (double)Math.Max(width, height));
            int w = Math.Max(8, (int)Math.Round(width * s)), h = Math.Max(8, (int)Math.Round(height * s));

            // The photo at the working size: colour and brightness.
            var colour = new float[w * h * 3];
            var light = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(height - 1, (int)((y + 0.5) * height / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(width - 1, (int)((x + 0.5) * width / w));
                    int i = (sy * width + sx) * 4, o = y * w + x;
                    colour[o * 3] = photo[i]; colour[o * 3 + 1] = photo[i + 1]; colour[o * 3 + 2] = photo[i + 2];
                    light[o] = (0.114f * photo[i] + 0.587f * photo[i + 1] + 0.299f * photo[i + 2]) / 255f;
                }
            }

            var depth = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    depth[y * w + x] = Bilinear(grey, depthWidth, depthHeight, (x + 0.5f) * depthWidth / w - 0.5f, (y + 0.5f) * depthHeight / h - 0.5f) / 255f;

            float[] alpha;
            if (subject != null && subjectSide > 0)
            {
                var subj = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        subj[y * w + x] = Bilinear(subject, subjectSide, subjectSide, (x + 0.5f) * subjectSide / w - 0.5f, (y + 0.5f) * subjectSide / h - 0.5f) / 255f;
                alpha = HolisticAlpha(light, depth, grey, subj, w, h, out _, out _);
            }
            else
            {
                // The split: where the depth map's two groups part (Otsu), with a narrow soft band either side, put onto
                // the photo's own edges and firmed.
                float cut = Otsu(grey) / 255f;
                var near = new float[w * h];
                for (int i = 0; i < near.Length; i++) near[i] = Math.Clamp((depth[i] - cut) / 0.04f + 0.5f, 0, 1);
                int radius = Math.Max(2, (int)Math.Round(8 * w / 1280.0));
                alpha = Guided(light, near, w, h, radius, 1e-3f);
                for (int i = 0; i < alpha.Length; i++) alpha[i] = Math.Clamp((alpha[i] - 0.5f) * 1.6f + 0.5f, 0, 1);
            }
            int nearCount = 0;
            double nearDepth = 0, farDepth = 0, sumX = 0, sumY = 0;
            for (int i = 0; i < alpha.Length; i++)
            {
                if (alpha[i] > 0.5f) { nearCount++; nearDepth += depth[i]; sumX += i % w; sumY += i / w; }
                else farDepth += depth[i];
            }
            float centreX = nearCount == 0 ? 0.5f : (float)((sumX / nearCount + 0.5) / w);
            float centreY = nearCount == 0 ? 0.5f : (float)((sumY / nearCount + 0.5) / h);
            int farCount = alpha.Length - nearCount;
            float inFront = nearCount == 0 || farCount == 0 ? 0 : (float)(nearDepth / nearCount - farDepth / farCount);

            // What needs filling:
            // 1. `hidden` for PushPull sampling uses a generous margin (`hideR`, ~6 px at 720w) around `alpha > 0.03f` so dark
            //    foreground antialiasing fringes or thin lightning rods never bleed into `fill`.
            // 2. However, when writing `fill` into `plate`, we only replace `photo` inside `alpha > 0.15f` (+ 1 px isotropic and
            //    + 2 px horizontally on the uncovered `-way` edge, feathered by `Box(..., 1)`), so the unoccluded sky/background
            //    around the subject at the starting position (`dx = 0`) is 100% the original photo with zero dilation halo or
            //    stair-stepping.
            var hidden = new bool[w * h];
            for (int i = 0; i < hidden.Length; i++) hidden[i] = alpha[i] > 0.03f;
            int hideR = Math.Max(5, (int)Math.Round(6 * w / 720.0));
            hidden = Grow(hidden, w, h, hideR);
            var fill = PushPull(colour, hidden, w, h, way);

            var tight = new bool[w * h];
            for (int i = 0; i < tight.Length; i++) tight[i] = alpha[i] > 0.15f;
            var plateHole = Grow(tight, w, h, 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int step = 1; step <= 2; step++)
                {
                    if (way > 0)
                    {
                        for (int x = 0; x < w - step; x++) if (tight[row + x + step]) plateHole[row + x] = true;
                    }
                    else
                    {
                        for (int x = step; x < w; x++) if (tight[row + x - step]) plateHole[row + x] = true;
                    }
                }
            }
            var hideW = new float[w * h];
            for (int i = 0; i < hideW.Length; i++) hideW[i] = plateHole[i] ? 1f : 0f;
            hideW = Box(hideW, w, h, 1);

            var plate = new byte[width * height * 4];
            var mask = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                float fy = (y + 0.5f) * h / height - 0.5f;
                for (int x = 0; x < width; x++)
                {
                    float fx = (x + 0.5f) * w / width - 0.5f;
                    int i = (y * width + x) * 4;
                    float a = BilinearF(alpha, w, h, fx, fy);
                    byte m = (byte)Math.Clamp(a * 255f + 0.5f, 0, 255);
                    mask[i] = m; mask[i + 1] = m; mask[i + 2] = m; mask[i + 3] = m;
                    float hw = BilinearF(hideW, w, h, fx, fy);
                    if (hw > 1e-3f)
                    {
                        float fb = BilinearC(fill, w, h, fx, fy, 0);
                        float fg = BilinearC(fill, w, h, fx, fy, 1);
                        float fr = BilinearC(fill, w, h, fx, fy, 2);
                        plate[i] = (byte)Math.Clamp(photo[i] * (1f - hw) + fb * hw + 0.5f, 0, 255);
                        plate[i + 1] = (byte)Math.Clamp(photo[i + 1] * (1f - hw) + fg * hw + 0.5f, 0, 255);
                        plate[i + 2] = (byte)Math.Clamp(photo[i + 2] * (1f - hw) + fr * hw + 0.5f, 0, 255);
                    }
                    else
                    {
                        plate[i] = photo[i]; plate[i + 1] = photo[i + 1]; plate[i + 2] = photo[i + 2];
                    }
                    plate[i + 3] = 255;
                }
            }
            return new Layers(plate, mask, nearCount / (float)(w * h), inFront, centreX, centreY);
        }

        private static int Otsu(byte[] grey)
        {
            var hist = new double[256];
            foreach (byte v in grey) hist[v]++;
            double total = grey.Length, sumAll = 0;
            for (int t = 0; t < 256; t++) sumAll += t * hist[t];
            double weight = 0, sum = 0, best = -1;
            int cut = 128;
            for (int t = 0; t < 255; t++)
            {
                weight += hist[t]; sum += t * hist[t];
                if (weight == 0 || weight == total) continue;
                double w0 = weight / total, mean0 = sum / weight, mean1 = (sumAll - sum) / (total - weight);
                double between = w0 * (1 - w0) * (mean0 - mean1) * (mean0 - mean1);
                if (between > best) { best = between; cut = t; }
            }
            return cut;
        }

        /// <summary>He, Sun and Tang's guided filter: <paramref name="p"/> made to follow the edges of <paramref name="guide"/>.</summary>
        private static float[] Guided(float[] guide, float[] p, int w, int h, int r, float eps)
        {
            int n = w * h;
            var ip = new float[n]; var ii = new float[n];
            for (int i = 0; i < n; i++) { ip[i] = guide[i] * p[i]; ii[i] = guide[i] * guide[i]; }
            var mI = Box(guide, w, h, r); var mp = Box(p, w, h, r); var mIp = Box(ip, w, h, r); var mII = Box(ii, w, h, r);
            var a = new float[n]; var b = new float[n];
            for (int i = 0; i < n; i++)
            {
                float variance = mII[i] - mI[i] * mI[i];
                a[i] = (mIp[i] - mI[i] * mp[i]) / (variance + eps);
                b[i] = mp[i] - a[i] * mI[i];
            }
            var ma = Box(a, w, h, r); var mb = Box(b, w, h, r);
            var q = new float[n];
            for (int i = 0; i < n; i++) q[i] = ma[i] * guide[i] + mb[i];
            return q;
        }

        /// <summary>The mean over a (2r+1) square, edges clamped, in two running passes.</summary>
        private static float[] Box(float[] src, int w, int h, int r)
        {
            var tmp = new float[w * h];
            var dst = new float[w * h];
            float span = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float acc = 0;
                for (int k = -r; k <= r; k++) acc += src[row + Math.Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = acc / span;
                    acc += src[row + Math.Min(w - 1, x + r + 1)] - src[row + Math.Max(0, x - r)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -r; k <= r; k++) acc += tmp[Math.Clamp(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = acc / span;
                    acc += tmp[Math.Min(h - 1, y + r + 1) * w + x] - tmp[Math.Max(0, y - r) * w + x];
                }
            }
            return dst;
        }

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

        private static bool[] Shrink(bool[] mask, int w, int h, int r)
        {
            var inv = new bool[w * h];
            for (int i = 0; i < inv.Length; i++) inv[i] = !mask[i];
            var grownInv = Grow(inv, w, h, r);
            var shrunk = new bool[w * h];
            for (int i = 0; i < shrunk.Length; i++) shrunk[i] = !grownInv[i];
            return shrunk;
        }

        /// <summary>Fills the hidden pixels from a one-sided edge-preserving propagation + capped harmonic pyramid of the uncovered strip, plus a horizontal bridge across narrow spires.</summary>
        private static float[] PushPull(float[] colour, bool[] hidden, int w, int h, int way)
        {
            // 1. Pre-fill the interior of hidden runs from the uncovered side (`way`) with edge-stopping vertical diffusion:
            //    this prevents dark colours on the opposite side of a wide subject (or across a horizon line) from bleeding
            //    into the uncovered strip.
            var seed = new float[w * h * 3];
            Array.Copy(colour, seed, colour.Length);
            var dist = new int[w * h];
            int xStart = way > 0 ? 1 : w - 2;
            int xEnd = way > 0 ? w : -1;
            int xStep = way > 0 ? 1 : -1;
            for (int x = xStart; x != xEnd; x += xStep)
            {
                int prevX = x - xStep;
                for (int y = 0; y < h; y++)
                {
                    int o = y * w + x;
                    if (!hidden[o]) continue;
                    int p = y * w + prevX;
                    dist[o] = dist[p] + 1;
                    int pUp = Math.Max(0, y - 1) * w + prevX;
                    int pDn = Math.Min(h - 1, y + 1) * w + prevX;
                    float b0 = seed[p * 3], g0 = seed[p * 3 + 1], r0 = seed[p * 3 + 2];
                    float bU = seed[pUp * 3], gU = seed[pUp * 3 + 1], rU = seed[pUp * 3 + 2];
                    float bD = seed[pDn * 3], gD = seed[pDn * 3 + 1], rD = seed[pDn * 3 + 2];
                    float dUp = Math.Max(Math.Abs(b0 - bU), Math.Max(Math.Abs(g0 - gU), Math.Abs(r0 - rU)));
                    float dDn = Math.Max(Math.Abs(b0 - bD), Math.Max(Math.Abs(g0 - gD), Math.Abs(r0 - rD)));
                    float wUp = 0.25f * MathF.Exp(-(dUp / 20f) * (dUp / 20f));
                    float wDn = 0.25f * MathF.Exp(-(dDn / 20f) * (dDn / 20f));
                    float w0 = 1f - wUp - wDn;
                    seed[o * 3] = b0 * w0 + bU * wUp + bD * wDn;
                    seed[o * 3 + 1] = g0 * w0 + gU * wUp + gD * wDn;
                    seed[o * 3 + 2] = r0 * w0 + rU * wUp + rD * wDn;
                }
            }

            // 2. Run a 4-level harmonic pyramid only over the uncovered strip (`dist <= pad`), treating `seed` beyond `pad`
            //    as the outer boundary so vertical blurring stays local (~10 px) while clouds and pockets blend smoothly in 2D.
            int pad = Math.Max(16, (int)Math.Round(0.052f * w));
            var levels = new System.Collections.Generic.List<(float[] C, float[] K, int W, int H)>();
            var c0 = new float[w * h * 3]; var k0 = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                bool activeHole = hidden[i] && dist[i] <= pad;
                float k = activeHole ? 0 : 1;
                k0[i] = k;
                c0[i * 3] = seed[i * 3] * k; c0[i * 3 + 1] = seed[i * 3 + 1] * k; c0[i * 3 + 2] = seed[i * 3 + 2] * k;
            }
            levels.Add((c0, k0, w, h));
            while (levels.Count < 4 && Math.Min(levels[^1].W, levels[^1].H) > 8)
            {
                var (c, k, lw, lh) = levels[^1];
                int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
                var c2 = new float[nw * nh * 3]; var k2 = new float[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        float ks = 0, r = 0, g = 0, b = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min(lw - 1, x * 2 + dx), sy = Math.Min(lh - 1, y * 2 + dy), s = sy * lw + sx;
                                ks += k[s]; r += c[s * 3]; g += c[s * 3 + 1]; b += c[s * 3 + 2];
                            }
                        int o = y * nw + x;
                        if (ks > 0)
                        {
                            k2[o] = 1f;
                            c2[o * 3] = r / ks; c2[o * 3 + 1] = g / ks; c2[o * 3 + 2] = b / ks;
                        }
                    }
                levels.Add((c2, k2, nw, nh));
            }
            var top = levels[^1];
            var fill = new float[top.W * top.H * 3];
            for (int y = 0; y < top.H; y++)
                for (int x = 0; x < top.W; x++)
                {
                    int i = y * top.W + x;
                    if (top.K[i] > 0)
                    {
                        float invK = 1f / top.K[i];
                        fill[i * 3] = top.C[i * 3] * invK; fill[i * 3 + 1] = top.C[i * 3 + 1] * invK; fill[i * 3 + 2] = top.C[i * 3 + 2] * invK;
                    }
                    else
                    {
                        float sx = (x + 0.5f) * w / top.W - 0.5f, sy = (y + 0.5f) * h / top.H - 0.5f;
                        fill[i * 3] = BilinearC(seed, w, h, sx, sy, 0);
                        fill[i * 3 + 1] = BilinearC(seed, w, h, sx, sy, 1);
                        fill[i * 3 + 2] = BilinearC(seed, w, h, sx, sy, 2);
                    }
                }
            int fw = top.W, fh = top.H;
            for (int l = levels.Count - 2; l >= 0; l--)
            {
                var (c, k, lw, lh) = levels[l];
                var cur = new float[lw * lh * 3];
                for (int y = 0; y < lh; y++)
                    for (int x = 0; x < lw; x++)
                    {
                        float ux = (x + 0.5f) * fw / lw - 0.5f, uy = (y + 0.5f) * fh / lh - 0.5f;
                        int o = y * lw + x;
                        if (k[o] > 0.5f)
                        {
                            float invK = 1f / Math.Max(k[o], 1e-4f);
                            cur[o * 3] = c[o * 3] * invK;
                            cur[o * 3 + 1] = c[o * 3 + 1] * invK;
                            cur[o * 3 + 2] = c[o * 3 + 2] * invK;
                        }
                        else
                        {
                            for (int ch = 0; ch < 3; ch++)
                                cur[o * 3 + ch] = BilinearC(fill, fw, fh, ux, uy, ch);
                        }
                    }
                var tmp = new float[lw * lh * 3];
                for (int pass = 0; pass < 4; pass++)
                {
                    Array.Copy(cur, tmp, cur.Length);
                    for (int y = 0; y < lh; y++)
                    {
                        int ym = Math.Max(0, y - 1) * lw, y0 = y * lw, yp = Math.Min(lh - 1, y + 1) * lw;
                        for (int x = 0; x < lw; x++)
                        {
                            int o = y0 + x;
                            if (k[o] > 0.5f) continue;
                            int xm = Math.Max(0, x - 1), xp = Math.Min(lw - 1, x + 1);
                            for (int ch = 0; ch < 3; ch++)
                            {
                                float ortho = tmp[(ym + x) * 3 + ch] + tmp[(yp + x) * 3 + ch] + tmp[(y0 + xm) * 3 + ch] + tmp[(y0 + xp) * 3 + ch];
                                float diag = tmp[(ym + xm) * 3 + ch] + tmp[(ym + xp) * 3 + ch] + tmp[(yp + xm) * 3 + ch] + tmp[(yp + xp) * 3 + ch];
                                cur[o * 3 + ch] = (ortho + 0.5f * diag) / 6f;
                            }
                        }
                    }
                }
                fill = cur; fw = lw; fh = lh;
            }

            // Narrow-span horizontal bridge (span < 0.14 * w, e.g. lantern/spire/flagpole) so horizon and mountain ridges stay continuous.
            var hzR = new float[w * h]; var hzG = new float[w * h]; var hzB = new float[w * h];
            var wHz = new float[w * h];
            for (int i = 0; i < w * h; i++) { hzB[i] = fill[i * 3]; hzG[i] = fill[i * 3 + 1]; hzR[i] = fill[i * 3 + 2]; }
            int maxSpan = (int)(0.14f * w);
            for (int y = 0; y < h; y++)
            {
                int row = y * w, x = 0;
                while (x < w)
                {
                    if (!hidden[row + x]) { x++; continue; }
                    int s = x;
                    while (x < w && hidden[row + x]) x++;
                    int e = x, span = e - s;
                    if (span < maxSpan && s > 4 && e < w - 4)
                    {
                        float bL = 0, gL = 0, rL = 0, cL = 0;
                        for (int j = Math.Max(0, s - 6); j < s; j++)
                        {
                            if (!hidden[row + j]) { int p = (row + j) * 3; bL += colour[p]; gL += colour[p + 1]; rL += colour[p + 2]; cL++; }
                        }
                        float bR = 0, gR = 0, rR = 0, cR = 0;
                        for (int j = e; j < Math.Min(w, e + 6); j++)
                        {
                            if (!hidden[row + j]) { int p = (row + j) * 3; bR += colour[p]; gR += colour[p + 1]; rR += colour[p + 2]; cR++; }
                        }
                        if (cL > 0 && cR > 0)
                        {
                            bL /= cL; gL /= cL; rL /= cL;
                            bR /= cR; gR /= cR; rR /= cR;
                            float wh = Math.Clamp(1f - (span - 0.07f * w) / (0.07f * w), 0f, 1f);
                            for (int j = 0; j < span; j++)
                            {
                                float t = (j + 0.5f) / span;
                                int idx = row + s + j;
                                hzB[idx] = bL * (1f - t) + bR * t;
                                hzG[idx] = gL * (1f - t) + gR * t;
                                hzR[idx] = rL * (1f - t) + rR * t;
                                wHz[idx] = wh;
                            }
                        }
                    }
                }
            }
            hzB = Box(hzB, w, h, 2); hzG = Box(hzG, w, h, 2); hzR = Box(hzR, w, h, 2);
            wHz = Box(wHz, w, h, 3);
            for (int i = 0; i < w * h; i++)
            {
                if (!hidden[i]) continue;
                float wh = wHz[i];
                fill[i * 3] = fill[i * 3] * (1f - wh) + hzB[i] * wh;
                fill[i * 3 + 1] = fill[i * 3 + 1] * (1f - wh) + hzG[i] * wh;
                fill[i * 3 + 2] = fill[i * 3 + 2] * (1f - wh) + hzR[i] * wh;
            }

            // 4. 2D high-frequency background detail transfer:
            //    Extract zero-mean high-frequency cloud and ripple detail from the known background outside `hidden`,
            //    and translate it horizontally by `-way * detShift` into the uncovered strip, so cloud wisps and ripples
            //    keep their natural camera sharpness without dark mirroring or contour seams.
            var bgB = new float[w * h]; var bgG = new float[w * h]; var bgR = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                bgB[i] = hidden[i] ? fill[i * 3] : colour[i * 3];
                bgG[i] = hidden[i] ? fill[i * 3 + 1] : colour[i * 3 + 1];
                bgR[i] = hidden[i] ? fill[i * 3 + 2] : colour[i * 3 + 2];
            }
            var low28B = Box(bgB, w, h, 28); var low28G = Box(bgG, w, h, 28); var low28R = Box(bgR, w, h, 28);
            var detB = new float[w * h]; var detG = new float[w * h]; var detR = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                detB[i] = bgB[i] - low28B[i];
                detG[i] = bgG[i] - low28G[i];
                detR[i] = bgR[i] - low28R[i];
            }
            int detShift = Math.Max(44, (int)Math.Round(0.056f * w));
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = row + x;
                    if (!hidden[idx]) continue;
                    int srcX = Math.Clamp(x - way * detShift, 0, w - 1);
                    int sIdx = row + srcX;
                    float diff = Math.Max(Math.Abs(low28B[sIdx] - fill[idx * 3]),
                                 Math.Max(Math.Abs(low28G[sIdx] - fill[idx * 3 + 1]),
                                          Math.Abs(low28R[sIdx] - fill[idx * 3 + 2])));
                    float wDet = MathF.Exp(-(diff / 75f) * (diff / 75f)) * (1f - wHz[idx]);
                    fill[idx * 3] = Math.Clamp(fill[idx * 3] + detB[sIdx] * wDet, 0f, 255f);
                    fill[idx * 3 + 1] = Math.Clamp(fill[idx * 3 + 1] + detG[sIdx] * wDet, 0f, 255f);
                    fill[idx * 3 + 2] = Math.Clamp(fill[idx * 3 + 2] + detR[sIdx] * wDet, 0f, 255f);
                }
            }
            return fill;
        }

        /// <summary>
        /// Combines the BiRefNet subject with connected foreground structures from the depth map (skylines, bridges, ground,
        /// perch branches) while keeping isolated sky objects (such as a moon) and soft bokeh backgrounds in the background layer,
        /// and stripping thin depth halos around the BiRefNet subject via morphological opening.
        /// </summary>
        internal static float[] HolisticAlpha(float[] light, float[] depth, byte[] greyDepth, float[] subject, int w, int h, out float unionArea, out float bgP90)
        {
            int n = w * h;
            var fgDepths = new System.Collections.Generic.List<float>(n / 4);
            var bgDepths = new System.Collections.Generic.List<float>(n / 2);
            double bgGradSum = 0;
            int bgGradCount = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (subject[i] > 0.5f) fgDepths.Add(depth[i]);
                    if (subject[i] < 0.2f)
                    {
                        bgDepths.Add(depth[i]);
                        bgGradSum += SobelF(light, w, h, x, y);
                        bgGradCount++;
                    }
                }
            float bgP20 = Percentile(bgDepths, 0.20f, 0.05f);
            float fgP20 = Percentile(fgDepths, 0.20f, 0.50f);
            float otsu = Otsu(greyDepth) / 255f;
            float bgTex = bgGradCount > 0 ? (float)(bgGradSum / bgGradCount) : 0.1f;
            float texThreshold = 0.14f * Math.Clamp(720f / Math.Max(w, h), 0.75f, 1.15f);
            float cut = (bgP20 < 0.03f && bgTex > texThreshold)
                ? Math.Min(otsu, bgP20 + 0.06f)
                : Math.Max(otsu, fgP20 - 0.08f);

            // Flood-fill connected components of (depth > cut) starting from the subject (subject > 0.2) or the bottom 8% of the frame.
            var fgBin = new bool[n];
            var keep = new bool[n];
            var queue = new int[n];
            int head = 0, tail = 0;
            int bottomStart = (int)(h * 0.92f) * w;
            for (int i = 0; i < n; i++)
            {
                if (depth[i] > cut)
                {
                    fgBin[i] = true;
                    if (subject[i] > 0.2f || i >= bottomStart)
                    {
                        keep[i] = true;
                        queue[tail++] = i;
                    }
                }
            }
            while (head < tail)
            {
                int cur = queue[head++];
                int cx = cur % w, cy = cur / w;
                if (cx > 0 && fgBin[cur - 1] && !keep[cur - 1]) { keep[cur - 1] = true; queue[tail++] = cur - 1; }
                if (cx + 1 < w && fgBin[cur + 1] && !keep[cur + 1]) { keep[cur + 1] = true; queue[tail++] = cur + 1; }
                if (cy > 0 && fgBin[cur - w] && !keep[cur - w]) { keep[cur - w] = true; queue[tail++] = cur - w; }
                if (cy + 1 < h && fgBin[cur + w] && !keep[cur + w]) { keep[cur + w] = true; queue[tail++] = cur + w; }
            }

            // Morphological opening on extra depth regions outside the BiRefNet subject: strips 3..15 px depth halos around the
            // subject (dome/statue rims against sky/mountain) while preserving substantial foreground bodies (towers, benches, trunks).
            var extra = new bool[n];
            for (int i = 0; i < n; i++) extra[i] = keep[i] && subject[i] < 0.2f;
            int openR = Math.Max(6, (int)Math.Round(12 * w / 720.0));
            var openedExtra = Grow(Shrink(extra, w, h, openR), w, h, openR);
            int extraCount = 0;
            int maxExtraY = 0;
            for (int i = 0; i < n; i++)
            {
                if (openedExtra[i] && extra[i])
                {
                    extraCount++;
                    int y = i / w;
                    if (y > maxExtraY) maxExtraY = y;
                }
            }
            // Discard small extra fragments (< 8% of frame) only when they are overhead floating fragments
            // (such as an overhead boat canopy bar touching a hat/turban in the upper portion of the frame).
            // Grounded structures extending into the lower half of the frame (towers, pillars, trunks, walls)
            // belong to the foreground architecture/landscape and must never be stripped.
            bool isOverheadFragment = fgDepths.Count > n * 0.20f && extraCount < n * 0.08f && maxExtraY < (int)(h * 0.50f);
            bool keepExtra = !isOverheadFragment;
            var cleanKeep = new bool[n];
            var near = new float[n];
            for (int i = 0; i < n; i++)
            {
                bool ck = subject[i] >= 0.2f || (keepExtra && openedExtra[i] && extra[i]);
                cleanKeep[i] = ck;
                near[i] = ck ? Math.Clamp((depth[i] - cut) / 0.04f + 0.5f, 0, 1) : 0f;
            }
            int radius = Math.Max(2, (int)Math.Round(8 * w / 1280.0));
            var alphaD = Guided(light, near, w, h, radius, 1e-3f);
            var alpha = new float[n];
            int uCount = 0;
            for (int i = 0; i < n; i++)
            {
                float ad = cleanKeep[i] ? Math.Clamp((alphaD[i] - 0.5f) * 1.6f + 0.5f, 0, 1) : 0f;
                float a = Math.Max(subject[i], ad);
                alpha[i] = a;
                if (a > 0.5f) uCount++;
            }
            unionArea = uCount / (float)n;
            var remBg = new System.Collections.Generic.List<float>(n - uCount);
            for (int i = 0; i < n; i++)
                if (alpha[i] <= 0.5f) remBg.Add(depth[i]);
            bgP90 = Percentile(remBg, 0.90f, 1.0f);

            // On a portrait/bokeh shot where depth expansion would freeze >68% of the picture, keep the clean subject mask.
            if (unionArea > 0.68f)
            {
                Array.Copy(subject, alpha, n);
            }
            return alpha;
        }

        private static float Percentile(System.Collections.Generic.List<float> values, float p, float fallback)
        {
            if (values.Count == 0) return fallback;
            values.Sort();
            int idx = Math.Clamp((int)(values.Count * p), 0, values.Count - 1);
            return values[idx];
        }

        private static double SobelF(float[] a, int w, int h, int x, int y)
        {
            float P(int dx, int dy) => a[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
            float gx = P(1, -1) + 2 * P(1, 0) + P(1, 1) - P(-1, -1) - 2 * P(-1, 0) - P(-1, 1);
            float gy = P(-1, 1) + 2 * P(0, 1) + P(1, 1) - P(-1, -1) - 2 * P(0, -1) - P(1, -1);
            return Math.Sqrt(gx * gx + gy * gy);
        }

        private static float Bilinear(byte[] a, int w, int h, float x, float y)
        {
            x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float fx = x - x0, fy = y - y0;
            return a[y0 * w + x0] * (1 - fx) * (1 - fy) + a[y0 * w + x1] * fx * (1 - fy) + a[y1 * w + x0] * (1 - fx) * fy + a[y1 * w + x1] * fx * fy;
        }

        private static float BilinearF(float[] a, int w, int h, float x, float y)
        {
            x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float fx = x - x0, fy = y - y0;
            return a[y0 * w + x0] * (1 - fx) * (1 - fy) + a[y0 * w + x1] * fx * (1 - fy) + a[y1 * w + x0] * (1 - fx) * fy + a[y1 * w + x1] * fx * fy;
        }

        private static float BilinearC(float[] a, int w, int h, float x, float y, int ch)
        {
            x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float fx = x - x0, fy = y - y0;
            return a[(y0 * w + x0) * 3 + ch] * (1 - fx) * (1 - fy) + a[(y0 * w + x1) * 3 + ch] * fx * (1 - fy)
                + a[(y1 * w + x0) * 3 + ch] * (1 - fx) * fy + a[(y1 * w + x1) * 3 + ch] * fx * fy;
        }
    }
}

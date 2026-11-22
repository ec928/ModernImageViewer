using System;
using System.Collections.Generic;

namespace ModernImageViewer
{
    public readonly record struct SubjectStats(
        float Area,
        float SubjectMedianDepth,
        float BgMedianDepth,
        bool HasUsableSubject);

    public readonly record struct BackgroundPalette(
        float TopR, float TopG, float TopB,
        float BotR, float BotG, float BotB,
        float AccR, float AccG, float AccB);

    /// <summary>
    /// Portrait-safe 3D depth shaping, subject-anchored pivot calculation, foreground color
    /// decontamination, and backdrop palette sampling. Pure net8.0 math with no UI/Win2D dependencies.
    /// </summary>
    public static class SwayPortrait
    {
        public static SubjectStats ComputeSubjectStats(byte[] depthGrey, int gw, int gh, byte[] maskAlpha, int maskSize)
        {
            if (depthGrey == null || maskAlpha == null || gw <= 0 || gh <= 0 || maskSize <= 0
                || depthGrey.Length < gw * gh || maskAlpha.Length < maskSize * maskSize)
            {
                return new SubjectStats(0f, 0.5f, 0.2f, false);
            }

            int n = gw * gh;
            var core = new List<float>(n / 3);
            var fallbackSubj = new List<float>(n / 3);
            var bg = new List<float>(n / 2);
            int halfCount = 0;

            for (int y = 0; y < gh; y++)
            {
                float v = (y + 0.5f) / gh;
                int row = y * gw;
                for (int x = 0; x < gw; x++)
                {
                    float u = (x + 0.5f) / gw;
                    float a = SampleMask(maskAlpha, maskSize, u, v);
                    float d = depthGrey[row + x] / 255f;

                    if (a >= 0.5f)
                    {
                        halfCount++;
                        fallbackSubj.Add(d);
                    }
                    if (a >= 0.75f)
                    {
                        core.Add(d);
                    }
                    else if (a <= 0.15f)
                    {
                        bg.Add(d);
                    }
                }
            }

            float area = halfCount / (float)n;
            var subjSamples = core.Count > 0 ? core : fallbackSubj;
            bool usable = area >= 0.03f && area <= 0.92f && subjSamples.Count > 0;
            float subjMedian = Median(subjSamples, 0.5f);
            float bgMedian = Median(bg, 0.2f);
            return new SubjectStats(area, subjMedian, bgMedian, usable);
        }

        public static float ComputePortraitPivot(float globalMiddle, SubjectStats stats, bool backdropActive)
        {
            float mid = float.IsFinite(globalMiddle) ? globalMiddle : 0.5f;
            if (!stats.HasUsableSubject)
                return Math.Clamp(mid, 0.05f, 0.95f);

            float subj = float.IsFinite(stats.SubjectMedianDepth) ? stats.SubjectMedianDepth : mid;
            if (backdropActive)
                return Math.Clamp(subj, 0.08f, 0.94f);

            return Math.Clamp(mid * 0.20f + subj * 0.80f, 0.15f, 0.88f);
        }

        /// <summary>
        /// Softly compresses internal foreground depth spread on the raw depth map BEFORE <see cref="SwayDepth.Crisp"/>
        /// builds the silhouette rim, ensuring the crisp rim inherits the exact shaped interior depth with zero spike wall.
        /// </summary>
        public static byte[] ShapeDepthGrey(
            byte[] depthGrey, int gw, int gh,
            byte[] maskAlpha, int maskSize,
            float subjectMedian,
            float internalCompression = 0.68f,
            float maxSpread = 0.24f)
        {
            if (depthGrey == null || gw <= 0 || gh <= 0 || depthGrey.Length < gw * gh)
                return Array.Empty<byte>();

            var result = new byte[gw * gh];
            if (maskAlpha == null || maskSize <= 0 || maskAlpha.Length < maskSize * maskSize)
            {
                Buffer.BlockCopy(depthGrey, 0, result, 0, result.Length);
                return result;
            }

            float med = float.IsFinite(subjectMedian) ? Math.Clamp(subjectMedian, 0.05f, 0.98f) : 0.5f;
            float comp = Math.Clamp(internalCompression, 0.0f, 1.0f);
            float spread = Math.Max(0.01f, maxSpread);
            float lo = Math.Max(0.05f, med - spread);
            float hi = Math.Min(0.98f, med + spread);

            for (int y = 0; y < gh; y++)
            {
                float v = (y + 0.5f) / gh;
                int row = y * gw;
                for (int x = 0; x < gw; x++)
                {
                    float u = (x + 0.5f) / gw;
                    int i = row + x;
                    float d = depthGrey[i] / 255f;
                    float a = SampleMask(maskAlpha, maskSize, u, v);

                    float outD = d;
                    if (a > 0.15f)
                    {
                        float compressed = Math.Clamp(med + (d - med) * comp, lo, hi);
                        float t = a >= 0.50f ? 1f : SmoothStep(0.15f, 0.50f, a);
                        outD = d + (compressed - d) * t;
                    }
                    result[i] = (byte)Math.Clamp((int)(outD * 255f + 0.5f), 0, 255);
                }
            }
            return result;
        }

        public static byte[] ShapeSubjectDepth(
            byte[] crispBgra, int w, int h,
            byte[] maskAlpha, int maskSize,
            float subjectMedian,
            float internalCompression = 0.55f,
            float maxSpread = 0.16f)
        {
            if (crispBgra == null || w <= 0 || h <= 0 || crispBgra.Length < w * h * 4)
                return Array.Empty<byte>();

            var result = new byte[w * h * 4];
            if (maskAlpha == null || maskSize <= 0 || maskAlpha.Length < maskSize * maskSize)
            {
                Buffer.BlockCopy(crispBgra, 0, result, 0, result.Length);
                return result;
            }

            float med = float.IsFinite(subjectMedian) ? Math.Clamp(subjectMedian, 0.05f, 0.98f) : 0.5f;
            float comp = Math.Clamp(internalCompression, 0.0f, 1.0f);
            float spread = Math.Max(0.01f, maxSpread);
            float lo = Math.Max(0.05f, med - spread);
            float hi = Math.Min(0.98f, med + spread);

            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    int o = (row + x) * 4;
                    float d = crispBgra[o] / 255f;
                    float a = SampleMask(maskAlpha, maskSize, u, v);

                    float outD = d;
                    if (a > 0.15f)
                    {
                        float compressed = Math.Clamp(med + (d - med) * comp, lo, hi);
                        float t = a >= 0.50f ? 1f : SmoothStep(0.15f, 0.50f, a);
                        outD = d + (compressed - d) * t;
                    }

                    byte b = (byte)Math.Clamp((int)(outD * 255f + 0.5f), 0, 255);
                    result[o] = b;
                    result[o + 1] = b;
                    result[o + 2] = b;
                    result[o + 3] = crispBgra[o + 3];
                }
            }
            return result;
        }

        public static byte[] SolidifySubjectMask(
            byte[] maskAlpha, int maskSize,
            byte[]? depthGrey, int gw, int gh,
            SubjectStats stats)
        {
            if (maskAlpha == null || maskSize <= 0 || maskAlpha.Length < maskSize * maskSize)
                return Array.Empty<byte>();

            int n = maskSize * maskSize;
            var result = new byte[n];
            Buffer.BlockCopy(maskAlpha, 0, result, 0, n);

            if (!stats.HasUsableSubject)
                return result;

            bool hasDepth = depthGrey != null && gw > 0 && gh > 0 && depthGrey.Length >= gw * gh;
            var extBg = new bool[n];
            for (int i = 0; i < n; i++)
                extBg[i] = maskAlpha[i] < 38;

            // Flood-fill background connected to the image borders.
            var reached = new bool[n];
            var queue = new int[n];
            int head = 0, tail = 0;
            for (int x = 0; x < maskSize; x++)
            {
                if (extBg[x] && !reached[x]) { reached[x] = true; queue[tail++] = x; }
                int bot = (maskSize - 1) * maskSize + x;
                if (extBg[bot] && !reached[bot]) { reached[bot] = true; queue[tail++] = bot; }
            }
            for (int y = 0; y < maskSize; y++)
            {
                int left = y * maskSize;
                if (extBg[left] && !reached[left]) { reached[left] = true; queue[tail++] = left; }
                int right = left + maskSize - 1;
                if (extBg[right] && !reached[right]) { reached[right] = true; queue[tail++] = right; }
            }
            while (head < tail)
            {
                int cur = queue[head++];
                int cx = cur % maskSize, cy = cur / maskSize;
                if (cx > 0 && extBg[cur - 1] && !reached[cur - 1]) { reached[cur - 1] = true; queue[tail++] = cur - 1; }
                if (cx + 1 < maskSize && extBg[cur + 1] && !reached[cur + 1]) { reached[cur + 1] = true; queue[tail++] = cur + 1; }
                if (cy > 0 && extBg[cur - maskSize] && !reached[cur - maskSize]) { reached[cur - maskSize] = true; queue[tail++] = cur - maskSize; }
                if (cy + 1 < maskSize && extBg[cur + maskSize] && !reached[cur + maskSize]) { reached[cur + maskSize] = true; queue[tail++] = cur + maskSize; }
            }

            // Exclude enclosed foreground-depth pockets (e.g. flash-lit cheek dips) from trueBg BEFORE measuring
            // silhouette distance, so their perimeter never creates a 2-3 px hollow ring. Keep threshold strictly
            // above BgMedianDepth so shallow-depth scenes never fill enclosed true-background holes.
            float depthSep = Math.Max(0f, stats.SubjectMedianDepth - stats.BgMedianDepth);
            float fgFloor = stats.BgMedianDepth + Math.Max(0.05f, depthSep * 0.35f);
            float fgThreshold = Math.Max(
                fgFloor,
                Math.Min(stats.SubjectMedianDepth - 0.28f, (stats.SubjectMedianDepth + stats.BgMedianDepth) * 0.5f + 0.02f));
            var fgDepthMap = new bool[n];
            var trueBg = new bool[n];
            for (int y = 0; y < maskSize; y++)
            {
                float v = (y + 0.5f) / maskSize;
                int row = y * maskSize;
                for (int x = 0; x < maskSize; x++)
                {
                    int i = row + x;
                    float u = (x + 0.5f) / maskSize;
                    float d = hasDepth ? SampleDepthBilinear(depthGrey!, gw, gh, u, v) : stats.SubjectMedianDepth;
                    bool isFg = d >= fgThreshold;
                    fgDepthMap[i] = isFg;
                    trueBg[i] = reached[i] || (extBg[i] && !isFg);
                }
            }

            int rimPx = Math.Max(4, maskSize / 160);
            int[] distToTrueBg = ComputeDistanceToMask(trueBg, maskSize, maskSize, rimPx + 3);

            for (int i = 0; i < n; i++)
            {
                byte m = maskAlpha[i];
                if (m >= 255) continue;

                bool isFg = fgDepthMap[i];
                if (extBg[i] && !reached[i] && isFg)
                {
                    result[i] = 255;
                }
                else if (!trueBg[i] && distToTrueBg[i] > rimPx && (isFg || m >= 120))
                {
                    float blend = Math.Clamp((distToTrueBg[i] - rimPx) / 3f, 0f, 1f);
                    float target = m + (255f - m) * blend;
                    result[i] = (byte)Math.Clamp((int)(target + 0.5f), 0, 255);
                }
            }

            return result;
        }

        public static byte[] BuildDecontaminatedCutout(byte[] photoBgra, int w, int h, byte[] maskAlpha, int maskSize)
        {
            if (photoBgra == null || w <= 0 || h <= 0 || photoBgra.Length < w * h * 4)
                return Array.Empty<byte>();

            int n = w * h;
            var result = new byte[n * 4];
            if (maskAlpha == null || maskSize <= 0 || maskAlpha.Length < maskSize * maskSize)
                return result;

            var alpha = new float[n];
            var origR = new float[n];
            var origG = new float[n];
            var origB = new float[n];
            var bgSeed = new bool[n];
            var known = new bool[n];
            var rimMask = new bool[n];
            var fgR = new float[n];
            var fgG = new float[n];
            var fgB = new float[n];

            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    int idx = row + x;
                    int o = idx * 4;
                    float a = SampleMask(maskAlpha, maskSize, u, v);
                    alpha[idx] = a;
                    bgSeed[idx] = a <= 0.08f;

                    byte pa = photoBgra[o + 3];
                    float pb = photoBgra[o];
                    float pg = photoBgra[o + 1];
                    float pr = photoBgra[o + 2];
                    if (pa > 0 && pa < 255)
                    {
                        float inv = 255f / pa;
                        pb = Math.Min(255f, pb * inv);
                        pg = Math.Min(255f, pg * inv);
                        pr = Math.Min(255f, pr * inv);
                    }
                    else if (pa == 0)
                    {
                        pb = pg = pr = 0f;
                    }

                    origB[idx] = pb;
                    origG[idx] = pg;
                    origR[idx] = pr;

                    if (a >= 0.92f)
                    {
                        known[idx] = true;
                        fgB[idx] = pb;
                        fgG[idx] = pg;
                        fgR[idx] = pr;
                    }
                }
            }

            // Seed silhouette distance ONLY from border-connected background pixels (plus verified large background holes),
            // so isolated interior alpha dips never trigger color decontamination inside the subject.
            var extBgReached = new bool[n];
            var bq = new int[n];
            int bHead = 0, bTail = 0;
            for (int x = 0; x < w; x++)
            {
                if (bgSeed[x] && !extBgReached[x]) { extBgReached[x] = true; bq[bTail++] = x; }
                int bot = (h - 1) * w + x;
                if (bgSeed[bot] && !extBgReached[bot]) { extBgReached[bot] = true; bq[bTail++] = bot; }
            }
            for (int y = 0; y < h; y++)
            {
                int left = y * w;
                if (bgSeed[left] && !extBgReached[left]) { extBgReached[left] = true; bq[bTail++] = left; }
                int right = left + w - 1;
                if (bgSeed[right] && !extBgReached[right]) { extBgReached[right] = true; bq[bTail++] = right; }
            }
            while (bHead < bTail)
            {
                int cur = bq[bHead++];
                int cx = cur % w, cy = cur / w;
                if (cx > 0 && bgSeed[cur - 1] && !extBgReached[cur - 1]) { extBgReached[cur - 1] = true; bq[bTail++] = cur - 1; }
                if (cx + 1 < w && bgSeed[cur + 1] && !extBgReached[cur + 1]) { extBgReached[cur + 1] = true; bq[bTail++] = cur + 1; }
                if (cy > 0 && bgSeed[cur - w] && !extBgReached[cur - w]) { extBgReached[cur - w] = true; bq[bTail++] = cur - w; }
                if (cy + 1 < h && bgSeed[cur + w] && !extBgReached[cur + w]) { extBgReached[cur + w] = true; bq[bTail++] = cur + w; }
            }

            // Include substantial enclosed background holes (>= 0.15% of frame, e.g. arm-torso gaps) in extBgReached
            var holeVisited = new bool[n];
            int minBgHolePixels = Math.Max(16, (int)(n * 0.0015f));
            var holeComponent = new List<int>(256);
            for (int s = 0; s < n; s++)
            {
                if (!bgSeed[s] || extBgReached[s] || holeVisited[s]) continue;
                holeComponent.Clear();
                bHead = 0; bTail = 0;
                holeVisited[s] = true;
                bq[bTail++] = s;
                while (bHead < bTail)
                {
                    int cur = bq[bHead++];
                    holeComponent.Add(cur);
                    int cx = cur % w, cy = cur / w;
                    if (cx > 0 && bgSeed[cur - 1] && !extBgReached[cur - 1] && !holeVisited[cur - 1]) { holeVisited[cur - 1] = true; bq[bTail++] = cur - 1; }
                    if (cx + 1 < w && bgSeed[cur + 1] && !extBgReached[cur + 1] && !holeVisited[cur + 1]) { holeVisited[cur + 1] = true; bq[bTail++] = cur + 1; }
                    if (cy > 0 && bgSeed[cur - w] && !extBgReached[cur - w] && !holeVisited[cur - w]) { holeVisited[cur - w] = true; bq[bTail++] = cur - w; }
                    if (cy + 1 < h && bgSeed[cur + w] && !extBgReached[cur + w] && !holeVisited[cur + w]) { holeVisited[cur + w] = true; bq[bTail++] = cur + w; }
                }
                if (holeComponent.Count >= minBgHolePixels)
                {
                    foreach (int p in holeComponent) extBgReached[p] = true;
                }
            }

            int maxRim = Math.Max(6, Math.Min(w, h) / 140);
            int[] distToBg = ComputeDistanceToMask(extBgReached, w, h, maxRim);
            for (int idx = 0; idx < n; idx++)
            {
                float a = alpha[idx];
                if (a > 0.015f && a < 0.92f && distToBg[idx] <= maxRim)
                    rimMask[idx] = true;
            }

            // Dilate eligible transition mask by 2 px so narrow hair strands/wisps separated by 1-2 px still receive foreground color.
            var eligible = DilateBool(rimMask, w, h, 2);

            // 8-ring BFS propagating opaque interior RGB outward across transition/rim pixels without full-image scans.
            var queued = new bool[n];
            var front = new List<int>(Math.Max(64, n / 32));
            for (int i = 0; i < n; i++)
            {
                if (!known[i]) continue;
                int x0 = i % w, y0 = i / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y0 + dy;
                    if (yy < 0 || yy >= h) continue;
                    int rOff = yy * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int xx = x0 + dx;
                        if (xx < 0 || xx >= w) continue;
                        int j = rOff + xx;
                        if (!known[j] && eligible[j] && !queued[j])
                        {
                            queued[j] = true;
                            front.Add(j);
                        }
                    }
                }
            }

            var nextFront = new List<int>(front.Count);
            var add = new List<(int idx, float b, float g, float r)>(front.Count);
            for (int ring = 0; ring < 8 && front.Count > 0; ring++)
            {
                add.Clear();
                foreach (int idx in front)
                {
                    int x = idx % w, y = idx / w;
                    float sb = 0, sg = 0, sr = 0;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        int rOff = yy * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            int j = rOff + xx;
                            if (!known[j]) continue;
                            sb += fgB[j];
                            sg += fgG[j];
                            sr += fgR[j];
                            count++;
                        }
                    }
                    if (count > 0)
                    {
                        float inv = 1f / count;
                        add.Add((idx, sb * inv, sg * inv, sr * inv));
                    }
                }

                if (add.Count == 0) break;

                foreach (var item in add)
                {
                    known[item.idx] = true;
                    fgB[item.idx] = item.b;
                    fgG[item.idx] = item.g;
                    fgR[item.idx] = item.r;
                }

                nextFront.Clear();
                foreach (var item in add)
                {
                    int x0 = item.idx % w, y0 = item.idx / w;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y0 + dy;
                        if (yy < 0 || yy >= h) continue;
                        int rOff = yy * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x0 + dx;
                            if (xx < 0 || xx >= w) continue;
                            int j = rOff + xx;
                            if (!known[j] && eligible[j] && !queued[j])
                            {
                                queued[j] = true;
                                nextFront.Add(j);
                            }
                        }
                    }
                }
                (front, nextFront) = (nextFront, front);
            }

            for (int i = 0; i < n; i++)
            {
                float a = alpha[i];
                int o = i * 4;
                if (a <= 0.015f)
                {
                    result[o] = 0;
                    result[o + 1] = 0;
                    result[o + 2] = 0;
                    result[o + 3] = 0;
                    continue;
                }

                float b = origB[i];
                float g = origG[i];
                float r = origR[i];

                if (a < 0.92f && known[i] && distToBg[i] <= maxRim)
                {
                    float decontam = (1f - SmoothStep(0.45f, 0.92f, a)) * 0.92f;
                    b += (fgB[i] - b) * decontam;
                    g += (fgG[i] - g) * decontam;
                    r += (fgR[i] - r) * decontam;
                }

                result[o] = (byte)Math.Clamp((int)(b * a + 0.5f), 0, 255);
                result[o + 1] = (byte)Math.Clamp((int)(g * a + 0.5f), 0, 255);
                result[o + 2] = (byte)Math.Clamp((int)(r * a + 0.5f), 0, 255);
                result[o + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            }

            return result;
        }

        public static byte[] BuildSubjectDilatedDepth(
            byte[] depthGrey, int gw, int gh,
            byte[] maskAlpha, int maskSize,
            float subjectMedian,
            float internalCompression = 0.48f,
            float maxSpread = 0.14f)
        {
            if (depthGrey == null || gw <= 0 || gh <= 0 || depthGrey.Length < gw * gh)
                return Array.Empty<byte>();

            int n = gw * gh;
            float med = float.IsFinite(subjectMedian) ? Math.Clamp(subjectMedian, 0.05f, 0.98f) : 0.5f;
            float comp = Math.Clamp(internalCompression, 0.0f, 1.0f);
            float spread = Math.Max(0.01f, maxSpread);
            float lo = Math.Max(0.05f, med - spread);
            float hi = Math.Min(0.98f, med + spread);

            var val = new float[n];
            var known = new bool[n];
            bool hasMask = maskAlpha != null && maskSize > 0 && maskAlpha.Length >= maskSize * maskSize;

            for (int y = 0; y < gh; y++)
            {
                float v = (y + 0.5f) / gh;
                int row = y * gw;
                for (int x = 0; x < gw; x++)
                {
                    float u = (x + 0.5f) / gw;
                    int i = row + x;
                    float a = hasMask ? SampleMask(maskAlpha!, maskSize, u, v) : 1f;
                    if (a >= 0.35f)
                    {
                        float d = depthGrey[i] / 255f;
                        val[i] = Math.Clamp(med + (d - med) * comp, lo, hi);
                        known[i] = true;
                    }
                }
            }

            int rings = Math.Max(12, gw / 24);
            var queued = new bool[n];
            var front = new List<int>(Math.Max(32, n / 16));
            for (int i = 0; i < n; i++)
            {
                if (!known[i]) continue;
                int x0 = i % gw, y0 = i / gw;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y0 + dy;
                    if (yy < 0 || yy >= gh) continue;
                    int rOff = yy * gw;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x0 + dx;
                        if (xx < 0 || xx >= gw) continue;
                        int j = rOff + xx;
                        if (!known[j] && !queued[j])
                        {
                            queued[j] = true;
                            front.Add(j);
                        }
                    }
                }
            }

            var nextFront = new List<int>(front.Count);
            var add = new List<(int idx, float d)>(front.Count);
            for (int ring = 0; ring < rings && front.Count > 0; ring++)
            {
                add.Clear();
                foreach (int idx in front)
                {
                    int x = idx % gw, y = idx / gw;
                    float sum = 0f;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= gh) continue;
                        int rOff = yy * gw;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= gw) continue;
                            int j = rOff + xx;
                            if (!known[j]) continue;
                            sum += val[j];
                            count++;
                        }
                    }
                    if (count > 0)
                        add.Add((idx, sum / count));
                }

                if (add.Count == 0) break;

                foreach (var item in add)
                {
                    known[item.idx] = true;
                    val[item.idx] = item.d;
                }

                nextFront.Clear();
                foreach (var item in add)
                {
                    int x0 = item.idx % gw, y0 = item.idx / gw;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y0 + dy;
                        if (yy < 0 || yy >= gh) continue;
                        int rOff = yy * gw;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x0 + dx;
                            if (xx < 0 || xx >= gw) continue;
                            int j = rOff + xx;
                            if (!known[j] && !queued[j])
                            {
                                queued[j] = true;
                                nextFront.Add(j);
                            }
                        }
                    }
                }
                (front, nextFront) = (nextFront, front);
            }

            for (int i = 0; i < n; i++)
            {
                if (!known[i]) val[i] = med;
            }

            // One 3x3 box blur pass keeps dilated depth smooth across the silhouette boundary.
            var blurred = BoxBlur3x3(val, gw, gh);
            var bytes = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                byte b = (byte)Math.Clamp((int)(blurred[i] * 255f + 0.5f), 0, 255);
                int o = i * 4;
                bytes[o] = b;
                bytes[o + 1] = b;
                bytes[o + 2] = b;
                bytes[o + 3] = 255;
            }
            return bytes;
        }

        public static BackgroundPalette SampleBackgroundPalette(byte[] photoBgra, int w, int h, byte[] maskAlpha, int maskSize)
        {
            var fallback = new BackgroundPalette(
                0.15f, 0.20f, 0.28f,
                0.09f, 0.11f, 0.16f,
                0.88f, 0.64f, 0.36f);

            if (photoBgra == null || w <= 0 || h <= 0 || photoBgra.Length < w * h * 4)
                return fallback;

            bool hasMask = maskAlpha != null && maskSize > 0 && maskAlpha.Length >= maskSize * maskSize;
            int stepX = Math.Max(1, w / 64);
            int stepY = Math.Max(1, h / 64);
            int halfH = h / 2;

            double topR = 0, topG = 0, topB = 0;
            int topCount = 0;
            double botR = 0, botG = 0, botB = 0;
            int botCount = 0;

            float bestAccScore = -1f;
            float bestAccR = fallback.AccR, bestAccG = fallback.AccG, bestAccB = fallback.AccB;

            for (int y = 0; y < h; y += stepY)
            {
                float v = (y + 0.5f) / h;
                int row = y * w;
                for (int x = 0; x < w; x += stepX)
                {
                    float u = (x + 0.5f) / w;
                    if (hasMask && SampleMask(maskAlpha!, maskSize, u, v) >= 0.15f)
                        continue;

                    int o = (row + x) * 4;
                    byte pa = photoBgra[o + 3];
                    if (pa == 0) continue;

                    float scale = pa < 255 ? (1f / pa) : (1f / 255f);
                    float b = Math.Clamp(photoBgra[o] * scale, 0f, 1f);
                    float g = Math.Clamp(photoBgra[o + 1] * scale, 0f, 1f);
                    float r = Math.Clamp(photoBgra[o + 2] * scale, 0f, 1f);

                    if (y < halfH)
                    {
                        topR += r; topG += g; topB += b; topCount++;
                    }
                    else
                    {
                        botR += r; botG += g; botB += b; botCount++;
                    }

                    float maxC = Math.Max(r, Math.Max(g, b));
                    float minC = Math.Min(r, Math.Min(g, b));
                    float sat = maxC - minC;
                    float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    float score = sat * (0.35f + 0.65f * luma);
                    if (score > bestAccScore)
                    {
                        bestAccScore = score;
                        bestAccR = r;
                        bestAccG = g;
                        bestAccB = b;
                    }
                }
            }

            if (topCount == 0 && botCount == 0)
                return fallback;

            float tR = topCount > 0 ? (float)(topR / topCount) : (float)(botR / botCount);
            float tG = topCount > 0 ? (float)(topG / topCount) : (float)(botG / botCount);
            float tB = topCount > 0 ? (float)(topB / topCount) : (float)(botB / botCount);

            float bR = botCount > 0 ? (float)(botR / botCount) : tR * 0.78f;
            float bG = botCount > 0 ? (float)(botG / botCount) : tG * 0.78f;
            float bB = botCount > 0 ? (float)(botB / botCount) : tB * 0.78f;

            if (bestAccScore < 0.08f)
            {
                bestAccR = Math.Clamp((tR + bR) * 0.5f + 0.28f, 0.25f, 0.95f);
                bestAccG = Math.Clamp((tG + bG) * 0.5f + 0.20f, 0.25f, 0.90f);
                bestAccB = Math.Clamp((tB + bB) * 0.5f + 0.12f, 0.25f, 0.92f);
            }

            return new BackgroundPalette(
                Math.Clamp(tR, 0f, 1f), Math.Clamp(tG, 0f, 1f), Math.Clamp(tB, 0f, 1f),
                Math.Clamp(bR, 0f, 1f), Math.Clamp(bG, 0f, 1f), Math.Clamp(bB, 0f, 1f),
                Math.Clamp(bestAccR, 0f, 1f), Math.Clamp(bestAccG, 0f, 1f), Math.Clamp(bestAccB, 0f, 1f));
        }

        /// <summary>
        /// Builds an isotropic, streak-free background clean plate for <c>CleanBlur</c> by replacing the dilated
        /// foreground silhouette with a symmetric Gaussian push-pull pyramid fill and feathering back into the photo.
        /// </summary>
        public static byte[] BuildSymmetricCleanPlate(byte[] photoBgra, int w, int h, byte[] maskAlpha, int maskSize)
        {
            if (photoBgra == null || w <= 0 || h <= 0 || photoBgra.Length < w * h * 4)
                return Array.Empty<byte>();

            var result = new byte[w * h * 4];
            if (maskAlpha == null || maskSize <= 0 || maskAlpha.Length < maskSize * maskSize)
            {
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 4;
                    result[o] = photoBgra[o];
                    result[o + 1] = photoBgra[o + 1];
                    result[o + 2] = photoBgra[o + 2];
                    result[o + 3] = 255;
                }
                return result;
            }

            const int WorkLongSide = 1024;
            double scale = Math.Min(1.0, WorkLongSide / (double)Math.Max(w, h));
            int ww = Math.Max(8, (int)Math.Round(w * scale));
            int wh = Math.Max(8, (int)Math.Round(h * scale));
            int wn = ww * wh;

            var orig = new float[wn * 3];
            var hidden = new bool[wn];
            for (int y = 0; y < wh; y++)
            {
                float sy = (y + 0.5f) * h / wh - 0.5f;
                float v = (y + 0.5f) / wh;
                for (int x = 0; x < ww; x++)
                {
                    float sx = (x + 0.5f) * w / ww - 0.5f;
                    float u = (x + 0.5f) / ww;
                    int idx = y * ww + x;
                    var (b, g, r) = SamplePhotoBgraBilinear(photoBgra, w, h, sx, sy);
                    orig[idx * 3] = b;
                    orig[idx * 3 + 1] = g;
                    orig[idx * 3 + 2] = r;
                    hidden[idx] = SampleMask(maskAlpha, maskSize, u, v) > 0.03f;
                }
            }

            int hideR = Math.Max(4, (int)Math.Round(6.0 * ww / 720.0));
            bool[] dilHidden = DilateBool(hidden, ww, wh, hideR);

            bool anyValid = false;
            var k0 = new float[wn];
            var c0 = new float[wn * 3];
            for (int i = 0; i < wn; i++)
            {
                if (!dilHidden[i])
                {
                    anyValid = true;
                    k0[i] = 1f;
                    c0[i * 3] = orig[i * 3];
                    c0[i * 3 + 1] = orig[i * 3 + 1];
                    c0[i * 3 + 2] = orig[i * 3 + 2];
                }
            }

            if (!anyValid)
            {
                for (int i = 0; i < w * h; i++)
                {
                    int o = i * 4;
                    result[o] = photoBgra[o];
                    result[o + 1] = photoBgra[o + 1];
                    result[o + 2] = photoBgra[o + 2];
                    result[o + 3] = 255;
                }
                return result;
            }

            // Symmetric push phase: downsample until 1x1 or zero holes remain.
            var levels = new List<(float[] C, float[] K, int W, int H)> { (c0, k0, ww, wh) };
            while (levels[^1].W > 1 || levels[^1].H > 1)
            {
                var (c, k, lw, lh) = levels[^1];
                bool hasHole = false;
                for (int i = 0; i < k.Length; i++)
                {
                    if (k[i] <= 0.5f) { hasHole = true; break; }
                }
                if (!hasHole) break;

                int nw = Math.Max(1, (lw + 1) / 2);
                int nh = Math.Max(1, (lh + 1) / 2);
                var c2 = new float[nw * nh * 3];
                var k2 = new float[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        float ks = 0f, sb = 0f, sg = 0f, sr = 0f;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            int sy = y * 2 + dy;
                            if (sy >= lh) continue;
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = x * 2 + dx;
                                if (sx >= lw) continue;
                                int s = sy * lw + sx;
                                float kw = k[s];
                                if (kw <= 0.5f) continue;
                                ks += 1f;
                                sb += c[s * 3];
                                sg += c[s * 3 + 1];
                                sr += c[s * 3 + 2];
                            }
                        }
                        int o = y * nw + x;
                        if (ks > 0f)
                        {
                            k2[o] = 1f;
                            c2[o * 3] = sb / ks;
                            c2[o * 3 + 1] = sg / ks;
                            c2[o * 3 + 2] = sr / ks;
                        }
                    }
                }
                levels.Add((c2, k2, nw, nh));
            }

            // Initialize top level (fill any residual top-level holes with valid top-level mean).
            var top = levels[^1];
            var fill = new float[top.W * top.H * 3];
            double meanB = 0, meanG = 0, meanR = 0;
            int validTopCount = 0;
            for (int i = 0; i < top.K.Length; i++)
            {
                if (top.K[i] > 0.5f)
                {
                    meanB += top.C[i * 3];
                    meanG += top.C[i * 3 + 1];
                    meanR += top.C[i * 3 + 2];
                    validTopCount++;
                }
            }
            float fbTop = validTopCount > 0 ? (float)(meanB / validTopCount) : 0f;
            float fgTop = validTopCount > 0 ? (float)(meanG / validTopCount) : 0f;
            float frTop = validTopCount > 0 ? (float)(meanR / validTopCount) : 0f;
            for (int i = 0; i < top.K.Length; i++)
            {
                if (top.K[i] > 0.5f)
                {
                    fill[i * 3] = top.C[i * 3];
                    fill[i * 3 + 1] = top.C[i * 3 + 1];
                    fill[i * 3 + 2] = top.C[i * 3 + 2];
                }
                else
                {
                    fill[i * 3] = fbTop;
                    fill[i * 3 + 1] = fgTop;
                    fill[i * 3 + 2] = frTop;
                }
            }

            // Symmetric pull phase with harmonic relaxation from coarse to fine.
            int fw = top.W, fh = top.H;
            for (int l = levels.Count - 2; l >= 0; l--)
            {
                var (c, k, lw, lh) = levels[l];
                var cur = new float[lw * lh * 3];
                for (int y = 0; y < lh; y++)
                {
                    float uy = (y - 0.5f) * 0.5f;
                    for (int x = 0; x < lw; x++)
                    {
                        float ux = (x - 0.5f) * 0.5f;
                        int o = y * lw + x;
                        if (k[o] > 0.5f)
                        {
                            cur[o * 3] = c[o * 3];
                            cur[o * 3 + 1] = c[o * 3 + 1];
                            cur[o * 3 + 2] = c[o * 3 + 2];
                        }
                        else
                        {
                            cur[o * 3] = SampleChannelBilinear(fill, fw, fh, ux, uy, 0);
                            cur[o * 3 + 1] = SampleChannelBilinear(fill, fw, fh, ux, uy, 1);
                            cur[o * 3 + 2] = SampleChannelBilinear(fill, fw, fh, ux, uy, 2);
                        }
                    }
                }

                var tmp = new float[lw * lh * 3];
                for (int pass = 0; pass < 6; pass++)
                {
                    Array.Copy(cur, tmp, cur.Length);
                    for (int y = 0; y < lh; y++)
                    {
                        int ym = Math.Max(0, y - 1) * lw;
                        int y0 = y * lw;
                        int yp = Math.Min(lh - 1, y + 1) * lw;
                        for (int x = 0; x < lw; x++)
                        {
                            int o = y0 + x;
                            if (k[o] > 0.5f) continue;
                            int xm = Math.Max(0, x - 1);
                            int xp = Math.Min(lw - 1, x + 1);
                            for (int ch = 0; ch < 3; ch++)
                            {
                                float ortho = tmp[(ym + x) * 3 + ch] + tmp[(yp + x) * 3 + ch] + tmp[(y0 + xm) * 3 + ch] + tmp[(y0 + xp) * 3 + ch];
                                float diag = tmp[(ym + xm) * 3 + ch] + tmp[(ym + xp) * 3 + ch] + tmp[(yp + xm) * 3 + ch] + tmp[(yp + xp) * 3 + ch];
                                cur[o * 3 + ch] = (ortho + 0.5f * diag) / 6f;
                            }
                        }
                    }
                }
                fill = cur;
                fw = lw;
                fh = lh;
            }

            // Feather replacement over the excluded dilated silhouette so soft fringes never etch into the clean plate,
            // while unoccluded outer background remains pixel-identical.
            var hideW = new float[wn];
            for (int i = 0; i < wn; i++)
                hideW[i] = dilHidden[i] ? 1f : 0f;
            hideW = BoxBlur3x3(hideW, ww, wh);

            for (int y = 0; y < h; y++)
            {
                float fy = (y + 0.5f) * wh / h - 0.5f;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float fx = (x + 0.5f) * ww / w - 0.5f;
                    int o = (row + x) * 4;
                    float hw = SampleScalarBilinear(hideW, ww, wh, fx, fy);
                    if (hw > 1e-3f)
                    {
                        float fb = SampleChannelBilinear(fill, ww, wh, fx, fy, 0);
                        float fg = SampleChannelBilinear(fill, ww, wh, fx, fy, 1);
                        float fr = SampleChannelBilinear(fill, ww, wh, fx, fy, 2);
                        result[o] = (byte)Math.Clamp((int)(photoBgra[o] * (1f - hw) + fb * hw + 0.5f), 0, 255);
                        result[o + 1] = (byte)Math.Clamp((int)(photoBgra[o + 1] * (1f - hw) + fg * hw + 0.5f), 0, 255);
                        result[o + 2] = (byte)Math.Clamp((int)(photoBgra[o + 2] * (1f - hw) + fr * hw + 0.5f), 0, 255);
                    }
                    else
                    {
                        result[o] = photoBgra[o];
                        result[o + 1] = photoBgra[o + 1];
                        result[o + 2] = photoBgra[o + 2];
                    }
                    result[o + 3] = 255;
                }
            }
            return result;
        }

        private static (float B, float G, float R) SamplePhotoBgraBilinear(byte[] bgra, int w, int h, float fx, float fy)
        {
            float cx = Math.Clamp(fx, 0f, w - 1);
            float cy = Math.Clamp(fy, 0f, h - 1);
            int x0 = (int)cx, y0 = (int)cy;
            int x1 = Math.Min(x0 + 1, w - 1);
            int y1 = Math.Min(y0 + 1, h - 1);
            float tx = cx - x0, ty = cy - y0;

            float w00 = (1f - tx) * (1f - ty);
            float w10 = tx * (1f - ty);
            float w01 = (1f - tx) * ty;
            float w11 = tx * ty;

            int o00 = (y0 * w + x0) * 4;
            int o10 = (y0 * w + x1) * 4;
            int o01 = (y1 * w + x0) * 4;
            int o11 = (y1 * w + x1) * 4;

            return (
                bgra[o00] * w00 + bgra[o10] * w10 + bgra[o01] * w01 + bgra[o11] * w11,
                bgra[o00 + 1] * w00 + bgra[o10 + 1] * w10 + bgra[o01 + 1] * w01 + bgra[o11 + 1] * w11,
                bgra[o00 + 2] * w00 + bgra[o10 + 2] * w10 + bgra[o01 + 2] * w01 + bgra[o11 + 2] * w11);
        }

        private static float SampleScalarBilinear(float[] map, int w, int h, float fx, float fy)
        {
            float cx = Math.Clamp(fx, 0f, w - 1);
            float cy = Math.Clamp(fy, 0f, h - 1);
            int x0 = (int)cx, y0 = (int)cy;
            int x1 = Math.Min(x0 + 1, w - 1);
            int y1 = Math.Min(y0 + 1, h - 1);
            float tx = cx - x0, ty = cy - y0;
            float top = map[y0 * w + x0] + (map[y0 * w + x1] - map[y0 * w + x0]) * tx;
            float bot = map[y1 * w + x0] + (map[y1 * w + x1] - map[y1 * w + x0]) * tx;
            return top + (bot - top) * ty;
        }

        private static float SampleChannelBilinear(float[] rgb, int w, int h, float fx, float fy, int ch)
        {
            float cx = Math.Clamp(fx, 0f, w - 1);
            float cy = Math.Clamp(fy, 0f, h - 1);
            int x0 = (int)cx, y0 = (int)cy;
            int x1 = Math.Min(x0 + 1, w - 1);
            int y1 = Math.Min(y0 + 1, h - 1);
            float tx = cx - x0, ty = cy - y0;
            float top = rgb[(y0 * w + x0) * 3 + ch] + (rgb[(y0 * w + x1) * 3 + ch] - rgb[(y0 * w + x0) * 3 + ch]) * tx;
            float bot = rgb[(y1 * w + x0) * 3 + ch] + (rgb[(y1 * w + x1) * 3 + ch] - rgb[(y1 * w + x0) * 3 + ch]) * tx;
            return top + (bot - top) * ty;
        }

        private static float SampleMask(byte[] alpha, int size, float u, float v) =>
            SampleDepthBilinear(alpha, size, size, u, v);

        private static float SampleDepthBilinear(byte[] map, int w, int h, float u, float v)
        {
            float gx = Math.Clamp(u * w - 0.5f, 0f, w - 1);
            float gy = Math.Clamp(v * h - 0.5f, 0f, h - 1);
            int x0 = (int)gx, y0 = (int)gy;
            int x1 = Math.Min(x0 + 1, w - 1);
            int y1 = Math.Min(y0 + 1, h - 1);
            float fx = gx - x0, fy = gy - y0;
            float top = map[y0 * w + x0] + (map[y0 * w + x1] - map[y0 * w + x0]) * fx;
            float bottom = map[y1 * w + x0] + (map[y1 * w + x1] - map[y1 * w + x0]) * fx;
            return (top + (bottom - top) * fy) / 255f;
        }

        private static int[] ComputeDistanceToMask(bool[] seedMask, int w, int h, int maxRings)
        {
            int n = w * h;
            var dist = new int[n];
            var front = new List<int>(Math.Max(64, n / 32));
            for (int i = 0; i < n; i++)
            {
                if (seedMask[i])
                {
                    dist[i] = 0;
                    int x = i % w, y = i / w;
                    bool touchesNonSeed =
                        (x > 0 && !seedMask[i - 1]) ||
                        (x + 1 < w && !seedMask[i + 1]) ||
                        (y > 0 && !seedMask[i - w]) ||
                        (y + 1 < h && !seedMask[i + w]);
                    if (touchesNonSeed) front.Add(i);
                }
                else
                {
                    dist[i] = int.MaxValue;
                }
            }

            var nextFront = new List<int>(front.Count);
            for (int ring = 1; ring <= maxRings && front.Count > 0; ring++)
            {
                nextFront.Clear();
                foreach (int idx in front)
                {
                    int x0 = idx % w, y0 = idx / w;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y0 + dy;
                        if (yy < 0 || yy >= h) continue;
                        int rOff = yy * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x0 + dx;
                            if (xx < 0 || xx >= w) continue;
                            int j = rOff + xx;
                            if (dist[j] != int.MaxValue) continue;
                            dist[j] = ring;
                            nextFront.Add(j);
                        }
                    }
                }
                (front, nextFront) = (nextFront, front);
            }
            return dist;
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / Math.Max(1e-6f, edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private static float Median(List<float> values, float fallback)
        {
            if (values.Count == 0) return fallback;
            values.Sort();
            int mid = values.Count / 2;
            if ((values.Count & 1) == 1) return values[mid];
            return 0.5f * (values[mid - 1] + values[mid]);
        }

        private static bool[] DilateBool(bool[] mask, int w, int h, int radius)
        {
            var row = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * w;
                int last = -100000;
                for (int x = 0; x < w; x++)
                {
                    if (mask[rowOff + x]) last = x;
                    row[rowOff + x] = x - last <= radius;
                }
                last = 100000;
                for (int x = w - 1; x >= 0; x--)
                {
                    if (mask[rowOff + x]) last = x;
                    if (last - x <= radius) row[rowOff + x] = true;
                }
            }
            var outMask = new bool[w * h];
            for (int x = 0; x < w; x++)
            {
                int last = -100000;
                for (int y = 0; y < h; y++)
                {
                    if (row[y * w + x]) last = y;
                    outMask[y * w + x] = y - last <= radius;
                }
                last = 100000;
                for (int y = h - 1; y >= 0; y--)
                {
                    if (row[y * w + x]) last = y;
                    if (last - y <= radius) outMask[y * w + x] = true;
                }
            }
            return outMask;
        }

        private static float[] BoxBlur3x3(float[] src, int w, int h)
        {
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                int ym = Math.Max(0, y - 1) * w;
                int y0 = y * w;
                int yp = Math.Min(h - 1, y + 1) * w;
                for (int x = 0; x < w; x++)
                {
                    int xm = Math.Max(0, x - 1);
                    int xp = Math.Min(w - 1, x + 1);
                    float sum =
                        src[ym + xm] + src[ym + x] + src[ym + xp] +
                        src[y0 + xm] + src[y0 + x] + src[y0 + xp] +
                        src[yp + xm] + src[yp + x] + src[yp + xp];
                    dst[y0 + x] = sum / 9f;
                }
            }
            return dst;
        }
    }
}

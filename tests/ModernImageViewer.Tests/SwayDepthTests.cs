using ModernImageViewer;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>
    /// Sway's depth at the subject's outline (SwayDepth.Crisp). Two cases from 2026-11-11: hair whose depth fades toward
    /// the background inside the mask tore at 6% (IMGP0801), and a profile's lips were lifted to the shoulder in front of
    /// them when the rim took the nearest depth anywhere within reach (Let_Me_Tell_You).
    /// </summary>
    public class SwayDepthTests
    {
        private const int N = 60; // depth map, mask and result all 60 x 60

        private static byte At(byte[] bgra, int x, int y) => bgra[(y * N + x) * 4];

        /// <summary>Subject is columns 15 and right; background left of it.</summary>
        private static byte[] Mask()
        {
            var alpha = new byte[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 15; x < N; x++)
                    alpha[y * N + x] = 255;
            return alpha;
        }

        [Fact]
        public void Hair_fading_inside_the_outline_joins_the_head()
        {
            var grey = new byte[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    grey[y * N + x] = x < 15 ? (byte)26 : x == 15 ? (byte)26 : x == 16 ? (byte)90 : (byte)153;
            var result = SwayDepth.Crisp(grey, N, N, Mask(), N, N, N);
            Assert.True(At(result, 15, 30) > 128, $"hair at the outline stayed at {At(result, 15, 30)}, not lifted to the head (153)");
            Assert.True(At(result, 13, 30) < 40, $"background beside the outline became {At(result, 13, 30)}");
        }

        [Fact]
        public void Lips_beside_a_nearer_shoulder_keep_the_face_depth()
        {
            // Face at 0.5 everywhere inside; a shoulder at 0.9 from row 30 down. Row 28 at the outline is the lips.
            var grey = new byte[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    grey[y * N + x] = x < 15 ? (byte)26 : y >= 30 ? (byte)230 : (byte)128;
            var result = SwayDepth.Crisp(grey, N, N, Mask(), N, N, N);
            Assert.True(At(result, 15, 28) < 140, $"lips at the outline were lifted to {At(result, 15, 28)} (shoulder 230, face 128)");
            Assert.True(At(result, 15, 34) > 200, $"shoulder at the outline dropped to {At(result, 15, 34)}");
        }

        // v0.35.4: with the photo, the outline is cleaned up. The cases below are 200 x 200 pictures (large enough for the
        // 2% stray-hair reach, 4 px, and the 0.25% hole limit, 100 px): subject from column 102 right, wall left of it; the
        // mask fades over columns 98-103 as a real one does. The depth map is half the size.

        private const int W = 200, G = 100;
        private static readonly byte[] SoftEdge = { 0, 10, 35, 90, 170, 235 }; // columns 98-103

        private static byte[] SoftMask()
        {
            var alpha = new byte[W * W];
            for (int y = 0; y < W; y++)
                for (int x = 0; x < W; x++)
                    alpha[y * W + x] = x < 98 ? (byte)0 : x < 104 ? SoftEdge[x - 98] : (byte)255;
            return alpha;
        }

        /// <summary>Depth map (100 x 100): wall 0.08 up to the cell beside the subject, hair 0.6 from there. The cell beside
        /// the edge holding the wall's depth is what made the shimmering dip on real photos.</summary>
        private static byte[] HalfGrey()
        {
            var grey = new byte[G * G];
            for (int y = 0; y < G; y++)
                for (int x = 0; x < G; x++)
                    grey[y * G + x] = x < 51 ? (byte)20 : (byte)153;
            return grey;
        }

        /// <summary>BGRA photo, grey: <paramref name="subject"/> from column 100 right, <paramref name="wallColour"/> left of it.</summary>
        private static byte[] Photo(byte subject, byte wallColour, Func<int, int, byte>? at = null)
        {
            var px = new byte[W * W * 4];
            for (int y = 0; y < W; y++)
                for (int x = 0; x < W; x++)
                {
                    byte v = at?.Invoke(x, y) ?? (x >= 100 ? subject : wallColour);
                    int o = (y * W + x) * 4;
                    px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
                }
            return px;
        }

        private static byte[] Run(byte[] alpha, byte[]? photo, byte[]? grey = null) =>
            SwayDepth.Crisp(grey ?? HalfGrey(), G, G, alpha, W, W, W, photo, photo == null ? 0 : W, photo == null ? 0 : W);

        private static byte D(byte[] bgra, int x, int y) => bgra[(y * W + x) * 4];

        [Fact]
        public void Soft_edge_of_the_hair_goes_with_the_hair()
        {
            // Column 100 is mask 0.14: the photo's soft hair edge. At the wall's depth it stayed behind as a ghost of the
            // outline (IMGP0801). With the photo it takes the hair's depth; without it, as v0.35.1, it stays near the wall.
            var with = Run(SoftMask(), Photo(20, 120));
            var without = Run(SoftMask(), null);
            Assert.True(D(with, 100, 100) > 130, $"soft edge at column 100 stayed at {D(with, 100, 100)}, not the hair's 153");
            Assert.True(D(without, 100, 100) < 100, $"without the photo column 100 became {D(without, 100, 100)}, not v0.35.1's");
            Assert.True(D(with, 90, 100) < 40, $"wall clear of the edge became {D(with, 90, 100)}");
        }

        [Fact]
        public void Edge_rises_in_depth_without_a_dip()
        {
            // Across the edge the depth must not dip: a pixel there far behind the hair on both sides was covered and
            // uncovered every frame, and the whole outline shimmered (v0.35.4 trial, 2026-11-12; dips of 100+ levels).
            // A few levels of rounding inside the hair are v0.35.1's own blend and do not shimmer.
            var r = Run(SoftMask(), Photo(20, 120));
            for (int y = 20; y < 180; y += 13)
                for (int x = 90; x < 115; x++)
                    Assert.True(D(r, x + 1, y) + 20 >= D(r, x, y), $"depth falls from {D(r, x, y)} to {D(r, x + 1, y)} at column {x + 1}, row {y}");
        }

        [Fact]
        public void Stray_hair_joins_only_beside_hair()
        {
            // Columns 98-99 (mask 0 and 0.04), rows 80-119: a dark strand on the wall, touching the subject's soft edge and
            // within the 4 px reach (beyond it, a strand counts as wall), on a light wall. Beside dark hair it joins the hair.
            // Beside skin it is shadow: nearer the skin's colour than the light wall's, it joined the arm before the
            // "only beside hair" rule (IMGP0801, a towel beyond the arm), and must stay with the wall.
            byte Strand(int x, int y, byte subject) => x >= 100 ? subject : x >= 98 && y >= 80 && y < 120 ? (byte)60 : (byte)230;
            var hair = Run(SoftMask(), Photo(20, 230, (x, y) => Strand(x, y, 20)));
            var skin = Run(SoftMask(), Photo(150, 230, (x, y) => Strand(x, y, 150)));
            // Column 99 takes the hair's depth; column 98, the joined strand's softened outer edge, most of the way to it.
            Assert.True(D(hair, 99, 100) > 130, $"strand beside hair stayed at {D(hair, 99, 100)}");
            Assert.True(D(hair, 98, 100) > 80, $"strand's outer edge beside hair stayed at {D(hair, 98, 100)}");
            Assert.True(D(skin, 98, 100) < 40, $"shadow beside skin joined at {D(skin, 98, 100)}");
        }

        [Fact]
        public void Small_enclosed_hole_keeps_the_old_depth()
        {
            // A 6 x 6 hole of wall inside the subject (cushion between a hand and a tummy, IMGP0997): made crisp, the shader
            // filled it from the hand in pink specks. It keeps the v0.35.1 depth.
            var alpha = SoftMask();
            for (int y = 97; y < 103; y++)
                for (int x = 147; x < 153; x++)
                    alpha[y * W + x] = 0;
            var grey = HalfGrey();
            for (int y = 48; y < 52; y++)
                for (int x = 73; x < 77; x++)
                    grey[y * G + x] = 20;
            var with = Run(alpha, Photo(20, 120), grey);
            var without = Run(alpha, null, grey);
            for (int y = 94; y < 106; y++)
                for (int x = 144; x < 156; x++)
                    Assert.True(D(with, x, y) == D(without, x, y), $"hole at {x},{y} changed from {D(without, x, y)} to {D(with, x, y)}");
        }
    }
}

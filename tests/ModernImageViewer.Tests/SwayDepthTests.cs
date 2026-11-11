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
        public void Faint_mask_beside_the_outline_goes_with_the_subject()
        {
            // Stray hair: the mask is faint (60 of 255) in columns 13 and 14 (within the rim), and the depth map has it at the wall's depth.
            // Left with the wall it was mirrored into the gap as a dark smear (IMGP0801, 2026-11-11).
            var alpha = Mask();
            for (int y = 0; y < N; y++)
                for (int x = 13; x < 15; x++)
                    alpha[y * N + x] = 60;
            var grey = new byte[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    grey[y * N + x] = x < 15 ? (byte)26 : (byte)153;
            var result = SwayDepth.Crisp(grey, N, N, alpha, N, N, N);
            Assert.True(At(result, 13, 30) > 128, $"faint mask at column 13 stayed at {At(result, 13, 30)}, not the subject's 153");
            Assert.True(At(result, 6, 30) < 40, $"background clear of the mask became {At(result, 6, 30)}");
        }

        [Fact]
        public void Halo_just_outside_the_outline_goes_with_the_subject()
        {
            // Sharpening leaves a 2-3 px halo outside an edge where the mask is 0. Left at the wall's depth it stayed behind
            // as a pale line beside ed.jpg's arm (2026-11-11). Result 600 px across, so the 0.4% growth is about 2 px.
            const int T = 600;
            var grey = new byte[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    grey[y * N + x] = x < 15 ? (byte)26 : (byte)153;
            // A sharp mask at the result's size: subject from column 150. Column 148 is outside it entirely.
            var alpha = new byte[T * T];
            for (int y = 0; y < T; y++)
                for (int x = 150; x < T; x++)
                    alpha[y * T + x] = 255;
            var result = SwayDepth.Crisp(grey, N, N, alpha, T, T, T);
            byte Big(int x, int y) => result[(y * T + x) * 4];
            Assert.True(Big(148, 300) > 128, $"halo 2 px outside the outline stayed at {Big(148, 300)}");
            Assert.True(Big(130, 300) < 40, $"background 20 px outside became {Big(130, 300)}");
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
    }
}

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
    }
}

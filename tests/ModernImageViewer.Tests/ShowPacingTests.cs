using System.Numerics;
using ModernImageViewer.Cinematic;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class ShowPacingTests
    {
        [Fact]
        public void Durations_stay_in_range_and_alternate_short_and_long()
        {
            var pacing = new ShowPacing();
            var rnd = new Random(7);
            bool? lastLong = null;
            for (int i = 0; i < 2000; i++)
            {
                double d = pacing.NextDuration(rnd);
                Assert.InRange(d, ShowPacing.ShortestSeconds, ShowPacing.LongestSeconds);
                bool isLong = d >= 12.5;
                if (lastLong != null) Assert.NotEqual(lastLong, isLong);
                lastLong = isLong;
            }
        }

        [Theory]
        [InlineData(1f, 1.2f, 0f, 0f, "in")]
        [InlineData(1.2f, 1f, 0f, 0f, "out")]
        [InlineData(1.1f, 1.1f, 100f, 0f, "left")]
        [InlineData(1.1f, 1.1f, -100f, 0f, "right")]
        [InlineData(1.1f, 1.1f, 0f, 100f, "up")]
        [InlineData(1.1f, 1.1f, 0f, -100f, "down")]
        [InlineData(1.1f, 1.1f, 1f, 1f, "hold")]
        public void Moves_are_named_as_the_viewer_sees_them(float start, float end, float dx, float dy, string expected)
        {
            Assert.Equal(expected, CameraFraming.MoveName(start, end, Vector2.Zero, new Vector2(dx, dy)));
        }

        [Fact]
        public void A_move_counts_as_a_repeat_only_straight_after_itself()
        {
            var pacing = new ShowPacing();
            Assert.False(pacing.IsRepeat("in"));
            pacing.Remember("in");
            Assert.True(pacing.IsRepeat("in"));
            Assert.False(pacing.IsRepeat("out"));
        }

        /// <summary>The same maths as the draw: scale, pan, then rotate about the screen centre.</summary>
        private static bool EveryScreenCornerIsOnThePicture(float scale, Vector2 pan, float rotation, float sw, float sh, float iw, float ih)
        {
            var toScreen = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(pan) * Matrix3x2.CreateRotation(rotation, new Vector2(sw / 2f, sh / 2f));
            Assert.True(Matrix3x2.Invert(toScreen, out var toPicture));
            foreach (var corner in new[] { new Vector2(0, 0), new Vector2(sw, 0), new Vector2(0, sh), new Vector2(sw, sh) })
            {
                var p = Vector2.Transform(corner, toPicture);
                if (p.X < -0.5f || p.Y < -0.5f || p.X > iw + 0.5f || p.Y > ih + 0.5f) return false;
            }
            return true;
        }

        [Theory]
        [InlineData(1920f, 1080f, 3840f, 2160f)]
        [InlineData(1920f, 1080f, 3840f, 1600f)]
        [InlineData(1380f, 800f, 4096f, 1024f)]
        [InlineData(1920f, 1080f, 2000f, 3000f)]
        [InlineData(800f, 1200f, 4000f, 3000f)]
        public void No_edge_shows_whatever_the_pan_and_tilt_ask_for(float sw, float sh, float iw, float ih)
        {
            var rnd = new Random(11);
            float fill = Math.Max(sw / iw, sh / ih);
            for (int i = 0; i < 3000; i++)
            {
                float scale = fill * (1f + (float)rnd.NextDouble() * 0.4f);
                var askedPan = new Vector2((float)(rnd.NextDouble() * 6000 - 4000), (float)(rnd.NextDouble() * 6000 - 4000));
                float askedTilt = (float)(rnd.NextDouble() - 0.5) * 0.1f;
                var (pan, tilt) = CameraFraming.KeepCovered(scale, askedPan, askedTilt, sw, sh, iw, ih);
                Assert.True(EveryScreenCornerIsOnThePicture(scale, pan, tilt, sw, sh, iw, ih), $"scale {scale} pan {pan} tilt {tilt}");
            }
        }

        [Fact]
        public void Tilt_fades_in_smoothly_as_the_zoom_makes_room()
        {
            float last = 0f;
            for (float zoom = 1f; zoom < 1.2f; zoom += 0.001f)
            {
                var (_, tilt) = CameraFraming.KeepCovered(zoom * 0.5f, Vector2.Zero, 0.014f, 1920, 1080, 3840, 2160);
                Assert.True(tilt - last < 0.002f, $"tilt jumped from {last} to {tilt} at zoom {zoom}");
                Assert.True(tilt >= last - 0.0001f);
                last = tilt;
            }
            Assert.Equal(0.014f, last, 5);
        }

        [Fact]
        public void A_picture_too_small_for_the_screen_is_centred_and_not_tilted()
        {
            var (pan, tilt) = CameraFraming.KeepCovered(1f, new Vector2(500, 500), 0.01f, 1920, 1080, 800, 600);
            Assert.Equal(0f, tilt);
            Assert.Equal(new Vector2(560, 240), pan);
        }
    }
}

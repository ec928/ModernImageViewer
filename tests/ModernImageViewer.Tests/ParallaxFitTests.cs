using ModernImageViewer;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class ParallaxFitTests
    {
        [Fact]
        public void A_horizon_passes_and_a_flat_picture_is_excluded()
        {
            // Sea at the bottom, sky above: the eclipse layout, which the warp played well.
            var horizon = new float[20 * 20];
            for (int i = 0; i < horizon.Length; i++) horizon[i] = i / 20 >= 15 ? 1f : 0f;
            Assert.Equal(ParallaxFit.Verdict.Pass, ParallaxFit.Judge(horizon, 20, 20, 3.79f));

            var flat = new float[20 * 20];
            for (int i = 0; i < flat.Length; i++) flat[i] = 0.5f;
            Assert.Equal(ParallaxFit.Verdict.Exclude, ParallaxFit.Judge(flat, 20, 20, 0.4f));
        }

        [Fact]
        public void A_short_span_on_an_otherwise_good_picture_is_unsure_and_left_out_of_pass()
        {
            var horizon = new float[20 * 20];
            for (int i = 0; i < horizon.Length; i++) horizon[i] = i / 20 >= 15 ? 1f : 0f;
            Assert.Equal(ParallaxFit.Verdict.Unsure, ParallaxFit.Judge(horizon, 20, 20, 3.2f));
        }

        [Fact]
        public void A_picture_that_is_mostly_foreground_is_excluded()
        {
            var filled = new float[20 * 20];
            for (int i = 0; i < filled.Length; i++) filled[i] = i / 20 >= 2 ? 1f : 0f;
            Assert.Equal(ParallaxFit.Verdict.Exclude, ParallaxFit.Judge(filled, 20, 20, 5f));
        }

        [Fact]
        public void A_subject_sitting_on_a_near_surface_is_excluded()
        {
            // 40x40. Rows 0-17 are far. Rows 18-39 are a thick floor at 0.55, under the near band, so the step
            // leaves the subject and stays on the floor. A floor as near as the subject joins the near region.
            var sitting = Floor(0.55f);
            Assert.Equal(ParallaxFit.Verdict.Exclude, ParallaxFit.Judge(sitting, 40, 40, 5f));

            // The same subject with far ground right beside it still passes.
            var onFar = Floor(0f);
            Assert.Equal(ParallaxFit.Verdict.Pass, ParallaxFit.Judge(onFar, 40, 40, 5f));

            // A horizon, large enough that the backdrop step is actually measured, still passes.
            var horizon = new float[40 * 40];
            for (int i = 0; i < horizon.Length; i++) horizon[i] = i / 40 >= 30 ? 1f : 0f;
            Assert.Equal(ParallaxFit.Verdict.Pass, ParallaxFit.Judge(horizon, 40, 40, 3.79f));
        }

        private static float[] Floor(float floor)
        {
            var map = new float[40 * 40];
            for (int row = 0; row < 40; row++)
                for (int col = 0; col < 40; col++)
                {
                    bool subject = row >= 26 && row <= 37 && col >= 12 && col <= 27;
                    map[row * 40 + col] = subject ? 1f : row >= 18 ? floor : 0f;
                }
            return map;
        }

        [Fact]
        public void The_Parallax_preset_is_Cinematic_with_the_filter_and_full_depth()
        {
            var preset = CinematicOverrides.ParallaxDefaults();
            Assert.Equal(1, preset.FewestPictures);
            Assert.Equal(1, preset.MostPictures);
            Assert.Equal(1, preset.DepthChance);
            Assert.True(preset.ParallaxOnly);
            Assert.Equal(0, preset.SequenceBeatsChance);
            Assert.Equal(0, preset.OddsFor(LayoutComposer.HeroSpotlight));
            Assert.Equal(SurpriseOptions.CameraDrift, preset.PlayCameraMode);
            Assert.Equal(0, preset.StillChance);
            Assert.Equal(0, preset.PathBendMost);
            Assert.Equal(SurpriseOptions.MostFavouredDesign, preset.OddsFor(LayoutComposer.EdgeToEdge));
            Assert.Equal(SurpriseOptions.DepthSlide, preset.DepthMotion);
            Assert.Equal(0f, SurpriseOptions.DepthPivot(preset.DepthMotion, 0.5f));
            Assert.Equal(0.5f, SurpriseOptions.DepthPivot(SurpriseOptions.DepthSway, 0.5f));
            Assert.Equal(SurpriseOptions.DepthSlide, SurpriseOptions.CanonicalDepthMotion(null));
            Assert.Equal(SurpriseOptions.DepthSlide, SurpriseOptions.CanonicalDepthMotion("nope"));

            var show = new ShowProject();
            show.GlobalDefaults.TakeValues(preset);
            var used = show.GlobalDefaults.ApplyTo(new SurpriseOptions
            {
                DepthChance = 0.8,
                CameraMode = SurpriseOptions.CameraZoomIn,
                StillChance = 0.5,
                PathBendMost = 0.5,
                SequenceBeatsChance = 1
            });
            Assert.Equal(1, used.DepthChance, 3);
            Assert.True(used.ParallaxOnly);
            Assert.Equal(1, used.MostPictures);
            Assert.Equal(SurpriseOptions.CameraDrift, used.CameraMode);
            Assert.Equal(0, used.StillChance);
            Assert.Equal(0, used.PathBendMost);
            Assert.Equal(0, used.SequenceBeatsChance);
            Assert.True(used.IsDesignOff(LayoutComposer.HeroSpotlight));

            // Cinematic does not take the filter, and it leaves the depth slider and the camera mode alone.
            var cinematic = new ShowProject();
            cinematic.GlobalDefaults.TakeValues(CinematicOverrides.Checked(new CinematicOverrides()));
            var plain = cinematic.GlobalDefaults.ApplyTo(new SurpriseOptions { DepthChance = 0.8, CameraMode = SurpriseOptions.CameraZoomIn });
            Assert.Equal(0.8, plain.DepthChance, 3);
            Assert.False(plain.ParallaxOnly);
            Assert.Equal(SurpriseOptions.CameraZoomIn, plain.CameraMode);
            Assert.Equal(0.25, plain.SequenceBeatsChance);
        }

        [Fact]
        public void A_settings_file_without_the_preset_gets_the_factory_one()
        {
            var read = SurpriseOptions.FromJson("{}");
            Assert.True(read.Parallax.ParallaxOnly);
            Assert.Equal(1, read.Parallax.DepthChance);
            Assert.Equal(SurpriseOptions.CameraDrift, read.Parallax.PlayCameraMode);
            Assert.Equal(SurpriseOptions.DepthSlide, read.Parallax.DepthMotion);
            // A preset saved without DepthMotion defaults to Slide. Rock and Sway are kept when named.
            var saved = SurpriseOptions.FromJson("{\"Parallax\":{\"DepthChance\":1,\"PlayCameraMode\":\"Drift\",\"ParallaxOnly\":true}}");
            Assert.Equal(SurpriseOptions.DepthSlide, saved.Parallax.DepthMotion);
            var rock = SurpriseOptions.FromJson("{\"Parallax\":{\"DepthChance\":1,\"PlayCameraMode\":\"Drift\",\"DepthMotion\":\"Rock\"}}");
            Assert.Equal(SurpriseOptions.DepthSway, rock.Parallax.DepthMotion);
            var sway = SurpriseOptions.FromJson("{\"Parallax\":{\"DepthChance\":1,\"PlayCameraMode\":\"Drift\",\"DepthMotion\":\"Sway\"}}");
            Assert.Equal(SurpriseOptions.DepthSway, sway.Parallax.DepthMotion);
            var slide = SurpriseOptions.FromJson("{\"Parallax\":{\"DepthChance\":1,\"PlayCameraMode\":\"Drift\",\"DepthMotion\":\"Slide\"}}");
            Assert.Equal(SurpriseOptions.DepthSlide, slide.Parallax.DepthMotion);
            var show = new ShowProject();
            show.GlobalDefaults.TakeValues(slide.Parallax);
            Assert.Equal(SurpriseOptions.DepthSlide, show.GlobalDefaults.ApplyTo(new SurpriseOptions()).DepthMotion);
            Assert.False(read.Cinematic.ParallaxOnly);
            Assert.Null(read.Cinematic.DepthChance);
            Assert.Null(read.Cinematic.PlayCameraMode);

            // Off unless a show sets it. 0.8 was the old default, and a file from before this preset still has it.
            Assert.Equal(0, new SurpriseOptions().DepthChance);
            Assert.Equal(0, SurpriseOptions.FromJson("{\"DepthChance\":0.8}").DepthChance);
            Assert.Equal(0.4, SurpriseOptions.FromJson("{\"DepthChance\":0.4}").DepthChance);
            Assert.Equal(0.8, SurpriseOptions.FromJson("{\"DepthChance\":0.8,\"Parallax\":{\"DepthChance\":1}}").DepthChance);
        }

        [Fact]
        public void Sway_settings_keep_the_current_look_when_the_file_omits_them()
        {
            var fresh = new SurpriseOptions();
            Assert.Equal(SurpriseOptions.SwayRotationDefault, fresh.SwayRotation, 5);
            Assert.Equal(SurpriseOptions.SwaySwingDefault, fresh.SwaySwing, 3);

            var old = SurpriseOptions.FromJson("{\"DepthChance\":0.4}");
            Assert.Equal(0.016, old.SwayRotation, 5);
            Assert.Equal(4.25, old.SwaySwing, 3);

            var set = SurpriseOptions.FromJson("{\"SwayRotation\":0.03,\"SwaySwing\":6.5}");
            Assert.Equal(0.03, set.SwayRotation, 5);
            Assert.Equal(6.5, set.SwaySwing, 3);
            var round = SurpriseOptions.FromJson(set.ToJson());
            Assert.Equal(0.03, round.SwayRotation, 5);
            Assert.Equal(6.5, round.SwaySwing, 3);

            var loud = SurpriseOptions.FromJson("{\"SwayRotation\":9,\"SwaySwing\":0}");
            Assert.Equal(SurpriseOptions.SwayRotationMost, loud.SwayRotation, 5);
            Assert.Equal(SurpriseOptions.SwaySwingLeast, loud.SwaySwing, 3);
        }

        [Fact]
        public void Changing_the_swing_keeps_the_pose_and_changes_the_speed()
        {
            double clock = -1;
            double phase = SurpriseOptions.NextSwayPhase(0, 0, ref clock, SurpriseOptions.SwaySwingDefault);
            Assert.Equal(0, phase, 5);

            // A quarter of the 4.25 s swing, in steps shorter than the stall cap.
            for (int i = 1; i <= 17; i++)
                phase = SurpriseOptions.NextSwayPhase(phase, i * 0.0625, ref clock, 4.25);
            Assert.Equal(0.25, phase, 5);

            double held = SurpriseOptions.NextSwayPhase(phase, 17 * 0.0625, ref clock, 12);
            Assert.Equal(0.25, held, 5);

            double later = SurpriseOptions.NextSwayPhase(held, 17 * 0.0625 + 0.05, ref clock, 12);
            Assert.Equal(0.25 + 0.05 / 12, later, 5);

            double stalled = SurpriseOptions.NextSwayPhase(later, 17 * 0.0625 + 0.05 + 2, ref clock, 4.25);
            Assert.Equal(later + 0.1 / 4.25, stalled, 5);
        }

        [Fact]
        public void Sway_rotation_follows_the_local_bend_and_stays_inside_the_slider()
        {
            const int width = 100;
            const int height = 40;
            int gap = Math.Max(2, (int)Math.Round(width * SurpriseOptions.SwayDepthWindow));
            Assert.Equal(8, gap);
            Assert.Equal(0.40, SurpriseOptions.SwayFullStep, 6);

            // Contrast 64 across every 8% step. 0.04 * (64/255) / 0.40 = 0.025098, under a 4% ceiling.
            var steep = Stripes(width, height, gap, 0, 64);
            double stepped = SurpriseOptions.SwayRotationForDepth(steep, width, height, SurpriseOptions.SwayRotationMost);
            Assert.Equal(0.025098, stepped, 6);
            Assert.True(stepped < SurpriseOptions.SwayRotationMost);
            Assert.True(stepped > SurpriseOptions.SwayRotationLeast);
            Assert.Equal(0.01, SurpriseOptions.SwayRotationForDepth(steep, width, height, 0.01), 6);

            // Almost no local step. Held at the old floor.
            var split = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = width / 2; x < width; x++)
                    split[y * width + x] = 255;
            Assert.Equal(SurpriseOptions.SwayRotationLeast, SurpriseOptions.SwayRotationForDepth(split, width, height, 0.04), 6);

            // A smooth ramp steps less than the stripes, so it is allowed less sway.
            var ramp = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    ramp[y * width + x] = (byte)(x * 255 / (width - 1));
            double ramped = SurpriseOptions.SwayRotationForDepth(ramp, width, height, 0.04);
            Assert.True(ramped < stepped);
            Assert.True(ramped > SurpriseOptions.SwayRotationLeast);

            // Depth rises by 2 per pixel. The wider span is that same slope, so nothing is taken off.
            // 0.04 * (16/255) / 0.40 = 0.006275.
            var grade = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    grade[y * width + x] = (byte)(x * 2);
            Assert.Equal(0.006275, SurpriseOptions.SwayRotationForDepth(grade, width, height, 0.04), 6);

            // A tooth every 20 pixels, rising 6 per pixel. The wider span is 90 against a short step of 72.
            // 0.04 * (72/255) / 0.40 - (18/255) * 0.02 = 0.026824.
            var tooth = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    tooth[y * width + x] = (byte)(6 * (x % 20));
            Assert.Equal(0.026824, SurpriseOptions.SwayRotationForDepth(tooth, width, height, 0.04), 6);

            var hard = Stripes(width, height, gap, 0, 255);
            Assert.Equal(SurpriseOptions.SwayRotationMost, SurpriseOptions.SwayRotationForDepth(hard, width, height, 0.04), 6);
            Assert.Equal(SurpriseOptions.SwayRotationLeast, SurpriseOptions.SwayRotationForDepth(split, width, height, 9), 6);
            Assert.Equal(0.02, SurpriseOptions.SwayRotationForDepth(ReadOnlySpan<byte>.Empty, 10, 10, 0.02), 6);
        }

        [Fact]
        public void Sway_cannot_be_raised_by_a_small_gap_and_cannot_vibrate()
        {
            const int width = 100;
            const int height = 40;
            int step = Math.Max(2, (int)Math.Round(width * SurpriseOptions.SwayDepthWindow));
            var mild = Stripes(width, height, step, 0, 64);
            // A small gap on its own is near 4%. The mild step keeps the picture at 2.5%.
            Assert.Equal(0.025098, SurpriseOptions.SwayRotationForPicture(mild, width, height, 0.22, 0.04), 6);

            var hard = Stripes(width, height, step, 0, 255);
            // A large gap on its own is 0.4%. That only vibrates, so the picture is lifted to 1.6%.
            Assert.Equal(SurpriseOptions.SwayRotationFloor, SurpriseOptions.SwayRotationForPicture(hard, width, height, 0.89, 0.04), 6);

            // Gap 0.49 is 2.1%, above the floor. A full step does not replace it.
            double mid = SurpriseOptions.SwayRotationForPicture(hard, width, height, 0.49, 0.04);
            Assert.Equal(SurpriseOptions.SwayRotationForBehind(0.49, 0.04), mid, 6);
            Assert.Equal(0.021018, mid, 6);

            // No gap reading leaves the local step alone.
            Assert.Equal(SurpriseOptions.SwayRotationMost, SurpriseOptions.SwayRotationForPicture(hard, width, height, double.NaN, 0.04), 6);
        }

        private static byte[] Stripes(int width, int height, int stripe, byte low, byte high)
        {
            var grey = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    grey[y * width + x] = (x / stripe) % 2 == 0 ? low : high;
            return grey;
        }

        [Fact]
        public void Slide_needs_one_subject_on_a_clean_background()
        {
            const int width = 40;
            const int height = 40;
            var foreground = new bool[width * height];
            var depth = new byte[width * height];
            for (int y = 8; y < 32; y++)
                for (int x = 10; x < 30; x++)
                {
                    foreground[y * width + x] = true;
                    depth[y * width + x] = 255;
                }
            Assert.True(SurpriseOptions.SlideIsOneCleanCut(foreground, depth, width, height));

            // A speck under 2% of the frame is not a second subject.
            var speck = (bool[])foreground.Clone();
            for (int i = 0; i < 10; i++) speck[i] = true;
            Assert.True(SurpriseOptions.SlideIsOneCleanCut(speck, depth, width, height));

            // A second block of 72 pixels is 4.5% of the frame.
            var two = (bool[])foreground.Clone();
            for (int y = 8; y < 20; y++)
                for (int x = 32; x < 38; x++)
                    two[y * width + x] = true;
            Assert.False(SurpriseOptions.SlideIsOneCleanCut(two, depth, width, height));

            // The background still runs from near to far.
            var wide = (byte[])depth.Clone();
            for (int i = 0; i < wide.Length; i++)
                if (!foreground[i]) wide[i] = (byte)((i % width) * 255 / (width - 1));
            Assert.False(SurpriseOptions.SlideIsOneCleanCut(foreground, wide, width, height));

            // One piece, but only the outline of its box.
            var frame = new bool[width * height];
            var frameDepth = new byte[width * height];
            for (int x = 5; x <= 34; x++)
            {
                frame[5 * width + x] = true;
                frame[34 * width + x] = true;
                frameDepth[5 * width + x] = 200;
                frameDepth[34 * width + x] = 200;
            }
            for (int y = 6; y <= 33; y++)
            {
                frame[y * width + 5] = true;
                frame[y * width + 34] = true;
                frameDepth[y * width + 5] = 200;
                frameDepth[y * width + 34] = 200;
            }
            Assert.False(SurpriseOptions.SlideIsOneCleanCut(frame, frameDepth, width, height));
        }

        [Fact]
        public void Preset_names_and_3D_mode_naming_are_consistent()
        {
            Assert.Equal("3D", CinematicOverrides.ParallaxName);
            Assert.Equal("Parallax", CinematicOverrides.LegacyParallaxName);
            Assert.Equal("Cinematic", CinematicOverrides.Name);

            var p = CinematicOverrides.ParallaxDefaults();
            Assert.True(p.ParallaxOnly);
            Assert.Equal(1.0, p.DepthChance);

            var c = new CinematicOverrides();
            Assert.False(c.ParallaxOnly);
        }
    }
}

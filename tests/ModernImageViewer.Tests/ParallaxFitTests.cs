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

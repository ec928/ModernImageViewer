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
            Assert.Equal(0.06, SurpriseOptions.SwayRotationDefault, 5);
            Assert.Equal(0.10, SurpriseOptions.SwayRotationMost, 5);

            var fresh = new SurpriseOptions();
            Assert.Equal(SurpriseOptions.SwayRotationDefault, fresh.SwayRotation, 5);
            Assert.Equal(SurpriseOptions.SwaySwingDefault, fresh.SwaySwing, 3);

            var old = SurpriseOptions.FromJson("{\"DepthChance\":0.4}");
            Assert.Equal(0.06, old.SwayRotation, 5);
            Assert.Equal(4.25, old.SwaySwing, 3);

            // Pre-v1 files still holding the old 4% default upgrade once to 6%; custom values stay as chosen.
            Assert.Equal(0.06, SurpriseOptions.FromJson("{\"SwayRotation\":0.04}").SwayRotation, 5);
            Assert.Equal(0.04, SurpriseOptions.FromJson(new SurpriseOptions { SwayRotation = 0.04 }.ToJson()).SwayRotation, 5);
            Assert.Equal(0.095, SurpriseOptions.FromJson("{\"SwayRotation\":0.095}").SwayRotation, 5);

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

        [Fact]
        public void Sway_surface_and_pointer_math_match_expected_bounds()
        {
            Assert.Equal((3072, 2048), ViewerMath.SwaySurfaceSize(6000, 4000));
            Assert.Equal((1536, 1024), ViewerMath.SwaySurfaceSize(1536, 1024));
            Assert.Equal((1728, 3072), ViewerMath.SwaySurfaceSize(2160, 3840));
            Assert.Equal((1, 1), ViewerMath.SwaySurfaceSize(0, -5));

            // Fitted image centered inside 1200px viewport (displayed width 800 -> x in [200, 1000])
            Assert.Equal(-1f, ViewerMath.CalculateSwayPointerAngle(100, 1200, 800, 0), 4);
            Assert.Equal(-1f, ViewerMath.CalculateSwayPointerAngle(200, 1200, 800, 0), 4);
            Assert.Equal(0f, ViewerMath.CalculateSwayPointerAngle(600, 1200, 800, 0), 4);
            Assert.Equal(1f, ViewerMath.CalculateSwayPointerAngle(1000, 1200, 800, 0), 4);
            Assert.Equal(1f, ViewerMath.CalculateSwayPointerAngle(1150, 1200, 800, 0), 4);

            // Zoomed wider than viewport -> mapped across full viewport [0, 1200]
            Assert.Equal(-1f, ViewerMath.CalculateSwayPointerAngle(0, 1200, 2400, -300), 4);
            Assert.Equal(0f, ViewerMath.CalculateSwayPointerAngle(600, 1200, 2400, -300), 4);
            Assert.Equal(1f, ViewerMath.CalculateSwayPointerAngle(1200, 1200, 2400, -300), 4);

            // Smoothing converges monotonically, snaps inside threshold, and clamps large stalls
            float s1 = ViewerMath.SmoothSwayAngle(0f, 1f, 0.016);
            float sStall = ViewerMath.SmoothSwayAngle(0f, 1f, 5.0);
            float sClamped = ViewerMath.SmoothSwayAngle(0f, 1f, 0.1);
            Assert.InRange(s1, 0.05f, 0.5f);
            Assert.Equal(sClamped, sStall, 5);
            Assert.Equal(0.5f, ViewerMath.SmoothSwayAngle(0.4998f, 0.5f, 0.016), 5);

            // Phase round-trips smoothly through sin(phase * 2pi)
            foreach (float targetAngle in new[] { -1f, -0.5f, 0f, 0.5f, 1f })
            {
                double phase = ViewerMath.SwayPhaseFromAngle(targetAngle);
                Assert.Equal(targetAngle, Math.Sin(phase * Math.PI * 2.0), 5);
            }
        }

        [Fact]
        public void View_transform_and_2D_pointer_mapping_are_exact_inverses()
        {
            // 2-axis tilt vector on fitted image (1200x800 view, 800x600 image -> X in [200,1000], Y in [100,700])
            var (vxCenter, vyCenter) = ViewerMath.CalculateSwayPointerVector(600, 400, 1200, 800, 800, 600);
            Assert.Equal(0f, vxCenter, 4);
            Assert.Equal(0f, vyCenter, 4);
            var (vxTL, vyTL) = ViewerMath.CalculateSwayPointerVector(200, 100, 1200, 800, 800, 600);
            Assert.Equal(-1f, vxTL, 4);
            Assert.Equal(-1f, vyTL, 4);

            // Overscan scales smoothly above 6% and adds breathing margin
            Assert.Equal(1.025f, ViewerMath.SwayOverscanFor(0.06, false), 4);
            Assert.True(ViewerMath.SwayOverscanFor(0.10, false) > 1.045f);
            Assert.True(ViewerMath.SwayOverscanFor(0.06, true) > ViewerMath.SwayOverscanFor(0.06, false));

            // Test every combination of rotation (0..3), mirrorH, flipV
            float bw = 400f, bh = 300f;
            foreach (int q in new[] { 0, 1, 2, 3 })
                foreach (bool mh in new[] { false, true })
                    foreach (bool fv in new[] { false, true })
                    {
                        float dispW = (q % 2 == 1) ? bh : bw;
                        float dispH = (q % 2 == 1) ? bw : bh;
                        var m = ViewerMath.BuildViewTransformMatrix(bw, bh, dispW, dispH, q, mh, fv);

                        foreach (var (u, v) in new[] { (0.2f, 0.3f), (0.8f, 0.25f), (0.15f, 0.85f) })
                        {
                            var drawn = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(u * bw, v * bh), m);
                            double viewW = dispW + 200.0;
                            double viewH = dispH + 100.0;
                            double ptrX = 100.0 + drawn.X;
                            double ptrY = 50.0 + drawn.Y;

                            var (inside, ru, rv) = ViewerMath.ScreenToSourceNormalized(
                                ptrX, ptrY, viewW, viewH, dispW, dispH, 0, 0, q, mh, fv);
                            Assert.True(inside);
                            Assert.Equal(u, ru, 3);
                            Assert.Equal(v, rv, 3);
                        }
                    }

            // Letterbox click reports inside=false
            var (outClick, _, _) = ViewerMath.ScreenToSourceNormalized(20, 400, 1200, 800, 800, 600, 0, 0, 0, false, false);
            Assert.False(outClick);

            // Zoomed + panned point maps accurately back to source
            var (inPan, pu, pv) = ViewerMath.ScreenToSourceNormalized(600, 400, 1200, 800, 2400, 1600, -600, -400, 0, false, false);
            Assert.True(inPan);
            Assert.Equal(0.5f, pu, 4);
            Assert.Equal(0.5f, pv, 4);
        }

        [Fact]
        public void PictureDeck_MarkDealt_skips_pinned_opening_photo_until_next_round()
        {
            var pictures = new[] { "c.jpg", "a.jpg", "b.jpg" };
            var deck = new PictureDeck(pictures, new Random(42));
            deck.MarkDealt("c.jpg");

            var remaining = deck.Deal(2);
            Assert.Equal(2, remaining.Count);
            Assert.DoesNotContain("c.jpg", remaining);
            Assert.Contains("a.jpg", remaining);
            Assert.Contains("b.jpg", remaining);

            var nextRound = deck.Deal(1);
            Assert.Equal(new[] { "c.jpg" }, nextRound);
        }

        [Fact]
        public void Slide_one_way_curve_and_loop_wrap_are_continuous()
        {
            // Non-looping once mode eases 0 -> 1 monotonically with no second pass
            Assert.Equal((0f, 0f, 0f), ViewerMath.SlideOneWayLayers(0.0, loop: false));
            Assert.Equal(0.5f, ViewerMath.SlideOneWayLayers(0.5, loop: false).OffsetA, 4);
            Assert.Equal((1f, 0f, 0f), ViewerMath.SlideOneWayLayers(1.0, loop: false));

            // Looping unidirectional mode holds Pass B off until 0.75, then blends seamlessly
            var start = ViewerMath.SlideOneWayLayers(0.0, loop: true);
            Assert.Equal(0f, start.OffsetA, 4);
            Assert.Equal(0f, start.BlendB, 4);

            var preFade = ViewerMath.SlideOneWayLayers(0.5, loop: true);
            Assert.Equal(0.5f, preFade.OffsetA, 4);
            Assert.Equal(0f, preFade.BlendB, 4);

            var wrapEnd = ViewerMath.SlideOneWayLayers(1.0, loop: true);
            var wrapNext = ViewerMath.SlideOneWayLayers(ViewerMath.SlideCrossfadeOverlap, loop: true);
            Assert.Equal(1.0f, wrapEnd.BlendB, 4);
            Assert.Equal(wrapEnd.OffsetB, wrapNext.OffsetA, 4);
            Assert.Equal(0f, wrapNext.BlendB, 4);

            // Phase advancement wraps cleanly inside [0.25, 1) when looping, clamps at 1 when once
            double p = ViewerMath.NextOneWaySlidePhase(0.25, 2.0, 2.0, loop: true);
            Assert.Equal(0.25, p, 4);
            double midWrap = ViewerMath.NextOneWaySlidePhase(0.75, 1.0, 2.0, loop: true);
            Assert.Equal(0.375, midWrap, 4);
            double clamped = ViewerMath.NextOneWaySlidePhase(0.8, 1.0, 2.0, loop: false);
            Assert.Equal(1.0, clamped, 4);
        }

        [Fact]
        public void SlideLightX_is_continuous_across_loop_wrap_and_ramps_smoothly_when_once()
        {
            // Once mode ramps monotonically from -0.7 to +0.7
            Assert.Equal(-0.7f, ViewerMath.SlideLightX(0.0, loop: false, 0f), 4);
            Assert.Equal(0.0f, ViewerMath.SlideLightX(0.5, loop: false, 0.5f), 4);
            Assert.Equal(0.7f, ViewerMath.SlideLightX(1.0, loop: false, 1f), 4);

            // Loop mode matches exactly at wrap boundary 1.0 -> SlideCrossfadeOverlap (0.25)
            float atEnd = ViewerMath.SlideLightX(1.0, loop: true, 1f);
            float atWrap = ViewerMath.SlideLightX(ViewerMath.SlideCrossfadeOverlap, loop: true, (float)ViewerMath.SlideCrossfadeOverlap);
            Assert.Equal(atWrap, atEnd, 4);

            // Loop mode oscillates smoothly across the cycle rather than staying constant
            float atMidLoop = ViewerMath.SlideLightX(ViewerMath.SlideCrossfadeOverlap + 0.1875, loop: true, 0.4375f);
            float atThreeQuarterLoop = ViewerMath.SlideLightX(ViewerMath.SlideCrossfadeOverlap + 0.5625, loop: true, 0.8125f);
            Assert.True(atMidLoop > 0.65f);
            Assert.True(atThreeQuarterLoop < -0.65f);
        }
    }
}

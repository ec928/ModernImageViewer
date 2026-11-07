using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>Two things ported from Cinematic Slideshow: faces framed by the eyes, and longer transitions as a range.</summary>
    public class FramingAndTransitionTests
    {
        /// <summary>Where the eye line sits in the view, from its top: a third is the camera operator's framing.</summary>
        private static double EyesDown(CameraView view, double eyeY) => (eyeY - (view.Y - 0.5 / view.Zoom)) * view.Zoom;

        [Fact]
        public void A_view_on_the_eyes_puts_them_a_third_of_the_way_down()
        {
            foreach (double zoom in new[] { 1.5, 2.0, 3.0 })
            {
                var view = CameraView.OnEyes(zoom, 0.5, 0.45);
                Assert.Equal(1.0 / 3, EyesDown(view, 0.45), 3);
            }
            // At the top of the picture the view cannot go higher: the eyes sit higher than a third, and the view is covered.
            var top = CameraView.OnEyes(2, 0.5, 0.1);
            Assert.True(EyesDown(top, 0.1) < 1.0 / 3);
            Assert.True(top.Y - 0.5 / top.Zoom >= -1e-9);
        }

        [Fact]
        public void A_face_close_up_is_framed_by_the_eyes_and_keeps_the_whole_face()
        {
            // A large face in the middle of a portrait: the face view of a sequence beat.
            var face = new UnitRect(0.40, 0.30, 0.20, 0.20);
            var photo = new PhotoFacts(@"C:\p\face.jpg", 1200, 1600, new List<UnitRect> { face }, 0.5, 0.5, "#000000");
            var options = new SurpriseOptions { LeastZoom = 1.5, MostZoom = 2.5, ZoomChance = 0 };
            for (int seed = 0; seed < 12; seed++)
            {
                var made = SceneCall.Make(new SceneRequest(new[] { photo }, 1920, 1080, options, 10, seed, Face: face))!;
                var cell = made.Composition!.Cells.Single();
                var (eyes, _) = LayoutComposer.OnFace(cell, face, 1920, 1080);
                var close = made.Move!.Shots[^1].To;
                Assert.Equal(1.0 / 3, EyesDown(close, eyes.Y), 2);
                // The top and bottom of the face are both in the view.
                var top = LayoutComposer.PointForNormalized(cell, face.X + face.W / 2, face.Y, 1920, 1080);
                var bottom = LayoutComposer.PointForNormalized(cell, face.X + face.W / 2, face.Y + face.H, 1920, 1080);
                double viewTop = close.Y - 0.5 / close.Zoom, viewBottom = close.Y + 0.5 / close.Zoom;
                Assert.InRange(top.Y, viewTop - 1e-6, viewBottom);
                Assert.InRange(bottom.Y, viewTop, viewBottom + 1e-6);
            }
        }

        [Fact]
        public void A_focus_point_with_no_face_stays_centred()
        {
            var subject = new List<(double X, double Y)> { (0.5, 0.45) };
            var style = CameraStyle.Plain;
            for (int seed = 0; seed < 20; seed++)
            {
                var move = CameraMove.Plan(subject, 1, 1, 1, 12, null, new Random(seed), CameraKind.ZoomIn, style, 3, subjectsAreEyes: false)!;
                var close = move.Shots[^1].To;
                Assert.Equal(0.45, close.Y, 3);
            }
        }

        [Fact]
        public void A_two_shot_needs_eight_seconds_on_screen_not_counting_the_run_on()
        {
            // Two-shots every time a face allows; a 6 s scene is 8.5 s with its run-on, which used to let one in.
            var options = new SurpriseOptions { TwoShotChance = 1, MotionFaceChance = 1 };
            var kept = new KeptLayout(null, new List<(double X, double Y)> { (0.5, 0.45) });
            int Shots(double hold, int seed) => SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, hold, seed, Keep: kept))!.Move!.Shots.Count;
            Assert.All(Enumerable.Range(0, 20), seed => Assert.Equal(1, Shots(6, seed)));
            Assert.Contains(Enumerable.Range(0, 20), seed => Shots(9, seed) == 2);
        }

        [Fact]
        public void Transition_length_is_picked_in_its_range_and_defaults_to_three_quarters_to_two_and_a_half_seconds()
        {
            var fresh = SurpriseOptions.FromJson("{}");
            Assert.Equal((0.75, 2.5), (fresh.TransitionShortest, fresh.TransitionLongest));
            var random = new Random(1);
            var picks = Enumerable.Range(0, 200).Select(_ => fresh.PickTransitionSeconds(random)).ToList();
            Assert.All(picks, s => Assert.InRange(s, 0.75, 2.5));
            Assert.True(picks.Max() - picks.Min() > 1.4);
            var one = new SurpriseOptions { TransitionShortest = 1.2, TransitionLongest = 1.2 };
            Assert.Equal(1.2, one.PickTransitionSeconds(random));
            // A transition never takes more than half the scene it brings in: a 3 s scene gets at most 1.5 s.
            var slow = new SurpriseOptions { TransitionShortest = 3, TransitionLongest = 4 };
            Assert.All(Enumerable.Range(0, 50), _ => Assert.InRange(slow.PickTransitionSeconds(random, 3), 0, 1.5));
            Assert.InRange(slow.PickTransitionSeconds(random, 20), 3, 4);
            var wild = SurpriseOptions.FromJson("{\"TransitionShortest\":0,\"TransitionLongest\":99}");
            Assert.Equal((SurpriseOptions.QuickestTransition, SurpriseOptions.SlowestTransition), (wild.TransitionShortest, wild.TransitionLongest));
        }
    }
}

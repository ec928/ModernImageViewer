using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>Sequence beats: the director writes closer views as kept scenes (design, the director; check 18).</summary>
    public class SequenceBeatsTests
    {
        private static PhotoFacts Photo(int n, params UnitRect[] faces) =>
            new($@"C:\p\{n}.jpg", 3000, 4000, faces.ToList(), 0.5, 0.5, "#FF804020");

        private static readonly UnitRect Big = new(0.35, 0.2, 0.25, 0.18), Small = new(0.65, 0.55, 0.12, 0.09);

        private static List<ShowScene> Step(IReadOnlyList<PhotoFacts> hand, SurpriseOptions options) =>
            ShowDirector.DraftStepAsync(hand, options, 1920, 1080, null, takeAll: true).Result;

        [Fact]
        public void Off_by_default_a_step_is_one_scene()
        {
            for (int i = 0; i < 20; i++)
                Assert.Single(Step(new[] { Photo(0, Big, Small) }, new SurpriseOptions()));
        }

        [Fact]
        public void A_portrait_becomes_the_picture_then_its_faces_joined_by_cuts()
        {
            var options = new SurpriseOptions { SequenceBeatsChance = 1, FewestScenesPerStep = 4, MostScenesPerStep = 4, FewestPictures = 1, MostPictures = 1 };
            for (int i = 0; i < 20; i++)
            {
                var step = Step(new[] { Photo(0, Big, Small) }, options);
                // The picture, then its two faces, largest first; no third face, so it stops at three of the four.
                Assert.Equal(3, step.Count);
                Assert.All(step.Skip(1), s => Assert.Equal("Cut", s.Transition));
                Assert.All(step, s => Assert.NotEqual("TwoShot", s.CameraOverride!.Kind));
                Assert.All(step, s => Assert.InRange(s.DwellSeconds!.Value, options.RandomFewestSeconds, options.RandomMostSeconds));
                // The same picture throughout: one layout kept.
                Assert.All(step, s => Assert.Equal(step[0].GetPhotoPaths().Single(p => p.Length > 0), s.GetPhotoPaths().Single(p => p.Length > 0)));
            }
        }

        [Fact]
        public void A_group_becomes_the_group_then_one_image_then_its_face()
        {
            var options = new SurpriseOptions { SequenceBeatsChance = 1, FewestScenesPerStep = 3, MostScenesPerStep = 3 };
            var hand = new[] { Photo(0, Big), Photo(1, Big), Photo(2, Big), Photo(3, Big) };
            for (int i = 0; i < 20; i++)
            {
                var step = Step(hand, options);
                Assert.Equal(3, step.Count);
                Assert.True(step[0].GetPhotoPaths().Count(p => p.Length > 0) > 1);
                Assert.Single(step[1].GetPhotoPaths(), p => p.Length > 0);
                Assert.NotEqual("Cut", step[1].Transition);
                Assert.Equal("Cut", step[2].Transition);
            }
        }

        [Fact]
        public void An_image_without_faces_has_no_closer_view()
        {
            var options = new SurpriseOptions { SequenceBeatsChance = 1, FewestScenesPerStep = 4, MostScenesPerStep = 4 };
            Assert.Single(Step(new[] { Photo(0) }, options));
        }

        [Fact]
        public void A_face_view_starts_where_the_last_scene_ended_or_on_the_face_and_never_cuts_the_face()
        {
            var options = new SurpriseOptions { SequenceBeatsChance = 1, FewestScenesPerStep = 2, MostScenesPerStep = 2, FewestPictures = 1, MostPictures = 1 };
            int snaps = 0;
            for (int i = 0; i < 40; i++)
            {
                var step = Step(new[] { Photo(0, Big) }, options);
                Assert.Equal(2, step.Count);
                var before = step[0].CameraOverride!.ToCameraMove(10)!;
                var face = step[1].CameraOverride!.ToCameraMove(10)!;
                var first = face.Shots[0].From;
                var ended = before.Shots[^1].To;
                if (Math.Abs(first.Zoom - ended.Zoom) < 0.01 && Math.Abs(first.X - ended.X) < 0.01 && Math.Abs(first.Y - ended.Y) < 0.01) snaps++;
            }
            Assert.InRange(snaps, 8, 32);   // about half snap from the last frame; the rest open on the face
        }

        [Fact]
        public void A_face_view_of_a_close_up_never_cuts_the_face()
        {
            // The face is 60% of the portrait's height, and the portrait is shown whole at full height, so 60% of the
            // screen: the camera may go to 0.85 / 0.6 = 1.42x at most, under the Zoom range's 1.5x.
            var huge = new UnitRect(0.1, 0.1, 0.8, 0.6);
            var options = new SurpriseOptions { SequenceBeatsChance = 1, FewestScenesPerStep = 2, MostScenesPerStep = 2, FewestPictures = 1, MostPictures = 1 };
            options.DisabledDesigns.UnionWith(LayoutComposer.Recipes.Where(r => r != LayoutComposer.EdgeToEdge));
            for (int i = 0; i < 30; i++)
            {
                var step = Step(new[] { Photo(0, huge) }, options);
                Assert.Equal(2, step.Count);
                var face = step[1].CameraOverride!.ToCameraMove(10)!;
                Assert.All(face.Shots, s => Assert.True(s.To.Zoom <= 0.85 / 0.6 + 0.01, $"zoomed to {s.To.Zoom:0.00}"));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>The one engine call: every caller gets its move from the same settings, and memory is passed in.</summary>
    public class SceneCallTests
    {
        private static PhotoFacts Portrait(int n = 0) =>
            new($@"C:\p\portrait{n}.jpg", 1200, 1600, new List<UnitRect> { new(0.45, 0.25, 0.1, 0.1) }, 0.5, 0.5, "#000000");

        [Fact]
        public void DraftedScenes_MoveByTheCameraSettings_NotTheirOwnOdds()
        {
            // The director used to choose the kind by its own odds (three in four a zoom for one image, one in twenty still).
            var options = new SurpriseOptions { MotionZoomChance = 0.5, TwoShotChance = 0 };
            int zooms = 0, stills = 0, total = 200;
            for (int i = 0; i < total; i++)
            {
                var scene = ShowDirector.DraftSceneAsync(new[] { Portrait() }, options, 1920, 1080, null).Result!;
                string kind = scene.CameraOverride!.Kind!;
                if (kind == "Still") stills++;
                else if (kind is "ZoomIn" or "ZoomOut") zooms++;
            }
            Assert.Equal(0, stills);
            Assert.InRange(zooms / (double)total, 0.38, 0.62);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 30)]
        public void StillScenes_FollowTheirOdds(double chance, int stillsExpected)
        {
            var options = new SurpriseOptions { StillChance = chance };
            var kept = KeptLayout.Of(LayoutComposer.Compose(new[] { Portrait() }, 1920, 1080, 1, options: options)!);
            int stills = Enumerable.Range(0, 30)
                .Count(seed => SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, 12, seed, Keep: kept))!.Move == null);
            Assert.Equal(stillsExpected, stills);

            var drafted = ShowDirector.DraftSceneAsync(new[] { Portrait() }, options, 1920, 1080, null).Result!;
            Assert.Equal(chance >= 1, drafted.CameraOverride!.Kind == "Still");
        }

        [Fact]
        public void AKeptLayoutGivenAsFaces_IsAimedAt()
        {
            // A Dynamic storyboard scene keeps its saved layout and passes where its faces are; it used to pass none.
            var options = new SurpriseOptions { CameraMode = SurpriseOptions.CameraZoomIn, MotionFaceChance = 1, TwoShotChance = 0 };
            var kept = new KeptLayout(null, new List<(double X, double Y)> { (0.52, 0.49) });
            for (int seed = 0; seed < 30; seed++)
            {
                var move = SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, 12, seed, Keep: kept))!.Move!;
                var end = move.Shots[^1].To;
                // Near the middle, so no zoom is clamped at the picture's edge.
                Assert.InRange(end.X, 0.51, 0.53);
                Assert.InRange(end.Y, 0.48, 0.50);
            }
        }

        [Fact]
        public void TheLastMove_IsPassedIn_AndNotRepeated()
        {
            var options = new SurpriseOptions { MotionZoomChance = 0.5, TwoShotChance = 0 };
            var kept = KeptLayout.Of(LayoutComposer.Compose(new[] { Portrait() }, 1920, 1080, 1, options: options)!);
            CameraKind? last = null;
            int repeats = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                var move = SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, 12, seed, LastKind: last, Keep: kept))!.Move!;
                if (move.Kind == last) repeats++;
                last = move.Kind;
            }
            Assert.Equal(0, repeats);
        }
    }
}

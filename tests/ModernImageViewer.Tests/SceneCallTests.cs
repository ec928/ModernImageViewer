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
            // Zoom frequency off, so only Zooms rather than drifts decides; one image rolls Zoom frequency first (design 3.5).
            var options = new SurpriseOptions { MotionZoomChance = 0.5, TwoShotChance = 0, ZoomChance = 0 };
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
        public void PlayFromImages_DraftsTheWholeHand()
        {
            // The hand Play deals is already sized within Images per scene; the director must not roll the count again.
            var options = new SurpriseOptions { FewestPictures = 1, MostPictures = 9 };
            var hand = Enumerable.Range(0, 5).Select(n => new PhotoFacts($@"C:\p\hand{n}.jpg", 1600, 1200, new List<UnitRect>(), 0.5, 0.5, "#000000")).ToList();
            for (int i = 0; i < 30; i++)
            {
                var scene = ShowDirector.DraftSceneAsync(hand, options, 1920, 1080, null, takeAll: true).Result!;
                Assert.Equal(5, scene.GetPhotoPaths().Count(p => !string.IsNullOrEmpty(p)));
            }
        }

        private static CameraMove OneImageMove(PhotoFacts photo, SurpriseOptions options, int seed)
        {
            var made = LayoutComposer.Compose(new[] { photo }, 1920, 1080, seed, options: options)!;
            return SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, 12, seed, Keep: KeptLayout.Of(made)))!.Move!;
        }

        private static SurpriseOptions EdgeToEdgeOnly(SurpriseOptions o)
        {
            foreach (string r in LayoutComposer.Recipes.Where(r => r != LayoutComposer.EdgeToEdge)) o.DisabledDesigns.Add(r);
            return o;
        }

        [Theory]
        [InlineData(1.0, 60)]
        [InlineData(0.0, 0)]
        public void OneImage_ZoomFrequency_IsHowOftenTheMoveReachesTheZoomRange(double frequency, int expected)
        {
            // Design check 15. The Zoom range sits above Camera zoom so the two cannot be mistaken; no faces, plenty of detail.
            var options = EdgeToEdgeOnly(new SurpriseOptions { ZoomChance = frequency, LeastZoom = 2.5, MostZoom = 4, CameraZoomLeast = 1.15, CameraZoomMost = 1.8, TwoShotChance = 0 });
            var photo = new PhotoFacts(@"C:\pig.jpg", 8000, 4500, new List<UnitRect>(), 0.5, 0.5, "#000000");
            int reached = Enumerable.Range(0, 60).Count(seed => OneImageMove(photo, options, seed).Shots.Max(s => Math.Max(s.From.Zoom, s.To.Zoom)) >= 2.5 - 0.001);
            Assert.Equal(expected, reached);
        }

        [Fact]
        public void OneImage_ACloseUp_IsNeverZoomedPastItsFace()
        {
            // Ed, 6 Nov: a snap zoom on a photo that was already a close-up cut off half the face, eyes included.
            var options = EdgeToEdgeOnly(new SurpriseOptions { TwoShotChance = 1, ZoomChance = 1 });
            var closeUp = new PhotoFacts(@"C:\p\closeup.jpg", 4000, 2250, new List<UnitRect> { new(0.3, 0.15, 0.4, 0.6) }, 0.5, 0.5, "#000000");
            for (int seed = 0; seed < 60; seed++)
            {
                var made = LayoutComposer.Compose(new[] { closeUp }, 1920, 1080, seed, options: options)!;
                double most = LayoutComposer.MostCameraZoom(made.Cells.Single(c => c.Photo != null), 1920, 1080);
                var move = SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, 12, seed, Keep: KeptLayout.Of(made)))!.Move!;
                Assert.All(move.Shots, s => Assert.True(Math.Max(s.From.Zoom, s.To.Zoom) <= most + 0.001, $"zoomed to {Math.Max(s.From.Zoom, s.To.Zoom):0.00}, the face allows {most:0.00}"));
            }
        }

        [Fact]
        public void OneImage_WithoutFaces_ZoomsTowardItsFocusPoint()
        {
            // A zoom with nothing to aim at used to head for a random spot near the middle.
            var options = EdgeToEdgeOnly(new SurpriseOptions { ZoomChance = 1, CameraMode = SurpriseOptions.CameraZoomIn, TwoShotChance = 0, LeastZoom = 1.5, MostZoom = 1.6 });
            var photo = new PhotoFacts(@"C:\pocus.jpg", 1920, 1080, new List<UnitRect>(), 0.56, 0.47, "#000000");
            for (int seed = 0; seed < 30; seed++)
            {
                var end = OneImageMove(photo, options, seed).Shots[^1].To;
                Assert.InRange(end.X, 0.55, 0.57);
                Assert.InRange(end.Y, 0.46, 0.48);
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

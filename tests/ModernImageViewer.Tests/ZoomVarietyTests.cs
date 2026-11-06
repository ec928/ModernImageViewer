using System;
using System.Linq;
using ModernImageViewer;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>A single image, portrait or not, with or without a face, varies from scene to scene under the default settings.</summary>
    public class ZoomVarietyTests
    {
        private static PhotoFacts Pic(double w, double h, bool face) =>
            new(@"C:\p\x.jpg", w, h, face ? new[] { new UnitRect(0.42, 0.25, 0.12, 0.09) } : Array.Empty<UnitRect>(), 0.5, 0.45, "#FF806050");

        [Theory]
        [InlineData(3024, 4032, true)]
        [InlineData(3024, 4032, false)]
        [InlineData(4032, 3024, false)]
        public void Cinematic_storyboard_single_images_vary(double w, double h, bool face)
        {
            // One image opens on its fit or fill; Zoom is where its one camera move arrives (design 3.5).
            var options = new SurpriseOptions().WithOverrides(CinematicOverrides.Name);
            var depths = Enumerable.Range(0, 60).Select(seed =>
            {
                var made = LayoutComposer.Compose(new[] { Pic(w, h, face) }, 1920, 1080, seed, options: options)!;
                Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
                var move = SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), 1920, 1080, options, 12, seed, Keep: KeptLayout.Of(made)))!.Move!;
                return move.Shots.Max(s => Math.Max(s.From.Zoom, s.To.Zoom));
            }).ToList();
            int toZoom = depths.Count(d => d >= options.LeastZoom - 0.001);
            Assert.InRange(toZoom, 30, 54);                                         // about 70%, and some not
            Assert.True(depths.Where(d => d >= options.LeastZoom).Select(d => Math.Round(d, 2)).Distinct().Count() >= 8);
            Assert.All(depths, d => Assert.InRange(d, 1, options.MostZoom + 0.001));
        }

        [Fact]
        public void Camera_zoom_follows_its_range()
        {
            var options = new SurpriseOptions { CameraZoomLeast = 1.4, CameraZoomMost = 2.2 };
            var random = new Random(5);
            var depths = Enumerable.Range(0, 200)
                .Select(_ => CameraMove.Plan(Array.Empty<(double, double)>(), 0.5, 1, 0, 12, null, random, null, options.CameraStyle())!)
                .Select(m => m.Shots.Max(s => Math.Max(s.From.Zoom, s.To.Zoom))).ToList();
            Assert.All(depths, d => Assert.InRange(d, 1.4 - 0.001, 2.2 + 0.001));
            Assert.True(depths.Max() - depths.Min() > 0.5);
        }
    }
}

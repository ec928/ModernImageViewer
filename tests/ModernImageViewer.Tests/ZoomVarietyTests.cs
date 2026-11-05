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
            var options = new SurpriseOptions().WithOverrides(CinematicOverrides.Name);
            var zooms = Enumerable.Range(0, 30).Select(seed => LayoutComposer.Compose(new[] { Pic(w, h, face) }, 1920, 1080, seed, options: options)!.Cells[0].Zoom).ToList();
            int zoomed = zooms.Count(z => z > 1.01);
            Assert.InRange(zoomed, 12, 28);                                        // about 70%, and some whole
            Assert.True(zooms.Where(z => z > 1.01).Select(z => Math.Round(z, 2)).Distinct().Count() >= 8);
            Assert.All(zooms, z => Assert.InRange(z, 1, options.MostZoom + 0.001));
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

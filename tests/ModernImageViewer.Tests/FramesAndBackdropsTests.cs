using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>Frame color, Frame width and Backdrop odds reach every design that has the thing (design 3.3).</summary>
    public class FramesAndBackdropsTests
    {
        private static List<PhotoFacts> Photos(int count) =>
            Enumerable.Range(0, count).Select(n => new PhotoFacts($@"C:\p\{n}.jpg", 1600, 1200, new List<UnitRect>(), 0.5, 0.5, "#FF804020")).ToList();

        private static SurpriseOptions Only(string design, SurpriseOptions o)
        {
            foreach (string r in LayoutComposer.Recipes.Where(r => r != design)) o.DisabledDesigns.Add(r);
            return o;
        }

        private static readonly string[] Framed = { LayoutComposer.HeroSpotlight, LayoutComposer.ScatteredPrints, LayoutComposer.Circles };

        [Fact]
        public void Frame_color_black_reaches_gallery_wall_hero_prints_and_circles()
        {
            // Design check 5. Hero, prints and circles were always white before.
            var gallery = LayoutComposer.Compose(Photos(4), 1920, 1080, 1, options: Only(LayoutComposer.GalleryWall, new SurpriseOptions { Card = SurpriseOptions.CardBlack }))!;
            Assert.Equal("#FF141414", gallery.CanvasColor);
            foreach (string design in Framed)
                for (int seed = 0; seed < 10; seed++)
                {
                    var made = LayoutComposer.Compose(Photos(4), 1920, 1080, seed, options: Only(design, new SurpriseOptions { Card = SurpriseOptions.CardBlack }))!;
                    Assert.All(made.Cells.Where(c => c.Floating && c.Frame.Thickness > 0), c => Assert.Equal("#FF141414", c.Frame.Color));
                }
        }

        [Fact]
        public void Frame_width_scales_every_frame()
        {
            foreach (string design in Framed)
            {
                double Width(double scale) => LayoutComposer.Compose(Photos(4), 1920, 1080, 3, options: Only(design, new SurpriseOptions { GapScale = scale }))!
                    .Cells.Where(c => c.Floating && c.Frame.Thickness > 0).Max(c => c.Frame.Thickness);
                Assert.True(Width(3) > 2.5 * Width(1), $"{design}: {Width(3)} against {Width(1)}");
            }
        }

        [Fact]
        public void Backdrop_odds_decide_the_backdrop()
        {
            // Fresh options each time: Only() turns the other designs off in the options it is given.
            SurpriseOptions SolidOnly() => new() { BackdropVignetteOdds = 0, BackdropGradientOdds = 0, BackdropSolidOdds = 1 };
            SurpriseOptions VignetteOnly() => new() { BackdropVignetteOdds = 1, BackdropGradientOdds = 0, BackdropSolidOdds = 0 };
            foreach (string design in Framed)
                for (int seed = 0; seed < 10; seed++)
                {
                    Assert.Equal(CanvasLook.Solid, LayoutComposer.Compose(Photos(4), 1920, 1080, seed, options: Only(design, SolidOnly()))!.CanvasSurface);
                    Assert.Equal(CanvasLook.Vignette, LayoutComposer.Compose(Photos(4), 1920, 1080, seed, options: Only(design, VignetteOnly()))!.CanvasSurface);
                }
            // At the defaults all three come up, solid rarely.
            var surfaces = Enumerable.Range(0, 300).Select(seed => LayoutComposer.Compose(Photos(4), 1920, 1080, seed, options: Only(LayoutComposer.ScatteredPrints, new SurpriseOptions()))!.CanvasSurface)
                .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
            Assert.InRange(surfaces.GetValueOrDefault(CanvasLook.Solid), 15, 70);
            Assert.InRange(surfaces.GetValueOrDefault(CanvasLook.Vignette), 100, 170);
            Assert.InRange(surfaces.GetValueOrDefault(CanvasLook.Gradient), 100, 170);
        }
    }
}

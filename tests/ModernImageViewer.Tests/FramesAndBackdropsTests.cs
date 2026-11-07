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
                double Width(double scale) => LayoutComposer.Compose(Photos(4), 1920, 1080, 3, options: Only(design, new SurpriseOptions { GapScale = scale, GapScaleLargest = scale }))!
                    .Cells.Where(c => c.Floating && c.Frame.Thickness > 0).Max(c => c.Frame.Thickness);
                Assert.True(Width(3) > 2.5 * Width(1), $"{design}: {Width(3)} against {Width(1)}");
            }
        }

        [Fact]
        public void Frame_width_is_a_range_each_layout_picks_from()
        {
            // Each layout picks its own width in the range; the ends together are one width.
            foreach (string design in Framed)
            {
                var widths = Enumerable.Range(0, 40).Select(seed => LayoutComposer.Compose(Photos(4), 1920, 1080, seed,
                        options: Only(design, new SurpriseOptions { GapScale = 0.5, GapScaleLargest = 3 }))!
                    .Cells.Where(c => c.Floating && c.Frame.Thickness > 0).Max(c => c.Frame.Thickness)).ToList();
                Assert.True(widths.Max() > 2 * widths.Min(), $"{design}: {widths.Min()} to {widths.Max()}");
            }
            // The default range is 0.25x to 2x.
            Assert.Equal((0.25, 2.0), (new SurpriseOptions().GapScale, new SurpriseOptions().GapScaleLargest));
            // A saved file from before the range: a width the user chose reads as that width alone; the old default 1x
            // takes the new default range.
            var chosen = SurpriseOptions.FromJson("{\"GapScale\":1.5}")!;
            Assert.Equal((1.5, 1.5), (chosen.GapScale, chosen.GapScaleLargest));
            var oldDefault = SurpriseOptions.FromJson("{\"GapScale\":1}")!;
            Assert.Equal((0.25, 2.0), (oldDefault.GapScale, oldDefault.GapScaleLargest));
            var firstRangeBuild = SurpriseOptions.FromJson("{\"GapScale\":1,\"GapScaleLargest\":1}")!;
            Assert.Equal((0.25, 2.0), (firstRangeBuild.GapScale, firstRangeBuild.GapScaleLargest));
            var ranged = SurpriseOptions.FromJson("{\"GapScale\":0.5,\"GapScaleLargest\":1.5}")!;
            Assert.Equal((0.5, 1.5), (ranged.GapScale, ranged.GapScaleLargest));
        }

        [Fact]
        public void Keep_my_canvas_reaches_a_drafted_show_scene()
        {
            // A show scene is drafted as a layout file; Keep my canvas puts the user's canvas in it and clears solid card gaps.
            foreach (string design in LayoutComposer.Recipes)
                for (int seed = 0; seed < 5; seed++)
                {
                    var made = LayoutComposer.Compose(Photos(4), 1920, 1080, seed, options: Only(design, new SurpriseOptions()));
                    if (made == null) continue;
                    var layout = made.ToLayoutFile();
                    var frame = new CellBorderDto { Color = "#FF112233", Thickness = 9 };
                    layout.KeepCanvas("#FF445566", CanvasLook.Vignette, frame);
                    Assert.Equal("#FF445566", layout.Canvas.BackgroundColor);
                    Assert.Equal(CanvasLook.Vignette, layout.Canvas.Surface);
                    Assert.Equal("#FF112233", layout.Canvas.Frame!.Color);
                    var borders = layout.Photos.Select(p => p.Border).Concat(layout.Freeform?.Select(c => c.Border) ?? Enumerable.Empty<CellBorderDto?>());
                    Assert.DoesNotContain(borders, b => b?.GutterMaterial == "Solid");
                }
        }

        [Fact]
        public void Backdrop_odds_decide_the_backdrop()
        {
            // Fresh options each time: Only() turns the other designs off in the options it is given.
            SurpriseOptions SolidOnly() => new() { BackdropVignetteOdds = 0, BackdropGradientOdds = 0, BackdropSolidOdds = 1, BackdropAmbientBlurOdds = 0 };
            SurpriseOptions VignetteOnly() => new() { BackdropVignetteOdds = 1, BackdropGradientOdds = 0, BackdropSolidOdds = 0, BackdropAmbientBlurOdds = 0 };
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
            Assert.InRange(surfaces.GetValueOrDefault(CanvasLook.AmbientBlur), 10, 60);
            // Ambient blur alone is every backdrop.
            var blurOnly = Only(LayoutComposer.Circles, new SurpriseOptions { BackdropVignetteOdds = 0, BackdropGradientOdds = 0, BackdropSolidOdds = 0, BackdropAmbientBlurOdds = 1 });
            Assert.Equal(CanvasLook.AmbientBlur, LayoutComposer.Compose(Photos(4), 1920, 1080, 2, options: blurOnly)!.CanvasSurface);
        }
    }
}

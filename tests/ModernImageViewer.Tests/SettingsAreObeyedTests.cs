using System;
using System.Collections.Generic;
using System.Linq;
using ModernImageViewer;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>The generator follows the user's settings: no rule of its own skips them.</summary>
    public class SettingsAreObeyedTests
    {
        private static PhotoFacts Photo(int n, double w = 1600, double h = 1200, bool featured = false) =>
            new($@"C:\p\{n}.jpg", w, h, Array.Empty<UnitRect>(), 0.5, 0.4, "#FF804020", Featured: featured);

        private static SurpriseOptions Only(string design)
        {
            var o = new SurpriseOptions();
            foreach (string r in LayoutComposer.Recipes.Where(r => r != design)) o.DisabledDesigns.Add(r);
            return o;
        }

        [Fact]
        public void A_portrait_follows_the_single_image_odds()
        {
            var portrait = new[] { Photo(0, 1200, 1600) };
            var options = new SurpriseOptions { SoloFillOdds = 1, SoloMattedOdds = 1, SoloWholeOdds = 1 };
            var recipes = Enumerable.Range(0, 60).Select(seed => LayoutComposer.Compose(portrait, 1920, 1080, seed, options: options)!.Recipe).ToHashSet();
            // Matted is a one-image Gallery wall; Fitted and Whole are the stage.
            Assert.Contains(LayoutComposer.GalleryWall, recipes);
            Assert.Contains(LayoutComposer.HeroSpotlight, recipes);
        }

        [Fact]
        public void A_portrait_with_only_Fill_on_is_full_height_and_unframed()
        {
            var options = new SurpriseOptions { SoloFillOdds = 1, SoloMattedOdds = 0, SoloWholeOdds = 0 };
            for (int seed = 0; seed < 20; seed++)
            {
                var made = LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, seed, options: options)!;
                var cell = Assert.Single(made.Cells);
                Assert.Equal(1080, cell.H, 3);
                Assert.Equal(810, cell.W, 3);
                Assert.Equal(0, cell.Frame.Thickness);
            }
        }

        [Theory]
        [InlineData(SurpriseOptions.CardWhite, "#FFF8F8F8")]
        [InlineData(SurpriseOptions.CardBlack, "#FF141414")]
        public void Whole_image_takes_the_frame_color(string card, string color)
        {
            var options = new SurpriseOptions { SoloFillOdds = 0, SoloMattedOdds = 0, SoloWholeOdds = 1, Card = card };
            var made = LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, 3, options: options)!;
            Assert.Equal(color, Assert.Single(made.Cells).Frame.Color);
        }

        [Fact]
        public void Whole_image_takes_the_frame_width()
        {
            double Rim(double scale)
            {
                var options = new SurpriseOptions { SoloFillOdds = 0, SoloMattedOdds = 0, SoloWholeOdds = 1, GapScale = scale };
                return LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, 3, options: options)!.Cells[0].Frame.Thickness;
            }
            Assert.True(Rim(3) > 2 * Rim(1));
        }

        [Theory]
        [InlineData("Circles")]
        [InlineData("Scattered prints")]
        [InlineData("Slanted panels")]
        [InlineData("Hero spotlight")]
        public void Two_images_can_use_any_design(string design)
        {
            for (int seed = 0; seed < 10; seed++)
            {
                var made = LayoutComposer.Compose(new[] { Photo(0), Photo(1, 1200, 1600) }, 1920, 1080, seed, options: Only(design));
                Assert.NotNull(made);
                Assert.Equal(design, made!.Recipe);
                Assert.Equal(2, made.Cells.Count);
                Assert.Equal(0, made.PicturesBuried);
                Assert.All(made.Cells, c => Assert.True(c.W * c.H > 0.08 * 1920 * 1080, $"{design} cell {c.W:0}x{c.H:0} too small"));
                Assert.All(made.Cells, c => Assert.InRange(c.X + c.W / 2, 0, 1920));
            }
        }

        [Theory]
        [InlineData("Circles", 9)]
        [InlineData("Slanted panels", 6)]
        public void A_design_that_is_off_never_comes_back(string design, int most)
        {
            var photos = Enumerable.Range(0, 12).Select(n => Photo(n, featured: n == 11)).ToList();
            for (int seed = 0; seed < 10; seed++)
            {
                var made = LayoutComposer.Compose(photos, 1920, 1080, seed, options: Only(design))!;
                Assert.Equal(design, made.Recipe);
                Assert.Equal(most, made.Cells.Count);
                Assert.Contains(made.Cells, c => c.Photo.Featured);
            }
        }
    }
}

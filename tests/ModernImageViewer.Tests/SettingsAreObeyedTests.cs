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
        public void One_image_goes_through_the_design_odds()
        {
            var portrait = new[] { Photo(0, 1200, 1600) };
            var recipes = Enumerable.Range(0, 120).Select(seed => LayoutComposer.Compose(portrait, 1920, 1080, seed)!.Recipe).ToHashSet();
            // The three that can show any image whole always can; the single-image branch and its odds are gone.
            Assert.Contains(LayoutComposer.EdgeToEdge, recipes);
            Assert.Contains(LayoutComposer.GalleryWall, recipes);
            Assert.Contains(LayoutComposer.ScatteredPrints, recipes);
        }

        [Fact]
        public void Crop_at_most_0_shows_every_image_whole_and_only_designs_that_can()
        {
            // Zoom is off: it crops after the fill, which slice 4 orders (design 3.5).
            var options = new SurpriseOptions { CropAtMost = 0, ZoomChance = 0 };
            for (int seed = 0; seed < 60; seed++)
            {
                var made = LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, seed, options: options)!;
                Assert.Contains(made.Recipe, new[] { LayoutComposer.EdgeToEdge, LayoutComposer.GalleryWall, LayoutComposer.ScatteredPrints });
                Assert.True(LayoutComposer.CropShare(Assert.Single(made.Cells)) < 0.01, $"{made.Recipe} cropped");
            }
        }

        [Fact]
        public void Crop_at_most_100_fills_edge_to_edge()
        {
            var options = Only(LayoutComposer.EdgeToEdge);
            options.CropAtMost = 1;
            for (int seed = 0; seed < 20; seed++)
            {
                var cell = Assert.Single(LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, seed, options: options)!.Cells);
                Assert.True(cell.W >= 1919 && cell.H >= 1079, "a portrait should fill the screen when any crop is allowed");
            }
        }

        [Fact]
        public void A_portrait_past_crop_at_most_is_full_height_and_unframed()
        {
            var options = Only(LayoutComposer.EdgeToEdge);
            for (int seed = 0; seed < 20; seed++)
            {
                var cell = Assert.Single(LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, seed, options: options)!.Cells);
                Assert.Equal(1080, cell.H, 3);
                Assert.Equal(810, cell.W, 3);
                Assert.Equal(0, cell.Frame.Thickness);
            }
        }

        [Theory]
        [InlineData(SurpriseOptions.CardWhite, "#FFF8F8F8")]
        [InlineData(SurpriseOptions.CardBlack, "#FF141414")]
        public void A_whole_image_in_gallery_wall_sits_in_the_card_color(string card, string color)
        {
            var options = Only(LayoutComposer.GalleryWall);
            options.Card = card;
            options.ZoomChance = 0;
            var made = LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, 3, options: options)!;
            Assert.Equal(color, made.CanvasColor);
            Assert.True(LayoutComposer.CropShare(Assert.Single(made.Cells)) < 0.01);
        }

        [Fact]
        public void A_whole_image_in_gallery_wall_keeps_the_card_margin()
        {
            double Height(double scale)
            {
                var options = Only(LayoutComposer.GalleryWall);
                options.GapScale = scale;
                return LayoutComposer.Compose(new[] { Photo(0, 1200, 1600) }, 1920, 1080, 3, options: options)!.Cells[0].H;
            }
            Assert.True(Height(3) < Height(1) - 10);
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

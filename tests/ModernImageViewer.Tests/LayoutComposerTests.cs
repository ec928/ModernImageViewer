using System;
using System.Collections.Generic;
using System.Linq;
using ModernImageViewer;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class LayoutComposerTests
    {
        private static PhotoFacts Photo(int n, double w = 1600, double h = 1200, params UnitRect[] faces) =>
            new($@"C:\p\{n}.jpg", w, h, faces, 0.5, 0.4, "#FF804020");

        private static List<PhotoFacts> Photos(int count, double w = 1600, double h = 1200, params UnitRect[] faces) =>
            Enumerable.Range(0, count).Select(n => Photo(n, w, h, faces)).ToList();

        public static IEnumerable<object[]> HostileSets()
        {
            yield return new object[] { "two", Photos(2) };
            yield return new object[] { "all portraits", Photos(16, 1000, 3000) };
            yield return new object[] { "panoramas", Photos(5, 10000, 1000) };
            yield return new object[] { "slivers", Photos(4, 1, 2000) };
            yield return new object[] { "one pixel", Photos(6, 1, 1) };
            yield return new object[] { "a face filling the picture", Photos(7, 1600, 1200, new UnitRect(0, 0, 1, 1)) };
            yield return new object[] { "a face past the edge", Photos(4, 1600, 1200, new UnitRect(0.9, 0.9, 0.5, 0.5)) };
            yield return new object[] { "mixed", new List<PhotoFacts> { Photo(0, 6000, 1000), Photo(1, 800, 3000), Photo(2, 500, 500), Photo(3, 20, 20) } };
            yield return new object[] { "more than can show", Photos(40) };
        }

        [Theory]
        [MemberData(nameof(HostileSets))]
        public void Hostile_sets_make_a_layout_that_loads(string name, List<PhotoFacts> photos)
        {
            foreach (string recipe in LayoutComposer.Recipes)
            {
                if (!LayoutComposer.CanMake(recipe, Math.Min(photos.Count, MosaicLayout.MaxShown))) continue;
                for (int seed = 0; seed < 5; seed++)
                {
                    var made = LayoutComposer.Compose(photos, 1280, 720, seed, null, 1, recipe);
                    Assert.True(made != null, $"{name}, {recipe}: nothing made");
                    Assert.Equal(recipe, made!.Recipe);
                    Assert.Equal(Math.Min(photos.Count, MosaicLayout.MaxShown), made.Cells.Select(c => c.Photo.Path).Distinct().Count());
                    foreach (var cell in made.Cells)
                    {
                        foreach (double v in new[] { cell.X, cell.Y, cell.W, cell.H, cell.PanX, cell.PanY, cell.ImageWidth, cell.ImageHeight })
                            Assert.True(double.IsFinite(v), $"{name}, {recipe}: {v}");
                        Assert.True(cell.W > 0 && cell.H > 0, $"{name}, {recipe}: {cell.W} x {cell.H}");
                    }

                    var read = MultiViewLayoutFile.FromJson(made.ToLayoutFile().ToJson());
                    Assert.NotNull(read);
                    Assert.NotEmpty(read!.Photos);
                    int grid = made.Cells.Count(c => !c.Floating);
                    if (grid > 0) Assert.True(read.Mosaic!.FitsPhotos(grid), $"{name}, {recipe}: tree does not fit {grid}");
                    Assert.Equal(made.Cells.Count(c => c.Floating), read.Freeform?.Count ?? 0);
                }
            }
        }

        [Fact]
        public void Too_few_pictures_or_no_room_make_nothing()
        {
            Assert.Null(LayoutComposer.Compose(Photos(1), 1280, 720, 1));
            Assert.Null(LayoutComposer.Compose(Photos(0), 1280, 720, 1));
            Assert.Null(LayoutComposer.Compose(Photos(4), 0, 0, 1));
            Assert.Null(LayoutComposer.Compose(Photos(4, 0, 0), 1280, 720, 1));
        }

        [Fact]
        public void Clicking_again_gives_a_different_recipe()
        {
            foreach (string shown in LayoutComposer.Recipes)
            {
                for (int seed = 0; seed < 25; seed++)
                {
                    var made = LayoutComposer.Compose(Photos(6), 1280, 720, seed, shown);
                    Assert.NotEqual(shown, made!.Recipe);
                }
            }
        }

        [Fact]
        public void Every_recipe_turns_up()
        {
            var seen = Enumerable.Range(0, 60).Select(seed => LayoutComposer.Compose(Photos(6), 1280, 720, seed)!.Recipe).ToHashSet();
            Assert.Equal(LayoutComposer.Recipes.Length, seen.Count);
        }

        [Fact]
        public void Hero_shape_is_not_decided_by_the_score()
        {
            var shapes = Enumerable.Range(0, 80)
                .Select(seed => LayoutComposer.Compose(Photos(6), 1280, 720, seed, null, 1, LayoutComposer.HeroSpotlight)!)
                .Select(c => c.Cells.Single(cell => cell.Floating).Geometry.Shape)
                .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(4, shapes.Count);
            Assert.True(shapes.Values.Max() <= 40, string.Join(", ", shapes.Select(s => $"{s.Key} {s.Value}")));
        }

        [Fact]
        public void A_chosen_hero_does_not_bury_the_picture_behind_it()
        {
            int clean = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                // Enough pictures behind the hero that one small cell could lie wholly under it.
                var made = LayoutComposer.Compose(Photos(10), 1280, 720, seed, null, 1, LayoutComposer.HeroSpotlight)!;
                if (made.PicturesBuried > 0) continue;
                clean++;
                var hero = made.Cells.Single(c => c.Floating);
                double pad = hero.Frame.Padding;
                foreach (var cell in made.Cells.Where(c => !c.Floating))
                {
                    double overX = Math.Min(cell.X + cell.W, hero.X + hero.W - pad) - Math.Max(cell.X, hero.X + pad);
                    double overY = Math.Min(cell.Y + cell.H, hero.Y + hero.H - pad) - Math.Max(cell.Y, hero.Y + pad);
                    double share = Math.Max(0, overX) * Math.Max(0, overY) / (cell.W * cell.H);
                    // Measured against the hero's box, which its shape does not fill, so only a cell wholly inside it counts.
                    Assert.True(share < 0.95, $"seed {seed}: {share:P0} of a picture under the hero");
                }
            }
            Assert.True(clean > 0, "no version was clean, so nothing was checked");
        }
    }
}

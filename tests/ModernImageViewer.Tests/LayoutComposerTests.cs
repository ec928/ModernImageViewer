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
        public void A_deck_shows_every_picture_before_any_comes_round_again()
        {
            var all = Enumerable.Range(0, 268).Select(n => $@"C:\p\{n}.jpg").ToList();
            var deck = new PictureDeck(all, new Random(3));
            var seen = new HashSet<string>();
            var random = new Random(4);
            while (seen.Count < all.Count)
            {
                var hand = deck.Deal(random.Next(9, 17));
                Assert.Equal(hand.Count, hand.Distinct().Count());
                int fresh = hand.Count(seen.Add);
                // Only the hand that finishes the round may hold pictures already seen.
                if (seen.Count < all.Count) Assert.Equal(hand.Count, fresh);
            }
            for (int i = 0; i < 200; i++)
            {
                var hand = deck.Deal(random.Next(9, 17));
                Assert.Equal(hand.Count, hand.Distinct().Count());
                Assert.InRange(hand.Count, 9, 16);
            }
        }

        [Fact]
        public void A_picture_added_to_a_deck_comes_up_next()
        {
            var deck = new PictureDeck(Enumerable.Range(0, 40).Select(n => $@"C:\p\{n}.jpg"), new Random(3));
            deck.Deal(12);
            deck.Include(new[] { @"C:\new.jpg", @"C:\p\0.jpg" });
            Assert.Equal(41, deck.Count);
            Assert.Contains(@"C:\new.jpg", deck.Deal(9));
        }

        private static SurpriseOptions Zoomed(string level) => new() { Zoom = level };

        [Fact]
        public void Zoom_is_off_unless_asked_for()
        {
            var made = LayoutComposer.Compose(Photos(6, 4000, 3000, new UnitRect(0.45, 0.4, 0.04, 0.05)), 1280, 720, 1)!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
            Assert.All(made.ToLayoutFile().Photos, p => Assert.Null(p.Zoom));
        }

        [Theory]
        [InlineData("Up to 2x", 2)]
        [InlineData("Up to 3x", 3)]
        [InlineData("Up to 5x", 5)]
        public void Zoom_goes_to_the_level_chosen_on_a_small_face(string level, double most)
        {
            var face = new UnitRect(0.45, 0.4, 0.02, 0.03);
            var made = LayoutComposer.Compose(Photos(6, 8000, 6000, face), 1280, 720, 2, null, 1, LayoutComposer.EdgeToEdge, Zoomed(level))!;
            Assert.All(made.Cells, c => Assert.Equal(most, c.Zoom, 3));
            Assert.Equal(0, made.FacesCut);
            Assert.All(made.ToLayoutFile().Photos, p => Assert.Equal(most, p.Zoom!.Value, 3));
        }

        [Fact]
        public void Zoom_leaves_a_picture_alone_when_faces_already_fill_it_or_none_were_found()
        {
            var made = LayoutComposer.Compose(Photos(4, 4000, 3000, new UnitRect(0.05, 0.05, 0.9, 0.9)), 1280, 720, 3, null, 1, LayoutComposer.EdgeToEdge, Zoomed("Up to 5x"))!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
            made = LayoutComposer.Compose(Photos(4, 4000, 3000), 1280, 720, 3, null, 1, LayoutComposer.EdgeToEdge, Zoomed("Up to 5x"))!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
        }

        [Fact]
        public void Zoom_stops_at_the_detail_a_small_picture_has()
        {
            var face = new UnitRect(0.45, 0.4, 0.02, 0.03);
            var made = LayoutComposer.Compose(Photos(4, 400, 300, face), 1280, 720, 4, null, 1, LayoutComposer.EdgeToEdge, Zoomed("Up to 5x"))!;
            Assert.All(made.Cells, c => Assert.True(c.ImageWidth <= Math.Max(2 * 400, c.ImageWidth / c.Zoom) + 0.01, $"{c.ImageWidth} at {c.Zoom}"));
        }

        [Fact]
        public void A_zoomed_face_stays_in_its_cell()
        {
            var face = new UnitRect(0.02, 0.05, 0.03, 0.04);
            var made = LayoutComposer.Compose(Photos(6, 8000, 6000, face), 1280, 720, 5, null, 1, LayoutComposer.GalleryWall, Zoomed("Up to 5x"))!;
            Assert.Contains(made.Cells, c => c.Zoom > 1);
            foreach (var cell in made.Cells)
            {
                double pad = cell.Frame.Padding;
                double cx = cell.X + cell.W / 2, cy = cell.Y + cell.H / 2;
                double x = cx + cell.PanX + (face.X + face.W / 2 - 0.5) * cell.ImageWidth;
                double y = cy + cell.PanY + (face.Y + face.H / 2 - 0.5) * cell.ImageHeight;
                Assert.InRange(x, cell.X + pad, cell.X + cell.W - pad);
                Assert.InRange(y, cell.Y + pad, cell.Y + cell.H - pad);
            }
        }

        [Fact]
        public void Only_the_ticked_designs_are_used()
        {
            var options = new SurpriseOptions { Designs = new List<string> { LayoutComposer.GalleryWall, LayoutComposer.HeroSpotlight } };
            var seen = Enumerable.Range(0, 40).Select(seed => LayoutComposer.Compose(Photos(6), 1280, 720, seed, null, 1, null, options)!.Recipe).ToHashSet();
            Assert.Equal(new HashSet<string>(options.Designs), seen);
        }

        [Fact]
        public void A_ticked_design_that_cannot_take_the_set_still_gives_a_layout()
        {
            var options = new SurpriseOptions { Designs = new List<string> { LayoutComposer.ScatteredPrints } };
            Assert.NotNull(LayoutComposer.Compose(Photos(12), 1280, 720, 1, null, 1, null, options));
        }

        [Fact]
        public void The_last_design_cannot_be_unticked()
        {
            var options = new SurpriseOptions { Designs = new List<string> { LayoutComposer.EdgeToEdge } };
            options.ToggleDesign(LayoutComposer.EdgeToEdge);
            Assert.Equal(new[] { LayoutComposer.EdgeToEdge }, options.Designs);
        }

        [Fact]
        public void Keeping_the_canvas_keeps_it_and_clears_a_cards_gaps()
        {
            var options = new SurpriseOptions { Designs = new List<string> { LayoutComposer.GalleryWall } };
            var made = LayoutComposer.Compose(Photos(6), 1280, 720, 1, null, 1, null, options)!;
            var frame = new CellBorderDto { Thickness = 4, Color = "#FF112233" };
            made.KeepCanvas("#FF445566", CanvasLook.Vignette, frame);
            var file = made.ToLayoutFile();
            Assert.Equal("#FF445566", file.Canvas.BackgroundColor);
            Assert.Equal(CanvasLook.Vignette, file.Canvas.Surface);
            Assert.Equal(4, file.Canvas.Frame!.Thickness);
            Assert.All(file.Photos, p => Assert.Equal("None", p.Border!.GutterMaterial));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"Designs\":[\"Nothing\"],\"Zoom\":\"Up to 99x\",\"Pictures\":\"lots\"}")]
        public void Damaged_options_fall_back_to_the_defaults(string? json)
        {
            var read = SurpriseOptions.FromJson(json);
            Assert.Equal(LayoutComposer.Recipes.Length, read.Designs.Count);
            Assert.Equal(1, read.MostZoom);
            Assert.Equal((9, 16), read.PictureRange);
        }

        [Fact]
        public void Options_survive_being_saved()
        {
            var options = new SurpriseOptions { Zoom = "Up to 3x", Pictures = "4 to 6", KeepCanvas = true, Designs = new List<string> { LayoutComposer.HeroSpotlight } };
            var read = SurpriseOptions.FromJson(options.ToJson());
            Assert.Equal(3, read.MostZoom);
            Assert.Equal((4, 6), read.PictureRange);
            Assert.True(read.KeepCanvas);
            Assert.Equal(options.Designs, read.Designs);
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

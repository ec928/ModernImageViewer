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

        [Theory]
        [InlineData(0, 3, 3, 0, 0)]
        [InlineData(0, 16, 16, 0, 0)]
        [InlineData(0, 120, 16, 0, 104)]
        [InlineData(14, 10, 2, 0, 8)]
        [InlineData(15, 1, 1, 0, 0)]
        [InlineData(16, 1, 0, 1, 0)]
        [InlineData(16, 4, 0, 4, 0)]
        [InlineData(16, 120, 0, 4, 116)]
        [InlineData(19, 6, 0, 4, 2)]
        public void AddedImagesFillTheCanvasThenFloatFourThenGoInTheTray(int onCanvas, int adding, int cells, int freeform, int tray) =>
            Assert.Equal((cells, freeform, tray), MosaicLayout.PlaceAdded(onCanvas, adding));

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
            // Images per scene starts at 1 by default, so one picture is a layout of one image.
            Assert.Single(LayoutComposer.Compose(Photos(1), 1280, 720, 1)!.Cells);
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

        [Fact]
        public void A_picture_removed_from_the_session_never_comes_round_again()
        {
            var all = Enumerable.Range(0, 40).Select(n => $@"C:\p\{n}.jpg").ToList();
            var deck = new PictureDeck(all, new Random(3));
            deck.Deal(12);
            const string gone = @"C:\p\7.jpg";
            deck.MatchSession(all.Where(p => p != gone).ToList());
            Assert.Equal(39, deck.Count);
            Assert.DoesNotContain(gone, deck.All);
            Assert.False(deck.Holds(gone));
            for (int i = 0; i < 100; i++) Assert.DoesNotContain(gone, deck.Deal(16));
        }

        private static void AssertCovered(CameraView view)
        {
            double margin = 0.5 / view.Zoom;
            Assert.True(view.Zoom >= 1, $"zoom {view.Zoom}");
            Assert.InRange(view.X, margin - 1e-9, 1 - margin + 1e-9);
            Assert.InRange(view.Y, margin - 1e-9, 1 - margin + 1e-9);
        }

        [Fact]
        public void The_camera_never_shows_past_the_edge_of_the_canvas()
        {
            var random = new Random(5);
            double[] amounts = { 0.01, 0.2, 0.5, 1, 7 };
            double[] lengths = { 0.1, 3, 5, 12, 62.5, 1000 };
            (double, double)[][] aims = { Array.Empty<(double, double)>(), new[] { (0.0, 0.0) }, new[] { (1.0, 1.0) }, new[] { (0.5, 0.5), (-3.0, 9.0) } };
            for (int i = 0; i < 4000; i++)
            {
                var move = CameraMove.Plan(aims[i % aims.Length], amounts[i % amounts.Length], random.NextDouble(), random.NextDouble(), lengths[i % lengths.Length], null, random)!;
                for (double t = -1; t <= move.Seconds + 1; t += Math.Max(move.Seconds / 7, 0.01)) AssertCovered(move.At(t));
            }
        }

        [Fact]
        public void The_camera_visibly_moves_and_each_kind_stays_what_it_says()
        {
            var random = new Random(9);
            var seen = new HashSet<CameraKind>();
            for (int i = 0; i < 500; i++)
            {
                var move = CameraMove.Plan(Array.Empty<(double, double)>(), 0.5, 0.5, 0, 8, null, random)!;
                seen.Add(move.Kind);
                var (start, end) = (move.At(0), move.At(8));
                if (move.Kind == CameraKind.ZoomIn) Assert.True(end.Zoom > start.Zoom);
                if (move.Kind == CameraKind.ZoomOut) Assert.True(end.Zoom < start.Zoom);
                if (move.Kind == CameraKind.Drift) Assert.True(Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) > 0.02);
            }
            Assert.Equal(3, seen.Count);
        }

        [Fact]
        public void No_motion_means_no_camera_and_the_same_kind_does_not_repeat()
        {
            var random = new Random(4);
            Assert.Null(CameraMove.Plan(Array.Empty<(double, double)>(), 0, 0.5, 0.5, 8, null, random));
            int repeats = 0;
            CameraKind? last = null;
            for (int i = 0; i < 500; i++)
            {
                var move = CameraMove.Plan(Array.Empty<(double, double)>(), 0.5, 0.5, 0, 8, last, random)!;
                if (move.Kind == last) repeats++;
                last = move.Kind;
            }
            Assert.True(repeats < 10, $"{repeats} repeats");
        }

        [Fact]
        public void The_camera_gets_somewhere_to_aim_on_every_design_with_faces()
        {
            var photos = Photos(9, 1600, 1200, new UnitRect(0.45, 0.4, 0.1, 0.12));
            foreach (string recipe in LayoutComposer.Recipes)
            {
                if (!LayoutComposer.CanMake(recipe, photos.Count)) continue;
                var made = LayoutComposer.Compose(photos, 1280, 720, 1, null, 1, recipe)!;
                var subjects = made.CameraSubjects();
                Assert.NotEmpty(subjects);
                Assert.True(subjects.Distinct().Count() > 1 || made.Cells.Count == 1, recipe);
                Assert.All(subjects, s => { Assert.InRange(s.X, 0, 1); Assert.InRange(s.Y, 0, 1); });
            }
        }

        [Fact]
        public void A_floating_cell_stays_mild_and_each_floats_its_own_way()
        {
            var random = new Random(11);
            double[] amounts = { 0.01, 0.5, 1, 7 };
            var plain = new CameraView(1, 0.5, 0.5);
            for (int i = 0; i < 2000; i++)
            {
                var cell = new CellFloat(i % 5, 5, amounts[i % amounts.Length], random);
                double seconds = random.NextDouble() * 500;
                var drift = cell.At(seconds, plain, 1500, 900);
                Assert.InRange(Math.Abs(drift.X), 0, 900 * 0.0121);
                Assert.InRange(Math.Abs(drift.Y), 0, 900 * 0.0121);
                Assert.InRange(Math.Abs(drift.Turn), 0, 1.21);
                Assert.Equal(1, drift.Scale);
                var homing = cell.At(seconds, plain, 1500, 900, strength: 0);
                Assert.Equal(0, Math.Abs(homing.X) + Math.Abs(homing.Y) + Math.Abs(homing.Turn), 9);
            }
            var a = new CellFloat(0, 2, 1, random).At(3, plain, 1500, 900);
            var b = new CellFloat(1, 2, 1, random).At(3, plain, 1500, 900);
            Assert.NotEqual(a, b);
            var still = new CellFloat(1, 2, 0, random).At(3, new CameraView(1.5, 0.3, 0.3), 1500, 900);
            Assert.Equal(new CellDrift(0, 0, 0, 1), still);
        }

        [Fact]
        public void A_front_cell_follows_the_camera_further_than_a_back_cell()
        {
            var random = new Random(2);
            var view = new CameraView(1.3, 0.7, 0.5);
            CellDrift Depth(int place) => new CellFloat(place, 5, 1, random).At(0, view, 1500, 900, strength: 0);
            var back = Depth(0);
            var front = Depth(4);
            Assert.True(front.Scale > back.Scale && back.Scale > 1);
            Assert.True(front.X < back.X && back.X < 0, "the view moved right, so a nearer cell moves further left");
        }

        private static SurpriseOptions Zoomed(double most) => new() { MostZoom = most };

        [Fact]
        public void Zoom_is_off_unless_asked_for()
        {
            var made = LayoutComposer.Compose(Photos(6, 4000, 3000, new UnitRect(0.45, 0.4, 0.04, 0.05)), 1280, 720, 1)!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
            Assert.All(made.ToLayoutFile().Photos, p => Assert.Null(p.Zoom));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        public void Zoom_goes_to_the_level_chosen_on_a_small_face(double most)
        {
            var face = new UnitRect(0.45, 0.4, 0.02, 0.03);
            var made = LayoutComposer.Compose(Photos(6, 8000, 6000, face), 1280, 720, 2, null, 1, LayoutComposer.EdgeToEdge, Zoomed(most))!;
            Assert.All(made.Cells, c => Assert.Equal(most, c.Zoom, 3));
            Assert.Equal(0, made.FacesCut);
            Assert.All(made.ToLayoutFile().Photos, p => Assert.Equal(most, p.Zoom!.Value, 3));
        }

        [Fact]
        public void Zoom_leaves_a_picture_alone_when_faces_already_fill_it_or_none_were_found()
        {
            var made = LayoutComposer.Compose(Photos(4, 4000, 3000, new UnitRect(0.05, 0.05, 0.9, 0.9)), 1280, 720, 3, null, 1, LayoutComposer.EdgeToEdge, Zoomed(5))!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
            made = LayoutComposer.Compose(Photos(4, 4000, 3000), 1280, 720, 3, null, 1, LayoutComposer.EdgeToEdge, Zoomed(5))!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
        }

        [Fact]
        public void Zoom_stops_at_the_detail_a_small_picture_has()
        {
            var face = new UnitRect(0.45, 0.4, 0.02, 0.03);
            var made = LayoutComposer.Compose(Photos(4, 400, 300, face), 1280, 720, 4, null, 1, LayoutComposer.EdgeToEdge, Zoomed(5))!;
            Assert.All(made.Cells, c => Assert.True(c.ImageWidth <= Math.Max(2 * 400, c.ImageWidth / c.Zoom) + 0.01, $"{c.ImageWidth} at {c.Zoom}"));
        }

        [Fact]
        public void A_zoomed_face_stays_in_its_cell()
        {
            var face = new UnitRect(0.02, 0.05, 0.03, 0.04);
            var made = LayoutComposer.Compose(Photos(6, 8000, 6000, face), 1280, 720, 5, null, 1, LayoutComposer.GalleryWall, Zoomed(5))!;
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
        public void Keeping_the_canvas_keeps_it_and_clears_a_cards_gaps()
        {
            var made = LayoutComposer.Compose(Photos(6), 1280, 720, 1, null, 1, LayoutComposer.GalleryWall)!;
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
        public void Damaged_options_fall_back_to_the_defaults(string? json)
        {
            var read = SurpriseOptions.FromJson(json);
            Assert.Equal(SurpriseOptions.CardAny, read.Card);
            Assert.Equal(1, read.MostZoom);
            Assert.Equal(3, read.Variety);
            Assert.Equal(1, read.FewestPictures);
            Assert.Equal(9, read.MostPictures);
            Assert.True(read.AutoPlayRandom);
            Assert.Equal(10, read.RandomFewestSeconds);
            Assert.Equal(15, read.RandomMostSeconds);
            Assert.Equal(10, read.AutoPlaySeconds);
            var random = new SurpriseOptions { AutoPlayRandom = true, RandomFewestSeconds = 8, RandomMostSeconds = 20 };
            var seen = Enumerable.Range(0, 2000).Select(_ => random.NextPlaySeconds(new Random(_))).ToHashSet();
            Assert.Equal(8, seen.Min());
            Assert.Equal(20, seen.Max());
            Assert.True(seen.Count > 5);
            Assert.Equal("8\u201320 s", random.PlayLabel);
            Assert.Equal(15, new SurpriseOptions { AutoPlayRandom = false, AutoPlaySeconds = 15 }.NextPlaySeconds(new Random(1)));
            Assert.InRange(new SurpriseOptions().NextPlaySeconds(new Random(1)), 10, 15);
            var crossed = SurpriseOptions.FromJson("{\"AutoPlaySeconds\":15,\"RandomFewestSeconds\":30,\"RandomMostSeconds\":5}");
            Assert.Equal(15, crossed.AutoPlaySeconds);
            Assert.True(crossed.RandomMostSeconds >= crossed.RandomFewestSeconds);
            // Any whole second the Duration slider can set is kept; only values out of range are brought back into it.
            Assert.Equal(7, SurpriseOptions.FromJson("{\"RandomFewestSeconds\":7}").RandomFewestSeconds);
            Assert.Equal(SurpriseOptions.FewestSeconds, SurpriseOptions.FromJson("{\"RandomFewestSeconds\":1}").RandomFewestSeconds);
            Assert.Equal(SurpriseOptions.MostSeconds, SurpriseOptions.FromJson("{\"RandomMostSeconds\":500}").RandomMostSeconds);
            Assert.False(new SurpriseOptions { MostPictures = 16 }.TakesTurns(16));
            Assert.True(new SurpriseOptions { MostPictures = 16 }.TakesTurns(17));
            Assert.True(new SurpriseOptions { MostPictures = 9 }.TakesTurns(14));
            Assert.False(new SurpriseOptions { MostPictures = 9 }.TakesTurns(9));
        }

        [Fact]
        public void Saved_values_out_of_range_come_back_within_it()
        {
            var read = SurpriseOptions.FromJson("{\"MostZoom\":99,\"ZoomChance\":-3,\"Variety\":0,\"FaceShown\":7,\"MostTilt\":900,\"HeroSize\":0,\"GapScale\":-1,\"FewestPictures\":14,\"MostPictures\":3,\"TogetherChance\":5,\"Card\":\"Plaid\",\"DesignOdds\":{\"Circles\":0,\"Gallery wall\":999}}");
            Assert.Equal(5, read.MostZoom);
            Assert.Equal(0, read.ZoomChance);
            Assert.Equal(1, read.Variety);
            Assert.Equal(1, read.FaceShown);
            Assert.Equal(20, read.MostTilt);
            // An old single hero size of 0 is brought to 0.4 first, as before, then becomes its range.
            Assert.Equal(0.3, read.HeroSmallest, 6);
            Assert.Equal(0.5, read.HeroLargest, 6);
            Assert.Equal(0.25, read.GapScale);
            Assert.Equal(14, read.FewestPictures);
            Assert.Equal(14, read.MostPictures);
            Assert.Equal(1, read.TogetherChance);
            Assert.Equal(SurpriseOptions.CardAny, read.Card);
            Assert.Equal(SurpriseOptions.RarestDesign, read.OddsOf(LayoutComposer.Circles));
            Assert.Equal(SurpriseOptions.MostFavouredDesign, read.OddsOf(LayoutComposer.GalleryWall));
        }

        [Fact]
        public void Options_survive_being_saved()
        {
            var options = new SurpriseOptions { MostZoom = 3, ZoomChance = 0.5, Variety = 7, MostTilt = 12, KeepCanvas = true, Card = SurpriseOptions.CardBlack, TogetherChance = 0.25, SkipScreens = false };
            options.DesignOdds[LayoutComposer.Circles] = 4;
            var read = SurpriseOptions.FromJson(options.ToJson());
            Assert.Equal(3, read.MostZoom);
            Assert.Equal(0.5, read.ZoomChance);
            Assert.Equal(7, read.Variety);
            Assert.Equal(12, read.MostTilt);
            Assert.True(read.KeepCanvas);
            Assert.Equal(SurpriseOptions.CardBlack, read.Card);
            Assert.Equal(0.25, read.TogetherChance);
            Assert.False(read.SkipScreens);
            Assert.Equal(4, read.OddsOf(LayoutComposer.Circles));
            Assert.Equal(1, read.OddsOf(LayoutComposer.EdgeToEdge));
        }

        [Fact]
        public void Play_progress_defaults_to_the_bottom_edge_and_keeps_a_chosen_one()
        {
            var fresh = SurpriseOptions.FromJson("{}");
            Assert.True(fresh.ProgressOn);
            Assert.Equal(SurpriseOptions.ProgressBottom, fresh.ProgressEdge);
            Assert.Equal(SurpriseOptions.ProgressAccent, fresh.ProgressColor);

            var saved = new SurpriseOptions
            {
                ProgressOn = false,
                ProgressEdge = SurpriseOptions.ProgressLeft,
                ProgressColor = SurpriseOptions.ProgressAmber
            };
            var read = SurpriseOptions.FromJson(saved.ToJson());
            Assert.False(read.ProgressOn);
            Assert.Equal(SurpriseOptions.ProgressLeft, read.ProgressEdge);
            Assert.Equal(SurpriseOptions.ProgressAmber, read.ProgressColor);

            var bad = SurpriseOptions.FromJson("{\"ProgressEdge\":\"Middle\",\"ProgressColor\":\"Plaid\"}");
            Assert.True(bad.ProgressOn);
            Assert.Equal(SurpriseOptions.ProgressBottom, bad.ProgressEdge);
            Assert.Equal(SurpriseOptions.ProgressAccent, bad.ProgressColor);
        }

        [Fact]
        public void Favouring_a_design_makes_it_come_up_more_and_none_is_shut_out()
        {
            var options = new SurpriseOptions();
            options.DesignOdds[LayoutComposer.GalleryWall] = SurpriseOptions.MostFavouredDesign;
            options.DesignOdds[LayoutComposer.Circles] = SurpriseOptions.RarestDesign;
            var counts = Enumerable.Range(0, 300).Select(seed => LayoutComposer.Compose(Photos(5), 1280, 720, seed, null, 1, null, options)!.Recipe)
                .GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(LayoutComposer.Recipes.Length, counts.Count);
            Assert.True(counts[LayoutComposer.GalleryWall] > 2 * counts[LayoutComposer.EdgeToEdge], string.Join(", ", counts));
            Assert.True(counts[LayoutComposer.Circles] < counts[LayoutComposer.EdgeToEdge] / 2, string.Join(", ", counts));
        }

        [Fact]
        public void Tilt_stays_within_the_most_asked_for()
        {
            foreach (double most in new[] { 0.0, 3, 20 })
            {
                var made = LayoutComposer.Compose(Photos(6), 1280, 720, 1, null, 1, LayoutComposer.ScatteredPrints, new SurpriseOptions { MostTilt = most })!;
                Assert.All(made.Cells, c => Assert.InRange(Math.Abs(c.Rotation), most * 0.23 - 1e-9, most + 1e-9));
            }
        }

        [Fact]
        public void Hero_size_follows_the_setting()
        {
            double Height(double low, double high) => Enumerable.Range(0, 30)
                .Select(seed => LayoutComposer.Compose(Photos(6), 1280, 720, seed, null, 1, LayoutComposer.HeroSpotlight, new SurpriseOptions { HeroSmallest = low, HeroLargest = high })!.Cells.Single(c => c.Floating).H).Average();
            Assert.True(Height(0.9, 0.9) > Height(0.4, 0.4) * 1.3);
        }

        [Fact]
        public void Hero_size_stays_inside_its_range()
        {
            List<double> Heights(double low, double high) => Enumerable.Range(0, 40)
                .Select(seed => LayoutComposer.Compose(Photos(6), 1280, 720, seed, null, 1, LayoutComposer.HeroSpotlight, new SurpriseOptions { HeroSmallest = low, HeroLargest = high }))
                .Where(c => c != null)
                .Select(c => c!.Cells.Single(x => x.Floating).H / 720)
                .ToList();
            var small = Heights(0.5, 0.55);
            var large = Heights(0.8, 0.85);
            Assert.NotEmpty(small);
            Assert.NotEmpty(large);
            // The card adds its frame to the hero, so compare two ranges rather than one against fixed numbers.
            Assert.True(small.Max() < large.Min(), $"largest of 50-55% {small.Max():0.000}, smallest of 80-85% {large.Min():0.000}");
        }

        [Fact]
        public void A_saved_hero_size_becomes_the_range_it_stood_for()
        {
            var read = SurpriseOptions.FromJson("{\"HeroSize\":0.72}");
            Assert.Equal(0.62, read.HeroSmallest, 6);
            Assert.Equal(0.82, read.HeroLargest, 6);
            Assert.DoesNotContain("HeroSize", read.ToJson());
            var bad = SurpriseOptions.FromJson("{\"HeroSmallest\":2,\"HeroLargest\":0.1}");
            Assert.Equal(SurpriseOptions.HeroHighest, bad.HeroSmallest);
            Assert.Equal(SurpriseOptions.HeroHighest, bad.HeroLargest);
        }

        [Fact]
        public void Zoom_chance_of_none_never_zooms()
        {
            var face = new UnitRect(0.45, 0.4, 0.02, 0.03);
            var made = LayoutComposer.Compose(Photos(6, 8000, 6000, face), 1280, 720, 2, null, 1, LayoutComposer.EdgeToEdge, new SurpriseOptions { MostZoom = 5, ZoomChance = 0 })!;
            Assert.All(made.Cells, c => Assert.Equal(1, c.Zoom));
        }

        [Fact]
        public void Variety_of_one_always_takes_the_best_version()
        {
            var photos = Photos(7, 1600, 1200, new UnitRect(0.4, 0.3, 0.2, 0.2));
            var strict = new SurpriseOptions { Variety = 1 };
            var loose = new SurpriseOptions { Variety = 12 };
            double Mean(SurpriseOptions o) => Enumerable.Range(0, 60).Average(seed => LayoutComposer.Compose(photos, 1280, 720, seed, null, 1, LayoutComposer.GalleryWall, o)!.Score);
            Assert.True(Mean(strict) > Mean(loose));
        }

        [Fact]
        public void A_hand_dealt_together_comes_from_pictures_next_to_each_other()
        {
            var all = Enumerable.Range(0, 200).Select(n => $@"C:\p\{n}.jpg").ToList();
            var deck = new PictureDeck(all, new Random(3));
            var seen = new HashSet<string>();
            int hands = 0, widths = 0;
            while (seen.Count < all.Count)
            {
                var hand = deck.Deal(12, together: true);
                var at = hand.Select(p => all.IndexOf(p)).ToList();
                if (seen.Count < 100)
                {
                    widths += at.Max() - at.Min();
                    hands++;
                }
                foreach (string picture in hand) seen.Add(picture);
            }
            Assert.True(widths / hands < 40, $"hands span {widths / hands} pictures on average");
        }

        [Fact]
        public void Screens_and_repeats_are_left_out_but_not_a_featured_picture_or_everything()
        {
            PhotoFacts One(int n, ulong look, bool screen = false, bool featured = false) => Photo(n) with { Look = look, ScreenLike = screen, Featured = featured };
            var kept = LayoutComposer.LeaveOutScreensAndRepeats(new List<PhotoFacts> { One(0, 0xF0F0), One(1, 0xF0F1), One(2, 0xFFFF0000), One(3, 0xAAAAAAAA, screen: true), One(4, 0x123456789ABCDEF, screen: true, featured: true) });
            Assert.Equal(new[] { @"C:\p\4.jpg", @"C:\p\0.jpg", @"C:\p\2.jpg" }, kept.Select(p => p.Path));
            var screens = new List<PhotoFacts> { One(0, 1, screen: true), One(1, 2, screen: true), One(2, 3, screen: true) };
            Assert.Equal(3, LayoutComposer.LeaveOutScreensAndRepeats(screens).Count);
        }

        [Fact]
        public void A_featured_picture_always_gets_the_best_place()
        {
            var photos = Photos(6, 1600, 1200, new UnitRect(0.4, 0.3, 0.2, 0.2));
            photos[3] = photos[3] with { Faces = Array.Empty<UnitRect>(), Featured = true };
            int largest = 0;
            for (int seed = 0; seed < 40; seed++)
            {
                var hero = LayoutComposer.Compose(photos, 1280, 720, seed, null, 1, LayoutComposer.HeroSpotlight)!;
                Assert.True(hero.Cells.Single(c => c.Floating).Photo.Featured);
                var grid = LayoutComposer.Compose(photos, 1280, 720, seed, null, 1, LayoutComposer.EdgeToEdge)!;
                if (grid.Cells.OrderByDescending(c => c.W * c.H).First().Photo.Featured) largest++;
                var pile = LayoutComposer.Compose(photos, 1280, 720, seed, null, 1, LayoutComposer.ScatteredPrints)!;
                Assert.True(pile.Cells[^1].Photo.Featured);
            }
            Assert.True(largest >= 30, $"largest cell in {largest} of 40");
        }

        [Fact]
        public void A_soft_or_dark_picture_does_not_take_the_hero_place_from_a_good_one()
        {
            var face = new UnitRect(0.2, 0.2, 0.5, 0.5);
            var photos = Photos(5, 1600, 1200, new UnitRect(0.4, 0.3, 0.15, 0.15));
            photos[0] = photos[0] with { Faces = new[] { face }, Sharpness = 20 };
            photos[1] = photos[1] with { Faces = new[] { face }, Brightness = 0.05 };
            for (int seed = 0; seed < 30; seed++)
            {
                var hero = LayoutComposer.Compose(photos, 1280, 720, seed, null, 1, LayoutComposer.HeroSpotlight)!.Cells.Single(c => c.Floating).Photo;
                Assert.False(hero.Sharpness == 20 || hero.Brightness == 0.05, hero.Path);
            }
        }

        [Fact]
        public void The_gallery_card_can_be_chosen()
        {
            for (int seed = 0; seed < 12; seed++)
            {
                var white = LayoutComposer.Compose(Photos(6), 1280, 720, seed, null, 1, LayoutComposer.GalleryWall, new SurpriseOptions { Card = SurpriseOptions.CardWhite })!;
                Assert.Equal("#FFF8F8F8", white.CanvasColor);
                var black = LayoutComposer.Compose(Photos(6), 1280, 720, seed, null, 1, LayoutComposer.GalleryWall, new SurpriseOptions { Card = SurpriseOptions.CardBlack })!;
                Assert.Equal("#FF141414", black.CanvasColor);
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(6)]
        public void Slanted_panels_leave_no_bare_corner_or_gap_wider_than_asked(int count)
        {
            var made = LayoutComposer.Compose(Photos(count), 1280, 720, 1, null, 1, LayoutComposer.SlantedPanels)!;
            Assert.True(made.Cells[0].X <= -0.19 * made.Cells[0].W, "first panel starts on the canvas");
            var last = made.Cells[^1];
            Assert.True(last.X + 0.8 * last.W >= 1279.9, $"last panel ends at {last.X + 0.8 * last.W}");
            var first = made.Cells[0];
            double gap = made.Cells[1].X - (first.X + 0.8 * first.W);
            Assert.InRange(gap, 5, 12);
        }

        [Theory]
        [InlineData("a.jpg", false, 768, 976, 0.0, 0.34, true)]    // a chat capture: flat areas
        [InlineData("a.jpg", false, 900, 1600, 0.0, 0, true)]       // as tall as a phone
        [InlineData("a.png", false, 1920, 1080, 0.0, 0, true)]
        [InlineData("Screenshot_1.jpg", false, 1000, 800, 0.0, 0, true)]
        [InlineData("a.jpg", true, 1080, 1920, 0.0, 0.9, false)]    // camera details mean a photo
        [InlineData("a.jpg", false, 720, 1280, 0.04, 0, false)]     // a photo sent by message, with a large face
        [InlineData("a.jpg", false, 1280, 723, 0.04, 0.09, false)]
        [InlineData("a.jpg", false, 800, 600, 0.0, 0.02, false)]
        public void Screens_are_told_from_photos(string name, bool camera, double width, double height, double faceArea, double flat, bool expected)
        {
            var faces = faceArea > 0 ? new[] { new UnitRect(0.3, 0.3, Math.Sqrt(faceArea), Math.Sqrt(faceArea)) } : Array.Empty<UnitRect>();
            Assert.Equal(expected, LayoutComposer.LooksLikeScreen(name, camera, width, height, faces, flat));
        }

        [Theory]
        [InlineData(5, 5)]
        [InlineData(30, 30)]
        // The Duration slider sets any whole second, so 7 is kept; out of range is brought into it.
        [InlineData(7, 7)]
        [InlineData(1, 3)]
        [InlineData(500, 60)]
        [InlineData(-5, 0)]
        public void The_auto_play_interval_is_saved_and_a_bad_one_is_off(int saved, int expected)
        {
            var read = SurpriseOptions.FromJson(new SurpriseOptions { AutoPlaySeconds = saved }.ToJson());
            Assert.Equal(expected, read.AutoPlaySeconds);
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

        [Fact]
        public void Camera_subjects_project_face_coordinates_and_burst_filter_keeps_sharpest()
        {
            var photo = new PhotoFacts(@"C:\p\face.jpg", 1000, 1000, new List<UnitRect> { new(0.75, 0.15, 0.1, 0.1) }, 0.5, 0.5, "#000000");
            var cell = new ComposedCell { Photo = photo, X = 100, Y = 50, W = 400, H = 400, ImageWidth = 400, ImageHeight = 400, Frame = FrameLooks.Flush(), Geometry = new CellGeometryDto(), Floating = false };
            var pos = LayoutComposer.SubjectOnCanvas(cell, 1000, 1000);
            Assert.Equal((100 + 0.8 * 400) / 1000.0, pos.X, 3);
            Assert.Equal((50 + 0.2 * 400) / 1000.0, pos.Y, 3);

            var soft = Photo(0) with { Look = 0xF0F0, Sharpness = 80 };
            var sharp = Photo(1) with { Look = 0xF0F1, Sharpness = 950 };
            var other = Photo(2) with { Look = 0x00FF00FF, Sharpness = 400 };
            var kept = LayoutComposer.LeaveOutScreensAndRepeats(new List<PhotoFacts> { soft, sharp, other });
            Assert.Equal(new[] { @"C:\p\1.jpg", @"C:\p\2.jpg" }, kept.Select(p => p.Path));

            var deck = new PictureDeck(new[] { "a", "b", "c", "d" }, new Random(4));
            deck.Deal(2);
            var clone = deck.Clone();
            clone.Deal(2);
            Assert.Equal(2, deck.Deal(2).Count);
        }
    }
}

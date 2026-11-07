using System;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>A show file carries its own values (design section 7): version 2, and how a version 1 file is read.</summary>
    public class ShowValuesTests
    {
        // A version 1 file as v0.31.2 writes it: smart pace noted at draft time, the overrides by name, no values.
        private const string Version1 = """
        {
          "$schemaVersion": 1,
          "title": "Old show",
          "globalDefaults": { "defaultDwellSeconds": 12.5, "smartPace": false, "cameraMotionAmount": 0.5, "cameraMode": "Auto", "defaultTransition": "Crossfade" },
          "soundtrack": {},
          "scenes": [ { "id": "a", "recipe": "Edge to edge", "dwellSeconds": 11 } ],
          "GeneratorOverrides": "Cinematic"
        }
        """;

        [Fact]
        public void A_version_1_show_holds_no_values_and_follows_the_settings()
        {
            var show = ShowProject.FromJson(Version1.Replace("\"GeneratorOverrides\": \"Cinematic\"", "\"GeneratorOverrides\": null"))!;
            Assert.Equal(1, show.ReadAsVersion);
            Assert.False(show.GlobalDefaults.HoldsValues);
            // Its smart pace was a note of the setting when drafted, not the show's own: the settings still decide.
            var engine = new SurpriseOptions { SmartPace = true, FewestPictures = 2, MostPictures = 7 };
            var used = show.GlobalDefaults.ApplyTo(engine);
            Assert.True(used.SmartPace);
            Assert.Equal(7, used.MostPictures);
        }

        [Fact]
        public void A_version_1_cinematic_show_takes_the_apps_cinematic_values_once_on_open()
        {
            var show = ShowProject.FromJson(Version1)!;
            var app = new CinematicOverrides { MostPictures = 2, FewestSeconds = 6, MostSeconds = 9 };
            show.AdoptOnOpen(app);
            var used = show.GlobalDefaults.ApplyTo(new SurpriseOptions());
            Assert.Equal(2, used.MostPictures);
            Assert.Equal(6, used.RandomFewestSeconds);
            Assert.Equal(9, used.RandomMostSeconds);
            Assert.True(used.IsDesignOff(LayoutComposer.GalleryWall));
            // Its scenes are not touched.
            Assert.Equal(11, show.Scenes.Single().DwellSeconds);

            // Once: a second open, after the app's values changed, keeps what it took.
            show.AdoptOnOpen(new CinematicOverrides { MostPictures = 5 });
            Assert.Equal(2, show.GlobalDefaults.ApplyTo(new SurpriseOptions()).MostPictures);
        }

        [Fact]
        public void A_saved_show_is_version_2_and_reads_back_its_values()
        {
            var show = ShowProject.FromJson(Version1)!;
            show.AdoptOnOpen(new CinematicOverrides { FewestPictures = 1, MostPictures = 3 });
            string json = show.ToJson();
            Assert.Contains("\"$schemaVersion\": 2", json);

            var read = ShowProject.FromJson(json)!;
            Assert.Equal(2, read.ReadAsVersion);
            var used = read.GlobalDefaults.ApplyTo(new SurpriseOptions { MostPictures = 9 });
            Assert.Equal(3, used.MostPictures);
            Assert.False(used.IsDesignOff(LayoutComposer.EdgeToEdge));
            Assert.True(used.IsDesignOff(LayoutComposer.Circles));
        }

        [Fact]
        public void A_preset_saved_from_a_show_gives_another_show_the_same_values()
        {
            var playing = new SurpriseOptions { FewestPictures = 2, MostPictures = 5, SequenceBeatsChance = 0.25, FewestScenesPerStep = 2, MostScenesPerStep = 3, MotionLeast = 0.2, MotionMost = 0.9 };
            playing.DisabledDesigns.Add(LayoutComposer.Circles);
            var preset = CinematicOverrides.From(playing);

            var other = new ShowProject();
            other.GlobalDefaults.TakeValues(preset);
            var used = ShowProject.FromJson(other.ToJson())!.GlobalDefaults.ApplyTo(new SurpriseOptions());
            Assert.Equal(5, used.MostPictures);
            Assert.Equal(0.25, used.SequenceBeatsChance, 3);
            Assert.Equal(3, used.MostScenesPerStep);
            Assert.Equal(0.9, used.MotionMost, 3);
            Assert.True(used.IsDesignOff(LayoutComposer.Circles));
            Assert.False(used.IsDesignOff(LayoutComposer.EdgeToEdge));
        }

        [Fact]
        public void My_presets_are_three_slots_kept_in_the_settings()
        {
            var options = new SurpriseOptions();
            options.MyPresets[1] = new CinematicOverrides { MostPictures = 4, SequenceBeatsChance = 0.5 };
            var read = SurpriseOptions.FromJson(options.ToJson());
            Assert.Equal(3, read.MyPresets.Count);
            Assert.Null(read.MyPresets[0]);
            Assert.Equal(4, read.MyPresets[1]!.MostPictures);
            Assert.Equal(0.5, read.MyPresets[1]!.SequenceBeatsChance, 3);
            Assert.Null(read.MyPresets[2]);
            // A settings file from before presets has three empty slots.
            Assert.Equal(3, SurpriseOptions.FromJson("{}").MyPresets.Count);
        }

        [Fact]
        public void Show_card_values_are_kept_in_the_app_settings()
        {
            var options = new SurpriseOptions();
            options.MultiImage = new CinematicOverrides { SequenceBeatsChance = 1, FewestPictures = 1, MostPictures = 9, FewestScenesPerStep = 2, MostScenesPerStep = 2 };
            options.Cinematic.SequenceBeatsChance = 1;
            var read = SurpriseOptions.FromJson(options.ToJson());
            Assert.Equal(1, read.MultiImage!.SequenceBeatsChance, 3);
            Assert.Equal(9, read.MultiImage.MostPictures);
            Assert.Equal(1, read.Cinematic.SequenceBeatsChance, 3);

            var show = new ShowProject();
            show.GlobalDefaults.TakeValues(read.MultiImage);
            Assert.Equal(1, show.GlobalDefaults.ApplyTo(new SurpriseOptions()).SequenceBeatsChance, 3);
            // An old settings file has no saved Multi-Image preset: Play follows the engine settings.
            Assert.Null(SurpriseOptions.FromJson("{}").MultiImage);
        }

        [Fact]
        public void Cinematic_default_is_one_image_and_beats_at_a_quarter()
        {
            var factory = CinematicOverrides.Checked(new CinematicOverrides());
            Assert.Equal(1, factory.FewestPictures);
            Assert.Equal(1, factory.MostPictures);
            Assert.Equal(0.25, factory.SequenceBeatsChance, 3);
            Assert.Equal(2, factory.FewestScenesPerStep);
            Assert.Equal(2, factory.MostScenesPerStep);
            Assert.Equal(10, factory.FewestSeconds);
            Assert.Equal(15, factory.MostSeconds);
            Assert.True(factory.SmartPace);
            Assert.Equal(0.4, factory.MotionLeast, 3);
            Assert.Equal(0.6, factory.MotionMost, 3);
            Assert.Equal(SurpriseOptions.MostFavouredDesign, factory.OddsFor(LayoutComposer.EdgeToEdge));
            Assert.Equal(0.25, factory.OddsFor(LayoutComposer.HeroSpotlight));
            Assert.Equal(0, factory.OddsFor(LayoutComposer.GalleryWall));
            Assert.Equal(0, factory.OddsFor(LayoutComposer.ScatteredPrints));
            Assert.Equal(0, factory.OddsFor(LayoutComposer.Circles));
            Assert.Equal(0, factory.OddsFor(LayoutComposer.SlantedPanels));
        }

        [Fact]
        public void A_show_holding_nothing_writes_none_of_the_new_fields()
        {
            // So an older app reading it finds what it always found.
            string json = new ShowProject().ToJson();
            foreach (string field in new[] { "fewestImages", "mostImages", "designOdds", "fewestSeconds", "mostSeconds", "motionLeast", "motionMost", "smartPace", "sequenceBeats", "fewestScenesPerStep", "mostScenesPerStep" })
                Assert.DoesNotContain($"\"{field}\"", json);
        }
    }
}

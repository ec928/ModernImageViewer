using System;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>The one Layout Generator with a named set of overrides (Cinematic storyboard), and the pacing it relies on.</summary>
    public class GeneratorOverridesTests
    {
        [Fact]
        public void WithOverrides_Cinematic_ReplacesOnlyItsSettings_AndLeavesTheSavedOnesAlone()
        {
            var main = new SurpriseOptions { FewestPictures = 4, MostPictures = 9, MostZoom = 3, TransitionVariety = 0.8, Card = SurpriseOptions.CardBlack };

            var cine = main.WithOverrides(CinematicOverrides.Name);

            Assert.NotSame(main, cine);
            Assert.Equal(1, cine.FewestPictures);
            Assert.Equal(1, cine.MostPictures);
            Assert.Equal(0, cine.TransitionVariety);
            Assert.Equal(0, cine.SoloMattedOdds);
            Assert.True(cine.AutoPlayRandom);
            Assert.Equal(5, cine.RandomFewestSeconds);
            Assert.Equal(20, cine.RandomMostSeconds);
            // Not overridden: comes from the main settings.
            Assert.Equal(3, cine.MostZoom);
            Assert.Equal(SurpriseOptions.CardBlack, cine.Card);
            // The saved settings are unchanged.
            Assert.Equal(4, main.FewestPictures);
            Assert.Equal(0.8, main.TransitionVariety);

            Assert.Same(main, main.WithOverrides(null));
            Assert.Same(main, main.WithOverrides("Nonsense"));
        }

        [Fact]
        public void CinematicOverrides_AreSettings_SavedAndCheckedLikeTheRest()
        {
            var options = new SurpriseOptions();
            options.Cinematic.MostPictures = 3;
            options.Cinematic.FewestSeconds = 8;
            options.Cinematic.MostSeconds = 12;
            options.Cinematic.TransitionVariety = 0.4;

            var read = SurpriseOptions.FromJson(options.ToJson());
            Assert.Equal(3, read.Cinematic.MostPictures);
            Assert.Equal(8, read.Cinematic.FewestSeconds);
            Assert.Equal(12, read.Cinematic.MostSeconds);
            Assert.Equal(0.4, read.Cinematic.TransitionVariety);
            Assert.Equal(12, read.WithOverrides(CinematicOverrides.Name).RandomMostSeconds);

            // A file from before the overrides, or with them out of range.
            Assert.Equal(1, SurpriseOptions.FromJson("{}").Cinematic.MostPictures);
            var bad = SurpriseOptions.FromJson("{\"Cinematic\":{\"FewestPictures\":40,\"MostPictures\":2,\"TransitionVariety\":7}}");
            Assert.Equal(MosaicLayout.MaxShown, bad.Cinematic.FewestPictures);
            Assert.Equal(MosaicLayout.MaxShown, bad.Cinematic.MostPictures);
            Assert.Equal(1, bad.Cinematic.TransitionVariety);
        }

        [Fact]
        public void FromJson_KeepsADurationTheSliderSet_OffTheOldMenuLists()
        {
            var options = new SurpriseOptions { AutoPlayRandom = true, AutoPlaySeconds = 7, RandomFewestSeconds = 7, RandomMostSeconds = 12 };

            var read = SurpriseOptions.FromJson(options.ToJson());

            Assert.Equal(7, read.AutoPlaySeconds);
            Assert.Equal(7, read.RandomFewestSeconds);
            Assert.Equal(12, read.RandomMostSeconds);
        }

        [Fact]
        public void ShowProject_KeepsItsOverrides_ThroughSaveAndLoad()
        {
            var show = new ShowProject { GeneratorOverrides = CinematicOverrides.Name };

            var read = ShowProject.FromJson(show.ToJson());

            Assert.Equal(CinematicOverrides.Name, read!.GeneratorOverrides);
            Assert.Null(ShowProject.FromJson(new ShowProject().ToJson())!.GeneratorOverrides);
        }

        [Fact]
        public async System.Threading.Tasks.Task DraftSceneAsync_TakesTheOtherHalfOfTheRange_FromThePreviousScene()
        {
            var facts = new[] { new PhotoFacts(@"C:\p\a.jpg", 1600, 1200, Array.Empty<UnitRect>(), 0.5, 0.5, "#808080") };
            var options = new SurpriseOptions { FewestPictures = 1, MostPictures = 1, AutoPlayRandom = true, SmartPace = true, RandomFewestSeconds = 5, RandomMostSeconds = 20 };

            for (int run = 0; run < 20; run++)
            {
                var afterLong = await ShowDirector.DraftSceneAsync(facts, options, 1920, 1080, new ShowScene { DwellSeconds = 18 });
                var afterShort = await ShowDirector.DraftSceneAsync(facts, options, 1920, 1080, new ShowScene { DwellSeconds = 6 });
                Assert.InRange(afterLong!.DwellSeconds!.Value, 5, 12.5);
                Assert.InRange(afterShort!.DwellSeconds!.Value, 12.5, 20);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task CinematicScene_IsOneImage_FillingTheScreen_WithACrossfade()
        {
            var facts = Enumerable.Range(0, 5)
                .Select(i => new PhotoFacts($@"C:\p\{i}.jpg", 1920, 1080, Array.Empty<UnitRect>(), 0.5, 0.5, "#808080"))
                .ToList();
            var cine = new SurpriseOptions().WithOverrides(CinematicOverrides.Name);

            for (int run = 0; run < 20; run++)
            {
                var scene = await ShowDirector.DraftSceneAsync(facts, cine, 1920, 1080, previous: null);

                Assert.Single(scene!.GetPhotoPaths());
                Assert.Equal("Crossfade", scene.Transition);
                Assert.Equal(LayoutComposer.EdgeToEdge, scene.Recipe);
            }
        }
    }
}

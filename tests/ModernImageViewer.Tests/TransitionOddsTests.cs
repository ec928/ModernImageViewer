using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>Transitions as four odds against each other, the designs' pattern, in place of one capped "variety" slider.</summary>
    public class TransitionOddsTests
    {
        private static Dictionary<LayoutTransition, double> Run(SurpriseOptions options, int count, int seed)
        {
            var random = new Random(seed);
            var seen = new Dictionary<LayoutTransition, int>();
            LayoutTransition? last = null;
            for (int i = 0; i < count; i++)
            {
                var next = options.PickTransition(last, random);
                if (next != LayoutTransition.Crossfade) Assert.NotEqual(last, next);
                seen[next] = seen.GetValueOrDefault(next) + 1;
                last = next;
            }
            return seen.ToDictionary(p => p.Key, p => p.Value / (double)count);
        }

        [Fact]
        public void EachKindFollowsItsOdds_AndOffMeansNever()
        {
            var noSlide = new SurpriseOptions { CrossfadeOdds = 1, ZoomThroughOdds = 1, ThroughDarkOdds = 1, SlideOdds = 0 };
            Assert.False(Run(noSlide, 3000, 1).ContainsKey(LayoutTransition.Slide));

            var onlyDark = new SurpriseOptions { CrossfadeOdds = 0, ZoomThroughOdds = 0, ThroughDarkOdds = 1, SlideOdds = 0 };
            // Never twice running, and nothing else is on, so every other change is the crossfade it falls back to.
            var dark = Run(onlyDark, 2000, 2);
            Assert.InRange(dark[LayoutTransition.ThroughDark], 0.49, 0.51);

            var allOff = new SurpriseOptions { CrossfadeOdds = 0, ZoomThroughOdds = 0, ThroughDarkOdds = 0, SlideOdds = 0 };
            Assert.Equal(1.0, Run(allOff, 200, 3)[LayoutTransition.Crossfade]);

            // With crossfades off, 100% of changes are something else: no hidden cap.
            var noCrossfade = new SurpriseOptions { CrossfadeOdds = 0, ZoomThroughOdds = 1, ThroughDarkOdds = 1, SlideOdds = 1 };
            Assert.False(Run(noCrossfade, 3000, 4).ContainsKey(LayoutTransition.Crossfade));
        }

        [Fact]
        public void TheMixLine_MatchesWhatIsPicked()
        {
            foreach (var options in new[]
            {
                new SurpriseOptions(),
                new SurpriseOptions { CrossfadeOdds = 0.3, ZoomThroughOdds = 2, ThroughDarkOdds = 1, SlideOdds = 0.5 },
                new SurpriseOptions { CrossfadeOdds = 0, ZoomThroughOdds = 0, ThroughDarkOdds = 1, SlideOdds = 0 }
            })
            {
                var predicted = options.TransitionShares();
                var seen = Run(options, 40000, 5);
                foreach (var kind in predicted.Keys)
                    Assert.InRange(seen.GetValueOrDefault(kind), predicted[kind] - 0.015, predicted[kind] + 0.015);
            }
        }

        [Fact]
        public void Defaults_KeepCrossfadeMostCommon_AndSlideRarest()
        {
            var shares = new SurpriseOptions().TransitionShares();
            Assert.True(shares[LayoutTransition.Crossfade] > 0.5);
            Assert.True(shares[LayoutTransition.Slide] < shares[LayoutTransition.ZoomThrough]);
            Assert.True(shares[LayoutTransition.Slide] > 0);
        }

        [Theory]
        [InlineData(0.5, 0.35)]
        [InlineData(1.0, 0.70)]
        [InlineData(0.2, 0.14)]
        public void ASavedVariety_BecomesOddsGivingAboutTheSameMix(double variety, double otherShare)
        {
            var read = SurpriseOptions.FromJson($"{{\"TransitionVariety\":{variety.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            double other = 1 - read.TransitionShares()[LayoutTransition.Crossfade];
            Assert.InRange(other, otherShare - 0.04, otherShare + 0.04);
            // Converted once: the old setting is not written back.
            Assert.DoesNotContain("TransitionVariety", read.ToJson());
            Assert.Null(SurpriseOptions.FromJson(read.ToJson()).TransitionVariety);
        }

        [Fact]
        public void ASavedVarietyOfZero_IsCrossfadeOnly_AndNewOddsSurviveSaving()
        {
            var zero = SurpriseOptions.FromJson("{\"TransitionVariety\":0}");
            Assert.Equal(1.0, zero.TransitionShares()[LayoutTransition.Crossfade], 6);

            var odds = new SurpriseOptions { CrossfadeOdds = 0.5, ZoomThroughOdds = 3, ThroughDarkOdds = 0, SlideOdds = 9 };
            var read = SurpriseOptions.FromJson(odds.ToJson());
            Assert.Equal(0.5, read.CrossfadeOdds);
            Assert.Equal(3, read.ZoomThroughOdds);
            Assert.Equal(0, read.ThroughDarkOdds);
            Assert.Equal(SurpriseOptions.MostFavouredDesign, read.SlideOdds);
        }

        [Fact]
        public async System.Threading.Tasks.Task ADraftedScene_TakesItsTransitionFromTheOdds()
        {
            var facts = new[] { new PhotoFacts(@"C:\p\a.jpg", 1600, 1200, Array.Empty<UnitRect>(), 0.5, 0.5, "#808080") };
            var options = new SurpriseOptions { FewestPictures = 1, MostPictures = 1, CrossfadeOdds = 0, ZoomThroughOdds = 0, ThroughDarkOdds = 1, SlideOdds = 0 };
            var scene = await ShowDirector.DraftSceneAsync(facts, options, 1920, 1080, previous: new ShowScene { Transition = "Crossfade" });
            Assert.Equal(nameof(LayoutTransition.ThroughDark), scene!.Transition);
        }
    }
}

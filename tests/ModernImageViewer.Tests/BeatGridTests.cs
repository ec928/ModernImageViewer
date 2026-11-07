using System;
using System.Linq;
using ModernImageViewer.Cinematic;
using Xunit;
using Xunit.Abstractions;

namespace ModernImageViewer.Tests
{
    /// <summary>Cut on the music (design 3.10): beats found where there are beats, and none trusted where there are not.</summary>
    public class BeatGridTests
    {
        private readonly ITestOutputHelper _out;
        public BeatGridTests(ITestOutputHelper output) => _out = output;

        private const int Rate = BeatGrid.SampleRate;

        private static float[] Noise(double seconds, double level, int seed)
        {
            var random = new Random(seed);
            var s = new float[(int)(seconds * Rate)];
            for (int i = 0; i < s.Length; i++) s[i] = (float)((random.NextDouble() * 2 - 1) * level);
            return s;
        }

        /// <summary>A short decaying tone burst at each time, over quiet noise. Every <paramref name="accentEvery"/>th one
        /// (from the first) also has a kick: a low thump, as a bar's first beat has.</summary>
        private static float[] Clicks(double seconds, double[] times, int seed, int accentEvery = 0, double level = 0.8)
        {
            var s = Noise(seconds, 0.02, seed);
            for (int k = 0; k < times.Length; k++)
            {
                int at = (int)(times[k] * Rate);
                bool kick = accentEvery > 0 && k % accentEvery == 0;
                for (int i = 0; i < 1600 && at + i < s.Length; i++)
                {
                    double v = i < 800 ? level * Math.Exp(-i / 150.0) * Math.Sin(2 * Math.PI * 880 * i / Rate) : 0;
                    if (kick) v += 0.9 * Math.Exp(-i / 500.0) * Math.Sin(2 * Math.PI * 60 * i / Rate);
                    s[at + i] += (float)v;
                }
            }
            return s;
        }

        [Fact]
        public void Bars_start_on_the_kick_and_are_not_trusted_without_one()
        {
            // Four clicks a bar, the first of each with a kick, starting on the second click: bar starts are clicks 1, 5, 9...
            var times = Enumerable.Range(0, 64).Select(i => 0.3 + i * 0.5).ToArray();
            var s = Clicks(33, times.Skip(1).ToArray(), 8, accentEvery: 4);
            var grid = BeatGrid.Find(s);
            _out.WriteLine($"accented: bars {grid.BarStrength:0.00}, agree {grid.BarsAgree}, changes {grid.BarChanges}, {grid.Downbeats.Count} bar starts");
            Assert.True(grid.BarsUsable);
            var kicks = times.Skip(1).Where((_, k) => k % 4 == 0).ToArray();
            var inside = grid.Downbeats.Where(d => d > kicks[0] - 0.25 && d < kicks[^1] + 0.25).ToList();
            Assert.All(inside, d => Assert.True(kicks.Min(t => Math.Abs(t - d)) < 0.04, $"bar start at {d:0.000}"));
            Assert.True(inside.Count >= kicks.Length - 1);

            var plain = BeatGrid.Find(Clicks(33, times, 9));
            _out.WriteLine($"plain: bars {plain.BarStrength:0.00}, agree {plain.BarsAgree}");
            Assert.False(plain.BarsUsable);
            Assert.True(plain.Usable);
        }

        /// <summary>Steady clicks whose loudness follows <paramref name="level"/> over time (seconds to 0..1).</summary>
        private static float[] Shaped(double seconds, Func<double, double> level, int seed)
        {
            var times = Enumerable.Range(0, (int)(seconds * 2) - 1).Select(i => 0.3 + i * 0.5).ToArray();
            var s = Clicks(seconds, times, seed, level: 1.0);
            var random = new Random(seed + 100);
            for (int i = 0; i < s.Length; i++)
            {
                double l = level(i / (double)Rate);
                s[i] = (float)(s[i] * l + (random.NextDouble() * 2 - 1) * 0.05 * l);
            }
            return s;
        }

        [Fact]
        public void A_sudden_lift_is_a_big_moment_and_a_slow_build_or_steady_music_is_not()
        {
            var sudden = BeatGrid.Find(Shaped(70, t => t < 35 ? 0.1 : 0.9, 12));
            var build = BeatGrid.Find(Shaped(70, t => 0.1 + 0.8 * t / 70, 13));
            var steady = BeatGrid.Find(Shaped(70, _ => 0.8, 14));
            _out.WriteLine($"sudden: {string.Join(" ", sudden.Lifts.Select(l => l.ToString("0.00")))}; build: {build.Lifts.Count}; steady: {steady.Lifts.Count}");
            Assert.Single(sudden.Lifts);
            Assert.InRange(sudden.Lifts[0], 34.5, 35.6);
            Assert.Contains(sudden.Beats, b => Math.Abs(b - sudden.Lifts[0]) < 0.001);
            Assert.Empty(build.Lifts);
            Assert.Empty(steady.Lifts);
        }

        [Fact]
        public void A_big_moment_in_reach_takes_the_change_as_a_hard_cut_by_its_odds()
        {
            var grid = BeatGrid.Find(Shaped(70, t => t < 35 ? 0.1 : 0.9, 15));
            _out.WriteLine($"confidence {grid.Confidence:0.0}, lifts {string.Join(" ", grid.Lifts.Select(l => l.ToString("0.00")))}");
            Assert.True(grid.Usable);
            double lift = grid.Lifts.Single();
            // Scene began 6 s before the lift, Duration 10 to 15 s: the lift is early but past 3 s, so it takes the change.
            double wait = BeatGrid.WaitBeforeChange(grid, lift - 6, 12, 10, 15, 0.2, true, 0, out var landing, hardCuts: 1, roll: 0.5);
            Assert.Equal(BeatGrid.Landing.HardCut, landing);
            Assert.Equal(lift, lift - 6 + wait + 0.2, 3);
            // The roll above the odds, or the odds at Off: no cut.
            BeatGrid.WaitBeforeChange(grid, lift - 6, 12, 10, 15, 0.2, true, 0, out landing, hardCuts: 0.4, roll: 0.5);
            Assert.NotEqual(BeatGrid.Landing.HardCut, landing);
            BeatGrid.WaitBeforeChange(grid, lift - 6, 12, 10, 15, 0.2, true, 0, out landing, hardCuts: 0, roll: 0);
            Assert.NotEqual(BeatGrid.Landing.HardCut, landing);
            // A lift under three seconds into the scene is too soon; one more than three seconds past Duration's longest is
            // out of reach. One just past the longest is reached by holding a little longer: else it would come under three
            // seconds into the next scene and no scene would take it (seen in the app, 7 Nov).
            BeatGrid.WaitBeforeChange(grid, lift - 2, 12, 10, 15, 0.2, true, 0, out landing, hardCuts: 1, roll: 0);
            Assert.NotEqual(BeatGrid.Landing.HardCut, landing);
            BeatGrid.WaitBeforeChange(grid, lift - 20, 12, 10, 15, 0.2, true, 0, out landing, hardCuts: 1, roll: 0);
            Assert.NotEqual(BeatGrid.Landing.HardCut, landing);
            BeatGrid.WaitBeforeChange(grid, lift - 15.2, 12, 10, 15, 0.2, true, 0, out landing, hardCuts: 1, roll: 0);
            Assert.Equal(BeatGrid.Landing.HardCut, landing);
        }

        [Fact]
        public void Following_the_music_holds_shorter_when_loud_and_longer_when_quiet()
        {
            // Sixty seconds of steady clicks: quiet for the first half, loud for the second.
            var times = Enumerable.Range(0, 120).Select(i => 0.3 + i * 0.5).ToArray();
            var quiet = Clicks(62, times, 10, level: 0.08);
            var loud = Clicks(62, times, 10, level: 0.8);
            var s = quiet.Take(31 * Rate).Concat(loud.Skip(31 * Rate)).ToArray();
            var grid = BeatGrid.Find(s);
            Assert.True(grid.EnergyBetween(2, 12) < 0.3, $"quiet part {grid.EnergyBetween(2, 12)}");
            Assert.True(grid.EnergyBetween(40, 50) > 0.7, $"loud part {grid.EnergyBetween(40, 50)}");
            double inQuiet = BeatGrid.WaitBeforeChange(grid, 2.0, 12, 8, 16, 0, cutOnBeat: false, follow: 1, out _);
            double inLoud = BeatGrid.WaitBeforeChange(grid, 40.0, 12, 8, 16, 0, cutOnBeat: false, follow: 1, out _);
            double off = BeatGrid.WaitBeforeChange(grid, 40.0, 12, 8, 16, 0, cutOnBeat: false, follow: 0, out _);
            _out.WriteLine($"follow 100%: quiet {inQuiet:0.0} s, loud {inLoud:0.0} s; follow 0: {off:0.0} s");
            Assert.True(inQuiet > 14, $"quiet {inQuiet}");
            Assert.True(inLoud < 10, $"loud {inLoud}");
            Assert.Equal(12, off, 3);
        }

        [Fact]
        public void A_steady_click_track_gives_its_tempo_and_beats_on_the_clicks()
        {
            var times = Enumerable.Range(0, 60).Select(i => 0.3 + i * 0.5).ToArray();
            var grid = BeatGrid.Find(Clicks(31, times, 1));
            _out.WriteLine($"clicks: {grid.Bpm:0.0} bpm, confidence {grid.Confidence:0.00}");
            Assert.InRange(grid.Bpm, 118, 122);
            Assert.True(grid.Usable);
            // Each beat found among the clicks is within 40 ms of one (a picture change 40 ms off the beat is not seen as
            // off it), and every click has a beat.
            var inside = grid.Beats.Where(b => b > times[0] - 0.25 && b < times[^1] + 0.25).ToList();
            _out.WriteLine($"largest miss {inside.Max(b => times.Min(t => Math.Abs(t - b))) * 1000:0} ms");
            Assert.All(inside, b => Assert.True(times.Min(t => Math.Abs(t - b)) < 0.04, $"beat at {b:0.000}"));
            Assert.Equal(times.Length, inside.Count);
        }

        [Fact]
        public void Noise_is_not_cut_to()
        {
            // Clicks at random times are no longer refused: real songs with an obvious beat scored below them, and a beat
            // trusted wrongly moves a change at most half a beat (7 Nov, 60-song samples). Noise still is.
            var noise = BeatGrid.Find(Noise(30, 0.3, 2));
            _out.WriteLine($"noise: confidence {noise.Confidence:0.00}");
            Assert.False(noise.Usable);
        }

        [Fact]
        public void A_song_whose_beats_are_not_trusted_still_has_its_energy_and_big_moments()
        {
            // Noise with no beat, quiet then suddenly loud: not cut on a beat, but followed and hard-cut on its lift.
            var random = new Random(16);
            var s = new float[70 * Rate];
            for (int i = 0; i < s.Length; i++) s[i] = (float)((random.NextDouble() * 2 - 1) * (i < 35 * Rate ? 0.03 : 0.5));
            var grid = BeatGrid.Find(s);
            _out.WriteLine($"confidence {grid.Confidence:0.00}, heard {grid.Heard}, lifts {string.Join(" ", grid.Lifts.Select(l => l.ToString("0.00")))}");
            Assert.False(grid.Usable);
            Assert.True(grid.Heard);
            Assert.True(grid.EnergyBetween(5, 15) < 0.3 && grid.EnergyBetween(45, 55) > 0.7);
            double lift = Assert.Single(grid.Lifts);
            Assert.InRange(lift, 34, 36.5);
            double wait = BeatGrid.WaitBeforeChange(grid, lift - 6, 12, 10, 15, 0.2, true, 0, out var landing, hardCuts: 1, roll: 0);
            Assert.Equal(BeatGrid.Landing.HardCut, landing);
            // Not cut on a beat: with no big moment in reach, the plan stands.
            BeatGrid.WaitBeforeChange(grid, 5, 12, 10, 15, 0.2, true, 0, out landing, hardCuts: 0, roll: 1);
            Assert.NotEqual(BeatGrid.Landing.Beat, landing);
            Assert.NotEqual(BeatGrid.Landing.BarStart, landing);
        }

        [Fact]
        public void Silence_and_a_very_short_sound_give_no_beats()
        {
            Assert.False(BeatGrid.Find(new float[Rate * 20]).Usable);
            Assert.False(BeatGrid.Find(new float[Rate / 2]).Usable);
            Assert.False(BeatGrid.Find(Array.Empty<float>()).Usable);
        }

        [Fact]
        public void The_wait_puts_the_change_on_the_beat_and_falls_back_to_the_plan()
        {
            var times = Enumerable.Range(0, 60).Select(i => 0.3 + i * 0.5).ToArray();
            var grid = BeatGrid.Find(Clicks(31, times, 6));
            // Song at 5.0 s, 10 s planned, Duration 8 to 15 s, the change takes 0.3 s: the beat nearest 15.0 s is 15.3 s,
            // so wait 15.3 - 5.0 - 0.3 = 10.0 s and the change starts on it.
            double Wait(BeatGrid? g, double? now, double plan, double lo, double hi) => BeatGrid.WaitBeforeChange(g, now, plan, lo, hi, 0.3, true, 0, out _);
            double wait = Wait(grid, 5.0, 10, 8, 15);
            double changeAt = 5.0 + wait + 0.3;
            Assert.True(times.Min(t => Math.Abs(t - changeAt)) < 0.04, $"change at {changeAt:0.000}");
            Assert.InRange(changeAt - 5.0, 9.7, 10.3);
            // No beats, no position, one fixed duration, a window past the song's end: the plan.
            Assert.Equal(10, Wait(null, 5.0, 10, 8, 15));
            Assert.Equal(10, Wait(grid, null, 10, 8, 15));
            Assert.Equal(10, Wait(grid, 5.0, 10, 10, 10));
            Assert.Equal(10, Wait(grid, 40.0, 10, 8, 15));
            Assert.Equal(10, Wait(BeatGrid.Find(Noise(30, 0.3, 7)), 5.0, 10, 8, 15));

            // With bars, the change lands on a bar's first beat, a whole number of bars from a scene that began on one.
            var bars = BeatGrid.Find(Clicks(33, times.Skip(1).ToArray(), 11, accentEvery: 4));
            var kicks = times.Skip(1).Where((_, k) => k % 4 == 0).ToArray();
            double start = kicks[2];
            double barWait = BeatGrid.WaitBeforeChange(bars, start, 10, 8, 15, 0.3, true, 0, out var landing);
            double barAt = start + barWait + 0.3;
            Assert.Equal(BeatGrid.Landing.BarStart, landing);
            Assert.True(kicks.Min(t => Math.Abs(t - barAt)) < 0.04, $"bar change at {barAt:0.000}");
        }

        [Fact]
        public void The_nearest_beat_is_taken_only_inside_the_window()
        {
            var times = Enumerable.Range(0, 60).Select(i => 0.3 + i * 0.5).ToArray();
            var grid = BeatGrid.Find(Clicks(31, times, 5));
            var near = grid.NearestBeat(planned: 12.1, earliest: 10, latest: 15);
            Assert.NotNull(near);
            Assert.InRange(near!.Value, 12.25, 12.35);
            Assert.Null(grid.NearestBeat(planned: 12.1, earliest: 12.36, latest: 12.70));
            Assert.Null(BeatGrid.None.NearestBeat(12, 10, 15));
        }
    }
}

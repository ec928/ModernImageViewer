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

        /// <summary>A short decaying tone burst at each time, over quiet noise.</summary>
        private static float[] Clicks(double seconds, double[] times, int seed)
        {
            var s = Noise(seconds, 0.02, seed);
            foreach (double t in times)
            {
                int at = (int)(t * Rate);
                for (int i = 0; i < 800 && at + i < s.Length; i++)
                    s[at + i] += (float)(0.8 * Math.Exp(-i / 150.0) * Math.Sin(2 * Math.PI * 880 * i / Rate));
            }
            return s;
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
        public void Noise_and_clicks_with_no_tempo_are_not_cut_to()
        {
            var noise = BeatGrid.Find(Noise(30, 0.3, 2));
            var random = new Random(3);
            double t = 0.2;
            var loose = new System.Collections.Generic.List<double>();
            while (t < 29) { loose.Add(t); t += 0.25 + random.NextDouble() * 1.2; }
            var unsteady = BeatGrid.Find(Clicks(30, loose.ToArray(), 4));
            _out.WriteLine($"noise: confidence {noise.Confidence:0.00}; unsteady clicks: confidence {unsteady.Confidence:0.00}, {unsteady.Bpm:0.0} bpm");
            Assert.False(noise.Usable);
            Assert.False(unsteady.Usable);
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
            double wait = BeatGrid.WaitBeforeChange(grid, 5.0, 10, 8, 15, 0.3);
            double changeAt = 5.0 + wait + 0.3;
            Assert.True(times.Min(t => Math.Abs(t - changeAt)) < 0.04, $"change at {changeAt:0.000}");
            Assert.InRange(changeAt - 5.0, 9.7, 10.3);
            // No beats, no position, one fixed duration, a window past the song's end: the plan.
            Assert.Equal(10, BeatGrid.WaitBeforeChange(null, 5.0, 10, 8, 15, 0.3));
            Assert.Equal(10, BeatGrid.WaitBeforeChange(grid, null, 10, 8, 15, 0.3));
            Assert.Equal(10, BeatGrid.WaitBeforeChange(grid, 5.0, 10, 10, 10, 0.3));
            Assert.Equal(10, BeatGrid.WaitBeforeChange(grid, 40.0, 10, 8, 15, 0.3));
            Assert.Equal(10, BeatGrid.WaitBeforeChange(BeatGrid.Find(Noise(30, 0.3, 7)), 5.0, 10, 8, 15, 0.3));
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

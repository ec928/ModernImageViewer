using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer.Cinematic
{
    /// <summary>
    /// The beats, bars and energy of one song, found from its sound alone (design 3.10). Beats: an onset strength from how
    /// much the spectrum rises frame to frame, a tempo from how that strength repeats, and beats placed on it by dynamic
    /// programming. Bars: which beat of every four is the strong one, weighted to the bass, where a kick lands; trusted only
    /// when it stands out and the count changes rarely. Energy: loudness, smoothed over seconds, from the song's own
    /// quiet (0) to its own loud (1). Free-time music and speech score low on confidence and are not cut to.
    /// </summary>
    public sealed class BeatGrid
    {
        public const int SampleRate = 11025;
        private const int Window = 1024, Hop = 128;
        private const double FrameSeconds = Hop / (double)SampleRate;
        // A frame's time is the middle of its window, where an onset in it is heard; its start put beats 70 ms early.
        private const double Middle = Window / 2.0 / SampleRate;
        private const int BeatsInABar = 4;
        private const double EnergyStep = 0.5;
        // An energy block is a whole number of frames; this is its real length, which gives its time.
        private static readonly double EnergyBlock = Math.Max(1, (int)(EnergyStep / FrameSeconds)) * FrameSeconds;

        /// <summary>
        /// Below this, the beats are not used: the scene holds as planned. Set from real songs (design check M1, 7 Nov):
        /// eight songs from the music folder scored 3.4 to 6.6; white noise 1.1 and clicks at random times 2.7.
        /// </summary>
        public const double LeastConfidence = 3.0;

        /// <summary>How much the strongest beat of the four must stand above the others to count as the bar's first.</summary>
        public const double LeastBarStrength = 1.12;

        public IReadOnlyList<double> Beats { get; }
        /// <summary>The first beat of each bar, when <see cref="BarsUsable"/>.</summary>
        public IReadOnlyList<double> Downbeats { get; }
        public double Bpm { get; }
        public double Confidence { get; }
        public double BarStrength { get; }
        public bool BarsAgree { get; }
        /// <summary>How many times the count of beats in a bar moved (a beat skipped or added), for the timing log.</summary>
        public int BarChanges { get; private set; }
        private readonly double[] _energy;

        public bool Usable => Confidence >= LeastConfidence && Beats.Count >= 8;
        public bool BarsUsable => Usable && BarsAgree && BarStrength >= LeastBarStrength && Downbeats.Count >= 4;

        private BeatGrid(IReadOnlyList<double> beats, IReadOnlyList<double> downbeats, double bpm, double confidence, double barStrength, bool barsAgree, double[] energy)
        {
            Beats = beats;
            Downbeats = downbeats;
            Bpm = bpm;
            Confidence = confidence;
            BarStrength = barStrength;
            BarsAgree = barsAgree;
            _energy = energy;
        }

        public static readonly BeatGrid None = new(Array.Empty<double>(), Array.Empty<double>(), 0, 0, 0, false, Array.Empty<double>());

        /// <summary>The beat nearest <paramref name="planned"/> seconds, if one lies between the two limits.</summary>
        public double? NearestBeat(double planned, double earliest, double latest) => Usable ? Nearest(Beats, planned, earliest, latest) : null;

        /// <summary>The first beat of a bar nearest <paramref name="planned"/>, if the bars are trusted and one lies between the limits.</summary>
        public double? NearestDownbeat(double planned, double earliest, double latest) => BarsUsable ? Nearest(Downbeats, planned, earliest, latest) : null;

        private static double? Nearest(IReadOnlyList<double> times, double planned, double earliest, double latest)
        {
            if (latest < earliest) return null;
            double? best = null;
            foreach (double time in times)
            {
                if (time < earliest) continue;
                if (time > latest) break;
                if (best == null || Math.Abs(time - planned) < Math.Abs(best.Value - planned)) best = time;
            }
            return best;
        }

        /// <summary>The song's energy from <paramref name="from"/> to <paramref name="to"/> seconds: 0 its quietest, 1 its loudest. Null past its end.</summary>
        public double? EnergyBetween(double from, double to)
        {
            if (_energy.Length == 0 || !Usable) return null;
            int a = Math.Max(0, (int)(from / EnergyBlock)), b = Math.Min(_energy.Length - 1, (int)(to / EnergyBlock));
            if (a >= _energy.Length || b < a) return null;
            double sum = 0;
            for (int i = a; i <= b; i++) sum += _energy[i];
            return sum / (b - a + 1);
        }

        /// <summary>How a scene's hold was chosen. <see cref="Landing.HardCut"/>: on a big moment, and the change is a cut.</summary>
        public enum Landing { AsPlanned, Beat, BarStart, HardCut }

        /// <summary>A scene takes a big moment from this far in, even before Duration's shortest: the moment is worth more.</summary>
        public const double EarliestHardCut = 3;

        /// <summary>
        /// For a scene just shown: how long to wait before starting the next change (design 3.10). A big moment of the
        /// song (<see cref="Lifts"/>) within reach takes the change, as a hard cut, by the odds <paramref name="hardCuts"/>
        /// against <paramref name="roll"/> (both 0 to 1). Otherwise Follow the music moves the planned end within Duration
        /// by the song's energy over the hold, toward the shortest when loud and the longest when quiet, by
        /// <paramref name="follow"/> (0 to 1), and Cut on the beat puts the change on the first beat of the bar nearest that
        /// end, or the nearest beat when the bars are not trusted. The change starts <paramref name="changeDelay"/> after the
        /// wait ends, so the wait is that much shorter. With no beats or no song position: the planned hold.
        /// </summary>
        public static double WaitBeforeChange(BeatGrid? beats, double? songNow, double plannedHold, double shortest, double longest,
            double changeDelay, bool cutOnBeat, double follow, out Landing landing, double hardCuts = 0, double roll = 1)
        {
            landing = Landing.AsPlanned;
            if (beats == null || !beats.Usable || songNow is not double now) return plannedHold;
            bool fixedHold = longest - shortest < 0.01;
            double low = fixedHold ? plannedHold : Math.Min(shortest, plannedHold), high = fixedHold ? plannedHold : Math.Max(longest, plannedHold);

            if (hardCuts > 0 && roll < hardCuts)
            {
                // A scene may run up to EarliestHardCut past Duration's longest to reach a big moment: one just out of its
                // reach would come under EarliestHardCut into the next scene, too soon for either, and be lost.
                double from = now + Math.Max(EarliestHardCut, 0.4 * low), to = now + high + EarliestHardCut;
                foreach (double lift in beats.Lifts)
                {
                    if (lift < from) continue;
                    if (lift > to) break;
                    double cutWait = lift - now - changeDelay;
                    if (cutWait < 1) break;
                    landing = Landing.HardCut;
                    return cutWait;
                }
            }
            if (fixedHold) return plannedHold;

            double target = plannedHold;
            if (follow > 0 && beats.EnergyBetween(now, now + plannedHold) is double energy)
            {
                double byEnergy = longest - energy * (longest - shortest);
                target = plannedHold + Math.Clamp(follow, 0, 1) * (byEnergy - plannedHold);
            }
            if (!cutOnBeat)
            {
                double plain = target - changeDelay;
                return plain < 1 ? plannedHold : plain;
            }

            double end;
            if (beats.NearestDownbeat(now + target, now + low, now + high) is double bar) { end = bar; landing = Landing.BarStart; }
            else if (beats.NearestBeat(now + target, now + low, now + high) is double beat) { end = beat; landing = Landing.Beat; }
            else { landing = Landing.AsPlanned; return target < 1 ? plannedHold : target; }
            double wait = end - now - changeDelay;
            if (wait < 1) { landing = Landing.AsPlanned; return plannedHold; }
            return wait;
        }

        /// <summary>Beats from mono samples at <see cref="SampleRate"/>.</summary>
        public static BeatGrid Find(float[] samples)
        {
            var (onset, bass, loudness) = Measure(samples);
            if (onset.Length < 200) return None;

            // Tempo: the lag at which the onsets best repeat, from 60 to 180 beats a minute, leaning toward 120 as listeners do.
            int lowLag = (int)Math.Round(60.0 / 180 / FrameSeconds), highLag = (int)Math.Round(60.0 / 60 / FrameSeconds);
            double bestScore = double.MinValue;
            int period = 0;
            var repeat = new double[highLag + 1];
            for (int lag = lowLag; lag <= highLag; lag++)
            {
                double sum = 0;
                for (int i = lag; i < onset.Length; i++) sum += onset[i] * onset[i - lag];
                repeat[lag] = sum / (onset.Length - lag);
                double bpm = 60.0 / (lag * FrameSeconds);
                double lean = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120.0) / 0.9, 2));
                double score = repeat[lag] * lean;
                if (score > bestScore) { bestScore = score; period = lag; }
            }
            if (period == 0) return None;
            // Confidence: how much more the onsets repeat at the tempo than at an average lag. Steady music stands well
            // above 1; noise, speech and loose clicks repeat about as much at any lag. (How strong the onsets on the beats
            // are says nothing: any clicks are strong, steady or not.)
            double meanRepeat = 0;
            for (int lag = lowLag; lag <= highLag; lag++) meanRepeat += repeat[lag];
            meanRepeat /= highLag - lowLag + 1;
            double confidence = meanRepeat <= 0 ? 0 : repeat[period] / meanRepeat;

            // Beats: each frame's best score is its onset plus the best earlier beat about one period back, less a cost for
            // straying from the period. The last strong beat is traced back.
            int n = onset.Length;
            var total = new double[n];
            var from = new int[n];
            const double tightness = 100;
            for (int t = 0; t < n; t++)
            {
                double best = 0;
                int bestFrom = -1;
                for (int prev = t - 2 * period; prev <= t - period / 2; prev++)
                {
                    if (prev < 0) continue;
                    double stray = Math.Log((t - prev) / (double)period);
                    double s = total[prev] - tightness * stray * stray;
                    if (bestFrom < 0 || s > best) { best = s; bestFrom = prev; }
                }
                total[t] = onset[t] + (bestFrom >= 0 ? best : 0);
                from[t] = bestFrom;
            }
            int end = n - 1;
            for (int t = n - 1; t >= Math.Max(0, n - period); t--) if (total[t] > total[end]) end = t;
            var frames = new List<int>();
            for (int t = end; t >= 0; t = from[t])
            {
                frames.Add(t);
                if (from[t] < 0) break;
            }
            frames.Reverse();
            // The period is a whole number of frames, so beats can slip a frame against the music: each settles on the
            // strongest onset within two frames of it.
            for (int i = 0; i < frames.Count; i++)
            {
                int at = frames[i];
                for (int f = Math.Max(0, at - 2); f <= Math.Min(n - 1, at + 2); f++)
                    if (onset[f] > onset[frames[i]]) frames[i] = f;
            }

            var (downbeats, barStrength, barsAgree, barChanges) = Bars(frames, onset, bass);
            return new BeatGrid(frames.Select(Seconds).ToList(), downbeats.Select(Seconds).ToList(), 60.0 / (period * FrameSeconds),
                confidence, barStrength, barsAgree, Energy(loudness)) { BarChanges = barChanges }
                .WithLifts(loudness, frames.Select(Seconds).ToList(), barsAgree && barStrength >= LeastBarStrength ? downbeats.Select(Seconds).ToList() : null);
        }

        private static double Seconds(int frame) => frame * FrameSeconds + Middle;

        /// <summary>The song's big moments: where it suddenly gets much louder, such as a chorus kicking in. On a beat.</summary>
        public IReadOnlyList<double> Lifts { get; private set; } = Array.Empty<double>();

        // A big moment: the next two seconds this much louder than the four before, on the song's own scale from its quiet
        // (0) to its loud (1), and loud after it. A slow build rises too little in four seconds; steady music not at all.
        public const double LeastLift = 0.35, LoudAfterLift = 0.6, LiftsApart = 15;
        private const double LiftStep = 0.25;

        private BeatGrid WithLifts(double[] loudness, List<double> beats, List<double>? downbeats)
        {
            int per = Math.Max(1, (int)(LiftStep / FrameSeconds));
            int steps = loudness.Length / per;
            if (steps < 40 || beats.Count == 0) return this;
            var level = new double[steps];
            for (int s = 0; s < steps; s++)
            {
                double sum = 0;
                for (int f = s * per; f < (s + 1) * per; f++) sum += loudness[f];
                level[s] = Math.Log(1e-6 + sum / per);
            }
            var sorted = level.OrderBy(v => v).ToArray();
            double quiet = sorted[sorted.Length / 10], loud = sorted[sorted.Length * 9 / 10];
            if (loud - quiet < 0.5) return this;
            for (int s = 0; s < steps; s++) level[s] = Math.Clamp((level[s] - quiet) / (loud - quiet), 0, 1.2);

            // A block is a whole number of frames: its real length, not LiftStep, gives its time (0.25 s was 2.5% off,
            // nearly a second by 35 s into a song).
            double block = per * FrameSeconds;
            int before = (int)(4 / block), after = (int)(2 / block);
            var rise = new double[steps];
            var beforeLevel = new double[steps];
            var afterLevel = new double[steps];
            for (int s = before; s + after <= steps; s++)
            {
                double b = 0, a = 0;
                for (int i = s - before; i < s; i++) b += level[i];
                for (int i = s; i < s + after; i++) a += level[i];
                b /= before; a /= after;
                beforeLevel[s] = b;
                afterLevel[s] = a;
                rise[s] = a >= LoudAfterLift ? a - b : 0;
            }
            double LevelAt(double t) => level[Math.Clamp((int)(t / block), 0, steps - 1)];
            // The steepest point of each rise, the biggest first, kept apart. The moment is the first beat at which the
            // sound has risen halfway, where it is heard to change (the nearest beat to the steepest point was often the
            // second loud one); a bar start within a quarter second of it is taken instead.
            var lifts = new List<double>();
            foreach (int s in Enumerable.Range(0, steps).Where(s => rise[s] >= LeastLift).OrderByDescending(s => rise[s]))
            {
                double at = s * block;
                if (lifts.Any(l => Math.Abs(l - at) < LiftsApart)) continue;
                double halfway = (beforeLevel[s] + afterLevel[s]) / 2;
                double? first = beats.Where(t => t >= at - 1 && t <= at + 1.5).Cast<double?>().FirstOrDefault(t => LevelAt(t!.Value) >= halfway);
                if (first is not double on) continue;
                if (downbeats is { Count: > 0 } && downbeats.OrderBy(t => Math.Abs(t - on)).First() is double bar && Math.Abs(bar - on) <= 0.25) on = bar;
                if (lifts.Any(l => Math.Abs(l - on) < LiftsApart)) continue;
                lifts.Add(on);
            }
            lifts.Sort();
            Lifts = lifts;
            return this;
        }

        /// <summary>
        /// Which beat of every four starts a bar: the one where the bass, and then everything, hits hardest on average.
        /// The count may move where a beat was skipped or added. Trusted when bar starts stand out and the count changes
        /// no more than about once in 30 seconds.
        /// </summary>
        private static (List<int> Downbeats, double Strength, bool Agree, int Changes) Bars(List<int> frames, double[] onset, double[] bass)
        {
            if (frames.Count < 4 * BeatsInABar) return (new List<int>(), 0, false, 0);
            double Hit(int frame)
            {
                double b = 0, o = 0;
                for (int f = Math.Max(0, frame - 2); f <= Math.Min(onset.Length - 1, frame + 2); f++) { b = Math.Max(b, bass[f]); o = Math.Max(o, onset[f]); }
                return b + 0.5 * o;
            }
            var hits = frames.Select(Hit).ToArray();
            // One count for the song that may change where the music says so: one beat the tracker skipped or added moves
            // every later bar by a beat, so a single whole-song count would be wrong after it. Each beat scores for the count
            // that makes it a bar's first beat and against the others; a change of count costs about two bars' evidence,
            // so the count holds through ordinary variation and moves only at a real slip.
            double mean = hits.Average();
            if (mean <= 0) return (new List<int>(), 0, false, 0);
            int n = hits.Length;
            var score = new double[n, BeatsInABar];
            var back = new int[n, BeatsInABar];
            double changeCost = 2.0 * BeatsInABar * 0.25;
            for (int i = 0; i < n; i++)
                for (int p = 0; p < BeatsInABar; p++)
                {
                    double gain = (i % BeatsInABar == p ? 1.0 : -1.0 / (BeatsInABar - 1)) * (hits[i] / mean - 1);
                    if (i == 0) { score[i, p] = gain; continue; }
                    int bestFrom = p;
                    double best = score[i - 1, p];
                    for (int q = 0; q < BeatsInABar; q++)
                        if (q != p && score[i - 1, q] - changeCost > best) { best = score[i - 1, q] - changeCost; bestFrom = q; }
                    score[i, p] = best + gain;
                    back[i, p] = bestFrom;
                }
            var phases = new int[n];
            int last = 0;
            for (int p = 1; p < BeatsInABar; p++) if (score[n - 1, p] > score[n - 1, last]) last = p;
            for (int i = n - 1; i >= 0; i--) { phases[i] = last; if (i > 0) last = back[i, last]; }

            var downbeats = new List<int>();
            double onDown = 0, offDown = 0;
            int countDown = 0, countOff = 0, lastDown = -BeatsInABar, changes = 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && phases[i] != phases[i - 1]) changes++;
                if (i % BeatsInABar == phases[i] && i - lastDown >= BeatsInABar - 1)
                {
                    downbeats.Add(frames[i]);
                    lastDown = i;
                    onDown += hits[i]; countDown++;
                }
                else { offDown += hits[i]; countOff++; }
            }
            double strength = countDown == 0 || countOff == 0 || offDown <= 0 ? 0 : (onDown / countDown) / (offDown / countOff);
            // Trusted when bar starts stand out and the count changes rarely: no more than once in thirty seconds. Eight real
            // songs changed about once in fifty (at section breaks, where the tracker slips); the bar starts follow each change.
            int beatsIn30s = Math.Max(16, (int)(30 / Math.Max(0.2, (Seconds(frames[^1]) - Seconds(frames[0])) / Math.Max(1, n - 1))));
            return (downbeats, strength, changes <= Math.Max(1, n / beatsIn30s), changes);
        }

        /// <summary>Loudness every half second, smoothed over four seconds, from the song's tenth percentile (0) to its ninetieth (1).</summary>
        private static double[] Energy(double[] loudness)
        {
            int per = Math.Max(1, (int)(EnergyStep / FrameSeconds));
            int steps = loudness.Length / per;
            if (steps < 8) return Array.Empty<double>();
            var raw = new double[steps];
            for (int s = 0; s < steps; s++)
            {
                double sum = 0;
                for (int f = s * per; f < (s + 1) * per; f++) sum += loudness[f];
                raw[s] = Math.Log(1e-6 + sum / per);
            }
            int reach = (int)(2.0 / EnergyBlock);
            var smooth = new double[steps];
            for (int s = 0; s < steps; s++)
            {
                int a = Math.Max(0, s - reach), b = Math.Min(steps - 1, s + reach);
                double sum = 0;
                for (int i = a; i <= b; i++) sum += raw[i];
                smooth[s] = sum / (b - a + 1);
            }
            var sorted = smooth.OrderBy(v => v).ToArray();
            double quiet = sorted[sorted.Length / 10], loud = sorted[sorted.Length * 9 / 10];
            if (loud - quiet < 1e-6) return smooth.Select(_ => 0.5).ToArray();
            return smooth.Select(v => Math.Clamp((v - quiet) / (loud - quiet), 0, 1)).ToArray();
        }

        /// <summary>
        /// Per frame: the onset strength (how much the spectrum rises, less its local average, never below zero), the same
        /// for the bass alone (below about 200 Hz, where a kick lands), and the loudness.
        /// </summary>
        private static (double[] Onset, double[] Bass, double[] Loudness) Measure(float[] samples)
        {
            int frames = (samples.Length - Window) / Hop;
            if (frames <= 0) return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
            const int bassTop = (int)(200.0 * Window / SampleRate);
            var hann = new double[Window];
            for (int i = 0; i < Window; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));
            var re = new double[Window];
            var im = new double[Window];
            var last = new double[Window / 2];
            var flux = new double[frames];
            var bassFlux = new double[frames];
            var loudness = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int at = f * Hop;
                double power = 0;
                for (int i = 0; i < Window; i++) { re[i] = samples[at + i] * hann[i]; im[i] = 0; power += samples[at + i] * (double)samples[at + i]; }
                loudness[f] = power / Window;
                Fft(re, im);
                double rise = 0, bassRise = 0;
                for (int k = 1; k < Window / 2; k++)
                {
                    double mag = Math.Log(1 + 100 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                    if (f > 0 && mag > last[k])
                    {
                        rise += mag - last[k];
                        if (k <= bassTop) bassRise += mag - last[k];
                    }
                    last[k] = mag;
                }
                flux[f] = rise;
                bassFlux[f] = bassRise;
            }
            return (LessLocalAverage(flux), LessLocalAverage(bassFlux), loudness);
        }

        /// <summary>Less the average of the half second around it, so a loud passage is not all onsets; scaled to unit size.</summary>
        private static double[] LessLocalAverage(double[] flux)
        {
            int frames = flux.Length;
            int half = (int)(0.25 / FrameSeconds);
            var onset = new double[frames];
            double running = 0;
            int count = 0, lo = 0, hi = -1;
            for (int f = 0; f < frames; f++)
            {
                while (hi < Math.Min(frames - 1, f + half)) { running += flux[++hi]; count++; }
                while (lo < f - half) { running -= flux[lo++]; count--; }
                onset[f] = Math.Max(0, flux[f] - running / count);
            }
            double sd = Math.Sqrt(onset.Average(v => v * v));
            if (sd > 0) for (int f = 0; f < frames; f++) onset[f] /= sd;
            return onset;
        }

        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                double wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int a = i + j, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double next = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = next;
                    }
                }
            }
        }
    }
}

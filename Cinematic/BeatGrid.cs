using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer.Cinematic
{
    /// <summary>
    /// The beats of one song, found from its sound alone (design 3.10): an onset strength from how much the spectrum rises
    /// frame to frame, a tempo from how that strength repeats, and beats placed on it by dynamic programming, so each beat
    /// sits on a strong onset and keeps to the tempo. Confidence says how much stronger the onsets are on the beats than
    /// between them: free-time music and speech score low and are not cut to.
    /// </summary>
    public sealed class BeatGrid
    {
        public const int SampleRate = 11025;
        private const int Window = 1024, Hop = 128;
        private const double FrameSeconds = Hop / (double)SampleRate;

        /// <summary>
        /// Below this, the beats are not used: the scene holds as planned. Set from real songs (design check M1, 7 Nov):
        /// eight songs from the music folder scored 3.4 to 6.6; white noise 1.1 and clicks at random times 2.7.
        /// </summary>
        public const double LeastConfidence = 3.0;

        public IReadOnlyList<double> Beats { get; }
        public double Bpm { get; }
        public double Confidence { get; }
        public bool Usable => Confidence >= LeastConfidence && Beats.Count >= 8;

        private BeatGrid(IReadOnlyList<double> beats, double bpm, double confidence)
        {
            Beats = beats;
            Bpm = bpm;
            Confidence = confidence;
        }

        public static readonly BeatGrid None = new(Array.Empty<double>(), 0, 0);

        /// <summary>The beat nearest <paramref name="planned"/> seconds, if one lies between the two limits.</summary>
        public double? NearestBeat(double planned, double earliest, double latest)
        {
            if (!Usable || latest < earliest) return null;
            double? best = null;
            foreach (double beat in Beats)
            {
                if (beat < earliest) continue;
                if (beat > latest) break;
                if (best == null || Math.Abs(beat - planned) < Math.Abs(best.Value - planned)) best = beat;
            }
            return best;
        }

        /// <summary>
        /// Cut on the beat (design 3.10), for a scene just shown: how long to wait before starting the next change, so the
        /// change lands on the beat nearest the planned end, inside Duration's shortest to longest. The change starts
        /// <paramref name="changeDelay"/> after the wait ends (the next scene being put on screen), so the wait is that
        /// much shorter. With no beats, no song position, one fixed duration, or no beat in reach: the planned hold.
        /// </summary>
        public static double WaitBeforeChange(BeatGrid? beats, double? songNow, double plannedHold, double shortest, double longest, double changeDelay)
        {
            if (beats == null || songNow is not double now || longest - shortest < 0.01) return plannedHold;
            double earliest = now + Math.Min(shortest, plannedHold), latest = now + Math.Max(longest, plannedHold);
            if (beats.NearestBeat(now + plannedHold, earliest, latest) is not double beat) return plannedHold;
            double wait = beat - now - changeDelay;
            return wait < 1 ? plannedHold : wait;
        }

        /// <summary>Beats from mono samples at <see cref="SampleRate"/>.</summary>
        public static BeatGrid Find(float[] samples)
        {
            var onset = OnsetStrength(samples);
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
            var score2 = new double[n];
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
                    double s = score2[prev] - tightness * stray * stray;
                    if (bestFrom < 0 || s > best) { best = s; bestFrom = prev; }
                }
                score2[t] = onset[t] + (bestFrom >= 0 ? best : 0);
                from[t] = bestFrom;
            }
            int end = n - 1;
            for (int t = n - 1; t >= Math.Max(0, n - period); t--) if (score2[t] > score2[end]) end = t;
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

            // A frame's time is the middle of its window, where an onset in it is heard; its start put beats 70 ms early.
            const double middle = Window / 2.0 / SampleRate;
            return new BeatGrid(frames.Select(f => f * FrameSeconds + middle).ToList(), 60.0 / (period * FrameSeconds), confidence);
        }

        /// <summary>How much the spectrum rises, frame to frame, summed over frequencies; less its local average, never below zero.</summary>
        private static double[] OnsetStrength(float[] samples)
        {
            int frames = (samples.Length - Window) / Hop;
            if (frames <= 0) return Array.Empty<double>();
            var hann = new double[Window];
            for (int i = 0; i < Window; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));
            var re = new double[Window];
            var im = new double[Window];
            var last = new double[Window / 2];
            var flux = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int at = f * Hop;
                for (int i = 0; i < Window; i++) { re[i] = samples[at + i] * hann[i]; im[i] = 0; }
                Fft(re, im);
                double rise = 0;
                for (int k = 1; k < Window / 2; k++)
                {
                    double mag = Math.Log(1 + 100 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                    if (f > 0 && mag > last[k]) rise += mag - last[k];
                    last[k] = mag;
                }
                flux[f] = rise;
            }
            // Less the average of the half second around it, so a loud passage is not all onsets.
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

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ModernImageViewer.Cinematic
{
    /// <summary>
    /// Reads a song's beats in the background (design 3.10): Windows' own transcoder turns it into a small mono WAV in the
    /// temp folder (under a second for a four-minute song), <see cref="BeatGrid"/> finds the beats, and the WAV is deleted.
    /// No new dependency, and the audio never leaves the machine. One song at a time; results are kept for the session by
    /// path, size and modified time. A song that cannot be read gives <see cref="BeatGrid.None"/>, and scenes hold as planned.
    /// </summary>
    public static class BeatReader
    {
        private static readonly ConcurrentDictionary<string, Task<BeatGrid>> Read = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static readonly TimeSpan MostTime = TimeSpan.FromSeconds(20);

        public static Task<BeatGrid> ReadAsync(string path)
        {
            string key;
            try
            {
                var info = new FileInfo(path);
                key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Task.FromResult(BeatGrid.None);
            }
            return Read.GetOrAdd(key, _ => Task.Run(() => DecodeAndFindAsync(path)));
        }

        private static async Task<BeatGrid> DecodeAndFindAsync(string path)
        {
            await OneAtATime.WaitAsync().ConfigureAwait(false);
            string wav = Path.Combine(Path.GetTempPath(), $"miv-beats-{Guid.NewGuid():N}.wav");
            try
            {
                var input = await StorageFile.GetFileFromPathAsync(path);
                File.WriteAllBytes(wav, Array.Empty<byte>());
                var output = await StorageFile.GetFileFromPathAsync(wav);
                var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
                profile.Audio = AudioEncodingProperties.CreatePcm(BeatGrid.SampleRate, 1, 16);
                var prepared = await new MediaTranscoder().PrepareFileTranscodeAsync(input, output, profile);
                if (!prepared.CanTranscode) return BeatGrid.None;
                var job = prepared.TranscodeAsync().AsTask();
                if (await Task.WhenAny(job, Task.Delay(MostTime)).ConfigureAwait(false) != job) return BeatGrid.None;
                await job.ConfigureAwait(false);
                return BeatGrid.Find(Samples(File.ReadAllBytes(wav)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[Beats] {Path.GetFileName(path)}: {ex.Message}");
                return BeatGrid.None;
            }
            finally
            {
                try { File.Delete(wav); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                OneAtATime.Release();
            }
        }

        /// <summary>16-bit mono PCM from a WAV file's data chunk.</summary>
        private static float[] Samples(byte[] wav)
        {
            int data = 44;
            for (int i = 12; i + 8 <= wav.Length; i++)
                if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a') { data = i + 8; break; }
            int count = Math.Max(0, (wav.Length - data) / 2);
            var samples = new float[count];
            for (int i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(wav, data + 2 * i) / 32768f;
            return samples;
        }
    }
}

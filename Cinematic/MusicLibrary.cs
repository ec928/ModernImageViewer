using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ModernImageViewer.Cinematic
{
    /// <summary>
    /// Reads star ratings from the tags in music files. Windows' own rating loses half-stars, so the tags are read directly.
    /// MP3 keeps it in an ID3 POPM frame and FLAC in a Vorbis comment. Other formats count as unrated.
    /// </summary>
    public static class MusicLibrary
    {
        public static readonly string[] Extensions = { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac" };

        public static double? ReadStars(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".mp3" => ReadId3Stars(file),
                    ".flac" => ReadFlacStars(file),
                    _ => null
                };
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// The song first, so it survives if the line is cut short. With no title in the tags, the file's name without the
        /// download site's name and bitrate that such files carry.
        /// </summary>
        public static string SongName(string? title, string? artist, string fileName)
        {
            title = title?.Trim();
            artist = artist?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                string name = Regex.Replace(fileName, @"^[\w.-]+\.(com|net|org)\s+-\s+", string.Empty, RegexOptions.IgnoreCase);
                return Regex.Replace(name, @"_\d+kbps$", string.Empty, RegexOptions.IgnoreCase).Trim();
            }
            return string.IsNullOrEmpty(artist) ? title : $"{title} \u2013 {artist}";
        }

        public static bool Qualifies(double? stars, double lowestStars, bool includeUnrated) =>
            stars is double s ? s >= lowestStars : includeUnrated;

        // What players write for each star value, half-stars included.
        private static readonly (byte Value, double Stars)[] PopularimeterSteps =
        {
            (1, 1), (13, 0.5), (23, 1), (54, 1.5), (64, 2), (118, 2.5), (128, 3), (186, 3.5), (196, 4), (242, 4.5), (255, 5)
        };

        public static double? StarsFromPopularimeter(byte value)
        {
            if (value == 0) return null;
            double best = 0;
            int bestGap = int.MaxValue;
            foreach (var step in PopularimeterSteps)
            {
                int gap = Math.Abs(step.Value - value);
                if (gap < bestGap) { bestGap = gap; best = step.Stars; }
            }
            return best;
        }

        /// <summary>RATING is 0 to 100, or 1 to 5 if a player wrote stars. FMPS_RATING is 0 to 1.</summary>
        public static double? StarsFromVorbis(string key, string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number <= 0) return null;
            if (key.Equals("FMPS_RATING", StringComparison.OrdinalIgnoreCase)) return Math.Min(5, number * 5);
            if (!key.Equals("RATING", StringComparison.OrdinalIgnoreCase)) return null;
            return Math.Min(5, number > 5 ? number / 20 : number);
        }

        private static double? ReadId3Stars(Stream file)
        {
            var header = new byte[10];
            if (file.Read(header, 0, 10) < 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;
            int version = header[3];
            long tagEnd = 10 + SyncSafe(header, 6);

            if ((header[5] & 0x40) != 0 && version >= 3)
            {
                var size = new byte[4];
                if (file.Read(size, 0, 4) < 4) return null;
                long extended = version >= 4 ? SyncSafe(size, 0) - 4 : BigEndian(size, 0);
                file.Seek(extended, SeekOrigin.Current);
            }

            int idLength = version <= 2 ? 3 : 4;
            int headerLength = version <= 2 ? 6 : 10;
            var frame = new byte[headerLength];
            // Frames are walked by seeking, so cover art in front of the rating costs nothing to pass.
            while (file.Position + headerLength < tagEnd)
            {
                if (file.Read(frame, 0, headerLength) < headerLength) return null;
                if (frame[0] == 0) return null;
                long length = version <= 2 ? (frame[3] << 16) | (frame[4] << 8) | frame[5]
                    : version >= 4 ? SyncSafe(frame, 4) : BigEndian(frame, 4);
                if (length < 0 || file.Position + length > tagEnd) return null;

                string id = Encoding.ASCII.GetString(frame, 0, idLength);
                if (id is "POPM" or "POP" && length <= 512)
                {
                    var body = new byte[length];
                    if (file.Read(body, 0, (int)length) < length) return null;
                    int nul = Array.IndexOf(body, (byte)0);
                    if (nul >= 0 && nul + 1 < body.Length && StarsFromPopularimeter(body[nul + 1]) is double stars) return stars;
                }
                else file.Seek(length, SeekOrigin.Current);
            }
            return null;
        }

        private static double? ReadFlacStars(Stream file)
        {
            var mark = new byte[4];
            if (file.Read(mark, 0, 4) < 4 || mark[0] != 'f' || mark[1] != 'L' || mark[2] != 'a' || mark[3] != 'C') return null;
            while (true)
            {
                if (file.Read(mark, 0, 4) < 4) return null;
                bool last = (mark[0] & 0x80) != 0;
                int kind = mark[0] & 0x7F;
                int length = (mark[1] << 16) | (mark[2] << 8) | mark[3];
                if (kind == 4)
                {
                    var body = new byte[length];
                    return file.Read(body, 0, length) < length ? null : StarsFromVorbisComments(body);
                }
                if (last) return null;
                file.Seek(length, SeekOrigin.Current);
            }
        }

        private static double? StarsFromVorbisComments(byte[] body)
        {
            int at = 0;
            if (!TryLength(body, ref at, out int vendor) || (at += vendor) > body.Length) return null;
            if (!TryLength(body, ref at, out int count)) return null;
            for (int i = 0; i < count; i++)
            {
                if (!TryLength(body, ref at, out int length) || at + length > body.Length) return null;
                string comment = Encoding.UTF8.GetString(body, at, length);
                at += length;
                int equals = comment.IndexOf('=');
                if (equals > 0 && StarsFromVorbis(comment[..equals], comment[(equals + 1)..]) is double stars) return stars;
            }
            return null;
        }

        private static bool TryLength(byte[] body, ref int at, out int length)
        {
            length = 0;
            if (at + 4 > body.Length) return false;
            uint value = BitConverter.ToUInt32(body, at);
            at += 4;
            if (value > int.MaxValue) return false;
            length = (int)value;
            return true;
        }

        private static long SyncSafe(byte[] b, int at) => (b[at] << 21) | (b[at + 1] << 14) | (b[at + 2] << 7) | b[at + 3];
        private static long BigEndian(byte[] b, int at) => ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];
    }

    /// <summary>
    /// Whose music is heard when two want to play. The one that started last is, and when it stops the one it silenced carries on.
    /// </summary>
    public sealed class OneSounding<T> where T : class
    {
        private T? _heard;
        private T? _waiting;

        /// <summary>Returns the one to silence, if any.</summary>
        public T? Started(T who)
        {
            if (ReferenceEquals(_heard, who)) return null;
            var silenced = _heard;
            if (silenced != null) _waiting = silenced;
            _heard = who;
            return silenced;
        }

        /// <summary>Returns the one to carry on, if any.</summary>
        public T? Stopped(T who)
        {
            if (ReferenceEquals(_waiting, who))
            {
                _waiting = null;
                return null;
            }
            if (!ReferenceEquals(_heard, who)) return null;
            _heard = null;
            var next = _waiting;
            _waiting = null;
            return next;
        }

        /// <summary>Changing to other songs is not stopping, so nothing else gets a turn in between.</summary>
        public void Changing(T who)
        {
            if (ReferenceEquals(_heard, who)) _heard = null;
        }
    }

    /// <summary>Songs in random order. None comes round again until all have played, and songs can join while it is in use.</summary>
    public sealed class MusicDeck
    {
        private readonly List<string> _waiting = new();
        private readonly List<string> _played = new();
        private readonly List<string> _heard = new();
        private int _at = -1;
        private string? _last;

        public int Count => _waiting.Count + _played.Count;

        public void Add(string song) => _waiting.Add(song);

        public string? Next(Random rnd)
        {
            if (Count == 0) return null;
            // After going back, forward retraces what was heard before choosing anything new.
            if (_at < _heard.Count - 1) return _heard[++_at];
            if (_waiting.Count == 0)
            {
                _waiting.AddRange(_played);
                _played.Clear();
            }
            int pick = rnd.Next(_waiting.Count);
            // The last song of one round is not the first of the next.
            if (_waiting[pick] == _last && _waiting.Count > 1) pick = (pick + 1) % _waiting.Count;
            string song = _waiting[pick];
            _waiting.RemoveAt(pick);
            _played.Add(song);
            _last = song;
            _heard.Add(song);
            _at = _heard.Count - 1;
            return song;
        }

        /// <summary>The song heard before this one, or nothing at the first.</summary>
        public string? Previous() => _at > 0 ? _heard[--_at] : null;
    }
}

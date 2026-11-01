using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ModernImageViewer.Cinematic
{
    /// <summary>
    /// Music under pictures: one file that loops, or random songs from a folder at or above a rating.
    /// Cinematic and Surprise me's auto-play each own one. The one that starts playing silences the other, which carries on when it stops.
    /// </summary>
    public sealed class MusicBed : IDisposable
    {
        private const int SongFailuresBeforeGivingUp = 5;
        private const int RecallOnDataAccess = 0x400000;

        private readonly DispatcherQueue _dispatcher;
        private MediaPlayer? _player;
        private MusicDeck? _deck;
        private CancellationTokenSource? _scanCts;
        private bool _songChosen;
        private int _failedInARow;
        private bool _wantPlaying;
        private int _openTicket;
        private double _volume = 0.6;
        // What was asked for. Asked for again, the song carries on rather than starting over.
        private string? _asked;
        private static readonly OneSounding<MusicBed> Sounding = new();

        public event Action<string?>? NowPlayingChanged;
        /// <summary>Nothing can play. The text says why, and the bed has already stopped.</summary>
        public event Action<string, string>? GaveUp;

        public MusicBed(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

        public bool IsFolder => _deck != null;
        public bool IsOn => _player != null;

        public bool IsPlayingFolder(string folder, double lowestStars, bool includeUnrated) =>
            _player != null && _asked == FolderKey(folder, lowestStars, includeUnrated);

        private static string FolderKey(string folder, double lowestStars, bool includeUnrated) => $"{folder}|{lowestStars}|{includeUnrated}";

        public void PlayFile(string path)
        {
            if (_player != null && _asked == path) return;
            StopPlayer();
            Sounding.Changing(this);
            _asked = path;
            try
            {
                _player = NewPlayer();
                _player.IsLoopingEnabled = true;
                _ = OpenAsync(path, Path.GetFileNameWithoutExtension(path));
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Stop();
                GaveUp?.Invoke("That music will not play", ex.Message);
            }
        }

        public void PlayFolder(string folder, double lowestStars, bool includeUnrated)
        {
            if (IsPlayingFolder(folder, lowestStars, includeUnrated)) return;
            StopPlayer();
            Sounding.Changing(this);
            _asked = FolderKey(folder, lowestStars, includeUnrated);
            _player = NewPlayer();
            _deck = new MusicDeck();
            var cts = new CancellationTokenSource();
            _scanCts = cts;
            var token = cts.Token;
            _ = Task.Run(() =>
            {
                int found = 0;
                try
                {
                    // In random order, so the first song to qualify, which starts the music, is a random one.
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    var songs = Directory.EnumerateFiles(folder, "*", options)
                        .Where(p => MusicLibrary.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                        .ToArray();
                    Random.Shared.Shuffle(songs);
                    foreach (var song in songs)
                    {
                        if (token.IsCancellationRequested) return;
                        // Reading the tag of a cloud-only file would download it.
                        try { if (((int)File.GetAttributes(song) & RecallOnDataAccess) != 0) continue; }
                        catch (IOException) { continue; }
                        if (!MusicLibrary.Qualifies(MusicLibrary.ReadStars(song), lowestStars, includeUnrated)) continue;
                        found++;
                        _dispatcher.TryEnqueue(() => SongFound(song, token));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"[Music] Folder: {ex.Message}");
                }
                if (found == 0 && !token.IsCancellationRequested)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        Stop();
                        string what = includeUnrated ? $"rated {lowestStars:0.#} stars or higher, or unrated," : $"rated {lowestStars:0.#} stars or higher";
                        GaveUp?.Invoke("No songs to play", $"Nothing in {Path.GetFileName(folder.TrimEnd('\\'))} is {what}.");
                    });
                }
            }, token);
        }

        public void SetPlaying(bool playing)
        {
            _wantPlaying = playing;
            Sync();
        }

        public void SetVolume(double percent)
        {
            _volume = Math.Clamp(percent, 0, 100) / 100.0;
            if (_player != null) _player.Volume = _volume;
        }

        public void NextSong()
        {
            if (_player == null || _deck == null) return;
            string? song = _deck.Next(Random.Shared);
            if (song == null) return;
            _songChosen = true;
            _ = OpenAsync(song, Path.GetFileNameWithoutExtension(song));
        }

        public void PreviousSong()
        {
            if (_player == null || _deck == null) return;
            string? song = _deck.Previous();
            if (song != null) _ = OpenAsync(song, Path.GetFileNameWithoutExtension(song));
        }

        /// <summary>By file, not by address: the player cannot find a file: address with an accent or other non-English letter in it.</summary>
        private async Task OpenAsync(string path, string name)
        {
            int ticket = ++_openTicket;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                if (ticket != _openTicket || _player == null) return;
                _player.Source = MediaSource.CreateFromStorageFile(file);
                Sync();
                string shown = await ShownNameAsync(file, name);
                if (ticket == _openTicket && _player != null) NowPlayingChanged?.Invoke(shown);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                if (ticket == _openTicket && _player != null) SongFailed(ex.Message);
            }
        }

        /// <summary>A file Windows cannot read the tags of still plays, under its file name.</summary>
        private static async Task<string> ShownNameAsync(Windows.Storage.StorageFile file, string fileName)
        {
            try
            {
                var tags = await file.Properties.GetMusicPropertiesAsync();
                return MusicLibrary.SongName(tags.Title, tags.Artist, fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                return MusicLibrary.SongName(null, null, fileName);
            }
        }

        public void Stop()
        {
            StopPlayer();
            var next = Sounding.Stopped(this);
            if (next is { _wantPlaying: true }) next.Sync();
        }

        private void StopPlayer()
        {
            _openTicket++;
            _asked = null;
            _scanCts?.Cancel();
            _scanCts = null;
            _deck = null;
            _songChosen = false;
            _failedInARow = 0;
            if (_player != null)
            {
                _player.MediaFailed -= Player_Failed;
                _player.MediaEnded -= Player_Ended;
                _player.MediaOpened -= Player_Opened;
                _player.Dispose();
                _player = null;
            }
            NowPlayingChanged?.Invoke(null);
        }

        public void Dispose() => Stop();

        private MediaPlayer NewPlayer()
        {
            var player = new MediaPlayer { Volume = _volume };
            player.MediaFailed += Player_Failed;
            player.MediaEnded += Player_Ended;
            player.MediaOpened += Player_Opened;
            return player;
        }

        private void SongFound(string song, CancellationToken token)
        {
            if (token.IsCancellationRequested || _deck == null) return;
            _deck.Add(song);
            if (!_songChosen) NextSong();
        }

        private void Sync()
        {
            if (_player == null || _player.Source == null) return;
            if (_wantPlaying)
            {
                Sounding.Started(this)?._player?.Pause();
                _player.Play();
            }
            else _player.Pause();
        }

        private void Player_Opened(MediaPlayer sender, object args) => _failedInARow = 0;

        private void Player_Ended(MediaPlayer sender, object args)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_deck != null) NextSong();
            });
        }

        private void Player_Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            string why = args.ErrorMessage;
            _dispatcher.TryEnqueue(() =>
            {
                // A failure that arrives after this player was stopped or replaced is not news.
                if (ReferenceEquals(sender, _player)) SongFailed(why);
            });
        }

        private void SongFailed(string why)
        {
            // One bad song in a folder is not worth stopping for.
            if (_deck != null && ++_failedInARow < SongFailuresBeforeGivingUp)
            {
                NextSong();
                return;
            }
            Stop();
            GaveUp?.Invoke("That music will not play", why);
        }
    }
}

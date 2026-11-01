using System.Text;
using ModernImageViewer.Cinematic;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class MusicLibraryTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("mivmusic").FullName;
        public void Dispose() => Directory.Delete(_dir, true);

        [Theory]
        [InlineData("Wellerman", "Nathan Evans", "y2mate.com - Nathan Evans  Wellerman Sea Shanty_320kbps", "Wellerman – Nathan Evans")]
        [InlineData("Sinners (Official Audio)", "  Barns Courtney ", "x", "Sinners (Official Audio) – Barns Courtney")]
        [InlineData("Arranca", "", "x", "Arranca")]
        [InlineData("Arranca", null, "x", "Arranca")]
        [InlineData("   ", "Moby", "Moby - Anthem", "Moby - Anthem")]
        [InlineData(null, null, "y2mate.com - Miike Snow - Animal_320kbps", "Miike Snow - Animal")]
        [InlineData(null, null, "Y2MATE.COM - Miike Snow", "Miike Snow")]
        [InlineData(null, null, "India.Arie - Ready For Love", "India.Arie - Ready For Love")]
        [InlineData(null, null, "274 - Jefferson Airplane", "274 - Jefferson Airplane")]
        public void A_song_shows_its_title_then_artist_or_a_clean_file_name(string? title, string? artist, string file, string expected) =>
            Assert.Equal(expected, MusicLibrary.SongName(title, artist, file));

        private static byte[] SyncSafe(int n) => new[] { (byte)((n >> 21) & 0x7F), (byte)((n >> 14) & 0x7F), (byte)((n >> 7) & 0x7F), (byte)(n & 0x7F) };
        private static byte[] BigEndian(int n) => new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };

        private static byte[] Frame(int version, string id, byte[] body)
        {
            var size = version >= 4 ? SyncSafe(body.Length) : BigEndian(body.Length);
            return Encoding.ASCII.GetBytes(id).Concat(size).Concat(new byte[2]).Concat(body).ToArray();
        }

        private static byte[] Popm(byte value) => Encoding.ASCII.GetBytes("no@email").Concat(new byte[] { 0, value, 0, 0, 0, 0 }).ToArray();

        private string Mp3(int version, params byte[][] frames)
        {
            var body = frames.SelectMany(f => f).Concat(new byte[64]).ToArray();
            var tag = new byte[] { (byte)'I', (byte)'D', (byte)'3', (byte)version, 0, 0 }.Concat(SyncSafe(body.Length)).Concat(body).Concat(new byte[100]).ToArray();
            string path = Path.Combine(_dir, Guid.NewGuid() + ".mp3");
            File.WriteAllBytes(path, tag);
            return path;
        }

        private string Flac(params string[] comments)
        {
            var body = new List<byte>(BitConverter.GetBytes(3)) { (byte)'v', (byte)'e', (byte)'n' };
            body.AddRange(BitConverter.GetBytes(comments.Length));
            foreach (var c in comments) { var b = Encoding.UTF8.GetBytes(c); body.AddRange(BitConverter.GetBytes(b.Length)); body.AddRange(b); }
            var file = new List<byte>(Encoding.ASCII.GetBytes("fLaC"));
            file.AddRange(new byte[] { 0, 0, 0, 34 }); file.AddRange(new byte[34]);
            file.Add(0x84); file.Add(0); file.Add((byte)(body.Count >> 8)); file.Add((byte)body.Count);
            file.AddRange(body);
            string path = Path.Combine(_dir, Guid.NewGuid() + ".flac");
            File.WriteAllBytes(path, file.ToArray());
            return path;
        }

        // Every value found in the real library.
        [Theory]
        [InlineData(54, 1.5)]
        [InlineData(64, 2.0)]
        [InlineData(118, 2.5)]
        [InlineData(128, 3.0)]
        [InlineData(186, 3.5)]
        [InlineData(196, 4.0)]
        [InlineData(242, 4.5)]
        [InlineData(255, 5.0)]
        public void Popularimeter_values_become_stars(byte value, double stars) => Assert.Equal(stars, MusicLibrary.StarsFromPopularimeter(value));

        [Fact]
        public void Popularimeter_zero_is_unrated() => Assert.Null(MusicLibrary.StarsFromPopularimeter(0));

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void Mp3_rating_is_found_in_both_tag_versions(int version) =>
            Assert.Equal(3.5, MusicLibrary.ReadStars(Mp3(version, Frame(version, "TIT2", new byte[30]), Frame(version, "POPM", Popm(186)))));

        [Fact]
        public void Mp3_rating_is_found_behind_large_cover_art() =>
            Assert.Equal(4.5, MusicLibrary.ReadStars(Mp3(3, Frame(3, "APIC", new byte[2_000_000]), Frame(3, "POPM", Popm(242)))));

        [Fact]
        public void Mp3_without_a_rating_is_unrated() => Assert.Null(MusicLibrary.ReadStars(Mp3(3, Frame(3, "TIT2", new byte[30]))));

        [Theory]
        [InlineData("RATING=70", 3.5)]
        [InlineData("RATING=60", 3.0)]
        [InlineData("rating=90", 4.5)]
        [InlineData("RATING=4", 4.0)]
        [InlineData("FMPS_RATING=0.7", 3.5)]
        public void Flac_rating_is_found(string comment, double stars) =>
            Assert.Equal(stars, MusicLibrary.ReadStars(Flac("TITLE=x", comment))!.Value, 3);

        [Fact]
        public void Flac_without_a_rating_is_unrated() => Assert.Null(MusicLibrary.ReadStars(Flac("TITLE=x")));

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 2, 3 })]
        [InlineData(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F })]
        [InlineData(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 1, 0, (byte)'P', (byte)'O', (byte)'P', (byte)'M', 0xFF, 0xFF, 0xFF, 0xFF, 0, 0 })]
        public void Broken_mp3_is_unrated_and_does_not_throw(byte[] bytes)
        {
            string path = Path.Combine(_dir, Guid.NewGuid() + ".mp3");
            File.WriteAllBytes(path, bytes);
            Assert.Null(MusicLibrary.ReadStars(path));
        }

        [Theory]
        [InlineData(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C' })]
        [InlineData(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0x84, 0xFF, 0xFF, 0xFF, 1, 2 })]
        [InlineData(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0x84, 0, 0, 4, 0xFF, 0xFF, 0xFF, 0xFF })]
        public void Broken_flac_is_unrated_and_does_not_throw(byte[] bytes)
        {
            string path = Path.Combine(_dir, Guid.NewGuid() + ".flac");
            File.WriteAllBytes(path, bytes);
            Assert.Null(MusicLibrary.ReadStars(path));
        }

        [Fact]
        public void Missing_file_is_unrated() => Assert.Null(MusicLibrary.ReadStars(Path.Combine(_dir, "gone.mp3")));

        [Theory]
        [InlineData(3.5, 3.5, false, true)]
        [InlineData(3.0, 3.5, false, false)]
        [InlineData(5.0, 3.5, false, true)]
        [InlineData(null, 3.5, false, false)]
        [InlineData(null, 3.5, true, true)]
        [InlineData(3.0, 3.5, true, false)]
        public void Filter_keeps_the_rating_and_up(double? stars, double lowest, bool unrated, bool expected) =>
            Assert.Equal(expected, MusicLibrary.Qualifies(stars, lowest, unrated));

        [Fact]
        public void Deck_plays_everything_before_repeating_and_never_twice_running()
        {
            var deck = new MusicDeck();
            var songs = Enumerable.Range(0, 20).Select(i => "s" + i).ToList();
            songs.ForEach(deck.Add);
            var rnd = new Random(5);
            string? last = null;
            for (int round = 0; round < 50; round++)
            {
                var heard = new HashSet<string>();
                for (int i = 0; i < songs.Count; i++)
                {
                    string song = deck.Next(rnd)!;
                    Assert.NotEqual(last, song);
                    Assert.True(heard.Add(song), $"{song} repeated in round {round}");
                    last = song;
                }
            }
        }

        [Fact]
        public void Previous_goes_back_through_what_was_heard_and_forward_again()
        {
            var deck = new MusicDeck();
            foreach (var s in new[] { "a", "b", "c", "d", "e" }) deck.Add(s);
            var rnd = new Random(2);
            Assert.Null(deck.Previous());
            string one = deck.Next(rnd)!, two = deck.Next(rnd)!, three = deck.Next(rnd)!;
            Assert.Equal(two, deck.Previous());
            Assert.Equal(one, deck.Previous());
            Assert.Null(deck.Previous());
            Assert.Equal(two, deck.Next(rnd));
            Assert.Equal(three, deck.Next(rnd));
            string four = deck.Next(rnd)!;
            Assert.DoesNotContain(four, new[] { one, two, three });
            Assert.Equal(three, deck.Previous());
        }

        [Fact]
        public void Deck_takes_songs_that_arrive_while_playing()
        {
            var deck = new MusicDeck();
            deck.Add("a");
            var rnd = new Random(1);
            Assert.Equal("a", deck.Next(rnd));
            deck.Add("b");
            Assert.Equal("b", deck.Next(rnd));
        }

        [Fact]
        public void Deck_with_one_song_repeats_it_and_empty_gives_nothing()
        {
            var deck = new MusicDeck();
            Assert.Null(deck.Next(new Random(1)));
            deck.Add("a");
            Assert.Equal("a", deck.Next(new Random(1)));
            Assert.Equal("a", deck.Next(new Random(1)));
        }
    }
}

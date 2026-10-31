using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class AppHelpersTests
    {
        private sealed class Row
        {
            public string Name = "";
            public int Version;
        }

        private static List<Row> Rows(params string[] names) => names.Select(n => new Row { Name = n }).ToList();

        [Fact]
        public void Merge_KeepsTheSameObjectsAndMatchesTheNewOrder()
        {
            var current = new ObservableCollection<Row>(Rows("a", "b", "c", "d"));
            var b = current[1];
            var d = current[3];
            var next = Rows("d", "new", "b");
            next[2].Version = 7;

            int changes = 0;
            current.CollectionChanged += (_, _) => changes++;
            ListMerge.Apply(current, next, r => r.Name, (kept, fresh) => kept.Version = fresh.Version);

            Assert.Equal(new[] { "d", "new", "b" }, current.Select(r => r.Name));
            Assert.Same(d, current[0]);
            Assert.Same(b, current[2]);
            Assert.Equal(7, b.Version);
            Assert.True(changes <= 4, "changes " + changes);
        }

        [Fact]
        public void Merge_OneNewFileIsOneChange()
        {
            var current = new ObservableCollection<Row>(Rows("a", "c"));
            int changes = 0;
            current.CollectionChanged += (_, _) => changes++;
            ListMerge.Apply(current, Rows("a", "b", "c"), r => r.Name, (_, _) => { });
            Assert.Equal(1, changes);
        }

        [Fact]
        public void AtomicWrite_AReaderNeverSeesHalfAFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "miv-atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "settings.json");
                string small = new string('a', 400000), large = new string('b', 800000);
                AtomicFile.WriteAllText(path, small);

                bool done = false;
                int partial = 0, reads = 0;
                var reader = System.Threading.Tasks.Task.Run(() =>
                {
                    while (!System.Threading.Volatile.Read(ref done))
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            long length = stream.Length;
                            reads++;
                            if (length != small.Length && length != large.Length) partial++;
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                });
                for (int i = 0; i < 60; i++) AtomicFile.WriteAllText(path, i % 2 == 0 ? large : small);
                System.Threading.Volatile.Write(ref done, true);
                reader.Wait();

                Assert.True(reads > 0);
                Assert.Equal(0, partial);
                Assert.Single(Directory.GetFiles(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData(1920, 1080, 100, 1.0, 100)]
        [InlineData(1920, 1080, 500, 0.7, 500)]
        [InlineData(1920, 1080, 5000, 0.5, 1035)]
        public void AnimationBudget_ShrinksThenStopsEarly(int w, int h, int frames, double scaleAtLeast, int framesExpected)
        {
            var (scale, kept) = ViewerMath.AnimationBudget(w, h, frames);
            Assert.InRange(scale, scaleAtLeast, 1.0);
            Assert.Equal(framesExpected, kept);
            Assert.True(w * scale * h * scale * 4 * kept <= ViewerMath.AnimationBudgetBytes * 1.001);
        }

        [Theory]
        [InlineData(5000, 300, 800, 600, 0, 0, 1920, 1040)] // On a monitor that has gone, off to the right.
        [InlineData(-3000, 100, 800, 600, 0, 0, 1920, 1040)]
        [InlineData(100, 5000, 800, 600, 0, 0, 1920, 1040)]
        [InlineData(100, -500, 800, 600, 0, 0, 1920, 1040)]
        public void KeepOnScreen_LeavesTheTitleBarReachable(int x, int y, int w, int h, int ax, int ay, int aw, int ah)
        {
            var (nx, ny) = ViewerMath.KeepOnScreen(x, y, w, h, ax, ay, aw, ah);
            Assert.True(nx + w >= ax + 100 && nx <= ax + aw - 100, "x " + nx);
            Assert.True(ny >= ay && ny <= ay + ah - 40, "y " + ny);
        }

        [Fact]
        public void KeepOnScreen_LeavesAWindowThatIsAlreadyThereAlone()
        {
            Assert.Equal((200, 150), ViewerMath.KeepOnScreen(200, 150, 800, 600, 0, 0, 1920, 1040));
            Assert.Equal((2100, 50), ViewerMath.KeepOnScreen(2100, 50, 800, 600, 1920, 0, 2560, 1400));
        }

        [Theory]
        [InlineData(164, 1.0, 250)]
        [InlineData(164, 1.5, 250)]
        [InlineData(200, 1.5, 300)]
        [InlineData(400, 1.0, 400)]
        [InlineData(400, 2.0, 800)]
        [InlineData(400, 4.0, 1024)]
        public void ThumbnailEdge_FollowsTheTileOnScreen(double tile, double scale, int edge) =>
            Assert.Equal(edge, ViewerMath.ThumbnailEdge(tile, scale));

        [Theory]
        [InlineData(0, "0 KB")]
        [InlineData(2048, "2 KB")]
        [InlineData(3 * 1048576L, "3.00 MB")]
        public void FileSizeReadsTheSameEverywhere(long bytes, string text) =>
            Assert.Equal(text, ViewerMath.FormatFileSize(bytes));
    }
}

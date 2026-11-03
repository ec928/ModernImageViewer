using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class ShowDirectorTests
    {
        private static PhotoFacts MakePhoto(int id, double w = 1920, double h = 1080) =>
            new($@"C:\Photos\{id}.jpg", w, h, Array.Empty<UnitRect>(), 0.5, 0.5, "#FF204060");

        private static List<PhotoFacts> MakePhotos(int count, double w = 1920, double h = 1080) =>
            Enumerable.Range(1, count).Select(i => MakePhoto(i, w, h)).ToList();

        [Fact]
        public void PartitionPhotos_SoloMode_Produces_One_Chunk_Per_Photo()
        {
            var photos = MakePhotos(6);
            var options = new SurpriseOptions
            {
                FewestPictures = 1,
                MostPictures = 1
            };

            var chunks = ShowDirector.PartitionPhotos(photos, options, Random.Shared);

            Assert.Equal(6, chunks.Count);
            Assert.All(chunks, c => Assert.Single(c));
        }

        [Fact]
        public void PartitionPhotos_MixMode_Partitions_All_Photos_Within_Bounds()
        {
            var photos = MakePhotos(15);
            var options = new SurpriseOptions
            {
                FewestPictures = 1,
                MostPictures = 4
            };

            var chunks = ShowDirector.PartitionPhotos(photos, options, Random.Shared);

            Assert.NotEmpty(chunks);
            // Verify every photo is accounted for exactly once
            int totalPhotosInChunks = chunks.Sum(c => c.Count);
            Assert.Equal(15, totalPhotosInChunks);

            // Verify each chunk is within bounds
            foreach (var chunk in chunks)
            {
                Assert.InRange(chunk.Count, 1, 4);
            }
        }

        [Fact]
        public void PartitionPhotos_WideRange_Uses_The_Top_Of_The_Range()
        {
            var photos = MakePhotos(181);
            var options = new SurpriseOptions
            {
                FewestPictures = 1,
                MostPictures = 16
            };

            var chunks = ShowDirector.PartitionPhotos(photos, options, new Random(1));

            Assert.Equal(181, chunks.Sum(c => c.Count));
            Assert.All(chunks, c => Assert.InRange(c.Count, 1, 16));
            Assert.Contains(chunks, c => c.Count == 16);
            Assert.Contains(chunks, c => c.Count > 6);
        }

        [Fact]
        public async Task DraftAsync_Generates_Complete_Project_Off_Thread()
        {
            var photos = MakePhotos(8);
            var options = new SurpriseOptions
            {
                FewestPictures = 1,
                MostPictures = 4,
                MotionAmount = 0.5
            };

            var progressReports = new List<ShowDraftProgress>();
            var progress = new Progress<ShowDraftProgress>(p => progressReports.Add(p));

            var project = await ShowDirector.DraftAsync(
                photos,
                options,
                canvasWidth: 1920,
                canvasHeight: 1080,
                projectTitle: "Automated Journey",
                progress: progress);

            Assert.NotNull(project);
            Assert.Equal("Automated Journey", project.Title);
            Assert.NotEmpty(project.Scenes);

            // Verify all photos made it into scenes
            var referencedPhotos = project.Scenes.SelectMany(s => s.GetPhotoPaths()).ToList();
            Assert.Equal(8, referencedPhotos.Count);

            // Verify each scene has valid dwell, layout, and camera override
            foreach (var scene in project.Scenes)
            {
                Assert.NotNull(scene.Layout);
                Assert.True(scene.DwellSeconds > 0);
                Assert.False(string.IsNullOrEmpty(scene.Transition));
                Assert.NotNull(scene.CameraOverride);
                Assert.True(scene.CameraOverride.Start.Zoom >= 1.0);
                Assert.True(scene.CameraOverride.End.Zoom >= 1.0);
            }
        }

        [Fact]
        public async Task DraftAsync_Supports_Cancellation()
        {
            var photos = MakePhotos(20);
            var options = new SurpriseOptions { FewestPictures = 1, MostPictures = 3 };

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await ShowDirector.DraftAsync(
                    photos,
                    options,
                    canvasWidth: 1920,
                    canvasHeight: 1080,
                    cancellationToken: cts.Token);
            });
        }

        [Fact]
        public async Task DraftAsync_Produces_Varied_Cinematic_Motion_And_No_Consecutive_Repeats()
        {
            var photos = MakePhotos(60);
            var options = new SurpriseOptions
            {
                FewestPictures = 1,
                MostPictures = 3,
                MotionAmount = 0.5,
                CameraMode = "Auto"
            };

            var project = await ShowDirector.DraftAsync(
                photos,
                options,
                canvasWidth: 1920,
                canvasHeight: 1080,
                projectTitle: "Diverse Presentation");

            Assert.True(project.Scenes.Count >= 20);

            var kinds = project.Scenes.Select(s => s.CameraOverride?.Kind).ToList();
            
            // Check for variety: must contain multiple distinct kinds
            var distinct = kinds.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToHashSet();
            Assert.True(distinct.Count >= 3, $"Expected at least 3 distinct motion kinds, got: {string.Join(", ", distinct)}");

            // Verify no consecutive repeats
            for (int i = 1; i < kinds.Count; i++)
            {
                Assert.NotEqual(kinds[i - 1], kinds[i]);
            }
        }
    }
}

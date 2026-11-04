using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class ShowProjectTests
    {
        [Fact]
        public void ShowProject_Serialization_RoundTrips_Fidelity()
        {
            var project = new ShowProject
            {
                Title = "Summer Vacation 2026",
                GlobalDefaults = new ShowGlobalDefaults
                {
                    DefaultDwellSeconds = 14.0,
                    SmartPace = true,
                    CameraMotionAmount = 0.65,
                    CameraMode = "Zoom in",
                    DefaultTransition = "ThroughDark"
                },
                Soundtrack = new ShowSoundtrackSettings
                {
                    MusicFolder = @"C:\Music\Acoustic",
                    MusicLowestStars = 4.0,
                    MusicIncludesUnrated = false,
                    MusicVolume = 82.0
                }
            };

            // Scene 1: Multi-Image 3-Photo Collage
            var scene1 = new ShowScene
            {
                Title = "The Mountain Trail",
                DwellSeconds = 15.0,
                Transition = "Crossfade",
                Recipe = "Hero spotlight"
            };
            scene1.Layout.Canvas.Aspect = "16:9";
            scene1.Layout.Canvas.Surface = "Vignette";
            scene1.Layout.State.Lines = 2;
            scene1.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\trail1.jpg", Slot = 0 });
            scene1.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\trail2.jpg", Slot = 1 });
            scene1.Layout.Freeform = new List<FreeformCellDto>
            {
                new FreeformCellDto { Path = @"C:\p\flower.png", X = 0.7, Y = 0.6, Width = 0.25, Height = 0.25 }
            };
            project.Scenes.Add(scene1);

            // Scene 2: Solo Portrait with Ken Burns Camera Override
            var scene2 = new ShowScene
            {
                Title = "Summit Portrait",
                DwellSeconds = 8.0,
                Recipe = "Solo",
                CameraOverride = new CameraKeyframeOverride
                {
                    Start = new CameraViewDto(1.0, 0.5, 0.5),
                    End = new CameraViewDto(2.2, 0.45, 0.35),
                    StartCrop = new NormalizedRectDto(0, 0, 1, 1),
                    EndCrop = new NormalizedRectDto(0.2, 0.1, 0.5, 0.5)
                }
            };
            scene2.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\summit.jpg" });
            project.Scenes.Add(scene2);

            // Serialize & Deserialize
            string json = project.ToJson();
            Assert.False(string.IsNullOrWhiteSpace(json));

            var restored = ShowProject.FromJson(json);
            Assert.NotNull(restored);
            Assert.Equal("Summer Vacation 2026", restored.Title);
            Assert.Equal(14.0, restored.GlobalDefaults.DefaultDwellSeconds);
            Assert.Equal("Zoom in", restored.GlobalDefaults.CameraMode);
            Assert.Equal(82.0, restored.Soundtrack.MusicVolume);
            Assert.Equal(2, restored.Scenes.Count);

            // Verify Scene 1
            var rScene1 = restored.Scenes[0];
            Assert.Equal("The Mountain Trail", rScene1.Title);
            Assert.Equal(15.0, rScene1.DwellSeconds);
            Assert.Equal("Vignette", rScene1.Layout.Canvas.Surface);
            Assert.Equal(2, rScene1.Layout.Photos.Count);
            Assert.Single(rScene1.Layout.Freeform!);
            Assert.Equal(3, rScene1.PhotoCount);
            Assert.False(rScene1.IsSolo);

            // Verify Scene 2
            var rScene2 = restored.Scenes[1];
            Assert.Equal("Summit Portrait", rScene2.Title);
            Assert.True(rScene2.IsSolo);
            Assert.NotNull(rScene2.CameraOverride);
            Assert.Equal(2.2, rScene2.CameraOverride.End.Zoom);
            Assert.Equal(0.45, rScene2.CameraOverride.End.X);
            Assert.Equal(0.35, rScene2.CameraOverride.End.Y);
        }

        [Fact]
        public void ShowProject_Calculates_Visible_Counts_And_Durations()
        {
            var project = new ShowProject();
            project.GlobalDefaults.DefaultDwellSeconds = 10.0;

            project.Scenes.Add(new ShowScene { DwellSeconds = 8.0, IsHidden = false });
            project.Scenes.Add(new ShowScene { DwellSeconds = 12.0, IsHidden = true });
            project.Scenes.Add(new ShowScene { DwellSeconds = null, IsHidden = false }); // Inherits 10.0

            Assert.Equal(3, project.Scenes.Count);
            Assert.Equal(2, project.VisibleSceneCount);
            Assert.Equal(18.0, project.TotalEstimatedDuration); // 8 + 10
        }

        [Fact]
        public void ShowProject_FromCinematicProject_Converts_Clips_And_Crops()
        {
            var legacy = new Cinematic.Data.CinematicProject
            {
                MusicFolder = @"C:\Music",
                MusicPath = @"C:\Music\song.mp3",
                MusicLowestStars = 4.5,
                MusicVolume = 70.0
            };
            legacy.GlobalDefaults.IntensityPercent = 20.0;
            legacy.GlobalDefaults.UseNarrativeArc = true;

            legacy.Sequence.Add(new Cinematic.Data.TrackClipRecord { FilePath = @"C:\p\photo1.jpg", Id = "clip1", IsHidden = false });
            legacy.Sequence.Add(new Cinematic.Data.TrackClipRecord { FilePath = @"C:\p\photo2.jpg", Id = "clip2", IsHidden = true });

            // Add manual overrides in ledger
            legacy.Ledger[@"C:\p\photo1.jpg"] = new Cinematic.Data.SlideSequenceDefinition
            {
                FileName = @"C:\p\photo1.jpg",
                DurationSeconds = 7.5,
                IsUserOverridden = true,
                StartCrop = new Cinematic.Data.NormalizedRect { X = 0.0, Y = 0.0, Width = 1.0, Height = 1.0 },
                EndCrop = new Cinematic.Data.NormalizedRect { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 }
            };

            var unified = ShowProject.FromCinematicProject(legacy, "Converted Show");

            Assert.NotNull(unified);
            Assert.Equal("Converted Show", unified.Title);
            Assert.Equal(@"C:\Music", unified.Soundtrack.MusicFolder);
            Assert.Equal(@"C:\Music\song.mp3", unified.Soundtrack.SpecificTrackPath);
            Assert.Equal(4.5, unified.Soundtrack.MusicLowestStars);
            Assert.Equal(70.0, unified.Soundtrack.MusicVolume);

            Assert.Equal(2, unified.Scenes.Count);

            // Clip 1
            var s1 = unified.Scenes[0];
            Assert.Equal("clip1", s1.Id);
            Assert.False(s1.IsHidden);
            Assert.True(s1.IsSolo);
            Assert.Equal(7.5, s1.DwellSeconds);
            Assert.Equal(@"C:\p\photo1.jpg", s1.GetPhotoPaths().Single());
            Assert.NotNull(s1.CameraOverride);
            Assert.NotNull(s1.CameraOverride.StartCrop);
            Assert.NotNull(s1.CameraOverride.EndCrop);
            Assert.Equal(1.0, s1.CameraOverride.StartCrop.Width);
            Assert.Equal(0.5, s1.CameraOverride.EndCrop.Width);
            Assert.Equal(2.0, s1.CameraOverride.End.Zoom, 2); // 1.0 / 0.5 = 2.0

            // Clip 2 (hidden, no override)
            var s2 = unified.Scenes[1];
            Assert.Equal("clip2", s2.Id);
            Assert.True(s2.IsHidden);
            Assert.Null(s2.DwellSeconds); // Inherits default
            Assert.Null(s2.CameraOverride);
        }

        [Fact]
        public void CameraKeyframeOverride_ToCameraMove_Builds_Covered_Move()
        {
            var keyframes = new CameraKeyframeOverride
            {
                Start = new CameraViewDto(1.0, 0.5, 0.5),
                End = new CameraViewDto(1.8, 0.6, 0.4)
            };

            var move = keyframes.ToCameraMove(10.0);
            Assert.NotNull(move);
            Assert.Equal(CameraKind.ZoomIn, move.Kind);
            Assert.Equal(10.0, move.Seconds);

            var atStart = move.At(0.0);
            Assert.Equal(1.0, atStart.Zoom, 2);
            Assert.Equal(0.5, atStart.X, 2);
            Assert.Equal(0.5, atStart.Y, 2);

            var atEnd = move.At(10.0);
            Assert.Equal(1.8, atEnd.Zoom, 2);
            Assert.Equal(0.6, atEnd.X, 2);
            Assert.Equal(0.4, atEnd.Y, 2);
        }

        [Fact]
        public void ShowProject_GetPhotoPaths_Returns_Distinct_Paths_Across_Scenes()
        {
            var project = new ShowProject();
            var s1 = new ShowScene();
            s1.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\photo1.jpg" });
            s1.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\photo2.jpg" });
            s1.Layout.Freeform = new List<FreeformCellDto> { new FreeformCellDto { Path = @"C:\p\photo3.png" } };

            var s2 = new ShowScene();
            s2.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\photo2.jpg" });
            s2.Layout.Photos.Add(new PhotoSlotDto { Path = @"C:\p\photo4.jpg" });

            project.Scenes.Add(s1);
            project.Scenes.Add(s2);

            var paths = project.GetPhotoPaths().ToList();
            Assert.Equal(4, paths.Count);
            Assert.Contains(@"C:\p\photo1.jpg", paths);
            Assert.Contains(@"C:\p\photo2.jpg", paths);
            Assert.Contains(@"C:\p\photo3.png", paths);
            Assert.Contains(@"C:\p\photo4.jpg", paths);
        }

        [Fact]
        public void Scene_counts_the_canvas_and_not_the_image_pool_beside_it()
        {
            var scene = new ShowScene();
            scene.Layout.State.FramedSlots = 3;
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 0, Path = @"C:\p\a.jpg" });
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 1, Path = @"C:\p\b.jpg" });
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 2, Path = string.Empty });
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 3, Path = @"C:\p\pool1.jpg" });
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 4, Path = @"C:\p\pool2.jpg" });
            scene.Layout.Freeform = new List<FreeformCellDto>
            {
                new FreeformCellDto { Path = @"C:\p\float.jpg" }
            };

            Assert.Equal(new[] { @"C:\p\a.jpg", @"C:\p\b.jpg", @"C:\p\float.jpg" }, scene.GetPhotoPaths().ToArray());
            // Two pictures, one empty canvas slot, one floating picture. The pool is not in the count.
            Assert.Equal(4, scene.PhotoCount);
            Assert.False(scene.IsSolo);

            scene.KeepCanvasPhotos();
            Assert.Equal(3, scene.Layout.Photos.Count);
            Assert.All(scene.Layout.Photos, photo => Assert.True(photo.Slot < 3));
            Assert.Equal(new[] { @"C:\p\a.jpg", @"C:\p\b.jpg", @"C:\p\float.jpg" }, scene.GetPhotoPaths().ToArray());

            var draft = new ShowScene();
            draft.Layout.Photos.Add(new PhotoSlotDto { Slot = 0, Path = @"C:\p\a.jpg" });
            draft.Layout.Photos.Add(new PhotoSlotDto { Slot = 1, Path = @"C:\p\b.jpg" });
            draft.KeepCanvasPhotos();
            Assert.Equal(2, draft.Layout.Photos.Count);
            Assert.Equal(2, draft.GetPhotoPaths().Count());
        }

        [Fact]
        public void Canvas_is_stored_only_into_the_scene_it_is_showing()
        {
            Assert.False(ShowProject.ShouldStoreCanvas(canvasSceneId: null, sceneId: "scene-1", explicitCommit: false));
            Assert.False(ShowProject.ShouldStoreCanvas(canvasSceneId: "scene-2", sceneId: "scene-1", explicitCommit: false));
            Assert.True(ShowProject.ShouldStoreCanvas(canvasSceneId: "scene-1", sceneId: "scene-1", explicitCommit: false));
            Assert.True(ShowProject.ShouldStoreCanvas(canvasSceneId: null, sceneId: "scene-1", explicitCommit: true));
            Assert.False(ShowProject.ShouldStoreCanvas(canvasSceneId: "scene-1", sceneId: null, explicitCommit: true));
        }

        [Fact]
        public void Play_transport_says_pause_while_running()
        {
            Assert.Equal("Pause", ShowProject.PlayTransportText(playing: true, storyboard: true, sceneNumber: 4, sceneCount: 33));
            Assert.Equal("Pause", ShowProject.PlayTransportText(playing: true, storyboard: false, sceneNumber: 1, sceneCount: 0));
            Assert.Equal("Play Show", ShowProject.PlayTransportText(playing: false, storyboard: true, sceneNumber: 1, sceneCount: 33));
            Assert.Equal("Play Show · 4/33", ShowProject.PlayTransportText(playing: false, storyboard: true, sceneNumber: 4, sceneCount: 33));
            Assert.Equal("Play Mix", ShowProject.PlayTransportText(playing: false, storyboard: false, sceneNumber: 1, sceneCount: 0));
        }

        [Fact]
        public void ImagesNotInShow_KeepsPoolOrder_AndLeavesOutEveryImageAScenesShows()
        {
            var show = new ShowProject();
            var scene = new ShowScene();
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 0, Path = @"C:\p\b.jpg" });
            scene.Layout.Freeform = new List<FreeformCellDto> { new() { Path = @"C:\P\D.JPG" } };
            show.Scenes.Add(scene);

            var fresh = show.ImagesNotInShow(new[] { @"C:\p\a.jpg", @"C:\p\b.jpg", @"C:\p\c.jpg", @"C:\p\d.jpg", @"C:\p\a.jpg", "" });

            Assert.Equal(new[] { @"C:\p\a.jpg", @"C:\p\c.jpg" }, fresh);
        }

        [Fact]
        public async System.Threading.Tasks.Task DraftSceneAsync_UsesOnlyTheGivenImages_WithinTheRange()
        {
            var facts = Enumerable.Range(0, 10)
                .Select(i => new PhotoFacts($@"C:\p\{i}.jpg", 1600, 1200, Array.Empty<UnitRect>(), 0.5, 0.5, "#808080"))
                .ToList();
            var options = new SurpriseOptions { FewestPictures = 2, MostPictures = 4 };

            for (int run = 0; run < 20; run++)
            {
                var scene = await ShowDirector.DraftSceneAsync(facts, options, 1920, 1080, previous: null);

                Assert.NotNull(scene);
                var paths = scene!.GetPhotoPaths().ToList();
                Assert.InRange(paths.Count, 2, 4);
                Assert.All(paths, p => Assert.Contains(p, facts.Select(f => f.Path)));
                Assert.NotNull(scene.CameraOverride);
            }
        }

        [Fact]
        public void ThumbnailBoxes_ThreeImagesInOneRow_AreThreeColumns()
        {
            var scene = new ShowScene();
            scene.Layout.State.Lines = 1;
            for (int i = 0; i < 3; i++)
                scene.Layout.Photos.Add(new PhotoSlotDto { Slot = i, Path = $@"C:\p\{i}.jpg" });

            var boxes = scene.ThumbnailBoxes();

            Assert.Equal(3, boxes.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal($@"C:\p\{i}.jpg", boxes[i].Path);
                Assert.Equal(i / 3.0, boxes[i].X, 6);
                Assert.Equal(0, boxes[i].Y, 6);
                Assert.Equal(1 / 3.0, boxes[i].W, 6);
                Assert.Equal(1, boxes[i].H, 6);
            }
        }

        [Fact]
        public void ThumbnailBoxes_FollowSavedSplit_LeaveOutPool_AndScaleFloatingCells()
        {
            var scene = new ShowScene();
            scene.Layout.Canvas.Width = 1600;
            scene.Layout.Canvas.Height = 900;
            scene.Layout.State.FramedSlots = 2;
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 0, Path = @"C:\p\a.jpg" });
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 1, Path = @"C:\p\b.jpg" });
            scene.Layout.Photos.Add(new PhotoSlotDto { Slot = 2, Path = @"C:\p\pool.jpg" });
            // A banner across the top, a quarter of the height, over the second image.
            scene.Layout.Mosaic = new MosaicNodeDto
            {
                Vertical = false,
                Weights = new List<double> { 1, 3 },
                Children = new List<MosaicNodeDto> { new() { Leaf = 0 }, new() { Leaf = 1 } }
            };
            scene.Layout.Freeform = new List<FreeformCellDto> { new() { Path = @"C:\p\f.jpg", X = 800, Y = 450, Width = 400, Height = 225 } };

            var boxes = scene.ThumbnailBoxes();

            Assert.Equal(new[] { @"C:\p\a.jpg", @"C:\p\b.jpg", @"C:\p\f.jpg" }, boxes.Select(b => b.Path));
            Assert.Equal(0.25, boxes[0].H, 6);
            Assert.Equal(0.25, boxes[1].Y, 6);
            Assert.Equal(1, boxes[1].W, 6);
            Assert.Equal((0.5, 0.5, 0.25, 0.25), (boxes[2].X, boxes[2].Y, boxes[2].W, boxes[2].H));
        }
    }
}

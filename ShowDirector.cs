using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    public readonly record struct ShowDraftProgress(int CompletedScenes, int TotalEstimatedScenes, string CurrentSceneTitle, double Percent);

    /// <summary>
    /// Generative Director that batches photos into a cohesive, sequenced presentation (ShowProject).
    /// Dispatches layout generation to a background task (Task.Run) to guarantee the UI thread never hitches.
    /// </summary>
    public static class ShowDirector
    {
        public const string DefaultShowTitle = "Auto-Drafted Presentation";

        /// <summary>
        /// Generates a complete ShowProject from a photo pool asynchronously off the UI thread.
        /// </summary>
        public static async Task<ShowProject> DraftAsync(
            IReadOnlyList<PhotoFacts> photos,
            SurpriseOptions? options,
            double canvasWidth = 1920,
            double canvasHeight = 1080,
            string? projectTitle = null,
            IProgress<ShowDraftProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (photos == null || photos.Count == 0)
            {
                return new ShowProject { Title = projectTitle ?? DefaultShowTitle };
            }

            options ??= new SurpriseOptions();
            double w = Math.Max(320, canvasWidth);
            double h = Math.Max(240, canvasHeight);

            return await Task.Run(() =>
                DraftInternal(photos, options, w, h, projectTitle, progress, cancellationToken),
                cancellationToken);
        }

        private static ShowProject DraftInternal(
            IReadOnlyList<PhotoFacts> rawPhotos,
            SurpriseOptions options,
            double width,
            double height,
            string? projectTitle,
            IProgress<ShowDraftProgress>? progress,
            CancellationToken cancellationToken)
        {
            var usable = rawPhotos.Where(p => p.Width >= 1 && p.Height >= 1).ToList();
            if (options.SkipScreens)
            {
                usable = LayoutComposer.LeaveOutScreensAndRepeats(usable);
            }

            if (usable.Count == 0)
            {
                return new ShowProject { Title = projectTitle ?? DefaultShowTitle };
            }

            var project = new ShowProject
            {
                Title = projectTitle ?? DefaultShowTitle,
                GlobalDefaults = new ShowGlobalDefaults
                {
                    DefaultDwellSeconds = options.AutoPlayRandom ? (options.RandomFewestSeconds + options.RandomMostSeconds) / 2.0 : 12.0,
                    SmartPace = options.SmartPace,
                    CameraMotionAmount = options.MotionAmount,
                    CameraMode = options.CameraMode
                }
            };

            var random = new Random(Random.Shared.Next());
            var chunks = PartitionPhotos(usable, options, random);
            int totalChunks = chunks.Count;

            string? lastRecipe = null;
            LayoutTransition? lastTransition = null;
            CameraKind? lastCameraKind = null;
            bool? lastWasLong = null;

            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = chunks[i];
                int seed = random.Next();

                var composition = LayoutComposer.Compose(
                    chunk,
                    width,
                    height,
                    seed,
                    avoid: lastRecipe,
                    rasterScale: 1.0,
                    only: null,
                    options: options);

                if (composition == null)
                {
                    // Fallback to simple Solo if a chunk fails composition
                    composition = LayoutComposer.Compose(
                        new[] { chunk[0] },
                        width,
                        height,
                        seed,
                        options: options);
                }

                if (composition == null) continue;

                // Pacing: adaptive dwell calculation
                double dwell = options.NextPlaySeconds(random, composition.Cells.Count, ref lastWasLong);

                // Transition selection
                var transition = LayoutTransitions.Choose(options.TransitionVariety, lastTransition, random);

                // Camera planning
                var subjects = composition.CameraSubjects();
                var move = CameraMove.Plan(
                    subjects,
                    options.MotionAmount,
                    options.MotionZoomChance,
                    options.MotionFaceChance,
                    dwell,
                    lastCameraKind,
                    random,
                    options.ForcedCameraKind);

                var scene = new ShowScene
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = $"{composition.Recipe} ({composition.Cells.Count})",
                    Recipe = composition.Recipe,
                    Layout = composition.ToLayoutFile(),
                    DwellSeconds = dwell,
                    Transition = transition.ToString()
                };

                if (move != null)
                {
                    scene.CameraOverride = new CameraKeyframeOverride
                    {
                        Start = new CameraViewDto(move.Start.Zoom, move.Start.X, move.Start.Y),
                        End = new CameraViewDto(move.End.Zoom, move.End.X, move.End.Y),
                        StartCrop = CameraViewToRect(move.Start),
                        EndCrop = CameraViewToRect(move.End)
                    };
                    lastCameraKind = move.Kind;
                }

                project.Scenes.Add(scene);

                lastRecipe = composition.Recipe;
                lastTransition = transition;

                double percent = (double)(i + 1) / totalChunks * 100.0;
                progress?.Report(new ShowDraftProgress(i + 1, totalChunks, scene.Title, percent));
            }

            return project;
        }

        /// <summary>
        /// Partitions photos into distinct scenes with organic variety based on user options.
        /// </summary>
        public static List<List<PhotoFacts>> PartitionPhotos(
            IReadOnlyList<PhotoFacts> photos,
            SurpriseOptions options,
            Random random)
        {
            var result = new List<List<PhotoFacts>>();
            if (photos == null || photos.Count == 0) return result;

            int fewest = Math.Clamp(options.FewestPictures, 1, 16);
            int most = Math.Clamp(options.MostPictures, fewest, 16);

            // Solo mode: every photo is its own scene
            if (fewest == 1 && most == 1)
            {
                foreach (var p in photos)
                    result.Add(new List<PhotoFacts> { p });
                return result;
            }

            // One scene uses the top of the range, so "1 to 16" is not a list that never reaches 16.
            bool placeTop = most > fewest && photos.Count >= most;
            int topAt = placeTop ? random.Next(Math.Max(1, photos.Count / most)) : -1;
            bool placedTop = !placeTop;

            int index = 0;
            int scene = 0;
            while (index < photos.Count)
            {
                int remaining = photos.Count - index;
                int target;
                if (!placedTop && (scene == topAt || remaining <= most))
                {
                    target = Math.Min(most, remaining);
                    placedTop = true;
                }
                else
                {
                    int high = Math.Min(most, remaining);
                    int low = Math.Min(fewest, high);
                    target = random.Next(low, high + 1);
                }

                // A leftover smaller than the minimum joins this scene, or the next scene is left large enough.
                int leftover = remaining - target;
                if (leftover > 0 && leftover < fewest)
                {
                    target = remaining <= most
                        ? remaining
                        : Math.Clamp(remaining - fewest, fewest, most);
                }

                target = Math.Min(target, remaining);
                var chunk = photos.Skip(index).Take(target).ToList();
                result.Add(chunk);
                index += target;
                scene++;
            }

            return result;
        }

        private static NormalizedRectDto CameraViewToRect(CameraView view)
        {
            double z = Math.Max(1.0, view.Zoom);
            double w = 1.0 / z;
            double h = 1.0 / z;
            double x = Math.Clamp(view.X - w / 2.0, 0.0, 1.0 - w);
            double y = Math.Clamp(view.Y - h / 2.0, 0.0, 1.0 - h);
            return new NormalizedRectDto(x, y, w, h);
        }
    }
}

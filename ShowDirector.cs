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
                    CameraMotionAmount = (options.MotionLeast + options.MotionMost) / 2,
                    CameraMode = options.CameraMode
                }
            };

            var random = new Random(Random.Shared.Next());
            var chunks = PartitionPhotos(usable, options, random);
            int totalChunks = chunks.Count;

            string? lastRecipe = null;
            LayoutTransition? lastTransition = null;
            CameraKind? lastKind = null;
            bool? lastWasLong = null;

            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = chunks[i];
                int seed = random.Next();

                // Fall back to one picture if the chunk cannot be laid out.
                var composition = SceneCall.Make(new SceneRequest(chunk, width, height, options, 0, seed, lastRecipe))?.Composition
                    ?? SceneCall.Make(new SceneRequest(new[] { chunk[0] }, width, height, options, 0, seed))?.Composition;

                if (composition == null) continue;

                var scene = BuildScene(composition, options, random, ref lastKind, lastTransition, ref lastWasLong, out var transition);
                project.Scenes.Add(scene);

                lastRecipe = composition.Recipe;
                lastTransition = transition;

                double percent = (double)(i + 1) / totalChunks * 100.0;
                progress?.Report(new ShowDraftProgress(i + 1, totalChunks, scene.Title ?? "", percent));
            }

            return project;
        }

        /// <summary>
        /// One more scene for a show, from the first of <paramref name="photos"/> in their order, as many as the
        /// images-per-scene range allows. It avoids repeating the previous scene's design, move and transition.
        /// Null when nothing can be laid out.
        /// </summary>
        /// <param name="takeAll">The photos are the scene's hand, already sized within Images per scene: use them all.</param>
        public static Task<ShowScene?> DraftSceneAsync(IReadOnlyList<PhotoFacts> photos, SurpriseOptions? options,
            double canvasWidth, double canvasHeight, ShowScene? previous, bool takeAll = false)
        {
            options ??= new SurpriseOptions();
            double width = Math.Max(320, canvasWidth);
            double height = Math.Max(240, canvasHeight);
            return Task.Run(() =>
            {
                var usable = photos.Where(p => p.Width >= 1 && p.Height >= 1).ToList();
                if (usable.Count == 0) return null;

                var random = new Random(Random.Shared.Next());
                int fewest = Math.Clamp(options.FewestPictures, 1, MosaicLayout.MaxShown);
                int most = Math.Clamp(options.MostPictures, fewest, MosaicLayout.MaxShown);
                var chunk = usable.Take(Math.Min(usable.Count, takeAll ? MosaicLayout.MaxShown : random.Next(fewest, most + 1))).ToList();

                var composition = SceneCall.Make(new SceneRequest(chunk, width, height, options, 0, random.Next(), previous?.Recipe))?.Composition
                    ?? SceneCall.Make(new SceneRequest(new[] { chunk[0] }, width, height, options, 0, random.Next()))?.Composition;
                if (composition == null) return (ShowScene?)null;

                CameraKind? lastKind = KindOf(previous?.CameraOverride?.Kind);
                // Short and long take turns across scenes drafted one at a time, as they do in a show drafted at once.
                bool? lastWasLong = options.WasLong(previous?.DwellSeconds);
                LayoutTransition? lastTransition = Enum.TryParse<LayoutTransition>(previous?.Transition, out var parsed) ? parsed : null;
                return BuildScene(composition, options, random, ref lastKind, lastTransition, ref lastWasLong, out _);
            });
        }

        /// <summary>
        /// One scene from a finished composition: its hold, the transition into it, and a camera path planned once by
        /// the one engine call, from the same camera settings the canvas uses. A scene that does not move is saved as Still.
        /// </summary>
        private static ShowScene BuildScene(Composition composition, SurpriseOptions options, Random random,
            ref CameraKind? lastKind, LayoutTransition? lastTransition, ref bool? lastWasLong, out LayoutTransition transition)
        {
            double dwell = options.NextPlaySeconds(random, composition.Cells.Count, ref lastWasLong);
            transition = options.PickTransition(lastTransition, random);

            var scene = new ShowScene
            {
                Id = Guid.NewGuid().ToString(),
                Title = $"{composition.Recipe} ({composition.Cells.Count})",
                Recipe = composition.Recipe,
                Layout = composition.ToLayoutFile(),
                DwellSeconds = dwell,
                Transition = transition.ToString()
            };

            var move = SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), composition.Width, composition.Height, options,
                dwell, random.Next(), LastKind: lastKind, Keep: KeptLayout.Of(composition)))?.Move;
            if (move == null)
            {
                scene.CameraOverride = new CameraKeyframeOverride(
                    new CameraViewDto(1.0, 0.5, 0.5),
                    new CameraViewDto(1.0, 0.5, 0.5),
                    "Still")
                {
                    StartCrop = new NormalizedRectDto(0, 0, 1, 1),
                    EndCrop = new NormalizedRectDto(0, 0, 1, 1)
                };
            }
            else
            {
                // Every shot, its curve, bend and turn: the scene plays what was planned.
                scene.CameraOverride = CameraKeyframeOverride.FromMove(move, move.Shots.Count > 1 ? "TwoShot" : move.Kind.ToString());
                lastKind = move.Kind;
            }
            return scene;
        }

        /// <summary>The kind of move a saved scene's path is, as the one planner names it; null for a still or a two-shot.</summary>
        public static CameraKind? KindOf(string? savedKind) => savedKind?.ToLowerInvariant() switch
        {
            "zoomin" => CameraKind.ZoomIn,
            "zoomout" => CameraKind.ZoomOut,
            "pan" or "drift" => CameraKind.Drift,
            _ => null
        };

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

    }
}

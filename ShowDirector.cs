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
            string? lastMotionKind = null;
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

                var scene = BuildScene(composition, options, random, ref lastMotionKind, lastTransition, ref lastWasLong, out var transition);
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
        public static Task<ShowScene?> DraftSceneAsync(IReadOnlyList<PhotoFacts> photos, SurpriseOptions? options,
            double canvasWidth, double canvasHeight, ShowScene? previous)
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
                var chunk = usable.Take(Math.Min(usable.Count, random.Next(fewest, most + 1))).ToList();

                var composition = LayoutComposer.Compose(chunk, width, height, random.Next(), avoid: previous?.Recipe, rasterScale: 1.0, only: null, options: options)
                    ?? LayoutComposer.Compose(new[] { chunk[0] }, width, height, random.Next(), options: options);
                if (composition == null) return (ShowScene?)null;

                string? lastMotion = previous?.CameraOverride?.Kind;
                bool? lastWasLong = null;
                LayoutTransition? lastTransition = Enum.TryParse<LayoutTransition>(previous?.Transition, out var parsed) ? parsed : null;
                return BuildScene(composition, options, random, ref lastMotion, lastTransition, ref lastWasLong, out _);
            });
        }

        /// <summary>One scene from a finished composition: its hold, the transition into it, and a camera path planned once, aimed at faces by the options.</summary>
        private static ShowScene BuildScene(Composition composition, SurpriseOptions options, Random random,
            ref string? lastMotionKind, LayoutTransition? lastTransition, ref bool? lastWasLong, out LayoutTransition transition)
        {
            // Pacing: adaptive dwell calculation
            double dwell = options.NextPlaySeconds(random, composition.Cells.Count, ref lastWasLong);

            // Transition selection
            transition = LayoutTransitions.Choose(options.TransitionVariety, lastTransition, random);

            // Camera planning: organic, varied motion sequence tailored to layout & avoiding consecutive repeats
            var subjects = composition.CameraSubjects();
            string chosenMotion = ChooseMotionKind(composition, options, lastMotionKind, random);
            lastMotionKind = chosenMotion;

            var scene = new ShowScene
            {
                Id = Guid.NewGuid().ToString(),
                Title = $"{composition.Recipe} ({composition.Cells.Count})",
                Recipe = composition.Recipe,
                Layout = composition.ToLayoutFile(),
                DwellSeconds = dwell,
                Transition = transition.ToString()
            };

            if (chosenMotion == "Still" || options.MotionAmount <= 0.001)
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
                CameraKind kindToPlan = chosenMotion switch
                {
                    "ZoomIn" => CameraKind.ZoomIn,
                    "ZoomOut" => CameraKind.ZoomOut,
                    _ => CameraKind.Drift
                };

                var move = CameraMove.Plan(
                    subjects,
                    options.MotionAmount,
                    options.MotionZoomChance,
                    options.MotionFaceChance,
                    dwell,
                    null,
                    random,
                    kindToPlan);

                if (move != null)
                {
                    scene.CameraOverride = new CameraKeyframeOverride
                    {
                        Start = new CameraViewDto(move.Start.Zoom, move.Start.X, move.Start.Y),
                        End = new CameraViewDto(move.End.Zoom, move.End.X, move.End.Y),
                        StartCrop = CameraViewToRect(move.Start),
                        EndCrop = CameraViewToRect(move.End),
                        Kind = chosenMotion
                    };
                }
            }

            return scene;
        }

        /// <summary>
        /// Selects an organic, varied camera motion for a scene to ensure the drafted presentation
        /// feels like a professionally edited Ken Burns documentary rather than a repetitive loop.
        /// Guaranteed to avoid consecutive repeats and provides a rich mix of Zoom In, Zoom Out, Pan, and Still.
        /// </summary>
        public static string ChooseMotionKind(
            Composition composition,
            SurpriseOptions options,
            string? lastMotion,
            Random random)
        {
            if (options.MotionAmount <= 0.001) return "Still";

            int cellCount = composition.Cells.Count;

            // Distribution weights tailored to layout complexity
            double pZoomIn, pZoomOut, pPan, pStill;

            if (cellCount >= 5)
            {
                // Complex multi-image collages (e.g. 5+ images): look best still or panning across
                pStill = 0.35;
                pPan = 0.35;
                pZoomIn = 0.15;
                pZoomOut = 0.15;
            }
            else if (cellCount == 1)
            {
                // Solo hero image: push in or reveal out are dramatic
                pStill = 0.05;
                pPan = 0.20;
                pZoomIn = 0.40;
                pZoomOut = 0.35;
            }
            else
            {
                // 2 to 4 images: balanced documentary cadence
                pStill = 0.10;
                pPan = 0.25;
                pZoomIn = 0.35;
                pZoomOut = 0.30;
            }

            // User preference bias from CameraMode setting (if not Auto)
            if (string.Equals(options.CameraMode, SurpriseOptions.CameraZoomIn, StringComparison.OrdinalIgnoreCase))
            {
                pZoomIn = 0.50; pZoomOut = 0.20; pPan = 0.20; pStill = 0.10;
            }
            else if (string.Equals(options.CameraMode, SurpriseOptions.CameraZoomOut, StringComparison.OrdinalIgnoreCase))
            {
                pZoomOut = 0.50; pZoomIn = 0.20; pPan = 0.20; pStill = 0.10;
            }
            else if (string.Equals(options.CameraMode, SurpriseOptions.CameraDrift, StringComparison.OrdinalIgnoreCase))
            {
                pPan = 0.50; pZoomIn = 0.20; pZoomOut = 0.20; pStill = 0.10;
            }

            // Strict anti-repetition: zero out previous motion kind
            if (lastMotion == "ZoomIn") pZoomIn = 0;
            else if (lastMotion == "ZoomOut") pZoomOut = 0;
            else if (lastMotion == "Pan") pPan = 0;
            else if (lastMotion == "Still") pStill = 0;

            double sum = pZoomIn + pZoomOut + pPan + pStill;
            if (sum <= 0)
            {
                return lastMotion == "ZoomIn" ? "ZoomOut" : "ZoomIn";
            }

            double roll = random.NextDouble() * sum;
            if (roll < pZoomIn) return "ZoomIn";
            roll -= pZoomIn;
            if (roll < pZoomOut) return "ZoomOut";
            roll -= pZoomOut;
            if (roll < pPan) return "Pan";
            return "Still";
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

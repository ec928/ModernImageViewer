using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;

namespace ModernImageViewer.Cinematic
{
    public class CinematicDirector
    {
        public Queue<CameraTransform> GetSequence(SceneIntelligence intel, Size bounds, SlideSettings settings, System.Threading.CancellationToken token)
        {
            var queue = new Queue<CameraTransform>();
            if (bounds.Width <= 0 || bounds.Height <= 0) return queue;

            var localIntel = new SceneIntelligence
            {
                ImageSize = intel.ImageSize,
                SaliencyTarget = intel.SaliencyTarget,
                Faces = new List<Rect>(intel.Faces)
            };

            if (settings.StartCrop is Rect startCrop && settings.EndCrop is Rect endCrop)
            {
                var start = CameraMath.CameraFromCrop(startCrop, bounds, localIntel.ImageSize);
                var end = CameraMath.CameraFromCrop(endCrop, bounds, localIntel.ImageSize);
                queue.Enqueue(new CameraTransform
                {
                    StrategyName = "Custom",
                    Technique = "Custom",
                    Mode = LayoutMode.Fullscreen,
                    StartScale = start.Scale,
                    StartPan = start.Pan,
                    EndScale = end.Scale,
                    EndPan = end.Pan,
                    RecommendedDurationSeconds = settings.DurationSeconds,
                    CrossfadeDurationSeconds = 2.5,
                    EaseKind = string.IsNullOrEmpty(settings.CurveOverride) ? "EaseInOut" : settings.CurveOverride,
                    IsSnapZoom = settings.CurveOverride == "SnapZoom"
                });
                return queue;
            }

            GeneratePrimarySequence(queue, localIntel, bounds, settings);

            int targetBeats = Math.Max(1, settings.BeatCount);
            int effectiveBeats = Math.Max(targetBeats, queue.Count);
            double durationPerBeat = settings.DurationSeconds / effectiveBeats;

            foreach (var shot in queue) shot.RecommendedDurationSeconds = durationPerBeat;

            while (queue.Count < targetBeats)
            {
                token.ThrowIfCancellationRequested();
                var lastShot = queue.Last();
                var continuationShot = new CameraTransform
                {
                    StrategyName = "Sequential Combo",
                    Mode = lastShot.Mode,
                    CurveSign = -lastShot.CurveSign,
                    RequiresCut = false,
                    IsSnapZoom = true,
                    Technique = $"Extended Combo (Beat {queue.Count + 1})",
                    RecommendedDurationSeconds = durationPerBeat,
                    CrossfadeDurationSeconds = 1.5
                };

                continuationShot.StartScale = lastShot.EndScale;
                continuationShot.StartPan = lastShot.EndPan;
                continuationShot.StartRotation = lastShot.EndRotation;

                bool isPullOut = queue.Count % 2 != 0;
                float baseFill = Math.Max((float)bounds.Width / (float)localIntel.ImageSize.Width, (float)bounds.Height / (float)localIntel.ImageSize.Height);
                Rect full = new Rect(0, 0, bounds.Width, bounds.Height);

                if (isPullOut)
                {
                    continuationShot.EndScale = baseFill * 1.05f;
                    Vector2 imageCenter = new Vector2((float)localIntel.ImageSize.Width / 2f, (float)localIntel.ImageSize.Height / 2f);
                    continuationShot.EndPan = CameraMath.GetTargetedPan(imageCenter, continuationShot.EndScale, full, localIntel.ImageSize, Random.Shared, false);
                }
                else
                {
                    float targetPushScale = settings.FocusTargetRect.HasValue
                        ? StrategyMath.GetExplicitTargetScale(settings.FocusTargetRect.Value, bounds, localIntel.ImageSize)
                        : queue.First().EndScale;

                    continuationShot.EndScale = Math.Max(lastShot.StartScale, targetPushScale);
                    continuationShot.EndPan = CameraMath.GetTargetedPan(localIntel.SaliencyTarget, continuationShot.EndScale, full, localIntel.ImageSize, Random.Shared, settings.FocusTargetRect.HasValue);
                }

                continuationShot.EndRotation = lastShot.StartRotation;
                queue.Enqueue(continuationShot);
            }

            foreach (var shot in queue) ApplyCurve(shot, settings);
            return queue;
        }

        private static void ApplyCurve(CameraTransform shot, SlideSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.CurveOverride) && settings.CurveOverride != "EaseInOut")
            {
                shot.EaseKind = settings.CurveOverride;
                shot.IsSnapZoom = settings.CurveOverride == "SnapZoom";
            }
            else
            {
                shot.EaseKind = shot.IsSnapZoom ? "SnapZoom" : "EaseInOut";
            }
        }

        private void GeneratePrimarySequence(Queue<CameraTransform> queue, SceneIntelligence intel, Size bounds, SlideSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.TechniqueOverride) && settings.TechniqueOverride != "Auto")
            {
                CameraTransform explicitShot;
                bool hasFaces = intel.Faces.Count > 0;

                switch (settings.TechniqueOverride)
                {
                    case "DiscoveryTrack": explicitShot = hasFaces ? new IntimateTrackingStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared) : new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared); explicitShot.IsSnapZoom = true; break;
                    case "IntimateReveal": explicitShot = hasFaces ? new PortraitHeadshotStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared) : new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared); explicitShot.RequiresCut = true; break;
                    case "EtherealHold": explicitShot = hasFaces ? new NarrativePushInStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared) : new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared); explicitShot.RecommendedDurationSeconds += 5.0; break;
                    default: explicitShot = new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared); break;
                }
                explicitShot.Technique = settings.TechniqueOverride;
                queue.Enqueue(explicitShot);
                return;
            }

            bool runTemplate = settings.UseNarrativeArc && intel.Faces.Count > 0 && Random.Shared.NextDouble() < 0.65;
            bool isPortrait = StrategyMath.GetCropRatio(intel, bounds) > 1.35f;

            if (runTemplate)
            {
                if (isPortrait)
                {
                    var shot1 = new AmbientBlurStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                    var shot2 = new PortraitHeadshotStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                    shot2.RequiresCut = true; shot2.CrossfadeDurationSeconds = 1.2; shot2.Technique = "The Intimate Reveal (Portrait Cut)";
                    queue.Enqueue(shot1); queue.Enqueue(shot2);
                }
                else
                {
                    if (intel.Faces.Count >= 2 && Random.Shared.NextDouble() > 0.4)
                    {
                        var shot1 = new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                        var shot2 = new DialoguePanStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                        shot2.StartScale = shot1.EndScale; shot2.StartPan = shot1.EndPan; shot2.StartRotation = shot1.EndRotation;
                        shot2.RequiresCut = false; shot2.IsSnapZoom = true; shot2.Technique = "Multi-Subject Discovery (Snap-Zoom)";
                        queue.Enqueue(shot1); queue.Enqueue(shot2);
                    }
                    else
                    {
                        int templateChoice = Random.Shared.Next(3);
                        if (templateChoice == 0)
                        {
                            var shot1 = new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                            var shot2 = new PortraitHeadshotStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                            shot2.RequiresCut = true; shot2.CrossfadeDurationSeconds = 1.2; shot2.Technique = "The Intimate Reveal (Cut)";
                            queue.Enqueue(shot1); queue.Enqueue(shot2);
                        }
                        else if (templateChoice == 1)
                        {
                            var shot1 = new StandardFallbackStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                            var shot2 = new IntimateTrackingStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                            shot2.StartScale = shot1.EndScale; shot2.StartPan = shot1.EndPan; shot2.StartRotation = shot1.EndRotation;
                            shot2.RequiresCut = false; shot2.IsSnapZoom = true; shot2.Technique = "The Discovery Track (Snap-Zoom)";
                            queue.Enqueue(shot1); queue.Enqueue(shot2);
                        }
                        else
                        {
                            var shot1 = new NarrativePushInStrategy().CalculateTrajectory(intel, bounds, settings, Random.Shared);
                            shot1.Technique = "The Ethereal Hold"; shot1.RecommendedDurationSeconds += 5.0;
                            queue.Enqueue(shot1);
                        }
                    }
                }
            }
            else
            {
                ICameraStrategy selected = isPortrait ? new AmbientBlurStrategy() : new StandardFallbackStrategy();
                var shot = selected.CalculateTrajectory(intel, bounds, settings, Random.Shared);
                shot.Technique = "Contextual Drift";
                queue.Enqueue(shot);
            }
        }
    }
}
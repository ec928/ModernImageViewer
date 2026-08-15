using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;

namespace ModernImageViewer.Cinematic
{
    public interface ICameraStrategy
    {
        bool CanExecute(SceneIntelligence intel, Size bounds);
        CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd);
    }

    internal static class StrategyMath
    {
        public static float GetCropRatio(SceneIntelligence intel, Size bounds)
        {
            float bw = (float)bounds.Width; float bh = (float)bounds.Height;
            float iw = (float)intel.ImageSize.Width; float ih = (float)intel.ImageSize.Height;
            float scaleToFit = Math.Min(bw / iw, bh / ih);
            float scaleToFill = Math.Max(bw / iw, bh / ih);
            return scaleToFill / scaleToFit;
        }

        public static float CalculateFaceZoom(Rect faceRect, Size bounds, float minScale)
        {
            float bh = (float)bounds.Height; float fh = (float)faceRect.Height;
            float idealZoom = bh / (fh * 3.33f);
            return Math.Max(minScale, idealZoom);
        }

        public static float CalculateMacroZoom(Rect faceRect, Size bounds, float minScale)
        {
            float bh = (float)bounds.Height; float fh = (float)faceRect.Height;
            float idealZoom = bh / (fh * 1.5f);
            return Math.Max(minScale, idealZoom);
        }

        public static Vector2 GetRuleOfThirdsPan(Rect faceRect, float scale, Size bounds, Size imgSize)
        {
            float scaledW = (float)imgSize.Width * scale;
            float scaledH = (float)imgSize.Height * scale;

            float minX = (float)bounds.Width - scaledW;
            float maxX = 0f;
            float minY = (float)bounds.Height - scaledH;
            float maxY = 0f;

            if (minX > maxX) minX = maxX = minX / 2f;
            if (minY > maxY) minY = maxY = minY / 2f;

            float faceCenterX = (float)faceRect.X + ((float)faceRect.Width / 2f);
            float faceCenterY = (float)faceRect.Y + ((float)faceRect.Height * 0.35f);

            float idealX = ((float)bounds.Width / 2f) - (faceCenterX * scale);
            float idealY = ((float)bounds.Height * 0.33f) - (faceCenterY * scale);

            return new Vector2(Math.Clamp(idealX, minX, maxX), Math.Clamp(idealY, minY, maxY));
        }

        public static float GetExplicitTargetScale(Rect targetRect, Size bounds, Size imgSize)
        {
            // PURE FLOAT: Completely eradicates the CS0266 implicit double conversion errors
            float bw = (float)bounds.Width; float bh = (float)bounds.Height;
            float iw = (float)imgSize.Width; float ih = (float)imgSize.Height;
            float tw = (float)targetRect.Width; float th = (float)targetRect.Height;

            float minScale = Math.Max(bw / iw, bh / ih);
            float minW = 1.0f / Math.Max(0.01f, tw);
            float minH = 1.0f / Math.Max(0.01f, th);

            float scaleMultiplier = Math.Max(minW, minH);
            return Math.Min(minScale * scaleMultiplier, minScale * 6.0f);
        }
    }

    public class AmbientBlurStrategy : ICameraStrategy
    {
        public bool CanExecute(SceneIntelligence intel, Size bounds) => StrategyMath.GetCropRatio(intel, bounds) > 1.35f;

        public CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd)
        {
            var transform = new CameraTransform
            {
                StrategyName = "Ambient Blur",
                Technique = "Ambient Blur", // This solves the "None" label issue
                Mode = LayoutMode.Portrait,
                CurveSign = rnd.Next(2) == 0 ? 1f : -1f
            };

            float scaleToFit = Math.Min((float)bounds.Width / (float)intel.ImageSize.Width, (float)bounds.Height / (float)intel.ImageSize.Height);
            float baseZoom = scaleToFit * 1.01f;

            // Dampened scale multiplier loop to preserve image resolution and composition tracking
            float maxZoom = scaleToFit * (1.0f + ((float)settings.IntensityPercent / 100.0f) * 1.5f);

            bool hasManualTarget = settings.FocusTargetRect.HasValue;
            if (settings.FocusTargetRect is { } manualTarget) maxZoom = Math.Max(maxZoom, StrategyMath.GetExplicitTargetScale(manualTarget, bounds, intel.ImageSize));

            bool zoomIn = settings.DirectionMode == 1 || (settings.DirectionMode != 2 && rnd.Next(2) == 0);
            transform.StartScale = settings.DirectionMode == 3 ? maxZoom : (zoomIn ? baseZoom : maxZoom);
            transform.EndScale = settings.DirectionMode == 3 ? maxZoom : (zoomIn ? maxZoom : baseZoom);

            if (settings.UseTilt)
            {
                float maxTilt = 0.5f * (float)(Math.PI / 180.0); // Mild cinematic stabilization
                transform.StartRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
                transform.EndRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
            }

            Rect full = new Rect(0, 0, bounds.Width, bounds.Height);
            transform.StartPan = CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.StartScale, full, intel.ImageSize, rnd, hasManualTarget);
            transform.EndPan = CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.EndScale, full, intel.ImageSize, rnd, hasManualTarget);

            transform.RecommendedDurationSeconds = 12.0 + rnd.NextDouble() * 3.0;
            transform.CrossfadeDurationSeconds = 1.8; // Snappier blend window to keep visual focus clean

            return transform;
        }
    }

    public class StandardFallbackStrategy : ICameraStrategy
    {
        public bool CanExecute(SceneIntelligence intel, Size bounds) => StrategyMath.GetCropRatio(intel, bounds) <= 1.35f;

        public CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd)
        {
            var transform = new CameraTransform { StrategyName = "Standard Contextual Drift", Mode = LayoutMode.Fullscreen, CurveSign = rnd.Next(2) == 0 ? 1f : -1f };
            float minScale = Math.Max((float)bounds.Width / (float)intel.ImageSize.Width, (float)bounds.Height / (float)intel.ImageSize.Height);

            float baseZoom = minScale * 1.03f;
            // Dampened scale multiplier loop to preserve image resolution and composition tracking
            float maxZoom = minScale * (1.0f + ((float)settings.IntensityPercent / 100.0f) * 1.5f);

            bool hasManualTarget = settings.FocusTargetRect.HasValue;
            if (settings.FocusTargetRect is { } manualTarget) maxZoom = Math.Max(maxZoom, StrategyMath.GetExplicitTargetScale(manualTarget, bounds, intel.ImageSize));

            bool zoomIn = settings.DirectionMode == 1 || (settings.DirectionMode != 2 && rnd.Next(2) == 0);
            transform.StartScale = settings.DirectionMode == 3 ? maxZoom : (zoomIn ? baseZoom : maxZoom);
            transform.EndScale = settings.DirectionMode == 3 ? maxZoom : (zoomIn ? maxZoom : baseZoom);

            if (settings.UseTilt)
            {
                float maxTilt = 0.8f * (float)(Math.PI / 180.0);
                transform.StartRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
                transform.EndRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
            }

            Rect full = new Rect(0, 0, bounds.Width, bounds.Height);
            transform.StartPan = CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.StartScale, full, intel.ImageSize, rnd, hasManualTarget);
            transform.EndPan = CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.EndScale, full, intel.ImageSize, rnd, hasManualTarget);

            transform.RecommendedDurationSeconds = 12.0 + rnd.NextDouble() * 3.0;
            transform.CrossfadeDurationSeconds = 1.8;

            return transform;
        }
    }

    public class NarrativePushInStrategy : ICameraStrategy
    {
        public bool CanExecute(SceneIntelligence intel, Size bounds) => intel.Faces.Any() && StrategyMath.GetCropRatio(intel, bounds) <= 1.35f;

        public CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd)
        {
            var transform = new CameraTransform { StrategyName = "Narrative Push-In", Mode = LayoutMode.Fullscreen, CurveSign = rnd.Next(2) == 0 ? 1f : -1f };
            float scaleToFill = Math.Max((float)bounds.Width / (float)intel.ImageSize.Width, (float)bounds.Height / (float)intel.ImageSize.Height);
            var primaryFace = intel.Faces.OrderByDescending(f => f.Width * f.Height).First();

            float targetZoom = StrategyMath.CalculateFaceZoom(primaryFace, bounds, scaleToFill);
            bool zoomIn = rnd.Next(2) == 0;

            // FIXED: Math.Max guarantees camera won't zoom out past the screen bounds exposing black edges
            transform.StartScale = zoomIn ? Math.Max(scaleToFill, targetZoom * 0.75f) : targetZoom;
            transform.EndScale = zoomIn ? targetZoom : Math.Max(scaleToFill, targetZoom * 0.75f);

            if (settings.UseTilt)
            {
                float maxTilt = 1.5f * (float)(Math.PI / 180.0);
                transform.StartRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
                transform.EndRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
            }

            Rect full = new Rect(0, 0, bounds.Width, bounds.Height);
            Vector2 imageCenter = new Vector2((float)intel.ImageSize.Width / 2f, (float)intel.ImageSize.Height / 2f);
            bool hasManualTarget = settings.FocusTargetRect.HasValue;

            // FIXED: WYSIWYG overrides the Rule-of-Thirds if human placed a blue box
            transform.StartPan = CameraMath.GetTargetedPan(imageCenter, transform.StartScale, full, intel.ImageSize, rnd);
            transform.EndPan = hasManualTarget
                ? CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.EndScale, full, intel.ImageSize, rnd, true)
                : StrategyMath.GetRuleOfThirdsPan(primaryFace, transform.EndScale, bounds, intel.ImageSize);

            transform.RecommendedDurationSeconds = 6.0 + rnd.NextDouble() * 2.5;
            transform.CrossfadeDurationSeconds = 1.7;

            return transform;
        }
    }

    public class IntimateTrackingStrategy : ICameraStrategy
    {
        public bool CanExecute(SceneIntelligence intel, Size bounds) => intel.Faces.Any() && StrategyMath.GetCropRatio(intel, bounds) <= 1.35f;

        public CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd)
        {
            var transform = new CameraTransform { StrategyName = "Intimate Face Tracking", Mode = LayoutMode.Fullscreen, CurveSign = rnd.Next(2) == 0 ? 1f : -1f };
            float scaleToFill = Math.Max((float)bounds.Width / (float)intel.ImageSize.Width, (float)bounds.Height / (float)intel.ImageSize.Height);
            var primaryFace = intel.Faces.OrderByDescending(f => f.Width * f.Height).First();

            float baseTightZoom = StrategyMath.CalculateFaceZoom(primaryFace, bounds, scaleToFill);
            float intensityMultiplier = 1.0f + (float)(settings.IntensityPercent / 100.0);
            float maxTightZoom = baseTightZoom * (1.0f + (intensityMultiplier - 1.0f) * 0.5f);

            bool zoomIn;
            if (settings.DirectionMode == 1) zoomIn = true;
            else if (settings.DirectionMode == 2) zoomIn = false;
            else zoomIn = rnd.Next(2) == 0;

            bool isPanOnly = settings.DirectionMode == 3;

            transform.StartScale = isPanOnly ? maxTightZoom : (zoomIn ? baseTightZoom : maxTightZoom);
            transform.EndScale = isPanOnly ? maxTightZoom : (zoomIn ? maxTightZoom : baseTightZoom);

            if (settings.UseTilt)
            {
                float maxTilt = 0.5f * (float)(Math.PI / 180.0);
                transform.StartRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
                transform.EndRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
            }

            Rect full = new Rect(0, 0, bounds.Width, bounds.Height);
            bool hasManualTarget = settings.FocusTargetRect.HasValue;

            transform.StartPan = hasManualTarget
                ? CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.StartScale, full, intel.ImageSize, rnd, true)
                : StrategyMath.GetRuleOfThirdsPan(primaryFace, transform.StartScale, bounds, intel.ImageSize);

            transform.EndPan = hasManualTarget
                ? CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.EndScale, full, intel.ImageSize, rnd, true)
                : StrategyMath.GetRuleOfThirdsPan(primaryFace, transform.EndScale, bounds, intel.ImageSize);

            transform.RecommendedDurationSeconds = 7.0 + rnd.NextDouble() * 2.0;
            transform.CrossfadeDurationSeconds = 2.0;

            return transform;
        }
    }

    public class PortraitHeadshotStrategy : ICameraStrategy
    {
        public bool CanExecute(SceneIntelligence intel, Size bounds) => intel.Faces.Any();

        public CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd)
        {
            var transform = new CameraTransform { StrategyName = "Portrait Headshot Anchor", Mode = LayoutMode.Fullscreen, CurveSign = 1f };
            float scaleToFill = Math.Max((float)bounds.Width / (float)intel.ImageSize.Width, (float)bounds.Height / (float)intel.ImageSize.Height);
            var primaryFace = intel.Faces.OrderByDescending(f => f.Width * f.Height).First();

            float targetZoom = StrategyMath.CalculateMacroZoom(primaryFace, bounds, scaleToFill);
            float microZoomFactor = 1.02f;

            bool zoomIn = rnd.Next(2) == 0;
            transform.StartScale = zoomIn ? targetZoom : targetZoom * microZoomFactor;
            transform.EndScale = zoomIn ? targetZoom * microZoomFactor : targetZoom;

            if (settings.UseTilt)
            {
                float maxTilt = 0.15f * (float)(Math.PI / 180.0);
                transform.StartRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
                transform.EndRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
            }

            Rect full = new Rect(0, 0, bounds.Width, bounds.Height);
            bool hasManualTarget = settings.FocusTargetRect.HasValue;

            transform.StartPan = hasManualTarget
                ? CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.StartScale, full, intel.ImageSize, rnd, true)
                : StrategyMath.GetRuleOfThirdsPan(primaryFace, transform.StartScale, bounds, intel.ImageSize);

            transform.EndPan = hasManualTarget
                ? CameraMath.GetTargetedPan(intel.SaliencyTarget, transform.EndScale, full, intel.ImageSize, rnd, true)
                : StrategyMath.GetRuleOfThirdsPan(primaryFace, transform.EndScale, bounds, intel.ImageSize);

            transform.RecommendedDurationSeconds = 5.0 + rnd.NextDouble() * 2.5;
            transform.CrossfadeDurationSeconds = 1.8;

            return transform;
        }
    }

    public class DialoguePanStrategy : ICameraStrategy
    {
        public bool CanExecute(SceneIntelligence intel, Size bounds) => intel.Faces.Count >= 2 && StrategyMath.GetCropRatio(intel, bounds) <= 1.35f;

        public CameraTransform CalculateTrajectory(SceneIntelligence intel, Size bounds, SlideSettings settings, Random rnd)
        {
            var transform = new CameraTransform { StrategyName = "Multi-Subject Dialogue Pan", Mode = LayoutMode.Fullscreen, CurveSign = rnd.Next(2) == 0 ? 1f : -1f };
            float scaleToFill = Math.Max((float)bounds.Width / (float)intel.ImageSize.Width, (float)bounds.Height / (float)intel.ImageSize.Height);
            var topFaces = intel.Faces.OrderByDescending(f => f.Width * f.Height).Take(2).ToList();

            var faceA = topFaces[0];
            var faceB = topFaces[1];

            float zoomA = StrategyMath.CalculateFaceZoom(faceA, bounds, scaleToFill);
            float zoomB = StrategyMath.CalculateFaceZoom(faceB, bounds, scaleToFill);

            // FIXED: Math.Max guarantees camera won't zoom out exposing black edges
            float sharedScale = Math.Max(scaleToFill, Math.Min(zoomA, zoomB) * 0.85f);

            transform.StartScale = sharedScale;
            transform.EndScale = sharedScale;

            if (settings.UseTilt)
            {
                float maxTilt = 1.0f * (float)(Math.PI / 180.0);
                transform.StartRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
                transform.EndRotation = (float)(rnd.NextDouble() * 2 * maxTilt) - maxTilt;
            }

            Vector2 targetA = new Vector2((float)(faceA.X + faceA.Width / 2f), (float)(faceA.Y + faceA.Height * 0.2f));
            Vector2 targetB = new Vector2((float)(faceB.X + faceB.Width / 2f), (float)(faceB.Y + faceB.Height * 0.2f));

            bool panAtoB = rnd.Next(2) == 0;
            Vector2 startTarget = panAtoB ? targetA : targetB;
            Vector2 endTarget = panAtoB ? targetB : targetA;

            Rect full = new Rect(0, 0, bounds.Width, bounds.Height);
            transform.StartPan = CameraMath.GetTargetedPan(startTarget, transform.StartScale, full, intel.ImageSize, rnd);
            transform.EndPan = CameraMath.GetTargetedPan(endTarget, transform.EndScale, full, intel.ImageSize, rnd);

            transform.RecommendedDurationSeconds = 8.5 + rnd.NextDouble() * 3.0;
            transform.CrossfadeDurationSeconds = 2.4;

            return transform;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;

namespace ModernImageViewer.Cinematic
{
    public enum LayoutMode { Fullscreen, Portrait }

    public class SlideSettings
    {
        public double IntensityPercent { get; set; }
        public int DirectionMode { get; set; }
        public bool UseTilt { get; set; }
        public bool UseNarrativeArc { get; set; } = true;
        public string TechniqueOverride { get; set; } = "Auto";
        public Rect? FocusTargetRect { get; set; }
        public double DurationSeconds { get; set; } = 10.0;
        public int BeatCount { get; set; } = 1;
    }

    public class SceneIntelligence
    {
        public Size ImageSize { get; set; }
        public List<Rect> Faces { get; set; } = new();
        public Vector2 SaliencyTarget { get; set; }
        public float AspectRatio => ImageSize.Height == 0 ? 1f : (float)(ImageSize.Width / ImageSize.Height);
    }

    public class CameraTransform
    {
        public string StrategyName { get; set; } = "Unknown";
        public string Technique { get; set; } = "None";
        public LayoutMode Mode { get; set; }
        public float StartScale { get; set; }
        public float EndScale { get; set; }
        public Vector2 StartPan { get; set; }
        public Vector2 EndPan { get; set; }
        public float StartRotation { get; set; }
        public float EndRotation { get; set; }
        public float CurveSign { get; set; } = 1f;

        public double RecommendedDurationSeconds { get; set; } = 10.0;
        public double CrossfadeDurationSeconds { get; set; } = 2.5;
        public bool RequiresCut { get; set; } = false;
        public bool IsSnapZoom { get; set; } = false;
    }

    public static class CameraMath
    {
        public static Vector2 GetTargetedPan(Vector2 targetPoint, float scale, Rect bounds, Size imgSize, Random rnd, bool isHumanOverride = false)
        {
            // PURE FLOAT CONVERSION: Prevents CS0266 double-to-float compile errors
            float bw = (float)bounds.Width; float bh = (float)bounds.Height;
            float iw = (float)imgSize.Width; float ih = (float)imgSize.Height;

            float scaledW = iw * scale;
            float scaledH = ih * scale;

            float minX = bw - scaledW;
            float maxX = 0f;
            float minY = bh - scaledH;
            float maxY = 0f;

            if (minX > maxX) minX = maxX = minX / 2f;
            if (minY > maxY) minY = maxY = minY / 2f;

            float idealX = (bw / 2f) - (targetPoint.X * scale);
            float idealY = (bh / 2f) - (targetPoint.Y * scale);

            float driftX = isHumanOverride ? 0f : (float)((rnd.NextDouble() - 0.5) * bw * 0.1);
            float driftY = isHumanOverride ? 0f : (float)((rnd.NextDouble() - 0.5) * bh * 0.1);

            return new Vector2(Math.Clamp(idealX + driftX, minX, maxX), Math.Clamp(idealY + driftY, minY, maxY));
        }
    }
}
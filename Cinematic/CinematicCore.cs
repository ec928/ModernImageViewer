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
        public Rect? StartCrop { get; set; }
        public Rect? EndCrop { get; set; }
        public double DurationSeconds { get; set; } = 10.0;
        public int BeatCount { get; set; } = 1;
        public string CurveOverride { get; set; } = "EaseInOut";
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
        public string EaseKind { get; set; } = "EaseInOut";
    }

    public static class CameraMath
    {
        public static Vector2 GetTargetedPan(Vector2 targetPoint, float scale, Rect bounds, Size imgSize, Random rnd, bool isHumanOverride = false)
        {
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

        public static Rect CropFromCamera(float scale, Vector2 pan, Size canvas, Size img)
        {
            if (scale <= 0.0001f) scale = 1f;
            double iw = Math.Max(1, img.Width);
            double ih = Math.Max(1, img.Height);
            double left = (0 - pan.X) / scale;
            double top = (0 - pan.Y) / scale;
            double w = canvas.Width / scale;
            double h = canvas.Height / scale;
            return new Rect(left / iw, top / ih, w / iw, h / ih);
        }

        public static (float Scale, Vector2 Pan) CameraFromCrop(Rect norm, Size canvas, Size img)
        {
            float iw = Math.Max(1f, (float)img.Width);
            float ih = Math.Max(1f, (float)img.Height);
            float rw = Math.Max(0.02f, (float)norm.Width) * iw;
            float rh = Math.Max(0.02f, (float)norm.Height) * ih;
            float scale = Math.Max((float)canvas.Width / rw, (float)canvas.Height / rh);
            float cx = ((float)norm.X + (float)norm.Width / 2f) * iw;
            float cy = ((float)norm.Y + (float)norm.Height / 2f) * ih;
            return (scale, new Vector2((float)canvas.Width / 2f - cx * scale, (float)canvas.Height / 2f - cy * scale));
        }
    }
}
using System;
using System.Numerics;

namespace ModernImageViewer.Cinematic
{
    /// <summary>What makes Play read as directed: slide lengths that alternate short and long, and no move twice running.</summary>
    public sealed class ShowPacing
    {
        public const double ShortestSeconds = 5.0;
        public const double LongestSeconds = 20.0;

        private readonly object _gate = new();
        private bool _lastWasLong = true;
        private string? _lastMove;

        public double NextDuration(Random rnd)
        {
            double middle = (ShortestSeconds + LongestSeconds) / 2.0;
            lock (_gate)
            {
                _lastWasLong = !_lastWasLong;
                double low = _lastWasLong ? middle : ShortestSeconds;
                double high = _lastWasLong ? LongestSeconds : middle;
                return low + rnd.NextDouble() * (high - low);
            }
        }

        public bool IsRepeat(string move)
        {
            lock (_gate) return move == _lastMove;
        }

        public void Remember(string move)
        {
            lock (_gate) _lastMove = move;
        }
    }

    public static class CameraFraming
    {
        /// <summary>The move as the viewer sees it. A push-in is a push-in whichever strategy made it.</summary>
        public static string MoveName(float startScale, float endScale, Vector2 startPan, Vector2 endPan)
        {
            float ratio = endScale / Math.Max(0.0001f, startScale);
            if (ratio > 1.02f) return "in";
            if (ratio < 0.98f) return "out";

            Vector2 travel = endPan - startPan;
            if (travel.Length() < 8f) return "hold";
            // Panning the picture right moves the view left.
            if (Math.Abs(travel.X) >= Math.Abs(travel.Y)) return travel.X > 0 ? "left" : "right";
            return travel.Y > 0 ? "up" : "down";
        }

        /// <summary>
        /// Keeps the picture over the whole screen on this frame. The curve on a pan and the tilt can both push an edge into view,
        /// so the tilt is limited to what the zoom leaves room for, which changes smoothly as the zoom does, and the pan is then held inside.
        /// A picture too small to cover the screen is centred.
        /// </summary>
        public static (Vector2 Pan, float Rotation) KeepCovered(float scale, Vector2 pan, float rotation, float screenW, float screenH, float imageW, float imageH)
        {
            float drawnW = imageW * scale;
            float drawnH = imageH * scale;

            float roomForTilt = Math.Min((drawnW - screenW) / Math.Max(1f, screenH), (drawnH - screenH) / Math.Max(1f, screenW));
            float largestTilt = roomForTilt <= 0f ? 0f : MathF.Asin(Math.Min(1f, roomForTilt));
            rotation = Math.Clamp(rotation, -largestTilt, largestTilt);

            float sin = Math.Abs(MathF.Sin(rotation));
            float cos = MathF.Cos(rotation);
            float reachX = screenW / 2f * cos + screenH / 2f * sin;
            float reachY = screenW / 2f * sin + screenH / 2f * cos;

            float x = drawnW <= 2f * reachX ? (screenW - drawnW) / 2f : Math.Clamp(pan.X, screenW / 2f + reachX - drawnW, screenW / 2f - reachX);
            float y = drawnH <= 2f * reachY ? (screenH - drawnH) / 2f : Math.Clamp(pan.Y, screenH / 2f + reachY - drawnH, screenH / 2f - reachY);
            return (new Vector2(x, y), rotation);
        }
    }
}

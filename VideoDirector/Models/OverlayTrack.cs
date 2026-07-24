using System;
using System.Collections.ObjectModel;

namespace ModernImageViewer.VideoDirector.Models
{
    // One upper track (Track 2, 3, 4). Strict: its clips are sequential and never overlap, so at
    // most ONE clip is active at any story time — which is what lets each track own exactly one
    // player/render surface. Simultaneity is expressed by using another track, never by stacking
    // within one. The spine (Track 1) is NOT an OverlayTrack; it keeps its own A/B-roll path.
    public sealed class OverlayTrack : ObservableObject
    {
        public ObservableCollection<CinematicOperation> Clips { get; set; } = new();

        // Where new clips on this track default to sitting (opposing corners per track).
        private double _defaultCenterX = 0.72;
        public double DefaultCenterX
        {
            get => _defaultCenterX;
            set => SetProperty(ref _defaultCenterX, value);
        }

        private double _defaultCenterY = 0.72;
        public double DefaultCenterY
        {
            get => _defaultCenterY;
            set => SetProperty(ref _defaultCenterY, value);
        }

        private string _name = "Track";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        // Nearest start time (seconds) at which a clip of length `dur` fits WITHOUT overlapping the
        // others on this track. The track is strict — only one clip can be active at a time, so an
        // overlap would silently hide one at playback. Pass the clip being moved so it ignores
        // itself; pass null when placing a brand-new clip.
        public double ClampToFreeSlot(CinematicOperation moving, double start, double dur)
        {
            double lower = 0, upper = double.MaxValue;
            double centre = start + dur / 2;

            foreach (var other in Clips)
            {
                if (moving != null && ReferenceEquals(other, moving)) continue;
                double s = other.StartTimeSeconds;
                double e = s + other.OpDuration.TotalSeconds;

                if (e <= centre) lower = Math.Max(lower, e);          // neighbour to our left
                else if (s >= centre) upper = Math.Min(upper, s);     // neighbour to our right
                else if (centre < (s + e) / 2) upper = Math.Min(upper, s);
                else lower = Math.Max(lower, e);
            }

            if (start < lower) start = lower;
            if (upper != double.MaxValue && start + dur > upper) start = upper - dur;
            if (start < lower) start = lower;   // no room in this gap — park against the left edge
            return Math.Max(0, start);
        }
    }
}

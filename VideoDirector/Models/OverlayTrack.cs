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
    }
}

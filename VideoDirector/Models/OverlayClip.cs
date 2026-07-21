using System;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModernImageViewer.VideoDirector.Models
{
    /// <summary>
    /// Represents a video or image overlay that appears on top of the main 
    /// sequential timeline at a specific story time. Overlays are simpler than
    /// main track CinematicOperations — no transitions, no A/B roll, just 
    /// "show at StartTime, hide at StartTime + Duration" with a static transform.
    /// </summary>
    public class OverlayClip : ObservableObject
    {
        private string _filePath = string.Empty;
        public string FilePath
        {
            get => _filePath;
            set
            {
                if (SetProperty(ref _filePath, value))
                {
                    OnPropertyChanged(nameof(FileName));
                }
            }
        }

        [JsonIgnore]
        public string FileName => System.IO.Path.GetFileName(_filePath);

        private BitmapImage? _thumbnail;
        [JsonIgnore]
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        /// <summary>
        /// When this overlay appears, in story time (relative to the main timeline).
        /// </summary>
        private TimeSpan _startTime = TimeSpan.Zero;
        public TimeSpan StartTime
        {
            get => _startTime;
            set => SetProperty(ref _startTime, value);
        }

        /// <summary>
        /// How long this overlay is visible.
        /// </summary>
        private TimeSpan _duration = TimeSpan.FromSeconds(5);
        public TimeSpan Duration
        {
            get => _duration;
            set => SetProperty(ref _duration, value);
        }

        /// <summary>
        /// Where to start playback within the source media file (trim).
        /// </summary>
        private TimeSpan _videoStartTime = TimeSpan.Zero;
        public TimeSpan VideoStartTime
        {
            get => _videoStartTime;
            set => SetProperty(ref _videoStartTime, value);
        }

        /// <summary>
        /// Scale relative to viewport. 1.0 = full size, 0.3 = 30% PIP.
        /// </summary>
        private float _scale = 0.3f;
        public float Scale
        {
            get => _scale;
            set => SetProperty(ref _scale, value);
        }

        /// <summary>
        /// Horizontal offset in pixels from center.
        /// </summary>
        private float _x = 0f;
        public float X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        /// <summary>
        /// Vertical offset in pixels from center.
        /// </summary>
        private float _y = 0f;
        public float Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        /// <summary>
        /// Opacity when visible. 0.0 = transparent, 1.0 = fully opaque.
        /// </summary>
        private float _opacity = 1.0f;
        public float Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>
        /// Stacking order among overlays. Higher = in front.
        /// </summary>
        private int _zOrder = 0;
        public int ZOrder
        {
            get => _zOrder;
            set => SetProperty(ref _zOrder, value);
        }

        // Convenience properties for UI binding
        public double StartTimeSeconds
        {
            get => _startTime.TotalSeconds;
            set => StartTime = TimeSpan.FromSeconds(value);
        }

        public double DurationSeconds
        {
            get => _duration.TotalSeconds;
            set => Duration = TimeSpan.FromSeconds(value);
        }

        /// <summary>
        /// The story time at which this overlay disappears.
        /// </summary>
        [JsonIgnore]
        public TimeSpan EndTime => _startTime + _duration;

        /// <summary>
        /// Returns true if this overlay should be visible at the given story time.
        /// </summary>
        public bool IsActiveAt(TimeSpan storyTime)
        {
            return storyTime >= _startTime && storyTime < EndTime;
        }
    }
}

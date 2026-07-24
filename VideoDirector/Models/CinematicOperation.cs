using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModernImageViewer.VideoDirector.Models
{
    public class CinematicOperation : ObservableObject
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

        public string FileName => System.IO.Path.GetFileName(_filePath);

        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        // A clip behaves as a still when it's an image file OR its playback speed is 0 (frozen
        // frame). Everything else is a video whose end is measured from the decode position.
        // Do NOT gate video detection on a hardcoded video-extension whitelist: any container we
        // can't positively identify as an image is a video (.mkv, .avi, .mov, … all count), or a
        // trimmed clip in an unlisted format silently falls through to still handling and overruns.
        [JsonIgnore]
        public bool IsStill
        {
            get
            {
                if (_playbackSpeed <= 0) return true;
                if (string.IsNullOrWhiteSpace(_filePath)) return true;
                var ext = System.IO.Path.GetExtension(_filePath);
                return Array.Exists(ImageExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
            }
        }

        private BitmapImage? _thumbnail;
        [JsonIgnore]
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        [JsonIgnore]
        public bool HasModifications => 
            StartMark.Scale != 1.0f || StartMark.X != 0 || StartMark.Y != 0 ||
            EndMark.Scale != 1.0f || EndMark.X != 0 || EndMark.Y != 0 ||
            PlaybackSpeed != 1.0 ||
            TransitionDuration > TimeSpan.Zero ||
            TransitionStyle != TransitionStyle.HardSnap ||
            CurveProfile != CurveProfile.Linear ||
            VideoStartTime > TimeSpan.Zero ||
            (VideoEndTime > TimeSpan.Zero && VideoEndTime != OpDuration);

        private bool _isPlaying;
        [JsonIgnore]
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        private bool _isUpdatingTiming = false;

        private TimeSpan _videoStartTime = TimeSpan.Zero;
        public TimeSpan VideoStartTime
        {
            get => _videoStartTime;
            set 
            {
                if (SetProperty(ref _videoStartTime, value))
                {
                    SyncTimingFromVideo();
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private TimeSpan _videoEndTime = TimeSpan.Zero;
        public TimeSpan VideoEndTime
        {
            get => _videoEndTime;
            set 
            {
                if (SetProperty(ref _videoEndTime, value))
                {
                    SyncTimingFromVideo();
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        // Full length of the source file, captured when the clip is added. The trim In/Out points
        // (VideoStartTime/VideoEndTime) live within [0, SourceDuration]. Not the timeline duration
        // (that's OpDuration = trimmed length / speed).
        private TimeSpan _sourceDuration;
        public TimeSpan SourceDuration
        {
            get => _sourceDuration;
            set { if (SetProperty(ref _sourceDuration, value)) OnPropertyChanged(nameof(SourceDurationSeconds)); }
        }

        [JsonIgnore]
        public double SourceDurationSeconds => _sourceDuration.TotalSeconds;

        private TimeSpan _opDuration = TimeSpan.Zero;
        public TimeSpan OpDuration
        {
            get => _opDuration;
            set 
            {
                if (SetProperty(ref _opDuration, value))
                {
                    SyncTimingFromOpDuration();
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private double _playbackSpeed = 1.0;
        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set 
            {
                if (SetProperty(ref _playbackSpeed, value))
                {
                    SyncTimingFromPlaybackSpeed();
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private void SyncTimingFromVideo()
        {
            if (_isUpdatingTiming) return;
            _isUpdatingTiming = true;
            try
            {
                double videoDuration = (_videoEndTime - _videoStartTime).TotalSeconds;
                if (videoDuration > 0 && _playbackSpeed > 0)
                {
                    _opDuration = TimeSpan.FromSeconds(videoDuration / _playbackSpeed);
                    OnPropertyChanged(nameof(OpDuration));
                }
            }
            finally { _isUpdatingTiming = false; }
        }

        private void SyncTimingFromOpDuration()
        {
            if (_isUpdatingTiming) return;
            _isUpdatingTiming = true;
            try
            {
                if (_playbackSpeed == 0.0) return; // Treat as still image; let duration stand

                double videoDuration = (_videoEndTime - _videoStartTime).TotalSeconds;
                if (videoDuration > 0 && _opDuration.TotalSeconds > 0)
                {
                    _playbackSpeed = videoDuration / _opDuration.TotalSeconds;
                    OnPropertyChanged(nameof(PlaybackSpeed));
                }
            }
            finally { _isUpdatingTiming = false; }
        }

        private void SyncTimingFromPlaybackSpeed()
        {
            if (_isUpdatingTiming) return;
            _isUpdatingTiming = true;
            try
            {
                double videoDuration = (_videoEndTime - _videoStartTime).TotalSeconds;
                if (videoDuration > 0 && _playbackSpeed > 0)
                {
                    _opDuration = TimeSpan.FromSeconds(videoDuration / _playbackSpeed);
                    OnPropertyChanged(nameof(OpDuration));
                }
            }
            finally { _isUpdatingTiming = false; }
        }

        // --- Upper-track (Track 2/3) clip properties ---
        // Track 1 ignores these (its timeline position is computed sequentially). Upper
        // tracks use StartTime as the editable master-timeline placement and Opacity for
        // compositing. Upper-track clips are the same CinematicOperation type as Track 1.

        // Editable placement on the master timeline. Display-only/computed for Track 1;
        // the freely-editable start position for Track 2/3 (gaps allowed).
        private TimeSpan _startTime = TimeSpan.Zero;
        public TimeSpan StartTime
        {
            get => _startTime;
            set
            {
                if (SetProperty(ref _startTime, value))
                {
                    OnPropertyChanged(nameof(StartTimeSeconds));
                }
            }
        }

        [JsonIgnore]
        public double StartTimeSeconds
        {
            get => _startTime.TotalSeconds;
            set => StartTime = TimeSpan.FromSeconds(value);
        }

        // Compositing opacity when this clip sits on an upper track. 1 = opaque.
        private float _opacity = 1.0f;
        public float Opacity
        {
            get => _opacity;
            set => SetProperty(ref _opacity, Math.Clamp(value, 0f, 1f));
        }

        // End of this clip's window on the master timeline (upper tracks).
        [JsonIgnore]
        public TimeSpan EndTimeOnTimeline => _startTime + _opDuration;

        // True if this clip is visible at the given master-timeline position (upper tracks).
        public bool IsActiveAt(TimeSpan storyTime) => storyTime >= _startTime && storyTime < EndTimeOnTimeline;

        // --- Placement (upper-track PiP box) ---
        // Where and how big the clip appears in the composite, INDEPENDENT of its content
        // framing (marks). Track 1 ignores placement (it is always full-frame). This is what
        // lets a clip be framed full-screen while editing but shown as a corner PiP at playback.

        // The SOURCE video's natural aspect (width/height), captured when the clip is added. The
        // PiP box is shaped from this. Do NOT infer it from the thumbnail: ThumbnailMode.VideosView
        // returns a letterboxed 16:9 image even for portrait video, which makes every box landscape
        // and crops the subject. 0 = not yet known (backfilled once the media opens).
        private double _sourceAspect;
        public double SourceAspect
        {
            get => _sourceAspect;
            set => SetProperty(ref _sourceAspect, value);
        }

        // Box size as INDEPENDENT fractions of the video's viewport-fit size (0.3 = 30%).
        // Width and Height are decoupled so the PiP box can be reshaped to any aspect; the
        // video content crop-fills the box (UniformToFill + clip) so it never distorts.
        // Default 0.3 x 0.3 reproduces the old aspect-locked 30% corner PiP exactly.
        private double _placementWidth = 0.3;
        public double PlacementWidth
        {
            get => _placementWidth;
            set => SetProperty(ref _placementWidth, Math.Clamp(value, 0.05, 1.0));
        }

        private double _placementHeight = 0.3;
        public double PlacementHeight
        {
            get => _placementHeight;
            set => SetProperty(ref _placementHeight, Math.Clamp(value, 0.05, 1.0));
        }

        // Box centre as a fraction of the viewport (0.5,0.5 = centre). Default lower-right.
        private double _placementCenterX = 0.72;
        public double PlacementCenterX
        {
            get => _placementCenterX;
            set => SetProperty(ref _placementCenterX, Math.Clamp(value, 0.0, 1.0));
        }

        private double _placementCenterY = 0.72;
        public double PlacementCenterY
        {
            get => _placementCenterY;
            set => SetProperty(ref _placementCenterY, Math.Clamp(value, 0.0, 1.0));
        }

        private SpatialMark _startMark = new();
        public SpatialMark StartMark
        {
            get => _startMark;
            set
            {
                if (_startMark != null) _startMark.PropertyChanged -= Mark_PropertyChanged;
                if (SetProperty(ref _startMark, value) && _startMark != null)
                {
                    _startMark.PropertyChanged += Mark_PropertyChanged;
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private SpatialMark? _midMark;
        public SpatialMark? MidMark
        {
            get => _midMark;
            set
            {
                if (_midMark != null) _midMark.PropertyChanged -= Mark_PropertyChanged;
                if (SetProperty(ref _midMark, value) && _midMark != null)
                {
                    _midMark.PropertyChanged += Mark_PropertyChanged;
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private SpatialMark _endMark = new();
        public SpatialMark EndMark
        {
            get => _endMark;
            set
            {
                if (_endMark != null) _endMark.PropertyChanged -= Mark_PropertyChanged;
                if (SetProperty(ref _endMark, value) && _endMark != null)
                {
                    _endMark.PropertyChanged += Mark_PropertyChanged;
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        public CinematicOperation()
        {
            _startMark.PropertyChanged += Mark_PropertyChanged;
            _endMark.PropertyChanged += Mark_PropertyChanged;
        }

        public void Reset()
        {
            StartMark.Scale = 1.0f;
            StartMark.X = 0;
            StartMark.Y = 0;
            
            if (MidMark != null)
            {
                MidMark.Scale = 1.0f;
                MidMark.X = 0;
                MidMark.Y = 0;
            }
            MidMark = null;

            EndMark.Scale = 1.0f;
            EndMark.X = 0;
            EndMark.Y = 0;

            PlaybackSpeed = 1.0;
            TransitionDuration = TimeSpan.Zero;
            TransitionStyle = TransitionStyle.HardSnap;
            CurveProfile = CurveProfile.Linear;
            VideoStartTime = TimeSpan.Zero;
            
            // Revert duration to match the full clip duration
            if (_videoEndTime > TimeSpan.Zero)
            {
                OpDuration = _videoEndTime;
            }
            
            RecordedPath.Clear();
            OnPropertyChanged(nameof(HasModifications));
        }

        private void Mark_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasModifications));
        }

        private List<TransformKeyframe> _recordedPath = new();
        public List<TransformKeyframe> RecordedPath
        {
            get => _recordedPath;
            set => SetProperty(ref _recordedPath, value);
        }

        private CurveProfile _curveProfile = CurveProfile.Linear;
        public CurveProfile CurveProfile
        {
            get => _curveProfile;
            set
            {
                if (SetProperty(ref _curveProfile, value))
                {
                    OnPropertyChanged(nameof(CurveProfileIndex));
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public int CurveProfileIndex
        {
            get => (int)_curveProfile;
            set
            {
                if (Enum.IsDefined(typeof(CurveProfile), value))
                {
                    CurveProfile = (CurveProfile)value;
                }
            }
        }

        private TimeSpan _transitionDuration = TimeSpan.Zero;
        public TimeSpan TransitionDuration
        {
            get => _transitionDuration;
            set 
            {
                if (SetProperty(ref _transitionDuration, value))
                {
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        private TransitionStyle _transitionStyle = TransitionStyle.HardSnap;
        public TransitionStyle TransitionStyle
        {
            get => _transitionStyle;
            set
            {
                if (SetProperty(ref _transitionStyle, value))
                {
                    OnPropertyChanged(nameof(TransitionStyleIndex));
                    OnPropertyChanged(nameof(TransitionIconGlyph));
                    OnPropertyChanged(nameof(TransitionIconTooltip));
                    OnPropertyChanged(nameof(HasModifications));
                }
            }
        }

        public int TransitionStyleIndex
        {
            get => (int)_transitionStyle;
            set
            {
                if (Enum.IsDefined(typeof(TransitionStyle), value))
                {
                    TransitionStyle = (TransitionStyle)value;
                }
            }
        }

        public string TransitionIconGlyph
        {
            get
            {
                return _transitionStyle switch
                {
                    TransitionStyle.Crossfade => "\uE88E", // Half-filled circle/star
                    TransitionStyle.CinematicBridge => "\uE811", // Bridge/Merge
                    TransitionStyle.DipToColor => "\uE790", // Color bucket
                    _ => "\uE8C6" // Cut/Scissors
                };
            }
        }

        public string TransitionIconTooltip => $"Transition Out: {_transitionStyle}";
    }
}

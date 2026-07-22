using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ModernImageViewer.VideoDirector.Models;
using Windows.Storage;

namespace ModernImageViewer.VideoDirector.ViewModels
{
    public enum EditTarget
    {
        Start,
        Mid,
        End
    }

    public class DirectorViewModel : ObservableObject
    {
        public ObservableCollection<CinematicOperation> TimelineNodes { get; } = new();

        // Track 2 (upper track). Same clip type as Track 1 — a clip is a clip. Upper-track
        // clips are freely placed on the timeline (editable StartTime, gaps allowed) and
        // composited over Track 1.
        public ObservableCollection<CinematicOperation> OverlayClips { get; } = new();

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    OnPropertyChanged(nameof(IsDockVisible));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        // The bottom track dock is shown when the storyboard is toggled on AND we're not
        // playing — it auto-hides during playback so the video gets the full canvas.
        public bool IsDockVisible => _isStoryboardVisible && !_isPlaying;

        private bool _isLooping = true;
        public bool IsLooping
        {
            get => _isLooping;
            set => SetProperty(ref _isLooping, value);
        }

        private bool _isAutoPlayEnabled = true;
        public bool IsAutoPlayEnabled
        {
            get => _isAutoPlayEnabled;
            set => SetProperty(ref _isAutoPlayEnabled, value);
        }

        private bool _isTelemetryVisible = true;
        public bool IsTelemetryVisible
        {
            get => _isTelemetryVisible;
            private set => SetProperty(ref _isTelemetryVisible, value);
        }

        private bool _isRecordingMotion;
        public bool IsRecordingMotion
        {
            get => _isRecordingMotion;
            set => SetProperty(ref _isRecordingMotion, value);
        }

        private bool _isStoryboardVisible = true;
        public bool IsStoryboardVisible
        {
            get => _isStoryboardVisible;
            set
            {
                if (SetProperty(ref _isStoryboardVisible, value))
                {
                    UpdateTelemetryVisibility();
                    OnPropertyChanged(nameof(IsDockVisible));
                }
            }
        }

        private bool _isControlsVisible = true;
        public bool IsControlsVisible
        {
            get => _isControlsVisible;
            set => SetProperty(ref _isControlsVisible, value);
        }

        private void UpdateTelemetryVisibility()
        {
            IsTelemetryVisible = IsStoryboardVisible;
        }

        private double _playbackSpeed = 1.0;
        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                if (SetProperty(ref _playbackSpeed, value))
                {
                    OnPropertyChanged(nameof(IsPausedSpeed));
                    PlaybackSpeedChanged?.Invoke(this, value);
                }
            }
        }

        public event EventHandler<double> PlaybackSpeedChanged;

        public bool IsPausedSpeed => _playbackSpeed == 0.0;

        public List<double> AvailableSpeeds { get; } = new List<double> { 1.0, 0.5, 0.25, 0.0 };

        public TimeSpan TotalStoryTime
        {
            get
            {
                TimeSpan total = TimeSpan.Zero;
                foreach (var node in TimelineNodes)
                {
                    total += node.OpDuration;
                    total += node.TransitionDuration;
                }
                return total;
            }
        }

        private TimeSpan _currentStoryTime;
        public TimeSpan CurrentStoryTime
        {
            get => _currentStoryTime;
            set => SetProperty(ref _currentStoryTime, value);
        }

        private CinematicOperation _selectedTimelineNode;
        public CinematicOperation SelectedTimelineNode
        {
            get => _selectedTimelineNode;
            set
            {
                if (SetProperty(ref _selectedTimelineNode, value))
                {
                    if (value != null) SelectedOverlay = null;
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        private CinematicOperation _selectedOverlay;
        public CinematicOperation SelectedOverlay
        {
            get => _selectedOverlay;
            set
            {
                if (SetProperty(ref _selectedOverlay, value))
                {
                    if (value != null) SelectedTimelineNode = null;
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        // True when either a Track 1 clip or an overlay is selected — the right panel shows
        // the relevant inspector, otherwise a "nothing selected" hint.
        public bool HasSelection => _selectedTimelineNode != null || _selectedOverlay != null;

        // Human-readable current mode, shown in a badge. The mouse wheel means different things
        // in edit vs composite, so the mode must always be visible. (Interactive canvas view is
        // still to come; for now this reflects the existing play/edit/idle states.)
        public string ModeLabel
        {
            get
            {
                if (_isEditMode)
                {
                    var name = _selectedOverlay?.FileName ?? _selectedTimelineNode?.FileName ?? "";
                    return "EDIT · " + name;
                }
                return _isPlaying ? "ARRANGE · playing" : "ARRANGE";
            }
        }

        // Edit vs Arrange mode — set by the engine; drives the badge.
        private bool _isEditMode;
        public bool IsEditMode
        {
            get => _isEditMode;
            set
            {
                if (SetProperty(ref _isEditMode, value))
                {
                    OnPropertyChanged(nameof(ModeLabel));
                }
            }
        }

        private TimeSpan _currentOperationTime;
        public TimeSpan CurrentOperationTime
        {
            get => _currentOperationTime;
            set
            {
                if (SetProperty(ref _currentOperationTime, value))
                {
                    OnPropertyChanged(nameof(CurrentOperationTimeSeconds));
                }
            }
        }

        public double CurrentOperationTimeSeconds
        {
            get => _currentOperationTime.TotalSeconds;
            set
            {
                if (Math.Abs(_currentOperationTime.TotalSeconds - value) > 0.1)
                {
                    CurrentOperationTime = TimeSpan.FromSeconds(value);
                    OperationSeekRequested?.Invoke(this, CurrentOperationTime);
                }
            }
        }

        public event EventHandler<TimeSpan> OperationSeekRequested;

        private TimeSpan _currentOperationDuration = TimeSpan.FromSeconds(10);
        public TimeSpan CurrentOperationDuration
        {
            get => _currentOperationDuration;
            set
            {
                if (SetProperty(ref _currentOperationDuration, value))
                {
                    OnPropertyChanged(nameof(CurrentOperationDurationSeconds));
                }
            }
        }

        public double CurrentOperationDurationSeconds => _currentOperationDuration.TotalSeconds;

        private EditTarget _currentEditTarget = EditTarget.Start;
        public EditTarget CurrentEditTarget
        {
            get => _currentEditTarget;
            set
            {
                if (SetProperty(ref _currentEditTarget, value))
                {
                    OnPropertyChanged(nameof(CurrentEditTargetIndex));
                    // When edit target changes, jump to it if we have an active operation
                    if (SelectedTimelineNode != null)
                    {
                        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        dispatcher.TryEnqueue(() => 
                        {
                            var evt = EditTargetChanged;
                            evt?.Invoke(this, SelectedTimelineNode);
                        });
                    }
                }
            }
        }
        
        public event EventHandler<CinematicOperation> EditTargetChanged;

        public int CurrentEditTargetIndex
        {
            get => (int)_currentEditTarget;
            set => CurrentEditTarget = (EditTarget)value;
        }

        public DirectorViewModel()
        {
            TimelineNodes.CollectionChanged += TimelineNodes_CollectionChanged;
        }

        private void TimelineNodes_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (CinematicOperation item in e.OldItems)
                {
                    item.PropertyChanged -= CinematicOperation_PropertyChanged;
                }
            }
            if (e.NewItems != null)
            {
                foreach (CinematicOperation item in e.NewItems)
                {
                    item.PropertyChanged += CinematicOperation_PropertyChanged;
                }
            }
            OnPropertyChanged(nameof(TotalStoryTime));
        }

        private void CinematicOperation_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CinematicOperation.OpDuration) || e.PropertyName == nameof(CinematicOperation.TransitionDuration))
            {
                OnPropertyChanged(nameof(TotalStoryTime));
            }
        }

        public async Task AddFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
            {
                TimeSpan duration = TimeSpan.FromSeconds(10);
                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumbnail = null;
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var props = await file.Properties.GetVideoPropertiesAsync();
                    if (props != null && props.Duration.TotalSeconds > 0)
                    {
                        duration = props.Duration;
                    }
                    else
                    {
                        var imgProps = await file.Properties.GetImagePropertiesAsync();
                        if (imgProps != null && imgProps.Width > 0)
                        {
                            duration = TimeSpan.FromSeconds(5); // Default 5s for images
                        }
                    }

                    // Get Thumbnail
                    var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 120, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                    if (thumb != null)
                    {
                        thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await thumbnail.SetSourceAsync(thumb);
                    }
                }
                catch { }

                // Update previous clip's transition if it doesn't have one
                if (TimelineNodes.Count > 0)
                {
                    var lastNode = TimelineNodes[^1];
                    if (lastNode.TransitionDuration == TimeSpan.Zero)
                    {
                        lastNode.TransitionDuration = TimeSpan.FromSeconds(1);
                        if (lastNode.TransitionStyle == TransitionStyle.HardSnap)
                        {
                            lastNode.TransitionStyle = TransitionStyle.Crossfade;
                        }
                    }
                }

                // Insert the new operation
                TimelineNodes.Add(new CinematicOperation
                {
                    FilePath = path,
                    OpDuration = duration,
                    VideoEndTime = duration,
                    TransitionDuration = TimeSpan.Zero, // Default 0s transition for the new last clip
                    Thumbnail = thumbnail
                });
            }
        }

        // Finds which Track 1 clip a given absolute story time falls within. Used to resume
        // playback from the correct clip when there's no selected timeline node to go by
        // (e.g. an overlay is selected instead) rather than silently restarting from clip 0.
        public int GetTimelineIndexForStoryTime(TimeSpan storyTime)
        {
            TimeSpan accumulated = TimeSpan.Zero;
            for (int i = 0; i < TimelineNodes.Count; i++)
            {
                var nodeSpan = TimelineNodes[i].OpDuration + TimelineNodes[i].TransitionDuration;
                if (storyTime < accumulated + nodeSpan || i == TimelineNodes.Count - 1)
                {
                    return i;
                }
                accumulated += nodeSpan;
            }
            return 0;
        }

        public async Task AddOverlayAsync(string filePath, TimeSpan startTime)
        {
            TimeSpan duration = TimeSpan.FromSeconds(5);
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumbnail = null;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var props = await file.Properties.GetVideoPropertiesAsync();
                if (props != null && props.Duration.TotalSeconds > 0)
                {
                    duration = props.Duration;
                }

                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 120, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null)
                {
                    thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await thumbnail.SetSourceAsync(thumb);
                }
            }
            catch { }

            // An upper-track clip is a normal CinematicOperation placed at the current playhead.
            // Content framing defaults to full-frame (marks at scale 1); the clip appears as a
            // 30% corner PiP via its placement (PlacementScale/Center defaults on the clip).
            var overlay = new CinematicOperation
            {
                FilePath = filePath,
                OpDuration = duration,
                VideoEndTime = duration,
                StartTime = startTime,
                Thumbnail = thumbnail
            };
            OverlayClips.Add(overlay);
            SelectedOverlay = overlay;
        }

        // Serialization wrapper so the JSON file can hold both timeline nodes and overlay clips
        private class ProjectData
        {
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> TimelineNodes { get; set; } = new();
            public System.Collections.ObjectModel.ObservableCollection<CinematicOperation> OverlayClips { get; set; } = new();
        }

        public async Task SaveAsync(Windows.Storage.StorageFile file)
        {
            var data = new ProjectData
            {
                TimelineNodes = TimelineNodes,
                OverlayClips = OverlayClips
            };
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0); // Clear existing content
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, data, options);
        }

        public async Task LoadAsync(Windows.Storage.StorageFile file)
        {
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            using var stream = await file.OpenStreamForReadAsync();
            
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // Read the raw JSON to determine format (old array vs new wrapper)
            using var reader = new System.IO.StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var trimmed = json.TrimStart();

            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> nodes = null;
            System.Collections.ObjectModel.ObservableCollection<CinematicOperation> overlays = null;

            if (trimmed.StartsWith("["))
            {
                // Legacy format: bare array of CinematicOperations
                nodes = System.Text.Json.JsonSerializer.Deserialize<System.Collections.ObjectModel.ObservableCollection<CinematicOperation>>(json, options);
            }
            else
            {
                // New format: ProjectData wrapper
                var data = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(json, options);
                if (data != null)
                {
                    nodes = data.TimelineNodes;
                    overlays = data.OverlayClips;
                }
            }

            if (nodes != null)
            {
                TimelineNodes.Clear();
                foreach (var node in nodes)
                {
                    TimelineNodes.Add(node);
                    _ = LoadThumbnailAsync(node, dispatcher);
                }
            }

            OverlayClips.Clear();
            if (overlays != null)
            {
                foreach (var overlay in overlays)
                {
                    OverlayClips.Add(overlay);
                    _ = LoadOverlayThumbnailAsync(overlay, dispatcher);
                }
            }
        }

        private async Task LoadThumbnailAsync(CinematicOperation node, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(node.FilePath)) return;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(node.FilePath);
                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 120, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null && dispatcher != null)
                {
                    // Ensure UI thread update
                    dispatcher.TryEnqueue(async () =>
                    {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(thumb);
                        node.Thumbnail = bitmap;
                    });
                }
            }
            catch { }
        }

        private async Task LoadOverlayThumbnailAsync(CinematicOperation overlay, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(overlay.FilePath)) return;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(overlay.FilePath);
                var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 120, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                if (thumb != null && dispatcher != null)
                {
                    dispatcher.TryEnqueue(async () =>
                    {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(thumb);
                        overlay.Thumbnail = bitmap;
                    });
                }
            }
            catch { }
        }

        public void Clear()
        {
            TimelineNodes.Clear();
            OverlayClips.Clear();
        }
    }
}

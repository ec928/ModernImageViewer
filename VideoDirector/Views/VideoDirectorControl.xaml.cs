using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernImageViewer.VideoDirector.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using ModernImageViewer.VideoDirector.Models;
using Microsoft.UI.Xaml.Input;

namespace ModernImageViewer.VideoDirector.Views
{
    public sealed partial class VideoDirectorControl : UserControl
    {
        public DirectorViewModel ViewModel { get; } = new DirectorViewModel();
        private VideoPlaybackEngine _playbackEngine;
        private DispatcherTimer _inactivityTimer;
        private double _preRecordSpeed = 1.0;

        // Proportional timeline bar (§7E/F): px-per-second scale + the playhead rectangle.
        private double _timelinePxPerSec;
        private Microsoft.UI.Xaml.Shapes.Rectangle _playhead;

        public VideoDirectorControl()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;

            _inactivityTimer = new DispatcherTimer();
            _inactivityTimer.Interval = TimeSpan.FromSeconds(5);
            _inactivityTimer.Tick += InactivityTimer_Tick;
            
            this.PointerMoved += VideoDirectorControl_PointerMoved;

            // Wire up the engine once the control loads
            this.Loaded += VideoDirectorControl_Loaded;
        }

        private void VideoDirectorControl_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _inactivityTimer.Stop();
            ViewModel.IsControlsVisible = true;
            _inactivityTimer.Start();
        }

        private void InactivityTimer_Tick(object sender, object e)
        {
            _inactivityTimer.Stop();
            if (!ViewModel.IsRecordingMotion)
            {
                ViewModel.IsControlsVisible = false;
            }
        }

        private void VideoDirectorControl_Loaded(object sender, RoutedEventArgs e)
        {
            _playbackEngine = new VideoPlaybackEngine(PlayerControl, ViewModel);
            PlayerControl.ViewportTransformChanged += PlayerControl_ViewportTransformChanged;
            PlayerControl.SizeChanged += PlayerControl_SizeChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.EditTargetChanged += ViewModel_EditTargetChanged;

            ViewModel.TimelineNodes.CollectionChanged += (s, ev) => BuildTimelineBar();
            ViewModel.OverlayClips.CollectionChanged += (s, ev) => BuildTimelineBar();
            BuildTimelineBar();
        }

        private void TimelineBar_SizeChanged(object sender, SizeChangedEventArgs e) => BuildTimelineBar();

        // Draws the proportional timeline on one shared px=seconds scale: spine clips (top row) laid
        // end-to-end with a thin transition sliver, overlays (lower row) positioned by start-time.
        // Reads the story-time authority on the VM so it agrees with playback (§7C/§7E).
        private void BuildTimelineBar()
        {
            if (TimelineBar == null) return;
            TimelineBar.Children.Clear();
            _playhead = null;

            double w = TimelineBar.ActualWidth;
            double total = ViewModel.TotalStoryDuration.TotalSeconds;
            if (w <= 0 || total <= 0) { _timelinePxPerSec = 0; return; }
            _timelinePxPerSec = w / total;

            const double spineY = 3, spineH = 15, ovY = 21, ovH = 15;

            for (int i = 0; i < ViewModel.TimelineNodes.Count; i++)
            {
                var clip = ViewModel.TimelineNodes[i];
                double x = ViewModel.GetSpineClipStart(i).TotalSeconds * _timelinePxPerSec;
                double cw = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                AddTimelineBlock(x, spineY, cw, spineH, Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6)); // spine = blue
                double tw = clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                if (tw > 0.5)
                    AddTimelineBlock(x + cw, spineY, tw, spineH, Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B)); // transition
            }

            foreach (var ov in ViewModel.OverlayClips)
            {
                double x = ov.StartTimeSeconds * _timelinePxPerSec;
                double ow = ov.OpDuration.TotalSeconds * _timelinePxPerSec;
                AddTimelineBlock(x, ovY, ow, ovH, Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)); // overlay = amber
            }

            _playhead = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 2,
                Height = TimelineBar.ActualHeight,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            };
            TimelineBar.Children.Add(_playhead);
            UpdatePlayhead();
        }

        private void AddTimelineBlock(double x, double y, double width, double height, Windows.UI.Color color)
        {
            if (width < 1) width = 1;
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            TimelineBar.Children.Add(r);
        }

        private void UpdatePlayhead()
        {
            if (_playhead == null || _timelinePxPerSec <= 0) return;
            Canvas.SetLeft(_playhead, ViewModel.CurrentStoryTime.TotalSeconds * _timelinePxPerSec);
        }

        private void PlayerControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // The canvas resizes when the bottom dock is toggled — keep WYSIWYG/overlay aligned.
            _playbackEngine?.OnViewportResized();
        }


        private void ViewModel_EditTargetChanged(object sender, CinematicOperation op)
        {
            if (!ViewModel.IsPlaying)
            {
                _playbackEngine?.EnterEditMode(op, ViewModel.CurrentEditTarget);
            }
        }

        private void PlayerControl_ViewportTransformChanged(object sender, EventArgs e)
        {
            if (ViewModel.IsPlaying || ViewModel.SelectedTimelineNode == null) return;
            var op = ViewModel.SelectedTimelineNode as CinematicOperation;
            var transform = PlayerControl.ActiveTransform;
            if (op == null || transform == null) return;
            
            // Only update the WYSIWYG overlay visual positions based on current viewport
            _playbackEngine?.UpdateWysiwygOverlay();
        }

        // Keyframe capture is identical for every track: it grabs the current content framing
        // (the edit-mode transform) onto the selected clip. One handler, whichever track is live.
        private void SetStart_Click(object sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.StartMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        private void SetMid_Click(object sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.MidMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        private void SetEnd_Click(object sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.EndMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        // Right-click the Mid button to clear it (back to a two-point Start -> End motion).
        private void ClearMid_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var op = ViewModel.SelectedClip;
            if (op != null)
            {
                op.MidMark = null;
                _playbackEngine?.UpdateWysiwygOverlay();
            }
            e.Handled = true;
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectorViewModel.CurrentStoryTime))
            {
                UpdatePlayhead();
                return;
            }
            if (e.PropertyName == nameof(DirectorViewModel.IsPlaying))
            {
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Symbol = ViewModel.IsPlaying ? Symbol.Pause : Symbol.Play;
                }
                _playbackEngine?.UpdateWysiwygOverlay();
            }
            else if (e.PropertyName == nameof(DirectorViewModel.SelectedOverlay))
            {
                if (ViewModel.SelectedOverlay is CinematicOperation overlay)
                {
                    // Selecting a Track 2 clip in the dock = Edit it (full-screen content), same
                    // as selecting a Track 1 clip. Returning to Arrange is the Exit button.
                    if (!ViewModel.IsPlaying)
                    {
                        _playbackEngine?.EnterOverlayEditMode(overlay);
                    }
                }
                // Deselection does not change mode — Exit returns to Arrange.
            }
            else if (e.PropertyName == nameof(DirectorViewModel.IsRecordingMotion))
            {
                if (RecordButton.IsChecked != ViewModel.IsRecordingMotion)
                {
                    RecordButton.IsChecked = ViewModel.IsRecordingMotion;
                }

                if (RecordIcon != null)
                {
                    RecordIcon.Symbol = ViewModel.IsRecordingMotion ? Symbol.Stop : Symbol.Video;
                }

                if (ViewModel.IsRecordingMotion)
                {
                    var op = ViewModel.SelectedTimelineNode as CinematicOperation ?? _playbackEngine?.CurrentPlayingOperation;
                    if (op != null)
                    {
                        _preRecordSpeed = ViewModel.PlaybackSpeed;
                        ViewModel.PlaybackSpeed = 0.5;
                        _playbackEngine?.StartRecordingMotion(op);
                    }
                    else
                    {
                        ViewModel.IsRecordingMotion = false; // Cannot record without a selected or playing node
                    }
                }
                else
                {
                    var op = ViewModel.SelectedTimelineNode as CinematicOperation ?? _playbackEngine?.CurrentPlayingOperation;
                    if (op != null)
                    {
                        _playbackEngine?.StopRecordingMotion(op);
                    }
                    ViewModel.PlaybackSpeed = _preRecordSpeed;
                }
            }
        }


        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecordButton.IsChecked.HasValue)
            {
                ViewModel.IsRecordingMotion = RecordButton.IsChecked.Value;
            }
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
            }
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new System.Collections.Generic.List<string>();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && (file.FileType == ".mp4" || file.FileType == ".mkv" || file.FileType == ".avi" || file.FileType == ".jpg" || file.FileType == ".png"))
                    {
                        paths.Add(item.Path);
                    }
                }

                if (paths.Count > 0)
                {
                    await ViewModel.AddFilesAsync(paths);
                }
            }
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_playbackEngine == null) return;
            // Strict segregation: in Edit mode, Play previews ONLY the edited clip's motion;
            // in Arrange mode, Play plays the whole composite.
            if (_playbackEngine.IsEditMode)
            {
                _playbackEngine.ToggleEditPreview();
            }
            else
            {
                await _playbackEngine.TogglePlayPauseAsync();
            }
        }

        private bool _wasPlayingBeforeDrag = false;

        private void TimelineRangeSlider_InteractionStarted(object sender, EventArgs e)
        {
            _wasPlayingBeforeDrag = ViewModel.IsPlaying;
            if (_wasPlayingBeforeDrag && _playbackEngine != null)
            {
                _ = _playbackEngine.TogglePlayPauseAsync(); // Pauses playback while dragging
            }
        }

        private async void TimelineRangeSlider_InteractionCompleted(object sender, EventArgs e)
        {
            if (_wasPlayingBeforeDrag && !ViewModel.IsPlaying && _playbackEngine != null)
            {
                await Task.Delay(100); // Give the player a tiny moment to settle the final scrub
                _ = _playbackEngine.TogglePlayPauseAsync(); // Resumes playback
            }
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            _playbackEngine?.SkipPrevious();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            _playbackEngine?.SkipNext();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Director Sequence", new List<string>() { ".json" });
            savePicker.SuggestedFileName = "NewSequence";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                await ViewModel.SaveAsync(file);
            }
        }

        private async void Load_Click(object sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);

            openPicker.ViewMode = PickerViewMode.List;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".json");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.LoadAsync(file);
                if (ViewModel.IsAutoPlayEnabled && ViewModel.TimelineNodes.Count > 0)
                {
                    _ = _playbackEngine?.StartPlaybackAsync(0);
                }
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Clear();
        }

        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsPlaying)
            {
                _playbackEngine?.StopPlayback();
            }
            else
            {
                int startIdx = 0;
                if (ViewModel.SelectedTimelineNode != null)
                {
                    startIdx = ViewModel.TimelineNodes.IndexOf(ViewModel.SelectedTimelineNode as CinematicOperation);
                    if (startIdx < 0) startIdx = 0;
                }
                await _playbackEngine?.StartPlaybackAsync(startIdx);
            }
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel.SelectedTimelineNode is CinematicOperation op)
            {
                if (ViewModel.IsPlaying)
                {
                    if (_playbackEngine?.CurrentPlayingOperation == op) return;

                    int index = ViewModel.TimelineNodes.IndexOf(op);
                    if (index >= 0)
                    {
                        _ = _playbackEngine?.StartPlaybackAsync(index);
                    }
                }
                else
                {
                    _playbackEngine?.EnterEditMode(op, ViewModel.CurrentEditTarget);
                }
            }
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            var node = ViewModel.SelectedTimelineNode;
            if (node != null)
            {
                int index = ViewModel.TimelineNodes.IndexOf(node);
                if (index >= 0)
                {
                    if (node is CinematicOperation op)
                    {
                        var newOp = new CinematicOperation
                        {
                            FilePath = op.FilePath,
                            VideoStartTime = op.VideoStartTime,
                            VideoEndTime = op.VideoEndTime,
                            OpDuration = op.OpDuration,
                            CurveProfile = op.CurveProfile,
                            StartMark = new SpatialMark(op.StartMark.Scale, op.StartMark.X, op.StartMark.Y),
                            EndMark = new SpatialMark(op.EndMark.Scale, op.EndMark.X, op.EndMark.Y),
                            TransitionDuration = op.TransitionDuration,
                            TransitionStyle = op.TransitionStyle
                        };
                        ViewModel.TimelineNodes.Insert(index + 1, newOp);
                    }
                }
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var node = ViewModel.SelectedTimelineNode;
            if (node != null)
            {
                ViewModel.TimelineNodes.Remove(node);
            }
        }

        private void ResetClip_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedTimelineNode != null)
            {
                ViewModel.SelectedTimelineNode.Reset();
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        private void ListView_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement element && element.DataContext is CinematicOperation node)
            {
                ViewModel.SelectedTimelineNode = node;
            }
        }

        private void ExitToArrange_Click(object sender, RoutedEventArgs e)
        {
            // Clear the selection so we don't immediately re-enter Edit, then return to Arrange.
            ViewModel.SelectedTimelineNode = null;
            ViewModel.SelectedOverlay = null;
            _playbackEngine?.ExitToArrange();
        }

        private async void AddOverlay_Click(object sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);

            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            openPicker.FileTypeFilter.Add(".mp4");
            openPicker.FileTypeFilter.Add(".mkv");
            openPicker.FileTypeFilter.Add(".avi");
            openPicker.FileTypeFilter.Add(".wmv");
            openPicker.FileTypeFilter.Add(".jpg");
            openPicker.FileTypeFilter.Add(".png");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.AddOverlayAsync(file.Path, ViewModel.CurrentStoryTime);
            }
        }

        private void DuplicateOverlay_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            if (overlay != null)
            {
                int index = ViewModel.OverlayClips.IndexOf(overlay);
                if (index >= 0)
                {
                    var newOverlay = new CinematicOperation
                    {
                        FilePath = overlay.FilePath,
                        OpDuration = overlay.OpDuration,
                        VideoStartTime = overlay.VideoStartTime,
                        VideoEndTime = overlay.VideoEndTime,
                        StartTime = overlay.StartTime + overlay.OpDuration, // Place right after the original
                        StartMark = new SpatialMark(overlay.StartMark.Scale, overlay.StartMark.X, overlay.StartMark.Y),
                        EndMark = new SpatialMark(overlay.EndMark.Scale, overlay.EndMark.X, overlay.EndMark.Y),
                        Opacity = overlay.Opacity,
                        PlacementWidth = overlay.PlacementWidth,
                        PlacementHeight = overlay.PlacementHeight,
                        PlacementCenterX = overlay.PlacementCenterX,
                        PlacementCenterY = overlay.PlacementCenterY,
                        Thumbnail = overlay.Thumbnail
                    };
                    ViewModel.OverlayClips.Insert(index + 1, newOverlay);
                }
            }
        }

        private void RemoveOverlay_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            if (overlay != null)
            {
                ViewModel.OverlayClips.Remove(overlay);
                ViewModel.SelectedOverlay = null;
            }
        }

        private void OverlayListView_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement element && element.DataContext is CinematicOperation overlay)
            {
                ViewModel.SelectedOverlay = overlay;
            }
        }

        private void OverlaySection_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Add as overlay";
                e.Handled = true;
            }
        }

        private async void OverlaySection_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && (file.FileType == ".mp4" || file.FileType == ".mkv" || file.FileType == ".avi" || file.FileType == ".jpg" || file.FileType == ".png"))
                    {
                        await ViewModel.AddOverlayAsync(item.Path, ViewModel.CurrentStoryTime);
                    }
                }
            }
        }
    }
}

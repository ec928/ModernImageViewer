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
        }

        private void PlayerControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // The canvas resizes when the bottom dock is toggled — keep WYSIWYG/overlay aligned.
            _playbackEngine?.OnViewportResized();
        }

        private void CanvasModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playbackEngine == null) return;
            if (_playbackEngine.IsCanvasMode) _playbackEngine.ExitCanvasMode();
            else _playbackEngine.EnterCanvasMode();
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

        private void SetStart_Click(object sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedTimelineNode as CinematicOperation;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.StartMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        private void SetMid_Click(object sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedTimelineNode as CinematicOperation;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.MidMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        private void SetEnd_Click(object sender, RoutedEventArgs e)
        {
            var op = ViewModel.SelectedTimelineNode as CinematicOperation;
            var transform = PlayerControl.ActiveTransform;
            if (op != null && transform != null)
            {
                op.EndMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
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
                    // In canvas/arrange mode, selecting a PiP just targets it for the inspector —
                    // it does NOT dive into full-screen content editing (that's double-tap).
                    if (!ViewModel.IsPlaying && _playbackEngine?.IsCanvasMode != true)
                    {
                        _playbackEngine?.EnterOverlayEditMode(overlay);
                    }
                }
                else if (_playbackEngine?.IsCanvasMode != true)
                {
                    _playbackEngine?.ClearOverlayEditMode();
                }
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
            if (_playbackEngine != null)
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

        private void SetOverlayStart_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            var transform = PlayerControl.ActiveTransform;
            if (overlay != null && transform != null)
            {
                overlay.StartMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
            }
        }

        private void EditOverlayContent_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            if (overlay == null || _playbackEngine == null) return;
            _playbackEngine.ExitCanvasMode();
            _playbackEngine.EnterOverlayEditMode(overlay);
        }

        private void SetOverlayMid_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            var transform = PlayerControl.ActiveTransform;
            if (overlay != null && transform != null)
            {
                overlay.MidMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
            }
        }

        private void ClearOverlayMid_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            if (overlay != null)
            {
                overlay.MidMark = null; // Back to a two-point (Start -> End) motion
            }
            e.Handled = true;
        }

        private void SetOverlayEnd_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            var transform = PlayerControl.ActiveTransform;
            if (overlay != null && transform != null)
            {
                overlay.EndMark = new SpatialMark((float)transform.ScaleX, (float)transform.TranslateX, (float)transform.TranslateY);
            }
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
                        PlacementScale = overlay.PlacementScale,
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

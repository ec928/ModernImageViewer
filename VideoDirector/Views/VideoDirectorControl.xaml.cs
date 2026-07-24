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

        // Proportional timeline bar (§7E/F): px-per-second scale + the playhead line & handle.
        private double _timelinePxPerSec;
        private Microsoft.UI.Xaml.Shapes.Rectangle _playhead;
        private Microsoft.UI.Xaml.Shapes.Polygon _playheadKnob;
        // Pointer state: ruler = scrub; clip row tap = select; clip row drag = move/reorder.
        private Windows.Foundation.Point _timelinePressPoint;
        private bool _timelinePressed;
        private bool _timelineScrubbing;
        private bool _timelineMovingClip;
        private CinematicOperation _dragClip;
        private bool _dragIsSpine;
        private double _dragGrabOffsetSec;
        private double _dragCursorX;      // live cursor x, for the spine ghost
        private int _dragInsertIndex;     // where the ghost would drop

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
            ViewModel.OverlayTracks.CollectionChanged += (s, ev) => { HookOverlayTrackClips(); BuildTimelineBar(); };
            HookOverlayTrackClips();
            BuildTimelineBar();
        }

        // Each overlay track owns its own clip collection, so the timeline has to watch them all.
        private readonly System.Collections.Generic.HashSet<OverlayTrack> _hookedTracks = new();
        private void HookOverlayTrackClips()
        {
            foreach (var track in ViewModel.OverlayTracks)
                if (_hookedTracks.Add(track))
                    track.Clips.CollectionChanged += (s, ev) => BuildTimelineBar();
        }

        private void AddOverlayTrack_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AddOverlayTrack();   // capped at MaxOverlayTracks; adds a new timeline row
        }

        // The track that owns a given upper-track clip (null if it's a spine clip).
        private OverlayTrack TrackOf(CinematicOperation clip)
        {
            foreach (var track in ViewModel.OverlayTracks)
                if (track.Clips.Contains(clip)) return track;
            return null;
        }

        private void TimelineBar_SizeChanged(object sender, SizeChangedEventArgs e) => BuildTimelineBar();

        // Timeline layout: a scrub ruler on top, then the spine row, then the overlay row — all on
        // one shared px=seconds scale (§7E). Scrub on the ruler; drag clips in their rows.
        private const double RulerH = 14, RowSpineY = 16, RowOvY = 34, BlockH = 16, RowPitch = 18;

        // Bar height grows with the number of upper tracks.
        private double TimelineBarHeight => RowOvY + Math.Max(1, ViewModel.OverlayTracks.Count) * RowPitch + 2;

        private void BuildTimelineBar()
        {
            if (TimelineBar == null) return;
            TimelineBar.Children.Clear();
            _playhead = null; _playheadKnob = null;

            TimelineBar.Height = TimelineBarHeight;   // grows with the upper-track count
            double w = TimelineBar.ActualWidth;
            double h = TimelineBarHeight;
            double total = ViewModel.TotalStoryDuration.TotalSeconds;
            if (w <= 0 || total <= 0) { _timelinePxPerSec = 0; return; }
            _timelinePxPerSec = w / total;

            // Faint ruler strip marks the scrub zone.
            var ruler = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w, Height = RulerH, IsHitTestVisible = false,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0x88, 0x88, 0x88))
            };
            Canvas.SetLeft(ruler, 0); Canvas.SetTop(ruler, 0);
            TimelineBar.Children.Add(ruler);

            var spineColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
            var transColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x64, 0x74, 0x8B);
            bool spineGhost = _timelineMovingClip && _dragIsSpine && _dragClip != null;

            if (!spineGhost)
            {
                for (int i = 0; i < ViewModel.TimelineNodes.Count; i++)
                {
                    var clip = ViewModel.TimelineNodes[i];
                    double x = ViewModel.GetSpineClipStart(i).TotalSeconds * _timelinePxPerSec;
                    double cw = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                    AddTimelineBlock(x, RowSpineY, cw, BlockH, spineColor, clip);
                    double tw = clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                    if (tw > 0.5)
                        AddTimelineBlock(x + cw, RowSpineY, tw, BlockH, transColor);
                }
            }
            else
            {
                // Spine is order-based, so there is no continuous position to write. Instead the
                // other clips reflow with a gap at the insertion point, and the grabbed clip is
                // drawn as a free ghost under the cursor. The order only changes on release.
                double dragW = _dragClip.OpDuration.TotalSeconds * _timelinePxPerSec;
                double x = 0;
                int drawn = 0;
                foreach (var clip in ViewModel.TimelineNodes)
                {
                    if (clip == _dragClip) continue;
                    if (drawn == _dragInsertIndex) x += dragW;   // open the drop gap
                    double cw = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                    AddTimelineBlock(x, RowSpineY, cw, BlockH, spineColor, clip);
                    double tw = clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
                    if (tw > 0.5) AddTimelineBlock(x + cw, RowSpineY, tw, BlockH, transColor);
                    x += cw + tw;
                    drawn++;
                }

                double ghostX = _dragCursorX - _dragGrabOffsetSec * _timelinePxPerSec;
                AddTimelineBlock(ghostX, RowSpineY, dragW, BlockH,
                    Microsoft.UI.ColorHelper.FromArgb(0xCC, 0x93, 0xC5, 0xFD), _dragClip); // ghost
            }

            // One row per upper track (§7B) — same loop for 1 track or 3.
            for (int ti = 0; ti < ViewModel.OverlayTracks.Count; ti++)
            {
                double rowY = RowOvY + ti * RowPitch;
                foreach (var ov in ViewModel.OverlayTracks[ti].Clips)
                {
                    double x = ov.StartTimeSeconds * _timelinePxPerSec;
                    double ow = ov.OpDuration.TotalSeconds * _timelinePxPerSec;
                    AddTimelineBlock(x, rowY, ow, BlockH, Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), ov);
                }
            }

            // Playhead: a bright red line the full height with a downward triangle handle in the ruler.
            var red = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44));
            _playhead = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 2, Height = h, IsHitTestVisible = false, Fill = red };
            TimelineBar.Children.Add(_playhead);
            _playheadKnob = new Microsoft.UI.Xaml.Shapes.Polygon { IsHitTestVisible = false, Fill = red };
            _playheadKnob.Points.Add(new Windows.Foundation.Point(0, 0));
            _playheadKnob.Points.Add(new Windows.Foundation.Point(11, 0));
            _playheadKnob.Points.Add(new Windows.Foundation.Point(5.5, 9));
            TimelineBar.Children.Add(_playheadKnob);
            UpdatePlayhead();
            BuildTrackLabels();
        }

        // "Track 1".."Track 4" in the left gutter, vertically aligned to each row.
        private void BuildTrackLabels()
        {
            if (TimelineLabels == null) return;
            TimelineLabels.Children.Clear();
            TimelineLabels.Height = TimelineBarHeight;

            AddTrackLabel("Track 1", RowSpineY);
            for (int ti = 0; ti < ViewModel.OverlayTracks.Count; ti++)
                AddTrackLabel("Track " + (ti + 2), RowOvY + ti * RowPitch);
        }

        private void AddTrackLabel(string text, double y)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                IsHitTestVisible = false,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
            };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, y + 1);
            TimelineLabels.Children.Add(label);
        }

        private void AddTimelineBlock(double x, double y, double width, double height, Windows.UI.Color color, CinematicOperation clip = null)
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

            // Small file-name label inside the block (trims when the block is narrow).
            if (clip != null && !string.IsNullOrEmpty(clip.FileName) && width > 16)
            {
                var label = new TextBlock
                {
                    Text = clip.FileName,
                    FontSize = 9,
                    MaxWidth = width - 6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };
                Canvas.SetLeft(label, x + 4);
                Canvas.SetTop(label, y + 1);
                TimelineBar.Children.Add(label);
            }
        }

        // Timeline pointer model (standard NLE): the top ruler scrubs; the clip rows drag clips.
        // Tap in a row = select; drag in a row = move (overlay = reposition in time, spine =
        // reorder). Empty space in the rows also scrubs.
        private void TimelineBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(TimelineBar);
            // Only the left button (or a touch/pen contact, which also reports it) drives
            // scrub/select/drag. Without this, a right-click starts a drag and captures the
            // pointer, which suppresses RightTapped — i.e. no context menu.
            if (!point.Properties.IsLeftButtonPressed) return;

            var p = point.Position;
            _timelinePressPoint = p;
            _timelinePressed = true;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            TimelineBar.CapturePointer(e.Pointer);

            if (p.Y < RulerH) { _timelineScrubbing = true; ScrubToX(p.X); return; }

            var hit = HitClip(p);
            if (hit.clip != null)
            {
                _dragClip = hit.clip;
                _dragIsSpine = hit.isSpine;
                _dragGrabOffsetSec = (p.X / _timelinePxPerSec) - hit.startSec;
            }
            else { _timelineScrubbing = true; ScrubToX(p.X); }
        }

        private void TimelineBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_timelinePressed) return;
            var p = e.GetCurrentPoint(TimelineBar).Position;

            if (_timelineScrubbing) { ScrubToX(p.X); return; }
            if (_dragClip == null) return;
            if (!_timelineMovingClip && Math.Abs(p.X - _timelinePressPoint.X) < 4) return;
            _timelineMovingClip = true;

            if (_dragIsSpine)
            {
                // Ghost follows the cursor; the order itself is committed on release.
                _dragCursorX = p.X;
                _dragInsertIndex = ComputeSpineInsertIndex(p.X);
                BuildTimelineBar();
            }
            else MoveOverlayTo(p.X);
        }

        // Insertion index = how many OTHER spine clips have their centre left of the cursor,
        // measured in the layout with the dragged clip removed. Monotonic, so it can't oscillate.
        private int ComputeSpineInsertIndex(double cursorX)
        {
            int insert = 0;
            double x = 0;
            foreach (var clip in ViewModel.TimelineNodes)
            {
                if (clip == _dragClip) continue;
                double w = clip.OpDuration.TotalSeconds * _timelinePxPerSec;
                if (x + w / 2 < cursorX) insert++;
                x += w + clip.TransitionDuration.TotalSeconds * _timelinePxPerSec;
            }
            return insert;
        }

        private void TimelineBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            TimelineBar.ReleasePointerCapture(e.Pointer);
            if (_timelinePressed && _dragClip != null)
            {
                if (!_timelineMovingClip) SelectClip(_dragClip, _dragIsSpine); // a tap selects
                else if (_dragIsSpine)
                {
                    // Commit the reorder exactly once, at the ghost's drop position.
                    int cur = ViewModel.TimelineNodes.IndexOf(_dragClip);
                    int target = Math.Clamp(_dragInsertIndex, 0, ViewModel.TimelineNodes.Count - 1);
                    if (cur >= 0 && target != cur) ViewModel.TimelineNodes.Move(cur, target);
                }
            }
            _timelinePressed = false;
            _timelineScrubbing = false;
            _timelineMovingClip = false;
            _dragClip = null;
            BuildTimelineBar(); // clear the ghost / settle the layout
        }

        // Right-click a block for Duplicate / Remove (re-homed from the old dock tile menus).
        private void TimelineBar_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var p = e.GetPosition(TimelineBar);
            var hit = HitClip(p);
            if (hit.clip == null) return;

            SelectClip(hit.clip, hit.isSpine);

            var flyout = new MenuFlyout();
            var dup = new MenuFlyoutItem { Text = "Duplicate" };
            dup.Click += (s, ev) => DuplicateClip(hit.clip, hit.isSpine);
            var del = new MenuFlyoutItem { Text = "Remove" };
            del.Click += (s, ev) => RemoveClip(hit.clip, hit.isSpine);
            flyout.Items.Add(dup);
            flyout.Items.Add(del);
            flyout.ShowAt(TimelineBar, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = p });
            e.Handled = true;
        }

        private void DuplicateClip(CinematicOperation clip, bool isSpine)
        {
            var copy = new CinematicOperation
            {
                FilePath = clip.FilePath,
                VideoStartTime = clip.VideoStartTime,
                VideoEndTime = clip.VideoEndTime,
                OpDuration = clip.OpDuration,
                CurveProfile = clip.CurveProfile,
                StartMark = new SpatialMark(clip.StartMark.Scale, clip.StartMark.X, clip.StartMark.Y),
                EndMark = new SpatialMark(clip.EndMark.Scale, clip.EndMark.X, clip.EndMark.Y),
                TransitionDuration = clip.TransitionDuration,
                TransitionStyle = clip.TransitionStyle,
                Opacity = clip.Opacity,
                PlacementWidth = clip.PlacementWidth,
                PlacementHeight = clip.PlacementHeight,
                PlacementCenterX = clip.PlacementCenterX,
                PlacementCenterY = clip.PlacementCenterY,
                Thumbnail = clip.Thumbnail
            };

            if (isSpine)
            {
                int i = ViewModel.TimelineNodes.IndexOf(clip);
                if (i >= 0) ViewModel.TimelineNodes.Insert(i + 1, copy);
            }
            else
            {
                var track = TrackOf(clip);
                int i = track?.Clips.IndexOf(clip) ?? -1;
                if (i < 0) return;
                copy.StartTime = clip.StartTime + clip.OpDuration; // place right after the original
                track.Clips.Insert(i + 1, copy);
            }
        }

        private void RemoveClip(CinematicOperation clip, bool isSpine)
        {
            if (isSpine)
            {
                ViewModel.TimelineNodes.Remove(clip);
                if (ViewModel.SelectedTimelineNode == clip) ViewModel.SelectedTimelineNode = null;
            }
            else
            {
                TrackOf(clip)?.Clips.Remove(clip);
                if (ViewModel.SelectedOverlay == clip) ViewModel.SelectedOverlay = null;
            }
        }

        // Map x -> story time and seek the composite (spine frame + active overlays).
        private void ScrubToX(double x)
        {
            if (_timelinePxPerSec <= 0) return;
            double total = ViewModel.TotalStoryDuration.TotalSeconds;
            double sec = Math.Clamp(x / _timelinePxPerSec, 0, total);
            _playbackEngine?.SeekCompositeToStoryTime(TimeSpan.FromSeconds(sec));
        }

        // Which clip (and its start-second) sits under a point in the clip rows, if any.
        private (CinematicOperation clip, bool isSpine, double startSec) HitClip(Windows.Foundation.Point p)
        {
            if (_timelinePxPerSec <= 0) return (null, false, 0);
            var t = TimeSpan.FromSeconds(Math.Max(0, p.X / _timelinePxPerSec));

            if (p.Y >= RowSpineY && p.Y < RowSpineY + BlockH && ViewModel.TimelineNodes.Count > 0)
            {
                int idx = ViewModel.GetTimelineIndexForStoryTime(t);
                if (idx >= 0 && idx < ViewModel.TimelineNodes.Count)
                    return (ViewModel.TimelineNodes[idx], true, ViewModel.GetSpineClipStart(idx).TotalSeconds);
            }
            else if (p.Y >= RowOvY)
            {
                int ti = (int)((p.Y - RowOvY) / RowPitch);   // which upper-track row
                if (ti >= 0 && ti < ViewModel.OverlayTracks.Count)
                {
                    foreach (var ov in ViewModel.OverlayTracks[ti].Clips)
                        if (t >= ov.StartTime && t < ov.StartTime + ov.OpDuration)
                            return (ov, false, ov.StartTimeSeconds);
                }
            }
            return (null, false, 0);
        }

        private void SelectClip(CinematicOperation clip, bool isSpine)
        {
            if (isSpine)
            {
                ViewModel.SelectedTimelineNode = clip;
                int idx = ViewModel.TimelineNodes.IndexOf(clip);
                if (ViewModel.IsPlaying)
                {
                    if (_playbackEngine?.CurrentPlayingOperation != clip && idx >= 0)
                        _ = _playbackEngine?.StartPlaybackAsync(idx);
                }
                else _playbackEngine?.EnterEditMode(clip, ViewModel.CurrentEditTarget);
            }
            else ViewModel.SelectedOverlay = clip; // PropertyChanged enters overlay edit
        }

        // Overlay drag = free reposition in time (set StartTime), keeping the grab offset.
        private void MoveOverlayTo(double x)
        {
            if (_dragClip == null || _timelinePxPerSec <= 0) return;
            double total = ViewModel.TotalStoryDuration.TotalSeconds;
            double newStart = (x / _timelinePxPerSec) - _dragGrabOffsetSec;
            newStart = Math.Clamp(newStart, 0, Math.Max(0, total - _dragClip.OpDuration.TotalSeconds));
            _dragClip.StartTime = TimeSpan.FromSeconds(newStart);
            BuildTimelineBar();
        }


        private void UpdatePlayhead()
        {
            if (_playhead == null || _timelinePxPerSec <= 0) return;
            double x = ViewModel.CurrentStoryTime.TotalSeconds * _timelinePxPerSec;
            Canvas.SetLeft(_playhead, x);
            if (_playheadKnob != null) Canvas.SetLeft(_playheadKnob, x - 4.5);
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
                var track = TrackOf(overlay);
                int index = track?.Clips.IndexOf(overlay) ?? -1;
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
                    track.Clips.Insert(index + 1, newOverlay);
                }
            }
        }

        private void RemoveOverlay_Click(object sender, RoutedEventArgs e)
        {
            var overlay = ViewModel.SelectedOverlay;
            if (overlay != null)
            {
                TrackOf(overlay)?.Clips.Remove(overlay);
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

        // Drop a video/image onto the timeline strip to add it as a Track 2 overlay. The drop
        // position sets its start time (falls back to the playhead if the scale isn't ready).
        private async void OverlaySection_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var drop = e.GetPosition(TimelineBar);
                TimeSpan startTime = _timelinePxPerSec > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, drop.X / _timelinePxPerSec))
                    : ViewModel.CurrentStoryTime;

                // Dropping on a track's row targets that track; above the rows targets track 0.
                int trackIndex = drop.Y >= RowOvY ? (int)((drop.Y - RowOvY) / RowPitch) : 0;
                trackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, ViewModel.OverlayTracks.Count - 1));

                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && (file.FileType == ".mp4" || file.FileType == ".mkv" || file.FileType == ".avi" || file.FileType == ".jpg" || file.FileType == ".png"))
                    {
                        await ViewModel.AddOverlayAsync(item.Path, startTime, trackIndex);
                    }
                }
            }
        }
    }
}

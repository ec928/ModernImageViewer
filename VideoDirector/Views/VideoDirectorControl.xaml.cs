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
        private TextBlock _playheadTime;
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
        private Windows.Foundation.Point _lastHoverPoint;  // for the context menu's target
        private int _lastActiveSignature = -1;             // playback spotlight refresh guard

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
            PlayerControl.EditRequested += PlayerControl_EditRequested;
            PlayerControl.ExitEditRequested += (s, ev) => ExitEditMode();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.EditTargetChanged += ViewModel_EditTargetChanged;

            ViewModel.TimelineNodes.CollectionChanged += (s, ev) => BuildTimelineBar();
            ViewModel.OverlayTracks.CollectionChanged += (s, ev) => { HookOverlayTrackClips(); BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
            HookOverlayTrackClips();
            BuildTimelineBar();
        }

        // Each overlay track owns its own clip collection, so the timeline has to watch them all.
        private readonly System.Collections.Generic.HashSet<OverlayTrack> _hookedTracks = new();
        private void HookOverlayTrackClips()
        {
            foreach (var track in ViewModel.OverlayTracks)
                if (_hookedTracks.Add(track))
                    track.Clips.CollectionChanged += (s, ev) => { BuildTimelineBar(); _playbackEngine?.RefreshComposite(); };
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

        // Bar height grows with the number of upper tracks. The last row is BlockH tall starting at
        // (count-1)*RowPitch, so allow for that plus a small bottom margin — otherwise the bottom
        // track gets clipped by a few pixels.
        private double TimelineBarHeight =>
            RowOvY + (Math.Max(1, ViewModel.OverlayTracks.Count) - 1) * RowPitch + BlockH + 6;

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

            // Per-lane bands: a faint tint of the track's own colour distinguishes the lanes by
            // colour rather than by height (space is at a premium), and ties each lane to its
            // identity colour. Drawn first so blocks/gridlines paint over them.
            DrawRowBand(RowSpineY, w, TrackPalette.Spine);
            for (int ti = 0; ti < ViewModel.OverlayTracks.Count; ti++)
                DrawRowBand(RowOvY + ti * RowPitch, w, TrackPalette.Overlay(ti));

            // Faint ruler strip marks the scrub zone.
            var ruler = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w, Height = RulerH, IsHitTestVisible = false,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0x88, 0x88, 0x88))
            };
            Canvas.SetLeft(ruler, 0); Canvas.SetTop(ruler, 0);
            TimelineBar.Children.Add(ruler);

            // Time scale: labelled ticks in the ruler + faint full-height gridlines behind the blocks.
            var gridBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x16, 0x88, 0x88, 0x88));
            var tickBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x77, 0x88, 0x88, 0x88));
            var labelBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            double step = NiceTimeStep(total, w);
            for (double t = 0; t <= total + 0.001 && step > 0; t += step)
            {
                double gx = t * _timelinePxPerSec;
                var grid = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = h - RulerH, IsHitTestVisible = false, Fill = gridBrush };
                Canvas.SetLeft(grid, gx); Canvas.SetTop(grid, RulerH);
                TimelineBar.Children.Add(grid);

                var tick = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 4, IsHitTestVisible = false, Fill = tickBrush };
                Canvas.SetLeft(tick, gx); Canvas.SetTop(tick, RulerH - 4);
                TimelineBar.Children.Add(tick);

                if (gx < w - 26)
                {
                    var tl = new TextBlock { Text = FormatTimeShort(t), FontSize = 8, IsHitTestVisible = false, Foreground = labelBrush };
                    Canvas.SetLeft(tl, gx + 2); Canvas.SetTop(tl, 1);
                    TimelineBar.Children.Add(tl);
                }
            }

            var spineColor = TrackPalette.Spine;
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

            // One row per upper track (§7B) — same loop for 1 track or 3, each in its own colour.
            for (int ti = 0; ti < ViewModel.OverlayTracks.Count; ti++)
            {
                double rowY = RowOvY + ti * RowPitch;
                var trackColor = TrackPalette.Overlay(ti);
                foreach (var ov in ViewModel.OverlayTracks[ti].Clips)
                {
                    double x = ov.StartTimeSeconds * _timelinePxPerSec;
                    double ow = ov.OpDuration.TotalSeconds * _timelinePxPerSec;
                    AddTimelineBlock(x, rowY, ow, BlockH, trackColor, ov);
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

            // A small time readout that rides the playhead.
            _playheadTime = new TextBlock
            {
                FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsHitTestVisible = false,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44))
            };
            TimelineBar.Children.Add(_playheadTime);

            UpdatePlayhead();
            BuildTrackLabels();
        }

        // Faint band across a lane in the track's own colour — lane separation without extra height.
        private void DrawRowBand(double rowY, double w, Windows.UI.Color color)
        {
            var band = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w, Height = RowPitch, IsHitTestVisible = false,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(TrackPalette.At(color, 0x1E))
            };
            Canvas.SetLeft(band, 0); Canvas.SetTop(band, rowY - 1);
            TimelineBar.Children.Add(band);
        }

        // A "nice" tick interval (seconds) aiming for ~80px between ticks.
        private double NiceTimeStep(double totalSeconds, double w)
        {
            if (_timelinePxPerSec <= 0) return 0;
            double rough = 80.0 / _timelinePxPerSec;   // seconds per ~80px
            double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            foreach (var s in steps) if (s >= rough) return s;
            return steps[steps.Length - 1];
        }

        private static string FormatTimeShort(double seconds)
        {
            int m = (int)(seconds / 60);
            int s = (int)System.Math.Round(seconds - m * 60);
            if (s == 60) { m++; s = 0; }
            return m > 0 ? $"{m}:{s:00}" : $"{s}s";
        }

        // "Track 1".."Track 4" in the left gutter, vertically aligned to each row.
        private void BuildTrackLabels()
        {
            if (TimelineLabels == null) return;
            TimelineLabels.Children.Clear();
            TimelineLabels.Height = TimelineBarHeight;

            AddTrackLabel("Track 1", RowSpineY, TrackPalette.Spine);
            for (int ti = 0; ti < ViewModel.OverlayTracks.Count; ti++)
                AddTrackLabel("Track " + (ti + 2), RowOvY + ti * RowPitch, TrackPalette.Overlay(ti));
        }

        private void AddTrackLabel(string text, double y, Windows.UI.Color color)
        {
            // A colour cap ties the label to the track's identity colour (same as its blocks and
            // its on-screen PiP).
            var cap = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 4, Height = BlockH - 2, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
            };
            Canvas.SetLeft(cap, 4); Canvas.SetTop(cap, y + 1);
            TimelineLabels.Children.Add(cap);

            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                IsHitTestVisible = false,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
            };
            Canvas.SetLeft(label, 12);
            Canvas.SetTop(label, y + 1);
            TimelineLabels.Children.Add(label);
        }

        // Spotlight opacity for a clip block, by mode:
        //   Arrange (not playing, nothing selected) -> everything full.
        //   Edit    (a clip selected)               -> that clip full, the rest dim.
        //   Play                                    -> every clip active at the playhead full, rest dim.
        private double BlockDim(CinematicOperation clip)
        {
            if (clip == null) return 1.0;                       // transitions / drag ghost

            // Edit spotlights the one edited clip; Play AND Arrange both spotlight whatever is on
            // screen at the playhead (the composite), so the timeline mirrors what you see.
            if (ViewModel.IsEditMode)
                return ReferenceEquals(clip, ViewModel.SelectedClip) ? 1.0 : 0.5;
            return IsActiveAtPlayhead(clip) ? 1.0 : 0.5;
        }

        private bool IsActiveAtPlayhead(CinematicOperation clip)
        {
            var t = ViewModel.CurrentStoryTime;
            if (ViewModel.TimelineNodes.Contains(clip))   // spine: the clip at the playhead
                return ViewModel.TimelineNodes.IndexOf(clip) == ViewModel.GetTimelineIndexForStoryTime(t);
            return clip.IsActiveAt(t);                    // overlay: live in its window
        }

        // Which clips are on screen right now — as a signature, so playback can rebuild the
        // highlights only when the active set actually changes (an overlay starts/ends), not every
        // frame. Spine boundaries already rebuild via the SelectedClip change.
        private int ActiveSignature()
        {
            var t = ViewModel.CurrentStoryTime;
            int sig = 17 * 31 + ViewModel.GetTimelineIndexForStoryTime(t);
            foreach (var track in ViewModel.OverlayTracks)
                foreach (var ov in track.Clips)
                    if (ov.IsActiveAt(t)) sig = sig * 31 + ov.GetHashCode();
            return sig;
        }

        private void AddTimelineBlock(double x, double y, double width, double height, Windows.UI.Color color, CinematicOperation clip = null)
        {
            if (width < 1) width = 1;

            // Spotlight (#1): the in-focus clip(s) stay full strength, the rest dim. "In focus"
            // depends on mode — Arrange: all; Edit: the edited clip; Play: everything on screen now.
            double dim = BlockDim(clip);

            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = 2,
                RadiusY = 2,
                Opacity = dim,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            TimelineBar.Children.Add(r);

            // File-name label inside the block, in whichever of black/white reads on this colour.
            if (clip != null && !string.IsNullOrEmpty(clip.FileName) && width > 16)
            {
                var label = new TextBlock
                {
                    Text = clip.FileName,
                    FontSize = 9,
                    MaxWidth = width - 6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                    Opacity = dim,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(TrackPalette.TextOn(color))
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
            // Recorded even when not dragging: the context menu resolves its target from here.
            _lastHoverPoint = e.GetCurrentPoint(TimelineBar).Position;

            if (!_timelinePressed) return;
            var p = _lastHoverPoint;

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
            else MoveOverlayTo(p);   // x = time, y = which track
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
            // This fires for the RIGHT button too. If we never started a left-press, do nothing —
            // in particular do NOT rebuild the bar: rebuilding destroys every Canvas child,
            // including the element the right-tap gesture started on, which kills the pending
            // context gesture before the flyout can open.
            if (!_timelinePressed) return;

            TimelineBar.ReleasePointerCapture(e.Pointer);
            bool wasMoving = _timelineMovingClip;

            if (_dragClip != null)
            {
                if (!wasMoving) SelectClip(_dragClip, _dragIsSpine); // a tap selects
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
            if (wasMoving) BuildTimelineBar(); // only needed to clear the drag ghost
        }

        // Duplicate / Remove for the block under the cursor. The platform opens the ContextFlyout;
        // we just resolve which clip it applies to from the last pointer position.
        private CinematicOperation _contextClip;
        private bool _contextIsSpine;

        private void TimelineContextMenu_Opening(object sender, object e)
        {
            // Keep this trivial: just record what was under the cursor. It previously also called
            // SelectClip (which changes mode / starts async work) — an exception in Opening aborts
            // the flyout, which is a candidate for "right-click does nothing".
            var hit = HitClip(_lastHoverPoint);
            _contextClip = hit.clip;
            _contextIsSpine = hit.isSpine;
        }

        private void TimelineDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (_contextClip != null) DuplicateClip(_contextClip, _contextIsSpine);
        }

        private void TimelineRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_contextClip != null) RemoveClip(_contextClip, _contextIsSpine);
        }

        private void DuplicateClip(CinematicOperation clip, bool isSpine)
        {
            var copy = new CinematicOperation
            {
                FilePath = clip.FilePath,
                // SourceDuration and PlaybackSpeed must precede the trim: the trim setters clamp
                // against the source length and derive OpDuration from the speed.
                SourceDuration = clip.SourceDuration,
                SourceAspect = clip.SourceAspect,
                PlaybackSpeed = clip.PlaybackSpeed,
                VideoStartTime = clip.VideoStartTime,
                VideoEndTime = clip.VideoEndTime,
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
                    return;
                }
            }
            else ViewModel.SelectedOverlay = clip;

            // One entry point for both tracks — selecting a clip (while not playing) edits it.
            if (!ViewModel.IsPlaying)
                _playbackEngine?.BeginEdit(ViewModel.SelectedClip, ViewModel.CurrentEditTarget);
        }

        // Overlay drag: horizontally = reposition in time, vertically = move to another track.
        private void MoveOverlayTo(Windows.Foundation.Point p)
        {
            if (_dragClip == null || _timelinePxPerSec <= 0) return;

            // Vertical: which track row is the cursor over?
            int targetIndex = p.Y >= RowOvY ? (int)((p.Y - RowOvY) / RowPitch) : 0;
            targetIndex = Math.Clamp(targetIndex, 0, ViewModel.OverlayTracks.Count - 1);
            var target = ViewModel.OverlayTracks[targetIndex];
            var current = TrackOf(_dragClip);
            if (current != null && !ReferenceEquals(current, target))
            {
                current.Clips.Remove(_dragClip);
                target.Clips.Add(_dragClip);
            }

            // Horizontal: set the start time, clamped so it can't overlap siblings on this track
            // (tracks are strict — an overlap would silently hide one clip at playback).
            double total = ViewModel.TotalStoryDuration.TotalSeconds;
            double dur = _dragClip.OpDuration.TotalSeconds;
            double newStart = (p.X / _timelinePxPerSec) - _dragGrabOffsetSec;
            newStart = Math.Clamp(newStart, 0, Math.Max(0, total - dur));
            newStart = target.ClampToFreeSlot(_dragClip, newStart, dur);
            _dragClip.StartTime = TimeSpan.FromSeconds(newStart);
            BuildTimelineBar();
            _playbackEngine?.RefreshComposite();   // moving in time can change what's on screen
        }


        private void UpdatePlayhead()
        {
            if (_playhead == null || _timelinePxPerSec <= 0) return;
            double sec = ViewModel.CurrentStoryTime.TotalSeconds;
            double x = sec * _timelinePxPerSec;
            Canvas.SetLeft(_playhead, x);
            if (_playheadKnob != null) Canvas.SetLeft(_playheadKnob, x - 4.5);

            if (_playheadTime != null)
            {
                int m = (int)(sec / 60);
                _playheadTime.Text = m > 0 ? $"{m}:{sec - m * 60:00.0}" : $"{sec:0.0}s";
                // Sit just right of the playhead, flipping to the left near the right edge.
                double w = TimelineBar?.ActualWidth ?? 0;
                double tx = x + 4;
                if (tx > w - 40) tx = x - 40;
                Canvas.SetLeft(_playheadTime, System.Math.Max(0, tx));
                Canvas.SetTop(_playheadTime, RulerH + 1);
            }
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
                _playbackEngine?.BeginEdit(op, ViewModel.CurrentEditTarget);
            }
        }

        private void PlayerControl_ViewportTransformChanged(object sender, EventArgs e)
        {
            if (ViewModel.IsPlaying || ViewModel.SelectedClip == null) return;
            var op = ViewModel.SelectedClip as CinematicOperation;
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
                // Play OR Arrange (scrubbing): refresh the spotlight when the set of clips on
                // screen changes (not every frame). Edit ignores the playhead.
                if (!ViewModel.IsEditMode)
                {
                    int sig = ActiveSignature();
                    if (sig != _lastActiveSignature) { _lastActiveSignature = sig; BuildTimelineBar(); }
                }
                return;
            }
            if (e.PropertyName == nameof(DirectorViewModel.IsEditMode))
            {
                BuildTimelineBar(); // spotlight switches between Edit and Arrange
                // Zone F: the global timeline recedes in Edit so it can't be confused with the
                // Playbar's per-clip scrubber — dimmed and non-interactive until back in Arrange.
                if (TrackDock != null)
                {
                    TrackDock.Opacity = ViewModel.IsEditMode ? 0.35 : 1.0;
                    TrackDock.IsHitTestVisible = !ViewModel.IsEditMode;
                }
                return;
            }
            if (e.PropertyName == nameof(DirectorViewModel.SelectedClip))
            {
                BuildTimelineBar();                  // redraw so the selection highlight moves
                _playbackEngine?.RefreshComposite(); // and so the PiP chrome follows the selection
            }
            if (e.PropertyName == nameof(DirectorViewModel.IsPlaying))
            {
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Symbol = ViewModel.IsPlaying ? Symbol.Pause : Symbol.Play;
                }
                _playbackEngine?.UpdateWysiwygOverlay();

                // Whenever playback stops by ANY route (pause, stop, reaching the end), put the
                // PiPs back into arrangeable stills. Keying off the observable state rather than
                // one specific method means no path can miss it.
                if (!ViewModel.IsPlaying) _playbackEngine?.RefreshComposite();

                // Rebuild so the spotlight switches between play-mode (active clips) and
                // selection-mode logic; reset the signature so the next play refreshes.
                _lastActiveSignature = -1;
                BuildTimelineBar();
            }
            // Note: entering Edit on selection is owned by SelectClip -> BeginEdit (one entry point
            // for both tracks), so there's no per-track edit trigger here anymore.
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
                    EditNewestSpineClip();   // open the new clip so you can trim/frame it
                }
            }
        }

        // After adding to Track 1, drop straight into Edit on the newest clip so it can be trimmed
        // and framed immediately (adding a clip is usually the prelude to editing it).
        private void EditNewestSpineClip()
        {
            if (ViewModel.IsPlaying || ViewModel.TimelineNodes.Count == 0) return;
            SelectClip(ViewModel.TimelineNodes[^1], isSpine: true);
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

        private async void Clear_Click(object sender, RoutedEventArgs e)
        {
            bool hasContent = ViewModel.TimelineNodes.Count > 0;
            if (!hasContent)
                foreach (var t in ViewModel.OverlayTracks)
                    if (t.Clips.Count > 0) { hasContent = true; break; }

            if (hasContent)
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear project?",
                    Content = "This removes every clip from all tracks. It can't be undone.",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            ViewModel.Clear();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.TimelineNodes.Count == 0)
            {
                await ShowExportMessage("Nothing to export", "Add at least one Track 1 clip first.");
                return;
            }

            var savePicker = new FileSavePicker();
            var window = MainWindow.Instance;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            savePicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            savePicker.FileTypeChoices.Add("MP4 Video", new List<string>() { ".mp4" });
            savePicker.SuggestedFileName = "Export";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            var bar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 320 };
            var status = new TextBlock { Text = "Rendering the composite (spine + overlays) — this can take a while for long clips." };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(status);
            panel.Children.Add(bar);
            var progressDialog = new ContentDialog
            {
                Title = "Exporting video",
                Content = panel,
                XamlRoot = this.XamlRoot
            };

            var exporter = new Models.VideoExporter();
            var progress = new Progress<double>(p => bar.Value = p);

            _ = progressDialog.ShowAsync(); // non-blocking; hidden when the render finishes
            var result = await exporter.ExportAsync(ViewModel.TimelineNodes, ViewModel.OverlayTracks, file, progress);
            progressDialog.Hide();

            switch (result.Outcome)
            {
                case Models.VideoExporter.ExportOutcome.Success:
                    var msg = $"Saved to:\n{result.Message}";
                    if (result.SkippedFiles.Count > 0)
                        msg += $"\n\nSkipped {result.SkippedFiles.Count} clip(s) with missing files:\n• " + string.Join("\n• ", result.SkippedFiles);
                    await ShowExportMessage("Export complete", msg);
                    break;
                case Models.VideoExporter.ExportOutcome.NothingToRender:
                    await ShowExportMessage("Nothing to export", result.Message);
                    break;
                default:
                    await ShowExportMessage("Export failed", result.Message);
                    break;
            }
        }

        private async Task ShowExportMessage(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }





        private void ResetClip_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedClip != null)
            {
                ViewModel.SelectedClip.Reset();
                _playbackEngine?.UpdateWysiwygOverlay();
            }
        }


        private void ExitToArrange_Click(object sender, RoutedEventArgs e) => ExitEditMode();

        // Double-tap the image (Arrange) to edit the clip under the cursor: an overlay PiP, or the
        // Track 1 clip at the playhead if the tap wasn't on a PiP. Routes through SelectClip so
        // there's one entry path (selection + enter-edit) shared with the timeline.
        private void PlayerControl_EditRequested(object sender, int slot)
        {
            if (ViewModel.IsPlaying) return;
            if (slot >= 0)
            {
                var clip = _playbackEngine?.GetActiveOverlay(slot);
                if (clip != null) SelectClip(clip, isSpine: false);
            }
            else if (ViewModel.TimelineNodes.Count > 0)
            {
                int idx = ViewModel.GetTimelineIndexForStoryTime(ViewModel.CurrentStoryTime);
                if (idx >= 0 && idx < ViewModel.TimelineNodes.Count)
                    SelectClip(ViewModel.TimelineNodes[idx], isSpine: true);
            }
        }

        private void ExitEditMode()
        {
            if (!ViewModel.IsEditMode) return;
            // Clear the selection so we don't immediately re-enter Edit, then return to Arrange.
            ViewModel.SelectedTimelineNode = null;
            ViewModel.SelectedOverlay = null;
            _playbackEngine?.ExitToArrange();
        }

        private void EscapeAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.IsEditMode) { ExitEditMode(); args.Handled = true; }
        }

        // Space = play/pause. Ignored while typing so it doesn't hijack text entry.
        private void PlayPauseAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                                  Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused()) return;
            args.Handled = true;
            PlayPause_Click(this, null);
        }

        // Delete = remove the selected clip (never while typing or during playback). If the clip is
        // being edited, drop back to Arrange first so we don't linger in Edit on a deleted clip.
        private void DeleteAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                               Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsTextInputFocused() || ViewModel.IsPlaying || ViewModel.SelectedClip == null) return;
            args.Handled = true;
            var clip = ViewModel.SelectedClip;
            bool isSpine = ViewModel.IsTrack1Selected;
            if (ViewModel.IsEditMode) ExitEditMode();
            RemoveClip(clip, isSpine);
        }

        // A NumberBox hosts an inner TextBox, so a focused TextBox means the user is typing —
        // in which case Space/Delete must reach the field, not trigger a shortcut.
        private bool IsTextInputFocused()
            => FocusManager.GetFocusedElement(this.XamlRoot) is TextBox;





        private void OverlaySection_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                // Name the row being hovered, so it's obvious where the drop will land.
                e.DragUIOverride.Caption = "Add to " + TrackNameAt(e.GetPosition(TimelineBar).Y);
                e.Handled = true;
            }
        }

        // Which track a given y in the timeline belongs to: the Track 1 row (and the ruler above
        // it) is the spine; the rows below are the overlay tracks.
        private string TrackNameAt(double y)
        {
            if (y < RowOvY) return "Track 1";
            int i = Math.Clamp((int)((y - RowOvY) / RowPitch), 0, Math.Max(0, ViewModel.OverlayTracks.Count - 1));
            return "Track " + (i + 2);
        }

        // Drop a video/image onto the timeline strip to add it. Which row you drop on decides the
        // track (Track 1 row = spine, lower rows = that overlay track); the drop x sets the start
        // time (falls back to the playhead if the scale isn't ready).
        private async void OverlaySection_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var drop = e.GetPosition(TimelineBar);
                TimeSpan startTime = _timelinePxPerSec > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, drop.X / _timelinePxPerSec))
                    : ViewModel.CurrentStoryTime;

                // The row you drop on decides the destination: the Track 1 row (and the ruler
                // above it) adds to the spine; the rows below add to that overlay track.
                bool toSpine = drop.Y < RowOvY;
                int trackIndex = toSpine ? 0 : (int)((drop.Y - RowOvY) / RowPitch);
                trackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, ViewModel.OverlayTracks.Count - 1));

                var items = await e.DataView.GetStorageItemsAsync();
                var spinePaths = new System.Collections.Generic.List<string>();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file && (file.FileType == ".mp4" || file.FileType == ".mkv" || file.FileType == ".avi" || file.FileType == ".jpg" || file.FileType == ".png"))
                    {
                        if (toSpine) spinePaths.Add(item.Path);
                        else await ViewModel.AddOverlayAsync(item.Path, startTime, trackIndex);
                    }
                }
                if (spinePaths.Count > 0) await ViewModel.AddFilesAsync(spinePaths);

                // Open the newest clip for editing, same as a canvas drop.
                if (toSpine) EditNewestSpineClip();
                else if (!ViewModel.IsPlaying)
                {
                    var track = ViewModel.OverlayTracks[trackIndex];
                    if (track.Clips.Count > 0) SelectClip(track.Clips[^1], isSpine: false);
                }
            }
        }
    }
}

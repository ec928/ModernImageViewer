using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media.FaceAnalysis;
using Windows.Storage;
using ModernImageViewer.Cinematic.Data;
using ModernImageViewer.Cinematic.ViewModels;
using ModernImageViewer.Cinematic.Services;


namespace ModernImageViewer.Cinematic
{
    public sealed partial class CinematicWindow
    {
        // --------------------------------------------------------
        // UI SYNC & WYSIWYG
        // --------------------------------------------------------
        private void SyncPlayPauseUI()
        {
            if (_editing && ViewModel.IsPlaying && !_previewing)
                ViewModel.TogglePlay();

            ApplyEditChrome();
            if (_editing && !_previewing && !ViewModel.HasUnsavedZoomPan) ResetKeyframeBoxes();
            SyncMusicPlayback();
            SlideshowCanvas.Invalidate();
        }

        /// <summary>Everything Edit show puts on screen. A preview takes it all away, so it shows the shot as it plays.</summary>
        private void ApplyEditChrome()
        {
            var editVisibility = _editing && !_previewing ? Visibility.Visible : Visibility.Collapsed;
            EditPanel.Visibility = editVisibility;
            SequenceBar.Visibility = editVisibility;
            WysiwygOverlay.Visibility = editVisibility;
            PreviewBanner.Visibility = _previewing ? Visibility.Visible : Visibility.Collapsed;
            BtnEditShow.IsChecked = _editing;
            ApplyPlaybarToggle(BtnEditShow, _editing);
            ActionBar.Margin = new Thickness(0, 0, _editing && !_previewing ? EditPanelWidth : 0, 40);
            if (_editing) ActionBar.Opacity = 1.0;
        }

        private void SetEditing(bool on)
        {
            if (on == _editing) return;
            bool wasPreviewing = _previewing;
            _previewing = false;
            _editing = on;
            DiscardPreload();
            if (on)
            {
                if (ViewModel.IsPlaying) ViewModel.TogglePlay();
                _idleTimer.Stop();
                SyncPlayPauseUI();
                ScrollSequenceToCurrent();
                return;
            }

            if (ViewModel.HasUnsavedZoomPan) ViewModel.DiscardZoomPanDraft();
            UpdateActiveTrajectoriesAsync();
            if (ViewModel.CurrentClip?.IsHidden == true) ViewModel.MoveNext();
            if (!ViewModel.IsPlaying) ViewModel.TogglePlay();
            _slideStartTime = TimeSpan.Zero;
            SyncPlayPauseUI();
            _idleTimer.Start();
        }

        private void BtnEditShow_Click(object sender, RoutedEventArgs e) => SetEditing(BtnEditShow.IsChecked == true);

        private void EndPreview()
        {
            _previewing = false;
            if (ViewModel.IsPlaying) ViewModel.TogglePlay();
            DiscardPreload();
            SlideProgressBar.Value = 0;
            SyncPlayPauseUI();
            SlideshowCanvas.Invalidate();
        }

        private void BtnUseAutoDuration_Click(object sender, RoutedEventArgs e) => ViewModel.UseAutoDuration();

        private void DrawEditWorkspace(CanvasDrawEventArgs args, SlideFrame frame, Size bounds)
        {
            var imgSize = frame.Intelligence.ImageSize;
            if (!TryEditLayout(bounds.Width, bounds.Height, imgSize, out float scale, out float drawW, out float drawH, out float ox, out float oy))
                return;

            var ds = args.DrawingSession;
            ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(ox, oy);
            ds.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, 1.0f, CanvasImageInterpolation.HighQualityCubic);
            ds.Transform = Matrix3x2.Identity;

            var selected = SelectedBox();
            if (selected != null)
            {
                float l = (float)Canvas.GetLeft(selected);
                float t = (float)Canvas.GetTop(selected);
                float w = (float)selected.Width;
                float h = (float)selected.Height;
                var dim = Windows.UI.Color.FromArgb(150, 0, 0, 0);
                ds.FillRectangle(0, 0, (float)bounds.Width, t, dim);
                ds.FillRectangle(0, t + h, (float)bounds.Width, (float)bounds.Height - (t + h), dim);
                ds.FillRectangle(0, t, l, h, dim);
                ds.FillRectangle(l + w, t, (float)bounds.Width - (l + w), h, dim);
            }

            DrawFramedPreview(ds, frame, bounds);
        }

        private void DrawFramedPreview(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, SlideFrame frame, Size bounds)
        {
            if (SelectedBox() == null || bounds.Width < 1) return;

            float pipW = 180f;
            float pipH = pipW * (float)(bounds.Height / Math.Max(1, bounds.Width));
            float pipX = 16f;
            float pipY = (float)(bounds.Height - EditBottomReserve) - pipH - 8f;
            var pipRect = new Rect(pipX, pipY, pipW, pipH);
            float k = pipW / (float)Math.Max(1, bounds.Width);
            float scale = _selectedIsStart ? frame.Trajectory.StartScale : frame.Trajectory.EndScale;
            var pan = _selectedIsStart ? frame.Trajectory.StartPan : frame.Trajectory.EndPan;

            ds.FillRectangle(pipRect, Windows.UI.Color.FromArgb(240, 0, 0, 0));
            using (ds.CreateLayer(1f, pipRect))
            {
                ds.Transform = Matrix3x2.CreateScale(scale * k) * Matrix3x2.CreateTranslation(pan.X * k + pipX, pan.Y * k + pipY);
                ds.DrawImage(frame.GpuBitmap, 0, 0, frame.GpuBitmap.Bounds, 1.0f, CanvasImageInterpolation.HighQualityCubic);
                ds.Transform = Matrix3x2.Identity;
            }

            var accent = _selectedIsStart
                ? Windows.UI.Color.FromArgb(255, 0, 212, 255)
                : Windows.UI.Color.FromArgb(255, 255, 138, 0);
            ds.DrawRectangle(pipRect, accent, 2.5f);
            using var fmt = new CanvasTextFormat { FontSize = 13, FontFamily = "Segoe UI" };
            ds.DrawText(_selectedIsStart ? "Start, as it fills the screen" : "End, as it fills the screen",
                pipX + 8, pipY + 6, accent, fmt);
        }

        private void WysiwygOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_editing && !ViewModel.IsPlaying && !_isDraggingBox && !_isResizing)
                ResetKeyframeBoxes();
        }

        private void WysiwygOverlay_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var box = SelectedBox();
            if (box == null || _primaryFrame == null) return;
            if (!TryEditLayout(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight, _primaryFrame.Intelligence.ImageSize, out _, out double drawW, out double drawH, out double ox, out double oy))
                return;

            double aspect = OutputAspect();
            double oldW = box.Width, oldH = box.Height;
            double oldL = Canvas.GetLeft(box), oldT = Canvas.GetTop(box);
            double delta = e.GetCurrentPoint(WysiwygOverlay).Properties.MouseWheelDelta;
            double newW = oldW + (delta > 0 ? -28 : 28);
            ApplyBoxRect(box, oldL + (oldW - newW) / 2, oldT + (oldH - newW / aspect) / 2, newW, newW / aspect);
            ApplyChrome();
            CommitKeyframeCrops(persist: false);
            SlideshowCanvas.Invalidate();
            e.Handled = true;
        }

        // The picture being framed stays clear of what sits over the canvas: the panel, the strip and the bar.
        private const double EditPanelWidth = 340;
        private const double EditTopReserve = 8;
        private const double EditBottomReserve = 256;

        private bool TryEditLayout(double viewW, double viewH, Size img, out float scale, out float drawW, out float drawH, out float ox, out float oy)
        {
            scale = drawW = drawH = ox = oy = 0;
            viewW -= EditPanelWidth;
            viewH -= EditTopReserve + EditBottomReserve;
            if (viewW <= 1 || viewH <= 1) return false;
            double iw = Math.Max(1, img.Width);
            double ih = Math.Max(1, img.Height);
            double pad = Math.Clamp(Math.Min(viewW, viewH) * 0.09, 52, 96);
            double availW = Math.Max(32, viewW - 2 * pad);
            double availH = Math.Max(32, viewH - 2 * pad);
            scale = (float)Math.Min(availW / iw, availH / ih);
            drawW = (float)(iw * scale);
            drawH = (float)(ih * scale);
            ox = (float)((viewW - drawW) / 2);
            oy = (float)((viewH - drawH) / 2 + EditTopReserve);
            return drawW > 1 && drawH > 1;
        }

        private bool TryEditLayout(double viewW, double viewH, Size img, out float scale, out double drawW, out double drawH, out double ox, out double oy)
        {
            bool ok = TryEditLayout(viewW, viewH, img, out scale, out float fw, out float fh, out float fox, out float foy);
            drawW = fw; drawH = fh; ox = fox; oy = foy;
            return ok;
        }

        private double OutputAspect() => SlideshowCanvas.ActualWidth / Math.Max(1, SlideshowCanvas.ActualHeight);

        private Border? SelectedBox() => _selectedIsStart ? StartFrameBox : EndFrameBox;

        private void PlaceCrop(Border box, Rect crop, double drawW, double drawH, double ox, double oy)
        {
            box.Width = Math.Max(8, crop.Width * drawW);
            box.Height = Math.Max(8, crop.Height * drawH);
            Canvas.SetLeft(box, ox + crop.X * drawW);
            Canvas.SetTop(box, oy + crop.Y * drawH);
        }

        private void ApplyBoxRect(Border box, double left, double top, double width, double height)
        {
            double aspect = OutputAspect();
            double viewW = Math.Max(32, WysiwygOverlay.ActualWidth - EditPanelWidth);
            double viewH = Math.Max(32, WysiwygOverlay.ActualHeight - EditTopReserve - EditBottomReserve);
            width = Math.Clamp(width, 32, viewW);
            height = width / aspect;
            if (height > viewH) { height = viewH; width = height * aspect; }
            left = Math.Clamp(left, 0, viewW - width);
            top = Math.Clamp(top, EditTopReserve, EditTopReserve + viewH - height);
            box.Width = width;
            box.Height = height;
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
        }

        private Rect CropFromBox(Border box, double drawW, double drawH, double ox, double oy)
        {
            double x = (Canvas.GetLeft(box) - ox) / drawW;
            double y = (Canvas.GetTop(box) - oy) / drawH;
            double w = Math.Max(0.02, box.Width / drawW);
            double h = Math.Max(0.02, box.Height / drawH);
            return new Rect(x, y, w, h);
        }

        private void ResetKeyframeBoxes()
        {
            if (StartFrameBox == null || EndFrameBox == null || _primaryFrame == null) return;
            if (_isDraggingBox || _isResizing) return;
            if (ViewModel.HasUnsavedZoomPan) { ApplyChrome(); return; }
            if (!TryEditLayout(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight, _primaryFrame.Intelligence.ImageSize, out _, out double drawW, out double drawH, out double ox, out double oy))
                return;

            var canvas = new Size(SlideshowCanvas.ActualWidth, SlideshowCanvas.ActualHeight);
            var img = _primaryFrame.Intelligence.ImageSize;
            var traj = _primaryFrame.Trajectory;

            Rect start, end;
            if (ViewModel.IsOverrideMode && ViewModel.CurrentDraft.StartCrop != null && ViewModel.CurrentDraft.EndCrop != null)
            {
                start = new Rect(ViewModel.CurrentDraft.StartCrop.X, ViewModel.CurrentDraft.StartCrop.Y, ViewModel.CurrentDraft.StartCrop.Width, ViewModel.CurrentDraft.StartCrop.Height);
                end = new Rect(ViewModel.CurrentDraft.EndCrop.X, ViewModel.CurrentDraft.EndCrop.Y, ViewModel.CurrentDraft.EndCrop.Width, ViewModel.CurrentDraft.EndCrop.Height);
            }
            else if (_primaryFrame.HasEngineCrops)
            {
                start = _primaryFrame.EngineStartCrop;
                end = _primaryFrame.EngineEndCrop;
            }
            else
            {
                start = CameraMath.CropFromCamera(traj.StartScale, traj.StartPan, canvas, img);
                end = CameraMath.CropFromCamera(traj.EndScale, traj.EndPan, canvas, img);
                SnapshotEngineCrops(_primaryFrame, traj, canvas);
            }

            PlaceCrop(StartFrameBox, start, drawW, drawH, ox, oy);
            PlaceCrop(EndFrameBox, end, drawW, drawH, ox, oy);
            ApplyCropsToTrajectory(start, end);
            SelectKeyframe(_selectedIsStart);
        }

        private void ApplyChrome()
        {
            if (StartFrameBox == null) return;

            var box = SelectedBox();
            if (box == null) return;
            double l = Canvas.GetLeft(box), t = Canvas.GetTop(box), w = box.Width, h = box.Height, hs = 16;
            Canvas.SetLeft(HandleTL, l - hs / 2); Canvas.SetTop(HandleTL, t - hs / 2);
            Canvas.SetLeft(HandleTR, l + w - hs / 2); Canvas.SetTop(HandleTR, t - hs / 2);
            Canvas.SetLeft(HandleBL, l - hs / 2); Canvas.SetTop(HandleBL, t + h - hs / 2);
            Canvas.SetLeft(HandleBR, l + w - hs / 2); Canvas.SetTop(HandleBR, t + h - hs / 2);

            var stroke = _selectedIsStart
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 212, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 138, 0));
            HandleTL.Stroke = stroke; HandleTR.Stroke = stroke; HandleBL.Stroke = stroke; HandleBR.Stroke = stroke;
            Canvas.SetZIndex(HandleTL, 30); Canvas.SetZIndex(HandleTR, 30);
            Canvas.SetZIndex(HandleBL, 30); Canvas.SetZIndex(HandleBR, 30);

            MotionPath.X1 = Canvas.GetLeft(StartFrameBox) + StartFrameBox.Width / 2;
            MotionPath.Y1 = Canvas.GetTop(StartFrameBox) + StartFrameBox.Height / 2;
            MotionPath.X2 = Canvas.GetLeft(EndFrameBox) + EndFrameBox.Width / 2;
            MotionPath.Y2 = Canvas.GetTop(EndFrameBox) + EndFrameBox.Height / 2;
        }

        private void SelectKeyframe(bool start)
        {
            _selectedIsStart = start;
            _activeKeyframeBox = start ? StartFrameBox : EndFrameBox;
            if (StartFrameBox != null)
            {
                StartFrameBox.BorderThickness = new Thickness(start ? 4 : 2);
                EndFrameBox.BorderThickness = new Thickness(start ? 2 : 4);
                Canvas.SetZIndex(StartFrameBox, start ? 20 : 10);
                Canvas.SetZIndex(EndFrameBox, start ? 10 : 20);
            }
            ApplyChrome();
            SlideshowCanvas.Invalidate();
        }

        private bool TryReadCrops(out Rect start, out Rect end)
        {
            start = end = default;
            if (_primaryFrame == null) return false;
            if (!TryEditLayout(WysiwygOverlay.ActualWidth, WysiwygOverlay.ActualHeight, _primaryFrame.Intelligence.ImageSize, out _, out double drawW, out double drawH, out double ox, out double oy))
                return false;
            start = CropFromBox(StartFrameBox, drawW, drawH, ox, oy);
            end = CropFromBox(EndFrameBox, drawW, drawH, ox, oy);
            return true;
        }

        private void ApplyBoxesToTrajectory()
        {
            if (_primaryFrame == null || !TryReadCrops(out var start, out var end)) return;
            var canvas = new Size(SlideshowCanvas.ActualWidth, SlideshowCanvas.ActualHeight);
            var img = _primaryFrame.Intelligence.ImageSize;
            var s = CameraMath.CameraFromCrop(start, canvas, img);
            var e = CameraMath.CameraFromCrop(end, canvas, img);
            _primaryFrame.Trajectory.StartScale = s.Scale;
            _primaryFrame.Trajectory.StartPan = s.Pan;
            _primaryFrame.Trajectory.EndScale = e.Scale;
            _primaryFrame.Trajectory.EndPan = e.Pan;
            if (ViewModel.HasUnsavedZoomPan || ViewModel.CurrentDraft.StartCrop != null)
            {
                _primaryFrame.Trajectory.StrategyName = "Custom";
                _primaryFrame.Trajectory.Technique = "Custom";
            }
        }

        private void CommitKeyframeCrops(bool persist)
        {
            if (!TryReadCrops(out var start, out var end)) return;
            ApplyBoxesToTrajectory();
            if (persist)
            {
                ViewModel.SaveKeyframeCrops(
                    new NormalizedRect { X = start.X, Y = start.Y, Width = start.Width, Height = start.Height },
                    new NormalizedRect { X = end.X, Y = end.Y, Width = end.Width, Height = end.Height });
            }
            else
            {
                ViewModel.MarkZoomPanDraft();
            }
        }

        private void BeginBoxInteraction(Border box, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, bool resize, string corner)
        {
            SelectKeyframe(box == StartFrameBox);
            _isDraggingBox = !resize;
            _isResizing = resize;
            _resizeCorner = corner;
            _dragStartPoint = e.GetCurrentPoint(WysiwygOverlay).Position;
            _boxStartLeft = Canvas.GetLeft(box);
            _boxStartTop = Canvas.GetTop(box);
            _boxStartW = box.Width;
            _boxStartH = box.Height;
            WysiwygOverlay.CapturePointer(e.Pointer);
            ViewModel.CaptureUndoSnapshot();
            e.Handled = true;
        }

        private static bool BoxContains(Border box, Windows.Foundation.Point p)
        {
            double l = Canvas.GetLeft(box), t = Canvas.GetTop(box);
            return p.X >= l && p.X <= l + box.Width && p.Y >= t && p.Y <= t + box.Height;
        }

        private Border? SmallestBoxAt(Windows.Foundation.Point p)
        {
            bool inStart = StartFrameBox != null && BoxContains(StartFrameBox, p);
            bool inEnd = EndFrameBox != null && BoxContains(EndFrameBox, p);
            if (inStart && inEnd)
            {
                double sa = StartFrameBox!.Width * StartFrameBox.Height;
                double ea = EndFrameBox!.Width * EndFrameBox.Height;
                return sa <= ea ? StartFrameBox : EndFrameBox;
            }
            if (inEnd) return EndFrameBox;
            if (inStart) return StartFrameBox;
            return null;
        }

        private void WysiwygOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingBox || _isResizing) return;
            var box = SmallestBoxAt(e.GetCurrentPoint(WysiwygOverlay).Position);
            if (box == null) return;
            BeginBoxInteraction(box, e, resize: false, corner: "");
        }

        private void Handle_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement handle || SelectedBox() is not Border box) return;
            BeginBoxInteraction(box, e, resize: true, corner: handle.Tag as string ?? "br");
        }

        private void WysiwygOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if ((!_isDraggingBox && !_isResizing) || SelectedBox() is not Border box || _primaryFrame == null) return;
            if (WysiwygOverlay.ActualWidth <= 1) return;

            var now = e.GetCurrentPoint(WysiwygOverlay).Position;
            double dx = now.X - _dragStartPoint.X;
            double dy = now.Y - _dragStartPoint.Y;
            double aspect = OutputAspect();

            if (_isResizing)
            {
                double newW = _resizeCorner is "tl" or "bl" ? _boxStartW - dx : _boxStartW + dx;
                double newL = _resizeCorner is "tl" or "bl" ? _boxStartLeft + (_boxStartW - newW) : _boxStartLeft;
                double newH = newW / aspect;
                double newT = _resizeCorner is "tl" or "tr" ? _boxStartTop + _boxStartH - newH : _boxStartTop;
                ApplyBoxRect(box, newL, newT, newW, newH);
            }
            else
            {
                ApplyBoxRect(box, _boxStartLeft + dx, _boxStartTop + dy, _boxStartW, _boxStartH);
            }

            ApplyChrome();
            SlideshowCanvas.Invalidate();
        }

        private void WysiwygOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isDraggingBox && !_isResizing) return;
            _isDraggingBox = false;
            _isResizing = false;
            try { WysiwygOverlay.ReleasePointerCapture(e.Pointer); } catch { }
            CommitKeyframeCrops(persist: false);
            SlideshowCanvas.Invalidate();
            e.Handled = true;
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_primaryFrame == null) return;
            if (ViewModel.HasUnsavedZoomPan || ViewModel.IsOverrideMode)
                ApplyBoxesToTrajectory();
            _previewing = true;
            ApplyEditChrome();
            _slideStartTime = TimeSpan.Zero;
            if (!ViewModel.IsPlaying) ViewModel.TogglePlay();
            SlideshowCanvas.Invalidate();
        }

        private void BtnSaveZoomPan_Click(object sender, RoutedEventArgs e)
        {
            CommitKeyframeCrops(persist: true);
            SlideshowCanvas.Invalidate();
        }

        private void BtnClearTarget_Click(object sender, RoutedEventArgs e)
        {
            _previewing = false;
            if (ViewModel.IsPlaying) ViewModel.TogglePlay();
            DiscardPreload();
            ViewModel.RevertToAuto();
            if (_primaryFrame != null) _primaryFrame.Trajectory.Technique = "Auto";
            if (_primaryFrame != null && _primaryFrame.HasEngineCrops)
            {
                ApplyCropsToTrajectory(_primaryFrame.EngineStartCrop, _primaryFrame.EngineEndCrop);
                ResetKeyframeBoxes();
            }
            else
            {
                UpdateActiveTrajectoriesAsync(forceEngineReroll: true);
            }
            SlideshowCanvas.Invalidate();
        }

        private void PinChromeForDrag()
        {
            _pointerOverChrome = true;
            if (ActionBar != null) ActionBar.Opacity = 1.0;
            _idleTimer.Stop();
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            PinChromeForDrag();
            e.AcceptedOperation = SequenceBar.Visibility == Visibility.Visible && e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        private void RootGrid_Drop(object sender, DragEventArgs e)
        {
            if (SequenceBar.Visibility == Visibility.Visible)
                SequenceBar_Drop(sender, e);
        }

        private void SequenceStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SequenceStrip.SelectedIndex >= 0 && SequenceStrip.SelectedIndex != ViewModel.CurrentIndex)
                ViewModel.NavigateTo(SequenceStrip.SelectedIndex);
            ScrollSequenceToCurrent();
        }

        private void ScrollSequenceToCurrent()
        {
            if (SequenceBar == null || SequenceBar.Visibility != Visibility.Visible || SequenceStrip == null) return;
            int index = ViewModel.CurrentIndex;
            if (index < 0 || index >= ViewModel.Track.Count) return;

            int gen = ++_seqScrollGeneration;
            if (SequenceStrip.ContainerFromIndex(index) is FrameworkElement)
            {
                CenterSequenceItem(index);
                return;
            }
            SequenceStrip.ScrollIntoView(ViewModel.Track[index]);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _seqScrollGeneration) return;
                CenterSequenceItem(index);
            });
        }

        private void CenterSequenceItem(int index)
        {
            if (SequenceStrip.ContainerFromIndex(index) is not FrameworkElement container) return;
            var sv = FindScrollViewer(SequenceStrip);
            if (sv == null || sv.ActualWidth <= 1) return;
            var origin = container.TransformToVisual(sv).TransformPoint(new Windows.Foundation.Point(0, 0));
            double itemCenter = origin.X + container.ActualWidth / 2.0;
            double target = sv.HorizontalOffset + itemCenter - sv.ViewportWidth / 2.0;
            target = Math.Clamp(target, 0, Math.Max(0, sv.ExtentWidth - sv.ViewportWidth));
            sv.ChangeView(target, null, null, disableAnimation: true);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                var nested = FindScrollViewer(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void SequenceStrip_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ViewModel.NotifyTrackReordered();
            DiscardPreload();
        }

        private void BtnSeqNew_Click(object sender, RoutedEventArgs e)
        {
            DiscardPreload();
            ViewModel.ResetToLaunchSet();
        }

        private void BtnSeqHide_Click(object sender, RoutedEventArgs e) => ViewModel.HideCurrentClip();
        private void BtnSeqRemove_Click(object sender, RoutedEventArgs e)
        {
            DiscardPreload();
            ViewModel.RemoveCurrentClip();
        }
        private void BtnSeqDuplicate_Click(object sender, RoutedEventArgs e) => ViewModel.DuplicateCurrentClip();

        private async void BtnSeqAddFromFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");
            try
            {
                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                DiscardPreload();
                ViewModel.AddFromFolder(folder.Path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cinematic] Add from folder: {ex.Message}");
            }
        }

        private void SequenceBar_DragOver(object sender, DragEventArgs e)
        {
            PinChromeForDrag();
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.Handled = true;
        }

        private async void SequenceBar_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = new List<string>();
                foreach (var item in items)
                {
                    if (item is StorageFile file)
                        paths.Add(file.Path);
                    else if (item is StorageFolder folder)
                    {
                        foreach (var f in await folder.GetFilesAsync())
                            paths.Add(f.Path);
                    }
                }
                if (paths.Count > 0)
                {
                    DiscardPreload();
                    ViewModel.AddPaths(paths);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cinematic] Sequence drop: {ex.Message}");
            }
        }

        private static void ApplyPlaybarToggle(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton btn, bool on)
        {
            btn.Background = on
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var fg = on
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180));
            btn.Foreground = fg;
            if (btn.Content is SymbolIcon icon)
                icon.Foreground = fg;
        }
        private void BtnBack_Click(object sender, RoutedEventArgs e) => ViewModel.MovePrevious();
        private void BtnForward_Click(object sender, RoutedEventArgs e) => ViewModel.MoveNext();
        private void BtnUndo_Click(object sender, RoutedEventArgs e) => ViewModel.ExecuteUndo();

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                savePicker.FileTypeChoices.Add("Show", new List<string>() { ".json" });
                savePicker.SuggestedFileName = "My show";

                StorageFile file = await savePicker.PickSaveFileAsync();
                if (file != null) await ViewModel.ExportProjectAsync(file);
            }
            catch (Exception ex)
            {
                await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Could not save the show", ex.Message);
            }
        }

        private async void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openPicker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(openPicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                openPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                openPicker.FileTypeFilter.Add(".json");

                StorageFile file = await openPicker.PickSingleFileAsync();
                if (file == null) return;
                var (show, missing) = CinematicViewModel.ReadShow(await FileIO.ReadTextAsync(file));
                if (show == null)
                {
                    await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Could not open the show",
                        missing > 0 ? "None of its pictures are still on disk." : "This file is not a saved show.");
                    return;
                }
                DiscardPreload();
                ViewModel.ApplyShow(show);
                await ReportShowProblemsAsync(missing);
            }
            catch (Exception ex)
            {
                await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Could not open the show", ex.Message);
            }
        }

        private async Task ReportShowProblemsAsync(int missingPictures)
        {
            if (missingPictures <= 0 && !ViewModel.MusicWasMissing) return;
            var lines = new List<string>();
            if (missingPictures > 0) lines.Add(missingPictures == 1 ? "1 picture is no longer on disk, so the rest will play." : $"{missingPictures} pictures are no longer on disk, so the rest will play.");
            if (ViewModel.MusicWasMissing) lines.Add("Its music file is no longer on disk, so it will play without music.");
            await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Part of this show is missing", string.Join(" ", lines));
        }

    }
}

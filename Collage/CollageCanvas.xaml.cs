using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.Storage.Streams;

namespace ModernImageViewer.Collage
{
    public sealed partial class CollageCanvas : UserControl
    {
        public event EventHandler<CollageProject>? UserInteractionCompleted;
        private CollageProject? _preInteractionState;

        private CollageProject? _project;
        private CollageElement? _selectedElement;
        private float _currentGamma = 2.2f;

        public ObservableCollection<CollageElement> SelectedElements { get; } = new();
        public event EventHandler? SelectionChanged;

        // Localized GPU cache bound to this specific Canvas Control / UI Thread
        private readonly Dictionary<string, CanvasBitmap> _localGpuCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _inflightLoads = new(StringComparer.OrdinalIgnoreCase);

        // === High-Res on-demand support ===
        private readonly HashSet<string> _highResInflight = new(StringComparer.OrdinalIgnoreCase);
        private const float HighResZoomThreshold = 1.8f;

        private float _globalZoom = 1.0f;
        private double _viewOffsetX = 0;
        private double _viewOffsetY = 0;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomHideTimer;
        private Point _lastContextMenuPosition;

        private bool _isDragging = false;
        private List<CollageElement> _draggedElements = new();
        private Point _dragStartPointer;
        private Dictionary<CollageElement, Rect> _dragStartLayouts = new();

        private int _activeResizeHandle = -1;
        private CollageElement? _resizeElement;
        private Rect _resizeStartLayout;

        private bool _isContentDragging = false;
        private CollageElement? _contentDragElement;
        private double _contentDragStartPanX;
        private double _contentDragStartPanY;

        private bool _isMarqueeSelecting = false;
        private Point _marqueeStartPoint;
        private Rect _marqueeRect;

        private bool _isPanning = false;
        private Point _panStartPoint;

        private int _canvasResizeEdge = 0;
        private Rect _canvasResizeStartLayout;

        public bool IsSnappingEnabled { get; set; } = true;
        public bool ShowGrid { get; set; } = false;
        private const float GridSpacing = 80f;
        private const double SnapThreshold = 8.0;
        private List<Rect> _activeSnapGuides = new();

        private const double BaseHandleSize = 10;
        private const double MinCellSize = 50;

        public CollageCanvas()
        {
            this.InitializeComponent();
            this.Unloaded += CollageCanvas_Unloaded;

            CollageCanvasControl.RightTapped += CollageCanvasControl_RightTapped;
            CollageCanvasControl.KeyDown += CollageCanvasControl_KeyDown;
            CollageCanvasControl.IsTabStop = true;

            CollageCanvasControl.PointerCanceled += CollageCanvasControl_PointerCaptureLost;
            CollageCanvasControl.PointerCaptureLost += CollageCanvasControl_PointerCaptureLost;
            CollageCanvasControl.CreateResources += CollageCanvasControl_CreateResources;
        }

        private void CollageCanvas_Unloaded(object sender, RoutedEventArgs e)
        {
            ReleaseLocalGpuResources();
        }

        private void CollageCanvasControl_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            if (args.Reason == Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice)
            {
                CollageCellRenderer.ResetSharedEffects();
                ReleaseLocalGpuResources();

                if (_project != null)
                {
                    foreach (var el in _project.Elements)
                        el.InvalidateClipGeometry();
                }

                Invalidate();
            }
        }

        public CollageProject? Project
        {
            get => _project;
            set
            {
                if (_project != value)
                {
                    ReleaseLocalGpuResources();
                    _project = value;
                    ClearSelection();
                    _viewOffsetX = 0;
                    _viewOffsetY = 0;
                    _globalZoom = 1.0f;
                    Invalidate();
                }
            }
        }

        public float Gamma
        {
            get => _currentGamma;
            set
            {
                if (Math.Abs(_currentGamma - value) > 0.01f)
                {
                    _currentGamma = value;
                    Invalidate();
                }
            }
        }

        public CollageElement? SelectedElement
        {
            get => _selectedElement;
            set
            {
                if (_selectedElement != value)
                {
                    _selectedElement = value;
                    Invalidate();
                }
            }
        }

        public void Invalidate()
        {
            CollageCanvasControl?.Invalidate();
        }

        public void ResetViewTo100Percent()
        {
            _globalZoom = 1.0f;
            _viewOffsetX = 0;
            _viewOffsetY = 0;
            Invalidate();
        }

        private void ClearSelection()
        {
            SelectedElements.Clear();
            _selectedElement = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        private void AddToSelection(CollageElement element)
        {
            if (!SelectedElements.Contains(element))
            {
                SelectedElements.Add(element);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            _selectedElement = element;
            Invalidate();
        }

        private void RemoveFromSelection(CollageElement element)
        {
            SelectedElements.Remove(element);
            if (_selectedElement == element)
            {
                _selectedElement = SelectedElements.LastOrDefault();
            }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        private void ReleaseLocalGpuResources()
        {
            foreach (var bitmap in _localGpuCache.Values)
            {
                bitmap?.Dispose();
            }
            _localGpuCache.Clear();

            // Also release any high-res bitmaps on elements
            if (_project != null)
            {
                foreach (var el in _project.Elements)
                {
                    el.ReleaseHighResBitmap();
                }
            }
        }

        private async Task ReloadImageAsync(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var entry = await ViewerEngine.DecodeFastPreviewAsync(path);
                if (entry?.Bitmap != null && CollageCanvasControl.ReadyToDraw)
                {
                    var localGpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(CollageCanvasControl.Device, entry.Bitmap);

                    _localGpuCache[path] = localGpuBitmap;

                    entry.Bitmap.Dispose();
                    entry.Bitmap = null;
                    App.GlobalImageCache[path] = entry;

                    Invalidate();
                }
            }
            catch { }
            finally
            {
                _inflightLoads.Remove(path);
            }
        }
        private async Task LoadHighResAsync(CollageElement element)
        {
            if (element == null || string.IsNullOrEmpty(element.ImagePath) || element.HasHighRes || element.IsHighResLoading)
                return;

            if (_highResInflight.Contains(element.ImagePath))
                return;

            _highResInflight.Add(element.ImagePath);
            element.IsHighResLoading = true;

            try
            {
                if (!CollageCanvasControl.ReadyToDraw) return;

                App.GlobalImageCache.TryGetValue(element.ImagePath, out var entry);
                CanvasBitmap highResBitmap = null;

                if (entry?.Profile != null)
                {
                    // Improved path: Use WIC for profiled images to preserve more color accuracy
                    highResBitmap = await LoadHighResWithColorManagementAsync(element.ImagePath);
                }
                else
                {
                    // Fast path for standard images
                    highResBitmap = await CanvasBitmap.LoadAsync(CollageCanvasControl.Device, element.ImagePath);
                }

                if (element != null && _project?.Elements.Contains(element) == true)
                {
                    element.HighResBitmap?.Dispose();
                    element.HighResBitmap = highResBitmap;
                    Invalidate();
                }
                else
                {
                    highResBitmap?.Dispose();
                }
            }
            catch
            {
                // Fallback to simple load
                try
                {
                    var fallback = await CanvasBitmap.LoadAsync(CollageCanvasControl.Device, element.ImagePath);
                    if (element != null && _project?.Elements.Contains(element) == true)
                    {
                        element.HighResBitmap?.Dispose();
                        element.HighResBitmap = fallback;
                        Invalidate();
                    }
                }
                catch { }
            }
            finally
            {
                if (element != null) element.IsHighResLoading = false;
                _highResInflight.Remove(element?.ImagePath ?? string.Empty);
            }
        }

        private async Task<CanvasBitmap> LoadHighResWithColorManagementAsync(string path)
        {
            if (!CollageCanvasControl.ReadyToDraw)
                return await CanvasBitmap.LoadAsync(CollageCanvasControl.Device, path);

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();

                var decoder = await BitmapDecoder.CreateAsync(stream);
                var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                try
                {
                    return CanvasBitmap.CreateFromSoftwareBitmap(
                        CollageCanvasControl.Device, softwareBitmap);
                }
                finally
                {
                    softwareBitmap.Dispose();
                }
            }
            catch
            {
                return await CanvasBitmap.LoadAsync(CollageCanvasControl.Device, path);
            }
        }
        private System.Numerics.Matrix3x2 GetRenderMatrix()
        {
            float dipScale = XamlRoot != null ? (float)XamlRoot.RasterizationScale : 1.0f;
            return System.Numerics.Matrix3x2.CreateScale(_globalZoom)
                 * System.Numerics.Matrix3x2.CreateTranslation((float)_viewOffsetX, (float)_viewOffsetY)
                 * System.Numerics.Matrix3x2.CreateScale(1.0f / dipScale);
        }

        private Point GetModelCoordinate(Point viewPoint)
        {
            if (System.Numerics.Matrix3x2.Invert(GetRenderMatrix(), out var inverseMatrix))
            {
                var modelPoint = System.Numerics.Vector2.Transform(new System.Numerics.Vector2((float)viewPoint.X, (float)viewPoint.Y), inverseMatrix);
                return new Point(modelPoint.X, modelPoint.Y);
            }
            return new Point(0, 0);
        }

        private void ShowZoomIndicator()
        {
            if (ZoomIndicatorContainer == null || ZoomIndicator == null) return;

            ZoomIndicator.Text = $"{_globalZoom * 100:0}%";
            ZoomIndicatorContainer.Opacity = 1.0;

            _zoomHideTimer?.Stop();

            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _zoomHideTimer = dq.CreateTimer();
            _zoomHideTimer.Interval = TimeSpan.FromMilliseconds(1600);

            _zoomHideTimer.Tick += (s, e) =>
            {
                if (ZoomIndicatorContainer != null) ZoomIndicatorContainer.Opacity = 0;
                _zoomHideTimer?.Stop();
            };
            _zoomHideTimer.Start();
        }

        private void CollageCanvasControl_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var rawPos = e.GetPosition(CollageCanvasControl);
            _lastContextMenuPosition = rawPos;

            var modelPos = GetModelCoordinate(rawPos);
            var hitCell = HitTestCell(modelPos);

            if (hitCell != null)
            {
                if (!SelectedElements.Contains(hitCell))
                {
                    ClearSelection();
                    AddToSelection(hitCell);
                }
                ShowContextMenu(rawPos);
            }
        }

        private void ShowContextMenu(Point position)
        {
            if (_project == null || SelectedElements.Count == 0) return;

            var flyout = new MenuFlyout();

            bool isAnyLocked = SelectedElements.Any(e => e.IsLocked);
            bool isAnyAnchored = SelectedElements.Any(e => e.IsAnchored);
            bool isAnyContentLocked = SelectedElements.Any(e => e.IsContentLocked);

            var rotateItem = new MenuFlyoutItem { Text = "Rotate 45° Clockwise", IsEnabled = !isAnyLocked && !isAnyContentLocked };
            rotateItem.Click += RotateSelected_Click;
            flyout.Items.Add(rotateItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var duplicateItem = new MenuFlyoutItem { Text = "Duplicate Selected" };
            duplicateItem.Click += DuplicateSelected_Click;
            flyout.Items.Add(duplicateItem);

            if (SelectedElements.Count == 1 && !string.IsNullOrEmpty(SelectedElements[0].ImagePath))
            {
                bool isLocked = SelectedElements[0].IsLocked;
                bool isContentLocked = SelectedElements[0].IsContentLocked;

                var fitItem = new MenuFlyoutItem { Text = "Fit to Cell", IsEnabled = !isLocked && !isContentLocked };
                fitItem.Click += FitToCell_Click;
                flyout.Items.Add(fitItem);

                var fillItem = new MenuFlyoutItem { Text = "Fill to Cell", IsEnabled = !isLocked && !isContentLocked };
                fillItem.Click += FillToCell_Click;
                flyout.Items.Add(fillItem);

                var fitBoxItem = new MenuFlyoutItem { Text = "Fit Box to Image", IsEnabled = !isLocked && !SelectedElements[0].IsAnchored && !isContentLocked };
                fitBoxItem.Click += FitBoxToImage_Click;
                flyout.Items.Add(fitBoxItem);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            if (SelectedElements.Count > 1)
            {
                var alignLeft = new MenuFlyoutItem { Text = "Align Left" };
                alignLeft.Click += (s, args) => AlignSelected(AlignmentType.Left);
                flyout.Items.Add(alignLeft);

                var alignRight = new MenuFlyoutItem { Text = "Align Right" };
                alignRight.Click += (s, args) => AlignSelected(AlignmentType.Right);
                flyout.Items.Add(alignRight);

                var alignTop = new MenuFlyoutItem { Text = "Align Top" };
                alignTop.Click += (s, args) => AlignSelected(AlignmentType.Top);
                flyout.Items.Add(alignTop);

                var alignBottom = new MenuFlyoutItem { Text = "Align Bottom" };
                alignBottom.Click += (s, args) => AlignSelected(AlignmentType.Bottom);
                flyout.Items.Add(alignBottom);

                if (SelectedElements.Count > 2 && _project != null)
                {
                    flyout.Items.Add(new MenuFlyoutSeparator());
                    var distH = new MenuFlyoutItem { Text = "Distribute Horizontally" };
                    distH.Click += (s, args) => { _project.DistributeElements(SelectedElements.ToList(), true); Invalidate(); };
                    flyout.Items.Add(distH);

                    var distV = new MenuFlyoutItem { Text = "Distribute Vertically" };
                    distV.Click += (s, args) => { _project.DistributeElements(SelectedElements.ToList(), false); Invalidate(); };
                    flyout.Items.Add(distV);
                }
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            var bringFront = new MenuFlyoutItem { Text = "Bring to Front" };
            bringFront.Click += BringToFront_Click;
            flyout.Items.Add(bringFront);

            var bringForward = new MenuFlyoutItem { Text = "Bring Forward" };
            bringForward.Click += BringForward_Click;
            flyout.Items.Add(bringForward);

            var sendBackward = new MenuFlyoutItem { Text = "Send Backward" };
            sendBackward.Click += SendBackward_Click;
            flyout.Items.Add(sendBackward);

            var sendBack = new MenuFlyoutItem { Text = "Send to Back" };
            sendBack.Click += SendToBack_Click;
            flyout.Items.Add(sendBack);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var lockItem = new ToggleMenuFlyoutItem { Text = "Locked", IsChecked = isAnyLocked };
            lockItem.Click += ToggleLock_Click;
            flyout.Items.Add(lockItem);

            var anchorItem = new ToggleMenuFlyoutItem { Text = "Anchored", IsChecked = isAnyAnchored };
            anchorItem.Click += ToggleAnchor_Click;
            flyout.Items.Add(anchorItem);

            var contentLockItem = new ToggleMenuFlyoutItem { Text = "Content Lock", IsChecked = isAnyContentLocked };
            contentLockItem.Click += ToggleContentLock_Click;
            flyout.Items.Add(contentLockItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var removeItem = new MenuFlyoutItem { Text = "Remove Selected Images" };
            removeItem.Click += RemoveSelectedCells_Click;
            flyout.Items.Add(removeItem);

            flyout.ShowAt(CollageCanvasControl, position);
        }

        private void BringToFront_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedElement != null && _project != null)
            {
                int index = _project.Elements.IndexOf(_selectedElement);
                if (index < _project.Elements.Count - 1)
                {
                    _project.Elements.Move(index, _project.Elements.Count - 1);
                    Invalidate();
                }
            }
        }

        private void BringForward_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedElement != null && _project != null)
            {
                int index = _project.Elements.IndexOf(_selectedElement);
                if (index < _project.Elements.Count - 1)
                {
                    _project.Elements.Move(index, index + 1);
                    Invalidate();
                }
            }
            ShowContextMenu(_lastContextMenuPosition);
        }

        private void SendBackward_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedElement != null && _project != null)
            {
                int index = _project.Elements.IndexOf(_selectedElement);
                if (index > 0)
                {
                    _project.Elements.Move(index, index - 1);
                    Invalidate();
                }
            }
            ShowContextMenu(_lastContextMenuPosition);
        }

        private void SendToBack_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedElement != null && _project != null)
            {
                int index = _project.Elements.IndexOf(_selectedElement);
                if (index > 0)
                {
                    _project.Elements.Move(index, 0);
                    Invalidate();
                }
            }
        }

        private void RotateSelected_Click(object sender, RoutedEventArgs e)
        {
            foreach (var el in SelectedElements.Where(el => !el.IsLocked && !el.IsContentLocked))
                el.Rotation = (el.Rotation + 45) % 360;
            Invalidate();
        }

        private void DuplicateSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;

            var newSelection = new List<CollageElement>();
            foreach (var el in SelectedElements.ToList())
            {
                var clone = el.Clone();
                clone.Layout = new Rect(el.Layout.X + 40, el.Layout.Y + 40, el.Layout.Width, el.Layout.Height);
                _project.Elements.Add(clone);
                newSelection.Add(clone);
            }

            ClearSelection();
            foreach (var ne in newSelection) AddToSelection(ne);
            Invalidate();
        }

        private void FitToCell_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedElements.Count != 1) return;
            var el = SelectedElements[0];
            if (el.IsLocked || el.IsContentLocked) return;

            if (string.IsNullOrEmpty(el.ImagePath)) return;

            if (!App.GlobalImageCache.TryGetValue(el.ImagePath, out var entry) || entry == null)
            {
                _ = ReloadImageAsync(el.ImagePath);
                return;
            }

            _localGpuCache.TryGetValue(el.ImagePath, out var localGpuBitmap);

            double w = entry.NativeWidth > 0 ? entry.NativeWidth : (localGpuBitmap?.SizeInPixels.Width ?? 1);
            double h = entry.NativeHeight > 0 ? entry.NativeHeight : (localGpuBitmap?.SizeInPixels.Height ?? 1);

            el.FitToCell(el.Layout.Width, el.Layout.Height, w, h);
            Invalidate();
        }

        private void FillToCell_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedElements.Count != 1) return;
            var el = SelectedElements[0];
            if (el.IsLocked || el.IsContentLocked) return;

            if (string.IsNullOrEmpty(el.ImagePath)) return;

            if (!App.GlobalImageCache.TryGetValue(el.ImagePath, out var entry) || entry == null)
            {
                _ = ReloadImageAsync(el.ImagePath);
                return;
            }

            _localGpuCache.TryGetValue(el.ImagePath, out var localGpuBitmap);

            double w = entry.NativeWidth > 0 ? entry.NativeWidth : (localGpuBitmap?.SizeInPixels.Width ?? 1);
            double h = entry.NativeHeight > 0 ? entry.NativeHeight : (localGpuBitmap?.SizeInPixels.Height ?? 1);

            el.FillToCell(w, h);
            Invalidate();
        }

        private void FitBoxToImage_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedElements.Count != 1) return;
            var el = SelectedElements[0];
            if (el.IsLocked || el.IsAnchored || el.IsContentLocked) return;

            if (string.IsNullOrEmpty(el.ImagePath)) return;

            if (!App.GlobalImageCache.TryGetValue(el.ImagePath, out var entry) || entry == null || !_localGpuCache.TryGetValue(el.ImagePath, out var localGpuBitmap) || localGpuBitmap == null)
            {
                _ = ReloadImageAsync(el.ImagePath);
                return;
            }

            double logicalW = entry.NativeWidth > 0 ? entry.NativeWidth : localGpuBitmap.SizeInPixels.Width;
            double logicalH = entry.NativeHeight > 0 ? entry.NativeHeight : localGpuBitmap.SizeInPixels.Height;

            double scaledW = logicalW * el.Zoom;
            double scaledH = logicalH * el.Zoom;

            double currentDrawX = el.Layout.X;
            double currentDrawY = el.Layout.Y;

            if (scaledW < el.Layout.Width + 1.0)
                currentDrawX += (el.Layout.Width - scaledW) / 2.0;

            if (scaledH < el.Layout.Height + 1.0)
                currentDrawY += (el.Layout.Height - scaledH) / 2.0;

            currentDrawX += el.PanX * el.Zoom;
            currentDrawY += el.PanY * el.Zoom;

            el.Layout = new Rect(currentDrawX, currentDrawY, scaledW, scaledH);
            el.PanX = 0;
            el.PanY = 0;

            Invalidate();
        }

        private void ToggleLock_Click(object sender, RoutedEventArgs e)
        {
            bool enable = !SelectedElements.All(el => el.IsLocked);
            foreach (var el in SelectedElements)
            {
                el.IsLocked = enable;
                if (enable)
                {
                    el.IsAnchored = false;
                    el.IsContentLocked = false;
                }
            }
            Invalidate();
        }

        private void ToggleAnchor_Click(object sender, RoutedEventArgs e)
        {
            bool enable = !SelectedElements.All(el => el.IsAnchored);
            foreach (var el in SelectedElements)
            {
                el.IsAnchored = enable;
                if (enable)
                {
                    el.IsLocked = false;
                    el.IsContentLocked = false;
                }
            }
            Invalidate();
        }

        private void ToggleContentLock_Click(object sender, RoutedEventArgs e)
        {
            bool enable = !SelectedElements.All(el => el.IsContentLocked);
            foreach (var el in SelectedElements)
            {
                el.IsContentLocked = enable;
                if (enable)
                {
                    el.IsLocked = false;
                    el.IsAnchored = false;
                }
            }
            Invalidate();
        }

        private void RemoveSelectedCells_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;

            foreach (var el in SelectedElements.Where(e => !e.IsLocked && !e.IsAnchored).ToList())
            {
                if (!string.IsNullOrEmpty(el.ImagePath))
                {
                    if (_localGpuCache.TryGetValue(el.ImagePath, out var bmp))
                    {
                        bmp?.Dispose();
                        _localGpuCache.Remove(el.ImagePath);
                    }
                    el.ReleaseHighResBitmap();
                }
                _project.Elements.Remove(el);
            }

            ClearSelection();
            Invalidate();
        }

        private enum AlignmentType { Left, Right, Top, Bottom }

        private void AlignSelected(AlignmentType type)
        {
            var active = SelectedElements.Where(e => !e.IsLocked && !e.IsAnchored).ToList();
            if (active.Count < 2) return;

            double target = type switch
            {
                AlignmentType.Left => active.Min(e => e.Layout.Left),
                AlignmentType.Right => active.Max(e => e.Layout.Right),
                AlignmentType.Top => active.Min(e => e.Layout.Top),
                AlignmentType.Bottom => active.Max(e => e.Layout.Bottom),
                _ => 0
            };

            foreach (var el in active)
            {
                var layout = el.Layout;
                switch (type)
                {
                    case AlignmentType.Left: el.Layout = new Rect(target, layout.Top, layout.Width, layout.Height); break;
                    case AlignmentType.Right: el.Layout = new Rect(target - layout.Width, layout.Top, layout.Width, layout.Height); break;
                    case AlignmentType.Top: el.Layout = new Rect(layout.Left, target, layout.Width, layout.Height); break;
                    case AlignmentType.Bottom: el.Layout = new Rect(layout.Left, target - layout.Height, layout.Width, layout.Height); break;
                }
            }
            Invalidate();
        }

        private void CollageCanvasControl_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Delete && SelectedElements.Count > 0 && _project != null)
            {
                foreach (var el in SelectedElements.Where(el => !el.IsLocked && !el.IsAnchored).ToList())
                    _project.Elements.Remove(el);
                ClearSelection();
                Invalidate();
                e.Handled = true;
            }
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                e.AcceptedOperation = DataPackageOperation.Copy;
        }

        private async void RootGrid_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();
            var rawDropPosition = e.GetPosition(CollageCanvasControl);
            var modelDropPosition = GetModelCoordinate(rawDropPosition);

            CollageElement? lastAdded = null;

            foreach (var item in items)
            {
                if (item is StorageFile file)
                {
                    lastAdded = await AddDroppedImageAsync(file.Path, modelDropPosition);
                    modelDropPosition.X += 40;
                    modelDropPosition.Y += 40;
                }
            }

            if (lastAdded != null)
            {
                ClearSelection();
                AddToSelection(lastAdded);
                Invalidate();
            }
        }

        private async Task<CollageElement?> AddDroppedImageAsync(string path, Point modelDropPosition)
        {
            if (_project == null) return null;

            ImageCacheEntry? entry = null;
            if (!App.GlobalImageCache.TryGetValue(path, out entry))
            {
                try
                {
                    entry = await ViewerEngine.DecodeFastPreviewAsync(path);
                    if (entry?.Bitmap != null && CollageCanvasControl.ReadyToDraw)
                    {
                        var localGpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(CollageCanvasControl.Device, entry.Bitmap);
                        _localGpuCache[path] = localGpuBitmap;

                        entry.Bitmap.Dispose();
                        entry.Bitmap = null;
                        App.GlobalImageCache[path] = entry;
                    }
                }
                catch { }
            }

            var newCell = new CollageElement
            {
                ImagePath = path,
                Layout = new Rect(modelDropPosition.X, modelDropPosition.Y, 420, 320)
            };

            _project.Elements.Add(newCell);
            return newCell;
        }

        private void CollageCanvasControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var rawPos = e.GetCurrentPoint(CollageCanvasControl).Position;
            var modelPos = GetModelCoordinate(rawPos);
            var properties = e.GetCurrentPoint(CollageCanvasControl).Properties;

            if (_project != null && !_isDragging && !_isContentDragging && _activeResizeHandle == -1)
            {
                _preInteractionState = _project.DeepClone();
            }

            bool shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (properties.IsMiddleButtonPressed)
            {
                _isPanning = true;
                _panStartPoint = rawPos;
                try { CollageCanvasControl.CapturePointer(e.Pointer); } catch { }
                e.Handled = true;
                return;
            }

            if (_project != null && HitTestCanvasEdge(modelPos) > 0)
            {
                _canvasResizeEdge = HitTestCanvasEdge(modelPos);
                _dragStartPointer = modelPos;
                _canvasResizeStartLayout = new Rect(0, 0, _project.CanvasWidth, _project.CanvasHeight);
                try { CollageCanvasControl.CapturePointer(e.Pointer); } catch { }
                e.Handled = true;
                return;
            }

            if (_selectedElement != null && !_selectedElement.IsAnchored && !_selectedElement.IsLocked && !_selectedElement.IsContentLocked)
            {
                int handle = HitTestResizeHandle(_selectedElement, modelPos);
                if (handle != -1)
                {
                    _activeResizeHandle = handle;
                    _resizeElement = _selectedElement;
                    _resizeStartLayout = _selectedElement.Layout;
                    _dragStartPointer = modelPos;
                    try { CollageCanvasControl.CapturePointer(e.Pointer); } catch { }
                    e.Handled = true;
                    return;
                }
            }

            var hitCell = HitTestCell(modelPos);

            if (hitCell != null)
            {
                bool ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (shiftDown)
                {
                    if (SelectedElements.Contains(hitCell)) RemoveFromSelection(hitCell);
                    else AddToSelection(hitCell);
                }
                else if (ctrlDown && hitCell == _selectedElement && !hitCell.IsLocked && !hitCell.IsContentLocked)
                {
                    _isContentDragging = true;
                    _contentDragElement = hitCell;
                    _dragStartPointer = rawPos;
                    _contentDragStartPanX = hitCell.PanX;
                    _contentDragStartPanY = hitCell.PanY;
                    try { CollageCanvasControl.CapturePointer(e.Pointer); } catch { }
                }
                else
                {
                    if (!SelectedElements.Contains(hitCell))
                    {
                        ClearSelection();
                        AddToSelection(hitCell);
                    }

                    if (SelectedElements.Count > 1)
                    {
                        _isDragging = true;
                        _draggedElements = SelectedElements.Where(el => !el.IsAnchored && !el.IsLocked).ToList();
                        _dragStartPointer = modelPos;
                        _dragStartLayouts.Clear();
                        foreach (var el in _draggedElements) _dragStartLayouts[el] = el.Layout;
                    }
                    else if (SelectedElements.Count == 1 && !hitCell.IsAnchored && !hitCell.IsLocked)
                    {
                        _isDragging = true;
                        _draggedElements.Clear();
                        _draggedElements.Add(hitCell);
                        _dragStartPointer = modelPos;
                        _dragStartLayouts.Clear();
                        _dragStartLayouts[hitCell] = hitCell.Layout;
                    }

                    try { CollageCanvasControl.CapturePointer(e.Pointer); } catch { }
                }
            }
            else
            {
                ClearSelection();
                _isMarqueeSelecting = true;
                _marqueeStartPoint = modelPos;
                _marqueeRect = new Rect();
                try { CollageCanvasControl.CapturePointer(e.Pointer); } catch { }
            }

            e.Handled = true;
        }

        private int HitTestCanvasEdge(Point modelPos)
        {
            if (_project == null) return 0;
            double hitTolerance = 30.0 / _globalZoom;

            bool onRight = Math.Abs(modelPos.X - _project.CanvasWidth) <= hitTolerance && modelPos.Y >= 0 && modelPos.Y <= _project.CanvasHeight + hitTolerance;
            bool onBottom = Math.Abs(modelPos.Y - _project.CanvasHeight) <= hitTolerance && modelPos.X >= 0 && modelPos.X <= _project.CanvasWidth + hitTolerance;

            if (onRight && onBottom) return 3;
            if (onRight) return 1;
            if (onBottom) return 2;
            return 0;
        }

        private void CollageCanvasControl_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var rawPos = e.GetCurrentPoint(CollageCanvasControl).Position;
            var modelPos = GetModelCoordinate(rawPos);
            float dipScale = XamlRoot != null ? (float)XamlRoot.RasterizationScale : 1.0f;

            if (_isPanning)
            {
                double dx = rawPos.X - _panStartPoint.X;
                double dy = rawPos.Y - _panStartPoint.Y;
                _viewOffsetX += dx * dipScale;
                _viewOffsetY += dy * dipScale;
                _panStartPoint = rawPos;
                Invalidate();
                e.Handled = true;
                return;
            }

            if (_canvasResizeEdge > 0 && _project != null)
            {
                double dx = modelPos.X - _dragStartPointer.X;
                double dy = modelPos.Y - _dragStartPointer.Y;

                if (_canvasResizeEdge == 1 || _canvasResizeEdge == 3)
                    _project.CanvasWidth = Math.Max(100, _canvasResizeStartLayout.Width + dx);
                if (_canvasResizeEdge == 2 || _canvasResizeEdge == 3)
                    _project.CanvasHeight = Math.Max(100, _canvasResizeStartLayout.Height + dy);

                Invalidate();
                e.Handled = true;
                return;
            }

            if (_isMarqueeSelecting)
            {
                double x = Math.Min(_marqueeStartPoint.X, modelPos.X);
                double y = Math.Min(_marqueeStartPoint.Y, modelPos.Y);
                _marqueeRect = new Rect(x, y, Math.Abs(modelPos.X - _marqueeStartPoint.X), Math.Abs(modelPos.Y - _marqueeStartPoint.Y));
                Invalidate();
                e.Handled = true;
                return;
            }

            if (_isContentDragging && _contentDragElement != null)
            {
                double dx = rawPos.X - _dragStartPointer.X;
                double dy = rawPos.Y - _dragStartPointer.Y;
                double sensitivity = dipScale / (_contentDragElement.Zoom * _globalZoom);

                _contentDragElement.PanX = _contentDragStartPanX + dx * sensitivity;
                _contentDragElement.PanY = _contentDragStartPanY + dy * sensitivity;
                Invalidate();
                e.Handled = true;
                return;
            }

            bool ctrlHeld = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            _activeSnapGuides.Clear();

            if (_activeResizeHandle != -1 && _resizeElement != null)
            {
                double dx = modelPos.X - _dragStartPointer.X;
                double dy = modelPos.Y - _dragStartPointer.Y;
                var newLayout = CalculateResizedLayout(_resizeStartLayout, _activeResizeHandle, dx, dy);
                if (IsSnappingEnabled && !ctrlHeld)
                    newLayout = ApplyResizeSnapping(newLayout, _activeResizeHandle);
                _resizeElement.Layout = newLayout;
                Invalidate();
                e.Handled = true;
                return;
            }

            if (!_isDragging || _draggedElements.Count == 0) return;

            double dxMove = modelPos.X - _dragStartPointer.X;
            double dyMove = modelPos.Y - _dragStartPointer.Y;

            var primary = _draggedElements[0];
            if (_dragStartLayouts.TryGetValue(primary, out var startLayout))
            {
                double proposedX = startLayout.X + dxMove;
                double proposedY = startLayout.Y + dyMove;

                var (snappedX, snappedY, guides) = GetSnappedPosition(primary, proposedX, proposedY, ctrlHeld);
                _activeSnapGuides = guides;

                double finalDx = snappedX - startLayout.X;
                double finalDy = snappedY - startLayout.Y;

                foreach (var el in _draggedElements)
                {
                    if (_dragStartLayouts.TryGetValue(el, out var sl))
                        el.Layout = new Rect(sl.X + finalDx, sl.Y + finalDy, sl.Width, sl.Height);
                }
            }

            Invalidate();
            e.Handled = true;
        }

        private void CollageCanvasControl_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isMarqueeSelecting && _project != null)
            {
                foreach (var element in _project.Elements)
                {
                    if (RectsIntersect(_marqueeRect, element.Layout))
                        AddToSelection(element);
                }
            }

            CollageCanvasControl_PointerCaptureLost(sender, e);
            try { CollageCanvasControl.ReleasePointerCapture(e.Pointer); } catch { }
            e.Handled = true;
        }

        private void CollageCanvasControl_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if ((_isDragging || _isContentDragging || _activeResizeHandle != -1 || _canvasResizeEdge > 0) && _preInteractionState != null)
            {
                UserInteractionCompleted?.Invoke(this, _preInteractionState);
            }
            _preInteractionState = null;

            _isPanning = false;
            _isMarqueeSelecting = false;
            _isDragging = false;
            _isContentDragging = false;
            _activeResizeHandle = -1;
            _resizeElement = null;
            _contentDragElement = null;
            _canvasResizeEdge = 0;

            _draggedElements.Clear();
            _dragStartLayouts.Clear();
            _activeSnapGuides.Clear();
            _marqueeRect = new Rect();

            Invalidate();
        }

        private void CollageCanvasControl_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pointerPoint = e.GetCurrentPoint(CollageCanvasControl);
            int delta = pointerPoint.Properties.MouseWheelDelta;

            if (_selectedElement != null && !_selectedElement.IsLocked && !_selectedElement.IsContentLocked)
            {
                float factor = delta > 0 ? 1.05f : 1f / 1.05f;
                _selectedElement.Zoom = Math.Clamp(_selectedElement.Zoom * factor, 0.05f, 50f);
                Invalidate();
                e.Handled = true;
                return;
            }

            double viewX = pointerPoint.Position.X;
            double viewY = pointerPoint.Position.Y;
            float dipScale = XamlRoot != null ? (float)XamlRoot.RasterizationScale : 1.0f;

            float zoomDelta = delta > 0 ? 1.1f : 1f / 1.1f;
            float newZoom = Math.Clamp(_globalZoom * zoomDelta, 0.05f, 50f);
            if (newZoom == _globalZoom) return;

            System.Numerics.Matrix3x2.Invert(GetRenderMatrix(), out var inverse);
            var oldModel = System.Numerics.Vector2.Transform(new System.Numerics.Vector2((float)viewX, (float)viewY), inverse);

            _globalZoom = newZoom;
            _viewOffsetX = (viewX * dipScale) - (oldModel.X * _globalZoom);
            _viewOffsetY = (viewY * dipScale) - (oldModel.Y * _globalZoom);

            Invalidate();
            ShowZoomIndicator();
            e.Handled = true;
        }

        private void CollageCanvasControl_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var rawPos = e.GetPosition(CollageCanvasControl);
            var modelPos = GetModelCoordinate(rawPos);
            if (HitTestCell(modelPos) == null)
            {
                ResetViewTo100Percent();
                ShowZoomIndicator();
                e.Handled = true;
            }
        }

        private Rect CalculateResizedLayout(Rect start, int handle, double dx, double dy)
        {
            double left = start.Left;
            double top = start.Top;
            double right = start.Right;
            double bottom = start.Bottom;

            switch (handle)
            {
                case 0: left += dx; top += dy; break;
                case 1: top += dy; break;
                case 2: right += dx; top += dy; break;
                case 3: right += dx; break;
                case 4: right += dx; bottom += dy; break;
                case 5: bottom += dy; break;
                case 6: left += dx; bottom += dy; break;
                case 7: left += dx; break;
            }

            double w = Math.Max(MinCellSize, right - left);
            double h = Math.Max(MinCellSize, bottom - top);

            if (handle == 0 || handle == 6 || handle == 7) left = right - w;
            if (handle == 0 || handle == 1 || handle == 2) top = bottom - h;

            return new Rect(left, top, w, h);
        }

        private Rect ApplyResizeSnapping(Rect proposed, int handle)
        {
            if (_project == null || _resizeElement == null) return proposed;

            double left = proposed.Left;
            double top = proposed.Top;
            double right = proposed.Right;
            double bottom = proposed.Bottom;
            double threshold = SnapThreshold;
            double canvasW = _project.CanvasWidth;
            double canvasH = _project.CanvasHeight;

            foreach (var other in _project.Elements)
            {
                if (other == _resizeElement || SelectedElements.Contains(other)) continue;
                var o = other.Layout;

                if (handle == 0 || handle == 7 || handle == 6)
                {
                    if (Math.Abs(left - o.Right) < threshold) left = o.Right;
                    if (Math.Abs(left - o.Left) < threshold) left = o.Left;
                }

                if (handle == 2 || handle == 3 || handle == 4)
                {
                    if (Math.Abs(right - o.Left) < threshold) right = o.Left;
                    if (Math.Abs(right - o.Right) < threshold) right = o.Right;
                }

                if (handle == 0 || handle == 1 || handle == 2)
                {
                    if (Math.Abs(top - o.Bottom) < threshold) top = o.Bottom;
                    if (Math.Abs(top - o.Top) < threshold) top = o.Top;
                }

                if (handle == 4 || handle == 5 || handle == 6)
                {
                    if (Math.Abs(bottom - o.Top) < threshold) bottom = o.Top;
                    if (Math.Abs(bottom - o.Bottom) < threshold) bottom = o.Bottom;
                }
            }

            if (handle == 0 || handle == 7 || handle == 6)
            {
                if (Math.Abs(left) < threshold) left = 0;
            }
            if (handle == 2 || handle == 3 || handle == 4)
            {
                if (Math.Abs(right - canvasW) < threshold) right = canvasW;
            }
            if (handle == 0 || handle == 1 || handle == 2)
            {
                if (Math.Abs(top) < threshold) top = 0;
            }
            if (handle == 4 || handle == 5 || handle == 6)
            {
                if (Math.Abs(bottom - canvasH) < threshold) bottom = canvasH;
            }

            double cx = canvasW / 2.0;
            double cy = canvasH / 2.0;

            if ((handle == 3 || handle == 7) && Math.Abs(((left + right) / 2) - cx) < threshold)
            {
                double w = right - left;
                left = cx - w / 2;
                right = cx + w / 2;
            }

            if ((handle == 1 || handle == 5) && Math.Abs(((top + bottom) / 2) - cy) < threshold)
            {
                double h = bottom - top;
                top = cy - h / 2;
                bottom = cy + h / 2;
            }

            double width = Math.Max(MinCellSize, right - left);
            double height = Math.Max(MinCellSize, bottom - top);

            if (handle == 0 || handle == 6 || handle == 7) left = right - width;
            if (handle == 0 || handle == 1 || handle == 2) top = bottom - height;

            return new Rect(left, top, width, height);
        }

        private (double snappedX, double snappedY, List<Rect> guides) GetSnappedPosition(
            CollageElement draggedElement, double proposedX, double proposedY, bool ctrlHeld)
        {
            if (!IsSnappingEnabled || ctrlHeld)
                return (proposedX, proposedY, new List<Rect>());

            double canvasWidth = _project?.CanvasWidth ?? 0;
            double canvasHeight = _project?.CanvasHeight ?? 0;

            var guides = new List<Rect>();
            double snappedX = proposedX;
            double snappedY = proposedY;

            var currentRect = new Rect(proposedX, proposedY, draggedElement.Layout.Width, draggedElement.Layout.Height);

            if (_project != null)
            {
                foreach (var other in _project.Elements)
                {
                    if (other == draggedElement || SelectedElements.Contains(other)) continue;
                    var otherRect = other.Layout;

                    if (Math.Abs(currentRect.Left - otherRect.Right) < SnapThreshold)
                    {
                        snappedX = otherRect.Right;
                        guides.Add(new Rect(otherRect.Right - 1, Math.Min(currentRect.Top, otherRect.Top), 2, Math.Max(currentRect.Height, otherRect.Height)));
                    }
                    else if (Math.Abs(currentRect.Left - otherRect.Left) < SnapThreshold)
                    {
                        snappedX = otherRect.Left;
                        guides.Add(new Rect(otherRect.Left - 1, Math.Min(currentRect.Top, otherRect.Top), 2, Math.Max(currentRect.Height, otherRect.Height)));
                    }
                    else if (Math.Abs(currentRect.Right - otherRect.Left) < SnapThreshold)
                    {
                        snappedX = otherRect.Left - currentRect.Width;
                        guides.Add(new Rect(otherRect.Left - 1, Math.Min(currentRect.Top, otherRect.Top), 2, Math.Max(currentRect.Height, otherRect.Height)));
                    }
                    else if (Math.Abs(currentRect.Right - otherRect.Right) < SnapThreshold)
                    {
                        snappedX = otherRect.Right - currentRect.Width;
                        guides.Add(new Rect(otherRect.Right - 1, Math.Min(currentRect.Top, otherRect.Top), 2, Math.Max(currentRect.Height, otherRect.Height)));
                    }

                    if (Math.Abs(currentRect.Top - otherRect.Bottom) < SnapThreshold)
                    {
                        snappedY = otherRect.Bottom;
                        guides.Add(new Rect(Math.Min(currentRect.Left, otherRect.Left), otherRect.Bottom - 1, Math.Max(currentRect.Width, otherRect.Width), 2));
                    }
                    else if (Math.Abs(currentRect.Top - otherRect.Top) < SnapThreshold)
                    {
                        snappedY = otherRect.Top;
                        guides.Add(new Rect(Math.Min(currentRect.Left, otherRect.Left), otherRect.Top - 1, Math.Max(currentRect.Width, otherRect.Width), 2));
                    }
                    else if (Math.Abs(currentRect.Bottom - otherRect.Top) < SnapThreshold)
                    {
                        snappedY = otherRect.Top - currentRect.Height;
                        guides.Add(new Rect(Math.Min(currentRect.Left, otherRect.Left), otherRect.Top - 1, Math.Max(currentRect.Width, otherRect.Width), 2));
                    }
                    else if (Math.Abs(currentRect.Bottom - otherRect.Bottom) < SnapThreshold)
                    {
                        snappedY = otherRect.Bottom - currentRect.Height;
                        guides.Add(new Rect(Math.Min(currentRect.Left, otherRect.Left), otherRect.Bottom - 1, Math.Max(currentRect.Width, otherRect.Width), 2));
                    }

                    double otherCenterX = otherRect.Left + otherRect.Width / 2.0;
                    if (Math.Abs(currentRect.Left + currentRect.Width / 2.0 - otherCenterX) < SnapThreshold)
                    {
                        snappedX = otherCenterX - currentRect.Width / 2.0;
                        double guideStartY = Math.Min(currentRect.Top, otherRect.Top) - 20;
                        double guideEndY = Math.Max(currentRect.Bottom, otherRect.Bottom) + 20;
                        guides.Add(new Rect(otherCenterX - 1, guideStartY, 2, guideEndY - guideStartY));
                    }

                    double otherCenterY = otherRect.Top + otherRect.Height / 2.0;
                    if (Math.Abs(currentRect.Top + currentRect.Height / 2.0 - otherCenterY) < SnapThreshold)
                    {
                        snappedY = otherCenterY - currentRect.Height / 2.0;
                        double guideStartX = Math.Min(currentRect.Left, otherRect.Left) - 20;
                        double guideEndX = Math.Max(currentRect.Right, otherRect.Right) + 20;
                        guides.Add(new Rect(guideStartX, otherCenterY - 1, guideEndX - guideStartX, 2));
                    }
                }

                double canvasCenterX = canvasWidth / 2.0;
                if (Math.Abs(currentRect.Left + currentRect.Width / 2.0 - canvasCenterX) < SnapThreshold)
                {
                    snappedX = canvasCenterX - currentRect.Width / 2.0;
                    guides.Add(new Rect(canvasCenterX - 1, 0, 2, canvasHeight));
                }

                double canvasCenterY = canvasHeight / 2.0;
                if (Math.Abs(currentRect.Top + currentRect.Height / 2.0 - canvasCenterY) < SnapThreshold)
                {
                    snappedY = canvasCenterY - currentRect.Height / 2.0;
                    guides.Add(new Rect(0, canvasCenterY - 1, canvasWidth, 2));
                }

                if (Math.Abs(currentRect.Left) < SnapThreshold)
                {
                    snappedX = 0;
                    guides.Add(new Rect(-1, Math.Min(currentRect.Top, 0), 2, Math.Max(currentRect.Height, canvasHeight)));
                }
                if (Math.Abs(currentRect.Right - canvasWidth) < SnapThreshold)
                {
                    snappedX = canvasWidth - currentRect.Width;
                    guides.Add(new Rect(canvasWidth - 1, Math.Min(currentRect.Top, 0), 2, Math.Max(currentRect.Height, canvasHeight)));
                }
                if (Math.Abs(currentRect.Top) < SnapThreshold)
                {
                    snappedY = 0;
                    guides.Add(new Rect(Math.Min(currentRect.Left, 0), -1, Math.Max(currentRect.Width, canvasWidth), 2));
                }
                if (Math.Abs(currentRect.Bottom - canvasHeight) < SnapThreshold)
                {
                    snappedY = canvasHeight - currentRect.Height;
                    guides.Add(new Rect(Math.Min(currentRect.Left, 0), canvasHeight - 1, Math.Max(currentRect.Width, canvasWidth), 2));
                }

                if (ShowGrid && _project != null)
                {
                    double leftEdge = proposedX;
                    double rightEdge = proposedX + currentRect.Width;

                    double nearestLeft = Math.Round(leftEdge / GridSpacing) * GridSpacing;
                    double nearestRight = Math.Round(rightEdge / GridSpacing) * GridSpacing;

                    if (Math.Abs(leftEdge - nearestLeft) < SnapThreshold)
                    {
                        snappedX = nearestLeft;
                    }
                    else if (Math.Abs(rightEdge - nearestRight) < SnapThreshold)
                    {
                        snappedX = nearestRight - currentRect.Width;
                    }

                    double topEdge = proposedY;
                    double bottomEdge = proposedY + currentRect.Height;

                    double nearestTop = Math.Round(topEdge / GridSpacing) * GridSpacing;
                    double nearestBottom = Math.Round(bottomEdge / GridSpacing) * GridSpacing;

                    if (Math.Abs(topEdge - nearestTop) < SnapThreshold)
                    {
                        snappedY = nearestTop;
                    }
                    else if (Math.Abs(bottomEdge - nearestBottom) < SnapThreshold)
                    {
                        snappedY = nearestBottom - currentRect.Height;
                    }
                }
            }

            return (snappedX, snappedY, guides);
        }

        private bool RectsIntersect(Rect a, Rect b)
        {
            return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
        }

        private CollageElement? HitTestCell(Point modelPos)
        {
            if (_project == null) return null;
            for (int i = _project.Elements.Count - 1; i >= 0; i--)
            {
                if (_project.Elements[i].Layout.Contains(modelPos))
                    return _project.Elements[i];
            }
            return null;
        }

        private int HitTestResizeHandle(CollageElement element, Point modelPos)
        {
            var r = element.Layout;
            double hitTolerance = 40.0 / _globalZoom;

            Point[] handles = {
                new Point(r.Left, r.Top), new Point(r.Left + r.Width / 2, r.Top),
                new Point(r.Right, r.Top), new Point(r.Right, r.Top + r.Height / 2),
                new Point(r.Right, r.Bottom), new Point(r.Left + r.Width / 2, r.Bottom),
                new Point(r.Left, r.Bottom), new Point(r.Left, r.Top + r.Height / 2)
            };

            for (int i = 0; i < handles.Length; i++)
            {
                if (Math.Abs(modelPos.X - handles[i].X) <= hitTolerance / 2 && Math.Abs(modelPos.Y - handles[i].Y) <= hitTolerance / 2)
                    return i;
            }
            return -1;
        }

        private Windows.UI.Color HexToColor(string hex)
        {
            try
            {
                var clean = hex.Replace("#", "");
                if (clean.Length == 6) clean = "FF" + clean;
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(clean.Substring(0, 2), 16),
                    Convert.ToByte(clean.Substring(2, 2), 16),
                    Convert.ToByte(clean.Substring(4, 2), 16),
                    Convert.ToByte(clean.Substring(6, 2), 16));
            }
            catch { return Microsoft.UI.Colors.White; }
        }

        private void CollageCanvasControl_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var session = args.DrawingSession;
            session.Clear(Windows.UI.Color.FromArgb(255, 45, 45, 45));

            if (_project == null) return;

            session.Transform = GetRenderMatrix();

            var canvasRect = new Rect(0, 0, _project.CanvasWidth, _project.CanvasHeight);
            session.FillRectangle(canvasRect, HexToColor(_project.BackgroundColor));
            session.DrawRectangle(canvasRect, Microsoft.UI.Colors.Black, 0.5f / _globalZoom);

            System.Numerics.Matrix3x2.Invert(GetRenderMatrix(), out var inverseMatrix);
            var topLeft = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(0, 0), inverseMatrix);
            var bottomRight = System.Numerics.Vector2.Transform(new System.Numerics.Vector2((float)sender.ActualWidth, (float)sender.ActualHeight), inverseMatrix);
            var visibleRect = new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);

            if (ShowGrid && _project != null)
            {
                float dipScale = XamlRoot != null ? (float)XamlRoot.RasterizationScale : 1.0f;
                float gridSpacing = GridSpacing;
                float scaledSpacing = gridSpacing * _globalZoom / dipScale;

                if (scaledSpacing > 8)
                {
                    var gridColor = Windows.UI.Color.FromArgb(65, 140, 160, 180);

                    float startX = (float)Math.Floor(topLeft.X / gridSpacing) * gridSpacing;
                    float startY = (float)Math.Floor(topLeft.Y / gridSpacing) * gridSpacing;

                    for (float x = startX; x < bottomRight.X; x += gridSpacing)
                    {
                        session.DrawLine(new System.Numerics.Vector2(x, topLeft.Y), new System.Numerics.Vector2(x, bottomRight.Y), gridColor, 1f / _globalZoom);
                    }

                    for (float y = startY; y < bottomRight.Y; y += gridSpacing)
                    {
                        session.DrawLine(new System.Numerics.Vector2(topLeft.X, y), new System.Numerics.Vector2(bottomRight.X, y), gridColor, 1f / _globalZoom);
                    }
                }
            }

            foreach (var element in _project.Elements)
            {
                if (!RectsIntersect(visibleRect, element.Layout)) continue;

                ImageCacheEntry? entry = null;
                CanvasBitmap? localGpuBitmap = null;

                if (!string.IsNullOrEmpty(element.ImagePath))
                {
                    App.GlobalImageCache.TryGetValue(element.ImagePath, out entry);

                    // Prefer HighRes if available
                    if (element.HasHighRes && element.HighResBitmap != null)
                    {
                        localGpuBitmap = element.HighResBitmap;
                    }
                    else
                    {
                        if (!_localGpuCache.TryGetValue(element.ImagePath, out localGpuBitmap) || localGpuBitmap == null)
                        {
                            if (_inflightLoads.Add(element.ImagePath))
                            {
                                string pathToLoad = element.ImagePath;
                                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                                {
                                    _ = ReloadImageAsync(pathToLoad);
                                });
                            }
                        }
                    }

                    // Request high-res when zoomed in or selected
                    bool shouldRequestHighRes =
                        (element.Zoom >= HighResZoomThreshold || SelectedElements.Contains(element)) &&
                        !element.HasHighRes &&
                        !element.IsHighResLoading;

                    if (shouldRequestHighRes && !_highResInflight.Contains(element.ImagePath))
                    {
                        var el = element;
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                        {
                            _ = LoadHighResAsync(el);
                        });
                    }
                }

                if (localGpuBitmap != null && element.NeedsContentFill)
                {
                    double logicW = entry?.NativeWidth > 0 ? entry.NativeWidth : localGpuBitmap.SizeInPixels.Width;
                    double logicH = entry?.NativeHeight > 0 ? entry.NativeHeight : localGpuBitmap.SizeInPixels.Height;
                    element.FillToCell(logicW, logicH);
                    element.NeedsContentFill = false;
                }

                var r = element.Layout;

                if (element.ShadowSize > 0)
                {
                    var shadowRect = new Rect(r.X + (element.ShadowSize / 3.0), r.Y + (element.ShadowSize / 3.0), r.Width, r.Height);
                    var shadowColor = Windows.UI.Color.FromArgb(90, 0, 0, 0);

                    if (element.CornerRadius > 0)
                        session.FillRoundedRectangle(shadowRect, element.CornerRadius, element.CornerRadius, shadowColor);
                    else
                        session.FillRectangle(shadowRect, shadowColor);
                }

                var clipGeom = element.GetOrUpdateClipGeometry(sender);

                CollageCellRenderer.DrawCell(session, element, entry, localGpuBitmap, _currentGamma, clipGeom);

                if (element.BorderThickness > 0)
                {
                    if (element.CornerRadius > 0)
                        session.DrawRoundedRectangle(r, element.CornerRadius, element.CornerRadius, Microsoft.UI.Colors.White, element.BorderThickness / _globalZoom);
                    else
                        session.DrawRectangle(r, Microsoft.UI.Colors.White, element.BorderThickness / _globalZoom);
                }
            }

            foreach (var element in SelectedElements)
            {
                var r = element.Layout;

                var borderColor = element.IsLocked ? Microsoft.UI.Colors.Red :
                                  (element.IsAnchored ? Microsoft.UI.Colors.Orange :
                                  (element.IsContentLocked ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.LimeGreen));

                session.DrawRectangle(r, borderColor, 2f / _globalZoom);

                if (!element.IsLocked && !element.IsAnchored && !element.IsContentLocked)
                {
                    double hSize = BaseHandleSize / _globalZoom;
                    DrawHandle(session, r.Left, r.Top, hSize);
                    DrawHandle(session, r.Left + r.Width / 2, r.Top, hSize);
                    DrawHandle(session, r.Right, r.Top, hSize);
                    DrawHandle(session, r.Right, r.Top + r.Height / 2, hSize);
                    DrawHandle(session, r.Right, r.Bottom, hSize);
                    DrawHandle(session, r.Left + r.Width / 2, r.Bottom, hSize);
                    DrawHandle(session, r.Left, r.Bottom, hSize);
                    DrawHandle(session, r.Left, r.Top + r.Height / 2, hSize);
                }
            }

            double cSize = BaseHandleSize / _globalZoom;
            session.FillRectangle(new Rect(_project.CanvasWidth - cSize / 2, _project.CanvasHeight / 2 - cSize / 2, cSize, cSize), Microsoft.UI.Colors.LightGray);
            session.FillRectangle(new Rect(_project.CanvasWidth / 2 - cSize / 2, _project.CanvasHeight - cSize / 2, cSize, cSize), Microsoft.UI.Colors.LightGray);
            session.FillRectangle(new Rect(_project.CanvasWidth - cSize / 2, _project.CanvasHeight - cSize / 2, cSize, cSize), Microsoft.UI.Colors.White);

            if (_isMarqueeSelecting && _marqueeRect.Width > 0 && _marqueeRect.Height > 0)
            {
                session.DrawRectangle(_marqueeRect, Microsoft.UI.Colors.DeepSkyBlue, 1.5f / _globalZoom);
            }

            foreach (var guide in _activeSnapGuides)
            {
                var color = Microsoft.UI.Colors.DeepSkyBlue;
                if (guide.Width < 4)
                    session.DrawLine(new System.Numerics.Vector2((float)guide.X, (float)guide.Y), new System.Numerics.Vector2((float)guide.X, (float)(guide.Y + guide.Height)), color, 2.0f / _globalZoom);
                else
                    session.DrawLine(new System.Numerics.Vector2((float)guide.X, (float)guide.Y), new System.Numerics.Vector2((float)(guide.X + guide.Width), (float)guide.Y), color, 2.0f / _globalZoom);
            }

            if (_canvasResizeEdge > 0 && _project != null)
            {
                string sizeText = $"{_project.CanvasWidth:0} × {_project.CanvasHeight:0}";

                using var textFormat = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
                {
                    FontSize = 18f / _globalZoom,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                    VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center
                };

                double centerX = _project.CanvasWidth / 2;
                double centerY = _project.CanvasHeight / 2;
                var textRect = new Rect(centerX - 100 / _globalZoom, centerY - 25 / _globalZoom, 200 / _globalZoom, 50 / _globalZoom);

                session.FillRoundedRectangle(textRect, 8f / _globalZoom, 8f / _globalZoom, Windows.UI.Color.FromArgb(200, 0, 0, 0));
                session.DrawText(sizeText, textRect, Microsoft.UI.Colors.White, textFormat);
            }

            session.Transform = System.Numerics.Matrix3x2.Identity;
        }

        private void DrawHandle(CanvasDrawingSession session, double x, double y, double size)
        {
            session.FillRectangle(new Rect(x - size / 2, y - size / 2, size, size), Microsoft.UI.Colors.White);
            session.DrawRectangle(new Rect(x - size / 2, y - size / 2, size, size), Microsoft.UI.Colors.Black, 1.0f / _globalZoom);
        }
    }
}
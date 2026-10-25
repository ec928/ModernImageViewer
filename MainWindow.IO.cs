using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ModernImageViewer
{
    public sealed partial class MainWindow
    {
        private bool _isSidebarHooked = false;
        private TreeViewNode? _pendingNodeToSelect;
        private int _pendingTargetIndex = 0;
        private FileSystemWatcher? _folderWatcher;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _folderWatchTimer;
        private bool _folderWatchDirty;

        private bool HasSupportedImages(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return false;
            try
            {
                var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };
                return Directory.EnumerateFiles(folderPath, "*.*", enumOptions)
                                .Any(f => SupportedExtensions.Contains(Path.GetExtension(f)));
            }
            catch { return false; }
        }

        public async void HandleFileActivation(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            string folderPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(folderPath)) return;

            _isStartupIndexing = true;

            var fastTokenSource = new CancellationTokenSource();
            var fastEntry = await Task.Run(() => ViewerEngine.DecodeFastPreviewAsync(filePath, fastTokenSource.Token));

            if (fastEntry != null && fastEntry.Bitmap != null)
            {
                fastEntry.GpuBitmap = ViewerEngine.CreateGpuBitmap(fastEntry.Bitmap);
                fastEntry.Bitmap.Dispose();
                fastEntry.Bitmap = null;

                var phantomItem = new ImageItem { Path = filePath, Name = Path.GetFileName(filePath), Dispatcher = this.DispatcherQueue };

                ViewerControl.Visibility = Visibility.Visible;
                if (HoverTriggerZone != null) HoverTriggerZone.Visibility = Visibility.Collapsed;
                ViewerControl.TargetImage = phantomItem;

                // Release, not Dispose: a detached window may hold a reference to this entry.
                // Refcounting frees it only once the last holder is done with it.
                ImageCache.Publish(filePath, fastEntry);

                TryDisposeRawGpuBitmap();
                _rawGpuBitmap = fastEntry.GpuBitmap;
                _rawGpuProfile = fastEntry.Profile;
                _logicalImageWidth = fastEntry.NativeWidth;
                _logicalImageHeight = fastEntry.NativeHeight;
                _currentRenderedItem = phantomItem;
                _isHighFidelityActive = fastEntry.IsHighFidelity;

                ViewerControl.InjectImage(fastEntry, true);

                QueueSharperDecode();
            }

            await ScanFolder(folderPath);

            var targetItem = Images.FirstOrDefault(x => x.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (targetItem != null)
            {
                _currentIndex = Images.IndexOf(targetItem);
                if (_listMode) PlaceListCursorOnCurrentImage();
                SyncTileCursor(scroll: false);
                _currentRenderedItem = targetItem;
                ViewerControl.TargetImage = targetItem;
                ViewerControl.ShowHud();

                _exifCts?.Cancel();
                _exifCts?.Dispose();
                _exifCts = new CancellationTokenSource();
                _ = targetItem.LoadExifAsync(_exifCts.Token);

                ManageCache(_currentIndex, 1);
                QueueSharperDecode();
            }
            else
            {
                _currentIndex = -1;
                _listCursor = -1;
                SyncTileCursor(scroll: false);
                ClosePreviewInternal();
            }

            _isStartupIndexing = false;
        }

        private async Task UpdateNavigationTreeOnlyAsync(string folderPath)
        {
            if (folderPath == DriveRootPath)
            {
                ShowDriveList();
                return;
            }
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            int myScanId = ++_currentScanId;
            _isScanning = false;
            _listReturnPath = IsChildOf(_currentDirectory, folderPath) ? _currentDirectory : null;
            _currentDirectory = folderPath;
            _currentIndex = -1;
            _listCursor = -1;
            Images.Clear();
            _childFolders = [];
            if (_listEntries.Count > 0) _listEntries.Clear();
            if (LoadingRing != null) LoadingRing.IsActive = false;
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;

            UpdateBreadcrumbs(folderPath);

            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };

            var dirTask = Task.Run(() =>
            {
                var dirInfo = new DirectoryInfo(folderPath);

                var subDirsArr = dirInfo.GetDirectories("*", enumOptions);
                Array.Sort(subDirsArr, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                var subDirs = subDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();

                var parentDir = Directory.GetParent(folderPath);
                var hopDirs = new List<FolderItem>();

                if (parentDir != null)
                {
                    var hopDirsArr = parentDir.GetDirectories("*", enumOptions);
                    Array.Sort(hopDirsArr, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                    hopDirs = hopDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();
                }

                return new { SubDirs = subDirs, HopDirs = hopDirs, Parent = parentDir };
            });

            var folderData = await dirTask;

            if (myScanId != _currentScanId) return;

            _hopFolders = folderData.HopDirs;
            _childFolders = folderData.SubDirs;
            PopulateTreeView(folderData.Parent, folderData.HopDirs, folderData.SubDirs, folderPath, 0);
            if (_listMode)
            {
                RebuildListEntries();
                bool returnedToFolder = TryPlaceListCursorOnReturnedFolder();
                if (!returnedToFolder) PlaceListCursorOnCurrentImage();
                SyncTileCursor(scroll: returnedToFolder);
            }
            _listReturnPath = null;
            UpdateHeaderFolderInfo();
        }

        private async Task ScanFolder(string folderPath)
        {
            if (folderPath == DriveRootPath)
            {
                ShowDriveList();
                return;
            }
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible && !_isStartupIndexing)
            {
                ClosePreviewInternal();
            }
            if (!_isStartupIndexing)
            {
                _currentIndex = -1;
                _listCursor = -1;
                SyncTileCursor(scroll: false);
                _currentRenderedItem = null;
                _isHighFidelityActive = false;
                _hfPromotionTimer?.Stop();
            }

            if (!_isStartupIndexing)
            {
                ClearImageCache();
                TryDisposeRawGpuBitmap();
                _rawGpuBitmap = null;
            }

            int myScanId = ++_currentScanId;
            _isScanning = true;
            _listReturnPath = IsChildOf(_currentDirectory, folderPath) ? _currentDirectory : null;
            _currentDirectory = folderPath;

            UpdateBreadcrumbs(folderPath);

            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Visible;
            if (LoadingRing != null) LoadingRing.IsActive = true;

            foreach (var item in Images)
            {
                item.ClearThumbnail();
            }
            Images.Clear();
            _childFolders = [];
            if (_listEntries.Count > 0) _listEntries.Clear();

            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };

            try
            {
                var dirTask = Task.Run(() =>
                {
                    var dirInfo = new DirectoryInfo(folderPath);

                    var subDirsArr = dirInfo.GetDirectories("*", enumOptions);
                    Array.Sort(subDirsArr, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                    var subDirs = subDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();

                    var parentDir = Directory.GetParent(folderPath);
                    var hopDirs = new List<FolderItem>();

                    if (parentDir != null)
                    {
                        var hopDirsArr = parentDir.GetDirectories("*", enumOptions);
                        Array.Sort(hopDirsArr, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                        hopDirs = hopDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();
                    }

                    return new { SubDirs = subDirs, HopDirs = hopDirs, Parent = parentDir };
                });

                var fileTask = Task.Run(() => BuildFolderImages(folderPath, myScanId));

                await Task.WhenAll(dirTask, fileTask);

                if (myScanId != _currentScanId) return;

                var folderData = dirTask.Result;
                var items = fileTask.Result;

                _hopFolders = folderData.HopDirs;
                _childFolders = folderData.SubDirs;
                PopulateTreeView(folderData.Parent, folderData.HopDirs, folderData.SubDirs, folderPath, items.Count);

                // Tile mode shows pictures as each batch lands. List mode waits, so the folder cells and the photos appear together.
                if (!_listMode)
                {
                    if (LoadingRing != null) LoadingRing.IsActive = false;
                    if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
                }

                const int batchSize = 100;
                for (int i = 0; i < items.Count; i += batchSize)
                {
                    if (myScanId != _currentScanId) return;
                    int count = Math.Min(batchSize, items.Count - i);
                    Images.AddRange(items.GetRange(i, count));
                    if (i + count < items.Count)
                        await Task.Yield();
                }

                if (myScanId == _currentScanId)
                {
                    if (_listMode) RebuildListEntries();
                    if (items.Count > 0) AddFolderToRecent(folderPath);
                    bool seeded = false;
                    if (items.Count > 0 && (_currentIndex < 0 || _currentIndex >= Images.Count))
                    {
                        _currentIndex = 0;
                        seeded = true;
                    }
                    bool returnedToFolder = false;
                    if (_listMode)
                    {
                        returnedToFolder = TryPlaceListCursorOnReturnedFolder();
                        if (!returnedToFolder) PlaceListCursorOnCurrentImage();
                        SyncTileCursor(scroll: returnedToFolder);
                    }
                    else if (seeded)
                        SyncTileCursor(scroll: false);
                    _listReturnPath = null;
                    UpdateHeaderFolderInfo();
                    RestartFolderWatcher(folderPath);
                }
            }
            finally
            {
                if (myScanId == _currentScanId)
                {
                    _isScanning = false;
                    if (LoadingRing != null) LoadingRing.IsActive = false;
                    if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
                    UpdateEmptyFolderState();
                    if (_folderWatchDirty) ScheduleFolderRefresh();
                }
            }
        }

        private List<ImageItem> BuildFolderImages(string folderPath, int scanId)
        {
            try
            {
                var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };
                var allFiles = new DirectoryInfo(folderPath).GetFiles("*.*", enumOptions);
                var filtered = new List<FileInfo>(allFiles.Length);
                foreach (var fi in allFiles)
                {
                    if (scanId != _currentScanId) return new List<ImageItem>();
                    if (SupportedExtensions.Contains(fi.Extension))
                        filtered.Add(fi);
                }

                string sortOrder = _currentSettings.SortOrder;
                if (sortOrder == "NameDesc")
                    filtered.Sort((a, b) => NaturalSort.Compare(b.Name, a.Name));
                else if (sortOrder == "DateDesc")
                    filtered.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                else if (sortOrder == "DateAsc")
                    filtered.Sort((a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                else
                    filtered.Sort((a, b) => NaturalSort.Compare(a.Name, b.Name));

                var dispatcher = this.DispatcherQueue;
                var items = new List<ImageItem>(filtered.Count);
                foreach (var fi in filtered)
                {
                    if (scanId != _currentScanId) return new List<ImageItem>();
                    items.Add(new ImageItem
                    {
                        Name = fi.Name,
                        Path = fi.FullName,
                        DateModified = fi.LastWriteTime,
                        SizeString = (fi.Length / 1024.0 < 1024) ? $"{fi.Length / 1024} KB" : $"{(fi.Length / 1048576.0):F2} MB",
                        Dispatcher = dispatcher
                    });
                }
                return items;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Scan] {folderPath}: {ex.Message}");
                return new List<ImageItem>();
            }
        }

        private void RestartFolderWatcher(string folderPath)
        {
            try
            {
                _folderWatcher?.Dispose();
                _folderWatcher = null;
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

                _folderWatcher = new FileSystemWatcher(folderPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _folderWatcher.Created += OnFolderWatchEvent;
                _folderWatcher.Deleted += OnFolderWatchEvent;
                _folderWatcher.Renamed += OnFolderWatchRenamed;
                _folderWatcher.Changed += OnFolderWatchEvent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FolderWatch] {ex.Message}");
            }
        }

        private void OnFolderWatchRenamed(object sender, RenamedEventArgs e)
        {
            if (IsSupportedWatchName(e.OldName) || IsSupportedWatchName(e.Name) || IsSupportedWatchName(e.FullPath))
                ScheduleFolderRefresh();
        }

        private void OnFolderWatchEvent(object sender, FileSystemEventArgs e)
        {
            if (!IsSupportedWatchName(e.Name ?? e.FullPath)) return;
            ScheduleFolderRefresh();
        }

        private static bool IsSupportedWatchName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return SupportedExtensions.Contains(Path.GetExtension(name));
        }

        private void ScheduleFolderRefresh()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_folderWatchTimer == null)
                {
                    _folderWatchTimer = DispatcherQueue.CreateTimer();
                    _folderWatchTimer.Interval = TimeSpan.FromMilliseconds(800);
                    _folderWatchTimer.IsRepeating = false;
                    _folderWatchTimer.Tick += (s, args) =>
                    {
                        _folderWatchTimer.Stop();
                        if (_isScanning || (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible))
                        {
                            _folderWatchDirty = true;
                            return;
                        }
                        _folderWatchDirty = false;
                        if (!string.IsNullOrEmpty(_currentDirectory))
                            _ = ScanFolder(_currentDirectory);
                    };
                }
                _folderWatchTimer.Stop();
                _folderWatchTimer.Start();
            });
        }

        internal void FlushDeferredFolderRefresh()
        {
            if (!_folderWatchDirty) return;
            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible) return;
            ScheduleFolderRefresh();
        }

        internal void StopFolderWatcher()
        {
            _folderWatchTimer?.Stop();
            _folderWatcher?.Dispose();
            _folderWatcher = null;
        }

        private static List<FolderItem> ListDrives()
        {
            var list = new List<FolderItem>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return list; }
            foreach (var drive in drives)
            {
                if (drive.DriveType == DriveType.NoRootDirectory) continue;
                string letter = drive.Name.TrimEnd('\\', '/');
                if (letter.Length == 0) continue;
                string name = letter;
                if (drive.DriveType == DriveType.Fixed)
                {
                    try
                    {
                        if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                            name = drive.VolumeLabel + " (" + letter + ")";
                    }
                    catch { }
                }
                list.Add(new FolderItem { Name = name, Path = drive.Name });
            }
            list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // keepPath is the drive to land on when refreshing. Leaving a drive root lands on that drive.
        private void ShowDriveList(string? keepPath = null)
        {
            ++_currentScanId;
            _isScanning = false;
            if (keepPath != null)
                _listReturnPath = keepPath;
            else if (!AtDriveRoot && IsDriveRoot(_currentDirectory))
                _listReturnPath = _currentDirectory;
            else
                _listReturnPath = null;

            _currentDirectory = DriveRootPath;
            _currentIndex = -1;
            Images.Clear();
            _childFolders = ListDrives();
            if (_listEntries.Count > 0) _listEntries.Clear();
            UpdateBreadcrumbs(DriveRootPath);
            PopulateDriveTree(_childFolders);
            StopFolderWatcher();
            if (LoadingRing != null) LoadingRing.IsActive = false;
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;

            if (_listMode)
            {
                RebuildListEntries();
                bool returned = TryPlaceListCursorOnReturnedFolder();
                if (!returned) PlaceListCursorOnCurrentImage();
                SyncTileCursor(scroll: returned);
            }
            else
                SyncTileCursor(scroll: false);
            _listReturnPath = null;
            UpdateHeaderFolderInfo();
        }

        private void PopulateDriveRootTree(List<FolderItem> drives, string currentFolderPath, List<FolderItem> subDirs, string countText)
        {
            var parentNode = new TreeViewNode
            {
                Content = new FolderItem { Name = DriveRootName, Path = DriveRootPath, NodeOpacity = 0.5, NodeIcon = Symbol.Up },
                IsExpanded = true
            };
            TreeViewNode? nodeToSelect = null;
            int targetIndex = 0;
            int currentIndex = 1;
            foreach (var drive in drives)
            {
                bool isCurrent = SameDirectory(drive.Path, currentFolderPath);
                var node = new TreeViewNode
                {
                    Content = new FolderItem
                    {
                        Name = drive.Name,
                        Path = drive.Path,
                        NodeOpacity = isCurrent ? 1.0 : 0.5,
                        NodeFontWeight = isCurrent ? "Bold" : "Normal",
                        Subtitle = isCurrent ? countText : string.Empty,
                        SubtitleVisibility = isCurrent ? Visibility.Visible : Visibility.Collapsed
                    }
                };
                if (isCurrent)
                {
                    node.IsExpanded = true;
                    nodeToSelect = node;
                    targetIndex = currentIndex;
                    foreach (var sub in subDirs)
                        node.Children.Add(new TreeViewNode { Content = new FolderItem { Name = sub.Name, Path = sub.Path } });
                }
                parentNode.Children.Add(node);
                currentIndex++;
            }
            FolderTreeView.RootNodes.Add(parentNode);
            if (nodeToSelect == null) return;
            FolderTreeView.SelectedNode = nodeToSelect;
            _pendingNodeToSelect = nodeToSelect;
            _pendingTargetIndex = targetIndex;
            _ = BringNodeIntoViewAsync(nodeToSelect, targetIndex);
        }

        private void PopulateDriveTree(List<FolderItem> drives)
        {
            if (FolderTreeView == null) return;
            if (!_isSidebarHooked && SidebarView != null)
            {
                SidebarView.PaneOpened += SidebarView_PaneOpened;
                _isSidebarHooked = true;
            }
            FolderTreeView.RootNodes.Clear();
            var root = new TreeViewNode
            {
                Content = new FolderItem { Name = DriveRootName, Path = DriveRootPath, NodeFontWeight = "Bold" },
                IsExpanded = true
            };
            foreach (var drive in drives)
                root.Children.Add(new TreeViewNode { Content = new FolderItem { Name = drive.Name, Path = drive.Path } });
            FolderTreeView.RootNodes.Add(root);
            FolderTreeView.SelectedNode = root;
            _pendingNodeToSelect = root;
            _pendingTargetIndex = 0;
            _ = BringNodeIntoViewAsync(root, 0);
        }

        private void PopulateTreeView(DirectoryInfo? parentDir, List<FolderItem> hopDirs, List<FolderItem> subDirs, string currentFolderPath, int currentImageCount = 0)
        {
            if (FolderTreeView == null) return;
            FolderTreeView.RootNodes.Clear();

            TreeViewNode? nodeToSelect = null;
            int targetIndex = 0;
            int currentIndex = 0;

            if (!_isSidebarHooked && SidebarView != null)
            {
                SidebarView.PaneOpened += SidebarView_PaneOpened;
                _isSidebarHooked = true;
            }

            string countText = currentImageCount == 1 ? "1 image" : $"{currentImageCount} images";

            if (parentDir == null && IsDriveRoot(currentFolderPath))
            {
                PopulateDriveRootTree(ListDrives(), currentFolderPath, subDirs, countText);
                return;
            }

            if (parentDir != null)
            {
                var parentNode = new TreeViewNode { Content = new FolderItem { Name = parentDir.Name, Path = parentDir.FullName, NodeOpacity = 0.5, NodeIcon = Symbol.Up }, IsExpanded = true };
                currentIndex++;

                foreach (var sibling in hopDirs)
                {
                    bool isCurrent = sibling.Path.Equals(currentFolderPath, StringComparison.OrdinalIgnoreCase);
                    var siblingNode = new TreeViewNode
                    {
                        Content = new FolderItem
                        {
                            Name = sibling.Name,
                            Path = sibling.Path,
                            NodeOpacity = isCurrent ? 1.0 : 0.5,
                            NodeFontWeight = isCurrent ? "Bold" : "Normal",
                            Subtitle = isCurrent ? countText : string.Empty,
                            SubtitleVisibility = isCurrent ? Visibility.Visible : Visibility.Collapsed
                        }
                    };

                    if (isCurrent)
                    {
                        siblingNode.IsExpanded = true;
                        nodeToSelect = siblingNode;
                        targetIndex = currentIndex;
                        foreach (var sub in subDirs) siblingNode.Children.Add(new TreeViewNode { Content = new FolderItem { Name = sub.Name, Path = sub.Path } });
                    }
                    parentNode.Children.Add(siblingNode);
                    currentIndex++;
                }
                FolderTreeView.RootNodes.Add(parentNode);
            }
            else
            {
                var rootNode = new TreeViewNode
                {
                    Content = new FolderItem
                    {
                        Name = currentFolderPath,
                        Path = currentFolderPath,
                        NodeFontWeight = "Bold",
                        Subtitle = countText,
                        SubtitleVisibility = Visibility.Visible
                    },
                    IsExpanded = true
                };

                nodeToSelect = rootNode;
                targetIndex = currentIndex;
                currentIndex++;

                foreach (var sub in subDirs) rootNode.Children.Add(new TreeViewNode { Content = new FolderItem { Name = sub.Name, Path = sub.Path } });
                FolderTreeView.RootNodes.Add(rootNode);
            }

            if (nodeToSelect != null)
            {
                FolderTreeView.SelectedNode = nodeToSelect;
                _pendingNodeToSelect = nodeToSelect;
                _pendingTargetIndex = targetIndex;
                _ = BringNodeIntoViewAsync(nodeToSelect, targetIndex);
            }
        }

        private void SidebarView_PaneOpened(SplitView sender, object args)
        {
            if (_pendingNodeToSelect != null)
            {
                _ = BringNodeIntoViewAsync(_pendingNodeToSelect, _pendingTargetIndex);
            }
        }

        private async Task BringNodeIntoViewAsync(TreeViewNode nodeToSelect, int targetIndex)
        {
            if (FolderTreeView == null || nodeToSelect == null) return;

            FolderTreeView.SelectedNode = nodeToSelect;
            _pendingNodeToSelect = nodeToSelect;
            _pendingTargetIndex = targetIndex;

            // Improved multi-attempt scroll
            for (int attempt = 0; attempt < 15; attempt++)
            {
                bool success = false;
                var tcs = new TaskCompletionSource<bool>();

                DispatcherQueue.TryEnqueue(() =>
                {
                    FolderTreeView.UpdateLayout();

                    var container = FolderTreeView.ContainerFromNode(nodeToSelect) as UIElement;
                    if (container != null)
                    {
                        container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5 });
                        success = true;
                    }
                    else
                    {
                        var sv = FindScrollViewer(FolderTreeView);
                        if (sv != null)
                        {
                            double estimatedOffset = targetIndex * 36.0;
                            sv.ChangeView(null, Math.Max(0, estimatedOffset - (sv.ViewportHeight / 2.0)), null, true);
                        }
                    }

                    tcs.TrySetResult(success);
                });

                await tcs.Task;

                if (success)
                {
                    _pendingNodeToSelect = null;
                    return;
                }

                await Task.Delay(30);
            }
        }

        private void UpdateBreadcrumbs(string path)
        {
            _breadcrumbs.Clear();
            if (path == DriveRootPath)
            {
                _breadcrumbs.Add(DriveRootName);
                UpdateHeaderFolderInfo();
                return;
            }
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var part in path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    _breadcrumbs.Add(part);
                }
            }
            UpdateHeaderFolderInfo();
        }

        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar s, BreadcrumbBarItemClickedEventArgs a)
        {
            if (_breadcrumbs.Count == 0) return;
            if (a.Index == 0 && _breadcrumbs[0] == DriveRootName && _breadcrumbs.Count == 1)
            {
                ShowDriveList();
                return;
            }
            string newPath = _breadcrumbs[0] + (char)Path.DirectorySeparatorChar;
            for (int i = 1; i <= a.Index; i++) newPath = Path.Combine(newPath, _breadcrumbs[i]);

            bool hasImages = await Task.Run(() => HasSupportedImages(newPath));

            if (hasImages)
            {
                if (SidebarView != null) SidebarView.IsPaneOpen = false;
                await ScanFolder(newPath);
            }
            else
            {
                if (SidebarView != null) SidebarView.IsPaneOpen = true;
                await UpdateNavigationTreeOnlyAsync(newPath);
            }
        }

        private async void BrowseButton_Click(object s, RoutedEventArgs e)
        {
            var p = new FolderPicker();
            p.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(p, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var f = await p.PickSingleFolderAsync();
            if (f != null) await ScanFolder(f.Path);
        }

        private async void GoUp_Click(object s, RoutedEventArgs e)
        {
            if (AtDriveRoot) return;
            var parent = Directory.GetParent(_currentDirectory);
            if (parent != null) await ScanFolder(parent.FullName);
            else if (IsDriveRoot(_currentDirectory)) ShowDriveList();
        }

        private async void RefreshFolder_Click(object s, RoutedEventArgs e)
        {
            if (AtDriveRoot)
            {
                string? keepDrive = null;
                if (_listMode && _listCursor >= 0 && _listCursor < _listEntries.Count)
                    keepDrive = _listEntries[_listCursor].Path;
                ShowDriveList(keepDrive);
                RestoreGridKeyFocus(keepDrive);
                return;
            }
            if (string.IsNullOrEmpty(_currentDirectory) || !Directory.Exists(_currentDirectory)) return;
            string? keepPath = null;
            if (_listMode && _listCursor >= 0 && _listCursor < _listEntries.Count)
                keepPath = _listEntries[_listCursor].Path;
            else if (_currentIndex >= 0 && _currentIndex < Images.Count)
                keepPath = Images[_currentIndex].Path;
            await ScanFolder(_currentDirectory);
            RestoreGridKeyFocus(keepPath);
        }
        private void ClearRecentFolders_Click(object sender, RoutedEventArgs e)
        {
            _recentFolders.Clear();
        }

        private async void FolderTreeView_ItemInvoked(TreeView s, TreeViewItemInvokedEventArgs a)
        {
            string path = (a.InvokedItem is TreeViewNode n && n.Content is FolderItem fi) ? fi.Path : "";
            if (path == DriveRootPath)
            {
                ShowDriveList();
                return;
            }
            if (!string.IsNullOrEmpty(path))
            {
                bool hasImages = await Task.Run(() => HasSupportedImages(path));

                if (hasImages)
                {
                    if (SidebarView != null) SidebarView.IsPaneOpen = false;
                    await ScanFolder(path);
                }
                else
                {
                    await UpdateNavigationTreeOnlyAsync(path);
                }
            }
        }

        private void Sort_Click(object s, RoutedEventArgs e)
        {
            if (s is MenuFlyoutItem item && item.Tag is string tag)
            {
                var currentItem = (_currentIndex >= 0 && _currentIndex < Images.Count) ? Images[_currentIndex] : null;

                var sorted = tag.StartsWith("Name") ?
                    (tag.EndsWith("Asc")
                        ? Images.OrderBy(x => x.Name, NaturalSort.Comparer)
                        : Images.OrderByDescending(x => x.Name, NaturalSort.Comparer)) :
                    (tag.EndsWith("Asc") ? Images.OrderBy(x => x.DateModified) : Images.OrderByDescending(x => x.DateModified));

                var sortedList = sorted.ToList();
                Images.Clear();
                Images.AddRange(sortedList);

                if (currentItem != null) _currentIndex = Images.IndexOf(currentItem);
                if (_listMode)
                {
                    RebuildListEntries();
                    PlaceListCursorOnCurrentImage();
                }
                SyncTileCursor(scroll: true);

                _currentSettings.SortOrder = tag;
                SyncSortChecks(tag);
                SaveAllSettings();
            }
        }

        private void HeaderSortFlyout_Opening(object sender, object e) => SyncSortChecks(_currentSettings.SortOrder);

        private void SyncSortChecks(string tag)
        {
            if (tag != "NameDesc" && tag != "DateDesc" && tag != "DateAsc") tag = "NameAsc";
            SetSortCheck(SortNameAscItem, tag == "NameAsc");
            SetSortCheck(SortNameDescItem, tag == "NameDesc");
            SetSortCheck(SortDateAscItem, tag == "DateAsc");
            SetSortCheck(SortDateDescItem, tag == "DateDesc");
            SetSortCheck(HeaderSortNameAscItem, tag == "NameAsc");
            SetSortCheck(HeaderSortNameDescItem, tag == "NameDesc");
            SetSortCheck(HeaderSortDateAscItem, tag == "DateAsc");
            SetSortCheck(HeaderSortDateDescItem, tag == "DateDesc");
        }

        private static void SetSortCheck(RadioMenuFlyoutItem? item, bool on)
        {
            if (item != null) item.IsChecked = on;
        }

        private void RecentFoldersMenu_Opening(object s, object e)
        {
            if (RecentFoldersMenu != null)
            {
                RecentFoldersMenu.Items.Clear();
                foreach (var p in _recentFolders)
                {
                    var i = new MenuFlyoutItem { Text = p, Icon = new SymbolIcon(Symbol.Folder) };
                    i.Click += async (s2, ev) =>
                    {
                        var text = (s2 as MenuFlyoutItem)?.Text;
                        if (!string.IsNullOrEmpty(text)) await ScanFolder(text);
                    };
                    RecentFoldersMenu.Items.Add(i);
                }
            }
        }

        private void AddFolderToRecent(string p)
        {
            _recentFolders.Remove(p);
            _recentFolders.Insert(0, p);
            if (_recentFolders.Count > 10) _recentFolders.RemoveAt(10);
        }

        private void RootGrid_DragOver(object s, DragEventArgs e) => e.AcceptedOperation = DataPackageOperation.Copy;

        private async void RootGrid_Drop(object s, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0)
                {
                    if (items[0] is StorageFolder folder)
                    {
                        await ScanFolder(folder.Path);
                    }
                    else if (items[0] is StorageFile file)
                    {
                        var parentFolder = await file.GetParentAsync();
                        if (parentFolder != null)
                        {
                            HandleFileActivation(file.Path);
                        }
                    }
                }
            }
        }

        private void ShowInExplorer_Click(object s, RoutedEventArgs e)
        {
            string? path = null;
            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible
                && _currentIndex >= 0 && _currentIndex < Images.Count)
                path = Images[_currentIndex].Path;
            else
            {
                var item = CursorItem();
                if (item != null) path = item.Path;
            }
            if (path == DriveRootPath)
                Process.Start("explorer.exe", "shell:MyComputerFolder");
            else if (!string.IsNullOrEmpty(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }
}
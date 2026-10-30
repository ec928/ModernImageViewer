using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        private TreeViewNode? _pendingNodeToSelect;
        private TreeViewNode? _currentNodeToSelect;
        private int _pendingTargetIndex = 0;
        private FileSystemWatcher? _folderWatcher;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _folderWatchTimer;
        private bool _folderWatchDirty;

        private bool HasSupportedImages(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return false;
            if (ComicArchive.IsComicArchivePath(folderPath)) return File.Exists(folderPath);
            if (!Directory.Exists(folderPath)) return false;
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

            if (filePath.EndsWith(".mivlayout", StringComparison.OrdinalIgnoreCase))
            {
                OpenLayoutFromFilePath(filePath);
                return;
            }

            if (ComicArchive.IsComicArchivePath(filePath))
            {
                await ScanFolder(filePath, openFirstPage: true);
                return;
            }

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
            _listEntriesValid = false;
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
            FocusGrid();
        }

        public async Task ScanFolder(string folderPath, bool openFirstPage = false)
        {
            if (folderPath == DriveRootPath)
            {
                ShowDriveList();
                return;
            }
            bool isComicArchive = ComicArchive.IsComicArchivePath(folderPath) && File.Exists(folderPath);
            if (string.IsNullOrEmpty(folderPath) || (!Directory.Exists(folderPath) && !isComicArchive)) return;

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
            ViewerControl?.SetBookSpreadMode(isComicArchive);

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
                    var subDirs = new List<FolderItem>();
                    if (!isComicArchive)
                    {
                        var dirInfo = new DirectoryInfo(folderPath);
                        var subDirsArr = dirInfo.GetDirectories("*", enumOptions);
                        Array.Sort(subDirsArr, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                        subDirs = subDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();
                    }

                    var parentDir = Directory.GetParent(folderPath);
                    var hopDirs = new List<FolderItem>();

                    if (parentDir != null)
                    {
                        var hopDirsArr = parentDir.GetDirectories("*", enumOptions);
                        Array.Sort(hopDirsArr, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                        hopDirs = hopDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();

                        if (isComicArchive)
                        {
                            var comicFiles = parentDir.GetFiles("*.*", enumOptions)
                                .Where(f => ComicArchive.IsComicArchivePath(f.Name))
                                .ToArray();
                            Array.Sort(comicFiles, (a, b) => NaturalSort.Compare(a.Name, b.Name));
                            foreach (var cf in comicFiles)
                            {
                                hopDirs.Add(new FolderItem
                                {
                                    Name = cf.Name,
                                    Path = cf.FullName,
                                    NodeIcon = Symbol.Library
                                });
                            }
                        }
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
                    else _listEntriesValid = false;
                    if (items.Count > 0 && !isComicArchive) AddFolderToRecent(folderPath);
                    bool returnedToItem = TryPlaceTileCursorOnReturnedItem();
                    bool seeded = false;
                    if (!returnedToItem && items.Count > 0 && (_currentIndex < 0 || _currentIndex >= Images.Count))
                    {
                        _currentIndex = 0;
                        seeded = true;
                    }
                    bool returnedToFolder = false;
                    if (_listMode)
                    {
                        returnedToFolder = TryPlaceListCursorOnReturnedFolder();
                        if (!returnedToFolder) PlaceListCursorOnCurrentImage();
                        SyncTileCursor(scroll: returnedToFolder || returnedToItem);
                    }
                    else if (seeded || returnedToItem)
                        SyncTileCursor(scroll: returnedToItem);
                    _listReturnPath = null;
                    UpdateHeaderFolderInfo();
                    RestartFolderWatcher(isComicArchive ? string.Empty : folderPath);
                    if (openFirstPage && Images.Count > 0)
                        LoadFullImage(0);
                    else
                        FocusGrid();
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
                var dispatcher = this.DispatcherQueue;
                if (ComicArchive.IsComicArchivePath(folderPath))
                {
                    var pages = ComicArchive.ListPages(folderPath);
                    var pageItems = new List<ImageItem>(pages.Count);
                    foreach (var p in pages)
                    {
                        if (scanId != _currentScanId) return new List<ImageItem>();
                        pageItems.Add(new ImageItem
                        {
                            Name = p.Name,
                            Path = ComicArchive.BuildEntryPath(folderPath, p.EntryKey),
                            DateModified = p.LastModified,
                            SizeString = (p.Size / 1024.0 < 1024) ? $"{p.Size / 1024} KB" : $"{(p.Size / 1048576.0):F2} MB",
                            Dispatcher = dispatcher
                        });
                    }
                    return pageItems;
                }

                var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };
                var filtered = new List<FileInfo>();
                foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", enumOptions))
                {
                    if (scanId != _currentScanId) return new List<ImageItem>();
                    string ext = Path.GetExtension(filePath);
                    if (SupportedExtensions.Contains(ext))
                        filtered.Add(new FileInfo(filePath));
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
            FocusGrid();
        }

        private void PopulateDriveRootTree(List<FolderItem> drives, string currentFolderPath, List<FolderItem> subDirs, string countText)
        {
            var parentNode = new TreeViewNode
            {
                Content = new FolderItem { Name = DriveRootName, Path = DriveRootPath, NodeOpacity = 0.75, NodeIcon = Symbol.Up },
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
                        NodeOpacity = 1.0,
                        NodeFontWeight = isCurrent ? "SemiBold" : "Normal",
                        IsCurrent = isCurrent,
                        Subtitle = isCurrent ? countText : string.Empty,
                        SubtitleVisibility = (isCurrent && !string.IsNullOrEmpty(countText)) ? Visibility.Visible : Visibility.Collapsed
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

            _pendingNodeToSelect = nodeToSelect;
            _pendingTargetIndex = targetIndex;
            _ = BringNodeIntoViewAsync(nodeToSelect, targetIndex);
        }

        private void PopulateDriveTree(List<FolderItem> drives)
        {
            if (FolderTreeView == null) return;

            FolderTreeView.RootNodes.Clear();
            var root = new TreeViewNode
            {
                Content = new FolderItem { Name = DriveRootName, Path = DriveRootPath, NodeFontWeight = "SemiBold", IsCurrent = true },
                IsExpanded = true
            };
            foreach (var drive in drives)
                root.Children.Add(new TreeViewNode { Content = new FolderItem { Name = drive.Name, Path = drive.Path } });
            FolderTreeView.RootNodes.Add(root);

            _currentNodeToSelect = root;
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



            string countText = currentImageCount > 0 ? currentImageCount.ToString() : string.Empty;

            if (parentDir == null && IsDriveRoot(currentFolderPath))
            {
                PopulateDriveRootTree(ListDrives(), currentFolderPath, subDirs, countText);
                return;
            }

            if (parentDir != null)
            {
                var parentNode = new TreeViewNode { Content = new FolderItem { Name = parentDir.Name, Path = parentDir.FullName, NodeOpacity = 0.75, NodeIcon = Symbol.Up }, IsExpanded = true };
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
                            NodeIcon = sibling.NodeIcon,
                            NodeOpacity = 1.0,
                            NodeFontWeight = isCurrent ? "SemiBold" : "Normal",
                            IsCurrent = isCurrent,
                            Subtitle = isCurrent ? countText : string.Empty,
                            SubtitleVisibility = (isCurrent && !string.IsNullOrEmpty(countText)) ? Visibility.Visible : Visibility.Collapsed
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
                        NodeFontWeight = "SemiBold",
                        IsCurrent = true,
                        Subtitle = countText,
                        SubtitleVisibility = !string.IsNullOrEmpty(countText) ? Visibility.Visible : Visibility.Collapsed
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
    
                _currentNodeToSelect = nodeToSelect;
                _pendingNodeToSelect = nodeToSelect;
                _pendingTargetIndex = targetIndex;
                _ = BringNodeIntoViewAsync(nodeToSelect, targetIndex);
            }
        }

        private void SidebarView_PaneOpened(SplitView sender, object args)
        {
            FocusSidebarTree();
            if (_pendingNodeToSelect != null)
            {
                _ = BringNodeIntoViewAsync(_pendingNodeToSelect, _pendingTargetIndex);
            }
        }

        private async Task BringNodeIntoViewAsync(TreeViewNode nodeToSelect, int targetIndex)
        {
            if (FolderTreeView == null || nodeToSelect == null) return;

            _pendingNodeToSelect = nodeToSelect;
            _pendingTargetIndex = targetIndex;

            if (SidebarView != null && !SidebarView.IsPaneOpen) return;

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
                        if (SidebarView != null && SidebarView.IsPaneOpen && container is Control c) c.Focus(FocusState.Programmatic);
                        success = true;
                    }
                    else
                    {
                        var sv = FindScrollViewer(FolderTreeView);
                        if (sv != null)
                        {
                            double estimatedOffset = targetIndex * 26.0;
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
            string breadcrumbPath = ComicArchive.IsComicArchivePath(path)
                ? (Path.GetDirectoryName(path) ?? path)
                : path;
            if (!string.IsNullOrEmpty(breadcrumbPath))
            {
                foreach (var part in breadcrumbPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
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
            FocusGrid();
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
            if (string.IsNullOrEmpty(_currentDirectory) || (!Directory.Exists(_currentDirectory) && !ComicArchive.IsComicArchivePath(_currentDirectory))) return;
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
            SaveAllSettings();
            FocusGrid();
        }

        public void FocusSidebarTree()
        {
            if (FolderTreeView == null) return;
            TreeViewNode? targetNode = _currentNodeToSelect ?? (FolderTreeView.RootNodes.Count > 0 ? (FindCurrentTreeNode(FolderTreeView.RootNodes) ?? FolderTreeView.RootNodes[0]) : null);
            if (targetNode != null)
            {
                if (FolderTreeView.ContainerFromNode(targetNode) is Control c)
                {
                    c.Focus(FocusState.Programmatic);
                    return;
                }
                else
                {
                    _ = BringNodeIntoViewAsync(targetNode, _pendingTargetIndex);
                }
            }

            FolderTreeView.Focus(FocusState.Programmatic);
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (SidebarView == null || !SidebarView.IsPaneOpen || FolderTreeView == null) return;
                TreeViewNode? node = _currentNodeToSelect ?? (FolderTreeView.RootNodes.Count > 0 ? (FindCurrentTreeNode(FolderTreeView.RootNodes) ?? FolderTreeView.RootNodes[0]) : null);
                if (node != null && FolderTreeView.ContainerFromNode(node) is Control ctrl)
                {
                    ctrl.Focus(FocusState.Programmatic);
                }
            });
        }

        private static TreeViewNode? FindCurrentTreeNode(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Content is FolderItem fi && fi.IsCurrent) return n;
                var child = FindCurrentTreeNode(n.Children);
                if (child != null) return child;
            }
            return null;
        }

        private async Task ActivateFolderNodeAsync(TreeViewNode n)
        {
            string path = (n.Content is FolderItem fi) ? fi.Path : "";
            if (path == DriveRootPath)
            {
                if (SidebarView != null) SidebarView.IsPaneOpen = false;
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

        private async void FolderTreeView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space)
            {
                var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
                TreeViewNode? targetNode = null;
                if (focused is TreeViewItem tvi)
                {
                    targetNode = FolderTreeView.NodeFromContainer(tvi);
                }
                else if (focused is DependencyObject dep)
                {
                    var parentTvi = FindVisualParent<TreeViewItem>(dep);
                    if (parentTvi != null) targetNode = FolderTreeView.NodeFromContainer(parentTvi);
                }

                if (targetNode != null)
                {
                    e.Handled = true;
                    await ActivateFolderNodeAsync(targetNode);
                    return;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Escape || e.Key == Windows.System.VirtualKey.Tab)
            {
                if (SidebarView != null) SidebarView.IsPaneOpen = false;
                FocusGrid();
                e.Handled = true;
                return;
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private async void FolderTreeView_ItemInvoked(TreeView s, TreeViewItemInvokedEventArgs a)
        {
            if (a.InvokedItem is TreeViewNode n)
            {
                await ActivateFolderNodeAsync(n);
            }
        }

        private void Sort_Click(object s, RoutedEventArgs e)
        {
            if (s is MenuFlyoutItem item && item.Tag is string tag)
            {
                var currentItem = (_currentIndex >= 0 && _currentIndex < Images.Count) ? Images[_currentIndex] : null;
                string? currentDirCursorPath = (_listMode && _listCursor >= 0 && _listCursor < _listEntries.Count && _listEntries[_listCursor].IsDirectory)
                    ? _listEntries[_listCursor].Path
                    : null;

                var sorted = tag.StartsWith("Name") ?
                    (tag.EndsWith("Asc")
                        ? Images.OrderBy(x => x.Name, NaturalSort.Comparer)
                        : Images.OrderByDescending(x => x.Name, NaturalSort.Comparer)) :
                    (tag.EndsWith("Asc") ? Images.OrderBy(x => x.DateModified) : Images.OrderByDescending(x => x.DateModified));

                var sortedList = sorted.ToList();
                Images.ResetWith(sortedList);

                if (currentItem != null) _currentIndex = Images.IndexOf(currentItem);
                if (_listMode)
                {
                    RebuildListEntries();
                    if (currentDirCursorPath != null)
                    {
                        int dirIdx = -1;
                        for (int i = 0; i < _listEntries.Count; i++)
                        {
                            if (_listEntries[i].IsDirectory && string.Equals(_listEntries[i].Path, currentDirCursorPath, StringComparison.OrdinalIgnoreCase))
                            {
                                dirIdx = i;
                                break;
                            }
                        }
                        if (dirIdx >= 0) _listCursor = dirIdx;
                        else PlaceListCursorOnCurrentImage();
                    }
                    else
                    {
                        PlaceListCursorOnCurrentImage();
                    }
                }
                else
                {
                    _listEntriesValid = false;
                }
                SyncTileCursor(scroll: true);

                _currentSettings.SortOrder = tag;
                SyncSortChecks(tag);
                SaveAllSettings();
                FocusGrid();
            }
        }

        private void HeaderSortFlyout_Opening(object sender, object e) => SyncSortChecks(_currentSettings.SortOrder);

        private void SyncSortChecks(string tag)
        {
            if (tag != "NameDesc" && tag != "DateDesc" && tag != "DateAsc") tag = "NameAsc";
            SetSortCheck(HeaderSortNameAscItem, tag == "NameAsc");
            SetSortCheck(HeaderSortNameDescItem, tag == "NameDesc");
            SetSortCheck(HeaderSortDateAscItem, tag == "DateAsc");
            SetSortCheck(HeaderSortDateDescItem, tag == "DateDesc");
        }

        private static void SetSortCheck(RadioMenuFlyoutItem? item, bool on)
        {
            if (item != null) item.IsChecked = on;
        }

        private const int MaxRecentFolders = 10;
        private const int MaxFavouriteFolders = 10;

        private Grid? _folderMenuHoverRow;
        private readonly List<Action> _folderMenuStarSyncs = [];

        private void RecentFoldersMenu_Opening(object s, object e) => BuildRecentFoldersMenu(null, FocusState.Programmatic);

        // Favourites on top (filled star, always shown), then recent folders (outline star on hover or keyboard focus).
        // A favourite is not listed again under Recent. Starring rebuilds the menu in place, so it stays open.
        private void BuildRecentFoldersMenu(string? focusStarFor, FocusState focusHow)
        {
            if (RecentFoldersMenu == null) return;
            _folderMenuHoverRow = null;
            _folderMenuStarSyncs.Clear();
            var panel = new StackPanel { MinWidth = 280, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled };
            var recents = _recentFolders.Where(p => !IsFavouriteFolder(p)).Take(MaxRecentFolders).ToList();
            Button? focusTarget = null;

            if (_favouriteFolders.Count > 0)
            {
                panel.Children.Add(FolderMenuHeader(_favouriteFolders.Count >= MaxFavouriteFolders
                    ? $"Favourites ({MaxFavouriteFolders} of {MaxFavouriteFolders})"
                    : "Favourites"));
                foreach (var p in _favouriteFolders)
                    panel.Children.Add(FolderMenuRow(p, favourite: true, focusStarFor, ref focusTarget));
                panel.Children.Add(FolderMenuDivider());
                panel.Children.Add(FolderMenuHeader("Recent"));
            }

            if (recents.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No recent folders",
                    Style = (Style)RootGrid.Resources["FolderMenuEmptyTextStyle"],
                });
            }
            else
            {
                foreach (var p in recents)
                    panel.Children.Add(FolderMenuRow(p, favourite: false, focusStarFor, ref focusTarget));
                panel.Children.Add(FolderMenuDivider());
                var clear = FolderMenuButton(new SymbolIcon(Symbol.Delete), "Clear Recent Folders", bold: false);
                clear.Click += (s2, ev) =>
                {
                    RecentFoldersMenu.Hide();
                    ClearRecentFolders_Click(s2, ev);
                };
                panel.Children.Add(clear);
            }

            RecentFoldersMenu.Content = panel;
            if (focusTarget != null)
                focusTarget.Loaded += (s2, ev) => focusTarget.Focus(focusHow);
        }

        private void SetFolderMenuHover(Grid? row)
        {
            _folderMenuHoverRow = row;
            foreach (var sync in _folderMenuStarSyncs) sync();
        }

        private Grid FolderMenuRow(string path, bool favourite, string? focusStarFor, ref Button? focusTarget)
        {
            bool available = Directory.Exists(path) || (ComicArchive.IsComicArchivePath(path) && File.Exists(path));
            bool isComic = ComicArchive.IsComicArchivePath(path);
            bool current = string.Equals(path, _currentDirectory, StringComparison.OrdinalIgnoreCase);

            IconElement rowIcon = isComic ? new SymbolIcon(Symbol.Library) : new SymbolIcon(Symbol.Folder);
            var open = FolderMenuButton(rowIcon, available ? path : path + "  (not available)", bold: current);
            open.IsEnabled = available;
            open.Click += async (s2, ev) =>
            {
                RecentFoldersMenu.Hide();
                await ScanFolder(path);
            };

            bool full = !favourite && _favouriteFolders.Count >= MaxFavouriteFolders;
            var starIcon = new FontIcon
            {
                Style = (Style)RootGrid.Resources[favourite ? "FolderMenuStarFilledIconStyle" : "FolderMenuStarEmptyIconStyle"]
            };

            var star = new Button
            {
                Style = (Style)RootGrid.Resources["FolderMenuStarButtonStyle"],
                Content = starIcon,
                IsEnabled = !full,
            };
            string starTip = favourite
                ? "Remove from favourites"
                : (full ? $"Favourites limit reached ({MaxFavouriteFolders} of {MaxFavouriteFolders})" : "Add to favourites");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(star, (favourite ? "Remove from favourites: " : "Add to favourites: ") + path);
            star.Click += (s2, ev) =>
            {
                // Keep keyboard users on the moved star; after a click, focus moves quietly so no star lights up.
                var how = star.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
                ToggleFavouriteFolder(path);
                BuildRecentFoldersMenu(path, how);
            };

            var starContainer = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            starContainer.Children.Add(star);
            ToolTipService.SetToolTip(starContainer, starTip);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(starContainer, 1);
            row.Children.Add(open);
            row.Children.Add(starContainer);

            // One hovered row at a time for the whole menu, so a missed PointerExited cannot leave a second star lit.
            row.PointerEntered += (s2, ev) => SetFolderMenuHover(row);
            row.PointerExited += (s2, ev) => { if (ReferenceEquals(_folderMenuHoverRow, row)) SetFolderMenuHover(null); };
            if (!favourite)
            {
                // The outline star shows on the row under the pointer, or on the row with keyboard focus --
                // not on focus the menu sets itself when it opens or rebuilds.
                void Sync() => star.Opacity = ReferenceEquals(_folderMenuHoverRow, row)
                    || open.FocusState == FocusState.Keyboard || star.FocusState == FocusState.Keyboard ? 1 : 0;
                star.Opacity = 0;
                _folderMenuStarSyncs.Add(Sync);
                open.GotFocus += (s2, ev) => Sync();
                open.LostFocus += (s2, ev) => Sync();
                star.GotFocus += (s2, ev) => Sync();
                star.LostFocus += (s2, ev) => Sync();
            }

            if (focusStarFor != null && string.Equals(path, focusStarFor, StringComparison.OrdinalIgnoreCase))
                focusTarget = star;
            return row;
        }

        private Button FolderMenuButton(IconElement icon, string text, bool bold)
        {
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            ToolTipService.SetToolTip(label, text);
            Grid.SetColumn(label, 1);
            content.Children.Add(icon);
            content.Children.Add(label);
            var button = new Button
            {
                Style = (Style)RootGrid.Resources["FolderMenuRowButtonStyle"],
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(11, 8, 11, 8),
            };
            ToolTipService.SetToolTip(button, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
            return button;
        }

        private TextBlock FolderMenuHeader(string text) => new()
        {
            Text = text,
            Style = (Style)RootGrid.Resources["FolderMenuHeaderTextStyle"],
        };

        private Microsoft.UI.Xaml.Shapes.Rectangle FolderMenuDivider() => new()
        {
            Style = (Style)RootGrid.Resources["FolderMenuDividerStyle"],
        };

        public bool IsFavouriteFolder(string path) =>
            _favouriteFolders.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        public void ToggleFavouriteFolder(string path, int maxLimit = 10)
        {
            int i = _favouriteFolders.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _favouriteFolders.RemoveAt(i);
            else if (_favouriteFolders.Count < maxLimit) _favouriteFolders.Add(path);
            else return;
            SaveAllSettings();
        }

        private void AddFolderToRecent(string p)
        {
            _recentFolders.Remove(p);
            _recentFolders.Insert(0, p);
            // Favourites stay in the history (so unstarring puts them back) but do not count towards the cap.
            int kept = 0;
            for (int i = 0; i < _recentFolders.Count; i++)
            {
                if (IsFavouriteFolder(_recentFolders[i])) continue;
                if (++kept > MaxRecentFolders) _recentFolders.RemoveAt(i--);
            }
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
                        if (file.Path.EndsWith(".mivlayout", StringComparison.OrdinalIgnoreCase))
                        {
                            OpenLayoutFromFilePath(file.Path);
                            return;
                        }
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
            {
                if (ComicArchive.TrySplitEntryPath(path, out string archivePath, out _))
                    path = archivePath;
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            FocusGrid();
        }
    }
}

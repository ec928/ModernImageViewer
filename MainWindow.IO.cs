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
                if (App.GlobalImageCache.TryGetValue(filePath, out var existing)) existing.Release();
                App.GlobalImageCache[filePath] = fastEntry;

                TryDisposeRawGpuBitmap();
                _rawGpuBitmap = fastEntry.GpuBitmap;
                _rawGpuProfile = fastEntry.Profile;
                _logicalImageWidth = fastEntry.NativeWidth;
                _logicalImageHeight = fastEntry.NativeHeight;
                _currentRenderedItem = phantomItem;
                _isHighFidelityActive = fastEntry.IsHighFidelity;

                ViewerControl.InjectImage(fastEntry, true);

                if (!_isHighFidelityActive) _hfPromotionTimer?.Start();
            }

            await ScanFolder(folderPath);

            var targetItem = Images.FirstOrDefault(x => x.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (targetItem != null)
            {
                _currentIndex = Images.IndexOf(targetItem);
                _currentRenderedItem = targetItem;
                ViewerControl.TargetImage = targetItem;
                ViewerControl.ShowHud();

                _exifCts?.Cancel();
                _exifCts?.Dispose();
                _exifCts = new CancellationTokenSource();
                _ = targetItem.LoadExifAsync(_exifCts.Token);

                ManageCache(_currentIndex, 1);
            }
            else
            {
                _currentIndex = -1;
                ClosePreviewInternal();
            }

            _isStartupIndexing = false;
        }

        private async Task UpdateNavigationTreeOnlyAsync(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            int myScanId = ++_currentScanId;
            _currentDirectory = folderPath;

            UpdateBreadcrumbs(folderPath);

            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };

            var dirTask = Task.Run(() =>
            {
                var dirInfo = new DirectoryInfo(folderPath);

                var subDirsArr = dirInfo.GetDirectories("*", enumOptions);
                Array.Sort(subDirsArr, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                var subDirs = subDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();

                var parentDir = Directory.GetParent(folderPath);
                var hopDirs = new List<FolderItem>();

                if (parentDir != null)
                {
                    var hopDirsArr = parentDir.GetDirectories("*", enumOptions);
                    Array.Sort(hopDirsArr, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    hopDirs = hopDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();
                }

                return new { SubDirs = subDirs, HopDirs = hopDirs, Parent = parentDir };
            });

            var folderData = await dirTask;

            if (myScanId != _currentScanId) return;

            _hopFolders = folderData.HopDirs;
            PopulateTreeView(folderData.Parent, folderData.HopDirs, folderData.SubDirs, folderPath, 0);
        }

        private async Task ScanFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible && !_isStartupIndexing)
            {
                ClosePreviewInternal();
            }
            if (!_isStartupIndexing)
            {
                _currentIndex = -1;
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
            _currentDirectory = folderPath;

            UpdateBreadcrumbs(folderPath);

            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Visible;
            if (LoadingRing != null) LoadingRing.IsActive = true;

            foreach (var item in Images)
            {
                item.ClearThumbnail();
            }
            Images.Clear();

            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };

            try
            {
                var dirTask = Task.Run(() =>
                {
                    var dirInfo = new DirectoryInfo(folderPath);

                    var subDirsArr = dirInfo.GetDirectories("*", enumOptions);
                    Array.Sort(subDirsArr, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    var subDirs = subDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();

                    var parentDir = Directory.GetParent(folderPath);
                    var hopDirs = new List<FolderItem>();

                    if (parentDir != null)
                    {
                        var hopDirsArr = parentDir.GetDirectories("*", enumOptions);
                        Array.Sort(hopDirsArr, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        hopDirs = hopDirsArr.Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).ToList();
                    }

                    return new { SubDirs = subDirs, HopDirs = hopDirs, Parent = parentDir };
                });

                var countTask = Task.Run(() =>
                {
                    try
                    {
                        return Directory.EnumerateFiles(folderPath, "*.*", enumOptions)
                            .Count(f => SupportedExtensions.Contains(Path.GetExtension(f)));
                    }
                    catch { return 0; }
                });

                await Task.WhenAll(dirTask, countTask);

                if (myScanId != _currentScanId) return;

                var folderData = dirTask.Result;
                int imageCount = countTask.Result;

                _hopFolders = folderData.HopDirs;
                PopulateTreeView(folderData.Parent, folderData.HopDirs, folderData.SubDirs, folderPath, imageCount);

                await Task.Run(() =>
                {
                    var dirInfo = new DirectoryInfo(folderPath);

                    var allFiles = dirInfo.GetFiles("*.*", enumOptions);
                    var filteredFiles = new List<FileInfo>(allFiles.Length);

                    foreach (var fi in allFiles)
                    {
                        if (myScanId != _currentScanId) return;
                        if (SupportedExtensions.Contains(fi.Extension))
                        {
                            filteredFiles.Add(fi);
                        }
                    }

                    filteredFiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                    int batchSize = 100;
                    var batch = new List<ImageItem>(batchSize);

                    foreach (var fi in filteredFiles)
                    {
                        if (myScanId != _currentScanId) break;

                        batch.Add(new ImageItem
                        {
                            Name = fi.Name,
                            Path = fi.FullName,
                            DateModified = fi.LastWriteTime,
                            SizeString = (fi.Length / 1024.0 < 1024) ? $"{fi.Length / 1024} KB" : $"{(fi.Length / 1048576.0):F2} MB",
                            Dispatcher = this.DispatcherQueue
                        });

                        if (batch.Count >= batchSize)
                        {
                            var yieldBatch = batch.ToList();
                            batch.Clear();
                            DispatcherQueue.TryEnqueue(() => { Images.AddRange(yieldBatch); });
                        }
                    }

                    if (batch.Count > 0 && myScanId == _currentScanId)
                    {
                        var yieldBatch = batch.ToList();
                        DispatcherQueue.TryEnqueue(() => { Images.AddRange(yieldBatch); });
                    }
                });

                if (myScanId == _currentScanId)
                {
                    if (Images.Count > 0) AddFolderToRecent(folderPath);
                }
            }
            finally
            {
                if (myScanId == _currentScanId)
                {
                    _isScanning = false;
                    if (LoadingRing != null) LoadingRing.IsActive = false;
                    if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
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
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var part in path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    _breadcrumbs.Add(part);
                }
            }
        }

        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar s, BreadcrumbBarItemClickedEventArgs a)
        {
            if (_breadcrumbs.Count == 0) return;
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
            var parent = Directory.GetParent(_currentDirectory);
            if (parent != null) await ScanFolder(parent.FullName);
        }

        private async void RefreshFolder_Click(object s, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentDirectory) && Directory.Exists(_currentDirectory))
            {
                await ScanFolder(_currentDirectory);
            }
        }
        private void ClearRecentFolders_Click(object sender, RoutedEventArgs e)
        {
            _recentFolders.Clear();
        }

        private async void FolderTreeView_ItemInvoked(TreeView s, TreeViewItemInvokedEventArgs a)
        {
            string path = (a.InvokedItem is TreeViewNode n && n.Content is FolderItem fi) ? fi.Path : "";
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
                    (tag.EndsWith("Asc") ? Images.OrderBy(x => x.Name) : Images.OrderByDescending(x => x.Name)) :
                    (tag.EndsWith("Asc") ? Images.OrderBy(x => x.DateModified) : Images.OrderByDescending(x => x.DateModified));

                var sortedList = sorted.ToList();
                Images.Clear();
                Images.AddRange(sortedList);

                if (currentItem != null) _currentIndex = Images.IndexOf(currentItem);
            }
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
            if (_currentIndex >= 0 && _currentIndex < Images.Count)
            {
                Process.Start("explorer.exe", $"/select,\"{Images[_currentIndex].Path}\"");
            }
        }
    }
}
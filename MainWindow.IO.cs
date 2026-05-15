using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ModernImageViewer
{
    public sealed partial class MainWindow
    {
        public async void HandleFileActivation(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            string folderPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(folderPath)) return;

            await ScanFolder(folderPath);
            await TryLoadTargetImageAsync(filePath);
        }

        private async Task TryLoadTargetImageAsync(string filePath)
        {
            ImageItem? targetItem = null;
            int retries = 0;

            while (retries < 20 && targetItem == null)
            {
                targetItem = Images.FirstOrDefault(x => x.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (targetItem == null) await Task.Delay(100);
                retries++;
            }

            if (targetItem != null) LoadFullImage(Images.IndexOf(targetItem));
        }

        private async Task ScanFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            ClearImageCache();
            int myScanId = ++_currentScanId;
            _isScanning = true;
            _currentDirectory = folderPath;

            UpdateBreadcrumbs(folderPath);

            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Visible;
            if (LoadingRing != null) LoadingRing.IsActive = true;
            if (EmptyStateBorder != null) EmptyStateBorder.Visibility = Visibility.Collapsed;
            Images.Clear();

            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true };

            try
            {
                var folderData = await Task.Run(() =>
                {
                    var dirInfo = new DirectoryInfo(folderPath);
                    var subDirs = dirInfo.GetDirectories("*", enumOptions).Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).OrderBy(f => f.Name).ToList();
                    var parentDir = Directory.GetParent(folderPath);
                    var hopDirs = parentDir != null ? parentDir.GetDirectories("*", enumOptions).Select(d => new FolderItem { Name = d.Name, Path = d.FullName }).OrderBy(f => f.Name).ToList() : new List<FolderItem>();
                    return new { SubDirs = subDirs, HopDirs = hopDirs, Parent = parentDir };
                });

                if (myScanId != _currentScanId) return;

                _hopFolders = folderData.HopDirs;
                PopulateTreeView(folderData.Parent, folderData.HopDirs, folderData.SubDirs, folderPath);

                await Task.Run(() =>
                {
                    var dirInfo = new DirectoryInfo(folderPath);
                    var filesQuery = dirInfo.EnumerateFiles("*.*", enumOptions).Where(fi => SupportedExtensions.Contains(fi.Extension)).OrderBy(fi => fi.Name);

                    int batchSize = 100;
                    var batch = new List<ImageItem>(batchSize);

                    foreach (var fi in filesQuery)
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
                    if (EmptyStateBorder != null) EmptyStateBorder.Visibility = Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        private void PopulateTreeView(DirectoryInfo? parentDir, List<FolderItem> hopDirs, List<FolderItem> subDirs, string currentFolderPath)
        {
            if (FolderTreeView == null) return;
            FolderTreeView.RootNodes.Clear();
            TreeViewNode? nodeToSelect = null;

            if (parentDir != null)
            {
                var parentNode = new TreeViewNode { Content = new FolderItem { Name = parentDir.Name, Path = parentDir.FullName, NodeOpacity = 0.5, NodeIcon = Symbol.Up }, IsExpanded = true };
                foreach (var sibling in hopDirs)
                {
                    bool isCurrent = sibling.Path.Equals(currentFolderPath, StringComparison.OrdinalIgnoreCase);
                    var siblingNode = new TreeViewNode { Content = new FolderItem { Name = sibling.Name, Path = sibling.Path, NodeOpacity = isCurrent ? 1.0 : 0.5, NodeFontWeight = isCurrent ? "Bold" : "Normal" } };

                    if (isCurrent)
                    {
                        siblingNode.IsExpanded = true;
                        nodeToSelect = siblingNode;
                        foreach (var sub in subDirs) siblingNode.Children.Add(new TreeViewNode { Content = new FolderItem { Name = sub.Name, Path = sub.Path } });
                    }
                    parentNode.Children.Add(siblingNode);
                }
                FolderTreeView.RootNodes.Add(parentNode);
            }
            else
            {
                var rootNode = new TreeViewNode { Content = new FolderItem { Name = currentFolderPath, Path = currentFolderPath, NodeFontWeight = "Bold" }, IsExpanded = true };
                nodeToSelect = rootNode;

                foreach (var sub in subDirs) rootNode.Children.Add(new TreeViewNode { Content = new FolderItem { Name = sub.Name, Path = sub.Path } });
                FolderTreeView.RootNodes.Add(rootNode);
            }

            if (nodeToSelect != null)
            {
                FolderTreeView.SelectedNode = nodeToSelect;
                _ = BringNodeIntoViewAsync(nodeToSelect);
            }
        }

        private async Task BringNodeIntoViewAsync(TreeViewNode nodeToSelect)
        {
            for (int i = 0; i < 10; i++)
            {
                bool success = false;
                var tcs = new TaskCompletionSource<bool>();
                DispatcherQueue.TryEnqueue(() =>
                {
                    FolderTreeView.UpdateLayout();
                    if (FolderTreeView.ContainerFromNode(nodeToSelect) is UIElement container)
                    {
                        container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5 });
                        success = true;
                    }
                    tcs.TrySetResult(success);
                });

                if (await tcs.Task) return;
                await Task.Delay(50);
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

            if (SidebarView != null) SidebarView.IsPaneOpen = false;
            await ScanFolder(newPath);
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

        private async void FolderTreeView_ItemInvoked(TreeView s, TreeViewItemInvokedEventArgs a)
        {
            string path = (a.InvokedItem is TreeViewNode n && n.Content is FolderItem fi) ? fi.Path : "";
            if (!string.IsNullOrEmpty(path))
            {
                if (SidebarView != null) SidebarView.IsPaneOpen = false;
                await ScanFolder(path);
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
                            await ScanFolder(parentFolder.Path);
                            await TryLoadTargetImageAsync(file.Path);
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
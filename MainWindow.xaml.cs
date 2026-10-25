using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using ModernImageViewer.Collage;

namespace ModernImageViewer
{
    public enum DetachLimit
    {
        CurrentZoom = 0,
        Target1080p = 1,
        Target1200p = 2,
        Target800p = 3
    }

    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        public static MainWindow? Instance { get; private set; }

        public string? StartupFilePath { get; set; }
        public Dictionary<string, int> DetachedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DetachLimit CurrentDetachLimit = DetachLimit.CurrentZoom;
        public string ImageEditorPath { get; set; } = "mspaint.exe";

        private ObservableRangeCollection<ImageItem> _images = [];
        public ObservableRangeCollection<ImageItem> Images
        {
            get => _images;
            set { _images = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly AppWindow _appWindow;
        public new AppWindow AppWindow => _appWindow;
        private AppSettings _currentSettings = new AppSettings();
        private readonly IntPtr _hWnd;
        private readonly KeyEventHandler _globalKeyDownHandler;
        private readonly KeyEventHandler _globalKeyUpHandler;
        // True only while the sidebar is open *because* Alt is held, so releasing
        // Alt never closes a pane the user opened by hover or by clicking.
        private bool _altPeekOpenedSidebar;

        private List<FolderItem> _hopFolders = [];
        private readonly ObservableCollection<string> _breadcrumbs = [];

        private int _currentIndex = -1;
        private ImageItem? _currentRenderedItem;

        private DispatcherTimer? _slideshowTimer;
        private DispatcherTimer? _hfPromotionTimer;
        private CancellationTokenSource? _hfCts;
        private CancellationTokenSource? _fastDecodeCts;
        private CancellationTokenSource? _exifCts;

        private bool _isFullscreen = false;
        private int _currentImageLoadId = 0, _currentScanId = 0;
        private bool _isScanning = false;
        private bool _isStartupIndexing = false;

        private string _currentDirectory = string.Empty;
        private DateTime _lastNavigationTime = DateTime.MinValue;

        private float _currentGamma = 2.2f;
        private bool _isHighFidelityActive = false;
        private int _promoteEdge;

        private double _logicalImageWidth = 1;
        private double _logicalImageHeight = 1;

        private readonly HashSet<string> _activeCachePaths = [];
        private readonly Dictionary<string, CancellationTokenSource> _activeDecodes = new(StringComparer.OrdinalIgnoreCase);

        private CanvasDevice? _canvasDevice;
        private CanvasBitmap? _rawGpuBitmap;
        private ColorManagementProfile? _rawGpuProfile;

        private ItemsWrapGrid? _imageWrapGrid;
        private readonly List<string> _recentFolders = [];

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif"
        };

        private readonly string _settingsPath = Path.Combine(AppPaths.LocalDirectory, "settings.json");

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();
            this.SystemBackdrop = new MicaBackdrop();

            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(DragZone);

            _canvasDevice = CanvasDevice.GetSharedDevice();

            this.Closed += (s, e) =>
            {
                RootGrid.RemoveHandler(UIElement.KeyDownEvent, _globalKeyDownHandler);
                RootGrid.RemoveHandler(UIElement.KeyUpEvent, _globalKeyUpHandler);
                Instance = null;
                SaveAllSettings();
                StopFolderWatcher();
                _hfPromotionTimer?.Stop();
                _hfCts?.Cancel();
                _hfCts?.Dispose();
                _fastDecodeCts?.Cancel();
                _fastDecodeCts?.Dispose();
                _exifCts?.Cancel();
                _exifCts?.Dispose();
                ClearImageCache();

                TryDisposeRawGpuBitmap();
            };

            _globalKeyDownHandler = new KeyEventHandler(Global_KeyDown);
            RootGrid.AddHandler(UIElement.KeyDownEvent, _globalKeyDownHandler, true);
            _globalKeyUpHandler = new KeyEventHandler(Global_KeyUp);
            RootGrid.AddHandler(UIElement.KeyUpEvent, _globalKeyUpHandler, true);

            // Alt+Tab steals focus while Alt is still down, so the KeyUp that would end an
            // Alt peek never arrives. Deactivation is the only reliable end of that gesture.
            this.Activated += MainWindow_Activated;
            RootGrid.PointerMoved += RootGrid_PointerMoved;
            FolderBreadcrumb.ItemsSource = _breadcrumbs;

            _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _slideshowTimer.Tick += (s, e) => Navigate(1);

            _hfPromotionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _hfPromotionTimer.Tick += async (s, e) =>
            {
                _hfPromotionTimer?.Stop();
                try
                {
                    await PromoteDisplayedAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HF Promotion Error] Exception caught by shield: {ex.Message}");
                }
            };

            ConfigureWindow(false);

            RootGrid.Loaded += async (s, e) =>
            {
                if (ViewerControl != null)
                {
                    ViewerControl.SetGammaLevel(_currentGamma);
                    ViewerControl.SetDetachLimitState((int)CurrentDetachLimit);
                    ViewerControl.GammaChanged += (_, gamma) => ApplyGamma(gamma);
                    ViewerControl.ZoomChanged += (_, _) => QueueSharperDecode();
                }

                if (!string.IsNullOrEmpty(StartupFilePath) && File.Exists(StartupFilePath))
                {
                    HandleFileActivation(StartupFilePath);
                }
                else
                {
                    await ScanFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                }
            };
        }

        /// <summary>
        /// Shows the About dialog. Most users reach the app through a Releases zip and never
        /// see the repository, so what the app is and how to report a problem both have to be
        /// reachable from inside the app.
        /// </summary>
        private async void About_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new AboutDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = ElementTheme.Dark
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // async void: ShowAsync throws if a dialog is already open, and
                // App_UnhandledException sets Handled = false, so this must not escape.
                Debug.WriteLine($"[About] Could not show dialog: {ex.Message}");
            }
        }

        public void RegisterDetachedWindow(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (DetachedPaths.TryGetValue(path, out int count)) DetachedPaths[path] = count + 1;
            else DetachedPaths[path] = 1;
        }

        public void UnregisterDetachedWindow(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (DetachedPaths.TryGetValue(path, out int count))
            {
                if (count <= 1) DetachedPaths.Remove(path);
                else DetachedPaths[path] = count - 1;
            }
            ManageCache(_currentIndex, 0);
        }

        public event Action<float>? GammaApplied;

        public void ApplyGamma(float gamma)
        {
            if (Math.Abs(_currentGamma - gamma) < 0.01f) return;
            _currentGamma = gamma;
            ViewerControl?.SetGammaLevel(gamma);
            GammaApplied?.Invoke(gamma);
            SaveAllSettings();
        }

        private sealed class AppSettings
        {
            public int Version { get; set; } = 1;
            public int WindowWidth { get; set; } = -1;
            public int WindowHeight { get; set; } = -1;
            public int WindowX { get; set; } = -1;
            public int WindowY { get; set; } = -1;
            public int DirectorWindowWidth { get; set; } = -1;
            public int DirectorWindowHeight { get; set; } = -1;
            public int DirectorWindowX { get; set; } = -1;
            public int DirectorWindowY { get; set; } = -1;
            public float Gamma { get; set; } = 2.2f;
            public string ImageEditorPath { get; set; } = "mspaint.exe";
            public string VideoDirectorPath { get; set; } = string.Empty;
            public string SortOrder { get; set; } = "NameAsc";
            public List<string> RecentFolders { get; set; } = new();
        }

        private void ConfigureWindow(bool forceReset)
        {
            AppSettings settings = new AppSettings();

            // Always attempt to load existing settings to preserve RecentFolders and Gamma
            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsPath);
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) settings = loaded;
                }
                catch { /* use defaults */ }
            }

            // Explicitly target window dimensions for reset, leaving arrays and user preferences intact
            if (forceReset)
            {
                settings.WindowWidth = -1;
                settings.WindowHeight = -1;
                settings.WindowX = -1;
                settings.WindowY = -1;
                
                settings.DirectorWindowWidth = -1;
                settings.DirectorWindowHeight = -1;
                settings.DirectorWindowX = -1;
                settings.DirectorWindowY = -1;

                try
                {
                    string cinematicBoundsPath = Path.Combine(AppPaths.LocalDirectory, "WindowBounds.json");
                    if (File.Exists(cinematicBoundsPath))
                    {
                        File.Delete(cinematicBoundsPath);
                    }
                }
                catch { }
            }

            if (settings.WindowWidth == -1 || settings.WindowHeight == -1 || settings.DirectorWindowWidth == -1 || settings.DirectorWindowHeight == -1)
            {
                try
                {
                    var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                    int targetHeight = (int)(displayArea.WorkArea.Height * 0.9375);
                    
                    if (settings.DirectorWindowWidth == -1 || settings.DirectorWindowHeight == -1)
                    {
                        int dirTargetWidth = (int)(targetHeight * (16.0 / 9.0));
                        settings.DirectorWindowWidth = dirTargetWidth;
                        settings.DirectorWindowHeight = targetHeight;
                    }
                    
                    if (settings.WindowWidth == -1 || settings.WindowHeight == -1)
                    {
                        int mainTargetWidth = (int)(targetHeight * 0.95);
                        settings.WindowWidth = mainTargetWidth;
                        settings.WindowHeight = targetHeight;
                    }
                }
                catch
                {
                    if (settings.DirectorWindowWidth == -1) settings.DirectorWindowWidth = 2400;
                    if (settings.DirectorWindowHeight == -1) settings.DirectorWindowHeight = 1350;
                    if (settings.WindowWidth == -1) settings.WindowWidth = 1282;
                    if (settings.WindowHeight == -1) settings.WindowHeight = 1350;
                }
            }

            _currentSettings = settings;

            int w = settings.WindowWidth < 300 ? 1280 : settings.WindowWidth;
            int h = settings.WindowHeight < 300 ? 1350 : settings.WindowHeight;

            _appWindow?.Resize(new Windows.Graphics.SizeInt32(w, h));

            if (settings.WindowX != -1 && settings.WindowY != -1)
                _appWindow?.Move(new Windows.Graphics.PointInt32(settings.WindowX, settings.WindowY));

            if (settings.SortOrder == "NameDesc") SortNameDescItem.IsChecked = true;
            else if (settings.SortOrder == "DateDesc") SortDateDescItem.IsChecked = true;
            else if (settings.SortOrder == "DateAsc") SortDateAscItem.IsChecked = true;
            else SortNameAscItem.IsChecked = true;

            _currentGamma = settings.Gamma;
            ImageEditorPath = settings.ImageEditorPath;

            _recentFolders.Clear();
            foreach (var folder in settings.RecentFolders)
            {
                if (Directory.Exists(folder) && !_recentFolders.Contains(folder))
                    _recentFolders.Add(folder);
            }

            if (ViewerControl != null)
            {
                ViewerControl.SetDetachLimitState((int)CurrentDetachLimit);
                ViewerControl.SetGammaLevel(_currentGamma);
            }
        }

        private void ImageGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            var paths = e.Items.OfType<ImageItem>()
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .ToArray();
            if (paths.Length == 0) return;

            // The data package is read when this method returns. StorageFile is async, so the
            // files are supplied when the drop target asks for them.
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, request =>
            {
                var deferral = request.GetDeferral();
                _ = ProvideDraggedFilesAsync(request, deferral, paths);
            });
        }

        private static async Task ProvideDraggedFilesAsync(DataProviderRequest request, DataProviderDeferral deferral, string[] paths)
        {
            try
            {
                var items = new List<IStorageItem>(paths.Length);
                foreach (var path in paths)
                {
                    try { items.Add(await StorageFile.GetFileFromPathAsync(path)); }
                    catch (Exception ex) { Debug.WriteLine($"[Drag] {path}: {ex.Message}"); }
                }
                if (items.Count > 0)
                    request.SetData(items);
            }
            finally
            {
                deferral.Complete();
            }
        }


        private CollageEditorWindow? _collageEditorWindow;
        private readonly List<ModernImageViewer.Cinematic.CinematicWindow> _cinematicWindows = new();

        public int CinematicWindowCount => _cinematicWindows.Count;

        public void OpenCinematic(List<string> paths, int startIndex)
        {
            if (paths == null || paths.Count == 0) return;
            var window = new ModernImageViewer.Cinematic.CinematicWindow(paths, startIndex);
            _cinematicWindows.Add(window);
            window.Closed += (_, _) => _cinematicWindows.Remove(window);
        }

        private async void OpenDirector_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = _currentSettings.VideoDirectorPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                    return;
                }

                string[] fallbacks = new[]
                {
                    System.IO.Path.Combine(AppContext.BaseDirectory, "VideoDirector.exe"),
                    System.IO.Path.Combine(AppContext.BaseDirectory, @"..\VideoDirector\VideoDirector.exe")
                };

                foreach (var candidate in fallbacks)
                {
                    if (File.Exists(candidate))
                    {
                        _currentSettings.VideoDirectorPath = candidate;
                        SaveAllSettings();
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = candidate,
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                // Prompt user to locate VideoDirector.exe if missing
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add(".exe");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _currentSettings.VideoDirectorPath = file.Path;
                    SaveAllSettings();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = file.Path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to launch Video Director: {ex.Message}");
            }
        }

        private void TestCollage_Click(object sender, RoutedEventArgs e)
        {
            if (_collageEditorWindow != null)
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_collageEditorWindow);

                    // Robust bring-to-front sequence
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);     // Restore if minimized
                    NativeMethods.SetForegroundWindow(hwnd);
                    NativeMethods.BringWindowToTop(hwnd);

                    _collageEditorWindow.Activate();
                }
                catch
                {
                    // Window is in bad state → recreate it
                    _collageEditorWindow.Close();
                    _collageEditorWindow = null;
                }

                if (_collageEditorWindow != null) return;
            }

            // Create new editor
            _collageEditorWindow = new CollageEditorWindow();
            _collageEditorWindow.Closed += (s, args) => _collageEditorWindow = null;
            _collageEditorWindow.Activate();
        }
        private void SaveAllSettings()
        {
            try
            {
                if (_appWindow == null) return;

                if (_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) return;

                _currentSettings.WindowWidth = _appWindow.Size.Width;
                _currentSettings.WindowHeight = _appWindow.Size.Height;
                _currentSettings.WindowX = _appWindow.Position.X;
                _currentSettings.WindowY = _appWindow.Position.Y;

                _currentSettings.Gamma = _currentGamma;
                _currentSettings.ImageEditorPath = ImageEditorPath;
                _currentSettings.SortOrder = _currentSettings.SortOrder; // Will be updated when sorting, just to keep it
                _currentSettings.RecentFolders = new List<string>(_recentFolders);

                string json = System.Text.Json.JsonSerializer.Serialize(_currentSettings, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                string? dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }
        private void PlayCinematicFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Images.Count == 0) return;
            OpenCinematic(Images.Select(img => img.Path).ToList(), 0);
        }

        private void ImageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int count = ImageGrid.SelectedItems.Count;
            if (count > 0)
            {
                MultiSelectCountText.Text = $"{count} selected";
                MultiSelectActionBar.Visibility = Visibility.Visible;
            }
            else
            {
                MultiSelectActionBar.Visibility = Visibility.Collapsed;
            }
        }

        private void MultiSelectCinematic_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().ToList();
            if (selectedItems.Count == 0) return;
            OpenCinematic(selectedItems.Select(i => i.Path).ToList(), 0);
            ImageGrid.SelectedItems.Clear();
        }

        private void MultiSelectCollage_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().ToList();
            if (selectedItems.Count == 0) return;
            
            if (_collageEditorWindow == null)
            {
                TestCollage_Click(this, new RoutedEventArgs());
            }

            if (_collageEditorWindow != null)
            {
                foreach (var item in selectedItems)
                {
                    _collageEditorWindow.AddExternalImage(item.Path);
                }
            }
            ImageGrid.SelectedItems.Clear();
        }

        private async void MultiSelectDelete_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().ToList();
            if (selectedItems.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "Delete Selected Files",
                Content = $"Move {selectedItems.Count} files to the Recycle Bin?",
                PrimaryButtonText = "Recycle",
                CloseButtonText = "Cancel",
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                int failed = 0;
                foreach (var item in selectedItems)
                {
                    if (ViewerEngine.RecycleFile(item.Path))
                    {
                        ImageCache.Evict(item.Path);
                        Images.Remove(item);
                    }
                    else
                    {
                        failed++;
                        Debug.WriteLine($"Failed to recycle {item.Path}");
                    }
                }
                if (failed > 0)
                    await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Delete", $"{failed} file(s) could not be moved to the Recycle Bin.");
            }
            ImageGrid.SelectedItems.Clear();
        }

        private void MultiSelectClear_Click(object sender, RoutedEventArgs e)
        {
            ImageGrid.SelectedItems.Clear();
        }

        private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint((UIElement)sender);
            if (ptr.Properties.IsMiddleButtonPressed)
            {
                if (sender is FrameworkElement el && el.DataContext is ImageItem imageItem)
                {
                    LaunchDetachedWindow(imageItem);
                    e.Handled = true;
                }
            }
        }
    }
}
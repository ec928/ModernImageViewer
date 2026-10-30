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
using Windows.UI;
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
        private bool _multiViewCanvasActive;
        private bool _multiViewParked;
        private int _stashedGridWidth;
        private int _stashedGridHeight;
        private readonly IntPtr _hWnd;
        private readonly KeyEventHandler _globalKeyDownHandler;
        private readonly KeyEventHandler _globalKeyUpHandler;
        // True only while the sidebar is open *because* Alt is held, so releasing
        // Alt never closes a pane the user opened by hover or by clicking.

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
        public string CurrentDirectory => _currentDirectory;


        // Not a filesystem path. The list and sidebar use it as the level above the drives.
        private const string DriveRootPath = "::{Drives}";
        private const string DriveRootName = "This PC";
        private bool AtDriveRoot => _currentDirectory == DriveRootPath;
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
        private readonly List<string> _favouriteFolders = [];
        private readonly List<string> _multiViewRecentSaveLocations = [];
        private readonly List<string> _multiViewFavouriteSaveLocations = [];

        public IReadOnlyList<string> MultiViewRecentSaveLocations => _multiViewRecentSaveLocations;
        public IReadOnlyList<string> MultiViewFavouriteSaveLocations => _multiViewFavouriteSaveLocations;

        public bool IsMultiViewFavourite(string path) =>
            _multiViewFavouriteSaveLocations.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        public void ToggleMultiViewFavourite(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            int i = _multiViewFavouriteSaveLocations.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                _multiViewFavouriteSaveLocations.RemoveAt(i);
                if (!_multiViewRecentSaveLocations.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                    _multiViewRecentSaveLocations.Insert(0, path);
                while (_multiViewRecentSaveLocations.Count > 3)
                    _multiViewRecentSaveLocations.RemoveAt(_multiViewRecentSaveLocations.Count - 1);
            }
            else if (_multiViewFavouriteSaveLocations.Count < 3)
            {
                _multiViewFavouriteSaveLocations.Add(path);
                _multiViewRecentSaveLocations.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }
            SaveAllSettings();
        }

        public void AddMultiViewSaveLocation(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            if (IsMultiViewFavourite(path)) return;
            _multiViewRecentSaveLocations.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _multiViewRecentSaveLocations.Insert(0, path);
            while (_multiViewRecentSaveLocations.Count > 3)
                _multiViewRecentSaveLocations.RemoveAt(_multiViewRecentSaveLocations.Count - 1);
            SaveAllSettings();
        }

        public void ClearMultiViewRecentLocations()
        {
            _multiViewRecentSaveLocations.Clear();
            SaveAllSettings();
        }
        // Activated and flyout Closed can still fire after the window closes; touching Content then throws and kills the app.
        private bool _windowClosed;

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".cbz", ".cbr"
        };

        private readonly string _settingsPath = Path.Combine(AppPaths.LocalDirectory, "settings.json");
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _boundsSaveTimer;

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();
            SidebarThemeShadow.Receivers.Add(MainContentGrid);
            MultiViewControl.CanvasAspectChosen += ApplyMultiViewAspect;
            this.SystemBackdrop = new MicaBackdrop();

            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);
            _appWindow.Closing += AppWindow_Closing;

            this.ExtendsContentIntoTitleBar = true;
            var titleBar = _appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            RootGrid.ActualThemeChanged += (_, _) =>
            {
                SyncTitleBarTheme();
                SyncViewModeButtons();
                SyncPresetButtons();
                SyncThemeButtons();
            };
            SyncTitleBarTheme();
            ApplyCaptionInset();
            SyncTileChrome();

            _appWindow.Changed += (_, args) =>
            {
                if (_isFullscreen || (!args.DidSizeChange && !args.DidPositionChange)) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_boundsSaveTimer == null)
                    {
                        _boundsSaveTimer = DispatcherQueue.CreateTimer();
                        _boundsSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
                        _boundsSaveTimer.IsRepeating = false;
                        _boundsSaveTimer.Tick += (_, _) =>
                        {
                            _boundsSaveTimer.Stop();
                            if (!_isFullscreen) SaveAllSettings();
                        };
                    }
                    _boundsSaveTimer.Stop();
                    _boundsSaveTimer.Start();
                });
            };

            _canvasDevice = CanvasDevice.GetSharedDevice();

            this.Closed += (s, e) =>
            {
                _windowClosed = true;
                CancelPreviewLoad();
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
                    ViewerControl.BookSpreadChanged += (_, _) =>
                    {
                        if (ViewerControl.Visibility == Visibility.Visible && _currentIndex >= 0)
                            LoadFullImage(_currentIndex);
                    };
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
            HeaderSettingsFlyout?.Hide();
            try
            {
                var dialog = new AboutDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = RootGrid.ActualTheme
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // async void: ShowAsync throws if a dialog is already open, and
                // App_UnhandledException sets Handled = false, so this must not escape.
                Debug.WriteLine($"[About] Could not show dialog: {ex.Message}");
            }
            FocusGrid();
        }

        public void RegisterDetachedWindow(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (DetachedPaths.TryGetValue(path, out int count)) DetachedPaths[path] = count + 1;
            else DetachedPaths[path] = 1;
        }

        private bool _isMainWindowHidden = false;

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (DetachedPaths.Count > 0)
            {
                args.Cancel = true;
                _isMainWindowHidden = true;
                sender.Hide();
            }
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

            if (_isMainWindowHidden && DetachedPaths.Count == 0)
            {
                _isMainWindowHidden = false;
                this.Close();
            }
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

        private sealed class WindowPreset
        {
            public int WindowWidth { get; set; }
            public int WindowHeight { get; set; }
            public int WindowX { get; set; } = -1;
            public int WindowY { get; set; } = -1;
            public double TileSize { get; set; } = 164;
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
            public int MultiViewWindowWidth { get; set; } = -1;
            public int MultiViewWindowHeight { get; set; } = -1;
            public float Gamma { get; set; } = 2.2f;
            public string ImageEditorPath { get; set; } = "mspaint.exe";
            public string VideoDirectorPath { get; set; } = string.Empty;
            public string SortOrder { get; set; } = "NameAsc";
            public bool ListMode { get; set; }
            public double TileSize { get; set; } = 164;
            public string ThemeMode { get; set; } = "System";
            public List<WindowPreset?> WindowPresets { get; set; } = new() { null, null, null };
            public List<string> RecentFolders { get; set; } = new();
            public List<string> FavouriteFolders { get; set; } = new();
            public List<string> MultiViewRecentSaveLocations { get; set; } = new();
            public List<string> MultiViewFavouriteSaveLocations { get; set; } = new();
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
                settings.MultiViewWindowWidth = -1;
                settings.MultiViewWindowHeight = -1;

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
                        int mainTargetWidth = (int)Math.Round(targetHeight * (1278.0 / 1350.0));
                        settings.WindowWidth = mainTargetWidth;
                        settings.WindowHeight = targetHeight;
                    }
                }
                catch
                {
                    if (settings.DirectorWindowWidth == -1) settings.DirectorWindowWidth = 2400;
                    if (settings.DirectorWindowHeight == -1) settings.DirectorWindowHeight = 1350;
                    if (settings.WindowWidth == -1) settings.WindowWidth = 1278;
                    if (settings.WindowHeight == -1) settings.WindowHeight = 1350;
                }
            }

            _currentSettings = settings;
            while (_currentSettings.WindowPresets.Count < 3)
                _currentSettings.WindowPresets.Add(null);
            ApplyTheme(_currentSettings.ThemeMode, save: false);
            if (forceReset) _currentSettings.TileSize = 164;
            else if (_currentSettings.TileSize < 100 || _currentSettings.TileSize > 400) _currentSettings.TileSize = 164;
            if (ThumbSizeSlider != null) ThumbSizeSlider.Value = _currentSettings.TileSize;
            bool viewChanged = _listMode != settings.ListMode;
            _listMode = settings.ListMode;
            ImageItem.ListRows = _listMode;
            // The panel can load during InitializeComponent, before settings are read.
            if (viewChanged && _imageWrapGrid != null)
                ApplyViewMode();

            int w = settings.WindowWidth < 300 ? 1278 : settings.WindowWidth;
            int h = settings.WindowHeight < 300 ? 1350 : settings.WindowHeight;

            _appWindow?.Resize(new Windows.Graphics.SizeInt32(w, h));

            if (settings.WindowX != -1 && settings.WindowY != -1)
                _appWindow?.Move(new Windows.Graphics.PointInt32(settings.WindowX, settings.WindowY));

            SyncSortChecks(settings.SortOrder);

            _currentGamma = settings.Gamma;
            ImageEditorPath = settings.ImageEditorPath;

            _recentFolders.Clear();
            foreach (var folder in settings.RecentFolders)
            {
                if (Directory.Exists(folder) && !_recentFolders.Contains(folder))
                    _recentFolders.Add(folder);
            }

            // Favourites are kept even when the folder is missing (an unplugged drive); only the user removes them.
            _favouriteFolders.Clear();
            foreach (var folder in settings.FavouriteFolders)
            {
                if (!string.IsNullOrWhiteSpace(folder) && !IsFavouriteFolder(folder) && _favouriteFolders.Count < MaxFavouriteFolders)
                    _favouriteFolders.Add(folder);
            }

            _multiViewRecentSaveLocations.Clear();
            foreach (var folder in settings.MultiViewRecentSaveLocations)
            {
                if (Directory.Exists(folder) && !_multiViewRecentSaveLocations.Contains(folder) && _multiViewRecentSaveLocations.Count < 3)
                    _multiViewRecentSaveLocations.Add(folder);
            }

            _multiViewFavouriteSaveLocations.Clear();
            foreach (var folder in settings.MultiViewFavouriteSaveLocations)
            {
                if (!string.IsNullOrWhiteSpace(folder) && !_multiViewFavouriteSaveLocations.Contains(folder) && _multiViewFavouriteSaveLocations.Count < 3)
                    _multiViewFavouriteSaveLocations.Add(folder);
            }

            if (ViewerControl != null)
            {
                ViewerControl.SetDetachLimitState((int)CurrentDetachLimit);
                ViewerControl.SetGammaLevel(_currentGamma);
            }
        }

        private List<ImageItem>? _draggedGridItems;
        private NativeMethods.POINT _gridDragStartCursorPos;

        private void ImageGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            var items = e.Items.OfType<ImageItem>().ToList();
            if (items.Count == 0) return;

            _draggedGridItems = items;
            NativeMethods.GetCursorPos(out _gridDragStartCursorPos);

            // Safe drag-to-detach: do not provide StorageItems with Move/Copy, which would allow
            // Windows Explorer to delete/move the real image file off disk.
            e.Data.RequestedOperation = DataPackageOperation.None;
            e.Data.SetText(items[0].Path ?? string.Empty);
        }

        private void ImageGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
        {
            var items = _draggedGridItems;
            _draggedGridItems = null;

            if (items == null || items.Count == 0) return;

            NativeMethods.GetCursorPos(out var dropCursorPos);
            int dx = dropCursorPos.X - _gridDragStartCursorPos.X;
            int dy = dropCursorPos.Y - _gridDragStartCursorPos.Y;

            // If mouse moved far enough (user dragged away from tile), detach window at drop location
            if (Math.Sqrt(dx * dx + dy * dy) >= 20)
            {
                int offset = 0;
                foreach (var item in items)
                {
                    var spawn = new Windows.Graphics.PointInt32(dropCursorPos.X + offset, dropCursorPos.Y + offset);
                    LaunchDetachedWindow(item, cursorSpawnPosition: null, screenSpawnPosition: spawn);
                    offset += 24;
                }
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
                    // Window is in bad state -> recreate it
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

                if (_multiViewCanvasActive)
                {
                    _currentSettings.MultiViewWindowWidth = _appWindow.Size.Width;
                    _currentSettings.MultiViewWindowHeight = _appWindow.Size.Height;
                    if (_stashedGridWidth >= 300 && _stashedGridHeight >= 300)
                    {
                        _currentSettings.WindowWidth = _stashedGridWidth;
                        _currentSettings.WindowHeight = _stashedGridHeight;
                    }
                }
                else
                {
                    _currentSettings.WindowWidth = _appWindow.Size.Width;
                    _currentSettings.WindowHeight = _appWindow.Size.Height;
                }
                _currentSettings.WindowX = _appWindow.Position.X;
                _currentSettings.WindowY = _appWindow.Position.Y;

                _currentSettings.Gamma = _currentGamma;
                _currentSettings.ImageEditorPath = ImageEditorPath;
                _currentSettings.SortOrder = _currentSettings.SortOrder; // Will be updated when sorting, just to keep it
                _currentSettings.RecentFolders = new List<string>(_recentFolders);
                _currentSettings.FavouriteFolders = new List<string>(_favouriteFolders);
                _currentSettings.MultiViewRecentSaveLocations = new List<string>(_multiViewRecentSaveLocations);
                _currentSettings.MultiViewFavouriteSaveLocations = new List<string>(_multiViewFavouriteSaveLocations);
                if (ThumbSizeSlider != null) _currentSettings.TileSize = Math.Round(ThumbSizeSlider.Value);

                string json = System.Text.Json.JsonSerializer.Serialize(_currentSettings, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                string settingsPath = _settingsPath;
                Task.Run(() =>
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(settingsPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.WriteAllText(settingsPath, json);
                    }
                    catch { }
                });
            }
            catch { }
        }
        private void PlayCinematicFolder_Click(object sender, RoutedEventArgs e)
        {
            var playable = Images.Where(img => !img.IsDirectory && !img.IsComicArchive && !img.IsComicEntry).Select(img => img.Path).ToList();
            if (playable.Count == 0) return;
            OpenCinematic(playable, 0);
        }

        private bool _strippingDirectorySelection;

        private void ImageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_strippingDirectorySelection) return;
            foreach (var item in e.RemovedItems.OfType<ImageItem>())
                item.IsGridSelected = false;
            var directories = new List<ImageItem>();
            foreach (var item in e.AddedItems.OfType<ImageItem>())
            {
                if (item.IsDirectory || item.IsComicEntry) directories.Add(item);
                else item.IsGridSelected = true;
            }
            if (directories.Count > 0)
            {
                _strippingDirectorySelection = true;
                try
                {
                    foreach (var directory in directories)
                    {
                        if (ImageGrid.SelectedItems.Contains(directory))
                            ImageGrid.SelectedItems.Remove(directory);
                    }
                }
                finally { _strippingDirectorySelection = false; }
            }

            int count = 0;
            foreach (var item in ImageGrid.SelectedItems.OfType<ImageItem>())
            {
                if (!item.IsDirectory) count++;
            }
            if (count > 0)
            {
                MultiSelectCountText.Text = $"{count} selected";
                MultiSelectActionBar.Visibility = Visibility.Visible;
            }
            else
            {
                MultiSelectActionBar.Visibility = Visibility.Collapsed;
            }
            NoteParkedSelection();
        }

        private void MultiSelectViewTogether_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
            if (selectedItems.Count > 1)
            {
                OpenMultiView(selectedItems);
            }
        }

        private void GridMenuMultiView_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
            if (selectedItems.Count > 1)
            {
                OpenMultiView(selectedItems);
            }
        }

        private void MultiViewControl_CloseRequested(object? sender, EventArgs e)
        {
            CloseMultiView();
        }

        private void MultiSelectCinematic_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
            if (selectedItems.Count == 0) return;
            OpenCinematic(selectedItems.Select(i => i.Path).ToList(), 0);
            ImageGrid.SelectedItems.Clear();
        }

        private void MultiSelectCollage_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
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
            var selectedItems = ImageGrid.SelectedItems.OfType<ImageItem>().Where(i => !i.IsDirectory).ToList();
            if (selectedItems.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "Delete Selected Files",
                Content = $"Move {selectedItems.Count} files to the Recycle Bin?",
                PrimaryButtonText = "Recycle",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                bool watcherWasEnabled = _folderWatcher?.EnableRaisingEvents == true;
                if (_folderWatcher != null) _folderWatcher.EnableRaisingEvents = false;
                try
                {
                    await Task.WhenAll(selectedItems.Select(i => i.CancelAndAwaitTasksAsync()));
                    var paths = selectedItems.Select(i => i.Path).ToList();
                    bool batchOk = await Task.Run(() => ViewerEngine.RecycleFiles(paths));

                    var deletedItems = new List<ImageItem>(selectedItems.Count);
                    int failed = 0;
                    if (batchOk)
                    {
                        deletedItems.AddRange(selectedItems);
                    }
                    else
                    {
                        foreach (var item in selectedItems)
                        {
                            bool gone = await Task.Run(() => !File.Exists(item.Path) || ViewerEngine.RecycleFile(item.Path));
                            if (gone) deletedItems.Add(item);
                            else
                            {
                                failed++;
                                Debug.WriteLine($"Failed to recycle {item.Path}");
                            }
                        }
                    }

                    ImageGrid.SelectedItems.Clear();
                    foreach (var item in deletedItems)
                        ImageCache.Evict(item.Path);
                    RemoveDisplayedImages(deletedItems);
                    UpdateHeaderFolderInfo();

                    if (failed > 0)
                        await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Delete", $"{failed} file(s) could not be moved to the Recycle Bin.");
                }
                finally
                {
                    if (_folderWatcher != null && watcherWasEnabled)
                        _folderWatcher.EnableRaisingEvents = true;
                }
            }
            FocusGrid();
        }

        private void MultiSelectClear_Click(object sender, RoutedEventArgs e)
        {
            ImageGrid.SelectedItems.Clear();
            FocusGrid();
        }

        private void ToggleGridItemSelection(ImageItem item)
        {
            if (item.IsDirectory || item.IsComicEntry) return;
            if (ImageGrid.SelectedItems.Contains(item))
                ImageGrid.SelectedItems.Remove(item);
            else
                ImageGrid.SelectedItems.Add(item);
        }

        private void TileCheck_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint((UIElement)sender);
            if (ptr.Properties.IsLeftButtonPressed && sender is FrameworkElement el && el.DataContext is ImageItem item && !item.IsDirectory)
            {
                ToggleGridItemSelection(item);
                e.Handled = true;
            }
        }

        private void TileCheck_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint((UIElement)sender);
            if (ptr.Properties.IsMiddleButtonPressed)
            {
                if (sender is FrameworkElement el && el.DataContext is ImageItem imageItem && !imageItem.IsDirectory && !imageItem.IsComicArchive)
                {
                    LaunchDetachedWindow(imageItem);
                    e.Handled = true;
                }
            }
        }
    }
}
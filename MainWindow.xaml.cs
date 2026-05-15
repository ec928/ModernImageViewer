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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

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

        // Always defaults to CurrentZoom on initialization to meet requirement
        public DetachLimit CurrentDetachLimit = DetachLimit.CurrentZoom;

        private ObservableRangeCollection<ImageItem> _images = [];
        public ObservableRangeCollection<ImageItem> Images
        {
            get => _images;
            set { _images = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static readonly string[] ColorProfileProperty = ["System.Image.ColorProfile"];
        private static readonly string[] ExifProperties = ["System.Photo.ISOSpeed", "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.CameraManufacturer", "System.Photo.CameraModel"];
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif" };

        private readonly AppWindow _appWindow;
        private readonly IntPtr _hWnd;
        private readonly KeyEventHandler _globalKeyDownHandler;

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

        private float _targetZoom = 1.0f;

        private string _currentDirectory = string.Empty;
        private int _mouseWheelAccumulator = 0;
        private DateTime _lastNavigationTime = DateTime.MinValue;

        private float _currentGamma = 2.2f;
        private bool _isHighFidelityActive = false;

        private double _logicalImageWidth = 1;
        private double _logicalImageHeight = 1;

        private readonly HashSet<string> _activeCachePaths = [];
        private readonly Dictionary<string, CancellationTokenSource> _activeDecodes = new(StringComparer.OrdinalIgnoreCase);

        private CanvasDevice? _canvasDevice;
        private CanvasBitmap? _rawGpuBitmap;
        private ColorManagementProfile? _rawGpuProfile;

        private ItemsWrapGrid? _imageWrapGrid;
        private readonly List<string> _recentFolders = [];
        private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernImageViewer", "settings.txt");

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
                Instance = null;
                SaveAllSettings();
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
            RootGrid.PointerMoved += RootGrid_PointerMoved;
            FolderBreadcrumb.ItemsSource = _breadcrumbs;

            _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _slideshowTimer.Tick += (s, e) => Navigate(1);

            // --> FIXED: Reduced Debounce from 500ms to 150ms for instant feel
            _hfPromotionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _hfPromotionTimer.Tick += async (s, e) =>
            {
                _hfPromotionTimer?.Stop();
                if (!_isHighFidelityActive && _currentIndex >= 0 && _currentIndex < Images.Count)
                {
                    int promoteId = _currentImageLoadId;
                    var path = Images[_currentIndex].Path;

                    _hfCts?.Cancel();
                    _hfCts?.Dispose();
                    _hfCts = new CancellationTokenSource();
                    var token = _hfCts.Token;

                    var hfEntry = await Task.Run(async () =>
                    {
                        var entry = await ViewerEngine.DecodeHighFidelityAsync(path, token);
                        if (entry != null && entry.Bitmap != null && _canvasDevice != null && !token.IsCancellationRequested)
                        {
                            try
                            {
                                entry.GpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, entry.Bitmap);
                                entry.Bitmap.Dispose();
                                entry.Bitmap = null;
                            }
                            catch { }
                        }
                        return entry;
                    });

                    if (token.IsCancellationRequested || promoteId != _currentImageLoadId || hfEntry == null || (hfEntry.GpuBitmap == null && hfEntry.Bitmap == null))
                    {
                        hfEntry?.Dispose();
                        return;
                    }

                    CanvasBitmap? newGpuBitmap = hfEntry?.GpuBitmap;
                    if (newGpuBitmap == null && hfEntry?.Bitmap != null && _canvasDevice != null)
                    {
                        newGpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvasDevice, hfEntry.Bitmap);
                        hfEntry.GpuBitmap = newGpuBitmap;
                        hfEntry.Bitmap.Dispose();
                        hfEntry.Bitmap = null;
                    }

                    TryDisposeRawGpuBitmap();

                    _rawGpuBitmap = newGpuBitmap;
                    _rawGpuProfile = hfEntry?.Profile;

                    if (App.GlobalImageCache.TryGetValue(path, out var oldEntry))
                    {
                        if (!DetachedPaths.ContainsKey(path))
                        {
                            oldEntry.Dispose();
                        }
                    }
                    if (hfEntry != null) App.GlobalImageCache[path] = hfEntry;

                    _isHighFidelityActive = true;

                    if (ViewerControl != null && ViewerControl.Visibility == Visibility.Visible)
                    {
                        double safeW = hfEntry?.NativeWidth > 0 ? hfEntry.NativeWidth : _logicalImageWidth;
                        double safeH = hfEntry?.NativeHeight > 0 ? hfEntry.NativeHeight : _logicalImageHeight;
                        ViewerControl.InjectGpuBitmap(_rawGpuBitmap, _rawGpuProfile, safeW, safeH, true, false);
                    }
                }
            };

            ConfigureWindow(false);
            LoadRecentFoldersFromSettings();

            RootGrid.Loaded += async (s, e) =>
            {
                if (ViewerControl != null)
                {
                    ViewerControl.SetGammaLevel(_currentGamma);
                    ViewerControl.SetDetachLimitState((int)CurrentDetachLimit);
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

        private void ConfigureWindow(bool forceReset)
        {
            if (forceReset)
            {
                _appWindow?.Resize(new Windows.Graphics.SizeInt32(1280, 1350));
            }
            else
            {
                try
                {
                    if (File.Exists(_settingsPath))
                    {
                        var lines = File.ReadAllLines(_settingsPath);
                        if (lines.Length >= 4) _appWindow?.MoveAndResize(new Windows.Graphics.RectInt32(int.Parse(lines[2]), int.Parse(lines[3]), int.Parse(lines[0]), int.Parse(lines[1])));

                        if (lines.Length >= 5 && float.TryParse(lines[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float savedGamma))
                        {
                            _currentGamma = savedGamma;
                        }
                    }

                    if (ViewerControl != null)
                    {
                        ViewerControl.SetDetachLimitState((int)CurrentDetachLimit);
                        ViewerControl.SetGammaLevel(_currentGamma);
                    }
                }
                catch { _appWindow?.Resize(new Windows.Graphics.SizeInt32(1280, 1350)); }
            }
        }

        private void SaveAllSettings()
        {
            try
            {
                if (_appWindow != null)
                {
                    var lines = new List<string>
                    {
                        _appWindow.Size.Width.ToString(),
                        _appWindow.Size.Height.ToString(),
                        _appWindow.Position.X.ToString(),
                        _appWindow.Position.Y.ToString(),
                        _currentGamma.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    };
                    lines.AddRange(_recentFolders);

                    string? dir = Path.GetDirectoryName(_settingsPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllLines(_settingsPath, lines);
                }
            }
            catch { }
        }

        private void LoadRecentFoldersFromSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var lines = File.ReadAllLines(_settingsPath);
                    foreach (var l in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(l) && Directory.Exists(l) && !_recentFolders.Contains(l))
                        {
                            _recentFolders.Add(l);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
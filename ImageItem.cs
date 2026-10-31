using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Graphics.Imaging;

namespace ModernImageViewer
{
    public partial class ImageItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        private string _path = string.Empty;
        public string Path
        {
            get => _path;
            set { if (_path != value) { _path = value; OnPropertyChanged(); } }
        }
        public DateTime DateModified { get; set; }
        public string DateText => DateModified.ToString("yyyy-MM-dd HH:mm");

        // The tile screen's list/tile switch. One template draws both, so the cell can change shape.
        public static bool ListRows;

        private bool _isListRow;
        public ImageItem()
        {
            _isListRow = ListRows;
        }

        public bool IsListRow
        {
            get => _isListRow;
            set
            {
                if (_isListRow == value) return;
                _isListRow = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TileVisibility));
                OnPropertyChanged(nameof(ListVisibility));
            }
        }

        public Visibility TileVisibility => (IsDirectory || ListRows || _isListRow) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ListVisibility => (IsDirectory || ListRows || _isListRow) ? Visibility.Visible : Visibility.Collapsed;

        // A list cell that navigates. It is never one of the photos in Images.
        public bool IsDirectory { get; private set; }
        public bool IsParentDirectory { get; private set; }
        public string DirectoryGlyph { get; private set; } = "";
        public bool IsComicArchive => ComicArchive.IsComicArchivePath(Path);
        public bool IsComicEntry => ComicArchive.IsEntryPath(Path);
        public Visibility ComicBadgeVisibility => IsComicArchive ? Visibility.Visible : Visibility.Collapsed;
        public string ComicBadgeText => IsComicArchive ? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() : string.Empty;
        public Visibility ComicListIconVisibility => IsComicArchive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PhotoListIconVisibility => (IsDirectory || IsComicArchive) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DirectoryListIconVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;

        private ImageSource? _directoryIcon;
        public ImageSource? DirectoryIcon
        {
            get => _directoryIcon;
            set
            {
                if (ReferenceEquals(_directoryIcon, value)) return;
                _directoryIcon = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DirectoryFallbackVisibility));
                OnPropertyChanged(nameof(ParentUpBadgeVisibility));
            }
        }

        // Gold folder outline until the coloured Windows icon arrives.
        public Visibility DirectoryFallbackVisibility => IsDirectory && _directoryIcon == null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ParentUpBadgeVisibility => IsParentDirectory && _directoryIcon == null ? Visibility.Visible : Visibility.Collapsed;

        public static ImageItem CreateDirectory(string name, string path, bool parent, string glyph)
        {
            if (string.IsNullOrEmpty(name)) name = path ?? "";
            return new ImageItem
            {
                Name = name,
                Path = path ?? "",
                IsDirectory = true,
                IsParentDirectory = parent,
                DirectoryGlyph = glyph ?? "",
                IsListRow = true
            };
        }

        public string SizeString { get; set; } = string.Empty;
        public bool HasExifData { get; set; } = false;

        public DispatcherQueue? Dispatcher { get; set; }

        private string _cameraModel = string.Empty;
        public string CameraModel { get => _cameraModel; set { _cameraModel = value; OnPropertyChanged(); } }

        private string _aperture = string.Empty;
        public string Aperture { get => _aperture; set { _aperture = value; OnPropertyChanged(); } }

        private string _shutterSpeed = string.Empty;
        public string ShutterSpeed { get => _shutterSpeed; set { _shutterSpeed = value; OnPropertyChanged(); } }

        private string _iso = string.Empty;
        public string Iso { get => _iso; set { _iso = value; OnPropertyChanged(); } }

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        // Keyboard cursor on the tile grid. Not a multi-selection: the action bar stays hidden.
        private bool _isKeyboardCursor;
        public bool IsKeyboardCursor
        {
            get => _isKeyboardCursor;
            set
            {
                if (_isKeyboardCursor == value) return;
                _isKeyboardCursor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyboardCursorOpacity));
            }
        }

        public double KeyboardCursorOpacity => _isKeyboardCursor ? 1 : 0;

        // Multi-select mark on the photograph. The grid's own box is hidden, so this is the visible selection.
        private bool _isGridSelected;
        public bool IsGridSelected
        {
            get => _isGridSelected;
            set
            {
                if (_isGridSelected == value) return;
                _isGridSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectionOpacity));
                OnPropertyChanged(nameof(SelectionWashOpacity));
                OnPropertyChanged(nameof(HoverCheckOpacity));
                OnPropertyChanged(nameof(ListHoverOpacity));
            }
        }

        public double SelectionOpacity => _isGridSelected ? 1 : 0;

        private bool _isPointerHovered;
        public bool IsPointerHovered
        {
            get => _isPointerHovered;
            set
            {
                if (_isPointerHovered == value) return;
                _isPointerHovered = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HoverCheckOpacity));
                OnPropertyChanged(nameof(ListHoverOpacity));
            }
        }

        // Soft tint on a selected list row. The tile filename stays as it is.
        public double SelectionWashOpacity => _isGridSelected ? 0.16 : 0;
        public double HoverCheckOpacity => (!_isGridSelected && _isPointerHovered && !IsDirectory && !IsComicEntry) ? 1 : 0;
        public double ListHoverOpacity => (!_isGridSelected && _isPointerHovered) ? 1 : 0;

        // The size wanted for a tile on screen, and the size this one was loaded at. A larger tile asks again.
        public static int ThumbnailEdge { get; set; } = 250;
        private int _loadedEdge;

        private bool _isLoadingThumb = false;
        private static readonly SemaphoreSlim _thumbSemaphore = new(4);
        private CancellationTokenSource? _cts;

        // Task tracking for deterministic file locks
        private Task? _thumbnailTask;
        private Task? _exifTask;

        private static readonly string[] ExifProperties = ["System.Photo.ISOSpeed", "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.CameraManufacturer", "System.Photo.CameraModel"];

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public async Task CancelAndAwaitTasksAsync()
        {
            _cts?.Cancel();
            var tasks = new List<Task>();
            if (_thumbnailTask != null && !_thumbnailTask.IsCompleted) tasks.Add(_thumbnailTask);
            if (_exifTask != null && !_exifTask.IsCompleted) tasks.Add(_exifTask);

            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); } catch { }
            }
        }

        public Task LoadExifAsync(CancellationToken token = default)
        {
            _exifTask = InternalLoadExifAsync(token);
            return _exifTask;
        }

        private async Task InternalLoadExifAsync(CancellationToken token)
        {
            if (HasExifData || string.IsNullOrEmpty(Path)) return;
            if (IsComicArchive || IsComicEntry)
            {
                HasExifData = true;
                return;
            }
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(Path).AsTask(token);
                var extraProps = await file.Properties.RetrievePropertiesAsync(ExifProperties).AsTask(token);
                if (token.IsCancellationRequested) return;

                string newCameraModel = string.Empty;
                if (extraProps.TryGetValue("System.Photo.CameraModel", out object? modelObj))
                {
                    string mfg = extraProps.TryGetValue("System.Photo.CameraManufacturer", out object? mfgObj) ? (mfgObj?.ToString() ?? "") : "";
                    newCameraModel = $"{mfg} {modelObj}".Trim();
                }

                string newIso = string.Empty;
                if (extraProps.TryGetValue("System.Photo.ISOSpeed", out object? iso)) newIso = $"ISO {iso}";

                string newAperture = string.Empty;
                if (extraProps.TryGetValue("System.Photo.FNumber", out object? f)) newAperture = $"f/{Convert.ToDouble(f):F1}";

                string newShutterSpeed = string.Empty;
                if (extraProps.TryGetValue("System.Photo.ExposureTime", out object? exp))
                {
                    double expVal = Convert.ToDouble(exp);
                    newShutterSpeed = expVal < 1 ? $"1/{(int)Math.Round(1.0 / expVal)}s" : $"{expVal}s";
                }

                Dispatcher?.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    CameraModel = newCameraModel;
                    Iso = newIso;
                    Aperture = newAperture;
                    ShutterSpeed = newShutterSpeed;
                    HasExifData = true;
                });
            }
            catch { }
        }

        public string GetHudDisplayString(double logicalW, double logicalH)
        {
            return $"{(int)logicalW} \u00D7 {(int)logicalH} \u2022 {SizeString} \u2022 {DateModified:yyyy-MM-dd HH:mm}";
        }

        public string GetZoomString(float zoom)
        {
            return $"Zoom: {(int)Math.Round(zoom * 100)}%";
        }

        public Task LoadThumbnailAsync()
        {
            if (IsDirectory) return Task.CompletedTask;
            _thumbnailTask = InternalLoadThumbnailAsync();
            return _thumbnailTask;
        }

        private async Task InternalLoadThumbnailAsync()
        {
            int edge = ThumbnailEdge;
            if ((Thumbnail != null && _loadedEdge >= edge) || string.IsNullOrEmpty(Path) || _isLoadingThumb) return;
            if (Dispatcher == null) return;

            _isLoadingThumb = true;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            bool enteredSemaphore = false;

            try
            {
                await Task.Delay(20, token);
                if (token.IsCancellationRequested) return;

                await _thumbSemaphore.WaitAsync(token);
                enteredSemaphore = true;
                if (token.IsCancellationRequested) return;

                ImageSource? newThumb = null;
                bool isArchiveBacked = IsComicArchive || IsComicEntry;

                if (!isArchiveBacked)
                {
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(Path).AsTask(token);
                        using StorageItemThumbnail storageThumbnail = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, (uint)edge, ThumbnailOptions.ResizeThumbnail).AsTask(token);

                        if (storageThumbnail != null && !token.IsCancellationRequested)
                        {
                            TaskCompletionSource<ImageSource?> tcs = new();
                            using var registration = token.Register(() => tcs.TrySetCanceled());
                            var clonedStream = storageThumbnail.CloneStream();

                            bool enqueued = Dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    BitmapImage bmp = new();
                                    var asyncOp = bmp.SetSourceAsync(clonedStream);
                                    asyncOp.Completed = (info, status) =>
                                    {
                                        tcs.TrySetResult(bmp);
                                        clonedStream.Dispose();
                                    };
                                }
                                catch { tcs.TrySetResult(null); clonedStream.Dispose(); }
                            });

                            if (!enqueued)
                            {
                                clonedStream.Dispose();
                                tcs.TrySetResult(null);
                            }
                            newThumb = await tcs.Task;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Windows has no thumbnail for it, so it is decoded below.
                        System.Diagnostics.Debug.WriteLine($"Shell thumbnail failed for {Path}: {ex.Message}");
                    }
                }

                if (newThumb == null && !token.IsCancellationRequested)
                {
                    TaskCompletionSource<ImageSource?> tcs = new();
                    using var registration = token.Register(() => tcs.TrySetCanceled());

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var stream = await ViewerEngine.OpenImageStreamAsync(Path, token);
                            if (stream == null) { tcs.TrySetResult(null); return; }
                            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                            if (token.IsCancellationRequested) { tcs.TrySetResult(null); return; }

                            uint w = decoder.PixelWidth;
                            uint h = decoder.PixelHeight;
                            double ratio = Math.Min(1.0, Math.Min((double)edge / w, (double)edge / h));
                            uint scaledW = (uint)Math.Max(1, w * ratio);
                            uint scaledH = (uint)Math.Max(1, h * ratio);

                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                                BitmapPixelFormat.Bgra8,
                                BitmapAlphaMode.Premultiplied,
                                new BitmapTransform { ScaledWidth = scaledW, ScaledHeight = scaledH, InterpolationMode = BitmapInterpolationMode.Fant },
                                ExifOrientationMode.RespectExifOrientation,
                                ColorManagementMode.ColorManageToSRgb).AsTask(token);

                            bool enqueued = Dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    SoftwareBitmapSource source = new();
                                    var asyncOp = source.SetBitmapAsync(softwareBitmap);
                                    asyncOp.Completed = (info, status) =>
                                    {
                                        // Never closed: doing so crashed the app inside WinUI. It goes with the source.
                                        tcs.TrySetResult(source);
                                    };
                                }
                                catch
                                {
                                    softwareBitmap?.Dispose();
                                    tcs.TrySetResult(null);
                                }
                            });

                            if (!enqueued)
                            {
                                softwareBitmap.Dispose();
                                tcs.TrySetResult(null);
                            }
                        }
                        catch { tcs.TrySetResult(null); }
                    });
                    newThumb = await tcs.Task;
                }

                if (newThumb != null && !token.IsCancellationRequested)
                {
                    Dispatcher.TryEnqueue(() =>
                    {
                        Thumbnail = newThumb;
                        _loadedEdge = edge;
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Thumbnail load failed for {Path}: {ex.Message}");
            }
            finally
            {
                _isLoadingThumb = false;
                if (enteredSemaphore) _thumbSemaphore.Release();
            }
        }

        /// <summary>The file changed on disk. The old thumbnail stays up until the new one is ready.</summary>
        public void NoteFileChanged(DateTime modified, string size)
        {
            DateModified = modified;
            SizeString = size;
            OnPropertyChanged(nameof(DateText));
            OnPropertyChanged(nameof(SizeString));
            _loadedEdge = 0;
            if (Thumbnail != null) _ = LoadThumbnailAsync();
        }

        public void ClearThumbnail()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // Only let go of it. A Multi-View cell, the viewer or the preview may be showing this source.
            Thumbnail = null;
            _loadedEdge = 0;
            _isLoadingThumb = false;
        }
    }
}
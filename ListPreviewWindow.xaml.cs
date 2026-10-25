using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    public sealed partial class ListPreviewWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int GwlpHwndParent = -8;
        private const int SwShowNoActivate = 4;
        private const int SwHide = 0;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        private readonly AppWindow _appWindow;
        private readonly IntPtr _hwnd;
        private CancellationTokenSource? _loadCts;
        private int _generation;
        private string? _path;
        private bool _visible;
        private bool _placed;
        private bool _drag;
        private POINT _dragCursor;
        private Windows.Graphics.PointInt32 _dragWindow;

        public event EventHandler? Dismissed;

        public ListPreviewWindow()
        {
            InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

            var presenter = OverlappedPresenter.Create();
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            _appWindow.SetPresenter(presenter);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(480, 360));

            long style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));

            Closed += (_, _) =>
            {
                CancelLoad();
                Dismissed?.Invoke(this, EventArgs.Empty);
            };
        }

        public void ShowItem(ImageItem item, IntPtr ownerHwnd, AppWindow ownerWindow, double rasterScale)
        {
            if (ownerHwnd != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GwlpHwndParent, ownerHwnd);
            // WinUI can clear these when the window is shown. Reapply so a click or show does not take keyboard focus.
            long style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));

            if (!_placed)
            {
                int w = Math.Max(320, (int)Math.Round(360 * rasterScale));
                int h = Math.Max(240, (int)Math.Round(280 * rasterScale));
                _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
                PlaceAtBottomRight(ownerWindow, w, h);
                _placed = true;
            }

            if (!_visible)
            {
                ShowWindow(_hwnd, SwShowNoActivate);
                SetForegroundWindow(ownerHwnd);
                _visible = true;
            }

            if (string.Equals(_path, item.Path, StringComparison.OrdinalIgnoreCase)) return;
            _path = item.Path;
            Title = item.Name;
            Facts.Text = FactsLine(item, 0, 0);
            if (item.Thumbnail != null)
            {
                PreviewImage.Source = item.Thumbnail;
                PreviewImage.Visibility = Visibility.Visible;
                Placeholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                Placeholder.Visibility = Visibility.Visible;
            }

            int generation = ++_generation;
            CancelLoad();
            _loadCts = new CancellationTokenSource();
            _ = LoadAsync(item, generation, _loadCts.Token);
        }

        public void HidePreview()
        {
            if (!_visible) return;
            ShowWindow(_hwnd, SwHide);
            _visible = false;
            CancelLoad();
            _path = null;
        }

        private void PlaceAtBottomRight(AppWindow owner, int width, int height)
        {
            int x = owner.Position.X + owner.Size.Width - width - 12;
            int y = owner.Position.Y + owner.Size.Height - height - 12;
            var area = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest);
            if (area != null)
            {
                var work = area.WorkArea;
                if (x + width > work.X + work.Width) x = work.X + work.Width - width - 12;
                if (y + height > work.Y + work.Height) y = work.Y + work.Height - height - 12;
                if (x < work.X) x = work.X + 12;
                if (y < work.Y) y = work.Y + 12;
            }
            _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }

        private async Task LoadAsync(ImageItem item, int generation, CancellationToken token)
        {
            try
            {
                var sizeTask = ReadSizeAsync(item.Path, token);
                ImageSource? thumb = item.Thumbnail;
                if (thumb == null)
                    thumb = await ReadShellThumbnailAsync(item.Path, token);
                // A refresh builds new rows, so the shell thumb that was already on the old row is gone.
                // When the shell returns nothing, decode this one file down to the preview size.
                if (thumb == null && !token.IsCancellationRequested)
                    thumb = await ReadScaledPreviewAsync(item.Path, token);
                Size size = await sizeTask;
                if (generation != _generation || token.IsCancellationRequested) return;

                void Apply()
                {
                    if (generation != _generation || token.IsCancellationRequested) return;
                    if (thumb != null)
                    {
                        item.Thumbnail = thumb;
                        PreviewImage.Source = thumb;
                        PreviewImage.Visibility = Visibility.Visible;
                        Placeholder.Visibility = Visibility.Collapsed;
                    }
                    Facts.Text = FactsLine(item, size.Width, size.Height);
                }

                if (DispatcherQueue.HasThreadAccess) Apply();
                else DispatcherQueue.TryEnqueue(Apply);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private static string FactsLine(ImageItem item, double width, double height)
        {
            string date = item.DateModified.ToString("yyyy-MM-dd HH:mm");
            if (width >= 1 && height >= 1)
                return $"{(int)width} × {(int)height} • {item.SizeString} • {date}";
            return $"{item.SizeString} • {date}";
        }

        private static async Task<Size> ReadSizeAsync(string path, CancellationToken token)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var stream = await file.OpenReadAsync().AsTask(token);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                return new Size(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new Size(0, 0); }
        }

        // Same shell thumbnail the tiles use. An empty Size is still worth opening:
        // right after a folder refresh the shell often reports 0 until the stream is read.
        private async Task<ImageSource?> ReadShellThumbnailAsync(string path, CancellationToken token)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var shell = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 400, ThumbnailOptions.UseCurrentScale).AsTask(token);
                if (shell == null) return null;
                var cloned = shell.CloneStream();
                return await SetBitmapSourceAsync(cloned, token);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<ImageSource?> ReadScaledPreviewAsync(string path, CancellationToken token)
        {
            const uint longest = 400;
            SoftwareBitmap? softwareBitmap = null;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using var stream = await file.OpenReadAsync().AsTask(token);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                uint w = decoder.PixelWidth;
                uint h = decoder.PixelHeight;
                if (w < 1 || h < 1) return null;
                double ratio = Math.Min(longest / (double)w, longest / (double)h);
                if (ratio > 1) ratio = 1;
                softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, Math.Round(w * ratio)),
                        ScaledHeight = (uint)Math.Max(1, Math.Round(h * ratio)),
                        InterpolationMode = BitmapInterpolationMode.Fant
                    },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask(token);

                var done = new TaskCompletionSource<ImageSource?>();
                var bitmap = softwareBitmap;
                softwareBitmap = null;
                bool enqueued = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (token.IsCancellationRequested)
                        {
                            bitmap.Dispose();
                            done.TrySetCanceled(token);
                            return;
                        }
                        var source = new SoftwareBitmapSource();
                        var op = source.SetBitmapAsync(bitmap);
                        op.Completed = (_, status) =>
                        {
                            try { bitmap.Dispose(); } catch { }
                            if (status == AsyncStatus.Completed) done.TrySetResult(source);
                            else done.TrySetResult(null);
                        };
                    }
                    catch
                    {
                        try { bitmap.Dispose(); } catch { }
                        done.TrySetResult(null);
                    }
                });
                if (!enqueued)
                {
                    bitmap.Dispose();
                    return null;
                }
                return await done.Task;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            finally
            {
                softwareBitmap?.Dispose();
            }
        }

        private Task<ImageSource?> SetBitmapSourceAsync(IRandomAccessStream stream, CancellationToken token)
        {
            var done = new TaskCompletionSource<ImageSource?>();
            bool enqueued = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        stream.Dispose();
                        done.TrySetCanceled(token);
                        return;
                    }
                    var bmp = new BitmapImage();
                    var op = bmp.SetSourceAsync(stream);
                    op.Completed = (_, status) =>
                    {
                        try { stream.Dispose(); } catch { }
                        if (status == AsyncStatus.Completed) done.TrySetResult(bmp);
                        else done.TrySetResult(null);
                    };
                }
                catch
                {
                    try { stream.Dispose(); } catch { }
                    done.TrySetResult(null);
                }
            });
            if (!enqueued)
            {
                stream.Dispose();
                done.TrySetResult(null);
            }
            return done.Task;
        }

        private void CancelLoad()
        {
            _loadCts?.Cancel();
            _loadCts = null;
        }

        private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!GetCursorPos(out _dragCursor)) return;
            _dragWindow = _appWindow.Position;
            _drag = true;
            Root.CapturePointer(e.Pointer);
        }

        private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_drag || !GetCursorPos(out var now)) return;
            _appWindow.Move(new Windows.Graphics.PointInt32(
                _dragWindow.X + now.X - _dragCursor.X,
                _dragWindow.Y + now.Y - _dragCursor.Y));
        }

        private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_drag) return;
            _drag = false;
            Root.ReleasePointerCapture(e.Pointer);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}

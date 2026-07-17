using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ModernImageViewer
{
    public class FolderItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public double NodeOpacity { get; set; } = 1.0;
        public string NodeFontWeight { get; set; } = "Normal";
        public Microsoft.UI.Xaml.Controls.Symbol NodeIcon { get; set; } = Microsoft.UI.Xaml.Controls.Symbol.Folder;

        private string _subtitle = string.Empty;
        public string Subtitle
        {
            get => _subtitle;
            set
            {
                if (_subtitle != value)
                {
                    _subtitle = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
                }
            }
        }

        private Visibility _subtitleVisibility = Visibility.Collapsed;
        public Visibility SubtitleVisibility
        {
            get => _subtitleVisibility;
            set
            {
                if (_subtitleVisibility != value)
                {
                    _subtitleVisibility = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleVisibility)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ObservableRangeCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) return;
            var newItems = collection.ToList();
            if (newItems.Count == 0) return;

            foreach (var item in newItems) Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    public class AnimationFrame : IDisposable
    {
        public CanvasBitmap? GpuBitmap { get; set; }
        public TimeSpan Delay { get; set; }

        public void Dispose()
        {
            GpuBitmap?.Dispose();
            GpuBitmap = null;
        }
    }

    public partial class ImageCacheEntry : IDisposable
    {
        private int _refCount = 1;

        public SoftwareBitmap? Bitmap { get; set; }
        public List<(SoftwareBitmap Bitmap, TimeSpan Delay, ushort Disposal)>? RawFrames { get; set; }

        public CanvasBitmap? GpuBitmap { get; set; }
        public AnimationFrame[]? AnimationFrames { get; set; }

        public ColorManagementProfile? Profile { get; set; }
        public double NativeWidth { get; set; }
        public double NativeHeight { get; set; }
        public bool IsHighFidelity { get; set; } = false;

        public void AddRef()
        {
            Interlocked.Increment(ref _refCount);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) <= 0)
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            GpuBitmap?.Dispose();
            Profile?.Dispose();

            if (RawFrames != null)
            {
                foreach (var f in RawFrames) f.Bitmap.Dispose();
                RawFrames = null;
            }

            if (AnimationFrames != null)
            {
                foreach (var f in AnimationFrames) f.Dispose();
                AnimationFrames = null;
            }
            GC.SuppressFinalize(this);
        }
    }

    public class EffectStack : IDisposable
    {
        public CropEffect? Crop { get; private set; }
        public ColorManagementEffect? ColorManagement { get; private set; }
        public GammaTransferEffect? DecodeToLinear { get; private set; }
        public GammaTransferEffect? UserGamma { get; private set; }
        public Transform2DEffect? Transform { get; private set; }
        public GammaTransferEffect? EncodeToSrgb { get; private set; }

        public void Initialize()
        {
            Crop = new CropEffect();
            ColorManagement = new ColorManagementEffect();
            DecodeToLinear = new GammaTransferEffect { RedExponent = 2.2f, GreenExponent = 2.2f, BlueExponent = 2.2f };
            UserGamma = new GammaTransferEffect();
            Transform = new Transform2DEffect { InterpolationMode = CanvasImageInterpolation.HighQualityCubic };
            EncodeToSrgb = new GammaTransferEffect { RedExponent = 1.0f / 2.2f, GreenExponent = 1.0f / 2.2f, BlueExponent = 1.0f / 2.2f };
        }

        public void Dispose()
        {
            Crop?.Dispose();
            ColorManagement?.Dispose();
            DecodeToLinear?.Dispose();
            UserGamma?.Dispose();
            Transform?.Dispose();
            EncodeToSrgb?.Dispose();
        }
    }

    public static class ViewerEngine
    {
        private static readonly string[] ColorProfileProperty = ["System.Image.ColorProfile"];

        public static async Task<ImageCacheEntry?> DecodeFastPreviewAsync(string path, CancellationToken token = default)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                    using var stream = await file.OpenReadAsync().AsTask(token);
                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);

                    if (token.IsCancellationRequested) return null;

                    double nativeW = decoder.OrientedPixelWidth;
                    double nativeH = decoder.OrientedPixelHeight;

                    uint w = decoder.PixelWidth;
                    uint h = decoder.PixelHeight;

                    if (w > 1200 || h > 1200)
                    {
                        double ratio = Math.Min(1200.0 / w, 1200.0 / h);
                        w = (uint)Math.Max(1, w * ratio);
                        h = (uint)Math.Max(1, h * ratio);
                    }

                    if (token.IsCancellationRequested) return null;

                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant },
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb).AsTask(token);

                    if (token.IsCancellationRequested)
                    {
                        softwareBitmap?.Dispose();
                        return null;
                    }

                    if (softwareBitmap != null) { softwareBitmap.DpiX = 96; softwareBitmap.DpiY = 96; }

                    // Returns with initial _refCount = 1
                    return new ImageCacheEntry { Bitmap = softwareBitmap, Profile = null, NativeWidth = nativeW, NativeHeight = nativeH, IsHighFidelity = false };
                }
                catch { return null; }
            }, token);
        }

        public static async Task<ImageCacheEntry?> DecodeHighFidelityAsync(string path, CancellationToken token = default)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                    using var stream = await file.OpenReadAsync().AsTask(token);
                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);

                    if (token.IsCancellationRequested) return null;

                    double nativeW = decoder.OrientedPixelWidth;
                    double nativeH = decoder.OrientedPixelHeight;

                    ColorManagementProfile? prof = null;
                    try
                    {
                        var props = await decoder.BitmapProperties.GetPropertiesAsync(ColorProfileProperty).AsTask(token);
                        if (props.TryGetValue("System.Image.ColorProfile", out var profileProperty) && profileProperty.Value is byte[] profileBytes)
                        {
                            prof = ColorManagementProfile.CreateCustom(profileBytes);
                        }
                    }
                    catch { }

                    if (prof == null) prof = new ColorManagementProfile(CanvasColorSpace.Srgb);
                    if (token.IsCancellationRequested) return null;

                    BitmapPixelFormat format = decoder.BitmapPixelFormat == BitmapPixelFormat.Rgba16 ? BitmapPixelFormat.Rgba16 : BitmapPixelFormat.Bgra8;

                    uint maxTexSize = 16384;
                    uint decW = decoder.PixelWidth;
                    uint decH = decoder.PixelHeight;

                    BitmapTransform transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
                    if (decW > maxTexSize || decH > maxTexSize)
                    {
                        double ratio = Math.Min((double)maxTexSize / decW, (double)maxTexSize / decH);
                        transform.ScaledWidth = (uint)Math.Max(1, decW * ratio);
                        transform.ScaledHeight = (uint)Math.Max(1, decH * ratio);
                    }

                    uint frameCount = decoder.FrameCount;

                    if (frameCount > 1)
                    {
                        var rawFrames = new List<(SoftwareBitmap, TimeSpan, ushort)>();
                        try
                        {
                            for (uint i = 0; i < frameCount; i++)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    foreach (var f in rawFrames) f.Item1.Dispose();
                                    rawFrames.Clear();
                                    return null;
                                }

                                var frame = await decoder.GetFrameAsync(i).AsTask(token);

                                TimeSpan delay = TimeSpan.FromMilliseconds(100);
                                ushort disposal = 0;
                                try
                                {
                                    var props = await frame.BitmapProperties.GetPropertiesAsync(new[] { "/grctlext/Delay", "/grctlext/Disposal" }).AsTask(token);
                                    if (props.TryGetValue("/grctlext/Delay", out var delayProp) && delayProp.Value is ushort delayVal)
                                    {
                                        if (delayVal > 0) delay = TimeSpan.FromMilliseconds(delayVal * 10);
                                    }
                                    if (props.TryGetValue("/grctlext/Disposal", out var dispProp) && dispProp.Value is byte dispVal)
                                    {
                                        disposal = dispVal;
                                    }
                                }
                                catch { }

                                var softwareBitmap = await frame.GetSoftwareBitmapAsync(
                                    format,
                                    BitmapAlphaMode.Premultiplied,
                                    transform,
                                    ExifOrientationMode.RespectExifOrientation,
                                    ColorManagementMode.ColorManageToSRgb).AsTask(token);

                                if (softwareBitmap != null)
                                {
                                    softwareBitmap.DpiX = 96; softwareBitmap.DpiY = 96;
                                    rawFrames.Add((softwareBitmap, delay, disposal));
                                }
                            }
                        }
                        catch
                        {
                            foreach (var f in rawFrames) f.Item1?.Dispose();
                            rawFrames.Clear();
                            return null;
                        }

                        if (token.IsCancellationRequested)
                        {
                            foreach (var f in rawFrames) f.Item1.Dispose();
                            rawFrames.Clear();
                            return null;
                        }
                        return new ImageCacheEntry { RawFrames = rawFrames, Profile = null, NativeWidth = nativeW, NativeHeight = nativeH, IsHighFidelity = true };
                    }
                    else
                    {
                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                            format,
                            BitmapAlphaMode.Premultiplied,
                            transform,
                            ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb).AsTask(token);

                        if (token.IsCancellationRequested) { softwareBitmap?.Dispose(); return null; }
                        if (softwareBitmap != null) { softwareBitmap.DpiX = 96; softwareBitmap.DpiY = 96; }

                        return new ImageCacheEntry { Bitmap = softwareBitmap, Profile = null, NativeWidth = nativeW, NativeHeight = nativeH, IsHighFidelity = true };
                    }
                }
                catch { return null; }
            });
        }

        public static CanvasBitmap? CreateGpuBitmap(SoftwareBitmap softwareBitmap)
        {
            if (softwareBitmap == null) return null;
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                return CanvasBitmap.CreateFromSoftwareBitmap(device, softwareBitmap);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewerEngine] CreateGpuBitmap failed: {ex.Message}");
                return null;
            }
        }

        public static bool FinalizeHighFidelityGpuResources(ImageCacheEntry entry)
        {
            try
            {
                var device = CanvasDevice.GetSharedDevice();

                if (entry.RawFrames != null && entry.RawFrames.Count > 1)
                {
                    entry.AnimationFrames = new AnimationFrame[entry.RawFrames.Count];
                    CanvasRenderTarget? previousFrame = null;
                    ushort lastDisposal = 0;

                    for (int i = 0; i < entry.RawFrames.Count; i++)
                    {
                        var raw = entry.RawFrames[i];
                        var currentTarget = new CanvasRenderTarget(device, (float)entry.NativeWidth, (float)entry.NativeHeight, 96f);

                        using (var ds = currentTarget.CreateDrawingSession())
                        {
                            ds.Clear(Microsoft.UI.Colors.Transparent);

                            if (previousFrame != null && lastDisposal != 2)
                            {
                                ds.DrawImage(previousFrame);
                            }

                            using var deltaBitmap = CanvasBitmap.CreateFromSoftwareBitmap(device, raw.Bitmap);
                            ds.DrawImage(deltaBitmap);
                        }

                        entry.AnimationFrames[i] = new AnimationFrame { GpuBitmap = currentTarget, Delay = raw.Delay };
                        previousFrame = currentTarget;
                        lastDisposal = raw.Disposal;
                        raw.Bitmap.Dispose();
                    }

                    entry.RawFrames.Clear();
                    entry.RawFrames = null;
                    return true;
                }
                else if (entry.Bitmap != null)
                {
                    entry.GpuBitmap = CanvasBitmap.CreateFromSoftwareBitmap(device, entry.Bitmap);
                    entry.Bitmap.Dispose();
                    entry.Bitmap = null;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewerEngine] Compositing failed: {ex.Message}");
                if (entry.RawFrames != null)
                {
                    foreach (var f in entry.RawFrames) f.Bitmap?.Dispose();
                    entry.RawFrames.Clear();
                    entry.RawFrames = null;
                }
                if (entry.AnimationFrames != null)
                {
                    foreach (var f in entry.AnimationFrames) f?.Dispose();
                    entry.AnimationFrames = null;
                }
                return false;
            }
        }

        public static bool RecycleFile(string path)
        {
            try
            {
                var op = new NativeMethods.SHFILEOPSTRUCT { wFunc = NativeMethods.FO_DELETE, pFrom = path + '\0' + '\0', fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT };
                return NativeMethods.SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
            }
            catch { return false; }
        }

        public static async Task<bool> RenameImageAsync(ImageItem item, XamlRoot xamlRoot)
        {
            var oldPath = item.Path;
            string dir = Path.GetDirectoryName(oldPath) ?? string.Empty;
            string ext = Path.GetExtension(oldPath);

            var input = new TextBox { Text = Path.GetFileNameWithoutExtension(oldPath), SelectionStart = 0, SelectionLength = 999 };
            var dialog = new ContentDialog { Title = "Rename", Content = input, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", XamlRoot = xamlRoot };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                string newName = input.Text.Trim();
                if (string.IsNullOrEmpty(newName)) return false;
                if (newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName = newName.Substring(0, newName.Length - ext.Length);
                string newPath = Path.Combine(dir, newName + ext);
                if (newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) return false;

                try
                {
                    File.Move(oldPath, newPath);
                    item.Path = newPath;
                    item.Name = Path.GetFileName(newPath);
                    return true;
                }
                catch { return false; }
            }
            return false;
        }

        public static async Task<bool> DeleteImageAsync(ImageItem item, XamlRoot xamlRoot)
        {
            var dialog = new ContentDialog { Title = "Delete", Content = "Move to Recycle Bin?", PrimaryButtonText = "Yes", SecondaryButtonText = "No", XamlRoot = xamlRoot };
            return await dialog.ShowAsync() == ContentDialogResult.Primary && RecycleFile(item.Path);
        }
    }

    public static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public struct POINT { public int X; public int Y; }
        public const int SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }

        public const uint FO_DELETE = 0x0003;
        public const ushort FOF_ALLOWUNDO = 0x0040;
        public const ushort FOF_NOCONFIRMATION = 0x0010;
        public const ushort FOF_SILENT = 0x0004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }
}
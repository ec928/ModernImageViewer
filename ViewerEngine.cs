using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ModernImageViewer
{
    public class FolderItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public double NodeOpacity { get; set; } = 1.0;
        public string NodeFontWeight { get; set; } = "Normal";
        public Microsoft.UI.Xaml.Controls.Symbol NodeIcon { get; set; } = Microsoft.UI.Xaml.Controls.Symbol.Folder;
    }

    public class ObservableRangeCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) return;
            var newItems = collection.ToList();
            if (newItems.Count == 0) return;

            int startingIndex = Items.Count;
            foreach (var item in newItems)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, startingIndex));
        }
    }

    public partial class ImageCacheEntry : IDisposable
    {
        public SoftwareBitmap? Bitmap { get; set; }
        public CanvasBitmap? GpuBitmap { get; set; }
        public ColorManagementProfile? Profile { get; set; }
        public double NativeWidth { get; set; }
        public double NativeHeight { get; set; }
        public bool IsHighFidelity { get; set; } = false;

        public void Dispose()
        {
            Bitmap?.Dispose();
            GpuBitmap?.Dispose();
            Profile?.Dispose();
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

                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        new BitmapTransform
                        {
                            ScaledWidth = w,
                            ScaledHeight = h,
                            InterpolationMode = BitmapInterpolationMode.Fant
                        },
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb).AsTask(token);

                    if (token.IsCancellationRequested)
                    {
                        softwareBitmap?.Dispose();
                        return null;
                    }

                    if (softwareBitmap != null)
                    {
                        softwareBitmap.DpiX = 96;
                        softwareBitmap.DpiY = 96;
                    }

                    return new ImageCacheEntry { Bitmap = softwareBitmap, Profile = prof, NativeWidth = nativeW, NativeHeight = nativeH, IsHighFidelity = false };
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

                    BitmapPixelFormat format = BitmapPixelFormat.Bgra8;
                    if (decoder.BitmapPixelFormat == BitmapPixelFormat.Rgba16)
                    {
                        format = BitmapPixelFormat.Rgba16;
                    }

                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        format,
                        BitmapAlphaMode.Premultiplied,
                        new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage).AsTask(token);

                    if (token.IsCancellationRequested)
                    {
                        softwareBitmap?.Dispose();
                        return null;
                    }

                    if (softwareBitmap != null)
                    {
                        softwareBitmap.DpiX = 96;
                        softwareBitmap.DpiY = 96;
                    }

                    return new ImageCacheEntry { Bitmap = softwareBitmap, Profile = prof, NativeWidth = nativeW, NativeHeight = nativeH, IsHighFidelity = true };
                }
                catch { return null; }
            });
        }
    }

    public static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public struct POINT
        {
            public int X;
            public int Y;
        }

        public const int SW_SHOWNOACTIVATE = 4;
    }
}
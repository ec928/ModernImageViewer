using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        private bool _savingGrid;

        private void RefreshCanvasLabel()
        {
            if (AspectText == null || CellsGrid == null) return;
            int w = (int)Math.Round(CellsGrid.ActualWidth);
            int h = (int)Math.Round(CellsGrid.ActualHeight);
            AspectText.Text = GridCanvasMath.MatchAspect(w, h);
        }

        private void AspectFlyout_Opening(object sender, object e)
        {
            AspectMenuFlyout.Items.Clear();
            string current = AspectText.Text;
            foreach (var aspect in GridCanvasMath.Aspects)
            {
                double ratio = aspect.WidthOverHeight;
                var item = new MenuFlyoutItem
                {
                    Text = (aspect.Label == current ? "✓  " : "    ") + aspect.Label
                };
                item.Click += (_, _) => CanvasAspectChosen?.Invoke(ratio);
                AspectMenuFlyout.Items.Add(item);
            }
        }

        private void SaveFlyout_Opening(object sender, object e)
        {
            SaveMenuFlyout.Items.Clear();
            double canvasW = CellsGrid.ActualWidth;
            double canvasH = CellsGrid.ActualHeight;
            foreach (int height in GridCanvasMath.SaveHeights)
            {
                if (!GridCanvasMath.TryOutputSize(height, canvasW, canvasH, out int width, out int outputH))
                    continue;
                int chosen = height;
                var item = new MenuFlyoutItem { Text = $"{chosen}p    {width}×{outputH}" };
                item.Click += async (_, _) => await SaveAtHeightAsync(chosen);
                SaveMenuFlyout.Items.Add(item);
            }

            var pixels = CanvasPixels();
            if (pixels.Width > 0 && pixels.Height > 0
                && pixels.Width <= GridCanvasMath.MaxEdge && pixels.Height <= GridCanvasMath.MaxEdge)
            {
                var windowItem = new MenuFlyoutItem { Text = $"This window    {pixels.Width}×{pixels.Height}" };
                windowItem.Click += async (_, _) => await SavePixelsAsync(pixels.Width, pixels.Height);
                SaveMenuFlyout.Items.Add(windowItem);
            }

            var custom = new MenuFlyoutItem { Text = "Custom…" };
            custom.Click += async (_, _) =>
            {
                int? height = await AskHeightAsync(canvasW, canvasH);
                if (height != null) await SaveAtHeightAsync(height.Value);
            };
            SaveMenuFlyout.Items.Add(custom);
        }

        private (int Width, int Height) CanvasPixels()
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            if (scale <= 0) scale = 1;
            return (
                (int)Math.Round(CellsGrid.ActualWidth * scale),
                (int)Math.Round(CellsGrid.ActualHeight * scale));
        }

        private async Task SaveAtHeightAsync(int height)
        {
            if (!GridCanvasMath.TryOutputSize(height, CellsGrid.ActualWidth, CellsGrid.ActualHeight, out int width, out int outputH))
            {
                await ShowExportMessageAsync("That height does not fit. An edge would pass 16384 pixels.");
                return;
            }
            await SavePixelsAsync(width, outputH);
        }

        private async Task<int?> AskHeightAsync(double canvasW, double canvasH)
        {
            var box = new TextBox { Text = "1080", Width = 160, HorizontalAlignment = HorizontalAlignment.Center };
            var preview = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 16
            };
            bool ok = false;
            void Update()
            {
                ok = int.TryParse(box.Text.Trim(), out int h)
                    && GridCanvasMath.TryOutputSize(h, canvasW, canvasH, out _, out _);
                if (ok && int.TryParse(box.Text.Trim(), out int height)
                    && GridCanvasMath.TryOutputSize(height, canvasW, canvasH, out int width, out int outputH))
                    preview.Text = $"{width}×{outputH}";
                else if (int.TryParse(box.Text.Trim(), out _))
                    preview.Text = "An edge would pass 16384 pixels.";
                else
                    preview.Text = "Type a height in pixels.";
            }
            box.TextChanged += (_, _) => Update();
            Update();

            var panel = new StackPanel { Spacing = 10, Width = 280 };
            panel.Children.Add(new TextBlock { Text = "Height in pixels. The width follows the window.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);
            panel.Children.Add(preview);

            var dialog = new ContentDialog
            {
                Title = "Custom size",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Primary
            };
            box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ok;
            dialog.IsPrimaryButtonEnabled = ok;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            if (!int.TryParse(box.Text.Trim(), out int chosen)) return null;
            return chosen;
        }

        private async Task SavePixelsAsync(int width, int height)
        {
            if (_savingGrid) return;
            var shots = CaptureCells();
            if (shots.Count == 0)
            {
                await ShowExportMessageAsync("Nothing to save.");
                return;
            }
            if (shots.Count < _cellElements.Count)
            {
                await ShowExportMessageAsync("A photo is still loading.");
                return;
            }

            if (MainWindow.Instance == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = SuggestedJpegName(width, height)
            };
            picker.FileTypeChoices.Add("JPEG Image", new List<string> { ".jpg" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) return;

            _savingGrid = true;
            SaveButton.IsEnabled = false;
            SaveText.Text = "Saving…";
            try
            {
                await RenderJpegAsync(file, shots, width, height);
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync(ex.Message);
            }
            finally
            {
                _savingGrid = false;
                SaveButton.IsEnabled = true;
                SaveText.Text = "Save";
            }
        }

        private string SuggestedJpegName(int width, int height)
        {
            string stem = _items.Count > 0 ? Path.GetFileNameWithoutExtension(_items[0].Name) : "grid";
            if (string.IsNullOrWhiteSpace(stem)) stem = "grid";
            foreach (char c in Path.GetInvalidFileNameChars())
                stem = stem.Replace(c, '-');
            return $"{stem}-{width}x{height}";
        }

        private List<CellShot> CaptureCells()
        {
            var shots = new List<CellShot>();
            int count = Math.Min(_items.Count, Math.Min(_cellElements.Count, _cellTransforms.Count));
            double canvasW = CellsGrid.ActualWidth;
            double canvasH = CellsGrid.ActualHeight;
            if (canvasW < 1 || canvasH < 1) return shots;

            for (int i = 0; i < count; i++)
            {
                if (_cellElements[i] is not Grid cell || cell.ActualWidth < 1 || cell.ActualHeight < 1)
                    continue;
                Grid? container = null;
                foreach (var child in cell.Children)
                {
                    if (child is Grid grid && grid.RenderTransform is CompositeTransform)
                    {
                        container = grid;
                        break;
                    }
                }
                if (container == null || container.Width < 1 || container.Height < 1) continue;

                int nativeW = 0;
                int nativeH = 0;
                foreach (var child in container.Children)
                {
                    if (child is Image image && image.Source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                    {
                        nativeW = bitmap.PixelWidth;
                        nativeH = bitmap.PixelHeight;
                        break;
                    }
                }
                if (nativeW < 1 || nativeH < 1) continue;

                Rect bounds = cell.TransformToVisual(CellsGrid).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
                var transform = _cellTransforms[i];
                shots.Add(new CellShot
                {
                    Path = _items[i].Path,
                    Cell = bounds,
                    ImageW = container.Width,
                    ImageH = container.Height,
                    Scale = transform.ScaleX,
                    TranslateX = transform.TranslateX,
                    TranslateY = transform.TranslateY,
                    NativeW = nativeW,
                    NativeH = nativeH,
                    CanvasW = canvasW,
                    CanvasH = canvasH
                });
            }
            return shots;
        }

        private static async Task RenderJpegAsync(StorageFile file, List<CellShot> shots, int width, int height)
        {
            var device = CanvasDevice.GetSharedDevice();
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var clear = target.CreateDrawingSession())
                clear.Clear(Color.FromArgb(255, 10, 10, 10));

            foreach (var shot in shots)
            {
                var placed = GridCanvasMath.Place(shot.Cell.Width, shot.Cell.Height, shot.ImageW, shot.ImageH, shot.Scale, shot.TranslateX, shot.TranslateY);
                double scaleX = width / shot.CanvasW;
                double scaleY = height / shot.CanvasH;
                var decode = GridCanvasMath.DecodeSize(shot.NativeW, shot.NativeH, placed.Width * scaleX, placed.Height * scaleY);
                using var bitmap = await DecodeForCellAsync(shot.Path, device, decode.Width, decode.Height);
                if (bitmap == null) continue;

                double x0 = Math.Floor(shot.Cell.X * scaleX);
                double y0 = Math.Floor(shot.Cell.Y * scaleY);
                double x1 = Math.Ceiling((shot.Cell.X + shot.Cell.Width) * scaleX);
                double y1 = Math.Ceiling((shot.Cell.Y + shot.Cell.Height) * scaleY);
                x0 = Math.Clamp(x0, 0, width);
                y0 = Math.Clamp(y0, 0, height);
                x1 = Math.Clamp(x1, 0, width);
                y1 = Math.Clamp(y1, 0, height);
                if (x1 <= x0 || y1 <= y0) continue;

                var dest = new Rect(
                    (shot.Cell.X + placed.Left) * scaleX,
                    (shot.Cell.Y + placed.Top) * scaleY,
                    placed.Width * scaleX,
                    placed.Height * scaleY);
                using var session = target.CreateDrawingSession();
                using (session.CreateLayer(1f, new Rect(x0, y0, x1 - x0, y1 - y0)))
                    session.DrawImage(bitmap, dest);
            }

            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            stream.Size = 0;
            await target.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, 0.95f);
        }

        private static async Task<CanvasBitmap?> DecodeForCellAsync(string path, CanvasDevice device, int decodeW, int decodeH)
        {
            StorageFile imageFile = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await imageFile.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint rawW = decoder.PixelWidth;
            uint rawH = decoder.PixelHeight;
            uint oriW = decoder.OrientedPixelWidth;
            uint oriH = decoder.OrientedPixelHeight;
            if (rawW < 1 || rawH < 1 || oriW < 1 || oriH < 1) return null;

            // Scale the stored pixels. Orientation is applied afterwards, so the factor is the oriented size we want divided by the file's oriented size.
            double scale = Math.Min(decodeW / (double)oriW, decodeH / (double)oriH);
            if (scale <= 0) scale = 1;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(rawW * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(rawH * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            using SoftwareBitmap? software = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            if (software == null) return null;
            software.DpiX = 96;
            software.DpiY = 96;
            return CanvasBitmap.CreateFromSoftwareBitmap(device, software);
        }

        private async Task ShowExportMessageAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Save",
                Content = message,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }

        private sealed class CellShot
        {
            public string Path = "";
            public Rect Cell;
            public double ImageW;
            public double ImageH;
            public double Scale;
            public double TranslateX;
            public double TranslateY;
            public int NativeW;
            public int NativeH;
            public double CanvasW;
            public double CanvasH;
        }
    }
}

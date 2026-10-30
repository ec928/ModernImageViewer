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
                    Text = (aspect.Label == current ? "\u2713  " : "    ") + aspect.Label
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
                var item = new MenuFlyoutItem { Text = $"{chosen}p    {width}\u00D7{outputH}" };
                item.Click += async (_, _) => await SaveAtHeightAsync(chosen);
                SaveMenuFlyout.Items.Add(item);
            }

            var pixels = CanvasPixels();
            if (pixels.Width > 0 && pixels.Height > 0
                && pixels.Width <= GridCanvasMath.MaxEdge && pixels.Height <= GridCanvasMath.MaxEdge)
            {
                var windowItem = new MenuFlyoutItem { Text = $"This window    {pixels.Width}\u00D7{pixels.Height}" };
                windowItem.Click += async (_, _) => await SavePixelsAsync(pixels.Width, pixels.Height);
                SaveMenuFlyout.Items.Add(windowItem);
            }

            var custom = new MenuFlyoutItem { Text = "Custom\u2026" };
            custom.Click += async (_, _) =>
            {
                int? height = await AskHeightAsync(canvasW, canvasH);
                if (height != null) await SaveAtHeightAsync(height.Value);
            };
            SaveMenuFlyout.Items.Add(custom);

            // Layout Save & Load
            SaveMenuFlyout.Items.Add(new MenuFlyoutSeparator());

            var saveLayoutItem = new MenuFlyoutItem { Text = "Save Layout as\u2026" };
            saveLayoutItem.Click += async (_, _) => await SaveLayoutFileAsync();
            SaveMenuFlyout.Items.Add(saveLayoutItem);

            var openLayoutItem = new MenuFlyoutItem { Text = "Open Layout\u2026" };
            openLayoutItem.Click += async (_, _) => await OpenLayoutFileAsync();
            SaveMenuFlyout.Items.Add(openLayoutItem);
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
                    preview.Text = $"{width}\u00D7{outputH}";
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
            if (shots.Count < Math.Min(_items.Count, _cellRoots.Length))
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
            SaveText.Text = "Saving\u2026";
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
            int count = Math.Min(_items.Count, _cellRoots.Length);
            double canvasW = CellsGrid.ActualWidth;
            double canvasH = CellsGrid.ActualHeight;
            if (canvasW < 1 || canvasH < 1) return shots;

            for (int i = 0; i < count; i++)
            {
                if (_cellRoots[i] is not Border root || root.Child is not Grid cell || cell.ActualWidth < 1 || cell.ActualHeight < 1)
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

                if (root.Tag is not CellParts parts
                    || parts.Photo.Source is not BitmapSource bitmap
                    || bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1)
                    continue;
                int nativeW = bitmap.PixelWidth;
                int nativeH = bitmap.PixelHeight;

                Rect bounds = cell.TransformToVisual(CellsGrid).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
                if (container.RenderTransform is not CompositeTransform transform) continue;
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

        private string SuggestedLayoutName()
        {
            string stem = _items.Count > 0 ? Path.GetFileNameWithoutExtension(_items[0].Name) : "layout";
            if (string.IsNullOrWhiteSpace(stem)) stem = "layout";
            foreach (char c in Path.GetInvalidFileNameChars())
                stem = stem.Replace(c, '-');
            return $"{stem}-layout";
        }

        private async Task SaveLayoutFileAsync()
        {
            if (MainWindow.Instance == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = SuggestedLayoutName()
            };
            picker.FileTypeChoices.Add("Multi-View Layout", new List<string> { ".mivlayout" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) return;

            WriteAllGridWeights();

            var layoutFile = new MultiViewLayoutFile
            {
                Created = DateTime.UtcNow,
                Canvas = new CanvasLayoutDto
                {
                    Aspect = AspectText?.Text,
                    BackgroundColor = null
                },
                State = new LayoutStateDto
                {
                    Lines = _lines,
                    LineCounts = _lineCounts != null ? new List<int>(_lineCounts) : null,
                    Flipped = _flipped,
                    HeroActive = _heroActive,
                    HeroKind = _heroKind.ToString(),
                    HeroPosition = _heroPosition,
                    IsFillMode = _isFillMode
                },
                Mosaic = _mosaic != null ? MosaicNodeDto.FromMosaicLayout(_mosaic) : null
            };

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var slotDto = new PhotoSlotDto
                {
                    Slot = i,
                    Path = item.Path ?? string.Empty,
                    Name = item.Name
                };

                if (i < _cellRoots.Length && _cellRoots[i]?.Tag is CellParts parts)
                {
                    slotDto.Zoom = parts.Transform.ScaleX;
                    slotDto.PanX = parts.Transform.TranslateX;
                    slotDto.PanY = parts.Transform.TranslateY;
                }

                layoutFile.Photos.Add(slotDto);
            }

            try
            {
                string json = layoutFile.ToJson();
                await FileIO.WriteTextAsync(file, json);
                await ShowExportMessageAsync("Layout saved.");
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync($"Failed to save layout: {ex.Message}");
            }
        }

        private async Task OpenLayoutFileAsync()
        {
            if (MainWindow.Instance == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".mivlayout");
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                string json = await FileIO.ReadTextAsync(file);
                var layoutFile = MultiViewLayoutFile.FromJson(json);
                if (layoutFile == null)
                {
                    await ShowExportMessageAsync("Could not read layout file.");
                    return;
                }
                ApplyLayoutFile(layoutFile);
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync($"Failed to open layout: {ex.Message}");
            }
        }

        public void ApplyLayoutFile(MultiViewLayoutFile layoutFile)
        {
            if (layoutFile == null) return;

            if (layoutFile.Photos != null && layoutFile.Photos.Count > 0)
            {
                var items = new List<ImageItem>();
                foreach (var photo in layoutFile.Photos)
                {
                    items.Add(new ImageItem
                    {
                        Path = photo.Path ?? string.Empty,
                        Name = !string.IsNullOrEmpty(photo.Name) ? photo.Name : Path.GetFileName(photo.Path ?? string.Empty),
                        Dispatcher = this.DispatcherQueue
                    });
                }
                _items.Clear();
                _items.AddRange(items);
            }

            _framedSlots = 0;

            if (layoutFile.State != null)
            {
                _isFillMode = layoutFile.State.IsFillMode;
                _lines = layoutFile.State.Lines > 0 ? layoutFile.State.Lines : 1;
                _lineCounts = layoutFile.State.LineCounts != null ? layoutFile.State.LineCounts.ToArray() : null;
                _flipped = layoutFile.State.Flipped;
                _heroActive = layoutFile.State.HeroActive;
                if (Enum.TryParse<HeroKind>(layoutFile.State.HeroKind, out var heroKind))
                    _heroKind = heroKind;
                else
                    _heroKind = HeroKind.Banner;
                _heroPosition = layoutFile.State.HeroPosition;
            }

            if (layoutFile.Mosaic != null)
            {
                _mosaic = layoutFile.Mosaic.ToMosaicLayout();
                _mosaicCustom = true;
                _keepMosaic = true;
            }
            else
            {
                _mosaic = null;
                _mosaicCustom = false;
                _keepMosaic = false;
            }

            ExitSolo();

            _layoutFromTemplate = true;
            try
            {
                BuildLayout();
            }
            finally
            {
                _layoutFromTemplate = false;
            }

            // Restore zoom and pan
            if (layoutFile.Photos != null)
            {
                for (int i = 0; i < layoutFile.Photos.Count; i++)
                {
                    var photo = layoutFile.Photos[i];
                    if (i < _cellRoots.Length && _cellRoots[i]?.Tag is CellParts parts)
                    {
                        if (photo.Zoom.HasValue && photo.Zoom.Value > 0)
                        {
                            parts.Transform.ScaleX = photo.Zoom.Value;
                            parts.Transform.ScaleY = photo.Zoom.Value;
                        }
                        if (photo.PanX.HasValue) parts.Transform.TranslateX = photo.PanX.Value;
                        if (photo.PanY.HasValue) parts.Transform.TranslateY = photo.PanY.Value;
                    }
                }
            }

            UpdateFillFitButton();
            ShowHudAndOverlays();

            // Aspect ratio if specified
            if (!string.IsNullOrWhiteSpace(layoutFile.Canvas?.Aspect))
            {
                foreach (var aspect in GridCanvasMath.Aspects)
                {
                    if (string.Equals(aspect.Label, layoutFile.Canvas.Aspect, StringComparison.OrdinalIgnoreCase))
                    {
                        CanvasAspectChosen?.Invoke(aspect.WidthOverHeight);
                        break;
                    }
                }
            }
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

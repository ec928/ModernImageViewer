using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
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

        private static readonly SolidColorBrush _menuWhitePrimary = new(Windows.UI.Color.FromArgb(245, 255, 255, 255));
        private static readonly SolidColorBrush _menuWhiteSecondary = new(Windows.UI.Color.FromArgb(170, 255, 255, 255));
        private static readonly SolidColorBrush _menuWhiteTertiary = new(Windows.UI.Color.FromArgb(115, 255, 255, 255));
        private static readonly SolidColorBrush _menuGoldStar = new(Windows.UI.Color.FromArgb(255, 255, 200, 59));
        private static readonly SolidColorBrush _menuDividerBrush = new(Windows.UI.Color.FromArgb(35, 255, 255, 255));

        private void SaveFlyout_Opening(object sender, object e)
        {
            BuildSaveFlyoutMenu();
        }

        private void BuildSaveFlyoutMenu()
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 1,
                XYFocusKeyboardNavigation = Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled
            };

            double canvasW = CellsGrid.ActualWidth;
            double canvasH = CellsGrid.ActualHeight;

            // Resolutions
            foreach (int height in GridCanvasMath.SaveHeights)
            {
                if (!GridCanvasMath.TryOutputSize(height, canvasW, canvasH, out int width, out int outputH))
                    continue;
                int chosen = height;
                var item = CreateMenuItemButton($"{chosen}p", $"{width}\u00D7{outputH}");
                item.Click += async (_, _) =>
                {
                    SaveFlyout.Hide();
                    await SaveAtHeightAsync(chosen);
                };
                panel.Children.Add(item);
            }

            var pixels = CanvasPixels();
            if (pixels.Width > 0 && pixels.Height > 0
                && pixels.Width <= GridCanvasMath.MaxEdge && pixels.Height <= GridCanvasMath.MaxEdge)
            {
                var windowItem = CreateMenuItemButton("This window", $"{pixels.Width}\u00D7{pixels.Height}");
                windowItem.Click += async (_, _) =>
                {
                    SaveFlyout.Hide();
                    await SavePixelsAsync(pixels.Width, pixels.Height);
                };
                panel.Children.Add(windowItem);
            }

            var custom = CreateMenuItemButton("Custom\u2026");
            custom.Click += async (_, _) =>
            {
                SaveFlyout.Hide();
                int? height = await AskHeightAsync(canvasW, canvasH);
                if (height != null) await SaveAtHeightAsync(height.Value);
            };
            panel.Children.Add(custom);

            // Layout Save & Load
            panel.Children.Add(CreateMenuDivider());

            var saveLayoutItem = CreateMenuItemButton("Save Layout as\u2026", icon: new FontIcon { Glyph = "\uE74E", FontSize = 13, Foreground = _menuWhiteSecondary });
            saveLayoutItem.Click += async (_, _) =>
            {
                SaveFlyout.Hide();
                await SaveLayoutFileAsync();
            };
            panel.Children.Add(saveLayoutItem);

            var openLayoutItem = CreateMenuItemButton("Open Layout\u2026", icon: new FontIcon { Glyph = "\uE8E5", FontSize = 13, Foreground = _menuWhiteSecondary });
            openLayoutItem.Click += async (_, _) =>
            {
                SaveFlyout.Hide();
                await OpenLayoutFileAsync();
            };
            panel.Children.Add(openLayoutItem);

            // Favourites & Recent save locations (last 3 recent, 3 favourites)
            if (MainWindow.Instance != null)
            {
                var favs = MainWindow.Instance.MultiViewFavouriteSaveLocations.Take(3).ToList();
                var recents = MainWindow.Instance.MultiViewRecentSaveLocations
                    .Where(p => !MainWindow.Instance.IsMultiViewFavourite(p))
                    .Take(3)
                    .ToList();

                if (favs.Count > 0 || recents.Count > 0)
                {
                    panel.Children.Add(CreateMenuDivider());
                }

                if (favs.Count > 0)
                {
                    panel.Children.Add(CreateSectionHeader(favs.Count >= 3 ? "Favourites (3 of 3)" : "Favourites"));
                    foreach (var p in favs)
                    {
                        AddFolderAndLayouts(panel, p, isFavourite: true);
                    }
                }

                if (recents.Count > 0)
                {
                    if (favs.Count > 0)
                    {
                        panel.Children.Add(CreateMenuDivider());
                    }
                    panel.Children.Add(CreateSectionHeader("Recent"));
                    foreach (var p in recents)
                    {
                        AddFolderAndLayouts(panel, p, isFavourite: false);
                    }

                    panel.Children.Add(CreateMenuDivider());
                    var clearBtn = CreateMenuItemButton("Clear Recent Locations", icon: new FontIcon { Glyph = "\uE74D", FontSize = 13, Foreground = _menuWhiteSecondary });
                    clearBtn.Click += (_, _) =>
                    {
                        MainWindow.Instance?.ClearMultiViewRecentLocations();
                        BuildSaveFlyoutMenu();
                    };
                    panel.Children.Add(clearBtn);
                }
                else if (favs.Count == 0)
                {
                    panel.Children.Add(CreateMenuDivider());
                    panel.Children.Add(new TextBlock
                    {
                        Text = "No recent locations",
                        FontSize = 12,
                        Foreground = _menuWhiteTertiary,
                        Margin = new Thickness(12, 6, 12, 6),
                        FontStyle = Windows.UI.Text.FontStyle.Italic
                    });
                }
            }

            var scroll = new ScrollViewer
            {
                Content = panel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 540
            };

            SaveFlyout.Content = scroll;
        }

        private Button CreateMenuItemButton(string text, string? rightText = null, IconElement? icon = null)
        {
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (icon != null)
            {
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);
            }

            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = _menuWhitePrimary
            };
            Grid.SetColumn(label, icon != null ? 1 : 0);
            grid.Children.Add(label);

            if (!string.IsNullOrEmpty(rightText))
            {
                var rightLabel = new TextBlock
                {
                    Text = rightText,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = _menuWhiteSecondary
                };
                Grid.SetColumn(rightLabel, 2);
                grid.Children.Add(rightLabel);
            }

            return new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(4),
                MinHeight = 32
            };
        }

        private FrameworkElement CreateMenuDivider()
        {
            return new Rectangle
            {
                Height = 1,
                Margin = new Thickness(4, 4, 4, 4),
                Fill = _menuDividerBrush
            };
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _menuWhiteSecondary,
                Margin = new Thickness(12, 10, 12, 4)
            };
        }

        private void AddFolderAndLayouts(StackPanel panel, string path, bool isFavourite)
        {
            panel.Children.Add(CreateFolderRow(path, isFavourite));

            if (Directory.Exists(path))
            {
                List<string> layoutFiles = new();
                try
                {
                    layoutFiles = Directory.GetFiles(path, "*.mivlayout")
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                        .ToList();
                }
                catch { }

                foreach (var layoutPath in layoutFiles.Take(3))
                {
                    panel.Children.Add(CreateLayoutFileRow(layoutPath));
                }

                if (layoutFiles.Count > 3)
                {
                    panel.Children.Add(CreateMoreLayoutsButton(path, layoutFiles.Count - 3));
                }
            }
        }

        private Button CreateLayoutFileRow(string layoutPath)
        {
            var content = new Grid { ColumnSpacing = 8, Margin = new Thickness(24, 0, 4, 0) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = "\uE8A5",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = _menuWhiteSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);

            string fileName = Path.GetFileName(layoutPath);
            var label = new TextBlock
            {
                Text = fileName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5,
                Foreground = _menuWhitePrimary
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 4, 8, 4),
                CornerRadius = new CornerRadius(4),
                MinHeight = 28,
                Margin = new Thickness(0, 0, 0, 1)
            };

            ToolTipService.SetToolTip(btn, $"Load layout:\n{layoutPath}");
            btn.Click += async (_, _) =>
            {
                SaveFlyout.Hide();
                await LoadLayoutFromPathAsync(layoutPath);
            };

            var contextMenu = new MenuFlyout();
            var loadItem = new MenuFlyoutItem
            {
                Text = "Load Layout",
                Icon = new FontIcon { Glyph = "\uE8E5", FontSize = 13 }
            };
            loadItem.Click += async (_, _) =>
            {
                SaveFlyout.Hide();
                await LoadLayoutFromPathAsync(layoutPath);
            };
            contextMenu.Items.Add(loadItem);

            var revealItem = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uE838", FontSize = 13 }
            };
            revealItem.Click += (_, _) =>
            {
                SaveFlyout.Hide();
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{layoutPath}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            contextMenu.Items.Add(revealItem);

            btn.ContextFlyout = contextMenu;
            return btn;
        }

        private Button CreateMoreLayoutsButton(string folderPath, int extraCount)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(24, 0, 0, 0) };
            content.Children.Add(new FontIcon
            {
                Glyph = "\uE8DA",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = _menuWhiteTertiary,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = $"+{extraCount} more layouts in folder…",
                FontSize = 11.5,
                Foreground = _menuWhiteSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6, 2, 8, 2),
                CornerRadius = new CornerRadius(4),
                MinHeight = 24
            };
            btn.Click += async (_, _) =>
            {
                SaveFlyout.Hide();
                await OpenLayoutFileAsync();
            };
            return btn;
        }

        private Grid CreateFolderRow(string path, bool isFavourite)
        {
            var row = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 1)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool available = Directory.Exists(path);

            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = "\uE8B7",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13.5,
                Foreground = _menuWhiteSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);

            string displayName = available ? path : path + " (not available)";
            var label = new TextBlock
            {
                Text = displayName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = available ? _menuWhitePrimary : _menuWhiteTertiary
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            var openBtn = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
                MinHeight = 32,
                IsEnabled = available
            };
            ToolTipService.SetToolTip(openBtn, $"{path}\nClick to open location in Explorer");
            openBtn.Click += (_, _) =>
            {
                SaveFlyout.Hide();
                if (Directory.Exists(path))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            };
            Grid.SetColumn(openBtn, 0);
            row.Children.Add(openBtn);

            // Star button
            bool full = !isFavourite && (MainWindow.Instance?.MultiViewFavouriteSaveLocations.Count ?? 0) >= 3;
            var starIcon = new FontIcon
            {
                Glyph = isFavourite ? "\uE735" : "\uE734",
                FontSize = 14,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = isFavourite ? _menuGoldStar : _menuWhiteSecondary
            };

            var starBtn = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Content = starIcon,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
                IsEnabled = !full || isFavourite
            };
            string tip = isFavourite ? "Remove from favourites" : (full ? "Favourites limit reached (3 of 3)" : "Add to favourites (up to 3)");
            ToolTipService.SetToolTip(starBtn, tip);
            starBtn.Click += (_, _) =>
            {
                MainWindow.Instance?.ToggleMultiViewFavourite(path);
                BuildSaveFlyoutMenu();
            };
            Grid.SetColumn(starBtn, 1);
            row.Children.Add(starBtn);

            // Context menu on the row
            var contextMenu = new MenuFlyout();
            var favItem = new MenuFlyoutItem
            {
                Text = isFavourite ? "Remove from Favourites" : "Add to Favourites",
                Icon = new FontIcon { Glyph = isFavourite ? "\uE735" : "\uE734", FontSize = 14, Foreground = isFavourite ? _menuGoldStar : _menuWhiteSecondary },
                IsEnabled = !full || isFavourite
            };
            favItem.Click += (_, _) =>
            {
                MainWindow.Instance?.ToggleMultiViewFavourite(path);
                BuildSaveFlyoutMenu();
            };
            contextMenu.Items.Add(favItem);

            var openExpItem = new MenuFlyoutItem
            {
                Text = "Open in File Explorer",
                Icon = new FontIcon { Glyph = "\uE838", FontSize = 14, Foreground = _menuWhiteSecondary }
            };
            openExpItem.Click += (_, _) =>
            {
                SaveFlyout.Hide();
                if (Directory.Exists(path))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
                    }
                    catch { }
                }
            };
            contextMenu.Items.Add(openExpItem);
            openBtn.ContextFlyout = contextMenu;
            return row;
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
                string? savedDir = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(savedDir)) MainWindow.Instance?.AddMultiViewSaveLocation(savedDir);
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync(ex.Message);
            }
            finally
            {
                _savingGrid = false;
                SaveButton.IsEnabled = true;
                SaveText.Text = "Save/Load";
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
                Rect cardBounds = root.TransformToVisual(CellsGrid).TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                if (container.RenderTransform is not CompositeTransform transform) continue;
                shots.Add(new CellShot
                {
                    Path = _items[i].Path,
                    Cell = bounds,
                    Card = cardBounds,
                    ImageW = container.Width,
                    ImageH = container.Height,
                    Scale = transform.ScaleX,
                    TranslateX = transform.TranslateX,
                    TranslateY = transform.TranslateY,
                    NativeW = nativeW,
                    NativeH = nativeH,
                    CanvasW = canvasW,
                    CanvasH = canvasH,
                    Border = GetBorderForItem(_items[i]).Clone()
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

                float rx = (float)((shot.Border?.CornerRadius ?? 0) * scaleX);
                float ry = (float)((shot.Border?.CornerRadius ?? 0) * scaleY);
                double cardX = shot.Card.X * scaleX;
                double cardY = shot.Card.Y * scaleY;
                double cardW = shot.Card.Width * scaleX;
                double cardH = shot.Card.Height * scaleY;

                // 1. Draw Drop Shadow or Ambient Rim Glow
                if (shot.Border != null && shot.Border.ShadowIntensity > 0)
                {
                    using var shadowSession = target.CreateDrawingSession();
                    if (shot.Border.RimGlow)
                    {
                        byte baseAlpha = (byte)Math.Clamp(shot.Border.ShadowIntensity * 1.6, 12, 190);
                        int steps = 4;
                        for (int s = 1; s <= steps; s++)
                        {
                            float expand = (float)(s * scaleX * 1.5);
                            byte a = (byte)(baseAlpha / (s * 1.7));
                            var glowCol = Color.FromArgb(a, 255, 255, 255);
                            float sx = (float)(cardX - expand);
                            float sy = (float)(cardY - expand);
                            float sw = (float)(cardW + 2 * expand);
                            float sh = (float)(cardH + 2 * expand);
                            float srx = (float)(rx + expand);
                            float sry = (float)(ry + expand);

                            if (rx > 0)
                                shadowSession.DrawRoundedRectangle(sx, sy, sw, sh, srx, sry, glowCol, (float)(1.5 * scaleX));
                            else
                                shadowSession.DrawRectangle(sx, sy, sw, sh, glowCol, (float)(1.5 * scaleX));
                        }
                    }
                    else
                    {
                        double shadowOffset = shot.Border.ShadowIntensity * scaleX * 0.12;
                        byte shadowAlpha = (byte)Math.Clamp(shot.Border.ShadowIntensity * 1.4, 0, 160);
                        var shadowColor = Color.FromArgb(shadowAlpha, 0, 0, 0);

                        float shX = (float)(cardX + shadowOffset * 0.5);
                        float shY = (float)(cardY + shadowOffset * 0.7);
                        if (rx > 0)
                            shadowSession.FillRoundedRectangle(shX, shY, (float)cardW, (float)cardH, rx, ry, shadowColor);
                        else
                            shadowSession.FillRectangle(shX, shY, (float)cardW, (float)cardH, shadowColor);
                    }
                }

                // 2. Draw Image with Rounded or Sharp Clipping
                using (var session = target.CreateDrawingSession())
                {
                    string strokeStyle = shot.Border?.StrokeStyle ?? "Solid";
                    float strokeW = (float)((shot.Border?.Thickness ?? 0) * scaleX);
                    if (string.Equals(strokeStyle, "InnerMuted", StringComparison.OrdinalIgnoreCase)) strokeW = (float)(1.0 * scaleX);

                    if (rx > 0 && ry > 0)
                    {
                        float innerRx = Math.Max(0, rx - strokeW / 2.0f);
                        float innerRy = Math.Max(0, ry - strokeW / 2.0f);
                        using var clip = CanvasGeometry.CreateRoundedRectangle(device, (float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0), innerRx, innerRy);
                        using (session.CreateLayer(1f, clip))
                        {
                            session.DrawImage(bitmap, dest);
                        }
                    }
                    else
                    {
                        using (session.CreateLayer(1f, new Rect(x0, y0, x1 - x0, y1 - y0)))
                        {
                            session.DrawImage(bitmap, dest);
                        }
                    }

                    // 3. Draw Stroke (Solid, Dashed, InnerMuted, Gradient)
                    if (string.Equals(strokeStyle, "InnerMuted", StringComparison.OrdinalIgnoreCase))
                    {
                        float sW = (float)Math.Max(1, 1.0 * scaleX);
                        var strokeColor = Color.FromArgb(45, 255, 255, 255);
                        float half = sW / 2.0f;
                        float sx = (float)(cardX + half);
                        float sy = (float)(cardY + half);
                        float sw = (float)Math.Max(1, cardW - sW);
                        float sh = (float)Math.Max(1, cardH - sW);
                        float srx = Math.Max(0, rx - half);
                        float sry = Math.Max(0, ry - half);

                        if (rx > 0)
                            session.DrawRoundedRectangle(sx, sy, sw, sh, srx, sry, strokeColor, sW);
                        else
                            session.DrawRectangle(sx, sy, sw, sh, strokeColor, sW);
                    }
                    else if (string.Equals(strokeStyle, "Gradient", StringComparison.OrdinalIgnoreCase))
                    {
                        float sW = (float)(Math.Max(1, shot.Border?.Thickness ?? 2) * scaleX);
                        var primaryColor = ParseColor(shot.Border?.Color);
                        Color secondaryColor;
                        if (!string.IsNullOrEmpty(shot.Border?.SecondaryColor) && shot.Border.SecondaryColor != "#00000000")
                        {
                            secondaryColor = ParseColor(shot.Border.SecondaryColor);
                        }
                        else
                        {
                            secondaryColor = Color.FromArgb((byte)(primaryColor.A * 0.40f), primaryColor.R, primaryColor.G, primaryColor.B);
                        }

                        float half = sW / 2.0f;
                        float sx = (float)(cardX + half);
                        float sy = (float)(cardY + half);
                        float sw = (float)Math.Max(1, cardW - sW);
                        float sh = (float)Math.Max(1, cardH - sW);
                        float srx = Math.Max(0, rx - half);
                        float sry = Math.Max(0, ry - half);

                        using var gradBrush = new CanvasLinearGradientBrush(device, primaryColor, secondaryColor)
                        {
                            StartPoint = new System.Numerics.Vector2((float)cardX, (float)cardY),
                            EndPoint = new System.Numerics.Vector2((float)(cardX + cardW), (float)(cardY + cardH))
                        };

                        if (rx > 0)
                            session.DrawRoundedRectangle(sx, sy, sw, sh, srx, sry, gradBrush, sW);
                        else
                            session.DrawRectangle(sx, sy, sw, sh, gradBrush, sW);
                    }
                    else if (string.Equals(strokeStyle, "Dashed", StringComparison.OrdinalIgnoreCase))
                    {
                        float sW = (float)(Math.Max(1, shot.Border?.Thickness ?? 2) * scaleX);
                        var strokeColor = ParseColor(shot.Border?.Color);
                        if (strokeColor.A > 0)
                        {
                            float half = sW / 2.0f;
                            float sx = (float)(cardX + half);
                            float sy = (float)(cardY + half);
                            float sw = (float)Math.Max(1, cardW - sW);
                            float sh = (float)Math.Max(1, cardH - sW);
                            float srx = Math.Max(0, rx - half);
                            float sry = Math.Max(0, ry - half);

                            var canvasStrokeStyle = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                            if (rx > 0)
                                session.DrawRoundedRectangle(sx, sy, sw, sh, srx, sry, strokeColor, sW, canvasStrokeStyle);
                            else
                                session.DrawRectangle(sx, sy, sw, sh, strokeColor, sW, canvasStrokeStyle);
                        }
                    }
                    else // Solid
                    {
                        if (strokeW > 0 && !string.IsNullOrEmpty(shot.Border?.Color))
                        {
                            var strokeColor = ParseColor(shot.Border.Color);
                            if (strokeColor.A > 0)
                            {
                                float half = strokeW / 2.0f;
                                float sx = (float)(cardX + half);
                                float sy = (float)(cardY + half);
                                float sw = (float)Math.Max(1, cardW - strokeW);
                                float sh = (float)Math.Max(1, cardH - strokeW);
                                float srx = Math.Max(0, rx - half);
                                float sry = Math.Max(0, ry - half);

                                if (rx > 0)
                                    session.DrawRoundedRectangle(sx, sy, sw, sh, srx, sry, strokeColor, strokeW);
                                else
                                    session.DrawRectangle(sx, sy, sw, sh, strokeColor, strokeW);
                            }
                        }
                    }
                }
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
                    Name = item.Name,
                    Border = GetBorderForItem(item).Clone()
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
                string? savedDir = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(savedDir)) MainWindow.Instance?.AddMultiViewSaveLocation(savedDir);
                await ShowExportMessageAsync("Layout saved.");
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync($"Failed to save layout: {ex.Message}");
            }
        }

        public async Task LoadLayoutFromPathAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                await ShowExportMessageAsync($"File not found:\n{filePath}");
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                var layoutFile = MultiViewLayoutFile.FromJson(json);
                if (layoutFile == null)
                {
                    await ShowExportMessageAsync("Could not read layout file.");
                    return;
                }
                string? openedDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(openedDir)) MainWindow.Instance?.AddMultiViewSaveLocation(openedDir);
                ApplyLayoutFile(layoutFile);
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync($"Failed to load layout: {ex.Message}");
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

            await LoadLayoutFromPathAsync(file.Path);
        }

        public void ApplyLayoutFile(MultiViewLayoutFile layoutFile)
        {
            if (layoutFile == null) return;

            if (layoutFile.Photos != null && layoutFile.Photos.Count > 0)
            {
                var items = new List<ImageItem>();
                foreach (var photo in layoutFile.Photos)
                {
                    var it = new ImageItem
                    {
                        Path = photo.Path ?? string.Empty,
                        Name = !string.IsNullOrEmpty(photo.Name) ? photo.Name : Path.GetFileName(photo.Path ?? string.Empty),
                        Dispatcher = this.DispatcherQueue
                    };
                    items.Add(it);
                    if (photo.Border != null && !string.IsNullOrEmpty(it.Path))
                    {
                        _itemBorders[it.Path] = photo.Border.Clone();
                    }
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

            ApplyBorderToAllCells();

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
            public Rect Card;
            public double ImageW;
            public double ImageH;
            public double Scale;
            public double TranslateX;
            public double TranslateY;
            public int NativeW;
            public int NativeH;
            public double CanvasW;
            public double CanvasH;
            public CellBorderDto? Border;
        }
    }
}

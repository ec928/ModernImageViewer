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

        private const double SaveMenuWidth = 660;
        private const double SaveMenuImageColumn = 216;
        private const double MenuRowHeight = 40;
        private const double MenuFolderHeight = 40;
        private const double MenuLayoutHeight = 35;
        private const double MenuHeaderHeight = 36;
        private const double MenuDividerHeight = 12;
        private const int MenuLayoutsPerFolder = 3;

        private void SaveFlyout_Opening(object sender, object e)
        {
            BuildSaveFlyoutMenu();
        }

        private void BuildSaveFlyoutMenu()
        {
            var panel = new StackPanel { Spacing = 1 };
            panel.Children.Add(CreateSectionHeader("Save image"));

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
            var imageColumn = panel;
            panel = new StackPanel { Spacing = 1 };
            panel.Children.Add(CreateSectionHeader("Layouts"));

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

                // Every folder always shows. Its layouts take whatever height the window has left.
                var folders = favs.Concat(recents).ToList();
                var files = folders.Select(LayoutFilesIn).ToList();
                double taken = MenuHeaderHeight + 2 * MenuRowHeight + folders.Count * MenuFolderHeight;
                if (favs.Count > 0) taken += MenuDividerHeight + MenuHeaderHeight;
                if (recents.Count > 0) taken += 2 * MenuDividerHeight + MenuHeaderHeight + MenuRowHeight;
                int spare = (int)Math.Max(0, (SaveMenuHeight() - taken) / MenuLayoutHeight);
                var shown = MenuRows.Share(files.Select(f => f.Count).ToList(), spare, MenuLayoutsPerFolder);

                for (int i = 0; i < folders.Count; i++)
                {
                    if (i == 0 && favs.Count > 0)
                        panel.Children.Add(CreateSectionHeader(favs.Count >= 3 ? "Favourites (3 of 3)" : "Favourites"));
                    if (i == favs.Count)
                    {
                        if (favs.Count > 0) panel.Children.Add(CreateMenuDivider());
                        panel.Children.Add(CreateSectionHeader("Recent"));
                    }
                    AddFolderAndLayouts(panel, folders[i], i < favs.Count, files[i], shown[i]);
                }

                if (recents.Count > 0)
                {
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

            var columns = new Grid
            {
                Width = Math.Min(SaveMenuWidth, Math.Max(320, RootGrid.ActualWidth - 32)),
                ColumnSpacing = 8,
                XYFocusKeyboardNavigation = Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled
            };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SaveMenuImageColumn) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rule = new Rectangle { Fill = _menuDividerBrush, Margin = new Thickness(0, 8, 0, 8) };
            Grid.SetColumn(rule, 1);
            Grid.SetColumn(panel, 2);
            columns.Children.Add(imageColumn);
            columns.Children.Add(rule);
            columns.Children.Add(panel);

            // Fits as it stands. It scrolls only in a window too short for the image sizes themselves.
            SaveFlyout.Content = new ScrollViewer
            {
                Content = columns,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SaveMenuHeight()
            };
        }

        /// <summary>The height free above the bar.</summary>
        private double SaveMenuHeight() => Math.Max(200, RootGrid.ActualHeight - UnifiedHud.ActualHeight - UnifiedHud.Margin.Bottom - 32);

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

        private static List<string> LayoutFilesIn(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    return Directory.GetFiles(path, "*.mivlayout").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return new List<string>();
        }

        private void AddFolderAndLayouts(StackPanel panel, string path, bool isFavourite, List<string> layoutFiles, int shown)
        {
            panel.Children.Add(CreateFolderRow(path, isFavourite, layoutFiles.Count - shown));
            foreach (var layoutPath in layoutFiles.Take(shown))
                panel.Children.Add(CreateLayoutFileRow(layoutPath));
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

            string fileName = Path.GetFileNameWithoutExtension(layoutPath);
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

        private Grid CreateFolderRow(string path, bool isFavourite, int moreLayouts)
        {
            var row = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 1)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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

            string shortName = FolderLabel.Short(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            string displayName = available ? shortName : shortName + " (not available)";
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

            if (moreLayouts > 0)
            {
                var moreBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                    Content = new TextBlock { Text = "+" + moreLayouts, FontSize = 12, Foreground = _menuWhiteSecondary },
                    Height = 32,
                    Padding = new Thickness(6, 0, 6, 0),
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(moreBtn, moreLayouts + " more in this folder. Click to choose a layout.");
                moreBtn.Click += async (_, _) =>
                {
                    SaveFlyout.Hide();
                    await OpenLayoutFileAsync();
                };
                Grid.SetColumn(moreBtn, 1);
                row.Children.Add(moreBtn);
            }

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
            Grid.SetColumn(starBtn, 2);
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

        /// <summary>A photo waiting off screen has no cell, and a missing file will never finish.</summary>
        private bool AnyPhotoLoading() =>
            _cellRoots.Any(root => root?.Tag is CellParts parts && !parts.Bound.Failed && !IsEmptyCell(parts.Bound.Item) && !CellImageReady(parts));

        private async Task SavePixelsAsync(int width, int height, bool preferPng = false)
        {
            if (_savingGrid) return;
            var shots = CaptureCells();
            if (shots.Count == 0)
            {
                await ShowExportMessageAsync("Nothing to save.");
                return;
            }
            if (AnyPhotoLoading())
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
            if (preferPng) picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG Image", new List<string> { ".jpg" });
            if (!preferPng) picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) return;

            _savingGrid = true;
            SaveButton.IsEnabled = false;
            SaveText.Text = "Saving\u2026";
            try
            {
                using var image = await RenderCanvasAsync(shots, width, height, _canvasColor, _canvasSurface);
                using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                stream.Size = 0;
                if (string.Equals(Path.GetExtension(file.Path), ".png", StringComparison.OrdinalIgnoreCase))
                    await image.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                else
                    await image.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, 0.95f);
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
            int count = Math.Min(GridCellCount, _cellRoots.Length);
            double canvasW = CellsGrid.ActualWidth;
            double canvasH = CellsGrid.ActualHeight;
            if (canvasW < 1 || canvasH < 1) return shots;

            // Grid cells first, then freeform cells from back to front, so the export stacks like the screen.
            var slots = new List<int>(Enumerable.Range(0, count));
            foreach (var card in _freeformCards.OrderBy(c => c.ZIndex))
            {
                int slot = _items.IndexOf(card.Item);
                if (slot >= 0 && slot < _cellRoots.Length) slots.Add(slot);
            }

            foreach (int i in slots)
            {
                var rootElem = _cellRoots[i];
                if (rootElem == null) continue;
                Border? root = CardBorder(rootElem);
                if (root == null || root.Child is not Grid cell || cell.ActualWidth < 1 || cell.ActualHeight < 1)
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

                // Measured inside the cell, then placed, so a turned window gives its own rectangle and not the box around it.
                var floating = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Container, rootElem));
                Point origin = floating != null
                    ? new Point(Canvas.GetLeft(rootElem), Canvas.GetTop(rootElem))
                    : rootElem.TransformToVisual(CellsGrid).TransformPoint(new Point(0, 0));
                Rect Placed(FrameworkElement element)
                {
                    Rect local = element.TransformToVisual(rootElem).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    return new Rect(origin.X + local.X, origin.Y + local.Y, local.Width, local.Height);
                }
                Rect bounds = new Rect(origin.X, origin.Y, rootElem.ActualWidth, rootElem.ActualHeight);
                Rect cardBounds = Placed(root);
                Rect photoBounds = Placed(cell);
                if (container.RenderTransform is not CompositeTransform transform) continue;
                shots.Add(new CellShot
                {
                    Path = _items[i].Path,
                    Cell = bounds,
                    Card = cardBounds,
                    Photo = photoBounds,
                    ImageW = container.Width,
                    ImageH = container.Height,
                    Scale = transform.ScaleX,
                    TranslateX = transform.TranslateX,
                    TranslateY = transform.TranslateY,
                    NativeW = nativeW,
                    NativeH = nativeH,
                    CanvasW = canvasW,
                    CanvasH = canvasH,
                    Border = GetBorderForItem(_items[i]).Clone(),
                    Geometry = GetGeometry(_items[i]).Clone()
                });
            }

            return shots;
        }

        /// <summary>Draw the canvas and its cells. The caller owns the image and disposes it.</summary>
        private static async Task<CanvasRenderTarget> RenderCanvasAsync(List<CellShot> shots, int width, int height, string canvasColor, string canvasSurface)
        {
            var device = CanvasDevice.GetSharedDevice();
            var target = new CanvasRenderTarget(device, width, height, 96);
            using (var clear = target.CreateDrawingSession())
            {
                var near = ToColor(FrameColor.Parse(canvasColor));
                var far = ToColor(CanvasLook.FarColor(canvasColor));
                clear.Clear(near);
                if (canvasSurface == CanvasLook.Gradient)
                {
                    using var brush = new CanvasLinearGradientBrush(device, near, far)
                    {
                        StartPoint = new System.Numerics.Vector2(0, 0),
                        EndPoint = new System.Numerics.Vector2(width, height)
                    };
                    clear.FillRectangle(0, 0, width, height, brush);
                }
                else if (canvasSurface == CanvasLook.Vignette)
                {
                    using var brush = new CanvasRadialGradientBrush(device, near, far)
                    {
                        Center = new System.Numerics.Vector2(width / 2f, height / 2f),
                        RadiusX = width * 0.75f,
                        RadiusY = height * 0.75f
                    };
                    clear.FillRectangle(0, 0, width, height, brush);
                }
            }

            foreach (var shot in shots)
            {
                // Hidden means the whole cell: no image, mat, frame or shadow.

                Rect photo = shot.Photo.Width >= 1 && shot.Photo.Height >= 1 ? shot.Photo
                    : (shot.Card.Width >= 1 && shot.Card.Height >= 1 ? shot.Card : shot.Cell);
                var placed = GridCanvasMath.Place(photo.Width, photo.Height, shot.ImageW, shot.ImageH, shot.Scale, shot.TranslateX, shot.TranslateY);
                double scaleX = width / shot.CanvasW;
                double scaleY = height / shot.CanvasH;
                var decode = GridCanvasMath.DecodeSize(shot.NativeW, shot.NativeH, placed.Width * scaleX, placed.Height * scaleY);
                using var bitmap = await DecodeForCellAsync(shot.Path, device, decode.Width, decode.Height);
                if (bitmap == null) continue;

                double x0 = Math.Floor(photo.X * scaleX);
                double y0 = Math.Floor(photo.Y * scaleY);
                double x1 = Math.Ceiling((photo.X + photo.Width) * scaleX);
                double y1 = Math.Ceiling((photo.Y + photo.Height) * scaleY);
                x0 = Math.Clamp(x0, 0, width);
                y0 = Math.Clamp(y0, 0, height);
                x1 = Math.Clamp(x1, 0, width);
                y1 = Math.Clamp(y1, 0, height);
                if (x1 <= x0 || y1 <= y0) continue;

                var dest = new Rect(
                    (photo.X + placed.Left) * scaleX,
                    (photo.Y + placed.Top) * scaleY,
                    placed.Width * scaleX,
                    placed.Height * scaleY);

                float rx = (float)((shot.Border?.CornerRadius ?? 0) * scaleX);
                float ry = (float)((shot.Border?.CornerRadius ?? 0) * scaleY);
                double cardX = shot.Card.X * scaleX;
                double cardY = shot.Card.Y * scaleY;
                double cardW = shot.Card.Width * scaleX;
                double cardH = shot.Card.Height * scaleY;

                // A freeform window turns as a whole and may have an outline of its own.
                var cellRect = new Rect(shot.Cell.X * scaleX, shot.Cell.Y * scaleY, shot.Cell.Width * scaleX, shot.Cell.Height * scaleY);
                var cellCenter = new System.Numerics.Vector2((float)(cellRect.X + cellRect.Width / 2), (float)(cellRect.Y + cellRect.Height / 2));
                var windowTurn = System.Numerics.Matrix3x2.CreateRotation((float)((shot.Geometry?.WindowRotation ?? 0) * Math.PI / 180), cellCenter);
                CanvasGeometry? Polygon((double X, double Y)[]? points) => points == null ? null : CanvasGeometry.CreatePolygon(device,
                    points.Select(p => new System.Numerics.Vector2((float)p.X, (float)p.Y)).ToArray());

                // The mat fills the outer outline. The gutter insets the photo and frame onto the inner one.
                double gutterPixels = cardX - cellRect.X;
                using CanvasGeometry? shape = Polygon(WindowShapes.Placed(shot.Geometry, cellRect.X, cellRect.Y, cellRect.Width, cellRect.Height,
                    rx > 0 ? rx + gutterPixels : 0, concaveRadius: Math.Max(0, rx - gutterPixels)));
                using CanvasGeometry? cardShape = Polygon(WindowShapes.Placed(shot.Geometry, cellRect.X, cellRect.Y, cellRect.Width, cellRect.Height, rx, gutterPixels));

                // The mat is the cell around the photo. The photo is drawn inside the card, so it does not cover the gutter.
                var mat = FrameColor.MatFill(shot.Border?.GutterMaterial, shot.Border?.GutterColor);
                if (mat.A > 0)
                {
                    using var matSession = target.CreateDrawingSession();
                    matSession.Transform = windowTurn;
                    float slotX = (float)(shot.Cell.X * scaleX);
                    float slotY = (float)(shot.Cell.Y * scaleY);
                    float slotW = (float)(shot.Cell.Width * scaleX);
                    float slotH = (float)(shot.Cell.Height * scaleY);
                    if (shape != null)
                        matSession.FillGeometry(shape, Color.FromArgb(mat.A, mat.R, mat.G, mat.B));
                    else
                        matSession.FillRectangle(slotX, slotY, slotW, slotH, Color.FromArgb(mat.A, mat.R, mat.G, mat.B));
                }

                // 1. Draw Drop Shadow or Ambient Rim Glow
                if (shot.Border != null && shot.Border.ShadowIntensity > 0)
                {
                    using var shadowSession = target.CreateDrawingSession();
                    shadowSession.Transform = windowTurn;
                    if (shape == null && shot.Border.RimGlow)
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
                        float slotX = (float)(shot.Cell.X * scaleX);
                        float slotY = (float)(shot.Cell.Y * scaleY);
                        int slotW = Math.Max(1, (int)Math.Round(shot.Cell.Width * scaleX));
                        int slotH = Math.Max(1, (int)Math.Round(shot.Cell.Height * scaleY));
                        var shadeOutline = WindowShapes.Placed(shot.Geometry, 0, 0, slotW, slotH, rx, gutterPixels);
                        var shade = shadeOutline != null
                            ? FrameShadow.RenderAlpha(slotW, slotH, shadeOutline, shot.Border.ShadowIntensity, scaleX, scaleY)
                            : FrameShadow.RenderAlpha(
                                slotW,
                                slotH,
                                cardX - slotX,
                                cardY - slotY,
                                cardW,
                                cardH,
                                shot.Border.CornerRadius * scaleX,
                                shot.Border.ShadowIntensity,
                                scaleX,
                                scaleY);
                        var shadePixels = new byte[slotW * slotH * 4];
                        for (int p = 0; p < shade.Length; p++)
                        {
                            byte a = shade[p];
                            if (a == 0) continue;
                            shadePixels[(p * 4) + 3] = a;
                        }
                        using var shadeBitmap = CanvasBitmap.CreateFromBytes(
                            device,
                            shadePixels,
                            slotW,
                            slotH,
                            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                        using (shape == null ? null : shadowSession.CreateLayer(1f, shape))
                        {
                            shadowSession.DrawImage(shadeBitmap, slotX, slotY);
                        }
                    }
                }

                // 2. Draw Image with Rounded or Sharp Clipping
                using (var session = target.CreateDrawingSession())
                {
                    session.Transform = windowTurn;
                    string strokeStyle = shot.Border?.StrokeStyle ?? "Solid";
                    float strokeW = (float)(Math.Max(0, shot.Border?.Thickness ?? 0) * scaleX);

                    void DrawPhoto()
                    {
                        var center = new System.Numerics.Vector2((float)(dest.X + dest.Width / 2), (float)(dest.Y + dest.Height / 2));
                        var flip = System.Numerics.Matrix3x2.CreateScale(
                            shot.Geometry?.ImageFlipH == true ? -1f : 1f,
                            shot.Geometry?.ImageFlipV == true ? -1f : 1f,
                            center);
                        var turn = System.Numerics.Matrix3x2.CreateRotation((float)((shot.Geometry?.ImageAngle ?? 0) * Math.PI / 180), center);
                        session.Transform = flip * turn * windowTurn;
                        session.DrawImage(bitmap, dest);
                        session.Transform = windowTurn;
                    }

                    using (cardShape == null ? null : session.CreateLayer(1f, cardShape))
                    {
                        if (rx > 0 && ry > 0)
                        {
                            float innerRx = Math.Max(0, rx - strokeW / 2.0f);
                            float innerRy = Math.Max(0, ry - strokeW / 2.0f);
                            using var clip = CanvasGeometry.CreateRoundedRectangle(device, (float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0), innerRx, innerRy);
                            using (session.CreateLayer(1f, clip))
                            {
                                DrawPhoto();
                            }
                        }
                        else
                        {
                            using (session.CreateLayer(1f, new Rect(x0, y0, x1 - x0, y1 - y0)))
                            {
                                DrawPhoto();
                            }
                        }
                    }

                    // 3. Draw Stroke (Solid, Dashed, InnerMuted, Gradient)
                    if (shape != null)
                    {
                        // Pulled in by half the stroke, as on screen, so its outer edge meets the photo's edge.
                        using var strokeShape = Polygon(WindowShapes.Placed(shot.Geometry, cellRect.X, cellRect.Y, cellRect.Width, cellRect.Height,
                            Math.Max(0, rx - strokeW / 2), gutterPixels + strokeW / 2, rx + strokeW / 2))!;
                        if (strokeW > 0 && string.Equals(strokeStyle, "Gradient", StringComparison.OrdinalIgnoreCase))
                        {
                            var start = FrameColor.Parse(shot.Border?.Color);
                            var end = FrameColor.GradientEnd(shot.Border?.SecondaryColor, start);
                            using var outlineBrush = new CanvasLinearGradientBrush(device, ToColor(start), ToColor(end))
                            {
                                StartPoint = new System.Numerics.Vector2((float)cardX, (float)cardY),
                                EndPoint = new System.Numerics.Vector2((float)(cardX + cardW), (float)(cardY + cardH))
                            };
                            session.DrawGeometry(strokeShape, outlineBrush, strokeW);
                        }
                        else
                        {
                            var outlineColor = string.Equals(strokeStyle, "InnerMuted", StringComparison.OrdinalIgnoreCase)
                                ? ToColor(FrameColor.MutedStroke(shot.Border?.Color))
                                : ParseColor(shot.Border?.Color);
                            if (strokeW > 0 && outlineColor.A > 0)
                            {
                                if (string.Equals(strokeStyle, "Dashed", StringComparison.OrdinalIgnoreCase))
                                    session.DrawGeometry(strokeShape, outlineColor, strokeW, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
                                else
                                    session.DrawGeometry(strokeShape, outlineColor, strokeW);
                            }
                        }
                    }
                    else if (string.Equals(strokeStyle, "InnerMuted", StringComparison.OrdinalIgnoreCase))
                    {
                        if (strokeW <= 0) { }
                        else
                        {
                        float sW = strokeW;
                        var muted = FrameColor.MutedStroke(shot.Border?.Color);
                        var strokeColor = Color.FromArgb(muted.A, muted.R, muted.G, muted.B);
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
                    }
                    else if (string.Equals(strokeStyle, "Gradient", StringComparison.OrdinalIgnoreCase))
                    {
                        float sW = strokeW;
                        var primaryRgba = FrameColor.Parse(shot.Border?.Color);
                        var endRgba = FrameColor.GradientEnd(shot.Border?.SecondaryColor, primaryRgba);
                        var primaryColor = Color.FromArgb(primaryRgba.A, primaryRgba.R, primaryRgba.G, primaryRgba.B);
                        var secondaryColor = Color.FromArgb(endRgba.A, endRgba.R, endRgba.G, endRgba.B);

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

                        if (sW > 0 && rx > 0)
                            session.DrawRoundedRectangle(sx, sy, sw, sh, srx, sry, gradBrush, sW);
                        else if (sW > 0)
                            session.DrawRectangle(sx, sy, sw, sh, gradBrush, sW);
                    }
                    else if (string.Equals(strokeStyle, "Dashed", StringComparison.OrdinalIgnoreCase))
                    {
                        float sW = strokeW;
                        var strokeColor = ParseColor(shot.Border?.Color);
                        if (strokeColor.A > 0 && sW > 0)
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

            return target;
        }

        private async Task CopyCanvasToClipboardAsync()
        {
            if (_savingGrid) return;
            var shots = CaptureCells();
            if (shots.Count == 0) return;
            if (AnyPhotoLoading())
            {
                await ShowExportMessageAsync("A photo is still loading.");
                return;
            }
            var (width, height) = CanvasPixels();
            if (width < 1 || height < 1) return;

            _savingGrid = true;
            try
            {
                using var image = await RenderCanvasAsync(shots, width, height, _canvasColor, _canvasSurface);
                var stream = new InMemoryRandomAccessStream();
                await image.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                stream.Seek(0);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage
                {
                    RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                };
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            }
            catch (Exception ex)
            {
                await ShowExportMessageAsync(ex.Message);
            }
            finally
            {
                _savingGrid = false;
            }
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

            await WriteLayoutFileAsync(file, BuildLayoutFile());
        }

        /// <summary>The whole session as a layout file. Saving and undo both use this.</summary>
        private MultiViewLayoutFile BuildLayoutFile()
        {
            WriteAllGridWeights();

            var layoutFile = new MultiViewLayoutFile
            {
                Created = DateTime.UtcNow,
                Canvas = new CanvasLayoutDto
                {
                    Aspect = AspectText?.Text,
                    BackgroundColor = _canvasColor,
                    Surface = _canvasSurface,
                    Width = RootGrid.ActualWidth,
                    Height = RootGrid.ActualHeight
                },
                State = new LayoutStateDto
                {
                    Lines = _lines,
                    LineCounts = _lineCounts != null ? new List<int>(_lineCounts) : null,
                    Flipped = _flipped,
                    HeroActive = _heroActive,
                    HeroKind = _heroKind.ToString(),
                    HeroPosition = _heroPosition,
                    IsFillMode = _isFillMode,
                    FramedSlots = _framedSlots
                },
                Mosaic = _mosaic != null ? MosaicNodeDto.FromMosaicLayout(_mosaic) : null
            };

            int gridCount = GridCellCount;
            for (int i = 0; i < gridCount; i++)
            {
                var item = _items[i];
                var slotDto = new PhotoSlotDto
                {
                    Slot = i,
                    Path = item.Path ?? string.Empty,
                    Name = item.Name,
                    Border = GetBorderForItem(item).Clone(),
                    Geometry = GetGeometry(item).Clone()
                };

                if (i < _cellRoots.Length && _cellRoots[i]?.Tag is CellParts parts)
                {
                    slotDto.Zoom = parts.Transform.ScaleX;
                    slotDto.PanX = parts.Transform.TranslateX;
                    slotDto.PanY = parts.Transform.TranslateY;
                }

                layoutFile.Photos.Add(slotDto);
            }

            if (_freeformCards.Count > 0)
            {
                layoutFile.Freeform = new List<FreeformCellDto>();
                foreach (var card in _freeformCards)
                {
                    layoutFile.Freeform.Add(new FreeformCellDto
                    {
                        SourceSlot = card.SourceSlot,
                        Path = card.Item.Path ?? string.Empty,
                        Name = card.Item.Name,
                        X = Canvas.GetLeft(card.Container),
                        Y = Canvas.GetTop(card.Container),
                        Width = card.Container.Width,
                        Height = card.Container.Height,
                        ZIndex = Canvas.GetZIndex(card.Container),
                        Zoom = (card.Container.Tag as CellParts)?.Transform?.ScaleX,
                        PanX = (card.Container.Tag as CellParts)?.Transform?.TranslateX,
                        PanY = (card.Container.Tag as CellParts)?.Transform?.TranslateY,
                        Border = GetBorderForItem(card.Item).Clone(),
                        Geometry = GetGeometry(card.Item).Clone()
                    });
                }
            }

            return layoutFile;
        }

        private async Task WriteLayoutFileAsync(StorageFile file, MultiViewLayoutFile layoutFile)
        {
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

        /// <summary>A layout file holds only the path, and Solo's details line needs the date and size.</summary>
        private static void FillFileDetails(ImageItem item)
        {
            try
            {
                var info = new FileInfo(item.Path);
                if (!info.Exists) return;
                item.DateModified = info.LastWriteTime;
                item.SizeString = (info.Length / 1024.0 < 1024) ? $"{info.Length / 1024} KB" : $"{(info.Length / 1048576.0):F2} MB";
            }
            catch (Exception) when (item.Path != null)
            {
                // An unusable path shows as File not found on its cell.
            }
        }

        public void ApplyLayoutFile(MultiViewLayoutFile layoutFile) => ApplyLayoutFile(layoutFile, restoreAspect: true);

        private void ApplyLayoutFile(MultiViewLayoutFile layoutFile, bool restoreAspect)
        {
            if (layoutFile == null) return;

            // An undo returns to photos already known, so their thumbnails and details carry over.
            var known = new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var old in _items)
            {
                if (!string.IsNullOrEmpty(old.Path)) known[old.Path] = old;
            }

            // Clear the cells of the session being replaced before anything counts them.
            ClearAllFreeformCards();
            SetCanvas(layoutFile.Canvas?.BackgroundColor, layoutFile.Canvas?.Surface);

            if (layoutFile.Photos != null && layoutFile.Photos.Count > 0)
            {
                _itemBorders.Clear();
                _cellGeometry.Clear();
                var items = new List<ImageItem>();
                foreach (var photo in layoutFile.Photos)
                {
                    var it = new ImageItem
                    {
                        // Layouts saved when cells could be hidden: those cells are now empty.
                        Path = photo.Hidden ? string.Empty : photo.Path ?? string.Empty,
                        Name = photo.Hidden ? string.Empty : !string.IsNullOrEmpty(photo.Name) ? photo.Name : Path.GetFileName(photo.Path ?? string.Empty),
                        Dispatcher = this.DispatcherQueue
                    };
                    if (known.TryGetValue(it.Path, out var seen))
                    {
                        it.Thumbnail = seen.Thumbnail;
                        it.DateModified = seen.DateModified;
                        it.SizeString = seen.SizeString;
                    }
                    else
                    {
                        FillFileDetails(it);
                    }
                    items.Add(it);
                    if (photo.Border != null)
                    {
                        _itemBorders[it] = photo.Border.Clone();
                    }
                    if (photo.Geometry != null)
                        _cellGeometry[it] = photo.Geometry.Clone();
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
                _framedSlots = layoutFile.State.FramedSlots;
            }

            // A hand-edited or damaged tree falls back to the plain layout, rather than leaving empty cells.
            if (layoutFile.Mosaic != null && layoutFile.Mosaic.FitsPhotos(VisibleCount(_items.Count)))
            {
                _mosaic = layoutFile.Mosaic.ToMosaicLayout();
                _mosaicCustom = true;
                _keepMosaic = false;
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

            if (layoutFile.Freeform != null && layoutFile.Freeform.Count > 0)
            {
                foreach (var dto in layoutFile.Freeform)
                {
                    int grid = GridCellCount;
                    var sourceItem = dto.SourceSlot >= 0 && dto.SourceSlot < grid
                        ? _items[dto.SourceSlot]
                        : _items.FirstOrDefault();
                    if (sourceItem != null)
                        SpawnFreeformCell(sourceItem, dto);
                }
            }

            // The saved cells are in the units of the canvas they were saved on. A file with no size is taken as this canvas.
            double liveW = RootGrid.ActualWidth;
            double liveH = RootGrid.ActualHeight;
            double savedW = layoutFile.Canvas?.Width ?? 0;
            double savedH = layoutFile.Canvas?.Height ?? 0;
            _freeformBasis = savedW > 0 && savedH > 0 ? new Size(savedW, savedH) : new Size(liveW, liveH);
            RescaleFreeformCards(liveW, liveH);

            for (int i = 0; i < _cellRoots.Length; i++)
                ShowEmptyState(i);

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

            if (!_restoringUndo) ResetUndo();

            // Aspect ratio if specified
            if (restoreAspect && !string.IsNullOrWhiteSpace(layoutFile.Canvas?.Aspect))
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
            public Rect Photo;
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
            public CellGeometryDto? Geometry;
        }
    }
}

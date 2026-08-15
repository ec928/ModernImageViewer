using Microsoft.Graphics.Canvas;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ModernImageViewer.Collage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace ModernImageViewer.Collage
{
    public sealed partial class CollageEditorWindow : Window
    {
        private CollageProject _project;
        private LinkedList<CollageProject> _undoStack = new();
        private const int MaxUndoSteps = 20;

        private AppWindow _appWindow;
        private string _settingsPath;
        private string? _lastSavedPath;

        private double _defaultCanvasWidth = 1920;
        private double _defaultCanvasHeight = 1080;
        private bool _isUpdatingUI = false;

        public CollageEditorWindow()
        {
            this.InitializeComponent();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = _appWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(50, 255, 255, 255);
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "ModernImageViewer");
            Directory.CreateDirectory(folder);
            _settingsPath = Path.Combine(folder, "collage_window_settings.json");

            RestoreWindowState();

            _project = new CollageProject
            {
                CanvasWidth = _defaultCanvasWidth,
                CanvasHeight = _defaultCanvasHeight
            };

            CollageCanvasControl.Project = _project;

            CollageCanvasControl.UserInteractionCompleted += (s, previousState) =>
            {
                _undoStack.AddFirst(previousState);
                if (_undoStack.Count > MaxUndoSteps) _undoStack.RemoveLast();
            };
            CollageCanvasControl.SelectionChanged += CollageCanvasControl_SelectionChanged;

            this.Closed += (s, e) => SaveWindowState();
            RootGrid.Loaded += Window_Loaded;

            UpdateProjectUI();
        }

        public string FormatValue(double value) => value.ToString("0");

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CollageCanvasControl.Invalidate();
        }

        // --- NEW: Add External Image Handler ---
        public void AddExternalImage(string imagePath)
        {
            if (_project == null || string.IsNullOrEmpty(imagePath)) return;

            _undoStack.AddFirst(_project.DeepClone());
            if (_undoStack.Count > MaxUndoSteps) _undoStack.RemoveLast();

            var newCell = new CollageElement
            {
                ImagePath = imagePath,
                Layout = new Rect(100, 100, 420, 320)
            };

            _project.Elements.Add(newCell);
            CollageCanvasControl.Invalidate();
        }

        private void LayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string layoutName && _project != null)
            {
                _undoStack.AddFirst(_project.DeepClone());
                if (_undoStack.Count > MaxUndoSteps) _undoStack.RemoveLast();

                _project.ApplyLayout(layoutName, _project.CanvasWidth, _project.CanvasHeight);
                CollageCanvasControl.Invalidate();
            }
        }

        private void RestoreWindowState()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(_settingsPath));
                    if (state != null && state.Width > 100)
                    {
                        _appWindow.MoveAndResize(new RectInt32(state.X, state.Y, state.Width, state.Height));
                    }
                    if (state != null)
                    {
                        if (state.DefaultCanvasWidth >= 100) _defaultCanvasWidth = state.DefaultCanvasWidth;
                        if (state.DefaultCanvasHeight >= 100) _defaultCanvasHeight = state.DefaultCanvasHeight;
                    }
                }
            }
            catch { }
        }

        private void SaveWindowState()
        {
            try
            {
                var state = new WindowState
                {
                    X = _appWindow.Position.X,
                    Y = _appWindow.Position.Y,
                    Width = _appWindow.Size.Width,
                    Height = _appWindow.Size.Height
                };
                if (_project != null)
                {
                    state.DefaultCanvasWidth = _project.CanvasWidth;
                    state.DefaultCanvasHeight = _project.CanvasHeight;
                }
                else
                {
                    state.DefaultCanvasWidth = _defaultCanvasWidth;
                    state.DefaultCanvasHeight = _defaultCanvasHeight;
                }
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private class WindowState
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public double DefaultCanvasWidth { get; set; } = 1920;
            public double DefaultCanvasHeight { get; set; } = 1080;
        }

        private void HoverZone_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SidebarView != null) SidebarView.IsPaneOpen = true;
        }

        private void Sidebar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SidebarView != null && SidebarPane != null)
            {
                var pos = e.GetCurrentPoint(SidebarPane).Position;
                if (pos.X < 0 || pos.X > SidebarPane.ActualWidth || pos.Y < 0 || pos.Y > SidebarPane.ActualHeight)
                    SidebarView.IsPaneOpen = false;
            }
        }

        private async void CanvasSize_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;

            var panel = new StackPanel { Spacing = 16, Width = 460 };

            bool isUpdatingSizes = false;
            double lockedAspect = _project.CanvasWidth / Math.Max(1.0, _project.CanvasHeight);

            var widthBox = new TextBox
            {
                Text = _project.CanvasWidth.ToString("0"),
                MinWidth = 90,
                Width = 110,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };

            var heightBox = new TextBox
            {
                Text = _project.CanvasHeight.ToString("0"),
                MinWidth = 90,
                Width = 110,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };

            var previewHeader = new TextBlock { Text = "Preview", FontWeight = FontWeights.SemiBold };
            panel.Children.Add(previewHeader);

            var previewFrame = new Border
            {
                Width = 220,
                Height = 150,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 25, 25)),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(10)
            };

            var previewShape = new Border
            {
                Background = new SolidColorBrush(HexToColor(_project.BackgroundColor)),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Black),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            previewFrame.Child = previewShape;
            panel.Children.Add(previewFrame);

            var sizeText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 4),
                FontSize = 13
            };
            panel.Children.Add(sizeText);

            var presetsHeader = new TextBlock { Text = "Quick Presets", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) };
            panel.Children.Add(presetsHeader);

            var presetsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var quickPresets = new (string name, string detail, int w, int h)[]
            {
                ("Square", "1:1  •  1080×1080", 1080, 1080),
                ("Instagram Post", "4:5  •  1080×1350", 1080, 1350),
                ("Instagram Story", "9:16  •  1080×1920", 1080, 1920),
                ("YouTube HD", "16:9  •  1920×1080", 1920, 1080),
                ("2K / QHD", "16:9  •  2560×1440", 2560, 1440),
                ("4K", "16:9  •  3840×2160", 3840, 2160),
                ("Ultrawide", "21:9  •  3440×1440", 3440, 1440),
                ("Portrait", "3:4  •  1080×1440", 1080, 1440),
                ("A4 Print", "A4  •  2480×3508", 2480, 3508)
            };

            int col = 0, row = 0;
            presetsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            foreach (var p in quickPresets)
            {
                var btnContent = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = p.name, FontWeight = FontWeights.Medium, FontSize = 12 },
                        new TextBlock { Text = p.detail, FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) }
                    }
                };

                var btn = new Button
                {
                    Content = btnContent,
                    Tag = (p.w, p.h),
                    MinWidth = 118,
                    Padding = new Thickness(6, 4, 6, 4)
                };

                btn.Click += (s, a) =>
                {
                    if (btn.Tag is ValueTuple<int, int> sz)
                    {
                        isUpdatingSizes = true;
                        widthBox.Text = sz.Item1.ToString();
                        heightBox.Text = sz.Item2.ToString();
                        isUpdatingSizes = false;
                        lockedAspect = sz.Item1 / (double)sz.Item2;
                        UpdatePreview();
                    }
                };

                Grid.SetColumn(btn, col);
                Grid.SetRow(btn, row);
                presetsGrid.Children.Add(btn);

                col++;
                if (col >= 3)
                {
                    col = 0;
                    row++;
                    presetsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
            }
            panel.Children.Add(presetsGrid);

            var fitBtn = new Button
            {
                Content = "📐  Fit to Images + Margin",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 6)
            };
            fitBtn.Click += (s, a) =>
            {
                if (_project.Elements.Count == 0) return;
                var bounds = _project.GetContentBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) return;

                double pad = _project.DefaultCellSpacing * 2.5;
                int newW = (int)Math.Ceiling(bounds.Width + pad * 2);
                int newH = (int)Math.Ceiling(bounds.Height + pad * 2);

                isUpdatingSizes = true;
                widthBox.Text = newW.ToString();
                heightBox.Text = newH.ToString();
                isUpdatingSizes = false;
                lockedAspect = newW / (double)newH;
                UpdatePreview();
            };
            panel.Children.Add(fitBtn);

            var customRow = new Grid
            {
                ColumnSpacing = 8,
                Margin = new Thickness(0, 6, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var customLabel = new TextBlock
            {
                Text = "Custom Dimensions",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var widthLabel = new TextBlock { Text = "W:", VerticalAlignment = VerticalAlignment.Center };
            var heightLabel = new TextBlock { Text = "H:", VerticalAlignment = VerticalAlignment.Center };

            widthBox.VerticalAlignment = VerticalAlignment.Center;
            heightBox.VerticalAlignment = VerticalAlignment.Center;

            var lockAspectToggle = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = new FontIcon { Glyph = "\uE167", FontSize = 14 },
                IsChecked = false,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 4, 6, 4)
            };
            ToolTipService.SetToolTip(lockAspectToggle, "Lock Aspect Ratio");

            customRow.Children.Add(customLabel);
            Grid.SetColumn(customLabel, 0);

            customRow.Children.Add(widthLabel);
            Grid.SetColumn(widthLabel, 1);

            customRow.Children.Add(widthBox);
            Grid.SetColumn(widthBox, 2);

            customRow.Children.Add(lockAspectToggle);
            Grid.SetColumn(lockAspectToggle, 3);

            customRow.Children.Add(heightLabel);
            Grid.SetColumn(heightLabel, 4);

            customRow.Children.Add(heightBox);
            Grid.SetColumn(heightBox, 5);

            panel.Children.Add(customRow);

            void UpdatePreview()
            {
                if (double.TryParse(widthBox.Text, out double w) &&
                    double.TryParse(heightBox.Text, out double h) && w > 0 && h > 0)
                {
                    double aspect = w / h;

                    double maxW = 190;
                    double maxH = 115;
                    double pW, pH;

                    if (aspect >= 1.0)
                    {
                        pW = maxW;
                        pH = maxW / aspect;
                        if (pH > maxH) { pH = maxH; pW = pH * aspect; }
                    }
                    else
                    {
                        pH = maxH;
                        pW = pH * aspect;
                        if (pW > maxW) { pW = maxW; pH = pW / aspect; }
                    }

                    previewShape.Width = Math.Max(20, pW);
                    previewShape.Height = Math.Max(20, pH);
                    sizeText.Text = $"{w:0} × {h:0} px   •   {aspect:0.00} : 1";
                }
            }

            widthBox.TextChanged += (s, a) =>
            {
                if (isUpdatingSizes) return;
                if (lockAspectToggle.IsChecked == true && double.TryParse(widthBox.Text, out double w) && w > 0)
                {
                    isUpdatingSizes = true;
                    heightBox.Text = Math.Round(w / lockedAspect).ToString("0");
                    isUpdatingSizes = false;
                }
                UpdatePreview();
            };

            heightBox.TextChanged += (s, a) =>
            {
                if (isUpdatingSizes) return;
                if (lockAspectToggle.IsChecked == true && double.TryParse(heightBox.Text, out double h) && h > 0)
                {
                    isUpdatingSizes = true;
                    widthBox.Text = Math.Round(h * lockedAspect).ToString("0");
                    isUpdatingSizes = false;
                }
                UpdatePreview();
            };

            lockAspectToggle.Checked += (s, a) =>
            {
                if (double.TryParse(widthBox.Text, out double w) &&
                    double.TryParse(heightBox.Text, out double h) && h > 0)
                {
                    lockedAspect = w / h;
                }
            };

            UpdatePreview();

            var dialog = new ContentDialog
            {
                Title = "Canvas Size",
                Content = panel,
                PrimaryButtonText = "Apply Changes",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                double newW = _project.CanvasWidth;
                double newH = _project.CanvasHeight;

                if (double.TryParse(widthBox.Text, out double parsedW) && parsedW >= 100) newW = parsedW;
                if (double.TryParse(heightBox.Text, out double parsedH) && parsedH >= 100) newH = parsedH;

                _project.CanvasWidth = newW;
                _project.CanvasHeight = newH;
                CollageCanvasControl.Invalidate();
            }

            if (SidebarView != null)
                SidebarView.IsPaneOpen = false;

            CollageCanvasControl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }

        private async void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = _project.Name };
            picker.FileTypeChoices.Add("Collage Project", new List<string> { ".collage" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            try
            {
                await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(_project, new JsonSerializerOptions { WriteIndented = true }));
                _lastSavedPath = file.Path;
                UpdateProjectUI();
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Save Failed", ex.Message);
            }
        }

        private async void LoadProject_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".collage");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                string json = await FileIO.ReadTextAsync(file);
                var loaded = JsonSerializer.Deserialize<CollageProject>(json);
                if (loaded == null) return;

                CollageCanvasControl.Project = null;
                foreach (var element in loaded.Elements)
                {
                    if (string.IsNullOrEmpty(element.ImagePath)) continue;
                    try
                    {
                        var entry = await ViewerEngine.DecodeFastPreviewAsync(element.ImagePath);
                        if (entry?.Bitmap != null)
                        {
                            App.GlobalImageCache[element.ImagePath] = entry;
                        }
                    }
                    catch { }
                }
                _project = loaded;
                CollageCanvasControl.Project = _project;
                _lastSavedPath = file.Path;
                UpdateProjectUI();
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Load Failed", ex.Message);
            }
        }

        private Windows.UI.Color HexToColor(string hex)
        {
            try
            {
                var cleanHex = hex.Replace("#", "");
                if (cleanHex.Length == 6) cleanHex = "FF" + cleanHex;
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(cleanHex.Substring(0, 2), 16),
                    Convert.ToByte(cleanHex.Substring(2, 2), 16),
                    Convert.ToByte(cleanHex.Substring(4, 2), 16),
                    Convert.ToByte(cleanHex.Substring(6, 2), 16));
            }
            catch { return Microsoft.UI.Colors.White; }
        }

        private async void SaveCollageImage_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = _project.Name
            };
            picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG Image", new List<string> { ".jpg", ".jpeg" });

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            string ext = Path.GetExtension(file.Path).ToLowerInvariant();
            bool isJpeg = ext == ".jpg" || ext == ".jpeg";

            int jpegQuality = 92;
            if (isJpeg)
            {
                jpegQuality = await ShowJpegQualityDialogAsync();
                if (jpegQuality < 0) return;
            }

            try
            {
                using var renderTarget = new CanvasRenderTarget(
                    CanvasDevice.GetSharedDevice(),
                    (float)_project.CanvasWidth,
                    (float)_project.CanvasHeight,
                    96);

                var elementBitmaps = new Dictionary<CollageElement, CanvasBitmap>();
                var bitmapsToDispose = new List<CanvasBitmap>();

                foreach (var element in _project.Elements)
                {
                    if (string.IsNullOrEmpty(element.ImagePath)) continue;

                    if (element.HighResBitmap != null)
                    {
                        elementBitmaps[element] = element.HighResBitmap;
                    }
                    else
                    {
                        try
                        {
                            var imgFile = await StorageFile.GetFileFromPathAsync(element.ImagePath);
                            using var imgStream = await imgFile.OpenReadAsync();
                            var decoder = await BitmapDecoder.CreateAsync(imgStream);
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                            var bmp = CanvasBitmap.CreateFromSoftwareBitmap(renderTarget.Device, softwareBitmap);
                            elementBitmaps[element] = bmp;
                            bitmapsToDispose.Add(bmp);
                        }
                        catch { }
                    }
                }

                using (var ds = renderTarget.CreateDrawingSession())
                {
                    ds.Clear(HexToColor(_project.BackgroundColor));

                    foreach (var element in _project.Elements)
                    {
                        elementBitmaps.TryGetValue(element, out var bitmapToUse);
                        
                        ImageCacheEntry? entry = null;
                        if (!string.IsNullOrEmpty(element.ImagePath))
                        {
                            App.GlobalImageCache.TryGetValue(element.ImagePath, out entry);
                        }

                        var clipGeom = element.GetOrUpdateClipGeometry(renderTarget);
                        CollageCellRenderer.DrawCell(ds, element, entry, bitmapToUse, 2.2f, clipGeom);
                    }
                }

                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                stream.Size = 0; // Explicitly truncate file to prevent corruption on overwrite

                if (!isJpeg)
                {
                    await renderTarget.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                }
                else
                {
                    await renderTarget.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, jpegQuality / 100f);
                }

                foreach (var bmp in bitmapsToDispose)
                {
                    bmp.Dispose();
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Export Failed", ex.Message);
            }
        }

        private async Task<int> ShowJpegQualityDialogAsync()
        {
            var panel = new StackPanel { Spacing = 14, Width = 340 };

            var header = new TextBlock
            {
                Text = "JPEG Export Quality",
                FontWeight = FontWeights.SemiBold,
                FontSize = 16
            };

            var slider = new Slider
            {
                Minimum = 50,
                Maximum = 100,
                Value = 92,
                StepFrequency = 1,
                Width = 280,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var valueText = new TextBlock
            {
                Text = "92%",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            slider.ValueChanged += (s, args) =>
            {
                valueText.Text = $"{slider.Value:0}%";
            };

            var note = new TextBlock
            {
                Text = "Higher = better quality, larger file size",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            panel.Children.Add(header);
            panel.Children.Add(slider);
            panel.Children.Add(valueText);
            panel.Children.Add(note);

            var dialog = new ContentDialog
            {
                Title = "Export as JPEG",
                Content = panel,
                PrimaryButtonText = "Export",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? (int)slider.Value : -1;
        }

        private async Task ShowErrorDialog(string title, string message)
        {
            await new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
        }

        private void ResetWindow_Click(object sender, RoutedEventArgs e)
        {
            _appWindow?.MoveAndResize(new RectInt32(100, 100, 1920, 1080));
            CollageCanvasControl?.ResetViewTo100Percent();
        }

        private void GapSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_project == null) return;

            _project.DefaultCellSpacing = e.NewValue;

            if (_project.Elements.Count > 0)
            {
                _project.ApplyLayout("Freeform", _project.CanvasWidth, _project.CanvasHeight);
                CollageCanvasControl.Invalidate();
            }
        }

        private void AppearanceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (CollageCanvasControl == null || CollageCanvasControl.Project == null) return;
            if (CollageCanvasControl.SelectedElements.Count == 0) return;

            bool isBorder = ReferenceEquals(sender, BorderSlider);
            bool isRadius = ReferenceEquals(sender, RadiusSlider);
            bool isShadow = ReferenceEquals(sender, ShadowSlider);

            foreach (var el in CollageCanvasControl.SelectedElements)
            {
                if (el.IsLocked || el.IsContentLocked) continue;

                if (isBorder) el.BorderThickness = (float)e.NewValue;
                if (isRadius) el.CornerRadius = (float)e.NewValue;
                if (isShadow) el.ShadowSize = (float)e.NewValue;
            }

            CollageCanvasControl.Invalidate();
        }

        private void CollageCanvasControl_SelectionChanged(object? sender, EventArgs e)
        {
            _isUpdatingUI = true;

            var first = System.Linq.Enumerable.FirstOrDefault(CollageCanvasControl.SelectedElements);

            if (first != null)
            {
                BorderSlider.Value = first.BorderThickness;
                RadiusSlider.Value = first.CornerRadius;
                ShadowSlider.Value = first.ShadowSize;
            }
            else
            {
                BorderSlider.Value = 0;
                RadiusSlider.Value = 0;
                ShadowSlider.Value = 0;
            }

            _isUpdatingUI = false;
        }

        private void UndoLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.First is { } firstNode)
            {
                _project = firstNode.Value;
                _undoStack.RemoveFirst();
                CollageCanvasControl.Project = _project;
                CollageCanvasControl.Invalidate();
            }
        }

        private void UpdateProjectUI()
        {
            this.Title = string.IsNullOrWhiteSpace(_project.Name) ? "Collage Editor" : $"Collage Editor - {_project.Name}";
        }

        private void GridToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (CollageCanvasControl != null && sender is ToggleSwitch toggle)
            {
                CollageCanvasControl.ShowGrid = toggle.IsOn;
                CollageCanvasControl.Invalidate();
            }
        }

        private void SnappingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (CollageCanvasControl != null && sender is ToggleSwitch toggle)
            {
                CollageCanvasControl.IsSnappingEnabled = toggle.IsOn;
            }
        }
    }
}
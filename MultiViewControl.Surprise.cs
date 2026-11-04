using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// Surprise me: arranges the session's pictures with LayoutComposer. The result is an ordinary layout, so it can be
    /// edited, saved and undone like any other. The pictures it draws from stay until the session is replaced or reset.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private const int FactReaders = 4;
        private const int MostWaitForPicturesMs = 1500;
        // Slider steps for a design's odds: each end is this many times as likely as the middle.
        private const double DesignOddsBase = 5;

        private readonly ConcurrentDictionary<string, PhotoFacts> _photoFacts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _featuredPictures = new(StringComparer.OrdinalIgnoreCase);
        // Only set while the session has more images than a click may use.
        private PictureDeck? _surpriseDeck;
        private bool _surpriseRun;
        private string? _surpriseRecipe;
        private bool _surpriseBusy;
        private bool _surpriseSettling;
        private SurpriseOptions? _surpriseOptions;
        private DispatcherTimer? _autoPlayTimer;
        private int _autoPlaySeconds;
        private Border? _surpriseCard;
        private Border? _autoPlayShield;
        private bool _autoPlayShieldPressed;
        private PreparedSurprise? _preparedSurprise;
        private System.Threading.CancellationTokenSource? _prepareCts;
        private Task? _prepareTask;
        private Action? _updateStorySummary;
        private Action? _refreshStoryboardCards;
        private Action? _syncStorySceneSelection;
        private Action<int>? _onStoryboardPlaybackIndexChanged;

        private sealed record PreparedSurprise(
            Composition Composition,
            PictureDeck? Deck,
            Dictionary<string, BitmapSource> DecodedBitmaps,
            List<string> SessionPictures,
            HashSet<string> FeaturedPictures,
            string OptionsJson,
            double Width,
            double Height);

        private static string SurpriseOptionsPath => System.IO.Path.Combine(AppPaths.LocalDirectory, "surprise-options.json");

        private SurpriseOptions SurpriseChoices
        {
            get
            {
                if (_surpriseOptions != null) return _surpriseOptions;
                string? json = null;
                try
                {
                    if (System.IO.File.Exists(SurpriseOptionsPath)) json = System.IO.File.ReadAllText(SurpriseOptionsPath);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // The defaults will do.
                }
                return _surpriseOptions = SurpriseOptions.FromJson(json);
            }
        }

        private void ChangeSurpriseChoices(Action<SurpriseOptions> change)
        {
            change(SurpriseChoices);
            CancelPreparedSurprise();
            try
            {
                System.IO.Directory.CreateDirectory(AppPaths.LocalDirectory);
                AtomicFile.WriteAllText(SurpriseOptionsPath, SurpriseChoices.ToJson());
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // It still applies until the app closes.
            }
            if (_autoPlaySeconds > 0) ApplyPlayEdge(CurrentPlayPercent());
        }

        private void AttachSurpriseStops()
        {
            // Closing the card leaves auto-play running. Only a click on the picture, Esc, Space or the bar's button pauses it.
            RootGrid.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (_surpriseCard != null && !IsInSurpriseCard(e.OriginalSource)) RemoveSurpriseCard();
                if (_trayCard != null
                    && !IsInside(e.OriginalSource, _trayCard)
                    && !IsInside(e.OriginalSource, UnifiedHud)
                    && !IsInside(e.OriginalSource, DrawerControl)
                    && !IsInside(e.OriginalSource, StoryDeckButton))
                    RemoveTrayCard();
            }), true);
            RootGrid.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                // A key the window's pass already acted on is done. Acting again on the same Esc closed the
                // drawer that leaving Camera Director had just reopened.
                if (e.Handled) return;
                // Here as well as with the other keys, because the window leaves Esc alone while a text box has focus.
                if ((_surpriseCard != null || _trayCard != null) && e.Key == Windows.System.VirtualKey.Escape)
                {
                    RemoveSurpriseCard();
                    RemoveTrayCard();
                    e.Handled = true;
                }
                else if (_isEditingCamera)
                {
                    if (e.Key is Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Enter)
                    {
                        ExitCameraDirector(save: e.Key == Windows.System.VirtualKey.Enter);
                        e.Handled = true;
                    }
                    else if (e.Key == Windows.System.VirtualKey.Tab)
                    {
                        ToggleCameraKeyframe();
                        e.Handled = true;
                    }
                    else if (e.Key == Windows.System.VirtualKey.Space)
                    {
                        TriggerCameraPreview();
                        e.Handled = true;
                    }
                    else if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.PageUp)
                    {
                        NavigateCameraScene(-1);
                        e.Handled = true;
                    }
                    else if (e.Key is Windows.System.VirtualKey.Right or Windows.System.VirtualKey.PageDown)
                    {
                        NavigateCameraScene(1);
                        e.Handled = true;
                    }
                }
                else if (_selectedSlot >= 0 && e.Key == Windows.System.VirtualKey.Escape)
                {
                    DeselectCell();
                    e.Handled = true;
                }
            }), true);
            RootGrid.SizeChanged += (_, _) =>
            {
                PlaceSurpriseCard();
                UpdateTrayCardDimensions();
            };
        }

        private void UpdateTrayCardDimensions()
        {
            if (_trayCard == null) return;
            _trayCard.Margin = new Thickness(0);
            _trayCard.Height = _trayDrawerHeight;
            _trayCard.Width = double.NaN;
            UpdatePlaybarReaction(_trayDrawerHeight);
        }

        private void UpdatePlaybarReaction(double drawerHeight)
        {
            if (_trayCard == null)
            {
                UnifiedHud.Margin = new Thickness(0, 0, 0, 28);
                UnifiedHud.Opacity = 1.0;
                UnifiedHud.IsHitTestVisible = true;
                return;
            }

            const double singleRowThreshold = 220;
            if (drawerHeight <= singleRowThreshold)
            {
                // Docked 1-row mode: playbar sits exactly 16px above the drawer's splitter
                UnifiedHud.Margin = new Thickness(0, 0, 0, drawerHeight + 16);
                UnifiedHud.Opacity = 1.0;
                UnifiedHud.IsHitTestVisible = true;
            }
            else
            {
                // Multi-row slide sorter: fade out playbar smoothly so the user has full management space
                double fade = Math.Clamp(1.0 - (drawerHeight - singleRowThreshold) / 25.0, 0.0, 1.0);
                UnifiedHud.Opacity = fade;
                UnifiedHud.IsHitTestVisible = fade > 0.05;
                UnifiedHud.Margin = new Thickness(0, 0, 0, drawerHeight + 16);
            }
        }

        private const double SurpriseCardWidth = 530;
        private const double SurpriseCardGap = 8;
        private bool _showingSurpriseAdvanced;
        private string _surpriseSectionOpen = "Images & faces";
        private TextBlock? _cardSongText;
        private Button? _cardPreviousSong;
        private Button? _cardNextSong;

        // Without focus, so Space still pauses a show after this has been pressed.
        private void FullScreenButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.ToggleFullScreen();

        /// <summary>The window says, because F11 changes it too. The same symbols and words as Cinematic's.</summary>
        public void ShowFullScreen(bool on)
        {
            FullScreenIcon.Glyph = on ? "\uE73F" : "\uE740";
            string name = on ? "Leave full screen (F11)" : "Full screen (F11)";
            ToolTipService.SetToolTip(FullScreenButton, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FullScreenButton, name);
        }

        private void SurpriseCardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_surpriseCard != null) RemoveSurpriseCard();
            else ShowSurpriseCard();
        }

        /// <summary>
        /// Everything about Surprise me, opened from its control on the bar and placed just above it, as the grid layout's card is.
        /// What is used most is at the top. The ranges, thresholds and odds are folded into sections, one open at a time.
        /// </summary>
        private void ShowSurpriseCard()
        {
            var options = SurpriseChoices;
            var body = new StackPanel { Spacing = 10 };

            FontIcon Icon(string glyph, double size = 14) => new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = glyph, FontSize = size };

            Style? SafeStyle(string key)
            {
                if (Resources.TryGetValue(key, out var s) && s is Style st) return st;
                if (Application.Current?.Resources.TryGetValue(key, out var asObj) == true && asObj is Style ast) return ast;
                return null;
            }

            var winBlue = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
            var winBlueDeep = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));

            void Row(StackPanel column, string label, string tip, FrameworkElement control)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.None };
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
                control.HorizontalAlignment = HorizontalAlignment.Right;
                control.VerticalAlignment = VerticalAlignment.Center;
                control.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetColumn(control, 1);
                row.Children.Add(text);
                row.Children.Add(control);
                column.Children.Add(row);
            }

            Slider Slide(StackPanel column, string label, string tip, double min, double max, double step, double value, Func<double, string> format, Action<SurpriseOptions, double> chosen)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.None };

                var rightGrid = new Grid { Width = 230, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

                var readout = new TextBlock
                {
                    Text = format(value),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextAlignment = TextAlignment.Right,
                    FontSize = 12,
                    Opacity = 0.82,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };
                var slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    StepFrequency = step,
                    SmallChange = step,
                    Value = value,
                    Width = 160,
                    Foreground = winBlue,
                    VerticalAlignment = VerticalAlignment.Center
                };
                slider.Resources["SliderThumbBackground"] = winBlue;
                slider.Resources["SliderThumbBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x70, 0xD0, 0xFF));
                slider.Resources["SliderThumbBackgroundPressed"] = winBlue;
                slider.Resources["SliderTrackValueFill"] = winBlue;
                slider.Resources["SliderTrackValueFillPointerOver"] = winBlue;
                slider.Resources["SliderTrackValueFillPressed"] = winBlue;
                slider.ValueChanged += (_, e) =>
                {
                    ChangeSurpriseChoices(o => chosen(o, e.NewValue));
                    readout.Text = format(e.NewValue);
                };
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, label);
                Grid.SetColumn(slider, 0);
                Grid.SetColumn(readout, 1);
                rightGrid.Children.Add(slider);
                rightGrid.Children.Add(readout);

                Grid.SetColumn(text, 0);
                Grid.SetColumn(rightGrid, 1);
                row.Children.Add(text);
                row.Children.Add(rightGrid);
                column.Children.Add(row);
                return slider;
            }

            ToggleSwitch Toggle(StackPanel column, string label, string tip, bool value, Action<SurpriseOptions, bool> chosen, string? subtitle = null)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

                FrameworkElement leftElement;
                if (!string.IsNullOrEmpty(subtitle))
                {
                    var labelStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    var text = new TextBlock { Text = label, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.None };
                    var sub = new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.6, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), TextWrapping = TextWrapping.Wrap };
                    labelStack.Children.Add(text);
                    labelStack.Children.Add(sub);
                    leftElement = labelStack;
                }
                else
                {
                    leftElement = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.None };
                }

                var toggle = new ToggleSwitch
                {
                    IsOn = value,
                    OnContent = string.Empty,
                    OffContent = string.Empty,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                toggle.Toggled += (_, _) => ChangeSurpriseChoices(o => chosen(o, toggle.IsOn));
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
                Grid.SetColumn(leftElement, 0);
                Grid.SetColumn(toggle, 1);
                row.Children.Add(leftElement);
                row.Children.Add(toggle);
                column.Children.Add(row);
                return toggle;
            }

            Button WordButton(string text, string name, string tip, RoutedEventHandler pressed)
            {
                var button = new Button
                {
                    Content = text,
                    Padding = new Thickness(10, 5, 10, 5),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    CornerRadius = new CornerRadius(4),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x2E, 0x2E)),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                ToolTipService.SetToolTip(button, tip);
                button.Click += pressed;
                return button;
            }

            Button IconButton(string glyph, string name, string tip, RoutedEventHandler pressed)
            {
                var button = new Button
                {
                    Content = Icon(glyph, 12),
                    Padding = new Thickness(8, 5, 8, 5),
                    MinWidth = 32,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                    CornerRadius = new CornerRadius(4),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x2E, 0x2E)),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                ToolTipService.SetToolTip(button, tip);
                button.Click += pressed;
                return button;
            }

            // Image count dual SpinNumberBox range control (Min to Max)
            Slider? fewestEach = null;
            Slider? mostEach = null;
            Action? updateImagesSummary = null;
            bool syncingImageRange = false;

            var fewestBox = new SpinNumberBox
            {
                Minimum = 1,
                Maximum = MosaicLayout.MaxShown,
                Value = options.FewestPictures,
                Width = 84,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(fewestBox, "Minimum images per scene (1 to 16)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fewestBox, "Minimum images per scene");

            var toLabel = new TextBlock
            {
                Text = "to",
                FontSize = 13,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };

            var mostBox = new SpinNumberBox
            {
                Minimum = 1,
                Maximum = MosaicLayout.MaxShown,
                Value = options.MostPictures,
                Width = 84,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(mostBox, "Maximum images per scene (1 to 16)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mostBox, "Maximum images per scene");

            var imageControlsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            imageControlsPanel.Children.Add(fewestBox);
            imageControlsPanel.Children.Add(toLabel);
            imageControlsPanel.Children.Add(mostBox);

            void SetImageRange(int f, int m)
            {
                f = Math.Clamp(f, 1, MosaicLayout.MaxShown);
                m = Math.Clamp(m, f, MosaicLayout.MaxShown);
                if (syncingImageRange) return;
                syncingImageRange = true;
                try
                {
                    if (fewestBox.Value != f) fewestBox.Value = f;
                    if (mostBox.Value != m) mostBox.Value = m;
                    ChangeSurpriseChoices(o => { o.FewestPictures = f; o.MostPictures = m; });
                    if (fewestEach != null && Math.Abs(fewestEach.Value - f) > 0.01) fewestEach.Value = f;
                    if (mostEach != null && Math.Abs(mostEach.Value - m) > 0.01) mostEach.Value = m;
                    updateImagesSummary?.Invoke();
                }
                finally
                {
                    syncingImageRange = false;
                }
            }

            fewestBox.ValueChanged += (_, newVal) =>
            {
                if (syncingImageRange) return;
                int f = Math.Clamp(newVal, 1, MosaicLayout.MaxShown);
                int m = Math.Max(f, mostBox.Value);
                SetImageRange(f, m);
            };

            mostBox.ValueChanged += (_, newVal) =>
            {
                if (syncingImageRange) return;
                int m = Math.Clamp(newVal, 1, MosaicLayout.MaxShown);
                int f = Math.Min(m, fewestBox.Value);
                SetImageRange(f, m);
            };

            var imagesRow = new Grid { ColumnSpacing = 12, MinHeight = 36, Margin = new Thickness(0, 2, 0, 2) };
            imagesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            imagesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            var imagesLabel = new TextBlock
            {
                Text = "Images per scene:",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            };
            Grid.SetColumn(imagesLabel, 0);
            Grid.SetColumn(imageControlsPanel, 1);

            imagesRow.Children.Add(imagesLabel);
            imagesRow.Children.Add(imageControlsPanel);

            void SpeedChanged()
            {
                if (_autoPlaySeconds > 0 && _autoPlayTimer != null)
                {
                    _autoPlaySeconds = SurpriseChoices.PlaySeconds;
                    _layoutSeconds = SurpriseChoices.NextPlaySeconds(Random.Shared);
                    _autoPlayTimer.Interval = TimeSpan.FromSeconds(_layoutSeconds);
                }
                ShowAutoPlayState();
            }

            if (!_showingSurpriseAdvanced)
            {
                var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = "Auto-Mix Settings", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var gearBtn = new Button
                {
                    Content = Icon("\uE713", 13),
                    Padding = new Thickness(6, 4, 6, 4),
                    CornerRadius = new CornerRadius(6),
                    Style = SafeStyle("QuietButtonStyle"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(gearBtn, "Advanced auto-mix settings");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gearBtn, "Advanced auto-mix settings");
                gearBtn.Click += (_, _) => { _showingSurpriseAdvanced = true; ShowSurpriseCard(); };
                Grid.SetColumn(gearBtn, 1);
                header.Children.Add(title);
                header.Children.Add(gearBtn);
                body.Children.Add(header);

                bool showAvailable = _activeShow != null && _activeShow.Scenes.Count > 0;
                bool showOn = PlayUsesStoryboard();
                var playModeRow = new Grid { ColumnSpacing = 12, MinHeight = 36 };
                playModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                playModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
                var playModeLabel = new TextBlock
                {
                    Text = "Play",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };
                var playModeChips = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var showChip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Content = "Storyboard",
                    IsEnabled = showAvailable,
                    Padding = new Thickness(12, 5, 12, 5),
                    FontSize = 13,
                    CornerRadius = new CornerRadius(8),
                    Opacity = showAvailable ? 1 : 0.45
                };
                var shuffleChip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Content = "Auto-Mix",
                    Padding = new Thickness(12, 5, 12, 5),
                    FontSize = 13,
                    CornerRadius = new CornerRadius(8)
                };
                void PaintPlayChip(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton chip, bool on)
                {
                    chip.IsChecked = on;
                    if (on)
                    {
                        chip.Background = winBlueDeep;
                        chip.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                        chip.BorderBrush = winBlue;
                    }
                    else
                    {
                        chip.ClearValue(Control.BackgroundProperty);
                        chip.ClearValue(Control.ForegroundProperty);
                        chip.ClearValue(Control.BorderBrushProperty);
                    }
                }
                PaintPlayChip(showChip, showOn);
                PaintPlayChip(shuffleChip, !showOn);
                ToolTipService.SetToolTip(showChip, showAvailable
                    ? "Play walks the storyboard. Arrows move between scenes. The playbar shows the same choice."
                    : "Make a show first.");
                ToolTipService.SetToolTip(shuffleChip, "Play makes a new layout each time, from the image pool. The playbar shows the same choice.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(showChip, "Play the storyboard");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shuffleChip, "Play Auto-Mix");
                showChip.Click += (_, _) =>
                {
                    if (!showAvailable)
                    {
                        PaintPlayChip(showChip, false);
                        PaintPlayChip(shuffleChip, true);
                        return;
                    }
                    _shuffleChosen = false;
                    if (_autoPlaySeconds > 0) _isStoryboardPlayback = true;
                    PaintPlayChip(showChip, true);
                    PaintPlayChip(shuffleChip, false);
                    ShowAutoPlayState();
                };
                shuffleChip.Click += (_, _) =>
                {
                    _shuffleChosen = true;
                    _isStoryboardPlayback = false;
                    PaintPlayChip(showChip, false);
                    PaintPlayChip(shuffleChip, true);
                    ShowAutoPlayState();
                };
                playModeChips.Children.Add(showChip);
                playModeChips.Children.Add(shuffleChip);
                Grid.SetColumn(playModeChips, 1);
                playModeRow.Children.Add(playModeLabel);
                playModeRow.Children.Add(playModeChips);
                ToolTipService.SetToolTip(playModeRow, "Storyboard plays the scenes in order. Auto-Mix generates dynamic layouts each time. The choice stays until you change it, and takes effect on the next picture.");
                body.Children.Add(playModeRow);

                body.Children.Add(imagesRow);

                int initialFrom = options.AutoPlayRandom ? options.RandomFewestSeconds : options.PlaySeconds;
                int initialTo = options.AutoPlayRandom ? options.RandomMostSeconds : options.PlaySeconds;
                initialFrom = Math.Clamp(initialFrom, 3, 30);
                initialTo = Math.Clamp(Math.Max(initialFrom, initialTo), 3, 30);

                var durationRow = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                durationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                durationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

                var durationLabel = new TextBlock { Text = "Duration", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };

                var durationRightGrid = new Grid { Width = 230, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                durationRightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                durationRightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

                string FormatDuration(int low, int high) => low == high ? $"{low:0}s" : $"{low:0}s - {high:0}s";

                var durationReadout = new TextBlock
                {
                    Text = FormatDuration(initialFrom, initialTo),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextAlignment = TextAlignment.Right,
                    FontSize = 12,
                    Opacity = 0.82,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };

                var durationSlider = new RangeSlider
                {
                    Minimum = 3,
                    Maximum = 30,
                    StepFrequency = 1,
                    RangeStart = initialFrom,
                    RangeEnd = initialTo,
                    Width = 160,
                    VerticalAlignment = VerticalAlignment.Center
                };

                durationSlider.RangeChanged += (_, range) =>
                {
                    int low = (int)Math.Round(range.Start);
                    int high = (int)Math.Round(range.End);
                    durationReadout.Text = FormatDuration(low, high);
                    bool isRange = low < high;
                    ChangeSurpriseChoices(o =>
                    {
                        o.AutoPlayRandom = isRange;
                        o.AutoPlaySeconds = low;
                        o.RandomFewestSeconds = low;
                        o.RandomMostSeconds = high;
                    });
                    SpeedChanged();
                };

                ToolTipService.SetToolTip(durationRow, "Slide duration per layout before advancing. Drag thumbs apart for a random range, together for a fixed duration. Double-click to set a single fixed duration.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(durationSlider, "Duration");

                Grid.SetColumn(durationSlider, 0);
                Grid.SetColumn(durationReadout, 1);
                durationRightGrid.Children.Add(durationSlider);
                durationRightGrid.Children.Add(durationReadout);

                Grid.SetColumn(durationLabel, 0);
                Grid.SetColumn(durationRightGrid, 1);
                durationRow.Children.Add(durationLabel);
                durationRow.Children.Add(durationRightGrid);

                body.Children.Add(durationRow);

                Toggle(body, "Smart pace", "When duration has a range, holds dense multi-image layouts toward the longer end and solo/pair layouts toward the shorter end.", options.SmartPace, (o, on) => o.SmartPace = on, subtitle: "Adjusts duration based on layout density");

                Slide(body, "Motion", "How far the camera moves over each layout while it plays, from still to strong, with floating cells following it.", 0, 100, 5, options.MotionAmount * 100,
                    v => v < 1 ? "Off" : $"{v:0}%",
                    (o, v) => o.MotionAmount = v / 100);

                body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Opacity = 0.15, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0, 6, 0, 4) });
                var soundtrackHeader = new TextBlock
                {
                    Text = "Soundtrack",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Margin = new Thickness(0, 2, 0, 4)
                };
                body.Children.Add(soundtrackHeader);
                body.Children.Add(MusicRow(Icon, IconButton));

                body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Opacity = 0.15, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0, 4, 0, 2) });
                var openAdvBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 7, 10, 7),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                var advStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                advStack.Children.Add(Icon("\uE713", 13));
                advStack.Children.Add(new TextBlock { Text = "Advanced auto-mix settings\u2026", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                openAdvBtn.Content = advStack;
                ToolTipService.SetToolTip(openAdvBtn, "Recipe odds, face intelligence, camera transitions and look");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(openAdvBtn, "Advanced auto-mix settings");
                openAdvBtn.Click += (_, _) => { _showingSurpriseAdvanced = true; ShowSurpriseCard(); };
                body.Children.Add(openAdvBtn);
            }
            else
            {
                var advHeader = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 2) };
                advHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                advHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var backBtn = new Button
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(6),
                    Style = SafeStyle("QuietButtonStyle"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var backStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                backStack.Children.Add(Icon("\uE72B", 12));
                backStack.Children.Add(new TextBlock { Text = "Back", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                backBtn.Content = backStack;
                ToolTipService.SetToolTip(backBtn, "Back to fundamental options");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(backBtn, "Back to fundamental options");
                backBtn.Click += (_, _) => { _showingSurpriseAdvanced = false; ShowSurpriseCard(); };
                var advTitle = new TextBlock { Text = "Advanced Settings", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(advTitle, 1);
                advHeader.Children.Add(backBtn);
                advHeader.Children.Add(advTitle);
                body.Children.Add(advHeader);

                // 4 Consolidated Expanders with Live Header Summaries
                var sections = new List<Expander>();
            (StackPanel Content, TextBlock Summary) Section(string title, string initialSummary)
            {
                var inner = new StackPanel { Spacing = 4 };
                var headerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, ColumnSpacing = 8 };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var titleBlock = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                var summaryBlock = new TextBlock { Text = initialSummary, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.62, Margin = new Thickness(0, 0, 6, 0) };
                Grid.SetColumn(summaryBlock, 1);
                headerGrid.Children.Add(titleBlock);
                headerGrid.Children.Add(summaryBlock);

                var section = new Expander
                {
                    Header = headerGrid,
                    Content = inner,
                    IsExpanded = title == _surpriseSectionOpen,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(section, title);
                section.Expanding += (_, _) =>
                {
                    _surpriseSectionOpen = title;
                    foreach (var other in sections)
                        if (!ReferenceEquals(other, section)) other.IsExpanded = false;
                };
                sections.Add(section);
                body.Children.Add(section);
                return (inner, summaryBlock);
            }

            // 1. Images & faces
            string ImagesSummaryText()
            {
                int f = SurpriseChoices.FewestPictures, m = SurpriseChoices.MostPictures;
                string rangeText = f == m ? $"{f}" : $"{f}\u2013{m}";
                string faceText = SurpriseChoices.MostZoom <= 1.01 || SurpriseChoices.ZoomChance <= 0.001
                    ? "Faces Off"
                    : $"Faces {SurpriseChoices.MostZoom:0.#}\u00D7";
                return $"{rangeText} \u00B7 {faceText}";
            }
            var (imagesSection, imagesSummary) = Section("Images & faces", ImagesSummaryText());
            updateImagesSummary = () => imagesSummary.Text = ImagesSummaryText();

            fewestEach = Slide(imagesSection, "Fewest images per scene", "The minimum images per scene. When more images are available, each scene selects within this range.",
                SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.FewestPictures,
                v => $"{v:0}",
                (o, v) => { if (!syncingImageRange) SetImageRange((int)v, (int)mostBox.Value); });
            mostEach = Slide(imagesSection, "Most images per scene", "The maximum images per scene. Additional images remain in the Image Pool and cycle across scenes.",
                SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.MostPictures,
                v => $"{v:0}",
                (o, v) => { if (!syncingImageRange) SetImageRange((int)fewestBox.Value, (int)v); });
            fewestEach.ValueChanged += (_, e) => { if (!syncingImageRange && mostEach.Value < e.NewValue) mostEach.Value = e.NewValue; };
            mostEach.ValueChanged += (_, e) => { if (!syncingImageRange && fewestEach.Value > e.NewValue) fewestEach.Value = e.NewValue; };

            Slide(imagesSection, "Same folder and date", "The odds that a click takes images that are neighbours when sorted by folder, then file date.",
                0, 100, 5, options.TogetherChance * 100,
                v => $"{v:0}%",
                (o, v) => o.TogetherChance = v / 100);
            Toggle(imagesSection, "Skip screens and repeats", "Leave out what looks like a screen capture or a graphic, and all but one of near-identical shots.",
                options.SkipScreens, (o, on) => o.SkipScreens = on);
            Slide(imagesSection, "Zoom on faces", "1x is off. Brings forward people who are small in the image.",
                1, SurpriseOptions.MostOfAnyZoom, 0.5, options.MostZoom,
                v => v <= 1.01 ? "Off" : $"{v:0.0}\u00D7",
                (o, v) => { o.MostZoom = v; updateImagesSummary(); });
            Slide(imagesSection, "Face zoom frequency", "The odds that an image with faces is zoomed.",
                0, 100, 5, options.ZoomChance * 100,
                v => v < 1 ? "Off" : $"{v:0}%",
                (o, v) => { o.ZoomChance = v / 100; updateImagesSummary(); });
            Slide(imagesSection, "Uncut face shown", "How much of a face must show for it to count as uncut. Lower allows bolder layouts.",
                50, 100, 5, options.FaceShown * 100,
                v => $"{v:0}%",
                (o, v) => o.FaceShown = v / 100);

            // 2. Designs (with style preset chips + Off stop)
            string DesignsSummaryText()
            {
                int active = LayoutComposer.Recipes.Count(r => !SurpriseChoices.IsDesignOff(r));
                return active == LayoutComposer.Recipes.Length ? $"All {active}" : $"{active} active";
            }
            var (designsSection, designsSummary) = Section("Designs", DesignsSummaryText());
            var designSliders = new Dictionary<string, Slider>(StringComparer.OrdinalIgnoreCase);
            var stylePresetChips = new List<(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton Chip, HashSet<string> ActiveSet)>();
            var stylePresetPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 16) };
            bool syncingDesignPresets = false;

            void SyncDesignPresetChips()
            {
                var currentActive = LayoutComposer.Recipes.Where(r => !SurpriseChoices.IsDesignOff(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var p in stylePresetChips)
                    p.Chip.IsChecked = p.ActiveSet.SetEquals(currentActive);
                designsSummary.Text = DesignsSummaryText();
            }

            void AddDesignPreset(string label, string tip, params string[] enabledRecipes)
            {
                var set = enabledRecipes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentActive = LayoutComposer.Recipes.Where(r => !options.IsDesignOff(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Content = label,
                    IsChecked = set.SetEquals(currentActive),
                    Padding = new Thickness(9, 4, 9, 4),
                    MinWidth = 0,
                    FontSize = 12
                };
                ToolTipService.SetToolTip(chip, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, label);
                chip.Click += (_, _) =>
                {
                    syncingDesignPresets = true;
                    try
                    {
                        ChangeSurpriseChoices(o =>
                        {
                            o.DisabledDesigns.Clear();
                            foreach (string r in LayoutComposer.Recipes)
                            {
                                if (set.Contains(r)) o.DesignOdds[r] = 1.0;
                                else o.DisabledDesigns.Add(r);
                            }
                        });
                        foreach (var kv in designSliders)
                            kv.Value.Value = set.Contains(kv.Key) ? 0.0 : -1.2;
                    }
                    finally
                    {
                        syncingDesignPresets = false;
                    }
                    SyncDesignPresetChips();
                };
                stylePresetChips.Add((chip, set));
                stylePresetPanel.Children.Add(chip);
            }
            AddDesignPreset("All", "All 6 layout styles enabled", LayoutComposer.Recipes.ToArray());
            AddDesignPreset("Clean grids", "Editorial grids and filmstrips without tilted prints or circle cutouts", "Hero and companions", "Balanced mosaic", "Filmstrip");
            AddDesignPreset("Framed", "Matted gallery walls and hero layouts", "Gallery wall", "Hero and companions");
            AddDesignPreset("Scrapbook", "Playful tilted prints, circle cutouts, and gallery mats", "Scattered prints", "Circles", "Gallery wall");
            designsSection.Children.Add(stylePresetPanel);

            string FormatDesignStep(double v) =>
                v <= -1.1 ? "Off" :
                v < -0.6 ? "Rare" :
                v < -0.15 ? "Less" :
                v <= 0.15 ? "Normal" :
                v <= 0.6 ? "More" : "Often";

            foreach (string design in LayoutComposer.Recipes)
            {
                double initialVal = options.IsDesignOff(design) ? -1.2 : Math.Log(Math.Max(SurpriseOptions.RarestDesign, options.DesignOdds.GetValueOrDefault(design, 1.0)), DesignOddsBase);
                var ds = Slide(designsSection, design, "Far left turns this design Off; right makes it appear more often.",
                    -1.2, 1.0, 0.1, initialVal, FormatDesignStep,
                    (o, v) =>
                    {
                        if (v <= -1.1) o.DisabledDesigns.Add(design);
                        else
                        {
                            o.DisabledDesigns.Remove(design);
                            o.DesignOdds[design] = Math.Pow(DesignOddsBase, v);
                        }
                        if (!syncingDesignPresets) SyncDesignPresetChips();
                    });
                designSliders[design] = ds;
            }
            Slide(designsSection, "Version variety", "How many of the highest-scored candidate layouts it picks from. 1 always picks the single best layout.",
                1, 12, 1, options.Variety,
                v => (int)v == 1 ? "Best" : $"Top {(int)v}",
                (o, v) => o.Variety = (int)v);

            // 3. Camera & transitions
            string CameraSummaryText() => $"{SurpriseChoices.CameraMode} \u00B7 {SurpriseChoices.TransitionVariety * 100:0}%";
            var (cameraSection, cameraSummary) = Section("Camera & transitions", CameraSummaryText());
            Slider? zoomVsDriftSlider = null;
            var camChips = new List<(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton Chip, string Mode)>();
            var camModePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 16) };
            foreach (string mode in SurpriseOptions.CameraModes)
            {
                var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Content = mode,
                    IsChecked = string.Equals(options.CameraMode, mode, StringComparison.OrdinalIgnoreCase),
                    Padding = new Thickness(9, 4, 9, 4),
                    MinWidth = 0,
                    FontSize = 12
                };
                ToolTipService.SetToolTip(chip, $"Camera motion style: {mode}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, mode);
                chip.Click += (_, _) =>
                {
                    ChangeSurpriseChoices(o => o.CameraMode = mode);
                    foreach (var c in camChips) c.Chip.IsChecked = ReferenceEquals(c.Chip, chip);
                    cameraSummary.Text = CameraSummaryText();
                };
                camChips.Add((chip, mode));
                camModePanel.Children.Add(chip);
            }
            cameraSection.Children.Add(camModePanel);
            zoomVsDriftSlider = Slide(cameraSection, "Zooms rather than drifts", "In Auto mode, the odds that the camera zooms in or out rather than panning across the layout.",
                0, 100, 5, options.MotionZoomChance * 100,
                v => $"{v:0}%",
                (o, v) => o.MotionZoomChance = v / 100);

            Slide(cameraSection, "Aims at faces", "The odds that the camera heads toward a large image with faces in it.",
                0, 100, 5, options.MotionFaceChance * 100,
                v => $"{v:0}%",
                (o, v) => o.MotionFaceChance = v / 100);
            Slide(cameraSection, "Transition variety", "How often a change of layout zooms through, fades through dark or slides, rather than a crossfade.",
                0, 100, 5, options.TransitionVariety * 100,
                v => v < 1 ? "Crossfade" : $"{v:0}%",
                (o, v) => { o.TransitionVariety = v / 100; cameraSummary.Text = CameraSummaryText(); });

            // 4. Soundtrack & audio
            string MusicSummaryText()
            {
                var m = MainWindow.Instance?.MyMusic;
                return m?.HasFolder == true ? m.Summary : "No folder";
            }
            var (musicSection, musicSummary) = Section("Soundtrack & audio", MusicSummaryText());

            string folderPath = MainWindow.Instance?.MyMusic?.Folder ?? "No music folder configured";
            var folderRow = new Grid { ColumnSpacing = 12, MinHeight = 48, Margin = new Thickness(0, 2, 0, 4) };
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            var folderStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            folderStack.Children.Add(new TextBlock { Text = "Music library", FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) });
            var folderSub = new TextBlock
            {
                Text = folderPath,
                FontSize = 11,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 250,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            };
            ToolTipService.SetToolTip(folderSub, folderPath);
            ToolTipService.SetToolTip(folderStack, folderPath);
            folderStack.Children.Add(folderSub);

            var openMusicBtn = WordButton("Configure\u2026", "Open music library settings", "Change music folder, rating filters and playback behavior", (_, _) => MainWindow.Instance?.OpenMusicSettings());
            openMusicBtn.HorizontalAlignment = HorizontalAlignment.Right;
            openMusicBtn.VerticalAlignment = VerticalAlignment.Center;

            Grid.SetColumn(folderStack, 0);
            Grid.SetColumn(openMusicBtn, 1);
            folderRow.Children.Add(folderStack);
            folderRow.Children.Add(openMusicBtn);
            musicSection.Children.Add(folderRow);

            var mus = MainWindow.Instance?.MyMusic;
            var lowestRatingText = mus?.LowestStars > 0 ? $"{mus.LowestStars:0}\u2605 and up" : "All ratings";
            var ratingRow = new Grid { ColumnSpacing = 12, MinHeight = 34 };
            ratingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ratingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            var ratingText = new TextBlock { Text = "Rating filter", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
            var ratingValue = new TextBlock { Text = lowestRatingText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, Opacity = 0.82, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
            Grid.SetColumn(ratingText, 0);
            Grid.SetColumn(ratingValue, 1);
            ratingRow.Children.Add(ratingText);
            ratingRow.Children.Add(ratingValue);
            musicSection.Children.Add(ratingRow);

            Toggle(musicSection, "Auto-play soundtrack", "Automatically play music while auto-play is running.",
                mus?.InSurprise != false,
                (_, on) => { MainWindow.Instance?.SetMusicInSurprise(on); musicSummary.Text = MusicSummaryText(); });

            // 5. Look
            string LookSummaryText()
            {
                string progress = SurpriseChoices.ProgressOn ? SurpriseChoices.ProgressEdge : "Progress off";
                return $"{SurpriseChoices.Card} \u00B7 {SurpriseChoices.MostTilt:0.#}\u00B0 \u00B7 {progress}";
            }
            var (lookSection, lookSummary) = Section("Look", LookSummaryText());
            var card = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Right, ItemsSource = SurpriseOptions.Cards.ToList(), SelectedItem = options.Card };
            card.SelectionChanged += (_, _) =>
            {
                if (card.SelectedItem is string name)
                {
                    ChangeSurpriseChoices(o => o.Card = name);
                    lookSummary.Text = LookSummaryText();
                }
            };
            Row(lookSection, "Gallery card", "The color of the card the images are set into.", card);
            Slide(lookSection, "Gallery gaps", "1x is the usual gap between images on a Gallery wall.",
                0.25, 3, 0.25, options.GapScale,
                v => $"{v:0.##}\u00D7",
                (o, v) => o.GapScale = v);
            Slide(lookSection, "Hero size", "The hero image's height against the canvas.",
                40, 90, 1, options.HeroSize * 100,
                v => $"{v:0}%",
                (o, v) => o.HeroSize = v / 100);
            Slide(lookSection, "Most tilt", "How far Scattered prints can turn.",
                0, 20, 0.5, options.MostTilt,
                v => v < 0.1 ? "0\u00B0" : $"{v:0.0}\u00B0",
                (o, v) => { o.MostTilt = v; lookSummary.Text = LookSummaryText(); });
            Toggle(lookSection, "Keep my canvas", "Keep the canvas color, surface and frame as they are now, instead of the design's.",
                options.KeepCanvas, (o, on) => o.KeepCanvas = on);

            Toggle(lookSection, "Play progress", "A line along an edge of the screen fills while each layout holds. It stays when the bar hides.",
                options.ProgressOn, (o, on) => { o.ProgressOn = on; lookSummary.Text = LookSummaryText(); });

            void AddLookChoice(StackPanel column, string label, string tip, FrameworkElement control)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
                };
                control.HorizontalAlignment = HorizontalAlignment.Right;
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                row.Children.Add(text);
                row.Children.Add(control);
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
                column.Children.Add(row);
            }

            var edgePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var edgeChips = new List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>();
            foreach (string edgeName in SurpriseOptions.ProgressEdges)
            {
                string edge = edgeName;
                var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Content = edge,
                    IsChecked = string.Equals(options.ProgressEdge, edge, StringComparison.OrdinalIgnoreCase),
                    Padding = new Thickness(9, 4, 9, 4),
                    MinWidth = 0,
                    FontSize = 12
                };
                ToolTipService.SetToolTip(chip, $"Draw the progress line along the {edge.ToLowerInvariant()} edge.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Progress edge {edge}");
                chip.Click += (_, _) =>
                {
                    ChangeSurpriseChoices(o => o.ProgressEdge = edge);
                    foreach (var other in edgeChips) other.IsChecked = ReferenceEquals(other, chip);
                    lookSummary.Text = LookSummaryText();
                };
                edgeChips.Add(chip);
                edgePanel.Children.Add(chip);
            }
            AddLookChoice(lookSection, "Edge", "Which side of the screen the progress line sits on. It fills from the left, or from the top on a side edge.", edgePanel);

            var colorPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            var colorSwatches = new List<(Border Shell, string Name)>();
            void PaintSwatch(Border shell, bool on)
            {
                shell.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(on
                    ? Microsoft.UI.Colors.White
                    : Windows.UI.Color.FromArgb(70, 255, 255, 255));
                shell.BorderThickness = new Thickness(on ? 2 : 1);
            }
            foreach (string colorName in SurpriseOptions.ProgressColors)
            {
                string name = colorName;
                var shell = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    Child = new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 16,
                        Height = 16,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(PlayEdgeColor(name))
                    }
                };
                PaintSwatch(shell, string.Equals(options.ProgressColor, name, StringComparison.OrdinalIgnoreCase));
                var swatch = new Button
                {
                    Width = 28,
                    Height = 28,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Content = shell,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(14),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                };
                ToolTipService.SetToolTip(swatch, name);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Progress color {name}");
                swatch.Click += (_, _) =>
                {
                    ChangeSurpriseChoices(o => o.ProgressColor = name);
                    foreach (var other in colorSwatches) PaintSwatch(other.Shell, other.Name == name);
                    lookSummary.Text = LookSummaryText();
                };
                colorSwatches.Add((shell, name));
                colorPanel.Children.Add(swatch);
            }
            AddLookChoice(lookSection, "Effect color", "The color of the progress line and its glow. Accent follows the Windows accent color.", colorPanel);

            // Reset to defaults
            var resetRow = new Grid { ColumnSpacing = 6, MinHeight = 32 };
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var askReset = new HyperlinkButton { Content = "Reset to defaults...", FontSize = 12, Padding = new Thickness(0, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(askReset, "Every setting on this card. Your music settings stay as they are.");
            var question = new TextBlock { Text = "Reset every setting?", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var keep = new Button { Content = "Keep", FontSize = 12, Visibility = Visibility.Collapsed };
            var reset = new Button { Content = "Reset", FontSize = 12, Visibility = Visibility.Collapsed };
            Grid.SetColumn(keep, 1);
            Grid.SetColumn(reset, 2);
            void AskingReset(bool asking)
            {
                askReset.Visibility = asking ? Visibility.Collapsed : Visibility.Visible;
                question.Visibility = keep.Visibility = reset.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
            }
            askReset.Click += (_, _) =>
            {
                AskingReset(true);
                keep.Focus(FocusState.Programmatic);
            };
            keep.Click += (_, _) => AskingReset(false);
            reset.Click += (_, _) =>
            {
                _surpriseOptions = new SurpriseOptions();
                ChangeSurpriseChoices(_ => { });
                if (_autoPlaySeconds > 0 && _autoPlayTimer != null)
                {
                    _autoPlaySeconds = SurpriseChoices.PlaySeconds;
                    _layoutSeconds = SurpriseChoices.NextPlaySeconds(Random.Shared);
                    _autoPlayTimer.Interval = TimeSpan.FromSeconds(_layoutSeconds);
                }
                ShowSurpriseCard();
            };
            var doneBtn = new Button
            {
                Content = "Done",
                FontSize = 12,
                Padding = new Thickness(16, 5, 16, 5),
                CornerRadius = new CornerRadius(6),
                Background = winBlueDeep,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = winBlue,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            doneBtn.Click += (_, _) => { _showingSurpriseAdvanced = false; ShowSurpriseCard(); };
            Grid.SetColumn(doneBtn, 3);
            resetRow.Children.Add(askReset);
            resetRow.Children.Add(question);
            resetRow.Children.Add(keep);
            resetRow.Children.Add(reset);
            resetRow.Children.Add(doneBtn);
            body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Opacity = 0.2, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0, 4, 0, 2) });
            body.Children.Add(resetRow);
            }

            RemoveSurpriseCard();
            CloseCountsCard();
            _surpriseCard = NewCard(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) });
            _surpriseCard.Width = SurpriseCardWidth;
            _surpriseCard.Padding = new Thickness(16, 14, 16, 14);
            _surpriseCard.HorizontalAlignment = HorizontalAlignment.Left;
            _surpriseCard.VerticalAlignment = VerticalAlignment.Bottom;
            AddOverCanvas(_surpriseCard, 6500);
            PlaceSurpriseCard();
            SurpriseCardButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            SurpriseCardArrow.Glyph = "\uE972";
            ShowAutoPlayState();
            HudMenu_Opened(this, EventArgs.Empty);
        }

        /// <summary>The open auto-play card has its own Storyboard / Surprise me choice. Rebuild it so it matches the playbar.</summary>
        private void RefreshPlayModeCard()
        {
            if (_surpriseCard != null && !_showingSurpriseAdvanced)
                ShowSurpriseCard();
        }

        /// <summary>The dark panel the Surprise me card and the image tray share.</summary>
        private static Border NewCard(UIElement child) => new()
        {
            Child = child,
            CornerRadius = new CornerRadius(10),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            RequestedTheme = ElementTheme.Dark
        };

        private void AddOverCanvas(FrameworkElement element, int layer)
        {
            Canvas.SetZIndex(element, layer);
            Grid.SetRowSpan(element, Math.Max(1, RootGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(element, Math.Max(1, RootGrid.ColumnDefinitions.Count));
            RootGrid.Children.Add(element);
        }

        /// <summary>Centred over the control and just above the bar, inside the window, as the grid layout's card is.</summary>
        private void PlaceSurpriseCard()
        {
            if (_surpriseCard == null) return;
            double fromBottom = UnifiedHud.ActualHeight + UnifiedHud.Margin.Bottom + SurpriseCardGap;
            _surpriseCard.MaxHeight = Math.Max(160, RootGrid.ActualHeight - fromBottom - SurpriseCardGap);
            var at = SurpriseControl.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
            double left = at.X + (SurpriseControl.ActualWidth - SurpriseCardWidth) / 2;
            left = Math.Max(SurpriseCardGap, Math.Min(left, RootGrid.ActualWidth - SurpriseCardWidth - SurpriseCardGap));
            _surpriseCard.Margin = new Thickness(left, 0, 0, fromBottom);
        }

        /// <summary>The song and its steps, on or off, and the way to the one set of music settings, which the main window's gear also opens.</summary>
        private FrameworkElement MusicRow(Func<string, double, FontIcon> icon, Func<string, string, string, RoutedEventHandler, Button> iconButton)
        {
            var music = MainWindow.Instance?.MyMusic;
            WatchMusicSettings();
            var panel = new StackPanel { Spacing = 4 };
            bool hasFolder = music?.HasFolder == true;
            bool isMusicActive = hasFolder && (music?.InSurprise != false);
            var winBlue = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));

            // 1. Background Music (Toggle)
            var bgRow = new Grid { ColumnSpacing = 12, MinHeight = 44, Margin = new Thickness(0, 2, 0, 2) };
            bgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            var bgLabelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var bgTitle = new TextBlock { Text = "Background music", FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
            string bgSubText = music?.Summary ?? MusicChoices.NoFolder;
            var bgSubtitle = new TextBlock
            {
                Text = bgSubText,
                FontSize = 11,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 250,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            };
            ToolTipService.SetToolTip(bgSubtitle, music?.Folder ?? bgSubText);
            ToolTipService.SetToolTip(bgLabelStack, music?.Folder ?? bgSubText);
            bgLabelStack.Children.Add(bgTitle);
            bgLabelStack.Children.Add(bgSubtitle);

            var onToggle = new ToggleSwitch
            {
                IsOn = music?.InSurprise != false,
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 0,
                IsEnabled = hasFolder,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(onToggle, "Background music");
            ToolTipService.SetToolTip(bgRow, "Play background soundtrack during auto-play");

            Grid.SetColumn(bgLabelStack, 0);
            Grid.SetColumn(onToggle, 1);
            bgRow.Children.Add(bgLabelStack);
            bgRow.Children.Add(onToggle);
            panel.Children.Add(bgRow);

            // 2. Volume (Full-Width Slider matching Motion and Duration)
            var volRow = new Grid { ColumnSpacing = 12, MinHeight = 34 };
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            var volLabel = new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };

            var volRightGrid = new Grid { Width = 230, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            volRightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            volRightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            double initialVol = music?.Volume ?? 60;
            var volReadout = new TextBlock
            {
                Text = $"{initialVol:0}%",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                FontSize = 12,
                Opacity = isMusicActive ? 0.82 : 0.4,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            };
            var volSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 1,
                SmallChange = 5,
                Value = initialVol,
                Width = 160,
                Foreground = winBlue,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = isMusicActive
            };
            volSlider.Resources["SliderThumbBackground"] = winBlue;
            volSlider.Resources["SliderThumbBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x70, 0xD0, 0xFF));
            volSlider.Resources["SliderThumbBackgroundPressed"] = winBlue;
            volSlider.Resources["SliderTrackValueFill"] = winBlue;
            volSlider.Resources["SliderTrackValueFillPointerOver"] = winBlue;
            volSlider.Resources["SliderTrackValueFillPressed"] = winBlue;
            volSlider.ValueChanged += (_, e) =>
            {
                MainWindow.Instance?.SetMusicVolume(e.NewValue);
                volReadout.Text = $"{e.NewValue:0}%";
            };
            ToolTipService.SetToolTip(volRow, "Soundtrack playback volume");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(volSlider, "Soundtrack volume");

            Grid.SetColumn(volSlider, 0);
            Grid.SetColumn(volReadout, 1);
            volRightGrid.Children.Add(volSlider);
            volRightGrid.Children.Add(volReadout);

            Grid.SetColumn(volLabel, 0);
            Grid.SetColumn(volRightGrid, 1);
            volRow.Children.Add(volLabel);
            volRow.Children.Add(volRightGrid);
            panel.Children.Add(volRow);

            onToggle.Toggled += (_, _) =>
            {
                MainWindow.Instance?.SetMusicInSurprise(onToggle.IsOn);
                volSlider.IsEnabled = hasFolder && onToggle.IsOn;
                volReadout.Opacity = onToggle.IsOn ? 0.82 : 0.4;
                if (!onToggle.IsOn) StopAutoPlayMusic();
                else if (_autoPlaySeconds > 0) StartAutoPlayMusic();
                ShowAutoPlayState();
                ShowCardSong();
            };

            // 3. Track Selection & Controls
            var trackRow = new Grid { ColumnSpacing = 12, MinHeight = 36 };
            trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            var trackLeft = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
            trackLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            trackLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var noteIcon = icon("\uE8D6", 13);
            noteIcon.VerticalAlignment = VerticalAlignment.Center;
            noteIcon.Foreground = winBlue;

            _cardSongText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                Opacity = 0.9
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_cardSongText, "SurpriseSongText");

            Grid.SetColumn(noteIcon, 0);
            Grid.SetColumn(_cardSongText, 1);
            trackLeft.Children.Add(noteIcon);
            trackLeft.Children.Add(_cardSongText);

            var trackButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            _cardPreviousSong = iconButton("\uE892", "Previous track", "Previous track", (_, _) => _surpriseMusic?.PreviousSong());
            _cardNextSong = iconButton("\uE893", "Next track", "Next track", (_, _) => _surpriseMusic?.NextSong());

            trackButtons.Children.Add(_cardPreviousSong);
            trackButtons.Children.Add(_cardNextSong);

            Grid.SetColumn(trackLeft, 0);
            Grid.SetColumn(trackButtons, 1);
            trackRow.Children.Add(trackLeft);
            trackRow.Children.Add(trackButtons);
            panel.Children.Add(trackRow);

            ShowCardSong();
            return panel;
        }

        private bool _watchingMusicSettings;

        private void WatchMusicSettings()
        {
            if (_watchingMusicSettings || MainWindow.Instance == null) return;
            _watchingMusicSettings = true;
            MainWindow.Instance.MusicSettingsChanged += ApplyMusicSettings;
        }

        private void ApplyMusicSettings()
        {
            var music = MainWindow.Instance?.MyMusic;
            _musicProblem = null;
            if (music != null) _surpriseMusic?.SetVolume(music.Volume);
            if (_autoPlaySeconds > 0) StartAutoPlayMusic();
            // While paused nothing starts, but a song that is no longer what the settings ask for should not be waiting to resume.
            else if (music == null || !music.InSurprise || !music.HasFolder || _surpriseMusic?.IsPlayingFolder(music.Folder!, music.LowestStars, music.IncludesUnrated) != true)
                StopAutoPlayMusic();
            if (_surpriseCard != null) ShowSurpriseCard();
            else ShowCardSong();
        }

        /// <summary>Steps through what Undo holds, pausing first, so going back does not race the next layout.</summary>
        private void StepLayout(bool back)
        {
            if (PlayUsesStoryboard())
            {
                _ = StepStoryboardScene(back);
                return;
            }

            StopAutoPlay();
            if (back)
            {
                if (CanUndo)
                {
                    Undo();
                    ShowAutoPlayToast("Previous layout");
                }
                else
                {
                    ShowAutoPlayToast("At earliest layout");
                }
            }
            else
            {
                if (CanRedo)
                {
                    Redo();
                    ShowAutoPlayToast("Next layout");
                }
                else
                {
                    _ = SurpriseAsync(fade: true);
                }
            }
        }

        private void RemoveSurpriseCard()
        {
            if (_surpriseCard == null) return;
            RootGrid.Children.Remove(_surpriseCard);
            _surpriseCard = null;
            _cardSongText = null;
            _cardPreviousSong = null;
            _cardNextSong = null;
            _showingSurpriseAdvanced = false;
            SurpriseCardButton.ClearValue(Control.BackgroundProperty);
            SurpriseCardArrow.Glyph = "\uE971";
            HudMenu_Closed(this, EventArgs.Empty);
        }

        private sealed record TrayImage(string Path, int Slot);

        private Border? _trayCard;
        private int _trayTab = 0; // 0 = Pool, 1 = Storyboard
        private double _trayDrawerHeight = 200;
        private const double SingleRowDrawerHeight = 200;
        private string? _activePoolPath;
        private ShowProject? _activeShow;
        private int _showPlaybackIndex = -1;
        private bool _isStoryboardPlayback;
        private ShowScene? _selectedShowScene;
        /// <summary>The scene whose layout is on the canvas. Null when the canvas is a mix or was never loaded from the selected scene.</summary>
        private string? _canvasSceneId;
        /// <summary>When set, Play keeps using Surprise me even if a storyboard exists. Otherwise a storyboard is what Play walks.</summary>
        private bool _shuffleChosen;

        private bool PlayUsesStoryboard()
        {
            bool hasScenes = _activeShow != null && _activeShow.Scenes.Count > 0;
            if (!hasScenes) return false;

            if (_isStoryboardPlayback) return true;
            if (_trayCard != null && _trayTab == 1) return true;

            return !_shuffleChosen;
        }

        /// <summary>The drafted title is "Gallery wall (1)". The card shows the name. The pictures are the count.</summary>
        private static string SceneCardTitle(ShowScene scene)
        {
            string title = scene.Title ?? scene.Recipe ?? "Scene";
            int open = title.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && title.EndsWith(')')
                && int.TryParse(title.AsSpan(open + 2, title.Length - open - 3), out _))
                return title[..open];
            return title;
        }

        /// <summary>A show length. Under a minute stays in seconds. Past that, minutes and the leftover seconds.</summary>
        private static string ShowLength(double seconds)
        {
            int whole = Math.Max(0, (int)Math.Round(seconds));
            if (whole < 60) return $"{whole}s";
            int mins = whole / 60;
            int secs = whole % 60;
            if (secs == 0) return mins == 1 ? "1 min" : $"{mins} min";
            return $"{mins} min {secs}s";
        }
        private DispatcherTimer? _playbackProgressTimer;
        private long _sceneStartTimestamp;
        private double _sceneDwellSeconds;
        private string? _playEdgeStyle;

        private double CurrentPlayPercent()
        {
            if (_sceneDwellSeconds <= 0) return 0;
            double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _sceneStartTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            return Math.Clamp(elapsed / _sceneDwellSeconds * 100.0, 0, 100);
        }

        private void StartPlaybackProgress(double dwellSeconds)
        {
            _sceneDwellSeconds = Math.Max(0.5, dwellSeconds);
            _sceneStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyPlayEdge(0);
            if (_playbackProgressTimer == null)
            {
                _playbackProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _playbackProgressTimer.Tick += (_, _) =>
                {
                    if (_autoPlaySeconds == 0)
                    {
                        StopPlaybackProgress();
                        return;
                    }
                    ApplyPlayEdge(CurrentPlayPercent());
                };
            }
            _playbackProgressTimer.Start();
        }

        private void StopPlaybackProgress()
        {
            _playbackProgressTimer?.Stop();
            _playEdgeStyle = null;
            if (PlayEdgeHost != null) PlayEdgeHost.Visibility = Visibility.Collapsed;
        }

        private const double PlayEdgeDepth = 18;
        private const double PlayEdgeCore = 3;

        /// <summary>The hold line. It is pinned to a screen edge, so hiding the bar does not hide it.</summary>
        private void ApplyPlayEdge(double pct)
        {
            if (PlayEdgeHost == null) return;
            var options = SurpriseChoices;
            if (_autoPlaySeconds == 0 || !options.ProgressOn)
            {
                _playEdgeStyle = null;
                PlayEdgeHost.Visibility = Visibility.Collapsed;
                return;
            }

            string edge = options.ProgressEdge;
            bool horizontal = edge is SurpriseOptions.ProgressBottom or SurpriseOptions.ProgressTop;
            bool atEnd = edge is SurpriseOptions.ProgressBottom or SurpriseOptions.ProgressRight;
            string style = edge + "|" + options.ProgressColor;
            if (style != _playEdgeStyle)
            {
                _playEdgeStyle = style;
                var color = PlayEdgeColor(options.ProgressColor);
                PlayEdgeFill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
                PlayEdgeGlow.Background = PlayEdgeGlowBrush(color, horizontal, atEnd);
                if (horizontal)
                {
                    PlayEdgeHost.ClearValue(FrameworkElement.WidthProperty);
                    PlayEdgeHost.Height = PlayEdgeDepth;
                    PlayEdgeHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                    PlayEdgeHost.VerticalAlignment = atEnd ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    PlayEdgeFill.Height = PlayEdgeCore;
                    PlayEdgeFill.VerticalAlignment = atEnd ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    PlayEdgeFill.HorizontalAlignment = HorizontalAlignment.Left;
                    PlayEdgeGlow.Height = PlayEdgeDepth;
                    PlayEdgeGlow.VerticalAlignment = VerticalAlignment.Stretch;
                    PlayEdgeGlow.HorizontalAlignment = HorizontalAlignment.Left;
                }
                else
                {
                    PlayEdgeHost.ClearValue(FrameworkElement.HeightProperty);
                    PlayEdgeHost.Width = PlayEdgeDepth;
                    PlayEdgeHost.VerticalAlignment = VerticalAlignment.Stretch;
                    PlayEdgeHost.HorizontalAlignment = atEnd ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    PlayEdgeFill.Width = PlayEdgeCore;
                    PlayEdgeFill.HorizontalAlignment = atEnd ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    PlayEdgeFill.VerticalAlignment = VerticalAlignment.Top;
                    PlayEdgeGlow.Width = PlayEdgeDepth;
                    PlayEdgeGlow.HorizontalAlignment = HorizontalAlignment.Stretch;
                    PlayEdgeGlow.VerticalAlignment = VerticalAlignment.Top;
                }
            }

            double span = Math.Max(0, horizontal ? RootGrid.ActualWidth : RootGrid.ActualHeight);
            double filled = span * Math.Clamp(pct, 0, 100) / 100.0;
            if (horizontal)
            {
                PlayEdgeFill.Width = filled;
                PlayEdgeGlow.Width = filled;
            }
            else
            {
                PlayEdgeFill.Height = filled;
                PlayEdgeGlow.Height = filled;
            }
            PlayEdgeHost.Visibility = Visibility.Visible;
        }

        private static Windows.UI.Color PlayEdgeColor(string name)
        {
            return name switch
            {
                SurpriseOptions.ProgressAmber => Windows.UI.Color.FromArgb(255, 255, 176, 46),
                SurpriseOptions.ProgressAqua => Windows.UI.Color.FromArgb(255, 64, 214, 220),
                SurpriseOptions.ProgressRose => Windows.UI.Color.FromArgb(255, 255, 95, 140),
                SurpriseOptions.ProgressWhite => Microsoft.UI.Colors.White,
                _ => SystemAccentColor()
            };
        }

        private static Windows.UI.Color SystemAccentColor()
        {
            if (Application.Current?.Resources.TryGetValue("SystemAccentColor", out var value) == true && value is Windows.UI.Color color)
                return color;
            return Windows.UI.Color.FromArgb(255, 0, 120, 212);
        }

        private static Microsoft.UI.Xaml.Media.LinearGradientBrush PlayEdgeGlowBrush(Windows.UI.Color color, bool horizontal, bool atEnd)
        {
            var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = horizontal
                    ? new Windows.Foundation.Point(0, atEnd ? 1 : 0)
                    : new Windows.Foundation.Point(atEnd ? 1 : 0, 0),
                EndPoint = horizontal
                    ? new Windows.Foundation.Point(0, atEnd ? 0 : 1)
                    : new Windows.Foundation.Point(atEnd ? 0 : 1, 0)
            };
            var solid = Windows.UI.Color.FromArgb(150, color.R, color.G, color.B);
            var clear = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B);
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = solid, Offset = 0 });
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = clear, Offset = 1 });
            return brush;
        }

        private List<TrayImage> TrayImages()
        {
            var tray = new List<TrayImage>();
            int grid = GridCellCount;
            for (int i = VisibleCount(grid); i < grid; i++)
            {
                if (!IsEmptyCell(_items[i])) tray.Add(new TrayImage(_items[i].Path, i));
            }
            return tray;
        }

        private int TraySlotOf(string path)
        {
            int grid = GridCellCount;
            for (int i = VisibleCount(grid); i < grid; i++)
            {
                if (string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private void StoryDeckButton_Click(object sender, RoutedEventArgs e)
        {
            bool hasScenes = _activeShow != null && _activeShow.Scenes.Count > 0;
            if (hasScenes)
            {
                _shuffleChosen = false;
                _isStoryboardPlayback = false;
            }

            if (_trayCard != null)
            {
                // If drawer is open on Image Pool, switch to Storyboard tab
                if (hasScenes && _trayTab != 1)
                {
                    _trayTab = 1;
                    RefreshTrayCardIfOpen();
                    ShowAutoPlayState();
                    return;
                }

                RemoveTrayCard();
                ShowAutoPlayState();
                return;
            }

            StopAutoPlay();
            if (hasScenes) _trayTab = 1;
            ShowTrayCard(NoCell);
            ShowAutoPlayState();
        }

        private const int NoCell = -1;

        /// <summary>The image pool or the storyboard, if one of them is open. Adding pictures leaves and comes back to it.</summary>
        public (bool Open, int Tab) OpenPanel() => (_trayCard != null, _trayTab);

        /// <summary>Back to the image pool or the storyboard after leaving to add pictures.</summary>
        public void RestorePanel(int tab)
        {
            _trayTab = tab == 1 ? 1 : 0;
            ShowTrayCard(NoCell);
        }

        /// <summary>
        /// A position that cannot be seen means nothing, so images in the tray are chosen by looking at them. Opened from a cell, a click
        /// fills that cell. Opened from the bar's count, it puts the image on the canvas by the same rule as adding one.
        /// </summary>
        private void ShowTrayCard(int intoSlot)
        {
            RemoveSurpriseCard();
            RemoveTrayCard();
            if (intoSlot != NoCell) _trayTab = 0; // Forced to pool if swapping into a cell
            else if ((_activeShow?.Scenes.Count ?? 0) > 0)
            {
                _trayTab = 1;
                _shuffleChosen = false;
            }

            var all = TrayImages().Select(t => t.Path).ToList();
            var current = new System.Collections.ObjectModel.ObservableCollection<string>(all);

            var winBlue = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
            var winBlueDeep = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
            var tabBgInactive = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            var tabBorderInactive = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

            // --- 1. Top Header Toolbar (Tabs + Actions + Close) ---
            var topHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var poolTab = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = $"🗃 Pool ({SessionPictures().Count})",
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var storyTab = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = _activeShow != null && _activeShow.Scenes.Count > 0 ? $"🎬 Storyboard ({_activeShow.Scenes.Count})" : "🎬 Storyboard",
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            tabStrip.Children.Add(poolTab);
            tabStrip.Children.Add(storyTab);
            topHeader.Children.Add(tabStrip);

            Action? updateStorySummary = null;

            var headerMiddle = new Grid { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(headerMiddle, 1);
            topHeader.Children.Add(headerMiddle);

            var clearShowBtn = new Button
            {
                Content = "Clear",
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Style = Resources.TryGetValue("QuietButtonStyle", out var clStyle) && clStyle is Style cls ? cls : null
            };
            ToolTipService.SetToolTip(clearShowBtn, "Clears all scenes from the storyboard.");

            var close = new Button
            {
                Content = new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uE711", FontSize = 12 },
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(14),
                Style = Resources.TryGetValue("QuietButtonStyle", out var qStyle) && qStyle is Style qs ? qs : null
            };
            ToolTipService.SetToolTip(close, "Close drawer (Esc)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close drawer");
            close.Click += (_, _) => RemoveTrayCard();

            var rightControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            rightControls.Children.Add(clearShowBtn);
            rightControls.Children.Add(close);
            Grid.SetColumn(rightControls, 2);
            topHeader.Children.Add(rightControls);

            // --- 2. Container A: Image Pool Tab ---
            string click = intoSlot == NoCell ? "Click one to put it on the canvas."
                : $"Click one to {(IsEmptyCell(_items[intoSlot]) ? "add it to" : "swap it into")} position {PositionOfSlot(intoSlot)}.";
            var heading = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis };

            var search = new TextBox { PlaceholderText = "Search names", Width = 180, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search the image pool");

            var addImagesBtn = new Button
            {
                Content = "+ Add Images",
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(12),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Background = winBlueDeep,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = winBlue,
                VerticalAlignment = VerticalAlignment.Center
            };
            addImagesBtn.Click += (_, _) => AddImagesRequested?.Invoke(this, EventArgs.Empty);
            ToolTipService.SetToolTip(addImagesBtn, "Add images from a folder");

            var clearPoolBtn = new Button
            {
                Content = "Clear Pool",
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(12),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Style = Resources.TryGetValue("QuietButtonStyle", out var cpStyle) && cpStyle is Style cps ? cps : null
            };
            ToolTipService.SetToolTip(clearPoolBtn, "Removes all off-canvas images from the pool.");

            var hint = new TextBlock { Text = "Right-click an image to remove it.", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };

            var poolHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            poolHeaderRow.Children.Add(heading);
            poolHeaderRow.Children.Add(search);
            poolHeaderRow.Children.Add(addImagesBtn);
            poolHeaderRow.Children.Add(clearPoolBtn);
            poolHeaderRow.Children.Add(hint);
            headerMiddle.Children.Add(poolHeaderRow);

            var poolContainer = new Grid();

            var itemContainerStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"GridViewItem\">" +
                "  <Setter Property=\"Margin\" Value=\"1\"/>" +
                "  <Setter Property=\"Padding\" Value=\"0\"/>" +
                "  <Setter Property=\"MinWidth\" Value=\"0\"/>" +
                "  <Setter Property=\"MinHeight\" Value=\"0\"/>" +
                "  <Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/>" +
                "  <Setter Property=\"VerticalContentAlignment\" Value=\"Stretch\"/>" +
                "</Style>");

            var emptyTrayPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 16,
                Margin = new Thickness(12, 6, 12, 6)
            };
            var emptyTrayIcon = new FontIcon { Glyph = "\uE8B9", FontSize = 30, Foreground = winBlue, VerticalAlignment = VerticalAlignment.Center };
            var trayTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var emptyTrayTitle = new TextBlock
            {
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var emptyTrayDesc = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.75
            };
            trayTextStack.Children.Add(emptyTrayTitle);
            trayTextStack.Children.Add(emptyTrayDesc);

            emptyTrayPanel.Children.Add(emptyTrayIcon);
            emptyTrayPanel.Children.Add(trayTextStack);

            var thumbnails = new GridView
            {
                ItemsSource = current,
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                ItemContainerStyle = itemContainerStyle,
                Padding = new Thickness(0, 2, 0, 0),
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid Width=\"180\" Height=\"120\" CornerRadius=\"6\" Background=\"#1AFFFFFF\" BorderBrush=\"#2AFFFFFF\" BorderThickness=\"1\"><Image Stretch=\"UniformToFill\" Margin=\"2\"/></Grid></DataTemplate>")
            };
            var poolPanelTemplate = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "  <ItemsWrapGrid Orientation=\"Horizontal\" ItemWidth=\"184\" ItemHeight=\"124\" />" +
                "</ItemsPanelTemplate>");
            thumbnails.ItemsPanel = poolPanelTemplate;
            ScrollViewer.SetHorizontalScrollBarVisibility(thumbnails, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(thumbnails, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollMode(thumbnails, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollMode(thumbnails, ScrollMode.Enabled);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(thumbnails, "ImageTray");

            async Task ClearPoolAsync()
            {
                var tray = TrayImages();
                if (tray.Count == 0) return;
                var dialog = new ContentDialog
                {
                    Title = "Clear the image pool?",
                    Content = tray.Count == 1 ? "This removes 1 image from the pool." : $"This removes all {tray.Count} images from the pool. Images on the canvas will stay.",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                    RequestedTheme = MainWindow.Instance?.CurrentRequestedTheme ?? ElementTheme.Default
                };
                DialogFooter.MainButtonOnRight(dialog);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                CaptureUndoPoint();
                int shown = VisibleCount(GridCellCount);
                var file = BuildLayoutFile();
                if (file.Photos.Count > shown)
                {
                    file.Photos.RemoveRange(shown, file.Photos.Count - shown);
                }
                for (int i = 0; i < file.Photos.Count; i++)
                {
                    file.Photos[i].Slot = i;
                }
                foreach (var card in file.Freeform ?? new List<FreeformCellDto>())
                {
                    if (card.SourceSlot >= shown) card.SourceSlot = -1;
                }
                file.State ??= new LayoutStateDto();
                file.State.FramedSlots = shown;
                ApplyKeepingBitmaps(file);

                all.Clear();
                current.Clear();
                search.Text = string.Empty;
                ShowHeading();
                ShowTrayChanged();
                poolTab.Content = $"🗃 Pool ({SessionPictures().Count})";
                updateStorySummary?.Invoke();
            }
            clearPoolBtn.Click += async (_, _) => await ClearPoolAsync();

            void ShowHeading()
            {
                int sessionCount = SessionPictures().Count;
                int onCanvas = ImagesOnCanvas();
                bool hasTray = all.Count > 0;

                heading.Text = current.Count == all.Count
                    ? (all.Count == 0 ? (sessionCount == 0 ? "" : $"{onCanvas} images on canvas.")
                                      : $"{all.Count} in pool. {click}")
                    : current.Count == 0 ? "No image matches that name."
                    : $"{current.Count} of {all.Count} in pool. {click}";

                search.Visibility = hasTray ? Visibility.Visible : Visibility.Collapsed;
                clearPoolBtn.Visibility = hasTray ? Visibility.Visible : Visibility.Collapsed;
                hint.Visibility = hasTray ? Visibility.Visible : Visibility.Collapsed;
                thumbnails.Visibility = hasTray ? Visibility.Visible : Visibility.Collapsed;
                emptyTrayPanel.Visibility = hasTray ? Visibility.Collapsed : Visibility.Visible;

                if (!hasTray)
                {
                    emptyTrayTitle.Text = sessionCount > 0 ? "All images are on the canvas" : "No images yet";
                    emptyTrayDesc.Text = sessionCount > 0
                        ? "Right-click an image and choose Move to Image Pool, or add more images."
                        : "Add images from a folder to start.";
                }
            }

            ShowHeading();

            void RemoveFromSession(string path)
            {
                int slot = TraySlotOf(path);
                if (slot < 0) return;
                RemoveItem(_items[slot]);
                all.Remove(path);
                current.Remove(path);
                ShowHeading();
                ShowTrayChanged();
                poolTab.Content = $"🗃 Pool ({SessionPictures().Count})";
                updateStorySummary?.Invoke();
                if (all.Count > 0) search.Focus(FocusState.Programmatic);
            }

            thumbnails.ContainerContentChanging += (_, args) =>
            {
                if (args.ItemContainer.ContentTemplateRoot is not Grid cell || cell.Children[0] is not Image image) return;
                if (args.InRecycleQueue) { image.Source = null; return; }
                if (args.Item is not string path) return;
                ToolTipService.SetToolTip(args.ItemContainer, System.IO.Path.GetFileName(path));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, System.IO.Path.GetFileName(path));
                var menu = new MenuFlyout();
                if (Resources.TryGetValue("MultiViewMenuFlyoutPresenterStyle", out var style) && style is Style presenter) menu.MenuFlyoutPresenterStyle = presenter;
                menu.Items.Add(MakeAdvancedItem("Remove from image pool", "\uE74D", () => RemoveFromSession(path)));
                args.ItemContainer.ContextFlyout = menu;
                bool isActive = string.Equals(path, _activePoolPath, StringComparison.OrdinalIgnoreCase);
                cell.BorderThickness = new Thickness(isActive ? 2.5 : 1);
                cell.BorderBrush = isActive ? winBlue : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));

                try { image.Source = new BitmapImage { DecodePixelWidth = 360, UriSource = new Uri(path) }; }
                catch (UriFormatException) { image.Source = null; }
            };

            thumbnails.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is not string path) return;
                _activePoolPath = path;
                if (thumbnails.ContainerFromItem(path) is GridViewItem item && item.ContentTemplateRoot is Grid cell)
                {
                    cell.BorderThickness = new Thickness(2.5);
                    cell.BorderBrush = winBlue;
                }
                RemoveTrayCard();
                if (intoSlot == NoCell) { BringFromTray(path); return; }
                int slot = TraySlotOf(path);
                if (slot < 0) return;
                SwapItems(intoSlot, slot, false);
                ShowTrayChanged();
                NoteUserAction();
            };

            search.TextChanged += (_, _) =>
            {
                current = new System.Collections.ObjectModel.ObservableCollection<string>(all.Where(p => System.IO.Path.GetFileName(p).Contains(search.Text, StringComparison.OrdinalIgnoreCase)));
                thumbnails.ItemsSource = current;
                ShowHeading();
            };

            poolContainer.Children.Add(emptyTrayPanel);
            poolContainer.Children.Add(thumbnails);

            // --- 3. Container B: Storyboard Tab ---
            var storyContainer = new Grid();
            storyContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // draftProgressRow
            storyContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // emptyPanel / scenesView

            var storySummary = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Opacity = 0.85,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var autoDraftBtn = new Button
            {
                Content = "✨ Auto-Draft",
                Background = winBlueDeep,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = winBlue,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(autoDraftBtn, "Auto-drafts scenes from the image pool, one screen at a time.");

            var addCanvasBtn = new Button
            {
                Content = "+ Add Layout",
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(addCanvasBtn, "Adds the images on screen as a new scene.");

            var updateSceneBtn = new Button
            {
                Content = "💾 Update Scene",
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = _selectedShowScene != null
            };
            ToolTipService.SetToolTip(updateSceneBtn, "Saves current canvas layout and photos into the selected storyboard scene.");
            updateSceneBtn.Click += (_, _) =>
            {
                SyncCanvasToSelectedScene(showToast: true, commitCanvas: true);
                CaptureUndoPoint();
            };

            var onAccent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            void ShowAsPrimary(Button button, bool primary)
            {
                if (primary)
                {
                    button.Background = winBlueDeep;
                    button.Foreground = onAccent;
                    button.BorderBrush = winBlue;
                }
                else
                {
                    button.ClearValue(Control.BackgroundProperty);
                    button.ClearValue(Control.ForegroundProperty);
                    button.ClearValue(Control.BorderBrushProperty);
                }
            }

            // Storyboard Pacing & Images Range Selector (Min to Max SpinNumberBoxes inside compact Flyout)
            bool syncingStoryRange = false;
            var storyFewestBox = new SpinNumberBox
            {
                Minimum = 1,
                Maximum = MosaicLayout.MaxShown,
                Value = SurpriseChoices.FewestPictures,
                Width = 72,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(storyFewestBox, "Fewest images in a scene, from 1 to 16");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(storyFewestBox, "Minimum images per scene");

            var storyToLabel = new TextBlock
            {
                Text = "to",
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };

            var storyMostBox = new SpinNumberBox
            {
                Minimum = 1,
                Maximum = MosaicLayout.MaxShown,
                Value = SurpriseChoices.MostPictures,
                Width = 72,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(storyMostBox, "Most images in a scene, from 1 to 16");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(storyMostBox, "Maximum images per scene");

            var rangeFlyoutStack = new StackPanel { Spacing = 8, Padding = new Thickness(6) };
            var rangeHeader = new TextBlock
            {
                Text = "Images per scene:",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var rangeInputsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            rangeInputsRow.Children.Add(storyFewestBox);
            rangeInputsRow.Children.Add(storyToLabel);
            rangeInputsRow.Children.Add(storyMostBox);
            rangeFlyoutStack.Children.Add(rangeHeader);
            rangeFlyoutStack.Children.Add(rangeInputsRow);

            var draftRangeFlyout = new Flyout { Content = rangeFlyoutStack };

            var draftRangeText = new TextBlock
            {
                Text = $"({SurpriseChoices.FewestPictures}–{SurpriseChoices.MostPictures}) ▾",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.9
            };
            var draftRangeBtn = new Button
            {
                Content = draftRangeText,
                Flyout = draftRangeFlyout,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(draftRangeBtn, "Adjust images per scene range for auto-drafting");

            Border MakeDivider() => new Border { Width = 1, Height = 16, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };

            var playShowDrawerBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE768", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 12 },
                        new TextBlock { Text = "Play Show", VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
                    }
                },
                Background = winBlueDeep,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = winBlue,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 4, 14, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(playShowDrawerBtn, "Play this Storyboard presentation. Space does the same.");
            playShowDrawerBtn.Click += async (_, _) =>
            {
                _shuffleChosen = false;
                await PlayStoryboardAsync();
            };

            var storyHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            storyHeaderRow.Children.Add(playShowDrawerBtn);
            storyHeaderRow.Children.Add(MakeDivider());
            storyHeaderRow.Children.Add(addCanvasBtn);
            storyHeaderRow.Children.Add(updateSceneBtn);
            storyHeaderRow.Children.Add(MakeDivider());
            storyHeaderRow.Children.Add(autoDraftBtn);
            storyHeaderRow.Children.Add(draftRangeBtn);
            storyHeaderRow.Children.Add(MakeDivider());
            storyHeaderRow.Children.Add(storySummary);
            headerMiddle.Children.Add(storyHeaderRow);

            void SetStoryRange(int f, int m)
            {
                f = Math.Clamp(f, 1, MosaicLayout.MaxShown);
                m = Math.Clamp(m, f, MosaicLayout.MaxShown);
                if (syncingStoryRange) return;
                syncingStoryRange = true;
                try
                {
                    if (storyFewestBox.Value != f) storyFewestBox.Value = f;
                    if (storyMostBox.Value != m) storyMostBox.Value = m;
                    ChangeSurpriseChoices(o => { o.FewestPictures = f; o.MostPictures = m; });
                    SyncStoryImageRange();
                }
                finally
                {
                    syncingStoryRange = false;
                }
            }

            storyFewestBox.ValueChanged += (_, newVal) =>
            {
                if (syncingStoryRange) return;
                int f = Math.Clamp(newVal, 1, MosaicLayout.MaxShown);
                int m = Math.Max(f, storyMostBox.Value);
                SetStoryRange(f, m);
            };

            storyMostBox.ValueChanged += (_, newVal) =>
            {
                if (syncingStoryRange) return;
                int m = Math.Clamp(newVal, 1, MosaicLayout.MaxShown);
                int f = Math.Min(m, storyFewestBox.Value);
                SetStoryRange(f, m);
            };

            void SyncStoryImageRange()
            {
                int f = SurpriseChoices.FewestPictures;
                int m = SurpriseChoices.MostPictures;

                if (!syncingStoryRange)
                {
                    syncingStoryRange = true;
                    try
                    {
                        if (storyFewestBox.Value != f) storyFewestBox.Value = f;
                        if (storyMostBox.Value != m) storyMostBox.Value = m;
                        draftRangeText.Text = $"({f}–{m}) ▾";
                    }
                    finally
                    {
                        syncingStoryRange = false;
                    }
                }
            }

            SyncStoryImageRange();
            var draftProgressRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 4)
            };
            var progressRing = new ProgressRing { IsActive = true, Width = 16, Height = 16, Foreground = winBlue };
            var progressText = new TextBlock { Text = "Reading images...", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            draftProgressRow.Children.Add(progressRing);
            draftProgressRow.Children.Add(progressText);

            // Empty state view
            var emptyPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 16,
                Margin = new Thickness(12, 6, 12, 6)
            };
            var emptyIcon = new FontIcon { Glyph = "\uE8B7", FontSize = 30, Foreground = winBlue, VerticalAlignment = VerticalAlignment.Center };
            var storyTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var emptyTitle = new TextBlock
            {
                Text = "No scenes in this show yet",
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var emptyDesc = new TextBlock
            {
                Text = "Click '✨ Auto-Draft' on the toolbar above to generate scenes, or '+ Add Layout'.",
                FontSize = 12,
                Opacity = 0.75
            };
            storyTextStack.Children.Add(emptyTitle);
            storyTextStack.Children.Add(emptyDesc);

            emptyPanel.Children.Add(emptyIcon);
            emptyPanel.Children.Add(storyTextStack);

            // A shown scene used to keep the whole image pool. Drop those slots before the cards count photos.
            if (_activeShow != null)
            {
                foreach (var scene in _activeShow.Scenes)
                    scene.KeepCanvasPhotos();
            }

            // Storyboard scenes gridview with mouse drag-and-drop reordering (2-row horizontal filmstrip)
            var scenesCollection = new System.Collections.ObjectModel.ObservableCollection<ShowScene>(_activeShow?.Scenes ?? Enumerable.Empty<ShowScene>());
            var scenesView = new GridView
            {
                ItemsSource = scenesCollection,
                SelectedItem = (_selectedShowScene != null && scenesCollection.Contains(_selectedShowScene))
                    ? _selectedShowScene
                    : (_isStoryboardPlayback && _showPlaybackIndex >= 0 && _showPlaybackIndex < scenesCollection.Count ? scenesCollection[_showPlaybackIndex] : null),
                SelectionMode = ListViewSelectionMode.Single,
                CanReorderItems = true,
                CanDragItems = true,
                AllowDrop = true,
                IsItemClickEnabled = true,
                ItemContainerStyle = itemContainerStyle,
                Padding = new Thickness(0, 2, 0, 0),
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                    "  <Grid Width=\"200\" Height=\"136\" CornerRadius=\"6\" Background=\"#20FFFFFF\" BorderBrush=\"#35FFFFFF\" BorderThickness=\"1\">" +
                    "    <Image Stretch=\"UniformToFill\" Margin=\"0,0,0,24\" IsHitTestVisible=\"False\"/>" +
                    "    <Grid Margin=\"0,0,0,24\" IsHitTestVisible=\"False\" Visibility=\"Collapsed\">" +
                    "      <Image Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "      <Image Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "    </Grid>" +
                    "    <Grid Margin=\"0,0,0,24\" IsHitTestVisible=\"False\" Visibility=\"Collapsed\">" +
                    "      <Grid.RowDefinitions>" +
                    "        <RowDefinition Height=\"*\"/>" +
                    "        <RowDefinition Height=\"*\"/>" +
                    "      </Grid.RowDefinitions>" +
                    "      <Grid.ColumnDefinitions>" +
                    "        <ColumnDefinition Width=\"*\"/>" +
                    "        <ColumnDefinition Width=\"*\"/>" +
                    "      </Grid.ColumnDefinitions>" +
                    "      <Image Grid.Row=\"0\" Grid.Column=\"0\" Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "      <Image Grid.Row=\"0\" Grid.Column=\"1\" Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "      <Image Grid.Row=\"1\" Grid.Column=\"0\" Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "      <Image Grid.Row=\"1\" Grid.Column=\"1\" Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "    </Grid>" +
                    "    <Border HorizontalAlignment=\"Right\" VerticalAlignment=\"Bottom\" Margin=\"0,0,3,27\" Background=\"#E0000000\" CornerRadius=\"3\" Padding=\"3,1\" IsHitTestVisible=\"False\" Visibility=\"Collapsed\">" +
                    "      <StackPanel Orientation=\"Horizontal\" Spacing=\"3\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons\" Glyph=\"&#xE8A9;\" FontSize=\"8.5\" Foreground=\"#4CC2FF\"/>" +
                    "        <TextBlock Text=\"9 images\" FontSize=\"8.5\" Foreground=\"White\" FontWeight=\"SemiBold\"/>" +
                    "      </StackPanel>" +
                    "    </Border>" +
                    "    <Grid VerticalAlignment=\"Top\" Margin=\"3\" ColumnSpacing=\"3\">" +
                    "      <Grid.ColumnDefinitions>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"*\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "      </Grid.ColumnDefinitions>" +
                    "      <Border Grid.Column=\"0\" HorizontalAlignment=\"Left\" Background=\"#D0000000\" CornerRadius=\"3\" Padding=\"3,1\" IsHitTestVisible=\"False\">" +
                    "        <TextBlock Text=\"#1\" FontSize=\"8.5\" Foreground=\"White\" FontWeight=\"SemiBold\"/>" +
                    "      </Border>" +
                    "      <Border Grid.Column=\"2\" HorizontalAlignment=\"Right\" Background=\"#D0000000\" CornerRadius=\"3\" Padding=\"3,1\">" +
                    "        <TextBlock Text=\"⏱ 12s\" FontSize=\"8.5\" Foreground=\"#4CC2FF\"/>" +
                    "      </Border>" +
                    "    </Grid>" +
                    "    <Grid VerticalAlignment=\"Bottom\" Height=\"24\" Background=\"#F0161616\" Padding=\"4,1\">" +
                    "      <Grid.ColumnDefinitions>" +
                    "        <ColumnDefinition Width=\"*\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "      </Grid.ColumnDefinitions>" +
                    "      <TextBlock Text=\"Recipe\" FontSize=\"9.5\" Foreground=\"White\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/>" +
                    "      <Button Grid.Column=\"1\" Padding=\"3,1\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"3\" ToolTipService.ToolTip=\"Play show from this scene\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons\" Glyph=\"&#xE768;\" FontSize=\"9.5\" Foreground=\"#4CC2FF\"/>" +
                    "      </Button>" +
                    "      <Button Grid.Column=\"2\" Padding=\"3,1\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"3\" ToolTipService.ToolTip=\"Edit camera path\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons\" Glyph=\"&#xE714;\" FontSize=\"9.5\" Foreground=\"#B0FFFFFF\"/>" +
                    "      </Button>" +
                    "      <Button Grid.Column=\"3\" Padding=\"2,1\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"3\" ToolTipService.ToolTip=\"Duplicate scene\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons\" Glyph=\"&#xE8C8;\" FontSize=\"8.5\" Foreground=\"#B0FFFFFF\"/>" +
                    "      </Button>" +
                    "      <Button Grid.Column=\"4\" Padding=\"2,1\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"3\" ToolTipService.ToolTip=\"Remove scene\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons\" Glyph=\"&#xE711;\" FontSize=\"8.5\" Foreground=\"#B0FFFFFF\"/>" +
                    "      </Button>" +
                    "    </Grid>" +
                    "  </Grid>" +
                    "</DataTemplate>")
            };
            var scenesPanelTemplate = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "  <ItemsWrapGrid Orientation=\"Horizontal\" ItemWidth=\"204\" ItemHeight=\"140\" />" +
                "</ItemsPanelTemplate>");
            scenesView.ItemsPanel = scenesPanelTemplate;
            ScrollViewer.SetHorizontalScrollBarVisibility(scenesView, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(scenesView, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollMode(scenesView, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollMode(scenesView, ScrollMode.Enabled);

            var emeraldGreen = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x10, 0xB9, 0x81));
            var inactiveCardBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));

            void UpdateCardBorder(Grid card, ShowScene scene, int index)
            {
                bool isPlayingScene = _isStoryboardPlayback && (_selectedShowScene != null ? scene == _selectedShowScene : (_showPlaybackIndex >= 0 && index == _showPlaybackIndex));
                bool isSelectedScene = scenesView.SelectedItem == scene;

                if (isPlayingScene)
                {
                    card.BorderThickness = new Thickness(2.5);
                    card.BorderBrush = emeraldGreen;
                }
                else if (isSelectedScene)
                {
                    card.BorderThickness = new Thickness(2.5);
                    card.BorderBrush = winBlue;
                }
                else
                {
                    card.BorderThickness = new Thickness(1);
                    card.BorderBrush = inactiveCardBorder;
                }
            }

            void RefreshCardBadges(bool refreshImages = false)
            {
                for (int i = 0; i < scenesCollection.Count; i++)
                {
                    var sc = scenesCollection[i];
                    if (scenesView.ContainerFromItem(sc) is GridViewItem item &&
                        item.ContentTemplateRoot is Grid card)
                    {
                        if (card.Children.Count > 4 && card.Children[4] is Grid topGrid)
                        {
                            if (topGrid.Children.Count > 0 &&
                                topGrid.Children[0] is Border badgeBorder &&
                                badgeBorder.Child is TextBlock badgeText)
                            {
                                badgeText.Text = $"#{i + 1}";
                            }
                            if (topGrid.Children.Count > 1 &&
                                topGrid.Children[1] is Border dwellBorder &&
                                dwellBorder.Child is TextBlock dwellText)
                            {
                                string motionLabel = GetMotionLabel(sc);
                                double d = sc.DwellSeconds ?? 12.0;
                                dwellText.Text = $"{motionLabel} · {d:F0}s";
                                ToolTipService.SetToolTip(dwellBorder, $"Duration: {d:F0}s · {motionLabel}");
                            }
                        }
                        if (card.Children.Count > 5 && card.Children[5] is Grid bottomGrid && bottomGrid.Children.Count > 0 && bottomGrid.Children[0] is TextBlock recipeText)
                        {
                            recipeText.Text = SceneCardTitle(sc);
                            ToolTipService.SetToolTip(recipeText, recipeText.Text);
                        }
                        UpdateCardBorder(card, sc, i);
                        if (refreshImages)
                        {
                            PopulateCardImages(card, sc, forceRefresh: true);
                        }
                    }
                }
            }

            scenesView.Loaded += (_, _) =>
            {
                if (_selectedShowScene != null && scenesCollection.Contains(_selectedShowScene))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        scenesView.SelectedItem = _selectedShowScene;
                        scenesView.ScrollIntoView(_selectedShowScene);
                        RefreshCardBadges();
                    });
                }
            };

            void UpdateStoryboardSummary()
            {
                var pictures = SessionPictures();
                int totalPhotos = pictures.Count;
                int sceneCount = _activeShow?.Scenes.Count ?? 0;
                bool hasScenes = sceneCount > 0;

                emptyPanel.Visibility = hasScenes ? Visibility.Collapsed : Visibility.Visible;
                scenesView.Visibility = hasScenes ? Visibility.Visible : Visibility.Collapsed;

                clearShowBtn.Visibility = (_trayTab == 1 && hasScenes) ? Visibility.Visible : Visibility.Collapsed;
                clearShowBtn.IsEnabled = hasScenes;
                playShowDrawerBtn.Visibility = hasScenes ? Visibility.Visible : Visibility.Collapsed;

                ShowAsPrimary(autoDraftBtn, !hasScenes);

                if (hasScenes)
                {
                    double dur = _activeShow!.TotalEstimatedDuration;
                    string length = ShowLength(dur);
                    storySummary.Text = $"{sceneCount} scenes · {length}";
                    autoDraftBtn.Content = "🔄 Re-draft";
                    ToolTipService.SetToolTip(autoDraftBtn, "Re-drafts scenes from the image pool using current images-per-scene settings.");
                }
                else
                {
                    storySummary.Text = totalPhotos == 0
                        ? "No scenes yet"
                        : $"No scenes yet · {totalPhotos} image{(totalPhotos == 1 ? "" : "s")}";
                    autoDraftBtn.Content = "✨ Auto-Draft";
                    ToolTipService.SetToolTip(autoDraftBtn, "Auto-drafts scenes from the image pool, one screen at a time.");
                }

                updateSceneBtn.IsEnabled = _selectedShowScene != null && scenesCollection.Contains(_selectedShowScene);
                SyncStoryImageRange();
                storyTab.Content = hasScenes ? $"🎬 Storyboard ({sceneCount})" : "🎬 Storyboard";
                ShowShownBadge();
                ShowAutoPlayState();
            }
            updateStorySummary = UpdateStoryboardSummary;
            _updateStorySummary = UpdateStoryboardSummary;
            _refreshStoryboardCards = () => RefreshCardBadges(refreshImages: true);

            void SyncSceneSelection()
            {
                if (_selectedShowScene != null)
                {
                    var match = scenesCollection.FirstOrDefault(s => s.Id == _selectedShowScene.Id);
                    if (match != null)
                    {
                        _selectedShowScene = match;
                        scenesView.SelectedItem = match;
                        scenesView.ScrollIntoView(match);
                    }
                }
                UpdateStoryboardSummary();
                RefreshCardBadges(refreshImages: true);
            }
            _syncStorySceneSelection = SyncSceneSelection;

            bool isSyncingStoryboard = false;
            void HookCollectionChanged()
            {
                scenesCollection.CollectionChanged += (_, _) =>
                {
                    if (isSyncingStoryboard) return;
                    if (_activeShow != null)
                    {
                        _activeShow.Scenes = scenesCollection.ToList();
                        UpdateStoryboardSummary();
                        RefreshCardBadges();
                    }
                };
            }
            HookCollectionChanged();

            scenesView.DragItemsCompleted += (_, _) =>
            {
                if (_activeShow != null)
                {
                    _activeShow.Scenes = scenesCollection.ToList();
                    UpdateStoryboardSummary();
                    RefreshCardBadges();
                }
            };

            void SyncStoryboardUI()
            {
                isSyncingStoryboard = true;
                try
                {
                    var targetScenes = (_activeShow?.Scenes ?? Enumerable.Empty<ShowScene>()).ToList();
                    scenesCollection.Clear();
                    foreach (var sc in targetScenes)
                    {
                        scenesCollection.Add(sc);
                    }
                    if (_activeShow != null)
                    {
                        _activeShow.Scenes = scenesCollection.ToList();
                    }
                }
                finally
                {
                    isSyncingStoryboard = false;
                }
                UpdateStoryboardSummary();
                RefreshCardBadges();
            }

            void PopulateCardImages(Grid card, ShowScene scene, bool forceRefresh = false)
            {
                var singleImg = card.Children.Count > 0 ? card.Children[0] as Image : null;
                var twoImgGrid = card.Children.Count > 1 ? card.Children[1] as Grid : null;
                var multiGrid = card.Children.Count > 2 ? card.Children[2] as Grid : null;
                var multiBadge = card.Children.Count > 3 ? card.Children[3] as Border : null;
                var multiText = (multiBadge?.Child as StackPanel)?.Children.Count > 1 ? (multiBadge.Child as StackPanel)!.Children[1] as TextBlock : null;

                if (forceRefresh)
                {
                    if (singleImg != null) { singleImg.Tag = null; singleImg.Source = null; }
                    if (twoImgGrid != null)
                    {
                        foreach (var c in twoImgGrid.Children)
                            if (c is Image ti) { ti.Tag = null; ti.Source = null; }
                    }
                    if (multiGrid != null)
                    {
                        foreach (var c in multiGrid.Children)
                            if (c is Image mi) { mi.Tag = null; mi.Source = null; }
                    }
                }

                // Empty slots are not photos. A circles layout keeps one empty grid cell under the pictures.
                var allPhotos = scene.GetPhotoPaths().Where(p => !string.IsNullOrEmpty(p)).ToList();
                int photoCount = allPhotos.Count;

                void SetThumbnail(Image? img, string? path, int decodeWidth)
                {
                    if (img == null) return;
                    if (string.IsNullOrEmpty(path))
                    {
                        img.Tag = null;
                        img.Source = null;
                        img.Visibility = Visibility.Collapsed;
                        return;
                    }
                    img.Visibility = Visibility.Visible;
                    if (!forceRefresh && img.Tag as string == path && img.Source != null) return;
                    img.Tag = path;
                    try
                    {
                        img.Source = new BitmapImage { DecodePixelWidth = decodeWidth, UriSource = new Uri(path) };
                    }
                    catch
                    {
                        img.Tag = null;
                        img.Source = null;
                    }
                }

                if (photoCount <= 1)
                {
                    if (singleImg != null) singleImg.Visibility = Visibility.Visible;
                    if (twoImgGrid != null) twoImgGrid.Visibility = Visibility.Collapsed;
                    if (multiGrid != null) multiGrid.Visibility = Visibility.Collapsed;
                    if (multiBadge != null) multiBadge.Visibility = Visibility.Collapsed;

                    string? leadPhoto = allPhotos.FirstOrDefault();
                    SetThumbnail(singleImg, leadPhoto, 480);
                }
                else if (photoCount == 2)
                {
                    if (singleImg != null) singleImg.Visibility = Visibility.Collapsed;
                    if (twoImgGrid != null) twoImgGrid.Visibility = Visibility.Visible;
                    if (multiGrid != null) multiGrid.Visibility = Visibility.Collapsed;
                    if (multiBadge != null) multiBadge.Visibility = Visibility.Collapsed;

                    if (twoImgGrid != null && twoImgGrid.Children.Count >= 2)
                    {
                        var img0 = twoImgGrid.Children[0] as Image;
                        var img1 = twoImgGrid.Children[1] as Image;

                        // Determine if split is vertical (side-by-side columns) or horizontal (stacked rows)
                        bool isVerticalSplit = false;
                        if (scene.Layout?.Mosaic != null)
                        {
                            isVerticalSplit = scene.Layout.Mosaic.Vertical;
                        }
                        else if (scene.Layout?.Freeform != null && scene.Layout.Freeform.Count >= 2)
                        {
                            var f0 = scene.Layout.Freeform[0];
                            var f1 = scene.Layout.Freeform[1];
                            isVerticalSplit = Math.Abs(f0.X - f1.X) > Math.Abs(f0.Y - f1.Y);
                        }
                        else if (scene.Layout?.State != null && scene.Layout.State.Lines == 1)
                        {
                            isVerticalSplit = true;
                        }

                        twoImgGrid.RowDefinitions.Clear();
                        twoImgGrid.ColumnDefinitions.Clear();

                        if (isVerticalSplit)
                        {
                            // 2 Columns side-by-side
                            twoImgGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                            twoImgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            twoImgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                            if (img0 != null) { Grid.SetRow(img0, 0); Grid.SetColumn(img0, 0); }
                            if (img1 != null) { Grid.SetRow(img1, 0); Grid.SetColumn(img1, 1); }
                        }
                        else
                        {
                            // 2 Rows stacked top and bottom (e.g. Edge-to-edge landscape scenes)
                            twoImgGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                            twoImgGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                            twoImgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                            if (img0 != null) { Grid.SetRow(img0, 0); Grid.SetColumn(img0, 0); }
                            if (img1 != null) { Grid.SetRow(img1, 1); Grid.SetColumn(img1, 0); }
                        }

                        SetThumbnail(img0, allPhotos.Count > 0 ? allPhotos[0] : null, 360);
                        SetThumbnail(img1, allPhotos.Count > 1 ? allPhotos[1] : null, 360);
                    }
                }
                else
                {
                    // 3+ Photos
                    if (singleImg != null) singleImg.Visibility = Visibility.Collapsed;
                    if (twoImgGrid != null) twoImgGrid.Visibility = Visibility.Collapsed;
                    if (multiGrid != null) multiGrid.Visibility = Visibility.Visible;

                    // Badge if more than 4 photos
                    if (multiBadge != null)
                    {
                        bool moreThanShown = photoCount > 4;
                        multiBadge.Visibility = moreThanShown ? Visibility.Visible : Visibility.Collapsed;
                        if (moreThanShown && multiText != null) multiText.Text = $"{photoCount} images";
                    }

                    if (multiGrid != null)
                    {
                        if (photoCount == 3)
                        {
                            // Top banner spans 2 cols, bottom has 2 squares
                            if (multiGrid.Children.Count > 0 && multiGrid.Children[0] is Image t0)
                            {
                                Grid.SetRow(t0, 0); Grid.SetColumn(t0, 0);
                                Grid.SetRowSpan(t0, 1); Grid.SetColumnSpan(t0, 2);
                                SetThumbnail(t0, allPhotos.Count > 0 ? allPhotos[0] : null, 360);
                            }
                            if (multiGrid.Children.Count > 1 && multiGrid.Children[1] is Image t1)
                            {
                                Grid.SetRow(t1, 1); Grid.SetColumn(t1, 0);
                                Grid.SetRowSpan(t1, 1); Grid.SetColumnSpan(t1, 1);
                                SetThumbnail(t1, allPhotos.Count > 1 ? allPhotos[1] : null, 240);
                            }
                            if (multiGrid.Children.Count > 2 && multiGrid.Children[2] is Image t2)
                            {
                                Grid.SetRow(t2, 1); Grid.SetColumn(t2, 1);
                                Grid.SetRowSpan(t2, 1); Grid.SetColumnSpan(t2, 1);
                                SetThumbnail(t2, allPhotos.Count > 2 ? allPhotos[2] : null, 240);
                            }
                            if (multiGrid.Children.Count > 3 && multiGrid.Children[3] is Image t3)
                            {
                                t3.Tag = null;
                                t3.Source = null;
                                t3.Visibility = Visibility.Collapsed;
                            }
                        }
                        else
                        {
                            // Standard 2x2 grid for 4+ photos
                            for (int i = 0; i < 4 && i < multiGrid.Children.Count; i++)
                            {
                                if (multiGrid.Children[i] is Image tile)
                                {
                                    int r = i / 2;
                                    int c = i % 2;
                                    Grid.SetRow(tile, r); Grid.SetColumn(tile, c);
                                    Grid.SetRowSpan(tile, 1); Grid.SetColumnSpan(tile, 1);
                                    SetThumbnail(tile, i < allPhotos.Count ? allPhotos[i] : null, 240);
                                }
                            }
                        }
                    }
                }
            }

            scenesView.ContainerContentChanging += (_, args) =>
            {
                if (args.ItemContainer.ContentTemplateRoot is not Grid card) return;

                var singleImg = card.Children.Count > 0 ? card.Children[0] as Image : null;
                var twoImgGrid = card.Children.Count > 1 ? card.Children[1] as Grid : null;
                var multiGrid = card.Children.Count > 2 ? card.Children[2] as Grid : null;
                var topGrid = card.Children.Count > 4 ? card.Children[4] as Grid : null;
                var badgeBorder = topGrid?.Children.Count > 0 ? topGrid.Children[0] as Border : null;
                var badgeText = badgeBorder?.Child as TextBlock;
                var dwellBorder = topGrid?.Children.Count > 1 ? topGrid.Children[1] as Border : null;
                var dwellText = dwellBorder?.Child as TextBlock;
                var bottomGrid = card.Children.Count > 5 ? card.Children[5] as Grid : null;
                var recipeText = bottomGrid?.Children.Count > 0 ? bottomGrid.Children[0] as TextBlock : null;

                if (args.InRecycleQueue)
                {
                    if (singleImg != null) { singleImg.Tag = null; singleImg.Source = null; }
                    if (twoImgGrid != null)
                    {
                        foreach (var c in twoImgGrid.Children)
                            if (c is Image ti) { ti.Tag = null; ti.Source = null; }
                    }
                    if (multiGrid != null)
                    {
                        foreach (var c in multiGrid.Children)
                            if (c is Image mi) { mi.Tag = null; mi.Source = null; }
                    }
                    return;
                }
                if (args.Item is not ShowScene scene) return;

                int sceneIdx = scenesCollection.IndexOf(scene);

                string motionLabel = GetMotionLabel(scene);

                if (badgeText != null)
                {
                    // Minimal compact badge hugging only the digits (#1, #12, etc.)
                    badgeText.Text = sceneIdx >= 0 ? $"#{sceneIdx + 1}" : "";
                }
                if (dwellText != null)
                {
                    double d = scene.DwellSeconds ?? 12.0;
                    dwellText.Text = $"{motionLabel} · {d:F0}s";
                    ToolTipService.SetToolTip(dwellBorder, $"Duration: {d:F0}s · {motionLabel}");
                }
                if (recipeText != null)
                {
                    recipeText.Text = SceneCardTitle(scene);
                    ToolTipService.SetToolTip(recipeText, recipeText.Text);
                }

                UpdateCardBorder(card, scene, sceneIdx);
                PopulateCardImages(card, scene, forceRefresh: false);

                if (dwellBorder != null)
                {
                    dwellBorder.Tag = scene;
                    if (dwellBorder.DataContext == null)
                    {
                        dwellBorder.DataContext = true;
                        ToolTipService.SetToolTip(dwellBorder, "Click to change camera motion & duration");
                        dwellBorder.Tapped += (sender, e) =>
                        {
                            e.Handled = true;
                            if (sender is not FrameworkElement fe || fe.Tag is not ShowScene curScene) return;
                            var flyout = new MenuFlyout();
                            if (Resources.TryGetValue("MultiViewMenuFlyoutPresenterStyle", out var style) && style is Style presenter)
                                flyout.MenuFlyoutPresenterStyle = presenter;

                            void SetCamera(Action act)
                            {
                                act();
                                UpdateStoryboardSummary();
                                RefreshCardBadges();
                                RefreshTrayCardIfOpen();
                            }

                            string motion = GetMotionLabel(curScene);
                            bool isZoomIn = motion.Contains("Zoom In");
                            bool isZoomOut = motion.Contains("Zoom Out");
                            bool isPan = motion.Contains("Pan");
                            bool isStill = motion.Contains("Still");
                            bool isAuto = curScene.CameraOverride == null;

                            // 1. Concrete Camera Motions
                            var zoomInItem = new MenuFlyoutItem { Text = isZoomIn ? "⤢ Zoom In  ✓" : "⤢ Zoom In" };
                            zoomInItem.Click += (_, _) => SetCamera(() => curScene.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.0, 0.5, 0.5), new CameraViewDto(1.25, 0.5, 0.5), "ZoomIn"));
                            flyout.Items.Add(zoomInItem);

                            var zoomOutItem = new MenuFlyoutItem { Text = isZoomOut ? "⤡ Zoom Out  ✓" : "⤡ Zoom Out" };
                            zoomOutItem.Click += (_, _) => SetCamera(() => curScene.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.25, 0.5, 0.5), new CameraViewDto(1.0, 0.5, 0.5), "ZoomOut"));
                            flyout.Items.Add(zoomOutItem);

                            var panItem = new MenuFlyoutItem { Text = isPan ? "⇄ Pan Across  ✓" : "⇄ Pan Across" };
                            panItem.Click += (_, _) => SetCamera(() => curScene.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.15, 0.40, 0.5), new CameraViewDto(1.15, 0.60, 0.5), "Pan"));
                            flyout.Items.Add(panItem);

                            var stillItem = new MenuFlyoutItem { Text = isStill ? "• Still (No Motion)  ✓" : "• Still (No Motion)" };
                            stillItem.Click += (_, _) => SetCamera(() => curScene.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.0, 0.5, 0.5), new CameraViewDto(1.0, 0.5, 0.5), "Still"));
                            flyout.Items.Add(stillItem);

                            flyout.Items.Add(new MenuFlyoutSeparator());

                            // 2. Dynamic Runtime Mode (clearly separated from fixed motions)
                            var autoItem = new MenuFlyoutItem { Text = isAuto ? "✨ Dynamic (Auto at Playback)  ✓" : "✨ Dynamic (Auto at Playback)" };
                            ToolTipService.SetToolTip(autoItem, "Removes locked keyframes so motion is chosen dynamically on each playback loop");
                            autoItem.Click += (_, _) => SetCamera(() => curScene.CameraOverride = null);
                            flyout.Items.Add(autoItem);

                            flyout.Items.Add(new MenuFlyoutSeparator());

                            // 3. Visual Director Tooling
                            var editCamItem = MakeAdvancedItem("🎯 Edit Camera Path…", "\uE714", () =>
                            {
                                _selectedShowScene = curScene;
                                _shuffleChosen = false;
                                scenesView.SelectedItem = curScene;
                                RefreshCardBadges();
                                EnterCameraDirector(curScene);
                            });
                            flyout.Items.Add(editCamItem);

                            flyout.Items.Add(new MenuFlyoutSeparator());

                            var holdSub = new MenuFlyoutSubItem { Text = "Duration" };
                            foreach (int secs in new[] { 5, 8, 12, 16, 20, 30 })
                            {
                                int s = secs;
                                bool isCurrent = Math.Abs((curScene.DwellSeconds ?? 12.0) - s) < 0.5;
                                var item = new MenuFlyoutItem { Text = isCurrent ? $"{s}s  ✓" : $"{s}s" };
                                item.Click += (_, _) =>
                                {
                                    curScene.DwellSeconds = s;
                                    UpdateStoryboardSummary();
                                    RefreshCardBadges();
                                    RefreshTrayCardIfOpen();
                                };
                                holdSub.Items.Add(item);
                            }
                            flyout.Items.Add(holdSub);

                            flyout.ShowAt(fe);
                        };
                    }
                }

                var playBtn = bottomGrid?.Children.Count > 1 ? bottomGrid.Children[1] as Button : null;
                var camBtn = bottomGrid?.Children.Count > 2 ? bottomGrid.Children[2] as Button : null;
                var dupBtn = bottomGrid?.Children.Count > 3 ? bottomGrid.Children[3] as Button : null;
                var delBtn = bottomGrid?.Children.Count > 4 ? bottomGrid.Children[4] as Button : null;

                if (playBtn != null)
                {
                    playBtn.Tag = scene;
                    if (playBtn.CommandParameter == null)
                    {
                        playBtn.CommandParameter = true;
                        playBtn.Click += async (s, e) =>
                        {
                            if (s is Button b && b.Tag is ShowScene targetScene)
                            {
                                _selectedShowScene = targetScene;
                                int idx = scenesCollection.IndexOf(targetScene);
                                await PlayStoryboardAsync(idx >= 0 ? idx : -1);
                            }
                        };
                    }
                }
                if (camBtn != null)
                {
                    camBtn.Tag = scene;
                    if (camBtn.CommandParameter == null)
                    {
                        camBtn.CommandParameter = true;
                        camBtn.Click += (s, _) =>
                        {
                            if (s is Button b && b.Tag is ShowScene targetScene)
                            {
                                _selectedShowScene = targetScene;
                                _shuffleChosen = false;
                                scenesView.SelectedItem = targetScene;
                                RefreshCardBadges();
                                EnterCameraDirector(targetScene);
                            }
                        };
                    }
                }
                if (dupBtn != null)
                {
                    dupBtn.Tag = scene;
                    if (dupBtn.CommandParameter == null)
                    {
                        dupBtn.CommandParameter = true;
                        dupBtn.Click += (s, _) =>
                        {
                            if (s is Button b && b.Tag is ShowScene targetScene)
                            {
                                var clone = targetScene.Clone();
                                int curIdx = scenesCollection.IndexOf(targetScene);
                                if (curIdx >= 0)
                                {
                                    scenesCollection.Insert(curIdx + 1, clone);
                                    ShowAutoPlayToast("Duplicated scene");
                                }
                            }
                        };
                    }
                }
                if (delBtn != null)
                {
                    delBtn.Tag = scene;
                    if (delBtn.CommandParameter == null)
                    {
                        delBtn.CommandParameter = true;
                        delBtn.Click += (s, _) =>
                        {
                            if (s is Button b && b.Tag is ShowScene targetScene)
                            {
                                scenesCollection.Remove(targetScene);
                                ShowAutoPlayToast("Removed scene");
                            }
                        };
                    }
                }

                // Scene Context Menu
                // Lazy Scene Context Menu (constructed on demand when right-clicked)
                if (args.ItemContainer.Tag == null)
                {
                    args.ItemContainer.Tag = true;
                    args.ItemContainer.ContextRequested += (sender, e) =>
                    {
                        if (sender is not GridViewItem gvi || gvi.Content is not ShowScene sc) return;
                        var menu = new MenuFlyout();
                        if (Resources.TryGetValue("MultiViewMenuFlyoutPresenterStyle", out var style) && style is Style presenter) menu.MenuFlyoutPresenterStyle = presenter;
                        menu.Items.Add(MakeAdvancedItem("▶ Play Show from Here", "", async () =>
                        {
                            _selectedShowScene = sc;
                            int idx = scenesCollection.IndexOf(sc);
                            await PlayStoryboardAsync(idx >= 0 ? idx : -1);
                        }));
                        menu.Items.Add(MakeAdvancedItem("💾 Update Scene from Canvas", "\uE74E", () =>
                        {
                            SyncCanvasToSelectedScene(sc, showToast: true, commitCanvas: true);
                            CaptureUndoPoint();
                        }));

                        menu.Items.Add(new MenuFlyoutSeparator());

                        // Camera Motion Submenu
                        var motionSub = new MenuFlyoutSubItem { Text = "Camera Motion" };
                        void SetCameraSub(Action act)
                        {
                            act();
                            UpdateStoryboardSummary();
                            RefreshCardBadges();
                            RefreshTrayCardIfOpen();
                        }

                        string motionSubStr = GetMotionLabel(sc);
                        bool isZIn = motionSubStr.Contains("Zoom In");
                        bool isZOut = motionSubStr.Contains("Zoom Out");
                        bool isP = motionSubStr.Contains("Pan");
                        bool isSt = motionSubStr.Contains("Still");
                        bool isAutoSub = sc.CameraOverride == null;

                        // 1. Concrete Camera Motions
                        var zInItem = new MenuFlyoutItem { Text = isZIn ? "⤢ Zoom In  ✓" : "⤢ Zoom In" };
                        zInItem.Click += (_, _) => SetCameraSub(() => sc.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.0, 0.5, 0.5), new CameraViewDto(1.25, 0.5, 0.5), "ZoomIn"));
                        motionSub.Items.Add(zInItem);

                        var zOutItem = new MenuFlyoutItem { Text = isZOut ? "⤡ Zoom Out  ✓" : "⤡ Zoom Out" };
                        zOutItem.Click += (_, _) => SetCameraSub(() => sc.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.25, 0.5, 0.5), new CameraViewDto(1.0, 0.5, 0.5), "ZoomOut"));
                        motionSub.Items.Add(zOutItem);

                        var pItem = new MenuFlyoutItem { Text = isP ? "⇄ Pan Across  ✓" : "⇄ Pan Across" };
                        pItem.Click += (_, _) => SetCameraSub(() => sc.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.15, 0.40, 0.5), new CameraViewDto(1.15, 0.60, 0.5), "Pan"));
                        motionSub.Items.Add(pItem);

                        var stItem = new MenuFlyoutItem { Text = isSt ? "• Still (No Motion)  ✓" : "• Still (No Motion)" };
                        stItem.Click += (_, _) => SetCameraSub(() => sc.CameraOverride = new CameraKeyframeOverride(new CameraViewDto(1.0, 0.5, 0.5), new CameraViewDto(1.0, 0.5, 0.5), "Still"));
                        motionSub.Items.Add(stItem);

                        motionSub.Items.Add(new MenuFlyoutSeparator());

                        // 2. Dynamic Runtime Mode (clearly separated from fixed motions)
                        var autoSubItem = new MenuFlyoutItem { Text = isAutoSub ? "✨ Dynamic (Auto at Playback)  ✓" : "✨ Dynamic (Auto at Playback)" };
                        ToolTipService.SetToolTip(autoSubItem, "Removes locked keyframes so motion is chosen dynamically on each playback loop");
                        autoSubItem.Click += (_, _) => SetCameraSub(() => sc.CameraOverride = null);
                        motionSub.Items.Add(autoSubItem);

                        motionSub.Items.Add(new MenuFlyoutSeparator());

                        // 3. Visual Director Tooling
                        motionSub.Items.Add(MakeAdvancedItem("🎯 Edit Camera Path…", "\uE714", () =>
                        {
                            _selectedShowScene = sc;
                            _shuffleChosen = false;
                            scenesView.SelectedItem = sc;
                            RefreshCardBadges();
                            EnterCameraDirector(sc);
                        }));
                        menu.Items.Add(motionSub);

                        // Quick Duration Adjustment Submenu
                        var dwellSub = new MenuFlyoutSubItem { Text = "Duration" };
                        foreach (int secs in new[] { 5, 8, 12, 16, 20, 30 })
                        {
                            int s = secs;
                            bool isCurrent = Math.Abs((sc.DwellSeconds ?? 12.0) - s) < 0.5;
                            var item = new MenuFlyoutItem { Text = isCurrent ? $"{s}s  ✓" : $"{s}s" };
                            item.Click += (_, _) =>
                            {
                                sc.DwellSeconds = s;
                                UpdateStoryboardSummary();
                                RefreshCardBadges();
                                RefreshTrayCardIfOpen();
                            };
                            dwellSub.Items.Add(item);
                        }
                        menu.Items.Add(dwellSub);

                        menu.Items.Add(new MenuFlyoutSeparator());
                        menu.Items.Add(MakeAdvancedItem("Duplicate Scene", "", () =>
                        {
                            var clone = sc.Clone();
                            int curIdx = scenesCollection.IndexOf(sc);
                            if (curIdx >= 0)
                            {
                                scenesCollection.Insert(curIdx + 1, clone);
                                ShowAutoPlayToast("Duplicated scene");
                            }
                        }));
                        menu.Items.Add(MakeAdvancedItem("Remove Scene", "", () =>
                        {
                            scenesCollection.Remove(sc);
                            ShowAutoPlayToast("Removed scene");
                        }));
                        int curScIdx = scenesCollection.IndexOf(sc);
                        if (curScIdx > 0)
                        {
                            menu.Items.Add(MakeAdvancedItem("Move Earlier", "", () =>
                            {
                                int cur = scenesCollection.IndexOf(sc);
                                if (cur > 0)
                                {
                                    scenesCollection.Move(cur, cur - 1);
                                }
                            }));
                        }
                        if (curScIdx >= 0 && curScIdx < scenesCollection.Count - 1)
                        {
                            menu.Items.Add(MakeAdvancedItem("Move Later", "", () =>
                            {
                                int cur = scenesCollection.IndexOf(sc);
                                if (cur >= 0 && cur < scenesCollection.Count - 1)
                                {
                                    scenesCollection.Move(cur, cur + 1);
                                }
                            }));
                        }

                        if (e.TryGetPosition(gvi, out var p))
                        {
                            menu.ShowAt(gvi, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = p });
                        }
                        else
                        {
                            menu.ShowAt(gvi);
                        }
                        e.Handled = true;
                    };
                }
            };

            scenesView.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is not ShowScene scene) return;
                if (_selectedShowScene != null && _selectedShowScene != scene && !_isStoryboardPlayback)
                {
                    SyncCanvasToSelectedScene(showToast: false);
                    CaptureUndoPoint();
                }
                _selectedShowScene = scene;
                _canvasSceneId = null;
                _shuffleChosen = false;
                scenesView.SelectedItem = scene;
                if (updateSceneBtn != null) updateSceneBtn.IsEnabled = true;
                for (int i = 0; i < scenesCollection.Count; i++)
                {
                    var s = scenesCollection[i];
                    if (scenesView.ContainerFromItem(s) is GridViewItem gvi && gvi.ContentTemplateRoot is Grid c)
                    {
                        UpdateCardBorder(c, s, i);
                    }
                }
                // Single-click previews on canvas without closing the panel
                if (scene.Layout != null)
                {
                    ApplyLayoutFile(WithRestInTray(scene.Layout, SessionPictures()), restoreAspect: false, preserveUndo: true);
                    _canvasSceneId = scene.Id;
                    CaptureUndoPoint();
                }
                ShowAutoPlayState();
            };

            scenesView.DoubleTapped += async (s, e) =>
            {
                if (scenesView.SelectedItem is ShowScene scene)
                {
                    _selectedShowScene = scene;
                    int idx = scenesCollection.IndexOf(scene);
                    await PlayStoryboardAsync(idx >= 0 ? idx : -1);
                }
            };

            scenesView.KeyDown += async (s, e) =>
            {
                if (scenesView.SelectedItem is not ShowScene selectedScene) return;

                if (e.Key == Windows.System.VirtualKey.Space)
                {
                    e.Handled = true;
                    if (_autoPlaySeconds > 0)
                    {
                        StopAutoPlay();
                        ShowAutoPlayToast("Paused");
                    }
                    else
                    {
                        int idx = scenesCollection.IndexOf(selectedScene);
                        await PlayStoryboardAsync(idx >= 0 ? idx : -1);
                    }
                }
                else if (e.Key == Windows.System.VirtualKey.Delete)
                {
                    e.Handled = true;
                    scenesCollection.Remove(selectedScene);
                    ShowAutoPlayToast("Removed scene");
                }
                else if (e.Key == Windows.System.VirtualKey.D && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                {
                    e.Handled = true;
                    var clone = selectedScene.Clone();
                    int curIdx = scenesCollection.IndexOf(selectedScene);
                    if (curIdx >= 0)
                    {
                        scenesCollection.Insert(curIdx + 1, clone);
                        scenesView.SelectedItem = clone;
                        ShowAutoPlayToast("Duplicated scene");
                    }
                }
                else if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    int idx = scenesCollection.IndexOf(selectedScene);
                    await PlayStoryboardAsync(idx >= 0 ? idx : -1);
                }
            };

            _onStoryboardPlaybackIndexChanged = idx =>
            {
                if (idx >= 0 && idx < scenesCollection.Count)
                {
                    var activeScene = scenesCollection[idx];
                    _selectedShowScene = activeScene;
                    scenesView.SelectedItem = activeScene;
                    scenesView.ScrollIntoView(activeScene);
                    RefreshCardBadges();
                }
            };

            async Task RunAutoDraftAsync()
            {
                var pictures = SessionPictures();
                if (pictures.Count == 0) return;

                int sceneCount = _activeShow?.Scenes.Count ?? 0;
                if (sceneCount > 0)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Replace the storyboard?",
                        Content = sceneCount == 1
                            ? "This replaces the scene with a new draft."
                            : $"This replaces all {sceneCount} scenes with a new draft.",
                        PrimaryButtonText = "Replace",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot,
                        RequestedTheme = MainWindow.Instance?.CurrentRequestedTheme ?? ElementTheme.Default
                    };
                    DialogFooter.MainButtonOnRight(dialog);
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }

                autoDraftBtn.IsEnabled = false;
                draftProgressRow.Visibility = Visibility.Visible;
                progressText.Text = "Reading images...";

                try
                {
                    pictures = SessionPictures();
                    if (pictures.Count == 0) return;

                    var ready = DecodedBitmaps();
                    var facts = await ReadFactsAsync(pictures, path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);

                    var progress = new Progress<ShowDraftProgress>(p =>
                    {
                        progressText.Text = $"Making scene {p.CompletedScenes} of {p.TotalEstimatedScenes}...";
                    });

                    _activeShow = await ShowDirector.DraftAsync(
                        facts,
                        SurpriseChoices,
                        ActualWidth,
                        ActualHeight,
                        "Auto-Drafted Presentation",
                        progress);
                    _selectedShowScene = null;
                    _canvasSceneId = null;
                    _shuffleChosen = false;
                    _isStoryboardPlayback = false;
                    _showPlaybackIndex = 0;
                }
                finally
                {
                    draftProgressRow.Visibility = Visibility.Collapsed;
                    autoDraftBtn.IsEnabled = true;
                    SyncStoryboardUI();
                }
            }

            void CaptureCurrentCanvasAsScene()
            {
                var pictures = SessionPictures();
                if (pictures.Count == 0) return;
                var file = BuildLayoutFile();
                int onShow = ShownSlots;
                var sceneFile = new MultiViewLayoutFile
                {
                    Created = file.Created,
                    Canvas = file.Canvas,
                    State = file.State,
                    Mosaic = file.Mosaic,
                    Photos = file.Photos.Where(p => p.Slot < onShow && !string.IsNullOrEmpty(p.Path)).ToList(),
                    Freeform = file.Freeform != null ? new List<FreeformCellDto>(file.Freeform) : null
                };
                int photoCount = sceneFile.Photos.Count + (sceneFile.Freeform?.Count ?? 0);
                if (photoCount == 0)
                {
                    ShowAutoPlayToast("Put at least one image on the canvas first");
                    return;
                }
                _shuffleChosen = false;
                string recipe = photoCount == 1 ? "Solo" : "Custom";
                string title = photoCount == 1 ? "Custom Solo" : $"Custom Layout ({photoCount})";
                var scene = new ShowScene
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = title,
                    Recipe = recipe,
                    Layout = sceneFile,
                    DwellSeconds = SurpriseChoices.PlaySeconds,
                    Transition = "Crossfade"
                };

                _activeShow ??= new ShowProject { Title = "Custom Presentation" };
                scenesCollection.Add(scene);
                _selectedShowScene = scene;
                _canvasSceneId = scene.Id;
                ShowAutoPlayToast($"Added this layout as scene {scenesCollection.Count}");
            }

            addCanvasBtn.Click += (_, _) => CaptureCurrentCanvasAsScene();
            autoDraftBtn.Click += async (_, _) => await RunAutoDraftAsync();
            clearShowBtn.Click += async (_, _) =>
            {
                if (_activeShow == null || _activeShow.Scenes.Count == 0) return;
                int count = _activeShow.Scenes.Count;
                var dialog = new ContentDialog
                {
                    Title = "Clear the storyboard?",
                    Content = count == 1 ? "This removes the scene." : $"This removes all {count} scenes.",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                    RequestedTheme = MainWindow.Instance?.CurrentRequestedTheme ?? ElementTheme.Default
                };
                DialogFooter.MainButtonOnRight(dialog);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                _shuffleChosen = false;
                _isStoryboardPlayback = false;
                _selectedShowScene = null;
                _canvasSceneId = null;
                _activeShow.Scenes.Clear();
                scenesCollection.Clear();
            };

            storyContainer.Children.Add(draftProgressRow);
            Grid.SetRow(emptyPanel, 1);
            storyContainer.Children.Add(emptyPanel);
            Grid.SetRow(scenesView, 1);
            storyContainer.Children.Add(scenesView);

            SyncStoryboardUI();

            // --- 4. Main Tab Switching Logic ---
            void SyncTabs()
            {
                poolTab.IsChecked = _trayTab == 0;
                poolTab.Background = _trayTab == 0 ? winBlueDeep : tabBgInactive;
                poolTab.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                poolTab.BorderBrush = _trayTab == 0 ? winBlue : tabBorderInactive;

                storyTab.IsChecked = _trayTab == 1;
                storyTab.Background = _trayTab == 1 ? winBlueDeep : tabBgInactive;
                storyTab.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                storyTab.BorderBrush = _trayTab == 1 ? winBlue : tabBorderInactive;

                poolHeaderRow.Visibility = _trayTab == 0 ? Visibility.Visible : Visibility.Collapsed;
                storyHeaderRow.Visibility = _trayTab == 1 ? Visibility.Visible : Visibility.Collapsed;

                bool hasScenes = (_activeShow?.Scenes.Count ?? 0) > 0;
                clearShowBtn.Visibility = (_trayTab == 1 && hasScenes) ? Visibility.Visible : Visibility.Collapsed;

                poolContainer.Visibility = _trayTab == 0 ? Visibility.Visible : Visibility.Collapsed;
                storyContainer.Visibility = _trayTab == 1 ? Visibility.Visible : Visibility.Collapsed;
                ShowShownBadge();
            }

            poolTab.Click += (_, _) => { _trayTab = 0; SyncTabs(); if (all.Count > 0) search.Focus(FocusState.Programmatic); };
            storyTab.Click += (_, _) =>
            {
                _trayTab = 1;
                if ((_activeShow?.Scenes.Count ?? 0) > 0)
                {
                    _shuffleChosen = false;
                }
                SyncTabs();
                ShowAutoPlayState();
            };

            SyncTabs();

            // Assemble Body
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: Splitter
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: Header Toolbar
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 2: Content (Pool / Storyboard)

            // Top drag splitter hit-target (8-11px thick, transparent background with subtle grab cue)
            var splitter = new SplitterElement
            {
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            splitter.SetCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);

            var grabPill = new Border
            {
                Width = 36,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            splitter.Children.Add(grabPill);

            bool isDraggingSplitter = false;
            double dragStartY = 0;
            double dragStartHeight = 0;

            splitter.PointerEntered += (_, _) => grabPill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            splitter.PointerExited += (_, _) => { if (!isDraggingSplitter) grabPill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)); };

            splitter.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
                isDraggingSplitter = true;
                dragStartY = e.GetCurrentPoint(RootGrid).Position.Y;
                dragStartHeight = _trayCard?.ActualHeight > 0 ? _trayCard.ActualHeight : _trayDrawerHeight;
                splitter.CapturePointer(e.Pointer);
                grabPill.Background = winBlue;
                e.Handled = true;
            };

            splitter.PointerMoved += (s, e) =>
            {
                if (!isDraggingSplitter || _trayCard == null) return;
                double currentY = e.GetCurrentPoint(RootGrid).Position.Y;
                double deltaY = dragStartY - currentY;
                double minH = SingleRowDrawerHeight;
                double maxH = Math.Max(450, RootGrid.ActualHeight - 80);
                double targetH = Math.Clamp(dragStartHeight + deltaY, minH, maxH);
                _trayDrawerHeight = targetH;
                _trayCard.Height = targetH;
                UpdatePlaybarReaction(targetH);
                e.Handled = true;
            };

            splitter.PointerReleased += (s, e) =>
            {
                if (isDraggingSplitter)
                {
                    isDraggingSplitter = false;
                    splitter.ReleasePointerCapture(e.Pointer);
                    grabPill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                    e.Handled = true;
                }
            };

            splitter.PointerCaptureLost += (s, e) =>
            {
                isDraggingSplitter = false;
                grabPill.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            };

            splitter.DoubleTapped += (_, e) =>
            {
                if (_trayCard == null) return;
                double expandedH = Math.Min(380, RootGrid.ActualHeight - 80);
                double target = _trayDrawerHeight > SingleRowDrawerHeight + 30 ? SingleRowDrawerHeight : expandedH;
                _trayDrawerHeight = target;
                _trayCard.Height = target;
                UpdatePlaybarReaction(target);
                e.Handled = true;
            };

            body.Children.Add(splitter);
            Grid.SetRow(splitter, 0);

            topHeader.Margin = new Thickness(0, 2, 0, 4);
            Grid.SetRow(topHeader, 1);
            body.Children.Add(topHeader);

            Grid.SetRow(poolContainer, 2);
            body.Children.Add(poolContainer);

            Grid.SetRow(storyContainer, 2);
            body.Children.Add(storyContainer);

            // Docked at the very bottom of the screen
            _trayCard = NewCard(body);
            _trayCard.Height = _trayDrawerHeight;
            _trayCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            _trayCard.VerticalAlignment = VerticalAlignment.Bottom;
            _trayCard.Margin = new Thickness(0, 0, 0, 0);
            _trayCard.Padding = new Thickness(12, 0, 12, 6);
            _trayCard.CornerRadius = new CornerRadius(0);
            _trayCard.BorderThickness = new Thickness(0, 1, 0, 0);
            _trayCard.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
            _trayCard.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xF6, 0x14, 0x14, 0x14));
            AddOverCanvas(_trayCard, 6500);

            UpdatePlaybarReaction(_trayDrawerHeight);

            HudMenu_Opened(this, EventArgs.Empty);
            ShowShownBadge();
            if (_trayTab == 0 && all.Count > 0) search.Loaded += (_, _) => search.Focus(FocusState.Programmatic);
        }

        private async Task PlayStoryboardAsync(int startIndex = -1)
        {
            if (_activeShow == null || _activeShow.Scenes.Count == 0) return;

            var visibleScenes = _activeShow.Scenes.Where(s => !s.IsHidden).ToList();
            if (visibleScenes.Count == 0) return;

            int targetIndex = startIndex;
            if (targetIndex < 0 && _selectedShowScene != null)
            {
                targetIndex = visibleScenes.IndexOf(_selectedShowScene);
            }
            if (targetIndex < 0 || targetIndex >= visibleScenes.Count)
            {
                targetIndex = 0;
            }

            // Sync current canvas before touching any playback state
            SyncCanvasToSelectedScene(showToast: false, force: true);
            CaptureUndoPoint();

            RemoveTrayCard();
            StopAutoPlay();
            _isStoryboardPlayback = true;
            _shuffleChosen = false;
            _showPlaybackIndex = targetIndex;
            _autoPlaySeconds = 12;
            AddAutoPlayShield();
            StartAutoPlayMusic();

            var startScene = visibleScenes[targetIndex];
            _selectedShowScene = startScene;
            _canvasSceneId = null;
            _onStoryboardPlaybackIndexChanged?.Invoke(targetIndex);
            _layoutSeconds = (int)(startScene.DwellSeconds ?? SurpriseChoices.NextPlaySeconds(Random.Shared));
            ShowAutoPlayState();
            ShowAutoPlayToast($"Playing Scene {targetIndex + 1} of {visibleScenes.Count}: {startScene.Title ?? startScene.Recipe ?? "Scene"}");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, _layoutSeconds)) };
            _autoPlayTimer = timer;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (_isStoryboardPlayback && _activeShow != null && _activeShow.Scenes.Count > 0)
                {
                    await PlayNextStoryboardSceneAsync(fade: true);
                }
                else
                {
                    await SurpriseAsync(fade: true);
                }
                timer.Interval = TimeSpan.FromSeconds(Math.Max(1, _layoutSeconds));
                if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
            };
            await PlayNextStoryboardSceneAsync(fade: true);
            timer.Interval = TimeSpan.FromSeconds(Math.Max(1, _layoutSeconds));
            if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
        }

        private async Task PlayNextStoryboardSceneAsync(bool fade)
        {
            if (_activeShow == null || _activeShow.Scenes.Count == 0)
            {
                StopAutoPlay();
                return;
            }

            var visibleScenes = _activeShow.Scenes.Where(s => !s.IsHidden).ToList();
            if (visibleScenes.Count == 0)
            {
                StopAutoPlay();
                return;
            }

            if (_showPlaybackIndex < 0 || _showPlaybackIndex >= visibleScenes.Count)
                _showPlaybackIndex = 0;

            int currentIndex = _showPlaybackIndex;
            var scene = visibleScenes[currentIndex];
            _selectedShowScene = scene;
            _canvasSceneId = null;
            _onStoryboardPlaybackIndexChanged?.Invoke(currentIndex);
            _layoutSeconds = (int)(scene.DwellSeconds ?? SurpriseChoices.NextPlaySeconds(Random.Shared));

            var pictures = SessionPictures();
            var ready = DecodedBitmaps();
            var outgoingStep = fade ? OutgoingCameraStep(0.7) : (1.0, 0.0, 0.0);
            Image? before = fade ? await CoverWithPictureOfCanvasAsync() : null;
            ResetCanvasCamera();

            _restoringUndo = true;
            _reuseBitmaps = ready;
            try
            {
                ApplyLayoutFile(WithRestInTray(scene.Layout, pictures), restoreAspect: false, preserveUndo: true);
                _canvasSceneId = scene.Id;
            }
            finally
            {
                _reuseBitmaps = null;
                _restoringUndo = false;
            }

            _settleTicket++;
            _surpriseSettling = true;
            try
            {
                await WaitForPicturesAsync();
            }
            finally
            {
                _surpriseSettling = false;
            }

            if (scene.CameraOverride != null)
            {
                var move = scene.CameraOverride.ToCameraMove(_layoutSeconds + CameraOverrunSeconds);
                if (move != null)
                {
                    RunCamera(move, goingHome: false);
                }
            }
            else
            {
                var move = CameraMove.Plan(
                    Array.Empty<(double, double)>(),
                    SurpriseChoices.MotionAmount,
                    SurpriseChoices.MotionZoomChance,
                    SurpriseChoices.MotionFaceChance,
                    _layoutSeconds + CameraOverrunSeconds,
                    _lastCameraKind,
                    Random.Shared,
                    SurpriseChoices.ForcedCameraKind);
                if (move != null)
                {
                    _lastCameraKind = move.Kind;
                    RunCamera(move, goingHome: false);
                }
            }

            if (before != null) ChangeLayoutOver(before, outgoingStep);

            int displayIndex = currentIndex + 1;
            int totalDisplay = visibleScenes.Count;
            string sceneName = scene.Title ?? scene.Recipe ?? $"Scene {displayIndex}";
            ShowAutoPlayToast($"Scene {displayIndex} of {totalDisplay}: {sceneName}");
            if (_autoPlaySeconds > 0) StartPlaybackProgress(_layoutSeconds);
            else StopPlaybackProgress();

            _showPlaybackIndex = (currentIndex + 1) % visibleScenes.Count;
            ShowAutoPlayState();
        }

        private void RefreshTrayCardIfOpen()
        {
            if (_trayCard == null) return;
            int tab = _trayTab;
            ShowTrayCard(NoCell);
            _trayTab = tab;
        }

        private async Task StepStoryboardScene(bool back)
        {
            if (_activeShow == null || _activeShow.Scenes.Count == 0) return;
            var visibleScenes = _activeShow.Scenes.Where(s => !s.IsHidden).ToList();
            if (visibleScenes.Count == 0) return;

            SyncCanvasToSelectedScene(showToast: false, force: true);
            CaptureUndoPoint();

            int target;
            if (_selectedShowScene != null)
            {
                int current = visibleScenes.IndexOf(_selectedShowScene);
                if (current < 0) current = 0;
                target = back
                    ? (current - 1 + visibleScenes.Count) % visibleScenes.Count
                    : (current + 1) % visibleScenes.Count;
            }
            else if (_showPlaybackIndex < 0)
            {
                target = back ? visibleScenes.Count - 1 : 0;
            }
            else
            {
                int current = (_showPlaybackIndex - 1 + visibleScenes.Count) % visibleScenes.Count;
                target = back
                    ? (current - 1 + visibleScenes.Count) % visibleScenes.Count
                    : (current + 1) % visibleScenes.Count;
            }

            _shuffleChosen = false;
            _showPlaybackIndex = target;
            await PlayNextStoryboardSceneAsync(fade: true);

            if (_autoPlayTimer != null)
            {
                _autoPlayTimer.Stop();
                _autoPlayTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _layoutSeconds));
                _autoPlayTimer.Start();
            }
        }

        private void ShowTrayChanged()
        {
            for (int i = 0; i < VisibleCount(GridCellCount); i++)
                ShowEmptyState(i);
            ShowShownBadge();
        }

        public void SyncCanvasToSelectedScene(ShowScene? targetScene = null, bool showToast = false, bool force = false, bool commitCanvas = false)
        {
            if (_isStoryboardPlayback && !force) return;
            var scene = targetScene ?? _selectedShowScene;
            if (scene == null || _activeShow == null) return;
            if (!ShowProject.ShouldStoreCanvas(_canvasSceneId, scene.Id, commitCanvas)) return;

            var file = BuildLayoutFile();
            int onShow = ShownSlots;
            var sceneFile = new MultiViewLayoutFile
            {
                Created = file.Created,
                Canvas = file.Canvas,
                State = file.State,
                Mosaic = file.Mosaic,
                Photos = file.Photos.Where(p => p.Slot < onShow && !string.IsNullOrEmpty(p.Path)).ToList(),
                Freeform = file.Freeform != null ? new List<FreeformCellDto>(file.Freeform) : null,
                ActiveSceneId = scene.Id
            };

            int photoCount = sceneFile.Photos.Count + (sceneFile.Freeform?.Count ?? 0);
            if (photoCount == 0) return;

            scene.Layout = MultiViewLayoutFile.FromJson(sceneFile.ToJson()) ?? sceneFile;
            if (commitCanvas) _canvasSceneId = scene.Id;
            _activeShow.Modified = DateTime.UtcNow;
            _refreshStoryboardCards?.Invoke();
            _updateStorySummary?.Invoke();
            if (showToast)
            {
                ShowAutoPlayToast($"Updated scene \"{scene.Title}\" from canvas");
            }
        }

        private void RemoveTrayCard()
        {
            if (_trayCard == null) return;
            RootGrid.Children.Remove(_trayCard);
            _trayCard = null;
            _updateStorySummary = null;
            _refreshStoryboardCards = null;
            _syncStorySceneSelection = null;
            _onStoryboardPlaybackIndexChanged = null;
            UpdatePlaybarReaction(0);
            ShowShownBadge();
            HudMenu_Closed(this, EventArgs.Empty);
            // The search box had the keyboard, so the keys would go nowhere otherwise.
            FocusView();
        }

        private static bool IsInside(object? source, DependencyObject container)
        {
            for (var node = source as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, container)) return true;
            }
            return false;
        }

        private bool IsInSurpriseCard(object? source) =>
            IsInside(source, SurpriseControl) || (_surpriseCard != null && IsInside(source, _surpriseCard));

        private bool? _lastPlayWasLong;
        private bool _lastHandWasSolo;

        /// <summary>The first new layout comes at once, so choosing it visibly does something.</summary>
        private void StartChosenPlay()
        {
            if (PlayUsesStoryboard())
                _ = PlayStoryboardAsync();
            else
                StartAutoPlay(SurpriseChoices.PlaySeconds);
        }

        private async void StartAutoPlay(int seconds)
        {
            DeselectCell();
            if (PlayUsesStoryboard())
            {
                await PlayStoryboardAsync();
                return;
            }
            _isStoryboardPlayback = false;
            int minPics = SurpriseChoices.FewestPictures == 1 ? 1 : 2;
            if (SessionPictures().Count < minPics)
            {
                AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            StopAutoPlay();
            CaptureUndoPoint();
            _autoPlaySeconds = seconds;
            _lastPlayWasLong = null;
            AddAutoPlayShield();
            StartAutoPlayMusic();
            ShowAutoPlayState();
            ShowAutoPlayToast($"Playing: a new layout every {SurpriseChoices.PlayWords}");
            _layoutSeconds = SurpriseChoices.NextPlaySeconds(Random.Shared);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_layoutSeconds) };
            _autoPlayTimer = timer;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (_isStoryboardPlayback && _activeShow != null && _activeShow.Scenes.Count > 0)
                {
                    await PlayNextStoryboardSceneAsync(fade: true);
                }
                else
                {
                    await SurpriseAsync(fade: true);
                }
                timer.Interval = TimeSpan.FromSeconds(_layoutSeconds);
                if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
            };
            await SurpriseAsync(fade: true);
            timer.Interval = TimeSpan.FromSeconds(_layoutSeconds);
            if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
        }

        private void StopAutoPlay()
        {
            if (_autoPlaySeconds == 0) return;
            _autoPlaySeconds = 0;
            _isStoryboardPlayback = false;
            StopPlaybackProgress();
            _autoPlayTimer?.Stop();
            _autoPlayTimer = null;
            if (!_autoPlayShieldPressed) RemoveAutoPlayShield();
            SendCanvasCameraHome();
            SyncCanvasToSelectedScene(showToast: false);
            CaptureUndoPoint();
            _surpriseMusic?.SetPlaying(false);
            ShowAutoPlayState();
            if (PlayUsesStoryboard() && _activeShow != null && _activeShow.Scenes.Count > 0)
            {
                var visibleScenes = _activeShow.Scenes.Where(s => !s.IsHidden).ToList();
                int idx = _selectedShowScene != null ? visibleScenes.IndexOf(_selectedShowScene) : -1;
                int cur = (idx >= 0 ? idx : 0) + 1;
                ShowAutoPlayToast($"Paused · Scene {cur} of {visibleScenes.Count}");
            }
            else
            {
                ShowAutoPlayToast("Paused Auto-Mix");
            }
        }

        /// <summary>The bar shows Play while a show is running, and exactly one of Storyboard and Surprise me as the playback mode.</summary>
        private void ShowAutoPlayState()
        {
            bool playing = _autoPlaySeconds > 0;
            bool storyboardMode = PlayUsesStoryboard();
            bool hasScenes = _activeShow != null && _activeShow.Scenes.Count > 0;
            var visibleScenes = hasScenes ? _activeShow!.Scenes.Where(s => !s.IsHidden).ToList() : null;
            int totalScenes = visibleScenes?.Count ?? 0;
            if (totalScenes == 0) storyboardMode = false;

            int curScene = 1;
            string currentSceneTitle = "Scene 1";
            if (storyboardMode && visibleScenes != null && visibleScenes.Count > 0)
            {
                int idx = _selectedShowScene != null ? visibleScenes.IndexOf(_selectedShowScene) : -1;
                if (idx < 0 && _showPlaybackIndex >= 0 && _showPlaybackIndex < visibleScenes.Count)
                {
                    idx = _showPlaybackIndex;
                }
                if (idx >= 0)
                {
                    curScene = idx + 1;
                    var sc = visibleScenes[idx];
                    currentSceneTitle = sc.Title ?? sc.Recipe ?? $"Scene {curScene}";
                }
            }

            string glyph = playing ? "\uE769" : "\uE768";
            string text = ShowProject.PlayTransportText(playing, storyboardMode, curScene, totalScenes);

            AutoPlayIcon.Glyph = glyph;
            AutoPlayText.Text = text;

            string playTip = playing
                ? (storyboardMode
                    ? $"Pause the storyboard show (Scene {curScene} of {totalScenes}: {currentSceneTitle}). Space does the same. Left and Right move between scenes."
                    : "Pause Auto-Mix. Space does the same. Left and Right move between layouts.")
                : (storyboardMode
                    ? (curScene > 1 && curScene <= totalScenes
                        ? $"Resume the storyboard show at Scene {curScene} of {totalScenes} ({currentSceneTitle}). Space does the same."
                        : $"Play the storyboard ({totalScenes} {(totalScenes == 1 ? "scene" : "scenes")}, {ShowLength(_activeShow!.TotalEstimatedDuration)}). Space does the same.")
                    : (hasScenes
                        ? $"Play a new layout every {SurpriseChoices.PlayWords}, from the image pool. Space does the same."
                        : $"Play a new layout every {SurpriseChoices.PlayWords}. Space does the same."));

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoPlayButton, playing ? $"Pause, {text}" : text);
            ToolTipService.SetToolTip(AutoPlayButton, playTip);
            PaintSelected(AutoPlayButton, AutoPlayText, AutoPlayIcon, playing);

            if (StoryDeckButton != null)
            {
                bool isDrawerOpen = _trayCard != null;
                if (isDrawerOpen)
                {
                    if (storyboardMode)
                    {
                        PaintSelected(StoryDeckButton, StoryDeckText, StoryDeckIcon, true);
                    }
                    else
                    {
                        PaintSelected(StoryDeckButton, StoryDeckText, StoryDeckIcon, false);
                        HoldFlyoutSource(StoryDeckButton, true);
                    }
                }
                else
                {
                    HoldFlyoutSource(StoryDeckButton, false);
                    PaintSelected(StoryDeckButton, StoryDeckText, StoryDeckIcon, storyboardMode);
                }

                string deckTip = hasScenes
                    ? (storyboardMode
                        ? $"Story Deck is active ({totalScenes} scenes). Click to toggle drawer (B)."
                        : $"Story Deck ({totalScenes} scenes). Click to toggle drawer (B).")
                    : "Story Deck: Manage image pool and storyboard scenes. Click to toggle drawer (B).";
                ToolTipService.SetToolTip(StoryDeckButton, deckTip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StoryDeckButton,
                    storyboardMode ? $"{StoryDeckText?.Text ?? "Story Deck"}, selected" : (StoryDeckText?.Text ?? "Story Deck"));
            }

            PaintSelected(SurpriseButton, SurpriseText, SurpriseIcon, !storyboardMode);
            if (SurpriseButton != null)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SurpriseButton, !storyboardMode ? "Auto-Mix, selected" : "Auto-Mix");
                ToolTipService.SetToolTip(SurpriseButton, storyboardMode
                    ? "Switch playback to Auto-Mix, and show one new layout now."
                    : "Auto-Mix is active. Click for one new layout now.");
            }
            if (PrevLayoutButton != null)
            {
                ToolTipService.SetToolTip(PrevLayoutButton, storyboardMode ? "Previous scene (Left Arrow)" : "Previous layout (Left Arrow). Pauses play.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrevLayoutButton, storyboardMode ? "Previous scene" : "Previous layout");
            }
            if (NextLayoutButton != null)
            {
                ToolTipService.SetToolTip(NextLayoutButton, storyboardMode ? "Next scene (Right Arrow)" : "Next layout (Right Arrow). Forward, or one new layout.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextLayoutButton, storyboardMode ? "Next scene" : "Next layout");
            }
            ShowCardSong();
            ShowHudAndOverlays();
        }

        private string? _surpriseSong;

        private void ShowCardSong()
        {
            if (_cardSongText == null) return;
            var music = MainWindow.Instance?.MyMusic;
            _cardSongText.Text = _surpriseMusic?.IsFolder == true
                ? _surpriseSong ?? "Looking for songs"
                : music?.HasFolder != true ? "No music folder" : !music.InSurprise ? "Music is off" : _musicProblem ?? "Current track";
            // The message over the picture would be behind this card.
            // A long song name is cut short here, so the whole of it goes there too.
            ToolTipService.SetToolTip(_cardSongText, _musicProblem != null && _surpriseMusic?.IsFolder != true ? _musicProblemWhy : _surpriseSong);
            bool canStep = music?.HasFolder == true && (music.InSurprise != false);
            if (_cardPreviousSong != null)
            {
                _cardPreviousSong.IsEnabled = canStep;
                _cardPreviousSong.Opacity = canStep ? 1.0 : 0.45;
                ToolTipService.SetToolTip(_cardPreviousSong, canStep ? "Previous track" : "Set a music folder in Settings to play soundtrack");
            }
            if (_cardNextSong != null)
            {
                _cardNextSong.IsEnabled = canStep;
                _cardNextSong.Opacity = canStep ? 1.0 : 0.45;
                ToolTipService.SetToolTip(_cardNextSong, canStep ? "Next track" : "Set a music folder in Settings to play soundtrack");
            }
        }

        private void PrevLayoutButton_Click(object sender, RoutedEventArgs e) => StepLayout(back: true);

        private void NextLayoutButton_Click(object sender, RoutedEventArgs e) => StepLayout(back: false);

        private void AutoPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_autoPlaySeconds > 0)
            {
                StopAutoPlay();
            }
            else
            {
                StartChosenPlay();
            }
        }

        /// <summary>
        /// Space plays and pauses. Esc pauses, and otherwise is the usual Back.
        /// In Show, Left and Right move between scenes. In Shuffle, they move between layouts once Surprise me has made some.
        /// A photo opened on its own keeps Space and the arrows for itself.
        /// </summary>
        private bool HandleAutoPlayKey(Windows.System.VirtualKey key)
        {
            if (_autoPlaySeconds > 0 && key is Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Space)
            {
                StopAutoPlay();
                return true;
            }
            if (IsSoloActive) return false;
            if (key == Windows.System.VirtualKey.Space)
            {
                StartChosenPlay();
                return true;
            }
            if (PlayUsesStoryboard() && key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
            {
                _ = StepStoryboardScene(back: key == Windows.System.VirtualKey.Left);
                return true;
            }
            if (_surpriseRun && key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
            {
                StepLayout(back: key == Windows.System.VirtualKey.Left);
                return true;
            }
            return false;
        }

        private Border? _autoPlayToast;
        private Storyboard? _autoPlayToastFade;

        /// <summary>The bar hides during a show, so a change of state also says so over the picture for a moment.</summary>
        private void ShowAutoPlayToast(string text)
        {
            _autoPlayToastFade?.Stop();
            if (_autoPlayToast != null) RootGrid.Children.Remove(_autoPlayToast);
            var toast = new Border
            {
                Child = new TextBlock { Text = text, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 15 },
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xD8, 0x18, 0x18, 0x18)),
                Padding = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 0, 0),
                IsHitTestVisible = false
            };
            _autoPlayToast = toast;
            AddOverCanvas(toast, 6200);
            var fade = new DoubleAnimation { From = 1, To = 0, BeginTime = TimeSpan.FromSeconds(1.6), Duration = TimeSpan.FromMilliseconds(500) };
            Storyboard.SetTarget(fade, toast);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var story = new Storyboard();
            story.Children.Add(fade);
            story.Completed += (_, _) =>
            {
                RootGrid.Children.Remove(toast);
                if (ReferenceEquals(_autoPlayToast, toast)) _autoPlayToast = null;
            };
            _autoPlayToastFade = story;
            story.Begin();
        }

        private Cinematic.MusicBed? _surpriseMusic;
        private string? _musicProblem;
        private string? _musicProblemWhy;

        private void StartAutoPlayMusic()
        {
            WatchMusicSettings();
            _musicProblem = null;
            var music = MainWindow.Instance?.MyMusic;
            if (music == null || !music.InSurprise || !music.HasFolder)
            {
                StopAutoPlayMusic();
                return;
            }
            if (_surpriseMusic == null)
            {
                _surpriseMusic = new Cinematic.MusicBed(DispatcherQueue);
                _surpriseMusic.GaveUp += (title, why) =>
                {
                    _musicProblem = title;
                    _musicProblemWhy = why;
                    ShowAutoPlayToast($"{title}. {why}");
                    ShowCardSong();
                };
                _surpriseMusic.NowPlayingChanged += song =>
                {
                    _surpriseSong = song;
                    ShowCardSong();
                };
            }
            _surpriseMusic.SetVolume(music.Volume);
            // Carrying on after a pause keeps the song. A changed folder or rating starts afresh.
            _surpriseMusic.PlayFolder(music.Folder!, music.LowestStars, music.IncludesUnrated);
            _surpriseMusic.SetPlaying(true);
        }

        private void StopAutoPlayMusic()
        {
            _surpriseMusic?.Stop();
        }

        /// <summary>
        /// Clear, over the canvas and under the bar and the card, so the click that pauses auto-play only pauses it and does not
        /// also reach the picture under it. It stays until that click is released, so the release cannot land on a cell either.
        /// </summary>
        private void AddAutoPlayShield()
        {
            if (_autoPlayShield != null) return;
            var shield = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            shield.PointerPressed += (_, _) =>
            {
                _autoPlayShieldPressed = true;
                if (_surpriseCard == null) StopAutoPlay();
            };
            void Released()
            {
                _autoPlayShieldPressed = false;
                if (_autoPlaySeconds == 0) RemoveAutoPlayShield();
            }
            shield.PointerReleased += (_, _) => Released();
            shield.PointerCanceled += (_, _) => Released();
            shield.PointerCaptureLost += (_, _) => Released();
            shield.Tapped += (_, e) => e.Handled = true;
            _autoPlayShield = shield;
            AddOverCanvas(shield, 5900);
        }

        private void RemoveAutoPlayShield()
        {
            if (_autoPlayShield == null) return;
            RootGrid.Children.Remove(_autoPlayShield);
            _autoPlayShield = null;
            _autoPlayShieldPressed = false;
        }

        /// <summary>Chooses Surprise me as the playback mode, and shows one new layout now. Play keeps using it.</summary>
        private async void SurpriseButton_Click(object sender, RoutedEventArgs e)
        {
            _shuffleChosen = true;
            _isStoryboardPlayback = false;
            if (_trayCard != null && _trayTab == 1)
            {
                RemoveTrayCard();
            }
            ShowAutoPlayState();
            RefreshPlayModeCard();
            await SurpriseAsync(fade: _autoPlaySeconds > 0);
        }

        /// <summary>The same accent fill Play uses while it is running. A white plate is the open-panel look, so it is not used for a mode.</summary>
        private static void PaintSelected(Button? button, TextBlock? label, IconElement? icon, bool selected)
        {
            if (button == null) return;
            if (!selected)
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
                button.ClearValue(Control.BorderThicknessProperty);
                if (label != null) label.ClearValue(TextBlock.ForegroundProperty);
                if (icon != null) icon.ClearValue(IconElement.ForegroundProperty);
                return;
            }

            button.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
            button.ClearValue(Control.BorderBrushProperty);
            button.ClearValue(Control.BorderThicknessProperty);
            var onAccent = Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
            if (label != null) label.Foreground = onAccent;
            if (icon != null) icon.Foreground = onAccent;
        }

        private async Task SurpriseAsync(bool fade)
        {
            if (_surpriseBusy) return;
            bool playing = _autoPlaySeconds > 0;
            bool sameRun = _surpriseRun;
            var pictures = SessionPictures();
            int minPics = SurpriseChoices.FewestPictures == 1 ? 1 : 2;
            if (pictures.Count < minPics)
            {
                if (!playing) AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _surpriseBusy = true;
            _quietBar = playing;
            SurpriseButton.IsEnabled = false;
            try
            {
                var inFlight = _prepareTask;
                if (inFlight != null)
                {
                    try { await inFlight; } catch { }
                }

                var options = SurpriseChoices;
                double scale = XamlRoot?.RasterizationScale ?? 1;
                double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
                var ready = DecodedBitmaps();
                Composition? composition = null;

                var prepared = _preparedSurprise;
                _preparedSurprise = null;
                if (prepared != null
                    && Math.Abs(prepared.Width - width) < 1
                    && Math.Abs(prepared.Height - height) < 1
                    && prepared.OptionsJson == options.ToJson()
                    && prepared.SessionPictures.SequenceEqual(pictures)
                    && prepared.FeaturedPictures.SetEquals(_featuredPictures))
                {
                    composition = prepared.Composition;
                    _surpriseDeck = prepared.Deck;
                    foreach (var pair in prepared.DecodedBitmaps)
                        ready.TryAdd(pair.Key, pair.Value);
                }
                else
                {
                    // The session is the whole set, tray included, so what the user removed stays out and a change to Most takes effect.
                    if (!options.TakesTurns(pictures.Count)) _surpriseDeck = null;
                    else if (_surpriseDeck == null) _surpriseDeck = new PictureDeck(PicturesInOrder(), new Random());
                    else _surpriseDeck.MatchSession(pictures);

                    var chosen = _surpriseDeck == null ? pictures : DealHand(_surpriseDeck, options);
                    // A picture already showing as failed would only put its message into the design.
                    var failed = _cellRoots.Select(root => root?.Tag as CellParts).Where(parts => parts?.Bound.Failed == true)
                        .Select(parts => parts!.Bound.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var facts = await ReadFactsAsync(chosen.Where(path => !failed.Contains(path)).ToList(), path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);
                    if (_surpriseDeck != null && options.SkipScreens) facts = LayoutComposer.LeaveOutScreensAndRepeats(facts);

                    string? avoid = sameRun ? _surpriseRecipe : null;
                    composition = await Task.Run(() => LayoutComposer.Compose(facts, width, height, Random.Shared.Next(), avoid, scale, null, options));
                }

                // The session was changed for other pictures while these were being read, and that wins.
                if (composition == null || !SessionPictures().SequenceEqual(pictures)) return;
                if (playing && _autoPlaySeconds == 0) return;

                if (options.KeepCanvas) composition.KeepCanvas(_canvasColor, _canvasSurface, _canvasFrame);
                if (!sameRun) AutoSaveSession();
                CaptureUndoPoint();
                _canvasSceneId = null;
                _cellBorderFlyout?.Hide();
                var outgoingStep = fade ? OutgoingCameraStep(0.7) : (1.0, 0.0, 0.0);
                Image? before = fade ? await CoverWithPictureOfCanvasAsync() : null;
                // The cover holds the old layout where the camera had it, and the new one starts from nothing.
                ResetCanvasCamera();
                _restoringUndo = true;
                _reuseBitmaps = ready;
                try
                {
                    ApplyLayoutFile(WithRestInTray(composition.ToLayoutFile(), pictures), restoreAspect: false);
                }
                finally
                {
                    _reuseBitmaps = null;
                    _restoringUndo = false;
                }
                _surpriseRun = true;
                _surpriseRecipe = composition.Recipe;
                // A step taken while pictures load could differ from the settled layout, and come back as a second, near-identical one.
                _settleTicket++;
                _surpriseSettling = true;
                try
                {
                    await WaitForPicturesAsync();
                }
                finally
                {
                    _surpriseSettling = false;
                }
                CaptureUndoPoint();
                if (_autoPlaySeconds > 0) _layoutSeconds = options.NextPlaySeconds(Random.Shared, composition.Cells.Count, ref _lastPlayWasLong);
                if (before != null && _autoPlaySeconds > 0) StartCanvasCamera(composition);
                if (before != null) ChangeLayoutOver(before, outgoingStep);
                if (_autoPlaySeconds > 0) StartPlaybackProgress(_layoutSeconds);
                StartPreparingNextSurprise();
            }
            finally
            {
                _surpriseBusy = false;
                _quietBar = false;
                SurpriseButton.IsEnabled = true;
            }
        }

        private void CancelPreparedSurprise()
        {
            _prepareCts?.Cancel();
            _prepareCts?.Dispose();
            _prepareCts = null;
            _prepareTask = null;
            _preparedSurprise = null;
        }

        /// <summary>Gets the next layout and its decoded pictures ready while the current one is on show, so the change does not wait on disk or face detection.</summary>
        private void StartPreparingNextSurprise()
        {
            CancelPreparedSurprise();
            var pictures = SessionPictures();
            double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
            int minPics = SurpriseChoices.FewestPictures == 1 ? 1 : 2;
            if (pictures.Count < minPics || width < 50 || height < 50) return;

            var cts = new System.Threading.CancellationTokenSource();
            _prepareCts = cts;
            _prepareTask = PrepareNextSurpriseAsync(pictures, width, height, cts.Token);
        }

        private async Task PrepareNextSurpriseAsync(List<string> pictures, double width, double height, System.Threading.CancellationToken token)
        {
            try
            {
                var options = SurpriseChoices;
                string optionsJson = options.ToJson();
                var featured = _featuredPictures.ToHashSet(StringComparer.OrdinalIgnoreCase);
                double scale = XamlRoot?.RasterizationScale ?? 1;
                string? avoid = _surpriseRecipe;

                PictureDeck? nextDeck = null;
                if (options.TakesTurns(pictures.Count))
                {
                    nextDeck = _surpriseDeck != null ? _surpriseDeck.Clone() : new PictureDeck(PicturesInOrder(), new Random());
                    nextDeck.MatchSession(pictures);
                }
                var chosen = nextDeck == null ? pictures : DealHand(nextDeck, options);
                var ready = DecodedBitmaps();
                var failed = _cellRoots.Select(root => root?.Tag as CellParts).Where(parts => parts?.Bound.Failed == true)
                    .Select(parts => parts!.Bound.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var facts = await ReadFactsAsync(chosen.Where(path => !failed.Contains(path)).ToList(), path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);
                if (token.IsCancellationRequested) return;
                if (nextDeck != null && options.SkipScreens) facts = LayoutComposer.LeaveOutScreensAndRepeats(facts);

                var composition = await Task.Run(() => LayoutComposer.Compose(facts, width, height, Random.Shared.Next(), avoid, scale, null, options), token);
                if (composition == null || token.IsCancellationRequested) return;

                var toPreload = composition.Cells.Select(c => c.Photo.Path)
                    .Where(p => !string.IsNullOrEmpty(p) && !ready.ContainsKey(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await PreloadBitmapsAsync(toPreload, ready, token);
                if (token.IsCancellationRequested) return;

                _preparedSurprise = new PreparedSurprise(composition, nextDeck, ready, pictures, featured, optionsJson, width, height);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task PreloadBitmapsAsync(List<string> paths, Dictionary<string, BitmapSource> ready, System.Threading.CancellationToken token)
        {
            foreach (string path in paths)
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask(token);
                    using var stream = await file.OpenReadAsync().AsTask(token);
                    if (token.IsCancellationRequested) return;
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(stream).AsTask(token);
                    if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                        ready[path] = bmp;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// A layout holds the hand, and every other image goes in its tray, so saving or Undo does not lose them.
        /// The canvas gets a copy. Writing the pool into the scene made its card count every photo in the session.
        /// </summary>
        private MultiViewLayoutFile WithRestInTray(MultiViewLayoutFile file, List<string> pictures)
        {
            var copy = MultiViewLayoutFile.FromJson(file.ToJson());
            if (copy == null) return file;

            int framed = file.State?.FramedSlots ?? 0;
            if (framed > 0)
                file.Photos.RemoveAll(photo => photo.Slot >= framed);

            var used = new HashSet<string>(copy.Photos.Select(p => p.Path ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var cell in copy.Freeform ?? new List<FreeformCellDto>()) used.Add(cell.Path ?? string.Empty);
            var sourceList = (_isStoryboardPlayback || _surpriseDeck == null) ? pictures : (_surpriseDeck.All);
            var rest = sourceList.Where(used.Add).ToList();
            if (rest.Count > 0) AppendToTray(copy, rest, copy.Photos.Count);
            return copy;
        }

        /// <summary>Featured pictures are always in the hand. The deck supplies the rest.</summary>
        private List<string> DealHand(PictureDeck deck, SurpriseOptions options)
        {
            int fewest = Math.Min(options.FewestPictures, options.MostPictures);
            int size;
            if (fewest == 1 && options.MostPictures > 1)
            {
                bool goSolo = !_lastHandWasSolo && Random.Shared.NextDouble() < 0.35;
                _lastHandWasSolo = goSolo;
                size = goSolo ? 1 : Random.Shared.Next(2, options.MostPictures + 1);
            }
            else
            {
                size = Random.Shared.Next(fewest, options.MostPictures + 1);
                _lastHandWasSolo = size == 1;
            }
            var hand = _featuredPictures.Where(deck.Holds).Take(size).ToList();
            foreach (string path in deck.Deal(size, Random.Shared.NextDouble() < options.TogetherChance))
            {
                if (hand.Count >= size) break;
                if (!hand.Contains(path, StringComparer.OrdinalIgnoreCase)) hand.Add(path);
            }
            return hand;
        }

        private async Task<Image?> CoverWithPictureOfCanvasAsync()
        {
            try
            {
                var shot = new RenderTargetBitmap();
                // From outside the canvas's clip: taken of the canvas itself, a zoomed layer reaching past its edge makes the picture bigger than the canvas.
                await shot.RenderAsync(this);
                var cover = new Image { Source = shot, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, IsHitTestVisible = false };
                AddOverCanvas(cover, 6000);
                return cover;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // No fade, then. The new layout still shows.
                return null;
            }
        }

        private async Task WaitForPicturesAsync()
        {
            for (int waited = 0; waited < MostWaitForPicturesMs && AnyPhotoLoading(); waited += 50)
                await Task.Delay(50);
            await Task.Delay(50);
        }

        private LayoutTransition? _lastTransition;

        /// <summary>
        /// The new layout stays covered until its pictures have loaded, so they appear together. The cover is a picture of the old layout,
        /// so each transition only has to animate that.
        /// </summary>
        private void ChangeLayoutOver(Image cover, (double Scale, double X, double Y) outgoingStep = default)
        {
            var kind = LayoutTransitions.Choose(_autoPlaySeconds > 0 ? SurpriseChoices.TransitionVariety : 0, _lastTransition, Random.Shared);
            _lastTransition = kind;
            var story = new Storyboard();
            void Animate(DependencyObject target, string property, double from, double to, int ms, int after = 0, EasingFunctionBase? ease = null)
            {
                var run = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), BeginTime = TimeSpan.FromMilliseconds(after), EasingFunction = ease };
                Storyboard.SetTarget(run, target);
                Storyboard.SetTargetProperty(run, property);
                story.Children.Add(run);
            }
            const string ScaleX = "(UIElement.RenderTransform).(CompositeTransform.ScaleX)";
            const string ScaleY = "(UIElement.RenderTransform).(CompositeTransform.ScaleY)";
            const string MoveX = "(UIElement.RenderTransform).(CompositeTransform.TranslateX)";
            const string MoveY = "(UIElement.RenderTransform).(CompositeTransform.TranslateY)";
            cover.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            cover.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform();
            var smooth = new CubicEase { EasingMode = EasingMode.EaseInOut };
            Border? dark = null;
            void ContinueOutgoing(int ms)
            {
                if (outgoingStep.Scale <= 1 && Math.Abs(outgoingStep.X) < 0.5 && Math.Abs(outgoingStep.Y) < 0.5) return;
                Animate(cover, ScaleX, 1, outgoingStep.Scale, ms);
                Animate(cover, ScaleY, 1, outgoingStep.Scale, ms);
                Animate(cover, MoveX, 0, outgoingStep.X, ms);
                Animate(cover, MoveY, 0, outgoingStep.Y, ms);
            }
            switch (kind)
            {
                case LayoutTransition.ZoomThrough:
                    Animate(cover, ScaleX, 1, 1.12, 750, ease: smooth);
                    Animate(cover, ScaleY, 1, 1.12, 750, ease: smooth);
                    Animate(cover, "Opacity", 1, 0, 750);
                    break;
                case LayoutTransition.ThroughDark:
                    dark = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A)), IsHitTestVisible = false };
                    AddOverCanvas(dark, 5990);
                    ContinueOutgoing(400);
                    Animate(cover, "Opacity", 1, 0, 400);
                    Animate(dark, "Opacity", 1, 0, 550, after: 450);
                    break;
                case LayoutTransition.Slide:
                    int way = Random.Shared.Next(3);
                    if (way == 2) Animate(cover, MoveY, 0, -RootGrid.ActualHeight, 850, ease: smooth);
                    else Animate(cover, MoveX, 0, way == 0 ? -RootGrid.ActualWidth : RootGrid.ActualWidth, 850, ease: smooth);
                    break;
                default:
                    ContinueOutgoing(700);
                    Animate(cover, "Opacity", 1, 0, 700);
                    break;
            }
            // A cover zooming or sliding reaches past the canvas, as a zoomed layer does.
            ClipRootToCanvas();
            story.Completed += (_, _) =>
            {
                RootGrid.Children.Remove(cover);
                if (dark != null) RootGrid.Children.Remove(dark);
                if (!_cameraRunning) RootGrid.ClearValue(UIElement.ClipProperty);
            };
            story.Begin();
        }

        private List<string> SessionPictures() =>
            _items.Where(i => !IsEmptyCell(i)).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>By folder and date, so pictures from one occasion sit next to each other.</summary>
        private List<string> PicturesInOrder() =>
            _items.Where(i => !IsEmptyCell(i))
                .OrderBy(i => System.IO.Path.GetDirectoryName(i.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DateModified)
                .Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>A different session has different pictures to draw from.</summary>
        private void ForgetSurprise()
        {
            CancelPreparedSurprise();
            StopAutoPlay();
            StopAutoPlayMusic();
            ResetCanvasCamera();
            _lastCameraKind = null;
            _surpriseDeck = null;
            _surpriseRun = false;
            _surpriseRecipe = null;
            _featuredPictures.Clear();
        }

        /// <summary>A picture the reader cannot open is still arranged, by the size it shows at or as a plain photo shape, rather than dropped from the session.</summary>
        private async Task<List<PhotoFacts>> ReadFactsAsync(List<string> paths, Func<string, (int Width, int Height)?> shown)
        {
            var waiting = new ConcurrentQueue<string>(paths.Where(p => !_photoFacts.ContainsKey(p)));
            if (!waiting.IsEmpty)
            {
                await Task.WhenAll(Enumerable.Range(0, FactReaders).Select(_ => Task.Run(async () =>
                {
                    var detector = await PhotoFactsReader.CreateDetectorAsync();
                    while (waiting.TryDequeue(out var path))
                    {
                        var read = await PhotoFactsReader.ReadAsync(path, detector);
                        if (read != null) _photoFacts[path] = read;
                    }
                })));
            }

            var facts = new List<PhotoFacts>();
            foreach (string path in paths)
            {
                bool featured = _featuredPictures.Contains(path);
                if (_photoFacts.TryGetValue(path, out var known))
                {
                    facts.Add(known with { Featured = featured });
                }
                else if (System.IO.File.Exists(path))
                {
                    var size = shown(path) ?? (4, 3);
                    facts.Add(new PhotoFacts(path, size.Width, size.Height, Array.Empty<UnitRect>(), 0.5, 0.42, "#FF808080", Featured: featured));
                }
            }
            return facts;
        }
    }
}

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
        /// <summary>Set while the drawer is open: its Image Pool tab counts the images off the canvas, which change with the canvas.</summary>
        private Action? _refreshPoolTab;
        private Action? _refreshStoryboardCards;
        private Action? _syncStorySceneSelection;
        /// <summary>
        /// The open drawer's way to take a scene the storyboard build added behind it. The drawer keeps its own list and
        /// writes it back to the show on any change, so a scene added only to the show was dropped by the next drawer action.
        /// </summary>
        private Action<ShowProject, ShowScene>? _sceneAddedBehindDrawer;
        /// <summary>The open drawer rebuilds its cards from the show. Null while the drawer is closed.</summary>
        private Action? _syncDrawerToShow;
        /// <summary>
        /// Images a scene is being made from right now. The storyboard build and Add new layout both take images not in the
        /// show yet; without this, one pressed during the build could take the same image as the scene the build was making.
        /// </summary>
        private readonly HashSet<string> _imagesBeingDrafted = new(StringComparer.OrdinalIgnoreCase);
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


        /// <summary>
        /// The Layout Generator settings for a show: the main ones, with every value the show holds in their place. A show
        /// made before shows held values, and not yet opened as one, still takes its overrides by name.
        /// </summary>
        private SurpriseOptions GeneratorSettingsFor(ShowProject? show) =>
            show == null ? SurpriseChoices
            : show.GlobalDefaults.HoldsValues ? show.GlobalDefaults.ApplyTo(SurpriseChoices)
            : SurpriseChoices.WithOverrides(show.GeneratorOverrides);

        /// <summary>
        /// Cinematic takes the saved Cinematic preset. Multi-Image takes the saved Multi-Image preset, or, until the user
        /// has changed one, follows the engine settings.
        /// </summary>
        private void SetCinematic(ShowProject show, bool on)
        {
            show.GeneratorOverrides = on ? CinematicOverrides.Name : null;
            show.PresetSlot = null;
            if (on)
            {
                show.PresetName = CinematicOverrides.Name;
                show.GlobalDefaults.TakeValues(SurpriseChoices.Cinematic ?? new CinematicOverrides());
            }
            else if (SurpriseChoices.MultiImage is CinematicOverrides multi)
            {
                show.PresetName = MultiImagePreset;
                show.GlobalDefaults.TakeValues(multi);
            }
            else
            {
                show.PresetName = MultiImagePreset;
                show.GlobalDefaults.ClearValues();
            }
        }

        /// <summary>
        /// Puts the preset in use back to its starting values and writes that. Multi-Image follows the engine settings
        /// again. Cinematic is a new built-in preset. A My preset is emptied, and the show goes back to Multi-Image.
        /// </summary>
        private void ResetShowPresetToDefaults(ShowProject show)
        {
            if (show.PresetSlot is int slot && slot >= 0 && slot < 3)
            {
                ChangeSurpriseChoices(o =>
                {
                    while (o.MyPresets.Count < 3) o.MyPresets.Add(null);
                    o.MyPresets[slot] = null;
                });
                SetCinematic(show, false);
            }
            else if (show.GeneratorOverrides == CinematicOverrides.Name || show.PresetName == CinematicOverrides.Name)
            {
                var factory = CinematicOverrides.Checked(new CinematicOverrides());
                ChangeSurpriseChoices(o => o.Cinematic = factory);
                show.PresetSlot = null;
                show.PresetName = CinematicOverrides.Name;
                show.GeneratorOverrides = CinematicOverrides.Name;
                show.GlobalDefaults.TakeValues(factory);
            }
            else if (show.PresetName == "Custom")
            {
                SetCinematic(show, false);
            }
            else
            {
                ChangeSurpriseChoices(o => o.MultiImage = null);
                SetCinematic(show, false);
            }
            CancelPreparedSurprise();
            DropScenesDraftedAhead();
        }

        /// <summary>The show's values are this preset's values, written with the other app settings.</summary>
        private void SaveShowPreset(ShowProject show)
        {
            var snapshot = CinematicOverrides.From(GeneratorSettingsFor(show));
            ChangeSurpriseChoices(o =>
            {
                if (show.PresetSlot is int slot && slot >= 0 && slot < 3)
                {
                    while (o.MyPresets.Count < 3) o.MyPresets.Add(null);
                    o.MyPresets[slot] = snapshot;
                }
                else if (show.GeneratorOverrides == CinematicOverrides.Name || show.PresetName == CinematicOverrides.Name)
                    o.Cinematic = snapshot;
                else
                    o.MultiImage = snapshot;
            });
        }

        /// <summary>
        /// A show just read from a file. One saved before shows held values, made as a Cinematic storyboard, takes the
        /// app's cinematic values once, now; its scenes, holds and camera paths are not touched, and the file is rewritten
        /// only when the user saves.
        /// </summary>
        private void AdoptOpenedShow(ShowProject show) => show.AdoptOnOpen(SurpriseChoices.Cinematic ?? new CinematicOverrides());

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
            // A smaller window must not leave a tall drawer over the playbar.
            _trayDrawerHeight = Math.Min(_trayDrawerHeight, MaxDrawerHeight());
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

            // The playbar always sits above the drawer. The drawer cannot grow past it (MaxDrawerHeight),
            // because Back, Play and Save/Load must stay in reach however the drawer is sized.
            UnifiedHud.Margin = new Thickness(0, 0, 0, drawerHeight + 16);
            UnifiedHud.Opacity = 1.0;
            UnifiedHud.IsHitTestVisible = true;
        }

        /// <summary>The tallest drawer that still leaves the playbar, and a strip of canvas, above it.</summary>
        private double MaxDrawerHeight()
        {
            double bar = UnifiedHud.ActualHeight > 0 ? UnifiedHud.ActualHeight : 48;
            return Math.Max(SingleRowDrawerHeight, RootGrid.ActualHeight - bar - 16 - 40);
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

            // Runs once input has been still for 350 ms: a drag is one change, not one per step.
            Action SettledAction(Action apply)
            {
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(350);
                timer.IsRepeating = false;
                timer.Tick += (_, _) => apply();
                return () => { timer.Stop(); timer.Start(); };
            }

            Slider Slide(StackPanel column, string label, string tip, double min, double max, double step, double value, Func<double, string> format, Action<SurpriseOptions, double> chosen)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.None };

                // The readout fits the longest word a slider shows ("Crossfade"); at 62 it was cut off.
                var rightGrid = new Grid { Width = 230, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
                rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

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
                    Width = 146,
                    Foreground = winBlue,
                    VerticalAlignment = VerticalAlignment.Center
                };
                slider.Resources["SliderThumbBackground"] = winBlue;
                slider.Resources["SliderThumbBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x70, 0xD0, 0xFF));
                slider.Resources["SliderThumbBackgroundPressed"] = winBlue;
                slider.Resources["SliderTrackValueFill"] = winBlue;
                slider.Resources["SliderTrackValueFillPointerOver"] = winBlue;
                slider.Resources["SliderTrackValueFillPressed"] = winBlue;
                // The readout follows the drag; the setting is written once the drag settles, as on the Show card, not on
                // every step of it (each write saved the settings file and dropped the layout being prepared).
                double pending = value;
                var save = SettledAction(() => ChangeSurpriseChoices(o => chosen(o, pending)));
                slider.ValueChanged += (_, e) =>
                {
                    pending = e.NewValue;
                    readout.Text = format(e.NewValue);
                    save();
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

            // The standard control for a setting that varies: two thumbs, and each layout picks within them. Ends together
            // is one value.
            void RangeRow(StackPanel column, string label, string tip, double min, double max, double step, double low, double high,
                Func<double, double, string> format, Action<SurpriseOptions, double, double> chosen, Action? changed = null)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
                var right = new Grid { Width = 230, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
                right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                double Snap(double v) => Math.Clamp(Math.Round(v / step) * step, min, max);
                low = Snap(low);
                high = Math.Max(low, Snap(high));
                var readout = new TextBlock { Text = format(low, high), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, Opacity = 0.82, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
                var range = new RangeSlider { Minimum = min, Maximum = max, StepFrequency = step, RangeStart = low, RangeEnd = high, Width = 146, VerticalAlignment = VerticalAlignment.Center };
                double pendingA = low, pendingB = high;
                var save = SettledAction(() => { ChangeSurpriseChoices(o => chosen(o, pendingA, pendingB)); changed?.Invoke(); });
                range.RangeChanged += (_, r) =>
                {
                    pendingA = Snap(r.Start);
                    pendingB = Math.Max(pendingA, Snap(r.End));
                    readout.Text = format(pendingA, pendingB);
                    save();
                };
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(range, label);
                Grid.SetColumn(range, 0);
                Grid.SetColumn(readout, 1);
                right.Children.Add(range);
                right.Children.Add(readout);
                Grid.SetColumn(right, 1);
                row.Children.Add(text);
                row.Children.Add(right);
                column.Children.Add(row);
            }

            static string Percents(double a, double b) => b < 0.5 ? "Off" : Math.Abs(a - b) < 0.5 ? $"{a:0}%" : $"{a:0}% - {b:0}%";
            static string Times(double a, double b) => b <= 1.01 ? "Off" : Math.Abs(a - b) < 0.01 ? $"{a:0.##}×" : $"{a:0.##}× - {b:0.##}×";

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

            // "More settings" for a section: a gear and a label, the same everywhere.
            Button MoreButton(string text, string name, string tip, RoutedEventHandler pressed)
            {
                var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                stack.Children.Add(Icon("\uE713", 12));
                stack.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var button = WordButton(text, name, tip, pressed);
                button.Content = stack;
                return button;
            }

            // A section's heading, with its "more settings" button on the right.
            Grid SectionHeading(string text, Button more)
            {
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var heading = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(more, 1);
                row.Children.Add(heading);
                row.Children.Add(more);
                return row;
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
                Text = "Images per scene",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            };
            Grid.SetColumn(imagesLabel, 0);
            Grid.SetColumn(imageControlsPanel, 1);

            imagesRow.Children.Add(imagesLabel);
            imagesRow.Children.Add(imageControlsPanel);

            if (!_showingSurpriseAdvanced)
            {
                // The Show card (R17): what Play plays. The director makes every scene from this show's values and keeps it in
                // the storyboard; the engine works behind it, its settings behind one link.
                var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = "Show", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                header.Children.Add(title);
                var engineLink = MoreButton("Engine settings\u2026", "Engine settings",
                    "How the engine makes one layout and one move: images and faces, designs, camera, transitions, canvas and progress.",
                    (_, _) => { _showingSurpriseAdvanced = true; ShowSurpriseCard(); });
                Grid.SetColumn(engineLink, 1);
                header.Children.Add(engineLink);
                body.Children.Add(header);
                body.Children.Add(new TextBlock
                {
                    Text = "Play plays this show: each scene is made from its values and kept, so you can step back to it in the drawer.",
                    FontSize = 11,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, -4, 0, 2)
                });

                var showPanel = new StackPanel { Spacing = 4 };
                body.Children.Add(showPanel);
                FillShowSettings(showPanel, () => ShowAutoPlayState());

                body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Opacity = 0.15, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0, 6, 0, 4) });
                // The music library's own settings (folder, ratings, which shows play music) open from here, so
                // everything about the soundtrack is in this one place.
                body.Children.Add(SectionHeading("Soundtrack", MoreButton("Configure\u2026", "Configure music",
                    "The music folder, the lowest rating played, and whether Play uses music.",
                    (_, _) => MainWindow.Instance?.OpenMusicSettings())));
                body.Children.Add(MusicRow(Icon, IconButton));

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
                ToolTipService.SetToolTip(backBtn, "Back to the show");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(backBtn, "Back to the show");
                backBtn.Click += (_, _) => { _showingSurpriseAdvanced = false; ShowSurpriseCard(); };
                var advTitle = new TextBlock { Text = "Engine settings", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
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
                string zoomText = SurpriseChoices.MostZoom <= 1.01 || SurpriseChoices.ZoomChance <= 0.001
                    ? "Zoom off"
                    : $"Zoom {Times(SurpriseChoices.LeastZoom, SurpriseChoices.MostZoom)}";
                return $"{rangeText} \u00B7 {zoomText}";
            }
            var (imagesSection, imagesSummary) = Section("Images & faces", ImagesSummaryText());
            updateImagesSummary = () => imagesSummary.Text = ImagesSummaryText();

            // The same number boxes as the first page (which is not shown while this one is), not a second kind of control.
            imagesSection.Children.Add(imagesRow);

            Slide(imagesSection, "Same folder and date", "The odds that a click takes images that are neighbours when sorted by folder, then file date.",
                0, 100, 5, options.TogetherChance * 100,
                v => $"{v:0}%",
                (o, v) => o.TogetherChance = v / 100);
            Toggle(imagesSection, "Skip screens and repeats", "Leave out what looks like a screen capture or a graphic, and all but one of near-identical shots.",
                options.SkipScreens, (o, on) => o.SkipScreens = on);
            RangeRow(imagesSection, "Zoom", "How far an image is zoomed into its frame when a layout is made, toward its faces or, with none found, its focus point. Each zoomed image picks within the range. It stops short of cutting a face, and of showing the photo past twice its own pixels, so it stays sharp. 1x is off.",
                1, SurpriseOptions.MostOfAnyZoom, 0.25, options.LeastZoom, options.MostZoom, Times,
                (o, a, b) => { o.LeastZoom = a; o.MostZoom = b; }, () => updateImagesSummary());
            Slide(imagesSection, "Zoom frequency", "The odds that an image is zoomed, with or without faces.",
                0, 100, 5, options.ZoomChance * 100,
                v => v < 1 ? "Off" : $"{v:0}%",
                (o, v) => { o.ZoomChance = v / 100; updateImagesSummary(); });
            Slide(imagesSection, "Face kept in frame, at least", "How much of a face a layout must keep in its frame. Lower lets a layout crop faces more boldly.",
                50, 100, 5, options.FaceShown * 100,
                v => $"{v:0}%",
                (o, v) => o.FaceShown = v / 100);
            Slide(imagesSection, "Crop at most", "How much of an image a layout of one image may crop away to fill its frame, measured against the frame's shape. Past it, the image is shown whole where the design can, and a design that can only crop sits that layout out.",
                0, 100, 5, options.CropAtMost * 100,
                v => $"{v:0}%",
                (o, v) => o.CropAtMost = v / 100);

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
            // Real design names only: these named designs that no longer exist, so Clean grids turned every design off.
            AddDesignPreset("All", $"All {LayoutComposer.Recipes.Length} designs", LayoutComposer.Recipes.ToArray());
            AddDesignPreset("Clean grids", "Straight grids only: Edge to edge and Gallery wall. No tilted prints, slants or circles.",
                LayoutComposer.EdgeToEdge, LayoutComposer.GalleryWall);
            AddDesignPreset("Framed", "Framed layouts only: Gallery wall and Hero spotlight.",
                LayoutComposer.GalleryWall, LayoutComposer.HeroSpotlight);
            AddDesignPreset("Scrapbook", "Playful layouts: Scattered prints, Circles and Gallery wall.",
                LayoutComposer.ScatteredPrints, LayoutComposer.Circles, LayoutComposer.GalleryWall);
            designsSection.Children.Add(stylePresetPanel);

            string FormatDesignStep(double v) =>
                v <= -1.1 ? "Off" :
                v < -0.6 ? "Rare" :
                v < -0.15 ? "Less" :
                v <= 0.15 ? "Normal" :
                v <= 0.6 ? "More" : "Often";

            // Shared by every design that has the thing, shown once above the designs (design 3.3).
            {
                var frameColor = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Right, ItemsSource = SurpriseOptions.Cards.ToList(), SelectedItem = options.Card };
                frameColor.SelectionChanged += (_, _) =>
                {
                    if (frameColor.SelectedItem is string name) ChangeSurpriseChoices(o => o.Card = name);
                };
                Row(designsSection, "Frame color", "The color of every frame: Gallery wall's card, a hero's outline, a print's border, a circle's outline. Any: each layout picks white, black or a tone from its images.", frameColor);
                RangeRow(designsSection, "Frame width", "How wide every frame is: Gallery wall's card and gaps, a hero's outline, a print's border, a circle's outline. Each layout picks a width in the range; drag the ends together for one width. 1x is the usual width.",
                    SurpriseOptions.NarrowestFrame, SurpriseOptions.WidestFrame, 0.25, options.GapScale, options.GapScaleLargest,
                    (a, b) => Math.Abs(a - b) < 0.01 ? $"{a:0.##}×" : $"{a:0.##}× - {b:0.##}×",
                    (o, a, b) => { o.GapScale = a; o.GapScaleLargest = Math.Max(a, b); });
                void BackdropSlide(string name, string tip, double odds, Action<SurpriseOptions, double> set)
                {
                    double initial = odds <= 0 ? -1.2 : Math.Log(Math.Max(SurpriseOptions.RarestDesign, odds), DesignOddsBase);
                    Slide(designsSection, name, tip, -1.2, 1.0, 0.1, initial, FormatDesignStep,
                        (o, v) => set(o, v <= -1.1 ? 0 : Math.Pow(DesignOddsBase, v)));
                }
                BackdropSlide("Backdrop: vignette", "How often a backdrop fades from the middle to the edges. The backdrop is what shows round an image shown whole, a hero, prints and circles. Far left turns it off.",
                    options.BackdropVignetteOdds, (o, v) => o.BackdropVignetteOdds = v);
                BackdropSlide("Backdrop: gradient", "How often a backdrop fades from corner to corner. Far left turns it off.",
                    options.BackdropGradientOdds, (o, v) => o.BackdropGradientOdds = v);
                BackdropSlide("Backdrop: solid", "How often a backdrop is one flat colour. Far left turns it off.",
                    options.BackdropSolidOdds, (o, v) => o.BackdropSolidOdds = v);
                BackdropSlide("Backdrop: ambient blur", "How often a backdrop is the layout's largest picture, darkened and blurred, as Cinematic Slideshow shows a portrait. Far left turns it off.",
                    options.BackdropAmbientBlurOdds, (o, v) => o.BackdropAmbientBlurOdds = v);
            }

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

                // A design's own settings sit under it, set in, named for what they change on screen.
                StackPanel Detail()
                {
                    var panel = new StackPanel { Margin = new Thickness(16, 0, 0, 6) };
                    designsSection.Children.Add(panel);
                    return panel;
                }
                if (design == LayoutComposer.HeroSpotlight)
                {
                    // The same range control as every other range, in percent of the canvas height.
                    RangeRow(Detail(), "Hero size", "The height of the hero image against the canvas. Each Hero spotlight layout picks a height in this range; drag the ends together for one size.",
                        SurpriseOptions.HeroLowest, SurpriseOptions.HeroHighest, 0.01, options.HeroSmallest, options.HeroLargest,
                        (a, b) => Math.Abs(a - b) < 0.005 ? $"{a * 100:0}%" : $"{a * 100:0}% - {b * 100:0}%",
                        (o, a, b) => { o.HeroSmallest = a; o.HeroLargest = Math.Max(a, b); });
                }
                else if (design == LayoutComposer.ScatteredPrints)
                {
                    RangeRow(Detail(), "Print tilt", "How far a print turns, either way. Each print turns within the range; drag the ends together for one angle, both to 0 for straight prints.",
                        0, SurpriseOptions.MostTiltAllowed, 0.5, options.LeastTilt, options.MostTilt,
                        (a, b) => b < 0.1 ? "0°" : Math.Abs(a - b) < 0.05 ? $"{a:0.#}°" : $"{a:0.#}° - {b:0.#}°",
                        (o, a, b) => { o.LeastTilt = a; o.MostTilt = Math.Max(a, b); });
                }
            }
            // For every design, so it sits with them and not under Single images.
            Slide(designsSection, "Pick from the best", "Each time, the generator makes several layouts and scores them. This is how many of the best it picks from: Best always takes the top one, more gives more variety.",
                1, 12, 1, options.Variety,
                v => (int)v == 1 ? "Best" : $"Top {(int)v}",
                (o, v) => o.Variety = (int)v);


            // 3. Camera: how it moves within a scene. 4. Transitions: how one scene changes to the next.
            string CameraSummaryText() => SurpriseChoices.CameraMode;
            var (cameraSection, cameraSummary) = Section("Camera", CameraSummaryText());
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

            RangeRow(cameraSection, "Camera zoom", "How far the camera zooms in or out while a layout plays, with or without faces. Each zoom picks within the range, and so does a two-shot's close-up.",
                SurpriseOptions.LeastCameraZoom, SurpriseOptions.MostCameraZoom, 0.05, options.CameraZoomLeast, options.CameraZoomMost, Times,
                (o, a, b) => { o.CameraZoomLeast = a; o.CameraZoomMost = b; });

            Slide(cameraSection, "Aims at faces", "The odds that the camera heads toward a large image with faces in it.",
                0, 100, 5, options.MotionFaceChance * 100,
                v => $"{v:0}%",
                (o, v) => o.MotionFaceChance = v / 100);
            MovementGroup(cameraSection);

            string TransitionSummaryText() => $"{SurpriseChoices.TransitionShares()[LayoutTransition.Crossfade] * 100:0}% crossfade";
            var (transitionSection, transitionSummary) = Section("Transitions", TransitionSummaryText());
            TransitionGroup(transitionSection, () => transitionSummary.Text = TransitionSummaryText());

            // 5. Canvas & progress: what applies to every design. Each design's own settings are under it in Designs.
            string LookSummaryText() => SurpriseChoices.ProgressOn ? $"Progress {SurpriseChoices.ProgressEdge.ToLowerInvariant()}" : "Progress off";
            var (lookSection, lookSummary) = Section("Canvas & progress", LookSummaryText());
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
            AddLookChoice(lookSection, "Progress edge","Which side of the screen the progress line sits on. It fills from the left, or from the top on a side edge.", edgePanel);

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
            AddLookChoice(lookSection, "Progress color","The color of the progress line and its glow. Accent follows the Windows accent color.", colorPanel);

            // A line of explanation under a setting whose name does not say enough on its own.
            void Note(StackPanel column, string text) => column.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -4, 0, 6)
            });

            // How the camera moves within a scene: two shots where there is a face, the curve each move eases by (odds, as
            // the designs), how far a pan's path bends, and how far the view turns. Each varies within its range.
            void MovementGroup(StackPanel column)
            {
                Slide(column, "Two-shot scenes", "The chance that a scene with a face, held 8 seconds or more, is two shots.",
                    0, 100, 5, options.TwoShotChance * 100,
                    v => v < 1 ? "Off" : $"{v:0}%",
                    (o, v) => o.TwoShotChance = v / 100);
                Note(column, "A wide shot, then a snap zoom or a cut to the face, as Cinematic Slideshow does. Only with the camera on Auto, in a scene held 8 seconds or more.");
                Slide(column, "Still scenes", "The chance that a scene does not move at all.",
                    0, 100, 5, options.StillChance * 100,
                    v => v < 1 ? "Off" : $"{v:0}%",
                    (o, v) => o.StillChance = v / 100);
                Toggle(column, "Log Play timing", "Temporary, for measuring: Play writes how long it takes to show each scene to play-timing.log in the app's folder. Timings only, no file names.",
                    options.LogPlayTiming, (o, v) => o.LogPlayTiming = v);

                column.Children.Add(new TextBlock
                {
                    Text = "Movement curves",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 10, 0, 2)
                });
                Note(column, "How each move gets going and comes to rest. Snap is used on zooms only. All off is Smooth.");
                var mix = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
                void ShowMix()
                {
                    var shares = CameraCurves.Shares(SurpriseChoices.CurveOdds).Zooms;
                    mix.Text = "Mix: " + string.Join(" · ", CameraCurves.All
                        .Where(c => shares[c] >= 0.005)
                        .Select(c => $"{c.ToString().ToLowerInvariant()} {shares[c] * 100:0}%"));
                }
                void Curve(string name, string tip, double odds, Action<SurpriseOptions, double> set)
                {
                    double initial = odds <= 0 ? -1.2 : Math.Log(Math.Max(SurpriseOptions.RarestDesign, odds), DesignOddsBase);
                    Slide(column, name, tip + " Far left turns it off.", -1.2, 1.0, 0.1, initial, FormatDesignStep,
                        (o, v) => { set(o, v <= -1.1 ? 0 : Math.Pow(DesignOddsBase, v)); ShowMix(); });
                }
                Curve("Steady", "The same speed throughout, so it is still moving through a crossfade.", options.CurveSteadyOdds, (o, v) => o.CurveSteadyOdds = v);
                Curve("Gentle", "The mildest start and stop.", options.CurveGentleOdds, (o, v) => o.CurveGentleOdds = v);
                Curve("Smooth", "A soft start and stop: how every move eased before there was a choice.", options.CurveSmoothOdds, (o, v) => o.CurveSmoothOdds = v);
                Curve("Cinematic", "Lingers at both ends and moves quicker in the middle, as Cinematic Slideshow does.", options.CurveCinematicOdds, (o, v) => o.CurveCinematicOdds = v);
                Curve("Settle", "Under way at once, then glides to rest.", options.CurveSettleOdds, (o, v) => o.CurveSettleOdds = v);
                Curve("Snap", "Slow, a quick push, then a long settle: Cinematic Slideshow's snap zoom. Zooms only.", options.CurveSnapOdds, (o, v) => o.CurveSnapOdds = v);
                column.Children.Add(mix);
                ShowMix();

                RangeRow(column, "Curved path", "How far a pan's path bows to one side, up to Cinematic Slideshow's bend. Each pan picks within the range.",
                    0, 100, 5, options.PathBendLeast * 100, options.PathBendMost * 100, Percents,
                    (o, a, b) => { o.PathBendLeast = a / 100; o.PathBendMost = b / 100; });
                RangeRow(column, "Rotation", "How far the view turns over a move, in degrees. Each move picks within the range; it zooms in just enough to keep every corner covered.",
                    0, SurpriseOptions.MostTurn, 0.5, options.TurnLeast, options.TurnMost,
                    (a, b) => b < 0.25 ? "Off" : Math.Abs(a - b) < 0.25 ? $"{a:0.#}°" : $"{a:0.#}° - {b:0.#}°",
                    (o, a, b) => { o.TurnLeast = a; o.TurnMost = b; });
            }

            // How a change of scene is made: one odds slider per kind, on the designs' Off to Often scale, and a line
            // giving the mix they make, so the words on the sliders never have to stand for a percentage.
            void TransitionGroup(StackPanel column, Action changed)
            {
                Note(column, "How one layout or scene changes to the next. The same kind never comes twice in a row, except a crossfade. All off is a crossfade.");
                var mix = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
                void ShowMix()
                {
                    var shares = SurpriseChoices.TransitionShares();
                    string Part(LayoutTransition kind, string name) => shares[kind] >= 0.005 ? $"{name} {shares[kind] * 100:0}%" : "";
                    mix.Text = "Mix: " + string.Join(" · ", new[]
                    {
                        Part(LayoutTransition.Crossfade, "crossfade"),
                        Part(LayoutTransition.ZoomThrough, "zoom through"),
                        Part(LayoutTransition.ThroughDark, "fade through black"),
                        Part(LayoutTransition.Slide, "slide")
                    }.Where(p => p.Length > 0));
                }
                void Kind(string name, string tip, double odds, Action<SurpriseOptions, double> set)
                {
                    double initial = odds <= 0 ? -1.2 : Math.Log(Math.Max(SurpriseOptions.RarestDesign, odds), DesignOddsBase);
                    Slide(column, name, tip + " Far left turns it off.", -1.2, 1.0, 0.1, initial, FormatDesignStep,
                        (o, v) => { set(o, v <= -1.1 ? 0 : Math.Pow(DesignOddsBase, v)); ShowMix(); changed(); });
                }
                Kind("Crossfade", "One scene dissolves into the next.", options.CrossfadeOdds, (o, v) => o.CrossfadeOdds = v);
                Kind("Zoom through", "The camera pushes through into the next scene.", options.ZoomThroughOdds, (o, v) => o.ZoomThroughOdds = v);
                Kind("Fade through black", "Out to black, then in to the next scene.", options.ThroughDarkOdds, (o, v) => o.ThroughDarkOdds = v);
                Kind("Slide", "The next scene slides in from the side.", options.SlideOdds, (o, v) => o.SlideOdds = v);
                column.Children.Add(mix);
                ShowMix();
            }

            // The Cinematic storyboard's values are the show's own now, edited on the storyboard (MultiViewControl.ShowSettings.cs).

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
            volRightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
            volRightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

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
                Width = 146,
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

            // Cut on the beat (design 3.10): scene changes land on the song's beats. With nothing read it changes nothing.
            var beatRow = new Grid { ColumnSpacing = 12, MinHeight = 34 };
            beatRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            beatRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            var beatLabel = new TextBlock { Text = "Cut on the beat", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
            var beatToggle = new ToggleSwitch
            {
                IsOn = music?.CutOnBeat != false,
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 0,
                IsEnabled = isMusicActive,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(beatToggle, "Cut on the beat");
            ToolTipService.SetToolTip(beatRow, "Each scene changes on the first beat of a bar nearest its planned end, within Duration, or on the nearest beat when the bars are not clear. A song without a clear beat, or one still being read, leaves the timing as it is. The show itself is not changed.");
            beatToggle.Toggled += (_, _) => MainWindow.Instance?.SetMusicCutOnBeat(beatToggle.IsOn);
            Grid.SetColumn(beatLabel, 0);
            Grid.SetColumn(beatToggle, 1);
            beatRow.Children.Add(beatLabel);
            beatRow.Children.Add(beatToggle);
            panel.Children.Add(beatRow);

            // Follow the music: loud passages hold toward Duration's shortest, quiet ones toward its longest.
            var followRow = new Grid { ColumnSpacing = 12, MinHeight = 34 };
            followRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            followRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            var followLabel = new TextBlock { Text = "Follow the music", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
            var followRight = new Grid { Width = 230, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            followRight.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
            followRight.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            string FollowText(double v) => v < 0.5 ? "Off" : $"{v:0}%";
            var followReadout = new TextBlock { Text = FollowText(music?.Follow ?? 50), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, Opacity = isMusicActive ? 0.82 : 0.4, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
            var followSlider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = music?.Follow ?? 50, Width = 146, VerticalAlignment = VerticalAlignment.Center, IsEnabled = isMusicActive };
            var saveFollow = DispatcherQueue.CreateTimer();
            saveFollow.Interval = TimeSpan.FromMilliseconds(350);
            saveFollow.IsRepeating = false;
            saveFollow.Tick += (_, _) => MainWindow.Instance?.SetMusicFollow(followSlider.Value);
            followSlider.ValueChanged += (_, e) =>
            {
                followReadout.Text = FollowText(e.NewValue);
                saveFollow.Stop();
                saveFollow.Start();
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(followSlider, "Follow the music");
            ToolTipService.SetToolTip(followRow, "How far scene lengths follow the song: loud, busy passages hold toward Duration's shortest, quiet ones toward its longest. Off keeps the planned lengths.");
            Grid.SetColumn(followSlider, 0);
            Grid.SetColumn(followReadout, 1);
            followRight.Children.Add(followSlider);
            followRight.Children.Add(followReadout);
            Grid.SetColumn(followLabel, 0);
            Grid.SetColumn(followRight, 1);
            followRow.Children.Add(followLabel);
            followRow.Children.Add(followRight);
            panel.Children.Add(followRow);

            onToggle.Toggled += (_, _) =>
            {
                MainWindow.Instance?.SetMusicInSurprise(onToggle.IsOn);
                volSlider.IsEnabled = hasFolder && onToggle.IsOn;
                beatToggle.IsEnabled = hasFolder && onToggle.IsOn;
                followSlider.IsEnabled = hasFolder && onToggle.IsOn;
                followReadout.Opacity = onToggle.IsOn ? 0.82 : 0.4;
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

        /// <summary>Play plays the show whenever it has scenes (R17); with none yet, Play makes them as it plays.</summary>
        private bool PlayUsesStoryboard() => _activeShow != null && _activeShow.Scenes.Count > 0;

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

        private const string SceneRemovedToast = "Removed scene. Ctrl+Z brings it back.";

        /// <summary>
        /// The camera choices for a scene, used by its time badge and by its right-click menu, so the two cannot drift apart.
        /// The current choice is a radio item, as the sort menu's is.
        /// </summary>
        private static void FillCameraMenu(IList<MenuFlyoutItemBase> items, ShowScene scene, Action changed, Action editPath)
        {
            // The badge's label already reads the kind, or the geometry when no kind was stored.
            string label = GetMotionLabel(scene);
            string current = scene.CameraOverride == null ? "Dynamic"
                : label.Contains("Zoom in") ? "ZoomIn"
                : label.Contains("Zoom out") ? "ZoomOut"
                : label.Contains("Pan") ? "Pan"
                : label.Contains("Still") ? "Still" : "";
            const string group = "SceneCamera";

            void Add(string text, string kind, Func<CameraKeyframeOverride?> make, string? tip = null)
            {
                var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = current == kind };
                if (tip != null) ToolTipService.SetToolTip(item, tip);
                item.Click += (_, _) => { scene.CameraOverride = make(); changed(); };
                items.Add(item);
            }

            Add("Zoom in", "ZoomIn", () => new CameraKeyframeOverride(new CameraViewDto(1.0, 0.5, 0.5), new CameraViewDto(1.25, 0.5, 0.5), "ZoomIn"));
            Add("Zoom out", "ZoomOut", () => new CameraKeyframeOverride(new CameraViewDto(1.25, 0.5, 0.5), new CameraViewDto(1.0, 0.5, 0.5), "ZoomOut"));
            Add("Pan across", "Pan", () => new CameraKeyframeOverride(new CameraViewDto(1.15, 0.40, 0.5), new CameraViewDto(1.15, 0.60, 0.5), "Pan"));
            Add("Still", "Still", () => new CameraKeyframeOverride(new CameraViewDto(1.0, 0.5, 0.5), new CameraViewDto(1.0, 0.5, 0.5), "Still"));
            items.Add(new MenuFlyoutSeparator());
            Add("Dynamic, chosen at playback", "Dynamic", () => null, "No fixed path: the camera move is chosen each time the scene plays");
            items.Add(new MenuFlyoutSeparator());
            var edit = new MenuFlyoutItem { Text = "Edit camera path…", Icon = new FontIcon { Glyph = "" } };
            edit.Click += (_, _) => editPath();
            items.Add(edit);
        }

        private static void FillDurationMenu(IList<MenuFlyoutItemBase> items, ShowScene scene, Action changed)
        {
            double now = scene.DwellSeconds ?? 12.0;
            foreach (int seconds in new[] { 5, 8, 12, 16, 20, 30 })
            {
                int s = seconds;
                var item = new RadioMenuFlyoutItem { Text = $"{s}s", GroupName = "SceneDuration", IsChecked = Math.Abs(now - s) < 0.5 };
                item.Click += (_, _) => { scene.DwellSeconds = s; changed(); };
                items.Add(item);
            }
        }

        /// <summary>The count is what the tab shows: images that are not on the canvas.</summary>
        private string PoolTabLabel()
        {
            int n = TrayImages().Count;
            return n > 0 ? $"Image Pool ({n})" : "Image Pool";
        }

        private string StoryTabLabel()
        {
            int n = _activeShow?.VisibleSceneCount ?? 0;
            return n > 0 ? $"Show ({n})" : "Show";
        }

        /// <summary>Opens or closes the drawer on the tab it was last left on. It never changes what Play plays.</summary>
        private void StoryDeckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_trayCard != null) RemoveTrayCard();
            else ShowTrayCard(NoCell);
            ShowAutoPlayState();
        }

        /// <summary>B: the drawer on its Storyboard tab, or closed again if that is what is showing.</summary>
        private void ToggleStoryboardDrawer()
        {
            if (_trayCard != null && _trayTab == 1)
            {
                RemoveTrayCard();
            }
            else
            {
                _trayTab = 1;
                ShowTrayCard(NoCell);
            }
            ShowAutoPlayState();
        }

        /// <summary>Play walks the storyboard from now on. Nothing on the canvas changes; while playing, the next picture is a scene.</summary>

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
                Content = PoolTabLabel(),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var storyTab = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = StoryTabLabel(),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            tabStrip.Children.Add(poolTab);
            tabStrip.Children.Add(storyTab);
            _refreshPoolTab = () => poolTab.Content = PoolTabLabel();
            topHeader.Children.Add(tabStrip);

            Action? updateStorySummary = null;

            var headerMiddle = new Grid { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(headerMiddle, 1);
            topHeader.Children.Add(headerMiddle);

            var clearShowBtn = new Button
            {
                Content = "New show",
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Style = Resources.TryGetValue("QuietButtonStyle", out var clStyle) && clStyle is Style cls ? cls : null
            };
            // Beside the scenes it replaces, and next to the Image Pool its scenes are made from.
            ToolTipService.SetToolTip(clearShowBtn, "Starts an empty show with this preset and values. Play then makes its scenes from the Image Pool.");

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
                Content = "Add images",
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
                Content = "Clear pool",
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
                    Content = tray.Count == 1 ? "This removes 1 image from the Image Pool." : $"This removes all {tray.Count} images from the Image Pool. Images on the canvas will stay.",
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
                poolTab.Content = PoolTabLabel();
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
                poolTab.Content = PoolTabLabel();
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

            var addCanvasBtn = new Button
            {
                Content = "Add this layout",
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(addCanvasBtn, "Adds the images on screen as a new scene.");

            Border MakeDivider() => new Border { Width = 1, Height = 16, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };

            // Play is on the play bar only: a second Play Show here did the same thing.
            var storyHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            // Play makes the show's scenes (R17); the preset and the show's values are on the Show card, opened from Show on
            // the bar. Add this layout stays: it keeps what is on the canvas as a scene, which Play cannot.
            storyHeaderRow.Children.Add(addCanvasBtn);
            storyHeaderRow.Children.Add(MakeDivider());
            storyHeaderRow.Children.Add(storySummary);
            headerMiddle.Children.Add(storyHeaderRow);

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
            var emptyIcon = new FontIcon { Glyph = "\uE786", FontSize = 30, Foreground = winBlue, VerticalAlignment = VerticalAlignment.Center };
            var storyTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var emptyTitle = new TextBlock
            {
                Text = "No scenes in this show yet",
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var emptyDesc = new TextBlock
            {
                Text = "Press Play: the show's scenes are made as it plays, from the preset on the Show card. Add this layout keeps the images on the canvas as a scene.",
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
                    "    <Image Stretch=\"UniformToFill\" Margin=\"0,0,0,28\" IsHitTestVisible=\"False\"/>" +
                    "    <Grid Margin=\"0,0,0,28\" IsHitTestVisible=\"False\" Visibility=\"Collapsed\">" +
                    "      <Image Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "      <Image Stretch=\"UniformToFill\" Margin=\"0.5\"/>" +
                    "    </Grid>" +
                    "    <Grid Margin=\"0,0,0,28\" IsHitTestVisible=\"False\" Visibility=\"Collapsed\">" +
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
                    "    <Border HorizontalAlignment=\"Right\" VerticalAlignment=\"Bottom\" Margin=\"0,0,3,31\" Background=\"#E0000000\" CornerRadius=\"3\" Padding=\"3,1\" IsHitTestVisible=\"False\" Visibility=\"Collapsed\">" +
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
                    "    <Grid VerticalAlignment=\"Bottom\" Height=\"28\" Background=\"#F0161616\" Padding=\"4,1\">" +
                    "      <Grid.ColumnDefinitions>" +
                    "        <ColumnDefinition Width=\"*\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "        <ColumnDefinition Width=\"Auto\"/>" +
                    "      </Grid.ColumnDefinitions>" +
                    "      <TextBlock Text=\"Recipe\" FontSize=\"9.5\" Foreground=\"White\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/>" +
                    "      <Button Grid.Column=\"1\" MinWidth=\"26\" MinHeight=\"24\" Padding=\"4,2\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"4\" ToolTipService.ToolTip=\"Play show from this scene\" AutomationProperties.Name=\"Play show from this scene\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons, Segoe MDL2 Assets\" Glyph=\"&#xE768;\" FontSize=\"11\" Foreground=\"#4CC2FF\"/>" +
                    "      </Button>" +
                    "      <Button Grid.Column=\"2\" MinWidth=\"26\" MinHeight=\"24\" Padding=\"4,2\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"4\" ToolTipService.ToolTip=\"Edit camera path\" AutomationProperties.Name=\"Edit camera path\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons, Segoe MDL2 Assets\" Glyph=\"&#xE714;\" FontSize=\"11\" Foreground=\"#B0FFFFFF\"/>" +
                    "      </Button>" +
                    "      <Button Grid.Column=\"3\" MinWidth=\"26\" MinHeight=\"24\" Padding=\"4,2\" Margin=\"1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"4\" ToolTipService.ToolTip=\"Duplicate scene\" AutomationProperties.Name=\"Duplicate scene\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons, Segoe MDL2 Assets\" Glyph=\"&#xE8C8;\" FontSize=\"11\" Foreground=\"#B0FFFFFF\"/>" +
                    "      </Button>" +
                    "      <Button Grid.Column=\"4\" MinWidth=\"26\" MinHeight=\"24\" Padding=\"4,2\" Margin=\"6,0,1,0\" Background=\"Transparent\" BorderThickness=\"0\" CornerRadius=\"4\" ToolTipService.ToolTip=\"Remove scene. Ctrl+Z brings it back.\" AutomationProperties.Name=\"Remove scene\">" +
                    "        <FontIcon FontFamily=\"Segoe Fluent Icons, Segoe MDL2 Assets\" Glyph=\"&#xE711;\" FontSize=\"11\" Foreground=\"#B0FFFFFF\"/>" +
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

                if (hasScenes)
                {
                    double dur = _activeShow!.TotalEstimatedDuration;
                    string length = ShowLength(dur);
                    storySummary.Text = $"Preset: {ActivePresetName(_activeShow)} · {sceneCount} {(sceneCount == 1 ? "scene" : "scenes")} · {length}";
                }
                else
                {
                    // One count, said in full: every image in the session is on the canvas or in the Image Pool.
                    int inPool = TrayImages().Count;
                    storySummary.Text = totalPhotos == 0
                        ? $"Preset: {ActivePresetName(_activeShow)} · No scenes yet"
                        : $"Preset: {ActivePresetName(_activeShow)} · No scenes yet · {totalPhotos} image{(totalPhotos == 1 ? "" : "s")}: {totalPhotos - inPool} on the canvas, {inPool} in the Image Pool";
                }

                storyTab.Content = StoryTabLabel();
                ShowShownBadge();
                ShowAutoPlayState();
            }
            updateStorySummary = UpdateStoryboardSummary;
            _updateStorySummary = UpdateStoryboardSummary;
            _refreshStoryboardCards = () => RefreshCardBadges(refreshImages: true);

            // A scene's camera or duration changed from one of its menus.
            void SceneMenuChanged()
            {
                UpdateStoryboardSummary();
                RefreshCardBadges();
                RefreshTrayCardIfOpen();
            }

            // Editing a scene's camera, or clicking its card, does not change what Play plays: only the switch does.
            void EditCameraPath(ShowScene scene)
            {
                _selectedShowScene = scene;
                scenesView.SelectedItem = scene;
                RefreshCardBadges();
                EnterCameraDirector(scene);
            }

            void SyncSceneSelection()
            {
                // After Undo or opening a file, the show is new objects and these cards are the old ones until the drawer
                // is rebuilt. Taking the old object as the selection would leave a scene that is not in the show.
                if (_activeShow == null || !scenesCollection.All(s => _activeShow.Scenes.Contains(s))) return;
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
                        // A removed scene is no longer selected or on the canvas. Left in place, every later
                        // Undo step stored the canvas into it, and Undo could not get back past the removal.
                        if (_selectedShowScene != null && !scenesCollection.Contains(_selectedShowScene))
                        {
                            _selectedShowScene = null;
                            _canvasSceneId = null;
                        }
                        UpdateStoryboardSummary();
                        RefreshCardBadges();
                        // Removing a scene does not ask, so each change to the list is its own Undo step,
                        // however it was made: footer button, menu, Delete key or a drag. Taken once the
                        // selection has settled, as a pointer release is; taken at once, the selection that
                        // follows a removal became a second step and Undo stopped on it.
                        NoteUserAction();
                    }
                };
            }
            HookCollectionChanged();
            _sceneAddedBehindDrawer = (show, scene) =>
            {
                // Only while these cards are that show's, and without writing back: the show already has it.
                if (!ReferenceEquals(show, _activeShow) || scenesCollection.Contains(scene)) return;
                if (!scenesCollection.All(s => show.Scenes.Contains(s))) return;
                isSyncingStoryboard = true;
                try { scenesCollection.Add(scene); }
                finally { isSyncingStoryboard = false; }
                RefreshCardBadges();
            };
            _syncDrawerToShow = SyncStoryboardUI;

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

            // The picture area of a card: the card less its footer.
            const double CardPictureWidth = 200;
            const double CardPictureHeight = 136 - 28;

            void PopulateCardImages(Grid card, ShowScene scene, bool forceRefresh = false)
            {
                var singleImg = card.Children.Count > 0 ? card.Children[0] as Image : null;
                var twoImgGrid = card.Children.Count > 1 ? card.Children[1] as Grid : null;
                var host = card.Children.Count > 2 ? card.Children[2] as Grid : null;
                var multiBadge = card.Children.Count > 3 ? card.Children[3] as Border : null;
                var multiText = (multiBadge?.Child as StackPanel)?.Children.Count > 1 ? (multiBadge.Child as StackPanel)!.Children[1] as TextBlock : null;
                if (host == null) return;

                // The card draws the scene's own split, so a three-column scene looks like three columns.
                if (singleImg != null) { singleImg.Source = null; singleImg.Visibility = Visibility.Collapsed; }
                if (twoImgGrid != null) twoImgGrid.Visibility = Visibility.Collapsed;
                host.Visibility = Visibility.Visible;

                var boxes = scene.ThumbnailBoxes();
                string signature = string.Join("|", boxes.Select(b => $"{b.Path}@{b.X:F3},{b.Y:F3},{b.W:F3},{b.H:F3}"));
                if (!forceRefresh && host.Tag as string == signature && host.Children.Count > 0) return;
                host.Tag = signature;

                host.RowDefinitions.Clear();
                host.ColumnDefinitions.Clear();
                host.Children.Clear();
                foreach (var box in boxes)
                {
                    double w = Math.Max(1, box.W * CardPictureWidth - 1);
                    double h = Math.Max(1, box.H * CardPictureHeight - 1);
                    var tile = new Image
                    {
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                        Width = w,
                        Height = h,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(box.X * CardPictureWidth + 0.5, box.Y * CardPictureHeight + 0.5, 0, 0)
                    };
                    try
                    {
                        // Small tiles decode small. A solo scene is the whole card.
                        int decode = (int)Math.Clamp(w * 2, 96, 480);
                        tile.Source = new BitmapImage { DecodePixelWidth = decode, UriSource = new Uri(box.Path) };
                    }
                    catch (UriFormatException)
                    {
                        tile.Source = null;
                    }
                    host.Children.Add(tile);
                }

                int photoCount = boxes.Count;
                if (multiBadge != null)
                {
                    bool many = photoCount > 4;
                    multiBadge.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
                    if (many && multiText != null) multiText.Text = $"{photoCount} images";
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

                            FillCameraMenu(flyout.Items, curScene, SceneMenuChanged, () => EditCameraPath(curScene));
                            flyout.Items.Add(new MenuFlyoutSeparator());
                            var holdSub = new MenuFlyoutSubItem { Text = "Duration" };
                            FillDurationMenu(holdSub.Items, curScene, SceneMenuChanged);
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
                                ShowAutoPlayToast(SceneRemovedToast);
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
                        menu.Items.Add(MakeAdvancedItem("Play show from here", "\uE768", async () =>
                        {
                            _selectedShowScene = sc;
                            int idx = scenesCollection.IndexOf(sc);
                            await PlayStoryboardAsync(idx >= 0 ? idx : -1);
                        }));
                        menu.Items.Add(MakeAdvancedItem("Update scene from canvas", "\uE74E", () =>
                        {
                            SyncCanvasToSelectedScene(sc, showToast: true, commitCanvas: true);
                            CaptureUndoPoint();
                        }));

                        menu.Items.Add(new MenuFlyoutSeparator());

                        var motionSub = new MenuFlyoutSubItem { Text = "Camera motion" };
                        FillCameraMenu(motionSub.Items, sc, SceneMenuChanged, () => EditCameraPath(sc));
                        menu.Items.Add(motionSub);

                        var dwellSub = new MenuFlyoutSubItem { Text = "Duration" };
                        FillDurationMenu(dwellSub.Items, sc, SceneMenuChanged);
                        menu.Items.Add(dwellSub);

                        menu.Items.Add(new MenuFlyoutSeparator());
                        menu.Items.Add(MakeAdvancedItem("Duplicate scene", "\uE8C8", () =>
                        {
                            var clone = sc.Clone();
                            int curIdx = scenesCollection.IndexOf(sc);
                            if (curIdx >= 0)
                            {
                                scenesCollection.Insert(curIdx + 1, clone);
                                ShowAutoPlayToast("Duplicated scene");
                            }
                        }));
                        menu.Items.Add(MakeAdvancedItem("Remove scene", "\uE74D", () =>
                        {
                            scenesCollection.Remove(sc);
                            ShowAutoPlayToast(SceneRemovedToast);
                        }));
                        int curScIdx = scenesCollection.IndexOf(sc);
                        if (curScIdx > 0)
                        {
                            menu.Items.Add(MakeAdvancedItem("Move earlier", "\uE76B", () =>
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
                            menu.Items.Add(MakeAdvancedItem("Move later", "\uE76C", () =>
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
                scenesView.SelectedItem = scene;
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

            // The first click of the pair has already put the scene on the canvas.
            scenesView.DoubleTapped += (s, e) =>
            {
                if (scenesView.SelectedItem is ShowScene) RemoveTrayCard();
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
                    ShowAutoPlayToast(SceneRemovedToast);
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
                    // Empty cells are part of the layout: dropped, a scene of floating prints lost its grid and a pool image took its place.
                    Photos = file.Photos.Where(p => p.Slot < onShow).ToList(),
                    Freeform = file.Freeform != null ? new List<FreeformCellDto>(file.Freeform) : null
                };
                int photoCount = sceneFile.Photos.Count(p => !string.IsNullOrEmpty(p.Path)) + (sceneFile.Freeform?.Count(c => !string.IsNullOrEmpty(c.Path)) ?? 0);
                if (photoCount == 0)
                {
                    ShowAutoPlayToast("Put at least one image on the canvas first");
                    return;
                }
                string recipe = photoCount == 1 ? "Solo" : "Custom";
                string title = photoCount == 1 ? "Custom Solo" : $"Custom Layout ({photoCount})";
                var scene = new ShowScene
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = title,
                    Recipe = recipe,
                    Layout = sceneFile,
                    DwellSeconds = GeneratorSettingsFor(_activeShow).PlaySeconds,
                    Transition = "Crossfade"
                };

                // No show yet: a new one starts from the preset last chosen, as Play's does.
                _activeShow ??= NewShowWithLastPreset();
                scenesCollection.Add(scene);
                _selectedShowScene = scene;
                _canvasSceneId = scene.Id;
                ShowAutoPlayToast($"Added this layout as scene {scenesCollection.Count}");
            }

            addCanvasBtn.Click += (_, _) => CaptureCurrentCanvasAsScene();
            clearShowBtn.Click += async (_, _) =>
            {
                if (_activeShow == null || _activeShow.Scenes.Count == 0) return;
                if (!await ConfirmReplaceShowAsync()) return;
                // An empty show with the same preset and values; scenes still being made for the old one stop.
                StartEmptyShowLikeThisOne();
                _isStoryboardPlayback = false;
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
            storyTab.Click += (_, _) => { _trayTab = 1; SyncTabs(); };

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
                double maxH = MaxDrawerHeight();
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
                double expandedH = Math.Min(380, MaxDrawerHeight());
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
            _trayDrawerHeight = Math.Min(_trayDrawerHeight, MaxDrawerHeight());
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

        // Brings the storyboard's settings button up to date after the Show card changes this show's values.
        private int _storyboardBuildTicket;
        // The show Play from images is still making scenes for: it holds at its last scene rather than going back to the start.
        private ShowProject? _showBeingBuilt;
        // Play starts once the first scene is made; the next is drafted while it holds.
        private const int ScenesBeforePlay = 1;
        private const int ScenesAheadWhilePlaying = 3;
        // Released at each change of scene, so the storyboard build makes its next scene then, mid-hold.
        private readonly System.Threading.SemaphoreSlim _storyboardAdvanced = new(0, 1);
        // Bumped when the show's values change, so a step already being made with the old values is not kept.
        private int _upcomingStepRevision;
        // Scenes were built while the show played and the summary and badge have not been brought up to date.
        private bool _storyCountsStale;
        private bool IsPlayingStoryboard => _isStoryboardPlayback && _autoPlaySeconds > 0;

        /// <summary>The scene Play is showing, or the one selected when nothing is playing.</summary>
        private string? SceneOnScreenId()
        {
            if (!string.IsNullOrEmpty(_canvasSceneId) && _activeShow?.Scenes.Any(s => s.Id == _canvasSceneId) == true)
                return _canvasSceneId;
            if (_selectedShowScene != null && _activeShow?.Scenes.Any(s => s.Id == _selectedShowScene.Id) == true)
                return _selectedShowScene.Id;
            if (!_isStoryboardPlayback || _activeShow == null) return null;
            var visible = _activeShow.Scenes.Where(s => !s.IsHidden).ToList();
            if (visible.Count == 0) return null;
            int shown = _showPlaybackIndex >= visible.Count ? visible.Count - 1 : _showPlaybackIndex - 1;
            return shown >= 0 ? visible[shown].Id : null;
        }

        /// <summary>
        /// Play drafts a few scenes ahead. Those were made with the old values, so a change of the show's values removes
        /// them and wakes the drafter. The scene on screen stays. A storyboard Play is not making is left as it is.
        /// </summary>
        private void DropScenesDraftedAhead()
        {
            var show = _activeShow;
            if (show == null || !ReferenceEquals(show, _showBeingBuilt)) return;
            _upcomingStepRevision++;
            string? onScreen = SceneOnScreenId();
            int keep = ShowDirector.ScenesKeptWhenValuesChange(show.Scenes, onScreen);
            if (keep < show.Scenes.Count)
                show.Scenes.RemoveRange(keep, show.Scenes.Count - keep);
            _syncDrawerToShow?.Invoke();
            if (_isStoryboardPlayback && onScreen != null)
            {
                var visible = show.Scenes.Where(s => !s.IsHidden).ToList();
                int shown = visible.FindIndex(s => s.Id == onScreen);
                if (shown >= 0) _showPlaybackIndex = shown + 1;
            }
            if (IsPlayingStoryboard) _storyCountsStale = true;
            if (_storyboardAdvanced.CurrentCount == 0) _storyboardAdvanced.Release();
        }

        // The next storyboard scene's images, decoded while the current one holds.
        private readonly Dictionary<string, BitmapSource> _storyPreloaded = new(StringComparer.OrdinalIgnoreCase);
        private System.Threading.CancellationTokenSource? _storyPreloadCts;

        private void PreloadStoryScene(ShowScene next)
        {
            _storyPreloadCts?.Cancel();
            _storyPreloadCts?.Dispose();
            var cts = new System.Threading.CancellationTokenSource();
            _storyPreloadCts = cts;
            var have = DecodedBitmaps();
            var paths = next.GetPhotoPaths()
                .Where(p => !string.IsNullOrEmpty(p) && !have.ContainsKey(p) && !_storyPreloaded.ContainsKey(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count > 0) _ = PreloadBitmapsAsync(paths, _storyPreloaded, cts.Token);
        }

        /// <summary>When a show stops playing, the counts it put off.</summary>
        private void CatchUpStoryCounts()
        {
            if (!_storyCountsStale) return;
            _storyCountsStale = false;
            _updateStorySummary?.Invoke();
            ShowShownBadge();
        }

        /// <summary>
        /// Play from images: a new storyboard of the session's images that starts playing after its first scene and goes on
        /// making scenes while it plays, every one kept. Each scene's images are dealt as Auto-Mix deals them: every image
        /// before any comes round again, neighbours in folder and date by Same folder and date, featured images first,
        /// screens and repeats left out. Once every image is in a scene it keeps making new ones while the show plays, and
        /// waits while it is paused. A show that was opened or saved is not made here: it replays its own scenes. Reading a
        /// large folder's faces first would keep the screen still for minutes. Stops if the show is replaced or cleared.
        /// </summary>
        /// <param name="overrides">Null for the main Layout Generator settings, or the name of a set of overrides
        /// (<see cref="CinematicOverrides.Name"/>) that the show keeps for every scene made for it.</param>
        public async Task MakeAndPlayStoryboardAsync(string? overrides = null)
        {
            int ticket = ++_storyboardBuildTicket;
            // The session has only just opened: wait for the canvas to have its size, so scenes take its shape.
            for (int i = 0; i < 40 && (ActualWidth < 1 || ActualHeight < 1); i++) await Task.Delay(50);

            StopAutoPlay();
            RemoveTrayCard();
            bool cinematic = string.Equals(overrides, CinematicOverrides.Name, StringComparison.OrdinalIgnoreCase);
            // Play with an empty show plays that show: its preset and values were chosen on the Show card.
            ShowProject show;
            if (overrides == null && _activeShow is { Scenes.Count: 0 } empty) show = empty;
            else
            {
                show = new ShowProject { Title = cinematic ? "Cinematic storyboard" : "Storyboard" };
                if (overrides == null && _activeShow is ShowProject previous && previous.GlobalDefaults.HoldsValues)
                {
                    // Play show from the folders uses the preset chosen on the Show card, not a fresh Multi-Image.
                    show.GeneratorOverrides = previous.GeneratorOverrides;
                    show.PresetName = previous.PresetName;
                    show.PresetSlot = previous.PresetSlot;
                    show.GlobalDefaults.TakeValues(CinematicOverrides.From(GeneratorSettingsFor(previous)));
                }
                else if (overrides == null) ApplyPreset(show, SurpriseChoices.LastPreset);
                else SetCinematic(show, cinematic);
            }
            _activeShow = show;
            _selectedShowScene = null;
            _canvasSceneId = null;
            _isStoryboardPlayback = false;
            _showPlaybackIndex = 0;
            ShowShownBadge();
            bool Current() => ticket == _storyboardBuildTicket && ReferenceEquals(show, _activeShow);
            var deck = new PictureDeck(PicturesInOrder(), new Random());
            int failedHands = 0;
            _showBeingBuilt = show;
            PlayTiming.Start(SurpriseChoices.LogPlayTiming);

            // Keep my canvas: every scene takes the canvas as it is now, before the show changes it. Only the old direct mix
            // applied it, so it did nothing once Play always played the show.
            var myCanvas = (Color: _canvasColor, Surface: _canvasSurface, Frame: _canvasFrame.Clone());

            // Every image dealt at least once, placed or not: the end of the first round through the session.
            var dealtOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool started = false;
            while (Current())
            {
                // While the show plays, stay only a few scenes ahead of the screen. Building a scene is UI work, and
                // building them all at once made the camera judder through the first part of the show. Woken by each
                // change of scene; the timeout notices a pause, after which the rest are built at once.
                while (Current() && started && IsPlayingStoryboard && show.Scenes.Count - _showPlaybackIndex >= ScenesAheadWhilePlaying)
                    await _storyboardAdvanced.WaitAsync(TimeSpan.FromSeconds(2));
                if (!Current()) return;

                var session = SessionPictures();
                if (session.Count == 0) break;
                deck.MatchSession(session);
                // Every image has been dealt once: more are made only while the show plays, so a pause does not draft forever.
                // Dealt, not placed: screens and near-repeats are left out and a layout may take fewer, so some images
                // are never in a scene, and waiting for that drafted without end (409 scenes from 182 images).
                if (started && !IsPlayingStoryboard && session.All(dealtOnce.Contains))
                {
                    await _storyboardAdvanced.WaitAsync(TimeSpan.FromSeconds(2));
                    continue;
                }
                // Read for each scene. A change also drops scenes drafted ahead but not yet shown, and bumps
                // _upcomingStepRevision so a step already in flight, made with the old values, is not kept.
                var settings = GeneratorSettingsFor(show);
                int draftedAt = _upcomingStepRevision;
                var chosen = DealHand(deck, settings).Where(p => !_imagesBeingDrafted.Contains(p)).ToList();
                if (chosen.Count == 0) break;
                dealtOnce.UnionWith(chosen);
                _imagesBeingDrafted.UnionWith(chosen);
                ShowScene? scene;
                try
                {
                    long dealt = PlayTiming.Now;
                    var ready = DecodedBitmaps();
                    var facts = await ReadFactsAsync(chosen, path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);
                    if (!Current()) return;
                    long read = PlayTiming.Now;
                    if (settings.SkipScreens) facts = LayoutComposer.LeaveOutScreensAndRepeats(facts);
                    var previous = show.Scenes.Count > 0 ? show.Scenes[^1] : null;
                    // The hand is the scene: its size was already rolled in the Images per scene range.
                    var step = await ShowDirector.DraftStepAsync(facts, settings, ActualWidth, ActualHeight, previous, takeAll: true);
                    scene = step.Count > 0 ? step[0] : null;
                    PlayTiming.Write($"scene {show.Scenes.Count + 1} drafted: {chosen.Count} images, {step.Count} scenes, faces and sizes read in {read - dealt} ms, laid out in {PlayTiming.Now - read} ms");
                    if (!Current()) return;
                    if (draftedAt != _upcomingStepRevision)
                    {
                        PlayTiming.Write("draft discarded: the show's values changed before this step was kept");
                        continue;
                    }
                    // A hand that cannot be laid out, such as files that will not open: the next hand may. Five in a row
                    // means nothing left can be.
                    if (scene == null)
                    {
                        if (++failedHands >= 5) break;
                        continue;
                    }
                    failedHands = 0;
                    if (settings.KeepCanvas)
                        foreach (var made in step) made.Layout?.KeepCanvas(myCanvas.Color, myCanvas.Surface, myCanvas.Frame);
                    show.Scenes.AddRange(step);
                    // Every scene of the step shows in the storyboard; the last is announced below, as a single scene was.
                    foreach (var earlier in step.Take(step.Count - 1)) _sceneAddedBehindDrawer?.Invoke(show, earlier);
                    scene = step[^1];
                }
                finally
                {
                    _imagesBeingDrafted.ExceptWith(chosen);
                }

                _sceneAddedBehindDrawer?.Invoke(show, scene);
                // The summary and the badge wait for a pause: nothing shows them while the show plays.
                if (IsPlayingStoryboard) _storyCountsStale = true;
                else
                {
                    _updateStorySummary?.Invoke();
                    ShowShownBadge();
                }
                if (!started && show.Scenes.Count >= ScenesBeforePlay)
                {
                    started = true;
                    await PlayStoryboardAsync(0);
                }
            }
            // Nothing more will be made: the show now replays its scenes from the start.
            if (ReferenceEquals(_showBeingBuilt, show)) _showBeingBuilt = null;
            if (!started && Current() && show.Scenes.Count > 0) await PlayStoryboardAsync(0);
            if (Current())
            {
                _updateStorySummary?.Invoke();
                ShowShownBadge();
            }
        }

        // How long a scene change takes to start once its turn comes (decoding is done ahead; this is putting it on screen).
        private double _changeDelaySeconds = 0.3;

        /// <summary>
        /// The wait before the next scene's change: its planned hold, or, with Cut on the beat and a song whose beats are
        /// read, the wait that puts the change on the beat nearest the planned end (design 3.10). The saved hold is not
        /// changed, so the show plays as drafted with other music or none.
        /// </summary>
        private double WaitOnTheBeat()
        {
            double planned = _layoutSeconds;
            var music = MainWindow.Instance?.MyMusic;
            if (music == null || _surpriseMusic == null || (!music.CutOnBeat && music.Follow <= 0)) return planned;
            var settings = GeneratorSettingsFor(_activeShow);
            if (!settings.AutoPlayRandom) return planned;
            var beats = _surpriseMusic.Beats;
            double? now = _surpriseMusic.Position;
            double wait = Cinematic.BeatGrid.WaitBeforeChange(beats, now, planned,
                Math.Min(settings.RandomFewestSeconds, settings.RandomMostSeconds), Math.Max(settings.RandomFewestSeconds, settings.RandomMostSeconds),
                _changeDelaySeconds, music.CutOnBeat, music.Follow / 100.0, out var landing);
            string energy = beats != null && now is double at && beats.EnergyBetween(at, at + planned) is double e ? $", energy {e:0.00}" : "";
            string bars = beats == null ? "no beats read" : beats.BarsUsable ? $"bars trusted ({beats.BarStrength:0.00})" : $"bars not trusted ({beats.BarStrength:0.00}, {beats.BarChanges} changes)";
            PlayTiming.Write($"music: {planned} s planned, holds {(wait == planned ? wait : wait + _changeDelaySeconds):0.00} s, lands on {landing}{energy}; {bars}");
            return wait;
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
            _showPlaybackIndex = targetIndex;
            _autoPlaySeconds = 12;
            AddAutoPlayShield();
            StartAutoPlayMusic();

            var startScene = visibleScenes[targetIndex];
            _selectedShowScene = startScene;
            _canvasSceneId = null;
            _onStoryboardPlaybackIndexChanged?.Invoke(targetIndex);
            _layoutSeconds = (int)(startScene.DwellSeconds ?? GeneratorSettingsFor(_activeShow).NextPlaySeconds(Random.Shared));
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
                    // The show was cleared or replaced while it played: Play stops, it does not turn into a direct mix (R17).
                    StopAutoPlay();
                    return;
                }
                timer.Interval = TimeSpan.FromSeconds(Math.Max(1, WaitOnTheBeat()));
                if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
            };
            await PlayNextStoryboardSceneAsync(fade: true);
            timer.Interval = TimeSpan.FromSeconds(Math.Max(1, WaitOnTheBeat()));
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

            long turnCame = PlayTiming.Now;
            var turnClock = System.Diagnostics.Stopwatch.StartNew();
            bool heldForNext = false;
            // The next scene is still being made: this one holds, still, until it is ready. Play never goes back to the
            // start of a show that is still growing, and never shows a blank screen.
            if (_showPlaybackIndex >= visibleScenes.Count && ReferenceEquals(_activeShow, _showBeingBuilt))
            {
                var show = _activeShow;
                int waitingFor = _showPlaybackIndex;
                while (_autoPlaySeconds > 0 && ReferenceEquals(_activeShow, show) && ReferenceEquals(show, _showBeingBuilt)
                       && show.Scenes.Count(s => !s.IsHidden) <= waitingFor)
                    await Task.Delay(50);
                PlayTiming.Write($"scene {waitingFor + 1} was not ready: held the last one {PlayTiming.Now - turnCame} ms longer");
                heldForNext = true;
                if (_autoPlaySeconds == 0 || !ReferenceEquals(_activeShow, show)) return;
                visibleScenes = show.Scenes.Where(s => !s.IsHidden).ToList();
            }

            if (_showPlaybackIndex < 0 || _showPlaybackIndex >= visibleScenes.Count)
                _showPlaybackIndex = 0;

            int currentIndex = _showPlaybackIndex;
            var scene = visibleScenes[currentIndex];
            _selectedShowScene = scene;
            _canvasSceneId = null;
            _onStoryboardPlaybackIndexChanged?.Invoke(currentIndex);
            var showSettings = GeneratorSettingsFor(_activeShow);
            _layoutSeconds = (int)(scene.DwellSeconds ?? showSettings.NextPlaySeconds(Random.Shared));

            var pictures = SessionPictures();
            var ready = DecodedBitmaps();
            foreach (var pair in _storyPreloaded) ready.TryAdd(pair.Key, pair.Value);
            _storyPreloaded.Clear();
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

            // The list taken at the start of this turn may have lost the scenes drafted ahead: the show's values changed
            // while this one was coming on. The next scene is whatever is in the show now.
            if (_activeShow == null)
            {
                StopAutoPlay();
                return;
            }
            var liveScenes = _activeShow.Scenes.Where(s => !s.IsHidden).ToList();
            int liveIndex = liveScenes.FindIndex(s => s.Id == scene.Id);
            if (liveIndex < 0) liveIndex = Math.Clamp(currentIndex, 0, Math.Max(0, liveScenes.Count - 1));
            // Before a Cut to a closer view of this image the move ends with the hold: its end frame is where the next starts.
            bool cutNext = liveIndex + 1 < liveScenes.Count && liveScenes[liveIndex + 1].Transition == nameof(LayoutTransition.Cut);
            double overrun = cutNext ? 0 : CameraOverrunSeconds;
            if (scene.CameraOverride != null)
            {
                var move = scene.CameraOverride.ToCameraMove(_layoutSeconds + overrun);
                if (move != null)
                {
                    _lastCameraKind = move.Kind;
                    // A path set by hand has no planned strength: the floats take one rolled in the show's Motion range.
                    StartFloats(showSettings.PickMotion(Random.Shared));
                    RunCamera(move, goingHome: false);
                }
            }
            else
            {
                // Dynamic: the scene keeps its saved layout, and this call plans only the move, aimed at the faces as shown.
                var subjects = await SubjectsOnCanvasAsync();
                var made = SceneCall.Make(new SceneRequest(Array.Empty<PhotoFacts>(), RootGrid.ActualWidth, RootGrid.ActualHeight,
                    showSettings, _layoutSeconds, Random.Shared.Next(), LastKind: _lastCameraKind,
                    Keep: new KeptLayout(null, subjects)));
                var move = made?.Move;
                if (move != null)
                {
                    _lastCameraKind = move.Kind;
                    StartFloats(made!.Amount);
                    RunCamera(move, goingHome: false);
                }
            }

            // The scene's own transition, chosen when it was made and shown on nothing else. It used to be rolled again
            // here from the current settings, so what a scene stored was never what played.
            // How long putting a scene on screen takes, from its turn to its change, for Cut on the beat. A turn that waited
            // for its scene to be made says nothing about that.
            if (!heldForNext) _changeDelaySeconds = 0.7 * _changeDelaySeconds + 0.3 * turnClock.Elapsed.TotalSeconds;
            if (before != null)
                ChangeLayoutOver(before, outgoingStep, Enum.TryParse<LayoutTransition>(scene.Transition, out var planned) ? planned : LayoutTransition.Crossfade);

            int displayIndex = liveIndex + 1;
            int totalDisplay = liveScenes.Count;
            string sceneName = scene.Title ?? scene.Recipe ?? $"Scene {displayIndex}";
            // Only with the bar up: once it has hidden for want of movement, nothing else should come up over the picture.
            if (_isHudVisible) ShowAutoPlayToast($"Scene {displayIndex} of {totalDisplay}: {sceneName}");
            if (_autoPlaySeconds > 0) StartPlaybackProgress(_layoutSeconds);
            else StopPlaybackProgress();

            PlayTiming.Write($"scene {currentIndex + 1} shown {PlayTiming.Now - turnCame} ms after its turn came, holds {_layoutSeconds} s");
            // A show still being made goes on to its next scene when that is ready; a finished one goes round again.
            bool growing = ReferenceEquals(_activeShow, _showBeingBuilt);
            _showPlaybackIndex = liveScenes.Count == 0 ? 0
                : growing && liveIndex + 1 >= liveScenes.Count ? liveIndex + 1
                : (liveIndex + 1) % liveScenes.Count;
            ShowAutoPlayState();
            if (_storyboardAdvanced.CurrentCount == 0) _storyboardAdvanced.Release();
            // The next scene's images load while this one holds, as Auto-Mix's next layout does, so its change does not wait on them.
            if (_autoPlaySeconds > 0 && _showPlaybackIndex >= 0 && _showPlaybackIndex < liveScenes.Count) PreloadStoryScene(liveScenes[_showPlaybackIndex]);
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
                // The scene keeps its own creation time; a fresh one on every store made each Undo step differ.
                Created = scene.Layout?.Created ?? file.Created,
                Canvas = file.Canvas,
                State = file.State,
                Mosaic = file.Mosaic,
                // Empty cells are part of the layout: dropped, a scene of floating prints lost its grid and a pool image took its place.
                    Photos = file.Photos.Where(p => p.Slot < onShow).ToList(),
                Freeform = file.Freeform != null ? new List<FreeformCellDto>(file.Freeform) : null,
                ActiveSceneId = scene.Id
            };

            int photoCount = sceneFile.Photos.Count(p => !string.IsNullOrEmpty(p.Path)) + (sceneFile.Freeform?.Count(c => !string.IsNullOrEmpty(c.Path)) ?? 0);
            if (photoCount == 0) return;

            var stored = MultiViewLayoutFile.FromJson(sceneFile.ToJson()) ?? sceneFile;
            if (commitCanvas) _canvasSceneId = scene.Id;
            // Undo compares whole sessions, the show included. Storing a canvas the scene already holds, and
            // stamping the show as modified, made every Undo press take a new step and undo only that one.
            if (scene.Layout != null && stored.ToJson() == scene.Layout.ToJson())
            {
                if (showToast) ShowAutoPlayToast($"Scene \"{scene.Title}\" already matches the canvas");
                return;
            }
            scene.Layout = stored;
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
            _refreshPoolTab = null;
            _refreshStoryboardCards = null;
            _syncStorySceneSelection = null;
            _sceneAddedBehindDrawer = null;
            _syncDrawerToShow = null;
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

        /// <summary>The first new layout comes at once, so choosing it visibly does something.</summary>
        private void StartChosenPlay()
        {
            // Play shows the show: the Show card closes rather than cover it.
            if (_surpriseCard != null) RemoveSurpriseCard();
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
            if (SessionPictures().Count < 1)
            {
                AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            // Everything goes through the director (R15, R17): a show with no scenes yet is made as it plays, and kept.
            await MakeAndPlayStoryboardAsync();
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
            CatchUpStoryCounts();
            // Wakes the storyboard build, which may now make the rest of its scenes.
            if (_storyboardAdvanced.CurrentCount == 0) _storyboardAdvanced.Release();
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
                ShowAutoPlayToast("Paused");
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
                    ? $"Pause the show (Scene {curScene} of {totalScenes}: {currentSceneTitle}). Space does the same. Left and Right move between scenes."
                    : "Pause the show. Space does the same.")
                : (storyboardMode
                    ? (curScene > 1 && curScene <= totalScenes
                        ? $"Resume the show at Scene {curScene} of {totalScenes} ({currentSceneTitle}). Space does the same."
                        : $"Play the show ({totalScenes} {(totalScenes == 1 ? "scene" : "scenes")}, {ShowLength(_activeShow!.TotalEstimatedDuration)}). Space does the same.")
                    : "Play the show: its scenes are made as it plays, from the preset and values on the Show card, and kept. Space does the same.");

            // The label already says Pause while something plays; the name is the label.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoPlayButton, text);
            ToolTipService.SetToolTip(AutoPlayButton, playTip);
            PaintSelected(AutoPlayButton, AutoPlayText, AutoPlayIcon, playing);

            // The drawer button shows the open-panel plate only. The mode lives on its own switch.
            if (StoryDeckButton != null)
            {
                PaintSelected(StoryDeckButton, StoryDeckText, StoryDeckIcon, false);
                HoldFlyoutSource(StoryDeckButton, _trayCard != null);
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
                _surpriseMusic = new Cinematic.MusicBed(DispatcherQueue) { ReadsBeats = true };
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

            // The same blue as the drawer's selected tab and its Play Show, so one surface has one "selected" look.
            // The theme's accent fill drew a grey plate dimmer than the open-drawer one, and a slate or grey
            // Windows accent does the same.
            button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
            button.ClearValue(Control.BorderBrushProperty);
            button.ClearValue(Control.BorderThicknessProperty);
            var onAccent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
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
                    composition = await Task.Run(() => SceneCall.Make(new SceneRequest(facts, width, height, options, 0, Random.Shared.Next(), avoid, RasterScale: scale))?.Composition);
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

                var composition = await Task.Run(() => SceneCall.Make(new SceneRequest(facts, width, height, options, 0, Random.Shared.Next(), avoid, RasterScale: scale))?.Composition, token);
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
            var sourceList = (_isStoryboardPlayback || _surpriseDeck == null) ? pictures : (_surpriseDeck.All);
            return MultiViewLayoutFile.WithPoolAfter(file, sourceList);
        }

        /// <summary>Featured pictures are always in the hand. The deck supplies the rest.</summary>
        private List<string> DealHand(PictureDeck deck, SurpriseOptions options)
        {
            int fewest = Math.Min(options.FewestPictures, options.MostPictures);
            // Any count in the range, each as likely, as storyboards draft them.
            int size = Random.Shared.Next(fewest, options.MostPictures + 1);
            var hand = _featuredPictures.Where(deck.Holds).Take(size).ToList();
            foreach (string path in deck.Deal(size, Random.Shared.NextDouble() < options.TogetherChance))
            {
                if (hand.Count >= size) break;
                if (!hand.Contains(path, StringComparer.OrdinalIgnoreCase)) hand.Add(path);
            }
            return hand;
        }

        private const int CoverLayer = 5700;

        private async Task<Image?> CoverWithPictureOfCanvasAsync()
        {
            try
            {
                var shot = new RenderTargetBitmap();
                // From outside the canvas's clip: taken of the canvas itself, a zoomed layer reaching past its edge makes the picture bigger than the canvas.
                await shot.RenderAsync(this);
                var cover = new Image { Source = shot, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, IsHitTestVisible = false };
                // Above the canvas and the floating cells (5000), below the progress edge, the bar, toasts and cards: the
                // picture holds them too, and above the bar it faded an old copy of it over the live one.
                AddOverCanvas(cover, CoverLayer);
                return cover;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // No fade, then. The new layout still shows.
                return null;
            }
        }

        /// <summary>
        /// Until the new layout's pictures have loaded, then one drawn frame so they are on screen. It used to look every
        /// 50 ms and then always wait another 50: with the pictures loaded ahead, that fixed wait was most of the pause
        /// at each change, the old picture standing still on screen.
        /// </summary>
        private async Task WaitForPicturesAsync()
        {
            for (int waited = 0; waited < MostWaitForPicturesMs && AnyPhotoLoading(); waited += 15)
                await Task.Delay(15);
            await NextRenderedFrameAsync();
        }

        /// <summary>
        /// The next drawn frame, or a quarter second, whichever comes first: a minimized window draws no frames, and the
        /// scene change must not wait on one.
        /// </summary>
        private async Task NextRenderedFrameAsync()
        {
            var drawn = new TaskCompletionSource();
            void Once(object? sender, object e) => drawn.TrySetResult();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Once;
            try
            {
                await Task.WhenAny(drawn.Task, Task.Delay(MostWaitForFrameMs));
            }
            finally
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Once;
            }
        }

        private const int MostWaitForFrameMs = 250;

        private LayoutTransition? _lastTransition;

        /// <summary>
        /// The new layout stays covered until its pictures have loaded, so they appear together. The cover is a picture of the old layout,
        /// so each transition only has to animate that.
        /// </summary>
        /// <param name="planned">A storyboard scene's own transition, made with it. Otherwise Auto-Mix picks one from the
        /// odds while it plays, and a change by hand is a crossfade.</param>
        private void ChangeLayoutOver(Image cover, (double Scale, double X, double Y) outgoingStep = default, LayoutTransition? planned = null)
        {
            var kind = planned
                ?? (_autoPlaySeconds > 0 ? SurpriseChoices.PickTransition(_lastTransition, Random.Shared) : LayoutTransition.Crossfade);
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
                    AddOverCanvas(dark, CoverLayer - 10);
                    ContinueOutgoing(400);
                    Animate(cover, "Opacity", 1, 0, 400);
                    Animate(dark, "Opacity", 1, 0, 550, after: 450);
                    break;
                case LayoutTransition.Cut:
                    // A closer view of the same picture: nothing on the canvas changes but the camera.
                    Animate(cover, "Opacity", 1, 0, 1);
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

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
        // Only set while the session started with more pictures than a layout can show.
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
            try
            {
                System.IO.Directory.CreateDirectory(AppPaths.LocalDirectory);
                AtomicFile.WriteAllText(SurpriseOptionsPath, SurpriseChoices.ToJson());
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // It still applies until the app closes.
            }
        }

        private void AttachSurpriseStops()
        {
            // Closing the card leaves auto-play running. Only a click on the picture, Esc, Space or the bar's button pauses it.
            RootGrid.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (_surpriseCard != null && !IsInSurpriseCard(e.OriginalSource)) RemoveSurpriseCard();
            }), true);
            RootGrid.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                if (_surpriseCard != null && e.Key == Windows.System.VirtualKey.Escape)
                {
                    RemoveSurpriseCard();
                    e.Handled = true;
                }
            }), true);
            RootGrid.SizeChanged += (_, _) => PlaceSurpriseCard();
        }

        private const double SurpriseCardWidth = 452;
        private const double SurpriseCardGap = 8;
        private string _surpriseSectionOpen = "How many pictures";
        private TextBlock? _cardSongText;
        private Button? _cardPlayButton;
        private FontIcon? _cardPlayIcon;
        private TextBlock? _cardPlayText;
        private Button? _cardPreviousSong;
        private Button? _cardNextSong;

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

            FontIcon Icon(string glyph, double size = 14) => new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), Glyph = glyph, FontSize = size };

            Button IconButton(string glyph, string name, RoutedEventHandler pressed)
            {
                var button = new Button { Content = Icon(glyph), Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                ToolTipService.SetToolTip(button, name);
                button.Click += pressed;
                return button;
            }

            void Row(StackPanel column, string label, string tip, FrameworkElement control)
            {
                var row = new Grid { ColumnSpacing = 10, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                row.Children.Add(text);
                row.Children.Add(control);
                column.Children.Add(row);
            }

            void Slide(StackPanel column, string label, string tip, double min, double max, double step, double value, Action<SurpriseOptions, double> chosen)
            {
                var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, Value = value };
                slider.ValueChanged += (_, e) => ChangeSurpriseChoices(o => chosen(o, e.NewValue));
                Row(column, label, tip, slider);
            }

            void Toggle(StackPanel column, string label, string tip, bool value, Action<SurpriseOptions, bool> chosen)
            {
                var toggle = new ToggleSwitch { IsOn = value, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0 };
                toggle.Toggled += (_, _) => ChangeSurpriseChoices(o => chosen(o, toggle.IsOn));
                Row(column, label, tip, toggle);
            }

            // Layouts: every step button says what it steps through, so it cannot be taken for the music's.
            Button WordButton(string text, string name, string tip, RoutedEventHandler pressed)
            {
                var button = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
                ToolTipService.SetToolTip(button, tip);
                button.Click += pressed;
                return button;
            }

            TextBlock Label(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7 };

            body.Children.Add(Label("Layouts"));
            var transport = new Grid { ColumnSpacing = 6 };
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var previousLayout = WordButton("\u2039 Layout", "Previous layout", "Previous layout (Left). Pauses first.", (_, _) => StepLayout(back: true));
            var nextLayout = WordButton("Layout \u203A", "Next layout", "Forward again after going back (Right).", (_, _) => StepLayout(back: false));
            _cardPlayIcon = Icon("\uE768", 13);
            _cardPlayText = new TextBlock { Text = "Play", VerticalAlignment = VerticalAlignment.Center };
            var playContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            playContent.Children.Add(_cardPlayIcon);
            playContent.Children.Add(_cardPlayText);
            _cardPlayButton = new Button { Content = playContent, HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_cardPlayButton, "Play or pause from the card");
            _cardPlayButton.Click += AutoPlayButton_Click;
            Grid.SetColumn(_cardPlayButton, 1);
            Grid.SetColumn(nextLayout, 2);
            transport.Children.Add(previousLayout);
            transport.Children.Add(_cardPlayButton);
            transport.Children.Add(nextLayout);
            body.Children.Add(transport);

            // How long each layout stays: a quick choice, or a random range
            var speeds = new List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>();
            var speed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var range = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = options.AutoPlayRandom ? Visibility.Visible : Visibility.Collapsed };
            void SpeedChanged()
            {
                if (_autoPlaySeconds > 0 && _autoPlayTimer != null)
                {
                    _autoPlaySeconds = SurpriseChoices.PlaySeconds;
                    _autoPlayTimer.Interval = TimeSpan.FromSeconds(SurpriseChoices.NextPlaySeconds(Random.Shared));
                }
                ShowAutoPlayState();
            }
            Microsoft.UI.Xaml.Controls.Primitives.ToggleButton Chip(object content, string name, bool on)
            {
                var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = content, IsChecked = on, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0, FontSize = 12 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, name);
                ToolTipService.SetToolTip(chip, name);
                speeds.Add(chip);
                speed.Children.Add(chip);
                return chip;
            }
            foreach (int seconds in SurpriseOptions.AutoPlayChoices.Where(s => s > 0))
            {
                var chip = Chip(seconds.ToString(), $"A new layout every {seconds} seconds", !options.AutoPlayRandom && seconds == options.PlaySeconds);
                chip.Click += (_, _) =>
                {
                    foreach (var other in speeds) other.IsChecked = ReferenceEquals(other, chip);
                    range.Visibility = Visibility.Collapsed;
                    ChangeSurpriseChoices(o => { o.AutoPlaySeconds = seconds; o.AutoPlayRandom = false; });
                    SpeedChanged();
                };
            }
            var randomChip = Chip("Random", "A random time for each layout", options.AutoPlayRandom);
            randomChip.Margin = new Thickness(4, 0, 0, 0);
            randomChip.Click += (_, _) =>
            {
                foreach (var other in speeds) other.IsChecked = ReferenceEquals(other, randomChip);
                range.Visibility = Visibility.Visible;
                ChangeSurpriseChoices(o => o.AutoPlayRandom = true);
                SpeedChanged();
            };
            ComboBox RangeBox(int value, string name)
            {
                var box = new ComboBox { ItemsSource = SurpriseOptions.RandomPlayChoices.ToList(), SelectedItem = value, MinWidth = 76, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, name);
                return box;
            }
            var fewest = RangeBox(options.RandomFewestSeconds, "Shortest random time");
            var most = RangeBox(options.RandomMostSeconds, "Longest random time");
            // The two ends cannot cross: moving one past the other brings the other with it.
            fewest.SelectionChanged += (_, _) =>
            {
                if (fewest.SelectedItem is not int value) return;
                if (most.SelectedItem is int high && high < value) most.SelectedItem = value;
                ChangeSurpriseChoices(o => o.RandomFewestSeconds = value);
                SpeedChanged();
            };
            most.SelectionChanged += (_, _) =>
            {
                if (most.SelectedItem is not int value) return;
                if (fewest.SelectedItem is int low && low > value) fewest.SelectedItem = value;
                ChangeSurpriseChoices(o => o.RandomMostSeconds = value);
                SpeedChanged();
            };
            range.Children.Add(new TextBlock { Text = "Between", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            range.Children.Add(fewest);
            range.Children.Add(new TextBlock { Text = "and", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            range.Children.Add(most);
            range.Children.Add(new TextBlock { Text = "seconds", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });

            var speedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            speedRow.Children.Add(new TextBlock { Text = "Every", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            speedRow.Children.Add(speed);
            speedRow.Children.Add(new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Visibility = Visibility.Collapsed });
            body.Children.Add(speedRow);
            body.Children.Add(range);

            body.Children.Add(Label("Music"));
            body.Children.Add(MusicRow(Icon, WordButton));
            body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Opacity = 0.2, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) });

            // The ranges, one section open at a time
            var sections = new List<Expander>();
            StackPanel Section(string title)
            {
                var inner = new StackPanel { Spacing = 2 };
                var section = new Expander { Header = title, Content = inner, IsExpanded = title == _surpriseSectionOpen, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                section.Expanding += (_, _) =>
                {
                    _surpriseSectionOpen = title;
                    foreach (var other in sections)
                        if (!ReferenceEquals(other, section)) other.IsExpanded = false;
                };
                sections.Add(section);
                body.Children.Add(section);
                return inner;
            }

            var count = Section("How many pictures");
            Slide(count, "Fewest each click", "The least a click uses, when there are more pictures than the most below.", SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.FewestPictures, (o, v) => o.FewestPictures = (int)v);
            Slide(count, "Most each click", "The most a click uses. With more pictures than this, clicks take turns through them.", SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.MostPictures, (o, v) => o.MostPictures = (int)v);
            Slide(count, "Same folder and date, %", "The odds that a click takes pictures that are neighbours when sorted by folder, then file date, which usually means one event. Otherwise it takes them from anywhere.", 0, 100, 5, options.TogetherChance * 100, (o, v) => o.TogetherChance = v / 100);
            Toggle(count, "Skip screens and repeats", "Leave out what looks like a screen capture or a graphic, and all but one of near-identical shots. This is a guess.", options.SkipScreens, (o, on) => o.SkipScreens = on);

            var designs = Section("How often each design comes up");
            foreach (string design in LayoutComposer.Recipes)
            {
                Slide(designs, design, "Left is rarer, right is more often. A design is never ruled out.", -1, 1, 0.1, Math.Log(options.OddsOf(design), DesignOddsBase),
                    (o, v) => o.DesignOdds[design] = Math.Pow(DesignOddsBase, v));
            }

            var version = Section("Choosing a version");
            Slide(version, "Variety", "How many of the best versions it picks from. 1 is always the best one.", 1, 12, 1, options.Variety, (o, v) => o.Variety = (int)v);
            Slide(version, "Face shown, %", "How much of a face must show for it to count as uncut. Lower allows bolder layouts.", 50, 100, 5, options.FaceShown * 100, (o, v) => o.FaceShown = v / 100);

            var faces = Section("Zoom on faces");
            Slide(faces, "Most zoom, x", "1 is off. Brings forward people who are small in the picture. A person Windows misses can be cropped.", 1, SurpriseOptions.MostOfAnyZoom, 0.5, options.MostZoom, (o, v) => o.MostZoom = v);
            Slide(faces, "How often, %", "The odds that a picture with faces is zoomed.", 0, 100, 5, options.ZoomChance * 100, (o, v) => o.ZoomChance = v / 100);

            var look = Section("Look");
            Slide(look, "Most tilt, degrees", "How far Scattered prints can turn.", 0, 20, 0.5, options.MostTilt, (o, v) => o.MostTilt = v);
            Slide(look, "Hero size, %", "The hero's height against the canvas, give or take a tenth.", 40, 90, 1, options.HeroSize * 100, (o, v) => o.HeroSize = v / 100);
            Slide(look, "Gallery gaps", "1 is the usual gap between pictures on a Gallery wall.", 0.25, 3, 0.25, options.GapScale, (o, v) => o.GapScale = v);
            var card = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = SurpriseOptions.Cards.ToList(), SelectedItem = options.Card };
            card.SelectionChanged += (_, _) => { if (card.SelectedItem is string name) ChangeSurpriseChoices(o => o.Card = name); };
            Row(look, "Gallery card", "The color of the card the pictures are set into.", card);
            Toggle(look, "Keep my canvas", "Keep the canvas color, surface and frame as they are now, instead of the design's.", options.KeepCanvas, (o, on) => o.KeepCanvas = on);

            var defaults = new Button { Content = "Back to defaults", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12 };
            defaults.Click += (_, _) =>
            {
                _surpriseOptions = new SurpriseOptions();
                ChangeSurpriseChoices(_ => { });
                StopAutoPlay();
                ShowAutoPlayState();
                RemoveSurpriseCard();
                ShowSurpriseCard();
            };
            body.Children.Add(defaults);

            RemoveSurpriseCard();
            CloseCountsCard();
            _surpriseCard = new Border
            {
                Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) },
                Width = SurpriseCardWidth,
                Padding = new Thickness(14, 12, 2, 12),
                CornerRadius = new CornerRadius(10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                RequestedTheme = ElementTheme.Dark
            };
            Canvas.SetZIndex(_surpriseCard, 6500);
            Grid.SetRowSpan(_surpriseCard, Math.Max(1, RootGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(_surpriseCard, Math.Max(1, RootGrid.ColumnDefinitions.Count));
            RootGrid.Children.Add(_surpriseCard);
            PlaceSurpriseCard();
            SurpriseCardButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            ShowAutoPlayState();
            HudMenu_Opened(this, EventArgs.Empty);
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
        private FrameworkElement MusicRow(Func<string, double, FontIcon> icon, Func<string, string, string, RoutedEventHandler, Button> wordButton)
        {
            var music = MainWindow.Instance?.MyMusic;
            WatchMusicSettings();
            var panel = new StackPanel { Spacing = 6 };

            var top = new Grid { ColumnSpacing = 6 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var note = icon("\uE8D6", 13);
            note.VerticalAlignment = VerticalAlignment.Center;
            _cardSongText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_cardSongText, "SurpriseSongText");
            Grid.SetColumn(_cardSongText, 1);
            _cardPreviousSong = wordButton("\u2039 Song", "Previous song", "Previous song", (_, _) => _surpriseMusic?.PreviousSong());
            Grid.SetColumn(_cardPreviousSong, 2);
            _cardNextSong = wordButton("Song \u203A", "Next song", "Next song", (_, _) => _surpriseMusic?.NextSong());
            Grid.SetColumn(_cardNextSong, 3);
            top.Children.Add(note);
            top.Children.Add(_cardSongText);
            top.Children.Add(_cardPreviousSong);
            top.Children.Add(_cardNextSong);
            panel.Children.Add(top);

            var bottom = new Grid { ColumnSpacing = 8 };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool hasFolder = music?.HasFolder == true;
            var where = new TextBlock
            {
                Text = hasFolder ? $"{System.IO.Path.GetFileName(music!.Folder!.TrimEnd('\\'))}, {music.LowestStars:0.#}+ stars" + (music.IncludesUnrated ? " and unrated" : "") : "No music folder",
                FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            var on = new ToggleSwitch { IsOn = music?.InSurprise != false, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, IsEnabled = hasFolder };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(on, "Music with auto-play");
            ToolTipService.SetToolTip(on, "Play music with auto-play");
            on.Toggled += (_, _) =>
            {
                MainWindow.Instance?.SetMusicInSurprise(on.IsOn);
                if (_autoPlaySeconds > 0) { if (on.IsOn) StartAutoPlayMusic(); else StopAutoPlayMusic(); }
                ShowAutoPlayState();
            };
            Grid.SetColumn(on, 1);
            var settings = wordButton("Music settings", "Music settings", "Folder, rating and volume. The main window's gear opens the same settings.", (_, _) => MainWindow.Instance?.OpenMusicSettings());
            Grid.SetColumn(settings, 2);
            bottom.Children.Add(where);
            bottom.Children.Add(on);
            bottom.Children.Add(settings);
            panel.Children.Add(bottom);
            return panel;
        }

        private bool _watchingMusicSettings;

        private void WatchMusicSettings()
        {
            if (_watchingMusicSettings || MainWindow.Instance == null) return;
            _watchingMusicSettings = true;
            MainWindow.Instance.MusicSettingsChanged += () =>
            {
                if (_autoPlaySeconds > 0) StartAutoPlayMusic();
                if (_surpriseCard != null) ShowSurpriseCard();
            };
        }

        /// <summary>Steps through what Undo holds, pausing first, so going back does not race the next layout.</summary>
        private void StepLayout(bool back)
        {
            StopAutoPlay();
            if (back) Undo();
            else Redo();
        }

        private void RemoveSurpriseCard()
        {
            if (_surpriseCard == null) return;
            RootGrid.Children.Remove(_surpriseCard);
            _surpriseCard = null;
            _cardSongText = null;
            _cardPlayButton = null;
            _cardPlayIcon = null;
            _cardPlayText = null;
            _cardPreviousSong = null;
            _cardNextSong = null;
            SurpriseCardButton.ClearValue(Control.BackgroundProperty);
            HudMenu_Closed(this, EventArgs.Empty);
        }

        private bool IsInSurpriseCard(object? source)
        {
            for (var node = source as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _surpriseCard) || ReferenceEquals(node, SurpriseControl)) return true;
            }
            return false;
        }

        /// <summary>The first new layout comes at once, so choosing it visibly does something.</summary>
        private async void StartAutoPlay(int seconds)
        {
            StopAutoPlay();
            CaptureUndoPoint();
            _autoPlaySeconds = seconds;
            AddAutoPlayShield();
            StartAutoPlayMusic();
            ShowAutoPlayState();
            ShowAutoPlayToast($"Playing: a new layout every {SurpriseChoices.PlayWords}");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SurpriseChoices.NextPlaySeconds(Random.Shared)) };
            _autoPlayTimer = timer;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await SurpriseAsync(fade: true);
                timer.Interval = TimeSpan.FromSeconds(SurpriseChoices.NextPlaySeconds(Random.Shared));
                if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
            };
            await SurpriseAsync(fade: true);
            if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
        }

        private void StopAutoPlay()
        {
            if (_autoPlaySeconds == 0) return;
            _autoPlaySeconds = 0;
            _autoPlayTimer?.Stop();
            _autoPlayTimer = null;
            if (!_autoPlayShieldPressed) RemoveAutoPlayShield();
            CaptureUndoPoint();
            _surpriseMusic?.SetPlaying(false);
            ShowAutoPlayState();
            ShowAutoPlayToast("Paused");
        }

        /// <summary>The bar and the card always say whether it is playing, and pressing either changes it.</summary>
        private void ShowAutoPlayState()
        {
            bool playing = _autoPlaySeconds > 0;
            string glyph = playing ? "\uE769" : "\uE768";
            string text = playing ? SurpriseChoices.PlayLabel : "Play";
            AutoPlayIcon.Glyph = glyph;
            AutoPlayText.Text = text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoPlayButton, playing ? "Pause auto-play" : "Play auto-play");
            ToolTipService.SetToolTip(AutoPlayButton, playing ? "Pause. A click on the picture, Esc or Space does the same." : $"Play a new layout every {SurpriseChoices.PlayWords}. Space does the same.");
            if (playing)
            {
                var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
                var onAccent = Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
                AutoPlayButton.Background = accent;
                AutoPlayText.Foreground = onAccent;
                AutoPlayIcon.Foreground = onAccent;
            }
            else
            {
                AutoPlayButton.ClearValue(Control.BackgroundProperty);
                AutoPlayText.ClearValue(TextBlock.ForegroundProperty);
                AutoPlayIcon.ClearValue(IconElement.ForegroundProperty);
            }
            if (_cardPlayIcon != null) _cardPlayIcon.Glyph = glyph;
            if (_cardPlayText != null) _cardPlayText.Text = playing ? "Pause" : "Play";
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
                : music?.HasFolder != true ? "No music yet" : music.InSurprise ? "Plays with auto-play" : "Music is off";
            bool canStep = _surpriseMusic?.IsFolder == true;
            if (_cardPreviousSong != null) _cardPreviousSong.IsEnabled = canStep;
            if (_cardNextSong != null) _cardNextSong.IsEnabled = canStep;
        }

        private void AutoPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_autoPlaySeconds > 0) StopAutoPlay();
            else StartAutoPlay(SurpriseChoices.PlaySeconds);
        }

        /// <summary>
        /// Space plays and pauses. Esc pauses, and otherwise is the usual Back. Left and Right step through the layouts once Surprise me has made some.
        /// </summary>
        private bool HandleAutoPlayKey(Windows.System.VirtualKey key)
        {
            if (_surpriseCard != null)
            {
                if (key != Windows.System.VirtualKey.Escape) return false;
                RemoveSurpriseCard();
                return true;
            }
            if (_autoPlaySeconds > 0 && key is Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Space)
            {
                StopAutoPlay();
                return true;
            }
            if (key == Windows.System.VirtualKey.Space)
            {
                StartAutoPlay(SurpriseChoices.PlaySeconds);
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
            Canvas.SetZIndex(toast, 6200);
            Grid.SetRowSpan(toast, Math.Max(1, RootGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(toast, Math.Max(1, RootGrid.ColumnDefinitions.Count));
            _autoPlayToast = toast;
            RootGrid.Children.Add(toast);
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
        private string? _surpriseMusicKey;

        private void StartAutoPlayMusic()
        {
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
                    _surpriseMusicKey = null;
                    ShowAutoPlayToast(title);
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
            string key = $"{music.Folder}|{music.LowestStars}|{music.IncludesUnrated}";
            if (key != _surpriseMusicKey || !_surpriseMusic.IsOn)
            {
                _surpriseMusicKey = key;
                _surpriseMusic.PlayFolder(music.Folder!, music.LowestStars, music.IncludesUnrated);
            }
            _surpriseMusic.SetPlaying(true);
        }

        private void StopAutoPlayMusic()
        {
            _surpriseMusic?.Stop();
            _surpriseMusicKey = null;
        }

        /// <summary>
        /// Clear, over the canvas and under the bar and the card, so the click that pauses auto-play only pauses it and does not
        /// also reach the picture under it. It stays until that click is released, so the release cannot land on a cell either.
        /// </summary>
        private void AddAutoPlayShield()
        {
            if (_autoPlayShield != null) return;
            var shield = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Canvas.SetZIndex(shield, 5900);
            Grid.SetRowSpan(shield, Math.Max(1, RootGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(shield, Math.Max(1, RootGrid.ColumnDefinitions.Count));
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
            RootGrid.Children.Add(shield);
        }

        private void RemoveAutoPlayShield()
        {
            if (_autoPlayShield == null) return;
            RootGrid.Children.Remove(_autoPlayShield);
            _autoPlayShield = null;
            _autoPlayShieldPressed = false;
        }

        /// <summary>One new layout, always. If a show is playing it carries on.</summary>
        private async void SurpriseButton_Click(object sender, RoutedEventArgs e) => await SurpriseAsync(fade: _autoPlaySeconds > 0);

        private async Task SurpriseAsync(bool fade)
        {
            if (_surpriseBusy) return;
            bool playing = _autoPlaySeconds > 0;
            bool sameRun = _surpriseRun;
            var pictures = SessionPictures();
            if (SurpriseChoices.TakesTurns(pictures.Count) && _surpriseDeck == null)
                _surpriseDeck = new PictureDeck(PicturesInOrder(), new Random());
            _surpriseDeck?.Include(pictures);
            if ((_surpriseDeck?.Count ?? pictures.Count) < 2)
            {
                if (!playing) AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _surpriseBusy = true;
            SurpriseButton.IsEnabled = false;
            try
            {
                var options = SurpriseChoices;
                var chosen = _surpriseDeck == null ? pictures : DealHand(_surpriseDeck, options);
                var ready = DecodedBitmaps();
                // A picture already showing as failed would only put its message into the design.
                var failed = _cellRoots.Select(root => root?.Tag as CellParts).Where(parts => parts?.Bound.Failed == true)
                    .Select(parts => parts!.Bound.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var facts = await ReadFactsAsync(chosen.Where(path => !failed.Contains(path)).ToList(), path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);
                if (_surpriseDeck != null && options.SkipScreens) facts = LayoutComposer.LeaveOutScreensAndRepeats(facts);

                double scale = XamlRoot?.RasterizationScale ?? 1;
                double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
                string? avoid = sameRun ? _surpriseRecipe : null;
                var composition = await Task.Run(() => LayoutComposer.Compose(facts, width, height, Random.Shared.Next(), avoid, scale, null, options));
                // The session was changed for other pictures while these were being read, and that wins.
                if (composition == null || !SessionPictures().SequenceEqual(pictures)) return;
                if (playing && _autoPlaySeconds == 0) return;

                if (options.KeepCanvas) composition.KeepCanvas(_canvasColor, _canvasSurface, _canvasFrame);
                if (!sameRun) AutoSaveSession();
                CaptureUndoPoint();
                _cellBorderFlyout?.Hide();
                Image? before = fade ? await CoverWithPictureOfCanvasAsync() : null;
                _restoringUndo = true;
                _reuseBitmaps = ready;
                try
                {
                    ApplyLayoutFile(composition.ToLayoutFile(), restoreAspect: false);
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
                if (before != null) FadeAway(before);
            }
            finally
            {
                _surpriseBusy = false;
                SurpriseButton.IsEnabled = true;
            }
        }

        /// <summary>Featured pictures are always in the hand. The deck supplies the rest.</summary>
        private List<string> DealHand(PictureDeck deck, SurpriseOptions options)
        {
            int fewest = Math.Min(options.FewestPictures, options.MostPictures);
            int size = Random.Shared.Next(fewest, options.MostPictures + 1);
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
                await shot.RenderAsync(RootGrid);
                var cover = new Image { Source = shot, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, IsHitTestVisible = false };
                Canvas.SetZIndex(cover, 6000);
                Grid.SetRowSpan(cover, Math.Max(1, RootGrid.RowDefinitions.Count));
                Grid.SetColumnSpan(cover, Math.Max(1, RootGrid.ColumnDefinitions.Count));
                RootGrid.Children.Add(cover);
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

        /// <summary>The new layout stays covered until its pictures have loaded, so they appear together.</summary>
        private void FadeAway(Image cover)
        {
            var fade = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(700) };
            Storyboard.SetTarget(fade, cover);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var story = new Storyboard();
            story.Children.Add(fade);
            story.Completed += (_, _) => RootGrid.Children.Remove(cover);
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
            StopAutoPlay();
            StopAutoPlayMusic();
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

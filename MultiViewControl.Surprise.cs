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
                if (_trayCard != null && !IsInside(e.OriginalSource, _trayCard) && !IsInside(e.OriginalSource, ShownBadge)) RemoveTrayCard();
            }), true);
            RootGrid.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                // Here as well as with the other keys, because the window leaves Esc alone while a text box has focus.
                if ((_surpriseCard != null || _trayCard != null) && e.Key == Windows.System.VirtualKey.Escape)
                {
                    RemoveSurpriseCard();
                    RemoveTrayCard();
                    e.Handled = true;
                }
            }), true);
            RootGrid.SizeChanged += (_, _) => PlaceSurpriseCard();
        }

        private const double SurpriseCardWidth = 452;
        private const double SurpriseCardGap = 8;
        private string _surpriseSectionOpen = "How many images";
        private TextBlock? _cardSongText;
        private Button? _cardPlayButton;
        private FontIcon? _cardPlayIcon;
        private TextBlock? _cardPlayText;
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

            Slider Slide(StackPanel column, string label, string tip, double min, double max, double step, double value, Action<SurpriseOptions, double> chosen)
            {
                var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, Value = value };
                slider.ValueChanged += (_, e) => ChangeSurpriseChoices(o => chosen(o, e.NewValue));
                Row(column, label, tip, slider);
                return slider;
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
            var previousLayout = WordButton("\u2039 Layout", "Previous layout", "Previous layout. Pauses first. With this card closed, Left does the same.", (_, _) => StepLayout(back: true));
            var nextLayout = WordButton("Layout \u203A", "Next layout", "Forward again after going back. With this card closed, Right does the same.", (_, _) => StepLayout(back: false));
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
                    _layoutSeconds = SurpriseChoices.NextPlaySeconds(Random.Shared);
                    _autoPlayTimer.Interval = TimeSpan.FromSeconds(_layoutSeconds);
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
            speedRow.Children.Add(new TextBlock { Text = "Seconds", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            speedRow.Children.Add(speed);
            body.Children.Add(speedRow);
            body.Children.Add(range);
            Slide(body, "Motion", "How far the camera moves over each layout while it plays, from still to strong, with floating cells following it. The middle matches a Cinematic show.", 0, 100, 5, options.MotionAmount * 100, (o, v) => o.MotionAmount = v / 100);

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

            var count = Section("How many images");
            var fewestEach = Slide(count, "Fewest each click", "The least a click uses, when there are more images than the most below.", SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.FewestPictures, (o, v) => o.FewestPictures = (int)v);
            var mostEach = Slide(count, "Most each click", "The most a click uses. The rest stay in the image tray, and clicks take turns through them.", SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.MostPictures, (o, v) => o.MostPictures = (int)v);
            // The two ends cannot cross, as with the random time: moving one past the other brings the other with it.
            fewestEach.ValueChanged += (_, e) => { if (mostEach.Value < e.NewValue) mostEach.Value = e.NewValue; };
            mostEach.ValueChanged += (_, e) => { if (fewestEach.Value > e.NewValue) fewestEach.Value = e.NewValue; };
            Slide(count, "Same folder and date, %", "The odds that a click takes images that are neighbours when sorted by folder, then file date, which usually means one event. Otherwise it takes them from anywhere.", 0, 100, 5, options.TogetherChance * 100, (o, v) => o.TogetherChance = v / 100);
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
            Slide(faces, "Most zoom, x", "1 is off. Brings forward people who are small in the image. A person Windows misses can be cropped.", 1, SurpriseOptions.MostOfAnyZoom, 0.5, options.MostZoom, (o, v) => o.MostZoom = v);
            Slide(faces, "How often, %", "The odds that an image with faces is zoomed.", 0, 100, 5, options.ZoomChance * 100, (o, v) => o.ZoomChance = v / 100);

            var camera = Section("Camera and transitions");
            Slide(camera, "Zooms rather than drifts, %", "The odds that the camera zooms in or out. Otherwise it drifts across the layout.", 0, 100, 5, options.MotionZoomChance * 100, (o, v) => o.MotionZoomChance = v / 100);
            Slide(camera, "Transition variety, %", "How often a change of layout zooms through, fades through dark or slides, rather than a crossfade. 0 is always a crossfade.", 0, 100, 5, options.TransitionVariety * 100, (o, v) => o.TransitionVariety = v / 100);
            Slide(camera, "Aims at faces, %", "The odds that it heads for a large cell with faces in it, where there is one.", 0, 100, 5, options.MotionFaceChance * 100, (o, v) => o.MotionFaceChance = v / 100);

            var look = Section("Look");
            Slide(look, "Most tilt, degrees", "How far Scattered prints can turn.", 0, 20, 0.5, options.MostTilt, (o, v) => o.MostTilt = v);
            Slide(look, "Hero size, %", "The hero's height against the canvas, give or take a tenth.", 40, 90, 1, options.HeroSize * 100, (o, v) => o.HeroSize = v / 100);
            Slide(look, "Gallery gaps", "1 is the usual gap between images on a Gallery wall.", 0.25, 3, 0.25, options.GapScale, (o, v) => o.GapScale = v);
            var card = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = SurpriseOptions.Cards.ToList(), SelectedItem = options.Card };
            card.SelectionChanged += (_, _) => { if (card.SelectedItem is string name) ChangeSurpriseChoices(o => o.Card = name); };
            Row(look, "Gallery card", "The color of the card the images are set into.", card);
            Toggle(look, "Keep my canvas", "Keep the canvas color, surface and frame as they are now, instead of the design's.", options.KeepCanvas, (o, on) => o.KeepCanvas = on);

            // Far from where a closing click lands, and it asks: this undoes every choice on the card, and Undo does not bring them back.
            var resetRow = new Grid { ColumnSpacing = 6, MinHeight = 32 };
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var askReset = new HyperlinkButton { Content = "Reset to defaults...", FontSize = 12, Padding = new Thickness(0, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(askReset, "Every setting on this card. Your music settings stay as they are.");
            var question = new TextBlock { Text = "Reset every Surprise me setting?", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
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
            resetRow.Children.Add(askReset);
            resetRow.Children.Add(question);
            resetRow.Children.Add(keep);
            resetRow.Children.Add(reset);
            body.Children.Add(resetRow);

            RemoveSurpriseCard();
            CloseCountsCard();
            _surpriseCard = NewCard(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) });
            _surpriseCard.Width = SurpriseCardWidth;
            _surpriseCard.Padding = new Thickness(14, 12, 2, 12);
            _surpriseCard.HorizontalAlignment = HorizontalAlignment.Left;
            _surpriseCard.VerticalAlignment = VerticalAlignment.Bottom;
            AddOverCanvas(_surpriseCard, 6500);
            PlaceSurpriseCard();
            SurpriseCardButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            SurpriseCardArrow.Glyph = "\uE70D";
            ShowAutoPlayState();
            HudMenu_Opened(this, EventArgs.Empty);
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
                Text = music?.Summary ?? MusicChoices.NoFolder,
                FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            var on = new ToggleSwitch { IsOn = music?.InSurprise != false, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, IsEnabled = hasFolder };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(on, "Music with auto-play");
            ToolTipService.SetToolTip(on, "Play music with auto-play");
            on.Toggled += (_, _) =>
            {
                MainWindow.Instance?.SetMusicInSurprise(on.IsOn);
                if (!on.IsOn) StopAutoPlayMusic();
                else if (_autoPlaySeconds > 0) StartAutoPlayMusic();
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
            SurpriseCardArrow.Glyph = "\uE70E";
            HudMenu_Closed(this, EventArgs.Empty);
        }

        private sealed record TrayImage(string Path, int Slot);

        private Border? _trayCard;

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

        private void ShownBadge_Click(object sender, RoutedEventArgs e)
        {
            if (_trayCard != null)
            {
                RemoveTrayCard();
                return;
            }
            StopAutoPlay();
            ShowTrayCard(NoCell);
        }

        private const int NoCell = -1;

        /// <summary>
        /// A position that cannot be seen means nothing, so images in the tray are chosen by looking at them. Opened from a cell, a click
        /// fills that cell. Opened from the bar's count, it puts the image on the canvas by the same rule as adding one.
        /// </summary>
        private void ShowTrayCard(int intoSlot)
        {
            RemoveSurpriseCard();
            RemoveTrayCard();
            var all = TrayImages().Select(t => t.Path).ToList();
            var current = new System.Collections.ObjectModel.ObservableCollection<string>(all);
            var thumbnails = new GridView
            {
                ItemsSource = current,
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid Width=\"132\" Height=\"96\"><Image Stretch=\"UniformToFill\"/></Grid></DataTemplate>")
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(thumbnails, "ImageTray");

            string click = intoSlot == NoCell ? "Click one to put it on the canvas."
                : $"Click one to {(IsEmptyCell(_items[intoSlot]) ? "add it to" : "swap it into")} position {PositionOfSlot(intoSlot)}.";
            var heading = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            void ShowHeading() => heading.Text = current.Count == all.Count ? $"Image tray: {all.Count}. {click}"
                : current.Count == 0 ? "No image in the tray has that in its name."
                : $"Image tray: {current.Count} of {all.Count}. {click}";
            ShowHeading();

            var search = new TextBox { PlaceholderText = "Search names", MinWidth = 180 };

            void RemoveFromSession(string path)
            {
                int slot = TraySlotOf(path);
                if (slot < 0) return;
                RemoveItem(_items[slot]);
                all.Remove(path);
                current.Remove(path);
                ShowHeading();
                ShowTrayChanged();
                if (all.Count == 0) RemoveTrayCard();
                // Its thumbnail had the focus and is gone.
                else search.Focus(FocusState.Programmatic);
            }

            thumbnails.ContainerContentChanging += (_, args) =>
            {
                if (args.ItemContainer.ContentTemplateRoot is not Grid cell || cell.Children[0] is not Image image) return;
                if (args.InRecycleQueue)
                {
                    image.Source = null;
                    return;
                }
                if (args.Item is not string path) return;
                ToolTipService.SetToolTip(args.ItemContainer, System.IO.Path.GetFileName(path));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, System.IO.Path.GetFileName(path));
                var menu = new MenuFlyout();
                if (Resources.TryGetValue("MultiViewMenuFlyoutPresenterStyle", out var style) && style is Style presenter) menu.MenuFlyoutPresenterStyle = presenter;
                menu.Items.Add(MakeAdvancedItem("Remove from session", "\uE74D", () => RemoveFromSession(path)));
                args.ItemContainer.ContextFlyout = menu;
                try
                {
                    image.Source = new BitmapImage { DecodePixelWidth = 200, UriSource = new Uri(path) };
                }
                catch (UriFormatException)
                {
                    image.Source = null;
                }
            };
            thumbnails.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is not string path) return;
                RemoveTrayCard();
                if (intoSlot == NoCell)
                {
                    BringFromTray(path);
                    return;
                }
                int slot = TraySlotOf(path);
                if (slot < 0) return;
                SwapItems(intoSlot, slot, false);
                ShowTrayChanged();
                NoteUserAction();
            };

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search the image tray");
            search.TextChanged += (_, _) =>
            {
                current = new System.Collections.ObjectModel.ObservableCollection<string>(all.Where(p => System.IO.Path.GetFileName(p).Contains(search.Text, StringComparison.OrdinalIgnoreCase)));
                thumbnails.ItemsSource = current;
                ShowHeading();
            };
            search.Loaded += (_, _) => search.Focus(FocusState.Programmatic);
            var close = new Button { Content = "Close" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close the image tray");
            close.Click += (_, _) => RemoveTrayCard();
            var top = new Grid { ColumnSpacing = 10 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(heading);
            Grid.SetColumn(search, 1);
            top.Children.Add(search);
            Grid.SetColumn(close, 2);
            top.Children.Add(close);
            var hint = new TextBlock { Text = "Right-click an image to remove it from the session.", FontSize = 12, Opacity = 0.7 };

            var body = new Grid { RowSpacing = 10 };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(hint, 1);
            Grid.SetRow(thumbnails, 2);
            body.Children.Add(top);
            body.Children.Add(hint);
            body.Children.Add(thumbnails);

            // Stretched up to a limit, so it follows the window if that is resized while it is open.
            _trayCard = NewCard(body);
            _trayCard.MaxWidth = 760;
            _trayCard.MaxHeight = 560;
            _trayCard.Margin = new Thickness(16);
            _trayCard.Padding = new Thickness(14);
            AddOverCanvas(_trayCard, 6500);
            HudMenu_Opened(this, EventArgs.Empty);
        }

        /// <summary>The tray may just have emptied, and the empty cells and the bar's count mention it.</summary>
        private void ShowTrayChanged()
        {
            for (int i = 0; i < VisibleCount(GridCellCount); i++)
                ShowEmptyState(i);
            ShowShownBadge();
        }

        private void RemoveTrayCard()
        {
            if (_trayCard == null) return;
            RootGrid.Children.Remove(_trayCard);
            _trayCard = null;
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

        /// <summary>The first new layout comes at once, so choosing it visibly does something.</summary>
        private async void StartAutoPlay(int seconds)
        {
            // With nothing to change between, it would claim to play and do nothing but the music.
            if (SessionPictures().Count < 2)
            {
                AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            StopAutoPlay();
            CaptureUndoPoint();
            _autoPlaySeconds = seconds;
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
                // Chosen first, so the camera knows how long the layout it is about to show will stay.
                _layoutSeconds = SurpriseChoices.NextPlaySeconds(Random.Shared);
                await SurpriseAsync(fade: true);
                timer.Interval = TimeSpan.FromSeconds(_layoutSeconds);
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
            SendCanvasCameraHome();
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
            ToolTipService.SetToolTip(AutoPlayButton, playing ? "Pause. A click on the canvas, Esc or Space does the same." : $"Play a new layout every {SurpriseChoices.PlayWords}. Space does the same.");
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
                : music?.HasFolder != true ? "No music yet" : !music.InSurprise ? "Music is off" : _musicProblem ?? "Plays with auto-play";
            // The message over the picture would be behind this card.
            // A long song name is cut short here, so the whole of it goes there too.
            ToolTipService.SetToolTip(_cardSongText, _musicProblem != null && _surpriseMusic?.IsFolder != true ? _musicProblemWhy : _surpriseSong);
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

        /// <summary>One new layout, always. If a show is playing it carries on.</summary>
        private async void SurpriseButton_Click(object sender, RoutedEventArgs e) => await SurpriseAsync(fade: _autoPlaySeconds > 0);

        private async Task SurpriseAsync(bool fade)
        {
            if (_surpriseBusy) return;
            bool playing = _autoPlaySeconds > 0;
            bool sameRun = _surpriseRun;
            var pictures = SessionPictures();
            // The session is the whole set, tray included, so what the user removed stays out and a change to Most takes effect.
            if (!SurpriseChoices.TakesTurns(pictures.Count)) _surpriseDeck = null;
            else if (_surpriseDeck == null) _surpriseDeck = new PictureDeck(PicturesInOrder(), new Random());
            else _surpriseDeck.MatchSession(pictures);
            if (pictures.Count < 2)
            {
                if (!playing) AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _surpriseBusy = true;
            _quietBar = playing;
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
                if (before != null && _autoPlaySeconds > 0) StartCanvasCamera(composition);
                if (before != null) ChangeLayoutOver(before);
            }
            finally
            {
                _surpriseBusy = false;
                _quietBar = false;
                SurpriseButton.IsEnabled = true;
            }
        }

        /// <summary>A layout holds the hand, and every other image goes in its tray, so saving or Undo does not lose them.</summary>
        private MultiViewLayoutFile WithRestInTray(MultiViewLayoutFile file, List<string> pictures)
        {
            var used = new HashSet<string>(file.Photos.Select(p => p.Path ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var cell in file.Freeform ?? new List<FreeformCellDto>()) used.Add(cell.Path ?? string.Empty);
            var rest = (_surpriseDeck?.All ?? (IReadOnlyList<string>)pictures).Where(used.Add).ToList();
            if (rest.Count > 0) AppendToTray(file, rest, file.Photos.Count);
            return file;
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
        private void ChangeLayoutOver(Image cover)
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
                    Animate(cover, "Opacity", 1, 0, 400);
                    Animate(dark, "Opacity", 1, 0, 550, after: 450);
                    break;
                case LayoutTransition.Slide:
                    int way = Random.Shared.Next(3);
                    if (way == 2) Animate(cover, MoveY, 0, -RootGrid.ActualHeight, 850, ease: smooth);
                    else Animate(cover, MoveX, 0, way == 0 ? -RootGrid.ActualWidth : RootGrid.ActualWidth, 850, ease: smooth);
                    break;
                default:
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

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
            // While the card is open, the input is about the card: closing it leaves auto-play running.
            RootGrid.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (_surpriseCard == null) StopAutoPlay();
                else if (!IsInSurpriseCard(e.OriginalSource)) RemoveSurpriseCard();
            }), true);
            RootGrid.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                if (_surpriseCard == null)
                {
                    StopAutoPlay();
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    RemoveSurpriseCard();
                    e.Handled = true;
                }
            }), true);
        }

        /// <summary>One card of ranges, thresholds and odds, which stays open while they are changed. A change applies from the next click.</summary>
        private void ShowSurpriseCard()
        {
            var options = SurpriseChoices;
            var left = new StackPanel { Spacing = 2, Width = 320 };
            var right = new StackPanel { Spacing = 2, Width = 340 };

            void Heading(StackPanel column, string text) =>
                column.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 2) });

            void Row(StackPanel column, string label, string tip, FrameworkElement control)
            {
                var row = new Grid { ColumnSpacing = 10, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
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

            ComboBox Choice(IEnumerable<string> names, string current, Action<string> chosen)
            {
                var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = names.ToList(), SelectedItem = current };
                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedItem is string name) chosen(name);
                };
                return box;
            }

            Heading(left, "How often each design comes up");
            foreach (string design in LayoutComposer.Recipes)
            {
                Slide(left, design, "Left is rarer, right is more often. A design is never ruled out.", -1, 1, 0.1, Math.Log(options.OddsOf(design), DesignOddsBase),
                    (o, v) => o.DesignOdds[design] = Math.Pow(DesignOddsBase, v));
            }
            Heading(left, "Choosing a version");
            Slide(left, "Variety", "How many of the best versions it picks from. 1 is always the best one.", 1, 12, 1, options.Variety, (o, v) => o.Variety = (int)v);
            Slide(left, "Face shown, %", "How much of a face must show for it to count as uncut. Lower allows bolder layouts.", 50, 100, 5, options.FaceShown * 100, (o, v) => o.FaceShown = v / 100);

            Heading(left, "Zoom on faces");
            Slide(left, "Most zoom, x", "1 is off. Brings forward people who are small in the picture. A person Windows misses can be cropped.", 1, SurpriseOptions.MostOfAnyZoom, 0.5, options.MostZoom, (o, v) => o.MostZoom = v);
            Slide(left, "How often, %", "The odds that a picture with faces is zoomed.", 0, 100, 5, options.ZoomChance * 100, (o, v) => o.ZoomChance = v / 100);

            Heading(right, "Look");
            Slide(right, "Most tilt, degrees", "How far Scattered prints can turn.", 0, 20, 0.5, options.MostTilt, (o, v) => o.MostTilt = v);
            Slide(right, "Hero size, %", "The hero's height against the canvas, give or take a tenth.", 40, 90, 1, options.HeroSize * 100, (o, v) => o.HeroSize = v / 100);
            Slide(right, "Gallery gaps", "1 is the usual gap between pictures on a Gallery wall.", 0.25, 3, 0.25, options.GapScale, (o, v) => o.GapScale = v);
            Row(right, "Gallery card", "The color of the card the pictures are set into.",
                Choice(SurpriseOptions.Cards, options.Card, name => ChangeSurpriseChoices(o => o.Card = name)));
            Toggle(right, "Keep my canvas", "Keep the canvas color, surface and frame as they are now, instead of the design's.", options.KeepCanvas, (o, on) => o.KeepCanvas = on);

            Heading(right, "With more than 16 pictures");
            Slide(right, "Fewest each click", "The least a click uses.", SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.FewestPictures, (o, v) => o.FewestPictures = (int)v);
            Slide(right, "Most each click", "The most a click uses.", SurpriseOptions.FewestAllowed, MosaicLayout.MaxShown, 1, options.MostPictures, (o, v) => o.MostPictures = (int)v);
            Slide(right, "Same folder and date, %", "The odds that a click takes pictures that are neighbours when sorted by folder, then file date, which usually means one event. Otherwise it takes them from anywhere.", 0, 100, 5, options.TogetherChance * 100, (o, v) => o.TogetherChance = v / 100);
            Toggle(right, "Skip screens and repeats", "Leave out what looks like a screen capture or a graphic, and all but one of near-identical shots. This is a guess.", options.SkipScreens, (o, on) => o.SkipScreens = on);

            Heading(right, "Auto-play");
            Row(right, "New layout every", "Starts as soon as it is chosen. Once this card is closed, a click on the picture or a key stops it.",
                Choice(new[] { "Off", "5 seconds", "10 seconds", "30 seconds" }, _autoPlaySeconds > 0 ? _autoPlaySeconds + " seconds" : "Off", name =>
                {
                    if (name == "Off") StopAutoPlay();
                    else StartAutoPlay(int.Parse(name.Split(' ')[0]));
                }));

            var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            columns.Children.Add(left);
            columns.Children.Add(right);
            var content = new StackPanel { Spacing = 8 };
            var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close Surprise me options");
            close.Click += (_, _) => RemoveSurpriseCard();
            var defaults = new Button { Content = "Back to defaults" };
            defaults.Click += (_, _) =>
            {
                _surpriseOptions = new SurpriseOptions();
                ChangeSurpriseChoices(_ => { });
                RemoveSurpriseCard();
                ShowSurpriseCard();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(defaults);
            buttons.Children.Add(close);
            content.Children.Add(columns);
            content.Children.Add(buttons);

            // In the window like the grid card, so it stays open when the window loses focus and takes no click meant for the canvas.
            RemoveSurpriseCard();
            _surpriseCard = new Border
            {
                Child = new ScrollViewer
                {
                    Content = content,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                Margin = new Thickness(8),
                Padding = new Thickness(16, 8, 16, 12),
                CornerRadius = new CornerRadius(10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RequestedTheme = ElementTheme.Dark
            };
            Canvas.SetZIndex(_surpriseCard, 6500);
            Grid.SetRowSpan(_surpriseCard, Math.Max(1, RootGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(_surpriseCard, Math.Max(1, RootGrid.ColumnDefinitions.Count));
            RootGrid.Children.Add(_surpriseCard);
            HudMenu_Opened(this, EventArgs.Empty);
        }

        private void RemoveSurpriseCard()
        {
            if (_surpriseCard == null) return;
            RootGrid.Children.Remove(_surpriseCard);
            _surpriseCard = null;
            HudMenu_Closed(this, EventArgs.Empty);
        }

        private bool IsInSurpriseCard(object? source)
        {
            for (var node = source as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _surpriseCard)) return true;
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
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _autoPlayTimer = timer;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await SurpriseAsync(fade: true);
                if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
            };
            await SurpriseAsync(fade: true);
            if (ReferenceEquals(_autoPlayTimer, timer)) timer.Start();
        }

        /// <summary>Everything auto-play showed is one undo step.</summary>
        private void StopAutoPlay()
        {
            if (_autoPlaySeconds == 0) return;
            _autoPlaySeconds = 0;
            _autoPlayTimer?.Stop();
            _autoPlayTimer = null;
            if (!_autoPlayShieldPressed) RemoveAutoPlayShield();
            CaptureUndoPoint();
        }

        /// <summary>
        /// Clear, over the canvas and under the card, so the click that stops auto-play only stops it and does not also open
        /// the picture under it. It stays until that click is released, so the release cannot land on a cell either.
        /// </summary>
        private void AddAutoPlayShield()
        {
            if (_autoPlayShield != null) return;
            var shield = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Canvas.SetZIndex(shield, 6400);
            Grid.SetRowSpan(shield, Math.Max(1, RootGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(shield, Math.Max(1, RootGrid.ColumnDefinitions.Count));
            shield.PointerPressed += (_, _) => _autoPlayShieldPressed = true;
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

        private async void SurpriseButton_Click(object sender, RoutedEventArgs e)
        {
            StopAutoPlay();
            await SurpriseAsync(fade: false);
        }

        private async Task SurpriseAsync(bool fade)
        {
            if (_surpriseBusy) return;
            bool playing = _autoPlaySeconds > 0;
            bool sameRun = _surpriseRun;
            var pictures = SessionPictures();
            if (pictures.Count > MosaicLayout.MaxShown && _surpriseDeck == null)
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
                if (!playing) CaptureUndoPoint();
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
                if (!playing) CaptureUndoPoint();
                _surpriseRun = true;
                _surpriseRecipe = composition.Recipe;
                if (before != null) await FadeAwayWhenLoadedAsync(before);
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

        /// <summary>The new layout stays covered until its pictures have loaded, so they appear together.</summary>
        private async Task FadeAwayWhenLoadedAsync(Image cover)
        {
            for (int waited = 0; waited < MostWaitForPicturesMs && AnyPhotoLoading(); waited += 50)
                await Task.Delay(50);
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

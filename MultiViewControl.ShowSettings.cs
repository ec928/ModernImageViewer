using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace ModernImageViewer
{
    /// <summary>
    /// The storyboard's own settings (design, the director): a preset to start from, and the values this show is played
    /// with. Layout Generator Settings say how one call is made; these say which values this show passes. A show that holds
    /// nothing of its own follows Layout Generator Settings; the first change here makes it hold the full set it was using.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private static readonly (string Name, double Odds)[] OddsSteps =
            { ("Off", 0), ("Rare", 0.25), ("Less", 0.5), ("Normal", 1), ("More", 2), ("Often", 5) };

        private const string MultiImagePreset = "Multi-Image";

        /// <summary>A new show, starting from the preset last chosen on the Show card, so the choice is kept across shows and sessions.</summary>
        private ShowProject NewShowWithLastPreset()
        {
            var show = new ShowProject { Title = "Storyboard" };
            ApplyPreset(show, SurpriseChoices.LastPreset);
            return show;
        }

        /// <summary>The show takes a preset's values by its name. A My preset that is empty, or an unknown name, is Multi-Image.</summary>
        private void ApplyPreset(ShowProject target, string? name)
        {
            if (name != null && name.StartsWith("My preset ", StringComparison.Ordinal)
                && int.TryParse(name.Substring("My preset ".Length), out int number)
                && SurpriseChoices.MyPresets.ElementAtOrDefault(number - 1) is CinematicOverrides saved)
            {
                target.GeneratorOverrides = null;
                target.PresetSlot = number - 1;
                target.PresetName = name;
                target.GlobalDefaults.TakeValues(saved);
            }
            else SetCinematic(target, name == CinematicOverrides.Name);
        }

        /// <summary>Remembers the preset the open show uses, in the app's settings.</summary>
        private void RememberPreset(ShowProject show)
        {
            string name = ActivePresetName(show);
            if (name == "Custom" || name == SurpriseChoices.LastPreset) return;
            ChangeSurpriseChoices(o => o.LastPreset = name);
        }

        // Whether the Show card's Designs section is open; it starts folded, as Engine settings' sections do.
        private bool _showDesignsOpen;

        /// <summary>The open show's values, for a change: the show takes every value it is played with now, if it held none.</summary>
        private ShowGlobalDefaults HoldingShowValues()
        {
            var show = _activeShow ??= NewShowWithLastPreset();
            if (!show.GlobalDefaults.HoldsValues)
                show.GlobalDefaults.TakeValues(CinematicOverrides.From(GeneratorSettingsFor(show)));
            return show.GlobalDefaults;
        }

        private void ChangeShowValues(Action<ShowGlobalDefaults> change, Action? changed)
        {
            change(HoldingShowValues());
            CancelPreparedSurprise();
            // Scenes already drafted were made with the old values. Drop the ones not yet shown so the next one uses these.
            DropScenesDraftedAhead();
            if (_activeShow != null) SaveShowPreset(_activeShow);
            _updateStorySummary?.Invoke();
            NoteUserAction();
            changed?.Invoke();
        }

        /// <summary>The storyboard's settings button names this show's preset, so the presets can be found there.</summary>
        private string ShowSettingsLabel()
        {
            var values = GeneratorSettingsFor(_activeShow);
            string preset = ActivePresetName(_activeShow);
            string images = values.FewestPictures == values.MostPictures ? $"{values.FewestPictures}" : $"{values.FewestPictures}–{values.MostPictures}";
            return $"Preset: {preset} · {images} images ▾";
        }

        /// <summary>Before a show with scenes is replaced: true when it has none, or the user says to replace it.</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmReplaceShowAsync()
        {
            int count = _activeShow?.Scenes.Count ?? 0;
            if (count == 0) return true;
            var dialog = new ContentDialog
            {
                Title = "Start a new show?",
                Content = $"The show's {count} scene{(count == 1 ? "" : "s")} will be replaced. Save it first from Save/Load to keep it.",
                PrimaryButtonText = "New show",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.Instance?.CurrentRequestedTheme ?? ElementTheme.Default
            };
            DialogFooter.MainButtonOnRight(dialog);
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        /// <summary>An empty show with the same preset and values as the open one; Play then makes its scenes.</summary>
        private void StartEmptyShowLikeThisOne()
        {
            var old = _activeShow;
            StopAutoPlay();
            _storyboardBuildTicket++;
            var show = new ShowProject { Title = old?.GeneratorOverrides == CinematicOverrides.Name ? "Cinematic storyboard" : "Storyboard" };
            if (old != null && old.GlobalDefaults.HoldsValues)
            {
                show.GeneratorOverrides = old.GeneratorOverrides;
                show.PresetName = old.PresetName;
                show.PresetSlot = old.PresetSlot;
                show.GlobalDefaults.TakeValues(CinematicOverrides.From(GeneratorSettingsFor(old)));
            }
            else ApplyPreset(show, SurpriseChoices.LastPreset);
            _activeShow = show;
            _selectedShowScene = null;
            _canvasSceneId = null;
            _showPlaybackIndex = 0;
            _updateStorySummary?.Invoke();
            ShowShownBadge();
            RefreshStoryboardSettingsLabel?.Invoke();
        }

        private static string PresetName(int slot, CinematicOverrides? preset) => $"My preset {slot + 1}" + (preset == null ? " (empty)" : "");

        private static string ActivePresetName(ShowProject? show)
        {
            if (!string.IsNullOrEmpty(show?.PresetName)) return show.PresetName!;
            if (show == null || !show.GlobalDefaults.HoldsValues) return MultiImagePreset;
            if (show.GeneratorOverrides == CinematicOverrides.Name) return CinematicOverrides.Name;
            return "Custom";
        }

        /// <summary>Fills the storyboard's settings pop-up with the open show's preset and values, as they are now.</summary>
        private void FillShowSettings(StackPanel panel, Action? changed)
        {
            panel.Children.Clear();
            // With no show open yet, the card shows the show Play would make: the last preset chosen.
            var show = _activeShow ??= NewShowWithLastPreset();
            var values = GeneratorSettingsFor(show);

            StackPanel into = panel;
            var valuesPanel = new StackPanel { Spacing = 2 };
            var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
            Grid Row(string label, string tip, FrameworkElement control)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                var text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                control.HorizontalAlignment = HorizontalAlignment.Right;
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                row.Children.Add(text);
                row.Children.Add(control);
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
                into.Children.Add(row);
                return row;
            }
            // A slider and its readout, as the card's other settings: 146 wide, the readout beside it.
            Grid WithReadout(FrameworkElement control, TextBlock readout)
            {
                var grid = new Grid { Width = 230, ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                Grid.SetColumn(readout, 1);
                grid.Children.Add(control);
                grid.Children.Add(readout);
                return grid;
            }
            TextBlock Readout(string text) => new() { Text = text, FontSize = 12, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            Slider Slider1(double min, double max, double step, double value)
            {
                var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, Value = value, Width = 146, Foreground = accent, VerticalAlignment = VerticalAlignment.Center };
                slider.Resources["SliderThumbBackground"] = accent;
                slider.Resources["SliderTrackValueFill"] = accent;
                slider.Resources["SliderTrackValueFillPointerOver"] = accent;
                slider.Resources["SliderTrackValueFillPressed"] = accent;
                return slider;
            }
            RangeSlider Range(double min, double max, double step, double low, double high) =>
                new() { Minimum = min, Maximum = max, StepFrequency = step, RangeStart = low, RangeEnd = Math.Max(low, high), Width = 146, VerticalAlignment = VerticalAlignment.Center };
            // A drag sends many values; the show takes the last one once the drag settles, so scenes are not redrafted each step.
            Action Settled(Action apply)
            {
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(350);
                timer.IsRepeating = false;
                timer.Tick += (_, _) => apply();
                return () => { timer.Stop(); timer.Start(); };
            }
            SpinNumberBox Box(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 72, Height = 28 };
            StackPanel Pair(SpinNumberBox a, SpinNumberBox b)
            {
                var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                pair.Children.Add(a);
                pair.Children.Add(new TextBlock { Text = "to", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                pair.Children.Add(b);
                return pair;
            }
            void Heading(string text) => into.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });

            // Presets, on one line: a press restores one, a long press (or right-click) saves this show into it, and the symbol
            // on the right puts the preset in use back to its defaults. As the window-size slots do: click to use, hold to keep.
            var presets = SurpriseChoices.MyPresets;
            string current = ActivePresetName(show);
            var presetLine = new Grid { ColumnSpacing = 8, MinHeight = 36, Margin = new Thickness(0, 4, 0, 4) };
            presetLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            presetLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            presetLine.Children.Add(new TextBlock { Text = "Preset", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(buttons, 1);
            presetLine.Children.Add(buttons);
            var selected = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));

            void Applied(string message)
            {
                if (_activeShow != null) RememberPreset(_activeShow);
                CancelPreparedSurprise();
                DropScenesDraftedAhead();
                _updateStorySummary?.Invoke();
                NoteUserAction();
                changed?.Invoke();
                ShowAutoPlayToast(message);
                // Rebuilt after this press is over, with the new preset lit.
                DispatcherQueue.TryEnqueue(() => FillShowSettings(panel, changed));
            }

            // Restores a preset: the show takes its values. An empty My preset has nothing to restore.
            void Restore(string name, int? slot)
            {
                var target = _activeShow ??= NewShowWithLastPreset();
                if (slot is int i)
                {
                    if (presets.ElementAtOrDefault(i) is not CinematicOverrides saved)
                    {
                        ShowAutoPlayToast($"My preset {i + 1} is empty. Hold it to keep this show's values there.");
                        return;
                    }
                    target.GeneratorOverrides = null;
                    target.PresetSlot = i;
                    target.PresetName = $"My preset {i + 1}";
                    target.GlobalDefaults.TakeValues(saved);
                }
                else SetCinematic(target, name == CinematicOverrides.Name);
                Applied($"Preset: {(slot is int n ? $"My preset {n + 1}" : name)}");
            }

            // Keeps this show's values in a preset, and makes it the one in use.
            void Keep(string name, int? slot)
            {
                var target = _activeShow ??= NewShowWithLastPreset();
                var snapshot = CinematicOverrides.From(GeneratorSettingsFor(target));
                ChangeSurpriseChoices(o =>
                {
                    if (slot is int i)
                    {
                        while (o.MyPresets.Count < 3) o.MyPresets.Add(null);
                        o.MyPresets[i] = snapshot;
                    }
                    else if (name == CinematicOverrides.Name) o.Cinematic = snapshot;
                    else o.MultiImage = snapshot;
                });
                target.GeneratorOverrides = name == CinematicOverrides.Name ? CinematicOverrides.Name : null;
                target.PresetSlot = slot;
                target.PresetName = slot is int k ? $"My preset {k + 1}" : name;
                target.GlobalDefaults.TakeValues(snapshot);
                Applied($"Kept this show's values in {(slot is int m ? $"My preset {m + 1}" : name)}");
            }

            void AddPreset(string label, string name, int? slot)
            {
                bool isCurrent = slot is int i ? current == $"My preset {i + 1}" : current == name;
                bool empty = slot is int j && presets.ElementAtOrDefault(j) == null;
                string title = slot is int t ? $"My preset {t + 1}" : name;
                var button = new Button
                {
                    Content = label,
                    FontSize = 12,
                    MinWidth = slot == null ? 0 : 36,
                    Padding = new Thickness(12, 4, 12, 4),
                    CornerRadius = new CornerRadius(12),
                    Opacity = empty ? 0.55 : 1
                };
                if (isCurrent)
                {
                    button.Background = selected;
                    button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                }
                ToolTipService.SetToolTip(button, empty
                    ? $"{title}: empty. Hold (or right-click) to keep this show's values here."
                    : $"{title}{(isCurrent ? ", in use" : "")}. Press to use it; hold (or right-click) to keep this show's values here.");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title + (isCurrent ? ", in use" : empty ? ", empty" : ""));

                // A press restores; a hold of 600 ms keeps. The hold is timed from the pointer, which the button would
                // otherwise swallow; the click that follows a hold is not a press.
                bool held = false;
                var hold = DispatcherQueue.CreateTimer();
                hold.Interval = TimeSpan.FromMilliseconds(600);
                hold.IsRepeating = false;
                hold.Tick += (_, _) => { held = true; Keep(name, slot); };
                button.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { held = false; hold.Start(); }), true);
                button.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => hold.Stop()), true);
                button.PointerCaptureLost += (_, _) => hold.Stop();
                button.PointerExited += (_, _) => hold.Stop();
                button.Click += (_, _) =>
                {
                    if (held) { held = false; return; }
                    Restore(name, slot);
                };
                // Right-click keeps too, for a mouse and the keyboard's menu key.
                var menu = new MenuFlyout();
                var keepItem = new MenuFlyoutItem { Text = $"Keep this show's values in {title}" };
                keepItem.Click += (_, _) => Keep(name, slot);
                menu.Items.Add(keepItem);
                button.ContextFlyout = menu;
                buttons.Children.Add(button);
            }
            AddPreset(MultiImagePreset, MultiImagePreset, null);
            AddPreset(CinematicOverrides.Name, CinematicOverrides.Name, null);
            for (int i = 0; i < 3; i++) AddPreset($"{i + 1}", $"My preset {i + 1}", i);

            // Back to defaults, for the preset in use.
            var reset = new Button
            {
                Content = new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE777", FontSize = 14 },
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Center
            };
            string resetWhat = current == "Custom" ? "this show" : current;
            ToolTipService.SetToolTip(reset, current.StartsWith("My preset")
                ? $"Reset {current}: it is emptied, and the show goes back to Multi-Image."
                : $"Reset {resetWhat} to its defaults.");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reset, $"Reset {resetWhat} to defaults");
            reset.Click += async (_, _) =>
            {
                var open = _activeShow;
                if (open?.PresetSlot is int slot && SurpriseChoices.MyPresets.ElementAtOrDefault(slot) != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Reset to defaults?",
                        Content = $"This empties My preset {slot + 1}. The show uses Multi-Image.",
                        PrimaryButtonText = "Reset",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot,
                        RequestedTheme = MainWindow.Instance?.CurrentRequestedTheme ?? ElementTheme.Default
                    };
                    DialogFooter.MainButtonOnRight(dialog);
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }
                var target = _activeShow ??= NewShowWithLastPreset();
                ResetShowPresetToDefaults(target);
                RememberPreset(target);
                _updateStorySummary?.Invoke();
                NoteUserAction();
                changed?.Invoke();
                DispatcherQueue.TryEnqueue(() => FillShowSettings(panel, changed));
            };
            Grid.SetColumn(reset, 2);
            presetLine.Children.Add(reset);
            panel.Children.Add(presetLine);

            // The values this show is played with, in their own panel, so a preset change rebuilds them and nothing else.
            panel.Children.Add(valuesPanel);
            FillValues();

            void FillValues()
            {
                valuesPanel.Children.Clear();
                into = valuesPanel;
                var values = GeneratorSettingsFor(_activeShow);

                Heading("Scenes");
                bool making = ReferenceEquals(_activeShow, _showBeingBuilt);
                valuesPanel.Children.Add(new TextBlock
                {
                    Text = making
                        ? "The scene on screen stays. Scenes not yet shown are made again with these."
                        : "Scenes already made stay as they are. These are used for scenes Play has not made yet.",
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                var fewestImages = Box(1, MosaicLayout.MaxShown, values.FewestPictures);
                var mostImages = Box(1, MosaicLayout.MaxShown, values.MostPictures);
                void SetImages() => ChangeShowValues(v =>
                {
                    v.FewestImages = Math.Min(fewestImages.Value, mostImages.Value);
                    v.MostImages = Math.Max(fewestImages.Value, mostImages.Value);
                }, changed);
                fewestImages.ValueChanged += (_, _) => SetImages();
                mostImages.ValueChanged += (_, _) => SetImages();
                Row("Images per scene", "How many images each new scene gets: any count in the range.", Pair(fewestImages, mostImages));

                static string Seconds(double a, double b) => a == b ? $"{a:0}s" : $"{a:0}s - {b:0}s";
                int fromSeconds = values.AutoPlayRandom ? values.RandomFewestSeconds : values.AutoPlaySeconds;
                int toSeconds = values.AutoPlayRandom ? values.RandomMostSeconds : values.AutoPlaySeconds;
                var duration = Range(SurpriseOptions.FewestSeconds, SurpriseOptions.MostSeconds, 1, fromSeconds, toSeconds);
                var durationReadout = Readout(Seconds(fromSeconds, Math.Max(fromSeconds, toSeconds)));
                (double Start, double End) durationNow = (fromSeconds, toSeconds);
                var applyDuration = Settled(() => ChangeShowValues(v =>
                {
                    v.FewestSeconds = (int)Math.Round(durationNow.Start);
                    v.MostSeconds = (int)Math.Round(Math.Max(durationNow.Start, durationNow.End));
                }, changed));
                duration.RangeChanged += (_, r) => { durationNow = r; durationReadout.Text = Seconds(Math.Round(r.Start), Math.Round(r.End)); applyDuration(); };
                Row("Duration", "How long each scene holds: each picks within the range. Thumbs together is one fixed duration.", WithReadout(duration, durationReadout));

                var smart = new ToggleSwitch { IsOn = values.SmartPace, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, Margin = new Thickness(0, 0, 4, 0) };
                smart.Toggled += (_, _) =>
                {
                    if (GeneratorSettingsFor(_activeShow).SmartPace == smart.IsOn) return;
                    ChangeShowValues(v => v.SmartPace = smart.IsOn, changed);
                };
                Row("Smart pace", "Dense layouts toward the longer end of the duration, layouts of one or two images toward the shorter.", smart);

                static string Percents(double a, double b) => a == b ? $"{a:0}%" : $"{a:0}% - {b:0}%";
                double fromMotion = Math.Round(values.MotionLeast * 100), toMotion = Math.Round(values.MotionMost * 100);
                var motion = Range(0, 100, 5, fromMotion, toMotion);
                var motionReadout = Readout(Percents(fromMotion, Math.Max(fromMotion, toMotion)));
                (double Start, double End) motionNow = (fromMotion, toMotion);
                var applyMotion = Settled(() => ChangeShowValues(v =>
                {
                    v.MotionLeast = motionNow.Start / 100.0;
                    v.MotionMost = Math.Max(motionNow.Start, motionNow.End) / 100.0;
                }, changed));
                motion.RangeChanged += (_, r) => { motionNow = r; motionReadout.Text = Percents(Math.Round(r.Start), Math.Round(r.End)); applyMotion(); };
                Row("Motion", "How far the camera moves in each scene: each picks within the range. 0 is still.", WithReadout(motion, motionReadout));

                static string Chance(double v) => v < 1 ? "Off" : $"{v:0}%";
                double beatsNow = Math.Round(values.SequenceBeatsChance * 100);
                var beats = Slider1(0, 100, 5, beatsNow);
                var beatsReadout = Readout(Chance(beatsNow));
                Row("Sequence beats", "The chance a step not yet shown is more than one scene: a group, then one image from it, then that image's faces. One image with no face stays one scene. Off: every step is one scene.", WithReadout(beats, beatsReadout));

                static string Scenes(double a, double b) => a == b ? $"{a:0}" : $"{a:0} to {b:0}";
                var scenes = Range(2, SurpriseOptions.MostScenesInAStep, 1, values.FewestScenesPerStep, values.MostScenesPerStep);
                var scenesReadout = Readout(Scenes(values.FewestScenesPerStep, Math.Max(values.FewestScenesPerStep, values.MostScenesPerStep)));
                (double Start, double End) scenesNow = (values.FewestScenesPerStep, values.MostScenesPerStep);
                var applyScenes = Settled(() => ChangeShowValues(v =>
                {
                    v.FewestScenesPerStep = (int)Math.Round(scenesNow.Start);
                    v.MostScenesPerStep = (int)Math.Round(Math.Max(scenesNow.Start, scenesNow.End));
                }, changed));
                scenes.RangeChanged += (_, r) => { scenesNow = r; scenesReadout.Text = Scenes(Math.Round(r.Start), Math.Round(r.End)); applyScenes(); };
                var scenesRow = Row("Scenes per step", "How many scenes a step may be when Sequence beats rolls, from 2 to 4. Not used while Sequence beats is Off. The step stops early when no closer view is left.", WithReadout(scenes, scenesReadout));
                void ShowScenesUsed()
                {
                    bool used = beats.Value >= 1;
                    scenes.IsHitTestVisible = used;
                    scenesRow.Opacity = used ? 1 : 0.45;
                }
                ShowScenesUsed();
                var applyBeats = Settled(() => ChangeShowValues(v => v.SequenceBeats = Math.Clamp(beats.Value, 0, 100) / 100.0, changed));
                beats.ValueChanged += (_, e) => { beatsReadout.Text = Chance(e.NewValue); ShowScenesUsed(); applyBeats(); };

                // Designs fold under one header, as Engine settings' sections do, so the card fits beside an open drawer.
                int designsOn = LayoutComposer.Recipes.Count(r => !values.IsDesignOff(r));
                var designsBody = new StackPanel { Spacing = 2, Visibility = _showDesignsOpen ? Visibility.Visible : Visibility.Collapsed };
                var designsTitle = new TextBlock { Text = "Designs", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var designsSummary = new TextBlock { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                void ShowDesignsSummary() => designsSummary.Text = (designsOn == LayoutComposer.Recipes.Length ? "All 6" : $"{designsOn} of 6") + (_showDesignsOpen ? "  \u25B4" : "  \u25BE");
                ShowDesignsSummary();
                var designsHead = new Grid { MinHeight = 32, Margin = new Thickness(0, 8, 0, 0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                designsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                designsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(designsSummary, 1);
                designsHead.Children.Add(designsTitle);
                designsHead.Children.Add(designsSummary);
                var designsToggle = new Button { Content = designsHead, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(designsToggle, "Designs");
                ToolTipService.SetToolTip(designsToggle, "How often this show uses each design.");
                designsToggle.Click += (_, _) =>
                {
                    _showDesignsOpen = !_showDesignsOpen;
                    designsBody.Visibility = _showDesignsOpen ? Visibility.Visible : Visibility.Collapsed;
                    ShowDesignsSummary();
                };
                valuesPanel.Children.Add(designsToggle);
                valuesPanel.Children.Add(designsBody);
                into = designsBody;
                static string Step(double v) => v <= -1.1 ? "Off" : v < -0.6 ? "Rare" : v < -0.15 ? "Less" : v <= 0.15 ? "Normal" : v <= 0.6 ? "More" : "Often";
                foreach (string design in LayoutComposer.Recipes)
                {
                    string named = design;
                    double odds = values.IsDesignOff(named) ? 0 : values.OddsOf(named);
                    double start = odds <= 0 ? -1.2 : Math.Log(Math.Max(SurpriseOptions.RarestDesign, odds), 5);
                    var pick = Slider1(-1.2, 1.0, 0.1, start);
                    var readout = Readout(Step(start));
                    var apply = Settled(() =>
                    {
                        double chosen = pick.Value <= -1.1 ? 0 : Math.Pow(5, pick.Value);
                        ChangeShowValues(v =>
                        {
                            v.DesignOdds ??= new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            v.DesignOdds[named] = chosen;
                        }, changed);
                    });
                    pick.ValueChanged += (_, e) => { readout.Text = Step(e.NewValue); apply(); };
                    Row(named, $"How often this show uses {named}. Far left turns it off for this show.", WithReadout(pick, readout));
                }
            }
        }
    }
}

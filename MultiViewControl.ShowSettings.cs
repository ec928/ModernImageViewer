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

        /// <summary>The open show's values, for a change: the show takes every value it is played with now, if it held none.</summary>
        private ShowGlobalDefaults HoldingShowValues()
        {
            var show = _activeShow ??= new ShowProject { Title = "Storyboard" };
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
                Content = $"The storyboard's {count} scene{(count == 1 ? "" : "s")} will be replaced. Save it first from the storyboard to keep it.",
                PrimaryButtonText = "New show",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.Instance?.CurrentRequestedTheme ?? ElementTheme.Default
            };
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
            var show = _activeShow;
            var values = GeneratorSettingsFor(show);

            Grid Row(string label, string tip, FrameworkElement control)
            {
                var row = new Grid { ColumnSpacing = 10, MinHeight = 30 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(control, 1);
                row.Children.Add(text);
                row.Children.Add(control);
                ToolTipService.SetToolTip(row, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
                panel.Children.Add(row);
                return row;
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
            void Heading(string text) => panel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });

            // Preset: the starting point. Choosing one gives this show its values.
            Heading("This show");
            var presets = SurpriseChoices.MyPresets;
            var names = new System.Collections.Generic.List<string> { MultiImagePreset, CinematicOverrides.Name };
            names.AddRange(Enumerable.Range(0, 3).Select(i => PresetName(i, presets.ElementAtOrDefault(i))));
            string current = ActivePresetName(show);
            if (current == "Custom") names.Add("Custom");
            var preset = new ComboBox { ItemsSource = names, SelectedItem = current, Width = 190 };
            preset.SelectionChanged += (_, _) =>
            {
                if (preset.SelectedItem is not string chosen || chosen == current || chosen == "Custom") return;
                var target = _activeShow ??= new ShowProject { Title = "Storyboard" };
                if (chosen == MultiImagePreset) SetCinematic(target, false);
                else if (chosen == CinematicOverrides.Name) SetCinematic(target, true);
                else
                {
                    int slot = names.IndexOf(chosen) - 2;
                    if (presets.ElementAtOrDefault(slot) is not CinematicOverrides saved) { preset.SelectedItem = current; return; }
                    target.GeneratorOverrides = null;
                    target.PresetSlot = slot;
                    target.PresetName = $"My preset {slot + 1}";
                    target.GlobalDefaults.TakeValues(saved);
                }
                CancelPreparedSurprise();
                DropScenesDraftedAhead();
                _updateStorySummary?.Invoke();
                NoteUserAction();
                changed?.Invoke();
                // Rebuilt once this event is over: the list is part of what is rebuilt.
                DispatcherQueue.TryEnqueue(() => FillShowSettings(panel, changed));
            };
            Row("Preset", "Where this show's values start from. What you change here is kept for the next Play. Cinematic is one image at a time, full screen, the camera always moving. My presets are yours.", preset);

            var save = new Button { Content = "Save as ▾", FontSize = 12 };
            var saveMenu = new MenuFlyout();
            for (int i = 0; i < 3; i++)
            {
                int slot = i;
                var item = new MenuFlyoutItem { Text = PresetName(slot, presets.ElementAtOrDefault(slot)).Replace(" (empty)", "") + (presets.ElementAtOrDefault(slot) == null ? "" : " (replace)") };
                item.Click += (_, _) =>
                {
                    var saved = CinematicOverrides.From(GeneratorSettingsFor(_activeShow));
                    ChangeSurpriseChoices(o =>
                    {
                        while (o.MyPresets.Count < 3) o.MyPresets.Add(null);
                        o.MyPresets[slot] = saved;
                    });
                    FillShowSettings(panel, changed);
                };
                saveMenu.Items.Add(item);
            }
            save.Flyout = saveMenu;
            Row("Save this show's values", "Keeps this show's values as My preset 1, 2 or 3, to start another show from.", save);

            var reset = new Button { Content = "Reset to defaults", FontSize = 12 };
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
                var target = _activeShow ??= new ShowProject { Title = "Storyboard" };
                ResetShowPresetToDefaults(target);
                _updateStorySummary?.Invoke();
                NoteUserAction();
                changed?.Invoke();
                FillShowSettings(panel, changed);
            };
            Row("Reset to defaults", "Puts this preset back to its starting values and keeps that. Multi-Image follows Layout Generator Settings again, with sequence beats at 25%. Cinematic goes back to one image, Edge to edge Often, Hero spotlight Rare, the others Off, 10 to 15 seconds, smart pace, motion 40% to 60%, sequence beats 25%. A My preset is emptied.", reset);

            // The values this show is played with.
            Heading("Scenes");
            bool making = ReferenceEquals(show, _showBeingBuilt);
            panel.Children.Add(new TextBlock
            {
                Text = making
                    ? "The scene on screen stays. Scenes not yet shown are made again with these."
                    : "Scenes already in the storyboard stay as they are. These are used for scenes Play has not made yet.",
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
            var fewestSeconds = Box(SurpriseOptions.FewestSeconds, SurpriseOptions.MostSeconds, values.AutoPlayRandom ? values.RandomFewestSeconds : values.AutoPlaySeconds);
            var mostSeconds = Box(SurpriseOptions.FewestSeconds, SurpriseOptions.MostSeconds, values.AutoPlayRandom ? values.RandomMostSeconds : values.AutoPlaySeconds);
            void SetSeconds() => ChangeShowValues(v =>
            {
                v.FewestSeconds = Math.Min(fewestSeconds.Value, mostSeconds.Value);
                v.MostSeconds = Math.Max(fewestSeconds.Value, mostSeconds.Value);
            }, changed);
            fewestSeconds.ValueChanged += (_, _) => SetSeconds();
            mostSeconds.ValueChanged += (_, _) => SetSeconds();
            Row("Duration, seconds", "How long each scene holds: each picks within the range.", Pair(fewestSeconds, mostSeconds));

            var smart = new CheckBox { IsChecked = values.SmartPace, MinWidth = 0 };
            smart.Checked += (_, _) =>
            {
                if (GeneratorSettingsFor(_activeShow).SmartPace) return;
                ChangeShowValues(v => v.SmartPace = true, changed);
            };
            smart.Unchecked += (_, _) =>
            {
                if (!GeneratorSettingsFor(_activeShow).SmartPace) return;
                ChangeShowValues(v => v.SmartPace = false, changed);
            };
            Row("Smart pace", "Dense layouts toward the longer end of the range, layouts of one or two images toward the shorter.", smart);

            var motionLeast = Box(0, 100, (int)Math.Round(values.MotionLeast * 100));
            var motionMost = Box(0, 100, (int)Math.Round(values.MotionMost * 100));
            void SetMotion() => ChangeShowValues(v =>
            {
                v.MotionLeast = Math.Min(motionLeast.Value, motionMost.Value) / 100.0;
                v.MotionMost = Math.Max(motionLeast.Value, motionMost.Value) / 100.0;
            }, changed);
            motionLeast.ValueChanged += (_, _) => SetMotion();
            motionMost.ValueChanged += (_, _) => SetMotion();
            Row("Motion, %", "How far the camera moves in each scene: each picks within the range. 0 is still.", Pair(motionLeast, motionMost));

            var beats = Box(0, 100, (int)Math.Round(values.SequenceBeatsChance * 100));
            Row("Sequence beats, %", "The chance a step not yet shown is more than one scene: a group, then one image from it, then that image's faces. One picture with no face stays one scene. At 0, every step is one scene and Scenes per step is not used.", beats);

            var fewestScenes = Box(2, SurpriseOptions.MostScenesInAStep, values.FewestScenesPerStep);
            var mostScenes = Box(2, SurpriseOptions.MostScenesInAStep, values.MostScenesPerStep);
            void SetScenes() => ChangeShowValues(v =>
            {
                v.FewestScenesPerStep = Math.Min(fewestScenes.Value, mostScenes.Value);
                v.MostScenesPerStep = Math.Max(fewestScenes.Value, mostScenes.Value);
            }, changed);
            fewestScenes.ValueChanged += (_, _) => SetScenes();
            mostScenes.ValueChanged += (_, _) => SetScenes();
            var scenesRow = Row("Scenes per step", "How many scenes a step may be when Sequence beats rolls, from 2 to 4. Nothing happens while Sequence beats is 0. The step stops early when no closer view is left.", Pair(fewestScenes, mostScenes));
            void ShowScenesUsed()
            {
                bool used = beats.Value > 0;
                fewestScenes.IsEnabled = used;
                mostScenes.IsEnabled = used;
                scenesRow.Opacity = used ? 1 : 0.45;
            }
            ShowScenesUsed();
            beats.ValueChanged += (_, value) =>
            {
                ChangeShowValues(v => v.SequenceBeats = Math.Clamp(value, 0, 100) / 100.0, changed);
                ShowScenesUsed();
            };

            Heading("Designs");
            foreach (string design in LayoutComposer.Recipes)
            {
                string named = design;
                double odds = values.IsDesignOff(named) ? 0 : values.OddsOf(named);
                var nearest = OddsSteps.OrderBy(s => Math.Abs(Math.Log(Math.Max(0.01, s.Odds)) - Math.Log(Math.Max(0.01, odds)))).First().Name;
                var pick = new ComboBox { ItemsSource = OddsSteps.Select(s => s.Name).ToList(), SelectedItem = nearest, Width = 110 };
                pick.SelectionChanged += (_, _) =>
                {
                    if (pick.SelectedItem is not string step) return;
                    double chosen = OddsSteps.First(s => s.Name == step).Odds;
                    var live = GeneratorSettingsFor(_activeShow);
                    double now = live.IsDesignOff(named) ? 0 : live.OddsOf(named);
                    if (Math.Abs(chosen - now) < 0.0001) return;
                    ChangeShowValues(v =>
                    {
                        v.DesignOdds ??= new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        v.DesignOdds[named] = chosen;
                    }, changed);
                };
                Row(named, $"How often this show uses {named}. Off: never in this show.", pick);
            }
        }
    }
}

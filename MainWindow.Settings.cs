using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernImageViewer.Cinematic;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>The choices in the main window's options that apply across the app: music and the image editor.</summary>
    public sealed partial class MainWindow
    {
        public MusicChoices MyMusic => new(
            _currentSettings.MusicFolder, _currentSettings.MusicLowestStars, _currentSettings.MusicIncludesUnrated,
            _currentSettings.MusicInCinematic, _currentSettings.MusicInSurprise, _currentSettings.MusicVolume, _currentSettings.MusicCutOnBeat);

        public event Action? MusicSettingsChanged;

        /// <summary>Cut on the beat (design 3.10): a show's scene changes land on the music's beats.</summary>
        public void SetMusicCutOnBeat(bool on)
        {
            _currentSettings.MusicCutOnBeat = on;
            SaveAllSettings();
        }

        public void OpenMusicSettings() => MusicSettings_Click(this, new RoutedEventArgs());

        /// <summary>From a place that shows these beside the music itself, so the one set of settings stays in step.</summary>
        public void SetMusicInSurprise(bool on)
        {
            _currentSettings.MusicInSurprise = on;
            SaveAllSettings();
        }

        public void SetMusicVolume(double volume)
        {
            _currentSettings.MusicVolume = Math.Clamp(volume, 0, 100);
            SaveAllSettings();
            MusicSettingsChanged?.Invoke();
        }

        private async void MusicSettings_Click(object sender, RoutedEventArgs e)
        {
            HeaderSettingsFlyout.Hide();
            string? folder = _currentSettings.MusicFolder;
            var (rating, unrated) = MusicChoices.RatingControls(_currentSettings.MusicLowestStars, _currentSettings.MusicIncludesUnrated);
            var inCinematic = SettingsCard.Switch("Cinematic shows", _currentSettings.MusicInCinematic);
            var inSurprise = SettingsCard.Switch("Auto-Mix", _currentSettings.MusicInSurprise);
            var volume = new Slider { Minimum = 0, Maximum = 100, Value = _currentSettings.MusicVolume, Width = 150, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(volume, "Music volume");
            var volumeText = new TextBlock { Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = SettingsCard.Quiet };
            void ShowVolume() => volumeText.Text = $"{volume.Value:0}%";
            ShowVolume();
            volume.ValueChanged += (_, _) => ShowVolume();
            var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            volumeRow.Children.Add(volume);
            volumeRow.Children.Add(volumeText);

            var choose = new Button { VerticalAlignment = VerticalAlignment.Center };
            var clear = new Button { Content = "No music", VerticalAlignment = VerticalAlignment.Center };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(choose);
            buttons.Children.Add(clear);
            var (folderRow, folderTitle, folderDetail) = SettingsCard.Row("\uE8B7", string.Empty, string.Empty, buttons);
            folderDetail.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "MusicFolderText");

            void ShowFolder(string? problem = null)
            {
                folderTitle.Text = folder == null ? "No folder chosen" : Path.GetFileName(folder.TrimEnd('\\'));
                folderDetail.Text = problem ?? folder ?? "Choose where your songs are";
                ToolTipService.SetToolTip(folderDetail, folderDetail.Text);
                choose.Content = folder == null ? "Choose folder..." : "Change...";
                clear.Visibility = folder == null ? Visibility.Collapsed : Visibility.Visible;
                // The rest says what to do with a folder, so it waits for one.
                rating.IsEnabled = unrated.IsEnabled = inCinematic.IsEnabled = inSurprise.IsEnabled = volume.IsEnabled = folder != null;
            }
            ShowFolder();
            choose.Click += async (_, _) =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
                    picker.FileTypeFilter.Add("*");
                    var picked = await picker.PickSingleFolderAsync();
                    if (picked == null) return;
                    folder = picked.Path;
                    ShowFolder();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ShowFolder("Could not choose that folder. " + ex.Message);
                }
            };
            clear.Click += (_, _) =>
            {
                folder = null;
                ShowFolder();
            };

            var panel = new StackPanel { Width = 480, Padding = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(SettingsCard.Section("Songs", first: true));
            panel.Children.Add(SettingsCard.Card(
                folderRow,
                SettingsCard.Row("\uE734", "Lowest rating", MusicChoices.FormatsNote, rating).Row,
                SettingsCard.Row("\uE9CE", "Include unrated songs", null, unrated).Row));
            panel.Children.Add(SettingsCard.Section("Play during"));
            panel.Children.Add(SettingsCard.Card(
                SettingsCard.Row("\uE714", "Cinematic shows", "A saved show with its own music plays that instead.", inCinematic).Row,
                SettingsCard.Row("\uE8B1", "Auto-Mix", null, inSurprise).Row));
            panel.Children.Add(SettingsCard.Section("Sound"));
            panel.Children.Add(SettingsCard.Card(SettingsCard.Row("\uE767", "Volume", null, volumeRow).Row));

            var dialog = new ContentDialog
            {
                Title = "Music",
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = CurrentRequestedTheme
            };
            DialogFooter.MainButtonOnRight(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            _currentSettings.MusicFolder = folder;
            _currentSettings.MusicLowestStars = MusicChoices.ChosenStars(rating);
            _currentSettings.MusicIncludesUnrated = unrated.IsOn;
            _currentSettings.MusicInCinematic = inCinematic.IsOn;
            _currentSettings.MusicInSurprise = inSurprise.IsOn;
            _currentSettings.MusicVolume = volume.Value;
            SaveAllSettings();
            MusicSettingsChanged?.Invoke();
            SyncOptionsLabels();
        }

        private async void ImageEditorSettings_Click(object sender, RoutedEventArgs e)
        {
            HeaderSettingsFlyout.Hide();
            string editor = ImageEditorPath;
            var choose = new Button { Content = "Change...", VerticalAlignment = VerticalAlignment.Center };
            var paint = new Button { Content = "Use Paint", VerticalAlignment = VerticalAlignment.Center };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(choose);
            buttons.Children.Add(paint);
            var (row, name, detail) = SettingsCard.Row("\uE70F", EditorName(editor), "Edit Image opens images in this program.", buttons);
            name.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "ImageEditorText");
            choose.Click += async (_, _) =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                    picker.FileTypeFilter.Add(".exe");
                    var picked = await picker.PickSingleFileAsync();
                    if (picked == null) return;
                    editor = picked.Path;
                    name.Text = EditorName(editor);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    detail.Text = "Could not choose that program. " + ex.Message;
                }
            };
            paint.Click += (_, _) =>
            {
                editor = DefaultEditor;
                name.Text = EditorName(editor);
            };
            var panel = new StackPanel { Width = 480 };
            panel.Children.Add(SettingsCard.Card(row));

            var dialog = new ContentDialog
            {
                Title = "Image editor",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = CurrentRequestedTheme
            };
            DialogFooter.MainButtonOnRight(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            ImageEditorPath = editor;
            SaveAllSettings();
            SyncOptionsLabels();
        }

        private const string DefaultEditor = "mspaint.exe";

        private static string EditorName(string? editor) =>
            string.IsNullOrWhiteSpace(editor) || editor.Equals(DefaultEditor, StringComparison.OrdinalIgnoreCase)
                ? "Paint"
                : Path.GetFileNameWithoutExtension(editor);

        private void SyncOptionsLabels()
        {
            if (MusicOptionText != null)
            {
                var music = MyMusic;
                MusicOptionText.Text = music.HasFolder ? "Music: " + music.Summary : "Music: none";
            }
            if (ImageEditorOptionText != null) ImageEditorOptionText.Text = "Image editor: " + EditorName(ImageEditorPath);
        }
    }

    /// <summary>The music set in the options, and the controls for a rating, which a show's own music uses too.</summary>
    public sealed record MusicChoices(string? Folder, double LowestStars, bool IncludesUnrated, bool InCinematic, bool InSurprise, double Volume, bool CutOnBeat = true)
    {
        public const string FormatsNote = "Ratings are read from MP3 and FLAC tags. M4A and WMA count as unrated.";

        public const string NoFolder = "No music folder";

        public bool HasFolder => !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder);

        /// <summary>The same words wherever these settings are shown.</summary>
        public string Summary => HasFolder
            ? $"{Path.GetFileName(Folder!.TrimEnd('\\'))}, {LowestStars:0.#}+ stars" + (IncludesUnrated ? " and unrated" : "")
            : NoFolder;

        public static (ComboBox Rating, ToggleSwitch Unrated) RatingControls(double lowestStars, bool includeUnrated)
        {
            var rating = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rating, "Lowest rating");
            for (double stars = 0.5; stars <= 5; stars += 0.5)
                rating.Items.Add(stars == 1 ? "1 star" : $"{stars:0.#} stars");
            rating.SelectedIndex = (int)Math.Clamp(Math.Round(lowestStars * 2) - 1, 0, 9);
            return (rating, SettingsCard.Switch("Include unrated songs", includeUnrated));
        }

        public static double ChosenStars(ComboBox rating) => (Math.Max(0, rating.SelectedIndex) + 1) / 2.0;
    }

    /// <summary>
    /// Settings in dialogs: small section headers as in About, and rows grouped on cards as on the app's other panels.
    /// A row is an icon, a title with an optional line under it, and its control at the right.
    /// </summary>
    public static class SettingsCard
    {
        /// <summary>Secondary text and icons. Taken from the dialog's own text color: a brush looked up in code would follow the app-wide theme, not the dialog's.</summary>
        public const double Quiet = 0.7;

        private static T FromXaml<T>(string attributes) where T : class =>
            (T)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" {attributes}/>");

        public static TextBlock Section(string text, bool first = false) => new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Opacity = Quiet,
            Margin = new Thickness(2, first ? 0 : 16, 0, 6)
        };

        public static ToggleSwitch Switch(string name, bool on)
        {
            var toggle = new ToggleSwitch { IsOn = on, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
            return toggle;
        }

        public static (Grid Row, TextBlock Title, TextBlock Detail) Row(string glyph, string title, string? detail, FrameworkElement control)
        {
            var row = new Grid { ColumnSpacing = 12, MinHeight = 44, Padding = new Thickness(14, 8, 14, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Opacity = Quiet };
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var name = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
            var line = new TextBlock { Text = detail ?? string.Empty, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = Quiet };
            words.Children.Add(name);
            words.Children.Add(line);
            line.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => line.Visibility = string.IsNullOrEmpty(line.Text) ? Visibility.Collapsed : Visibility.Visible);
            line.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(words, 1);
            Grid.SetColumn(control, 2);
            row.Children.Add(icon);
            row.Children.Add(words);
            row.Children.Add(control);
            return (row, name, line);
        }

        public static Border Card(params FrameworkElement[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(FromXaml<Border>("Height=\"1\" Background=\"{ThemeResource DividerStrokeColorDefaultBrush}\""));
                stack.Children.Add(rows[i]);
            }
            var card = FromXaml<Border>("CornerRadius=\"8\" BorderThickness=\"1\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"");
            card.Child = stack;
            return card;
        }
    }
}

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
            _currentSettings.MusicInCinematic, _currentSettings.MusicInSurprise, _currentSettings.MusicVolume);

        public event Action? MusicSettingsChanged;

        public void OpenMusicSettings() => MusicSettings_Click(this, new RoutedEventArgs());

        /// <summary>From a place that shows these beside the music itself, so the one set of settings stays in step.</summary>
        public void SetMusicInSurprise(bool on)
        {
            _currentSettings.MusicInSurprise = on;
            SaveAllSettings();
        }

        private async void MusicSettings_Click(object sender, RoutedEventArgs e)
        {
            HeaderSettingsFlyout.Hide();
            string? folder = _currentSettings.MusicFolder;
            var (rating, unrated) = MusicChoices.RatingControls(_currentSettings.MusicLowestStars, _currentSettings.MusicIncludesUnrated);
            var inCinematic = new CheckBox { Content = "Cinematic shows", IsChecked = _currentSettings.MusicInCinematic };
            var inSurprise = new CheckBox { Content = "Surprise me auto-play", IsChecked = _currentSettings.MusicInSurprise };
            var volume = new Slider { Header = "Volume", Minimum = 0, Maximum = 100, Value = _currentSettings.MusicVolume };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(volume, "Music volume");

            var where = new TextBlock { Text = folder ?? "No folder chosen", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            where.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "MusicFolderText");
            var choose = new Button { Content = "Choose folder..." };
            var clear = new Button { Content = "No music", IsEnabled = folder != null };
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
                    where.Text = folder;
                    clear.IsEnabled = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    where.Text = "Could not choose that folder. " + ex.Message;
                }
            };
            clear.Click += (_, _) =>
            {
                folder = null;
                where.Text = "No folder chosen";
                clear.IsEnabled = false;
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(choose);
            buttons.Children.Add(clear);

            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(where);
            panel.Children.Add(buttons);
            panel.Children.Add(rating);
            panel.Children.Add(unrated);
            panel.Children.Add(new TextBlock { Text = MusicChoices.FormatsNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });
            panel.Children.Add(new TextBlock { Text = "Play it during", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            panel.Children.Add(inCinematic);
            panel.Children.Add(inSurprise);
            panel.Children.Add(volume);
            panel.Children.Add(new TextBlock { Text = "A saved show can have its own music, which comes first. Songs play in random order.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });

            var dialog = new ContentDialog
            {
                Title = "Music",
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            _currentSettings.MusicFolder = folder;
            _currentSettings.MusicLowestStars = MusicChoices.ChosenStars(rating);
            _currentSettings.MusicIncludesUnrated = unrated.IsChecked == true;
            _currentSettings.MusicInCinematic = inCinematic.IsChecked == true;
            _currentSettings.MusicInSurprise = inSurprise.IsChecked == true;
            _currentSettings.MusicVolume = volume.Value;
            SaveAllSettings();
            MusicSettingsChanged?.Invoke();
            SyncOptionsLabels();
        }

        private async void ImageEditorSettings_Click(object sender, RoutedEventArgs e)
        {
            HeaderSettingsFlyout.Hide();
            string editor = ImageEditorPath;
            var name = new TextBlock { Text = EditorName(editor), TextWrapping = TextWrapping.Wrap };
            name.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "ImageEditorText");
            var choose = new Button { Content = "Choose program..." };
            var paint = new Button { Content = "Use Paint" };
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
                    name.Text = "Could not choose that program. " + ex.Message;
                }
            };
            paint.Click += (_, _) =>
            {
                editor = DefaultEditor;
                name.Text = EditorName(editor);
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(choose);
            buttons.Children.Add(paint);
            var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
            panel.Children.Add(name);
            panel.Children.Add(buttons);

            var dialog = new ContentDialog
            {
                Title = "Image editor",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
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
    public sealed record MusicChoices(string? Folder, double LowestStars, bool IncludesUnrated, bool InCinematic, bool InSurprise, double Volume)
    {
        public const string FormatsNote = "Ratings are read from MP3 and FLAC tags. M4A and WMA count as unrated.";

        public const string NoFolder = "No music folder";

        public bool HasFolder => !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder);

        /// <summary>The same words wherever these settings are shown.</summary>
        public string Summary => HasFolder
            ? $"{Path.GetFileName(Folder!.TrimEnd('\\'))}, {LowestStars:0.#}+ stars" + (IncludesUnrated ? " and unrated" : "")
            : NoFolder;

        public static (ComboBox Rating, CheckBox Unrated) RatingControls(double lowestStars, bool includeUnrated)
        {
            var rating = new ComboBox { Header = "Lowest rating", HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rating, "Lowest rating");
            for (double stars = 0.5; stars <= 5; stars += 0.5)
                rating.Items.Add(stars == 1 ? "1 star" : $"{stars:0.#} stars");
            rating.SelectedIndex = (int)Math.Clamp(Math.Round(lowestStars * 2) - 1, 0, 9);
            return (rating, new CheckBox { Content = "Include unrated songs", IsChecked = includeUnrated });
        }

        public static double ChosenStars(ComboBox rating) => (Math.Max(0, rating.SelectedIndex) + 1) / 2.0;
    }
}

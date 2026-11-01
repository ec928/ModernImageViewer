using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernImageViewer.Cinematic
{
    /// <summary>
    /// A show can have its own music or none. Otherwise it plays the music set in the main window's options.
    /// It waits while the show is paused or being edited.
    /// </summary>
    public sealed partial class CinematicWindow
    {
        private MusicBed? _music;

        private void SetUpMusic()
        {
            _music = new MusicBed(DispatcherQueue);
            _music.NowPlayingChanged += ViewModel.SetNowPlaying;
            _music.GaveUp += (title, why) => _ = ReportMusicStoppedAsync(title, why);
            if (MainWindow.Instance != null) MainWindow.Instance.MusicSettingsChanged += ApplyMusic;
        }

        private void ApplyMusic()
        {
            if (_music == null) return;
            ApplyMusicVolume();
            var mine = MainWindow.Instance?.MyMusic;
            if (ViewModel.MusicIsOff) _music.Stop();
            else if (ViewModel.IsMusicFolder) _music.PlayFolder(ViewModel.MusicFolder!, ViewModel.MusicLowestStars, ViewModel.MusicIncludesUnrated);
            else if (!string.IsNullOrEmpty(ViewModel.MusicPath)) _music.PlayFile(ViewModel.MusicPath!);
            else if (mine != null && mine.InCinematic && mine.HasFolder) _music.PlayFolder(mine.Folder!, mine.LowestStars, mine.IncludesUnrated);
            else _music.Stop();
            ViewModel.SetMusicState(_music.IsOn, _music.IsFolder);
            SyncMusicPlayback();
        }

        private void ApplyMusicVolume() => _music?.SetVolume(ViewModel.MusicVolume);

        private void SyncMusicPlayback() => _music?.SetPlaying(ViewModel.IsPlaying && !_editing);

        private void DisposeMusic()
        {
            if (MainWindow.Instance != null) MainWindow.Instance.MusicSettingsChanged -= ApplyMusic;
            _music?.Dispose();
            _music = null;
        }

        private async Task ReportMusicStoppedAsync(string title, string why)
        {
            ViewModel.SetMusic(null);
            if (RootGrid.XamlRoot != null)
                await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, title, "The show carries on without music. " + why);
        }

        private void BtnNextSong_Click(object sender, RoutedEventArgs e) => _music?.NextSong();
        private void BtnPreviousSong_Click(object sender, RoutedEventArgs e) => _music?.PreviousSong();

        private async Task ChooseMusicFileAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
                foreach (var type in MusicLibrary.Extensions) picker.FileTypeFilter.Add(type);
                StorageFile file = await picker.PickSingleFileAsync();
                if (file != null) ViewModel.SetMusic(file.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Could not choose that", ex.Message);
            }
        }

        private async Task ChooseMusicFolderAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");
                StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder != null) await AskMusicRatingAsync(folder.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "Could not choose that", ex.Message);
            }
        }

        private async Task AskMusicRatingAsync(string folder)
        {
            var mine = MainWindow.Instance?.MyMusic;
            double startStars = ViewModel.IsMusicFolder ? ViewModel.MusicLowestStars : mine?.LowestStars ?? 3.5;
            bool startUnrated = ViewModel.IsMusicFolder ? ViewModel.MusicIncludesUnrated : mine?.IncludesUnrated ?? false;
            var (rating, unrated) = MusicChoices.RatingControls(startStars, startUnrated);

            var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
            panel.Children.Add(rating);
            panel.Children.Add(unrated);
            panel.Children.Add(new TextBlock { Text = MusicChoices.FormatsNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });

            var dialog = new ContentDialog
            {
                Title = "Music for this show: " + Path.GetFileName(folder.TrimEnd('\\')),
                Content = panel,
                PrimaryButtonText = "Play",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.SetMusicFolder(folder, MusicChoices.ChosenStars(rating), unrated.IsChecked == true);
        }

        private void BtnMusic_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            void Add(string text, Action pressed)
            {
                var item = new MenuFlyoutItem { Text = text };
                item.Click += (_, _) => pressed();
                menu.Items.Add(item);
            }
            var mine = MainWindow.Instance?.MyMusic;
            bool ownMusic = ViewModel.IsMusicFolder || !string.IsNullOrEmpty(ViewModel.MusicPath);
            if (mine != null && mine.HasFolder && (ownMusic || ViewModel.MusicIsOff || !mine.InCinematic))
                Add("Use my music", ViewModel.UseMyMusic);
            Add("Music from a folder...", () => _ = ChooseMusicFolderAsync());
            Add("One music file...", () => _ = ChooseMusicFileAsync());
            if (ViewModel.IsMusicFolder) Add("Change rating...", () => _ = AskMusicRatingAsync(ViewModel.MusicFolder!));
            if (_music?.IsOn == true) Add("No music for this show", () => ViewModel.SetMusic(null));
            menu.ShowAt(BtnMusic);
        }
    }
}

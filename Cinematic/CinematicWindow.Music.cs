using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace ModernImageViewer.Cinematic
{
    /// <summary>One music file under the show. It loops while the show plays, and waits while the show is paused or being edited.</summary>
    public sealed partial class CinematicWindow
    {
        private MediaPlayer? _music;

        private void ApplyMusic()
        {
            DisposeMusic();
            if (!ViewModel.HasMusic) return;
            try
            {
                _music = new MediaPlayer { IsLoopingEnabled = true };
                _music.MediaFailed += Music_Failed;
                _music.Source = MediaSource.CreateFromUri(new Uri(ViewModel.MusicPath!));
                ApplyMusicVolume();
                SyncMusicPlayback();
            }
            catch (Exception ex)
            {
                DisposeMusic();
                _ = ReportMusicFailedAsync(ex.Message);
            }
        }

        private void ApplyMusicVolume()
        {
            if (_music != null) _music.Volume = ViewModel.MusicVolume / 100.0;
        }

        private void SyncMusicPlayback()
        {
            if (_music == null) return;
            if (ViewModel.IsPlaying && !_editing) _music.Play();
            else _music.Pause();
        }

        private void DisposeMusic()
        {
            if (_music == null) return;
            _music.MediaFailed -= Music_Failed;
            _music.Dispose();
            _music = null;
        }

        private void Music_Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() => _ = ReportMusicFailedAsync(args.ErrorMessage));
        }

        private async Task ReportMusicFailedAsync(string why)
        {
            ViewModel.SetMusic(null);
            if (RootGrid.XamlRoot != null)
                await ViewerEngine.ShowMessageAsync(RootGrid.XamlRoot, "That music will not play", "The show carries on without it. " + why);
        }

        private async Task ChooseMusicAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
                foreach (var type in new[] { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac" })
                    picker.FileTypeFilter.Add(type);
                StorageFile file = await picker.PickSingleFileAsync();
                if (file != null) ViewModel.SetMusic(file.Path);
            }
            catch (Exception ex)
            {
                await ReportMusicFailedAsync(ex.Message);
            }
        }

        private void BtnMusic_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasMusic)
            {
                _ = ChooseMusicAsync();
                return;
            }
            var menu = new MenuFlyout();
            var choose = new MenuFlyoutItem { Text = "Choose other music..." };
            choose.Click += (_, _) => _ = ChooseMusicAsync();
            var remove = new MenuFlyoutItem { Text = "Remove music" };
            remove.Click += (_, _) => ViewModel.SetMusic(null);
            menu.Items.Add(choose);
            menu.Items.Add(remove);
            menu.ShowAt(BtnMusic);
        }
    }
}

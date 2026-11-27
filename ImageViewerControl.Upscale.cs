using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// AI upscale in the viewer (Upscale extension): the user picks 2x or 4x, the upscaled picture replaces the original
    /// on screen at the same size and zoom, and the bar compares, saves a PNG copy beside the original, or discards it.
    /// Nothing is written until Save. Moving to another image, closing, or Discard drops it.
    /// </summary>
    public sealed partial class ImageViewerControl
    {
        /// <summary>Largest result offered, in pixels. Above this only the smaller scale (or none) is offered.</summary>
        private const long MaxUpscalePixels = 100_000_000;

        private sealed class UpscaleView
        {
            public required string Path { get; init; }
            public required int Scale { get; init; }
            public CanvasBitmap? Bitmap;
            public bool ShowUpscaled = true;
            public string? SavedAs;
        }

        private UpscaleView? _upscale;
        private CancellationTokenSource? _upscaleCts;
        private bool _upscaleRunning;
        private int _upscaleRunScale;
        private int _upscaleTicket;

        /// <summary>True while an upscale runs or is held for comparing; 3D effects are off meanwhile.</summary>
        private bool IsUpscaleHeld => _upscaleRunning || _upscale != null;

        private bool CanUseUpscale =>
            UpscaleHost.Available
            && TargetImage != null
            && !TargetImage.IsComicEntry
            && !string.IsNullOrEmpty(TargetImage.Path)
            && (_animationFrames == null || _animationFrames.Length <= 1)
            && !IsShowingTwoPages
            && _previewResourcesLoaded;

        /// <summary>Whether the result of <paramref name="scale"/> fits the card's largest bitmap and the pixel cap.</summary>
        private bool UpscaleFits(int scale)
        {
            double w = _logicalImageWidth, h = _logicalImageHeight;
            if (w <= 1 || h <= 1) return false;
            int maxSide = 16384;
            try { if (_canvasDevice != null) maxSide = _canvasDevice.MaximumBitmapSizeInPixels; } catch { }
            return w * scale <= maxSide && h * scale <= maxSide && w * h * scale * scale <= MaxUpscalePixels;
        }

        public void OnUpscaleExtensionChanged()
        {
            if (!UpscaleHost.Available && IsUpscaleHeld) DropUpscale(requestRender: true);
            UpdateUpscaleAvailability();
            UpdateSwayAvailability();
        }

        private void UpdateUpscaleAvailability()
        {
            bool available = CanUseUpscale && (UpscaleFits(2) || UpscaleFits(4));
            Visibility vis = available ? Visibility.Visible : Visibility.Collapsed;
            if (UpscaleButton != null) UpscaleButton.Visibility = vis;
            if (UpscaleMenu != null) UpscaleMenu.Visibility = vis;
            SyncUpscaleItems(UpscaleMenu2x, UpscaleMenu4x);
            SyncUpscaleItems(UpscaleHud2x, UpscaleHud4x);
        }

        private void SyncUpscaleItems(MenuFlyoutItem? two, MenuFlyoutItem? four)
        {
            if (two != null) SyncUpscaleItem(two, 2);
            if (four != null) SyncUpscaleItem(four, 4);
        }

        private void SyncUpscaleItem(MenuFlyoutItem item, int scale)
        {
            bool fits = UpscaleFits(scale);
            item.IsEnabled = fits;
            string size = $"{(long)_logicalImageWidth * scale} × {(long)_logicalImageHeight * scale}";
            item.Text = fits ? $"Upscale {scale}×  ({size})" : $"Upscale {scale}×  (too large)";
        }

        private void UpscaleFlyout_Opening(object sender, object e) => UpdateUpscaleAvailability();

        private void Upscale_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int scale))
                _ = StartUpscaleAsync(scale);
        }

        private async Task StartUpscaleAsync(int scale)
        {
            if (!CanUseUpscale || !UpscaleFits(scale) || TargetImage == null) return;

            // 3D effects and an upscale both run models on the card and both replace the picture; one at a time.
            if (_isSwayEnabled) ToggleSway();

            DropUpscale(requestRender: false);
            int ticket = ++_upscaleTicket;
            var cts = new CancellationTokenSource();
            _upscaleCts = cts;
            string path = TargetImage.Path;
            _upscaleRunning = true;
            _upscaleRunScale = scale;
            if (UpscaleProgress != null) UpscaleProgress.Value = 0;
            if (UpscalePercent != null) UpscalePercent.Text = "0%";
            SyncUpscaleChrome();
            UpdateSwayAvailability();

            var progress = new Progress<double>(p =>
            {
                if (ticket != _upscaleTicket) return;
                if (UpscaleProgress != null) UpscaleProgress.Value = p * 100;
                if (UpscalePercent != null) UpscalePercent.Text = $"{p * 100:0}%";
            });

            var clock = Stopwatch.StartNew();
            Contracts.IUpscaledImage? result = null;
            string? failure = null;
            try
            {
                result = await UpscaleHost.Service.UpscaleAsync(path, scale, progress, cts.Token);
                if (result == null) failure = "Could not upscale this image";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or IOException or InvalidOperationException or ArgumentException)
            {
                failure = "Could not upscale this image";
                Debug.WriteLine($"[Upscale] {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                if (ticket == _upscaleTicket)
                {
                    _upscaleRunning = false;
                    if (ReferenceEquals(_upscaleCts, cts)) _upscaleCts = null;
                }
                cts.Dispose();
            }

            if (ticket != _upscaleTicket || _swayUnloaded) return;
            if (TargetImage == null || !string.Equals(TargetImage.Path, path, StringComparison.OrdinalIgnoreCase)) return;

            if (result != null)
            {
                try
                {
                    var device = _canvasDevice ?? CanvasDevice.GetSharedDevice();
                    var bitmap = CanvasBitmap.CreateFromBytes(device, result.Bgra, result.Width, result.Height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Premultiplied);
                    _upscale = new UpscaleView { Path = path, Scale = scale, Bitmap = bitmap };
                    PlayTiming.Write($"Upscale: shown x{scale} {result.Width}x{result.Height} after {clock.ElapsedMilliseconds} ms");
                }
                catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.COMException)
                {
                    failure = "The upscaled image is too large for this graphics card";
                    Debug.WriteLine($"[Upscale] bitmap: {ex.GetType().Name} {ex.Message}");
                }
            }

            SyncUpscaleChrome();
            UpdateSwayAvailability();
            RequestRender();
            if (failure != null) ShowNotification(failure);
        }

        private CanvasBitmap? TryGetUpscaleBitmap() =>
            _upscale is { ShowUpscaled: true, Bitmap: { } bitmap } && TargetImage != null
            && string.Equals(_upscale.Path, TargetImage.Path, StringComparison.OrdinalIgnoreCase)
                ? bitmap
                : null;

        private void SyncUpscaleChrome()
        {
            bool running = _upscaleRunning;
            bool ready = !running && _upscale?.Bitmap != null;
            if (UpscaleHud != null) UpscaleHud.Visibility = running || ready ? Visibility.Visible : Visibility.Collapsed;
            if (UpscaleRunningPanel != null) UpscaleRunningPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (UpscaleReadyPanel != null) UpscaleReadyPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
            if (UpscaleRunningText != null) UpscaleRunningText.Text = $"Upscaling {_upscaleRunScale}×";

            if (ready && _upscale != null)
            {
                var size = _upscale.Bitmap!.SizeInPixels;
                if (UpscaleReadyText != null) UpscaleReadyText.Text = $"{_upscale.Scale}×  {size.Width} × {size.Height}";
                var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                if (UpscaleShowOriginalButton != null) UpscaleShowOriginalButton.Background = _upscale.ShowUpscaled ? clear : accent;
                if (UpscaleShowUpscaledButton != null) UpscaleShowUpscaledButton.Background = _upscale.ShowUpscaled ? accent : clear;
                bool saved = _upscale.SavedAs != null;
                if (UpscaleSaveText != null) UpscaleSaveText.Text = saved ? "Saved" : "Save";
                if (UpscaleSaveButton != null)
                {
                    UpscaleSaveButton.IsEnabled = !saved;
                    ToolTipService.SetToolTip(UpscaleSaveButton, saved ? "Saved as " + Path.GetFileName(_upscale.SavedAs) : "Save as a new PNG beside the original");
                }
            }
        }

        /// <summary>Cancels a run and forgets a held upscale.</summary>
        private void DropUpscale(bool requestRender)
        {
            ++_upscaleTicket;
            try { _upscaleCts?.Cancel(); } catch (ObjectDisposedException) { }
            _upscaleCts = null;
            _upscaleRunning = false;
            var old = _upscale;
            _upscale = null;
            if (old?.Bitmap != null && _effectStack.Crop != null && ReferenceEquals(_effectStack.Crop.Source, old.Bitmap))
                _effectStack.Crop.Source = _rawGpuBitmap;
            old?.Bitmap?.Dispose();
            SyncUpscaleChrome();
            if (requestRender && !_swayUnloaded) RequestRender();
        }

        private void OnUpscalePrepareForNewImage()
        {
            if (IsUpscaleHeld) DropUpscale(requestRender: false);
            UpdateUpscaleAvailability();
        }

        /// <summary>A sharper decode of the same picture keeps the upscale; a different picture drops it.</summary>
        private void OnUpscaleImageInjected()
        {
            if (IsUpscaleHeld && TargetImage != null && _upscale != null
                && !string.Equals(_upscale.Path, TargetImage.Path, StringComparison.OrdinalIgnoreCase))
                DropUpscale(requestRender: false);
            UpdateUpscaleAvailability();
        }

        private void CleanupUpscaleOnUnload() => DropUpscale(requestRender: false);

        private void HandleUpscaleDeviceLost()
        {
            if (IsUpscaleHeld)
            {
                DropUpscale(requestRender: false);
                ShowNotification("Upscale discarded: the graphics device was reset");
            }
        }

        private void UpscaleCancel_Click(object sender, RoutedEventArgs e)
        {
            DropUpscale(requestRender: true);
            UpdateSwayAvailability();
            ShowNotification("Upscale cancelled");
        }

        private void UpscaleDiscard_Click(object sender, RoutedEventArgs e)
        {
            DropUpscale(requestRender: true);
            UpdateSwayAvailability();
            ShowHud();
        }

        private void UpscaleShowOriginal_Click(object sender, RoutedEventArgs e) => SetUpscaleShown(false);

        private void UpscaleShowUpscaled_Click(object sender, RoutedEventArgs e) => SetUpscaleShown(true);

        private void SetUpscaleShown(bool upscaled)
        {
            if (_upscale == null) return;
            _upscale.ShowUpscaled = upscaled;
            SyncUpscaleChrome();
            RequestRender();
        }

        private async void UpscaleSave_Click(object sender, RoutedEventArgs e)
        {
            var view = _upscale;
            if (view?.Bitmap == null || view.SavedAs != null) return;
            if (UpscaleSaveButton != null) UpscaleSaveButton.IsEnabled = false;
            string? target = null;
            bool created = false;
            try
            {
                target = UpscaleSavePath(view.Path, view.Scale);
                // CreateNew: never replaces a file, even one that appeared since the name was chosen.
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using var stream = fs.AsRandomAccessStream();
                    await view.Bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                }
                view.SavedAs = target;
                ShowNotification("Saved " + Path.GetFileName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
            {
                // A write that failed part-way leaves a broken file; remove it, and only a file this save created.
                if (created && target != null) { try { File.Delete(target); } catch { } }
                ShowNotification("Could not save: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_upscale, view)) SyncUpscaleChrome();
            }
        }

        /// <summary>"name (2x).png" beside the original, or "name (2x) (2).png" and so on when that exists.</summary>
        internal static string UpscaleSavePath(string original, int scale)
        {
            string dir = Path.GetDirectoryName(original) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(original) + $" ({scale}x)";
            string candidate = Path.Combine(dir, stem + ".png");
            for (int n = 2; File.Exists(candidate); n++)
                candidate = Path.Combine(dir, $"{stem} ({n}).png");
            return candidate;
        }

        private void UpscaleHud_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;
    }
}

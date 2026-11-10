using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Globalization;

namespace ModernImageViewer
{
    /// <summary>
    /// Prototype: the Sway rotation on the play bar, so each picture's most before it distorts can be set while it plays.
    /// The value applies to the playing picture on the next frame. The settings file is written once changes stop, and
    /// without dropping the scene being prepared (sway does not change a layout).
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private bool _swayBarSyncing;
        private DispatcherTimer? _swayBarSave;

        private void InitSwayBar()
        {
            if (!DepthMaps.Available) return;
            SwayBarControl.Visibility = Visibility.Visible;
            ShowSwayOnBar(SurpriseChoices.SwayRotation);
        }

        /// <summary>Puts a sway (a fraction of the width) on the bar's slider and readout without counting as a change.</summary>
        private void ShowSwayOnBar(double fraction)
        {
            double percent = Math.Round(SurpriseOptions.ClampedSwayRotation(fraction) * 100, 1);
            _swayBarSyncing = true;
            SwayBarSlider.Value = percent;
            _swayBarSyncing = false;
            SwayBarText.Text = percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private void SwayBarSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_swayBarSyncing || SwayBarText == null) return;
            SetSwayFromBar(e.NewValue);
        }

        private void SwayStep_Click(object sender, RoutedEventArgs e)
        {
            int way = sender is FrameworkElement { Tag: string tag } && tag == "-1" ? -1 : 1;
            double percent = SurpriseOptions.ClampedSwayRotation(SurpriseChoices.SwayRotation) * 100 + way * 0.1;
            SetSwayFromBar(percent);
        }

        private void SetSwayFromBar(double percent)
        {
            double fraction = SurpriseOptions.ClampedSwayRotation(Math.Round(percent, 1) / 100);
            SurpriseChoices.SwayRotation = fraction;
            ShowSwayOnBar(fraction);
            if (_swayBarSave == null)
            {
                _swayBarSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _swayBarSave.Tick += (_, _) =>
                {
                    _swayBarSave!.Stop();
                    try
                    {
                        System.IO.Directory.CreateDirectory(AppPaths.LocalDirectory);
                        AtomicFile.WriteAllText(SurpriseOptionsPath, SurpriseChoices.ToJson());
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        // It still applies until the app closes.
                    }
                };
            }
            _swayBarSave.Stop();
            _swayBarSave.Start();
        }
    }
}

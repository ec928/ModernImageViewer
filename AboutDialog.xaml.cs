using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Reflection;

namespace ModernImageViewer
{
    /// <summary>
    /// Standard About dialog: what the app is, which build this is, and the two routes
    /// back to the project. Replaces a bare "?" button that jumped straight to the issue
    /// tracker with no explanation of where it was sending you.
    /// </summary>
    public sealed partial class AboutDialog : ContentDialog
    {
        private const string RepositoryUrl = "https://github.com/ec928/ModernImageViewer";
        private const string NewIssueUrl = RepositoryUrl + "/issues/new/choose";

        public AboutDialog()
        {
            this.InitializeComponent();
            VersionText.Text = $"Version {GetDisplayVersion()}";
            try
            {
                string pngIcon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                string icon = System.IO.File.Exists(pngIcon)
                    ? pngIcon
                    : System.IO.Path.Combine(AppContext.BaseDirectory, "ModernImageViewer.ico");
                if (System.IO.File.Exists(icon))
                    AppIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[About] Could not load the icon: {ex.Message}");
            }
            ApplyHeroWash();
        }

        private void ApplyHeroWash()
        {
            Windows.UI.Color c = Windows.UI.Color.FromArgb(255, 0x00, 0x78, 0xD4);
            bool light = ActualTheme == ElementTheme.Light || RequestedTheme == ElementTheme.Light;
            var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1)
            };
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            {
                Color = Windows.UI.Color.FromArgb((byte)(light ? 0x58 : 0x78), c.R, c.G, c.B),
                Offset = 0
            });
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            {
                Color = Windows.UI.Color.FromArgb(0x10, c.R, c.G, c.B),
                Offset = 1
            });
            HeroTint.Background = brush;
        }

        /// <summary>
        /// Lays the six highlights out as two columns of three in a wider panel, which is about 100 px
        /// shorter. For windows wide enough to hold it; the single column stays for narrow ones.
        /// </summary>
        public void UseWideLayout()
        {
            if (FeatureGrid.ColumnDefinitions.Count > 2) return;
            RootPanel.Width = 700;
            FeatureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FeatureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Children come in pairs: icon, then words.
            for (int i = 0; i < FeatureGrid.Children.Count; i++)
            {
                if (FeatureGrid.Children[i] is not FrameworkElement el) continue;
                int item = i / 2;
                bool words = i % 2 == 1;
                Grid.SetRow(el, item % 3);
                Grid.SetColumn(el, (item / 3) * 2 + (words ? 1 : 0));
                if (!words && item >= 3) el.Margin = new Thickness(10, 2, 0, 0);
            }
        }

        /// <summary>
        /// Reads the version stamped by the project file rather than repeating it here.
        /// Trims the trailing revision so 0.11.0.0 reads as "0.11.0", matching the
        /// release tag and the zip name.
        /// </summary>
        private static string GetDisplayVersion()
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        private void ReportButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(NewIssueUrl);
        }

        private void ProjectButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(RepositoryUrl);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // A missing or broken browser association must not take the app down.
                Debug.WriteLine($"[About] Could not open {url}: {ex.Message}");
            }
        }
    }
}

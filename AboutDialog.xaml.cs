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

        // args.Cancel keeps the dialog open: following a link is not a reason to dismiss
        // the About box, and closing it would hide the version the user may be copying.
        private void ReportButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            OpenUrl(NewIssueUrl);
        }

        private void ProjectButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            OpenUrl(RepositoryUrl);
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

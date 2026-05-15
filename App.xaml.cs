using Microsoft.UI.Xaml;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ModernImageViewer
{
    public partial class App : Application
    {
        private Window? _window;

        // Centralized Memory & Cache Management
        public static ConcurrentDictionary<string, ImageCacheEntry> GlobalImageCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public static SemaphoreSlim GlobalCacheSemaphore { get; } = new(4, 4);

        public App()
        {
            InitializeComponent();
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Exception intercepted: {e.Exception.Message}");
            e.Handled = true;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            var mainWindow = (MainWindow)_window;

            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();

            if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
            {
                if (activatedArgs.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs && fileArgs.Files.Count > 0)
                {
                    mainWindow.StartupFilePath = fileArgs.Files[0].Path;
                }
            }
            else if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch)
            {
                string[] cmdArgs = Environment.GetCommandLineArgs();
                if (cmdArgs.Length > 1 && File.Exists(cmdArgs[1]))
                {
                    mainWindow.StartupFilePath = cmdArgs[1];
                }
            }

            _window.Activate();
        }
    }
}
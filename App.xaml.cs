using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace ModernImageViewer
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
            this.UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => LogCrash(a.ExceptionObject?.ToString());
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => LogCrash(a.Exception?.ToString());
        }

        private static void LogCrash(string? msg)
        {
            try
            {
                string logFile = System.IO.Path.Combine(AppPaths.LocalDirectory, "crash.log");
                System.IO.File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n\n");
            }
            catch { }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception?.ToString());
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Exception: {e.Exception}");
            try
            {
                ImageCache.Clear();
            }
            catch { }
            e.Handled = false;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            var mainWindow = (MainWindow)_window;

            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();

            if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
            {
                if (activatedArgs.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs
                    && fileArgs.Files.Count > 0)
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
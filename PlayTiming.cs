using System;
using System.Diagnostics;
using System.IO;

namespace ModernImageViewer
{
    /// <summary>
    /// A temporary diagnostic, off by default (Log Play timing, under Camera): how long Play takes to show its first
    /// picture, and how often a scene holds past its time because the next one was not ready. Timings only: no file
    /// names, paths or pictures. Written to play-timing.log in the app's own folder. To be removed once measured.
    /// </summary>
    internal static class PlayTiming
    {
        private static readonly Stopwatch Clock = new();

        public static bool On { get; private set; }

        public static string LogPath => Path.Combine(AppPaths.LocalDirectory, "play-timing.log");

        public static long Now => Clock.ElapsedMilliseconds;

        public static void Start(bool on)
        {
            On = on;
            if (!On) return;
            Clock.Restart();
            Write("Play pressed");
        }

        public static void Write(string what)
        {
            if (!On) return;
            try
            {
                Directory.CreateDirectory(AppPaths.LocalDirectory);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{Clock.ElapsedMilliseconds,8} ms\t{what}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A diagnostic never stops Play.
            }
        }
    }
}

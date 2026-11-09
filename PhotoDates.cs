using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernImageViewer
{
    /// <summary>
    /// When a photo was taken: the camera's date (System.Photo.DateTaken), or the file's last-written date when it has
    /// none. Read once per file and kept for the session. Used by a show's title and captions.
    /// </summary>
    public static class PhotoDates
    {
        private static readonly ConcurrentDictionary<string, DateTime> Known = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<DateTime> TakenAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return DateTime.MinValue;
            if (Known.TryGetValue(path, out var known)) return known;
            DateTime when = FileDate(path);
            try
            {
                if (!ComicArchive.IsEntryPath(path) && !ComicArchive.IsComicArchivePath(path) && File.Exists(path))
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var props = await file.Properties.RetrievePropertiesAsync(new[] { "System.Photo.DateTaken" });
                    if (props.TryGetValue("System.Photo.DateTaken", out object? value))
                    {
                        if (value is DateTimeOffset dto) when = dto.LocalDateTime;
                        else if (value is DateTime dt) when = dt;
                    }
                }
            }
            catch
            {
                // Fall back to file date on any read error
            }
            Known[path] = when;
            return when;
        }

        private static DateTime FileDate(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return File.GetLastWriteTime(path);
                return DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}

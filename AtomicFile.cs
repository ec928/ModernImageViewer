using System;
using System.IO;

namespace ModernImageViewer
{
    /// <summary>
    /// Writes a file so that a crash or power cut leaves the old file or the new one, never half of one.
    /// File.WriteAllText empties the file first, so anything that stops it part way loses the old content too.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string text)
        {
            string temp = $"{path}.{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllText(temp, text);
                // Something reading the file, such as a virus scanner, can hold it for a moment.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(temp, path, overwrite: true);
                        return;
                    }
                    catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
                    {
                        System.Threading.Thread.Sleep(30);
                    }
                }
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }
}

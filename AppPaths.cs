using System;
using System.IO;

namespace ModernImageViewer
{
    public static class AppPaths
    {
        public static string LocalDirectory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ModernImageViewer");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}

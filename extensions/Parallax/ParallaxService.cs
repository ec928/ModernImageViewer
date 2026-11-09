using ModernImageViewer.Contracts;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ModernImageViewer.Extensions.Parallax
{
    public sealed class ParallaxService : IParallaxService
    {
        public static string ExtensionDirectory =>
            Path.GetDirectoryName(typeof(ParallaxService).Assembly.Location) ?? AppContext.BaseDirectory;

        public static Action<string>? Logger
        {
            get => DepthMaps.Logger;
            set
            {
                DepthMaps.Logger = value;
                SubjectMasks.Logger = value;
            }
        }

        public bool IsAvailable => DepthMaps.Available;

        public async Task<IParallaxMap?> GetDepthMapAsync(string photoPath)
        {
            var map = await DepthMaps.ForAsync(photoPath).ConfigureAwait(false);
            return map;
        }

        public async Task<IParallaxMask?> GetSubjectMaskAsync(string photoPath)
        {
            var mask = await SubjectMasks.ForAsync(photoPath).ConfigureAwait(false);
            return mask;
        }

        public void DisposeSession()
        {
            DepthMaps.DisposeSession();
            SubjectMasks.DisposeSession();
        }
    }
}

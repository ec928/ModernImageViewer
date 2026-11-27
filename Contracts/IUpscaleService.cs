using System;
using System.Threading;
using System.Threading.Tasks;

namespace ModernImageViewer.Contracts
{
    /// <summary>An upscaled picture: BGRA8, premultiplied (fully opaque), row-major, Width x Height.</summary>
    public interface IUpscaledImage
    {
        byte[] Bgra { get; }
        int Width { get; }
        int Height { get; }
    }

    /// <summary>The Upscale extension: AI super-resolution of a whole photo, read from its file.</summary>
    public interface IUpscaleService
    {
        bool IsAvailable { get; }

        /// <summary>
        /// Upscales the photo at <paramref name="photoPath"/> by <paramref name="scale"/> (2 or 4), oriented and
        /// colour-managed to sRGB as the viewer shows it. Reports progress in [0, 1]. Null if it could not be done;
        /// throws OperationCanceledException when cancelled.
        /// </summary>
        Task<IUpscaledImage?> UpscaleAsync(string photoPath, int scale, IProgress<double>? progress, CancellationToken ct);

        void DisposeSession();
    }

    public sealed class NoOpUpscaleService : IUpscaleService
    {
        public static readonly NoOpUpscaleService Instance = new();
        public bool IsAvailable => false;
        public Task<IUpscaledImage?> UpscaleAsync(string photoPath, int scale, IProgress<double>? progress, CancellationToken ct) =>
            Task.FromResult<IUpscaledImage?>(null);
        public void DisposeSession() { }
    }
}

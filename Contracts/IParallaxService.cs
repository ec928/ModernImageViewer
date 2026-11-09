using System;
using System.Threading.Tasks;

namespace ModernImageViewer.Contracts
{
    public interface IParallaxMap
    {
        byte[] Grey { get; }
        int Width { get; }
        int Height { get; }
        float Middle { get; }
        float RawSpan { get; }
        byte[]? Raw { get; }
        byte[]? Light { get; }
    }

    public interface IParallaxMask
    {
        byte[] Alpha { get; }
        int Size { get; }
        bool Clear { get; }
        string Why { get; }
        float Area { get; }
        float Sides { get; }
        float Vague { get; }
        float Top { get; }
        float Bottom { get; }
    }

    public interface IParallaxService
    {
        bool IsAvailable { get; }
        Task<IParallaxMap?> GetDepthMapAsync(string photoPath);
        Task<IParallaxMask?> GetSubjectMaskAsync(string photoPath);
        void DisposeSession();
    }
}

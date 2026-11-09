using System;
using System.Threading.Tasks;

namespace ModernImageViewer.Contracts
{
    public sealed class NoOpParallaxService : IParallaxService
    {
        public static readonly NoOpParallaxService Instance = new();

        public bool IsAvailable => false;

        public Task<IParallaxMap?> GetDepthMapAsync(string photoPath) => Task.FromResult<IParallaxMap?>(null);

        public Task<IParallaxMask?> GetSubjectMaskAsync(string photoPath) => Task.FromResult<IParallaxMask?>(null);

        public void DisposeSession() { }
    }
}

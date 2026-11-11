// Sway as a forward warp, done as a gather. Each photo pixel s lands at s + shift * (depth(s) - pivot), along the row.
// For one output pixel, walk the sources within reach, find every segment whose landing crosses this pixel, and keep
// the nearest (largest depth). Nearer pixels cover farther ones, so an edge is drawn once, not twice. Where a gap
// opens behind a moving edge, the only crossing is the segment across the depth jump, so the edge is stretched over it.
//
// Input 0: the photo, clamped at its border. Input 1: the depth at photo size (red, 0 far .. 1 near), clamped.
// Compile (Windows SDK, see tools\Compile-Shaders.ps1):
//   fxc /nologo /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I <SDK>\um /Fo SwaySplat.bin SwaySplat.hlsl
#define D2D_INPUT_COUNT 2
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#include "d2d1effecthelpers.hlsli"

// Win2D sets these by name (PixelShaderEffect.Properties).
float shift;   // pixels at depth 1 against pivot, signed
float pivot;   // depth that stays still, 0..1
float stepPx;  // distance between sources tried, pixels
float reach;   // how far either side to look, pixels; at most the effect's MaxSamplerOffset

D2D_PS_ENTRY(main)
{
    float bestD = -1;
    float bestU = 0;
    float prevS = -reach;
    float prevD = D2DSampleInputAtOffset(1, float2(prevS, 0)).r;
    float prevL = prevS + shift * (prevD - pivot);
    int count = (int)ceil(2 * reach / stepPx);
    [loop] for (int i = 1; i <= count; i++)
    {
        float s = min(-reach + i * stepPx, reach);
        float d = D2DSampleInputAtOffset(1, float2(s, 0)).r;
        float L = s + shift * (d - pivot);
        if (min(prevL, L) <= 0 && max(prevL, L) >= 0)
        {
            float span = L - prevL;
            float t = abs(span) > 1e-4 ? -prevL / span : 0.5;
            float dd = lerp(prevD, d, t);
            if (dd > bestD)
            {
                bestD = dd;
                bestU = lerp(prevS, s, t);
            }
        }
        prevS = s; prevD = d; prevL = L;
    }
    if (bestD < 0)
    {
        // Nothing crossed (beyond reach): the old warp, reading depth here.
        bestU = -shift * (D2DSampleInputAtOffset(1, float2(0, 0)).r - pivot);
    }
    return D2DSampleInputAtOffset(0, float2(bestU, 0));
}

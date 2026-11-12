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
    float bestGrow = 1;   // how much the winning segment widened on landing; well above 1 means a gap opened there
    float bestFill = 0;   // the far end of that segment, then the background read for the gap
    float bestAway = 1;   // which way the background lies from the edge
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
                // A segment across a depth edge lands much wider than it was: the gap uncovered behind a moving edge.
                // Stretching it smears whatever sits on the edge (an ear, hair). The far end of the segment is the
                // background; read the background as far beyond that end as this pixel is from where the end lands.
                bestGrow = abs(span) / stepPx;
                bool prevFar = prevD < d;
                bestFill = prevFar ? prevS : s;
                bestAway = prevFar ? -1 : 1;
            }
        }
        prevS = s; prevD = d; prevL = L;
    }
    if (bestD < 0)
    {
        // Nothing crossed (beyond reach): the old warp, reading depth here.
        bestU = -shift * (D2DSampleInputAtOffset(1, float2(0, 0)).r - pivot);
    }
    else
    {
        // Twice as wide or less is an ordinary slope and stays a stretch; three times or more is a gap and shows background.
        float gapWeight = saturate(bestGrow - 2);
        if (gapWeight > 0)
        {
            // Walk outward from the edge until depth stops falling: there the background proper starts, past the soft
            // rim the depth map keeps around the subject. Every pixel of one gap finds the same start, so the
            // background mirrored about it is continuous (mirroring from each segment's own end gave a barcode).
            // The background start must not depend on which segment won, or neighbouring pixels mirror about different
            // points (a barcode). From the segment's near end, take the lowest depth over the next 16 steps, then the
            // first point whose depth is within 0.03 of it. The depth map's 8-bit plateaus cannot stop this early.
            float sStart = bestFill - bestAway * stepPx;
            float dMin = 1;
            [loop] for (int k = 0; k <= 16; k++)
                dMin = min(dMin, D2DSampleInputAtOffset(1, float2(clamp(sStart + bestAway * k * stepPx, -reach, reach), 0)).r);
            float sBg = sStart;
            [loop] for (int j = 0; j <= 16; j++)
            {
                sBg = clamp(sStart + bestAway * j * stepPx, -reach, reach);
                if (D2DSampleInputAtOffset(1, float2(sBg, 0)).r <= dMin + 0.03) break;
            }
            sBg += bestAway * stepPx;
            float dBg = D2DSampleInputAtOffset(1, float2(sBg, 0)).r;
            float lBg = sBg + shift * (dBg - pivot);
            // This pixel is |lBg| short of where that background start lands; read as far beyond the start.
            float fill = clamp(sBg + bestAway * abs(lBg), -reach, reach);
            // If the mirror reaches something nearer (the other ear, a second person), repeat the background edge instead.
            if (D2DSampleInputAtOffset(1, float2(fill, 0)).r > dBg + 0.08) fill = sBg;
            // Mix the two colours, not the two places: a place partway between them is some third pixel, a stray speck
            // that changed every frame (IMGP0997's arm, v0.35.4).
            return lerp(D2DSampleInputAtOffset(0, float2(bestU, 0)), D2DSampleInputAtOffset(0, float2(fill, 0)), gapWeight);
        }
    }
    return D2DSampleInputAtOffset(0, float2(bestU, 0));
}

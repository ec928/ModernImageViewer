// 2D & radial forward warp as a gather along the motion ray. Each photo pixel s lands at s + (mag + s*dolly) * (depth(s) - pivot)
// along dir = v / mag, covering horizontal Sway, 2-axis Orbit/Tilt (dolly == 0), and radial Breathe / Dolly Zoom (shift == 0).
// Compile (Windows SDK, see tools\Compile-Shaders.ps1):
//   fxc /nologo /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I <SDK>\um /Fo Sway2DSplat.bin Sway2DSplat.hlsl
#define D2D_INPUT_COUNT 2
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

// Win2D sets these by name (PixelShaderEffect.Properties).
float shiftX;   // horizontal shift at depth 1 against pivot, pixels, signed
float shiftY;   // vertical shift at depth 1 against pivot, pixels, signed
float dolly;    // radial parallax factor at depth 1 against pivot (Breathe mode), signed
float centerX;  // image center X in scene pixels
float centerY;  // image center Y in scene pixels
float pivot;    // depth that stays still, 0..1
float stepPx;   // distance between sources tried, pixels
float reach;    // maximum sampler offset radius, pixels

D2D_PS_ENTRY(main)
{
    float2 rel = D2DGetScenePosition().xy - float2(centerX, centerY);
    float2 v = float2(shiftX, shiftY) + rel * dolly;
    float mag = length(v);
    if (mag < 0.05 || reach < 0.5 || stepPx <= 0.0)
        return D2DSampleInputAtOffset(0, float2(0, 0));

    float2 dir = v / mag;
    float localReach = min(reach, mag * max(pivot, 1.0 - pivot) + stepPx * 2.0);
    if (localReach < 0.5)
        return D2DSampleInputAtOffset(0, float2(0, 0));

    float bestD = -1.0;
    float bestU = 0.0;
    float bestGrow = 1.0;
    float bestFill = 0.0;
    float bestAway = 1.0;

    float prevS = -localReach;
    float prevD = D2DSampleInputAtOffset(1, prevS * dir).r;
    float prevL = prevS + (mag + prevS * dolly) * (prevD - pivot);
    int count = max(1, (int)ceil(2.0 * localReach / stepPx));
    [loop] for (int i = 1; i <= count; i++)
    {
        float s = min(-localReach + i * stepPx, localReach);
        float d = D2DSampleInputAtOffset(1, s * dir).r;
        float L = s + (mag + s * dolly) * (d - pivot);
        if (min(prevL, L) <= 0.0 && max(prevL, L) >= 0.0)
        {
            float span = L - prevL;
            float t = abs(span) > 1e-4 ? -prevL / span : 0.5;
            float dd = lerp(prevD, d, t);
            if (dd > bestD)
            {
                bestD = dd;
                bestU = lerp(prevS, s, t);
                bestGrow = abs(span) / stepPx;
                bool prevFar = prevD < d;
                bestFill = prevFar ? prevS : s;
                bestAway = prevFar ? -1.0 : 1.0;
            }
        }
        prevS = s; prevD = d; prevL = L;
    }

    if (bestD < 0.0)
    {
        bestU = clamp(-mag * (D2DSampleInputAtOffset(1, float2(0, 0)).r - pivot), -localReach, localReach);
    }
    else
    {
        float gapWeight = saturate(bestGrow - 2.0);
        if (gapWeight > 0.0)
        {
            float sStart = bestFill - bestAway * stepPx;
            float dMin = 1.0;
            [loop] for (int k = 0; k <= 16; k++)
                dMin = min(dMin, D2DSampleInputAtOffset(1, clamp(sStart + bestAway * k * stepPx, -localReach, localReach) * dir).r);
            float sBg = sStart;
            [loop] for (int j = 0; j <= 16; j++)
            {
                sBg = clamp(sStart + bestAway * j * stepPx, -localReach, localReach);
                if (D2DSampleInputAtOffset(1, sBg * dir).r <= dMin + 0.03) break;
            }
            sBg = clamp(sBg + bestAway * stepPx, -localReach, localReach);
            float dBg = D2DSampleInputAtOffset(1, sBg * dir).r;
            float lBg = sBg + (mag + sBg * dolly) * (dBg - pivot);
            float fill = clamp(sBg + bestAway * abs(lBg), -localReach, localReach);
            if (D2DSampleInputAtOffset(1, fill * dir).r > dBg + 0.08) fill = sBg;
            return lerp(D2DSampleInputAtOffset(0, bestU * dir), D2DSampleInputAtOffset(0, fill * dir), gapWeight);
        }
    }
    return D2DSampleInputAtOffset(0, bestU * dir);
}

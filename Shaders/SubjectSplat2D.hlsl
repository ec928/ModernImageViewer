// 2D & radial forward warp for a decontaminated premultiplied RGBA subject cutout composited over an artificial backdrop.
// Input 0: premultiplied subject cutout RGBA (clamped).
// Input 1: dilated & smoothed subject depth 0..1 (red).
// Input 2: pre-rendered artificial backdrop RGBA (clamped, same pixel dimensions).
// Compile (Windows SDK, see tools\Compile-Shaders.ps1):
//   fxc /nologo /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I <SDK>\um /Fo SubjectSplat2D.bin SubjectSplat2D.hlsl
#define D2D_INPUT_COUNT 3
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_INPUT2_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

float shiftX;     // horizontal shift at depth 1 against pivot, pixels, signed
float shiftY;     // vertical shift at depth 1 against pivot, pixels, signed
float dolly;      // radial parallax factor at depth 1 against pivot (Breathe mode), signed
float centerX;    // image center X in scene pixels
float centerY;    // image center Y in scene pixels
float pivot;      // depth that stays still, 0..1
float stepPx;     // distance between sources tried, pixels
float reach;      // maximum sampler offset radius, pixels
float lightWrap;  // 0..1 rim light wrap strength from backdrop onto subject edges
float edgeShadow; // 0..1 subtle contact/drop shadow cast onto backdrop
float lightX;     // -1..1 light horizontal direction
float lightY;     // -1..1 light vertical direction

D2D_PS_ENTRY(main)
{
    float2 rel = D2DGetScenePosition().xy - float2(centerX, centerY);
    float2 v = float2(shiftX, shiftY) + rel * dolly;
    float mag = length(v);

    float4 fg = float4(0.0, 0.0, 0.0, 0.0);
    float2 fgPos = float2(0.0, 0.0);

    float localReach = mag >= 0.05 && stepPx > 0.0
        ? min(reach, mag * 0.25 + stepPx * 3.0)
        : 0.0;

    if (localReach < 0.5 || stepPx <= 0.0)
    {
        fg = D2DSampleInputAtOffset(0, float2(0.0, 0.0));
    }
    else
    {
        float2 dir = v / mag;
        float bestTopD = -1.0;
        float4 bestTopCol = float4(0.0, 0.0, 0.0, 0.0);
        float bestTopU = 0.0;
        float bestUnderD = -1.0;
        float4 bestUnderCol = float4(0.0, 0.0, 0.0, 0.0);

        float prevS = -localReach;
        float prevD = D2DSampleInputAtOffset(1, prevS * dir).r;
        float prevL = prevS + (mag + prevS * dolly) * (prevD - pivot);
        int count = max(1, (int)ceil(2.0 * localReach / stepPx));

        [loop] for (int i = 1; i <= count; i++)
        {
            float s = min(-localReach + i * stepPx, localReach);
            float d = D2DSampleInputAtOffset(1, s * dir).r;
            float L = s + (mag + s * dolly) * (d - pivot);
            if (prevL <= 0.0 && L >= 0.0)
            {
                float span = L - prevL;
                float t = span > 1e-4 ? -prevL / span : 0.5;
                float dd = lerp(prevD, d, t);
                float u = lerp(prevS, s, t);
                float4 c = D2DSampleInputAtOffset(0, u * dir);
                if (c.a > 0.004)
                {
                    if (dd > bestTopD)
                    {
                        if (bestTopCol.a > 0.85 && bestTopD > bestUnderD)
                        {
                            bestUnderD = bestTopD;
                            bestUnderCol = bestTopCol;
                        }
                        bestTopD = dd;
                        bestTopCol = c;
                        bestTopU = u;
                    }
                    if (c.a > 0.85 && dd < bestTopD && dd > bestUnderD)
                    {
                        bestUnderD = dd;
                        bestUnderCol = c;
                    }
                }
            }
            prevS = s;
            prevD = d;
            prevL = L;
        }

        if (bestTopD >= 0.0)
        {
            fg = bestTopCol;
            fgPos = bestTopU * dir;
            if (fg.a < 0.99 && bestUnderD >= 0.0 && bestUnderD < bestTopD)
            {
                float invA = 1.0 - fg.a;
                fg = float4(fg.rgb + bestUnderCol.rgb * invA, fg.a + bestUnderCol.a * invA);
            }
        }
    }

    float4 bg = D2DSampleInputAtOffset(2, float2(0.0, 0.0));
    float3 bgRgb = bg.a > 1e-4 ? (bg.rgb / bg.a) : bg.rgb;

    float refScale = clamp(max(centerX, centerY) / 600.0, 0.5, 2.2);

    if (edgeShadow > 0.001 && fg.a < 0.99)
    {
        float2 shCenter = float2(lightX * 11.0 * refScale, -8.0 * refScale);
        float shSpread = 6.5 * refScale;
        float sh0 = D2DSampleInputAtOffset(0, (shCenter + float2(-shSpread, -shSpread))).a;
        float sh1 = D2DSampleInputAtOffset(0, (shCenter + float2( shSpread, -shSpread))).a;
        float sh2 = D2DSampleInputAtOffset(0, (shCenter + float2(-shSpread,  shSpread))).a;
        float sh3 = D2DSampleInputAtOffset(0, (shCenter + float2( shSpread,  shSpread))).a;
        float shadowA = (sh0 + sh1 + sh2 + sh3) * 0.25;
        bgRgb *= (1.0 - shadowA * edgeShadow * 0.24);
    }

    float3 fgRgb = fg.a > 1e-4 ? (fg.rgb / fg.a) : float3(0.0, 0.0, 0.0);
    if (lightWrap > 0.001 && fg.a > 0.004)
    {
        float wr = 3.2 * refScale;
        float aL = D2DSampleInputAtOffset(0, (fgPos + float2(-wr,  0.0))).a;
        float aR = D2DSampleInputAtOffset(0, (fgPos + float2( wr,  0.0))).a;
        float aU = D2DSampleInputAtOffset(0, (fgPos + float2( 0.0, -wr))).a;
        float aD = D2DSampleInputAtOffset(0, (fgPos + float2( 0.0,  wr))).a;
        float minA = min(min(aL, aR), min(aU, aD));
        float rim = saturate(1.0 - minA) * fg.a;
        fgRgb = lerp(fgRgb, bgRgb * 0.80 + fgRgb * 0.20, rim * lightWrap * 0.42);
    }

    float3 outRgb = fgRgb * fg.a + bgRgb * (1.0 - fg.a);
    return float4(saturate(outRgb), 1.0);
}

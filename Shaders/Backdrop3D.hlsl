// Multi-plane procedural 3D backdrop generator driven by Sway / Slide / Orbit / Breathe / Tilt camera motion.
// Input 0: source-sized dummy surface (used only to establish exact output scene dimensions).
// Modes:
//   1 = StudioDark, 2 = StudioWarm, 3 = StudioMatch, 4 = Bokeh, 5 = Mist, 6 = NightSky
// Compile (Windows SDK, see tools\Compile-Shaders.ps1):
//   fxc /nologo /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I <SDK>\um /Fo Backdrop3D.bin Backdrop3D.hlsl
#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

float mode;
float widthPx;
float heightPx;
float shiftX;
float shiftY;
float dolly;
float blurAmount;
float dimAmount;
float desatAmount;
float topR;
float topG;
float topB;
float botR;
float botG;
float botB;
float accR;
float accG;
float accB;

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float2 Hash22(float2 p)
{
    float3 a = frac(p.xyx * float3(123.34, 234.34, 345.65));
    a += dot(a, a + 34.45);
    return frac(float2(a.x * a.y, a.y * a.z));
}

float Noise2D(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + float2(1.0, 0.0));
    float c = Hash21(i + float2(0.0, 1.0));
    float d = Hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float Fbm2(float2 p)
{
    float v = Noise2D(p) * 0.65;
    v += Noise2D(p * 2.07 + float2(13.1, 7.4)) * 0.35;
    return v;
}

float2 LayerPos(float2 baseP, float z, float minDim)
{
    float2 pan = float2(shiftX, shiftY) / minDim * z;
    float scale = max(0.50, 1.0 + dolly * z * 0.65);
    return baseP * scale + pan;
}

float3 BokehLayer(float2 lp, float gridScale, float zSeed, float blurAmt, float3 accCol)
{
    float2 g = lp * gridScale;
    float2 cell = floor(g);
    float2 f = frac(g);
    float3 sum = float3(0.0, 0.0, 0.0);

    float radScale = lerp(0.55, 1.30, saturate(blurAmt));
    float edgeSoft = lerp(0.05, 0.16, saturate(blurAmt));

    [loop] for (int dy = -1; dy <= 1; dy++)
    {
        [loop] for (int dx = -1; dx <= 1; dx++)
        {
            float2 id = cell + float2((float)dx, (float)dy);
            float2 rnd = Hash22(id + float2(zSeed * 17.3, zSeed * 31.7));
            float show = Hash21(id * 1.37 + float2(zSeed * 5.1, 9.2));
            if (show > 0.38)
            {
                float2 center = float2((float)dx, (float)dy) + 0.18 + rnd * 0.64 - f;
                float dist = length(center);
                float radius = lerp(0.18, 0.42, rnd.x) * radScale;
                float disc = 1.0 - smoothstep(max(0.01, radius - edgeSoft), radius + edgeSoft, dist);
                float ring = smoothstep(radius * 0.55, radius * 0.88, dist)
                           * (1.0 - smoothstep(radius * 0.88, radius + edgeSoft, dist));
                float alpha = (disc * 0.55 + ring * 0.35) * lerp(0.25, 0.90, rnd.y);

                float huePick = Hash21(id * 2.13 + zSeed);
                float3 warmGold = float3(1.00, 0.76, 0.42);
                float3 coolCyan = float3(0.45, 0.78, 1.00);
                float3 orbCol = huePick < 0.36 ? warmGold : (huePick < 0.72 ? coolCyan : lerp(accCol, float3(1.0, 0.95, 0.90), 0.30));
                sum += orbCol * alpha;
            }
        }
    }
    return sum;
}

float3 StarLayer(float2 lp, float gridScale, float zSeed, float blurAmt)
{
    float2 g = lp * gridScale;
    float2 cell = floor(g);
    float2 f = frac(g);
    float3 sum = float3(0.0, 0.0, 0.0);

    float blurScale = lerp(1.0, 2.8, saturate(blurAmt));
    [loop] for (int dy = -1; dy <= 1; dy++)
    {
        [loop] for (int dx = -1; dx <= 1; dx++)
        {
            float2 id = cell + float2((float)dx, (float)dy);
            float show = Hash21(id * 3.11 + zSeed * 19.7);
            if (show > 0.64)
            {
                float2 rnd = Hash22(id + float2(zSeed * 23.1, zSeed * 11.4));
                float2 center = float2((float)dx, (float)dy) + 0.2 + rnd * 0.6 - f;
                float dist = length(center);
                float mag = pow(rnd.x, 1.8);
                float coreR = (0.030 + 0.045 * mag) * blurScale;
                float haloR = (0.14 + 0.12 * mag) * blurScale;
                float core = 1.0 - smoothstep(coreR * 0.3, coreR, dist);
                float halo = exp(-dist * dist / max(0.001, haloR * haloR * 0.35)) * 0.32;
                float bright = (core + halo) * lerp(0.30, 1.05, mag) * lerp(1.0, 0.55, saturate(blurAmt));
                float3 starCol = lerp(float3(0.75, 0.86, 1.0), float3(1.0, 0.90, 0.74), rnd.y);
                sum += starCol * bright;
            }
        }
    }
    return sum;
}

D2D_PS_ENTRY(main)
{
    float dummy = (D2DSampleInputAtOffset(0, float2(0.0, 0.0)).a - 1.0) * 1e-4;

    float2 scenePos = D2DGetScenePosition().xy;
    float2 size = max(float2(widthPx, heightPx), float2(1.0, 1.0));
    float2 uv = saturate(scenePos / size);
    float aspect = size.x / size.y;
    float minDim = max(min(size.x, size.y), 1.0);
    float2 baseP = (uv - 0.5) * float2(aspect, 1.0);

    float3 topCol = float3(topR, topG, topB);
    float3 botCol = float3(botR, botG, botB);
    float3 accCol = float3(accR, accG, accB);
    float3 lumaW = float3(0.2126, 0.7152, 0.0722);

    float3 rgb = float3(0.1, 0.1, 0.12);
    float grainAmp = 0.007;

    if (mode < 1.5)
    {
        // 1: StudioDark
        rgb = lerp(float3(0.11, 0.125, 0.16), float3(0.055, 0.06, 0.08), uv.y);
        float2 lp = LayerPos(baseP - float2(0.0, -0.08), 0.65, minDim);
        float glow = exp(-dot(lp, lp) * lerp(2.6, 1.5, blurAmount));
        rgb += float3(0.18, 0.20, 0.25) * glow * lerp(1.0, 0.78, blurAmount);
        float vig = 1.0 - 0.34 * dot(uv - 0.5, uv - 0.5);
        rgb *= vig;
    }
    else if (mode < 2.5)
    {
        // 2: StudioWarm
        rgb = lerp(float3(0.87, 0.82, 0.75), float3(0.61, 0.54, 0.46), uv.y);
        float2 lp = LayerPos(baseP - float2(0.0, -0.10), 0.65, minDim);
        float glow = exp(-dot(lp, lp) * lerp(2.2, 1.3, blurAmount));
        rgb += float3(0.12, 0.105, 0.085) * glow;
        float vig = 1.0 - 0.22 * dot(uv - 0.5, uv - 0.5);
        rgb *= vig;
        grainAmp = 0.009;
    }
    else if (mode < 3.5)
    {
        // 3: StudioMatch
        float3 tSmooth = lerp(topCol, float3(dot(topCol, lumaW), dot(topCol, lumaW), dot(topCol, lumaW)), 0.18) * 0.85;
        float3 bSmooth = lerp(botCol, float3(dot(botCol, lumaW), dot(botCol, lumaW), dot(botCol, lumaW)), 0.22) * 0.72;
        rgb = lerp(tSmooth, bSmooth, uv.y);
        float2 lp = LayerPos(baseP - float2(0.0, -0.07), 0.65, minDim);
        float glow = exp(-dot(lp, lp) * lerp(2.3, 1.35, blurAmount));
        float3 spotCol = lerp(accCol, float3(1.0, 0.98, 0.95), 0.40) * 0.24;
        rgb += spotCol * glow;
        rgb *= (1.0 - 0.28 * dot(uv - 0.5, uv - 0.5));
    }
    else if (mode < 4.5)
    {
        // 4: Bokeh (3 parallaxed depth planes)
        float3 baseTop = lerp(float3(0.05, 0.06, 0.10), topCol * 0.45, 0.40);
        float3 baseBot = lerp(float3(0.08, 0.06, 0.10), botCol * 0.40, 0.35);
        rgb = lerp(baseTop, baseBot, uv.y);
        rgb += BokehLayer(LayerPos(baseP, 0.35, minDim), 5.6, 1.0, blurAmount, accCol) * 0.35;
        rgb += BokehLayer(LayerPos(baseP, 0.65, minDim), 3.7, 2.0, blurAmount, accCol) * 0.52;
        rgb += BokehLayer(LayerPos(baseP, 1.00, minDim), 2.4, 3.0, blurAmount, accCol) * 0.62;
    }
    else if (mode < 5.5)
    {
        // 5: Mist (3 parallaxed atmospheric mist curtains)
        float3 skyTop = lerp(float3(0.08, 0.11, 0.17), topCol * 0.55, 0.28);
        float3 skyBot = lerp(float3(0.14, 0.17, 0.23), botCol * 0.55, 0.28);
        rgb = lerp(skyTop, skyBot, uv.y);

        float2 m1 = LayerPos(baseP, 0.30, minDim) * float2(1.4, 2.6) + float2(2.1, 4.7);
        float2 m2 = LayerPos(baseP, 0.65, minDim) * float2(1.2, 2.3) + float2(9.3, 1.8);
        float2 m3 = LayerPos(baseP, 1.05, minDim) * float2(1.0, 2.0) + float2(5.6, 8.2);

        float bandLo = lerp(0.32, 0.18, blurAmount);
        float bandHi = lerp(0.78, 0.92, blurAmount);
        float f1 = smoothstep(bandLo, bandHi, Fbm2(m1));
        float f2 = smoothstep(bandLo, bandHi, Fbm2(m2));
        float f3 = smoothstep(bandLo, bandHi, Fbm2(m3));

        float3 mistCool = float3(0.32, 0.46, 0.58);
        float3 mistAccent = lerp(float3(0.46, 0.42, 0.58), accCol, 0.45);
        float3 mistNear = float3(0.68, 0.74, 0.80);

        rgb = lerp(rgb, rgb + mistCool * 0.36, f1 * 0.65);
        rgb = lerp(rgb, rgb + mistAccent * 0.34, f2 * 0.65);
        rgb = lerp(rgb, rgb + mistNear * 0.32, f3 * (0.35 + 0.45 * uv.y));
    }
    else
    {
        // 6: NightSky (nebula wisps + 3 parallaxed star layers)
        rgb = lerp(float3(0.015, 0.020, 0.050), float3(0.045, 0.065, 0.135), uv.y);
        float2 n1 = LayerPos(baseP, 0.25, minDim) * float2(1.5, 1.8) + float2(3.4, 1.2);
        float2 n2 = LayerPos(baseP, 0.50, minDim) * float2(1.9, 1.4) + float2(8.1, 6.5);
        float neb1 = smoothstep(0.42, 0.92, Fbm2(n1));
        float neb2 = smoothstep(0.45, 0.95, Fbm2(n2));
        rgb += lerp(float3(0.10, 0.14, 0.30), accCol * 0.35, 0.35) * neb1 * 0.55;
        rgb += float3(0.14, 0.09, 0.24) * neb2 * 0.45;

        rgb += StarLayer(LayerPos(baseP, 0.25, minDim), 22.0, 1.0, blurAmount) * 0.55;
        rgb += StarLayer(LayerPos(baseP, 0.55, minDim), 14.0, 2.0, blurAmount) * 0.78;
        rgb += StarLayer(LayerPos(baseP, 0.95, minDim),  9.0, 3.0, blurAmount) * 1.00;
    }

    if (desatAmount > 0.001)
    {
        float luma = dot(rgb, lumaW);
        rgb = lerp(rgb, float3(luma, luma, luma), saturate(desatAmount));
    }
    if (dimAmount > 0.001)
    {
        rgb *= (1.0 - saturate(dimAmount) * 0.68);
    }

    float dither = (Hash21(scenePos) - 0.5) * grainAmp + dummy;
    return float4(saturate(rgb + dither), 1.0);
}

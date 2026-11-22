// Single-pass optical Focus Bokeh + Subject Color Pop / Dim + 3D Depth Relighting shader.
// Input 0: photo (clamped). Input 1: depth 0..1 (red). Input 2: subject mask 0..1 (red) or white when absent.
// Compile (Windows SDK, see tools\Compile-Shaders.ps1):
//   fxc /nologo /T ps_4_0 /E main /D D2D_FULL_SHADER /D D2D_ENTRY=main /I <SDK>\um /Fo DepthLens.bin DepthLens.hlsl
#define D2D_INPUT_COUNT 3
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_INPUT2_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

float blurPx;
float focusDepth;
float focusBand;
float bgDesat;
float bgDim;
float relight;
float lightX;
float lightY;
float widthPx;
float heightPx;
float hasMask;
float normalStepPx;

float EdgeSafeDepth(float sampleD, float centerD)
{
    float diff = sampleD - centerD;
    float w = 1.0 - smoothstep(0.08, 0.22, abs(diff));
    return centerD + diff * w;
}

D2D_PS_ENTRY(main)
{
    float4 baseCol = D2DSampleInputAtOffset(0, float2(0, 0));
    float centerD = D2DSampleInputAtOffset(1, float2(0, 0)).r;
    float maskRaw = D2DSampleInputAtOffset(2, float2(0, 0)).r;

    float subject = hasMask > 0.5
        ? smoothstep(0.15, 0.85, maskRaw)
        : smoothstep(focusDepth - 0.12, focusDepth + 0.08, centerD);

    float4 accumCol = baseCol;
    if (blurPx > 0.25)
    {
        float centerCoc = saturate((abs(centerD - focusDepth) - focusBand) / 0.35) * blurPx;
        if (centerCoc > 0.35)
        {
            float4 sum = baseCol;
            float totalW = 1.0;
            [loop] for (int i = 1; i <= 24; i++)
            {
                float theta = i * 2.39996323;
                float r = sqrt(i / 24.0) * centerCoc;
                float2 off = float2(cos(theta), sin(theta)) * r;

                float sd = D2DSampleInputAtOffset(1, off).r;
                float sCoc = saturate((abs(sd - focusDepth) - focusBand) / 0.35) * blurPx;
                float w = 1.0;
                if (sd > centerD + 0.06)
                    w *= saturate(sCoc / max(centerCoc, 0.001));

                float4 sc = D2DSampleInputAtOffset(0, off);
                float lum = dot(sc.rgb, float3(0.2126, 0.7152, 0.0722));
                w *= (1.0 + 0.35 * smoothstep(0.65, 1.0, lum));

                sum += sc * w;
                totalW += w;
            }
            accumCol = sum / totalW;
        }
    }

    float a = max(accumCol.a, 1e-4);
    float3 rgb = accumCol.rgb / a;

    float bgWeight = 1.0 - subject;
    if (bgDesat > 0.001)
    {
        float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
        rgb = lerp(rgb, float3(luma, luma, luma), bgWeight * bgDesat);
    }
    if (bgDim > 0.001)
    {
        rgb *= (1.0 - bgWeight * bgDim * 0.68) * (1.0 + subject * bgDim * 0.06);
    }

    if (relight > 0.001)
    {
        float ns = max(normalStepPx, 1.5);
        float dTL = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(-ns, -ns)).r, centerD);
        float dT  = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(0.0, -ns)).r, centerD);
        float dTR = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(ns, -ns)).r, centerD);
        float dL  = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(-ns, 0.0)).r, centerD);
        float dR  = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(ns, 0.0)).r, centerD);
        float dBL = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(-ns, ns)).r, centerD);
        float dB  = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(0.0, ns)).r, centerD);
        float dBR = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(ns, ns)).r, centerD);
        float gxFine = ((dTL + 2.0 * dL + dBL) - (dTR + 2.0 * dR + dBR)) * 0.25;
        float gyFine = ((dTL + 2.0 * dT + dTR) - (dBL + 2.0 * dB + dBR)) * 0.25;

        float ws = min(ns * 2.6, 34.0);
        float dWL = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(-ws, 0.0)).r, centerD);
        float dWR = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(ws, 0.0)).r, centerD);
        float dWT = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(0.0, -ws)).r, centerD);
        float dWB = EdgeSafeDepth(D2DSampleInputAtOffset(1, float2(0.0, ws)).r, centerD);
        float gxWide = dWL - dWR;
        float gyWide = dWT - dWB;

        float gx = gxFine * 0.35 + gxWide * 0.65;
        float gy = gyFine * 0.35 + gyWide * 0.65;
        float3 N = normalize(float3(gx * 3.4, gy * 3.4, 0.55));

        float2 posNorm = (D2DGetScenePosition().xy / max(float2(widthPx, heightPx), float2(1.0, 1.0))) * 2.0 - 1.0;
        float3 L = normalize(float3(lightX - posNorm.x * 0.42, lightY - posNorm.y * 0.42, 0.78));
        float3 H = normalize(L + float3(0.0, 0.0, 1.0));

        float wrapDiff = saturate((dot(N, L) + 0.42) / 1.42);
        float spec = pow(saturate(dot(N, H)), 20.0) * 0.20;
        float shade = lerp(0.72, 1.22, wrapDiff);
        float depthLift = lerp(0.92, 1.07, centerD);
        float3 lit = rgb * shade * depthLift + float3(1.0, 0.97, 0.93) * spec * (0.35 + 0.65 * centerD);
        rgb = lerp(rgb, lit, relight);
    }

    return float4(saturate(rgb) * accumCol.a, accumCol.a);
}

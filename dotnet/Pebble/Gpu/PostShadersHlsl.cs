// HLSL (Direct3D 12) for the offscreen-HDR post-processing chain. Direct
// translation of PostShaders.cs (the GLSL the Vulkan backend uses) — same bloom
// bright-pass + separable gaussian, same SSAO + volumetric god-ray math, same
// ACES composite. Compiled at runtime with D3DCompiler (vs_5_0 / ps_5_0).
//
// Binding model (per pass): root CBV b0 = CompositeU/UltraU, SRV table t0.. for
// the sampled textures, static sampler s0 (linear/clamp). Clip space is D3D
// (+Y up, z in [0,1]); the fullscreen-triangle VS emits a top-left-origin UV
// matching the Metal fs_vs / the Vulkan post VS.

namespace Pebble.Gpu;

internal static class PostShadersHlsl
{
    // Shared full-screen-triangle vertex shader. SV_VertexID 0=(-1,-1) 1=(3,-1)
    // 2=(-1,3); UV = (x*0.5+0.5, 0.5 - y*0.5) (top-left origin).
    public const string FullscreenVertex = @"
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 p = float2(vid == 1 ? 3.0 : -1.0, vid == 2 ? 3.0 : -1.0);
    o.pos = float4(p, 0.0, 1.0);
    o.uv = float2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
    return o;
}
";

    public const string BloomExtract = @"
Texture2D    scene   : register(t0);
SamplerState samp    : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

float4 PSMain(VSOut input) : SV_Target {
    float3 c = scene.Sample(samp, input.uv).rgb;
    float lum = dot(c, float3(0.299, 0.587, 0.114));
    float k = smoothstep(0.62, 0.95, lum);
    return float4(c * k, 1.0);
}
";

    public const string Blur = @"
cbuffer CompositeU : register(b0) {
    float4 params;   // bloomAmt, warp, time, darkness
    float4 tint;     // tint.xy = blur dir
    float4 params2;
};
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

float4 PSMain(VSOut input) : SV_Target {
    float2 dir = tint.xy;
    float2 uv = input.uv;
    float3 c  = tex.Sample(samp, uv).rgb * 0.227;
    c += tex.Sample(samp, uv + dir * 1.384).rgb * 0.316;
    c += tex.Sample(samp, uv - dir * 1.384).rgb * 0.316;
    c += tex.Sample(samp, uv + dir * 3.230).rgb * 0.07;
    c += tex.Sample(samp, uv - dir * 3.230).rgb * 0.07;
    return float4(c, 1.0);
}
";

    public const string Ultra = @"
cbuffer UltraU : register(b0) {
    float4x4 invViewProj;
    float4x4 viewProj;
    float4x4 shadowMat;
    float4   sunDir;     // xyz + dayLight
    float4   params;     // time, far, volumetricsOn, underwater
    float4   fogColor;   // rgb + renderDistance
    float4   texel;      // 1/w, 1/h
};
Texture2D    depthTex  : register(t0);
Texture2D    shadowMap : register(t1);
SamplerState samp      : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

float3 worldPosFromDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0);
    float4 p = mul(invViewProj, ndc);
    return p.xyz / p.w;
}

float4 PSMain(VSOut input) : SV_Target {
    float2 vUv = input.uv;
    float depth = depthTex.SampleLevel(samp, vUv, 0).r;
    float3 wpos = worldPosFromDepth(vUv, depth);
    float dist = length(wpos);
    float3 rayDir = wpos / max(dist, 1e-5);
    bool isSky = depth >= 0.99999;
    float dayLight = sunDir.w;

    float ao = 1.0;
    if (!isSky && dist < 140.0) {
        float2 px = texel.xy;
        float3 pR = worldPosFromDepth(vUv + float2(px.x, 0.0), depthTex.SampleLevel(samp, vUv + float2(px.x, 0.0), 0).r);
        float3 pD = worldPosFromDepth(vUv + float2(0.0, px.y), depthTex.SampleLevel(samp, vUv + float2(0.0, px.y), 0).r);
        float3 nrm = normalize(cross(pD - wpos, pR - wpos));
        float ang0 = frac(sin(dot(vUv * 961.0, float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
        float occ = 0.0;
        const int TAPS = 8;
        [loop] for (int i = 0; i < TAPS; i++) {
            float a = ang0 + float(i) * 2.399963;
            float r = (float(i) + 0.7) / float(TAPS);
            float rad = 0.65 * r;
            float3 t = float3(cos(a), 0.0, sin(a));
            float3 tang = normalize(t - nrm * dot(t, nrm));
            float3 sp = wpos + (tang * rad + nrm * rad * 0.55);
            float4 cp = mul(viewProj, float4(sp, 1.0));
            if (cp.w <= 0.0) continue;
            float2 suv = float2(cp.x / cp.w * 0.5 + 0.5, 0.5 - cp.y / cp.w * 0.5);
            if (suv.x < 0.0 || suv.x > 1.0 || suv.y < 0.0 || suv.y > 1.0) continue;
            float sd = depthTex.SampleLevel(samp, suv, 0).r;
            float3 spos = worldPosFromDepth(suv, sd);
            float3 dvec = spos - wpos;
            float dlen = length(dvec);
            if (dlen < 0.001) continue;
            float occA = max(0.0, dot(nrm, dvec / dlen) - 0.08);
            float fall = 1.0 - clamp(dlen / 1.6, 0.0, 1.0);
            occ += occA * fall;
        }
        ao = clamp(1.0 - occ / float(TAPS) * 2.4, 0.0, 1.0);
        ao = lerp(ao, 1.0, clamp(dist / 140.0, 0.0, 1.0));
    }

    float3 vol = float3(0.0, 0.0, 0.0);
    if (params.z > 0.5 && dayLight > 0.05) {
        float3 sr = float3(shadowMat[0].z, shadowMat[1].z, shadowMat[2].z);
        float3 sunD = normalize(dot(sr, sr) > 1e-6 ? sr : float3(0.0, 1.0, 0.0));
        if (sunD.y < 0.0) sunD = -sunD;
        float cosA = dot(rayDir, sunD);
        float g = 0.62;
        float phase = (1.0 - g * g) / (4.0 * 3.14159 * pow(1.0 + g * g - 2.0 * g * cosA, 1.5));
        float marchEnd = min(isSky ? params.y : dist, 72.0);
        const int STEPS = 18;
        float dither = frac(sin(dot(vUv * 917.0, float2(36.887, 19.781))) * 24634.6345);
        float lit = 0.0;
        [loop] for (int i = 0; i < STEPS; i++) {
            float fparam = (float(i) + dither) / float(STEPS);
            fparam = fparam * fparam;
            float3 p = rayDir * (fparam * marchEnd);
            float4 sc = mul(shadowMat, float4(p, 1.0));
            float3 sp = sc.xyz / sc.w;
            // D3D shadow tex V is top-down: flip Y like the world PS does.
            float2 suv = float2(sp.x * 0.5 + 0.5, -sp.y * 0.5 + 0.5);
            if (suv.x <= 0.0 || suv.x >= 1.0 || suv.y <= 0.0 || suv.y >= 1.0 || sp.z >= 1.0) {
                lit += 0.6;
                continue;
            }
            float sd = shadowMap.SampleLevel(samp, suv, 0).r;
            lit += (clamp(sp.z, 0.0, 1.0) - 0.0015 <= sd) ? 1.0 : 0.0;
        }
        lit /= float(STEPS);
        float strength = 0.55 * dayLight * phase;
        vol = float3(1.0, 0.92, 0.74) * lit * strength;
    }
    return float4(vol, ao);
}
";

    public const string UltraBlur = @"
cbuffer CompositeU : register(b0) {
    float4 params;
    float4 tint;     // tint.xy = blur dir
    float4 params2;
};
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

float4 PSMain(VSOut input) : SV_Target {
    float2 dir = tint.xy;
    float2 uv = input.uv;
    float4 c  = tex.Sample(samp, uv) * 0.227;
    c += tex.Sample(samp, uv + dir * 1.384) * 0.316;
    c += tex.Sample(samp, uv - dir * 1.384) * 0.316;
    c += tex.Sample(samp, uv + dir * 3.230) * 0.07;
    c += tex.Sample(samp, uv - dir * 3.230) * 0.07;
    return c;
}
";

    public const string Composite = @"
cbuffer CompositeU : register(b0) {
    float4 params;   // bloomAmt, warp, time, darkness
    float4 tint;     // rgb + amount
    float4 params2;  // ultraOn, aoStrength, volStrength, _
};
Texture2D    scene : register(t0);
Texture2D    bloom : register(t1);
Texture2D    ultra : register(t2);
SamplerState samp  : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

float3 acesTonemap(float3 c) {
    c *= 0.92;
    return clamp((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14), 0.0, 1.0);
}

float4 PSMain(VSOut input) : SV_Target {
    float2 uv = input.uv;
    float warp = params.y, time = params.z;
    if (warp > 0.001) {
        uv += float2(sin(uv.y * 14.0 + time * 2.2), cos(uv.x * 12.0 + time * 1.8)) * 0.012 * warp;
    }
    float3 c = scene.Sample(samp, uv).rgb;
    float ultraOn = params2.x;
    if (ultraOn > 0.5) {
        float4 ul = ultra.Sample(samp, uv);
        c *= lerp(1.0, ul.a, params2.y);
        c += ul.rgb * params2.z;
    }
    c += bloom.Sample(samp, uv).rgb * params.x;
    c = lerp(c, tint.rgb, tint.a);
    float darkness = params.w;
    if (darkness > 0.001) {
        float d = distance(uv, float2(0.5, 0.5));
        c *= lerp(1.0, clamp(0.25 - d, 0.0, 0.25) * 4.0, darkness);
    }
    c = acesTonemap(c);
    float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
    c = lerp(float3(lum, lum, lum), c, 1.12);
    return float4(c, 1.0);
}
";
}

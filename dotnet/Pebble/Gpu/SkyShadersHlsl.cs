// HLSL (DX12) for the atmosphere passes — the DX12 peer of SkyShaders.cs. Direct
// translation of the Metal sky_vs/sky_fs + celestial_vs/celestial_fs and their
// SkyU / CelestialU uniforms, compiled at runtime with D3DCompiler (vs_5_0/ps_5_0).
//
// Same binding model as the world pass: a single cbuffer b0 (root CBV) holds the
// uniform; no vertex buffer (positions come from SV_VertexID). viewProj /
// invViewProj are uploaded UN-transposed (row-major bytes of System.Numerics
// view*proj and its inverse), so mul(M, v) evaluates transpose(M)*v == v_row*M,
// matching the world pass exactly.

namespace Pebble.Gpu;

internal static class SkyShadersHlsl
{
    // ── Sky gradient (full-screen triangle) ──
    private const string SkyCommon = @"
cbuffer SkyU : register(b0)
{
    float4x4 invViewProj;
    float4   zenith;
    float4   horizon;
    float4   horizonSun;  // rgb + sunGlow
    float4   sunDir;      // xyz + voidSky
};

struct VSOutput
{
    float4 pos  : SV_Position;
    float3 vDir : TEXCOORD0;
};
";

    public const string SkyVertex = SkyCommon + @"
VSOutput VSMain(uint vid : SV_VertexID)
{
    VSOutput o;
    float2 p = float2(vid == 1 ? 3.0 : -1.0, vid == 2 ? 3.0 : -1.0);
    o.pos = float4(p, 0.99999, 1.0);
    float4 p0 = mul(invViewProj, float4(p, 0.0, 1.0));
    float4 p1 = mul(invViewProj, float4(p, 1.0, 1.0));
    o.vDir = p1.xyz / p1.w - p0.xyz / p0.w;
    return o;
}
";

    public const string SkyFragment = SkyCommon + @"
float4 PSMain(VSOutput input) : SV_Target
{
    float3 d = normalize(input.vDir);
    float h = clamp(d.y, -1.0, 1.0);
    float t = pow(clamp(1.0 - h, 0.0, 1.0), 1.6);
    float3 col = lerp(zenith.rgb, horizon.rgb, t * step(0.0, h) + step(h, 0.0));
    if (h < 0.0) col = lerp(horizon.rgb, zenith.rgb * 0.35, clamp(-h * 2.2, 0.0, 1.0));

    float2 sd = sunDir.xz;
    float lsd = length(sd);
    float sunness = lsd < 1e-5 ? 0.0 : max(0.0, dot(normalize(d.xz), sd / lsd));
    float band = exp(-abs(h) * 5.0);
    col = lerp(col, horizonSun.rgb, horizonSun.w * band * pow(sunness * 0.5 + 0.5, 3.0));

    if (sunDir.w > 0.5) {
        col = lerp(float3(0.03, 0.025, 0.05), float3(0.09, 0.07, 0.12), clamp(h + 0.5, 0.0, 1.0));
    }
    return float4(col, 1.0);
}
";

    // ── Sun / moon celestial billboards ──
    private const string CelCommon = @"
cbuffer CelestialU : register(b0)
{
    float4x4 viewProj;
    float4   center;   // xyz + size
    float4   right;    // xyz + texMode
    float4   up;       // xyz + moonPhase (<0 = sun)
};

struct VSOutput
{
    float4 pos : SV_Position;
    float2 vUv : TEXCOORD0;
};
";

    public const string CelestialVertex = CelCommon + @"
VSOutput VSMain(uint vid : SV_VertexID)
{
    VSOutput o;
    const float2 corners[6] = {
        float2(-1,-1), float2(1,-1), float2(1,1),
        float2(-1,-1), float2(1,1), float2(-1,1) };
    float2 a = corners[vid];
    float3 p = center.xyz + (a.x * right.xyz + a.y * up.xyz) * center.w;
    float4 cp = mul(viewProj, float4(p, 1.0));
    o.pos = float4(cp.xy, cp.w, cp.w);   // depth = far
    o.vUv = a * 0.5 + 0.5;
    return o;
}
";

    public const string CelestialFragment = CelCommon + @"
float4 PSMain(VSOutput input) : SV_Target
{
    float2 dd = input.vUv - 0.5;
    float r = length(dd) * 2.0;
    float moonPhase = up.w;

    if (moonPhase < -0.5) {
        float disc = smoothstep(0.62, 0.55, r);
        float glow = exp(-r * 2.4) * 0.55 * smoothstep(1.0, 0.72, r);
        float3 col = float3(1.0, 0.97, 0.85) * disc + float3(1.0, 0.85, 0.6) * glow;
        return float4(col, max(disc, glow));
    } else {
        float disc = smoothstep(0.5, 0.46, r);
        float ph = moonPhase;
        float shift = (ph - 0.5) * 2.2;
        float shadow = smoothstep(0.42, 0.5, length(dd * 2.0 + float2(shift, 0.0)));
        float3 col = float3(0.92, 0.94, 1.0) * disc * lerp(0.12, 1.0, shadow);
        col *= 1.0 - 0.16 * smoothstep(0.2, 0.1, length(dd - float2(0.1, 0.08)));
        col *= 1.0 - 0.12 * smoothstep(0.16, 0.07, length(dd + float2(0.12, -0.05)));
        return float4(col, disc);
    }
}
";

    // ── Stars (point list at night) ──
    // D3D11/12 has no gl_PointSize; we expand each star CPU-side into a 6-vertex
    // quad (two triangles) of NDC-space size. Per-vertex: pos f3, mag f1, corner f2.
    // StarsU: mat4 viewProj; float4 params (time, alpha, aspect, _).
    private const string StarsCommon = @"
cbuffer StarsU : register(b0)
{
    float4x4 viewProj;
    float4   params;   // time, alpha, aspect, _
};

struct VSInput
{
    float3 inPos    : POSITION;
    float  inMag    : TEXCOORD0;
    float2 inCorner : TEXCOORD1;
};

struct VSOutput
{
    float4 pos    : SV_Position;
    float2 vCorner: TEXCOORD0;
    float  vBright: TEXCOORD1;
};
";

    public const string StarsVertex = StarsCommon + @"
VSOutput VSMain(VSInput input)
{
    VSOutput o;
    float4 cp = mul(viewProj, float4(input.inPos * 900.0, 1.0));
    cp = float4(cp.xy, cp.w, cp.w);   // depth = far plane
    // expand to a small NDC quad (point sprite); aspect keeps stars square
    float sz = (1.0 + input.inMag * 1.6) * 0.0016;
    float2 off = input.inCorner * sz * cp.w;
    off.x /= max(params.z, 0.001);
    o.pos = float4(cp.x + off.x, cp.y + off.y, cp.z, cp.w);
    o.vCorner = input.inCorner;
    o.vBright = 0.55 + 0.45 * sin(params.x * (1.0 + input.inMag * 2.0) + input.inPos.x * 50.0);
    return o;
}
";

    public const string StarsFragment = StarsCommon + @"
float4 PSMain(VSOutput input) : SV_Target
{
    float a = smoothstep(0.5, 0.1, length(input.vCorner) * 0.5) * input.vBright * params.y;
    return float4(float3(0.95, 0.96, 1.0), a);
}
";

    // ── Clouds (flat quad, alpha-blended) ──
    // CloudU: mat4 viewProj; float4 offset (xyz + planeScale); float4 scroll
    // (sx, sy, brightness, fogEnd).
    private const string CloudCommon = @"
cbuffer CloudU : register(b0)
{
    float4x4 viewProj;
    float4   offset;   // xyz + planeScale
    float4   scroll;   // sx, sy, brightness, fogEnd
};

struct VSOutput
{
    float4 pos  : SV_Position;
    float2 vUv  : TEXCOORD0;
    float  vDist: TEXCOORD1;
};
";

    public const string CloudVertex = CloudCommon + @"
VSOutput VSMain(uint vid : SV_VertexID)
{
    VSOutput o;
    const float2 corners[6] = {
        float2(-1,-1), float2(1,-1), float2(1,1),
        float2(-1,-1), float2(1,1), float2(-1,1) };
    float2 a = corners[vid];
    float3 p = float3(a.x * offset.w, 0.0, a.y * offset.w) + offset.xyz;
    o.pos = mul(viewProj, float4(p, 1.0));
    o.vUv = a * 0.5 + 0.5;
    o.vDist = length(p.xz);
    return o;
}
";

    public const string CloudFragment = CloudCommon + @"
Texture2D    cloudTex : register(t0);
SamplerState cloudSampler : register(s0);

float4 PSMain(VSOutput input) : SV_Target
{
    float c = cloudTex.Sample(cloudSampler, input.vUv * 12.0 + scroll.xy).r;
    if (c < 0.5) discard;
    float fogEnd = scroll.w;
    float fade = 1.0 - clamp((input.vDist - fogEnd * 0.7) / (fogEnd * 0.6), 0.0, 1.0);
    return float4(float3(scroll.z, scroll.z, scroll.z), 0.72 * fade);
}
";
}

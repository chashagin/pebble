// HLSL source for the world (chunk) pipeline used by the Direct3D 12 backend.
// Compiled to DXBC at runtime via Silk.NET.Direct3D.Compilers (D3DCompiler.Compile,
// vs_5_0 / ps_5_0). This is a direct translation of WorldShaders.cs (the GLSL the
// Vulkan backend uses) — same math, same packed A/B unpacking, same lighting, fog
// and alpha test. The only differences are the binding model (cbuffer b0 + root
// constants b1 + Texture2DArray t0 + sampler s0) and D3D clip-space conventions
// (+Y up, z in [0,1], left-handed) which are handled in the C# matrix builder, not
// here.
//
// Vertex format (28 bytes, matches Mesher.cs): POSITION float3 @0, TEXCOORD float2
// @12, A uint @20, B uint @24.

namespace Pebble.Gpu;

internal static class WorldShadersHlsl
{
    // cbuffer b0 mirrors the Vulkan ChunkShared std140 block:
    //   mat4 viewProj; mat4 shadowMat; vec4 light; vec4 fog; vec4 fogColor; vec4 misc;
    // HLSL packs float4x4 + float4 on 16-byte boundaries, identical to std140 here.
    // Root constants live in their own cbuffer b1 (origin).
    private const string Common = @"
cbuffer ChunkShared : register(b0)
{
    float4x4 viewProj;
    float4x4 shadowMat;
    float4   light;    // x=dayLight y=gamma z=ambient w=shadowsOn
    float4   fog;      // x=start y=end z=alphaTest w=globalAlpha
    float4   fogColor; // rgb = fog/sky color
    float4   misc;     // x=timeSeconds
};

cbuffer PushConstants : register(b1)
{
    float4 origin;     // (sectionWorld - cam) xyz
};

struct VSInput
{
    float3 inPos : POSITION;
    float2 inUv  : TEXCOORD0;
    uint   inA   : A;
    uint   inB   : B;
};

struct VSOutput
{
    float4 pos     : SV_Position;
    float2 vUv     : TEXCOORD0;
    float3 vColor  : COLOR0;
    float  vFogDist: TEXCOORD1;
    nointerpolation uint vLayer : TEXCOORD2;
    float4 vShadowPos : TEXCOORD3;
    float  vSkyAmt    : TEXCOORD4;
};
";

    public const string Vertex = Common + @"
static const float FACE_SHADE[6] = { 0.55, 1.0, 0.8, 0.8, 0.62, 0.62 };

VSOutput VSMain(VSInput input)
{
    VSOutput o;

    uint  layer    = input.inA & 4095u;
    uint  normal   = (input.inA >> 12) & 7u;
    float ao       = float((input.inA >> 15) & 3u) / 3.0;
    float sky      = float((input.inA >> 17) & 15u) / 15.0;
    float blk      = float((input.inA >> 21) & 15u) / 15.0;
    float emissive = float((input.inA >> 25) & 1u);

    float3 tint = float3(float((input.inB >> 16) & 255u),
                         float((input.inB >> 8) & 255u),
                         float(input.inB & 255u)) / 255.0;

    float3 rel = input.inPos + origin.xyz;
    // viewProj is uploaded so that mul(viewProj, float4(rel,1)) == u.viewProj * vec4(rel,1)
    // in the GLSL (column-major M*v) sense. See D3D12Backend.BuildViewProj.
    o.pos = mul(viewProj, float4(rel, 1.0));

    float dayLight = light.x;
    float gamma    = light.y;
    float ambient0 = light.z;

    float skyBright  = sky * dayLight;
    float ambient    = max(ambient0, 0.03);
    float lightLevel = max(max(skyBright, blk), ambient);
    float l = lightLevel / (4.0 - 3.0 * lightLevel);
    l = lerp(l, 1.0, gamma * 0.35);

    float3 skyCol   = lerp(float3(0.45, 0.55, 0.9), float3(1.0, 1.0, 1.0), clamp(dayLight, 0.0, 1.0));
    float3 blockCol = float3(1.0, 0.85, 0.62);
    float sb = skyBright, bb = blk;
    float3 lightColor = (sb + bb < 0.001) ? float3(1.0, 1.0, 1.0)
                                          : (skyCol * sb + blockCol * bb) / (sb + bb);

    float aoF = lerp(0.42, 1.0, ao);
    o.vColor = tint * FACE_SHADE[normal] * aoF * max(l, emissive)
               * lerp(lightColor, float3(1.0, 1.0, 1.0), emissive);

    o.vFogDist = length(rel.xz);
    o.vUv = input.inUv;
    o.vLayer = layer;
    o.vShadowPos = mul(shadowMat, float4(rel, 1.0));
    o.vSkyAmt = sky * (1.0 - emissive);
    return o;
}
";

    // Shadow-pass vertex shader: light-space depth only (no PS). shadowMat is
    // uploaded into the viewProj slot of cbuffer b0 for this pass.
    public const string ShadowVertex = Common + @"
float4 VSMain(VSInput input) : SV_Position
{
    float3 rel = input.inPos + origin.xyz;
    return mul(viewProj, float4(rel, 1.0));
}
";

    // Sun-shadow path (re-added). The DX12 world root signature binds the shadow
    // depth map (a Texture2D, register t1) plus a comparison sampler (s1, LessEqual).
    // The 3×3 PCF below mirrors WorldShaders.cs's GLSL chunk_fs: it remaps the same
    // clip-space shadow position into [0,1] tex coords, does a 3×3 hardware-PCF
    // comparison, and gates the shadow factor by inMap × skyAmt × dayLight, blending
    // toward 0.55 in fully shadowed regions. Shadow xy/z conventions match the C#
    // SkyState.BuildShadowMatrix(flipY:false) used for D3D's +Y-up clip space.
    public const string Fragment = Common + @"
Texture2DArray atlas : register(t0);
SamplerState   atlasSampler : register(s0);
Texture2D            shadowMap     : register(t1);
SamplerComparisonState shadowSampler : register(s1);

float4 PSMain(VSOutput input) : SV_Target
{
    float4 tex = atlas.Sample(atlasSampler, float3(input.vUv, float(input.vLayer)));
    float alphaTest = fog.z;
    if (alphaTest > 0.0 && tex.a < alphaTest) discard;

    // ── sun shadow (3x3 PCF via comparison sampler) ──
    float shadow = 1.0;
    float shadowsOn = light.w;
    float dayLight  = light.x;
    if (shadowsOn > 0.5 && dayLight > 0.05)
    {
        float3 sp = input.vShadowPos.xyz / input.vShadowPos.w;
        // clip xy [-1,1] -> tex [0,1]; D3D tex V is top-down so flip Y.
        float2 suv = float2(sp.x * 0.5 + 0.5, -sp.y * 0.5 + 0.5);
        float inMap = (suv.x > 0.0 && suv.x < 1.0 && suv.y > 0.0 && suv.y < 1.0 && sp.z < 1.0) ? 1.0 : 0.0;
        float2 cuv = clamp(suv, 0.0, 1.0);
        float cz = clamp(sp.z, 0.0, 1.0) - 0.0012;
        float texel = misc.w > 0.0 ? misc.w : (1.0 / 2048.0);
        float s = 0.0;
        [unroll] for (int dy = -1; dy <= 1; dy++)
        [unroll] for (int dx = -1; dx <= 1; dx++)
            s += shadowMap.SampleCmpLevelZero(shadowSampler,
                     cuv + float2(dx, dy) * texel, cz);
        s /= 9.0;
        shadow = lerp(1.0, lerp(0.55, 1.0, s),
                      inMap * clamp(input.vSkyAmt, 0.0, 1.0) * dayLight);
    }

    float3 col = tex.rgb * input.vColor * shadow;
    float alpha = tex.a * fog.w;

    float f = clamp((input.vFogDist - fog.x) / (fog.y - fog.x), 0.0, 1.0);
    col = lerp(col, fogColor.rgb, f);
    return float4(col, alpha);
}
";
}

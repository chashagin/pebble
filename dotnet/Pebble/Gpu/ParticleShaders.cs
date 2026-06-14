// Shaders for the instanced particle billboard pass — the GLSL (Vulkan) / HLSL
// (DX12) port of Sources/Pebble/Shaders.swift's particle_vs / particle_fs and the
// ParticleU uniform. Each instance is a camera-facing quad sampling the block
// atlas Texture2DArray; size + atlas layer are packed in one float (tile*256 +
// size*100), exactly as the Metal original encodes them (see Particles.Pack).
//
// Vertex inputs:
//   binding 0 (per-vertex): vec2 corner               (the [-1..1] quad corner)
//   binding 1 (per-instance, stride 48):
//     vec3  pos        @0    (world position − camera)
//     vec4  uvRect     @12   (u0 v0 u1 v1 within the tile)
//     float layerSize  @28   (tile*256 + size*100)
//     vec4  colorLight @32   (rgb tint + light)
//
// ParticleU (std140 / cbuffer): mat4 viewProj; vec4 right; vec4 up(xyz + dayLight).

namespace Pebble.Gpu;

internal static class ParticleShaders
{
    // ── GLSL (Vulkan) ─────────────────────────────────────────────────────────
    public const string VulkanVertex = @"#version 450

layout(location = 0) in vec2 inCorner;
layout(location = 1) in vec3 inPos;
layout(location = 2) in vec4 inUvRect;
layout(location = 3) in float inLayerSize;
layout(location = 4) in vec4 inColorLight;

layout(set = 0, binding = 0, std140) uniform ParticleU {
    mat4 viewProj;
    vec4 right;
    vec4 up;          // xyz + dayLight
} u;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(location = 2) flat out uint vLayer;

void main() {
    float layer = floor(inLayerSize / 256.0);
    float size  = mod(inLayerSize, 256.0) / 100.0;
    vec3 p = inPos + (inCorner.x * u.right.xyz + inCorner.y * u.up.xyz) * size;
    gl_Position = u.viewProj * vec4(p, 1.0);
    vUv = mix(inUvRect.xy, inUvRect.zw, inCorner * 0.5 + 0.5);
    float light = inColorLight.a;
    float dayLight = u.up.w;
    float l = max(light * dayLight, 0.06);
    l = l / (4.0 - 3.0 * l);
    vColor = vec4(inColorLight.rgb * max(l, 0.25), 1.0);
    vLayer = uint(layer);
}
";

    public const string VulkanFragment = @"#version 450

layout(set = 0, binding = 1) uniform sampler2DArray atlas;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) flat in uint vLayer;

layout(location = 0) out vec4 o;

void main() {
    vec4 tex = texture(atlas, vec3(vUv, float(vLayer)));
    if (tex.a < 0.3) discard;
    o = vec4(tex.rgb * vColor.rgb, tex.a);
}
";

    // ── HLSL (DX12) ───────────────────────────────────────────────────────────
    private const string HlslCommon = @"
cbuffer ParticleU : register(b0)
{
    float4x4 viewProj;
    float4   right;
    float4   up;       // xyz + dayLight
};

struct VSInput
{
    float2 inCorner     : CORNER;
    float3 inPos        : POSITION;
    float4 inUvRect     : UVRECT;
    float  inLayerSize  : LAYERSIZE;
    float4 inColorLight : COLORLIGHT;
};

struct VSOutput
{
    float4 pos    : SV_Position;
    float2 vUv    : TEXCOORD0;
    float4 vColor : COLOR0;
    nointerpolation uint vLayer : TEXCOORD1;
};
";

    public const string HlslVertex = HlslCommon + @"
VSOutput VSMain(VSInput input)
{
    VSOutput o;
    float layer = floor(input.inLayerSize / 256.0);
    float size  = fmod(input.inLayerSize, 256.0) / 100.0;
    float3 p = input.inPos + (input.inCorner.x * right.xyz + input.inCorner.y * up.xyz) * size;
    o.pos = mul(viewProj, float4(p, 1.0));
    o.vUv = lerp(input.inUvRect.xy, input.inUvRect.zw, input.inCorner * 0.5 + 0.5);
    float light = input.inColorLight.a;
    float dayLight = up.w;
    float l = max(light * dayLight, 0.06);
    l = l / (4.0 - 3.0 * l);
    o.vColor = float4(input.inColorLight.rgb * max(l, 0.25), 1.0);
    o.vLayer = (uint)layer;
    return o;
}
";

    public const string HlslFragment = @"
Texture2DArray atlas : register(t0);
SamplerState   atlasSampler : register(s0);

struct VSOutput
{
    float4 pos    : SV_Position;
    float2 vUv    : TEXCOORD0;
    float4 vColor : COLOR0;
    nointerpolation uint vLayer : TEXCOORD1;
};

float4 PSMain(VSOutput input) : SV_Target
{
    float4 tex = atlas.Sample(atlasSampler, float3(input.vUv, (float)input.vLayer));
    if (tex.a < 0.3) discard;
    return float4(tex.rgb * input.vColor.rgb, tex.a);
}
";
}

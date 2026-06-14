// Shaders for the item-billboard sprite pass — the GLSL (Vulkan) / HLSL (DX12)
// port of Sources/Pebble/Shaders.swift's sprite_vs / sprite_fs + SpriteU. Each
// sprite is a camera-facing quad (anchored at its base, like the Metal original)
// sampling one layer of the backend's icon Texture2DArray (16×16 item icons). No
// vertex buffer — the 6 corners come from the vertex id.
//
// SpriteU (std140 / cbuffer):
//   mat4  viewProj;
//   vec4  center;    // xyz (world − cam) + size
//   vec4  right;     // camera right xz (y=0) + layer (w = icon array layer)
//   vec4  light;     // light(brightness), fogStart, fogEnd, _
//   vec4  fogColor;  // rgb + 1

namespace Pebble.Gpu;

internal static class SpriteShaders
{
    // ── GLSL (Vulkan) ─────────────────────────────────────────────────────────
    public const string VulkanVertex = @"#version 450

layout(set = 0, binding = 0, std140) uniform SpriteU {
    mat4 viewProj;
    vec4 center;   // xyz + size
    vec4 right;    // xyz + layer
    vec4 light;    // light, fogStart, fogEnd, _
    vec4 fogColor;
} u;

layout(location = 0) out vec2 vUv;
layout(location = 1) out float vDist;

void main() {
    const vec2 corners[6] = vec2[6](
        vec2(-0.5, 0.0), vec2(0.5, 0.0), vec2(0.5, 1.0),
        vec2(-0.5, 0.0), vec2(0.5, 1.0), vec2(-0.5, 1.0));
    vec2 a = corners[gl_VertexIndex];
    vec3 pos = u.center.xyz + u.right.xyz * a.x * u.center.w + vec3(0.0, 1.0, 0.0) * a.y * u.center.w;
    vUv = vec2(a.x + 0.5, 1.0 - a.y);
    vDist = length(pos);
    gl_Position = u.viewProj * vec4(pos, 1.0);
}
";

    public const string VulkanFragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform SpriteU {
    mat4 viewProj;
    vec4 center;
    vec4 right;    // xyz + layer
    vec4 light;
    vec4 fogColor;
} u;

layout(set = 0, binding = 1) uniform sampler2DArray icons;

layout(location = 0) in vec2 vUv;
layout(location = 1) in float vDist;

layout(location = 0) out vec4 o;

void main() {
    vec4 c = texture(icons, vec3(vUv, u.right.w));
    if (c.a < 0.1) discard;
    float fog = clamp((vDist - u.light.y) / max(u.light.z - u.light.y, 0.001), 0.0, 1.0);
    o = vec4(mix(c.rgb * u.light.x, u.fogColor.rgb, fog), c.a);
}
";

    // ── HLSL (DX12) ───────────────────────────────────────────────────────────
    private const string HlslCommon = @"
cbuffer SpriteU : register(b0)
{
    float4x4 viewProj;
    float4   center;   // xyz + size
    float4   right;    // xyz + layer
    float4   light;    // light, fogStart, fogEnd, _
    float4   fogColor;
};

struct VSOutput
{
    float4 pos   : SV_Position;
    float2 vUv   : TEXCOORD0;
    float  vDist : TEXCOORD1;
};
";

    public const string HlslVertex = HlslCommon + @"
VSOutput VSMain(uint vid : SV_VertexID)
{
    const float2 corners[6] = {
        float2(-0.5, 0.0), float2(0.5, 0.0), float2(0.5, 1.0),
        float2(-0.5, 0.0), float2(0.5, 1.0), float2(-0.5, 1.0) };
    float2 a = corners[vid];
    float3 pos = center.xyz + right.xyz * a.x * center.w + float3(0.0, 1.0, 0.0) * a.y * center.w;
    VSOutput o;
    o.vUv = float2(a.x + 0.5, 1.0 - a.y);
    o.vDist = length(pos);
    o.pos = mul(viewProj, float4(pos, 1.0));
    return o;
}
";

    public const string HlslFragment = HlslCommon + @"
Texture2DArray icons : register(t0);
SamplerState   iconSampler : register(s0);

float4 PSMain(VSOutput input) : SV_Target
{
    float4 c = icons.Sample(iconSampler, float3(input.vUv, right.w));
    if (c.a < 0.1) discard;
    float fog = clamp((input.vDist - light.y) / max(light.z - light.y, 0.001), 0.0, 1.0);
    return float4(lerp(c.rgb * light.x, fogColor.rgb, fog), c.a);
}
";
}

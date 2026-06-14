// Shader source for the 3D entity pipeline (mobs + dropped-item models).
//
// Direct translation of Sources/Pebble/Shaders.swift's `entity_vs` / `entity_fs`
// (the Metal originals) and its `EntityU` uniform. The entity pass draws posed,
// box-modelled mobs between the world pass (which wrote color+depth) and the HUD
// pass (no depth). Depth test+write are ON so entities occlude/are-occluded by
// terrain correctly.
//
// Vertex format (stride 40): float3 pos @0, float3 normal @12, float2 uv @24,
// float part @32 — the part index selects parts[(int)(part+0.5)].
//
// EntityU std140 / HLSL cbuffer layout (matches the Metal struct exactly):
//   float4x4 viewProj;        //   0
//   float4x4 model;           //  64
//   float4x4 parts[24];       // 128 .. 128+1536 = 1664
//   float4 light;             // sky, block, dayLight, gamma
//   float4 misc;              // ambient, alpha, fogStart, fogEnd
//   float4 overlay;           // hurt-flash rgba
//   float4 fogColor;          // rgb + 1
// = 64 + 64 + 1536 + 64 = 1728 bytes.
//
// GLSL (-> SPIR-V via shaderc) for Vulkan; HLSL (-> DXBC via D3DCompiler) for DX12.
// Both are direct translations; the only differences are the binding model and
// clip-space conventions (handled in the C# matrix builder, identical to the
// world pass: Vulkan negates proj row 1 for +Y-down clip; D3D keeps +Y-up).

namespace Pebble.Gpu;

internal static class EntityShaders
{
    // ── GLSL (Vulkan) ─────────────────────────────────────────────────────────
    public const string VulkanVertex = @"#version 450

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in float inPart;

layout(set = 0, binding = 0, std140) uniform EntityU {
    mat4 viewProj;
    mat4 model;
    mat4 parts[24];
    vec4 light;      // sky, block, dayLight, gamma
    vec4 misc;       // ambient, alpha, fogStart, fogEnd
    vec4 overlay;    // hurt flash rgba
    vec4 fogColor;
} u;

layout(location = 0) out vec2 vUv;
layout(location = 1) out float vLight;
layout(location = 2) out vec3 vNormal;
layout(location = 3) out float vFogDist;

void main() {
    mat4 part = u.parts[int(inPart + 0.5)];
    vec4 wp = u.model * part * vec4(inPos, 1.0);
    gl_Position = u.viewProj * wp;
    vUv = inUv;

    float sky = u.light.x / 15.0 * u.light.z;
    float lightLevel = max(max(sky, u.light.y / 15.0), max(u.misc.x, 0.03));
    float l = lightLevel / (4.0 - 3.0 * lightLevel);
    vLight = mix(l, 1.0, u.light.w * 0.35);

    mat3 m3 = mat3(u.model);
    mat3 p3 = mat3(part);
    vNormal = m3 * p3 * inNormal;
    vFogDist = length(wp.xz);
}
";

    public const string VulkanFragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform EntityU {
    mat4 viewProj;
    mat4 model;
    mat4 parts[24];
    vec4 light;
    vec4 misc;
    vec4 overlay;
    vec4 fogColor;
} u;

layout(set = 0, binding = 1) uniform sampler2D tex;

layout(location = 0) in vec2 vUv;
layout(location = 1) in float vLight;
layout(location = 2) in vec3 vNormal;
layout(location = 3) in float vFogDist;

layout(location = 0) out vec4 o;

void main() {
    vec4 t = texture(tex, vUv);
    if (t.a < 0.1) discard;
    vec3 n = normalize(vNormal);
    float shade = 0.62 + 0.38 * clamp(n.y * 0.7 + 0.55, 0.0, 1.0);
    vec3 col = t.rgb * vLight * shade;
    col = mix(col, u.overlay.rgb, u.overlay.a);
    float fog = clamp((vFogDist - u.misc.z) / (u.misc.w - u.misc.z), 0.0, 1.0);
    col = mix(col, u.fogColor.rgb, fog * fog);
    o = vec4(col, t.a * u.misc.y);
}
";

    // ── HLSL (DX12) ───────────────────────────────────────────────────────────
    // cbuffer b0 mirrors the Vulkan EntityU std140 block (float4x4 + float4[]
    // align identically). Texture2D t0 + static sampler s0.
    private const string HlslCommon = @"
cbuffer EntityU : register(b0)
{
    float4x4 viewProj;
    float4x4 model;
    float4x4 parts[24];
    float4 light;      // sky, block, dayLight, gamma
    float4 misc;       // ambient, alpha, fogStart, fogEnd
    float4 overlay;    // hurt flash rgba
    float4 fogColor;
};

struct VSInput
{
    float3 inPos    : POSITION;
    float3 inNormal : NORMAL;
    float2 inUv     : TEXCOORD0;
    float  inPart   : PART;
};

struct VSOutput
{
    float4 pos      : SV_Position;
    float2 vUv      : TEXCOORD0;
    float  vLight   : TEXCOORD1;
    float3 vNormal  : NORMAL;
    float  vFogDist : TEXCOORD2;
};
";

    public const string HlslVertex = HlslCommon + @"
VSOutput VSMain(VSInput input)
{
    VSOutput o;
    float4x4 part = parts[(int)(input.inPart + 0.5)];
    // viewProj/model/part are uploaded so that mul(M, v) == (GLSL M * v).
    float4 wp = mul(model, mul(part, float4(input.inPos, 1.0)));
    o.pos = mul(viewProj, wp);
    o.vUv = input.inUv;

    float sky = light.x / 15.0 * light.z;
    float lightLevel = max(max(sky, light.y / 15.0), max(misc.x, 0.03));
    float l = lightLevel / (4.0 - 3.0 * lightLevel);
    o.vLight = lerp(l, 1.0, light.w * 0.35);

    float3x3 m3 = (float3x3)model;
    float3x3 p3 = (float3x3)part;
    o.vNormal = mul(m3, mul(p3, input.inNormal));
    o.vFogDist = length(wp.xz);
    return o;
}
";

    public const string HlslFragment = HlslCommon + @"
Texture2D    tex : register(t0);
SamplerState texSampler : register(s0);

float4 PSMain(VSOutput input) : SV_Target
{
    float4 t = tex.Sample(texSampler, input.vUv);
    if (t.a < 0.1) discard;
    float3 n = normalize(input.vNormal);
    float shade = 0.62 + 0.38 * clamp(n.y * 0.7 + 0.55, 0.0, 1.0);
    float3 col = t.rgb * input.vLight * shade;
    col = lerp(col, overlay.rgb, overlay.a);
    float fog = clamp((input.vFogDist - misc.z) / (misc.w - misc.z), 0.0, 1.0);
    col = lerp(col, fogColor.rgb, fog * fog);
    return float4(col, t.a * misc.y);
}
";
}

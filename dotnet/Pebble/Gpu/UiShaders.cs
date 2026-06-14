// Shader source for the 2D UI / HUD pipeline.
//
// The UI pass is orthographic screen-space, alpha-blended, with NO depth. Each
// vertex carries: position in SCREEN PIXELS, a uv, a LAYER index, and an rgba
// color. The vertex shader maps screen-pixel position -> NDC using the framebuffer
// size (passed via push constant / root constant). The fragment shader:
//   * layer < 0  -> solid colored quad (crosshair, hotbar bar, hearts, font, ...)
//   * layer >= 0 -> sample the UI icon Texture2DArray at that layer, multiply color
//
// GLSL (-> SPIR-V via shaderc) for Vulkan, HLSL (-> DXBC via D3DCompiler) for DX12.
// Both are direct translations of each other; the only differences are the binding
// model and clip-space conventions (handled by the screen->NDC math: Vulkan clip is
// +Y-down so we keep pixel-y growing downward == NDC y growing downward; D3D clip is
// +Y-up so we flip y there).

namespace Pebble.Gpu;

internal static class UiShaders
{
    // Vertex layout (36 bytes): float2 pos @0, float2 uv @8, float layer @16,
    // float4 color @20.
    public const string VulkanVertex = @"#version 450

layout(location = 0) in vec2 inPos;     // screen pixels
layout(location = 1) in vec2 inUv;
layout(location = 2) in float inLayer;  // <0 = solid white
layout(location = 3) in vec4 inColor;   // rgba, premultiplied-style tint

layout(push_constant) uniform Push {
    vec4 screen;   // x=width y=height (pixels)
} pc;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(location = 2) flat out int vLayer;

void main() {
    // pixel -> [0,1] -> NDC. Vulkan clip space is +Y-down, matching pixel-y-down,
    // so NDC y = pixel_y/H*2-1 (no flip).
    float nx = inPos.x / pc.screen.x * 2.0 - 1.0;
    float ny = inPos.y / pc.screen.y * 2.0 - 1.0;
    gl_Position = vec4(nx, ny, 0.0, 1.0);
    vUv = inUv;
    vColor = inColor;
    vLayer = int(inLayer);
}
";

    public const string VulkanFragment = @"#version 450

layout(set = 0, binding = 0) uniform sampler2DArray icons;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 2) flat in int vLayer;

layout(location = 0) out vec4 o;

void main() {
    if (vLayer < 0) {
        o = vColor;                 // solid colored quad
    } else {
        vec4 tex = texture(icons, vec3(vUv, float(vLayer)));
        o = tex * vColor;           // icon, tinted
    }
    if (o.a <= 0.0) discard;
}
";

    // ── HLSL (DX12) ──────────────────────────────────────────────────────────
    private const string HlslCommon = @"
cbuffer Push : register(b0)
{
    float4 screen;   // x=width y=height
};

struct VSInput
{
    float2 inPos   : POSITION;
    float2 inUv    : TEXCOORD0;
    float  inLayer : LAYER;
    float4 inColor : COLOR0;
};

struct VSOutput
{
    float4 pos    : SV_Position;
    float2 vUv    : TEXCOORD0;
    float4 vColor : COLOR0;
    nointerpolation int vLayer : TEXCOORD1;
};
";

    public const string HlslVertex = HlslCommon + @"
VSOutput VSMain(VSInput input)
{
    VSOutput o;
    // pixel -> [0,1] -> NDC. D3D clip space is +Y-up, but pixel-y grows downward,
    // so flip: NDC y = 1 - pixel_y/H*2.
    float nx = input.inPos.x / screen.x * 2.0 - 1.0;
    float ny = 1.0 - input.inPos.y / screen.y * 2.0;
    o.pos = float4(nx, ny, 0.0, 1.0);
    o.vUv = input.inUv;
    o.vColor = input.inColor;
    o.vLayer = (int)input.inLayer;
    return o;
}
";

    public const string HlslFragment = HlslCommon + @"
Texture2DArray icons : register(t0);
SamplerState   iconSampler : register(s0);

float4 PSMain(VSOutput input) : SV_Target
{
    float4 o;
    if (input.vLayer < 0) {
        o = input.vColor;
    } else {
        float4 tex = icons.Sample(iconSampler, float3(input.vUv, (float)input.vLayer));
        o = tex * input.vColor;
    }
    if (o.a <= 0.0) discard;
    return o;
}
";
}

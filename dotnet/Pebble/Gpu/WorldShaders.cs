// GLSL source for the world (chunk) pipeline, compiled to SPIR-V at runtime via
// Silk.NET.Shaderc (see VulkanBackend.CompileGlsl). The vertex shader unpacks the
// packed per-vertex A/B words and computes the lit vertex color; the fragment
// shader samples the texture-array atlas, alpha-tests, and applies fog.
//
// Mirrors the macOS Metal `ChunkShared` uniform and the engine's 28-byte vertex
// format (see Mesher.cs):
//   loc0 vec3 inPos @0, loc1 vec2 inUv @12, loc2 uint inA @20, loc3 uint inB @24.

namespace Pebble.Gpu;

internal static class WorldShaders
{
    public const string Vertex = @"#version 450

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uint inA;
layout(location = 3) in uint inB;

layout(set = 0, binding = 0, std140) uniform ChunkShared {
    mat4 viewProj;
    mat4 shadowMat;
    vec4 light;     // x=dayLight y=gamma z=ambient w=shadowsOn
    vec4 fog;       // x=start y=end z=alphaTest w=globalAlpha
    vec4 fogColor;  // rgb = fog/sky color
    vec4 misc;      // x=timeSeconds
} u;

layout(push_constant) uniform Push {
    vec4 origin;    // (sectionWorld - cam) xyz
} pc;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec3 vColor;
layout(location = 2) out float vFogDist;
layout(location = 3) flat out uint vLayer;
layout(location = 4) out vec4 vShadowPos;   // shadowMat * worldPos
layout(location = 5) out float vSkyAmt;     // sky light fraction (gates shadowing)

const float FACE_SHADE[6] = float[6](0.55, 1.0, 0.8, 0.8, 0.62, 0.62);

void main() {
    uint layer    = inA & 4095u;
    uint normal   = (inA >> 12) & 7u;
    float ao      = float((inA >> 15) & 3u) / 3.0;
    float sky     = float((inA >> 17) & 15u) / 15.0;
    float blk     = float((inA >> 21) & 15u) / 15.0;
    float emissive= float((inA >> 25) & 1u);

    vec3 tint = vec3(float((inB >> 16) & 255u), float((inB >> 8) & 255u), float(inB & 255u)) / 255.0;

    vec3 rel = inPos + pc.origin.xyz;
    gl_Position = u.viewProj * vec4(rel, 1.0);

    float dayLight = u.light.x;
    float gamma    = u.light.y;
    float ambient0 = u.light.z;

    float skyBright = sky * dayLight;
    float ambient   = max(ambient0, 0.03);
    float lightLevel = max(max(skyBright, blk), ambient);
    float l = lightLevel / (4.0 - 3.0 * lightLevel);
    l = mix(l, 1.0, gamma * 0.35);

    vec3 skyCol = mix(vec3(0.45, 0.55, 0.9), vec3(1.0), clamp(dayLight, 0.0, 1.0));
    vec3 blockCol = vec3(1.0, 0.85, 0.62);
    float sb = skyBright, bb = blk;
    vec3 lightColor = (sb + bb < 0.001) ? vec3(1.0) : (skyCol * sb + blockCol * bb) / (sb + bb);

    float aoF = mix(0.42, 1.0, ao);
    vColor = tint * FACE_SHADE[normal] * aoF * max(l, emissive) * mix(lightColor, vec3(1.0), emissive);

    vFogDist = length(rel.xz);
    vUv = inUv;
    vLayer = layer;
    vShadowPos = u.shadowMat * vec4(rel, 1.0);
    vSkyAmt = sky * (1.0 - emissive);
}
";

    public const string Fragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform ChunkShared {
    mat4 viewProj;
    mat4 shadowMat;
    vec4 light;     // x=dayLight y=gamma z=ambient w=shadowsOn
    vec4 fog;       // x=start y=end z=alphaTest w=globalAlpha
    vec4 fogColor;
    vec4 misc;      // x=time y=packFluidDamp z=ultraOn w=shadowTexel
} u;

layout(set = 0, binding = 1) uniform sampler2DArray atlas;
layout(set = 0, binding = 2) uniform sampler2D shadowMap;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vColor;
layout(location = 2) in float vFogDist;
layout(location = 3) flat in uint vLayer;
layout(location = 4) in vec4 vShadowPos;
layout(location = 5) in float vSkyAmt;

layout(location = 0) out vec4 o;

void main() {
    vec4 tex = texture(atlas, vec3(vUv, float(vLayer)));
    float alphaTest = u.fog.z;
    if (alphaTest > 0.0 && tex.a < alphaTest) discard;

    // ── sun shadow (3x3 PCF, manual depth compare) ──
    float shadow = 1.0;
    float shadowsOn = u.light.w;
    float dayLight = u.light.x;
    if (shadowsOn > 0.5 && dayLight > 0.05) {
        vec3 sp = vShadowPos.xyz / vShadowPos.w;
        // Vulkan clip z is already [0,1]; map xy NDC [-1,1] to [0,1] tex coords.
        vec2 suv = sp.xy * 0.5 + 0.5;
        float inMap = (suv.x > 0.0 && suv.x < 1.0 && suv.y > 0.0 && suv.y < 1.0 && sp.z < 1.0) ? 1.0 : 0.0;
        vec2 cuv = clamp(suv, vec2(0.0), vec2(1.0));
        float cz = clamp(sp.z, 0.0, 1.0) - 0.0012;
        float texel = u.misc.w > 0.0 ? u.misc.w : (1.0 / 2048.0);
        float s = 0.0;
        for (int dy = -1; dy <= 1; dy++) {
            for (int dx = -1; dx <= 1; dx++) {
                float d = texture(shadowMap, cuv + vec2(float(dx), float(dy)) * texel).r;
                s += (cz <= d) ? 1.0 : 0.0;
            }
        }
        s /= 9.0;
        shadow = mix(1.0, mix(0.55, 1.0, s), inMap * clamp(vSkyAmt, 0.0, 1.0) * dayLight);
    }

    vec3 col = tex.rgb * vColor * shadow;
    float alpha = tex.a * u.fog.w;

    float f = clamp((vFogDist - u.fog.x) / (u.fog.y - u.fog.x), 0.0, 1.0);
    col = mix(col, u.fogColor.rgb, f);
    o = vec4(col, alpha);
}
";

    // Shadow-pass vertex shader: writes only light-space depth (no fragment shader).
    // shadowMat is uploaded into the viewProj slot of ChunkShared for this pass, so
    // we reuse the same UBO + push-constant origin as the world pass.
    public const string ShadowVertex = @"#version 450

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uint inA;
layout(location = 3) in uint inB;

layout(set = 0, binding = 0, std140) uniform ChunkShared {
    mat4 viewProj;   // = shadowMat for this pass
    mat4 shadowMat;
    vec4 light;
    vec4 fog;
    vec4 fogColor;
    vec4 misc;
} u;

layout(push_constant) uniform Push { vec4 origin; } pc;

void main() {
    gl_Position = u.viewProj * vec4(inPos + pc.origin.xyz, 1.0);
}
";

    // Shadow-pass fragment shader: writes nothing (depth-only). Some drivers fault
    // on a vertex-only graphics pipeline rasterizing into a depth attachment, so we
    // attach this trivial (no color output) FS for a complete VS+FS pipeline.
    public const string ShadowFragment = @"#version 450
void main() { }
";
}

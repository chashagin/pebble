// GLSL (Vulkan) for the offscreen-HDR post-processing chain — the Windows port of
// the composite / bloom / ultra (SSAO + volumetric god-rays) fragment shaders in
// Sources/Pebble/Shaders.swift. The 3D scene renders into an R16G16B16A16_SFLOAT
// HDR target; these passes then:
//   * bloom_extract  — bright-pass the HDR scene (lum smoothstep 0.62..0.95)
//   * blur           — separable 5-tap gaussian (a couple of downsampled mips)
//   * ultra          — half-res SSAO (alpha) + shadow-marched volumetric light (rgb)
//   * composite      — ACES filmic tonemap + tint/vignette + bloom add + ultra add,
//                      writing to the swapchain (the UI pass then draws on top).
//
// All four are full-screen-triangle passes reusing the SkyShaders fullscreen-tri
// vertex convention (vid 0=(-1,-1) 1=(3,-1) 2=(-1,3)), with the framebuffer UV
// computed as (p.x*0.5+0.5, 0.5 - p.y*0.5) — same as the Metal fs_vs.

namespace Pebble.Gpu;

internal static class PostShaders
{
    // Shared full-screen-triangle vertex shader: emits a 0..1 UV that matches the
    // Metal fs_vs (top-left origin). All post passes use this single VS.
    public const string FullscreenVertex = @"#version 450

layout(location = 0) out vec2 vUv;

void main() {
    vec2 p = vec2(gl_VertexIndex == 1 ? 3.0 : -1.0, gl_VertexIndex == 2 ? 3.0 : -1.0);
    gl_Position = vec4(p, 0.0, 1.0);
    // Vulkan: framebuffer +Y is down (matches clip +Y down), so UV.y is NOT
    // flipped — clip (-1,-1) is the top-left texel, UV (0,0).
    vUv = vec2(p.x * 0.5 + 0.5, p.y * 0.5 + 0.5);
}
";

    // ── bloom bright-pass: extract pixels brighter than ~0.62 luminance ──
    // Shares the one-texture set layout (UBO@0 unused + sampler@1) with the blur.
    public const string BloomExtract = @"#version 450

layout(set = 0, binding = 0, std140) uniform CompositeU {
    vec4 params; vec4 tint; vec4 params2;
} u;
layout(set = 0, binding = 1) uniform sampler2D scene;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 o;

void main() {
    vec3 c = texture(scene, vUv).rgb;
    float lum = dot(c, vec3(0.299, 0.587, 0.114));
    float k = smoothstep(0.62, 0.95, lum);
    o = vec4(c * k, 1.0);
}
";

    // ── separable gaussian blur (5-tap). CompositeU.tint.xy carries the blur dir. ──
    public const string Blur = @"#version 450

layout(set = 0, binding = 0, std140) uniform CompositeU {
    vec4 params;    // bloomAmt, warp, time, darkness
    vec4 tint;      // reused: tint.xy = blur dir
    vec4 params2;   // ultraOn, aoStrength, volStrength, _
} u;

layout(set = 0, binding = 1) uniform sampler2D tex;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 o;

void main() {
    vec2 dir = u.tint.xy;
    vec3 c  = texture(tex, vUv).rgb * 0.227;
    c += texture(tex, vUv + dir * 1.384).rgb * 0.316;
    c += texture(tex, vUv - dir * 1.384).rgb * 0.316;
    c += texture(tex, vUv + dir * 3.230).rgb * 0.07;
    c += texture(tex, vUv - dir * 3.230).rgb * 0.07;
    o = vec4(c, 1.0);
}
";

    // ── ultra: half-res SSAO (alpha) + shadow-marched volumetric light (rgb) ──
    // Reconstructs camera-relative world position from depth + invViewProj.
    public const string Ultra = @"#version 450

layout(set = 0, binding = 0, std140) uniform UltraU {
    mat4 invViewProj;   // camera-relative clip -> world
    mat4 viewProj;
    mat4 shadowMat;
    vec4 sunDir;        // xyz + dayLight
    vec4 params;        // time, far, volumetricsOn, underwater
    vec4 fogColor;      // rgb + renderDistance(blocks)
    vec4 texel;         // 1/w, 1/h of the ultra target
} u;

layout(set = 0, binding = 1) uniform sampler2D depthTex;
layout(set = 0, binding = 2) uniform sampler2D shadowMap;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 o;

vec3 worldPosFromDepth(vec2 uv, float depth) {
    // Vulkan: UV(0,0) is the top-left texel which maps to clip.y = -1 (the world
    // pass's +Y-down projection). So ndc.y = uv.y*2 - 1. clip z already in [0,1].
    vec4 ndc = vec4(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0, depth, 1.0);
    vec4 p = u.invViewProj * ndc;
    return p.xyz / p.w;
}

void main() {
    float depth = texture(depthTex, vUv).r;
    vec3 wpos = worldPosFromDepth(vUv, depth);
    float dist = length(wpos);
    vec3 rayDir = wpos / max(dist, 1e-5);
    bool isSky = depth >= 0.99999;
    float dayLight = u.sunDir.w;

    // --- SSAO: golden-angle spiral hemisphere, depth-compared in screen space ---
    float ao = 1.0;
    if (!isSky && dist < 140.0) {
        vec2 px = u.texel.xy;
        vec3 pR = worldPosFromDepth(vUv + vec2(px.x, 0.0), texture(depthTex, vUv + vec2(px.x, 0.0)).r);
        vec3 pD = worldPosFromDepth(vUv + vec2(0.0, px.y), texture(depthTex, vUv + vec2(0.0, px.y)).r);
        vec3 nrm = normalize(cross(pD - wpos, pR - wpos));
        float ang0 = fract(sin(dot(vUv * 961.0, vec2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
        float occ = 0.0;
        const int TAPS = 8;
        for (int i = 0; i < TAPS; i++) {
            float a = ang0 + float(i) * 2.399963;
            float r = (float(i) + 0.7) / float(TAPS);
            float rad = 0.65 * r;
            vec3 t = vec3(cos(a), 0.0, sin(a));
            vec3 tang = normalize(t - nrm * dot(t, nrm));
            vec3 sp = wpos + (tang * rad + nrm * rad * 0.55);
            vec4 cp = u.viewProj * vec4(sp, 1.0);
            if (cp.w <= 0.0) continue;
            vec2 suv = vec2(cp.x / cp.w * 0.5 + 0.5, cp.y / cp.w * 0.5 + 0.5);
            if (suv.x < 0.0 || suv.x > 1.0 || suv.y < 0.0 || suv.y > 1.0) continue;
            float sd = texture(depthTex, suv).r;
            vec3 spos = worldPosFromDepth(suv, sd);
            vec3 dvec = spos - wpos;
            float dlen = length(dvec);
            if (dlen < 0.001) continue;
            float occA = max(0.0, dot(nrm, dvec / dlen) - 0.08);
            float fall = 1.0 - clamp(dlen / 1.6, 0.0, 1.0);
            occ += occA * fall;
        }
        ao = clamp(1.0 - occ / float(TAPS) * 2.4, 0.0, 1.0);
        ao = mix(ao, 1.0, clamp(dist / 140.0, 0.0, 1.0));
    }

    // --- volumetric light: march the camera ray, sample the shadow map ---
    vec3 vol = vec3(0.0);
    if (u.params.z > 0.5 && dayLight > 0.05) {
        vec3 sr = vec3(u.shadowMat[0].z, u.shadowMat[1].z, u.shadowMat[2].z);
        vec3 sunD = normalize(dot(sr, sr) > 1e-6 ? sr : vec3(0.0, 1.0, 0.0));
        if (sunD.y < 0.0) sunD = -sunD;
        float cosA = dot(rayDir, sunD);
        float g = 0.62;
        float phase = (1.0 - g * g) / (4.0 * 3.14159 * pow(1.0 + g * g - 2.0 * g * cosA, 1.5));
        float marchEnd = min(isSky ? u.params.y : dist, 72.0);
        const int STEPS = 18;
        float dither = fract(sin(dot(vUv * 917.0, vec2(36.887, 19.781))) * 24634.6345);
        float lit = 0.0;
        for (int i = 0; i < STEPS; i++) {
            float f = (float(i) + dither) / float(STEPS);
            f = f * f;
            vec3 p = rayDir * (f * marchEnd);
            vec4 sc = u.shadowMat * vec4(p, 1.0);
            vec3 sp = sc.xyz / sc.w;
            // shadowMat is built with the world pass's clip convention; map to tex.
            vec2 suv = vec2(sp.x * 0.5 + 0.5, sp.y * 0.5 + 0.5);
            if (suv.x <= 0.0 || suv.x >= 1.0 || suv.y <= 0.0 || suv.y >= 1.0 || sp.z >= 1.0) {
                lit += 0.6;
                continue;
            }
            float sd = texture(shadowMap, suv).r;
            lit += (clamp(sp.z, 0.0, 1.0) - 0.0015 <= sd) ? 1.0 : 0.0;
        }
        lit /= float(STEPS);
        float strength = 0.55 * dayLight * phase;
        vol = vec3(1.0, 0.92, 0.74) * lit * strength;
    }
    o = vec4(vol, ao);
}
";

    // ── ultra blur: gaussian that PRESERVES alpha (the AO channel) ──
    public const string UltraBlur = @"#version 450

layout(set = 0, binding = 0, std140) uniform CompositeU {
    vec4 params;
    vec4 tint;      // tint.xy = blur dir
    vec4 params2;
} u;

layout(set = 0, binding = 1) uniform sampler2D tex;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 o;

void main() {
    vec2 dir = u.tint.xy;
    vec4 c  = texture(tex, vUv) * 0.227;
    c += texture(tex, vUv + dir * 1.384) * 0.316;
    c += texture(tex, vUv - dir * 1.384) * 0.316;
    c += texture(tex, vUv + dir * 3.230) * 0.07;
    c += texture(tex, vUv - dir * 3.230) * 0.07;
    o = c;
}
";

    // ── composite: scene + bloom + ultra + ACES tonemap + tint/vignette ──
    // Always-on default final pass. Writes to the swapchain.
    public const string Composite = @"#version 450

layout(set = 0, binding = 0, std140) uniform CompositeU {
    vec4 params;    // bloomAmt, warp, time, darkness
    vec4 tint;      // rgb + amount
    vec4 params2;   // ultraOn, aoStrength, volStrength, _
} u;

layout(set = 0, binding = 1) uniform sampler2D scene;
layout(set = 0, binding = 2) uniform sampler2D bloom;
layout(set = 0, binding = 3) uniform sampler2D ultra;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 o;

vec3 acesTonemap(vec3 c) {
    c *= 0.92;
    return clamp((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14), 0.0, 1.0);
}

void main() {
    vec2 uv = vUv;
    float warp = u.params.y, time = u.params.z;
    if (warp > 0.001) {
        uv += vec2(sin(uv.y * 14.0 + time * 2.2), cos(uv.x * 12.0 + time * 1.8)) * 0.012 * warp;
    }
    vec3 c = texture(scene, uv).rgb;
    float ultraOn = u.params2.x;
    if (ultraOn > 0.5) {
        vec4 ul = texture(ultra, uv);
        c *= mix(1.0, ul.a, u.params2.y);   // SSAO
        c += ul.rgb * u.params2.z;          // volumetric light
    }
    c += texture(bloom, uv).rgb * u.params.x;
    c = mix(c, u.tint.rgb, u.tint.a);
    float darkness = u.params.w;
    if (darkness > 0.001) {
        float d = distance(uv, vec2(0.5));
        c *= mix(1.0, clamp(0.25 - d, 0.0, 0.25) * 4.0, darkness);
    }
    // ACES filmic tonemap (always on — gives the original's color grading) plus a
    // gentle saturation lift so the graded scene doesn't read flat.
    c = acesTonemap(c);
    float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = mix(vec3(lum), c, 1.12);
    o = vec4(c, 1.0);
}
";
}

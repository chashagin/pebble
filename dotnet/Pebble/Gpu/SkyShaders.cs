// GLSL (Vulkan) for the atmosphere passes: the full-screen sky gradient dome and
// the sun/moon celestial billboards. Direct translations of the Metal originals
// in Sources/Pebble/Shaders.swift (sky_vs/sky_fs, celestial_vs/celestial_fs) and
// their SkyU / CelestialU uniforms.
//
// The sky pass is a 3-vertex full-screen triangle drawn FIRST each frame with
// depth-write OFF and depth-compare ALWAYS at clip z ~ far, so terrain (and the
// celestials drawn at far depth) compose over it. invViewProj is the inverse of
// the same camera-relative viewProj the world pass uses, so the reconstructed
// ray direction is correct in world space.
//
// Convention: viewProj / invViewProj are uploaded UN-transposed (row-major bytes
// of the System.Numerics view*proj), so `M * v` in GLSL evaluates transpose(M)*v
// == v_row * M — exactly the clip coordinate the world pass produces. The same
// holds for invViewProj (built as Matrix4x4.Invert of that product).

namespace Pebble.Gpu;

internal static class SkyShaders
{
    // ── SkyU ──────────────────────────────────────────────────────────────────
    //   mat4 invViewProj; vec4 zenith; vec4 horizon; vec4 horizonSun; vec4 sunDir;
    // = 64 + 4*16 = 128 bytes.
    public const string SkyVertex = @"#version 450

layout(set = 0, binding = 0, std140) uniform SkyU {
    mat4 invViewProj;
    vec4 zenith;
    vec4 horizon;
    vec4 horizonSun;  // rgb + sunGlow
    vec4 sunDir;      // xyz + voidSky
} u;

layout(location = 0) out vec3 vDir;

void main() {
    // Full-screen triangle: vid 0=(-1,-1) 1=(3,-1) 2=(-1,3).
    vec2 p = vec2(gl_VertexIndex == 1 ? 3.0 : -1.0, gl_VertexIndex == 2 ? 3.0 : -1.0);
    gl_Position = vec4(p, 0.99999, 1.0);

    // Unproject near + far points; their difference is the view ray (world space).
    vec4 p0 = u.invViewProj * vec4(p, 0.0, 1.0);
    vec4 p1 = u.invViewProj * vec4(p, 1.0, 1.0);
    vDir = p1.xyz / p1.w - p0.xyz / p0.w;
}
";

    public const string SkyFragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform SkyU {
    mat4 invViewProj;
    vec4 zenith;
    vec4 horizon;
    vec4 horizonSun;
    vec4 sunDir;
} u;

layout(location = 0) in vec3 vDir;
layout(location = 0) out vec4 o;

void main() {
    vec3 d = normalize(vDir);
    float h = clamp(d.y, -1.0, 1.0);
    float t = pow(clamp(1.0 - h, 0.0, 1.0), 1.6);
    vec3 col = mix(u.zenith.rgb, u.horizon.rgb, t * step(0.0, h) + step(h, 0.0));
    if (h < 0.0) col = mix(u.horizon.rgb, u.zenith.rgb * 0.35, clamp(-h * 2.2, 0.0, 1.0));

    vec2 sd = u.sunDir.xz;
    float lsd = length(sd);
    float sunness = lsd < 1e-5 ? 0.0 : max(0.0, dot(normalize(d.xz), sd / lsd));
    float band = exp(-abs(h) * 5.0);
    col = mix(col, u.horizonSun.rgb, u.horizonSun.w * band * pow(sunness * 0.5 + 0.5, 3.0));

    if (u.sunDir.w > 0.5) {
        col = mix(vec3(0.03, 0.025, 0.05), vec3(0.09, 0.07, 0.12), clamp(h + 0.5, 0.0, 1.0));
    }
    o = vec4(col, 1.0);
}
";

    // ── CelestialU ────────────────────────────────────────────────────────────
    //   mat4 viewProj; vec4 center (xyz+size); vec4 right (xyz+texMode);
    //   vec4 up (xyz + moonPhase, <0 => sun).
    // The sun/moon are PROCEDURAL here (no pack art on Windows), so texMode is 0.
    public const string CelestialVertex = @"#version 450

layout(set = 0, binding = 0, std140) uniform CelestialU {
    mat4 viewProj;
    vec4 center;   // xyz + size
    vec4 right;    // xyz + texMode
    vec4 up;       // xyz + moonPhase (<0 = sun)
} u;

layout(location = 0) out vec2 vUv;

void main() {
    const vec2 corners[6] = vec2[6](
        vec2(-1,-1), vec2(1,-1), vec2(1,1), vec2(-1,-1), vec2(1,1), vec2(-1,1));
    vec2 a = corners[gl_VertexIndex];
    vec3 p = u.center.xyz + (a.x * u.right.xyz + a.y * u.up.xyz) * u.center.w;
    vec4 cp = u.viewProj * vec4(p, 1.0);
    // Force depth to the far plane (z = w) so celestials sit behind all terrain.
    gl_Position = vec4(cp.xy, cp.w, cp.w);
    vUv = a * 0.5 + 0.5;
}
";

    public const string CelestialFragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform CelestialU {
    mat4 viewProj;
    vec4 center;
    vec4 right;
    vec4 up;
} u;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 o;

void main() {
    vec2 dd = vUv - 0.5;
    float r = length(dd) * 2.0;
    float moonPhase = u.up.w;

    if (moonPhase < -0.5) {
        // sun: bright disc + warm halo, faded to zero before the quad edge
        float disc = smoothstep(0.62, 0.55, r);
        float glow = exp(-r * 2.4) * 0.55 * smoothstep(1.0, 0.72, r);
        vec3 col = vec3(1.0, 0.97, 0.85) * disc + vec3(1.0, 0.85, 0.6) * glow;
        o = vec4(col, max(disc, glow));
    } else {
        // moon: phased disc with a soft terminator
        float disc = smoothstep(0.5, 0.46, r);
        float ph = moonPhase;
        float shift = (ph - 0.5) * 2.2;
        float shadow = smoothstep(0.42, 0.5, length(dd * 2.0 + vec2(shift, 0.0)));
        vec3 col = vec3(0.92, 0.94, 1.0) * disc * mix(0.12, 1.0, shadow);
        col *= 1.0 - 0.16 * smoothstep(0.2, 0.1, length(dd - vec2(0.1, 0.08)));
        col *= 1.0 - 0.12 * smoothstep(0.16, 0.07, length(dd + vec2(0.12, -0.05)));
        o = vec4(col, disc);
    }
}
";

    // ── StarsU ──────────────────────────────────────────────────────────────────
    //   mat4 viewProj; vec4 params (time, alpha). Per-vertex: pos f3 @0, mag f1 @12.
    // Drawn as a point list at night; the gl_PointSize twinkle matches the Metal
    // stars_vs/stars_fs. The point sprite is shaded by gl_PointCoord in the FS.
    public const string StarsVertex = @"#version 450

layout(location = 0) in vec3 inPos;
layout(location = 1) in float inMag;

layout(set = 0, binding = 0, std140) uniform StarsU {
    mat4 viewProj;
    vec4 params;   // time, alpha
} u;

layout(location = 0) out float vBright;

void main() {
    vec4 cp = u.viewProj * vec4(inPos * 900.0, 1.0);
    gl_Position = vec4(cp.xy, cp.w, cp.w);   // depth = far plane
    gl_PointSize = 1.0 + inMag * 1.6;
    vBright = 0.55 + 0.45 * sin(u.params.x * (1.0 + inMag * 2.0) + inPos.x * 50.0);
}
";

    public const string StarsFragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform StarsU {
    mat4 viewProj;
    vec4 params;   // time, alpha
} u;

layout(location = 0) in float vBright;
layout(location = 0) out vec4 o;

void main() {
    vec2 d = gl_PointCoord - 0.5;
    float a = smoothstep(0.5, 0.1, length(d)) * vBright * u.params.y;
    o = vec4(vec3(0.95, 0.96, 1.0), a);
}
";

    // ── CloudU ──────────────────────────────────────────────────────────────────
    //   mat4 viewProj; vec4 offset (xyz + planeScale); vec4 scroll (sx, sy,
    //   brightness, fogEnd). A flat 6-vertex quad at cloud height, alpha-blended,
    //   sampling the cellular cloud texture. Matches Metal cloud_vs/cloud_fs.
    public const string CloudVertex = @"#version 450

layout(set = 0, binding = 0, std140) uniform CloudU {
    mat4 viewProj;
    vec4 offset;   // xyz + planeScale
    vec4 scroll;   // sx, sy, brightness, fogEnd
} u;

layout(location = 0) out vec2 vUv;
layout(location = 1) out float vDist;

void main() {
    const vec2 corners[6] = vec2[6](
        vec2(-1,-1), vec2(1,-1), vec2(1,1), vec2(-1,-1), vec2(1,1), vec2(-1,1));
    vec2 a = corners[gl_VertexIndex];
    vec3 p = vec3(a.x * u.offset.w, 0.0, a.y * u.offset.w) + u.offset.xyz;
    gl_Position = u.viewProj * vec4(p, 1.0);
    vUv = a * 0.5 + 0.5;
    vDist = length(p.xz);
}
";

    public const string CloudFragment = @"#version 450

layout(set = 0, binding = 0, std140) uniform CloudU {
    mat4 viewProj;
    vec4 offset;
    vec4 scroll;
} u;

layout(set = 0, binding = 1) uniform sampler2D cloudTex;

layout(location = 0) in vec2 vUv;
layout(location = 1) in float vDist;
layout(location = 0) out vec4 o;

void main() {
    float c = texture(cloudTex, vUv * 12.0 + u.scroll.xy).r;
    if (c < 0.5) discard;
    float fogEnd = u.scroll.w;
    float fade = 1.0 - clamp((vDist - fogEnd * 0.7) / (fogEnd * 0.6), 0.0, 1.0);
    o = vec4(vec3(u.scroll.z), 0.72 * fade);
}
";
}

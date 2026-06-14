// Computes the per-frame atmosphere parameters (sky gradient colors, sun/moon
// direction, day fraction, fog) from the engine's world time-of-day. This is the
// Windows port of the relevant parts of Sources/Pebble/WorldRenderer.swift:
//   * skyColors(world, cam)  -> zenith / horizon / fog / dayLight / sunGlow
//   * the sunDir / sunAngle / shadowOK math from render(...)
//
// It is backend-agnostic (plain System.Numerics) so both VulkanBackend and
// D3D12Backend feed the identical numbers into their sky/celestial/world passes.
// Nothing here touches the GPU; Program.cs builds one SkyState per frame from the
// live PebbleCore.World and hands it to IGpuBackend.RenderSky + RenderWorld.

using System;
using System.Numerics;
using PebbleCore;

namespace Pebble.Gpu;

/// Resolved atmosphere parameters for one frame (camera-relative directions).
public struct SkyState
{
    // Gradient endpoints (linear-ish 0..1 rgb).
    public Vector3 Zenith;
    public Vector3 Horizon;
    public Vector3 Fog;
    // Horizon sun-glow tint (rgb) + strength (a) — the warm dawn/dusk band.
    public Vector4 HorizonSun;
    // Sun direction (world space, camera-relative is identical since it's a
    // direction). Moon is -SunDir.
    public Vector3 SunDir;
    // 0..1 daylight strength used to drive terrain/entity lighting + star/celestial
    // fades. Already clamped to [0.06, 1] like the Metal original.
    public float DayLight;
    // True night-sky look (the End dimension); selects the dark gradient branch.
    public bool VoidSky;
    // True when the sun casts (overworld, daytime, above horizon) — for future use.
    public bool SunUp;
    // Fog distances (blocks), already adjusted for underwater/lava/etc by the caller.
    public float FogStart, FogEnd;
    // Phase index 0..7 for the moon billboard.
    public int MoonPhase;
    // Star fade alpha 0..1 (only meaningful in the overworld).
    public float StarAlpha;
    // True when the sun casts a shadow this frame (overworld, daytime, above horizon).
    public bool ShadowsOn;

    /// Build the sun-direction shadow matrix (lightView*lightProj, row-major so the
    /// shader's column-major M*v matches BuildViewProj), texel-snapped to the world
    /// so the shadow grid doesn't shimmer as the camera moves. `flipY` matches the
    /// backend's clip-space convention (Vulkan flips Y like its perspective proj;
    /// DX12 does not). Ports the shadow-pass block of WorldRenderer.render().
    public static Matrix4x4 BuildShadowMatrix(Vector3 sunDir, in CamState cam, int shadowSize, bool flipY)
    {
        const float r = 72f;
        // light view: eye at sunDir*120 looking toward the origin (camera-relative).
        var eye = sunDir * 120f;
        // pick an up vector not parallel to the sun direction (sun can be near +Y)
        var up = MathF.Abs(sunDir.Y) > 0.95f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, up);
        // ortho proj, z in [0,1] (near=1, far=320). Optional Y flip for Vulkan.
        var proj = Ortho(-r, r, -r, r, 1f, 320f, flipY);
        var shadowMat = Matrix4x4.Multiply(view, proj);

        // texel snap: pin the shadow grid to the world (anchor = -cam, the world
        // origin in camera-relative space) so sub-texel drift doesn't shimmer.
        var anchor = new Vector4((float)-cam.x, (float)-cam.y, (float)-cam.z, 1f);
        var anchorClip = Vector4.Transform(anchor, shadowMat);
        float texel = 2f / shadowSize;
        float sx = -(anchorClip.X % texel);
        float sy = -(anchorClip.Y % texel);
        var snap = Matrix4x4.Identity;
        snap.M41 = sx; snap.M42 = sy;   // row-major translation row
        return Matrix4x4.Multiply(shadowMat, snap);
    }

    // RH orthographic, z mapped to [0,1] (row-major, v*M). flipY negates the Y row.
    private static Matrix4x4 Ortho(float l, float r, float b, float t, float n, float f, bool flipY)
    {
        float sy = (flipY ? -1f : 1f) * 2f / (t - b);
        return new Matrix4x4(
            2f / (r - l), 0, 0, 0,
            0, sy, 0, 0,
            0, 0, 1f / (n - f), 0,
            -(r + l) / (r - l), (flipY ? 1f : -1f) * (t + b) / (t - b), n / (n - f), 1);
    }

    /// Build the frame's atmosphere from the live world + camera, mirroring
    /// WorldRenderer.skyColors + the sun-direction block in render().
    public static SkyState FromWorld(World world, in CamState cam, double renderDistanceChunks)
    {
        var s = new SkyState();
        var info = world.info;

        // ── per-dimension gradient (overworld is the day/night-driven case) ──
        if (world.dim == Dim.nether)
        {
            var f = info.fogColor;
            s.Zenith = new Vector3((float)(f.Item1 * 0.55), (float)(f.Item2 * 0.5), (float)(f.Item3 * 0.5));
            s.Horizon = new Vector3((float)f.Item1, (float)f.Item2, (float)f.Item3);
            s.Fog = s.Horizon;
            s.DayLight = 1f;
            s.HorizonSun = new Vector4(1f, 0.45f, 0.18f, 0f);
            s.SunDir = new Vector3(0, 1, 0);
        }
        else if (world.dim == Dim.end)
        {
            s.Zenith = new Vector3(0.03f, 0.025f, 0.05f);
            s.Horizon = new Vector3(0.1f, 0.08f, 0.13f);
            s.Fog = new Vector3(0.07f, 0.06f, 0.1f);
            s.DayLight = 1f;
            s.VoidSky = true;
            s.HorizonSun = new Vector4(1f, 0.45f, 0.18f, 0f);
            s.SunDir = new Vector3(0, 1, 0);
        }
        else
        {
            double angle = world.sunAngle();
            double sunH = DetMath.detCos(angle * Math.PI * 2);
            double day = Math.Min(1.0, Math.Max(0.0, sunH * 2 + 0.5));
            double dusk = Math.Min(1.0, Math.Max(0.0, 1 - Math.Abs(sunH) * 3.2));
            float rain = (float)world.rainLevel;

            var dayZen = new Vector3(0.45f, 0.65f, 1.0f);
            var nightZen = new Vector3(0.012f, 0.015f, 0.04f);
            var dayHor = new Vector3(0.74f, 0.84f, 1.0f);
            var nightHor = new Vector3(0.04f, 0.05f, 0.1f);
            var zenith = nightZen + (dayZen - nightZen) * (float)day;
            var horizon = nightHor + (dayHor - nightHor) * (float)day;
            float grayZ = (zenith.X + zenith.Y + zenith.Z) / 3;
            float grayH = (horizon.X + horizon.Y + horizon.Z) / 3;
            zenith += (new Vector3(grayZ * 0.7f, grayZ * 0.7f, grayZ * 0.75f) - zenith) * rain;
            horizon += (new Vector3(grayH * 0.75f, grayH * 0.75f, grayH * 0.8f) - horizon) * rain;

            s.Zenith = zenith;
            s.Horizon = horizon;
            s.Fog = horizon;
            s.DayLight = (float)Math.Min(1, Math.Max(0.06, day + cam.nightVision));
            s.HorizonSun = new Vector4(1.0f, 0.45f, 0.18f, (float)(dusk * (1 - rain)));
            s.StarAlpha = (float)(Math.Min(1.0, Math.Max(0.0, 1 - s.DayLight * 1.6)) * (1 - rain));
        }

        // ── sun direction (matches render(): a great-circle sweep with a small
        //    constant +Z lean so it isn't perfectly edge-on) ──
        double a2 = world.sunAngle();
        s.SunDir = Vector3.Normalize(new Vector3(
            (float)-Math.Sin(a2 * Math.PI * 2 + Math.PI),
            (float)Math.Cos(a2 * Math.PI * 2),
            0.18f));
        s.SunUp = world.dim == Dim.overworld && s.DayLight > 0.1 && s.SunDir.Y > 0.05;

        // moon phase 0..7 from world day count
        s.MoonPhase = (int)(((world.time / 24000) % 8 + 8) % 8);

        // shadows cast in the overworld, daytime, sun above the horizon
        s.ShadowsOn = world.dim == Dim.overworld && s.DayLight > 0.1 && s.SunDir.Y > 0.05;

        // ── fog distances (overworld defaults; caller overrides for underwater) ──
        float rd = (float)renderDistanceChunks * 16f;
        s.FogStart = rd * 0.55f;
        s.FogEnd = rd * 0.95f;
        if (cam.underwater)
        {
            // Approximate the engine's underwater fog (blue, short range). The exact
            // biome water tint isn't needed for the visible win; use a generic blue.
            s.Fog = new Vector3(0.1f, 0.2f, 0.45f);
            s.FogStart = 4f;
            s.FogEnd = 28f + (float)cam.nightVision * 40f;
        }
        else if (cam.underLava)
        {
            s.Fog = new Vector3(0.6f, 0.18f, 0.04f);
            s.FogStart = 0.2f; s.FogEnd = 2.2f;
        }
        else if (world.dim == Dim.nether)
        {
            s.FogStart = Math.Min(s.FogStart, 60f);
            s.FogEnd = Math.Min(s.FogEnd, 128f);
        }
        return s;
    }
}

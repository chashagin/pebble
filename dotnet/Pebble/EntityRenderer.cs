// Entity renderer — the Windows port of Sources/Pebble/EntityRendererM.swift.
//
// For each visible mob / dropped-item model it:
//   * resolves the model (from the ALREADY-PORTED PebbleCore.EntityModels data),
//   * builds the per-part pose matrices for the model's animator profile,
//   * builds the camera-relative model matrix (world pos − cam, yaw, scale),
//   * picks the procedural skin pixels,
// and hands a ready-to-draw EntityDrawList to the backend, which owns the GPU
// pipeline / per-model vertex buffer + skin texture cache.
//
// This file is backend-agnostic: it produces matrices + geometry + skin data;
// VulkanBackend / D3D12Backend consume EntityDrawList in their entity pass
// (drawn after the world, before the HUD). Matrices are built in the SAME
// column-vector ("simd") convention as the Metal original (EntityMat), and the
// backends upload them transposed so the GLSL/HLSL `M * v` reads them correctly
// — exactly the convention the world pass already uses for viewProj.
//
// The pose() switch is a direct line-by-line port of EntityRendererM.pose().

using System;
using System.Collections.Generic;
using PebbleCore;

namespace Pebble;

/// 4×4 matrix in column-major / column-vector ("simd_float4x4") convention,
/// matching the Metal original's mTranslate/mRotate*/mScale helpers exactly.
/// m[col*4 + row] addressing; helpers post-multiply (result = m * helper) so a
/// chain `m = T(m); m = R(m)` yields T*R applied to a column vector as T*R*v.
public struct EntityMat
{
    // column-major storage: c[c*4 + r]
    public float c0r0, c0r1, c0r2, c0r3;
    public float c1r0, c1r1, c1r2, c1r3;
    public float c2r0, c2r1, c2r2, c2r3;
    public float c3r0, c3r1, c3r2, c3r3;

    public static EntityMat Identity => new EntityMat
    {
        c0r0 = 1, c1r1 = 1, c2r2 = 1, c3r3 = 1,
    };

    // result = a * b (column-major 4x4 multiply: a's columns are basis vectors)
    public static EntityMat Mul(in EntityMat a, in EntityMat b)
    {
        EntityMat r;
        // r.col(j) = a * b.col(j)
        // a as column-major: a.col(k) = (a.c{k}r0..3). a*v = sum_k v[k]*a.col(k)
        r.c0r0 = a.c0r0 * b.c0r0 + a.c1r0 * b.c0r1 + a.c2r0 * b.c0r2 + a.c3r0 * b.c0r3;
        r.c0r1 = a.c0r1 * b.c0r0 + a.c1r1 * b.c0r1 + a.c2r1 * b.c0r2 + a.c3r1 * b.c0r3;
        r.c0r2 = a.c0r2 * b.c0r0 + a.c1r2 * b.c0r1 + a.c2r2 * b.c0r2 + a.c3r2 * b.c0r3;
        r.c0r3 = a.c0r3 * b.c0r0 + a.c1r3 * b.c0r1 + a.c2r3 * b.c0r2 + a.c3r3 * b.c0r3;

        r.c1r0 = a.c0r0 * b.c1r0 + a.c1r0 * b.c1r1 + a.c2r0 * b.c1r2 + a.c3r0 * b.c1r3;
        r.c1r1 = a.c0r1 * b.c1r0 + a.c1r1 * b.c1r1 + a.c2r1 * b.c1r2 + a.c3r1 * b.c1r3;
        r.c1r2 = a.c0r2 * b.c1r0 + a.c1r2 * b.c1r1 + a.c2r2 * b.c1r2 + a.c3r2 * b.c1r3;
        r.c1r3 = a.c0r3 * b.c1r0 + a.c1r3 * b.c1r1 + a.c2r3 * b.c1r2 + a.c3r3 * b.c1r3;

        r.c2r0 = a.c0r0 * b.c2r0 + a.c1r0 * b.c2r1 + a.c2r0 * b.c2r2 + a.c3r0 * b.c2r3;
        r.c2r1 = a.c0r1 * b.c2r0 + a.c1r1 * b.c2r1 + a.c2r1 * b.c2r2 + a.c3r1 * b.c2r3;
        r.c2r2 = a.c0r2 * b.c2r0 + a.c1r2 * b.c2r1 + a.c2r2 * b.c2r2 + a.c3r2 * b.c2r3;
        r.c2r3 = a.c0r3 * b.c2r0 + a.c1r3 * b.c2r1 + a.c2r3 * b.c2r2 + a.c3r3 * b.c2r3;

        r.c3r0 = a.c0r0 * b.c3r0 + a.c1r0 * b.c3r1 + a.c2r0 * b.c3r2 + a.c3r0 * b.c3r3;
        r.c3r1 = a.c0r1 * b.c3r0 + a.c1r1 * b.c3r1 + a.c2r1 * b.c3r2 + a.c3r1 * b.c3r3;
        r.c3r2 = a.c0r2 * b.c3r0 + a.c1r2 * b.c3r1 + a.c2r2 * b.c3r2 + a.c3r2 * b.c3r3;
        r.c3r3 = a.c0r3 * b.c3r0 + a.c1r3 * b.c3r1 + a.c2r3 * b.c3r2 + a.c3r3 * b.c3r3;
        return r;
    }

    // mTranslate(m, x,y,z) = m * T   (T has translation in column 3)
    public static EntityMat Translate(in EntityMat m, float x, float y, float z)
    {
        var t = Identity;
        t.c3r0 = x; t.c3r1 = y; t.c3r2 = z;
        return Mul(m, t);
    }
    public static EntityMat RotateX(in EntityMat m, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        var r = Identity;
        r.c1r1 = c; r.c1r2 = s;   // columns.1 = (0,c,s,0)
        r.c2r1 = -s; r.c2r2 = c;  // columns.2 = (0,-s,c,0)
        return Mul(m, r);
    }
    public static EntityMat RotateY(in EntityMat m, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        var r = Identity;
        r.c0r0 = c; r.c0r2 = -s;  // columns.0 = (c,0,-s,0)
        r.c2r0 = s; r.c2r2 = c;   // columns.2 = (s,0,c,0)
        return Mul(m, r);
    }
    public static EntityMat RotateZ(in EntityMat m, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        var r = Identity;
        r.c0r0 = c; r.c0r1 = s;   // columns.0 = (c,s,0,0)
        r.c1r0 = -s; r.c1r1 = c;  // columns.1 = (-s,c,0,0)
        return Mul(m, r);
    }
    public static EntityMat Scale(in EntityMat m, float x, float y, float z)
    {
        var s = Identity;
        s.c0r0 = x; s.c1r1 = y; s.c2r2 = z;
        return Mul(m, s);
    }

    /// Write into a float[16] in COLUMN-MAJOR order (as the GLSL/HLSL cbuffer
    /// expects: identical convention to the world pass's viewProj upload).
    public void WriteColumnMajor(float[] dst, int offset)
    {
        dst[offset + 0] = c0r0; dst[offset + 1] = c0r1; dst[offset + 2] = c0r2; dst[offset + 3] = c0r3;
        dst[offset + 4] = c1r0; dst[offset + 5] = c1r1; dst[offset + 6] = c1r2; dst[offset + 7] = c1r3;
        dst[offset + 8] = c2r0; dst[offset + 9] = c2r1; dst[offset + 10] = c2r2; dst[offset + 11] = c2r3;
        dst[offset + 12] = c3r0; dst[offset + 13] = c3r1; dst[offset + 14] = c3r2; dst[offset + 15] = c3r3;
    }
}

/// Per-entity draw record handed to the backend's entity pass. Matrices are in
/// EntityMat column-major; light/misc/overlay/fogColor mirror the Metal EntityU.
public sealed class EntityDraw
{
    public string model = "";          // resolved model key (cache id for VB + skin)
    public EntityMat modelMat;         // camera-relative world transform
    public EntityMat[] parts = null!;  // 24 part matrices
    public float lightSky, lightBlock, lightDay, lightGamma;
    public float ambient, alpha, fogStart, fogEnd;
    public float overlayR, overlayG, overlayB, overlayA;
    public float fogR, fogG, fogB;
}

/// The full set of entity draws for one frame plus the shared viewProj.
public sealed class EntityDrawList
{
    public readonly List<EntityDraw> draws = new();
    public void Clear() => draws.Clear();
}

/// CPU-side entity geometry + skin for one model, built once and cached by the
/// backend (which uploads the VB + a 2D skin texture keyed by model name).
public sealed class EntityModelGpuData
{
    public float[] verts = null!;      // 9 floats/vertex (pos3 normal3 uv2 part1)
    public int vertexCount;
    public int skinW, skinH;
    public byte[] skinRgba = null!;
    public MobModel model;
}

public sealed class EntityRenderer
{
    // CPU geometry/skin cache by model key (the backend caches the GPU resources).
    private readonly Dictionary<string, EntityModelGpuData> _geom = new();
    private readonly EntityMat[] _partMats = new EntityMat[24];

    /// Build (or fetch cached) CPU geometry + procedural skin for a model.
    public EntityModelGpuData Geom(string name)
    {
        if (_geom.TryGetValue(name, out var g)) return g;
        string resolved = EntityModelsGlobals.hasModel(name) ? name : "pig";
        var built = EntityModelsGlobals.buildEntityGeometry(resolved);
        var data = new EntityModelGpuData
        {
            verts = built.verts,
            vertexCount = built.vertexCount,
            skinW = built.skin.w,
            skinH = built.skin.h,
            skinRgba = built.skin.data,
            model = built.model,
        };
        _geom[name] = data;
        return data;
    }

    private static double WrapAngle(double a)
    {
        while (a > Math.PI) a -= Math.PI * 2;
        while (a < -Math.PI) a += Math.PI * 2;
        return a;
    }
    private static int IFloor(double v) => (int)Math.Floor(v);

    /// Resolve the model key for an entity (mirrors WorldRenderer.modelNameFor,
    /// limited to the models the ported EntityModels data actually defines). The
    /// projectile/vehicle placeholder models are not yet ported, so those return
    /// null and are skipped (they render as sprites in the macOS app).
    private static string? ModelNameFor(Entity ent)
    {
        string type = ent.type;
        if (type == "sheep" && ent.data.color is int c && c > 0 && !(ent.data.sheared ?? false)
            && EntityModelsGlobals.hasModel($"sheep_{c}"))
            return $"sheep_{c}";
        if (type == "villager" && ent is Villager vil && vil.profession != "none"
            && EntityModelsGlobals.hasModel($"villager_{vil.profession}"))
            return $"villager_{vil.profession}";
        if (EntityModelsGlobals.hasModel(type)) return type;
        return null;
    }

    /// Build the full per-frame draw list for the world's entities, exactly
    /// mirroring WorldRenderer.drawEntities + EntityRendererM.draw/pose.
    public void Build(EntityDrawList outList, GameCore game, double partial, double timeSec)
    {
        outList.Clear();
        var w = game.world;
        var cam = game.camState(partial, timeSec);
        double camX = cam.x, camY = cam.y, camZ = cam.z;
        double entDist = game.settings.entityDistance;
        double maxD = entDist * entDist;
        double dayLight = w.isDay() ? 1.0 : 0.0;
        // fog (start, end) and color mirror the world pass's constants.
        float fogStart = 350f, fogEnd = 512f;
        float fogR = 0.62f, fogG = 0.74f, fogB = 1.0f;
        double gamma = game.settings.gamma;
        double ambient = (double)w.info.ambientLight / 15.0;

        foreach (var er in w.entities)
        {
            if (er.dead) continue;
            if (er is not Entity ent) continue;
            if (ent.type == "player" && game.perspective == 0) continue;
            double dx = ent.x - camX, dz = ent.z - camZ;
            if (dx * dx + dz * dz > maxD) continue;
            string? name = ModelNameFor(ent);
            if (name == null) continue;

            var liv = ent as LivingEntity;
            var mob = ent as Mob;

            double ix = ent.prevX + (ent.x - ent.prevX) * partial;
            double iy = ent.prevY + (ent.y - ent.prevY) * partial;
            double iz = ent.prevZ + (ent.z - ent.prevZ) * partial;
            double yaw = ent.prevYaw + WrapAngle(ent.yaw - ent.prevYaw) * partial;
            int bx = IFloor(ent.x), by = IFloor(ent.y + ent.height * 0.5), bz = IFloor(ent.z);
            double deathFlip = (liv?.deathTime ?? 0) > 0 ? Math.Min(1.0, (double)liv!.deathTime / 20.0) : 0.0;

            // Pose fields (mirror EntityPose population in drawEntities).
            double pHeadYaw = liv != null ? WrapAngle(liv.headYaw - yaw) : 0.0;
            double pPitch = ent.pitch;
            double pLimbSwing = liv?.limbSwing ?? 0.0;
            double pLimbAmp = liv?.limbAmp ?? 0.0;
            double pAttackSwing = liv?.attackAnim ?? 0.0;
            double pHurtFlash = (liv?.hurtTime ?? 0) > 0 ? (double)liv!.hurtTime / 10.0 : deathFlip * 0.6;
            bool pBaby = ent.data.baby ?? false;
            int pSky = w.getSkyLight(bx, by, bz);
            int pBlock = w.getBlockLight(bx, by, bz);
            bool pAirborne = !ent.onGround;
            bool pAiming = mob?.target != null;
            bool pCrossed = pAiming;
            bool pGrazing = ent.data.grazing ?? false;
            double pOpen = (ent as Shulker)?.peekAmount ?? 0.0;
            bool pHanging = ent.type == "bat" && ent.onGround;
            double pAlpha = deathFlip > 0 ? 1.0 - deathFlip * 0.6 : 1.0;

            var g = Geom(name);

            // --- per-part pose matrices (EntityRendererM.pose) ---
            BuildPose(g.model, ix, pHeadYaw, pPitch, pLimbSwing, pLimbAmp, pAttackSwing,
                      pAiming, pCrossed, pGrazing, pAirborne, pHanging, pOpen, timeSec);

            // --- model matrix (camera-relative + yaw + scale) ---
            var m = EntityMat.Identity;
            m = EntityMat.Translate(m, (float)(ix - camX), (float)(iy - camY), (float)(iz - camZ));
            m = EntityMat.RotateY(m, (float)(-yaw));
            float sc = (float)(g.model.scale * (pBaby ? 0.5 : 1.0));
            m = EntityMat.Scale(m, sc, sc, sc);

            var draw = new EntityDraw
            {
                model = name,
                modelMat = m,
                parts = (EntityMat[])_partMats.Clone(),
                lightSky = pSky, lightBlock = pBlock, lightDay = (float)dayLight, lightGamma = (float)gamma,
                ambient = (float)ambient, alpha = (float)pAlpha, fogStart = fogStart, fogEnd = fogEnd,
                overlayR = 1f, overlayG = 0.2f, overlayB = 0.2f, overlayA = (float)(pHurtFlash * 0.5),
                fogR = fogR, fogG = fogG, fogB = fogB,
            };
            outList.draws.Add(draw);
        }
    }

    // Direct port of EntityRendererM.pose — fills _partMats[0..23].
    private void BuildPose(MobModel model, double px, double headYaw, double pitch,
                           double limbSwing, double limbAmp, double attackSwing,
                           bool aiming, bool crossed, bool grazing, bool airborne,
                           bool hanging, double open, double time)
    {
        double swing = limbSwing;
        double amp = limbAmp;
        double walkA = Math.Cos(swing * 0.6662) * 1.2 * amp;
        double walkB = Math.Cos(swing * 0.6662 + Math.PI) * 1.2 * amp;
        double idle = Math.Sin(time * 2 + px) * 0.02;
        string anim = model.anim;

        for (int i = 0; i < 24; i++)
        {
            if (i >= model.parts.Length) { _partMats[i] = EntityMat.Identity; continue; }
            var part = model.parts[i];
            var m = EntityMat.Identity;
            var (pvx, pvy, pvz) = part.pivot;
            m = EntityMat.Translate(m, (float)(pvx / 16), (float)(pvy / 16), (float)(pvz / 16));
            var (brx, bry, brz) = part.rot;
            if (brz != 0) m = EntityMat.RotateZ(m, (float)brz);
            if (bry != 0) m = EntityMat.RotateY(m, (float)bry);
            if (brx != 0) m = EntityMat.RotateX(m, (float)brx);
            string n = part.name;

            switch (anim)
            {
                case "biped": case "zombie": case "skeleton": case "illager": case "villager": case "fly_biped":
                    if (n == "head") { m = EntityMat.RotateY(m, (float)headYaw); m = EntityMat.RotateX(m, (float)(-pitch)); }
                    else if (n == "armR")
                    {
                        double rx = walkA * 0.8;
                        if (anim == "zombie" || (anim == "skeleton" && aiming)) rx = Math.PI / 2 + idle * 4;
                        if (attackSwing > 0) rx = Math.PI / 2 * Math.Sin(attackSwing * Math.PI) + 0.4;
                        if (anim == "illager" && crossed) rx = 0.7;
                        m = EntityMat.RotateX(m, (float)rx);
                    }
                    else if (n == "armL")
                    {
                        double rx = walkB * 0.8;
                        if (anim == "zombie") rx = Math.PI / 2 - idle * 4;
                        if (anim == "illager" && crossed) rx = 0.7;
                        m = EntityMat.RotateX(m, (float)rx);
                    }
                    else if (n == "legR") m = EntityMat.RotateX(m, (float)walkA);
                    else if (n == "legL") m = EntityMat.RotateX(m, (float)walkB);
                    else if (n == "wingR") m = EntityMat.RotateY(m, (float)(Math.Sin(time * 18) * 0.8 + 0.3));
                    else if (n == "wingL") m = EntityMat.RotateY(m, (float)(-Math.Sin(time * 18) * 0.8 - 0.3));
                    break;
                case "quad": case "quadTail": case "horse":
                    if (n == "head")
                    {
                        m = EntityMat.RotateY(m, (float)(headYaw * 0.6));
                        m = EntityMat.RotateX(m, (float)(-(pitch * 0.6) - (grazing ? 0.9 : 0)));
                    }
                    else if (n == "legFR" || n == "legBL") m = EntityMat.RotateX(m, (float)walkA);
                    else if (n == "legFL" || n == "legBR") m = EntityMat.RotateX(m, (float)walkB);
                    else if (n == "tail") m = EntityMat.RotateX(m, (float)(-0.6 - Math.Sin(time * 3) * 0.15 * (1 + amp * 2)));
                    break;
                case "creeper":
                    if (n == "head") { m = EntityMat.RotateY(m, (float)headYaw); m = EntityMat.RotateX(m, (float)(-pitch)); }
                    else if (n == "legFR" || n == "legBL") m = EntityMat.RotateX(m, (float)(walkA * 0.6));
                    else if (n == "legFL" || n == "legBR") m = EntityMat.RotateX(m, (float)(walkB * 0.6));
                    break;
                case "spider":
                    if (n.StartsWith("legR") || n.StartsWith("legL"))
                    {
                        double li = ParseTrailingInt(n, 4);
                        double side = n[3] == 'R' ? -1 : 1;
                        double lift = Math.Cos(swing * 0.6662 * 2 + li * 1.7) * 0.3 * amp;
                        m = EntityMat.RotateZ(m, (float)(-side * 0.55));
                        m = EntityMat.RotateY(m, (float)((li - 1.5) * 0.5236 * side));
                        m = EntityMat.RotateZ(m, (float)(-side * (0.05 + Math.Abs(lift))));
                    }
                    else if (n == "head") m = EntityMat.RotateY(m, (float)(headYaw * 0.4));
                    break;
                case "chicken":
                    if (n == "head") { m = EntityMat.RotateY(m, (float)headYaw); m = EntityMat.RotateX(m, (float)(-pitch)); }
                    else if (n == "legR") m = EntityMat.RotateX(m, (float)walkA);
                    else if (n == "legL") m = EntityMat.RotateX(m, (float)walkB);
                    else if (n == "wingR") m = EntityMat.RotateZ(m, (float)(airborne ? Math.Sin(time * 30) * 0.8 + 0.8 : 0));
                    else if (n == "wingL") m = EntityMat.RotateZ(m, (float)(airborne ? -Math.Sin(time * 30) * 0.8 - 0.8 : 0));
                    break;
                case "slime":
                {
                    double squish = 1 + Math.Sin(time * 6 + px) * 0.06 * (1 + amp);
                    m = EntityMat.Scale(m, (float)squish, (float)(1 / squish), (float)squish);
                    break;
                }
                case "blaze":
                    if (n.StartsWith("rod"))
                    {
                        double ri = ParseTrailingInt(n, 3);
                        double ring = Math.Floor(ri / 4);
                        double ang = (ri % 4) / 4 * Math.PI * 2 + time * (ring == 1 ? -1.1 : 1.3) + ring * 0.7;
                        double r = 5.0 / 16 + ring * 2 / 16;
                        m = EntityMat.Translate(m, (float)(Math.Cos(ang) * r),
                            (float)(-ring * 5 / 16 + Math.Sin(time * 3 + ri) * 0.04), (float)(Math.Sin(ang) * r));
                    }
                    else if (n == "head") { m = EntityMat.RotateY(m, (float)headYaw); m = EntityMat.RotateX(m, (float)(-pitch)); }
                    break;
                case "ghast":
                    if (n.StartsWith("tent"))
                    {
                        double ti = ParseTrailingInt(n, 4);
                        m = EntityMat.RotateX(m, (float)(Math.Sin(time * 2 + ti * 1.3) * 0.25));
                    }
                    break;
                case "squid":
                    if (n.StartsWith("tent"))
                    {
                        double ti = ParseTrailingInt(n, 4);
                        double ang = ti / 8 * Math.PI * 2;
                        double sway = Math.Sin(time * 4 + ti) * 0.3 + 0.4 * amp;
                        m = EntityMat.RotateX(m, (float)(Math.Cos(ang) * sway * 0.4));
                        m = EntityMat.RotateZ(m, (float)(-Math.Sin(ang) * sway * 0.4));
                    }
                    break;
                case "fish": case "dolphin":
                    if (n == "tail") m = EntityMat.RotateY(m, (float)(Math.Sin(time * 8 + swing) * 0.5));
                    else if (n == "body" && anim == "fish") m = EntityMat.RotateY(m, (float)(Math.Sin(time * 8) * 0.1));
                    else if (n == "head" && anim == "dolphin") m = EntityMat.RotateX(m, (float)(-pitch * 0.5));
                    break;
                case "guardian":
                    if (n.StartsWith("spike"))
                    {
                        int si = (int)ParseTrailingInt(n, 5);
                        var dirs = new (double, double, double)[]
                        {
                            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
                            (0.7, 0.7, 0), (-0.7, 0.7, 0), (0.7, -0.7, 0), (-0.7, -0.7, 0), (0, 0.7, 0.7), (0, 0.7, -0.7),
                        };
                        var d = dirs[si % dirs.Length];
                        double ext = 0.45 + Math.Sin(time * 2 + si) * 0.06;
                        m = EntityMat.Translate(m, (float)(d.Item1 * ext), (float)(d.Item2 * ext), (float)(d.Item3 * ext));
                        m = EntityMat.RotateX(m, (float)(d.Item3 != 0 ? (d.Item3 > 0 ? 1.0 : -1.0) * Math.PI / 2 * Math.Abs(d.Item3) : 0));
                        m = EntityMat.RotateZ(m, (float)(d.Item1 != 0 ? -(d.Item1 > 0 ? 1.0 : -1.0) * Math.PI / 2 * Math.Abs(d.Item1) : (d.Item2 < 0 ? Math.PI : 0)));
                    }
                    else if (n == "tail") m = EntityMat.RotateY(m, (float)(Math.Sin(time * 4) * 0.3));
                    break;
                case "shulker":
                    if (n == "lid")
                    {
                        m = EntityMat.Translate(m, 0, (float)(open * 0.45), 0);
                        m = EntityMat.RotateY(m, (float)(open * time * 1.5));
                    }
                    break;
                case "crystal":
                    if (n == "crystal")
                    {
                        m = EntityMat.Translate(m, 0, (float)(Math.Sin(time * 1.5) * 0.1), 0);
                        m = EntityMat.RotateY(m, (float)(time * 1.6));
                        m = EntityMat.RotateZ(m, 0.96f);
                    }
                    break;
                case "bat":
                    if (n == "wingR") m = EntityMat.RotateY(m, (float)(Math.Sin(time * 22) * 1 + 0.4));
                    else if (n == "wingL") m = EntityMat.RotateY(m, (float)(-Math.Sin(time * 22) * 1 - 0.4));
                    else if (n == "head" && hanging) m = EntityMat.RotateX(m, (float)Math.PI);
                    break;
                case "bee":
                    if (n == "wingR") m = EntityMat.RotateY(m, (float)(Math.Sin(time * 40) * 0.9 + 0.3));
                    else if (n == "wingL") m = EntityMat.RotateY(m, (float)(-Math.Sin(time * 40) * 0.9 - 0.3));
                    else if (n == "body") m = EntityMat.RotateX(m, (float)(Math.Sin(time * 3) * 0.08));
                    break;
                case "parrot":
                    if (n == "wingR" || n == "wingL")
                    {
                        double flap = airborne ? Math.Sin(time * 25) * 0.8 : 0;
                        m = EntityMat.RotateZ(m, (float)((n == "wingR" ? 1.0 : -1.0) * flap));
                    }
                    else if (n == "head") { m = EntityMat.RotateY(m, (float)headYaw); m = EntityMat.RotateX(m, (float)(-pitch)); }
                    break;
                case "phantom":
                    if (n == "wingR") m = EntityMat.RotateZ(m, (float)(Math.Sin(time * 4) * 0.3 + 0.1));
                    else if (n == "wingL") m = EntityMat.RotateZ(m, (float)(-Math.Sin(time * 4) * 0.3 - 0.1));
                    break;
                case "dragon":
                    if (n == "wingR") m = EntityMat.RotateZ(m, (float)(Math.Sin(time * 1.6) * 0.55 + 0.12));
                    else if (n == "wingL") m = EntityMat.RotateZ(m, (float)(-Math.Sin(time * 1.6) * 0.55 - 0.12));
                    else if (n == "head") { m = EntityMat.RotateY(m, (float)(headYaw * 0.5)); m = EntityMat.RotateX(m, (float)(-pitch * 0.5)); }
                    else if (n.StartsWith("tail"))
                    {
                        double ti = ParseTrailingInt(n, 4);
                        m = EntityMat.RotateY(m, (float)(Math.Sin(time * 1.2 - ti * 0.6) * 0.18));
                    }
                    else if (n.StartsWith("leg")) m = EntityMat.RotateX(m, 0.4f);
                    break;
                case "wither":
                    if (n == "head") { m = EntityMat.RotateY(m, (float)headYaw); m = EntityMat.RotateX(m, (float)(-pitch)); }
                    else if (n == "headR") m = EntityMat.RotateY(m, (float)(headYaw + Math.Sin(time * 1.4) * 0.3));
                    else if (n == "headL") m = EntityMat.RotateY(m, (float)(headYaw - Math.Sin(time * 1.7) * 0.3));
                    break;
                case "snowman":
                    if (n == "head") m = EntityMat.RotateY(m, (float)headYaw);
                    else if (n == "armR") m = EntityMat.RotateZ(m, (float)(Math.Sin(time * 2) * 0.05));
                    else if (n == "armL") m = EntityMat.RotateZ(m, (float)(-Math.Sin(time * 2) * 0.05));
                    break;
                case "strider":
                    if (n == "legR") m = EntityMat.RotateX(m, (float)walkA);
                    else if (n == "legL") m = EntityMat.RotateX(m, (float)walkB);
                    break;
                case "rabbit": case "frog":
                {
                    double hop = Math.Min(1, Math.Max(0, Math.Sin(swing * 1.2) * amp * 2));
                    if (n.StartsWith("legB")) m = EntityMat.RotateX(m, (float)(-hop * 0.8));
                    else if (n.StartsWith("legF")) m = EntityMat.RotateX(m, (float)(hop * 0.5));
                    break;
                }
                case "silverfish":
                    if (n == "body") m = EntityMat.RotateY(m, (float)(Math.Sin(swing * 1.5) * 0.15 * amp));
                    break;
                default:
                    break;
            }
            _partMats[i] = m;
        }
    }

    // Parse the integer suffix of a part name after `prefixLen` chars (e.g.
    // "legR2" with prefixLen 4 -> 2; "tent10" with prefixLen 4 -> 10). Returns 0
    // when there is no parseable suffix (matches the Swift `Int(...) ?? 0`).
    private static double ParseTrailingInt(string n, int prefixLen)
    {
        if (n.Length <= prefixLen) return 0;
        return int.TryParse(n.Substring(prefixLen), out int v) ? v : 0;
    }
}

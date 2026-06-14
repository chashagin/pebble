// Gameplay-feedback render data builders (backend-agnostic), the Windows port of
// the sprite-entity + block-break-crack paths in Sources/Pebble/WorldRenderer.swift
// (drawSprites + drawCrack).
//
//   * SpriteList   — camera-facing item billboards for the SPRITE_TYPES entities
//                    (arrows/thrown items/dropped ItemEntity/fireballs/xp orbs).
//                    Each sprite references an icon Texture2DArray layer (resolved
//                    through the backend's IconLayerResolver) plus a light/fog tint.
//   * BreakOverlay — the destroy_N crack mesh wrapping the targeted block's faces,
//                    emitted in the engine's 28-byte chunk vertex format so the
//                    backends can draw it with their existing world (translucent)
//                    pipeline + an origin push-constant.
//
// Both are built on the CPU each frame from the live GameCore and consumed by the
// backend's billboard / overlay passes. Sprite math mirrors drawSprites exactly
// (sub-UV is the whole icon tile here: the backend's icon array stores one icon
// per layer, so uvRect is 0..1).

using System;
using System.Collections.Generic;
using PebbleCore;
using static PebbleCore.ItemGlobals;
using static PebbleCore.Reg;
using static PebbleCore.BlockShapes;

namespace Pebble;

/// One camera-facing item billboard (a SPRITE_TYPES entity or dropped item).
public struct SpriteDraw
{
    public float x, y, z;      // world position (camera-relative offset applied by backend)
    public float size;         // billboard size (world units)
    public float bob;          // vertical bob offset (dropped items spin/float)
    public int iconLayer;      // layer in the backend icon Texture2DArray
    public float light;        // 0..1 brightness (emissive sprites pass 1)
}

public sealed class SpriteList
{
    public readonly List<SpriteDraw> sprites = new();
    public float fogStart, fogEnd;
    public float fogR, fogG, fogB;
    public float rightX, rightZ;     // camera right vector (xz) for the billboard
    public float dayLight;
    public void Clear() => sprites.Clear();
}

/// The destroy_N crack overlay mesh for the targeted block (chunk vertex format).
public sealed class BreakOverlay
{
    public bool active;
    public float originX, originY, originZ;  // (breaking block - cam)
    public uint[] verts = Array.Empty<uint>(); // 7 uint words/vertex (pos as float bits)
    public uint[] idx = Array.Empty<uint>();
    public int vertexCount;
    public int indexCount;
}

public static class WorldFx
{
    private static int IFloor(double v) => (int)Math.Floor(v);

    // sprite icon name map for the projectile/thrown SPRITE_TYPES (drawSprites)
    private static readonly Dictionary<string, string> SpriteMap = new()
    {
        { "snowball", "snowball" }, { "egg", "egg" }, { "ender_pearl", "ender_pearl" },
        { "xp_bottle", "experience_bottle" }, { "thrown_potion", "splash_potion" },
        { "firework", "firework_rocket" }, { "eye_of_ender", "ender_eye" },
        { "fishing_bobber", "string" }, { "wither_skull", "wither_skeleton_skull_item" },
        { "dragon_fireball", "fire_charge" }, { "fireball", "fire_charge" },
        { "shulker_bullet", "shulker_shell" }, { "llama_spit", "snowball" },
    };

    /// Build the per-frame sprite list (direct port of WorldRenderer.drawSprites).
    /// `resolveIcon` maps (itemId, data) → icon array layer (backend-owned).
    public static void BuildSprites(SpriteList outList, GameCore game, double partial,
                                    Func<int, PebbleCore.StackData?, int> resolveIcon)
    {
        outList.Clear();
        var w = game.world;
        var cam = game.camState(partial, 0);
        double camX = cam.x, camY = cam.y, camZ = cam.z;
        double dayLight = w.isDay() ? 1.0 : 0.0;

        outList.fogStart = 350f; outList.fogEnd = 512f;
        outList.fogR = 0.62f; outList.fogG = 0.74f; outList.fogB = 1.0f;
        outList.rightX = (float)DetMath.detCos(cam.yaw);
        outList.rightZ = (float)DetMath.detSin(cam.yaw);
        outList.dayLight = (float)dayLight;

        foreach (var er in w.entities)
        {
            if (er.dead) continue;
            if (er is not Entity ent) continue;
            ItemStack? stack = null;
            double size = 0.45;
            double emissive = 0.0;
            if (ent.type == "item")
            {
                stack = (ent as ItemEntity)?.stack;
            }
            else if (ent.type == "xp_orb")
            {
                stack = new ItemStack(iid("experience_bottle"), 1);
                size = 0.3; emissive = 1;
            }
            else if (GameCoreGlobals.SPRITE_TYPES.Contains(ent.type))
            {
                int id = iidOpt(SpriteMap.TryGetValue(ent.type, out var nm) ? nm : "snowball") ?? iid("snowball");
                stack = new ItemStack(id, 1);
                size = 0.35;
                if (ent.type == "fireball" || ent.type == "dragon_fireball" || ent.type == "wither_skull") emissive = 1;
            }
            if (stack == null) continue;

            double dx = ent.x - camX, dz = ent.z - camZ;
            if (dx * dx + dz * dz > 64 * 64) continue;

            int layer = resolveIcon(stack.id, stack.data);
            if (layer < 0) continue;

            double ix = ent.prevX + (ent.x - ent.prevX) * partial;
            double iy = ent.prevY + (ent.y - ent.prevY) * partial;
            double iz = ent.prevZ + (ent.z - ent.prevZ) * partial;
            int bx = IFloor(ent.x), by = IFloor(ent.y + 0.3), bz = IFloor(ent.z);
            double sky = Math.Max(0, (double)w.getSkyLight(bx, by, bz) - w.skyDarken());
            double light = Math.Max((double)w.info.ambientLight,
                Math.Max(sky * dayLight * 15 / Math.Max(1, 15 - w.skyDarken()), (double)w.getBlockLight(bx, by, bz)));
            double bob = ent.type == "item"
                ? DetMath.detSin(((double)ent.age + partial) * 0.08) * 0.08 + 0.12
                : 0;
            double brightness = emissive > 0 ? 1.0 : Math.Min(1, Math.Max(0.12, light / 15)) * (0.35 + dayLight * 0.65) + 0.08;

            outList.sprites.Add(new SpriteDraw
            {
                x = (float)ix, y = (float)iy, z = (float)iz,
                size = (float)size, bob = (float)bob,
                iconLayer = layer, light = (float)brightness,
            });
        }
    }

    /// Build the destroy_N crack overlay mesh for the currently-mined block (direct
    /// port of WorldRenderer.drawCrack). Returns false (overlay inactive) when the
    /// player isn't mining or is in creative.
    public static bool BuildBreakOverlay(BreakOverlay overlay, GameCore game, double partial)
    {
        overlay.active = false;
        var p = game.player;
        if (p == null) return false;
        if (p.breakingProgress < 0 || p.gameMode == GameMode.creative) return false;

        var cam = game.camState(partial, 0);
        var w = game.world;
        int cell = w.getBlock(p.breakingX, p.breakingY, p.breakingZ);
        int stage = Math.Min(9, Math.Max(0, (int)(p.breakingProgress * 10)));

        var scratch = new List<AABB>();
        shapeBoxes(cell, (dx, dy, dz) => w.getBlock(p.breakingX + dx, p.breakingY + dy, p.breakingZ + dz), scratch, false);
        if (scratch.Count == 0) scratch.Add(new AABB(0, 0, 0, 1, 1, 1));

        var verts = new List<uint>();
        var idx = new List<uint>();
        int layer = tileId($"destroy_{stage}");
        const float g = 0.004f;
        uint A = (uint)((layer & 4095) | (3 << 15) | (15 << 17) | (15 << 21));
        // face UVs (matches drawCrack): [0,1],[1,1],[1,0],[0,0]
        var uvs = new float[4, 2] { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } };

        foreach (var b in scratch)
        {
            float x0 = (float)b.x0 - g, y0 = (float)b.y0 - g, z0 = (float)b.z0 - g;
            float x1 = (float)b.x1 + g, y1 = (float)b.y1 + g, z1 = (float)b.z1 + g;
            float[][][] faces = new float[][][]
            {
                new[] { new[]{x0,y0,z1}, new[]{x1,y0,z1}, new[]{x1,y0,z0}, new[]{x0,y0,z0} },
                new[] { new[]{x0,y1,z0}, new[]{x1,y1,z0}, new[]{x1,y1,z1}, new[]{x0,y1,z1} },
                new[] { new[]{x1,y0,z0}, new[]{x1,y1,z0}, new[]{x0,y1,z0}, new[]{x0,y0,z0} },
                new[] { new[]{x0,y0,z1}, new[]{x0,y1,z1}, new[]{x1,y1,z1}, new[]{x1,y0,z1} },
                new[] { new[]{x0,y0,z0}, new[]{x0,y1,z0}, new[]{x0,y1,z1}, new[]{x0,y0,z1} },
                new[] { new[]{x1,y0,z1}, new[]{x1,y1,z1}, new[]{x1,y1,z0}, new[]{x1,y0,z0} },
            };
            for (int fi = 0; fi < 6; fi++)
            {
                uint baseI = (uint)(verts.Count / 7);
                for (int i = 0; i < 4; i++)
                {
                    var c = faces[fi][i];
                    verts.Add(BitConverter.SingleToUInt32Bits(c[0]));
                    verts.Add(BitConverter.SingleToUInt32Bits(c[1]));
                    verts.Add(BitConverter.SingleToUInt32Bits(c[2]));
                    verts.Add(BitConverter.SingleToUInt32Bits(uvs[i, 0]));
                    verts.Add(BitConverter.SingleToUInt32Bits(uvs[i, 1]));
                    verts.Add(A | (uint)(fi << 12));
                    verts.Add(0xffffffu);
                }
                idx.Add(baseI); idx.Add(baseI + 1); idx.Add(baseI + 2);
                idx.Add(baseI + 2); idx.Add(baseI + 3); idx.Add(baseI);
            }
        }

        overlay.active = true;
        overlay.originX = (float)(p.breakingX - cam.x);
        overlay.originY = (float)(p.breakingY - cam.y);
        overlay.originZ = (float)(p.breakingZ - cam.z);
        overlay.verts = verts.ToArray();
        overlay.idx = idx.ToArray();
        overlay.vertexCount = verts.Count / 7;
        overlay.indexCount = idx.Count;
        return true;
    }
}

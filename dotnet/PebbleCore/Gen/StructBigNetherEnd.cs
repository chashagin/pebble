// Ocean monuments, woodland mansions and nether
// fortresses, bastion remnants, end cities.
//
// Ported from Sources/PebbleCore/Gen/StructBigNetherEnd.swift.
//
// Defines static class: StructBigNetherEndGlobals
//   (module-level free funcs/state: registerBigStructures, registerNetherEndStructures,
//    registerAllStructures — the master structure entrypoint) — surface via
//    `global using static PebbleCore.StructBigNetherEndGlobals`.
//
// External symbols referenced by name (defined elsewhere in the assembly):
//   registerOverworldStructures (StructOverworld), registerUndergroundStructures (StructUnderground),
//   BEValue/BESpec/EntitySpec/ChunkSink (Gen/Features), Biome (Gen/Biomes), Dim (World/Chunk),
//   and everything surfaced via global-using static (Reg/B, BlockCaches.cell, WorldGlobals.posMod,
//   StructFrameworkGlobals.registerStructure/piece, etc.).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class StructBigNetherEndGlobals
{
    private const int AIR = 0;

    public static void registerBigStructures()
    {
        var W = (int)cell(B.water, 0);

        registerStructure(new StructureDef(
            id: "ocean_monument", spacing: 32, separation: 5, salt: 10387313, maxRadiusChunks: 3,
            check: (ctx, ocx, ocz, _) =>
            {
                var b = ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8);
                return b == (int)Biome.deepOcean || b == (int)Biome.deepColdOcean
                    || b == (int)Biome.deepLukewarmOcean || b == (int)Biome.deepFrozenOcean;
            },
            plan: (_, ocx, ocz, _) =>
            {
                int x0 = ocx * 16 - 21, z0 = ocz * 16 - 21;
                var y0 = 39;
                int PR = (int)cell(B.prismarine), PB = (int)cell(B.prismarine_bricks), SL = (int)cell(B.sea_lantern);
                return (StructurePlan?)new StructurePlan(id: "ocean_monument", pieces: new List<StructPiece> {
                    piece(x0, y0 - 2, z0, x0 + 57, y0 + 22, z0 + 57, b =>
                    {
                        // platform
                        for (int dz = 0; dz < 58; dz++) { for (int dx = 0; dx < 58; dx++) { b.set(x0 + dx, y0 - 1, z0 + dz, PR); } }
                        // outer wall ring
                        for (int h = 0; h < 18; h++)
                        {
                            for (int d = 0; d < 58; d++)
                            {
                                var edge = h < 2 || d % 14 < 2;
                                b.set(x0 + d, y0 + h, z0, edge ? PB : PR);
                                b.set(x0 + d, y0 + h, z0 + 57, edge ? PB : PR);
                                b.set(x0, y0 + h, z0 + d, edge ? PB : PR);
                                b.set(x0 + 57, y0 + h, z0 + d, edge ? PB : PR);
                            }
                        }
                        // interior water
                        for (int h = 0; h < 18; h++)
                        {
                            for (int dz = 1; dz < 57; dz++) { for (int dx = 1; dx < 57; dx++) { b.set(x0 + dx, y0 + h, z0 + dz, W); } }
                        }
                        // roof
                        for (int dz = 0; dz < 58; dz++) { for (int dx = 0; dx < 58; dx++) {
                            b.set(x0 + dx, y0 + 18, z0 + dz, (dx + dz) % 9 == 0 ? SL : PR);
                        } }
                        // entrance (north): gap in wall
                        b.fill(x0 + 26, y0, z0, x0 + 31, y0 + 8, z0 + 1, W);
                        // pillars at corners
                        foreach (var (px, pz) in new (int, int)[] { (6, 6), (51, 6), (6, 51), (51, 51) })
                        {
                            b.fill(x0 + px - 1, y0, z0 + pz - 1, x0 + px + 1, y0 + 17, z0 + pz + 1, PB);
                        }
                        // central core with gold
                        var cx = x0 + 29; var cz = z0 + 29;
                        b.fill(cx - 4, y0 + 2, cz - 4, cx + 4, y0 + 12, cz + 4, PB);
                        b.fill(cx - 3, y0 + 3, cz - 3, cx + 3, y0 + 11, cz + 3, W);
                        b.fill(cx - 1, y0 + 6, cz - 1, cx + 1, y0 + 7, cz + 1, (int)cell(B.gold_block));
                        b.fill(cx - 4, y0 + 7, cz - 4, cx - 4, y0 + 8, cz + 4, W); // openings
                        b.fill(cx + 4, y0 + 7, cz - 4, cx + 4, y0 + 8, cz + 4, W);
                        b.set(cx, y0 + 12, cz, SL);
                        // sponge room
                        var sx = x0 + 12; var sz = z0 + 40;
                        b.fill(sx, y0 + 10, sz, sx + 8, y0 + 14, sz + 8, PB);
                        b.fill(sx + 1, y0 + 11, sz + 1, sx + 7, y0 + 13, sz + 7, AIR);
                        for (int _i = 0; _i < 12; _i++)
                        {
                            b.set(sx + 1 + b.rng.nextInt(7), y0 + 13, sz + 1 + b.rng.nextInt(7), (int)cell(B.wet_sponge));
                        }
                        // elder guardians
                        b.mob("elder_guardian", cx, y0 + 9, cz);
                        b.mob("elder_guardian", x0 + 8, y0 + 6, z0 + 8);
                        b.mob("elder_guardian", x0 + 49, y0 + 6, z0 + 49);
                    }),
                }, @ref: new StructRefBox(x0, y0 - 2, z0, x0 + 57, y0 + 22, z0 + 57));
            }
        ));

        registerStructure(new StructureDef(
            id: "woodland_mansion", spacing: 80, separation: 20, salt: 10387319, maxRadiusChunks: 4,
            check: (ctx, ocx, ocz, _) =>
                ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8) == (int)Biome.darkForest,
            plan: (ctx, ocx, ocz, rng) =>
            {
                int x0 = ocx * 16 - 16, z0 = ocz * 16 - 24;
                var y = ctx.heightAt(ocx * 16 + 8, ocz * 16 + 8);
                var pieces = new List<StructPiece>();
                int PLANK = (int)cell(B.dark_oak_planks), LOG = (int)cell(B.dark_oak_log);
                var CARPET = (int)cell(B.red_carpet);
                int COBBLE = (int)cell(B.cobblestone), GLASS = (int)cell(B.glass_pane);
                var BIRCH = (int)cell(B.birch_planks);
                // room grid: 5 × 4 rooms of 8×8, 3 floors
                int ROOMS_X = 5, ROOMS_Z = 4, ROOM = 8, FLOORS = 3, FLOOR_H = 6;
                int width = ROOMS_X * ROOM + 2, depth = ROOMS_Z * ROOM + 2;

                // foundation + shell
                pieces.Add(piece(x0 - 2, y - 10, z0 - 2, x0 + width + 2, y + FLOORS * FLOOR_H + 8, z0 + depth + 2, b =>
                {
                    for (int dz = 0; dz <= depth; dz++) { for (int dx = 0; dx <= width; dx++) {
                        b.foundation(x0 + dx, y - 1, z0 + dz, COBBLE, 10);
                    } }
                    // outer walls
                    for (int f = 0; f < FLOORS; f++)
                    {
                        var fy = y + f * FLOOR_H;
                        for (int h = 0; h < FLOOR_H; h++)
                        {
                            for (int d = 0; d <= width; d++)
                            {
                                var isWin = h >= 2 && h <= 3 && d % 6 == 3;
                                b.set(x0 + d, fy + h, z0, isWin ? GLASS : PLANK);
                                b.set(x0 + d, fy + h, z0 + depth, isWin ? GLASS : PLANK);
                            }
                            for (int d = 0; d <= depth; d++)
                            {
                                var isWin = h >= 2 && h <= 3 && d % 6 == 3;
                                b.set(x0, fy + h, z0 + d, isWin ? GLASS : PLANK);
                                b.set(x0 + width, fy + h, z0 + d, isWin ? GLASS : PLANK);
                            }
                        }
                        // floor
                        for (int dz = 1; dz < depth; dz++) { for (int dx = 1; dx < width; dx++) {
                            b.set(x0 + dx, fy - 1, z0 + dz, f == 0 ? COBBLE : BIRCH);
                            for (int h = 0; h < FLOOR_H - 1; h++) { b.set(x0 + dx, fy + h, z0 + dz, AIR); }
                        } }
                    }
                    // roof
                    for (int dz = -1; dz <= depth + 1; dz++) { for (int dx = -1; dx <= width + 1; dx++) {
                        b.set(x0 + dx, y + FLOORS * FLOOR_H, z0 + dz, PLANK);
                        b.set(x0 + dx, y + FLOORS * FLOOR_H + 1, z0 + dz, posMod(dx, 4) == 0 ? (int)cell(B.dark_oak_slab, 0) : AIR);
                    } }
                    // corner pillars
                    foreach (var (px, pz) in new (int, int)[] { (0, 0), (width, 0), (0, depth), (width, depth) })
                    {
                        for (int h = 0; h < FLOORS * FLOOR_H + 2; h++) { b.set(x0 + px, y + h, z0 + pz, LOG); }
                    }
                    // entrance
                    b.fill(x0 + width / 2 - 1, y, z0, x0 + width / 2 + 1, y + 3, z0 + 1, AIR);
                }));

                // rooms with interior walls + furnishings + mobs
                var roomKinds = new List<string> { "bedroom", "library", "dining", "storage", "allay", "conference", "flower", "plain", "lootRare" };
                for (int f = 0; f < FLOORS; f++)
                {
                    for (int rz = 0; rz < ROOMS_Z; rz++)
                    {
                        for (int rx = 0; rx < ROOMS_X; rx++)
                        {
                            int roomX = x0 + 1 + rx * ROOM, roomZ = z0 + 1 + rz * ROOM;
                            var fy = y + f * FLOOR_H;
                            var kind = rng.pick(roomKinds);
                            var hasEastWall = rx < ROOMS_X - 1;
                            var hasSouthWall = rz < ROOMS_Z - 1;
                            pieces.Add(piece(roomX, fy, roomZ, roomX + ROOM, fy + FLOOR_H - 1, roomZ + ROOM, b =>
                            {
                                // interior walls with door gaps
                                if (hasEastWall)
                                {
                                    for (int h = 0; h < FLOOR_H - 1; h++)
                                    {
                                        for (int d = 0; d < ROOM; d++)
                                        {
                                            if (d == 4 && h < 2) continue; // doorway
                                            b.set(roomX + ROOM, fy + h, roomZ + d, PLANK);
                                        }
                                    }
                                }
                                if (hasSouthWall)
                                {
                                    for (int h = 0; h < FLOOR_H - 1; h++)
                                    {
                                        for (int d = 0; d < ROOM; d++)
                                        {
                                            if (d == 4 && h < 2) continue;
                                            b.set(roomX + d, fy + h, roomZ + ROOM, PLANK);
                                        }
                                    }
                                }
                                int midX = roomX + 3, midZ = roomZ + 3;
                                switch (kind)
                                {
                                    case "bedroom":
                                        b.set(midX, fy, midZ, (int)cell(B.red_bed, 0 | 4));
                                        b.set(midX, fy, midZ + 1, (int)cell(B.red_bed, 0));
                                        b.set(midX + 2, fy, midZ, (int)cell(B.chest, 1));
                                        b.set(midX - 1, fy, midZ - 1, CARPET);
                                        break;
                                    case "library":
                                        for (int i = 0; i < 3; i++)
                                        {
                                            for (int h = 0; h < 3; h++)
                                            {
                                                b.set(roomX + 1, fy + h, roomZ + 1 + i * 2, (int)cell(B.bookshelf));
                                                b.set(roomX + 5, fy + h, roomZ + 1 + i * 2, (int)cell(B.bookshelf));
                                            }
                                        }
                                        break;
                                    case "dining":
                                        b.fill(midX - 1, fy, midZ, midX + 1, fy, midZ, (int)cell(B.dark_oak_slab, 1));
                                        b.set(midX - 2, fy, midZ, (int)cell(B.dark_oak_stairs, 3));
                                        b.set(midX + 2, fy, midZ, (int)cell(B.dark_oak_stairs, 2));
                                        break;
                                    case "storage":
                                        b.chest(midX, fy, midZ, 0, "woodland_mansion");
                                        b.set(midX + 1, fy, midZ, (int)cell(B.barrel, 1));
                                        break;
                                    case "allay":
                                        // jail cell with allay
                                        b.fill(midX - 1, fy, midZ - 1, midX + 1, fy + 2, midZ + 1, (int)cell(B.dark_oak_fence));
                                        b.fill(midX, fy, midZ, midX, fy + 1, midZ, AIR);
                                        b.mob("allay", midX, fy, midZ, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                                        break;
                                    case "conference":
                                        for (int i = 0; i < 4; i++) { b.set(roomX + 1 + i, fy, roomZ + 2, (int)cell(B.dark_oak_stairs, 1)); }
                                        b.set(midX, fy + 3, midZ, (int)cell(B.lantern, 1));
                                        break;
                                    case "flower":
                                        b.set(midX, fy, midZ, (int)cell(B.flower_pot));
                                        b.s.addBlockEntity(new BESpec(x: midX, y: fy, z: midZ, kind: "pot_plant", data: new Dictionary<string, BEValue> { ["plant"] = BEValue.str("poppy") }));
                                        b.set(midX, fy - 1, midZ, (int)cell(B.grass_block));
                                        break;
                                    case "lootRare":
                                        b.chest(midX, fy, midZ, 0, "woodland_mansion");
                                        b.set(midX, fy - 1, midZ, (int)cell(B.obsidian));
                                        break;
                                    default:
                                        break;
                                }
                                // illager population
                                var r = b.rng.nextFloat();
                                if (r < 0.3) { b.mob("vindicator", midX + 1, fy, midZ + 1, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) }); }
                                else if (r < 0.42) { b.mob("evoker", midX - 1, fy, midZ - 1, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) }); }
                                // torch
                                b.set(roomX + 1, fy + 3, roomZ + 1, (int)cell(B.torch, 0));
                            }));
                        }
                    }
                }
                return (StructurePlan?)new StructurePlan(id: "woodland_mansion", pieces: pieces,
                                     @ref: new StructRefBox(x0 - 8, y - 8, z0 - 8, x0 + width + 8, y + FLOORS * FLOOR_H + 8, z0 + depth + 8));
            }
        ));
    }

    // =============================================================================
    // NETHER + END
    // =============================================================================
    private static string netherStructurePick(Rng rng)
    {
        return rng.nextFloat() < 0.4 ? "fortress" : "bastion";
    }

    public static void registerNetherEndStructures()
    {
        registerStructure(new StructureDef(
            id: "fortress", spacing: 27, separation: 4, salt: 30084232, maxRadiusChunks: 6,
            check: (ctx, _, _, rng) =>
                ctx.dim == (int)Dim.nether && netherStructurePick(rng) == "fortress",
            plan: (_, ocx, ocz, rng) =>
            {
                var pieces = new List<StructPiece>();
                var NB = (int)cell(B.nether_bricks);
                var FENCE = (int)cell(B.nether_brick_fence);
                int cx = ocx * 16 + 8, cz = ocz * 16 + 8;
                var y = 48 + rng.nextInt(16);

                Action<int, int> crossing = (x, z) =>
                {
                    pieces.Add(piece(x - 4, y - 6, z - 4, x + 4, y + 7, z + 4, b =>
                    {
                        b.fill(x - 3, y, z - 3, x + 3, y, z + 3, NB);
                        b.fill(x - 3, y + 1, z - 3, x + 3, y + 5, z + 3, AIR);
                        // pillars to ground
                        foreach (var (px, pz) in new (int, int)[] { (-3, -3), (3, -3), (-3, 3), (3, 3) })
                        {
                            for (int d = 1; d < 18; d++)
                            {
                                var cur = b.get(x + px, y - d, z + pz);
                                if (cur > 0 && (ushort)(cur >> 4) != B.lava) break;
                                b.set(x + px, y - d, z + pz, NB);
                            }
                        }
                        // railings
                        for (int d = -3; d <= 3; d++)
                        {
                            if (Math.Abs(d) == 2) continue;
                            b.set(x + d, y + 1, z - 3, FENCE); b.set(x + d, y + 1, z + 3, FENCE);
                            b.set(x - 3, y + 1, z + d, FENCE); b.set(x + 3, y + 1, z + d, FENCE);
                        }
                    }));
                };
                Func<int, int, int, int, (int, int)> bridge = (x, z, dir, len) =>
                {
                    int dx = new[] { 0, 0, -1, 1 }[dir], dz = new[] { -1, 1, 0, 0 }[dir];
                    int ex = x + dx * len, ez = z + dz * len;
                    pieces.Add(piece(
                        Math.Min(x, ex) - 3, y - 10, Math.Min(z, ez) - 3,
                        Math.Max(x, ex) + 3, y + 7, Math.Max(z, ez) + 3,
                    b =>
                    {
                        for (int i = 0; i <= len; i++)
                        {
                            int px = x + dx * i, pz = z + dz * i;
                            for (int w = -2; w <= 2; w++)
                            {
                                int wx = px + (dz != 0 ? w : 0), wz = pz + (dx != 0 ? w : 0);
                                b.set(wx, y, wz, NB);
                                for (int h = 1; h <= 5; h++) { b.set(wx, y + h, wz, AIR); }
                                if (Math.Abs(w) == 2) { b.set(wx, y + 1, wz, FENCE); }
                            }
                            // support arches
                            if (i % 6 == 3)
                            {
                                foreach (var w in new[] { -2, 2 })
                                {
                                    int wx = px + (dz != 0 ? w : 0), wz = pz + (dx != 0 ? w : 0);
                                    for (int d = 1; d < 14; d++)
                                    {
                                        var cur = b.get(wx, y - d, wz);
                                        if (cur > 0 && (ushort)(cur >> 4) != B.lava) break;
                                        b.set(wx, y - d, wz, NB);
                                    }
                                }
                            }
                        }
                    }));
                    return (ex, ez);
                };
                Action<int, int> blazePlatform = (x, z) =>
                {
                    pieces.Add(piece(x - 3, y, z - 3, x + 3, y + 9, z + 3, b =>
                    {
                        b.fill(x - 3, y + 1, z - 3, x + 3, y + 1, z + 3, NB);
                        b.fill(x - 2, y + 2, z - 2, x + 2, y + 7, z + 2, AIR);
                        // stairs up
                        for (int i = 0; i < 3; i++) { b.set(x - 3 + i, y + 1 + i, z, (int)cell(B.nether_brick_stairs, 3)); }
                        b.fill(x - 1, y + 2, z - 1, x + 1, y + 2, z + 1, NB);
                        b.spawner(x, y + 3, z, "blaze");
                        for (int d = -2; d <= 2; d++)
                        {
                            b.set(x + d, y + 2, z - 2, FENCE); b.set(x + d, y + 2, z + 2, FENCE);
                            b.set(x - 2, y + 2, z + d, FENCE); b.set(x + 2, y + 2, z + d, FENCE);
                        }
                    }));
                };
                Action<int, int> wartRoom = (x, z) =>
                {
                    pieces.Add(piece(x - 4, y - 2, z - 4, x + 4, y + 6, z + 4, b =>
                    {
                        b.walls(x - 4, y, z - 4, x + 4, y + 5, z + 4, NB, AIR);
                        b.fill(x - 3, y + 1, z - 3, x + 3, y + 1, z + 3, (int)cell(B.soul_sand));
                        for (int dz = -3; dz <= 3; dz++) { for (int dx = -3; dx <= 3; dx++) {
                            if (b.rng.nextFloat() < 0.7) { b.set(x + dx, y + 2, z + dz, (int)cell(B.nether_wart, b.rng.nextInt(4))); }
                        } }
                        b.set(x, y + 2, z - 4, AIR); b.set(x, y + 3, z - 4, AIR); // doorway
                        b.chest(x + 3, y + 2, z + 3, 0, "nether_fortress");
                    }));
                };

                crossing(cx, cz);
                var arms = 0;
                for (int dir = 0; dir < 4; dir++)
                {
                    if (rng.nextFloat() < 0.3 && arms >= 2) continue;
                    arms += 1;
                    var len = 16 + rng.nextInt(20);
                    var (ex, ez) = bridge(cx, cz, dir, len);
                    crossing(ex, ez);
                    var what = rng.nextFloat();
                    if (what < 0.4) { blazePlatform(ex + (dir < 2 ? 8 : 0), ez + (dir >= 2 ? 8 : 0)); }
                    else if (what < 0.65) { wartRoom(ex + (dir < 2 ? 9 : 0), ez + (dir >= 2 ? 9 : 0)); }
                    else if (what < 0.85)
                    {
                        var len2 = 12 + rng.nextInt(12);
                        var dir2 = (dir + (rng.nextBoolean() ? 2 : 3)) % 4;
                        var (ex2, ez2) = bridge(ex, ez, dir2, len2);
                        crossing(ex2, ez2);
                        if (rng.nextBoolean()) { blazePlatform(ex2, ez2 + 8); }
                    }
                }
                return (StructurePlan?)new StructurePlan(id: "fortress", pieces: pieces,
                                     @ref: new StructRefBox(cx - 70, y - 20, cz - 70, cx + 70, y + 12, cz + 70));
            }
        ));

        registerStructure(new StructureDef(
            id: "bastion", spacing: 27, separation: 4, salt: 30084232, maxRadiusChunks: 3,
            check: (ctx, _, _, rng) =>
            {
                if (ctx.dim != (int)Dim.nether || netherStructurePick(rng) != "bastion") { return false; }
                return true;
            },
            plan: (_, ocx, ocz, rng) =>
            {
                int x0 = ocx * 16 - 8, z0 = ocz * 16 - 8;
                var y = 50 + rng.nextInt(12);
                var BS = new List<(int, double)> { ((int)cell(B.blackstone), 5), ((int)cell(B.polished_blackstone_bricks), 4), ((int)cell(B.cracked_polished_blackstone_bricks), 2), ((int)cell(B.gilded_blackstone), 0.4) };
                int W = 32, D = 32, H = 20;
                return (StructurePlan?)new StructurePlan(id: "bastion", pieces: new List<StructPiece> {
                    piece(x0 - 1, y - 16, z0 - 1, x0 + W + 1, y + H + 1, z0 + D + 1, b =>
                    {
                        // big hollow shell with internal bridges
                        for (int dz = 0; dz <= D; dz++)
                        {
                            for (int dx = 0; dx <= W; dx++)
                            {
                                var isWall = dx == 0 || dx == W || dz == 0 || dz == D;
                                b.foundation(x0 + dx, y - 1, z0 + dz, (int)cell(B.blackstone), 14);
                                for (int h = 0; h < H; h++)
                                {
                                    if (isWall)
                                    {
                                        // ruined: upper parts decay
                                        if (h < H - b.rng.nextInt(6)) { b.fillRandom(x0 + dx, y + h, z0 + dz, x0 + dx, y + h, z0 + dz, BS); }
                                    }
                                    else
                                    {
                                        b.set(x0 + dx, y + h, z0 + dz, AIR);
                                    }
                                }
                            }
                        }
                        // internal floors (3 levels of partial bridges)
                        for (int lvl = 0; lvl < 3; lvl++)
                        {
                            var fy = y + 1 + lvl * 6;
                            for (int dz = 2; dz <= D - 2; dz++)
                            {
                                for (int dx = 2; dx <= W - 2; dx++)
                                {
                                    var onBridge = (dz % 10 < 3) || (dx % 12 < 3);
                                    if (onBridge && b.rng.nextFloat() < 0.92)
                                    {
                                        b.fillRandom(x0 + dx, fy, z0 + dz, x0 + dx, fy, z0 + dz, BS);
                                    }
                                }
                            }
                        }
                        // gold blocks scattered
                        for (int _i = 0; _i < 8; _i++)
                        {
                            int gx = x0 + 3 + b.rng.nextInt(W - 6), gz = z0 + 3 + b.rng.nextInt(D - 6);
                            var gy = y + 1 + b.rng.nextInt(3) * 6 + 1;
                            b.set(gx, gy, gz, (int)cell(B.gold_block));
                        }
                        // treasure room at center bottom
                        int cx = x0 + W / 2, cz = z0 + D / 2;
                        b.walls(cx - 4, y, cz - 4, cx + 4, y + 6, cz + 4, (int)cell(B.polished_blackstone_bricks), AIR);
                        b.fill(cx - 1, y + 1, cz - 1, cx + 1, y + 1, cz + 1, (int)cell(B.gold_block));
                        b.chest(cx, y + 2, cz, 0, "bastion_treasure");
                        b.set(cx - 4, y + 1, cz, AIR); b.set(cx - 4, y + 2, cz, AIR);
                        b.fill(cx - 2, y + 1, cz - 3, cx - 2, y + 1, cz - 3, (int)cell(B.lava, 0));
                        // other chests (rng drawn before the chunk-relative get()
                        // so the stream stays identical across bordering chunks)
                        for (int _i = 0; _i < 3; _i++)
                        {
                            int lx = x0 + 4 + b.rng.nextInt(W - 8), lz = z0 + 4 + b.rng.nextInt(D - 8);
                            var ly = y + 1 + b.rng.nextInt(3) * 6 + 1;
                            var facing = b.rng.nextInt(4);
                            if (b.get(lx, ly - 1, lz) > 0) { b.chest(lx, ly, lz, facing, "bastion_other"); }
                        }
                        // mobs
                        b.mob("piglin", cx + 3, y + 1, cz + 3, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("piglin", cx - 3, y + 7, cz - 3, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("piglin", x0 + 6, y + 1, z0 + 6, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("piglin_brute", cx + 1, y + 3, cz - 2, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("piglin_brute", x0 + W - 6, y + 13, z0 + 6, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("hoglin", x0 + 8, y + 1, z0 + D - 8, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("hoglin", x0 + W - 8, y + 1, z0 + D - 8, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                    }),
                }, @ref: new StructRefBox(x0 - 8, y - 16, z0 - 8, x0 + W + 8, y + H + 4, z0 + D + 8));
            }
        ));

        registerStructure(new StructureDef(
            id: "end_city", spacing: 20, separation: 11, salt: 10387313, maxRadiusChunks: 3,
            check: (ctx, ocx, ocz, rng) =>
            {
                if (ctx.dim != (int)Dim.end) { return false; }
                int x = ocx * 16 + 8, z = ocz * 16 + 8;
                var distSq = x * x + z * z;
                if (distSq < 768 * 768) { return false; } // outer islands only
                return ctx.heightAt(x, z) > 30 && rng.nextFloat() < 0.65;
            },
            plan: (ctx, ocx, ocz, rng) =>
            {
                int cx = ocx * 16 + 8, cz = ocz * 16 + 8;
                var baseY = ctx.heightAt(cx, cz);
                int PUR = (int)cell(B.purpur_block), PIL = (int)cell(B.purpur_pillar), END_ROD = (int)cell(B.end_rod);
                var pieces = new List<StructPiece>();
                var floors = 3 + rng.nextInt(3);

                // tower
                pieces.Add(piece(cx - 7, baseY - 4, cz - 7, cx + 7, baseY + floors * 5 + 8, cz + 7, b =>
                {
                    for (int dz = -4; dz <= 4; dz++) { for (int dx = -4; dx <= 4; dx++) {
                        b.foundation(cx + dx, baseY - 1, cz + dz, (int)cell(B.end_stone_bricks), 6);
                    } }
                    for (int f = 0; f < floors; f++)
                    {
                        var fy = baseY + f * 5;
                        // walls 9×9
                        for (int h = 0; h < 5; h++)
                        {
                            for (int d = -4; d <= 4; d++)
                            {
                                var win = h >= 2 && h <= 3 && Math.Abs(d) == 2;
                                b.set(cx + d, fy + h, cz - 4, win ? (int)cell(B.purple_stained_glass) : PUR);
                                b.set(cx + d, fy + h, cz + 4, win ? (int)cell(B.purple_stained_glass) : PUR);
                                b.set(cx - 4, fy + h, cz + d, win ? (int)cell(B.purple_stained_glass) : PUR);
                                b.set(cx + 4, fy + h, cz + d, win ? (int)cell(B.purple_stained_glass) : PUR);
                            }
                        }
                        // interior + floor
                        for (int dz = -3; dz <= 3; dz++) { for (int dx = -3; dx <= 3; dx++) {
                            b.set(cx + dx, fy - 1, cz + dz, PUR);
                            for (int h = 0; h < 4; h++) { b.set(cx + dx, fy + h, cz + dz, AIR); }
                        } }
                        // corner pillars
                        foreach (var (px, pz) in new (int, int)[] { (-4, -4), (4, -4), (-4, 4), (4, 4) })
                        {
                            for (int h = 0; h < 5; h++) { b.set(cx + px, fy + h, cz + pz, PIL); }
                        }
                        // spiral purpur stairs inside
                        var steps = new (int, int)[] { (-2, -2), (0, -3), (2, -2), (3, 0), (2, 2), (0, 3), (-2, 2), (-3, 0) };
                        var (sx, sz) = steps[f % steps.Length];
                        b.set(cx + sx, fy + 1, cz + sz, (int)cell(B.purpur_stairs, f % 4));
                        b.set(cx + sx, fy + 2, cz + sz, AIR);
                        // shulker guarding each floor
                        b.mob("shulker", cx + (f % 2 == 0 ? 2 : -2), fy, cz + (f % 2 == 0 ? 2 : -2), new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        // end rods
                        b.set(cx - 3, fy + 3, cz - 3, END_ROD);
                        b.set(cx + 3, fy + 3, cz + 3, END_ROD);
                    }
                    // door at base
                    b.fill(cx, baseY, cz - 4, cx, baseY + 2, cz - 4, AIR);
                    // roof + loot
                    var ty = baseY + floors * 5;
                    for (int dz = -5; dz <= 5; dz++) { for (int dx = -5; dx <= 5; dx++) {
                        if (Math.Abs(dx) == 5 || Math.Abs(dz) == 5) { b.set(cx + dx, ty, cz + dz, (int)cell(B.purpur_slab, 0)); }
                        else { b.set(cx + dx, ty, cz + dz, PUR); }
                    } }
                    b.chest(cx - 2, ty + 1, cz, 3, "end_city_treasure");
                    b.chest(cx + 2, ty + 1, cz, 2, "end_city_treasure");
                    b.set(cx, ty + 1, cz, END_ROD);
                    b.mob("shulker", cx, ty + 1, cz + 2, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                }));

                // end ship (60%)
                if (rng.nextFloat() < 0.6)
                {
                    int sx = cx + 14, sy = baseY + floors * 5 - 4, sz = cz;
                    pieces.Add(piece(sx - 3, sy - 4, sz - 4, sx + 18, sy + 10, sz + 4, b =>
                    {
                        // hull
                        for (int i = 0; i < 16; i++)
                        {
                            var w = i < 3 ? 1 : i > 12 ? 1 : 2;
                            for (int dz = -w; dz <= w; dz++)
                            {
                                b.set(sx + i, sy, sz + dz, PUR);
                                b.set(sx + i, sy + 1, sz - w, PUR);
                                b.set(sx + i, sy + 1, sz + w, PUR);
                            }
                            for (int dz = -w + 1; dz <= w - 1; dz++) { b.set(sx + i, sy + 1, sz + dz, AIR); }
                        }
                        // deck + cabin
                        b.fill(sx + 3, sy + 2, sz - 2, sx + 12, sy + 2, sz + 2, PUR);
                        b.walls(sx + 9, sy + 3, sz - 2, sx + 13, sy + 6, sz + 2, PUR, AIR);
                        // mast
                        for (int h = 0; h < 8; h++) { b.set(sx + 6, sy + 3 + h, sz, PIL); }
                        // dragon head prow
                        b.set(sx - 1, sy + 1, sz, (int)cell(B.dragon_head));
                        // treasure: elytra chest + brewing stand
                        b.chest(sx + 10, sy + 3, sz, 4, "end_city_treasure");
                        b.s.addBlockEntity(new BESpec(x: sx + 11, y: sy + 3, z: sz, kind: "elytra_chest"));
                        b.set(sx + 11, sy + 3, sz, (int)cell(B.chest, 4));
                        b.set(sx + 12, sy + 3, sz - 1, (int)cell(B.brewing_stand));
                        b.mob("shulker", sx + 7, sy + 3, sz, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("shulker", sx + 11, sy + 4, sz + 1, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                    }));
                }
                return (StructurePlan?)new StructurePlan(id: "end_city", pieces: pieces,
                                     @ref: new StructRefBox(cx - 24, baseY - 8, cz - 24, cx + 36, baseY + floors * 5 + 12, cz + 24));
            }
        ));
    }

    /// register every structure family in a frozen order
    /// (overworld, underground, big, nether_end) — STRUCTURES array order matters
    /// for buildStructuresForChunk iteration.
    /// A global `let` is dispatch_once-initialized, so concurrent generateChunk
    /// calls on the gen queue can't double-register (a plain bool check raced).
    private static readonly bool structuresRegistered = InitStructuresRegistered();
    private static bool InitStructuresRegistered()
    {
        registerOverworldStructures();
        registerUndergroundStructures();
        registerBigStructures();
        registerNetherEndStructures();
        return true;
    }
    public static void registerAllStructures()
    {
        _ = structuresRegistered;
    }
}

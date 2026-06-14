// Underground structures —
// mineshafts, strongholds (with end portal room), ancient cities.
//
// Ported from Sources/PebbleCore/Gen/StructUnderground.swift.
//
// Defines static class: StructUndergroundGlobals
//   (module-level free func + private state: registerUndergroundStructures,
//    strongholdChunks, strongholdCache, AIR) — surface the public entrypoint via
//    `global using static PebbleCore.StructUndergroundGlobals`.
//
// Cross-layer symbols referenced by name (defined elsewhere in the assembly):
//   SEA (public let in Terrain), BESpec/BEValue/EntitySpec/ChunkSink (Features),
//   cell/B (Block registry), hash2 (RandomX/Rng), posMod (WorldGlobals),
//   registerStructure/piece/strongholdPositions (StructFrameworkGlobals),
//   StructureDef/StructurePlan/StructRefBox/Builder/Rng/GenCtx (StructFramework).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class StructUndergroundGlobals
{
    private const int AIR = 0;

    private static (uint seed, List<(int, int)> positions)? strongholdCache;

    private static List<(int, int)> strongholdChunks(uint seed)
    {
        if (strongholdCache == null || strongholdCache.Value.seed != seed)
        {
            strongholdCache = (seed, strongholdPositions(seed));
        }
        return strongholdCache.Value.positions;
    }

    public static void registerUndergroundStructures()
    {
        registerStructure(new StructureDef(
            // radius must cover the worst-case corridor walk (~103 blocks ≈ 7
            // chunks) or far pieces get sliced off at chunk borders
            id: "mineshaft", spacing: 16, separation: 4, salt: 30084232, maxRadiusChunks: 7,
            check: (_, _, _, rng) =>
            {
                return rng.nextFloat() < 0.25;
            },
            plan: (_, ocx, ocz, rng) =>
            {
                var pieces = new List<StructPiece>();
                var baseY = -20 + rng.nextInt(50);
                int cx = ocx * 16 + 8, cz = ocz * 16 + 8;
                int P = (int)cell(B.oak_planks), F = (int)cell(B.oak_fence);

                void corridor(int x, int y, int z, int dir, int len, int depth)
                {
                    int dx = new[] { 0, 0, -1, 1 }[dir], dz = new[] { -1, 1, 0, 0 }[dir];
                    int ex = x + dx * len, ez = z + dz * len;
                    pieces.Add(piece(
                        Math.Min(x, ex) - 2, y - 1, Math.Min(z, ez) - 2,
                        Math.Max(x, ex) + 2, y + 4, Math.Max(z, ez) + 2,
                        b =>
                        {
                            for (int i = 0; i <= len; i++)
                            {
                                int px = x + dx * i, pz = z + dz * i;
                                // carve 3×3 tunnel — preserve water below sea level so
                                // shafts crossing aquifers/ocean floors flood like vanilla
                                for (int w = -1; w <= 1; w++)
                                {
                                    for (int h = 0; h <= 2; h++)
                                    {
                                        int wx = px + (dz != 0 ? w : 0), wz = pz + (dx != 0 ? w : 0);
                                        var cur = b.get(wx, y + h, wz);
                                        if (y + h <= SEA && (cur >> 4) == (int)B.water) continue;
                                        b.set(wx, y + h, wz, AIR);
                                    }
                                }
                                // floor planks over gaps
                                for (int w = -1; w <= 1; w++)
                                {
                                    int wx = px + (dz != 0 ? w : 0), wz = pz + (dx != 0 ? w : 0);
                                    var below = b.get(wx, y - 1, wz);
                                    if (below == 0) { b.set(wx, y - 1, wz, P); }
                                }
                                // supports every 4
                                if (i % 4 == 2)
                                {
                                    int lx = px + (dz != 0 ? -1 : 0), lz = pz + (dx != 0 ? -1 : 0);
                                    int rx = px + (dz != 0 ? 1 : 0), rz = pz + (dx != 0 ? 1 : 0);
                                    b.set(lx, y, lz, F); b.set(lx, y + 1, lz, F);
                                    b.set(rx, y, rz, F); b.set(rx, y + 1, rz, F);
                                    b.set(lx, y + 2, lz, P); b.set(px, y + 2, pz, P); b.set(rx, y + 2, rz, P);
                                    if (b.rng.nextFloat() < 0.25) { b.set(px, y + 2, pz, P); }
                                    if (b.rng.nextFloat() < 0.15) { b.set(px, y + 1, pz, (int)cell(B.torch, 0)); }
                                }
                                // rails
                                if (b.rng.nextFloat() < 0.6)
                                {
                                    b.set(px, y, pz, (int)cell(B.rail, dir < 2 ? 0 : 1));
                                }
                                // cobwebs
                                if (b.rng.nextFloat() < 0.06)
                                {
                                    int wx = px + (dz != 0 ? b.rng.nextInt(3) - 1 : 0), wz = pz + (dx != 0 ? b.rng.nextInt(3) - 1 : 0);
                                    b.set(wx, y + 1 + b.rng.nextInt(2), wz, (int)cell(B.cobweb));
                                }
                            }
                        }));
                    if (depth < 3)
                    {
                        // branches from the end
                        var branches = rng.nextInt(3);
                        for (int _b = 0; _b < branches + 1; _b++)
                        {
                            var ndir = rng.nextInt(4);
                            if (ndir == (dir ^ 1)) continue;
                            var ny = y + (rng.nextFloat() < 0.2 ? rng.nextInt(7) - 3 : 0);
                            corridor(ex, ny, ez, ndir, 8 + rng.nextInt(16), depth + 1);
                        }
                        // special rooms at junctions
                        if (rng.nextFloat() < 0.15)
                        {
                            pieces.Add(piece(ex - 3, y - 1, ez - 3, ex + 3, y + 4, ez + 3, b =>
                            {
                                // cave spider nest
                                b.spawner(ex, y + 1, ez, "cave_spider");
                                for (int _i = 0; _i < 16; _i++)
                                {
                                    int wx = ex + b.rng.nextInt(7) - 3, wy = y + b.rng.nextInt(3), wz = ez + b.rng.nextInt(7) - 3;
                                    if (b.get(wx, wy, wz) == 0) { b.set(wx, wy, wz, (int)cell(B.cobweb)); }
                                }
                            }));
                        }
                        if (rng.nextFloat() < 0.2)
                        {
                            int lx = ex + rng.nextInt(5) - 2, lz = ez + rng.nextInt(5) - 2;
                            // facing decided at PLAN time — drawing the shared plan rng
                            // inside build closures made results depend on which chunks
                            // happened to build first (a real bug the goldens caught)
                            var facing = rng.nextInt(4);
                            pieces.Add(piece(lx, y, lz, lx, y + 1, lz, b =>
                            {
                                b.chest(lx, y, lz, facing, "mineshaft");
                            }));
                        }
                    }
                }
                // central room
                pieces.Add(piece(cx - 4, baseY - 1, cz - 4, cx + 4, baseY + 5, cz + 4, b =>
                {
                    b.fill(cx - 3, baseY, cz - 3, cx + 3, baseY + 3, cz + 3, AIR);
                    for (int dz = -3; dz <= 3; dz++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            if (b.get(cx + dx, baseY - 1, cz + dz) == 0) { b.set(cx + dx, baseY - 1, cz + dz, P); }
                        }
                    }
                }));
                for (int d = 0; d < 4; d++)
                {
                    if (rng.nextFloat() < 0.8) { corridor(cx, baseY, cz, d, 10 + rng.nextInt(14), 0); }
                }
                return (StructurePlan?)new StructurePlan(id: "mineshaft", pieces: pieces);
            }
        ));

        registerStructure(new StructureDef(
            // radius must cover the worst-case corridor walk: 9 rooms × 17-block
            // corridors ≈ 160 blocks ≈ 10 chunks — too small and the outer rooms
            // (the PORTAL ROOM is always last/farthest) get sliced off
            id: "stronghold", spacing: 1, separation: 0, salt: 0, maxRadiusChunks: 11,
            check: (ctx, ocx, ocz, _) =>
            {
                foreach (var (sx, sz) in strongholdChunks(ctx.seed))
                {
                    if (sx == ocx && sz == ocz) return true;
                }
                return false;
            },
            plan: (_, ocx, ocz, rng) =>
            {
                var pieces = new List<StructPiece>();
                var SB = new List<(int, double)> { ((int)cell(B.stone_bricks), 7), ((int)cell(B.mossy_stone_bricks), 2), ((int)cell(B.cracked_stone_bricks), 2) };
                var baseY = 10 + rng.nextInt(15);
                int cx = ocx * 16 + 8, cz = ocz * 16 + 8;

                void room(int x, int y, int z, int w, int h, int d, Action<Builder, int, int, int> fn = null)
                {
                    pieces.Add(piece(x - 1, y - 1, z - 1, x + w + 1, y + h + 1, z + d + 1, b =>
                    {
                        for (int dy = -1; dy <= h; dy++)
                        {
                            for (int dz = -1; dz <= d; dz++)
                            {
                                for (int dx = -1; dx <= w; dx++)
                                {
                                    var isWall = dx == -1 || dx == w || dz == -1 || dz == d || dy == -1 || dy == h;
                                    if (isWall)
                                    {
                                        b.fillRandom(x + dx, y + dy, z + dz, x + dx, y + dy, z + dz, SB);
                                    }
                                    else
                                    {
                                        b.set(x + dx, y + dy, z + dz, AIR);
                                    }
                                }
                            }
                        }
                        fn?.Invoke(b, x, y, z);
                    }));
                }
                (int, int, int) corridorPiece(int x, int y, int z, int dir, int len)
                {
                    int dx = new[] { 0, 0, -1, 1 }[dir], dz = new[] { -1, 1, 0, 0 }[dir];
                    int ex = x + dx * len, ez = z + dz * len;
                    pieces.Add(piece(
                        Math.Min(x, ex) - 2, y - 1, Math.Min(z, ez) - 2,
                        Math.Max(x, ex) + 2, y + 4, Math.Max(z, ez) + 2,
                        b =>
                        {
                            for (int i = 0; i <= len; i++)
                            {
                                int px = x + dx * i, pz = z + dz * i;
                                for (int w = -2; w <= 2; w++)
                                {
                                    for (int h = -1; h <= 3; h++)
                                    {
                                        int wx = px + (dz != 0 ? w : 0), wz = pz + (dx != 0 ? w : 0);
                                        var isWall = Math.Abs(w) == 2 || h == -1 || h == 3;
                                        if (isWall) { b.fillRandom(wx, y + h, wz, wx, y + h, wz, SB); }
                                        else { b.set(wx, y + h, wz, AIR); }
                                    }
                                }
                                if (i % 6 == 3 && b.rng.nextFloat() < 0.4)
                                {
                                    b.set(px + (dz != 0 ? 1 : 0), y + 2, pz + (dx != 0 ? 1 : 0), (int)cell(B.torch, 0));
                                }
                            }
                        }));
                    return (ex, y, ez);
                }

                // start: spiral stair shaft down to baseY
                pieces.Add(piece(cx - 3, baseY - 1, cz - 3, cx + 3, baseY + 30, cz + 3, b =>
                {
                    for (int y = baseY; y < baseY + 28; y++)
                    {
                        for (int dz = -2; dz <= 2; dz++)
                        {
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                var isWall = Math.Abs(dx) == 2 || Math.Abs(dz) == 2;
                                b.set(cx + dx, y, cz + dz, isWall ? (int)cell(B.stone_bricks) : AIR);
                            }
                        }
                        var step = posMod(y, 8);
                        int sx = new[] { 1, 1, 0, -1, -1, -1, 0, 1 }[step], sz = new[] { 0, 1, 1, 1, 0, -1, -1, -1 }[step];
                        b.set(cx + sx, y, cz + sz, (int)cell(B.stone_brick_slab, 0));
                    }
                }));

                // rooms connected by corridors
                int px = cx, py = baseY, pz = cz;
                var roomCount = 6 + rng.nextInt(4);
                for (int i = 0; i < roomCount; i++)
                {
                    var dir = rng.nextInt(4);
                    var len = 8 + rng.nextInt(10);
                    (px, py, pz) = corridorPiece(px, py, pz, dir, len);
                    var kind = i == roomCount - 1 ? "portal" : rng.pick(new[] { "plain", "library", "fountain", "storage", "plain" });
                    if (kind == "plain")
                    {
                        room(px - 3, py - 1, pz - 3, 7, 5, 7);
                    }
                    else if (kind == "library")
                    {
                        room(px - 5, py - 1, pz - 5, 11, 7, 11, (b, x, y, z) =>
                        {
                            foreach (var bx in new[] { 1, 4, 7 })
                            {
                                for (int dz2 = 1; dz2 < 10; dz2++)
                                {
                                    if (dz2 % 3 == 0) continue;
                                    for (int h = 0; h < 3; h++) { b.set(x + bx, y + h, z + dz2, (int)cell(B.bookshelf)); }
                                }
                            }
                            b.chest(x + 9, y, z + 1, 2, "stronghold_library");
                            b.chest(x + 9, y + 4, z + 9, 0, "stronghold_library");
                            for (int _i = 0; _i < 8; _i++)
                            {
                                int wx = x + 1 + b.rng.nextInt(9), wy = y + b.rng.nextInt(5), wz = z + 1 + b.rng.nextInt(9);
                                // draw before the chunk-relative get() so the rng
                                // stream stays identical across bordering chunks
                                var place = b.rng.nextFloat() < 0.5;
                                if (place && b.get(wx, wy, wz) == 0) { b.set(wx, wy, wz, (int)cell(B.cobweb)); }
                            }
                        });
                    }
                    else if (kind == "fountain")
                    {
                        room(px - 3, py - 1, pz - 3, 7, 5, 7, (b, x, y, z) =>
                        {
                            b.set(x + 3, y, z + 3, (int)cell(B.water, 0));
                            b.fill(x + 2, y, z + 2, x + 4, y, z + 4, (int)cell(B.stone_brick_slab, 0));
                            b.set(x + 3, y, z + 3, (int)cell(B.water, 0));
                        });
                    }
                    else if (kind == "storage")
                    {
                        room(px - 3, py - 1, pz - 3, 7, 5, 7, (b, x, y, z) =>
                        {
                            b.chest(x + 1, y, z + 1, 1, "stronghold_corridor");
                            b.set(x + 5, y, z + 5, (int)cell(B.cobblestone));
                            b.set(x + 5, y + 1, z + 5, (int)cell(B.torch, 0));
                        });
                    }
                    else if (kind == "portal")
                    {
                        // PORTAL ROOM
                        room(px - 5, py - 1, pz - 5, 11, 8, 13, (b, x, y, z) =>
                        {
                            // lava pools
                            b.fill(x + 1, y, z + 1, x + 9, y, z + 2, (int)cell(B.lava, 0));
                            // platform with portal frame
                            int fx = x + 3, fz = z + 6;
                            b.fill(fx, y, fz, fx + 4, y, fz + 4, (int)cell(B.stone_bricks));
                            b.fill(fx + 1, y, fz + 1, fx + 3, y, fz + 3, (int)cell(B.lava, 0));
                            // frame ring with seeded eyes
                            var frameRng = new RandomX(hash2(0xE7E, x, z, 0));
                            void setFrame(int wx, int wz, int facing)
                            {
                                var eye = frameRng.nextFloat() < 0.1 ? 4 : 0;
                                b.set(wx, y + 1, wz, (int)cell(B.end_portal_frame, facing | eye));
                            }
                            for (int i2 = 1; i2 <= 3; i2++)
                            {
                                setFrame(fx + i2, fz, 1);          // north row faces south
                                setFrame(fx + i2, fz + 4, 0);      // south row faces north
                                setFrame(fx, fz + i2, 3);          // west column faces east
                                setFrame(fx + 4, fz + i2, 2);      // east column faces west
                            }
                            // stairs up to portal
                            for (int i2 = 0; i2 < 3; i2++)
                            {
                                b.fill(x + 4, y + i2, z + 3 + i2, x + 6, y + i2, z + 3 + i2, (int)cell(B.stone_brick_stairs, 1));
                            }
                            b.spawner(x + 5, y + 1, z + 3, "silverfish");
                            // infested blocks scattered (get() is -1 outside the
                            // building chunk — never feed that to UInt16)
                            for (int _i = 0; _i < 10; _i++)
                            {
                                int wx = x + b.rng.nextInt(11), wy = y + b.rng.nextInt(3), wz = z + b.rng.nextInt(13);
                                var cur = b.get(wx, wy, wz);
                                if (cur > 0 && (ushort)(cur >> 4) == B.stone_bricks) { b.set(wx, wy, wz, (int)cell(B.infested_stone_bricks)); }
                            }
                        });
                    }
                }
                return (StructurePlan?)new StructurePlan(id: "stronghold", pieces: pieces,
                                         @ref: new StructRefBox(cx - 170, baseY - 10, cz - 170, cx + 170, baseY + 40, cz + 170));
            }
        ));

        registerStructure(new StructureDef(
            id: "ancient_city", spacing: 24, separation: 8, salt: 20083232, maxRadiusChunks: 6,
            check: (_, _, _, rng) =>
            {
                return rng.nextFloat() < 0.28;
            },
            plan: (_, ocx, ocz, rng) =>
            {
                var pieces = new List<StructPiece>();
                int cx = ocx * 16 + 8, cz = ocz * 16 + 8;
                var y = -51;
                var DS = new List<(int, double)> { ((int)cell(B.deepslate_bricks), 5), ((int)cell(B.cracked_deepslate_bricks), 3), ((int)cell(B.deepslate_tiles), 3), ((int)cell(B.cobbled_deepslate), 2) };

                // grand central chamber + frame ("the portal")
                pieces.Add(piece(cx - 20, y - 3, cz - 12, cx + 20, y + 22, cz + 12, b =>
                {
                    b.fill(cx - 19, y, cz - 11, cx + 19, y + 18, cz + 11, AIR);
                    b.fillRandom(cx - 19, y - 1, cz - 11, cx + 19, y - 1, cz + 11, new List<(int, double)> { ((int)cell(B.sculk), 4), ((int)cell(B.deepslate), 4), ((int)cell(B.deepslate_tiles), 2) });
                    // the frame structure
                    int fx = cx, fz = cz;
                    b.fillRandom(fx - 7, y, fz - 1, fx + 7, y + 14, fz + 1, DS);
                    b.fill(fx - 4, y + 1, fz - 1, fx + 4, y + 10, fz + 1, AIR);
                    b.fill(fx - 4, y + 1, fz, fx + 4, y + 10, fz, (int)cell(B.reinforced_deepslate));
                    b.fill(fx - 3, y + 1, fz, fx + 3, y + 9, fz, AIR);
                    // stepped arch corners like the vanilla frame
                    foreach (var (sx, sy) in new[] { (-3, 9), (3, 9), (-3, 8), (3, 8), (-2, 9), (2, 9) })
                    {
                        b.set(fx + sx, y + sy, fz, (int)cell(B.reinforced_deepslate));
                    }
                    // soul fire braziers
                    foreach (var sx in new[] { -6, 6 })
                    {
                        b.set(fx + sx, y + 1, fz - 2, (int)cell(B.soul_sand));
                        b.set(fx + sx, y + 2, fz - 2, (int)cell(B.soul_fire));
                    }
                    // sculk spread on floor (rng drawn before the chunk-relative
                    // get() so the stream stays identical across bordering chunks)
                    for (int _i = 0; _i < 200; _i++)
                    {
                        int wx = cx - 19 + b.rng.nextInt(39), wz = cz - 11 + b.rng.nextInt(23);
                        var spread = b.rng.nextFloat() < 0.5;
                        var cur = b.get(wx, y - 1, wz);
                        if (cur > 0 && spread) { b.set(wx, y - 1, wz, (int)cell(B.sculk)); }
                    }
                    // shriekers + sensors near center
                    b.set(cx - 9, y, cz + 4, (int)cell(B.sculk_shrieker));
                    b.s.addBlockEntity(new BESpec(x: cx - 9, y: y, z: cz + 4, kind: "shrieker", data: new Dictionary<string, BEValue> { ["canSummon"] = BEValue.@bool(true) }));
                    b.set(cx + 9, y, cz - 4, (int)cell(B.sculk_shrieker));
                    b.s.addBlockEntity(new BESpec(x: cx + 9, y: y, z: cz - 4, kind: "shrieker", data: new Dictionary<string, BEValue> { ["canSummon"] = BEValue.@bool(true) }));
                    b.set(cx - 6, y, cz - 6, (int)cell(B.sculk_sensor));
                    b.set(cx + 6, y, cz + 6, (int)cell(B.sculk_sensor));
                    b.set(cx, y, cz + 8, (int)cell(B.sculk_catalyst));
                }));

                // boulevard east-west with ruins
                foreach (var dir in new[] { -1, 1 })
                {
                    pieces.Add(piece(cx + (dir == 1 ? 20 : -76), y - 2, cz - 5, cx + (dir == 1 ? 76 : -20), y + 12, cz + 5, b =>
                    {
                        for (int i = 20; i < 76; i++)
                        {
                            var px = cx + dir * i;
                            for (int w = -4; w <= 4; w++)
                            {
                                b.set(px, y - 1, cz + w, Math.Abs(w) <= 2 ? (int)cell(B.deepslate_tiles) : (int)cell(B.cobbled_deepslate));
                                for (int h = 0; h <= 8; h++) { b.set(px, y + h, cz + w, AIR); }
                            }
                            if (i % 9 == 0)
                            {
                                b.set(px, y, cz - 4, (int)cell(B.soul_lantern, 0));
                                b.set(px, y, cz + 4, (int)cell(B.soul_lantern, 0));
                            }
                        }
                    }));
                    // side buildings
                    var count = 3 + rng.nextInt(3);
                    for (int _c = 0; _c < count; _c++)
                    {
                        var bx = cx + dir * (26 + rng.nextInt(44));
                        var bz = cz + (rng.nextBoolean() ? 7 + rng.nextInt(8) : -(7 + rng.nextInt(8)));
                        int w = 5 + rng.nextInt(5), d = 5 + rng.nextInt(5), h = 4 + rng.nextInt(3);
                        pieces.Add(piece(bx - 1, y - 2, bz - 1, bx + w + 1, y + h + 6, bz + d + 1, b =>
                        {
                            b.fill(bx, y + h + 1, bz, bx + w, y + h + 5, bz + d, AIR);
                            for (int dz2 = 0; dz2 <= d; dz2++)
                            {
                                for (int dx2 = 0; dx2 <= w; dx2++)
                                {
                                    b.set(bx + dx2, y - 1, bz + dz2, (int)cell(B.deepslate_bricks));
                                    var isWall = dx2 == 0 || dx2 == w || dz2 == 0 || dz2 == d;
                                    for (int dy2 = 0; dy2 <= h; dy2++)
                                    {
                                        if (isWall)
                                        {
                                            if (b.rng.nextFloat() < 0.75) { b.fillRandom(bx + dx2, y + dy2, bz + dz2, bx + dx2, y + dy2, bz + dz2, DS); }
                                        }
                                        else
                                        {
                                            b.set(bx + dx2, y + dy2, bz + dz2, AIR);
                                        }
                                    }
                                }
                            }
                            if (b.rng.nextFloat() < 0.7) { b.chest(bx + 1 + b.rng.nextInt(Math.Max(1, w - 1)), y, bz + 1 + b.rng.nextInt(Math.Max(1, d - 1)), 0, "ancient_city"); }
                            if (b.rng.nextFloat() < 0.4)
                            {
                                b.set(bx + 2, y, bz + 2, (int)cell(B.sculk_sensor));
                            }
                            if (b.rng.nextFloat() < 0.25)
                            {
                                b.set(bx + w - 1, y, bz + d - 1, (int)cell(B.sculk_shrieker));
                                b.s.addBlockEntity(new BESpec(x: bx + w - 1, y: y, z: bz + d - 1, kind: "shrieker", data: new Dictionary<string, BEValue> { ["canSummon"] = BEValue.@bool(true) }));
                            }
                            // candles + skulls flavor
                            if (b.rng.nextFloat() < 0.5) { b.set(bx + 1, y, bz + d - 1, (int)cell(B.candle, 2 | 8)); }
                            if (b.rng.nextFloat() < 0.3) { b.set(bx + w - 1, y, bz + 1, (int)cell(B.skeleton_skull)); }
                        }));
                    }
                }
                // ice box room
                pieces.Add(piece(cx - 14, y, cz - 22, cx - 6, y + 6, cz - 14, b =>
                {
                    b.walls(cx - 14, y, cz - 22, cx - 6, y + 5, cz - 14, (int)cell(B.deepslate_bricks), AIR);
                    b.fill(cx - 12, y + 1, cz - 20, cx - 8, y + 2, cz - 16, (int)cell(B.packed_ice));
                    b.chest(cx - 10, y + 3, cz - 18, 0, "ancient_city");
                }));
                // wool corridors (sneaking path)
                pieces.Add(piece(cx - 5, y, cz + 12, cx + 5, y + 3, cz + 30, b =>
                {
                    for (int i = 12; i < 30; i++)
                    {
                        b.set(cx, y - 1, cz + i, (int)cell(B.gray_wool));
                        b.set(cx - 1, y - 1, cz + i, (int)cell(B.gray_carpet));
                        b.set(cx + 1, y - 1, cz + i, (int)cell(B.gray_carpet));
                        for (int h = 0; h <= 2; h++) { for (int w = -1; w <= 1; w++) { b.set(cx + w, y + h, cz + i, AIR); } }
                    }
                }));
                return (StructurePlan?)new StructurePlan(id: "ancient_city", pieces: pieces,
                                         @ref: new StructRefBox(cx - 80, y - 6, cz - 32, cx + 80, y + 24, cz + 32));
            }
        ));
    }
}

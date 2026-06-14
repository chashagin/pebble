// Overworld surface structures —
// villages (5 styles), desert & jungle temples, igloos, witch huts, pillager
// outposts, shipwrecks, ocean ruins, buried treasure, ruined portals, trail
// ruins and dungeons. RNG call order mirrors baseline exactly.
//
// Ported from Sources/PebbleCore/Gen/StructOverworld.swift.
//
// Defines:
//   - struct VillageStyle (namespace-level public value type, mutable fields)
//   - static class StructOverworldGlobals: module-level free funcs/state
//       (buildRuinedPortal, tryDungeons, registerOverworldStructures, plus private
//        helpers styleFor/houseSmall/houseJob/farm/well and `AIR`).
//     Surface via: `global using static PebbleCore.StructOverworldGlobals;`
//
// External (cross-layer) symbols referenced by name: ChunkSink, EntitySpec, BESpec,
// BEValue (Gen/Features), Builder/Rng/GenCtx/StructPiece/StructRefBox/StructurePlan/
// StructureDef (Gen/StructFramework), Biome/isOceanBiome (Gen/Biomes), and the
// unqualified free funcs cell/B/bid/hash2/rotF/piece/registerStructure.

using System;
using System.Collections.Generic;

namespace PebbleCore;

// =============================================================================
// VILLAGE
// =============================================================================
public struct VillageStyle
{
    public int planks;
    public int log;
    public int stairs;
    public int slab;
    public int wall;
    public int path;
    public int fence;
    public ushort door;
    public int window;
    public int farmFrame;
    public int roofStairs;

    public VillageStyle(int planks, int log, int stairs, int slab, int wall, int path,
                        int fence, ushort door, int window, int farmFrame, int roofStairs)
    {
        this.planks = planks; this.log = log; this.stairs = stairs; this.slab = slab;
        this.wall = wall; this.path = path; this.fence = fence; this.door = door;
        this.window = window; this.farmFrame = farmFrame; this.roofStairs = roofStairs;
    }
}

public static class StructOverworldGlobals
{
    private const int AIR = 0;

    private static VillageStyle? styleFor(int biomeId)
    {
        VillageStyle mk(string wood, int wallBlock, int path, int window)
        {
            return new VillageStyle(
                planks: (int)cell(bid($"{wood}_planks")), log: (int)cell(bid($"{wood}_log")),
                stairs: (int)cell(bid($"{wood}_stairs")), slab: (int)cell(bid($"{wood}_slab")),
                wall: wallBlock, path: path, fence: (int)cell(bid($"{wood}_fence")), door: bid($"{wood}_door"),
                window: window, farmFrame: (int)cell(bid($"{wood}_log")),
                roofStairs: (int)cell(bid($"{wood}_stairs"))
            );
        }
        VillageStyle mkDefault(string wood, int wallBlock, int path) => mk(wood, wallBlock, path, (int)cell(B.glass_pane));

        if (biomeId == (int)Biome.plains || biomeId == (int)Biome.sunflowerPlains || biomeId == (int)Biome.meadow)
        {
            return mkDefault("oak", (int)cell(B.cobblestone), (int)cell(B.dirt_path));
        }
        else if (biomeId == (int)Biome.desert)
        {
            var st = mkDefault("jungle", (int)cell(B.sandstone), (int)cell(B.smooth_sandstone));
            st.planks = (int)cell(B.smooth_sandstone);
            st.log = (int)cell(B.cut_sandstone);
            st.stairs = (int)cell(B.sandstone_stairs);
            st.slab = (int)cell(B.sandstone_slab);
            st.roofStairs = (int)cell(B.sandstone_stairs);
            st.fence = (int)cell(B.sandstone_wall);
            st.door = B.oak_door;
            return st;
        }
        else if (biomeId == (int)Biome.savanna || biomeId == (int)Biome.savannaPlateau)
        {
            return mkDefault("acacia", (int)cell(B.cobblestone), (int)cell(B.dirt_path));
        }
        else if (biomeId == (int)Biome.taiga || biomeId == (int)Biome.oldGrowthPineTaiga || biomeId == (int)Biome.oldGrowthSpruceTaiga)
        {
            return mkDefault("spruce", (int)cell(B.cobblestone), (int)cell(B.dirt_path));
        }
        else if (biomeId == (int)Biome.snowyPlains || biomeId == (int)Biome.snowyTaiga)
        {
            return mk("spruce", (int)cell(B.snow_block), (int)cell(B.snow_block), (int)cell(B.glass_pane));
        }
        else
        {
            return null;
        }
    }

    private static void houseSmall(Builder b, int x, int y, int z, VillageStyle st, int rot, ChunkSink sink)
    {
        // 5×5 footprint, walls 3 high, stair roof
        for (int dz = 0; dz < 5; dz++) { for (int dx = 0; dx < 5; dx++) { b.foundation(x + dx, y - 1, z + dz, st.wall); } }
        b.fill(x, y, z, x + 4, y + 2, z + 4, AIR);
        b.fill(x + 1, y + 3, z + 1, x + 3, y + 3, z + 3, AIR);
        // walls
        for (int h = 0; h < 3; h++)
        {
            for (int d = 0; d < 5; d++)
            {
                b.set(x + d, y + h, z, h == 1 && d == 2 ? st.window : st.planks);
                b.set(x + d, y + h, z + 4, h == 1 && d == 2 ? st.window : st.planks);
                b.set(x, y + h, z + d, h == 1 && d == 2 ? st.window : st.planks);
                b.set(x + 4, y + h, z + d, st.planks);
            }
        }
        foreach (var (cx2, cz2) in new[] { (0, 0), (4, 0), (0, 4), (4, 4) })
        {
            for (int h = 0; h < 3; h++) { b.set(x + cx2, y + h, z + cz2, st.log); }
        }
        // slab roof with planks core
        for (int dz = -1; dz <= 5; dz++)
        {
            for (int dx = -1; dx <= 5; dx++)
            {
                b.set(x + dx, y + 3, z + dz, (dx >= 1 && dx <= 3 && dz >= 1 && dz <= 3) ? st.planks : st.slab);
            }
        }
        b.set(x + 2, y + 4, z + 2, st.slab);
        // door (south face center)
        var doorFace = rotF(0, rot);
        b.set(x + 2, y, z, (int)cell(st.door, doorFace));
        b.set(x + 2, y + 1, z, (int)cell(st.door, 8));
        // interior: bed + crafting
        b.set(x + 3, y, z + 3, (int)cell(B.red_bed, 2 | 4));
        b.set(x + 3, y, z + 2, (int)cell(B.red_bed, 2));
        b.set(x + 1, y, z + 3, (int)cell(B.crafting_table));
        b.set(x + 1, y + 2, z + 1, (int)cell(B.torch));
        sink.addEntity(new EntitySpec(mob: "villager", x: (double)x + 2.5, y: (double)y, z: (double)z + 2.5));
    }

    private static void houseJob(Builder b, int x, int y, int z, VillageStyle st, int jobBlock, string lootTable, ChunkSink sink)
    {
        for (int dz = 0; dz < 6; dz++) { for (int dx = 0; dx < 6; dx++) { b.foundation(x + dx, y - 1, z + dz, st.wall); } }
        b.fill(x, y, z, x + 5, y + 3, z + 5, AIR);
        for (int h = 0; h < 3; h++)
        {
            for (int d = 0; d < 6; d++)
            {
                b.set(x + d, y + h, z, h == 1 && (d == 2 || d == 3) ? st.window : st.planks);
                b.set(x + d, y + h, z + 5, h == 1 && d == 2 ? st.window : st.planks);
                b.set(x, y + h, z + d, h == 1 && d == 3 ? st.window : st.planks);
                b.set(x + 5, y + h, z + d, st.planks);
            }
        }
        foreach (var (cx2, cz2) in new[] { (0, 0), (5, 0), (0, 5), (5, 5) })
        {
            for (int h = 0; h < 3; h++) { b.set(x + cx2, y + h, z + cz2, st.log); }
        }
        for (int dz = -1; dz <= 6; dz++)
        {
            for (int dx = -1; dx <= 6; dx++)
            {
                b.set(x + dx, y + 3, z + dz, (dx >= 1 && dx <= 4 && dz >= 1 && dz <= 4) ? st.planks : st.slab);
            }
        }
        b.set(x + 2, y, z, (int)cell(st.door, 0));
        b.set(x + 2, y + 1, z, (int)cell(st.door, 8));
        b.set(x + 4, y, z + 4, jobBlock);
        if (lootTable != null) { b.chest(x + 1, y, z + 4, 3, lootTable); }
        b.set(x + 1, y + 2, z + 1, (int)cell(B.torch));
        b.set(x + 4, y + 2, z + 1, (int)cell(B.torch));
        sink.addEntity(new EntitySpec(mob: "villager", x: (double)x + 3.5, y: (double)y, z: (double)z + 3.5));
    }

    private static void farm(Builder b, int x, int y, int z, VillageStyle st, Rng rng)
    {
        for (int dz = 0; dz < 7; dz++)
        {
            for (int dx = 0; dx < 9; dx++)
            {
                b.foundation(x + dx, y - 1, z + dz, (int)cell(B.dirt));
                var edge = dx == 0 || dx == 8 || dz == 0 || dz == 6;
                if (edge)
                {
                    b.set(x + dx, y - 1, z + dz, st.farmFrame);
                    b.set(x + dx, y, z + dz, AIR);
                }
                else if (dx == 4)
                {
                    b.set(x + dx, y - 1, z + dz, (int)cell(B.water, 0));
                }
                else
                {
                    b.set(x + dx, y - 1, z + dz, (int)cell(B.farmland, 7));
                    var crop = rng.nextFloat();
                    b.set(x + dx, y, z + dz, crop < 0.5 ? (int)cell(B.wheat, 4 + rng.nextInt(4)) : crop < 0.75 ? (int)cell(B.carrots, 4 + rng.nextInt(4)) : (int)cell(B.potatoes, 4 + rng.nextInt(4)));
                }
            }
        }
        b.set(x, y, z, (int)cell(B.composter));
    }

    private static void well(Builder b, int x, int y, int z, VillageStyle st)
    {
        // enclosed shaft: stone walls down to a floor, water contained one below the rim
        for (int dy = -11; dy <= -1; dy++)
        {
            for (int dz = -1; dz <= 4; dz++)
            {
                for (int dx = -1; dx <= 4; dx++)
                {
                    var edge = dx == -1 || dx == 4 || dz == -1 || dz == 4;
                    if (edge || dy == -11) { b.set(x + dx, y + dy, z + dz, st.wall); }
                }
            }
        }
        b.fill(x, y - 10, z, x + 3, y - 1, z + 3, (int)cell(B.water, 0));
        // rim
        for (int dz = -1; dz <= 4; dz++)
        {
            for (int dx = -1; dx <= 4; dx++)
            {
                var edge = dx == -1 || dx == 4 || dz == -1 || dz == 4;
                if (edge)
                {
                    b.set(x + dx, y, z + dz, st.wall);
                    b.foundation(x + dx, y - 1, z + dz, st.wall);
                }
            }
        }
        foreach (var (px, pz) in new[] { (-1, -1), (4, -1), (-1, 4), (4, 4) })
        {
            b.set(x + px, y + 1, z + pz, st.fence);
            b.set(x + px, y + 2, z + pz, st.fence);
        }
        b.fill(x - 1, y + 3, z - 1, x + 4, y + 3, z + 4, st.slab);
    }

    // =========================================================================
    // RUINED PORTAL (shared with nether)
    // =========================================================================
    public static void buildRuinedPortal(Builder b, int x, int y, int z, bool nether)
    {
        int OBS = (int)cell(B.obsidian), CRY = (int)cell(B.crying_obsidian);
        int STONE_FILL = (int)cell(B.netherrack);
        int w = 4, h = 5;
        // the nether variant decays less and runs hotter (more magma/lava) —
        // same rng draw count either way, only thresholds differ
        var decay = nether ? 0.15 : 0.25;
        var magma = nether ? 0.5 : 0.25;
        var lava = nether ? 0.8 : 0.5;
        // frame with decay
        for (int dx = 0; dx < w; dx++)
        {
            for (int dy = 0; dy < h; dy++)
            {
                var isFrame = dx == 0 || dx == w - 1 || dy == 0 || dy == h - 1;
                if (!isFrame) { continue; }
                if (b.rng.nextFloat() < decay) { continue; } // missing
                b.set(x + dx, y + dy, z, b.rng.nextFloat() < 0.18 ? CRY : OBS);
            }
        }
        // netherrack + magma splash
        for (int _i = 0; _i < 14; _i++)
        {
            int px = x + b.rng.nextInt(7) - 2, pz = z + b.rng.nextInt(5) - 2;
            var py = y - 1 + b.rng.nextInt(2) - 1;
            var hot = b.rng.nextFloat() < magma;
            var cur = b.get(px, py, pz);
            if (cur > 0) { b.set(px, py, pz, hot ? (int)cell(B.magma_block) : STONE_FILL); }
        }
        if (b.rng.nextFloat() < lava) { b.set(x + 1, y - 1, z + 1, (int)cell(B.lava, 0)); }
        b.chest(x - 2, y, z + 1, 0, "ruined_portal");
    }

    // =========================================================================
    // DUNGEON (placed per-chunk, not region)
    // =========================================================================
    public static void tryDungeons(uint seed, int ocx, int ocz, ChunkSink sink)
    {
        var rng = new Rng(hash2(seed, ocx, ocz, 0xD0D6E0));
        for (int _i = 0; _i < 4; _i++)
        {
            if (rng.nextFloat() > 0.12) { continue; }
            var x = ocx * 16 + 3 + rng.nextInt(10);
            var z = ocz * 16 + 3 + rng.nextInt(10);
            var y = -40 + rng.nextInt(90);
            var b = new Builder(sink, rng);
            // need air adjacent (cave opening)
            var openings = 0;
            foreach (var (dx, dz) in new[] { (-4, 0), (4, 0), (0, -4), (0, 4) })
            {
                if (b.get(x + dx, y + 1, z + dz) == 0) { openings += 1; }
            }
            if (openings == 0 || openings > 3) { continue; }
            var hw = 3 + rng.nextInt(2);
            var mossy = new List<(int, double)> { ((int)cell(B.cobblestone), 5), ((int)cell(B.mossy_cobblestone), 5) };
            // floor + walls + ceiling
            b.fillRandom(x - hw, y - 1, z - hw, x + hw, y - 1, z + hw, mossy);
            for (int dy = 0; dy <= 3; dy++)
            {
                for (int dz = -hw; dz <= hw; dz++)
                {
                    for (int dx = -hw; dx <= hw; dx++)
                    {
                        var isWall = Math.Abs(dx) == hw || Math.Abs(dz) == hw;
                        if (dy == 3) { b.set(x + dx, y + dy, z + dz, (int)cell(B.cobblestone)); continue; }
                        if (isWall)
                        {
                            if (b.rng.nextFloat() < 0.85)
                            {
                                b.set(x + dx, y + dy, z + dz, b.rng.nextFloat() < 0.5 ? (int)cell(B.cobblestone) : (int)cell(B.mossy_cobblestone));
                            }
                        }
                        else
                        {
                            b.set(x + dx, y + dy, z + dz, AIR);
                        }
                    }
                }
            }
            var mobRoll = rng.nextFloat();
            b.spawner(x, y, z, mobRoll < 0.5 ? "zombie" : mobRoll < 0.75 ? "skeleton" : "spider");
            b.chest(x + hw - 1, y, z + hw - 1, 0, "dungeon");
            if (rng.nextBoolean()) { b.chest(x - hw + 1, y, z - hw + 1, 0, "dungeon"); }
            return; // max one per chunk
        }
    }

    // =========================================================================
    // registration
    // =========================================================================
    public static void registerOverworldStructures()
    {
        registerStructure(new StructureDef(
            id: "village", spacing: 34, separation: 8, salt: 10387312, maxRadiusChunks: 5,
            check: (ctx, ocx, ocz, _r) =>
                styleFor(ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8)) != null,
            plan: (ctx, ocx, ocz, rng) =>
            {
                int centerX = ocx * 16 + 8, centerZ = ocz * 16 + 8;
                var biomeId = ctx.biomeAt(centerX, centerZ);
                var st = styleFor(biomeId).Value;
                var pieces = new List<StructPiece>();
                var cy = ctx.heightAt(centerX, centerZ);

                // well at center
                pieces.Add(piece(centerX - 2, cy - 12, centerZ - 2, centerX + 5, cy + 4, centerZ + 5, b =>
                {
                    well(b, centerX, cy, centerZ, st);
                }));
                // bell next to well
                pieces.Add(piece(centerX - 4, cy, centerZ, centerX - 4, cy + 2, centerZ, b =>
                {
                    b.foundation(centerX - 4, cy - 1, centerZ, st.wall);
                    b.set(centerX - 4, cy, centerZ, st.wall);
                    b.set(centerX - 4, cy + 1, centerZ, (int)cell(B.bell, 0));
                }));
                // iron golem + extras at center
                pieces.Add(piece(centerX, cy, centerZ - 3, centerX, cy + 2, centerZ - 3, b =>
                {
                    b.mob("iron_golem", centerX, cy, centerZ - 3);
                    b.mob("cat", centerX + 2, cy, centerZ - 3);
                    if (biomeId == (int)Biome.desert) { b.mob("camel", centerX - 3, cy, centerZ - 4); }
                }));

                // roads in 4 directions with buildings
                var jobs = new List<(int, string)>
                {
                    ((int)cell(B.smithing_table), "village_weaponsmith"),
                    ((int)cell(B.lectern, 0), null),
                    ((int)cell(B.blast_furnace, 0), "village_toolsmith"),
                    ((int)cell(B.brewing_stand), "village_temple"),
                    ((int)cell(B.loom), null),
                    ((int)cell(B.cauldron), null),
                    ((int)cell(B.stonecutter, 0), null),
                    ((int)cell(B.barrel, 1), null),
                    ((int)cell(B.grindstone, 0), null),
                    ((int)cell(B.fletching_table), null),
                    ((int)cell(B.cartography_table), null),
                };
                var jobIdx = rng.nextInt(jobs.Count);
                var arms = 3 + rng.nextInt(2);
                var dirOrder = rng.shuffle(new[] { 0, 1, 2, 3 });
                for (int a = 0; a < arms; a++)
                {
                    var dir = dirOrder[a];
                    int dx = new[] { 0, 0, -1, 1 }[dir], dz = new[] { -1, 1, 0, 0 }[dir];
                    var len = 14 + rng.nextInt(16);
                    // road piece
                    var rx0 = Math.Min(centerX + dx * 4, centerX + dx * (4 + len)) - 1;
                    var rx1 = Math.Max(centerX + dx * 4, centerX + dx * (4 + len)) + 1;
                    var rz0 = Math.Min(centerZ + dz * 4, centerZ + dz * (4 + len)) - 1;
                    var rz1 = Math.Max(centerZ + dz * 4, centerZ + dz * (4 + len)) + 1;
                    pieces.Add(piece(rx0, cy - 6, rz0, rx1, cy + 30, rz1, b =>
                    {
                        for (int i = 4; i <= 4 + len; i++)
                        {
                            int px = centerX + dx * i, pz = centerZ + dz * i;
                            _ = ctx.heightAt(px, pz);
                            for (int w = -1; w <= 1; w++)
                            {
                                int wx = px + (dz != 0 ? w : 0), wz = pz + (dx != 0 ? w : 0);
                                var wy = ctx.heightAt(wx, wz);
                                b.foundation(wx, wy - 1, wz, st.path, 4);
                                b.set(wx, wy, wz, AIR);
                                b.set(wx, wy + 1, wz, AIR);
                            }
                            // lamp posts
                            if (i % 7 == 0)
                            {
                                int lx = px + (dz != 0 ? 2 : 0), lz = pz + (dx != 0 ? 2 : 0);
                                var ly = ctx.heightAt(lx, lz);
                                b.set(lx, ly, lz, st.fence);
                                b.set(lx, ly + 1, lz, st.fence);
                                b.set(lx, ly + 2, lz, (int)cell(B.torch));
                            }
                        }
                    }));
                    // buildings along the arm
                    var bcount = 2 + rng.nextInt(3);
                    for (int _b = 0; _b < bcount; _b++)
                    {
                        var along = 7 + rng.nextInt(Math.Max(1, len - 6));
                        var side = rng.nextBoolean() ? 1 : -1;
                        var off = 3 + rng.nextInt(2);
                        var bx = centerX + dx * along + (dz != 0 ? side * off : 0);
                        var bz = centerZ + dz * along + (dx != 0 ? side * off : 0);
                        var by = ctx.heightAt(bx + 3, bz + 3);
                        var kind = rng.nextFloat();
                        if (kind < 0.4)
                        {
                            pieces.Add(piece(bx - 1, by - 8, bz - 1, bx + 6, by + 6, bz + 6, b =>
                            {
                                houseSmall(b, bx, by, bz, st, 0, b.s);
                            }));
                        }
                        else if (kind < 0.62)
                        {
                            pieces.Add(piece(bx - 1, by - 8, bz - 1, bx + 10, by + 3, bz + 8, b =>
                            {
                                farm(b, bx, by, bz, st, b.rng);
                            }));
                        }
                        else
                        {
                            var (jobBlock, loot) = jobs[jobIdx % jobs.Count];
                            jobIdx += 1;
                            pieces.Add(piece(bx - 1, by - 8, bz - 1, bx + 7, by + 6, bz + 7, b =>
                            {
                                houseJob(b, bx, by, bz, st, jobBlock, loot, b.s);
                            }));
                        }
                    }
                }
                return new StructurePlan(id: "village", pieces: pieces,
                                         @ref: new StructRefBox(centerX - 80, cy - 20, centerZ - 80, centerX + 80, cy + 40, centerZ + 80));
            }
        ));

        registerStructure(new StructureDef(
            id: "desert_temple", spacing: 32, separation: 9, salt: 14357617, maxRadiusChunks: 2,
            check: (ctx, ocx, ocz, _r) =>
                ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8) == (int)Biome.desert,
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16, z = ocz * 16;
                var y = ctx.heightAt(x + 10, z + 10);
                int SS = (int)cell(B.sandstone), CUT = (int)cell(B.cut_sandstone), CHIS = (int)cell(B.chiseled_sandstone);
                int OR = (int)cell(B.orange_terracotta), BL = (int)cell(B.blue_terracotta);
                return new StructurePlan(id: "desert_temple", pieces: new List<StructPiece>
                {
                    piece(x - 1, y - 16, z - 1, x + 21, y + 22, z + 21, b =>
                    {
                        for (int dz = 0; dz < 21; dz++) { for (int dx = 0; dx < 21; dx++) { b.foundation(x + dx, y - 1, z + dz, SS, 10); } }
                        for (int layer = 0; layer < 10; layer++)
                        {
                            var i = layer;
                            b.fill(x + i, y + layer, z + i, x + 20 - i, y + layer, z + 20 - i, SS);
                        }
                        b.fill(x + 1, y, z + 1, x + 19, y + 3, z + 19, AIR);
                        for (int dz = 1; dz < 20; dz++) { for (int dx = 1; dx < 20; dx++) { b.set(x + dx, y - 1, z + dz, SS); } }
                        int cx2 = x + 10, cz2 = z + 10;
                        b.fill(cx2 - 1, y - 1, cz2 - 1, cx2 + 1, y - 1, cz2 + 1, OR);
                        b.set(cx2, y - 1, cz2, BL);
                        // treasure pit — clears/floors FIRST, trap LAST (the plate
                        // used to be wiped by the later fills: dead trap, sealed TNT)
                        b.fill(cx2 - 2, y - 14, cz2 - 2, cx2 + 2, y - 2, cz2 + 2, AIR);
                        b.fill(cx2 - 3, y - 15, cz2 - 3, cx2 + 3, y - 15, cz2 + 3, SS);
                        b.fill(cx2 - 3, y - 14, cz2 - 3, cx2 + 3, y - 10, cz2 + 3, AIR);
                        b.fill(cx2 - 3, y - 14, cz2 - 3, cx2 + 3, y - 14, cz2 + 3, SS);
                        b.set(cx2 - 3, y - 14, cz2 - 3, AIR); b.set(cx2 + 3, y - 14, cz2 + 3, AIR);
                        b.fill(cx2 - 1, y - 16, cz2 - 1, cx2 + 1, y - 16, cz2 + 1, (int)cell(B.tnt));
                        b.set(cx2, y - 13, cz2, AIR);
                        b.set(cx2, y - 14, cz2, (int)cell(B.stone_pressure_plate));
                        b.chest(cx2 - 2, y - 13, cz2, 5, "desert_temple");
                        b.chest(cx2 + 2, y - 13, cz2, 4, "desert_temple");
                        b.chest(cx2, y - 13, cz2 - 2, 1, "desert_temple");
                        b.chest(cx2, y - 13, cz2 + 2, 0, "desert_temple");
                        // towers
                        foreach (var (tx, tz) in new[] { (x + 2, z + 2), (x + 16, z + 2) })
                        {
                            b.fill(tx, y, tz, tx + 2, y + 9, tz + 2, SS);
                            b.fill(tx, y + 10, tz, tx + 2, y + 10, tz + 2, CUT);
                            b.set(tx + 1, y + 6, tz + 1, CHIS);
                        }
                        // entrance
                        b.fill(x + 9, y, z, x + 11, y + 2, z + 1, AIR);
                        b.set(x + 9, y + 2, z, CUT); b.set(x + 11, y + 2, z, CUT);
                        // orange decoration band
                        var d = 0;
                        while (d < 21)
                        {
                            b.set(x + d, y + 4, z, OR);
                            b.set(x + d, y + 4, z + 20, OR);
                            d += 2;
                        }
                        // archaeology
                        b.suspicious(cx2 - 2, y - 14, cz2 - 2, false, "desert_pyramid_archaeology");
                        b.suspicious(cx2 + 2, y - 14, cz2 + 2, false, "desert_pyramid_archaeology");
                        b.suspicious(cx2 + 2, y - 14, cz2 - 2, false, "desert_pyramid_archaeology");
                        b.suspicious(cx2 - 2, y - 14, cz2 + 2, false, "desert_pyramid_archaeology");
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "jungle_temple", spacing: 32, separation: 9, salt: 14357619, maxRadiusChunks: 2,
            check: (ctx, ocx, ocz, _r) =>
            {
                var bm = ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8);
                return bm == (int)Biome.jungle || bm == (int)Biome.bambooJungle;
            },
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16 + 2, z = ocz * 16 + 2;
                var y = ctx.heightAt(x + 6, z + 7);
                int C = (int)cell(B.cobblestone), M = (int)cell(B.mossy_cobblestone);
                var mossy = new List<(int, double)> { (C, 6), (M, 4) };
                return new StructurePlan(id: "jungle_temple", pieces: new List<StructPiece>
                {
                    piece(x - 1, y - 6, z - 1, x + 12, y + 14, z + 15, b =>
                    {
                        for (int dz = 0; dz < 15; dz++) { for (int dx = 0; dx < 12; dx++) { b.foundation(x + dx, y - 1, z + dz, C, 6); } }
                        b.fillRandom(x, y, z, x + 11, y, z + 14, mossy);
                        b.fillRandom(x, y + 1, z, x + 11, y + 4, z + 14, mossy);
                        b.fill(x + 1, y + 1, z + 1, x + 10, y + 3, z + 13, AIR);
                        b.fillRandom(x + 1, y + 4, z + 1, x + 10, y + 4, z + 13, mossy);
                        b.fillRandom(x + 2, y + 5, z + 2, x + 9, y + 8, z + 12, mossy);
                        b.fill(x + 3, y + 5, z + 3, x + 8, y + 7, z + 11, AIR);
                        b.fillRandom(x + 3, y + 9, z + 3, x + 8, y + 10, z + 11, mossy);
                        // entrance (north)
                        b.fill(x + 5, y + 5, z, x + 6, y + 7, z + 3, AIR);
                        b.fill(x + 5, y + 1, z + 1, x + 6, y + 4, z + 1, AIR);
                        // stairs down inside
                        for (int i = 0; i < 4; i++)
                        {
                            b.set(x + 5, y + 4 - i, z + 4 + i, (int)cell(B.cobblestone_stairs, 1));
                            b.set(x + 6, y + 4 - i, z + 4 + i, (int)cell(B.cobblestone_stairs, 1));
                            b.fill(x + 5, y + 5 - i, z + 4 + i, x + 6, y + 7 - i, z + 4 + i, AIR);
                        }
                        // tripwire trap corridor
                        b.set(x + 1, y + 1, z + 8, (int)cell(B.tripwire_hook, 3));
                        b.set(x + 10, y + 1, z + 8, (int)cell(B.tripwire_hook, 2));
                        for (int dx = 2; dx <= 9; dx++) { b.set(x + dx, y + 1, z + 8, (int)cell(B.tripwire)); }
                        b.set(x + 1, y + 1, z + 9, (int)cell(B.dispenser, 3));
                        b.s.addBlockEntity(new BESpec(x: x + 1, y: y + 1, z: z + 9, kind: "dispenser_arrows"));
                        // puzzle levers + hidden chest room
                        b.set(x + 2, y + 2, z + 12, (int)cell(B.lever, 4));
                        b.set(x + 9, y + 2, z + 12, (int)cell(B.lever, 5));
                        b.chest(x + 2, y + 1, z + 13, 0, "jungle_temple");
                        b.chest(x + 9, y + 5, z + 2, 1, "jungle_temple");
                        // vines on walls
                        for (int _i = 0; _i < 30; _i++)
                        {
                            int vx = x + b.rng.nextInt(12), vy = y + 1 + b.rng.nextInt(9), vz = z + b.rng.nextInt(15);
                            if (b.get(vx, vy, vz) == 0)
                            {
                                for (int f = 0; f < 4; f++)
                                {
                                    int wx = vx + new[] { 0, 0, -1, 1 }[f], wz = vz + new[] { -1, 1, 0, 0 }[f];
                                    var w = b.get(wx, vy, wz);
                                    if (w > 0 && (w == C || w == M))
                                    {
                                        b.set(vx, vy, vz, (int)cell(B.vine, 1 << f));
                                        break;
                                    }
                                }
                            }
                        }
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "igloo", spacing: 32, separation: 8, salt: 14357618, maxRadiusChunks: 1,
            check: (ctx, ocx, ocz, _r) =>
            {
                var bm = ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8);
                return bm == (int)Biome.snowyPlains || bm == (int)Biome.snowyTaiga;
            },
            plan: (ctx, ocx, ocz, rng) =>
            {
                int x = ocx * 16 + 4, z = ocz * 16 + 4;
                var y = ctx.heightAt(x + 3, z + 3);
                var SNOW = (int)cell(B.snow_block);
                var hasBasement = rng.nextFloat() < 0.5;
                return new StructurePlan(id: "igloo", pieces: new List<StructPiece>
                {
                    piece(x - 1, y - 24, z - 1, x + 8, y + 5, z + 10, b =>
                    {
                        // dome 7×7
                        for (int dz = 0; dz < 7; dz++)
                        {
                            for (int dx = 0; dx < 7; dx++)
                            {
                                b.foundation(x + dx, y - 1, z + dz, SNOW, 4);
                                var d2 = (dx - 3) * (dx - 3) + (dz - 3) * (dz - 3);
                                if (d2 <= 10) { b.set(x + dx, y + 3, z + dz, d2 <= 4 ? SNOW : AIR); }
                                if (d2 <= 4) { b.set(x + dx, y + 4, z + dz, d2 <= 1 ? SNOW : AIR); }
                                if (d2 > 4 && d2 <= 10) { b.set(x + dx, y + 1, z + dz, SNOW); b.set(x + dx, y + 2, z + dz, SNOW); }
                                if (d2 <= 4) { b.set(x + dx, y + 1, z + dz, AIR); b.set(x + dx, y + 2, z + dz, AIR); }
                                if (d2 <= 10) { b.set(x + dx, y, z + dz, SNOW); }
                            }
                        }
                        b.fill(x + 2, y + 3, z + 2, x + 4, y + 3, z + 4, SNOW);
                        b.set(x + 3, y + 3, z + 3, (int)cell(B.snow_block));
                        // entrance tunnel south
                        b.fill(x + 3, y + 1, z + 6, x + 3, y + 2, z + 9, AIR);
                        b.fill(x + 2, y + 1, z + 6, x + 2, y + 3, z + 9, SNOW);
                        b.fill(x + 4, y + 1, z + 6, x + 4, y + 3, z + 9, SNOW);
                        b.fill(x + 2, y + 3, z + 6, x + 4, y + 3, z + 9, SNOW);
                        // furnishings
                        b.set(x + 1, y + 1, z + 3, (int)cell(B.red_bed, 2 | 4));
                        b.set(x + 1, y + 1, z + 2, (int)cell(B.red_bed, 2));
                        b.set(x + 5, y + 1, z + 2, (int)cell(B.furnace, 2));
                        b.set(x + 5, y + 1, z + 4, (int)cell(B.crafting_table));
                        b.set(x + 3, y + 2, z + 1, (int)cell(B.torch, 3));
                        if (hasBasement)
                        {
                            b.set(x + 3, y + 1, z + 4, (int)cell(B.white_carpet));
                            // trapdoor under the carpet — the shaft used to be sealed
                            // by solid snow with nothing hinting at the basement
                            b.set(x + 3, y, z + 4, (int)cell(B.oak_trapdoor, 0));
                            var by = y - 20;
                            b.walls(x - 1, by - 1, z - 1, x + 7, by + 3, z + 5, (int)cell(B.stone_bricks), AIR);
                            // ladder shaft — carved AFTER the basement box: walls()
                            // writes its top plane solid and AIRs the interior,
                            // which would plug the shaft and delete the rungs
                            for (int d = 1; d <= 20; d++)
                            {
                                b.set(x + 3, y - d, z + 4, (int)cell(B.ladder, 0));
                                b.set(x + 3, y - d, z + 5, (int)cell(B.stone));
                            }
                            b.set(x + 1, by, z + 1, (int)cell(B.brewing_stand));
                            b.set(x + 1, by, z + 2, (int)cell(B.cauldron, 3));
                            b.chest(x + 6, by, z + 1, 2, "igloo");
                            // prisoner cells
                            b.set(x + 5, by, z + 4, (int)cell(B.iron_bars));
                            b.set(x + 6, by, z + 4, (int)cell(B.iron_bars));
                            b.mob("villager", x + 5, by, z + 3);
                            b.mob("zombie_villager", x + 6, by, z + 3);
                            b.set(x + 2, by + 2, z + 2, (int)cell(B.torch));
                        }
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "witch_hut", spacing: 32, separation: 8, salt: 14357620, maxRadiusChunks: 1,
            check: (ctx, ocx, ocz, _r) =>
                ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8) == (int)Biome.swamp,
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16 + 5, z = ocz * 16 + 5;
                var y = Math.Max(64, ctx.heightAt(x + 3, z + 4) + 1);
                int P = (int)cell(B.spruce_planks), L = (int)cell(B.oak_log);
                return new StructurePlan(id: "witch_hut", pieces: new List<StructPiece>
                {
                    piece(x - 1, y - 8, z - 1, x + 8, y + 7, z + 10, b =>
                    {
                        // stilts
                        foreach (var (sx, sz) in new[] { (1, 1), (5, 1), (1, 7), (5, 7) })
                        {
                            for (int d = 0; d < 8; d++)
                            {
                                var yy = y - d;
                                var cur = b.get(x + sx, yy, z + sz);
                                if (cur > 0 && (ushort)(cur >> 4) != B.water) { break; }
                                b.set(x + sx, yy, z + sz, L);
                            }
                        }
                        // platform + room
                        b.fill(x, y + 1, z, x + 6, y + 1, z + 8, P);
                        b.walls(x, y + 2, z + 1, x + 6, y + 5, z + 8, P, AIR);
                        b.fill(x + 1, y + 5, z + 2, x + 5, y + 5, z + 7, P);
                        // door gap + windows
                        b.set(x + 3, y + 2, z + 1, AIR); b.set(x + 3, y + 3, z + 1, AIR);
                        b.set(x + 1, y + 3, z + 4, AIR); b.set(x + 5, y + 3, z + 4, AIR);
                        // furnishings
                        b.set(x + 5, y + 2, z + 7, (int)cell(B.cauldron, 2 | 0));
                        b.set(x + 1, y + 2, z + 7, (int)cell(B.crafting_table));
                        b.set(x + 1, y + 2, z + 2, (int)cell(B.flower_pot));
                        b.s.addBlockEntity(new BESpec(x: x + 1, y: y + 2, z: z + 2, kind: "pot_plant", data: new Dictionary<string, BEValue> { ["plant"] = BEValue.str("red_mushroom") }));
                        b.mob("witch", x + 3, y + 2, z + 4, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("cat", x + 2, y + 2, z + 5, new Dictionary<string, BEValue> { ["variant"] = BEValue.str("black"), ["persistent"] = BEValue.@bool(true) });
                    }),
                }, @ref: new StructRefBox(x - 8, y - 8, z - 8, x + 14, y + 12, z + 16));
            }
        ));

        registerStructure(new StructureDef(
            id: "pillager_outpost", spacing: 32, separation: 9, salt: 165745296, maxRadiusChunks: 2,
            check: (ctx, ocx, ocz, rng) =>
            {
                var bm = ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8);
                var ok = bm == (int)Biome.plains || bm == (int)Biome.desert || bm == (int)Biome.savanna ||
                    bm == (int)Biome.taiga || bm == (int)Biome.snowyPlains || bm == (int)Biome.meadow;
                return ok && rng.nextFloat() < 0.5;
            },
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16 + 4, z = ocz * 16 + 4;
                var y = ctx.heightAt(x + 4, z + 4);
                int P = (int)cell(B.dark_oak_planks), L = (int)cell(B.dark_oak_log), C = (int)cell(B.cobblestone);
                return new StructurePlan(id: "pillager_outpost", pieces: new List<StructPiece>
                {
                    piece(x - 6, y - 6, z - 6, x + 14, y + 22, z + 14, b =>
                    {
                        for (int dz = 0; dz < 8; dz++) { for (int dx = 0; dx < 8; dx++) { b.foundation(x + dx, y - 1, z + dz, C, 6); } }
                        b.walls(x, y, z, x + 7, y + 3, z + 7, C, AIR);
                        b.walls(x + 1, y + 4, z + 1, x + 6, y + 9, z + 6, P, AIR);
                        b.walls(x, y + 10, z, x + 7, y + 14, z + 7, P, AIR);
                        foreach (var (cx2, cz2) in new[] { (0, 0), (7, 0), (0, 7), (7, 7) })
                        {
                            for (int h = 0; h < 15; h++) { b.set(x + cx2, y + h, z + cz2, L); }
                        }
                        // door
                        b.set(x + 3, y, z, AIR); b.set(x + 4, y, z, AIR);
                        b.set(x + 3, y + 1, z, AIR); b.set(x + 4, y + 1, z, AIR);
                        // floors + ladders
                        b.fill(x + 1, y + 4, z + 1, x + 6, y + 4, z + 6, P);
                        b.fill(x + 1, y + 10, z + 1, x + 6, y + 10, z + 6, P);
                        b.set(x + 1, y + 4, z + 1, AIR); b.set(x + 1, y + 10, z + 1, AIR);
                        for (int h = 0; h < 14; h++) { b.set(x + 1, y + h, z + 2, (int)cell(B.ladder, 5)); }
                        // crenellations + windows
                        var d = 0;
                        while (d < 8)
                        {
                            b.set(x + d, y + 15, z, P); b.set(x + d, y + 15, z + 7, P);
                            b.set(x, y + 15, z + d, P); b.set(x + 7, y + 15, z + d, P);
                            d += 2;
                        }
                        foreach (var wy in new[] { 6, 12 })
                        {
                            b.set(x + 3, y + wy, z, AIR); b.set(x + 4, y + wy, z + 7, AIR);
                            b.set(x, y + wy, z + 3, AIR); b.set(x + 7, y + wy, z + 4, AIR);
                        }
                        b.chest(x + 5, y + 11, z + 5, 2, "pillager_outpost");
                        // mobs: captain on top + patrols
                        b.mob("pillager", x + 4, y + 11, z + 4, new Dictionary<string, BEValue> { ["captain"] = BEValue.@bool(true), ["persistent"] = BEValue.@bool(true) });
                        b.mob("pillager", x + 2, y, z + 3, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("pillager", x + 9, y, z + 8, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        b.mob("pillager", x - 2, y, z + 2, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        // golem cage (50%)
                        if (b.rng.nextBoolean())
                        {
                            int gx = x + 11, gz = z + 2;
                            var gy = ctx.heightAt(gx + 1, gz + 1);
                            b.walls(gx, gy, gz, gx + 3, gy + 3, gz + 3, (int)cell(B.dark_oak_fence), AIR);
                            b.fill(gx, gy + 3, gz, gx + 3, gy + 3, gz + 3, P);
                            b.mob("iron_golem", gx + 1, gy, gz + 1);
                        }
                        // tent
                        int tx = x - 5, tz = z + 8;
                        var ty = ctx.heightAt(tx + 1, tz + 1);
                        b.fill(tx, ty, tz, tx + 2, ty, tz + 2, (int)cell(B.white_wool));
                        b.fill(tx, ty + 1, tz + 1, tx + 2, ty + 1, tz + 1, (int)cell(B.white_wool));
                    }),
                }, @ref: new StructRefBox(x - 16, y - 8, z - 16, x + 24, y + 24, z + 24));
            }
        ));

        registerStructure(new StructureDef(
            id: "shipwreck", spacing: 24, separation: 4, salt: 165745295, maxRadiusChunks: 2,
            check: (ctx, ocx, ocz, rng) =>
            {
                var bm = ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8);
                return (isOceanBiome(bm) || bm == (int)Biome.beach) && rng.nextFloat() < 0.3;
            },
            plan: (ctx, ocx, ocz, rng) =>
            {
                int x = ocx * 16 + 2, z = ocz * 16 + 4;
                var seafloor = ctx.heightAt(x + 5, z + 4);
                var y = Math.Max(seafloor, 35);
                var variantRoll = rng.nextFloat();
                var variant = variantRoll < 0.4 ? "full" : variantRoll < 0.7 ? "bow" : "stern";
                var P = (int)cell(rng.nextBoolean() ? B.oak_planks : B.spruce_planks);
                var L = (int)cell(B.spruce_log);
                var len = 20;
                return new StructurePlan(id: "shipwreck", pieces: new List<StructPiece>
                {
                    piece(x - 1, y - 2, z - 1, x + len + 1, y + 12, z + 8, b =>
                    {
                        var tilt = b.rng.nextInt(3) - 1;
                        var x0 = variant == "bow" ? 8 : 0;
                        var x1 = variant == "stern" ? 12 : len;
                        // hull
                        for (int dx = x0; dx <= x1; dx++)
                        {
                            var w = dx < 3 || dx > len - 3 ? 2 : 3; // taper ends
                            var yy = y + (int)Math.Floor((double)(dx * tilt) * 0.1);
                            for (int dz = 4 - w; dz <= 3 + w - 1; dz++)
                            {
                                b.set(x + dx, yy, z + dz, P);
                                b.set(x + dx, yy + 1, z + 4 - w, P);
                                b.set(x + dx, yy + 1, z + 3 + w - 1, P);
                                b.set(x + dx, yy + 2, z + 4 - w, P);
                                b.set(x + dx, yy + 2, z + 3 + w - 1, P);
                            }
                            if (dx == x0 || dx == x1)
                            {
                                for (int dz = 4 - w; dz <= 3 + w - 1; dz++)
                                {
                                    b.set(x + dx, yy + 1, z + dz, P);
                                    b.set(x + dx, yy + 2, z + dz, P);
                                }
                            }
                            // deck
                            if (dx > x0 + 1 && dx < x1 - 1 && b.rng.nextFloat() < 0.8)
                            {
                                for (int dz = 2; dz <= 5; dz++) { b.set(x + dx, yy + 3, z + dz, P); }
                            }
                        }
                        // mast
                        if (variant != "stern")
                        {
                            for (int h = 0; h < 9; h++) { b.set(x + 12, y + 3 + h, z + 4, L); }
                        }
                        // chests
                        if (variant != "bow") { b.chest(x + 3, y + 1, z + 4, 1, "shipwreck_supply"); }
                        if (variant != "stern") { b.chest(x + len - 3, y + 1, z + 4, 0, "shipwreck_treasure"); }
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "ocean_ruin", spacing: 20, separation: 8, salt: 14357621, maxRadiusChunks: 1,
            check: (ctx, ocx, ocz, _r) =>
                isOceanBiome(ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8)),
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16 + 4, z = ocz * 16 + 4;
                var bm = ctx.biomeAt(x, z);
                var warm = bm == (int)Biome.warmOcean || bm == (int)Biome.lukewarmOcean || bm == (int)Biome.deepLukewarmOcean;
                var y = ctx.heightAt(x + 3, z + 3);
                var W = warm ? (int)cell(B.sandstone) : (int)cell(B.stone_bricks);
                var W2 = warm ? (int)cell(B.cut_sandstone) : (int)cell(B.cracked_stone_bricks);
                return new StructurePlan(id: "ocean_ruin", pieces: new List<StructPiece>
                {
                    piece(x - 1, y - 2, z - 1, x + 8, y + 6, z + 8, b =>
                    {
                        // ruined shell
                        for (int dz = 0; dz < 7; dz++)
                        {
                            for (int dx = 0; dx < 7; dx++)
                            {
                                if (dx == 0 || dx == 6 || dz == 0 || dz == 6)
                                {
                                    var h = b.rng.nextInt(4);
                                    for (int dy = 0; dy <= h; dy++)
                                    {
                                        b.set(x + dx, y + dy, z + dz, b.rng.nextFloat() < 0.7 ? W : W2);
                                    }
                                }
                                else
                                {
                                    b.set(x + dx, y - 1, z + dz, W);
                                }
                            }
                        }
                        var big = b.rng.nextFloat() < 0.3;
                        b.chest(x + 3, y, z + 3, 0, big ? "underwater_ruin_big" : "underwater_ruin_small");
                        b.mob("drowned", x + 2, y + 1, z + 2, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) });
                        if (big) { b.mob("drowned", x + 4, y + 1, z + 4, new Dictionary<string, BEValue> { ["persistent"] = BEValue.@bool(true) }); }
                        // archaeology
                        b.suspicious(x + 1, y - 1, z + 5, !warm, warm ? "ocean_ruin_warm_archaeology" : "ocean_ruin_cold_archaeology");
                        b.suspicious(x + 5, y - 1, z + 1, !warm, warm ? "ocean_ruin_warm_archaeology" : "ocean_ruin_cold_archaeology");
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "buried_treasure", spacing: 8, separation: 4, salt: 10387320, maxRadiusChunks: 1,
            check: (ctx, ocx, ocz, rng) =>
            {
                var bm = ctx.biomeAt(ocx * 16 + 9, ocz * 16 + 9);
                return (bm == (int)Biome.beach || bm == (int)Biome.snowyBeach) && rng.nextFloat() < 0.01 * 8;
            },
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16 + 9, z = ocz * 16 + 9;
                var y = ctx.heightAt(x, z) - 4;
                return new StructurePlan(id: "buried_treasure", pieces: new List<StructPiece>
                {
                    piece(x, y, z, x, y, z, b =>
                    {
                        b.chest(x, y, z, 0, "buried_treasure");
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "ruined_portal", spacing: 28, separation: 10, salt: 34222645, maxRadiusChunks: 1,
            check: (ctx, ocx, ocz, _r) =>
                !isOceanBiome(ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8)),
            plan: (ctx, ocx, ocz, _r) =>
            {
                int x = ocx * 16 + 5, z = ocz * 16 + 7;
                var y = ctx.heightAt(x + 2, z);
                return new StructurePlan(id: "ruined_portal", pieces: new List<StructPiece>
                {
                    piece(x - 3, y - 3, z - 3, x + 7, y + 6, z + 4, b =>
                    {
                        buildRuinedPortal(b, x, y, z, false);
                    }),
                });
            }
        ));

        registerStructure(new StructureDef(
            id: "trail_ruins", spacing: 34, separation: 8, salt: 83469867, maxRadiusChunks: 2,
            check: (ctx, ocx, ocz, _r) =>
            {
                var bm = ctx.biomeAt(ocx * 16 + 8, ocz * 16 + 8);
                return bm == (int)Biome.taiga || bm == (int)Biome.snowyTaiga || bm == (int)Biome.oldGrowthBirchForest ||
                    bm == (int)Biome.oldGrowthPineTaiga || bm == (int)Biome.jungle;
            },
            plan: (ctx, ocx, ocz, rng) =>
            {
                int cxw = ocx * 16 + 8, czw = ocz * 16 + 8;
                var surfaceY = ctx.heightAt(cxw, czw);
                var y = surfaceY - 6;
                var pieces = new List<StructPiece>();
                var mats = new[] { (int)cell(B.mud_bricks), (int)cell(B.packed_mud), (int)cell(B.terracotta), (int)cell(B.cobblestone), (int)cell(B.bricks) };
                var buildings = 3 + rng.nextInt(3);
                for (int _bld = 0; _bld < buildings; _bld++)
                {
                    int bx = cxw + rng.nextInt(24) - 12, bz = czw + rng.nextInt(24) - 12;
                    int w = 5 + rng.nextInt(4), d = 5 + rng.nextInt(4), h = 3 + rng.nextInt(2);
                    pieces.Add(piece(bx - 1, y - 2, bz - 1, bx + w + 1, y + h + 1, bz + d + 1, b =>
                    {
                        for (int dz = 0; dz <= d; dz++)
                        {
                            for (int dx = 0; dx <= w; dx++)
                            {
                                var isWall = dx == 0 || dx == w || dz == 0 || dz == d;
                                b.set(bx + dx, y - 1, bz + dz, mats[b.rng.nextInt(mats.Length)]);
                                if (isWall)
                                {
                                    var wh = b.rng.nextInt(h + 1);
                                    for (int dy = 0; dy <= wh; dy++)
                                    {
                                        if (b.rng.nextFloat() < 0.85) { b.set(bx + dx, y + dy, bz + dz, mats[b.rng.nextInt(mats.Length)]); }
                                    }
                                }
                            }
                        }
                        // suspicious gravel with archaeology loot
                        for (int _i = 0; _i < 3; _i++)
                        {
                            var sx = bx + 1 + b.rng.nextInt(Math.Max(1, w - 1));
                            var sz = bz + 1 + b.rng.nextInt(Math.Max(1, d - 1));
                            b.suspicious(sx, y, sz, true, b.rng.nextFloat() < 0.18 ? "trail_ruins_rare" : "trail_ruins_archaeology");
                        }
                        // decorated pot + lamps
                        if (b.rng.nextBoolean())
                        {
                            b.set(bx + 2, y, bz + 2, (int)cell(B.decorated_pot));
                            b.s.addBlockEntity(new BESpec(x: bx + 2, y: y, z: bz + 2, kind: "pot_sherds"));
                        }
                    }));
                }
                return new StructurePlan(id: "trail_ruins", pieces: pieces);
            }
        ));
    }
}

// Random-tick behaviors — crop growth, saplings,
// grass/mycelium spread, leaf decay, fire spread, ice/snow, copper oxidation,
// cane/cactus/bamboo/kelp/vines/berries, farmland moisture, turtle eggs,
// frogspawn, amethyst, dripstone, sculk, chorus, gravity blocks, weather.
//
// Uses its own seeded module RNG (0xFA01) exactly like the frozen baseline.
//
// Defines static class `FarmingGlobals` (surface via `global using static
// PebbleCore.FarmingGlobals`) holding the module-global free funcs/vars of
// Farming.swift: farmingRng + the public entrypoints growTreeAt/igniteTNT/
// sculkBloom/weatherRandomTick/registerFarmingHandlers/popBlock/registerSupportPops.
// Namespace-level type declared here: WorldSink (the world-backed ChunkSink).
//
// External (cross-layer) symbols referenced by name: World, ChunkSink, BESpec,
// EntitySpec, BEValue, SpawnOpts, spawnMob, spawnItem, ItemStack, TNTEntity,
// FallingBlockEntity, the gen* tree functions (Features), itemDefs/blockToItem/
// iidOpt (ItemGlobals), gameRng (DetMath), snowsAt (BiomeGlobals), COLORS
// (BlockData). cell/SHAPE_OF/blockDefs/B/bid/bidOpt come from BlockCaches/Reg;
// blockTickHandlers/randomTickHandlers/neighborHandlers from WorldHandlers.

using System;
using System.Collections.Generic;

namespace PebbleCore;

// world-backed sink for tree growth
public sealed class WorldSink : ChunkSink
{
    public int cx { get; set; } = 0;
    public int cz { get; set; } = 0;
    public int minY { get; }
    public int maxY { get; }
    private readonly World world;

    public WorldSink(World world)
    {
        this.world = world;
        minY = world.info.minY;
        maxY = world.info.minY + world.info.height;
    }

    public void set(int x, int y, int z, ushort c)
    {
        int cur = world.getBlock(x, y, z) >> 4;
        string curName = cur != 0 ? blockDefs[cur].name : "";
        if (cur == 0 || blockDefs[cur].replaceable || curName.Contains("leaves") || c == 0)
        {
            world.setBlock(x, y, z, (int)c);
        }
        else if ((c >> 4) != 0)
        {
            string newName = blockDefs[(int)(c >> 4)].name;
            if (newName.Contains("log") || newName.Contains("dirt") || newName.Contains("mangrove_roots"))
            {
                world.setBlock(x, y, z, (int)c);
            }
        }
    }
    public int get(int x, int y, int z) => world.getBlock(x, y, z);
    public int topY(int x, int z) => world.surfaceY(x, z);
    public void addBlockEntity(BESpec spec) { }
    public void addEntity(EntitySpec spec)
    {
        var opts = new SpawnOpts();
        if (spec.data.TryGetValue("baby", out var v) && v != null
            && v.tag == BEValue.Tag.@bool && v.boolVal)
        {
            opts.baby = true;
        }
        spawnMob(world, spec.mob, spec.x, spec.y, spec.z, opts);
    }
}

public static class FarmingGlobals
{
    public static RandomX farmingRng = new RandomX(0xFA01);

    public static bool growTreeAt(World world, int x, int y, int z, int sapId)
    {
        var sink = new WorldSink(world);
        world.setBlock(x, y, z, 0);
        if (sapId == (int)B.oak_sapling) { genOakTree(sink, ref farmingRng, x, y, z, fancy: farmingRng.nextFloat() < 0.1); }
        else if (sapId == (int)B.birch_sapling) { genBirchTree(sink, ref farmingRng, x, y, z); }
        else if (sapId == (int)B.spruce_sapling) { genSpruceTree(sink, ref farmingRng, x, y, z); }
        else if (sapId == (int)B.jungle_sapling) { genJungleTree(sink, ref farmingRng, x, y, z, mega: false); }
        else if (sapId == (int)B.acacia_sapling) { genAcaciaTree(sink, ref farmingRng, x, y, z); }
        else if (sapId == (int)B.dark_oak_sapling) { genDarkOakTree(sink, ref farmingRng, x, y, z); }
        else if (sapId == (int)B.cherry_sapling) { genCherryTree(sink, ref farmingRng, x, y, z); }
        else if (sapId == (int)B.mangrove_propagule) { genMangroveTree(sink, ref farmingRng, x, y, z); }
        else if (sapId == (int)B.crimson_fungus) { genHugeFungus(sink, ref farmingRng, x, y, z, crimson: true); }
        else if (sapId == (int)B.warped_fungus) { genHugeFungus(sink, ref farmingRng, x, y, z, crimson: false); }
        else { world.setBlock(x, y, z, (int)cell((ushort)sapId)); return false; }
        return true;
    }

    public static void igniteTNT(World world, int x, int y, int z)
    {
        world.setBlock(x, y, z, 0);
        var tnt = new TNTEntity(world);
        tnt.setPos((double)x + 0.5, (double)y, (double)z + 0.5);
        world.addEntity(tnt);
        world.hooks.playSound("entity.tnt.primed", (double)x + 0.5, (double)y, (double)z + 0.5, 1, 1);
    }

    // sculk spreading from catalysts handled at mob death (Game hook)
    public static void sculkBloom(World world, int x, int y, int z, int xp)
    {
        // find catalyst nearby
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dz = -8; dz <= 8; dz++)
            {
                for (int dx = -8; dx <= 8; dx++)
                {
                    if ((world.getBlock(x + dx, y + dy, z + dz) >> 4) == (int)B.sculk_catalyst)
                    {
                        // spread sculk around death point
                        int n = Math.Min(20, 2 + xp);
                        for (int _i = 0; _i < n; _i++)
                        {
                            int sx = x + farmingRng.nextInt(7) - 3;
                            int sz = z + farmingRng.nextInt(7) - 3;
                            int sy = y + 3;
                            while (sy > y - 4)
                            {
                                int ground = world.getBlock(sx, sy - 1, sz) >> 4;
                                int at = world.getBlock(sx, sy, sz) >> 4;
                                if (at == 0 && ground != 0 && blockDefs[ground].solid && ground != (int)B.sculk)
                                {
                                    world.setBlock(sx, sy - 1, sz, (int)cell(B.sculk));
                                    if (farmingRng.nextFloat() < 0.09) { world.setBlock(sx, sy, sz, (int)cell(B.sculk_vein, 0)); }
                                    break;
                                }
                                sy -= 1;
                            }
                        }
                        world.hooks.playSound("block.sculk_catalyst.bloom", (double)(x + dx), (double)(y + dy), (double)(z + dz), 1, 1);
                        world.hooks.addParticles("sculk_soul", (double)x, (double)y + 0.5, (double)z, 6, 0.4, 0);
                        return;
                    }
                }
            }
        }
    }

    // snow/ice formation during snowfall (called from weather tick)
    public static void weatherRandomTick(World world, int x, int z)
    {
        if (world.rainLevel < 0.5) { return; }
        int y = world.heightAt(x, z) + 1;
        int biome = world.biomeAt(x, y, z);
        if (snowsAt(biome, y))
        {
            int at = world.getBlock(x, y, z);
            int below = world.getBlock(x, y - 1, z);
            if (at == 0 && (below >> 4) != 0 && blockDefs[below >> 4].fullCube)
            {
                world.setBlock(x, y, z, (int)cell(B.snow, 0));
            }
            if ((below >> 4) == (int)B.water && (below & 15) == 0)
            {
                world.setBlock(x, y - 1, z, (int)cell(B.ice));
            }
        }
        else
        {
            // cauldron rain fill
            int at = world.getBlock(x, y - 1, z);
            if ((at >> 4) == (int)B.cauldron && ((at >> 2) & 3) == 0 && (at & 3) < 3 && farmingRng.nextFloat() < 0.3)
            {
                world.setBlock(x, y - 1, z, (int)cell(B.cauldron, (at & 3) + 1));
            }
        }
    }

    private static bool farmingRegistered = false;

    public static void registerFarmingHandlers()
    {
        if (farmingRegistered) { return; }
        farmingRegistered = true;

        void reg(ushort id, BlockTickFn fn)
        {
            randomTickHandlers[(int)id] = fn;
        }

        // crops -------------------------------------------------------------------
        BlockTickFn cropTick(int maxStage, int metaBits)
        {
            return (world, x, y, z, c) =>
            {
                int stage = c & metaBits;
                if (stage >= maxStage) { return; }
                if (world.lightAt(x, y, z) < 9) { return; }
                // moist farmland boosts
                int below = world.getBlock(x, y - 1, z);
                bool moist = (below >> 4) == (int)B.farmland && (below & 7) >= 7;
                if (farmingRng.nextFloat() < (moist ? 0.33 : 0.14))
                {
                    world.setBlock(x, y, z, (int)cell((ushort)(c >> 4), stage + 1));
                }
            };
        }
        reg(B.wheat, cropTick(7, 7));
        reg(B.carrots, cropTick(7, 7));
        reg(B.potatoes, cropTick(7, 7));
        reg(B.beetroots, cropTick(3, 3));
        reg(B.torchflower_crop, (world, x, y, z, c) =>
        {
            int stage = c & 1;
            if (world.lightAt(x, y, z) < 9) { return; }
            if (farmingRng.nextFloat() < 0.2)
            {
                if (stage == 0) { world.setBlock(x, y, z, (int)cell(B.torchflower_crop, 1)); }
                else { world.setBlock(x, y, z, (int)cell(B.torchflower)); }
            }
        });
        reg(B.pitcher_crop, (world, x, y, z, c) =>
        {
            int stage = c & 7;
            if (world.lightAt(x, y, z) < 9 || (c & 8) != 0) { return; }
            if (farmingRng.nextFloat() < 0.2)
            {
                if (stage < 4) { world.setBlock(x, y, z, (int)cell(B.pitcher_crop, stage + 1)); }
                else
                {
                    world.setBlock(x, y, z, (int)cell(B.pitcher_plant, 0));
                    if (world.getBlock(x, y + 1, z) == 0) { world.setBlock(x, y + 1, z, (int)cell(B.pitcher_plant, 1)); }
                }
            }
        });
        foreach (var stem in new[] { B.melon_stem, B.pumpkin_stem })
        {
            reg(stem, (world, x, y, z, c) =>
            {
                if (world.lightAt(x, y, z) < 9) { return; }
                int stage = c & 7;
                if ((c & 8) != 0) { return; } // attached
                if (farmingRng.nextFloat() > 0.2) { return; }
                if (stage < 7)
                {
                    world.setBlock(x, y, z, (int)cell(stem, stage + 1));
                }
                else
                {
                    // grow fruit
                    ushort fruit = stem == B.melon_stem ? B.melon : B.pumpkin;
                    var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
                    // already has fruit?
                    foreach (var (ddx, ddz) in dirs)
                    {
                        if ((world.getBlock(x + ddx, y, z + ddz) >> 4) == (int)fruit) { return; }
                    }
                    var (dx, dz) = dirs[farmingRng.nextInt(4)];
                    int target = world.getBlock(x + dx, y, z + dz);
                    int ground = world.getBlock(x + dx, y - 1, z + dz) >> 4;
                    if (target == 0 && (ground == (int)B.grass_block || ground == (int)B.dirt || ground == (int)B.farmland || ground == (int)B.coarse_dirt))
                    {
                        world.setBlock(x + dx, y, z + dz, (int)cell(fruit));
                        world.setBlock(x, y, z, (int)cell(stem, 8 | 7));
                    }
                }
            });
        }
        reg(B.nether_wart, (world, x, y, z, c) =>
        {
            int stage = c & 3;
            if (stage < 3 && farmingRng.nextFloat() < 0.1) { world.setBlock(x, y, z, (int)cell(B.nether_wart, stage + 1)); }
        });
        reg(B.sweet_berry_bush, (world, x, y, z, c) =>
        {
            int stage = c & 3;
            if (stage < 3 && world.lightAt(x, y, z) >= 9 && farmingRng.nextFloat() < 0.2)
            {
                world.setBlock(x, y, z, (int)cell(B.sweet_berry_bush, stage + 1));
            }
        });
        reg(B.cocoa, (world, x, y, z, c) =>
        {
            int age = (c >> 2) & 3;
            if (age < 2 && farmingRng.nextFloat() < 0.2) { world.setBlock(x, y, z, (int)cell(B.cocoa, (c & 3) | ((age + 1) << 2))); }
        });
        reg(B.farmland, (world, x, y, z, c) =>
        {
            // hydration scan
            bool wet = false;
            for (int dz = -4; dz <= 4; dz++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        if ((world.getBlock(x + dx, y + dy, z + dz) >> 4) == (int)B.water) { wet = true; goto outer; }
                    }
                }
            }
        outer:
            int moisture = c & 7;
            if (wet || world.isRainingAt(x, y + 1, z))
            {
                if (moisture < 7) { world.setBlock(x, y, z, (int)cell(B.farmland, 7)); }
            }
            else if (moisture > 0)
            {
                world.setBlock(x, y, z, (int)cell(B.farmland, moisture - 1));
            }
            else
            {
                // dry: revert to dirt if no crop
                int above = world.getBlock(x, y + 1, z) >> 4;
                if (above != (int)B.wheat && above != (int)B.carrots && above != (int)B.potatoes && above != (int)B.beetroots && above != (int)B.melon_stem && above != (int)B.pumpkin_stem)
                {
                    world.setBlock(x, y, z, (int)cell(B.dirt));
                }
            }
        });

        // saplings / fungi --------------------------------------------------------
        foreach (var sap in new[] { B.oak_sapling, B.birch_sapling, B.spruce_sapling, B.jungle_sapling, B.acacia_sapling, B.dark_oak_sapling, B.cherry_sapling })
        {
            reg(sap, (world, x, y, z, c) =>
            {
                if (world.lightAt(x, y, z) < 9) { return; }
                if ((c & 8) == 0)
                {
                    if (farmingRng.nextFloat() < 0.15) { world.setBlock(x, y, z, (int)cell((ushort)(c >> 4), c & 7 | 8)); }
                }
                else if (farmingRng.nextFloat() < 0.15)
                {
                    growTreeAt(world, x, y, z, c >> 4);
                }
            });
        }
        reg(B.mangrove_propagule, (world, x, y, z, c) =>
        {
            if ((c & 8) != 0)
            {
                // hanging: grow age then drop
                int age = c & 7;
                if (age < 4 && farmingRng.nextFloat() < 0.15) { world.setBlock(x, y, z, (int)cell(B.mangrove_propagule, (age + 1) | 8)); }
                return;
            }
            if (world.lightAt(x, y, z) >= 9 && farmingRng.nextFloat() < 0.12) { growTreeAt(world, x, y, z, (int)B.mangrove_propagule); }
        });

        // grass spread / decay ------------------------------------------------------
        reg(B.grass_block, (world, x, y, z, _) =>
        {
            int above = world.getBlock(x, y + 1, z);
            int aboveId = above >> 4;
            if (blockDefs[aboveId].lightOpacity >= 15 && aboveId != 0)
            {
                world.setBlock(x, y, z, (int)cell(B.dirt));
                return;
            }
            if (world.lightAt(x, y + 1, z) >= 9)
            {
                for (int _i = 0; _i < 4; _i++)
                {
                    int tx = x + farmingRng.nextInt(3) - 1;
                    int ty = y + farmingRng.nextInt(5) - 3;
                    int tz = z + farmingRng.nextInt(3) - 1;
                    if ((world.getBlock(tx, ty, tz) >> 4) == (int)B.dirt)
                    {
                        int tAbove = world.getBlock(tx, ty + 1, tz) >> 4;
                        if (blockDefs[tAbove].lightOpacity < 15 && world.lightAt(tx, ty + 1, tz) >= 4)
                        {
                            world.setBlock(tx, ty, tz, (int)cell(B.grass_block));
                        }
                    }
                }
            }
        });
        reg(B.mycelium, (world, x, y, z, _) =>
        {
            int above = world.getBlock(x, y + 1, z) >> 4;
            if (blockDefs[above].lightOpacity >= 15 && above != 0) { world.setBlock(x, y, z, (int)cell(B.dirt)); }
        });
        foreach (var nylium in new[] { B.crimson_nylium, B.warped_nylium })
        {
            reg(nylium, (world, x, y, z, _) =>
            {
                int above = world.getBlock(x, y + 1, z) >> 4;
                if (blockDefs[above].lightOpacity >= 15 && above != 0) { world.setBlock(x, y, z, (int)cell(B.netherrack)); }
            });
        }

        // leaves decay ----------------------------------------------------------------
        foreach (var leaf in new[] { "oak_leaves", "spruce_leaves", "birch_leaves", "jungle_leaves", "acacia_leaves", "dark_oak_leaves", "mangrove_leaves", "cherry_leaves", "azalea_leaves", "flowering_azalea_leaves" })
        {
            reg(bid(leaf), (world, x, y, z, c) =>
            {
                if ((c & 8) != 0) { return; } // persistent
                // distance to log scan (BFS depth 5)
                bool foundLog = false;
                var seen = new HashSet<(int, int, int)>();
                var queue = new List<(int, int, int, int)> { (x, y, z, 0) };
                int head = 0;
                while (head < queue.Count)
                {
                    var (qx, qy, qz, d) = queue[head];
                    head += 1;
                    if (d > 5) { continue; }
                    foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                    {
                        int nx = qx + dx, ny = qy + dy, nz = qz + dz;
                        var k = (nx, ny, nz);
                        if (seen.Contains(k)) { continue; }
                        seen.Add(k);
                        int id = world.getBlock(nx, ny, nz) >> 4;
                        string name = id >= 0 && id < blockDefs.Count ? blockDefs[id].name : "";
                        if (name.EndsWith("_log") || name.EndsWith("_stem") || name.EndsWith("_wood") || name.Contains("hyphae")) { foundLog = true; break; }
                        if (name.Contains("leaves") && d < 5) { queue.Add((nx, ny, nz, d + 1)); }
                    }
                    if (foundLog) { break; }
                }
                if (!foundLog)
                {
                    world.breakBlockNaturally(x, y, z);
                }
            });
        }

        // fire spread -------------------------------------------------------------------
        blockTickHandlers[(int)B.fire] = (world, x, y, z, c) =>
        {
            if (!world.rule("doFireTick")) { return; }
            int below = world.getBlock(x, y - 1, z) >> 4;
            bool infiniburn = below == (int)B.netherrack || below == (int)B.magma_block || below == (int)B.bedrock;
            // rain extinguish
            if (world.isRainingAt(x, y, z) && !infiniburn)
            {
                world.setBlock(x, y, z, 0);
                return;
            }
            int age = c & 15;
            if (age < 15) { world.setBlock(x, y, z, (int)cell(B.fire, age + 1), 2 | 4); }
            // burn neighbors
            bool anyFlammable = infiniburn;
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            {
                int n = world.getBlock(x + dx, y + dy, z + dz);
                int nid = n >> 4;
                if (nid == 0) { continue; }
                var def = blockDefs[nid];
                if (def.flammable > 0)
                {
                    anyFlammable = true;
                    if (farmingRng.nextInt(100) < def.burnOdds)
                    {
                        world.setBlock(x + dx, y + dy, z + dz, farmingRng.nextFloat() < 0.75 ? (int)cell(B.fire) : 0);
                        if (nid == (int)B.tnt)
                        {
                            igniteTNT(world, x + dx, y + dy, z + dz);
                        }
                    }
                }
            }
            if (!anyFlammable && (below == 0 || !blockDefs[below].solid))
            {
                world.setBlock(x, y, z, 0);
                return;
            }
            if (age >= 15 && !infiniburn && farmingRng.nextFloat() < 0.3)
            {
                world.setBlock(x, y, z, 0);
                return;
            }
            // spread to nearby flammables
            if (world.difficulty > 0)
            {
                for (int _i = 0; _i < 3; _i++)
                {
                    int tx = x + farmingRng.nextInt(3) - 1;
                    int ty = y + farmingRng.nextInt(4) - 1;
                    int tz = z + farmingRng.nextInt(3) - 1;
                    if (world.getBlock(tx, ty, tz) != 0) { continue; }
                    // flammable neighbor?
                    foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                    {
                        var def = blockDefs[world.getBlock(tx + dx, ty + dy, tz + dz) >> 4];
                        if (def.flammable > 0 && farmingRng.nextInt(200) < def.flammable)
                        {
                            world.setBlock(tx, ty, tz, (int)cell(B.fire));
                            break;
                        }
                    }
                }
            }
            world.scheduleTick(x, y, z, (int)B.fire, 30 + farmingRng.nextInt(10));
        };
        neighborHandlers[(int)B.fire] = (world, x, y, z, c, _, _, _) =>
        {
            if (!world.hasScheduledTick(x, y, z, (int)B.fire)) { world.scheduleTick(x, y, z, (int)B.fire, 30); }
            int below = world.getBlock(x, y - 1, z) >> 4;
            if (below == 0 || (!blockDefs[below].solid && below != (int)B.netherrack))
            {
                bool anyNeighbor = false;
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0) })
                {
                    if (blockDefs[world.getBlock(x + dx, y + dy, z + dz) >> 4].flammable > 0) { anyNeighbor = true; break; }
                }
                if (!anyNeighbor) { world.setBlock(x, y, z, 0); }
            }
        };

        // ice / snow -------------------------------------------------------------------
        reg(B.ice, (world, x, y, z, _) =>
        {
            if (world.lightAt(x, y, z) > 11)
            {
                world.setBlock(x, y, z, world.dim == Dim.overworld ? (int)cell(B.water, 0) : 0);
            }
        });
        reg(B.frosted_ice, (world, x, y, z, c) =>
        {
            int age = c & 3;
            if (age < 3) { world.setBlock(x, y, z, (int)cell(B.frosted_ice, age + 1)); }
            else { world.setBlock(x, y, z, (int)cell(B.water, 0)); }
        });
        reg(B.snow, (world, x, y, z, _) =>
        {
            if (world.lightAt(x, y, z) > 11) { world.setBlock(x, y, z, 0); }
        });

        // copper oxidation ----------------------------------------------------------------
        var copperChain = new List<(string, string)>();
        foreach (var @base in new[] { "copper_block", "cut_copper", "cut_copper_stairs", "cut_copper_slab" })
        {
            string stem = @base == "copper_block" ? "copper_block" : @base;
            copperChain.Add((@base, $"exposed_{stem}"));
            copperChain.Add(($"exposed_{stem}", $"weathered_{stem}"));
            copperChain.Add(($"weathered_{stem}", $"oxidized_{stem}"));
        }
        foreach (var (from, to) in copperChain)
        {
            var fromIdOpt = bidOpt(from);
            var toIdOpt = bidOpt(to);
            if (fromIdOpt == null || toIdOpt == null) { continue; }
            ushort fromId = fromIdOpt.Value;
            ushort toId = toIdOpt.Value;
            reg(fromId, (world, x, y, z, c) =>
            {
                if (farmingRng.nextFloat() < 0.05)
                {
                    world.setBlock(x, y, z, (int)cell(toId, c & 15));
                }
            });
        }

        // cane / cactus / bamboo / kelp / vines ---------------------------------------------
        reg(B.sugar_cane, (world, x, y, z, _) =>
        {
            if (world.getBlock(x, y + 1, z) != 0) { return; }
            int h = 1;
            while ((world.getBlock(x, y - h, z) >> 4) == (int)B.sugar_cane) { h += 1; }
            if (h < 3 && farmingRng.nextFloat() < 0.18) { world.setBlock(x, y + 1, z, (int)cell(B.sugar_cane)); }
        });
        reg(B.cactus, (world, x, y, z, _) =>
        {
            if (world.getBlock(x, y + 1, z) != 0) { return; }
            int h = 1;
            while ((world.getBlock(x, y - h, z) >> 4) == (int)B.cactus) { h += 1; }
            if (h < 3 && farmingRng.nextFloat() < 0.18) { world.setBlock(x, y + 1, z, (int)cell(B.cactus)); }
        });
        reg(B.bamboo, (world, x, y, z, _) =>
        {
            if (world.getBlock(x, y + 1, z) != 0) { return; }
            int h = 1;
            while ((world.getBlock(x, y - h, z) >> 4) == (int)B.bamboo) { h += 1; }
            if (h < 14 && farmingRng.nextFloat() < 0.3)
            {
                world.setBlock(x, y + 1, z, (int)cell(B.bamboo, h > 4 ? 2 | 4 : 1 | 4));
            }
        });
        reg(B.bamboo_sapling, (world, x, y, z, _) =>
        {
            if (world.getBlock(x, y + 1, z) == 0 && farmingRng.nextFloat() < 0.3)
            {
                world.setBlock(x, y, z, (int)cell(B.bamboo, 4));
                world.setBlock(x, y + 1, z, (int)cell(B.bamboo, 1));
            }
        });
        reg(B.kelp, (world, x, y, z, c) =>
        {
            int age = c & 15;
            if (age >= 14) { return; }
            if ((world.getBlock(x, y + 1, z) >> 4) == (int)B.water && farmingRng.nextFloat() < 0.14)
            {
                world.setBlock(x, y, z, (int)cell(B.kelp_plant));
                world.setBlock(x, y + 1, z, (int)cell(B.kelp, age + 1));
            }
        });
        reg(B.vine, (world, x, y, z, c) =>
        {
            if (farmingRng.nextFloat() > 0.25) { return; }
            // grow downward
            if (world.getBlock(x, y - 1, z) == 0)
            {
                world.setBlock(x, y - 1, z, (int)cell(B.vine, c & 15));
            }
        });
        foreach (var vines in new[] { B.weeping_vines, B.twisting_vines })
        {
            reg(vines, (world, x, y, z, _) =>
            {
                int dir = vines == B.weeping_vines ? -1 : 1;
                if (world.getBlock(x, y + dir, z) == 0 && farmingRng.nextFloat() < 0.1)
                {
                    world.setBlock(x, y + dir, z, (int)cell(vines));
                }
            });
        }
        reg(B.cave_vines, (world, x, y, z, c) =>
        {
            if (world.getBlock(x, y - 1, z) == 0 && farmingRng.nextFloat() < 0.1)
            {
                world.setBlock(x, y, z, (int)cell(B.cave_vines_plant, c & 8));
                world.setBlock(x, y - 1, z, (int)cell(B.cave_vines, farmingRng.nextFloat() < 0.11 ? 8 : 0));
            }
            else if ((c & 8) == 0 && farmingRng.nextFloat() < 0.06)
            {
                world.setBlock(x, y, z, (int)cell(B.cave_vines, 8));
            }
        });

        // azalea growth
        foreach (var az in new[] { B.azalea, B.flowering_azalea })
        {
            reg(az, (world, x, y, z, _) =>
            {
                if (world.lightAt(x, y, z) >= 9 && farmingRng.nextFloat() < 0.1)
                {
                    var sink = new WorldSink(world);
                    world.setBlock(x, y, z, 0);
                    genAzaleaTree(sink, ref farmingRng, x, y, z);
                }
            });
        }

        // turtle eggs / frogspawn / sniffer egg ----------------------------------------
        reg(B.turtle_egg, (world, x, y, z, c) =>
        {
            int hatch = (c >> 2) & 3;
            if (!world.isDay() && farmingRng.nextFloat() < 0.2)
            {
                if (hatch < 2)
                {
                    world.setBlock(x, y, z, (int)cell(B.turtle_egg, (c & 3) | ((hatch + 1) << 2)));
                    world.hooks.playSound("entity.turtle.egg_crack", (double)x + 0.5, (double)y, (double)z + 0.5, 0.7, 1);
                }
                else
                {
                    int count = (c & 3) + 1;
                    world.setBlock(x, y, z, 0);
                    world.hooks.playSound("entity.turtle.egg_hatch", (double)x + 0.5, (double)y, (double)z + 0.5, 0.7, 1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        spawnMob(world, "turtle", (double)x + 0.3 + farmingRng.nextFloat() * 0.4, (double)y, (double)z + 0.3 + farmingRng.nextFloat() * 0.4, new SpawnOpts(baby: true));
                    }
                }
            }
        });
        reg(B.frogspawn, (world, x, y, z, _) =>
        {
            if (farmingRng.nextFloat() < 0.25)
            {
                world.setBlock(x, y, z, 0);
                // baseline: rng-in-loop-condition — rerolls every iteration check
                int i = 0;
                while (i < 3 + farmingRng.nextInt(4))
                {
                    spawnMob(world, "tadpole", (double)x + farmingRng.nextFloat(), (double)y - 0.5, (double)z + farmingRng.nextFloat(), new SpawnOpts());
                    i += 1;
                }
            }
        });
        reg(B.sniffer_egg, (world, x, y, z, c) =>
        {
            int crack = c & 3;
            bool onMoss = (world.getBlock(x, y - 1, z) >> 4) == (int)B.moss_block;
            if (farmingRng.nextFloat() < (onMoss ? 0.3 : 0.15))
            {
                if (crack < 2)
                {
                    world.setBlock(x, y, z, (int)cell(B.sniffer_egg, crack + 1));
                    world.hooks.playSound("entity.sniffer.egg_crack", (double)x + 0.5, (double)y, (double)z + 0.5, 0.7, 1);
                }
                else
                {
                    world.setBlock(x, y, z, 0);
                    spawnMob(world, "sniffer", (double)x + 0.5, (double)y, (double)z + 0.5, new SpawnOpts(baby: true));
                    world.hooks.playSound("entity.sniffer.egg_hatch", (double)x + 0.5, (double)y, (double)z + 0.5, 1, 1);
                }
            }
        });

        // amethyst growth ----------------------------------------------------------------
        reg(B.budding_amethyst, (world, x, y, z, _) =>
        {
            if (farmingRng.nextFloat() > 0.2) { return; }
            int f = farmingRng.nextInt(6);
            int dx = new[] { 0, 0, 0, 0, -1, 1 }[f], dy = new[] { -1, 1, 0, 0, 0, 0 }[f], dz = new[] { 0, 0, -1, 1, 0, 0 }[f];
            int target = world.getBlock(x + dx, y + dy, z + dz);
            int tid = target >> 4;
            int next = -1;
            if (tid == 0) { next = (int)B.small_amethyst_bud; }
            else if (tid == (int)B.small_amethyst_bud) { next = (int)B.medium_amethyst_bud; }
            else if (tid == (int)B.medium_amethyst_bud) { next = (int)B.large_amethyst_bud; }
            else if (tid == (int)B.large_amethyst_bud) { next = (int)B.amethyst_cluster; }
            if (next >= 0)
            {
                world.setBlock(x + dx, y + dy, z + dz, (int)cell((ushort)next, f ^ 1));
                world.hooks.playSound("block.amethyst_cluster.step", (double)(x + dx), (double)(y + dy), (double)(z + dz), 0.5, 1);
            }
        });

        // pointed dripstone: growth + dripping into cauldrons ----------------------------
        reg(B.pointed_dripstone, (world, x, y, z, c) =>
        {
            bool pointingDown = (c & 1) != 0;
            if (!pointingDown) { return; }
            // find water above the ceiling block
            int ceilY = y;
            while ((world.getBlock(x, ceilY, z) >> 4) == (int)B.pointed_dripstone) { ceilY += 1; }
            int aboveCeil = world.getBlock(x, ceilY + 1, z) >> 4;
            string fluid = aboveCeil == (int)B.water ? "water" : aboveCeil == (int)B.lava ? "lava" : null;
            if (farmingRng.nextFloat() < 0.12)
            {
                world.hooks.addParticles(fluid == "lava" ? "drip_lava" : "drip_water", (double)x + 0.5, (double)y - 0.3, (double)z + 0.5, 1, 0.05, 0);
            }
            if (fluid == null) { return; }
            // drip into cauldron below
            if (farmingRng.nextFloat() < 0.06)
            {
                for (int dy = 1; dy < 11; dy++)
                {
                    int below = world.getBlock(x, y - dy, z);
                    int bidv = below >> 4;
                    if (bidv == 0) { continue; }
                    if (bidv == (int)B.cauldron)
                    {
                        int level = below & 3;
                        int kind = (below >> 2) & 3;
                        if (fluid == "water" && (level == 0 || kind == 0) && level < 3)
                        {
                            world.setBlock(x, y - dy, z, (int)cell(B.cauldron, (level + 1) | (0 << 2)));
                        }
                        else if (fluid == "lava" && level == 0)
                        {
                            world.setBlock(x, y - dy, z, (int)cell(B.cauldron, 3 | (1 << 2)));
                        }
                    }
                    break;
                }
            }
            // grow longer (rare, water only)
            if (fluid == "water" && farmingRng.nextFloat() < 0.011)
            {
                int tip = world.getBlock(x, y - 1, z);
                if (tip == 0)
                {
                    world.setBlock(x, y, z, (int)cell(B.pointed_dripstone, 1 | (1 << 1)));
                    world.setBlock(x, y - 1, z, (int)cell(B.pointed_dripstone, 1 | (0 << 1)));
                }
            }
        });

        // chorus flower growth ----------------------------------------------------------
        reg(B.chorus_flower, (world, x, y, z, c) =>
        {
            int age = c & 7;
            if (age >= 5) { return; }
            int above = world.getBlock(x, y + 1, z);
            if (above == 0 && farmingRng.nextFloat() < 0.3)
            {
                // grow up or branch
                int height = 1;
                while ((world.getBlock(x, y - height, z) >> 4) == (int)B.chorus_plant) { height += 1; }
                if (height < 4 && farmingRng.nextFloat() < 0.7)
                {
                    world.setBlock(x, y, z, (int)cell(B.chorus_plant));
                    world.setBlock(x, y + 1, z, (int)cell(B.chorus_flower, age));
                }
                else
                {
                    // branch sideways
                    world.setBlock(x, y, z, (int)cell(B.chorus_plant));
                    bool branched = false;
                    for (int _i = 0; _i < 4; _i++)
                    {
                        int f = farmingRng.nextInt(4);
                        int dx = new[] { 1, -1, 0, 0 }[f], dz = new[] { 0, 0, 1, -1 }[f];
                        if (world.getBlock(x + dx, y, z + dz) == 0)
                        {
                            world.setBlock(x + dx, y, z + dz, (int)cell(B.chorus_flower, age + 1));
                            branched = true;
                        }
                    }
                    if (!branched) { world.setBlock(x, y, z, (int)cell(B.chorus_flower, 5)); }
                }
            }
        });

        // gravity blocks (scheduled by neighbor updates in World) -------------------------
        BlockTickFn fallTick = (world, x, y, z, c) =>
        {
            int below = world.getBlock(x, y - 1, z);
            int bidv = below >> 4;
            if (bidv == 0 || blockDefs[bidv].replaceable || bidv == (int)B.water || bidv == (int)B.lava || bidv == (int)B.fire)
            {
                world.setBlock(x, y, z, 0);
                var fb = new FallingBlockEntity(world);
                fb.setPos((double)x + 0.5, (double)y, (double)z + 0.5);
                fb.blockCell = c;
                world.addEntity(fb);
            }
        };
        var gravityBlocks = new[] { "sand", "red_sand", "gravel", "suspicious_sand", "suspicious_gravel", "anvil", "chipped_anvil", "damaged_anvil", "dragon_egg" };
        foreach (var grav in gravityBlocks)
        {
            blockTickHandlers[(int)bid(grav)] = fallTick;
        }
        foreach (var c in COLORS)
        {
            blockTickHandlers[(int)bid($"{c}_concrete_powder")] = fallTick;
        }
        NeighborFn fallTickSchedule = (world, x, y, z, c, _, _, _) =>
        {
            world.scheduleTick(x, y, z, c >> 4, 2);
        };
        // concrete powder + water = concrete (neighbor update)
        foreach (var col in COLORS)
        {
            ushort concrete = bid($"{col}_concrete");
            neighborHandlers[(int)bid($"{col}_concrete_powder")] = (world, x, y, z, c, nx, ny, nz) =>
            {
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    if ((world.getBlock(x + dx, y + dy, z + dz) >> 4) == (int)B.water)
                    {
                        world.setBlock(x, y, z, (int)cell(concrete));
                        return;
                    }
                }
                fallTickSchedule(world, x, y, z, c, nx, ny, nz);
            };
        }
        foreach (var grav in gravityBlocks)
        {
            neighborHandlers[(int)bid(grav)] = fallTickSchedule;
        }
    }

    // =============================================================================
    // SUPPORT POPS — vanilla neighbor-update behavior: plants/layers break (with
    // drops) when the block they stand on vanishes. Cascades up columns
    // (sugarcane/bamboo/cactus/kelp) because each pop re-notifies its neighbors.
    // =============================================================================

    /// break a block from a neighbor update — no player, no tool, normal drops
    public static void popBlock(World world, int x, int y, int z)
    {
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        if (id == 0) { return; }
        var def = blockDefs[id];
        world.hooks.playSound("block." + def.sound + ".break", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.8, 1);
        world.hooks.addParticles("block", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 10, 0.4, c);
        world.setBlock(x, y, z, 0);
        if (!world.rule("doTileDrops")) { return; }
        var ctx = new DropCtx(random: () => gameRng.nextFloat(), fortune: 0, silkTouch: false, toolType: ToolType.none, toolTier: 0, shears: false);
        List<Drop> drops;
        if (def.drops != null)
        {
            drops = def.drops(c & 15, ctx);
        }
        else if (blockToItem[id] is int itemId0 && itemId0 >= 0)
        {
            drops = new List<Drop> { new Drop(itemDefs[itemId0].name) };
        }
        else
        {
            drops = new List<Drop>();
        }
        foreach (var d in drops)
        {
            var itemIdOpt = iidOpt(d.item);
            if (itemIdOpt == null) { continue; }
            int itemId = itemIdOpt.Value;
            int count = d.countMin;
            if (d.countMax > d.countMin) { count = d.countMin + gameRng.nextInt(d.countMax - d.countMin + 1); }
            if (d.chance != 1 && gameRng.nextFloat() > d.chance) { continue; }
            if (count > 0) { spawnItem(world, (double)x + 0.5, (double)y + 0.3, (double)z + 0.5, new ItemStack(itemId, count)); }
        }
    }

    private static bool isDirtish(int id)
    {
        return id == (int)B.grass_block || id == (int)B.dirt || id == (int)B.coarse_dirt
            || id == (int)B.podzol || id == (int)B.mycelium || id == (int)B.rooted_dirt
            || id == (int)B.moss_block || id == (int)B.mud || id == (int)B.farmland
            || id == (int)B.muddy_mangrove_roots;
    }
    private static bool isSandy(int id)
    {
        return id == (int)B.sand || id == (int)B.red_sand || id == (int)B.gravel
            || id == (int)B.suspicious_sand || id == (int)B.suspicious_gravel;
    }

    /// can this cell keep standing where it is?
    private static bool canSurvive(World world, int x, int y, int z, int c)
    {
        int id = c >> 4;
        int below = world.getBlock(x, y - 1, z) >> 4;
        if (id == (int)B.sugar_cane)
        {
            if (below == id) { return true; }
            if (!(isDirtish(below) || isSandy(below))) { return false; }
            // needs water (or frosted ice) next to the supporting block
            foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int n = world.getBlock(x + dx, y - 1, z + dz) >> 4;
                if (n == (int)B.water || n == (int)B.frosted_ice) { return true; }
            }
            return false;
        }
        if (id == (int)B.cactus)
        {
            if (!(below == id || isSandy(below))) { return false; }
            // no solid horizontal neighbors at the base
            foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int n = world.getBlock(x + dx, y, z + dz) >> 4;
                if (n != 0 && blockDefs[n].solid) { return false; }
            }
            return true;
        }
        if (id == (int)B.bamboo || id == (int)B.bamboo_sapling)
        {
            return below == (int)B.bamboo || below == (int)B.bamboo_sapling
                || isDirtish(below) || isSandy(below);
        }
        if (id == (int)B.kelp || id == (int)B.kelp_plant)
        {
            return below == (int)B.kelp || below == (int)B.kelp_plant
                || (below != 0 && blockDefs[below].solid);
        }
        if (id == (int)B.lily_pad || id == (int)B.frogspawn)
        {
            return below == (int)B.water || below == (int)B.ice || below == (int)B.frosted_ice;
        }
        Shape shape = (Shape)SHAPE_OF[id];
        if (shape == Shape.crop || id == (int)B.melon_stem || id == (int)B.pumpkin_stem)
        {
            return below == (int)B.farmland;
        }
        if (shape == Shape.layer || shape == Shape.carpet || shape == Shape.pressurePlate)
        {
            return below != 0 && blockDefs[below].solid;
        }
        // generic floor plants (flowers, saplings, grasses, mushrooms, roots…)
        return below != 0 && (blockDefs[below].solid || isDirtish(below));
    }

    public static void registerSupportPops()
    {
        NeighborFn popHandler = (world, x, y, z, c, _, fy, _) =>
        {
            // only support-relevant updates (below for everything; sides matter
            // for cactus, so it re-checks on any neighbor change)
            int id = c >> 4;
            if (fy != y - 1 && id != (int)B.cactus) { return; }
            if (!canSurvive(world, x, y, z, c))
            {
                popBlock(world, x, y, z);
            }
        };
        var floorShapes = new HashSet<Shape> { Shape.cross, Shape.crop, Shape.tallCross, Shape.rootsShape, Shape.netherWart,
                                               Shape.sweetBerry, Shape.bambooSapling, Shape.smallDripleafShape,
                                               Shape.pitcherCropShape, Shape.lilyPad, Shape.frogspawn, Shape.bamboo,
                                               Shape.layer, Shape.carpet, Shape.propagule };
        foreach (var def in blockDefs)
        {
            if (def.id == 0) { continue; }
            Shape shape = (Shape)SHAPE_OF[def.id];
            bool special = def.id == (int)B.cactus || def.id == (int)B.sugar_cane;
            if ((floorShapes.Contains(shape) || special) && !neighborHandlers.ContainsKey(def.id))
            {
                neighborHandlers[def.id] = popHandler;
            }
        }
    }
}

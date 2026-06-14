// Water & lava flow — Registered as scheduled-tick +
// neighbor handlers. Meta: bits0-2 level (0 = source, 1..7 flowing), bit3 = falling.
//
// Defines static class `FluidsGlobals` (surface via `global using static
// PebbleCore.FluidsGlobals`) holding the module-global free funcs/vars of
// Fluids.swift — the only public entrypoint is registerFluidHandlers().
// blockTickHandlers/neighborHandlers come from WorldHandlers; REPLACEABLE/cell
// from BlockCaches/BlockRegistry; blockDefs/B from Reg; World/B.* by name.

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class FluidsGlobals
{
    private static readonly int[] HDX = { 0, 0, -1, 1 };
    private static readonly int[] HDZ = { -1, 1, 0, 0 };

    private static int tickRate(World world, int fluidId)
    {
        if (fluidId == (int)B.water) { return 5; }
        return world.dim == Dim.nether ? 10 : 30;
    }
    private static int maxSpread(World world, int fluidId)
    {
        if (fluidId == (int)B.water) { return 7; }
        return world.dim == Dim.nether ? 7 : 3;
    }
    private static int levelStep(World world, int fluidId)
    {
        if (fluidId == (int)B.water) { return 1; }
        return world.dim == Dim.nether ? 1 : 2;
    }

    private static bool canReplace(World world, int x, int y, int z, int fluidId)
    {
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        if (id == 0) { return true; }
        if (id == fluidId) { return true; }
        if (id == (int)B.water || id == (int)B.lava) { return true; } // interaction handled separately
        var def = blockDefs[id];
        if (REPLACEABLE[id] == 1) { return true; }
        if (!def.solid && def.piston == PistonBehavior.destroy) { return true; }
        return false;
    }

    private static void destroyForFluid(World world, int x, int y, int z)
    {
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        if (id == 0 || id == (int)B.water || id == (int)B.lava) { return; }
        world.breakBlockNaturally(x, y, z);
    }

    /// effective fluid level at cell for compare: source=0 best; returns -1 if not this fluid
    private static int levelOf(int c, int fluidId)
    {
        if ((c >> 4) != fluidId) { return -1; }
        if ((c & 8) != 0) { return 0; } // falling acts like source for downward feed
        return c & 7;
    }

    private static bool lavaWaterContact(World world, int x, int y, int z, int lavaCell)
    {
        // returns true if converted
        for (int d = 0; d < 6; d++)
        {
            int nx = x + new[] { 0, 0, 0, 0, -1, 1 }[d];
            int ny = y + new[] { -1, 1, 0, 0, 0, 0 }[d];
            int nz = z + new[] { 0, 0, -1, 1, 0, 0 }[d];
            if (d == 0) { continue; } // water below lava doesn't convert the lava
            if ((world.getBlock(nx, ny, nz) >> 4) == (int)B.water)
            {
                bool isSource = (lavaCell & 15) == 0;
                world.setBlock(x, y, z, (int)cell(isSource ? B.obsidian : B.cobblestone));
                world.hooks.playSound("block.fire.extinguish", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.5, 1);
                world.hooks.addParticles("smoke", (double)x + 0.5, (double)y + 1, (double)z + 0.5, 8, 0.4, 0);
                return true;
            }
        }
        return false;
    }

    private static void fluidTick(World world, int x, int y, int z, int c)
    {
        int fluidId = c >> 4;
        int meta = c & 15;
        int level = meta & 7;
        bool falling = (meta & 8) != 0;
        int rate = tickRate(world, fluidId);
        int step = levelStep(world, fluidId);
        int maxS = maxSpread(world, fluidId);

        if (fluidId == (int)B.lava && lavaWaterContact(world, x, y, z, c)) { return; }

        // 1. verify this block is still fed (non-sources)
        if (level > 0 || falling)
        {
            bool fed = false;
            int above = world.getBlock(x, y + 1, z);
            if ((above >> 4) == fluidId) { fed = true; }
            if (!fed)
            {
                for (int d = 0; d < 4; d++)
                {
                    int n = world.getBlock(x + HDX[d], y, z + HDZ[d]);
                    int nl = levelOf(n, fluidId);
                    if (nl >= 0 && nl < level) { fed = true; break; }
                    if (nl == 0 && (n & 15) == 0) { fed = true; break; }
                }
            }
            if (!fed)
            {
                // decay
                int newLevel = level + step;
                if (newLevel > maxS || falling)
                {
                    world.setBlock(x, y, z, 0);
                }
                else
                {
                    world.setBlock(x, y, z, (int)cell((ushort)fluidId, newLevel));
                    world.scheduleTick(x, y, z, fluidId, rate);
                }
                return;
            }
            // falling block with no fluid above turns into spreading flow
            if (falling && (above >> 4) != fluidId)
            {
                world.setBlock(x, y, z, (int)cell((ushort)fluidId, Math.Min(maxS, 1)));
                world.scheduleTick(x, y, z, fluidId, rate);
                return;
            }
        }

        // 2. infinite water: 2+ adjacent sources over solid/source
        if (fluidId == (int)B.water && level > 0 && !falling)
        {
            int sources = 0;
            for (int d = 0; d < 4; d++)
            {
                int n = world.getBlock(x + HDX[d], y, z + HDZ[d]);
                if ((n >> 4) == (int)B.water && (n & 15) == 0) { sources += 1; }
            }
            if (sources >= 2)
            {
                int below2 = world.getBlock(x, y - 1, z);
                int belowId2 = below2 >> 4;
                if ((belowId2 != 0 && blockDefs[belowId2].solid) || ((below2 >> 4) == (int)B.water && (below2 & 15) == 0))
                {
                    world.setBlock(x, y, z, (int)cell(B.water, 0));
                }
            }
        }

        int selfCell = world.getBlock(x, y, z);
        if ((selfCell >> 4) != fluidId) { return; }
        int selfLevel = selfCell & 7;
        bool selfFalling = (selfCell & 8) != 0;

        // 3. flow down
        int below = world.getBlock(x, y - 1, z);
        int belowId = below >> 4;
        if (canReplace(world, x, y - 1, z, fluidId) && belowId != fluidId)
        {
            if (fluidId == (int)B.water && belowId == (int)B.lava)
            {
                bool isSrc = (below & 15) == 0;
                world.setBlock(x, y - 1, z, (int)cell(isSrc ? B.obsidian : B.cobblestone));
                world.hooks.playSound("block.fire.extinguish", (double)x + 0.5, (double)y - 0.5, (double)z + 0.5, 0.5, 1);
                return;
            }
            if (fluidId == (int)B.lava && belowId == (int)B.water)
            {
                world.setBlock(x, y - 1, z, (int)cell(B.stone));
                world.hooks.playSound("block.fire.extinguish", (double)x + 0.5, (double)y - 0.5, (double)z + 0.5, 0.5, 1);
                return;
            }
            if (belowId != 0 && belowId != fluidId) { destroyForFluid(world, x, y - 1, z); }
            world.setBlock(x, y - 1, z, (int)cell((ushort)fluidId, 8 | 1));
            world.scheduleTick(x, y - 1, z, fluidId, rate);
            return; // water prefers falling; doesn't spread sideways while over a hole
        }
        if (belowId == fluidId)
        {
            // keep feeding downward; sources also spread sideways
            if (selfLevel != 0 && !selfFalling) { return; }
        }

        // 4. spread horizontally (path-seek toward nearest drop within 4)
        int spreadLevel = selfFalling ? step : selfLevel + step;
        if (spreadLevel > maxS) { return; }
        int[] dists = { 99, 99, 99, 99 };
        int best = 99;
        for (int d = 0; d < 4; d++)
        {
            int nx = x + HDX[d], nz = z + HDZ[d];
            if (!canReplace(world, nx, y, nz, fluidId)) { continue; }
            dists[d] = dropDistance(world, nx, y, nz, fluidId, 1);
            if (dists[d] < best) { best = dists[d]; }
        }
        for (int d = 0; d < 4; d++)
        {
            if (dists[d] != best) { continue; }
            int nx = x + HDX[d], nz = z + HDZ[d];
            if (!canReplace(world, nx, y, nz, fluidId)) { continue; }
            int ncell = world.getBlock(nx, y, nz);
            int nid = ncell >> 4;
            if (nid == fluidId)
            {
                int nl = ncell & 7;
                if ((ncell & 8) == 0 && nl <= spreadLevel) { continue; } // already as strong
            }
            if (fluidId == (int)B.water && nid == (int)B.lava)
            {
                world.setBlock(nx, y, nz, (int)cell((ncell & 15) == 0 ? B.obsidian : B.cobblestone));
                continue;
            }
            if (fluidId == (int)B.lava && nid == (int)B.water)
            {
                world.setBlock(nx, y, nz, (int)cell(B.cobblestone));
                continue;
            }
            if (nid != 0 && nid != fluidId) { destroyForFluid(world, nx, y, nz); }
            world.setBlock(nx, y, nz, (int)cell((ushort)fluidId, spreadLevel));
            world.scheduleTick(nx, y, nz, fluidId, rate);
        }
    }

    /// distance (1..4) to nearest hole the fluid could fall into; 99 if none
    private static int dropDistance(World world, int x, int y, int z, int fluidId, int depth)
    {
        if (canReplace(world, x, y - 1, z, fluidId) && (world.getBlock(x, y - 1, z) >> 4) != fluidId) { return depth; }
        if (depth >= 4) { return 99; }
        int best = 99;
        for (int d = 0; d < 4; d++)
        {
            int nx = x + HDX[d], nz = z + HDZ[d];
            if (!canReplace(world, nx, y, nz, fluidId)) { continue; }
            int r = dropDistance(world, nx, y, nz, fluidId, depth + 1);
            if (r < best) { best = r; }
        }
        return best;
    }

    private static void scheduleFluid(World world, int x, int y, int z, int c)
    {
        world.scheduleTick(x, y, z, c >> 4, tickRate(world, c >> 4));
    }

    private static bool fluidsRegistered = false;
    public static void registerFluidHandlers()
    {
        if (fluidsRegistered) { return; }
        fluidsRegistered = true;
        blockTickHandlers[(int)B.water] = fluidTick;
        blockTickHandlers[(int)B.lava] = fluidTick;
        neighborHandlers[(int)B.water] = (w, x, y, z, c, _, _, _) => scheduleFluid(w, x, y, z, c);
        neighborHandlers[(int)B.lava] = (w, x, y, z, c, _, _, _) => scheduleFluid(w, x, y, z, c);
    }
}

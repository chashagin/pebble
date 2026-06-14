// Portals — nether portal frame validation +
// ignition, portal search/creation on the far side, end portal travel
// positions, end exit/gateway activation.
//
// Defines static class: PortalsGlobals (surface via `global using static PebbleCore.PortalsGlobals`).

namespace PebbleCore;

internal struct Frame
{
    public int x0, y0, w, h, zc, xc;
    public Frame(int x0, int y0, int w, int h, int zc, int xc)
    {
        this.x0 = x0; this.y0 = y0; this.w = w; this.h = h; this.zc = zc; this.xc = xc;
    }
}

public static class PortalsGlobals
{
    /// try to ignite a nether portal with fire placed at (x,y,z) inside a frame
    public static bool tryIgnitePortal(World world, int x, int y, int z)
    {
        foreach (int axis in new[] { 0, 1 })
        {
            var frameOpt = findPortalFrame(world, x, y, z, axis);
            if (frameOpt != null)
            {
                var frame = frameOpt.Value;
                for (int dy = 0; dy < frame.h; dy++)
                {
                    for (int dw = 0; dw < frame.w; dw++)
                    {
                        if (axis == 0) { world.setBlock(frame.x0 + dw, frame.y0 + dy, frame.zc, (int)cell(B.nether_portal, 0)); }
                        else { world.setBlock(frame.xc, frame.y0 + dy, frame.x0 + dw, (int)cell(B.nether_portal, 1)); }
                    }
                }
                world.hooks.playSound("block.portal.trigger", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                return true;
            }
        }
        return false;
    }

    private static Frame? findPortalFrame(World world, int x, int y, int z, int axis)
    {
        bool isObsidian(int bx, int by, int bz)
        {
            return (world.getBlock(bx, by, bz) >> 4) == (int)B.obsidian;
        }
        bool isInterior(int bx, int by, int bz)
        {
            int c = world.getBlock(bx, by, bz) >> 4;
            return c == 0 || c == (int)B.fire || c == (int)B.nether_portal;
        }
        // descend
        int by2 = y;
        while (isInterior(x, by2 - 1, z) && by2 > world.info.minY + 1) { by2 -= 1; }
        if (!isObsidian(x, by2 - 1, z)) { return null; }
        // find left edge
        int start = axis == 0 ? x : z;
        int probe = start;
        for (int _i = 0; _i < 22; _i++)
        {
            int px = axis == 0 ? probe - 1 : x;
            int pz = axis == 0 ? z : probe - 1;
            if (isInterior(px, by2, pz)) { probe -= 1; }
            else { break; }
        }
        int leftX = axis == 0 ? probe : x;
        int leftZ = axis == 0 ? z : probe;
        if (!isObsidian(axis == 0 ? leftX - 1 : x, by2, axis == 0 ? z : leftZ - 1)) { return null; }
        // width
        int w = 0;
        while (w < 21)
        {
            int px = axis == 0 ? leftX + w : x;
            int pz = axis == 0 ? z : leftZ + w;
            if (isInterior(px, by2, pz))
            {
                if (!isObsidian(px, by2 - 1, pz)) { return null; }
                w += 1;
            }
            else { break; }
        }
        if (w < 2 || w > 21) { return null; }
        if (!isObsidian(axis == 0 ? leftX + w : x, by2, axis == 0 ? z : leftZ + w)) { return null; }
        // height
        int h = 0;
        bool brokeOuter = false;
        while (h < 21)
        {
            for (int dw = 0; dw < w; dw++)
            {
                int px = axis == 0 ? leftX + dw : x;
                int pz = axis == 0 ? z : leftZ + dw;
                if (!isInterior(px, by2 + h, pz)) { brokeOuter = true; break; }
                // side rails
                if (dw == 0 && !isObsidian(axis == 0 ? leftX - 1 : x, by2 + h, axis == 0 ? z : leftZ - 1)) { brokeOuter = true; break; }
                if (dw == w - 1 && !isObsidian(axis == 0 ? leftX + w : x, by2 + h, axis == 0 ? z : leftZ + w)) { brokeOuter = true; break; }
            }
            if (brokeOuter) { break; }
            h += 1;
        }
        if (h < 3 || h > 21) { return null; }
        // top rail
        for (int dw = 0; dw < w; dw++)
        {
            int px = axis == 0 ? leftX + dw : x;
            int pz = axis == 0 ? z : leftZ + dw;
            if (!isObsidian(px, by2 + h, pz)) { return null; }
        }
        return new Frame(x0: axis == 0 ? leftX : leftZ, y0: by2, w: w, h: h, zc: z, xc: x);
    }

    /// find or build a portal near target coords in destination world; returns spawn pos
    public static (double, double, double) findOrCreatePortal(World dest, double x, double y, double z)
    {
        var existing = dest.findPortalNear(ifloor(x), ifloor(y), ifloor(z), 8, (int)B.nether_portal);
        if (existing != null)
        {
            return ((double)existing.Value.Item1 + 0.5, (double)existing.Value.Item2, (double)existing.Value.Item3 + 0.5);
        }
        // build a fresh portal on a platform
        int px = ifloor(x), pz = ifloor(z);
        int py = Math.Max(dest.info.minY + 16, Math.Min(dest.info.minY + dest.info.height - 24, dest.surfaceY(px, pz)));
        if (dest.dim == Dim.nether)
        {
            // find an air pocket
            py = 32;
            for (int ty = 36; ty < 96; ty++)
            {
                if (dest.getBlock(px, ty, pz) == 0 && dest.getBlock(px, ty + 1, pz) == 0 && dest.getBlock(px, ty + 2, pz) == 0)
                {
                    py = ty;
                    break;
                }
            }
        }
        // platform + frame (axis X)
        for (int dz = -1; dz <= 2; dz++)
        {
            for (int dx = -1; dx <= 3; dx++)
            {
                dest.setBlock(px + dx, py - 1, pz + dz, (int)cell(B.obsidian));
                for (int dy = 0; dy < 5; dy++)
                {
                    if (dz == 0) { continue; }
                    dest.setBlock(px + dx, py + dy, pz + dz, 0);
                }
            }
        }
        for (int dy = 0; dy < 5; dy++)
        {
            for (int dx = 0; dx <= 3; dx++)
            {
                bool isFrame = dy == 0 || dy == 4 || dx == 0 || dx == 3;
                dest.setBlock(px + dx, py + dy, pz, isFrame ? (int)cell(B.obsidian) : (int)cell(B.nether_portal, 0));
            }
        }
        return ((double)px + 1.5, (double)(py + 1), (double)pz + 0.5);
    }

    /// activate the End exit portal (after dragon death)
    public static void activateEndPortal(World world)
    {
        // the fountain (gen/end) is solid bedrock at py=62; the inner ring becomes
        // portal surface, the d=4 rim and corner cells stay bedrock
        int py = 62;
        for (int dz = -3; dz <= 3; dz++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                int d = Math.Abs(dx) + Math.Abs(dz);
                if (d >= 1 && d <= 3 && !(Math.Abs(dx) == 2 && Math.Abs(dz) == 2))
                {
                    world.setBlock(dx, py, dz, (int)cell(B.end_portal));
                }
            }
        }
        // dragon egg rests on the bedrock pillar top, replacing the torch
        world.setBlock(0, py + 4, 0, (int)cell(B.dragon_egg));
    }

    /// spawn an end gateway after a dragon kill
    public static void spawnEndGateway(World world, int index)
    {
        double ang = (double)index / 20 * Math.PI * 2;
        int gx = (int)detRound(detCos(ang) * 96);
        int gz = (int)detRound(detSin(ang) * 96);
        int gy = 75;
        // bedrock shell with gateway core
        foreach (var (dx, dy, dz) in new[] { (0, 1, 0), (0, -1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
        {
            world.setBlock(gx + dx, gy + dy, gz + dz, (int)cell(B.bedrock));
        }
        world.setBlock(gx, gy, gz, (int)cell(B.end_gateway));
        var be = new BlockEntityData(type: "end_gateway", x: gx, y: gy, z: gz);
        be.exitX = gx * 12;
        be.exitY = 70;
        be.exitZ = gz * 12;
        be.exactTeleport = false;
        world.setBlockEntity(be);
    }

    /// obsidian platform in the End for arriving players
    public static (double, double, double) buildEndSpawnPlatform(World world)
    {
        int px = 100, py = 48, pz = 0;
        for (int dz = -2; dz <= 2; dz++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                world.setBlock(px + dx, py, pz + dz, (int)cell(B.obsidian));
                for (int dy = 1; dy <= 3; dy++) { world.setBlock(px + dx, py + dy, pz + dz, 0); }
            }
        }
        return ((double)px + 0.5, (double)(py + 1), (double)pz + 0.5);
    }
}

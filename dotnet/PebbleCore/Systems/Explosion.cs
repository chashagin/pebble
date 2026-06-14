// Explosion engine — ray-based block
// destruction with blast resistance, entity damage + knockback, item drops,
// chain TNT ignition. All state randomness draws from gameRng in baseline order.
//
// Defines static class: ExplosionGlobals (surface via
//   global using static PebbleCore.ExplosionGlobals
// so the free functions explode/registerExplosionHandler resolve unqualified).
//
// All cross-layer symbols (gameRng, ifloor, detHyp3, blockDefs, B, DropCtx,
// iidOpt, spawnItem, ItemStack, TNTEntity, bindExplode, World, Entity, …)
// resolve from the rest of the assembly exactly as in Swift.

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class ExplosionGlobals
{
    public static void explode(World world, double x, double y, double z, double power, bool fire, Entity source)
    {
        world.hooks.playSound("entity.generic.explode", x, y, z, 4, (1 + (System.Random.Shared.NextDouble() - System.Random.Shared.NextDouble()) * 0.2) * 0.7);
        world.hooks.addParticles("explosion", x, y, z, (int)Math.Min(40, power * 8), power * 0.6, 0);

        // --- block destruction: 16×16 rays from center ---
        var destroyedList = new List<(int, int, int)>();
        var destroyedSet = new HashSet<long>();
        void addDestroyed(int bx, int by, int bz)
        {
            long key = ((long)bx << 40) ^ ((long)by << 20) ^ (long)bz;
            if (destroyedSet.Add(key))
            {
                destroyedList.Add((bx, by, bz));
            }
        }
        if (world.rule("mobGriefing") || source?.type == "tnt")
        {
            for (int rx = 0; rx < 16; rx++)
            {
                for (int ry = 0; ry < 16; ry++)
                {
                    for (int rz = 0; rz < 16; rz++)
                    {
                        if (rx != 0 && rx != 15 && ry != 0 && ry != 15 && rz != 0 && rz != 15) { continue; }
                        double dx = (double)rx / 15 * 2 - 1;
                        double dy = (double)ry / 15 * 2 - 1;
                        double dz = (double)rz / 15 * 2 - 1;
                        double len = detHyp3(dx, dy, dz);
                        dx /= len; dy /= len; dz /= len;
                        double intensity = power * (0.7 + gameRng.nextFloat() * 0.6);
                        double px = x, py = y, pz = z;
                        while (intensity > 0)
                        {
                            int bx = ifloor(px), by = ifloor(py), bz = ifloor(pz);
                            int c = world.getBlock(bx, by, bz);
                            int bid = c >> 4;
                            if (bid != 0)
                            {
                                double res = blockDefs[bid].resistance;
                                if (blockDefs[bid].hardness < 0) { break; } // unbreakable
                                intensity -= (res + 0.3) * 0.3;
                                if (intensity > 0) { addDestroyed(bx, by, bz); }
                            }
                            intensity -= 0.225;
                            px += dx * 0.3; py += dy * 0.3; pz += dz * 0.3;
                        }
                    }
                }
            }
        }

        // --- entity damage ---
        double radius = power * 2;
        foreach (var e in world.getEntitiesNear(x, y, z, radius))
        {
            if (!(e is Entity ent)) { continue; }
            double dist = detHyp3(ent.x - x, ent.y + ent.height / 2 - y, ent.z - z);
            if (dist > radius) { continue; }
            double exposure = 1 - dist / radius; // (skip expensive exposure rays)
            double dmg = Math.Floor((exposure * exposure + exposure) / 2 * 7 * power + 1);
            ent.hurt(dmg, "explosion", source);
            double dx2 = ent.x - x, dy2 = (ent.y + ent.height / 2) - y, dz2 = ent.z - z;
            double d2 = detHyp3(dx2, dy2, dz2);
            if (d2 == 0) { d2 = 1; }
            double kb = exposure * 1.2;
            ent.vx += dx2 / d2 * kb;
            ent.vy += dy2 / d2 * kb + 0.1;
            ent.vz += dz2 / d2 * kb;
        }

        // --- apply destruction ---
        foreach (var (bx, by, bz) in destroyedList)
        {
            int c = world.getBlock(bx, by, bz);
            int bid = c >> 4;
            if (bid == 0) { continue; }
            if (bid == (int)B.tnt)
            {
                // chain ignite
                world.setBlock(bx, by, bz, 0);
                var tnt = new TNTEntity(world);
                tnt.setPos((double)bx + 0.5, (double)by, (double)bz + 0.5);
                tnt.fuse = 10 + gameRng.nextInt(20);
                world.addEntity(tnt);
                continue;
            }
            world.setBlock(bx, by, bz, 0);
            // drop with 1/power chance
            if (world.rule("doTileDrops") && gameRng.nextFloat() < 1 / power)
            {
                var ctx = new DropCtx(fortune: 0, silkTouch: false, toolType: ToolType.none, toolTier: 0, shears: false,
                                      random: () => gameRng.nextFloat());
                var drops = blockDefs[bid].drops?.Invoke(c & 15, ctx);
                if (drops != null)
                {
                    foreach (var d in drops)
                    {
                        if (iidOpt(d.item) is int itemId)
                        {
                            int count = d.countMin == d.countMax
                                ? d.countMin
                                : d.countMin + gameRng.nextInt(d.countMax - d.countMin + 1);
                            spawnItem(world, (double)bx + 0.5, (double)by + 0.5, (double)bz + 0.5, new ItemStack(itemId, count));
                        }
                    }
                }
            }
        }
        // fire
        if (fire)
        {
            foreach (var (bx, by, bz) in destroyedList)
            {
                if (gameRng.nextFloat() < 0.33 && world.getBlock(bx, by, bz) == 0 && (world.getBlock(bx, by - 1, bz) >> 4) != 0)
                {
                    world.setBlock(bx, by, bz, (int)B.fire << 4);
                }
            }
        }
    }

    public static void registerExplosionHandler()
    {
        bindExplode(explode);
    }
}

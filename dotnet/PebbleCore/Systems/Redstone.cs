// Redstone — power model, wire networks, torches
// (with burnout), repeaters (delay + locking), comparators (compare/subtract +
// container reading), pistons (incl. sticky + slime/honey push sets +
// quasi-connectivity), observers, dispensers/droppers, note blocks, lamps,
// rails, doors, sensors, TNT — with vanilla-style scheduled tick timing.
//
// Uses its own seeded module RNG (0x4ED0) exactly like the frozen baseline.
//
// Ported from Sources/PebbleCore/Systems/Redstone.swift.
//
// Defines static class `RedstoneGlobals` (all module-level free funcs/vars:
//   redstoneRng, applyBonemealFn, wardenWarnings, emittedPower, strongPowerAt,
//   powerAt, wireConnectsDir, updateWireNetwork, containerSignal,
//   tickEntityTriggers, playNoteBlock, handleVibration, shriek,
//   registerRedstoneHandlers, bindBonemeal). Surface via
//   `global using static PebbleCore.RedstoneGlobals`.
//
// External symbols (defined elsewhere in the assembly, referenced by name):
//   World, Entity, EntityRef, LivingEntity, ItemStack, BlockEntityData, AABB    (World/Entity layers)
//   ifloor                                                                       (Entity/Entity)
//   spawnItem, spawnMob, SpawnOpts                                               (Entity layer)
//   igniteTNT                                                                    (Systems/Farming)
//   Projectile, ArrowEntity, ThrownSnowball, ThrownEgg, ThrownPotion,
//     Fireball, FireworkEntity, Sheep                                            (Entity layer)
//   itemDef, iid, maxStackOf                                                     (Items/ItemGlobals)
//   WOODS                                                                        (World/BlockData)
//   cell, blockDefs, B, bid                                                      (World/Reg + BlockCaches)
//   Dir, DIR_X/DIR_Y/DIR_Z/DIR_OPPOSITE                                          (Core/Dirs)
//   detRound                                                                     (Core/DetMath)
//   neighborHandlers/blockTickHandlers/onPlacedHandlers, NeighborFn             (World/WorldHandlers)

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public static class RedstoneGlobals
{
    public static RandomX redstoneRng = new RandomX(0x4ED0);

    private static readonly int[] FACING_DX = { 0, 0, -1, 1 }; // 0=N 1=S 2=W 3=E (horizontal facing in meta)
    private static readonly int[] FACING_DZ = { -1, 1, 0, 0 };

    private static List<int> WOOD_BUTTONS = new();
    private static List<int> WOOD_PLATES = new();

    /// position key for wire/piston flood fills
    private struct K : IEquatable<K>
    {
        public readonly int x, y, z;
        public K(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(K o) => x == o.x && y == o.y && z == o.z;
        public override bool Equals(object o) => o is K k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }

    // ---------------------------------------------------------------------------
    // Power queries
    // ---------------------------------------------------------------------------
    /// power a cell emits toward direction `dir` (pointing FROM the emitter TO the queried neighbor)
    public static int emittedPower(World world, int x, int y, int z, int dir)
    {
        int c = world.getBlock(x, y, z);
        int id = c >> 4, meta = c & 15;
        if (id == (int)B.redstone_block) { return 15; }
        if (id == (int)B.redstone_torch)
        {
            // emits 15 except toward its support block
            int attach = meta == 0 ? Dir.down : meta;
            return dir == attach ? 0 : 15;
        }
        if (id == (int)B.lever || id == (int)B.stone_button || id == (int)B.polished_blackstone_button)
        {
            return (meta & 8) != 0 ? 15 : 0;
        }
        foreach (var w in WOOD_BUTTONS) { if (id == w) { return (meta & 8) != 0 ? 15 : 0; } }
        if (id == (int)B.redstone_wire)
        {
            if (dir == Dir.up) { return 0; }
            if (dir == Dir.down) { return meta; }
            // emits to the sides its visual shape points at: connected sides, plus
            // the far end of a single-connection line. A bare dot powers no sides.
            int f = dirToFacing(dir);
            if (f < 0) { return 0; }
            int mask = 0, count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (wireConnectsDir(world, x, y, z, i)) { mask |= 1 << i; count += 1; }
            }
            if ((mask & (1 << f)) != 0) { return meta; }
            if (count == 1 && (mask & (1 << (f ^ 1))) != 0) { return meta; }
            return 0;
        }
        if (id == (int)B.repeater_on)
        {
            int facing = meta & 3;
            return dir == facingToDir(facing) ? 15 : 0;
        }
        if (id == (int)B.comparator_on || id == (int)B.comparator)
        {
            int facing = meta & 3;
            if (dir == facingToDir(facing))
            {
                return world.getBlockEntity(x, y, z)?.output ?? 0;
            }
            return 0;
        }
        if (id == (int)B.observer)
        {
            return (meta & 8) != 0 && dir == ((meta & 7) ^ 1) ? 15 : 0;
        }
        if (isPressurePlate(id)) { return (meta & 8) != 0 ? 15 : 0; }
        if (id == (int)B.detector_rail) { return (meta & 8) != 0 ? 15 : 0; }
        if (id == (int)B.daylight_detector || id == (int)B.daylight_detector_inverted) { return meta; }
        if (id == (int)B.target) { return meta; }
        if (id == (int)B.sculk_sensor || id == (int)B.calibrated_sculk_sensor) { return meta; }
        if (id == (int)B.tripwire_hook) { return (meta & 8) != 0 ? 15 : 0; }
        if (id == (int)B.lightning_rod) { return (meta & 8) != 0 ? 15 : 0; }
        if (id == (int)B.lectern) { return 0; }
        if (id == (int)B.trapped_chest)
        {
            // emits weak power scaled by viewers — approximated via BE flag
            var v = world.getBlockEntity(x, y, z)?.viewers;
            if (v != null && v > 0) { return Math.Min(15, v.Value); }
            return 0;
        }
        return 0;
    }

    /// strong power INTO a solid block from a component attached/facing it
    private static int strongPowerInto(World world, int x, int y, int z, int dir)
    {
        int c = world.getBlock(x, y, z);
        int id = c >> 4, meta = c & 15;
        if (id == (int)B.redstone_torch) { return dir == Dir.up ? 15 : 0; } // powers block above
        if (id == (int)B.repeater_on)
        {
            return dir == facingToDir(meta & 3) ? 15 : 0;
        }
        if (id == (int)B.comparator_on)
        {
            if (dir == facingToDir(meta & 3))
            {
                return world.getBlockEntity(x, y, z)?.output ?? 0;
            }
            return 0;
        }
        if (id == (int)B.lever || isButton(id))
        {
            int attach = meta & 7;
            return dir == (attach ^ 1) ? ((meta & 8) != 0 ? 15 : 0) : 0;
        }
        if (isPressurePlate(id)) { return dir == Dir.down ? ((meta & 8) != 0 ? 15 : 0) : 0; }
        if (id == (int)B.detector_rail) { return dir == Dir.down ? ((meta & 8) != 0 ? 15 : 0) : 0; }
        if (id == (int)B.redstone_wire)
        {
            if (dir == Dir.down) { return meta; } // treat as weak (matches baseline comment)
            return 0;
        }
        if (id == (int)B.observer) { return (meta & 8) != 0 && dir == ((meta & 7) ^ 1) ? 15 : 0; }
        if (id == (int)B.tripwire_hook)
        {
            int facing = meta & 3;
            return ((meta & 8) != 0 && dir == facingToDir(facing)) ? 15 : 0;
        }
        return 0;
    }

    /// is this solid block strongly powered (can conduct to wires)
    public static int strongPowerAt(World world, int x, int y, int z)
    {
        int p = 0;
        for (int d = 0; d < 6; d++)
        {
            int nx = x + DIR_X[d], ny = y + DIR_Y[d], nz = z + DIR_Z[d];
            p = Math.Max(p, strongPowerInto(world, nx, ny, nz, DIR_OPPOSITE[d]));
            if (p >= 15) { return 15; }
        }
        return p;
    }

    /// power level received at a position (for components)
    public static int powerAt(World world, int x, int y, int z, bool ignoreWire = false)
    {
        int p = 0;
        for (int d = 0; d < 6; d++)
        {
            int nx = x + DIR_X[d], ny = y + DIR_Y[d], nz = z + DIR_Z[d];
            int nc = world.getBlock(nx, ny, nz);
            int nid = nc >> 4;
            if (ignoreWire && nid == (int)B.redstone_wire) { continue; }
            p = Math.Max(p, emittedPower(world, nx, ny, nz, DIR_OPPOSITE[d]));
            // conduction through solid block
            if (blockDefs[nid].opaque && blockDefs[nid].fullCube)
            {
                p = Math.Max(p, strongPowerAt(world, nx, ny, nz));
            }
            if (p >= 15) { return 15; }
        }
        return p;
    }

    private static readonly int[] FACING_DIRS = { Dir.north, Dir.south, Dir.west, Dir.east };
    private static int facingToDir(int facing)
    {
        return FACING_DIRS[facing];
    }
    private static int dirToFacing(int dir)
    {
        int idx = Array.IndexOf(FACING_DIRS, dir);
        return idx;
    }
    private static bool isButton(int id)
    {
        return id == (int)B.stone_button || id == (int)B.polished_blackstone_button || WOOD_BUTTONS.Contains(id);
    }
    private static bool isPressurePlate(int id)
    {
        return id == (int)B.stone_pressure_plate || id == (int)B.polished_blackstone_pressure_plate ||
            id == (int)B.light_weighted_pressure_plate || id == (int)B.heavy_weighted_pressure_plate ||
            WOOD_PLATES.Contains(id);
    }

    // ---------------------------------------------------------------------------
    // Wire networks
    // ---------------------------------------------------------------------------
    public static bool wireConnectsDir(World world, int x, int y, int z, int facing)
    {
        int dx = FACING_DX[facing], dz = FACING_DZ[facing];
        int n = world.getBlock(x + dx, y, z + dz);
        int nid = n >> 4;
        if (connectsToWire(nid, n & 15, facing)) { return true; }
        // up a block
        bool upBlocked = blockDefs[world.getBlock(x, y + 1, z) >> 4].opaque;
        if (!upBlocked && blockDefs[nid].opaque && (world.getBlock(x + dx, y + 1, z + dz) >> 4) == (int)B.redstone_wire) { return true; }
        // down a block
        if (!blockDefs[nid].opaque && (world.getBlock(x + dx, y - 1, z + dz) >> 4) == (int)B.redstone_wire) { return true; }
        return false;
    }
    private static bool connectsToWire(int id, int meta, int facingToward)
    {
        if (id == (int)B.redstone_wire || id == (int)B.redstone_torch || id == (int)B.redstone_torch_off ||
            id == (int)B.lever || isButton(id) || id == (int)B.redstone_block || id == (int)B.target ||
            id == (int)B.daylight_detector || id == (int)B.daylight_detector_inverted ||
            id == (int)B.sculk_sensor || id == (int)B.calibrated_sculk_sensor || id == (int)B.tripwire_hook ||
            isPressurePlate(id) || id == (int)B.detector_rail || id == (int)B.trapped_chest || id == (int)B.lightning_rod || id == (int)B.observer) { return true; }
        if (id == (int)B.repeater || id == (int)B.repeater_on || id == (int)B.comparator || id == (int)B.comparator_on)
        {
            int f = meta & 3;
            // connects on its input/output axis
            return (f <= 1) == (facingToward <= 1);
        }
        return false;
    }

    /// recompute the wire network containing (x,y,z)
    public static void updateWireNetwork(World world, int x, int y, int z)
    {
        // collect network (insertion-ordered like the baseline Map)
        var networkOrder = new List<K>();
        var networkSet = new HashSet<K>();
        var queue = new List<(int, int, int)> { (x, y, z) };
        if ((world.getBlock(x, y, z) >> 4) != (int)B.redstone_wire) { return; }
        var k0 = new K(x, y, z);
        networkOrder.Add(k0);
        networkSet.Add(k0);
        while (queue.Count != 0 && networkSet.Count < 1024)
        {
            var (qx, qy, qz) = queue[queue.Count - 1];
            queue.RemoveAt(queue.Count - 1);
            for (int f = 0; f < 4; f++)
            {
                int dx = FACING_DX[f], dz = FACING_DZ[f];
                foreach (var dy in new[] { 0, 1, -1 })
                {
                    int nx = qx + dx, ny = qy + dy, nz = qz + dz;
                    if ((world.getBlock(nx, ny, nz) >> 4) != (int)B.redstone_wire) { continue; }
                    if (dy == 1 && blockDefs[world.getBlock(qx, qy + 1, qz) >> 4].opaque) { continue; }
                    if (dy == -1 && blockDefs[world.getBlock(nx, ny + 1, nz) >> 4].opaque) { continue; }
                    var k = new K(nx, ny, nz);
                    if (!networkSet.Contains(k))
                    {
                        networkSet.Add(k);
                        networkOrder.Add(k);
                        queue.Add((nx, ny, nz));
                    }
                }
            }
        }
        // source power at each wire
        var levels = new Dictionary<K, int>();
        var bfs = new List<(K, int)>();
        foreach (var k in networkOrder)
        {
            int p = powerAt(world, k.x, k.y, k.z, true);
            levels[k] = p;
            if (p > 0) { bfs.Add((k, p)); }
        }
        // propagate decrementing
        while (bfs.Count != 0)
        {
            var (k, p) = bfs[bfs.Count - 1];
            bfs.RemoveAt(bfs.Count - 1);
            if ((levels.TryGetValue(k, out var lk) ? lk : 0) > p) { continue; }
            for (int f = 0; f < 4; f++)
            {
                int dx = FACING_DX[f], dz = FACING_DZ[f];
                foreach (var dy in new[] { 0, 1, -1 })
                {
                    var nk = new K(k.x + dx, k.y + dy, k.z + dz);
                    if (!networkSet.Contains(nk)) { continue; }
                    int np = p - 1;
                    if (np > (levels.TryGetValue(nk, out var lnk) ? lnk : 0))
                    {
                        levels[nk] = np;
                        bfs.Add((nk, np));
                    }
                }
            }
        }
        // apply
        foreach (var k in networkOrder)
        {
            int cur = world.getBlock(k.x, k.y, k.z);
            int newMeta = levels.TryGetValue(k, out var lv) ? lv : 0;
            if ((cur & 15) != newMeta)
            {
                world.setBlock(k.x, k.y, k.z, (int)cell(B.redstone_wire, newMeta), 1 | 4);
                // also update diagonal neighbors' components (block below wire)
                world.notifyBlock(k.x, k.y - 1, k.z, k.x, k.y, k.z);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Torch (with burnout)
    // ---------------------------------------------------------------------------
    private static Dictionary<long, List<int>> torchBurnout = new();
    private static long torchKey(int x, int y, int z)
    {
        return ((long)x << 40) ^ ((long)y << 20) ^ (long)z;
    }
    private static (int, int, int) torchSupportPos(int x, int y, int z, int meta)
    {
        if (meta == 0) { return (x, y - 1, z); }
        return (x + DIR_X[meta], y + DIR_Y[meta], z + DIR_Z[meta]);
    }
    private static bool torchShouldBeOff(World world, int x, int y, int z, int meta)
    {
        var (sx, sy, sz) = torchSupportPos(x, y, z, meta);
        // powered support → torch off
        int sc = world.getBlock(sx, sy, sz);
        if (blockDefs[sc >> 4].opaque)
        {
            if (strongPowerAt(world, sx, sy, sz) > 0) { return true; }
            // also direct emission into the support
            for (int d = 0; d < 6; d++)
            {
                int nx = sx + DIR_X[d], ny = sy + DIR_Y[d], nz = sz + DIR_Z[d];
                if (nx == x && ny == y && nz == z) { continue; }
                if (emittedPower(world, nx, ny, nz, DIR_OPPOSITE[d]) > 0) { return true; }
            }
        }
        return false;
    }
    private static void torchTick(World world, int x, int y, int z, int c)
    {
        int id = c >> 4, meta = c & 15;
        bool shouldOff = torchShouldBeOff(world, x, y, z, meta);
        bool isOn = id == (int)B.redstone_torch;
        if (isOn == !shouldOff) { return; }
        // burnout: 8 toggles in 60 ticks
        long k = torchKey(x, y, z);
        var hist = torchBurnout.TryGetValue(k, out var h0) ? h0 : new List<int>();
        int now = world.time;
        while (hist.Count != 0 && now - hist[0] > 60) { hist.RemoveAt(0); }
        if (shouldOff || hist.Count < 8)
        {
            hist.Add(now);
            torchBurnout[k] = hist;
            world.setBlock(x, y, z, (int)cell(shouldOff ? B.redstone_torch_off : B.redstone_torch, meta));
            if (hist.Count >= 8 && !shouldOff)
            {
                // burned out — re-light later
                world.scheduleTick(x, y, z, (int)B.redstone_torch_off, 160);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Repeater
    // ---------------------------------------------------------------------------
    private static int repeaterInputPower(World world, int x, int y, int z, int facing)
    {
        // input is BEHIND (opposite of facing)
        int dx = -FACING_DX[facing], dz = -FACING_DZ[facing];
        int nx = x + dx, nz = z + dz;
        int nc = world.getBlock(nx, y, nz);
        int nid = nc >> 4;
        int p = emittedPower(world, nx, y, nz, facingToDir(facing));
        if (nid == (int)B.redstone_wire) { p = Math.Max(p, nc & 15); }
        if (blockDefs[nid].opaque && blockDefs[nid].fullCube) { p = Math.Max(p, strongPowerAt(world, nx, y, nz)); }
        return p;
    }
    private static bool repeaterLocked(World world, int x, int y, int z, int facing)
    {
        // side repeaters/comparators pointing into this lock it
        foreach (var side in new[] { leftFacing(facing), rightFacing(facing) })
        {
            int dx = FACING_DX[side], dz = FACING_DZ[side];
            int nc = world.getBlock(x + dx, y, z + dz);
            int nid = nc >> 4;
            if ((nid == (int)B.repeater_on || nid == (int)B.comparator_on) && (nc & 3) == oppFacing(side)) { return true; }
        }
        return false;
    }
    private static readonly int[] LEFT_FACING = { 2, 3, 1, 0 };
    private static readonly int[] RIGHT_FACING = { 3, 2, 0, 1 };
    private static readonly int[] OPP_FACING = { 1, 0, 3, 2 };
    private static int leftFacing(int f) { return LEFT_FACING[f]; }
    private static int rightFacing(int f) { return RIGHT_FACING[f]; }
    private static int oppFacing(int f) { return OPP_FACING[f]; }

    private static void repeaterNeighbor(World world, int x, int y, int z, int c)
    {
        int id = c >> 4;
        if (!world.hasScheduledTick(x, y, z, id))
        {
            int meta = c & 15;
            int facing = meta & 3;
            if (repeaterLocked(world, x, y, z, facing)) { return; }
            bool powered = repeaterInputPower(world, x, y, z, facing) > 0;
            bool isOn = id == (int)B.repeater_on;
            if (powered != isOn)
            {
                int delay = ((meta >> 2) & 3) + 1;
                world.scheduleTick(x, y, z, id, delay * 2, isOn ? 0 : -1);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Comparator
    // ---------------------------------------------------------------------------
    public static int containerSignal(World world, int x, int y, int z)
    {
        var be = world.getBlockEntity(x, y, z);
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        if (id == (int)B.cake) { return (7 - (c & 7)) * 2; }
        if (id == (int)B.cauldron) { return c & 3; }
        if (id == (int)B.composter) { return Math.Min(8, c & 15); }
        if (id == (int)B.jukebox)
        {
            return be?.disc != null ? 15 : 0;
        }
        if (id == (int)B.chiseled_bookshelf)
        {
            var items0 = be?.items;
            if (items0 != null)
            {
                int n = 0;
                foreach (var s in items0) { if (s != null) { n += 1; } }
                return n > 0 ? Math.Min(15, n * 2 + 3) : 0;
            }
            return 0;
        }
        if (be == null) { return -1; }
        ItemStack[] items = null;
        if (be.type == "container" || be.type == "hopper" || be.type == "furnace" || be.type == "brewing")
        {
            items = be.items;
        }
        if (items == null) { return -1; }
        double filled = 0.0;
        bool any = false;
        foreach (var s in items)
        {
            if (s != null)
            {
                any = true;
                filled += (double)s.count / (double)maxStackOf(s);
            }
        }
        if (!any) { return 0; }
        return (int)Math.Floor(1 + (filled / (double)items.Length) * 14);
    }

    private static int comparatorRearSignal(World world, int x, int y, int z, int facing)
    {
        int dx = -FACING_DX[facing], dz = -FACING_DZ[facing];
        int nx = x + dx, nz = z + dz;
        int cs = containerSignal(world, nx, y, nz);
        if (cs >= 0) { return cs; }
        // block behind a solid block can be a container too (reading through)
        int nc = world.getBlock(nx, y, nz);
        if (blockDefs[nc >> 4].opaque)
        {
            int cs2 = containerSignal(world, nx + dx, y, nz + dz);
            if (cs2 >= 0) { return cs2; }
        }
        int wire = world.getBlock(nx, y, nz);
        int p = emittedPower(world, nx, y, nz, facingToDir(facing));
        if ((wire >> 4) == (int)B.redstone_wire) { p = Math.Max(p, wire & 15); }
        if (blockDefs[nc >> 4].opaque && blockDefs[nc >> 4].fullCube) { p = Math.Max(p, strongPowerAt(world, nx, y, nz)); }
        return p;
    }
    private static int comparatorSideSignal(World world, int x, int y, int z, int facing)
    {
        int p = 0;
        foreach (var side in new[] { leftFacing(facing), rightFacing(facing) })
        {
            int dx = FACING_DX[side], dz = FACING_DZ[side];
            int nc = world.getBlock(x + dx, y, z + dz);
            int nid = nc >> 4;
            if (nid == (int)B.redstone_wire) { p = Math.Max(p, nc & 15); }
            else if (nid == (int)B.repeater_on && (nc & 3) == oppFacing(side)) { p = Math.Max(p, 15); }
            else if (nid == (int)B.redstone_block) { p = Math.Max(p, 15); }
            else if (nid == (int)B.comparator_on && (nc & 3) == oppFacing(side))
            {
                p = Math.Max(p, world.getBlockEntity(x + dx, y, z + dz)?.output ?? 0);
            }
        }
        return p;
    }
    private static void comparatorTick(World world, int x, int y, int z, int c)
    {
        int meta = c & 15;
        int facing = meta & 3;
        bool subtract = (meta & 4) != 0;
        int rear = comparatorRearSignal(world, x, y, z, facing);
        int side = comparatorSideSignal(world, x, y, z, facing);
        int outv = subtract ? Math.Max(0, rear - side) : (rear >= side ? rear : 0);
        var be = world.getBlockEntity(x, y, z);
        if (be == null || be.type != "comparator")
        {
            be = new BlockEntityData("comparator", x, y, z);
            be.output = 0;
            world.setBlockEntity(be);
        }
        if (be.output != outv)
        {
            be.output = outv;
            ushort newId = outv > 0 ? B.comparator_on : B.comparator;
            if ((c >> 4) != (int)newId)
            {
                world.setBlock(x, y, z, (int)cell(newId, meta));
                world.setBlockEntity(be);
            }
            else
            {
                world.updateNeighbors(x, y, z);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Entity-driven triggers (plates, tripwire, detector rails) — called per tick
    // ---------------------------------------------------------------------------
    private static bool plateAccepts(int plateId, EntityRef e)
    {
        if (plateId == (int)B.stone_pressure_plate || plateId == (int)B.polished_blackstone_pressure_plate)
        {
            return e is LivingEntity; // mobs + players only
        }
        return true; // wood + weighted accept all
    }

    public static void tickEntityTriggers(World world)
    {
        foreach (var e in world.entities)
        {
            var ent = e as Entity;
            if (ent == null || ent.dead) { continue; }
            int bx = ifloor(ent.x), by = ifloor(ent.y), bz = ifloor(ent.z);
            int c = world.getBlock(bx, by, bz);
            int id = c >> 4;
            if (isPressurePlate(id) && plateAccepts(id, ent))
            {
                if ((c & 8) == 0)
                {
                    world.setBlock(bx, by, bz, (int)cell((ushort)id, (c & 7) | 8));
                    world.hooks.playSound("block.stone_pressure_plate.click_on", (double)bx + 0.5, (double)by, (double)bz + 0.5, 0.4, 0.8);
                    world.updateNeighbors(bx, by - 1, bz);
                }
                world.scheduleTick(bx, by, bz, id, 20);
            }
            else if (id == (int)B.tripwire)
            {
                if ((c & 8) == 0)
                {
                    world.setBlock(bx, by, bz, (int)cell(B.tripwire, (c & 7) | 8));
                    // find + power hooks at both ends
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        for (int i = 1; i <= 40; i++)
                        {
                            int tc = world.getBlock(bx + dx * i, by, bz + dz * i);
                            if ((tc >> 4) == (int)B.tripwire) { continue; }
                            if ((tc >> 4) == (int)B.tripwire_hook)
                            {
                                world.setBlock(bx + dx * i, by, bz + dz * i, (int)cell(B.tripwire_hook, (tc & 7) | 8));
                                world.scheduleTick(bx + dx * i, by, bz + dz * i, (int)B.tripwire_hook, 10);
                            }
                            break;
                        }
                    }
                    world.scheduleTick(bx, by, bz, (int)B.tripwire, 10);
                }
            }
            else if (ent.type == "minecart")
            {
                int rc = world.getBlock(bx, by, bz);
                if ((rc >> 4) == (int)B.detector_rail && (rc & 8) == 0)
                {
                    world.setBlock(bx, by, bz, (int)cell(B.detector_rail, (rc & 7) | 8));
                    world.updateNeighbors(bx, by, bz);
                    world.updateNeighbors(bx, by - 1, bz);
                    world.scheduleTick(bx, by, bz, (int)B.detector_rail, 20);
                }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Note block
    // ---------------------------------------------------------------------------
    public static void playNoteBlock(World world, int x, int y, int z)
    {
        if (world.getBlock(x, y + 1, z) != 0) { return; }
        int note = world.getBlockEntity(x, y, z)?.note ?? 0;
        int below = world.getBlock(x, y - 1, z) >> 4;
        string name = blockDefs[below].name;
        string instrument = "harp";
        if (name.Contains("wool")) { instrument = "guitar"; }
        else if (name.Contains("sand") || name.Contains("gravel")) { instrument = "snare"; }
        else if (name.Contains("glass") || name.Contains("sea_lantern")) { instrument = "hat"; }
        else if (name.Contains("stone") || name.Contains("netherrack") || name.Contains("obsidian") || name.Contains("quartz") || name.Contains("sandstone") || name.Contains("prismarine") || name.Contains("brick") || name.Contains("deepslate") || name.Contains("blackstone")) { instrument = "basedrum"; }
        else if (name.Contains("gold_block")) { instrument = "bell"; }
        else if (name.Contains("clay")) { instrument = "flute"; }
        else if (name.Contains("packed_ice")) { instrument = "chime"; }
        else if (name.Contains("bone_block")) { instrument = "xylophone"; }
        else if (name.Contains("iron_block")) { instrument = "iron_xylophone"; }
        else if (name.Contains("soul_sand")) { instrument = "cow_bell"; }
        else if (name.Contains("pumpkin")) { instrument = "didgeridoo"; }
        else if (name.Contains("emerald_block")) { instrument = "bit"; }
        else if (name.Contains("hay_block")) { instrument = "banjo"; }
        else if (name.Contains("glowstone")) { instrument = "pling"; }
        else if (name.Contains("planks") || name.Contains("log") || name.Contains("wood")) { instrument = "bass"; }
        world.hooks.playSound($"note.{instrument}.{note}", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
        world.hooks.addParticles("note", (double)x + 0.5, (double)y + 1.1, (double)z + 0.5, 1, 0, note);
        world.emitVibration((double)x, (double)y, (double)z, 10, null);
    }

    // ---------------------------------------------------------------------------
    // Dispenser / dropper
    // ---------------------------------------------------------------------------
    private static void dispense(World world, int x, int y, int z, int c, bool isDispenser)
    {
        var be = world.getBlockEntity(x, y, z);
        if (be == null || be.type != "container") { return; }
        var items = be.items ?? new ItemStack[0];
        // pick random non-empty slot
        var slots = new List<int>();
        for (int i = 0; i < items.Length; i++) { if (items[i] != null) { slots.Add(i); } }
        if (slots.Count == 0)
        {
            world.hooks.playSound("block.dispenser.fail", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1.2);
            return;
        }
        int slot = slots[redstoneRng.nextInt(slots.Count)];
        var stack = items[slot];
        int facing = c & 7;
        string name = itemDef(stack.id).name;

        if (isDispenser)
        {
            bool handled = dispenseBehavior(world, x, y, z, facing, stack, be, slot, name);
            if (handled)
            {
                world.hooks.playSound("block.dispenser.dispense", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                return;
            }
            items = be.items ?? items;
        }
        // default: spit the item out
        stack.count -= 1;
        if (stack.count <= 0) { items[slot] = null; }
        be.items = items;
        var one = stack.copy();
        one.count = 1;
        var item = spawnItem(world, (double)x + 0.5 + (double)DIR_X[facing] * 0.7, (double)y + 0.5 + (double)DIR_Y[facing] * 0.7, (double)z + 0.5 + (double)DIR_Z[facing] * 0.7, one);
        item.vx = (double)DIR_X[facing] * 0.2 + (redstoneRng.nextFloat() - 0.5) * 0.05;
        item.vy = (double)DIR_Y[facing] * 0.2 + 0.1;
        item.vz = (double)DIR_Z[facing] * 0.2 + (redstoneRng.nextFloat() - 0.5) * 0.05;
        world.hooks.playSound("block.dispenser.dispense", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
    }

    private static bool dispenseBehavior(World world, int x, int y, int z, int facing,
                                         ItemStack stack, BlockEntityData be, int slot, string name)
    {
        int fx = x + DIR_X[facing], fy = y + DIR_Y[facing], fz = z + DIR_Z[facing];
        void consume()
        {
            stack.count -= 1;
            if (stack.count <= 0)
            {
                var items = be.items ?? new ItemStack[0];
                items[slot] = null;
                be.items = items;
            }
        }
        void clearSlot()
        {
            var items = be.items ?? new ItemStack[0];
            items[slot] = null;
            be.items = items;
        }
        void replaceSlot(ItemStack s)
        {
            var items = be.items ?? new ItemStack[0];
            items[slot] = s;
            be.items = items;
        }
        void shoot(Projectile p, double power)
        {
            p.setPos((double)x + 0.5 + (double)DIR_X[facing] * 0.8, (double)y + 0.5 + (double)DIR_Y[facing] * 0.8, (double)z + 0.5 + (double)DIR_Z[facing] * 0.8);
            p.shoot((double)DIR_X[facing], (double)DIR_Y[facing] + 0.1, (double)DIR_Z[facing], power, 6);
            world.addEntity(p);
        }
        if (name == "arrow" || name == "spectral_arrow" || name == "tipped_arrow")
        {
            var a = new ArrowEntity(world);
            a.pickupable = true;
            a.spectral = name == "spectral_arrow";
            a.potionId = name == "tipped_arrow" ? stack.data.potion : null;
            shoot(a, 1.1);
            consume();
            return true;
        }
        if (name == "snowball") { shoot(new ThrownSnowball(world), 1.1); consume(); return true; }
        if (name == "egg") { shoot(new ThrownEgg(world), 1.1); consume(); return true; }
        if (name == "splash_potion" || name == "lingering_potion")
        {
            var p = new ThrownPotion(world);
            p.potionId = stack.data.potion ?? "water";
            p.lingering = name == "lingering_potion";
            shoot(p, 1.1);
            consume();
            return true;
        }
        if (name == "fire_charge")
        {
            var f = new Fireball(world);
            f.small = true;
            shoot(f, 1.0);
            consume();
            return true;
        }
        if (name == "tnt")
        {
            igniteTNT(world, fx, fy, fz);
            consume();
            return true;
        }
        if (name == "flint_and_steel")
        {
            if (world.getBlock(fx, fy, fz) == 0)
            {
                world.setBlock(fx, fy, fz, (int)cell(B.fire));
                stack.damage += 1;
                if (stack.damage >= 64) { clearSlot(); }
                return true;
            }
            return false;
        }
        if (name == "water_bucket" || name == "lava_bucket")
        {
            int target = world.getBlock(fx, fy, fz);
            if (target == 0 || blockDefs[target >> 4].replaceable)
            {
                world.setBlock(fx, fy, fz, (int)cell(name == "water_bucket" ? B.water : B.lava, 0));
                replaceSlot(new ItemStack(iid("bucket"), 1));
                return true;
            }
            return false;
        }
        if (name == "bucket")
        {
            int target = world.getBlock(fx, fy, fz);
            int tid = target >> 4;
            if ((tid == (int)B.water || tid == (int)B.lava) && (target & 15) == 0)
            {
                world.setBlock(fx, fy, fz, 0);
                replaceSlot(new ItemStack(iid(tid == (int)B.water ? "water_bucket" : "lava_bucket"), 1));
                return true;
            }
            return false;
        }
        if (name == "bone_meal")
        {
            bool ok = applyBonemealFn?.Invoke(world, fx, fy, fz) ?? false;
            if (ok) { consume(); }
            return ok;
        }
        if (name.EndsWith("_spawn_egg"))
        {
            string mob = name.Substring(0, name.Length - 10);
            _ = spawnMob(world, mob, (double)fx + 0.5, (double)fy, (double)fz + 0.5, new SpawnOpts());
            consume();
            return true;
        }
        if (name == "firework_rocket")
        {
            var fw = new FireworkEntity(world);
            fw.setPos((double)fx + 0.5, (double)fy + 0.5, (double)fz + 0.5);
            fw.vy = 0.5;
            world.addEntity(fw);
            consume();
            return true;
        }
        if (name == "shears")
        {
            // shear sheep in front
            foreach (var e in world.getEntitiesNear((double)fx + 0.5, (double)fy + 0.5, (double)fz + 0.5, 1.2, filter: e2 =>
            {
                var sheepF = e2 as Sheep;
                if (sheepF == null) { return false; }
                return !sheepF.sheared;
            }))
            {
                var sheep = e as Sheep;
                if (sheep == null) { continue; }
                sheep.sheared = true;
                // baseline: rng-in-loop-condition — rerolls every iteration check
                int i = 0;
                while (i < 1 + redstoneRng.nextInt(3))
                {
                    spawnItem(world, sheep.x, sheep.y + 0.5, sheep.z, new ItemStack(iid("white_wool"), 1));
                    i += 1;
                }
                stack.damage += 1;
                if (stack.damage >= 238) { clearSlot(); }
                return true;
            }
            return false;
        }
        return false;
    }

    public static Func<World, int, int, int, bool> applyBonemealFn = null;
    public static void bindBonemeal(Func<World, int, int, int, bool> fn) { applyBonemealFn = fn; }

    // ---------------------------------------------------------------------------
    // Powered rails (chain propagation)
    // ---------------------------------------------------------------------------
    private static bool railChainPowered(World world, int x, int y, int z, int railId, int depth)
    {
        if (powerAt(world, x, y, z) > 0) { return true; }
        if (depth >= 8) { return false; }
        // follow the rail line both ways
        int c = world.getBlock(x, y, z);
        int shape = c & 7;
        bool axisX = shape == 1 || shape == 2 || shape == 3;
        var dirs = axisX ? new[] { (1, 0), (-1, 0) } : new[] { (0, 1), (0, -1) };
        foreach (var (dx, dz) in dirs)
        {
            foreach (var dy in new[] { 0, 1, -1 })
            {
                int nc = world.getBlock(x + dx, y + dy, z + dz);
                if ((nc >> 4) == railId)
                {
                    if (powerAt(world, x + dx, y + dy, z + dz) > 0) { return true; }
                    if (depth < 8 && railChainPoweredShallow(world, x + dx, y + dy, z + dz, railId, depth + 1, dx, dz)) { return true; }
                    break;
                }
            }
        }
        return false;
    }
    private static bool railChainPoweredShallow(World world, int x, int y, int z, int railId, int depth, int dx, int dz)
    {
        if (powerAt(world, x, y, z) > 0) { return true; }
        if (depth >= 8) { return false; }
        foreach (var dy in new[] { 0, 1, -1 })
        {
            int nc = world.getBlock(x + dx, y + dy, z + dz);
            if ((nc >> 4) == railId)
            {
                return railChainPoweredShallow(world, x + dx, y + dy, z + dz, railId, depth + 1, dx, dz);
            }
        }
        return false;
    }

    // ---------------------------------------------------------------------------
    // Sculk sensors & shriekers
    // ---------------------------------------------------------------------------
    public static void handleVibration(World world, double x, double y, double z, int freq)
    {
        // find sensors within 8 blocks via chunk-tracked sets
        int cx0 = ifloor((x - 8) / 16), cx1 = ifloor((x + 8) / 16);
        int cz0 = ifloor((z - 8) / 16), cz1 = ifloor((z + 8) / 16);
        for (int cz = cz0; cz <= cz1; cz++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                var chunk = world.getChunk(cx, cz);
                if (chunk == null) { continue; }
                // Set iteration is hash-ordered — sort so multi-sensor activations
                // schedule their ticks in a deterministic order
                foreach (var idx in chunk.sculkSensors.OrderBy(v => v))
                {
                    var (sx, sy, sz) = chunk.idxToWorld(idx);
                    double ddx = (double)sx - x, ddy = (double)sy - y, ddz = (double)sz - z;
                    double dSq = ddx * ddx + ddy * ddy + ddz * ddz;
                    if (dSq > 64) { continue; }
                    int c = world.getBlock(sx, sy, sz);
                    int id = c >> 4;
                    if (id == (int)B.sculk_sensor || id == (int)B.calibrated_sculk_sensor)
                    {
                        if ((c & 15) > 0) { continue; } // cooldown
                        // calibrated filters by side input
                        if (id == (int)B.calibrated_sculk_sensor)
                        {
                            int inputPower = powerAt(world, sx, sy, sz);
                            if (inputPower > 0 && inputPower != freq) { continue; }
                        }
                        double dist = Math.Sqrt(dSq);
                        int power = Math.Max(1, Math.Min(15, (int)detRound((1 - dist / 8) * 15)));
                        world.setBlock(sx, sy, sz, (int)cell((ushort)id, power));
                        world.scheduleTick(sx, sy, sz, id, 40);
                        world.hooks.playSound("block.sculk_sensor.clicking", (double)sx + 0.5, (double)sy + 0.5, (double)sz + 0.5, 1, 1);
                        world.hooks.addParticles("sculk_soul", (double)sx + 0.5, (double)sy + 0.5, (double)sz + 0.5, 3, 0.3, 0);
                    }
                    else if (id == (int)B.sculk_shrieker)
                    {
                        shriek(world, sx, sy, sz);
                    }
                }
            }
        }
    }

    public static int wardenWarnings = 0;
    public static void shriek(World world, int x, int y, int z)
    {
        int c = world.getBlock(x, y, z);
        if ((c & 1) != 0) { return; } // already shrieking
        world.setBlock(x, y, z, (int)cell(B.sculk_shrieker, 1), 4);
        world.scheduleTick(x, y, z, (int)B.sculk_shrieker, 60);
        world.hooks.playSound("block.sculk_shrieker.shriek", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 3, 1);
        world.hooks.addParticles("sculk_soul", (double)x + 0.5, (double)y + 1, (double)z + 0.5, 10, 0.3, 0);
        if (world.getBlockEntity(x, y, z)?.canSummon == true)
        {
            wardenWarnings += 1;
            foreach (var p in world.getEntitiesNear((double)x, (double)y, (double)z, 16, filter: e => (e as Entity)?.isPlayer ?? false))
            {
                (p as LivingEntity)?.addEffect("darkness", 200, 0);
            }
            if (wardenWarnings >= 4)
            {
                wardenWarnings = 0;
                // summon warden if none nearby
                var wardens = world.getEntitiesNear((double)x, (double)y, (double)z, 48, filter: e => (e as Entity)?.type == "warden");
                if (wardens.Count == 0)
                {
                    _ = spawnMob(world, "warden", (double)x + 0.5, (double)y, (double)z + 0.5, new SpawnOpts());
                }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Pistons
    // ---------------------------------------------------------------------------
    private static bool pistonPowered(World world, int x, int y, int z, int facing)
    {
        for (int d = 0; d < 6; d++)
        {
            if (d == facing) { continue; }
            int nx = x + DIR_X[d], ny = y + DIR_Y[d], nz = z + DIR_Z[d];
            if (emittedPower(world, nx, ny, nz, DIR_OPPOSITE[d]) > 0) { return true; }
            int nc = world.getBlock(nx, ny, nz);
            if (blockDefs[nc >> 4].opaque && blockDefs[nc >> 4].fullCube && strongPowerAt(world, nx, ny, nz) > 0) { return true; }
        }
        // quasi-connectivity: check the block above (powered there counts)
        if (powerAt(world, x, y + 1, z) > 0) { return true; }
        return false;
    }

    private static List<(int, int, int)> gatherPushSet(World world, int x, int y, int z, int facing)
    {
        int dx = DIR_X[facing], dy = DIR_Y[facing], dz = DIR_Z[facing];
        var set = new List<(int, int, int)>();
        var seen = new HashSet<K>();
        var toCheck = new List<(int, int, int)> { (x + dx, y + dy, z + dz) };
        int head = 0;
        while (head < toCheck.Count)
        {
            var (px, py, pz) = toCheck[head];
            head += 1;
            var k = new K(px, py, pz);
            if (seen.Contains(k)) { continue; }
            int c = world.getBlock(px, py, pz);
            int id = c >> 4;
            if (id == 0 || blockDefs[id].replaceable) { continue; }
            var behavior = blockDefs[id].piston;
            if (behavior == PistonBehavior.destroy) { continue; } // destroyed on push, not part of set
            if (behavior == PistonBehavior.block || behavior == PistonBehavior.blockEntity || blockDefs[id].hardness < 0) { return null; }
            seen.Add(k);
            set.Add((px, py, pz));
            if (set.Count > 12) { return null; }
            // block in front must move too
            toCheck.Add((px + dx, py + dy, pz + dz));
            // slime/honey adhesion
            if (id == (int)B.slime_block || id == (int)B.honey_block)
            {
                for (int d = 0; d < 6; d++)
                {
                    int nx = px + DIR_X[d], ny = py + DIR_Y[d], nz = pz + DIR_Z[d];
                    int nc = world.getBlock(nx, ny, nz) >> 4;
                    if (nc == 0) { continue; }
                    // slime doesn't stick to honey
                    if ((id == (int)B.slime_block && nc == (int)B.honey_block) || (id == (int)B.honey_block && nc == (int)B.slime_block)) { continue; }
                    if (blockDefs[nc].piston == PistonBehavior.normal && !seen.Contains(new K(nx, ny, nz)))
                    {
                        toCheck.Add((nx, ny, nz));
                    }
                }
            }
        }
        return set;
    }

    private static void pistonTick(World world, int x, int y, int z, int c)
    {
        int id = c >> 4;
        int facing = c & 7;
        bool extended = (c & 8) != 0;
        bool powered = pistonPowered(world, x, y, z, facing);
        int dx = DIR_X[facing], dy = DIR_Y[facing], dz = DIR_Z[facing];
        if (powered && !extended)
        {
            // EXTEND
            var set = gatherPushSet(world, x, y, z, facing);
            if (set == null) { return; }
            // sort: move farthest first (stable mirror of deterministic sort on distance)
            var indexed = set.Select((element, offset) => (element, offset)).ToList();
            indexed.Sort((a, b) =>
            {
                int da = (a.element.Item1 - x) * dx + (a.element.Item2 - y) * dy + (a.element.Item3 - z) * dz;
                int db = (b.element.Item1 - x) * dx + (b.element.Item2 - y) * dy + (b.element.Item3 - z) * dz;
                if (da != db) { return da > db ? -1 : 1; }
                return a.offset < b.offset ? -1 : (a.offset > b.offset ? 1 : 0);
            });
            set = indexed.Select(t => t.element).ToList();
            foreach (var (px, py, pz) in set)
            {
                int destX = px + dx, destY = py + dy, destZ = pz + dz;
                int destC = world.getBlock(destX, destY, destZ);
                if (destC != 0 && blockDefs[destC >> 4].piston == PistonBehavior.destroy)
                {
                    world.breakBlockNaturally(destX, destY, destZ);
                }
                world.setBlock(destX, destY, destZ, world.getBlock(px, py, pz));
                world.setBlock(px, py, pz, 0);
            }
            // destroy block directly in front if Destroy-type and not moved
            int frontC = world.getBlock(x + dx, y + dy, z + dz);
            if (frontC != 0 && blockDefs[frontC >> 4].piston == PistonBehavior.destroy)
            {
                world.breakBlockNaturally(x + dx, y + dy, z + dz);
            }
            world.setBlock(x, y, z, (int)cell((ushort)id, facing | 8));
            world.setBlock(x + dx, y + dy, z + dz, (int)cell(B.piston_head, facing | (id == (int)B.sticky_piston ? 8 : 0)));
            world.hooks.playSound("block.piston.extend", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.6, 0.8);
            // push entities
            var box = new AABB((double)(x + dx), (double)(y + dy), (double)(z + dz), (double)(x + dx + 1), (double)(y + dy) + 1.5, (double)(z + dz + 1));
            foreach (var e in world.getEntitiesInBox(box))
            {
                var ent = e as Entity;
                if (ent == null) { continue; }
                ent.x += (double)dx * 1.01;
                ent.y += (double)dy * 1.01;
                ent.z += (double)dz * 1.01;
                if (dy > 0) { ent.vy = Math.Max(ent.vy, 0.3); }
            }
        }
        else if (!powered && extended)
        {
            // RETRACT
            world.setBlock(x + dx, y + dy, z + dz, 0); // remove head
            world.setBlock(x, y, z, (int)cell((ushort)id, facing));
            if (id == (int)B.sticky_piston)
            {
                // pull the block 2 ahead
                int px = x + dx * 2, py = y + dy * 2, pz = z + dz * 2;
                int pc = world.getBlock(px, py, pz);
                int pid = pc >> 4;
                if (pid != 0 && blockDefs[pid].piston == PistonBehavior.normal && blockDefs[pid].hardness >= 0)
                {
                    world.setBlock(x + dx, y + dy, z + dz, pc);
                    world.setBlock(px, py, pz, 0);
                }
            }
            world.hooks.playSound("block.piston.contract", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.6, 0.7);
        }
    }

    // ---------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------
    private static bool redstoneRegistered = false;

    public static void registerRedstoneHandlers()
    {
        if (redstoneRegistered) { return; }
        redstoneRegistered = true;

        WOOD_BUTTONS = WOODS.Select(w => (int)bid($"{w}_button")).ToList();
        WOOD_PLATES = WOODS.Select(w => (int)bid($"{w}_pressure_plate")).ToList();

        neighborHandlers[(int)B.redstone_wire] = (world, x, y, z, c, fx, fy, fz) =>
        {
            // support check
            int below = world.getBlock(x, y - 1, z);
            if (!blockDefs[below >> 4].fullCube)
            {
                world.breakBlockNaturally(x, y, z);
                return;
            }
            updateWireNetwork(world, x, y, z);
        };
        onPlacedHandlers[(int)B.redstone_wire] = (world, x, y, z, c) => updateWireNetwork(world, x, y, z);

        blockTickHandlers[(int)B.redstone_torch] = torchTick;
        blockTickHandlers[(int)B.redstone_torch_off] = torchTick;
        neighborHandlers[(int)B.redstone_torch] = (world, x, y, z, c, fx, fy, fz) =>
        {
            if (!world.hasScheduledTick(x, y, z, (int)B.redstone_torch)) { world.scheduleTick(x, y, z, (int)B.redstone_torch, 2); }
        };
        neighborHandlers[(int)B.redstone_torch_off] = (world, x, y, z, c, fx, fy, fz) =>
        {
            if (!world.hasScheduledTick(x, y, z, (int)B.redstone_torch_off)) { world.scheduleTick(x, y, z, (int)B.redstone_torch_off, 2); }
        };

        neighborHandlers[(int)B.repeater] = (world, x, y, z, c, fx, fy, fz) => repeaterNeighbor(world, x, y, z, c);
        neighborHandlers[(int)B.repeater_on] = (world, x, y, z, c, fx, fy, fz) => repeaterNeighbor(world, x, y, z, c);
        blockTickHandlers[(int)B.repeater] = (world, x, y, z, c) =>
        {
            int meta = c & 15;
            if (repeaterLocked(world, x, y, z, meta & 3)) { return; }
            if (repeaterInputPower(world, x, y, z, meta & 3) > 0)
            {
                world.setBlock(x, y, z, (int)cell(B.repeater_on, meta));
            }
        };
        blockTickHandlers[(int)B.repeater_on] = (world, x, y, z, c) =>
        {
            int meta = c & 15;
            if (repeaterLocked(world, x, y, z, meta & 3)) { return; }
            if (repeaterInputPower(world, x, y, z, meta & 3) == 0)
            {
                world.setBlock(x, y, z, (int)cell(B.repeater, meta));
            }
        };

        blockTickHandlers[(int)B.comparator] = comparatorTick;
        blockTickHandlers[(int)B.comparator_on] = comparatorTick;
        NeighborFn comparatorNeighbor = (world, x, y, z, c, fx, fy, fz) =>
        {
            if (!world.hasScheduledTick(x, y, z, c >> 4)) { world.scheduleTick(x, y, z, c >> 4, 2); }
        };
        neighborHandlers[(int)B.comparator] = comparatorNeighbor;
        neighborHandlers[(int)B.comparator_on] = comparatorNeighbor;

        neighborHandlers[(int)B.observer] = (world, x, y, z, c, fx, fy, fz) =>
        {
            int meta = c & 15;
            int facing = meta & 7;
            // only triggers when the WATCHED block (in facing dir) changed
            if (fx == x + DIR_X[facing] && fy == y + DIR_Y[facing] && fz == z + DIR_Z[facing])
            {
                if ((meta & 8) == 0 && !world.hasScheduledTick(x, y, z, (int)B.observer))
                {
                    world.scheduleTick(x, y, z, (int)B.observer, 2);
                }
            }
        };
        blockTickHandlers[(int)B.observer] = (world, x, y, z, c) =>
        {
            int meta = c & 15;
            if ((meta & 8) != 0)
            {
                world.setBlock(x, y, z, (int)cell(B.observer, meta & 7));
            }
            else
            {
                world.setBlock(x, y, z, (int)cell(B.observer, meta | 8));
                world.scheduleTick(x, y, z, (int)B.observer, 2);
            }
        };

        // buttons / plates ----------------------------------------------------------
        foreach (var btn in new[] { (int)B.stone_button, (int)B.polished_blackstone_button }.Concat(WOOD_BUTTONS))
        {
            int btnId = btn;
            blockTickHandlers[btnId] = (world, x, y, z, c) =>
            {
                // unpress
                if ((c & 8) != 0)
                {
                    world.setBlock(x, y, z, (int)cell((ushort)btnId, c & 7));
                    world.hooks.playSound("block.stone_button.click_off", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.5, 0.8);
                    int attach = c & 7;
                    world.updateNeighbors(x + DIR_X[attach], y + DIR_Y[attach], z + DIR_Z[attach]);
                }
            };
        }
        foreach (var plate in new[] { (int)B.stone_pressure_plate, (int)B.polished_blackstone_pressure_plate, (int)B.light_weighted_pressure_plate, (int)B.heavy_weighted_pressure_plate }.Concat(WOOD_PLATES))
        {
            int plateId = plate;
            blockTickHandlers[plateId] = (world, x, y, z, c) =>
            {
                // re-check entities; release if none
                var box = new AABB((double)x, (double)y, (double)z, (double)(x + 1), (double)y + 0.3, (double)(z + 1));
                var entities = world.getEntitiesInBox(box);
                var filtered = entities.Where(en => plateAccepts(plateId, en)).ToList();
                if (filtered.Count == 0)
                {
                    if ((c & 8) != 0)
                    {
                        world.setBlock(x, y, z, (int)cell((ushort)plateId, c & 7));
                        world.hooks.playSound("block.stone_pressure_plate.click_off", (double)x + 0.5, (double)y, (double)z + 0.5, 0.4, 0.7);
                        world.updateNeighbors(x, y - 1, z);
                    }
                }
                else
                {
                    world.scheduleTick(x, y, z, plateId, 20);
                }
            };
        }

        blockTickHandlers[(int)B.tripwire] = (world, x, y, z, c) =>
        {
            // release if no entity
            var box = new AABB((double)x, (double)y, (double)z, (double)(x + 1), (double)y + 0.5, (double)(z + 1));
            if (world.getEntitiesInBox(box).Count == 0)
            {
                world.setBlock(x, y, z, (int)cell(B.tripwire, c & 7));
            }
            else
            {
                world.scheduleTick(x, y, z, (int)B.tripwire, 10);
            }
        };
        blockTickHandlers[(int)B.tripwire_hook] = (world, x, y, z, c) =>
        {
            if ((c & 8) != 0)
            {
                world.setBlock(x, y, z, (int)cell(B.tripwire_hook, c & 7));
                int facing = c & 3;
                world.updateNeighbors(x - FACING_DX[facing], y, z - FACING_DZ[facing]);
            }
        };
        blockTickHandlers[(int)B.detector_rail] = (world, x, y, z, c) =>
        {
            var box = new AABB((double)x, (double)y, (double)z, (double)(x + 1), (double)(y + 1), (double)(z + 1));
            var carts = world.getEntitiesInBox(box, except: null, filter: en => (en as Entity)?.type == "minecart");
            if (carts.Count == 0 && (c & 8) != 0)
            {
                world.setBlock(x, y, z, (int)cell(B.detector_rail, c & 7));
                world.updateNeighbors(x, y, z);
                world.updateNeighbors(x, y - 1, z);
            }
            else if (carts.Count != 0)
            {
                world.scheduleTick(x, y, z, (int)B.detector_rail, 20);
            }
        };
        blockTickHandlers[(int)B.target] = (world, x, y, z, c) =>
        {
            if ((c & 15) != 0)
            {
                world.setBlock(x, y, z, (int)cell(B.target, 0));
            }
        };

        // lamp / doors / gates / hopper / TNT / note ----------------------------------
        neighborHandlers[(int)B.redstone_lamp] = (world, x, y, z, c, fx, fy, fz) =>
        {
            if (powerAt(world, x, y, z) > 0) { world.setBlock(x, y, z, (int)cell(B.redstone_lamp_on)); }
        };
        neighborHandlers[(int)B.redstone_lamp_on] = (world, x, y, z, c, fx, fy, fz) =>
        {
            if (powerAt(world, x, y, z) == 0 && !world.hasScheduledTick(x, y, z, (int)B.redstone_lamp_on))
            {
                world.scheduleTick(x, y, z, (int)B.redstone_lamp_on, 4);
            }
        };
        blockTickHandlers[(int)B.redstone_lamp_on] = (world, x, y, z, c) =>
        {
            if (powerAt(world, x, y, z) == 0) { world.setBlock(x, y, z, (int)cell(B.redstone_lamp)); }
        };

        // doors/trapdoors/gates respond to power TRANSITIONS, not absolute state —
        // an absolute check slams a hand-opened door shut on the very next
        // neighbor update (including the self-notify from opening it)
        var doors = WOODS.Select(w => (int)bid($"{w}_door")).Concat(new[] { (int)B.iron_door }).ToList();
        foreach (var door in doors)
        {
            int doorId = door;
            neighborHandlers[doorId] = (world, x, y, z, c, fx, fy, fz) =>
            {
                int meta = c & 15;
                bool isUpper = (meta & 8) != 0;
                int lowerY = isUpper ? y - 1 : y;
                bool powered = powerAt(world, x, lowerY, z) > 0 || powerAt(world, x, lowerY + 1, z) > 0;
                int lower = world.getBlock(x, lowerY, z);
                if ((lower >> 4) != doorId) { return; }
                var key = new OpenablePos(x, lowerY, z);
                bool wasPowered = world.poweredOpenables.Contains(key);
                if (powered != wasPowered)
                {
                    if (powered) { world.poweredOpenables.Add(key); } else { world.poweredOpenables.Remove(key); }
                    bool open = (lower & 4) != 0;
                    if (powered != open)
                    {
                        world.setBlock(x, lowerY, z, (int)cell((ushort)doorId, (lower & 15) ^ 4));
                        world.hooks.playSound(powered ? "block.wooden_door.open" : "block.wooden_door.close", (double)x + 0.5, (double)(lowerY + 1), (double)z + 0.5, 1, 1);
                    }
                }
                // support check
                int belowD = world.getBlock(x, lowerY - 1, z);
                if (!blockDefs[belowD >> 4].fullCube && !isUpper)
                {
                    world.breakBlockNaturally(x, lowerY, z);
                    world.setBlock(x, lowerY + 1, z, 0);
                }
            };
        }
        var trapdoors = WOODS.Select(w => (int)bid($"{w}_trapdoor")).Concat(new[] { (int)B.iron_trapdoor }).ToList();
        foreach (var td in trapdoors)
        {
            int tdId = td;
            neighborHandlers[tdId] = (world, x, y, z, c, fx, fy, fz) =>
            {
                bool powered = powerAt(world, x, y, z) > 0;
                var key = new OpenablePos(x, y, z);
                bool wasPowered = world.poweredOpenables.Contains(key);
                if (powered != wasPowered)
                {
                    if (powered) { world.poweredOpenables.Add(key); } else { world.poweredOpenables.Remove(key); }
                    bool open = (c & 4) != 0;
                    if (powered != open)
                    {
                        world.setBlock(x, y, z, (int)cell((ushort)tdId, (c & 15) ^ 4));
                    }
                }
            };
        }
        foreach (var gate in WOODS.Select(w => (int)bid($"{w}_fence_gate")).ToList())
        {
            int gateId = gate;
            neighborHandlers[gateId] = (world, x, y, z, c, fx, fy, fz) =>
            {
                bool powered = powerAt(world, x, y, z) > 0;
                var key = new OpenablePos(x, y, z);
                bool wasPowered = world.poweredOpenables.Contains(key);
                if (powered != wasPowered)
                {
                    if (powered) { world.poweredOpenables.Add(key); } else { world.poweredOpenables.Remove(key); }
                    bool open = (c & 4) != 0;
                    if (powered != open)
                    {
                        world.setBlock(x, y, z, (int)cell((ushort)gateId, (c & 15) ^ 4));
                        world.hooks.playSound(powered ? "block.fence_gate.open" : "block.fence_gate.close", (double)x + 0.5, (double)y, (double)z + 0.5, 1, 1);
                    }
                }
            };
        }
        neighborHandlers[(int)B.hopper] = (world, x, y, z, c, fx, fy, fz) =>
        {
            bool powered = powerAt(world, x, y, z) > 0;
            bool locked = (c & 8) != 0;
            if (powered != locked) { world.setBlock(x, y, z, (int)cell(B.hopper, (c & 7) | (powered ? 8 : 0)), 4); }
        };
        neighborHandlers[(int)B.tnt] = (world, x, y, z, c, fx, fy, fz) =>
        {
            if (powerAt(world, x, y, z) > 0) { igniteTNT(world, x, y, z); }
        };
        neighborHandlers[(int)B.note_block] = (world, x, y, z, c, fx, fy, fz) =>
        {
            bool powered = powerAt(world, x, y, z) > 0;
            bool was = (c & 1) != 0;
            if (powered && !was)
            {
                world.setBlock(x, y, z, (int)cell(B.note_block, 1), 4);
                playNoteBlock(world, x, y, z);
            }
            else if (!powered && was)
            {
                world.setBlock(x, y, z, (int)cell(B.note_block, 0), 4);
            }
        };

        // dispenser / dropper ---------------------------------------------------------
        NeighborFn dispenserNeighbor = (world, x, y, z, c, fx, fy, fz) =>
        {
            int id = c >> 4;
            // quasi-connectivity: also check block above
            bool powered = powerAt(world, x, y, z) > 0 || powerAt(world, x, y + 1, z) > 0;
            bool triggered = (c & 8) != 0;
            if (powered && !triggered)
            {
                world.scheduleTick(x, y, z, id, 4);
                world.setBlock(x, y, z, (int)cell((ushort)id, (c & 7) | 8), 4);
            }
            else if (!powered && triggered)
            {
                world.setBlock(x, y, z, (int)cell((ushort)id, c & 7), 4);
            }
        };
        neighborHandlers[(int)B.dispenser] = dispenserNeighbor;
        neighborHandlers[(int)B.dropper] = dispenserNeighbor;
        blockTickHandlers[(int)B.dispenser] = (world, x, y, z, c) => dispense(world, x, y, z, c, true);
        blockTickHandlers[(int)B.dropper] = (world, x, y, z, c) => dispense(world, x, y, z, c, false);

        // powered rails --------------------------------------------------------------
        foreach (var railId0 in new[] { (int)B.powered_rail, (int)B.activator_rail })
        {
            int railId = railId0;
            neighborHandlers[railId] = (world, x, y, z, c, fx, fy, fz) =>
            {
                // support check
                int below = world.getBlock(x, y - 1, z);
                if (!blockDefs[below >> 4].fullCube)
                {
                    world.breakBlockNaturally(x, y, z);
                    return;
                }
                bool powered = railChainPowered(world, x, y, z, railId, 0);
                bool was = (c & 8) != 0;
                if (powered != was)
                {
                    world.setBlock(x, y, z, (int)cell((ushort)railId, (c & 7) | (powered ? 8 : 0)), 1 | 4);
                }
            };
        }
        neighborHandlers[(int)B.rail] = (world, x, y, z, c, fx, fy, fz) =>
        {
            int below = world.getBlock(x, y - 1, z);
            if (!blockDefs[below >> 4].fullCube) { world.breakBlockNaturally(x, y, z); }
        };

        // sculk sensors / shrieker ------------------------------------------------------
        foreach (var sid0 in new[] { (int)B.sculk_sensor, (int)B.calibrated_sculk_sensor })
        {
            int sid = sid0;
            blockTickHandlers[sid] = (world, x, y, z, c) =>
            {
                if ((c & 15) != 0)
                {
                    world.setBlock(x, y, z, (int)cell((ushort)sid, 0));
                }
            };
        }
        blockTickHandlers[(int)B.sculk_shrieker] = (world, x, y, z, c) =>
        {
            if ((c & 1) != 0) { world.setBlock(x, y, z, (int)cell(B.sculk_shrieker, 0), 4); }
        };

        // daylight detector --------------------------------------------------------------
        foreach (var dd0 in new[] { (int)B.daylight_detector, (int)B.daylight_detector_inverted })
        {
            int dd = dd0;
            blockTickHandlers[dd] = (world, x, y, z, c) =>
            {
                int sky = world.getSkyLight(x, y + 1, z);
                double dayF = Math.Max(0, 1 - world.skyDarken() / 11);
                int power = (int)detRound((double)sky * dayF);
                if ((c >> 4) == (int)B.daylight_detector_inverted) { power = (int)detRound((double)sky * (1 - dayF)); }
                power = Math.Max(0, Math.Min(15, power));
                if ((c & 15) != power)
                {
                    world.setBlock(x, y, z, (int)cell((ushort)(c >> 4), power), 1 | 4);
                }
                world.scheduleTick(x, y, z, c >> 4, 20);
            };
            onPlacedHandlers[dd] = (world, x, y, z, c) => world.scheduleTick(x, y, z, dd, 20);
        }

        // pistons --------------------------------------------------------------------
        NeighborFn pistonNeighbor = (world, x, y, z, c, fx, fy, fz) =>
        {
            int id = c >> 4;
            int facing = c & 7;
            bool extended = (c & 8) != 0;
            bool powered = pistonPowered(world, x, y, z, facing);
            if (powered != extended && !world.hasScheduledTick(x, y, z, id))
            {
                world.scheduleTick(x, y, z, id, 2);
            }
        };
        neighborHandlers[(int)B.piston] = pistonNeighbor;
        neighborHandlers[(int)B.sticky_piston] = pistonNeighbor;
        blockTickHandlers[(int)B.piston] = pistonTick;
        blockTickHandlers[(int)B.sticky_piston] = pistonTick;

        // lightning rod power-off
        blockTickHandlers[(int)B.lightning_rod] = (world, x, y, z, c) =>
        {
            if ((c & 8) != 0) { world.setBlock(x, y, z, (int)cell(B.lightning_rod, c & 7)); }
        };
    }
}

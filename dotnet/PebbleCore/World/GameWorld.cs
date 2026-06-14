// The World — Chunk map, block access with
// light/remesh propagation, scheduled + random ticks, block entities, entity
// lists, raycasting, weather and time. Handler registries are module-global
// dictionaries filled by the systems layer at init, deterministically.
//
// Defines static class `WorldHandlers` (SET_SILENT/SET_DEFAULT/SET_NO_NEIGHBORS,
// DAY_LENGTH, the handler-registry dictionaries blockTickHandlers/randomTickHandlers/
// neighborHandlers/beTickHandlers/onPlacedHandlers, and the BlockTickFn/NeighborFn/
// BETickFn delegate types). floorDiv/posMod/chunkKey/CHUNK_W/dimInfo/DIMS come from
// WorldGlobals. blockDefs/B come from Reg/BlockDefs. OPAQUE/LIGHT_OPACITY/HAS_GRAVITY/
// RANDOM_TICKS come from BlockCaches. cell helpers (isWaterlogged, shapeBoxes, aabb,
// lightEmitOf, etc.) come from the cell/shape helper layers; detCos from DetMath;
// sweep/rayAABB from MathX.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

/// structural view of an entity — the real Entity class satisfies this
public interface EntityRef
{
    int id { get; }
    double x { get; }
    double y { get; }
    double z { get; }
    bool dead { get; }
    AABB bb();
}

public delegate void BlockTickFn(World world, int x, int y, int z, int cell);
public delegate void NeighborFn(World world, int x, int y, int z, int cell, int fromX, int fromY, int fromZ);
public delegate void BETickFn(World world, BlockEntityData be);

public static class WorldHandlers
{
    public const int SET_SILENT = 0;            // worldgen: no updates at all
    public const int SET_DEFAULT = 1 | 2 | 4;   // neighbors + light + remesh
    public const int SET_NO_NEIGHBORS = 2 | 4;

    public const int DAY_LENGTH = 24000;

    // shared handler registries (filled by systems at module init)
    public static Dictionary<int, BlockTickFn> blockTickHandlers = new();
    public static Dictionary<int, BlockTickFn> randomTickHandlers = new();
    public static Dictionary<int, NeighborFn> neighborHandlers = new();
    public static Dictionary<string, BETickFn> beTickHandlers = new();
    public static Dictionary<int, BlockTickFn> onPlacedHandlers = new();
}

public struct ScheduledTick
{
    public int time;
    public int x, y, z;
    public int id;
    public int priority;
    public int order;

    public ScheduledTick(int time, int x, int y, int z, int id, int priority, int order)
    {
        this.time = time; this.x = x; this.y = y; this.z = z;
        this.id = id; this.priority = priority; this.order = order;
    }
}

public struct RaycastHit
{
    public readonly int x, y, z;
    public readonly int face;          // Dir
    public readonly int cell;
    public readonly double t;
    public readonly double px, py, pz;

    public RaycastHit(int x, int y, int z, int face, int cell, double t, double px, double py, double pz)
    {
        this.x = x; this.y = y; this.z = z;
        this.face = face; this.cell = cell; this.t = t;
        this.px = px; this.py = py; this.pz = pz;
    }
}

public struct WorldHooks
{
    public Action<int, int, int> onSectionDirty;
    public Action<string, double, double, double, double, double> playSound;
    public Action<string, double, double, double, int, double, int> addParticles;
    public Action<double, double, double, int, EntityRef> onVibration;
    public Action<int, int> requestChunk;

    public WorldHooks()
    {
        onSectionDirty = (_, _, _) => { };
        playSound = (_, _, _, _, _, _) => { };
        addParticles = (_, _, _, _, _, _, _) => { };
        onVibration = null;
        requestChunk = null;
    }
}

/// position key for transient redstone state on doors/trapdoors/gates
public struct OpenablePos : IEquatable<OpenablePos>
{
    public readonly int x, y, z;
    public OpenablePos(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }

    public bool Equals(OpenablePos o) => x == o.x && y == o.y && z == o.z;
    public override bool Equals(object o) => o is OpenablePos p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(x, y, z);
}

internal struct TickKey : IEquatable<TickKey>
{
    public int x, y, z, id;
    public TickKey(int x, int y, int z, int id) { this.x = x; this.y = y; this.z = z; this.id = id; }

    public bool Equals(TickKey o) => x == o.x && y == o.y && z == o.z && id == o.id;
    public override bool Equals(object o) => o is TickKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(x, y, z, id);
}

public sealed class World
{
    private static int tickOrderCounter = 0;

    public readonly Dim dim;
    public readonly uint seed;
    public Dictionary<long, Chunk> chunks = new();
    public List<EntityRef> entities = new();
    public Dictionary<int, EntityRef> entityById = new();
    public readonly DimInfo info;
    public int time = 0;            // total ticks elapsed (world age)
    public int dayTime = 1000;      // 0..23999
    public bool raining = false;
    public bool thundering = false;
    public double rainLevel = 0.0;
    public double thunderLevel = 0.0;
    public int weatherTimer = 12000;
    public RandomX rng;
    public double spawnX = 0.0, spawnY = 80.0, spawnZ = 0.0;
    public LightEngine light { get; private set; }
    public int difficulty = 2;
    public WorldHooks hooks = new WorldHooks();
    private List<ScheduledTick> tickQueue = new();
    private HashSet<TickKey> scheduledSet = new();
    /// block entities needing per-tick updates — insertion-ordered array like
    /// the golden baselines an insertion-ordered map (a Dictionary ticked hoppers/furnaces in
    /// hash-seeded order, different every run); the Set is the dedupe index
    public HashSet<BlockEntityData> tickingBE = new();
    public List<BlockEntityData> tickingBEList = new();
    private void trackTickingBE(BlockEntityData be)
    {
        if (tickingBE.Add(be)) tickingBEList.Add(be);
    }
    private void untrackTickingBE(BlockEntityData be)
    {
        if (tickingBE.Remove(be)) tickingBEList.RemoveAll(x => ReferenceEquals(x, be));
    }
    /// openables (doors/trapdoors/gates) that were powered on their last
    /// neighbor update — lets redstone handlers act on power TRANSITIONS only,
    /// so a manual open isn't slammed shut by the next unrelated update.
    /// transient: after a reload the first power-on re-fires as a transition.
    public HashSet<OpenablePos> poweredOpenables = new();
    public int simCenterX = 0, simCenterZ = 0;
    public int simDistance = 6;
    public int randomTickSpeed = 3;
    public Dictionary<string, double> gameRules = new()
    {
        { "doDaylightCycle", 1 }, { "doWeatherCycle", 1 }, { "doMobSpawning", 1 }, { "doFireTick", 1 },
        { "mobGriefing", 1 }, { "keepInventory", 0 }, { "doMobLoot", 1 }, { "doTileDrops", 1 },
        { "naturalRegeneration", 1 }, { "fallDamage", 1 }, { "drowningDamage", 1 }, { "fireDamage", 1 },
    };

    public World(Dim dim, uint seed)
    {
        this.dim = dim;
        this.seed = seed;
        info = DIMS[(int)dim];
        rng = new RandomX(unchecked((uint)((int)seed ^ ((int)dim * 7919))));
        light = new LightEngine(this);
    }

    public bool rule(string name) => (gameRules.TryGetValue(name, out var v) ? v : 0) != 0;

    // MARK: - chunk access
    public Chunk getChunk(int cx, int cz)
        => chunks.TryGetValue(chunkKey(cx, cz), out var c) ? c : null;
    public Chunk getChunkAt(int x, int z)
        => chunks.TryGetValue(chunkKey(floorDiv(x, CHUNK_W), floorDiv(z, CHUNK_W)), out var c) ? c : null;
    public void setChunk(Chunk c)
    {
        chunks[chunkKey(c.cx, c.cz)] = c;
    }
    public void removeChunk(int cx, int cz)
    {
        chunks.Remove(chunkKey(cx, cz));
    }
    public bool isChunkReady(int cx, int cz)
    {
        var c = getChunk(cx, cz);
        if (c == null) return false;
        return c.status != Chunk.ChunkStatus.empty;
    }
    public bool neighborsReady(int cx, int cz)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!isChunkReady(cx + dx, cz + dz)) return false;
            }
        }
        return true;
    }

    // MARK: - block access
    public int getBlock(int x, int y, int z)
    {
        if (!chunks.TryGetValue(chunkKey(floorDiv(x, CHUNK_W), floorDiv(z, CHUNK_W)), out var c)) return 0;
        return (int)c.get(posMod(x, CHUNK_W), y, posMod(z, CHUNK_W));
    }
    public int getBlockId(int x, int y, int z)
        => getBlock(x, y, z) >> 4;
    public int getMeta(int x, int y, int z)
        => getBlock(x, y, z) & 15;
    public bool isLoadedAt(int x, int z)
        => getChunkAt(x, z) != null;

    public int setBlock(int x, int y, int z, int cellV, int flags = SET_DEFAULT)
    {
        int cx = floorDiv(x, CHUNK_W), cz = floorDiv(z, CHUNK_W);
        if (!chunks.TryGetValue(chunkKey(cx, cz), out var c) || !c.inYRange(y)) return 0;
        int lx = posMod(x, CHUNK_W), lz = posMod(z, CHUNK_W);
        int old = (int)c.get(lx, y, lz);
        if (old == cellV) return old;
        c.set(lx, y, lz, (ushort)cellV);
        c.modified = true;
        c.trackSpecial(lx, y, lz, (ushort)(cellV >> 4));

        int oldId = old >> 4, newId = cellV >> 4;
        if (oldId != newId && (blockDefs[oldId].shape != blockDefs[newId].shape || !blockDefs[newId].solid))
        {
            // block entity invalidation when the block type changes
            var be = c.getBlockEntity(lx, y, lz);
            if (be != null)
            {
                untrackTickingBE(be);
                c.removeBlockEntity(lx, y, lz);
            }
        }

        // heightmap
        int hPrev = c.heightAt(lx, lz);
        if (y >= hPrev || OPAQUE[newId] == 1 || LIGHT_OPACITY[newId] > 0) { c.updateHeight(lx, lz); }

        if ((flags & 2) != 0) light.onBlockChanged(x, y, z, old, cellV);

        if ((flags & 4) != 0)
        {
            c.markDirtyAt(y);
            hooks.onSectionDirty(cx, cz, (y - info.minY) >> 4);
            // border remesh
            int sy = (y - info.minY) & 15;
            if (lx == 0) dirtyNeighbor(cx - 1, cz, y);
            if (lx == 15) dirtyNeighbor(cx + 1, cz, y);
            if (lz == 0) dirtyNeighbor(cx, cz - 1, y);
            if (lz == 15) dirtyNeighbor(cx, cz + 1, y);
            if (sy == 0) hooks.onSectionDirty(cx, cz, (y - 1 - info.minY) >> 4);
            if (sy == 15) hooks.onSectionDirty(cx, cz, (y + 1 - info.minY) >> 4);
        }

        if ((flags & 1) != 0)
        {
            updateNeighbors(x, y, z);
            notifyBlock(x, y, z, x, y, z); // self update (rails, wire reshape)
        }
        return old;
    }
    private void dirtyNeighbor(int cx, int cz, int y)
    {
        var n = getChunk(cx, cz);
        if (n != null)
        {
            n.markDirtyAt(y);
            hooks.onSectionDirty(cx, cz, (y - info.minY) >> 4);
        }
    }

    /// notify the 6 neighbors that (x,y,z) changed
    public void updateNeighbors(int x, int y, int z)
    {
        notifyBlock(x - 1, y, z, x, y, z);
        notifyBlock(x + 1, y, z, x, y, z);
        notifyBlock(x, y - 1, z, x, y, z);
        notifyBlock(x, y + 1, z, x, y, z);
        notifyBlock(x, y, z - 1, x, y, z);
        notifyBlock(x, y, z + 1, x, y, z);
    }
    public void notifyBlock(int x, int y, int z, int fromX, int fromY, int fromZ)
    {
        int cell = getBlock(x, y, z);
        if (neighborHandlers.TryGetValue(cell >> 4, out var h))
        {
            h(this, x, y, z, cell, fromX, fromY, fromZ);
        }
        // gravity blocks fall when support vanishes
        if (HAS_GRAVITY[cell >> 4] == 1 && fromY == y - 1)
        {
            scheduleTick(x, y, z, cell >> 4, 2);
        }
    }

    public void breakBlockNaturally(int x, int y, int z)
    {
        // used by pistons/explosions for Destroy-behavior blocks
        int cell = getBlock(x, y, z);
        if (cell == 0) return;
        setBlock(x, y, z, 0);
        hooks.addParticles("block", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 12, 0.4, cell);
    }

    // MARK: - scheduled ticks
    public void scheduleTick(int x, int y, int z, int id, int delay, int priority = 0)
    {
        var key = new TickKey(x, y, z, id);
        if (scheduledSet.Contains(key)) return;
        scheduledSet.Add(key);
        var t = new ScheduledTick(time + delay, x, y, z, id, priority, tickOrderCounter);
        tickOrderCounter += 1;
        // sift up in place — a local copy forced a full CoW clone per insert
        tickQueue.Add(t);
        int i = tickQueue.Count - 1;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (compareTicks(tickQueue[p], tickQueue[i]) <= 0) break;
            (tickQueue[p], tickQueue[i]) = (tickQueue[i], tickQueue[p]);
            i = p;
        }
    }
    public bool hasScheduledTick(int x, int y, int z, int id)
        => scheduledSet.Contains(new TickKey(x, y, z, id));
    private void popDueTicks(List<ScheduledTick> outList)
    {
        while (tickQueue.Count != 0 && tickQueue[0].time <= time)
        {
            var top = tickQueue[0];
            var last = tickQueue[tickQueue.Count - 1];
            tickQueue.RemoveAt(tickQueue.Count - 1);
            if (tickQueue.Count != 0)
            {
                tickQueue[0] = last;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1;
                    int m = i;
                    if (l < tickQueue.Count && compareTicks(tickQueue[l], tickQueue[m]) < 0) m = l;
                    if (r < tickQueue.Count && compareTicks(tickQueue[r], tickQueue[m]) < 0) m = r;
                    if (m == i) break;
                    (tickQueue[m], tickQueue[i]) = (tickQueue[i], tickQueue[m]);
                    i = m;
                }
            }
            scheduledSet.Remove(new TickKey(top.x, top.y, top.z, top.id));
            outList.Add(top);
        }
    }

    private List<ScheduledTick> dueScratch = new();
    public void tick()
    {
        time += 1;
        if (rule("doDaylightCycle") && info.hasSky)
        {
            dayTime = (dayTime + 1) % DAY_LENGTH;
        }
        tickWeather();

        // scheduled block ticks — fluids run under a per-tick budget; a save
        // full of worldgen water can otherwise dump thousands of flow ticks
        // (each with a 4-deep drop-seek) into a single tick and melt the frame
        dueScratch.Clear();
        popDueTicks(dueScratch);
        int fluidA = (int)B.water, fluidB = (int)B.lava;
        int fluidBudget = 512;
        foreach (var t in dueScratch)
        {
            if (t.id == fluidA || t.id == fluidB)
            {
                if (fluidBudget <= 0)
                {
                    scheduleTick(t.x, t.y, t.z, t.id, 1);
                    continue;
                }
                fluidBudget -= 1;
            }
            int cell = getBlock(t.x, t.y, t.z);
            if ((cell >> 4) != t.id) continue;
            if (blockTickHandlers.TryGetValue(t.id, out var h))
            {
                h(this, t.x, t.y, t.z, cell);
            }
        }

        // random ticks in sim-range chunks
        if (randomTickSpeed > 0)
        {
            int sd = simDistance;
            for (int dz = -sd; dz <= sd; dz++)
            {
                for (int dx = -sd; dx <= sd; dx++)
                {
                    var c = getChunk(simCenterX + dx, simCenterZ + dz);
                    if (c == null || c.status == Chunk.ChunkStatus.empty) continue;
                    for (int s = 0; s < c.sections; s++)
                    {
                        for (int _i = 0; _i < randomTickSpeed; _i++)
                        {
                            int rx = rng.nextInt(16), rz = rng.nextInt(16), ry = info.minY + s * 16 + rng.nextInt(16);
                            int cell = (int)c.get(rx, ry, rz);
                            int id = cell >> 4;
                            if (id != 0 && RANDOM_TICKS[id] == 1)
                            {
                                if (randomTickHandlers.TryGetValue(id, out var h))
                                {
                                    h(this, c.cx * 16 + rx, ry, c.cz * 16 + rz, cell);
                                }
                            }
                        }
                    }
                }
            }
        }

        // ticking block entities (array is CoW — the loop iterates a snapshot,
        // so handlers may add/remove BEs safely)
        foreach (var be in tickingBEList.ToArray())
        {
            if (beTickHandlers.TryGetValue(be.type, out var h))
            {
                h(this, be);
            }
        }

        light.flush();
    }

    private void tickWeather()
    {
        if (dim != Dim.overworld) return;
        if (rule("doWeatherCycle"))
        {
            weatherTimer -= 1;
            if (weatherTimer <= 0)
            {
                if (raining)
                {
                    raining = false;
                    thundering = false;
                    weatherTimer = 12000 + rng.nextInt(156000);
                }
                else
                {
                    raining = true;
                    thundering = rng.chance(0.3);
                    weatherTimer = 12000 + rng.nextInt(12000);
                }
            }
        }
        rainLevel += raining ? 0.01 : -0.01;
        rainLevel = Math.Max(0, Math.Min(1, rainLevel));
        thunderLevel += thundering ? 0.01 : -0.01;
        thunderLevel = Math.Max(0, Math.Min(1, thunderLevel));
    }

    /// celestial angle 0..1 (0.0 = noon-ish vanilla curve)
    public double sunAngle()
    {
        double f = ((double)dayTime / (double)DAY_LENGTH) - 0.25;
        double frac = f < 0 ? f + 1 : f;
        double a = 1 - (detCos(frac * Math.PI) + 1) / 2;
        return frac + (a - frac) / 3;
    }
    /// sky darkness factor 0..11 subtracted from skylight 15
    public double skyDarken()
    {
        double angle = sunAngle();
        double f = 1 - (detCos(angle * Math.PI * 2) * 2 + 0.5);
        f = Math.Max(0, Math.Min(1, f));
        f = 1 - f;
        f *= 1 - rainLevel * 5 / 16;
        f *= 1 - thunderLevel * 5 / 16;
        return (1 - f) * 11;
    }
    public bool isDay() => skyDarken() < 4;

    // MARK: - light
    public int getSkyLight(int x, int y, int z)
    {
        var c = getChunkAt(x, z);
        if (c == null) return 15;
        return c.getSky(posMod(x, CHUNK_W), y, posMod(z, CHUNK_W));
    }
    public int getBlockLight(int x, int y, int z)
    {
        var c = getChunkAt(x, z);
        if (c == null) return 0;
        return c.getBlockLight(posMod(x, CHUNK_W), y, posMod(z, CHUNK_W));
    }
    /// effective light for mob spawning / rendering decisions
    public double lightAt(int x, int y, int z)
    {
        double sky = Math.Max(0, (double)getSkyLight(x, y, z) - skyDarken());
        return Math.Max((double)info.ambientLight, Math.Max(sky, (double)getBlockLight(x, y, z)));
    }

    public int heightAt(int x, int z)
    {
        var c = getChunkAt(x, z);
        if (c == null) return info.minY;
        return c.heightAt(posMod(x, CHUNK_W), posMod(z, CHUNK_W));
    }
    /// highest motion-blocking block + 1 (where an entity can stand)
    public int surfaceY(int x, int z)
    {
        int top = info.minY + info.height - 1;
        int y = top;
        while (y > info.minY)
        {
            int cell = getBlock(x, y, z);
            int id = cell >> 4;
            if (id != 0 && blockDefs[id].solid) return y + 1;
            y -= 1;
        }
        return info.minY + 1;
    }
    public bool canSeeSky(int x, int y, int z)
        => y >= heightAt(x, z) && info.hasSky;
    public int biomeAt(int x, int y, int z)
    {
        var c = getChunkAt(x, z);
        if (c == null) return 0;
        return c.biomeAt(posMod(x, CHUNK_W), y, posMod(z, CHUNK_W));
    }

    // MARK: - block entities
    public BlockEntityData getBlockEntity(int x, int y, int z)
        => getChunkAt(x, z)?.getBlockEntity(posMod(x, CHUNK_W), y, posMod(z, CHUNK_W));
    public void setBlockEntity(BlockEntityData be)
    {
        var c = getChunkAt(be.x, be.z);
        if (c == null) return;
        c.setBlockEntity(posMod(be.x, CHUNK_W), be.y, posMod(be.z, CHUNK_W), be);
        // Swift's BlockEntityData.type is non-optional, so for valid data this guard
        // is always true (identical behavior). It only short-circuits a malformed or
        // legacy save whose `type` decoded as null, which must not crash the load.
        if (be.type != null && beTickHandlers.ContainsKey(be.type))
        {
            trackTickingBE(be);
        }
    }
    /// rebuild ticking set after chunk load — sorted by cell index so the tick
    /// order is reproducible (Dictionary.values order is hash-seeded)
    public void adoptChunkBlockEntities(Chunk c)
    {
        foreach (var kv in c.blockEntities.OrderBy(p => p.Key))
        {
            var be = kv.Value;
            // type is non-optional in Swift; guard only protects against a malformed
            // or legacy save whose `type` decoded as null (faithful for valid data).
            if (be.type != null && beTickHandlers.ContainsKey(be.type)) trackTickingBE(be);
        }
    }
    public void releaseChunkBlockEntities(Chunk c)
    {
        foreach (var be in c.blockEntities.Values)
        {
            untrackTickingBE(be);
        }
    }

    // MARK: - entities
    public void addEntity(EntityRef e)
    {
        entities.Add(e);
        entityById[e.id] = e;
    }
    public void removeEntity(EntityRef e)
    {
        int i = entities.FindIndex(x => ReferenceEquals(x, e));
        if (i >= 0)
        {
            entities.RemoveAt(i);
        }
        entityById.Remove(e.id);
    }
    public List<EntityRef> getEntitiesInBox(AABB box, EntityRef except = null, Func<EntityRef, bool> filter = null)
    {
        var outList = new List<EntityRef>();
        foreach (var e in entities)
        {
            if (ReferenceEquals(e, except) || e.dead) continue;
            if (filter != null && !filter(e)) continue;
            if (e.bb().intersects(box)) outList.Add(e);
        }
        return outList;
    }
    public List<EntityRef> getEntitiesNear(double x, double y, double z, double radius, Func<EntityRef, bool> filter = null)
    {
        double r2 = radius * radius;
        var outList = new List<EntityRef>();
        foreach (var e in entities)
        {
            if (e.dead) continue;
            double dx = e.x - x, dy = e.y - y, dz = e.z - z;
            if (dx * dx + dy * dy + dz * dz <= r2 && (filter?.Invoke(e) ?? true)) outList.Add(e);
        }
        return outList;
    }

    // MARK: - vibrations (sculk)
    public void emitVibration(double x, double y, double z, int freq, EntityRef src)
    {
        hooks.onVibration?.Invoke(x, y, z, freq, src);
    }

    // MARK: - portals
    public (int, int, int)? findPortalNear(int x, int y, int z, int radiusChunks, int portalId)
    {
        int cx = floorDiv(x, CHUNK_W), cz = floorDiv(z, CHUNK_W);
        (int, int, int)? best = null;
        double bestD = double.PositiveInfinity;
        for (int dz = -radiusChunks; dz <= radiusChunks; dz++)
        {
            for (int dx = -radiusChunks; dx <= radiusChunks; dx++)
            {
                var c = getChunk(cx + dx, cz + dz);
                if (c == null) continue;
                // sorted: Set order is hash-seeded, and equidistant candidates
                // must tie-break the same way every run
                foreach (int idx in c.portalBlocks.OrderBy(v => v))
                {
                    var (wx, wy, wz) = c.idxToWorld(idx);
                    if ((int)(c.blocks[idx] >> 4) != portalId) continue;
                    double d = (double)((wx - x) * (wx - x) + (wy - y) * (wy - y) + (wz - z) * (wz - z));
                    if (d < bestD) { bestD = d; best = (wx, wy, wz); }
                }
            }
        }
        return best;
    }

    // MARK: - collision / raycast
    public void forEachCollisionBox(AABB box, Action<AABB> cb)
    {
        int x0 = (int)Math.Floor(box.x0 - 1), x1 = (int)Math.Floor(box.x1 + 1);
        int y0 = (int)Math.Floor(box.y0 - 1), y1 = (int)Math.Floor(box.y1 + 1);
        int z0 = (int)Math.Floor(box.z0 - 1), z1 = (int)Math.Floor(box.z1 + 1);
        var scratch = new List<AABB>();
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = getBlock(x, y, z);
                    if (cell == 0) continue;
                    int id = cell >> 4;
                    if (!blockDefs[id].solid) continue;
                    scratch.Clear();
                    int cx = x, cy = y, cz = z;
                    shapeBoxes(cell, (dx, dy, dz) => getBlock(cx + dx, cy + dy, cz + dz), scratch, true);
                    foreach (var b in scratch)
                    {
                        cb(aabb(b.x0 + (double)x, b.y0 + (double)y, b.z0 + (double)z,
                                b.x1 + (double)x, b.y1 + (double)y, b.z1 + (double)z));
                    }
                }
            }
        }
    }

    public RaycastHit? raycast(double ox, double oy, double oz, double dx, double dy, double dz, double maxDist, bool fluid = false)
    {
        int x = (int)Math.Floor(ox), y = (int)Math.Floor(oy), z = (int)Math.Floor(oz);
        int stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        int stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        int stepZ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
        double tDeltaX = stepX != 0 ? Math.Abs(1 / dx) : double.PositiveInfinity;
        double tDeltaY = stepY != 0 ? Math.Abs(1 / dy) : double.PositiveInfinity;
        double tDeltaZ = stepZ != 0 ? Math.Abs(1 / dz) : double.PositiveInfinity;
        double tMaxX = stepX > 0 ? ((double)x + 1 - ox) * tDeltaX : stepX < 0 ? (ox - (double)x) * tDeltaX : double.PositiveInfinity;
        double tMaxY = stepY > 0 ? ((double)y + 1 - oy) * tDeltaY : stepY < 0 ? (oy - (double)y) * tDeltaY : double.PositiveInfinity;
        double tMaxZ = stepZ > 0 ? ((double)z + 1 - oz) * tDeltaZ : stepZ < 0 ? (oz - (double)z) * tDeltaZ : double.PositiveInfinity;
        int face = 0;
        var scratch = new List<AABB>();
        for (int _i = 0; _i < 512; _i++)
        {
            int cell = getBlock(x, y, z);
            int id = cell >> 4;
            if (id != 0)
            {
                bool isFluid = id == (int)B.water || id == (int)B.lava;
                if ((isFluid && fluid) || (!isFluid && blockDefs[id].hardness != 100))
                {
                    scratch.Clear();
                    if (isFluid)
                    {
                        scratch.Add(aabb(0, 0, 0, 1, 14.0 / 16, 1));
                    }
                    else
                    {
                        int cx = x, cy = y, cz = z;
                        shapeBoxes(cell, (ddx, ddy, ddz) => getBlock(cx + ddx, cy + ddy, cz + ddz), scratch, false);
                    }
                    double bestT = double.PositiveInfinity;
                    int bestFace = face;
                    foreach (var b in scratch)
                    {
                        var wb = aabb(b.x0 + (double)x, b.y0 + (double)y, b.z0 + (double)z,
                                      b.x1 + (double)x, b.y1 + (double)y, b.z1 + (double)z);
                        double t = rayAABB(ox, oy, oz, dx, dy, dz, wb);
                        if (t >= 0 && t < bestT && t <= maxDist)
                        {
                            bestT = t;
                            // recompute hit face from hit point
                            double hx = ox + dx * t, hy = oy + dy * t, hz = oz + dz * t;
                            double ex = Math.Min(Math.Abs(hx - wb.x0), Math.Abs(hx - wb.x1));
                            double ey = Math.Min(Math.Abs(hy - wb.y0), Math.Abs(hy - wb.y1));
                            double ez = Math.Min(Math.Abs(hz - wb.z0), Math.Abs(hz - wb.z1));
                            if (ey <= ex && ey <= ez) bestFace = Math.Abs(hy - wb.y0) < Math.Abs(hy - wb.y1) ? 0 : 1;
                            else if (ex <= ez) bestFace = Math.Abs(hx - wb.x0) < Math.Abs(hx - wb.x1) ? 4 : 5;
                            else bestFace = Math.Abs(hz - wb.z0) < Math.Abs(hz - wb.z1) ? 2 : 3;
                        }
                    }
                    if (bestT != double.PositiveInfinity)
                    {
                        return new RaycastHit(x, y, z, bestFace, cell, bestT,
                                              ox + dx * bestT, oy + dy * bestT, oz + dz * bestT);
                    }
                }
            }
            // advance
            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                if (tMaxX > maxDist) return null;
                x += stepX; tMaxX += tDeltaX; face = stepX > 0 ? 4 : 5;
            }
            else if (tMaxY < tMaxZ)
            {
                if (tMaxY > maxDist) return null;
                y += stepY; tMaxY += tDeltaY; face = stepY > 0 ? 0 : 1;
            }
            else
            {
                if (tMaxZ > maxDist) return null;
                z += stepZ; tMaxZ += tDeltaZ; face = stepZ > 0 ? 2 : 3;
            }
        }
        return null;
    }

    // MARK: - fluid helpers
    public bool isWaterAt(int x, int y, int z)
        => isWaterlogged((ushort)getBlock(x, y, z));
    public bool isLavaAt(int x, int y, int z)
        => (getBlock(x, y, z) >> 4) == (int)B.lava;
    public double fluidHeight(int x, int y, int z)
    {
        int cell = getBlock(x, y, z);
        int id = cell >> 4;
        if (id != (int)B.water && id != (int)B.lava) return isWaterlogged((ushort)cell) ? 14.0 / 16 : 0;
        int level = cell & 7;
        if ((cell & 8) != 0) return 1; // falling
        if (level == 0)
        {
            // source: full if fluid above
            int above = getBlock(x, y + 1, z) >> 4;
            return above == id ? 1 : 14.0 / 16;
        }
        return Math.Max(2.0 / 16, (double)(8 - level) / 8 * 14 / 16);
    }

    public bool isRainingAt(int x, int y, int z)
    {
        if (rainLevel <= 0.2 || !info.hasSky) return false;
        if (!canSeeSky(x, y, z)) return false;
        return true;
    }

    private static int compareTicks(ScheduledTick a, ScheduledTick b)
    {
        if (a.time != b.time) return a.time - b.time;
        if (a.priority != b.priority) return a.priority - b.priority;
        return a.order - b.order;
    }
}

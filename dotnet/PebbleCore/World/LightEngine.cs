// Incremental flood-fill lighting — LightEngine.
// computeLocalLight (LocalLight.cs) does initial chunk lighting on flat
// arrays; this engine handles seam stitching after adoption and incremental
// add/remove on block edits. Frontier rule preserved from the golden baselines: never
// keep propagating into a missing chunk or border cells ping-pong forever.
//
// One deliberate divergence: the golden baselines packs dirty-section keys into a
// float ((cx*(1<<26)+cz)*64+sy) which silently rounds sy bits at the f64
// mantissa edge — a latent remesh-skipping bug. Dirty marking only affects
// remesh scheduling (never light values), so Swift uses exact Int packing.
//
// Defines no module-global static class. LIGHT_OPACITY comes from BlockCaches;
// lightEmitOf comes from the block helpers layer. floorDiv/posMod/CHUNK_W come
// from WorldGlobals.

using System;
using System.Collections.Generic;

namespace PebbleCore;

internal sealed class LightQueue
{
    public int[] xs = new int[1 << 16];
    public int[] ys = new int[1 << 16];
    public int[] zs = new int[1 << 16];
    public int[] vs = new int[1 << 16];
    public int head = 0;
    public int tail = 0;

    public void push(int x, int y, int z, int v)
    {
        if (tail >= xs.Length) grow();
        xs[tail] = x; ys[tail] = y; zs[tail] = z; vs[tail] = v;
        tail += 1;
    }
    private void grow()
    {
        int oldLen = xs.Length;
        int newLen = oldLen * 2;
        var nxs = new int[newLen]; Array.Copy(xs, nxs, oldLen); xs = nxs;
        var nys = new int[newLen]; Array.Copy(ys, nys, oldLen); ys = nys;
        var nzs = new int[newLen]; Array.Copy(zs, nzs, oldLen); zs = nzs;
        var nvs = new int[newLen]; Array.Copy(vs, nvs, oldLen); vs = nvs;
    }
    public bool empty => head >= tail;
    public void reset() { head = 0; tail = 0; }
}

public sealed class LightEngine
{
    private static readonly int[] DX = { 0, 0, 0, 0, -1, 1 };
    private static readonly int[] DY = { -1, 1, 0, 0, 0, 0 };
    private static readonly int[] DZ = { 0, 0, -1, 1, 0, 0 };

    private readonly World world;
    private readonly LightQueue addSky = new LightQueue();
    private readonly LightQueue delSky = new LightQueue();
    private readonly LightQueue addBlock = new LightQueue();
    private readonly LightQueue delBlock = new LightQueue();
    private HashSet<long> dirtySections = new HashSet<long>();

    public LightEngine(World world)
    {
        this.world = world;
    }

    /// initial lighting for a freshly generated chunk (call when 3×3 ready or alone)
    public void initChunkLight(Chunk c)
    {
        var w = world;
        int minY = c.minY, H = c.height;
        if (w.info.hasSky)
        {
            // top-down skylight columns
            for (int z = 0; z < CHUNK_W; z++)
            {
                for (int x = 0; x < CHUNK_W; x++)
                {
                    int level = 15;
                    int y = minY + H - 1;
                    while (y >= minY)
                    {
                        ushort cell = c.get(x, y, z);
                        int op = LIGHT_OPACITY[cell >> 4];
                        if (op > 0) level = Math.Max(0, level - Math.Max(op, 1));
                        else if (level < 15) level = Math.Max(0, level - 1);
                        if (level == 0)
                        {
                            // zero the rest of the column quickly
                            int yy = y;
                            while (yy >= minY)
                            {
                                ushort cc = c.get(x, yy, z);
                                if (lightEmitOf(cc) > 0) break; // emitters handled below
                                c.setSky(x, yy, z, 0);
                                yy -= 1;
                            }
                            break;
                        }
                        c.setSky(x, y, z, level);
                        if (level < 15 || columnNeedsSpread(c, x, y, z))
                        {
                            addSky.push(c.cx * 16 + x, y, c.cz * 16 + z, level);
                        }
                        y -= 1;
                    }
                }
            }
            // seed horizontal spread at exposed column borders
            for (int z = 0; z < CHUNK_W; z++)
            {
                for (int x = 0; x < CHUNK_W; x++)
                {
                    int h = c.heightAt(x, z);
                    int y = minY + H - 1;
                    while (y > h)
                    {
                        if (x == 0 || x == 15 || z == 0 || z == 15 ||
                            c.heightAt(Math.Max(0, x - 1), z) > y || c.heightAt(Math.Min(15, x + 1), z) > y ||
                            c.heightAt(x, Math.Max(0, z - 1)) > y || c.heightAt(x, Math.Min(15, z + 1)) > y)
                        {
                            addSky.push(c.cx * 16 + x, y, c.cz * 16 + z, c.getSky(x, y, z));
                        }
                        y -= 1;
                    }
                }
            }
        }
        // block light emitters
        for (int i = 0; i < c.blocks.Length; i++)
        {
            ushort cell = c.blocks[i];
            if (cell == 0) continue;
            int emit = lightEmitOf(cell);
            if (emit > 0)
            {
                int x = i & 15, z = (i >> 4) & 15, y = (i >> 8) + minY;
                c.setBlockLight(x, y, z, emit);
                addBlock.push(c.cx * 16 + x, y, c.cz * 16 + z, emit);
            }
        }
        c.status = Chunk.ChunkStatus.lit;
        propagate();
    }
    private bool columnNeedsSpread(Chunk c, int x, int y, int z)
        => y <= c.heightAt(x, z) + 1;

    /// Seam exchange for a chunk whose local light was precomputed (worker).
    public void stitchChunk(Chunk c)
    {
        var w = world;
        int minY = c.minY, H = c.height;
        bool hasSky = w.info.hasSky;
        const int YS = 256;
        int x0 = c.cx * 16, z0 = c.cz * 16;
        var edges = new[] { (edge: 0, ox: -1, oz: 0), (edge: 1, ox: 1, oz: 0), (edge: 2, ox: 0, oz: -1), (edge: 3, ox: 0, oz: 1) };
        foreach (var (edge, ox, oz) in edges)
        {
            var n = w.getChunk(c.cx + ox, c.cz + oz);
            if (n == null || n.status == Chunk.ChunkStatus.empty) continue;
            for (int i = 0; i < 16; i++)
            {
                // border cell in c and the adjacent cell in n, as flat-array bases
                int ax = edge == 0 ? 0 : edge == 1 ? 15 : i;
                int az = edge == 2 ? 0 : edge == 3 ? 15 : i;
                int bx = edge == 0 ? 15 : edge == 1 ? 0 : i;
                int bz = edge == 2 ? 15 : edge == 3 ? 0 : i;
                int aCol = az * 16 + ax, bCol = bz * 16 + bx;
                int wxA = x0 + ax, wzA = z0 + az;
                int wxB = wxA + ox, wzB = wzA + oz;
                for (int y = 0; y < H; y++)
                {
                    int ai = y * YS + aCol, bi = y * YS + bCol;
                    if (hasSky)
                    {
                        int aS = c.skyLight[ai], bS = n.skyLight[bi];
                        if (aS > bS + 1) addSky.push(wxA, minY + y, wzA, aS);
                        else if (bS > aS + 1) addSky.push(wxB, minY + y, wzB, bS);
                    }
                    int aB = c.blockLight[ai], bB = n.blockLight[bi];
                    if (aB > bB + 1) addBlock.push(wxA, minY + y, wzA, aB);
                    else if (bB > aB + 1) addBlock.push(wxB, minY + y, wzB, bB);
                }
            }
        }
        c.status = Chunk.ChunkStatus.lit;
        propagate();
    }

    public void onBlockChanged(int x, int y, int z, int oldCell, int newCell)
    {
        var w = world;
        int oldEmit = lightEmitOf((ushort)oldCell), newEmit = lightEmitOf((ushort)newCell);
        int oldOp = LIGHT_OPACITY[oldCell >> 4], newOp = LIGHT_OPACITY[newCell >> 4];

        if (oldEmit > 0)
        {
            delBlock.push(x, y, z, oldEmit);
            _ = setBL(x, y, z, 0);
        }
        if (newEmit > 0)
        {
            _ = setBL(x, y, z, newEmit);
            addBlock.push(x, y, z, newEmit);
        }
        if (newOp > oldOp)
        {
            // got more opaque: remove light passing through
            int bl = getBL(x, y, z);
            if (bl > 0 && newEmit == 0) { delBlock.push(x, y, z, bl); _ = setBL(x, y, z, 0); }
            if (w.info.hasSky)
            {
                int sl = getSL(x, y, z);
                if (sl > 0) { delSky.push(x, y, z, sl); _ = setSL(x, y, z, 0); }
            }
        }
        else if (newOp < oldOp)
        {
            // became transparent: pull light in from neighbors
            for (int d = 0; d < 6; d++)
            {
                int nx = x + DX[d], ny = y + DY[d], nz = z + DZ[d];
                int nbl = getBL(nx, ny, nz);
                if (nbl > 1) addBlock.push(nx, ny, nz, nbl);
                if (w.info.hasSky)
                {
                    int nsl = getSL(nx, ny, nz);
                    if (nsl > 0) addSky.push(nx, ny, nz, nsl);
                }
            }
            if (w.info.hasSky && y >= w.heightAt(x, z))
            {
                // direct sky above again — re-seed column
                _ = setSL(x, y, z, 15);
                addSky.push(x, y, z, 15);
            }
        }
        propagate();
    }

    /// drain queues — called from world tick and after edits
    public void flush()
    {
        if (!addSky.empty || !delSky.empty || !addBlock.empty || !delBlock.empty)
        {
            propagate();
        }
    }

    private void propagate()
    {
        removePass(delBlock, addBlock, false);
        addPass(addBlock, false);
        if (world.info.hasSky)
        {
            removePass(delSky, addSky, true);
            addPass(addSky, true);
        }
        delBlock.reset(); addBlock.reset();
        delSky.reset(); addSky.reset();
        // mark dirty sections for remesh
        foreach (long key in dirtySections)
        {
            int sy = (int)(key & 63);
            int cz = (int)((key >> 6) & 0x3FF_FFFF) - (1 << 25);
            int cx = (int)(key >> 32) - (1 << 25);
            var c = world.getChunk(cx, cz);
            if (c != null)
            {
                c.dirty[Math.Max(0, Math.Min(c.sections - 1, sy))] = true;
                c.version += 1;
                world.hooks.onSectionDirty(cx, cz, sy);
            }
        }
        dirtySections.Clear();
    }

    private void markDirty(int x, int y, int z)
    {
        long cx = floorDiv(x, 16) + (1 << 25);
        long cz = floorDiv(z, 16) + (1 << 25);
        long sy = Math.Max(0, Math.Min(63, (y - world.info.minY) >> 4));
        dirtySections.Add((cx << 32) | (cz << 6) | sy);
    }

    private int getBL(int x, int y, int z) => world.getBlockLight(x, y, z);
    /// returns false when the chunk doesn't exist — callers must NOT keep
    /// propagating there, or border cells ping-pong the queue forever
    private bool setBL(int x, int y, int z, int v)
    {
        var c = world.getChunkAt(x, z);
        if (c == null) return false;
        c.setBlockLight(posMod(x, 16), y, posMod(z, 16), v);
        markDirty(x, y, z);
        return true;
    }
    private int getSL(int x, int y, int z) => world.getSkyLight(x, y, z);
    private bool setSL(int x, int y, int z, int v)
    {
        var c = world.getChunkAt(x, z);
        if (c == null) return false;
        c.setSky(posMod(x, 16), y, posMod(z, 16), v);
        markDirty(x, y, z);
        return true;
    }

    private void addPass(LightQueue q, bool sky)
    {
        var w = world;
        while (!q.empty)
        {
            int i = q.head;
            q.head += 1;
            int x = q.xs[i], y = q.ys[i], z = q.zs[i], v = q.vs[i];
            int cur = sky ? getSL(x, y, z) : getBL(x, y, z);
            if (cur > v) continue; // stale
            for (int d = 0; d < 6; d++)
            {
                int nx = x + DX[d], ny = y + DY[d], nz = z + DZ[d];
                if (ny < w.info.minY || ny >= w.info.minY + w.info.height) continue;
                int ncell = w.getBlock(nx, ny, nz);
                int rawOp = LIGHT_OPACITY[ncell >> 4];
                int op = Math.Max(1, rawOp);
                int nv;
                if (sky && d == 0 && v == 15 && rawOp == 0) nv = 15; // skylight falls undiminished
                else nv = v - op;
                if (nv <= 0) continue;
                int ncur = sky ? getSL(nx, ny, nz) : getBL(nx, ny, nz);
                if (ncur >= nv) continue;
                // only continue the flood where the write actually landed
                bool wrote = sky ? setSL(nx, ny, nz, nv) : setBL(nx, ny, nz, nv);
                if (!wrote) continue;
                q.push(nx, ny, nz, nv);
            }
        }
    }

    private void removePass(LightQueue del, LightQueue add, bool sky)
    {
        var w = world;
        while (!del.empty)
        {
            int i = del.head;
            del.head += 1;
            int x = del.xs[i], y = del.ys[i], z = del.zs[i], v = del.vs[i];
            for (int d = 0; d < 6; d++)
            {
                int nx = x + DX[d], ny = y + DY[d], nz = z + DZ[d];
                if (ny < w.info.minY || ny >= w.info.minY + w.info.height) continue;
                int ncur = sky ? getSL(nx, ny, nz) : getBL(nx, ny, nz);
                if (ncur == 0) continue;
                bool wasFedByUs = ncur < v || (sky && d == 0 && v == 15 && ncur == 15);
                if (wasFedByUs)
                {
                    bool wrote = sky ? setSL(nx, ny, nz, 0) : setBL(nx, ny, nz, 0);
                    if (!wrote) continue; // missing chunk — do not chase the removal there
                    del.push(nx, ny, nz, ncur);
                    // re-add emitters encountered
                    if (!sky)
                    {
                        int emit = lightEmitOf((ushort)w.getBlock(nx, ny, nz));
                        if (emit > 0)
                        {
                            _ = setBL(nx, ny, nz, emit);
                            add.push(nx, ny, nz, emit);
                        }
                    }
                }
                else
                {
                    // boundary: neighbor has light from another source — respread from it
                    add.push(nx, ny, nz, ncur);
                }
            }
        }
    }
}

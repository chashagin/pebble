// Structure framework — Deterministic
// region-based placement, plan caching, template stamping and build utilities.
// Ported from Sources/PebbleCore/Gen/StructFramework.swift.
//
// Defines static class: StructFrameworkGlobals
//   (module-level free funcs/state: STRUCTURES, registerStructure, structureOriginFor,
//    getPlan, buildStructuresForChunk, piece, strongholdPositions, rotF) — surface via
//    `global using static PebbleCore.StructFrameworkGlobals`.
//
// NAME CONFLICT (orchestrator must resolve): this file declares the boxed reference
// type `public class Rng` (faithful to Swift — other Gen files reference it by type
// name and construct it). The RandomX.cs free-function holder is currently ALSO named
// `Rng` and surfaced via `global using static PebbleCore.Rng`. C# cannot have a static
// class and a class share one name in the same namespace. The free-function holder must
// be renamed (e.g. `RngF`/`Hashing`) and the global-using updated; its members
// (hash2/hash3/mix32/hashString/chunkRandom/hashFloat2/hashFloat3) are referenced
// unqualified here (hash2) and must stay surfaced. The TYPE `Rng` keeps its Swift name.

using System;
using System.Collections.Generic;

namespace PebbleCore;

/// reference-boxed RandomX so structure plan/build closures can share one
/// advancing stream like the baseline Random class instances do
public sealed class Rng
{
    public RandomX r;
    public Rng(uint seed) { r = new RandomX(seed); }
    public Rng(RandomX rx) { r = rx; }
    public double nextFloat() => r.nextFloat();
    public int nextInt(int bound) => r.nextInt(bound);
    public int nextIntBetween(int a, int b) => r.nextIntBetween(a, b);
    public bool nextBoolean() => r.nextBoolean();
    public bool chance(double p) => r.chance(p);
    public T pick<T>(IReadOnlyList<T> arr) => r.pick(arr);
    public T[] shuffle<T>(T[] arr)
    {
        var a = (T[])arr.Clone();
        r.shuffle(a);
        return a;
    }
}

public struct GenCtx
{
    public readonly uint seed;
    /// noise-based surface height estimate (overworld) or floor probe (nether/end)
    public readonly Func<int, int, int> heightAt;
    public readonly Func<int, int, int> biomeAt;
    public readonly int dim;

    public GenCtx(uint seed, Func<int, int, int> heightAt, Func<int, int, int> biomeAt, int dim)
    {
        this.seed = seed;
        this.heightAt = heightAt;
        this.biomeAt = biomeAt;
        this.dim = dim;
    }
}

public struct StructPiece
{
    public readonly int x0, y0, z0, x1, y1, z1;
    public readonly Action<Builder> build;

    public StructPiece(int x0, int y0, int z0, int x1, int y1, int z1, Action<Builder> build)
    {
        this.x0 = x0; this.y0 = y0; this.z0 = z0;
        this.x1 = x1; this.y1 = y1; this.z1 = z1;
        this.build = build;
    }
}

public struct StructRefBox
{
    public readonly int x0, y0, z0, x1, y1, z1;
    public StructRefBox(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        this.x0 = x0; this.y0 = y0; this.z0 = z0;
        this.x1 = x1; this.y1 = y1; this.z1 = z1;
    }
}

public struct StructurePlan
{
    public readonly string id;
    public readonly List<StructPiece> pieces;
    /// world-space ref box stored on chunks for runtime queries (mob spawning)
    public StructRefBox? @ref;

    public StructurePlan(string id, List<StructPiece> pieces, StructRefBox? @ref = null)
    {
        this.id = id;
        this.pieces = pieces;
        this.@ref = @ref;
    }
}

public struct StructureDef
{
    public readonly string id;
    public readonly int spacing;
    public readonly int separation;
    public readonly uint salt;
    public readonly int maxRadiusChunks;
    public readonly Func<GenCtx, int, int, Rng, bool> check;
    public readonly Func<GenCtx, int, int, Rng, StructurePlan?> plan;

    public StructureDef(string id, int spacing, int separation, uint salt, int maxRadiusChunks,
                        Func<GenCtx, int, int, Rng, bool> check,
                        Func<GenCtx, int, int, Rng, StructurePlan?> plan)
    {
        this.id = id;
        this.spacing = spacing;
        this.separation = separation;
        this.salt = salt;
        this.maxRadiusChunks = maxRadiusChunks;
        this.check = check;
        this.plan = plan;
    }
}

public struct StructRef
{
    public readonly string id;
    public readonly int x0, y0, z0, x1, y1, z1;

    public StructRef(string id, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        this.id = id;
        this.x0 = x0; this.y0 = y0; this.z0 = z0;
        this.x1 = x1; this.y1 = y1; this.z1 = z1;
    }
}

// ---------------------------------------------------------------------------
// Builder
// ---------------------------------------------------------------------------
public sealed class Builder
{
    public readonly ChunkSink s;
    public readonly Rng rng;

    public Builder(ChunkSink s, Rng rng)
    {
        this.s = s;
        this.rng = rng;
    }

    public void set(int x, int y, int z, int c) { s.set(x, y, z, (ushort)c); }
    public int get(int x, int y, int z) => s.get(x, y, z);

    public void fill(int x0, int y0, int z0, int x1, int y1, int z1, int c)
    {
        var y = y0;
        while (y <= y1)
        {
            var z = z0;
            while (z <= z1)
            {
                var x = x0;
                while (x <= x1) { s.set(x, y, z, (ushort)c); x += 1; }
                z += 1;
            }
            y += 1;
        }
    }

    public void fillRandom(int x0, int y0, int z0, int x1, int y1, int z1, List<(int, double)> choices)
    {
        var total = 0.0;
        foreach (var ch in choices) total += ch.Item2;
        var y = y0;
        while (y <= y1)
        {
            var z = z0;
            while (z <= z1)
            {
                var x = x0;
                while (x <= x1)
                {
                    var r = rng.nextFloat() * total;
                    foreach (var (c, w) in choices)
                    {
                        r -= w;
                        if (r <= 0) { s.set(x, y, z, (ushort)c); break; }
                    }
                    x += 1;
                }
                z += 1;
            }
            y += 1;
        }
    }

    public void walls(int x0, int y0, int z0, int x1, int y1, int z1, int wall, int inner)
    {
        var y = y0;
        while (y <= y1)
        {
            var z = z0;
            while (z <= z1)
            {
                var x = x0;
                while (x <= x1)
                {
                    var isWall = x == x0 || x == x1 || z == z0 || z == z1 || y == y0 || y == y1;
                    s.set(x, y, z, (ushort)(isWall ? wall : inner));
                    x += 1;
                }
                z += 1;
            }
            y += 1;
        }
    }

    /// clear box to air
    public void clear(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        fill(x0, y0, z0, x1, y1, z1, 0);
    }

    /// column of c from y down until solid ground (foundation)
    public void foundation(int x, int yTop, int z, int c, int maxDepth = 8)
    {
        for (int d = 0; d < maxDepth; d++)
        {
            var y = yTop - d;
            var cur = s.get(x, y, z);
            if (cur > 0 && (ushort)(cur >> 4) != B.water && (ushort)(cur >> 4) != B.lava && d > 0) return;
            s.set(x, y, z, (ushort)c);
        }
    }

    public void chest(int x, int y, int z, int facing, string lootTable)
    {
        s.set(x, y, z, cell(B.chest, facing));
        s.addBlockEntity(new BESpec(x: x, y: y, z: z, kind: "chest_loot",
                                    data: new Dictionary<string, BEValue> {
                                        ["lootTable"] = BEValue.str(lootTable),
                                        ["seed"] = BEValue.num((double)hash2(0, x, z, unchecked((uint)y)))
                                    }));
    }

    public void barrelLoot(int x, int y, int z, string lootTable)
    {
        s.set(x, y, z, cell(B.barrel, 1));
        s.addBlockEntity(new BESpec(x: x, y: y, z: z, kind: "chest_loot",
                                    data: new Dictionary<string, BEValue> {
                                        ["lootTable"] = BEValue.str(lootTable),
                                        ["seed"] = BEValue.num((double)hash2(0, x, z, unchecked((uint)y)))
                                    }));
    }

    public void spawner(int x, int y, int z, string mob)
    {
        s.set(x, y, z, cell(B.spawner));
        s.addBlockEntity(new BESpec(x: x, y: y, z: z, kind: "spawner",
                                    data: new Dictionary<string, BEValue> { ["mob"] = BEValue.str(mob) }));
    }

    public void mob(string mobName, int x, int y, int z, Dictionary<string, BEValue> data = null)
    {
        s.addEntity(new EntitySpec(mob: mobName, x: (double)x + 0.5, y: (double)y, z: (double)z + 0.5,
                                   data: data ?? new Dictionary<string, BEValue>()));
    }

    public void suspicious(int x, int y, int z, bool gravel, string lootTable)
    {
        s.set(x, y, z, cell(gravel ? B.suspicious_gravel : B.suspicious_sand));
        s.addBlockEntity(new BESpec(x: x, y: y, z: z, kind: "brushable",
                                    data: new Dictionary<string, BEValue> { ["lootTable"] = BEValue.str(lootTable) }));
    }

    // Swift enum with associated values: case cell(Int) / case fn((Rng, Int) -> Int).
    public sealed class PaletteEntry
    {
        public enum Tag { cell, fn }
        public readonly Tag tag;
        public readonly int cellValue;
        public readonly Func<Rng, int, int> fnValue;

        private PaletteEntry(Tag tag, int cellValue, Func<Rng, int, int> fnValue)
        {
            this.tag = tag;
            this.cellValue = cellValue;
            this.fnValue = fnValue;
        }

        public static PaletteEntry cell(int v) => new PaletteEntry(Tag.cell, v, null);
        public static PaletteEntry fn(Func<Rng, int, int> f) => new PaletteEntry(Tag.fn, 0, f);
    }

    /// Stamp an ASCII template. layers bottom-to-top; row index = z.
    public void template(int ox, int oy, int oz, List<List<string>> layers,
                         Dictionary<char, PaletteEntry> palette, int rot = 0)
    {
        for (int ly = 0; ly < layers.Count; ly++)
        {
            var rows = layers[ly];
            for (int lz = 0; lz < rows.Count; lz++)
            {
                var row = rows[lz];
                for (int lx = 0; lx < row.Length; lx++)
                {
                    var ch = row[lx];
                    if (ch == ' ') continue;
                    int c;
                    if (ch == '.') { c = 0; }
                    else
                    {
                        if (!palette.TryGetValue(ch, out var p)) continue;
                        switch (p.tag)
                        {
                            case PaletteEntry.Tag.cell: c = p.cellValue; break;
                            default: c = p.fnValue(rng, rot); break;
                        }
                    }
                    int wx, wz;
                    switch (rot & 3)
                    {
                        case 0: wx = ox + lx; wz = oz + lz; break;
                        case 1: wx = ox - lz; wz = oz + lx; break;
                        case 2: wx = ox - lx; wz = oz - lz; break;
                        default: wx = ox + lz; wz = oz - lx; break;
                    }
                    s.set(wx, oy + ly, wz, (ushort)c);
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Module-level free functions + state.
// ---------------------------------------------------------------------------
public static class StructFrameworkGlobals
{
    /// rotate a horizontal facing (0=N 1=S 2=W 3=E) by template rotation
    public static int rotF(int facing, int rot)
    {
        var cw = new[] { 3, 2, 0, 1 }; // N→E, S→W, W→N, E→S
        var f = facing;
        for (int i = 0; i < (rot & 3); i++) f = cw[f];
        return f;
    }

    // -----------------------------------------------------------------------
    // Registry + chunk build
    // -----------------------------------------------------------------------
    public static List<StructureDef> STRUCTURES = new List<StructureDef>();
    public static void registerStructure(StructureDef def) { STRUCTURES.Add(def); }

    private static Dictionary<string, StructurePlan?> planCache = new Dictionary<string, StructurePlan?>();
    private static readonly object planCacheLock = new object();

    public static (int, int) structureOriginFor(StructureDef def, uint seed, int rcx, int rcz)
    {
        var rng = new RandomX(hash2(seed, rcx, rcz, def.salt));
        var range = Math.Max(1, def.spacing - def.separation);
        return (rcx * def.spacing + rng.nextInt(range), rcz * def.spacing + rng.nextInt(range));
    }

    public static StructurePlan? getPlan(StructureDef def, GenCtx ctx, int ocx, int ocz)
    {
        var key = $"{ctx.dim}:{def.id}:{ocx}:{ocz}";
        lock (planCacheLock)
        {
            if (planCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }
        var rng = new Rng(hash2(ctx.seed, ocx, ocz, def.salt ^ 0x5757));
        StructurePlan? plan = null;
        if (def.check(ctx, ocx, ocz, rng))
        {
            plan = def.plan(ctx, ocx, ocz, new Rng(hash2(ctx.seed, ocx, ocz, def.salt ^ 0x1234)));
        }
        lock (planCacheLock)
        {
            if (planCache.Count > 600)
            {
                planCache.Clear(); // recompute is deterministic; policy is correctness-neutral
            }
            planCache[key] = plan;
        }
        return plan;
    }

    public static List<StructRef> buildStructuresForChunk(GenCtx ctx, int cx, int cz, ChunkSink sink, List<StructureDef> dimStructures)
    {
        var refs = new List<StructRef>();
        int chunkX0 = cx * 16, chunkZ0 = cz * 16;
        foreach (var def in dimStructures)
        {
            var r = def.maxRadiusChunks;
            int rc0x = floorDiv(cx - r, def.spacing), rc1x = floorDiv(cx + r, def.spacing);
            int rc0z = floorDiv(cz - r, def.spacing), rc1z = floorDiv(cz + r, def.spacing);
            for (int rcz = rc0z; rcz <= rc1z; rcz++)
            {
                for (int rcx = rc0x; rcx <= rc1x; rcx++)
                {
                    var (ocx, ocz) = structureOriginFor(def, ctx.seed, rcx, rcz);
                    if (Math.Abs(ocx - cx) > r || Math.Abs(ocz - cz) > r) continue;
                    var planOpt = getPlan(def, ctx, ocx, ocz);
                    if (planOpt == null) continue;
                    var plan = planOpt.Value;
                    for (int pi = 0; pi < plan.pieces.Count; pi++)
                    {
                        var piece = plan.pieces[pi];
                        // does the piece intersect this chunk?
                        if (piece.x1 < chunkX0 || piece.x0 > chunkX0 + 15 || piece.z1 < chunkZ0 || piece.z0 > chunkZ0 + 15) continue;
                        // rng is a pure function of (structure, piece) — NEVER the
                        // target chunk — so a piece spanning a chunk border draws the
                        // identical stream in both rebuilds (rails/decay/chests used
                        // to discontinue exactly at chunk seams)
                        var b = new Builder(sink, new Rng(hash2(ctx.seed, unchecked(ocx * 1_000_003 + pi), unchecked(ocz * 31 - pi), def.salt ^ 0x9999)));
                        piece.build(b);
                    }
                    if (plan.@ref != null)
                    {
                        var rf = plan.@ref.Value;
                        if (!(rf.x1 < chunkX0 || rf.x0 > chunkX0 + 15 || rf.z1 < chunkZ0 || rf.z0 > chunkZ0 + 15))
                        {
                            refs.Add(new StructRef(id: def.id, x0: rf.x0, y0: rf.y0, z0: rf.z0, x1: rf.x1, y1: rf.y1, z1: rf.z1));
                        }
                    }
                }
            }
        }
        return refs;
    }

    /// simple piece helper
    public static StructPiece piece(int x0, int y0, int z0, int x1, int y1, int z1, Action<Builder> build)
        => new StructPiece(x0, y0, z0, x1, y1, z1, build);

    /// stronghold ring positions — pure function of seed, also used by eyes of ender
    public static List<(int, int)> strongholdPositions(uint seed)
    {
        var rng = new RandomX(hash2(seed, 0, 0, 0x57A0));
        var @out = new List<(int, int)>();
        var baseAngle = rng.nextFloat() * Math.PI * 2;
        for (int ring = 0; ring < 3; ring++)
        {
            var count = ring == 0 ? 3 : ring == 1 ? 6 : 10;
            var radius = (double)(1280 + ring * 3072 + rng.nextInt(640));
            for (int i = 0; i < count; i++)
            {
                var ang = baseAngle + (double)ring * 0.7 + ((double)i / (double)count) * Math.PI * 2 + (rng.nextFloat() - 0.5) * 0.3;
                var dist = radius + (double)rng.nextInt(512);
                @out.Add(((int)Math.Floor(detCos(ang) * dist / 16), (int)Math.Floor(detSin(ang) * dist / 16)));
            }
        }
        return @out;
    }
}

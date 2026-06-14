// Feature placement — Every tree, plant patch, geode,
// iceberg, dripstone cluster, sculk patch, nether & ocean feature.
// Deterministic per origin chunk; RNG call order mirrors baseline exactly (including
// short-circuit evaluation), so features crossing chunk borders generate
// identically from every side — and identically to the golden baselines.
//
// Module-global free functions/vars of Features.swift live in the static class
// `FeaturesGlobals` (surface via `global using static PebbleCore.FeaturesGlobals`).
// Namespace-level types declared here: BEValue, BESpec, EntitySpec, ChunkSink
// (the latter three / the interface are referenced by StructFramework.cs).
//
// External (defined in sibling Gen files): SEA (Terrain), Biome (Biomes),
// chunkRandom/hash2 (Rng), cell/SOLID/OPAQUE (BlockCaches), bid/bidOpt/B (Reg),
// detRound/detSin/detCos (DetMath).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public sealed class BEValue : IEquatable<BEValue>
{
    public enum Tag { num, str, @bool }

    public readonly Tag tag;
    private readonly double numValue;
    private readonly string strValue;
    private readonly bool boolValue;

    private BEValue(Tag tag, double n, string s, bool b)
    {
        this.tag = tag;
        numValue = n;
        strValue = s;
        boolValue = b;
    }

    public static BEValue num(double v) => new BEValue(Tag.num, v, null, false);
    public static BEValue str(string v) => new BEValue(Tag.str, 0, v, false);
    public static BEValue @bool(bool v) => new BEValue(Tag.@bool, 0, null, v);

    public double numVal => numValue;
    public string strVal => strValue;
    public bool boolVal => boolValue;

    public bool Equals(BEValue other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (tag != other.tag) return false;
        switch (tag)
        {
            case Tag.num: return numValue == other.numValue;
            case Tag.str: return strValue == other.strValue;
            case Tag.@bool: return boolValue == other.boolValue;
            default: return false;
        }
    }

    public override bool Equals(object obj) => obj is BEValue v && Equals(v);

    public override int GetHashCode()
    {
        switch (tag)
        {
            case Tag.num: return numValue.GetHashCode();
            case Tag.str: return strValue == null ? 0 : strValue.GetHashCode();
            case Tag.@bool: return boolValue.GetHashCode();
            default: return 0;
        }
    }

    public static bool operator ==(BEValue a, BEValue b)
    {
        if (a is null) return b is null;
        return a.Equals(b);
    }

    public static bool operator !=(BEValue a, BEValue b) => !(a == b);
}

public struct BESpec
{
    public readonly int x, y, z;
    public readonly string kind;
    public readonly Dictionary<string, BEValue> data;

    public BESpec(int x, int y, int z, string kind, Dictionary<string, BEValue> data = null)
    {
        this.x = x; this.y = y; this.z = z;
        this.kind = kind;
        this.data = data ?? new Dictionary<string, BEValue>();
    }
}

public struct EntitySpec
{
    public readonly string mob;
    public readonly double x, y, z;
    public readonly Dictionary<string, BEValue> data;

    public EntitySpec(string mob, double x, double y, double z, Dictionary<string, BEValue> data = null)
    {
        this.mob = mob;
        this.x = x; this.y = y; this.z = z;
        this.data = data ?? new Dictionary<string, BEValue>();
    }
}

public interface ChunkSink
{
    int cx { get; }
    int cz { get; }
    int minY { get; }
    int maxY { get; }
    /// clipped write, world coords
    void set(int x, int y, int z, ushort c);
    /// read; outside chunk returns -1
    int get(int x, int y, int z);
    /// top solid y within chunk; outside chunk uses noise estimate
    int topY(int x, int z);
    void addBlockEntity(BESpec spec);
    void addEntity(EntitySpec spec);
}

public static class FeaturesGlobals
{
    private const int AIR = 0;
    private static int WATER_CELL => (int)cell(B.water);

    // negative cells (= outside chunk, -1) classify as nothing — mirrors deterministic `>>> 4`
    // turning -1 into a huge index whose table lookups are undefined/falsy
    private static bool isSoil(int c)
    {
        if (c < 0) return false;
        ushort id = (ushort)(c >> 4);
        return id == B.grass_block || id == B.dirt || id == B.coarse_dirt || id == B.podzol ||
            id == B.mycelium || id == B.rooted_dirt || id == B.moss_block || id == B.mud || id == B.farmland;
    }

    private static bool isSand(int c)
    {
        if (c < 0) return false;
        ushort id = (ushort)(c >> 4);
        return id == B.sand || id == B.red_sand;
    }

    private static bool isStoneLike(int c)
    {
        if (c < 0) return false;
        ushort id = (ushort)(c >> 4);
        return id == B.stone || id == B.deepslate || id == B.andesite || id == B.diorite ||
            id == B.granite || id == B.tuff || id == B.gravel || id == B.dirt;
    }

    private static ushort idOf(int c) => unchecked((ushort)(c >> 4));
    private static bool solidId(ushort id) => SOLID[(int)id] == 1;
    private static bool opaqueId(ushort id) => OPAQUE[(int)id] == 1;

    // ---------------------------------------------------------------------------
    // Trees
    // ---------------------------------------------------------------------------
    private static void leafBlob(ChunkSink s, ref RandomX rng, int x, int y, int z, ushort leaves, double rx, int ry)
    {
        for (int dy = -ry; dy <= ry; dy++)
        {
            double r = rx - (double)Math.Abs(dy) * (rx / (double)(ry + 1)) * 0.7;
            int cr = (int)Math.Ceiling(r);
            if (cr < 0) continue;
            for (int dz = -cr; dz <= cr; dz++)
            {
                for (int dx = -cr; dx <= cr; dx++)
                {
                    double d = (double)(dx * dx + dz * dz);
                    if (d > r * r + 0.2) continue;
                    if (Math.Abs(dx) == cr && Math.Abs(dz) == cr && rng.nextFloat() < 0.5) continue;
                    int c = s.get(x + dx, y + dy, z + dz);
                    if (c == 0 || c == -1) s.set(x + dx, y + dy, z + dz, leaves);
                }
            }
        }
    }

    public static void genOakTree(ChunkSink s, ref RandomX rng, int x, int y, int z, bool fancy = false, double beeChance = 0)
    {
        int h = fancy ? 6 + rng.nextInt(5) : 4 + rng.nextInt(3);
        ushort log = cell(B.oak_log), leaves = cell(B.oak_leaves, 4);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
        leafBlob(s, ref rng, x, y + h - 1, z, leaves, 2.5, 2);
        s.set(x, y + h, z, leaves);
        if (fancy)
        {
            // a few branches
            int branches = 2 + rng.nextInt(3);
            for (int _b = 0; _b < branches; _b++)
            {
                int by = y + 3 + rng.nextInt(h - 4);
                int dx = rng.nextInt(3) - 1, dz = rng.nextInt(3) - 1;
                if (dx == 0 && dz == 0) continue;
                s.set(x + dx, by, z + dz, log);
                leafBlob(s, ref rng, x + dx * 2, by + 1, z + dz * 2, leaves, 2, 1);
            }
        }
        s.set(x, y - 1, z, cell(B.dirt));
        if (beeChance > 0 && rng.nextFloat() < beeChance)
        {
            int bx = x + (rng.nextBoolean() ? 1 : -1);
            if (s.get(bx, y + 1, z) <= 0)
            {
                s.set(bx, y + 1, z, cell(B.bee_nest, 1));
                s.addBlockEntity(new BESpec(x: bx, y: y + 1, z: z, kind: "beehive", data: new Dictionary<string, BEValue> { ["bees"] = BEValue.num(3) }));
            }
        }
    }

    public static void genBirchTree(ChunkSink s, ref RandomX rng, int x, int y, int z, bool tall = false)
    {
        int h = (tall ? 7 : 5) + rng.nextInt(3);
        ushort log = cell(B.birch_log), leaves = cell(B.birch_leaves, 4);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
        leafBlob(s, ref rng, x, y + h - 1, z, leaves, 2.3, 2);
        s.set(x, y + h, z, leaves);
        s.set(x, y - 1, z, cell(B.dirt));
    }

    public static void genSpruceTree(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        int h = 6 + rng.nextInt(4);
        ushort log = cell(B.spruce_log), leaves = cell(B.spruce_leaves, 4);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
        int r = 1;
        int dy = h;
        while (dy >= 2)
        {
            int rr = dy == h ? 0 : r;
            if (rr >= 0)
            {
                for (int dz = -rr; dz <= rr; dz++)
                {
                    for (int dx = -rr; dx <= rr; dx++)
                    {
                        if (Math.Abs(dx) == rr && Math.Abs(dz) == rr && rr > 1) continue;
                        if (dx == 0 && dz == 0 && dy < h) continue;
                        int c = s.get(x + dx, y + dy, z + dz);
                        if (c == 0 || c == -1) s.set(x + dx, y + dy, z + dz, leaves);
                    }
                }
            }
            r = r >= (dy % 2 == 0 ? 2 : 3) ? 1 : r + 1;
            if ((double)dy < (double)h * 0.4) r = Math.Min(r, 2);
            dy -= 1;
        }
        s.set(x, y + h, z, leaves);
        s.set(x, y - 1, z, cell(B.dirt));
    }

    public static void genMegaSpruce(ChunkSink s, ref RandomX rng, int x, int y, int z, bool pine)
    {
        int h = 18 + rng.nextInt(10);
        ushort log = cell(B.spruce_log), leaves = cell(B.spruce_leaves, 4);
        for (int i = 0; i < h; i++)
        {
            s.set(x, y + i, z, log); s.set(x + 1, y + i, z, log);
            s.set(x, y + i, z + 1, log); s.set(x + 1, y + i, z + 1, log);
        }
        int leafStart = pine ? h - 5 : (int)Math.Floor((double)h * 0.35);
        int r = pine ? 2 : 1;
        int dy = h + 1;
        while (dy >= leafStart)
        {
            int rr = dy > h ? 1 : r;
            for (int dz = -rr; dz <= rr + 1; dz++)
            {
                for (int dx = -rr; dx <= rr + 1; dx++)
                {
                    int ex = dx > 0 ? dx - 1 : dx, ez = dz > 0 ? dz - 1 : dz;
                    if (ex * ex + ez * ez > rr * rr + 1) continue;
                    int c = s.get(x + dx, y + dy, z + dz);
                    if (c == 0 || c == -1) s.set(x + dx, y + dy, z + dz, leaves);
                }
            }
            if (!pine) { r = r > 3 ? 1 : r + 1; }
            else { r = Math.Min(4, r + (dy % 2)); }
            dy -= 1;
        }
        for (int dx = -1; dx <= 2; dx++)
        {
            for (int dz = -1; dz <= 2; dz++)
            {
                if (rng.nextFloat() < 0.5) s.set(x + dx, y - 1, z + dz, cell(B.podzol));
            }
        }
    }

    public static void genJungleTree(ChunkSink s, ref RandomX rng, int x, int y, int z, bool mega)
    {
        ushort log = cell(B.jungle_log), leaves = cell(B.jungle_leaves, 4);
        if (mega)
        {
            int h = 20 + rng.nextInt(12);
            for (int i = 0; i < h; i++)
            {
                s.set(x, y + i, z, log); s.set(x + 1, y + i, z, log);
                s.set(x, y + i, z + 1, log); s.set(x + 1, y + i, z + 1, log);
                // vines on trunk
                if (rng.nextFloat() < 0.3) s.set(x - 1, y + i, z, cell(B.vine, 8));
                if (rng.nextFloat() < 0.3) s.set(x + 2, y + i, z, cell(B.vine, 4));
            }
            leafBlob(s, ref rng, x, y + h, z, leaves, 4, 2);
            leafBlob(s, ref rng, x + 1, y + h - 4 - rng.nextInt(4), z + 1, leaves, 3, 1);
        }
        else
        {
            int h = 5 + rng.nextInt(6);
            for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
            leafBlob(s, ref rng, x, y + h, z, leaves, 2.5, 1);
            // cocoa
            if (rng.nextFloat() < 0.3)
            {
                int f = rng.nextInt(4);
                int dx = (new[] { 0, 0, -1, 1 })[f], dz = (new[] { -1, 1, 0, 0 })[f];
                int cy = y + h - 2 - rng.nextInt(2);
                if (s.get(x + dx, cy, z + dz) <= 0)
                {
                    s.set(x + dx, cy, z + dz, cell(B.cocoa, ((new[] { 1, 0, 3, 2 })[f] | (rng.nextInt(3) << 2))));
                }
            }
        }
        s.set(x, y - 1, z, cell(B.dirt));
    }

    public static void genAcaciaTree(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        ushort log = cell(B.acacia_log), leaves = cell(B.acacia_leaves, 4);
        int h = 4 + rng.nextInt(3);
        int px = x, pz = z;
        int dx = rng.nextInt(3) - 1, dz = rng.nextInt(3) - 1;
        for (int i = 0; i < h; i++)
        {
            s.set(px, y + i, pz, log);
            if (i >= h - 2) { px += dx; pz += dz; }
        }
        // flat canopy
        for (int lz = -2; lz <= 2; lz++)
        {
            for (int lx = -2; lx <= 2; lx++)
            {
                if (Math.Abs(lx) == 2 && Math.Abs(lz) == 2) continue;
                int c = s.get(px + lx, y + h, pz + lz);
                if (c == 0 || c == -1) s.set(px + lx, y + h, pz + lz, leaves);
                if (Math.Abs(lx) <= 1 && Math.Abs(lz) <= 1 && Math.Abs(lx) + Math.Abs(lz) <= 1)
                {
                    int c2 = s.get(px + lx, y + h + 1, pz + lz);
                    if (c2 == 0 || c2 == -1) s.set(px + lx, y + h + 1, pz + lz, leaves);
                }
            }
        }
        s.set(x, y - 1, z, cell(B.dirt));
    }

    public static void genDarkOakTree(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        ushort log = cell(B.dark_oak_log), leaves = cell(B.dark_oak_leaves, 4);
        int h = 6 + rng.nextInt(3);
        for (int i = 0; i < h; i++)
        {
            s.set(x, y + i, z, log); s.set(x + 1, y + i, z, log);
            s.set(x, y + i, z + 1, log); s.set(x + 1, y + i, z + 1, log);
        }
        for (int dy = 0; dy <= 2; dy++)
        {
            double r = dy == 2 ? 2 : 3.5;
            int cr = (int)Math.Ceiling(r);
            for (int dz = -cr; dz <= cr + 1; dz++)
            {
                for (int dx = -cr; dx <= cr + 1; dx++)
                {
                    int ex = dx > 0 ? dx - 1 : dx, ez = dz > 0 ? dz - 1 : dz;
                    if ((double)(ex * ex + ez * ez) > r * r) continue;
                    int c = s.get(x + dx, y + h - 1 + dy, z + dz);
                    if (c == 0 || c == -1) s.set(x + dx, y + h - 1 + dy, z + dz, leaves);
                }
            }
        }
        for (int dx = 0; dx <= 1; dx++) { for (int dz = 0; dz <= 1; dz++) { s.set(x + dx, y - 1, z + dz, cell(B.dirt)); } }
    }

    public static void genCherryTree(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        ushort log = cell(B.cherry_log), leaves = cell(B.cherry_leaves, 4);
        int h = 4 + rng.nextInt(3);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
        // two arched branches
        int branches = 2 + rng.nextInt(2);
        for (int _b = 0; _b < branches; _b++)
        {
            double ang = rng.nextFloat() * Math.PI * 2;
            int len = 2 + rng.nextInt(2);
            int bx = x, bz = z, by = y + h - 1;
            for (int i = 0; i < len; i++)
            {
                bx += (int)detRound(detCos(ang));
                bz += (int)detRound(detSin(ang));
                by += i == 0 ? 1 : (rng.nextFloat() < 0.5 ? 1 : 0);
                s.set(bx, by, bz, log);
            }
            leafBlob(s, ref rng, bx, by + 1, bz, leaves, 2.8, 1);
            leafBlob(s, ref rng, bx, by + 2, bz, leaves, 1.6, 0);
        }
        s.set(x, y - 1, z, cell(B.dirt));
    }

    public static void genMangroveTree(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        ushort log = cell(B.mangrove_log), leaves = cell(B.mangrove_leaves, 4), roots = cell(B.mangrove_roots);
        int stiltH = 2 + rng.nextInt(2);
        int waterC = WATER_CELL;
        // stilt roots
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            for (int i = 0; i <= stiltH; i++)
            {
                int c = s.get(x + dx, y + i - 1, z + dz);
                if (c == 0 || c == waterC || c == -1) s.set(x + dx, y + i - 1, z + dz, roots);
                if (i == 0)
                {
                    // anchor down into ground/water
                    for (int dd = 1; dd <= 2; dd++)
                    {
                        int cc = s.get(x + dx, y - 1 - dd, z + dz);
                        if (cc == 0 || cc == waterC) s.set(x + dx, y - 1 - dd, z + dz, roots);
                        else break;
                    }
                }
            }
        }
        int h = stiltH + 4 + rng.nextInt(3);
        for (int i = stiltH - 1; i < h; i++) s.set(x, y + i, z, log);
        leafBlob(s, ref rng, x, y + h, z, leaves, 2.8, 1);
        leafBlob(s, ref rng, x, y + h + 1, z, leaves, 1.8, 0);
        // hanging propagules
        for (int _p = 0; _p < 3; _p++)
        {
            int px = x + rng.nextInt(5) - 2, pz = z + rng.nextInt(5) - 2;
            int py = y + h - rng.nextInt(2);
            if (s.get(px, py, pz) == (int)cell(B.mangrove_leaves, 4) && s.get(px, py - 1, pz) <= 0)
            {
                s.set(px, py - 1, pz, cell(B.mangrove_propagule, 8 | 4));
            }
        }
    }

    public static void genSwampOak(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        int h = 5 + rng.nextInt(3);
        ushort log = cell(B.oak_log), leaves = cell(B.oak_leaves, 4);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
        leafBlob(s, ref rng, x, y + h - 1, z, leaves, 3, 2);
        // vines hanging off leaves
        for (int _v = 0; _v < 8; _v++)
        {
            int vx = x + rng.nextInt(7) - 3, vz = z + rng.nextInt(7) - 3;
            int vy = y + h - 1 - rng.nextInt(2);
            if (idOf(s.get(vx, vy, vz)) == B.oak_leaves && s.get(vx, vy - 1, vz) <= 0)
            {
                int len = 1 + rng.nextInt(3);
                for (int v = 1; v <= len; v++)
                {
                    if (s.get(vx, vy - v, vz) <= 0) s.set(vx, vy - v, vz, cell(B.vine, (1 << rng.nextInt(4))));
                }
            }
        }
        s.set(x, y - 1, z, cell(B.dirt));
    }

    public static void genAzaleaTree(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        int h = 4 + rng.nextInt(2);
        ushort log = cell(B.oak_log);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, log);
        leafBlob(s, ref rng, x, y + h - 1, z, cell(B.azalea_leaves, 4), 2.4, 1);
        for (int _l = 0; _l < 4; _l++)
        {
            int lx = x + rng.nextInt(5) - 2, lz = z + rng.nextInt(5) - 2;
            if (idOf(s.get(lx, y + h, lz)) == B.azalea_leaves && rng.nextFloat() < 0.5)
            {
                s.set(lx, y + h, lz, cell(B.flowering_azalea_leaves, 4));
            }
        }
        s.set(x, y - 1, z, cell(B.rooted_dirt));
    }

    public static void genHugeMushroom(ChunkSink s, ref RandomX rng, int x, int y, int z, bool red)
    {
        ushort stem = cell(B.mushroom_stem);
        ushort capBlock = red ? cell(B.red_mushroom_block) : cell(B.brown_mushroom_block);
        int h = 4 + rng.nextInt(3);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, stem);
        if (red)
        {
            for (int dy = 0; dy < 2; dy++)
            {
                int r = dy == 1 ? 1 : 2;
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dy == 0 && Math.Abs(dx) == r && Math.Abs(dz) == r) continue;
                        if (dy == 0 && Math.Abs(dx) < r && Math.Abs(dz) < r) continue;
                        s.set(x + dx, y + h - 1 + dy, z + dz, capBlock);
                    }
                }
            }
            s.set(x, y + h, z, capBlock);
        }
        else
        {
            for (int dz = -3; dz <= 3; dz++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (Math.Abs(dx) == 3 && Math.Abs(dz) == 3) continue;
                    s.set(x + dx, y + h, z + dz, capBlock);
                }
            }
        }
    }

    public static void genHugeFungus(ChunkSink s, ref RandomX rng, int x, int y, int z, bool crimson)
    {
        ushort stem = cell(crimson ? B.crimson_stem : B.warped_stem);
        ushort wart = cell(crimson ? B.nether_wart_block : B.warped_wart_block);
        ushort light = cell(B.shroomlight);
        int h = 5 + rng.nextInt(7);
        for (int i = 0; i < h; i++) s.set(x, y + i, z, stem);
        for (int dy = -2; dy <= 1; dy++)
        {
            int r = dy >= 0 ? (dy == 1 ? 1 : 2) : 3;
            int rr = dy == -2 ? 3 : dy == -1 ? 3 : r;
            for (int dz = -rr; dz <= rr; dz++)
            {
                for (int dx = -rr; dx <= rr; dx++)
                {
                    if (dx * dx + dz * dz > rr * rr + 1) continue;
                    int c = s.get(x + dx, y + h + dy, z + dz);
                    if (c == 0 || c == -1)
                    {
                        s.set(x + dx, y + h + dy, z + dz, rng.nextFloat() < 0.06 ? light : wart);
                    }
                }
            }
        }
    }

    public static void genChorus(ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        ushort plant = cell(B.chorus_plant);
        // ref rng threaded as a parameter (C# can't capture a ref param in a local function)
        void grow(int px, int py, int pz, int depth, ref RandomX rng)
        {
            s.set(px, py, pz, plant);
            int h = 1 + rng.nextInt(3);
            for (int i = 1; i <= h; i++) s.set(px, py + i, pz, plant);
            if (depth < 3 && rng.nextFloat() < 0.8)
            {
                int branches = 1 + rng.nextInt(3);
                for (int _b = 0; _b < branches; _b++)
                {
                    int f = rng.nextInt(4);
                    int nx = px + (new[] { 0, 0, -1, 1 })[f], nz = pz + (new[] { -1, 1, 0, 0 })[f];
                    if (s.get(nx, py + h, nz) <= 0) grow(nx, py + h, nz, depth + 1, ref rng);
                }
            }
            else
            {
                s.set(px, py + h + 1, pz, cell(B.chorus_flower, 0));
            }
        }
        grow(x, y, z, 0, ref rng);
    }

    // ---------------------------------------------------------------------------
    // Feature dispatch
    // ---------------------------------------------------------------------------
    private static readonly Dictionary<string, ushort[]> FLOWER_SETS = new Dictionary<string, ushort[]>
    {
        ["plains"] = new ushort[] { B.dandelion, B.poppy, B.azure_bluet, B.oxeye_daisy, B.cornflower },
        ["forest"] = new ushort[] { B.dandelion, B.poppy, B.lily_of_the_valley },
        ["flower_forest"] = new ushort[] { B.dandelion, B.poppy, B.allium, B.azure_bluet, B.red_tulip, B.orange_tulip,
                          B.white_tulip, B.pink_tulip, B.oxeye_daisy, B.cornflower, B.lily_of_the_valley,
                          B.peony, B.lilac, B.rose_bush },
        ["meadow"] = new ushort[] { B.dandelion, B.poppy, B.allium, B.azure_bluet, B.oxeye_daisy, B.cornflower },
        ["jungle"] = new ushort[] { B.dandelion, B.poppy },
        ["cherry"] = new ushort[] { B.pink_tulip, B.allium },
        ["swamp"] = new ushort[] { B.blue_orchid },
    };

    public static void runFeature(string key, ChunkSink s, ref RandomX rng, int ocx, int ocz, uint seed, Func<int, int, int> biomeAt)
    {
        string[] parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
        string name = parts[0];
        int baseX = ocx * 16, baseZ = ocz * 16;
        int intArg(int i, int def = 0) => parts.Length > i ? (int.TryParse(parts[i], out int pv) ? pv : def) : def;
        (int, int) randPos(ref RandomX rng2)
        {
            int x = baseX + rng2.nextInt(16);
            int z = baseZ + rng2.nextInt(16);
            return (x, z);
        }

        switch (name)
        {
            case "trees":
                {
                    string kind = parts[1];
                    int count = intArg(2);
                    double extra = parts.Length > 3 ? (double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pv) ? pv : 0.1) : 0.1;
                    int n = count;
                    if (rng.nextFloat() < extra) n += 1;
                    if (count == 0 && n == 0 && rng.nextFloat() < extra * 2) n = 1;
                    for (int _i = 0; _i < n; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        if (y <= s.minY || y > 250) continue;
                        int ground = s.get(x, y - 1, z);
                        if (ground != -1 && !isSoil(ground)) continue;
                        if (ground == -1)
                        {
                            // base lies outside this sink: the soil can't be read, so be
                            // conservative — at/below sea level it's water or beach, and a
                            // blind paint here is exactly the floating-canopy fragment bug
                            if (y <= SEA + 1) continue;
                            int biome = biomeAt(x, z);
                            if (biome == (int)Biome.river || biome == (int)Biome.beach
                                || biome == (int)Biome.snowyBeach || biome == (int)Biome.stonyShore) continue;
                        }
                        placeTreeKind(kind, s, ref rng, x, y, z);
                    }
                    break;
                }

            case "patch":
                {
                    string blockName = parts[1];
                    int count = intArg(2);
                    ushort? idOpt = bidOpt(blockName);
                    if (idOpt == null) return;
                    ushort id = idOpt.Value;
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int ground = s.get(x, y - 1, z);
                        if (ground == -1 || (!isSoil(ground) && !(blockName == "dead_bush" && (isSand(ground) || idOf(ground) == B.terracotta)))) continue;
                        if (s.get(x, y, z) != 0) continue;
                        if (blockName == "tall_grass" || blockName == "large_fern" || blockName == "sunflower")
                        {
                            s.set(x, y, z, cell(id, 0));
                            s.set(x, y + 1, z, cell(id, 1));
                        }
                        else
                        {
                            s.set(x, y, z, cell(id));
                        }
                    }
                    break;
                }

            case "flowers":
                {
                    ushort[] set = FLOWER_SETS.TryGetValue(parts[1], out ushort[] fs) ? fs : FLOWER_SETS["plains"];
                    int count = intArg(2);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int ground = s.get(x, y - 1, z);
                        if (ground == -1 || !isSoil(ground) || s.get(x, y, z) != 0) continue;
                        // flower gradient: same flower in local area (seeded — a fixed
                        // salt made every world grow the identical flower map)
                        ushort fl = set[(int)((long)hash2(seed, x >> 3, z >> 3, 7) % set.Length)];
                        if (fl == B.peony || fl == B.lilac || fl == B.rose_bush)
                        {
                            s.set(x, y, z, cell(fl, 0));
                            s.set(x, y + 1, z, cell(fl, 1));
                        }
                        else
                        {
                            s.set(x, y, z, cell(fl));
                        }
                    }
                    break;
                }

            case "sugar_cane":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int ground = s.get(x, y - 1, z);
                        if (ground == -1 || (!isSoil(ground) && !isSand(ground))) continue;
                        // needs adjacent water
                        bool nearWater = false;
                        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                        {
                            if (idOf(s.get(x + dx, y - 1, z + dz)) == B.water) { nearWater = true; break; }
                        }
                        if (!nearWater || s.get(x, y, z) != 0) continue;
                        int h = 2 + rng.nextInt(3);
                        for (int j = 0; j < h; j++) s.set(x, y + j, z, cell(B.sugar_cane));
                    }
                    break;
                }

            case "cactus":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        if (!isSand(s.get(x, y - 1, z))) continue;
                        bool clear = true;
                        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                        {
                            int c = s.get(x + dx, y, z + dz);
                            if (c > 0 && solidId(idOf(c))) { clear = false; break; }
                        }
                        if (!clear || s.get(x, y, z) != 0) continue;
                        int h = 1 + rng.nextInt(3);
                        for (int j = 0; j < h; j++) s.set(x, y + j, z, cell(B.cactus));
                    }
                    break;
                }

            case "pumpkin":
            case "melon":
                {
                    int rarity = intArg(1);
                    if (rng.nextInt(rarity) != 0) return;
                    var (x, z) = randPos(ref rng);
                    int y = s.topY(x, z);
                    if (!isSoil(s.get(x, y - 1, z)) || s.get(x, y, z) != 0) return;
                    s.set(x, y, z, cell(name == "pumpkin" ? B.pumpkin : B.melon));
                    break;
                }

            case "lily_pad":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        for (int y = 62; y <= 64; y++)
                        {
                            if (idOf(s.get(x, y, z)) == B.water && s.get(x, y + 1, z) == 0)
                            {
                                s.set(x, y + 1, z, cell(B.lily_pad));
                                break;
                            }
                        }
                    }
                    break;
                }

            case "vines":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = 64 + rng.nextInt(50);
                        for (int f = 0; f < 4; f++)
                        {
                            int dx = (new[] { 0, 0, -1, 1 })[f], dz = (new[] { -1, 1, 0, 0 })[f];
                            int wall = s.get(x + dx, y, z + dz);
                            if (wall > 0 && opaqueId(idOf(wall)) && s.get(x, y, z) == 0)
                            {
                                int len = 1 + rng.nextInt(4);
                                for (int v = 0; v < len; v++)
                                {
                                    if (s.get(x, y - v, z) == 0) s.set(x, y - v, z, cell(B.vine, (1 << f)));
                                }
                                break;
                            }
                        }
                    }
                    break;
                }

            case "berry_bush":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        if (isSoil(s.get(x, y - 1, z)) && s.get(x, y, z) == 0)
                        {
                            s.set(x, y, z, cell(B.sweet_berry_bush, (2 + rng.nextInt(2))));
                        }
                    }
                    break;
                }

            case "bamboo":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        if (!isSoil(s.get(x, y - 1, z))) continue;
                        int h = 6 + rng.nextInt(10);
                        for (int j = 0; j < h; j++)
                        {
                            if (s.get(x, y + j, z) != 0 && s.get(x, y + j, z) != -1) break;
                            int leavesMeta = j > h - 3 ? 2 : j > h - 5 ? 1 : 0;
                            s.set(x, y + j, z, cell(B.bamboo, (leavesMeta | 4)));
                        }
                    }
                    break;
                }

            case "bee_nest":
            case "cocoa":
            case "badlands_gold":
            case "emerald_ore":
                return;

            case "huge_mushroom":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        if (rng.nextFloat() < 0.6) continue;
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int g = s.get(x, y - 1, z);
                        if (g == -1 || (!isSoil(g) && idOf(g) != B.mycelium)) continue;
                        bool red = rng.nextBoolean();
                        genHugeMushroom(s, ref rng, x, y, z, red: red);
                    }
                    break;
                }

            case "huge_fungus":
                {
                    bool crimson = parts[1] == "crimson";
                    int count = intArg(2);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0) continue;
                        ushort g = idOf(s.get(x, y - 1, z));
                        if (g != (crimson ? B.crimson_nylium : B.warped_nylium)) continue;
                        if (rng.nextFloat() < 0.4) genHugeFungus(s, ref rng, x, y, z, crimson: crimson);
                    }
                    break;
                }

            case "nether_vegetation":
                {
                    bool crimson = parts[1] == "crimson";
                    int count = intArg(2);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0) continue;
                        ushort g = idOf(s.get(x, y - 1, z));
                        if (g != B.crimson_nylium && g != B.warped_nylium) continue;
                        double r = rng.nextFloat();
                        if (crimson)
                        {
                            s.set(x, y, z, cell(r < 0.7 ? B.crimson_roots : r < 0.9 ? B.crimson_fungus : B.warped_fungus));
                        }
                        else
                        {
                            s.set(x, y, z, cell(r < 0.55 ? B.warped_roots : r < 0.8 ? B.nether_sprouts : B.warped_fungus));
                        }
                    }
                    break;
                }

            case "weeping_vines":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = 40 + rng.nextInt(70);
                        int ceil = s.get(x, y + 1, z);
                        if (ceil > 0 && opaqueId(idOf(ceil)) && s.get(x, y, z) == 0)
                        {
                            int len = 2 + rng.nextInt(6);
                            for (int v = 0; v < len; v++)
                            {
                                if (s.get(x, y - v, z) == 0) s.set(x, y - v, z, cell(B.weeping_vines));
                                else break;
                            }
                        }
                    }
                    break;
                }

            case "twisting_vines":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0 || s.get(x, y, z) != 0) continue;
                        int len = 2 + rng.nextInt(5);
                        for (int v = 0; v < len; v++)
                        {
                            if (s.get(x, y + v, z) == 0) s.set(x, y + v, z, cell(B.twisting_vines));
                            else break;
                        }
                    }
                    break;
                }

            case "glowstone_cluster":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = 30 + rng.nextInt(80);
                        int ceil = s.get(x, y + 1, z);
                        if (ceil > 0 && idOf(ceil) == B.netherrack && s.get(x, y, z) == 0)
                        {
                            s.set(x, y, z, cell(B.glowstone));
                            for (int _g = 0; _g < 6; _g++)
                            {
                                int gx = x + rng.nextInt(3) - 1, gy = y - rng.nextInt(2), gz = z + rng.nextInt(3) - 1;
                                if (s.get(gx, gy, gz) == 0 && rng.nextFloat() < 0.7) s.set(gx, gy, gz, cell(B.glowstone));
                            }
                        }
                    }
                    break;
                }

            case "lava_spring":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.minY + 6 + rng.nextInt(100);
                        int c = s.get(x, y, z);
                        if (c > 0 && opaqueId(idOf(c)))
                        {
                            int airSides = 0;
                            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
                            {
                                if (s.get(x + dx, y + dy, z + dz) == 0) airSides += 1;
                            }
                            if (airSides == 1) s.set(x, y, z, cell(B.lava, 0));
                        }
                    }
                    break;
                }

            case "fire_patch":
            case "fire_patch_soul":
                {
                    int count = intArg(1);
                    bool soul = name == "fire_patch_soul";
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0) continue;
                        ushort g = idOf(s.get(x, y - 1, z));
                        if (soul ? (g == B.soul_sand || g == B.soul_soil) : g == B.netherrack)
                        {
                            s.set(x, y, z, cell(soul ? B.soul_fire : B.fire));
                        }
                    }
                    break;
                }

            case "magma_blob":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = 24 + rng.nextInt(16);
                        for (int _j = 0; _j < 8; _j++)
                        {
                            int bx = x + rng.nextInt(3) - 1, by = y + rng.nextInt(2) - 1, bz = z + rng.nextInt(3) - 1;
                            int c = s.get(bx, by, bz);
                            if (c > 0 && (idOf(c) == B.netherrack || idOf(c) == B.basalt)) s.set(bx, by, bz, cell(B.magma_block));
                        }
                    }
                    break;
                }

            case "basalt_pillar":
            case "basalt_column":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0) continue;
                        int h = name == "basalt_pillar" ? 4 + rng.nextInt(8) : 1 + rng.nextInt(4);
                        for (int j = 0; j < h; j++) s.set(x, y + j, z, cell(B.basalt));
                    }
                    break;
                }

            case "bone_spire":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        if (rng.nextFloat() < 0.7) continue;
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0) continue;
                        int h = 3 + rng.nextInt(5);
                        for (int j = 0; j < h; j++) s.set(x, y + j, z, cell(B.bone_block));
                    }
                    break;
                }

            case "delta":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y < 0) continue;
                        int r = 2 + rng.nextInt(4);
                        for (int dz = -r; dz <= r; dz++)
                        {
                            for (int dx = -r; dx <= r; dx++)
                            {
                                if (dx * dx + dz * dz > r * r) continue;
                                int g = s.get(x + dx, y - 1, z + dz);
                                if (g > 0 && opaqueId(idOf(g)) && s.get(x + dx, y, z + dz) == 0)
                                {
                                    s.set(x + dx, y - 1, z + dz, rng.nextFloat() < 0.85 ? cell(B.lava, 0) : cell(B.magma_block));
                                }
                            }
                        }
                    }
                    break;
                }

            case "brown_mushroom_nether":
            case "red_mushroom_nether":
                {
                    int count = intArg(1);
                    ushort id = name.StartsWith("brown") ? B.brown_mushroom : B.red_mushroom;
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = netherFloorY(s, x, z, ref rng);
                        if (y > 0 && s.get(x, y, z) == 0) s.set(x, y, z, cell(id));
                    }
                    break;
                }

            // --- ocean ---
            case "patch_water":
                {
                    string blockName = parts[1];
                    int count = intArg(2);
                    ushort id = bid(blockName);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = seafloorY(s, x, z);
                        if (y < 0) continue;
                        if (idOf(s.get(x, y, z)) == B.water)
                        {
                            if (blockName == "seagrass" && rng.nextFloat() < 0.3 && idOf(s.get(x, y + 1, z)) == B.water)
                            {
                                s.set(x, y, z, cell(B.tall_seagrass, 0));
                                s.set(x, y + 1, z, cell(B.tall_seagrass, 1));
                            }
                            else
                            {
                                s.set(x, y, z, cell(id));
                            }
                        }
                    }
                    break;
                }

            case "kelp":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = seafloorY(s, x, z);
                        if (y < 0) continue;
                        int maxH = 62 - y;
                        if (maxH < 3) continue;
                        int h = 3 + rng.nextInt(Math.Min(14, maxH));
                        for (int j = 0; j < h; j++)
                        {
                            if (idOf(s.get(x, y + j, z)) != B.water) break;
                            s.set(x, y + j, z, j == h - 1 ? cell(B.kelp, (rng.nextInt(16))) : cell(B.kelp_plant));
                        }
                    }
                    break;
                }

            case "sea_pickle":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = seafloorY(s, x, z);
                        if (y < 0) continue;
                        int g = s.get(x, y - 1, z);
                        if (g > 0 && solidId(idOf(g))) s.set(x, y, z, cell(B.sea_pickle, (rng.nextInt(4))));
                    }
                    break;
                }

            case "coral_reef":
                {
                    int count = intArg(1);
                    string[] corals = new[] { "tube", "brain", "bubble", "fire", "horn" };
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = seafloorY(s, x, z);
                        if (y < 0 || y > 56) continue;
                        string coral = corals[rng.nextInt(5)];
                        ushort blockC = cell(bid($"{coral}_coral_block"));
                        int kind = rng.nextInt(3);
                        if (kind == 0)
                        {
                            // claw/tree
                            int h = 2 + rng.nextInt(3);
                            for (int j = 0; j < h; j++) s.set(x, y + j, z, blockC);
                            for (int _c = 0; _c < 3; _c++)
                            {
                                int f = rng.nextInt(4);
                                int bx = x + (new[] { 0, 0, -1, 1 })[f], bz = z + (new[] { -1, 1, 0, 0 })[f];
                                s.set(bx, y + h - 1, bz, blockC);
                                if (rng.nextBoolean()) s.set(bx, y + h, bz, blockC);
                            }
                        }
                        else if (kind == 1)
                        {
                            // mushroom
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    s.set(x + dx, y + 1, z + dz, blockC);
                                    if (rng.nextFloat() < 0.6) s.set(x + dx, y + 2, z + dz, blockC);
                                }
                            }
                            s.set(x, y, z, blockC);
                        }
                        else
                        {
                            s.set(x, y, z, blockC);
                            s.set(x, y + 1, z, blockC);
                        }
                        // decorate with corals + fans
                        for (int _d = 0; _d < 6; _d++)
                        {
                            int dx2 = rng.nextInt(5) - 2, dz2 = rng.nextInt(5) - 2;
                            int ty = seafloorY(s, x + dx2, z + dz2);
                            if (ty > 0 && idOf(s.get(x + dx2, ty, z + dz2)) == B.water)
                            {
                                string c2 = corals[rng.nextInt(5)];
                                bool pick = rng.nextBoolean();
                                s.set(x + dx2, ty, z + dz2, cell(bid(pick ? $"{c2}_coral" : $"{c2}_coral_fan")));
                            }
                        }
                    }
                    break;
                }

            case "iceberg":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        if (rng.nextFloat() < 0.94) continue;
                        var (x, z) = randPos(ref rng);
                        int r = 4 + rng.nextInt(6);
                        int peak = 4 + rng.nextInt(8);
                        bool blue = rng.nextFloat() < 0.2;
                        for (int dy = -r; dy <= peak; dy++)
                        {
                            double rr = dy < 0 ? (double)r * (1 + (double)dy / (double)(r + 1) * 0.5) : (double)r * (1 - (double)dy / (double)(peak + 1));
                            int cr = (int)Math.Ceiling(rr);
                            if (cr < 0) continue;
                            for (int dz = -cr; dz <= cr; dz++)
                            {
                                for (int dx = -cr; dx <= cr; dx++)
                                {
                                    if ((double)(dx * dx + dz * dz) > rr * rr) continue;
                                    int y = 63 + dy;
                                    int c = s.get(x + dx, y, z + dz);
                                    if (c == WATER_CELL || c == 0 || c == -1)
                                    {
                                        s.set(x + dx, y, z + dz, blue ? cell(B.blue_ice) : (rng.nextFloat() < 0.1 ? cell(B.snow_block) : cell(B.packed_ice)));
                                    }
                                }
                            }
                        }
                    }
                    break;
                }

            case "ice_spike":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        if (idOf(s.get(x, y - 1, z)) != B.snow_block) continue;
                        bool tall = rng.nextFloat() < 0.1;
                        int h = tall ? 12 + rng.nextInt(20) : 6 + rng.nextInt(6);
                        int r = tall ? 2 : 1 + rng.nextInt(2);
                        for (int dy = 0; dy < h; dy++)
                        {
                            double rr = Math.Max(0, (double)r * (1 - (double)dy / (double)h));
                            int cr = (int)Math.Ceiling(rr);
                            for (int dz = -cr; dz <= cr; dz++)
                            {
                                for (int dx = -cr; dx <= cr; dx++)
                                {
                                    if ((double)(dx * dx + dz * dz) > rr * rr + 0.3) continue;
                                    s.set(x + dx, y + dy, z + dz, cell(B.packed_ice));
                                }
                            }
                        }
                    }
                    break;
                }

            case "ice_patch":
            case "powder_snow":
                {
                    int count = intArg(1);
                    ushort blk = name == "ice_patch" ? cell(B.packed_ice) : cell(B.powder_snow);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int r = 2 + rng.nextInt(3);
                        for (int dz = -r; dz <= r; dz++)
                        {
                            for (int dx = -r; dx <= r; dx++)
                            {
                                if (dx * dx + dz * dz > r * r) continue;
                                int ty = s.topY(x + dx, z + dz);
                                if (Math.Abs(ty - y) <= 1 && idOf(s.get(x + dx, ty - 1, z + dz)) == B.snow_block)
                                {
                                    s.set(x + dx, ty - 1, z + dz, blk);
                                }
                            }
                        }
                    }
                    break;
                }

            case "mossy_boulder":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        if (rng.nextFloat() < 0.7) continue;
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int r = 1 + rng.nextInt(2);
                        for (int dy = -1; dy <= r; dy++)
                        {
                            for (int dz = -r; dz <= r; dz++)
                            {
                                for (int dx = -r; dx <= r; dx++)
                                {
                                    if (dx * dx + dy * dy + dz * dz > r * r + 1) continue;
                                    s.set(x + dx, y + dy, z + dz, cell(B.mossy_cobblestone));
                                }
                            }
                        }
                    }
                    break;
                }

            case "hoodoo":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        if (rng.nextFloat() < 0.5) continue;
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        int h = 6 + rng.nextInt(14);
                        ushort[] bands = new ushort[] { B.terracotta, B.orange_terracotta, B.red_terracotta, B.white_terracotta, B.yellow_terracotta };
                        for (int dy = 0; dy < h; dy++)
                        {
                            int r = dy > h - 3 ? 0 : 1;
                            for (int dz = -r; dz <= r; dz++)
                            {
                                for (int dx = -r; dx <= r; dx++)
                                {
                                    s.set(x + dx, y + dy, z + dz, cell(bands[(y + dy) % bands.Length]));
                                }
                            }
                        }
                    }
                    break;
                }

            case "desert_well":
                {
                    if (rng.nextInt(500) != 0) return;
                    var (x, z) = randPos(ref rng);
                    int y = s.topY(x, z);
                    if (!isSand(s.get(x, y - 1, z))) return;
                    ushort ss = cell(B.sandstone), slab = cell(B.sandstone_slab);
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            s.set(x + dx, y - 1, z + dz, dx == 0 && dz == 0 ? cell(B.water, 0) : ss);
                            if (Math.Abs(dx) == 2 || Math.Abs(dz) == 2) s.set(x + dx, y, z + dz, slab);
                        }
                    }
                    // suspicious sand for archaeology
                    s.set(x + 1, y - 2, z, cell(B.suspicious_sand));
                    s.addBlockEntity(new BESpec(x: x + 1, y: y - 2, z: z, kind: "brushable", data: new Dictionary<string, BEValue> { ["lootTable"] = BEValue.str("desert_well_archaeology") }));
                    foreach (var (px, pz) in new[] { (-1, -1), (1, 1) })
                    {
                        s.set(x + px, y, z + pz, ss);
                        s.set(x + px, y + 1, z + pz, ss);
                        s.set(x + px, y + 2, z + pz, ss);
                    }
                    for (int dz = -1; dz <= 1; dz++) { for (int dx = -1; dx <= 1; dx++) { s.set(x + dx, y + 3, z + dz, slab); } }
                    break;
                }

            // --- caves ---
            case "dripstone_cluster":
            case "pointed_dripstone":
            case "dripstone_pool":
            case "moss_patch":
            case "lush_vegetation":
            case "glow_berries":
            case "spore_blossom":
            case "azalea_tree":
            case "big_dripleaf":
            case "small_dripleaf":
            case "clay_pool":
            case "sculk_patch":
            case "sculk_vein":
            case "sculk_shrieker":
            case "sculk_sensor":
                caveFeature(name, intArg(1), s, ref rng, ocx, ocz, biomeAt);
                break;

            case "chorus":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        if (rng.nextFloat() < 0.7) continue;
                        var (x, z) = randPos(ref rng);
                        int y = s.topY(x, z);
                        if (idOf(s.get(x, y - 1, z)) == B.end_stone && s.get(x, y, z) == 0)
                        {
                            genChorus(s, ref rng, x, y, z);
                        }
                    }
                    break;
                }

            case "clay_disk":
                {
                    int count = intArg(1);
                    for (int _i = 0; _i < count; _i++)
                    {
                        var (x, z) = randPos(ref rng);
                        int y = seafloorY(s, x, z);
                        if (y < 0) continue;
                        int r = 2 + rng.nextInt(3);
                        for (int dz = -r; dz <= r; dz++)
                        {
                            for (int dx = -r; dx <= r; dx++)
                            {
                                if (dx * dx + dz * dz > r * r) continue;
                                int g = s.get(x + dx, y - 1, z + dz);
                                if (g > 0 && (idOf(g) == B.sand || idOf(g) == B.dirt || idOf(g) == B.gravel))
                                {
                                    s.set(x + dx, y - 1, z + dz, cell(B.clay));
                                }
                            }
                        }
                    }
                    break;
                }

            default:
                return;
        }
    }

    public static void placeTreeKind(string kind, ChunkSink s, ref RandomX rng, int x, int y, int z)
    {
        switch (kind)
        {
            case "oak_sparse":
            case "oak_small": genOakTree(s, ref rng, x, y, z, fancy: false); break;
            case "oak_bee": genOakTree(s, ref rng, x, y, z, fancy: false, beeChance: 0.3); break;
            case "oak_birch":
                if (rng.nextFloat() < 0.2) { genBirchTree(s, ref rng, x, y, z); }
                else
                {
                    bool fancy = rng.nextFloat() < 0.1;
                    genOakTree(s, ref rng, x, y, z, fancy: fancy, beeChance: 0.01);
                }
                break;
            case "oak_spruce":
                if (rng.nextBoolean()) { genSpruceTree(s, ref rng, x, y, z); }
                else { genOakTree(s, ref rng, x, y, z, fancy: false); }
                break;
            case "birch": genBirchTree(s, ref rng, x, y, z); break;
            case "tall_birch": genBirchTree(s, ref rng, x, y, z, tall: true); break;
            case "spruce": genSpruceTree(s, ref rng, x, y, z); break;
            case "mega_spruce":
                if (rng.nextFloat() < 0.33) { genMegaSpruce(s, ref rng, x, y, z, pine: false); }
                else { genSpruceTree(s, ref rng, x, y, z); }
                break;
            case "mega_pine":
                if (rng.nextFloat() < 0.33) { genMegaSpruce(s, ref rng, x, y, z, pine: true); }
                else { genSpruceTree(s, ref rng, x, y, z); }
                break;
            case "jungle":
                if (rng.nextFloat() < 0.1) { genJungleTree(s, ref rng, x, y, z, mega: true); }
                else if (rng.nextFloat() < 0.15) { genOakTree(s, ref rng, x, y, z, fancy: false); }
                else { genJungleTree(s, ref rng, x, y, z, mega: false); }
                break;
            case "jungle_sparse": genJungleTree(s, ref rng, x, y, z, mega: false); break;
            case "acacia": genAcaciaTree(s, ref rng, x, y, z); break;
            case "dark_oak": genDarkOakTree(s, ref rng, x, y, z); break;
            case "cherry": genCherryTree(s, ref rng, x, y, z); break;
            case "mangrove": genMangroveTree(s, ref rng, x, y, z); break;
            case "swamp_oak": genSwampOak(s, ref rng, x, y, z); break;
            default: break;
        }
    }

    public static int netherFloorY(ChunkSink s, int x, int z, ref RandomX rng)
    {
        // random air column probe between 32..100
        int start = 32 + rng.nextInt(68);
        int y = start;
        while (y > 6)
        {
            int c = s.get(x, y, z);
            int below = s.get(x, y - 1, z);
            if (c == 0 && below > 0 && solidId(idOf(below))) return y;
            y -= 1;
        }
        return -1;
    }

    public static int seafloorY(ChunkSink s, int x, int z)
    {
        int y = 62;
        while (y > 8)
        {
            int c = s.get(x, y, z);
            if (c == -1) return -1;
            if (idOf(c) == B.water)
            {
                int below = s.get(x, y - 1, z);
                if (below > 0 && solidId(idOf(below))) return y;
            }
            else if (c != 0)
            {
                return -1;
            }
            y -= 1;
        }
        return -1;
    }

    // cave features operate within the target chunk only
    private static void caveFeature(string name, int count, ChunkSink s, ref RandomX rng, int ocx, int ocz, Func<int, int, int> biomeAt)
    {
        int baseX = ocx * 16, baseZ = ocz * 16;
        for (int _i = 0; _i < count; _i++)
        {
            int x = baseX + rng.nextInt(16), z = baseZ + rng.nextInt(16);
            int yTop = Math.Min(58, s.topY(x, z) - 8);
            if (yTop <= s.minY + 6) continue;
            int y = s.minY + 6 + rng.nextInt(Math.Max(1, yTop - s.minY - 6));
            int c = s.get(x, y, z);
            if (c != 0) continue;
            // find floor & ceiling of this air pocket
            int floor = -10000, ceil = 10000;
            for (int d = 1; d < 12; d++)
            {
                if (floor == -10000)
                {
                    int b = s.get(x, y - d, z);
                    if (b > 0 && solidId(idOf(b))) floor = y - d + 1;
                    else if (b == -1) break;
                }
                if (ceil == 10000)
                {
                    int b = s.get(x, y + d, z);
                    if (b > 0 && solidId(idOf(b))) ceil = y + d - 1;
                }
                if (floor != -10000 && ceil != 10000) break;
            }
            switch (name)
            {
                case "dripstone_cluster":
                    {
                        if (floor == -10000) break;
                        for (int _j = 0; _j < 6; _j++)
                        {
                            int dx = rng.nextInt(5) - 2, dz = rng.nextInt(5) - 2;
                            int g = s.get(x + dx, floor - 1, z + dz);
                            if (g > 0 && isStoneLike(g))
                            {
                                s.set(x + dx, floor - 1, z + dz, cell(B.dripstone_block));
                                if (rng.nextFloat() < 0.6 && s.get(x + dx, floor, z + dz) == 0)
                                {
                                    int h = 1 + rng.nextInt(3);
                                    for (int k = 0; k < h; k++)
                                    {
                                        int thickness = k == h - 1 ? 0 : (k == h - 2 ? 1 : 3);
                                        if (s.get(x + dx, floor + k, z + dz) == 0)
                                        {
                                            s.set(x + dx, floor + k, z + dz, cell(B.pointed_dripstone, (thickness << 1)));
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    }

                case "pointed_dripstone":
                    {
                        bool up = rng.nextBoolean();
                        if (up && floor != -10000)
                        {
                            int g = s.get(x, floor - 1, z);
                            if (g > 0 && isStoneLike(g) && s.get(x, floor, z) == 0)
                            {
                                s.set(x, floor, z, cell(B.pointed_dripstone, 0));
                            }
                        }
                        else if (ceil != 10000)
                        {
                            int g = s.get(x, ceil + 1, z);
                            if (g > 0 && isStoneLike(g) && s.get(x, ceil, z) == 0)
                            {
                                int len = 1 + rng.nextInt(3);
                                for (int k = 0; k < len; k++)
                                {
                                    int thickness = k == len - 1 ? 0 : (k == len - 2 ? 1 : 3);
                                    if (s.get(x, ceil - k, z) == 0)
                                    {
                                        s.set(x, ceil - k, z, cell(B.pointed_dripstone, (1 | (thickness << 1))));
                                    }
                                }
                            }
                        }
                        break;
                    }

                case "dripstone_pool":
                    {
                        if (floor == -10000) break;
                        int g = s.get(x, floor - 1, z);
                        if (g > 0 && isStoneLike(g)) s.set(x, floor - 1, z, cell(B.water, 0));
                        break;
                    }

                case "moss_patch":
                    {
                        if (floor == -10000 || (biomeAt(x, z) != (int)Biome.lushCaves && rng.nextFloat() < 0.5)) break;
                        int r = 2 + rng.nextInt(3);
                        for (int dz = -r; dz <= r; dz++)
                        {
                            for (int dx = -r; dx <= r; dx++)
                            {
                                if (dx * dx + dz * dz > r * r) continue;
                                int g = s.get(x + dx, floor - 1, z + dz);
                                if (g > 0 && isStoneLike(g))
                                {
                                    s.set(x + dx, floor - 1, z + dz, cell(B.moss_block));
                                    int above = s.get(x + dx, floor, z + dz);
                                    if (above == 0 && rng.nextFloat() < 0.5)
                                    {
                                        double r2 = rng.nextFloat();
                                        s.set(x + dx, floor, z + dz, r2 < 0.5 ? cell(B.short_grass) : r2 < 0.7 ? cell(B.moss_carpet) : r2 < 0.9 ? cell(B.tall_grass, 0) : cell(B.azalea));
                                    }
                                }
                            }
                        }
                        break;
                    }

                case "lush_vegetation":
                    {
                        if (floor == -10000) break;
                        int g = s.get(x, floor - 1, z);
                        if (g > 0 && (idOf(g) == B.moss_block || isSoil(g)) && s.get(x, floor, z) == 0)
                        {
                            double r = rng.nextFloat();
                            s.set(x, floor, z, r < 0.6 ? cell(B.short_grass) : r < 0.8 ? cell(B.moss_carpet) : cell(B.flowering_azalea));
                        }
                        break;
                    }

                case "glow_berries":
                    {
                        if (ceil == 10000) break;
                        int g = s.get(x, ceil + 1, z);
                        if (g > 0 && opaqueId(idOf(g)) && s.get(x, ceil, z) == 0)
                        {
                            int len = 2 + rng.nextInt(5);
                            for (int k = 0; k < len; k++)
                            {
                                if (s.get(x, ceil - k, z) != 0) break;
                                int lit = rng.nextFloat() < 0.3 ? 8 : 0;
                                s.set(x, ceil - k, z, k == len - 1 ? cell(B.cave_vines, lit) : cell(B.cave_vines_plant, lit));
                            }
                        }
                        break;
                    }

                case "spore_blossom":
                    {
                        if (ceil == 10000) break;
                        int g = s.get(x, ceil + 1, z);
                        if (g > 0 && (idOf(g) == B.moss_block || opaqueId(idOf(g))) && s.get(x, ceil, z) == 0)
                        {
                            s.set(x, ceil, z, cell(B.spore_blossom));
                        }
                        break;
                    }

                case "azalea_tree":
                    {
                        if (floor == -10000 || rng.nextFloat() < 0.8) break;
                        int g = s.get(x, floor - 1, z);
                        if (g > 0 && (idOf(g) == B.moss_block || isSoil(g))) genAzaleaTree(s, ref rng, x, floor, z);
                        break;
                    }

                case "big_dripleaf":
                    {
                        if (floor == -10000) break;
                        int g = s.get(x, floor - 1, z);
                        if (g > 0 && (idOf(g) == B.moss_block || idOf(g) == B.clay || isSoil(g)) && s.get(x, floor, z) == 0)
                        {
                            int h = 1 + rng.nextInt(3);
                            int facing = rng.nextInt(4);
                            for (int k = 0; k < h - 1; k++) s.set(x, floor + k, z, cell(B.big_dripleaf_stem, (facing)));
                            s.set(x, floor + h - 1, z, cell(B.big_dripleaf, (facing)));
                        }
                        break;
                    }

                case "small_dripleaf":
                    {
                        if (floor == -10000) break;
                        int g = s.get(x, floor - 1, z);
                        if (g > 0 && (idOf(g) == B.moss_block || idOf(g) == B.clay) && s.get(x, floor, z) == 0)
                        {
                            int facing = rng.nextInt(4);
                            s.set(x, floor, z, cell(B.small_dripleaf, (facing << 1)));
                            s.set(x, floor + 1, z, cell(B.small_dripleaf, (1 | (facing << 1))));
                        }
                        break;
                    }

                case "clay_pool":
                    {
                        if (floor == -10000) break;
                        int r = 1 + rng.nextInt(2);
                        for (int dz = -r; dz <= r; dz++)
                        {
                            for (int dx = -r; dx <= r; dx++)
                            {
                                int g = s.get(x + dx, floor - 1, z + dz);
                                if (g > 0 && isStoneLike(g))
                                {
                                    s.set(x + dx, floor - 1, z + dz, dx == 0 && dz == 0 ? cell(B.water, 0) : cell(B.clay));
                                }
                            }
                        }
                        break;
                    }

                case "sculk_patch":
                    {
                        if (floor == -10000 || biomeAt(x, z) != (int)Biome.deepDark) break;
                        int r = 2 + rng.nextInt(4);
                        for (int dz = -r; dz <= r; dz++)
                        {
                            for (int dx = -r; dx <= r; dx++)
                            {
                                if (dx * dx + dz * dz > r * r) continue;
                                int g = s.get(x + dx, floor - 1, z + dz);
                                if (g > 0 && isStoneLike(g)) s.set(x + dx, floor - 1, z + dz, cell(B.sculk));
                            }
                        }
                        break;
                    }

                case "sculk_vein":
                    {
                        if (floor == -10000 || biomeAt(x, z) != (int)Biome.deepDark) break;
                        int g = s.get(x, floor - 1, z);
                        if (g > 0 && s.get(x, floor, z) == 0) s.set(x, floor, z, cell(B.sculk_vein, 0));
                        break;
                    }

                case "sculk_shrieker":
                    {
                        if (floor == -10000 || biomeAt(x, z) != (int)Biome.deepDark || rng.nextFloat() < 0.7) break;
                        if (idOf(s.get(x, floor - 1, z)) == B.sculk && s.get(x, floor, z) == 0)
                        {
                            s.set(x, floor, z, cell(B.sculk_shrieker));
                            s.addBlockEntity(new BESpec(x: x, y: floor, z: z, kind: "shrieker", data: new Dictionary<string, BEValue> { ["canSummon"] = BEValue.@bool(true) }));
                        }
                        break;
                    }

                case "sculk_sensor":
                    {
                        if (floor == -10000 || biomeAt(x, z) != (int)Biome.deepDark || rng.nextFloat() < 0.5) break;
                        if (idOf(s.get(x, floor - 1, z)) == B.sculk && s.get(x, floor, z) == 0)
                        {
                            s.set(x, floor, z, cell(B.sculk_sensor));
                        }
                        break;
                    }

                default:
                    break;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Geodes (cross-chunk capable, rare)
    // ---------------------------------------------------------------------------
    public static void tryGeode(uint seed, int ocx, int ocz, ChunkSink s)
    {
        var rng = chunkRandom(seed, ocx, ocz, 0xCE0DE);
        if (rng.nextFloat() > 1.0 / 26) return;
        int x = ocx * 16 + rng.nextInt(16);
        int z = ocz * 16 + rng.nextInt(16);
        int y = -52 + rng.nextInt(76);
        int r = 4 + rng.nextInt(3);
        for (int dy = -r - 1; dy <= r + 1; dy++)
        {
            for (int dz = -r - 1; dz <= r + 1; dz++)
            {
                for (int dx = -r - 1; dx <= r + 1; dx++)
                {
                    double d = Math.Sqrt((double)(dx * dx + dy * dy + dz * dz)) + (double)(hash2(seed, x + dx + dy, z + dz, 5) % 100) / 220;
                    if (d > (double)r + 1.2) continue;
                    int wx = x + dx, wy = y + dy, wz = z + dz;
                    if (d > (double)r + 0.3)
                    {
                        int c = s.get(wx, wy, wz);
                        if (c > 0) s.set(wx, wy, wz, cell(B.smooth_basalt));
                    }
                    else if (d > (double)r - 0.9)
                    {
                        s.set(wx, wy, wz, cell(B.calcite));
                    }
                    else if (d > (double)r - 1.9)
                    {
                        s.set(wx, wy, wz, rng.nextFloat() < 0.08 ? cell(B.budding_amethyst) : cell(B.amethyst_block));
                    }
                    else
                    {
                        s.set(wx, wy, wz, 0);
                    }
                }
            }
        }
        // clusters inside
        for (int _i = 0; _i < 8; _i++)
        {
            int dx = rng.nextInt(r * 2 - 2) - (r - 1), dy = rng.nextInt(r * 2 - 2) - (r - 1), dz = rng.nextInt(r * 2 - 2) - (r - 1);
            int wx = x + dx, wy = y + dy, wz = z + dz;
            if (s.get(wx, wy, wz) == 0)
            {
                // attach to a neighboring amethyst wall
                for (int f = 0; f < 6; f++)
                {
                    int nx = wx + (new[] { 0, 0, 0, 0, -1, 1 })[f], ny = wy + (new[] { -1, 1, 0, 0, 0, 0 })[f], nz = wz + (new[] { 0, 0, -1, 1, 0, 0 })[f];
                    ushort nb = idOf(s.get(nx, ny, nz));
                    if (nb == B.amethyst_block || nb == B.budding_amethyst)
                    {
                        s.set(wx, wy, wz, cell(B.amethyst_cluster, (f ^ 1)));
                        break;
                    }
                }
            }
        }
    }
}

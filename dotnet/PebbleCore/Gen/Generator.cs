// Chunk generation orchestrator — every
// dimension, structures included. Returns transferable arrays + block entity /
// entity / structure specs.
//
// Module-level free funcs/vars (overworldGen, netherGen, endGen, generateChunk,
// generateOverworldChunk) are surfaced via: public static class GenGlobals
//   -> add `global using static PebbleCore.GenGlobals;`
// Types GenOutput and ArraySink live at namespace level.

using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public struct GenOutput
{
    public readonly ushort[] blocks;
    public readonly byte[] biomes;
    public readonly List<BESpec> blockEntities;
    public readonly List<EntitySpec> entities;
    public readonly List<StructRef> structRefs;

    public GenOutput(ushort[] blocks, byte[] biomes, List<BESpec> blockEntities,
                     List<EntitySpec> entities, List<StructRef> structRefs)
    {
        this.blocks = blocks;
        this.biomes = biomes;
        this.blockEntities = blockEntities;
        this.entities = entities;
        this.structRefs = structRefs;
    }
}

public sealed class ArraySink : ChunkSink
{
    public int cx { get; }
    public int cz { get; }
    public int minY { get; }
    public int maxY { get; }
    public ushort[] blocks;
    public List<BESpec> blockEntities = new List<BESpec>();
    public List<EntitySpec> entities = new List<EntitySpec>();
    private readonly Func<int, int, int> heightFallback;

    public ArraySink(int cx, int cz, ushort[] blocks, int minY, int maxY, Func<int, int, int> heightFallback)
    {
        this.cx = cx;
        this.cz = cz;
        this.blocks = blocks;
        this.minY = minY;
        this.maxY = maxY;
        this.heightFallback = heightFallback;
    }

    public void set(int x, int y, int z, ushort c)
    {
        int lx = x - cx * 16, lz = z - cz * 16;
        if (lx < 0 || lx > 15 || lz < 0 || lz > 15 || y < minY || y >= maxY) return;
        blocks[((y - minY) * 16 + lz) * 16 + lx] = c;
    }

    public int get(int x, int y, int z)
    {
        int lx = x - cx * 16, lz = z - cz * 16;
        if (lx < 0 || lx > 15 || lz < 0 || lz > 15) return -1;
        if (y < minY || y >= maxY) return 0;
        return (int)blocks[((y - minY) * 16 + lz) * 16 + lx];
    }

    public int topY(int x, int z)
    {
        int lx = x - cx * 16, lz = z - cz * 16;
        if (lx < 0 || lx > 15 || lz < 0 || lz > 15) return heightFallback(x, z);
        int y = maxY - 1;
        while (y > minY)
        {
            ushort c = blocks[((y - minY) * 16 + lz) * 16 + lx];
            if (c != 0)
            {
                ushort id = (ushort)(c >> 4);
                if (id == B.water) return y + 1;
                if (SOLID[(int)id] == 1 || id == B.lava) return y + 1;
            }
            y -= 1;
        }
        return minY + 1;
    }

    public void addBlockEntity(BESpec spec)
    {
        int lx = spec.x - cx * 16, lz = spec.z - cz * 16;
        if (lx < 0 || lx > 15 || lz < 0 || lz > 15) return;
        blockEntities.Add(spec);
    }

    public void addEntity(EntitySpec spec)
    {
        int lx = (int)Math.Floor(spec.x) - cx * 16, lz = (int)Math.Floor(spec.z) - cz * 16;
        if (lx < 0 || lx > 15 || lz < 0 || lz > 15) return;
        entities.Add(spec);
    }
}

public static class GenGlobals
{
    private static Dictionary<uint, OverworldGen> overworldGens = new Dictionary<uint, OverworldGen>();
    private static Dictionary<uint, NetherGen> netherGens = new Dictionary<uint, NetherGen>();
    private static Dictionary<uint, EndGen> endGens = new Dictionary<uint, EndGen>();
    private static readonly object genLock = new object();

    public static OverworldGen overworldGen(uint seed)
    {
        lock (genLock)
        {
            if (overworldGens.TryGetValue(seed, out var g)) return g;
            var ng = new OverworldGen(seed);
            overworldGens[seed] = ng;
            return ng;
        }
    }

    public static NetherGen netherGen(uint seed)
    {
        lock (genLock)
        {
            if (netherGens.TryGetValue(seed, out var g)) return g;
            var ng = new NetherGen(seed);
            netherGens[seed] = ng;
            return ng;
        }
    }

    public static EndGen endGen(uint seed)
    {
        lock (genLock)
        {
            if (endGens.TryGetValue(seed, out var g)) return g;
            var ng = new EndGen(seed);
            endGens[seed] = ng;
            return ng;
        }
    }

    public static GenOutput generateChunk(Dim dim, uint seed, int cx, int cz)
    {
        registerAllStructures();
        var info = DIMS[(int)dim];
        int n = CHUNK_W * CHUNK_W * info.height;
        ushort[] blocks = new ushort[n];
        byte[] biomes = new byte[4 * 4 * ((info.height + 3) / 4)];

        if (dim == Dim.overworld)
        {
            var gen = overworldGen(seed);
            var res = gen.fillTerrain(cx, cz, ref blocks, ref biomes);
            gen.carve(cx, cz, ref blocks);
            gen.applySurface(cx, cz, ref blocks, res.heights, res.surfaceBiomes);
            gen.placeOres(cx, cz, ref blocks, res.surfaceBiomes);

            // refined estimate (incl. 3D detail) — the spline-only one diverged ±34
            // from real terrain, scattering trees and burying/hovering structures
            var sink = new ArraySink(cx, cz, blocks, GEN_MIN_Y, GEN_MIN_Y + WORLD_H,
                                     (x, z) => gen.refinedHeightEstimate((double)x, (double)z));
            var ctx = new GenCtx(seed,
                                 (x, z) => gen.refinedHeightEstimate((double)x, (double)z),
                                 (x, z) => (int)gen.surfaceBiomeAt((double)x, (double)z),
                                 (int)dim);
            var overworldStructs = STRUCTURES.Where(s => !new[] { "fortress", "bastion", "end_city" }.Contains(s.id)).ToList();
            var structRefs = buildStructuresForChunk(ctx, cx, cz, sink, overworldStructs);

            // features from 3×3 origin chunks
            Func<int, int, int> surfaceBiomeAt = (x, z) => (int)gen.surfaceBiomeAt((double)x, (double)z);
            for (int oz = cz - 1; oz <= cz + 1; oz++)
            {
                for (int ox = cx - 1; ox <= cx + 1; ox++)
                {
                    var centerBiome = gen.surfaceBiomeAt((double)(ox * 16 + 8), (double)(oz * 16 + 8));
                    var feats = biomeDef((int)centerBiome).features;
                    uint salt = 9000;
                    foreach (var f in feats)
                    {
                        var rng = chunkRandom(seed, ox, oz, salt);
                        salt += 1;
                        runFeature(f, sink, ref rng, ox, oz, seed, surfaceBiomeAt);
                    }
                    // cave biome features from the full 3×3 origins — running them
                    // only for the target chunk clipped dripstone/moss/sculk flat
                    // at every chunk face (their radius reaches up to 5 blocks)
                    foreach (var cb in new[] { Biome.lushCaves, Biome.dripstoneCaves, Biome.deepDark })
                    {
                        var feats2 = biomeDef((int)cb).features;
                        uint salt2 = (uint)(12000 + (int)cb * 100);
                        foreach (var f in feats2)
                        {
                            var rng = chunkRandom(seed, ox, oz, salt2);
                            salt2 += 1;
                            runFeature(f, sink, ref rng, ox, oz, seed, (x, z) =>
                            {
                                int cbb = gen.caveBiomeAt((double)x, -10, (double)z, gen.heightEstimate((double)x, (double)z));
                                return cbb == -1 ? (int)gen.surfaceBiomeAt((double)x, (double)z) : cbb;
                            });
                        }
                    }
                    tryGeode(seed, ox, oz, sink);
                }
            }
            tryDungeons(seed, cx, cz, sink);
            gen.applySnowAndIce(cx, cz, ref sink.blocks, res.surfaceBiomes);

            // worldgen passive mobs
            var mobRng = chunkRandom(seed, cx, cz, 0xAB1E);
            if (mobRng.nextFloat() < 0.1)
            {
                var centerBiome = gen.surfaceBiomeAt((double)(cx * 16 + 8), (double)(cz * 16 + 8));
                var list = biomeDef((int)centerBiome).creatures;
                if (list.Count != 0)
                {
                    var entry = mobRng.pickWeighted(list, e => e.weight);
                    int pack = entry.minPack + mobRng.nextInt(entry.maxPack - entry.minPack + 1);
                    for (int i = 0; i < pack; i++)
                    {
                        int px = cx * 16 + mobRng.nextInt(16), pz = cz * 16 + mobRng.nextInt(16);
                        int py = sink.topY(px, pz);
                        // require real ground — topY over oceans returned the water
                        // surface and shipped chickens standing on the sea
                        int ground = sink.get(px, py - 1, pz);
                        int gid = ground >> 4;
                        bool grounded = ground != -1 && gid != (int)B.water && gid != (int)B.lava
                            && gid != 0 && blockDefs[gid].solid;
                        if (py > 50 && py < 200 && grounded)
                        {
                            sink.addEntity(new EntitySpec(entry.mob, (double)px + 0.5, (double)py, (double)pz + 0.5));
                        }
                    }
                }
            }
            return new GenOutput(sink.blocks, biomes,
                                 sink.blockEntities, sink.entities, structRefs);
        }

        if (dim == Dim.nether)
        {
            var gen = netherGen(seed);
            var surfaceBiomes = gen.fillTerrain(cx, cz, ref blocks, ref biomes);
            gen.applySurface(cx, cz, ref blocks, surfaceBiomes);
            gen.placeOres(cx, cz, blocks);
            var sink = new ArraySink(cx, cz, blocks, 0, NETHER_H,
                                     (x, z) => gen.heightEstimate((double)x, (double)z));
            var ctx = new GenCtx(seed,
                                 (x, z) => gen.heightEstimate((double)x, (double)z),
                                 (x, z) => gen.biomeAt((double)x, (double)z),
                                 (int)dim);
            var netherStructs = STRUCTURES.Where(s => s.id == "fortress" || s.id == "bastion" || s.id == "ruined_portal").ToList();
            var structRefs = buildStructuresForChunk(ctx, cx, cz, sink, netherStructs);
            Func<int, int, int> biomeAt = (x, z) => gen.biomeAt((double)x, (double)z);
            for (int oz = cz - 1; oz <= cz + 1; oz++)
            {
                for (int ox = cx - 1; ox <= cx + 1; ox++)
                {
                    int centerBiome = gen.biomeAt((double)(ox * 16 + 8), (double)(oz * 16 + 8));
                    var feats = biomeDef(centerBiome).features;
                    uint salt = 9500;
                    foreach (var f in feats)
                    {
                        var rng = chunkRandom(seed, ox, oz, salt);
                        salt += 1;
                        runFeature(f, sink, ref rng, ox, oz, seed, biomeAt);
                    }
                }
            }
            return new GenOutput(sink.blocks, biomes,
                                 sink.blockEntities, sink.entities, structRefs);
        }

        // End
        {
            var gen = endGen(seed);
            var surfaceBiomes = gen.fillTerrain(cx, cz, ref blocks, ref biomes);
            _ = surfaceBiomes;
            var sink = new ArraySink(cx, cz, blocks, 0, END_H, (x, z) => 60);
            var fixtureBlocks = sink.blocks;
            gen.placeFixtures(cx, cz, fixtureBlocks, (mob, x, y, z, data) =>
            {
                sink.entities.Add(new EntitySpec(mob, x, y, z, data));
            });
            sink.blocks = fixtureBlocks;
            var ctx = new GenCtx(seed,
                                 (x, z) =>
                                 {
                                     double f = gen.islandFactor((double)x, (double)z);
                                     return f > 0 ? (int)Math.Floor(58 + f * 4) : 0;
                                 },
                                 (x, z) => gen.biomeColumn((double)x, (double)z),
                                 (int)dim);
            var endStructs = STRUCTURES.Where(s => s.id == "end_city").ToList();
            var structRefs = buildStructuresForChunk(ctx, cx, cz, sink, endStructs);
            Func<int, int, int> biomeColumnAt = (x, z) => gen.biomeColumn((double)x, (double)z);
            for (int oz = cz - 1; oz <= cz + 1; oz++)
            {
                for (int ox = cx - 1; ox <= cx + 1; ox++)
                {
                    int centerBiome = gen.biomeColumn((double)(ox * 16 + 8), (double)(oz * 16 + 8));
                    var feats = biomeDef(centerBiome).features;
                    uint salt = 9900;
                    foreach (var f in feats)
                    {
                        var rng = chunkRandom(seed, ox, oz, salt);
                        salt += 1;
                        runFeature(f, sink, ref rng, ox, oz, seed, biomeColumnAt);
                    }
                }
            }
            return new GenOutput(sink.blocks, biomes,
                                 sink.blockEntities, sink.entities, structRefs);
        }
    }

    /// back-compat shim for callers built against the pre-structures pipeline
    public static GenOutput generateOverworldChunk(uint seed, int cx, int cz)
    {
        return generateChunk(Dim.overworld, seed, cx, cz);
    }
}

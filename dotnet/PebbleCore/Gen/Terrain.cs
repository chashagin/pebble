// Overworld terrain — Climate sampling, spline heights,
// interpolated 3D density with cheese/spaghetti/noodle caves, worm carvers,
// ravines, aquifers, surface rules, deepslate, bedrock, ores.
//
// Bit-exactness notes vs the golden baselines:
//  - Math.round(x) in deterministic rounds half toward +inf → detRound() here
//  - the density lattice is a Float32Array in baseline → [Float] here, interpolation
//    reads back through Double(lattice[i]) to mirror the f32 truncation
//  - all seed offsets use &+ to match deterministic uint32 wrap
//
// Module-level free funcs/vars (SEA, GEN_MIN_Y, WORLD_H, detRound, baseHeight,
// solidForSnow) are surfaced via: public static class TerrainGlobals
//   -> add `global using static PebbleCore.TerrainGlobals;`
// Namespace-level types: ClimateSampler, AquiferInfo, TerrainResult, OverworldGen.

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class TerrainGlobals
{
    public const int SEA = 63;
    public const int GEN_MIN_Y = -64;
    public const int WORLD_H = 384;

    public static double detRound(double x) => Math.Floor(x + 0.5);

    public static readonly Spline SPLINE_BASE = new Spline(new (double, double)[] {
        (-1.0, 34), (-0.62, 40), (-0.45, 48), (-0.25, 56), (-0.17, 62.5), (-0.1, 66),
        (0.0, 69), (0.22, 75), (0.55, 84), (1.0, 92),
    });
    public static readonly Spline SPLINE_ERO_FLAT = new Spline(new (double, double)[] {
        (-1.0, 1.45), (-0.45, 1.0), (0.1, 0.72), (0.55, 0.45), (1.0, 0.32),
    });
    // amp 110 produced 170-block sheer terrace walls ("cliffs way too large");
    // 82 keeps dramatic peaks at vanilla-like ±80 without the mega-cliffs
    public static readonly Spline SPLINE_PV_AMP = new Spline(new (double, double)[] {
        (-1.0, 82), (-0.55, 48), (-0.25, 22), (0.1, 13), (0.55, 6), (1.0, 3.5),
    });
    public static readonly Spline SPLINE_3D_AMP = new Spline(new (double, double)[] {
        (-1.0, 24), (-0.45, 15), (0.0, 10), (0.5, 7), (1.0, 5),
    });

    public static double baseHeight(Climate cl)
    {
        double h = SPLINE_BASE.at(cl.c);
        if (cl.c > -0.16)
        {
            double inlandGate = clampD(mapRange(cl.c, -0.16, -0.02, 0, 1), 0, 1);
            double flat = SPLINE_ERO_FLAT.at(cl.e);
            h = 63 + (h - 63) * flat;
            double pvAmp = SPLINE_PV_AMP.at(cl.e) * inlandGate;
            h += cl.pv * pvAmp;
            // swamps flatten to sea level
            if (cl.e > 0.55 && cl.h > 0.1 && cl.c < 0.35 && cl.t > -0.1)
            {
                h = lerpD(h, 62.4, clampD(mapRange(cl.e, 0.55, 0.75, 0, 1), 0, 1));
            }
            // river carve
            double riverF = clampD(mapRange(cl.pv, -0.72, -0.85, 0, 1), 0, 1);
            if (riverF > 0 && cl.e > -0.4)
            {
                h = lerpD(h, 58.5, riverF);
            }
        }
        return h;
    }

    public static bool solidForSnow(ushort id)
    {
        // only sturdy tops carry snow layers — landing snow on grass tufts/flowers
        // (placed by the feature pass that runs first) left layers floating on plants
        if (id == B.ice || id == B.packed_ice || id == B.air || id == B.water || id == B.lava) return false;
        return blockDefs[(int)id].solid;
    }
}

public sealed class ClimateSampler
{
    private readonly FBM temp, humid, cont, ero, weird, rare;

    public ClimateSampler(uint seed)
    {
        temp = new FBM(unchecked(seed + 11), 4, 1.0 / 2800, lacunarity: 2, persistence: 0.55);
        humid = new FBM(unchecked(seed + 22), 4, 1.0 / 1600, lacunarity: 2, persistence: 0.55);
        cont = new FBM(unchecked(seed + 33), 6, 1.0 / 2400, lacunarity: 2, persistence: 0.55);
        ero = new FBM(unchecked(seed + 44), 4, 1.0 / 1100, lacunarity: 2, persistence: 0.5);
        weird = new FBM(unchecked(seed + 55), 4, 1.0 / 520, lacunarity: 2, persistence: 0.5);
        rare = new FBM(unchecked(seed + 66), 2, 1.0 / 650, lacunarity: 2, persistence: 0.5);
    }

    public Climate at(double x, double z)
    {
        double w = clampD(weird.sample2(x, z) * 1.6, -1, 1);
        return new Climate(
            t: clampD(temp.sample2(x, z) * 1.7, -1, 1),
            h: clampD(humid.sample2(x, z) * 1.7, -1, 1),
            c: clampD(cont.sample2(x, z) * 1.55 + 0.08, -1, 1),
            e: clampD(ero.sample2(x, z) * 1.6, -1, 1),
            w: w,
            pv: peaksValleys(w),
            rare: rare.sample2(x, z) * 0.5 + 0.5
        );
    }
}

public struct AquiferInfo
{
    public readonly int level;
    public readonly bool lava;
    public AquiferInfo(int level, bool lava) { this.level = level; this.lava = lava; }
}

public struct TerrainResult
{
    public readonly short[] heights;
    public readonly Climate[] climates;
    public readonly byte[] surfaceBiomes;
    public TerrainResult(short[] heights, Climate[] climates, byte[] surfaceBiomes)
    {
        this.heights = heights;
        this.climates = climates;
        this.surfaceBiomes = surfaceBiomes;
    }
}

public sealed class OverworldGen
{
    public readonly uint seed;
    public readonly ClimateSampler climate;
    private readonly FBM detail;
    private readonly FBM cheese;
    private readonly FBM spag1;
    private readonly FBM spag2;
    private readonly FBM noodleA;
    private readonly FBM noodleB;
    private readonly FBM surfNoise;
    private readonly FBM aquiferNoise;
    private readonly FBM caveBiomeNoise;
    private readonly FBM dripstoneNoise;
    private readonly FBM deepDarkNoise;
    private readonly SimplexNoise bandOffset;

    private const ushort AIR = 0;
    private readonly ushort STONE = cell(B.stone);
    private readonly ushort DEEPSLATE = cell(B.deepslate);
    private readonly ushort WATER = cell(B.water);
    private readonly ushort LAVA = cell(B.lava);
    private readonly ushort BEDROCK = cell(B.bedrock);

    public OverworldGen(uint seed)
    {
        this.seed = seed;
        climate = new ClimateSampler(seed);
        detail = new FBM(unchecked(seed + 101), 4, 1.0 / 110, lacunarity: 2.2, persistence: 0.5);
        cheese = new FBM(unchecked(seed + 202), 3, 1.0 / 150, lacunarity: 2, persistence: 0.6);
        spag1 = new FBM(unchecked(seed + 303), 2, 1.0 / 92, lacunarity: 2, persistence: 0.5);
        spag2 = new FBM(unchecked(seed + 404), 2, 1.0 / 92, lacunarity: 2, persistence: 0.5);
        noodleA = new FBM(unchecked(seed + 505), 2, 1.0 / 60, lacunarity: 2, persistence: 0.5);
        noodleB = new FBM(unchecked(seed + 606), 2, 1.0 / 60, lacunarity: 2, persistence: 0.5);
        surfNoise = new FBM(unchecked(seed + 707), 3, 1.0 / 48, lacunarity: 2, persistence: 0.5);
        aquiferNoise = new FBM(unchecked(seed + 808), 2, 1.0 / 280, lacunarity: 2, persistence: 0.5);
        caveBiomeNoise = new FBM(unchecked(seed + 909), 3, 1.0 / 240, lacunarity: 2, persistence: 0.5);
        dripstoneNoise = new FBM(unchecked(seed + 1010), 3, 1.0 / 220, lacunarity: 2, persistence: 0.5);
        deepDarkNoise = new FBM(unchecked(seed + 1111), 2, 1.0 / 300, lacunarity: 2, persistence: 0.5);
        bandOffset = new SimplexNoise(unchecked(seed + 1212));
    }

    /// terrain height estimate from pure noise — usable anywhere without blocks
    public int heightEstimate(double x, double z)
    {
        return (int)detRound(baseHeight(climate.at(x, z)));
    }

    /// height estimate INCLUDING the 3D detail term the density function adds —
    /// the spline-only estimate misses ±SPLINE_3D_AMP, which scattered trees and
    /// buried structures relative to the real surface. Two fixed-point passes on
    /// the same detail noise land within a couple of blocks of the actual terrain.
    public int refinedHeightEstimate(double x, double z)
    {
        Climate cl = climate.at(x, z);
        double target = baseHeight(cl);
        double amp = SPLINE_3D_AMP.at(cl.e) * clampD(mapRange(cl.c, -0.19, -0.05, 0.35, 1), 0.35, 1);
        double y = target + detail.sample3(x, target * 1.35, z) * amp;
        y = target + detail.sample3(x, y * 1.35, z) * amp;
        if (y > 170) { y = 170 + (y - 170) / 1.55; }   // density's high-peak rounding term
        return (int)detRound(y);
    }

    public Biome surfaceBiomeAt(double x, double z)
    {
        return selectBiome(climate.at(x, z));
    }

    public AquiferInfo aquiferAt(double x, double z, Climate cl)
    {
        if (cl.c < -0.11) { return new AquiferInfo(level: SEA, lava: false); }
        double n = aquiferNoise.sample2(x, z);
        if (n > 0.28) { return new AquiferInfo(level: (int)Math.Floor(30 + (n - 0.28) * 60), lava: false); }
        if (n < -0.4) { return new AquiferInfo(level: 12, lava: true); }
        return new AquiferInfo(level: -1000, lava: false);
    }

    /// -1 when no cave biome applies
    public int caveBiomeAt(double x, int y, double z, int surfaceH)
    {
        if (y > surfaceH - 9) { return -1; }
        if (y < 12)
        {
            double dd = deepDarkNoise.sample2(x, z);
            if (dd > 0.42 && y < 0) { return (int)Biome.deepDark; }
        }
        double lush = caveBiomeNoise.sample3(x, (double)y * 1.5, z);
        if (lush > 0.4 && y < 60) { return (int)Biome.lushCaves; }
        double drip = dripstoneNoise.sample3(x + 1000, (double)y * 1.5, z - 1000);
        if (drip > 0.45 && y < 70) { return (int)Biome.dripstoneCaves; }
        return -1;
    }

    private static double bil(double a, double b, double c, double d, double fx, double fz)
    {
        return lerpD(lerpD(a, b, fx), lerpD(c, d, fx), fz);
    }

    /// Fill terrain for a chunk. blocks indexed ((y-minY)*16+z)*16+x.
    public TerrainResult fillTerrain(int cx, int cz, ref ushort[] blocks, ref byte[] biomes)
    {
        int baseX = cx * CHUNK_W, baseZ = cz * CHUNK_W;
        // climate per column (16×16) — sample at 4-block grid and bilerp
        var climGrid = new List<Climate>();
        climGrid.Capacity = 25;
        for (int gz = 0; gz <= 4; gz++)
        {
            for (int gx = 0; gx <= 4; gx++)
            {
                climGrid.Add(climate.at((double)(baseX + gx * 4), (double)(baseZ + gz * 4)));
            }
        }
        var climates = new Climate[256];
        for (int i = 0; i < 256; i++) climates[i] = new Climate(t: 0, h: 0, c: 0, e: 0, w: 0, pv: 0, rare: 0);
        var heights = new short[256];
        var surfaceBiomes = new byte[256];
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                int gx = x >> 2, gz = z >> 2;
                double fx = (double)(x & 3) / 4, fz = (double)(z & 3) / 4;
                Climate c00 = climGrid[gz * 5 + gx], c10 = climGrid[gz * 5 + gx + 1];
                Climate c01 = climGrid[(gz + 1) * 5 + gx], c11 = climGrid[(gz + 1) * 5 + gx + 1];
                var cl = new Climate(
                    t: bil(c00.t, c10.t, c01.t, c11.t, fx, fz),
                    h: bil(c00.h, c10.h, c01.h, c11.h, fx, fz),
                    c: bil(c00.c, c10.c, c01.c, c11.c, fx, fz),
                    e: bil(c00.e, c10.e, c01.e, c11.e, fx, fz),
                    w: bil(c00.w, c10.w, c01.w, c11.w, fx, fz),
                    pv: 0, rare: bil(c00.rare, c10.rare, c01.rare, c11.rare, fx, fz)
                );
                cl.pv = peaksValleys(cl.w);
                climates[z * 16 + x] = cl;
                heights[z * 16 + x] = (short)detRound(baseHeight(cl));
                surfaceBiomes[z * 16 + x] = (byte)(int)selectBiome(cl);
            }
        }

        // density sampling on a 5 × 49 × 5 lattice (4×8×4 cells)
        int NX = 5, NY = WORLD_H / 8 + 1, NZ = 5;
        var lattice = new float[NX * NY * NZ];
        for (int gz = 0; gz < NZ; gz++)
        {
            for (int gx = 0; gx < NX; gx++)
            {
                double wx = (double)(baseX + gx * 4), wz = (double)(baseZ + gz * 4);
                // sample climate at the lattice point's TRUE position — the old
                // min(15,…) clamp reused the x/z=15 column for the x/z=16 edge,
                // so adjacent chunks disagreed about their shared boundary
                Climate cl = climate.at(wx, wz);
                double target = baseHeight(cl);
                double amp = SPLINE_3D_AMP.at(cl.e) * clampD(mapRange(cl.c, -0.19, -0.05, 0.35, 1), 0.35, 1);
                for (int gy = 0; gy < NY; gy++)
                {
                    double y = (double)(GEN_MIN_Y + gy * 8);
                    double d = (target - y) + detail.sample3(wx, y * 1.35, wz) * amp;
                    // round high peaks, solidify deeps
                    if (y > 170) { d -= (y - 170) * 0.55; }
                    if (y < -40) { d += (-40 - y) * 0.35; }
                    // cheese caves
                    if (y < 58)
                    {
                        double ch = cheese.sample3(wx * 0.9, y * 2.0, wz * 0.9);
                        double fade = clampD((58 - y) / 14, 0, 1) * clampD((y - (double)(GEN_MIN_Y + 4)) / 10, 0, 1);
                        if (ch > 0.42 && fade > 0)
                        {
                            d = Math.Min(d, lerpD(d, (0.42 - ch) * 260, fade));
                        }
                    }
                    // spaghetti caves
                    if (y < 110)
                    {
                        double s1 = spag1.sample3(wx, y * 1.6, wz);
                        double s2 = spag2.sample3(wx, y * 1.6, wz);
                        double tube = Math.Max(Math.Abs(s1), Math.Abs(s2));
                        double thresh = 0.065 + clampD((y - 60) / 240, 0, 0.03);
                        if (tube < thresh)
                        {
                            double fade = clampD((y - (double)(GEN_MIN_Y + 3)) / 8, 0, 1);
                            if (fade > 0) { d = Math.Min(d, (tube - thresh) * 900 * fade); }
                        }
                    }
                    // noodle caves (thin, deep)
                    if (y < 28)
                    {
                        double n1 = noodleA.sample3(wx, y * 1.8, wz);
                        double n2 = noodleB.sample3(wx, y * 1.8, wz);
                        double tube = Math.Max(Math.Abs(n1), Math.Abs(n2));
                        if (tube < 0.038)
                        {
                            double fade = clampD((y - (double)(GEN_MIN_Y + 3)) / 8, 0, 1);
                            if (fade > 0) { d = Math.Min(d, (tube - 0.038) * 1200 * fade); }
                        }
                    }
                    lattice[(gy * NZ + gz) * NX + gx] = (float)d;
                }
            }
        }

        // fill blocks by trilinear interpolation
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                int ci = z * 16 + x;
                Climate cl = climates[ci];
                AquiferInfo aq = aquiferAt((double)(baseX + x), (double)(baseZ + z), cl);
                int gx = x >> 2, gz = z >> 2;
                double fx = (double)(x & 3) / 4, fz = (double)(z & 3) / 4;
                int topSolid = GEN_MIN_Y - 1;
                for (int gy = 0; gy < (NY - 1); gy++)
                {
                    double d000 = (double)lattice[(gy * NZ + gz) * NX + gx];
                    double d100 = (double)lattice[(gy * NZ + gz) * NX + gx + 1];
                    double d010 = (double)lattice[(gy * NZ + gz + 1) * NX + gx];
                    double d110 = (double)lattice[(gy * NZ + gz + 1) * NX + gx + 1];
                    double d001 = (double)lattice[((gy + 1) * NZ + gz) * NX + gx];
                    double d101 = (double)lattice[((gy + 1) * NZ + gz) * NX + gx + 1];
                    double d011 = (double)lattice[((gy + 1) * NZ + gz + 1) * NX + gx];
                    double d111 = (double)lattice[((gy + 1) * NZ + gz + 1) * NX + gx + 1];
                    double b0 = bil(d000, d100, d010, d110, fx, fz);
                    double b1 = bil(d001, d101, d011, d111, fx, fz);
                    for (int sy = 0; sy < 8; sy++)
                    {
                        int y = GEN_MIN_Y + gy * 8 + sy;
                        double d = b0 + (b1 - b0) * ((double)sy / 8);
                        int idx = ((y - GEN_MIN_Y) * 16 + z) * 16 + x;
                        if (d > 0)
                        {
                            blocks[idx] = STONE;
                            topSolid = y;
                        }
                        else if (y <= SEA && cl.c < -0.11)
                        {
                            blocks[idx] = WATER;
                        }
                        else if (y <= aq.level)
                        {
                            // lava aquifers are lava throughout — the old `y < 0`
                            // condition capped them with a layer of water
                            blocks[idx] = aq.lava ? LAVA : WATER;
                        }
                        else if (y <= GEN_MIN_Y + 10 && y < -54)
                        {
                            blocks[idx] = y <= -56 ? LAVA : AIR;
                        }
                        else
                        {
                            blocks[idx] = AIR;
                        }
                    }
                }
                // open-water rules, column-complete:
                // 1) every open cell at/below SEA floods — the climate-gated fill
                //    left dry pits and sheer water walls where ocean met "inland"
                //    columns whose terrain dips under sea level
                // 2) aquifer fluid the noise put in OPEN AIR above SEA is scrubbed
                //    (aquifers are subterranean; valleys got 9-deep ponds)
                if (topSolid < SEA)
                {
                    int y = Math.Max(topSolid + 1, GEN_MIN_Y);
                    while (y <= SEA)
                    {
                        int idx = ((y - GEN_MIN_Y) * 16 + z) * 16 + x;
                        if (blocks[idx] == AIR) { blocks[idx] = WATER; }
                        y += 1;
                    }
                }
                if (aq.level > SEA && cl.c >= -0.11 && topSolid < aq.level)
                {
                    int y = Math.Max(topSolid + 1, SEA + 1);
                    int yMax = Math.Min(aq.level, GEN_MIN_Y + WORLD_H - 1);
                    while (y <= yMax)
                    {
                        int idx = ((y - GEN_MIN_Y) * 16 + z) * 16 + x;
                        if (blocks[idx] == WATER || blocks[idx] == LAVA) { blocks[idx] = AIR; }
                        y += 1;
                    }
                }
            }
        }

        // biomes (quart resolution, with cave biome overrides)
        for (int qz = 0; qz < 4; qz++)
        {
            for (int qx = 0; qx < 4; qx++)
            {
                int ci = (qz * 4 + 2) * 16 + qx * 4 + 2;
                byte surfB = surfaceBiomes[ci];
                int surfH = (int)heights[ci];
                double wx = (double)(baseX + qx * 4 + 2), wz = (double)(baseZ + qz * 4 + 2);
                for (int qy = 0; qy < (WORLD_H / 4); qy++)
                {
                    int y = GEN_MIN_Y + qy * 4 + 2;
                    int cb = caveBiomeAt(wx, y, wz, surfH);
                    biomes[(qy * 4 + qz) * 4 + qx] = cb == -1 ? surfB : (byte)cb;
                }
            }
        }

        return new TerrainResult(heights: heights, climates: climates, surfaceBiomes: surfaceBiomes);
    }

    /// worm carvers + ravines, deterministic per source chunk, range 4
    public void carve(int cx, int cz, ref ushort[] blocks)
    {
        int RANGE = 4;
        for (int ocz = cz - RANGE; ocz <= cz + RANGE; ocz++)
        {
            for (int ocx = cx - RANGE; ocx <= cx + RANGE; ocx++)
            {
                var rng = chunkRandom(seed, ocx, ocz, 1337);
                // worm caves: 1 in 3 chunks spawn a system
                if (rng.nextFloat() < 0.3)
                {
                    int tunnels = 1 + rng.nextInt(3);
                    for (int t = 0; t < tunnels; t++)
                    {
                        double x = (double)(ocx * 16 + rng.nextInt(16));
                        double y = (double)(GEN_MIN_Y + 8 + rng.nextInt(100));
                        double z = (double)(ocz * 16 + rng.nextInt(16));
                        double yaw = rng.nextFloat() * Math.PI * 2;
                        double pitch = (rng.nextFloat() - 0.5) * 0.6;
                        int length = 40 + rng.nextInt(60);
                        double radius = 1.4 + rng.nextFloat() * 1.8;
                        for (int i = 0; i < length; i++)
                        {
                            x += detCos(yaw) * detCos(pitch);
                            y += detSin(pitch) * 0.7;
                            z += detSin(yaw) * detCos(pitch);
                            yaw += (rng.nextFloat() - 0.5) * 0.5;
                            pitch = clampD(pitch + (rng.nextFloat() - 0.5) * 0.3, -0.9, 0.9);
                            double r = radius * (1 + detSin((double)i / (double)length * Math.PI) * 0.8);
                            if (rng.nextFloat() < 0.02) { radius = 1.2 + rng.nextFloat() * 2.2; }
                            carveSphere(cx, cz, blocks, x, y, z, r);
                            // occasional branching
                            if (i > 10 && rng.nextFloat() < 0.02 && tunnels < 4)
                            {
                                yaw += (rng.nextBoolean() ? 1 : -1) * (0.8 + rng.nextFloat());
                            }
                        }
                    }
                }
                // ravines: rare
                if (rng.nextFloat() < 0.02)
                {
                    double x = (double)(ocx * 16 + rng.nextInt(16));
                    double z = (double)(ocz * 16 + rng.nextInt(16));
                    int y = 20 + rng.nextInt(40);
                    double yaw = rng.nextFloat() * Math.PI * 2;
                    int length = 60 + rng.nextInt(50);
                    int depth = 24 + rng.nextInt(36);
                    double width = 2.2 + rng.nextFloat() * 2.4;
                    for (int i = 0; i < length; i++)
                    {
                        x += detCos(yaw);
                        z += detSin(yaw);
                        yaw += (rng.nextFloat() - 0.5) * 0.18;
                        double w = width * (1 + detSin((double)i / (double)length * Math.PI) * 0.6);
                        for (int dy = 0; dy < depth; dy++)
                        {
                            int yy = y - dy;
                            double taper = 1 - ((double)dy / (double)depth) * 0.55;
                            carveSphereFlat(cx, cz, blocks, x, (double)yy, z, w * taper, 1.4);
                        }
                    }
                }
            }
        }
    }

    private void carveSphere(int cx, int cz, ushort[] blocks, double x, double y, double z, double r)
    {
        carveSphereFlat(cx, cz, blocks, x, y, z, r, 0.72);
    }

    /// is this carved cell under a water body (open water above within the
    /// column, no solid roof in between)? decides water-fill vs air carve
    private bool columnHasWaterAbove(ushort[] blocks, int idx, int lx, int y, int lz)
    {
        int yy = y + 1;
        while (yy <= SEA)
        {
            int i = ((yy - GEN_MIN_Y) * 16 + lz) * 16 + lx;
            ushort c = blocks[i];
            if (c == WATER) { return true; }
            if (c != AIR && c != LAVA) { return false; }   // solid roof → dry cave
            yy += 1;
        }
        return false;
    }

    private void carveSphereFlat(int cx, int cz, ushort[] blocks, double x, double y, double z, double r, double yScale)
    {
        int baseX = cx * 16, baseZ = cz * 16;
        int x0 = Math.Max(baseX, (int)Math.Floor(x - r)), x1 = Math.Min(baseX + 15, (int)Math.Ceiling(x + r));
        if (x1 < x0) { return; }
        int z0 = Math.Max(baseZ, (int)Math.Floor(z - r)), z1 = Math.Min(baseZ + 15, (int)Math.Ceiling(z + r));
        if (z1 < z0) { return; }
        double ry = r * yScale;
        int y0 = Math.Max(GEN_MIN_Y + 2, (int)Math.Floor(y - ry)), y1 = Math.Min(GEN_MIN_Y + WORLD_H - 1, (int)Math.Ceiling(y + ry));
        if (y1 < y0) { return; }
        for (int yy = y0; yy <= y1; yy++)
        {
            for (int zz = z0; zz <= z1; zz++)
            {
                for (int xx = x0; xx <= x1; xx++)
                {
                    double dx = ((double)xx + 0.5 - x) / r;
                    double dy = ((double)yy + 0.5 - y) / ry;
                    double dz = ((double)zz + 0.5 - z) / r;
                    if (dx * dx + dy * dy + dz * dz < 1)
                    {
                        int idx = ((yy - GEN_MIN_Y) * 16 + (zz - baseZ)) * 16 + (xx - baseX);
                        ushort cur = blocks[idx];
                        if (cur == WATER || cur == LAVA) { continue; } // don't breach water
                        if (cur != AIR)
                        {
                            // below sea level a carver fills with fluid (vanilla
                            // 1.18 semantics) — air ravines through the seafloor
                            // left dry trenches walled by static water faces
                            blocks[idx] = yy <= -55 ? LAVA : (yy <= SEA && columnHasWaterAbove(blocks, idx, xx - baseX, yy, zz - baseZ) ? WATER : AIR);
                        }
                    }
                }
            }
        }
    }

    /// surface pass: biome top/under blocks, deepslate, bedrock, snow/ice
    public void applySurface(int cx, int cz, ref ushort[] blocks, short[] heights, byte[] surfaceBiomes)
    {
        int baseX = cx * 16, baseZ = cz * 16;
        _ = heights;
        ushort[] TERRA_BANDS = new ushort[] {
            B.terracotta, B.orange_terracotta, B.terracotta, B.yellow_terracotta,
            B.white_terracotta, B.light_gray_terracotta, B.orange_terracotta, B.red_terracotta,
            B.terracotta, B.brown_terracotta, B.orange_terracotta, B.terracotta,
        };
        ushort SAND = cell(B.sand), RED_SAND = cell(B.red_sand);
        ushort SANDSTONE = cell(B.sandstone), RED_SANDSTONE = cell(B.red_sandstone);
        int nBands = TERRA_BANDS.Length;
        ushort band(int y, int shift)
        {
            return cell(TERRA_BANDS[(((y + shift) % nBands) + nBands) % nBands]);
        }
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                int wx = baseX + x, wz = baseZ + z;
                int b = (int)surfaceBiomes[z * 16 + x];
                BiomeDef def = biomeDef(b);
                double sn = surfNoise.sample2((double)wx, (double)wz);
                int surfDepth = 3 + (int)Math.Floor((sn * 0.5 + 0.5) * 2);
                int depth = -1;
                bool isBadlands = b == (int)Biome.badlands || b == (int)Biome.erodedBadlands || b == (int)Biome.woodedBadlands;
                int bandShift = (int)Math.Floor(bandOffset.noise2((double)wx / 90, (double)wz / 90) * 4);
                int deepY = 8 - (int)Math.Floor((sn * 0.5 + 0.5) * 8);
                int y = GEN_MIN_Y + WORLD_H - 1;
                while (y >= GEN_MIN_Y)
                {
                    int idx = ((y - GEN_MIN_Y) * 16 + z) * 16 + x;
                    ushort c = blocks[idx];
                    if (c == AIR || c == WATER || c == LAVA) { depth = -1; y -= 1; continue; }
                    if (c != STONE) { depth = -1; y -= 1; continue; }
                    depth += 1;
                    // deepslate transition
                    if (y < deepY)
                    {
                        blocks[idx] = DEEPSLATE;
                        if (depth >= surfDepth) { y -= 1; continue; }
                    }
                    if (depth == 0)
                    {
                        ushort above = y + 1 <= GEN_MIN_Y + WORLD_H - 1 ? blocks[idx + 256] : AIR;
                        bool underwater = above == WATER;
                        if (isBadlands)
                        {
                            blocks[idx] = underwater ? RED_SAND : (y > 74 ? band(y, bandShift) : def.top);
                        }
                        else if (underwater)
                        {
                            blocks[idx] = def.underwaterTop;
                        }
                        else
                        {
                            blocks[idx] = def.top;
                        }
                    }
                    else if (depth < surfDepth)
                    {
                        if (isBadlands && y > 74)
                        {
                            blocks[idx] = band(y, bandShift);
                        }
                        else
                        {
                            blocks[idx] = def.under;
                        }
                    }
                    else if (isBadlands && y > 60 && depth < 16)
                    {
                        blocks[idx] = band(y, bandShift);
                    }
                    // sandstone under sand
                    if (depth >= 1 && depth < surfDepth + 3)
                    {
                        ushort above = blocks[idx + 256];
                        if ((above == SAND || above == RED_SAND) && blocks[idx] == def.under)
                        {
                            blocks[idx] = above == SAND ? SANDSTONE : RED_SANDSTONE;
                        }
                    }
                    y -= 1;
                }
                // bedrock floor
                uint brand = hash2(seed, wx, wz, 99);
                for (int by = GEN_MIN_Y; by < (GEN_MIN_Y + 5); by++)
                {
                    if (by == GEN_MIN_Y || (((brand >> (by - GEN_MIN_Y)) & 3) != 0 && by < GEN_MIN_Y + 1 + (int)((brand >> 8) & 3) + 1))
                    {
                        blocks[((by - GEN_MIN_Y) * 16 + z) * 16 + x] = BEDROCK;
                    }
                }
                blocks[(0 * 16 + z) * 16 + x] = BEDROCK;
            }
        }
    }

    /// freeze + snow-layer pass after features
    public void applySnowAndIce(int cx, int cz, ref ushort[] blocks, byte[] surfaceBiomes)
    {
        ushort ICE = cell(B.ice);
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                int b = (int)surfaceBiomes[z * 16 + x];
                // find top
                int y = GEN_MIN_Y + WORLD_H - 1;
                while (y > GEN_MIN_Y)
                {
                    int idx = ((y - GEN_MIN_Y) * 16 + z) * 16 + x;
                    ushort c = blocks[idx];
                    if (c == AIR) { y -= 1; continue; }
                    if (c == WATER)
                    {
                        if (snowsAt(b, y)) { blocks[idx] = ICE; }
                        break;
                    }
                    ushort id = (ushort)(c >> 4);
                    if (id == B.lava || id == B.ice) { break; }
                    if (snowsAt(b, y + 1) && y + 1 <= GEN_MIN_Y + WORLD_H - 1)
                    {
                        // snow layer on solid full-ish blocks
                        ushort above = blocks[idx + 256];
                        if (above == AIR && solidForSnow(id))
                        {
                            blocks[idx + 256] = cell(B.snow, 0);
                        }
                    }
                    break;
                }
            }
        }
    }

    /// ore placement
    public void placeOres(int cx, int cz, ref ushort[] blocks, byte[] surfaceBiomes)
    {
        var rng = chunkRandom(seed, cx, cz, 4242);
        ushort[] blk = blocks;
        void place(ushort oreStone, ushort oreDeep, int attempts, int minY, int maxY, int size, bool triangular, double discardOnAir = 0)
        {
            for (int a = 0; a < attempts; a++)
            {
                int x = rng.nextInt(16), z = rng.nextInt(16);
                int y;
                if (triangular)
                {
                    double mid = (double)(minY + maxY) / 2;
                    y = (int)Math.Floor(rng.nextTriangular(mid, (double)(maxY - minY) / 2));
                }
                else
                {
                    y = minY + rng.nextInt(Math.Max(1, maxY - minY));
                }
                if (y < GEN_MIN_Y + 1 || y >= GEN_MIN_Y + WORLD_H - 1) { continue; }
                oreBlob(blk, ref rng, x, y - GEN_MIN_Y, z, size, oreStone, oreDeep, discardOnAir);
            }
        }
        // vanilla 1.20 attempts/sizes/bands (the old numbers left mountains
        // nearly iron-free — 8 attempts vs vanilla's 90 — and misplaced coal)
        place(cell(B.coal_ore), cell(B.deepslate_coal_ore), 30, 136, 320, 17, false);
        place(cell(B.coal_ore), cell(B.deepslate_coal_ore), 20, 0, 192, 17, true, 0.5);
        place(cell(B.iron_ore), cell(B.deepslate_iron_ore), 10, -24, 56, 9, true);
        place(cell(B.iron_ore), cell(B.deepslate_iron_ore), 90, 80, 384, 9, true);
        place(cell(B.iron_ore), cell(B.deepslate_iron_ore), 10, -64, 72, 4, false);
        place(cell(B.copper_ore), cell(B.deepslate_copper_ore), 16, -16, 112, 10, true);
        place(cell(B.gold_ore), cell(B.deepslate_gold_ore), 4, -64, 32, 9, true);
        place(cell(B.gold_ore), cell(B.deepslate_gold_ore), 1, -64, -48, 9, false, 0.5);
        place(cell(B.redstone_ore), cell(B.deepslate_redstone_ore), 4, -64, 15, 8, false);
        place(cell(B.redstone_ore), cell(B.deepslate_redstone_ore), 8, -96, -32, 8, true);
        place(cell(B.lapis_ore), cell(B.deepslate_lapis_ore), 2, -32, 32, 7, true);
        place(cell(B.lapis_ore), cell(B.deepslate_lapis_ore), 4, -64, 64, 7, false, 1.0);
        place(cell(B.diamond_ore), cell(B.deepslate_diamond_ore), 7, -144, 16, 8, true, 0.5);
        if (rng.nextInt(9) == 0)
        {   // vanilla "large diamond" — 1 in 9 chunks
            place(cell(B.diamond_ore), cell(B.deepslate_diamond_ore), 1, -144, 16, 12, true, 0.7);
        }
        place(cell(B.diamond_ore), cell(B.deepslate_diamond_ore), 4, -64, -48, 8, false, 1.0);
        // badlands bonus gold (was a feature no-op — badlands had no extra gold)
        int cBiome = (int)surfaceBiomes[8 * 16 + 8];
        if (cBiome == (int)Biome.badlands || cBiome == (int)Biome.woodedBadlands
            || cBiome == (int)Biome.erodedBadlands)
        {
            place(cell(B.gold_ore), cell(B.deepslate_gold_ore), 50, 32, 256, 9, false);
        }
        // emeralds in mountains
        int centerBiome = (int)surfaceBiomes[8 * 16 + 8];
        var emeraldBiomes = new int[] {
            (int)Biome.windsweptHills, (int)Biome.windsweptGravellyHills, (int)Biome.meadow, (int)Biome.grove, (int)Biome.snowySlopes,
            (int)Biome.jaggedPeaks, (int)Biome.frozenPeaks, (int)Biome.stonyPeaks, (int)Biome.cherryGrove, (int)Biome.windsweptForest,
        };
        if (Array.IndexOf(emeraldBiomes, centerBiome) >= 0)
        {
            place(cell(B.emerald_ore), cell(B.deepslate_emerald_ore), 100, -16, 480, 3, true);
        }
        // stone variety blobs
        ushort TUFF = cell(B.tuff);
        var varieties = new (ushort, int, int, int, int)[] {
            (cell(B.granite), 2, 0, 60, 32), (cell(B.diorite), 2, 0, 60, 32), (cell(B.andesite), 2, 0, 60, 32),
            (cell(B.granite), 2, 64, 128, 32), (cell(B.diorite), 2, 64, 128, 32), (cell(B.andesite), 2, 64, 128, 32),
            (TUFF, 3, -64, 0, 24), (cell(B.dirt), 4, 0, 128, 20), (cell(B.gravel), 3, -32, 128, 22),
        };
        foreach (var (vblk, attempts, lo, hi, size) in varieties)
        {
            for (int a = 0; a < attempts; a++)
            {
                int x = rng.nextInt(16), z = rng.nextInt(16);
                int y = lo + rng.nextInt(hi - lo);
                if (y < GEN_MIN_Y + 1 || y >= GEN_MIN_Y + WORLD_H - 1) { continue; }
                oreBlob(blk, ref rng, x, y - GEN_MIN_Y, z, size, vblk, vblk == TUFF ? vblk : DEEPSLATE, 0);
            }
        }
    }

    private void oreBlob(ushort[] blocks, ref RandomX rng, int x, int ry, int z, int size, ushort oreStone, ushort oreDeep, double discardOnAir)
    {
        // vanilla spheroid vein: ellipsoids strung along a random diagonal —
        // the old ±1 random walk left 1-2 block scatter instead of real veins
        ushort TUFF = cell(B.tuff), GRANITE = cell(B.granite), DIORITE = cell(B.diorite), ANDESITE = cell(B.andesite);
        double ang = rng.nextFloat() * Math.PI;
        double fs = (double)size / 8.0;
        double sx = (double)x + 0.5 + detSin(ang) * fs;
        double ex = (double)x + 0.5 - detSin(ang) * fs;
        double sz2 = (double)z + 0.5 + detCos(ang) * fs;
        double ez = (double)z + 0.5 - detCos(ang) * fs;
        double sy = (double)ry + (double)rng.nextInt(3) - 2;
        double ey = (double)ry + (double)rng.nextInt(3) - 2;
        int steps = Math.Max(1, size);
        for (int i = 0; i < steps; i++)
        {
            double t = (double)i / (double)steps;
            double cxp = sx + (ex - sx) * t;
            double cyp = sy + (ey - sy) * t;
            double czp = sz2 + (ez - sz2) * t;
            // bulging radius along the strand, vanilla-style
            double dd = (double)rng.nextFloat() * (double)size / 16.0;
            double r = (detSin(t * Math.PI) + 1) * dd * 0.6 + 0.5;
            int x0 = (int)Math.Floor(cxp - r), x1 = (int)Math.Ceiling(cxp + r);
            int y0 = (int)Math.Floor(cyp - r), y1 = (int)Math.Ceiling(cyp + r);
            int z0 = (int)Math.Floor(czp - r), z1 = (int)Math.Ceiling(czp + r);
            for (int py = y0; py <= y1; py++)
            {
                if (py < 1 || py >= WORLD_H - 1) { continue; }
                double dy = ((double)py + 0.5 - cyp) / r;
                if (dy * dy >= 1) { continue; }
                for (int pz = z0; pz <= z1; pz++)
                {
                    if (pz < 0 || pz > 15) { continue; }
                    double dz = ((double)pz + 0.5 - czp) / r;
                    if (dy * dy + dz * dz >= 1) { continue; }
                    for (int px = x0; px <= x1; px++)
                    {
                        if (px < 0 || px > 15) { continue; }
                        double dx = ((double)px + 0.5 - cxp) / r;
                        if (dx * dx + dy * dy + dz * dz >= 1) { continue; }
                        int idx = (py * 16 + pz) * 16 + px;
                        ushort cur = blocks[idx];
                        if (!(cur == STONE || cur == DEEPSLATE || cur == TUFF
                            || cur == GRANITE || cur == DIORITE || cur == ANDESITE)) { continue; }
                        if (discardOnAir > 0)
                        {
                            bool exposed = false;
                            foreach (var (ddx, ddy, ddz) in new (int, int, int)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                            {
                                int nx2 = px + ddx, ny2 = py + ddy, nz2 = pz + ddz;
                                if (nx2 < 0 || nx2 > 15 || nz2 < 0 || nz2 > 15 || ny2 < 0 || ny2 >= WORLD_H) { continue; }
                                if (blocks[(ny2 * 16 + nz2) * 16 + nx2] == AIR) { exposed = true; break; }
                            }
                            if (exposed && rng.nextFloat() < discardOnAir) { continue; }
                        }
                        blocks[idx] = (cur == DEEPSLATE) ? oreDeep : oreStone;
                    }
                }
            }
        }
    }
}

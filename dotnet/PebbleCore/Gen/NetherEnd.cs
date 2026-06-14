// Nether terrain (the frozen baseline) and The End (the frozen baseline).
//
// Module-level free funcs/vars (NETHER_H, END_H, endPillars, gatewayPositions)
// are surfaced via: public static class NetherEndGlobals
//   -> add `global using static PebbleCore.NetherEndGlobals;`
//
// Cross-layer symbols referenced by name (defined elsewhere in the assembly):
//   BEValue (Gen/Features) with the `.bool(...)` factory; biomeDef (BiomeGlobals);
//   detRound (Gen/Terrain globals). cell/hash2/chunkRandom/clampD/lerpD/detSin/
//   detCos/B are surfaced unqualified via existing global-usings.

using System;
using System.Collections.Generic;

namespace PebbleCore;

public struct PillarSpec
{
    public readonly int x, z;
    public readonly int height;
    public readonly int radius;
    public readonly bool caged;

    public PillarSpec(int x, int z, int height, int radius, bool caged)
    {
        this.x = x;
        this.z = z;
        this.height = height;
        this.radius = radius;
        this.caged = caged;
    }
}

public sealed class NetherGen
{
    public readonly uint seed;
    private readonly FBM density;
    private readonly FBM temp;
    private readonly FBM humid;
    private readonly FBM surf;

    public NetherGen(uint seed)
    {
        this.seed = seed;
        density = new FBM(unchecked(seed + 7001), 4, 1.0 / 90, lacunarity: 2.1, persistence: 0.55);
        temp = new FBM(unchecked(seed + 7002), 3, 1.0 / 320, lacunarity: 2, persistence: 0.5);
        humid = new FBM(unchecked(seed + 7003), 3, 1.0 / 320, lacunarity: 2, persistence: 0.5);
        surf = new FBM(unchecked(seed + 7004), 2, 1.0 / 40, lacunarity: 2, persistence: 0.5);
    }

    public int biomeAt(double x, double z)
    {
        double t = temp.sample2(x, z) * 1.5;
        double h = humid.sample2(x, z) * 1.5;
        if (h < -0.42) return (int)Biome.basaltDeltas;
        if (h > 0.42) return (int)Biome.soulSandValley;
        if (t > 0.3) return (int)Biome.crimsonForest;
        if (t < -0.3) return (int)Biome.warpedForest;
        return (int)Biome.netherWastes;
    }

    /// floor height estimate for structures
    public int heightEstimate(double x, double z)
    {
        for (int y = 40; y < 90; y++)
        {
            if (densityAt(x, y, z) <= 0 && densityAt(x, y - 1, z) > 0) return y;
        }
        return 40;
    }

    private double densityAt(double x, int y, double z)
    {
        double d = density.sample3(x, (double)y * 1.5, z) * 1.15;
        d += clampD((double)(38 - y) / 30, 0, 1) * 1.1;           // solid toward floor
        d += clampD((double)(y - 96) / 26, 0, 1) * 1.6;           // solid toward ceiling
        d -= clampD((double)(y - 60) / 50, 0, 0.25);              // bias open mid
        return d;
    }

    public byte[] fillTerrain(int cx, int cz, ref ushort[] blocks, ref byte[] biomes)
    {
        int baseX = cx * 16, baseZ = cz * 16;
        ushort NETHERRACK = cell(B.netherrack);
        ushort LAVA = cell(B.lava);
        ushort BEDROCK = cell(B.bedrock);
        var surfaceBiomes = new byte[256];
        // density on 5×33×5 lattice, cells 4×4×4
        int NX = 5, NY = NETHER_H / 4 + 1, NZ = 5;
        var lattice = new float[NX * NY * NZ];
        for (int gz = 0; gz < NZ; gz++)
        {
            for (int gx = 0; gx < NX; gx++)
            {
                for (int gy = 0; gy < NY; gy++)
                {
                    lattice[(gy * NZ + gz) * NX + gx] = (float)densityAt((double)(baseX + gx * 4), gy * 4, (double)(baseZ + gz * 4));
                }
            }
        }
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                surfaceBiomes[z * 16 + x] = (byte)biomeAt((double)(baseX + x), (double)(baseZ + z));
                int gx = x >> 2, gz = z >> 2;
                double fx = (double)(x & 3) / 4, fz = (double)(z & 3) / 4;
                for (int gy = 0; gy < NY - 1; gy++)
                {
                    double d000 = (double)lattice[(gy * NZ + gz) * NX + gx], d100 = (double)lattice[(gy * NZ + gz) * NX + gx + 1];
                    double d010 = (double)lattice[(gy * NZ + gz + 1) * NX + gx], d110 = (double)lattice[(gy * NZ + gz + 1) * NX + gx + 1];
                    double d001 = (double)lattice[((gy + 1) * NZ + gz) * NX + gx], d101 = (double)lattice[((gy + 1) * NZ + gz) * NX + gx + 1];
                    double d011 = (double)lattice[((gy + 1) * NZ + gz + 1) * NX + gx], d111 = (double)lattice[((gy + 1) * NZ + gz + 1) * NX + gx + 1];
                    double b0 = lerpD(lerpD(d000, d100, fx), lerpD(d010, d110, fx), fz);
                    double b1 = lerpD(lerpD(d001, d101, fx), lerpD(d011, d111, fx), fz);
                    for (int sy = 0; sy < 4; sy++)
                    {
                        int y = gy * 4 + sy;
                        if (y >= NETHER_H) break;
                        double d = b0 + (b1 - b0) * ((double)sy / 4);
                        int idx = (y * 16 + z) * 16 + x;
                        if (d > 0) blocks[idx] = NETHERRACK;
                        else if (y <= LAVA_SEA) blocks[idx] = LAVA;
                        else blocks[idx] = (ushort)AIR;
                    }
                }
                // bedrock floor + ceiling
                uint br = hash2(seed, baseX + x, baseZ + z, 31);
                for (int d = 0; d < 5; d++)
                {
                    if (d == 0 || (((br >> d) & 3u) != 0 && d < 1 + (int)(br & 3u)))
                    {
                        blocks[(d * 16 + z) * 16 + x] = BEDROCK;
                    }
                    int cy = NETHER_H - 1 - d;
                    if (d == 0 || (((br >> (d + 8)) & 3u) != 0 && d < 1 + (int)((br >> 4) & 3u)))
                    {
                        blocks[(cy * 16 + z) * 16 + x] = BEDROCK;
                    }
                }
                blocks[(0 * 16 + z) * 16 + x] = BEDROCK;
                blocks[((NETHER_H - 1) * 16 + z) * 16 + x] = BEDROCK;
            }
        }
        // biome grid
        for (int qz = 0; qz < 4; qz++)
        {
            for (int qx = 0; qx < 4; qx++)
            {
                byte bm = surfaceBiomes[(qz * 4 + 2) * 16 + qx * 4 + 2];
                for (int qy = 0; qy < NETHER_H / 4; qy++)
                {
                    biomes[(qy * 4 + qz) * 4 + qx] = bm;
                }
            }
        }
        return surfaceBiomes;
    }

    public void applySurface(int cx, int cz, ref ushort[] blocks, byte[] surfaceBiomes)
    {
        ushort NETHERRACK = cell(B.netherrack);
        ushort LAVAC = cell(B.lava);
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                BiomeDef def = biomeDef((int)surfaceBiomes[z * 16 + x]);
                int bm = (int)surfaceBiomes[z * 16 + x];
                double sn = surf.sample2((double)(cx * 16 + x), (double)(cz * 16 + z));
                int depth = bm == (int)Biome.soulSandValley || bm == (int)Biome.basaltDeltas
                    ? 2 + (int)Math.Floor((sn + 1) * 1.5) : 1;
                int below = 0;
                int y = NETHER_H - 2;
                while (y > 1)
                {
                    int idx = (y * 16 + z) * 16 + x;
                    ushort c = blocks[idx];
                    if (c == (ushort)AIR || c == LAVAC) { below = 0; y -= 1; continue; }
                    if (c != NETHERRACK) { below += 1; y -= 1; continue; }
                    if (below == 0)
                    {
                        blocks[idx] = def.top;
                        int d = 1;
                        while (d < depth)
                        {
                            int idx2 = ((y - d) * 16 + z) * 16 + x;
                            if (blocks[idx2] == NETHERRACK) blocks[idx2] = def.under;
                            d += 1;
                        }
                    }
                    below += 1;
                    y -= 1;
                }
            }
        }
    }

    // by-value array (only element mutation, never reassigned) so nested blob()/debris() can use it
    public void placeOres(int cx, int cz, ushort[] blocks)
    {
        var rng = chunkRandom(seed, cx, cz, 777);
        ushort NETHERRACK = cell(B.netherrack);
        void blob(ushort ore, int x, int y, int z, int size)
        {
            int px = x, py = y, pz = z;
            for (int _i = 0; _i < size; _i++)
            {
                px += rng.nextInt(3) - 1; py += rng.nextInt(3) - 1; pz += rng.nextInt(3) - 1;
                if (px < 0 || px > 15 || pz < 0 || pz > 15 || py < 2 || py > NETHER_H - 3) continue;
                int idx = (py * 16 + pz) * 16 + px;
                if (blocks[idx] == NETHERRACK) blocks[idx] = ore;
            }
        }
        for (int _i = 0; _i < 16; _i++) blob(cell(B.nether_quartz_ore), rng.nextInt(16), 10 + rng.nextInt(108), rng.nextInt(16), 10);
        for (int _i = 0; _i < 10; _i++) blob(cell(B.nether_gold_ore), rng.nextInt(16), 10 + rng.nextInt(108), rng.nextInt(16), 8);
        for (int _i = 0; _i < 4; _i++) blob(cell(B.magma_block), rng.nextInt(16), 27 + rng.nextInt(10), rng.nextInt(16), 18);
        for (int _i = 0; _i < 2; _i++) blob(cell(B.blackstone), rng.nextInt(16), 5 + rng.nextInt(28), rng.nextInt(16), 24);
        for (int _i = 0; _i < 2; _i++) blob(cell(B.soul_sand), rng.nextInt(16), 30 + rng.nextInt(35), rng.nextInt(16), 14);
        for (int _i = 0; _i < 2; _i++) blob(cell(B.gravel), rng.nextInt(16), 30 + rng.nextInt(35), rng.nextInt(16), 14);
        // ancient debris — buried checks
        void debris(int y, int size)
        {
            int x = rng.nextInt(16), z = rng.nextInt(16);
            int px = x, py = y, pz = z;
            for (int _i = 0; _i < size; _i++)
            {
                px += rng.nextInt(3) - 1; py += rng.nextInt(3) - 1; pz += rng.nextInt(3) - 1;
                if (px < 0 || px > 15 || pz < 0 || pz > 15 || py < 2 || py > NETHER_H - 3) continue;
                int idx = (py * 16 + pz) * 16 + px;
                if (blocks[idx] != NETHERRACK) continue;
                // require no air neighbors (vanilla "in air: 0")
                bool buried = true;
                foreach (var (dx, dy, dz) in new (int, int, int)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    int nx = px + dx, ny = py + dy, nz = pz + dz;
                    if (nx < 0 || nx > 15 || nz < 0 || nz > 15) continue;
                    if (blocks[(ny * 16 + nz) * 16 + nx] == (ushort)AIR) { buried = false; break; }
                }
                if (buried) blocks[idx] = cell(B.ancient_debris);
            }
        }
        debris(8 + rng.nextInt(15), 3);
        if (rng.nextFloat() < 0.5) debris(8 + rng.nextInt(104), 2);
    }
}

// =============================================================================
// THE END
// =============================================================================

public sealed class EndGen
{
    public readonly uint seed;
    private readonly FBM islandNoise;
    private readonly FBM detail;

    public EndGen(uint seed)
    {
        this.seed = seed;
        islandNoise = new FBM(unchecked(seed + 9001), 3, 1.0 / 180, lacunarity: 2, persistence: 0.55);
        detail = new FBM(unchecked(seed + 9002), 3, 1.0 / 36, lacunarity: 2, persistence: 0.5);
    }

    /// island "presence" 0..1 at a column
    public double islandFactor(double x, double z)
    {
        double dist = Math.Sqrt(x * x + z * z);
        if (dist < 120)
        {
            return clampD(1 - dist / 115, 0, 1) * 3 + 0.4; // main island plateau
        }
        if (dist < 850) return 0; // the void gap
        double n = islandNoise.sample2(x, z);
        return clampD((n - 0.26) * 4.5, 0, 1.2);
    }

    public int heightEstimate(double x, double z)
    {
        double f = islandFactor(x, z);
        if (f <= 0) return 0;
        return 60;
    }

    public int biomeColumn(double x, double z)
    {
        double dist = Math.Sqrt(x * x + z * z);
        if (dist < 130) return (int)Biome.theEnd;
        if (dist < 850) return (int)Biome.smallEndIslands;
        double f = islandFactor(x, z);
        if (f > 0.7) return (int)Biome.endHighlands;
        if (f > 0.2) return (int)Biome.endMidlands;
        return (int)Biome.endBarrens;
    }

    public byte[] fillTerrain(int cx, int cz, ref ushort[] blocks, ref byte[] biomes)
    {
        int baseX = cx * 16, baseZ = cz * 16;
        ushort ENDSTONE = cell(B.end_stone);
        var surfaceBiomes = new byte[256];
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                int wx = baseX + x, wz = baseZ + z;
                surfaceBiomes[z * 16 + x] = (byte)biomeColumn((double)wx, (double)wz);
                double f = islandFactor((double)wx, (double)wz);
                if (f <= 0.02)
                {
                    // tiny floating islands in the gap, rare
                    double h = (double)hash2(seed, wx >> 4, wz >> 4, 0x51A) / 4294967296;
                    if (h < 0.03)
                    {
                        int ox = (wx & 15) - 8, oz = (wz & 15) - 8;
                        if (ox * ox + oz * oz < 12)
                        {
                            for (int y = 58; y <= 60; y++) blocks[(y * 16 + z) * 16 + x] = ENDSTONE;
                        }
                    }
                    continue;
                }
                double surfBump = detail.sample2((double)wx, (double)wz) * 4;
                double top = Math.Min((double)(END_H - 40), 58 + f * 4 + surfBump);
                double thickness = 8 + f * 26;
                double bottom = Math.Max(2, top - thickness - detail.sample2((double)wx + 999, (double)wz - 999) * 10);
                int y2 = (int)Math.Floor(bottom);
                int yTop = (int)Math.Floor(top);
                while (y2 <= yTop)
                {
                    blocks[(y2 * 16 + z) * 16 + x] = ENDSTONE;
                    y2 += 1;
                }
            }
        }
        for (int qz = 0; qz < 4; qz++)
        {
            for (int qx = 0; qx < 4; qx++)
            {
                byte bm = surfaceBiomes[(qz * 4 + 2) * 16 + qx * 4 + 2];
                for (int qy = 0; qy < END_H / 4; qy++) biomes[(qy * 4 + qz) * 4 + qx] = bm;
            }
        }
        return surfaceBiomes;
    }

    /// central island fixtures: pillars + exit portal base
    // by-value array (only element mutation, never reassigned) so nested set() can use it
    public void placeFixtures(int cx, int cz, ushort[] blocks,
                              Action<string, double, double, double, Dictionary<string, BEValue>> addEntity)
    {
        int baseX = cx * 16, baseZ = cz * 16;
        bool inChunk(int x, int z) => x >= baseX && x < baseX + 16 && z >= baseZ && z < baseZ + 16;
        void set(int x, int y, int z, ushort c)
        {
            if (!inChunk(x, z) || y < 0 || y >= END_H) return;
            blocks[(y * 16 + (z - baseZ)) * 16 + (x - baseX)] = c;
        }
        ushort OBS = cell(B.obsidian), BEDROCK = cell(B.bedrock), BARS = cell(B.iron_bars);

        // pillars
        foreach (var p in endPillars(seed))
        {
            if (p.x + p.radius < baseX - 1 || p.x - p.radius > baseX + 16 || p.z + p.radius < baseZ - 1 || p.z - p.radius > baseZ + 16) continue;
            for (int y = 40; y <= p.height; y++)
            {
                for (int dz = -p.radius; dz <= p.radius; dz++)
                {
                    for (int dx = -p.radius; dx <= p.radius; dx++)
                    {
                        if ((double)(dx * dx + dz * dz) <= (double)(p.radius * p.radius) + 0.5) set(p.x + dx, y, p.z + dz, OBS);
                    }
                }
            }
            set(p.x, p.height + 1, p.z, BEDROCK);
            if (p.caged)
            {
                for (int dy = 0; dy <= 3; dy++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            bool edge = Math.Abs(dx) == 2 || Math.Abs(dz) == 2;
                            if (dy == 3 || edge)
                            {
                                if (!(dx == 0 && dz == 0 && dy < 3))
                                {
                                    set(p.x + dx, p.height + 1 + dy, p.z + dz, dy == 3 ? BARS : (edge ? BARS : (ushort)0));
                                }
                            }
                        }
                    }
                }
            }
            // crystal entity on top
            if (inChunk(p.x, p.z))
            {
                addEntity("end_crystal", (double)p.x + 0.5, (double)(p.height + 2), (double)p.z + 0.5,
                          new Dictionary<string, BEValue> { ["pillar"] = BEValue.@bool(true), ["caged"] = BEValue.@bool(p.caged), ["showBottom"] = BEValue.@bool(true) });
            }
        }

        // exit portal base (inactive): bedrock fountain at 0,0
        if (Math.Abs(baseX) <= 16 && Math.Abs(baseZ) <= 16)
        {
            int py = 62;
            for (int dz = -3; dz <= 3; dz++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    int d = Math.Abs(dx) + Math.Abs(dz);
                    if (d <= 4 && !(dx == 0 && dz == 0))
                    {
                        set(dx, py, dz, BEDROCK);
                    }
                }
            }
            for (int dy = 1; dy <= 3; dy++) set(0, py + dy, 0, BEDROCK);
            set(0, py + 4, 0, cell(B.torch, 0));
            // platform under fountain
            for (int dz = -2; dz <= 2; dz++) { for (int dx = -2; dx <= 2; dx++) { set(dx, py - 1, dz, cell(B.end_stone)); } }
        }
    }
}

public static class NetherEndGlobals
{
    public const int NETHER_H = 128;
    internal const int LAVA_SEA = 32;
    internal const int AIR = 0;

    public const int END_H = 256;

    public static List<PillarSpec> endPillars(uint seed)
    {
        var rng = new RandomX(hash2(seed, 7, 7, 0xE17D));
        var heights = new List<int>();
        for (int i = 0; i < 10; i++) heights.Add(76 + i * 3);
        rng.shuffle(heights);
        var @out = new List<PillarSpec>();
        for (int i = 0; i < 10; i++)
        {
            double ang = ((double)i / 10) * Math.PI * 2;
            @out.Add(new PillarSpec(
                x: (int)detRound(detCos(ang) * 42),
                z: (int)detRound(detSin(ang) * 42),
                height: heights[i],
                radius: (int)(3 + (double)(heights[i] - 76) / 9 * 0.34),
                caged: heights[i] <= 79
            ));
        }
        return @out;
    }

    /// 20 gateway positions on a r=96 ring (activated after dragon kills)
    public static List<(int, int)> gatewayPositions()
    {
        var @out = new List<(int, int)>();
        for (int i = 0; i < 20; i++)
        {
            double ang = ((double)i / 20) * Math.PI * 2;
            @out.Add(((int)detRound(detCos(ang) * 96), (int)detRound(detSin(ang) * 96)));
        }
        return @out;
    }
}

// Chunk storage: 16×H×16 cells (UInt16 id<<4|meta), light arrays, heightmap,
// quart-resolution biomes. Layout pinned by the frozen baselines.
// Ported from Sources/PebbleCore/World/Chunk.swift.
//
// Defines static class `WorldGlobals` (CHUNK_W, SECTION_H, DIMS, dimInfo, floorDiv,
// posMod, chunkKey). OPAQUE/LIGHT_OPACITY/B come from BlockIDsCache.cs / BlockDefs.cs.

using System.Collections.Generic;

namespace PebbleCore;

public enum Dim { overworld = 0, nether = 1, end = 2 }

public struct DimInfo
{
    public int minY;
    public int height;
    public int seaLevel;
    public bool hasSky;
    public int ambientLight;
    public double coordScale;
    public bool bedrockFloor;
    public bool bedrockCeil;
    public (double, double, double) fogColor;

    public DimInfo(int minY, int height, int seaLevel, bool hasSky, int ambientLight,
                   double coordScale, bool bedrockFloor, bool bedrockCeil, (double, double, double) fogColor)
    {
        this.minY = minY; this.height = height; this.seaLevel = seaLevel; this.hasSky = hasSky;
        this.ambientLight = ambientLight; this.coordScale = coordScale; this.bedrockFloor = bedrockFloor;
        this.bedrockCeil = bedrockCeil; this.fogColor = fogColor;
    }
}

public static class WorldGlobals
{
    public const int CHUNK_W = 16;
    public const int SECTION_H = 16;

    public static readonly DimInfo[] DIMS = {
        new DimInfo(minY: -64, height: 384, seaLevel: 63, hasSky: true, ambientLight: 0, coordScale: 1, bedrockFloor: true, bedrockCeil: false, fogColor: (0.62, 0.74, 1.0)),
        new DimInfo(minY: 0, height: 128, seaLevel: 32, hasSky: false, ambientLight: 7, coordScale: 8, bedrockFloor: true, bedrockCeil: true, fogColor: (0.2, 0.03, 0.03)),
        new DimInfo(minY: 0, height: 256, seaLevel: 0, hasSky: false, ambientLight: 9, coordScale: 1, bedrockFloor: false, bedrockCeil: false, fogColor: (0.04, 0.03, 0.06)),
    };

    public static DimInfo dimInfo(Dim d) => DIMS[(int)d];

    public static int floorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0 && (a ^ b) < 0) ? q - 1 : q;
    }
    public static int posMod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }
    public static long chunkKey(int cx, int cz)
        => ((long)cx << 32) | ((long)cz & 0xFFFF_FFFFL);
}

public sealed class Chunk
{
    public enum ChunkStatus { empty, generated, lit }

    public readonly int cx;
    public readonly int cz;
    public readonly int minY;
    public readonly int height;
    public readonly int sections;

    public ushort[] blocks;
    public byte[] skyLight;
    public byte[] blockLight;
    /// highest opaque-to-sky block per column (world Y), minY-1 if none
    public short[] heightmap;
    /// quart (4×4×4) biome ids
    public byte[] biomes;
    public bool[] dirty;
    public int version = 0;
    public ChunkStatus status = ChunkStatus.empty;
    public bool modified = false;
    /// keyed by cell index
    public Dictionary<int, BlockEntityData> blockEntities = new();
    public HashSet<int> portalBlocks = new();
    public HashSet<int> sculkSensors = new();

    public Chunk(int cx, int cz, int minY, int height)
    {
        this.cx = cx;
        this.cz = cz;
        this.minY = minY;
        this.height = height;
        sections = (height + WorldGlobals.SECTION_H - 1) / WorldGlobals.SECTION_H;
        int n = WorldGlobals.CHUNK_W * WorldGlobals.CHUNK_W * height;
        blocks = new ushort[n];
        skyLight = new byte[n];
        blockLight = new byte[n];
        heightmap = new short[WorldGlobals.CHUNK_W * WorldGlobals.CHUNK_W];
        for (int i = 0; i < heightmap.Length; i++) heightmap[i] = (short)(minY - 1);
        biomes = new byte[4 * 4 * ((height + 3) / 4)];
        dirty = new bool[sections];
    }

    public int index(int x, int y, int z) => ((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x;

    public bool inYRange(int y) => y >= minY && y < minY + height;

    public ushort get(int x, int y, int z)
    {
        if (y < minY || y >= minY + height) return 0;
        return blocks[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x];
    }
    public void set(int x, int y, int z, ushort cell)
    {
        if (y < minY || y >= minY + height) return;
        blocks[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x] = cell;
    }
    public int getSky(int x, int y, int z)
    {
        if (y >= minY + height) return 15;
        if (y < minY) return 0;
        return skyLight[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x];
    }
    public void setSky(int x, int y, int z, int v)
    {
        if (y < minY || y >= minY + height) return;
        skyLight[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x] = (byte)v;
    }
    public int getBlockLight(int x, int y, int z)
    {
        if (y < minY || y >= minY + height) return 0;
        return blockLight[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x];
    }
    public void setBlockLight(int x, int y, int z, int v)
    {
        if (y < minY || y >= minY + height) return;
        blockLight[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x] = (byte)v;
    }

    public int heightAt(int x, int z) => heightmap[z * WorldGlobals.CHUNK_W + x];

    public void updateHeight(int x, int z)
    {
        int top = minY + height - 1;
        int y = top;
        while (y >= minY)
        {
            ushort c = blocks[((y - minY) * WorldGlobals.CHUNK_W + z) * WorldGlobals.CHUNK_W + x];
            if (c != 0)
            {
                int id = c >> 4;
                if (BlockCaches.OPAQUE[id] == 1 || BlockCaches.LIGHT_OPACITY[id] > 0)
                {
                    heightmap[z * WorldGlobals.CHUNK_W + x] = (short)y;
                    return;
                }
            }
            y -= 1;
        }
        heightmap[z * WorldGlobals.CHUNK_W + x] = (short)(minY - 1);
    }

    public void buildHeightmap()
    {
        for (int z = 0; z < WorldGlobals.CHUNK_W; z++)
            for (int x = 0; x < WorldGlobals.CHUNK_W; x++) updateHeight(x, z);
    }

    public int biomeAt(int x, int y, int z)
    {
        int qy = System.Math.Max(0, System.Math.Min((height >> 2) - 1, (y - minY) >> 2));
        return biomes[(qy * 4 + (z >> 2)) * 4 + (x >> 2)];
    }
    public void setBiome(int qx, int qy, int qz, int biome)
    {
        biomes[(qy * 4 + qz) * 4 + qx] = (byte)biome;
    }

    public void markDirtyAt(int y)
    {
        int s = System.Math.Max(0, System.Math.Min(sections - 1, (y - minY) >> 4));
        dirty[s] = true;
        version += 1;
    }
    public void markAllDirty()
    {
        for (int i = 0; i < sections; i++) dirty[i] = true;
        version += 1;
    }

    // MARK: - block entities & special blocks

    public BlockEntityData getBlockEntity(int x, int y, int z)
        => blockEntities.TryGetValue(index(x, y, z), out var be) ? be : null;
    public void setBlockEntity(int x, int y, int z, BlockEntityData be)
    {
        blockEntities[index(x, y, z)] = be;
        modified = true;
    }
    public void removeBlockEntity(int x, int y, int z)
    {
        if (blockEntities.Remove(index(x, y, z))) modified = true;
    }

    public void trackSpecial(int x, int y, int z, ushort id)
    {
        int idx = index(x, y, z);
        if (id == B.nether_portal || id == B.end_portal || id == B.end_gateway) portalBlocks.Add(idx);
        else portalBlocks.Remove(idx);
        if (id == B.sculk_sensor || id == B.calibrated_sculk_sensor || id == B.sculk_shrieker) sculkSensors.Add(idx);
        else sculkSensors.Remove(idx);
    }

    /// rebuild special-block sets after bulk generation
    public void scanSpecials()
    {
        portalBlocks.Clear();
        sculkSensors.Clear();
        ushort p1 = B.nether_portal, p2 = B.end_portal, p3 = B.end_gateway;
        ushort s1 = B.sculk_sensor, s2 = B.calibrated_sculk_sensor, s3 = B.sculk_shrieker;
        for (int i = 0; i < blocks.Length; i++)
        {
            int id = blocks[i] >> 4;
            if (id == p1 || id == p2 || id == p3) portalBlocks.Add(i);
            else if (id == s1 || id == s2 || id == s3) sculkSensors.Add(i);
        }
    }

    public (int, int, int) idxToWorld(int idx)
    {
        int x = idx & 15;
        int z = (idx >> 4) & 15;
        int y = (idx >> 8) + minY;
        return (cx * 16 + x, y, cz * 16 + z);
    }
}

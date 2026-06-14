// Section mesher — Builds opaque/cutout/translucent
// vertex buffers from a padded 18×18×18 snapshot. Greedy merging for uniform
// full-cube faces, per-vertex AO, smooth light, biome tints, shape geometry.
//
// Vertex layout (28 bytes, 7 uint32 words):
//   float x, y, z      — section-local position
//   float u, v         — tile-local UV (wraps across merged quads)
//   uint32 A: layer(12) | normal(3) | ao(2) | sky(4) | block(4) | emissive(1)
//   uint32 B: tintR(8) | tintG(8) | tintB(8) | anim(3)
// anim: 0 none, 1 water, 2 lava, 3 portal, 4 fire, 5 sway-weak, 6 sway-strong
//
// Defines: static class `MesherGlobals` (module-level free funcs/vars of
// Mesher.swift: P, idxOf, FACE_OFF, WHITE, GRASS_FALLBACK, PACK_TINT_GATE,
// TILE_WIRE_DOT, TILE_WIRE_LINE, buildSectionMesh). The orchestrator should add
// `global using static PebbleCore.MesherGlobals;` so buildSectionMesh and
// PACK_TINT_GATE resolve unqualified.
//
// Relies (unqualified) on already-surfaced static classes:
//   Reg (B, tileId), BlockCaches (OPAQUE/FULL_CUBE/SHAPE_OF/TINT_OF/TRANSLUCENT/
//   TRANSPARENT_RENDER/CULL_SAME/AO_OF/EMISSIVE/IS_LEAVES/TILE_TABLE/isWaterlogged),
//   BlockShapes (shapeBoxes, CellGetter), TerrainGlobals (detRound),
//   BiomeGlobals (BIOMES).

namespace PebbleCore;

public struct MeshInput
{
    /// 18×18×18 padded cells: idx = ((y+1)*18 + (z+1))*18 + (x+1)
    public ushort[] blocks;
    public byte[] skyLight;
    public byte[] blockLight;
    /// per-column biome for 18×18 (padded)
    public byte[] biomes;
    /// emit one quad per face instead of greedy-merged spans
    public bool noMerge;

    public MeshInput(ushort[] blocks, byte[] skyLight, byte[] blockLight, byte[] biomes, bool noMerge = false)
    {
        this.blocks = blocks;
        this.skyLight = skyLight;
        this.blockLight = blockLight;
        this.biomes = biomes;
        this.noMerge = noMerge;
    }
}

public struct MeshLayer
{
    /// interleaved vertex words: x,y,z,u,v as Float bitPattern + A,B raw
    public readonly uint[] data;
    public readonly uint[] idx;
    public readonly int count;

    public MeshLayer(uint[] data, uint[] idx, int count)
    {
        this.data = data;
        this.idx = idx;
        this.count = count;
    }
}

public struct MeshOutput
{
    public readonly MeshLayer opaque;
    public readonly MeshLayer cutout;
    public readonly MeshLayer translucent;

    public MeshOutput(MeshLayer opaque, MeshLayer cutout, MeshLayer translucent)
    {
        this.opaque = opaque;
        this.cutout = cutout;
        this.translucent = translucent;
    }
}

public static class MesherGlobals
{
    internal const int P = 18;

    internal static int idxOf(int x, int y, int z)
        => ((y + 1) * P + (z + 1)) * P + (x + 1);

    // dirs: 0=-y 1=+y 2=-z 3=+z 4=-x 5=+x
    internal static readonly (int, int, int)[] FACE_OFF = new (int, int, int)[]
    {
        (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1), (-1, 0, 0), (1, 0, 0),
    };

    internal const int WHITE = 0xffffff;
    internal const int GRASS_FALLBACK = 0x91bd59;

    /// per-tile biome-tint gate installed by the app when a resource pack is
    /// active (1 = tile keeps its tint, 0 = render untinted because the imported
    /// art is pre-colored). nil — the always-tint vanilla-parity path pebsmoke
    /// goldens exercise — costs nothing in the hot loop.
    public static byte[] PACK_TINT_GATE = null;

    internal static readonly int TILE_WIRE_DOT = tileId("redstone_dust_dot");
    internal static readonly int TILE_WIRE_LINE = tileId("redstone_dust_line");

    public static MeshOutput buildSectionMesh(MeshInput input)
    {
        var m = new SectionMesher(input);
        return m.run();
    }
}

internal sealed class MeshBuilder
{
    public List<float> verts = new();   // 5 per vertex
    public List<uint> uints = new();    // 2 per vertex (A, B)
    public List<uint> idx = new();
    public int vcount = 0;

    // swiftlint:disable:next function_parameter_count
    public void quad(
        double x0, double y0, double z0, double x1, double y1, double z1,
        double x2, double y2, double z2, double x3, double y3, double z3,
        double u0, double v0, double u1, double v1, double u2, double v2, double u3, double v3,
        int layer, int normal,
        int ao0, int ao1, int ao2, int ao3,
        int sky0, int sky1, int sky2, int sky3,
        int blk0, int blk1, int blk2, int blk3,
        int emissive, int tint, int anim)
    {
        uint @base = (uint)vcount;
        double[] xs = { x0, x1, x2, x3 }, ys = { y0, y1, y2, y3 }, zs = { z0, z1, z2, z3 };
        double[] us = { u0, u1, u2, u3 }, vs = { v0, v1, v2, v3 };
        int[] aos = { ao0, ao1, ao2, ao3 }, skys = { sky0, sky1, sky2, sky3 }, blks = { blk0, blk1, blk2, blk3 };
        for (int c = 0; c < 4; c++)
        {
            verts.Add((float)xs[c]); verts.Add((float)ys[c]); verts.Add((float)zs[c]);
            verts.Add((float)us[c]); verts.Add((float)vs[c]);
            uint A = (uint)(layer & 4095) | ((uint)normal << 12) | ((uint)(aos[c] & 3) << 15)
                | ((uint)(skys[c] & 15) << 17) | ((uint)(blks[c] & 15) << 21) | ((uint)emissive << 25);
            uint Bv = (uint)(tint & 0xffffff) | ((uint)anim << 24);
            uints.Add(A); uints.Add(Bv);
            vcount += 1;
        }
        // flip quad for better AO interpolation
        if (ao0 + ao2 > ao1 + ao3)
        {
            idx.Add(@base + 1); idx.Add(@base + 2); idx.Add(@base + 3);
            idx.Add(@base + 3); idx.Add(@base + 0); idx.Add(@base + 1);
        }
        else
        {
            idx.Add(@base); idx.Add(@base + 1); idx.Add(@base + 2);
            idx.Add(@base + 2); idx.Add(@base + 3); idx.Add(@base);
        }
    }

    public MeshLayer build()
    {
        uint[] data = new uint[vcount * 7];
        for (int i = 0; i < vcount; i++)
        {
            data[i * 7] = BitConverter.SingleToUInt32Bits(verts[i * 5]);
            data[i * 7 + 1] = BitConverter.SingleToUInt32Bits(verts[i * 5 + 1]);
            data[i * 7 + 2] = BitConverter.SingleToUInt32Bits(verts[i * 5 + 2]);
            data[i * 7 + 3] = BitConverter.SingleToUInt32Bits(verts[i * 5 + 3]);
            data[i * 7 + 4] = BitConverter.SingleToUInt32Bits(verts[i * 5 + 4]);
            data[i * 7 + 5] = uints[i * 2];
            data[i * 7 + 6] = uints[i * 2 + 1];
        }
        return new MeshLayer(data, idx.ToArray(), vcount);
    }
}

internal sealed class SectionMesher
{
    public readonly MeshInput input;
    public readonly MeshBuilder opaque = new MeshBuilder();
    public readonly MeshBuilder cutout = new MeshBuilder();
    public readonly MeshBuilder translucent = new MeshBuilder();

    public SectionMesher(MeshInput input)
    {
        this.input = input;
    }

    public int cellAt(int x, int y, int z) { return (int)input.blocks[idxOf(x, y, z)]; }
    public int skyAt(int x, int y, int z) { return (int)input.skyLight[idxOf(x, y, z)]; }
    public int blkAt(int x, int y, int z) { return (int)input.blockLight[idxOf(x, y, z)]; }
    public int biomeAt(int x, int z) { return (int)input.biomes[(z + 1) * P + (x + 1)]; }

    public int tintFor(int cell, int x, int z)
    {
        int t = TINT_OF[cell >> 4];
        if (t == 0) { return WHITE; }
        if (!(biomeAt(x, z) < BIOMES.Count) || BIOMES[biomeAt(x, z)] == null) { return GRASS_FALLBACK; }
        var bd = BIOMES[biomeAt(x, z)];
        if (t == 1) { return (int)bd.grassColor; }
        if (t == 2) { return (int)bd.foliageColor; }
        return (int)bd.waterColor;
    }

    public int animFor(int id, Shape shape)
    {
        if (id == (int)B.water) { return 1; }
        if (id == (int)B.lava) { return 2; }
        if (id == (int)B.nether_portal || id == (int)B.end_portal || id == (int)B.end_gateway) { return 3; }
        if (id == (int)B.fire || id == (int)B.soul_fire) { return 4; }
        if (shape == Shape.cross || shape == Shape.crop || shape == Shape.tallCross || shape == Shape.rootsShape || shape == Shape.netherWart || shape == Shape.sweetBerry) { return 6; }
        if (IS_LEAVES[id] == 1) { return 5; }
        return 0;
    }

    /// corner light = avg of the 4 cells adjacent to the corner on the face's outside plane
    public int cornerLight(
        int ox, int oy, int oz,
        int ux, int uy, int uz,
        int vx, int vy, int vz,
        int du, int dv, bool sky)
    {
        Func<int, int, int, int> get = sky ? skyAt : blkAt;
        int a = get(ox, oy, oz);
        int b = get(ox + ux * du, oy + uy * du, oz + uz * du);
        int c = get(ox + vx * dv, oy + vy * dv, oz + vz * dv);
        int dcell = cellAt(ox + ux * du + vx * dv, oy + uy * du + vy * dv, oz + uz * du + vz * dv);
        bool occluded = OPAQUE[dcell >> 4] == 1
            && OPAQUE[cellAt(ox + ux * du, oy + uy * du, oz + uz * du) >> 4] == 1
            && OPAQUE[cellAt(ox + vx * dv, oy + vy * dv, oz + vz * dv) >> 4] == 1;
        int d = occluded ? a : get(ox + ux * du + vx * dv, oy + uy * du + vy * dv, oz + uz * du + vz * dv);
        return (int)detRound((double)(a + b + c + d) / 4);
    }

    public int cornerAO(
        int ox, int oy, int oz,
        int ux, int uy, int uz,
        int vx, int vy, int vz,
        int du, int dv)
    {
        int side1 = (int)OPAQUE[cellAt(ox + ux * du, oy + uy * du, oz + uz * du) >> 4];
        int side2 = (int)OPAQUE[cellAt(ox + vx * dv, oy + vy * dv, oz + vz * dv) >> 4];
        int corner = (int)OPAQUE[cellAt(ox + ux * du + vx * dv, oy + uy * du + vy * dv, oz + uz * du + vz * dv) >> 4];
        if (side1 == 1 && side2 == 1) { return 0; }
        return 3 - (side1 + side2 + corner);
    }

    public struct MaskData
    {
        public int layer;
        public int[] ao;
        public int[] sky;
        public int[] blk;
        public int tint;
        public int emissive;

        public static MaskData Empty()
        {
            return new MaskData
            {
                layer = 0,
                ao = new int[] { 0, 0, 0, 0 },
                sky = new int[] { 0, 0, 0, 0 },
                blk = new int[] { 0, 0, 0, 0 },
                tint = 0,
                emissive = 0,
            };
        }
    }

    public MeshOutput run()
    {
        greedyPass();
        blockPass();
        return new MeshOutput(opaque.build(), cutout.build(), translucent.build());
    }

    private void greedyPass()
    {
        int[] maskKeyA = new int[256];
        // Double, not UInt64: the golden baselines packs this key into a Float64Array
        // where tint*2^32 pushes the sum past 2^53 — the low bits (sky[0]) are
        // rounded away and quads with slightly different corner light merge.
        // That lossy equality is part of the golden baselines canonical mesh output.
        double[] maskKeyB = new double[256];
        int[] maskCell = new int[256];
        MaskData[] maskData = new MaskData[256];
        for (int mfill = 0; mfill < 256; mfill++) { maskData[mfill] = MaskData.Empty(); }

        for (int dir = 0; dir < 6; dir++)
        {
            var (nx, ny, nz) = FACE_OFF[dir];
            // axis setup: w = layer axis, u/v = in-plane axes
            int ux = 0, uy = 0, uz = 0, vx = 0, vy = 0, vz = 0;
            if (dir < 2) { ux = 1; vz = 1; }        // y faces: u=x, v=z
            else if (dir < 4) { ux = 1; vy = 1; }   // z faces: u=x, v=y
            else { uz = 1; vy = 1; }                // x faces: u=z, v=y

            for (int layer = 0; layer < 16; layer++)
            {
                bool maskFilled = false;
                for (int v = 0; v < 16; v++)
                {
                    for (int u = 0; u < 16; u++)
                    {
                        int x = ux * u + vx * v + (dir >= 4 ? layer : 0);
                        int y = uy * u + vy * v + (dir < 2 ? layer : 0);
                        int z = uz * u + vz * v + (dir >= 2 && dir < 4 ? layer : 0);
                        int mi = v * 16 + u;
                        maskCell[mi] = 0;
                        int cell = cellAt(x, y, z);
                        if (cell == 0) { continue; }
                        int id = cell >> 4;
                        if (FULL_CUBE[id] == 0 || SHAPE_OF[id] != (byte)Shape.cube) { continue; }
                        // neighbor cull
                        int ncell = cellAt(x + nx, y + ny, z + nz);
                        int nid = ncell >> 4;
                        if (OPAQUE[nid] == 1) { continue; }
                        if (CULL_SAME[id] == 1 && nid == id) { continue; }
                        if (TRANSLUCENT[id] == 1 && nid == id) { continue; }
                        // light + AO at 4 corners (outside cell)
                        int ox = x + nx, oy = y + ny, oz = z + nz;
                        bool useAO = AO_OF[id] == 1;
                        int[] ao = { 0, 0, 0, 0 };
                        int[] sky = { 0, 0, 0, 0 };
                        int[] blk = { 0, 0, 0, 0 };
                        // corner order: (-u,-v) (+u,-v) (+u,+v) (-u,+v)
                        (int, int)[] dirs = { (-1, -1), (1, -1), (1, 1), (-1, 1) };
                        for (int ci = 0; ci < 4; ci++)
                        {
                            var (du, dv) = dirs[ci];
                            ao[ci] = useAO ? cornerAO(ox, oy, oz, ux, uy, uz, vx, vy, vz, du, dv) : 3;
                            sky[ci] = cornerLight(ox, oy, oz, ux, uy, uz, vx, vy, vz, du, dv, true);
                            blk[ci] = cornerLight(ox, oy, oz, ux, uy, uz, vx, vy, vz, du, dv, false);
                        }
                        int tile = (int)TILE_TABLE[((int)cell << 3) | dir];
                        int tint = tintFor(cell, x, z);
                        if (PACK_TINT_GATE != null && PACK_TINT_GATE[tile] == 0) { tint = WHITE; }
                        maskCell[mi] = cell | 0x10000; // mark filled
                        maskKeyA[mi] = (tile << 8) | (ao[0] << 0) | (ao[1] << 2) | (ao[2] << 4) | (ao[3] << 6) | (id << 20);
                        maskKeyB[mi] = (double)sky[0] + (double)sky[1] * 16 + (double)sky[2] * 256 + (double)sky[3] * 4096
                            + (double)blk[0] * 65536 + (double)blk[1] * 1048576 + (double)blk[2] * 16777216
                            + (double)blk[3] * 268435456 + (double)tint * 4294967296;
                        maskData[mi] = new MaskData { layer = tile, ao = ao, sky = sky, blk = blk, tint = tint, emissive = (int)EMISSIVE[id] };
                        maskFilled = true;
                    }
                }
                if (!maskFilled) { continue; }
                // greedy merge
                for (int v = 0; v < 16; v++)
                {
                    int u = 0;
                    while (u < 16)
                    {
                        int mi = v * 16 + u;
                        if (maskCell[mi] == 0) { u += 1; continue; }
                        int keyA = maskKeyA[mi];
                        double keyB = maskKeyB[mi];
                        // expand width
                        int w = 1;
                        while (!input.noMerge && u + w < 16)
                        {
                            int mj = v * 16 + u + w;
                            if (maskCell[mj] == 0 || maskKeyA[mj] != keyA || maskKeyB[mj] != keyB) { break; }
                            w += 1;
                        }
                        // expand height
                        int h = 1;
                        while (!input.noMerge && v + h < 16)
                        {
                            bool broke = false;
                            for (int du = 0; du < w; du++)
                            {
                                int mj = (v + h) * 16 + u + du;
                                if (maskCell[mj] == 0 || maskKeyA[mj] != keyA || maskKeyB[mj] != keyB) { broke = true; break; }
                            }
                            if (broke) { break; }
                            h += 1;
                        }
                        MaskData d = maskData[mi];
                        int cell = maskCell[mi] & 0xffff;
                        int id = cell >> 4;
                        // emit quad: compute corners
                        int L = layer + (dir == 1 || dir == 3 || dir == 5 ? 1 : 0);
                        var corners = new List<(double, double, double)>();
                        // corner uv space: (u, v), (u+w, v), (u+w, v+h), (u, v+h)
                        (int, int)[] cuv = { (u, v), (u + w, v), (u + w, v + h), (u, v + h) };
                        foreach (var (cu, cv) in cuv)
                        {
                            int x = ux * cu + vx * cv + (dir >= 4 ? L : 0);
                            int y = uy * cu + vy * cv + (dir < 2 ? L : 0);
                            int z = uz * cu + vz * cv + (dir >= 2 && dir < 4 ? L : 0);
                            corners.Add(((double)x, (double)y, (double)z));
                        }
                        // winding: flip for negative dirs so faces point outward
                        bool flip = (dir == 0 || dir == 3 || dir == 4);
                        int[] ord = flip ? new int[] { 0, 3, 2, 1 } : new int[] { 0, 1, 2, 3 };
                        int[] aoO = flip ? new int[] { d.ao[0], d.ao[3], d.ao[2], d.ao[1] } : d.ao;
                        int[] skyO = flip ? new int[] { d.sky[0], d.sky[3], d.sky[2], d.sky[1] } : d.sky;
                        int[] blkO = flip ? new int[] { d.blk[0], d.blk[3], d.blk[2], d.blk[1] } : d.blk;
                        (int, int)[] uvO = flip ? new (int, int)[] { cuv[0], cuv[3], cuv[2], cuv[1] } : cuv;
                        MeshBuilder target = TRANSLUCENT[id] == 1 ? translucent : (TRANSPARENT_RENDER[id] == 1 ? cutout : opaque);
                        target.quad(
                            corners[ord[0]].Item1, corners[ord[0]].Item2, corners[ord[0]].Item3,
                            corners[ord[1]].Item1, corners[ord[1]].Item2, corners[ord[1]].Item3,
                            corners[ord[2]].Item1, corners[ord[2]].Item2, corners[ord[2]].Item3,
                            corners[ord[3]].Item1, corners[ord[3]].Item2, corners[ord[3]].Item3,
                            (double)(uvO[0].Item1 - u), (double)(uvO[0].Item2 - v), (double)(uvO[1].Item1 - u), (double)(uvO[1].Item2 - v),
                            (double)(uvO[2].Item1 - u), (double)(uvO[2].Item2 - v), (double)(uvO[3].Item1 - u), (double)(uvO[3].Item2 - v),
                            d.layer, dir,
                            aoO[0], aoO[1], aoO[2], aoO[3],
                            skyO[0], skyO[1], skyO[2], skyO[3],
                            blkO[0], blkO[1], blkO[2], blkO[3],
                            d.emissive, d.tint, animFor(id, Shape.cube));
                        // clear mask
                        for (int dv2 = 0; dv2 < h; dv2++)
                        {
                            for (int du2 = 0; du2 < w; du2++) { maskCell[(v + dv2) * 16 + u + du2] = 0; }
                        }
                        u += w;
                    }
                }
            }
        }
    }

    private void blockPass()
    {
        var boxes = new List<AABB>();
        for (int y = 0; y < 16; y++)
        {
            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int cell = cellAt(x, y, z);
                    if (cell == 0) { continue; }
                    int id = cell >> 4;
                    Shape shape = (Shape)SHAPE_OF[id];
                    if (shape == Shape.cube || shape == Shape.air) { continue; }
                    int meta = cell & 15;
                    int anim = animFor(id, shape);
                    MeshBuilder target = TRANSLUCENT[id] == 1 ? translucent : cutout;
                    int sky = skyAt(x, y, z), blk = blkAt(x, y, z);
                    int skyUp = skyAt(x, y + 1, z), blkUp = blkAt(x, y + 1, z);
                    int s4 = Math.Max(sky, skyUp), b4 = Math.Max(blk, blkUp);
                    Func<int, int> tileOf = (face) => (int)TILE_TABLE[((int)cell << 3) | face];
                    int tint = tintFor(cell, x, z);
                    if (PACK_TINT_GATE != null)
                    {
                        var gate = PACK_TINT_GATE;
                        bool crossLike = shape == Shape.cross || shape == Shape.crop || shape == Shape.tallCross ||
                            shape == Shape.rootsShape || shape == Shape.netherWart || shape == Shape.web ||
                            shape == Shape.fire || shape == Shape.sweetBerry || shape == Shape.bambooSapling ||
                            shape == Shape.caveVinesShape || shape == Shape.hangingRoots ||
                            shape == Shape.smallDripleafShape || shape == Shape.pitcherCropShape ||
                            shape == Shape.vine || shape == Shape.glowLichen || shape == Shape.sculkVein;
                        if (gate[tileOf(crossLike ? 2 : 1)] == 0) { tint = WHITE; }
                    }

                    if (shape == Shape.liquid)
                    {
                        emitLiquid(target, x, y, z, cell, tileOf(1), tint, anim, s4, b4);
                        continue;
                    }
                    if (shape == Shape.cross || shape == Shape.crop || shape == Shape.tallCross ||
                        shape == Shape.rootsShape || shape == Shape.netherWart || shape == Shape.web ||
                        shape == Shape.fire || shape == Shape.sweetBerry || shape == Shape.bambooSapling ||
                        shape == Shape.caveVinesShape || shape == Shape.hangingRoots || shape == Shape.smallDripleafShape ||
                        shape == Shape.pitcherCropShape)
                    {
                        emitCross(target, x, y, z, tileOf(2), s4, b4, tint, anim, shape == Shape.crop);
                        continue;
                    }
                    if (shape == Shape.vine || shape == Shape.glowLichen || shape == Shape.sculkVein)
                    {
                        emitWallQuads(target, x, y, z, cell, tileOf(2), s4, b4, tint);
                        continue;
                    }
                    if (shape == Shape.redstoneWire)
                    {
                        emitWire(cutout, x, y, z, meta, s4, b4);
                        continue;
                    }
                    if (shape == Shape.rail)
                    {
                        emitRail(cutout, x, y, z, cell >> 4, meta, tileOf(1), s4, b4);
                        continue;
                    }
                    if (shape == Shape.lilyPad || shape == Shape.frogspawn)
                    {
                        emitFlatTop(target, (double)x, (double)y, (double)z, tileOf(1), s4, b4, tint, 1.0 / 16);
                        continue;
                    }
                    if (shape == Shape.portalShape || shape == Shape.endPortalShape)
                    {
                        emitPortal(translucent, x, y, z, cell, tileOf(1), b4);
                        continue;
                    }
                    // generic: render the outline boxes with per-face culling
                    boxes.Clear();
                    shapeBoxes(cell, (dx, dy, dz) => this.cellAt(x + dx, y + dy, z + dz), boxes, false);
                    foreach (var bx in boxes)
                    {
                        emitBox(target, x, y, z, bx, tileOf, s4, b4, tint, anim, (int)EMISSIVE[id]);
                    }
                }
            }
        }
    }

    // --- shape emitters -------------------------------------------------------

    private void emitCross(MeshBuilder b, int x, int y, int z, int tile, int sky, int blk, int tint, int anim, bool inset)
    {
        double o = inset ? 4.0 / 16 : 1.6 / 16;
        double xd = (double)x, yd = (double)y, zd = (double)z;
        (double, double, double, double)[] pairs =
        {
            (o, o, 1 - o, 1 - o), (1 - o, o, o, 1 - o), (o, 1 - o, 1 - o, o), (1 - o, 1 - o, o, o),
        };
        // a real X needs the two PERPENDICULAR diagonals — pairs[3] is pairs[0]
        // reversed, which shipped every cross plant as a single flat plane
        (double, double, double, double)[] firstSet = inset ? new[] { pairs[0], pairs[2] } : new[] { pairs[0], pairs[1] };
        foreach (var (x0, z0, x1, z1) in firstSet)
        {
            foreach (var flip in new[] { false, true })
            {
                var a = flip ? (xd + x1, zd + z1) : (xd + x0, zd + z0);
                var c = flip ? (xd + x0, zd + z0) : (xd + x1, zd + z1);
                b.quad(
                    a.Item1, yd, a.Item2, c.Item1, yd, c.Item2, c.Item1, yd + 1, c.Item2, a.Item1, yd + 1, a.Item2,
                    0, 1, 1, 1, 1, 0, 0, 0,
                    tile, 3, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, anim);
            }
        }
        if (inset)
        {
            foreach (var (x0, z0, x1, z1) in new[] { pairs[1], pairs[3] })
            {
                foreach (var flip in new[] { false, true })
                {
                    var a = flip ? (xd + x1, zd + z1) : (xd + x0, zd + z0);
                    var c = flip ? (xd + x0, zd + z0) : (xd + x1, zd + z1);
                    b.quad(
                        a.Item1, yd, a.Item2, c.Item1, yd, c.Item2, c.Item1, yd + 1, c.Item2, a.Item1, yd + 1, a.Item2,
                        0, 1, 1, 1, 1, 0, 0, 0,
                        tile, 3, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, anim);
                }
            }
        }
    }

    private void emitFlatTop(MeshBuilder b, double x, double y, double z, int tile, int sky, int blk, int tint, double h)
    {
        foreach (var flip in new[] { 0, 1 })
        {
            b.quad(
                x + (flip == 1 ? 1 : 0), y + h, z, x + (flip == 1 ? 0 : 1), y + h, z,
                x + (flip == 1 ? 0 : 1), y + h, z + 1, x + (flip == 1 ? 1 : 0), y + h, z + 1,
                (double)flip, 0, (double)(1 - flip), 0, (double)(1 - flip), 1, (double)flip, 1,
                tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, 0);
        }
    }

    private void emitWallQuads(MeshBuilder b, int x, int y, int z, int cell, int tile, int sky, int blk, int tint)
    {
        int id = cell >> 4, meta = cell & 15;
        double e = 0.8 / 16;
        double xd = (double)x, yd = (double)y, zd = (double)z;
        if (id == (int)B.vine)
        {
            // meta bits: 1=N 2=S 4=W 8=E
            var faces = new List<(double, double, double, double, int)>();
            if ((meta & 1) != 0) { faces.Add((xd, yd, zd + e, xd + 1, 2)); }
            if ((meta & 2) != 0) { faces.Add((xd, yd, zd + 1 - e, xd + 1, 3)); }
            if ((meta & 4) != 0) { faces.Add((xd + e, yd, zd, zd + 1, 4)); }
            if ((meta & 8) != 0) { faces.Add((xd + 1 - e, yd, zd, zd + 1, 5)); }
            foreach (var f in faces)
            {
                if (f.Item5 < 4)
                {
                    foreach (var flip in new[] { false, true })
                    {
                        b.quad(
                            flip ? f.Item3 : f.Item1, yd, f.Item2, flip ? f.Item1 : f.Item3, yd, f.Item2,
                            flip ? f.Item1 : f.Item3, yd + 1, f.Item2, flip ? f.Item3 : f.Item1, yd + 1, f.Item2,
                            0, 1, 1, 1, 1, 0, 0, 0, tile, f.Item5, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, 5);
                    }
                }
                else
                {
                    foreach (var flip in new[] { false, true })
                    {
                        b.quad(
                            f.Item1, yd, flip ? f.Item4 : f.Item3, f.Item1, yd, flip ? f.Item3 : f.Item4,
                            f.Item1, yd + 1, flip ? f.Item3 : f.Item4, f.Item1, yd + 1, flip ? f.Item4 : f.Item3,
                            0, 1, 1, 1, 1, 0, 0, 0, tile, f.Item5, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, 5);
                    }
                }
            }
            // ceiling vine when block above is solid
            if (OPAQUE[cellAt(x, y + 1, z) >> 4] == 1)
            {
                emitFlatTop(b, xd, yd - 1.0 / 16 + 15.0 / 16 - 14.0 / 16, zd, tile, sky, blk, tint, 15.0 / 16);
            }
            return;
        }
        // glow lichen / sculk vein: meta = attach dir 0..5
        int d = meta % 6;
        if (d == 0) { emitFlatTop(b, xd, yd - 14.0 / 16, zd, tile, sky, blk, tint, 15.0 / 16); }
        else if (d == 1) { emitFlatTop(b, xd, yd + 14.2 / 16 - 14.0 / 16, zd, tile, sky, blk, tint, 1.0 / 16 + 14.2 / 16 - 15.0 / 16 + 14.0 / 16); }
        else
        {
            double zq = d == 2 ? zd + e : d == 3 ? zd + 1 - e : 0;
            double xq = d == 4 ? xd + e : d == 5 ? xd + 1 - e : 0;
            foreach (var flip in new[] { false, true })
            {
                if (d < 4)
                {
                    b.quad(
                        flip ? xd + 1 : xd, yd, zq, flip ? xd : xd + 1, yd, zq,
                        flip ? xd : xd + 1, yd + 1, zq, flip ? xd + 1 : xd, yd + 1, zq,
                        0, 1, 1, 1, 1, 0, 0, 0, tile, d, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, 0);
                }
                else
                {
                    b.quad(
                        xq, yd, flip ? zd + 1 : zd, xq, yd, flip ? zd : zd + 1,
                        xq, yd + 1, flip ? zd : zd + 1, xq, yd + 1, flip ? zd + 1 : zd,
                        0, 1, 1, 1, 1, 0, 0, 0, tile, d, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, tint, 0);
                }
            }
        }
    }

    private bool wireConnects(int x, int y, int z, int dx, int dz)
    {
        int n = cellAt(x + dx, y, z + dz);
        ushort nid = (ushort)(n >> 4);
        if (nid == B.redstone_wire || nid == B.repeater || nid == B.repeater_on || nid == B.comparator || nid == B.comparator_on ||
            nid == B.redstone_torch || nid == B.redstone_torch_off || nid == B.lever || nid == B.target || nid == B.daylight_detector) { return true; }
        // wire up a block
        if (OPAQUE[(int)nid] == 1 && (ushort)(cellAt(x + dx, y + 1, z + dz) >> 4) == B.redstone_wire) { return true; }
        // wire down a block
        if (OPAQUE[(int)nid] == 0 && (ushort)(cellAt(x + dx, y - 1, z + dz) >> 4) == B.redstone_wire) { return true; }
        return false;
    }

    private void emitWire(MeshBuilder b, int x, int y, int z, int power, int sky, int blk)
    {
        double bright = 0.3 + ((double)power / 15) * 0.7;
        int tint = ((int)detRound(255 * bright) << 16) | ((int)detRound(40 * bright) << 8) | (int)detRound(30 * bright);
        double h = 0.6 / 16;
        double xd = (double)x, yd = (double)y, zd = (double)z;
        bool[] conn =
        {
            wireConnects(x, y, z, 0, -1), wireConnects(x, y, z, 0, 1),
            wireConnects(x, y, z, -1, 0), wireConnects(x, y, z, 1, 0),
        };
        bool any = conn[0] || conn[1] || conn[2] || conn[3];
        // dot
        if (!any || (conn[0] && conn[1] && conn[2] && conn[3]) || ((conn[0] || conn[1]) && (conn[2] || conn[3])))
        {
            quadFlat(b, xd, yd + h, zd, xd + 1, zd + 1, TILE_WIRE_DOT, sky, blk, tint);
        }
        if (conn[0]) { quadFlatRot(b, xd, yd + h, zd, xd + 1, zd + 0.5, TILE_WIRE_LINE, sky, blk, tint, true); }
        if (conn[1]) { quadFlatRot(b, xd, yd + h, zd + 0.5, xd + 1, zd + 1, TILE_WIRE_LINE, sky, blk, tint, true); }
        if (conn[2]) { quadFlatRot(b, xd, yd + h, zd, xd + 0.5, zd + 1, TILE_WIRE_LINE, sky, blk, tint, false); }
        if (conn[3]) { quadFlatRot(b, xd + 0.5, yd + h, zd, xd + 1, zd + 1, TILE_WIRE_LINE, sky, blk, tint, false); }
        // wall climbs
        for (int d = 0; d < 4; d++)
        {
            int dx = new[] { 0, 0, -1, 1 }[d], dz = new[] { -1, 1, 0, 0 }[d];
            int n = cellAt(x + dx, y, z + dz);
            if (OPAQUE[n >> 4] == 1 && (ushort)(cellAt(x + dx, y + 1, z + dz) >> 4) == B.redstone_wire)
            {
                double e = 0.6 / 16;
                double px = dx == -1 ? xd + e : dx == 1 ? xd + 1 - e : 0;
                double pz = dz == -1 ? zd + e : dz == 1 ? zd + 1 - e : 0;
                foreach (var flip in new[] { false, true })
                {
                    if (d < 2)
                    {
                        b.quad(
                            flip ? xd + 1 : xd, yd, pz, flip ? xd : xd + 1, yd, pz,
                            flip ? xd : xd + 1, yd + 1, pz, flip ? xd + 1 : xd, yd + 1, pz,
                            0, 1, 1, 1, 1, 0, 0, 0, TILE_WIRE_LINE, d + 2, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 1, tint, 0);
                    }
                    else
                    {
                        b.quad(
                            px, yd, flip ? zd + 1 : zd, px, yd, flip ? zd : zd + 1,
                            px, yd + 1, flip ? zd : zd + 1, px, yd + 1, flip ? zd + 1 : zd,
                            0, 1, 1, 1, 1, 0, 0, 0, TILE_WIRE_LINE, d + 2, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 1, tint, 0);
                    }
                }
            }
        }
    }

    private void quadFlat(MeshBuilder b, double x0, double y, double z0, double x1, double z1, int tile, int sky, int blk, int tint)
    {
        b.quad(
            x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1,
            0, 0, 1, 0, 1, 1, 0, 1,
            tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 1, tint, 0);
    }

    private void quadFlatRot(MeshBuilder b, double x0, double y, double z0, double x1, double z1, int tile, int sky, int blk, int tint, bool vertical)
    {
        if (vertical)
        {
            b.quad(
                x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1,
                0, 0, 0, 1, 1, 1, 1, 0,
                tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 1, tint, 0);
        }
        else
        {
            quadFlat(b, x0, y, z0, x1, z1, tile, sky, blk, tint);
        }
    }

    private void emitRail(MeshBuilder b, int x, int y, int z, int id, int meta, int tile, int sky, int blk)
    {
        // plain rail: meta IS the shape (0-9, curves 6-9). powered/detector/
        // activator rails: bit3 = powered flag, shape lives in bits 0-2 — a
        // meta threshold can't distinguish a powered straight rail (8/9)
        // from a plain curve (8/9)
        int shape = id == (int)B.rail ? meta : (meta & 7);
        double h = 1.0 / 16;
        double xd = (double)x, yd = (double)y, zd = (double)z;
        // ascending rails: tilt the quad
        if (shape >= 2 && shape <= 5)
        {
            // 2=ascE 3=ascW 4=ascN 5=ascS
            if (shape == 2)
            { // rises toward +x
                b.quad(xd, yd + h, zd, xd + 1, yd + 1 + h, zd, xd + 1, yd + 1 + h, zd + 1, xd, yd + h, zd + 1,
                       0, 0, 0, 1, 1, 1, 1, 0, tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, WHITE, 0);
            }
            else if (shape == 3)
            {
                b.quad(xd, yd + 1 + h, zd, xd + 1, yd + h, zd, xd + 1, yd + h, zd + 1, xd, yd + 1 + h, zd + 1,
                       0, 0, 0, 1, 1, 1, 1, 0, tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, WHITE, 0);
            }
            else if (shape == 4)
            {
                b.quad(xd, yd + 1 + h, zd, xd + 1, yd + 1 + h, zd, xd + 1, yd + h, zd + 1, xd, yd + h, zd + 1,
                       0, 0, 1, 0, 1, 1, 0, 1, tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, WHITE, 0);
            }
            else
            {
                b.quad(xd, yd + h, zd, xd + 1, yd + h, zd, xd + 1, yd + 1 + h, zd + 1, xd, yd + 1 + h, zd + 1,
                       0, 0, 1, 0, 1, 1, 0, 1, tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, WHITE, 0);
            }
            return;
        }
        // flat: 0 NS, 1 EW, curves 6-9
        if (shape == 1)
        {
            b.quad(xd, yd + h, zd, xd + 1, yd + h, zd, xd + 1, yd + h, zd + 1, xd, yd + h, zd + 1,
                   0, 0, 0, 1, 1, 1, 1, 0, tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, 0, WHITE, 0);
        }
        else
        {
            quadFlat(b, xd, yd + h, zd, xd + 1, zd + 1, tile, sky, blk, WHITE);
        }
    }

    private void emitPortal(MeshBuilder b, int x, int y, int z, int cell, int tile, int blk)
    {
        int id = cell >> 4;
        double xd = (double)x, yd = (double)y, zd = (double)z;
        if (id == (int)B.end_portal || id == (int)B.end_gateway)
        {
            emitFlatTop(b, xd, yd - 4.0 / 16, zd, tile, 15, 15, WHITE, 12.0 / 16);
            return;
        }
        // nether portal: vertical slab along axis (meta 0 = X axis, 1 = Z axis)
        int axis = cell & 1;
        if (axis == 0)
        {
            foreach (var zq in new[] { 6.0 / 16, 10.0 / 16 })
            {
                foreach (var flip in new[] { false, true })
                {
                    double a = flip ? xd + 1 : xd, c = flip ? xd : xd + 1;
                    b.quad(a, yd, zd + zq, c, yd, zd + zq, c, yd + 1, zd + zq, a, yd + 1, zd + zq,
                           0, 1, 1, 1, 1, 0, 0, 0, tile, 2, 3, 3, 3, 3, 15, 15, 15, 15, blk, blk, blk, blk, 1, WHITE, 3);
                }
            }
        }
        else
        {
            foreach (var xq in new[] { 6.0 / 16, 10.0 / 16 })
            {
                foreach (var flip in new[] { false, true })
                {
                    double a = flip ? zd + 1 : zd, c = flip ? zd : zd + 1;
                    b.quad(xd + xq, yd, a, xd + xq, yd, c, xd + xq, yd + 1, c, xd + xq, yd + 1, a,
                           0, 1, 1, 1, 1, 0, 0, 0, tile, 4, 3, 3, 3, 3, 15, 15, 15, 15, blk, blk, blk, blk, 1, WHITE, 3);
                }
            }
        }
    }

    private double heightOfFluid(int cell)
    {
        int level = cell & 7;
        if ((cell & 8) != 0) { return 1; }
        if (level == 0) { return 14.0 / 16; }
        return Math.Max(2.0 / 16, (double)(8 - level) / 8 * 14 / 16);
    }

    private void emitLiquid(MeshBuilder b, int x, int y, int z, int cell, int tile, int tint, int anim, int sky, int blk)
    {
        int id = cell >> 4;
        double xd = (double)x, yd = (double)y, zd = (double)z;
        bool sameAbove = (cellAt(x, y + 1, z) >> 4) == id;
        double hSelf = sameAbove ? 1 : heightOfFluid(cell);
        int em = id == (int)B.lava ? 1 : 0;
        // corner heights: max over the 4 cells sharing the corner
        Func<int, int, double> cornerH = (cx, cz) =>
        {
            if (sameAbove) { return 1; }
            double h = hSelf;
            foreach (var (dx, dz) in new[] { (cx - 1, cz - 1), (cx, cz - 1), (cx - 1, cz), (cx, cz) })
            {
                if (dx == 0 && dz == 0) { continue; }
                int n = cellAt(x + dx, y, z + dz);
                if ((n >> 4) == id)
                {
                    if ((cellAt(x + dx, y + 1, z + dz) >> 4) == id) { return 1; }
                    h = Math.Max(h, heightOfFluid(n));
                }
            }
            return h;
        };
        double h00 = cornerH(0, 0), h10 = cornerH(1, 0), h11 = cornerH(1, 1), h01 = cornerH(0, 1);
        // top face (if not fully covered by same fluid)
        if (!sameAbove)
        {
            b.quad(
                xd, yd + h00, zd, xd + 1, yd + h10, zd, xd + 1, yd + h11, zd + 1, xd, yd + h01, zd + 1,
                0, 0, 1, 0, 1, 1, 0, 1,
                tile, 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, em, tint, anim);
            // underside of surface (visible from below water)
            b.quad(
                xd, yd + h00, zd, xd, yd + h01, zd + 1, xd + 1, yd + h11, zd + 1, xd + 1, yd + h10, zd,
                0, 0, 0, 1, 1, 1, 1, 0,
                tile, 0, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, em, tint, anim);
        }
        // sides + bottom: cull against same fluid and opaque
        (int, int, int)[] sides = { (0, -1, 2), (0, 1, 3), (-1, 0, 4), (1, 0, 5) };
        foreach (var (dx, dz, dir) in sides)
        {
            int n = cellAt(x + dx, y, z + dz);
            int nid = n >> 4;
            if (nid == id || OPAQUE[nid] == 1 || (isWaterlogged((ushort)n) && id == (int)B.water)) { continue; }
            double hA = dir == 2 ? h00 : dir == 3 ? h01 : dir == 4 ? h00 : h10;
            double hB = dir == 2 ? h10 : dir == 3 ? h11 : dir == 4 ? h01 : h11;
            if (dir < 4)
            {
                double zz = dir == 2 ? zd : zd + 1;
                bool flip = dir == 3;
                b.quad(
                    flip ? xd + 1 : xd, yd, zz, flip ? xd : xd + 1, yd, zz,
                    flip ? xd : xd + 1, yd + (flip ? hA : hB), zz, flip ? xd + 1 : xd, yd + (flip ? hB : hA), zz,
                    0, 1, 1, 1, 1, 0, 0, 0,
                    tile, dir, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, em, tint, anim);
            }
            else
            {
                double xx = dir == 4 ? xd : xd + 1;
                bool flip = dir == 4;
                b.quad(
                    xx, yd, flip ? zd + 1 : zd, xx, yd, flip ? zd : zd + 1,
                    xx, yd + (flip ? hA : hB), flip ? zd : zd + 1, xx, yd + (flip ? hB : hA), flip ? zd + 1 : zd,
                    0, 1, 1, 1, 1, 0, 0, 0,
                    tile, dir, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, em, tint, anim);
            }
        }
        int below = cellAt(x, y - 1, z);
        if ((below >> 4) != id && OPAQUE[below >> 4] == 0)
        {
            b.quad(
                xd, yd, zd, xd, yd, zd + 1, xd + 1, yd, zd + 1, xd + 1, yd, zd,
                0, 0, 0, 1, 1, 1, 1, 0,
                tile, 0, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, em, tint, anim);
        }
    }

    private void emitBox(
        MeshBuilder b, int x, int y, int z, AABB box,
        Func<int, int> tileOf, int sky, int blk, int tint, int anim, int emissive)
    {
        double x0 = (double)x + box.x0, y0 = (double)y + box.y0, z0 = (double)z + box.z0;
        double x1 = (double)x + box.x1, y1 = (double)y + box.y1, z1 = (double)z + box.z1;
        Func<double, bool> fullLow = (v) => v <= 0.001;
        Func<double, bool> fullHigh = (v) => v >= 0.999;
        // bottom (0)
        if (!(fullLow(box.y0) && OPAQUE[cellAt(x, y - 1, z) >> 4] == 1))
        {
            b.quad(x0, y0, z0, x0, y0, z1, x1, y0, z1, x1, y0, z0,
                   box.x0, box.z0, box.x0, box.z1, box.x1, box.z1, box.x1, box.z0,
                   tileOf(0), 0, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, emissive, tint, anim);
        }
        // top (1)
        if (!(fullHigh(box.y1) && OPAQUE[cellAt(x, y + 1, z) >> 4] == 1))
        {
            b.quad(x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1,
                   box.x0, box.z0, box.x1, box.z0, box.x1, box.z1, box.x0, box.z1,
                   tileOf(1), 1, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, emissive, tint, anim);
        }
        // north -z (2) — vertex order matches the greedy cube winding (back-culled)
        if (!(fullLow(box.z0) && OPAQUE[cellAt(x, y, z - 1) >> 4] == 1))
        {
            b.quad(x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
                   1 - box.x0, 1 - box.y0, 1 - box.x1, 1 - box.y0, 1 - box.x1, 1 - box.y1, 1 - box.x0, 1 - box.y1,
                   tileOf(2), 2, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, emissive, tint, anim);
        }
        // south +z (3)
        if (!(fullHigh(box.z1) && OPAQUE[cellAt(x, y, z + 1) >> 4] == 1))
        {
            b.quad(x1, y0, z1, x0, y0, z1, x0, y1, z1, x1, y1, z1,
                   box.x1, 1 - box.y0, box.x0, 1 - box.y0, box.x0, 1 - box.y1, box.x1, 1 - box.y1,
                   tileOf(3), 3, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, emissive, tint, anim);
        }
        // west -x (4)
        if (!(fullLow(box.x0) && OPAQUE[cellAt(x - 1, y, z) >> 4] == 1))
        {
            b.quad(x0, y0, z1, x0, y0, z0, x0, y1, z0, x0, y1, z1,
                   box.z1, 1 - box.y0, box.z0, 1 - box.y0, box.z0, 1 - box.y1, box.z1, 1 - box.y1,
                   tileOf(4), 4, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, emissive, tint, anim);
        }
        // east +x (5)
        if (!(fullHigh(box.x1) && OPAQUE[cellAt(x + 1, y, z) >> 4] == 1))
        {
            b.quad(x1, y0, z0, x1, y0, z1, x1, y1, z1, x1, y1, z0,
                   1 - box.z0, 1 - box.y0, 1 - box.z1, 1 - box.y0, 1 - box.z1, 1 - box.y1, 1 - box.z0, 1 - box.y1,
                   tileOf(5), 5, 3, 3, 3, 3, sky, sky, sky, sky, blk, blk, blk, blk, emissive, tint, anim);
        }
    }
}

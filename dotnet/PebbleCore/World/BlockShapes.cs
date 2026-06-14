// Geometry for every block shape — collision boxes and outline boxes in
// block-local [0,1] space, neighbor-aware (fences, panes, walls, stairs). Used by
// physics, raycasting and the mesher. Ported from
// Sources/PebbleCore/World/BlockShapes.swift.
//
// Defines static class `BlockShapes` (module-level free funcs + state):
//   aabb, FACE_DX, FACE_DZ, FACE_OPP, sturdyFace, sturdyTop, sturdyBottom,
//   sturdySide, fenceConnects, wallConnects, paneConnects, ConnKind, connMask,
//   stairsShapeOf, leftOf, rightOf, shapeBoxes, hasAnyCollision, CellGetter.
//
// External engine symbols referenced (surfaced via global using static):
//   blockDefs, B, SHAPE_OF, FULL_CUBE (from Reg / block registry).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class BlockShapes
{
    public delegate int CellGetter(int x, int y, int z);

    public static AABB aabb(double x0, double y0, double z0, double x1, double y1, double z1)
        => new AABB(x0, y0, z0, x1, y1, z1);

    private static readonly AABB FULL = aabb(0, 0, 0, 1, 1, 1);

    /// facing index (0=N -z, 1=S +z, 2=W -x, 3=E +x) → delta
    public static readonly int[] FACE_DX = { 0, 0, -1, 1 };
    public static readonly int[] FACE_DZ = { -1, 1, 0, 0 };
    public static readonly int[] FACE_OPP = { 1, 0, 3, 2 };

    private static Shape shapeOf(int id) => (Shape)SHAPE_OF[id];

    /// Is the given face (Dir: 0=bottom, 1=top, 2-5 sides) of this block solid/full?
    public static bool sturdyFace(int cell, int face)
    {
        int id = cell >> 4;
        if (FULL_CUBE[id] == 1) return blockDefs[id].solid;
        if (face == 1) return sturdyTop(cell);
        if (face == 0) return sturdyBottom(cell);
        return sturdySide(cell);
    }
    /// can a torch/plant/etc stand on top of this cell?
    public static bool sturdyTop(int cell)
    {
        int id = cell >> 4, meta = cell & 15;
        if (FULL_CUBE[id] == 1 && blockDefs[id].solid) return true;
        var shape = shapeOf(id);
        if (shape == Shape.slab) return (meta & 3) != 0; // top slab or double
        if (shape == Shape.stairs) return (meta & 4) != 0;
        if (shape == Shape.farmland || shape == Shape.path || shape == Shape.composter || shape == Shape.daylightSensor) return true;
        if (id == (int)B.soul_sand || id == (int)B.mud || id == (int)B.honey_block) return true;
        if (shape == Shape.hopper) return true;
        return false;
    }
    public static bool sturdyBottom(int cell)
    {
        int id = cell >> 4, meta = cell & 15;
        if (FULL_CUBE[id] == 1 && blockDefs[id].solid) return true;
        var shape = shapeOf(id);
        if (shape == Shape.slab) return (meta & 3) != 1;
        if (shape == Shape.stairs) return (meta & 4) == 0;
        return false;
    }
    public static bool sturdySide(int cell)
    {
        int id = cell >> 4;
        return FULL_CUBE[id] != 0 && blockDefs[id].solid;
    }

    // connection tests -----------------------------------------------------------
    public static bool fenceConnects(int selfId, int other, int dirFacing)
    {
        int oid = other >> 4;
        var oshape = shapeOf(oid);
        if (oshape == Shape.fence)
        {
            bool selfNether = selfId == (int)B.nether_brick_fence;
            bool otherNether = oid == (int)B.nether_brick_fence;
            return selfNether == otherNether;
        }
        if (oshape == Shape.fenceGate)
        {
            int gFacing = other & 3;
            // gate connects on its sides (perpendicular axis)
            bool gateAxisX = gFacing >= 2; // facing W/E → gate spans N-S
            bool dirIsX = dirFacing >= 2;
            return gateAxisX != dirIsX;
        }
        return sturdySide(other);
    }
    public static bool wallConnects(int other, int dirFacing)
    {
        int oid = other >> 4;
        var oshape = shapeOf(oid);
        if (oshape == Shape.wall || oshape == Shape.pane || oshape == Shape.bars || oshape == Shape.fence) return true;
        if (oshape == Shape.fenceGate)
        {
            int gFacing = other & 3;
            bool gateAxisX = gFacing >= 2;
            return gateAxisX != (dirFacing >= 2);
        }
        return sturdySide(other);
    }
    public static bool paneConnects(int selfId, int other)
    {
        int oid = other >> 4;
        var oshape = shapeOf(oid);
        if (oshape == Shape.pane || oshape == Shape.bars || oshape == Shape.wall) return true;
        return sturdySide(other);
    }

    public enum ConnKind { fence, wall, pane }

    /// 4-bit mask NSWE of connections
    public static int connMask(int cell, CellGetter get, ConnKind kind)
    {
        int id = cell >> 4;
        int m = 0;
        for (int f = 0; f < 4; f++)
        {
            int other = get(FACE_DX[f], 0, FACE_DZ[f]);
            bool ok;
            switch (kind)
            {
                case ConnKind.fence: ok = fenceConnects(id, other, f); break;
                case ConnKind.wall: ok = wallConnects(other, f); break;
                default: ok = paneConnects(id, other); break; // .pane
            }
            if (ok) m |= 1 << f;
        }
        return m;
    }

    /// stairs shape: 0 straight, 1 inner-left, 2 inner-right, 3 outer-left, 4 outer-right
    public static int stairsShapeOf(int meta, CellGetter get)
    {
        int facing = meta & 3, half = meta & 4;
        int behind = get(FACE_DX[facing], 0, FACE_DZ[facing]);
        if (SHAPE_OF[behind >> 4] == (byte)Shape.stairs && (behind & 4) == half)
        {
            int bf = behind & 3;
            if ((bf >= 2) != (facing >= 2)) // perpendicular
            {
                int left = leftOf(facing);
                int sideCell = get(FACE_DX[FACE_OPP[bf]], 0, FACE_DZ[FACE_OPP[bf]]);
                if (!(SHAPE_OF[sideCell >> 4] == (byte)Shape.stairs && (sideCell & 3) == facing && (sideCell & 4) == half))
                {
                    return bf == left ? 3 : 4; // outer corner
                }
            }
        }
        int front = get(-FACE_DX[facing], 0, -FACE_DZ[facing]);
        if (SHAPE_OF[front >> 4] == (byte)Shape.stairs && (front & 4) == half)
        {
            int ff = front & 3;
            if ((ff >= 2) != (facing >= 2))
            {
                int left = leftOf(facing);
                int sideCell = get(FACE_DX[ff], 0, FACE_DZ[ff]);
                if (!(SHAPE_OF[sideCell >> 4] == (byte)Shape.stairs && (sideCell & 3) == facing && (sideCell & 4) == half))
                {
                    return ff == left ? 1 : 2; // inner corner
                }
            }
        }
        return 0;
    }
    public static int leftOf(int facing)
    {
        // facing N→left=W, S→E, W→S, E→N
        return new[] { 2, 3, 1, 0 }[facing];
    }
    public static int rightOf(int facing)
    {
        return new[] { 3, 2, 0, 1 }[facing];
    }

    // ---------------------------------------------------------------------------
    // Boxes
    // ---------------------------------------------------------------------------
    public static void shapeBoxes(int cell, CellGetter get, List<AABB> outList, bool forCollision)
    {
        int id = cell >> 4, meta = cell & 15;
        var shape = shapeOf(id);
        switch (shape)
        {
            case Shape.air:
            case Shape.liquid:
            case Shape.fire:
            case Shape.portalShape:
            case Shape.endPortalShape:
                return;
            case Shape.cube:
                if (id == (int)B.soul_sand || id == (int)B.mud) { outList.Add(aabb(0, 0, 0, 1, 14.0 / 16, 1)); return; }
                if (id == (int)B.honey_block || id == (int)B.powder_snow)
                {
                    if (id == (int)B.powder_snow && forCollision) return; // entities sink (special physics)
                    outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 15.0 / 16, 15.0 / 16));
                    return;
                }
                outList.Add(FULL);
                break;
            case Shape.cross:
            case Shape.tallCross:
            case Shape.crop:
            case Shape.netherWart:
            case Shape.sweetBerry:
            case Shape.rootsShape:
            case Shape.web:
            case Shape.caveVinesShape:
            case Shape.hangingRoots:
            case Shape.sporeBlossom:
                if (forCollision) return;
                outList.Add(aabb(2.0 / 16, 0, 2.0 / 16, 14.0 / 16, 13.0 / 16, 14.0 / 16));
                break;
            case Shape.slab:
            {
                int t = meta & 3;
                if (t == 2) { outList.Add(FULL); }
                else if (t == 1) { outList.Add(aabb(0, 0.5, 0, 1, 1, 1)); }
                else { outList.Add(aabb(0, 0, 0, 1, 0.5, 1)); }
                break;
            }
            case Shape.stairs:
            {
                int facing = meta & 3;
                bool top = (meta & 4) != 0;
                int sshape = stairsShapeOf(meta, get);
                // base slab
                outList.Add(top ? aabb(0, 0.5, 0, 1, 1, 1) : aabb(0, 0, 0, 1, 0.5, 1));
                double y0 = top ? 0.0 : 0.5, y1 = top ? 0.5 : 1.0;
                AABB quad(int f)
                {
                    switch (f)
                    {
                        case 0: return aabb(0, y0, 0, 1, y1, 0.5);        // N
                        case 1: return aabb(0, y0, 0.5, 1, y1, 1);        // S
                        case 2: return aabb(0, y0, 0, 0.5, y1, 1);        // W
                        default: return aabb(0.5, y0, 0, 1, y1, 1);       // E
                    }
                }
                AABB corner(int f1, int f2)
                {
                    var a = quad(f1);
                    var b = quad(f2);
                    return aabb(Math.Max(a.x0, b.x0), y0, Math.Max(a.z0, b.z0), Math.Min(a.x1, b.x1), y1, Math.Min(a.z1, b.z1));
                }
                if (sshape == 0) { outList.Add(quad(facing)); }
                else if (sshape == 1) { outList.Add(quad(facing)); outList.Add(corner(FACE_OPP[facing], leftOf(facing))); }
                else if (sshape == 2) { outList.Add(quad(facing)); outList.Add(corner(FACE_OPP[facing], rightOf(facing))); }
                else if (sshape == 3) { outList.Add(corner(facing, leftOf(facing))); }
                else { outList.Add(corner(facing, rightOf(facing))); }
                break;
            }
            case Shape.fence:
            {
                int m = connMask(cell, get, ConnKind.fence);
                double h = forCollision ? 1.5 : 1.0;
                outList.Add(aabb(6.0 / 16, 0, 6.0 / 16, 10.0 / 16, h, 10.0 / 16));
                if ((m & 1) != 0) { outList.Add(aabb(7.0 / 16, 0, 0, 9.0 / 16, h, 6.0 / 16)); }
                if ((m & 2) != 0) { outList.Add(aabb(7.0 / 16, 0, 10.0 / 16, 9.0 / 16, h, 1)); }
                if ((m & 4) != 0) { outList.Add(aabb(0, 0, 7.0 / 16, 6.0 / 16, h, 9.0 / 16)); }
                if ((m & 8) != 0) { outList.Add(aabb(10.0 / 16, 0, 7.0 / 16, 1, h, 9.0 / 16)); }
                break;
            }
            case Shape.wall:
            {
                int m = connMask(cell, get, ConnKind.wall);
                double h = forCollision ? 1.5 : 1.0;
                int above = get(0, 1, 0);
                bool post = (above != 0 && (above >> 4) != (int)B.air) || m == 0 || (m != 3 && m != 12);
                if (post) { outList.Add(aabb(4.0 / 16, 0, 4.0 / 16, 12.0 / 16, h, 12.0 / 16)); }
                double wh = forCollision ? 1.5 : 14.0 / 16;
                if ((m & 1) != 0) { outList.Add(aabb(5.0 / 16, 0, 0, 11.0 / 16, wh, 8.0 / 16)); }
                if ((m & 2) != 0) { outList.Add(aabb(5.0 / 16, 0, 8.0 / 16, 11.0 / 16, wh, 1)); }
                if ((m & 4) != 0) { outList.Add(aabb(0, 0, 5.0 / 16, 8.0 / 16, wh, 11.0 / 16)); }
                if ((m & 8) != 0) { outList.Add(aabb(8.0 / 16, 0, 5.0 / 16, 1, wh, 11.0 / 16)); }
                break;
            }
            case Shape.pane:
            case Shape.bars:
            {
                int m = connMask(cell, get, ConnKind.pane);
                if (m == 0) { outList.Add(aabb(7.0 / 16, 0, 7.0 / 16, 9.0 / 16, 1, 9.0 / 16)); return; }
                outList.Add(aabb(7.0 / 16, 0, 7.0 / 16, 9.0 / 16, 1, 9.0 / 16));
                if ((m & 1) != 0) { outList.Add(aabb(7.0 / 16, 0, 0, 9.0 / 16, 1, 7.0 / 16)); }
                if ((m & 2) != 0) { outList.Add(aabb(7.0 / 16, 0, 9.0 / 16, 9.0 / 16, 1, 1)); }
                if ((m & 4) != 0) { outList.Add(aabb(0, 0, 7.0 / 16, 7.0 / 16, 1, 9.0 / 16)); }
                if ((m & 8) != 0) { outList.Add(aabb(9.0 / 16, 0, 7.0 / 16, 1, 1, 9.0 / 16)); }
                break;
            }
            case Shape.door:
            {
                // figure facing+open+hinge from both halves
                int lower = meta, upper = meta;
                if ((meta & 8) != 0) { lower = get(0, -1, 0) & 15; }
                else { upper = get(0, 1, 0) & 15; }
                int facing = lower & 3;
                bool open = (lower & 4) != 0, hingeRight = (upper & 1) != 0;
                int side;
                if (!open) { side = facing; }
                else { side = hingeRight ? leftOf(facing) : rightOf(facing); }
                switch (side)
                {
                    case 0: outList.Add(aabb(0, 0, 0, 1, 1, 3.0 / 16)); break;
                    case 1: outList.Add(aabb(0, 0, 13.0 / 16, 1, 1, 1)); break;
                    case 2: outList.Add(aabb(0, 0, 0, 3.0 / 16, 1, 1)); break;
                    default: outList.Add(aabb(13.0 / 16, 0, 0, 1, 1, 1)); break;
                }
                break;
            }
            case Shape.trapdoor:
            {
                int facing = meta & 3;
                bool open = (meta & 4) != 0, top = (meta & 8) != 0;
                if (!open)
                {
                    outList.Add(top ? aabb(0, 13.0 / 16, 0, 1, 1, 1) : aabb(0, 0, 0, 1, 3.0 / 16, 1));
                }
                else
                {
                    switch (facing)
                    {
                        case 0: outList.Add(aabb(0, 0, 13.0 / 16, 1, 1, 1)); break;
                        case 1: outList.Add(aabb(0, 0, 0, 1, 1, 3.0 / 16)); break;
                        case 2: outList.Add(aabb(13.0 / 16, 0, 0, 1, 1, 1)); break;
                        default: outList.Add(aabb(0, 0, 0, 3.0 / 16, 1, 1)); break;
                    }
                }
                break;
            }
            case Shape.fenceGate:
            {
                int facing = meta & 3;
                bool open = (meta & 4) != 0;
                if (open && forCollision) return;
                if (forCollision)
                {
                    if (facing < 2) { outList.Add(aabb(0, 0, 6.0 / 16, 1, 1.5, 10.0 / 16)); }
                    else { outList.Add(aabb(6.0 / 16, 0, 0, 10.0 / 16, 1.5, 1)); }
                    return;
                }
                // render: end posts + two bars + center upright (gate across X or Z)
                if (facing < 2)
                {
                    outList.Add(aabb(0, 5.0 / 16, 6.0 / 16, 2.0 / 16, 1, 10.0 / 16));
                    outList.Add(aabb(14.0 / 16, 5.0 / 16, 6.0 / 16, 1, 1, 10.0 / 16));
                    if (!open)
                    {
                        outList.Add(aabb(2.0 / 16, 6.0 / 16, 7.0 / 16, 14.0 / 16, 9.0 / 16, 9.0 / 16));
                        outList.Add(aabb(2.0 / 16, 12.0 / 16, 7.0 / 16, 14.0 / 16, 15.0 / 16, 9.0 / 16));
                        outList.Add(aabb(6.0 / 16, 9.0 / 16, 7.0 / 16, 10.0 / 16, 12.0 / 16, 9.0 / 16));
                    }
                    else
                    {
                        // swung halves folded back to the posts
                        outList.Add(aabb(0, 6.0 / 16, 10.0 / 16, 2.0 / 16, 15.0 / 16, 1));
                        outList.Add(aabb(14.0 / 16, 6.0 / 16, 10.0 / 16, 1, 15.0 / 16, 1));
                    }
                }
                else
                {
                    outList.Add(aabb(6.0 / 16, 5.0 / 16, 0, 10.0 / 16, 1, 2.0 / 16));
                    outList.Add(aabb(6.0 / 16, 5.0 / 16, 14.0 / 16, 10.0 / 16, 1, 1));
                    if (!open)
                    {
                        outList.Add(aabb(7.0 / 16, 6.0 / 16, 2.0 / 16, 9.0 / 16, 9.0 / 16, 14.0 / 16));
                        outList.Add(aabb(7.0 / 16, 12.0 / 16, 2.0 / 16, 9.0 / 16, 15.0 / 16, 14.0 / 16));
                        outList.Add(aabb(7.0 / 16, 9.0 / 16, 6.0 / 16, 9.0 / 16, 12.0 / 16, 10.0 / 16));
                    }
                    else
                    {
                        outList.Add(aabb(10.0 / 16, 6.0 / 16, 0, 1, 15.0 / 16, 2.0 / 16));
                        outList.Add(aabb(10.0 / 16, 6.0 / 16, 14.0 / 16, 1, 15.0 / 16, 2.0 / 16));
                    }
                }
                break;
            }
            case Shape.layer:
            {
                int layers = (meta & 7) + 1;
                if (forCollision)
                {
                    if (layers == 1) return;
                    outList.Add(aabb(0, 0, 0, 1, (double)(layers - 1) * 2 / 16, 1));
                }
                else
                {
                    outList.Add(aabb(0, 0, 0, 1, (double)layers * 2 / 16, 1));
                }
                break;
            }
            case Shape.farmland:
            case Shape.path:
                outList.Add(aabb(0, 0, 0, 1, 15.0 / 16, 1));
                break;
            case Shape.carpet:
            case Shape.lilyPad:
            case Shape.frogspawn:
                if (forCollision && shape == Shape.frogspawn) return;
                outList.Add(aabb(0, 0, 0, 1, shape == Shape.carpet ? 1.0 / 16 : 1.5 / 16, 1));
                break;
            case Shape.torch:
            {
                if (forCollision) return;
                if (meta >= 2 && meta <= 5)
                {
                    // wall torch leaning out of wall at meta dir
                    int f = meta - 2; // 0=N wall...
                    double dx = (double)(new[] { 0, 0, -1, 1 }[f]), dz = (double)(new[] { -1, 1, 0, 0 }[f]);
                    outList.Add(aabb(
                        0.5 - 1.5 / 16 + dx * 5 / 16, 3.0 / 16, 0.5 - 1.5 / 16 + dz * 5 / 16,
                        0.5 + 1.5 / 16 + dx * 5 / 16, 13.0 / 16, 0.5 + 1.5 / 16 + dz * 5 / 16
                    ));
                }
                else if (id == (int)B.lightning_rod)
                {
                    outList.Add(aabb(6.0 / 16, 0, 6.0 / 16, 10.0 / 16, 1, 10.0 / 16));
                }
                else if (id == (int)B.end_rod)
                {
                    outList.Add(aabb(6.0 / 16, 0, 6.0 / 16, 10.0 / 16, 1, 10.0 / 16));
                }
                else
                {
                    outList.Add(aabb(6.0 / 16, 0, 6.0 / 16, 10.0 / 16, 10.0 / 16, 10.0 / 16));
                }
                break;
            }
            case Shape.lever:
            case Shape.button:
                if (forCollision) return;
                outList.Add(aabb(5.0 / 16, 0, 5.0 / 16, 11.0 / 16, 6.0 / 16, 11.0 / 16));
                break;
            case Shape.pressurePlate:
                if (forCollision) return;
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 1.0 / 16, 15.0 / 16));
                break;
            case Shape.rail:
                if (forCollision) return;
                outList.Add(aabb(0, 0, 0, 1, 2.0 / 16, 1));
                break;
            case Shape.redstoneWire:
            case Shape.tripwire:
                if (forCollision) return;
                outList.Add(aabb(0, 0, 0, 1, 1.0 / 16, 1));
                break;
            case Shape.tripwireHook:
                if (forCollision) return;
                outList.Add(aabb(5.0 / 16, 0, 5.0 / 16, 11.0 / 16, 10.0 / 16, 11.0 / 16));
                break;
            case Shape.repeater:
            case Shape.comparator:
            case Shape.daylightSensor:
                outList.Add(aabb(0, 0, 0, 1, shape == Shape.daylightSensor ? 6.0 / 16 : 2.0 / 16, 1));
                break;
            case Shape.chest:
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 14.0 / 16, 15.0 / 16));
                break;
            case Shape.ladder:
            {
                int f = meta & 3;
                switch (f)
                {
                    case 0: outList.Add(aabb(0, 0, 13.0 / 16, 1, 1, 1)); break;
                    case 1: outList.Add(aabb(0, 0, 0, 1, 1, 3.0 / 16)); break;
                    case 2: outList.Add(aabb(13.0 / 16, 0, 0, 1, 1, 1)); break;
                    default: outList.Add(aabb(0, 0, 0, 3.0 / 16, 1, 1)); break;
                }
                if (forCollision) { outList.RemoveAt(outList.Count - 1); } // no collision
                break;
            }
            case Shape.sign:
            {
                if (forCollision) return;
                bool alongX = ((meta + 4) & 15) < 8;   // rotation buckets → board axis
                outList.Add(aabb(7.0 / 16, 0, 7.0 / 16, 9.0 / 16, 9.0 / 16, 9.0 / 16));
                if (alongX) { outList.Add(aabb(0, 9.0 / 16, 6.5 / 16, 1, 1, 9.5 / 16)); }
                else { outList.Add(aabb(6.5 / 16, 9.0 / 16, 0, 9.5 / 16, 1, 1)); }
                break;
            }
            case Shape.wallSign:
                if (forCollision) return;
                switch (meta & 3)
                {
                    case 0: outList.Add(aabb(0, 4.5 / 16, 14.0 / 16, 1, 12.5 / 16, 1)); break;
                    case 1: outList.Add(aabb(0, 4.5 / 16, 0, 1, 12.5 / 16, 2.0 / 16)); break;
                    case 2: outList.Add(aabb(14.0 / 16, 4.5 / 16, 0, 1, 12.5 / 16, 1)); break;
                    default: outList.Add(aabb(0, 4.5 / 16, 0, 2.0 / 16, 12.5 / 16, 1)); break;
                }
                break;
            case Shape.hangingSign:
            {
                if (forCollision) return;
                bool alongX = (meta & 1) == 0;
                if (alongX)
                {
                    outList.Add(aabb(1.0 / 16, 0, 7.0 / 16, 15.0 / 16, 10.0 / 16, 9.0 / 16));
                    outList.Add(aabb(0, 14.0 / 16, 6.0 / 16, 1, 1, 10.0 / 16));
                }
                else
                {
                    outList.Add(aabb(7.0 / 16, 0, 1.0 / 16, 9.0 / 16, 10.0 / 16, 15.0 / 16));
                    outList.Add(aabb(6.0 / 16, 14.0 / 16, 0, 10.0 / 16, 1, 1));
                }
                break;
            }
            case Shape.cake:
            {
                int bites = meta & 7;
                outList.Add(aabb((double)(1 + bites * 2) / 16, 0, 1.0 / 16, 15.0 / 16, 0.5, 15.0 / 16));
                break;
            }
            case Shape.bed:
                outList.Add(aabb(0, 0, 0, 1, 9.0 / 16, 1));
                break;
            case Shape.anvil:
            {
                int facing = meta & 3;
                if (facing < 2) { outList.Add(aabb(3.0 / 16, 0, 0, 13.0 / 16, 1, 1)); }
                else { outList.Add(aabb(0, 0, 3.0 / 16, 1, 1, 13.0 / 16)); }
                break;
            }
            case Shape.hopper:
                outList.Add(aabb(0, 10.0 / 16, 0, 1, 1, 1));
                outList.Add(aabb(4.0 / 16, 4.0 / 16, 4.0 / 16, 12.0 / 16, 10.0 / 16, 12.0 / 16));
                break;
            case Shape.cauldron:
            case Shape.composter:
                outList.Add(aabb(0, 0, 0, 1, 1, 1));
                break;
            case Shape.brewingStand:
                outList.Add(aabb(7.0 / 16, 0, 7.0 / 16, 9.0 / 16, 14.0 / 16, 9.0 / 16));
                outList.Add(aabb(0, 0, 0, 1, 2.0 / 16, 1));
                break;
            case Shape.enchantTable:
                outList.Add(aabb(0, 0, 0, 1, 12.0 / 16, 1));
                break;
            case Shape.lectern:
                if (forCollision) { outList.Add(aabb(0, 0, 0, 1, 12.0 / 16, 1)); return; }
                outList.Add(aabb(0, 0, 0, 1, 2.0 / 16, 1));
                outList.Add(aabb(4.0 / 16, 2.0 / 16, 4.0 / 16, 12.0 / 16, 12.0 / 16, 12.0 / 16));
                outList.Add(aabb(1.0 / 16, 12.0 / 16, 1.0 / 16, 15.0 / 16, 15.0 / 16, 15.0 / 16));
                break;
            case Shape.lantern:
            {
                bool hang = (meta & 1) != 0;
                double y0 = hang ? 1.0 / 16 : 0;
                outList.Add(aabb(5.0 / 16, y0, 5.0 / 16, 11.0 / 16, y0 + 8.0 / 16, 11.0 / 16));
                break;
            }
            case Shape.chain:
            {
                int axis = meta & 3;
                if (axis == 0) { outList.Add(aabb(6.5 / 16, 0, 6.5 / 16, 9.5 / 16, 1, 9.5 / 16)); }
                else if (axis == 1) { outList.Add(aabb(0, 6.5 / 16, 6.5 / 16, 1, 9.5 / 16, 9.5 / 16)); }
                else { outList.Add(aabb(6.5 / 16, 6.5 / 16, 0, 9.5 / 16, 9.5 / 16, 1)); }
                break;
            }
            case Shape.flowerPot:
                if (forCollision) return;
                outList.Add(aabb(5.0 / 16, 0, 5.0 / 16, 11.0 / 16, 6.0 / 16, 11.0 / 16));
                break;
            case Shape.endPortalFrame:
                outList.Add(aabb(0, 0, 0, 1, 13.0 / 16, 1));
                if ((meta & 4) != 0) { outList.Add(aabb(4.0 / 16, 13.0 / 16, 4.0 / 16, 12.0 / 16, 1, 12.0 / 16)); }
                break;
            case Shape.dragonEgg:
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 1, 15.0 / 16));
                break;
            case Shape.conduit:
                outList.Add(aabb(5.0 / 16, 5.0 / 16, 5.0 / 16, 11.0 / 16, 11.0 / 16, 11.0 / 16));
                break;
            case Shape.grindstone:
                outList.Add(aabb(2.0 / 16, 2.0 / 16, 2.0 / 16, 14.0 / 16, 14.0 / 16, 14.0 / 16));
                break;
            case Shape.stonecutter:
                outList.Add(aabb(0, 0, 0, 1, 9.0 / 16, 1));
                break;
            case Shape.bamboo:
                outList.Add(aabb(6.5 / 16, 0, 6.5 / 16, 9.5 / 16, 1, 9.5 / 16));
                break;
            case Shape.cactusShape:
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, forCollision ? 15.0 / 16 : 1, 15.0 / 16));
                break;
            case Shape.bambooSapling:
                if (forCollision) return;
                outList.Add(aabb(4.0 / 16, 0, 4.0 / 16, 12.0 / 16, 12.0 / 16, 12.0 / 16));
                break;
            case Shape.candle:
            {
                if (forCollision) return;
                int count = (meta & 3) + 1;
                double w = (double)(count == 1 ? 2 : count == 2 ? 5 : 6);
                outList.Add(aabb((8 - w) / 16, 0, (8 - w) / 16, (8 + w) / 16, 6.0 / 16, (8 + w) / 16));
                break;
            }
            case Shape.seaPickle:
                if (forCollision) return;
                outList.Add(aabb(3.0 / 16, 0, 3.0 / 16, 13.0 / 16, 6.0 / 16, 13.0 / 16));
                break;
            case Shape.turtleEgg:
            case Shape.snifferEgg:
                outList.Add(aabb(3.0 / 16, 0, 3.0 / 16, 13.0 / 16, shape == Shape.snifferEgg ? 12.0 / 16 : 7.0 / 16, 13.0 / 16));
                break;
            case Shape.dripstone:
            {
                int thickness = (meta >> 1) & 7;
                double w = (double)(thickness == 0 ? 3 : thickness == 1 ? 5 : thickness == 4 ? 3 : 7);
                outList.Add(aabb((8 - w) / 16, 0, (8 - w) / 16, (8 + w) / 16, 1, (8 + w) / 16));
                break;
            }
            case Shape.amethystCluster:
                outList.Add(aabb(3.0 / 16, 0, 3.0 / 16, 13.0 / 16, 7.0 / 16, 13.0 / 16));
                break;
            case Shape.decoratedPot:
                if (forCollision) { outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 1, 15.0 / 16)); return; }
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 13.0 / 16, 15.0 / 16));
                outList.Add(aabb(5.0 / 16, 13.0 / 16, 5.0 / 16, 11.0 / 16, 1, 11.0 / 16));
                break;
            case Shape.campfire:
                if (forCollision) { outList.Add(aabb(0, 0, 0, 1, 7.0 / 16, 1)); return; }
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 1.0 / 16, 15.0 / 16));
                outList.Add(aabb(0, 0, 1.0 / 16, 1, 4.0 / 16, 5.0 / 16));
                outList.Add(aabb(0, 0, 11.0 / 16, 1, 4.0 / 16, 15.0 / 16));
                outList.Add(aabb(1.0 / 16, 3.0 / 16, 0, 5.0 / 16, 7.0 / 16, 1));
                outList.Add(aabb(11.0 / 16, 3.0 / 16, 0, 15.0 / 16, 7.0 / 16, 1));
                break;
            case Shape.scaffolding:
                outList.Add(aabb(0, 14.0 / 16, 0, 1, 1, 1));
                if ((meta & 8) != 0) { outList.Add(aabb(0, 0, 0, 1, 2.0 / 16, 1)); }
                if (!forCollision)
                {
                    // corner posts make the lattice frame visible
                    outList.Add(aabb(0, 0, 0, 2.0 / 16, 14.0 / 16, 2.0 / 16));
                    outList.Add(aabb(14.0 / 16, 0, 0, 1, 14.0 / 16, 2.0 / 16));
                    outList.Add(aabb(0, 0, 14.0 / 16, 2.0 / 16, 14.0 / 16, 1));
                    outList.Add(aabb(14.0 / 16, 0, 14.0 / 16, 1, 14.0 / 16, 1));
                }
                break;
            case Shape.piston:
            {
                bool extended = (meta & 8) != 0;
                if (!extended) { outList.Add(FULL); return; }
                int f = meta & 7;
                switch (f)
                {
                    case 0: outList.Add(aabb(0, 4.0 / 16, 0, 1, 1, 1)); break;
                    case 1: outList.Add(aabb(0, 0, 0, 1, 12.0 / 16, 1)); break;
                    case 2: outList.Add(aabb(0, 0, 4.0 / 16, 1, 1, 1)); break;
                    case 3: outList.Add(aabb(0, 0, 0, 1, 1, 12.0 / 16)); break;
                    case 4: outList.Add(aabb(4.0 / 16, 0, 0, 1, 1, 1)); break;
                    default: outList.Add(aabb(0, 0, 0, 12.0 / 16, 1, 1)); break;
                }
                break;
            }
            case Shape.pistonHead:
            {
                int f = meta & 7;
                switch (f)
                {
                    case 0: outList.Add(aabb(0, 0, 0, 1, 4.0 / 16, 1)); break;
                    case 1: outList.Add(aabb(0, 12.0 / 16, 0, 1, 1, 1)); break;
                    case 2: outList.Add(aabb(0, 0, 0, 1, 1, 4.0 / 16)); break;
                    case 3: outList.Add(aabb(0, 0, 12.0 / 16, 1, 1, 1)); break;
                    case 4: outList.Add(aabb(0, 0, 0, 4.0 / 16, 1, 1)); break;
                    default: outList.Add(aabb(12.0 / 16, 0, 0, 1, 1, 1)); break;
                }
                break;
            }
            case Shape.chorus:
            {
                outList.Add(aabb(4.0 / 16, 4.0 / 16, 4.0 / 16, 12.0 / 16, 12.0 / 16, 12.0 / 16));
                var dirs = new[] { (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1), (-1, 0, 0), (1, 0, 0) };
                foreach (var (dx, dy, dz) in dirs)
                {
                    int o = get(dx, dy, dz);
                    int oid = o >> 4;
                    if (oid == (int)B.chorus_plant || oid == (int)B.chorus_flower || (dy == -1 && oid == (int)B.end_stone))
                    {
                        outList.Add(aabb(
                            4.0 / 16 + (double)dx * 4 / 16, 4.0 / 16 + (double)dy * 4 / 16, 4.0 / 16 + (double)dz * 4 / 16,
                            12.0 / 16 + (double)dx * 4 / 16, 12.0 / 16 + (double)dy * 4 / 16, 12.0 / 16 + (double)dz * 4 / 16
                        ));
                    }
                }
                break;
            }
            case Shape.chorusFlower:
                outList.Add(aabb(2.0 / 16, 2.0 / 16, 2.0 / 16, 14.0 / 16, 14.0 / 16, 14.0 / 16));
                break;
            case Shape.cocoa:
            {
                int age = (meta >> 2) & 3;
                int f = meta & 3;
                double size = (double)(4 + age * 2);
                double dx = (double)FACE_DX[f], dz = (double)FACE_DZ[f];
                double cx = 0.5 + dx * (double)(6 - age) / 16, cz = 0.5 + dz * (double)(6 - age) / 16;
                outList.Add(aabb(cx - size / 32, (12 - size) / 16 - 1.0 / 16, cz - size / 32, cx + size / 32, 12.0 / 16, cz + size / 32));
                break;
            }
            case Shape.vine:
            case Shape.glowLichen:
            case Shape.sculkVein:
                if (forCollision) return;
                outList.Add(aabb(1.0 / 16, 0, 1.0 / 16, 15.0 / 16, 1, 15.0 / 16));
                break;
            case Shape.bigDripleaf:
            {
                int tilt = (meta >> 2) & 3;
                if (forCollision && tilt >= 2) return;
                outList.Add(aabb(0, 11.0 / 16, 0, 1, 15.0 / 16, 1));
                if (!forCollision)
                {
                    outList.Add(aabb(6.0 / 16, 0, 6.0 / 16, 10.0 / 16, 11.0 / 16, 10.0 / 16));   // stem
                }
                break;
            }
            case Shape.smallDripleafShape:
            case Shape.pitcherCropShape:
                if (forCollision) return;
                outList.Add(aabb(2.0 / 16, 0, 2.0 / 16, 14.0 / 16, 14.0 / 16, 14.0 / 16));
                break;
            case Shape.propagule:
                if (forCollision) return;
                outList.Add(aabb(5.0 / 16, 0, 5.0 / 16, 11.0 / 16, 1, 11.0 / 16));
                break;
            case Shape.bell:
                if (forCollision) { outList.Add(aabb(4.0 / 16, 4.0 / 16, 4.0 / 16, 12.0 / 16, 1, 12.0 / 16)); return; }
                // floor bell: two posts + crossbar + bell body with flared lip
                outList.Add(aabb(2.0 / 16, 0, 7.0 / 16, 4.0 / 16, 15.0 / 16, 9.0 / 16));
                outList.Add(aabb(12.0 / 16, 0, 7.0 / 16, 14.0 / 16, 15.0 / 16, 9.0 / 16));
                outList.Add(aabb(2.0 / 16, 13.0 / 16, 7.0 / 16, 14.0 / 16, 15.0 / 16, 9.0 / 16));
                outList.Add(aabb(5.0 / 16, 6.0 / 16, 5.0 / 16, 11.0 / 16, 13.0 / 16, 11.0 / 16));
                outList.Add(aabb(4.0 / 16, 4.0 / 16, 4.0 / 16, 12.0 / 16, 6.0 / 16, 12.0 / 16));
                break;
            case Shape.beacon:
                outList.Add(FULL);
                break;
            case Shape.head:
                outList.Add(aabb(4.0 / 16, 0, 4.0 / 16, 12.0 / 16, 8.0 / 16, 12.0 / 16));
                break;
            case Shape.pointedAttach:
            case Shape.muddyMangroveRoots:
            case Shape.structureVoid:
                outList.Add(FULL);
                break;
        }
    }

    public static bool hasAnyCollision(int cell, CellGetter get)
    {
        var scratch = new List<AABB>();
        shapeBoxes(cell, get, scratch, true);
        return scratch.Count != 0;
    }
}

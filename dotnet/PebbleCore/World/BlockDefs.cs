// The block registry — every block in the game. Cells are UInt16: (id << 4) | meta.
// Tile registration order is deterministic so the atlas painter and mesher agree on
// layer indices. Ported from Sources/PebbleCore/World/BlockDefs.swift.
//
// Defines static class `Reg` (registry globals): registerBlock, tileId, bid, B, ...

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

// MARK: - enums

public enum Shape : byte
{
    cube = 0, cross, slab, stairs, fence, fenceGate, wall, pane, door, trapdoor,
    torch, lever, button, pressurePlate, liquid, layer, farmland, path, crop,
    carpet, rail, chest, ladder, sign, wallSign, hangingSign, cake, bed, anvil, hopper,
    cauldron, brewingStand, enchantTable, lantern, chain, flowerPot, endPortalFrame,
    dragonEgg, conduit, grindstone, stonecutter, bamboo, candle, seaPickle, turtleEgg,
    dripstone, amethystCluster, snifferEgg, decoratedPot, campfire, scaffolding,
    tripwire, tripwireHook, redstoneWire, repeater, comparator, piston, pistonHead,
    daylightSensor, chorus, chorusFlower, cocoa, vine, lilyPad, glowLichen,
    sporeBlossom, hangingRoots, bigDripleaf, smallDripleafShape, propagule, frogspawn,
    web, fire, portalShape, endPortalShape, bell, composter, pointedAttach, tallCross,
    bars, head, lectern, rootsShape, netherWart, sweetBerry, caveVinesShape, beacon,
    structureVoid, bambooSapling, pitcherCropShape, muddyMangroveRoots, sculkVein, cactusShape, air
}

public enum ToolType { pickaxe, axe, shovel, hoe, sword, shears, none }

public enum PistonBehavior : byte { normal = 0, destroy, block, blockEntity }

// MARK: - drops

public struct Drop
{
    public readonly string item;
    public readonly int countMin;
    public readonly int countMax;
    public readonly double chance;

    public Drop(string item, int count = 1, double chance = 1)
    {
        this.item = item;
        countMin = count;
        countMax = count;
        this.chance = chance;
    }
    public Drop(string item, int min, int max, double chance = 1)
    {
        this.item = item;
        countMin = min;
        countMax = max;
        this.chance = chance;
    }
}

public sealed class DropCtx
{
    public readonly int fortune;
    public readonly bool silkTouch;
    public readonly ToolType toolType;
    public readonly int toolTier;
    public readonly bool shears;
    public readonly Func<double> random;

    public DropCtx(Func<double> random, int fortune = 0, bool silkTouch = false,
                   ToolType toolType = ToolType.none, int toolTier = 0, bool shears = false)
    {
        this.fortune = fortune;
        this.silkTouch = silkTouch;
        this.toolType = toolType;
        this.toolTier = toolTier;
        this.shears = shears;
        this.random = random;
    }
}

/// mirrors the baseline drops union: undefined (self) / null / 'item' / [list] / fn
public sealed class DropSpec
{
    public enum Kind { selfDrop, none, item, list, fn }
    public readonly Kind kind;
    public readonly string itemName;
    public readonly Drop[] listArr;
    public readonly Func<int, DropCtx, List<Drop>> fnVal;
    private DropSpec(Kind k, string s = null, Drop[] l = null, Func<int, DropCtx, List<Drop>> f = null)
    { kind = k; itemName = s; listArr = l; fnVal = f; }

    public static readonly DropSpec selfDrop = new DropSpec(Kind.selfDrop);
    public static readonly DropSpec none = new DropSpec(Kind.none);
    public static DropSpec item(string s) => new DropSpec(Kind.item, s: s);
    public static DropSpec list(Drop[] arr) => new DropSpec(Kind.list, l: arr);
    public static DropSpec fn(Func<int, DropCtx, List<Drop>> f) => new DropSpec(Kind.fn, f: f);
}

/// mirrors the baseline tex union: undefined (name) / 'tile' / Int32Array
public sealed class TexSpec
{
    public enum Kind { own, named, faces }
    public readonly Kind kind;
    public readonly string name;
    public readonly int[] facesArr;
    private TexSpec(Kind k, string n = null, int[] f = null) { kind = k; name = n; facesArr = f; }

    public static readonly TexSpec own = new TexSpec(Kind.own);
    public static TexSpec named(string n) => new TexSpec(Kind.named, n: n);
    public static TexSpec faces(int[] arr) => new TexSpec(Kind.faces, f: arr);
}

// MARK: - block definition

public sealed class BlockDef
{
    public readonly int id;
    public readonly string name;
    public readonly string displayName;
    public readonly Shape shape;
    public readonly int[] tex;
    public readonly Func<int, int, int> texFn;
    public readonly bool opaque;
    public readonly bool solid;
    public readonly bool fullCube;
    public readonly bool replaceable;
    public readonly int lightEmit;
    public readonly int lightOpacity;
    public readonly double hardness;
    public readonly double resistance;
    public readonly ToolType tool;
    public readonly int tier;
    public readonly bool requiresTool;
    public readonly string sound;
    public readonly int tint;
    public readonly int flammable;
    public readonly int burnOdds;
    public readonly PistonBehavior piston;
    public readonly bool gravity;
    public readonly bool climbable;
    public readonly bool randomTicks;
    public readonly bool transparentRender;
    public readonly bool translucent;
    public readonly bool emissiveRender;
    public readonly bool cullSame;
    public readonly Func<int, DropCtx, List<Drop>> drops;
    public readonly bool ao;

    public BlockDef(int id, string name, string displayName, Shape shape, int[] tex, Func<int, int, int> texFn,
        bool opaque, bool solid, bool fullCube, bool replaceable, int lightEmit, int lightOpacity,
        double hardness, double resistance, ToolType tool, int tier, bool requiresTool,
        string sound, int tint, int flammable, int burnOdds, PistonBehavior piston,
        bool gravity, bool climbable, bool randomTicks, bool transparentRender,
        bool translucent, bool emissiveRender, bool cullSame, Func<int, DropCtx, List<Drop>> drops, bool ao)
    {
        this.id = id; this.name = name; this.displayName = displayName; this.shape = shape;
        this.tex = tex; this.texFn = texFn; this.opaque = opaque; this.solid = solid;
        this.fullCube = fullCube; this.replaceable = replaceable; this.lightEmit = lightEmit;
        this.lightOpacity = lightOpacity; this.hardness = hardness; this.resistance = resistance;
        this.tool = tool; this.tier = tier; this.requiresTool = requiresTool; this.sound = sound;
        this.tint = tint; this.flammable = flammable; this.burnOdds = burnOdds; this.piston = piston;
        this.gravity = gravity; this.climbable = climbable; this.randomTicks = randomTicks;
        this.transparentRender = transparentRender; this.translucent = translucent;
        this.emissiveRender = emissiveRender; this.cullSame = cullSame; this.drops = drops; this.ao = ao;
    }
}

/// Registry globals (Swift module-level state + free functions).
public static class Reg
{
    // ---- tile registry ----
    private static readonly List<string> tileNames = new();
    private static readonly Dictionary<string, int> tileMap = new();

    public static int tileId(string name)
    {
        if (tileMap.TryGetValue(name, out int t)) return t;
        t = tileNames.Count;
        tileNames.Add(name);
        tileMap[name] = t;
        return t;
    }
    public static List<string> allTileNames() => tileNames;
    public static int tileCount() => tileNames.Count;
    public static string tileName(int idx) => idx >= 0 && idx < tileNames.Count ? tileNames[idx] : "missing";

    public static TexSpec tex(string all)
    {
        int t = tileId(all);
        return TexSpec.faces(new[] { t, t, t, t, t, t });
    }
    public static TexSpec texTB(string top, string bottom, string side)
    {
        int s = tileId(side);
        return TexSpec.faces(new[] { tileId(bottom), tileId(top), s, s, s, s });
    }
    public static TexSpec texCol(string end, string side)
    {
        int e = tileId(end), s = tileId(side);
        return TexSpec.faces(new[] { e, e, s, s, s, s });
    }
    public static TexSpec tex6(string d, string u, string n, string s, string w, string e)
        => TexSpec.faces(new[] { tileId(d), tileId(u), tileId(n), tileId(s), tileId(w), tileId(e) });

    // ---- block registry ----
    public static readonly List<BlockDef> blockDefs = new();
    private static readonly Dictionary<string, int> byName = new();

    public static ResolvedBlockIDs B = new ResolvedBlockIDs();

    public static string prettify(string name)
        => string.Join(" ", name.Split('_').Select(w => w.Substring(0, 1).ToUpperInvariant() + w.Substring(1)));

    public static ushort registerBlock(
        string name,
        Shape shape = Shape.cube,
        TexSpec tex = null,
        Func<int, int, int> texFn = null,
        string display = null,
        bool opaque = true,
        bool solid = true,
        bool? fullCube = null,
        bool replaceable = false,
        int light = 0,
        int? lightOpacity = null,
        double hardness = 1,
        double? resistance = null,
        ToolType tool = ToolType.none,
        int tier = 0,
        bool requiresTool = false,
        string sound = "stone",
        int tint = 0,
        int flammable = 0,
        int burnOdds = 0,
        PistonBehavior piston = PistonBehavior.normal,
        bool gravity = false,
        bool climbable = false,
        bool randomTicks = false,
        bool? transparentRender = null,
        bool translucent = false,
        bool? emissiveRender = null,
        bool cullSame = false,
        DropSpec drops = null,
        bool ao = true)
    {
        int id = blockDefs.Count;
        if (id >= 4096) throw new InvalidOperationException("block id space exhausted");
        if (byName.ContainsKey(name)) throw new InvalidOperationException($"duplicate block: {name}");
        TexSpec texSpec = tex ?? TexSpec.own;   // null default == .own
        int[] texArr;
        switch (texSpec.kind)
        {
            case TexSpec.Kind.own:
                { int t = tileId(name); texArr = new[] { t, t, t, t, t, t }; break; }
            case TexSpec.Kind.named:
                { int t = tileId(texSpec.name); texArr = new[] { t, t, t, t, t, t }; break; }
            default:
                texArr = texSpec.facesArr; break;
        }
        DropSpec dropSpec = drops ?? DropSpec.selfDrop;   // null default == .selfDrop
        Func<int, DropCtx, List<Drop>> dropsFn;
        switch (dropSpec.kind)
        {
            case DropSpec.Kind.selfDrop: dropsFn = null; break;
            case DropSpec.Kind.none: dropsFn = (_, __) => new List<Drop>(); break;
            case DropSpec.Kind.item: { var it = dropSpec.itemName; dropsFn = (_, __) => new List<Drop> { new Drop(it) }; break; }
            case DropSpec.Kind.list: { var arr = dropSpec.listArr; dropsFn = (_, __) => arr.ToList(); break; }
            default: dropsFn = dropSpec.fnVal; break;
        }
        var def = new BlockDef(
            id: id, name: name,
            displayName: display ?? prettify(name),
            shape: shape,
            tex: texArr,
            texFn: texFn,
            opaque: opaque,
            solid: solid,
            fullCube: fullCube ?? (shape == Shape.cube),
            replaceable: replaceable,
            lightEmit: light,
            lightOpacity: lightOpacity ?? (opaque ? 15 : 0),
            hardness: hardness,
            resistance: resistance ?? hardness,
            tool: tool, tier: tier, requiresTool: requiresTool,
            sound: sound, tint: tint, flammable: flammable, burnOdds: burnOdds,
            piston: piston, gravity: gravity, climbable: climbable, randomTicks: randomTicks,
            transparentRender: transparentRender ?? !opaque,
            translucent: translucent,
            emissiveRender: emissiveRender ?? (light >= 10),
            cullSame: cullSame,
            drops: dropsFn,
            ao: ao);
        blockDefs.Add(def);
        byName[name] = id;
        return (ushort)id;
    }

    public static ushort bid(string name)
    {
        if (!byName.TryGetValue(name, out int id)) throw new InvalidOperationException($"unknown block: {name}");
        return (ushort)id;
    }
    public static ushort? bidOpt(string name) => byName.TryGetValue(name, out int id) ? (ushort)id : (ushort?)null;
    public static string blockName(int id) => id < blockDefs.Count ? blockDefs[id].name : "air";
    public static bool blockExists(string name) => byName.ContainsKey(name);
}

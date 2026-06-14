// Block entity data layer — one class with the union of fields (pragmatic,
// mutable in place, JSON-serializable for saves). Behavior lives in the systems
// layer, deterministically. Ported from Sources/PebbleCore/World/BlockEntityData.swift.
//
// Defines static class `BlockEntities` (make* factories + containerSizeFor).

namespace PebbleCore;

public sealed class BlockEntityData
{
    public string type;
    public int x;
    public int y;
    public int z;

    // container / hopper / furnace / brewing / shelf / campfire
    public ItemStack[] items;
    public string lootTable;
    public int? lootSeed;
    public string name;
    public int? cooldown;
    // furnace
    public string kind;            // furnace | blast | smoker
    public int? burnTime;
    public int? burnTotal;
    public int? cookTime;
    public int? cookTotal;
    public double? xpBank;
    // brewing
    public int? brewTime;
    public int? fuel;
    // sign
    public string[] lines;
    public bool? glowing;
    public string color;
    // spawner
    public string mob;
    public int? delay;
    // jukebox
    public ItemStack disc;
    public int? startedTick;
    // beacon
    public string primary;
    public string secondary;
    public int? levels;
    // beehive
    public int? bees;
    public int? honey;
    // shelf
    public int? lastSlot;
    // pot
    public string[] sherds;
    // campfire
    public int[] times;
    // brushable
    public ItemStack item;
    public int? dusted;
    // comparator / note
    public int? output;
    public int? note;
    // piston
    public int? movedCell;
    public int? facing;
    public bool? extending;
    public double? progress;
    public bool? isSourceHead;
    // conduit
    public bool? active;
    public int? eyeTarget;
    // end gateway
    public int? exitX;
    public int? exitY;
    public int? exitZ;
    public bool? exactTeleport;
    // shrieker
    public bool? canSummon;
    public int? viewers;
    public int? shrieking;
    // potted plant (lectern slot reuse, like baseline)
    public string plant;

    public BlockEntityData() { }   // for JSON

    public BlockEntityData(string type, int x, int y, int z)
    {
        this.type = type;
        this.x = x;
        this.y = y;
        this.z = z;
    }

    /// Deep copy for the save queue. Live BEs keep mutating while the queue
    /// serializes (the chest/furnace inventory the player is using), and both
    /// BlockEntityData and its ItemStacks are reference types, so the contained
    /// stacks must be cloned too. Strings are immutable; the primitive arrays
    /// (`lines`/`sherds`/`times`) are cloned so a later edit can't reach the
    /// snapshot. All other fields are value types / nullable value types.
    public BlockEntityData copy()
    {
        var c = (BlockEntityData)MemberwiseClone();
        c.items = CopyStacks(items);
        c.disc = disc?.copy();
        c.item = item?.copy();
        if (lines != null) c.lines = (string[])lines.Clone();
        if (sherds != null) c.sherds = (string[])sherds.Clone();
        if (times != null) c.times = (int[])times.Clone();
        return c;
    }

    private static ItemStack[] CopyStacks(ItemStack[] src)
    {
        if (src == null) return null;
        var dst = new ItemStack[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i]?.copy();
        return dst;
    }
}

public static class BlockEntities
{
    public static BlockEntityData makeContainerBE(int x, int y, int z, int size)
    {
        var be = new BlockEntityData("container", x, y, z);
        be.items = new ItemStack[size];
        return be;
    }
    public static BlockEntityData makeHopperBE(int x, int y, int z)
    {
        var be = new BlockEntityData("hopper", x, y, z);
        be.items = new ItemStack[5];
        be.cooldown = 0;
        return be;
    }
    public static BlockEntityData makeFurnaceBE(int x, int y, int z, string kind)
    {
        var be = new BlockEntityData("furnace", x, y, z);
        be.kind = kind;
        be.items = new ItemStack[3];
        be.burnTime = 0;
        be.burnTotal = 0;
        be.cookTime = 0;
        be.cookTotal = 200;
        be.xpBank = 0;
        return be;
    }
    public static BlockEntityData makeBrewingBE(int x, int y, int z)
    {
        var be = new BlockEntityData("brewing", x, y, z);
        be.items = new ItemStack[5];
        be.brewTime = 0;
        be.fuel = 0;
        return be;
    }
    public static BlockEntityData makeSignBE(int x, int y, int z)
    {
        var be = new BlockEntityData("sign", x, y, z);
        be.lines = new[] { "", "", "", "" };
        be.glowing = false;
        be.color = "black";
        return be;
    }
    public static BlockEntityData makeSpawnerBE(int x, int y, int z, string mob)
    {
        var be = new BlockEntityData("spawner", x, y, z);
        be.mob = mob;
        be.delay = 200;
        return be;
    }
    public static BlockEntityData makeBrushableBE(int x, int y, int z, string lootTable, int lootSeed)
    {
        var be = new BlockEntityData("brushable", x, y, z);
        be.lootTable = lootTable;
        be.lootSeed = lootSeed;
        be.dusted = 0;
        return be;
    }
    public static int containerSizeFor(string blockName)
    {
        if (blockName.Contains("shulker_box")) return 27;
        if (blockName == "chest" || blockName == "trapped_chest" || blockName == "barrel") return 27;
        if (blockName == "dispenser" || blockName == "dropper") return 9;
        return 27;
    }
}

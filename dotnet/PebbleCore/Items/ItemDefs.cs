// Item registry — Block items auto-generate from
// the block registry; registration order mirrors baseline for id parity.
// Ported from Sources/PebbleCore/Items/ItemDefs.swift.
//
// Defines static class `ItemGlobals` (item registry globals):
//   itemDefs, blockToItem, registerItem, iid, iidOpt, itemExists, itemDef,
//   itemName, stack, copyStack, stacksEqual, canMerge, maxStackOf, maxDamageOf,
//   enchLevel.
// (itemByName is module-private — kept private inside ItemGlobals.)

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public struct FoodDef
{
    public readonly int hunger;
    public readonly double saturation;
    public readonly bool alwaysEat;
    public readonly bool meat;
    public readonly bool fast;
    public readonly List<(string effect, int duration, int amplifier, double chance)> effects;

    public FoodDef(int hunger, double saturation, bool alwaysEat = false, bool meat = false, bool fast = false,
                   List<(string effect, int duration, int amplifier, double chance)> effects = null)
    {
        this.hunger = hunger;
        this.saturation = saturation;
        this.alwaysEat = alwaysEat;
        this.meat = meat;
        this.fast = fast;
        this.effects = effects ?? new List<(string effect, int duration, int amplifier, double chance)>();
    }
}

public struct ToolDef
{
    public readonly string type;   // pickaxe/axe/shovel/hoe/sword/shears/flint_and_steel/fishing_rod/bow/crossbow/trident/brush
    public readonly int tier;
    public readonly double speed;
    public readonly double attackDamage;
    public readonly double attackSpeed;
    public readonly int durability;
    public readonly int enchantability;

    public ToolDef(string type, int tier, double speed, double attackDamage, double attackSpeed, int durability, int enchantability)
    {
        this.type = type; this.tier = tier; this.speed = speed;
        this.attackDamage = attackDamage; this.attackSpeed = attackSpeed;
        this.durability = durability; this.enchantability = enchantability;
    }
}

public struct ArmorDef
{
    public readonly int slot;       // 0 head 1 chest 2 legs 3 feet
    public readonly int defense;
    public readonly double toughness;
    public readonly double knockbackRes;
    public readonly int durability;
    public readonly int enchantability;
    public readonly string material;

    public ArmorDef(int slot, int defense, double toughness, double knockbackRes, int durability, int enchantability, string material)
    {
        this.slot = slot; this.defense = defense; this.toughness = toughness;
        this.knockbackRes = knockbackRes; this.durability = durability;
        this.enchantability = enchantability; this.material = material;
    }
}

public sealed class ItemDef
{
    public readonly int id;
    public readonly string name;
    public readonly string displayName;
    public int maxStack;
    public readonly ushort? block;
    public readonly FoodDef? food;
    public readonly ToolDef? tool;
    public readonly ArmorDef? armor;
    public readonly string category;
    public readonly string icon;
    public readonly int burnTime;
    public readonly int rarity;
    public readonly double compostChance;

    public ItemDef(int id, string name, string displayName, int maxStack, ushort? block, FoodDef? food,
                   ToolDef? tool, ArmorDef? armor, string category, string icon, int burnTime, int rarity, double compostChance)
    {
        this.id = id; this.name = name; this.displayName = displayName; this.maxStack = maxStack;
        this.block = block; this.food = food; this.tool = tool; this.armor = armor;
        this.category = category; this.icon = icon; this.burnTime = burnTime;
        this.rarity = rarity; this.compostChance = compostChance;
    }
}

/// enchantment instance on a stack
public struct EnchInstance : IEquatable<EnchInstance>
{
    public string id;
    public int lvl;
    public EnchInstance(string id, int lvl) { this.id = id; this.lvl = lvl; }

    public bool Equals(EnchInstance other) => id == other.id && lvl == other.lvl;
    public override bool Equals(object obj) => obj is EnchInstance o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(id, lvl);
    public static bool operator ==(EnchInstance a, EnchInstance b) => a.Equals(b);
    public static bool operator !=(EnchInstance a, EnchInstance b) => !a.Equals(b);
}

/// armor trim payload — mirrors baseline `data.trim = { pattern, material }`
public struct TrimData : IEquatable<TrimData>
{
    public string pattern;
    public string material;
    public TrimData(string pattern, string material)
    {
        this.pattern = pattern;
        this.material = material;
    }

    public bool Equals(TrimData other) => pattern == other.pattern && material == other.material;
    public override bool Equals(object obj) => obj is TrimData o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(pattern, material);
    public static bool operator ==(TrimData a, TrimData b) => a.Equals(b);
    public static bool operator !=(TrimData a, TrimData b) => !a.Equals(b);
}

/// stack `data` payload (potion id, armor trim, sherds, anvil work, …)
public struct StackData : IEquatable<StackData>
{
    public string potion;
    public TrimData? trim;
    public List<string> sherds;
    public bool? charged;
    public int? priorWork;
    public int? repairUnits;
    /// shulker-box carried inventory
    public List<ItemStack> contents;
    /// lodestone compass target [x, y, z, dim]
    public List<int> lodestone;
    /// firework flight duration
    public int? flight;

    public bool isEmpty =>
        potion == null && trim == null && sherds == null && charged == null
            && priorWork == null && repairUnits == null && contents == null
            && lodestone == null && flight == null;

    public bool Equals(StackData other)
        => potion == other.potion
            && trim == other.trim
            && SeqEqual(sherds, other.sherds)
            && charged == other.charged
            && priorWork == other.priorWork
            && repairUnits == other.repairUnits
            && StackSeqEqual(contents, other.contents)
            && SeqEqual(lodestone, other.lodestone)
            && flight == other.flight;

    public override bool Equals(object obj) => obj is StackData o && Equals(o);
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(potion); h.Add(trim); h.Add(charged); h.Add(priorWork);
        h.Add(repairUnits); h.Add(flight);
        if (sherds != null) foreach (var s in sherds) h.Add(s);
        if (lodestone != null) foreach (var v in lodestone) h.Add(v);
        if (contents != null) foreach (var c in contents) h.Add(c);
        return h.ToHashCode();
    }
    public static bool operator ==(StackData a, StackData b) => a.Equals(b);
    public static bool operator !=(StackData a, StackData b) => !a.Equals(b);

    /// Swift StackData is a struct of value arrays → deep copy. New list instances
    /// with copied elements (ItemStack element refs shared, matching Swift's value
    /// array of reference objects).
    public StackData copy()
    {
        var c = this;
        if (sherds != null) c.sherds = new List<string>(sherds);
        if (contents != null) c.contents = new List<ItemStack>(contents);
        if (lodestone != null) c.lodestone = new List<int>(lodestone);
        return c;
    }

    private static bool SeqEqual<T>(List<T> a, List<T> b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Count != b.Count) return false;
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < a.Count; i++) if (!cmp.Equals(a[i], b[i])) return false;
        return true;
    }
    private static bool StackSeqEqual(List<ItemStack> a, List<ItemStack> b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x == null || y == null) { if (!(x == null && y == null)) return false; }
            else if (!x.Equals(y)) return false;
        }
        return true;
    }
}

/// Reference type on purpose: the golden baselines passes stacks around as shared
/// mutable objects (give() mutates the caller's stack, mending repairs items
/// in place, containers move the same object between slots). All stored
/// fields are value types, so copy() is a deep copy (baseline copyStack).
public sealed class ItemStack : IEquatable<ItemStack>
{
    public int id;
    public int count;
    public int damage;
    public List<EnchInstance> ench;
    public string label;
    public StackData data;

    public ItemStack(int id, int count = 1, int damage = 0, List<EnchInstance> ench = null, string label = null, StackData data = default)
    {
        this.id = id;
        this.count = count;
        this.damage = damage;
        this.ench = ench ?? new List<EnchInstance>();
        this.label = label;
        this.data = data;
    }

    // Swift's stored fields are value types, so copy() is a deep copy. ench is a
    // value array in Swift → clone the List (EnchInstance is a struct → independent);
    // data deep-copies its collections via StackData.copy().
    public ItemStack copy()
        => new ItemStack(id, count, damage: damage, ench: new List<EnchInstance>(ench), label: label, data: data.copy());

    public bool Equals(ItemStack b)
    {
        if (b == null) return false;
        return id == b.id && count == b.count && damage == b.damage
            && EnchListEqual(ench, b.ench) && label == b.label && data == b.data;
    }
    public override bool Equals(object obj) => Equals(obj as ItemStack);
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(id); h.Add(count); h.Add(damage); h.Add(label); h.Add(data);
        if (ench != null) foreach (var e in ench) h.Add(e);
        return h.ToHashCode();
    }
    public static bool operator ==(ItemStack a, ItemStack b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }
    public static bool operator !=(ItemStack a, ItemStack b) => !(a == b);

    private static bool EnchListEqual(List<EnchInstance> a, List<EnchInstance> b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }
}

public static class ItemGlobals
{
    public static ItemStack copyStack(ItemStack s) => s?.copy();

    public static List<ItemDef> itemDefs = new();
    private static Dictionary<string, int> itemByName = new();
    public static int[] blockToItem = Enumerable.Repeat(-1, 4096).ToArray();

    public static int registerItem(
        string name,
        string display = null,
        int? maxStack = null,
        ushort? block = null,
        FoodDef? food = null,
        ToolDef? tool = null,
        ArmorDef? armor = null,
        string category = "none",
        string icon = null,
        int burnTime = 0,
        int rarity = 0,
        double compostChance = 0)
    {
        if (itemByName.ContainsKey(name)) throw new InvalidOperationException($"duplicate item: {name}");
        int id = itemDefs.Count;
        var def = new ItemDef(
            id: id, name: name,
            displayName: display ?? prettify(name),
            maxStack: maxStack ?? ((tool != null || armor != null) ? 1 : 64),
            block: block, food: food, tool: tool, armor: armor,
            category: category,
            icon: icon ?? (block != null ? "block" : name),
            burnTime: burnTime, rarity: rarity, compostChance: compostChance
        );
        itemDefs.Add(def);
        itemByName[name] = id;
        if (block is ushort b && blockToItem[(int)b] == -1) blockToItem[(int)b] = id;
        return id;
    }

    public static int iid(string name)
    {
        if (!itemByName.TryGetValue(name, out int id)) throw new InvalidOperationException($"unknown item: {name}");
        return id;
    }
    public static int? iidOpt(string name) => itemByName.TryGetValue(name, out int id) ? id : (int?)null;
    public static bool itemExists(string name) => itemByName.ContainsKey(name);
    public static ItemDef itemDef(int id) => itemDefs[id];
    public static string itemName(int id) => id < itemDefs.Count ? itemDefs[id].name : "air";
    public static ItemStack stack(string name, int count = 1) => new ItemStack(iid(name), count);

    // MARK: - stack helpers

    public static bool stacksEqual(ItemStack a, ItemStack b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.id == b.id && a.damage == b.damage && EnchListEqual(a.ench, b.ench)
            && a.data == b.data && (a.label ?? "") == (b.label ?? "");
    }
    public static bool canMerge(ItemStack a, ItemStack b)
    {
        if (a == null || b == null) return false;
        if (itemDefs[a.id].maxStack <= 1) return false;
        return stacksEqual(a, b);
    }
    public static int maxStackOf(ItemStack s) => itemDefs[s.id].maxStack;
    public static int maxDamageOf(ItemStack s)
    {
        var d = itemDefs[s.id];
        return d.tool?.durability ?? d.armor?.durability ?? 0;
    }
    public static int enchLevel(ItemStack s, string ench)
    {
        if (s == null) return 0;
        foreach (var e in s.ench) if (e.id == ench) return e.lvl;
        return 0;
    }

    private static bool EnchListEqual(List<EnchInstance> a, List<EnchInstance> b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }
}

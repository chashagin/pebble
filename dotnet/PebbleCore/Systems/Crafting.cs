// Crafting — grid matching (shaped with
// mirroring, shapeless, tags), enchanting table / anvil / grindstone math.
//
// Ported from Sources/PebbleCore/Systems/Crafting.swift.
//
// Defines static class `CraftingGlobals` (module-level free funcs):
//   matchCrafting, consumeCraftingGrid, matchSmithing, enchantingOptions,
//   applyEnchanting, anvilCombine, grindstoneResult.
//   (ingMatches, selectEnchants, REPAIR_MATS are module-private — kept private inside.)
// Surface via: global using static PebbleCore.CraftingGlobals;
//
// Namespace-level public types: EnchantOption, AnvilResult.
//
// External symbols (defined elsewhere in the assembly, referenced by name):
//   itemDef, iid                                  (Items/ItemDefs → ItemGlobals)
//   tagMatches, craftingRecipes, smithingRecipes,
//   TRIM_MATERIALS, CraftRecipe, SmithRecipe      (Items/Recipes → RecipeGlobals)
//   ENCHANTMENTS, enchDef, compatible, appliesTo,
//   enchantability, EnchantmentDef                (Items/EnchantsPotions → EnchantsPotionsGlobals)
//   ItemStack, EnchInstance, TrimData, StackData  (Items/ItemDefs)
//   RandomX                                       (Core/RandomX)
//   detRound                                      (Gen/Terrain globals)

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

// ---------------------------------------------------------------------------
// Enchanting table
// ---------------------------------------------------------------------------
public struct EnchantOption
{
    public int level;          // XP level cost requirement
    public int lapis;          // 1-3
    public EnchInstance? preview;
    public List<EnchInstance> enchants;

    public EnchantOption(int level, int lapis, EnchInstance? preview, List<EnchInstance> enchants)
    {
        this.level = level;
        this.lapis = lapis;
        this.preview = preview;
        this.enchants = enchants;
    }
}

// ---------------------------------------------------------------------------
// Anvil
// ---------------------------------------------------------------------------
public struct AnvilResult
{
    public ItemStack @out;
    public int cost;

    public AnvilResult(ItemStack @out, int cost)
    {
        this.@out = @out;
        this.cost = cost;
    }
}

public static class CraftingGlobals
{
    private static bool ingMatches(string ing, ItemStack stack)
    {
        if (stack == null) return false;
        var name = itemDef(stack.id).name;
        if (ing.StartsWith("#")) return tagMatches(ing.Substring(1), name);
        return ing == name;
    }

    /// match a w×h grid of stacks against all recipes; returns output or nil
    public static (ItemStack @out, CraftRecipe recipe)? matchCrafting(List<ItemStack> grid, int gw, int gh)
    {
        // trim grid to bounding box
        int minX = gw, minY = gh, maxX = -1, maxY = -1;
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                if (grid[y * gw + x] != null)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        }
        if (maxX < 0) return null;
        int bw = maxX - minX + 1, bh = maxY - minY + 1;

        foreach (var r in craftingRecipes)
        {
            switch (r.tag)
            {
                case CraftRecipe.Tag.shapeless:
                {
                    var inputs = r.inputs;
                    var count = r.count;
                    bool ok = true;
                    var used = new bool[gw * gh];
                    foreach (var ing in inputs)
                    {
                        bool found = false;
                        for (int i = 0; i < grid.Count; i++)
                        {
                            if (used[i] || grid[i] == null) continue;
                            if (ingMatches(ing, grid[i])) { used[i] = true; found = true; break; }
                        }
                        if (!found) { ok = false; break; }
                    }
                    if (ok)
                    {
                        // no extra items
                        bool extra = false;
                        for (int i = 0; i < grid.Count; i++)
                            if (grid[i] != null && !used[i]) extra = true;
                        if (!extra) return (new ItemStack(iid(r.@out), count), r);
                    }
                    break;
                }
                case CraftRecipe.Tag.shaped:
                {
                    int rw = r.w, rh = r.h;
                    var rgrid = r.grid;
                    var count = r.count;
                    if (rw != bw || rh != bh) continue;
                    foreach (var mirror in new[] { false, true })
                    {
                        bool ok = true;
                        for (int y = 0; y < bh && ok; y++)
                        {
                            for (int x = 0; x < bw && ok; x++)
                            {
                                int rx = mirror ? bw - 1 - x : x;
                                var ing = rgrid[y * rw + rx];
                                var stack = grid[(minY + y) * gw + (minX + x)];
                                if (ing == null) { if (stack != null) ok = false; }
                                else if (!ingMatches(ing, stack)) ok = false;
                            }
                        }
                        if (ok) return (new ItemStack(iid(r.@out), count), r);
                    }
                    break;
                }
            }
        }
        return null;
    }

    /// consume one of each ingredient; returns container items (buckets/bottles)
    public static List<ItemStack> consumeCraftingGrid(List<ItemStack> grid)
    {
        var returns = new List<ItemStack>();
        for (int i = 0; i < grid.Count; i++)
        {
            var s = grid[i];
            if (s == null) continue;
            var name = itemDef(s.id).name;
            if (name == "milk_bucket" || name == "water_bucket" || name == "lava_bucket")
            {
                returns.Add(new ItemStack(iid("bucket"), 1));
            }
            if (name == "honey_bottle") { returns.Add(new ItemStack(iid("glass_bottle"), 1)); }
            s.count -= 1;
            if (s.count <= 0) grid[i] = null;
        }
        return returns;
    }

    /// smithing: template + base + addition
    public static ItemStack matchSmithing(ItemStack template, ItemStack @base, ItemStack addition)
    {
        if (template == null || @base == null || addition == null) return null;
        var tName = itemDef(template.id).name;
        var bName = itemDef(@base.id).name;
        var aName = itemDef(addition.id).name;
        foreach (var r in smithingRecipes)
        {
            if (r.template != tName) continue;
            if (r.output == "trim")
            {
                // any armor + trim material
                if (itemDef(@base.id).armor == null) continue;
                if (!TRIM_MATERIALS.Contains(aName)) continue;
                var outStack = @base.copy();
                outStack.data.trim = new TrimData(tName.Replace("_armor_trim", ""), aName);
                return outStack;
            }
            if (r.@base == bName && r.addition == aName)
            {
                var outStack = @base.copy();
                outStack.id = iid(r.output);
                return outStack;
            }
        }
        return null;
    }

    public static List<EnchantOption> enchantingOptions(ItemStack item, int bookshelves, int seed)
    {
        var outList = new List<EnchantOption>();
        if (item == null) return outList;
        var def = itemDef(item.id);
        bool isBook = def.name == "book";
        if (!isBook && !ENCHANTMENTS.Any(e => appliesTo(e, def))) return outList;
        if (item.ench.Count != 0) return outList; // already enchanted
        var rng = new RandomX(unchecked((uint)seed));
        int b = Math.Min(15, bookshelves);
        int @base = rng.nextInt(8) + 1 + (b >> 1) + rng.nextInt(b + 1);
        var levels = new[]
        {
            (int)Math.Max((double)@base / 3, 1),
            (int)((double)@base * 2 / 3 + 1),
            Math.Max(@base, b * 2),
        };
        for (int slot = 0; slot < 3; slot++)
        {
            int level = levels[slot];
            var slotRng = new RandomX(unchecked((uint)(seed + slot * 947)));
            var enchants = selectEnchants(item, level, ref slotRng);
            outList.Add(new EnchantOption(level, slot + 1,
                enchants.Count > 0 ? enchants[0] : (EnchInstance?)null, enchants));
        }
        return outList;
    }

    private static List<EnchInstance> selectEnchants(ItemStack item, int level, ref RandomX rng)
    {
        var def = itemDef(item.id);
        bool isBook = def.name == "book";
        int enchValue = enchantability(def);
        int modLevel = level + 1 + rng.nextInt((enchValue >> 2) + 1) + rng.nextInt((enchValue >> 2) + 1);
        double bonus = 1 + (rng.nextFloat() + rng.nextFloat() - 1) * 0.15;
        modLevel = Math.Max(1, (int)detRound((double)modLevel * bonus));
        var picked = new List<EnchInstance>();
        var candidates = new List<(EnchantmentDef e, int lvl)>();
        foreach (var e in ENCHANTMENTS)
        {
            if (e.treasure || e.curse) continue;
            if (!isBook && !appliesTo(e, def)) continue;
            int l = e.maxLevel;
            while (l >= 1)
            {
                if (modLevel >= e.minPower(l) && modLevel <= e.maxPower(l))
                {
                    candidates.Add((e, l));
                    break;
                }
                l -= 1;
            }
        }
        if (candidates.Count == 0) return picked;
        var first = rng.pickWeighted(candidates, c => (double)c.e.weight);
        picked.Add(new EnchInstance(first.e.id, first.lvl));
        int lvl2 = modLevel;
        while (rng.nextFloat() < (double)(lvl2 + 1) / 50)
        {
            lvl2 = (int)Math.Floor((double)lvl2 / 2);
            var remaining = candidates.Where(c =>
                picked.All(p => compatible(c.e, enchDef(p.id))) && !picked.Any(p => p.id == c.e.id)
            ).ToList();
            if (remaining.Count == 0) break;
            var next = rng.pickWeighted(remaining, c => (double)c.e.weight);
            picked.Add(new EnchInstance(next.e.id, next.lvl));
        }
        return picked;
    }

    public static ItemStack applyEnchanting(ItemStack item, EnchantOption option)
    {
        var def = itemDef(item.id);
        if (def.name == "book")
        {
            return new ItemStack(iid("enchanted_book"), 1, ench: option.enchants);
        }
        var result = item.copy();
        result.ench = option.enchants;
        return result;
    }

    private static readonly Dictionary<string, string> REPAIR_MATS = new Dictionary<string, string>
    {
        { "leather", "leather" }, { "chainmail", "iron_ingot" }, { "iron", "iron_ingot" }, { "golden", "gold_ingot" },
        { "diamond", "diamond" }, { "netherite", "netherite_ingot" }, { "turtle", "scute" }, { "elytra", "phantom_membrane" },
        { "wooden", "oak_planks" }, { "stone", "cobblestone" },
    };

    public static AnvilResult? anvilCombine(ItemStack left, ItemStack right, string rename)
    {
        if (left == null) return null;
        var @out = left.copy();
        double cost = 0.0;
        int prior = (left.data.priorWork ?? 0) + (right?.data.priorWork ?? 0);
        cost += Math.Pow(2, (double)(left.data.priorWork ?? 0)) - 1;
        if (right != null) cost += Math.Pow(2, (double)(right.data.priorWork ?? 0)) - 1;

        if (right != null)
        {
            var ldef = itemDef(left.id);
            var rdef = itemDef(right.id);
            var rName = rdef.name;
            string material = ldef.tool != null
                ? (REPAIR_MATS.TryGetValue(ldef.name.Split('_')[0], out var m0) ? m0 : null)
                : ldef.armor != null
                    ? (REPAIR_MATS.TryGetValue(ldef.armor.Value.material, out var m1) ? m1 : null)
                    : null;
            if (rName == "enchanted_book" && right.ench.Count != 0)
            {
                // book apply
                var newEnch = @out.ench;
                bool applied = false;
                foreach (var be in right.ench)
                {
                    var e = enchDef(be.id);
                    if (itemDef(left.id).name != "enchanted_book" && !appliesTo(e, ldef)) continue;
                    bool conflict = newEnch.Any(x => x.id != be.id && !compatible(e, enchDef(x.id)));
                    if (conflict) { cost += 1; continue; }
                    int idx = newEnch.FindIndex(x => x.id == be.id);
                    if (idx >= 0)
                    {
                        var cur = newEnch[idx];
                        cur.lvl = cur.lvl == be.lvl ? Math.Min(e.maxLevel, cur.lvl + 1) : Math.Max(cur.lvl, be.lvl);
                        newEnch[idx] = cur;
                    }
                    else
                    {
                        newEnch.Add(be);
                    }
                    cost += (double)be.lvl * (e.weight >= 10 ? 1 : e.weight >= 5 ? 2 : e.weight >= 2 ? 4 : 8) / 2;
                    applied = true;
                }
                if (!applied) return null;
                @out.ench = newEnch;
            }
            else if (material != null && rName == material)
            {
                // unit repair: each mat repairs 25%
                int maxD = ldef.tool?.durability ?? ldef.armor?.durability ?? 0;
                if (maxD == 0 || left.damage == 0) return null;
                int quarter = (int)Math.Ceiling((double)maxD / 4);
                int units = Math.Min(right.count, (int)Math.Ceiling((double)left.damage / ((double)maxD / 4)));
                @out.damage = Math.Max(0, left.damage - units * quarter);
                cost += (double)units;
                @out.data.repairUnits = units;
            }
            else if (right.id == left.id)
            {
                // combine same items
                int maxD = ldef.tool?.durability ?? ldef.armor?.durability ?? 0;
                if (maxD != 0)
                {
                    int totalLife = (maxD - left.damage) + (maxD - right.damage) + (int)Math.Floor((double)maxD * 0.12);
                    @out.damage = Math.Max(0, maxD - totalLife);
                    cost += 2;
                }
                // merge enchants
                if (right.ench.Count != 0)
                {
                    var newEnch = @out.ench;
                    foreach (var be in right.ench)
                    {
                        var e = enchDef(be.id);
                        bool conflict = newEnch.Any(x => x.id != be.id && !compatible(e, enchDef(x.id)));
                        if (conflict) { cost += 1; continue; }
                        int idx = newEnch.FindIndex(x => x.id == be.id);
                        if (idx >= 0)
                        {
                            var cur = newEnch[idx];
                            cur.lvl = cur.lvl == be.lvl ? Math.Min(e.maxLevel, cur.lvl + 1) : Math.Max(cur.lvl, be.lvl);
                            newEnch[idx] = cur;
                        }
                        else { newEnch.Add(be); }
                        cost += (double)be.lvl;
                    }
                    @out.ench = newEnch;
                }
            }
            else
            {
                return null;
            }
        }
        if (rename != null && rename != (left.label ?? ""))
        {
            @out.label = rename.Length == 0 ? null : rename;
            cost += 1;
        }
        if (cost <= 0) return null;
        @out.data.priorWork = prior + 1;
        return new AnvilResult(@out, Math.Min(39, (int)Math.Ceiling(cost)));
    }

    /// grindstone: strip enchants, repair by combining, return XP value
    public static (ItemStack @out, int xp)? grindstoneResult(ItemStack a, ItemStack b)
    {
        var item = a ?? b;
        if (item == null) return null;
        if (a != null && b != null && a.id != b.id) return null;
        var def = itemDef(item.id);
        var @out = item.copy();
        int xp = 0;
        if (@out.ench.Count != 0)
        {
            foreach (var e in @out.ench)
            {
                if (!enchDef(e.id).curse) xp += enchDef(e.id).minPower(e.lvl);
            }
            @out.ench = @out.ench.Where(x => enchDef(x.id).curse).ToList();
        }
        if (a != null && b != null)
        {
            int maxD = def.tool?.durability ?? def.armor?.durability ?? 0;
            if (maxD != 0)
            {
                int totalLife = (maxD - a.damage) + (maxD - b.damage) + (int)Math.Floor((double)maxD * 0.05);
                @out.damage = Math.Max(0, maxD - totalLife);
            }
        }
        if (def.name == "enchanted_book" && @out.ench.Count == 0)
        {
            @out.id = iid("book");
        }
        @out.data = new StackData();
        return (@out, Math.Min(50, (int)Math.Ceiling((double)xp / 2)));
    }
}

// Every crafting, smelting, stonecutting and smithing recipe — Ingredients are item names; '#name' references a tag.
// Registration order matters for golden parity.
//
// Ported from Sources/PebbleCore/Items/Recipes.swift.
//
// Defines static class `RecipeGlobals` (module-level state + free funcs):
//   craftingRecipes, smeltingRecipes, stonecuttingRecipes, smithingRecipes, TAGS,
//   TRIM_MATERIALS, tagMatches, registerAllRecipes().
// Surface via: global using static PebbleCore.RecipeGlobals;
//
// External symbols (defined elsewhere in the assembly, referenced by name):
//   itemExists                          (Items/ItemDefs → ItemGlobals)
//   WOODS, COLORS                       (World/BlockRegistry → BlockData)
//   TRIM_PATTERNS                       (Items/ItemRegistry → ItemRegistryGlobals)

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

// Swift enum with associated values → class with private tag enum + payload + static factories.
public sealed class CraftRecipe
{
    public enum Tag { shaped, shapeless }
    public readonly Tag tag;

    // shaped payload
    public readonly int w;
    public readonly int h;
    public readonly List<string> grid; // [String?] — null entries are empty cells
    // shapeless payload
    public readonly List<string> inputs;
    // shared
    public readonly string @out;
    public readonly int count;

    private CraftRecipe(Tag tag, int w, int h, List<string> grid, List<string> inputs, string @out, int count)
    {
        this.tag = tag;
        this.w = w;
        this.h = h;
        this.grid = grid;
        this.inputs = inputs;
        this.@out = @out;
        this.count = count;
    }

    public static CraftRecipe shaped(int w, int h, List<string> grid, string @out, int count)
        => new CraftRecipe(Tag.shaped, w, h, grid, null, @out, count);

    public static CraftRecipe shapeless(List<string> inputs, string @out, int count)
        => new CraftRecipe(Tag.shapeless, 0, 0, null, inputs, @out, count);
}

public struct SmeltRecipe
{
    public readonly string input;
    public readonly string output;
    public readonly double xp;
    public readonly string kind; // any | blast | smoke
    public SmeltRecipe(string input, string output, double xp, string kind)
    {
        this.input = input;
        this.output = output;
        this.xp = xp;
        this.kind = kind;
    }
}

public struct StonecutRecipe
{
    public readonly string input;
    public readonly string output;
    public readonly int count;
    public StonecutRecipe(string input, string output, int count)
    {
        this.input = input;
        this.output = output;
        this.count = count;
    }
}

public struct SmithRecipe
{
    public readonly string template;
    public readonly string @base;
    public readonly string addition;
    public readonly string output; // item name or 'trim'
    public SmithRecipe(string template, string @base, string addition, string output)
    {
        this.template = template;
        this.@base = @base;
        this.addition = addition;
        this.output = output;
    }
}

public static class RecipeGlobals
{
    public static List<CraftRecipe> craftingRecipes = new List<CraftRecipe>();
    public static List<SmeltRecipe> smeltingRecipes = new List<SmeltRecipe>();
    public static List<StonecutRecipe> stonecuttingRecipes = new List<StonecutRecipe>();
    public static List<SmithRecipe> smithingRecipes = new List<SmithRecipe>();
    public static Dictionary<string, List<string>> TAGS = new Dictionary<string, List<string>>();

    public static bool tagMatches(string tag, string itemNm)
        => TAGS.TryGetValue(tag, out var list) ? list.Contains(itemNm) : false;

    public static readonly string[] TRIM_MATERIALS = { "iron_ingot", "copper_ingot", "gold_ingot", "lapis_lazuli", "emerald", "diamond", "netherite_ingot", "redstone", "amethyst_shard", "quartz" };

    private static void shaped(string pattern, Dictionary<char, string> key, string @out, int count = 1)
    {
        var rows = pattern.Split('/'); // omittingEmptySubsequences: false → keep empties (C# default)
        int h = rows.Length;
        int w = rows.Length == 0 ? 0 : rows.Max(r => r.Length);
        var grid = new List<string>();
        for (int y = 0; y < h; y++)
        {
            var chars = rows[y];
            for (int x = 0; x < w; x++)
            {
                char ch = x < chars.Length ? chars[x] : ' ';
                if (ch == ' ') { grid.Add(null); continue; }
                if (!key.TryGetValue(ch, out var ing))
                    throw new InvalidOperationException($"recipe {@out}: no key for '{ch}'");
                grid.Add(ing);
            }
        }
        craftingRecipes.Add(CraftRecipe.shaped(w: w, h: h, grid: grid, @out: @out, count: count));
    }

    private static void shapeless(List<string> inputs, string @out, int count = 1)
    {
        craftingRecipes.Add(CraftRecipe.shapeless(inputs: inputs, @out: @out, count: count));
    }

    private static void smelt(string input, string output, double xp, string kind = "any")
    {
        smeltingRecipes.Add(new SmeltRecipe(input: input, output: output, xp: xp, kind: kind));
    }

    private static void cut(string input, string output, int count = 1)
    {
        stonecuttingRecipes.Add(new StonecutRecipe(input: input, output: output, count: count));
    }

    private static void stoneSet(string fam, string @base, bool stairs = true, bool wall = true)
    {
        if (stairs && itemExists($"{fam}_stairs"))
        {
            shaped("X  /XX /XXX", new Dictionary<char, string> { ['X'] = @base }, $"{fam}_stairs", 4);
            cut(@base, $"{fam}_stairs");
        }
        if (itemExists($"{fam}_slab"))
        {
            shaped("XXX", new Dictionary<char, string> { ['X'] = @base }, $"{fam}_slab", 6);
            cut(@base, $"{fam}_slab", 2);
        }
        if (wall && itemExists($"{fam}_wall"))
        {
            shaped("XXX/XXX", new Dictionary<char, string> { ['X'] = @base }, $"{fam}_wall", 6);
            cut(@base, $"{fam}_wall");
        }
    }

    private static bool recipesRegistered = false;
    public static void registerAllRecipes()
    {
        if (recipesRegistered) return;
        recipesRegistered = true;

        // --- tags ---
        TAGS = new Dictionary<string, List<string>>
        {
            ["planks"] = WOODS.Select(w => $"{w}_planks").ToList(),
            ["logs"] = new List<string>(),
            ["wool"] = COLORS.Select(c => $"{c}_wool").ToList(),
            ["wooden_slabs"] = WOODS.Select(w => $"{w}_slab").ToList(),
            ["stone_crafting"] = new List<string> { "cobblestone", "cobbled_deepslate", "blackstone" },
            ["coals"] = new List<string> { "coal", "charcoal" },
            ["soul_fire_base"] = new List<string> { "soul_sand", "soul_soil" },
            ["fishes"] = new List<string> { "cod", "salmon", "tropical_fish", "pufferfish" },
        };
        foreach (var w in WOODS)
        {
            if (w == "bamboo")
            {
                TAGS["bamboo_logs"] = new List<string> { "bamboo_block", "stripped_bamboo_block" };
                continue;
            }
            var log = (w == "crimson" || w == "warped") ? $"{w}_stem" : $"{w}_log";
            var woodB = (w == "crimson" || w == "warped") ? $"{w}_hyphae" : $"{w}_wood";
            TAGS[$"{w}_logs"] = new List<string> { log, woodB, $"stripped_{log}", $"stripped_{woodB}" };
            TAGS["logs"].AddRange(TAGS[$"{w}_logs"]);
        }
        TAGS["logs_that_burn"] = TAGS["logs"].Where(s => !s.Contains("crimson") && !s.Contains("warped")).ToList();

        // --- wood ---
        foreach (var w in WOODS)
        {
            var planks = $"{w}_planks";
            if (w == "bamboo")
            {
                shaped("BBB/BBB/BBB", new Dictionary<char, string> { ['B'] = "bamboo" }, "bamboo_block");
                shapeless(new List<string> { "#bamboo_logs" }, "bamboo_planks", 2);
                shaped("S/S", new Dictionary<char, string> { ['S'] = "bamboo_slab" }, "bamboo_mosaic");
                shaped("X  /XX /XXX", new Dictionary<char, string> { ['X'] = "bamboo_mosaic" }, "bamboo_mosaic_stairs", 4);
                shaped("XXX", new Dictionary<char, string> { ['X'] = "bamboo_mosaic" }, "bamboo_mosaic_slab", 6);
                shaped("P P/PPP", new Dictionary<char, string> { ['P'] = planks }, "bamboo_raft");
                shapeless(new List<string> { "bamboo_raft", "chest" }, "bamboo_chest_raft");
            }
            else
            {
                shapeless(new List<string> { $"#{w}_logs" }, planks, 4);
                var log = (w == "crimson" || w == "warped") ? $"{w}_stem" : $"{w}_log";
                var woodB = (w == "crimson" || w == "warped") ? $"{w}_hyphae" : $"{w}_wood";
                shaped("LL/LL", new Dictionary<char, string> { ['L'] = log }, woodB, 3);
                shaped("LL/LL", new Dictionary<char, string> { ['L'] = $"stripped_{log}" }, $"stripped_{woodB}", 3);
                if (w != "crimson" && w != "warped")
                {
                    shaped("P P/PPP", new Dictionary<char, string> { ['P'] = planks }, $"{w}_boat");
                    shapeless(new List<string> { $"{w}_boat", "chest" }, $"{w}_chest_boat");
                }
            }
            shaped("X  /XX /XXX", new Dictionary<char, string> { ['X'] = planks }, $"{w}_stairs", 4);
            shaped("XXX", new Dictionary<char, string> { ['X'] = planks }, $"{w}_slab", 6);
            shaped("PSP/PSP", new Dictionary<char, string> { ['P'] = planks, ['S'] = "stick" }, $"{w}_fence", 3);
            shaped("SPS/SPS", new Dictionary<char, string> { ['P'] = planks, ['S'] = "stick" }, $"{w}_fence_gate");
            shaped("PP/PP/PP", new Dictionary<char, string> { ['P'] = planks }, $"{w}_door", 3);
            shaped("PPP/PPP", new Dictionary<char, string> { ['P'] = planks }, $"{w}_trapdoor", 2);
            shapeless(new List<string> { planks }, $"{w}_button");
            shaped("PP", new Dictionary<char, string> { ['P'] = planks }, $"{w}_pressure_plate");
            shaped("PPP/PPP/ S ", new Dictionary<char, string> { ['P'] = planks, ['S'] = "stick" }, $"{w}_sign", 3);
            var stripped = w == "bamboo" ? "stripped_bamboo_block" : ((w == "crimson" || w == "warped") ? $"stripped_{w}_stem" : $"stripped_{w}_log");
            shaped("C C/SSS/SSS", new Dictionary<char, string> { ['C'] = "chain", ['S'] = stripped }, $"{w}_hanging_sign", 6);
        }
        shaped("PP/PP", new Dictionary<char, string> { ['P'] = "#planks" }, "crafting_table");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "#planks" }, "stick", 4);
        shaped("P P/P P/PPP", new Dictionary<char, string> { ['P'] = "#planks" }, "ladder", 3);
        shaped("PPP/P P/PPP", new Dictionary<char, string> { ['P'] = "#planks" }, "chest");
        shaped("PSP/P P/PSP", new Dictionary<char, string> { ['P'] = "#planks", ['S'] = "#wooden_slabs" }, "barrel");
        shaped("P P/PPP", new Dictionary<char, string> { ['P'] = "#planks" }, "bowl", 4);
        shapeless(new List<string> { "chest", "tripwire_hook" }, "trapped_chest");
        shaped("PPP/BBB/PPP", new Dictionary<char, string> { ['P'] = "#planks", ['B'] = "book" }, "bookshelf");
        shaped("PPP/SSS/PPP", new Dictionary<char, string> { ['P'] = "#planks", ['S'] = "#wooden_slabs" }, "chiseled_bookshelf");
        shaped("SSS/ B /SSS", new Dictionary<char, string> { ['S'] = "#wooden_slabs", ['B'] = "bookshelf" }, "lectern");

        // --- stone & building ---
        stoneSet("cobblestone", "cobblestone");
        stoneSet("mossy_cobblestone", "mossy_cobblestone");
        stoneSet("stone", "stone");
        stoneSet("smooth_stone", "smooth_stone", stairs: false, wall: false);
        stoneSet("stone_brick", "stone_bricks");
        stoneSet("mossy_stone_brick", "mossy_stone_bricks");
        stoneSet("granite", "granite");
        stoneSet("polished_granite", "polished_granite", wall: false);
        stoneSet("diorite", "diorite");
        stoneSet("polished_diorite", "polished_diorite", wall: false);
        stoneSet("andesite", "andesite");
        stoneSet("polished_andesite", "polished_andesite", wall: false);
        stoneSet("cobbled_deepslate", "cobbled_deepslate");
        stoneSet("polished_deepslate", "polished_deepslate");
        stoneSet("deepslate_brick", "deepslate_bricks");
        stoneSet("deepslate_tile", "deepslate_tiles");
        stoneSet("brick", "bricks");
        stoneSet("mud_brick", "mud_bricks");
        stoneSet("sandstone", "sandstone");
        stoneSet("smooth_sandstone", "smooth_sandstone", wall: false);
        stoneSet("cut_sandstone", "cut_sandstone", stairs: false, wall: false);
        stoneSet("red_sandstone", "red_sandstone");
        stoneSet("smooth_red_sandstone", "smooth_red_sandstone", wall: false);
        stoneSet("cut_red_sandstone", "cut_red_sandstone", stairs: false, wall: false);
        stoneSet("prismarine", "prismarine");
        stoneSet("prismarine_brick", "prismarine_bricks", wall: false);
        stoneSet("dark_prismarine", "dark_prismarine", wall: false);
        stoneSet("nether_brick", "nether_bricks");
        stoneSet("red_nether_brick", "red_nether_bricks");
        stoneSet("blackstone", "blackstone");
        stoneSet("polished_blackstone", "polished_blackstone");
        stoneSet("polished_blackstone_brick", "polished_blackstone_bricks");
        stoneSet("end_stone_brick", "end_stone_bricks");
        stoneSet("purpur", "purpur_block", wall: false);
        stoneSet("quartz", "quartz_block", wall: false);
        stoneSet("smooth_quartz", "smooth_quartz", wall: false);
        stoneSet("tuff", "tuff");

        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "stone" }, "stone_bricks", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "cobbled_deepslate" }, "polished_deepslate", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "polished_deepslate" }, "deepslate_bricks", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "deepslate_bricks" }, "deepslate_tiles", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "blackstone" }, "polished_blackstone", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "polished_blackstone" }, "polished_blackstone_bricks", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "granite" }, "polished_granite", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "diorite" }, "polished_diorite", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "andesite" }, "polished_andesite", 4);
        shapeless(new List<string> { "diorite", "cobblestone" }, "andesite", 2);
        shapeless(new List<string> { "diorite", "quartz" }, "granite");
        shapeless(new List<string> { "cobblestone", "quartz" }, "diorite", 2);
        shapeless(new List<string> { "cobblestone", "vine" }, "mossy_cobblestone");
        shapeless(new List<string> { "cobblestone", "moss_block" }, "mossy_cobblestone");
        shapeless(new List<string> { "stone_bricks", "vine" }, "mossy_stone_bricks");
        shapeless(new List<string> { "stone_bricks", "moss_block" }, "mossy_stone_bricks");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "stone_brick_slab" }, "chiseled_stone_bricks");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "sandstone_slab" }, "chiseled_sandstone");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "red_sandstone_slab" }, "chiseled_red_sandstone");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "cobbled_deepslate_slab" }, "chiseled_deepslate");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "nether_brick_slab" }, "chiseled_nether_bricks");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "polished_blackstone_slab" }, "chiseled_polished_blackstone");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "quartz_slab" }, "chiseled_quartz_block");
        shaped("S/S", new Dictionary<char, string> { ['S'] = "purpur_slab" }, "purpur_pillar");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "sand" }, "sandstone");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "red_sand" }, "red_sandstone");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "sandstone" }, "cut_sandstone", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "red_sandstone" }, "cut_red_sandstone", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "brick" }, "bricks");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "nether_brick" }, "nether_bricks");
        shaped("NW/WN", new Dictionary<char, string> { ['N'] = "nether_brick", ['W'] = "nether_wart" }, "red_nether_bricks");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "packed_mud" }, "mud_bricks", 4);
        shapeless(new List<string> { "mud", "wheat" }, "packed_mud");
        shapeless(new List<string> { "mud", "mangrove_roots" }, "muddy_mangrove_roots");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "quartz" }, "quartz_block");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "quartz_block" }, "quartz_bricks", 4);
        shaped("X/X", new Dictionary<char, string> { ['X'] = "quartz_block" }, "quartz_pillar", 2);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "end_stone" }, "end_stone_bricks", 4);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "popped_chorus_fruit" }, "purpur_block", 4);
        shaped("X/X", new Dictionary<char, string> { ['X'] = "basalt" }, "polished_basalt", 2);
        shaped("DG/GD", new Dictionary<char, string> { ['D'] = "dirt", ['G'] = "gravel" }, "coarse_dirt", 4);
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "melon_slice" }, "melon");
        shapeless(new List<string> { "pumpkin" }, "pumpkin_seeds", 4);
        shapeless(new List<string> { "melon_slice" }, "melon_seeds");
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "ice" }, "packed_ice");
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "packed_ice" }, "blue_ice");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "snowball" }, "snow_block");
        shaped("XXX", new Dictionary<char, string> { ['X'] = "snow_block" }, "snow", 6);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "clay_ball" }, "clay");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "glowstone_dust" }, "glowstone");
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "wheat" }, "hay_block");
        shapeless(new List<string> { "hay_block" }, "wheat", 9);
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "bone_meal" }, "bone_block");
        shapeless(new List<string> { "bone_block" }, "bone_meal", 9);
        shapeless(new List<string> { "bone" }, "bone_meal", 3);
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "dried_kelp" }, "dried_kelp_block");
        shapeless(new List<string> { "dried_kelp_block" }, "dried_kelp", 9);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "string" }, "white_wool");
        shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = "slime_ball" }, "slime_block");
        shapeless(new List<string> { "slime_block" }, "slime_ball", 9);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "honeycomb" }, "honeycomb_block");
        shaped("BB/BB", new Dictionary<char, string> { ['B'] = "honey_bottle" }, "honey_block");
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "amethyst_shard" }, "amethyst_block");
        shaped(" S /SGS/ S ", new Dictionary<char, string> { ['S'] = "amethyst_shard", ['G'] = "glass" }, "tinted_glass", 2);
        shaped("XX/XX", new Dictionary<char, string> { ['X'] = "pointed_dripstone" }, "dripstone_block");

        // metals
        foreach (var (ingot, blockNm, nugget) in new (string, string, string)[] { ("iron_ingot", "iron_block", "iron_nugget"), ("gold_ingot", "gold_block", "gold_nugget") })
        {
            shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = ingot }, blockNm);
            shapeless(new List<string> { blockNm }, ingot, 9);
            shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = nugget }, ingot);
            shapeless(new List<string> { ingot }, nugget, 9);
        }
        foreach (var (item, blockNm) in new (string, string)[] {
            ("diamond", "diamond_block"), ("emerald", "emerald_block"), ("coal", "coal_block"),
            ("lapis_lazuli", "lapis_block"), ("redstone", "redstone_block"),
            ("copper_ingot", "copper_block"), ("netherite_ingot", "netherite_block"),
            ("raw_iron", "raw_iron_block"), ("raw_copper", "raw_copper_block"), ("raw_gold", "raw_gold_block") })
        {
            shaped("XXX/XXX/XXX", new Dictionary<char, string> { ['X'] = item }, blockNm);
            shapeless(new List<string> { blockNm }, item, 9);
        }
        shapeless(new List<string> { "gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot", "netherite_scrap", "netherite_scrap", "netherite_scrap", "netherite_scrap" }, "netherite_ingot");

        // copper family
        for (int stage = 0; stage < 4; stage++)
        {
            var p = new[] { "", "exposed_", "weathered_", "oxidized_" }[stage];
            shaped("XX/XX", new Dictionary<char, string> { ['X'] = $"{p}copper_block" }, $"{p}cut_copper", 4);
            cut($"{p}copper_block", $"{p}cut_copper", 4);
            shaped("X  /XX /XXX", new Dictionary<char, string> { ['X'] = $"{p}cut_copper" }, $"{p}cut_copper_stairs", 4);
            cut($"{p}copper_block", $"{p}cut_copper_stairs", 4);
            cut($"{p}cut_copper", $"{p}cut_copper_stairs");
            shaped("XXX", new Dictionary<char, string> { ['X'] = $"{p}cut_copper" }, $"{p}cut_copper_slab", 6);
            cut($"{p}copper_block", $"{p}cut_copper_slab", 8);
            cut($"{p}cut_copper", $"{p}cut_copper_slab", 2);
            foreach (var @base in new[] { $"{p}copper_block", $"{p}cut_copper", $"{p}cut_copper_stairs", $"{p}cut_copper_slab" })
            {
                shapeless(new List<string> { @base, "honeycomb" }, $"waxed_{@base}");
            }
        }
        shaped("C/C/C", new Dictionary<char, string> { ['C'] = "copper_ingot" }, "lightning_rod");

        // --- tools / combat ---
        foreach (var (mat, ing) in new (string, string)[] {
            ("wooden", "#planks"), ("stone", "#stone_crafting"), ("iron", "iron_ingot"),
            ("golden", "gold_ingot"), ("diamond", "diamond") })
        {
            shaped("X/X/S", new Dictionary<char, string> { ['X'] = ing, ['S'] = "stick" }, $"{mat}_sword");
            shaped("XXX/ S / S ", new Dictionary<char, string> { ['X'] = ing, ['S'] = "stick" }, $"{mat}_pickaxe");
            shaped("XX/XS/ S", new Dictionary<char, string> { ['X'] = ing, ['S'] = "stick" }, $"{mat}_axe");
            shaped("X/S/S", new Dictionary<char, string> { ['X'] = ing, ['S'] = "stick" }, $"{mat}_shovel");
            shaped("XX/ S/ S", new Dictionary<char, string> { ['X'] = ing, ['S'] = "stick" }, $"{mat}_hoe");
        }
        foreach (var (mat, ing) in new (string, string)[] {
            ("leather", "leather"), ("iron", "iron_ingot"), ("golden", "gold_ingot"), ("diamond", "diamond") })
        {
            shaped("XXX/X X", new Dictionary<char, string> { ['X'] = ing }, $"{mat}_helmet");
            shaped("X X/XXX/XXX", new Dictionary<char, string> { ['X'] = ing }, $"{mat}_chestplate");
            shaped("XXX/X X/X X", new Dictionary<char, string> { ['X'] = ing }, $"{mat}_leggings");
            shaped("X X/X X", new Dictionary<char, string> { ['X'] = ing }, $"{mat}_boots");
        }
        shaped("XXX/X X", new Dictionary<char, string> { ['X'] = "scute" }, "turtle_helmet");
        shaped("WIW/WWW/ W ", new Dictionary<char, string> { ['W'] = "#planks", ['I'] = "iron_ingot" }, "shield");
        shaped("SIS/XTX/ S ", new Dictionary<char, string> { ['S'] = "stick", ['I'] = "iron_ingot", ['T'] = "tripwire_hook", ['X'] = "string" }, "crossbow");
        shaped(" SX/S X/ SX", new Dictionary<char, string> { ['S'] = "stick", ['X'] = "string" }, "bow");
        shaped("F/S/E", new Dictionary<char, string> { ['F'] = "flint", ['S'] = "stick", ['E'] = "feather" }, "arrow", 4);
        shapeless(new List<string> { "arrow", "glowstone_dust", "glowstone_dust", "glowstone_dust", "glowstone_dust" }, "spectral_arrow", 2);
        shaped("  S/ SX/S X", new Dictionary<char, string> { ['S'] = "stick", ['X'] = "string" }, "fishing_rod");
        shapeless(new List<string> { "iron_ingot", "flint" }, "flint_and_steel");
        shaped("SS /SE /  S", new Dictionary<char, string> { ['S'] = "string", ['E'] = "slime_ball" }, "lead", 2);
        shaped("F/C/S", new Dictionary<char, string> { ['F'] = "feather", ['C'] = "copper_ingot", ['S'] = "stick" }, "brush");
        shaped("A/C/C", new Dictionary<char, string> { ['A'] = "amethyst_shard", ['C'] = "copper_ingot" }, "spyglass");
        shapeless(new List<string> { "fishing_rod", "carrot" }, "carrot_on_a_stick");
        shapeless(new List<string> { "fishing_rod", "warped_fungus" }, "warped_fungus_on_a_stick");

        // --- food ---
        shaped("WWW", new Dictionary<char, string> { ['W'] = "wheat" }, "bread");
        shaped("WCW", new Dictionary<char, string> { ['W'] = "wheat", ['C'] = "cocoa_beans" }, "cookie", 8);
        shaped("MMM/SES/WWW", new Dictionary<char, string> { ['M'] = "milk_bucket", ['S'] = "sugar", ['E'] = "egg", ['W'] = "wheat" }, "cake");
        shapeless(new List<string> { "pumpkin", "sugar", "egg" }, "pumpkin_pie");
        shapeless(new List<string> { "brown_mushroom", "red_mushroom", "bowl" }, "mushroom_stew");
        shapeless(new List<string> { "beetroot", "beetroot", "beetroot", "beetroot", "beetroot", "beetroot", "bowl" }, "beetroot_soup");
        shapeless(new List<string> { "cooked_rabbit", "carrot", "baked_potato", "brown_mushroom", "bowl" }, "rabbit_stew");
        shapeless(new List<string> { "brown_mushroom", "red_mushroom", "bowl", "dandelion" }, "suspicious_stew");
        shaped("GGG/GAG/GGG", new Dictionary<char, string> { ['G'] = "gold_ingot", ['A'] = "apple" }, "golden_apple");
        shaped("GGG/GCG/GGG", new Dictionary<char, string> { ['G'] = "gold_nugget", ['C'] = "carrot" }, "golden_carrot");
        shaped("GGG/GMG/GGG", new Dictionary<char, string> { ['G'] = "gold_nugget", ['M'] = "melon_slice" }, "glistering_melon_slice");
        shapeless(new List<string> { "sugar_cane" }, "sugar");
        shapeless(new List<string> { "honey_bottle" }, "sugar", 3);

        // --- redstone ---
        shaped("C/S", new Dictionary<char, string> { ['C'] = "#coals", ['S'] = "stick" }, "torch", 4);
        shaped("C/S/B", new Dictionary<char, string> { ['C'] = "#coals", ['S'] = "stick", ['B'] = "#soul_fire_base" }, "soul_torch", 4);
        shaped("R/S", new Dictionary<char, string> { ['R'] = "redstone", ['S'] = "stick" }, "redstone_torch");
        shaped("S/C", new Dictionary<char, string> { ['S'] = "stick", ['C'] = "cobblestone" }, "lever");
        shaped("TRT/SSS", new Dictionary<char, string> { ['T'] = "redstone_torch", ['R'] = "redstone", ['S'] = "stone" }, "repeater");
        shaped(" T /TQT/SSS", new Dictionary<char, string> { ['T'] = "redstone_torch", ['Q'] = "quartz", ['S'] = "stone" }, "comparator");
        shapeless(new List<string> { "stone" }, "stone_button");
        shapeless(new List<string> { "polished_blackstone" }, "polished_blackstone_button");
        shaped("SS", new Dictionary<char, string> { ['S'] = "stone" }, "stone_pressure_plate");
        shaped("SS", new Dictionary<char, string> { ['S'] = "polished_blackstone" }, "polished_blackstone_pressure_plate");
        shaped("GG", new Dictionary<char, string> { ['G'] = "gold_ingot" }, "light_weighted_pressure_plate");
        shaped("II", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "heavy_weighted_pressure_plate");
        shaped("WWW/CIC/CRC", new Dictionary<char, string> { ['W'] = "#planks", ['C'] = "cobblestone", ['I'] = "iron_ingot", ['R'] = "redstone" }, "piston");
        shapeless(new List<string> { "piston", "slime_ball" }, "sticky_piston");
        shaped("CCC/RRQ/CCC", new Dictionary<char, string> { ['C'] = "cobblestone", ['R'] = "redstone", ['Q'] = "quartz" }, "observer");
        shaped("CCC/CBC/CRC", new Dictionary<char, string> { ['C'] = "cobblestone", ['B'] = "bow", ['R'] = "redstone" }, "dispenser");
        shaped("CCC/C C/CRC", new Dictionary<char, string> { ['C'] = "cobblestone", ['R'] = "redstone" }, "dropper");
        shaped("I I/ICI/ I ", new Dictionary<char, string> { ['I'] = "iron_ingot", ['C'] = "chest" }, "hopper");
        shaped(" G /GRG/ G ", new Dictionary<char, string> { ['G'] = "glowstone", ['R'] = "redstone" }, "redstone_lamp");
        shaped("GGG/QQQ/SSS", new Dictionary<char, string> { ['G'] = "glass", ['Q'] = "quartz", ['S'] = "#wooden_slabs" }, "daylight_detector");
        shaped(" R /RHR/ R ", new Dictionary<char, string> { ['R'] = "redstone", ['H'] = "hay_block" }, "target");
        shaped("GSG/SGS/GSG", new Dictionary<char, string> { ['G'] = "gunpowder", ['S'] = "sand" }, "tnt");
        shaped("I/S", new Dictionary<char, string> { ['I'] = "iron_ingot", ['S'] = "stick" }, "tripwire_hook", 2);
        shaped("PPP/PRP/PPP", new Dictionary<char, string> { ['P'] = "#planks", ['R'] = "redstone" }, "note_block");
        shaped("PPP/PDP/PPP", new Dictionary<char, string> { ['P'] = "#planks", ['D'] = "diamond" }, "jukebox");
        shaped(" A /ASA", new Dictionary<char, string> { ['A'] = "amethyst_shard", ['S'] = "sculk_sensor" }, "calibrated_sculk_sensor");
        shaped("I I/ISI/I I", new Dictionary<char, string> { ['I'] = "iron_ingot", ['S'] = "stick" }, "rail", 16);
        shaped("G G/GSG/GRG", new Dictionary<char, string> { ['G'] = "gold_ingot", ['S'] = "stick", ['R'] = "redstone" }, "powered_rail", 6);
        shaped("I I/IPI/IRI", new Dictionary<char, string> { ['I'] = "iron_ingot", ['P'] = "stone_pressure_plate", ['R'] = "redstone" }, "detector_rail", 6);
        shaped("ISI/IRI/ISI", new Dictionary<char, string> { ['I'] = "iron_ingot", ['S'] = "stick", ['R'] = "redstone_torch" }, "activator_rail", 6);
        shaped("I I/III", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "minecart");
        shapeless(new List<string> { "minecart", "chest" }, "chest_minecart");
        shapeless(new List<string> { "minecart", "furnace" }, "furnace_minecart");
        shapeless(new List<string> { "minecart", "hopper" }, "hopper_minecart");
        shapeless(new List<string> { "minecart", "tnt" }, "tnt_minecart");

        // --- functional ---
        shaped("CCC/C C/CCC", new Dictionary<char, string> { ['C'] = "cobblestone" }, "furnace");
        shaped("III/IFI/SSS", new Dictionary<char, string> { ['I'] = "iron_ingot", ['F'] = "furnace", ['S'] = "smooth_stone" }, "blast_furnace");
        shaped(" L /LFL/ L ", new Dictionary<char, string> { ['L'] = "#logs_that_burn", ['F'] = "furnace" }, "smoker");
        shaped(" S /SCS/LLL", new Dictionary<char, string> { ['S'] = "stick", ['C'] = "#coals", ['L'] = "#logs_that_burn" }, "campfire");
        shaped(" S /SCS/LLL", new Dictionary<char, string> { ['S'] = "stick", ['C'] = "#soul_fire_base", ['L'] = "#logs_that_burn" }, "soul_campfire");
        shaped("III/ICI/III", new Dictionary<char, string> { ['I'] = "iron_nugget", ['C'] = "torch" }, "lantern");
        shaped("III/ICI/III", new Dictionary<char, string> { ['I'] = "iron_nugget", ['C'] = "soul_torch" }, "soul_lantern");
        shaped("N/I/N", new Dictionary<char, string> { ['N'] = "iron_nugget", ['I'] = "iron_ingot" }, "chain");
        shaped(" B /DOD/OOO", new Dictionary<char, string> { ['B'] = "book", ['D'] = "diamond", ['O'] = "obsidian" }, "enchanting_table");
        shaped("BBB/ I /III", new Dictionary<char, string> { ['B'] = "iron_block", ['I'] = "iron_ingot" }, "anvil");
        shaped("SAS/P P", new Dictionary<char, string> { ['S'] = "stick", ['A'] = "stone_slab", ['P'] = "#planks" }, "grindstone");
        shaped(" I /SSS", new Dictionary<char, string> { ['I'] = "iron_ingot", ['S'] = "stone" }, "stonecutter");
        shaped("II/PP/PP", new Dictionary<char, string> { ['I'] = "iron_ingot", ['P'] = "#planks" }, "smithing_table");
        shaped("FF/PP/PP", new Dictionary<char, string> { ['F'] = "flint", ['P'] = "#planks" }, "fletching_table");
        shaped("PP/MM/MM", new Dictionary<char, string> { ['P'] = "paper", ['M'] = "#planks" }, "cartography_table");
        shaped("SS/PP/PP", new Dictionary<char, string> { ['S'] = "string", ['P'] = "#planks" }, "loom");
        shaped("P P/P P/PPP", new Dictionary<char, string> { ['P'] = "#wooden_slabs" }, "composter");
        shaped("I I/I I/III", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "cauldron");
        shaped(" B /SSS", new Dictionary<char, string> { ['B'] = "blaze_rod", ['S'] = "cobblestone" }, "brewing_stand");
        shaped("GGG/GNG/OOO", new Dictionary<char, string> { ['G'] = "glass", ['N'] = "nether_star", ['O'] = "obsidian" }, "beacon");
        shaped("NNN/NHN/NNN", new Dictionary<char, string> { ['N'] = "nautilus_shell", ['H'] = "heart_of_the_sea" }, "conduit");
        shaped("CCC/CNC/CCC", new Dictionary<char, string> { ['C'] = "chiseled_stone_bricks", ['N'] = "netherite_ingot" }, "lodestone");
        shaped("OOO/GGG/OOO", new Dictionary<char, string> { ['O'] = "crying_obsidian", ['G'] = "glowstone" }, "respawn_anchor");
        shaped("I I/ I ", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "bucket");
        shapeless(new List<string> { "glass", "glass", "glass" }, "glass_bottle", 3);
        shaped(" I/I ", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "shears");
        shaped(" I /IRI/ I ", new Dictionary<char, string> { ['I'] = "iron_ingot", ['R'] = "redstone" }, "compass");
        shaped(" G /GRG/ G ", new Dictionary<char, string> { ['G'] = "gold_ingot", ['R'] = "redstone" }, "clock");
        shaped("CCC/CEC/CCC", new Dictionary<char, string> { ['C'] = "echo_shard", ['E'] = "compass" }, "recovery_compass");
        shaped("OOO/OEO/OOO", new Dictionary<char, string> { ['O'] = "obsidian", ['E'] = "ender_eye" }, "ender_chest");
        shapeless(new List<string> { "ender_pearl", "blaze_powder" }, "ender_eye");
        shaped("S/C/S", new Dictionary<char, string> { ['S'] = "shulker_shell", ['C'] = "chest" }, "shulker_box");
        shaped("GGG/GEG/GTG", new Dictionary<char, string> { ['G'] = "glass", ['E'] = "ender_eye", ['T'] = "ghast_tear" }, "end_crystal");
        shapeless(new List<string> { "blaze_rod" }, "blaze_powder", 2);
        shapeless(new List<string> { "blaze_powder", "slime_ball" }, "magma_cream");
        shapeless(new List<string> { "spider_eye", "brown_mushroom", "sugar" }, "fermented_spider_eye");
        shapeless(new List<string> { "gunpowder", "blaze_powder", "coal" }, "fire_charge", 3);
        shaped("SSS", new Dictionary<char, string> { ['S'] = "sugar_cane" }, "paper", 3);
        shapeless(new List<string> { "paper", "paper", "paper", "leather" }, "book");
        shapeless(new List<string> { "book", "ink_sac", "feather" }, "writable_book");
        shaped("GGG/GGG", new Dictionary<char, string> { ['G'] = "glass" }, "glass_pane", 16);
        shaped("III/III", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "iron_bars", 16);
        shaped("H/S", new Dictionary<char, string> { ['H'] = "honeycomb", ['S'] = "string" }, "candle");
        shaped("SCS/CCC/SCS", new Dictionary<char, string> { ['S'] = "prismarine_shard", ['C'] = "prismarine_crystals" }, "sea_lantern");
        shaped("SS/SS", new Dictionary<char, string> { ['S'] = "prismarine_shard" }, "prismarine");
        shaped("SSS/SSS/SSS", new Dictionary<char, string> { ['S'] = "prismarine_shard" }, "prismarine_bricks");
        shaped("SSS/SIS/SSS", new Dictionary<char, string> { ['S'] = "prismarine_shard", ['I'] = "black_dye" }, "dark_prismarine");
        shaped("PPP/HHH/PPP", new Dictionary<char, string> { ['P'] = "#planks", ['H'] = "honeycomb" }, "beehive");
        shaped("P/B", new Dictionary<char, string> { ['P'] = "pumpkin", ['B'] = "torch" }, "jack_o_lantern");
        shaped("BSB/B B/B B", new Dictionary<char, string> { ['B'] = "bamboo", ['S'] = "string" }, "scaffolding", 6);
        shaped("II/II/II", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "iron_door", 3);
        shaped("II/II", new Dictionary<char, string> { ['I'] = "iron_ingot" }, "iron_trapdoor");
        shaped(" B /B B/ B ", new Dictionary<char, string> { ['B'] = "brick" }, "decorated_pot");
        shapeless(new List<string> { "paper", "gunpowder" }, "firework_rocket", 3);
        shaped("B B/ B ", new Dictionary<char, string> { ['B'] = "brick" }, "flower_pot");

        // --- dyes & colored blocks ---
        var DYE_SOURCES = new (string, string)[] {
            ("dandelion", "yellow_dye"), ("poppy", "red_dye"), ("blue_orchid", "light_blue_dye"),
            ("allium", "magenta_dye"), ("azure_bluet", "light_gray_dye"), ("red_tulip", "red_dye"),
            ("orange_tulip", "orange_dye"), ("white_tulip", "light_gray_dye"), ("pink_tulip", "pink_dye"),
            ("oxeye_daisy", "light_gray_dye"), ("cornflower", "blue_dye"), ("lily_of_the_valley", "white_dye"),
            ("wither_rose", "black_dye"), ("beetroot", "red_dye"),
            ("lapis_lazuli", "blue_dye"), ("cocoa_beans", "brown_dye"), ("ink_sac", "black_dye"),
            ("bone_meal", "white_dye"), ("torchflower", "orange_dye"),
        };
        foreach (var (src, dye) in DYE_SOURCES) { shapeless(new List<string> { src }, dye); }
        foreach (var (src, dye) in new (string, string)[] { ("sunflower", "yellow_dye"), ("lilac", "magenta_dye"), ("rose_bush", "red_dye"), ("peony", "pink_dye"), ("pitcher_plant", "cyan_dye") })
        {
            shapeless(new List<string> { src }, dye, 2);
        }
        shapeless(new List<string> { "red_dye", "yellow_dye" }, "orange_dye", 2);
        shapeless(new List<string> { "blue_dye", "white_dye" }, "light_blue_dye", 2);
        shapeless(new List<string> { "blue_dye", "green_dye" }, "cyan_dye", 2);
        shapeless(new List<string> { "red_dye", "blue_dye" }, "purple_dye", 2);
        shapeless(new List<string> { "purple_dye", "pink_dye" }, "magenta_dye", 2);
        shapeless(new List<string> { "red_dye", "white_dye" }, "pink_dye", 2);
        shapeless(new List<string> { "green_dye", "white_dye" }, "lime_dye", 2);
        shapeless(new List<string> { "black_dye", "white_dye" }, "gray_dye", 2);
        shapeless(new List<string> { "gray_dye", "white_dye" }, "light_gray_dye", 2);
        foreach (var c in COLORS)
        {
            if (c != "white") { shapeless(new List<string> { $"{c}_dye", "white_wool" }, $"{c}_wool"); }
            shaped("WW", new Dictionary<char, string> { ['W'] = $"{c}_wool" }, $"{c}_carpet", 3);
            shaped("WWW/PPP", new Dictionary<char, string> { ['W'] = $"{c}_wool", ['P'] = "#planks" }, $"{c}_bed");
            shaped("GGG/GDG/GGG", new Dictionary<char, string> { ['G'] = "glass", ['D'] = $"{c}_dye" }, $"{c}_stained_glass", 8);
            shaped("GGG/GGG", new Dictionary<char, string> { ['G'] = $"{c}_stained_glass" }, $"{c}_stained_glass_pane", 16);
            shaped("TTT/TDT/TTT", new Dictionary<char, string> { ['T'] = "terracotta", ['D'] = $"{c}_dye" }, $"{c}_terracotta", 8);
            shapeless(new List<string> { $"{c}_dye", "sand", "sand", "sand", "sand", "gravel", "gravel", "gravel", "gravel" }, $"{c}_concrete_powder", 8);
            shapeless(new List<string> { "candle", $"{c}_dye" }, $"{c}_candle");
            shapeless(new List<string> { "shulker_box", $"{c}_dye" }, $"{c}_shulker_box");
        }

        // --- smelting ---
        var ORE_SMELTS = new (string, string, double)[] {
            ("iron_ore", "iron_ingot", 0.7), ("deepslate_iron_ore", "iron_ingot", 0.7), ("raw_iron", "iron_ingot", 0.7),
            ("gold_ore", "gold_ingot", 1), ("deepslate_gold_ore", "gold_ingot", 1), ("raw_gold", "gold_ingot", 1),
            ("copper_ore", "copper_ingot", 0.7), ("deepslate_copper_ore", "copper_ingot", 0.7), ("raw_copper", "copper_ingot", 0.7),
            ("coal_ore", "coal", 0.1), ("deepslate_coal_ore", "coal", 0.1),
            ("diamond_ore", "diamond", 1), ("deepslate_diamond_ore", "diamond", 1),
            ("emerald_ore", "emerald", 1), ("deepslate_emerald_ore", "emerald", 1),
            ("lapis_ore", "lapis_lazuli", 0.2), ("deepslate_lapis_ore", "lapis_lazuli", 0.2),
            ("redstone_ore", "redstone", 0.7), ("deepslate_redstone_ore", "redstone", 0.7),
            ("nether_gold_ore", "gold_ingot", 1), ("nether_quartz_ore", "quartz", 0.2),
            ("ancient_debris", "netherite_scrap", 2),
        };
        foreach (var (i, o, xp) in ORE_SMELTS) { smelt(i, o, xp, "blast"); }
        var FOOD_SMELTS = new (string, string, double)[] {
            ("beef", "cooked_beef", 0.35), ("porkchop", "cooked_porkchop", 0.35),
            ("chicken", "cooked_chicken", 0.35), ("mutton", "cooked_mutton", 0.35),
            ("rabbit", "cooked_rabbit", 0.35), ("cod", "cooked_cod", 0.35),
            ("salmon", "cooked_salmon", 0.35), ("potato", "baked_potato", 0.35),
            ("kelp", "dried_kelp", 0.1),
        };
        foreach (var (i, o, xp) in FOOD_SMELTS) { smelt(i, o, xp, "smoke"); }
        var GENERAL_SMELTS = new (string, string, double)[] {
            ("cobblestone", "stone", 0.1), ("stone", "smooth_stone", 0.1),
            ("sand", "glass", 0.1), ("red_sand", "glass", 0.1),
            ("sandstone", "smooth_sandstone", 0.1), ("red_sandstone", "smooth_red_sandstone", 0.1),
            ("quartz_block", "smooth_quartz", 0.1), ("basalt", "smooth_basalt", 0.1),
            ("clay_ball", "brick", 0.3), ("clay", "terracotta", 0.35),
            ("netherrack", "nether_brick", 0.1), ("cobbled_deepslate", "deepslate", 0.1),
            ("cactus", "green_dye", 1), ("sea_pickle", "lime_dye", 0.1),
            ("chorus_fruit", "popped_chorus_fruit", 0.1), ("wet_sponge", "sponge", 0.15),
            ("stone_bricks", "cracked_stone_bricks", 0.1), ("deepslate_bricks", "cracked_deepslate_bricks", 0.1),
            ("deepslate_tiles", "cracked_deepslate_tiles", 0.1), ("nether_bricks", "cracked_nether_bricks", 0.1),
            ("polished_blackstone_bricks", "cracked_polished_blackstone_bricks", 0.1),
        };
        foreach (var (i, o, xp) in GENERAL_SMELTS) { smelt(i, o, xp); }
        foreach (var log in TAGS["logs_that_burn"]) { smelt(log, "charcoal", 0.15); }
        foreach (var c in COLORS) { smelt($"{c}_terracotta", $"{c}_glazed_terracotta", 0.1); }

        // --- stonecutting extras ---
        cut("stone", "stone_bricks");
        cut("stone", "chiseled_stone_bricks");
        cut("stone_bricks", "chiseled_stone_bricks");
        cut("sandstone", "cut_sandstone");
        cut("sandstone", "chiseled_sandstone");
        cut("red_sandstone", "cut_red_sandstone");
        cut("red_sandstone", "chiseled_red_sandstone");
        cut("cobbled_deepslate", "polished_deepslate");
        cut("cobbled_deepslate", "deepslate_bricks");
        cut("cobbled_deepslate", "deepslate_tiles");
        cut("cobbled_deepslate", "chiseled_deepslate");
        cut("polished_deepslate", "deepslate_bricks");
        cut("polished_deepslate", "deepslate_tiles");
        cut("deepslate_bricks", "deepslate_tiles");
        cut("blackstone", "polished_blackstone");
        cut("blackstone", "polished_blackstone_bricks");
        cut("blackstone", "chiseled_polished_blackstone");
        cut("polished_blackstone", "polished_blackstone_bricks");
        cut("polished_blackstone", "chiseled_polished_blackstone");
        cut("end_stone", "end_stone_bricks");
        cut("quartz_block", "quartz_bricks");
        cut("quartz_block", "quartz_pillar");
        cut("quartz_block", "chiseled_quartz_block");
        cut("purpur_block", "purpur_pillar");
        cut("granite", "polished_granite");
        cut("diorite", "polished_diorite");
        cut("andesite", "polished_andesite");

        // --- smithing ---
        foreach (var t in new[] { "sword", "pickaxe", "axe", "shovel", "hoe" })
        {
            smithingRecipes.Add(new SmithRecipe(template: "netherite_upgrade", @base: $"diamond_{t}", addition: "netherite_ingot", output: $"netherite_{t}"));
        }
        foreach (var s in new[] { "helmet", "chestplate", "leggings", "boots" })
        {
            smithingRecipes.Add(new SmithRecipe(template: "netherite_upgrade", @base: $"diamond_{s}", addition: "netherite_ingot", output: $"netherite_{s}"));
        }
        foreach (var t in TRIM_PATTERNS)
        {
            smithingRecipes.Add(new SmithRecipe(template: $"{t}_armor_trim", @base: "#armor", addition: "#trim_material", output: "trim"));
            shaped("DTD/DMD/DDD", new Dictionary<char, string> { ['D'] = "diamond", ['T'] = $"{t}_armor_trim", ['M'] = "cobblestone" }, $"{t}_armor_trim", 2);
        }
        shaped("DTD/DMD/DDD", new Dictionary<char, string> { ['D'] = "diamond", ['T'] = "netherite_upgrade", ['M'] = "netherrack" }, "netherite_upgrade", 2);
    }
}

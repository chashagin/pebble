// Block registrations — registration order is frozen (ids persist in saves).
// REGISTRATION ORDER IS LOAD-BEARING: ids must match the golden baselines so worldgen
// output can be verified cell-for-cell against goldens.
//
// Defines: static class `BlockRegistration` (partial, spread across BlockRegistry{,2,3}.cs)
//          and static class `BlockData` (data tables shared with worldgen/UI).

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

// MARK: - data tables shared with worldgen/UI

public static class BlockData
{
    public static readonly string[] WOODS = { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "bamboo", "crimson", "warped" };
    public static readonly string[] LEAF_WOODS = { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "azalea", "flowering_azalea" };
    public static readonly string[] COLORS = { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
                                               "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" };
    public static readonly Dictionary<string, uint> COLOR_RGB = new()
    {
        ["white"] = 0xf9fffe, ["orange"] = 0xf9801d, ["magenta"] = 0xc74ebd, ["light_blue"] = 0x3ab3da,
        ["yellow"] = 0xfed83d, ["lime"] = 0x80c71f, ["pink"] = 0xf38baa, ["gray"] = 0x474f52,
        ["light_gray"] = 0x9d9d97, ["cyan"] = 0x169c9c, ["purple"] = 0x8932b8, ["blue"] = 0x3c44aa,
        ["brown"] = 0x835432, ["green"] = 0x5e7c16, ["red"] = 0xb02e26, ["black"] = 0x1d1d21,
    };
    public static readonly string[] CORALS = { "tube", "brain", "bubble", "fire", "horn" };
    public static readonly string[] FLOWERS = { "dandelion", "poppy", "blue_orchid", "allium", "azure_bluet", "red_tulip",
                                                "orange_tulip", "white_tulip", "pink_tulip", "oxeye_daisy", "cornflower",
                                                "lily_of_the_valley", "torchflower" };
    public static readonly string[] COPPER_STAGES = { "", "exposed_", "weathered_", "oxidized_" };

    public static readonly (string, string)[] STONE_FAMILIES =
    {
        ("cobblestone", "cobblestone"), ("mossy_cobblestone", "mossy_cobblestone"),
        ("stone", "stone"), ("smooth_stone", "smooth_stone"),
        ("stone_brick", "stone_bricks"), ("mossy_stone_brick", "mossy_stone_bricks"),
        ("granite", "granite"), ("polished_granite", "polished_granite"),
        ("diorite", "diorite"), ("polished_diorite", "polished_diorite"),
        ("andesite", "andesite"), ("polished_andesite", "polished_andesite"),
        ("cobbled_deepslate", "cobbled_deepslate"), ("polished_deepslate", "polished_deepslate"),
        ("deepslate_brick", "deepslate_bricks"), ("deepslate_tile", "deepslate_tiles"),
        ("brick", "bricks"), ("mud_brick", "mud_bricks"),
        ("sandstone", "sandstone"), ("smooth_sandstone", "sandstone_top"), ("cut_sandstone", "cut_sandstone"),
        ("red_sandstone", "red_sandstone"), ("smooth_red_sandstone", "red_sandstone_top"), ("cut_red_sandstone", "cut_red_sandstone"),
        ("prismarine", "prismarine"), ("prismarine_brick", "prismarine_bricks"), ("dark_prismarine", "dark_prismarine"),
        ("nether_brick", "nether_bricks"), ("red_nether_brick", "red_nether_bricks"),
        ("blackstone", "blackstone"), ("polished_blackstone", "polished_blackstone"), ("polished_blackstone_brick", "polished_blackstone_bricks"),
        ("end_stone_brick", "end_stone_bricks"), ("purpur", "purpur_block"),
        ("quartz", "quartz_block_side"), ("smooth_quartz", "quartz_block_bottom"),
        ("tuff", "tuff"),
    };
}

public static partial class BlockRegistration
{
    // MARK: - builder shorthands

    private static ushort stone(string name, double hardness, double? resistance = null, int tier = 0,
                                int light = 0, string sound = "stone", TexSpec tex = null, Func<int, int, int> texFn = null,
                                string display = null, DropSpec drops = null)
    {
        return registerBlock(name, tex: tex, texFn: texFn, display: display, light: light,
                             hardness: hardness, resistance: resistance ?? hardness * 2,
                             tool: ToolType.pickaxe, tier: tier, requiresTool: true, sound: sound, drops: drops);
    }

    private static ushort wood(string name, Shape shape = Shape.cube, TexSpec tex = null, Func<int, int, int> texFn = null,
                               bool opaque = true, bool? fullCube = null, int light = 0, int? lightOpacity = null,
                               double hardness = 2, string sound = "wood", int flammable = 5,
                               PistonBehavior piston = PistonBehavior.normal, DropSpec drops = null)
    {
        return registerBlock(name, shape: shape, tex: tex, texFn: texFn, opaque: opaque, fullCube: fullCube,
                             light: light, lightOpacity: lightOpacity, hardness: hardness, resistance: 3,
                             tool: ToolType.axe, sound: sound, flammable: flammable, burnOdds: flammable > 0 ? 20 : 0,
                             piston: piston, drops: drops);
    }

    private static ushort earth(string name, double hardness, string sound = "gravel",
                                bool gravity = false, DropSpec drops = null)
    {
        return registerBlock(name, hardness: hardness, tool: ToolType.shovel, sound: sound, gravity: gravity, drops: drops);
    }

    private static ushort plant(string name, Shape shape = Shape.cross, TexSpec tex = null, Func<int, int, int> texFn = null,
                                string display = null, bool replaceable = false, int light = 0, int tint = 0,
                                string sound = "grass", int flammable = 60, bool randomTicks = false,
                                bool climbable = false, DropSpec drops = null)
    {
        return registerBlock(name, shape: shape, tex: tex, texFn: texFn, display: display,
                             opaque: false, solid: false, fullCube: false, replaceable: replaceable,
                             light: light, hardness: 0, sound: sound, tint: tint,
                             flammable: flammable, burnOdds: flammable > 0 ? 100 : 0,
                             piston: PistonBehavior.destroy, climbable: climbable, randomTicks: randomTicks,
                             drops: drops, ao: false);
    }

    private static ushort ore(string name, int tier, string dropItem, double hardness = 3,
                              string sound = "stone", bool randomTicks = false, DropSpec drops = null)
    {
        DropSpec spec = DropSpec.selfDrop;
        if (drops != null)
        {
            spec = drops;
        }
        else if (dropItem != null)
        {
            var item = dropItem;
            spec = DropSpec.fn((m, ctx) =>
            {
                if (ctx.silkTouch) { return new List<Drop> { new Drop(name) }; }
                var bonus = ctx.fortune > 0 ? System.Math.Max(1, (int)(ctx.random() * (double)(ctx.fortune + 2))) : 1;
                return new List<Drop> { new Drop(item, bonus) };
            });
        }
        return registerBlock(name, hardness: hardness, resistance: 3, tool: ToolType.pickaxe, tier: tier,
                            requiresTool: true, sound: sound, randomTicks: randomTicks, drops: spec);
    }

    // MARK: - registration (call registerAllBlocks() exactly once at startup)

    private static bool registered = false;

    public static void registerAllBlocks()
    {
        if (registered) { return; }
        registered = true;

        // air
        registerBlock("air", shape: Shape.air, opaque: false, solid: false, fullCube: false, replaceable: true, lightOpacity: 0, hardness: 0, drops: DropSpec.none);
        registerBlock("cave_air", shape: Shape.air, opaque: false, solid: false, fullCube: false, replaceable: true, lightOpacity: 0, hardness: 0, drops: DropSpec.none);
        registerBlock("void_air", shape: Shape.air, opaque: false, solid: false, fullCube: false, replaceable: true, lightOpacity: 0, hardness: 0, drops: DropSpec.none);

        // stones
        stone("stone", 1.5, resistance: 6, drops: DropSpec.item("cobblestone"));
        stone("granite", 1.5, resistance: 6);
        stone("polished_granite", 1.5, resistance: 6);
        stone("diorite", 1.5, resistance: 6);
        stone("polished_diorite", 1.5, resistance: 6);
        stone("andesite", 1.5, resistance: 6);
        stone("polished_andesite", 1.5, resistance: 6);
        registerBlock("deepslate", tex: texCol("deepslate_top", "deepslate"), hardness: 3, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate", drops: DropSpec.item("cobbled_deepslate"));
        registerBlock("cobbled_deepslate", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("polished_deepslate", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("deepslate_bricks", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("cracked_deepslate_bricks", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("deepslate_tiles", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("cracked_deepslate_tiles", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("chiseled_deepslate", hardness: 3.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "deepslate");
        registerBlock("reinforced_deepslate", tex: texTB("reinforced_deepslate_top", "reinforced_deepslate_bottom", "reinforced_deepslate_side"), hardness: 55, resistance: 1200, tool: ToolType.pickaxe, sound: "deepslate", drops: DropSpec.none);
        registerBlock("tuff", hardness: 1.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "tuff");
        registerBlock("calcite", hardness: 0.75, tool: ToolType.pickaxe, requiresTool: true, sound: "stone");
        stone("dripstone_block", 1.5, sound: "pointed_dripstone");
        stone("cobblestone", 2, resistance: 6);
        stone("mossy_cobblestone", 2, resistance: 6);
        stone("smooth_stone", 2, resistance: 6);
        stone("stone_bricks", 1.5, resistance: 6);
        stone("mossy_stone_bricks", 1.5, resistance: 6);
        stone("cracked_stone_bricks", 1.5, resistance: 6);
        stone("chiseled_stone_bricks", 1.5, resistance: 6);
        stone("bricks", 2, resistance: 6);
        registerBlock("bedrock", hardness: -1, resistance: 3_600_000, drops: DropSpec.none);
        stone("obsidian", 50, resistance: 1200, tier: 3);
        stone("crying_obsidian", 50, resistance: 1200, tier: 3, light: 10);

        // earth
        registerBlock("grass_block", tex: texTB("grass_top", "dirt", "grass_side"), hardness: 0.6, tool: ToolType.shovel, sound: "grass", tint: 1, randomTicks: true, drops: DropSpec.item("dirt"));
        earth("dirt", 0.5);
        earth("coarse_dirt", 0.5);
        earth("rooted_dirt", 0.5);
        registerBlock("podzol", tex: texTB("podzol_top", "dirt", "podzol_side"), hardness: 0.5, tool: ToolType.shovel, sound: "gravel", drops: DropSpec.item("dirt"));
        registerBlock("mycelium", tex: texTB("mycelium_top", "dirt", "mycelium_side"), hardness: 0.6, tool: ToolType.shovel, sound: "grass", randomTicks: true, drops: DropSpec.item("dirt"));
        registerBlock("dirt_path", shape: Shape.path, tex: texTB("dirt_path_top", "dirt", "dirt_path_side"), opaque: false, fullCube: false, hardness: 0.65, tool: ToolType.shovel, sound: "grass", drops: DropSpec.item("dirt"));
        registerBlock("farmland", shape: Shape.farmland, tex: texTB("farmland_dry", "dirt", "dirt"),
                      texFn: (m, f) => f == 1 ? (m >= 7 ? tileId("farmland_wet") : tileId("farmland_dry")) : tileId("dirt"),
                      opaque: false, fullCube: false, hardness: 0.6, tool: ToolType.shovel, sound: "gravel", randomTicks: true, drops: DropSpec.item("dirt"));
        registerBlock("mud", hardness: 0.5, tool: ToolType.shovel, sound: "mud");
        registerBlock("packed_mud", hardness: 1, tool: ToolType.pickaxe, sound: "mud");
        stone("mud_bricks", 1.5, sound: "mud");
        earth("clay", 0.6, drops: DropSpec.list(new[] { new Drop("clay_ball", 4) }));
        earth("gravel", 0.6, gravity: true, drops: DropSpec.fn((m, ctx) => ctx.random() < 0.1 + (double)ctx.fortune * 0.04 ? new List<Drop> { new Drop("flint") } : new List<Drop> { new Drop("gravel") }));
        earth("sand", 0.5, sound: "sand", gravity: true);
        earth("red_sand", 0.5, sound: "sand", gravity: true);
        earth("suspicious_sand", 0.25, sound: "suspicious_sand", gravity: true, drops: DropSpec.none);
        earth("suspicious_gravel", 0.25, sound: "suspicious_gravel", gravity: true, drops: DropSpec.none);
        stone("sandstone", 0.8, tex: texTB("sandstone_top", "sandstone_bottom", "sandstone_side"));
        stone("chiseled_sandstone", 0.8, tex: texTB("sandstone_top", "sandstone_top", "chiseled_sandstone"));
        stone("cut_sandstone", 0.8, tex: texTB("sandstone_top", "sandstone_top", "cut_sandstone"));
        stone("smooth_sandstone", 2, tex: TexSpec.named("sandstone_top"));
        stone("red_sandstone", 0.8, tex: texTB("red_sandstone_top", "red_sandstone_bottom", "red_sandstone_side"));
        stone("chiseled_red_sandstone", 0.8, tex: texTB("red_sandstone_top", "red_sandstone_top", "chiseled_red_sandstone"));
        stone("cut_red_sandstone", 0.8, tex: texTB("red_sandstone_top", "red_sandstone_top", "cut_red_sandstone"));
        stone("smooth_red_sandstone", 2, tex: TexSpec.named("red_sandstone_top"));

        // ores
        ore("coal_ore", 0, "coal");
        ore("deepslate_coal_ore", 0, "coal", hardness: 4.5, sound: "deepslate");
        ore("copper_ore", 1, null, drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("copper_ore") } : new List<Drop> { new Drop("raw_copper", 2, 5) }));
        ore("deepslate_copper_ore", 1, null, hardness: 4.5, sound: "deepslate", drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("deepslate_copper_ore") } : new List<Drop> { new Drop("raw_copper", 2, 5) }));
        ore("iron_ore", 1, null, drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("iron_ore") } : new List<Drop> { new Drop("raw_iron") }));
        ore("deepslate_iron_ore", 1, null, hardness: 4.5, sound: "deepslate", drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("deepslate_iron_ore") } : new List<Drop> { new Drop("raw_iron") }));
        ore("gold_ore", 2, null, drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("gold_ore") } : new List<Drop> { new Drop("raw_gold") }));
        ore("deepslate_gold_ore", 2, null, hardness: 4.5, sound: "deepslate", drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("deepslate_gold_ore") } : new List<Drop> { new Drop("raw_gold") }));
        ore("redstone_ore", 2, null, randomTicks: true, drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("redstone_ore") } : new List<Drop> { new Drop("redstone", 4, 5 + ctx.fortune) }));
        ore("deepslate_redstone_ore", 2, null, hardness: 4.5, sound: "deepslate", drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("deepslate_redstone_ore") } : new List<Drop> { new Drop("redstone", 4, 5 + ctx.fortune) }));
        ore("lapis_ore", 1, null, drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("lapis_ore") } : new List<Drop> { new Drop("lapis_lazuli", 4, 9) }));
        ore("deepslate_lapis_ore", 1, null, hardness: 4.5, sound: "deepslate", drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("deepslate_lapis_ore") } : new List<Drop> { new Drop("lapis_lazuli", 4, 9) }));
        ore("diamond_ore", 2, "diamond");
        ore("deepslate_diamond_ore", 2, "diamond", hardness: 4.5, sound: "deepslate");
        ore("emerald_ore", 2, "emerald");
        ore("deepslate_emerald_ore", 2, "emerald", hardness: 4.5, sound: "deepslate");
        ore("nether_gold_ore", 0, null, sound: "netherrack", drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("nether_gold_ore") } : new List<Drop> { new Drop("gold_nugget", 2, 6) }));
        ore("nether_quartz_ore", 0, "quartz", sound: "netherrack");
        registerBlock("ancient_debris", tex: texCol("ancient_debris_top", "ancient_debris_side"), hardness: 30, resistance: 1200, tool: ToolType.pickaxe, tier: 3, requiresTool: true, sound: "netherite");

        // mineral blocks
        stone("coal_block", 5, resistance: 6);
        stone("iron_block", 5, resistance: 6, tier: 1, sound: "metal");
        stone("gold_block", 3, resistance: 6, tier: 2, sound: "metal");
        stone("diamond_block", 5, resistance: 6, tier: 2, sound: "metal");
        stone("emerald_block", 5, resistance: 6, tier: 2, sound: "metal");
        stone("lapis_block", 3, tier: 1);
        stone("redstone_block", 5, resistance: 6, sound: "metal");
        stone("netherite_block", 50, resistance: 1200, tier: 3, sound: "netherite");
        stone("quartz_block", 0.8, tex: texTB("quartz_block_top", "quartz_block_bottom", "quartz_block_side"));
        stone("chiseled_quartz_block", 0.8, tex: texCol("chiseled_quartz_block_top", "chiseled_quartz_block"));
        stone("quartz_pillar", 0.8, tex: texCol("quartz_pillar_top", "quartz_pillar"));
        stone("smooth_quartz", 2, tex: TexSpec.named("quartz_block_bottom"));
        stone("quartz_bricks", 0.8);
        stone("raw_iron_block", 5, resistance: 6, tier: 1);
        stone("raw_copper_block", 5, resistance: 6, tier: 1);
        stone("raw_gold_block", 5, resistance: 6, tier: 2);
        registerBlock("amethyst_block", hardness: 1.5, tool: ToolType.pickaxe, requiresTool: true, sound: "amethyst");
        registerBlock("budding_amethyst", hardness: 1.5, tool: ToolType.pickaxe, requiresTool: true, sound: "amethyst", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.none);

        // wood family
        string woodSound(string w)
            => w == "cherry" ? "cherry" : w == "bamboo" ? "bamboo_wood" : (w == "crimson" || w == "warped") ? "nether_wood" : "wood";
        Func<int, int, int> logTexFn(string top, string side)
        {
            return (m, f) =>
            {
                int topT = tileId(top), sideT = tileId(side);
                var axis = m & 3;
                if (axis == 0) { return (f == 0 || f == 1) ? topT : sideT; }
                if (axis == 1) { return (f == 4 || f == 5) ? topT : sideT; }
                return (f == 2 || f == 3) ? topT : sideT;
            };
        }
        foreach (var w in BlockData.WOODS)
        {
            var snd = woodSound(w);
            var flam = (w == "crimson" || w == "warped") ? 0 : 5;
            if (w != "bamboo")
            {
                var logName = (w == "crimson" || w == "warped") ? $"{w}_stem" : $"{w}_log";
                wood(logName, tex: texCol($"{logName}_top", logName), texFn: logTexFn($"{logName}_top", logName), sound: snd, flammable: flam);
                var strippedLog = $"stripped_{logName}";
                wood(strippedLog, tex: texCol($"{strippedLog}_top", strippedLog), texFn: logTexFn($"{strippedLog}_top", strippedLog), sound: snd, flammable: flam);
                var woodName = (w == "crimson" || w == "warped") ? $"{w}_hyphae" : $"{w}_wood";
                wood(woodName, tex: TexSpec.named(logName), sound: snd, flammable: flam);
                wood($"stripped_{woodName}", tex: TexSpec.named(strippedLog), sound: snd, flammable: flam);
            }
            else
            {
                wood("bamboo_block", tex: texCol("bamboo_block_top", "bamboo_block"), sound: "bamboo_wood");
                wood("stripped_bamboo_block", tex: texCol("stripped_bamboo_block_top", "stripped_bamboo_block"), sound: "bamboo_wood");
            }
            var planksName = w == "bamboo" ? "bamboo_planks" : $"{w}_planks";
            wood(planksName, sound: snd, flammable: flam);
            if (w == "bamboo") { wood("bamboo_mosaic", sound: snd); }
            wood($"{w}_stairs", shape: Shape.stairs, tex: TexSpec.named(planksName), opaque: false, fullCube: false, lightOpacity: 0, sound: snd, flammable: flam);
            wood($"{w}_slab", shape: Shape.slab, tex: TexSpec.named(planksName), opaque: false, fullCube: false, lightOpacity: 0, sound: snd, flammable: flam,
                 drops: DropSpec.fn((m, _) => new List<Drop> { new Drop($"{w}_slab", (m & 3) == 2 ? 2 : 1) }));
            if (w == "bamboo")
            {
                wood("bamboo_mosaic_stairs", shape: Shape.stairs, tex: TexSpec.named("bamboo_mosaic"), opaque: false, fullCube: false, sound: snd);
                wood("bamboo_mosaic_slab", shape: Shape.slab, tex: TexSpec.named("bamboo_mosaic"), opaque: false, fullCube: false, sound: snd);
            }
            wood($"{w}_fence", shape: Shape.fence, tex: TexSpec.named(planksName), opaque: false, fullCube: false, sound: snd, flammable: flam);
            wood($"{w}_fence_gate", shape: Shape.fenceGate, tex: TexSpec.named(planksName), opaque: false, fullCube: false, sound: snd, flammable: flam);
            wood($"{w}_door", shape: Shape.door, tex: TexSpec.named($"{w}_door"), opaque: false, fullCube: false, sound: snd, piston: PistonBehavior.destroy,
                 drops: DropSpec.fn((m, _) => (m & 8) != 0 ? new List<Drop>() : new List<Drop> { new Drop($"{w}_door") }));
            wood($"{w}_trapdoor", shape: Shape.trapdoor, tex: TexSpec.named($"{w}_trapdoor"), opaque: false, fullCube: false, hardness: 3, sound: snd);
            registerBlock($"{w}_button", shape: Shape.button, tex: TexSpec.named(planksName), opaque: false, solid: false, fullCube: false, hardness: 0.5, sound: snd, piston: PistonBehavior.destroy);
            registerBlock($"{w}_pressure_plate", shape: Shape.pressurePlate, tex: TexSpec.named(planksName), opaque: false, solid: false, fullCube: false, hardness: 0.5, sound: snd, piston: PistonBehavior.destroy);
            registerBlock($"{w}_sign", shape: Shape.sign, tex: TexSpec.named(planksName), opaque: false, solid: false, fullCube: false, hardness: 1, sound: snd, piston: PistonBehavior.destroy, drops: DropSpec.item($"{w}_sign"));
            registerBlock($"{w}_wall_sign", shape: Shape.wallSign, tex: TexSpec.named(planksName), opaque: false, solid: false, fullCube: false, hardness: 1, sound: snd, piston: PistonBehavior.destroy, drops: DropSpec.item($"{w}_sign"));
            registerBlock($"{w}_hanging_sign", shape: Shape.hangingSign, tex: TexSpec.named(planksName), opaque: false, solid: false, fullCube: false, hardness: 1, sound: "hanging_sign", piston: PistonBehavior.destroy, drops: DropSpec.item($"{w}_hanging_sign"));
        }

        // leaves / saplings
        foreach (var w in BlockData.LEAF_WOODS)
        {
            var name = $"{w}_leaves";
            var tint = (w == "birch" || w == "spruce" || w == "cherry" || w == "azalea" || w == "flowering_azalea") ? 0 : 2;
            registerBlock(name, tex: TexSpec.named(name), opaque: false, lightOpacity: 1, hardness: 0.2, tool: ToolType.shears,
                          sound: w == "cherry" ? "cherry" : w.Contains("azalea") ? "azalea" : "grass",
                          tint: tint, flammable: 30, burnOdds: 60, randomTicks: true, transparentRender: true, cullSame: false,
                          drops: DropSpec.fn((m, ctx) =>
                          {
                              if (ctx.shears || ctx.silkTouch) { return new List<Drop> { new Drop(name) }; }
                              var outDrops = new List<Drop>();
                              var sapling = w == "azalea" ? "azalea" : w == "flowering_azalea" ? "flowering_azalea"
                                  : w == "mangrove" ? "mangrove_propagule" : $"{w}_sapling";
                              if (ctx.random() < 0.05 + (double)ctx.fortune * 0.01) { outDrops.Add(new Drop(sapling)); }
                              if (ctx.random() < 0.02 + (double)ctx.fortune * 0.005) { outDrops.Add(new Drop("stick", 1, 2)); }
                              if (w == "oak" || w == "dark_oak")
                              {
                                  if (ctx.random() < 0.005 + (double)ctx.fortune * 0.001) { outDrops.Add(new Drop("apple")); }
                              }
                              return outDrops;
                          }), ao: true);
        }
        foreach (var w in new[] { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "cherry" })
        {
            plant($"{w}_sapling", randomTicks: true);
        }
        registerBlock("mangrove_propagule", shape: Shape.propagule, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true, ao: false);
        registerBlock("mangrove_roots", shape: Shape.cube, opaque: false, lightOpacity: 1, hardness: 0.7, tool: ToolType.axe, sound: "mangrove_roots", transparentRender: true);
        registerBlock("muddy_mangrove_roots", tex: texCol("muddy_mangrove_roots_top", "muddy_mangrove_roots_side"), hardness: 0.7, tool: ToolType.shovel, sound: "mud");

        // liquids
        registerBlock("water", shape: Shape.liquid, opaque: false, solid: false, fullCube: false, replaceable: true,
                      lightOpacity: 1, hardness: 100, sound: "water", tint: 3, piston: PistonBehavior.destroy, translucent: true, drops: DropSpec.none, ao: false);
        registerBlock("lava", shape: Shape.liquid, opaque: false, solid: false, fullCube: false, replaceable: true,
                      light: 15, lightOpacity: 1, hardness: 100, sound: "lava", piston: PistonBehavior.destroy, emissiveRender: true, drops: DropSpec.none, ao: false);

        // glass
        registerBlock("glass", opaque: false, lightOpacity: 0, hardness: 0.3, sound: "glass", transparentRender: true, cullSame: true, drops: DropSpec.none);
        registerBlock("tinted_glass", opaque: false, lightOpacity: 15, hardness: 0.3, sound: "glass", translucent: true, cullSame: true, drops: DropSpec.item("tinted_glass"));
        registerBlock("glass_pane", shape: Shape.pane, tex: TexSpec.named("glass"), opaque: false, fullCube: false, hardness: 0.3, sound: "glass", drops: DropSpec.none);
        registerBlock("iron_bars", shape: Shape.bars, tex: TexSpec.named("iron_bars"), opaque: false, fullCube: false, hardness: 5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "metal");

        // colored families
        foreach (var c in BlockData.COLORS)
        {
            registerBlock($"{c}_wool", hardness: 0.8, tool: ToolType.shears, sound: "cloth", flammable: 30, burnOdds: 60);
            registerBlock($"{c}_carpet", shape: Shape.carpet, tex: TexSpec.named($"{c}_wool"), opaque: false, fullCube: false, hardness: 0.1, sound: "cloth", flammable: 60, burnOdds: 20);
            stone($"{c}_concrete", 1.8);
            earth($"{c}_concrete_powder", 0.5, sound: "sand", gravity: true);
            stone($"{c}_terracotta", 1.25, resistance: 4.2);
            registerBlock($"{c}_glazed_terracotta", hardness: 1.4, resistance: 2.8, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.block);
            registerBlock($"{c}_stained_glass", opaque: false, lightOpacity: 0, hardness: 0.3, sound: "glass", translucent: true, cullSame: true, drops: DropSpec.none);
            registerBlock($"{c}_stained_glass_pane", shape: Shape.pane, tex: TexSpec.named($"{c}_stained_glass"), opaque: false, fullCube: false, hardness: 0.3, sound: "glass", translucent: true, drops: DropSpec.none);
            registerBlock($"{c}_bed", shape: Shape.bed, tex: TexSpec.named($"{c}_wool"), opaque: false, fullCube: false, hardness: 0.2, sound: "wood", piston: PistonBehavior.destroy,
                          drops: DropSpec.fn((m, _) => (m & 8) != 0 ? new List<Drop>() : new List<Drop> { new Drop($"{c}_bed") }));
            registerBlock($"{c}_candle", shape: Shape.candle, tex: TexSpec.named($"{c}_candle"), opaque: false, solid: false, fullCube: false, hardness: 0.1, sound: "candle", piston: PistonBehavior.destroy,
                          drops: DropSpec.fn((m, _) => new List<Drop> { new Drop($"{c}_candle", (m & 3) + 1) }));
            registerBlock($"{c}_shulker_box", opaque: false, hardness: 2, tool: ToolType.pickaxe, sound: "stone", piston: PistonBehavior.blockEntity, drops: DropSpec.none);
        }
        stone("terracotta", 1.25, resistance: 4.2);
        registerBlock("candle", shape: Shape.candle, opaque: false, solid: false, fullCube: false, hardness: 0.1, sound: "candle", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, _) => new List<Drop> { new Drop("candle", (m & 3) + 1) }));
        registerBlock("shulker_box", opaque: false, hardness: 2, tool: ToolType.pickaxe, sound: "stone", piston: PistonBehavior.blockEntity, drops: DropSpec.none);

        // plants
        plant("short_grass", display: "Grass", replaceable: true, tint: 1,
              drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("short_grass") } : (ctx.random() < 0.125 ? new List<Drop> { new Drop("wheat_seeds") } : new List<Drop>())));
        plant("fern", replaceable: true, tint: 1,
              drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("fern") } : (ctx.random() < 0.125 ? new List<Drop> { new Drop("wheat_seeds") } : new List<Drop>())));
        plant("dead_bush", drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("dead_bush") } : new List<Drop> { new Drop("stick", 0, 2) }));
        plant("tall_grass", shape: Shape.tallCross, replaceable: true, tint: 1,
              drops: DropSpec.fn((m, ctx) => ctx.shears && (m & 1) == 0 ? new List<Drop> { new Drop("tall_grass") } : new List<Drop>()));
        plant("large_fern", shape: Shape.tallCross, replaceable: true, tint: 1,
              drops: DropSpec.fn((m, ctx) => ctx.shears && (m & 1) == 0 ? new List<Drop> { new Drop("large_fern") } : new List<Drop>()));
        foreach (var f in BlockData.FLOWERS) { plant(f); }
        plant("wither_rose");
        foreach (var f in new[] { "sunflower", "lilac", "rose_bush", "peony" })
        {
            plant(f, shape: Shape.tallCross, tex: TexSpec.named($"{f}_bottom"), texFn: (m, _) => tileId((m & 1) != 0 ? $"{f}_top" : $"{f}_bottom"),
                  drops: DropSpec.fn((m, _) => (m & 1) != 0 ? new List<Drop>() : new List<Drop> { new Drop(f) }));
        }
        plant("pitcher_plant", shape: Shape.tallCross, tex: TexSpec.named("pitcher_plant_bottom"), texFn: (m, _) => tileId((m & 1) != 0 ? "pitcher_plant_top" : "pitcher_plant_bottom"),
              drops: DropSpec.fn((m, _) => (m & 1) != 0 ? new List<Drop>() : new List<Drop> { new Drop("pitcher_plant") }));
        registerBlock("pitcher_crop", shape: Shape.pitcherCropShape, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.none, ao: false);
        plant("brown_mushroom", light: 1, flammable: 0);
        plant("red_mushroom", flammable: 0);
        plant("crimson_fungus", sound: "fungus", flammable: 0);
        plant("warped_fungus", sound: "fungus", flammable: 0);
        plant("crimson_roots", shape: Shape.rootsShape, replaceable: true, sound: "fungus", flammable: 0);
        plant("warped_roots", shape: Shape.rootsShape, replaceable: true, sound: "fungus", flammable: 0);
        plant("nether_sprouts", replaceable: true, sound: "fungus", flammable: 0,
              drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("nether_sprouts") } : new List<Drop>()));
        registerBlock("weeping_vines", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "fungus", piston: PistonBehavior.destroy, climbable: true, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => (ctx.shears || ctx.random() < 0.33) ? new List<Drop> { new Drop("weeping_vines") } : new List<Drop>()), ao: false);
        registerBlock("twisting_vines", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "fungus", piston: PistonBehavior.destroy, climbable: true, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => (ctx.shears || ctx.random() < 0.33) ? new List<Drop> { new Drop("twisting_vines") } : new List<Drop>()), ao: false);
        registerBlock("sugar_cane", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", tint: 1, flammable: 60, piston: PistonBehavior.destroy, randomTicks: true, ao: false);
        registerBlock("cactus", shape: Shape.cactusShape, tex: texTB("cactus_top", "cactus_bottom", "cactus_side"), opaque: false, fullCube: false, hardness: 0.4, sound: "cloth", piston: PistonBehavior.destroy, randomTicks: true);
        registerBlock("bamboo", shape: Shape.bamboo, opaque: false, fullCube: false, hardness: 1, tool: ToolType.sword, sound: "bamboo", flammable: 60, piston: PistonBehavior.destroy, randomTicks: true);
        registerBlock("bamboo_sapling", shape: Shape.bambooSapling, opaque: false, solid: false, fullCube: false, hardness: 1, sound: "bamboo", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.item("bamboo"));
        registerBlock("vine", shape: Shape.vine, opaque: false, solid: false, fullCube: false, hardness: 0.2, tool: ToolType.shears, sound: "grass", tint: 2, flammable: 15, burnOdds: 100, piston: PistonBehavior.destroy, climbable: true, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("vine") } : new List<Drop>()), ao: false);
        registerBlock("glow_lichen", shape: Shape.glowLichen, opaque: false, solid: false, fullCube: false, replaceable: true, light: 7, hardness: 0.2, tool: ToolType.shears, sound: "glow_lichen", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("glow_lichen") } : new List<Drop>()), ao: false);
        registerBlock("sculk_vein", shape: Shape.sculkVein, opaque: false, solid: false, fullCube: false, replaceable: true, hardness: 0.2, tool: ToolType.hoe, sound: "sculk", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("sculk_vein") } : new List<Drop>()), ao: false);
        registerBlock("lily_pad", shape: Shape.lilyPad, opaque: false, fullCube: false, hardness: 0, sound: "wet_grass", tint: 2, piston: PistonBehavior.destroy);
        plant("seagrass", replaceable: true, sound: "wet_grass", flammable: 0,
              drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("seagrass") } : new List<Drop>()));
        plant("tall_seagrass", shape: Shape.tallCross, tex: TexSpec.named("tall_seagrass_bottom"), texFn: (m, _) => tileId((m & 1) != 0 ? "tall_seagrass_top" : "tall_seagrass_bottom"),
              replaceable: true, sound: "wet_grass", flammable: 0, drops: DropSpec.none);
        registerBlock("kelp", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "wet_grass", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.item("kelp"), ao: false);
        registerBlock("kelp_plant", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "wet_grass", piston: PistonBehavior.destroy, drops: DropSpec.item("kelp"), ao: false);
        registerBlock("sea_pickle", shape: Shape.seaPickle, opaque: false, fullCube: false, light: 6, hardness: 0, sound: "slime", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, _) => new List<Drop> { new Drop("sea_pickle", (m & 3) + 1) }));
        registerBlock("spore_blossom", shape: Shape.sporeBlossom, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy);
        registerBlock("hanging_roots", shape: Shape.hangingRoots, opaque: false, solid: false, fullCube: false, replaceable: true, hardness: 0, tool: ToolType.shears, sound: "hanging_sign", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.shears ? new List<Drop> { new Drop("hanging_roots") } : new List<Drop>()), ao: false);
        registerBlock("big_dripleaf", shape: Shape.bigDripleaf, opaque: false, fullCube: false, hardness: 0.1, sound: "big_dripleaf", tint: 2, flammable: 15, piston: PistonBehavior.destroy);
        registerBlock("big_dripleaf_stem", shape: Shape.cross, tex: TexSpec.named("big_dripleaf_stem"), opaque: false, solid: false, fullCube: false, hardness: 0.1, sound: "big_dripleaf", tint: 2, piston: PistonBehavior.destroy, drops: DropSpec.item("big_dripleaf"), ao: false);
        registerBlock("small_dripleaf", shape: Shape.smallDripleafShape, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "big_dripleaf", tint: 2, piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.shears && (m & 1) == 0 ? new List<Drop> { new Drop("small_dripleaf") } : new List<Drop>()));
        registerBlock("moss_block", hardness: 0.1, tool: ToolType.hoe, sound: "moss");
        registerBlock("moss_carpet", shape: Shape.carpet, tex: TexSpec.named("moss_block"), opaque: false, fullCube: false, hardness: 0.1, sound: "moss", piston: PistonBehavior.destroy);
        registerBlock("pink_petals", shape: Shape.frogspawn, tex: TexSpec.named("pink_petals"), opaque: false, solid: false, fullCube: false, hardness: 0, sound: "cherry", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, _) => new List<Drop> { new Drop("pink_petals", (m & 3) + 1) }), ao: false);
        registerBlock("azalea", shape: Shape.cross, opaque: false, fullCube: false, hardness: 0, sound: "azalea", flammable: 60, piston: PistonBehavior.destroy, randomTicks: true);
        registerBlock("flowering_azalea", shape: Shape.cross, opaque: false, fullCube: false, hardness: 0, sound: "azalea", flammable: 60, piston: PistonBehavior.destroy, randomTicks: true);
        registerBlock("cave_vines", shape: Shape.caveVinesShape, tex: TexSpec.named("cave_vines"), texFn: (m, _) => tileId((m & 8) != 0 ? "cave_vines_lit" : "cave_vines"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, climbable: true, randomTicks: true,
                      drops: DropSpec.fn((m, _) => (m & 8) != 0 ? new List<Drop> { new Drop("glow_berries") } : new List<Drop>()), ao: false);
        registerBlock("cave_vines_plant", shape: Shape.caveVinesShape, tex: TexSpec.named("cave_vines_plant"), texFn: (m, _) => tileId((m & 8) != 0 ? "cave_vines_plant_lit" : "cave_vines_plant"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, climbable: true,
                      drops: DropSpec.fn((m, _) => (m & 8) != 0 ? new List<Drop> { new Drop("glow_berries") } : new List<Drop>()), ao: false);
        registerBlock("sweet_berry_bush", shape: Shape.sweetBerry, tex: TexSpec.named("sweet_berry_bush_stage3"), texFn: (m, _) => tileId($"sweet_berry_bush_stage{System.Math.Min(3, m & 3)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, _) => (m & 3) >= 2 ? new List<Drop> { new Drop("sweet_berries", 1, 3) } : new List<Drop>()));

        // crops
        registerBlock("wheat", shape: Shape.crop, tex: TexSpec.named("wheat_stage7"), texFn: (m, _) => tileId($"wheat_stage{System.Math.Min(7, m & 7)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => (m & 7) >= 7 ? new List<Drop> { new Drop("wheat"), new Drop("wheat_seeds", 1, 3 + ctx.fortune) } : new List<Drop> { new Drop("wheat_seeds") }), ao: false);
        registerBlock("carrots", shape: Shape.crop, tex: TexSpec.named("carrots_stage3"), texFn: (m, _) => tileId($"carrots_stage{System.Math.Min(3, (m & 7) >> 1)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => (m & 7) >= 7 ? new List<Drop> { new Drop("carrot", 2, 5 + ctx.fortune) } : new List<Drop> { new Drop("carrot") }), ao: false);
        registerBlock("potatoes", shape: Shape.crop, tex: TexSpec.named("potatoes_stage3"), texFn: (m, _) => tileId($"potatoes_stage{System.Math.Min(3, (m & 7) >> 1)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) =>
                      {
                          if ((m & 7) < 7) { return new List<Drop> { new Drop("potato") }; }
                          var d = new List<Drop> { new Drop("potato", 2, 5 + ctx.fortune) };
                          if (ctx.random() < 0.02) { d.Add(new Drop("poisonous_potato")); }
                          return d;
                      }), ao: false);
        registerBlock("beetroots", shape: Shape.crop, tex: TexSpec.named("beetroots_stage3"), texFn: (m, _) => tileId($"beetroots_stage{System.Math.Min(3, m & 3)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => (m & 3) >= 3 ? new List<Drop> { new Drop("beetroot"), new Drop("beetroot_seeds", 1, 3 + ctx.fortune) } : new List<Drop> { new Drop("beetroot_seeds") }), ao: false);
        registerBlock("torchflower_crop", shape: Shape.crop, tex: TexSpec.named("torchflower_crop_stage1"), texFn: (m, _) => tileId($"torchflower_crop_stage{System.Math.Min(1, m & 1)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.item("torchflower_seeds"), ao: false);
        registerBlock("melon_stem", shape: Shape.crop, tex: TexSpec.named("stem_stage7"), texFn: (m, _) => tileId((m & 8) != 0 ? "attached_stem" : $"stem_stage{m & 7}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", tint: 1, piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => ctx.random() < 0.4 ? new List<Drop> { new Drop("melon_seeds") } : new List<Drop>()), ao: false);
        registerBlock("pumpkin_stem", shape: Shape.crop, tex: TexSpec.named("stem_stage7"), texFn: (m, _) => tileId((m & 8) != 0 ? "attached_stem" : $"stem_stage{m & 7}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "grass", tint: 1, piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => ctx.random() < 0.4 ? new List<Drop> { new Drop("pumpkin_seeds") } : new List<Drop>()), ao: false);
        registerBlock("melon", tex: texCol("melon_top", "melon_side"), hardness: 1, tool: ToolType.axe, sound: "wood",
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("melon") } : new List<Drop> { new Drop("melon_slice", 3, System.Math.Min(9, 7 + ctx.fortune)) }));
        registerBlock("pumpkin", tex: texCol("pumpkin_top", "pumpkin_side"), hardness: 1, tool: ToolType.axe, sound: "wood");
        registerBlock("carved_pumpkin", tex: tex6("pumpkin_top", "pumpkin_top", "carved_pumpkin", "pumpkin_side", "pumpkin_side", "pumpkin_side"),
                      texFn: (m, f) =>
                      {
                          var face = new[] { 2, 3, 4, 5 }[m & 3];
                          return f == face ? tileId("carved_pumpkin") : (f <= 1 ? tileId("pumpkin_top") : tileId("pumpkin_side"));
                      }, hardness: 1, tool: ToolType.axe, sound: "wood");
        registerBlock("jack_o_lantern", tex: tex6("pumpkin_top", "pumpkin_top", "jack_o_lantern", "pumpkin_side", "pumpkin_side", "pumpkin_side"),
                      texFn: (m, f) =>
                      {
                          var face = new[] { 2, 3, 4, 5 }[m & 3];
                          return f == face ? tileId("jack_o_lantern") : (f <= 1 ? tileId("pumpkin_top") : tileId("pumpkin_side"));
                      }, light: 15, hardness: 1, tool: ToolType.axe, sound: "wood");
        registerBlock("cocoa", shape: Shape.cocoa, tex: TexSpec.named("cocoa_stage2"), texFn: (m, _) => tileId($"cocoa_stage{(m >> 2) & 3}"),
                      opaque: false, fullCube: false, hardness: 0.2, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, _) => new List<Drop> { new Drop("cocoa_beans", ((m >> 2) & 3) >= 2 ? 3 : 1) }));
        registerBlock("nether_wart", shape: Shape.netherWart, tex: TexSpec.named("nether_wart_stage2"), texFn: (m, _) => tileId($"nether_wart_stage{System.Math.Min(2, m & 3)}"),
                      opaque: false, solid: false, fullCube: false, hardness: 0, sound: "wart", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, ctx) => (m & 3) >= 3 ? new List<Drop> { new Drop("nether_wart", 2, 4 + ctx.fortune) } : new List<Drop> { new Drop("nether_wart") }), ao: false);

        registerSnowToEnd();
    }
}

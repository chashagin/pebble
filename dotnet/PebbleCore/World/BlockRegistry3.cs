// Block registrations, part 3: functional blocks, redstone, rails, stone
// families, copper, amethyst, mushroom, misc — then lookup tables.
//
// Extends: static partial class `BlockRegistration`.
// Defines: static class `BlockCaches` (lookup tables + cell helpers).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static partial class BlockRegistration
{
    private static Func<int, int, int> facingFrontTexFn(string front, string top, string side, string bottom = null)
    {
        return (m, f) =>
        {
            var face = new[] { 2, 3, 4, 5 }[m & 3];
            if (f == face) { return tileId(front); }
            if (bottom != null)
            {
                return f == 1 ? tileId(top) : f == 0 ? tileId(bottom) : tileId(side);
            }
            return f <= 1 ? tileId(top) : tileId(side);
        };
    }

    public static void registerFunctionalToEnd()
    {
        // functional blocks
        registerBlock("crafting_table", tex: tex6("oak_planks", "crafting_table_top", "crafting_table_front", "crafting_table_side", "crafting_table_side", "crafting_table_front"), hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("furnace", tex: tex6("furnace_top", "furnace_top", "furnace_front", "furnace_side", "furnace_side", "furnace_side"),
                      texFn: facingFrontTexFn("furnace_front", "furnace_top", "furnace_side"), hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("furnace_lit", tex: tex6("furnace_top", "furnace_top", "furnace_front_lit", "furnace_side", "furnace_side", "furnace_side"),
                      texFn: facingFrontTexFn("furnace_front_lit", "furnace_top", "furnace_side"), light: 13, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity, drops: DropSpec.item("furnace"));
        registerBlock("blast_furnace", tex: tex6("blast_furnace_top", "blast_furnace_top", "blast_furnace_front", "blast_furnace_side", "blast_furnace_side", "blast_furnace_side"),
                      texFn: facingFrontTexFn("blast_furnace_front", "blast_furnace_top", "blast_furnace_side"), hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("blast_furnace_lit", tex: tex6("blast_furnace_top", "blast_furnace_top", "blast_furnace_front_lit", "blast_furnace_side", "blast_furnace_side", "blast_furnace_side"),
                      texFn: facingFrontTexFn("blast_furnace_front_lit", "blast_furnace_top", "blast_furnace_side"), light: 13, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity, drops: DropSpec.item("blast_furnace"));
        registerBlock("smoker", tex: tex6("smoker_bottom", "smoker_top", "smoker_front", "smoker_side", "smoker_side", "smoker_side"),
                      texFn: facingFrontTexFn("smoker_front", "smoker_top", "smoker_side", bottom: "smoker_bottom"), hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("smoker_lit", tex: tex6("smoker_bottom", "smoker_top", "smoker_front_lit", "smoker_side", "smoker_side", "smoker_side"),
                      texFn: facingFrontTexFn("smoker_front_lit", "smoker_top", "smoker_side", bottom: "smoker_bottom"), light: 13, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity, drops: DropSpec.item("smoker"));
        registerBlock("chest", shape: Shape.chest, tex: TexSpec.named("chest_side"), opaque: false, fullCube: false, hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5, piston: PistonBehavior.blockEntity);
        registerBlock("trapped_chest", shape: Shape.chest, tex: TexSpec.named("chest_side"), opaque: false, fullCube: false, hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5, piston: PistonBehavior.blockEntity);
        registerBlock("ender_chest", shape: Shape.chest, tex: TexSpec.named("ender_chest_side"), opaque: false, fullCube: false, light: 7, hardness: 22.5, resistance: 600, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("ender_chest") } : new List<Drop> { new Drop("obsidian", 8) }));
        registerBlock("barrel", tex: texCol("barrel_top", "barrel_side"),
                      texFn: (m, f) =>
                      {
                          var facing = m & 7;
                          var openTop = (m & 8) != 0;
                          if (f == facing) { return tileId(openTop ? "barrel_top_open" : "barrel_top"); }
                          if ((f ^ 1) == facing) { return tileId("barrel_bottom"); }
                          return tileId("barrel_side");
                      }, hardness: 2.5, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.blockEntity);
        registerBlock("bookshelf", tex: texCol("oak_planks", "bookshelf"), hardness: 1.5, tool: ToolType.axe, sound: "wood", flammable: 30, burnOdds: 20, drops: DropSpec.list(new[] { new Drop("book", 3) }));
        registerBlock("chiseled_bookshelf", tex: texCol("chiseled_bookshelf_top", "chiseled_bookshelf_empty"),
                      texFn: facingFrontTexFn("chiseled_bookshelf_occupied", "chiseled_bookshelf_top", "chiseled_bookshelf_side"),
                      hardness: 1.5, tool: ToolType.axe, sound: "wood", flammable: 30, burnOdds: 20, piston: PistonBehavior.blockEntity, drops: DropSpec.none);
        registerBlock("enchanting_table", shape: Shape.enchantTable, tex: tex6("enchanting_table_bottom", "enchanting_table_top", "enchanting_table_side", "enchanting_table_side", "enchanting_table_side", "enchanting_table_side"),
                      opaque: false, fullCube: false, light: 7, hardness: 5, resistance: 1200, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("anvil", shape: Shape.anvil, tex: texCol("anvil_top", "anvil_side"), opaque: false, fullCube: false, hardness: 5, resistance: 1200, tool: ToolType.pickaxe, requiresTool: true, sound: "anvil", piston: PistonBehavior.block, gravity: true);
        registerBlock("chipped_anvil", shape: Shape.anvil, tex: texCol("chipped_anvil_top", "anvil_side"), opaque: false, fullCube: false, hardness: 5, resistance: 1200, tool: ToolType.pickaxe, requiresTool: true, sound: "anvil", piston: PistonBehavior.block, gravity: true);
        registerBlock("damaged_anvil", shape: Shape.anvil, tex: texCol("damaged_anvil_top", "anvil_side"), opaque: false, fullCube: false, hardness: 5, resistance: 1200, tool: ToolType.pickaxe, requiresTool: true, sound: "anvil", piston: PistonBehavior.block, gravity: true);
        registerBlock("grindstone", shape: Shape.grindstone, tex: texCol("grindstone_pivot", "grindstone_side"), opaque: false, fullCube: false, hardness: 2, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.block);
        registerBlock("stonecutter", shape: Shape.stonecutter, tex: tex6("stonecutter_bottom", "stonecutter_top", "stonecutter_side", "stonecutter_side", "stonecutter_side", "stonecutter_side"), opaque: false, fullCube: false, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true);
        registerBlock("smithing_table", tex: tex6("smithing_table_bottom", "smithing_table_top", "smithing_table_front", "smithing_table_front", "smithing_table_side", "smithing_table_side"), hardness: 2.5, tool: ToolType.axe, sound: "wood");
        registerBlock("fletching_table", tex: tex6("fletching_table_top", "fletching_table_top", "fletching_table_front", "fletching_table_front", "fletching_table_side", "fletching_table_side"), hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("cartography_table", tex: tex6("cartography_table_side", "cartography_table_top", "cartography_table_side", "cartography_table_side", "cartography_table_side", "cartography_table_side"), hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("loom", tex: tex6("loom_bottom", "loom_top", "loom_front", "loom_front", "loom_side", "loom_side"), hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("composter", shape: Shape.composter, tex: texTB("composter_top", "composter_bottom", "composter_side"), opaque: false, fullCube: false, hardness: 0.6, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("cauldron", shape: Shape.cauldron, tex: texTB("cauldron_top", "cauldron_bottom", "cauldron_side"), opaque: false, fullCube: false, hardness: 2, tool: ToolType.pickaxe, requiresTool: true, sound: "metal", drops: DropSpec.item("cauldron"));
        registerBlock("brewing_stand", shape: Shape.brewingStand, tex: TexSpec.named("brewing_stand"), opaque: false, fullCube: false, light: 1, hardness: 0.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("jukebox", tex: texCol("jukebox_top", "jukebox_side"), hardness: 2, tool: ToolType.axe, sound: "wood", flammable: 5, piston: PistonBehavior.blockEntity);
        registerBlock("note_block", tex: TexSpec.named("note_block"), hardness: 0.8, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("lectern", shape: Shape.lectern, tex: texTB("lectern_top", "oak_planks", "lectern_side"), opaque: false, fullCube: false, hardness: 2.5, tool: ToolType.axe, sound: "wood", flammable: 5);
        registerBlock("bell", shape: Shape.bell, tex: TexSpec.named("bell_body"), opaque: false, fullCube: false, hardness: 5, tool: ToolType.pickaxe, requiresTool: true, sound: "metal", piston: PistonBehavior.destroy);
        registerBlock("ladder", shape: Shape.ladder, tex: TexSpec.named("ladder"), opaque: false, solid: false, fullCube: false, hardness: 0.4, tool: ToolType.axe, sound: "ladder", piston: PistonBehavior.destroy, climbable: true, ao: false);
        registerBlock("scaffolding", shape: Shape.scaffolding, tex: texCol("scaffolding_top", "scaffolding_side"), opaque: false, solid: false, fullCube: false, hardness: 0, sound: "scaffolding", flammable: 60, piston: PistonBehavior.destroy, climbable: true, ao: false);
        registerBlock("torch", shape: Shape.torch, tex: TexSpec.named("torch"), opaque: false, solid: false, fullCube: false, light: 14, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, emissiveRender: true, ao: false);
        registerBlock("soul_torch", shape: Shape.torch, tex: TexSpec.named("soul_torch"), opaque: false, solid: false, fullCube: false, light: 10, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, emissiveRender: true, ao: false);
        registerBlock("lantern", shape: Shape.lantern, tex: TexSpec.named("lantern"), opaque: false, fullCube: false, light: 15, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, sound: "chain", piston: PistonBehavior.destroy, emissiveRender: true);
        registerBlock("soul_lantern", shape: Shape.lantern, tex: TexSpec.named("soul_lantern"), opaque: false, fullCube: false, light: 10, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, sound: "chain", piston: PistonBehavior.destroy, emissiveRender: true);
        registerBlock("chain", shape: Shape.chain, tex: TexSpec.named("chain"), opaque: false, fullCube: false, hardness: 5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "chain");
        registerBlock("campfire", shape: Shape.campfire, tex: TexSpec.named("campfire_log"), opaque: false, fullCube: false, light: 15, hardness: 2, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.destroy, emissiveRender: true,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("campfire") } : new List<Drop> { new Drop("charcoal", 2) }));
        registerBlock("soul_campfire", shape: Shape.campfire, tex: TexSpec.named("soul_campfire_log"), opaque: false, fullCube: false, light: 10, hardness: 2, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.destroy, emissiveRender: true,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("soul_campfire") } : new List<Drop> { new Drop("soul_soil") }));
        registerBlock("beacon", shape: Shape.beacon, tex: TexSpec.named("beacon"), opaque: false, fullCube: false, light: 15, hardness: 3, sound: "glass", piston: PistonBehavior.blockEntity);
        registerBlock("conduit", shape: Shape.conduit, tex: TexSpec.named("conduit"), opaque: false, fullCube: false, light: 15, hardness: 3, piston: PistonBehavior.blockEntity);
        registerBlock("lodestone", tex: texCol("lodestone_top", "lodestone_side"), hardness: 3.5, resistance: 3.5, tool: ToolType.pickaxe, requiresTool: true);
        registerBlock("respawn_anchor", tex: texCol("respawn_anchor_top", "respawn_anchor_side"),
                      texFn: (m, f) =>
                      {
                          if (f == 1) { return tileId((m & 7) > 0 ? "respawn_anchor_top" : "respawn_anchor_top_off"); }
                          return f == 0 ? tileId("respawn_anchor_bottom") : tileId("respawn_anchor_side");
                      }, hardness: 50, resistance: 1200, tool: ToolType.pickaxe, tier: 3, requiresTool: true, piston: PistonBehavior.block, emissiveRender: true);
        registerBlock("flower_pot", shape: Shape.flowerPot, tex: TexSpec.named("flower_pot"), opaque: false, fullCube: false, hardness: 0, piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, _) => new List<Drop> { new Drop("flower_pot") }));
        registerBlock("decorated_pot", shape: Shape.decoratedPot, tex: TexSpec.named("decorated_pot_side"), opaque: false, fullCube: false, hardness: 0, sound: "decorated_pot", piston: PistonBehavior.destroy, drops: DropSpec.none);
        registerBlock("spawner", opaque: false, lightOpacity: 1, hardness: 5, tool: ToolType.pickaxe, requiresTool: true, sound: "spawner", piston: PistonBehavior.blockEntity, transparentRender: true, drops: DropSpec.none);
        registerBlock("slime_block", opaque: false, lightOpacity: 0, hardness: 0, sound: "slime", translucent: true, cullSame: true);
        registerBlock("honey_block", opaque: false, fullCube: false, lightOpacity: 0, hardness: 0, sound: "honey", translucent: true);
        registerBlock("honeycomb_block", hardness: 0.6, sound: "coral");
        registerBlock("bee_nest", tex: tex6("bee_nest_bottom", "bee_nest_top", "bee_nest_front", "bee_nest_side", "bee_nest_side", "bee_nest_side"), hardness: 0.3, tool: ToolType.axe, sound: "wood", flammable: 30, burnOdds: 20, piston: PistonBehavior.blockEntity, drops: DropSpec.none);
        registerBlock("beehive", tex: tex6("beehive_end", "beehive_end", "beehive_front", "beehive_side", "beehive_side", "beehive_side"), hardness: 0.6, tool: ToolType.axe, sound: "wood", flammable: 30, burnOdds: 20, piston: PistonBehavior.blockEntity, drops: DropSpec.item("beehive"));
        registerBlock("hay_block", tex: texCol("hay_block_top", "hay_block_side"), hardness: 0.5, tool: ToolType.hoe, sound: "grass", flammable: 60, burnOdds: 20);
        registerBlock("target", tex: texCol("target_top", "target_side"), hardness: 0.5, tool: ToolType.hoe, sound: "grass");
        registerBlock("tnt", tex: texTB("tnt_top", "tnt_bottom", "tnt_side"), hardness: 0, sound: "grass", flammable: 15, burnOdds: 100);
        registerBlock("cake", shape: Shape.cake, tex: texTB("cake_top", "cake_bottom", "cake_side"), opaque: false, fullCube: false, hardness: 0.5, sound: "cloth", piston: PistonBehavior.destroy, drops: DropSpec.none);
        registerBlock("dragon_head", shape: Shape.head, tex: TexSpec.named("obsidian"), opaque: false, solid: false, fullCube: false, hardness: 1, piston: PistonBehavior.destroy, drops: DropSpec.item("dragon_head"));
        registerBlock("skeleton_skull", shape: Shape.head, tex: TexSpec.named("bone_block_side"), opaque: false, solid: false, fullCube: false, hardness: 1, piston: PistonBehavior.destroy, drops: DropSpec.item("skeleton_skull"));
        registerBlock("wither_skeleton_skull", shape: Shape.head, tex: TexSpec.named("blackstone"), opaque: false, solid: false, fullCube: false, hardness: 1, piston: PistonBehavior.destroy, drops: DropSpec.item("wither_skeleton_skull"));

        // redstone
        registerBlock("redstone_wire", shape: Shape.redstoneWire, tex: TexSpec.named("redstone_dust_dot"), opaque: false, solid: false, fullCube: false, hardness: 0, piston: PistonBehavior.destroy, drops: DropSpec.item("redstone"), ao: false);
        registerBlock("redstone_torch", shape: Shape.torch, tex: TexSpec.named("redstone_torch"), opaque: false, solid: false, fullCube: false, light: 7, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, emissiveRender: true, ao: false);
        registerBlock("redstone_torch_off", shape: Shape.torch, tex: TexSpec.named("redstone_torch_off"), opaque: false, solid: false, fullCube: false, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, drops: DropSpec.item("redstone_torch"), ao: false);
        registerBlock("repeater", shape: Shape.repeater, tex: TexSpec.named("repeater"), opaque: false, fullCube: false, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, drops: DropSpec.item("repeater"));
        registerBlock("repeater_on", shape: Shape.repeater, tex: TexSpec.named("repeater_on"), opaque: false, fullCube: false, light: 7, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, drops: DropSpec.item("repeater"));
        registerBlock("comparator", shape: Shape.comparator, tex: TexSpec.named("comparator"), opaque: false, fullCube: false, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, drops: DropSpec.item("comparator"));
        registerBlock("comparator_on", shape: Shape.comparator, tex: TexSpec.named("comparator_on"), opaque: false, fullCube: false, light: 7, hardness: 0, sound: "wood", piston: PistonBehavior.destroy, drops: DropSpec.item("comparator"));
        registerBlock("lever", shape: Shape.lever, tex: TexSpec.named("lever"), opaque: false, solid: false, fullCube: false, hardness: 0.5, sound: "wood", piston: PistonBehavior.destroy);
        registerBlock("stone_button", shape: Shape.button, tex: TexSpec.named("stone"), opaque: false, solid: false, fullCube: false, hardness: 0.5, piston: PistonBehavior.destroy);
        registerBlock("polished_blackstone_button", shape: Shape.button, tex: TexSpec.named("polished_blackstone"), opaque: false, solid: false, fullCube: false, hardness: 0.5, piston: PistonBehavior.destroy);
        registerBlock("stone_pressure_plate", shape: Shape.pressurePlate, tex: TexSpec.named("stone"), opaque: false, solid: false, fullCube: false, hardness: 0.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.destroy);
        registerBlock("polished_blackstone_pressure_plate", shape: Shape.pressurePlate, tex: TexSpec.named("polished_blackstone"), opaque: false, solid: false, fullCube: false, hardness: 0.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.destroy);
        registerBlock("light_weighted_pressure_plate", shape: Shape.pressurePlate, tex: TexSpec.named("gold_block"), opaque: false, solid: false, fullCube: false, hardness: 0.5, tool: ToolType.pickaxe, requiresTool: true, sound: "metal", piston: PistonBehavior.destroy);
        registerBlock("heavy_weighted_pressure_plate", shape: Shape.pressurePlate, tex: TexSpec.named("iron_block"), opaque: false, solid: false, fullCube: false, hardness: 0.5, tool: ToolType.pickaxe, requiresTool: true, sound: "metal", piston: PistonBehavior.destroy);
        registerBlock("tripwire_hook", shape: Shape.tripwireHook, tex: TexSpec.named("tripwire_hook"), opaque: false, solid: false, fullCube: false, hardness: 0, sound: "wood", piston: PistonBehavior.destroy);
        registerBlock("tripwire", shape: Shape.tripwire, tex: TexSpec.named("tripwire"), opaque: false, solid: false, fullCube: false, hardness: 0, piston: PistonBehavior.destroy, drops: DropSpec.item("string"), ao: false);
        registerBlock("piston", shape: Shape.piston, tex: texCol("piston_top", "piston_side"), opaque: false, fullCube: false, hardness: 1.5, piston: PistonBehavior.block);
        registerBlock("sticky_piston", shape: Shape.piston, tex: texCol("piston_top_sticky", "piston_side"), opaque: false, fullCube: false, hardness: 1.5, piston: PistonBehavior.block);
        registerBlock("piston_head", shape: Shape.pistonHead, tex: texCol("piston_top", "piston_side"), opaque: false, fullCube: false, hardness: 1.5, piston: PistonBehavior.block, drops: DropSpec.none);
        registerBlock("moving_piston", tex: TexSpec.named("piston_side"), opaque: false, solid: false, fullCube: false, hardness: -1, piston: PistonBehavior.block, drops: DropSpec.none);
        registerBlock("observer", tex: texCol("observer_top", "observer_side"),
                      texFn: (m, f) =>
                      {
                          var facing = m & 7;
                          if (f == facing) { return tileId("observer_front"); }
                          if ((f ^ 1) == facing) { return tileId((m & 8) != 0 ? "observer_back_lit" : "observer_back"); }
                          return (f <= 1 || facing <= 1) ? tileId("observer_top") : tileId("observer_side");
                      }, hardness: 3, tool: ToolType.pickaxe, requiresTool: true);
        registerBlock("dispenser", tex: texCol("furnace_top", "furnace_side"),
                      texFn: (m, f) =>
                      {
                          var facing = m & 7;
                          if (f == facing) { return tileId(facing <= 1 ? "dispenser_front_vertical" : "dispenser_front"); }
                          return f <= 1 ? tileId("furnace_top") : tileId("furnace_side");
                      }, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("dropper", tex: texCol("furnace_top", "furnace_side"),
                      texFn: (m, f) =>
                      {
                          var facing = m & 7;
                          if (f == facing) { return tileId(facing <= 1 ? "dropper_front_vertical" : "dropper_front"); }
                          return f <= 1 ? tileId("furnace_top") : tileId("furnace_side");
                      }, hardness: 3.5, tool: ToolType.pickaxe, requiresTool: true, piston: PistonBehavior.blockEntity);
        registerBlock("hopper", shape: Shape.hopper, tex: texTB("hopper_top", "hopper_outside", "hopper_outside"), opaque: false, fullCube: false, hardness: 3, resistance: 4.8, tool: ToolType.pickaxe, requiresTool: true, sound: "metal", piston: PistonBehavior.blockEntity);
        registerBlock("redstone_lamp", hardness: 0.3, sound: "glass");
        registerBlock("redstone_lamp_on", light: 15, hardness: 0.3, sound: "glass", drops: DropSpec.item("redstone_lamp"));
        registerBlock("daylight_detector", shape: Shape.daylightSensor, tex: texTB("daylight_detector_top", "daylight_detector_side", "daylight_detector_side"), opaque: false, fullCube: false, hardness: 0.2, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.blockEntity);
        registerBlock("daylight_detector_inverted", shape: Shape.daylightSensor, tex: texTB("daylight_detector_inverted_top", "daylight_detector_side", "daylight_detector_side"), opaque: false, fullCube: false, hardness: 0.2, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.blockEntity, drops: DropSpec.item("daylight_detector"));
        registerBlock("sculk_sensor", shape: Shape.daylightSensor, tex: texTB("sculk_sensor_top", "sculk_sensor_bottom", "sculk_sensor_side"), opaque: false, fullCube: false, light: 1, hardness: 1.5, tool: ToolType.hoe, sound: "sculk_sensor", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("sculk_sensor") } : new List<Drop>()));
        registerBlock("calibrated_sculk_sensor", shape: Shape.daylightSensor, tex: texTB("calibrated_sculk_sensor_top", "sculk_sensor_bottom", "calibrated_sculk_sensor_side"), opaque: false, fullCube: false, light: 1, hardness: 1.5, tool: ToolType.hoe, sound: "sculk_sensor", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("calibrated_sculk_sensor") } : new List<Drop>()));
        registerBlock("sculk", hardness: 0.2, tool: ToolType.hoe, sound: "sculk",
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("sculk") } : new List<Drop>()));
        registerBlock("sculk_catalyst", tex: texTB("sculk_catalyst_top", "sculk_catalyst_bottom", "sculk_catalyst_side"), light: 6, hardness: 3, tool: ToolType.hoe, sound: "sculk_catalyst",
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("sculk_catalyst") } : new List<Drop>()));
        registerBlock("sculk_shrieker", shape: Shape.daylightSensor, tex: texTB("sculk_shrieker_top", "sculk_shrieker_bottom", "sculk_shrieker_side"), opaque: false, fullCube: false, hardness: 3, tool: ToolType.hoe, sound: "sculk_shrieker", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("sculk_shrieker") } : new List<Drop>()));

        // rails
        registerBlock("rail", shape: Shape.rail, tex: TexSpec.named("rail"), opaque: false, solid: false, fullCube: false, hardness: 0.7, tool: ToolType.pickaxe, sound: "metal", ao: false);
        registerBlock("powered_rail", shape: Shape.rail, tex: TexSpec.named("powered_rail"), texFn: (m, _) => tileId((m & 8) != 0 ? "powered_rail_on" : "powered_rail"), opaque: false, solid: false, fullCube: false, hardness: 0.7, tool: ToolType.pickaxe, sound: "metal", ao: false);
        registerBlock("detector_rail", shape: Shape.rail, tex: TexSpec.named("detector_rail"), texFn: (m, _) => tileId((m & 8) != 0 ? "detector_rail_on" : "detector_rail"), opaque: false, solid: false, fullCube: false, hardness: 0.7, tool: ToolType.pickaxe, sound: "metal", ao: false);
        registerBlock("activator_rail", shape: Shape.rail, tex: TexSpec.named("activator_rail"), texFn: (m, _) => tileId((m & 8) != 0 ? "activator_rail_on" : "activator_rail"), opaque: false, solid: false, fullCube: false, hardness: 0.7, tool: ToolType.pickaxe, sound: "metal", ao: false);

        registerBlock("iron_door", shape: Shape.door, tex: TexSpec.named("iron_door"), opaque: false, fullCube: false, hardness: 5, resistance: 5, tool: ToolType.pickaxe, requiresTool: true, sound: "metal", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, _) => (m & 8) != 0 ? new List<Drop>() : new List<Drop> { new Drop("iron_door") }));
        registerBlock("iron_trapdoor", shape: Shape.trapdoor, tex: TexSpec.named("iron_trapdoor"), opaque: false, fullCube: false, hardness: 5, resistance: 5, tool: ToolType.pickaxe, requiresTool: true, sound: "metal");

        // stone families (stairs/slabs/walls)
        var NO_WALL = new HashSet<string> { "smooth_stone", "purpur", "quartz", "smooth_quartz", "smooth_sandstone", "smooth_red_sandstone", "cut_sandstone", "cut_red_sandstone" };
        var NO_STAIRS = new HashSet<string> { "cut_sandstone", "cut_red_sandstone" };
        foreach (var (fam, baseTex) in BlockData.STONE_FAMILIES)
        {
            var deep = fam.Contains("deepslate");
            var snd = deep ? "deepslate" : fam.Contains("nether_brick") ? "nether_brick" : fam.Contains("tuff") ? "tuff" : fam.Contains("mud") ? "mud" : "stone";
            var sideTex = blockExists(baseTex) ? (baseTex == "sandstone" ? "sandstone_side" : baseTex == "red_sandstone" ? "red_sandstone_side" : baseTex) : baseTex;
            if (!NO_STAIRS.Contains(fam))
            {
                registerBlock($"{fam}_stairs", shape: Shape.stairs, tex: TexSpec.named(sideTex), opaque: false, fullCube: false, lightOpacity: 0, hardness: 1.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: snd);
            }
            registerBlock($"{fam}_slab", shape: Shape.slab, tex: TexSpec.named(sideTex), opaque: false, fullCube: false, lightOpacity: 0, hardness: 1.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: snd,
                          drops: DropSpec.fn((m, _) => new List<Drop> { new Drop($"{fam}_slab", (m & 3) == 2 ? 2 : 1) }));
            if (!NO_WALL.Contains(fam))
            {
                registerBlock($"{fam}_wall", shape: Shape.wall, tex: TexSpec.named(sideTex), opaque: false, fullCube: false, hardness: 1.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: snd);
            }
        }
        registerBlock("petrified_oak_slab", shape: Shape.slab, tex: TexSpec.named("oak_planks"), opaque: false, fullCube: false, lightOpacity: 0, hardness: 2, resistance: 6, tool: ToolType.pickaxe, requiresTool: true);

        // copper chain
        for (int stage = 0; stage < 4; stage++)
        {
            foreach (var waxed in new[] { "", "waxed_" })
            {
                var p = waxed + BlockData.COPPER_STAGES[stage];
                var baseTexName = BlockData.COPPER_STAGES[stage].Length == 0 ? "copper_block" : $"{BlockData.COPPER_STAGES[stage]}copper";
                var ticks = waxed.Length == 0 && stage < 3;
                registerBlock($"{p}copper_block", tex: TexSpec.named(baseTexName),
                              display: prettify($"{p}copper{(waxed.Length == 0 && stage == 0 ? " block" : "")}"),
                              hardness: 3, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "copper", randomTicks: ticks);
                registerBlock($"{p}cut_copper", tex: TexSpec.named($"{BlockData.COPPER_STAGES[stage]}cut_copper"), hardness: 3, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "copper", randomTicks: ticks);
                registerBlock($"{p}cut_copper_stairs", shape: Shape.stairs, tex: TexSpec.named($"{BlockData.COPPER_STAGES[stage]}cut_copper"), opaque: false, fullCube: false, lightOpacity: 0, hardness: 3, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "copper", randomTicks: ticks);
                registerBlock($"{p}cut_copper_slab", shape: Shape.slab, tex: TexSpec.named($"{BlockData.COPPER_STAGES[stage]}cut_copper"), opaque: false, fullCube: false, lightOpacity: 0, hardness: 3, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "copper", randomTicks: ticks);
            }
        }
        registerBlock("lightning_rod", shape: Shape.torch, tex: TexSpec.named("lightning_rod"), opaque: false, fullCube: false, hardness: 3, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "copper");

        // amethyst / dripstone / lush pieces
        registerBlock("amethyst_cluster", shape: Shape.amethystCluster, tex: TexSpec.named("amethyst_cluster"), opaque: false, solid: false, fullCube: false, light: 5, hardness: 1.5, tool: ToolType.pickaxe, sound: "amethyst", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.toolType == ToolType.pickaxe ? new List<Drop> { new Drop("amethyst_shard", 4 + System.Math.Max(0, ctx.fortune)) } : new List<Drop> { new Drop("amethyst_shard", 2) }));
        registerBlock("large_amethyst_bud", shape: Shape.amethystCluster, tex: TexSpec.named("large_amethyst_bud"), opaque: false, solid: false, fullCube: false, light: 4, hardness: 1.5, tool: ToolType.pickaxe, sound: "amethyst", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("large_amethyst_bud") } : new List<Drop>()));
        registerBlock("medium_amethyst_bud", shape: Shape.amethystCluster, tex: TexSpec.named("medium_amethyst_bud"), opaque: false, solid: false, fullCube: false, light: 2, hardness: 1.5, tool: ToolType.pickaxe, sound: "amethyst", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("medium_amethyst_bud") } : new List<Drop>()));
        registerBlock("small_amethyst_bud", shape: Shape.amethystCluster, tex: TexSpec.named("small_amethyst_bud"), opaque: false, solid: false, fullCube: false, light: 1, hardness: 1.5, tool: ToolType.pickaxe, sound: "amethyst", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("small_amethyst_bud") } : new List<Drop>()));
        registerBlock("pointed_dripstone", shape: Shape.dripstone, tex: TexSpec.named("pointed_dripstone"), opaque: false, solid: false, fullCube: false, hardness: 1.5, resistance: 3, tool: ToolType.pickaxe, sound: "pointed_dripstone", piston: PistonBehavior.destroy, randomTicks: true);
        registerBlock("sniffer_egg", shape: Shape.snifferEgg, tex: TexSpec.named("sniffer_egg"), opaque: false, fullCube: false, hardness: 0.5, sound: "sniffer_egg", piston: PistonBehavior.destroy, randomTicks: true);
        registerBlock("turtle_egg", shape: Shape.turtleEgg, tex: TexSpec.named("turtle_egg"), opaque: false, fullCube: false, hardness: 0.5, piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.none);
        registerBlock("frogspawn", shape: Shape.frogspawn, tex: TexSpec.named("frogspawn"), opaque: false, solid: false, fullCube: false, hardness: 0, sound: "frogspawn", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.none, ao: false);
        registerBlock("ochre_froglight", tex: texCol("ochre_froglight_top", "ochre_froglight_side"), light: 15, hardness: 0.3, sound: "froglight");
        registerBlock("verdant_froglight", tex: texCol("verdant_froglight_top", "verdant_froglight_side"), light: 15, hardness: 0.3, sound: "froglight");
        registerBlock("pearlescent_froglight", tex: texCol("pearlescent_froglight_top", "pearlescent_froglight_side"), light: 15, hardness: 0.3, sound: "froglight");

        // mushroom blocks
        registerBlock("brown_mushroom_block", hardness: 0.2, tool: ToolType.axe, sound: "wood",
                      drops: DropSpec.fn((m, ctx) =>
                      {
                          var r = ctx.random();
                          return r < 0.25 ? new List<Drop> { new Drop("brown_mushroom", r < 0.125 ? 1 : 2) } : new List<Drop>();
                      }));
        registerBlock("red_mushroom_block", hardness: 0.2, tool: ToolType.axe, sound: "wood",
                      drops: DropSpec.fn((m, ctx) =>
                      {
                          var r = ctx.random();
                          return r < 0.25 ? new List<Drop> { new Drop("red_mushroom", r < 0.125 ? 1 : 2) } : new List<Drop>();
                      }));
        registerBlock("mushroom_stem", hardness: 0.2, tool: ToolType.axe, sound: "wood", drops: DropSpec.none);

        // misc terrain
        registerBlock("cobweb", shape: Shape.web, tex: TexSpec.named("cobweb"), opaque: false, solid: false, fullCube: false, hardness: 4, tool: ToolType.sword, sound: "cloth", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => (ctx.shears || ctx.silkTouch) ? new List<Drop> { new Drop("cobweb") } : new List<Drop> { new Drop("string") }), ao: false);
        registerBlock("fire", shape: Shape.fire, tex: TexSpec.named("fire"), opaque: false, solid: false, fullCube: false, replaceable: true, light: 15, hardness: 0, sound: "cloth", piston: PistonBehavior.destroy, randomTicks: true, emissiveRender: true, drops: DropSpec.none, ao: false);
        registerBlock("soul_fire", shape: Shape.fire, tex: TexSpec.named("soul_fire"), opaque: false, solid: false, fullCube: false, replaceable: true, light: 10, hardness: 0, sound: "cloth", piston: PistonBehavior.destroy, emissiveRender: true, drops: DropSpec.none, ao: false);
        stone2("infested_stone", 0.75, tex: TexSpec.named("stone"), display: "Stone", drops: DropSpec.none);
        stone2("infested_cobblestone", 1, tex: TexSpec.named("cobblestone"), display: "Cobblestone", drops: DropSpec.none);
        stone2("infested_stone_bricks", 0.75, tex: TexSpec.named("stone_bricks"), display: "Stone Bricks", drops: DropSpec.none);
        registerBlock("infested_deepslate", tex: texCol("deepslate_top", "deepslate"), display: "Deepslate", hardness: 1.5, tool: ToolType.pickaxe, sound: "deepslate", drops: DropSpec.none);

        finalizeBlockRegistry();
    }

    public static void finalizeBlockRegistry()
    {
        foreach (var d in blockDefs)
        {
            BlockCaches.OPAQUE[d.id] = (byte)(d.opaque ? 1 : 0);
            BlockCaches.FULL_CUBE[d.id] = (byte)(d.fullCube ? 1 : 0);
            BlockCaches.SOLID[d.id] = (byte)(d.solid ? 1 : 0);
            BlockCaches.LIGHT_EMIT[d.id] = (byte)d.lightEmit;
            BlockCaches.LIGHT_OPACITY[d.id] = (byte)d.lightOpacity;
            BlockCaches.REPLACEABLE[d.id] = (byte)(d.replaceable ? 1 : 0);
            BlockCaches.SHAPE_OF[d.id] = (byte)d.shape;
            BlockCaches.TINT_OF[d.id] = (byte)d.tint;
            BlockCaches.TRANSLUCENT[d.id] = (byte)(d.translucent ? 1 : 0);
            BlockCaches.TRANSPARENT_RENDER[d.id] = (byte)(d.transparentRender ? 1 : 0);
            BlockCaches.CULL_SAME[d.id] = (byte)(d.cullSame ? 1 : 0);
            BlockCaches.HAS_GRAVITY[d.id] = (byte)(d.gravity ? 1 : 0);
            BlockCaches.CLIMBABLE[d.id] = (byte)(d.climbable ? 1 : 0);
            BlockCaches.RANDOM_TICKS[d.id] = (byte)(d.randomTicks ? 1 : 0);
            BlockCaches.AO_OF[d.id] = (byte)(d.ao ? 1 : 0);
            BlockCaches.EMISSIVE[d.id] = (byte)(d.emissiveRender ? 1 : 0);
        }
        // water-filled aquatic plants
        foreach (var n in new[] { "seagrass", "tall_seagrass", "kelp", "kelp_plant", "sea_pickle" }) { BlockCaches.waterFilled.Add(bid(n)); }
        foreach (var c in BlockData.CORALS)
        {
            BlockCaches.waterFilled.Add(bid($"{c}_coral"));
            BlockCaches.waterFilled.Add(bid($"{c}_coral_fan"));
        }
        BlockCaches.CANDLE_IDS.Add(bid("candle"));
        foreach (var c in BlockData.COLORS) { BlockCaches.CANDLE_IDS.Add(bid($"{c}_candle")); }
        // eagerly exercise every texFn so tile order is deterministic before atlas build
        foreach (var d in blockDefs)
        {
            if (d.texFn != null)
            {
                for (int m = 0; m < 16; m++)
                {
                    for (int f = 0; f < 6; f++) { _ = d.texFn(m, f); }
                }
            }
        }
        foreach (var extra in new[]
        {
            "redstone_dust_line", "campfire_fire", "soul_campfire_fire",
            "destroy_0", "destroy_1", "destroy_2", "destroy_3", "destroy_4",
            "destroy_5", "destroy_6", "destroy_7", "destroy_8", "destroy_9",
            "smoke_particle", "flame_particle", "portal_particle", "crit_particle",
            "heart_particle", "angry_particle", "splash_particle", "bubble_particle",
            "snow_particle", "petal_particle", "note_particle", "redstone_particle",
            "soul_particle", "enchant_particle", "slime_particle", "sweep_particle",
        }) { tileId(extra); }

        // freeze every B.<name> into stored fields — direct loads on all hot paths
        populateBlockIDs();

        // prebaked cell→tile table + leaves flags: the mesher resolved these via
        // blockDefs[id] struct copies (8 refcounted fields) per visible cell face,
        // which dominated section meshing
        BlockCaches.TILE_TABLE = new int[65536 * 8];
        BlockCaches.IS_LEAVES = new byte[4096];
        foreach (var d in blockDefs)
        {
            if (d.name.Contains("leaves")) { BlockCaches.IS_LEAVES[d.id] = 1; }
            for (int m = 0; m < 16; m++)
            {
                var cellV = (d.id << 4) | m;
                for (int f = 0; f < 6; f++)
                {
                    var tile = d.texFn != null ? d.texFn(m, f) : (d.tex.Length == 0 ? 0 : (int)d.tex[f]);
                    BlockCaches.TILE_TABLE[(cellV << 3) | f] = (int)tile;
                }
            }
        }

        // bed faces get dedicated blanket/frame tiles — allocated HERE, after the
        // frozen baseline tile range, so the original 757 tile IDs stay stable
        foreach (var c in BlockData.COLORS)
        {
            var bedId = (int)bid($"{c}_bed");
            var top = (int)tileId($"{c}_bed_top");
            var side = (int)tileId($"{c}_bed_side");
            var bottom = (int)tileId("oak_planks");
            for (int m = 0; m < 16; m++)
            {
                var cellV = (bedId << 4) | m;
                BlockCaches.TILE_TABLE[(cellV << 3) | 0] = bottom;
                BlockCaches.TILE_TABLE[(cellV << 3) | 1] = top;
                for (int f = 2; f < 6; f++) { BlockCaches.TILE_TABLE[(cellV << 3) | f] = side; }
            }
        }
    }
}

// MARK: - lookup tables + cell helpers
//
// Defines: static class `BlockCaches` (the lookup-table arrays + the cell helper
// free functions). The orchestrator should add `global using static PebbleCore.BlockCaches;`
// so the cell helpers (cell, cellId, cellMeta, defOf, isAir, ...) resolve unqualified.

public static class BlockCaches
{
    public static byte[] OPAQUE = new byte[4096];
    public static byte[] FULL_CUBE = new byte[4096];
    public static byte[] SOLID = new byte[4096];
    public static byte[] LIGHT_EMIT = new byte[4096];
    public static byte[] LIGHT_OPACITY = new byte[4096];
    public static byte[] REPLACEABLE = new byte[4096];
    public static byte[] SHAPE_OF = new byte[4096];
    public static byte[] TINT_OF = new byte[4096];
    public static byte[] TRANSLUCENT = new byte[4096];
    public static byte[] TRANSPARENT_RENDER = new byte[4096];
    public static byte[] CULL_SAME = new byte[4096];
    public static byte[] HAS_GRAVITY = new byte[4096];
    public static byte[] CLIMBABLE = new byte[4096];
    public static byte[] RANDOM_TICKS = new byte[4096];
    public static byte[] AO_OF = new byte[4096];
    public static byte[] EMISSIVE = new byte[4096];

    internal static HashSet<ushort> waterFilled = new();
    public static HashSet<ushort> CANDLE_IDS = new();

    /// tile for (cell, face), index = (cell << 3) | face — covers texFn + tex
    public static int[] TILE_TABLE = new int[0];
    public static byte[] IS_LEAVES = new byte[0];

    // MARK: - cells

    public static ushort cell(ushort id, int meta = 0) => (ushort)((id << 4) | (ushort)(meta & 15));
    public static int cellId(ushort c) => c >> 4;
    public static int cellMeta(ushort c) => c & 15;
    public static BlockDef defOf(ushort c) => blockDefs[c >> 4];

    public static bool isAir(ushort c)
    {
        var id = c >> 4;
        return id == B.air || id == B.cave_air || id == B.void_air;
    }
    public static bool isWaterCell(ushort c) => (c >> 4) == B.water;
    public static bool isLavaCell(ushort c) => (c >> 4) == B.lava;
    public static bool isLiquid(ushort c)
    {
        var id = c >> 4;
        return id == B.water || id == B.lava;
    }
    public static bool isWaterlogged(ushort c)
        => (c >> 4) == B.water || waterFilled.Contains((ushort)(c >> 4));

    /// meta-dependent light emission
    public static int lightEmitOf(ushort c)
    {
        var id = c >> 4;
        var m = c & 15;
        if (id == B.respawn_anchor)
        {
            var charges = m & 7;
            return charges > 0 ? charges * 4 - 1 : 0;
        }
        if (id == B.campfire || id == B.soul_campfire) { return (m & 4) != 0 ? (int)LIGHT_EMIT[(int)id] : 0; }
        if (id == B.cave_vines || id == B.cave_vines_plant) { return (m & 8) != 0 ? 14 : 0; }
        if (id == B.sea_pickle) { return 6 + 3 * (m & 3); }
        if (CANDLE_IDS.Contains((ushort)id)) { return (m & 8) != 0 ? 3 * ((m & 3) + 1) : 0; }
        if (id == B.redstone_ore || id == B.deepslate_redstone_ore) { return (m & 1) != 0 ? 9 : 0; }
        return (int)LIGHT_EMIT[(int)id];
    }
}

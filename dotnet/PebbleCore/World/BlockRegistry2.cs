// Block registrations, part 2: snow/ice through the lookup tables.
// Order continues from BlockRegistry.cs — order is frozen — ids persist in saves.
//
// Extends: static partial class `BlockRegistration`.

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static partial class BlockRegistration
{
    public static void registerSnowToEnd()
    {
        // snow / ice
        registerBlock("snow", shape: Shape.layer, opaque: false, fullCube: false, replaceable: true, hardness: 0.1, tool: ToolType.shovel, requiresTool: true, sound: "snow", piston: PistonBehavior.destroy, randomTicks: true,
                      drops: DropSpec.fn((m, _) => new List<Drop> { new Drop("snowball", (m & 7) + 1) }));
        registerBlock("snow_block", hardness: 0.2, tool: ToolType.shovel, requiresTool: true, sound: "snow", drops: DropSpec.list(new[] { new Drop("snowball", 4) }));
        registerBlock("powder_snow", opaque: false, solid: false, fullCube: true, lightOpacity: 1, hardness: 0.25, sound: "powder_snow", transparentRender: false, drops: DropSpec.none);
        registerBlock("ice", opaque: false, lightOpacity: 1, hardness: 0.5, tool: ToolType.pickaxe, sound: "glass", randomTicks: true, translucent: true, cullSame: true, drops: DropSpec.none);
        registerBlock("packed_ice", hardness: 0.5, tool: ToolType.pickaxe, sound: "glass", drops: DropSpec.none);
        registerBlock("blue_ice", hardness: 2.8, tool: ToolType.pickaxe, sound: "glass", drops: DropSpec.none);
        registerBlock("frosted_ice", opaque: false, lightOpacity: 1, hardness: 0.5, sound: "glass", randomTicks: true, translucent: true, cullSame: true, drops: DropSpec.none);

        // ocean
        stone2("prismarine", 1.5, resistance: 6);
        stone2("prismarine_bricks", 1.5, resistance: 6);
        stone2("dark_prismarine", 1.5, resistance: 6);
        registerBlock("sea_lantern", light: 15, hardness: 0.3, sound: "glass",
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("sea_lantern") } : new List<Drop> { new Drop("prismarine_crystals", 2, System.Math.Min(5, 3 + ctx.fortune)) }));
        registerBlock("sponge", hardness: 0.6, tool: ToolType.hoe, sound: "grass");
        registerBlock("wet_sponge", hardness: 0.6, tool: ToolType.hoe, sound: "grass");
        registerBlock("dried_kelp_block", hardness: 0.5, tool: ToolType.hoe, sound: "grass", flammable: 30, burnOdds: 60);
        foreach (var c in BlockData.CORALS)
        {
            registerBlock($"{c}_coral_block", hardness: 1.5, tool: ToolType.pickaxe, requiresTool: true, sound: "coral", randomTicks: true,
                          drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop($"{c}_coral_block") } : new List<Drop> { new Drop($"dead_{c}_coral_block") }));
            registerBlock($"dead_{c}_coral_block", hardness: 1.5, tool: ToolType.pickaxe, requiresTool: true, sound: "coral");
            registerBlock($"{c}_coral", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "coral", piston: PistonBehavior.destroy,
                          drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop($"{c}_coral") } : new List<Drop>()), ao: false);
            registerBlock($"dead_{c}_coral", shape: Shape.cross, opaque: false, solid: false, fullCube: false, hardness: 0, sound: "coral", piston: PistonBehavior.destroy,
                          drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop($"dead_{c}_coral") } : new List<Drop>()), ao: false);
            registerBlock($"{c}_coral_fan", shape: Shape.lilyPad, tex: TexSpec.named($"{c}_coral_fan"), opaque: false, solid: false, fullCube: false, hardness: 0, sound: "coral", piston: PistonBehavior.destroy,
                          drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop($"{c}_coral_fan") } : new List<Drop>()), ao: false);
        }

        // nether
        registerBlock("netherrack", hardness: 0.4, tool: ToolType.pickaxe, requiresTool: true, sound: "netherrack");
        registerBlock("crimson_nylium", tex: texTB("crimson_nylium", "netherrack", "crimson_nylium_side"), hardness: 0.4, tool: ToolType.pickaxe, requiresTool: true, sound: "nylium", randomTicks: true, drops: DropSpec.item("netherrack"));
        registerBlock("warped_nylium", tex: texTB("warped_nylium", "netherrack", "warped_nylium_side"), hardness: 0.4, tool: ToolType.pickaxe, requiresTool: true, sound: "nylium", randomTicks: true, drops: DropSpec.item("netherrack"));
        registerBlock("soul_sand", fullCube: false, hardness: 0.5, tool: ToolType.shovel, sound: "soulsand");
        registerBlock("soul_soil", hardness: 0.5, tool: ToolType.shovel, sound: "soulsand");
        registerBlock("magma_block", light: 3, hardness: 0.5, tool: ToolType.pickaxe, requiresTool: true, randomTicks: true, emissiveRender: true);
        registerBlock("glowstone", light: 15, hardness: 0.3, sound: "glass",
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch ? new List<Drop> { new Drop("glowstone") } : new List<Drop> { new Drop("glowstone_dust", 2, 4) }));
        registerBlock("shroomlight", light: 15, hardness: 1, tool: ToolType.hoe, sound: "shroomlight");
        registerBlock("nether_wart_block", hardness: 1, tool: ToolType.hoe, sound: "wart");
        registerBlock("warped_wart_block", hardness: 1, tool: ToolType.hoe, sound: "wart");
        registerBlock("basalt", tex: texCol("basalt_top", "basalt_side"), hardness: 1.25, resistance: 4.2, tool: ToolType.pickaxe, requiresTool: true, sound: "basalt");
        registerBlock("polished_basalt", tex: texCol("polished_basalt_top", "polished_basalt_side"), hardness: 1.25, resistance: 4.2, tool: ToolType.pickaxe, requiresTool: true, sound: "basalt");
        registerBlock("smooth_basalt", hardness: 1.25, resistance: 4.2, tool: ToolType.pickaxe, requiresTool: true, sound: "basalt");
        stone2("blackstone", 1.5, resistance: 6, tex: texCol("blackstone_top", "blackstone"));
        stone2("polished_blackstone", 2, resistance: 6);
        stone2("polished_blackstone_bricks", 1.5, resistance: 6);
        stone2("cracked_polished_blackstone_bricks", 1.5, resistance: 6);
        stone2("chiseled_polished_blackstone", 1.5, resistance: 6);
        registerBlock("gilded_blackstone", hardness: 1.5, resistance: 6, tool: ToolType.pickaxe, requiresTool: true,
                      drops: DropSpec.fn((m, ctx) => ctx.silkTouch || ctx.random() >= 0.1 + (double)ctx.fortune * 0.05 ? new List<Drop> { new Drop("gilded_blackstone") } : new List<Drop> { new Drop("gold_nugget", 2, 5) }));
        stone2("nether_bricks", 2, resistance: 6, sound: "nether_brick");
        registerBlock("nether_brick_fence", shape: Shape.fence, tex: TexSpec.named("nether_bricks"), opaque: false, fullCube: false, hardness: 2, resistance: 6, tool: ToolType.pickaxe, requiresTool: true, sound: "nether_brick");
        stone2("cracked_nether_bricks", 2, resistance: 6, sound: "nether_brick");
        stone2("chiseled_nether_bricks", 2, resistance: 6, sound: "nether_brick");
        stone2("red_nether_bricks", 2, resistance: 6, sound: "nether_brick");
        registerBlock("bone_block", tex: texCol("bone_block_top", "bone_block_side"), hardness: 2, tool: ToolType.pickaxe, requiresTool: true, sound: "bone");

        // end
        stone2("end_stone", 3, resistance: 9);
        stone2("end_stone_bricks", 3, resistance: 9);
        stone2("purpur_block", 1.5, resistance: 6);
        stone2("purpur_pillar", 1.5, resistance: 6, tex: texCol("purpur_pillar_top", "purpur_pillar"));
        registerBlock("end_rod", shape: Shape.torch, tex: TexSpec.named("end_rod"), opaque: false, solid: false, fullCube: false, light: 14, hardness: 0, sound: "wood");
        registerBlock("chorus_plant", shape: Shape.chorus, tex: TexSpec.named("chorus_plant"), opaque: false, fullCube: false, hardness: 0.4, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.destroy,
                      drops: DropSpec.fn((m, ctx) => ctx.random() < 0.5 ? new List<Drop> { new Drop("chorus_fruit") } : new List<Drop>()));
        registerBlock("chorus_flower", shape: Shape.chorusFlower, tex: TexSpec.named("chorus_flower"), texFn: (m, _) => tileId((m & 7) >= 5 ? "chorus_flower_dead" : "chorus_flower"),
                      opaque: false, fullCube: false, hardness: 0.4, tool: ToolType.axe, sound: "wood", piston: PistonBehavior.destroy, randomTicks: true, drops: DropSpec.none);
        registerBlock("dragon_egg", shape: Shape.dragonEgg, tex: TexSpec.named("dragon_egg"), opaque: false, fullCube: false, light: 1, hardness: 3, resistance: 9, piston: PistonBehavior.destroy, gravity: true);
        registerBlock("end_portal_frame", shape: Shape.endPortalFrame, tex: texTB("end_portal_frame_top", "end_stone", "end_portal_frame_side"), opaque: false, fullCube: false, light: 1, hardness: -1, resistance: 3_600_000, drops: DropSpec.none);
        registerBlock("end_portal", shape: Shape.endPortalShape, tex: TexSpec.named("end_portal"), opaque: false, solid: false, fullCube: false, light: 15, hardness: -1, resistance: 3_600_000, drops: DropSpec.none, ao: false);
        registerBlock("end_gateway", shape: Shape.endPortalShape, tex: TexSpec.named("end_portal"), opaque: false, solid: false, fullCube: false, light: 15, hardness: -1, resistance: 3_600_000, drops: DropSpec.none, ao: false);
        registerBlock("nether_portal", shape: Shape.portalShape, tex: TexSpec.named("nether_portal"), opaque: false, solid: false, fullCube: false, light: 11, hardness: -1, sound: "glass", translucent: true, emissiveRender: true, drops: DropSpec.none, ao: false);

        registerFunctionalToEnd();
    }

    // stone shorthand re-declared here (fileprivate in the other file)
    public static ushort stone2(string name, double hardness, double? resistance = null, int tier = 0,
                                int light = 0, string sound = "stone", TexSpec tex = null,
                                string display = null, DropSpec drops = null)
    {
        return registerBlock(name, tex: tex, display: display, light: light,
                             hardness: hardness, resistance: resistance ?? hardness * 2,
                             tool: ToolType.pickaxe, tier: tier, requiresTool: true, sound: sound, drops: drops);
    }
}

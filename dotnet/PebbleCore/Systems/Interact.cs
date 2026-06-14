// Interactions — block placement with
// orientation, right-click block uses, item uses (buckets, food, throwables,
// tools-on-blocks), eating, block breaking with drops & XP.
//
// Ported from Sources/PebbleCore/Systems/Interact.swift.
//
// Defines static class `InteractGlobals` (the module-level free functions:
// useBlock / useItem / placeBlock / finishUsingItem / releaseUsingItem /
// finishBreaking / applyBonemeal / resolveLoot / registerAllSystems, plus the
// private module helpers and `interactRng`). Surface it via
//   global using static PebbleCore.InteractGlobals;
// Types ScreenData / InteractCtx live at namespace level.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

/// payload passed to the UI layer's openScreen
public struct ScreenData
{
    public BlockEntityData be;
    public BlockEntityData other;
    public string title;
    public int? block;
    public int x, y, z;
    public int damage;
    public string text;
    public ScreenData() { }
}

public struct InteractCtx
{
    public readonly World world;
    public readonly Player player;
    public readonly Action<string, ScreenData?> openScreen;
    public readonly Action<string> advance;

    public InteractCtx(World world, Player player,
                       Action<string, ScreenData?> openScreen = null,
                       Action<string> advance = null)
    {
        this.world = world;
        this.player = player;
        this.openScreen = openScreen ?? ((_, _) => { });
        this.advance = advance ?? (_ => { });
    }
}

public static class InteractGlobals
{
    public static RandomX interactRng = new RandomX(0x17AC7);

    private static int dirFacingMeta(Player player)
    {
        // horizontal facing meta (0=N 1=S 2=W 3=E) — direction the PLAYER faces
        int d = yawToDir(player.yaw * 180 / Math.PI);
        return new[] { 0, 0, 0, 1, 2, 3 }[d];
    }
    private static int dirFacingMetaOpp(Player player)
    {
        return new[] { 1, 0, 3, 2 }[dirFacingMeta(player)];
    }

    private static Shape shapeOf(int id)
    {
        // Swift: Shape(rawValue: SHAPE_OF[id]) ?? .cube — SHAPE_OF holds only
        // valid raw values (cast from BlockDef.shape), so the cast never falls back.
        return (Shape)SHAPE_OF[id];
    }

    // =============================================================================
    // BLOCK USE (right-click on block)
    // =============================================================================
    public static bool useBlock(InteractCtx ctx, RaycastHit hit)
    {
        World world = ctx.world; Player player = ctx.player;
        int x = hit.x, y = hit.y, z = hit.z;
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        int meta = c & 15;
        var def = blockDefs[id];
        Shape shape = shapeOf(id);
        string name = def.name;

        // doors / trapdoors / gates
        if (shape == Shape.door && id != (int)B.iron_door)
        {
            int lowerY = (meta & 8) != 0 ? y - 1 : y;
            int lower = world.getBlock(x, lowerY, z);
            world.setBlock(x, lowerY, z, (int)cell((ushort)id, (lower & 15) ^ 4));
            world.hooks.playSound((lower & 4) != 0 ? "block.wooden_door.close" : "block.wooden_door.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
            world.emitVibration((double)x, (double)y, (double)z, 11, player);
            return true;
        }
        if (shape == Shape.trapdoor && id != (int)B.iron_trapdoor)
        {
            world.setBlock(x, y, z, (int)cell((ushort)id, meta ^ 4));
            world.hooks.playSound((meta & 4) != 0 ? "block.wooden_trapdoor.close" : "block.wooden_trapdoor.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
            return true;
        }
        if (shape == Shape.fenceGate)
        {
            int m = meta ^ 4;
            if ((m & 4) != 0)
            {
                // open away from player
                int f = dirFacingMeta(player);
                m = (m & 12) | f;
                if ((meta & 3) == ((f + 2) % 4)) { m = (m & 12) | f; }
            }
            world.setBlock(x, y, z, (int)cell((ushort)id, m));
            world.hooks.playSound((meta & 4) != 0 ? "block.fence_gate.close" : "block.fence_gate.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
            return true;
        }
        // lever / button
        if (id == (int)B.lever)
        {
            world.setBlock(x, y, z, (int)cell(B.lever, meta ^ 8));
            world.hooks.playSound("block.lever.click", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.5, (meta & 8) != 0 ? 0.6 : 0.7);
            int attach = meta & 7;
            world.updateNeighbors(x + DIR_X[attach], y + DIR_Y[attach], z + DIR_Z[attach]);
            world.emitVibration((double)x, (double)y, (double)z, 10, player);
            return true;
        }
        if (shape == Shape.button)
        {
            if ((meta & 8) == 0)
            {
                world.setBlock(x, y, z, (int)cell((ushort)id, meta | 8));
                world.hooks.playSound("block.stone_button.click_on", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.5, 0.9);
                int attach = meta & 7;
                world.updateNeighbors(x + DIR_X[attach], y + DIR_Y[attach], z + DIR_Z[attach]);
                world.scheduleTick(x, y, z, id, def.sound == "wood" ? 30 : 20);
            }
            return true;
        }
        // repeater / comparator adjust
        if (id == (int)B.repeater || id == (int)B.repeater_on)
        {
            int delay = ((meta >> 2) & 3) + 1;
            int next = delay % 4;
            world.setBlock(x, y, z, (int)cell((ushort)id, (meta & 3) | (next << 2)), 4);
            world.hooks.playSound("block.lever.click", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.3, 1);
            return true;
        }
        if (id == (int)B.comparator || id == (int)B.comparator_on)
        {
            world.setBlock(x, y, z, (int)cell((ushort)id, meta ^ 4), 4);
            world.scheduleTick(x, y, z, id, 2);
            world.hooks.playSound("block.comparator.click", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.3, (meta & 4) != 0 ? 0.55 : 0.5);
            return true;
        }
        if (id == (int)B.note_block)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "note")
            {
                be = new BlockEntityData("note", x, y, z);
                be.note = 0;
                world.setBlockEntity(be);
            }
            be.note = ((be.note ?? 0) + 1) % 25;
            playNoteBlock(world, x, y, z);
            return true;
        }
        if (id == (int)B.daylight_detector || id == (int)B.daylight_detector_inverted)
        {
            ushort other = id == (int)B.daylight_detector ? B.daylight_detector_inverted : B.daylight_detector;
            world.setBlock(x, y, z, (int)cell(other, 0));
            world.scheduleTick(x, y, z, (int)other, 2);
            return true;
        }
        // containers & screens
        if (id == (int)B.crafting_table) { ctx.openScreen("crafting", null); return true; }
        if (id == (int)B.chest || id == (int)B.trapped_chest)
        {
            // blocked by solid above?
            if (blockDefs[world.getBlock(x, y + 1, z) >> 4].opaque) { return true; }
            var be = world.getBlockEntity(x, y, z);
            if (be == null) { be = makeContainerBE(x, y, z, 27); world.setBlockEntity(be); }
            resolveLoot(world, be);
            // double chest
            BlockEntityData other = null;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if ((world.getBlock(x + dx, y, z + dz) >> 4) == id)
                {
                    var obe = world.getBlockEntity(x + dx, y, z + dz);
                    if (obe == null) { obe = makeContainerBE(x + dx, y, z + dz, 27); world.setBlockEntity(obe); }
                    resolveLoot(world, obe);
                    other = obe;
                    break;
                }
            }
            var data = new ScreenData();
            data.be = be;
            data.other = other;
            data.title = other != null ? "Large Chest" : "Chest";
            data.block = id;
            ctx.openScreen("chest", data);
            world.hooks.playSound("block.chest.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.6, 1);
            return true;
        }
        if (id == (int)B.barrel)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null) { be = makeContainerBE(x, y, z, 27); world.setBlockEntity(be); }
            resolveLoot(world, be);
            var data = new ScreenData();
            data.be = be;
            data.title = "Barrel";
            data.block = id;
            ctx.openScreen("chest", data);
            world.hooks.playSound("block.barrel.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.6, 1);
            return true;
        }
        if (id == (int)B.ender_chest)
        {
            ctx.openScreen("ender_chest", null);
            world.hooks.playSound("block.ender_chest.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.6, 1);
            return true;
        }
        if (name.EndsWith("shulker_box") || id == (int)B.shulker_box)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null) { be = makeContainerBE(x, y, z, 27); world.setBlockEntity(be); }
            var data = new ScreenData();
            data.be = be;
            data.title = "Shulker Box";
            data.block = id;
            ctx.openScreen("chest", data);
            world.hooks.playSound("block.shulker_box.open", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.6, 1);
            return true;
        }
        if (id == (int)B.furnace || id == (int)B.furnace_lit || id == (int)B.blast_furnace || id == (int)B.blast_furnace_lit || id == (int)B.smoker || id == (int)B.smoker_lit)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "furnace")
            {
                string kind = (id == (int)B.blast_furnace || id == (int)B.blast_furnace_lit) ? "blast" : (id == (int)B.smoker || id == (int)B.smoker_lit) ? "smoker" : "furnace";
                be = makeFurnaceBE(x, y, z, kind);
                world.setBlockEntity(be);
            }
            var data = new ScreenData();
            data.be = be;
            ctx.openScreen("furnace", data);
            return true;
        }
        if (id == (int)B.brewing_stand)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "brewing") { be = makeBrewingBE(x, y, z); world.setBlockEntity(be); }
            var data = new ScreenData();
            data.be = be;
            ctx.openScreen("brewing", data);
            return true;
        }
        if (id == (int)B.enchanting_table)
        {
            var data = new ScreenData();
            data.x = x; data.y = y; data.z = z;
            ctx.openScreen("enchanting", data);
            return true;
        }
        if (id == (int)B.anvil || id == (int)B.chipped_anvil || id == (int)B.damaged_anvil)
        {
            var data = new ScreenData();
            data.x = x; data.y = y; data.z = z;
            data.damage = id == (int)B.anvil ? 0 : id == (int)B.chipped_anvil ? 1 : 2;
            ctx.openScreen("anvil", data);
            return true;
        }
        if (id == (int)B.grindstone) { ctx.openScreen("grindstone", null); return true; }
        if (id == (int)B.stonecutter) { ctx.openScreen("stonecutter", null); return true; }
        if (id == (int)B.smithing_table) { ctx.openScreen("smithing", null); return true; }
        if (id == (int)B.loom || id == (int)B.cartography_table || id == (int)B.fletching_table)
        {
            return true; // villager job sites; no player UI
        }
        if (id == (int)B.beacon)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "beacon")
            {
                be = new BlockEntityData("beacon", x, y, z);
                be.levels = 0;
                world.setBlockEntity(be);
            }
            var data = new ScreenData();
            data.be = be;
            ctx.openScreen("beacon", data);
            return true;
        }
        if (id == (int)B.hopper)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "hopper") { be = makeHopperBE(x, y, z); world.setBlockEntity(be); }
            var data = new ScreenData();
            data.be = be;
            data.title = "Hopper";
            data.block = id;
            ctx.openScreen("chest", data);
            return true;
        }
        if (id == (int)B.dispenser || id == (int)B.dropper)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "container") { be = makeContainerBE(x, y, z, 9); world.setBlockEntity(be); }
            var data = new ScreenData();
            data.be = be;
            data.title = id == (int)B.dispenser ? "Dispenser" : "Dropper";
            data.block = id;
            ctx.openScreen("chest", data);
            return true;
        }
        // beds
        if (shape == Shape.bed)
        {
            return useBed(ctx, x, y, z, c);
        }
        if (id == (int)B.respawn_anchor)
        {
            int charges = meta & 7;
            var held = player.mainHand;
            if (held != null && itemDef(held.id).name == "glowstone" && charges < 4)
            {
                player.consumeHeld(1);
                world.setBlock(x, y, z, (int)cell(B.respawn_anchor, charges + 1));
                world.hooks.playSound("block.respawn_anchor.charge", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                return true;
            }
            if (charges > 0)
            {
                if (world.dim == Dim.nether)
                {
                    player.spawnPoint = (x, y + 1, z);
                    player.spawnDim = 1;
                    world.hooks.playSound("block.respawn_anchor.set_spawn", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                }
                else
                {
                    // BOOM
                    world.setBlock(x, y, z, 0);
                    explodeFn?.Invoke(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 5, true, null);
                }
                return true;
            }
            return false;
        }
        if (id == (int)B.bell)
        {
            world.hooks.playSound("block.bell.use", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 2, 1);
            world.emitVibration((double)x, (double)y, (double)z, 15, player);
            return true;
        }
        if (id == (int)B.cake)
        {
            if (player.hunger < 20)
            {
                player.feed(2, 0.4);
                int bites = meta & 7;
                if (bites >= 6) { world.setBlock(x, y, z, 0); }
                else { world.setBlock(x, y, z, (int)cell(B.cake, bites + 1)); }
                world.hooks.playSound("entity.generic.eat", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 0.8, 1);
                return true;
            }
            return false;
        }
        if (id == (int)B.jukebox)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be != null && be.disc != null)
            {
                var disc = be.disc;
                spawnItem(world, (double)x + 0.5, (double)y + 1.1, (double)z + 0.5, disc);
                be.disc = null;
                world.hooks.playSound("jukebox.stop", (double)x + 0.5, (double)y, (double)z + 0.5, 1, 1);
                return true;
            }
            var held = player.mainHand;
            if (held != null && itemDef(held.id).name.StartsWith("music_disc"))
            {
                var jbe = be;
                if (jbe == null)
                {
                    jbe = new BlockEntityData("jukebox", x, y, z);
                    world.setBlockEntity(jbe);
                }
                var one = held.copy();
                one.count = 1;
                jbe.disc = one;
                jbe.startedTick = world.time;
                player.consumeHeld(1);
                world.hooks.playSound("jukebox.play." + itemDef(held.id).name, (double)x + 0.5, (double)y, (double)z + 0.5, 4, 1);
                return true;
            }
            return false;
        }
        if (id == (int)B.chiseled_bookshelf)
        {
            var be = world.getBlockEntity(x, y, z);
            if (be == null || be.type != "shelf")
            {
                be = new BlockEntityData("shelf", x, y, z);
                be.items = new ItemStack[6];
                be.lastSlot = -1;
                world.setBlockEntity(be);
            }
            var held = player.mainHand;
            string heldName = held != null ? itemDef(held.id).name : null;
            if (held != null && (heldName == "book" || heldName == "enchanted_book" || heldName == "writable_book"))
            {
                var items = be.items ?? new ItemStack[6];
                for (int i = 0; i < 6; i++)
                {
                    if (items[i] != null) continue;
                    var one = held.copy();
                    one.count = 1;
                    items[i] = one;
                    be.items = items;
                    player.consumeHeld(1);
                    be.lastSlot = i;
                    world.hooks.playSound("block.chiseled_bookshelf.insert", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    updateShelfVisual(world, x, y, z, be);
                    return true;
                }
            }
            else
            {
                var items = be.items ?? new ItemStack[6];
                int i = 5;
                while (i >= 0)
                {
                    if (items[i] != null)
                    {
                        var s = items[i];
                        if (player.give(s))
                        {
                            items[i] = null;
                            be.items = items;
                            world.hooks.playSound("block.chiseled_bookshelf.pickup", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                            updateShelfVisual(world, x, y, z, be);
                        }
                        return true;
                    }
                    i -= 1;
                }
            }
            return false;
        }
        if (id == (int)B.composter)
        {
            var held = player.mainHand;
            int level = meta;
            if (level >= 8)
            {
                // collect bone meal
                world.setBlock(x, y, z, (int)cell(B.composter, 0));
                spawnItem(world, (double)x + 0.5, (double)y + 1, (double)z + 0.5, new ItemStack(iid("bone_meal"), 1));
                world.hooks.playSound("block.composter.empty", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                return true;
            }
            if (held != null)
            {
                double chance = itemDef(held.id).compostChance;
                if (chance != 0 && level < 7)
                {
                    player.consumeHeld(1);
                    if (gameRng.nextFloat() < chance)
                    {
                        world.setBlock(x, y, z, (int)cell(B.composter, Math.Min(8, level + 1 == 7 ? 8 : level + 1)));
                        world.hooks.playSound("block.composter.fill_success", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    }
                    else
                    {
                        world.hooks.playSound("block.composter.fill", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (id == (int)B.cauldron)
        {
            return useCauldron(ctx, x, y, z, c);
        }
        if (id == (int)B.campfire || id == (int)B.soul_campfire)
        {
            var held = player.mainHand;
            if (held != null && itemDef(held.id).food != null && (meta & 4) != 0)
            {
                var be = world.getBlockEntity(x, y, z);
                if (be == null || be.type != "campfire")
                {
                    be = new BlockEntityData("campfire", x, y, z);
                    be.items = new ItemStack[4];
                    be.times = new[] { 0, 0, 0, 0 };
                    world.setBlockEntity(be);
                }
                var items = be.items ?? new ItemStack[4];
                var times = be.times ?? new[] { 0, 0, 0, 0 };
                for (int i = 0; i < 4; i++)
                {
                    if (items[i] != null) continue;
                    var one = held.copy();
                    one.count = 1;
                    items[i] = one;
                    times[i] = 0;
                    be.items = items;
                    be.times = times;
                    player.consumeHeld(1);
                    world.hooks.playSound("block.campfire.crackle", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    return true;
                }
            }
            return false;
        }
        if (id == (int)B.beehive || id == (int)B.bee_nest)
        {
            var be = world.getBlockEntity(x, y, z);
            int honey = be?.honey ?? 0;
            var heldStack = player.mainHand;
            string heldName = heldStack != null ? itemDef(heldStack.id).name : null;
            if (honey >= 5)
            {
                if (heldName == "shears")
                {
                    for (int rep = 0; rep < 3; rep++) { spawnItem(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, new ItemStack(iid("honeycomb"), 1)); }
                    if (be != null) be.honey = 0;
                    player.damageHeld(1);
                    world.hooks.playSound("block.beehive.shear", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    return true;
                }
                if (heldName == "glass_bottle")
                {
                    player.replaceHeld(new ItemStack(iid("honey_bottle"), 1));
                    if (be != null) be.honey = 0;
                    world.hooks.playSound("item.bottle.fill", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    return true;
                }
            }
            return false;
        }
        if (id == (int)B.flower_pot)
        {
            var be = world.getBlockEntity(x, y, z);
            var held = player.mainHand;
            if (be != null && be.plant != null)
            {
                string plant = be.plant;
                if (iidOpt(plant) is int itemId && player.give(new ItemStack(itemId, 1)))
                {
                    be.plant = null;
                }
                return true;
            }
            if (held != null)
            {
                string heldName = itemDef(held.id).name;
                var pottableWords = new[] { "sapling", "fern", "dandelion", "poppy", "orchid", "allium", "bluet", "tulip", "daisy", "cornflower", "lily_of", "wither_rose", "mushroom", "cactus", "bamboo", "azalea", "fungus", "roots", "dead_bush", "torchflower" };
                if (pottableWords.Any(w => heldName.Contains(w)))
                {
                    var pbe = be;
                    if (pbe == null)
                    {
                        pbe = new BlockEntityData("lectern", x, y, z);
                        world.setBlockEntity(pbe);
                    }
                    pbe.plant = heldName;
                    player.consumeHeld(1);
                    world.hooks.playSound("block.flower_pot.place_plant", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                    return true;
                }
            }
            return false;
        }
        if (shape == Shape.sign || shape == Shape.wallSign || shape == Shape.hangingSign)
        {
            var data = new ScreenData();
            data.x = x; data.y = y; data.z = z;
            data.be = world.getBlockEntity(x, y, z);
            ctx.openScreen("sign", data);
            return true;
        }
        if (id == (int)B.suspicious_sand || id == (int)B.suspicious_gravel)
        {
            return false; // brushing handled by brush item hold
        }
        if (id == (int)B.end_portal_frame)
        {
            var held = player.mainHand;
            if (held != null && itemDef(held.id).name == "ender_eye" && (meta & 4) == 0)
            {
                world.setBlock(x, y, z, (int)cell(B.end_portal_frame, meta | 4));
                player.consumeHeld(1);
                world.hooks.playSound("block.end_portal_frame.fill", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                checkEndPortalComplete(world, x, y, z);
                ctx.advance("eye_for_an_eye");
                return true;
            }
            return false;
        }
        if (id == (int)B.tnt)
        {
            var held = player.mainHand;
            if (held != null)
            {
                string hn = itemDef(held.id).name;
                if (hn == "flint_and_steel" || hn == "fire_charge")
                {
                    igniteTNT(world, x, y, z);
                    if (hn == "flint_and_steel") { player.damageHeld(1); }
                    else { player.consumeHeld(1); }
                    return true;
                }
            }
            return false;
        }
        if (id == (int)B.lectern || id == (int)B.decorated_pot)
        {
            return false;
        }
        if (id == (int)B.lodestone)
        {
            var held = player.mainHand;
            if (held != null && itemDef(held.id).name == "compass")
            {
                var data = new StackData();
                data.lodestone = new List<int> { x, y, z, (int)world.dim };
                held.data = data;
                world.hooks.playSound("item.lodestone_compass.lock", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                return true;
            }
            return false;
        }
        return false;
    }

    private static void updateShelfVisual(World world, int x, int y, int z, BlockEntityData be)
    {
        int c = world.getBlock(x, y, z);
        bool any = (be.items ?? new ItemStack[0]).Any(s => s != null);
        int newMeta = (c & 3) | (any ? 4 : 0);
        if ((c & 15) != newMeta)
        {
            world.setBlock(x, y, z, (int)cell(B.chiseled_bookshelf, newMeta), 4);
            world.setBlockEntity(be);
        }
    }

    public static void resolveLoot(World world, BlockEntityData be)
    {
        if (be.lootTable != null)
        {
            string lootTable = be.lootTable;
            var lootRng = new RandomX(unchecked((uint)(be.lootSeed ?? 1)));
            var loot = rollLoot(lootTable, ref lootRng);
            var items = be.items ?? new ItemStack[0];
            var slots = Enumerable.Range(0, items.Length).ToList();
            var r = new RandomX(unchecked((uint)((be.lootSeed ?? 1) ^ 0x55)));
            r.shuffle(slots);
            for (int i = 0; i < Math.Min(loot.Count, slots.Count); i++)
            {
                items[slots[i]] = loot[i];
            }
            be.items = items;
            be.lootTable = null;
        }
    }

    private static bool useBed(InteractCtx ctx, int x, int y, int z, int c)
    {
        World world = ctx.world; Player player = ctx.player;
        if (world.dim != Dim.overworld)
        {
            // bed explodes!
            world.setBlock(x, y, z, 0);
            explodeFn?.Invoke(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 5, true, null);
            return true;
        }
        // set spawn
        player.spawnPoint = (x, y, z);
        player.spawnDim = 0;
        // sleep if night
        if (world.isDay() && world.rainLevel < 0.9)
        {
            var data = new ScreenData();
            data.text = "You can only sleep at night";
            ctx.openScreen("toast", data);
            return true;
        }
        // monsters nearby?
        var monsters = world.getEntitiesNear((double)x, (double)y, (double)z, 8, e =>
            (e as Mob)?.category == "monster" && !e.dead);
        if (monsters.Count != 0)
        {
            var data = new ScreenData();
            data.text = "You may not rest now; there are monsters nearby";
            ctx.openScreen("toast", data);
            return true;
        }
        player.sleepTicks = 1;
        player.bedPos = (x, y, z);
        player.setPos((double)x + 0.5, (double)y + 0.6, (double)z + 0.5);
        return true;
    }

    private static bool useCauldron(InteractCtx ctx, int x, int y, int z, int c)
    {
        World world = ctx.world; Player player = ctx.player;
        var held = player.mainHand;
        if (held == null) return false;
        string name = itemDef(held.id).name;
        int level = c & 3;
        int kind = (c >> 2) & 3;
        if (name == "water_bucket")
        {
            world.setBlock(x, y, z, (int)cell(B.cauldron, 3 | (0 << 2)));
            player.replaceHeld(new ItemStack(iid("bucket"), 1));
            world.hooks.playSound("item.bucket.empty", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
            return true;
        }
        if (name == "lava_bucket")
        {
            world.setBlock(x, y, z, (int)cell(B.cauldron, 3 | (1 << 2)));
            player.replaceHeld(new ItemStack(iid("bucket"), 1));
            return true;
        }
        if (name == "powder_snow_bucket")
        {
            world.setBlock(x, y, z, (int)cell(B.cauldron, 3 | (2 << 2)));
            player.replaceHeld(new ItemStack(iid("bucket"), 1));
            return true;
        }
        if (name == "bucket" && level == 3)
        {
            string outName = kind == 0 ? "water_bucket" : kind == 1 ? "lava_bucket" : "powder_snow_bucket";
            world.setBlock(x, y, z, (int)cell(B.cauldron, 0));
            player.replaceHeld(new ItemStack(iid(outName), 1));
            world.hooks.playSound("item.bucket.fill", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
            return true;
        }
        if (name == "glass_bottle" && kind == 0 && level > 0)
        {
            world.setBlock(x, y, z, (int)cell(B.cauldron, (level - 1) | (0 << 2)));
            var data = new StackData();
            data.potion = "water";
            player.replaceHeld(new ItemStack(iid("potion"), 1, data: data));
            world.hooks.playSound("item.bottle.fill", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
            return true;
        }
        if (name == "potion" && held.data.potion == "water" && level < 3 && kind == 0)
        {
            world.setBlock(x, y, z, (int)cell(B.cauldron, level + 1));
            player.replaceHeld(new ItemStack(iid("glass_bottle"), 1));
            return true;
        }
        // wash dyed items
        if (kind == 0 && level > 0)
        {
            if (name.EndsWith("_shulker_box"))
            {
                player.replaceHeld(new ItemStack(iid("shulker_box"), 1));
                world.setBlock(x, y, z, (int)cell(B.cauldron, level - 1));
                return true;
            }
        }
        return false;
    }

    private static void checkEndPortalComplete(World world, int x, int y, int z)
    {
        // scan candidate centers near the frame
        for (int cz = z - 4; cz <= z + 4; cz++)
        {
            for (int cx = x - 4; cx <= x + 4; cx++)
            {
                // a complete portal: frames at ring positions around 3×3 air
                bool complete = true;
                for (int i = -1; i <= 1; i++)
                {
                    foreach (var (fx, fz) in new[] { (i, -2), (i, 2), (-2, i), (2, i) })
                    {
                        int fc = world.getBlock(cx + fx, y, cz + fz);
                        if ((fc >> 4) != (int)B.end_portal_frame || (fc & 4) == 0) { complete = false; goto doneOuter; }
                    }
                }
            doneOuter:;
                if (complete)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            world.setBlock(cx + dx, y, cz + dz, (int)cell(B.end_portal));
                        }
                    }
                    world.hooks.playSound("block.end_portal.spawn", (double)cx + 0.5, (double)y, (double)cz + 0.5, 3, 1);
                    return;
                }
            }
        }
    }

    // =============================================================================
    // ITEM USE
    // =============================================================================
    public static bool useItem(InteractCtx ctx, RaycastHit? hit)
    {
        World world = ctx.world; Player player = ctx.player;
        var held = player.mainHand;
        if (held == null) return false;
        var def = itemDef(held.id);
        string name = def.name;

        // stick-steering boost while riding (vanilla: 140–980 ticks)
        if (name == "carrot_on_a_stick" && player.vehicle is Pig pig)
        {
            pig.boostTime = 140 + (int)(world.rng.nextFloat() * 840);
            return true;
        }
        if (name == "warped_fungus_on_a_stick" && player.vehicle is Strider strider)
        {
            strider.boostTime = 140 + (int)(world.rng.nextFloat() * 840);
            return true;
        }

        // food → start eating
        if (def.food is FoodDef food)
        {
            if (player.hunger < 20 || food.alwaysEat || player.gameMode == GameMode.creative)
            {
                player.usingItem = true;
                player.useItemTicks = 0;
                return true;
            }
            return false;
        }
        if (name == "potion")
        {
            player.usingItem = true;
            player.useItemTicks = 0;
            return true;
        }
        if (name == "bow" || name == "trident" || name == "crossbow" || name == "shield" || name == "spyglass" || name == "goat_horn")
        {
            player.usingItem = true;
            player.useItemTicks = 0;
            if (name == "goat_horn")
            {
                world.hooks.playSound("item.goat_horn.sound", player.x, player.y, player.z, 8, 1);
                world.emitVibration((double)ifloor(player.x), (double)ifloor(player.y), (double)ifloor(player.z), 15, player);
            }
            return true;
        }
        // throwables
        if (name == "snowball" || name == "egg" || name == "ender_pearl" || name == "experience_bottle" || name == "splash_potion" || name == "lingering_potion")
        {
            Projectile proj;
            if (name == "snowball") { proj = new ThrownSnowball(world); }
            else if (name == "egg") { proj = new ThrownEgg(world); }
            else if (name == "ender_pearl") { proj = new ThrownPearl(world); }
            else if (name == "experience_bottle") { proj = new ThrownXPBottle(world); }
            else
            {
                var p = new ThrownPotion(world);
                p.potionId = held.data.potion ?? "water";
                p.lingering = name == "lingering_potion";
                proj = p;
            }
            proj.shootFrom(player, player.pitch, player.yaw, 1.5, 1);
            proj.gravity = name == "splash_potion" || name == "lingering_potion" ? 0.05 : 0.03;
            world.addEntity(proj);
            world.hooks.playSound("entity.snowball.throw", player.x, player.y, player.z, 0.5, 0.5);
            player.consumeHeld(1);
            return true;
        }
        if (name == "fishing_rod")
        {
            if (player.fishingBobberId is int bobberId)
            {
                if (world.entityById.TryGetValue(bobberId, out var be0) && be0 is FishingBobber bobber && !bobber.dead)
                {
                    bobber.retrieve();
                    player.damageHeld(1);
                    player.fishingBobberId = null;
                    return true;
                }
                player.fishingBobberId = null;
            }
            var newBobber = new FishingBobber(world);
            newBobber.ownerPlayer = player;
            newBobber.setPos(player.x, player.eyeY() - 0.1, player.z);
            double lookX = -detSin(player.yaw) * detCos(player.pitch);
            double lookY = -detSin(player.pitch);
            double lookZ = detCos(player.yaw) * detCos(player.pitch);
            newBobber.vx = lookX * 0.8;
            newBobber.vy = lookY * 0.8 + 0.1;
            newBobber.vz = lookZ * 0.8;
            world.addEntity(newBobber);
            player.fishingBobberId = newBobber.id;
            world.hooks.playSound("entity.fishing_bobber.throw", player.x, player.y, player.z, 0.5, 0.6);
            return true;
        }
        if (name == "firework_rocket")
        {
            var fw = new FireworkEntity(world);
            if (player.elytraFlying)
            {
                fw.attachedTo = player;
                fw.setPos(player.x, player.y, player.z);
            }
            else if (hit is RaycastHit h0)
            {
                fw.setPos(h0.px, h0.py, h0.pz);
                fw.vy = 0.4;
            }
            else { return false; }
            fw.flightDuration = held.data.flight ?? 1;
            world.addEntity(fw);
            world.hooks.playSound("entity.firework_rocket.launch", player.x, player.y, player.z, 1, 1);
            player.consumeHeld(1);
            return true;
        }
        if (name == "ender_eye")
        {
            // locate stronghold
            var positions = strongholdPositions(world.seed);
            (double, double)? best = null;
            double bestD = double.PositiveInfinity;
            foreach (var (scx, scz) in positions)
            {
                double dx = (double)(scx * 16) - player.x, dz = (double)(scz * 16) - player.z;
                double d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = ((double)(scx * 16 + 8), (double)(scz * 16 + 8)); }
            }
            if (best is (double, double) bestPos)
            {
                var eye = new EyeOfEnderEntity(world);
                eye.setPos(player.x, player.eyeY(), player.z);
                eye.targetX = bestPos.Item1;
                eye.targetZ = bestPos.Item2;
                world.addEntity(eye);
                world.hooks.playSound("entity.ender_eye.launch", player.x, player.y, player.z, 1, 1);
                player.consumeHeld(1);
                ctx.advance("follow_ender_eye");
                return true;
            }
            return false;
        }
        // armor equip
        if (def.armor is ArmorDef armor)
        {
            int slot = armor.slot;
            var cur = player.armor[slot];
            var one = held.copy();
            one.count = 1;
            player.armor[slot] = one;
            player.consumeHeld(1);
            if (cur != null) { player.give(cur); }
            world.hooks.playSound("item.armor.equip_generic", player.x, player.y, player.z, 1, 1);
            return true;
        }

        // ---- block-targeted item uses ----
        if (hit == null) return false;
        RaycastHit hitV = hit.Value;
        int x = hitV.x, y = hitV.y, z = hitV.z, face = hitV.face;
        int tx = x + DIR_X[face], ty = y + DIR_Y[face], tz = z + DIR_Z[face];
        int targetCell = world.getBlock(x, y, z);
        int targetId = targetCell >> 4;

        if (name == "water_bucket" || name == "lava_bucket" || name == "powder_snow_bucket" ||
            name == "cod_bucket" || name == "salmon_bucket" || name == "pufferfish_bucket" ||
            name == "tropical_fish_bucket" || name == "axolotl_bucket" || name == "tadpole_bucket")
        {
            bool isReplaceable = REPLACEABLE[targetId] == 1;
            int px = isReplaceable ? x : tx;
            int py = isReplaceable ? y : ty;
            int pz = isReplaceable ? z : tz;
            int cur = world.getBlock(px, py, pz);
            if (cur != 0 && REPLACEABLE[cur >> 4] == 0 && blockDefs[cur >> 4].solid) { return false; }
            if (name == "lava_bucket") { world.setBlock(px, py, pz, (int)cell(B.lava, 0)); }
            else if (name == "powder_snow_bucket") { world.setBlock(px, py, pz, (int)cell(B.powder_snow)); }
            else { world.setBlock(px, py, pz, (int)cell(B.water, 0)); }
            if (name != "water_bucket" && name != "lava_bucket" && name != "powder_snow_bucket")
            {
                string mob = name.Replace("_bucket", "");
                spawnMob(world, mob, (double)px + 0.5, (double)py + 0.3, (double)pz + 0.5, new SpawnOpts());
            }
            player.replaceHeld(new ItemStack(iid("bucket"), 1));
            world.hooks.playSound(name == "lava_bucket" ? "item.bucket.empty_lava" : "item.bucket.empty", (double)px + 0.5, (double)py + 0.5, (double)pz + 0.5, 1, 1);
            return true;
        }
        if (name == "bucket")
        {
            // pick up fluid (raycast through fluids)
            var fhit = world.raycast(player.x, player.eyeY(), player.z,
                                     -detSin(player.yaw) * detCos(player.pitch), -detSin(player.pitch), detCos(player.yaw) * detCos(player.pitch), 5, fluid: true);
            if (fhit is RaycastHit fhitV)
            {
                int fc = world.getBlock(fhitV.x, fhitV.y, fhitV.z);
                int fid = fc >> 4;
                if ((fid == (int)B.water || fid == (int)B.lava) && (fc & 15) == 0)
                {
                    world.setBlock(fhitV.x, fhitV.y, fhitV.z, 0);
                    player.replaceHeld(new ItemStack(iid(fid == (int)B.water ? "water_bucket" : "lava_bucket"), 1));
                    world.hooks.playSound(fid == (int)B.water ? "item.bucket.fill" : "item.bucket.fill_lava", (double)fhitV.x, (double)fhitV.y, (double)fhitV.z, 1, 1);
                    return true;
                }
                if (fid == (int)B.powder_snow)
                {
                    world.setBlock(fhitV.x, fhitV.y, fhitV.z, 0);
                    player.replaceHeld(new ItemStack(iid("powder_snow_bucket"), 1));
                    return true;
                }
            }
            return false;
        }
        if (name == "flint_and_steel" || name == "fire_charge")
        {
            // portal ignition first
            if (tryIgnitePortal(world, tx, ty, tz) || tryIgnitePortal(world, x, y + 1, z))
            {
                if (name == "flint_and_steel") { player.damageHeld(1); } else { player.consumeHeld(1); }
                world.hooks.playSound("item.flintandsteel.use", (double)tx + 0.5, (double)ty + 0.5, (double)tz + 0.5, 1, 1);
                ctx.advance("ignite_portal");
                return true;
            }
            if (targetId == (int)B.campfire || targetId == (int)B.soul_campfire)
            {
                if ((targetCell & 4) == 0)
                {
                    world.setBlock(x, y, z, (int)cell((ushort)targetId, (targetCell & 15) | 4));
                    if (name == "flint_and_steel") { player.damageHeld(1); } else { player.consumeHeld(1); }
                    return true;
                }
            }
            if (world.getBlock(tx, ty, tz) == 0)
            {
                world.setBlock(tx, ty, tz, (int)cell(B.fire));
                if (name == "flint_and_steel") { player.damageHeld(1); } else { player.consumeHeld(1); }
                world.hooks.playSound("item.flintandsteel.use", (double)tx + 0.5, (double)ty + 0.5, (double)tz + 0.5, 1, 1);
                return true;
            }
            return false;
        }
        if (name == "bone_meal")
        {
            if (applyBonemeal(world, x, y, z))
            {
                player.consumeHeld(1);
                return true;
            }
            return false;
        }
        if (def.tool?.type == "hoe")
        {
            if ((targetId == (int)B.grass_block || targetId == (int)B.dirt || targetId == (int)B.dirt_path) && world.getBlock(x, y + 1, z) == 0)
            {
                world.setBlock(x, y, z, (int)cell(B.farmland, 0));
                world.hooks.playSound("item.hoe.till", (double)x + 0.5, (double)y + 1, (double)z + 0.5, 1, 1);
                player.damageHeld(1);
                return true;
            }
            if (targetId == (int)B.rooted_dirt)
            {
                world.setBlock(x, y, z, (int)cell(B.dirt));
                spawnItem(world, (double)x + 0.5, (double)y + 1, (double)z + 0.5, new ItemStack(iid("hanging_roots"), 1));
                player.damageHeld(1);
                return true;
            }
            return false;
        }
        if (def.tool?.type == "shovel")
        {
            if (targetId == (int)B.grass_block && world.getBlock(x, y + 1, z) == 0)
            {
                world.setBlock(x, y, z, (int)cell(B.dirt_path));
                world.hooks.playSound("item.shovel.flatten", (double)x + 0.5, (double)y + 1, (double)z + 0.5, 1, 1);
                player.damageHeld(1);
                return true;
            }
            if ((targetId == (int)B.campfire || targetId == (int)B.soul_campfire) && (targetCell & 4) != 0)
            {
                world.setBlock(x, y, z, (int)cell((ushort)targetId, targetCell & 11));
                world.hooks.playSound("block.fire.extinguish", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                player.damageHeld(1);
                return true;
            }
            return false;
        }
        if (def.tool?.type == "axe")
        {
            int stripped = strippedVersion(targetId);
            if (stripped >= 0)
            {
                world.setBlock(x, y, z, (int)cell((ushort)stripped, targetCell & 15));
                world.hooks.playSound("item.axe.strip", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                player.damageHeld(1);
                return true;
            }
            int scraped = scrapedCopper(targetId);
            if (scraped >= 0)
            {
                world.setBlock(x, y, z, (int)cell((ushort)scraped, targetCell & 15));
                world.hooks.playSound("item.axe.scrape", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                player.damageHeld(1);
                return true;
            }
            return false;
        }
        if (name == "shears")
        {
            if (targetId == (int)B.pumpkin)
            {
                world.setBlock(x, y, z, (int)cell(B.carved_pumpkin, dirFacingMetaOpp(player)));
                spawnItem(world, (double)x + 0.5, (double)y + 1, (double)z + 0.5, new ItemStack(iid("pumpkin_seeds"), 4));
                player.damageHeld(1);
                world.hooks.playSound("block.pumpkin.carve", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 1);
                return true;
            }
            return false;
        }
        if (name == "honeycomb")
        {
            int waxed = waxedCopper(targetId);
            if (waxed >= 0)
            {
                world.setBlock(x, y, z, (int)cell((ushort)waxed, targetCell & 15));
                world.hooks.addParticles("wax", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 8, 0.5, 0);
                player.consumeHeld(1);
                return true;
            }
            return false;
        }
        if (name.EndsWith("_boat") || name.EndsWith("_raft") || name.Contains("chest_boat") || name.Contains("chest_raft"))
        {
            var boat = new Boat(world);
            boat.wood = name.Split('_')[0] == "bamboo" ? "bamboo"
                : name.Replace("_chest_boat", "")
                    .Replace("_boat", "")
                    .Replace("_chest_raft", "")
                    .Replace("_raft", "");
            boat.hasChest = name.Contains("chest");
            boat.setPos(hitV.px, hitV.py + 0.2, hitV.pz);
            boat.yaw = player.yaw;
            world.addEntity(boat);
            player.consumeHeld(1);
            return true;
        }
        if (name == "minecart" || name.EndsWith("_minecart"))
        {
            if (shapeOf(targetId) == Shape.rail)
            {
                var cart = new Minecart(world);
                cart.variant = name == "minecart" ? "empty" : name.Replace("_minecart", "");
                cart.setPos((double)x + 0.5, (double)y + 0.3, (double)z + 0.5);
                world.addEntity(cart);
                player.consumeHeld(1);
                return true;
            }
            return false;
        }
        if (name.EndsWith("_spawn_egg"))
        {
            string mob = name.Substring(0, name.Length - 10);
            spawnMob(world, mob, (double)tx + 0.5, (double)ty, (double)tz + 0.5, new SpawnOpts());
            if (player.gameMode != GameMode.creative) { player.consumeHeld(1); }
            return true;
        }
        if (name == "brush") { return false; } // handled as hold-to-brush in Game
        if (name == "lead" || name == "name_tag") { return false; } // entity interactions

        // ---- block placement ----
        if (def.block is ushort block)
        {
            return placeBlock(ctx, hitV, (int)block, held);
        }
        return false;
    }

    private static int strippedVersion(int id)
    {
        string name = blockDefs[id].name;
        var logWoods = new[] { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry" };
        bool isLog = logWoods.Any(w => name == $"{w}_log" || name == $"{w}_wood");
        bool isStem = name == "crimson_stem" || name == "crimson_hyphae" || name == "warped_stem" || name == "warped_hyphae";
        if (isLog || isStem || name == "bamboo_block")
        {
            string strippedName = "stripped_" + name;
            return bidOpt(strippedName) is ushort v ? (int)v : -1;
        }
        return -1;
    }
    private static int scrapedCopper(int id)
    {
        string name = blockDefs[id].name;
        if (name.StartsWith("waxed_"))
        {
            string unwaxed = name.Substring(6);
            return bidOpt(unwaxed) is ushort v ? (int)v : -1;
        }
        foreach (var (from, to) in new[] { ("exposed_", ""), ("weathered_", "exposed_"), ("oxidized_", "weathered_") })
        {
            if (name.StartsWith(from) && name.Contains("copper"))
            {
                string next = to + name.Substring(from.Length);
                return bidOpt(next) is ushort v ? (int)v : -1;
            }
        }
        return -1;
    }
    private static int waxedCopper(int id)
    {
        string name = blockDefs[id].name;
        if (name.Contains("copper") && !name.StartsWith("waxed_") && !name.Contains("ore") && !name.Contains("raw"))
        {
            string waxed = "waxed_" + name;
            return bidOpt(waxed) is ushort v ? (int)v : -1;
        }
        return -1;
    }

    // =============================================================================
    // PLACEMENT
    // =============================================================================
    public static bool placeBlock(InteractCtx ctx, RaycastHit hit, int blockId, ItemStack held)
    {
        World world = ctx.world; Player player = ctx.player;
        int targetCell = world.getBlock(hit.x, hit.y, hit.z);
        int px = hit.x, py = hit.y, pz = hit.z;
        if (REPLACEABLE[targetCell >> 4] == 0)
        {
            px += DIR_X[hit.face];
            py += DIR_Y[hit.face];
            pz += DIR_Z[hit.face];
        }
        int cur = world.getBlock(px, py, pz);
        if (cur != 0 && REPLACEABLE[cur >> 4] == 0)
        {
            // slab merging
            if (shapeOf(blockId) == Shape.slab && (cur >> 4) == blockId && (cur & 3) != 2)
            {
                world.setBlock(px, py, pz, (int)cell((ushort)blockId, 2));
                placeEffects(world, blockId, px, py, pz);
                player.consumeHeld(1);
                return true;
            }
            return false;
        }
        // entity collision check
        var def = blockDefs[blockId];
        if (def.solid)
        {
            var box = new AABB((double)px + 0.05, (double)py + 0.05, (double)pz + 0.05, (double)px + 0.95, (double)py + 0.95, (double)pz + 0.95);
            var blocked = world.getEntitiesInBox(box, except: null, filter: e => e is LivingEntity);
            if (blocked.Count != 0) { return false; }
        }
        int meta = placementMeta(world, player, hit, blockId, px, py, pz);
        if (meta == -1) { return false; }
        // snow layer stacking
        if (blockId == (int)B.snow && (cur >> 4) == (int)B.snow)
        {
            int layers = cur & 7;
            if (layers < 7)
            {
                world.setBlock(px, py, pz, (int)cell(B.snow, layers + 1));
                placeEffects(world, blockId, px, py, pz);
                player.consumeHeld(1);
                return true;
            }
            return false;
        }
        // candles & pickles & petals stack
        if ((cur >> 4) == blockId && (shapeOf(blockId) == Shape.candle || shapeOf(blockId) == Shape.seaPickle || blockId == (int)bid("pink_petals") || shapeOf(blockId) == Shape.turtleEgg))
        {
            int count = cur & 3;
            if (count < 3)
            {
                world.setBlock(px, py, pz, (int)cell((ushort)blockId, (cur & 12) | (count + 1)));
                placeEffects(world, blockId, px, py, pz);
                player.consumeHeld(1);
                return true;
            }
            return false;
        }

        world.setBlock(px, py, pz, (int)cell((ushort)blockId, meta));
        // multi-block: doors & beds & tall plants
        Shape shape = shapeOf(blockId);
        if (shape == Shape.door)
        {
            int above = world.getBlock(px, py + 1, pz);
            if (above != 0 && REPLACEABLE[above >> 4] == 0)
            {
                world.setBlock(px, py, pz, 0);
                return false;
            }
            // hinge: pick side with more support
            int hinge = interactRng.nextBoolean() ? 1 : 0;
            world.setBlock(px, py + 1, pz, (int)cell((ushort)blockId, 8 | hinge));
        }
        else if (shape == Shape.bed)
        {
            int f = meta & 3;
            int hx = px + new[] { 0, 0, -1, 1 }[f], hz = pz + new[] { -1, 1, 0, 0 }[f];
            int headCur = world.getBlock(hx, py, hz);
            if (headCur != 0 && REPLACEABLE[headCur >> 4] == 0)
            {
                world.setBlock(px, py, pz, 0);
                return false;
            }
            world.setBlock(hx, py, hz, (int)cell((ushort)blockId, f | 4));
        }
        else if (shape == Shape.tallCross || blockId == (int)B.pitcher_plant)
        {
            if (world.getBlock(px, py + 1, pz) == 0)
            {
                world.setBlock(px, py + 1, pz, (int)cell((ushort)blockId, meta | 1));
            }
        }
        // block entities on placement
        attachPlacementBE(world, blockId, px, py, pz, held);
        if (onPlacedHandlers.TryGetValue(blockId, out var handler))
        {
            handler(world, px, py, pz, (int)cell((ushort)blockId, meta));
        }
        placeEffects(world, blockId, px, py, pz);
        player.consumeHeld(1);
        player.stats["blocksPlaced"] = (player.stats.TryGetValue("blocksPlaced", out var bp) ? bp : 0) + 1;
        world.emitVibration((double)px, (double)py, (double)pz, 13, player);
        return true;
    }

    private static void placeEffects(World world, int blockId, int x, int y, int z)
    {
        world.hooks.playSound("block." + blockDefs[blockId].sound + ".place", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 0.8);
    }

    private static void attachPlacementBE(World world, int blockId, int x, int y, int z, ItemStack held)
    {
        if (blockId == (int)B.chest || blockId == (int)B.trapped_chest || blockId == (int)B.barrel)
        {
            world.setBlockEntity(makeContainerBE(x, y, z, 27));
        }
        else if (blockId == (int)B.furnace || blockId == (int)B.blast_furnace || blockId == (int)B.smoker)
        {
            world.setBlockEntity(makeFurnaceBE(x, y, z, blockId == (int)B.blast_furnace ? "blast" : blockId == (int)B.smoker ? "smoker" : "furnace"));
        }
        else if (blockId == (int)B.brewing_stand)
        {
            world.setBlockEntity(makeBrewingBE(x, y, z));
        }
        else if (blockId == (int)B.hopper)
        {
            world.setBlockEntity(makeHopperBE(x, y, z));
        }
        else if (blockId == (int)B.dispenser || blockId == (int)B.dropper)
        {
            world.setBlockEntity(makeContainerBE(x, y, z, 9));
        }
        else if (blockDefs[blockId].name.EndsWith("shulker_box") || blockId == (int)B.shulker_box)
        {
            var be = makeContainerBE(x, y, z, 27);
            if (held.data.contents != null) { be.items = held.data.contents.ToArray(); }
            world.setBlockEntity(be);
        }
        else if (shapeOf(blockId) == Shape.sign || shapeOf(blockId) == Shape.wallSign || shapeOf(blockId) == Shape.hangingSign)
        {
            world.setBlockEntity(makeSignBE(x, y, z));
        }
        else if (blockId == (int)B.beacon)
        {
            var be = new BlockEntityData("beacon", x, y, z);
            be.levels = 0;
            world.setBlockEntity(be);
        }
        else if (blockId == (int)B.conduit)
        {
            var be = new BlockEntityData("conduit", x, y, z);
            be.active = false;
            world.setBlockEntity(be);
        }
        else if (blockId == (int)B.decorated_pot)
        {
            var be = new BlockEntityData("pot", x, y, z);
            be.sherds = held.data.sherds != null ? held.data.sherds.ToArray() : new string[] { null, null, null, null };
            world.setBlockEntity(be);
        }
    }

    /// compute placement meta for orientation-aware shapes; -1 = can't place
    private static int placementMeta(World world, Player player, RaycastHit hit, int blockId, int px, int py, int pz)
    {
        Shape shape = shapeOf(blockId);
        string name = blockDefs[blockId].name;
        int facing = dirFacingMeta(player);       // direction player faces
        int facingOpp = dirFacingMetaOpp(player); // toward player
        double hitFY = hit.py - (double)hit.y;

        switch (shape)
        {
            case Shape.stairs:
            {
                int top = (hit.face == 0 || (hit.face != 1 && hitFY > 0.5)) ? 4 : 0;
                return facing | top;
            }
            case Shape.slab:
            {
                int top = (hit.face == 0 || (hit.face != 1 && hitFY > 0.5)) ? 1 : 0;
                return top;
            }
            case Shape.door:
            case Shape.fenceGate:
            case Shape.bed:
                return facing;
            case Shape.trapdoor:
            {
                int top = (hit.face == 0 || (hit.face != 1 && hitFY > 0.5)) ? 8 : 0;
                return facingOpp | top;
            }
            case Shape.torch:
            {
                if (blockId == (int)B.lightning_rod || blockId == (int)B.end_rod) { return hit.face; }
                if (hit.face >= 2)
                {
                    // wall torch: meta = dir TOWARD support block = opposite of face
                    int support = world.getBlock(px - DIR_X[hit.face], py, pz - DIR_Z[hit.face]) >> 4;
                    if (blockDefs[support].fullCube) { return hit.face ^ 1; }
                }
                int below = world.getBlock(px, py - 1, pz);
                if (!blockDefs[below >> 4].fullCube && !sturdyTopOk(below)) { return -1; }
                return 0;
            }
            case Shape.lever:
            case Shape.button:
                return hit.face ^ 1; // attach toward support
            case Shape.ladder:
            case Shape.wallSign:
            {
                if (hit.face < 2) { return -1; }
                return new[] { 0, 1, 2, 3 }[hit.face - 2];
            }
            case Shape.sign:
            {
                double deg = (player.yaw * 180 / Math.PI + 180) % 360;
                return (int)Math.Floor(deg / 22.5) & 15;
            }
            case Shape.chest:
                return facingOpp;
            case Shape.repeater:
            case Shape.comparator:
                return facing;
            case Shape.rail:
                // orient along player facing; curves/connections simplified to straight
                return (facing == 2 || facing == 3) ? 1 : 0;
            case Shape.crop:
            {
                int below = world.getBlock(px, py - 1, pz) >> 4;
                if (blockId == (int)B.nether_wart) { return below == (int)B.soul_sand ? 0 : -1; }
                return below == (int)B.farmland ? 0 : -1;
            }
            case Shape.cross:
            case Shape.tallCross:
            {
                int below = world.getBlock(px, py - 1, pz) >> 4;
                string belowName = blockDefs[below].name;
                var plantWords = new[] { "sapling", "grass", "fern", "flower", "bush", "tulip", "daisy", "orchid", "allium", "bluet", "dandelion", "poppy", "cornflower", "lily", "rose", "peony", "lilac", "sunflower", "torchflower", "pitcher" };
                if (plantWords.Any(w => name.Contains(w)))
                {
                    var soilWords = new[] { "grass_block", "dirt", "podzol", "farmland", "coarse", "rooted", "moss", "mud" };
                    return soilWords.Any(w => belowName.Contains(w)) ? 0 : -1;
                }
                var netherWords = new[] { "fungus", "roots", "sprouts" };
                if (netherWords.Any(w => name.Contains(w)))
                {
                    var netherSoil = new[] { "nylium", "netherrack", "soul", "grass_block", "dirt", "moss" };
                    return netherSoil.Any(w => belowName.Contains(w)) ? 0 : -1;
                }
                if (name == "sugar_cane")
                {
                    // vanilla: on another cane, or on dirt/sand family with water (or
                    // frosted ice) horizontally adjacent to the SUPPORT block.
                    // (the "special check below" this pointed at never existed — cane
                    // was unplaceable everywhere)
                    if (below == (int)B.sugar_cane) { return 0; }
                    var dirtSand = new[] { "grass_block", "dirt", "coarse_dirt", "podzol", "mycelium",
                                    "rooted_dirt", "moss_block", "mud", "sand", "red_sand",
                                    "suspicious_sand", "gravel" };
                    if (!dirtSand.Contains(belowName)) return -1;
                    foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                    {
                        int n = world.getBlock(px + dx, py - 1, pz + dz) >> 4;
                        if (n == (int)B.water || n == (int)B.frosted_ice) { return 0; }
                    }
                    return -1;
                }
                if (name == "dead_bush")
                {
                    var dryWords = new[] { "sand", "terracotta", "dirt" };
                    return dryWords.Any(w => belowName.Contains(w)) ? 0 : -1;
                }
                return blockDefs[below].fullCube ? 0 : -1;
            }
            case Shape.carpet:
            case Shape.pressurePlate:
            case Shape.lilyPad:
            {
                int below = world.getBlock(px, py - 1, pz);
                if (shape == Shape.lilyPad) { return (below >> 4) == (int)B.water ? 0 : -1; }
                return blockDefs[below >> 4].fullCube || sturdyTopOk(below) ? 0 : -1;
            }
            case Shape.hopper:
                return hit.face == 1 ? 0 : hit.face == 0 ? 0 : hit.face ^ 1;
            case Shape.lantern:
                return hit.face == 0 ? 1 : 0;
            case Shape.campfire:
                return facing;
            case Shape.anvil:
                return (facing == 0 || facing == 1) ? 2 : 0;
            case Shape.piston:
                // facing away from player incl vertical
                if (player.pitch < -0.9) { return Dir.down; }
                if (player.pitch > 0.9) { return Dir.up; }
                return new[] { Dir.north, Dir.south, Dir.west, Dir.east }[facingOpp];
            case Shape.vine:
                if (hit.face < 2) { return -1; }
                return 1 << (hit.face - 2);
            case Shape.glowLichen:
            case Shape.sculkVein:
                return hit.face ^ 1;
            case Shape.cocoa:
            {
                if (hit.face < 2) { return -1; }
                int support = world.getBlock(px - DIR_X[hit.face], py, pz - DIR_Z[hit.face]) >> 4;
                string supportName = blockDefs[support].name;
                if (!supportName.Contains("jungle_log") && !supportName.Contains("jungle_wood")) { return -1; }
                return (hit.face - 2) ^ 1;
            }
            case Shape.amethystCluster:
                return hit.face ^ 1;
            default:
            {
                // axis blocks (logs, basalt, chain, bone)
                bool isAxis = name.EndsWith("_log") || name.EndsWith("_stem") || name.EndsWith("_wood")
                    || name.Contains("hyphae") || name.Contains("basalt") || name == "bone_block"
                    || name == "chain" || name == "quartz_pillar" || name == "purpur_pillar" || name == "bamboo_block";
                if (isAxis)
                {
                    return hit.face < 2 ? 0 : (hit.face < 4 ? 2 : 1);
                }
                if (blockId == (int)B.observer)
                {
                    if (player.pitch < -0.9) { return Dir.up; }
                    if (player.pitch > 0.9) { return Dir.down; }
                    return new[] { Dir.north, Dir.south, Dir.west, Dir.east }[facing];
                }
                if (blockId == (int)B.dispenser || blockId == (int)B.dropper)
                {
                    if (player.pitch < -0.9) { return Dir.down; }
                    if (player.pitch > 0.9) { return Dir.up; }
                    return new[] { Dir.north, Dir.south, Dir.west, Dir.east }[facingOpp];
                }
                if (blockId == (int)B.barrel)
                {
                    if (player.pitch < -0.9) { return Dir.up; }
                    if (player.pitch > 0.9) { return Dir.down; }
                    return new[] { Dir.north, Dir.south, Dir.west, Dir.east }[facingOpp];
                }
                if (blockId == (int)B.furnace || blockId == (int)B.blast_furnace || blockId == (int)B.smoker ||
                    blockId == (int)B.carved_pumpkin || blockId == (int)B.jack_o_lantern || blockId == (int)B.chiseled_bookshelf || blockId == (int)B.loom)
                {
                    return facingOpp;
                }
                return 0;
            }
        }
    }
    private static bool sturdyTopOk(int c)
    {
        int id = c >> 4;
        Shape s = shapeOf(id);
        if (s == Shape.slab) { return (c & 3) != 0; }
        if (s == Shape.stairs) { return (c & 4) != 0; }
        return blockDefs[id].fullCube;
    }

    // =============================================================================
    // EATING / DRINKING completion
    // =============================================================================
    public static void finishUsingItem(InteractCtx ctx)
    {
        World world = ctx.world; Player player = ctx.player;
        var held = player.mainHand;
        if (held == null) return;
        var def = itemDef(held.id);
        if (def.food is FoodDef food)
        {
            player.feed(food.hunger, food.saturation);
            foreach (var e in food.effects)
            {
                if (e.chance == 0 || gameRng.nextFloat() < e.chance)
                {
                    player.addEffect(e.effect, e.duration, e.amplifier);
                }
            }
            if (def.name == "milk_bucket")
            {
                player.clearEffects();
                player.replaceHeld(new ItemStack(iid("bucket"), 1));
            }
            else if (def.name == "chorus_fruit")
            {
                // random teleport
                for (int rep = 0; rep < 16; rep++)
                {
                    double tx = player.x + (gameRng.nextFloat() - 0.5) * 16;
                    double tz = player.z + (gameRng.nextFloat() - 0.5) * 16;
                    int ty = world.surfaceY(ifloor(tx), ifloor(tz));
                    if (ty > world.info.minY)
                    {
                        player.setPos(tx, (double)ty, tz);
                        world.hooks.playSound("item.chorus_fruit.teleport", tx, (double)ty, tz, 1, 1);
                        break;
                    }
                }
                player.consumeHeld(1);
            }
            else if (def.name.Contains("stew") || def.name.Contains("soup"))
            {
                player.replaceHeld(new ItemStack(iid("bowl"), 1));
            }
            else
            {
                player.consumeHeld(1);
            }
            world.hooks.playSound("entity.player.burp", player.x, player.y, player.z, 0.5, 1);
            ctx.advance("husbandry_eat");
        }
        else if (def.name == "potion")
        {
            var pot = potionDef(held.data.potion ?? "water");
            foreach (var e in pot.effects) { player.addEffect(e.effect, e.duration, e.amplifier); }
            player.replaceHeld(new ItemStack(iid("glass_bottle"), 1));
            world.hooks.playSound("entity.generic.drink", player.x, player.y, player.z, 0.5, 1);
        }
        player.usingItem = false;
        player.useItemTicks = 0;
    }

    public static void releaseUsingItem(InteractCtx ctx)
    {
        Player player = ctx.player;
        if (!player.usingItem) return;
        var held = player.mainHand;
        string name = held != null ? itemDef(held.id).name : "";
        if (name == "bow") { shootBow(player, player.useItemTicks); }
        else if (name == "trident") { throwTridentPlayer(player, player.useItemTicks); }
        else if (name == "crossbow")
        {
            if (player.useItemTicks >= 25 - enchLevel(held, "quick_charge") * 5)
            {
                shootBow(player, 20); // full power
                ctx.world.hooks.playSound("item.crossbow.shoot", player.x, player.y, player.z, 1, 1);
            }
        }
        player.usingItem = false;
        player.useItemTicks = 0;
    }

    // =============================================================================
    // BREAKING
    // =============================================================================
    public static void finishBreaking(InteractCtx ctx, int x, int y, int z)
    {
        World world = ctx.world; Player player = ctx.player;
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        if (id == 0) return;
        var def = blockDefs[id];
        world.hooks.playSound("block." + def.sound + ".break", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 1, 0.9);
        world.hooks.addParticles("block", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 18, 0.4, c);
        world.emitVibration((double)x, (double)y, (double)z, 12, player);

        // container contents spill
        var beBreak = world.getBlockEntity(x, y, z);
        if (beBreak != null)
        {
            var be = beBreak;
            bool isShulker = def.name.EndsWith("shulker_box") || id == (int)B.shulker_box;
            if (!isShulker && (be.type == "container" || be.type == "hopper" || be.type == "furnace" || be.type == "brewing" || be.type == "shelf" || be.type == "campfire"))
            {
                if (be.items != null)
                {
                    foreach (var s in be.items) { if (s != null) spawnItem(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, s); }
                }
                if (be.type == "furnace")
                {
                    int xp = (int)Math.Floor(be.xpBank ?? 0);
                    if (xp > 0) { spawnXP(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, xp); }
                }
            }
            if (be.type == "jukebox" && be.disc != null)
            {
                var disc = be.disc;
                spawnItem(world, (double)x + 0.5, (double)y + 1, (double)z + 0.5, disc);
            }
            if (isShulker && world.rule("doTileDrops"))
            {
                // shulker keeps contents
                int itemId = blockToItem[id];
                if (itemId >= 0)
                {
                    var stack = new ItemStack((int)itemId, 1);
                    if (be.items != null && be.items.Any(s => s != null))
                    {
                        stack.data.contents = be.items.ToList();
                    }
                    spawnItem(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, stack);
                }
                world.setBlock(x, y, z, 0);
                player.stats["blocksMined"] = (player.stats.TryGetValue("blocksMined", out var bm0) ? bm0 : 0) + 1;
                return;
            }
        }

        // door/bed upper-half cleanup
        Shape shape = shapeOf(id);
        if (shape == Shape.door)
        {
            bool upper = (c & 8) != 0;
            world.setBlock(x, upper ? y - 1 : y + 1, z, 0);
        }
        else if (shape == Shape.bed)
        {
            int f = c & 3;
            bool head = (c & 4) != 0;
            int ox = x + (head ? -1 : 1) * new[] { 0, 0, -1, 1 }[f];
            int oz = z + (head ? -1 : 1) * new[] { -1, 1, 0, 0 }[f];
            if (shapeOf(world.getBlock(ox, y, oz) >> 4) == Shape.bed) { world.setBlock(ox, y, oz, 0); }
        }
        else if (shape == Shape.tallCross)
        {
            bool upper = (c & 1) != 0;
            int oy = upper ? y - 1 : y + 1;
            if ((world.getBlock(x, oy, z) >> 4) == id) { world.setBlock(x, oy, z, 0, 2 | 4); }
        }

        // infested → spawn silverfish
        if (def.name.StartsWith("infested"))
        {
            world.setBlock(x, y, z, 0);
            spawnMob(world, "silverfish", (double)x + 0.5, (double)y, (double)z + 0.5, new SpawnOpts());
            return;
        }
        // ice melts to water if supported
        if (id == (int)B.ice && player.gameMode != GameMode.creative)
        {
            var held = player.mainHand;
            if (held == null || enchLevel(held, "silk_touch") == 0)
            {
                int below = world.getBlock(x, y - 1, z) >> 4;
                world.setBlock(x, y, z, below != 0 && blockDefs[below].solid ? (int)cell(B.water, 0) : 0);
                return;
            }
        }

        world.setBlock(x, y, z, 0);
        player.stats["blocksMined"] = (player.stats.TryGetValue("blocksMined", out var bm) ? bm : 0) + 1;

        if (player.gameMode == GameMode.creative) return;
        if (!world.rule("doTileDrops")) return;
        if (!canHarvest(player, c)) return;

        var heldB = player.mainHand;
        int fortune = heldB != null ? enchLevel(heldB, "fortune") : 0;
        bool silk = heldB != null ? enchLevel(heldB, "silk_touch") > 0 : false;
        ToolDef? toolDef = heldB != null ? itemDef(heldB.id).tool : null;

        // silk touch: drop the block itself
        if (silk)
        {
            int itemId = blockToItem[id];
            if (itemId >= 0)
            {
                spawnItem(world, (double)x + 0.5, (double)y + 0.3, (double)z + 0.5, new ItemStack((int)itemId, 1));
                damageToolForBreak(player, c);
                return;
            }
        }
        var ctx2 = new DropCtx(random: () => gameRng.nextFloat(),
                               fortune: fortune, silkTouch: silk,
                               toolType: Enum.TryParse<ToolType>(toolDef?.type ?? "none", out var tt) ? tt : ToolType.none,
                               toolTier: toolDef?.tier ?? 0,
                               shears: toolDef?.type == "shears");
        List<Drop> drops;
        if (def.drops != null)
        {
            drops = def.drops(c & 15, ctx2);
        }
        else
        {
            drops = defaultDrop(id);
        }
        foreach (var d in drops)
        {
            if (!(iidOpt(d.item) is int itemId)) continue;
            int count = 1;
            if (d.countMin == d.countMax) { count = d.countMin; }
            else { count = d.countMin + gameRng.nextInt(Math.Max(0, d.countMax - d.countMin) + 1); }
            if (d.chance != 1 && gameRng.nextFloat() > d.chance) { continue; }
            if (count > 0) { spawnItem(world, (double)x + 0.5, (double)y + 0.3, (double)z + 0.5, new ItemStack(itemId, count)); }
        }
        // ore XP
        var xpMap = new Dictionary<int, (int, int)>
        {
            { (int)B.coal_ore, (0, 2) }, { (int)B.deepslate_coal_ore, (0, 2) },
            { (int)B.diamond_ore, (3, 7) }, { (int)B.deepslate_diamond_ore, (3, 7) },
            { (int)B.emerald_ore, (3, 7) }, { (int)B.deepslate_emerald_ore, (3, 7) },
            { (int)B.lapis_ore, (2, 5) }, { (int)B.deepslate_lapis_ore, (2, 5) },
            { (int)B.redstone_ore, (1, 5) }, { (int)B.deepslate_redstone_ore, (1, 5) },
            { (int)B.nether_quartz_ore, (2, 5) }, { (int)B.nether_gold_ore, (0, 1) },
            { (int)B.spawner, (15, 43) }, { (int)B.sculk, (1, 1) }, { (int)B.sculk_sensor, (5, 5) }, { (int)B.sculk_catalyst, (5, 5) }, { (int)B.sculk_shrieker, (5, 5) },
        };
        if (!silk && xpMap.TryGetValue(id, out var loHi))
        {
            int lo = loHi.Item1, hi = loHi.Item2;
            spawnXP(world, (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, lo + gameRng.nextInt(hi - lo + 1));
        }
        damageToolForBreak(player, c);
        player.addExhaustion(0.005);
    }
    private static void damageToolForBreak(Player player, int c)
    {
        var held = player.mainHand;
        if (held == null) return;
        var toolDef = itemDef(held.id).tool;
        if (toolDef != null && blockDefs[c >> 4].hardness > 0)
        {
            player.damageHeld(toolDef.Value.type == "sword" ? 2 : 1);
        }
    }
    private static List<Drop> defaultDrop(int id)
    {
        string name = blockDefs[id].name;
        return itemExists(name) ? new List<Drop> { new Drop(name) } : new List<Drop>();
    }

    // =============================================================================
    // Bonemeal
    // =============================================================================
    public static bool applyBonemeal(World world, int x, int y, int z)
    {
        int c = world.getBlock(x, y, z);
        int id = c >> 4;
        world.hooks.addParticles("glow", (double)x + 0.5, (double)y + 0.5, (double)z + 0.5, 12, 0.5, 0);

        if (id == (int)B.wheat || id == (int)B.carrots || id == (int)B.potatoes)
        {
            int stage = Math.Min(7, (c & 7) + 2 + gameRng.nextInt(3));
            world.setBlock(x, y, z, (int)cell((ushort)id, stage));
            return true;
        }
        if (id == (int)B.beetroots || id == (int)B.sweet_berry_bush)
        {
            int stage = Math.Min(3, (c & 3) + 1);
            world.setBlock(x, y, z, (int)cell((ushort)id, stage));
            return true;
        }
        if (blockDefs[id].name.Contains("sapling") || id == (int)B.mangrove_propagule || id == (int)B.crimson_fungus || id == (int)B.warped_fungus)
        {
            if (gameRng.nextFloat() < 0.45) { growTreeAt(world, x, y, z, id); }
            return true;
        }
        if (id == (int)B.grass_block)
        {
            // sprout vegetation around
            for (int rep = 0; rep < 24; rep++)
            {
                int tx = x + gameRng.nextInt(7) - 3;
                int tz = z + gameRng.nextInt(7) - 3;
                int ty = world.surfaceY(tx, tz);
                if ((world.getBlock(tx, ty - 1, tz) >> 4) == (int)B.grass_block && world.getBlock(tx, ty, tz) == 0)
                {
                    double r = gameRng.nextFloat();
                    world.setBlock(tx, ty, tz, r < 0.8 ? (int)cell(B.short_grass) : r < 0.9 ? (int)cell(B.dandelion) : (int)cell(B.poppy));
                }
            }
            return true;
        }
        if (id == (int)B.melon_stem || id == (int)B.pumpkin_stem)
        {
            int stage = Math.Min(7, (c & 7) + 2 + gameRng.nextInt(3));
            world.setBlock(x, y, z, (int)cell((ushort)id, stage));
            return true;
        }
        if (id == (int)B.bamboo || id == (int)B.bamboo_sapling || id == (int)B.kelp || id == (int)B.sugar_cane || id == (int)B.cactus)
        {
            // trigger a few growth ticks
            if (randomTickHandlers.TryGetValue(id, out var h))
            {
                h(world, x, y, z, c);
                h(world, x, y, z, world.getBlock(x, y, z));
            }
            return true;
        }
        if (id == (int)B.short_grass)
        {
            world.setBlock(x, y, z, (int)cell(B.tall_grass, 0));
            if (world.getBlock(x, y + 1, z) == 0) { world.setBlock(x, y + 1, z, (int)cell(B.tall_grass, 1)); }
            return true;
        }
        if (id == (int)B.moss_block)
        {
            for (int rep = 0; rep < 12; rep++)
            {
                int tx = x + gameRng.nextInt(5) - 2;
                int tz = z + gameRng.nextInt(5) - 2;
                int tc = world.getBlock(tx, y, tz) >> 4;
                if (tc == (int)B.stone || tc == (int)B.dirt || tc == (int)B.grass_block || tc == (int)B.deepslate)
                {
                    world.setBlock(tx, y, tz, (int)cell(B.moss_block));
                    if (world.getBlock(tx, y + 1, tz) == 0 && gameRng.nextFloat() < 0.5)
                    {
                        world.setBlock(tx, y + 1, tz, gameRng.nextFloat() < 0.7 ? (int)cell(B.moss_carpet) : (int)cell(B.azalea));
                    }
                }
            }
            return true;
        }
        if (id == (int)B.torchflower_crop || id == (int)B.pitcher_crop)
        {
            if (randomTickHandlers.TryGetValue(id, out var h)) { h(world, x, y, z, c); }
            return true;
        }
        if (id == (int)B.cocoa)
        {
            int age = (c >> 2) & 3;
            if (age < 2)
            {
                world.setBlock(x, y, z, (int)cell(B.cocoa, (c & 3) | ((age + 1) << 2)));
                return true;
            }
        }
        return false;
    }

    // =============================================================================
    // Registration umbrella for the whole systems layer
    // =============================================================================
    private static bool systemsRegistered = false;

    public static void registerAllSystems()
    {
        if (systemsRegistered) return;
        systemsRegistered = true;
        registerExplosionHandler();
        registerCombatBindings();
        registerBlockEntityHandlers();
        registerFarmingHandlers();
        registerSupportPops();
        registerRedstoneHandlers();
        registerFluidHandlers();   // was only wired in pebsmoke — in-app water/lava never flowed
        bindBonemeal(applyBonemeal);
    }
}

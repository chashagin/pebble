// Block entity tickers — furnaces, brewing
// stands, hoppers, campfires, spawners, beacons, conduits. Registered into
// the world handler maps.
//
// Ported from Sources/PebbleCore/Systems/BlockEntities.swift.
//
// Defines static class `BlockEntitySystems` (NOTE: NOT `BlockEntities`, which is
// the data-layer factories class in BlockEntityData.cs). Holds BE *behavior*:
// smeltResultFor / fuelTime / registerBlockEntityHandlers and the private
// containerAt / insertInto helpers + the `blockEntitiesRegistered` flag.
// Surface via `global using static PebbleCore.BlockEntitySystems` so the free
// functions resolve unqualified.
//
// Cross-layer symbols referenced by name (resolve elsewhere in the assembly):
//   World, BlockEntityData, ItemStack, Entity, LivingEntity, ItemEntity,
//   itemDef, iid, maxStackOf, canMerge (ItemGlobals),
//   smeltingRecipes / SmeltRecipe (RecipeGlobals), findBrew (EnchantsPotions),
//   StackData, spawnItem / ItemEntity (Misc), spawnMob / SpawnOpts (registry),
//   gameRng (DetMath), ifloor (EntityGlobals), beTickHandlers (WorldHandlers),
//   B / blockDefs / cell (Reg / BlockCaches).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class BlockEntitySystems
{
    // -----------------------------------------------------------------------
    // Furnace family
    // -----------------------------------------------------------------------
    public static (string output, double xp)? smeltResultFor(ItemStack input, string kind)
    {
        if (input == null) return null;
        var name = itemDef(input.id).name;
        foreach (var r in smeltingRecipes)
        {
            if (r.input != name) continue;
            if (kind == "furnace") return (r.output, r.xp);
            if (kind == "blast" && (r.kind == "blast" || r.kind == "any")) return (r.output, r.xp);
            if (kind == "smoker" && r.kind == "smoke") return (r.output, r.xp);
        }
        return null;
    }
    public static int fuelTime(ItemStack s)
    {
        if (s == null) return 0;
        return itemDef(s.id).burnTime;
    }

    private static BlockEntityData containerAt(World world, int x, int y, int z)
    {
        var be = world.getBlockEntity(x, y, z);
        if (be == null) return null;
        if (be.type == "container" || be.type == "hopper" || be.type == "furnace" || be.type == "brewing")
        {
            return be.items != null ? be : null;
        }
        return null;
    }

    private static bool insertInto(BlockEntityData be, ItemStack stack, Func<int, bool> slotFilter = null)
    {
        var items = be.items;
        if (items == null) return false;
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (slotFilter != null && !slotFilter(i)) continue;
                var s = items[i];
                if (s != null && canMerge(s, stack) && s.count < maxStackOf(s))
                {
                    s.count += 1;
                    stack.count -= 1;
                    return true;
                }
            }
            for (int i = 0; i < items.Length; i++)
            {
                if (slotFilter != null && !slotFilter(i)) continue;
                if (items[i] == null)
                {
                    var one = stack.copy();
                    one.count = 1;
                    items[i] = one;
                    stack.count -= 1;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            be.items = items;
        }
    }

    private static bool blockEntitiesRegistered = false;

    public static void registerBlockEntityHandlers()
    {
        if (blockEntitiesRegistered) return;
        blockEntitiesRegistered = true;

        beTickHandlers["furnace"] = (world, be) =>
        {
            var kind = be.kind ?? "furnace";
            int speed = kind == "furnace" ? 1 : 2;
            var items = be.items ?? new ItemStack[] { null, null, null };
            try
            {
                bool wasBurning = (be.burnTime ?? 0) > 0;
                if ((be.burnTime ?? 0) > 0) { be.burnTime = (be.burnTime ?? 0) - speed; }
                var result = smeltResultFor(items[0], kind);
                var @out = items[2];
                bool canOutput = result != null && (@out == null || (itemDef(@out.id).name == result.Value.output && @out.count < maxStackOf(@out)));

                if ((be.burnTime ?? 0) <= 0 && canOutput)
                {
                    int fuel = fuelTime(items[1]);
                    if (fuel > 0)
                    {
                        be.burnTime = fuel;
                        be.burnTotal = fuel;
                        var f = items[1];
                        if (itemDef(f.id).name == "lava_bucket")
                        {
                            items[1] = new ItemStack(iid("bucket"), 1);
                        }
                        else
                        {
                            f.count -= 1;
                            if (f.count <= 0) { items[1] = null; }
                        }
                    }
                }
                if ((be.burnTime ?? 0) > 0 && canOutput)
                {
                    be.cookTime = (be.cookTime ?? 0) + speed;
                    if ((be.cookTime ?? 0) >= (be.cookTotal ?? 200))
                    {
                        be.cookTime = 0;
                        var input = items[0];
                        input.count -= 1;
                        if (input.count <= 0) { items[0] = null; }
                        if (@out != null) { @out.count += 1; }
                        else { items[2] = new ItemStack(iid(result.Value.output), 1); }
                        be.xpBank = (be.xpBank ?? 0) + result.Value.xp;
                    }
                }
                else
                {
                    be.cookTime = Math.Max(0, (be.cookTime ?? 0) - 2);
                }
                // lit state
                bool isBurning = (be.burnTime ?? 0) > 0;
                if (isBurning != wasBurning)
                {
                    int c = world.getBlock(be.x, be.y, be.z);
                    int bid = c >> 4;
                    int meta = c & 15;
                    var litMap = new Dictionary<int, ushort>
                    {
                        { (int)B.furnace, B.furnace_lit }, { (int)B.furnace_lit, B.furnace_lit },
                        { (int)B.blast_furnace, B.blast_furnace_lit }, { (int)B.blast_furnace_lit, B.blast_furnace_lit },
                        { (int)B.smoker, B.smoker_lit }, { (int)B.smoker_lit, B.smoker_lit },
                    };
                    var unlitMap = new Dictionary<int, ushort>
                    {
                        { (int)B.furnace_lit, B.furnace }, { (int)B.furnace, B.furnace },
                        { (int)B.blast_furnace_lit, B.blast_furnace }, { (int)B.blast_furnace, B.blast_furnace },
                        { (int)B.smoker_lit, B.smoker }, { (int)B.smoker, B.smoker },
                    };
                    ushort? newId = (isBurning ? litMap : unlitMap).TryGetValue(bid, out var nv) ? (ushort?)nv : null;
                    if (newId != null && (int)newId.Value != bid)
                    {
                        be.items = items;
                        world.setBlock(be.x, be.y, be.z, (int)cell(newId.Value, meta));
                        // setBlock with different id removes BEs of changed blocks — re-attach
                        world.setBlockEntity(be);
                        items = be.items ?? items;
                    }
                }
                if (isBurning && world.time % 24 == 0)
                {
                    world.hooks.addParticles("flame", (double)be.x + 0.5, (double)be.y + 0.3, (double)be.z + 0.5, 1, 0.3, 0);
                    world.hooks.playSound("block.furnace.fire_crackle", (double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 0.3, 1);
                }
            }
            finally
            {
                be.items = items;
            }
        };

        beTickHandlers["brewing"] = (world, be) =>
        {
            var items = be.items ?? new ItemStack[] { null, null, null, null, null };
            try
            {
                // refuel
                if ((be.fuel ?? 0) <= 0 && items[4] != null)
                {
                    var f = items[4];
                    if (itemDef(f.id).name == "blaze_powder")
                    {
                        be.fuel = 20;
                        f.count -= 1;
                        if (f.count <= 0) { items[4] = null; }
                    }
                }
                var ing = items[3];
                if (ing == null) { be.brewTime = 0; return; }
                var ingName = itemDef(ing.id).name;
                // check any bottle can brew
                bool anyBrewable = false;
                for (int i = 0; i < 3; i++)
                {
                    var bottle = items[i];
                    if (bottle == null) continue;
                    var bName = itemDef(bottle.id).name;
                    if (bName != "potion" && bName != "splash_potion" && bName != "lingering_potion") continue;
                    var potionId = bottle.data.potion ?? "water";
                    if (ingName == "gunpowder" && bName == "potion") { anyBrewable = true; break; }
                    if (ingName == "dragon_breath" && bName == "splash_potion") { anyBrewable = true; break; }
                    if (findBrew(potionId, ingName) != null) { anyBrewable = true; break; }
                }
                if (!anyBrewable || (be.fuel ?? 0) <= 0) { be.brewTime = 0; return; }
                if ((be.brewTime ?? 0) == 0) { be.fuel = (be.fuel ?? 0) - 1; }
                be.brewTime = (be.brewTime ?? 0) + 1;
                if ((be.brewTime ?? 0) >= 400)
                {
                    be.brewTime = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        var bottle = items[i];
                        if (bottle == null) continue;
                        var bName = itemDef(bottle.id).name;
                        var potionId = bottle.data.potion ?? "water";
                        if (ingName == "gunpowder" && bName == "potion")
                        {
                            var data = new StackData();
                            data.potion = potionId;
                            items[i] = new ItemStack(iid("splash_potion"), 1, data: data);
                        }
                        else if (ingName == "dragon_breath" && bName == "splash_potion")
                        {
                            var data = new StackData();
                            data.potion = potionId;
                            items[i] = new ItemStack(iid("lingering_potion"), 1, data: data);
                        }
                        else
                        {
                            var result = findBrew(potionId, ingName);
                            if (result != null)
                            {
                                var data = new StackData();
                                data.potion = result;
                                bottle.data = data;
                            }
                        }
                    }
                    ing.count -= 1;
                    if (ing.count <= 0) { items[3] = null; }
                    world.hooks.playSound("block.brewing_stand.brew", (double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 1, 1);
                }
            }
            finally
            {
                be.items = items;
            }
        };

        beTickHandlers["hopper"] = (world, be) =>
        {
            if ((be.cooldown ?? 0) > 0) { be.cooldown = (be.cooldown ?? 0) - 1; return; }
            int c = world.getBlock(be.x, be.y, be.z);
            if ((c >> 4) != (int)B.hopper) return;
            if ((c & 8) != 0) return; // locked by redstone
            int facing = c & 7;
            bool acted = false;

            // push: into the container we face
            int fx = be.x + new int[] { 0, 0, 0, 0, -1, 1 }[facing == 0 ? 0 : facing];
            int fy = be.y + (facing == 0 ? -1 : 0);
            int fz = be.z + new int[] { 0, 0, -1, 1, 0, 0 }[facing == 0 ? 0 : facing];
            var target = containerAt(world, fx, fy, fz);
            if (target != null)
            {
                var beItems = be.items ?? new ItemStack[] { };
                for (int i = 0; i < beItems.Length; i++)
                {
                    var s = beItems[i];
                    if (s == null) continue;
                    Func<int, bool> filter = null;
                    if (target.type == "furnace")
                    {
                        bool isFuel = fuelTime(s) > 0;
                        filter = facing == 0 ? (Func<int, bool>)(idx => idx == 0) : (idx => idx == 1 && isFuel);
                    }
                    else if (target.type == "brewing")
                    {
                        var name = itemDef(s.id).name;
                        filter = idx =>
                            (idx == 4 && name == "blaze_powder") || (idx == 3 && name != "potion") || (idx < 3 && (name == "potion" || name == "splash_potion" || name == "glass_bottle"));
                    }
                    if (insertInto(target, s, filter))
                    {
                        if (s.count <= 0) { beItems[i] = null; }
                        be.items = beItems;
                        acted = true;
                        break;
                    }
                }
            }

            // pull: from container above, or sucked item entities
            var above = containerAt(world, be.x, be.y + 1, be.z);
            if (above != null)
            {
                var aboveItems = above.items ?? new ItemStack[] { };
                for (int i = 0; i < aboveItems.Length; i++)
                {
                    if (above.type == "furnace" && i != 2) continue; // only output slot
                    var s = aboveItems[i];
                    if (s == null) continue;
                    var one = s.copy();
                    one.count = 1;
                    if (insertInto(be, one))
                    {
                        s.count -= 1;
                        if (s.count <= 0) { aboveItems[i] = null; }
                        above.items = aboveItems;
                        acted = true;
                        break;
                    }
                }
            }
            else
            {
                // vacuum item entities above
                foreach (var e in world.getEntitiesNear((double)be.x + 0.5, (double)be.y + 1, (double)be.z + 0.5, 1))
                {
                    var item = e as ItemEntity;
                    if (item == null || item.dead) continue;
                    int before = item.stack.count;
                    while (item.stack.count > 0)
                    {
                        var one = item.stack.copy();
                        one.count = 1;
                        if (!insertInto(be, one)) break;
                        item.stack.count -= 1;
                    }
                    if (item.stack.count <= 0) { item.remove(); }
                    if (item.stack.count != before) { acted = true; break; }
                }
            }
            if (acted) { be.cooldown = 8; }
        };

        beTickHandlers["campfire"] = (world, be) =>
        {
            int c = world.getBlock(be.x, be.y, be.z);
            bool lit = (c & 4) != 0;
            var items = be.items ?? new ItemStack[] { null, null, null, null };
            var times = be.times ?? new int[] { 0, 0, 0, 0 };
            // saved BEs can carry short arrays — pad before the indexed writes
            if (items.Length < 4)
            {
                var grown = new List<ItemStack>(items);
                while (grown.Count < 4) grown.Add(null);
                items = grown.ToArray();
            }
            if (times.Length < 4)
            {
                var grown = new List<int>(times);
                while (grown.Count < 4) grown.Add(0);
                times = grown.ToArray();
            }
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var s = items[i];
                    if (s == null) continue;
                    if (!lit) continue;
                    times[i] += 1;
                    if (times[i] >= 600)
                    {
                        var result = smeltResultFor(s, "smoker");
                        if (result != null)
                        {
                            spawnItem(world, (double)be.x + 0.5, (double)be.y + 1, (double)be.z + 0.5, new ItemStack(iid(result.Value.output), 1));
                        }
                        items[i] = null;
                        times[i] = 0;
                    }
                }
                if (lit && world.time % 40 == 0)
                {
                    world.hooks.addParticles("campfire_smoke", (double)be.x + 0.5, (double)be.y + 0.8, (double)be.z + 0.5, 1, 0.2, 0);
                }
            }
            finally
            {
                be.items = items;
                be.times = times;
            }
        };

        beTickHandlers["spawner"] = (world, be) =>
        {
            // require player within 16
            bool near = world.getEntitiesNear((double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 16, filter: e => (e as Entity)?.isPlayer ?? false).Count != 0;
            if (!near) return;
            if (world.difficulty == 0) return;
            if (world.time % 10 == 0)
            {
                world.hooks.addParticles("flame", (double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 1, 0.4, 0);
                world.hooks.addParticles("smoke", (double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 1, 0.4, 0);
            }
            be.delay = (be.delay ?? 0) - 1;
            if ((be.delay ?? 0) > 0) return;
            be.delay = 200 + gameRng.nextInt(600);
            var mob = be.mob ?? "zombie";
            // count nearby same-type
            int count = world.getEntitiesNear((double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 9, filter: e => (e as Entity)?.type == mob).Count;
            if (count >= 6) return;
            int n = 1 + gameRng.nextInt(4);
            for (int _i = 0; _i < n; _i++)
            {
                double px = (double)be.x + 0.5 + (gameRng.nextFloat() - 0.5) * 7;
                double pz = (double)be.z + 0.5 + (gameRng.nextFloat() - 0.5) * 7;
                int py = be.y + gameRng.nextInt(3) - 1;
                int at = world.getBlock(ifloor(px), py, ifloor(pz));
                if (at != 0) continue;
                spawnMob(world, mob, px, (double)py, pz, new SpawnOpts());
                world.hooks.addParticles("flame", px, (double)py + 0.5, pz, 8, 0.4, 0);
            }
            world.hooks.playSound("block.spawner.spawn", (double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 1, 1);
        };

        beTickHandlers["beacon"] = (world, be) =>
        {
            if (world.time % 80 != 0) return;
            // pyramid check
            int levels = 0;
            var valid = new HashSet<int> { (int)B.iron_block, (int)B.gold_block, (int)B.diamond_block, (int)B.emerald_block, (int)B.netherite_block };
            for (int layer = 1; layer <= 4; layer++)
            {
                bool complete = true;
                for (int dz = -layer; dz <= layer && complete; dz++)
                {
                    for (int dx = -layer; dx <= layer && complete; dx++)
                    {
                        if (!valid.Contains(world.getBlock(be.x + dx, be.y - layer, be.z + dz) >> 4)) { complete = false; }
                    }
                }
                if (complete) { levels = layer; }
                else { break; }
            }
            be.levels = levels;
            // sky access (glass passes the beam)
            bool skyOK = true;
            int y = be.y + 1;
            while (y < world.info.minY + world.info.height)
            {
                int bid = world.getBlock(be.x, y, be.z) >> 4;
                if (bid == 0) { y += 1; continue; }
                if (!blockDefs[bid].name.Contains("glass")) { skyOK = false; break; }
                y += 1;
            }
            if (!(levels > 0)) return;
            if (!skyOK) return;
            if (be.primary == null) return;
            var primary = be.primary;
            double range = (double)(10 + levels * 10);
            foreach (var e in world.getEntitiesNear((double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, range, filter: ent => (ent as Entity)?.isPlayer ?? false))
            {
                var p = e as LivingEntity;
                if (p == null) continue;
                p.addEffect(primary, 260, levels >= 4 && be.secondary == primary ? 1 : 0, ambient: true);
                if (levels >= 4 && be.secondary == "regeneration") { p.addEffect("regeneration", 260, 0, ambient: true); }
            }
        };

        beTickHandlers["conduit"] = (world, be) =>
        {
            if (world.time % 80 != 0) return;
            // count prismarine frame blocks in 5×5×5 shell
            int frame = 0;
            var valid = new HashSet<int> { (int)B.prismarine, (int)B.prismarine_bricks, (int)B.dark_prismarine, (int)B.sea_lantern };
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (Math.Abs(dx) != 2 && Math.Abs(dy) != 2 && Math.Abs(dz) != 2) continue;
                        if (valid.Contains(world.getBlock(be.x + dx, be.y + dy, be.z + dz) >> 4)) { frame += 1; }
                    }
                }
            }
            be.active = frame >= 16;
            if (be.active == true)
            {
                double range = (double)((frame / 7) * 16);
                foreach (var e in world.getEntitiesNear((double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, range, filter: ent => (ent as Entity)?.isPlayer ?? false))
                {
                    var p = e as LivingEntity;
                    if (p == null) continue;
                    if (p.inWater || world.rainLevel > 0.5) { p.addEffect("conduit_power", 260, 0, ambient: true); }
                }
                world.hooks.playSound("block.conduit.ambient", (double)be.x + 0.5, (double)be.y + 0.5, (double)be.z + 0.5, 0.5, 1);
            }
        };
    }
}

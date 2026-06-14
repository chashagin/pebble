// Combat — player attacks (1.9 cooldown, crits,
// sweeping, knockback, fire aspect), mob ranged-attack bindings, evoker
// fangs, lightning conversions, breaking and harvesting math.
//
// Defines static class CombatGlobals (surface via global using static
// PebbleCore.CombatGlobals) holding the module-level free funcs/vars:
//   typeMatchesAny (private), playerAttack, shootBow, throwTridentPlayer,
//   tickFangs, breakSpeed, canHarvest, registerCombatBindings, and the private
//   module-global fangQueue.
// Cross-layer symbols referenced by name (resolved elsewhere in the assembly):
//   Player, Entity/LivingEntity, ItemStack, World, ArrowEntity, TridentEntity,
//   ThrownSnowball, LlamaSpit, LightningBolt, Creeper, SpawnOpts, spawnMob,
//   itemDef/enchLevel/iid (ItemGlobals), blockDefs/B (Reg), detSin/detCos/detHyp
//   (DetMath), ifloor (EntityGlobals), gameRng (DetMath), tickPendingTimeouts
//   (NetherMobsGlobals), bindShootArrow/bindThrowTrident (MonstersGlobals),
//   bindThrowSnowball/bindSpit (VillagersGlobals), bindFangs (MonstersGlobals),
//   bindSpawnLightning (ProjectilesGlobals), bindLightningConversion (MiscGlobals).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class CombatGlobals
{
    private static bool typeMatchesAny(string type, string[] words)
    {
        foreach (var w in words) { if (type.Contains(w)) return true; }
        return false;
    }

    // ---------------------------------------------------------------------------
    // Player melee attack
    // ---------------------------------------------------------------------------
    public static void playerAttack(Player player, Entity target)
    {
        double strength = player.attackStrength();
        player.resetAttackCooldown();
        player.attackAnim = 1;
        player.lastHurtTarget = target;

        var held = player.mainHand;
        var tool = held != null ? itemDef(held.id).tool : null;
        double dmg = 1 + (tool?.attackDamage ?? 0);
        // enchants
        if (held != null)
        {
            dmg += (double)enchLevel(held, "sharpness") * 0.5 + (enchLevel(held, "sharpness") > 0 ? 0.5 : 0);
            if (typeMatchesAny(target.type, new[] { "zombie", "skeleton", "wither", "phantom", "drowned", "husk", "stray", "zoglin" }))
            {
                dmg += (double)enchLevel(held, "smite") * 2.5;
            }
            if (typeMatchesAny(target.type, new[] { "spider", "silverfish", "endermite", "bee" }))
            {
                dmg += (double)enchLevel(held, "bane_of_arthropods") * 2.5;
            }
        }
        dmg += 3 * (double)player.effectLevel("strength");
        dmg -= 4 * (double)player.effectLevel("weakness");
        // cooldown scaling
        dmg *= 0.2 + strength * strength * 0.8;

        // crit: falling, not sprinting, full strength
        bool crit = strength > 0.9 && player.fallDistance > 0 && !player.onGround && !player.inWater && !player.sprinting && !player.hasEffect("blindness");
        if (crit)
        {
            dmg *= 1.5;
            player.world.hooks.addParticles("crit", target.x, target.y + target.height * 0.7, target.z, 10, 0.4, 0);
            player.world.hooks.playSound("entity.player.attack.crit", player.x, player.y, player.z, 0.8, 1);
        }
        else if (strength > 0.9)
        {
            player.world.hooks.playSound("entity.player.attack.strong", player.x, player.y, player.z, 0.8, 1);
        }
        else
        {
            player.world.hooks.playSound("entity.player.attack.weak", player.x, player.y, player.z, 0.6, 1);
        }

        bool hurt = target.hurt(Math.Max(0, dmg), "mob", player);
        if (hurt)
        {
            // knockback enchant + sprint knockback (capture sprint state first —
            // the kb branch clears it, and the sweep gate below must see it)
            bool wasSprinting = player.sprinting;
            int kb = (held != null ? enchLevel(held, "knockback") : 0) + (wasSprinting ? 1 : 0);
            if (kb > 0)
            {
                target.vx += -detSin(player.yaw) * (double)kb * 0.4;
                target.vz += detCos(player.yaw) * (double)kb * 0.4;
                player.sprinting = false;
            }
            // fire aspect
            int fa = held != null ? enchLevel(held, "fire_aspect") : 0;
            if (fa > 0) { target.fireTicks = Math.Max(target.fireTicks, fa * 80); }
            // thorns on target
            if (target is LivingEntity liv)
            {
                int thorns = 0;
                foreach (var a in liv.armor) { if (a != null) thorns += enchLevel(a, "thorns"); }
                if (thorns > 0 && gameRng.nextFloat() < (double)thorns * 0.15)
                {
                    player.hurt((double)(1 + gameRng.nextInt(4)), "thorns", target);
                }
            }
            // sweeping (sword, full strength, on ground-ish)
            if (tool?.type == "sword" && strength > 0.9 && !crit && !wasSprinting && player.onGround)
            {
                int sweepLvl = held != null ? enchLevel(held, "sweeping_edge") : 0;
                double sweepDmg = 1 + (sweepLvl > 0 ? dmg * (double)sweepLvl / (double)(sweepLvl + 1) : 0);
                foreach (var e in player.world.getEntitiesNear(target.x, target.y, target.z, 1.5))
                {
                    if (!(e is LivingEntity other) || ReferenceEquals(other, target) || ReferenceEquals(other, player)) continue;
                    other.hurt(sweepDmg, "mob", player);
                }
                player.world.hooks.addParticles("sweep", player.x - detSin(player.yaw) * 1.5, player.y + 1.1, player.z + detCos(player.yaw) * 1.5, 1, 0, 0);
                player.world.hooks.playSound("entity.player.attack.sweep", player.x, player.y, player.z, 0.8, 1);
            }
            // durability
            if (held != null && tool != null)
            {
                player.damageHeld(tool.Value.type == "sword" || tool.Value.type == "trident" ? 1 : 2);
            }
            player.addExhaustion(0.1);
        }
    }

    // ---------------------------------------------------------------------------
    // Bow / crossbow shooting (player)
    // ---------------------------------------------------------------------------
    public static void shootBow(Player player, int chargeTicks)
    {
        var held = player.mainHand;
        if (held == null) return;
        double power = (double)chargeTicks / 20;
        power = (power * power + power * 2) / 3;
        if (power < 0.1) return;
        if (power > 1) power = 1;

        bool infinity = enchLevel(held, "infinity") > 0;
        bool hasArrow = player.gameMode == 1 || infinity || player.countItem(iid("arrow")) > 0 ||
            player.countItem(iid("spectral_arrow")) > 0 || player.countItem(iid("tipped_arrow")) > 0;
        if (!hasArrow) return;

        // find arrow type
        string arrowItem = "arrow";
        string potionId = null;
        foreach (var name in new[] { "tipped_arrow", "spectral_arrow", "arrow" })
        {
            if (player.countItem(iid(name)) > 0)
            {
                arrowItem = name;
                break;
            }
        }
        if (arrowItem == "tipped_arrow")
        {
            foreach (var s in player.inventory)
            {
                if (s != null && s.id == iid("tipped_arrow")) { potionId = s.data.potion; break; }
            }
        }
        if (player.gameMode != 1 && !(infinity && arrowItem == "arrow"))
        {
            player.removeItems(iid(arrowItem), 1);
        }

        var arrow = new ArrowEntity(player.world);
        arrow.shootFrom(player, player.pitch, player.yaw, power * 3, 1);
        arrow.damage = 2 + (double)enchLevel(held, "power") * 0.5 + (enchLevel(held, "power") > 0 ? 0.5 : 0);
        arrow.critical = power >= 1;
        arrow.punchLevel = enchLevel(held, "punch");
        arrow.flame = enchLevel(held, "flame") > 0;
        arrow.potionId = potionId;
        arrow.spectral = arrowItem == "spectral_arrow";
        arrow.pickupable = !(infinity && arrowItem == "arrow") && player.gameMode != 1;
        player.world.addEntity(arrow);
        player.world.hooks.playSound("entity.arrow.shoot", player.x, player.y, player.z, 1, 1 / (System.Random.Shared.NextDouble() * 0.4 + 1.2) + power * 0.5);
        player.damageHeld(1);
        player.stats["arrowsShot"] = (player.stats.TryGetValue("arrowsShot", out var __as) ? __as : 0) + 1;
    }

    public static void throwTridentPlayer(Player player, int chargeTicks)
    {
        var held = player.mainHand;
        if (held == null || chargeTicks < 10) return;
        int riptide = enchLevel(held, "riptide");
        if (riptide > 0)
        {
            if (!player.inWater && player.world.rainLevel < 0.2) return;
            // launch player
            double lookX = -detSin(player.yaw) * detCos(player.pitch);
            double lookY = -detSin(player.pitch);
            double lookZ = detCos(player.yaw) * detCos(player.pitch);
            double f = 1.5 * (double)(1 + riptide) / 4;
            player.vx += lookX * f * 2;
            player.vy += lookY * f * 2;
            player.vz += lookZ * f * 2;
            player.damageHeld(1);
            player.world.hooks.playSound("item.trident.riptide_1", player.x, player.y, player.z, 1, 1);
            return;
        }
        var trident = new TridentEntity(player.world);
        trident.shootFrom(player, player.pitch, player.yaw, 2.5, 1);
        trident.stack = held.copy();
        trident.loyalty = enchLevel(held, "loyalty");
        player.world.addEntity(trident);
        player.world.hooks.playSound("item.trident.throw", player.x, player.y, player.z, 1, 1);
        player.consumeHeld(1);
    }

    // ---------------------------------------------------------------------------
    // Evoker fangs: delayed bite at position
    // ---------------------------------------------------------------------------
    private static List<(World world, double x, double y, double z, int time, Entity owner)> fangQueue = new();

    public static void tickFangs(World world)
    {
        tickPendingTimeouts(world);
        int i = fangQueue.Count - 1;
        while (i >= 0)
        {
            var f = fangQueue[i];
            if (!ReferenceEquals(f.world, world)) { i -= 1; continue; }
            if (world.time >= f.time)
            {
                fangQueue.RemoveAt(i);
                // snap to ground
                int gy = ifloor(f.y);
                for (int _i = 0; _i < 4; _i++)
                {
                    if ((world.getBlock(ifloor(f.x), gy - 1, ifloor(f.z)) >> 4) != 0) break;
                    gy -= 1;
                }
                world.hooks.addParticles("crit", f.x, (double)gy + 0.3, f.z, 8, 0.3, 0);
                world.hooks.playSound("entity.evoker_fangs.attack", f.x, (double)gy, f.z, 1, 1);
                foreach (var e in world.getEntitiesNear(f.x, (double)gy + 0.5, f.z, 1.2))
                {
                    if (!(e is LivingEntity liv) || ReferenceEquals(liv, f.owner)) continue;
                    liv.hurt(6, "magic", f.owner);
                }
            }
            i -= 1;
        }
    }

    // ---------------------------------------------------------------------------
    // Mining math
    // ---------------------------------------------------------------------------
    public static double breakSpeed(Player player, int cellVal)
    {
        int bid = cellVal >> 4;
        var def = blockDefs[bid];
        if (def.hardness < 0) return 0;
        if (def.hardness == 0) return double.PositiveInfinity;
        var held = player.mainHand;
        var tool = held != null ? itemDef(held.id).tool : null;
        double speed = 1.0;
        bool matches = tool != null && (tool.Value.type == def.tool.ToString() ||
            (tool.Value.type == "sword" && (bid == (int)B.cobweb || bid == (int)B.bamboo)) ||
            (tool.Value.type == "shears" && (def.tool == ToolType.shears || def.name.Contains("leaves") || def.name.Contains("wool"))));
        if (matches && tool != null)
        {
            speed = tool.Value.speed;
            int eff = held != null ? enchLevel(held, "efficiency") : 0;
            if (eff > 0) speed += (double)(eff * eff + 1);
        }
        if (player.hasEffect("haste")) { speed *= 1 + 0.2 * (double)player.effectLevel("haste"); }
        if (player.hasEffect("mining_fatigue"))
        {
            // exact 0.3^n via repeated multiply (Math.pow is engine-specific)
            double fatigue = 1.0;
            for (int _i = 0; _i < Math.Min(4, player.effectLevel("mining_fatigue")); _i++) fatigue *= 0.3;
            speed *= fatigue;
        }
        if (player.underwater && !(player.armor[0] != null && enchLevel(player.armor[0], "aqua_affinity") > 0)) speed /= 5;
        if (!player.onGround) speed /= 5;

        bool canHarvestNow = !def.requiresTool || (matches && (tool?.tier ?? 0) >= def.tier);
        double divisor = canHarvestNow ? 30 : 100;
        return speed / def.hardness / divisor;
    }

    public static bool canHarvest(Player player, int cellVal)
    {
        int bid = cellVal >> 4;
        var def = blockDefs[bid];
        if (!def.requiresTool) return true;
        var held = player.mainHand;
        if (held == null) return false;
        var tool = itemDef(held.id).tool;
        if (tool == null) return false;
        bool matches = tool.Value.type == def.tool.ToString() || (tool.Value.type == "sword" && bid == (int)B.cobweb);
        return matches && tool.Value.tier >= def.tier;
    }

    // ---------------------------------------------------------------------------
    // Bindings (baseline runs these at module import; Swift registers explicitly)
    // ---------------------------------------------------------------------------
    public static void registerCombatBindings()
    {
        bindShootArrow((from, at, power, damage) =>
        {
            var arrow = new ArrowEntity(from.world);
            double dx = at.x - from.x;
            double dy = at.y + at.height / 3 - (from.y + from.height * 0.85);
            double dz = at.z - from.z;
            double horiz = detHyp(dx, dz);
            arrow.setPos(from.x, from.y + from.height * 0.85, from.z);
            arrow.owner = from;
            arrow.shoot(dx, dy + horiz * 0.2, dz, 1.6, (double)(14 - from.world.difficulty * 4));
            arrow.damage = damage / 2;
            arrow.pickupable = false;
            from.world.addEntity(arrow);
            from.world.hooks.playSound("entity.skeleton.shoot", from.x, from.y, from.z, 1, 1);
        });

        bindThrowTrident((from, at) =>
        {
            var t = new TridentEntity(from.world);
            double dx = at.x - from.x, dy = at.eyeY() - (from.y + from.height * 0.85), dz = at.z - from.z;
            t.setPos(from.x, from.y + from.height * 0.85, from.z);
            t.owner = from;
            t.shoot(dx, dy + detHyp(dx, dz) * 0.15, dz, 1.6, 6);
            t.stack = null;
            from.world.addEntity(t);
            from.world.hooks.playSound("item.trident.throw", from.x, from.y, from.z, 1, 1);
        });

        bindThrowSnowball((from, at) =>
        {
            var s = new ThrownSnowball(from.world);
            double dx = at.x - from.x, dy = at.eyeY() - 1 - (from.y + from.height * 0.85), dz = at.z - from.z;
            s.setPos(from.x, from.y + from.height * 0.85, from.z);
            s.owner = from;
            s.shoot(dx, dy + detHyp(dx, dz) * 0.12, dz, 1.5, 10);
            from.world.addEntity(s);
            from.world.hooks.playSound("entity.snowball.throw", from.x, from.y, from.z, 1, 0.6);
        });

        bindSpit((from, at) =>
        {
            var s = new LlamaSpit(from.world);
            double dx = at.x - from.x, dy = at.eyeY() - (from.y + from.height * 0.85), dz = at.z - from.z;
            s.setPos(from.x, from.y + from.height * 0.85, from.z);
            s.owner = from;
            s.shoot(dx, dy + detHyp(dx, dz) * 0.1, dz, 1.5, 8);
            from.world.addEntity(s);
            from.world.hooks.playSound("entity.llama.spit", from.x, from.y, from.z, 1, 1);
        });

        bindFangs((world, x, y, z, delay, owner) =>
        {
            fangQueue.Add((world: world, x: x, y: y, z: z, time: world.time + delay, owner: owner));
        });

        bindSpawnLightning((world, x, y, z) =>
        {
            var bolt = new LightningBolt(world);
            bolt.setPos(x, y, z);
            world.addEntity(bolt);
        });

        bindLightningConversion(e =>
        {
            string t = e.type;
            var w = e.world;
            if (t == "pig")
            {
                var zp = spawnMob(w, "zombified_piglin", e.x, e.y, e.z, new SpawnOpts());
                if (zp != null) e.remove();
            }
            else if (t == "creeper")
            {
                if (e is Creeper cr) cr.charged = true;
                e.data.charged = true;
            }
            else if (t == "villager")
            {
                var witch = spawnMob(w, "witch", e.x, e.y, e.z, new SpawnOpts());
                if (witch != null) e.remove();
            }
            else if (t == "mooshroom")
            {
                // toggles brown/red — flavor
                e.data.brown = !(e.data.brown ?? false);
            }
        });
    }
}

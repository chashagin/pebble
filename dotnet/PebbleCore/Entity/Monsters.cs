// Hostile mobs — undead, spiders, creepers,
// slimes, witches, endermen, phantoms, guardians, shulkers, silverfish, and
// the illager family.
//
// Defines static class MonstersGlobals (surface via global using static
// PebbleCore.MonstersGlobals) holding the module-level free funcs/vars:
//   isPlayerTarget, targetsVillagers, blockNameOf, spawnFangsFn/bindFangs,
//   shootArrowFn/bindShootArrow, throwTridentFn/bindThrowTrident.
// All mob types are at namespace level.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public static class MonstersGlobals
{
    public static readonly Func<LivingEntity, bool> isPlayerTarget = e => e.isPlayer && !e.dead;
    public static readonly Func<LivingEntity, bool> targetsVillagers = e =>
        e.isPlayer || e.type == "villager" || e.type == "wandering_trader" || e.type == "iron_golem";

    public static string blockNameOf(int id)
    {
        return id >= 0 && id < blockDefs.Count ? blockDefs[id].name : "";
    }

    // late-bound combat helpers
    public static Action<World, double, double, double, int, Entity> spawnFangsFn;
    public static void bindFangs(Action<World, double, double, double, int, Entity> fn) { spawnFangsFn = fn; }

    public static Action<Mob, LivingEntity, double, double> shootArrowFn;
    public static void bindShootArrow(Action<Mob, LivingEntity, double, double> fn) { shootArrowFn = fn; }

    public static Action<Mob, LivingEntity> throwTridentFn;
    public static void bindThrowTrident(Action<Mob, LivingEntity> fn) { throwTridentFn = fn; }
}

public class Monster : Mob
{
    public Monster(World world) : base(world)
    {
        category = "monster";
        xpReward = 5;
    }

    public void addMonsterGoals(double speed = 1.05)
    {
        goals.add(new FloatGoal(this, 0));
        goals.add(new MeleeAttackGoal(this, 2, speed));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        goals.add(new RandomLookGoal(this, 8));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.isPlayerTarget, followRange));
    }
}

// ZOMBIES ----------------------------------------------------------------------
public class Zombie : Monster
{
    public override string type => "zombie";
    public int conversionTime = -1;

    public Zombie(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 20; health = 20;
        speed = 0.095;
        attackDamage = 3;
        burnsInSun = true;
        if (gameRng.nextFloat() < 0.05) baby = true;
        if (baby) speed *= 1.5;
        addMonsterGoals();
        targetGoals.add(new NearestTargetGoal(this, 3, e =>
            e.type == "villager" || e.type == "iron_golem" || e.type == "snow_golem"
        , 16));
    }

    public override void tick()
    {
        base.tick();
        // drowned conversion
        if (type == "zombie" && underwater)
        {
            if (conversionTime < 0) { conversionTime = 600; }
            else
            {
                conversionTime -= 1;
                if (conversionTime <= 0)
                {
                    var d = spawnMobFn?.Invoke(world, "drowned", x, y, z, new SpawnOpts(baby: baby));
                    if (d != null) remove();
                }
            }
        }
        else if (type == "zombie")
        {
            conversionTime = -1;
        }
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("rotten_flesh", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("iron_ingot", chance: 0.025, lootingBonus: 0.01),
            new DropEntry("carrot", chance: 0.025, lootingBonus: 0.01),
            new DropEntry("potato", chance: 0.025, lootingBonus: 0.01),
        };
    }
}

public sealed class Husk : Zombie
{
    public override string type => "husk";

    public Husk(World world) : base(world)
    {
        burnsInSun = false;
    }

    public override void doMeleeAttack(LivingEntity target)
    {
        base.doMeleeAttack(target);
        target.addEffect("hunger", 140, 0);
    }
}

public sealed class Drowned : Zombie
{
    public override string type => "drowned";
    public bool hasTrident = false;

    public Drowned(World world) : base(world)
    {
        hasTrident = gameRng.nextFloat() < 0.15;   // baseline field-init order
        breathesWater = true;
        burnsInSun = true;
        if (hasTrident)
        {
            goals.add(new RangedAttackGoal(this, 1, 40, 10, (t, _) =>
            {
                MonstersGlobals.throwTridentFn?.Invoke(this, t);
            }));
        }
        goals.add(new RandomSwimGoal(this, 5, 1, 30));
    }

    public override List<DropEntry> drops()
    {
        var d = new List<DropEntry> { new DropEntry("rotten_flesh", min: 0, max: 2, lootingBonus: 1) };
        d.Add(new DropEntry("copper_ingot", chance: 0.11, lootingBonus: 0.02));
        if (hasTrident) d.Add(new DropEntry("trident", chance: 0.085));
        return d;
    }
}

public sealed class ZombieVillagerMob : Zombie
{
    public override string type => "zombie_villager";
    public int curing = -1;

    public ZombieVillagerMob(World world) : base(world) { }

    public override bool interact(Entity player, ItemStack stack)
    {
        // cure with golden apple while weak
        if (stack != null && itemDef(stack.id).name == "golden_apple" && hasEffect("weakness"))
        {
            (player as LivingEntity)?.consumeHeld(1);
            curing = 2000 + gameRng.nextInt(2000);
            addEffect("strength", curing, 0);
            world.hooks.playSound("entity.zombie_villager.cure", x, y, z, 1, 1);
            return true;
        }
        return false;
    }

    public override void tick()
    {
        base.tick();
        if (curing > 0)
        {
            curing -= 1;
            if (curing == 0)
            {
                var v = spawnMobFn?.Invoke(world, "villager", x, y, z, new SpawnOpts());
                if (v != null) remove();
            }
        }
    }
}

// SKELETONS --------------------------------------------------------------------
public class Skeleton : Monster
{
    public override string type => "skeleton";

    public Skeleton(World world) : base(world)
    {
        width = 0.6; height = 1.99;
        maxHealth = 20; health = 20;
        speed = 0.1;
        burnsInSun = true;
        goals.add(new FloatGoal(this, 0));
        goals.add(new RangedAttackGoal(this, 2, 30, 15, (t, power) =>
        {
            MonstersGlobals.shootArrowFn?.Invoke(this, t, power, 2 + (double)this.world.difficulty);
        }));
        goals.add(new AvoidEntityGoal(this, 3, e => e.type == "wolf", 6, 1.2));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        goals.add(new RandomLookGoal(this, 8));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.isPlayerTarget, 16));
        targetGoals.add(new NearestTargetGoal(this, 3, e => e.type == "iron_golem", 16));
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("arrow", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("bone", min: 0, max: 2, lootingBonus: 1),
        };
    }
}

public sealed class Stray : Skeleton
{
    public override string type => "stray";

    public Stray(World world) : base(world) { }

    public override List<DropEntry> drops()
    {
        var d = base.drops();
        d.Add(new DropEntry("tipped_arrow", chance: 0.5));
        return d;
    }
}

// CREEPER ------------------------------------------------------------------------
public sealed class Creeper : Monster
{
    public override string type => "creeper";
    public int swellTicks = 0;
    public bool charged = false;

    public Creeper(World world) : base(world)
    {
        width = 0.6; height = 1.7;
        maxHealth = 20; health = 20;
        speed = 0.1;
        goals.add(new FloatGoal(this, 0));
        goals.add(new SwellGoal(this, 1));
        goals.add(new MeleeAttackGoal(this, 2, 1.0));
        goals.add(new AvoidEntityGoal(this, 3, e => e.type == "cat" || e.type == "ocelot", 6, 1.3));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        goals.add(new RandomLookGoal(this, 8));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.isPlayerTarget, 16));
    }

    public override void doMeleeAttack(LivingEntity target) { } // creepers don't melee

    public override void tick()
    {
        base.tick();
        if (swellTicks > 0)
        {
            data.swelling = (double)swellTicks / 30;
            if (swellTicks == 1) { world.hooks.playSound("entity.creeper.primed", x, y, z, 1, 0.6); }
            swellTicks += 1;
            if (swellTicks > 30)
            {
                remove();
                explodeFn?.Invoke(world, x, y + 0.5, z, charged ? 6 : 3, false, this);
            }
        }
        else
        {
            data.swelling = 0;
        }
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("gunpowder", min: 0, max: 2, lootingBonus: 1) };
    }
}

internal sealed class SwellGoal : Goal
{
    public SwellGoal(Mob mob, int priority) : base(mob, priority) { }

    public override bool canUse()
    {
        var c = mob as Creeper;
        if (c == null) return false;
        var t = c.target;
        if (t == null) return false;
        return !t.dead && c.distanceToSq(t) < 9;
    }

    public override void tick()
    {
        var c = mob as Creeper;
        if (c == null) return;
        var t = c.target;
        if (t != null && c.distanceToSq(t) < 9 && c.canSee(t))
        {
            if (c.swellTicks == 0) c.swellTicks = 1;
            c.nav.stop();
        }
        else
        {
            c.swellTicks = 0;
        }
    }

    public override void stop() { var c = mob as Creeper; if (c != null) c.swellTicks = 0; }
}

// SPIDERS -----------------------------------------------------------------------
public class Spider : Monster
{
    public override string type => "spider";

    public Spider(World world) : base(world)
    {
        width = 1.4; height = 0.9;
        maxHealth = 16; health = 16;
        speed = 0.13;
        attackDamage = 2;
        addMonsterGoals(1.1);
    }

    public override void tick()
    {
        base.tick();
        // wall climbing
        if (horizontalCollision) vy = 0.2;
    }

    public bool canTargetInLight()
    {
        // spiders neutral in daylight
        return world.lightAt(ifloor(x), ifloor(y), ifloor(z)) <= 7;
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("string", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("spider_eye", chance: 0.33, lootingBonus: 0.05),
        };
    }
}

public sealed class CaveSpider : Spider
{
    public override string type => "cave_spider";

    public CaveSpider(World world) : base(world)
    {
        width = 0.7; height = 0.5;
        maxHealth = 12; health = 12;
    }

    public override void doMeleeAttack(LivingEntity target)
    {
        base.doMeleeAttack(target);
        target.addEffect("poison", world.difficulty >= 2 ? 140 : 70, 0);
    }
}

// SLIME --------------------------------------------------------------------------
public class Slime : Monster
{
    public override string type => "slime";
    public int size = 2;
    public int jumpDelay = 0;

    public Slime(World world) : base(world)
    {
        setSize(new int[] { 1, 2, 4 }[gameRng.nextInt(3)]);
        targetGoals.add(new NearestTargetGoal(this, 1, MonstersGlobals.isPlayerTarget, 16));
        targetGoals.add(new HurtByTargetGoal(this, 2));
    }

    public void setSize(int s)
    {
        size = s;
        width = 0.51 * (double)s;
        height = 0.51 * (double)s;
        maxHealth = (double)(s * s); health = (double)(s * s);
        attackDamage = (double)s;
        xpReward = s;
    }

    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) return;
        targetGoals.tick(2, age);
        // hop toward target
        if (onGround)
        {
            jumpDelay -= 1;
            if (jumpDelay <= 0)
            {
                jumpDelay = 10 + rng.nextInt(40);
                var t = target;
                if (t != null && !t.dead)
                {
                    jumpDelay = 6;
                    double dx = t.x - x, dz = t.z - z;
                    var d = Math.Sqrt(dx * dx + dz * dz);
                    if (d == 0) d = 1;
                    vx = dx / d * 0.25;
                    vz = dz / d * 0.25;
                }
                else
                {
                    var ang = rng.nextFloat() * Math.PI * 2;
                    vx = detSin(ang) * 0.18;
                    vz = detCos(ang) * 0.18;
                }
                vy = 0.42;
                world.hooks.playSound("entity.slime.jump", x, y, z, 0.6, 0.8 + rng.nextFloat() * 0.4);
            }
            else
            {
                vx *= 0.5; vz *= 0.5;
            }
        }
        move(vx, vy, vz);
        vy -= 0.08;
        vy *= 0.98;
        // damage on touch
        {
            var t = target;
            if (t != null && !t.dead && distanceToSq(t) < (width * 0.8) * (width * 0.8) + 1 && age % 20 == 0)
            {
                t.hurt(attackDamage, "mob", this);
            }
        }
    }

    public override void die(string source, Entity attacker = null)
    {
        base.die(source, attacker);
        if (size > 1)
        {
            // baseline: `for (let i = 0; i < 2 + this.rng.nextInt(2); i++)` — the
            // bound REROLLS on every condition check; each check consumes rng
            var i = 0;
            while (i < 2 + rng.nextInt(2))
            {
                spawnMobFn?.Invoke(world, type, x + (rng.nextFloat() - 0.5), y + 0.5, z + (rng.nextFloat() - 0.5), new SpawnOpts(size: size / 2));
                i += 1;
            }
        }
    }

    public override List<DropEntry> drops()
    {
        return size == 1
            ? new List<DropEntry> { new DropEntry("slime_ball", min: 0, max: 2, lootingBonus: 1) }
            : new List<DropEntry>();
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        if (d.TryGetValue("data", out var ddObj) && ddObj is Dictionary<string, object> dd
            && dd.TryGetValue("size", out var sObj) && sObj != null)
        {
            int s = inum(sObj);
            if (s != 0) setSize(s);
        }
    }
}

// WITCH --------------------------------------------------------------------------
public sealed class Witch : Monster
{
    public override string type => "witch";
    public int drinkTime = 0;

    public Witch(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 26; health = 26;
        speed = 0.1;
        goals.add(new FloatGoal(this, 0));
        goals.add(new RangedAttackGoal(this, 2, 50, 10, (t, _) =>
        {
            var pot = new ThrownPotion(this.world);
            var dy = t.y + t.height * 0.5 - (this.y + 1.4);
            pot.potionId = t.hasEffect("slowness") ? "harming" : (this.distanceToSq(t) > 64 ? "slowness" : "poison");
            var horiz = Math.Sqrt((t.x - this.x) * (t.x - this.x) + (t.z - this.z) * (t.z - this.z));
            pot.shootFrom(this, -detAtan2(dy + 0.3, horiz), detAtan2(-(t.x - this.x), t.z - this.z), 0.75, 8);
            pot.gravity = 0.05;
            this.world.addEntity(pot);
            this.world.hooks.playSound("entity.witch.throw", this.x, this.y, this.z, 1, 1);
        }));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.isPlayerTarget, 16));
    }

    public override void tick()
    {
        base.tick();
        // drink potions defensively
        if (drinkTime > 0) { drinkTime -= 1; }
        else
        {
            if (fireTicks > 0 && !hasEffect("fire_resistance"))
            {
                addEffect("fire_resistance", 400, 0);
                drinkTime = 40;
            }
            else if (health < maxHealth * 0.75 && rng.nextFloat() < 0.05)
            {
                heal(6);
                drinkTime = 40;
                world.hooks.playSound("entity.witch.drink", x, y, z, 1, 1);
            }
        }
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("glass_bottle", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("glowstone_dust", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("gunpowder", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("redstone", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("spider_eye", min: 0, max: 2, chance: 0.5),
            new DropEntry("sugar", min: 0, max: 2, chance: 0.5),
            new DropEntry("stick", min: 0, max: 2, chance: 0.5),
        };
    }
}

// ENDERMAN ------------------------------------------------------------------------
public sealed class Enderman : Monster
{
    public override string type => "enderman";
    public int carrying = 0; // block cell

    public Enderman(World world) : base(world)
    {
        width = 0.6; height = 2.9;
        maxHealth = 40; health = 40;
        speed = 0.15;
        attackDamage = 7;
        goals.add(new FloatGoal(this, 0));
        goals.add(new MeleeAttackGoal(this, 2, 1.2));
        goals.add(new StrollGoal(this, 7, 0.8));
        goals.add(new LookAtPlayerGoal(this, 8));
        targetGoals.add(new HurtByTargetGoal(this, 1));
    }

    public override void tick()
    {
        base.tick();
        // stare aggro
        if (target == null && age % 10 == 0)
        {
            foreach (var e in world.getEntitiesNear(x, y, z, 32, filter: o => (o as Entity)?.isPlayer ?? false))
            {
                var p = e as LivingEntity;
                if (p == null) continue;
                if (p.gameMode == 1) continue;
                if (p.wearingPumpkin) continue;
                // is the player looking at my head?
                double dx = x - p.x, dy = (y + 2.55) - p.eyeY(), dz = z - p.z;
                var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var lookX = -detSin(p.yaw) * detCos(p.pitch);
                var lookY = -detSin(p.pitch);
                var lookZ = detCos(p.yaw) * detCos(p.pitch);
                var dot = (dx / dist) * lookX + (dy / dist) * lookY + (dz / dist) * lookZ;
                if (dot > 0.99 && p.canSee(this))
                {
                    setTarget(p);
                    world.hooks.playSound("entity.enderman.stare", p.x, p.y, p.z, 1, 1);
                }
            }
        }
        // water hurts
        if (inWater || (world.rainLevel > 0.5 && world.canSeeSky(ifloor(x), ifloor(y), ifloor(z))))
        {
            hurt(1, "drown");
            teleportRandomly();
        }
        // teleport when hurt by projectile or to chase
        {
            var t = target;
            if (t != null && age % 30 == 0 && distanceToSq(t) > 256)
            {
                teleportNear(t);
            }
        }
        // pick up / place blocks rarely
        if (world.rule("mobGriefing") && rng.nextFloat() < 0.002)
        {
            if (carrying == 0)
            {
                var bx = ifloor(x) + rng.nextInt(5) - 2;
                var by = ifloor(y) + rng.nextInt(3);
                var bz = ifloor(z) + rng.nextInt(5) - 2;
                var c = world.getBlock(bx, by, bz);
                var bid = c >> 4;
                var name = MonstersGlobals.blockNameOf(bid);
                if (new string[] { "grass_block", "dirt", "sand", "gravel", "pumpkin", "melon", "cactus", "clay", "mycelium", "podzol", "red_mushroom", "brown_mushroom", "tnt" }.Contains(name))
                {
                    carrying = c;
                    world.setBlock(bx, by, bz, 0);
                }
            }
            else
            {
                int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
                if (world.getBlock(bx, by, bz) == 0 && (world.getBlock(bx, by - 1, bz) >> 4) != 0)
                {
                    world.setBlock(bx, by, bz, carrying);
                    carrying = 0;
                }
            }
        }
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (source == "projectile")
        {
            teleportRandomly();
            if (attacker is LivingEntity living) { setTarget(living); }
            return false;
        }
        return base.hurt(amount, source, attacker);
    }

    public void teleportRandomly()
    {
        for (int _i = 0; _i < 16; _i++)
        {
            var tx = x + (rng.nextFloat() - 0.5) * 32;
            var tz = z + (rng.nextFloat() - 0.5) * 32;
            var ty = world.surfaceY(ifloor(tx), ifloor(tz));
            if (ty > world.info.minY + 1)
            {
                world.hooks.addParticles("portal", x, y + 1, z, 16, 0.5, 0);
                setPos(tx, (double)ty, tz);
                world.hooks.playSound("entity.enderman.teleport", tx, (double)ty, tz, 1, 1);
                return;
            }
        }
    }

    public void teleportNear(Entity t)
    {
        var tx = t.x + (rng.nextFloat() - 0.5) * 8;
        var tz = t.z + (rng.nextFloat() - 0.5) * 8;
        var ty = world.surfaceY(ifloor(tx), ifloor(tz));
        world.hooks.addParticles("portal", x, y + 1, z, 16, 0.5, 0);
        setPos(tx, (double)ty, tz);
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("ender_pearl", min: 0, max: 1, lootingBonus: 1) };
    }
}

// SILVERFISH / ENDERMITE -------------------------------------------------------------
public sealed class Silverfish : Monster
{
    public override string type => "silverfish";

    public Silverfish(World world) : base(world)
    {
        width = 0.4; height = 0.3;
        maxHealth = 8; health = 8;
        speed = 0.13;
        attackDamage = 1;
        addMonsterGoals(1.2);
    }

    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}

public sealed class Endermite : Monster
{
    public override string type => "endermite";
    public int lifeTicks = 2400;

    public Endermite(World world) : base(world)
    {
        width = 0.4; height = 0.3;
        maxHealth = 8; health = 8;
        speed = 0.13;
        attackDamage = 2;
        addMonsterGoals(1.2);
    }

    public override void tick()
    {
        base.tick();
        lifeTicks -= 1;
        if (lifeTicks <= 0) remove();
    }

    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}

// PHANTOM -----------------------------------------------------------------------------
public sealed class Phantom : Monster
{
    public override string type => "phantom";
    public double circleX = 0.0, circleY = 0.0, circleZ = 0.0;
    public string attackPhase = "circle";   // circle | swoop

    public Phantom(World world) : base(world)
    {
        width = 0.9; height = 0.5;
        maxHealth = 20; health = 20;
        attackDamage = 6;
        noGravity = true;
        burnsInSun = true;
        targetGoals.add(new NearestTargetGoal(this, 1, MonstersGlobals.isPlayerTarget, 64, false));
    }

    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) return;
        targetGoals.tick(2, age);
        if (circleX == 0 && circleY == 0)
        {
            circleX = x; circleY = y + 10; circleZ = z;
        }
        var t = target;
        if (t != null && !t.dead)
        {
            if (attackPhase == "circle")
            {
                circleX = t.x;
                circleY = t.y + 12;
                circleZ = t.z;
                var ang = (double)age * 0.06;
                var tx = circleX + detCos(ang) * 12;
                var tz = circleZ + detSin(ang) * 12;
                flyToward(tx, circleY, tz, 0.12);
                if (age % 100 == 0 && rng.nextFloat() < 0.4)
                {
                    attackPhase = "swoop";
                    world.hooks.playSound("entity.phantom.swoop", x, y, z, 2, 1);
                }
            }
            else
            {
                flyToward(t.x, t.y + 1, t.z, 0.25);
                if (distanceToSq(t) < 3)
                {
                    doMeleeAttack(t);
                    attackPhase = "circle";
                }
                if (y < t.y - 2 || hurtTime > 0) attackPhase = "circle";
            }
        }
        else
        {
            var ang = (double)age * 0.04;
            flyToward(circleX + detCos(ang) * 14, circleY, circleZ + detSin(ang) * 14, 0.1);
        }
        move(vx, vy, vz);
        yaw = detAtan2(-vx, vz);
    }

    private void flyToward(double tx, double ty, double tz, double speed)
    {
        double dx = tx - x, dy = ty - y, dz = tz - z;
        var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d == 0) d = 1;
        vx += (dx / d * speed - vx) * 0.1;
        vy += (dy / d * speed - vy) * 0.1;
        vz += (dz / d * speed - vz) * 0.1;
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("phantom_membrane", min: 0, max: 1, lootingBonus: 1) };
    }
}

// GUARDIANS -----------------------------------------------------------------------------
public class Guardian : Monster
{
    public override string type => "guardian";
    public LivingEntity laserTarget;
    public int laserTime = 0;

    public Guardian(World world) : base(world)
    {
        breathesWater = true;
        breathesWaterOnly = true;
        width = 0.85; height = 0.85;
        maxHealth = 30; health = 30;
        attackDamage = 6;
        speed = 0.1;
        goals.add(new RandomSwimGoal(this, 5, 1, 20));
        targetGoals.add(new NearestTargetGoal(this, 1, e =>
            MonstersGlobals.isPlayerTarget(e) || e.type == "squid" || e.type == "axolotl"
        , 16));
    }

    public override void tick()
    {
        base.tick();
        var t = target;
        if (t != null && !t.dead && canSee(t) && inWater)
        {
            if (laserTarget == null)
            {
                laserTarget = t;
                laserTime = 0;
                world.hooks.playSound("entity.guardian.attack", x, y, z, 1, 1);
            }
            laserTime += 1;
            lookX = t.x; lookY = t.eyeY(); lookZ = t.z;
            if (laserTime % 10 == 0)
            {
                // beam particles
                var steps = 8;
                for (int i = 1; i < steps; i++)
                {
                    var f = (double)i / (double)steps;
                    world.hooks.addParticles("bubble",
                                             x + (t.x - x) * f,
                                             y + 0.5 + (t.eyeY() - y - 0.5) * f,
                                             z + (t.z - z) * f, 1, 0.05, 0);
                }
            }
            if (laserTime >= (type == "elder_guardian" ? 60 : 80))
            {
                t.hurt(type == "elder_guardian" ? 8 : 6, "magic", this);
                laserTarget = null;
                laserTime = 0;
            }
        }
        else
        {
            laserTarget = null;
            laserTime = 0;
        }
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        // thorns
        if (source == "mob" && attacker != null && rng.nextFloat() < 0.5)
        {
            attacker.hurt(2, "thorns", this);
        }
        return base.hurt(amount, source, attacker);
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("prismarine_shard", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("cod", chance: 0.4, lootingBonus: 0.02),
            new DropEntry("prismarine_crystals", chance: 0.4, lootingBonus: 0.05),
        };
    }
}

public sealed class ElderGuardian : Guardian
{
    public override string type => "elder_guardian";

    public ElderGuardian(World world) : base(world)
    {
        width = 2; height = 2;
        maxHealth = 80; health = 80;
        attackDamage = 8;
        persistent = true;
        xpReward = 10;
    }

    public override void tick()
    {
        base.tick();
        // mining fatigue aura
        if (age % 1200 == 0)
        {
            foreach (var p in world.getEntitiesNear(x, y, z, 50, filter: o => (o as Entity)?.isPlayer ?? false))
            {
                var pl = p as LivingEntity;
                if (pl == null) continue;
                pl.addEffect("mining_fatigue", 6000, 2);
                world.hooks.playSound("entity.elder_guardian.curse", pl.x, pl.y, pl.z, 1, 1);
            }
        }
    }

    public override List<DropEntry> drops()
    {
        var d = base.drops();
        d.Add(new DropEntry("wet_sponge"));
        d.Add(new DropEntry("tide_armor_trim", chance: 0.2));
        return d;
    }
}

// SHULKER --------------------------------------------------------------------------------
public sealed class Shulker : Monster
{
    public override string type => "shulker";
    public double peekAmount = 0.0;

    public Shulker(World world) : base(world)
    {
        width = 1; height = 1;
        maxHealth = 30; health = 30;
        speed = 0;
        kbResist = 1;
        noGravity = true;
        targetGoals.add(new NearestTargetGoal(this, 1, MonstersGlobals.isPlayerTarget, 16));
    }

    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) return;
        targetGoals.tick(2, age);
        vx = 0; vy = 0; vz = 0;
        var t = target;
        if (t != null && !t.dead && canSee(t))
        {
            data.open = 0.7;
            if (age % 40 == 0)
            {
                var bullet = new ShulkerBullet(world);
                bullet.setPos(x, y + 0.5, z);
                bullet.owner = this;
                bullet.targetId = t.id;
                bullet.vx = 0.1;
                world.addEntity(bullet);
                world.hooks.playSound("entity.shulker.shoot", x, y, z, 1, 1);
            }
        }
        else
        {
            data.open = rng.nextFloat() < 0.05 ? 0.3 : 0;
        }
    }

    public override bool hurt(double amountIn, string source, Entity attacker = null)
    {
        var amount = amountIn;
        if ((data.open ?? 0) < 0.1 && source != "magic") { amount *= 0.3; } // closed armor
        var r = base.hurt(amount, source, attacker);
        if (r && health < maxHealth / 2 && rng.nextFloat() < 0.25)
        {
            // teleport to nearby surface
            for (int _i = 0; _i < 8; _i++)
            {
                var tx = ifloor(x) + rng.nextInt(17) - 8;
                var tz = ifloor(z) + rng.nextInt(17) - 8;
                var ty = world.surfaceY(tx, tz);
                if (ty > world.info.minY)
                {
                    setPos((double)tx + 0.5, (double)ty, (double)tz + 0.5);
                    world.hooks.playSound("entity.shulker.teleport", (double)tx, (double)ty, (double)tz, 1, 1);
                    break;
                }
            }
        }
        return r;
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("shulker_shell", chance: 0.5, lootingBonus: 0.0625) };
    }
}

// ILLAGERS --------------------------------------------------------------------------------
public sealed class Pillager : Monster
{
    public override string type => "pillager";
    public bool isCaptain = false;

    public Pillager(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 24; health = 24;
        speed = 0.11;
        data.crossed = true;
        goals.add(new FloatGoal(this, 0));
        goals.add(new RangedAttackGoal(this, 2, 40, 10, (t, power) =>
        {
            MonstersGlobals.shootArrowFn?.Invoke(this, t, power * 1.1, 4);
            this.world.hooks.playSound("item.crossbow.shoot", this.x, this.y, this.z, 1, 1);
        }, false));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.targetsVillagers, 24));
    }

    public override List<DropEntry> drops()
    {
        var d = new List<DropEntry> { new DropEntry("arrow", min: 0, max: 2), new DropEntry("crossbow", chance: 0.085) };
        if (isCaptain) d.Add(new DropEntry("emerald", min: 1, max: 3));
        return d;
    }

    public override void die(string source, Entity attacker = null)
    {
        base.die(source, attacker);
        if (isCaptain && attacker != null && attacker.isPlayer)
        {
            (attacker as LivingEntity)?.addEffect("bad_omen", 120000, 0);
        }
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["captain"] = isCaptain;
        return d;
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        if (d.TryGetValue("captain", out var cObj) && cObj is bool c)
        {
            isCaptain = c;
        }
        else if (d.TryGetValue("data", out var ddObj) && ddObj is Dictionary<string, object> dd
            && dd.TryGetValue("captain", out var c2Obj) && c2Obj is bool c2)
        {
            isCaptain = c2;
        }
        else
        {
            isCaptain = false;
        }
    }
}

public sealed class Vindicator : Monster
{
    public override string type => "vindicator";
    public bool isCaptain = false;

    public Vindicator(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 24; health = 24;
        speed = 0.115;
        attackDamage = 8; // wields axe (built into damage)
        addMonsterGoals(1.15);
        targetGoals.add(new NearestTargetGoal(this, 3, MonstersGlobals.targetsVillagers, 24));
    }

    public override List<DropEntry> drops()
    {
        var d = new List<DropEntry> { new DropEntry("emerald", min: 0, max: 1, lootingBonus: 1), new DropEntry("iron_axe", chance: 0.085) };
        if (isCaptain) d.Add(new DropEntry("emerald", min: 1, max: 3));
        return d;
    }

    public override void die(string source, Entity attacker = null)
    {
        base.die(source, attacker);
        if (isCaptain && attacker != null && attacker.isPlayer)
        {
            (attacker as LivingEntity)?.addEffect("bad_omen", 120000, 0);
        }
    }
}

public sealed class Evoker : Monster
{
    public override string type => "evoker";
    public int castCooldown = 100;

    public Evoker(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 24; health = 24;
        speed = 0.12;
        goals.add(new FloatGoal(this, 0));
        goals.add(new AvoidEntityGoal(this, 1, e =>
        {
            var t = this.target;
            if (t == null) return false;
            return ReferenceEquals(e, t) && this.distanceToSq(t) < 36;
        }, 7, 1.1));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.targetsVillagers, 24));
    }

    public override void tick()
    {
        base.tick();
        var t = target;
        if (t != null && !t.dead)
        {
            castCooldown -= 1;
            if (castCooldown <= 0)
            {
                var which = rng.nextFloat();
                if (which < 0.6)
                {
                    // evoker fangs line
                    castCooldown = 100;
                    world.hooks.playSound("entity.evoker.cast_spell", x, y, z, 1, 1);
                    double dx = t.x - x, dz = t.z - z;
                    var d = Math.Sqrt(dx * dx + dz * dz);
                    if (d == 0) d = 1;
                    for (int i = 1; i <= 16; i++)
                    {
                        var fx = x + dx / d * (double)i;
                        var fz = z + dz / d * (double)i;
                        MonstersGlobals.spawnFangsFn?.Invoke(world, fx, y, fz, i * 2, this);
                    }
                }
                else
                {
                    // summon vexes
                    castCooldown = 340;
                    world.hooks.playSound("entity.evoker.prepare_summon", x, y, z, 1, 1);
                    for (int _i = 0; _i < 3; _i++)
                    {
                        var vex = spawnMobFn?.Invoke(world, "vex", x + rng.nextFloat() * 2 - 1, y + 1, z + rng.nextFloat() * 2 - 1, new SpawnOpts());
                        (vex as Mob)?.setTarget(target);
                    }
                }
            }
        }
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("totem_of_undying"), new DropEntry("emerald", min: 0, max: 1, lootingBonus: 1) };
    }
}

public sealed class Vex : Monster
{
    public override string type => "vex";
    public int lifeTicks = 0;

    public Vex(World world) : base(world)
    {
        lifeTicks = 600 + gameRng.nextInt(600);   // baseline field-init order
        width = 0.4; height = 0.8;
        maxHealth = 14; health = 14;
        attackDamage = 9;
        noGravity = true;
        noClip = true;
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, MonstersGlobals.isPlayerTarget, 16));
    }

    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) return;
        targetGoals.tick(2, age);
        lifeTicks -= 1;
        if (lifeTicks <= 0) hurt(1, "magic");
        var t = target;
        if (t != null && !t.dead)
        {
            double dx = t.x - x, dy = t.eyeY() - 0.5 - y, dz = t.z - z;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d == 0) d = 1;
            vx += (dx / d * 0.18 - vx) * 0.12;
            vy += (dy / d * 0.18 - vy) * 0.12;
            vz += (dz / d * 0.18 - vz) * 0.12;
            yaw = detAtan2(-dx, dz);
            if (d < 1.2 && age % 20 == 0) doMeleeAttack(t);
        }
        else
        {
            vx *= 0.9; vy *= 0.9; vz *= 0.9;
        }
        x += vx; y += vy; z += vz;
    }

    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}

public sealed class Ravager : Monster
{
    public override string type => "ravager";
    public int roarCooldown = 0;

    public Ravager(World world) : base(world)
    {
        width = 1.95; height = 2.2;
        maxHealth = 100; health = 100;
        speed = 0.12;
        attackDamage = 12;
        kbResist = 0.75;
        stepHeight = 1;
        xpReward = 20;
        addMonsterGoals(1.1);
        targetGoals.add(new NearestTargetGoal(this, 3, MonstersGlobals.targetsVillagers, 32));
    }

    public override void tick()
    {
        base.tick();
        // trample crops/leaves
        if (world.rule("mobGriefing") && age % 5 == 0)
        {
            int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
            foreach (var (dx, dz) in new (int, int)[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var c = world.getBlock(bx + dx, by, bz + dz);
                var name = MonstersGlobals.blockNameOf(c >> 4);
                if (new string[] { "leaves", "wheat", "carrot", "potato", "beetroot" }.Any(s => name.Contains(s)))
                {
                    world.breakBlockNaturally(bx + dx, by, bz + dz);
                }
            }
        }
        if (roarCooldown > 0) roarCooldown -= 1;
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        var r = base.hurt(amount, source, attacker);
        if (r && rng.nextFloat() < 0.15 && roarCooldown <= 0)
        {
            // roar: knockback all nearby
            roarCooldown = 100;
            world.hooks.playSound("entity.ravager.roar", x, y, z, 2, 1);
            foreach (var e in world.getEntitiesNear(x, y, z, 5))
            {
                var ent = e as Entity;
                if (ent == null || ReferenceEquals(ent, this)) continue;
                double dx = ent.x - x, dz = ent.z - z;
                var d = Math.Sqrt(dx * dx + dz * dz);
                if (d == 0) d = 1;
                ent.vx += dx / d * 0.8;
                ent.vy += 0.4;
                ent.vz += dz / d * 0.8;
            }
        }
        return r;
    }

    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("saddle") }; }
}

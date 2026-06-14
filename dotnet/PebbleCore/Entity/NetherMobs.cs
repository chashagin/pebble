// Nether mobs — blazes, ghasts, magma cubes,
// piglins (with bartering), piglin brutes, zombified piglins, hoglins,
// zoglins, wither skeletons.
//
// Ported from Sources/PebbleCore/Entity/NetherMobs.swift.
//
// Defines static class NetherMobsGlobals holding the module-level free funcs
// scheduleEntityTimeout / tickPendingTimeouts / clearEntityTimeouts (and the
// private pendingTimeouts queue). Surface via `global using static
// PebbleCore.NetherMobsGlobals` so tickPendingTimeouts / clearEntityTimeouts
// resolve unqualified from Combat.cs / GameCore.cs.
//
// All mob classes live at namespace level. Cross-layer symbols referenced by
// name (World, Monster, Slime, Mob, LivingEntity, Entity, ItemStack, Fireball,
// ItemEntity, SpawnOpts, spawnMobFn, spawnItem, rollLoot, itemDef, B, etc.)
// are defined in sibling files of the same assembly.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public sealed class Blaze : Monster
{
    public override string type => "blaze";
    public Blaze(World world) : base(world)
    {
        width = 0.6; height = 1.8;
        maxHealth = 20; health = 20;
        speed = 0.1;
        gravityScale = 0.3;
        xpReward = 10;
        goals.add(new RangedAttackGoal(this, 1, 60, 24, (t, _) =>
        {
            // 3-shot volley
            for (int i = 0; i < 3; i++)
            {
                scheduleEntityTimeout(this.world, i * 6, () =>
                {
                    if (this.dead || t.dead) return;
                    var fb = new Fireball(this.world);
                    fb.small = true;
                    fb.width = 0.3;
                    double dx = t.x - this.x, dy = t.eyeY() - (this.y + 1), dz = t.z - this.z;
                    fb.setPos(this.x, this.y + 1, this.z);
                    fb.owner = this;
                    fb.shoot(dx, dy + Math.Sqrt(dx * dx + dz * dz) * 0.04, dz, 1.1, 6);
                    this.world.addEntity(fb);
                    this.world.hooks.playSound("entity.blaze.shoot", this.x, this.y, this.z, 1, 1);
                });
            }
        }, false));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, isPlayerTarget, 48, false));
    }
    public override void tick()
    {
        base.tick();
        if (vy < 0) vy *= 0.6;
        if (target is LivingEntity t && t.y > y + 1 && rng.nextFloat() < 0.1) vy = 0.2;
        if (age % 8 == 0) world.hooks.addParticles("smoke", x, y + 0.8, z, 1, 0.3, 0);
        if (inWater) hurt(1, "drown");
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("blaze_rod", min: 0, max: 1, lootingBonus: 1) };
    }
}

// keyed by world identity — dims tick independent time counters, and a
// timeout scheduled in the nether must not fire against overworld time
public static class NetherMobsGlobals
{
    private struct PendingTimeout
    {
        public World world;
        public int time;
        public Action fn;
        public PendingTimeout(World world, int time, Action fn) { this.world = world; this.time = time; this.fn = fn; }
    }

    private static List<PendingTimeout> pendingTimeouts = new List<PendingTimeout>();

    public static void scheduleEntityTimeout(World world, int delayTicks, Action fn)
    {
        pendingTimeouts.Add(new PendingTimeout(world, world.time + delayTicks, fn));
    }

    public static void tickPendingTimeouts(World world)
    {
        var wid = world;
        var fired = new List<Action>();
        // mirror Swift `pendingTimeouts.removeAll { ... }` with side effects: walk
        // in order, keep unmatched, collect matched fns in FIFO order, assign back
        var kept = new List<PendingTimeout>();
        foreach (var t in pendingTimeouts)
        {
            if (ReferenceEquals(t.world, wid) && t.time <= world.time)
            {
                fired.Add(t.fn);
            }
            else
            {
                kept.Add(t);
            }
        }
        pendingTimeouts = kept;
        foreach (var fn in fired) fn(); // FIFO, deterministically setTimeout
    }

    /// drop everything on world exit — entries hold closures that would otherwise
    /// keep dead worlds' entities alive (and could fire into a reloaded world)
    public static void clearEntityTimeouts()
    {
        pendingTimeouts.Clear();
    }
}

public sealed class Ghast : Monster
{
    public override string type => "ghast";
    public int shootCooldown = 0;
    public Ghast(World world) : base(world)
    {
        width = 4; height = 4;
        maxHealth = 10; health = 10;
        noGravity = true;
        xpReward = 5;
        targetGoals.add(new NearestTargetGoal(this, 1, isPlayerTarget, 64, false));
    }
    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) return;
        targetGoals.tick(2, age);
        // wander
        if (age % 60 == 0 && rng.nextFloat() < 0.5)
        {
            vx = (rng.nextFloat() - 0.5) * 0.1;
            vy = (rng.nextFloat() - 0.5) * 0.06;
            vz = (rng.nextFloat() - 0.5) * 0.1;
        }
        move(vx, vy, vz);
        if (horizontalCollision)
        {
            vx = (rng.nextFloat() - 0.5) * 0.1;
            vz = (rng.nextFloat() - 0.5) * 0.1;
        }
        // shoot fireballs
        if (shootCooldown > 0) shootCooldown -= 1;
        if (target is LivingEntity t && !t.dead && shootCooldown <= 0)
        {
            double dSq = distanceToSq(t);
            if (dSq < 64 * 64 && canSee(t))
            {
                shootCooldown = 60;
                world.hooks.playSound("entity.ghast.warn", x, y, z, 4, 1);
                var fb = new Fireball(world);
                fb.power = 1;
                fb.setPos(x, y + 2, z);
                fb.owner = this;
                fb.shoot(t.x - x, t.eyeY() - (y + 2), t.z - z, 0.8, 0);
                world.addEntity(fb);
                world.hooks.playSound("entity.ghast.shoot", x, y, z, 4, 1);
            }
        }
        if (target is LivingEntity t2 && !t2.dead)
        {
            lookX = t2.x; lookY = t2.y; lookZ = t2.z;
            yaw = detAtan2(-(t2.x - x), t2.z - z);
        }
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("ghast_tear", min: 0, max: 1, lootingBonus: 1),
            new DropEntry("gunpowder", min: 0, max: 2),
        };
    }
}

public sealed class MagmaCube : Slime
{
    public override string type => "magma_cube";
    public MagmaCube(World world) : base(world) { }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (source == "fire" || source == "lava") return false;
        return base.hurt(amount, source, attacker);
    }
    public override List<DropEntry> drops()
    {
        return size > 1 ? new List<DropEntry> { new DropEntry("magma_cream", min: 0, max: 1, lootingBonus: 1) } : new List<DropEntry>();
    }
}

public sealed class ZombifiedPiglin : Monster
{
    public override string type => "zombified_piglin";
    public int angerTime = 0;
    public ZombifiedPiglin(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 20; health = 20;
        speed = 0.1;
        attackDamage = 8; // golden sword
        goals.add(new FloatGoal(this, 0));
        goals.add(new MeleeAttackGoal(this, 2, 1.2));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1, true));
    }
    public override void tick()
    {
        base.tick();
        if (angerTime > 0)
        {
            angerTime -= 1;
            if (angerTime == 0) setTarget(null);
        }
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        bool r = base.hurt(amount, source, attacker);
        if (r && attacker != null && attacker.isPlayer)
        {
            angerTime = 800;
            // alert the pack!
            foreach (var e in world.getEntitiesNear(x, y, z, 32, filter: o => (o as Entity)?.type == "zombified_piglin"))
            {
                if (!(e is ZombifiedPiglin zp)) continue;
                zp.setTarget(attacker as LivingEntity);
                zp.angerTime = 800;
            }
        }
        return r;
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("rotten_flesh", min: 0, max: 1, lootingBonus: 1),
            new DropEntry("gold_nugget", min: 0, max: 1, lootingBonus: 1),
            new DropEntry("gold_ingot", chance: 0.025, lootingBonus: 0.01),
        };
    }
}

public class Piglin : Monster
{
    public override string type => "piglin";
    public int admiring = 0;
    public ItemStack admiredItem = null;
    public int zombifyTime = 300;
    public Piglin(World world) : base(world)
    {
        width = 0.6; height = 1.95;
        maxHealth = 16; health = 16;
        speed = 0.11;
        attackDamage = 5;
        goals.add(new FloatGoal(this, 0));
        goals.add(new AvoidEntityGoal(this, 1, e => new List<string> { "zombified_piglin", "zoglin" }.Contains(e.type), 8, 1.1));
        goals.add(new MeleeAttackGoal(this, 2, 1.15));
        goals.add(new StrollGoal(this, 6, 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1, true));
        targetGoals.add(new NearestTargetGoal(this, 2, e =>
        {
            // attacks players not wearing gold
            if (!e.isPlayer || e.dead) return false;
            foreach (var a in e.armor)
            {
                if (a != null && itemDef(a.id).name.Contains("golden_")) return false;
            }
            return true;
        }, 16));
        targetGoals.add(new NearestTargetGoal(this, 3, e => e.type == "wither_skeleton", 16));
    }
    public override void tick()
    {
        base.tick();
        // overworld zombification
        if (world.dim == Dim.overworld)
        {
            zombifyTime -= 1;
            if (zombifyTime <= 0)
            {
                var zomb = spawnMobFn?.Invoke(world, "zombified_piglin", x, y, z, new SpawnOpts());
                if (zomb != null)
                {
                    world.hooks.playSound("entity.zombified_piglin.angry", x, y, z, 1, 1);
                    remove();
                    return;
                }
            }
        }
        // admire gold ingots thrown nearby (bartering)
        if (admiring > 0)
        {
            admiring -= 1;
            nav.stop();
            if (admiring == 0 && admiredItem != null)
            {
                // barter!
                var loot = rollLoot("piglin_bartering", ref rng);
                foreach (var s in loot)
                {
                    spawnItem(world, x, y + 0.8, z, s,
                              -detSin(yaw) * 0.2, 0.2, detCos(yaw) * 0.2);
                }
                admiredItem = null;
            }
        }
        else if (age % 10 == 0 && target == null)
        {
            foreach (var e in world.getEntitiesNear(x, y, z, 2, filter: o => (o as Entity)?.type == "item"))
            {
                if (!(e is ItemEntity item)) continue;
                if (itemDef(item.stack.id).name == "gold_ingot" && item.pickupDelay <= 0)
                {
                    item.stack.count -= 1;
                    if (item.stack.count <= 0) item.remove();
                    admiring = 120;
                    admiredItem = new ItemStack(item.stack.id, 1);
                    world.hooks.playSound("entity.piglin.admiring_item", x, y, z, 1, 1);
                    break;
                }
            }
        }
    }
    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}

public sealed class PiglinBrute : Piglin
{
    public override string type => "piglin_brute";
    public PiglinBrute(World world) : base(world)
    {
        maxHealth = 50; health = 50;
        attackDamage = 13; // golden axe
        xpReward = 20;
        // brutes fear nothing — drop the avoid goal inherited from Piglin
        goals.goals.RemoveAll(g => g is AvoidEntityGoal);
        // brutes attack regardless of gold — this priority-1 goal outranks
        // the inherited gold-check target goal
        targetGoals.add(new NearestTargetGoal(this, 1, isPlayerTarget, 16));
    }
    public override void tick()
    {
        // no bartering, no fleeing — call Monster-level behavior
        mobTick();
        if (world.dim == Dim.overworld)
        {
            zombifyTime -= 1;
            if (zombifyTime <= 0)
            {
                var zomb = spawnMobFn?.Invoke(world, "zombified_piglin", x, y, z, new SpawnOpts());
                if (zomb != null) remove();
            }
        }
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("golden_axe", chance: 0.085) }; }
}

public class Hoglin : Monster
{
    public override string type => "hoglin";
    public int zombifyTime = 300;
    public Hoglin(World world) : base(world)
    {
        width = 1.4; height = 1.4;
        maxHealth = 40; health = 40;
        speed = 0.12;
        attackDamage = 6;
        xpReward = 5;
        addMonsterGoals(1.2);
        goals.add(new AvoidEntityGoal(this, 1, _ => false, 8, 1.3));
    }
    public override void tick()
    {
        base.tick();
        // flee warped fungus
        if (age % 20 == 0)
        {
            int dx = -4;
            while (dx <= 4)
            {
                int dz = -4;
                while (dz <= 4)
                {
                    if ((world.getBlock(ifloor(x) + dx, ifloor(y), ifloor(z) + dz) >> 4) == (int)B.warped_fungus)
                    {
                        nav.moveTo(x - (double)dx * 3, y, z - (double)dz * 3, 1.3);
                    }
                    dz += 2;
                }
                dx += 2;
            }
        }
        if (world.dim == Dim.overworld)
        {
            zombifyTime -= 1;
            if (zombifyTime <= 0)
            {
                var zomb = spawnMobFn?.Invoke(world, "zoglin", x, y, z, new SpawnOpts());
                if (zomb != null) remove();
            }
        }
    }
    public override void doMeleeAttack(LivingEntity target)
    {
        base.doMeleeAttack(target);
        target.vy += 0.5; // fling
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry(fireTicks > 0 ? "cooked_porkchop" : "porkchop", min: 2, max: 4, lootingBonus: 1),
            new DropEntry("leather", min: 0, max: 1),
        };
    }
}

public sealed class Zoglin : Hoglin
{
    public override string type => "zoglin";
    public Zoglin(World world) : base(world)
    {
        zombifyTime = int.MaxValue;   // baseline sets Infinity; the check never fires
        targetGoals.add(new NearestTargetGoal(this, 1, e =>
            !new List<string> { "zoglin", "creeper", "ghast" }.Contains(e.type)
        , 16));
    }
    public override void tick()
    {
        mobTick();
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("rotten_flesh", min: 1, max: 3, lootingBonus: 1) }; }
}

public sealed class WitherSkeletonMob : Monster
{
    public override string type => "wither_skeleton";
    public WitherSkeletonMob(World world) : base(world)
    {
        width = 0.7; height = 2.4;
        maxHealth = 20; health = 20;
        speed = 0.12;
        attackDamage = 8; // stone sword
        xpReward = 5;
        addMonsterGoals(1.2);
    }
    public override void doMeleeAttack(LivingEntity target)
    {
        base.doMeleeAttack(target);
        target.addEffect("wither", 200, 0);
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (source == "fire" || source == "lava") return false;
        return base.hurt(amount, source, attacker);
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("coal", min: 0, max: 1, lootingBonus: 1),
            new DropEntry("bone", min: 0, max: 2, lootingBonus: 1),
            new DropEntry("wither_skeleton_skull", chance: 0.025, lootingBonus: 0.01),
        };
    }
}

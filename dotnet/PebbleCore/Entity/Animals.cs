// Passive & tameable mobs — farm animals, pets,
// water creatures, and ambient mobs. (Villagers/golems/horses live in
// Villagers.swift, mirroring the baseline file split.)
//
// constructor randomness (variants, colors, egg timers) is deliberately
// nondeterministic; golden tests override those fields after spawning.
//
// Ported from Sources/PebbleCore/Entity/Animals.swift.
// Defines static class AnimalsGlobals (module-level free funcs heldStack/heldName —
// surface via global using static AnimalsGlobals). All public mob types
// (Animal, Cow, Mooshroom, Pig, Sheep, Chicken, Rabbit, TamableAnimal, Wolf, Cat,
// Ocelot, Fox, Parrot, Bee, Axolotl, Frog, Tadpole, Goat, Turtle, Dolphin, Squid,
// GlowSquid, Bat, PolarBear, Panda, Strider, Camel, Sniffer, Allay, AbstractFish,
// Cod, Salmon, TropicalFish, Pufferfish) live at namespace level.
// All other symbols (spawnMobFn, SpawnOpts, spawnItem, spawnXP, rollLoot, COLORS,
// wrapAngle, gameRng, ItemEntity, Slime, World, Entity, ItemStack, BlockEntityData,
// itemDef, iid, blockDefs, cell, B, the goal classes, …) resolve from the rest of
// the assembly.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public static class AnimalsGlobals
{
    public static ItemStack heldStack(Entity player) => (player as LivingEntity)?.mainHand;
    public static string heldName(Entity player)
    {
        var h = heldStack(player);
        if (h == null) { return null; }
        return itemDef(h.id).name;
    }
}

public class Animal : Mob
{
    public List<string> foods = new List<string>();

    public Animal(World world) : base(world)
    {
        category = "creature";
    }

    public override bool isFood(ItemStack stack)
    {
        if (stack == null) { return false; }
        return foods.Contains(itemDef(stack.id).name);
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        if (stack != null && isFood(stack))
        {
            if (tryFeed(player, stack))
            {
                (player as LivingEntity)?.consumeHeld(1);
                world.hooks.playSound("entity.generic.eat", x, y, z, 1, 1);
                return true;
            }
        }
        return false;
    }

    public void addBasicGoals(double speed = 1, double panicSpeed = 1.4)
    {
        goals.add(new FloatGoal(this, 0));
        goals.add(new PanicGoal(this, 1, panicSpeed));
        goals.add(new BreedGoal(this, 2, (a, b) => this.spawnBaby(a, b)));
        if (foods.Count != 0) { goals.add(new TemptGoal(this, 3, foods, 1.1)); }
        goals.add(new FollowParentGoal(this, 4));
        goals.add(new StrollGoal(this, 6, speed * 0.8));
        goals.add(new LookAtPlayerGoal(this, 7));
        goals.add(new RandomLookGoal(this, 8));
    }

    public virtual void spawnBaby(Mob a, Mob b)
    {
        var baby = spawnMobFn?.Invoke(world, type, a.x, a.y, a.z, new SpawnOpts(baby: true));
        if (baby != null)
        {
            spawnXP(world, a.x, a.y, a.z, 1 + gameRng.nextInt(7));
        }
    }
}

// ---------------------------------------------------------------------------
public class Cow : Animal
{
    public override string type => "cow";
    public Cow(World world) : base(world)
    {
        width = 0.9; height = 1.4;
        maxHealth = 10; health = 10;
        speed = 0.08;
        foods = new List<string> { "wheat" };
        xpReward = 3;
        addBasicGoals();
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("leather", min: 0, max: 2, lootingBonus: 1),
            new DropEntry(fireTicks > 0 ? "cooked_beef" : "beef", min: 1, max: 3, lootingBonus: 1),
        };
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        if (stack != null && itemDef(stack.id).name == "bucket" && !baby)
        {
            (player as LivingEntity)?.replaceHeld(new ItemStack(iid("milk_bucket"), 1));
            world.hooks.playSound("entity.cow.milk", x, y, z, 1, 1);
            return true;
        }
        return base.interact(player, stack);
    }
}

public sealed class Mooshroom : Cow
{
    public override string type => "mooshroom";
    public Mooshroom(World world) : base(world) { }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (name == "bowl" && !baby)
        {
            (player as LivingEntity)?.replaceHeld(new ItemStack(iid("mushroom_stew"), 1));
            world.hooks.playSound("entity.mooshroom.milk", x, y, z, 1, 1);
            return true;
        }
        if (name == "shears" && !baby)
        {
            // shear into cow
            var cow = spawnMobFn?.Invoke(world, "cow", x, y, z, new SpawnOpts());
            if (cow != null)
            {
                remove();
                for (int _i = 0; _i < 5; _i++) { spawnItem(world, x, y + 1, z, new ItemStack(iid("red_mushroom"), 1)); }
                (player as LivingEntity)?.damageHeld(1);
                world.hooks.playSound("entity.mooshroom.shear", x, y, z, 1, 1);
            }
            return true;
        }
        return base.interact(player, stack);
    }
}

public sealed class Pig : Animal
{
    public override string type => "pig";
    public bool saddled = false;
    public int boostTime = 0;
    public Pig(World world) : base(world)
    {
        width = 0.9; height = 0.9;
        maxHealth = 10; health = 10;
        speed = 0.09;
        foods = new List<string> { "carrot", "potato", "beetroot" };
        xpReward = 3;
        addBasicGoals();
    }
    public override List<DropEntry> drops()
    {
        var d = new List<DropEntry> { new DropEntry(fireTicks > 0 ? "cooked_porkchop" : "porkchop", min: 1, max: 3, lootingBonus: 1) };
        if (saddled) { d.Add(new DropEntry("saddle")); }
        return d;
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (name == "saddle" && !saddled && !baby)
        {
            saddled = true;
            (player as LivingEntity)?.consumeHeld(1);
            world.hooks.playSound("entity.pig.saddle", x, y, z, 1, 1);
            return true;
        }
        if (saddled && !((player as LivingEntity)?.sneaking ?? false) && (stack == null || name != "carrot"))
        {
            player.mount(this);
            return true;
        }
        return base.interact(player, stack);
    }
    public override void tick()
    {
        base.tick();
        // carrot-on-a-stick steering
        if (passengers.FirstOrDefault() is LivingEntity rider && rider.isPlayer && saddled)
        {
            if (rider.mainHand is ItemStack held && itemDef(held.id).name == "carrot_on_a_stick")
            {
                yaw = rider.yaw;
                moveForward = 0.7 + (boostTime > 0 ? 0.6 : 0);
                if (boostTime > 0) { boostTime -= 1; }
            }
            else
            {
                moveForward = 0;
            }
        }
    }
    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["saddled"] = saddled;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        saddled = (d.TryGetValue("saddled", out var vSaddled) && vSaddled is bool bSaddled) ? bSaddled : false;
    }
}

public sealed class Sheep : Animal
{
    public override string type => "sheep";
    public bool sheared
    {
        get { return data.sheared ?? false; }
        set { data.sheared = value; }
    }
    public int color
    {
        get { return data.color ?? 0; }
        set { data.color = value; }
    }
    public int eatGrassTimer = 0;
    public Sheep(World world) : base(world)
    {
        width = 0.9; height = 1.3;
        maxHealth = 8; health = 8;
        speed = 0.09;
        foods = new List<string> { "wheat" };
        xpReward = 2;
        if (gameRng.nextFloat() < 0.05) { color = gameRng.nextFloat() < 0.5 ? 7 : 8; } // gray-ish rare
        if (gameRng.nextFloat() < 0.03) { color = 12; }                                  // brown
        if (gameRng.nextFloat() < 0.0016) { color = 6; }                                 // pink!
        addBasicGoals();
        goals.add(new EatGrassGoal(this, 5));
    }
    public override List<DropEntry> drops()
    {
        var d = new List<DropEntry> { new DropEntry(fireTicks > 0 ? "cooked_mutton" : "mutton", min: 1, max: 2, lootingBonus: 1) };
        if (!sheared) { d.Add(new DropEntry(COLORS[color] + "_wool")); }
        return d;
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (name == "shears" && !sheared && !baby)
        {
            sheared = true;
            int count = 1 + gameRng.nextInt(3);
            for (int _i = 0; _i < count; _i++) { spawnItem(world, x, y + 0.5, z, new ItemStack(iid(COLORS[color] + "_wool"), 1)); }
            (player as LivingEntity)?.damageHeld(1);
            world.hooks.playSound("entity.sheep.shear", x, y, z, 1, 1);
            return true;
        }
        if (name != null && name.EndsWith("_dye"))
        {
            int colorIdx = Array.IndexOf(COLORS, name.Substring(0, name.Length - 4));
            if (colorIdx >= 0 && colorIdx != color)
            {
                color = colorIdx;
                (player as LivingEntity)?.consumeHeld(1);
                return true;
            }
        }
        return base.interact(player, stack);
    }
}
sealed class EatGrassGoal : Goal
{
    int timer = 0;
    public EatGrassGoal(Mob mob, int priority) : base(mob, priority) { }
    public override bool canUse()
    {
        var m = mob;
        if (m.rng.nextInt(m.baby ? 50 : 1000) != 0) { return false; }
        int below = m.world.getBlock(ifloor(m.x), ifloor(m.y - 1), ifloor(m.z)) >> 4;
        int at = m.world.getBlock(ifloor(m.x), ifloor(m.y), ifloor(m.z)) >> 4;
        return below == (int)B.grass_block || at == (int)B.short_grass;
    }
    public override bool canContinue() { return timer > 0; }
    public override void start() { timer = 40; mob.nav.stop(); mob.data.grazing = true; }
    public override void stop() { mob.data.grazing = false; }
    public override void tick()
    {
        timer -= 1;
        if (timer == 4)
        {
            if (!(mob is Sheep m)) { return; }
            int bx = ifloor(m.x), by = ifloor(m.y), bz = ifloor(m.z);
            if ((m.world.getBlock(bx, by, bz) >> 4) == (int)B.short_grass)
            {
                m.world.breakBlockNaturally(bx, by, bz);
                m.sheared = false;
            }
            else if ((m.world.getBlock(bx, by - 1, bz) >> 4) == (int)B.grass_block && m.world.rule("mobGriefing"))
            {
                m.world.setBlock(bx, by - 1, bz, (int)cell(B.dirt));
                m.sheared = false;
            }
            if (m.baby) { m.growUpAge = Math.Max(0, m.growUpAge - 600); }
        }
    }
}

public sealed class Chicken : Animal
{
    public override string type => "chicken";
    public int eggTime = 0;
    public Chicken(World world) : base(world)
    {
        eggTime = 6000 + gameRng.nextInt(6000);   // baseline field-init order
        width = 0.4; height = 0.7;
        maxHealth = 4; health = 4;
        speed = 0.1;
        foods = new List<string> { "wheat_seeds", "melon_seeds", "pumpkin_seeds", "beetroot_seeds", "torchflower_seeds", "pitcher_pod" };
        xpReward = 2;
        addBasicGoals();
    }
    public override void tick()
    {
        base.tick();
        // flap fall
        if (!onGround && vy < 0) { vy *= 0.6; }
        data.airborne = !onGround;
        if (!baby)
        {
            eggTime -= 1;
            if (eggTime <= 0)
            {
                eggTime = 6000 + gameRng.nextInt(6000);
                spawnItem(world, x, y, z, new ItemStack(iid("egg"), 1));
                world.hooks.playSound("entity.chicken.egg", x, y, z, 1, 1);
            }
        }
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("feather", min: 0, max: 2, lootingBonus: 1),
            new DropEntry(fireTicks > 0 ? "cooked_chicken" : "chicken", min: 1, max: 1),
        };
    }
}

public sealed class Rabbit : Animal
{
    public override string type => "rabbit";
    public Rabbit(World world) : base(world)
    {
        width = 0.4; height = 0.5;
        maxHealth = 3; health = 3;
        speed = 0.3;
        jumpPower = 0.5;
        foods = new List<string> { "carrot", "golden_carrot", "dandelion" };
        xpReward = 2;
        addBasicGoals(1, 2.2);
        goals.add(new AvoidEntityGoal(this, 2, e => e.isPlayer || e.type == "wolf", 8, 2.2));
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry>
        {
            new DropEntry("rabbit_hide", min: 0, max: 1, lootingBonus: 1),
            new DropEntry(fireTicks > 0 ? "cooked_rabbit" : "rabbit", min: 1, max: 1),
            new DropEntry("rabbit_foot", chance: 0.1, lootingBonus: 0.03),
        };
    }
}

public class TamableAnimal : Animal
{
    public bool tamed = false;
    public TamableAnimal(World world) : base(world) { }
    public override bool interact(Entity player, ItemStack stack)
    {
        if (tamed && ownerId == player.id && stack == null)
        {
            sitting = !sitting;
            return true;
        }
        return base.interact(player, stack);
    }
    public bool tryTame(Entity player, double chance)
    {
        (player as LivingEntity)?.consumeHeld(1);
        if (gameRng.nextFloat() < chance)
        {
            tamed = true;
            ownerId = player.id;
            persistent = true;
            world.hooks.addParticles("heart", x, y + height, z, 6, 0.4, 0);
            return true;
        }
        world.hooks.addParticles("smoke", x, y + height, z, 6, 0.4, 0);
        return false;
    }
    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["tamed"] = tamed;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        tamed = (d.TryGetValue("tamed", out var vTamed) && vTamed is bool bTamed) ? bTamed : false;
    }
}

public sealed class Wolf : TamableAnimal
{
    public override string type => "wolf";
    public Wolf(World world) : base(world)
    {
        width = 0.6; height = 0.85;
        maxHealth = 8; health = 8;
        speed = 0.15;
        attackDamage = 4;
        foods = new List<string> { "beef", "cooked_beef", "porkchop", "cooked_porkchop", "mutton", "cooked_mutton", "chicken", "cooked_chicken", "rabbit", "cooked_rabbit", "rotten_flesh" };
        xpReward = 3;
        goals.add(new FloatGoal(this, 0));
        goals.add(new SitWhenOrderedGoal(this, 1));
        goals.add(new MeleeAttackGoal(this, 2, 1.3));
        goals.add(new FollowOwnerGoal(this, 3));
        goals.add(new BreedGoal(this, 4, (a, b) => this.spawnBaby(a, b)));
        goals.add(new StrollGoal(this, 6, 0.9));
        goals.add(new LookAtPlayerGoal(this, 7));
        goals.add(new RandomLookGoal(this, 8));
        targetGoals.add(new HurtByTargetGoal(this, 1, true));
        targetGoals.add(new OwnerHurtTargetGoal(this, 2));
        targetGoals.add(new NearestTargetGoal(this, 3, e =>
            !this.tamed && (e.type == "sheep" || e.type == "rabbit" || e.type == "fox" || e.type == "skeleton")
        , 16));
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (!tamed && name == "bone")
        {
            tryTame(player, 1.0 / 3);
            return true;
        }
        if (tamed && stack != null && isFood(stack) && health < maxHealth)
        {
            heal((double)(itemDef(stack.id).food?.hunger ?? 2));
            (player as LivingEntity)?.consumeHeld(1);
            return true;
        }
        return base.interact(player, stack);
    }
}
sealed class OwnerHurtTargetGoal : Goal
{
    public OwnerHurtTargetGoal(Mob mob, int priority) : base(mob, priority)
    {
        flags = GoalFlag.target;
    }
    public override bool canUse()
    {
        var m = mob;
        if (!(m is TamableAnimal tam) || !tam.tamed || m.sitting || m.ownerId == null) { return false; }
        int ownerId = m.ownerId.Value;
        if (!(m.world.entityById.TryGetValue(ownerId, out var ownerRef) && ownerRef is LivingEntity owner)) { return false; }
        var t = owner.lastAttacker ?? owner.lastHurtTarget;
        if (t != null && !t.dead && !ReferenceEquals(t, m) && t is LivingEntity living)
        {
            m.setTarget(living);
            return true;
        }
        return false;
    }
    public override bool canContinue() { return mob.target != null && !mob.target.dead; }
}

public class Cat : TamableAnimal
{
    public override string type => "cat";
    public Cat(World world) : base(world)
    {
        width = 0.6; height = 0.7;
        maxHealth = 10; health = 10;
        speed = 0.15;
        attackDamage = 3;
        foods = new List<string> { "cod", "salmon" };
        xpReward = 2;
        goals.add(new FloatGoal(this, 0));
        goals.add(new SitWhenOrderedGoal(this, 1));
        goals.add(new TemptGoal(this, 3, foods, 0.9));
        goals.add(new FollowOwnerGoal(this, 4));
        goals.add(new BreedGoal(this, 5, (a, b) => this.spawnBaby(a, b)));
        goals.add(new StrollGoal(this, 6, 0.9));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new NearestTargetGoal(this, 1, e => e.type == "rabbit" || e.type == "chicken", 10));
        goals.add(new MeleeAttackGoal(this, 2, 1.3));
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (!tamed && (name == "cod" || name == "salmon"))
        {
            tryTame(player, 1.0 / 3);
            return true;
        }
        return base.interact(player, stack);
    }
}
public sealed class Ocelot : Cat
{
    public override string type => "ocelot";
    public Ocelot(World world) : base(world) { }
}

public sealed class Fox : Animal
{
    public override string type => "fox";
    public Fox(World world) : base(world)
    {
        width = 0.6; height = 0.7;
        maxHealth = 10; health = 10;
        speed = 0.16;
        attackDamage = 2;
        foods = new List<string> { "sweet_berries", "glow_berries" };
        xpReward = 3;
        addBasicGoals(1, 2);
        goals.add(new AvoidEntityGoal(this, 2, e => e.isPlayer || e.type == "wolf", 10, 2));
        targetGoals.add(new NearestTargetGoal(this, 1, e => e.type == "chicken" || e.type == "rabbit" || e.type == "cod", 12));
        goals.add(new MeleeAttackGoal(this, 3, 1.4));
    }
    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}

public sealed class Parrot : TamableAnimal
{
    public override string type => "parrot";
    public Parrot(World world) : base(world)
    {
        width = 0.5; height = 0.9;
        maxHealth = 6; health = 6;
        speed = 0.15;
        foods = new List<string>();
        xpReward = 2;
        data.variant = gameRng.nextInt(5);
        goals.add(new FloatGoal(this, 0));
        goals.add(new PanicGoal(this, 1, 1.3));
        goals.add(new SitWhenOrderedGoal(this, 2));
        goals.add(new FollowOwnerGoal(this, 3));
        goals.add(new StrollGoal(this, 5, 1));
        goals.add(new LookAtPlayerGoal(this, 6));
    }
    public override void tick()
    {
        base.tick();
        data.airborne = !onGround;
        if (!onGround && vy < 0) { vy *= 0.6; }
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (!tamed && name != null && new[] { "wheat_seeds", "melon_seeds", "pumpkin_seeds", "beetroot_seeds" }.Contains(name))
        {
            tryTame(player, 1.0 / 3);
            return true;
        }
        if (name == "cookie")
        {
            hurt(100, "poison");
            return true;
        }
        return base.interact(player, stack);
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("feather", min: 1, max: 2) }; }
}

public sealed class Bee : Animal
{
    public override string type => "bee";
    public int angry = 0;
    public bool hasNectar = false;
    public bool hasStung = false;
    public int? hiveX = null;
    public int hiveY = 0, hiveZ = 0;
    public Bee(World world) : base(world)
    {
        width = 0.7; height = 0.6;
        maxHealth = 10; health = 10;
        speed = 0.12;
        attackDamage = 2;
        noGravity = false;
        gravityScale = 0.04;
        foods = new List<string> { "dandelion", "poppy", "blue_orchid", "allium", "azure_bluet", "red_tulip", "sunflower" };
        xpReward = 2;
        goals.add(new FloatGoal(this, 0));
        goals.add(new MeleeAttackGoal(this, 1, 1.3));
        goals.add(new BreedGoal(this, 2, (a, b) => this.spawnBaby(a, b)));
        goals.add(new TemptGoal(this, 3, foods, 1));
        goals.add(new PollinateGoal(this, 4));
        goals.add(new ReturnToHiveGoal(this, 5));
        goals.add(new StrollGoal(this, 6, 1, 60));
        targetGoals.add(new HurtByTargetGoal(this, 1, true));
    }
    public override void tick()
    {
        base.tick();
        // hover
        if (!onGround && vy < 0) { vy *= 0.7; }
        if (age % 30 == 0 && rng.nextFloat() < 0.5) { vy += 0.04; }
        if (hasStung)
        {
            int t = (data.stingTimer ?? 0) - 1;
            data.stingTimer = t;
            if (t <= 0) { hurt(100, "sting"); }
        }
    }
    public override void doMeleeAttack(LivingEntity target)
    {
        base.doMeleeAttack(target);
        target.addEffect("poison", 100, 0);
        hasStung = true;
        data.stingTimer = 1200;
        setTarget(null);
    }
    int hiveScanCooldown = 0;
    /// find a nearby hive/nest to deposit nectar in (throttled — the scan box
    /// is ~5k getBlock calls)
    public void locateHive()
    {
        if (hiveScanCooldown > 0) { hiveScanCooldown -= 1; return; }
        hiveScanCooldown = 40;
        int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dz = -12; dz <= 12; dz++)
            {
                for (int dx = -12; dx <= 12; dx++)
                {
                    ushort id = (ushort)(world.getBlock(bx + dx, by + dy, bz + dz) >> 4);
                    if (id == B.beehive || id == B.bee_nest)
                    {
                        hiveX = bx + dx; hiveY = by + dy; hiveZ = bz + dz;
                        return;
                    }
                }
            }
        }
    }
    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}
sealed class PollinateGoal : MoveToBlockGoal
{
    static readonly List<string> FLOWER_WORDS = new List<string> { "tulip", "dandelion", "poppy", "orchid", "allium", "bluet", "daisy", "cornflower", "sunflower", "azalea", "flower" };
    int pollinating = 0;
    public PollinateGoal(Mob mob, int priority) : base(mob, priority, (w, x, y, z) =>
        {
            int id = w.getBlock(x, y, z) >> 4;
            string name = id != 0 ? blockDefs[id].name : "";
            return PollinateGoal.FLOWER_WORDS.Any(f => name.Contains(f));
        }, 8, 1, 30)
    {
    }
    public override bool canUse()
    {
        if (!(mob is Bee bee) || bee.hasNectar) { return false; }
        return base.canUse();
    }
    public override void tick()
    {
        if (reached())
        {
            pollinating += 1;
            mob.world.hooks.addParticles("crit", mob.x, mob.y, mob.z, 1, 0.3, 0);
            if (pollinating > 100)
            {
                if (mob is Bee bee) { bee.hasNectar = true; }
                targetPos = null;
            }
        }
    }
}
sealed class ReturnToHiveGoal : Goal
{
    public ReturnToHiveGoal(Mob mob, int priority) : base(mob, priority) { }
    public override bool canUse()
    {
        if (!(mob is Bee bee) || !bee.hasNectar) { return false; }
        if (bee.hiveX == null) { bee.locateHive(); }
        return bee.hiveX != null;
    }
    public override void tick()
    {
        if (!(mob is Bee bee) || bee.hiveX == null) { return; }
        int hx = bee.hiveX.Value;
        bee.nav.moveTo((double)hx, (double)bee.hiveY, (double)bee.hiveZ, 1);
        double dx = bee.x - (double)hx, dy = bee.y - (double)bee.hiveY, dz = bee.z - (double)bee.hiveZ;
        if (dx * dx + dy * dy + dz * dz < 4)
        {
            int c = bee.world.getBlock(hx, bee.hiveY, bee.hiveZ);
            ushort id = (ushort)(c >> 4);
            if (id == B.beehive || id == B.bee_nest)
            {
                // player-placed hives have no BE until first use — create lazily
                var be = bee.world.getBlockEntity(hx, bee.hiveY, bee.hiveZ);
                if (be == null)
                {
                    var nb = new BlockEntityData(type: "beehive", x: hx, y: bee.hiveY, z: bee.hiveZ);
                    nb.honey = 0;
                    bee.world.setBlockEntity(nb);
                    be = nb;
                }
                be.honey = Math.Min(5, (be.honey ?? 0) + 1);
                bee.hasNectar = false;
                if ((be.honey ?? 0) >= 5)
                {
                    bee.world.setBlock(hx, bee.hiveY, bee.hiveZ, c); // refresh
                }
            }
            else
            {
                bee.hiveX = null;
            }
        }
    }
}

public sealed class Axolotl : Animal
{
    public override string type => "axolotl";
    public int playDead = 0;
    public Axolotl(World world) : base(world)
    {
        breathesWater = true;
        width = 0.75; height = 0.42;
        maxHealth = 14; health = 14;
        speed = 0.12;
        attackDamage = 2;
        foods = new List<string> { "tropical_fish_bucket" };
        xpReward = 3;
        data.variant = gameRng.nextFloat() < 0.001 ? 4 : gameRng.nextInt(4); // blue rare!
        goals.add(new RandomSwimGoal(this, 5, 1, 30));
        goals.add(new MeleeAttackGoal(this, 2, 1.2));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new NearestTargetGoal(this, 1, e =>
            new[] { "cod", "salmon", "tropical_fish", "pufferfish", "squid", "glow_squid", "drowned", "guardian" }.Contains(e.type)
        , 8));
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        bool r = base.hurt(amount, source, attacker);
        if (r && health < maxHealth / 2 && gameRng.nextFloat() < 0.33)
        {
            playDead = 200;
            addEffect("regeneration", 100, 0);
        }
        return r;
    }
    public override void tick()
    {
        if (playDead > 0)
        {
            playDead -= 1;
            baseLivingTick();
            travel();
            return;
        }
        base.tick();
    }
    public override List<DropEntry> drops() { return new List<DropEntry>(); }
}

public sealed class Frog : Animal
{
    public override string type => "frog";
    public Frog(World world) : base(world)
    {
        width = 0.5; height = 0.5;
        maxHealth = 10; health = 10;
        speed = 0.16;
        jumpPower = 0.6;
        foods = new List<string> { "slime_ball" };
        xpReward = 2;
        data.variant = gameRng.nextInt(3); // temperate/warm/cold
        addBasicGoals();
        targetGoals.add(new NearestTargetGoal(this, 1, e =>
            ((e as Mob)?.type == "slime" && (e as Slime)?.size == 1) || ((e as Mob)?.type == "magma_cube" && (e as Slime)?.size == 1)
        , 8));
        goals.add(new MeleeAttackGoal(this, 2, 1.2));
    }
    public override void doMeleeAttack(LivingEntity target)
    {
        // tongue eat
        if (target.type == "magma_cube" && (target as Slime)?.size == 1)
        {
            target.remove();
            var lights = new[] { "ochre_froglight", "pearlescent_froglight", "verdant_froglight" };
            spawnItem(world, x, y, z, new ItemStack(iid(lights[(data.variant ?? 0) % 3]), 1));
            world.hooks.playSound("entity.frog.eat", x, y, z, 1, 1);
        }
        else if (target.type == "slime" && (target as Slime)?.size == 1)
        {
            target.remove();
            spawnItem(world, x, y, z, new ItemStack(iid("slime_ball"), 1));
            world.hooks.playSound("entity.frog.eat", x, y, z, 1, 1);
        }
        else
        {
            base.doMeleeAttack(target);
        }
    }
    public override void spawnBaby(Mob a, Mob b)
    {
        // frogs lay frogspawn on water
        for (int _i = 0; _i < 8; _i++)
        {
            int x = ifloor(a.x) + a.rng.nextInt(5) - 2;
            int z = ifloor(a.z) + a.rng.nextInt(5) - 2;
            int y = ifloor(a.y) + 2;
            while (y > ifloor(a.y) - 3)
            {
                if ((a.world.getBlock(x, y, z) >> 4) == (int)B.water && (a.world.getBlock(x, y + 1, z) >> 4) == 0)
                {
                    a.world.setBlock(x, y + 1, z, (int)cell(B.frogspawn));
                    return;
                }
                y -= 1;
            }
        }
    }
}

public sealed class Tadpole : Animal
{
    public override string type => "tadpole";
    public Tadpole(World world) : base(world)
    {
        breathesWater = true;
        width = 0.4; height = 0.3;
        maxHealth = 6; health = 6;
        speed = 0.1;
        growUpAge = 24000;
        baby = true;
        xpReward = 1;
        goals.add(new RandomSwimGoal(this, 2, 1, 20));
    }
    public override void tick()
    {
        base.tick();
        if (!baby)
        {
            // grow into frog
            _ = spawnMobFn?.Invoke(world, "frog", x, y, z, new SpawnOpts());
            remove();
        }
    }
}

public sealed class Goat : Animal
{
    public override string type => "goat";
    public bool screaming = false;
    public int ramCooldown = 200;
    public Goat(World world) : base(world)
    {
        screaming = gameRng.nextFloat() < 0.02;   // baseline field-init order
        width = 0.9; height = 1.3;
        maxHealth = 10; health = 10;
        speed = 0.1;
        jumpPower = 0.6;
        attackDamage = 2;
        foods = new List<string> { "wheat" };
        xpReward = 3;
        addBasicGoals();
        goals.add(new RamGoal(this, 5));
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        if (stack != null && itemDef(stack.id).name == "bucket" && !baby)
        {
            (player as LivingEntity)?.replaceHeld(new ItemStack(iid("milk_bucket"), 1));
            world.hooks.playSound("entity.goat.milk", x, y, z, 1, 1);
            return true;
        }
        return base.interact(player, stack);
    }
}
sealed class RamGoal : Goal
{
    Entity target;
    bool charging = false;
    public RamGoal(Mob mob, int priority) : base(mob, priority) { }
    public override bool canUse()
    {
        if (!(mob is Goat g)) { return false; }
        int cd = g.ramCooldown;
        g.ramCooldown -= 1;
        if (cd > 0) { return false; }
        var targets = g.world.getEntitiesNear(g.x, g.y, g.z, 9, e =>
            e is LivingEntity && !ReferenceEquals(e, g) && (e as Entity)?.type != "goat"
        );
        if (targets.Count == 0 || g.rng.nextFloat() > 0.1) { return false; }
        target = targets[0] as Entity;
        g.ramCooldown = 600 + g.rng.nextInt(600);
        return true;
    }
    public override bool canContinue() { return target != null && !target.dead && charging; }
    public override void start()
    {
        charging = true;
        mob.world.hooks.playSound("entity.goat.ram_impact", mob.x, mob.y, mob.z, 0.6, 1.3);
    }
    public override void tick()
    {
        var m = mob;
        if (target == null) { return; }
        var t = target;
        m.nav.moveToEntity(t, 2.5);
        if (m.distanceToSq(t) < 2.5)
        {
            t.hurt(2, "mob", m);
            double dx = t.x - m.x, dz = t.z - m.z;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d == 0) { d = 1; }
            t.vx += dx / d * 1.2; t.vz += dz / d * 1.2; t.vy += 0.4;
            charging = false;
            mob.world.hooks.playSound("entity.goat.ram_impact", m.x, m.y, m.z, 1, 1);
        }
        if (m.distanceToSq(t) > 200) { charging = false; }
    }
}

public sealed class Turtle : Animal
{
    public override string type => "turtle";
    public Turtle(World world) : base(world)
    {
        breathesWater = true;
        width = 1.2; height = 0.4;
        maxHealth = 30; health = 30;
        speed = 0.07;
        foods = new List<string> { "seagrass" };
        xpReward = 3;
        addBasicGoals();
        goals.add(new MoveToBlockGoal(this, 5, (w, x, y, z) => (w.getBlock(x, y, z) >> 4) == (int)B.water, 12, 1.2, 60));
    }
    public override void spawnBaby(Mob a, Mob b)
    {
        // lay eggs on sand
        int x = ifloor(a.x), z = ifloor(a.z);
        int y = a.world.surfaceY(x, z);
        if ((a.world.getBlock(x, y - 1, z) >> 4) == (int)B.sand)
        {
            a.world.setBlock(x, y, z, (int)cell(B.turtle_egg, a.rng.nextInt(4)));
        }
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("seagrass", chance: 1) }; }
}

public sealed class Dolphin : Animal
{
    public override string type => "dolphin";
    public bool treasureHunting = false;
    public Dolphin(World world) : base(world)
    {
        width = 0.9; height = 0.6;
        maxHealth = 10; health = 10;
        speed = 0.14;
        attackDamage = 3;
        xpReward = 3;
        goals.add(new RandomSwimGoal(this, 4, 1.2, 15));
        goals.add(new LookAtPlayerGoal(this, 6));
        targetGoals.add(new HurtByTargetGoal(this, 1, true));
        goals.add(new MeleeAttackGoal(this, 2, 1.4));
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (name == "cod" || name == "salmon")
        {
            (player as LivingEntity)?.consumeHeld(1);
            // grant dolphin's grace & lead toward treasure
            (player as LivingEntity)?.addEffect("dolphins_grace", 2400, 0);
            world.hooks.playSound("entity.dolphin.eat", x, y, z, 1, 1);
            treasureHunting = true;
            return true;
        }
        return false;
    }
    public override void tick()
    {
        base.tick();
        if (inWater && age % 4 == 0 && Math.Sqrt(vx * vx + vz * vz) > 0.08)
        {
            world.hooks.addParticles("bubble", x, y + 0.3, z, 1, 0.3, 0);
        }
        // grace aura to nearby swimming players
        if (age % 40 == 0)
        {
            foreach (var p in world.getEntitiesNear(x, y, z, 9, filter: o => (o as Entity)?.isPlayer ?? false))
            {
                if (p is LivingEntity pl && pl.inWater) { pl.addEffect("dolphins_grace", 100, 0); }
            }
        }
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("cod", min: 0, max: 1) }; }
}

public class Squid : Animal
{
    public override string type => "squid";
    public Squid(World world) : base(world)
    {
        breathesWater = true;
        breathesWaterOnly = true;
        width = 0.8; height = 0.8;
        maxHealth = 10; health = 10;
        speed = 0.08;
        category = "water";
        xpReward = 2;
        goals.add(new RandomSwimGoal(this, 2, 1, 20));
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        bool r = base.hurt(amount, source, attacker);
        if (r)
        {
            world.hooks.addParticles("squid_ink", x, y + 0.4, z, 12, 0.4, 0);
            // jet away
            double ang = gameRng.nextFloat() * Math.PI * 2;
            vx = detSin(ang) * 0.3;
            vz = detCos(ang) * 0.3;
        }
        return r;
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("ink_sac", min: 1, max: 3, lootingBonus: 1) }; }
}
public sealed class GlowSquid : Squid
{
    public override string type => "glow_squid";
    public GlowSquid(World world) : base(world) { }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("glow_ink_sac", min: 1, max: 3, lootingBonus: 1) }; }
}

public sealed class Bat : Mob
{
    public override string type => "bat";
    public bool hanging = false;
    public Bat(World world) : base(world)
    {
        category = "ambient";
        width = 0.5; height = 0.9;
        maxHealth = 6; health = 6;
        noGravity = true;
        xpReward = 1;
    }
    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) { return; }
        int above = world.getBlock(ifloor(x), ifloor(y + 1), ifloor(z)) >> 4;
        if (hanging)
        {
            data.hanging = true;
            if (above == 0 || rng.nextFloat() < 0.005) { hanging = false; }
            vx = 0; vy = 0; vz = 0;
        }
        else
        {
            data.hanging = false;
            // erratic flight
            if (age % 4 == 0)
            {
                vx += (rng.nextFloat() - 0.5) * 0.12;
                vy += (rng.nextFloat() - 0.5) * 0.1;
                vz += (rng.nextFloat() - 0.5) * 0.12;
            }
            vy -= 0.005;
            move(vx, vy, vz);
            vx *= 0.9; vy *= 0.9; vz *= 0.9;
            // only steer toward real motion — assigning yaw from near-zero
            // noise velocity every tick made bats spin like tops
            if (vx * vx + vz * vz > 0.0004)
            {
                double target = detAtan2(-vx, vz);
                yaw += clampD(wrapAngle(target - yaw), -0.5, 0.5);
            }
            if (above != 0 && rng.nextFloat() < 0.01) { hanging = true; }
            // despawn far
            if (!persistent && age > 600 && rng.nextFloat() < 0.005) { remove(); }
        }
    }
}

public sealed class PolarBear : Animal
{
    public override string type => "polar_bear";
    public PolarBear(World world) : base(world)
    {
        width = 1.4; height = 1.4;
        maxHealth = 30; health = 30;
        speed = 0.13;
        attackDamage = 6;
        xpReward = 5;
        addBasicGoals();
        goals.add(new MeleeAttackGoal(this, 1, 1.25));
        targetGoals.add(new HurtByTargetGoal(this, 1, true));
        // protective parents
        targetGoals.add(new NearestTargetGoal(this, 2, e =>
        {
            if (!e.isPlayer) { return false; }
            // attack players near cubs
            var cubs = this.world.getEntitiesNear(this.x, this.y, this.z, 8, e2 =>
                (e2 as Mob)?.type == "polar_bear" && ((e2 as Mob)?.baby ?? false)
            );
            return cubs.Count != 0;
        }, 12));
    }
    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("cod", min: 0, max: 2, lootingBonus: 1), new DropEntry("salmon", min: 0, max: 2, chance: 0.5) };
    }
}

public sealed class Panda : Animal
{
    public override string type => "panda";
    public Panda(World world) : base(world)
    {
        width = 1.3; height = 1.25;
        maxHealth = 20; health = 20;
        speed = 0.1;
        attackDamage = 6;
        foods = new List<string> { "bamboo" };
        xpReward = 4;
        data.gene = (new[] { "normal", "lazy", "playful", "worried", "weak", "aggressive", "brown" })[gameRng.nextInt(7)];
        addBasicGoals(0.9);
        targetGoals.add(new HurtByTargetGoal(this, 1));
        goals.add(new MeleeAttackGoal(this, 2, 1.1));
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("bamboo", min: 0, max: 2) }; }
}

public sealed class Strider : Animal
{
    public override string type => "strider";
    public bool saddled = false;
    public int boostTime = 0;
    public Strider(World world) : base(world)
    {
        width = 0.9; height = 1.7;
        maxHealth = 20; health = 20;
        speed = 0.18;
        foods = new List<string> { "warped_fungus" };
        xpReward = 3;
        addBasicGoals();
    }
    public override void tick()
    {
        base.tick();
        // walk on lava!
        if (inLava)
        {
            vy = Math.Max(vy, 0.12);
            inLava = false; // immune
            fireTicks = 0;
        }
        data.cold = !inLava && (world.getBlock(ifloor(x), ifloor(y - 0.2), ifloor(z)) >> 4) != (int)B.lava;
        if (passengers.FirstOrDefault() is LivingEntity rider && rider.isPlayer && saddled)
        {
            if (rider.mainHand is ItemStack held && itemDef(held.id).name == "warped_fungus_on_a_stick")
            {
                yaw = rider.yaw;
                moveForward = 0.6 + (boostTime > 0 ? 0.4 : 0);
                if (boostTime > 0) { boostTime -= 1; }
            }
            else { moveForward = 0; }
        }
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (source == "lava" || source == "fire") { return false; }
        return base.hurt(amount, source, attacker);
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (name == "saddle" && !saddled && !baby)
        {
            saddled = true;
            (player as LivingEntity)?.consumeHeld(1);
            return true;
        }
        if (saddled && !((player as LivingEntity)?.sneaking ?? false))
        {
            player.mount(this);
            return true;
        }
        return base.interact(player, stack);
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("string", min: 2, max: 5, lootingBonus: 1) }; }
}

public sealed class Camel : Animal
{
    public override string type => "camel";
    public bool saddled = false;
    public int dashCooldown = 0;
    public Camel(World world) : base(world)
    {
        width = 1.7; height = 2.375;
        maxHealth = 32; health = 32;
        speed = 0.09;
        stepHeight = 1.5;
        foods = new List<string> { "cactus" };
        xpReward = 4;
        addBasicGoals();
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (name == "saddle" && !saddled && !baby)
        {
            saddled = true;
            (player as LivingEntity)?.consumeHeld(1);
            return true;
        }
        if (saddled && !((player as LivingEntity)?.sneaking ?? false) && !isFood(stack))
        {
            player.mount(this);
            return true;
        }
        return base.interact(player, stack);
    }
    public override void tick()
    {
        base.tick();
        if (dashCooldown > 0) { dashCooldown -= 1; }
        if (passengers.FirstOrDefault() is LivingEntity rider && rider.isPlayer && saddled)
        {
            yaw = rider.yaw;
            moveForward = rider.moveForward * 0.9;
            moveStrafe = rider.moveStrafe * 0.5;
            if (rider.jumping && dashCooldown <= 0 && onGround)
            {
                // dash!
                dashCooldown = 55;
                vx += -detSin(yaw) * 1.4;
                vz += detCos(yaw) * 1.4;
                vy += 0.25;
                world.hooks.playSound("entity.camel.dash", x, y, z, 1, 1);
            }
        }
    }
}

public sealed class Sniffer : Animal
{
    public override string type => "sniffer";
    public int digCooldown = 0;
    public Sniffer(World world) : base(world)
    {
        width = 1.9; height = 1.75;
        maxHealth = 14; health = 14;
        speed = 0.08;
        foods = new List<string> { "torchflower_seeds" };
        xpReward = 5;
        addBasicGoals(0.9);
        goals.add(new SniffDigGoal(this, 5));
    }
}
sealed class SniffDigGoal : Goal
{
    int digging = 0;
    public SniffDigGoal(Mob mob, int priority) : base(mob, priority) { }
    public override bool canUse()
    {
        if (!(mob is Sniffer m)) { return false; }
        if (m.digCooldown > 0) { m.digCooldown -= 1; return false; }
        if (m.rng.nextInt(400) != 0) { return false; }
        int below = m.world.getBlock(ifloor(m.x), ifloor(m.y - 1), ifloor(m.z)) >> 4;
        return below == (int)B.grass_block || below == (int)B.dirt || below == (int)B.moss_block || below == (int)B.mud;
    }
    public override bool canContinue() { return digging < 120; }
    public override void start() { digging = 0; mob.nav.stop(); }
    public override void tick()
    {
        digging += 1;
        var m = mob;
        if (digging % 10 == 0)
        {
            m.world.hooks.addParticles("block", m.x, m.y + 0.2, m.z, 4, 0.5, (int)cell(B.dirt));
            m.world.hooks.playSound("entity.sniffer.digging", m.x, m.y, m.z, 0.5, 1);
        }
        if (digging == 119)
        {
            var loot = rollLoot("sniffer_digging", ref m.rng);
            foreach (var s in loot) { spawnItem(m.world, m.x, m.y, m.z, s); }
            if (m is Sniffer sn) { sn.digCooldown = 1200; }
        }
    }
}

public sealed class Allay : Mob
{
    public override string type => "allay";
    public ItemStack likedItem;
    public List<ItemStack> heldItems = new List<ItemStack>();
    public Allay(World world) : base(world)
    {
        category = "creature";
        width = 0.35; height = 0.6;
        maxHealth = 20; health = 20;
        speed = 0.1;
        noGravity = true;
        xpReward = 2;
        goals.add(new AllayCollectGoal(this, 1));
        goals.add(new FollowOwnerGoal(this, 2, 3, 16));
        goals.add(new StrollGoal(this, 5, 1, 40));
        goals.add(new LookAtPlayerGoal(this, 6));
    }
    public override void tick()
    {
        vy += 0.02; // hover
        base.tick();
        vy *= 0.85;
        if (age % 10 == 0) { world.hooks.addParticles("crit", x, y + 0.3, z, 1, 0.2, 0); }
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        if (stack != null && likedItem == null)
        {
            var liked = stack.copy();
            liked.count = 1;
            likedItem = liked;
            ownerId = player.id;
            persistent = true;
            (player as LivingEntity)?.consumeHeld(1);
            world.hooks.playSound("entity.allay.item_given", x, y, z, 1, 1);
            return true;
        }
        if (stack == null && heldItems.Count != 0)
        {
            foreach (var s in heldItems) { spawnItem(world, x, y, z, s); }
            heldItems = new List<ItemStack>();
            return true;
        }
        return false;
    }
}
sealed class AllayCollectGoal : Goal
{
    ItemEntity targetItem;
    public AllayCollectGoal(Mob mob, int priority) : base(mob, priority) { }
    public override bool canUse()
    {
        if (!(mob is Allay a) || a.likedItem == null || a.heldItems.Count >= 1) { return false; }
        var liked = a.likedItem;
        var items = a.world.getEntitiesNear(a.x, a.y, a.z, 16, e =>
            (e as ItemEntity)?.stack.id == liked.id && !e.dead
        );
        targetItem = items.FirstOrDefault() as ItemEntity;
        return targetItem != null;
    }
    public override bool canContinue() { return targetItem != null && !targetItem.dead; }
    public override void tick()
    {
        if (!(mob is Allay a) || targetItem == null) { return; }
        var t = targetItem;
        double dx = t.x - a.x, dy = t.y - a.y, dz = t.z - a.z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d == 0) { d = 1; }
        a.vx += dx / d * 0.03;
        a.vy += dy / d * 0.03;
        a.vz += dz / d * 0.03;
        a.yaw = detAtan2(-dx, dz);
        if (d < 1.2)
        {
            a.heldItems.Add(t.stack);
            t.remove();
            targetItem = null;
            a.world.hooks.playSound("entity.allay.item_taken", a.x, a.y, a.z, 1, 1);
        }
    }
}

// fish ------------------------------------------------------------------------
public class AbstractFish : Mob
{
    public override string type => "cod";
    public string bucketItem = "cod_bucket";
    public AbstractFish(World world) : base(world)
    {
        category = "water";
        breathesWater = true;
        breathesWaterOnly = true;
        width = 0.5; height = 0.4;
        maxHealth = 3; health = 3;
        speed = 0.12;
        xpReward = 1;
        goals.add(new RandomSwimGoal(this, 2, 1, 12));
        goals.add(new AvoidEntityGoal(this, 1, e => e.isPlayer, 6, 1.6));
    }
    public override bool interact(Entity player, ItemStack stack)
    {
        if (stack != null && itemDef(stack.id).name == "water_bucket")
        {
            (player as LivingEntity)?.replaceHeld(new ItemStack(iid(bucketItem), 1));
            remove();
            world.hooks.playSound("item.bucket.fill_fish", x, y, z, 1, 1);
            return true;
        }
        return false;
    }
    public override void tick()
    {
        base.tick();
        if (!inWater && onGround)
        {
            // flop
            if (age % 10 == 0)
            {
                vy = 0.3;
                vx = (rng.nextFloat() - 0.5) * 0.2;
                vz = (rng.nextFloat() - 0.5) * 0.2;
                world.hooks.playSound("entity.cod.flop", x, y, z, 0.4, 1.2);
            }
        }
    }
}
public sealed class Cod : AbstractFish
{
    public override string type => "cod";
    public Cod(World world) : base(world)
    {
        bucketItem = "cod_bucket";
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("cod"), new DropEntry("bone_meal", chance: 0.05) }; }
}
public sealed class Salmon : AbstractFish
{
    public override string type => "salmon";
    public Salmon(World world) : base(world)
    {
        bucketItem = "salmon_bucket";
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("salmon"), new DropEntry("bone_meal", chance: 0.05) }; }
}
public sealed class TropicalFish : AbstractFish
{
    public override string type => "tropical_fish";
    public TropicalFish(World world) : base(world)
    {
        bucketItem = "tropical_fish_bucket";
        data.pattern = gameRng.nextInt(12);
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("tropical_fish") }; }
}
public sealed class Pufferfish : AbstractFish
{
    public override string type => "pufferfish";
    public int puffed = 0;
    public Pufferfish(World world) : base(world)
    {
        bucketItem = "pufferfish_bucket";
    }
    public override void tick()
    {
        base.tick();
        var threats = world.getEntitiesNear(x, y, z, 2.5, e =>
            ((e as Entity)?.isPlayer ?? false) || (e as Entity)?.type == "axolotl"
        );
        if (threats.Count != 0)
        {
            puffed = Math.Min(60, puffed + 3);
            foreach (var t in threats)
            {
                if (puffed > 20 && age % 20 == 0)
                {
                    (t as Entity)?.hurt(2, "mob", this);
                    (t as LivingEntity)?.addEffect("poison", 60, 0);
                }
            }
        }
        else if (puffed > 0) { puffed -= 1; }
        data.puffed = puffed > 20;
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("pufferfish") }; }
}

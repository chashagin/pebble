// Non-living entities — dropped items, XP orbs,
// falling blocks, primed TNT, lightning bolts, end crystals, area effect
// clouds, eyes of ender.
//
// cosmetic jitter (bob phase, drop velocities, anvil-degrade chance,
// eye-of-ender survival) is deliberately nondeterministic; golden tests
// must not hash values derived from it.
//
// Defines static class: MiscGlobals (surface via `global using static PebbleCore.MiscGlobals`).
// It holds the module-level free funcs / late-bound vars:
//   spawnItem, spawnXP, registerEntityHelpers, explodeFn, bindExplode,
//   struckByLightningFn, bindLightningConversion, crystalDestroyedFn, bindCrystalDestroyed.
// Types ItemEntity, XPOrb, FallingBlockEntity, TNTEntity, LightningBolt, EndCrystal,
// AreaEffectCloud, EyeOfEnderEntity live at namespace level.
//
// External (unqualified) symbols resolved elsewhere in the assembly:
//   ifloor, gameRng, cell, B, blockDefs, blockToItem, detSin, detCos, itemDef,
//   maxStackOf, canMerge, iid, bindSpawners, Dim, World, Entity, LivingEntity, ItemStack.
//
// `Double.random(in: 0..<1)` → System.Random.Shared.NextDouble() (cosmetic, never hashed;
// matches the hurt-sound pitch jitter convention already used in Living.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PebbleCore;

public sealed class ItemEntity : Entity
{
    public override string type => "item";
    public ItemStack stack = new ItemStack(0, 1);
    public int pickupDelay = 10;
    public int lifeTime = 6000;
    public double bobOffset = System.Random.Shared.NextDouble() * Math.PI * 2;

    public ItemEntity(World world) : base(world)
    {
        width = 0.25;
        height = 0.25;
    }

    public override void tick()
    {
        baseTick();
        if (pickupDelay > 0) { pickupDelay -= 1; }
        lifeTime -= 1;
        if (lifeTime <= 0) { remove(); return; }

        // float up in water
        if (inWater)
        {
            vy = Math.Min(vy + 0.04, 0.06);
            vx *= 0.95; vz *= 0.95;
        }
        else if (inLava)
        {
            string nm = itemDef(stack.id).name;
            if (nm == "netherite_ingot" || nm == "ancient_debris" || nm.Contains("netherite"))
            {
                vy = Math.Min(vy + 0.05, 0.08);
            }
            else
            {
                remove();
                return;
            }
        }
        else if (!noGravity)
        {
            vy -= 0.04;
        }
        move(vx, vy, vz);
        double drag = onGround ? 0.6 : 0.98;
        vx *= drag; vz *= drag;
        vy *= 0.98;

        // merge with nearby item entities
        if (age % 20 == 0 && stack.count < maxStackOf(stack))
        {
            foreach (var e in world.getEntitiesInBox(bb().expand(0.8, 0.5, 0.8), except: this))
            {
                if (!(e is ItemEntity other) || other.dead) { continue; }
                if (canMerge(stack, other.stack) && stack.count + other.stack.count <= maxStackOf(stack))
                {
                    stack.count += other.stack.count;
                    other.remove();
                }
            }
        }
        if (fireTicks > 0) { remove(); }
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (source == "explosion" || source == "fire" || source == "lava") { remove(); return true; }
        return false;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        try
        {
            byte[] enc = JsonSerializer.SerializeToUtf8Bytes(stack, stackJsonOpts);
            object obj = JsonSerializer.Deserialize<object>(enc);
            d["stack"] = obj;
        }
        catch { }
        d["pickupDelay"] = pickupDelay;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        ItemStack s = null;
        object raw = d.TryGetValue("stack", out var v) ? v : null;
        if (raw != null)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
                s = JsonSerializer.Deserialize<ItemStack>(bytes, stackJsonOpts);
            }
            catch { s = null; }
        }
        stack = s ?? new ItemStack(0, 1);
        pickupDelay = inum(d.TryGetValue("pickupDelay", out var pv) ? pv : null);
    }

    // Swift's synthesized Codable round-trips ItemStack through JSON, omitting nil keys.
    private static readonly JsonSerializerOptions stackJsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };
}

public sealed class XPOrb : Entity
{
    public override string type => "xp_orb";
    public int amount = 1;
    public int lifeTime = 6000;
    public Entity followTarget;

    public XPOrb(World world) : base(world)
    {
        width = 0.4;
        height = 0.4;
    }
    public override void tick()
    {
        baseTick();
        lifeTime -= 1;
        if (lifeTime <= 0) { remove(); return; }
        if (inWater) { vy = Math.Min(vy + 0.03, 0.05); }
        else if (!noGravity) { vy -= 0.03; }
        // magnet toward player
        if (age % 10 == 0 || followTarget?.dead == true)
        {
            followTarget = null;
            var players = world.getEntitiesNear(x, y, z, 8, e =>
                ((e as Entity)?.isPlayer ?? false) && !e.dead);
            if (players.Count != 0) { followTarget = players[0] as Entity; }
        }
        if (followTarget is Entity t)
        {
            double dx = t.x - x;
            double dy = t.eyeY() - 0.5 - y;
            double dz = t.z - z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < 8)
            {
                double f = (1 - d / 8) * 0.1;
                vx += dx / d * f;
                vy += dy / d * f;
                vz += dz / d * f;
            }
        }
        move(vx, vy, vz);
        double drag = onGround ? 0.7 : 0.98;
        vx *= drag; vz *= drag;
        vy *= 0.98;
    }
    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["amount"] = amount;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        amount = (d.TryGetValue("amount", out var v) && v is IConvertible c && v is not string) ? Convert.ToInt32(c) : 1;
    }
}

public sealed class FallingBlockEntity : Entity
{
    public override string type => "falling_block";
    public int blockCell = 0;
    public FallingBlockEntity(World world) : base(world)
    {
        width = 0.98;
        height = 0.98;
    }
    public override void tick()
    {
        baseTick();
        vy -= 0.04;
        move(vx, vy, vz);
        vx *= 0.98; vy *= 0.98; vz *= 0.98;
        if (onGround)
        {
            int bx = ifloor(x), by = ifloor(y + 0.01), bz = ifloor(z);
            int cur = world.getBlock(bx, by, bz);
            int curId = cur >> 4;
            if (curId == 0 || blockDefs[curId].replaceable || !blockDefs[curId].solid)
            {
                // anvil damage on landing
                int placeCell = blockCell;
                int bid = blockCell >> 4;
                if ((bid == (int)B.anvil || bid == (int)B.chipped_anvil || bid == (int)B.damaged_anvil) && fallDistance > 1)
                {
                    // hurt entities below
                    foreach (var e in world.getEntitiesInBox(bb()))
                    {
                        (e as Entity)?.hurt(Math.Min(40, Math.Ceiling(fallDistance * 2)), "anvil");
                    }
                    if (gameRng.nextFloat() < 0.05 * fallDistance)
                    {
                        placeCell = bid == (int)B.anvil ? (int)cell(B.chipped_anvil, blockCell & 15)
                            : bid == (int)B.chipped_anvil ? (int)cell(B.damaged_anvil, blockCell & 15) : 0;
                    }
                    world.hooks.playSound("block.anvil.land", x, y, z, 1, 1);
                }
                if (placeCell != 0) { world.setBlock(bx, by, bz, placeCell); }
            }
            else
            {
                // can't place — drop as item
                int itemId = blockToItem[blockCell >> 4];
                if (itemId >= 0) { spawnItem(world, x, y, z, new ItemStack((int)itemId, 1)); }
            }
            remove();
        }
        if (age > 600)
        {
            remove();
        }
    }
    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["blockCell"] = blockCell;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        blockCell = inum(d.TryGetValue("blockCell", out var v) ? v : null);
    }
}

public sealed class TNTEntity : Entity
{
    public override string type => "tnt";
    public int fuse = 80;
    public double power = 4.0;
    public TNTEntity(World world) : base(world)
    {
        width = 0.98;
        height = 0.98;
    }
    public override void tick()
    {
        baseTick();
        vy -= 0.04;
        move(vx, vy, vz);
        vx *= 0.98; vy *= 0.98; vz *= 0.98;
        if (onGround) { vx *= 0.7; vz *= 0.7; }
        if (age % 5 == 0)
        {
            world.hooks.addParticles("smoke", x, y + 1, z, 1, 0.05, 0);
        }
        fuse -= 1;
        if (fuse <= 0)
        {
            remove();
            explodeFn?.Invoke(world, x, y + 0.5, z, power, true, this);
        }
    }
    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["fuse"] = fuse;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        fuse = (d.TryGetValue("fuse", out var v) && v is IConvertible c && v is not string) ? Convert.ToInt32(c) : 80;
    }
}

public sealed class LightningBolt : Entity
{
    public override string type => "lightning";
    public int life = 6;
    public LightningBolt(World world) : base(world)
    {
        width = 0.1; height = 12;
        noGravity = true;
    }
    public override void tick()
    {
        if (age == 0)
        {
            world.hooks.playSound("entity.lightning_bolt.thunder", x, y, z, 6, 0.8 + System.Random.Shared.NextDouble() * 0.2);
            world.hooks.playSound("entity.lightning_bolt.impact", x, y, z, 2, 0.6);
            // fire + damage + mob conversion
            if (world.rule("doFireTick"))
            {
                int bx = ifloor(x), bz = ifloor(z);
                int by = world.surfaceY(bx, bz);
                if ((world.getBlock(bx, by, bz) >> 4) == 0)
                {
                    world.setBlock(bx, by, bz, (int)cell(B.fire));
                }
            }
            foreach (var e in world.getEntitiesNear(x, y, z, 4))
            {
                if (!(e is Entity ent) || ReferenceEquals(ent, this)) { continue; }
                struckByLightningFn?.Invoke(ent);
                ent.hurt(5, "lightning");
                ent.fireTicks = Math.Max(ent.fireTicks, 160);
            }
        }
        age += 1;
        if (age > life) { remove(); }
    }
}

public sealed class EndCrystal : Entity
{
    public override string type => "end_crystal";
    public bool showBottom = true;
    public (int, int, int)? beamTarget = null;
    public EndCrystal(World world) : base(world)
    {
        width = 2; height = 2;
        noGravity = true;
    }
    public override void tick()
    {
        age += 1;
        // fire below
        int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
        if (world.dim == Dim.end && (world.getBlock(bx, by, bz) >> 4) == 0)
        {
            world.setBlock(bx, by, bz, (int)cell(B.fire));
        }
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (dead || (source == "explosion" && amount < 0)) { return false; }
        remove();
        explodeFn?.Invoke(world, x, y + 1, z, 6, false, this);
        crystalDestroyedFn?.Invoke(this, attacker);
        return true;
    }
    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["showBottom"] = showBottom;
        if (beamTarget is (int, int, int) bt) { d["beamTarget"] = new object[] { bt.Item1, bt.Item2, bt.Item3 }; }
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        showBottom = (d.TryGetValue("showBottom", out var sv) && sv is bool sb) ? sb : true;
        var bt = dec<List<int>>(d.TryGetValue("beamTarget", out var bv) ? bv : null);
        if (bt != null && bt.Count == 3)
        {
            beamTarget = (bt[0], bt[1], bt[2]);
        }
        else
        {
            beamTarget = null;
        }
    }

    private static T dec<T>(object raw)
    {
        if (raw == null) { return default; }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
            return JsonSerializer.Deserialize<T>(bytes);
        }
        catch { return default; }
    }
}

public sealed class AreaEffectCloud : Entity
{
    public override string type => "effect_cloud";
    public double radius = 3.0;
    public int duration = 600;
    public string effectId = "instant_damage";
    public int amplifier = 0;
    public int reapplyDelay = 20;
    private Dictionary<int, int> affected = new Dictionary<int, int>();
    public string particleType = "dragon_breath";
    public AreaEffectCloud(World world) : base(world)
    {
        width = 6; height = 0.5;
        noGravity = true;
    }
    public override void tick()
    {
        age += 1;
        if (age > duration) { remove(); return; }
        radius = Math.Max(0.5, radius - 0.005);
        if (age % 2 == 0)
        {
            double a = System.Random.Shared.NextDouble() * Math.PI * 2;
            double r = System.Random.Shared.NextDouble() * radius;
            world.hooks.addParticles(particleType, x + detCos(a) * r, y + 0.3, z + detSin(a) * r, 1, 0.1, 0);
        }
        if (age % 5 == 0)
        {
            foreach (var e in world.getEntitiesNear(x, y + 0.25, z, radius))
            {
                if (!(e is LivingEntity liv) || liv.dead) { continue; }
                int last = affected.TryGetValue(e.id, out var lv) ? lv : -999;
                if (age - last < reapplyDelay) { continue; }
                affected[e.id] = age;
                liv.addEffect(effectId, effectId.StartsWith("instant") ? 1 : 200, amplifier);
            }
        }
    }
}

public sealed class EyeOfEnderEntity : Entity
{
    public override string type => "eye_of_ender";
    public double targetX = 0.0, targetZ = 0.0;
    public int life = 0;
    public double surviveChance = 0.8;
    public EyeOfEnderEntity(World world) : base(world)
    {
        width = 0.25; height = 0.25;
        noGravity = true;
    }
    public override void tick()
    {
        age += 1;
        life += 1;
        double dx = targetX - x, dz = targetZ - z;
        double d = Math.Sqrt(dx * dx + dz * dz);
        if (d > 1)
        {
            vx = dx / d * 0.3;
            vz = dz / d * 0.3;
            vy = life < 20 ? 0.18 : (y < (double)(world.surfaceY(ifloor(x), ifloor(z)) + 12) ? 0.08 : -0.02);
        }
        else
        {
            vx *= 0.8; vz *= 0.8; vy = -0.01;
        }
        x += vx; y += vy; z += vz;
        world.hooks.addParticles("portal", x, y, z, 2, 0.15, 0);
        if (life > 60)
        {
            remove();
            if (gameRng.nextFloat() < surviveChance)
            {
                spawnItem(world, x, y, z, new ItemStack(iid("ender_eye"), 1));
            }
            else
            {
                world.hooks.addParticles("crit", x, y, z, 12, 0.3, 0);
                world.hooks.playSound("entity.ender_eye.death", x, y, z, 1, 1);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// helpers + late binding
// ---------------------------------------------------------------------------
public static class MiscGlobals
{
    public static ItemEntity spawnItem(World world, double x, double y, double z, ItemStack stack,
                                       double vx = 0, double vy = 0.2, double vz = 0)
    {
        var e = new ItemEntity(world);
        e.setPos(x, y, z);
        e.stack = stack;
        e.vx = vx + (gameRng.nextFloat() - 0.5) * 0.08;
        e.vy = vy;
        e.vz = vz + (gameRng.nextFloat() - 0.5) * 0.08;
        world.addEntity(e);
        return e;
    }
    public static void spawnXP(World world, double x, double y, double z, int amountIn)
    {
        int amount = amountIn;
        while (amount > 0)
        {
            int size = amount > 37 ? 37 : amount > 17 ? 17 : amount > 7 ? 7 : amount > 3 ? 3 : 1;
            amount -= size;
            var orb = new XPOrb(world);
            orb.setPos(x + (gameRng.nextFloat() - 0.5) * 0.5, y, z + (gameRng.nextFloat() - 0.5) * 0.5);
            orb.amount = size;
            orb.vx = (gameRng.nextFloat() - 0.5) * 0.2;
            orb.vy = gameRng.nextFloat() * 0.2 + 0.1;
            orb.vz = (gameRng.nextFloat() - 0.5) * 0.2;
            world.addEntity(orb);
        }
    }

    /// the original design binds the spawners at module init; Swift does it lazily via
    /// registerEntityHelpers() (called from registerAllEntities()).
    public static void registerEntityHelpers()
    {
        bindSpawners((w, x, y, z, s, vx, vy, vz) => { _ = spawnItem(w, x, y, z, s, vx, vy, vz); }, spawnXP);
    }

    public static Action<World, double, double, double, double, bool, Entity> explodeFn;
    public static void bindExplode(Action<World, double, double, double, double, bool, Entity> fn) { explodeFn = fn; }
    public static Action<Entity> struckByLightningFn;
    public static void bindLightningConversion(Action<Entity> fn) { struckByLightningFn = fn; }
    public static Action<EndCrystal, Entity> crystalDestroyedFn;
    public static void bindCrystalDestroyed(Action<EndCrystal, Entity> fn) { crystalDestroyedFn = fn; }
}

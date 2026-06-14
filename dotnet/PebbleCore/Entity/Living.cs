// LivingEntity — health, status effects, equipment,
// movement physics (ground friction / water / lava / climbing), armor damage
// math, death.
//
// `effects` is insertion-ordered with replace-in-place on re-add — that is deterministic
// Map.set semantics and the iteration order feeds damage/heal tick order.
//
// Defines static class: LivingGlobals (surface spawnItemFn/spawnXPFn/bindSpawners via
// global using static). All other module symbols (ifloor, gameRng, ActiveEffect,
// effectDef, itemDef, enchLevel, iid, itemExists, Entity, Player, ItemStack, World, …)
// resolve from the rest of the assembly.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public struct DropEntry
{
    public string item;
    public int? min;
    public int? max;
    public double? chance;
    public double? lootingBonus;

    public DropEntry(string item, int? min = null, int? max = null, double? chance = null, double? lootingBonus = null)
    {
        this.item = item; this.min = min; this.max = max;
        this.chance = chance; this.lootingBonus = lootingBonus;
    }
}

public class LivingEntity : Entity
{
    public double health = 20.0;
    public double maxHealth = 20.0;
    public double absorption = 0.0;
    public List<ActiveEffect> effects = new List<ActiveEffect>();   // ordered like an insertion-ordered map
    /// equipment: 0 head 1 chest 2 legs 3 feet
    public ItemStack[] armor = new ItemStack[] { null, null, null, null };
    private ItemStack _mainHand;
    public virtual ItemStack mainHand
    {
        get { return _mainHand; }
        set { _mainHand = value; }
    }
    public ItemStack offHand;
    public int hurtTime = 0;
    public int deathTime = 0;
    public double attackCooldown = 0.0;
    /// movement attributes
    public double speed = 0.1;
    public double kbResist = 0.0;
    public double jumpPower = 0.42;
    /// AI movement intent (set by goals / input)
    public double moveForward = 0.0;
    public double moveStrafe = 0.0;
    public bool jumping = false;
    public bool sprinting = false;
    public bool sneaking = false;
    public double limbSwing = 0.0;
    public double limbAmp = 0.0;
    public double attackAnim = 0.0;
    public double headYaw = 0.0;
    public double bodyYaw = 0.0;
    public Entity lastAttacker;
    /// set by Player on outgoing attacks (wolf assist targeting reads it)
    public Entity lastHurtTarget;
    public int lastHurtByPlayerTime = 0;
    public RandomX rng = new RandomX(0);   // seeded from gameRng in init (baseline field-init order)
    public int xpReward = 5;
    /// water mobs (baseline dynamic props)
    public bool breathesWater = false;

    public LivingEntity(World world) : base(world)
    {
        // baseline Living field initializer order: rng seeds from gameRng here
        rng = new RandomX((uint)gameRng.nextInt(1000000000));
    }

    public bool breathesWaterOnly = false;
    /// player-only props read through `(e as any)` in baseline goal filters
    public virtual int gameMode => 0;
    public virtual bool invisibleToMobs => false;
    /// player-only inventory hooks called from mob interactions (no-ops here)
    public virtual void consumeHeld(int n) { }
    public virtual void replaceHeld(ItemStack stack) { }
    public virtual void damageHeld(int n) { }
    public virtual bool give(ItemStack stack) { return false; }
    public virtual bool wearingPumpkin => false;

    // ---- effects ----------------------------------------------------------
    public void addEffect(string id, int duration, int amplifier = 0, bool ambient = false)
    {
        int i = effects.FindIndex(e => e.id == id);
        if (i >= 0)
        {
            var cur = effects[i];
            if (cur.amplifier > amplifier || (cur.amplifier == amplifier && cur.duration > duration)) { return; }
            effects[i] = new ActiveEffect(id: id, duration: duration, amplifier: amplifier, ambient: ambient);
        }
        else
        {
            effects.Add(new ActiveEffect(id: id, duration: duration, amplifier: amplifier, ambient: ambient));
        }
        var def = effectDef(id);
        if (def.instant) { applyInstantEffect(id, amplifier); }
    }
    private void applyInstantEffect(string id, int amp)
    {
        if (id == "instant_health") { heal(4 * Math.Pow(2, (double)amp)); }
        else if (id == "instant_damage") { hurt(6 * Math.Pow(2, (double)amp), "magic"); }
        else if (id == "saturation") { feed(amp + 1, 2); }
    }
    /// Player hook (baseline `(this as any).feed?.()`)
    public virtual void feed(int hunger, double saturation) { }
    /// Player hook (baseline `(this as any).addExhaustion?.()`)
    public virtual void addExhaustion(double amount) { }

    public bool hasEffect(string id) => effects.Any(e => e.id == id);
    public int effectLevel(string id)
    {
        int idx = effects.FindIndex(e => e.id == id);
        if (idx < 0) { return 0; }
        return effects[idx].amplifier + 1;
    }
    public void removeEffect(string id) { effects.RemoveAll(e => e.id == id); }
    public void clearEffects() { effects.Clear(); }

    internal void tickEffects()
    {
        foreach (var snapshot in effects.ToArray())
        {
            // re-locate: a prior iteration may have removed entries
            int i = effects.FindIndex(e => e.id == snapshot.id);
            if (i < 0) { continue; }
            if (effects[i].duration > 0) { var ae = effects[i]; ae.duration -= 1; effects[i] = ae; }
            if (effects[i].duration == 0) { effects.RemoveAt(i); continue; }
            var e = effects[i];
            switch (e.id)
            {
                case "regeneration":
                    if (age % Math.Max(1, 50 >> e.amplifier) == 0) { heal(1); }
                    break;
                case "poison":
                    if (age % Math.Max(1, 25 >> e.amplifier) == 0 && health > 1) { hurt(1, "magic"); }
                    break;
                case "wither":
                    if (age % Math.Max(1, 40 >> e.amplifier) == 0) { hurt(1, "wither"); }
                    break;
                case "hunger":
                    addExhaustion(0.005 * (double)(e.amplifier + 1));
                    break;
                case "levitation":
                    vy += (0.05 * (double)(e.amplifier + 1) - vy) * 0.2;
                    fallDistance = 0;
                    break;
                case "slow_falling":
                    if (vy < 0) { vy *= 0.6; }
                    fallDistance = 0;
                    break;
                default: break;
            }
        }
    }

    // ---- health -----------------------------------------------------------
    public void heal(double amount)
    {
        if (dead) { return; }
        health = Math.Min(maxHealth, health + amount);
    }

    public double armorValue()
    {
        double v = 0.0;
        foreach (var a in armor)
        {
            if (a != null) { v += (double)(itemDef(a.id).armor?.defense ?? 0); }
        }
        return v;
    }
    public double armorToughness()
    {
        double v = 0.0;
        foreach (var a in armor)
        {
            if (a != null) { v += itemDef(a.id).armor?.toughness ?? 0; }
        }
        return v;
    }
    public double protectionLevel(string source)
    {
        int epf = 0;
        foreach (var a in armor)
        {
            if (a == null) { continue; }
            epf += enchLevel(a, "protection");
            if (source == "fire" || source == "lava") { epf += enchLevel(a, "fire_protection") * 2; }
            if (source == "explosion") { epf += enchLevel(a, "blast_protection") * 2; }
            if (source == "projectile") { epf += enchLevel(a, "projectile_protection") * 2; }
            if (source == "fall") { epf += enchLevel(a, "feather_falling") * 3; }
        }
        return (double)Math.Min(20, epf);
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (dead || amount <= 0) { return false; }
        if (invulnTicks > 0) { return false; }
        if (source == "fire" && hasEffect("fire_resistance")) { return false; }
        if (source == "lava" && hasEffect("fire_resistance")) { return false; }
        if (source == "fall" && (type == "cat" || type == "ocelot" || type == "chicken" || type == "bat" || type == "parrot" || type == "bee" || type == "shulker" || type == "iron_golem")) { return false; }

        // armor reduction (vanilla formula)
        bool bypassesArmor = source == "fall" || source == "void" || source == "magic" || source == "wither" || source == "starve" || source == "drown" || source == "freeze" || source == "sonic";
        double dmg = amount;
        if (!bypassesArmor)
        {
            double armorV = armorValue();
            double tough = armorToughness();
            dmg = dmg * (1 - Math.Min(20, Math.Max(armorV / 5, armorV - 4 * dmg / (tough + 8))) / 25);
            damageArmor(amount);
        }
        // resistance effect
        int res = effectLevel("resistance");
        if (res > 0) { dmg *= Math.Max(0, 1 - (double)res * 0.2); }
        // protection enchants
        double epf = protectionLevel(source);
        dmg *= 1 - epf / 25;

        // absorption
        if (absorption > 0)
        {
            double absorbed = Math.Min(absorption, dmg);
            absorption -= absorbed;
            dmg -= absorbed;
        }
        health -= dmg;
        hurtTime = 10;
        invulnTicks = 10;
        if (attacker != null)
        {
            lastAttacker = attacker;
            if (attacker.isPlayer) { lastHurtByPlayerTime = 100; }
            // knockback
            double dx = x - attacker.x, dz = z - attacker.z;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d == 0) { d = 1; }
            double kb = 0.4 * (1 - kbResist);
            vx += dx / d * kb;
            vz += dz / d * kb;
            vy = Math.Min(vy + 0.36 * (1 - kbResist), 0.4);
        }
        world.hooks.playSound(hurtSound(), x, y, z, 1, 0.9 + System.Random.Shared.NextDouble() * 0.2);
        if (health <= 0) { die(source, attacker); }
        return true;
    }

    internal void damageArmor(double amount)
    {
        int dmg = Math.Max(1, (int)Math.Floor(amount / 4));
        for (int i = 0; i < 4; i++)
        {
            var a = armor[i];
            if (a == null) { continue; }
            int unb = enchLevel(a, "unbreaking");
            if (unb > 0 && rng.nextFloat() < (double)unb / (double)(unb + 1) * 0.6) { continue; }
            a.damage += dmg;
            int maxD = itemDef(a.id).armor?.durability ?? 100;
            if (a.damage >= maxD)
            {
                armor[i] = null;
                world.hooks.playSound("entity.item.break", x, y, z, 1, 1);
            }
            else
            {
                armor[i] = a;
            }
        }
    }

    public virtual void die(string source, Entity attacker = null)
    {
        health = 0;
        deathTime = 1;
        world.hooks.playSound(deathSound(), x, y, z, 1, 1);
        if (world.rule("doMobLoot"))
        {
            int looting = (attacker is LivingEntity le && le.mainHand != null) ? enchLevel(le.mainHand, "looting") : 0;
            dropLoot(looting, lastHurtByPlayerTime > 0);
        }
    }

    public override void onLand(double fallDistance)
    {
        if (!world.rule("fallDamage")) { return; }
        if (hasEffect("slow_falling")) { return; }
        int ground = groundBlock() >> 4;
        double dmg = Math.Ceiling(fallDistance - 3 - (double)effectLevel("jump_boost"));
        if (ground == (int)B.hay_block) { dmg = Math.Ceiling(dmg * 0.2); }
        if (ground == (int)B.slime_block || ground == (int)B.honey_block) { dmg = 0; }
        if (ground == (int)B.pointed_dripstone) { dmg = Math.Ceiling(fallDistance * 2 - 2); }
        if (ground >= 0 && ground < blockDefs.Count && blockDefs[ground].shape == Shape.bed) { dmg = Math.Ceiling(dmg * 0.5); }
        if (dmg > 0)
        {
            hurt(dmg, fallDistance > 5 ? "fall_high" : "fall");
            int cell = groundBlock();
            world.hooks.playSound(dmg > 4 ? "entity.generic.big_fall" : "entity.generic.small_fall", x, y, z, 1, 1);
            // farmland trample
            if ((cell >> 4) == (int)B.farmland && world.rule("mobGriefing"))
            {
                world.setBlock(ifloor(x), ifloor(y - 0.35), ifloor(z), (int)B.dirt << 4);
            }
            // turtle egg trample
            if ((cell >> 4) == (int)B.turtle_egg && rng.chance(0.7))
            {
                world.breakBlockNaturally(ifloor(x), ifloor(y - 0.35), ifloor(z));
            }
        }
    }

    /// loot drops — override per mob
    public virtual void dropLoot(int looting, bool byPlayer)
    {
        foreach (var d in drops())
        {
            double chance = d.chance ?? 1;
            if (rng.nextFloat() > chance + (double)looting * (d.lootingBonus ?? 0.01)) { continue; }
            int mn = d.min ?? 1;
            int mx = (d.max ?? mn) + (d.lootingBonus != null ? rng.nextInt(looting + 1) : 0);
            int count = mn + rng.nextInt(Math.Max(1, mx - mn + 1));
            if (count > 0 && itemExists(d.item)) { dropStack(new ItemStack(iid(d.item), count)); }
        }
    }
    public virtual List<DropEntry> drops() { return new List<DropEntry>(); }

    public void dropStack(ItemStack stack)
    {
        spawnItemFn?.Invoke(world, x, y + height / 2, z, stack,
                            (rng.nextFloat() - 0.5) * 0.1, 0.2, (rng.nextFloat() - 0.5) * 0.1);
    }

    public virtual string hurtSound() { return $"entity.{type}.hurt"; }
    public virtual string deathSound() { return $"entity.{type}.death"; }
    public virtual string ambientSound() { return $"entity.{type}.ambient"; }

    // ---- movement -----------------------------------------------------------
    public double effectiveSpeed()
    {
        double s = speed;
        s *= 1 + 0.2 * (double)effectLevel("speed");
        s *= Math.Max(0.1, 1 - 0.15 * (double)effectLevel("slowness"));
        if (sprinting) { s *= 1.3; }
        if (sneaking) { s *= 0.3; }
        // soul speed / soul sand
        int ground = groundBlock() >> 4;
        if (ground == (int)B.soul_sand)
        {
            int soulSpeed = armor[3] != null ? enchLevel(armor[3], "soul_speed") : 0;
            s *= soulSpeed > 0 ? 1 + 0.3 * (double)soulSpeed : 0.4;
        }
        if (sneaking)
        {
            var legs = armor[2];
            if (legs != null)
            {
                int swift = enchLevel(legs, "swift_sneak");
                if (swift > 0) { s *= 1 + 0.5 * (double)swift; }
            }
        }
        return s;
    }

    /// vanilla-style travel physics; call each tick with intent set
    public virtual void travel()
    {
        bool inWater = this.inWater, inLava = this.inLava;
        int slipperinessBlock = world.getBlock(ifloor(x), ifloor(y - 0.5), ifloor(z)) >> 4;
        double slip = onGround
            ? (slipperinessBlock == (int)B.ice || slipperinessBlock == (int)B.packed_ice || slipperinessBlock == (int)B.frosted_ice ? 0.98
                : slipperinessBlock == (int)B.blue_ice ? 0.989
                : slipperinessBlock == (int)B.slime_block ? 0.8 : 0.6)
            : 0.91;

        if (inWater)
        {
            int depthStrider = armor[3] != null ? enchLevel(armor[3], "depth_strider") : 0;
            double waterSpeed = 0.02 + (double)depthStrider * 0.01 * (onGround ? 1 : 0.5);
            if (hasEffect("dolphins_grace")) { waterSpeed *= 3; }
            applyInput(waterSpeed);
            move(vx, vy, vz);
            vx *= 0.8; vy *= 0.8; vz *= 0.8;
            if (!noGravity) { vy -= 0.02 * gravityScale; }
            if (jumping) { vy = Math.Min(vy + 0.04, 0.2); }
            if (horizontalCollision && isClimbing()) { vy = 0.2; }
        }
        else if (inLava)
        {
            applyInput(0.02);
            move(vx, vy, vz);
            vx *= 0.5; vy *= 0.5; vz *= 0.5;
            if (!noGravity) { vy -= 0.02; }
            if (jumping) { vy = Math.Min(vy + 0.04, 0.2); }
        }
        else
        {
            double accel = onGround ? effectiveSpeed() * (0.21600002 / (slip * slip * slip)) : 0.02 + (sprinting ? 0.006 : 0);
            applyInput(accel);
            if (isClimbing())
            {
                vx = clampD(vx, -0.15, 0.15);
                vz = clampD(vz, -0.15, 0.15);
                if (vy < -0.15) { vy = -0.15; }
                if (sneaking && vy < 0) { vy = 0; }
                if (horizontalCollision || jumping) { vy = 0.2; }
            }
            move(vx, vy, vz);
            if (!noGravity)
            {
                double g = 0.08 * gravityScale;
                if (hasEffect("slow_falling") && vy <= 0) { g = 0.01; }
                vy -= g;
            }
            vy *= 0.98;
            vx *= slip * 0.91;
            vz *= slip * 0.91;
            // jump
            if (jumping && onGround)
            {
                vy = jumpPower + (double)effectLevel("jump_boost") * 0.1;
                if (sprinting)
                {
                    vx += -detSin(yaw) * 0.2;
                    vz += detCos(yaw) * 0.2;
                }
                onGround = false;
            }
        }
        // limb animation
        double dxm = x - prevX, dzm = z - prevZ;
        double moved = Math.Min(1, Math.Sqrt(dxm * dxm + dzm * dzm) * 4);
        limbAmp += (moved - limbAmp) * 0.4;
        limbSwing += limbAmp * 1.2;
    }

    internal void applyInput(double accel)
    {
        double f = moveForward, s = moveStrafe;
        double len = Math.Sqrt(f * f + s * s);
        if (len < 0.01) { return; }
        if (len > 1) { f /= len; s /= len; }
        double sn = detSin(yaw), cs = detCos(yaw);
        vx += (s * cs - f * sn) * accel;
        vz += (f * cs + s * sn) * accel;
    }

    public virtual void tickDeath()
    {
        deathTime += 1;
        if (deathTime >= 20)
        {
            // XP burst
            if (lastHurtByPlayerTime > 0 && spawnXPFn != null)
            {
                spawnXPFn(world, x, y + height / 2, z, xpReward);
            }
            world.hooks.addParticles("explosion", x, y + height / 2, z, 6, width, 0);
            remove();
        }
    }

    public void baseLivingTick()
    {
        baseTick();
        if (hurtTime > 0) { hurtTime -= 1; }
        if (lastHurtByPlayerTime > 0) { lastHurtByPlayerTime -= 1; }
        if (attackAnim > 0) { attackAnim = Math.Max(0, attackAnim - 0.125); }
        tickEffects();
        if (deathTime > 0) { tickDeath(); return; }
        // drowning
        if (underwater && !breathesWater && !hasEffect("water_breathing") && !hasEffect("conduit_power"))
        {
            int respiration = armor[0] != null ? enchLevel(armor[0], "respiration") : 0;
            if (respiration == 0 || rng.nextFloat() < 1 / (double)(respiration + 1)) { airSupply -= 1; }
            if (airSupply <= -20)
            {
                airSupply = 0;
                if (world.rule("drowningDamage")) { hurt(2, "drown"); }
            }
        }
        else if (breathesWaterOnly && !inWater)
        {
            airSupply -= 1;
            if (airSupply <= -20) { airSupply = 0; hurt(2, "drown"); }
        }
        else
        {
            airSupply = Math.Min(300, airSupply + 4);
        }
        // body yaw follows movement
        double dxm = x - prevX, dzm = z - prevZ;
        if (dxm * dxm + dzm * dzm > 0.0025)
        {
            double moveYaw = detAtan2(-dxm, dzm);
            double d = moveYaw - bodyYaw;
            while (d > Math.PI) { d -= Math.PI * 2; }
            while (d < -Math.PI) { d += Math.PI * 2; }
            bodyYaw += d * 0.3;
        }
        else
        {
            double d = yaw - bodyYaw;
            while (d > Math.PI) { d -= Math.PI * 2; }
            while (d < -Math.PI) { d += Math.PI * 2; }
            if (Math.Abs(d) > 0.9) { bodyYaw += d * 0.2; }
        }
        headYaw = yaw;
        pushEntities();
    }

    /// vanilla Entity.push — overlapping living entities shove each other apart
    /// (the missing piece that let the player walk straight through mobs)
    internal void pushEntities()
    {
        if (dead || vehicle != null) { return; }
        if (this is Player p && p.flying) { return; }
        foreach (var e in world.entities)
        {
            if (!(e is LivingEntity other) || ReferenceEquals(other, this) || other.dead
                || other.deathTime != 0 || ReferenceEquals(other.vehicle, this)) { continue; }
            double rx = (width + other.width) * 0.5;
            if (Math.Abs(other.x - x) >= rx || Math.Abs(other.z - z) >= rx) { continue; }
            if (other.y >= y + height || y >= other.y + other.height) { continue; }
            double dx = other.x - x;
            double dz = other.z - z;
            double d = Math.Max(Math.Abs(dx), Math.Abs(dz));
            if (d >= 0.01)
            {
                d = Math.Sqrt(d);
                dx /= d;
                dz /= d;
                double scale = Math.Min(1.0, 1.0 / d) * 0.05;
                other.vx += dx * scale;
                other.vz += dz * scale;
                vx -= dx * scale;
                vz -= dz * scale;
            }
        }
    }
}

// late-bound spawners to avoid import cycles (set by Misc.swift registration)
public static class LivingGlobals
{
    public static Action<World, double, double, double, ItemStack, double, double, double> spawnItemFn;
    public static Action<World, double, double, double, int> spawnXPFn;

    public static void bindSpawners(
        Action<World, double, double, double, ItemStack, double, double, double> item,
        Action<World, double, double, double, int> xp)
    {
        spawnItemFn = item;
        spawnXPFn = xp;
    }
}

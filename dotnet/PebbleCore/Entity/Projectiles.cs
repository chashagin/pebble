// Projectiles — arrows, snowballs, eggs, ender
// pearls, XP bottles, potions, fireballs, wither skulls, shulker bullets,
// tridents, fishing bobbers, fireworks, llama spit, dragon fireballs.
//
// Two deterministic bug-compats kept on purpose: Fireball.width and WitherSkull.drag read
// their `small`/`blue` flags during construction (always false), so width=1 /
// drag=1 regardless of the flag set afterwards.
//
// Defines static class: ProjectilesGlobals (free funcs/vars: spawnLightningFn,
//   bindSpawnLightning, and the private rayBox helper). Surface via
//   `global using static PebbleCore.ProjectilesGlobals`.

namespace PebbleCore;

public class Projectile : Entity
{
    public Entity owner;
    public double gravity = 0.03;
    public double drag = 0.99;
    public bool stuck = false;

    public Projectile(World world) : base(world)
    {
        width = 0.25;
        height = 0.25;
    }

    public void shoot(double dx, double dy, double dz, double power, double inaccuracy)
    {
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len == 0) { len = 1; }
        double r() { return (gameRng.nextFloat() - 0.5) * 0.0075 * inaccuracy; }
        vx = (dx / len + r()) * power;
        vy = (dy / len + r()) * power;
        vz = (dz / len + r()) * power;
        yaw = detAtan2(-vx, vz);
        pitch = -detAtan2(vy, Math.Sqrt(vx * vx + vz * vz));
    }

    public void shootFrom(Entity shooter, double pitchRad, double yawRad, double power, double inaccuracy)
    {
        double dx = -detSin(yawRad) * detCos(pitchRad);
        double dy = -detSin(pitchRad);
        double dz = detCos(yawRad) * detCos(pitchRad);
        double ey = shooter.eyeY() - 0.1;
        setPos(shooter.x, ey, shooter.z);
        shoot(dx, dy, dz, power, inaccuracy);
        owner = shooter;
    }

    public override void tick()
    {
        baseTick();
        if (stuck)
        {
            onStuckTick();
            return;
        }
        // move with raycast collision
        double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (speed > 0.001)
        {
            var hit = world.raycast(x, y, z, vx / speed, vy / speed, vz / speed, speed);
            // entity hit check along path — eHit.t is a FRACTION of this
            // tick's travel, hit.t is in BLOCKS; scale before comparing or
            // entities get hit through walls
            var eHit = findEntityHit(speed);
            if (eHit != null && (hit == null || eHit.Value.t * speed <= hit.Value.t))
            {
                onHitEntity(eHit.Value.entity);
            }
            else if (hit != null)
            {
                x = hit.Value.px; y = hit.Value.py; z = hit.Value.pz;
                onHitBlock(hit.Value);
            }
        }
        if (dead) { return; }
        x += vx; y += vy; z += vz;
        vy -= gravity;
        double dragF = inWater ? 0.8 : drag;
        vx *= dragF; vy *= dragF; vz *= dragF;
        yaw = detAtan2(-vx, vz);
        pitch = -detAtan2(vy, Math.Sqrt(vx * vx + vz * vz));
        if (age > 1200) { remove(); }
    }

    internal (Entity entity, double t)? findEntityHit(double speed)
    {
        (Entity entity, double t)? best = null;
        foreach (var e in world.getEntitiesNear(x, y, z, speed + 2))
        {
            var ent = e as Entity;
            if (ent == null) { continue; }
            if (ent == this || (ent == owner && age < 5) || ent.dead) { continue; }
            if (!(ent is LivingEntity) && ent.type != "end_crystal") { continue; }
            var bb = ent.bb();
            // ray vs box
            var t = ProjectilesGlobals.rayBox(x, y, z, vx, vy, vz,
                           bb.x0 - 0.1, bb.y0 - 0.1, bb.z0 - 0.1,
                           bb.x1 + 0.1, bb.y1 + 0.1, bb.z1 + 0.1);
            if (t != null && t <= 1 && (best == null || t < best.Value.t)) { best = (ent, t.Value); }
        }
        return best;
    }

    public virtual void onStuckTick() { remove(); }
    public virtual void onHitEntity(Entity e) { }
    public virtual void onHitBlock(RaycastHit hit) { }
}

public sealed class ArrowEntity : Projectile
{
    public override string type => "arrow";
    public double damage = 2.0;
    public bool critical = false;
    public bool pickupable = true;
    public int punchLevel = 0;
    public bool flame = false;
    public string potionId = null; // tipped
    public bool spectral = false;
    public int stuckTime = 0;
    public bool fromCrossbow = false;
    public int piercingLeft = 0;

    public ArrowEntity(World world) : base(world)
    {
        gravity = 0.05;
    }

    public override void onHitEntity(Entity e)
    {
        double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        double dmg = Math.Ceiling(speed * damage);
        if (critical) { dmg += Math.Floor(gameRng.nextFloat() * (dmg / 2 + 1)); }
        if (flame) { e.fireTicks = Math.Max(e.fireTicks, 100); }
        bool hurt = e.hurt(dmg, "projectile", owner);
        if (hurt)
        {
            if (punchLevel > 0 && e is LivingEntity liv)
            {
                double d = Math.Sqrt(vx * vx + vz * vz);
                if (d == 0) { d = 1; }
                liv.vx += vx / d * (double)punchLevel * 0.4;
                liv.vz += vz / d * (double)punchLevel * 0.4;
            }
            if (potionId != null && e is LivingEntity liv2)
            {
                var pot = potionDef(potionId);
                foreach (var ef in pot.effects) { liv2.addEffect(ef.effect, ef.duration / 8, ef.amplifier); }
            }
            if (spectral && e is LivingEntity liv3) { liv3.addEffect("glowing", 200, 0); }
            world.hooks.playSound("entity.arrow.hit_player", e.x, e.y, e.z, 0.6, 1.2);
        }
        if (piercingLeft > 0)
        {
            piercingLeft -= 1;
        }
        else
        {
            remove();
        }
    }
    public override void onHitBlock(RaycastHit hit)
    {
        stuck = true;
        vx = 0; vy = 0; vz = 0;
        world.hooks.playSound("entity.arrow.hit", x, y, z, 0.7, 1.1);
        // target block signal
        if ((hit.cell >> 4) == (int)B.target)
        {
            double cx = (double)hit.x + 0.5, cy = (double)hit.y + 0.5, cz = (double)hit.z + 0.5;
            double ddx = x - cx, ddy = y - cy, ddz = z - cz;
            double dist = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
            int power = Math.Max(1, Math.Min(15, (int)detRound((1 - dist) * 15 + 8)));
            world.setBlock(hit.x, hit.y, hit.z, (int)cell(B.target, Math.Min(15, power)));
            world.scheduleTick(hit.x, hit.y, hit.z, (int)B.target, 20);
        }
    }
    public override void onStuckTick()
    {
        stuckTime += 1;
        // pickup
        if (pickupable && stuckTime > 10)
        {
            foreach (var e in world.getEntitiesNear(x, y, z, 1.2, filter: o => (o as Entity)?.isPlayer ?? false))
            {
                var p = e as LivingEntity;
                if (p == null) { continue; }
                var s = new ItemStack(iid(potionId != null ? "tipped_arrow" : spectral ? "spectral_arrow" : "arrow"), 1);
                if (potionId != null) { s.data.potion = potionId; }
                if (p.give(s))
                {
                    world.hooks.playSound("entity.item.pickup", x, y, z, 0.3, 1.6);
                    remove();
                    return;
                }
            }
        }
        if (stuckTime > 1200) { remove(); }
    }
}

public sealed class ThrownSnowball : Projectile
{
    public override string type => "snowball";
    public ThrownSnowball(World world) : base(world) { }
    public override void onHitEntity(Entity e)
    {
        double dmg = e.type == "blaze" ? 3 : 0;
        e.hurt(dmg, "projectile", owner);
        world.hooks.addParticles("snow", x, y, z, 6, 0.2, 0);
        remove();
    }
    public override void onHitBlock(RaycastHit hit)
    {
        world.hooks.addParticles("snow", x, y, z, 6, 0.2, 0);
        remove();
    }
}

public sealed class ThrownEgg : Projectile
{
    public override string type => "egg";
    public ThrownEgg(World world) : base(world) { }
    public override void onHitEntity(Entity e)
    {
        e.hurt(0, "projectile", owner);
        hatch();
    }
    public override void onHitBlock(RaycastHit hit) { hatch(); }
    private void hatch()
    {
        if (gameRng.nextFloat() < 0.125)
        {
            int count = gameRng.nextFloat() < 0.0312 ? 4 : 1;
            for (int _i = 0; _i < count; _i++)
            {
                spawnMobFn?.Invoke(world, "chicken", x, y, z, new SpawnOpts(baby: true));
            }
        }
        world.hooks.addParticles("block", x, y, z, 6, 0.2, (int)cell(B.bone_block));
        remove();
    }
}

public sealed class ThrownPearl : Projectile
{
    public override string type => "ender_pearl";
    public ThrownPearl(World world) : base(world) { }
    public override void onHitEntity(Entity e) { teleport(); }
    public override void onHitBlock(RaycastHit hit) { teleport(); }
    private void teleport()
    {
        if (owner != null && !owner.dead)
        {
            owner.x = x;
            owner.y = y + 0.1;
            owner.z = z;
            owner.hurt(5, "fall");
            world.hooks.playSound("entity.enderman.teleport", x, y, z, 1, 1);
            world.hooks.addParticles("portal", x, y + 1, z, 24, 0.5, 0);
        }
        remove();
    }
}

public sealed class ThrownXPBottle : Projectile
{
    public override string type => "xp_bottle";
    public ThrownXPBottle(World world) : base(world) { }
    public override void onHitEntity(Entity e) { smash(); }
    public override void onHitBlock(RaycastHit hit) { smash(); }
    private void smash()
    {
        spawnXP(world, x, y, z, 3 + gameRng.nextInt(9));
        world.hooks.playSound("block.glass.break", x, y, z, 1, 1);
        world.hooks.addParticles("enchant", x, y, z, 16, 0.4, 0);
        remove();
    }
}

public sealed class ThrownPotion : Projectile
{
    public override string type => "thrown_potion";
    public string potionId = "water";
    public bool lingering = false;
    public ThrownPotion(World world) : base(world) { }
    public override void onHitEntity(Entity e) { smash(); }
    public override void onHitBlock(RaycastHit hit) { smash(); }
    private void smash()
    {
        var pot = potionDef(potionId);
        world.hooks.playSound("block.glass.break", x, y, z, 1, 1);
        if (lingering)
        {
            var cloud = new AreaEffectCloud(world);
            cloud.setPos(x, y, z);
            if (pot.effects.Count > 0)
            {
                var first = pot.effects[0];
                cloud.effectId = first.effect;
                cloud.amplifier = first.amplifier;
            }
            cloud.particleType = "portal";
            cloud.duration = 600;
            world.addEntity(cloud);
        }
        else
        {
            foreach (var e in world.getEntitiesNear(x, y, z, 4))
            {
                var liv = e as LivingEntity;
                if (liv == null || liv.dead) { continue; }
                double ddx = e.x - x, ddy = e.y - y, ddz = e.z - z;
                double dist = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                double f = Math.Max(0, 1 - dist / 4);
                foreach (var ef in pot.effects)
                {
                    liv.addEffect(ef.effect, (int)Math.Floor((double)ef.duration * 0.75 * f), ef.amplifier);
                }
                // water extinguishes
                if (potionId == "water") { liv.fireTicks = 0; }
            }
        }
        world.hooks.addParticles("splash", x, y, z, 20, 0.6, 0);
        remove();
    }
}

public sealed class Fireball : Projectile
{
    public override string type => "fireball";
    public double power = 1.0;
    public bool small = false;
    public Fireball(World world) : base(world)
    {
        gravity = 0;
        drag = 1;
        width = 1;   // baseline evaluates `small ? 0.3 : 1` at construction (small=false)
    }
    public override void tick()
    {
        base.tick();
        if (!dead && age % 2 == 0)
        {
            world.hooks.addParticles(small ? "flame" : "smoke", x, y, z, 1, 0.1, 0);
        }
    }
    public override void onHitEntity(Entity e)
    {
        e.hurt(small ? 5 : 6, "fireball", owner);
        e.fireTicks = Math.Max(e.fireTicks, 100);
        explode();
    }
    public override void onHitBlock(RaycastHit hit) { explode(); }
    private void explode()
    {
        if (small)
        {
            // small fireball: just fire
            int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
            if (world.rule("mobGriefing") && (world.getBlock(bx, by + 1, bz) >> 4) == 0)
            {
                world.setBlock(bx, by + 1, bz, (int)cell(B.fire));
            }
        }
        else
        {
            explodeFn?.Invoke(world, x, y, z, power, world.rule("mobGriefing"), this);
        }
        remove();
    }
    /// ghast fireballs can be deflected
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (attacker != null)
        {
            double dx = x - attacker.x, dy = y - attacker.eyeY(), dz = z - attacker.z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d == 0) { d = 1; }
            vx = dx / d * 1.2; vy = dy / d * 1.2; vz = dz / d * 1.2;
            owner = attacker;
            return true;
        }
        return false;
    }
}

public sealed class WitherSkull : Projectile
{
    public override string type => "wither_skull";
    public bool blue = false;
    public WitherSkull(World world) : base(world)
    {
        gravity = 0;
        drag = 1;    // baseline evaluates `blue ? 0.73 : 1` at construction (blue=false)
    }
    public override void onHitEntity(Entity e)
    {
        e.hurt(8, "wither_skull", owner);
        (e as LivingEntity)?.addEffect("wither", 200, 1);
        explode();
    }
    public override void onHitBlock(RaycastHit hit) { explode(); }
    private void explode()
    {
        explodeFn?.Invoke(world, x, y, z, 1, false, this);
        remove();
    }
}

public sealed class DragonFireball : Projectile
{
    public override string type => "dragon_fireball";
    public DragonFireball(World world) : base(world)
    {
        gravity = 0;
        drag = 1;
    }
    public override void onHitEntity(Entity e) { breath(); }
    public override void onHitBlock(RaycastHit hit) { breath(); }
    private void breath()
    {
        var cloud = new AreaEffectCloud(world);
        cloud.setPos(x, y, z);
        cloud.effectId = "instant_damage";
        cloud.amplifier = 0;
        cloud.radius = 3;
        cloud.duration = 120;
        cloud.particleType = "dragon_breath";
        world.addEntity(cloud);
        world.hooks.playSound("entity.generic.explode", x, y, z, 1, 0.8);
        remove();
    }
}

public sealed class ShulkerBullet : Projectile
{
    public override string type => "shulker_bullet";
    public int? targetId = null;
    public ShulkerBullet(World world) : base(world)
    {
        gravity = 0;
        drag = 1;
    }
    public override void tick()
    {
        // homing
        if (targetId != null && world.entityById.TryGetValue(targetId.Value, out var t) && !t.dead)
        {
            double dx = t.x - x, dy = t.y + 1 - y, dz = t.z - z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d == 0) { d = 1; }
            vx += (dx / d * 0.18 - vx) * 0.18;
            vy += (dy / d * 0.18 - vy) * 0.18;
            vz += (dz / d * 0.18 - vz) * 0.18;
        }
        base.tick();
        if (!dead && age % 2 == 0) { world.hooks.addParticles("portal", x, y, z, 1, 0.1, 0); }
    }
    public override void onHitEntity(Entity e)
    {
        e.hurt(4, "projectile", owner);
        (e as LivingEntity)?.addEffect("levitation", 200, 0);
        remove();
    }
    public override void onHitBlock(RaycastHit hit) { remove(); }
}

public sealed class TridentEntity : Projectile
{
    public override string type => "trident";
    public ItemStack stack = null;
    public int loyalty = 0;
    public bool returning = false;
    public bool dealtDamage = false;
    public TridentEntity(World world) : base(world)
    {
        gravity = 0.05;
    }
    public override void tick()
    {
        if (returning && owner != null && !owner.dead)
        {
            double dx = owner.x - x, dy = owner.eyeY() - y, dz = owner.z - z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d == 0) { d = 1; }
            vx = dx / d * (0.3 + (double)loyalty * 0.12);
            vy = dy / d * (0.3 + (double)loyalty * 0.12);
            vz = dz / d * (0.3 + (double)loyalty * 0.12);
            x += vx; y += vy; z += vz;
            age += 1;
            if (d < 1.5)
            {
                if ((owner as LivingEntity)?.give(stack) == true)
                {
                    world.hooks.playSound("entity.item.pickup", x, y, z, 0.4, 1.4);
                }
                remove();
            }
            return;
        }
        base.tick();
    }
    public override void onHitEntity(Entity e)
    {
        if (dealtDamage) { return; } // one target per throw — no piercing
        int impaling = stack != null ? enchLevel(stack, "impaling") : 0;
        double waterBonus = ((e as LivingEntity)?.breathesWater ?? false) ? 1.0 : 0.0;
        double dmg = 8 + (double)impaling * 2.5 * waterBonus;
        e.hurt(dmg, "projectile", owner);
        dealtDamage = true;
        // channeling
        if (stack != null && enchLevel(stack, "channeling") > 0 && world.thundering &&
           world.canSeeSky(ifloor(e.x), ifloor(e.y), ifloor(e.z)))
        {
            spawnLightningFn?.Invoke(world, e.x, e.y, e.z);
        }
        if (loyalty > 0)
        {
            beginReturn();
        }
        else
        {
            // deflect and drop so it lands near the target for pickup
            vx *= -0.01; vy *= -0.1; vz *= -0.01;
        }
    }
    public override void onHitBlock(RaycastHit hit)
    {
        stuck = true;
        vx = 0; vy = 0; vz = 0;
        world.hooks.playSound("item.trident.hit_ground", x, y, z, 1, 1);
        if (loyalty > 0) { beginReturn(); }
    }
    private void beginReturn()
    {
        if (loyalty > 0)
        {
            returning = true;
            stuck = false;
        }
    }
    public override void onStuckTick()
    {
        // pickup
        foreach (var e in world.getEntitiesNear(x, y, z, 1.4, filter: o => (o as Entity)?.isPlayer ?? false))
        {
            if ((e as LivingEntity)?.give(stack) == true)
            {
                world.hooks.playSound("entity.item.pickup", x, y, z, 0.4, 1.2);
                remove();
                return;
            }
        }
        if (age > 2400) { remove(); }
    }
}

public sealed class FireworkEntity : Entity
{
    public override string type => "firework";
    public int life = 0;
    public int lifeTotal = 30;
    public Entity attachedTo = null;
    public int flightDuration = 1;
    public FireworkEntity(World world) : base(world)
    {
        width = 0.25; height = 0.25;
        noGravity = true;
    }
    public override void tick()
    {
        baseTick();
        // roll the fuse ONCE — re-rolling drained a gameRng draw per tick,
        // desyncing the shared deterministic stream for everything else
        if (life == 0) { lifeTotal = 20 * (flightDuration + 1) + gameRng.nextInt(6); }
        if (attachedTo is Entity e && !e.dead)
        {
            // elytra boost
            double lookX = -detSin(e.yaw) * detCos(e.pitch);
            double lookY = -detSin(e.pitch);
            double lookZ = detCos(e.yaw) * detCos(e.pitch);
            e.vx += lookX * 0.1 + (lookX * 1.5 - e.vx) * 0.5;
            e.vy += lookY * 0.1 + (lookY * 1.5 - e.vy) * 0.5;
            e.vz += lookZ * 0.1 + (lookZ * 1.5 - e.vz) * 0.5;
            setPos(e.x, e.y, e.z);
        }
        else
        {
            vy += 0.04;
            move(vx, vy, vz);
        }
        if (age % 2 == 0) { world.hooks.addParticles("flame", x, y - 0.3, z, 1, 0.05, 0); }
        life += 1;
        if (life > lifeTotal)
        {
            world.hooks.playSound("entity.firework_rocket.blast", x, y, z, 2, 1);
            world.hooks.addParticles("totem", x, y, z, 30, 0.5, 0);
            // damage if boosted
            attachedTo?.hurt(0, "firework");
            remove();
        }
    }
}

public sealed class FishingBobber : Entity
{
    public override string type => "fishing_bobber";
    public LivingEntity ownerPlayer = null;
    public int biteTime = 0;
    public int nibbling = 0;
    public Entity hookedEntity = null;
    public FishingBobber(World world) : base(world)
    {
        width = 0.25; height = 0.25;
    }
    public override void tick()
    {
        baseTick();
        var op = ownerPlayer;
        if (op == null || op.dead) { remove(); return; }
        double ddx = x - op.x, ddy = y - op.y, ddz = z - op.z;
        double d = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
        if (d > 32) { retrieve(); return; }
        if (hookedEntity is Entity hooked)
        {
            if (hooked.dead) { hookedEntity = null; return; }
            setPos(hooked.x, hooked.y + hooked.height / 2, hooked.z);
            return;
        }
        bool inWaterNow = (world.getBlock(ifloor(x), ifloor(y), ifloor(z)) >> 4) == (int)B.water;
        if (inWaterNow)
        {
            vy *= 0.4;
            vx *= 0.9; vz *= 0.9;
            double surface = (double)ifloor(y) + world.fluidHeight(ifloor(x), ifloor(y), ifloor(z));
            if (y < surface - 0.1) { vy += 0.1; }
            // bite logic: nibble phase first (bobber bobs, not catchable),
            // then the bite window where retrieve() actually catches
            if (nibbling > 0)
            {
                nibbling -= 1;
                vy -= 0.02;
                if (nibbling == 0)
                {
                    biteTime = 20;
                    vy -= 0.2;
                    world.hooks.playSound("entity.fishing_bobber.splash", x, y, z, 0.9, 0.8);
                    world.hooks.addParticles("splash", x, y + 0.2, z, 12, 0.3, 0);
                }
            }
            else if (biteTime > 0)
            {
                biteTime -= 1;
            }
            else
            {
                int lure = op.mainHand != null ? enchLevel(op.mainHand, "lure") : 0;
                if (gameRng.nextFloat() < 1 / (double)Math.Max(20, 400 - lure * 100))
                {
                    nibbling = 20 + gameRng.nextInt(20);
                    world.hooks.playSound("entity.fishing_bobber.splash", x, y, z, 0.6, 1);
                    world.hooks.addParticles("splash", x, y + 0.2, z, 8, 0.25, 0);
                }
            }
        }
        else
        {
            vy -= 0.04;
        }
        move(vx, vy, vz);
        vx *= 0.92; vy *= 0.92; vz *= 0.92;
        // hook entities
        foreach (var e in world.getEntitiesInBox(bb(), except: this, filter: e2 =>
            e2 is LivingEntity && !(e2 == this.ownerPlayer)))
        {
            hookedEntity = e as Entity;
            break;
        }
    }
    /// returns loot if a fish was caught
    public void retrieve()
    {
        if (hookedEntity is Entity e && ownerPlayer is LivingEntity op)
        {
            // yank entity toward player
            e.vx += (op.x - e.x) * 0.1;
            e.vy += (op.y - e.y) * 0.1 + 0.3;
            e.vz += (op.z - e.z) * 0.1;
        }
        else if (biteTime > 0 && ownerPlayer is LivingEntity op2)
        {
            // catch!
            int luck = op2.mainHand != null ? enchLevel(op2.mainHand, "luck_of_the_sea") : 0;
            var rng = new RandomX((uint)gameRng.nextInt(1000000000));
            double roll = rng.nextFloat();
            string table = roll < 0.85 - (double)luck * 0.02 ? "fishing_fish"
                : roll < 0.95 - (double)luck * 0.01 ? "fishing_junk" : "fishing_treasure";
            var loot = rollLoot(table, ref rng, luck: (double)luck);
            foreach (var stack in loot)
            {
                var item = spawnItem(world, x, y, z, stack);
                double dx = op2.x - x, dy = op2.y + 1 - y, dz = op2.z - z;
                double dd = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dd == 0) { dd = 1; }
                item.vx = dx / dd * 0.35;
                item.vy = dy / dd * 0.35 + Math.Sqrt(dd) * 0.04 + 0.15;
                item.vz = dz / dd * 0.35;
            }
            spawnXP(world, op2.x, op2.y, op2.z, 1 + gameRng.nextInt(6));
        }
        remove();
    }
}

public sealed class LlamaSpit : Projectile
{
    public override string type => "llama_spit";
    public LlamaSpit(World world) : base(world) { }
    public override void onHitEntity(Entity e)
    {
        e.hurt(1, "projectile", owner);
        remove();
    }
    public override void onHitBlock(RaycastHit hit) { remove(); }
}

public static class ProjectilesGlobals
{
    internal static double? rayBox(double ox, double oy, double oz,
                        double dx, double dy, double dz,
                        double x0, double y0, double z0,
                        double x1, double y1, double z1)
    {
        double tmin = 0.0, tmax = 1.0;
        (double, double, double, double)[] axes = { (ox, dx, x0, x1), (oy, dy, y0, y1), (oz, dz, z0, z1) };
        foreach (var (o, d, lo, hi) in axes)
        {
            if (Math.Abs(d) < 1e-9)
            {
                if (o < lo || o > hi) { return null; }
            }
            else
            {
                double t1 = (lo - o) / d, t2 = (hi - o) / d;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) { return null; }
            }
        }
        return tmin;
    }

    public static Action<World, double, double, double> spawnLightningFn;
    public static void bindSpawnLightning(Action<World, double, double, double> fn) { spawnLightningFn = fn; }
}

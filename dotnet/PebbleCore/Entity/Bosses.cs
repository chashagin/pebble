// Bosses — the Warden (vibration-driven, sonic
// boom, darkness), the Ender Dragon (phase machine, crystal healing, perch +
// breath), and the Wither (summon charge-up, skull volleys, armor phase,
// block breaking).
//
// Defines NO new module-global static class. Types Warden, EnderDragon,
// WitherBoss live at namespace level. All module symbols (gameRng, spawnXP,
// explodeFn, ifloor, cell, detSin, detCos, detAtan2, clampD, blockDefs, B,
// Monster, LivingEntity, Entity, World, EndCrystal, DragonFireball,
// WitherSkull, HurtByTargetGoal, NearestTargetGoal, FloatGoal, MeleeAttackGoal,
// StrollGoal, Mob, …) resolve from the rest of the assembly.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

// =============================================================================
// WARDEN
// =============================================================================
public sealed class Warden : Monster
{
    public override string type => "warden";
    public Dictionary<int, int> anger = new Dictionary<int, int>();   // entity id → anger
    public int sonicCharge = 0;
    public int sniffCooldown = 0;
    public int diggingOut = 60;
    public bool emerging = true;
    public Warden(World world) : base(world)
    {
        width = 0.9; height = 2.9;
        maxHealth = 500; health = 500;
        speed = 0.13;
        attackDamage = 30;
        kbResist = 1;
        persistent = true;
        xpReward = 5;
        goals.add(new FloatGoal(this, 0));
        goals.add(new MeleeAttackGoal(this, 2, 1.2));
        goals.add(new StrollGoal(this, 6, 0.6));
    }
    /// vibration event from the world
    public void hearVibration(double x, double y, double z, Entity srcEntity)
    {
        if (dead) { return; }
        double dx = x - this.x, dy = y - this.y, dz = z - this.z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d > 24) { return; }
        if (srcEntity is LivingEntity src)
        {
            addAnger(src, 35);
        }
        // investigate location
        if (target == null)
        {
            nav.moveTo(x, y, z, 1.1);
            world.hooks.playSound("entity.warden.listening", this.x, this.y, this.z, 2, 1);
        }
    }
    public void addAnger(Entity e, int amount)
    {
        int cur = (anger.TryGetValue(e.id, out var a0) ? a0 : 0) + amount;
        anger[e.id] = cur;
        if (cur >= 80 && e is LivingEntity living)
        {
            setTarget(living);
            world.hooks.playSound("entity.warden.angry", x, y, z, 3, 1);
        }
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        bool r = base.hurt(amount, source, attacker);
        if (r && attacker != null) { addAnger(attacker, 100); }
        return r;
    }
    public override void tick()
    {
        if (emerging)
        {
            diggingOut -= 1;
            if (age % 5 == 0)
            {
                world.hooks.addParticles("block", x, y + 0.5, z, 8, 0.8, (int)cell(B.sculk));
                world.hooks.playSound("entity.warden.dig", x, y, z, 1, 1);
            }
            if (diggingOut <= 0)
            {
                emerging = false;
                world.hooks.playSound("entity.warden.emerge", x, y, z, 3, 1);
            }
            baseLivingTick();
            return;
        }
        base.tick();
        // darkness pulse
        if (age % 120 == 0)
        {
            foreach (var p in world.getEntitiesNear(x, y, z, 20, filter: o => (o as Entity)?.isPlayer ?? false))
            {
                (p as LivingEntity)?.addEffect("darkness", 260, 0);
            }
            world.hooks.playSound("entity.warden.heartbeat", x, y, z, 3, 0.8);
        }
        // sniff for players when no target
        if (target == null)
        {
            sniffCooldown -= 1;
            if (sniffCooldown <= 0)
            {
                sniffCooldown = 100;
                var players = world.getEntitiesNear(x, y, z, 16, e =>
                {
                    if (!(e is LivingEntity p)) { return false; }
                    return p.isPlayer && !p.sneaking;
                });
                if (players.FirstOrDefault() is LivingEntity p)
                {
                    world.hooks.playSound("entity.warden.sniff", x, y, z, 2, 1);
                    addAnger(p, 20);
                    nav.moveTo(p.x, p.y, p.z, 1);
                }
            }
        }
        // sonic boom when target unreachable or far
        if (target is LivingEntity t && !t.dead)
        {
            double dSq = distanceToSq(t);
            if (dSq > 9 && dSq < 240 && age % 10 == 0 && (nav.isDone() || dSq > 60))
            {
                sonicCharge += 1;
                if (sonicCharge == 2) { world.hooks.playSound("entity.warden.sonic_charge", x, y, z, 3, 1); }
                if (sonicCharge >= 4)
                {
                    sonicCharge = 0;
                    // sonic boom: pierce armor
                    world.hooks.playSound("entity.warden.sonic_boom", x, y, z, 3, 1);
                    int steps = 12;
                    for (int i = 1; i <= steps; i++)
                    {
                        double f = (double)i / (double)steps;
                        world.hooks.addParticles("sculk_soul",
                                                 x + (t.x - x) * f, y + 1.6 + (t.eyeY() - y - 1.6) * f, z + (t.z - z) * f, 1, 0.05, 0);
                    }
                    t.hurt(10, "sonic", this);
                    t.vx += (t.x - x) * 0.15;
                    t.vy += 0.5;
                    t.vz += (t.z - z) * 0.15;
                }
            }
        }
        // calm down over time
        if (age % 20 == 0)
        {
            foreach (var kv in anger.ToArray())
            {
                int id = kv.Key, a = kv.Value;
                if (a <= 1) { anger.Remove(id); }
                else { anger[id] = a - 1; }
            }
        }
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("sculk_catalyst") }; }
}

// =============================================================================
// ENDER DRAGON
// =============================================================================
public sealed class EnderDragon : LivingEntity
{
    public override string type => "ender_dragon";
    public string phase = "circling";   // circling | strafing | approach_perch | perched | takeoff | charging | dying
    public int phaseTime = 0;
    public double pathAngle = 0.0;
    public double pathRadius = 50.0;
    public double pathHeight = 80.0;
    public int deathAnimTime = 0;
    public int breathTime = 0;
    /// set by Game: callback when dragon dies (portal activation)
    public Action<EnderDragon> onDeath = null;
    public EnderDragon(World world) : base(world)
    {
        pathAngle = gameRng.nextFloat() * Math.PI * 2;   // baseline field-init order
        width = 8; height = 3;
        maxHealth = 200; health = 200;
        noGravity = true;
        kbResist = 1;
        persistent = true;
        xpReward = 12000;
    }
    public override void tick()
    {
        prevX = x; prevY = y; prevZ = z;
        prevYaw = yaw;
        age += 1;
        phaseTime += 1;
        if (hurtTime > 0) { hurtTime -= 1; }
        if (invulnTicks > 0) { invulnTicks -= 1; }

        if (phase == "dying")
        {
            deathAnimTime += 1;
            vy = 0.08;
            y += vy;
            if (age % 3 == 0)
            {
                world.hooks.addParticles("explosion", x + (System.Random.Shared.NextDouble() - 0.5) * 8, y + System.Random.Shared.NextDouble() * 3, z + (System.Random.Shared.NextDouble() - 0.5) * 8, 2, 1, 0);
            }
            if (deathAnimTime % 10 == 0)
            {
                spawnXP(world, x, 70, z, 500);
                world.hooks.playSound("entity.generic.explode", x, y, z, 4, 0.8);
            }
            if (deathAnimTime >= 120)
            {
                spawnXP(world, 0.5, 70, 0.5, 6000);
                onDeath?.Invoke(this);
                remove();
            }
            return;
        }

        // crystal healing
        if (age % 10 == 0)
        {
            foreach (var e in world.entities)
            {
                if (!(e is EndCrystal c) || c.dead) { continue; }
                double dSq = distanceToSq(c);
                if (dSq < 32 * 32)
                {
                    heal(1);
                    c.beamTarget = ((int)x, (int)(y + 1), (int)z);
                }
                else if (c.beamTarget.HasValue)
                {
                    var bt = c.beamTarget.Value;
                    double ddx = (double)bt.Item1 - x, ddz = (double)bt.Item3 - z;
                    if (Math.Sqrt(ddx * ddx + ddz * ddz) < 12)
                    {
                        c.beamTarget = null;
                    }
                }
            }
        }

        var nearestPlayer = findPlayer();

        switch (phase)
        {
            case "circling":
                pathAngle += 0.012;
                {
                    double tx = detCos(pathAngle) * pathRadius;
                    double tz = detSin(pathAngle) * pathRadius;
                    double ty = pathHeight + detSin((double)age * 0.02) * 6;
                    flyToward(tx, ty, tz, 0.06);
                }
                if (phaseTime > 200 && gameRng.nextFloat() < 0.01)
                {
                    setPhase(gameRng.nextFloat() < 0.5 ? "strafing" : (gameRng.nextFloat() < 0.35 ? "approach_perch" : "charging"));
                }
                break;
            case "strafing":
                {
                    if (nearestPlayer == null) { setPhase("circling"); break; }
                    var p = nearestPlayer;
                    flyToward(p.x, p.y + 18, p.z, 0.07);
                    double dSq = distanceToSq(p);
                    if (dSq < 30 * 30)
                    {
                        // fireball
                        var fb = new DragonFireball(world);
                        fb.setPos(x, y - 1, z);
                        fb.owner = this;
                        fb.shoot(p.x - x, p.eyeY() - y + 1, p.z - z, 0.6, 2);
                        world.addEntity(fb);
                        world.hooks.playSound("entity.ender_dragon.shoot", x, y, z, 4, 1);
                        setPhase("circling");
                    }
                    if (phaseTime > 200) { setPhase("circling"); }
                }
                break;
            case "approach_perch":
                flyToward(0, 70, 0, 0.08);
                if (Math.Sqrt(x * x + z * z) < 8 && y < 74)
                {
                    setPhase("perched");
                    vx = 0; vy = 0; vz = 0;
                    setPosToFountain();
                    world.hooks.playSound("entity.ender_dragon.growl", x, y, z, 5, 0.8);
                }
                break;
            case "perched":
                // breath attack at near players, vulnerable to melee
                if (nearestPlayer is LivingEntity pp0 && distanceToSq(pp0) < 400 && phaseTime > 25)
                {
                    lookAt(pp0.x, pp0.y, pp0.z, 0.2, 0.2);
                    breathTime += 1;
                    if (breathTime > 10 && breathTime % 4 == 0)
                    {
                        // breath cloud toward player
                        double dx = pp0.x - x, dz = pp0.z - z;
                        double d = Math.Sqrt(dx * dx + dz * dz);
                        if (d == 0) { d = 1; }
                        double px = x + dx / d * 6;
                        double pz = z + dz / d * 6;
                        world.hooks.addParticles("dragon_breath", px, 65, pz, 6, 1.2, 0);
                        foreach (var pp in world.getEntitiesNear(px, 64, pz, 3, filter: o => (o as Entity)?.isPlayer ?? false))
                        {
                            (pp as Entity)?.hurt(3, "magic", this);
                        }
                    }
                }
                if (phaseTime > 220 || (hurtTime > 0 && phaseTime > 60))
                {
                    setPhase("takeoff");
                }
                break;
            case "takeoff":
                flyToward(detCos(pathAngle) * 50, 90, detSin(pathAngle) * 50, 0.08);
                if (phaseTime > 60) { setPhase("circling"); }
                break;
            case "charging":
                {
                    if (nearestPlayer == null) { setPhase("circling"); break; }
                    var p = nearestPlayer;
                    flyToward(p.x, p.y, p.z, 0.12);
                    // damage players hit
                    foreach (var pp in world.getEntitiesNear(x, y, z, 5, filter: o => (o as Entity)?.isPlayer ?? false))
                    {
                        if (!(pp is Entity pe)) { continue; }
                        pe.hurt(10, "mob", this);
                        pe.vx += vx * 2;
                        pe.vy += 0.6;
                        pe.vz += vz * 2;
                    }
                    if (phaseTime > 100 || distanceToSq(p) < 9) { setPhase("circling"); }
                }
                break;
            default:
                break;
        }

        x += vx; y += vy; z += vz;
        if (phase != "perched")
        {
            yaw = detAtan2(-vx, vz);
        }
        // destroy blocks the dragon passes through (except end-grade blocks)
        if (world.rule("mobGriefing") && age % 4 == 0 && phase != "perched")
        {
            int bx = ifloor(x), by = ifloor(y + 1), bz = ifloor(z);
            for (int dy = -1; dy <= 2; dy++)
            {
                for (int dz = -3; dz <= 3; dz++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int c = world.getBlock(bx + dx, by + dy, bz + dz);
                        int bid = c >> 4;
                        if (bid != 0 && bid != (int)B.obsidian && bid != (int)B.end_stone && bid != (int)B.bedrock && bid != (int)B.iron_bars && bid != (int)B.end_portal && bid != (int)B.end_portal_frame && blockDefs[bid].hardness >= 0)
                        {
                            world.setBlock(bx + dx, by + dy, bz + dz, 0);
                        }
                    }
                }
            }
        }
    }
    private void setPosToFountain()
    {
        x = 0.5; y = 66; z = 0.5;
        yaw = gameRng.nextFloat() * Math.PI * 2;
    }
    private void setPhase(string p)
    {
        phase = p;
        phaseTime = 0;
        breathTime = 0;
    }
    private void flyToward(double tx, double ty, double tz, double accel)
    {
        double dx = tx - x, dy = ty - y, dz = tz - z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d == 0) { d = 1; }
        double speed = phase == "charging" ? 1.3 : 0.8;
        vx += (dx / d * speed - vx) * accel;
        vy += (dy / d * speed - vy) * accel;
        vz += (dz / d * speed - vz) * accel;
    }
    private LivingEntity findPlayer()
    {
        LivingEntity best = null;
        double bestD = double.PositiveInfinity;
        foreach (var e in world.entities)
        {
            if (!(e is LivingEntity p) || !p.isPlayer || p.dead || p.gameMode == 1) { continue; }
            double d = distanceToSq(p);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (invulnTicks > 0 || phase == "dying") { return false; }
        // reduced damage while flying (head hits are full — simplified: 50% while airborne)
        double dmg = amount;
        if (phase != "perched") { dmg *= 0.5; }
        if (source == "explosion") { dmg *= 0.25; }
        health -= dmg;
        hurtTime = 10;
        invulnTicks = 10;
        world.hooks.playSound("entity.ender_dragon.hurt", x, y, z, 4, 1);
        if (health <= 0)
        {
            health = 0;
            setPhase("dying");
            world.hooks.playSound("entity.ender_dragon.death", x, y, z, 6, 1);
        }
        else if (phase == "perched" && gameRng.nextFloat() < 0.3)
        {
            setPhase("takeoff");
        }
        return true;
    }
}

// =============================================================================
// WITHER
// =============================================================================
public sealed class WitherBoss : Monster
{
    public override string type => "wither";
    public int chargeTime = 220;
    public int[] shootCooldown = new int[] { 0, 0, 0 };
    public WitherBoss(World world) : base(world)
    {
        width = 0.9; height = 3.5;
        maxHealth = 300; health = 300;
        attackDamage = 8;
        noGravity = true;
        kbResist = 1;
        persistent = true;
        xpReward = 50;
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, e =>
            (e.isPlayer || (e as Mob)?.category == "creature") && !e.dead
        , 40, false));
    }
    public override void tick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) { return; }

        // summon charge-up
        if (chargeTime > 0)
        {
            chargeTime -= 1;
            health = Math.Min(maxHealth, health + maxHealth / 220);
            if (chargeTime % 20 == 0)
            {
                world.hooks.playSound("entity.wither.ambient", x, y, z, 4, 0.7);
            }
            if (chargeTime == 0)
            {
                explodeFn?.Invoke(world, x, y + 1.7, z, 7, false, this);
                world.hooks.playSound("entity.wither.spawn", x, y, z, 8, 1);
            }
            return;
        }

        targetGoals.tick(2, age);

        // hover toward target
        if (target is LivingEntity t && !t.dead)
        {
            double ty = t.y + 5;
            double dy = ty - y;
            vy += clampD(dy * 0.02, -0.06, 0.06);
            double dx = t.x - x, dz = t.z - z;
            double dh = Math.Sqrt(dx * dx + dz * dz);
            if (dh > 10)
            {
                vx += dx / dh * 0.02;
                vz += dz / dh * 0.02;
            }
            else if (dh < 5)
            {
                vx -= dx / dh * 0.02;
                vz -= dz / dh * 0.02;
            }
            yaw = detAtan2(-dx, dz);
            // shoot skulls from 3 heads
            for (int head = 0; head < 3; head++)
            {
                if (shootCooldown[head] > 0) { shootCooldown[head] -= 1; }
                else
                {
                    shootCooldown[head] = 40 + rng.nextInt(40);
                    double hx = head == 0 ? x : x + (head == 1 ? -1.3 : 1.3) * detCos(yaw);
                    double hz = head == 0 ? z : z + (head == 1 ? -1.3 : 1.3) * detSin(yaw);
                    var skull = new WitherSkull(world);
                    skull.blue = rng.nextFloat() < 0.1;
                    skull.setPos(hx, y + 3, hz);
                    skull.owner = this;
                    skull.shoot(t.x - hx, t.eyeY() - (y + 3), t.z - hz, skull.blue ? 0.45 : 0.9, 4);
                    world.addEntity(skull);
                    world.hooks.playSound("entity.wither.shoot", x, y, z, 3, 1);
                }
            }
        }
        else
        {
            vy += (0.3 - vy) * 0.02;
            vx *= 0.9; vz *= 0.9;
        }
        move(vx, vy, vz);
        vx *= 0.95; vy *= 0.92; vz *= 0.95;

        // break blocks around when hurt
        if (hurtTime > 0 && world.rule("mobGriefing") && age % 10 == 0)
        {
            bool broke = false;
            for (int dy = 0; dy <= 3; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int bx = ifloor(x) + dx, by = ifloor(y) + dy, bz = ifloor(z) + dz;
                        int c = world.getBlock(bx, by, bz);
                        int bid = c >> 4;
                        if (bid != 0 && bid != (int)B.bedrock && bid != (int)B.end_portal_frame && bid != (int)B.end_portal && blockDefs[bid].hardness >= 0 && blockDefs[bid].resistance < 1000)
                        {
                            world.breakBlockNaturally(bx, by, bz);
                            broke = true;
                        }
                    }
                }
            }
            if (broke) { world.hooks.playSound("entity.wither.break_block", x, y, z, 2, 1); }
        }
        // regen
        if (age % 20 == 0) { heal(1); }
    }
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (chargeTime > 0) { return false; }
        // armored below half health: immune to projectiles
        if (health < maxHealth / 2 && source == "projectile") { return false; }
        return base.hurt(amount, source, attacker);
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("nether_star") }; }
}

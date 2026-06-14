// Mob AI — A* pathfinding, navigation, goal framework,
// and the standard goal library used by every mob.
//
// deterministic-semantics contracts preserved here:
//  - GoalSelector.goals sort is STABLE (a stable sort); ties keep insert order.
//  - GoalSelector.active is insertion-ordered (deterministic Set iteration order).
//  - findPath pops the lowest-f node by linear scan, first match wins ties.
//
// Ported from Sources/PebbleCore/Entity/AI.swift.
// Defines static class AIGlobals (module-level free funcs walkable/findPath and
// the GoalFlag constants live there; surface via global using static AIGlobals).
// All other public types (PathNode, Navigation, Goal, GoalSelector, Mob, and the
// standard goals) live at namespace level.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

// ---------------------------------------------------------------------------
// Pathfinding (grid A*)
// ---------------------------------------------------------------------------
public struct PathNode
{
    public int x, y, z;
    public PathNode(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
}

public static class AIGlobals
{
    private static readonly (int, int, int)[] NEIGHBORS = new (int, int, int)[]
    {
        (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1),
        (1, 0, 1), (1, 0, -1), (-1, 0, 1), (-1, 0, -1),
    };

    public static bool walkable(World world, int x, int y, int z, bool avoidWater)
    {
        int below = world.getBlock(x, y - 1, z);
        int bid = below >> 4;
        int feet = world.getBlock(x, y, z) >> 4;
        int head = world.getBlock(x, y + 1, z) >> 4;
        bool feetOK = feet == 0 || !blockDefs[feet].solid;
        bool headOK = head == 0 || !blockDefs[head].solid;
        if (!feetOK || !headOK) return false;
        if (feet == (int)B.lava || head == (int)B.lava || bid == (int)B.lava) return false;
        if (feet == (int)B.fire || bid == (int)B.magma_block || bid == (int)B.cactus) return false;
        if (feet == (int)B.water) return !avoidWater;
        if (bid == (int)B.water) return !avoidWater; // swim surface
        return bid != 0 && blockDefs[bid].solid;
    }

    private struct K : IEquatable<K>
    {
        public int x, y, z;
        public K(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(K o) => x == o.x && y == o.y && z == o.z;
        public override bool Equals(object o) => o is K k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }

    private sealed class Node
    {
        public readonly int x, y, z;
        public readonly double g, f;
        public readonly Node parent;
        public Node(int x, int y, int z, double g, double f, Node parent)
        {
            this.x = x; this.y = y; this.z = z; this.g = g; this.f = f; this.parent = parent;
        }
    }

    public static List<PathNode> findPath(World world, double fromX, double fromY, double fromZ,
                                          double toX, double toY, double toZ,
                                          int maxNodes = 600, bool avoidWater = false)
    {
        int sx = ifloor(fromX), sy = ifloor(fromY), sz = ifloor(fromZ);
        int tx = ifloor(toX), ty = ifloor(toY), tz = ifloor(toZ);
        if (sx == tx && sy == ty && sz == tz) return new List<PathNode>();
        var open = new List<Node> { new Node(sx, sy, sz, 0, 0, null) };
        var seen = new Dictionary<K, double> { { new K(sx, sy, sz), 0 } };
        Node best = null;
        double bestH = double.PositiveInfinity;
        int iter = 0;
        while (open.Count != 0)
        {
            iter += 1;
            if (iter > maxNodes) break;
            // pop lowest f
            int bi = 0;
            for (int i = 1; i < open.Count; i++) if (open[i].f < open[bi].f) bi = i;
            var cur = open[bi];
            open.RemoveAt(bi);
            double h = (double)(Math.Abs(cur.x - tx) + Math.Abs(cur.y - ty) + Math.Abs(cur.z - tz));
            if (h < bestH) { bestH = h; best = cur; }
            if (cur.x == tx && Math.Abs(cur.y - ty) <= 1 && cur.z == tz) { best = cur; break; }
            foreach (var (dx, _, dz) in NEIGHBORS)
            {
                bool diag = dx != 0 && dz != 0;
                foreach (var dy in new[] { 0, 1, -1, -2, -3 })
                {
                    if (diag && dy != 0) continue;
                    int nx = cur.x + dx, ny = cur.y + dy, nz = cur.z + dz;
                    if (dy == 1)
                    {
                        // need headroom to jump
                        int above = world.getBlock(cur.x, cur.y + 2, cur.z) >> 4;
                        if (above != 0 && blockDefs[above].solid) continue;
                    }
                    if (!walkable(world, nx, ny, nz, avoidWater)) continue;
                    if (diag)
                    {
                        // both cardinals must be passable
                        if (!walkable(world, cur.x + dx, cur.y, cur.z, avoidWater) && !walkable(world, cur.x, cur.y, cur.z + dz, avoidWater)) continue;
                    }
                    double cost = (diag ? 1.41 : 1) + (double)Math.Abs(dy) * 0.5 + (dy < -1 ? (dy < -2 ? 4.0 : 2.0) : 0.0);
                    double g = cur.g + cost;
                    var k = new K(nx, ny, nz);
                    if (seen.TryGetValue(k, out double prev) && prev <= g) continue;
                    seen[k] = g;
                    double hh = (double)(Math.Abs(nx - tx) + Math.Abs(ny - ty) + Math.Abs(nz - tz));
                    open.Add(new Node(nx, ny, nz, g, g + hh * 1.1, cur));
                    break; // take first valid dy per direction
                }
            }
        }
        if (best == null || !(bestH <= 24)) return null;
        var bestNode = best;
        var path = new List<PathNode>();
        Node n = bestNode;
        while (n != null)
        {
            var cur = n;
            path.Insert(0, new PathNode(cur.x, cur.y, cur.z));
            n = cur.parent;
        }
        if (path.Count != 0) path.RemoveAt(0);
        return path;
    }
}

// ---------------------------------------------------------------------------
// Goal framework
// ---------------------------------------------------------------------------
// Swift `public enum GoalFlag` is a bare namespace for static Int constants — a
// top-level static class with consts here so `GoalFlag.move` resolves unqualified.
public static class GoalFlag
{
    public const int move = 1;
    public const int look = 2;
    public const int target = 4;
}

public sealed class Navigation
{
    public List<PathNode> path = null;
    public int pathIndex = 0;
    public int repathCooldown = 0;
    public double targetX = 0.0, targetY = 0.0, targetZ = 0.0;
    public double speedMod = 1.0;
    public int stuckTicks = 0;
    public int nodeTicks = 0;
    public double lastX = 0.0, lastZ = 0.0;
    public bool avoidWater = false;

    private readonly Mob mob;
    public Navigation(Mob mob) { this.mob = mob; }

    public bool moveTo(double x, double y, double z, double speedMod = 1)
    {
        this.speedMod = speedMod;
        double dx = x - targetX, dy = y - targetY, dz = z - targetZ;
        if (path != null && dx * dx + dy * dy + dz * dz < 1 && pathIndex < path.Count) return true;
        targetX = x; targetY = y; targetZ = z;
        if (repathCooldown > 0) return path != null;
        repathCooldown = 20;
        path = findPath(mob.world, mob.x, mob.y, mob.z, x, y, z, 600, avoidWater);
        pathIndex = 0;
        return path != null;
    }

    public bool moveToEntity(Entity e, double speedMod = 1)
    {
        return moveTo(e.x, e.y, e.z, speedMod);
    }

    public void stop()
    {
        path = null;
        mob.moveForward = 0;
        mob.jumping = false;
    }

    public bool isDone()
    {
        return path == null || pathIndex >= (path?.Count ?? 0);
    }

    public void tick()
    {
        if (repathCooldown > 0) repathCooldown -= 1;
        if (path == null || pathIndex >= path.Count)
        {
            mob.moveForward *= 0.5;
            return;
        }
        var p = path;
        var node = p[pathIndex];
        double nx = (double)node.x + 0.5, nz = (double)node.z + 0.5;
        double dx = nx - mob.x, dz = nz - mob.z;
        double distSq = dx * dx + dz * dz;
        // accept on horizontal arrival regardless of y when very close — a mob
        // >1 block above/below its node otherwise orbits it forever (the old
        // y-gate + fast movement never tripped the stuck detector)
        if (distSq < 0.6 * 0.6 && (Math.Abs(node.y - ifloor(mob.y)) <= 1 || distSq < 0.35 * 0.35))
        {
            pathIndex += 1;
            nodeTicks = 0;
            return;
        }
        nodeTicks += 1;
        if (nodeTicks > 80)
        {   // orbit/unreachable-node breaker
            stop();
            nodeTicks = 0;
            return;
        }
        // steer
        double targetYaw = detAtan2(-dx, dz);
        double d = targetYaw - mob.yaw;
        while (d > Math.PI) d -= Math.PI * 2;
        while (d < -Math.PI) d += Math.PI * 2;
        mob.yaw += clampD(d, -0.35, 0.35);
        mob.moveForward = Math.Abs(d) > 1.6 ? 0.2 : speedMod;
        mob.jumping = (node.y > ifloor(mob.y) && distSq < 2.5) || (mob.horizontalCollision && mob.onGround);
        if (mob.inWater && node.y >= ifloor(mob.y)) mob.jumping = true;
        // stuck detection
        double mdx = mob.x - lastX, mdz = mob.z - lastZ;
        double moved = Math.Sqrt(mdx * mdx + mdz * mdz);
        lastX = mob.x; lastZ = mob.z;
        if (moved < 0.01)
        {
            stuckTicks += 1;
            if (stuckTicks > 50)
            {
                stop();
                stuckTicks = 0;
                repathCooldown = 0;
            }
        }
        else
        {
            stuckTicks = 0;
        }
    }
}

public class Goal
{
    public int flags = GoalFlag.move | GoalFlag.look;
    public readonly Mob mob;
    public readonly int priority;
    public Goal(Mob mob, int priority)
    {
        this.mob = mob;
        this.priority = priority;
    }
    public virtual bool canUse() { return false; }
    public virtual bool canContinue() { return canUse(); }
    public virtual void start() { }
    public virtual void stop() { }
    public virtual void tick() { }
}

public sealed class GoalSelector
{
    public List<Goal> goals = new List<Goal>();
    public List<Goal> active = new List<Goal>();   // insertion-ordered like a deterministic Set

    public GoalSelector() { }

    public void add(Goal goal)
    {
        goals.Add(goal);
        // stable sort by priority (a stable sort)
        goals = goals
            .Select((element, offset) => (element, offset))
            .OrderBy(t => t, Comparer<(Goal element, int offset)>.Create((lhs, rhs) =>
            {
                if (lhs.element.priority != rhs.element.priority)
                    return lhs.element.priority < rhs.element.priority ? -1 : 1;
                if (lhs.offset != rhs.offset)
                    return lhs.offset < rhs.offset ? -1 : 1;
                return 0;
            }))
            .Select(t => t.element)
            .ToList();
    }

    public void tick(int interval, int age)
    {
        // stop finished
        // Swift `for g in active where !g.canContinue()` iterates a COW snapshot of
        // the pre-loop array and evaluates the `where` lazily per element, even as
        // `removeAll` mutates `active`. Mirror that: snapshot, then test inside.
        foreach (var g in active.ToList())
        {
            if (!g.canContinue())
            {
                g.stop();
                active.RemoveAll(x => ReferenceEquals(x, g));
            }
        }
        // try to start (staggered)
        if (age % interval == 0)
        {
            foreach (var g in goals)
            {
                if (active.Any(x => ReferenceEquals(x, g))) continue;
                // check mutex with higher-priority active goals
                bool blocked = false;
                foreach (var a in active.Where(a => (a.flags & g.flags) != 0))
                {
                    if (a.priority <= g.priority) { blocked = true; break; }
                }
                if (blocked) continue;
                if (g.canUse())
                {
                    // cancel lower-priority conflicting (COW-snapshot + lazy where)
                    foreach (var a in active.ToList())
                    {
                        if ((a.flags & g.flags) != 0 && a.priority > g.priority)
                        {
                            a.stop();
                            active.RemoveAll(x => ReferenceEquals(x, a));
                        }
                    }
                    g.start();
                    active.Add(g);
                }
            }
        }
        foreach (var g in active) g.tick();
    }

    public void stopAll()
    {
        foreach (var g in active) g.stop();
        active.Clear();
    }
}

// ---------------------------------------------------------------------------
// Mob base
// ---------------------------------------------------------------------------
public class Mob : LivingEntity
{
    // C# does not inherit constructors (Swift Mob inherits LivingEntity.init(world:)).
    public Mob(World world) : base(world) { }

    public GoalSelector goals = new GoalSelector();
    public GoalSelector targetGoals = new GoalSelector();
    private Navigation _nav;
    public Navigation nav => _nav ??= new Navigation(this);
    public LivingEntity target;
    public double? lookX = null;
    public double lookY = 0.0, lookZ = 0.0;
    public int ambientSoundTimer = 0;
    public bool baby = false;
    public int growUpAge = 0;       // ticks until adult (if baby)
    public int loveTicks = 0;
    public int breedCooldown = 0;
    public string category = "creature";   // monster | creature | water | ambient | misc
    public double attackDamage = 2.0;
    public double followRange = 16.0;
    public bool burnsInSun = false;
    public Entity leashedTo;
    public (int, int, int)? leashFence = null;
    public int? ownerId = null;
    public bool sitting = false;

    public override void tick() { mobTick(); }

    /// the Mob-level tick body — exposed so PiglinBrute/Zoglin can mimic the
    /// baseline `Monster.prototype.tick.call(this)` grandparent dispatch
    public void mobTick()
    {
        baseLivingTick();
        if (dead || deathTime > 0) return;

        // despawn rules
        if (!persistent && category == "monster")
        {
            double nearestPlayer = double.PositiveInfinity;
            foreach (var e in world.entities)
            {
                if (e is Entity ent && ent.isPlayer)
                {
                    double d = (ent.x - x) * (ent.x - x) + (ent.y - y) * (ent.y - y) + (ent.z - z) * (ent.z - z);
                    nearestPlayer = Math.Min(nearestPlayer, d);
                }
            }
            if (nearestPlayer > 128 * 128) { remove(); return; }
            if (nearestPlayer > 32 * 32 && rng.nextFloat() < 1.0 / 800) { remove(); return; }
        }

        // sunlight burning
        if (burnsInSun && world.info.hasSky && world.isDay() && fireTicks <= 0)
        {
            var helm = armor[0];
            if (helm == null && world.canSeeSky(ifloor(x), ifloor(y + height), ifloor(z)) && !inWater && world.rainLevel < 0.4)
            {
                fireTicks = 160;
            }
        }

        // baby growth
        if (baby && growUpAge > 0)
        {
            growUpAge -= 1;
            if (growUpAge <= 0) baby = false;
        }
        if (loveTicks > 0)
        {
            loveTicks -= 1;
            if (age % 8 == 0) world.hooks.addParticles("heart", x, y + height + 0.3, z, 1, 0.3, 0);
        }
        if (breedCooldown > 0) breedCooldown -= 1;

        // AI
        targetGoals.tick(2, age);
        goals.tick(2, age);
        nav.tick();

        // look control
        if (lookX is double lx)
        {
            lookAt(lx, lookY, lookZ, 0.25, 0.25);
        }

        // leash physics
        if (leashedTo != null || leashFence != null)
        {
            double lx2 = leashedTo?.x ?? (double)leashFence.Value.Item1 + 0.5;
            double ly = leashedTo?.y ?? (double)leashFence.Value.Item2 + 0.5;
            double lz = leashedTo?.z ?? (double)leashFence.Value.Item3 + 0.5;
            double dx = lx2 - x, dy = ly - y, dz = lz - z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist > 10)
            {
                leashedTo = null; leashFence = null;
                dropStack(new ItemStack(iid("lead"), 1));
            }
            else if (dist > 4)
            {
                vx += dx * 0.02; vy += dy * 0.01; vz += dz * 0.02;
            }
        }

        travel();

        // ambient sound
        ambientSoundTimer -= 1;
        if (ambientSoundTimer <= 0)
        {
            ambientSoundTimer = 80 + rng.nextInt(160);
            var snd = ambientSound();
            if (snd != null)
            {
                world.hooks.playSound(snd, x, y, z, 1, baby ? 1.4 : 0.95 + rng.nextFloat() * 0.1);
            }
        }
    }

    public void setTarget(LivingEntity t) { target = t; }

    public virtual void doMeleeAttack(LivingEntity target)
    {
        attackAnim = 1;
        double dmg = attackDamage;
        dmg += 3 * (double)effectLevel("strength");
        dmg -= 4 * (double)effectLevel("weakness");
        target.hurt(Math.Max(0, dmg), "mob", this);
        world.hooks.playSound("entity.player.attack.strong", x, y, z, 0.7, 1);
    }

    public virtual bool isFood(ItemStack stack) { return false; }

    /// breeding interaction — returns true if consumed
    public bool tryFeed(Entity player, ItemStack stack)
    {
        if (!isFood(stack)) return false;
        if (baby)
        {
            growUpAge = Math.Max(0, growUpAge - 1200);
            return true;
        }
        if (loveTicks <= 0 && breedCooldown <= 0)
        {
            loveTicks = 600;
            data.loveCause = player?.id;
            return true;
        }
        return false;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["health"] = health;
        d["baby"] = baby;
        d["sitting"] = sitting;
        if (ownerId is int oid) d["ownerId"] = oid;
        return d;
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        // (d["k"] as? NSNumber)?.doubleValue ?? default — JSON numbers arrive as IConvertible.
        health = (d.TryGetValue("health", out var vHealth) && vHealth is IConvertible cHealth && vHealth is not string)
            ? Convert.ToDouble(cHealth) : maxHealth;
        baby = (d.TryGetValue("baby", out var vBaby) && vBaby is bool bBaby) ? bBaby : false;
        sitting = (d.TryGetValue("sitting", out var vSit) && vSit is bool bSit) ? bSit : false;
        ownerId = (d.TryGetValue("ownerId", out var vOwner) && vOwner is IConvertible cOwner && vOwner is not string)
            ? Convert.ToInt32(cOwner) : (int?)null;
    }
}

// ---------------------------------------------------------------------------
// Standard goals
// ---------------------------------------------------------------------------
public sealed class FloatGoal : Goal
{
    public FloatGoal(Mob mob, int priority) : base(mob, priority)
    {
        flags = 0;
    }
    public override bool canUse() { return mob.inWater || mob.inLava; }
    public override void tick() { if (mob.rng.nextFloat() < 0.8) mob.jumping = true; }
}

public sealed class PanicGoal : Goal
{
    private readonly double speedMod;
    public PanicGoal(Mob mob, int priority, double speedMod = 1.25) : base(mob, priority)
    {
        this.speedMod = speedMod;
    }
    public override bool canUse()
    {
        return (mob.lastAttacker != null && mob.hurtTime > 0) || mob.fireTicks > 0;
    }
    public override bool canContinue() { return !mob.nav.isDone(); }
    public override void start()
    {
        // run away from attacker or random
        var m = mob;
        var away = m.lastAttacker;
        double ang = away != null ? detAtan2(m.x - away.x, m.z - away.z) : m.rng.nextFloat() * Math.PI * 2;
        double tx = m.x + detSin(ang) * 8 + (m.rng.nextFloat() - 0.5) * 4;
        double tz = m.z + detCos(ang) * 8 + (m.rng.nextFloat() - 0.5) * 4;
        m.nav.moveTo(tx, m.y, tz, speedMod);
    }
}

public class StrollGoal : Goal
{
    public readonly double speedMod;
    public readonly int interval;
    public StrollGoal(Mob mob, int priority, double speedMod = 1, int interval = 120) : base(mob, priority)
    {
        this.speedMod = speedMod;
        this.interval = interval;
    }
    public override bool canUse()
    {
        if (mob.sitting) return false;
        return mob.rng.nextInt(interval) == 0;
    }
    public override bool canContinue() { return !mob.nav.isDone(); }
    public override void start()
    {
        var m = mob;
        for (int i = 0; i < 8; i++)
        {
            int tx = ifloor(m.x) + m.rng.nextInt(21) - 10;
            int tz = ifloor(m.z) + m.rng.nextInt(21) - 10;
            int ty = ifloor(m.y) + m.rng.nextInt(7) - 3;
            if (walkable(m.world, tx, ty, tz, m.nav.avoidWater))
            {
                m.nav.moveTo((double)tx, (double)ty, (double)tz, speedMod);
                return;
            }
        }
    }
    public override void stop() { mob.nav.stop(); }
}

public sealed class LookAtPlayerGoal : Goal
{
    private readonly double range;
    private readonly double chance;
    private int lookTime = 0;
    private Entity targetE;
    public LookAtPlayerGoal(Mob mob, int priority, double range = 8, double chance = 0.02) : base(mob, priority)
    {
        this.range = range;
        this.chance = chance;
        flags = GoalFlag.look;
    }
    public override bool canUse()
    {
        if (mob.rng.nextFloat() > chance) return false;
        var players = mob.world.getEntitiesNear(mob.x, mob.y, mob.z, range, e => (e as Entity)?.isPlayer ?? false);
        if (players.Count == 0) return false;
        targetE = players[0] as Entity;
        return true;
    }
    public override bool canContinue() { return lookTime > 0 && targetE != null && !targetE.dead; }
    public override void start() { lookTime = 40 + mob.rng.nextInt(40); }
    public override void tick()
    {
        lookTime -= 1;
        if (targetE is Entity t)
        {
            mob.lookX = t.x;
            mob.lookY = t.eyeY();
            mob.lookZ = t.z;
        }
    }
    public override void stop() { mob.lookX = null; }
}

public sealed class RandomLookGoal : Goal
{
    private int time = 0;
    public RandomLookGoal(Mob mob, int priority) : base(mob, priority)
    {
        flags = GoalFlag.look;
    }
    public override bool canUse() { return mob.rng.nextFloat() < 0.02; }
    public override bool canContinue() { return time > 0; }
    public override void start()
    {
        time = 20 + mob.rng.nextInt(20);
        double ang = mob.rng.nextFloat() * Math.PI * 2;
        mob.lookX = mob.x + detSin(ang) * 4;
        mob.lookY = mob.eyeY();
        mob.lookZ = mob.z + detCos(ang) * 4;
    }
    public override void tick() { time -= 1; }
    public override void stop() { mob.lookX = null; }
}

public sealed class MeleeAttackGoal : Goal
{
    private readonly double speedMod;
    private int attackCooldown = 0;
    public MeleeAttackGoal(Mob mob, int priority, double speedMod = 1.2) : base(mob, priority)
    {
        this.speedMod = speedMod;
    }
    public override bool canUse() { return mob.target != null && !mob.target.dead; }
    public override void start() { mob.sprinting = true; }
    public override void stop()
    {
        mob.sprinting = false;
        mob.nav.stop();
    }
    public override void tick()
    {
        var m = mob;
        if (m.target == null) return;
        var t = m.target;
        m.lookX = t.x; m.lookY = t.eyeY(); m.lookZ = t.z;
        if (attackCooldown > 0) attackCooldown -= 1;
        double reach = m.width / 2 + t.width / 2 + 0.8;
        double reachSq = reach * reach;
        double dSq = (m.x - t.x) * (m.x - t.x) + (m.z - t.z) * (m.z - t.z);
        double vert = Math.Abs(t.y - m.y);
        if (dSq <= reachSq && vert < 2.5)
        {
            if (attackCooldown <= 0)
            {
                attackCooldown = 20;
                m.doMeleeAttack(t);
            }
            m.nav.stop();
        }
        else
        {
            m.nav.moveToEntity(t, speedMod);
        }
    }
}

public sealed class NearestTargetGoal : Goal
{
    private readonly Func<LivingEntity, bool> filter;
    private readonly double range;
    private readonly bool needSight;
    private readonly Func<World, int, int, int, bool> lightGate;
    private int scanCooldown = 0;
    public NearestTargetGoal(Mob mob, int priority, Func<LivingEntity, bool> filter,
                             double range = 16, bool needSight = true,
                             Func<World, int, int, int, bool> lightGate = null) : base(mob, priority)
    {
        this.filter = filter;
        this.range = range;
        this.needSight = needSight;
        this.lightGate = lightGate;
        flags = GoalFlag.target;
    }
    public override bool canUse()
    {
        int sc = scanCooldown;
        scanCooldown -= 1;
        if (sc > 0) return false;
        scanCooldown = 10;
        var m = mob;
        var candidates = m.world.getEntitiesNear(m.x, m.y, m.z, range, e =>
        {
            if (!(e is LivingEntity le)) return false;
            return !ReferenceEquals(le, m) && !le.dead && this.filter(le);
        }).Select(e => e as LivingEntity).Where(e => e != null).ToList();
        LivingEntity best = null;
        double bestD = double.PositiveInfinity;
        foreach (var c in candidates)
        {
            if (c.isPlayer && (c.gameMode == 1 || c.invisibleToMobs)) continue;
            double d = m.distanceToSq(c);
            if (d < bestD && (!needSight || m.canSee(c))) { bestD = d; best = c; }
        }
        if (best != null)
        {
            m.setTarget(best);
            return true;
        }
        return false;
    }
    public override bool canContinue()
    {
        var t = mob.target;
        if (t == null || t.dead) return false;
        if (mob.distanceToSq(t) > (range * 1.5) * (range * 1.5)) { mob.setTarget(null); return false; }
        return true;
    }
}

public sealed class HurtByTargetGoal : Goal
{
    private readonly bool alertSame;
    public HurtByTargetGoal(Mob mob, int priority, bool alertSame = false) : base(mob, priority)
    {
        this.alertSame = alertSame;
        flags = GoalFlag.target;
    }
    public override bool canUse()
    {
        var m = mob;
        if (m.lastAttacker is Entity attacker && !attacker.dead && m.hurtTime > 0 && attacker is LivingEntity living)
        {
            m.setTarget(living);
            if (alertSame)
            {
                foreach (var e in m.world.getEntitiesNear(m.x, m.y, m.z, 16, filter: o => (o as Entity)?.type == m.type))
                {
                    if (e is Mob mob2 && mob2.target == null)
                    {
                        mob2.setTarget(living);
                    }
                }
            }
            return true;
        }
        return false;
    }
    public override bool canContinue() { return mob.target != null && !mob.target.dead; }
}

public sealed class AvoidEntityGoal : Goal
{
    private readonly Func<Entity, bool> filter;
    private readonly double range;
    private readonly double speedMod;
    private Entity fleeing;
    public AvoidEntityGoal(Mob mob, int priority, Func<Entity, bool> filter,
                           double range = 8, double speedMod = 1.2) : base(mob, priority)
    {
        this.filter = filter;
        this.range = range;
        this.speedMod = speedMod;
    }
    public override bool canUse()
    {
        var near = mob.world.getEntitiesNear(mob.x, mob.y, mob.z, range, e =>
        {
            if (!(e is Entity ent)) return false;
            return this.filter(ent);
        });
        fleeing = near.FirstOrDefault() as Entity;
        return fleeing != null;
    }
    public override void tick()
    {
        var m = mob;
        if (fleeing == null) return;
        var f = fleeing;
        if (m.nav.isDone())
        {
            double ang = detAtan2(m.x - f.x, m.z - f.z);
            m.nav.moveTo(m.x + detSin(ang) * 10, m.y, m.z + detCos(ang) * 10, speedMod);
        }
    }
    public override void stop() { mob.nav.stop(); }
}

public sealed class TemptGoal : Goal
{
    private readonly List<string> items;
    private readonly double speedMod;
    private LivingEntity player;
    public TemptGoal(Mob mob, int priority, List<string> items, double speedMod = 1) : base(mob, priority)
    {
        this.items = items;
        this.speedMod = speedMod;
    }
    public override bool canUse()
    {
        var players = mob.world.getEntitiesNear(mob.x, mob.y, mob.z, 10, e => (e as Entity)?.isPlayer ?? false);
        foreach (var p in players)
        {
            if (!(p is LivingEntity pl)) continue;
            if (pl.mainHand is ItemStack held && items.Contains(itemDef(held.id).name))
            {
                player = pl;
                return true;
            }
        }
        return false;
    }
    public override void tick()
    {
        var m = mob;
        if (player == null) return;
        var p = player;
        m.lookX = p.x; m.lookY = p.eyeY(); m.lookZ = p.z;
        if (m.distanceToSq(p) > 5) m.nav.moveToEntity(p, speedMod);
        else m.nav.stop();
    }
    public override void stop() { mob.nav.stop(); player = null; }
}

public sealed class BreedGoal : Goal
{
    private readonly Action<Mob, Mob> spawnBaby;
    private Mob partner;
    private int timer = 0;
    public BreedGoal(Mob mob, int priority, Action<Mob, Mob> spawnBaby) : base(mob, priority)
    {
        this.spawnBaby = spawnBaby;
    }
    public override bool canUse()
    {
        var m = mob;
        if (m.loveTicks <= 0) return false;
        var partners = m.world.getEntitiesNear(m.x, m.y, m.z, 8, e =>
        {
            if (!(e is Mob other)) return false;
            return other.type == m.type && !ReferenceEquals(other, m) && other.loveTicks > 0;
        });
        partner = partners.FirstOrDefault() as Mob;
        return partner != null;
    }
    public override bool canContinue()
    {
        return partner != null && !partner.dead && mob.loveTicks > 0 && partner.loveTicks > 0;
    }
    public override void start() { timer = 0; }
    public override void tick()
    {
        var m = mob;
        if (partner == null) return;
        var p = partner;
        m.lookX = p.x; m.lookY = p.y; m.lookZ = p.z;
        m.nav.moveToEntity(p, 1);
        if (m.distanceToSq(p) < 6)
        {
            timer += 1;
            if (timer >= 60)
            {
                m.loveTicks = 0; p.loveTicks = 0;
                m.breedCooldown = 6000; p.breedCooldown = 6000;
                spawnBaby(m, p);
                partner = null;
            }
        }
    }
}

public sealed class FollowParentGoal : Goal
{
    private Mob parent;
    public FollowParentGoal(Mob mob, int priority) : base(mob, priority)
    {
    }
    public override bool canUse()
    {
        if (!mob.baby) return false;
        var parents = mob.world.getEntitiesNear(mob.x, mob.y, mob.z, 12, e =>
        {
            if (!(e is Mob other)) return false;
            return other.type == this.mob.type && !other.baby;
        });
        parent = parents.FirstOrDefault() as Mob;
        if (parent == null) return false;
        var p = parent;
        return mob.distanceToSq(p) > 9;
    }
    public override void tick()
    {
        if (parent is Mob p && mob.nav.isDone()) mob.nav.moveToEntity(p, 1.1);
    }
}

public sealed class FollowOwnerGoal : Goal
{
    private readonly double minDist;
    private readonly double teleportDist;
    public FollowOwnerGoal(Mob mob, int priority, double minDist = 4, double teleportDist = 12) : base(mob, priority)
    {
        this.minDist = minDist;
        this.teleportDist = teleportDist;
    }
    private Entity owner()
    {
        if (mob.ownerId == null) return null;
        int ownerId = mob.ownerId.Value;
        return mob.world.entityById.TryGetValue(ownerId, out var e) ? e as Entity : null;
    }
    public override bool canUse()
    {
        if (mob.sitting) return false;
        var o = owner();
        if (o == null) return false;
        return mob.distanceToSq(o) > minDist * minDist;
    }
    public override void tick()
    {
        var o = owner();
        if (o == null) return;
        var m = mob;
        m.lookX = o.x; m.lookY = o.eyeY(); m.lookZ = o.z;
        double dSq = m.distanceToSq(o);
        if (dSq > teleportDist * teleportDist)
        {
            // teleport to owner
            m.setPos(o.x + (m.rng.nextFloat() - 0.5) * 3, o.y + 0.5, o.z + (m.rng.nextFloat() - 0.5) * 3);
            m.nav.stop();
        }
        else if (m.nav.isDone())
        {
            m.nav.moveToEntity(o, 1.2);
        }
    }
    public override void stop() { mob.nav.stop(); }
}

public sealed class SitWhenOrderedGoal : Goal
{
    public SitWhenOrderedGoal(Mob mob, int priority) : base(mob, priority) { }
    public override bool canUse() { return mob.sitting; }
    public override void tick() { mob.nav.stop(); mob.moveForward = 0; }
}

public sealed class RangedAttackGoal : Goal
{
    private readonly int interval;
    private readonly double range;
    private readonly Action<LivingEntity, double> attack;
    private readonly bool strafe;
    private int cooldown = 0;
    public RangedAttackGoal(Mob mob, int priority, int interval, double range,
                            Action<LivingEntity, double> attack, bool strafe = true) : base(mob, priority)
    {
        this.interval = interval;
        this.range = range;
        this.attack = attack;
        this.strafe = strafe;
    }
    public override bool canUse() { return mob.target != null && !mob.target.dead; }
    public override void stop() { mob.nav.stop(); mob.sprinting = false; mob.data.aiming = false; }
    public override void tick()
    {
        var m = mob;
        if (m.target == null) return;
        var t = m.target;
        m.lookX = t.x; m.lookY = t.eyeY(); m.lookZ = t.z;
        m.data.aiming = true;
        double dSq = m.distanceToSq(t);
        bool canSee = m.canSee(t);
        if (dSq < range * range && canSee)
        {
            m.nav.stop();
            // strafe sideways
            if (strafe)
            {
                m.moveStrafe = detSin((double)m.age * 0.1) * 0.5;
                if (dSq < 9) m.moveForward = -0.5;
                else m.moveForward = 0;
            }
            int cd = cooldown;
            cooldown -= 1;
            if (cd <= 0)
            {
                cooldown = interval;
                attack(t, clampD(Math.Sqrt(dSq) / range, 0.3, 1));
            }
        }
        else
        {
            m.moveStrafe = 0;
            m.nav.moveToEntity(t, 1.1);
            cooldown = Math.Max(cooldown - 1, 10);
        }
    }
}

public class MoveToBlockGoal : Goal
{
    public readonly Func<World, int, int, int, bool> valid;
    public readonly int range;
    public readonly double speedMod;
    public readonly int interval;
    public (int, int, int)? targetPos = null;
    // NOTE: the Swift original has a `var tries = 0` here that is never read (canUse
    // uses a local loop counter); it was dead in the source, so it's omitted here.
    public MoveToBlockGoal(Mob mob, int priority, Func<World, int, int, int, bool> valid,
                           int range = 8, double speedMod = 1, int interval = 40) : base(mob, priority)
    {
        this.valid = valid;
        this.range = range;
        this.speedMod = speedMod;
        this.interval = interval;
    }
    public override bool canUse()
    {
        if (mob.rng.nextInt(interval) != 0) return false;
        var m = mob;
        for (int i = 0; i < 16; i++)
        {
            int x = ifloor(m.x) + m.rng.nextInt(range * 2 + 1) - range;
            int y = ifloor(m.y) + m.rng.nextInt(5) - 2;
            int z = ifloor(m.z) + m.rng.nextInt(range * 2 + 1) - range;
            if (valid(m.world, x, y, z))
            {
                targetPos = (x, y, z);
                return true;
            }
        }
        return false;
    }
    public override bool canContinue()
    {
        if (targetPos == null) return false;
        var t = targetPos.Value;
        return !mob.nav.isDone() && valid(mob.world, t.Item1, t.Item2, t.Item3);
    }
    public override void start()
    {
        if (targetPos != null) { var t = targetPos.Value; mob.nav.moveTo((double)t.Item1, (double)t.Item2, (double)t.Item3, speedMod); }
    }
    public bool reached()
    {
        if (targetPos == null) return false;
        var t = targetPos.Value;
        double dx = mob.x - (double)t.Item1 - 0.5, dz = mob.z - (double)t.Item3 - 0.5;
        return dx * dx + dz * dz < 2 && Math.Abs(mob.y - (double)t.Item2) < 2;
    }
}

public class RandomSwimGoal : StrollGoal
{
    public RandomSwimGoal(Mob mob, int priority, double speedMod = 1, int interval = 120)
        : base(mob, priority, speedMod, interval) { }
    public override void start()
    {
        var m = mob;
        for (int i = 0; i < 10; i++)
        {
            int tx = ifloor(m.x) + m.rng.nextInt(15) - 7;
            int ty = ifloor(m.y) + m.rng.nextInt(7) - 3;
            int tz = ifloor(m.z) + m.rng.nextInt(15) - 7;
            if ((m.world.getBlock(tx, ty, tz) >> 4) == (int)B.water)
            {
                m.data.swimTarget = new[] { (double)tx + 0.5, (double)ty + 0.5, (double)tz + 0.5 };
                return;
            }
        }
    }
    public override bool canContinue() { return mob.data.swimTarget != null; }
    public override void tick()
    {
        var m = mob;
        if (m.data.swimTarget == null) return;
        var t = m.data.swimTarget;
        double dx = t[0] - m.x, dy = t[1] - m.y, dz = t[2] - m.z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d < 1.2 || (m.world.getBlock(ifloor(t[0]), ifloor(t[1]), ifloor(t[2])) >> 4) != (int)B.water)
        {
            m.data.swimTarget = null;
            return;
        }
        m.vx += dx / d * 0.02;
        m.vy += dy / d * 0.02;
        m.vz += dz / d * 0.02;
        // near-vertical targets have degenerate horizontal headings — turning
        // toward atan2 noise made fish/squid spin in place
        if (dx * dx + dz * dz > 0.01)
        {
            double target = detAtan2(-dx, dz);
            m.yaw += clampD(wrapAngle(target - m.yaw), -0.3, 0.3);
        }
    }
}

// Entity base — AABB physics with auto-step, fluid
// state, fire, riding, fall tracking, persistence hooks.
//
// Defines static class `EntityGlobals` holding the module-level free funcs/var:
//   ifloor, resetEntityIds, peekNextEntityId, dnum, inum (and the private
//   nextEntityId counter). Surface via `global using static PebbleCore.EntityGlobals`.
// Types EntityData and Entity live at namespace level.
//
// CLIMBABLE comes from BlockCaches; isWaterlogged from the cell helper layer;
// detAtan2 from DetMath; clampD/sweepX/sweepY/sweepZ from MathX; B/blockDefs from
// Reg/BlockDefs; World/ItemStack referenced by name (defined elsewhere in the
// assembly).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PebbleCore;

public static class EntityGlobals
{
    public static int ifloor(double x) => (int)Math.Floor(x);

    private static int nextEntityId = 1;
    /// true reset (not a ratchet) — called on world load so id sequences don't
    /// depend on which worlds were opened earlier in the session
    public static void resetEntityIds(int start) { nextEntityId = start; }
    public static int peekNextEntityId() => nextEntityId;

    // allocate the next entity id (mutates the module-global counter)
    internal static int allocEntityId()
    {
        int id = nextEntityId;
        nextEntityId += 1;
        return id;
    }

    // JSON field readers (baseline `d.x ?? 0` semantics)
    public static double dnum(object v) => v is IConvertible c && v is not string ? Convert.ToDouble(c) : 0;
    public static int inum(object v) => v is IConvertible c && v is not string ? Convert.ToInt32(c) : 0;
}

/// generic per-entity data bag (variant, color, tame owner id, …) — closed
/// field set surveyed from the baseline `data: Record<string, any>` usage.
public sealed class EntityData : IEquatable<EntityData>
{
    public int? variant;
    public int? color;
    public int? size;
    public int? pattern;
    public bool? puffed;
    public double? swelling;
    public bool? grazing;
    public int? stingTimer;
    public int? buckTimer;
    public int? loveCause;
    public bool? baby;
    public bool? brown;
    public bool? sheared;
    public bool? charged;
    public bool? captain;
    public bool? cold;
    public bool? hanging;
    public bool? aiming;
    public bool? airborne;
    public bool? crossed;
    public bool? leatherBoots;
    public bool? persistent;
    public double? open;
    public string gene;
    public string deathCause;
    public string deathAttacker;
    public double[] swimTarget;

    public EntityData() { }

    public bool Equals(EntityData o)
    {
        if (o == null) return false;
        return variant == o.variant && color == o.color && size == o.size && pattern == o.pattern
            && puffed == o.puffed && swelling == o.swelling && grazing == o.grazing
            && stingTimer == o.stingTimer && buckTimer == o.buckTimer && loveCause == o.loveCause
            && baby == o.baby && brown == o.brown && sheared == o.sheared && charged == o.charged
            && captain == o.captain && cold == o.cold && hanging == o.hanging && aiming == o.aiming
            && airborne == o.airborne && crossed == o.crossed && leatherBoots == o.leatherBoots
            && persistent == o.persistent && open == o.open && gene == o.gene
            && deathCause == o.deathCause && deathAttacker == o.deathAttacker
            && SeqEq(swimTarget, o.swimTarget);
    }
    private static bool SeqEq(double[] a, double[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    public override bool Equals(object o) => Equals(o as EntityData);
    public override int GetHashCode() => HashCode.Combine(variant, color, size, pattern, gene, deathCause, open, persistent);
}

public class Entity : EntityRef
{
    public int id { get; }
    public virtual string type => "entity";
    public double x { get; set; } = 0.0;
    public double y { get; set; } = 0.0;
    public double z { get; set; } = 0.0;
    public double prevX = 0.0, prevY = 0.0, prevZ = 0.0;
    public double vx = 0.0, vy = 0.0, vz = 0.0;
    public double yaw = 0.0, pitch = 0.0;            // radians
    public double prevYaw = 0.0, prevPitch = 0.0;
    public double width = 0.6, height = 1.8;
    public double stepHeight = 0.6;
    public bool onGround = false;
    public bool horizontalCollision = false;
    public bool dead { get; set; } = false;
    public int age = 0;
    public int fireTicks = 0;
    public int airSupply = 300;
    public int invulnTicks = 0;
    public double fallDistance = 0.0;
    public bool inWater = false;
    public bool inLava = false;
    public bool underwater = false;
    public bool inPowderSnow = false;
    public int freezeTicks = 0;
    public bool noGravity = false;
    public double gravityScale = 1.0;
    /// riding
    public Entity vehicle;
    public List<Entity> passengers = new List<Entity>();
    /// mark for save persistence
    public bool persistent = false;
    public EntityData data = new EntityData();
    public int portalCooldown = 0;
    public int portalTime = 0;
    /// player-only flags surfaced on the base for baseline `(this as any)` checks
    public virtual bool isPlayer => false;
    public bool noClip = false;

    /// var (not let): dimension travel re-homes the player into the dest world
    public World world;

    public Entity(World world)
    {
        this.id = EntityGlobals.allocEntityId();
        this.world = world;
    }

    public AABB bb()
    {
        double hw = width / 2;
        return new AABB(x - hw, y, z - hw, x + hw, y + height, z + hw);
    }
    public virtual double eyeY() => y + height * 0.85;
    public double centerY() => y + height / 2;

    public void setPos(double x, double y, double z)
    {
        this.x = x; this.y = y; this.z = z;
        prevX = x; prevY = y; prevZ = z;
    }

    public virtual void remove()
    {
        dead = true;
        foreach (var p in passengers) { p.vehicle = null; }
        passengers.Clear();
        if (vehicle != null) { dismount(); }
    }

    public void mount(Entity v)
    {
        if (vehicle != null) { dismount(); }
        vehicle = v;
        v.passengers.Add(this);
    }
    public void dismount()
    {
        if (vehicle == null) return;
        var v = vehicle;
        vehicle = null;
        int i = v.passengers.FindIndex(p => ReferenceEquals(p, this));
        if (i >= 0)
        {
            v.passengers.RemoveAt(i);
        }
        // pop out to a safe spot
        y = v.y + v.height + 0.01;
    }

    public void baseTick()
    {
        prevX = x; prevY = y; prevZ = z;
        prevYaw = yaw; prevPitch = pitch;
        age += 1;
        if (invulnTicks > 0) { invulnTicks -= 1; }
        if (portalCooldown > 0) { portalCooldown -= 1; }
        updateFluidState();
        // fire
        if (fireTicks > 0)
        {
            if (inWater || inPowderSnow)
            {
                fireTicks = 0;
                if (inPowderSnow) { world.setBlock(ifloor(x), ifloor(y), ifloor(z), 0); }
            }
            else
            {
                if (fireTicks % 20 == 0 && world.rule("fireDamage")) { _ = hurt(1, "fire"); }
                fireTicks -= 1;
            }
        }
        if (inLava)
        {
            _ = hurt(4, "lava");
            fireTicks = Math.Max(fireTicks, 300);
            fallDistance *= 0.5;
        }
        // void
        if (y < (double)(world.info.minY - 64))
        {
            _ = hurt(4, "void");
            if (y < (double)(world.info.minY - 128) && !isPlayer) { remove(); }
        }
        // freezing
        if (inPowderSnow)
        {
            freezeTicks = Math.Min(freezeTicks + 1, 140);
        }
        else
        {
            freezeTicks = Math.Max(0, freezeTicks - 2);
        }
        if (freezeTicks >= 140 && age % 40 == 0) { _ = hurt(1, "freeze"); }
    }

    internal void updateFluidState()
    {
        var box = bb();
        bool water = false, lava = false, powder = false;
        int x0 = ifloor(box.x0), x1 = ifloor(box.x1);
        int y0 = ifloor(box.y0), y1 = ifloor(box.y1);
        int z0 = ifloor(box.z0), z1 = ifloor(box.z1);
        for (int yy = y0; yy <= Math.Max(y0, y1); yy++)
        {
            for (int zz = z0; zz <= Math.Max(z0, z1); zz++)
            {
                for (int xx = x0; xx <= Math.Max(x0, x1); xx++)
                {
                    int cell = world.getBlock(xx, yy, zz);
                    int bid = cell >> 4;
                    if (bid == (int)B.water || (cell >= 0 && isWaterlogged((ushort)cell)))
                    {
                        double h = (double)yy + world.fluidHeight(xx, yy, zz);
                        if (box.y0 < h) { water = true; }
                    }
                    else if (bid == (int)B.lava)
                    {
                        double h = (double)yy + world.fluidHeight(xx, yy, zz);
                        if (box.y0 < h) { lava = true; }
                    }
                    else if (bid == (int)B.powder_snow)
                    {
                        powder = true;
                    }
                }
            }
        }
        inWater = water;
        inLava = lava;
        inPowderSnow = powder;
        int eyeCell = world.getBlock(ifloor(x), ifloor(eyeY()), ifloor(z));
        underwater = (eyeCell >> 4) == (int)B.water || (eyeCell >= 0 && isWaterlogged((ushort)eyeCell));
        // bubble columns: water column above magma (down) or soul sand (up)
        if (water)
        {
            int bx = ifloor(x), bz = ifloor(z);
            int by = ifloor(box.y0);
            while (by > world.info.minY && by > ifloor(box.y0) - 16)
            {
                int c = world.getBlock(bx, by, bz) >> 4;
                if (c == (int)B.water) { by -= 1; continue; }
                if (c == (int)B.magma_block) { vy = Math.Max(vy - 0.05, -0.5); }
                else if (c == (int)B.soul_sand) { vy = Math.Min(vy + 0.06, 0.6); }
                break;
            }
        }
    }

    /// swept AABB move with auto-step
    public virtual void move(double dxIn, double dyIn, double dzIn)
    {
        double dx = dxIn, dy = dyIn, dz = dzIn;
        if (noClip)
        {
            x += dx; y += dy; z += dz;
            return;
        }
        // cobweb / sweet berry slow
        int cell = world.getBlock(ifloor(x), ifloor(y + 0.2), ifloor(z));
        int bid = cell >> 4;
        if (bid == (int)B.cobweb) { dx *= 0.25; dy *= 0.05; dz *= 0.25; vx = 0; vy = 0; vz = 0; }
        else if (bid == (int)B.sweet_berry_bush) { dx *= 0.8; dy *= 0.75; dz *= 0.8; }
        else if (bid == (int)B.powder_snow && !(data.leatherBoots ?? false)) { dx *= 0.9; dy *= 0.9; dz *= 0.9; }
        if (bid == (int)B.honey_block || (world.getBlock(ifloor(x), ifloor(y - 0.1), ifloor(z)) >> 4) == (int)B.honey_block)
        {
            dx *= 0.4; dz *= 0.4;
            if (dy < -0.13) { dy = -0.05; vy = -0.05; }
        }

        double origDx = dx, origDy = dy, origDz = dz;
        var boxes = new List<AABB>();
        var bb0 = bb();
        var query = new AABB(
            Math.Min(bb0.x0, bb0.x0 + dx) - 0.5, Math.Min(bb0.y0, bb0.y0 + dy) - 1.5,
            Math.Min(bb0.z0, bb0.z0 + dz) - 0.5, Math.Max(bb0.x1, bb0.x1 + dx) + 0.5,
            Math.Max(bb0.y1, bb0.y1 + dy) + 0.5, Math.Max(bb0.z1, bb0.z1 + dz) + 0.5
        );
        world.forEachCollisionBox(query, b => boxes.Add(b));

        var box = bb0;
        // Y
        foreach (var b in boxes) { dy = sweepY(box, b, dy); }
        box = new AABB(box.x0, box.y0 + dy, box.z0, box.x1, box.y1 + dy, box.z1);
        // X
        foreach (var b in boxes) { dx = sweepX(box, b, dx); }
        box = new AABB(box.x0 + dx, box.y0, box.z0, box.x1 + dx, box.y1, box.z1);
        // Z
        foreach (var b in boxes) { dz = sweepZ(box, b, dz); }

        bool hitX = Math.Abs(dx - origDx) > 1e-7;
        bool hitZ = Math.Abs(dz - origDz) > 1e-7;
        bool hitY = Math.Abs(dy - origDy) > 1e-7;
        bool wasOnGround = onGround || (hitY && origDy < 0);

        // auto-step
        if ((hitX || hitZ) && wasOnGround && stepHeight > 0)
        {
            double sx = origDx, sy = stepHeight, sz = origDz;
            var sbox = bb0;
            foreach (var b in boxes) { sy = sweepY(sbox, b, sy); }
            sbox = new AABB(sbox.x0, sbox.y0 + sy, sbox.z0, sbox.x1, sbox.y1 + sy, sbox.z1);
            foreach (var b in boxes) { sx = sweepX(sbox, b, sx); }
            sbox = new AABB(sbox.x0 + sx, sbox.y0, sbox.z0, sbox.x1 + sx, sbox.y1, sbox.z1);
            foreach (var b in boxes) { sz = sweepZ(sbox, b, sz); }
            sbox = new AABB(sbox.x0, sbox.y0, sbox.z0 + sz, sbox.x1, sbox.y1, sbox.z1 + sz);
            // settle back down
            double down = -stepHeight;
            foreach (var b in boxes) { down = sweepY(sbox, b, down); }
            if ((sx * sx + sz * sz) > (dx * dx + dz * dz) + 1e-7)
            {
                dx = sx; dz = sz; dy = sy + down;
            }
        }

        x += dx; y += dy; z += dz;
        horizontalCollision = hitX || hitZ;
        onGround = hitY && origDy < 0;
        if (hitX) { vx = 0; }
        if (hitY) { vy = 0; }
        if (hitZ) { vz = 0; }

        // fall distance
        if (onGround)
        {
            if (fallDistance > 0)
            {
                onLand(fallDistance);
                fallDistance = 0;
            }
        }
        else if (dy < 0)
        {
            fallDistance -= dy;
        }
        if (inWater || isClimbing()) { fallDistance = 0; }
    }

    public bool isClimbing()
    {
        int cell = world.getBlock(ifloor(x), ifloor(y), ifloor(z));
        int bid = cell >> 4;
        return bid >= 0 && bid < CLIMBABLE.Length && CLIMBABLE[bid] == 1;
    }

    /// standing-on block
    public int groundBlock()
        => world.getBlock(ifloor(x), ifloor(y - 0.35), ifloor(z));

    public virtual void onLand(double fallDistance) { }

    public virtual void tick() { }

    public virtual bool hurt(double amount, string source, Entity attacker = null) => false;

    /// right-click interaction; return true if handled
    public virtual bool interact(Entity player, ItemStack stack) => false;

    public double distanceToSq(Entity e)
    {
        double dx = x - e.x, dy = y - e.y, dz = z - e.z;
        return dx * dx + dy * dy + dz * dz;
    }
    public double distanceTo(Entity e) => Math.Sqrt(distanceToSq(e));

    public void lookAt(double tx, double ty, double tz, double maxYawStep = 0.5, double maxPitchStep = 0.5)
    {
        double dx = tx - x, dy = ty - eyeY(), dz = tz - z;
        double targetYaw = detAtan2(-dx, dz);
        double horiz = Math.Sqrt(dx * dx + dz * dz);
        double targetPitch = -detAtan2(dy, horiz);
        double dYaw = targetYaw - yaw;
        while (dYaw > Math.PI) { dYaw -= Math.PI * 2; }
        while (dYaw < -Math.PI) { dYaw += Math.PI * 2; }
        yaw += clampD(dYaw, -maxYawStep, maxYawStep);
        pitch += clampD(targetPitch - pitch, -maxPitchStep, maxPitchStep);
    }

    public bool canSee(Entity e)
    {
        double ox = x, oy = eyeY(), oz = z;
        double tx = e.x, ty = e.eyeY(), tz = e.z;
        double dx = tx - ox, dy = ty - oy, dz = tz - oz;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 0.01) { return true; }
        var hit = world.raycast(ox, oy, oz, dx / dist, dy / dist, dz / dist, dist);
        return hit == null;
    }

    // Swift's synthesized Codable encodes optional struct fields with
    // encodeIfPresent, so nil keys are absent from the JSON object. Mirror that
    // so the saved `data` blob matches the baseline (only non-nil keys present).
    private static readonly JsonSerializerOptions dataJsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };

    /// serialize for chunk save
    public virtual Dictionary<string, object> save()
    {
        var d = new Dictionary<string, object>
        {
            { "type", type }, { "x", x }, { "y", y }, { "z", z },
            { "vx", vx }, { "vy", vy }, { "vz", vz },
            { "yaw", yaw }, { "pitch", pitch }, { "age", age },
            { "fire", fireTicks }, { "persistent", persistent },
        };
        try
        {
            byte[] enc = JsonSerializer.SerializeToUtf8Bytes(data, dataJsonOpts);
            object obj = JsonSerializer.Deserialize<Dictionary<string, object>>(enc);
            d["data"] = obj;
        }
        catch { }
        return d;
    }
    public virtual void load(Dictionary<string, object> d)
    {
        setPos(dnum(dget(d, "x")), dnum(dget(d, "y")), dnum(dget(d, "z")));
        vx = dnum(dget(d, "vx")); vy = dnum(dget(d, "vy")); vz = dnum(dget(d, "vz"));
        yaw = dnum(dget(d, "yaw")); pitch = dnum(dget(d, "pitch"));
        age = inum(dget(d, "age")); fireTicks = inum(dget(d, "fire"));
        object raw = dget(d, "data");
        EntityData decoded = null;
        if (raw != null)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
                decoded = JsonSerializer.Deserialize<EntityData>(bytes, dataJsonOpts);
            }
            catch { decoded = null; }
        }
        data = decoded ?? new EntityData();
        persistent = (dget(d, "persistent") is bool b) ? b : false;
    }

    private static object dget(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v : null;
}

// Boats (with chest variants) and minecarts (with chest/hopper/TNT/furnace)
// including rail physics
//
// Defines static class `VehiclesGlobals` (openContainerScreenFn / bindOpenContainerScreen
// and the private RAIL_DIRS table). External by name: Entity, LivingEntity, ItemEntity,
// World, ItemStack, iid, itemDef, spawnItem, explodeFn, gameRng, ifloor, inum — all
// defined in other layers of the same assembly. detSin/detCos/detAtan2/detRound/clampD
// come from DetMath/MathX (global using static); B/blockDefs/SHAPE_OF from Reg/BlockCaches.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PebbleCore;

public sealed class Boat : Entity
{
    public override string type => "boat";
    public string wood = "oak";
    public bool hasChest = false;
    public List<ItemStack> chestItems = Enumerable.Repeat<ItemStack>(null, 27).ToList();
    public double paddleAnim = 0.0;

    public Boat(World world) : base(world)
    {
        width = 1.375;
        height = 0.5625;
    }

    public override void tick()
    {
        baseTick();
        bool inWaterNow = inWater;
        // buoyancy
        double? waterTop = waterSurface();
        if (waterTop != null)
        {
            double depth = waterTop.Value - y;
            if (depth > 0.1) { vy += 0.06; }
            else if (depth > 0) { vy += depth * 0.5 + 0.005; }
            vy *= 0.7;
        }
        else if (!onGround)
        {
            vy -= 0.04;
        }
        // rider control
        if (passengers.Count > 0 && passengers[0] is LivingEntity rider && rider.isPlayer)
        {
            yaw += rider.moveStrafe * -0.05;
            double f = rider.moveForward;
            if (f != 0)
            {
                double sp = inWaterNow ? 0.04 : (onGround ? 0.008 : 0.02);
                vx += -detSin(yaw) * f * sp;
                vz += detCos(yaw) * f * sp;
                paddleAnim += 0.3;
            }
        }
        move(vx, vy, vz);
        double drag = inWaterNow ? 0.94 : onGround ? 0.6 : 0.96;
        vx *= drag; vz *= drag;
        if (onGround && !inWaterNow) { vx *= 0.6; vz *= 0.6; }
    }

    // move() zeroes fallDistance on touchdown, so the fall check must hook
    // onLand (a post-move check always saw 0 and boats never broke)
    public override void onLand(double fallDistance)
    {
        if (fallDistance > 3) { breakBoat(); }
    }

    private double? waterSurface()
    {
        int bx = ifloor(x), bz = ifloor(z);
        int dy = 1;
        while (dy >= -1)
        {
            int by = ifloor(y) + dy;
            int c = world.getBlock(bx, by, bz);
            if ((c >> 4) == (int)B.water)
            {
                return (double)by + world.fluidHeight(bx, by, bz);
            }
            dy -= 1;
        }
        return null;
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (dead) { return false; }
        breakBoat();
        return true;
    }

    private void breakBoat()
    {
        string itemName = hasChest
            ? (wood == "bamboo" ? "bamboo_chest_raft" : wood + "_chest_boat")
            : (wood == "bamboo" ? "bamboo_raft" : wood + "_boat");
        spawnItem(world, x, y + 0.5, z, new ItemStack(iid(itemName), 1));
        if (hasChest)
        {
            foreach (var s in chestItems) { if (s != null) { spawnItem(world, x, y + 0.5, z, s); } }
        }
        remove();
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        if (hasChest && ((player as LivingEntity)?.sneaking ?? false))
        {
            openContainerScreenFn?.Invoke(player, "boat_chest", this);
            return true;
        }
        if (passengers.Count < (hasChest ? 1 : 2))
        {
            player.mount(this);
            return true;
        }
        return false;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["wood"] = wood;
        d["hasChest"] = hasChest;
        try
        {
            byte[] enc = JsonSerializer.SerializeToUtf8Bytes(chestItems, encJsonOpts);
            object obj = JsonSerializer.Deserialize<object>(enc);
            d["chestItems"] = obj;
        }
        catch { }
        return d;
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        wood = (d.TryGetValue("wood", out var w) && w is string ws) ? ws : "oak";
        hasChest = (d.TryGetValue("hasChest", out var hc) && hc is bool hcb) ? hcb : false;
        List<ItemStack> decoded = null;
        if (d.TryGetValue("chestItems", out var raw) && raw != null)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
                decoded = JsonSerializer.Deserialize<List<ItemStack>>(bytes, encJsonOpts);
            }
            catch { decoded = null; }
        }
        chestItems = decoded ?? Enumerable.Repeat<ItemStack>(null, 27).ToList();
    }

    // Swift's synthesized Codable round-trips [ItemStack?] through JSON; mirror with
    // System.Text.Json, omitting nil keys so the saved blob matches the baseline.
    private static readonly JsonSerializerOptions encJsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };
}

public sealed class Minecart : Entity
{
    public override string type => "minecart";
    public string variant = "empty";   // empty | chest | hopper | tnt | furnace
    public List<ItemStack> chestItems = Enumerable.Repeat<ItemStack>(null, 27).ToList();
    public int fuel = 0;
    public int tntFuse = -1;

    public Minecart(World world) : base(world)
    {
        width = 0.98;
        height = 0.7;
    }

    private int railAt(int x, int y, int z)
    {
        int c = world.getBlock(x, y, z);
        int bid = c >> 4;
        if (bid >= 0 && bid < SHAPE_OF.Length && SHAPE_OF[bid] == (byte)Shape.rail) { return c; }
        return -1;
    }

    public override void tick()
    {
        baseTick();
        if (tntFuse >= 0)
        {
            tntFuse -= 1;
            if (age % 4 == 0) { world.hooks.addParticles("smoke", x, y + 0.8, z, 1, 0.1, 0); }
            if (tntFuse <= 0)
            {
                remove();
                explodeFn?.Invoke(world, x, y, z, 4 + gameRng.nextFloat() * 1.5, false, this);
                return;
            }
        }
        int bx = ifloor(x), bz = ifloor(z);
        int by = ifloor(y);
        int rail = railAt(bx, by, bz);
        if (rail == -1) { rail = railAt(bx, by - 1, bz); }
        if (rail != -1)
        {
            by = ifloor(y);
            if (railAt(bx, by, bz) == -1) { by -= 1; }
            tickOnRail(bx, by, bz, rail);
        }
        else
        {
            // off rail
            vy -= 0.04;
            move(vx, vy, vz);
            vx *= onGround ? 0.5 : 0.95;
            vz *= onGround ? 0.5 : 0.95;
        }
        // rider input minor push
        if (passengers.Count > 0 && passengers[0] is LivingEntity rider && rider.isPlayer && rider.moveForward != 0 && rail != -1)
        {
            double sp = Math.Sqrt(vx * vx + vz * vz);
            if (sp < 0.01)
            {
                vx += -detSin(rider.yaw) * 0.02;
                vz += detCos(rider.yaw) * 0.02;
            }
        }
        // hopper cart: pick up items
        if (variant == "hopper" && age % 4 == 0)
        {
            foreach (var e in world.getEntitiesNear(x, y, z, 1.2, e => (e as Entity)?.type == "item"))
            {
                if (!(e is ItemEntity item)) { continue; }
                if (item.pickupDelay > 0) { continue; }
                for (int i = 0; i < chestItems.Count; i++)
                {
                    if (chestItems[i] == null) { chestItems[i] = item.stack; item.remove(); break; }
                }
            }
        }
    }

    private void tickOnRail(int bx, int by, int bz, int rail)
    {
        int bid = rail >> 4;
        int meta = rail & 15;
        int shape = bid == (int)B.rail ? meta : (meta & 7);
        bool powered = bid == (int)B.powered_rail && (meta & 8) != 0;
        var dirs = RAIL_DIRS.TryGetValue(shape, out var dd) ? dd : RAIL_DIRS[0];
        // gravity on slopes
        if (shape >= 2 && shape <= 5)
        {
            double downX = shape == 2 ? -1 : shape == 3 ? 1 : 0;
            double downZ = shape == 4 ? 1 : shape == 5 ? -1 : 0;
            vx += downX * 0.0078;
            vz += downZ * 0.0078;
        }
        // snap velocity to rail axis
        double axisX = (double)(dirs.Item3 - dirs.Item1), axisZ = (double)(dirs.Item4 - dirs.Item2);
        double alen = Math.Sqrt(axisX * axisX + axisZ * axisZ);
        if (alen == 0) { alen = 1; }
        double ax = axisX / alen, az = axisZ / alen;
        double speed = vx * ax + vz * az;
        // friction / boost
        if (bid == (int)B.powered_rail)
        {
            if (powered)
            {
                speed = speed == 0 ? 0 : (speed > 0 ? 1.0 : -1.0) * Math.Min(0.6, Math.Abs(speed) + 0.06);
                if (Math.Abs(speed) < 0.03)
                {
                    // launch from solid block behind
                    int behind = world.getBlock(bx - (int)detRound(ax), by, bz - (int)detRound(az));
                    if ((behind >> 4) != 0 && blockDefs[behind >> 4].solid) { speed = 0.3; }
                    int front = world.getBlock(bx + (int)detRound(ax), by, bz + (int)detRound(az));
                    if ((front >> 4) != 0 && blockDefs[front >> 4].solid) { speed = -0.3; }
                }
            }
            else
            {
                speed *= 0.6; // unpowered = brake
            }
        }
        else
        {
            speed *= passengers.Count == 0 ? 0.96 : 0.997;
        }
        if (variant == "furnace" && fuel > 0)
        {
            fuel -= 1;
            double sgn = (speed != 0 ? speed : 1) > 0 ? 1 : -1;
            speed = sgn * Math.Min(0.4, Math.Abs(speed) + 0.02);
            if (age % 20 == 0) { world.hooks.addParticles("smoke", x, y + 0.9, z, 1, 0.1, 0); }
        }
        speed = clampD(speed, -0.6, 0.6);
        vx = ax * speed;
        vz = az * speed;
        // center on rail
        double cx = (double)bx + 0.5, cz = (double)bz + 0.5;
        if (shape == 0) { x += (cx - x) * 0.3; }
        if (shape == 1) { z += (cz - z) * 0.3; }
        // curves: redirect
        if (shape >= 6)
        {
            x += (cx - x) * 0.2;
            z += (cz - z) * 0.2;
        }
        // slope height
        if (shape >= 2 && shape <= 5)
        {
            double fx = x - (double)bx, fz = z - (double)bz;
            double h = 0.0;
            if (shape == 2) { h = fx; }
            else if (shape == 3) { h = 1 - fx; }
            else if (shape == 4) { h = 1 - fz; }
            else if (shape == 5) { h = fz; }
            y = (double)by + h + 0.0625 + 0.2;
            vy = 0;
        }
        else
        {
            y = (double)by + 0.0625 + 0.2;
            vy = 0;
        }
        x += vx;
        z += vz;
        onGround = true;
        if (Math.Abs(vx) > 0.001 || Math.Abs(vz) > 0.001)
        {
            yaw = detAtan2(-vx, vz) + Math.PI / 2;
        }
        // detector rail trigger handled by redstone system scanning carts
        // activator rail: eject / tnt ignite
        int below = world.getBlock(bx, by, bz);
        if ((below >> 4) == (int)B.activator_rail && (below & 8) != 0)
        {
            if (variant == "tnt" && tntFuse < 0)
            {
                tntFuse = 80;
                world.hooks.playSound("entity.tnt.primed", x, y, z, 1, 1);
            }
            else if (passengers.Count != 0)
            {
                foreach (var p in passengers) { p.dismount(); }
            }
        }
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (dead) { return false; }
        if (variant == "tnt" && (source == "fire" || source == "explosion"))
        {
            tntFuse = 10;
            return true;
        }
        string itemName = variant == "empty" ? "minecart" : $"{variant}_minecart";
        spawnItem(world, x, y, z, new ItemStack(iid(itemName), 1));
        if (variant == "chest" || variant == "hopper")
        {
            foreach (var s in chestItems) { if (s != null) { spawnItem(world, x, y, z, s); } }
        }
        remove();
        return true;
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        if (variant == "chest" || variant == "hopper")
        {
            openContainerScreenFn?.Invoke(player, "minecart_chest", this);
            return true;
        }
        if (variant == "furnace")
        {
            string name = stack != null ? itemDef(stack.id).name : null;
            if (name == "coal" || name == "charcoal")
            {
                fuel += 3600;
                (player as LivingEntity)?.consumeHeld(1);
                return true;
            }
            return false;
        }
        if (variant == "empty" && passengers.Count == 0)
        {
            player.mount(this);
            return true;
        }
        return false;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["variant"] = variant;
        try
        {
            byte[] enc = JsonSerializer.SerializeToUtf8Bytes(chestItems, encJsonOpts);
            object obj = JsonSerializer.Deserialize<object>(enc);
            d["chestItems"] = obj;
        }
        catch { }
        d["fuel"] = fuel;
        return d;
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        variant = (d.TryGetValue("variant", out var v) && v is string vs) ? vs : "empty";
        List<ItemStack> decoded = null;
        if (d.TryGetValue("chestItems", out var raw) && raw != null)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
                decoded = JsonSerializer.Deserialize<List<ItemStack>>(bytes, encJsonOpts);
            }
            catch { decoded = null; }
        }
        chestItems = decoded ?? Enumerable.Repeat<ItemStack>(null, 27).ToList();
        fuel = inum(d.TryGetValue("fuel", out var f) ? f : null);
    }

    // Swift's synthesized Codable round-trips [ItemStack?] through JSON; mirror with
    // System.Text.Json, omitting nil keys so the saved blob matches the baseline.
    private static readonly JsonSerializerOptions encJsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };
}

/// module-level free funcs/vars of Vehicles.swift: late-bound container UI hook and
/// the rail-direction table. Surface via `global using static PebbleCore.VehiclesGlobals`.
public static class VehiclesGlobals
{
    /// late-bound container UI hook (player.openContainerScreen in baseline)
    public static Action<Entity, string, Entity> openContainerScreenFn;
    public static void bindOpenContainerScreen(Action<Entity, string, Entity> fn) { openContainerScreenFn = fn; }

    // rails ------------------------------------------------------------------
    // shape → (dx0, dz0, dx1, dz1) (the two connected directions)
    internal static readonly Dictionary<int, (int, int, int, int)> RAIL_DIRS = new()
    {
        { 0, (0, -1, 0, 1) },   // NS
        { 1, (-1, 0, 1, 0) },   // EW
        { 2, (-1, 0, 1, 0) },   // asc E
        { 3, (-1, 0, 1, 0) },   // asc W
        { 4, (0, -1, 0, 1) },   // asc N
        { 5, (0, -1, 0, 1) },   // asc S
        { 6, (0, 1, 1, 0) },    // SE curve
        { 7, (0, 1, -1, 0) },   // SW
        { 8, (0, -1, -1, 0) },  // NW
        { 9, (0, -1, 1, 0) },   // NE
    };
}

// The player — inventory, hunger/saturation, XP,
// mining state, abilities, elytra flight, sleeping, ender chest, and
// interaction helpers.
//
// Defines static class: PlayerGlobals (PLAYER_HEIGHT/PLAYER_SNEAK_HEIGHT/PLAYER_EYE/
// PLAYER_SNEAK_EYE module-level constants) — surface via
// `global using static PebbleCore.PlayerGlobals`.
//
// GameMode is a small namespace of two Int constants (Swift `enum GameMode` with
// `static let survival/creative`); kept as a static class with `const int` members,
// referenced as GameMode.survival / GameMode.creative (no global using needed — it
// is its own type name).
//
// All other module symbols resolve from the rest of the assembly:
//   - free funcs spawnItem / spawnXP / prettyEntityName (Misc.swift / GameCore.swift)
//   - gameRng / detSin / detCos / clampD (DetMath / MathX)
//   - itemDef / itemDefs / enchLevel / canMerge / maxStackOf / iid / itemExists (ItemGlobals)
//   - ifloor (EntityGlobals); B / blockDefs (Reg)
//   - types ItemStack / ItemEntity / XPOrb / ActiveEffect / World / Entity / LivingEntity

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PebbleCore;

public static class PlayerGlobals
{
    public const double PLAYER_HEIGHT = 1.8;
    public const double PLAYER_SNEAK_HEIGHT = 1.5;
    public const double PLAYER_EYE = 1.62;
    public const double PLAYER_SNEAK_EYE = 1.27;
}

public static class GameMode
{
    public const int survival = 0;
    public const int creative = 1;
}

public class Player : LivingEntity
{
    public override string type => "player";
    public override bool isPlayer => true;
    private int _gameMode = GameMode.survival;
    public override int gameMode => _gameMode;
    public void setGameMode(int m) { _gameMode = m; }
    /// inventory: 0-8 hotbar, 9-35 main
    public List<ItemStack> inventory = Enumerable.Repeat<ItemStack>(null, 36).ToList();
    public List<ItemStack> enderChest = Enumerable.Repeat<ItemStack>(null, 27).ToList();
    public int selectedSlot = 0;
    public int hunger = 20;
    public double saturation = 5.0;
    public double exhaustion = 0.0;
    public int foodTickTimer = 0;
    public int xp = 0;          // total points
    public int xpLevel = 0;
    public double xpProgress = 0.0;  // 0..1
    public bool flying = false;
    public bool elytraFlying = false;
    public int sleepTicks = 0;
    public (int, int, int)? bedPos = null;
    public (int, int, int)? spawnPoint = null;
    public int spawnDim = 0;
    public bool spawnForced = false;
    /// mining state
    public int breakingX = 0, breakingY = 0, breakingZ = 0;
    public double breakingProgress = -1.0;
    public double attackStrengthTicker = 100.0;
    public bool usingItem = false;
    public int useItemTicks = 0;
    public string useItemHand = "main";   // main | off
    public int? fishingBobberId = null;
    private bool _wearingPumpkin = false;
    public override bool wearingPumpkin => _wearingPumpkin;
    /// stats for advancements
    public Dictionary<string, double> stats = new Dictionary<string, double>();
    public int portalTicks = 0;
    public string insidePortalKind = null;   // nether | end | nil

    public Player(World world) : base(world)
    {
        width = 0.6;
        height = PlayerGlobals.PLAYER_HEIGHT;
        maxHealth = 20;
        health = 20;
        speed = 0.1;
        persistent = true;
        stepHeight = 0.6;
    }

    public ItemStack mainHandStack => inventory[selectedSlot];
    public override ItemStack mainHand
    {
        get { return inventory[selectedSlot]; }
        set { inventory[selectedSlot] = value; }
    }

    public override double eyeY()
    {
        return y + (sneaking ? PlayerGlobals.PLAYER_SNEAK_EYE : PlayerGlobals.PLAYER_EYE);
    }

    public override void tick()
    {
        height = sneaking ? PlayerGlobals.PLAYER_SNEAK_HEIGHT : PlayerGlobals.PLAYER_HEIGHT;
        baseLivingTick();
        if (dead) { return; }
        _wearingPumpkin = armor[0] != null && itemDef(armor[0].id).name == "carved_pumpkin";
        if (attackStrengthTicker < 100) { attackStrengthTicker += attackSpeedPerTick(); }
        if (gameMode == GameMode.creative)
        {
            hunger = 20;
            airSupply = 300;
            fireTicks = Math.Min(fireTicks, 0);
        }
        else
        {
            tickHunger();
        }
        if (sleepTicks > 0) { sleepTicks += 1; }
        if (portalTicks > 0 && insidePortalKind == null) { portalTicks = Math.Max(0, portalTicks - 4); }
        // item magnet pickup
        if (age % 2 == 0 && !dead)
        {
            foreach (var e in world.getEntitiesNear(x, y + 0.5, z, 1.6))
            {
                if (e is ItemEntity item && item.pickupDelay <= 0)
                {
                    int before = item.stack.count;
                    if (give(item.stack))
                    {
                        world.hooks.playSound("entity.item.pickup", x, y, z, 0.3, 1.4 + System.Random.Shared.NextDouble() * 0.6);
                        item.remove();
                    }
                    else if (item.stack.count != before)
                    {
                        world.hooks.playSound("entity.item.pickup", x, y, z, 0.3, 1.4);
                    }
                }
                else if (e is XPOrb orb)
                {
                    addXP(orb.amount);
                    world.hooks.playSound("entity.experience_orb.pickup", x, y, z, 0.4, 0.8 + System.Random.Shared.NextDouble() * 0.6);
                    orb.remove();
                }
            }
        }
        // elytra
        if (elytraFlying)
        {
            tickElytra();
            if (onGround || inWater || !hasElytra()) { elytraFlying = false; }
        }
    }

    // =========================================================================
    // Vanilla-exact movement (Java 1.20). Overrides the shared LivingEntity
    // travel(), which mobs keep (their physics is golden-locked). References:
    // LocalPlayer.aiStep input shaping, LivingEntity.travel/handleRelative-
    // FrictionAndCalculateMovement, Entity.getBlockSpeedFactor.
    // =========================================================================
    public int noJumpDelay = 0;

    /// speed attribute chain: base 0.1, sprint ×1.3, speed/slowness effects
    private double vanillaSpeed()
    {
        double s = 0.1;
        if (sprinting) { s *= 1.3; }
        s *= 1 + 0.2 * (double)effectLevel("speed");
        s *= Math.Max(0, 1 - 0.15 * (double)effectLevel("slowness"));
        return s;
    }

    private double slipperiness(int id)
    {
        if (id == (int)B.ice || id == (int)B.packed_ice || id == (int)B.frosted_ice) { return 0.98; }
        if (id == (int)B.blue_ice) { return 0.989; }
        if (id == (int)B.slime_block) { return 0.8; }
        return 0.6;
    }

    private void moveRelativeP(double f, double s, double accel)
    {
        double ff = f, ss = s;
        double lenSq = ff * ff + ss * ss;
        if (lenSq < 1e-7) { return; }
        if (lenSq > 1)
        {
            double len = Math.Sqrt(lenSq);
            ff /= len;
            ss /= len;
        }
        double sn = detSin(yaw), cs = detCos(yaw);
        vx += (ss * cs - ff * sn) * accel;
        vz += (ff * cs + ss * sn) * accel;
    }

    private bool wouldCollide(double dx, double dy, double dz)
    {
        var b = bb();
        var test = new AABB(b.x0 + dx, b.y0 + dy, b.z0 + dz, b.x1 + dx, b.y1 + dy, b.z1 + dz);
        bool hit = false;
        world.forEachCollisionBox(test, box =>
        {
            if (box.intersects(test)) { hit = true; }
        });
        return hit;
    }

    private void jumpFromGround()
    {
        // honey reduces jump height (block at feet or below)
        int at = world.getBlockId(ifloor(x), ifloor(y), ifloor(z));
        int below = world.getBlockId(ifloor(x), ifloor(y - 0.5000001), ifloor(z));
        double jumpFactor = (at == (int)B.honey_block || below == (int)B.honey_block) ? 0.5 : 1.0;
        vy = 0.42 * jumpFactor + (double)effectLevel("jump_boost") * 0.1;
        if (sprinting)
        {
            vx += -detSin(yaw) * 0.2;
            vz += detCos(yaw) * 0.2;
        }
        onGround = false;
        addExhaustion(sprinting ? 0.2 : 0.05);
    }

    public override void travel()
    {
        // ---- input shaping (LocalPlayer.aiStep) ----
        double f = moveForward, s = moveStrafe;
        if (sneaking)
        {
            // swift sneak raises the 0.3 sneak multiplier toward 1.0
            int swift = armor[2] != null ? enchLevel(armor[2], "swift_sneak") : 0;
            double mult = clampD(0.3 + 0.15 * (double)swift, 0, 1);
            f *= mult;
            s *= mult;
        }
        f *= 0.98;
        s *= 0.98;

        // ---- jumping (before physics, 10-tick re-jump delay while held) ----
        if (noJumpDelay > 0) { noJumpDelay -= 1; }
        bool fluid = inWater || inLava;
        if (jumping)
        {
            if (fluid)
            {
                vy += 0.04;
            }
            else if (onGround && noJumpDelay == 0)
            {
                jumpFromGround();
                noJumpDelay = 10;
            }
        }
        else
        {
            noJumpDelay = 0;
        }

        if (inWater)
        {
            // ---- water (LivingEntity.travel water branch) ----
            double yBefore = y;
            double waterSlow = sprinting ? 0.9 : 0.8;   // vanilla sprint-swim inertia
            double speed = 0.02;
            double ds = (double)(armor[3] != null ? enchLevel(armor[3], "depth_strider") : 0);
            ds = Math.Min(3, ds);
            if (!onGround) { ds *= 0.5; }
            if (ds > 0)
            {
                waterSlow += (0.54600006 - waterSlow) * ds / 3;
                speed += (vanillaSpeed() - speed) * ds / 3;
            }
            if (hasEffect("dolphins_grace")) { waterSlow = 0.96; }
            moveRelativeP(f, s, speed);
            move(vx, vy, vz);
            vx *= waterSlow;
            vy *= 0.8;
            vz *= waterSlow;
            // fluid-falling gravity: 0.08/16 with the vanilla epsilon snap
            if (!noGravity && !sprinting)
            {
                bool falling = vy <= 0;
                if (falling && Math.Abs(vy - 0.005) >= 0.003 && Math.Abs(vy - 0.08 / 16) < 0.003)
                {
                    vy = -0.003;
                }
                else
                {
                    vy -= 0.08 / 16;
                }
            }
            // hop out at edges
            if (horizontalCollision && !wouldCollide(vx, vy + 0.6 - y + yBefore, vz))
            {
                vy = 0.3;
            }
        }
        else if (inLava)
        {
            // ---- lava ----
            double yBefore = y;
            moveRelativeP(f, s, 0.02);
            move(vx, vy, vz);
            vx *= 0.5;
            vy *= 0.5;
            vz *= 0.5;
            if (!noGravity)
            {
                bool falling = vy <= 0;
                if (falling && Math.Abs(vy - 0.005) >= 0.003 && Math.Abs(vy - 0.08 / 16) < 0.003)
                {
                    vy = -0.003;
                }
                else
                {
                    vy -= 0.08 / 16;
                }
            }
            if (horizontalCollision && !wouldCollide(vx, vy + 0.6 - y + yBefore, vz))
            {
                vy = 0.3;
            }
        }
        else
        {
            // ---- land / air ----
            int below = world.getBlockId(ifloor(x), ifloor(y - 0.5000001), ifloor(z));
            double slip = slipperiness(below);
            double friction = onGround ? slip * 0.91 : 0.91;
            // 0.216/slip³ with the ×0.98 input gives the exact vanilla speeds:
            // walk 4.3172, sprint 5.6114, sneak 1.295 blocks/s at equilibrium
            double accel = onGround
                ? vanillaSpeed() * 0.21600002 / (slip * slip * slip)
                : 0.02 + (sprinting ? 0.006 : 0);
            moveRelativeP(f, s, accel);
            // climbing clamps (before move)
            if (isClimbing())
            {
                vx = clampD(vx, -0.15, 0.15);
                vz = clampD(vz, -0.15, 0.15);
                if (vy < -0.15) { vy = -0.15; }
                if (sneaking && vy < 0) { vy = 0; }
            }
            move(vx, vy, vz);
            // pushing into a climbable (or jumping on it) climbs
            if ((horizontalCollision || jumping) && isClimbing())
            {
                vy = 0.2;
            }
            // gravity + drag
            if (hasEffect("levitation"))
            {
                vy += (0.05 * (double)(effectLevel("levitation") + 1) - vy) * 0.2;
                fallDistance = 0;
            }
            else if (!noGravity)
            {
                double g = 0.08 * gravityScale;
                if (hasEffect("slow_falling") && vy <= 0)
                {
                    g = 0.01;
                    fallDistance = 0;
                }
                vy -= g;
            }
            vy *= 0.98;
            vx *= friction;
            vz *= friction;
            // soul sand velocity factor (honey is damped inside move())
            int at = world.getBlockId(ifloor(x), ifloor(y), ifloor(z));
            int under = at == 0 ? world.getBlockId(ifloor(x), ifloor(y - 0.5000001), ifloor(z)) : at;
            if (under == (int)B.soul_sand)
            {
                int soulSpeed = armor[3] != null ? enchLevel(armor[3], "soul_speed") : 0;
                if (soulSpeed == 0)
                {
                    vx *= 0.4;
                    vz *= 0.4;
                }
            }
        }

        // limb animation (matches LivingEntity)
        double dxm = x - prevX, dzm = z - prevZ;
        double moved = Math.Min(1, Math.Sqrt(dxm * dxm + dzm * dzm) * 4);
        limbAmp += (moved - limbAmp) * 0.4;
        limbSwing += limbAmp * 1.2;
    }

    /// vanilla maybeBackOffFromEdge — sneaking on the ground never walks off
    public override void move(double dxIn, double dyIn, double dzIn)
    {
        double dx = dxIn, dz = dzIn;
        if (sneaking && onGround && dyIn <= 0 && !flying)
        {
            double step = 0.05;
            while (dx != 0 && !wouldCollide(dx, -maxUpStep(), 0))
            {
                dx = Math.Abs(dx) <= step ? 0 : dx - (dx > 0 ? step : -step);
            }
            while (dz != 0 && !wouldCollide(0, -maxUpStep(), dz))
            {
                dz = Math.Abs(dz) <= step ? 0 : dz - (dz > 0 ? step : -step);
            }
            while (dx != 0 && dz != 0 && !wouldCollide(dx, -maxUpStep(), dz))
            {
                dx = Math.Abs(dx) <= step ? 0 : dx - (dx > 0 ? step : -step);
                dz = Math.Abs(dz) <= step ? 0 : dz - (dz > 0 ? step : -step);
            }
        }
        base.move(dx, dyIn, dz);
    }
    private double maxUpStep() { return stepHeight; }

    private double attackSpeedPerTick()
    {
        ToolDef? t = mainHand != null ? itemDef(mainHand.id).tool : null;
        double speed = t?.attackSpeed ?? 4;
        return speed * 5; // reaches 100 in 20/speed ticks
    }
    public double attackStrength()
    {
        return clampD(attackStrengthTicker / 100, 0, 1);
    }
    public void resetAttackCooldown()
    {
        attackStrengthTicker = 0;
    }

    public bool hasElytra()
    {
        var chest = armor[1];
        if (chest == null || itemDef(chest.id).name != "elytra") { return false; }
        return chest.damage < (itemDef(chest.id).armor?.durability ?? 432) - 1;
    }
    public bool startElytra()
    {
        if (!onGround && !elytraFlying && !inWater && hasElytra())
        {
            elytraFlying = true;
            return true;
        }
        return false;
    }
    private void tickElytra()
    {
        // vanilla elytra physics
        double pitch = this.pitch;
        double lookX = -detSin(yaw) * detCos(pitch);
        double lookZ = detCos(yaw) * detCos(pitch);
        double hLook = Math.Sqrt(lookX * lookX + lookZ * lookZ);
        double hVel = Math.Sqrt(vx * vx + vz * vz);
        double cosP = detCos(pitch);
        double cosP2 = cosP * cosP;
        double g = hasEffect("slow_falling") && vy <= 0 ? 0.01 : 0.08;
        vy += g * (-1 + cosP2 * 0.75);
        if (vy < 0 && hLook > 0)
        {
            double lift = vy * -0.1 * cosP2;
            vx += lookX / hLook * lift;
            vy += lift;
            vz += lookZ / hLook * lift;
        }
        if (pitch < 0 && hLook > 0)
        {
            // vanilla scales only the VERTICAL component by 3.2
            double pull = hVel * -detSin(pitch) * 0.04;
            vx += -lookX / hLook * pull;
            vy += pull * 3.2;
            vz += -lookZ / hLook * pull;
        }
        if (hLook > 0)
        {
            vx += (lookX / hLook * hVel - vx) * 0.1;
            vz += (lookZ / hLook * hVel - vz) * 0.1;
        }
        vx *= 0.99;
        vy *= 0.98;
        vz *= 0.99;
        // durability
        if (age % 20 == 0 && armor[1] != null)
        {
            damageStack(armor[1], 1);
        }
        // wall smack damage
        if (horizontalCollision)
        {
            double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            double dmg = speed * 10 - 3;
            if (dmg > 0) { hurt(dmg, "fly_into_wall"); }
        }
    }

    // ---- hunger ------------------------------------------------------------
    public override void addExhaustion(double amount)
    {
        if (gameMode == GameMode.creative) { return; }
        exhaustion += amount;
        while (exhaustion >= 4)
        {
            exhaustion -= 4;
            if (saturation > 0) { saturation = Math.Max(0, saturation - 1); }
            else { hunger = Math.Max(0, hunger - 1); }
        }
    }
    public override void feed(int hungerIn, double saturationIn)
    {
        hunger = Math.Min(20, hunger + hungerIn);
        saturation = Math.Min((double)hunger, saturation + saturationIn);
    }
    private void tickHunger()
    {
        foodTickTimer += 1;
        if (hunger >= 18 && health < maxHealth && world.rule("naturalRegeneration"))
        {
            bool fast = hunger >= 20 && saturation > 0;
            if (foodTickTimer >= (fast ? 10 : 80))
            {
                heal(1);
                addExhaustion(fast ? 6 : 6);
                foodTickTimer = 0;
            }
        }
        else if (hunger <= 0)
        {
            if (foodTickTimer >= 80)
            {
                int diff = world.difficulty;
                if (health > (diff >= 3 ? 0 : diff == 2 ? 1 : 10)) { hurt(1, "starve"); }
                foodTickTimer = 0;
            }
        }
        else if (foodTickTimer > 80)
        {
            foodTickTimer = 0;
        }
        // sprint exhaustion handled in movement; swimming:
        if (inWater && (Math.Abs(x - prevX) > 0.01 || Math.Abs(z - prevZ) > 0.01))
        {
            double dx = x - prevX, dz = z - prevZ;
            addExhaustion(0.01 * Math.Sqrt(dx * dx + dz * dz) * 5);
        }
        else if (sprinting && onGround)
        {
            double dx = x - prevX, dz = z - prevZ;
            addExhaustion(0.1 * Math.Sqrt(dx * dx + dz * dz));
        }
    }

    // ---- XP ------------------------------------------------------------------
    public int xpForLevel(int level)
    {
        if (level >= 30) { return 112 + (level - 30) * 9; }
        if (level >= 15) { return 37 + (level - 15) * 5; }
        return 7 + level * 2;
    }
    public void addXP(int pointsIn)
    {
        int points = pointsIn;
        // mending first
        if (points > 0)
        {
            var mendables = new List<ItemStack>();
            var candidates = new List<ItemStack> { mainHand, offHand };
            candidates.AddRange(armor);
            foreach (var s in candidates)
            {
                if (s != null && enchLevel(s, "mending") > 0 && s.damage > 0) { mendables.Add(s); }
            }
            if (mendables.Count != 0)
            {
                var s = mendables[gameRng.nextInt(mendables.Count)];
                int repair = Math.Min(s.damage, points * 2);
                s.damage -= repair;
                points -= (int)Math.Ceiling((double)repair / 2);
            }
        }
        xp += points;
        double need = (double)xpForLevel(xpLevel);
        double cur = xpProgress * need + (double)points;
        while (cur >= need)
        {
            cur -= need;
            xpLevel += 1;
            need = (double)xpForLevel(xpLevel);
            world.hooks.playSound("entity.player.levelup", x, y, z, 0.7, 1);
        }
        while (cur < 0 && xpLevel > 0)
        {
            xpLevel -= 1;
            need = (double)xpForLevel(xpLevel);
            cur += need;
        }
        xpProgress = Math.Max(0, cur / need);
    }
    public void takeLevels(int levels)
    {
        xpLevel = Math.Max(0, xpLevel - levels);
        xpProgress = 0;
    }

    // ---- inventory -------------------------------------------------------------
    public override bool give(ItemStack stackIn)
    {
        if (stackIn == null) { return false; }
        var stack = stackIn;
        // merge into existing
        for (int i = 0; i < inventory.Count; i++)
        {
            if (stack.count <= 0) { continue; }
            var s = inventory[i];
            if (s != null && canMerge(s, stack))
            {
                int space = maxStackOf(s) - s.count;
                int take = Math.Min(space, stack.count);
                s.count += take;
                stack.count -= take;
            }
        }
        if (stack.count <= 0) { return true; }
        for (int i = 0; i < inventory.Count; i++)
        {
            if (inventory[i] == null)
            {
                inventory[i] = stack.copy();
                stack.count = 0;
                return true;
            }
        }
        return false;
    }
    public int countItem(int itemId)
    {
        int n = 0;
        foreach (var s in inventory) { if (s != null && s.id == itemId) { n += s.count; } }
        return n;
    }
    public bool removeItems(int itemId, int countIn)
    {
        if (countItem(itemId) < countIn) { return false; }
        int count = countIn;
        for (int i = 0; i < inventory.Count; i++)
        {
            if (count <= 0) { continue; }
            var s = inventory[i];
            if (s != null && s.id == itemId)
            {
                int take = Math.Min(s.count, count);
                s.count -= take;
                count -= take;
                if (s.count <= 0) { inventory[i] = null; }
            }
        }
        return true;
    }
    public override void consumeHeld(int n)
    {
        if (gameMode == GameMode.creative) { return; }
        var s = mainHand;
        if (s == null) { return; }
        s.count -= n;
        if (s.count <= 0) { mainHand = null; }
    }
    public override void replaceHeld(ItemStack stack)
    {
        var s = mainHand;
        if (gameMode == GameMode.creative)
        {
            if (countItem(stack.id) == 0) { give(stack); }
            return;
        }
        if (s != null && s.count > 1)
        {
            s.count -= 1;
            if (!give(stack)) { spawnItem(world, x, y, z, stack); }
        }
        else
        {
            mainHand = stack;
        }
    }
    public override void damageHeld(int amount)
    {
        var s = mainHand;
        if (s != null) { damageStack(s, amount); }
    }
    public void damageStack(ItemStack s, int amount)
    {
        if (gameMode == GameMode.creative) { return; }
        var def = itemDef(s.id);
        int maxD = def.tool?.durability ?? def.armor?.durability ?? 0;
        if (maxD <= 0) { return; }
        int unb = enchLevel(s, "unbreaking");
        for (int i = 0; i < amount; i++)
        {
            if (unb > 0 && gameRng.nextFloat() < (double)unb / (double)(unb + 1)) { continue; }
            s.damage += 1;
        }
        if (s.damage >= maxD)
        {
            // break
            int idx = inventory.FindIndex(o => ReferenceEquals(o, s));
            if (idx >= 0) { inventory[idx] = null; }
            int aIdx = Array.FindIndex(armor, o => ReferenceEquals(o, s));
            if (aIdx >= 0) { armor[aIdx] = null; }
            if (ReferenceEquals(offHand, s)) { offHand = null; }
            world.hooks.playSound("entity.item.break", x, y, z, 0.8, 1);
        }
    }
    public void dropSelected(bool all)
    {
        var s = mainHand;
        if (s == null) { return; }
        int count = all ? s.count : 1;
        var dropped = s.copy();
        dropped.count = count;
        s.count -= count;
        if (s.count <= 0) { mainHand = null; }
        var e = spawnItem(world, x, eyeY() - 0.3, z, dropped);
        e.vx = -detSin(yaw) * 0.3;
        e.vy = 0.1;
        e.vz = detCos(yaw) * 0.3;
        e.pickupDelay = 40;
    }

    // ---- combat / death -------------------------------------------------------
    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        if (gameMode == GameMode.creative && source != "void") { return false; }
        bool r = base.hurt(amount, source, attacker);
        if (r)
        {
            addExhaustion(0.1);
            sleepTicks = 0;
            bedPos = null;
        }
        return r;
    }
    public override void die(string source, Entity attacker = null)
    {
        // totem of undying
        for (int hand = 0; hand < 2; hand++)
        {
            var s = hand == 0 ? mainHand : offHand;
            if (s != null && itemDef(s.id).name == "totem_of_undying")
            {
                if (hand == 0) { mainHand = null; } else { offHand = null; }
                health = 1;
                clearEffects();
                addEffect("regeneration", 900, 1);
                addEffect("absorption", 100, 1);
                addEffect("fire_resistance", 800, 0);
                world.hooks.playSound("item.totem.use", x, y, z, 1, 1);
                world.hooks.addParticles("totem", x, y + 1, z, 60, 0.6, 0);
                return;
            }
        }
        health = 0;
        deathTime = 1;
        data.deathCause = source;
        data.deathAttacker = lastAttacker != null ? prettyEntityName(lastAttacker.type) : null;
        world.hooks.playSound("entity.player.death", x, y, z, 1, 1);
        if (!world.rule("keepInventory"))
        {
            for (int i = 0; i < inventory.Count; i++)
            {
                var s = inventory[i];
                if (s != null)
                {
                    spawnItem(world, x, y + 0.5, z, s);
                    inventory[i] = null;
                }
            }
            for (int i = 0; i < 4; i++)
            {
                var s = armor[i];
                if (s != null)
                {
                    spawnItem(world, x, y + 0.5, z, s);
                    armor[i] = null;
                }
            }
            if (offHand != null) { spawnItem(world, x, y + 0.5, z, offHand); offHand = null; }
            spawnXP(world, x, y, z, Math.Min(xpLevel * 7, 100));
            xpLevel = 0; xpProgress = 0; xp = 0;
        }
    }
    public override void tickDeath()
    {
        deathTime += 1;
        // Game shows death screen; respawn via respawn()
    }
    public void respawn()
    {
        dead = false;
        deathTime = 0;
        health = maxHealth;
        hunger = 20;
        saturation = 5;
        exhaustion = 0;
        fireTicks = 0;
        airSupply = 300;
        fallDistance = 0;
        clearEffects();
        vx = 0; vy = 0; vz = 0;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        Func<object, object> enc = (v) =>
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(v, encJsonOpts);
                return JsonSerializer.Deserialize<object>(bytes);
            }
            catch { return null; }
        };
        d["inventory"] = enc(inventory);
        d["enderChest"] = enc(enderChest);
        d["armor"] = enc(armor);
        if (offHand != null) { d["offHand"] = enc(offHand); }
        d["selectedSlot"] = selectedSlot;
        d["hunger"] = hunger;
        d["saturation"] = saturation;
        d["xpLevel"] = xpLevel;
        d["xpProgress"] = xpProgress;
        d["health"] = health;
        d["gameMode"] = gameMode;
        if (spawnPoint is (int, int, int) sp) { d["spawnPoint"] = new object[] { sp.Item1, sp.Item2, sp.Item3 }; }
        d["spawnDim"] = spawnDim;
        d["effects"] = enc(effects);
        d["stats"] = stats;
        return d;
    }
    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        T dec<T>(object raw)
        {
            if (raw == null) { return default; }
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
                return JsonSerializer.Deserialize<T>(bytes, encJsonOpts);
            }
            catch { return default; }
        }
        inventory = dec<List<ItemStack>>(dget(d, "inventory")) ?? Enumerable.Repeat<ItemStack>(null, 36).ToList();
        enderChest = dec<List<ItemStack>>(dget(d, "enderChest")) ?? Enumerable.Repeat<ItemStack>(null, 27).ToList();
        armor = dec<ItemStack[]>(dget(d, "armor")) ?? new ItemStack[] { null, null, null, null };
        offHand = dec<ItemStack>(dget(d, "offHand"));
        // harden against corrupt/truncated saves: fix array sizes, clamp the
        // hotbar slot, drop stacks with out-of-range item ids (itemDefs is
        // indexed unchecked in hot paths)
        while (inventory.Count < 36) { inventory.Add(null); }
        while (enderChest.Count < 27) { enderChest.Add(null); }
        if (armor.Length < 4)
        {
            var grown = new ItemStack[4];
            Array.Copy(armor, grown, armor.Length);
            armor = grown;
        }
        ushort nItems = (ushort)itemDefs.Count;
        Func<ItemStack, ItemStack> valid = (s) => (s?.id ?? 0) < (int)nItems ? s : null;
        for (int i = 0; i < inventory.Count; i++) { inventory[i] = valid(inventory[i]); }
        for (int i = 0; i < enderChest.Count; i++) { enderChest[i] = valid(enderChest[i]); }
        for (int i = 0; i < armor.Length; i++) { armor[i] = valid(armor[i]); }
        offHand = valid(offHand);
        selectedSlot = Math.Min(8, Math.Max(0, inum(dget(d, "selectedSlot"))));
        hunger = numOr(dget(d, "hunger"), 20);
        saturation = dnumOr(dget(d, "saturation"), 5);
        xpLevel = inum(dget(d, "xpLevel"));
        xpProgress = dnum(dget(d, "xpProgress"));
        health = dnumOr(dget(d, "health"), 20);
        _gameMode = inum(dget(d, "gameMode"));
        var spRaw = dec<List<int>>(dget(d, "spawnPoint"));
        if (spRaw != null && spRaw.Count == 3)
        {
            spawnPoint = (spRaw[0], spRaw[1], spRaw[2]);
        }
        else
        {
            spawnPoint = null;
        }
        spawnDim = inum(dget(d, "spawnDim"));
        stats = dec<Dictionary<string, double>>(dget(d, "stats")) ?? new Dictionary<string, double>();
        var fx = dec<List<ActiveEffect>>(dget(d, "effects"));
        if (fx != null)
        {
            foreach (var e in fx)
            {
                int i = effects.FindIndex(o => o.id == e.id);
                if (i >= 0) { effects[i] = e; }
                else { effects.Add(e); }
            }
        }
    }

    // Swift's synthesized Codable round-trips ItemStack/ActiveEffect through JSON;
    // mirror with System.Text.Json, omitting nil keys so the saved blob matches.
    private static readonly JsonSerializerOptions encJsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };

    private static object dget(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v : null;

    // (d[key] as? NSNumber)?.intValue ?? fallback  /  ?.doubleValue ?? fallback
    private static int numOr(object v, int fallback)
        => (v is IConvertible c && v is not string) ? Convert.ToInt32(c) : fallback;
    private static double dnumOr(object v, double fallback)
        => (v is IConvertible c && v is not string) ? Convert.ToDouble(c) : fallback;
}

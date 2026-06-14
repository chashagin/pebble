// Raids — triggered by Bad Omen near a village,
// waves of pillagers/vindicators/witches/ravagers/evokers, Hero of the
// Village on victory. Plus wandering pillager patrols.
//
// Ported from Sources/PebbleCore/Systems/Raids.swift.
//
// Defines static class `RaidsGlobals` (module-level free funcs/vars):
//   raidManager (the process-global RaidManager instance), tryPatrolSpawn.
//   (WAVES is module-private — kept private inside RaidsGlobals.)
// Surface via: global using static PebbleCore.RaidsGlobals;
//
// Namespace-level public types: Raid, RaidManager.
//
// External symbols (defined elsewhere in the assembly, referenced by name):
//   World, Player, Entity, LivingEntity, Mob, EntityRef   (World/GameWorld, Entity/*)
//   spawnMob, SpawnOpts                                    (Entity/EntityRegistry, Entity/SpawnHooks)
//   ifloor                                                 (Entity/Entity → EntityGlobals)
//   detCos, detSin                                         (Core/DetMath)
//   RandomX                                                (Core/RandomX)

using System;
using System.Collections.Generic;

namespace PebbleCore;

public sealed class Raid
{
    // weak: raidManager is process-global and outlives world switches — an
    // unowned ref trapped on the first touch after loading another save
    public World world;
    public int cx, cy, cz;
    public int wave = 0;
    public int totalWaves;
    public List<int> raiders = new();   // entity ids
    public bool active = true;
    public bool victory = false;
    public bool defeat = false;
    public int cooldown = 60;
    public double totalHealth = 0.0;
    public double maxHealth = 1.0;

    public Raid(World world, int cx, int cy, int cz, int totalWaves)
    {
        this.world = world;
        this.cx = cx; this.cy = cy; this.cz = cz;
        this.totalWaves = totalWaves;
    }
}

public sealed class RaidManager
{
    public List<Raid> raids = new();
    private RandomX rng = new RandomX(0x4A1D);

    public RaidManager() {}

    /// call when a player with Bad Omen enters a village area
    public void tryStartRaid(World world, Player player)
    {
        if (!player.hasEffect("bad_omen")) { return; }
        // is there a village nearby? (bell or villagers)
        var villagers = world.getEntitiesNear(player.x, player.y, player.z, 48, filter: e => (e as Entity)?.type == "villager");
        if (villagers.Count < 1) { return; }
        // existing raid at this village?
        foreach (var r in raids)
        {
            double dx = (double)r.cx - player.x, dz = (double)r.cz - player.z;
            if (r.world == world && !r.victory && !r.defeat && dx * dx + dz * dz < 96 * 96) { return; }
        }
        int omenLvl = player.effectLevel("bad_omen");
        player.removeEffect("bad_omen");
        int totalWaves = (world.difficulty == 1 ? 3 : world.difficulty == 2 ? 5 : 7) + (omenLvl > 1 ? 1 : 0);
        var raid = new Raid(world, ifloor(player.x), ifloor(player.y), ifloor(player.z), totalWaves);
        raids.Add(raid);
        world.hooks.playSound("event.raid.horn", player.x, player.y + 8, player.z, 6, 1);
    }

    public void tick(World world)
    {
        raids.RemoveAll(r => r.world == null);
        foreach (var raid in raids)
        {
            if (raid.world != world || !raid.active) { continue; }
            // count living raiders + health
            int alive = 0;
            double hp = 0.0;
            foreach (var id in raid.raiders)
            {
                if (world.entityById.TryGetValue(id, out var ev) && ev is LivingEntity e && !e.dead) { alive += 1; hp += e.health; }
            }
            raid.totalHealth = hp;
            if (raid.cooldown > 0)
            {
                raid.cooldown -= 1;
                continue;
            }
            if (alive == 0)
            {
                if (raid.wave >= raid.totalWaves)
                {
                    // VICTORY
                    raid.active = false;
                    raid.victory = true;
                    world.hooks.playSound("ui.toast.challenge_complete", (double)raid.cx, (double)raid.cy, (double)raid.cz, 4, 1);
                    foreach (var p in world.getEntitiesNear((double)raid.cx, (double)raid.cy, (double)raid.cz, 64, filter: e => (e as Entity)?.isPlayer ?? false))
                    {
                        (p as LivingEntity)?.addEffect("hero_of_the_village", 48000, 0);
                    }
                    continue;
                }
                // next wave
                raid.wave += 1;
                raid.raiders = new List<int>();
                var comp = WAVES.TryGetValue(Math.Min(7, raid.wave), out var c0) ? c0 : WAVES[7];
                double ang = rng.nextFloat() * Math.PI * 2;
                double sx = (double)raid.cx + detCos(ang) * 40;
                double sz = (double)raid.cz + detSin(ang) * 40;
                bool captainSet = false;
                double maxHp = 0.0;
                foreach (var (mob, count) in comp)
                {
                    for (int _i = 0; _i < count; _i++)
                    {
                        double px = sx + rng.nextFloat() * 6 - 3;
                        double pz = sz + rng.nextFloat() * 6 - 3;
                        int py = world.surfaceY(ifloor(px), ifloor(pz));
                        var e = spawnMob(world, mob, px, (double)py, pz, new SpawnOpts(persistent: true, captain: !captainSet && mob == "pillager"));
                        if (e != null)
                        {
                            raid.raiders.Add(e.id);
                            maxHp += (e as LivingEntity)?.maxHealth ?? 20;
                            if (mob == "pillager") { captainSet = true; }
                            // raiders hunt the village
                            (e as Mob)?.nav.moveTo((double)raid.cx, (double)raid.cy, (double)raid.cz, 1.1);
                        }
                    }
                }
                raid.maxHealth = maxHp;
                raid.cooldown = 40;
                world.hooks.playSound("event.raid.horn", (double)raid.cx, (double)(raid.cy + 8), (double)raid.cz, 6, 1);
            }
            else
            {
                raid.cooldown = 20;
                // defeat check: all villagers dead
                if (world.time % 100 == 0)
                {
                    var villagers = world.getEntitiesNear((double)raid.cx, (double)raid.cy, (double)raid.cz, 64, filter: e => (e as Entity)?.type == "villager");
                    if (villagers.Count == 0)
                    {
                        raid.active = false;
                        raid.defeat = true;
                    }
                }
            }
        }
        // prune finished
        if (world.time % 200 == 0)
        {
            raids = raids.FindAll(r => r.active || (world.time % 1200 != 0));
        }
    }

    public Raid activeRaidNear(World world, double x, double z)
    {
        foreach (var r in raids)
        {
            double dx = (double)r.cx - x, dz = (double)r.cz - z;
            if (r.world == world && r.active && dx * dx + dz * dz < 96 * 96) { return r; }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // wave composition table (module-private in Swift: `private let WAVES`)
    // ---------------------------------------------------------------------
    private static readonly Dictionary<int, List<(string, int)>> WAVES = new()
    {
        [1] = new List<(string, int)> { ("pillager", 4), ("vindicator", 1) },
        [2] = new List<(string, int)> { ("pillager", 5), ("vindicator", 2) },
        [3] = new List<(string, int)> { ("pillager", 4), ("vindicator", 2), ("witch", 1), ("ravager", 1) },
        [4] = new List<(string, int)> { ("pillager", 5), ("vindicator", 3), ("witch", 2) },
        [5] = new List<(string, int)> { ("pillager", 5), ("vindicator", 4), ("witch", 2), ("evoker", 1), ("ravager", 1) },
        [6] = new List<(string, int)> { ("pillager", 6), ("vindicator", 4), ("witch", 2), ("evoker", 1) },
        [7] = new List<(string, int)> { ("pillager", 7), ("vindicator", 5), ("witch", 3), ("evoker", 2), ("ravager", 2) },
    };
}

public static class RaidsGlobals
{
    public static readonly RaidManager raidManager = new RaidManager();

    /// patrols: occasionally spawn pillager patrols in the world
    public static void tryPatrolSpawn(World world, List<Player> players, ref RandomX rng)
    {
        if (world.time % 12000 != 0 || world.difficulty == 0 || players.Count == 0) { return; }
        if (rng.nextFloat() > 0.2) { return; }
        var p = players[rng.nextInt(players.Count)];
        double ang = rng.nextFloat() * Math.PI * 2;
        double x = p.x + detCos(ang) * (32 + rng.nextFloat() * 32);
        double z = p.z + detSin(ang) * (32 + rng.nextFloat() * 32);
        int y = world.surfaceY(ifloor(x), ifloor(z));
        if (world.lightAt(ifloor(x), y, ifloor(z)) > 7 && !world.isDay()) { return; }
        // baseline: rng-in-loop-condition — rerolls every iteration check
        int i = 0;
        while (i < 2 + rng.nextInt(3))
        {
            spawnMob(world, "pillager", x + rng.nextFloat() * 4 - 2, (double)y, z + rng.nextFloat() * 4 - 2, new SpawnOpts(persistent: false, captain: i == 0));
            i += 1;
        }
    }
}

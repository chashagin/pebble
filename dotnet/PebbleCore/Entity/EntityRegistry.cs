// Entity factory registry + natural spawning rules — Registration order mirrors baseline (it feeds entityTypes()).
//
// Module-global free funcs/vars from EntityRegistry.swift live in static class
// EntityRegistryGlobals (surface via `global using static PebbleCore.EntityRegistryGlobals`).
// The Swift typealias `EntityFactory` becomes the namespace-level delegate `EntityFactory`.
//
// Depends (unqualified, surface these via global-using if not already):
//   EntityGlobals.ifloor, BiomeGlobals.BIOMES/biomeDef, DetMath.detSin/detCos, Reg.B/blockDefs.
// External symbols by name: World, Entity, EntityRef, RandomX, Player, Mob, Slime, Pillager,
//   Vindicator, all mob/entity factory classes, SpawnOpts, registerEntityHelpers, bindSpawnMob, Biome.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public delegate Entity EntityFactory(World world);

public static class EntityRegistryGlobals
{
    private static List<(string, EntityFactory)> FACTORIES = new();
    private static Dictionary<string, EntityFactory> FACTORY_BY_NAME = new();

    private static void reg(string name, EntityFactory f)
    {
        FACTORIES.Add((name, f));
        FACTORY_BY_NAME[name] = f;
    }

    private static bool entitiesRegistered = false;

    public static void registerAllEntities()
    {
        if (entitiesRegistered) { return; }
        entitiesRegistered = true;
        registerEntityHelpers();

        reg("cow", w => new Cow(w)); reg("mooshroom", w => new Mooshroom(w));
        reg("pig", w => new Pig(w)); reg("sheep", w => new Sheep(w));
        reg("chicken", w => new Chicken(w)); reg("rabbit", w => new Rabbit(w));
        reg("wolf", w => new Wolf(w)); reg("cat", w => new Cat(w)); reg("ocelot", w => new Ocelot(w));
        reg("fox", w => new Fox(w)); reg("parrot", w => new Parrot(w)); reg("bee", w => new Bee(w));
        reg("axolotl", w => new Axolotl(w)); reg("frog", w => new Frog(w)); reg("tadpole", w => new Tadpole(w));
        reg("goat", w => new Goat(w)); reg("turtle", w => new Turtle(w)); reg("dolphin", w => new Dolphin(w));
        reg("squid", w => new Squid(w)); reg("glow_squid", w => new GlowSquid(w)); reg("bat", w => new Bat(w));
        reg("polar_bear", w => new PolarBear(w)); reg("panda", w => new Panda(w)); reg("strider", w => new Strider(w));
        reg("camel", w => new Camel(w)); reg("sniffer", w => new Sniffer(w)); reg("allay", w => new Allay(w));
        reg("cod", w => new Cod(w)); reg("salmon", w => new Salmon(w));
        reg("tropical_fish", w => new TropicalFish(w)); reg("pufferfish", w => new Pufferfish(w));
        reg("villager", w => new Villager(w)); reg("wandering_trader", w => new WanderingTrader(w));
        reg("iron_golem", w => new IronGolem(w)); reg("snow_golem", w => new SnowGolem(w));
        reg("horse", w => new Horse(w)); reg("donkey", w => new Donkey(w)); reg("mule", w => new Mule(w));
        reg("skeleton_horse", w => new SkeletonHorse(w)); reg("llama", w => new Llama(w));
        reg("zombie", w => new Zombie(w)); reg("husk", w => new Husk(w)); reg("drowned", w => new Drowned(w));
        reg("zombie_villager", w => new ZombieVillagerMob(w));
        reg("skeleton", w => new Skeleton(w)); reg("stray", w => new Stray(w));
        reg("creeper", w => new Creeper(w));
        reg("spider", w => new Spider(w)); reg("cave_spider", w => new CaveSpider(w));
        reg("slime", w => new Slime(w)); reg("witch", w => new Witch(w));
        reg("enderman", w => new Enderman(w));
        reg("silverfish", w => new Silverfish(w)); reg("endermite", w => new Endermite(w));
        reg("phantom", w => new Phantom(w));
        reg("guardian", w => new Guardian(w)); reg("elder_guardian", w => new ElderGuardian(w));
        reg("shulker", w => new Shulker(w));
        reg("pillager", w => new Pillager(w)); reg("vindicator", w => new Vindicator(w));
        reg("evoker", w => new Evoker(w)); reg("vex", w => new Vex(w)); reg("ravager", w => new Ravager(w));
        reg("blaze", w => new Blaze(w)); reg("ghast", w => new Ghast(w)); reg("magma_cube", w => new MagmaCube(w));
        reg("zombified_piglin", w => new ZombifiedPiglin(w));
        reg("piglin", w => new Piglin(w)); reg("piglin_brute", w => new PiglinBrute(w));
        reg("hoglin", w => new Hoglin(w)); reg("zoglin", w => new Zoglin(w));
        reg("wither_skeleton", w => new WitherSkeletonMob(w));
        reg("warden", w => new Warden(w));
        reg("ender_dragon", w => new EnderDragon(w));
        reg("wither", w => new WitherBoss(w));
        reg("item", w => new ItemEntity(w)); reg("xp_orb", w => new XPOrb(w));
        reg("falling_block", w => new FallingBlockEntity(w)); reg("tnt", w => new TNTEntity(w));
        reg("lightning", w => new LightningBolt(w)); reg("end_crystal", w => new EndCrystal(w));
        reg("effect_cloud", w => new AreaEffectCloud(w)); reg("eye_of_ender", w => new EyeOfEnderEntity(w));
        reg("arrow", w => new ArrowEntity(w)); reg("snowball", w => new ThrownSnowball(w));
        reg("egg", w => new ThrownEgg(w)); reg("ender_pearl", w => new ThrownPearl(w));
        reg("xp_bottle", w => new ThrownXPBottle(w)); reg("thrown_potion", w => new ThrownPotion(w));
        reg("fireball", w => new Fireball(w)); reg("wither_skull", w => new WitherSkull(w));
        reg("dragon_fireball", w => new DragonFireball(w)); reg("shulker_bullet", w => new ShulkerBullet(w));
        reg("trident", w => new TridentEntity(w)); reg("firework", w => new FireworkEntity(w));
        reg("fishing_bobber", w => new FishingBobber(w)); reg("llama_spit", w => new LlamaSpit(w));
        reg("boat", w => new Boat(w)); reg("minecart", w => new Minecart(w));
        reg("player", w => new Player(w));

        bindSpawnMob(spawnMob);
    }

    public static Entity createEntity(string type, World world)
        => FACTORY_BY_NAME.TryGetValue(type, out var f) ? f(world) : null;

    public static List<string> entityTypes() => FACTORIES.Select(t => t.Item1).ToList();

    public static Entity spawnMob(World world, string type, double x, double y, double z, SpawnOpts? data = null)
    {
        var e = createEntity(type, world);
        if (e == null) { return null; }
        e.setPos(x, y, z);
        if (data != null)
        {
            var d = data.Value;
            if (d.baby && e is Mob mob)
            {
                mob.baby = true;
                mob.growUpAge = 24000;
            }
            if (d.size is int size && size != 0 && e is Slime slime) { slime.setSize(size); }
            if (d.persistent) { e.persistent = true; }
            if (d.captain)
            {
                if (e is Pillager pillager) pillager.isCaptain = true;
                if (e is Vindicator vindicator) vindicator.isCaptain = true;
            }
            if (d.variant is int v && v != 0) { e.data.variant = v; }
            // mirror the spawn option-bag fields onto entity data
            if (d.captain) { e.data.captain = true; }
            if (d.baby) { e.data.baby = true; }
            if (d.persistent) { e.data.persistent = true; }
            if (d.size is int s) { e.data.size = s; }
        }
        world.addEntity(e);
        return e;
    }

    public static Entity loadEntity(World world, Dictionary<string, object> d)
    {
        if (!(d.TryGetValue("type", out var tv) && tv is string type)) { return null; }
        var e = createEntity(type, world);
        if (e == null) { return null; }
        e.load(d);
        return e;
    }

    // =============================================================================
    // Natural spawning
    // =============================================================================
    public static void naturalSpawnTick(World world, List<Player> players, ref RandomX rng)
    {
        if (!world.rule("doMobSpawning") || players.Count == 0) { return; }
        // count by category
        var counts = new Dictionary<string, int>();
        foreach (var e in world.entities)
        {
            if (e is Mob mob)
            {
                counts[mob.category] = (counts.TryGetValue(mob.category, out var cur) ? cur : 0) + 1;
            }
        }
        var attempts = new List<(string, int, bool)>
        {
            ("monster", 70, true),          // every tick
            ("creature", 10, world.time % 400 == 0),
            ("ambient", 15, world.time % 400 == 0),
            ("water", 5, world.time % 400 == 0),
        };
        foreach (var (cat, cap, doIt) in attempts)
        {
            if (!doIt) { continue; }
            if (cat == "monster" && world.difficulty == 0) { continue; }
            if ((counts.TryGetValue(cat, out var catCount) ? catCount : 0) >= cap) { continue; }
            // pick a random player and position
            var p = players[rng.nextInt(players.Count)];
            double dist = 24 + rng.nextFloat() * 80;
            double ang = rng.nextFloat() * Math.PI * 2;
            int x = ifloor(p.x + detCos(ang) * dist);
            int z = ifloor(p.z + detSin(ang) * dist);
            if (!world.isLoadedAt(x, z)) { continue; }
            int y;
            if (cat == "monster" && rng.nextFloat() < 0.6)
            {
                // try caves: random y below surface
                y = world.info.minY + 1 + rng.nextInt(Math.Max(1, world.surfaceY(x, z) - world.info.minY));
            }
            else
            {
                y = world.surfaceY(x, z);
            }
            int biome = world.biomeAt(x, y, z);
            var bdef = BIOMES[(int)biome];
            if (bdef == null) { continue; }
            var list = cat == "monster" ? bdef.monsters : cat == "creature" ? bdef.creatures : cat == "water" ? bdef.waterCreatures : bdef.ambient;
            if (list.Count == 0) { continue; }
            var entry = rng.pickWeighted(list, e => e.weight);
            string mobType = entry.mob;
            int minPack = entry.minPack;
            int maxPack = entry.maxPack;

            // spawn conditions
            if (!canSpawnAt(world, mobType, cat, x, y, z, ref rng)) { continue; }
            // pack spawn
            int pack = minPack + rng.nextInt(Math.Max(1, maxPack - minPack + 1));
            int spawned = 0;
            for (int i = 0; i < pack; i++)
            {
                int px = x + rng.nextInt(9) - 4;
                int pz = z + rng.nextInt(9) - 4;
                int py = cat == "water" ? y : world.surfaceY(px, pz);
                if (cat == "monster") { py = y; }
                if (!canSpawnAt(world, mobType, cat, px, py, pz, ref rng)) { continue; }
                // don't spawn too close to players
                bool tooClose = false;
                foreach (var pl in players)
                {
                    double dx = pl.x - (double)px, dy = pl.y - (double)py, dz = pl.z - (double)pz;
                    if (dx * dx + dy * dy + dz * dz < 24 * 24) { tooClose = true; break; }
                }
                if (tooClose) { continue; }
                var mob = spawnMob(world, mobType, (double)px + 0.5, (double)py, (double)pz + 0.5, new SpawnOpts());
                if (mob != null) { spawned += 1; }
                if ((counts.TryGetValue(cat, out var cc) ? cc : 0) + spawned >= cap) { break; }
            }
        }
    }

    public static bool canSpawnAt(World world, string mobType, string cat, int x, int y, int z, ref RandomX rng)
    {
        if (y <= world.info.minY || y >= world.info.minY + world.info.height - 1) { return false; }
        int at = world.getBlock(x, y, z);
        int atId = at >> 4;
        int below = world.getBlock(x, y - 1, z) >> 4;
        if (cat == "water")
        {
            return atId == (int)B.water;
        }
        // land mobs never spawn inside fluids (water is "replaceable" and slipped
        // through — zombies and chickens were spawning in the ocean)
        if (atId == (int)B.water || atId == (int)B.lava) { return false; }
        int headId = world.getBlock(x, y + 1, z) >> 4;
        if (headId == (int)B.water) { return false; }
        if (atId != 0 && !blockDefs[atId].replaceable) { return false; }
        int head = world.getBlock(x, y + 1, z) >> 4;
        if (head != 0 && blockDefs[head].solid) { return false; }
        if (below == 0 || !blockDefs[below].solid) { return false; }
        if (cat == "monster")
        {
            // vanilla 1.20 isDarkEnoughToSpawn: block light must be 0, then two
            // probabilistic gates — raw skylight vs rand(32), then skyDarken-adjusted
            // light vs rand(8). The old "≤7" rule let every midday shadow spawn mobs.
            int blockLight = world.getBlockLight(x, y, z);
            if (blockLight > 0) { return false; }
            if (mobType == "blaze" || mobType == "magma_cube" || mobType == "ghast" || mobType == "zombified_piglin" || mobType == "piglin" || mobType == "hoglin" || mobType == "strider")
            {
                return true; // nether mobs ignore light
            }
            if (world.info.hasSky)
            {
                int rawSky = world.getSkyLight(x, y, z);
                if (rawSky > rng.nextInt(32)) { return false; }
                int effective = (int)world.lightAt(x, y, z);
                if (effective > rng.nextInt(8)) { return false; }
            }
            if (below == (int)B.bedrock) { return false; }
            // slimes: swamps at night or slime chunks below y=40
            if (mobType == "slime")
            {
                int biome = world.biomeAt(x, y, z);
                if (biome == (int)Biome.swamp || biome == (int)Biome.mangroveSwamp) { return y < 70; }
                // slime chunk
                int cx = (int)Math.Floor((double)x / 16), cz = (int)Math.Floor((double)z / 16);
                uint h = (imul32(cx, 0x1f1f1f1f) ^ imul32(cz, 0x5f356495) ^ world.seed);
                return (h % 10) == 0 && y < 40;
            }
            return true;
        }
        if (cat == "creature")
        {
            // animals need grass-ish + light
            if (world.lightAt(x, y, z) < 9 && world.info.hasSky) { return false; }
            return below == (int)B.grass_block || below == (int)B.sand || below == (int)B.snow_block || below == (int)B.mycelium || below == (int)B.podzol || !world.info.hasSky;
        }
        return true;
    }

    private static uint imul32(int a, uint b)
        => unchecked((uint)unchecked((int)a) * b);

    /// helper for commands /summon listing
    public static List<string> spawnableMobs()
    {
        var excluded = new HashSet<string> { "item", "xp_orb", "falling_block", "tnt", "lightning", "effect_cloud", "eye_of_ender", "arrow", "snowball", "egg", "ender_pearl", "xp_bottle", "thrown_potion", "fireball", "wither_skull", "dragon_fireball", "shulker_bullet", "trident", "firework", "fishing_bobber", "llama_spit", "player" };
        return entityTypes().Where(t => !excluded.Contains(t)).ToList();
    }
}

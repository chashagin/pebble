// Every biome: climate selection, surface rules, colors, features, spawns.
// Enum order and climate thresholds are pinned by the frozen golden baselines.
//
// Module-level free funcs/vars (registerAllBiomes, BIOMES, selectBiome,
// peaksValleys, biomeDef, temperatureAt, snowsAt, isOceanBiome, isCaveBiome)
// are surfaced via: public static class BiomeGlobals
//   -> add `global using static PebbleCore.BiomeGlobals;`

using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public enum Biome
{
    ocean = 0, deepOcean, frozenOcean, deepFrozenOcean, coldOcean, deepColdOcean,
    lukewarmOcean, deepLukewarmOcean, warmOcean,
    river, frozenRiver, beach, snowyBeach, stonyShore,
    plains, sunflowerPlains, snowyPlains, iceSpikes, desert,
    swamp, mangroveSwamp,
    forest, flowerForest, birchForest, oldGrowthBirchForest, darkForest,
    taiga, oldGrowthPineTaiga, oldGrowthSpruceTaiga, snowyTaiga,
    savanna, savannaPlateau, windsweptSavanna,
    windsweptHills, windsweptGravellyHills, windsweptForest,
    jungle, sparseJungle, bambooJungle,
    badlands, erodedBadlands, woodedBadlands,
    meadow, cherryGrove, grove, snowySlopes, jaggedPeaks, frozenPeaks, stonyPeaks,
    mushroomFields,
    dripstoneCaves, lushCaves, deepDark,
    netherWastes, crimsonForest, warpedForest, soulSandValley, basaltDeltas,
    theEnd, endHighlands, endMidlands, smallEndIslands, endBarrens
}

// Swift: public typealias SpawnEntry = (mob: String, weight: Double, minPack: Int, maxPack: Int)
// C#: named tuple.

public sealed class BiomeDef
{
    public readonly Biome id;
    public readonly string name;
    public readonly string displayName;
    public readonly double temperature;
    public readonly double downfall;
    public readonly uint grassColor;
    public readonly uint foliageColor;
    public readonly uint waterColor;
    public readonly uint fogTint;
    public readonly ushort top;
    public readonly ushort under;
    public readonly ushort underwaterTop;
    public readonly List<string> features;
    public readonly List<(string mob, double weight, int minPack, int maxPack)> monsters;
    public readonly List<(string mob, double weight, int minPack, int maxPack)> creatures;
    public readonly List<(string mob, double weight, int minPack, int maxPack)> waterCreatures;
    public readonly List<(string mob, double weight, int minPack, int maxPack)> ambient;
    public readonly string mood;

    public BiomeDef(Biome id, string name, double temperature, double downfall,
        uint grassColor, uint foliageColor, uint waterColor, uint fogTint,
        ushort top, ushort under, ushort underwaterTop,
        List<string> features,
        List<(string mob, double weight, int minPack, int maxPack)> monsters,
        List<(string mob, double weight, int minPack, int maxPack)> creatures,
        List<(string mob, double weight, int minPack, int maxPack)> waterCreatures,
        List<(string mob, double weight, int minPack, int maxPack)> ambient,
        string mood)
    {
        this.id = id; this.name = name;
        displayName = string.Join(" ", name.Split('_')
            .Select(w => w.Substring(0, 1).ToUpperInvariant() + w.Substring(1)));
        this.temperature = temperature; this.downfall = downfall;
        this.grassColor = grassColor; this.foliageColor = foliageColor;
        this.waterColor = waterColor; this.fogTint = fogTint;
        this.top = top; this.under = under; this.underwaterTop = underwaterTop;
        this.features = features; this.monsters = monsters; this.creatures = creatures;
        this.waterCreatures = waterCreatures; this.ambient = ambient; this.mood = mood;
    }
}

public struct Climate
{
    public double t;
    public double h;
    public double c;
    public double e;
    public double w;
    public double pv;
    public double rare;

    public Climate(double t, double h, double c, double e, double w, double pv, double rare)
    {
        this.t = t; this.h = h; this.c = c; this.e = e; this.w = w; this.pv = pv; this.rare = rare;
    }
}

public static class BiomeGlobals
{
    public static List<BiomeDef> BIOMES = new();

    private static bool biomesRegistered = false;

    public static void registerAllBiomes()
    {
        if (biomesRegistered) return;
        biomesRegistered = true;
        if (blockDefs.Count == 0) throw new System.InvalidOperationException("blocks must register first");

        int biomeCount = System.Enum.GetValues(typeof(Biome)).Length;
        BIOMES = Enumerable.Repeat<BiomeDef>(null, biomeCount).ToList();

        var DEF_MONSTERS = new List<(string mob, double weight, int minPack, int maxPack)>
        {
            ("zombie", 95, 4, 4), ("skeleton", 100, 4, 4), ("creeper", 100, 4, 4),
            ("spider", 100, 4, 4), ("enderman", 10, 1, 4), ("witch", 5, 1, 1),
        };
        var DEF_CREATURES = new List<(string mob, double weight, int minPack, int maxPack)>
        {
            ("sheep", 12, 4, 4), ("pig", 10, 4, 4), ("chicken", 10, 4, 4), ("cow", 8, 4, 4),
        };
        var OCEAN_WATER = new List<(string mob, double weight, int minPack, int maxPack)>
        {
            ("squid", 4, 1, 4), ("cod", 10, 3, 6), ("dolphin", 2, 1, 2)
        };
        var BAT_AMBIENT = new List<(string mob, double weight, int minPack, int maxPack)>
        {
            ("bat", 10, 8, 8)
        };

        void biome(Biome id, string name, double temperature, double downfall,
            uint grassColor = 0x91bd59, uint foliageColor = 0x77ab2f,
            uint waterColor = 0x3f76e4, uint fogTint = 0xffffff,
            ushort? top = null, ushort? under = null, ushort? underwaterTop = null,
            List<string> features = null,
            List<(string mob, double weight, int minPack, int maxPack)> monsters = null,
            List<(string mob, double weight, int minPack, int maxPack)> creatures = null,
            List<(string mob, double weight, int minPack, int maxPack)> waterCreatures = null,
            List<(string mob, double weight, int minPack, int maxPack)> ambient = null,
            string mood = "overworld")
        {
            BIOMES[(int)id] = new BiomeDef(
                id: id, name: name, temperature: temperature, downfall: downfall,
                grassColor: grassColor, foliageColor: foliageColor, waterColor: waterColor, fogTint: fogTint,
                top: top ?? cell(B.grass_block), under: under ?? cell(B.dirt), underwaterTop: underwaterTop ?? cell(B.dirt),
                features: features ?? new List<string>(),
                monsters: monsters ?? DEF_MONSTERS,
                creatures: creatures ?? DEF_CREATURES,
                waterCreatures: waterCreatures ?? new List<(string mob, double weight, int minPack, int maxPack)>(),
                ambient: ambient ?? BAT_AMBIENT, mood: mood);
        }

        const string GRASS_PATCH = "patch:short_grass:24";
        string TREES(string kind, int count, double extra = 0.1) => $"trees:{kind}:{count}:{extra}";

        // oceans
        var oceanFeat = new List<string> { "patch_water:seagrass:32", "kelp:6" };
        ushort gravel = cell(B.gravel), sand = cell(B.sand);
        biome(Biome.ocean, "ocean", temperature: 0.5, downfall: 0.5, top: sand, under: sand, underwaterTop: sand, features: oceanFeat, waterCreatures: OCEAN_WATER, mood: "water");
        biome(Biome.deepOcean, "deep_ocean", temperature: 0.5, downfall: 0.5, top: gravel, under: gravel, underwaterTop: gravel, features: oceanFeat, waterCreatures: OCEAN_WATER, mood: "water");
        biome(Biome.frozenOcean, "frozen_ocean", temperature: 0.0, downfall: 0.5, waterColor: 0x3938c9, top: gravel, under: gravel, underwaterTop: gravel, features: new List<string> { "iceberg:1" }, creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("polar_bear", 1, 1, 2) }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("salmon", 15, 1, 5) }, mood: "water");
        biome(Biome.deepFrozenOcean, "deep_frozen_ocean", temperature: 0.0, downfall: 0.5, waterColor: 0x3938c9, top: gravel, under: gravel, underwaterTop: gravel, features: new List<string> { "iceberg:2" }, creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("polar_bear", 1, 1, 2) }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("salmon", 15, 1, 5) }, mood: "water");
        biome(Biome.coldOcean, "cold_ocean", temperature: 0.3, downfall: 0.5, waterColor: 0x3d57d6, top: gravel, under: gravel, underwaterTop: gravel, features: new List<string> { "patch_water:seagrass:32", "kelp:10" }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("cod", 15, 3, 6), ("salmon", 15, 1, 5) }, mood: "water");
        biome(Biome.deepColdOcean, "deep_cold_ocean", temperature: 0.3, downfall: 0.5, waterColor: 0x3d57d6, top: gravel, under: gravel, underwaterTop: gravel, features: new List<string> { "patch_water:seagrass:32", "kelp:10" }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("cod", 15, 3, 6), ("salmon", 15, 1, 5) }, mood: "water");
        biome(Biome.lukewarmOcean, "lukewarm_ocean", temperature: 0.6, downfall: 0.5, waterColor: 0x45adf2, top: sand, under: sand, underwaterTop: sand, features: new List<string> { "patch_water:seagrass:48", "kelp:2", "sea_pickle:1" }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("cod", 8, 3, 6), ("tropical_fish", 12, 4, 8), ("dolphin", 2, 1, 2), ("pufferfish", 5, 1, 3) }, mood: "water");
        biome(Biome.deepLukewarmOcean, "deep_lukewarm_ocean", temperature: 0.6, downfall: 0.5, waterColor: 0x45adf2, top: sand, under: sand, underwaterTop: sand, features: oceanFeat, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("cod", 8, 3, 6), ("tropical_fish", 12, 4, 8), ("dolphin", 2, 1, 2) }, mood: "water");
        biome(Biome.warmOcean, "warm_ocean", temperature: 0.7, downfall: 0.5, waterColor: 0x43d5ee, top: sand, under: sand, underwaterTop: sand, features: new List<string> { "coral_reef:8", "patch_water:seagrass:48", "sea_pickle:3" }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 4, 1, 4), ("tropical_fish", 25, 4, 8), ("dolphin", 2, 1, 2), ("pufferfish", 15, 1, 3) }, mood: "water");

        // rivers / shores
        biome(Biome.river, "river", temperature: 0.5, downfall: 0.5, top: sand, under: sand, underwaterTop: sand, features: new List<string> { "patch_water:seagrass:20", "sugar_cane:8", "clay_disk:1" }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("squid", 2, 1, 4), ("salmon", 5, 1, 5) }, mood: "water");
        biome(Biome.frozenRiver, "frozen_river", temperature: 0.0, downfall: 0.5, top: sand, under: sand, underwaterTop: sand, features: new List<string> { "sugar_cane:4" }, waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("salmon", 5, 1, 5) });
        biome(Biome.beach, "beach", temperature: 0.8, downfall: 0.4, top: sand, under: sand, underwaterTop: sand, features: new List<string> { "sugar_cane:6" }, creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("turtle", 5, 2, 5) }, mood: "water");
        biome(Biome.snowyBeach, "snowy_beach", temperature: 0.05, downfall: 0.3, top: sand, under: sand, underwaterTop: sand);
        biome(Biome.stonyShore, "stony_shore", temperature: 0.2, downfall: 0.3, top: gravel, under: cell(B.stone), underwaterTop: gravel);

        // temperate
        biome(Biome.plains, "plains", temperature: 0.8, downfall: 0.4,
              features: new List<string> { TREES("oak_sparse", 0, 0.05), "flowers:plains:8", GRASS_PATCH, "pumpkin:32", "bee_nest:oak:40" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("horse", 5, 2, 6), ("donkey", 1, 1, 3) }).ToList());
        biome(Biome.sunflowerPlains, "sunflower_plains", temperature: 0.8, downfall: 0.4,
              features: new List<string> { "patch:sunflower:20", TREES("oak_sparse", 0, 0.05), "flowers:plains:8", GRASS_PATCH },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("horse", 5, 2, 6) }).ToList());
        biome(Biome.snowyPlains, "snowy_plains", temperature: 0.0, downfall: 0.5, grassColor: 0x80b497, foliageColor: 0x60a17b,
              features: new List<string> { TREES("spruce", 0, 0.08) },
              monsters: DEF_MONSTERS.Where(x => x.mob != "skeleton").Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("skeleton", 20, 4, 4), ("stray", 80, 4, 4) }).ToList(),
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("rabbit", 10, 2, 3), ("polar_bear", 1, 1, 2) });
        biome(Biome.iceSpikes, "ice_spikes", temperature: 0.0, downfall: 0.5, grassColor: 0x80b497, top: cell(B.snow_block), under: cell(B.dirt),
              features: new List<string> { "ice_spike:3" },
              monsters: DEF_MONSTERS.Where(x => x.mob != "skeleton").Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("stray", 80, 4, 4) }).ToList(),
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("rabbit", 10, 2, 3), ("polar_bear", 1, 1, 2) });
        biome(Biome.desert, "desert", temperature: 2.0, downfall: 0, grassColor: 0xbfb755, foliageColor: 0xaea42a,
              top: sand, under: cell(B.sandstone), underwaterTop: sand,
              features: new List<string> { "cactus:4", "patch:dead_bush:4", "sugar_cane:4", "desert_well:1" },
              monsters: DEF_MONSTERS.Where(x => x.mob != "zombie").Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("zombie", 19, 4, 4), ("husk", 80, 4, 4) }).ToList(),
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("rabbit", 4, 2, 3), ("camel", 1, 1, 1) });
        biome(Biome.swamp, "swamp", temperature: 0.8, downfall: 0.9, grassColor: 0x6a7039, foliageColor: 0x6a7039, waterColor: 0x617b64,
              features: new List<string> { TREES("swamp_oak", 2), "patch:blue_orchid:4", GRASS_PATCH, "lily_pad:4", "patch_water:seagrass:24", "sugar_cane:10", "clay_disk:1", "patch:brown_mushroom:8", "patch:red_mushroom:8" },
              monsters: DEF_MONSTERS.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("slime", 100, 1, 1) }).ToList(),
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("frog", 10, 2, 5) }).ToList(), mood: "dark");
        biome(Biome.mangroveSwamp, "mangrove_swamp", temperature: 0.8, downfall: 0.9, grassColor: 0x6a7039, foliageColor: 0x8db127, waterColor: 0x3a7a6a,
              top: cell(B.mud), under: cell(B.mud), underwaterTop: cell(B.mud),
              features: new List<string> { TREES("mangrove", 8), "lily_pad:2", "patch_water:seagrass:24" },
              monsters: DEF_MONSTERS.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("slime", 100, 1, 1) }).ToList(),
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("frog", 10, 2, 5) }, mood: "dark");
        biome(Biome.forest, "forest", temperature: 0.7, downfall: 0.8,
              features: new List<string> { TREES("oak_birch", 10), "flowers:forest:4", GRASS_PATCH, "patch:brown_mushroom:16", "bee_nest:oak:80" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("wolf", 5, 4, 4) }).ToList());
        biome(Biome.flowerForest, "flower_forest", temperature: 0.7, downfall: 0.8,
              features: new List<string> { TREES("oak_birch", 4), "flowers:flower_forest:40", GRASS_PATCH, "bee_nest:oak:20" },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("rabbit", 8, 2, 3) });
        biome(Biome.birchForest, "birch_forest", temperature: 0.6, downfall: 0.6, grassColor: 0x88bb67, foliageColor: 0x6ba941,
              features: new List<string> { TREES("birch", 10), "flowers:forest:4", GRASS_PATCH, "bee_nest:birch:80" });
        biome(Biome.oldGrowthBirchForest, "old_growth_birch_forest", temperature: 0.6, downfall: 0.6, grassColor: 0x88bb67, foliageColor: 0x6ba941,
              features: new List<string> { TREES("tall_birch", 10), "flowers:forest:4", GRASS_PATCH });
        biome(Biome.darkForest, "dark_forest", temperature: 0.7, downfall: 0.8, grassColor: 0x507a32, foliageColor: 0x59ae30,
              features: new List<string> { TREES("dark_oak", 16), "huge_mushroom:2", "patch:brown_mushroom:16", "flowers:forest:4", GRASS_PATCH }, mood: "dark");
        biome(Biome.taiga, "taiga", temperature: 0.25, downfall: 0.8, grassColor: 0x86b783, foliageColor: 0x68a464,
              features: new List<string> { TREES("spruce", 10), GRASS_PATCH, "patch:fern:16", "berry_bush:4", "patch:large_fern:8" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("wolf", 8, 4, 4), ("fox", 8, 2, 4), ("rabbit", 4, 2, 3) }).ToList());
        biome(Biome.oldGrowthPineTaiga, "old_growth_pine_taiga", temperature: 0.3, downfall: 0.8, grassColor: 0x86b87f, foliageColor: 0x68a55f, top: cell(B.podzol),
              features: new List<string> { TREES("mega_pine", 8), GRASS_PATCH, "patch:fern:16", "patch:brown_mushroom:12", "patch:red_mushroom:12", "mossy_boulder:2" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("wolf", 8, 4, 4), ("fox", 8, 2, 4) }).ToList());
        biome(Biome.oldGrowthSpruceTaiga, "old_growth_spruce_taiga", temperature: 0.25, downfall: 0.8, grassColor: 0x86b783, foliageColor: 0x68a464, top: cell(B.podzol),
              features: new List<string> { TREES("mega_spruce", 8), GRASS_PATCH, "patch:fern:16", "mossy_boulder:2" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("wolf", 8, 4, 4), ("fox", 8, 2, 4) }).ToList());
        biome(Biome.snowyTaiga, "snowy_taiga", temperature: -0.5, downfall: 0.4, grassColor: 0x80b497, foliageColor: 0x60a17b,
              features: new List<string> { TREES("spruce", 7), "patch:fern:8", "berry_bush:1" },
              monsters: DEF_MONSTERS.Where(x => x.mob != "skeleton").Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("skeleton", 20, 4, 4), ("stray", 80, 4, 4) }).ToList(),
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("wolf", 8, 4, 4), ("fox", 8, 2, 4), ("rabbit", 4, 2, 3) });
        biome(Biome.savanna, "savanna", temperature: 1.2, downfall: 0, grassColor: 0xbfb755, foliageColor: 0xaea42a,
              features: new List<string> { TREES("acacia", 1), "patch:short_grass:60", "patch:tall_grass:10" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("horse", 1, 2, 6), ("donkey", 1, 1, 1), ("llama", 8, 4, 4) }).ToList());
        biome(Biome.savannaPlateau, "savanna_plateau", temperature: 1.0, downfall: 0, grassColor: 0xbfb755, foliageColor: 0xaea42a,
              features: new List<string> { TREES("acacia", 1), "patch:short_grass:40" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("horse", 1, 2, 6), ("llama", 8, 4, 4) }).ToList());
        biome(Biome.windsweptSavanna, "windswept_savanna", temperature: 1.1, downfall: 0, grassColor: 0xbfb755, foliageColor: 0xaea42a,
              features: new List<string> { TREES("acacia", 1), "patch:short_grass:40" });
        biome(Biome.windsweptHills, "windswept_hills", temperature: 0.2, downfall: 0.3,
              features: new List<string> { TREES("spruce", 0, 0.05), GRASS_PATCH, "emerald_ore:1" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("llama", 5, 4, 6) }).ToList());
        biome(Biome.windsweptGravellyHills, "windswept_gravelly_hills", temperature: 0.2, downfall: 0.3, top: gravel, under: gravel,
              features: new List<string> { TREES("spruce", 0, 0.05), "emerald_ore:1" });
        biome(Biome.windsweptForest, "windswept_forest", temperature: 0.2, downfall: 0.3, top: cell(B.coarse_dirt),
              features: new List<string> { TREES("oak_spruce", 3), GRASS_PATCH, "emerald_ore:1" });
        biome(Biome.jungle, "jungle", temperature: 0.95, downfall: 0.9, grassColor: 0x59c93c, foliageColor: 0x30bb0b,
              features: new List<string> { TREES("jungle", 14), "patch:short_grass:50", "patch:fern:20", "melon:32", "vines:40", "flowers:jungle:6", "cocoa:8" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("parrot", 40, 1, 2), ("panda", 1, 1, 2), ("ocelot", 2, 1, 3), ("chicken", 10, 4, 4) }).ToList());
        biome(Biome.sparseJungle, "sparse_jungle", temperature: 0.95, downfall: 0.8, grassColor: 0x64c73f, foliageColor: 0x3eb80f,
              features: new List<string> { TREES("jungle_sparse", 2), "patch:short_grass:40", "melon:64", "vines:20" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("parrot", 40, 1, 2), ("ocelot", 2, 1, 3) }).ToList());
        biome(Biome.bambooJungle, "bamboo_jungle", temperature: 0.95, downfall: 0.9, grassColor: 0x59c93c, foliageColor: 0x30bb0b, top: cell(B.podzol),
              features: new List<string> { "bamboo:160", TREES("jungle_sparse", 2), "patch:short_grass:30", "melon:64" },
              creatures: DEF_CREATURES.Concat(new List<(string mob, double weight, int minPack, int maxPack)> { ("parrot", 40, 1, 2), ("panda", 80, 1, 2), ("ocelot", 2, 1, 3) }).ToList());
        ushort bTop = cell(B.red_sand), bUnder = cell(B.terracotta);
        biome(Biome.badlands, "badlands", temperature: 2.0, downfall: 0, grassColor: 0x90814d, foliageColor: 0x9e814d, top: bTop, under: bUnder, underwaterTop: bTop,
              features: new List<string> { "patch:dead_bush:6", "cactus:2", "badlands_gold:1" }, creatures: new List<(string mob, double weight, int minPack, int maxPack)>());
        biome(Biome.erodedBadlands, "eroded_badlands", temperature: 2.0, downfall: 0, grassColor: 0x90814d, foliageColor: 0x9e814d, top: bTop, under: bUnder, underwaterTop: bTop,
              features: new List<string> { "hoodoo:6", "patch:dead_bush:6", "cactus:2", "badlands_gold:1" }, creatures: new List<(string mob, double weight, int minPack, int maxPack)>());
        biome(Biome.woodedBadlands, "wooded_badlands", temperature: 2.0, downfall: 0, grassColor: 0x90814d, foliageColor: 0x9e814d, top: cell(B.coarse_dirt), under: bUnder, underwaterTop: bTop,
              features: new List<string> { TREES("oak_small", 3), "patch:dead_bush:6", "badlands_gold:1" }, creatures: new List<(string mob, double weight, int minPack, int maxPack)>());

        // mountain
        biome(Biome.meadow, "meadow", temperature: 0.5, downfall: 0.8, grassColor: 0x83bb6d, foliageColor: 0x63a948,
              features: new List<string> { "flowers:meadow:30", "patch:tall_grass:20", TREES("oak_bee", 0, 0.02) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("sheep", 12, 2, 4), ("donkey", 1, 1, 2), ("rabbit", 2, 2, 6) });
        biome(Biome.cherryGrove, "cherry_grove", temperature: 0.5, downfall: 0.8, grassColor: 0xb6db61, foliageColor: 0xb6db61, waterColor: 0x5db7ef,
              features: new List<string> { TREES("cherry", 4), "patch:pink_petals:30", "flowers:cherry:6", "bee_nest:cherry:40" },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("sheep", 12, 2, 4), ("rabbit", 2, 2, 6), ("pig", 10, 4, 4) });
        biome(Biome.grove, "grove", temperature: -0.2, downfall: 0.8, top: cell(B.snow_block), under: cell(B.dirt),
              features: new List<string> { TREES("spruce", 10) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("wolf", 8, 4, 4), ("rabbit", 4, 2, 3), ("fox", 8, 2, 4) });
        biome(Biome.snowySlopes, "snowy_slopes", temperature: -0.3, downfall: 0.9, top: cell(B.snow_block), under: cell(B.snow_block),
              features: new List<string> { "powder_snow:2" },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("rabbit", 4, 2, 3), ("goat", 5, 1, 3) });
        biome(Biome.jaggedPeaks, "jagged_peaks", temperature: -0.7, downfall: 0.9, top: cell(B.snow_block), under: cell(B.stone),
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("goat", 5, 1, 3) });
        biome(Biome.frozenPeaks, "frozen_peaks", temperature: -0.7, downfall: 0.9, top: cell(B.snow_block), under: cell(B.packed_ice),
              features: new List<string> { "ice_patch:2" },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("goat", 5, 1, 3) });
        biome(Biome.stonyPeaks, "stony_peaks", temperature: 1.0, downfall: 0.3, top: cell(B.stone), under: cell(B.stone), creatures: new List<(string mob, double weight, int minPack, int maxPack)>());
        biome(Biome.mushroomFields, "mushroom_fields", temperature: 0.9, downfall: 1, grassColor: 0x55c93f, foliageColor: 0x2bbb0f,
              top: cell(B.mycelium), under: cell(B.dirt),
              features: new List<string> { "huge_mushroom:3", "patch:red_mushroom:8", "patch:brown_mushroom:8" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)>(), creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("mooshroom", 8, 4, 8) });

        // cave biomes
        biome(Biome.dripstoneCaves, "dripstone_caves", temperature: 0.8, downfall: 0.4,
              features: new List<string> { "dripstone_cluster:25", "pointed_dripstone:100", "dripstone_pool:8" }, mood: "dark");
        biome(Biome.lushCaves, "lush_caves", temperature: 0.5, downfall: 0.5, grassColor: 0x91bd59,
              features: new List<string> { "moss_patch:40", "lush_vegetation:60", "glow_berries:30", "spore_blossom:8", "azalea_tree:2", "big_dripleaf:10", "small_dripleaf:8", "clay_pool:6" },
              waterCreatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("axolotl", 10, 1, 4), ("glow_squid", 10, 2, 4), ("tropical_fish", 25, 8, 8) }, mood: "lush");
        biome(Biome.deepDark, "deep_dark", temperature: 0.8, downfall: 0.4,
              features: new List<string> { "sculk_patch:60", "sculk_vein:40", "sculk_shrieker:4", "sculk_sensor:8" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)>(), creatures: new List<(string mob, double weight, int minPack, int maxPack)>(), ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "dark");

        // nether
        ushort rack = cell(B.netherrack);
        biome(Biome.netherWastes, "nether_wastes", temperature: 2, downfall: 0, fogTint: 0x330808,
              top: rack, under: rack, underwaterTop: rack,
              features: new List<string> { "glowstone_cluster:10", "lava_spring:8", "fire_patch:3", "magma_blob:4", "brown_mushroom_nether:2", "red_mushroom_nether:2" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)> { ("ghast", 50, 4, 4), ("zombified_piglin", 100, 4, 4), ("magma_cube", 2, 4, 4), ("enderman", 1, 4, 4), ("piglin", 15, 4, 4) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("strider", 60, 1, 2) }, ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "nether");
        biome(Biome.crimsonForest, "crimson_forest", temperature: 2, downfall: 0, fogTint: 0x330303,
              top: cell(B.crimson_nylium), under: rack, underwaterTop: rack,
              features: new List<string> { "huge_fungus:crimson:8", "nether_vegetation:crimson:40", "weeping_vines:30", "glowstone_cluster:8", "lava_spring:4" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)> { ("zombified_piglin", 1, 2, 4), ("hoglin", 9, 3, 4), ("piglin", 5, 3, 4) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("strider", 60, 1, 2) }, ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "nether");
        biome(Biome.warpedForest, "warped_forest", temperature: 2, downfall: 0, fogTint: 0x0a1b1b,
              top: cell(B.warped_nylium), under: rack, underwaterTop: rack,
              features: new List<string> { "huge_fungus:warped:8", "nether_vegetation:warped:40", "twisting_vines:20", "glowstone_cluster:8" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)> { ("enderman", 1, 4, 4) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("strider", 60, 1, 2) }, ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "nether");
        biome(Biome.soulSandValley, "soul_sand_valley", temperature: 2, downfall: 0, fogTint: 0x1b4745,
              top: cell(B.soul_sand), under: cell(B.soul_soil), underwaterTop: cell(B.soul_sand),
              features: new List<string> { "basalt_pillar:4", "fire_patch_soul:6", "glowstone_cluster:6", "bone_spire:2" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)> { ("skeleton", 20, 5, 5), ("ghast", 50, 4, 4), ("enderman", 1, 4, 4) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("strider", 60, 1, 2) }, ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "nether");
        biome(Biome.basaltDeltas, "basalt_deltas", temperature: 2, downfall: 0, fogTint: 0x685f70,
              top: cell(B.basalt), under: cell(B.blackstone), underwaterTop: cell(B.basalt),
              features: new List<string> { "delta:12", "basalt_column:8", "magma_blob:8", "lava_spring:8", "glowstone_cluster:4" },
              monsters: new List<(string mob, double weight, int minPack, int maxPack)> { ("ghast", 40, 1, 1), ("magma_cube", 100, 2, 5) },
              creatures: new List<(string mob, double weight, int minPack, int maxPack)> { ("strider", 60, 1, 2) }, ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "nether");

        // end
        ushort endStone = cell(B.end_stone);
        void endBiome(Biome id, string name, List<string> features = null)
        {
            biome(id, name, temperature: 0.5, downfall: 0.5, fogTint: 0xa080a0,
                  top: endStone, under: endStone, underwaterTop: endStone,
                  features: features ?? new List<string>(),
                  monsters: new List<(string mob, double weight, int minPack, int maxPack)> { ("enderman", 10, 4, 4) },
                  creatures: new List<(string mob, double weight, int minPack, int maxPack)>(),
                  ambient: new List<(string mob, double weight, int minPack, int maxPack)>(), mood: "end");
        }
        endBiome(Biome.theEnd, "the_end");
        endBiome(Biome.endHighlands, "end_highlands", features: new List<string> { "chorus:6" });
        endBiome(Biome.endMidlands, "end_midlands");
        endBiome(Biome.smallEndIslands, "small_end_islands");
        endBiome(Biome.endBarrens, "end_barrens");
    }

    // MARK: - climate → biome

    public static double peaksValleys(double w)
    {
        return -(System.Math.Abs(System.Math.Abs(w * 3) - 2) - 1);
    }

    public static Biome selectBiome(Climate cl)
    {
        double t = cl.t, h = cl.h, c = cl.c, e = cl.e, pv = cl.pv, rare = cl.rare;

        if (c < -0.74 && rare > 0.93) return Biome.mushroomFields;

        if (c < -0.19)
        {
            bool deep = c < -0.45;
            if (t < -0.45) return deep ? Biome.deepFrozenOcean : Biome.frozenOcean;
            if (t < -0.15) return deep ? Biome.deepColdOcean : Biome.coldOcean;
            if (t < 0.4) return deep ? Biome.deepOcean : Biome.ocean;
            if (t < 0.7 || deep) return deep ? Biome.deepLukewarmOcean : Biome.lukewarmOcean;
            return Biome.warmOcean;
        }

        if (pv < -0.78 && e > -0.4) return t < -0.45 ? Biome.frozenRiver : Biome.river;

        if (c < -0.11 && pv < 0.2 && e > -0.2)
        {
            if (t < -0.45) return Biome.snowyBeach;
            if (e < 0.1 && t < 0.2) return Biome.stonyShore;
            return Biome.beach;
        }

        if (e < -0.375 && pv > 0.3)
        {
            if (pv > 0.7)
            {
                if (t < -0.2) return rare > 0.5 ? Biome.frozenPeaks : Biome.jaggedPeaks;
                if (t > 0.55) return Biome.stonyPeaks;
                return rare > 0.5 ? Biome.frozenPeaks : Biome.jaggedPeaks;
            }
            if (t < -0.1) return h > 0.1 ? Biome.grove : Biome.snowySlopes;
            if (h < -0.25 && t > 0.1 && rare > 0.55) return Biome.cherryGrove;
            return Biome.meadow;
        }

        if (t > 0.55 && h < -0.35 && e < 0.05)
        {
            if (h < -0.6) return rare > 0.6 ? Biome.erodedBadlands : Biome.badlands;
            return Biome.woodedBadlands;
        }

        if (e < -0.22 && pv > 0.05 && t < 0.3 && t > -0.45)
        {
            if (rare > 0.85) return Biome.windsweptSavanna;
            if (h < -0.3) return Biome.windsweptGravellyHills;
            if (h > 0.3) return Biome.windsweptForest;
            return Biome.windsweptHills;
        }

        if (e > 0.55 && h > 0.1 && c < 0.35)
        {
            if (t > 0.35) return Biome.mangroveSwamp;
            if (t > -0.1) return Biome.swamp;
        }

        if (t < -0.45)
        {
            if (h > 0.3) return Biome.snowyTaiga;
            return rare > 0.92 ? Biome.iceSpikes : Biome.snowyPlains;
        }
        if (t < -0.15)
        {
            if (h > 0.3) return rare > 0.8 ? (rare > 0.9 ? Biome.oldGrowthSpruceTaiga : Biome.oldGrowthPineTaiga) : Biome.taiga;
            if (h > -0.1) return Biome.taiga;
            return Biome.plains;
        }
        if (t < 0.2)
        {
            if (h < -0.35) return rare > 0.9 ? Biome.sunflowerPlains : Biome.plains;
            if (h < -0.1) return rare > 0.88 ? Biome.flowerForest : Biome.plains;
            if (h < 0.1) return Biome.forest;
            if (h < 0.3) return rare > 0.85 ? Biome.oldGrowthBirchForest : Biome.birchForest;
            return Biome.darkForest;
        }
        if (t < 0.55)
        {
            if (h < -0.35) return Biome.plains;
            if (h < -0.1) return rare > 0.88 ? Biome.flowerForest : Biome.forest;
            if (h < 0.3) return Biome.forest;
            return rare > 0.6 ? Biome.bambooJungle : Biome.sparseJungle;
        }
        if (h < -0.35) return Biome.desert;
        if (h < -0.1) return e < -0.1 ? Biome.savannaPlateau : Biome.savanna;
        if (h < 0.25) return Biome.sparseJungle;
        return rare > 0.7 ? Biome.bambooJungle : Biome.jungle;
    }

    public static BiomeDef biomeDef(int b)
    {
        if (b >= 0 && b < BIOMES.Count && BIOMES[b] != null) return BIOMES[b];
        return BIOMES[(int)Biome.plains];
    }

    public static double temperatureAt(int b, int y)
    {
        double @base = biomeDef(b).temperature;
        // vanilla altitude lapse is 0.00125/block above y80 — the stray ×8 put
        // snow lines at y85-90 and froze biomes that should never see snow
        if (y > 80) return @base - (double)(y - 80) * 0.00125;
        return @base;
    }
    public static bool snowsAt(int b, int y) => temperatureAt(b, y) < 0.15;
    public static bool isOceanBiome(int b) => b <= (int)Biome.warmOcean;
    public static bool isCaveBiome(int b)
    {
        return b == (int)Biome.dripstoneCaves || b == (int)Biome.lushCaves || b == (int)Biome.deepDark;
    }
}

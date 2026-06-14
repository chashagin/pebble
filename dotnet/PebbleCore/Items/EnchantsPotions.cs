// Enchantments (the frozen baseline) and status effects + the full brewing
// graph (the frozen baseline).
//
// Ported from Sources/PebbleCore/Items/EnchantsPotions.swift.
//
// Defines static class `EnchantsPotionsGlobals` (module-global lets/funcs):
//   ENCHANTMENTS, ENCH_BY_ID, enchDef, compatible, appliesTo, enchantability,
//   EFFECTS, EFFECT_BY_ID, effectDef, POTIONS, POTION_BY_ID, potionDef,
//   BREW_RECIPES, findBrew, isBrewIngredient.
// (ench, enchF, MINUTE, potion, fx are module-private — kept private inside.)
//
// Namespace-level public types: EnchantmentDef, EffectDef, ActiveEffect,
//   PotionEffectSpec, PotionDef, BrewRecipe.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public struct EnchantmentDef
{
    public readonly string id;
    public readonly string displayName;
    public readonly int maxLevel;
    public readonly int weight;             // 10 common, 5 uncommon, 2 rare, 1 very rare
    public readonly string target;
    public readonly bool treasure;
    public readonly bool curse;
    public readonly bool tradeable;
    /// enchanting power window for level L (1-based)
    public readonly Func<int, int> minPower;
    public readonly Func<int, int> maxPower;
    public readonly string exclusiveGroup;

    public EnchantmentDef(string id, string displayName, int maxLevel, int weight, string target,
                          bool treasure, bool curse, bool tradeable,
                          Func<int, int> minPower, Func<int, int> maxPower, string exclusiveGroup)
    {
        this.id = id;
        this.displayName = displayName;
        this.maxLevel = maxLevel;
        this.weight = weight;
        this.target = target;
        this.treasure = treasure;
        this.curse = curse;
        this.tradeable = tradeable;
        this.minPower = minPower;
        this.maxPower = maxPower;
        this.exclusiveGroup = exclusiveGroup;
    }
}

// =============================================================================
// Status effects + potions
// =============================================================================
public struct EffectDef
{
    public readonly string id;
    public readonly string displayName;
    public readonly int color;
    public readonly bool beneficial;
    public readonly bool instant;

    public EffectDef(string id, string displayName, int color, bool beneficial, bool instant = false)
    {
        this.id = id;
        this.displayName = displayName;
        this.color = color;
        this.beneficial = beneficial;
        this.instant = instant;
    }
}

public struct ActiveEffect : IEquatable<ActiveEffect>
{
    public string id;
    public int duration;    // ticks remaining (-1 infinite)
    public int amplifier;   // 0 = level I
    public bool? ambient;
    public bool? showParticles;

    public ActiveEffect(string id, int duration, int amplifier, bool? ambient = null, bool? showParticles = null)
    {
        this.id = id;
        this.duration = duration;
        this.amplifier = amplifier;
        this.ambient = ambient;
        this.showParticles = showParticles;
    }

    public bool Equals(ActiveEffect other)
        => id == other.id && duration == other.duration && amplifier == other.amplifier
            && ambient == other.ambient && showParticles == other.showParticles;
    public override bool Equals(object obj) => obj is ActiveEffect o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(id, duration, amplifier, ambient, showParticles);
    public static bool operator ==(ActiveEffect a, ActiveEffect b) => a.Equals(b);
    public static bool operator !=(ActiveEffect a, ActiveEffect b) => !a.Equals(b);
}

public struct PotionEffectSpec
{
    public readonly string effect;
    public readonly int duration;
    public readonly int amplifier;

    public PotionEffectSpec(string effect, int duration, int amplifier)
    {
        this.effect = effect;
        this.duration = duration;
        this.amplifier = amplifier;
    }
}

public struct PotionDef
{
    public readonly string id;
    public readonly string displayName;
    public readonly int color;
    public readonly List<PotionEffectSpec> effects;

    public PotionDef(string id, string displayName, int color, List<PotionEffectSpec> effects)
    {
        this.id = id;
        this.displayName = displayName;
        this.color = color;
        this.effects = effects;
    }
}

// brewing: base potion + ingredient → result
public struct BrewRecipe
{
    public readonly string @base;
    public readonly string ingredient;
    public readonly string result;

    public BrewRecipe(string @base, string ingredient, string result)
    {
        this.@base = @base;
        this.ingredient = ingredient;
        this.result = result;
    }
}

public static class EnchantsPotionsGlobals
{
    private static EnchantmentDef ench(string id, string displayName, int maxLevel, int weight, string target,
                                       Func<int, int> minP, int span,
                                       bool treasure = false, bool curse = false, string group = null, bool tradeable = true)
        => new EnchantmentDef(id: id, displayName: displayName, maxLevel: maxLevel, weight: weight, target: target,
                              treasure: treasure, curse: curse, tradeable: tradeable,
                              minPower: minP, maxPower: l => minP(l) + span, exclusiveGroup: group);

    private static EnchantmentDef enchF(string id, string displayName, int maxLevel, int weight, string target,
                                        Func<int, int> minP, Func<int, int> maxP,
                                        bool treasure = false, bool curse = false, string group = null, bool tradeable = true)
        => new EnchantmentDef(id: id, displayName: displayName, maxLevel: maxLevel, weight: weight, target: target,
                              treasure: treasure, curse: curse, tradeable: tradeable,
                              minPower: minP, maxPower: maxP, exclusiveGroup: group);

    public static readonly List<EnchantmentDef> ENCHANTMENTS = new()
    {
        // armor
        ench("protection", "Protection", 4, 10, "armor", l => 1 + (l - 1) * 11, 11, group: "protection"),
        ench("fire_protection", "Fire Protection", 4, 5, "armor", l => 10 + (l - 1) * 8, 8, group: "protection"),
        ench("feather_falling", "Feather Falling", 4, 5, "armor_feet", l => 5 + (l - 1) * 6, 6),
        ench("blast_protection", "Blast Protection", 4, 2, "armor", l => 5 + (l - 1) * 8, 8, group: "protection"),
        ench("projectile_protection", "Projectile Protection", 4, 5, "armor", l => 3 + (l - 1) * 6, 6, group: "protection"),
        ench("respiration", "Respiration", 3, 2, "armor_head", l => 10 * l, 30),
        ench("aqua_affinity", "Aqua Affinity", 1, 2, "armor_head", _ => 1, 40),
        ench("thorns", "Thorns", 3, 1, "armor", l => 10 + 20 * (l - 1), 50),
        ench("depth_strider", "Depth Strider", 3, 2, "armor_feet", l => 10 * l, 15, group: "boots_move"),
        ench("frost_walker", "Frost Walker", 2, 2, "armor_feet", l => 10 * l, 15, treasure: true, group: "boots_move"),
        ench("curse_of_binding", "Curse of Binding", 1, 1, "wearable", _ => 25, 25, treasure: true, curse: true),
        ench("soul_speed", "Soul Speed", 3, 1, "armor_feet", l => 10 * l, 15, treasure: true, tradeable: false),
        ench("swift_sneak", "Swift Sneak", 3, 1, "armor_legs", l => 25 * l, 50, treasure: true, tradeable: false),
        // sword
        ench("sharpness", "Sharpness", 5, 10, "sword", l => 1 + (l - 1) * 11, 20, group: "damage"),
        ench("smite", "Smite", 5, 5, "sword", l => 5 + (l - 1) * 8, 20, group: "damage"),
        ench("bane_of_arthropods", "Bane of Arthropods", 5, 5, "sword", l => 5 + (l - 1) * 8, 20, group: "damage"),
        ench("knockback", "Knockback", 2, 5, "sword", l => 5 + 20 * (l - 1), 50),
        ench("fire_aspect", "Fire Aspect", 2, 2, "sword", l => 10 + 20 * (l - 1), 50),
        ench("looting", "Looting", 3, 2, "sword", l => 15 + (l - 1) * 9, 50),
        ench("sweeping_edge", "Sweeping Edge", 3, 2, "sword", l => 5 + (l - 1) * 9, 15),
        // tools
        ench("efficiency", "Efficiency", 5, 10, "digger", l => 1 + 10 * (l - 1), 50),
        ench("silk_touch", "Silk Touch", 1, 1, "digger", _ => 15, 50, group: "silk_fortune"),
        ench("unbreaking", "Unbreaking", 3, 5, "breakable", l => 5 + (l - 1) * 8, 50),
        ench("fortune", "Fortune", 3, 2, "digger", l => 15 + (l - 1) * 9, 50, group: "silk_fortune"),
        // bow
        ench("power", "Power", 5, 10, "bow", l => 1 + (l - 1) * 10, 15),
        ench("punch", "Punch", 2, 2, "bow", l => 12 + (l - 1) * 20, 25),
        ench("flame", "Flame", 1, 2, "bow", _ => 20, 30),
        ench("infinity", "Infinity", 1, 1, "bow", _ => 20, 30, group: "inf_mend"),
        // fishing
        ench("luck_of_the_sea", "Luck of the Sea", 3, 2, "fishing_rod", l => 15 + (l - 1) * 9, 50),
        ench("lure", "Lure", 3, 2, "fishing_rod", l => 15 + (l - 1) * 9, 50),
        // trident
        enchF("loyalty", "Loyalty", 3, 5, "trident", l => 5 + 7 * l, _ => 50, group: "riptide_x"),
        ench("impaling", "Impaling", 5, 2, "trident", l => 1 + (l - 1) * 8, 20),
        enchF("riptide", "Riptide", 3, 2, "trident", l => 10 + 7 * l, _ => 50, group: "riptide"),
        ench("channeling", "Channeling", 1, 1, "trident", _ => 25, 25, group: "riptide_x"),
        // crossbow
        ench("multishot", "Multishot", 1, 2, "crossbow", _ => 20, 30, group: "multi_pierce"),
        ench("quick_charge", "Quick Charge", 3, 5, "crossbow", l => 12 + (l - 1) * 20, 50),
        ench("piercing", "Piercing", 4, 10, "crossbow", l => 1 + (l - 1) * 10, 50, group: "multi_pierce"),
        // universal
        ench("mending", "Mending", 1, 2, "breakable", _ => 25, 50, treasure: true, group: "inf_mend"),
        ench("curse_of_vanishing", "Curse of Vanishing", 1, 1, "vanishable", _ => 25, 25, treasure: true, curse: true),
    };

    public static readonly Dictionary<string, EnchantmentDef> ENCH_BY_ID =
        ENCHANTMENTS.ToDictionary(e => e.id, e => e);

    public static EnchantmentDef enchDef(string id)
    {
        if (!ENCH_BY_ID.TryGetValue(id, out var e)) throw new InvalidOperationException($"unknown enchantment: {id}");
        return e;
    }

    public static bool compatible(EnchantmentDef a, EnchantmentDef b)
    {
        if (a.id == b.id) return false;
        if (a.exclusiveGroup != null && a.exclusiveGroup == b.exclusiveGroup) return false;
        var pair = new HashSet<string> { a.exclusiveGroup, b.exclusiveGroup };
        if (pair.Contains("riptide") && pair.Contains("riptide_x")) return false;
        return true;
    }

    public static bool appliesTo(EnchantmentDef e, ItemDef item)
    {
        var t = item.tool;
        var a = item.armor;
        switch (e.target)
        {
            case "armor": return a != null && a.Value.material != "elytra";
            case "armor_head": return a?.slot == 0;
            case "armor_chest": return a?.slot == 1 && a?.material != "elytra";
            case "armor_legs": return a?.slot == 2;
            case "armor_feet": return a?.slot == 3;
            case "sword": return t?.type == "sword";
            case "digger": return t?.type == "pickaxe" || t?.type == "axe" || t?.type == "shovel" || t?.type == "hoe";
            case "axe": return t?.type == "axe";
            case "bow": return t?.type == "bow";
            case "crossbow": return t?.type == "crossbow";
            case "trident": return t?.type == "trident";
            case "fishing_rod": return t?.type == "fishing_rod";
            case "breakable": return t != null || a != null || item.name == "shield";
            case "wearable": return a != null;
            case "vanishable": return t != null || a != null || item.name == "shield" || item.name == "compass";
            default: return false;
        }
    }

    public static int enchantability(ItemDef item)
        => item.tool?.enchantability ?? item.armor?.enchantability ?? 1;

    public static readonly List<EffectDef> EFFECTS = new()
    {
        new EffectDef("speed", "Speed", 0x33ebff, true),
        new EffectDef("slowness", "Slowness", 0x8bafe0, false),
        new EffectDef("haste", "Haste", 0xd9c043, true),
        new EffectDef("mining_fatigue", "Mining Fatigue", 0x4a4217, false),
        new EffectDef("strength", "Strength", 0xffc700, true),
        new EffectDef("instant_health", "Instant Health", 0xf82423, true, true),
        new EffectDef("instant_damage", "Instant Damage", 0xa9656a, false, true),
        new EffectDef("jump_boost", "Jump Boost", 0xfdff84, true),
        new EffectDef("nausea", "Nausea", 0x551d4a, false),
        new EffectDef("regeneration", "Regeneration", 0xcd5cab, true),
        new EffectDef("resistance", "Resistance", 0x9146f0, true),
        new EffectDef("fire_resistance", "Fire Resistance", 0xff9900, true),
        new EffectDef("water_breathing", "Water Breathing", 0x98dac0, true),
        new EffectDef("invisibility", "Invisibility", 0xf6f6f6, true),
        new EffectDef("blindness", "Blindness", 0x1f1f23, false),
        new EffectDef("night_vision", "Night Vision", 0xc2ff66, true),
        new EffectDef("hunger", "Hunger", 0x587653, false),
        new EffectDef("weakness", "Weakness", 0x484d48, false),
        new EffectDef("poison", "Poison", 0x87a363, false),
        new EffectDef("wither", "Wither", 0x736156, false),
        new EffectDef("health_boost", "Health Boost", 0xf87d23, true),
        new EffectDef("absorption", "Absorption", 0x2552a5, true),
        new EffectDef("saturation", "Saturation", 0xf82423, true, true),
        new EffectDef("glowing", "Glowing", 0x94a061, false),
        new EffectDef("levitation", "Levitation", 0xceffff, false),
        new EffectDef("slow_falling", "Slow Falling", 0xf3cfb9, true),
        new EffectDef("conduit_power", "Conduit Power", 0x1dc2d1, true),
        new EffectDef("dolphins_grace", "Dolphin's Grace", 0x88a3be, true),
        new EffectDef("bad_omen", "Bad Omen", 0x0b6138, false),
        new EffectDef("hero_of_the_village", "Hero of the Village", 0x44ff44, true),
        new EffectDef("darkness", "Darkness", 0x292721, false),
    };

    public static readonly Dictionary<string, EffectDef> EFFECT_BY_ID =
        EFFECTS.ToDictionary(e => e.id, e => e);

    public static EffectDef effectDef(string id)
    {
        if (!EFFECT_BY_ID.TryGetValue(id, out var e)) throw new InvalidOperationException($"unknown effect: {id}");
        return e;
    }

    private const int MINUTE = 1200; // 1 minute in ticks

    private static PotionDef potion(string id, string displayName, int color, List<PotionEffectSpec> effects = null)
        => new PotionDef(id: id, displayName: displayName, color: color, effects: effects ?? new List<PotionEffectSpec>());

    private static PotionEffectSpec fx(string e, int d, int a) => new PotionEffectSpec(effect: e, duration: d, amplifier: a);

    public static readonly List<PotionDef> POTIONS = new()
    {
        potion("water", "Water Bottle", 0x385dc6),
        potion("mundane", "Mundane Potion", 0x385dc6),
        potion("thick", "Thick Potion", 0x385dc6),
        potion("awkward", "Awkward Potion", 0x385dc6),
        potion("night_vision", "Potion of Night Vision", 0x1f1fa1, new() { fx("night_vision", 3 * MINUTE, 0) }),
        potion("long_night_vision", "Potion of Night Vision", 0x1f1fa1, new() { fx("night_vision", 8 * MINUTE, 0) }),
        potion("invisibility", "Potion of Invisibility", 0x7f8392, new() { fx("invisibility", 3 * MINUTE, 0) }),
        potion("long_invisibility", "Potion of Invisibility", 0x7f8392, new() { fx("invisibility", 8 * MINUTE, 0) }),
        potion("leaping", "Potion of Leaping", 0x22ff4c, new() { fx("jump_boost", 3 * MINUTE, 0) }),
        potion("long_leaping", "Potion of Leaping", 0x22ff4c, new() { fx("jump_boost", 8 * MINUTE, 0) }),
        potion("strong_leaping", "Potion of Leaping II", 0x22ff4c, new() { fx("jump_boost", 90 * 20, 1) }),
        potion("fire_resistance", "Potion of Fire Resistance", 0xe49a3a, new() { fx("fire_resistance", 3 * MINUTE, 0) }),
        potion("long_fire_resistance", "Potion of Fire Resistance", 0xe49a3a, new() { fx("fire_resistance", 8 * MINUTE, 0) }),
        potion("swiftness", "Potion of Swiftness", 0x7cafc6, new() { fx("speed", 3 * MINUTE, 0) }),
        potion("long_swiftness", "Potion of Swiftness", 0x7cafc6, new() { fx("speed", 8 * MINUTE, 0) }),
        potion("strong_swiftness", "Potion of Swiftness II", 0x7cafc6, new() { fx("speed", 90 * 20, 1) }),
        potion("slowness", "Potion of Slowness", 0x5a6c81, new() { fx("slowness", 90 * 20, 0) }),
        potion("long_slowness", "Potion of Slowness", 0x5a6c81, new() { fx("slowness", 4 * MINUTE, 0) }),
        potion("strong_slowness", "Potion of Slowness IV", 0x5a6c81, new() { fx("slowness", 20 * 20, 3) }),
        potion("water_breathing", "Potion of Water Breathing", 0x2e5299, new() { fx("water_breathing", 3 * MINUTE, 0) }),
        potion("long_water_breathing", "Potion of Water Breathing", 0x2e5299, new() { fx("water_breathing", 8 * MINUTE, 0) }),
        potion("healing", "Potion of Healing", 0xf82423, new() { fx("instant_health", 1, 0) }),
        potion("strong_healing", "Potion of Healing II", 0xf82423, new() { fx("instant_health", 1, 1) }),
        potion("harming", "Potion of Harming", 0x430a09, new() { fx("instant_damage", 1, 0) }),
        potion("strong_harming", "Potion of Harming II", 0x430a09, new() { fx("instant_damage", 1, 1) }),
        potion("poison", "Potion of Poison", 0x4e9331, new() { fx("poison", 45 * 20, 0) }),
        potion("long_poison", "Potion of Poison", 0x4e9331, new() { fx("poison", 90 * 20, 0) }),
        potion("strong_poison", "Potion of Poison II", 0x4e9331, new() { fx("poison", 21 * 20 + 12, 1) }),
        potion("regeneration", "Potion of Regeneration", 0xcd5cab, new() { fx("regeneration", 45 * 20, 0) }),
        potion("long_regeneration", "Potion of Regeneration", 0xcd5cab, new() { fx("regeneration", 90 * 20, 0) }),
        potion("strong_regeneration", "Potion of Regeneration II", 0xcd5cab, new() { fx("regeneration", 22 * 20 + 10, 1) }),
        potion("strength", "Potion of Strength", 0xffc700, new() { fx("strength", 3 * MINUTE, 0) }),
        potion("long_strength", "Potion of Strength", 0xffc700, new() { fx("strength", 8 * MINUTE, 0) }),
        potion("strong_strength", "Potion of Strength II", 0xffc700, new() { fx("strength", 90 * 20, 1) }),
        potion("weakness", "Potion of Weakness", 0x484d48, new() { fx("weakness", 90 * 20, 0) }),
        potion("long_weakness", "Potion of Weakness", 0x484d48, new() { fx("weakness", 4 * MINUTE, 0) }),
        potion("slow_falling", "Potion of Slow Falling", 0xf3cfb9, new() { fx("slow_falling", 90 * 20, 0) }),
        potion("long_slow_falling", "Potion of Slow Falling", 0xf3cfb9, new() { fx("slow_falling", 4 * MINUTE, 0) }),
        potion("turtle_master", "Potion of the Turtle Master", 0x7691c9, new() { fx("slowness", 20 * 20, 3), fx("resistance", 20 * 20, 2) }),
        potion("long_turtle_master", "Potion of the Turtle Master", 0x7691c9, new() { fx("slowness", 40 * 20, 3), fx("resistance", 40 * 20, 2) }),
        potion("strong_turtle_master", "Potion of the Turtle Master II", 0x7691c9, new() { fx("slowness", 20 * 20, 5), fx("resistance", 20 * 20, 3) }),
    };

    public static readonly Dictionary<string, PotionDef> POTION_BY_ID =
        POTIONS.ToDictionary(p => p.id, p => p);

    public static PotionDef potionDef(string id) => POTION_BY_ID.TryGetValue(id, out var p) ? p : POTIONS[0];

    public static readonly List<BrewRecipe> BREW_RECIPES = BuildBrewRecipes();

    private static List<BrewRecipe> BuildBrewRecipes()
    {
        var output = new List<BrewRecipe>();
        void brew(string @base, string ingredient, string result)
        {
            output.Add(new BrewRecipe(@base: @base, ingredient: ingredient, result: result));
        }
        brew("water", "nether_wart", "awkward");
        brew("water", "glowstone_dust", "thick");
        brew("water", "redstone", "mundane");
        brew("water", "fermented_spider_eye", "weakness");
        brew("awkward", "golden_carrot", "night_vision");
        brew("awkward", "rabbit_foot", "leaping");
        brew("awkward", "magma_cream", "fire_resistance");
        brew("awkward", "sugar", "swiftness");
        brew("awkward", "pufferfish", "water_breathing");
        brew("awkward", "glistering_melon_slice", "healing");
        brew("awkward", "spider_eye", "poison");
        brew("awkward", "ghast_tear", "regeneration");
        brew("awkward", "blaze_powder", "strength");
        brew("awkward", "phantom_membrane", "slow_falling");
        brew("awkward", "turtle_helmet", "turtle_master");
        // corruptions
        brew("night_vision", "fermented_spider_eye", "invisibility");
        brew("long_night_vision", "fermented_spider_eye", "long_invisibility");
        brew("swiftness", "fermented_spider_eye", "slowness");
        brew("long_swiftness", "fermented_spider_eye", "long_slowness");
        brew("strong_swiftness", "fermented_spider_eye", "strong_slowness");
        brew("leaping", "fermented_spider_eye", "slowness");
        brew("healing", "fermented_spider_eye", "harming");
        brew("strong_healing", "fermented_spider_eye", "strong_harming");
        brew("poison", "fermented_spider_eye", "harming");
        brew("long_poison", "fermented_spider_eye", "harming");
        brew("strong_poison", "fermented_spider_eye", "strong_harming");
        // redstone extensions
        foreach (var p in new[] { "night_vision", "invisibility", "leaping", "fire_resistance", "swiftness", "slowness", "water_breathing", "poison", "regeneration", "strength", "weakness", "slow_falling", "turtle_master" })
        {
            brew(p, "redstone", $"long_{p}");
        }
        // glowstone strengthening
        foreach (var p in new[] { "leaping", "swiftness", "healing", "harming", "poison", "regeneration", "strength", "slowness", "turtle_master" })
        {
            brew(p, "glowstone_dust", $"strong_{p}");
        }
        return output;
    }

    public static string findBrew(string @base, string ingredient)
    {
        foreach (var r in BREW_RECIPES)
        {
            if (r.@base == @base && r.ingredient == ingredient) return r.result;
        }
        return null;
    }

    public static bool isBrewIngredient(string item)
    {
        if (item == "gunpowder" || item == "dragon_breath") return true;
        foreach (var r in BREW_RECIPES)
        {
            if (r.ingredient == item) return true;
        }
        return false;
    }
}

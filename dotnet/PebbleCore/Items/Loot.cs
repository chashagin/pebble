// Loot tables — Structure chests, fishing, piglin
// bartering, archaeology, sniffer digging. Mob drops live with mob defs.
//
// RNG order is contract: pickWeighted consumes one float even for 'nothing';
// the count roll consumes one float even when min/max are absent (deterministic computes
// 1 + nextInt(1)); the enchant level rolls BEFORE enchantStackRandomly runs.
//
// Module-global free funcs/vars for this file live in static class LootGlobals
// (surface via `global using static PebbleCore.LootGlobals`).
// Types LootEntry and LootPool are namespace-level.

namespace PebbleCore;

public struct LootEntry
{
    public string item;
    public double weight;
    public int? min;
    public int? max;
    /// enchant with level in [a,b]; 'book' entries become enchanted books
    public (int, int)? enchant;
    /// stack data payload (potion id etc.)
    public string dataPotion;

    public LootEntry(string item, double weight, int? min, int? max, (int, int)? enchant, string dataPotion)
    {
        this.item = item;
        this.weight = weight;
        this.min = min;
        this.max = max;
        this.enchant = enchant;
        this.dataPotion = dataPotion;
    }
}

public struct LootPool
{
    public (int, int) rolls;
    public List<LootEntry> entries;
    public double? bonusRollsPerLuck;

    public LootPool((int, int) rolls, List<LootEntry> entries, double? bonusRollsPerLuck)
    {
        this.rolls = rolls;
        this.entries = entries;
        this.bonusRollsPerLuck = bonusRollsPerLuck;
    }
}

public static class LootGlobals
{
    public static Dictionary<string, List<LootPool>> LOOT_TABLES = new Dictionary<string, List<LootPool>>();
    private static List<string> lootTableOrder = new List<string>();

    private static void table(string name, params LootPool[] pools)
    {
        LOOT_TABLES[name] = new List<LootPool>(pools);
        lootTableOrder.Add(name);
    }

    private static LootPool pool(int a, int b, List<LootEntry> entries)
    {
        return new LootPool((a, b), entries, null);
    }

    /// [name, weight] tuple form
    private static LootEntry E(string item, double w)
    {
        return new LootEntry(item, w, null, null, null, null);
    }

    /// [name, weight, min, max] tuple form
    private static LootEntry E(string item, double w, int mn, int mx)
    {
        return new LootEntry(item, w, mn, mx, null, null);
    }

    /// object form
    private static LootEntry E(string item, double w, int? min = null, int? max = null,
                              (int, int)? enchant = null, string potion = null)
    {
        return new LootEntry(item, w, min, max, enchant, potion);
    }

    // MARK: - rolling

    /// random enchantment application for loot
    public static ItemStack enchantStackRandomly(ItemStack s, ref RandomX rng, int level)
    {
        var def = itemDef(s.id);
        bool isBook = def.name == "book" || def.name == "enchanted_book";
        var candidates = ENCHANTMENTS.Where(e => !e.curse && (isBook || appliesTo(e, def))).ToList();
        if (candidates.Count == 0) { return s; }
        var picked = new List<EnchInstance>();
        int l = level;
        var first = candidates[rng.nextInt(candidates.Count)];
        int flv = 1;
        int i = first.maxLevel;
        while (i >= 1)
        {
            if (l >= first.minPower(i)) { flv = i; break; }
            i -= 1;
        }
        picked.Add(new EnchInstance(first.id, flv));
        while (rng.nextFloat() < (double)(level + 1) / 50.0 && picked.Count < 4)
        {
            var remaining = candidates.Where(c =>
                picked.All(p =>
                {
                    var pd = ENCH_BY_ID[p.id];
                    return compatible(c, pd);
                })).ToList();
            if (remaining.Count == 0) { break; }
            var next = remaining[rng.nextInt(remaining.Count)];
            int nlv = 1;
            int j = next.maxLevel;
            while (j >= 1)
            {
                if (l >= next.minPower(j)) { nlv = j; break; }
                j -= 1;
            }
            picked.Add(new EnchInstance(next.id, nlv));
            l = l / 2;
        }
        if (isBook) { return new ItemStack(iid("enchanted_book"), 1, ench: picked); }
        var @out = s;
        @out.ench = picked;
        return @out;
    }

    public static List<ItemStack> rollLoot(string name, ref RandomX rng, double luck = 0)
    {
        if (!LOOT_TABLES.TryGetValue(name, out var t)) { return new List<ItemStack>(); }
        var @out = new List<ItemStack>();
        foreach (var p in t)
        {
            int rolls = p.rolls.Item1 + rng.nextInt(p.rolls.Item2 - p.rolls.Item1 + 1);
            if (p.bonusRollsPerLuck is double bonus) { rolls += (int)Math.Floor(bonus * luck); }
            int r = 0;
            while (r < rolls)
            {
                r += 1;
                var e = rng.pickWeighted(p.entries, x => x.weight);
                if (e.item == "nothing") { continue; }
                int mn = e.min ?? 1;
                int mx = e.max ?? e.min ?? 1;
                int count = mn + rng.nextInt(mx - mn + 1);
                if (count <= 0) { continue; }
                var s = new ItemStack(iid(e.item), count);
                if (e.dataPotion is string pot) { s.data.potion = pot; }
                if (e.enchant is (int ea, int eb))
                {
                    int lvl = ea + rng.nextInt(eb - ea + 1);
                    s = enchantStackRandomly(s, ref rng, lvl);
                }
                @out.Add(s);
            }
        }
        return @out;
    }

    public static bool hasLootTable(string name) => LOOT_TABLES.ContainsKey(name);
    public static List<string> allLootTables() => lootTableOrder;

    // MARK: - tables

    private static bool lootRegistered = false;

    public static void registerAllLootTables()
    {
        if (lootRegistered) { return; }
        lootRegistered = true;
        if (itemDefs.Count == 0) { throw new InvalidOperationException("registerAllItems() must run first"); }

        // -----------------------------------------------------------------------
        // Chest tables
        // -----------------------------------------------------------------------
        table("dungeon",
            pool(1, 3, new List<LootEntry> {
                E("saddle", 20), E("golden_apple", 15),
                E("enchanted_golden_apple", 2), E("music_disc_wander", 4),
                E("music_disc_aurora", 4), E("name_tag", 20),
                E("golden_horse_armor", 10), E("iron_horse_armor", 15),
                E("diamond_horse_armor", 5), E("book", 10, enchant: (20, 39)),
            }),
            pool(1, 4, new List<LootEntry> {
                E("iron_ingot", 10, 1, 4), E("gold_ingot", 5, 1, 4), E("bread", 20), E("wheat", 20, 1, 4),
                E("bucket", 10), E("redstone", 15, 1, 4), E("coal", 15, 1, 4), E("melon_seeds", 10, 2, 4),
                E("pumpkin_seeds", 10, 2, 4), E("beetroot_seeds", 10, 2, 4),
            }),
            pool(3, 3, new List<LootEntry> { E("bone", 10, 1, 8), E("gunpowder", 10, 1, 8), E("rotten_flesh", 10, 1, 8), E("string", 10, 1, 8) })
        );
        table("mineshaft",
            pool(1, 1, new List<LootEntry> {
                E("golden_apple", 20), E("enchanted_golden_apple", 1),
                E("name_tag", 30), E("book", 10, enchant: (20, 39)),
                E("nothing", 5),
            }),
            pool(2, 4, new List<LootEntry> {
                E("iron_ingot", 10, 1, 5), E("gold_ingot", 5, 1, 3), E("redstone", 5, 4, 9), E("lapis_lazuli", 5, 4, 9),
                E("diamond", 3, 1, 2), E("coal", 10, 3, 8), E("bread", 15, 1, 3), E("melon_seeds", 10, 2, 4),
                E("pumpkin_seeds", 10, 2, 4), E("beetroot_seeds", 10, 2, 4),
            }),
            pool(3, 3, new List<LootEntry> { E("rail", 20, 4, 8), E("powered_rail", 5, 1, 4), E("detector_rail", 5, 1, 4), E("activator_rail", 5, 1, 4), E("torch", 15, 1, 16) })
        );
        table("stronghold_corridor",
            pool(2, 3, new List<LootEntry> {
                E("ender_pearl", 10), E("diamond", 3, 1, 3), E("iron_ingot", 10, 1, 5), E("gold_ingot", 5, 1, 3),
                E("redstone", 5, 4, 9), E("bread", 15, 1, 3), E("apple", 15, 1, 3), E("iron_pickaxe", 5),
                E("book", 6, enchant: (20, 39)),
            })
        );
        table("stronghold_library",
            pool(2, 10, new List<LootEntry> {
                E("book", 20, 1, 3), E("paper", 20, 2, 7), E("compass", 5), E("ender_pearl", 5),
                E("book", 10, enchant: (30, 39)),
            })
        );
        table("desert_temple",
            pool(2, 4, new List<LootEntry> {
                E("diamond", 5, 1, 3), E("iron_ingot", 15, 1, 5), E("gold_ingot", 15, 2, 7), E("emerald", 15, 1, 3),
                E("bone", 25, 4, 6), E("spider_eye", 25, 1, 3), E("rotten_flesh", 25, 3, 7), E("saddle", 20),
                E("iron_horse_armor", 15), E("golden_horse_armor", 10), E("diamond_horse_armor", 5),
                E("book", 20, enchant: (20, 39)), E("golden_apple", 20),
                E("enchanted_golden_apple", 2),
            }),
            pool(4, 4, new List<LootEntry> { E("bone", 10, 1, 8), E("gunpowder", 10, 1, 8), E("rotten_flesh", 10, 1, 8), E("string", 10, 1, 8), E("sand", 10, 1, 8) })
        );
        table("jungle_temple",
            pool(2, 6, new List<LootEntry> {
                E("diamond", 3, 1, 3), E("iron_ingot", 10, 1, 5), E("gold_ingot", 15, 2, 7), E("emerald", 2, 1, 3),
                E("bone", 20, 4, 6), E("rotten_flesh", 16, 3, 7), E("saddle", 3), E("bamboo", 15, 1, 3),
                E("book", 1, enchant: (30, 39)),
            })
        );
        table("igloo",
            pool(2, 8, new List<LootEntry> {
                E("apple", 15, 1, 3), E("coal", 15, 1, 4), E("gold_nugget", 10, 1, 3), E("stone_axe", 2),
                E("rotten_flesh", 10), E("emerald", 1), E("wheat", 10, 2, 3), E("golden_apple", 1),
            })
        );
        table("pillager_outpost",
            pool(2, 3, new List<LootEntry> {
                E("crossbow", 10), E("wheat", 30, 3, 5), E("potato", 30, 2, 5), E("carrot", 30, 3, 5),
                E("dark_oak_log", 30, 2, 3), E("experience_bottle", 20), E("string", 20, 1, 6),
                E("arrow", 20, 2, 7), E("tripwire_hook", 20, 1, 3), E("iron_ingot", 20, 1, 3),
                E("book", 10, enchant: (20, 39)), E("goat_horn", 10),
            })
        );
        table("woodland_mansion",
            pool(1, 3, new List<LootEntry> {
                E("lead", 20), E("golden_apple", 15),
                E("enchanted_golden_apple", 2), E("music_disc_descent", 5),
                E("name_tag", 20), E("chainmail_chestplate", 10),
                E("diamond_hoe", 15), E("bucket", 10),
                E("book", 10, enchant: (20, 39)), E("totem_of_undying", 3),
            }),
            pool(1, 4, new List<LootEntry> {
                E("iron_ingot", 10, 1, 4), E("gold_ingot", 5, 1, 4), E("bread", 20), E("wheat", 20, 1, 4),
                E("redstone", 15, 1, 4), E("coal", 15, 1, 4), E("melon_seeds", 10, 2, 4), E("pumpkin_seeds", 10, 2, 4),
            })
        );
        table("shipwreck_treasure",
            pool(3, 6, new List<LootEntry> {
                E("iron_ingot", 90, 1, 5), E("gold_ingot", 10, 1, 5), E("emerald", 40, 1, 5), E("diamond", 5),
                E("experience_bottle", 5),
            }),
            pool(2, 5, new List<LootEntry> { E("iron_nugget", 50, 1, 10), E("gold_nugget", 10, 1, 10), E("lapis_lazuli", 20, 1, 10) })
        );
        table("shipwreck_supply",
            pool(3, 10, new List<LootEntry> {
                E("paper", 8, 1, 12), E("potato", 7, 2, 6), E("poisonous_potato", 7, 2, 6), E("carrot", 7, 4, 8),
                E("wheat", 7, 8, 21), E("suspicious_stew", 10), E("coal", 6, 2, 8),
                E("rotten_flesh", 5, 5, 24), E("gunpowder", 3, 1, 5), E("leather_helmet", 3, 1, 1),
                E("bamboo", 2, 1, 3), E("pumpkin", 2, 1, 3), E("tnt", 1, 1, 2),
            })
        );
        table("buried_treasure",
            pool(1, 1, new List<LootEntry> { E("heart_of_the_sea", 1) }),
            pool(5, 8, new List<LootEntry> {
                E("iron_ingot", 20, 1, 4), E("gold_ingot", 10, 1, 4), E("tnt", 5, 1, 2), E("emerald", 5, 4, 8),
                E("diamond", 5, 1, 2), E("prismarine_crystals", 5, 1, 5), E("leather_chestplate", 1), E("iron_sword", 1),
                E("cooked_cod", 10, 2, 4), E("cooked_salmon", 10, 2, 4),
            })
        );
        table("underwater_ruin_small",
            pool(2, 8, new List<LootEntry> {
                E("coal", 30, 1, 4), E("stone_axe", 2), E("rotten_flesh", 5), E("emerald", 1), E("wheat", 10, 2, 3),
                E("fishing_rod", 5, enchant: (20, 30)),
            })
        );
        table("underwater_ruin_big",
            pool(2, 8, new List<LootEntry> {
                E("coal", 10, 1, 4), E("gold_nugget", 10, 1, 3), E("emerald", 1), E("wheat", 10, 2, 3),
                E("fishing_rod", 5, enchant: (25, 35)), E("book", 5, enchant: (25, 35)),
                E("golden_apple", 2),
            })
        );
        table("ruined_portal",
            pool(4, 8, new List<LootEntry> {
                E("obsidian", 40, 1, 2), E("flint", 40, 1, 4), E("iron_nugget", 40, 9, 18), E("flint_and_steel", 40),
                E("fire_charge", 40), E("golden_apple", 15), E("gold_nugget", 15, 4, 24), E("golden_sword", 15),
                E("golden_axe", 15), E("golden_hoe", 15), E("golden_shovel", 15), E("golden_pickaxe", 15),
                E("golden_boots", 15), E("golden_chestplate", 15), E("golden_helmet", 15), E("golden_leggings", 15),
                E("glistering_melon_slice", 5, 4, 12), E("golden_horse_armor", 5), E("light_weighted_pressure_plate", 5),
                E("golden_carrot", 5, 4, 12), E("clock", 5), E("gold_ingot", 5, 2, 8), E("bell", 1), E("enchanted_golden_apple", 1),
                E("gold_block", 1, 1, 2),
            })
        );
        table("nether_fortress",
            pool(2, 4, new List<LootEntry> {
                E("diamond", 5, 1, 3), E("iron_ingot", 5, 1, 5), E("gold_ingot", 15, 1, 3), E("golden_sword", 5),
                E("golden_chestplate", 5), E("flint_and_steel", 5), E("nether_wart", 5, 3, 7), E("saddle", 10),
                E("golden_horse_armor", 8), E("iron_horse_armor", 5), E("diamond_horse_armor", 3),
                E("obsidian", 2, 2, 4),
            })
        );
        table("bastion_treasure",
            pool(3, 4, new List<LootEntry> {
                E("netherite_ingot", 15), E("ancient_debris", 10, min: 1, max: 2),
                E("netherite_scrap", 8), E("gold_block", 12, 2, 5), E("gold_ingot", 16, 3, 9),
                E("iron_block", 10, 2, 5), E("iron_ingot", 16, 3, 9), E("diamond", 10, 2, 6),
                E("diamond_sword", 6, enchant: (15, 35)), E("diamond_chestplate", 6, enchant: (15, 35)),
            })
        );
        table("bastion_other",
            pool(2, 4, new List<LootEntry> {
                E("netherite_upgrade", 6), E("snout_armor_trim", 4),
                E("gold_ingot", 20, 1, 6), E("iron_ingot", 20, 1, 6), E("golden_sword", 10), E("golden_boots", 8, 1, 1),
                E("crossbow", 12), E("gold_nugget", 20, 2, 8), E("string", 15, 1, 6), E("arrow", 15, 5, 17),
                E("cooked_porkchop", 15, 1, 5), E("golden_carrot", 8, 6, 17), E("gilded_blackstone", 10, 1, 5),
                E("ender_pearl", 10, 2, 4), E("obsidian", 10, 4, 6), E("chain", 12, 2, 10),
                E("magma_cream", 10, 2, 6), E("golden_apple", 8), E("spectral_arrow", 12, 10, 22),
            })
        );
        table("end_city_treasure",
            pool(2, 6, new List<LootEntry> {
                E("diamond", 5, 2, 7), E("iron_ingot", 10, 4, 8), E("gold_ingot", 15, 2, 7), E("emerald", 2, 2, 6),
                E("beetroot_seeds", 5, 1, 10), E("saddle", 3), E("iron_horse_armor", 1), E("golden_horse_armor", 1),
                E("diamond_horse_armor", 1),
                E("diamond_sword", 3, enchant: (20, 39)),
                E("diamond_boots", 3, enchant: (20, 39)),
                E("diamond_chestplate", 3, enchant: (20, 39)),
                E("diamond_leggings", 3, enchant: (20, 39)),
                E("diamond_helmet", 3, enchant: (20, 39)),
                E("diamond_pickaxe", 3, enchant: (20, 39)),
                E("diamond_shovel", 3, enchant: (20, 39)),
                E("iron_sword", 3, enchant: (20, 39)),
                E("spire_armor_trim", 2),
            })
        );
        table("ancient_city",
            pool(4, 10, new List<LootEntry> {
                E("enchanted_golden_apple", 2), E("music_disc_descent", 4),
                E("echo_shard", 6, min: 1, max: 3), E("ward_armor_trim", 2),
                E("silence_armor_trim", 1), E("name_tag", 6),
                E("book", 6, enchant: (30, 39)), E("sculk_sensor", 5, 1, 3), E("sculk_catalyst", 4, 1, 2),
                E("experience_bottle", 6, 1, 3), E("glow_berries", 5, 1, 15), E("ice", 5, 1, 6), E("soul_torch", 5, 1, 15),
                E("bone", 5, 1, 15), E("coal", 7, 6, 15), E("candle", 5, 1, 4), E("amethyst_shard", 7, 1, 15),
                E("diamond_hoe", 4, enchant: (30, 50)), E("diamond_leggings", 2, enchant: (30, 50)),
                E("lead", 5, 1, 1), E("saddle", 5), E("compass", 5, 1, 2), E("sculk", 6, 4, 10),
                E("potion", 5, potion: "strong_regeneration"),
            })
        );
        table("trail_ruins_common",
            pool(3, 5, new List<LootEntry> {
                E("emerald", 4, 1, 3), E("wheat", 10, 1, 3), E("wooden_hoe", 10), E("clay_ball", 10, 1, 3),
                E("brick", 10, 1, 3), E("yellow_dye", 10), E("blue_dye", 10), E("light_blue_dye", 10),
                E("white_dye", 10), E("orange_dye", 10), E("red_candle", 10, 1, 2), E("green_candle", 4, 1, 2),
                E("purple_candle", 4, 1, 2), E("brown_candle", 4, 1, 2), E("magenta_glazed_terracotta", 1),
                E("pink_glazed_terracotta", 1), E("blue_glazed_terracotta", 1), E("light_blue_glazed_terracotta", 1),
                E("red_glazed_terracotta", 1), E("yellow_glazed_terracotta", 1), E("purple_glazed_terracotta", 1),
                E("spruce_hanging_sign", 1), E("oak_hanging_sign", 1), E("gold_nugget", 5, 1, 5), E("coal", 10, 1, 4),
                E("wheat_seeds", 10, 1, 2), E("beetroot_seeds", 10, 1, 2), E("dead_bush", 10, 1, 2), E("flower_pot", 10),
                E("string", 10, 1, 3), E("lead", 10),
            })
        );
        table("trail_ruins_rare",
            pool(1, 1, new List<LootEntry> {
                E("burn_pottery_sherd", 10), E("danger_pottery_sherd", 10),
                E("friend_pottery_sherd", 10), E("heart_pottery_sherd", 10),
                E("heartbreak_pottery_sherd", 10), E("howl_pottery_sherd", 10),
                E("sheaf_pottery_sherd", 10), E("wayfinder_armor_trim", 8),
                E("raiser_armor_trim", 8), E("shaper_armor_trim", 8),
                E("host_armor_trim", 8), E("relic_unused", 0),
            })
        );
        table("desert_well_archaeology",
            pool(1, 1, new List<LootEntry> {
                E("arms_up_pottery_sherd", 2), E("brewer_pottery_sherd", 2),
                E("brick", 2), E("emerald", 2), E("stick", 2), E("suspicious_stew", 1),
            })
        );
        table("desert_pyramid_archaeology",
            pool(1, 1, new List<LootEntry> {
                E("archer_pottery_sherd", 2), E("miner_pottery_sherd", 2),
                E("prize_pottery_sherd", 2), E("skull_pottery_sherd", 2),
                E("diamond", 1), E("tnt", 1), E("gunpowder", 2), E("emerald", 2),
            })
        );
        table("ocean_ruin_warm_archaeology",
            pool(1, 1, new List<LootEntry> {
                E("angler_pottery_sherd", 2), E("shelter_pottery_sherd", 2),
                E("snort_pottery_sherd", 2), E("sniffer_egg_loot", 0),
                E("iron_axe", 1), E("emerald", 2), E("wheat", 2), E("coal", 2), E("gold_nugget", 2),
            })
        );
        table("ocean_ruin_cold_archaeology",
            pool(1, 1, new List<LootEntry> {
                E("blade_pottery_sherd", 2), E("explorer_pottery_sherd", 2),
                E("mourner_pottery_sherd", 2), E("plenty_pottery_sherd", 2),
                E("iron_axe", 1), E("emerald", 2), E("wheat", 2), E("coal", 2), E("gold_nugget", 2),
            })
        );
        table("trail_ruins_archaeology",
            pool(1, 1, new List<LootEntry> {
                E("burn_pottery_sherd", 1), E("danger_pottery_sherd", 1),
                E("friend_pottery_sherd", 1), E("heart_pottery_sherd", 1),
                E("heartbreak_pottery_sherd", 1), E("howl_pottery_sherd", 1),
                E("sheaf_pottery_sherd", 1), E("emerald", 2), E("coal", 3, 1, 2), E("brick", 3),
                E("clay_ball", 2), E("gold_nugget", 2), E("wheat_seeds", 2), E("string", 2),
            })
        );
        table("village_weaponsmith",
            pool(3, 8, new List<LootEntry> {
                E("diamond", 3, 1, 3), E("iron_ingot", 10, 1, 5), E("gold_ingot", 5, 1, 3), E("bread", 15, 1, 3),
                E("apple", 15, 1, 3), E("iron_pickaxe", 5), E("iron_sword", 5), E("iron_chestplate", 5),
                E("iron_helmet", 5), E("iron_leggings", 5), E("iron_boots", 5), E("obsidian", 5, 3, 7),
                E("oak_sapling", 5, 3, 7), E("saddle", 3), E("iron_horse_armor", 1),
            })
        );
        table("village_house",
            pool(3, 8, new List<LootEntry> {
                E("bread", 20, 1, 4), E("wheat", 10, 1, 4), E("carrot", 10, 1, 4), E("potato", 10, 1, 4),
                E("wheat_seeds", 10, 1, 2), E("book", 5), E("feather", 5), E("emerald", 2, 1, 4),
                E("oak_sapling", 5, 1, 2), E("poppy", 5), E("dandelion", 5),
            })
        );
        table("village_toolsmith",
            pool(3, 8, new List<LootEntry> {
                E("diamond", 1, 1, 3), E("iron_ingot", 5, 1, 5), E("gold_ingot", 1, 1, 3), E("bread", 15, 1, 3),
                E("iron_pickaxe", 5), E("iron_shovel", 5), E("iron_axe", 5), E("coal", 10, 1, 4), E("stick", 20, 1, 3),
                E("iron_hoe", 5),
            })
        );
        table("village_temple",
            pool(3, 8, new List<LootEntry> {
                E("redstone", 2, 1, 4), E("bread", 7, 1, 4), E("rotten_flesh", 7, 1, 4), E("lapis_lazuli", 1, 1, 4),
                E("gold_ingot", 1, 1, 4), E("emerald", 1, 1, 4),
            })
        );
        table("igloo_chest_dup", pool(1, 1, new List<LootEntry> { E("nothing", 1) }));

        // fishing
        table("fishing_fish",
            pool(1, 1, new List<LootEntry> { E("cod", 60), E("salmon", 25), E("tropical_fish", 2), E("pufferfish", 13) })
        );
        table("fishing_junk",
            pool(1, 1, new List<LootEntry> {
                E("leather_boots", 10), E("leather", 10), E("bone", 10), E("rotten_flesh", 10), E("stick", 5),
                E("string", 5), E("bowl", 10), E("fishing_rod", 2), E("ink_sac", 1, 10, 10), E("tripwire_hook", 10),
                E("lily_pad", 17), E("bamboo", 10),
            })
        );
        table("fishing_treasure",
            pool(1, 1, new List<LootEntry> {
                E("bow", 1, enchant: (22, 30)), E("fishing_rod", 1, enchant: (22, 30)),
                E("book", 1, enchant: (30, 39)), E("name_tag", 1), E("nautilus_shell", 1), E("saddle", 1),
            })
        );

        // piglin bartering
        table("piglin_bartering",
            pool(1, 1, new List<LootEntry> {
                E("book", 5, enchant: (21, 21)), E("iron_boots", 8),
                E("potion", 8, potion: "fire_resistance"),
                E("splash_potion", 8, potion: "fire_resistance"),
                E("potion", 10, potion: "water"),
                E("iron_nugget", 10, 10, 36), E("ender_pearl", 10, 2, 4),
                E("string", 20, 3, 9), E("quartz", 20, 5, 12), E("obsidian", 40), E("crying_obsidian", 40, 1, 3),
                E("fire_charge", 40), E("leather", 40, 2, 4), E("soul_sand", 40, 2, 8), E("nether_brick", 40, 2, 8),
                E("spectral_arrow", 40, 6, 12), E("gravel", 40, 8, 16), E("blackstone", 40, 8, 16),
            })
        );

        // sniffer digging
        table("sniffer_digging",
            pool(1, 1, new List<LootEntry> { E("torchflower_seeds", 1), E("pitcher_pod", 1) })
        );

        // fix up placeholder entries (weight 0 entries are never picked, but ensure names exist)
        foreach (var name in lootTableOrder)
        {
            LOOT_TABLES[name] = LOOT_TABLES[name].Select(p =>
            {
                var q = p;
                q.entries = p.entries.Where(x => x.item == "nothing" || (x.weight > 0 && itemExists(x.item))).ToList();
                return q;
            }).ToList();
        }
    }
}

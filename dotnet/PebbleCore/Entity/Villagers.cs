// Villagers with profession-based trading, wandering traders, horses, golems
//
//
// RNG contract: tradesFor builds the full trade table first (librarian rows
// call enchBookOffer, consuming rng DURING construction, in textual order),
// then shuffles each level's pool. Keep that order exactly.
//
// Ported from Sources/PebbleCore/Entity/Villagers.swift.
// Defines static class VillagersGlobals (the module-level free funcs/vars:
//   PROFESSIONS, WORKSTATIONS, tradesFor, openTradingFn, bindOpenTrading,
//   throwSnowballFn, bindThrowSnowball, spitFn, bindSpit — plus the private
//   helpers offer/enchBookOffer). Surface via global using static VillagersGlobals.
// The public types (TradeOffer, Villager, WanderingTrader, IronGolem, SnowGolem,
//   HorseBase, Horse, Donkey, Mule, SkeletonHorse, Llama) live at namespace level.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PebbleCore;

// ---------------------------------------------------------------------------
public struct TradeOffer
{
    public ItemStack buyA;
    public ItemStack buyB;
    public ItemStack sell;
    public int maxUses;
    public int uses;
    public int xp;
}

public static class VillagersGlobals
{
    // Swift `private func offer` is file-private; in a single-file C# the same-file
    // WanderingTrader ctor builds its pool with this helper, so expose it internally.
    internal static TradeOffer offer((string, int) buyA, (string, int) sell, int maxUses = 12, int xp = 2,
                                     (string, int)? buyB = null)
    {
        return new TradeOffer
        {
            buyA = new ItemStack(iid(buyA.Item1), buyA.Item2),
            buyB = buyB.HasValue ? new ItemStack(iid(buyB.Value.Item1), buyB.Value.Item2) : null,
            sell = new ItemStack(iid(sell.Item1), sell.Item2),
            maxUses = maxUses, uses = 0, xp = xp
        };
    }

    public static readonly List<string> PROFESSIONS = new List<string>
    {
        "farmer", "librarian", "armorer", "weaponsmith", "toolsmith", "cleric", "butcher",
        "fisherman", "shepherd", "fletcher", "mason", "cartographer", "leatherworker"
    };

    public static readonly Dictionary<string, string> WORKSTATIONS = new Dictionary<string, string>
    {
        { "composter", "farmer" }, { "lectern", "librarian" }, { "blast_furnace", "armorer" },
        { "grindstone", "weaponsmith" }, { "smithing_table", "toolsmith" }, { "brewing_stand", "cleric" },
        { "smoker", "butcher" }, { "barrel", "fisherman" }, { "loom", "shepherd" },
        { "fletching_table", "fletcher" }, { "stonecutter", "mason" }, { "cartography_table", "cartographer" },
        { "cauldron", "leatherworker" },
    };

    public static List<TradeOffer> tradesFor(string prof, int level, ref RandomX rng)
    {
        // table construction consumes rng for the librarian's book offers — must
        // run for EVERY profession before any pick(), mirroring the baseline object
        // literal evaluation order.
        var T = new Dictionary<string, List<List<TradeOffer>>>();
        T["farmer"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("wheat", 20), ("emerald", 1)), offer(("potato", 26), ("emerald", 1)), offer(("carrot", 22), ("emerald", 1)), offer(("beetroot", 15), ("emerald", 1)), offer(("emerald", 1), ("bread", 6), 16, 1) },
            new List<TradeOffer> { offer(("pumpkin", 6), ("emerald", 1)), offer(("emerald", 1), ("pumpkin_pie", 4)), offer(("emerald", 1), ("apple", 4)) },
            new List<TradeOffer> { offer(("melon", 4), ("emerald", 1)), offer(("emerald", 3), ("cookie", 18)) },
            new List<TradeOffer> { offer(("emerald", 1), ("cake", 1)), offer(("emerald", 1), ("suspicious_stew", 1)) },
            new List<TradeOffer> { offer(("emerald", 3), ("golden_carrot", 3)), offer(("emerald", 4), ("glistering_melon_slice", 3)) },
        };
        T["librarian"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("paper", 24), ("emerald", 1)), offer(("emerald", 9), ("book", 1)), enchBookOffer(ref rng, 1) },
            new List<TradeOffer> { offer(("book", 4), ("emerald", 1)), offer(("emerald", 1), ("lantern", 1)), enchBookOffer(ref rng, 2) },
            new List<TradeOffer> { offer(("ink_sac", 5), ("emerald", 1)), offer(("emerald", 1), ("glass", 4)), enchBookOffer(ref rng, 3) },
            new List<TradeOffer> { offer(("writable_book", 2), ("emerald", 1)), offer(("emerald", 5), ("clock", 1)), offer(("emerald", 4), ("compass", 1)) },
            new List<TradeOffer> { offer(("emerald", 20), ("name_tag", 1)), enchBookOffer(ref rng, 4) },
        };
        T["armorer"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("coal", 15), ("emerald", 1)), offer(("emerald", 5), ("iron_helmet", 1)), offer(("emerald", 9), ("iron_chestplate", 1)) },
            new List<TradeOffer> { offer(("iron_ingot", 4), ("emerald", 1)), offer(("emerald", 36), ("bell", 1)), offer(("emerald", 7), ("iron_leggings", 1)) },
            new List<TradeOffer> { offer(("lava_bucket", 1), ("emerald", 1)), offer(("emerald", 4), ("chainmail_leggings", 1)), offer(("emerald", 1), ("chainmail_boots", 1)) },
            new List<TradeOffer> { offer(("emerald", 19), ("diamond_leggings", 1)), offer(("emerald", 13), ("shield", 1)) },
            new List<TradeOffer> { offer(("emerald", 21), ("diamond_chestplate", 1)), offer(("emerald", 13), ("diamond_helmet", 1)) },
        };
        T["weaponsmith"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("coal", 15), ("emerald", 1)), offer(("emerald", 3), ("iron_axe", 1)), offer(("emerald", 7), ("iron_sword", 1)) },
            new List<TradeOffer> { offer(("iron_ingot", 4), ("emerald", 1)), offer(("emerald", 36), ("bell", 1)) },
            new List<TradeOffer> { offer(("flint", 24), ("emerald", 1)) },
            new List<TradeOffer> { offer(("diamond", 1), ("emerald", 1)), offer(("emerald", 19), ("diamond_axe", 1)) },
            new List<TradeOffer> { offer(("emerald", 13), ("diamond_sword", 1)) },
        };
        T["toolsmith"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("coal", 15), ("emerald", 1)), offer(("emerald", 1), ("stone_axe", 1)), offer(("emerald", 1), ("stone_pickaxe", 1)) },
            new List<TradeOffer> { offer(("iron_ingot", 4), ("emerald", 1)), offer(("emerald", 36), ("bell", 1)) },
            new List<TradeOffer> { offer(("flint", 30), ("emerald", 1)), offer(("emerald", 6), ("iron_pickaxe", 1)) },
            new List<TradeOffer> { offer(("diamond", 1), ("emerald", 1)), offer(("emerald", 18), ("diamond_pickaxe", 1)) },
            new List<TradeOffer> { offer(("emerald", 16), ("diamond_shovel", 1)), offer(("emerald", 22), ("diamond_hoe", 1)) },
        };
        T["cleric"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("rotten_flesh", 32), ("emerald", 1)), offer(("emerald", 1), ("redstone", 2)) },
            new List<TradeOffer> { offer(("gold_ingot", 3), ("emerald", 1)), offer(("emerald", 1), ("lapis_lazuli", 1)) },
            new List<TradeOffer> { offer(("rabbit_foot", 2), ("emerald", 1)), offer(("emerald", 4), ("glowstone", 1)) },
            new List<TradeOffer> { offer(("scute", 4), ("emerald", 1)), offer(("glass_bottle", 9), ("emerald", 1)), offer(("emerald", 5), ("ender_pearl", 1)) },
            new List<TradeOffer> { offer(("nether_wart", 22), ("emerald", 1)), offer(("emerald", 3), ("experience_bottle", 1)) },
        };
        T["butcher"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("chicken", 14), ("emerald", 1)), offer(("porkchop", 7), ("emerald", 1)), offer(("rabbit", 4), ("emerald", 1)), offer(("emerald", 1), ("rabbit_stew", 1)) },
            new List<TradeOffer> { offer(("coal", 15), ("emerald", 1)), offer(("emerald", 1), ("cooked_porkchop", 5)), offer(("emerald", 1), ("cooked_chicken", 8)) },
            new List<TradeOffer> { offer(("mutton", 7), ("emerald", 1)), offer(("beef", 10), ("emerald", 1)) },
            new List<TradeOffer> { offer(("dried_kelp_block", 10), ("emerald", 1)) },
            new List<TradeOffer> { offer(("sweet_berries", 10), ("emerald", 1)) },
        };
        T["fisherman"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("string", 20), ("emerald", 1)), offer(("coal", 10), ("emerald", 1)), offer(("emerald", 1), ("cod_bucket", 1)), offer(("cod", 6), ("emerald", 1)) },
            new List<TradeOffer> { offer(("cod", 15), ("emerald", 1)), offer(("emerald", 1), ("cooked_cod", 6)) },
            new List<TradeOffer> { offer(("salmon", 13), ("emerald", 1)), offer(("emerald", 8), ("fishing_rod", 1)) },
            new List<TradeOffer> { offer(("tropical_fish", 6), ("emerald", 1)) },
            new List<TradeOffer> { offer(("pufferfish", 4), ("emerald", 1)), offer(("emerald", 3), ("campfire", 1)) },
        };
        T["shepherd"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("white_wool", 18), ("emerald", 1)), offer(("emerald", 2), ("shears", 1)) },
            new List<TradeOffer> { offer(("white_dye", 12), ("emerald", 1)), offer(("emerald", 1), ("white_wool", 1)), offer(("emerald", 1), ("white_carpet", 4)) },
            new List<TradeOffer> { offer(("red_dye", 12), ("emerald", 1)), offer(("emerald", 3), ("red_bed", 1)) },
            new List<TradeOffer> { offer(("blue_dye", 12), ("emerald", 1)) },
            new List<TradeOffer> { offer(("emerald", 2), ("pink_wool", 3)), offer(("emerald", 2), ("cyan_wool", 3)) },
        };
        T["fletcher"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("stick", 32), ("emerald", 1)), offer(("emerald", 1), ("arrow", 16)), offer(("gravel", 10), ("emerald", 1), 12, 1, ("flint", 10)) },
            new List<TradeOffer> { offer(("flint", 26), ("emerald", 1)), offer(("emerald", 2), ("bow", 1)) },
            new List<TradeOffer> { offer(("string", 14), ("emerald", 1)), offer(("emerald", 3), ("crossbow", 1)) },
            new List<TradeOffer> { offer(("feather", 24), ("emerald", 1)) },
            new List<TradeOffer> { offer(("emerald", 2), ("spectral_arrow", 5)) },
        };
        T["mason"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("clay_ball", 10), ("emerald", 1)), offer(("emerald", 1), ("bricks", 10)) },
            new List<TradeOffer> { offer(("stone", 20), ("emerald", 1)), offer(("emerald", 1), ("chiseled_stone_bricks", 4)) },
            new List<TradeOffer> { offer(("granite", 16), ("emerald", 1)), offer(("emerald", 1), ("polished_andesite", 4)), offer(("emerald", 1), ("polished_granite", 4)) },
            new List<TradeOffer> { offer(("quartz", 12), ("emerald", 1)), offer(("emerald", 1), ("orange_terracotta", 1)), offer(("emerald", 1), ("red_glazed_terracotta", 1)) },
            new List<TradeOffer> { offer(("emerald", 1), ("quartz_pillar", 1)), offer(("emerald", 1), ("quartz_block", 1)) },
        };
        T["cartographer"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("paper", 24), ("emerald", 1)), offer(("emerald", 7), ("compass", 1)) },
            new List<TradeOffer> { offer(("glass_pane", 11), ("emerald", 1)) },
            new List<TradeOffer> { offer(("compass", 1), ("emerald", 1)) },
            new List<TradeOffer> { offer(("emerald", 14), ("ender_eye", 1)) },
            new List<TradeOffer> { offer(("emerald", 8), ("clock", 1)) },
        };
        T["leatherworker"] = new List<List<TradeOffer>>
        {
            new List<TradeOffer> { offer(("leather", 6), ("emerald", 1)), offer(("emerald", 3), ("leather_leggings", 1)) },
            new List<TradeOffer> { offer(("flint", 26), ("emerald", 1)), offer(("emerald", 5), ("leather_chestplate", 1)) },
            new List<TradeOffer> { offer(("rabbit_hide", 9), ("emerald", 1)), offer(("emerald", 4), ("leather_helmet", 1)) },
            new List<TradeOffer> { offer(("scute", 4), ("emerald", 1)), offer(("emerald", 4), ("leather_boots", 1)) },
            new List<TradeOffer> { offer(("emerald", 6), ("leather_horse_armor", 1)), offer(("emerald", 5), ("saddle", 1)) },
        };
        if (!T.TryGetValue(prof, out var tables)) return new List<TradeOffer>();
        int lvl = Math.Min(level, tables.Count);
        var outList = new List<TradeOffer>();
        for (int i = 0; i < lvl; i++)
        {
            var pool = new List<TradeOffer>(tables[i]);
            rng.shuffle(pool);
            int take = Math.Min(2, pool.Count);
            for (int j = 0; j < take; j++) outList.Add(pool[j]);
        }
        return outList;
    }

    private static TradeOffer enchBookOffer(ref RandomX rng, int tier)
    {
        var tradeable = ENCHANTMENTS.Where(e => e.tradeable && !e.curse).ToList();
        var e = tradeable[rng.nextInt(tradeable.Count)];
        int lvl = 1 + rng.nextInt(Math.Min(e.maxLevel, tier + 1));
        int cost = 2 + rng.nextInt(5 + lvl * 10) + lvl * 3;
        return new TradeOffer
        {
            buyA = new ItemStack(iid("emerald"), Math.Min(64, cost)),
            buyB = new ItemStack(iid("book"), 1),
            sell = new ItemStack(iid("enchanted_book"), 1, ench: new List<EnchInstance> { new EnchInstance(e.id, lvl) }),
            maxUses = 12, uses = 0, xp = 5
        };
    }

    /// late-bound trading UI hook (player.openTrading in baseline)
    public static Action<Entity, Mob> openTradingFn;
    public static void bindOpenTrading(Action<Entity, Mob> fn) { openTradingFn = fn; }

    public static Action<Mob, LivingEntity> throwSnowballFn;
    public static void bindThrowSnowball(Action<Mob, LivingEntity> fn) { throwSnowballFn = fn; }

    public static Action<Mob, LivingEntity> spitFn;
    public static void bindSpit(Action<Mob, LivingEntity> fn) { spitFn = fn; }
}

// ---------------------------------------------------------------------------
public class Villager : Mob
{
    public override string type => "villager";
    public string profession = "none";
    public int tradeLevel = 1;
    public int tradeXP = 0;
    public List<TradeOffer> offers = new List<TradeOffer>();
    public int restockTimer = 0;
    public (int, int, int)? workstation = null;
    public (int, int, int)? homeBed = null;

    public Villager(World world) : base(world)
    {
        category = "creature";
        width = 0.6; height = 1.95;
        maxHealth = 20; health = 20;
        speed = 0.1;
        persistent = true;
        xpReward = 0;
        goals.add(new FloatGoal(this, 0));
        goals.add(new AvoidEntityGoal(this, 1, e =>
            new[] { "zombie", "husk", "drowned", "zombie_villager", "pillager", "vindicator", "evoker", "vex", "ravager", "zoglin" }.Contains(e.type),
            10, 1.2));
        goals.add(new FindWorkstationGoal(this, 2));
        goals.add(new StrollGoal(this, 6, 0.7));
        goals.add(new LookAtPlayerGoal(this, 7, 8, 0.05));
        goals.add(new RandomLookGoal(this, 8));
    }

    public override void tick()
    {
        base.tick();
        // acquire profession from claimed workstation
        if (profession == "none" && workstation is { } ws && age % 40 == 0)
        {
            int bid = world.getBlock(ws.Item1, ws.Item2, ws.Item3) >> 4;
            string name = blockNameOf(bid);
            if (WORKSTATIONS.TryGetValue(name, out var prof))
            {
                profession = prof;
                refreshTrades();
                world.hooks.playSound("entity.villager.work", x, y, z, 1, 1);
            }
            else
            {
                workstation = null;
            }
        }
        // restock at workstation
        if (restockTimer > 0) restockTimer -= 1;
    }

    public void refreshTrades()
    {
        if (profession == "none" || profession == "nitwit") return;
        offers = tradesFor(profession, tradeLevel, ref rng);
    }

    public void addTradeXP(int xp)
    {
        tradeXP += xp;
        int[] thresholds = { 0, 10, 70, 150, 250 };
        if (tradeLevel < 5 && tradeXP >= thresholds[tradeLevel])
        {
            tradeLevel += 1;
            refreshTrades();
            world.hooks.addParticles("heart", x, y + 2, z, 5, 0.4, 0);
        }
    }

    public void restock()
    {
        for (int i = 0; i < offers.Count; i++)
        {
            var o = offers[i];
            o.uses = 0;
            offers[i] = o;
        }
        restockTimer = 2400;
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        if (baby || profession == "none" || profession == "nitwit")
        {
            world.hooks.playSound("entity.villager.no", x, y, z, 1, 1);
            return false;
        }
        if (offers.Count == 0) refreshTrades();
        openTradingFn?.Invoke(player, this);
        return true;
    }

    public override bool hurt(double amount, string source, Entity attacker = null)
    {
        bool r = base.hurt(amount, source, attacker);
        if (r && attacker != null && attacker.isPlayer)
        {
            // gossip: minor reputation hit — golems may aggro
            foreach (var g in world.getEntitiesNear(x, y, z, 16, filter: o => (o as Entity)?.type == "iron_golem"))
            {
                (g as Mob)?.setTarget(attacker as LivingEntity);
            }
        }
        return r;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["profession"] = profession;
        d["tradeLevel"] = tradeLevel;
        d["tradeXP"] = tradeXP;
        try
        {
            byte[] enc = JsonSerializer.SerializeToUtf8Bytes(offers, tradeOfferJsonOpts);
            object obj = JsonSerializer.Deserialize<object>(enc);
            d["offers"] = obj;
        }
        catch { }
        return d;
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        profession = (d.TryGetValue("profession", out var vProf) && vProf is string sProf) ? sProf : "none";
        tradeLevel = (d.TryGetValue("tradeLevel", out var vLvl) && vLvl is IConvertible cLvl && vLvl is not string)
            ? Convert.ToInt32(cLvl) : 1;
        tradeXP = (d.TryGetValue("tradeXP", out var vXP) && vXP is IConvertible cXP && vXP is not string)
            ? Convert.ToInt32(cXP) : 0;
        List<TradeOffer> decoded = null;
        if (d.TryGetValue("offers", out var raw) && raw != null)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw);
                decoded = JsonSerializer.Deserialize<List<TradeOffer>>(bytes, tradeOfferJsonOpts);
            }
            catch { decoded = null; }
        }
        offers = decoded ?? new List<TradeOffer>();
    }

    // Swift's synthesized Codable encodes the optional buyB with encodeIfPresent,
    // so a nil buyB is absent from the JSON object. Mirror that.
    private static readonly JsonSerializerOptions tradeOfferJsonOpts = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };
}

internal sealed class FindWorkstationGoal : MoveToBlockGoal
{
    public FindWorkstationGoal(Mob mob, int priority)
        : base(mob, priority, (w, x, y, z) =>
        {
            if (!(mob is Villager v)) return false;
            if (v.profession != "none" && v.workstation != null) return false;
            string name = blockNameOf(w.getBlock(x, y, z) >> 4);
            return WORKSTATIONS.ContainsKey(name);
        }, 10, 1, 80)
    {
    }

    public override void start()
    {
        base.start();
        if (!(mob is Villager v)) return;
        if (targetPos is { } t && v.workstation == null) v.workstation = t;
    }
}

public sealed class WanderingTrader : Mob
{
    public override string type => "wandering_trader";
    public List<TradeOffer> offers = new List<TradeOffer>();
    public int despawnTimer = 48000;
    public int restockTimer = 0;

    public WanderingTrader(World world) : base(world)
    {
        category = "creature";
        width = 0.6; height = 1.95;
        maxHealth = 20; health = 20;
        speed = 0.12;
        persistent = true;
        goals.add(new FloatGoal(this, 0));
        goals.add(new PanicGoal(this, 1, 1.4));
        goals.add(new AvoidEntityGoal(this, 2, e =>
            new[] { "zombie", "pillager", "vindicator", "evoker", "vex", "zoglin" }.Contains(e.type),
            10, 1.2));
        goals.add(new StrollGoal(this, 5, 1));
        goals.add(new LookAtPlayerGoal(this, 7));
        var pool = new List<TradeOffer>
        {
            offer(("emerald", 1), ("fern", 1)), offer(("emerald", 1), ("sugar_cane", 1)),
            offer(("emerald", 1), ("pumpkin", 1)), offer(("emerald", 1), ("dandelion", 1)),
            offer(("emerald", 1), ("poppy", 1)), offer(("emerald", 1), ("wheat_seeds", 1)),
            offer(("emerald", 1), ("beetroot_seeds", 1)), offer(("emerald", 1), ("oak_sapling", 1)),
            offer(("emerald", 5), ("cherry_sapling", 1)), offer(("emerald", 1), ("red_mushroom", 1)),
            offer(("emerald", 1), ("brown_mushroom", 1)), offer(("emerald", 1), ("lily_pad", 2)),
            offer(("emerald", 1), ("sand", 8)), offer(("emerald", 1), ("red_sand", 4)),
            offer(("emerald", 3), ("packed_ice", 1)), offer(("emerald", 6), ("blue_ice", 1)),
            offer(("emerald", 1), ("kelp", 1)), offer(("emerald", 5), ("nautilus_shell", 1)),
            offer(("emerald", 1), ("bamboo", 1)), offer(("emerald", 4), ("sea_pickle", 1)),
        };
        rng.shuffle(pool);
        offers = pool.Take(6).ToList();
    }

    public override void tick()
    {
        base.tick();
        despawnTimer -= 1;
        if (despawnTimer <= 0) remove();
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        openTradingFn?.Invoke(player, this);
        return true;
    }

    public void addTradeXP(int xp) { }
    public void restock() { }
}

public sealed class IronGolem : Mob
{
    public override string type => "iron_golem";
    public bool playerMade = false;

    public IronGolem(World world) : base(world)
    {
        category = "creature";
        width = 1.4; height = 2.7;
        maxHealth = 100; health = 100;
        speed = 0.12;
        attackDamage = 12;
        kbResist = 1;
        persistent = true;
        xpReward = 0;
        goals.add(new FloatGoal(this, 0));
        goals.add(new MeleeAttackGoal(this, 1, 1.1));
        goals.add(new StrollGoal(this, 5, 0.6));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new HurtByTargetGoal(this, 1));
        targetGoals.add(new NearestTargetGoal(this, 2, e =>
        {
            string t = e.type;
            return ((e as Mob)?.category == "monster" && t != "creeper")
                || new[] { "zombie", "skeleton", "spider", "pillager", "vindicator", "evoker", "ravager" }.Contains(t);
        }, 20));
    }

    public override void doMeleeAttack(LivingEntity target)
    {
        attackAnim = 1;
        target.hurt(attackDamage + (double)rng.nextInt(8), "mob", this);
        target.vy += 0.5; // launch!
        world.hooks.playSound("entity.iron_golem.attack", x, y, z, 1, 1);
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        if (stack != null && itemDef(stack.id).name == "iron_ingot" && health < maxHealth)
        {
            heal(25);
            (player as LivingEntity)?.consumeHeld(1);
            world.hooks.playSound("entity.iron_golem.repair", x, y, z, 1, 1);
            return true;
        }
        return false;
    }

    public override List<DropEntry> drops()
    {
        return new List<DropEntry> { new DropEntry("iron_ingot", min: 3, max: 5), new DropEntry("poppy", min: 0, max: 2) };
    }
}

public sealed class SnowGolem : Mob
{
    public override string type => "snow_golem";

    public SnowGolem(World world) : base(world)
    {
        category = "creature";
        width = 0.7; height = 1.9;
        maxHealth = 4; health = 4;
        speed = 0.2;
        persistent = true;
        goals.add(new FloatGoal(this, 0));
        goals.add(new SnowballAttackGoal(this, 1));
        goals.add(new StrollGoal(this, 5, 1));
        goals.add(new LookAtPlayerGoal(this, 7));
        targetGoals.add(new NearestTargetGoal(this, 1, e => (e as Mob)?.category == "monster", 10));
    }

    public override void tick()
    {
        base.tick();
        // snow trail
        if (onGround && world.rule("mobGriefing"))
        {
            int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
            int at = world.getBlock(bx, by, bz);
            if (at == 0 && (world.getBlock(bx, by - 1, bz) >> 4) != 0)
            {
                world.setBlock(bx, by, bz, (int)cell(B.snow, 0));
            }
        }
        // melt in hot/wet
        if (inWater) hurt(1, "drown");
    }

    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("snowball", min: 0, max: 15) }; }
}

internal sealed class SnowballAttackGoal : Goal
{
    private int cooldown = 0;

    public SnowballAttackGoal(Mob mob, int priority) : base(mob, priority) { }

    public override bool canUse() { return mob.target != null && !mob.target.dead; }

    public override void tick()
    {
        var m = mob;
        var t = m.target;
        if (t == null) return;
        m.lookX = t.x; m.lookY = t.eyeY(); m.lookZ = t.z;
        if (m.distanceToSq(t) > 100) m.nav.moveToEntity(t, 1.2);
        else m.nav.stop();
        int cd = cooldown;
        cooldown -= 1;
        if (cd <= 0 && m.canSee(t))
        {
            cooldown = 20;
            throwSnowballFn?.Invoke(m, t);
        }
    }
}

// ---------------------------------------------------------------------------
// Horses
// ---------------------------------------------------------------------------
public class HorseBase : Animal
{
    public override string type => "horse";
    public bool tamed = false;
    public bool saddled = false;
    public int temper = 0;
    public double jumpStrength = 0.0;

    public HorseBase(World world) : base(world)
    {
        jumpStrength = 0.5 + gameRng.nextFloat() * 0.5;   // baseline field-init order
        width = 1.4; height = 1.6;
        double hp = 15 + (double)gameRng.nextInt(16);
        maxHealth = hp; health = hp;
        speed = 0.15 + gameRng.nextFloat() * 0.12;
        stepHeight = 1.0;
        foods = new List<string> { "golden_apple", "golden_carrot", "apple", "sugar", "wheat", "hay_block" };
        xpReward = 3;
        addBasicGoals(1, 1.6);
    }

    public override bool interact(Entity player, ItemStack stack)
    {
        string name = stack != null ? itemDef(stack.id).name : null;
        if (tamed && name == "saddle" && !saddled)
        {
            saddled = true;
            (player as LivingEntity)?.consumeHeld(1);
            world.hooks.playSound("entity.horse.saddle", x, y, z, 1, 1);
            return true;
        }
        if (stack != null && isFood(stack)) return base.interact(player, stack);
        if (!((player as LivingEntity)?.sneaking ?? false) && !baby)
        {
            if (!tamed)
            {
                // attempt taming by riding
                player.mount(this);
                temper += 5;
                if (rng.nextInt(100) < temper)
                {
                    tamed = true;
                    persistent = true;
                    world.hooks.addParticles("heart", x, y + 1.6, z, 7, 0.6, 0);
                }
                else
                {
                    // buck off after a moment
                    data.buckTimer = 20 + rng.nextInt(20);
                }
            }
            else
            {
                player.mount(this);
            }
            return true;
        }
        return base.interact(player, stack);
    }

    public override void tick()
    {
        base.tick();
        if (data.buckTimer != null && passengers.Count != 0)
        {
            data.buckTimer = (data.buckTimer ?? 0) - 1;
            if ((data.buckTimer ?? 0) <= 0)
            {
                var bucked = passengers[0];
                bucked.dismount();
                bucked.vy = 0.4;
                data.buckTimer = null;
                world.hooks.playSound("entity.horse.angry", x, y, z, 1, 1);
            }
        }
        // rider control
        if (passengers.FirstOrDefault() is LivingEntity rider && rider.isPlayer && tamed && saddled)
        {
            yaw = rider.yaw;
            moveForward = rider.moveForward;
            moveStrafe = rider.moveStrafe * 0.5;
            if (rider.jumping && onGround)
            {
                vy = jumpStrength;
                onGround = false;
            }
        }
    }

    public override List<DropEntry> drops()
    {
        var d = new List<DropEntry> { new DropEntry("leather", min: 0, max: 2, lootingBonus: 1) };
        if (saddled) d.Add(new DropEntry("saddle"));
        return d;
    }

    public override Dictionary<string, object> save()
    {
        var d = base.save();
        d["tamed"] = tamed; d["saddled"] = saddled;
        d["jumpStrength"] = jumpStrength; d["speed"] = speed;
        return d;
    }

    public override void load(Dictionary<string, object> d)
    {
        base.load(d);
        tamed = (d.TryGetValue("tamed", out var vTamed) && vTamed is bool bTamed) ? bTamed : false;
        saddled = (d.TryGetValue("saddled", out var vSad) && vSad is bool bSad) ? bSad : false;
        jumpStrength = (d.TryGetValue("jumpStrength", out var vJump) && vJump is IConvertible cJump && vJump is not string)
            ? Convert.ToDouble(cJump) : 0.7;
        speed = (d.TryGetValue("speed", out var vSpeed) && vSpeed is IConvertible cSpeed && vSpeed is not string)
            ? Convert.ToDouble(cSpeed) : 0.2;
    }
}

public sealed class Horse : HorseBase
{
    public override string type => "horse";
    public Horse(World world) : base(world) { }
}

public sealed class Donkey : HorseBase
{
    public override string type => "donkey";
    public Donkey(World world) : base(world)
    {
        width = 1.3; height = 1.5;
    }
}

public sealed class Mule : HorseBase
{
    public override string type => "mule";
    public Mule(World world) : base(world) { }
}

public sealed class SkeletonHorse : HorseBase
{
    public override string type => "skeleton_horse";
    public SkeletonHorse(World world) : base(world)
    {
        maxHealth = 15; health = 15;
        tamed = true;
    }
    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("bone", min: 0, max: 2) }; }
}

public sealed class Llama : Animal
{
    public override string type => "llama";
    public int spitCooldown = 0;

    public Llama(World world) : base(world)
    {
        width = 0.9; height = 1.87;
        double hp = 15 + (double)gameRng.nextInt(16);
        maxHealth = hp; health = hp;
        speed = 0.12;
        foods = new List<string> { "wheat", "hay_block" };
        xpReward = 3;
        data.variant = gameRng.nextInt(4);
        addBasicGoals();
        targetGoals.add(new HurtByTargetGoal(this, 1));
    }

    public override void tick()
    {
        base.tick();
        if (spitCooldown > 0) spitCooldown -= 1;
        if (target is { } t && !t.dead && spitCooldown <= 0 && distanceToSq(t) < 100)
        {
            spitCooldown = 40;
            spitFn?.Invoke(this, t);
            setTarget(null);
        }
    }

    public override List<DropEntry> drops() { return new List<DropEntry> { new DropEntry("leather", min: 0, max: 2) }; }
}

// Advancements — definition tree, trigger
// API, persistence-ready state. earned keeps insertion order (deterministic Set).
//
// Ported from Sources/PebbleCore/Systems/Advancements.swift.
// Defines static class `AdvancementsGlobals` (holds module-level `ADVANCEMENTS`
// and `ADV_BY_ID`) — surface via `global using static PebbleCore.AdvancementsGlobals`.

using System.Collections.Generic;

namespace PebbleCore;

public struct AdvancementDef
{
    public readonly string id;
    public readonly string title;
    public readonly string description;
    public readonly string icon;            // item name for the icon
    public readonly string parent;
    public readonly string frame;           // task | goal | challenge

    public AdvancementDef(string id, string title, string description, string icon, string parent, string frame)
    {
        this.id = id;
        this.title = title;
        this.description = description;
        this.icon = icon;
        this.parent = parent;
        this.frame = frame;
    }
}

public static class AdvancementsGlobals
{
    public static readonly List<AdvancementDef> ADVANCEMENTS = buildAdvancements();

    private static List<AdvancementDef> buildAdvancements()
    {
        var A = new List<AdvancementDef>();
        void adv(string id, string title, string description, string icon, string parent, string frame = "task")
        {
            A.Add(new AdvancementDef(id, title, description, icon, parent, frame));
        }

        // story (main progression)
        adv("root", "Pebble", "The heart and story of the game", "grass_block", null);
        adv("mine_log", "Getting Wood", "Punch a tree until a block of wood pops out", "oak_log", "root");
        adv("crafting_table", "Benchmarking", "Craft a crafting table", "crafting_table", "mine_log");
        adv("wooden_pickaxe", "Time to Mine!", "Use planks and sticks to make a pickaxe", "wooden_pickaxe", "crafting_table");
        adv("mine_stone", "Stone Age", "Mine stone with your new pickaxe", "cobblestone", "wooden_pickaxe");
        adv("stone_pickaxe", "Getting an Upgrade", "Construct a better pickaxe", "stone_pickaxe", "mine_stone");
        adv("iron_ingot", "Acquire Hardware", "Smelt an iron ingot", "iron_ingot", "stone_pickaxe");
        adv("iron_tools", "Isn't It Iron Pick", "Upgrade your pickaxe", "iron_pickaxe", "iron_ingot");
        adv("iron_armor", "Suit Up", "Protect yourself with a piece of iron armor", "iron_chestplate", "iron_ingot");
        adv("mine_diamond", "Diamonds!", "Acquire diamonds", "diamond", "iron_tools");
        adv("diamond_armor", "Cover Me with Diamonds", "Diamond armor saves lives", "diamond_chestplate", "mine_diamond");
        adv("enchant_item", "Enchanter", "Enchant an item at an enchanting table", "enchanting_table", "mine_diamond");
        adv("ignite_portal", "We Need to Go Deeper", "Build, light and enter a Nether portal", "flint_and_steel", "mine_diamond");
        adv("return_portal", "Return to Sender", "Destroy a Ghast with a fireball", "fire_charge", "ignite_portal", "challenge");
        adv("obtain_blaze_rod", "Into Fire", "Relieve a Blaze of its rod", "blaze_rod", "ignite_portal");
        adv("brew_potion", "Local Brewery", "Brew a potion", "brewing_stand", "obtain_blaze_rod");
        adv("ender_eye", "Eye for an Eye", "Obtain an Eye of Ender", "ender_eye", "obtain_blaze_rod");
        adv("follow_ender_eye", "Eye Spy", "Throw an Eye of Ender", "ender_eye", "obtain_blaze_rod");
        adv("enter_end", "The End?", "Enter the End portal", "end_stone", "ender_eye");
        adv("kill_dragon", "Free the End", "Good luck", "dragon_head", "enter_end", "goal");
        adv("dragon_egg", "The Next Generation", "Hold the Dragon Egg", "dragon_egg", "kill_dragon");
        adv("enter_gateway", "Remote Getaway", "Escape the island", "ender_pearl", "kill_dragon");
        adv("elytra", "Sky's the Limit", "Find an Elytra", "elytra", "enter_gateway");
        adv("dragon_breath", "You Need a Mint", "Collect Dragon's Breath in a glass bottle", "dragon_breath", "kill_dragon", "goal");

        // nether branch
        adv("nether_root", "Nether", "Bring summer clothes", "netherrack", "ignite_portal");
        adv("find_fortress", "A Terrible Fortress", "Break your way into a Nether Fortress", "nether_bricks", "nether_root");
        adv("obtain_ancient_debris", "Hidden in the Depths", "Obtain Ancient Debris", "ancient_debris", "nether_root");
        adv("netherite_armor", "Cover Me in Debris", "Get a full suit of Netherite armor", "netherite_chestplate", "obtain_ancient_debris", "challenge");
        adv("find_bastion", "Those Were the Days", "Enter a Bastion Remnant", "gilded_blackstone", "nether_root");
        adv("distract_piglin", "Oh Shiny", "Distract Piglins with gold", "gold_ingot", "find_bastion");
        adv("kill_wither", "Withering Heights", "Summon and defeat the Wither", "nether_star", "find_fortress", "challenge");
        adv("beacon", "Bring Home the Beacon", "Construct and place a beacon", "beacon", "kill_wither");

        // adventure branch
        adv("adventure_root", "Adventure", "Adventure, exploration and combat", "compass", "root");
        adv("kill_mob", "Monster Hunter", "Kill any hostile monster", "iron_sword", "adventure_root");
        adv("shoot_arrow", "Take Aim", "Shoot something with an arrow", "bow", "kill_mob");
        adv("sleep_in_bed", "Sweet Dreams", "Sleep in a bed to change your respawn point", "red_bed", "adventure_root");
        adv("trade_villager", "What a Deal!", "Successfully trade with a Villager", "emerald", "adventure_root");
        adv("totem", "Postmortal", "Use a Totem of Undying to cheat death", "totem_of_undying", "kill_mob", "goal");
        adv("sniper_duel", "Sniper Duel", "Kill a Skeleton from at least 50 meters away", "arrow", "shoot_arrow", "challenge");
        adv("hero_village", "Hero of the Village", "Successfully defend a village from a raid", "iron_axe", "kill_mob", "challenge");
        adv("brush_sherd", "Respecting the Remnants", "Brush a suspicious block to obtain a pottery sherd", "brush", "adventure_root");
        adv("avoid_warden", "It Spreads", "Kill a mob near a Sculk Catalyst", "sculk_catalyst", "kill_mob");
        adv("lightning_rod", "Surge Protector", "Protect a villager from a lightning strike", "lightning_rod", "adventure_root");

        // husbandry
        adv("husbandry_root", "Husbandry", "The world is full of friends and food", "wheat", "root");
        adv("husbandry_eat", "A First Bite", "Eat anything", "apple", "husbandry_root");
        adv("breed_animals", "The Parrots and the Bats", "Breed two animals together", "wheat", "husbandry_root");
        adv("tame_animal", "Best Friends Forever", "Tame an animal", "bone", "husbandry_root");
        adv("plant_seed", "A Seedy Place", "Plant a seed and watch it grow", "wheat_seeds", "husbandry_root");
        adv("fish", "Fishy Business", "Catch a fish", "fishing_rod", "husbandry_root");
        adv("tactical_fishing", "Tactical Fishing", "Catch a fish... without a fishing rod!", "pufferfish_bucket", "fish");
        adv("sniffer_egg", "Smells Interesting", "Obtain a Sniffer Egg", "sniffer_egg", "husbandry_root");
        adv("plant_torchflower", "Planting the Past", "Plant any Sniffer seed", "torchflower_seeds", "sniffer_egg");
        adv("full_beehive", "Total Beelocation", "Move a bee nest with silk touch", "bee_nest", "husbandry_root");
        adv("axolotl_bucket", "The Cutest Predator", "Catch an axolotl in a bucket", "axolotl_bucket", "husbandry_root");
        adv("wax_copper", "Wax On", "Apply honeycomb to a copper block", "honeycomb", "husbandry_root");

        return A;
    }

    public static readonly Dictionary<string, AdvancementDef> ADV_BY_ID = buildAdvById();

    private static Dictionary<string, AdvancementDef> buildAdvById()
    {
        var d = new Dictionary<string, AdvancementDef>();
        foreach (var a in ADVANCEMENTS) d[a.id] = a;
        return d;
    }
}

public sealed class AdvancementTracker
{
    /// insertion-ordered (deterministic Set semantics)
    public List<string> earnedOrder { get; private set; } = new List<string>();
    private HashSet<string> earnedSet = new HashSet<string>();
    public List<AdvancementDef> pendingToasts = new List<AdvancementDef>();

    public AdvancementTracker() { }

    public bool grant(string id)
    {
        if (earnedSet.Contains(id)) return false;
        if (!AdvancementsGlobals.ADV_BY_ID.TryGetValue(id, out var def)) return false;
        addEarned(id);
        // auto-grant parents (silently)
        var p = def.parent;
        while (p != null)
        {
            var cur = p;
            addEarned(cur);
            p = AdvancementsGlobals.ADV_BY_ID.TryGetValue(cur, out var pd) ? pd.parent : null;
        }
        pendingToasts.Add(def);
        return true;
    }

    private void addEarned(string id)
    {
        if (earnedSet.Add(id))
        {
            earnedOrder.Add(id);
        }
    }

    public bool has(string id) => earnedSet.Contains(id);

    public List<string> save() => earnedOrder;

    public void load(List<string> ids)
    {
        earnedOrder = new List<string>();
        earnedSet = new HashSet<string>();
        foreach (var id in ids) addEarned(id);
    }
}

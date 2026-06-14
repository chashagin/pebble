// GameCore — the simulation orchestrator (the app target owns rendering
// and UI; everything sim-side lives here, ticking in frozen order). Chunk
// gen/meshing/saves run on GCD queues, persistence goes through SaveDB,
// and input + screens reach the app through the GameHost protocol.
//
// Defines static class `GameCoreGlobals` (surface via `global using static
// PebbleCore.GameCoreGlobals`): the module-level constants TICK_MS,
// REACH_SURVIVAL, REACH_CREATIVE, ATTACK_REACH, SPRITE_TYPES, and the free
// funcs prettyEntityName / deathCauseText, plus tryBuildGolem / tryBuildWither.
// Namespace-level types declared here: LoadProf, BossBarInfo, GameHost,
// CamState, GameCore, DispatchQueue (threading helper).
//
// External (defined in sibling files, referenced by name): World, Chunk,
// BlockEntityData, Player, Entity, LivingEntity, Mob, EnderDragon, WitherBoss,
// Warden, EndCrystal, ItemStack, RaycastHit, AABB; Settings/loadSettings/
// saveSettings/loadKeybinds/saveKeybinds; SaveDB / WorldRecord / DimState /
// ChunkRecord / chunkRecord (here); AdvancementTracker / AdvancementDef;
// ScreenData / InteractCtx; MeshInput / MeshOutput; generateChunk / GenOutput /
// computeLocalLight; spawnMob / SpawnOpts / loadEntity / naturalSpawnTick /
// clearEntityTimeouts; rollLoot; the BE factories (BlockEntities); the
// registerAll* registry boots; raidManager; bind*/...Fn hooks; spawnItem /
// spawnLightningFn; the systems free funcs (useBlock/useItem/finishBreaking/
// releaseUsingItem/finishUsingItem/playerAttack/breakSpeed/tickFangs/
// tickEntityTriggers/sculkBloom/weatherRandomTick/handleVibration/
// findOrCreatePortal/activateEndPortal/spawnEndGateway/buildEndSpawnPlatform/
// tryPatrolSpawn); Biome / BIOMES / Dim / DIMS / GameMode / PLAYER_EYE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace PebbleCore;

// =============================================================================
// Threading helper — a faithful stand-in for Grand Central Dispatch. The app's
// frame pump drains the main queue (pumpMain) so the off-main → main handoff
// stays single-threaded and reproducible like the golden baselines.
// =============================================================================
public sealed class DispatchQueue
{
    private readonly bool concurrent;
    private readonly object serialGate = new object();

    /// the main-thread queue: actions are buffered here and drained by pumpMain()
    public static readonly DispatchQueue main = new DispatchQueue(concurrent: false, isMain: true);
    private readonly bool isMain;
    private readonly ConcurrentQueue<Action> mainBuffer = new ConcurrentQueue<Action>();

    public DispatchQueue(bool concurrent, bool isMain = false)
    {
        this.concurrent = concurrent;
        this.isMain = isMain;
    }

    public void async(Action body)
    {
        if (isMain) { mainBuffer.Enqueue(body); return; }
        if (concurrent)
        {
            ThreadPool.QueueUserWorkItem(_ => body());
        }
        else
        {
            // serial utility queue: chain onto a single worker preserving order
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (serialGate) { body(); }
            });
        }
    }

    public void sync(Action body)
    {
        if (isMain) { body(); return; }
        if (concurrent) { body(); return; }
        lock (serialGate) { body(); }
    }

    /// drain every action enqueued on the main queue (called by the frame pump)
    public void pumpMain()
    {
        if (!isMain) return;
        while (mainBuffer.TryDequeue(out var action))
        {
            action();
        }
    }
}

// =============================================================================
// Constants (the frozen baseline)
// =============================================================================
public static class GameCoreGlobals
{
    public static readonly double TICK_MS = 1000.0 / 20.0;
    public static readonly double REACH_SURVIVAL = 4.5;
    public static readonly double REACH_CREATIVE = 5.0;
    public static readonly double ATTACK_REACH = 3.0;

    internal static readonly int SAVE_INTERVAL_TICKS = 1200;    // 60 s autosave
    internal static readonly int GEN_RADIUS_PAD = 1;            // generate one ring beyond render distance
    internal static readonly int MAX_GEN_INFLIGHT = 24;
    internal static readonly int MAX_MESH_INFLIGHT = 26;
    internal static readonly double LIGHT_BUDGET_MS = 4.0;      // seam-stitch time budget per frame

    /// item-billboard projectiles (the app renders these as sprites)
    public static readonly HashSet<string> SPRITE_TYPES = new HashSet<string>
    {
        "snowball", "egg", "ender_pearl", "xp_bottle", "thrown_potion", "firework",
        "eye_of_ender", "fishing_bobber", "wither_skull", "dragon_fireball", "fireball",
        "shulker_bullet", "llama_spit",
    };
    /// entities that tick regardless of sim distance (bosses roam the whole arena)
    internal static readonly HashSet<string> ALWAYS_TICK = new HashSet<string>
    {
        "ender_dragon", "wither", "warden", "end_crystal", "eye_of_ender", "lightning",
    };

    // =========================================================================
    // Construction-pattern detection (golems, wither)
    // =========================================================================
    public static void tryBuildGolem(World world, int x, int y, int z)
    {
        // snow golem: pumpkin on 2 snow blocks
        if (world.getBlockId(x, y - 1, z) == (int)B.snow_block && world.getBlockId(x, y - 2, z) == (int)B.snow_block)
        {
            world.setBlock(x, y, z, 0);
            world.setBlock(x, y - 1, z, 0);
            world.setBlock(x, y - 2, z, 0);
            spawnMob(world, "snow_golem", (double)x + 0.5, (double)(y - 2), (double)z + 0.5, new SpawnOpts(persistent: true));
            world.hooks.addParticles("block", (double)x + 0.5, (double)(y - 1) + 0.5, (double)z + 0.5, 20, 0.6, (int)cell(B.snow_block));
            return;
        }
        // iron golem: T of iron blocks below pumpkin
        if (world.getBlockId(x, y - 1, z) != (int)B.iron_block || world.getBlockId(x, y - 2, z) != (int)B.iron_block) return;
        foreach (var (ax, az) in new[] { (1, 0), (0, 1) })
        {
            if (world.getBlockId(x - ax, y - 1, z - az) == (int)B.iron_block && world.getBlockId(x + ax, y - 1, z + az) == (int)B.iron_block)
            {
                world.setBlock(x, y, z, 0);
                world.setBlock(x, y - 1, z, 0);
                world.setBlock(x, y - 2, z, 0);
                world.setBlock(x - ax, y - 1, z - az, 0);
                world.setBlock(x + ax, y - 1, z + az, 0);
                // (baseline passes playerMade:true into the loose data bag; nothing reads it)
                spawnMob(world, "iron_golem", (double)x + 0.5, (double)(y - 2), (double)z + 0.5, new SpawnOpts(persistent: true));
                world.hooks.playSound("block.anvil.land", (double)x, (double)y, (double)z, 0.6, 1.2);
                return;
            }
        }
    }

    public static void tryBuildWither(World world, int x, int y, int z)
    {
        bool isSoul(int id) => id == (int)B.soul_sand || id == (int)B.soul_soil;
        bool isSkull(int bx, int by, int bz) => world.getBlockId(bx, by, bz) == (int)B.wither_skeleton_skull;
        foreach (var (ax, az) in new[] { (1, 0), (0, 1) })
        {
            // the skull just placed can be any of the three top positions
            for (int off = -1; off <= 1; off++)
            {
                int cx = x - off * ax, cz = z - off * az;
                if (!isSkull(cx - ax, y, cz - az) || !isSkull(cx, y, cz) || !isSkull(cx + ax, y, cz + az)) continue;
                if (!isSoul(world.getBlockId(cx, y - 1, cz)) || !isSoul(world.getBlockId(cx - ax, y - 1, cz - az)) ||
                    !isSoul(world.getBlockId(cx + ax, y - 1, cz + az)) || !isSoul(world.getBlockId(cx, y - 2, cz))) continue;
                // clear the structure
                foreach (var (dx, dy, dz) in new[] { (-ax, 0, -az), (0, 0, 0), (ax, 0, az), (-ax, -1, -az), (0, -1, 0), (ax, -1, az), (0, -2, 0) })
                {
                    world.setBlock(cx + dx, y + dy, cz + dz, 0);
                }
                var w = spawnMob(world, "wither", (double)cx + 0.5, (double)(y - 2), (double)cz + 0.5, null);
                if (w != null) world.hooks.playSound("entity.wither.spawn", (double)cx, (double)y, (double)cz, 8, 1);
                return;
            }
        }
    }

    // ---- death messages -----------------------------------------------------
    /// "wither_skeleton" → "Wither Skeleton"
    public static string prettyEntityName(string type)
        => string.Join(" ", type.Split('_').Select(s => s.Substring(0, 1).ToUpperInvariant() + s.Substring(1)));

    /// vanilla 1.20 death-message grammar, with attacker names and the
    /// "whilst trying to escape" variants for environmental deaths
    public static string deathCauseText(string source, string attacker = null)
    {
        if (attacker != null)
        {
            var a = attacker;
            switch (source)
            {
                case "mob": case "player": return $"was slain by {a}";
                case "arrow": return $"was shot by {a}";
                case "explosion": return $"was blown up by {a}";
                case "magic": return $"was killed by {a} using magic";
                case "sonic": return $"was sonically charged by {a}";
                case "wither": return $"withered away whilst fighting {a}";
                case "fall": return $"hit the ground too hard whilst trying to escape {a}";
                case "fall_high": return $"fell from a high place whilst trying to escape {a}";
                case "lava": return $"tried to swim in lava to escape {a}";
                case "fire": return $"was burned to a crisp whilst fighting {a}";
                case "drown": return $"drowned whilst trying to escape {a}";
                case "freeze": return $"was frozen to death by {a}";
                case "cactus": return $"walked into a cactus whilst trying to escape {a}";
                default: break;
            }
        }
        switch (source)
        {
            case "void": return "fell out of the world";
            case "fall": return "hit the ground too hard";
            case "fall_high": return "fell from a high place";
            case "lava": return "tried to swim in lava";
            case "fire": return "went up in flames";
            case "fire_tick": return "burned to death";
            case "drown": return "drowned";
            case "starve": return "starved to death";
            case "explosion": return "blew up";
            case "magic": return "was killed by magic";
            case "arrow": return "was shot";
            case "wither": return "withered away";
            case "freeze": return "froze to death";
            case "lightning": return "was struck by lightning";
            case "sonic": return "was sonically charged";
            case "fly_into_wall": return "experienced kinetic energy";
            case "cactus": return "was pricked to death";
            case "sweet_berry": return "was poked to death by a sweet berry bush";
            case "anvil": return "was squashed by a falling anvil";
            case "falling_block": return "was squashed by a falling block";
            case "stalagmite": return "was impaled on a stalagmite";
            case "suffocate": return "suffocated in a wall";
            case "mob": case "player": return "was slain";
            default: return "died";
        }
    }

    // ---- small math helpers -------------------------------------------------
    internal static double wrapAngle(double a)
    {
        while (a > Math.PI) a -= Math.PI * 2;
        while (a < -Math.PI) a += Math.PI * 2;
        return a;
    }

    internal static double? rayBoxT(double ox, double oy, double oz,
                                    double dx, double dy, double dz,
                                    double x0, double y0, double z0,
                                    double x1, double y1, double z1)
    {
        double tmin = 0.0, tmax = double.PositiveInfinity;
        var axes = new[] { (ox, dx, x0, x1), (oy, dy, y0, y1), (oz, dz, z0, z1) };
        foreach (var (o, d, lo, hi) in axes)
        {
            if (Math.Abs(d) < 1e-9)
            {
                if (o < lo || o > hi) return null;
            }
            else
            {
                double t1 = (lo - o) / d, t2 = (hi - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return null;
            }
        }
        return tmin;
    }

    /// deterministic ToInt32 (the `| 0` coercion) for seed-text parity with the golden baselines
    internal static int wrapToInt32(double d)
    {
        if (!double.IsFinite(d)) return 0;
        double m = d % 4294967296.0;
        uint u = unchecked((uint)(long)m);
        return unchecked((int)u);
    }
}

// =============================================================================
// Load-path profiler (PEBBLE_PROF=1) — aggregates per-stage wall time
// =============================================================================
public sealed class LoadProf
{
    public static readonly LoadProf shared = new LoadProf();
    public readonly bool enabled = Environment.GetEnvironmentVariable("PEBBLE_PROF") != null;
    private readonly object lockObj = new object();
    private Dictionary<string, (int count, double ms)> buckets = new Dictionary<string, (int, double)>();
    private double lastPrint = nowSeconds();

    private static double nowSeconds() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public T time<T>(string name, Func<T> body)
    {
        if (!enabled) return body();
        double t0 = nowSeconds();
        var r = body();
        double ms = (nowSeconds() - t0) * 1000;
        lock (lockObj)
        {
            var b = buckets.TryGetValue(name, out var v) ? v : (0, 0.0);
            buckets[name] = (b.Item1 + 1, b.Item2 + ms);
        }
        return r;
    }

    public void time(string name, Action body)
    {
        if (!enabled) { body(); return; }
        double t0 = nowSeconds();
        body();
        double ms = (nowSeconds() - t0) * 1000;
        lock (lockObj)
        {
            var b = buckets.TryGetValue(name, out var v) ? v : (0, 0.0);
            buckets[name] = (b.Item1 + 1, b.Item2 + ms);
        }
    }

    public void tickPrint()
    {
        if (!enabled) return;
        double now = nowSeconds();
        if (now - lastPrint < 2) return;
        lastPrint = now;
        Dictionary<string, (int count, double ms)> snap;
        lock (lockObj) { snap = new Dictionary<string, (int, double)>(buckets); }
        string line = string.Join("  ", snap
            .OrderByDescending(kv => kv.Value.ms)
            .Select(kv => string.Format(CultureInfo.InvariantCulture, "{0} {1:F0}ms/{2}({3:F1})",
                kv.Key, kv.Value.ms, kv.Value.count, kv.Value.ms / Math.Max(1, kv.Value.count))));
        Console.WriteLine("[prof] " + line);
        Console.Out.Flush();
    }
}

// =============================================================================
// Host surface — the app implements this (screens, HUD, audio, renderer)
// =============================================================================
public struct BossBarInfo
{
    public readonly string name;
    public readonly double progress;
    public readonly string color;
    public BossBarInfo(string name, double progress, string color)
    {
        this.name = name;
        this.progress = progress;
        this.color = color;
    }
}

public interface GameHost
{
    // screens
    bool hasScreen();
    bool screenPausesGame();
    void openScreen(string kind, ScreenData? data);
    void openTrading(Mob villager);
    void openVehicleChest(string kind, Entity vehicle);
    void openChat(string prefix);
    void openDeathScreen(string message);
    void openPauseScreen();
    void openTitleScreen();
    void closeAllScreens();
    void releasePointer();
    // HUD / chat
    void showActionBar(string text, int time);
    void pushChat(string line);
    void pushToast(AdvancementDef adv);
    void setBossBars(List<BossBarInfo> bars);
    // audio
    void playSound(string name, double x, double y, double z, double volume, double pitch);
    void playUI(string name);
    void setAudioEnvironment(bool underwater, double caveFactor);
    void setAudioListener(double x, double y, double z, double yaw);
    void tickMusic(string mood, bool enabled);
    void stopDisc();
    // particles (count already scaled by the particles setting)
    void addParticles(string type, double x, double y, double z, int count, double spread, int cell);
    void spawnPrecipitation(string kind, double x, double y, double z, double groundY);
    // renderer
    void uploadMesh(int cx, int sy, int cz, int minY, MeshOutput mesh);
    void removeChunkMeshes(int cx, int cz, int sections);
    void clearAllSections();
}

/// interpolated camera for the renderer — reference implementation CameraState
public struct CamState
{
    public double x, y, z;
    public double yaw, pitch;
    public double fov;
    public bool underwater;
    public bool underLava;
    public bool powderSnow;
    public double portalWarp;
    public double nightVision;
    public double darkness;
    public double blindness;

    public static CamState Create() => new CamState { fov = 70.0 };
}

// =============================================================================
// Streaming bookkeeping keys
// =============================================================================
internal struct SectionPos : IEquatable<SectionPos>
{
    public readonly int cx, sy, cz;
    public SectionPos(int cx, int sy, int cz) { this.cx = cx; this.sy = sy; this.cz = cz; }
    public bool Equals(SectionPos o) => cx == o.cx && sy == o.sy && cz == o.cz;
    public override bool Equals(object o) => o is SectionPos p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(cx, sy, cz);
}
internal struct DimSection : IEquatable<DimSection>
{
    public readonly int dim;
    public readonly SectionPos pos;
    public DimSection(int dim, SectionPos pos) { this.dim = dim; this.pos = pos; }
    public bool Equals(DimSection o) => dim == o.dim && pos.Equals(o.pos);
    public override bool Equals(object o) => o is DimSection d && Equals(d);
    public override int GetHashCode() => HashCode.Combine(dim, pos);
}
internal struct DimChunk : IEquatable<DimChunk>
{
    public readonly int dim;
    public readonly long key;
    public DimChunk(int dim, long key) { this.dim = dim; this.key = key; }
    public bool Equals(DimChunk o) => dim == o.dim && key == o.key;
    public override bool Equals(object o) => o is DimChunk d && Equals(d);
    public override int GetHashCode() => HashCode.Combine(dim, key);
}
internal sealed class MeshJobState
{
    public bool dirtyAgain = false;
}

// =============================================================================
// The Game
// =============================================================================
public sealed class GameCore
{
    public GameHost host;
    public readonly SaveDB db = new SaveDB();
    public Settings settings;
    public Dictionary<string, string> keybinds;

    // world state
    public Dictionary<Dim, World> worlds = new Dictionary<Dim, World>();
    public Dim dim = Dim.overworld;
    public Player player;
    public WorldRecord? worldRec;
    public AdvancementTracker advancements = new AdvancementTracker();
    public bool inWorld { get; private set; } = false;
    public bool paused { get; private set; } = false;

    // streaming
    private HashSet<DimChunk> genInFlight = new HashSet<DimChunk>();
    /// keys of chunks that exist on disk — fresh chunks skip the read entirely
    private HashSet<string> savedChunkKeys = new HashSet<string>();
    /// keys whose DB record holds full block data — an unload rewrite of these
    /// must emit a full record again or the blocks are lost (entity-only stubs
    /// REPLACE the row)
    private HashSet<string> savedFullKeys = new HashSet<string>();
    /// chunks awaiting initial lighting, processed under a per-frame budget
    private Dictionary<Dim, HashSet<long>> lightQueue = new Dictionary<Dim, HashSet<long>>();
    private Dictionary<Dim, HashSet<SectionPos>> dirtySections = new Dictionary<Dim, HashSet<SectionPos>>();
    /// sections whose neighborhood isn't ready — parked so the per-frame dirty
    /// scan doesn't rescan the streaming frontier forever; retried once per second
    private Dictionary<Dim, HashSet<SectionPos>> stalledSections = new Dictionary<Dim, HashSet<SectionPos>>();
    private Dictionary<DimSection, MeshJobState> meshJobs = new Dictionary<DimSection, MeshJobState>();
    public int meshedThisSecond { get; private set; } = 0;
    public int lastChunkUpdates = 0;
    /// unload records awaiting the once-per-second batched write
    private Dictionary<string, ChunkRecord> pendingChunkSaves = new Dictionary<string, ChunkRecord>();

    private readonly DispatchQueue genQueue = new DispatchQueue(concurrent: true);
    private readonly DispatchQueue meshQueue = new DispatchQueue(concurrent: true);
    private readonly DispatchQueue saveQueue = new DispatchQueue(concurrent: false);

    // input
    private HashSet<string> keys = new HashSet<string>();
    private bool leftDown = false;
    private bool rightDown = false;
    private int useCooldown = 0;
    private int breakCooldown = 0;
    private int lastLightHealTick = -1;
    private int lastSlot = 0;
    public int heldNameTime = 0;
    public (int x, int y, int z, int cell)? targetedBlock;
    public int perspective = 0;          // 0 first, 1 back, 2 front
    private bool sprintHeld = false;
    private double lastJumpPress = 0.0;
    private double lastForwardPress = 0.0;

    // loop
    private double accumulator = 0.0;
    private int ticksSinceSave = 0;

    // bookkeeping for vanilla feel — bob advances per TICK (frame-rate
    // independent) and camState interpolates; mutating it per frame made the
    // camera shake violently once the fps cap was lifted
    private double bobPhase = 0.0;
    private double prevBobPhase = 0.0;
    private double bobAmp = 0.0;
    private double prevBobAmp = 0.0;
    private double fovScale = 1.0;
    private double prevFovScale = 1.0;
    public double portalWarp { get; private set; } = 0.0;
    private bool dragonSpawned = false;
    private int brushTicks = 0;
    private bool deathScreenShown = false;
    /// a portal/respawn chunk-load is in flight — player is held in place
    private bool traveling = false;
    /// player is frozen because the chunk under them hasn't streamed in yet
    private bool heldForChunks = false;
    public string musicMood { get; private set; } = "menu";

    /// non-deterministic native randomness (seed picking, world ids, cosmetic
    /// particle jitter) — Swift used Int32.random / Double.random, which are
    /// system-seeded; nothing here feeds the golden baselines
    private readonly Random nativeRng = new Random();

    public GameCore()
    {
        settings = loadSettings();
        keybinds = loadKeybinds();
        // registry boot, in frozen order
        registerAllBlocks();
        registerAllItems();
        registerAllBiomes();
        registerAllRecipes();
        registerAllLootTables();
        registerAllEntities();
        registerAllSystems();
        foreach (var d in new[] { Dim.overworld, Dim.nether, Dim.end })
        {
            dirtySections[d] = new HashSet<SectionPos>();
            lightQueue[d] = new HashSet<long>();
        }
        onPlacedHandlers[(int)B.carved_pumpkin] = (w, x, y, z, _) => tryBuildGolem(w, x, y, z);
        onPlacedHandlers[(int)B.wither_skeleton_skull] = (w, x, y, z, _) => tryBuildWither(w, x, y, z);
        bindCrystalDestroyed((crystal, attacker) =>
        {
            var dragon = crystal.world.entities.FirstOrDefault(e =>
                (e as Entity)?.type == "ender_dragon" && !e.dead) as EnderDragon;
            if (dragon != null)
            {
                dragon.hurt(10, "explosion");
                dragon.pathAngle += 1.5;
            }
            if (attacker is Player) advance("free_the_end_crystal");
        });
        // screen-opening hooks fired from entity interactions
        bindOpenTrading((player, villager) =>
        {
            if (!(player is Player)) return;
            host?.openTrading(villager);
            advanceLater("trade_villager");
        });
        openContainerScreenFn = (player, kind, vehicle) =>
        {
            if (!(player is Player)) return;
            host?.openVehicleChest(kind, vehicle);
        };
    }

    // ===========================================================================
    // GameUI / MenuHost surface
    // ===========================================================================
    public World world => worlds[dim];
    public bool hasWorld() => inWorld;

    public void playUISound(string name)
    {
        host?.playUI(name);
    }

    public void advance(string id)
    {
        if (!inWorld) return;
        if (advancements.grant(id))
        {
            playUISound("ui.toast.challenge_complete");
        }
    }
    /// queue an advancement next runloop turn (screens may open before tracker updates)
    private void advanceLater(string id)
    {
        DispatchQueue.main.async(() => advance(id));
    }

    public void applySettings()
    {
        saveSettings(settings);
        saveKeybinds(keybinds);
    }

    public void respawnPlayer()
    {
        if (traveling) return;
        traveling = true;
        try
        {
            var p = player;
            deathScreenShown = false;
            // respawn at bed / anchor / world spawn
            (double, double, double)? dest = null;
            Dim destDim = Enum.IsDefined(typeof(Dim), p.spawnDim) ? (Dim)p.spawnDim : Dim.overworld;
            if (p.spawnPoint != null)
            {
                var sp = p.spawnPoint.Value;
                var w = worlds[destDim];
                ensureChunksLoaded(w, floorDiv(sp.Item1, 16), floorDiv(sp.Item3, 16), 1);
                int sx = sp.Item1, sy = sp.Item2, sz = sp.Item3;
                int id = w.getBlockId(sx, sy, sz);
                var def = blockDefs[id];
                if (SHAPE_OF[id] == (byte)Shape.bed || def.name == "respawn_anchor")
                {
                    if (def.name == "respawn_anchor")
                    {
                        int charge = w.getMeta(sx, sy, sz);
                        if (charge > 0)
                        {
                            w.setBlock(sx, sy, sz, (int)cell((ushort)id, charge - 1));
                            dest = ((double)sx + 0.5, (double)(sy + 1), (double)sz + 0.5);
                        }
                    }
                    else
                    {
                        dest = ((double)sx + 0.5, (double)sy + 0.6, (double)sz + 0.5);
                    }
                }
            }
            if (dest == null)
            {
                destDim = Dim.overworld;
                var w = worlds[Dim.overworld];
                ensureChunksLoaded(w, floorDiv((int)w.spawnX, 16), floorDiv((int)w.spawnZ, 16), 1);
                dest = (w.spawnX + 0.5, (double)w.surfaceY((int)w.spawnX, (int)w.spawnZ), w.spawnZ + 0.5);
            }
            if (destDim != dim) moveToDimension(destDim);
            p.respawn();
            p.setPos(dest.Value.Item1, dest.Value.Item2, dest.Value.Item3);
            p.insidePortalKind = null;
            p.portalTicks = 0;
        }
        finally { traveling = false; }
    }

    public void exitToTitle()
    {
        if (inWorld) saveAndFlush(synchronous: true);
        inWorld = false;
        worldRec = null;
        dragonSpawned = false;
        deathScreenShown = false;
        worlds.Clear();
        foreach (var d in dirtySections.Keys.ToList()) dirtySections[d].Clear();
        stalledSections.Clear();
        foreach (var d in lightQueue.Keys.ToList()) lightQueue[d].Clear();
        meshJobs.Clear();
        genInFlight.Clear();
        savedChunkKeys.Clear();
        savedFullKeys.Clear();
        clearEntityTimeouts();
        host?.clearAllSections();
        host?.setBossBars(new List<BossBarInfo>());
        host?.stopDisc();
        host?.releasePointer();
        host?.closeAllScreens();
        host?.openTitleScreen();
    }

    // ---- MenuHost ----
    public List<WorldRecord> listWorlds()
    {
        return db.listWorlds();
    }

    public void createWorld(string name, string seedText, int mode, int difficulty)
    {
        string trimmed = seedText.Trim();
        int seed;
        if (trimmed.Length == 0)
        {
            seed = nativeRng.Next(0, 0x7fffffff);
        }
        else if (Regex.IsMatch(trimmed, "^-?\\d+$"))
        {
            seed = wrapToInt32(double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv) ? dv : 0);
        }
        else
        {
            // (seed * 31 + ch.charCodeAt(0)) | 0 over code points
            seed = 0;
            foreach (char unit in trimmed)
            {
                seed = unchecked(seed * 31 + (int)unit);
            }
        }
        long ms = (long)((DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalMilliseconds);
        string id = "w" + toBase36(ms) + toBase36(nativeRng.Next(0, 1_000_000));
        var rec = new WorldRecord(id, name, seed, mode, difficulty);
        // pick a spawn: walk outward for a land biome
        var gen = overworldGen(unchecked((uint)seed));
        int sx = 8, sz = 8;
        for (int r = 0; r < 40; r++)
        {
            int tx = 8 + r * 40, tz = 8 + ((r * 13) % 7 - 3) * 40;
            var b = gen.surfaceBiomeAt((double)tx, (double)tz);
            int h = gen.heightEstimate((double)tx, (double)tz);
            int bi = (int)b;
            string bname = ((bi >= 0 && bi < BIOMES.Count && BIOMES[bi] != null) ? BIOMES[bi].name : "").ToLowerInvariant();
            if (h > DIMS[(int)Dim.overworld].seaLevel && !bname.Contains("ocean") && !bname.Contains("river"))
            {
                sx = tx;
                sz = tz;
                break;
            }
        }
        rec.spawnX = sx;
        rec.spawnZ = sz;
        rec.spawnY = gen.heightEstimate((double)sx, (double)sz) + 1;
        db.putWorld(rec);
        enterWorld(rec, null, null);
    }

    public void loadWorld(string id)
    {
        var rec = db.getWorld(id);
        if (rec == null) return;
        var playerData = db.getPlayer(id);
        var adv = db.getAdvancements(id);
        enterWorld(rec.Value, playerData, adv);
    }

    public void deleteWorld(string id)
    {
        db.deleteWorld(id);
    }

    // ===========================================================================
    // World lifecycle
    // ===========================================================================
    private void enterWorld(WorldRecord rec, Dictionary<string, object> playerData, List<string> adv)
    {
        worldRec = rec;
        advancements = new AdvancementTracker();
        if (adv != null) advancements.load(adv);
        dragonSpawned = false;
        worlds.Clear();
        resetEntityIds(Math.Max(1, rec.nextEntityId));
        foreach (var d in new[] { Dim.overworld, Dim.nether, Dim.end })
        {
            var w = new World(d, unchecked((uint)rec.seed));
            if (rec.dims.TryGetValue($"{(int)d}", out var ds))
            {
                w.time = ds.time;
                w.dayTime = ds.dayTime;
                w.raining = ds.raining;
                w.thundering = ds.thundering;
                w.weatherTimer = ds.weatherTimer;
                w.rainLevel = ds.raining ? 1 : 0;
                w.thunderLevel = ds.thundering ? 1 : 0;
            }
            w.difficulty = rec.difficulty;
            foreach (var kv in rec.gameRules) w.gameRules[kv.Key] = kv.Value;
            w.spawnX = (double)rec.spawnX;
            w.spawnY = (double)rec.spawnY;
            w.spawnZ = (double)rec.spawnZ;
            hookWorld(w);
            worlds[d] = w;
        }
        savedChunkKeys = db.getChunkKeys(rec.id);
        dim = Enum.IsDefined(typeof(Dim), playerDataInt(playerData, "dim", 0)) ? (Dim)playerDataInt(playerData, "dim", 0) : Dim.overworld;
        var world0 = world;
        player = new Player(world0);
        player.setGameMode(rec.gameMode);
        if (playerData != null && playerData.TryGetValue("data", out var pdObj) && pdObj is Dictionary<string, object> pd)
        {
            player.load(pd);
            // a corrupted save (NaN position from an old physics blowup) renders
            // nothing at all — snap back to world spawn instead
            if (!double.IsFinite(player.x) || !double.IsFinite(player.y) || !double.IsFinite(player.z))
            {
                player.setPos((double)rec.spawnX + 0.5, (double)(rec.spawnY + 1), (double)rec.spawnZ + 0.5);
                player.vx = 0; player.vy = 0; player.vz = 0;
            }
        }
        else
        {
            player.setPos((double)rec.spawnX + 0.5, (double)(rec.spawnY + 1), (double)rec.spawnZ + 0.5);
            // starter nothing — vanilla survival starts empty-handed
        }
        world0.addEntity(player);

        // spawn area must exist before the first tick — saved copies are read so
        // edits near spawn aren't shadowed by fresh generation
        int pcx = floorDiv(ifloor(player.x), 16), pcz = floorDiv(ifloor(player.z), 16);
        ensureChunksLoaded(world0, pcx, pcz, 1);
        if (playerData == null)
        {
            int sy = world0.surfaceY(rec.spawnX, rec.spawnZ);
            player.setPos((double)rec.spawnX + 0.5, (double)sy, (double)rec.spawnZ + 0.5);
        }
        // if loading into the End with a living fight, re-arm the dragon hook
        foreach (var e in world0.entities)
        {
            if (e is EnderDragon d) armDragon(d);
        }

        inWorld = true;
        deathScreenShown = false;
        ticksSinceSave = 0;
        host?.closeAllScreens();
        host?.showActionBar($"§e{rec.name}§r — seed {rec.seed}", 60);
        // loaded in deep underground? say so loudly instead of looking like a render bug
        int bx = ifloor(player.x), bz = ifloor(player.z);
        if (world0.info.hasSky && (double)world0.heightAt(bx, bz) > player.eyeY() + 10)
        {
            host?.pushChat("§eYou are deep underground. Type §f/surface§e to climb out.");
            host?.showActionBar("§eDeep underground — press T, type §f/surface", 400);
        }
    }

    /// `playerData["dim"]` read with the same coercion as Swift's
    /// `(playerData?["dim"] as? NSNumber)?.intValue ?? 0`
    private static int playerDataInt(Dictionary<string, object> playerData, string key, int fallback)
    {
        if (playerData == null) return fallback;
        if (!playerData.TryGetValue(key, out var v) || v == null) return fallback;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private void hookWorld(World w)
    {
        var hooks = new WorldHooks();
        hooks.onSectionDirty = (cx, cz, sy) =>
        {
            dirtySections[w.dim].Add(new SectionPos(cx, sy, cz));
        };
        hooks.playSound = (name, x, y, z, volume, pitch) =>
        {
            if (!ReferenceEquals(w, worldsOrNull(dim))) return;
            host?.playSound(name, x, y, z, volume, pitch);
        };
        hooks.addParticles = (type, x, y, z, count, spread, data) =>
        {
            if (!ReferenceEquals(w, worldsOrNull(dim))) return;
            double mult = new[] { 0.3, 0.65, 1.0 }[Math.Min(2, Math.Max(0, settings.particles))];
            int n = Math.Max(1, (int)Math.Round((double)count * mult, MidpointRounding.AwayFromZero));
            host?.addParticles(type, x, y, z, n, spread, data);
        };
        hooks.onVibration = (x, y, z, freq, src) =>
        {
            handleVibration(w, x, y, z, freq);
            foreach (var e in w.getEntitiesNear(x, y, z, 24, filter: e => (e as Entity)?.type == "warden"))
            {
                (e as Warden)?.hearVibration(x, y, z, src as Entity);
            }
        };
        hooks.requestChunk = (cx, cz) =>
        {
            requestChunk(w, cx, cz);
        };
        w.hooks = hooks;
    }

    private World worldsOrNull(Dim d) => worlds.TryGetValue(d, out var w) ? w : null;

    public void saveAndFlush(bool synchronous = false)
    {
        if (!inWorld || worldRec == null) return;
        var rec = worldRec.Value;
        rec.lastPlayed = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalMilliseconds;
        rec.gameMode = player.gameMode;
        rec.nextEntityId = peekNextEntityId();
        foreach (var kv in worlds)
        {
            var d = kv.Key; var w = kv.Value;
            rec.dims[$"{(int)d}"] = new DimState(
                time: w.time, dayTime: w.dayTime,
                raining: w.raining, thundering: w.thundering, weatherTimer: w.weatherTimer);
        }
        // rules/difficulty are world-global (kept in sync across dims by
        // setGameRule/setDifficulty) — read one deterministic source
        if (worlds.TryGetValue(dim, out var cur))
        {
            rec.difficulty = cur.difficulty;
            rec.gameRules = cur.gameRules;
        }
        worldRec = rec;
        db.putWorld(rec);
        db.putPlayer(rec.id, new Dictionary<string, object> { ["dim"] = (int)dim, ["data"] = player.save() });
        db.putAdvancements(rec.id, advancements.save());
        // all modified chunks across all dims
        var records = new List<ChunkRecord>();
        foreach (var kv in worlds)
        {
            var d = kv.Key; var w = kv.Value;
            foreach (var c in w.chunks.Values)
            {
                if (c.modified) records.Add(chunkRecord(rec.id, d, w, c));
            }
        }
        // include any unload records still waiting in the batch buffer
        foreach (var r in pendingChunkSaves.Values) records.Add(r);
        pendingChunkSaves.Clear();
        foreach (var r in records) savedChunkKeys.Add(r.key);
        if (synchronous)
        {
            saveQueue.sync(() => writeChunkBatch(records));
        }
        else
        {
            saveQueue.async(() => writeChunkBatch(records));
        }
        foreach (var w in worlds.Values)
        {
            foreach (var c in w.chunks.Values) c.modified = false;
        }
    }

    /// runs ON the save queue; on failure re-marks the chunks dirty (on main)
    /// so the next autosave retries instead of silently losing the edits
    private void writeChunkBatch(List<ChunkRecord> records)
    {
        if (db.putChunks(records)) return;
        DispatchQueue.main.async(() =>
        {
            Console.WriteLine($"[saves] chunk batch failed — re-marking {records.Count} chunks dirty for retry");
            foreach (var r in records)
            {
                if (!Enum.IsDefined(typeof(Dim), r.dim) || !worlds.TryGetValue((Dim)r.dim, out var w)) continue;
                if (w.chunks.TryGetValue(chunkKey(r.cx, r.cz), out var c))
                {
                    c.modified = true;
                }
                else
                {
                    // already unloaded — requeue the record itself
                    pendingChunkSaves[r.key] = r;
                }
            }
        });
    }

    /// Difficulty is world-global: apply to every dimension so the value can't
    /// drift (and can't depend on which dim happens to save last).
    public void setDifficulty(int d)
    {
        foreach (var w in worlds.Values) w.difficulty = d;
        if (worldRec != null) { var r = worldRec.Value; r.difficulty = d; worldRec = r; }
    }

    /// Game rules are world-global: apply to every dimension.
    public void setGameRule(string rule, double value)
    {
        foreach (var w in worlds.Values) w.gameRules[rule] = value;
        if (worldRec != null) { var r = worldRec.Value; r.gameRules[rule] = value; worldRec = r; }
    }

    private ChunkRecord chunkRecord(string worldId, Dim d, World w, Chunk c)
    {
        // persist entities standing in this chunk (skip player + transient)
        var ents = new List<Dictionary<string, object>>();
        foreach (var e in w.entities)
        {
            var ent = e as Entity;
            if (ent == null || ent.isPlayer || ent.dead) continue;
            if (floorDiv(ifloor(ent.x), 16) != c.cx || floorDiv(ifloor(ent.z), 16) != c.cz) continue;
            if ((ent.type == "item" || ent.type == "xp_orb") && ent.age > 4000) continue;
            ents.Add(ent.save());
        }
        string key = db.chunkKey(worldId, (int)d, c.cx, c.cz);
        if (!c.modified && !savedFullKeys.Contains(key))
        {
            // entity-only record (~KBs): the blocks regenerate from seed
            return new ChunkRecord(key: key, worldId: worldId, dim: (int)d, cx: c.cx, cz: c.cz, entities: ents);
        }
        // once a full record exists on disk, every rewrite must stay full —
        // the autosave clears `modified` but the blocks no longer regenerate
        savedFullKeys.Add(key);
        // deep-copy block entities — live BEs keep mutating while the save
        // queue serializes, and BlockEntityData is a reference type
        List<BlockEntityData> besCopy = null;
        var besLive = c.blockEntities.Values.ToList();
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(besLive);
            besCopy = JsonSerializer.Deserialize<List<BlockEntityData>>(json);
        }
        catch { besCopy = null; }
        return new ChunkRecord(
            key: key, worldId: worldId, dim: (int)d, cx: c.cx, cz: c.cz,
            blocks: c.blocks, biomes: c.biomes, blockEntities: besCopy ?? besLive, entities: ents);
    }

    // ===========================================================================
    // Chunk streaming
    // ===========================================================================
    private void requestChunk(World w, int cx, int cz)
    {
        long key = chunkKey(cx, cz);
        var flight = new DimChunk((int)w.dim, key);
        if (w.chunks.ContainsKey(key) || genInFlight.Contains(flight)) return;
        if (genInFlight.Count >= MAX_GEN_INFLIGHT) return;
        if (worldRec == null) return;
        var rec = worldRec.Value;
        genInFlight.Add(flight);
        string worldId = rec.id;
        Dim d = w.dim;
        uint seed = w.seed;
        int height = w.info.height;
        bool hasSky = w.info.hasSky;
        var db = this.db;
        bool saved = savedChunkKeys.Contains(db.chunkKey(worldId, (int)d, cx, cz));
        int minY = w.info.minY;
        genQueue.async(() =>
        {
            ChunkRecord? savedRec = null;
            if (saved) savedRec = db.getChunk(worldId, (int)d, cx, cz);
            Chunk c;
            List<BESpec> beSpecs = null;
            List<EntitySpec> entitySpecs = null;
            bool loadedFull = false;
            if (savedRec != null && recordUsable(savedRec.Value, height))
            {
                // saved chunk: relight the stored blocks
                loadedFull = true;
                var light = LoadProf.shared.time("light", () => computeLocalLight(savedRec.Value.blocks, height, hasSky));
                c = LoadProf.shared.time("mkchunk", () => makeChunk(cx, cz, minY, height, savedRec.Value.blocks, savedRec.Value.biomes, light.sky, light.blk));
            }
            else
            {
                // fresh generation; a corrupt/entity-only record still re-attaches its entities
                var outg = LoadProf.shared.time("gen", () => generateChunk(d, seed, cx, cz));
                var light = LoadProf.shared.time("light", () => computeLocalLight(outg.blocks, height, hasSky));
                c = LoadProf.shared.time("mkchunk", () => makeChunk(cx, cz, minY, height, outg.blocks, outg.biomes, light.sky, light.blk));
                beSpecs = outg.blockEntities;
                entitySpecs = savedRec != null ? null : outg.entities;
            }
            var savedFinal = savedRec;
            DispatchQueue.main.async(() =>
            {
                genInFlight.Remove(flight);
                if (!inWorld || !ReferenceEquals(worldsOrNull(d), w) || w.chunks.ContainsKey(key)) return;
                if (loadedFull) savedFullKeys.Add(db.chunkKey(worldId, (int)d, cx, cz));
                adoptChunk(w, c, beSpecs, entitySpecs, savedFinal);
                enqueueLightAround(w, cx, cz);
            });
        });
    }

    /// A saved record is only trustworthy if its arrays have the exact expected sizes
    private static bool recordUsable(ChunkRecord saved, int height)
    {
        if (saved.blocks == null || saved.biomes == null) return false;
        if (saved.blocks.Length != 16 * 16 * height) return false;
        if (saved.biomes.Length != 4 * 4 * ((height + 3) / 4)) return false;
        return true;
    }

    /// heavy chunk assembly (heightmap + special scan) — safe off-main, the
    /// chunk isn't shared until adoptChunk
    private static Chunk makeChunk(
        int cx, int cz, int minY, int height,
        ushort[] blocks, byte[] biomes,
        byte[] skyLight, byte[] blockLight)
    {
        var c = new Chunk(cx, cz, minY, height);
        c.blocks = blocks;
        c.biomes = biomes;
        if (skyLight != null) c.skyLight = skyLight;
        if (blockLight != null) c.blockLight = blockLight;
        c.buildHeightmap();
        c.scanSpecials();
        c.status = Chunk.ChunkStatus.generated;
        return c;
    }

    private void adoptChunk(
        World w, Chunk c,
        List<BESpec> beSpecs, List<EntitySpec> entitySpecs,
        ChunkRecord? saved)
    {
        int cx = c.cx, cz = c.cz;
        _ = (cx, cz);
        w.setChunk(c);
        // block entities: a full saved record carries them verbatim; otherwise
        // worldgen specs resolve deterministically
        if (saved != null && saved.Value.blockEntities != null)
        {
            foreach (var be in saved.Value.blockEntities)
            {
                c.setBlockEntity(posMod(be.x, 16), be.y, posMod(be.z, 16), be);
            }
            c.modified = true; // remains a fully-saved chunk
        }
        else if (beSpecs != null)
        {
            foreach (var spec in beSpecs) resolveBESpec(w, c, spec);
            c.modified = false;
        }
        w.adoptChunkBlockEntities(c);
        // entities: any saved record (full or entity-only) overrides worldgen spawns
        if (saved != null)
        {
            foreach (var ed in saved.Value.entities)
            {
                var e = loadEntity(w, ed);
                if (e != null)
                {
                    w.addEntity(e);
                    if (e is EnderDragon dragon) armDragon(dragon);
                }
            }
        }
        else if (entitySpecs != null)
        {
            foreach (var es in entitySpecs)
            {
                var m = spawnMob(w, es.mob, es.x, es.y, es.z, spawnOptsFrom(es.data));
                if (m != null) m.persistent = true;
            }
        }
    }

    private SpawnOpts spawnOptsFrom(Dictionary<string, BEValue> data)
    {
        var opts = new SpawnOpts();
        if (data.TryGetValue("baby", out var b0) && b0.tag == BEValue.Tag.@bool) opts.baby = b0.boolVal;
        if (data.TryGetValue("persistent", out var b1) && b1.tag == BEValue.Tag.@bool) opts.persistent = b1.boolVal;
        if (data.TryGetValue("captain", out var b2) && b2.tag == BEValue.Tag.@bool) opts.captain = b2.boolVal;
        if (data.TryGetValue("size", out var n0) && n0.tag == BEValue.Tag.num) opts.size = (int)n0.numVal;
        if (data.TryGetValue("variant", out var n1) && n1.tag == BEValue.Tag.num) opts.variant = (int)n1.numVal;
        return opts;
    }

    /// worldgen block-entity specs → live block entities
    private void resolveBESpec(World w, Chunk c, BESpec spec)
    {
        int x = spec.x, y = spec.y, z = spec.z;
        int lx = posMod(x, 16), lz = posMod(z, 16);
        void put(BlockEntityData be) { c.setBlockEntity(lx, y, lz, be); }
        string str(string k)
        {
            if (spec.data.TryGetValue(k, out var v) && v.tag == BEValue.Tag.str) return v.strVal;
            return null;
        }
        double? num(string k)
        {
            if (spec.data.TryGetValue(k, out var v) && v.tag == BEValue.Tag.num) return v.numVal;
            return null;
        }
        bool? boolOf(string k)
        {
            if (spec.data.TryGetValue(k, out var v) && v.tag == BEValue.Tag.@bool) return v.boolVal;
            return null;
        }
        var rng = new RandomX(hash3(w.seed ^ 0xBE5, x, y, z));
        switch (spec.kind)
        {
            case "chest_loot":
            {
                var be = makeContainerBE(x, y, z, 27);
                var lootRng = new RandomX(unchecked((uint)(long)(num("seed") ?? 0)));
                var items = rollLoot(str("lootTable") ?? "", ref lootRng, luck: 0);
                foreach (var s in items)
                {
                    int slot = rng.nextInt(27);
                    int i = 0;
                    while (i < 27 && be.items[slot] != null)
                    {
                        slot = (slot + 1) % 27;
                        i += 1;
                    }
                    be.items[slot] = s;
                }
                be.lootTable = str("lootTable");
                put(be);
                break;
            }
            case "elytra_chest":
            {
                var be = makeContainerBE(x, y, z, 27);
                be.items[13] = new ItemStack(iid("elytra"), 1);
                var extra = rollLoot("end_city_treasure", ref rng, luck: 0);
                foreach (var s in extra.Take(4))
                {
                    int slot = rng.nextInt(27);
                    if (be.items[slot] == null) be.items[slot] = s;
                }
                put(be);
                break;
            }
            case "dispenser_arrows":
            {
                var be = makeContainerBE(x, y, z, 9);
                be.items[4] = new ItemStack(iid("arrow"), 2 + rng.nextInt(7));
                put(be);
                break;
            }
            case "spawner":
                put(makeSpawnerBE(x, y, z, str("mob") ?? "zombie"));
                break;
            case "brushable":
                put(makeBrushableBE(x, y, z, str("lootTable") ?? "trail_ruins_common", (int)hash3(w.seed, x, y, z)));
                break;
            case "beehive":
            {
                var be = new BlockEntityData("beehive", x, y, z);
                be.bees = (int)(num("bees") ?? 3);
                be.honey = 0;
                put(be);
                break;
            }
            case "pot_plant":
            {
                var be = new BlockEntityData("lectern", x, y, z);
                be.plant = str("plant") ?? "poppy";
                put(be);
                break;
            }
            case "pot_sherds":
            {
                var pool = new string[] { "archer", "prize", "arms_up", "skull", "heart", "heartbreak", "howl", "sheaf", null, null };
                var be = new BlockEntityData("pot", x, y, z);
                be.sherds = Enumerable.Range(0, 4).Select(_ => pool[rng.nextInt(pool.Length)]).ToArray();
                put(be);
                break;
            }
            case "shrieker":
            {
                var be = new BlockEntityData("shrieker", x, y, z);
                be.canSummon = boolOf("canSummon") ?? true;
                be.shrieking = 0;
                put(be);
                break;
            }
            default:
                break;
        }
    }

    /// queue every chunk around (cx,cz) whose 3×3 neighborhood is now generated
    private void enqueueLightAround(World w, int cx, int cz)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                var c = w.getChunk(cx + dx, cz + dz);
                if (c == null || c.status != Chunk.ChunkStatus.generated) continue;
                if (!w.neighborsReady(c.cx, c.cz)) continue;
                lightQueue[w.dim].Add(chunkKey(c.cx, c.cz));
            }
        }
    }

    /// finish lighting a chunk: cheap seam exchange (local light came from the gen queue)
    private void lightChunk(World w, Chunk c)
    {
        w.light.stitchChunk(c);
        var dirty = dirtySections[w.dim];
        for (int s = 0; s < c.sections; s++) dirty.Add(new SectionPos(c.cx, s, c.cz));
        foreach (var (nx, nz) in new[] { (c.cx - 1, c.cz), (c.cx + 1, c.cz), (c.cx, c.cz - 1), (c.cx, c.cz + 1) })
        {
            var n = w.getChunk(nx, nz);
            if (n != null && n.status == Chunk.ChunkStatus.lit)
            {
                for (int s = 0; s < n.sections; s++) dirty.Add(new SectionPos(nx, s, nz));
            }
        }
        dirtySections[w.dim] = dirty;
        activateFluids(w, c);
    }

    /// schedule flow ticks for fluid cells that should be moving — the fluid sim
    /// is purely event-driven, so worldgen springs/lakes (and saves made mid-flow)
    /// otherwise sit frozen forever until some neighbor changes
    private void activateFluids(World w, Chunk c)
    {
        ushort waterId = B.water, lavaId = B.lava;
        int minY = c.minY;
        // pure scan first; world lookups outside the unsafe buffer access
        var fluidCells = new List<(int, int, int, ushort)>();
        var bp = c.blocks;
        for (int i = 0; i < bp.Length; i++)
        {
            ushort cellv = bp[i];
            int id = cellv >> 4;
            if (id == waterId || id == lavaId)
            {
                fluidCells.Add((i & 15, (i >> 8) + minY, (i >> 4) & 15, cellv));
            }
        }
        // ocean/lake chunks hold thousands of stable sources — waking them all
        // floods the tick queue and the drop-seek BFS melts the frame rate.
        // Big water bodies are self-stable; only wake modest fluid populations.
        bool wakeSources = fluidCells.Count <= 400;
        int woken = 0;
        int baseX = c.cx * 16, baseZ = c.cz * 16;
        foreach (var (lx, wy, lz, cellv) in fluidCells)
        {
            if (woken >= 128) break;
            int wx = baseX + lx, wz = baseZ + lz;
            int id = cellv >> 4;
            if ((cellv & 15) != 0)
            {
                w.scheduleTick(wx, wy, wz, id, id == (int)waterId ? 5 : 30);
                woken += 1;
                continue;
            }
            if (!wakeSources) continue;
            // source: only wake it if it can actually act
            int below = w.getBlock(wx, wy - 1, wz) >> 4;
            if (below == 0 || (below != id && REPLACEABLE[below] == 1))
            {
                w.scheduleTick(wx, wy, wz, id, id == (int)waterId ? 5 : 30);
                woken += 1;
                continue;
            }
            for (int d = 0; d < 4; d++)
            {
                int n = w.getBlock(wx + new[] { 0, 0, -1, 1 }[d], wy, wz + new[] { -1, 1, 0, 0 }[d]) >> 4;
                if (n == 0)
                {
                    w.scheduleTick(wx, wy, wz, id, id == (int)waterId ? 5 : 30);
                    woken += 1;
                    break;
                }
            }
        }
    }

    /// Budgeted initial lighting: nearest chunks first, stop when the frame's budget is spent
    private void processLightQueue()
    {
        if (!inWorld) return;
        var w = world;
        var q = lightQueue[dim];
        if (q.Count != 0)
        {
            int pcx = floorDiv(ifloor(player.x), 16);
            int pcz = floorDiv(ifloor(player.z), 16);
            var ready = new List<(long key, Chunk c, int d)>();
            foreach (var key in q.ToList())
            {
                if (!w.chunks.TryGetValue(key, out var c) || c.status != Chunk.ChunkStatus.generated)
                {
                    q.Remove(key); // gone or already lit — done with it
                    continue;
                }
                if (!w.neighborsReady(c.cx, c.cz)) continue; // keep queued, never drop
                ready.Add((key, c, (c.cx - pcx) * (c.cx - pcx) + (c.cz - pcz) * (c.cz - pcz)));
            }
            ready.Sort((a, b) => a.d.CompareTo(b.d));
            double t0 = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            foreach (var r in ready)
            {
                q.Remove(r.key);
                lightChunk(w, r.c);
                if (((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - t0) * 1000 > LIGHT_BUDGET_MS) break;
            }
        }
        // self-heal: any chunk that slipped through the queue gets re-queued
        // (this runs per frame — gate to once per qualifying tick)
        if (w.time % 20 == 0 && lastLightHealTick != w.time)
        {
            lastLightHealTick = w.time;
            foreach (var c in w.chunks.Values)
            {
                if (c.status == Chunk.ChunkStatus.generated && w.neighborsReady(c.cx, c.cz))
                {
                    q.Add(chunkKey(c.cx, c.cz));
                }
            }
            // retry sections parked on an unfinished neighborhood
            if (stalledSections.TryGetValue(dim, out var stalled) && stalled.Count != 0)
            {
                dirtySections[dim].UnionWith(stalled);
                stalledSections[dim] = new HashSet<SectionPos>();
            }
        }
        lightQueue[dim] = q;
    }

    /// Guarantee an area exists before placing the player in it (synchronous)
    private void ensureChunksLoaded(World w, int ccx, int ccz, int radius)
    {
        string worldId = worldRec?.id;
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int cx = ccx + dx, cz = ccz + dz;
                if (w.chunks.ContainsKey(chunkKey(cx, cz))) continue;
                ChunkRecord? saved = null;
                if (worldId != null && savedChunkKeys.Contains(db.chunkKey(worldId, (int)w.dim, cx, cz)))
                {
                    saved = db.getChunk(worldId, (int)w.dim, cx, cz);
                    if (saved != null && recordUsable(saved.Value, w.info.height))
                    {
                        var lt = computeLocalLight(saved.Value.blocks, w.info.height, w.info.hasSky);
                        adoptChunk(w, makeChunk(cx, cz, w.info.minY, w.info.height, saved.Value.blocks, saved.Value.biomes, lt.sky, lt.blk),
                                   null, null, saved);
                        continue;
                    }
                }
                var outg = generateChunk(w.dim, w.seed, cx, cz);
                var light = computeLocalLight(outg.blocks, w.info.height, w.info.hasSky);
                adoptChunk(w, makeChunk(cx, cz, w.info.minY, w.info.height, outg.blocks, outg.biomes, light.sky, light.blk),
                           outg.blockEntities, saved != null ? null : outg.entities, saved);
            }
        }
        // teleport targets need light immediately — stitch the area now
        for (int dz = -radius - 1; dz <= radius + 1; dz++)
        {
            for (int dx = -radius - 1; dx <= radius + 1; dx++)
            {
                var c = w.getChunk(ccx + dx, ccz + dz);
                if (c != null && c.status == Chunk.ChunkStatus.generated && w.neighborsReady(c.cx, c.cz))
                {
                    lightChunk(w, c);
                }
            }
        }
    }

    private void streamChunks()
    {
        var w = world;
        int pcx = floorDiv(ifloor(player.x), 16);
        int pcz = floorDiv(ifloor(player.z), 16);
        w.simCenterX = pcx;
        w.simCenterZ = pcz;
        int R = settings.renderDistance + GEN_RADIUS_PAD;
        // request missing chunks ring by ring (closest first)
        bool broke = false;
        for (int r = 0; r <= R && !broke; r++)
        {
            for (int dz = -r; dz <= r && !broke; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    if (genInFlight.Count >= MAX_GEN_INFLIGHT) { broke = true; break; }
                    int cx = pcx + dx, cz = pcz + dz;
                    if (!w.chunks.ContainsKey(chunkKey(cx, cz))) requestChunk(w, cx, cz);
                }
            }
        }
        // unload far chunks (tight radius — chunk arrays are ~400KB each)
        int dropR = R + 2;
        foreach (var c in w.chunks.Values.ToArray())
        {
            if (Math.Abs(c.cx - pcx) > dropR || Math.Abs(c.cz - pcz) > dropR)
            {
                unloadChunk(w, c);
            }
        }
        // inactive dimensions don't stream — drop everything they hold
        if (w.time % 100 == 0)
        {
            foreach (var kv in worlds)
            {
                var d = kv.Key; var other = kv.Value;
                if (d == dim) continue;
                foreach (var c in other.chunks.Values.ToArray()) unloadChunk(other, c);
            }
        }
    }

    private void unloadChunk(World w, Chunk c)
    {
        // persist if edited, if live entities stand in it, or if a stale record exists
        bool hasEntities = false;
        foreach (var e in w.entities)
        {
            var ent = e as Entity;
            if (ent == null || ent.isPlayer || ent.dead) continue;
            if (floorDiv(ifloor(ent.x), 16) == c.cx && floorDiv(ifloor(ent.z), 16) == c.cz)
            {
                hasEntities = true;
                break;
            }
        }
        if (worldRec != null)
        {
            var rec = worldRec.Value;
            string dbKey = db.chunkKey(rec.id, (int)w.dim, c.cx, c.cz);
            if (c.modified || hasEntities || savedChunkKeys.Contains(dbKey))
            {
                var record = chunkRecord(rec.id, w.dim, w, c);
                savedChunkKeys.Add(record.key);
                pendingChunkSaves[record.key] = record;
            }
        }
        // entities standing in the chunk were captured in the record; drop the live ones
        foreach (var e in w.entities.ToArray())
        {
            var ent = e as Entity;
            if (ent == null || ent.isPlayer || ent.dead) continue;
            if (floorDiv(ifloor(ent.x), 16) == c.cx && floorDiv(ifloor(ent.z), 16) == c.cz)
            {
                w.removeEntity(e);
            }
        }
        w.releaseChunkBlockEntities(c);
        w.removeChunk(c.cx, c.cz);
        if (w.dim == dim) host?.removeChunkMeshes(c.cx, c.cz, c.sections);
        lightQueue[w.dim].Remove(chunkKey(c.cx, c.cz));
        for (int s = 0; s < c.sections; s++)
        {
            dirtySections[w.dim].Remove(new SectionPos(c.cx, s, c.cz));
        }
    }

    // ---- meshing --------------------------------------------------------------
    private void streamMeshes()
    {
        if (!inWorld) return;
        var w = world;
        var dirty = dirtySections[dim];
        if (dirty.Count == 0) return;
        // skip the scan entirely while the queue is already full
        if (meshJobs.Count >= MAX_MESH_INFLIGHT) return;
        int pcx = floorDiv(ifloor(player.x), 16);
        int pcz = floorDiv(ifloor(player.z), 16);
        // sort a bounded batch by distance
        var candidates = new List<(SectionPos pos, int d)>();
        foreach (var pos in dirty.ToList())
        {
            var c = w.getChunk(pos.cx, pos.cz);
            if (c == null || c.status != Chunk.ChunkStatus.lit || !w.neighborsReady(pos.cx, pos.cz)) continue;
            // neighbors must be lit too or the seam light is garbage
            bool ok = true;
            foreach (var (nx, nz) in new[] { (pos.cx - 1, pos.cz), (pos.cx + 1, pos.cz), (pos.cx, pos.cz - 1), (pos.cx, pos.cz + 1) })
            {
                var n = w.getChunk(nx, nz);
                if (n == null || n.status != Chunk.ChunkStatus.lit) { ok = false; break; }
            }
            // diagonals only need to exist — the snapshot reads their cells
            if (ok && (w.getChunk(pos.cx - 1, pos.cz - 1) == null || w.getChunk(pos.cx + 1, pos.cz - 1) == null ||
                w.getChunk(pos.cx - 1, pos.cz + 1) == null || w.getChunk(pos.cx + 1, pos.cz + 1) == null)) ok = false;
            if (!ok)
            {
                // park it: retried when the surrounding area completes
                if (!stalledSections.TryGetValue(dim, out var sset)) { sset = new HashSet<SectionPos>(); stalledSections[dim] = sset; }
                sset.Add(pos);
                dirty.Remove(pos);
                continue;
            }
            candidates.Add((pos, (pos.cx - pcx) * (pos.cx - pcx) + (pos.cz - pcz) * (pos.cz - pcz)));
        }
        candidates.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (var cand in candidates)
        {
            if (meshJobs.Count >= MAX_MESH_INFLIGHT) break;
            var jobKey = new DimSection((int)dim, cand.pos);
            if (meshJobs.TryGetValue(jobKey, out var existing))
            {
                existing.dirtyAgain = true;
                dirty.Remove(cand.pos);
                continue;
            }
            dirty.Remove(cand.pos);
            dirtySections[dim] = dirty;
            dispatchMesh(w, cand.pos, jobKey);
            dirty = dirtySections[dim];
        }
        dirtySections[dim] = dirty;
    }

    /// flip mesh mode at runtime and rebuild every visible section
    public void setMeshMode(bool simple)
    {
        settings.simpleMesh = simple;
        saveSettings(settings);
        remeshAll();
    }

    private void remeshAll()
    {
        host?.clearAllSections();
        meshJobs.Clear();
        if (!inWorld) return;
        var w = world;
        foreach (var c in w.chunks.Values)
        {
            if (c.status != Chunk.ChunkStatus.lit) continue;
            for (int s = 0; s < c.sections; s++)
            {
                dirtySections[dim].Add(new SectionPos(c.cx, s, c.cz));
            }
        }
    }

    private void dispatchMesh(World w, SectionPos pos, DimSection jobKey)
    {
        var snapOpt = LoadProf.shared.time("snapshot", () => buildSnapshot(w, pos.cx, pos.sy, pos.cz));
        if (snapOpt == null)
        {
            // a diagonal neighbor is missing (streaming frontier / unload churn) —
            // the dirty key was already consumed, so REQUEUE or this section's
            // remesh is lost forever (stale black mesh / permanent hole)
            dirtySections[w.dim].Add(pos);
            return;
        }
        var snap = snapOpt.Value;
        snap.noMerge = settings.simpleMesh;
        var state = new MeshJobState();
        meshJobs[jobKey] = state;
        Dim d = w.dim;
        int minY = w.info.minY;
        meshQueue.async(() =>
        {
            var mesh = LoadProf.shared.time("mesh", () => buildSectionMesh(snap));
            DispatchQueue.main.async(() =>
            {
                meshJobs.Remove(jobKey);
                if (!inWorld || !ReferenceEquals(worldsOrNull(d), w)) return;
                if (d == dim)
                {
                    host?.uploadMesh(pos.cx, pos.sy, pos.cz, minY, mesh);
                    meshedThisSecond += 1;
                }
                if (state.dirtyAgain) dirtySections[d].Add(pos);
            });
        });
    }

    /// reset the once-per-second chunk-update counter (the app's fps timer calls this)
    public int harvestMeshCounter()
    {
        int n = meshedThisSecond;
        meshedThisSecond = 0;
        lastChunkUpdates = n;
        return n;
    }

    /// padded 18×18×18 snapshot for the mesher
    private MeshInput? buildSnapshot(World w, int cx, int sy, int cz)
    {
        const int P = 18;
        var blocks = new ushort[P * P * P];
        var skyLight = new byte[P * P * P];
        var blockLight = new byte[P * P * P];
        var biomes = new byte[P * P];
        int minY = w.info.minY;
        int baseY = minY + sy * 16;
        int baseX = cx * 16, baseZ = cz * 16;
        for (int dz = -1; dz <= 16; dz++)
        {
            for (int dx = -1; dx <= 16; dx++)
            {
                int wx = baseX + dx, wz = baseZ + dz;
                var c = w.getChunkAt(wx, wz);
                if (c == null) return null;
                int lx = posMod(wx, 16), lz = posMod(wz, 16);
                int col = (dz + 1) * P + (dx + 1);
                biomes[col] = (byte)c.biomeAt(lx, Math.Min(minY + w.info.height - 1, Math.Max(minY, baseY + 8)), lz);
                for (int dy = -1; dy <= 16; dy++)
                {
                    int wy = baseY + dy;
                    int idx = ((dy + 1) * P + (dz + 1)) * P + (dx + 1);
                    if (wy < minY || wy >= minY + w.info.height)
                    {
                        skyLight[idx] = (byte)(wy >= minY + w.info.height ? 15 : 0);
                    }
                    else
                    {
                        blocks[idx] = c.get(lx, wy, lz);
                        skyLight[idx] = (byte)c.getSky(lx, wy, lz);
                        blockLight[idx] = (byte)c.getBlockLight(lx, wy, lz);
                    }
                }
            }
        }
        return new MeshInput(blocks: blocks, skyLight: skyLight, blockLight: blockLight, biomes: biomes);
    }

    /// drop every GPU mesh and re-mesh all lit chunks in the current dimension —
    /// used when the atlas/tint semantics change under us (resource pack swap)
    public void remeshAllLoaded()
    {
        if (!hasWorld()) return;
        host?.clearAllSections();
        meshJobs.Clear();
        stalledSections.Clear();
        foreach (var c in world.chunks.Values)
        {
            if (c.status != Chunk.ChunkStatus.lit) continue;
            for (int s = 0; s < c.sections; s++)
            {
                dirtySections[dim].Add(new SectionPos(c.cx, s, c.cz));
            }
        }
    }

    // ===========================================================================
    // Dimension travel
    // ===========================================================================
    private void moveToDimension(Dim dest)
    {
        var from = world;
        from.removeEntity(player);
        dim = dest;
        var w = world;
        player.world = w;
        w.addEntity(player);
        host?.clearAllSections();
        meshJobs.Clear();
        // re-mesh everything already loaded in the destination
        foreach (var c in w.chunks.Values)
        {
            if (c.status != Chunk.ChunkStatus.lit) continue;
            for (int s = 0; s < c.sections; s++)
            {
                dirtySections[dest].Add(new SectionPos(c.cx, s, c.cz));
            }
        }
    }

    private void travelNetherPortal()
    {
        if (traveling) return;
        traveling = true;
        try
        {
            var p = player;
            Dim fromDim = dim;
            Dim destDim = fromDim == Dim.nether ? Dim.overworld : Dim.nether;
            double scale = DIMS[(int)fromDim].coordScale / DIMS[(int)destDim].coordScale;
            double tx = p.x * scale, tz = p.z * scale;
            var destW = worlds[destDim];
            ensureChunksLoaded(destW, floorDiv(ifloor(tx), 16), floorDiv(ifloor(tz), 16), 1);
            var (px, py, pz) = findOrCreatePortal(
                destW, tx,
                clampD(p.y, (double)(destW.info.minY + 4), (double)(destW.info.minY + destW.info.height - 8)), tz);
            moveToDimension(destDim);
            p.setPos(px, py, pz);
            p.vx = 0; p.vy = 0; p.vz = 0;
            p.portalCooldown = 200;
            p.portalTicks = 0;
            p.insidePortalKind = null;
            host?.playSound("block.portal.travel", px, py, pz, 1, 1);
            advance("nether_root");
        }
        finally { traveling = false; }
    }

    private void travelEndPortal()
    {
        if (traveling) return;
        traveling = true;
        try
        {
            var p = player;
            if (dim != Dim.end)
            {
                var end = worlds[Dim.end];
                ensureChunksLoaded(end, floorDiv(100, 16), 0, 1);
                var (px, py, pz) = buildEndSpawnPlatform(end);
                moveToDimension(Dim.end);
                p.setPos(px, py, pz);
                p.vx = 0; p.vy = 0; p.vz = 0;
                p.portalCooldown = 200;
                p.insidePortalKind = null;
                advance("enter_end");
                // first visit: the fight begins
                if (!(worldRec?.dragonKilled ?? false) && !dragonSpawned)
                {
                    ensureChunksLoaded(end, 0, 0, 2);
                    var dragon = spawnMob(end, "ender_dragon", 0.5, 90, 0.5, null) as EnderDragon;
                    if (dragon != null) armDragon(dragon);
                    dragonSpawned = true;
                }
            }
            else
            {
                // leaving the End: drop at world spawn (credits-free return)
                var ow = worlds[Dim.overworld];
                ensureChunksLoaded(ow, floorDiv((int)ow.spawnX, 16), floorDiv((int)ow.spawnZ, 16), 1);
                moveToDimension(Dim.overworld);
                p.setPos(ow.spawnX + 0.5, (double)ow.surfaceY((int)ow.spawnX, (int)ow.spawnZ), ow.spawnZ + 0.5);
                p.vx = 0; p.vy = 0; p.vz = 0;
                p.portalCooldown = 200;
                p.insidePortalKind = null;
            }
        }
        finally { traveling = false; }
    }

    private void armDragon(EnderDragon dragon)
    {
        dragonSpawned = true;
        dragon.onDeath = (_) =>
        {
            if (!worlds.TryGetValue(Dim.end, out var end)) return;
            activateEndPortal(end);
            int n = worldRec?.gatewaysSpawned ?? 0;
            if (worldRec != null) { var r = worldRec.Value; r.gatewaysSpawned = n + 1; worldRec = r; }
            spawnEndGateway(end, n);
            if (worldRec != null) { var r = worldRec.Value; r.dragonKilled = true; worldRec = r; }
            advance("kill_dragon");
            host?.pushChat("§dThe Ender Dragon has been defeated!");
        };
    }

    private void tickPortals()
    {
        var p = player;
        if (p.portalCooldown > 0)
        {
            p.insidePortalKind = null;
            return;
        }
        var bb = p.bb();
        bool inNether = false, inEnd = false;
        BlockEntityData inGateway = null;
        int x0 = ifloor(bb.x0), x1 = ifloor(bb.x1);
        int y0 = ifloor(bb.y0), y1 = ifloor(bb.y1);
        int z0 = ifloor(bb.z0), z1 = ifloor(bb.z1);
        var w = world;
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int id = w.getBlockId(x, y, z);
                    if (id == (int)B.nether_portal) inNether = true;
                    else if (id == (int)B.end_portal) inEnd = true;
                    else if (id == (int)B.end_gateway)
                    {
                        var be = w.getBlockEntity(x, y, z);
                        if (be != null && be.type == "end_gateway") inGateway = be;
                    }
                }
            }
        }
        if (inGateway != null)
        {
            var be = inGateway;
            if (traveling) return;
            traveling = true;
            try
            {
                p.portalCooldown = 200;
                int exitX = be.exitX ?? 0, exitZ = be.exitZ ?? 0;
                ensureChunksLoaded(w, floorDiv(exitX, 16), floorDiv(exitZ, 16), 1);
                int sy = w.surfaceY(exitX, exitZ);
                p.setPos((double)exitX + 0.5,
                         (be.exactTeleport ?? false) ? (double)(be.exitY ?? 0) : (double)Math.Max(sy, 50),
                         (double)exitZ + 0.5);
                p.vx = 0; p.vy = 0; p.vz = 0;
                host?.playSound("block.portal.travel", p.x, p.y, p.z, 1, 1);
                advance("enter_gateway");
            }
            finally { traveling = false; }
            return;
        }
        if (inEnd)
        {
            travelEndPortal();
            return;
        }
        if (inNether)
        {
            p.insidePortalKind = "nether";
            p.portalTicks += 1;
            int wait = p.gameMode == GameMode.creative ? 1 : 80;
            if (p.portalTicks >= wait) travelNetherPortal();
        }
        else
        {
            p.insidePortalKind = null;
        }
    }

    // ===========================================================================
    // Tick
    // ===========================================================================
    private void tick()
    {
        if (!inWorld) return;
        var w = world;
        var p = player;
        paused = host?.screenPausesGame() ?? false;
        if (paused) return;

        streamChunks();

        // ---- player intent ----
        bool playerDead = p.dead || p.deathTime > 0;
        bool blocked = (host?.hasScreen() ?? false) || playerDead || p.sleepTicks > 0;
        if (!blocked)
        {
            bool k(string b) => keys.Contains(keybinds.TryGetValue(b, out var v) ? v : "");
            p.moveForward = (k("forward") ? 1 : 0) + (k("back") ? -1 : 0);
            // applyInput's strafe basis points +strafe to the LEFT of the view
            // direction (golden baselines shipped the same quirk) — flip here so D=right
            p.moveStrafe = (k("right") ? -1 : 0) + (k("left") ? 1 : 0);
            // vanilla: eating / drawing a bow slows movement to 20%
            if (p.usingItem)
            {
                p.moveForward *= 0.2;
                p.moveStrafe *= 0.2;
            }
            p.jumping = k("jump");
            p.sneaking = k("sneak") && !p.flying;
            bool wantSprint = (k("sprint") || sprintHeld) && p.moveForward > 0 && p.hunger > 6 && !p.sneaking;
            if (wantSprint && !p.sprinting && p.moveForward > 0) p.sprinting = true;
            if (!wantSprint || p.moveForward <= 0 || p.horizontalCollision) p.sprinting = false;
            sprintHeld = p.sprinting;
            // creative flight vertical
            if (p.flying)
            {
                p.vy = (k("jump") ? 0.35 : 0) + (k("sneak") ? -0.35 : 0);
            }
            // elytra start: jump while airborne
            if (k("jump") && !p.onGround && !p.elytraFlying && p.vy < 0 && !p.flying)
            {
                if (p.startElytra())
                {
                    host?.playSound("item.armor.equip_elytra", p.x, p.y, p.z, 1, 1);
                }
            }
        }
        else
        {
            p.moveForward = 0;
            p.moveStrafe = 0;
            p.jumping = false;
            p.sprinting = false;
        }

        // ---- player physics & state ----
        if (!double.IsFinite(p.x) || !double.IsFinite(p.y) || !double.IsFinite(p.z))
        {
            // physics blowup — recover instead of rendering nothing forever
            p.setPos(w.spawnX + 0.5, Math.Max(double.IsFinite(p.prevY) ? p.prevY : w.spawnY, w.spawnY), w.spawnZ + 0.5);
            p.vx = 0; p.vy = 0; p.vz = 0;
        }
        bool feetReady = w.isChunkReady(floorDiv(ifloor(p.x), 16), floorDiv(ifloor(p.z), 16));
        if (!playerDead && (!feetReady || traveling))
        {
            // the world doesn't exist under the player yet — hold them in place
            p.prevX = p.x; p.prevY = p.y; p.prevZ = p.z;
            p.vx = 0; p.vy = 0; p.vz = 0;
            p.fallDistance = 0;
            heldForChunks = true;
        }
        else if (!playerDead)
        {
            if (heldForChunks)
            {
                heldForChunks = false;
                // if the hold began mid-fall through unloaded terrain, the player may
                // now be inside solid blocks or under the world — surface them
                int bx = ifloor(p.x), bz = ifloor(p.z);
                bool inSolid = blockDefs[w.getBlockId(bx, ifloor(p.y), bz)].solid ||
                    blockDefs[w.getBlockId(bx, ifloor(p.y + 1), bz)].solid;
                if (p.y < (double)(w.info.minY + 1) || inSolid)
                {
                    p.setPos(p.x, (double)w.surfaceY(bx, bz), p.z);
                }
            }
            p.tick();
            if (p.vehicle != null)
            {
                var v = p.vehicle;
                p.setPos(v.x, v.y + v.height * 0.6, v.z);
                p.fallDistance = 0;
                p.vx = 0; p.vy = 0; p.vz = 0;
            }
            else if (p.flying)
            {
                // creative flight: friction-only horizontal w/ input
                double speed = p.sprinting ? 0.05 : 0.025;
                double sin = detSin(p.yaw), cos = detCos(p.yaw);
                p.vx += (p.moveStrafe * cos - p.moveForward * sin) * speed * 2.5;
                p.vz += (p.moveForward * cos + p.moveStrafe * sin) * speed * 2.5;
                p.move(p.vx, p.vy, p.vz);
                p.vx *= 0.85; p.vy *= 0.6; p.vz *= 0.85;
                p.fallDistance = 0;
                if (p.onGround) p.flying = false;
            }
            else if (p.elytraFlying)
            {
                p.move(p.vx, p.vy, p.vz);
            }
            else if (p.sleepTicks <= 0)
            {
                p.travel();
            }
            if (p.sprinting) p.addExhaustion(0.0);
        }
        else
        {
            p.tickDeath();
            if (!deathScreenShown)
            {
                deathScreenShown = true;
                host?.closeAllScreens();
                host?.openDeathScreen(deathCauseText(p.data.deathCause, p.data.deathAttacker));
                host?.releasePointer();
            }
        }

        // ---- world & entities ----
        w.tick();
        double simR = (double)(w.simDistance * 16) * (double)(w.simDistance * 16);
        foreach (var e in w.entities.ToArray())
        {
            if (ReferenceEquals(e, p) || e.dead) continue;
            var ent = e as Entity;
            if (ent == null) continue;
            double dx = ent.x - p.x, dz = ent.z - p.z;
            if (dx * dx + dz * dz > simR && !ALWAYS_TICK.Contains(ent.type)) continue;
            ent.tick();
            // sculk catalyst blooms on death
            if (ent is LivingEntity liv && liv.deathTime == 1)
            {
                tryCatalystBloom(w, ent.x, ent.y, ent.z, liv.xpReward);
            }
        }
        foreach (var e in w.entities.ToArray())
        {
            if (e.dead) w.removeEntity(e);
        }

        // ---- per-tick systems ----
        tickEntityTriggers(w);
        tickFangs(w);
        // (updateDaylightDetectors is a no-op — detectors self-schedule ticks)
        naturalSpawnTick(w, new List<Player> { p }, ref w.rng);
        raidManager.tick(w);
        if (w.time % 1200 == 0 && dim == Dim.overworld) tryPatrolSpawn(w, new List<Player> { p }, ref w.rng);
        raidManager.tryStartRaid(w, p);
        tickWeatherEffects();
        tickPortals();
        tickUsing();
        tickMining();
        tickViewBob();
        tickAmbience();
        tickAdvancementScan();
        tickBossBars();

        // toasts
        while (advancements.pendingToasts.Count != 0)
        {
            var t = advancements.pendingToasts[0];
            advancements.pendingToasts.RemoveAt(0);
            host?.pushToast(t);
        }

        // sleeping skips to morning
        if (p.sleepTicks > 100)
        {
            p.sleepTicks = 0;
            w.dayTime = 0;
            if (w.raining && w.rng.chance(0.6))
            {
                w.raining = false;
                w.thundering = false;
                w.weatherTimer = 12000;
            }
            advance("sleep_in_bed");
        }

        // hotbar name flash
        if (p.selectedSlot != lastSlot)
        {
            lastSlot = p.selectedSlot;
            heldNameTime = 60;
        }
        else if (heldNameTime > 0)
        {
            heldNameTime -= 1;
        }
        if (useCooldown > 0) useCooldown -= 1;
        if (breakCooldown > 0) breakCooldown -= 1;

        // portal overlay warp factor
        double targetWarp = p.insidePortalKind == "nether" ? Math.Min(1, (double)p.portalTicks / 60) : 0;
        portalWarp += (targetWarp - portalWarp) * 0.1;

        // batched unload writes — one transaction per second at most
        if (pendingChunkSaves.Count != 0 && w.time % 20 == 0)
        {
            var batch = pendingChunkSaves.Values.ToList();
            pendingChunkSaves.Clear();
            saveQueue.async(() => writeChunkBatch(batch));
        }

        // autosave
        ticksSinceSave += 1;
        if (ticksSinceSave >= SAVE_INTERVAL_TICKS)
        {
            ticksSinceSave = 0;
            saveAndFlush();
        }
    }

    private void tryCatalystBloom(World w, double x, double y, double z, int xp)
    {
        int bx = ifloor(x), by = ifloor(y), bz = ifloor(z);
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dz = -4; dz <= 4; dz++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    if (w.getBlockId(bx + dx, by + dy, bz + dz) == (int)B.sculk_catalyst)
                    {
                        sculkBloom(w, bx + dx, by + dy, bz + dz, xp);
                        advance("avoid_warden");
                        return;
                    }
                }
            }
        }
    }

    private void tickWeatherEffects()
    {
        var w = world;
        if (dim != Dim.overworld) return;
        var p = player;
        // lightning strikes
        if (w.thundering && w.rng.chance(0.00004 * w.thunderLevel * 16))
        {
            int x = ifloor(p.x + (w.rng.nextFloat() - 0.5) * 160);
            int z = ifloor(p.z + (w.rng.nextFloat() - 0.5) * 160);
            if (w.isLoadedAt(x, z))
            {
                int y = w.surfaceY(x, z);
                if (w.canSeeSky(x, y, z))
                {
                    spawnLightningFn?.Invoke(w, (double)x + 0.5, (double)y, (double)z + 0.5);
                }
            }
        }
        // snow/ice accumulation + fire spread damp: sample random loaded columns
        if (w.rainLevel > 0.5)
        {
            for (int _i = 0; _i < 4; _i++)
            {
                int x = ifloor(p.x + (w.rng.nextFloat() - 0.5) * 128);
                int z = ifloor(p.z + (w.rng.nextFloat() - 0.5) * 128);
                if (w.isLoadedAt(x, z)) weatherRandomTick(w, x, z);
            }
        }
        // precipitation particles near the camera (cosmetic randomness stays native)
        if (w.rainLevel > 0.2 && settings.particles > 0)
        {
            int n = (int)Math.Round(6 * w.rainLevel, MidpointRounding.AwayFromZero);
            for (int _i = 0; _i < n; _i++)
            {
                double x = p.x + (nativeRng.NextDouble() - 0.5) * 18;
                double z = p.z + (nativeRng.NextDouble() - 0.5) * 18;
                int bx = ifloor(x), bz = ifloor(z);
                if (!w.isLoadedAt(bx, bz)) continue;
                double top = (double)(w.heightAt(bx, bz) + 1);
                if (top > p.y + 14 || top < p.y - 20) continue;
                int biome = w.biomeAt(bx, ifloor(p.y), bz);
                if (snowsAt(biome, (int)top))
                {
                    host?.spawnPrecipitation("snow", x, p.y + 8 + nativeRng.NextDouble() * 4, z, 0);
                }
                else if (((biome >= 0 && biome < BIOMES.Count && BIOMES[biome] != null) ? BIOMES[biome].downfall : 1) > 0.05)
                {
                    host?.spawnPrecipitation("rain", x, top + 4 + nativeRng.NextDouble() * 8, z, top);
                }
            }
        }
    }

    /// per-tick walk-bob state, vanilla-style smoothed amplitude
    private void tickViewBob()
    {
        var p = player;
        prevBobPhase = bobPhase;
        prevBobAmp = bobAmp;
        double speed = Math.Min(0.4, detHyp(p.x - p.prevX, p.z - p.prevZ));
        bobAmp += (speed - bobAmp) * 0.4;
        if (p.onGround && p.vehicle == null)
        {
            bobPhase += bobAmp * 1.4;
        }
        // smoothed FOV kick (vanilla eases toward the speed-scaled FOV)
        prevFovScale = fovScale;
        double target = p.elytraFlying ? 1.12 : (p.sprinting ? 1.15 : (p.usingItem && itemUseSlows() ? 0.9 : 1.0));
        fovScale += (target - fovScale) * 0.5;
    }

    /// bows/spyglass zoom-slow while charging
    private bool itemUseSlows()
    {
        var held = player?.mainHand;
        if (held == null) return false;
        string n = itemDef(held.id).name;
        return n == "bow" || n == "spyglass" || n == "crossbow" || n == "trident";
    }

    // ---- held item use / eating / brushing ----
    private void tickUsing()
    {
        var p = player;
        if (p.dead || p.deathTime > 0 || (host?.hasScreen() ?? false))
        {
            if (p.usingItem) p.usingItem = false;
            return;
        }
        var ctx = interactCtx();
        if (p.usingItem)
        {
            if (!rightDown)
            {
                releaseUsingItem(ctx);
            }
            else
            {
                p.useItemTicks += 1;
                var held = p.mainHand;
                var def = held != null ? itemDef(held.id) : null;
                if (def != null && (def.food != null || def.name == "potion" || def.name == "milk_bucket"))
                {
                    if (p.useItemTicks % 4 == 0)
                    {
                        host?.playSound("entity.generic.eat", p.x, p.y, p.z, 0.4, 0.9 + nativeRng.NextDouble() * 0.3);
                    }
                    if (p.useItemTicks >= 32) finishUsingItem(ctx);
                }
            }
        }
        else if (rightDown && useCooldown <= 0)
        {
            // held use repeats (block placement etc.) every 4 ticks
            doUse();
            useCooldown = 4;
        }
        // brushing suspicious blocks
        var held2 = p.mainHand;
        if (rightDown && held2 != null && itemDef(held2.id).name == "brush" && !p.usingItem)
        {
            var hit = crosshairBlock();
            if (hit != null && ((hit.Value.cell >> 4) == (int)B.suspicious_sand || (hit.Value.cell >> 4) == (int)B.suspicious_gravel))
            {
                var h = hit.Value;
                brushTicks += 1;
                var w = world;
                if (brushTicks % 5 == 0)
                {
                    host?.playSound("item.brush.brushing", (double)h.x, (double)h.y, (double)h.z, 0.8, 1);
                    w.hooks.addParticles("block", h.px, h.py, h.pz, 4, 0.15, h.cell);
                }
                if (brushTicks >= 10)
                {
                    brushTicks = 0;
                    var be = w.getBlockEntity(h.x, h.y, h.z);
                    if (be == null || be.type != "brushable")
                    {
                        be = makeBrushableBE(h.x, h.y, h.z, "trail_ruins_common", (int)hash3(w.seed, h.x, h.y, h.z));
                        w.setBlockEntity(be);
                    }
                    var brushable = be;
                    brushable.dusted = (brushable.dusted ?? 0) + 1;
                    if ((brushable.dusted ?? 0) >= 4)
                    {
                        if (brushable.item == null)
                        {
                            var rng = new RandomX(unchecked((uint)(brushable.lootSeed ?? 0)));
                            var loot = rollLoot(brushable.lootTable ?? "trail_ruins_common", ref rng, luck: 0);
                            brushable.item = loot.Count != 0 ? loot[0] : new ItemStack(iid("stick"), 1);
                        }
                        bool isGravel = (h.cell >> 4) == (int)B.suspicious_gravel;
                        world.setBlock(h.x, h.y, h.z, (int)cell(isGravel ? B.gravel : B.sand));
                        spawnItem(world, (double)h.x + 0.5, (double)h.y + 0.6, (double)h.z + 0.5, brushable.item);
                        if (itemDef(brushable.item.id).name.EndsWith("_pottery_sherd")) advance("brush_sherd");
                        if (itemDef(brushable.item.id).name == "sniffer_egg") advance("sniffer_egg");
                        host?.playSound("block.suspicious_sand.break", (double)h.x, (double)h.y, (double)h.z, 1, 1);
                    }
                }
            }
            else
            {
                brushTicks = 0;
            }
        }
        else if (!rightDown)
        {
            brushTicks = 0;
        }
    }

    // ---- mining ----
    private void tickMining()
    {
        var p = player;
        if (!leftDown || p.dead || p.deathTime > 0 || (host?.hasScreen() ?? false))
        {
            p.breakingProgress = -1;
            return;
        }
        var hitOpt = crosshairBlock();
        targetedBlock = hitOpt != null ? (hitOpt.Value.x, hitOpt.Value.y, hitOpt.Value.z, hitOpt.Value.cell) : ((int, int, int, int)?)null;
        if (hitOpt == null)
        {
            p.breakingProgress = -1;
            return;
        }
        var hit = hitOpt.Value;
        var w = world;
        var def = blockDefs[hit.cell >> 4];
        if (def.hardness < 0 && p.gameMode != GameMode.creative)
        {
            p.breakingProgress = -1;
            return;
        }
        if (p.gameMode == GameMode.creative)
        {
            if (breakCooldown <= 0)
            {
                finishBreaking(interactCtx(), hit.x, hit.y, hit.z);
                breakCooldown = 5;
            }
            return;
        }
        if (breakCooldown > 0)
        {
            p.breakingProgress = -1;
            return;
        }
        if (p.breakingProgress < 0 || p.breakingX != hit.x || p.breakingY != hit.y || p.breakingZ != hit.z)
        {
            p.breakingX = hit.x; p.breakingY = hit.y; p.breakingZ = hit.z;
            p.breakingProgress = 0;
        }
        p.breakingProgress += breakSpeed(p, hit.cell);
        p.attackAnim = 1;
        if (w.time % 4 == 0)
        {
            host?.playSound($"block.{def.sound}.hit", (double)hit.x + 0.5, (double)hit.y + 0.5, (double)hit.z + 0.5, 0.25, 0.6);
            w.hooks.addParticles("block", hit.px, hit.py, hit.pz, 1, 0.12, hit.cell);
        }
        if (p.breakingProgress >= 1)
        {
            finishBreaking(interactCtx(), hit.x, hit.y, hit.z);
            trackBreakAdvancements(hit.cell >> 4);
            p.breakingProgress = -1;
            breakCooldown = 3;
            p.addExhaustion(0.005);
        }
    }

    private void trackBreakAdvancements(int id)
    {
        string name = blockDefs[id].name;
        if (name.EndsWith("_log") || name.EndsWith("_stem")) advance("mine_log");
        if (id == (int)B.stone || id == (int)B.deepslate) advance("mine_stone");
        if (id == (int)B.diamond_ore || id == (int)B.deepslate_diamond_ore) advance("mine_diamond");
        if (id == (int)B.ancient_debris) advance("obtain_ancient_debris");
    }

    /// periodic inventory / situation scan for item- & place-based advancements
    private void tickAdvancementScan()
    {
        var w = world;
        var p = player;
        if (w.time % 40 != 0) return;
        bool has(string n)
        {
            var idOpt = iidOpt(n);
            if (idOpt == null) return false;
            int id = idOpt.Value;
            if (p.inventory.Any(s => s?.id == id) || p.offHand?.id == id) return true;
            return p.armor.Any(s => s?.id == id);
        }
        if (has("crafting_table")) advance("crafting_table");
        if (has("wooden_pickaxe")) advance("wooden_pickaxe");
        if (has("stone_pickaxe")) advance("stone_pickaxe");
        if (has("iron_ingot")) advance("iron_ingot");
        if (has("iron_pickaxe")) advance("iron_tools");
        if (has("iron_chestplate") || has("iron_helmet") || has("iron_leggings") || has("iron_boots")) advance("iron_armor");
        if (has("diamond")) advance("mine_diamond");
        if (has("diamond_chestplate") && has("diamond_helmet") && has("diamond_leggings") && has("diamond_boots")) advance("diamond_armor");
        if (has("blaze_rod")) advance("obtain_blaze_rod");
        if (has("ender_eye")) advance("ender_eye");
        if (has("dragon_egg")) advance("dragon_egg");
        if (has("elytra")) advance("elytra");
        if (has("ancient_debris")) advance("obtain_ancient_debris");
        if (has("netherite_helmet") && has("netherite_chestplate") && has("netherite_leggings") && has("netherite_boots")) advance("netherite_armor");
        if (has("nether_star")) advance("kill_wither");
        if (has("dragon_breath")) advance("dragon_breath");
        if (has("sniffer_egg")) advance("sniffer_egg");
        if (has("emerald")) advance("adventure_root");
        if (has("wheat_seeds") || has("wheat")) advance("husbandry_root");
        // location-based
        if (dim == Dim.nether)
        {
            int below = w.getBlockId(ifloor(p.x), ifloor(p.y) - 1, ifloor(p.z));
            if (below == (int)B.nether_bricks) advance("find_fortress");
            if (below == (int)B.polished_blackstone_bricks || below == (int)B.gilded_blackstone) advance("find_bastion");
        }
    }

    private void tickBossBars()
    {
        var w = world;
        var bars = new List<BossBarInfo>();
        foreach (var e in w.entities)
        {
            var ent = e as Entity;
            if (ent == null) continue;
            if (ent is EnderDragon d)
            {
                bars.Add(new BossBarInfo(name: "Ender Dragon", progress: d.health / d.maxHealth, color: "#e864e8"));
            }
            else if (ent is WitherBoss d2)
            {
                bars.Add(new BossBarInfo(name: "Wither", progress: d2.health / d2.maxHealth, color: "#7a7ab8"));
            }
        }
        foreach (var r in raidManager.raids)
        {
            if (ReferenceEquals(r.world, w) && r.active)
            {
                bars.Add(new BossBarInfo(
                    name: "Raid",
                    progress: r.maxHealth > 0 ? Math.Min(1, r.totalHealth / Math.Max(1, r.maxHealth)) : 1,
                    color: "#e84040"));
            }
        }
        host?.setBossBars(bars);
    }

    private void tickAmbience()
    {
        var w = world;
        var p = player;
        // audio environment
        int eyeBlock = w.getBlock(ifloor(p.x), ifloor(p.eyeY()), ifloor(p.z));
        bool underwater = isWaterlogged((ushort)eyeBlock);
        int sky = w.getSkyLight(ifloor(p.x), ifloor(p.y), ifloor(p.z));
        double caveFactor = dim == Dim.overworld
            ? clampD((double)(8 - sky) / 8, 0, 1) * (p.y < 50 ? 1 : 0.4)
            : 0.3;
        host?.setAudioEnvironment(underwater, caveFactor);
        host?.setAudioListener(p.x, p.eyeY(), p.z, p.yaw);
        // music mood
        string mood = "overworld";
        if (dim == Dim.nether) mood = "nether";
        else if (dim == Dim.end) mood = "end";
        else
        {
            int biome = w.biomeAt(ifloor(p.x), ifloor(p.y), ifloor(p.z));
            if (biome == (int)Biome.deepDark) mood = "dark";
            else if (biome == (int)Biome.lushCaves) mood = "lush";
            else if (underwater) mood = "water";
        }
        musicMood = mood;
        host?.tickMusic(mood, (settings.volumes.TryGetValue("music", out var mv) ? mv : 0) > 0.01);
    }

    // ===========================================================================
    // Interaction wiring
    // ===========================================================================
    private InteractCtx interactCtx()
    {
        return new InteractCtx(
            world: world,
            player: player,
            openScreen: (kind, data) => openScreen(kind, data),
            advance: (id) => advance(id));
    }

    public void openScreen(string kind, ScreenData? data)
    {
        if (kind == "toast")
        {
            host?.showActionBar(data?.text ?? "", 60);
            return;
        }
        host?.openScreen(kind, data);
    }

    /// raycast the crosshair against blocks
    public RaycastHit? crosshairBlock()
    {
        var p = player;
        double reach = p.gameMode == GameMode.creative ? REACH_CREATIVE : REACH_SURVIVAL;
        double dx = -detSin(p.yaw) * detCos(p.pitch);
        double dy = -detSin(p.pitch);
        double dz = detCos(p.yaw) * detCos(p.pitch);
        return world.raycast(p.x, p.eyeY(), p.z, dx, dy, dz, reach);
    }

    /// nearest entity under the crosshair within attack reach
    private Entity crosshairEntity(double maxDist)
    {
        var p = player;
        double dx = -detSin(p.yaw) * detCos(p.pitch);
        double dy = -detSin(p.pitch);
        double dz = detCos(p.yaw) * detCos(p.pitch);
        double ox = p.x, oy = p.eyeY(), oz = p.z;
        Entity best = null;
        double bestT = maxDist;
        var blockHit = world.raycast(ox, oy, oz, dx, dy, dz, maxDist);
        double blockT = blockHit?.t ?? maxDist;
        foreach (var e in world.getEntitiesNear(ox, oy, oz, maxDist + 2))
        {
            if (ReferenceEquals(e, p) || e.dead) continue;
            var ent = e as Entity;
            if (ent == null) continue;
            if (!(ent is LivingEntity) && !new[] { "boat", "minecart", "item_frame", "end_crystal" }.Contains(ent.type)) continue;
            var bb = ent.bb();
            var t = rayBoxT(ox, oy, oz, dx, dy, dz,
                            bb.x0 - 0.1, bb.y0 - 0.1, bb.z0 - 0.1,
                            bb.x1 + 0.1, bb.y1 + 0.1, bb.z1 + 0.1);
            if (t != null && t.Value < bestT && t.Value < blockT)
            {
                best = ent;
                bestT = t.Value;
            }
        }
        return best;
    }

    private void doAttack()
    {
        var p = player;
        if (p.dead || p.deathTime > 0 || (host?.hasScreen() ?? false)) return;
        var target = crosshairEntity(ATTACK_REACH);
        p.attackAnim = 1;
        if (target != null)
        {
            if (target is LivingEntity || target.type == "end_crystal")
            {
                playerAttack(p, target);
                advance("kill_mob_attempt");
            }
            else if (target.type == "boat" || target.type == "minecart")
            {
                target.hurt(2, "player", p);
            }
            return;
        }
        host?.playSound("entity.player.attack.sweep", p.x, p.y, p.z, 0.3, 1.2);
    }

    private void doUse()
    {
        var p = player;
        if (p.dead || p.deathTime > 0 || (host?.hasScreen() ?? false)) return;
        var ctx = interactCtx();
        // entities first
        var target = crosshairEntity(REACH_SURVIVAL - 1);
        if (target != null && !p.sneaking)
        {
            if (target.interact(p, p.mainHand))
            {
                p.attackAnim = 0.6;
                return;
            }
        }
        var hit = crosshairBlock();
        if (hit != null && (!p.sneaking || p.mainHand == null))
        {
            if (useBlock(ctx, hit.Value))
            {
                p.attackAnim = 0.6;
                return;
            }
        }
        if (useItem(ctx, hit))
        {
            p.attackAnim = 0.6;
        }
    }

    private void pickBlock()
    {
        var p = player;
        var hitOpt = crosshairBlock();
        if (hitOpt == null) return;
        var hit = hitOpt.Value;
        int raw = blockToItem[hit.cell >> 4];
        if (raw < 0) return;
        int itemId = raw;
        // already in hotbar?
        for (int i = 0; i < 9; i++)
        {
            if (p.inventory[i]?.id == itemId)
            {
                p.selectedSlot = i;
                return;
            }
        }
        if (p.gameMode == GameMode.creative)
        {
            p.inventory[p.selectedSlot] = new ItemStack(itemId, 1);
        }
        else
        {
            for (int i = 9; i < 36; i++)
            {
                if (p.inventory[i]?.id == itemId)
                {
                    var tmp = p.inventory[p.selectedSlot];
                    p.inventory[p.selectedSlot] = p.inventory[i];
                    p.inventory[i] = tmp;
                    return;
                }
            }
        }
    }

    // ===========================================================================
    // Input — the app forwards events here when no screen is open
    // ===========================================================================
    public void mouseDown(int button)
    {
        if (!inWorld || (host?.hasScreen() ?? false)) return;
        if (button == 0)
        {
            leftDown = true;
            doAttack();
        }
        else if (button == 1)
        {
            pickBlock();
        }
        else if (button == 2)
        {
            rightDown = true;
            doUse();
            useCooldown = 4;
        }
    }

    public void mouseUp(int button)
    {
        if (button == 0) leftDown = false;
        if (button == 2)
        {
            rightDown = false;
            if (player?.usingItem == true) releaseUsingItem(interactCtx());
        }
    }

    public void mouseDelta(double dx, double dy)
    {
        if (!inWorld || (host?.hasScreen() ?? false) || player == null) return;
        var p = player;
        double sens = 0.0008 + settings.sensitivity * 0.004;
        p.yaw += dx * sens;
        p.pitch += dy * sens * (settings.invertY ? -1 : 1);
        p.pitch = clampD(p.pitch, -Math.PI / 2 + 0.001, Math.PI / 2 - 0.001);
    }

    public void wheelHotbar(int dir)
    {
        if (!inWorld || player == null) return;
        var p = player;
        p.selectedSlot = posMod(p.selectedSlot + dir, 9);
    }

    /// world-mode keydown (app already routed screen input elsewhere).
    /// `now` is a monotonic millisecond clock for double-tap detection.
    public void keyDown(string code, double now, bool ctrlOrCmd = false)
    {
        if (!inWorld) return;
        keys.Add(code);
        var p = player;
        if (code == "Escape")
        {
            host?.openPauseScreen();
            host?.releasePointer();
        }
        else if (code == keybindOrNull("perspective"))
        {
            perspective = (perspective + 1) % 3;
        }
        else if (code == keybindOrNull("inventory"))
        {
            if (p.gameMode == GameMode.creative)
            {
                host?.openScreen("creative", null);
            }
            else
            {
                host?.openScreen("inventory", null);
            }
        }
        else if (code == keybindOrNull("chat"))
        {
            host?.openChat("");
        }
        else if (code == keybindOrNull("command"))
        {
            host?.openChat("/");
        }
        else if (code == keybindOrNull("drop"))
        {
            p.dropSelected(ctrlOrCmd);
        }
        else if (code == keybindOrNull("swapOffhand"))
        {
            var tmp = p.offHand;
            p.offHand = p.mainHand;
            p.mainHand = tmp;
        }
        else
        {
            // hotbar digits
            if (code.StartsWith("Digit") && code.Length == 6 && int.TryParse(code.Substring(code.Length - 1), out var n) && n >= 1 && n <= 9)
            {
                p.selectedSlot = n - 1;
            }
            // double-space → toggle creative flight
            if (code == keybindOrNull("jump"))
            {
                if (now - lastJumpPress < 280 && p.gameMode == GameMode.creative)
                {
                    p.flying = !p.flying;
                    if (p.flying) p.vy = 0;
                }
                lastJumpPress = now;
            }
            if (code == keybindOrNull("sprint")) sprintHeld = true;
        }
        // double-tap forward → sprint
        if (code == keybindOrNull("forward"))
        {
            if (now - lastForwardPress < 250) sprintHeld = true;
            lastForwardPress = now;
        }
    }

    private string keybindOrNull(string name) => keybinds.TryGetValue(name, out var v) ? v : null;

    public void keyUp(string code)
    {
        keys.Remove(code);
        if (code == keybindOrNull("sprint")) sprintHeld = player?.sprinting ?? false;
    }

    /// window blur / screen opened — release all held input
    public void clearInput()
    {
        keys.Clear();
        leftDown = false;
        rightDown = false;
    }

    // ===========================================================================
    // Frame pump — the app's render loop calls this once per frame
    // ===========================================================================
    /// Runs fixed-step sim ticks, then the budgeted light/mesh streamers.
    /// Returns the interpolation partial for rendering.
    public double frame(double dtMs)
    {
        // service the main-thread queue once per frame (Swift's run loop drains
        // DispatchQueue.main between frames, never interleaved with fixed-step
        // ticks) so off-main gen/mesh results are adopted at a frame boundary
        DispatchQueue.main.pumpMain();
        if (!inWorld) return 0;
        accumulator += Math.Min(dtMs, 250);
        int steps = 0;
        while (accumulator >= TICK_MS && steps < 10)
        {
            LoadProf.shared.time("tick", () => tick());
            accumulator -= TICK_MS;
            steps += 1;
        }
        if (steps >= 10) accumulator = 0;
        LoadProf.shared.time("lightQ", () => processLightQueue());
        LoadProf.shared.time("streamMesh", () => streamMeshes());
        LoadProf.shared.tickPrint();
        return paused ? 1 : clampD(accumulator / TICK_MS, 0, 1);
    }

    /// interpolated camera (view bobbing, third person, effect overlays)
    public CamState camState(double partial, double timeSec)
    {
        var p = player;
        var w = world;
        double ix = p.prevX + (p.x - p.prevX) * partial;
        double iy = p.prevY + (p.y - p.prevY) * partial;
        double iz = p.prevZ + (p.z - p.prevZ) * partial;
        double eyeY = iy + (p.sneaking ? 1.27 : PlayerGlobals.PLAYER_EYE);
        double cx = ix, cz = iz;
        // view bobbing — phase/amp advance in tickViewBob (20Hz), interpolate here
        if (settings.viewBobbing && p.vehicle == null)
        {
            double bp = prevBobPhase + (bobPhase - prevBobPhase) * partial;
            double ba = prevBobAmp + (bobAmp - prevBobAmp) * partial;
            eyeY += Math.Abs(detSin(bp * Math.PI)) * ba * 1.2;
            cx += detSin(bp * Math.PI) * ba * 0.3 * detCos(p.yaw);
            cz += detSin(bp * Math.PI) * ba * 0.3 * detSin(p.yaw);
        }
        double yaw = p.yaw, pitch = p.pitch;
        if (perspective == 2)
        {
            yaw += Math.PI;
            pitch = -pitch;
        }
        // third person: pull camera back along the view ray, clipped by blocks
        if (perspective > 0)
        {
            double back = 4.0;
            double dx = detSin(yaw) * detCos(pitch);
            double dy = detSin(pitch);
            double dz = -detCos(yaw) * detCos(pitch);
            var hit = w.raycast(cx, eyeY, cz, dx, dy, dz, back);
            double dist = hit != null ? Math.Max(0.2, hit.Value.t - 0.25) : back;
            cx += dx * dist;
            eyeY += dy * dist;
            cz += dz * dist;
        }
        int eyeBlock = w.getBlock(ifloor(cx), ifloor(eyeY), ifloor(cz));
        int eyeId = eyeBlock >> 4;
        var cam = CamState.Create();
        cam.x = cx; cam.y = eyeY; cam.z = cz;
        cam.yaw = yaw; cam.pitch = pitch;
        cam.fov = (double)settings.fov * (prevFovScale + (fovScale - prevFovScale) * partial);
        cam.underwater = isWaterlogged((ushort)eyeBlock);
        cam.underLava = eyeId == (int)B.lava;
        cam.powderSnow = eyeId == (int)B.powder_snow;
        cam.portalWarp = portalWarp;
        cam.nightVision = p.hasEffect("night_vision") ? 1 : 0;
        cam.darkness = p.hasEffect("darkness") ? (0.6 + 0.4 * detSin(timeSec * 2)) * settings.darknessPulse : 0;
        cam.blindness = p.hasEffect("blindness") ? 1 : 0;
        return cam;
    }

    // ---- base-36 string formatting (Swift String(_, radix: 36)) ---------------
    private static string toBase36(long value)
    {
        if (value == 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        bool neg = value < 0;
        ulong v = neg ? (ulong)(-value) : (ulong)value;
        var sb = new System.Text.StringBuilder();
        while (v > 0)
        {
            sb.Insert(0, digits[(int)(v % 36)]);
            v /= 36;
        }
        return neg ? "-" + sb.ToString() : sb.ToString();
    }
}

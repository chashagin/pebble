// Pebble (Windows) entry point. Opens a window via Silk.NET, drives the render
// loop through a selected IGpuBackend (Vulkan or Direct3D 12), and runs the
// verified engine (GameCore) through the GameHost seam.
//
//   Pebble                 -> Vulkan (default)
//   Pebble --dx12          -> Direct3D 12
//   Pebble --seconds N     -> auto-close after N seconds (headless verification)
//   Pebble --warmup N      -> frames to warm up before a --screenshot (default 180)
//   Pebble --nohud         -> skip the 2D HUD overlay (world only)
//
// The deterministic engine (PebbleCore) is verified (456/456 goldens). This
// shell is the platform layer replacing the macOS Metal/AppKit app. The
// WorldRenderer pipelines that DRAW the streamed meshes are task #15; for now
// the engine generates a real world and streams section meshes to the host,
// which proves end-to-end engine execution inside the Windows app.

using System;
using Pebble;
using Pebble.Gpu;
using Pebble.UI;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

// --audiotest: render a few representative synth recipes to an in-memory buffer
// WITHOUT opening the output device, then assert non-silence (peak/RMS). This is
// the headless proof that the synthesizer produces sound.
if (Array.Exists(args, a => a == "--audiotest"))
{
    // Environment.Exit (not `return`) so the entry point stays void-returning and
    // doesn't force a trailing `return` on the windowed code path below.
    Environment.Exit(Pebble.Audio.AudioTest.Run());
}

// --saveloadtest: headless proof of the SAVE -> LOAD round-trip with block
// entities. Creates a world, places a chest + furnace with real ItemStacks,
// saveAndFlush(synchronous), then constructs a FRESH GameCore and loadWorld()s
// the same id (the title -> Singleplayer -> click-a-world path). Asserts no
// exception, the chest BE survives at its position with its 17 diamonds, and the
// furnace BE survives. This is the gap that was never exercised (createWorld +
// --screen game only). Writes to the real save DB and deletes the test world after.
if (Array.Exists(args, a => a == "--saveloadtest"))
{
    Environment.Exit(SaveLoadTest.Run());
}

// --seedworld: headless — create a world with a chest + furnace (with items),
// save it, and exit. Honours PEBBLE_SUPPORT_DIR so a test harness can stage a
// loadable world in a scratch DB, then launch the windowed app with --loadlast
// pointed at the same dir to verify the real SAVE -> LOAD app flow.
if (Array.Exists(args, a => a == "--seedworld"))
{
    Environment.Exit(SaveLoadTest.Seed());
}

bool useDx12 =
    Array.Exists(args, a => a is "--dx12" or "--d3d12") ||
    (Array.IndexOf(args, "--backend") is int bi && bi >= 0 && bi + 1 < args.Length &&
     args[bi + 1] is "dx12" or "d3d12" or "directx12");

double autoCloseSeconds = 0;
if (Array.IndexOf(args, "--seconds") is int si && si >= 0 && si + 1 < args.Length &&
    double.TryParse(args[si + 1], out var secs)) autoCloseSeconds = secs;

// --screenshot <path>: render ~180 frames so chunks stream + mesh, then read back
// the presented color image and write it as a PNG, then close.
string? screenshotPath = null;
if (Array.IndexOf(args, "--screenshot") is int shi && shi >= 0 && shi + 1 < args.Length)
    screenshotPath = args[shi + 1];
// Frames to warm up before a screenshot so terrain has streamed + meshed. Can be
// overridden with --warmup N (lower values reduce the per-frame draw load at
// capture time, which the heavy DX12 streaming path is sensitive to).
int screenshotWarmupFrames = 180;
if (Array.IndexOf(args, "--warmup") is int wfi && wfi >= 0 && wfi + 1 < args.Length &&
    int.TryParse(args[wfi + 1], out var wf)) screenshotWarmupFrames = wf;

// --testmove: programmatically hold W after warmup to verify the input→movement
// path headlessly (the camera should advance from the spawn position).
bool testMove = Array.Exists(args, a => a == "--testmove");
bool testMoveStarted = false;

// --screen <name>: after boot, force a screen open for screenshots. "title"
// stays on the title screen (no world). Any other name (inventory/crafting/
// chest/...) enters a fresh world first, then opens that screen via the engine.
string? forceScreen = null;
if (Array.IndexOf(args, "--screen") is int fsi && fsi >= 0 && fsi + 1 < args.Length)
    forceScreen = args[fsi + 1];
bool forceScreenApplied = false;

// --loadlast: after boot, load the most-recently-played saved world and enter it
// (the real title -> Singleplayer -> click-saved-world path) instead of opening
// the title screen. Used to verify the SAVE -> LOAD path in the actual app. If no
// saved world exists, it falls back to the title screen.
bool loadLast = Array.Exists(args, a => a == "--loadlast");
bool loadLastApplied = false;

// --loadid <id>: like --loadlast but loads a specific saved world id (used to
// verify loading a particular world, e.g. one with corrupt/lost chunks).
string? loadId = null;
if (Array.IndexOf(args, "--loadid") is int lidi && lidi >= 0 && lidi + 1 < args.Length)
    loadId = args[lidi + 1];

// --spawntest: after warmup, spawn a few mobs in front of the player and aim the
// camera at them, so the entity render pass is guaranteed something visible to
// draw (used to verify the entity pass on both backends).
bool spawnTest = Array.Exists(args, a => a == "--spawntest");
bool spawnTestStarted = false;
double spawnAnchorX = 0, spawnAnchorY = 0, spawnAnchorZ = 0;

// --skylook: after warmup, aim the camera up toward the sun so the sky gradient
// (top→horizon) and the sun billboard are framed for the atmosphere screenshot.
bool skyLook = Array.Exists(args, a => a == "--skylook");

// --fxtest: after warmup, drop a few item entities in front of the player (sprite
// billboards) and continuously emit block-break crumb particles there, so the
// particle + sprite passes are framed for a screenshot. Pins the camera like
// spawntest. Independent of --spawntest so the entity-pass shot stays clean.
bool fxTest = Array.Exists(args, a => a == "--fxtest");
bool fxTestStarted = false;
double fxAnchorX = 0, fxAnchorY = 0, fxAnchorZ = 0;
double fxPx = 0, fxPy = 0, fxPz = 0;

// --breaktest: after warmup, put the player in survival, build a stone wall right
// in front, aim at it and hold left-click so mining progresses and the destroy_N
// crack overlay shows over the targeted block (block-break overlay verification).
bool breakTest = Array.Exists(args, a => a == "--breaktest");
bool breakTestStarted = false;
double brAnchorX = 0, brAnchorY = 0, brAnchorZ = 0, brYaw = 0, brPitch = 0;

// --watertest: after warmup, scan for the nearest water surface around the player
// and aim the camera at it, so the translucent (water) pass is framed for a
// screenshot. Pins the look direction each frame once a target is found.
bool waterTest = Array.Exists(args, a => a == "--watertest");
bool waterFound = false;
double waterYaw = 0, waterPitch = 0;
double waterAnchorX = 0, waterAnchorY = 0, waterAnchorZ = 0;

// Sun shadow-map pass: ON BY DEFAULT on both Vulkan and DX12. The depth image is
// double-buffered per in-flight frame so frame N's shadow write never races frame
// N-1's still-in-flight sample (the cross-frame hazard that used to force shadows
// off). Pass --noshadows to disable. (--shadows is still accepted as a no-op for
// back-compat / explicitness.)
// --ultra: enable the SSAO/volumetrics + bloom post chain (off by default).
bool enableShadows = !Array.Exists(args, a => a == "--noshadows");
bool enableUltra = Array.Exists(args, a => a == "--ultra");

IGpuBackend backend = useDx12
    ? new D3D12Backend(enableShadows, enableUltra)
    : new VulkanBackend(enableShadows, enableUltra);

var host = new HostBridge();
PebbleCore.GameCore? game = null;

// 2D UI / HUD. The canvas batches quads/text; the Hud draws the crosshair,
// hotbar, hearts/hunger/armor/air and XP bar from live player state. Icon layers
// are resolved through the backend (it owns the icon Texture2DArray).
var uiCanvas = new UICanvas { IconResolver = backend.IconResolver };
var hud = new Hud();

// UI screen stack (title/pause/inventory/crafting/...). Draws through the same
// UICanvas the HUD uses; the UIManager applies the GUI scale on BeginFrame.
var ui = new UIManager(uiCanvas);

// Seed a few hotbar + main-inventory items so the HUD and inventory screen
// exercise the icon/count path (survival start is empty). Only fills empties.
void SeedItems(PebbleCore.Player pl)
{
    void Put(int slot, string name, int count)
    {
        if (slot < 0 || slot >= pl.inventory.Count || pl.inventory[slot] != null) return;
        if (!PebbleCore.ItemGlobals.itemExists(name)) return;
        pl.inventory[slot] = PebbleCore.ItemGlobals.stack(name, count);
    }
    Put(0, "stone", 64); Put(1, "oak_planks", 32); Put(2, "diamond_pickaxe", 1);
    Put(3, "iron_sword", 1); Put(4, "apple", 12); Put(5, "torch", 48);
    Put(6, "diamond", 5); Put(7, "bread", 8); Put(8, "iron_ingot", 16);
    // a couple of rows of the main inventory so the grid shows item slots
    Put(9, "cobblestone", 64); Put(10, "oak_log", 32); Put(11, "coal", 24);
    Put(12, "iron_ingot", 40); Put(13, "gold_ingot", 16); Put(14, "redstone", 50);
    Put(15, "bone", 12); Put(16, "string", 9); Put(17, "gunpowder", 7);
    Put(18, "wheat", 20); Put(19, "carrot", 14); Put(20, "stick", 33);
    Put(21, "arrow", 64); Put(22, "leather", 6); Put(23, "feather", 11);
}

// 3D entity renderer: builds per-entity model + part matrices and procedural
// skins each frame; the backend owns the GPU pipeline + per-model VB/skin cache.
var entityRenderer = new EntityRenderer();
var entityList = new EntityDrawList();

// Gameplay-feedback render data (sprite billboards + block-break crack overlay),
// rebuilt each frame from the live world. Particles live on the host bridge (the
// engine spawns them through the world hooks); we tick them once per rendered
// frame here and hand them to the backend's billboard pass.
var spriteList = new SpriteList();
var breakOverlay = new BreakOverlay();

var options = WindowOptions.Default with
{
    Size = new Vector2D<int>(1280, 720),
    Title = $"Pebble — {backend.Name}",
    API = useDx12 ? GraphicsAPI.None : GraphicsAPI.DefaultVulkan,
    VSync = true,
};

using var window = Window.Create(options);

double elapsed = 0, sinceLog = 0;
long lastUploads = 0;
int frameNo = 0;
bool screenshotRequested = false;

window.Load += () =>
{
    backend.Initialize(window);
    Console.WriteLine($"[Pebble] window loaded, backend = {backend.Name}");

    // Boot the engine: the GameCore ctor runs the full registration in frozen
    // order. The app opens to the TITLE SCREEN (mirroring the macOS app) — the
    // world is created from the title's "Create New World" flow, not at boot.
    Console.WriteLine("[Pebble] booting engine (registering blocks/items/biomes/recipes/loot/entities/systems)...");
    game = new PebbleCore.GameCore { host = host };
    Console.WriteLine($"[Pebble] engine booted: {PebbleCore.Reg.blockDefs.Count} blocks registered");

    var input = window.CreateInput();

    // Wire the screen stack to the host bridge + app callbacks.
    host.ui = ui;
    host.game = game;
    host.onReleasePointer = () =>
    {
        foreach (var m in input.Mice) m.Cursor.CursorMode = CursorMode.Normal;
    };
    // title "Quit Game" closes the window; loading screen reads live section count
    TitleScreen.QuitAction = () => window.Close();
    LoadingScreen.SectionCountFn = () =>
    {
        if (game?.player is not { } p) return 0;
        int pcx = (int)Math.Floor(p.x) >> 4, pcz = (int)Math.Floor(p.z) >> 4;
        int n = 0;
        foreach (var key in host.sections.Keys)
            if (Math.Abs(key.Item1 - pcx) <= 2 && Math.Abs(key.Item3 - pcz) <= 2) n++;
        return n;
    };

    // GUI scale + initial layout, then open the title screen.
    {
        var fb = window.FramebufferSize;
        ui.Resize(Math.Max(1, fb.X), Math.Max(1, fb.Y), game.settings.guiScale);
    }
    ui.Open(new TitleScreen(), game);

    InputRouter.Wire(input, game, ui, () => elapsed * 1000.0,
        () => { var fb = window.FramebufferSize; return ((double)fb.X, (double)fb.Y); });
    Console.WriteLine($"[Pebble] input wired ({input.Keyboards.Count} keyboards, {input.Mice.Count} mice) — title screen open; click buttons, Esc backs out");
};

window.FramebufferResize += sz => backend.Resize(sz.X, sz.Y);

window.Render += dt =>
{
    elapsed += dt; sinceLog += dt;
    frameNo++;

    // Drive the engine: tick + stream meshes (frame() pumps the main queue,
    // ticks at fixed 20 Hz, and streams section meshes to host.uploadMesh).
    double partial = game?.frame(dt * 1000.0) ?? 0;

    // --loadlast / --loadid: load a saved world and enter it, mirroring title ->
    // Singleplayer -> clicking a saved world (the SAVE -> LOAD path).
    if ((loadLast || loadId != null) && !loadLastApplied && game != null && frameNo >= 5)
    {
        loadLastApplied = true;
        var saved = game.listWorlds();
        if (saved.Count == 0)
        {
            Console.WriteLine("[Pebble] --loadlast: no saved worlds found — staying on title screen");
        }
        else
        {
            string id;
            string name;
            if (loadId != null)
            {
                var match = saved.Find(s => s.id == loadId);
                id = match.id ?? loadId;
                name = match.name ?? "(unknown)";
            }
            else
            {
                saved.Sort((a, b) => b.lastPlayed.CompareTo(a.lastPlayed));
                id = saved[0].id;
                name = saved[0].name;
            }
            Console.WriteLine($"[Pebble] --loadlast: loading '{name}' (id={id})");
            ui.CloseAll(game);
            game.loadWorld(id);   // the crash path: loadWorld -> enterWorld -> adoptChunkBlockEntities
            Console.WriteLine($"[Pebble] --loadlast: entered world = {game.hasWorld()}");
        }
    }

    // --screen <name>: force a screen open shortly after boot for screenshots.
    if (forceScreen != null && !forceScreenApplied && game != null && frameNo >= 5)
    {
        if (forceScreen == "title")
        {
            // already on the title screen from window.Load — nothing to do
            forceScreenApplied = true;
            Console.WriteLine("[Pebble] --screen title: title screen is open");
        }
        else if (!game.hasWorld())
        {
            // enter a fresh world, then open the requested screen once it's up
            ui.CloseAll(game);
            game.createWorld(name: "Screenshot World", seedText: "12345", mode: 0, difficulty: 2);
            if (game.player is { } seedP) SeedItems(seedP);
            Console.WriteLine($"[Pebble] --screen {forceScreen}: world created, opening screen");
        }
        else
        {
            game.openScreen(forceScreen, null);
            forceScreenApplied = true;
            Console.WriteLine($"[Pebble] --screen {forceScreen}: screen open = {ui.HasScreen()}");
        }
    }

    if (testMove && !testMoveStarted && game != null && game.hasWorld() && frameNo >= 90)
    {
        testMoveStarted = true;
        game.keyDown("KeyW", elapsed * 1000.0);   // forward
        Console.WriteLine("[Pebble] testmove: holding W (forward) — camera should advance");
    }

    // --spawntest: once the world has warmed up, spawn a row of mobs a few blocks
    // in front of the player and point the camera at them so the entity pass is
    // guaranteed visible silhouettes near screen center.
    if (spawnTest && !spawnTestStarted && game != null && game.hasWorld()
        && game.player is { } sp && frameNo >= Math.Min(screenshotWarmupFrames, 120))
    {
        spawnTestStarted = true;
        // Freeze the player so it can't slide off a slope between spawn and capture
        // (gravity/physics keep ticking otherwise and the camera drifts away).
        sp.noGravity = true;
        sp.vx = 0; sp.vy = 0; sp.vz = 0;
        // Face +Z and spawn mobs just ahead along +Z so they fill the view. Engine
        // yaw convention (see camState dir): dir.z = cos(yaw), dir.x = -sin(yaw), so
        // yaw = 0 looks toward +Z; dir.y = sin(-pitch), so a small +pitch looks down.
        sp.yaw = 0; sp.prevYaw = 0; sp.pitch = 0.18; sp.prevPitch = 0.18;
        double bx = sp.x;
        double bz = sp.z;
        // Float the mobs slightly below the camera's eye height a few blocks ahead
        // with gravity off, so they hover in clear air in front of the camera
        // regardless of the local terrain (the player may stand on a slope/column).
        double by = sp.eyeY() - 1.6;
        string[] mobs = { "cow", "pig", "zombie", "chicken", "creeper" };
        for (int i = 0; i < mobs.Length; i++)
        {
            double mx = bx + (i - 2) * 1.8;        // spread across the view
            double mz = bz + 6.0;                   // 6 blocks ahead (+Z)
            var e = PebbleCore.EntityRegistryGlobals.spawnMob(
                game.world, mobs[i], mx, by, mz, new PebbleCore.SpawnOpts(persistent: true));
            if (e != null)
            {
                e.yaw = Math.PI; e.prevYaw = Math.PI;   // face back toward player
                e.setPos(mx, by, mz);                    // pin (avoid 1-tick gravity drift in prev*)
                e.noGravity = true;                      // keep them put for the screenshot
            }
        }
        spawnAnchorX = sp.x; spawnAnchorY = sp.y; spawnAnchorZ = sp.z;
        Console.WriteLine($"[Pebble] spawntest: spawned {mobs.Length} mobs ~3 blocks ahead of player at ({bx:F1},{by:F1},{bz:F1})");
    }

    // Hold the player perfectly still after spawntest: re-pin its position and look
    // direction every frame so the camera keeps framing the mobs until capture.
    if (spawnTest && spawnTestStarted && game?.player is { } pp)
    {
        pp.noGravity = true;
        pp.vx = 0; pp.vy = 0; pp.vz = 0;
        pp.setPos(spawnAnchorX, spawnAnchorY, spawnAnchorZ);
        pp.yaw = 0; pp.prevYaw = 0; pp.pitch = 0.18; pp.prevPitch = 0.18;
    }

    // --fxtest: drop item-entity sprites + emit break particles in front of the
    // player, then hold the camera on them. Spawns once; emits each frame.
    if (fxTest && !fxTestStarted && game != null && game.hasWorld()
        && game.player is { } fp && frameNo >= Math.Min(screenshotWarmupFrames, 120))
    {
        fxTestStarted = true;
        // lift the camera high above the forest canopy and place the items at eye
        // level a few blocks ahead, so the SKY is the backdrop (no foliage behind
        // the sprites) and they read clearly against the gradient.
        fp.setPos(fp.x, fp.y + 18, fp.z);
        fp.noGravity = true; fp.vx = 0; fp.vy = 0; fp.vz = 0;
        fp.yaw = 0; fp.prevYaw = 0; fp.pitch = 0.0; fp.prevPitch = 0.0;
        double bx = fp.x, bz = fp.z, by = fp.eyeY();
        string[] drops = { "diamond", "apple", "iron_ingot", "arrow", "egg" };
        for (int i = 0; i < drops.Length; i++)
        {
            if (!PebbleCore.ItemGlobals.itemExists(drops[i])) continue;
            // close (1.6 blocks) at eye level so each sprite is large + crisp
            double mx = bx + (i - 2) * 0.5, mz = bz + 1.6;
            var it = PebbleCore.MiscGlobals.spawnItem(game.world, mx, by, mz,
                PebbleCore.ItemGlobals.stack(drops[i], 1), 0, 0, 0);
            it.noGravity = true; it.vx = it.vy = it.vz = 0;
            it.setPos(mx, by, mz);
        }
        // emit break crumbs just left of the item row (still near the camera)
        fxPx = bx - 1.6; fxPy = by; fxPz = bz + 1.6;
        fxAnchorX = fp.x; fxAnchorY = fp.y; fxAnchorZ = fp.z;
        Console.WriteLine($"[Pebble] fxtest: dropped {drops.Length} item sprites ~3 blocks ahead at ({bx:F1},{by:F1},{bz:F1})");
    }
    if (fxTest && fxTestStarted && game?.player is { } fpp)
    {
        fpp.noGravity = true; fpp.vx = 0; fpp.vy = 0; fpp.vz = 0;
        fpp.setPos(fxAnchorX, fxAnchorY, fxAnchorZ);
        fpp.yaw = 0; fpp.prevYaw = 0; fpp.pitch = 0.0; fpp.prevPitch = 0.0;
        // keep emitting break crumbs to the side of the dropped items each frame
        int stoneCell = PebbleCore.BlockCaches.cell(PebbleCore.Reg.bid("stone"));
        game.world.hooks.addParticles("block", fxPx, fxPy, fxPz, 6, 0.5, stoneCell);
    }

    // --breaktest: survival mining of a stone wall in front → destroy_N crack overlay.
    if (breakTest && !breakTestStarted && game != null && game.hasWorld()
        && game.player is { } brp && frameNo >= Math.Min(screenshotWarmupFrames, 140))
    {
        breakTestStarted = true;
        brp.setGameMode(PebbleCore.GameMode.survival);   // crack only shows in survival
        // lift above the canopy into clear air so nothing occludes the wall
        brp.setPos(brp.x, brp.y + 18, brp.z);
        brp.noGravity = true; brp.vx = 0; brp.vy = 0; brp.vz = 0;
        brYaw = 0; brPitch = 0;                            // look +Z, level
        brp.yaw = brYaw; brp.prevYaw = brYaw; brp.pitch = brPitch; brp.prevPitch = brPitch;
        // build a 3×3 stone wall ~2.5 blocks ahead (+Z) at eye level
        int wx0 = (int)Math.Floor(brp.x), wz = (int)Math.Floor(brp.z) + 3;
        int wy0 = (int)Math.Floor(brp.eyeY());
        ushort stone = PebbleCore.BlockCaches.cell(PebbleCore.Reg.bid("stone"));
        for (int ddx = -1; ddx <= 1; ddx++)
            for (int ddy = -1; ddy <= 1; ddy++)
                game.world.setBlock(wx0 + ddx, wy0 + ddy, wz, stone);
        brAnchorX = brp.x; brAnchorY = brp.y; brAnchorZ = brp.z;
        game.mouseDown(0);   // hold left-click → mining progresses each tick
        Console.WriteLine($"[Pebble] breaktest: survival mining a stone wall at z={wz}, eye y={wy0}");
    }
    if (breakTest && breakTestStarted && game?.player is { } brpp)
    {
        brpp.noGravity = true; brpp.vx = 0; brpp.vy = 0; brpp.vz = 0;
        brpp.setPos(brAnchorX, brAnchorY, brAnchorZ);
        brpp.yaw = brYaw; brpp.prevYaw = brYaw; brpp.pitch = brPitch; brpp.prevPitch = brPitch;
        // keep the block from fully breaking (would end the crack): pin progress < 1
        if (brpp.breakingProgress > 0.85) brpp.breakingProgress = 0.55;
    }

    // --watertest: find the nearest water surface near the player and aim at it so
    // the translucent water pass is framed. Scans a column-search outward in a disc.
    if (waterTest && game != null && game.hasWorld() && game.player is { } wtp
        && frameNo >= Math.Min(screenshotWarmupFrames, 150))
    {
        if (!waterFound)
        {
            int waterId = PebbleCore.Reg.bid("water");
            double ex = wtp.x, ey = wtp.eyeY(), ez = wtp.z;
            int bx = (int)Math.Floor(ex), bz = (int)Math.Floor(ez);
            double best = double.MaxValue; double tx = 0, ty = 0, tz = 0;
            // search a 128-block radius disc; for each column find the topmost water.
            for (int dx = -128; dx <= 128; dx += 2)
            for (int dz = -128; dz <= 128; dz += 2)
            {
                int wx = bx + dx, wz = bz + dz;
                for (int wy = 100; wy >= 48; wy--)   // top-down: first water = surface
                {
                    if ((game.world.getBlock(wx, wy, wz) >> 4) != waterId) continue;
                    if ((game.world.getBlock(wx, wy + 1, wz) >> 4) != 0) break; // submerged, skip column
                    double cx2 = wx + 0.5, cy2 = wy + 1.0, cz2 = wz + 0.5;
                    double d = (cx2 - ex) * (cx2 - ex) + (cz2 - ez) * (cz2 - ez);
                    if (d > 9 && d < best) { best = d; tx = cx2; ty = cy2; tz = cz2; }
                    break;
                }
            }
            if (best < double.MaxValue)
            {
                // Teleport just above + back from the surface so the water fills the
                // lower frame, then aim down at it (the surface may sit in a pit the
                // spawn hilltop occludes).
                waterAnchorX = tx; waterAnchorY = ty + 5.0; waterAnchorZ = tz + 9.0;
                wtp.setPos(waterAnchorX, waterAnchorY, waterAnchorZ);
                wtp.noGravity = true; wtp.vx = wtp.vy = wtp.vz = 0;
                double ey2 = wtp.eyeY();
                double dirx = tx - waterAnchorX, diry = ty - ey2, dirz = tz - waterAnchorZ;
                double l = Math.Sqrt(dirx * dirx + diry * diry + dirz * dirz);
                dirx /= l; diry /= l; dirz /= l;
                waterYaw = Math.Atan2(-dirx, dirz);
                waterPitch = -Math.Asin(diry);
                waterFound = true;
                Console.WriteLine($"[Pebble] watertest: teleported above water surface ({tx:F1},{ty:F1},{tz:F1})");
            }
            else if (frameNo % 30 == 0)
                Console.WriteLine("[Pebble] watertest: no water within 64 blocks yet");
        }
        if (waterFound)
        {
            wtp.noGravity = true; wtp.vx = wtp.vy = wtp.vz = 0;
            wtp.setPos(waterAnchorX, waterAnchorY, waterAnchorZ);
            wtp.yaw = waterYaw; wtp.prevYaw = waterYaw;
            wtp.pitch = waterPitch; wtp.prevPitch = waterPitch;
        }
    }

    // --skylook: aim the camera up toward the sun's azimuth so the gradient sky +
    // sun billboard fill the upper frame (atmosphere-pass screenshot framing).
    if (skyLook && game != null && game.hasWorld() && game.player is { } slp)
    {
        // sunDir = (-sin(a*2π+π), cos(a*2π), 0.18) (see SkyState). The engine view
        // dir is (cos(p)*-sin(yaw), sin(-p), cos(p)*cos(yaw)); solve yaw/pitch so the
        // view ray points at the sun.
        double a = game.world.sunAngle();
        double sx = -Math.Sin(a * Math.PI * 2 + Math.PI);
        double sy = Math.Cos(a * Math.PI * 2);
        double sz = 0.18;
        double len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        sx /= len; sy /= len; sz /= len;
        double yaw = Math.Atan2(-sx, sz);           // dir.x=-sin(yaw)*cos(p), dir.z=cos(yaw)*cos(p)
        double pitch = -Math.Asin(sy);              // dir.y = sin(-pitch)
        slp.yaw = yaw; slp.prevYaw = yaw;
        slp.pitch = pitch; slp.prevPitch = pitch;
    }

    // Build the WHOLE UI vertex stream BEFORE opening the frame's command list:
    // HUD (when in-world) + the current screen, in one UICanvas. Drawing resolves
    // item icons, which the backend uploads to its icon Texture2DArray; those GPU
    // uploads must NOT run while the per-frame command list is open (on D3D12 a
    // mid-frame queue execution can hang the device). The UIManager applies the
    // GUI scale on BeginFrame (mirrors UIManager.resize).
    bool drawUi = game != null;
    if (drawUi)
    {
        var fb = window.FramebufferSize;
        ui.Resize(Math.Max(1, fb.X), Math.Max(1, fb.Y), game!.settings.guiScale, game);

        ui.BeginFrame();
        var screen = ui.Current();
        bool hudHidden = Array.Exists(args, a => a == "--nohud");
        // draw the HUD under the screen when in-world and the screen doesn't pause
        // the game (a non-pausing screen like a container still shows the HUD)
        if (game.hasWorld() && !hudHidden &&
            (screen == null || screen.showHUD || !screen.pausesGame))
        {
            hud.Draw(uiCanvas, ui.width, ui.height, game, partial);
        }
        screen?.Draw(ui, game, partial);
        ui.EndFrame();
    }

    // Build the entity draw list (model + part matrices, skins) BEFORE opening the
    // frame, like the HUD: skin/VB uploads must not run mid-frame on the per-frame
    // command list. The backend caches GPU resources keyed by model name.
    bool drawEntities = game != null && game.hasWorld();
    if (drawEntities)
        entityRenderer.Build(entityList, game!, partial, elapsed);

    // Advance the particle simulation once per rendered frame (matches the macOS
    // app), then build the sprite-billboard list + the block-break crack overlay.
    // Icon uploads (sprite/held-item) happen via the resolver — already exercised
    // by the HUD above, so they don't run mid-frame.
    if (game != null && game.hasWorld())
    {
        host.particles.Tick(game.world);
        WorldFx.BuildSprites(spriteList, game, partial, (id, data) => backend.IconResolver(id, data));
        WorldFx.BuildBreakOverlay(breakOverlay, game, partial);
    }

    // Render the streamed world (the render pass clears color+depth internally).
    backend.BeginFrame();
    if (game != null && game.hasWorld())
    {
        var rcam = game.camState(partial, elapsed);
        // Atmosphere: a real sky gradient + sun/moon behind the world, replacing
        // the flat clear. Computed from the live world time-of-day (SkyState).
        var sky = Pebble.Gpu.SkyState.FromWorld(game.world, rcam, game.settings.renderDistance);
        backend.RenderSky(sky, rcam, elapsed);
        backend.RenderWorld(host, rcam, elapsed);
    }
    else
        backend.ClearColor(0.62f, 0.74f, 1.0f, 1f);   // sky

    // Draw the 3D entities between the world and the HUD (depth test+write ON).
    if (drawEntities)
    {
        var ecam = game!.camState(partial, elapsed);
        backend.RenderEntities(entityRenderer, entityList, ecam, elapsed);
        // Block-break crack overlay, then sprite billboards, then particles —
        // all after the world+entities so they composite correctly (matches the
        // macOS WorldRenderer ordering: cubes/crack, sprites, particles).
        backend.RenderBreakOverlay(breakOverlay, ecam);
        backend.RenderSprites(spriteList, ecam);
        backend.RenderParticles(host.particles, ecam);
    }

    // Draw the pre-built UI (HUD + current screen) on top of the world
    // (icons already uploaded above).
    if (drawUi)
        backend.RenderUI(uiCanvas);

    // Screenshot: after a warmup so chunks have streamed + meshed, request the
    // next frame be captured; close once it's written.
    if (screenshotPath != null)
    {
        if (!screenshotRequested && frameNo >= screenshotWarmupFrames)
        {
            backend.RequestScreenshot(screenshotPath);
            screenshotRequested = true;
        }
    }

    backend.EndFrame();

    if (screenshotPath != null && screenshotRequested && backend.ScreenshotComplete)
    {
        Console.WriteLine($"[Pebble] screenshot done; live sections={host.sections.Count}");
        window.Close();
    }

    if (sinceLog >= 1.0)
    {
        sinceLog = 0;
        long up = System.Threading.Interlocked.Read(ref host.totalUploads);
        string pos = "";
        if (game != null && game.hasWorld())
        {
            var cam = game.camState(partial, elapsed);
            pos = $", camera=({cam.x:F1},{cam.y:F1},{cam.z:F1})";
        }
        Console.WriteLine($"[Pebble] t={elapsed:F0}s  meshes streamed={up} (+{up - lastUploads}/s), live sections={host.sections.Count}{pos}");
        lastUploads = up;
    }

    if (autoCloseSeconds > 0 && elapsed >= autoCloseSeconds)
    {
        long up = System.Threading.Interlocked.Read(ref host.totalUploads);
        Console.WriteLine($"[Pebble] DONE after {elapsed:F1}s — total meshes streamed={up}, live sections={host.sections.Count}");
        window.Close();
    }
};

window.Closing += () => { host.disposeAudio(); backend.Dispose(); };

window.Run();

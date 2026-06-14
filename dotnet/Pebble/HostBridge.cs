// Minimal GameHost: the seam the verified engine (GameCore) talks through.
// For now it captures the chunk-section meshes the engine streams (so we can
// render them next) and counts them; UI / audio / particles are stubs that the
// later tasks (#15/#16) fill in. Implements PebbleCore.GameHost verbatim.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PebbleCore;
using Pebble.UI;

namespace Pebble;

/// A streamed chunk-section mesh, kept until the WorldRenderer draws it.
public sealed class SectionMesh
{
    public int cx, sy, cz, minY;
    public MeshOutput mesh;
    public SectionMesh(int cx, int sy, int cz, int minY, MeshOutput mesh)
    { this.cx = cx; this.sy = sy; this.cz = cz; this.minY = minY; this.mesh = mesh; }
}

public sealed class HostBridge : GameHost
{
    // section key (cx,sy,cz) → mesh. Concurrent because uploadMesh runs on the
    // engine's main-pump while the render thread reads the set.
    public readonly ConcurrentDictionary<(int, int, int), SectionMesh> sections = new();
    public long totalUploads;
    public long totalRemovals;

    // ---- renderer (the part we actually use right now) -------------------
    public void uploadMesh(int cx, int sy, int cz, int minY, MeshOutput mesh)
    {
        sections[(cx, sy, cz)] = new SectionMesh(cx, sy, cz, minY, mesh);
        System.Threading.Interlocked.Increment(ref totalUploads);
    }

    public void removeChunkMeshes(int cx, int cz, int sections)
    {
        for (int sy = 0; sy < sections; sy++)
        {
            if (this.sections.TryRemove((cx, sy, cz), out _))
                System.Threading.Interlocked.Increment(ref totalRemovals);
        }
    }

    public void clearAllSections() => sections.Clear();

    // ---- screens (drive the UIManager screen stack) ----------------------
    // Wired by Program.cs at startup. The render loop owns the UIManager and the
    // GameCore; the bridge routes the engine's screen requests into the stack.
    public UIManager? ui;
    public GameCore? game;
    /// app callback to release the OS pointer (show cursor) when a screen opens
    public Action? onReleasePointer;

    public bool hasScreen() => ui?.HasScreen() ?? false;
    public bool screenPausesGame() => ui?.Current()?.pausesGame ?? false;

    public void openScreen(string kind, ScreenData? data)
    {
        if (ui == null || game == null) return;
        Screen? s = kind switch
        {
            "crafting" => new CraftingScreen(),
            "inventory" => new InventoryScreen(),
            "creative" => new InventoryScreen(),     // creative inventory deferred → survival inventory
            "chest" => MakeChest(data, data?.title ?? "Chest"),
            "ender_chest" => MakeEnderChest(),
            // container/machine screens (wired to the engine like the macOS app)
            "furnace" => MakeFurnace(data),
            "brewing" => MakeBrewing(data),
            "enchanting" => new EnchantingScreen(data?.x ?? 0, data?.y ?? 0, data?.z ?? 0),
            "anvil" => new AnvilScreen(data?.x ?? 0, data?.y ?? 0, data?.z ?? 0, data?.damage ?? 0),
            "grindstone" => new GrindstoneScreen(),
            "stonecutter" => new StonecutterScreen(),
            "smithing" => new SmithingScreen(),
            "beacon" => MakeBeacon(data),
            "sign" => null,
            _ => null,
        };
        if (s != null)
        {
            ui.Open(s, game);
            if (ui.HasScreen()) onReleasePointer?.Invoke();
        }
    }

    private Screen? MakeChest(ScreenData? data, string title)
    {
        var be = data?.be;
        if (be?.items == null) return new ContainerStub(title);
        int count = be.items.Length;
        return new ChestScreen(i => be.items[i], (i, v) => be.items[i] = v, count, title);
    }
    // Furnace/brewing/beacon need a BlockEntityData. Real interactions (Interact.cs)
    // always supply data.be; the --screen debug arg passes null, so build a
    // throwaway BE at the player's feet for those force-opens.
    private Screen MakeFurnace(ScreenData? data)
        => new FurnaceScreen(data?.be ?? BlockEntities.makeFurnaceBE(PX(), PY(), PZ(), "furnace"));
    private Screen MakeBrewing(ScreenData? data)
        => new BrewingScreen(data?.be ?? BlockEntities.makeBrewingBE(PX(), PY(), PZ()));
    private Screen MakeBeacon(ScreenData? data)
    {
        var be = data?.be;
        if (be == null)
        {
            be = new BlockEntityData("beacon", PX(), PY(), PZ());
            be.levels = 4;   // full pyramid so every power shows unlocked in the debug view
        }
        return new BeaconScreen(be);
    }
    private int PX() => (int)Math.Floor(game?.player?.x ?? 0);
    private int PY() => (int)Math.Floor(game?.player?.y ?? 0);
    private int PZ() => (int)Math.Floor(game?.player?.z ?? 0);
    private Screen MakeEnderChest()
    {
        var p = game!.player;
        return new ChestScreen(i => p.enderChest[i], (i, v) => p.enderChest[i] = v, p.enderChest.Count, "Ender Chest");
    }

    public void openTrading(Mob villager)
    {
        // trading screen deferred — show a stub so the interaction doesn't crash
        if (ui == null || game == null) return;
        ui.Open(new ContainerStub("Trading"), game);
        onReleasePointer?.Invoke();
    }
    public void openVehicleChest(string kind, Entity vehicle)
    {
        if (ui == null || game == null) return;
        ui.Open(new ContainerStub(kind == "boat_chest" ? "Chest Boat" : "Minecart with Chest"), game);
        onReleasePointer?.Invoke();
    }
    public void openChat(string prefix)
    {
        // chat/command screen deferred
    }
    public void openDeathScreen(string message)
    {
        // death screen deferred — auto-respawn so play can continue
        game?.respawnPlayer();
    }
    public void openPauseScreen()
    {
        if (ui == null || game == null) return;
        if (ui.HasScreen()) return;   // don't stack pause on top of an open screen
        ui.Open(new PauseScreen(), game);
        onReleasePointer?.Invoke();
    }
    public void openTitleScreen()
    {
        if (ui == null || game == null) return;
        ui.Open(new TitleScreen(), game);
        onReleasePointer?.Invoke();
    }
    public void closeAllScreens() => ui?.CloseAll(game!);
    public void releasePointer() => onReleasePointer?.Invoke();

    // ---- HUD / chat -------------------------------------------------------
    public void showActionBar(string text, int time) { }
    public void pushChat(string line) { }
    public void pushToast(AdvancementDef adv) { }
    public void setBossBars(List<BossBarInfo> bars) { }

    // ---- audio (synthesizer ported from Audio.swift) ----------------------
    // Lazily constructed so headless tests / offline rendering can run without a
    // device; the engine opens WASAPI on first construction here.
    private Pebble.Audio.AudioEngine? audio;

    private Pebble.Audio.AudioEngine Audio
    {
        get
        {
            if (audio == null)
            {
                var a = new Pebble.Audio.AudioEngine();
                a.InitEngine(openDevice: true);
                audio = a;
            }
            return audio;
        }
    }

    public void playSound(string name, double x, double y, double z, double volume, double pitch)
    {
        // jukebox discs are requested through playSound with a "jukebox.play.<disc>"
        // name (see Interact.cs); route those to the disc generator instead.
        if (name.StartsWith("jukebox.play."))
            Audio.PlayDisc(name.Substring("jukebox.play.".Length), x, y, z);
        else
            Audio.Play(name, x, y, z, volume, pitch);
    }
    public void playUI(string name) => Audio.PlayUI(name);
    public void setAudioEnvironment(bool underwater, double caveFactor) => Audio.SetEnvironment(underwater, caveFactor);
    public void setAudioListener(double x, double y, double z, double yaw) => Audio.SetListener(x, y, z, yaw);
    public void tickMusic(string mood, bool enabled) => Audio.TickMusic(mood, enabled);
    public void stopDisc() => Audio.StopDisc();

    public void disposeAudio() => audio?.Dispose();

    // ---- particles (ported from ParticlesM.swift) -------------------------
    // The engine calls addParticles/spawnPrecipitation through the world hooks
    // (see GameCore.makeHooks). We forward the spawn into the CPU particle system;
    // Program ticks it once per rendered frame and hands the packed instance
    // stream to the backend's billboard pass.
    public readonly Particles particles = new Particles();

    public void addParticles(string type, double x, double y, double z, int count, double spread, int cell)
    {
        if (game?.world is { } w)
            particles.Spawn(w, type, x, y, z, count, spread, cell);
    }
    public void spawnPrecipitation(string kind, double x, double y, double z, double groundY)
    {
        // rain/snow are single-particle ambience streamed per column by the engine.
        if (game?.world is { } w)
            particles.Spawn(w, kind, x, y, z, 1, 0.0);
    }
}

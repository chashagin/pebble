// Menus — title screen, world select/create, loading, pause, and a minimal
// options screen. Ported from the PROCEDURAL path of Sources/Pebble/MenusM.swift
// (no title photo / wordmark texture on the Windows app, so the animated-sky +
// block-shadowed-text fallback is what runs). The title screen's startup is
// driven by HostBridge.openTitleScreen(); see main.swift parity.

using System;
using System.Linq;
using PebbleCore;
using static PebbleCore.ItemGlobals;

namespace Pebble.UI;

// =============================================================================
public sealed class TitleScreen : Screen
{
    private string splash = "";
    private static readonly string[] SPLASHES =
    {
        "Punch a tree!", "Watch out for creepers!", "Don't dig straight down!",
        "Diamonds run deep!", "Now with wardens!", "Sculk is listening!",
        "The dragon is waiting!", "Cherry blossoms!", "Archaeology!",
        "Goats will punt you!", "Trade with villagers!", "Ride a strider!",
        "X marks the buried treasure!", "Hero of the Village!", "Singleplayer, for now!",
        "Do not stare at endermen!", "Beds explode in the Nether!", "Llamas spit back!",
        "Lava is not a swimming pool!", "Blame the goat!", "Bring a bucket!",
        "Mostly bug free!", "Creepers hate him!", "The chickens are watching!",
    };

    /// app-supplied "quit the process" action (wired in Program.cs)
    public static Action? QuitAction;

    public TitleScreen() { closeOnEsc = false; }

    public override void InitScreen(UIManager ui, GameCore game)
    {
        if (splash.Length == 0) splash = SPLASHES[new Random().Next(SPLASHES.Length)];
        double cx = Math.Floor(ui.width / 2);
        double y = Math.Floor(ui.height / 4) + 48;
        buttons.Add(new Button(cx - 100, y, 200, 20, "Singleplayer", () => ui.Open(new WorldSelectScreen(), game)));
        y += 24;
        buttons.Add(new Button(cx - 100, y, 200, 20, "Credits", () => ui.Open(new CreditsScreen(), game)));
        y += 36;
        buttons.Add(new Button(cx - 100, y, 98, 20, "Options...", () => ui.Open(new SettingsScreen(), game)));
        buttons.Add(new Button(cx + 2, y, 98, 20, "Quit Game", () => QuitAction?.Invoke()));
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        var cv = ui.cv;
        double now = Environment.TickCount64;
        // animated gradient sky + floating cubes fallback (no title photo)
        double t = now / 30000.0;
        string top = $"hsl({(215 + Math.Sin(t) * 12).ToString(System.Globalization.CultureInfo.InvariantCulture)}, 55%, {(28 + Math.Sin(t * 1.7) * 6).ToString(System.Globalization.CultureInfo.InvariantCulture)}%)";
        string bottom = $"hsl({(230 + Math.Cos(t) * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)}, 45%, 12%)";
        cv.FillRect(0, 0, ui.width, ui.height, top, bottom);
        for (int i = 0; i < 24; i++)
        {
            double fx = ((i * 137.5 + now / (90 + i * 7)) % (ui.width + 40)) - 20;
            double fy = 20 + ((i * 53) % Math.Max(1, (int)ui.height - 60));
            double size = 3 + (i % 4) * 2;
            cv.SetFill($"hsla({110 + i * 17 % 120}, 35%, {30 + i % 30}%, 0.25)");
            cv.FillRect(fx, fy, size, size);
        }
        // wordmark: block-shadowed text (no logo texture)
        double logoY = Math.Floor(ui.width >= 0 ? Math.Floor(ui.height / 4) - 26 : 0);
        cv.DrawTextCentered("PEBBLE", ui.width / 2 + 2, logoY + 2, 4, "#1c1c1c", shadow: false);
        cv.DrawTextCentered("PEBBLE", ui.width / 2, logoY, 4, "#e8e8e8", shadow: false);
        // splash anchored to the logo's right edge
        cv.Save();
        cv.Translate(ui.width / 2 + 92, logoY + 26);
        double pulse = 1 + Math.Sin(now / 250.0) * 0.06;
        cv.Scale(pulse, pulse);
        cv.DrawTextCentered(splash, 0, 0, 1, "#ffff55");
        cv.Restore();
        cv.DrawText("Pebble (Windows)", 2, ui.height - 10, 1, "#c8c8c8");
        string credit = "Singleplayer, for now";
        cv.DrawText(credit, ui.width - Font.TextWidth(credit) - 2, ui.height - 10, 1, "#c8c8c8");
        ui.DrawButtons(this);
    }
}

// =============================================================================
public sealed class WorldSelectScreen : Screen
{
    private System.Collections.Generic.List<WorldRecord> worlds = new();
    private int selected = -1;
    private bool loaded = false;
    private Button playBtn = null!;
    private Button deleteBtn = null!;
    private double scroll = 0.0;
    private const double listTop = 30;
    private double ListBottom(UIManager ui) => ui.height - 78;
    private double MaxScroll(UIManager ui) => Math.Max(0, worlds.Count * 30 - (ListBottom(ui) - listTop));

    public override void InitScreen(UIManager ui, GameCore game)
    {
        worlds = game.listWorlds().OrderByDescending(w => w.lastPlayed).ToList();
        loaded = true;
        double cx = Math.Floor(ui.width / 2);
        double by = ui.height - 50;
        playBtn = new Button(cx - 154, by, 100, 20, "Play Selected", () =>
        {
            if (selected >= 0)
            {
                game.loadWorld(worlds[selected].id);
                ui.Open(new LoadingScreen(), game);
            }
        });
        deleteBtn = new Button(cx - 50, by, 100, 20, "Delete", () =>
        {
            if (selected >= 0)
            {
                game.deleteWorld(worlds[selected].id);
                worlds = game.listWorlds().OrderByDescending(w => w.lastPlayed).ToList();
                selected = -1;
            }
        });
        buttons.Add(playBtn);
        buttons.Add(deleteBtn);
        buttons.Add(new Button(cx + 54, by, 100, 20, "Create New", () => ui.Open(new WorldCreateScreen(), game)));
        buttons.Add(new Button(cx - 100, by + 24, 200, 20, "Back", () => ui.CloseTop(game)));
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        ui.DrawDirtBg();
        ui.cv.DrawTextCentered("Select World", ui.width / 2, 10, 1);
        playBtn.enabled = selected >= 0;
        deleteBtn.enabled = selected >= 0;
        double listX = Math.Floor(ui.width / 2) - 130;
        if (!loaded)
            ui.cv.DrawTextCentered("Loading...", ui.width / 2, 60, 1, "#a0a0a0");
        else if (worlds.Count == 0)
            ui.cv.DrawTextCentered("No worlds yet — create one!", ui.width / 2, 60, 1, "#a0a0a0");
        scroll = Math.Min(Math.Max(0, scroll), MaxScroll(ui));
        double top = listTop, bottom = ListBottom(ui);
        for (int i = 0; i < worlds.Count; i++)
        {
            var w = worlds[i];
            double y = top + i * 30 - scroll;
            if (y + 28 <= top || y >= bottom) continue;
            bool hover = ui.mouseX >= listX && ui.mouseX < listX + 260 && ui.mouseY >= y && ui.mouseY < y + 28;
            ui.cv.SetFill(i == selected ? "rgba(255,255,255,0.25)" : hover ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.3)");
            ui.cv.FillRect(listX, y, 260, 28);
            ui.cv.DrawText(w.name, listX + 4, y + 4, 1);
            ui.cv.DrawText($"§7{(w.gameMode == GameMode.creative ? "Creative" : "Survival")} • seed {w.seed}", listX + 4, y + 15, 1);
        }
        ui.DrawButtons(this);
    }
    public override bool OnWheel(UIManager ui, GameCore game, double dy)
    {
        scroll = Math.Min(Math.Max(0, scroll + dy * 12), MaxScroll(ui));
        return true;
    }
    public override bool OnMouseDown(UIManager ui, GameCore game, double mx, double my, int btn)
    {
        if (base.OnMouseDown(ui, game, mx, my, btn)) return true;
        double listX = Math.Floor(ui.width / 2) - 130;
        double top = listTop, bottom = ListBottom(ui);
        if (!(my >= top && my < bottom && mx >= listX && mx < listX + 260)) return false;
        for (int i = 0; i < worlds.Count; i++)
        {
            double y = top + i * 30 - scroll;
            if (y + 28 <= top || y >= bottom) continue;
            if (my >= y && my < y + 28)
            {
                if (selected == i)
                {
                    game.loadWorld(worlds[i].id);
                    ui.Open(new LoadingScreen(), game);
                }
                selected = i;
                return true;
            }
        }
        return false;
    }
}

// =============================================================================
public sealed class WorldCreateScreen : Screen
{
    private readonly TextField nameField = new(0, 0, 200, 16, "New World");
    private readonly TextField seedField = new(0, 0, 200, 16, "Leave blank for random");
    private int mode = GameMode.survival;
    private int difficulty = 2;
    private bool creating = false;
    private static readonly string[] DIFF_NAMES = { "Peaceful", "Easy", "Normal", "Hard" };

    public override void InitScreen(UIManager ui, GameCore game)
    {
        double cx = Math.Floor(ui.width / 2);
        nameField.x = cx - 100; nameField.y = 40;
        seedField.x = cx - 100; seedField.y = 76;
        fields.Add(nameField);
        fields.Add(seedField);
        var modeBtn = new Button(cx - 100, 102, 200, 20, "Game Mode: Survival", () => { });
        modeBtn.onClick = () =>
        {
            mode = mode == GameMode.survival ? GameMode.creative : GameMode.survival;
            modeBtn.label = $"Game Mode: {(mode == GameMode.creative ? "Creative" : "Survival")}";
        };
        var diffBtn = new Button(cx - 100, 126, 200, 20, "Difficulty: Normal", () => { });
        diffBtn.onClick = () =>
        {
            difficulty = (difficulty + 1) % 4;
            diffBtn.label = $"Difficulty: {DIFF_NAMES[difficulty]}";
        };
        buttons.Add(modeBtn);
        buttons.Add(diffBtn);
        buttons.Add(new Button(cx - 100, 158, 98, 20, "Create World", () =>
        {
            if (creating) return;
            creating = true;
            game.createWorld(nameField.text.Length == 0 ? "New World" : nameField.text,
                             seedField.text, mode, difficulty);
            ui.Open(new LoadingScreen(), game);
        }));
        buttons.Add(new Button(cx + 2, 158, 98, 20, "Cancel", () => ui.CloseTop(game)));
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        ui.DrawDirtBg();
        ui.cv.DrawTextCentered("Create New World", ui.width / 2, 10, 1);
        ui.cv.DrawText("World Name", nameField.x, nameField.y - 10, 1, "#a0a0a0");
        ui.cv.DrawText("Seed", seedField.x, seedField.y - 10, 1, "#a0a0a0");
        if (creating)
            ui.cv.DrawTextCentered("Generating world…", ui.width / 2, 190, 1, "#ffff55");
        ui.DrawButtons(this);
    }
}

// =============================================================================
/// shown right after world entry while nearby chunks mesh. Closes once the host
/// reports enough live sections near spawn (or after a timeout). The section
/// count is read through SectionCountFn, set by Program.cs.
public sealed class LoadingScreen : Screen
{
    private readonly long openedAt = Environment.TickCount64;
    private const int target = 30;
    /// app hook: returns live meshed-section count near the player
    public static Func<int>? SectionCountFn;

    public LoadingScreen() { closeOnEsc = false; }

    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        int done = Math.Min(target, SectionCountFn?.Invoke() ?? 0);
        bool ready = done >= target;
        double elapsed = (Environment.TickCount64 - openedAt) / 1000.0;
        if ((ready && elapsed > 0.4) || elapsed > 8)
        {
            ui.CloseTop(game);
            return;
        }
        ui.DrawDirtBg();
        ui.cv.DrawTextCentered("Loading world…", ui.width / 2, ui.height / 2 - 24, 1);
        double w = 200.0;
        double x = (ui.width - w) / 2, y = ui.height / 2;
        double f = target > 0 ? Math.Min(1, (double)done / target) : 0;
        ui.cv.SetFill("#1c1c1c"); ui.cv.FillRect(x, y, w, 6);
        ui.cv.SetFill("#80ff20"); ui.cv.FillRect(x, y, Math.Round(w * f), 6);
        ui.cv.DrawTextCentered($"§7Building terrain ({done}/{target})", ui.width / 2, y + 14, 1);
    }
}

// =============================================================================
public sealed class PauseScreen : Screen
{
    public PauseScreen() { pausesGame = true; }

    public override void InitScreen(UIManager ui, GameCore game)
    {
        double cx = Math.Floor(ui.width / 2);
        double y = Math.Floor(ui.height / 2) - 50;
        buttons.Add(new Button(cx - 100, y, 200, 20, "Back to Game", () => ui.CloseTop(game)));
        y += 24;
        buttons.Add(new Button(cx - 100, y, 200, 20, "Options...", () => ui.Open(new SettingsScreen(), game)));
        y += 24;
        buttons.Add(new Button(cx - 100, y, 200, 20, "Save & Quit to Title", () =>
        {
            game.saveAndFlush(synchronous: true);
            game.exitToTitle();
        }));
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        ui.DrawDarkBg(0.5);
        ui.cv.DrawTextCentered("Game Menu", ui.width / 2, Math.Floor(ui.height / 2) - 70, 1);
        ui.DrawButtons(this);
    }
}

// =============================================================================
/// Minimal options screen — render distance + FOV + sensitivity sliders and a
/// GUI-scale toggle, enough for the pause/title "Options..." entry. The full
/// settings tabs (audio/controls/accessibility) are deferred.
public sealed class SettingsScreen : Screen
{
    public override void InitScreen(UIManager ui, GameCore game)
    {
        double cx = Math.Floor(ui.width / 2);
        double y = 50;
        sliders.Add(new Slider(cx - 100, y, 200, 18,
            () => $"Render Distance: {game.settings.renderDistance}",
            () => (double)(game.settings.renderDistance - 4) / 12,
            v => { game.settings.renderDistance = 4 + (int)Math.Round(v * 12); game.applySettings(); }));
        y += 24;
        sliders.Add(new Slider(cx - 100, y, 200, 18,
            () => $"FOV: {game.settings.fov}",
            () => (double)(game.settings.fov - 60) / 50,
            v => { game.settings.fov = 60 + (int)Math.Round(v * 50); game.applySettings(); }));
        y += 24;
        sliders.Add(new Slider(cx - 100, y, 200, 18,
            () => $"Sensitivity: {(int)Math.Round(game.settings.sensitivity * 200)}%",
            () => game.settings.sensitivity,
            v => { game.settings.sensitivity = v; game.applySettings(); }));
        y += 24;
        buttons.Add(new Button(cx - 100, y, 200, 18,
            $"GUI Scale: {(game.settings.guiScale == 0 ? "Auto" : game.settings.guiScale.ToString())}", () => { }));
        var scaleBtn = buttons[^1];
        scaleBtn.onClick = () =>
        {
            game.settings.guiScale = (game.settings.guiScale + 1) % 5;
            scaleBtn.label = $"GUI Scale: {(game.settings.guiScale == 0 ? "Auto" : game.settings.guiScale.ToString())}";
            game.applySettings();
        };
        buttons.Add(new Button(cx - 100, ui.height - 30, 200, 20, "Done", () =>
        {
            game.applySettings();
            ui.CloseTop(game);
        }));
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        if (game.hasWorld()) ui.DrawDarkBg(0.65);
        else ui.DrawDirtBg();
        ui.cv.DrawTextCentered("Options", ui.width / 2, 6, 1);
        ui.DrawButtons(this);
    }
}

// =============================================================================
public sealed class CreditsScreen : Screen
{
    private double scroll = 0.0;
    private long lastT = Environment.TickCount64;
    private static readonly string[] lines =
    {
        "§ePEBBLE", "",
        "§7A complete block-survival game", "§7built from scratch.", "",
        "§fEvery sound synthesized in real time.", "§fEvery chunk carved from noise.", "",
        "§eThank you for playing.", "",
        "§7Inspired by the classic block game.", "§7Pebble is an original fan re-creation.",
    };
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        ui.cv.SetFill("#000000");
        ui.cv.FillRect(0, 0, ui.width, ui.height);
        long nowT = Environment.TickCount64;
        scroll += Math.Min(0.25, (nowT - lastT) / 1000.0) * 15;
        lastT = nowT;
        double y = ui.height - scroll;
        foreach (var line in lines)
        {
            if (y > -10 && y < ui.height + 10) ui.cv.DrawTextCentered(line, ui.width / 2, y, 1);
            y += 14;
        }
        if (y < -20) ui.CloseTop(game);
        ui.cv.DrawTextCentered("§8Press Esc to skip", ui.width / 2, ui.height - 12, 1);
    }
}

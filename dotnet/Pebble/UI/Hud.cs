// HUD — crosshair, hotbar (9 slots + selection + item icons + counts), hearts,
// hunger, armor, air bubbles, XP bar, held-item name. Ported from the PROCEDURAL
// (non-resource-pack) path of Sources/Pebble/HudM.swift — the Windows app loads no
// GUI sheets, so the Swift `packHud == false` branch is what runs. Drawn through
// UICanvas in GUI units; the UI manager applies the GUI scale.
//
// State is pulled live from the engine each frame: game.player (a PebbleCore
// Player) supplies inventory/selectedSlot/health/maxHealth/hunger/xp/armor/air/
// effects/absorption; itemDef() gives display names.

using System;
using PebbleCore;
using static PebbleCore.ItemGlobals;
using static PebbleCore.EnchantsPotionsGlobals;

namespace Pebble.UI;

public sealed class Hud
{
    public bool hideGui = false;

    /// width/height are in GUI units; partial is the 0..1 frame interpolation.
    public void Draw(UICanvas cv, double width, double height, GameCore game, double partial)
    {
        if (hideGui) return;
        var player = game.player;
        if (player == null) return;

        double W = width, H = height;
        double cx = Math.Floor(W / 2);

        // ── crosshair (plain white; vanilla canvas used difference-blend) ────────
        cv.SetFill("rgba(255,255,255,0.85)");
        cv.FillRect(cx - 5, H / 2 - 0.5, 10, 1);
        cv.FillRect(cx - 0.5, H / 2 - 5, 1, 10);
        // attack indicator (cooldown sweep under the crosshair)
        double str = player.attackStrength();
        if (str < 1)
        {
            cv.SetFill("rgba(255,255,255,0.4)");
            cv.FillRect(cx - 8, H / 2 + 8, 16, 2);
            cv.SetFill("#ffffff");
            cv.FillRect(cx - 8, H / 2 + 8, Math.Round(16 * str), 2);
        }

        // ── item in hand (first person) ─────────────────────────────────────────
        // The macOS app has no dedicated 3D viewmodel; we draw the selected held
        // item as a large flat item sprite anchored bottom-right, with the swing
        // animation driven by the player's attack progress (attackAnim 1→0). Only
        // in first person (perspective 0) and not while a screen is open.
        if (game.perspective == 0 && !(game.host?.hasScreen() ?? false))
        {
            var hand = player.inventory[player.selectedSlot];
            if (hand != null)
            {
                // swing arc: a quick raise-and-drop as attackAnim decays 1→0
                double sw = player.attackAnim;            // 1 just after a click, decays to 0
                double arc = Math.Sin(Math.Min(1, sw) * Math.PI);
                double iconSize = 48;
                double baseX = W - iconSize - 14 + arc * 10;
                double baseY = H - iconSize - 26 + (1 - arc) * 14;
                cv.DrawItemIcon(hand.id, hand.data, baseX, baseY, iconSize, iconSize);
            }
        }

        // ── hotbar (182×22 dark bar, 9 cells of 20px, selection outline) ─────────
        double hbX = cx - 91;
        double hbY = H - 23;
        cv.SetFill("rgba(0,0,0,0.55)");
        cv.FillRect(hbX, hbY, 182, 22);
        cv.SetStroke("rgba(255,255,255,0.35)");
        cv.StrokeRect(hbX, hbY, 182, 22);
        for (int i = 0; i < 9; i++)
        {
            double sx = hbX + 1 + i * 20;
            if (i == player.selectedSlot)
            {
                cv.SetStroke("#ffffff");
                cv.StrokeRect(sx - 1, hbY - 1, 23, 24, 2);
            }
            var s = player.inventory[i];
            if (s != null) DrawItemStack(cv, s, sx + 1, hbY + 2);
        }

        // held item name fades in/out above the hotbar
        var held = player.inventory[player.selectedSlot];
        if (held != null && game.heldNameTime > 0)
        {
            string name = held.label ?? itemDef(held.id).displayName;
            cv.globalAlpha = (float)Math.Min(1, game.heldNameTime / 20.0);
            cv.DrawTextCentered(name, cx, hbY - 38, 1);
            cv.globalAlpha = 1f;
        }

        if (player.gameMode != GameMode.creative)
        {
            double healthY = hbY - 10;

            // ── hearts ──────────────────────────────────────────────────────────
            int hearts = (int)Math.Ceiling(player.maxHealth / 2);
            double hp = player.health;
            double shake = player.hurtTime > 0 ? Math.Round(Math.Sin(NowSeconds() * 50) * 1) : 0;
            string kind = player.hasEffect("wither") ? "wither"
                : player.hasEffect("poison") ? "poison"
                : player.freezeTicks > 100 ? "frozen" : "normal";
            for (int i = 0; i < hearts; i++)
            {
                double hx = hbX + i * 8;
                double hy = healthY + (player.hurtTime > 0 && i % 2 == 0 ? shake : 0);
                double v = hp - i * 2;
                DrawHeart(cv, hx, hy, "bg", false);
                if (v >= 2) DrawHeart(cv, hx, hy, kind, false);
                else if (v >= 1) DrawHeart(cv, hx, hy, kind, true);
            }
            // absorption hearts (row above)
            if (player.absorption > 0)
            {
                int n = (int)Math.Ceiling(player.absorption / 2);
                for (int i = 0; i < n; i++)
                    DrawHeart(cv, hbX + (i % 10) * 8, healthY - 10, "absorb", false);
            }

            // ── hunger (right-aligned) ────────────────────────────────────────────
            bool rotten = player.hasEffect("hunger");
            for (int i = 0; i < 10; i++)
            {
                double hx = hbX + 182 - 9 - i * 8;
                double v = player.hunger - i * 2;
                DrawFood(cv, hx, healthY, "bg");
                if (v >= 2) DrawFood(cv, hx, healthY, rotten ? "rotten" : "normal");
                else if (v >= 1) DrawFood(cv, hx, healthY, "half");
            }

            // ── armor (left, row above hearts) ────────────────────────────────────
            double armorVal = player.armorValue();
            if (armorVal > 0)
            {
                for (int i = 0; i < 10; i++)
                {
                    double ax = hbX + i * 8;
                    double v = armorVal - i * 2;
                    if (v >= 2) DrawArmorIcon(cv, ax, healthY - 10, "full");
                    else if (v >= 1) DrawArmorIcon(cv, ax, healthY - 10, "half");
                    else DrawArmorIcon(cv, ax, healthY - 10, "empty");
                }
            }

            // ── air bubbles (right, above hunger) ─────────────────────────────────
            if (player.airSupply < 300)
            {
                int bubbles = (int)Math.Ceiling(player.airSupply / 30.0);
                for (int i = 0; i < 10 && i < bubbles; i++)
                    DrawBubble(cv, hbX + 182 - 9 - i * 8, healthY - 10);
            }

            // ── XP bar ────────────────────────────────────────────────────────────
            double xpY = hbY - 4;
            cv.SetFill("#1c1c1c");
            cv.FillRect(hbX, xpY, 182, 3);
            cv.SetFill("#80ff20");
            cv.FillRect(hbX, xpY, Math.Round(182 * player.xpProgress), 3);
            if (player.xpLevel > 0)
                cv.DrawTextCentered(player.xpLevel.ToString(), cx, xpY - 10, 1, "#80ff20");
        }

        // ── status effect icons (top right) ─────────────────────────────────────
        double efX = W - 26;
        foreach (var e in player.effects)
        {
            var def = effectDef(e.id);
            cv.SetFill(def.beneficial ? "rgba(30,30,80,0.7)" : "rgba(80,30,30,0.7)");
            cv.FillRect(efX, 4, 22, 22);
            cv.SetFill("#" + def.color.ToString("x6"));
            cv.FillRect(efX + 4, 8, 14, 10);
            int secs = e.duration / 20;
            string t = e.duration < 0 ? "inf" : $"{secs / 60}:{(secs % 60):D2}";
            cv.DrawTextCentered(t, efX + 11, 19, 1, "#ffffff", false);
            efX -= 24;
        }
    }

    // ── item stack (icon + count + durability), from UIManagerM.drawItemStack ──
    private static void DrawItemStack(UICanvas cv, ItemStack s, double x, double y)
    {
        cv.DrawItemIcon(s.id, s.data, x + 1, y + 1, 16, 16);
        // durability bar
        var def = itemDef(s.id);
        int maxD = def.tool?.durability ?? def.armor?.durability ?? 0;
        if (maxD > 0 && s.damage > 0)
        {
            double f = 1 - (double)s.damage / maxD;
            cv.SetFill("#000000");
            cv.FillRect(x + 2, y + 15, 14, 2);
            cv.SetFill(f > 0.5 ? "#40c040" : f > 0.25 ? "#e8d83c" : "#e84040");
            cv.FillRect(x + 2, y + 15, Math.Max(1, Math.Round(14 * f)), 1);
        }
        if (s.count > 1)
        {
            string cnt = s.count.ToString();
            cv.DrawText(cnt, x + 18 - Font.TextWidth(cnt) - 1, y + 10, 1);
        }
    }

    // ── pixel-art icon helpers (procedural fallbacks from HudM.swift) ──────────
    private static void DrawHeart(UICanvas cv, double x, double y, string kind, bool half)
    {
        (string main, string dark) = kind switch
        {
            "bg" => ("#3f1414", "#1f0a0a"),
            "normal" => ("#ff2020", "#a80000"),
            "poison" => ("#94a061", "#586038"),
            "wither" => ("#3a3a3a", "#1c1c1c"),
            "frozen" => ("#60c8e8", "#3088a8"),
            "absorb" => ("#e8c83c", "#a8862c"),
            _ => ("#ff2020", "#a80000"),
        };
        string[] rows = { ".##.##.", "#######", "#######", ".#####.", "..###..", "...#..." };
        for (int ry = 0; ry < rows.Length; ry++)
        {
            string row = rows[ry];
            for (int rx = 0; rx < row.Length; rx++)
            {
                if (row[rx] != '#') continue;
                if (half && rx >= 4) continue;
                cv.SetFill(ry < 3 ? main : dark);
                cv.FillRect(x + rx, y + ry, 1, 1);
            }
        }
        if (kind != "bg")
        {
            cv.SetFill("#ffffff");
            cv.FillRect(x + 1, y + 1, 1, 1);
        }
    }

    private static void DrawFood(UICanvas cv, double x, double y, string kind)
    {
        string main = kind == "bg" ? "#2a1c10" : kind == "rotten" ? "#7a8a4a" : "#c87830";
        string dark = kind == "bg" ? "#180f08" : kind == "rotten" ? "#586038" : "#8a4a1c";
        string[] rows = { "..###..", ".#####.", ".#####.", ".#####.", "..###..", "...#..." };
        for (int ry = 0; ry < rows.Length; ry++)
        {
            string row = rows[ry];
            for (int rx = 0; rx < row.Length; rx++)
            {
                if (row[rx] != '#') continue;
                if (kind == "half" && rx >= 4) continue;
                cv.SetFill(ry > 2 ? dark : main);
                cv.FillRect(x + rx, y + ry, 1, 1);
            }
        }
    }

    private static void DrawArmorIcon(UICanvas cv, double x, double y, string kind)
    {
        string main = kind == "empty" ? "#3a3a3a" : "#c8c8c8";
        string[] rows = { "##.##", "#####", "#####", ".###." };
        for (int ry = 0; ry < rows.Length; ry++)
        {
            string row = rows[ry];
            for (int rx = 0; rx < row.Length; rx++)
            {
                if (row[rx] != '#') continue;
                cv.SetFill(kind == "half" && rx >= 3 ? "#3a3a3a" : main);
                cv.FillRect(x + rx + 1, y + ry + 1, 1, 1);
            }
        }
    }

    private static void DrawBubble(UICanvas cv, double x, double y)
    {
        string[] rows = { ".###.", "#...#", "#...#", ".###." };
        for (int ry = 0; ry < rows.Length; ry++)
        {
            string row = rows[ry];
            for (int rx = 0; rx < row.Length; rx++)
            {
                if (row[rx] != '#') continue;
                cv.SetFill("#6ab8e8");
                cv.FillRect(x + rx + 1, y + ry + 1, 1, 1);
            }
        }
    }

    private static double NowSeconds() => Environment.TickCount64 / 1000.0;
}

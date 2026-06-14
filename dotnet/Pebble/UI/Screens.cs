// Gameplay screens — the survival inventory (2×2 craft + armor + offhand), the
// 3×3 crafting table, and a generic chest container. Ported from the PROCEDURAL
// path of Sources/Pebble/ScreensM.swift (no GUI sheets on the Windows app).
//
// Container screens other than these (furnace/brewing/enchanting/anvil/
// grindstone/stonecutter/smithing/beacon/trading/creative/sign/death/chat) are
// DEFERRED — HostBridge.openScreen routes them to a small ContainerStub so the
// game never crashes when one is requested; see HostBridge.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using PebbleCore;
using static PebbleCore.ItemGlobals;
using static PebbleCore.CraftingGlobals;

namespace Pebble.UI;

// =============================================================================
// Base container screen with player inventory
// =============================================================================
public class ContainerScreen : Screen
{
    public double panelX = 0.0;
    public double panelY = 0.0;
    public double panelW = 176.0;
    public double panelH = 166.0;
    public string title = "";
    public double titleX = 8.0;
    public double titleY = 6.0;
    public bool showInvLabel = true;
    public List<SlotDef> playerSlots = new();
    public List<SlotDef> containerSlots = new();
    public virtual double playerInvY => panelH - 83;

    public override void InitScreen(UIManager ui, GameCore game)
    {
        panelX = Math.Floor((ui.width - panelW) / 2);
        panelY = Math.Floor((ui.height - panelH) / 2);
        playerSlots = UISlotGlobals.PlayerInvSlots(game.player, panelX + 7, panelY + playerInvY);
        containerSlots = new List<SlotDef>();
        BuildSlots(ui, game);
        slots = containerSlots.Concat(playerSlots).ToList();
    }
    public virtual void BuildSlots(UIManager ui, GameCore game) { }

    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        ui.DrawDarkBg(0.55);
        ui.DrawPanel(panelX, panelY, panelW, panelH);
        ui.cv.DrawText(title, panelX + titleX, panelY + titleY, 1, "#3f3f3f", shadow: false);
        if (showInvLabel)
            ui.cv.DrawText("Inventory", panelX + 8, panelY + playerInvY - 10, 1, "#3f3f3f", shadow: false);
        DrawExtra(ui, game);
        ui.DrawSlots(this, slotBg: true);
        ui.DrawButtons(this);
    }
    public virtual void DrawExtra(UIManager ui, GameCore game) { }

    public override void QuickMove(GameCore game, SlotDef slot)
    {
        var s = slot.get();
        if (s == null) return;
        bool fromContainer = containerSlots.Any(c => ReferenceEquals(c, slot));
        var targets = fromContainer ? playerSlots : containerSlots.Where(c => !c.output).ToList();
        if (UISlotGlobals.QuickMoveInto(s, targets))
        {
            if (s.count <= 0) slot.set(null);
        }
        if (s.count <= 0) slot.set(null);
        slot.onChange?.Invoke();
    }
}

// =============================================================================
// Inventory (survival) — 2×2 crafting + armor + offhand
// =============================================================================
public sealed class InventoryScreen : ContainerScreen
{
    // grid stores null in empty cells (the engine craft helpers tolerate nulls);
    // declared non-nullable element type so it matches matchCrafting/consume sigs.
    private readonly List<ItemStack> craftGrid = new() { null!, null!, null!, null! };
    private ItemStack? craftResult;

    public InventoryScreen()
    {
        title = "Crafting";
        titleX = 97;
        titleY = 8;
        showInvLabel = false;
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        var p = game.player;
        double px = panelX, py = panelY;
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            containerSlots.Add(new SlotDef(
                px + 7, py + 7 + idx * 18,
                () => p.armor[idx],
                v => p.armor[idx] = v,
                canPlace: s => itemDef(s.id).armor?.slot == idx));
        }
        containerSlots.Add(new SlotDef(
            px + 76, py + 61,
            () => p.offHand,
            v => p.offHand = v));
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            containerSlots.Add(new SlotDef(
                px + 97 + (idx % 2) * 18, py + 18 + (idx / 2) * 18,
                () => craftGrid[idx],
                v => { craftGrid[idx] = v; UpdateResult(); },
                onChange: UpdateResult));
        }
        containerSlots.Add(new SlotDef(
            px + 153, py + 27,
            () => craftResult,
            _ => { },
            output: true,
            onTake: _ =>
            {
                ConsumeGrid(game);
                UpdateResult();
                game.advance("craft_any");
            }));
        UpdateResult();
    }
    private void ConsumeGrid(GameCore game)
    {
        // engine helper mutates the backing list in place and returns container
        // items (buckets/bottles) to hand back to the player
        var returns = consumeCraftingGrid(craftGrid);
        foreach (var r in returns) game.player.give(r);
    }
    private void UpdateResult()
    {
        craftResult = matchCrafting(craftGrid, 2, 2)?.@out;
    }
    public override void DrawExtra(UIManager ui, GameCore game)
    {
        var cv = ui.cv;
        cv.DrawText("▶", panelX + 138, panelY + 31, 1, "#3f3f3f", shadow: false);
        cv.SetFill("#1c1c1c");
        cv.FillRect(panelX + 26, panelY + 8, 49, 70);
        // simple front-facing player figure centered in the preview window
        double cx = panelX + 50, by = panelY + 10;
        double sway = Math.Sin(Environment.TickCount64 / 600.0) * 1.5;
        var p = game.player;
        void Px(double x, double y, double w, double h, string c)
        {
            cv.SetFill(c);
            cv.FillRect(Math.Round(cx + x + (y < 20 ? sway : 0)), by + 6 + y, w, h);
        }
        Px(-6, 0, 12, 12, "#b88a64");
        Px(-4, 3, 3, 3, "#ffffff"); Px(1, 3, 3, 3, "#ffffff");
        Px(-3, 4, 2, 2, "#3a6ea8"); Px(2, 4, 2, 2, "#3a6ea8");
        Px(-6, 0, 12, 4, "#5a3c28");
        Px(-6, 13, 12, 18, p.armor[1] != null ? "#c8c8d0" : "#2ea3a3");
        Px(-11, 13, 5, 16, "#b88a64");
        Px(6, 13, 5, 16, "#b88a64");
        Px(-6, 31, 5, 18, p.armor[2] != null ? "#a8a8b0" : "#3a3a8c");
        Px(1, 31, 5, 18, p.armor[2] != null ? "#a8a8b0" : "#3a3a8c");
        Px(-6, 49, 5, 4, p.armor[3] != null ? "#909098" : "#6a6a6a");
        Px(1, 49, 5, 4, p.armor[3] != null ? "#909098" : "#6a6a6a");
        if (p.armor[0] != null) Px(-6, 0, 12, 5, "#c8c8d0");
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        for (int i = 0; i < 4; i++)
        {
            if (craftGrid[i] is { } s)
            {
                game.player.give(s);
                craftGrid[i] = null;
            }
        }
    }
}

// =============================================================================
// Crafting table 3×3
// =============================================================================
public sealed class CraftingScreen : ContainerScreen
{
    private readonly List<ItemStack> craftGrid = new(new ItemStack[9]);
    private ItemStack? craftResult;

    public CraftingScreen()
    {
        title = "Crafting";
        titleX = 29;
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        for (int i = 0; i < 9; i++)
        {
            int idx = i;
            containerSlots.Add(new SlotDef(
                px + 29 + (idx % 3) * 18, py + 16 + (idx / 3) * 18,
                () => craftGrid[idx],
                v => { craftGrid[idx] = v; UpdateResult(); },
                onChange: UpdateResult));
        }
        containerSlots.Add(new SlotDef(
            px + 123, py + 34,
            () => craftResult,
            _ => { },
            output: true,
            onTake: _ =>
            {
                var returns = consumeCraftingGrid(craftGrid);
                foreach (var r in returns) game.player.give(r);
                UpdateResult();
            }));
        UpdateResult();
    }
    private void UpdateResult()
    {
        craftResult = matchCrafting(craftGrid, 3, 3)?.@out;
    }
    public override void DrawExtra(UIManager ui, GameCore game)
    {
        ui.cv.DrawText("▶", panelX + 95, panelY + 38, 2, "#3f3f3f", shadow: false);
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        for (int i = 0; i < 9; i++)
        {
            if (craftGrid[i] is { } s)
            {
                game.player.give(s);
                craftGrid[i] = null;
            }
        }
    }
}

// =============================================================================
// Generic chest-style container (used for chest / ender_chest / vehicle chests)
// =============================================================================
public sealed class ChestScreen : ContainerScreen
{
    private readonly Func<int, ItemStack?> getItem;
    private readonly Action<int, ItemStack?> setItem;
    private readonly int count;

    public ChestScreen(Func<int, ItemStack?> getItem, Action<int, ItemStack?> setItem, int count, string title)
    {
        this.getItem = getItem;
        this.setItem = setItem;
        this.count = count;
        this.title = title;
        panelH = 114 + ((count + 8) / 9) * 18;
    }
    public override double playerInvY => panelH - 84;

    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            containerSlots.Add(new SlotDef(
                px + 7 + (idx % 9) * 18,
                py + 17 + (idx / 9) * 18,
                () => getItem(idx),
                v => setItem(idx, v)));
        }
    }
}

// =============================================================================
// Container stub — a placeholder panel for the deferred container screens, so
// the engine can request "furnace"/"anvil"/etc. without crashing. Shows the
// player inventory (fully interactive) plus a labelled empty panel.
// =============================================================================
public sealed class ContainerStub : ContainerScreen
{
    public ContainerStub(string title) { this.title = title; }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        ui.cv.DrawText("", 0, 0, 1);   // no container slots
    }
    public override void DrawExtra(UIManager ui, GameCore game)
    {
        ui.cv.DrawTextCentered("§7(screen not yet ported)", panelX + panelW / 2, panelY + 30, 1, "#7a7a7a", shadow: false);
    }
}

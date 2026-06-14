// Container/machine screens — furnace, brewing, anvil, grindstone, stonecutter,
// smithing, beacon, enchanting. Ported from the PROCEDURAL path of
// Sources/Pebble/ScreensM.swift (the Windows app loads no GUI sheets, so only the
// `!textured` branches are kept; the pack-sheet blit code is dropped).
//
// Each extends the ContainerScreen base in Screens.cs (player inventory rows,
// standard slot click/drag/quick-move via UIManager's slot engine, tooltips).
// They read/write BlockEntityData / engine functions exactly like the macOS
// screens, and HostBridge.openScreen wires every `kind` to the matching screen.

using System;
using System.Collections.Generic;
using System.Linq;
using PebbleCore;
using static PebbleCore.ItemGlobals;
using static PebbleCore.CraftingGlobals;
using static PebbleCore.BlockEntitySystems;   // fuelTime
using static PebbleCore.EnchantsPotionsGlobals; // isBrewIngredient
using static PebbleCore.MiscGlobals;           // spawnXP
using static PebbleCore.Reg;                   // B (ResolvedBlockIDs)
using static PebbleCore.BlockCaches;           // cell
using static PebbleCore.RecipeGlobals;         // stonecuttingRecipes

namespace Pebble.UI;

// =============================================================================
// Furnace / blast furnace / smoker
// =============================================================================
public sealed class FurnaceScreen : ContainerScreen
{
    private readonly BlockEntityData be;

    public FurnaceScreen(BlockEntityData be)
    {
        this.be = be;
        title = be.kind == "blast" ? "Blast Furnace" : be.kind == "smoker" ? "Smoker" : "Furnace";
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        var b = be;
        // input
        containerSlots.Add(new SlotDef(
            px + 55, py + 16,
            () => b.items[0], v => b.items[0] = v));
        // fuel
        containerSlots.Add(new SlotDef(
            px + 55, py + 52,
            () => b.items[1], v => b.items[1] = v,
            canPlace: s => fuelTime(s) > 0));
        // output
        containerSlots.Add(new SlotDef(
            px + 115, py + 34,
            () => b.items[2],
            _ => { },
            output: true,
            onTake: _ =>
            {
                b.items[2] = null;
                int xp = (int)(b.xpBank ?? 0);
                if (xp > 0)
                {
                    spawnXP(game.world, game.player.x, game.player.y, game.player.z, xp);
                    b.xpBank = 0;
                }
            }));
    }
    public override void DrawExtra(UIManager ui, GameCore game)
    {
        var cv = ui.cv;
        double px = panelX, py = panelY;
        int burnTime = be.burnTime ?? 0;
        double burnF = (be.burnTotal ?? 0) > 0 ? (double)burnTime / be.burnTotal!.Value : 0;
        double prog = (be.cookTotal ?? 0) > 0 ? (double)(be.cookTime ?? 0) / be.cookTotal!.Value : 0;
        // fuel flame (fills bottom-up)
        if (burnTime > 0)
        {
            double h = Math.Ceiling(burnF * 13);
            cv.SetFill("#ff9a2c");
            cv.FillRect(px + 57, py + 36 + (13 - h), 13, h);
        }
        else
        {
            cv.SetFill("#3a3a3a");
            cv.FillRect(px + 57, py + 36, 13, 13);
        }
        // progress arrow (fills left-to-right)
        cv.SetFill("#5a5a5a");
        cv.FillRect(px + 79, py + 38, 24, 10);
        cv.SetFill("#ffffff");
        cv.FillRect(px + 79, py + 38, Math.Round(24 * prog), 10);
    }
}

// =============================================================================
// Brewing stand
// =============================================================================
public sealed class BrewingScreen : ContainerScreen
{
    private readonly BlockEntityData be;

    public BrewingScreen(BlockEntityData be)
    {
        this.be = be;
        title = "Brewing Stand";
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        var b = be;
        var bottlePositions = new (double, double)[] { (55, 50), (79, 57), (103, 50) };
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            containerSlots.Add(new SlotDef(
                px + bottlePositions[i].Item1, py + bottlePositions[i].Item2,
                () => b.items[idx], v => b.items[idx] = v,
                canPlace: s => new[] { "potion", "splash_potion", "lingering_potion", "glass_bottle" }
                    .Contains(itemDef(s.id).name)));
        }
        // ingredient
        containerSlots.Add(new SlotDef(
            px + 78, py + 16,
            () => b.items[3], v => b.items[3] = v,
            canPlace: s => isBrewIngredient(itemDef(s.id).name)));
        // blaze-powder fuel
        containerSlots.Add(new SlotDef(
            px + 16, py + 16,
            () => b.items[4], v => b.items[4] = v,
            canPlace: s => itemDef(s.id).name == "blaze_powder"));
    }
    public override void DrawExtra(UIManager ui, GameCore game)
    {
        var cv = ui.cv;
        double px = panelX, py = panelY;
        double fuelW = Math.Round(18 * (double)(be.fuel ?? 0) / 20);
        bool brewing = (be.brewTime ?? 0) > 0;
        double f = brewing ? (double)be.brewTime!.Value / 400 : 0;
        // fuel strip
        cv.SetFill("#3a3a3a");
        cv.FillRect(px + 36, py + 18, 18, 4);
        cv.SetFill("#e89a3c");
        cv.FillRect(px + 36, py + 18, fuelW, 4);
        // brew progress arrow (fills downward)
        if (brewing)
        {
            cv.SetFill("#e8e8e8");
            cv.FillRect(px + 98, py + 18, 2, Math.Round(26 * f));
        }
        cv.DrawText("◡", px + 76, py + 36, 1, "#3f3f3f", shadow: false);
    }
}

// =============================================================================
// Anvil — two inputs + output, rename field, cost display
// =============================================================================
public sealed class AnvilScreen : ContainerScreen
{
    private ItemStack? left;
    private ItemStack? right;
    private ItemStack? result;
    private int cost;
    private readonly TextField nameField = new TextField(0, 0, 96, 14);
    private int posX, posY, posZ, posDamage;

    public AnvilScreen(int x, int y, int z, int damage)
    {
        posX = x; posY = y; posZ = z; posDamage = damage;
        title = "Repair & Name";
    }
    public override void InitScreen(UIManager ui, GameCore game)
    {
        base.InitScreen(ui, game);
        nameField.x = panelX + 60;
        nameField.y = panelY + 22;
        fields.Add(nameField);
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        containerSlots.Add(new SlotDef(
            px + 26, py + 46,
            () => left,
            v => { left = v; Refresh(); },
            onChange: Refresh));
        containerSlots.Add(new SlotDef(
            px + 75, py + 46,
            () => right,
            v => { right = v; Refresh(); },
            onChange: Refresh));
        containerSlots.Add(new SlotDef(
            px + 133, py + 46,
            () => result,
            _ => { },
            output: true,
            onTake: _ =>
            {
                game.player.takeLevels(cost);
                left = null;
                if (right is { } r)
                {
                    int? units = result?.data.repairUnits;
                    if (units is { } u && r.count > u) r.count -= u;
                    else right = null;
                }
                if (result != null)
                {
                    var d = result.data;
                    d.repairUnits = null;
                    result.data = d;
                }
                result = null;
                cost = 0;
                // anvil degrade chance (deterministic enough for the UI thread)
                if (new Random().NextDouble() < 0.12)
                {
                    int c = game.world.getBlock(posX, posY, posZ);
                    if (posDamage >= 2)
                    {
                        game.world.setBlock(posX, posY, posZ, 0);
                        game.world.hooks.playSound("block.anvil.destroy", posX + 0.5, posY + 0.5, posZ + 0.5, 1, 1);
                        ui.CloseTop(game);
                    }
                    else
                    {
                        ushort next = posDamage == 0 ? B.chipped_anvil : B.damaged_anvil;
                        game.world.setBlock(posX, posY, posZ, (int)cell(next, c & 15));
                        posDamage += 1;
                    }
                }
                game.world.hooks.playSound("block.anvil.use", posX + 0.5, posY + 0.5, posZ + 0.5, 1, 1);
            }));
    }
    private void Refresh()
    {
        var r = anvilCombine(left, right, nameField.text.Length == 0 ? null : nameField.text);
        result = r?.@out;
        cost = r?.cost ?? 0;
    }
    public override bool OnChar(UIManager ui, GameCore game, string ch)
    {
        bool r = base.OnChar(ui, game, ch);
        if (r) Refresh();
        return r;
    }
    public override bool OnKey(UIManager ui, GameCore game, string key)
    {
        bool r = base.OnKey(ui, game, key);
        if (r) Refresh();
        return r;
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        base.Draw(ui, game, partial);
        if (cost > 0)
        {
            bool ok = game.player.xpLevel >= cost && cost < 40;
            ui.cv.DrawText(cost >= 40 ? "Too Expensive!" : $"Enchantment Cost: {cost}",
                panelX + 8, panelY + 71, 1, ok ? "#80ff20" : "#ff5050");
        }
        ui.cv.DrawText("+", panelX + 56, panelY + 50, 1, "#3f3f3f", shadow: false);
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        if (left is { } l) game.player.give(l);
        if (right is { } r) game.player.give(r);
    }
}

// =============================================================================
// Grindstone — two inputs + output (disenchant / repair)
// =============================================================================
public sealed class GrindstoneScreen : ContainerScreen
{
    private ItemStack? top;
    private ItemStack? bottom;
    private ItemStack? result;
    private int xp;

    public GrindstoneScreen() { title = "Repair & Disenchant"; }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        containerSlots.Add(new SlotDef(
            px + 48, py + 18,
            () => top,
            v => { top = v; Refresh(); },
            onChange: Refresh));
        containerSlots.Add(new SlotDef(
            px + 48, py + 39,
            () => bottom,
            v => { bottom = v; Refresh(); },
            onChange: Refresh));
        containerSlots.Add(new SlotDef(
            px + 128, py + 33,
            () => result,
            _ => { },
            output: true,
            onTake: _ =>
            {
                top = null;
                bottom = null;
                result = null;
                if (xp > 0)
                    spawnXP(game.world, game.player.x, game.player.y, game.player.z, xp);
                xp = 0;
                game.playUISound("block.grindstone.use");
            }));
    }
    private void Refresh()
    {
        var r = grindstoneResult(top, bottom);
        result = r?.@out;
        xp = r?.xp ?? 0;
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        if (top is { } t) game.player.give(t);
        if (bottom is { } b) game.player.give(b);
    }
}

// =============================================================================
// Stonecutter — input + recipe button list + output
// =============================================================================
public sealed class StonecutterScreen : ContainerScreen
{
    private ItemStack? input;
    private int selected = -1;
    private List<(string output, int count)> options = new();

    public StonecutterScreen() { title = "Stonecutter"; }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        containerSlots.Add(new SlotDef(
            px + 19, py + 32,
            () => input,
            v => { input = v; Refresh(); },
            onChange: Refresh));
        containerSlots.Add(new SlotDef(
            px + 142, py + 32,
            () =>
            {
                if (selected < 0 || input == null || selected >= options.Count) return null;
                var o = options[selected];
                return new ItemStack(iid(o.output), o.count);
            },
            _ => { },
            output: true,
            onTake: _ =>
            {
                if (input is { } inp)
                {
                    int sel = selected;
                    inp.count -= 1;
                    if (inp.count <= 0) input = null;
                    Refresh();
                    // keep the recipe selected while input remains (Refresh clears
                    // it) so repeated/shift takes keep cutting
                    if (input != null && sel >= 0 && sel < options.Count) selected = sel;
                }
                game.playUISound("ui.stonecutter.take_result");
            }));
    }
    private void Refresh()
    {
        options = new();
        selected = -1;
        if (input == null) return;
        string name = itemDef(input.id).name;
        foreach (var r in stonecuttingRecipes)
            if (r.input == name) options.Add((r.output, r.count));
    }
    private double GridX => panelX + 48;
    private double GridY => panelY + 14;
    private const double CellW = 18;

    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        base.Draw(ui, game, partial);
        var cv = ui.cv;
        for (int i = 0; i < Math.Min(12, options.Count); i++)
        {
            double ox = GridX + (i % 4) * CellW, oy = GridY + (i / 4) * 18;
            cv.SetFill(i == selected ? "#8a8aff" : "#5a5a5a");
            cv.FillRect(ox, oy, CellW, 18);
            cv.DrawItemIcon(iid(options[i].output), null, ox + (CellW - 16) / 2, oy + 1, 16, 16);
        }
    }
    public override bool OnMouseDown(UIManager ui, GameCore game, double mx, double my, int btn)
    {
        double px = GridX, py = GridY;
        if (mx >= px && mx < px + CellW * 4 && my >= py && my < py + 54)
        {
            int i = (int)((mx - px) / CellW) + (int)((my - py) / 18) * 4;
            if (i >= 0 && i < options.Count)
            {
                selected = i;
                game.playUISound("ui.stonecutter.select_recipe");
                return true;
            }
        }
        return base.OnMouseDown(ui, game, mx, my, btn);
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        if (input is { } inp) game.player.give(inp);
    }
}

// =============================================================================
// Smithing table — template + base + addition + output
// =============================================================================
public sealed class SmithingScreen : ContainerScreen
{
    private ItemStack? template;
    private ItemStack? @base;
    private ItemStack? addition;
    private ItemStack? result;

    public SmithingScreen()
    {
        title = "Upgrade Gear";
        titleX = 44;
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        SlotDef Mk(double x, Func<ItemStack?> getF, Action<ItemStack?> setF)
            => new SlotDef(
                px + x, py + 47,
                getF,
                v => { setF(v); Refresh(); },
                onChange: Refresh);
        containerSlots.Add(Mk(7, () => template, v => template = v));
        containerSlots.Add(Mk(25, () => @base, v => @base = v));
        containerSlots.Add(Mk(43, () => addition, v => addition = v));
        containerSlots.Add(new SlotDef(
            px + 97, py + 47,
            () => result,
            _ => { },
            output: true,
            onTake: _ =>
            {
                ConsumeOne(ref template);
                ConsumeOne(ref @base);
                ConsumeOne(ref addition);
                result = null;
                Refresh();
                game.playUISound("block.smithing_table.use");
            }));
    }
    private static void ConsumeOne(ref ItemStack? s)
    {
        if (s is { } stack)
        {
            stack.count -= 1;
            if (stack.count <= 0) s = null;
        }
    }
    private void Refresh()
    {
        result = matchSmithing(template, @base, addition);
    }
    public override void DrawExtra(UIManager ui, GameCore game)
    {
        ui.cv.DrawText("▶", panelX + 74, panelY + 48, 1, "#3f3f3f", shadow: false);
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        foreach (var s in new[] { template, @base, addition })
            if (s is { } st) game.player.give(st);
        template = null;
        @base = null;
        addition = null;
    }
}

// =============================================================================
// Beacon — payment slot + primary/secondary power selection from pyramid levels
// =============================================================================
public sealed class BeaconScreen : Screen
{
    private ItemStack? payment;
    private string? pendingPrimary;
    private double panelX, panelY;
    private readonly BlockEntityData be;

    public BeaconScreen(BlockEntityData be) { this.be = be; }

    public override void InitScreen(UIManager ui, GameCore game)
    {
        panelX = Math.Floor((ui.width - 200) / 2);
        panelY = Math.Floor((ui.height - 120) / 2);
        slots = new List<SlotDef>
        {
            new SlotDef(
                panelX + 160, panelY + 90,
                () => payment,
                v => payment = v,
                canPlace: s => new[] { "iron_ingot", "gold_ingot", "diamond", "emerald", "netherite_ingot" }
                    .Contains(itemDef(s.id).name)),
        };
        pendingPrimary = be.primary;
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        ui.DrawDarkBg(0.55);
        ui.DrawPanel(panelX, panelY, 200, 120);
        var cv = ui.cv;
        cv.DrawText($"Beacon (Pyramid level {be.levels ?? 0})", panelX + 8, panelY + 6, 1, "#3f3f3f", shadow: false);
        var powers = new (string id, string name, int level)[]
        {
            ("speed", "Speed", 1), ("haste", "Haste", 1),
            ("resistance", "Resistance", 2), ("jump_boost", "Jump Boost", 2),
            ("strength", "Strength", 3),
        };
        for (int i = 0; i < powers.Length; i++)
        {
            var p = powers[i];
            double bx = panelX + 10 + (i % 2) * 92;
            double by = panelY + 20 + (i / 2) * 22;
            bool unlocked = (be.levels ?? 0) >= p.level;
            bool sel = pendingPrimary == p.id;
            cv.SetFill(!unlocked ? "#3a3a3a" : sel ? "#6a8aff" : "#5a5a5a");
            cv.FillRect(bx, by, 88, 18);
            cv.DrawText(p.name, bx + 5, by + 5, 1, unlocked ? "#ffffff" : "#808080");
        }
        cv.DrawText("Pay:", panelX + 132, panelY + 95, 1, "#3f3f3f", shadow: false);
        ui.DrawSlots(this);
        bool can = pendingPrimary != null && payment != null && (be.levels ?? 0) > 0;
        cv.SetFill(can ? "#4a8a4a" : "#3a3a3a");
        cv.FillRect(panelX + 10, panelY + 92, 60, 16);
        cv.DrawTextCentered("Confirm", panelX + 40, panelY + 96, 1, can ? "#ffffff" : "#808080");
    }
    public override bool OnMouseDown(UIManager ui, GameCore game, double mx, double my, int btn)
    {
        string[] powers = { "speed", "haste", "resistance", "jump_boost", "strength" };
        int[] minLvls = { 1, 1, 2, 2, 3 };
        for (int i = 0; i < powers.Length; i++)
        {
            double bx = panelX + 10 + (i % 2) * 92;
            double by = panelY + 20 + (i / 2) * 22;
            if (mx >= bx && mx < bx + 88 && my >= by && my < by + 18 && (be.levels ?? 0) >= minLvls[i])
            {
                pendingPrimary = powers[i];
                return true;
            }
        }
        if (mx >= panelX + 10 && mx < panelX + 70 && my >= panelY + 92 && my < panelY + 108)
        {
            if (pendingPrimary is { } primary && payment is { } pay && (be.levels ?? 0) > 0)
            {
                be.primary = primary;
                be.secondary = (be.levels ?? 0) >= 4 ? primary : null;
                pay.count -= 1;
                if (pay.count <= 0) payment = null;
                game.playUISound("block.beacon.power_select");
                ui.CloseTop(game);
            }
            return true;
        }
        return base.OnMouseDown(ui, game, mx, my, btn);
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        if (payment is { } pay) game.player.give(pay);
    }
}

// =============================================================================
// Enchanting table — item + lapis slots, 3 enchant options
// =============================================================================
public sealed class EnchantingScreen : ContainerScreen
{
    private ItemStack? item;
    private ItemStack? lapis;
    private List<EnchantOption> options = new();
    private int seed = new Random().Next(0, 1_000_000_000);
    private int bookshelves;
    private readonly int posX, posY, posZ;

    public EnchantingScreen(int x, int y, int z)
    {
        posX = x; posY = y; posZ = z;
        title = "Enchant";
    }
    public override void InitScreen(UIManager ui, GameCore game)
    {
        base.InitScreen(ui, game);
        int n = 0;
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (Math.Abs(dx) < 2 && Math.Abs(dz) < 2) continue;
                foreach (int dy in new[] { 0, 1 })
                    if ((game.world.getBlock(posX + dx, posY + dy, posZ + dz) >> 4) == B.bookshelf) n++;
            }
        bookshelves = Math.Min(15, n);
    }
    public override void BuildSlots(UIManager ui, GameCore game)
    {
        double px = panelX, py = panelY;
        containerSlots.Add(new SlotDef(
            px + 14, py + 46,
            () => item,
            v => { item = v; Refresh(); },
            onChange: Refresh));
        containerSlots.Add(new SlotDef(
            px + 34, py + 46,
            () => lapis,
            v => { lapis = v; Refresh(); },
            canPlace: s => itemDef(s.id).name == "lapis_lazuli",
            onChange: Refresh));
    }
    private void Refresh()
    {
        options = enchantingOptions(item, bookshelves, seed);
    }
    public override void Draw(UIManager ui, GameCore game, double partial)
    {
        base.Draw(ui, game, partial);
        var cv = ui.cv;
        double px = panelX, py = panelY;
        int lapisCount = lapis?.count ?? 0;
        var roman = new[] { "I", "II", "III", "IV", "V" };
        for (int i = 0; i < 3; i++)
        {
            EnchantOption? opt = i < options.Count ? options[i] : (EnchantOption?)null;
            double bx = px + 60, by = py + 14 + i * 19, bw = 108.0, bh = 18.0;
            bool affordable = opt != null && game.player.xpLevel >= opt.Value.level && lapisCount >= opt.Value.lapis;
            bool hover = ui.mouseX >= bx && ui.mouseX < bx + bw && ui.mouseY >= by && ui.mouseY < by + bh;
            cv.SetFill(opt == null ? "#3a3a3a" : affordable ? (hover ? "#5a4a8a" : "#4a3a6a") : "#3a3a3a");
            cv.FillRect(bx, by, bw, bh);
            if (opt is { } o)
            {
                cv.DrawText(o.level.ToString(), bx + bw - 12, by + 9, 1, affordable ? "#80ff20" : "#407f10");
                if (o.preview is { } e)
                {
                    string label = e.id.Replace("_", " ") + " " + roman[Math.Min(4, e.lvl - 1)] + "…";
                    cv.DrawText(label, bx + 4, by + 5, 1, affordable ? "#d8c8f8" : "#707070");
                }
                cv.DrawText(new string('•', o.lapis), bx + 4, by + 12, 1, "#3c5ac8");
            }
        }
        cv.DrawText($"Bookshelves: {bookshelves}", px + 60, py + 73, 1, "#3f3f3f", shadow: false);
    }
    public override bool OnMouseDown(UIManager ui, GameCore game, double mx, double my, int btn)
    {
        double px = panelX, py = panelY;
        for (int i = 0; i < 3; i++)
        {
            double bx = px + 60, by = py + 14 + i * 19;
            if (mx >= bx && mx < bx + 108 && my >= by && my < by + 18)
            {
                if (i < options.Count && item is { } it)
                {
                    var opt = options[i];
                    if (game.player.xpLevel >= opt.level && (lapis?.count ?? 0) >= opt.lapis)
                    {
                        item = applyEnchanting(it, opt);
                        lapis!.count -= opt.lapis;
                        if (lapis.count <= 0) lapis = null;
                        game.player.takeLevels(opt.lapis);
                        seed = new Random().Next(0, 1_000_000_000);
                        Refresh();
                        game.playUISound("block.enchantment_table.use");
                        game.advance("enchant_item");
                    }
                }
                return true;
            }
        }
        return base.OnMouseDown(ui, game, mx, my, btn);
    }
    public override void OnClose(UIManager ui, GameCore game)
    {
        if (item is { } it) game.player.give(it);
        if (lapis is { } l) game.player.give(l);
    }
}

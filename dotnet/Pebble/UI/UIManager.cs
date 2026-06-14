// UI core — screen stack, cursor item, GUI scaling, MC-style panel/slot/button/
// slider/textfield drawing, and the slot-interaction framework shared by every
// container screen. Draws through UICanvas. Ported from
// Sources/Pebble/UIManagerM.swift — the PROCEDURAL path (the Windows app loads
// no GUI sheets, so the Swift `packUI == nil` branches are what run here; the
// pack-sheet blit code is dropped).

using System;
using System.Collections.Generic;
using System.Linq;
using PebbleCore;
using static PebbleCore.ItemGlobals;

namespace Pebble.UI;

/// One slot in a container/inventory screen. `get`/`set` close over the backing
/// store (player inventory list, crafting grid, etc.).
public sealed class SlotDef
{
    public double x;
    public double y;
    public readonly Func<ItemStack?> get;
    public readonly Action<ItemStack?> set;
    public Func<ItemStack, bool>? canPlace;
    public bool output;
    public Action<ItemStack>? onTake;
    public Action? onChange;

    public SlotDef(double x, double y, Func<ItemStack?> get, Action<ItemStack?> set,
                   Func<ItemStack, bool>? canPlace = null, bool output = false,
                   Action<ItemStack>? onTake = null, Action? onChange = null)
    {
        this.x = x; this.y = y;
        this.get = get; this.set = set;
        this.canPlace = canPlace;
        this.output = output;
        this.onTake = onTake;
        this.onChange = onChange;
    }
}

public class Button
{
    public bool enabled = true;
    public bool visible = true;
    public double x, y, w, h;
    public string label;
    public Action onClick;

    public Button(double x, double y, double w, double h, string label, Action onClick)
    {
        this.x = x; this.y = y; this.w = w; this.h = h;
        this.label = label;
        this.onClick = onClick;
    }
    public virtual bool Contains(double mx, double my)
        => visible && enabled && mx >= x && mx < x + w && my >= y && my < y + h;
}

public sealed class Slider : Button
{
    public readonly Func<string> getLabel;
    public readonly Func<double> getValue;
    public readonly Action<double> setValue;
    public bool dragging = false;

    public Slider(double x, double y, double w, double h,
                  Func<string> getLabel, Func<double> getValue, Action<double> setValue)
        : base(x, y, w, h, "", () => { })
    {
        this.getLabel = getLabel;
        this.getValue = getValue;
        this.setValue = setValue;
    }
    public override bool Contains(double mx, double my)
        => visible && enabled && mx >= x && mx < x + w && my >= y && my < y + h;
}

public sealed class TextField
{
    public string text = "";
    public bool focused = false;
    public int caret = 0;
    public int maxLength = 64;
    public double x, y, w, h;
    public string placeholder;

    public TextField(double x, double y, double w, double h, string placeholder = "")
    {
        this.x = x; this.y = y; this.w = w; this.h = h;
        this.placeholder = placeholder;
    }
    public bool Contains(double mx, double my)
        => mx >= x && mx < x + w && my >= y && my < y + h;
    public void Type(string ch)
    {
        if (text.Length < maxLength)
        {
            caret = Math.Clamp(caret, 0, text.Length);
            text = text.Insert(caret, ch);
            caret += ch.Length;
        }
    }
    public void Backspace()
    {
        if (caret > 0)
        {
            text = text.Remove(caret - 1, 1);
            caret -= 1;
        }
    }
}

public class Screen
{
    public bool closeOnEsc = true;
    public bool showHUD = false;
    public bool pausesGame = false;
    public List<Button> buttons = new();
    public List<Slider> sliders = new();
    public List<TextField> fields = new();
    public List<SlotDef> slots = new();

    public virtual void InitScreen(UIManager ui, GameCore game) { }
    public virtual void Draw(UIManager ui, GameCore game, double partial) { }
    public virtual void OnClose(UIManager ui, GameCore game) { }

    public virtual bool OnMouseDown(UIManager ui, GameCore game, double mx, double my, int btn)
    {
        foreach (var f in fields) f.focused = f.Contains(mx, my);
        foreach (var b in buttons)
        {
            if (b is Slider) continue;
            if (b.Contains(mx, my))
            {
                game.playUISound("ui.button.click");
                b.onClick();
                return true;
            }
        }
        foreach (var s in sliders)
        {
            if (s.Contains(mx, my))
            {
                s.dragging = true;
                s.setValue(Math.Clamp((mx - s.x - 4) / (s.w - 8), 0, 1));
                return true;
            }
        }
        var slot = SlotAt(mx, my);
        if (slot != null)
        {
            ui.HandleSlotClick(game, this, slot, btn, ui.shiftDown);
            return true;
        }
        return false;
    }
    public virtual void OnMouseUp(UIManager ui, GameCore game, double mx, double my)
    {
        foreach (var s in sliders) s.dragging = false;
    }
    public virtual void OnMouseMove(UIManager ui, GameCore game, double mx, double my)
    {
        foreach (var s in sliders)
            if (s.dragging) s.setValue(Math.Clamp((mx - s.x - 4) / (s.w - 8), 0, 1));
    }
    public virtual bool OnWheel(UIManager ui, GameCore game, double dy) => false;
    public virtual bool OnKey(UIManager ui, GameCore game, string key)
    {
        foreach (var f in fields)
        {
            if (!f.focused) continue;
            if (key == "Backspace") { f.Backspace(); return true; }
            if (key == "ArrowLeft") { f.caret = Math.Max(0, f.caret - 1); return true; }
            if (key == "ArrowRight") { f.caret = Math.Min(f.text.Length, f.caret + 1); return true; }
        }
        return false;
    }
    public virtual bool OnChar(UIManager ui, GameCore game, string ch)
    {
        foreach (var f in fields)
        {
            if (f.focused) { f.Type(ch); return true; }
        }
        return false;
    }
    public SlotDef? SlotAt(double mx, double my)
        => slots.FirstOrDefault(s => mx >= s.x && mx < s.x + 18 && my >= s.y && my < s.y + 18);

    /// shift-click routing — override in container screens
    public virtual void QuickMove(GameCore game, SlotDef slot) { }
}

public sealed class UIManager
{
    public readonly UICanvas cv;
    public double scale = 3.0;
    public double width = 0.0;    // GUI units
    public double height = 0.0;
    public double mouseX = 0.0;
    public double mouseY = 0.0;
    public bool shiftDown = false;
    public ItemStack? cursorStack;
    private readonly List<Screen> stack = new();
    public List<string>? tooltipLines;

    public UIManager(UICanvas cv) { this.cv = cv; }

    private static double Now() => Environment.TickCount64;

    public void Resize(double pw, double ph, int guiScaleSetting, GameCore? game = null)
    {
        double auto = Math.Max(1.0, Math.Min(Math.Floor(pw / 380), Math.Floor(ph / 240)));
        double newScale = guiScaleSetting == 0 ? auto : Math.Min(guiScaleSetting, auto);
        double newWidth = Math.Ceiling(pw / newScale);
        double newHeight = Math.Ceiling(ph / newScale);
        bool changed = newScale != scale || newWidth != width || newHeight != height;
        scale = newScale;
        width = newWidth;
        height = newHeight;
        if (!changed || game == null) return;
        // re-run layout for every open screen, carrying typed field state over
        foreach (var s in stack)
        {
            var saved = s.fields.Select(f => (f.text, f.caret, f.focused)).ToList();
            s.buttons.Clear();
            s.sliders.Clear();
            s.fields.Clear();
            s.slots.Clear();
            s.InitScreen(this, game);
            for (int i = 0; i < saved.Count && i < s.fields.Count; i++)
            {
                s.fields[i].text = saved[i].text;
                s.fields[i].caret = Math.Min(saved[i].caret, saved[i].text.Length);
                s.fields[i].focused = saved[i].focused;
            }
        }
    }

    public void Open(Screen s, GameCore game)
    {
        if (stack.Count == 0) game.clearInput();
        stack.Add(s);
        s.InitScreen(this, game);
    }
    public void Replace(Screen s, GameCore game)
    {
        CloseTop(game);
        Open(s, game);
    }
    public void CloseTop(GameCore game)
    {
        if (stack.Count > 0)
        {
            var top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            top.OnClose(this, game);
        }
        if (stack.Count == 0 && cursorStack != null)
        {
            game.player?.give(cursorStack);
            cursorStack = null;
        }
    }
    public void CloseAll(GameCore game)
    {
        while (stack.Count > 0) CloseTop(game);
    }
    public Screen? Current() => stack.Count > 0 ? stack[^1] : null;
    public bool HasScreen() => stack.Count > 0;

    // ---- frame ----------------------------------------------------------------
    public void BeginFrame()
    {
        cv.Begin(width * scale, height * scale);
        cv.Scale(scale, scale);
        tooltipLines = null;
    }
    public void EndFrame()
    {
        if (cursorStack != null) DrawItemStack(cursorStack, mouseX - 8, mouseY - 8);
        if (tooltipLines != null && tooltipLines.Count > 0)
            DrawTooltipBox(tooltipLines, mouseX + 6, mouseY - 6);
    }

    // ---- drawing helpers --------------------------------------------------------
    public bool HasSheet(string s) => false;  // Windows app loads no GUI sheets

    public void DrawPanel(double x, double y, double w, double h)
    {
        cv.SetFill("#c6c6c6"); cv.FillRect(x + 1, y + 1, w - 2, h - 2);
        cv.SetFill("#ffffff"); cv.FillRect(x + 1, y, w - 2, 1); cv.FillRect(x, y + 1, 1, h - 2);
        cv.SetFill("#555555"); cv.FillRect(x + 1, y + h - 1, w - 2, 1); cv.FillRect(x + w - 1, y + 1, 1, h - 2);
        cv.SetFill("#000000");
        cv.FillRect(x + 2, y - 1, w - 4, 1); cv.FillRect(x + 2, y + h, w - 4, 1);
        cv.FillRect(x - 1, y + 2, 1, h - 4); cv.FillRect(x + w, y + 2, 1, h - 4);
    }
    public void DrawSlotBg(double x, double y)
    {
        cv.SetFill("#8b8b8b"); cv.FillRect(x, y, 18, 18);
        cv.SetFill("#373737"); cv.FillRect(x, y, 17, 1); cv.FillRect(x, y, 1, 17);
        cv.SetFill("#ffffff"); cv.FillRect(x + 1, y + 17, 17, 1); cv.FillRect(x + 17, y + 1, 1, 17);
    }
    public void DrawItemStack(ItemStack s, double x, double y)
    {
        cv.DrawItemIcon(s.id, s.data, x + 1, y + 1, 16, 16);
        // enchant glint
        if ((s.ench != null && s.ench.Count > 0) || itemDef(s.id).name == "enchanted_golden_apple")
        {
            cv.SetFill("rgba(160,80,255,0.22)");
            cv.FillRect(x + 1, y + 1, 16, 16);
        }
        // durability bar
        var def = itemDef(s.id);
        int maxD = def.tool?.durability ?? def.armor?.durability ?? 0;
        if (maxD > 0 && s.damage > 0)
        {
            double f = 1 - (double)s.damage / maxD;
            cv.SetFill("#000000"); cv.FillRect(x + 2, y + 15, 14, 2);
            cv.SetFill(f > 0.5 ? "#40c040" : f > 0.25 ? "#e8d83c" : "#e84040");
            cv.FillRect(x + 2, y + 15, Math.Max(1, Math.Round(14 * f)), 1);
        }
        if (s.count > 1)
        {
            string c = s.count.ToString();
            cv.DrawText(c, x + 18 - Font.TextWidth(c) - 1, y + 10, 1);
        }
    }
    public void DrawSlot(SlotDef s, bool hover, bool slotBg = true)
    {
        if (slotBg) DrawSlotBg(s.x, s.y);
        var stack = s.get();
        if (stack != null) DrawItemStack(stack, s.x, s.y);
        if (hover)
        {
            cv.SetFill("rgba(255,255,255,0.45)");
            cv.FillRect(s.x + 1, s.y + 1, 16, 16);
            if (stack != null) tooltipLines = ItemTooltip(stack);
        }
    }
    public void DrawSlots(Screen screen, bool slotBg = true)
    {
        foreach (var s in screen.slots)
        {
            bool hover = mouseX >= s.x && mouseX < s.x + 18 && mouseY >= s.y && mouseY < s.y + 18;
            DrawSlot(s, hover, slotBg);
        }
    }
    public void DrawButton(Button b, bool hover)
    {
        if (!b.visible) return;
        cv.SetFill(b.enabled ? (hover ? "#7a8cbf" : "#6f6f6f") : "#3f3f3f");
        cv.FillRect(b.x, b.y, b.w, b.h);
        cv.SetFill(b.enabled ? (hover ? "#aab8e0" : "#a0a0a0") : "#555555");
        cv.FillRect(b.x, b.y, b.w, 1); cv.FillRect(b.x, b.y, 1, b.h);
        cv.SetFill("#2a2a2a");
        cv.FillRect(b.x, b.y + b.h - 1, b.w, 1); cv.FillRect(b.x + b.w - 1, b.y, 1, b.h);
        cv.DrawTextCentered(b.label, b.x + b.w / 2, b.y + (b.h - 8) / 2, 1, b.enabled ? "#ffffff" : "#a0a0a0");
    }
    public void DrawButtons(Screen screen)
    {
        foreach (var b in screen.buttons)
        {
            if (b is Slider) continue;
            DrawButton(b, b.Contains(mouseX, mouseY));
        }
        foreach (var s in screen.sliders)
        {
            cv.SetFill("#3f3f3f"); cv.FillRect(s.x, s.y, s.w, s.h);
            cv.SetFill("#1c1c1c"); cv.FillRect(s.x, s.y, s.w, 1);
            double v = s.getValue();
            double hx = s.x + 2 + v * (s.w - 10);
            cv.SetFill("#8a8a8a"); cv.FillRect(hx, s.y + 1, 6, s.h - 2);
            cv.SetFill("#c8c8c8"); cv.FillRect(hx, s.y + 1, 6, 2);
            cv.DrawTextCentered(s.getLabel(), s.x + s.w / 2, s.y + (s.h - 8) / 2, 1);
        }
        foreach (var f in screen.fields)
        {
            cv.SetFill("#000000"); cv.FillRect(f.x, f.y, f.w, f.h);
            cv.SetStroke(f.focused ? "#ffffff" : "#a0a0a0"); cv.StrokeRect(f.x, f.y, f.w, f.h);
            cv.DrawText(f.text, f.x + 4, f.y + (f.h - 8) / 2, 1, f.text.Length == 0 ? "#707070" : "#ffffff");
            if (f.text.Length == 0 && f.placeholder.Length > 0)
                cv.DrawText(f.placeholder, f.x + 4, f.y + (f.h - 8) / 2, 1, "#5a5a5a");
            if (f.focused && (int)(Now() / 400) % 2 == 0)
            {
                int caret = Math.Clamp(f.caret, 0, f.text.Length);
                double cx = f.x + 4 + Font.TextWidth(f.text.Substring(0, caret));
                cv.SetFill("#ffffff"); cv.FillRect(cx, f.y + 3, 1, f.h - 6);
            }
        }
    }
    public void DrawDarkBg(double alpha = 0.6)
    {
        cv.SetFill($"rgba(8,8,12,{alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        cv.FillRect(0, 0, width, height);
    }
    public void DrawDirtBg()
    {
        cv.SetFill("#3a2a1e");
        cv.FillRect(0, 0, width, height);
        double y = 0.0;
        while (y < height)
        {
            double x = 0.0;
            while (x < width)
            {
                int xi = (int)x, yi = (int)y;
                int h = ((xi * 31 + yi * 17) ^ (xi >> 3)) & 255;
                cv.SetFill(h < 60 ? "#33241a" : h < 120 ? "#403021" : h < 200 ? "#382a1d" : "#443325");
                cv.FillRect(x, y, 4, 4);
                x += 4;
            }
            y += 4;
        }
        cv.SetFill("rgba(0,0,0,0.45)");
        cv.FillRect(0, 0, width, height);
    }
    public void DrawTooltipBox(List<string> lines, double xIn, double yIn)
    {
        double w = 0.0;
        foreach (var l in lines) w = Math.Max(w, Font.TextWidth(l));
        double h = lines.Count * 10 + 6;
        double x = Math.Min(xIn, width - w - 10);
        double y = Math.Max(4, Math.Min(yIn, height - h - 4));
        cv.SetFill("rgba(16,0,16,0.94)"); cv.FillRect(x, y, w + 8, h);
        cv.SetStroke("rgba(80,0,255,0.45)"); cv.StrokeRect(x, y, w + 8, h);
        for (int i = 0; i < lines.Count; i++)
            cv.DrawText(lines[i], x + 4, y + 4 + i * 10, 1);
    }
    public List<string> ItemTooltip(ItemStack s)
    {
        var def = itemDef(s.id);
        var lines = new List<string>();
        string rarityColor = new[] { "§f", "§e", "§b", "§d" }[Math.Min(3, Math.Max(0, def.rarity))];
        lines.Add(rarityColor + (s.label ?? def.displayName));
        if (s.ench != null)
        {
            foreach (var e in s.ench)
                lines.Add("§7" + e.id.Replace("_", " ") + (e.lvl > 1 ? " " + new[] { "I", "II", "III", "IV", "V" }[Math.Min(4, e.lvl - 1)] : ""));
        }
        if (def.food != null) lines.Add($"§2+{def.food.Value.hunger} hunger");
        int maxD = def.tool?.durability ?? def.armor?.durability ?? 0;
        if (maxD > 0) lines.Add($"§7Durability: {maxD - s.damage} / {maxD}");
        return lines;
    }

    // ---- slot interaction ---------------------------------------------------------
    private void QuickMoveOutput(Screen screen, SlotDef slot)
    {
        var targets = (screen as ContainerScreen)?.playerSlots ?? new List<SlotDef>();
        if (targets.Count == 0) return;
        int rounds = 0;
        while (slot.get() is { } s && s.count > 0 && rounds < 64)
        {
            if (!UISlotGlobals.CanFullyInsert(s, targets)) break;
            var taken = copyStack(s)!;
            UISlotGlobals.QuickMoveInto(taken, targets);
            slot.onTake?.Invoke(s);
            rounds += 1;
            if (slot.get() is { } again && ReferenceEquals(again, s)) break;
        }
    }

    public void HandleSlotClick(GameCore game, Screen screen, SlotDef slot, int btn, bool shift = false)
    {
        var inSlot = slot.get();
        var cursor = cursorStack;
        if (shift)
        {
            if (inSlot != null)
            {
                if (slot.output) QuickMoveOutput(screen, slot);
                else screen.QuickMove(game, slot);
                slot.onChange?.Invoke();
            }
            return;
        }
        if (slot.output)
        {
            if (inSlot != null && (cursor == null || (canMerge(cursor, inSlot) && cursor.count + inSlot.count <= maxStackOf(cursor))))
            {
                if (cursor != null) cursor.count += inSlot.count;
                else cursorStack = copyStack(inSlot);
                slot.onTake?.Invoke(inSlot);
                slot.onChange?.Invoke();
            }
            return;
        }
        if (btn == 0)
        {
            if (cursor != null && inSlot != null && canMerge(cursor, inSlot))
            {
                int space = maxStackOf(inSlot) - inSlot.count;
                int move = Math.Min(space, cursor.count);
                inSlot.count += move;
                cursor.count -= move;
                if (cursor.count <= 0) cursorStack = null;
            }
            else if (cursor != null)
            {
                if (slot.canPlace?.Invoke(cursor) ?? true)
                {
                    slot.set(cursor);
                    cursorStack = inSlot;
                }
            }
            else if (inSlot != null)
            {
                cursorStack = inSlot;
                slot.set(null);
            }
        }
        else if (btn == 2)
        {
            // right click
            if (cursor != null)
            {
                if (inSlot == null && (slot.canPlace?.Invoke(cursor) ?? true))
                {
                    var one = copyStack(cursor)!;
                    one.count = 1;
                    slot.set(one);
                    cursor.count -= 1;
                    if (cursor.count <= 0) cursorStack = null;
                }
                else if (inSlot != null && canMerge(cursor, inSlot) && inSlot.count < maxStackOf(inSlot))
                {
                    inSlot.count += 1;
                    cursor.count -= 1;
                    if (cursor.count <= 0) cursorStack = null;
                }
            }
            else if (inSlot != null)
            {
                int half = (inSlot.count + 1) / 2;
                var taken = copyStack(inSlot)!;
                taken.count = half;
                cursorStack = taken;
                inSlot.count -= half;
                if (inSlot.count <= 0) slot.set(null);
            }
        }
        slot.onChange?.Invoke();
    }
}

public static class UISlotGlobals
{
    /// standard player inventory slots (27 main + 9 hotbar) at panel-local coords
    public static List<SlotDef> PlayerInvSlots(Player player, double px, double py)
    {
        var outv = new List<SlotDef>();
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 9; col++)
            {
                int idx = 9 + row * 9 + col;
                outv.Add(new SlotDef(
                    px + col * 18, py + row * 18,
                    () => player.inventory[idx],
                    v => player.inventory[idx] = v));
            }
        }
        for (int col = 0; col < 9; col++)
        {
            int idx = col;
            outv.Add(new SlotDef(
                px + col * 18, py + 58,
                () => player.inventory[idx],
                v => player.inventory[idx] = v));
        }
        return outv;
    }

    /// true if `stack` fits entirely into `targets` (merge space + empty slots)
    public static bool CanFullyInsert(ItemStack stack, List<SlotDef> targets)
    {
        int remaining = stack.count;
        foreach (var t in targets)
        {
            if (t.get() is { } ts && canMerge(ts, stack))
            {
                remaining -= maxStackOf(ts) - ts.count;
                if (remaining <= 0) return true;
            }
        }
        foreach (var t in targets)
        {
            if (t.get() == null && (t.canPlace?.Invoke(stack) ?? true))
            {
                remaining -= maxStackOf(stack);
                if (remaining <= 0) return true;
            }
        }
        return remaining <= 0;
    }

    /// shift-move a stack into a list of slots (merge then empty)
    public static bool QuickMoveInto(ItemStack stack, List<SlotDef> targets)
    {
        foreach (var t in targets)
        {
            if (t.get() is { } ts && canMerge(ts, stack))
            {
                int space = maxStackOf(ts) - ts.count;
                int move = Math.Min(space, stack.count);
                ts.count += move;
                stack.count -= move;
                if (stack.count <= 0) return true;
            }
        }
        foreach (var t in targets)
        {
            if (t.get() == null && (t.canPlace?.Invoke(stack) ?? true))
            {
                t.set(copyStack(stack));
                stack.count = 0;
                return true;
            }
        }
        return stack.count <= 0;
    }
}

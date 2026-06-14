// Routes Silk.NET keyboard/mouse to either the open UI screen or the engine,
// mirroring the macOS app's GameView event routing (main.swift):
//   • When a screen is open, mouse clicks/moves/wheel and keys go to the screen
//     widgets (not the player). Escape closes/backs out. The inventory key (E)
//     closes inventory-style screens. The OS pointer is shown (released).
//   • When no screen is open and we're in a world, input drives the player and
//     the first click captures the pointer (raw mouse look).
//
// The engine expects KeyboardEvent.code-style strings ("KeyW", "Space", ...).

using System;
using PebbleCore;
using Pebble.UI;
using Silk.NET.Input;

namespace Pebble;

public static class InputRouter
{
    /// `guiScale` returns the current UIManager scale; `fbSize` returns the
    /// framebuffer pixel size so screen mouse coords map to GUI units.
    public static void Wire(IInputContext input, GameCore game, UIManager ui,
                            Func<double> nowMs, Func<(double w, double h)> fbSize)
    {
        // map a raw window mouse position (pixels) to GUI units (pixels / scale)
        (double mx, double my) ToGui(System.Numerics.Vector2 p)
        {
            double s = ui.scale <= 0 ? 1 : ui.scale;
            return (p.X / s, p.Y / s);
        }

        foreach (var kb in input.Keyboards)
        {
            kb.KeyDown += (k, key, _) =>
            {
                string? code = EngineKey(key);
                if (code == null) return;
                bool ctrl = kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight)
                            || kb.IsKeyPressed(Key.SuperLeft) || kb.IsKeyPressed(Key.SuperRight);
                ui.shiftDown = kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight);

                // ── screen open: route to the screen, never to the player ──
                var screen = ui.Current();
                if (screen != null)
                {
                    if (code == "Escape")
                    {
                        if (screen.closeOnEsc)
                        {
                            ui.CloseTop(game);
                            ReleaseOrRecapture(input, ui, game);
                        }
                        return;
                    }
                    if (screen.OnKey(ui, game, code)) return;
                    string? ch = PrintableChar(key, kb);
                    if (ch != null && !ctrl && screen.OnChar(ui, game, ch)) return;
                    // inventory key closes inventory-style screens
                    if (code == Keybind(game, "inventory") && screen.closeOnEsc
                        && !ScreenHasFocusedField(screen))
                    {
                        ui.CloseTop(game);
                        ReleaseOrRecapture(input, ui, game);
                    }
                    return;
                }

                // ── no screen: world input ──
                if (!game.hasWorld()) return;
                game.keyDown(code, nowMs(), ctrl);
                // the engine opens the pause/inventory screen on Esc/E; if it did,
                // make sure the pointer is released
                if (ui.HasScreen())
                    ReleaseCursor(input);
            };
            kb.KeyUp += (k, key, _) =>
            {
                string? code = EngineKey(key);
                if (code != null && !ui.HasScreen()) game.keyUp(code);
                ui.shiftDown = kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight);
            };
        }

        foreach (var mouse in input.Mice)
        {
            bool firstMove = true;
            System.Numerics.Vector2 last = default;

            mouse.MouseDown += (m, btn) =>
            {
                var screen = ui.Current();
                if (screen != null)
                {
                    var (mx, my) = ToGui(m.Position);
                    ui.mouseX = mx; ui.mouseY = my;
                    screen.OnMouseDown(ui, game, mx, my, ButtonIndex(btn));
                    ReleaseOrRecapture(input, ui, game);
                    return;
                }
                if (!game.hasWorld()) return;
                if (m.Cursor.CursorMode != CursorMode.Raw)
                {
                    // first click captures the pointer (like the macOS app)
                    m.Cursor.CursorMode = CursorMode.Raw;
                    firstMove = true;
                    return;
                }
                game.mouseDown(ButtonIndex(btn));
            };
            mouse.MouseUp += (m, btn) =>
            {
                var screen = ui.Current();
                if (screen != null)
                {
                    var (mx, my) = ToGui(m.Position);
                    screen.OnMouseUp(ui, game, mx, my);
                    return;
                }
                game.mouseUp(ButtonIndex(btn));
            };

            mouse.MouseMove += (m, pos) =>
            {
                var screen = ui.Current();
                if (screen != null || m.Cursor.CursorMode != CursorMode.Raw)
                {
                    var (mx, my) = ToGui(pos);
                    screen?.OnMouseMove(ui, game, mx, my);
                    ui.mouseX = mx; ui.mouseY = my;
                    last = pos;
                    return;
                }
                if (firstMove) { last = pos; firstMove = false; return; }
                var d = pos - last;
                last = pos;
                if (d.X != 0 || d.Y != 0) game.mouseDelta(d.X, d.Y);
            };

            mouse.Scroll += (m, wheel) =>
            {
                var screen = ui.Current();
                if (screen != null)
                {
                    screen.OnWheel(ui, game, wheel.Y > 0 ? -1 : 1);
                    return;
                }
                if (wheel.Y > 0) game.wheelHotbar(1);
                else if (wheel.Y < 0) game.wheelHotbar(-1);
            };
        }
    }

    private static bool ScreenHasFocusedField(Screen s)
    {
        foreach (var f in s.fields) if (f.focused) return true;
        return false;
    }

    private static string? Keybind(GameCore game, string name)
        => game.keybinds.TryGetValue(name, out var v) ? v : null;

    /// After closing a screen: if no screen remains and we're in a world, recapture
    /// the pointer for mouse look; otherwise leave the cursor visible.
    private static void ReleaseOrRecapture(IInputContext input, UIManager ui, GameCore game)
    {
        foreach (var mouse in input.Mice)
        {
            if (!ui.HasScreen() && game.hasWorld())
                mouse.Cursor.CursorMode = CursorMode.Raw;
            else
                mouse.Cursor.CursorMode = CursorMode.Normal;
        }
    }

    private static void ReleaseCursor(IInputContext input)
    {
        foreach (var mouse in input.Mice) mouse.Cursor.CursorMode = CursorMode.Normal;
    }

    private static int ButtonIndex(MouseButton b) => b switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 2,    // engine: 2 == right (use), 1 == middle (pick)
        MouseButton.Middle => 1,
        _ => 0,
    };

    /// Silk.NET Key → a printable character for screen text fields (basic, no IME).
    private static string? PrintableChar(Key k, IKeyboard kb)
    {
        bool shift = kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight);
        if (k >= Key.A && k <= Key.Z)
        {
            char c = (char)('a' + (k - Key.A));
            return shift ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }
        if (k >= Key.Number0 && k <= Key.Number9 && !shift)
            return ((char)('0' + (k - Key.Number0))).ToString();
        return k switch
        {
            Key.Space => " ",
            Key.Minus => shift ? "_" : "-",
            Key.Period => ".",
            Key.Comma => ",",
            _ => null,
        };
    }

    /// Silk.NET Key → engine key-code string. Returns null for unmapped keys.
    public static string? EngineKey(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return "Key" + k.ToString();             // KeyA..KeyZ
        if (k >= Key.Number0 && k <= Key.Number9) return "Digit" + (int)(k - Key.Number0);
        if (k >= Key.Keypad0 && k <= Key.Keypad9) return "Numpad" + (int)(k - Key.Keypad0);
        if (k >= Key.F1 && k <= Key.F12) return k.ToString();                  // F1..F12
        return k switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.KeypadEnter => "NumpadEnter",
            Key.Escape => "Escape",
            Key.Tab => "Tab",
            Key.Backspace => "Backspace",
            Key.Delete => "Delete",
            Key.ShiftLeft => "ShiftLeft",
            Key.ShiftRight => "ShiftLeft",
            Key.ControlLeft => "ControlLeft",
            Key.ControlRight => "ControlLeft",
            Key.AltLeft => "AltLeft",
            Key.AltRight => "AltLeft",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Minus => "Minus",
            Key.Equal => "Equal",
            Key.LeftBracket => "BracketLeft",
            Key.RightBracket => "BracketRight",
            Key.Semicolon => "Semicolon",
            Key.Apostrophe => "Quote",
            Key.Comma => "Comma",
            Key.Period => "Period",
            Key.Slash => "Slash",
            Key.BackSlash => "Backslash",
            Key.GraveAccent => "Backquote",
            _ => null,
        };
    }
}

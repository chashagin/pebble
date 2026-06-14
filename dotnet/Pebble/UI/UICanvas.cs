// UICanvas — an immediate-mode 2D quad batcher, ported from
// Sources/Pebble/UICanvas.swift. It records solid/gradient colored quads, a 5×7
// pixel font, and item-icon blits into one interleaved vertex stream that the GPU
// backend's UI pass draws in a single (or few) draw calls.
//
// Vertex layout (9 floats / 36 bytes): pos.xy (screen px), uv.xy, layer (float;
// <0 = solid white / no texture), color.rgba (already multiplied by globalAlpha).
//
// Method names are kept close to the Swift original (fillRect, setFill,
// drawText, drawTextCentered, save/restore/translate/scale, drawItemIcon).
// The transform stack is a 3×3 affine matrix as in the Swift version.
//
// Item icons are resolved through an injected delegate (IconLayerResolver): the
// backend owns the icon Texture2DArray and returns the layer index for a given
// (itemId, potion) icon, uploading its 16×16 pixels on first use.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Pebble.UI;

/// Resolves an item icon to a Texture2DArray layer index, uploading its pixels on
/// first request. Returns -1 if the icon can't be provided (drawn as a blank quad).
public delegate int IconLayerResolver(int itemId, PebbleCore.StackData? data);

public sealed class UICanvas
{
    // Interleaved vertex stream: pos2 uv2 layer1 color4 = 9 floats per vertex.
    public const int FloatsPerVertex = 9;
    private readonly List<float> _verts = new(8192);
    public IReadOnlyList<float> Verts => _verts;
    public int VertexCount => _verts.Count / FloatsPerVertex;

    public IconLayerResolver? IconResolver;

    // canvas state (mirrors the Swift fields)
    public Vector4 fillStyle = new(1, 1, 1, 1);
    public Vector4 strokeStyle = new(1, 1, 1, 1);
    public float globalAlpha = 1f;

    public double width;
    public double height;

    // 3×3 affine transform (column-major like the Swift simd_float3x3).
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private readonly Stack<Matrix3x2> _stack = new();

    // ── frame lifecycle ──────────────────────────────────────────────────────
    public void Begin(double w, double h)
    {
        width = w;
        height = h;
        _verts.Clear();
        _transform = Matrix3x2.Identity;
        _stack.Clear();
        globalAlpha = 1f;
    }

    // ── transform stack ──────────────────────────────────────────────────────
    public void Save() => _stack.Push(_transform);
    public void Restore() { if (_stack.Count > 0) _transform = _stack.Pop(); }
    public void Translate(double x, double y) =>
        _transform = Matrix3x2.CreateTranslation((float)x, (float)y) * _transform;
    public void Scale(double x, double y) =>
        _transform = Matrix3x2.CreateScale((float)x, (float)y) * _transform;

    private Vector2 Xf(float x, float y) => Vector2.Transform(new Vector2(x, y), _transform);

    // ── color parsing ("#rrggbb", "#rgb", "#rgba", rgb()/rgba(), hsl()/hsla()) ─
    public static Vector4 Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return new Vector4(1, 1, 1, 1);
        if (s[0] == '#')
        {
            string hex = s.Substring(1);
            if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v6))
                return new Vector4(((v6 >> 16) & 255) / 255f, ((v6 >> 8) & 255) / 255f, (v6 & 255) / 255f, 1);
            if (hex.Length == 4 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v4))
                return new Vector4(((v4 >> 12) & 15) / 15f, ((v4 >> 8) & 15) / 15f, ((v4 >> 4) & 15) / 15f, (v4 & 15) / 15f);
            if (hex.Length == 3 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v3))
                return new Vector4(((v3 >> 8) & 15) / 15f, ((v3 >> 4) & 15) / 15f, (v3 & 15) / 15f, 1);
            return new Vector4(1, 1, 1, 1);
        }

        List<double> Nums(string str)
        {
            var outv = new List<double>();
            var cur = new System.Text.StringBuilder();
            foreach (char ch in str)
            {
                if (char.IsDigit(ch) || ch == '.' || ch == '-') cur.Append(ch);
                else if (cur.Length > 0) { outv.Add(ParseD(cur.ToString())); cur.Clear(); }
            }
            if (cur.Length > 0) outv.Add(ParseD(cur.ToString()));
            return outv;
        }

        var n = Nums(s);
        if (s.StartsWith("hsl"))
        {
            double h = n.Count > 0 ? n[0] : 0, sat = (n.Count > 1 ? n[1] : 0) / 100, l = (n.Count > 2 ? n[2] : 0) / 100;
            double a = s.StartsWith("hsla") && n.Count > 3 ? n[3] : 1;
            double c = (1 - Math.Abs(2 * l - 1)) * sat;
            double hp = (((h % 360) + 360) % 360) / 60;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double r1, g1, b1;
            switch ((int)hp)
            {
                case 0: (r1, g1, b1) = (c, x, 0); break;
                case 1: (r1, g1, b1) = (x, c, 0); break;
                case 2: (r1, g1, b1) = (0, c, x); break;
                case 3: (r1, g1, b1) = (0, x, c); break;
                case 4: (r1, g1, b1) = (x, 0, c); break;
                default: (r1, g1, b1) = (c, 0, x); break;
            }
            double m = l - c / 2;
            return new Vector4((float)(r1 + m), (float)(g1 + m), (float)(b1 + m), (float)a);
        }
        if (s.StartsWith("rgb"))
        {
            double a = s.StartsWith("rgba") && n.Count > 3 ? n[3] : 1;
            return new Vector4((float)((n.Count > 0 ? n[0] : 0) / 255), (float)((n.Count > 1 ? n[1] : 0) / 255),
                               (float)((n.Count > 2 ? n[2] : 0) / 255), (float)a);
        }
        return new Vector4(1, 1, 1, 1);
    }

    private static double ParseD(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    public void SetFill(string s) => fillStyle = Parse(s);
    public void SetStroke(string s) => strokeStyle = Parse(s);

    // ── quad emission ────────────────────────────────────────────────────────
    private const float WhiteLayer = -1f;

    private void EmitQuad(float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, float layer, Vector4 cTop, Vector4 cBot)
    {
        Vector2 p0 = Xf(x, y), p1 = Xf(x + w, y), p2 = Xf(x + w, y + h), p3 = Xf(x, y + h);
        Vector4 ct = cTop, cb = cBot;
        ct.W *= globalAlpha;
        cb.W *= globalAlpha;

        void Push(Vector2 p, float u, float v, Vector4 c)
        {
            _verts.Add(p.X); _verts.Add(p.Y);
            _verts.Add(u); _verts.Add(v);
            _verts.Add(layer);
            _verts.Add(c.X); _verts.Add(c.Y); _verts.Add(c.Z); _verts.Add(c.W);
        }
        Push(p0, u0, v0, ct); Push(p1, u1, v0, ct); Push(p2, u1, v1, cb);
        Push(p0, u0, v0, ct); Push(p2, u1, v1, cb); Push(p3, u0, v1, cb);
    }

    public void FillRect(double x, double y, double w, double h)
        => EmitQuad((float)x, (float)y, (float)w, (float)h, 0, 0, 1, 1, WhiteLayer, fillStyle, fillStyle);

    public void FillRect(double x, double y, double w, double h, string top, string bottom)
        => EmitQuad((float)x, (float)y, (float)w, (float)h, 0, 0, 1, 1, WhiteLayer, Parse(top), Parse(bottom));

    public void StrokeRect(double x, double y, double w, double h, double lw = 1)
    {
        var c = fillStyle;
        fillStyle = strokeStyle;
        FillRect(x, y, w, lw);
        FillRect(x, y + h - lw, w, lw);
        FillRect(x, y, lw, h);
        FillRect(x + w - lw, y, lw, h);
        fillStyle = c;
    }

    // ── item icons ───────────────────────────────────────────────────────────
    public void DrawItemIcon(int itemId, PebbleCore.StackData? data, double x, double y, double w = 16, double h = 16)
    {
        int layer = IconResolver?.Invoke(itemId, data) ?? -1;
        if (layer < 0) return;
        EmitQuad((float)x, (float)y, (float)w, (float)h, 0, 0, 1, 1, layer,
            new Vector4(1, 1, 1, 1), new Vector4(1, 1, 1, 1));
    }

    // ── 5×7 pixel font ───────────────────────────────────────────────────────
    public double DrawText(string text, double x, double y, double s, string color = "#FFFFFF", bool shadow = true)
    {
        Vector4 curColor = Parse(color);
        Vector4 curShadow = ShadowOf(curColor);
        double cx = x;
        double startX = x;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch == '§') // §
            {
                if (i + 1 < text.Length)
                {
                    string code = char.ToLowerInvariant(text[i + 1]).ToString();
                    if (Font.McColors.TryGetValue(code, out var mc))
                    {
                        curColor = Parse(mc);
                        curShadow = ShadowOf(curColor);
                    }
                    else if (code == "r")
                    {
                        curColor = Parse(color);
                        curShadow = ShadowOf(curColor);
                    }
                    i += 2;
                    continue;
                }
            }
            int[] g = Font.Glyph(ch);
            for (int col = 0; col < g.Length; col++)
            {
                int bits = g[col];
                for (int row = 0; row < 8; row++)
                {
                    if ((bits & (1 << row)) == 0) continue;
                    if (shadow)
                    {
                        var save = fillStyle;
                        fillStyle = curShadow;
                        FillRect(cx + (col + 1) * s, y + (row + 1) * s, s, s);
                        fillStyle = save;
                    }
                    var save2 = fillStyle;
                    fillStyle = curColor;
                    FillRect(cx + col * s, y + row * s, s, s);
                    fillStyle = save2;
                }
            }
            cx += (g.Length + 1) * s;
            i += 1;
        }
        return cx - startX;
    }

    public void DrawTextCentered(string text, double cx, double y, double s, string color = "#FFFFFF", bool shadow = true)
        => DrawText(text, cx - Font.TextWidth(text) * s / 2, y, s, color, shadow);

    private static Vector4 ShadowOf(Vector4 c) => new(c.X / 4, c.Y / 4, c.Z / 4, c.W);
}

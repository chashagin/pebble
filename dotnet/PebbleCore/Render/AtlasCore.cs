// Procedural texture atlas core — (paint surface +
// registry + assembly). Painters live in AtlasPainters1/2.swift. Pixel output
// is byte-identical to the golden baselines: same hash-driven randomness, same
// integer color math.
// Ported from Sources/PebbleCore/Render/AtlasCore.swift.
//
// Defines static class `AtlasGlobals` (module-level funcs/vars: TILE, rgb,
// shade, mixC, painter registry, ensurePainters, paintTile, buildAtlas,
// missingTiles, etc.) plus the public types `T` and `BuiltAtlas`, and the
// delegate aliases `Painter`/`RegexPainter`.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PebbleCore;

// painter registry delegate aliases
public delegate void Painter(T t);
public delegate void RegexPainter(T t, string[] groups);

/// per-tile paint surface
public sealed class T
{
    public byte[] data = new byte[AtlasGlobals.TILE * AtlasGlobals.TILE * 4];
    public readonly uint seed;
    public readonly string name;

    public T(string name)
    {
        this.name = name;
        seed = hashString(name);
    }

    public double rand(int x, int y, uint salt = 0)
    {
        return (double)hash2(seed, x, y, salt) / 4294967296.0;
    }
    public void set(int x, int y, int c, int a = 255)
    {
        if (x < 0 || x >= AtlasGlobals.TILE || y < 0 || y >= AtlasGlobals.TILE) return;
        int i = (y * AtlasGlobals.TILE + x) * 4;
        data[i] = (byte)((c >> 16) & 255);
        data[i + 1] = (byte)((c >> 8) & 255);
        data[i + 2] = (byte)(c & 255);
        data[i + 3] = (byte)Math.Min(255, Math.Max(0, a));
    }
    public int get(int x, int y)
    {
        int i = (y * AtlasGlobals.TILE + x) * 4;
        return AtlasGlobals.rgb((int)data[i], (int)data[i + 1], (int)data[i + 2]);
    }
    public int alphaAt(int x, int y) => (int)data[(y * AtlasGlobals.TILE + x) * 4 + 3];
    public void fill(int c, int a = 255)
    {
        for (int y = 0; y < AtlasGlobals.TILE; y++) { for (int x = 0; x < AtlasGlobals.TILE; x++) { set(x, y, c, a); } }
    }
    /// filled with per-pixel brightness noise
    public void noise(int c, double amt = 0.12, uint salt = 0)
    {
        for (int y = 0; y < AtlasGlobals.TILE; y++)
        {
            for (int x = 0; x < AtlasGlobals.TILE; x++)
            {
                double f = 1 - amt + rand(x, y, salt) * amt * 2;
                set(x, y, AtlasGlobals.shade(c, f));
            }
        }
    }
    /// smooth blotchy noise (2-scale)
    public void blotch(int c, double amt = 0.14, int scale = 4, uint salt = 1)
    {
        for (int y = 0; y < AtlasGlobals.TILE; y++)
        {
            for (int x = 0; x < AtlasGlobals.TILE; x++)
            {
                int bx = x / scale, by = y / scale;
                double fx = (double)(x % scale) / (double)scale, fy = (double)(y % scale) / (double)scale;
                double v00 = rand(bx, by, salt), v10 = rand(bx + 1, by, salt);
                double v01 = rand(bx, by + 1, salt), v11 = rand(bx + 1, by + 1, salt);
                double v = (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
                double fine = rand(x, y, salt + 7) * 0.5 + 0.5;
                double f = 1 - amt + (v * 0.7 + fine * 0.3) * amt * 2;
                set(x, y, AtlasGlobals.shade(c, f));
            }
        }
    }
    public void rect(int x0, int y0, int x1, int y1, int c, int a = 255)
    {
        int y = y0;
        while (y <= y1)
        {
            int x = x0;
            while (x <= x1) { set(x, y, c, a); x += 1; }
            y += 1;
        }
    }
    public void speckle(int c, int count, uint salt = 2)
    {
        for (int i = 0; i < count; i++)
        {
            int x = (int)(rand(i, 0, salt) * (double)AtlasGlobals.TILE);
            int y = (int)(rand(i, 1, salt) * (double)AtlasGlobals.TILE);
            set(x, y, c);
        }
    }
    public void border(int c)
    {
        for (int i = 0; i < AtlasGlobals.TILE; i++)
        {
            set(i, 0, c); set(i, AtlasGlobals.TILE - 1, c);
            set(0, i, c); set(AtlasGlobals.TILE - 1, i, c);
        }
    }
    /// ore blobs over existing base
    public void oreBlobs(int c, int count = 5, uint salt = 3)
    {
        int hi = AtlasGlobals.shade(c, 1.35), lo = AtlasGlobals.shade(c, 0.7);
        for (int i = 0; i < count; i++)
        {
            int cx = 2 + (int)(rand(i, 0, salt) * 12);
            int cy = 2 + (int)(rand(i, 1, salt) * 12);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) == 2 && rand(cx + dx, cy + dy, salt) < 0.5) continue;
                    double r = rand(cx + dx, cy + dy, salt + 1);
                    set(cx + dx, cy + dy, r < 0.3 ? hi : r < 0.7 ? c : lo);
                }
            }
        }
    }
    /// vertical wood grain — NOTE: TILE/cols is FLOAT division in the baseline source
    public void grain(int @base, double contrast = 0.16, int cols = 4, uint salt = 4)
    {
        for (int x = 0; x < AtlasGlobals.TILE; x++)
        {
            int colSeed = (int)Math.Floor((double)x / ((double)AtlasGlobals.TILE / (double)cols));
            double tone = 1 - contrast / 2 + rand(colSeed, 0, salt) * contrast;
            for (int y = 0; y < AtlasGlobals.TILE; y++)
            {
                double wob = rand(colSeed, y / 3, salt + 1) * 0.1;
                double fine = rand(x, y, salt + 2) * 0.08;
                set(x, y, AtlasGlobals.shade(@base, tone - wob + fine));
            }
        }
    }
    public void planks(int @base, uint salt = 5)
    {
        grain(@base, 0.12, 5, salt);
        int dark = AtlasGlobals.shade(@base, 0.62);
        foreach (int y in new[] { 3, 7, 11, 15 })
        {
            for (int x = 0; x < AtlasGlobals.TILE; x++) { set(x, y, dark); }
        }
        // staggered vertical joints
        set(4, 0, dark); set(4, 1, dark); set(4, 2, dark);
        set(11, 4, dark); set(11, 5, dark); set(11, 6, dark);
        set(2, 8, dark); set(2, 9, dark); set(2, 10, dark);
        set(13, 12, dark); set(13, 13, dark); set(13, 14, dark);
    }
    public void bricks(int @base, int mortar, int bw = 8, int bh = 4, uint salt = 6)
    {
        for (int y = 0; y < AtlasGlobals.TILE; y++)
        {
            int row = y / bh;
            int off = (row % 2) * (bw / 2);
            for (int x = 0; x < AtlasGlobals.TILE; x++)
            {
                bool isMortarY = (y % bh) == bh - 1;
                bool isMortarX = ((x + off) % bw) == bw - 1;
                if (isMortarY || isMortarX) { set(x, y, mortar); }
                else
                {
                    int bidx = (x + off) / bw + row * 7;
                    double tone = 0.9 + rand(bidx, row, salt) * 0.2 + rand(x, y, salt + 1) * 0.08;
                    set(x, y, AtlasGlobals.shade(@base, tone));
                }
            }
        }
    }
    /// ASCII pixel art: rows of chars; palette maps char→color
    public void px(string[] rows, Dictionary<char, int> palette, int dx = 0, int dy = 0)
    {
        for (int y = 0; y < rows.Length; y++)
        {
            string row = rows[y];
            for (int x = 0; x < row.Length; x++)
            {
                char ch = row[x];
                if (ch == ' ' || ch == '.') continue;
                if (!palette.TryGetValue(ch, out int c)) continue;
                set(x + dx, y + dy, c);
            }
        }
    }
    public void clearAlpha()
    {
        int i = 3;
        while (i < data.Length) { data[i] = 0; i += 4; }
    }
    public void vline(int x, int y0, int y1, int c)
    {
        int y = y0;
        while (y <= y1) { set(x, y, c); y += 1; }
    }
    public void hline(int y, int x0, int x1, int c)
    {
        int x = x0;
        while (x <= x1) { set(x, y, c); x += 1; }
    }
    public void disc(double cx, double cy, double r, int c)
    {
        int y = (int)Math.Floor(cy - r);
        while ((double)y <= cy + r)
        {
            int x = (int)Math.Floor(cx - r);
            while ((double)x <= cx + r)
            {
                double dx = (double)x - cx, dy = (double)y - cy;
                if (dx * dx + dy * dy <= r * r) { set(x, y, c); }
                x += 1;
            }
            y += 1;
        }
    }
    public void cross(int @base, uint salt = 8)
    {
        // generic plant: stems + leaf clusters
        clearAlpha();
        int dark = AtlasGlobals.shade(@base, 0.7), light = AtlasGlobals.shade(@base, 1.25);
        for (int i = 0; i < 4; i++)
        {
            int x = 3 + (int)(rand(i, 0, salt) * 10);
            int h = 5 + (int)(rand(i, 1, salt) * 9);
            int y = AtlasGlobals.TILE - 1;
            while (y > AtlasGlobals.TILE - 1 - h)
            {
                int wob = (int)(rand(i, y, salt) * 2);
                set(x + wob, y, rand(x, y, salt) < 0.5 ? @base : dark);
                y -= 1;
            }
            set(x, AtlasGlobals.TILE - h, light);
        }
    }
}

public sealed class BuiltAtlas
{
    public readonly int count;
    /// tiles as raw RGBA (TILE*TILE*4 bytes each) for Metal upload
    public readonly byte[][] pixels;
    public readonly string[] missing;

    public BuiltAtlas(int count, byte[][] pixels, string[] missing)
    {
        this.count = count;
        this.pixels = pixels;
        this.missing = missing;
    }
}

// ---------------------------------------------------------------------------
// module-level free functions / vars (Swift global scope of AtlasCore.swift)
// ---------------------------------------------------------------------------
public static class AtlasGlobals
{
    public const int TILE = 16;

    public static int rgb(int r, int g, int b)
    {
        return (r << 16) | (g << 8) | b;
    }
    public static int shade(int c, double f)
    {
        int r = Math.Min(255, Math.Max(0, (int)detRound((double)((c >> 16) & 255) * f)));
        int g = Math.Min(255, Math.Max(0, (int)detRound((double)((c >> 8) & 255) * f)));
        int b = Math.Min(255, Math.Max(0, (int)detRound((double)(c & 255) * f)));
        return rgb(r, g, b);
    }
    public static int mixC(int a, int b, double t)
    {
        int ar = (a >> 16) & 255, ag = (a >> 8) & 255, ab = a & 255;
        int br = (b >> 16) & 255, bg = (b >> 8) & 255, bb = b & 255;
        return rgb((int)detRound((double)ar + (double)(br - ar) * t),
                   (int)detRound((double)ag + (double)(bg - ag) * t),
                   (int)detRound((double)ab + (double)(bb - ab) * t));
    }

    // -----------------------------------------------------------------------
    // painter registry
    // -----------------------------------------------------------------------
    public static Dictionary<string, Painter> painters = new Dictionary<string, Painter>();
    public static List<(Regex, RegexPainter)> regexPainters = new List<(Regex, RegexPainter)>();

    public static void p(string name, Painter fn) { painters[name] = fn; }
    public static void rp(string pattern, RegexPainter fn)
    {
        Regex re = new Regex(pattern);
        regexPainters.Add((re, fn));
    }

    public static string[] matchGroups(string name, Regex re)
    {
        Match m = re.Match(name);
        if (!m.Success) return null;
        List<string> groups = new List<string>();
        for (int i = 0; i < m.Groups.Count; i++)
        {
            Group r = m.Groups[i];
            if (!r.Success) groups.Add(null);
            else groups.Add(r.Value);
        }
        return groups.ToArray();
    }

    public static List<string> missingTiles = new List<string>();

    public static void fallbackPaint(T t)
    {
        uint h = mix32(t.seed);
        int c = rgb(100 + (int)(h & 63), 100 + (int)((h >> 6) & 63), 100 + (int)((h >> 12) & 63));
        t.blotch(c, 0.13, 3);
        missingTiles.Add(t.name);
    }

    public static void paintInto(T t, string name)
    {
        if (painters.TryGetValue(name, out Painter fn)) { fn(t); return; }
        foreach (var (re, rfn) in regexPainters)
        {
            string[] m = matchGroups(name, re);
            if (m != null) { rfn(t, m); return; }
        }
        fallbackPaint(t);
    }

    private static bool paintersRegistered = false;
    public static void ensurePainters()
    {
        if (paintersRegistered) return;
        paintersRegistered = true;
        registerPainters1();
        registerPainters2();
    }

    public static T paintTile(string name)
    {
        ensurePainters();
        T t = new T(name);
        if (painters.TryGetValue(name, out Painter fn)) { fn(t); return t; }
        foreach (var (re, rfn) in regexPainters)
        {
            string[] m = matchGroups(name, re);
            if (m != null) { rfn(t, m); return t; }
        }
        fallbackPaint(t);
        return t;
    }

    public static BuiltAtlas buildAtlas()
    {
        ensurePainters();
        missingTiles = new List<string>();
        List<string> names = allTileNames();
        List<byte[]> pixels = new List<byte[]>();
        pixels.Capacity = names.Count;
        foreach (string n in names)
        {
            pixels.Add(paintTile(n).data);
        }
        return new BuiltAtlas(names.Count, pixels.ToArray(), missingTiles.ToArray());
    }
}

// Item icons — 16×16 RGBA pixel sprites rendered once
// into a cache: blocks as isometric mini-cubes from atlas tiles, items from
// pixel-art templates. Same templates, same colors, same fallbacks.
//
// Ported from Sources/PebbleCore/Render/Icons.swift.
//
// Defines static class `IconGlobals` (module-level funcs/vars: itemIconOverride,
// initIcons, resetIconCache, itemIconPixels). All other funcs in the Swift file
// are private file-locals and stay private static here. Pixel output is
// byte-identical to the golden baselines: same hash-driven randomness, same
// integer color math, same rounding.
//
// External symbols referenced by name (defined elsewhere in the assembly):
//   BuiltAtlas (Render/AtlasCore), StackData/ItemDef/ToolDef/ArmorDef/itemDefs
//   (Items), blockDefs/Shape/SHAPE_OF (World), potionDef (Items/EnchantsPotions),
//   COLOR_RGB (World/BlockRegistry), hashString/hash2 (Rng).

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class IconGlobals
{
    private static Dictionary<string, byte[]> iconCache = new();
    private static BuiltAtlas iconAtlas = null;

    /// app-installed resource-pack item textures (item name → 16×16 straight RGBA).
    /// pebsmoke never sets this, so the icon goldens always see the painters.
    public static Func<string, byte[]> itemIconOverride = null;

    public static void initIcons(BuiltAtlas atlas)
    {
        iconAtlas = atlas;
    }

    public static void resetIconCache()
    {
        iconCache.Clear();
    }

    private static byte[] tilePixels(int tile)
    {
        if (iconAtlas == null || tile < 0 || tile >= iconAtlas.pixels.Length) return null;
        return iconAtlas.pixels[tile];
    }

    /// 16×16 RGBA pixels for an item icon (cached per item+potion)
    public static byte[] itemIconPixels(int itemId, StackData? data = null)
    {
        var def = itemDefs[itemId];
        string potion = data?.potion;
        string key = def.name + (potion != null ? ":" + potion : "");
        if (iconCache.TryGetValue(key, out var c)) { return c; }
        // potion-family icons keep the procedural painter (dynamic per-effect tint)
        if (potion == null && itemIconOverride != null)
        {
            var ov = itemIconOverride;
            var px2 = ov(def.name);
            if (px2 != null)
            {
                iconCache[key] = px2;
                return px2;
            }
        }
        var img = new byte[16 * 16 * 4];
        paintIcon(img, def, data);
        iconCache[key] = img;
        return img;
    }

    // rgb int helpers (baseline used css strings; here colors are 0xRRGGBB ints, -1 = clear)
    private static void put(byte[] img, int x, int y, int c)
    {
        if (x < 0 || x > 15 || y < 0 || y > 15 || c < 0) return;
        int i = (y * 16 + x) * 4;
        img[i] = (byte)((c >> 16) & 255);
        img[i + 1] = (byte)((c >> 8) & 255);
        img[i + 2] = (byte)(c & 255);
        img[i + 3] = 255;
    }

    private static int hsl(double h, double s, double l)
    {
        double cc = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = ((h % 360) + 360) % 360 / 60;
        double xx = cc * (1 - Math.Abs(hp % 2 - 1));
        double r1, g1, b1;
        switch ((int)hp)
        {
            case 0: (r1, g1, b1) = (cc, xx, 0); break;
            case 1: (r1, g1, b1) = (xx, cc, 0); break;
            case 2: (r1, g1, b1) = (0, cc, xx); break;
            case 3: (r1, g1, b1) = (0, xx, cc); break;
            case 4: (r1, g1, b1) = (xx, 0, cc); break;
            default: (r1, g1, b1) = (cc, 0, xx); break;
        }
        double m = l - cc / 2;
        int r = (int)Math.Round((r1 + m) * 255, MidpointRounding.AwayFromZero);
        int g = (int)Math.Round((g1 + m) * 255, MidpointRounding.AwayFromZero);
        int b = (int)Math.Round((b1 + m) * 255, MidpointRounding.AwayFromZero);
        return (Math.Max(0, Math.Min(255, r)) << 16) | (Math.Max(0, Math.Min(255, g)) << 8) | Math.Max(0, Math.Min(255, b));
    }

    private static (int, int, int) materialColors(string name)
    {
        if (name.StartsWith("wooden")) return (0xa8845c, 0x8a6a42, 0x5c4426);
        if (name.StartsWith("stone")) return (0xaaaaaa, 0x8a8a8a, 0x5c5c5c);
        if (name.StartsWith("iron")) return (0xe8e8e8, 0xc8c8c8, 0x8a8a8a);
        if (name.StartsWith("golden")) return (0xfcee4b, 0xe8c83c, 0xa8862c);
        if (name.StartsWith("diamond")) return (0x8cf4e2, 0x4aedd9, 0x2ca89a);
        if (name.StartsWith("netherite")) return (0x5a5054, 0x42383b, 0x2a2326);
        if (name.StartsWith("leather")) return (0xc08850, 0x9a6a42, 0x6a4426);
        if (name.StartsWith("chainmail")) return (0xd8d8d8, 0xaaaaaa, 0x787878);
        if (name.StartsWith("turtle")) return (0x6a9a4c, 0x47702e, 0x2c4c1c);
        return (0xcccccc, 0x999999, 0x666666);
    }

    private static readonly Dictionary<string, string[]> TOOL_TEMPLATES = new()
    {
        ["sword"] = new[] {
            "..........#...",
            ".........#H#..",
            "........#HB#..",
            ".......#HB#...",
            "......#HB#....",
            ".....#HB#.....",
            "....#HB#......",
            ".W.#HB#.......",
            ".WW#B#........",
            ".WWW#.........",
            "#WWW..........",
            "#W#WW.........",
            ".#..#.........",
        },
        ["pickaxe"] = new[] {
            "....#HHHH#....",
            "..#HHBBBBH#...",
            ".#HB##..#BH#..",
            ".#B#..W..#B#..",
            ".##..WW...##..",
            ".....WW.......",
            "....WW........",
            "...WW.........",
            "..WW..........",
            ".WW...........",
            "#W............",
        },
        ["axe"] = new[] {
            "...#HH#.......",
            "..#HBBH#......",
            ".#HBB#B#......",
            ".#BB#WW#......",
            ".#B#.WW.......",
            ".##.WW........",
            "...WW.........",
            "..WW..........",
            ".WW...........",
            "#W............",
        },
        ["shovel"] = new[] {
            ".......#HH#...",
            "......#HBBH#..",
            "......#BBBB#..",
            ".....W#BBB#...",
            "....WW.##.....",
            "...WW.........",
            "..WW..........",
            ".WW...........",
            "#W............",
        },
        ["hoe"] = new[] {
            "....#HHH#.....",
            "..#HHBB##.....",
            "..##.#W#......",
            ".....WW.......",
            "....WW........",
            "...WW.........",
            "..WW..........",
            ".WW...........",
            "#W............",
        },
    };

    private static readonly Dictionary<int, string[]> ARMOR_TEMPLATES = new()
    {
        [0] = new[] {
            "....######....",
            "...#HHHHHH#...",
            "..#HHBBBBHH#..",
            "..#HBBBBBBB#..",
            "..#BB####BB#..",
            "..#BB#..#BB#..",
            "..###....###..",
        },
        [1] = new[] {
            "..##......##..",
            ".#HH#....#HH#.",
            ".#HBB####BBH#.",
            ".#HBBBBBBBBH#.",
            "..##BBBBBB##..",
            "...#BBBBBB#...",
            "...#BBBBBB#...",
            "...#BBBBBB#...",
            "...########...",
        },
        [2] = new[] {
            "...########...",
            "..#HBBBBBBH#..",
            "..#HB####BH#..",
            "..#BB#..#BB#..",
            "..#BB#..#BB#..",
            "..#BB#..#BB#..",
            "..#BB#..#BB#..",
            "..####..####..",
        },
        [3] = new[] {
            "..............",
            "..##....##....",
            "..#B#...#B#...",
            "..#B#...#B#...",
            "..#BB#..#BB#..",
            "..#BBB#.#BBB#.",
            "..#####.#####.",
        },
    };

    private static void drawTemplate(byte[] img, string[] rows, int H, int Bc, int D, int ox = 1, int oy = 1)
    {
        for (int y = 0; y < rows.Length; y++)
        {
            string row = rows[y];
            for (int x = 0; x < row.Length; x++)
            {
                char ch = row[x];
                if (ch == '.') continue;
                int c = ch == 'H' ? H : ch == 'B' ? Bc : ch == '#' ? D : ch == 'W' ? 0x8a6a42 : D;
                put(img, x + ox, y + oy, c);
            }
        }
    }

    private static void paintIcon(byte[] img, ItemDef def, StackData? data)
    {
        string name = def.name;
        // tools
        if (def.tool != null && TOOL_TEMPLATES.TryGetValue(def.tool.Value.type, out var tpl))
        {
            var (H, Bc, D) = materialColors(name);
            drawTemplate(img, tpl, H, Bc, D);
            return;
        }
        // armor
        if (def.armor != null && def.armor.Value.material != "elytra" && ARMOR_TEMPLATES.TryGetValue(def.armor.Value.slot, out var atpl))
        {
            var (H, Bc, D) = materialColors(def.armor.Value.material);
            drawTemplate(img, atpl, H, Bc, D, 1, 3);
            return;
        }
        // block items → mini isometric cube or flat tile
        if (def.block != null && iconAtlas != null)
        {
            int bid = (int)def.block.Value;
            var bdef = blockDefs[bid];
            byte shape = SHAPE_OF[bid];
            HashSet<byte> solidShapes = new() {
                (byte)Shape.cube, (byte)Shape.slab, (byte)Shape.stairs,
                (byte)Shape.piston, (byte)Shape.chest, (byte)Shape.hopper,
                (byte)Shape.cauldron };
            bool flat = !solidShapes.Contains(shape);
            int tile(int face)
            {
                return bdef.texFn != null ? bdef.texFn(0, face) : (bdef.tex.Length == 0 ? 0 : bdef.tex[face]);
            }
            if (flat)
            {
                blitTile(img, tile(2), tintFor(bid));
                return;
            }
            drawIsoCube(img, tile(1), tile(2), tile(5), tintFor(bid));
            return;
        }
        if (paintSpecific(img, name, data)) { return; }
        // generic fallback: rounded blob with hashed hue, category-shaped
        uint h = hashString(name);
        double hue = (double)(h % 360);
        int @base = hsl(hue, 0.45, 0.55);
        int dark = hsl(hue, 0.45, 0.38);
        int light = hsl(hue, 0.50, 0.70);
        if (def.category == "food")
        {
            for (int y = 4; y < 13; y++)
            {
                for (int x = 3; x < 13; x++)
                {
                    double d = (double)((x - 8) * (x - 8)) + ((double)y - 8.5) * ((double)y - 8.5);
                    if (d < 22) put(img, x, y, d < 9 ? light : @base);
                }
            }
            put(img, 8, 3, 0x6a4426);
            put(img, 8, 2, 0x4a7a2c);
        }
        else
        {
            for (int y = 5; y < 12; y++)
            {
                for (int x = 3; x < 13; x++)
                {
                    double ax = Math.Abs((double)x - 8);
                    bool inside = (double)y >= 5 + ax * 0.3 - 1 && (double)y <= 11 - ax * 0.2;
                    if (inside) put(img, x, y, y < 8 ? light : y < 10 ? @base : dark);
                }
            }
        }
    }

    private static int? tintFor(int blockId)
    {
        int t = blockDefs[blockId].tint;
        if (t == 1) return 0x7cbd4f;
        if (t == 2) return 0x59ab30;
        if (t == 3) return 0x3f76e4;
        return null;
    }

    private static void blitTile(byte[] img, int tile, int? tint)
    {
        var pix = tilePixels(tile);
        if (pix == null) return;
        for (int i = 0; i < 256; i++)
        {
            double r = (double)pix[i * 4], g = (double)pix[i * 4 + 1], b = (double)pix[i * 4 + 2];
            if (tint != null)
            {
                r = r * (double)((tint.Value >> 16) & 255) / 255;
                g = g * (double)((tint.Value >> 8) & 255) / 255;
                b = b * (double)(tint.Value & 255) / 255;
            }
            img[i * 4] = (byte)Math.Min(255, r);
            img[i * 4 + 1] = (byte)Math.Min(255, g);
            img[i * 4 + 2] = (byte)Math.Min(255, b);
            img[i * 4 + 3] = pix[i * 4 + 3];
        }
    }

    private static void drawIsoCube(byte[] img, int topTile, int leftTile, int rightTile, int? tint)
    {
        var top = tilePixels(topTile);
        var left = tilePixels(leftTile);
        var right = tilePixels(rightTile);
        if (top == null || left == null || right == null) return;
        int? sample(byte[] pix, double u, double v, double bright, bool useTint)
        {
            int x = Math.Min(15, Math.Max(0, (int)(u * 16)));
            int y = Math.Min(15, Math.Max(0, (int)(v * 16)));
            int i = (y * 16 + x) * 4;
            if (pix[i + 3] < 50) return null;
            double r = (double)pix[i], g = (double)pix[i + 1], b = (double)pix[i + 2];
            if (useTint && tint != null)
            {
                r = r * (double)((tint.Value >> 16) & 255) / 255;
                g = g * (double)((tint.Value >> 8) & 255) / 255;
                b = b * (double)(tint.Value & 255) / 255;
            }
            int ri = (int)Math.Round(r * bright, MidpointRounding.AwayFromZero);
            int gi = (int)Math.Round(g * bright, MidpointRounding.AwayFromZero);
            int bi = (int)Math.Round(b * bright, MidpointRounding.AwayFromZero);
            return (Math.Min(255, ri) << 16) | (Math.Min(255, gi) << 8) | Math.Min(255, bi);
        }
        for (int sy = 0; sy < 16; sy++)
        {
            for (int sx = 0; sx < 16; sx++)
            {
                double fx = (double)sx - 8 + 0.5;
                double fy = (double)sy - 0.5;
                double ty = fy - 4;
                if (Math.Abs(fx) / 8 + Math.Abs(ty) / 4 <= 1)
                {
                    double u = (fx / 8 + (-ty / 4)) / 2 + 0.5;
                    double v = ((-fx / 8) + (-ty / 4)) / 2 + 0.5;
                    var col = sample(top, u, v, 1.0, true);
                    if (col != null)
                    {
                        put(img, sx, sy, col.Value);
                        continue;
                    }
                }
                if (fx >= -8 && fx < 0)
                {
                    double yTop2 = 8 - (-fx) / 8 * 4;
                    double yBot = yTop2 + 8;
                    if (fy >= yTop2 && fy < yBot)
                    {
                        double u = (fx + 8) / 8;
                        double v = (fy - yTop2) / 8;
                        var col = sample(left, u, v, 0.65, false);
                        if (col != null)
                        {
                            put(img, sx, sy, col.Value);
                            continue;
                        }
                    }
                }
                if (fx >= 0 && fx < 8)
                {
                    double yTop2 = 4 + fx / 8 * 4;
                    double yBot = yTop2 + 8;
                    if (fy >= yTop2 && fy < yBot)
                    {
                        double u = fx / 8;
                        double v = (fy - yTop2) / 8;
                        var col = sample(right, u, v, 0.8, false);
                        if (col != null)
                        {
                            put(img, sx, sy, col.Value);
                            continue;
                        }
                    }
                }
            }
        }
    }

    private static bool paintSpecific(byte[] img, string name, StackData? data)
    {
        void px(int x, int y, int c) { put(img, x, y, c); }
        void fill(int x0, int y0, int x1, int y1, int c)
        {
            for (int y = y0; y <= y1; y++) { for (int x = x0; x <= x1; x++) { px(x, y, c); } }
        }
        switch (name)
        {
            case "stick":
                fill(11, 2, 12, 3, 0x8a6a42); fill(9, 4, 10, 5, 0x8a6a42); fill(7, 6, 8, 7, 0x6a4f30);
                fill(5, 8, 6, 9, 0x8a6a42); fill(3, 10, 4, 11, 0x6a4f30);
                return true;
            case "coal":
            case "charcoal": blob(img, name == "coal" ? 0x2e2e2e : 0x3a3026, 0x4a4a4a); return true;
            case "diamond": gem(img, 0x4aedd9, 0x2ca89a, 0xaef8ee); return true;
            case "emerald": gem(img, 0x2cc24e, 0x1a8a36, 0x7ce89a); return true;
            case "lapis_lazuli": blob(img, 0x1e4ca8, 0x3a6ac8); return true;
            case "quartz": gem(img, 0xece6df, 0xb8b0a6, 0xffffff); return true;
            case "amethyst_shard": gem(img, 0x9a72d0, 0x6a4a9e, 0xc8aae8); return true;
            case "echo_shard": gem(img, 0x0c4456, 0x062c3a, 0x2ce8e8); return true;
            case "iron_ingot": ingot(img, 0xe8e8e8, 0xc8c8c8, 0x8a8a8a); return true;
            case "gold_ingot": ingot(img, 0xfcee4b, 0xe8c83c, 0xa8862c); return true;
            case "copper_ingot": ingot(img, 0xe8966a, 0xc06843, 0x8a4a30); return true;
            case "netherite_ingot": ingot(img, 0x5a5054, 0x42383b, 0x2a2326); return true;
            case "netherite_scrap": blob(img, 0x42383b, 0x5e4439); return true;
            case "raw_iron": blob(img, 0xa88a72, 0xc8aa90); return true;
            case "raw_copper": blob(img, 0xa05a3c, 0xc87a52); return true;
            case "raw_gold": blob(img, 0xddaa3e, 0xf8d860); return true;
            case "iron_nugget": smallBlob(img, 0xc8c8c8, 0xe8e8e8); return true;
            case "gold_nugget": smallBlob(img, 0xe8c83c, 0xfcee4b); return true;
            case "redstone": pile(img, 0xcc1500, 0xff2200); return true;
            case "glowstone_dust": pile(img, 0xd8a84a, 0xfcd97a); return true;
            case "gunpowder": pile(img, 0x5a5a5a, 0x7a7a7a); return true;
            case "sugar": pile(img, 0xe8e8e8, 0xffffff); return true;
            case "bone_meal": pile(img, 0xd8d8c8, 0xf0f0e0); return true;
            case "apple": fruit(img, 0xd03030, 0xf05050); return true;
            case "golden_apple":
            case "enchanted_golden_apple": fruit(img, 0xe8c83c, 0xfcee6b); return true;
            case "bread":
                fill(3, 6, 12, 10, 0xa87838); fill(4, 5, 11, 5, 0xc89858); fill(4, 7, 11, 7, 0xc89858);
                return true;
            case "wheat":
                for (int i = 0; i < 4; i++)
                {
                    fill(4 + i * 2, 4, 4 + i * 2, 12, 0xd8c054);
                    px(4 + i * 2, 3, 0xe8d87a);
                }
                return true;
            case "wheat_seeds":
            case "beetroot_seeds":
            case "melon_seeds":
            case "pumpkin_seeds":
            case "torchflower_seeds":
            case "pitcher_pod":
                seeds(img, name == "melon_seeds" ? 0x1c1c1c : name == "pumpkin_seeds" ? 0xe8e0c8 : 0x4a8a2c);
                return true;
            case "egg":
                fill(6, 4, 9, 5, 0xe8dcc0); fill(5, 6, 10, 10, 0xe8dcc0); fill(6, 11, 9, 12, 0xe8dcc0); fill(6, 5, 7, 7, 0xf8f0e0);
                return true;
            case "bone":
                fill(3, 11, 4, 12, 0xe8e8d8); fill(11, 3, 12, 4, 0xe8e8d8);
                for (int i = 0; i < 6; i++) { px(5 + i, 10 - i, 0xd8d8c8); }
                for (int i = 0; i < 6; i++) { px(6 + i, 11 - i, 0xe8e8d8); }
                return true;
            case "string":
                for (int i = 0; i < 10; i++) { px(3 + i, 5 + (int)Math.Floor(Math.Sin((double)i) * 2 + 4), 0xe8e8e8); }
                return true;
            case "feather":
                for (int i = 0; i < 8; i++)
                {
                    px(4 + i, 11 - i, 0xe8e8e8);
                    px(5 + i, 11 - i, 0xd8d8d8);
                }
                px(3, 12, 0xb8b8b8);
                return true;
            case "flint": blob(img, 0x3a3a3a, 0x555555); return true;
            case "leather": fill(4, 5, 11, 11, 0x9a6a42); fill(5, 4, 10, 4, 0x9a6a42); return true;
            case "arrow":
                for (int i = 0; i < 9; i++) { px(11 - i, 4 + i, 0x8a6a42); }
                px(12, 3, 0xd8d8d8); px(11, 4, 0xd8d8d8); px(3, 12, 0xe8e8e8); px(4, 11, 0xe8e8e8);
                return true;
            case "bow": arc(img); return true;
            case "ender_pearl": orb(img, 0x1c5c50, 0x39e8c8); return true;
            case "ender_eye": orb(img, 0x39e8c8, 0x1c5c50); return true;
            case "blaze_rod":
                for (int i = 0; i < 10; i++)
                {
                    px(10 - i / 2, 3 + i, 0xe8c23c);
                    px(11 - i / 2, 3 + i, 0xd8a02c);
                }
                return true;
            case "blaze_powder": pile(img, 0xe8901c, 0xfcb83c); return true;
            case "ghast_tear": gem(img, 0xe8e8f0, 0xb8b8c8, 0xffffff); return true;
            case "slime_ball": orb(img, 0x6fc05c, 0x84d46e); return true;
            case "magma_cream": orb(img, 0xc8742c, 0xe8a23c); return true;
            case "snowball": orb(img, 0xe8f0f0, 0xffffff); return true;
            case "clay_ball": orb(img, 0x9aa3b3, 0xb8c0cc); return true;
            case "brick": ingot(img, 0xb87058, 0x96604f, 0x6a4438); return true;
            case "nether_brick": ingot(img, 0x3c2228, 0x2c171b, 0x1a0d10); return true;
            case "paper": fill(4, 3, 11, 12, 0xf0f0f0); fill(4, 3, 5, 12, 0xd8d8d8); return true;
            case "book": bookIcon(img, 0x8a4a2c); return true;
            case "enchanted_book":
                bookIcon(img, 0x8a4a2c);
                px(5, 4, 0xd667e8); px(10, 6, 0xd667e8); px(7, 9, 0xe89af0);
                return true;
            case "writable_book": bookIcon(img, 0x6a4a8a); return true;
            case "compass":
            case "recovery_compass":
                orb(img, 0x5a5a62, 0x8a8a92);
                px(8, 6, name == "compass" ? 0xe83a3a : 0x2ce8e8); px(8, 7, 0xe8e8e8);
                return true;
            case "clock": orb(img, 0xe8c83c, 0xa8862c); px(8, 7, 0x3a3a3a); px(9, 8, 0x3a3a3a); return true;
            case "bucket": bucketIcon(img, null); return true;
            case "water_bucket": bucketIcon(img, 0x3f76e4); return true;
            case "lava_bucket": bucketIcon(img, 0xe85d10); return true;
            case "milk_bucket": bucketIcon(img, 0xf0f0f0); return true;
            case "powder_snow_bucket": bucketIcon(img, 0xe8f0f0); return true;
            case "cod_bucket":
            case "salmon_bucket":
            case "pufferfish_bucket":
            case "tropical_fish_bucket":
            case "axolotl_bucket":
            case "tadpole_bucket":
                bucketIcon(img, 0x3f76e4);
                px(8, 5, name == "salmon_bucket" ? 0xa84a3a : name == "pufferfish_bucket" ? 0xd8b83c : name == "axolotl_bucket" ? 0xf0a8c8 : 0x8a7a5c);
                return true;
            case "potion":
            case "splash_potion":
            case "lingering_potion":
                {
                    var pot = potionDef(data?.potion ?? "water");
                    bottleIcon(img, pot.color);
                    return true;
                }
            case "glass_bottle": bottleIcon(img, null); return true;
            case "experience_bottle": bottleIcon(img, 0x7ce84a); return true;
            case "honey_bottle": bottleIcon(img, 0xf0a83c); return true;
            case "dragon_breath": bottleIcon(img, 0xc84ae8); return true;
            case "rotten_flesh": blob(img, 0x8a5a3c, 0x6a9a5a); return true;
            case "spider_eye": orb(img, 0x8a1c2c, 0xc83a4a); px(8, 7, 0x3c0a12); return true;
            case "fermented_spider_eye": orb(img, 0x9a6a8a, 0xc898b8); px(8, 7, 0x3c0a12); return true;
            case "shield": shieldIcon(img); return true;
            case "elytra": elytraIcon(img); return true;
            case "totem_of_undying": totemIcon(img); return true;
            case "nether_star": starIcon(img, 0xf8f8d8); return true;
            case "ender_chest": return false;
            case "firework_rocket": rocketIcon(img); return true;
            case "name_tag": fill(4, 6, 11, 10, 0xd8c898); px(5, 7, 0x8a7a52); fill(3, 7, 3, 9, 0x8a7a52); return true;
            case "lead":
                for (int i = 0; i < 8; i++) { px(4 + i, 4 + (int)Math.Floor(Math.Sin((double)i * 0.8) * 2) + 4, 0xb89868); }
                return true;
            case "saddle":
                fill(4, 6, 11, 8, 0x8a4a2c); fill(5, 5, 10, 5, 0x6a3a20); px(4, 9, 0xe8c83c); px(11, 9, 0xe8c83c);
                return true;
            case "shulker_shell": fill(4, 5, 11, 9, 0x976797); fill(5, 4, 10, 4, 0xb89ab8); fill(5, 10, 10, 10, 0x7a527a); return true;
            case "nautilus_shell": orb(img, 0xd8c8b8, 0xb89a88); px(7, 7, 0x8a6a58); px(9, 7, 0x8a6a58); return true;
            case "heart_of_the_sea": orb(img, 0x1c8ac8, 0x3ab8e8); return true;
            case "prismarine_shard": gem(img, 0x6fa495, 0x4a7468, 0x9ac8b8); return true;
            case "prismarine_crystals": gem(img, 0xcdebe2, 0x9ac0b4, 0xffffff); return true;
            case "ink_sac": blob(img, 0x1c1c28, 0x3a3a4a); return true;
            case "glow_ink_sac": blob(img, 0x1c5c5c, 0x2ce8e8); return true;
            case "scute": blob(img, 0x47702e, 0x6a9a4c); return true;
            case "honeycomb":
                fill(4, 4, 11, 11, 0xe89a2c);
                px(6, 6, 0xc87818); px(9, 6, 0xc87818); px(6, 9, 0xc87818); px(9, 9, 0xc87818);
                return true;
            case "rabbit_hide": fill(4, 5, 11, 11, 0xb8966a); fill(5, 4, 7, 4, 0xb8966a); return true;
            case "rabbit_foot": fill(6, 3, 9, 11, 0xb8966a); fill(5, 10, 10, 12, 0xa8865a); return true;
            case "phantom_membrane": fill(4, 4, 11, 11, 0xb8c8d8); px(6, 6, 0x8aa0b8); px(9, 8, 0x8aa0b8); return true;
            case "fire_charge": orb(img, 0x3a1c0c, 0xe85d10); px(7, 5, 0xfcb83c); return true;
            case "bowl": fill(4, 8, 11, 10, 0x8a6a42); fill(5, 11, 10, 11, 0x6a4f30); return true;
            case "flint_and_steel": fill(3, 8, 6, 11, 0x3a3a3a); fill(9, 4, 12, 7, 0xc8c8c8); px(8, 8, 0xfcb83c); return true;
            case "shears":
                fill(5, 3, 6, 8, 0xc8c8c8); fill(9, 3, 10, 8, 0xc8c8c8);
                fill(4, 9, 6, 11, 0x8a3a3a); fill(9, 9, 11, 11, 0x8a3a3a);
                return true;
            case "fishing_rod":
                for (int i = 0; i < 9; i++) { px(3 + i, 12 - i, 0x8a6a42); }
                for (int i = 0; i < 5; i++) { px(12, 4 + i, 0xd8d8d8); }
                px(12, 9, 0x8a8a8a);
                return true;
            case "carrot_on_a_stick":
            case "warped_fungus_on_a_stick":
                for (int i = 0; i < 8; i++) { px(3 + i, 12 - i, 0x8a6a42); }
                {
                    int c = name.StartsWith("carrot") ? 0xe87a2c : 0x14a8a8;
                    px(12, 5, c); px(12, 6, c);
                }
                return true;
            case "spyglass": fill(5, 8, 7, 10, 0xc06843); fill(8, 5, 10, 7, 0x7a5c34); px(11, 4, 0xaee8f8); return true;
            case "goat_horn":
                for (int i = 0; i < 8; i++)
                {
                    px(4 + i, 11 - (int)Math.Floor((double)i * 0.8), 0xd8d0c4);
                    px(4 + i, 12 - (int)Math.Floor((double)i * 0.8), 0xb8b0a4);
                }
                return true;
            case "brush": fill(7, 3, 8, 7, 0x8a6a42); fill(6, 8, 9, 11, 0xc8a868); fill(6, 12, 9, 12, 0xe8d8a8); return true;
            case "end_crystal": gem(img, 0xe8a8e8, 0xc84ae8, 0xf8e0f8); return true;
            case "trident":
                for (int i = 0; i < 9; i++) { px(8, 4 + i, 0x2c8a7a); }
                px(6, 4, 0x2c8a7a); px(10, 4, 0x2c8a7a); px(6, 5, 0x2c8a7a); px(10, 5, 0x2c8a7a);
                px(6, 3, 0x4ab8a8); px(8, 3, 0x4ab8a8); px(10, 3, 0x4ab8a8);
                return true;
            case "crossbow":
                fill(4, 6, 11, 7, 0x6a4f30); fill(7, 4, 8, 11, 0x8a6a42); px(4, 5, 0xd8d8d8); px(11, 5, 0xd8d8d8);
                return true;
            case "spectral_arrow":
                for (int i = 0; i < 9; i++) { px(11 - i, 4 + i, 0xd8c054); }
                px(12, 3, 0xfcee9a);
                return true;
            case "tipped_arrow":
                for (int i = 0; i < 9; i++) { px(11 - i, 4 + i, 0x8a6a42); }
                px(12, 3, 0xc84ae8); px(11, 4, 0xc84ae8);
                return true;
            case "chorus_fruit":
            case "popped_chorus_fruit": blob(img, name == "chorus_fruit" ? 0x6a4a7a : 0x9a7ab0, 0xb796c8); return true;
            case "nether_wart": blob(img, 0x71080a, 0xa61415); return true;
            case "glistering_melon_slice": melonSlice(img, 0xe8c83c); return true;
            case "melon_slice": melonSlice(img, 0xd83030); return true;
            case "cocoa_beans": seeds(img, 0x8a5a2c); return true;
            case "sweet_berries": seeds(img, 0xd03048); return true;
            case "glow_berries": seeds(img, 0xffb83c); return true;
            case "cookie":
                orb(img, 0xb8824a, 0xd8a86a);
                px(6, 6, 0x4a2c14); px(9, 8, 0x4a2c14); px(7, 9, 0x4a2c14);
                return true;
            case "cake": return false;
            case "music_disc_wander":
            case "music_disc_aurora":
            case "music_disc_descent":
                orb(img, 0x1c1c1c, 0x3a3a3a);
                px(8, 7, name.EndsWith("wander") ? 0x4ae04a : name.EndsWith("aurora") ? 0x4a8ae8 : 0xe84a8a);
                px(7, 8, 0xc8c8c8);
                return true;
            default:
                break;
        }
        if (name.EndsWith("_dye"))
        {
            string colorName = name.Substring(0, name.Length - 4);
            if (COLOR_RGB.TryGetValue(colorName, out var col))
            {
                pile(img, (int)col, lighten((int)col));
                return true;
            }
        }
        if (name.EndsWith("_spawn_egg"))
        {
            uint h = hashString(name);
            int c1 = hsl((double)(h % 360), 0.50, 0.55);
            int c2 = hsl((double)((h >> 8) % 360), 0.55, 0.40);
            for (int y = 3; y < 13; y++)
            {
                for (int x = 4; x < 12; x++)
                {
                    double dx = ((double)x - 7.5) / 3.6, dy = ((double)y - 8) / 4.6;
                    if (dx * dx + dy * dy < 1)
                    {
                        put(img, x, y, hash2(h, x, y) % 5 == 0 ? c2 : c1);
                    }
                }
            }
            return true;
        }
        if (name.Contains("_boat") || name.Contains("_raft"))
        {
            void fill2(int x0, int y0, int x1, int y1, int c)
            {
                for (int y = y0; y <= y1; y++) { for (int x = x0; x <= x1; x++) { put(img, x, y, c); } }
            }
            fill2(3, 7, 12, 9, 0x9a6b35); fill2(4, 10, 11, 11, 0x7a5226); fill2(4, 6, 5, 6, 0x9a6b35); fill2(10, 6, 11, 6, 0x9a6b35);
            if (name.Contains("chest")) { fill2(6, 4, 9, 6, 0x8a5c2c); }
            return true;
        }
        if (name.Contains("minecart"))
        {
            fill(3, 6, 12, 11, 0x6a6a72); fill(4, 5, 11, 5, 0x8a8a92); fill(4, 7, 11, 10, 0x3a3a3e);
            if (name.StartsWith("chest")) { fill(5, 4, 10, 8, 0x9a6b35); }
            if (name.StartsWith("furnace")) { fill(5, 4, 10, 8, 0x7d7d7d); }
            if (name.StartsWith("tnt")) { fill(5, 4, 10, 8, 0xc8412e); }
            if (name.StartsWith("hopper")) { fill(5, 4, 10, 8, 0x4a4a4e); }
            return true;
        }
        if (name.EndsWith("_sign") || name.EndsWith("_hanging_sign"))
        {
            fill(3, 4, 12, 9, 0xb8945f); fill(7, 10, 8, 13, 0x8a6a42);
            return true;
        }
        if (name.EndsWith("_door"))
        {
            bool iron = name.StartsWith("iron");
            fill(4, 2, 11, 13, iron ? 0xc8c8c8 : 0x9a6b35);
            fill(5, 3, 10, 6, iron ? 0xa8a8a8 : 0x7a5226);
            fill(5, 9, 10, 12, iron ? 0xa8a8a8 : 0x7a5226);
            return true;
        }
        if (name.EndsWith("_bed"))
        {
            int col = (int)(COLOR_RGB.TryGetValue(name.Substring(0, name.Length - 4), out var bc) ? bc : 0xb02e26u);
            fill(2, 7, 13, 9, col); fill(2, 6, 5, 6, 0xf0f0f0); fill(2, 10, 13, 11, 0x8a6a42);
            return true;
        }
        if (name.EndsWith("pottery_sherd"))
        {
            fill(4, 5, 11, 11, 0x9a5838); px(5, 4, 0x9a5838); px(10, 4, 0x9a5838);
            px(7, 7, 0x5a3220); px(8, 8, 0x5a3220);
            return true;
        }
        if (name.EndsWith("armor_trim") || name == "netherite_upgrade")
        {
            fill(4, 3, 11, 12, 0x3a5c5c); fill(5, 4, 10, 11, 0x4a7474); px(7, 6, 0x8ac8c8); px(8, 8, 0x8ac8c8);
            return true;
        }
        return false;
    }

    private static int lighten(int c)
    {
        int r = Math.Min(255, ((c >> 16) & 255) + 50), g = Math.Min(255, ((c >> 8) & 255) + 50), b = Math.Min(255, (c & 255) + 50);
        return (r << 16) | (g << 8) | b;
    }

    private static void blob(byte[] img, int @base, int light)
    {
        for (int y = 4; y < 13; y++)
        {
            for (int x = 3; x < 13; x++)
            {
                double dx = ((double)x - 8) / 4.6, dy = ((double)y - 8.5) / 4.2;
                double d = dx * dx + dy * dy;
                if (d < 1) put(img, x, y, d < 0.4 ? light : @base);
            }
        }
    }

    private static void smallBlob(byte[] img, int @base, int light)
    {
        for (int y = 6; y < 11; y++)
        {
            for (int x = 6; x < 11; x++)
            {
                double dx = ((double)x - 8) / 2.4, dy = ((double)y - 8.5) / 2.2;
                double d = dx * dx + dy * dy;
                if (d < 1) put(img, x, y, d < 0.4 ? light : @base);
            }
        }
    }

    private static void gem(byte[] img, int @base, int dark, int light)
    {
        for (int y = 4; y < 12; y++)
        {
            double hw = y < 7 ? (double)(y - 3) * 1.6 : (double)(12 - y) * 1.4;
            int x = (int)Math.Ceiling(8 - hw);
            while ((double)x <= 7 + hw)
            {
                put(img, x, y, y < 6 ? light : y < 9 ? @base : dark);
                x += 1;
            }
        }
    }

    private static void ingot(byte[] img, int light, int @base, int dark)
    {
        for (int i = 0; i < 3; i++)
        {
            put(img, 5 - i + 3, 6 + i, light);
        }
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                put(img, 3 + x + (3 - (int)Math.Floor((double)y * 0.8)), 7 + y, y == 0 ? light : y < 3 ? @base : dark);
            }
        }
    }

    private static void pile(byte[] img, int @base, int light)
    {
        for (int y = 8; y < 13; y++)
        {
            double hw = (double)(y - 7) * 1.2;
            int x = (int)Math.Ceiling(8 - hw);
            while ((double)x <= 7 + hw)
            {
                put(img, x, y, (x + y) % 3 != 0 ? @base : light);
                x += 1;
            }
        }
        put(img, 7, 7, light);
        put(img, 8, 6, @base);
    }

    private static void seeds(byte[] img, int c)
    {
        foreach (var (x, y) in new[] { (5, 6), (9, 5), (7, 8), (10, 9), (5, 10), (8, 11) })
        {
            put(img, x, y, c);
            put(img, x + 1, y, c);
            put(img, x, y + 1, c);
        }
    }

    private static void fruit(byte[] img, int @base, int light)
    {
        for (int y = 5; y < 13; y++)
        {
            for (int x = 4; x < 12; x++)
            {
                double dx = ((double)x - 8) / 3.8, dy = ((double)y - 9) / 3.8;
                if (dx * dx + dy * dy < 1) put(img, x, y, x < 7 && y < 9 ? light : @base);
            }
        }
        put(img, 8, 4, 0x6a4426);
        put(img, 9, 3, 0x4a7a2c);
        put(img, 10, 3, 0x4a7a2c);
    }

    private static void orb(byte[] img, int @base, int light)
    {
        for (int y = 4; y < 13; y++)
        {
            for (int x = 4; x < 13; x++)
            {
                double dx = ((double)x - 8) / 4.2, dy = ((double)y - 8) / 4.2;
                double d = dx * dx + dy * dy;
                if (d < 1) put(img, x, y, d < 0.35 && x < 8 && y < 8 ? light : @base);
            }
        }
    }

    private static void arc(byte[] img)
    {
        for (int i = 0; i < 10; i++)
        {
            double a = (double)i / 9 * Math.PI / 2 + Math.PI * 0.75;
            put(img, (int)Math.Round(8 + Math.Cos(a) * 5.5, MidpointRounding.AwayFromZero), (int)Math.Round(8 + Math.Sin(a) * 5.5, MidpointRounding.AwayFromZero), 0x8a6a42);
            put(img, (int)Math.Round(8 + Math.Cos(a) * 4.5, MidpointRounding.AwayFromZero), (int)Math.Round(8 + Math.Sin(a) * 4.5, MidpointRounding.AwayFromZero), 0x6a4f30);
        }
        for (int i = 0; i < 9; i++) { put(img, 4 + i, 4 + i, 0xe8e8e8); }
    }

    private static void bookIcon(byte[] img, int cover)
    {
        for (int y = 3; y < 13; y++) { for (int x = 4; x < 12; x++) { put(img, x, y, cover); } }
        for (int y = 4; y < 12; y++)
        {
            put(img, 11, y, 0xe8e0c8);
            put(img, 10, y, 0xd8d0b8);
        }
        for (int y = 3; y < 13; y++) { put(img, 5, y, cover); }
    }

    private static void bucketIcon(byte[] img, int? contents)
    {
        for (int i = 0; i < 8; i++) { put(img, 4 + i, 5, 0x8a8a8a); }
        for (int y = 6; y < 12; y++)
        {
            int inset = (int)Math.Floor((double)(y - 6) * 0.4);
            put(img, 4 + inset, y, 0xa8a8a8);
            put(img, 11 - inset, y, 0x787878);
            for (int x = 5 + inset; x < 11 - inset; x++) { put(img, x, y, 0x989898); }
        }
        if (contents != null)
        {
            for (int x = 5; x < 11; x++) { put(img, x, 5, contents.Value); }
            for (int x = 6; x < 10; x++) { put(img, x, 6, contents.Value); }
        }
        put(img, 3, 5, 0x787878);
        put(img, 12, 5, 0x787878);
    }

    private static void bottleIcon(byte[] img, int? contents)
    {
        put(img, 7, 2, 0xb8b8c8); put(img, 8, 2, 0xb8b8c8);
        put(img, 7, 3, 0xd8d8e8); put(img, 8, 3, 0xd8d8e8);
        for (int y = 4; y < 13; y++)
        {
            int hw = y < 6 ? 1 : 3;
            for (int x = 8 - hw; x <= 7 + hw; x++)
            {
                bool edge = x == 8 - hw || x == 7 + hw || y == 12;
                if (edge) { put(img, x, y, 0xd8d8e8); }
                else if (contents != null && y >= 6) { put(img, x, y, contents.Value); }
            }
        }
    }

    private static void shieldIcon(byte[] img)
    {
        for (int y = 3; y < 13; y++)
        {
            int hw = y < 9 ? 4 : 4 - (y - 9);
            for (int x = 8 - hw; x <= 7 + hw; x++)
            {
                put(img, x, y, (x + y) % 2 != 0 ? 0x8a6a42 : 0x9a7a52);
            }
        }
        for (int y = 3; y < 8; y++) { put(img, 7, y, 0xc8c8c8); }
    }

    private static void elytraIcon(byte[] img)
    {
        for (int i = 0; i < 6; i++)
        {
            for (int w = 0; w <= i; w++)
            {
                put(img, 6 - i + w, 4 + i, 0xd8d8e8);
                put(img, 9 + i - w, 4 + i, 0xb8b8d0);
            }
        }
        for (int i = 0; i < 4; i++)
        {
            put(img, 3 + i, 10 + i, 0xa8a8c0);
            put(img, 12 - i, 10 + i, 0x8a8aa8);
        }
    }

    private static void totemIcon(byte[] img)
    {
        put(img, 7, 3, 0xe8c83c); put(img, 8, 3, 0xe8c83c);
        put(img, 6, 4, 0xe8c83c); put(img, 9, 4, 0xe8c83c);
        put(img, 7, 4, 0x4ae04a); put(img, 8, 4, 0x4ae04a);
        for (int y = 5; y < 9; y++) { for (int x = 6; x < 10; x++) { put(img, x, y, 0xe8c83c); } }
        put(img, 4, 6, 0xe8c83c); put(img, 5, 6, 0xe8c83c); put(img, 10, 6, 0xe8c83c); put(img, 11, 6, 0xe8c83c);
        for (int y = 9; y < 12; y++)
        {
            put(img, 7, y, 0xc8a82c);
            put(img, 8, y, 0xc8a82c);
        }
    }

    private static void starIcon(byte[] img, int c)
    {
        put(img, 8, 3, c); put(img, 8, 4, c);
        for (int i = 0; i < 5; i++)
        {
            put(img, 4 + i, 8, c);
            put(img, 8 + i, 8, c);
        }
        put(img, 8, 12, c); put(img, 8, 11, c);
        for (int y = 5; y < 11; y++)
        {
            for (int x = 6; x < 11; x++)
            {
                if (Math.Abs(x - 8) + Math.Abs(y - 8) < 3) { put(img, x, y, c); }
            }
        }
    }

    private static void rocketIcon(byte[] img)
    {
        for (int y = 3; y < 10; y++)
        {
            put(img, 7, y, 0xd8d8d8);
            put(img, 8, y, 0xc8c8c8);
        }
        put(img, 7, 2, 0xc84040); put(img, 8, 2, 0xc84040);
        for (int i = 0; i < 4; i++) { put(img, 7 + (i % 2), 10 + i, 0x8a6a42); }
    }

    private static void melonSlice(byte[] img, int c)
    {
        for (int y = 4; y < 12; y++)
        {
            int hw = Math.Min(5, y - 3);
            for (int x = 8 - hw; x <= 7 + hw; x++)
            {
                put(img, x, y, y == 11 || Math.Abs((double)x - 7.5) > (double)hw - 1.5 ? 0x5a8c1a : c);
            }
        }
        put(img, 7, 6, 0x1c1c1c);
        put(img, 9, 8, 0x1c1c1c);
    }
}

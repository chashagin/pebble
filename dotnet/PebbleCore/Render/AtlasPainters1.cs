// Atlas painters part 1 — terrain, stone families, snow/ice, nether, end,
// liquids, glass, wool/colored, woods, ores, metals, copper, prismarine,
// corals. Procedural painter registrations.
//
// Ported from Sources/PebbleCore/Render/AtlasPainters1.swift.
//
// Defines static class `AtlasPainters1Globals` holding the module-level lets
// (C_STONE..C_OAK, WOOD_COLORS, LEAF_COLORS, ORE_COLORS) and free funcs
// (colorRGB, crack, metalBlock, registerPainters1, liveCoralPaint), plus the
// value type `WoodColor`. `registerPainters1` is called UNQUALIFIED from
// AtlasGlobals.ensurePainters(), so surface via:
//   global using static PebbleCore.AtlasPainters1Globals;
//
// External symbols referenced unqualified (already surfaced elsewhere):
//   - T, BuiltAtlas, shade, mixC, p, rp, paintInto (AtlasGlobals + types)
//   - detRound (TerrainGlobals), COLOR_RGB lives on BlockData (qualified).

using System.Collections.Generic;

namespace PebbleCore;

public struct WoodColor
{
    public readonly int plank;
    public readonly int bark;
    public readonly int inner;
    public WoodColor(int plank, int bark, int inner)
    {
        this.plank = plank;
        this.bark = bark;
        this.inner = inner;
    }
}

public static class AtlasPainters1Globals
{
    public const int C_STONE = 0x7d7d7d, C_DEEPSLATE = 0x4c4c50, C_DIRT = 0x866043, C_GRASS_GRAY = 0x9b9b9b;
    public const int C_SAND = 0xdbd3a0, C_RED_SAND = 0xbe6721, C_GRAVEL = 0x84807b, C_NETHER = 0x6e3533;
    public const int C_END = 0xdbde9e, C_WATER = 0x3f76e4, C_LAVA = 0xd45a12, C_OAK = 0xb8945f;

    public static readonly Dictionary<string, WoodColor> WOOD_COLORS = new Dictionary<string, WoodColor>
    {
        ["oak"] = new WoodColor(0xb8945f, 0x6e5530, 0xc29d62),
        ["spruce"] = new WoodColor(0x82613a, 0x3a2817, 0x826034),
        ["birch"] = new WoodColor(0xd7cb8d, 0xdfdcd5, 0xc8b77a),
        ["jungle"] = new WoodColor(0xb88764, 0x585235, 0xb88764),
        ["acacia"] = new WoodColor(0xba6337, 0x676157, 0xb05d3c),
        ["dark_oak"] = new WoodColor(0x4f3218, 0x342817, 0x53381a),
        ["mangrove"] = new WoodColor(0x773934, 0x583c2e, 0x7d3d36),
        ["cherry"] = new WoodColor(0xe2b3ab, 0x35201d, 0xe7c0b8),
        ["bamboo"] = new WoodColor(0xc9b863, 0x80a23a, 0xc9b863),
        ["crimson"] = new WoodColor(0x693a4e, 0x4b2737, 0x7e3a56),
        ["warped"] = new WoodColor(0x2b6963, 0x39283d, 0x2b6963),
    };
    public static readonly Dictionary<string, int> LEAF_COLORS = new Dictionary<string, int>
    {
        ["oak"] = 0x59ab30, ["spruce"] = 0x53814f, ["birch"] = 0x77a44c, ["jungle"] = 0x4aa520,
        ["acacia"] = 0x659129, ["dark_oak"] = 0x3f7a1e, ["mangrove"] = 0x4a9d2e, ["cherry"] = 0xf1b3c4,
        ["azalea"] = 0x5e7c2a, ["flowering_azalea"] = 0x5e7c2a,
    };
    public static readonly Dictionary<string, int> ORE_COLORS = new Dictionary<string, int>
    {
        ["coal"] = 0x2e2e2e, ["iron"] = 0xd8af93, ["copper"] = 0xc06843, ["gold"] = 0xfcee4b,
        ["redstone"] = 0xfc0a0a, ["lapis"] = 0x1e4ca8, ["diamond"] = 0x4aedd9, ["emerald"] = 0x17c544,
    };

    public static int colorRGB(string name, int def)
    {
        if (name != null && BlockData.COLOR_RGB.TryGetValue(name, out uint c)) { return (int)c; }
        return def;
    }

    public static void crack(T t, int c)
    {
        int x = 3 + (int)(t.rand(0, 99) * 8), y = 1;
        while (y < 15)
        {
            t.set(x, y, c);
            x += (int)(t.rand(x, y, 9) * 3) - 1;
            y += t.rand(x, y, 10) < 0.7 ? 1 : 0;
            x = Math.Max(1, Math.Min(14, x));
            y += 1;
        }
    }

    public static void metalBlock(T t, int c)
    {
        t.blotch(c, 0.04, 5);
        t.border(shade(c, 0.8));
        t.hline(1, 1, 14, shade(c, 1.15));
        t.vline(1, 1, 14, shade(c, 1.1));
    }

    public static void registerPainters1()
    {
        // --- terrain ---
        p("stone", t => { t.blotch(C_STONE, 0.13); });
        p("cobblestone", t =>
        {
            t.blotch(shade(C_STONE, 0.94), 0.1);
            for (int i = 0; i < 9; i++)
            {
                int cx = (i % 3) * 5 + 1 + (int)(t.rand(i, 0) * 2);
                int cy = (i / 3) * 5 + 1 + (int)(t.rand(i, 1) * 2);
                double tone = 0.8 + t.rand(i, 2) * 0.45;
                for (int dy = 0; dy < 4; dy++)
                {
                    for (int dx = 0; dx < 4; dx++)
                    {
                        if ((dx == 0 || dx == 3) && (dy == 0 || dy == 3)) { continue; }
                        t.set(cx + dx, cy + dy, shade(C_STONE, tone * (0.92 + t.rand(cx + dx, cy + dy) * 0.16)));
                    }
                }
            }
        });
        p("mossy_cobblestone", t =>
        {
            paintInto(t, "cobblestone");
            for (int i = 0; i < 26; i++)
            {
                int x = (int)(t.rand(i, 5) * 16), y = (int)(t.rand(i, 6) * 16);
                t.set(x, y, shade(0x5d7e43, 0.85 + t.rand(x, y) * 0.3));
                if (t.rand(i, 7) < 0.5) { t.set(x + 1, y, 0x5d7e43); }
            }
        });
        p("deepslate", t =>
        {
            t.blotch(C_DEEPSLATE, 0.12);
            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    if ((x + y * 3) % 7 == 0 && t.rand(x, y) < 0.4)
                    {
                        t.set(x, y, shade(C_DEEPSLATE, 0.85));
                    }
                }
            }
        });
        p("deepslate_top", t => { t.blotch(shade(C_DEEPSLATE, 1.05), 0.1); });
        p("cobbled_deepslate", t => { t.blotch(shade(C_DEEPSLATE, 0.9), 0.16, 3); });
        p("polished_deepslate", t => { t.blotch(shade(C_DEEPSLATE, 1.02), 0.06); t.border(shade(C_DEEPSLATE, 0.8)); });
        p("deepslate_bricks", t => { t.bricks(shade(C_DEEPSLATE, 1.02), shade(C_DEEPSLATE, 0.7), 8, 4); });
        p("cracked_deepslate_bricks", t => { t.bricks(shade(C_DEEPSLATE, 1.02), shade(C_DEEPSLATE, 0.7), 8, 4); crack(t, shade(C_DEEPSLATE, 0.5)); });
        p("deepslate_tiles", t => { t.bricks(shade(C_DEEPSLATE, 0.95), shade(C_DEEPSLATE, 0.65), 4, 4); });
        p("cracked_deepslate_tiles", t => { t.bricks(shade(C_DEEPSLATE, 0.95), shade(C_DEEPSLATE, 0.65), 4, 4); crack(t, shade(C_DEEPSLATE, 0.5)); });
        p("chiseled_deepslate", t => { t.blotch(C_DEEPSLATE, 0.07); t.border(shade(C_DEEPSLATE, 0.75)); t.rect(4, 4, 11, 11, shade(C_DEEPSLATE, 0.85)); t.rect(5, 5, 10, 10, shade(C_DEEPSLATE, 1.1)); });
        p("reinforced_deepslate_side", t => { t.blotch(C_DEEPSLATE, 0.08); t.border(shade(0x9a9573, 0.9)); t.rect(3, 3, 12, 12, shade(C_DEEPSLATE, 0.9)); t.rect(6, 6, 9, 9, 0x9a9573); });
        p("reinforced_deepslate_top", t => { t.blotch(C_DEEPSLATE, 0.08); t.border(0x9a9573); t.rect(5, 5, 10, 10, shade(0x9a9573, 0.8)); });
        p("reinforced_deepslate_bottom", t => { t.blotch(shade(C_DEEPSLATE, 0.9), 0.08); });

        p("granite", t => { t.blotch(0x9a6b59, 0.14, 3); t.speckle(0xb98e7a, 16); t.speckle(0x6b4a3e, 10); });
        p("polished_granite", t => { t.blotch(0x9a6b59, 0.07); t.border(shade(0x9a6b59, 0.82)); });
        p("diorite", t => { t.blotch(0xc2c2c4, 0.13, 3); t.speckle(0x8e8e90, 22); });
        p("polished_diorite", t => { t.blotch(0xc6c6c8, 0.06); t.border(shade(0xc6c6c8, 0.82)); });
        p("andesite", t => { t.blotch(0x888a85, 0.12, 3); t.speckle(0x6e706b, 14); });
        p("polished_andesite", t => { t.blotch(0x8c8e89, 0.06); t.border(shade(0x8c8e89, 0.82)); });
        p("tuff", t => { t.blotch(0x6d6e66, 0.13, 3); t.speckle(0x84857c, 12); });
        p("calcite", t => { t.blotch(0xdfe0dc, 0.08, 3); });
        p("dripstone_block", t => { t.blotch(0x866956, 0.14, 3); });
        p("smooth_stone", t => { t.blotch(0xa0a0a0, 0.05); t.border(shade(0xa0a0a0, 0.85)); });
        p("stone_bricks", t => { t.bricks(C_STONE, shade(C_STONE, 0.62)); });
        p("mossy_stone_bricks", t =>
        {
            t.bricks(C_STONE, shade(C_STONE, 0.62));
            for (int i = 0; i < 30; i++)
            {
                int x = (int)(t.rand(i, 5) * 16), y = (int)(t.rand(i, 6) * 16);
                t.set(x, y, 0x5d7e43);
            }
        });
        p("cracked_stone_bricks", t => { t.bricks(C_STONE, shade(C_STONE, 0.62)); crack(t, shade(C_STONE, 0.5)); });
        p("chiseled_stone_bricks", t => { t.blotch(C_STONE, 0.06); t.border(shade(C_STONE, 0.7)); t.disc(7.5, 7.5, 5, shade(C_STONE, 0.85)); t.disc(7.5, 7.5, 3.4, shade(C_STONE, 1.12)); });
        p("bricks", t => { t.bricks(0x96604f, 0xa8a8a8, 8, 4); });
        p("bedrock", t => { t.blotch(0x565656, 0.35, 3); });
        p("obsidian", t => { t.blotch(0x100c1c, 0.18, 4); t.speckle(0x3c2e63, 7); });
        p("crying_obsidian", t => { t.blotch(0x140d24, 0.18, 4); t.speckle(0x7a3ee8, 10); t.speckle(0xb47ef8, 4); });
        p("dirt", t => { t.blotch(C_DIRT, 0.15, 3); t.speckle(shade(C_DIRT, 0.7), 12); t.speckle(shade(C_DIRT, 1.3), 8); });
        p("coarse_dirt", t => { t.blotch(shade(C_DIRT, 0.92), 0.16, 3); t.speckle(0x7d7d7d, 9); });
        p("rooted_dirt", t =>
        {
            t.blotch(C_DIRT, 0.14, 3);
            for (int i = 0; i < 5; i++)
            {
                int x = (int)(t.rand(i, 0) * 14) + 1;
                t.vline(x, (int)(t.rand(i, 1) * 8), (int)(t.rand(i, 1) * 8) + 5, 0xa3835c);
            }
        });
        p("grass_top", t => { t.blotch(C_GRASS_GRAY, 0.16, 3); }); // tinted in shader
        p("grass_side", t =>
        {
            paintInto(t, "dirt");
            for (int x = 0; x < 16; x++)
            {
                int h = 2 + (int)(t.rand(x, 0) * 2);
                for (int y = 0; y < h; y++) { t.set(x, y, shade(0x7cbd4f, 0.9 + t.rand(x, y) * 0.25)); }
            }
        });
        p("podzol_top", t => { t.blotch(0x7a5b34, 0.15, 3); t.speckle(0x8c6a3f, 14); });
        p("podzol_side", t =>
        {
            paintInto(t, "dirt");
            for (int x = 0; x < 16; x++)
            {
                int h = 2 + (int)(t.rand(x, 0) * 2);
                for (int y = 0; y < h; y++) { t.set(x, y, shade(0x7a5b34, 0.9 + t.rand(x, y) * 0.2)); }
            }
        });
        p("mycelium_top", t => { t.blotch(0x6f6265, 0.14, 3); t.speckle(0x9a8e92, 16); });
        p("mycelium_side", t =>
        {
            paintInto(t, "dirt");
            for (int x = 0; x < 16; x++)
            {
                int h = 2 + (int)(t.rand(x, 0) * 2);
                for (int y = 0; y < h; y++) { t.set(x, y, 0x6f6265); }
            }
        });
        p("dirt_path_top", t => { t.blotch(0xb09659, 0.12, 3); });
        p("dirt_path_side", t => { paintInto(t, "dirt"); for (int x = 0; x < 16; x++) { t.set(x, 0, 0xb09659); } });
        p("farmland_dry", t => { t.blotch(0x966c4a, 0.12); foreach (int x in new[] { 3, 7, 11 }) { t.vline(x, 0, 15, shade(0x966c4a, 0.7)); } });
        p("farmland_wet", t => { t.blotch(0x5c3d26, 0.12); foreach (int x in new[] { 3, 7, 11 }) { t.vline(x, 0, 15, shade(0x5c3d26, 0.7)); } });
        p("mud", t => { t.blotch(0x3c3a3d, 0.13, 3); t.speckle(0x57555a, 10); });
        p("packed_mud", t => { t.blotch(0x8f6c51, 0.1, 3); });
        p("mud_bricks", t => { t.bricks(0x8f6c51, shade(0x8f6c51, 0.7), 8, 4); });
        p("muddy_mangrove_roots_top", t => { t.blotch(0x3c3a3d, 0.12); t.speckle(0x583c2e, 16); });
        p("muddy_mangrove_roots_side", t => { t.blotch(0x46423f, 0.12); foreach (int x in new[] { 2, 6, 10, 14 }) { t.vline(x, 0, 15, 0x583c2e); } });
        p("clay", t => { t.blotch(0x9aa3b3, 0.09, 3); });
        p("gravel", t => { t.blotch(C_GRAVEL, 0.2, 2); t.speckle(shade(C_GRAVEL, 0.6), 16); t.speckle(shade(C_GRAVEL, 1.3), 14); });
        p("sand", t => { t.blotch(C_SAND, 0.09, 3); t.speckle(shade(C_SAND, 0.85), 12); });
        p("red_sand", t => { t.blotch(C_RED_SAND, 0.1, 3); t.speckle(shade(C_RED_SAND, 0.85), 12); });
        p("suspicious_sand", t => { t.blotch(shade(C_SAND, 0.95), 0.1, 3); t.speckle(shade(C_SAND, 0.7), 18); t.set(7, 7, shade(C_SAND, 0.6)); t.set(8, 8, shade(C_SAND, 0.6)); });
        p("suspicious_gravel", t => { t.blotch(shade(C_GRAVEL, 0.95), 0.16, 2); t.speckle(shade(C_GRAVEL, 0.55), 18); });
        p("sandstone_top", t => { t.blotch(shade(C_SAND, 1.02), 0.07, 4); });
        p("sandstone_bottom", t => { t.bricks(C_SAND, shade(C_SAND, 0.85), 4, 4); });
        p("sandstone_side", t =>
        {
            t.blotch(C_SAND, 0.08, 4);
            t.hline(0, 0, 15, shade(C_SAND, 1.06));
            t.hline(15, 0, 15, shade(C_SAND, 0.8));
            foreach (int y in new[] { 5, 9, 12 }) { t.hline(y, 0, 15, shade(C_SAND, 0.9)); }
        });
        p("cut_sandstone", t => { t.blotch(C_SAND, 0.06, 4); t.border(shade(C_SAND, 0.85)); });
        p("chiseled_sandstone", t => { t.blotch(C_SAND, 0.06, 4); t.border(shade(C_SAND, 0.85)); t.disc(7.5, 7.5, 4, shade(C_SAND, 0.8)); t.disc(7.5, 7.5, 2.5, shade(C_SAND, 1.1)); });
        p("red_sandstone_top", t => { t.blotch(shade(C_RED_SAND, 1.02), 0.07, 4); });
        p("red_sandstone_bottom", t => { t.bricks(C_RED_SAND, shade(C_RED_SAND, 0.85), 4, 4); });
        p("red_sandstone_side", t => { t.blotch(C_RED_SAND, 0.08, 4); foreach (int y in new[] { 5, 9, 12 }) { t.hline(y, 0, 15, shade(C_RED_SAND, 0.9)); } });
        p("cut_red_sandstone", t => { t.blotch(C_RED_SAND, 0.06, 4); t.border(shade(C_RED_SAND, 0.85)); });
        p("chiseled_red_sandstone", t => { t.blotch(C_RED_SAND, 0.06, 4); t.border(shade(C_RED_SAND, 0.85)); t.disc(7.5, 7.5, 4, shade(C_RED_SAND, 0.8)); });

        // --- snow/ice ---
        p("snow", t => { t.blotch(0xf6fbfb, 0.04, 4); });
        p("snow_block", t => { t.blotch(0xf6fbfb, 0.05, 4); });
        p("powder_snow", t => { t.blotch(0xf8fdfd, 0.03, 4); t.speckle(0xe8efef, 10); });
        p("ice", t => { t.blotch(0x91b9f0, 0.08, 4); t.speckle(0xbcd8f7, 8); });
        p("packed_ice", t => { t.blotch(0x84a8e0, 0.07, 4); });
        p("blue_ice", t => { t.blotch(0x4a8ae0, 0.06, 4); });
        p("frosted_ice", t => { t.blotch(0x9cc2f2, 0.08, 4); crack(t, 0xc8e0fa); });

        // --- nether ---
        p("netherrack", t => { t.blotch(C_NETHER, 0.18, 3); t.speckle(shade(C_NETHER, 1.4), 10); t.speckle(shade(C_NETHER, 0.6), 12); });
        p("crimson_nylium", t => { t.blotch(0x892c2c, 0.14, 3); t.speckle(0xc23535, 14); });
        p("crimson_nylium_side", t => { paintInto(t, "netherrack"); for (int x = 0; x < 16; x++) { t.set(x, 0, 0x9c3030); } });
        p("warped_nylium", t => { t.blotch(0x167e86, 0.14, 3); t.speckle(0x2ab5a5, 14); });
        p("warped_nylium_side", t => { paintInto(t, "netherrack"); for (int x = 0; x < 16; x++) { t.set(x, 0, 0x1d8b8b); } });
        p("soul_sand", t => { t.blotch(0x514033, 0.13, 3); t.set(4, 5, 0x2e2017); t.set(5, 5, 0x2e2017); t.set(10, 9, 0x2e2017); t.set(11, 9, 0x2e2017); t.set(11, 10, 0x2e2017); });
        p("soul_soil", t => { t.blotch(0x4b3a2e, 0.14, 3); t.speckle(0x39291f, 14); });
        p("magma_block", t =>
        {
            t.blotch(0x3c1e12, 0.15, 3);
            for (int i = 0; i < 7; i++)
            {
                int x = (int)(t.rand(i, 0) * 13) + 1, y = (int)(t.rand(i, 1) * 13) + 1;
                t.set(x, y, 0xff7b1d); t.set(x + 1, y, 0xe8521a); t.set(x, y + 1, 0xc23a10);
            }
        });
        p("glowstone", t => { t.blotch(0xab7b4a, 0.12, 3); t.speckle(0xffd98c, 26); t.speckle(0xfcbd5d, 16); });
        p("shroomlight", t => { t.blotch(0xf09a5a, 0.1, 3); t.speckle(0xffd6a0, 18); });
        p("nether_wart_block", t => { t.blotch(0x71080a, 0.14, 2); t.speckle(0xa61415, 16); });
        p("warped_wart_block", t => { t.blotch(0x167b80, 0.14, 2); t.speckle(0x14a8a8, 16); });
        p("basalt_side", t => { t.blotch(0x4e4e56, 0.1); foreach (int x in new[] { 0, 5, 10, 15 }) { t.vline(x, 0, 15, shade(0x4e4e56, 0.78)); } });
        p("basalt_top", t =>
        {
            t.blotch(0x56565e, 0.09);
            for (int i = 0; i < 6; i++)
            {
                t.disc(2 + (double)(i % 3) * 5.5, 3 + (double)(i / 3) * 8, 2, shade(0x56565e, 0.86 + t.rand(i, 4) * 0.3));
            }
        });
        p("polished_basalt_side", t => { t.blotch(0x58585f, 0.07); foreach (int x in new[] { 0, 7, 15 }) { t.vline(x, 0, 15, shade(0x58585f, 0.8)); } });
        p("polished_basalt_top", t => { t.blotch(0x5d5d64, 0.06); t.disc(7.5, 7.5, 5, shade(0x5d5d64, 0.85)); });
        p("smooth_basalt", t => { t.blotch(0x47474e, 0.07); });
        p("blackstone", t => { t.blotch(0x241f26, 0.16, 3); t.speckle(0x3f3a42, 12); });
        p("blackstone_top", t => { t.blotch(0x2a242c, 0.13, 3); });
        p("polished_blackstone", t => { t.blotch(0x2f2a32, 0.07); t.border(shade(0x2f2a32, 0.75)); });
        p("polished_blackstone_bricks", t => { t.bricks(0x2f2a32, 0x1c181e, 8, 4); });
        p("cracked_polished_blackstone_bricks", t => { t.bricks(0x2f2a32, 0x1c181e, 8, 4); crack(t, 0x151218); });
        p("chiseled_polished_blackstone", t => { t.blotch(0x2f2a32, 0.06); t.border(0x1c181e); t.rect(4, 4, 11, 11, 0x241f26); t.rect(6, 6, 9, 9, 0x39333c); });
        p("gilded_blackstone", t => { t.blotch(0x241f26, 0.15, 3); t.speckle(0xf0c64c, 9); t.set(4, 4, 0xfadd72); t.set(11, 9, 0xfadd72); });
        p("nether_bricks", t => { t.bricks(0x2c171b, 0x190d10, 8, 4); });
        p("cracked_nether_bricks", t => { t.bricks(0x2c171b, 0x190d10, 8, 4); crack(t, 0x120a0c); });
        p("chiseled_nether_bricks", t => { t.blotch(0x2c171b, 0.07); t.border(0x190d10); t.rect(4, 4, 11, 11, 0x241317); });
        p("red_nether_bricks", t => { t.bricks(0x481419, 0x2b0c0f, 8, 4); });
        p("bone_block_side", t => { t.blotch(0xd1cda5, 0.07); foreach (int x in new[] { 3, 7, 11 }) { t.vline(x, 0, 15, shade(0xd1cda5, 0.85)); } });
        p("bone_block_top", t => { t.blotch(0xc9c597, 0.06); t.disc(7.5, 7.5, 4.5, shade(0xc9c597, 0.88)); });
        p("ancient_debris_side", t => { t.blotch(0x5e4439, 0.12, 3); t.speckle(0x8a6a55, 8); t.rect(5, 6, 9, 9, shade(0x5e4439, 0.8)); });
        p("ancient_debris_top", t => { t.blotch(0x5e4439, 0.11, 3); t.disc(7.5, 7.5, 4, shade(0x6e5244, 1.05)); t.disc(7.5, 7.5, 2, shade(0x5e4439, 0.85)); });

        // --- end ---
        p("end_stone", t => { t.blotch(C_END, 0.09, 3); t.speckle(shade(C_END, 0.85), 18); });
        p("end_stone_bricks", t => { t.bricks(C_END, shade(C_END, 0.82), 8, 4); });
        p("purpur_block", t => { t.blotch(0xa97aa9, 0.09, 3); t.speckle(0xbf94bf, 10); });
        p("purpur_pillar", t => { t.blotch(0xaa7caa, 0.07); foreach (int x in new[] { 0, 15 }) { t.vline(x, 0, 15, shade(0xaa7caa, 0.85)); } });
        p("purpur_pillar_top", t => { t.blotch(0xaa7caa, 0.06); t.border(shade(0xaa7caa, 0.85)); t.rect(4, 4, 11, 11, shade(0xaa7caa, 0.92)); });
        p("end_portal_frame_top", t => { t.blotch(0x3a5e52, 0.08); t.border(shade(C_END, 0.9)); t.disc(7.5, 7.5, 3, 0x2a443c); });
        p("end_portal_frame_side", t => { t.blotch(C_END, 0.08); t.hline(0, 0, 15, 0x3a5e52); t.hline(1, 0, 15, 0x466e60); });
        p("end_portal", t => { t.fill(0x000000); t.speckle(0x4a3a6a, 20); t.speckle(0x8a7ab0, 10); t.speckle(0xc8c0e8, 5); });
        p("nether_portal", t =>
        {
            t.fill(0x4a1190, 200);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    double v = t.rand(x / 2, y / 3);
                    t.set(x, y, mixC(0x3a0a78, 0x8a2ae0, v), 210);
                }
            }
        });
        p("dragon_egg", t => { t.blotch(0x0d0810, 0.15, 3); t.speckle(0x3a2a4a, 10); });
        p("chorus_plant", t => { t.blotch(0x6a4a7a, 0.1, 3); t.border(shade(0x6a4a7a, 0.8)); });
        p("chorus_flower", t => { t.blotch(0x9a7ab0, 0.09, 3); t.border(0x6a4a7a); t.rect(5, 5, 10, 10, 0xb796c8); });
        p("chorus_flower_dead", t => { t.blotch(0x7a5a6a, 0.1, 3); t.border(0x5a4054); });
        p("end_rod", t => { t.clearAlpha(); t.vline(7, 0, 15, 0xf8eadf); t.vline(8, 0, 15, 0xfdf6ee); t.rect(6, 13, 9, 15, 0xc8a8d8); });

        // --- liquids ---
        p("water", t =>
        {
            for (int y = 0; y < 16; y++) { for (int x = 0; x < 16; x++) { t.set(x, y, shade(0xffffff, 0.75 + t.rand(x, y) * 0.25), 255); } }
        });
        p("lava", t =>
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    double v = (t.rand(x / 3, y / 3) + t.rand(x, y) * 0.4) / 1.4;
                    t.set(x, y, v < 0.4 ? 0xc8390f : v < 0.7 ? 0xe85d10 : v < 0.9 ? 0xfc9a2c : 0xffd45c);
                }
            }
        });

        // --- glass ---
        p("glass", t =>
        {
            t.clearAlpha();
            t.border(0xd8eff5);
            t.set(3, 2, 0xffffff, 160); t.set(4, 3, 0xffffff, 160); t.set(2, 3, 0xffffff, 120);
            t.set(11, 10, 0xffffff, 90); t.set(12, 11, 0xffffff, 90);
        });
        p("tinted_glass", t => { t.fill(0x2a2333, 190); t.border(0x483e54); t.set(3, 2, 0x6a5a80, 220); t.set(4, 3, 0x6a5a80, 220); });
        p("iron_bars", t => { t.clearAlpha(); t.vline(7, 0, 15, 0x828383); t.vline(8, 0, 15, 0x6a6b6b); t.hline(0, 5, 10, 0x828383); t.hline(8, 5, 10, 0x747575); });

        // --- wool & colored families ---
        rp("^(\\w+?)_wool$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xffffff);
            t.blotch(c, 0.08, 2);
            int y = 0;
            while (y < 16)
            {
                int x = 0;
                while (x < 16)
                {
                    t.set(x + (y / 4) % 2, y, shade(c, 0.88));
                    x += 4;
                }
                y += 4;
            }
        });
        rp("^(\\w+?)_concrete_powder$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xffffff);
            t.blotch(shade(c, 0.96), 0.12, 2);
            t.speckle(shade(c, 0.8), 14); t.speckle(shade(c, 1.15), 10);
        });
        rp("^(\\w+?)_concrete$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xffffff);
            t.blotch(shade(c, 0.9), 0.04, 5);
        });
        p("terracotta", t => { t.blotch(0x985e43, 0.09, 3); });
        rp("^(\\w+?)_terracotta$", (t, m) =>
        {
            int c = colorRGB(m[1], 0x985e43);
            t.blotch(mixC(c, 0x985e43, 0.45), 0.09, 3);
        });
        rp("^(\\w+?)_glazed_terracotta$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xffffff);
            int c2 = shade(c, 0.6), c3 = mixC(c, 0xffffff, 0.45);
            t.fill(c3);
            t.disc(4, 4, 4.5, c);
            t.disc(12, 12, 4.5, c);
            t.disc(12, 4, 2.5, c2);
            t.disc(4, 12, 2.5, c2);
            t.border(shade(c, 0.8));
        });
        rp("^(\\w+?)_stained_glass$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xffffff);
            t.fill(c, 140);
            t.border(shade(c, 0.75));
            t.set(3, 2, 0xffffff, 200); t.set(4, 3, 0xffffff, 200);
        });
        rp("^(\\w+?)_candle$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xd8d2c8);
            t.clearAlpha();
            t.rect(7, 6, 8, 14, c);
            t.set(7, 5, 0x808080);
            t.set(7, 4, 0xffd86a);
        });
        p("candle", t => { t.clearAlpha(); t.rect(7, 6, 8, 14, 0xd8cfc0); t.set(7, 5, 0x808080); t.set(7, 4, 0xffd86a); });
        rp("^(\\w+?)_bed_top$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xb03030);
            t.blotch(c, 0.08);                                  // blanket
            t.rect(0, 0, 16, 5, 0xe8e4dc); t.hline(5, 0, 15, shade(c, 0.7));   // pillow + seam
            t.border(shade(c, 0.82));
        });
        rp("^(\\w+?)_bed_side$", (t, m) =>
        {
            int c = colorRGB(m[1], 0xb03030);
            t.planks(0x9a7444);                                 // wood frame
            t.rect(0, 0, 16, 6, c);                             // blanket band
            t.rect(0, 0, 16, 2, 0xe8e4dc);                      // pillow edge
            t.hline(6, 0, 15, shade(c, 0.7));
        });
        rp("^(\\w+?)_shulker_box$", (t, m) =>
        {
            int c = colorRGB(m[1], 0x976797);
            t.blotch(c, 0.06, 4);
            t.border(shade(c, 0.78));
            t.hline(7, 1, 14, shade(c, 0.85));
            t.hline(8, 1, 14, shade(c, 1.12));
        });
        p("shulker_box", t => { t.blotch(0x976797, 0.06, 4); t.border(shade(0x976797, 0.78)); t.hline(7, 1, 14, shade(0x976797, 0.85)); });

        // --- woods ---
        rp("^(?:stripped_)?(\\w+?)_(?:log|stem|hyphae|wood)(_top)?$", (t, m) =>
        {
            WoodColor wood = WOOD_COLORS.TryGetValue(m[1] ?? "", out WoodColor wc) ? wc : WOOD_COLORS["oak"];
            bool stripped = t.name.StartsWith("stripped");
            if (m[2] != null)
            {
                // top: rings
                int @base = wood.inner;
                t.blotch(@base, 0.07, 4);
                t.border(stripped ? shade(wood.inner, 0.85) : wood.bark);
                t.rect(3, 3, 12, 12, shade(@base, 0.92));
                t.rect(5, 5, 10, 10, shade(@base, 1.06));
                t.rect(7, 7, 8, 8, shade(@base, 0.88));
            }
            else
            {
                int @base = stripped ? wood.inner : wood.bark;
                t.grain(@base, stripped ? 0.1 : 0.22, stripped ? 5 : 6);
                if (!stripped)
                {
                    // bark texture knots
                    for (int i = 0; i < 4; i++)
                    {
                        int x = (int)(t.rand(i, 0) * 14), y = (int)(t.rand(i, 1) * 14);
                        t.set(x, y, shade(@base, 0.65)); t.set(x, y + 1, shade(@base, 0.72));
                    }
                }
            }
        });
        rp("^(\\w+?)_planks$", (t, m) =>
        {
            WoodColor wood = WOOD_COLORS.TryGetValue(m[1] ?? "", out WoodColor wc) ? wc : WOOD_COLORS["oak"];
            t.planks(wood.plank);
        });
        p("bamboo_mosaic", t =>
        {
            int c = WOOD_COLORS["bamboo"].plank;
            t.grain(c, 0.1, 8);
            foreach (int y in new[] { 3, 7, 11, 15 }) { t.hline(y, 0, 15, shade(c, 0.7)); }
            foreach (int x in new[] { 7, 15 }) { t.vline(x, 0, 15, shade(c, 0.75)); }
        });
        p("bamboo_block", t =>
        {
            int c = 0x82a83c;
            t.grain(c, 0.12, 5);
            foreach (int y in new[] { 4, 12 }) { t.hline(y, 0, 15, shade(c, 0.7)); }
        });
        p("bamboo_block_top", t => { t.blotch(0x9ec045, 0.08); t.disc(7.5, 7.5, 5, shade(0x9ec045, 0.85)); t.disc(7.5, 7.5, 2, shade(0x9ec045, 1.1)); });
        p("stripped_bamboo_block", t =>
        {
            int c = WOOD_COLORS["bamboo"].plank;
            t.grain(c, 0.09, 5);
            foreach (int y in new[] { 4, 12 }) { t.hline(y, 0, 15, shade(c, 0.78)); }
        });
        p("stripped_bamboo_block_top", t =>
        {
            int c = WOOD_COLORS["bamboo"].plank;
            t.blotch(c, 0.07);
            t.disc(7.5, 7.5, 5, shade(c, 0.88));
        });
        rp("^(\\w+?)_leaves$", (t, m) =>
        {
            string key = m[1] ?? "";
            int c = LEAF_COLORS.TryGetValue(key, out int lc) ? lc : (t.name.Contains("azalea") ? LEAF_COLORS["azalea"] : 0x878787);
            bool gray = key == "oak" || key == "jungle" || key == "acacia" || key == "dark_oak" || key == "mangrove"; // tinted in shader
            int @base = gray ? 0x8c8c8c : c;
            t.clearAlpha();
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    double v = t.rand(x, y);
                    if (v < 0.82) { t.set(x, y, shade(@base, 0.7 + v * 0.55)); }
                }
            }
            if (key == "flowering_azalea") { t.speckle(0xd667c8, 7); t.speckle(0xe88ad8, 5); }
            if (key == "cherry") { t.speckle(0xf7c7d4, 16); t.speckle(0xe8a0b8, 10); }
        });

        // --- ores & mineral blocks ---
        rp("^(deepslate_)?(\\w+?)_ore$", (t, m) =>
        {
            if (m[1] != null) { paintInto(t, "deepslate"); }
            else { paintInto(t, "stone"); }
            if (ORE_COLORS.TryGetValue(m[2] ?? "", out int oc))
            {
                bool many = m[2] == "coal" || m[2] == "iron" || m[2] == "copper";
                t.oreBlobs(oc, many ? 6 : 4);
            }
        });
        p("nether_gold_ore", t => { paintInto(t, "netherrack"); t.oreBlobs(0xfcee4b, 5); });
        p("nether_quartz_ore", t => { paintInto(t, "netherrack"); t.oreBlobs(0xe8e0d8, 5); });
        rp("^raw_(\\w+?)_block$", (t, m) =>
        {
            int c = m[1] == "iron" ? 0xa88a72 : m[1] == "copper" ? 0xa05a3c : 0xddaa3e;
            t.blotch(c, 0.13, 3);
            t.speckle(shade(c, 1.25), 12);
        });
        p("coal_block", t => { t.blotch(0x161616, 0.14, 3); t.speckle(0x2c2c2c, 10); });
        p("iron_block", t => { metalBlock(t, 0xd8d8d8); });
        p("gold_block", t => { metalBlock(t, 0xf6cf3c); });
        p("diamond_block", t => { metalBlock(t, 0x62e5dc); });
        p("emerald_block", t => { metalBlock(t, 0x2cc24e); });
        p("netherite_block", t => { t.blotch(0x42383b, 0.09, 3); t.border(shade(0x42383b, 0.8)); });
        p("lapis_block", t => { t.blotch(0x1f4ba8, 0.1, 3); t.speckle(0x2c5fc4, 10); t.speckle(0xc4a73c, 4); });
        p("redstone_block", t => { t.blotch(0xa50d0d, 0.12, 3); t.speckle(0xd42020, 10); });
        p("amethyst_block", t => { t.blotch(0x8662bf, 0.1, 3); t.speckle(0xa886d8, 12); t.speckle(0x6a4a9e, 8); });
        p("budding_amethyst", t =>
        {
            t.blotch(0x8662bf, 0.1, 3);
            t.rect(6, 6, 9, 9, 0xb595e2); t.rect(7, 7, 8, 8, 0x6a4a9e);
            t.set(2, 2, 0xb595e2); t.set(13, 13, 0xb595e2); t.set(13, 2, 0xb595e2); t.set(2, 13, 0xb595e2);
        });
        p("quartz_block_top", t => { t.blotch(0xece6df, 0.05, 4); });
        p("quartz_block_bottom", t => { t.blotch(0xe2dcd4, 0.05, 4); });
        p("quartz_block_side", t => { t.blotch(0xece6df, 0.05, 4); t.hline(0, 0, 15, shade(0xece6df, 1.05)); });
        p("chiseled_quartz_block", t => { t.blotch(0xece6df, 0.05); t.border(shade(0xece6df, 0.85)); t.disc(7.5, 7.5, 4, shade(0xece6df, 0.9)); });
        p("chiseled_quartz_block_top", t => { t.blotch(0xece6df, 0.05); t.border(shade(0xece6df, 0.85)); });
        p("quartz_pillar", t => { t.blotch(0xece6df, 0.05); foreach (int x in new[] { 0, 15 }) { t.vline(x, 0, 15, shade(0xece6df, 0.85)); } });
        p("quartz_pillar_top", t => { t.blotch(0xece6df, 0.05); t.disc(7.5, 7.5, 5, shade(0xece6df, 0.88)); });
        p("quartz_bricks", t => { t.bricks(0xece6df, shade(0xece6df, 0.85), 8, 4); });

        // --- copper family ---
        rp("^(?:waxed_)?(exposed_|weathered_|oxidized_)?(?:cut_)?copper(?:_block)?$", (t, m) =>
        {
            string stage = m[1] ?? "";
            int @base = stage == "" ? 0xc06843 : stage == "exposed_" ? 0xa07955 : stage == "weathered_" ? 0x6a9468 : 0x53a486;
            bool isCut = t.name.Contains("cut_");
            t.blotch(@base, 0.09, 3);
            if (isCut)
            {
                t.border(shade(@base, 0.8)); t.hline(7, 0, 15, shade(@base, 0.85)); t.vline(7, 0, 15, shade(@base, 0.85));
            }
            else
            {
                t.speckle(shade(@base, 1.18), 10);
            }
        });
        p("lightning_rod", t => { t.clearAlpha(); t.vline(7, 2, 15, 0xc06843); t.vline(8, 2, 15, 0xa05a3c); t.rect(6, 0, 9, 2, 0xc87a52); });

        // --- prismarine / ocean ---
        p("prismarine", t =>
        {
            t.blotch(0x6fa495, 0.1, 3);
            int y = 0;
            while (y < 16)
            {
                int x = 0;
                while (x < 16)
                {
                    t.rect(x, y, x + 3, y, shade(0x6fa495, 0.82));
                    x += 4;
                }
                y += 4;
            }
        });
        p("prismarine_bricks", t => { t.bricks(0x84c0b0, shade(0x84c0b0, 0.75), 4, 4); });
        p("dark_prismarine", t => { t.bricks(0x356456, shade(0x356456, 0.7), 4, 4); });
        p("sea_lantern", t => { t.blotch(0xcdebe2, 0.06, 3); t.border(0xa8cfc5); t.rect(5, 5, 10, 10, 0xe8fffa); t.rect(7, 7, 8, 8, 0xf8fffe); });
        p("sponge", t => { t.blotch(0xc4c14b, 0.1, 2); t.speckle(0x8f8d36, 18); t.set(4, 4, 0x6e6c2a); t.set(10, 7, 0x6e6c2a); t.set(6, 12, 0x6e6c2a); });
        p("wet_sponge", t => { t.blotch(0xa8a73e, 0.1, 2); t.speckle(0x7a7830, 18); t.speckle(0x5a6ab0, 6); });
        p("dried_kelp_block", t => { t.blotch(0x3a4a2a, 0.12, 2); foreach (int y in new[] { 5, 11 }) { t.hline(y, 0, 15, 0x2c3a20); } });

        // --- corals ---
        rp("^(tube|brain|bubble|fire|horn)(_coral|_coral_block|_coral_fan)$", (t, m) =>
        {
            liveCoralPaint(t, m);
        });
        rp("^dead_(tube|brain|bubble|fire|horn)(_coral|_coral_block|_coral_fan)$", (t, m) =>
        {
            T fake = new T($"{m[1]}{m[2]}");
            liveCoralPaint(fake, new string[] { null, m[1], m[2] });
            for (int i = 0; i < fake.data.Length; i += 4)
            {
                int g = (int)detRound((double)fake.data[i] * 0.35 + (double)fake.data[i + 1] * 0.45 + (double)fake.data[i + 2] * 0.2);
                t.data[i] = (byte)Math.Min(255, g);
                t.data[i + 1] = (byte)Math.Min(255, (int)detRound((double)g * 0.96));
                t.data[i + 2] = (byte)Math.Min(255, (int)detRound((double)g * 0.9));
                t.data[i + 3] = fake.data[i + 3];
            }
        });
    }

    private static void liveCoralPaint(T t, string[] m)
    {
        Dictionary<string, int> cols = new Dictionary<string, int>
        {
            ["tube"] = 0x3060c8, ["brain"] = 0xd05ba8, ["bubble"] = 0xb030b0, ["fire"] = 0xc83838, ["horn"] = 0xd8c84a
        };
        int c = cols[m[1] ?? ""];
        if (m[2] == "_coral_block")
        {
            t.blotch(c, 0.1, 2);
            t.speckle(shade(c, 1.2), 16);
        }
        else if (m[2] == "_coral")
        {
            t.clearAlpha();
            t.vline(7, 6, 15, c); t.vline(8, 8, 15, shade(c, 0.85));
            t.vline(4, 9, 15, shade(c, 1.1)); t.vline(11, 10, 15, c);
            t.set(7, 5, shade(c, 1.2)); t.set(4, 8, shade(c, 1.2));
        }
        else
        {
            t.clearAlpha();
            for (int i = 0; i < 5; i++)
            {
                int x = 2 + i * 3;
                t.vline(x, 10 - i % 2 * 2, 15, shade(c, 0.9 + (double)(i % 3) * 0.1));
            }
            t.hline(15, 2, 14, shade(c, 0.8));
        }
    }
}

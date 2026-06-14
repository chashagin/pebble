// Java-format resource-pack loader for the Windows app — port of
// Sources/Pebble/ResourcePacks.swift (the app-layer pack importer), trimmed to
// what the Vulkan/D3D12 backends need: it opens the Faithful 32x .zip, maps the
// standard pack texture tree onto the engine's tile registry (Reg.tileName),
// builds one RGBA slice per tile id at the pack resolution (32), composites the
// block-entity tiles (beds/chests/bell/decorated pot) out of the entity/ art,
// reads .mcmeta frame animations, and falls back to the procedural painter for
// any tile the pack doesn't cover. PebbleCore is never touched.
//
// Decode uses SixLabors.ImageSharp; the zip is read with System.IO.Compression.
// Slice byte order is straight (un-premultiplied) RGBA8, the same layout the
// backends upload and the engine's procedural atlas produces.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Pebble;

/// straight RGBA8 image (width*height*4)
internal sealed class RGBAImage
{
    public int width;
    public int height;
    public byte[] pixels;
    public RGBAImage(int w, int h, byte[] px) { width = w; height = h; pixels = px; }
}

/// one .mcmeta frame animation bound to an atlas slice
public sealed class TileAnimation
{
    public int slice;
    public List<byte[]> frames = new();        // each res*res*4
    public List<(int index, int ticks)> order = new();
    public bool interpolate;
}

/// the result handed to the backends: one slice per tile id (in tileName order),
/// at `res`×`res`, plus the 16× icon atlas, animations and item-icon overrides.
public sealed class PackAtlasResult
{
    public int res;
    public required byte[][] slices;                         // [tileCount][res*res*4]
    public required PebbleCore.BuiltAtlas icon16;            // 16× tiles for the icon renderer
    public List<TileAnimation> animations = new();
    public Dictionary<string, byte[]> itemIcons = new();    // item name → 16×16 RGBA
    public bool fluidAnimated;
    public int appliedTiles;
    public int appliedItems;
}

public static class ResourcePacks
{
    public const string DefaultPackFile = "Faithful 32x - 1.20.1.zip";

    // ── pack handle (zip) ─────────────────────────────────────────────────────
    private sealed class Pack : IDisposable
    {
        private readonly ZipArchive _zip;
        // lowercased in-pack path → entry (zips from Windows tools vary in case)
        private readonly Dictionary<string, ZipArchiveEntry> _index = new();
        public string texRoot = "";   // "assets/<ns>/textures/"

        public Pack(string path)
        {
            _zip = ZipFile.OpenRead(path);
            foreach (var e in _zip.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;
                _index[e.FullName.ToLowerInvariant()] = e;
            }
            // texture root: prefer the vanilla "minecraft" namespace (a pack may
            // also ship assets/forge/textures or other namespaces — block/* lives
            // under minecraft). Fall back to the first textures/ root found.
            // NOTE: _index is a Dictionary (unordered), so we can't rely on "first
            // hit wins" the way the Swift loader did — pick minecraft explicitly.
            string fallbackRoot = "";
            foreach (var k in _index.Keys)
            {
                int r = k.IndexOf("assets/", StringComparison.Ordinal);
                if (r < 0) continue;
                string rest = k.Substring(r + "assets/".Length);
                int slash = rest.IndexOf('/');
                if (slash <= 0) continue;
                string ns = rest.Substring(0, slash);
                if (!rest.Substring(slash).Contains("/textures/")) continue;
                if (ns == "minecraft") { texRoot = "assets/minecraft/textures/"; break; }
                if (fallbackRoot.Length == 0) fallbackRoot = $"assets/{ns}/textures/";
            }
            if (texRoot.Length == 0) texRoot = fallbackRoot;
        }

        public byte[]? File(string inPackPath)
        {
            if (!_index.TryGetValue(inPackPath.ToLowerInvariant(), out var e)) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public IEnumerable<string> List(string prefix)
        {
            string p = prefix.ToLowerInvariant();
            foreach (var kv in _index)
                if (kv.Key.StartsWith(p, StringComparison.Ordinal)) yield return kv.Value.FullName;
        }

        public void Dispose() => _zip.Dispose();
    }

    // ── PNG decode → straight RGBA8 ───────────────────────────────────────────
    private static RGBAImage? DecodePNG(byte[] data)
    {
        try
        {
            using var img = Image.Load<Rgba32>(data);
            int w = img.Width, h = img.Height;
            if (w <= 0 || h <= 0 || w > 4096 || h > 8192) return null;
            var px = new byte[w * h * 4];
            img.CopyPixelDataTo(px);   // straight RGBA8, top-left origin
            return new RGBAImage(w, h, px);
        }
        catch { return null; }
    }

    // ── pixel helpers (nearest upscale / box downscale / tint bake) ───────────
    private static byte[] ScaleNearest(RGBAImage img, int res)
    {
        if (img.width == res && img.height == res) return img.pixels;
        var outp = new byte[res * res * 4];
        for (int y = 0; y < res; y++)
        {
            int sy = y * img.height / res;
            for (int x = 0; x < res; x++)
            {
                int sx = x * img.width / res;
                int s = (sy * img.width + sx) * 4, d = (y * res + x) * 4;
                outp[d] = img.pixels[s]; outp[d + 1] = img.pixels[s + 1];
                outp[d + 2] = img.pixels[s + 2]; outp[d + 3] = img.pixels[s + 3];
            }
        }
        return outp;
    }

    private static byte[] ScaleBox(RGBAImage img, int res)
    {
        if (img.width == res && img.height == res) return img.pixels;
        if (img.width < res || img.height < res) return ScaleNearest(img, res);
        var outp = new byte[res * res * 4];
        int bx = img.width / res, by = img.height / res;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int dy = 0; dy < by; dy++)
                {
                    for (int dx = 0; dx < bx; dx++)
                    {
                        int s = ((y * by + dy) * img.width + (x * bx + dx)) * 4;
                        int pa = img.pixels[s + 3];
                        r += img.pixels[s] * pa;
                        g += img.pixels[s + 1] * pa;
                        b += img.pixels[s + 2] * pa;
                        a += pa; n++;
                    }
                }
                int d = (y * res + x) * 4;
                if (a > 0)
                {
                    outp[d] = (byte)(r / a); outp[d + 1] = (byte)(g / a); outp[d + 2] = (byte)(b / a);
                }
                outp[d + 3] = (byte)(a / n);
            }
        }
        return outp;
    }

    private static byte[] ScaleTo(RGBAImage img, int res) => img.width > res ? ScaleBox(img, res) : ScaleNearest(img, res);

    private static void BakeTint(byte[] px, int rgb)
    {
        int tr = (rgb >> 16) & 255, tg = (rgb >> 8) & 255, tb = rgb & 255;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(px[i] * tr / 255);
            px[i + 1] = (byte)(px[i + 1] * tg / 255);
            px[i + 2] = (byte)(px[i + 2] * tb / 255);
        }
    }

    private static RGBAImage StripFrame(RGBAImage img, int i)
    {
        int w = img.width;
        int start = i * w * w * 4;
        var px = new byte[w * w * 4];
        Array.Copy(img.pixels, start, px, 0, w * w * 4);
        return new RGBAImage(w, w, px);
    }

    // ── tile name → pack texture path mapping (from the Swift NAME_MAP) ────────
    private static readonly Dictionary<string, string[]> NameMap = new()
    {
        ["grass_top"] = new[] { "block/grass_block_top" },
        ["grass_side"] = new[] { "block/grass_block_side" },
        ["farmland_dry"] = new[] { "block/farmland" },
        ["farmland_wet"] = new[] { "block/farmland_moist" },
        ["sandstone_side"] = new[] { "block/sandstone" },
        ["red_sandstone_side"] = new[] { "block/red_sandstone" },
        ["snow_block"] = new[] { "block/snow" },
        ["frosted_ice"] = new[] { "block/frosted_ice_0" },
        ["dried_kelp_block"] = new[] { "block/dried_kelp_side" },
        ["magma_block"] = new[] { "block/magma" },
        ["water"] = new[] { "block/water_still" },
        ["lava"] = new[] { "block/lava_still" },
        ["fire"] = new[] { "block/fire_0" },
        ["soul_fire"] = new[] { "block/soul_fire_0" },
        ["short_grass"] = new[] { "block/short_grass", "block/grass" },
        ["mangrove_roots"] = new[] { "block/mangrove_roots_side", "block/mangrove_roots" },
        ["suspicious_sand"] = new[] { "block/suspicious_sand_0" },
        ["suspicious_gravel"] = new[] { "block/suspicious_gravel_0" },
        ["bamboo"] = new[] { "block/bamboo_stalk" },
        ["bamboo_sapling"] = new[] { "block/bamboo_stage0" },
        ["big_dripleaf"] = new[] { "block/big_dripleaf_top" },
        ["small_dripleaf"] = new[] { "block/small_dripleaf_top" },
        ["azalea"] = new[] { "block/azalea_top" },
        ["flowering_azalea"] = new[] { "block/flowering_azalea_top" },
        ["pitcher_plant_top"] = new[] { "block/pitcher_plant_top", "block/pitcher_crop_top" },
        ["pitcher_plant_bottom"] = new[] { "block/pitcher_plant_bottom", "block/pitcher_crop_bottom" },
        ["pitcher_crop"] = new[] { "block/pitcher_crop_top", "block/pitcher_crop_bottom" },
        ["furnace_front_lit"] = new[] { "block/furnace_front_on" },
        ["blast_furnace_front_lit"] = new[] { "block/blast_furnace_front_on" },
        ["smoker_front_lit"] = new[] { "block/smoker_front_on" },
        ["observer_back_lit"] = new[] { "block/observer_back_on" },
        ["anvil_side"] = new[] { "block/anvil" },
        ["cartography_table_side"] = new[] { "block/cartography_table_side3" },
        ["lectern_side"] = new[] { "block/lectern_sides" },
        ["soul_campfire_log"] = new[] { "block/soul_campfire_log_lit" },
        ["respawn_anchor_side"] = new[] { "block/respawn_anchor_side0" },
        ["honey_block"] = new[] { "block/honey_block_side" },
        ["calibrated_sculk_sensor_side"] = new[] { "block/calibrated_sculk_sensor_input_side" },
        ["pointed_dripstone"] = new[] { "block/pointed_dripstone_down_tip" },
        ["sniffer_egg"] = new[] { "block/sniffer_egg_not_cracked_north", "block/sniffer_egg_not_cracked" },
        ["cocoa_stage3"] = new[] { "block/cocoa_stage2" },
        ["redstone_dust_line"] = new[] { "block/redstone_dust_line0" },
        ["stem_stage7"] = new[] { "block/pumpkin_stem", "block/melon_stem" },
        ["attached_stem"] = new[] { "block/attached_pumpkin_stem", "block/attached_melon_stem" },
        // particle sprites (best-effort; procedural fallback is fine)
        ["smoke_particle"] = new[] { "particle/big_smoke_2", "particle/generic_3" },
        ["flame_particle"] = new[] { "particle/flame" },
        ["heart_particle"] = new[] { "particle/heart" },
        ["angry_particle"] = new[] { "particle/angry" },
        ["crit_particle"] = new[] { "particle/critical_hit" },
        ["splash_particle"] = new[] { "particle/splash_0" },
        ["bubble_particle"] = new[] { "particle/bubble" },
        ["note_particle"] = new[] { "particle/note" },
        ["soul_particle"] = new[] { "particle/soul_1", "particle/soul_0" },
        ["sweep_particle"] = new[] { "particle/sweep_2", "particle/sweep_0" },
        ["slime_particle"] = new[] { "item/slime_ball" },
        ["snow_particle"] = new[] { "particle/snowflake" },
        ["petal_particle"] = new[] { "particle/cherry_0", "particle/glow" },
        ["portal_particle"] = new[] { "particle/glow" },
        ["redstone_particle"] = new[] { "particle/glitter_0" },
        ["enchant_particle"] = new[] { "particle/sga_a" },
        // entity-textured / shader-effect blocks: stay procedural
        ["air"] = Array.Empty<string>(), ["cave_air"] = Array.Empty<string>(), ["void_air"] = Array.Empty<string>(),
        ["end_portal"] = Array.Empty<string>(), ["chest_side"] = Array.Empty<string>(), ["ender_chest_side"] = Array.Empty<string>(),
        ["decorated_pot_side"] = Array.Empty<string>(), ["bell_body"] = Array.Empty<string>(),
    };

    // fixed vanilla tints to bake (engine renders these untinted, MC art grayscale)
    private static readonly Dictionary<string, int> BakeTints = new()
    {
        ["birch_leaves"] = 0x80A755,
        ["spruce_leaves"] = 0x619961,
        ["redstone_dust_dot"] = 0xFF3030,
        ["redstone_dust_line"] = 0xFF3030,
    };

    private static string[] Candidates(string tile)
    {
        if (NameMap.TryGetValue(tile, out var m)) return m;
        if (tile.StartsWith("destroy_") && int.TryParse(tile.Substring("destroy_".Length), out int n))
            return new[] { $"block/destroy_stage_{n}" };
        if (tile.StartsWith("stem_stage") && int.TryParse(tile.Substring("stem_stage".Length), out _))
            return new[] { "block/pumpkin_stem", "block/melon_stem" };
        return new[] { $"block/{tile}" };
    }

    // tiles built by stacking MC top/bottom halves into one square (door + 2-tall plants)
    private static (string top, string bottom)? CompositeHalves(string tile)
    {
        if (tile.EndsWith("_door")) return ($"block/{tile}_top", $"block/{tile}_bottom");
        if (tile == "tall_grass" || tile == "large_fern") return ($"block/{tile}_top", $"block/{tile}_bottom");
        return null;
    }

    // vanilla stem age tint: r = age*32, g = 255-age*8, b = age*4
    private static int StemTint(int age) => (Math.Min(255, age * 32) << 16) | ((255 - age * 8) << 8) | (age * 4);

    // ── entity-texture crops (beds/chests/bell/decorated pot) ─────────────────
    private struct EntityCrop
    {
        public string path;                                  // relative to texRoot, no .png
        public (double x, double y, double w, double h)[] rects;   // fractional, stacked vertically
        public bool rotate;
    }

    private static EntityCrop? EntityTileCrop(string tile)
    {
        if (tile.EndsWith("_bed_top"))
        {
            string c = tile.Substring(0, tile.Length - "_bed_top".Length);
            return new EntityCrop
            {
                path = $"entity/bed/{c}",
                rects = new[] { (6 / 64.0, 6 / 64.0, 16 / 64.0, 16 / 64.0), (6 / 64.0, 28 / 64.0, 16 / 64.0, 16 / 64.0) },
                rotate = false,
            };
        }
        if (tile.EndsWith("_bed_side"))
        {
            string c = tile.Substring(0, tile.Length - "_bed_side".Length);
            return new EntityCrop
            {
                path = $"entity/bed/{c}",
                rects = new[] { (22 / 64.0, 6 / 64.0, 6 / 64.0, 16 / 64.0) },
                rotate = true,
            };
        }
        switch (tile)
        {
            case "chest_side":
                return new EntityCrop
                {
                    path = "entity/chest/normal",
                    rects = new[] { (14 / 64.0, 14 / 64.0, 14 / 64.0, 5 / 64.0), (14 / 64.0, 33 / 64.0, 14 / 64.0, 10 / 64.0) },
                    rotate = false,
                };
            case "ender_chest_side":
                return new EntityCrop
                {
                    path = "entity/chest/ender",
                    rects = new[] { (14 / 64.0, 14 / 64.0, 14 / 64.0, 5 / 64.0), (14 / 64.0, 33 / 64.0, 14 / 64.0, 10 / 64.0) },
                    rotate = false,
                };
            case "bell_body":
                return new EntityCrop
                {
                    path = "entity/bell/bell_body",
                    rects = new[] { (6 / 32.0, 6 / 32.0, 6 / 32.0, 7 / 32.0) },
                    rotate = false,
                };
            case "decorated_pot_side":
                return new EntityCrop
                {
                    path = "entity/decorated_pot/decorated_pot_side",
                    rects = new[] { (0.0, 0.0, 1.0, 1.0) },
                    rotate = false,
                };
            default:
                return null;
        }
    }

    private static RGBAImage? CropEntityTile(Pack pack, EntityCrop crop)
    {
        var tex = LoadTexture(pack, crop.path);
        if (tex == null) return null;
        var img = tex.image;
        var pieces = new List<RGBAImage>();
        foreach (var r in crop.rects)
        {
            int x0 = (int)(r.x * img.width), y0 = (int)(r.y * img.height);
            int w = Math.Max(1, (int)(r.w * img.width)), h = Math.Max(1, (int)(r.h * img.height));
            if (x0 + w > img.width || y0 + h > img.height) return null;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = ((y0 + y) * img.width + (x0 + x)) * 4, d = (y * w + x) * 4;
                    px[d] = img.pixels[s]; px[d + 1] = img.pixels[s + 1];
                    px[d + 2] = img.pixels[s + 2]; px[d + 3] = img.pixels[s + 3];
                }
            pieces.Add(new RGBAImage(w, h, px));
        }
        RGBAImage outImg;
        if (pieces.Count == 1) outImg = pieces[0];
        else
        {
            int w = pieces[0].width;
            int h = pieces.Sum(p => p.height);
            var px = new byte[w * h * 4];
            int yOff = 0;
            foreach (var p in pieces)
            {
                for (int y = 0; y < p.height; y++)
                {
                    int s = y * p.width * 4, d = (yOff + y) * w * 4;
                    Array.Copy(p.pixels, s, px, d, p.width * 4);
                }
                yOff += p.height;
            }
            outImg = new RGBAImage(w, h, px);
        }
        if (crop.rotate)
        {
            // dst(row r, col c) = src(col r, row c): strip column 0 → tile row 0
            var px = new byte[outImg.width * outImg.height * 4];
            for (int r = 0; r < outImg.width; r++)
                for (int c = 0; c < outImg.height; c++)
                {
                    int s = (c * outImg.width + r) * 4, d = (r * outImg.height + c) * 4;
                    px[d] = outImg.pixels[s]; px[d + 1] = outImg.pixels[s + 1];
                    px[d + 2] = outImg.pixels[s + 2]; px[d + 3] = outImg.pixels[s + 3];
                }
            outImg = new RGBAImage(outImg.height, outImg.width, px);
        }
        return outImg;
    }

    // ── texture + optional .mcmeta animation load ─────────────────────────────
    private sealed class LoadedTexture
    {
        public required RGBAImage image;
        public List<(int index, int ticks)>? animFrames;   // null = not animated
        public bool interpolate;
    }

    private static LoadedTexture? LoadTexture(Pack pack, string relPath)
    {
        string full = pack.texRoot + relPath + ".png";
        var d = pack.File(full);
        if (d == null) return null;
        var img = DecodePNG(d);
        if (img == null) return null;
        var lt = new LoadedTexture { image = img };
        if (img.height > img.width && img.height % img.width == 0)
        {
            int frametime = 1;
            bool interpolate = false;
            var frames = new List<(int, int)>();
            var md = pack.File(full + ".mcmeta");
            if (md != null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(md);
                    if (doc.RootElement.TryGetProperty("animation", out var a))
                    {
                        if (a.TryGetProperty("frametime", out var ft) && ft.TryGetInt32(out int ftv)) frametime = Math.Max(1, ftv);
                        if (a.TryGetProperty("interpolate", out var ip) && ip.ValueKind == System.Text.Json.JsonValueKind.True) interpolate = true;
                        if (a.TryGetProperty("frames", out var fl) && fl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var f in fl.EnumerateArray())
                            {
                                if (f.ValueKind == System.Text.Json.JsonValueKind.Number && f.TryGetInt32(out int fi))
                                    frames.Add((fi, frametime));
                                else if (f.ValueKind == System.Text.Json.JsonValueKind.Object && f.TryGetProperty("index", out var idx) && idx.TryGetInt32(out int ii))
                                {
                                    int t = frametime;
                                    if (f.TryGetProperty("time", out var tv) && tv.TryGetInt32(out int tt)) t = Math.Max(1, tt);
                                    frames.Add((ii, t));
                                }
                            }
                        }
                    }
                }
                catch { /* malformed .mcmeta → default linear playback */ }
            }
            int count = img.height / img.width;
            if (frames.Count == 0) for (int i = 0; i < count; i++) frames.Add((i, frametime));
            frames = frames.Where(f => f.Item1 >= 0 && f.Item1 < count).ToList();
            if (frames.Count > 1) { lt.animFrames = frames; lt.interpolate = interpolate; }
        }
        return lt;
    }

    // ── robust pack-zip location ──────────────────────────────────────────────
    /// search next to the exe, then walk parents looking for packaging/<file>
    public static string? FindPackZip(string fileName = DefaultPackFile)
    {
        var candidates = new List<string>();
        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, fileName));                 // next to the exe
        candidates.Add(Path.Combine(baseDir, "packaging", fileName));
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "packaging", fileName));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "packaging", fileName));
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    // ── atlas build ───────────────────────────────────────────────────────────
    /// Open the pack zip at `path` and build per-tile slices (in tileName order),
    /// the 16× icon atlas, animations and item-icon overrides. Returns null if the
    /// zip can't be opened or has no usable texture root.
    public static PackAtlasResult? LoadPack(string path)
    {
        Pack pack;
        try { pack = new Pack(path); }
        catch { return null; }
        using (pack)
        {
            if (string.IsNullOrEmpty(pack.texRoot)) return null;

            var baseAtlas = PebbleCore.AtlasGlobals.buildAtlas();    // procedural fallback substrate (16×)
            var names = PebbleCore.Reg.allTileNames();
            int count = names.Count;

            // resolve every tile to (texture, animation) | composite halves | entity crop
            var resolved = new LoadedTexture[count];
            var compositeSrcs = new (RGBAImage top, RGBAImage bottom)?[count];
            var entityTiles = new RGBAImage[count];
            for (int i = 0; i < count; i++)
            {
                string name = names[i];
                var halves = CompositeHalves(name);
                if (halves != null)
                {
                    var t = LoadTexture(pack, halves.Value.top)?.image;
                    var b = LoadTexture(pack, halves.Value.bottom)?.image;
                    if (t != null && b != null)
                    {
                        if (t.height > t.width) t = StripFrame(t, 0);
                        if (b.height > b.width) b = StripFrame(b, 0);
                        compositeSrcs[i] = (t, b);
                    }
                    continue;
                }
                foreach (var c in Candidates(name))
                {
                    var t = LoadTexture(pack, c);
                    if (t != null) { resolved[i] = t; break; }
                }
                if (resolved[i] == null)
                {
                    var ec = EntityTileCrop(name);
                    if (ec != null)
                    {
                        var img = CropEntityTile(pack, ec.Value);
                        if (img != null) entityTiles[i] = img;
                    }
                }
            }

            // one atlas resolution: the DOMINANT native size among resolved tiles,
            // clamped to [16,128]. We use the mode rather than the max because a
            // handful of oddball HD sprites (e.g. the 64px sweep/particle art in a
            // 32× pack) must not force the whole atlas — and every tile up/down-
            // scales to the chosen res anyway. Terrain (the overwhelming majority)
            // is 32× in Faithful, so the mode lands on 32.
            var widthVotes = new Dictionary<int, int>();
            void Vote(int w) { int c = Math.Min(128, Math.Max(16, w)); widthVotes[c] = widthVotes.GetValueOrDefault(c) + 1; }
            for (int i = 0; i < count; i++)
            {
                if (resolved[i] != null) Vote(resolved[i].image.width);
                else if (compositeSrcs[i] is { } cs) Vote(Math.Max(cs.top.width, cs.bottom.width));
                else if (entityTiles[i] != null) Vote(entityTiles[i].width);
            }
            int res = 16;
            int bestVotes = -1;
            foreach (var kv in widthVotes)
                if (kv.Value > bestVotes || (kv.Value == bestVotes && kv.Key > res)) { res = kv.Key; bestVotes = kv.Value; }

            var slices = new byte[count][];
            var icon16 = new byte[count][];
            var animations = new List<TileAnimation>();
            bool fluidAnimated = false;
            int applied = 0;

            for (int i = 0; i < count; i++)
            {
                string name = names[i];
                byte[] px;
                if (resolved[i] != null)
                {
                    applied++;
                    var t = resolved[i];
                    if (t.animFrames != null)
                    {
                        int frameCount = t.image.height / t.image.width;
                        var frames = new List<byte[]>(frameCount);
                        for (int f = 0; f < frameCount; f++)
                        {
                            var fp = ScaleTo(StripFrame(t.image, f), res);
                            if (BakeTints.TryGetValue(name, out int bake)) BakeTint(fp, bake);
                            frames.Add(fp);
                        }
                        px = frames[t.animFrames[0].index];
                        var anim = new TileAnimation { slice = i, frames = frames, order = t.animFrames, interpolate = t.interpolate };
                        animations.Add(anim);
                        if (name == "water" || name == "lava" || name == "fire" || name == "soul_fire") fluidAnimated = true;
                    }
                    else
                    {
                        var img = t.image;
                        if (img.height > img.width) img = StripFrame(img, 0);   // strip without anim meta
                        px = ScaleTo(img, res);
                        if (BakeTints.TryGetValue(name, out int bake)) BakeTint(px, bake);
                        if (name.StartsWith("stem_stage") && int.TryParse(name.Substring("stem_stage".Length), out int age))
                        {
                            int keep = res * 2 * (age + 1) / 16;
                            for (int y = 0; y < res - keep; y++)
                                for (int x = 0; x < res; x++) px[(y * res + x) * 4 + 3] = 0;
                            BakeTint(px, StemTint(age));
                        }
                        else if (name == "attached_stem") BakeTint(px, StemTint(7));
                    }
                }
                else if (entityTiles[i] != null)
                {
                    applied++;
                    px = ScaleTo(entityTiles[i], res);
                }
                else if (compositeSrcs[i] is { } cs)
                {
                    applied++;
                    px = new byte[res * res * 4];
                    int half = res / 2;
                    var t = ScaleTo(cs.top, res);
                    var b = ScaleTo(cs.bottom, res);
                    for (int y = 0; y < half; y++)
                        for (int x = 0; x < res; x++)
                        {
                            int sT = ((y * 2) * res + x) * 4, dT = (y * res + x) * 4;
                            px[dT] = t[sT]; px[dT + 1] = t[sT + 1]; px[dT + 2] = t[sT + 2]; px[dT + 3] = t[sT + 3];
                            int sB = ((y * 2) * res + x) * 4, dB = ((y + half) * res + x) * 4;
                            px[dB] = b[sB]; px[dB + 1] = b[sB + 1]; px[dB + 2] = b[sB + 2]; px[dB + 3] = b[sB + 3];
                        }
                }
                else
                {
                    // procedural substrate, upscaled to the pack resolution (air,
                    // portal, and other shader-/entity-effect tiles the pack lacks)
                    if (Environment.GetEnvironmentVariable("PEBBLE_PACKDEBUG") != null)
                        Console.WriteLine($"[packs] no pack art for tile {i}: {name} (procedural fallback)");
                    px = ScaleNearest(new RGBAImage(16, 16, baseAtlas.pixels[i]), res);
                }
                slices[i] = px;
                icon16[i] = ScaleBox(new RGBAImage(res, res, px), 16);
            }

            // item icons: every textures/item/*.png in the pack, 16× for the icon cache
            var itemIcons = new Dictionary<string, byte[]>();
            foreach (var p in pack.List(pack.texRoot + "item/"))
            {
                if (!p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                string fileName = p.Substring(p.LastIndexOf('/') + 1);
                string itemName = fileName.Substring(0, fileName.Length - 4);
                var d = pack.File(p);
                if (d == null) continue;
                var img = DecodePNG(d);
                if (img == null) continue;
                if (img.height > img.width && img.height % img.width == 0) img = StripFrame(img, 0);
                if (img.width != img.height) continue;
                itemIcons[itemName] = ScaleBox(img, 16);
            }

            return new PackAtlasResult
            {
                res = res,
                slices = slices,
                icon16 = new PebbleCore.BuiltAtlas(count, icon16, Array.Empty<string>()),
                animations = animations,
                itemIcons = itemIcons,
                fluidAnimated = fluidAnimated,
                appliedTiles = applied,
                appliedItems = itemIcons.Count,
            };
        }
    }

    // ── one-shot load used by the backends at atlas-build time ────────────────
    private static bool _attempted;
    private static PackAtlasResult? _cached;

    /// Load the default Faithful pack once and cache the result; null if missing.
    /// Both backends call this from their atlas init so they share one decode.
    public static PackAtlasResult? LoadDefault()
    {
        if (_attempted) return _cached;
        _attempted = true;
        string? zip = FindPackZip();
        if (zip == null)
        {
            Console.WriteLine($"[packs] {DefaultPackFile} not found — using procedural atlas");
            return null;
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _cached = LoadPack(zip);
            sw.Stop();
            if (_cached != null)
            {
                // item-icon overrides for the UI: block-item iso-cubes already use
                // the pack tiles via initIcons; this covers flat item sprites too.
                var icons = _cached.itemIcons;
                if (icons.Count > 0)
                    PebbleCore.IconGlobals.itemIconOverride = name => icons.TryGetValue(name, out var b) ? b : null;
                // block icons (iso-cubes / flat tiles) sample from the pack atlas
                PebbleCore.IconGlobals.initIcons(_cached.icon16);
                Console.WriteLine($"[packs] {Path.GetFileName(zip)} → {_cached.appliedTiles}/{_cached.slices.Length} tiles, " +
                                  $"{_cached.appliedItems} item icons, {_cached.animations.Count} animated, {_cached.res}x atlas ({sw.ElapsedMilliseconds}ms)");
            }
            else Console.WriteLine($"[packs] failed to read {zip} — using procedural atlas");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[packs] error loading pack: {ex.Message} — using procedural atlas");
            _cached = null;
        }
        return _cached;
    }
}

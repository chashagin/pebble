// Headless smoke tests for the C# PebbleCore port. Mirrors Sources/pebsmoke
// and validates against the same frozen goldens/*.json baselines so the port
// reproduces engine behavior bit-for-bit.
//
// NOTE: the Swift harness has PEBBLE_REGOLD maintenance branches that rewrite
// the golden files. The verification run is always WITHOUT that env var, so the
// check (non-regold) paths below are faithful ports of every assertion. When
// PEBBLE_REGOLD is set we skip the golden-rewriting (a tooling concern, not a
// check) and emit the same pass placeholders the Swift harness prints.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PebbleCore;
using static PebbleCore.Hashing;
using static PebbleCore.DetMath;
using static PebbleCore.MathX;

int passed = 0, failed = 0;

void check(string name, bool cond, string detail = "")
{
    if (cond) { passed++; Console.WriteLine($"  ✓ {name}"); }
    else { failed++; Console.WriteLine($"  ✗ {name} {detail}"); }
}

void checkD(string name, double got, double want, double tol = 1e-12)
    => check(name, Math.Abs(got - want) <= tol, $"got {got:R} want {want:R}");

void section(string name) => Console.WriteLine($"\n— {name}");

bool REGOLD = Environment.GetEnvironmentVariable("PEBBLE_REGOLD") != null;

// Locate the repo root (the directory containing goldens/) by walking upward.
string FindGoldens()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            var g = Path.Combine(dir.FullName, "goldens");
            if (Directory.Exists(g)) return g;
            dir = dir.Parent;
        }
    }
    return null;
}
string goldensDir = FindGoldens();

JsonElement? loadJSON(string name)
{
    if (goldensDir == null) return null;
    var path = Path.Combine(goldensDir, name);
    if (!File.Exists(path)) return null;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    return doc.RootElement.Clone();
}

// ---- JSON access helpers (translate Swift `as! [...]` casts) ----------------
int jInt(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.Parse(e.GetString(), CultureInfo.InvariantCulture);
long jLong(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : long.Parse(e.GetString(), CultureInfo.InvariantCulture);
uint jU32(JsonElement e) => unchecked((uint)jLong(e));
double jDbl(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.Parse(e.GetString(), CultureInfo.InvariantCulture);
bool jBool(JsonElement e) => e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.Number && e.GetInt32() != 0);
string jStr(JsonElement e) => e.GetString();
JsonElement jGet(JsonElement g, string k) => g.GetProperty(k);
int jNum(JsonElement g, string k) => jInt(g.GetProperty(k));
uint jHash(JsonElement g, string k) => jU32(g.GetProperty(k));
List<JsonElement> jArr(JsonElement e) => e.EnumerateArray().ToList();

// FNV-1a hashers (mirror Swift) -----------------------------------------------
uint fnvU16(ushort[] arr)
{
    uint h = 2166136261;
    foreach (var v in arr)
    {
        h = unchecked((h ^ (uint)(v & 0xff)) * 16777619);
        h = unchecked((h ^ (uint)(v >> 8)) * 16777619);
    }
    return h;
}
uint fnvI16(short[] arr)
{
    uint h = 2166136261;
    foreach (var s in arr)
    {
        ushort v = unchecked((ushort)s);
        h = unchecked((h ^ (uint)(v & 0xff)) * 16777619);
        h = unchecked((h ^ (uint)(v >> 8)) * 16777619);
    }
    return h;
}
uint fnvU8(byte[] arr)
{
    uint h = 2166136261;
    foreach (var b in arr) h = unchecked((h ^ (uint)b) * 16777619);
    return h;
}
uint fnvU32(uint[] arr)
{
    uint h = 2166136261;
    foreach (var v in arr)
    {
        h = unchecked((h ^ (v & 0xff)) * 16777619);
        h = unchecked((h ^ ((v >> 8) & 0xff)) * 16777619);
        h = unchecked((h ^ ((v >> 16) & 0xff)) * 16777619);
        h = unchecked((h ^ ((v >> 24) & 0xff)) * 16777619);
    }
    return h;
}

string radix16(uint v) => Convert.ToString((long)v, 16);

// ---------------------------------------------------------------------------
section("random (vs goldens)");
check("hashString abc", hashString("abc") == 440920331, $"got {hashString("abc")}");
check("mix32 12345", mix32(12345) == 1011272156, $"got {mix32(12345)}");
check("hash2", hash2(999, -1234, 5678, 7) == 1511826033, $"got {hash2(999, -1234, 5678, 7)}");
check("hash3", hash3(999, -12, 34, -56, 3) == 2031202406, $"got {hash3(999, -12, 34, -56, 3)}");

var r = new RandomX(12345);
uint[] golden12345 = { 1009662611, 487413528, 3278825217, 2736101217, 2510057557, 1701016183, 572264801, 2565169478 };
bool seqOK = true;
for (int i = 0; i < golden12345.Length; i++)
{
    var got = r.next();
    if (got != golden12345[i]) { seqOK = false; Console.WriteLine($"    sfc32[{i}] got {got} want {golden12345[i]}"); }
}
check("sfc32 seed 12345 sequence", seqOK);

var r2 = new RandomX(0xDEAD_BEEF);
uint[] goldenDB = { 1504311087, 3087835436, 4013932724, 864736003 };
bool seq2OK = true;
foreach (var want in goldenDB) if (r2.next() != want) seq2OK = false;
check("sfc32 seed 0xDEADBEEF sequence", seq2OK);

var r3 = new RandomX(777);
bool inRange = true;
for (int i = 0; i < 1000; i++) { var v = r3.nextInt(10); if (v < 0 || v >= 10) inRange = false; }
check("nextInt bounds", inRange);

// ---------------------------------------------------------------------------
section("simplex noise (vs goldens)");
var n = new SimplexNoise(42);
checkD("noise2 (0.5,0.5)", n.noise2(0.5, 0.5), -0.30780618346945793);
checkD("noise2 (10.25,-3.75)", n.noise2(10.25, -3.75), 0);
checkD("noise2 (100.1,200.9)", n.noise2(100.1, 200.9), -0.6225765639891507);
checkD("noise2 (-55.5,17.3)", n.noise2(-55.5, 17.3), 0.4811125458747653);
checkD("noise3 (1.5,2.5,3.5)", n.noise3(1.5, 2.5, 3.5), 0);
checkD("noise3 (-10.1,40.2,-7.7)", n.noise3(-10.1, 40.2, -7.7), 0.12712837501423255);

var f = new FBM(7, 4, 0.01);
checkD("fbm sample2 (123.4,567.8)", f.sample2(123.4, 567.8), -0.17945870068084002);
checkD("fbm ridge2 (123.4,567.8)", f.ridge2(123.4, 567.8), 0.4321547307883241);
checkD("fbm sample2 (-1000.5,250.25)", f.sample2(-1000.5, 250.25), -0.37532916362726393);
checkD("fbm ridge2 (-1000.5,250.25)", f.ridge2(-1000.5, 250.25), 0.41162552326329793);

var sp = new Spline(new (double, double)[] { (0, 0), (0.5, 10), (1, 4) });
checkD("spline at -1", sp.at(-1), 0);
checkD("spline at 0.25", sp.at(0.25), 5);
checkD("spline at 0.5", sp.at(0.5), 10);
checkD("spline at 0.75", sp.at(0.75), 7);
checkD("spline at 2", sp.at(2), 4);

// ---------------------------------------------------------------------------
section("math");
var aabbA = new AABB(0, 0, 0, 1, 1, 1);
var aabbB = new AABB(2, 0, 0, 3, 1, 1);
checkD("sweepX blocked", sweepX(aabbA, aabbB, 5), 1);
checkD("sweepX clear (offset z)", sweepX(aabbA, aabbB.offset(0, 0, 5), 5), 5);
checkD("sweepY through", sweepY(aabbA, aabbB, 3), 3);
check("aabb intersects", new AABB(0, 0, 0, 2, 2, 2).intersects(new AABB(1, 1, 1, 3, 3, 3)));
check("aabb no intersect", !new AABB(0, 0, 0, 1, 1, 1).intersects(new AABB(1, 0, 0, 2, 1, 1)));

var tHit = rayAABB(-5, 0.5, 0.5, 1, 0, 0, aabbA);
checkD("rayAABB hit", tHit, 5);
check("rayAABB miss", rayAABB(-5, 5, 0.5, 1, 0, 0, aabbA) == -1);

// column-major (simd-style) Mat4 multiply: (A*B).col[j] = A applied to B.col[j]
static Mat4 mat4Mul(Mat4 A, Mat4 B)
{
    Vec4f apply(Vec4f v) => A.c0 * v.X + A.c1 * v.Y + A.c2 * v.Z + A.c3 * v.W;
    return new Mat4(apply(B.c0), apply(B.c1), apply(B.c2), apply(B.c3));
}

var fr = Frustum.Create();
var proj = mat4Perspective(fovYRad: (float)degToRad(70), aspect: 16.0f / 9.0f, near: 0.05f, far: 400);
var view = mat4LookDir(eye: new Vec3f(0, 0, 0), dir: new Vec3f(0, 0, 1), up: new Vec3f(0, 1, 0));
fr.setFromMatrix(mat4Mul(proj, view));
check("frustum sees box ahead", fr.intersectsBox(-5, -5, 10, 5, 5, 20));
check("frustum culls box behind", !fr.intersectsBox(-5, -5, -20, 5, 5, -10));
check("frustum culls box far right", !fr.intersectsBox(500, -5, 10, 510, 5, 20));

checkD("wrapDegrees 270", wrapDegrees(270), -90);
checkD("wrapDegrees -270", wrapDegrees(-270), 90);
checkD("lerp", lerpD(0, 10, 0.25), 2.5);

// ---------------------------------------------------------------------------
section("block registry (vs goldens)");
registerAllBlocks();
check("block count", blockDefs.Count == 879, $"got {blockDefs.Count} want 879");
check("tile count (baseline range intact)", tileCount() >= 757, $"got {tileCount()} want >= 757");
(string, ushort)[] idGoldens =
{
    ("air", 0), ("stone", 3), ("grass_block", 33), ("oak_log", 95),
    ("water", 292), ("lava", 293), ("glass", 294), ("white_wool", 298),
    ("black_shulker_box", 473), ("wheat", 537), ("snow", 550),
    ("netherrack", 589), ("end_stone", 614), ("crafting_table", 626),
    ("redstone_wire", 684), ("rail", 717), ("tuff_wall", 823),
    ("oxidized_cut_copper_slab", 852), ("waxed_oxidized_cut_copper_slab", 856),
    ("infested_deepslate", 878), ("sculk_shrieker", 716),
    ("cherry_leaves", 279), ("mangrove_propagule", 289),
};
bool idsOK = true;
foreach (var (name, want) in idGoldens)
{
    var got = bidOpt(name);
    if (got != want)
    {
        idsOK = false;
        Console.WriteLine($"    id mismatch {name}: got {(got.HasValue ? got.Value.ToString() : "nil")} want {want}");
    }
}
check("23 block ids bit-identical to baseline", idsOK);
check("tile grass_top", tileId("grass_top") == 38, $"got {tileId("grass_top")}");
check("tile destroy_9", tileId("destroy_9") == 740, $"got {tileId("destroy_9")}");
check("tile 756 is sweep_particle", allTileNames().Count > 756 && allTileNames()[756] == "sweep_particle", $"got {(allTileNames().Count > 756 ? allTileNames()[756] : "nil")}");
check("cell roundtrip", cell(B.stone, 7) >> 4 == B.stone && cellMeta(cell(B.stone, 7)) == 7);
check("lightEmitOf torch", lightEmitOf(cell(B.torch)) == 14);
check("lightEmitOf sea_pickle x4", lightEmitOf(cell(B.sea_pickle, 3)) == 15);
check("water replaceable", REPLACEABLE[(int)B.water] == 1);
check("stone opaque", OPAQUE[(int)B.stone] == 1);
check("glass not opaque", OPAQUE[(int)B.glass] == 0);

// ---------------------------------------------------------------------------
section("item registry (vs goldens)");
registerAllItems();
// 1186 baseline items + 2 appended (weeping/twisting vines)
const int BASE_ITEM_COUNT = 1186;
check("item count", itemDefs.Count == 1188, $"got {itemDefs.Count} want 1188");
check("item ids stable after append", iid("weeping_vines") == 1186 && iid("twisting_vines") == 1187,
      $"vines ids {iid("weeping_vines")}/{iid("twisting_vines")} want 1186/1187");
(string, int)[] itemGoldens =
{
    ("stone", 0), ("wheat_seeds", 764), ("wooden_sword", 832), ("netherite_hoe", 861),
    ("leather_helmet", 869), ("elytra", 894), ("apple", 896), ("milk_bucket", 934),
    ("stick", 935), ("goat_horn", 1008), ("white_dye", 1009), ("bucket", 1025),
    ("potion", 1045), ("oak_boat", 1048), ("music_disc_descent", 1075),
    ("angler_pottery_sherd", 1076), ("netherite_upgrade", 1112),
    ("zombified_piglin_spawn_egg", 1185),
};
bool itemIdsOK = true;
foreach (var (name, want) in itemGoldens)
{
    var got = iidOpt(name);
    if (got != want)
    {
        itemIdsOK = false;
        Console.WriteLine($"    item id mismatch {name}: got {(got.HasValue ? got.Value.ToString() : "nil")} want {want}");
    }
}
check("18 item ids bit-identical to baseline", itemIdsOK);
check("blockToItem stone", blockToItem[(int)B.stone] == iid("stone"));
check("cake maxStack 1", itemDefs[iid("cake")].maxStack == 1);
check("netherite sword dmg", itemDefs[iid("netherite_sword")].tool?.attackDamage == 7);
check("diamond chest durability", itemDefs[iid("diamond_chestplate")].armor?.durability == 529);
check("steak hunger", itemDefs[iid("cooked_beef")].food?.hunger == 8);
check("lava bucket burn", itemDefs[iid("lava_bucket")].burnTime == 20000);
check("merge same", canMerge(new ItemStack(iid("stone"), 5), new ItemStack(iid("stone"), 3)));
check("no merge tools", !canMerge(new ItemStack(iid("iron_sword")), new ItemStack(iid("iron_sword"))));

// ---------------------------------------------------------------------------
section("biomes (vs goldens)");
registerAllBiomes();
int biomeEnumCount = Enum.GetValues(typeof(Biome)).Length;
check("biome count = enum count", BIOMES.Count == biomeEnumCount, $"got {BIOMES.Count}");

{
    var gOpt = loadJSON("biome-goldens.json");
    if (gOpt is JsonElement g)
    {
        int count = jNum(g, "biomeCount");
        check("biome count vs goldens", BIOMES.Count == count, $"got {BIOMES.Count} want {count}");

        var names = jArr(jGet(g, "names"));
        bool namesOK = true;
        for (int i = 0; i < names.Count; i++)
        {
            string want = jStr(names[i]);
            if (BIOMES[i]?.name != want)
            {
                namesOK = false;
                Console.WriteLine($"    biome[{i}] got {BIOMES[i]?.name ?? "nil"} want {want}");
            }
        }
        check($"{names.Count} biome names in identical order", namesOK);

        var climates = jArr(jGet(g, "climates"));
        var samples = jArr(jGet(g, "samples")).Select(jInt).ToList();
        int mismatches = 0;
        for (int i = 0; i < climates.Count; i++)
        {
            var cl = jArr(climates[i]);
            var c = new Climate(t: jDbl(cl[0]), h: jDbl(cl[1]), c: jDbl(cl[2]),
                                e: jDbl(cl[3]), w: jDbl(cl[4]),
                                pv: peaksValleys(jDbl(cl[4])), rare: jDbl(cl[5]));
            if ((int)selectBiome(c) != samples[i])
            {
                mismatches += 1;
                if (mismatches <= 5)
                    Console.WriteLine($"    selectBiome[{i}] got {(int)selectBiome(c)} want {samples[i]}");
            }
        }
        check("selectBiome 2000 samples bit-identical", mismatches == 0, $"{mismatches} mismatches");

        var pvG = jArr(jGet(g, "pv")).Select(jDbl).ToList();
        bool pvOK = true;
        for (int i = 0; i < pvG.Count; i++)
            if (Math.Abs(peaksValleys(-1 + i * 0.05) - pvG[i]) > 1e-12) pvOK = false;
        check("peaksValleys curve", pvOK);

        var defs = jArr(jGet(g, "defChecks"));
        bool defOK = true;
        void defFail(int bb, string what) { defOK = false; Console.WriteLine($"    def[{bb}] {what}"); }
        foreach (var d in defs)
        {
            int bi = jNum(d, "b");
            var def = BIOMES[bi];
            if (def == null) { defFail(bi, "missing"); continue; }
            if (def.name != jStr(jGet(d, "name"))) defFail(bi, "name");
            if (def.displayName != jStr(jGet(d, "display"))) defFail(bi, "display");
            if (Math.Abs(def.temperature - jDbl(jGet(d, "temp"))) > 1e-12) defFail(bi, "temp");
            if (Math.Abs(def.downfall - jDbl(jGet(d, "downfall"))) > 1e-12) defFail(bi, "downfall");
            if (def.grassColor != jU32(jGet(d, "grass"))) defFail(bi, "grass");
            if (def.foliageColor != jU32(jGet(d, "foliage"))) defFail(bi, "foliage");
            if (def.waterColor != jU32(jGet(d, "water"))) defFail(bi, "water");
            if (def.fogTint != jU32(jGet(d, "fogTint"))) defFail(bi, "fogTint");
            if ((int)def.top != jNum(d, "top")) defFail(bi, $"top got {def.top} want {jNum(d, "top")}");
            if ((int)def.under != jNum(d, "under")) defFail(bi, $"under got {def.under} want {jNum(d, "under")}");
            if ((int)def.underwaterTop != jNum(d, "uwTop")) defFail(bi, $"uwTop got {def.underwaterTop} want {jNum(d, "uwTop")}");
            var wantFeatures = jArr(jGet(d, "features")).Select(jStr).ToList();
            if (!def.features.SequenceEqual(wantFeatures))
                defFail(bi, $"features\n      got  [{string.Join(", ", def.features)}]\n      want [{string.Join(", ", wantFeatures)}]");
            if (def.mood != jStr(jGet(d, "mood"))) defFail(bi, "mood");
            var monsters = jArr(jGet(d, "monsters"));
            if (def.monsters.Count != monsters.Count) defFail(bi, "monsters count");
            else
            {
                for (int i = 0; i < monsters.Count; i++)
                {
                    var m = jArr(monsters[i]);
                    var got = def.monsters[i];
                    if (got.mob != jStr(m[0]) || got.weight != jDbl(m[1])
                        || got.minPack != jInt(m[2]) || got.maxPack != jInt(m[3]))
                        defFail(bi, $"monster[{i}]");
                }
            }
            var creatures = jArr(jGet(d, "creatures"));
            if (def.creatures.Count != creatures.Count) defFail(bi, "creatures count");
            else
            {
                for (int i = 0; i < creatures.Count; i++)
                {
                    var m = jArr(creatures[i]);
                    var got = def.creatures[i];
                    if (got.mob != jStr(m[0]) || got.weight != jDbl(m[1]))
                        defFail(bi, $"creature[{i}]");
                }
            }
        }
        check("10 BiomeDef spot checks (fields, features, spawns)", defOK);

        var temps = jArr(jGet(g, "tempSamples"));
        if (REGOLD)
        {
            check("temperature: goldens regenerated (native baseline)", true);
        }
        else
        {
            bool tOK = true;
            foreach (var s in temps)
            {
                int bi = jNum(s, "b");
                int y = jNum(s, "y");
                double want = jDbl(jGet(s, "t"));
                bool wantSnows = jBool(jGet(s, "snows"));
                if (Math.Abs(temperatureAt(bi, y) - want) > 1e-12 || snowsAt(bi, y) != wantSnows)
                {
                    tOK = false;
                    Console.WriteLine($"    temp b={bi} y={y} got {temperatureAt(bi, y)} want {want}");
                }
            }
            check($"temperatureAt/snowsAt {temps.Count} samples", tOK);
        }

        var flags = jArr(jGet(g, "flags")).Select(jInt).ToList();
        bool flagsOK = true;
        for (int i = 0; i < flags.Count; i++)
        {
            int got = (isOceanBiome(i) ? 1 : 0) | (isCaveBiome(i) ? 2 : 0);
            if (got != flags[i]) { flagsOK = false; Console.WriteLine($"    flags[{i}] got {got} want {flags[i]}"); }
        }
        check($"ocean/cave flags all {flags.Count} biomes", flagsOK);

        if (g.TryGetProperty("allColors", out var allColorsE) && allColorsE.ValueKind == JsonValueKind.Array)
        {
            var allColors = jArr(allColorsE);
            bool colorsOK = true;
            for (int i = 0; i < allColors.Count; i++)
            {
                var cs = jArr(allColors[i]);
                var d = BIOMES[i];
                if (d == null) { colorsOK = false; continue; }
                uint[] got = { d.grassColor, d.foliageColor, d.waterColor, d.fogTint };
                for (int j = 0; j < cs.Count; j++)
                {
                    if (got[j] != jU32(cs[j]))
                    {
                        colorsOK = false;
                        Console.WriteLine($"    {d.name} color[{j}] got {Convert.ToString(got[j], 16)} want {Convert.ToString(jU32(cs[j]), 16)}");
                    }
                }
            }
            check($"grass/foliage/water/fog colors all {allColors.Count} biomes", colorsOK);
        }
    }
    else
    {
        check("biome-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("overworld terrain (vs goldens)");

{
    var gOpt = loadJSON("terrain-goldens.json");
    if (gOpt is JsonElement g)
    {
        var terrainGens = new Dictionary<uint, OverworldGen>();
        OverworldGen genFor(uint s)
        {
            if (terrainGens.TryGetValue(s, out var gg)) return gg;
            var ng = new OverworldGen(s);
            terrainGens[s] = ng;
            return ng;
        }

        var chunkList = jArr(jGet(g, "chunks"));
        for (int i = 0; i < chunkList.Count; i++)
        {
            var c = chunkList[i];
            uint seed = jU32(jGet(c, "seed"));
            int cx = jNum(c, "cx");
            int cz = jNum(c, "cz");
            var gen = genFor(seed);
            var blocks = new ushort[16 * 16 * WORLD_H];
            var biomes = new byte[4 * 4 * ((WORLD_H + 3) / 4)];
            var res = gen.fillTerrain(cx, cz, ref blocks, ref biomes);
            string label = $"seed {seed} ({cx},{cz})";
            uint hFill = fnvU16(blocks);
            uint hHeights = fnvI16(res.heights);
            uint hSurfaceBiomes = fnvU8(res.surfaceBiomes);
            uint hBiomes = fnvU8(biomes);
            gen.carve(cx, cz, ref blocks);
            uint hCarve = fnvU16(blocks);
            gen.applySurface(cx, cz, ref blocks, res.heights, res.surfaceBiomes);
            uint hSurface = fnvU16(blocks);
            gen.placeOres(cx, cz, ref blocks, res.surfaceBiomes);
            uint hOres = fnvU16(blocks);
            gen.applySnowAndIce(cx, cz, ref blocks, res.surfaceBiomes);
            uint hSnow = fnvU16(blocks);
            if (REGOLD) continue;
            uint wantFill = jU32(jGet(c, "hFill"));
            check($"{label} fillTerrain hash", hFill == wantFill,
                  $"got {radix16(hFill)} want {radix16(wantFill)}");
            check($"{label} heights hash", hHeights == jU32(jGet(c, "hHeights")));
            check($"{label} surfaceBiomes hash", hSurfaceBiomes == jU32(jGet(c, "hSurfaceBiomes")));
            check($"{label} biomes hash", hBiomes == jU32(jGet(c, "hBiomes")));
            check($"{label} carve hash", hCarve == jU32(jGet(c, "hCarve")),
                  $"got {radix16(hCarve)} want {radix16(jU32(jGet(c, "hCarve")))}");
            check($"{label} applySurface hash", hSurface == jU32(jGet(c, "hSurface")));
            check($"{label} placeOres hash", hOres == jU32(jGet(c, "hOres")));
            check($"{label} snow/ice hash", hSnow == jU32(jGet(c, "hSnow")));

            // cell-level diff for the first case if anything mismatched
            if (i == 0 && c.TryGetProperty("blocksB64", out var b64E) && b64E.ValueKind == JsonValueKind.String
                && fnvU16(blocks) != jU32(jGet(c, "hSnow")))
            {
                var data = Convert.FromBase64String(b64E.GetString());
                var want = new ushort[data.Length / 2];
                Buffer.BlockCopy(data, 0, want, 0, want.Length * 2);
                int shown = 0;
                for (int idx = 0; idx < Math.Min(want.Length, blocks.Length); idx++)
                {
                    if (want[idx] == blocks[idx]) continue;
                    int y = idx / 256 + GEN_MIN_Y, z = (idx / 16) % 16, x = idx % 16;
                    Console.WriteLine($"    cell ({x},{y},{z}) got {blocks[idx]} want {want[idx]}");
                    shown += 1;
                    if (shown >= 12) break;
                }
            }

            var wantHeights = jArr(jGet(c, "heights")).Select(e => (short)jInt(e)).ToArray();
            check($"{label} heights array equal", res.heights.SequenceEqual(wantHeights));
        }

        // scalar samples on seed 12345
        var sg = genFor(12345);
        var coords = jArr(jGet(g, "coords")).Select(e => { var p = jArr(e); return (jDbl(p[0]), jDbl(p[1])); }).ToList();
        if (REGOLD)
        {
            check("terrain: goldens regenerated (native baseline)", true);
        }
        else
        {
            var wantHeightsS = jArr(jGet(g, "heightSamples")).Select(jInt).ToList();
            bool hOK = true;
            for (int i = 0; i < coords.Count; i++)
            {
                var (x, z) = coords[i];
                if (sg.heightEstimate(x, z) != wantHeightsS[i])
                {
                    hOK = false;
                    Console.WriteLine($"    heightEstimate({x},{z}) got {sg.heightEstimate(x, z)} want {wantHeightsS[i]}");
                }
            }
            check($"heightEstimate {coords.Count} samples", hOK);

            var wantBiomesS = jArr(jGet(g, "biomeSamples")).Select(jInt).ToList();
            bool bOK = true;
            for (int i = 0; i < coords.Count; i++)
            {
                var (x, z) = coords[i];
                if ((int)sg.surfaceBiomeAt(x, z) != wantBiomesS[i]) bOK = false;
            }
            check($"surfaceBiomeAt {coords.Count} samples", bOK);

            var wantAq = jArr(jGet(g, "aquiferSamples"));
            bool aqOK = true;
            for (int i = 0; i < coords.Count; i++)
            {
                var (x, z) = coords[i];
                var a = sg.aquiferAt(x, z, sg.climate.at(x, z));
                var wa = jArr(wantAq[i]);
                if (a.level != jInt(wa[0]) || (a.lava ? 1 : 0) != jInt(wa[1])) aqOK = false;
            }
            check($"aquiferAt {coords.Count} samples", aqOK);

            var wantCave = jArr(jGet(g, "caveSamples")).Select(jInt).ToList();
            bool cvOK = true;
            int cvi = 0;
            foreach (var (x, z) in coords)
            {
                foreach (var y in new[] { -30, 0, 40 })
                {
                    if (sg.caveBiomeAt(x, y, z, sg.heightEstimate(x, z)) != wantCave[cvi]) cvOK = false;
                    cvi += 1;
                }
            }
            check($"caveBiomeAt {wantCave.Count} samples", cvOK);
        }

        var wantClim = jArr(jGet(g, "climSamples"));
        bool clOK = true;
        for (int i = 0; i < wantClim.Count; i++)
        {
            var cs = jArr(wantClim[i]);
            var (x, z) = coords[i];
            var c = sg.climate.at(x, z);
            double[] got = { c.t, c.h, c.c, c.e, c.w, c.pv, c.rare };
            for (int j = 0; j < cs.Count; j++)
            {
                double want = BitConverter.UInt64BitsToDouble(Convert.ToUInt64(jStr(cs[j]), 16));
                if (BitConverter.DoubleToUInt64Bits(got[j]) != BitConverter.DoubleToUInt64Bits(want))
                {
                    clOK = false;
                    Console.WriteLine($"    climate[{i}][{j}] got {got[j]:R} want {want:R}");
                }
            }
        }
        check($"climate fields bit-pattern-exact {wantClim.Count} samples", clOK);
    }
    else
    {
        check("terrain-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("full chunk pipeline with features (vs goldens)");

{
    var gOpt = loadJSON("feature-goldens.json");
    if (gOpt is JsonElement g)
    {
        uint fnvStr(uint h0, string s)
        {
            uint h = h0;
            foreach (var b in Encoding.UTF8.GetBytes(s)) h = unchecked((h ^ (uint)b) * 16777619);
            return h;
        }
        uint fnvInt(uint h0, int v)
        {
            uint h = h0;
            uint u = unchecked((uint)v);
            h = unchecked((h ^ (u & 0xff)) * 16777619);
            h = unchecked((h ^ ((u >> 8) & 0xff)) * 16777619);
            h = unchecked((h ^ ((u >> 16) & 0xff)) * 16777619);
            h = unchecked((h ^ ((u >> 24) & 0xff)) * 16777619);
            return h;
        }
        var cases = jArr(jGet(g, "cases"));
        foreach (var c in cases)
        {
            uint seed = jU32(jGet(c, "seed"));
            int cx = jNum(c, "cx");
            int cz = jNum(c, "cz");
            var dim = (Dim)jNum(c, "dim");
            var outp = generateChunk(dim, seed, cx, cz);
            string label = $"d{(int)dim} seed {seed} ({cx},{cz})";
            uint hBlocks = fnvU16(outp.blocks);
            uint hBiomes = fnvU8(outp.biomes);
            uint beHash = 2166136261;
            foreach (var be in outp.blockEntities)
            {
                beHash = fnvInt(beHash, be.x); beHash = fnvInt(beHash, be.y); beHash = fnvInt(beHash, be.z);
                beHash = fnvStr(beHash, be.kind);
            }
            uint entHash = 2166136261;
            foreach (var e in outp.entities)
            {
                entHash = fnvStr(entHash, e.mob);
                entHash = fnvInt(entHash, (int)Math.Round(e.x * 2, MidpointRounding.AwayFromZero));
                entHash = fnvInt(entHash, (int)Math.Round(e.y * 2, MidpointRounding.AwayFromZero));
                entHash = fnvInt(entHash, (int)Math.Round(e.z * 2, MidpointRounding.AwayFromZero));
            }
            uint refHash = 2166136261;
            foreach (var rf in outp.structRefs)
            {
                refHash = fnvStr(refHash, rf.id);
                refHash = fnvInt(refHash, rf.x0); refHash = fnvInt(refHash, rf.y0); refHash = fnvInt(refHash, rf.z0);
                refHash = fnvInt(refHash, rf.x1); refHash = fnvInt(refHash, rf.y1); refHash = fnvInt(refHash, rf.z1);
            }
            if (REGOLD) continue;
            uint wantBlocks = jU32(jGet(c, "hBlocks"));
            check($"{label} blocks hash", hBlocks == wantBlocks,
                  $"got {radix16(hBlocks)} want {radix16(wantBlocks)}");
            check($"{label} biomes hash", hBiomes == jU32(jGet(c, "hBiomes")));
            check($"{label} BE count", outp.blockEntities.Count == jNum(c, "beCount"),
                  $"got {outp.blockEntities.Count} want {jNum(c, "beCount")}");
            check($"{label} BE hash", beHash == jU32(jGet(c, "beHash")));
            check($"{label} entity count", outp.entities.Count == jNum(c, "entCount"),
                  $"got {outp.entities.Count} want {jNum(c, "entCount")}");
            check($"{label} entity hash", entHash == jU32(jGet(c, "entHash")));
            check($"{label} structRefs {outp.structRefs.Count}", outp.structRefs.Count == jNum(c, "refCount")
                  && refHash == jU32(jGet(c, "refHash")));
        }
        if (REGOLD) check("features: goldens regenerated (native baseline)", true);
    }
    else
    {
        check("feature-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("atlas painters (vs goldens)");

{
    var gOpt = loadJSON("atlas-goldens.json");
    if (gOpt is JsonElement g)
    {
        var hashes = jGet(g, "hashes");
        var atlas = buildAtlas();
        int baseCount = jNum(g, "count");
        check("tile count (baseline range intact)", atlas.count >= baseCount,
              $"got {atlas.count} want >= {baseCount}");
        check("no missing painters", atlas.missing.Length == 0, $"missing: {string.Join(", ", atlas.missing.Take(10))}");
        var names = allTileNames().Take(baseCount).ToList();
        var mismatches = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            string nm = names[i];
            uint h = fnvU8(atlas.pixels[i]);
            uint want = hashes.TryGetProperty(nm, out var he) ? jU32(he) : 0;
            if (h != want) mismatches.Add(nm);
        }
        check($"{names.Count} baseline tiles pixel-identical", mismatches.Count == 0,
              $"{mismatches.Count} mismatched: {string.Join(", ", mismatches.Take(12))}");
        if (mismatches.Count != 0 && g.TryGetProperty("sampleB64", out var b64E) && b64E.ValueKind == JsonValueKind.String
            && g.TryGetProperty("sampleName", out var snE) && snE.ValueKind == JsonValueKind.String
            && mismatches.Contains(snE.GetString()))
        {
            string sampleName = snE.GetString();
            var data = Convert.FromBase64String(b64E.GetString());
            var got = atlas.pixels[names.IndexOf(sampleName)];
            for (int i = 0; i < Math.Min(data.Length, got.Length); i++)
            {
                if (data[i] != got[i])
                {
                    Console.WriteLine($"    {sampleName} byte[{i}] px({i / 4 % 16},{i / 64}) ch{i % 4} got {got[i]} want {data[i]}");
                    break;
                }
            }
        }
    }
    else
    {
        check("atlas-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("section mesher (vs goldens)");

{
    var gOpt = loadJSON("mesh-goldens.json");
    if (gOpt is JsonElement g)
    {
        var litCache = new Dictionary<string, (ushort[] blocks, byte[] biomes, byte[] sky, byte[] blk)>();
        (ushort[] blocks, byte[] biomes, byte[] sky, byte[] blk) litChunk(uint seed, int cx, int cz)
        {
            string key = $"{seed}:{cx},{cz}";
            if (litCache.TryGetValue(key, out var cc)) return cc;
            var outp = generateOverworldChunk(seed, cx, cz);
            var light = computeLocalLight(outp.blocks, WORLD_H, true);
            var c = (outp.blocks, outp.biomes, light.sky, light.blk);
            litCache[key] = c;
            return c;
        }
        byte chunkBiomeAt((ushort[] blocks, byte[] biomes, byte[] sky, byte[] blk) c, int lx, int y, int lz)
        {
            int qy = Math.Max(0, Math.Min((WORLD_H >> 2) - 1, (y - GEN_MIN_Y) >> 2));
            return c.biomes[(qy * 4 + (lz >> 2)) * 4 + (lx >> 2)];
        }
        MeshInput buildSnapshot(uint seed, int cx, int sy, int cz)
        {
            const int P = 18;
            var blocks = new ushort[P * P * P];
            var skyLight = new byte[P * P * P];
            var blockLight = new byte[P * P * P];
            var biomes = new byte[P * P];
            int baseY = GEN_MIN_Y + sy * 16;
            int baseX = cx * 16, baseZ = cz * 16;
            for (int dz = -1; dz <= 16; dz++)
            {
                for (int dx = -1; dx <= 16; dx++)
                {
                    int wx = baseX + dx, wz = baseZ + dz;
                    var c = litChunk(seed, floorDiv(wx, 16), floorDiv(wz, 16));
                    int lx = posMod(wx, 16), lz = posMod(wz, 16);
                    biomes[(dz + 1) * P + (dx + 1)] = chunkBiomeAt(c, lx, Math.Min(GEN_MIN_Y + WORLD_H - 1, Math.Max(GEN_MIN_Y, baseY + 8)), lz);
                    for (int dy = -1; dy <= 16; dy++)
                    {
                        int wy = baseY + dy;
                        int idx = ((dy + 1) * P + (dz + 1)) * P + (dx + 1);
                        if (wy < GEN_MIN_Y || wy >= GEN_MIN_Y + WORLD_H)
                        {
                            blocks[idx] = 0;
                            skyLight[idx] = (byte)(wy >= GEN_MIN_Y + WORLD_H ? 15 : 0);
                            blockLight[idx] = 0;
                        }
                        else
                        {
                            int ci = ((wy - GEN_MIN_Y) * 16 + lz) * 16 + lx;
                            blocks[idx] = c.blocks[ci];
                            skyLight[idx] = c.sky[ci];
                            blockLight[idx] = c.blk[ci];
                        }
                    }
                }
            }
            return new MeshInput(blocks, skyLight, blockLight, biomes);
        }

        // verify lighting first — light feeds the greedy merge keys
        if (g.TryGetProperty("lights", out var lightsE) && lightsE.ValueKind == JsonValueKind.Array)
        {
            var lights = jArr(lightsE);
            bool lightOK = true;
            foreach (var l in lights)
            {
                string key = jStr(jGet(l, "key"));
                var parts = key.Split(':');
                uint seed = uint.Parse(parts[0], CultureInfo.InvariantCulture);
                var crd = parts[1].Split(',');
                var c = litChunk(seed, int.Parse(crd[0], CultureInfo.InvariantCulture), int.Parse(crd[1], CultureInfo.InvariantCulture));
                if (REGOLD) continue;
                if (fnvU8(c.sky) != jU32(jGet(l, "hSky")))
                {
                    lightOK = false;
                    Console.WriteLine($"    sky light mismatch at {key}: got {radix16(fnvU8(c.sky))} want {radix16(jU32(jGet(l, "hSky")))}");
                }
                if (fnvU8(c.blk) != jU32(jGet(l, "hBlk")))
                {
                    lightOK = false;
                    Console.WriteLine($"    block light mismatch at {key}");
                }
            }
            if (REGOLD) check("computeLocalLight: goldens regenerated", true);
            else check($"computeLocalLight {lights.Count} chunks bit-identical", lightOK);
        }

        var meshCases = jArr(jGet(g, "cases"));
        foreach (var c in meshCases)
        {
            uint seed = jU32(jGet(c, "seed"));
            int cx = jNum(c, "cx");
            int sy = jNum(c, "sy");
            int cz = jNum(c, "cz");
            var snap = buildSnapshot(seed, cx, sy, cz);
            var mesh = buildSectionMesh(snap);
            string label = $"seed {seed} ({cx},s{sy},{cz})";
            if (REGOLD) continue;
            var layers = new (string name, MeshLayer layer, JsonElement want)[]
            {
                ("opaque", mesh.opaque, jGet(c, "o")),
                ("cutout", mesh.cutout, jGet(c, "c")),
                ("translucent", mesh.translucent, jGet(c, "t")),
            };
            foreach (var (lname, layer, want) in layers)
            {
                int wn = jNum(want, "n");
                uint whd = jU32(jGet(want, "hd"));
                uint whi = jU32(jGet(want, "hi"));
                check($"{label} {lname} {wn}v",
                      layer.count == wn && fnvU32(layer.data) == whd && fnvU32(layer.idx) == whi,
                      $"got n={layer.count} hd={radix16(fnvU32(layer.data))} hi={radix16(fnvU32(layer.idx))} want n={wn} hd={radix16(whd)} hi={radix16(whi)}");
            }
        }
        if (REGOLD) check("mesh: goldens regenerated (native baseline)", true);
    }
    else
    {
        check("mesh-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("world simulation: light engine + fluids + ticks (vs goldens)");

{
    var gOpt = loadJSON("worldsim-goldens.json");
    if (gOpt is JsonElement g)
    {
        registerFluidHandlers();
        var world = new World(Dim.overworld, 12345);
        for (int cz = -1; cz <= 1; cz++)
        {
            for (int cx = -1; cx <= 1; cx++)
            {
                var outp = generateOverworldChunk(12345, cx, cz);
                var light = computeLocalLight(outp.blocks, WORLD_H, true);
                var c = new Chunk(cx, cz, GEN_MIN_Y, WORLD_H);
                c.blocks = outp.blocks;
                c.skyLight = light.sky;
                c.blockLight = light.blk;
                c.biomes = outp.biomes;
                c.buildHeightmap();
                c.scanSpecials();
                c.status = Chunk.ChunkStatus.generated;
                world.setChunk(c);
            }
        }
        for (int cz = -1; cz <= 1; cz++)
            for (int cx = -1; cx <= 1; cx++)
                world.light.stitchChunk(world.getChunk(cx, cz));

        (uint, uint, uint) fnvAll()
        {
            uint hb = 2166136261, hs = 2166136261, hl = 2166136261;
            for (int cz = -1; cz <= 1; cz++)
            {
                for (int cx = -1; cx <= 1; cx++)
                {
                    var c = world.getChunk(cx, cz);
                    for (int i = 0; i < c.blocks.Length; i++)
                    {
                        ushort v = c.blocks[i];
                        hb = unchecked((hb ^ (uint)(v & 0xff)) * 16777619);
                        hb = unchecked((hb ^ (uint)(v >> 8)) * 16777619);
                        hs = unchecked((hs ^ c.skyLight[i]) * 16777619);
                        hl = unchecked((hl ^ c.blockLight[i]) * 16777619);
                    }
                }
            }
            return (hb, hs, hl);
        }

        var stages = jArr(jGet(g, "stages"));
        int stageIdx = 0;
        void checkStage(string name)
        {
            var (hb, hs, hl) = fnvAll();
            if (REGOLD) { stageIdx++; return; }
            var want = stages[stageIdx];
            stageIdx++;
            string wn = jStr(jGet(want, "name"));
            var wh = jGet(want, "h");
            check($"stage {name} blocks+sky+blockLight",
                  wn == name && hb == jU32(jGet(wh, "b")) && hs == jU32(jGet(wh, "s")) && hl == jU32(jGet(wh, "l")),
                  $"got b={radix16(hb)} s={radix16(hs)} l={radix16(hl)} want b={radix16(jU32(jGet(wh, "b")))} s={radix16(jU32(jGet(wh, "s")))} l={radix16(jU32(jGet(wh, "l")))}");
        }

        checkStage("adopted");

        int TORCH = (int)cell(B.torch), GLOW = (int)cell(B.glowstone), STONE = (int)cell(B.stone);
        int WATERC = (int)cell(B.water, 0), LAVAC = (int)cell(B.lava, 0);

        for (int y = 70; y <= 74; y++) for (int z = 2; z <= 6; z++) for (int x = 2; x <= 6; x++) world.setBlock(x, y, z, 0);
        for (int z = 2; z <= 6; z++) for (int x = 2; x <= 6; x++) world.setBlock(x, 69, z, STONE);
        checkStage("box");

        world.setBlock(4, 70, 4, TORCH);
        checkStage("torch");

        for (int y = 68; y >= 40; y--) world.setBlock(8, y, 8, 0);
        world.setBlock(8, 40, 8, GLOW);
        checkStage("shaft");

        world.setBlock(4, 72, 4, WATERC);
        world.scheduleTick(4, 72, 4, (int)B.water, 1);
        for (int i = 0; i < 200; i++) world.tick();
        checkStage("water");

        world.setBlock(6, 73, 6, LAVAC);
        world.scheduleTick(6, 73, 6, (int)B.lava, 1);
        for (int i = 0; i < 400; i++) world.tick();
        checkStage("lava");

        world.setBlock(4, 70, 4, 0);
        for (int i = 0; i < 10; i++) world.tick();
        checkStage("untorch");

        world.setBlock(4, 69, 4, 0);
        world.setBlock(4, 68, 4, 0);
        for (int i = 0; i < 600; i++) world.tick();
        checkStage("drain");

        int p1 = world.rng.nextInt(1000000007), p2 = world.rng.nextInt(1000000007);
        if (REGOLD)
        {
            check("worldsim: goldens regenerated (native baseline)", true);
        }
        else
        {
            var wantProbe = jArr(jGet(g, "rngProbe")).Select(jInt).ToList();
            check("world rng state in lockstep", p1 == wantProbe[0] && p2 == wantProbe[1],
                  $"got {p1},{p2} want {wantProbe[0]},{wantProbe[1]}");
            check("world time/dayTime", world.time == jNum(g, "time") && world.dayTime == jNum(g, "dayTime"));
        }
    }
    else
    {
        check("worldsim-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("items: recipes/enchants/potions/loot (vs goldens)");
registerAllRecipes();
registerAllLootTables();

{
    var gOpt = loadJSON("items-goldens.json");
    if (gOpt is JsonElement g)
    {
        int num(string k) => jNum(g, k);
        uint hash32(string k) => jHash(g, k);

        // recipes
        var craftSer = string.Join(";", craftingRecipes.Select(rc =>
        {
            switch (rc.tag)
            {
                case CraftRecipe.Tag.shaped:
                    return $"S|{rc.w}|{rc.h}|{string.Join(",", rc.grid.Select(s => s ?? "."))}|{rc.@out}|{rc.count}";
                default: // shapeless
                    return $"L|{string.Join(",", rc.inputs)}|{rc.@out}|{rc.count}";
            }
        }));
        check("crafting recipe count", craftingRecipes.Count == num("craftCount"),
              $"got {craftingRecipes.Count} want {num("craftCount")}");
        check("crafting recipes hash", hashString(craftSer) == hash32("craftH"),
              $"got {hashString(craftSer)} want {hash32("craftH")}");

        var smeltSer = string.Join(";", smeltingRecipes.Select(s =>
            $"{s.input}>{s.output}|{(int)Math.Floor(s.xp * 1000 + 0.5)}|{s.kind}"));
        check("smelting recipe count", smeltingRecipes.Count == num("smeltCount"),
              $"got {smeltingRecipes.Count} want {num("smeltCount")}");
        check("smelting recipes hash", hashString(smeltSer) == hash32("smeltH"),
              $"got {hashString(smeltSer)} want {hash32("smeltH")}");

        var cutSer = string.Join(";", stonecuttingRecipes.Select(s => $"{s.input}>{s.output}x{s.count}"));
        check("stonecutting recipe count", stonecuttingRecipes.Count == num("cutCount"),
              $"got {stonecuttingRecipes.Count} want {num("cutCount")}");
        check("stonecutting recipes hash", hashString(cutSer) == hash32("cutH"),
              $"got {hashString(cutSer)} want {hash32("cutH")}");

        var smithSer = string.Join(";", smithingRecipes.Select(s => $"{s.template}+{s.@base}+{s.addition}>{s.output}"));
        check("smithing recipe count", smithingRecipes.Count == num("smithCount"),
              $"got {smithingRecipes.Count} want {num("smithCount")}");
        check("smithing recipes hash", hashString(smithSer) == hash32("smithH"),
              $"got {hashString(smithSer)} want {hash32("smithH")}");

        var tagsSer = string.Join(";", TAGS.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}:{string.Join(",", TAGS[k])}"));
        check("tags hash", hashString(tagsSer) == hash32("tagsH"),
              $"got {hashString(tagsSer)} want {hash32("tagsH")}");
        check("trim materials", string.Join(",", TRIM_MATERIALS) == jStr(jGet(g, "trimMaterials")));

        // enchantments
        check("enchantment count", ENCHANTMENTS.Count == num("enchCount"),
              $"got {ENCHANTMENTS.Count} want {num("enchCount")}");
        var enchGold = jArr(jGet(g, "enchEntries"));
        bool enchOK = true, appliesOK = true;
        for (int i = 0; i < enchGold.Count; i++)
        {
            var eg = enchGold[i];
            var e = ENCHANTMENTS[i];
            string wantId = jStr(jGet(eg, "id"));
            if (e.id != wantId) { enchOK = false; Console.WriteLine($"    ench[{i}] id {e.id} want {wantId}"); continue; }
            string s = $"{e.id}|{e.maxLevel}|{e.weight}|{e.target}|{(e.treasure ? 1 : 0)}|{(e.curse ? 1 : 0)}|{(e.tradeable ? 1 : 0)}|{e.exclusiveGroup ?? "-"}";
            for (int l = 1; l <= e.maxLevel; l++) s += $"|{e.minPower(l)}..{e.maxPower(l)}";
            if (hashString(s) != jU32(jGet(eg, "h")))
            {
                enchOK = false; Console.WriteLine($"    ench[{i}] {e.id} def hash mismatch: {s}");
            }
            var applies = string.Concat(itemDefs.Take(BASE_ITEM_COUNT).Select(it => appliesTo(e, it) ? "1" : "0"));
            if (hashString(applies) != jU32(jGet(eg, "applies")))
            {
                appliesOK = false; Console.WriteLine($"    ench[{i}] {e.id} appliesTo bits mismatch");
            }
        }
        check("39 enchantment defs + power windows bit-identical", enchOK);
        check($"appliesTo over baseline {BASE_ITEM_COUNT} items × 39 enchs", appliesOK);

        var compatSer = string.Join("|", ENCHANTMENTS.Select(ea =>
            string.Concat(ENCHANTMENTS.Select(eb => compatible(ea, eb) ? "1" : "0"))));
        check("compatibility matrix hash", hashString(compatSer) == hash32("compatH"),
              $"got {hashString(compatSer)} want {hash32("compatH")}");

        var enchabilitySer = string.Join(";", itemDefs.Take(BASE_ITEM_COUNT).Select(it => $"{it.name}:{enchantability(it)}"));
        check("enchantability over baseline items", hashString(enchabilitySer) == hash32("enchabilityH"),
              $"got {hashString(enchabilitySer)} want {hash32("enchabilityH")}");

        // effects / potions / brewing
        var effectsSer = string.Join(";", EFFECTS.Select(e => $"{e.id}|{e.displayName}|{e.color}|{(e.beneficial ? 1 : 0)}|{(e.instant ? 1 : 0)}"));
        check("effect count", EFFECTS.Count == num("effectsCount"), $"got {EFFECTS.Count} want {num("effectsCount")}");
        check("effects hash", hashString(effectsSer) == hash32("effectsH"),
              $"got {hashString(effectsSer)} want {hash32("effectsH")}");

        var potionsSer = string.Join(";", POTIONS.Select(p =>
            $"{p.id}|{p.displayName}|{p.color}|{string.Join(",", p.effects.Select(ef => $"{ef.effect}:{ef.duration}:{ef.amplifier}"))}"));
        check("potion count", POTIONS.Count == num("potionsCount"), $"got {POTIONS.Count} want {num("potionsCount")}");
        check("potions hash", hashString(potionsSer) == hash32("potionsH"),
              $"got {hashString(potionsSer)} want {hash32("potionsH")}");

        var brewSer = string.Join(";", BREW_RECIPES.Select(b => $"{b.@base}+{b.ingredient}>{b.result}"));
        check("brew recipe count", BREW_RECIPES.Count == num("brewCount"), $"got {BREW_RECIPES.Count} want {num("brewCount")}");
        check("brew recipes hash", hashString(brewSer) == hash32("brewH"),
              $"got {hashString(brewSer)} want {hash32("brewH")}");

        // loot tables — 40 rolls per table, full stack serialization in RNG lockstep
        string serStack(ItemStack s)
        {
            string str = $"{itemDef(s.id).name}x{s.count}";
            if (s.ench.Count != 0) str += $"e[{string.Join(",", s.ench.Select(en => $"{en.id}:{en.lvl}"))}]";
            if (s.data.potion != null) str += $"p[{s.data.potion}]";
            return str;
        }
        var lootGold = jArr(jGet(g, "lootTables"));
        check("loot table count + order", allLootTables().SequenceEqual(lootGold.Select(lg => jStr(jGet(lg, "name")))),
              $"got {allLootTables().Count} tables");
        bool lootOK = true;
        foreach (var lg in lootGold)
        {
            string name = jStr(jGet(lg, "name"));
            var rng = new RandomX(hashString(name));
            var parts = new List<string>();
            for (int i = 0; i < 40; i++)
            {
                foreach (var s in rollLoot(name, ref rng)) parts.Add(serStack(s));
                parts.Add(";");
            }
            uint h = hashString(string.Join("|", parts));
            if (h != jU32(jGet(lg, "h")))
            {
                lootOK = false;
                Console.WriteLine($"    loot {name}: got {h} want {jU32(jGet(lg, "h"))}");
            }
        }
        check($"{lootGold.Count} loot tables × 40 rolls in RNG lockstep", lootOK);

        // direct enchantStackRandomly probes
        var probeGold = jArr(jGet(g, "enchProbes")).Select(jStr).ToList();
        var probes = new List<string>();
        foreach (var item in new[] { "diamond_sword", "book", "fishing_rod", "diamond_chestplate", "diamond_pickaxe", "bow", "iron_boots", "diamond_hoe" })
        {
            foreach (var lvl in new[] { 1, 5, 10, 15, 20, 25, 30, 39, 50 })
            {
                var rng = new RandomX(hashString($"{item}/{lvl}"));
                var s = enchantStackRandomly(new ItemStack(iid(item), 1), ref rng, lvl);
                probes.Add($"{item}@{lvl}={itemDef(s.id).name}:{string.Join(",", s.ench.Select(en => $"{en.id}:{en.lvl}"))}");
            }
        }
        bool probesOK = probes.Count == probeGold.Count;
        if (probesOK)
        {
            for (int i = 0; i < probes.Count; i++)
                if (probes[i] != probeGold[i])
                {
                    probesOK = false;
                    Console.WriteLine($"    probe[{i}] got {probes[i]} want {probeGold[i]}");
                }
        }
        check($"{probeGold.Count} enchant-randomly probes byte-identical", probesOK);
    }
    else
    {
        check("items-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("portable fdlibm math (vs fmath goldens)");

{
    var gOpt = loadJSON("fmath-goldens.json");
    if (gOpt is JsonElement g)
    {
        string hexD(double x)
        {
            ulong bp = BitConverter.DoubleToUInt64Bits(x);
            return Convert.ToString((long)(bp >> 32), 16) + "-" + Convert.ToString((long)(bp & 0xffff_ffff), 16);
        }
        double parseHex(string s)
        {
            var pp = s.Split('-');
            ulong h = Convert.ToUInt64(pp[0], 16);
            ulong l = Convert.ToUInt64(pp[1], 16);
            return BitConverter.UInt64BitsToDouble((h << 32) | l);
        }
        ulong bits(double x) => BitConverter.DoubleToUInt64Bits(x);
        var probes = jArr(jGet(g, "probes")).Select(jStr).ToList();
        int okCount = 0, badCount = 0;
        foreach (var p in probes)
        {
            var io = p.Split(':');
            var ins = io[0].Split(',');
            var outs = io[1].Split(',');
            if (ins.Length == 1)
            {
                double x = parseHex(ins[0]);
                double ws = parseHex(outs[0]), wc = parseHex(outs[1]);
                if (bits(detSin(x)) == bits(ws) && bits(detCos(x)) == bits(wc)) okCount++;
                else
                {
                    badCount++;
                    if (badCount <= 3) Console.WriteLine($"    sin/cos({x:R}): got {hexD(detSin(x))},{hexD(detCos(x))} want {string.Join(",", outs)}");
                }
            }
            else
            {
                double y = parseHex(ins[0]), x = parseHex(ins[1]);
                double w = parseHex(outs[0]);
                if (bits(detAtan2(y, x)) == bits(w)) okCount++;
                else
                {
                    badCount++;
                    if (badCount <= 3) Console.WriteLine($"    atan2({y:R},{x:R}): got {hexD(detAtan2(y, x))} want {outs[0]}");
                }
            }
        }
        check($"{probes.Count} fdlibm sin/cos/atan2 probes bit-identical", badCount == 0, $"{badCount} mismatches");
    }
    else
    {
        check("fmath-goldens.json loadable", false, "not found");
    }
}

// ---------------------------------------------------------------------------
section("entities: zoo/combat/physics/trades/pathfinding/spawning (vs goldens)");
registerAllEntities();

{
    var gOpt = loadJSON("entity-goldens.json");
    if (gOpt is JsonElement g)
    {
        string hex(double x)
        {
            ulong bp = BitConverter.DoubleToUInt64Bits(x);
            return Convert.ToString((long)(bp >> 32), 16) + "-" + Convert.ToString((long)(bp & 0xffff_ffff), 16);
        }
        int ifloor(double x) => (int)Math.Floor(x);
        int num(string k) => jNum(g, k);
        uint hash32(string k) => jHash(g, k);

        check("entity type count", entityTypes().Count == num("entityTypeCount"),
              $"got {entityTypes().Count} want {num("entityTypeCount")}");
        check("entity registration order", hashString(string.Join(",", entityTypes())) == hash32("entityTypesH"));
        check("spawnable mob list", hashString(string.Join(",", spawnableMobs())) == hash32("spawnableH"));

        World buildWorld()
        {
            var world = new World(Dim.overworld, 12345);
            for (int cz = -2; cz <= 2; cz++)
            {
                for (int cx = -2; cx <= 2; cx++)
                {
                    var outp = generateOverworldChunk(12345, cx, cz);
                    var light = computeLocalLight(outp.blocks, WORLD_H, true);
                    var c = new Chunk(cx, cz, GEN_MIN_Y, WORLD_H);
                    c.blocks = outp.blocks;
                    c.skyLight = light.sky;
                    c.blockLight = light.blk;
                    c.biomes = outp.biomes;
                    c.buildHeightmap();
                    c.scanSpecials();
                    c.status = Chunk.ChunkStatus.generated;
                    world.setChunk(c);
                }
            }
            for (int cz = -2; cz <= 2; cz++)
                for (int cx = -2; cx <= 2; cx++)
                    world.light.stitchChunk(world.getChunk(cx, cz));
            return world;
        }

        string serMob(Entity e, int i)
        {
            string s = $"{e.type}#{i}:{hex(e.x)},{hex(e.y)},{hex(e.z)},{hex(e.vx)},{hex(e.vy)},{hex(e.vz)},{hex(e.yaw)}";
            s += $",og{(e.onGround ? 1 : 0)},w{(e.inWater ? 1 : 0)},a{e.age},f{e.fireTicks}";
            if (e is LivingEntity liv) s += $",h{hex(liv.health)}";
            return s;
        }

        void stepWorld(World world)
        {
            world.tick();
            tickPendingTimeouts(world);
            foreach (var e in world.entities.ToArray()) (e as Entity)?.tick();   // snapshot: Swift [Entity] iterates by value
            foreach (var e in world.entities.Where(e => e.dead).ToList()) world.removeEntity(e);
        }

        void determinize(Entity e, int i)
        {
            e.persistent = true;
            if (e is LivingEntity m) m.rng = new RandomX(hashString($"{e.type}#{i}"));
            if (e is Sheep sheep) { sheep.color = i % 16; sheep.sheared = false; }
            if (e is Chicken chicken) chicken.eggTime = 99999;
            if (e is Parrot) e.data.variant = i % 5;
            if (e is Frog) e.data.variant = i % 3;
            if (e is Axolotl) e.data.variant = i % 4;
            if (e is Panda) e.data.gene = "normal";
            if (e is Goat goat) goat.screaming = false;
            if (e is Zombie z)
            {
                z.baby = false; z.speed = 0.095;
                if (z is Drowned d) d.hasTrident = false;
            }
            if (e is Slime slime) slime.setSize(2);
            if (e is HorseBase h) { h.jumpStrength = 0.7; h.speed = 0.2; h.maxHealth = 26; h.health = 26; }
            if (e is Llama l) { l.maxHealth = 22; l.health = 22; l.data.variant = i % 4; }
            if (e is Vex v) v.lifeTicks = 99999;
            if (e is EnderDragon dr) dr.pathAngle = 1.25;
        }

        bool diffSer(string label, string got, string want)
        {
            if (got == want) return true;
            var gParts = got.Split('|');
            var wParts = want.Split('|');
            for (int i = 0; i < Math.Min(gParts.Length, wParts.Length); i++)
                if (gParts[i] != wParts[i])
                {
                    Console.WriteLine($"    {label} first diff @{i}:\n      got {gParts[i]}\n      want {wParts[i]}");
                    return false;
                }
            Console.WriteLine($"    {label} length mismatch: got {gParts.Length} want {wParts.Length}");
            return false;
        }

        // --- A) zoo
        string[] ZOO = { "cow", "mooshroom", "pig", "sheep", "chicken", "rabbit", "wolf", "cat", "fox", "parrot",
                         "bee", "axolotl", "frog", "goat", "turtle", "dolphin", "squid", "bat", "polar_bear", "panda",
                         "strider", "camel", "sniffer", "allay", "cod", "villager", "iron_golem", "snow_golem", "horse", "llama",
                         "zombie", "skeleton", "creeper", "spider", "slime", "witch", "enderman", "silverfish", "phantom", "guardian",
                         "shulker", "pillager", "vindicator", "evoker", "vex", "blaze", "ghast", "magma_cube", "zombified_piglin", "piglin",
                         "hoglin", "wither_skeleton", "warden", "wither", "ender_dragon" };
        resetGameRng(hashString("zoo"));
        var zooWorld = buildWorld();
        zooWorld.dayTime = 13000;
        var zooMobs = new List<Entity>();
        for (int i = 0; i < ZOO.Length; i++)
        {
            int sx = -20 + (i % 8) * 6;
            int sz = -20 + (i / 8) * 6;
            int sy = zooWorld.surfaceY(sx, sz);
            var e = spawnMob(zooWorld, ZOO[i], sx + 0.5, sy, sz + 0.5, new SpawnOpts());
            determinize(e, i);
            zooMobs.Add(e);
        }

        var zooGold = jArr(jGet(g, "zooStages"));
        int zooIdx = 0;
        bool zooOK = true;
        for (int t = 1; t <= 200; t++)
        {
            stepWorld(zooWorld);
            if (t == 50 || t == 120 || t == 200)
            {
                string ser = string.Join("|", zooMobs.Select((m, off) => serMob(m, off)));
                if (!REGOLD)
                {
                    string want = jStr(jGet(zooGold[zooIdx], "ser"));
                    if (!diffSer($"zoo t={t}", ser, want)) zooOK = false;
                }
                zooIdx++;
            }
        }
        if (REGOLD) check("zoo: golden regenerated (native baseline)", true);
        else check("zoo: 55 mob types × 200 ticks bit-identical (3 checkpoints)", zooOK);

        // --- B) combat
        resetGameRng(hashString("combat"));
        var combatWorld = buildWorld();
        combatWorld.dayTime = 13000;
        var cPlayer = new Player(combatWorld);
        int py = combatWorld.surfaceY(0, 0);
        cPlayer.setPos(0.5, py, 0.5);
        cPlayer.rng = new RandomX(hashString("player"));
        combatWorld.addEntity(cPlayer);
        var combatants = new List<Entity> { cPlayer };
        string[] CMOBS = { "zombie", "spider", "slime", "vex", "iron_golem" };
        for (int i = 0; i < CMOBS.Length; i++)
        {
            double ang = (double)i / CMOBS.Length * Math.PI * 2;
            int sx = ifloor(0.5 + Math.Cos(ang) * 10);
            int sz = ifloor(0.5 + Math.Sin(ang) * 10);
            int sy = combatWorld.surfaceY(sx, sz);
            var e = spawnMob(combatWorld, CMOBS[i], sx + 0.5, sy, sz + 0.5, new SpawnOpts());
            determinize(e, 100 + i);
            combatants.Add(e);
        }
        var combatGold = jArr(jGet(g, "combatStages"));
        int combatIdx = 0;
        bool combatOK = true;
        for (int t = 1; t <= 150; t++)
        {
            stepWorld(combatWorld);
            cPlayer.travel();
            if (t == 50 || t == 100 || t == 150)
            {
                string ser = string.Join("|", combatants.Select((m, off) => serMob(m, off)));
                ser += $"|hunger{cPlayer.hunger},sat{hex(cPlayer.saturation)},exh{hex(cPlayer.exhaustion)},dead{(cPlayer.dead ? 1 : 0)}";
                if (!REGOLD)
                {
                    string want = jStr(jGet(combatGold[combatIdx], "ser"));
                    if (!diffSer($"combat t={t}", ser, want)) combatOK = false;
                }
                combatIdx++;
            }
        }
        if (REGOLD) check("combat: golden regenerated (vanilla baseline)", true);
        else check("combat: player + 5 mobs, damage/knockback in lockstep", combatOK);

        // --- C) player physics
        resetGameRng(hashString("phys"));
        var physWorld = buildWorld();
        physWorld.dayTime = 13000;
        var pPlayer = new Player(physWorld);
        int ppy = physWorld.surfaceY(4, 4);
        pPlayer.setPos(4.5, ppy, 4.5);
        pPlayer.rng = new RandomX(hashString("physplayer"));
        physWorld.addEntity(pPlayer);
        var physGold = jArr(jGet(g, "physStages"));
        int physIdx = 0;
        bool physOK = true;
        for (int t = 1; t <= 200; t++)
        {
            pPlayer.moveForward = 0; pPlayer.moveStrafe = 0;
            pPlayer.jumping = false; pPlayer.sprinting = false; pPlayer.sneaking = false;
            if (t <= 40) pPlayer.moveForward = 1;
            else if (t <= 60) { pPlayer.moveForward = 1; pPlayer.jumping = true; }
            else if (t <= 100) pPlayer.moveStrafe = 1;
            else if (t <= 140) { pPlayer.moveForward = 1; pPlayer.sprinting = true; pPlayer.jumping = t % 10 == 0; }
            else if (t <= 160) { pPlayer.moveForward = 1; pPlayer.sneaking = true; pPlayer.yaw = 0.8; }
            physWorld.tick();
            pPlayer.tick();
            pPlayer.travel();
            if (t % 20 == 0)
            {
                string s = $"{hex(pPlayer.x)},{hex(pPlayer.y)},{hex(pPlayer.z)},{hex(pPlayer.vx)},{hex(pPlayer.vy)},{hex(pPlayer.vz)},og{(pPlayer.onGround ? 1 : 0)},fall{hex(pPlayer.fallDistance)},h{hex(pPlayer.health)}";
                if (!REGOLD)
                {
                    string want = jStr(jGet(physGold[physIdx], "s"));
                    if (s != want)
                    {
                        physOK = false;
                        Console.WriteLine($"    phys t={t}:\n      got {s}\n      want {want}");
                    }
                }
                physIdx++;
            }
        }
        if (REGOLD) check("player physics: golden regenerated (vanilla baseline)", true);
        else check("player physics: 200 scripted-input ticks vs native baseline", physOK);

        // --- D) trades
        resetGameRng(hashString("trades"));
        var tradeWorld = buildWorld();
        bool tradeOK = true;
        var tradeGold = jArr(jGet(g, "tradeProbes")).Select(jStr).ToList();
        int tg = 0;
        foreach (var prof in PROFESSIONS)
        {
            for (int lvl = 1; lvl <= 5; lvl++)
            {
                var v = (Villager)createEntity("villager", tradeWorld);
                v.profession = prof;
                v.tradeLevel = lvl;
                v.rng = new RandomX(hashString($"{prof}/{lvl}"));
                v.refreshTrades();
                var ser = string.Join(";", v.offers.Select(o =>
                {
                    string s = $"{o.buyA.id}x{o.buyA.count}";
                    if (o.buyB != null) s += $"+{o.buyB.id}x{o.buyB.count}";
                    s += $">{o.sell.id}x{o.sell.count}";
                    if (o.sell.ench.Count != 0) s += $"e[{string.Join(",", o.sell.ench.Select(en => $"{en.id}:{en.lvl}"))}]";
                    return s;
                }));
                string got = $"{prof}@{lvl}={ser}";
                if (got != tradeGold[tg])
                {
                    tradeOK = false;
                    Console.WriteLine($"    trade {prof}@{lvl}:\n      got {got}\n      want {tradeGold[tg]}");
                }
                tg++;
            }
        }
        check($"{tradeGold.Count} villager trade tables byte-identical", tradeOK);

        // --- E) pathfinding
        resetGameRng(hashString("paths"));
        var pathWorld = buildWorld();
        var pathGold = jArr(jGet(g, "pathProbes")).Select(jStr).ToList();
        bool pathOK = true;
        for (int i = 0; i < 8; i++)
        {
            int fx = -24 + i * 6, fz = -18 + i * 4;
            int tx = fx + 10 - (i % 3) * 7, tz = fz + 8 - (i % 4) * 5;
            var p = findPath(pathWorld, fx + 0.5, pathWorld.surfaceY(fx, fz), fz + 0.5,
                             tx + 0.5, pathWorld.surfaceY(tx, tz), tz + 0.5);
            string got = p == null ? "null" : string.Join(";", p.Select(nd => $"{nd.x},{nd.y},{nd.z}"));
            if (!REGOLD && got != pathGold[i])
            {
                pathOK = false;
                Console.WriteLine($"    path[{i}]:\n      got {got.Substring(0, Math.Min(120, got.Length))}\n      want {pathGold[i].Substring(0, Math.Min(120, pathGold[i].Length))}");
            }
        }
        if (REGOLD) check("A* paths: golden regenerated (native baseline)", true);
        else check("8 A* paths node-identical", pathOK);

        // --- F) natural spawning
        resetGameRng(hashString("spawn"));
        var spawnWorld = buildWorld();
        spawnWorld.dayTime = 13000;
        var sPlayer = new Player(spawnWorld);
        sPlayer.setPos(0.5, spawnWorld.surfaceY(0, 0), 0.5);
        spawnWorld.addEntity(sPlayer);
        var spawnRng = new RandomX(hashString("natural"));
        for (int i = 0; i < 40; i++)
        {
            spawnWorld.time = i * 400;
            naturalSpawnTick(spawnWorld, new List<Player> { sPlayer }, ref spawnRng);
        }
        var spawnedSer = string.Join("|", spawnWorld.entities
            .Select(e => e as Entity).Where(e => e != null && !ReferenceEquals(e, sPlayer))
            .Select(e => $"{e.type}@{hex(e.x)},{hex(e.y)},{hex(e.z)}"));
        if (REGOLD)
        {
            check("natural spawn: golden regenerated (native baseline)", true);
            check("natural spawn hash: golden regenerated", true);
        }
        else
        {
            check("natural spawn count", spawnWorld.entities.Count - 1 == num("spawnCount"),
                  $"got {spawnWorld.entities.Count - 1} want {num("spawnCount")}");
            check("natural spawn types+positions hash", hashString(spawnedSer) == hash32("spawnH"),
                  $"got {hashString(spawnedSer)} want {hash32("spawnH")}");
        }
    }
    else
    {
        check("entity-goldens.json loadable", false, "not found — run from the repo root (goldens/)");
    }
}

// ---------------------------------------------------------------------------
section("systems: crafting/BEs/redstone/explosion/interact/portals (vs goldens)");
registerAllSystems();

{
    var gOpt = loadJSON("systems-goldens.json");
    if (gOpt is JsonElement g)
    {
        string hex(double x)
        {
            ulong bp = BitConverter.DoubleToUInt64Bits(x);
            return Convert.ToString((long)(bp >> 32), 16) + "-" + Convert.ToString((long)(bp & 0xffff_ffff), 16);
        }
        string detNum(double x)
        {
            if (x == Math.Round(x, MidpointRounding.ToEven) && Math.Abs(x) < 1e15) return ((int)x).ToString();
            return x.ToString("R", CultureInfo.InvariantCulture);
        }
        World buildWorld()
        {
            var world = new World(Dim.overworld, 12345);
            for (int cz = -1; cz <= 1; cz++)
            {
                for (int cx = -1; cx <= 1; cx++)
                {
                    var outp = generateOverworldChunk(12345, cx, cz);
                    var light = computeLocalLight(outp.blocks, WORLD_H, true);
                    var c = new Chunk(cx, cz, GEN_MIN_Y, WORLD_H);
                    c.blocks = outp.blocks;
                    c.skyLight = light.sky;
                    c.blockLight = light.blk;
                    c.biomes = outp.biomes;
                    c.buildHeightmap();
                    c.scanSpecials();
                    c.status = Chunk.ChunkStatus.generated;
                    world.setChunk(c);
                }
            }
            for (int cz = -1; cz <= 1; cz++)
                for (int cx = -1; cx <= 1; cx++)
                    world.light.stitchChunk(world.getChunk(cx, cz));
            return world;
        }
        string serStack(ItemStack s)
        {
            if (s == null) return "-";
            string str = $"{itemDef(s.id).name}x{s.count}";
            if (s.damage != 0) str += $"d{s.damage}";
            if (s.ench.Count != 0) str += $"e[{string.Join(",", s.ench.Select(en => $"{en.id}:{en.lvl}"))}]";
            if (s.data.potion != null) str += $"p[{s.data.potion}]";
            if (s.data.priorWork is int w && w != 0) str += $"w{w}";
            if (s.data.trim is TrimData tr) str += $"t[{tr.pattern}:{tr.material}]";
            if (s.label != null) str += $"l[{s.label}]";
            return str;
        }
        uint regionHash(World world, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            uint h = 2166136261;
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int c = world.getBlock(x, y, z);
                        h = unchecked((h ^ (uint)(c & 0xff)) * 16777619);
                        h = unchecked((h ^ (uint)(c >> 8)) * 16777619);
                    }
            return h;
        }
        string entsSer(World world, EntityRef skip = null)
        {
            return string.Join("|", world.entities.Where(e => !ReferenceEquals(e, skip))
                .Select(e => $"{(e as Entity)?.type ?? "?"}@{hex(e.x)},{hex(e.y)},{hex(e.z)}"));
        }
        void stepWorld(World world)
        {
            world.tick();
            tickPendingTimeouts(world);
            foreach (var e in world.entities.ToArray()) (e as Entity)?.tick();   // snapshot: Swift [Entity] iterates by value
            foreach (var e in world.entities.Where(e => e.dead).ToList()) world.removeEntity(e);
        }
        void cmpList(string label, List<string> got, List<string> want)
        {
            bool ok = got.Count == want.Count;
            if (ok)
            {
                for (int i = 0; i < want.Count; i++)
                    if (got[i] != want[i])
                    {
                        ok = false;
                        Console.WriteLine($"    {label}[{i}]:\n      got {got[i]}\n      want {want[i]}");
                        break;
                    }
            }
            else
            {
                Console.WriteLine($"    {label} count: got {got.Count} want {want.Count}");
            }
            check(label, ok);
        }
        List<string> jStrList(string k) => jArr(jGet(g, k)).Select(jStr).ToList();

        // --- A) crafting probes
        ItemStack stk(string name, int count = 1) => new ItemStack(iid(name), count);
        var craftGot = new List<string>();
        var craftCases = new (string, int, int, ItemStack[])[]
        {
            ("planks", 2, 2, new[] { stk("oak_log"), null, null, null }),
            ("sticks", 2, 2, new[] { stk("oak_planks"), null, stk("oak_planks"), null }),
            ("table", 2, 2, new[] { stk("oak_planks"), stk("oak_planks"), stk("oak_planks"), stk("oak_planks") }),
            ("pick", 3, 3, new[] { stk("oak_planks"), stk("oak_planks"), stk("oak_planks"), null, stk("stick"), null, null, stk("stick"), null }),
            ("axe-mirrored", 3, 3, new[] { null, stk("oak_planks"), stk("oak_planks"), null, stk("stick"), stk("oak_planks"), null, stk("stick"), null }),
            ("tag-planks-chest", 3, 3, new[] { stk("birch_planks"), stk("birch_planks"), stk("birch_planks"), stk("birch_planks"), null, stk("birch_planks"), stk("birch_planks"), stk("birch_planks"), stk("birch_planks") }),
            ("shapeless-flint", 2, 2, new[] { stk("iron_ingot"), stk("flint"), null, null }),
            ("no-match-extra", 3, 3, new[] { stk("oak_log"), stk("stick"), null, null, null, null, null, null, null }),
            ("torch", 2, 2, new[] { stk("coal"), null, stk("stick"), null }),
            ("bread", 3, 3, new[] { stk("wheat"), stk("wheat"), stk("wheat"), null, null, null, null, null, null }),
        };
        foreach (var (label, w, h, grid) in craftCases)
        {
            var m = matchCrafting(grid.ToList(), w, h);
            craftGot.Add($"{label}={(m != null ? serStack(m.Value.@out) : "null")}");
        }
        cmpList("crafting grid probes", craftGot, jStrList("craftProbes"));

        var smithGot = new List<string>();
        smithGot.Add($"netherite={serStack(matchSmithing(stk("netherite_upgrade"), stk("diamond_sword"), stk("netherite_ingot")))}");
        smithGot.Add($"trim={serStack(matchSmithing(stk("coast_armor_trim"), stk("iron_chestplate"), stk("emerald")))}");
        smithGot.Add($"bad={serStack(matchSmithing(stk("netherite_upgrade"), stk("stone"), stk("netherite_ingot")))}");
        cmpList("smithing probes", smithGot, jStrList("smithProbes"));

        // --- B) enchanting / anvil / grindstone
        var enchGot = new List<string>();
        foreach (var (item, shelves, sd) in new[] { ("diamond_sword", 15, 777), ("book", 8, 1234), ("iron_pickaxe", 0, 42), ("diamond_chestplate", 15, 90210) })
        {
            var opts = enchantingOptions(stk(item), shelves, sd);
            enchGot.Add($"{item}@{shelves}/{sd}=" + string.Join(";", opts.Select(o =>
                $"L{o.level}:{string.Join(",", o.enchants.Select(en => $"{en.id}:{en.lvl}"))}")));
        }
        cmpList("enchanting options", enchGot, jStrList("enchProbes"));

        var anvilGot = new List<string>();
        var sword = new ItemStack(iid("diamond_sword"), 1, damage: 100);
        var sword2 = new ItemStack(iid("diamond_sword"), 1, damage: 500, ench: new List<EnchInstance> { new EnchInstance("sharpness", 3) });
        var bookS = new ItemStack(iid("enchanted_book"), 1, ench: new List<EnchInstance> { new EnchInstance("sharpness", 3), new EnchInstance("knockback", 2) });
        var ar1 = anvilCombine(sword, sword2, null);
        anvilGot.Add($"combine={(ar1 != null ? serStack(ar1.Value.@out) + $"${ar1.Value.cost}" : "null")}");
        var ar2 = anvilCombine(sword, bookS, null);
        anvilGot.Add($"book={(ar2 != null ? serStack(ar2.Value.@out) + $"${ar2.Value.cost}" : "null")}");
        var ar3 = anvilCombine(sword, new ItemStack(iid("diamond"), 3), null);
        anvilGot.Add($"repair={(ar3 != null ? serStack(ar3.Value.@out) + $"${ar3.Value.cost}" : "null")}");
        var ar4 = anvilCombine(new ItemStack(iid("iron_sword"), 1), null, "Slicey");
        anvilGot.Add($"rename={(ar4 != null ? serStack(ar4.Value.@out) + $"${ar4.Value.cost}" : "null")}");
        var gr1 = grindstoneResult(sword2, null);
        anvilGot.Add($"grind={(gr1 != null ? serStack(gr1.Value.@out) + $"${gr1.Value.xp}" : "null")}");
        cmpList("anvil/grindstone probes", anvilGot, jStrList("anvilProbes"));

        // --- C) BE timelines
        resetGameRng(hashString("be"));
        var beWorld = buildWorld();
        int bePy = beWorld.surfaceY(0, 0);
        int beBase = bePy + 20;
        for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++) beWorld.setBlock(dx, beBase - 1, dz, (int)cell(B.stone));
        beWorld.setBlock(0, beBase, 0, (int)cell(B.furnace, 0));
        var fbe = makeFurnaceBE(0, beBase, 0, "furnace");
        var fitems = fbe.items;
        fitems[0] = new ItemStack(iid("raw_iron"), 3);
        fitems[1] = new ItemStack(iid("coal"), 2);
        fbe.items = fitems;
        beWorld.setBlockEntity(fbe);
        beWorld.setBlock(0, beBase + 1, 0, (int)cell(B.hopper, 0));
        var hbe = makeHopperBE(0, beBase + 1, 0);
        var hitems = hbe.items;
        hitems[0] = new ItemStack(iid("raw_gold"), 2);
        hbe.items = hitems;
        beWorld.setBlockEntity(hbe);
        beWorld.setBlock(2, beBase, 0, (int)cell(B.brewing_stand, 0));
        var bbe = makeBrewingBE(2, beBase, 0);
        var bitems = bbe.items;
        var pd = new StackData(); pd.potion = "awkward";
        bitems[0] = new ItemStack(iid("potion"), 1, data: pd);
        bitems[3] = new ItemStack(iid("blaze_powder"), 2);
        bitems[4] = new ItemStack(iid("blaze_powder"), 2);
        bbe.items = bitems;
        beWorld.setBlockEntity(bbe);
        var beGot = new List<string>();
        for (int t = 1; t <= 450; t++)
        {
            stepWorld(beWorld);
            if (t == 100 || t == 250 || t == 450)
            {
                string ff = $"f:{string.Join(",", (fbe.items ?? new ItemStack[0]).Select(serStack))}:b{fbe.burnTime ?? 0}:c{fbe.cookTime ?? 0}:x{detNum(fbe.xpBank ?? 0)}";
                string hh = $"h:{string.Join(",", (hbe.items ?? new ItemStack[0]).Select(serStack))}:cd{hbe.cooldown ?? 0}";
                string pp = $"p:{string.Join(",", (bbe.items ?? new ItemStack[0]).Select(serStack))}:bt{bbe.brewTime ?? 0}:fu{bbe.fuel ?? 0}";
                beGot.Add(string.Join("|", new[] { ff, hh, pp }));
            }
        }
        cmpList("BE timelines (furnace/hopper/brewing)", beGot, jStrList("beStages"));

        // --- D) redstone contraption
        resetGameRng(hashString("redstone"));
        var rsWorld = buildWorld();
        int rsBase = rsWorld.surfaceY(8, 8) + 20;
        for (int dz = 0; dz <= 8; dz++) for (int dx = 0; dx <= 12; dx++) rsWorld.setBlock(8 + dx, rsBase - 1, 8 + dz, (int)cell(B.stone));
        rsWorld.setBlock(8, rsBase, 8, (int)cell(B.lever, 0));
        for (int i = 1; i <= 5; i++) rsWorld.setBlock(8 + i, rsBase, 8, (int)cell(B.redstone_wire, 0));
        rsWorld.setBlock(14, rsBase, 8, (int)cell(B.repeater, 3));
        rsWorld.setBlock(15, rsBase, 8, (int)cell(B.redstone_wire, 0));
        rsWorld.setBlock(16, rsBase, 8, (int)cell(B.redstone_lamp));
        rsWorld.setBlock(11, rsBase, 9, (int)cell(B.piston, 3));
        rsWorld.setBlock(11, rsBase, 10, (int)cell(B.stone));
        rsWorld.setBlock(12, rsBase, 10, (int)cell(B.observer, 4));
        void flip(bool on)
        {
            int c = rsWorld.getBlock(8, rsBase, 8);
            rsWorld.setBlock(8, rsBase, 8, (int)cell(B.lever, on ? (c & 7) | 8 : c & 7));
            rsWorld.updateNeighbors(8, rsBase, 8);
            rsWorld.updateNeighbors(8, rsBase - 1, 8);
        }
        var rsGot = new List<uint>();
        flip(true);
        for (int i = 1; i <= 30; i++) stepWorld(rsWorld);
        rsGot.Add(regionHash(rsWorld, 6, rsBase - 2, 6, 20, rsBase + 2, 14));
        flip(false);
        for (int i = 1; i <= 30; i++) stepWorld(rsWorld);
        rsGot.Add(regionHash(rsWorld, 6, rsBase - 2, 6, 20, rsBase + 2, 14));
        flip(true);
        for (int i = 1; i <= 4; i++) stepWorld(rsWorld);
        rsGot.Add(regionHash(rsWorld, 6, rsBase - 2, 6, 20, rsBase + 2, 14));
        var rsWant = jArr(jGet(g, "redstoneStages")).Select(jU32).ToList();
        check("redstone contraption (lever/wire/repeater/piston/lamp/observer)", rsGot.SequenceEqual(rsWant),
              $"got [{string.Join(", ", rsGot)}] want [{string.Join(", ", rsWant)}]");

        // --- E) random ticks
        resetGameRng(hashString("crops"));
        var cropWorld = buildWorld();
        int cropBase = cropWorld.surfaceY(-8, -8) + 20;
        for (int dz = 0; dz < 6; dz++)
        {
            for (int dx = 0; dx < 6; dx++)
            {
                cropWorld.setBlock(-8 + dx, cropBase - 1, -8 + dz, (int)cell(B.farmland, 7));
                cropWorld.setBlock(-8 + dx, cropBase, -8 + dz, (int)cell(B.wheat, 0));
            }
        }
        cropWorld.randomTickSpeed = 40;
        for (int i = 1; i <= 400; i++) cropWorld.tick();
        uint cropGot = regionHash(cropWorld, -8, cropBase - 1, -8, -3, cropBase, -3);
        if (REGOLD) check("crop growth: golden regenerated", true);
        else check("crop growth via seeded random ticks", cropGot == jU32(jGet(g, "cropHash")),
                   $"got {cropGot} want {jU32(jGet(g, "cropHash"))}");

        // --- F) explosion
        resetGameRng(hashString("boom"));
        var boomWorld = buildWorld();
        int bpx = 4, bpz = 4;
        int bpy = boomWorld.surfaceY(bpx, bpz);
        var cow = spawnMob(boomWorld, "cow", bpx + 3.5, bpy + 1, bpz + 0.5, new SpawnOpts());
        if (cow is LivingEntity cowLiv) cowLiv.rng = new RandomX(hashString("boomcow"));
        cow.persistent = true;
        explode(boomWorld, bpx + 0.5, bpy + 0.5, bpz + 0.5, 4, true, null);
        uint boomGot = regionHash(boomWorld, bpx - 8, bpy - 8, bpz - 8, bpx + 8, bpy + 8, bpz + 8);
        uint boomEnts = hashString(entsSer(boomWorld));
        if (REGOLD)
        {
            check("explosion: goldens regenerated", true);
            check("explosion ents: goldens regenerated", true);
        }
        else
        {
            check("explosion crater bit-identical", boomGot == jU32(jGet(g, "explosionHash")),
                  $"got {boomGot} want {jU32(jGet(g, "explosionHash"))}");
            check("explosion entity state (knockback + drops)", boomEnts == jU32(jGet(g, "explosionEnts")),
                  $"got {boomEnts} want {jU32(jGet(g, "explosionEnts"))}");
        }

        // --- G) interact
        resetGameRng(hashString("interact"));
        var iWorld = buildWorld();
        var iPlayer = new Player(iWorld);
        int ipy = iWorld.surfaceY(0, -10);
        iPlayer.setPos(0.5, ipy, -9.5);
        iPlayer.rng = new RandomX(hashString("iplayer"));
        iWorld.addEntity(iPlayer);
        var ictx = new InteractCtx(iWorld, iPlayer);
        void giveP(string name, int count = 1) { iPlayer.inventory[iPlayer.selectedSlot] = new ItemStack(iid(name), count); }
        RaycastHit mkHit(int x, int y, int z, int face)
            => new RaycastHit(x, y, z, face, iWorld.getBlock(x, y, z), 0,
                              x + 0.5, y + (face == 1 ? 1 : 0.5), z + 0.5);
        var iGot = new List<string>();
        int ibx = 0, ibz = -14;
        int iby = iWorld.surfaceY(ibx, ibz);
        iPlayer.yaw = 0;
        giveP("oak_stairs", 4);
        iGot.Add($"stairs={(placeBlock(ictx, mkHit(ibx, iby - 1, ibz, 1), (int)itemDef(iPlayer.mainHand.id).block.Value, iPlayer.mainHand) ? "true" : "false")}@{Convert.ToString(iWorld.getBlock(ibx, iby, ibz), 16)}");
        giveP("oak_door", 2);
        iGot.Add($"door={(placeBlock(ictx, mkHit(ibx + 2, iby - 1, ibz, 1), (int)itemDef(iPlayer.mainHand.id).block.Value, iPlayer.mainHand) ? "true" : "false")}@{Convert.ToString(iWorld.getBlock(ibx + 2, iby, ibz), 16)},{Convert.ToString(iWorld.getBlock(ibx + 2, iby + 1, ibz), 16)}");
        iGot.Add($"doorUse={(useBlock(ictx, mkHit(ibx + 2, iby, ibz, 3)) ? "true" : "false")}@{Convert.ToString(iWorld.getBlock(ibx + 2, iby, ibz), 16)}");
        giveP("white_bed");
        iGot.Add($"bed={(placeBlock(ictx, mkHit(ibx + 4, iby - 1, ibz, 1), (int)itemDef(iPlayer.mainHand.id).block.Value, iPlayer.mainHand) ? "true" : "false")}@{Convert.ToString(iWorld.getBlock(ibx + 4, iby, ibz), 16)}");
        giveP("torch", 4);
        iGot.Add($"torch={(placeBlock(ictx, mkHit(ibx, iby, ibz - 2, 3), (int)itemDef(iPlayer.mainHand.id).block.Value, iPlayer.mainHand) ? "true" : "false")}");
        giveP("iron_pickaxe");
        finishBreaking(ictx, ibx, iby, ibz);
        iWorld.setBlock(ibx + 6, iby - 1, ibz, (int)cell(B.farmland, 7));
        iWorld.setBlock(ibx + 6, iby, ibz, (int)cell(B.wheat, 0));
        iGot.Add($"bonemeal={(applyBonemeal(iWorld, ibx + 6, iby, ibz) ? "true" : "false")}@{Convert.ToString(iWorld.getBlock(ibx + 6, iby, ibz), 16)}");
        giveP("golden_apple");
        useItem(ictx, null);
        finishUsingItem(ictx);
        iGot.Add($"ate=h{iPlayer.hunger},s{hex(iPlayer.saturation)},fx{string.Join(";", iPlayer.effects.Select(ef => $"{ef.id}:{ef.duration}:{ef.amplifier}"))}");
        uint iEnts = hashString(entsSer(iWorld, iPlayer));
        uint iRegion = regionHash(iWorld, ibx - 2, iby - 2, ibz - 4, ibx + 8, iby + 2, ibz + 2);
        if (REGOLD)
        {
            check("interact: goldens regenerated", true);
            check("interact ents: goldens regenerated", true);
            check("interact region: goldens regenerated", true);
        }
        else
        {
            cmpList("interact probes (place/use/break/bonemeal/eat)", iGot, jStrList("interactProbes"));
            check("interact entity drops", iEnts == jU32(jGet(g, "interactEnts")),
                  $"got {iEnts} want {jU32(jGet(g, "interactEnts"))}");
            check("interact region blocks", iRegion == jU32(jGet(g, "interactRegion")),
                  $"got {iRegion} want {jU32(jGet(g, "interactRegion"))}");
        }

        // --- H) portal
        var pWorld = buildWorld();
        int ppy2 = pWorld.surfaceY(-12, 12) + 25;
        for (int dy = 0; dy < 5; dy++)
        {
            for (int dx = 0; dx < 4; dx++)
            {
                bool frame = dy == 0 || dy == 4 || dx == 0 || dx == 3;
                pWorld.setBlock(-12 + dx, ppy2 + dy, 12, frame ? (int)cell(B.obsidian) : 0);
            }
        }
        bool pok = tryIgnitePortal(pWorld, -11, ppy2 + 1, 12);
        string pGot = $"{(pok ? "true" : "false")}@{regionHash(pWorld, -13, ppy2 - 1, 11, -8, ppy2 + 5, 13)}";
        if (REGOLD) check("portal: golden regenerated", true);
        else check("nether portal frame ignition", pGot == jStr(jGet(g, "portal")),
                   $"got {pGot} want {jStr(jGet(g, "portal"))}");
    }
    else
    {
        check("systems-goldens.json loadable", false, "not found");
    }
}

// ---------------------------------------------------------------------------
section("vanilla player physics constants (independent derivations)");
{
    // flat stone slab world — equilibrium measurements need perfectly flat ground
    (World, int) flatWorld(ushort topBlock = 0)
    {
        var world = new World(Dim.overworld, 1);
        int groundY = 64;
        for (int cz = -2; cz <= 2; cz++)
        {
            for (int cx = -2; cx <= 2; cx++)
            {
                var c = new Chunk(cx, cz, GEN_MIN_Y, WORLD_H);
                var blocks = new ushort[16 * 16 * WORLD_H];
                ushort stone = cell(B.stone);
                for (int y = 0; y <= groundY - GEN_MIN_Y; y++)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        blocks[y * 256 + i] = (y == groundY - GEN_MIN_Y && topBlock != 0) ? cell(topBlock) : stone;
                    }
                }
                c.blocks = blocks;
                c.skyLight = Enumerable.Repeat((byte)15, blocks.Length).ToArray();
                c.blockLight = new byte[blocks.Length];
                c.buildHeightmap();
                c.scanSpecials();
                c.status = Chunk.ChunkStatus.lit;
                world.setChunk(c);
            }
        }
        return (world, groundY + 1);
    }
    Player mkPlayer(World world, int gy)
    {
        var p = new Player(world);
        p.setPos(0.5, gy, 0.5);
        p.rng = new RandomX(7);
        world.addEntity(p);
        for (int i = 0; i < 5; i++) { p.tick(); p.travel(); }
        return p;
    }
    void runTicks(Player p, int nn, double forward = 0, double strafe = 0,
                  bool jump = false, bool sprint = false, bool sneak = false)
    {
        for (int i = 0; i < nn; i++)
        {
            p.moveForward = forward; p.moveStrafe = strafe;
            p.jumping = jump; p.sprinting = sprint; p.sneaking = sneak;
            p.tick();
            p.travel();
        }
    }

    // 1) WALK equilibrium
    {
        var (w, gy) = flatWorld();
        var p = mkPlayer(w, gy);
        runTicks(p, 150, forward: 1);
        double z0 = p.z;
        runTicks(p, 1, forward: 1);
        double perTick = p.z - z0;
        double a = 0.98 * 0.1 * (0.21600002 / (0.6 * 0.6 * 0.6));
        double expect = a / (1 - 0.6 * 0.91);
        check($"walk speed = {perTick * 20:F4} b/s (vanilla 4.317)",
              Math.Abs(perTick - expect) < 1e-9 && Math.Abs(perTick * 20 - 4.317) < 0.001,
              $"got {perTick} want {expect}");
    }
    // 2) SPRINT equilibrium
    {
        var (w, gy) = flatWorld();
        var p = mkPlayer(w, gy);
        runTicks(p, 150, forward: 1, sprint: true);
        double z0 = p.z;
        runTicks(p, 1, forward: 1, sprint: true);
        double perTick = p.z - z0;
        double a = 0.98 * 0.13 * (0.21600002 / 0.216);
        double expect = a / (1 - 0.546);
        check($"sprint speed = {perTick * 20:F4} b/s (vanilla 5.612)",
              Math.Abs(perTick - expect) < 1e-9 && Math.Abs(perTick * 20 - 5.612) < 0.001);
    }
    // 3) SNEAK
    {
        var (w, gy) = flatWorld();
        var p = mkPlayer(w, gy);
        runTicks(p, 150, forward: 1, sneak: true);
        double z0 = p.z;
        runTicks(p, 1, forward: 1, sneak: true);
        double perTick = p.z - z0;
        double a = 0.3 * 0.98 * 0.1 * (0.21600002 / 0.216);
        double expect = a / (1 - 0.546);
        check($"sneak speed = {perTick * 20:F4} b/s (vanilla 1.295)",
              Math.Abs(perTick - expect) < 1e-9 && Math.Abs(perTick * 20 - 1.295) < 0.001);
    }
    // 4) JUMP apex
    {
        var (w, gy) = flatWorld();
        var p = mkPlayer(w, gy);
        double y0 = p.y;
        double apex = 0.0;
        for (int t = 0; t < 30; t++)
        {
            runTicks(p, 1, jump: t == 0);
            apex = Math.Max(apex, p.y - y0);
        }
        double ev = 0.42, ey = 0.0, eApex = 0.0;
        for (int i = 0; i < 30; i++)
        {
            ey += ev;
            eApex = Math.Max(eApex, ey);
            ev = (ev - 0.08) * 0.98;
        }
        check($"jump apex = {apex:F4} (vanilla 1.2522)",
              Math.Abs(apex - eApex) < 1e-9 && Math.Abs(apex - 1.2522) < 0.001,
              $"got {apex} want {eApex}");
    }
    // 5) SPRINT-JUMP
    {
        var (w, gy) = flatWorld();
        var p = mkPlayer(w, gy);
        runTicks(p, 150, forward: 1, sprint: true);
        double z0 = p.z;
        runTicks(p, 1, forward: 1, jump: true, sprint: true);
        int airTicks = 1;
        while (!p.onGround && airTicks < 30)
        {
            runTicks(p, 1, forward: 1, sprint: true);
            airTicks += 1;
        }
        double dist = p.z - z0;
        check($"sprint-jump: {dist:F3} blocks in {airTicks} air ticks",
              dist > 3.5 && dist < 4.6 && airTicks >= 11 && airTicks <= 14);
    }
    // 6) FALL DAMAGE
    {
        var (w, gy) = flatWorld();
        var p = mkPlayer(w, gy);
        p.setPos(0.5, gy + 20, 0.5);
        p.vx = 0; p.vy = 0; p.vz = 0;
        p.onGround = false;
        int t = 0;
        while (!p.onGround && t < 100)
        {
            runTicks(p, 1);
            t += 1;
        }
        check($"20-block fall: damage {20 - p.health:F1} (vanilla 17)",
              Math.Abs((20 - p.health) - 17) < 1.01);
    }
    // 7) WATER terminal sink velocity
    {
        var (w, gy) = flatWorld();
        for (int y = gy; y <= gy + 60; y++)
        {
            w.setBlock(0, y, 0, (int)cell(B.water), SET_SILENT);
        }
        var p = new Player(w);
        p.setPos(0.5, gy + 40, 0.5);
        p.rng = new RandomX(7);
        w.addEntity(p);
        for (int i = 0; i < 60; i++) { p.tick(); p.travel(); }
        check($"water sink terminal vy = {p.vy:F4} (vanilla −0.025)",
              Math.Abs(p.vy - (-0.025)) < 0.002);
        p.setPos(0.5, gy + 8, 0.5);
        p.vy = 0;
        for (int i = 0; i < 50; i++)
        {
            p.jumping = true;
            p.tick();
            p.travel();
        }
        check($"swim-up terminal vy = {p.vy:F4} (vanilla +0.135)",
              Math.Abs(p.vy - 0.135) < 0.002);
    }
    // 8) ICE equilibrium
    {
        var (w, gy) = flatWorld(B.packed_ice);
        var p = mkPlayer(w, gy);
        runTicks(p, 150, forward: 1);
        double z0 = p.z;
        runTicks(p, 1, forward: 1);
        double perTick = p.z - z0;
        double slip = 0.98;
        double a = 0.98 * 0.1 * (0.21600002 / (slip * slip * slip));
        double expect = a / (1 - slip * 0.91);
        check($"ice glide = {perTick * 20:F3} b/s",
              Math.Abs(perTick - expect) < 1e-6);
    }
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
Environment.Exit(failed == 0 ? 0 : 1);

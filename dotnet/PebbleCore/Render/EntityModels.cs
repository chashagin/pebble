// Entity box models (box-UV) + dormant procedural skin painters — geometry
// is pinned by the golden baselines.
// Model space: 1 unit = 1/16 block, Y up, origin at feet center.
//
// Ported from Sources/PebbleCore/Render/EntityModels.swift.
// Defines static class: EntityModelsGlobals
//   (module free funcs/vars: shadeColor, MODELS, getModel, hasModel,
//    ensureModels, buildEntityGeometry + private helpers).
// Surface via: global using static PebbleCore.EntityModelsGlobals;
//
// External symbols (defined elsewhere, referenced by name as in Swift):
//   detRound (Gen/Terrain globals), hashString/hash2 (Rng), registerAllModels
//   (EntityModels2), EntitySkin's deps. EntitySkin is defined here.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PebbleCore;

public struct ModelBox
{
    public readonly double x, y, z;
    public readonly double w, h, d;
    public readonly double u, v;
    public readonly double grow;

    public ModelBox(double x, double y, double z, double w, double h, double d, double u, double v, double grow = 0)
    {
        this.x = x; this.y = y; this.z = z;
        this.w = w; this.h = h; this.d = d;
        this.u = u; this.v = v;
        this.grow = grow;
    }
}

public struct ModelPart
{
    public readonly string name;
    public readonly (double, double, double) pivot;
    public readonly (double, double, double) rot;   // baked rotation, radians (XYZ order)
    public readonly ModelBox[] boxes;

    public ModelPart(string name, (double, double, double) pivot,
                     (double, double, double) rot, ModelBox[] boxes)
    {
        this.name = name;
        this.pivot = pivot;
        this.rot = rot;
        this.boxes = boxes;
    }

    public ModelPart(string name, (double, double, double) pivot, ModelBox[] boxes)
        : this(name, pivot, (0, 0, 0), boxes) { }
}

public struct MobModel
{
    public readonly int texW;
    public readonly int texH;
    public readonly ModelPart[] parts;
    public readonly string anim;
    public readonly double scale;
    public readonly Action<EntitySkin> paint;
    /// vanilla entity texture path(s) this model's UV layout matches —
    /// base texture + optional overlays composited in order (eyes etc.)
    public readonly string[] packTex;
    /// true = packTex entries stack VERTICALLY into one sheet (sheep + fur)
    /// instead of compositing over each other
    public readonly bool packTexStack;
    /// per-layer RGB multipliers for packTex — vanilla ships some entity art
    /// grayscale and tints it at render time (tropical fish, sheep wool);
    /// empty = no tinting
    public readonly int[] packTexTints;

    public MobModel(int texW, int texH, ModelPart[] parts, string anim, double scale,
                    Action<EntitySkin> paint, string[] packTex = null, bool packTexStack = false,
                    int[] packTexTints = null)
    {
        this.texW = texW;
        this.texH = texH;
        this.parts = parts;
        this.anim = anim;
        this.scale = scale;
        this.paint = paint;
        this.packTex = packTex ?? new string[] { };
        this.packTexStack = packTexStack;
        this.packTexTints = packTexTints ?? new int[] { };
    }
}

/// raw-pixel skin painter (the original renderer design painted a canvas)
public class EntitySkin
{
    public readonly int w, h;
    public byte[] data;
    public readonly uint seed;

    public EntitySkin(int w, int h, string name)
    {
        this.w = w;
        this.h = h;
        data = new byte[w * h * 4];
        seed = hashString(name);
    }

    public double rand(int x, int y, uint salt = 0)
    {
        return (double)hash2(seed, x, y, salt) / 4294967296.0;
    }

    private void put(int x, int y, int r, int g, int b)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int i = (y * w + x) * 4;
        data[i] = (byte)Math.Min(255, Math.Max(0, r));
        data[i + 1] = (byte)Math.Min(255, Math.Max(0, g));
        data[i + 2] = (byte)Math.Min(255, Math.Max(0, b));
        data[i + 3] = 255;
    }

    public void fill(int u, int v, int fw, int fh, int c, double noise = 0.08)
    {
        for (int y = 0; y < fh; y++)
        {
            for (int x = 0; x < fw; x++)
            {
                double f = 1 - noise + rand(u + x, v + y) * noise * 2;
                put(u + x, v + y, (int)((double)((c >> 16) & 255) * f), (int)((double)((c >> 8) & 255) * f), (int)((double)(c & 255) * f));
            }
        }
    }

    public void px(int u, int v, int c)
    {
        put(u, v, (c >> 16) & 255, (c >> 8) & 255, c & 255);
    }

    public void rect(int u, int v, int rw, int rh, int c)
    {
        for (int y = 0; y < rh; y++) { for (int x = 0; x < rw; x++) { px(u + x, v + y, c); } }
    }

    /// fill the whole box-UV unwrap of a box at (u,v) sized w,h,d
    public void box(int u, int v, int bw, int bh, int bd, int c, double noise = 0.08)
    {
        fill(u + bd, v, bw, bd, c, noise);                       // top
        fill(u + bd + bw, v, bw, bd, shadeColor(c, 0.8), noise); // bottom
        fill(u, v + bd, bd, bh, shadeColor(c, 0.85), noise);     // right
        fill(u + bd, v + bd, bw, bh, c, noise);                  // front
        fill(u + bd + bw, v + bd, bd, bh, shadeColor(c, 0.85), noise); // left
        fill(u + bd + bw + bd, v + bd, bw, bh, shadeColor(c, 0.92), noise); // back
    }

    private void putA(int x, int y, int r, int g, int b, int a)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int i = (y * w + x) * 4;
        data[i] = (byte)Math.Min(255, Math.Max(0, r));
        data[i + 1] = (byte)Math.Min(255, Math.Max(0, g));
        data[i + 2] = (byte)Math.Min(255, Math.Max(0, b));
        data[i + 3] = (byte)Math.Min(255, Math.Max(0, a));
    }

    public void fillA(int u, int v, int fw, int fh, int c, int a, double noise = 0.08)
    {
        for (int y = 0; y < fh; y++)
        {
            for (int x = 0; x < fw; x++)
            {
                double f = 1 - noise + rand(u + x, v + y) * noise * 2;
                putA(u + x, v + y, (int)((double)((c >> 16) & 255) * f), (int)((double)((c >> 8) & 255) * f), (int)((double)(c & 255) * f), a);
            }
        }
    }

    /// box unwrap fill with uniform alpha (translucent shells like slime gel)
    public void boxA(int u, int v, int bw, int bh, int bd, int c, int a, double noise = 0.08)
    {
        fillA(u + bd, v, bw, bd, c, a, noise);
        fillA(u + bd + bw, v, bw, bd, shadeColor(c, 0.8), a, noise);
        fillA(u, v + bd, bd, bh, shadeColor(c, 0.85), a, noise);
        fillA(u + bd, v + bd, bw, bh, c, a, noise);
        fillA(u + bd + bw, v + bd, bd, bh, shadeColor(c, 0.85), a, noise);
        fillA(u + bd + bw + bd, v + bd, bw, bh, shadeColor(c, 0.92), a, noise);
    }

    public void eyes(int u, int v, int d, int fx, int fy, int gap, int ew, int eh, int white, int pupil)
    {
        int fu = u + d, fv = v + d;
        rect(fu + fx, fv + fy, ew, eh, white);
        rect(fu + fx + ew - 1, fv + fy, 1, eh, pupil);
        rect(fu + fx + gap, fv + fy, ew, eh, white);
        rect(fu + fx + gap, fv + fy, 1, eh, pupil);
    }
}

// ---------------------------------------------------------------------------
// geometry: 9 floats per vertex (pos3, normal3, uv2, partIdx)
// ---------------------------------------------------------------------------
public struct EntityGeometry
{
    public readonly float[] verts;
    public readonly int vertexCount;
    public readonly string[] partNames;
    public readonly MobModel model;
    public readonly EntitySkin skin;

    public EntityGeometry(float[] verts, int vertexCount, string[] partNames, MobModel model, EntitySkin skin)
    {
        this.verts = verts;
        this.vertexCount = vertexCount;
        this.partNames = partNames;
        this.model = model;
        this.skin = skin;
    }
}

public static class EntityModelsGlobals
{
    public static int shadeColor(int c, double f)
    {
        int r = Math.Min(255, (int)detRound((double)((c >> 16) & 255) * f));
        int g = Math.Min(255, (int)detRound((double)((c >> 8) & 255) * f));
        int b = Math.Min(255, (int)detRound((double)(c & 255) * f));
        return (r << 16) | (g << 8) | b;
    }

    public static Dictionary<string, MobModel> MODELS = new Dictionary<string, MobModel>();

    public static MobModel getModel(string name)
    {
        ensureModels();
        return MODELS.TryGetValue(name, out var m) ? m : MODELS["pig"];
    }

    public static bool hasModel(string name)
    {
        ensureModels();
        return MODELS.ContainsKey(name);
    }

    private static void M(string name, MobModel m) { MODELS[name] = m; }

    private static ModelPart part(string name, (double, double, double) pivot, params ModelBox[] boxes)
    {
        return new ModelPart(name: name, pivot: pivot, boxes: boxes);
    }

    private static ModelPart rpart(string name, (double, double, double) pivot, (double, double, double) rot, params ModelBox[] boxes)
    {
        return new ModelPart(name: name, pivot: pivot, rot: rot, boxes: boxes);
    }

    private static ModelBox box(double x, double y, double z, double w, double h, double d, double u, double v, double grow = 0)
    {
        return new ModelBox(x, y, z, w, h, d, u, v, grow);
    }

    private static MobModel quadModel(
        Action<EntitySkin> paint,
        double bodyW = 10, double bodyH = 8, double bodyL = 16,
        double legH = 6, double headS = 8, double scale = 1, string anim = "quad",
        int texW = 64, int texH = 64)
    {
        double bw = bodyW, bh = bodyH, bl = bodyL, lh = legH, hs = headS;
        return new MobModel(
            texW: texW, texH: texH,
            parts: new ModelPart[] {
                part("head", (0, lh + bh - 1, -bl / 2), box(-hs / 2, -hs / 2 + 1, -hs, hs, hs, hs, 0, 0)),
                part("body", (0, lh, 0), box(-bw / 2, 0, -bl / 2, bw, bh, bl, 28, 8)),
                part("legFR", (-bw / 2 + 2, lh, -bl / 2 + 2), box(-2, -lh, -2, 4, lh, 4, 0, 16)),
                part("legFL", (bw / 2 - 2, lh, -bl / 2 + 2), box(-2, -lh, -2, 4, lh, 4, 0, 16)),
                part("legBR", (-bw / 2 + 2, lh, bl / 2 - 2), box(-2, -lh, -2, 4, lh, 4, 0, 16)),
                part("legBL", (bw / 2 - 2, lh, bl / 2 - 2), box(-2, -lh, -2, 4, lh, 4, 0, 16)),
            },
            anim: anim, scale: scale, paint: paint
        );
    }

    private static MobModel cowModel(int c, string[] packTex)
    {
        return new MobModel(
            texW: 64, texH: 32,
            parts: new ModelPart[] {
                part("head", (0, 20, -8), box(-4, -4, -6, 8, 8, 6, 0, 0),
                     box(-5, 2, -4, 1, 3, 1, 22, 0), box(4, 2, -4, 1, 3, 1, 22, 0)),
                rpart("body", (0, 19, 2), (-Math.PI / 2, 0, 0),
                      box(-6, -8, -7, 12, 18, 10, 18, 4), box(-2, -8, -8, 4, 6, 1, 52, 0)),
                part("legFR", (-4, 12, -6), box(-2, -12, -2, 4, 12, 4, 0, 16)),
                part("legFL", (4, 12, -6), box(-2, -12, -2, 4, 12, 4, 0, 16)),
                part("legBR", (-4, 12, 7), box(-2, -12, -2, 4, 12, 4, 0, 16)),
                part("legBL", (4, 12, 7), box(-2, -12, -2, 4, 12, 4, 0, 16)),
            },
            anim: "quad", scale: 1,
            paint: s => {
                s.box(0, 0, 8, 8, 6, c, 0.12);
                s.eyes(0, 0, 6, 1, 3, 5, 2, 1, 0xffffff, 0x1c1c1c);
                s.box(22, 0, 1, 3, 1, 0xc8c0b0, 0.06);               // horns
                s.box(18, 4, 12, 18, 10, c, 0.12);                   // body
                s.box(52, 0, 4, 6, 1, 0xe8d8d0, 0.08);               // udder
                s.box(0, 16, 4, 12, 4, c, 0.12);                     // legs
                s.fill(4, 29, 4, 3, 0xe8e0d8, 0.1);                  // hooves
            },
            packTex: packTex);
    }

    private static bool modelsRegistered = false;
    public static void ensureModels()
    {
        if (modelsRegistered) return;
        modelsRegistered = true;

        M("pig", new MobModel(
            texW: 64, texH: 32,
            parts: new ModelPart[] {
                part("head", (0, 12, -6), box(-4, -4, -8, 8, 8, 8, 0, 0), box(-2, -3, -9, 4, 3, 1, 16, 16)),
                rpart("body", (0, 13, 2), (-Math.PI / 2, 0, 0), box(-5, -6, -7, 10, 16, 8, 28, 8)),
                part("legFR", (-3, 6, -5), box(-2, -6, -2, 4, 6, 4, 0, 16)),
                part("legFL", (3, 6, -5), box(-2, -6, -2, 4, 6, 4, 0, 16)),
                part("legBR", (-3, 6, 7), box(-2, -6, -2, 4, 6, 4, 0, 16)),
                part("legBL", (3, 6, 7), box(-2, -6, -2, 4, 6, 4, 0, 16)),
            },
            anim: "quad", scale: 1,
            paint: s => {
                s.box(0, 0, 8, 8, 8, 0xeea4a4, 0.12);
                s.eyes(0, 0, 8, 1, 4, 5, 2, 1, 0xffffff, 0x1c1c1c);
                s.box(16, 16, 4, 3, 1, 0xd88a8a, 0.1);   // snout
                s.box(28, 8, 10, 16, 8, 0xeea4a4, 0.12);
                s.box(0, 16, 4, 6, 4, 0xeea4a4, 0.12);
            },
            packTex: new string[] { "entity/pig/pig.png" }));

        M("cow", cowModel(0x443626, packTex: new string[] { "entity/cow/cow.png" }));

        M("sheep", new MobModel(
            texW: 64, texH: 64,
            parts: new ModelPart[] {
                part("head", (0, 18, -8), box(-3, -2, -6, 6, 6, 8, 0, 0), box(-3, -2, -6, 6, 6, 8, 0, 32, 0.6)),
                rpart("body", (0, 19, 2), (-Math.PI / 2, 0, 0),
                      box(-4, -6, -7, 8, 16, 6, 28, 8), box(-4, -6, -7, 8, 16, 6, 28, 40, 1.75)),
                part("legFR", (-3, 12, -5), box(-2, -12, -2, 4, 12, 4, 0, 16), box(-2, -6, -2, 4, 6, 4, 0, 48, 0.5)),
                part("legFL", (3, 12, -5), box(-2, -12, -2, 4, 12, 4, 0, 16), box(-2, -6, -2, 4, 6, 4, 0, 48, 0.5)),
                part("legBR", (-3, 12, 7), box(-2, -12, -2, 4, 12, 4, 0, 16), box(-2, -6, -2, 4, 6, 4, 0, 48, 0.5)),
                part("legBL", (3, 12, 7), box(-2, -12, -2, 4, 12, 4, 0, 16), box(-2, -6, -2, 4, 6, 4, 0, 48, 0.5)),
            },
            anim: "quad", scale: 1,
            paint: s => {
                int skinC = 0xb89a8a, wool = 0xe8e8e8;
                s.box(0, 0, 6, 6, 8, skinC, 0.1);
                s.rect(8 + 1, 8 + 2, 1, 2, 0x1c1c1c); s.rect(8 + 4, 8 + 2, 1, 2, 0x1c1c1c); // eyes
                s.box(28, 8, 8, 16, 6, skinC, 0.1);
                s.box(0, 16, 4, 12, 4, skinC, 0.1);
                s.box(0, 32, 6, 6, 8, wool, 0.05);
                s.box(28, 40, 8, 16, 6, wool, 0.05);
                s.box(0, 48, 4, 6, 4, wool, 0.05);
            },
            packTex: new string[] { "entity/sheep/sheep.png", "entity/sheep/sheep_fur.png" }, packTexStack: true));
        // dyed sheep: same rig, wool sheet (layer 2 of the stack) tinted per dye.
        // modelNameFor routes sheep with data.color N to "sheep_N"
        var sheepBase = MODELS["sheep"];
        int[] DYE_RGB = new int[] { 0xF9FFFE, 0xF9801D, 0xC74EBD, 0x3AB3DA, 0xFED83D, 0x80C71F, 0xF38BAA, 0x474F52,
                              0x9D9D97, 0x169C9C, 0x8932B8, 0x3C44AA, 0x835432, 0x5E7C16, 0xB02E26, 0x1D1D21 };
        for (int i = 0; i < DYE_RGB.Length; i++)
        {
            if (!(i > 0)) continue;
            int dye = DYE_RGB[i];
            M($"sheep_{i}", new MobModel(texW: sheepBase.texW, texH: sheepBase.texH, parts: sheepBase.parts,
                                     anim: sheepBase.anim, scale: 1, paint: sheepBase.paint,
                                     packTex: sheepBase.packTex, packTexStack: true,
                                     packTexTints: new int[] { 0xFFFFFF, dye }));
        }

        M("chicken", new MobModel(
            texW: 64, texH: 32,
            parts: new ModelPart[] {
                part("head", (0, 9, -4), box(-2, 0, -2, 4, 6, 3, 0, 0), box(-2, 2, -4, 4, 2, 2, 14, 0), box(-1, 0, -3, 2, 2, 2, 14, 4)),
                rpart("body", (0, 8, 0), (-Math.PI / 2, 0, 0), box(-3, -4, -3, 6, 8, 6, 0, 9)),
                part("wingR", (-3, 11, 0), box(-1, -4, -3, 1, 4, 6, 24, 13)),
                part("wingL", (3, 11, 0), box(0, -4, -3, 1, 4, 6, 24, 13)),
                part("legR", (-2, 5, 1), box(-1, -5, -3, 3, 5, 3, 26, 0)),
                part("legL", (2, 5, 1), box(-2, -5, -3, 3, 5, 3, 26, 0)),
            },
            anim: "chicken", scale: 1,
            paint: s => {
                s.box(0, 0, 4, 6, 3, 0xe8e8e8, 0.08);
                s.box(14, 0, 4, 2, 2, 0xe8a83c);  // beak
                s.box(14, 4, 2, 2, 2, 0xc84040);  // wattle
                int fu = 0 + 3, fv = 0 + 3;
                s.px(fu + 0, fv + 1, 0x1c1c1c); s.px(fu + 3, fv + 1, 0x1c1c1c);
                s.box(0, 9, 6, 8, 6, 0xe8e8e8, 0.08);
                s.box(24, 13, 1, 4, 6, 0xd8d8d8, 0.08);
                s.box(26, 0, 3, 5, 3, 0xe8a83c, 0.08);
            },
            packTex: new string[] { "entity/chicken.png" }));

        M("mooshroom", ((Func<MobModel>)(() => {
            var bas = cowModel(0xa42c2c, packTex: new string[] { "entity/cow/red_mooshroom.png" });
            var parts = bas.parts.ToList();
            // back mushrooms — small caps sampling the red hide region of the
            // mooshroom sheet (vanilla renders real mushroom blocks; this is a
            // textured approximation)
            parts.Add(new ModelPart(name: "shrooms", pivot: (0, 24, 2), boxes: new ModelBox[] {
                box(-1.5, 0, -7, 3, 3, 3, 20, 6), box(-1.5, 0, -1, 3, 3, 3, 20, 6),
                box(-1.5, 0, 5, 3, 3, 3, 20, 6),
            }));
            return new MobModel(texW: bas.texW, texH: bas.texH, parts: parts.ToArray(), anim: bas.anim,
                            scale: bas.scale, paint: bas.paint, packTex: bas.packTex);
        }))());

        // everything else EntityModels2.swift
        registerAllModels();
    }

    public static EntityGeometry buildEntityGeometry(string name)
    {
        ensureModels();
        var model = getModel(name);
        var verts = new List<float>();
        var partNames = new List<string>();
        for (int pi = 0; pi < model.parts.Length; pi++)
        {
            if (!(pi < 24)) continue;
            var p = model.parts[pi];
            partNames.Add(p.name);
            foreach (var b in p.boxes)
            {
                double g = b.grow;
                double x0 = (b.x - g) / 16, y0 = (b.y - g) / 16, z0 = (b.z - g) / 16;
                double x1 = (b.x + b.w + g) / 16, y1 = (b.y + b.h + g) / 16, z1 = (b.z + b.d + g) / 16;
                double tw = (double)model.texW, th = (double)model.texH;
                double u = b.u, v = b.v, w = b.w, h = b.h, d = b.d;
                var top = (u + d, v, u + d + w, v + d);
                var bottom = (u + d + w, v, u + d + w + w, v + d);
                var right = (u, v + d, u + d, v + d + h);
                var front = (u + d, v + d, u + d + w, v + d + h);
                var left = (u + d + w, v + d, u + d + w + d, v + d + h);
                var back = (u + d + w + d, v + d, u + d + w + d + w, v + d + h);

                void quad(double ax, double ay, double az, double bx, double by, double bz,
                          double cx, double cy, double cz, double dx, double dy, double dz,
                          double nx, double ny, double nz, (double, double, double, double) uv)
                {
                    var (u0, v0, u1, v1) = uv;
                    var corners = new (double, double, double, double, double)[] {
                        (ax, ay, az, u0 / tw, v1 / th), (bx, by, bz, u1 / tw, v1 / th),
                        (cx, cy, cz, u1 / tw, v0 / th), (dx, dy, dz, u0 / tw, v0 / th),
                    };
                    foreach (int i in new int[] { 0, 2, 1, 0, 3, 2 })
                    {
                        var c = corners[i];
                        verts.Add((float)c.Item1); verts.Add((float)c.Item2); verts.Add((float)c.Item3);
                        verts.Add((float)nx); verts.Add((float)ny); verts.Add((float)nz);
                        verts.Add((float)c.Item4); verts.Add((float)c.Item5);
                        verts.Add((float)pi);
                    }
                }

                quad(x0, y0, z0, x1, y0, z0, x1, y0, z1, x0, y0, z1, 0, -1, 0, bottom);
                quad(x0, y1, z1, x1, y1, z1, x1, y1, z0, x0, y1, z0, 0, 1, 0, top);
                quad(x1, y0, z0, x0, y0, z0, x0, y1, z0, x1, y1, z0, 0, 0, -1, front);
                quad(x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1, 0, 0, 1, back);
                quad(x0, y0, z0, x0, y0, z1, x0, y1, z1, x0, y1, z0, -1, 0, 0, right);
                quad(x1, y0, z1, x1, y0, z0, x1, y1, z0, x1, y1, z1, 1, 0, 0, left);
            }
        }
        var skin = new EntitySkin(model.texW, model.texH, name);
        model.paint(skin);
        return new EntityGeometry(verts: verts.ToArray(), vertexCount: verts.Count / 9, partNames: partNames.ToArray(), model: model, skin: skin);
    }
}

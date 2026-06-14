// CPU-simulated particle system — the Windows port of Sources/Pebble/ParticlesM.swift.
//
// This is backend-agnostic: it owns the live particle list, the per-type spawn
// recipes, the tick/physics and the per-frame instance packing. VulkanBackend /
// D3D12Backend consume the packed instance stream (12 floats/instance, the SAME
// encoding as the Metal original) in an instanced billboard pass drawn after the
// entities, sampling the block-atlas Texture2DArray (alpha-blended, depth-test on,
// depth-write off).
//
// HostBridge.addParticles / spawnPrecipitation feed spawn() here; Program drives
// tick() once per rendered frame and hands the packed buffer to the backend.
//
// Instance encoding (mirrors ParticleSystemM.render, 12 floats):
//   [0..2] worldPos - camPos (xyz)
//   [3..6] uvRect (u0 v0 u1 v1) within the tile
//   [7]    tile*256 + clamp(size*lifeFactor*100, 0, 255)   (layer + size word)
//   [8..10] rgb tint
//   [11]   light (0..1)

using System;
using System.Collections.Generic;
using PebbleCore;
using static PebbleCore.Reg;
using static PebbleCore.BlockCaches;
using static PebbleCore.BiomeGlobals;

namespace Pebble;

public sealed class Particles
{
    private struct Particle
    {
        public double x, y, z;
        public double vx, vy, vz;
        public double life, maxLife;
        public double size;
        public double gravity;
        public double drag;
        public int tile;
        public double u0, v0, u1, v1;
        public double r, g, b;
        public double light;
        public bool collide;
        public bool shrink;
    }

    public const int MaxParticles = 4096;
    private readonly List<Particle> _particles = new();
    private uint _rng = 12345;

    // Packed per-frame instance stream (12 floats/instance), reused across frames.
    private float[] _inst = new float[MaxParticles * 12];

    public int Count => _particles.Count;

    private double Rand()
    {
        _rng = unchecked(_rng * 1664525u + 1013904223u);
        return _rng / 4294967296.0;
    }

    private void Push(in Particle p)
    {
        if (_particles.Count >= MaxParticles) _particles.RemoveAt(0);
        _particles.Add(p);
    }

    public void Clear() => _particles.Clear();

    private static int IFloor(double v) => (int)Math.Floor(v);

    /// Spawn `count` particles of `type` around (x,y,z). `cell` carries the block
    /// cell for "block" particles, `note` for "note". Direct port of
    /// ParticleSystemM.spawn (same per-type recipes + LCG randomness shape).
    public void Spawn(World world, string type, double x, double y, double z,
                      int count, double spread = 0.3, int cell = 0, int note = 0)
    {
        double lightOf = world.lightAt(IFloor(x), IFloor(y), IFloor(z)) / 15.0;
        for (int it = 0; it < count; it++)
        {
            double ox = (Rand() - 0.5) * 2 * spread;
            double oy = (Rand() - 0.5) * 2 * spread;
            double oz = (Rand() - 0.5) * 2 * spread;
            var p = new Particle
            {
                x = x + ox, y = y + oy, z = z + oz,
                vx = (Rand() - 0.5) * 0.08, vy = Rand() * 0.1, vz = (Rand() - 0.5) * 0.08,
                maxLife = 20 + Rand() * 20,
                size = 0.08 + Rand() * 0.06,
                gravity = 0.04,
                drag = 0.98,
                tile = tileId("stone"),
                u0 = 0, v0 = 0, u1 = 0.25, v1 = 0.25,
                r = 1, g = 1, b = 1,
                light = lightOf,
                collide = false,
                shrink = true,
            };
            switch (type)
            {
                case "block":
                {
                    int id = cell >> 4;
                    if (id == 0) return;
                    var def = blockDefs[id];
                    p.tile = def.texFn != null ? def.texFn(cell & 15, 2)
                                               : (def.tex.Length == 0 ? 0 : def.tex[2]);
                    double u = Rand() * 0.75, v = Rand() * 0.75;
                    p.u0 = u; p.v0 = v; p.u1 = u + 0.25; p.v1 = v + 0.25;
                    if (TINT_OF[id] == 1 || TINT_OF[id] == 2)
                    {
                        int bi = world.biomeAt(IFloor(x), IFloor(y), IFloor(z));
                        var bm = (bi >= 0 && bi < BIOMES.Count) ? BIOMES[bi] : null;
                        uint c = TINT_OF[id] == 1 ? (bm?.grassColor ?? 0x91bd59u) : (bm?.foliageColor ?? 0x77ab2fu);
                        p.r = ((c >> 16) & 255) / 255.0; p.g = ((c >> 8) & 255) / 255.0; p.b = (c & 255) / 255.0;
                    }
                    p.vx = (Rand() - 0.5) * 0.2;
                    p.vy = Rand() * 0.18 + 0.05;
                    p.vz = (Rand() - 0.5) * 0.2;
                    p.collide = true;
                    p.maxLife = 14 + Rand() * 16;
                    break;
                }
                case "smoke":
                case "campfire_smoke":
                    p.tile = tileId("smoke_particle");
                    p.gravity = -0.004;
                    p.vy = 0.04 + Rand() * 0.04;
                    p.vx *= 0.4; p.vz *= 0.4;
                    p.maxLife = type == "campfire_smoke" ? 80 + Rand() * 60 : 30 + Rand() * 20;
                    { double g2 = 0.25 + Rand() * 0.2; p.r = g2; p.g = g2; p.b = g2; }
                    p.size = 0.12 + Rand() * 0.1;
                    break;
                case "flame":
                    p.tile = tileId("flame_particle");
                    p.gravity = -0.001;
                    p.vx *= 0.2; p.vy = 0.01; p.vz *= 0.2;
                    p.maxLife = 16 + Rand() * 10;
                    p.light = 1; p.size = 0.07;
                    break;
                case "soul_flame":
                    p.tile = tileId("flame_particle");
                    p.r = 0.2; p.g = 0.85; p.b = 0.9;
                    p.gravity = -0.002; p.maxLife = 20; p.light = 1; p.size = 0.07;
                    break;
                case "portal":
                    p.tile = tileId("portal_particle");
                    p.gravity = -0.01;
                    p.vx = (Rand() - 0.5) * 0.25; p.vy = (Rand() - 0.5) * 0.25; p.vz = (Rand() - 0.5) * 0.25;
                    p.r = 0.5 + Rand() * 0.3; p.g = 0.2; p.b = 0.8;
                    p.light = 1; p.maxLife = 30 + Rand() * 30;
                    break;
                case "crit":
                    p.tile = tileId("crit_particle");
                    p.vx = (Rand() - 0.5) * 0.4; p.vy = Rand() * 0.3; p.vz = (Rand() - 0.5) * 0.4;
                    p.r = 1; p.g = 0.85; p.b = 0.4; p.maxLife = 10 + Rand() * 8;
                    break;
                case "heart":
                    p.tile = tileId("heart_particle");
                    p.gravity = -0.002; p.vy = 0.05; p.maxLife = 24; p.size = 0.12; p.light = 1;
                    break;
                case "angry":
                    p.tile = tileId("angry_particle");
                    p.gravity = -0.002; p.maxLife = 20; p.size = 0.12; p.light = 1;
                    break;
                case "splash":
                case "bubble":
                    p.tile = tileId(type == "bubble" ? "bubble_particle" : "splash_particle");
                    p.gravity = type == "bubble" ? -0.02 : 0.06;
                    p.vy = type == "bubble" ? 0.05 : 0.18 + Rand() * 0.1;
                    p.r = 0.7; p.g = 0.8; p.b = 1; p.maxLife = type == "bubble" ? 18 : 14;
                    break;
                case "drip_water":
                case "drip_lava":
                    p.tile = tileId(type == "drip_water" ? "splash_particle" : "flame_particle");
                    p.gravity = 0.05; p.vx = 0; p.vz = 0; p.vy = 0; p.maxLife = 40; p.collide = true;
                    if (type == "drip_lava") { p.r = 1; p.g = 0.5; p.b = 0.1; p.light = 1; }
                    else { p.r = 0.3; p.g = 0.45; p.b = 1; }
                    break;
                case "rain":
                    p.tile = tileId("splash_particle");
                    p.gravity = 0; p.vx = 0; p.vz = 0; p.vy = -0.9 - Rand() * 0.2;
                    p.r = 0.6; p.g = 0.7; p.b = 1; p.maxLife = 30; p.size = 0.1;
                    p.collide = true; p.shrink = false;
                    break;
                case "snow":
                    p.tile = tileId("snow_particle");
                    p.gravity = 0; p.vy = -0.06 - Rand() * 0.04;
                    p.vx = (Rand() - 0.5) * 0.03; p.vz = (Rand() - 0.5) * 0.03;
                    p.maxLife = 120; p.size = 0.07; p.collide = true; p.shrink = false;
                    break;
                case "cherry_petal":
                    p.tile = tileId("petal_particle");
                    p.gravity = 0.003; p.drag = 0.96;
                    p.vx = (Rand() - 0.5) * 0.06; p.vy = -0.02; p.vz = (Rand() - 0.5) * 0.06;
                    p.maxLife = 140; p.size = 0.09; p.collide = true; p.shrink = false;
                    break;
                case "note":
                {
                    p.tile = tileId("note_particle");
                    p.gravity = -0.004;
                    double hue = note / 24.0;
                    p.r = Math.Min(1, Math.Max(0, Math.Sin(hue * 6.28) * 0.5 + 0.6));
                    p.g = Math.Min(1, Math.Max(0, Math.Sin(hue * 6.28 + 2.1) * 0.5 + 0.6));
                    p.b = Math.Min(1, Math.Max(0, Math.Sin(hue * 6.28 + 4.2) * 0.5 + 0.6));
                    p.maxLife = 18; p.light = 1; p.size = 0.12;
                    break;
                }
                case "redstone":
                    p.tile = tileId("redstone_particle");
                    p.gravity = 0; p.vx = 0; p.vy = 0.005; p.vz = 0;
                    p.r = 1; p.g = 0.15; p.b = 0.1; p.maxLife = 18; p.light = 1; p.size = 0.09;
                    break;
                case "explosion":
                    p.tile = tileId("smoke_particle");
                    p.size = 0.5 + Rand() * 0.6; p.gravity = -0.002;
                    p.vx = (Rand() - 0.5) * 0.3; p.vy = (Rand() - 0.5) * 0.3; p.vz = (Rand() - 0.5) * 0.3;
                    { double wv = 0.85 + Rand() * 0.15; p.r = wv; p.g = wv * 0.95; p.b = wv * 0.85; }
                    p.maxLife = 12 + Rand() * 10; p.light = 1;
                    break;
                case "sculk_soul":
                case "soul":
                    p.tile = tileId("soul_particle");
                    p.gravity = -0.006; p.r = 0.25; p.g = 0.8; p.b = 0.85; p.maxLife = 40; p.light = 1;
                    break;
                case "enchant":
                    p.tile = tileId("enchant_particle");
                    p.gravity = 0.02;
                    p.vx = (Rand() - 0.5) * 0.3; p.vy = 0.2 + Rand() * 0.2; p.vz = (Rand() - 0.5) * 0.3;
                    p.r = 0.8; p.g = 0.6; p.b = 1; p.maxLife = 26; p.light = 1;
                    break;
                case "slime":
                    p.tile = tileId("slime_particle");
                    p.r = 0.45; p.g = 0.8; p.b = 0.35; p.maxLife = 12;
                    break;
                case "totem":
                    p.tile = tileId("crit_particle");
                    p.r = 0.5 + Rand() * 0.5; p.g = 0.9; p.b = 0.3;
                    p.vx = (Rand() - 0.5) * 0.5; p.vy = Rand() * 0.5; p.vz = (Rand() - 0.5) * 0.5;
                    p.maxLife = 40 + Rand() * 30; p.light = 1;
                    break;
                case "squid_ink":
                    p.tile = tileId("smoke_particle");
                    p.r = 0.08; p.g = 0.08; p.b = 0.12; p.gravity = 0.01; p.maxLife = 30; p.size = 0.15;
                    break;
                case "glow":
                    p.tile = tileId("crit_particle");
                    p.r = 0.4; p.g = 0.95; p.b = 0.85; p.gravity = -0.001; p.light = 1; p.maxLife = 30;
                    break;
                case "dragon_breath":
                    p.tile = tileId("portal_particle");
                    p.r = 0.75; p.g = 0.2; p.b = 0.85; p.gravity = 0.002; p.light = 1;
                    p.maxLife = 30 + Rand() * 20; p.size = 0.14;
                    break;
                case "wax":
                    p.tile = tileId("crit_particle");
                    p.r = 1; p.g = 0.7; p.b = 0.2; p.maxLife = 12;
                    break;
                case "sweep":
                    p.tile = tileId("sweep_particle");
                    p.size = 0.6; p.gravity = 0; p.vx = 0; p.vy = 0; p.vz = 0; p.maxLife = 5; p.light = 1;
                    break;
                default:
                    break;
            }
            Push(p);
        }
    }

    /// Advance every particle one tick (physics + block collision). Direct port of
    /// ParticleSystemM.tick.
    public void Tick(World world)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.life += 1;
            if (p.life >= p.maxLife) { _particles.RemoveAt(i); continue; }
            p.vy -= p.gravity;
            p.vx *= p.drag; p.vy *= p.drag; p.vz *= p.drag;
            double nx = p.x + p.vx, ny = p.y + p.vy, nz = p.z + p.vz;
            if (p.collide)
            {
                int c = world.getBlock(IFloor(nx), IFloor(ny), IFloor(nz));
                int id = c >> 4;
                if (id != 0 && blockDefs[id].solid)
                {
                    p.vx = 0; p.vz = 0;
                    if (p.vy < 0) { p.vy = 0; p.life = Math.Max(p.life, p.maxLife - 4); }
                    _particles[i] = p;
                    continue;
                }
            }
            p.x = nx; p.y = ny; p.z = nz;
            _particles[i] = p;
        }
    }

    /// Pack this frame's particles into the instance stream (camera-relative).
    /// Returns the number of instances packed; the buffer is `Instances`.
    public int Pack(double camX, double camY, double camZ)
    {
        int n = Math.Min(_particles.Count, MaxParticles);
        for (int i = 0; i < n; i++)
        {
            var p = _particles[i];
            int o = i * 12;
            _inst[o + 0] = (float)(p.x - camX);
            _inst[o + 1] = (float)(p.y - camY);
            _inst[o + 2] = (float)(p.z - camZ);
            _inst[o + 3] = (float)p.u0;
            _inst[o + 4] = (float)p.v0;
            _inst[o + 5] = (float)p.u1;
            _inst[o + 6] = (float)p.v1;
            double lifeF = p.shrink ? Math.Max(0.2, 1 - p.life / p.maxLife) : 1;
            _inst[o + 7] = (float)(p.tile * 256 + Math.Min(255, p.size * lifeF * 100));
            _inst[o + 8] = (float)p.r;
            _inst[o + 9] = (float)p.g;
            _inst[o + 10] = (float)p.b;
            _inst[o + 11] = (float)p.light;
        }
        return n;
    }

    public float[] Instances => _inst;
}

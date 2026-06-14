// Backend-agnostic procedural cloud texture + star field, ported from
// WorldRenderer.buildClouds / buildStars in Sources/Pebble/WorldRenderer.swift.
// Both the Vulkan and D3D12 backends build these once and feed them into the
// cloud (sampler2D) and stars (point/quad) pipelines so the night sky and the
// daytime cloud layer match the Metal renderer.

using System;
using PebbleCore;

namespace Pebble.Gpu;

internal static class SkyAssets
{
    public const int CloudSize = 128;
    public const int StarCount = 1300;

    /// 128×128 RGBA8 cellular cloud mask (R=G=B = on/off, A=255). Same blobby,
    /// wrapping pattern the Metal renderer bakes (3-octave value noise, threshold).
    public static byte[] BuildCloudTexture()
    {
        int size = CloudSize;
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double v = 0.0;
                (int s, double w)[] octaves = { (8, 0.55), (16, 0.3), (32, 0.15) };
                foreach (var (s, w) in octaves)
                {
                    int cellW = size / s;
                    int gx = x / cellW, gy = y / cellW;
                    double fx = (double)(x % cellW) / cellW, fy = (double)(y % cellW) / cellW;
                    double H(int a, int b) =>
                        Hashing.hash2(31337, ((a % s) + s) % s, ((b % s) + s) % s, (uint)s) / 4294967296.0;
                    double v00 = H(gx, gy), v10 = H(gx + 1, gy), v01 = H(gx, gy + 1), v11 = H(gx + 1, gy + 1);
                    double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
                    v += ((v00 * (1 - sx) + v10 * sx) * (1 - sy) + (v01 * (1 - sx) + v11 * sx) * sy) * w;
                }
                byte on = v > 0.56 ? (byte)255 : (byte)0;
                int i = (y * size + x) * 4;
                px[i] = on; px[i + 1] = on; px[i + 2] = on; px[i + 3] = 255;
            }
        }
        return px;
    }

    /// Star directions on the unit sphere + a per-star magnitude (twinkle phase).
    /// Layout: float4 per star (x, y, z, mag) — matches the Metal stars buffer.
    public static float[] BuildStars()
    {
        int n = StarCount;
        var data = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            double u = Hashing.hash2(777, i, 0) / 4294967296.0;
            double v = Hashing.hash2(777, i, 1) / 4294967296.0;
            double theta = u * Math.PI * 2;
            double phi = Math.Acos(2 * v - 1);
            data[i * 4] = (float)(Math.Sin(phi) * Math.Cos(theta));
            data[i * 4 + 1] = (float)Math.Cos(phi);
            data[i * 4 + 2] = (float)(Math.Sin(phi) * Math.Sin(theta));
            data[i * 4 + 3] = (float)(Hashing.hash2(777, i, 2) / 4294967296.0);
        }
        return data;
    }
}

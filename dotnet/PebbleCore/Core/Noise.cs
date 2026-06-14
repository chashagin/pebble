// Simplex noise 2D/3D with seeded permutation, FBM stacks, splines.
// Bit-deterministic (same shuffle, same normalization constants) so worldgen
// produces the same worlds for the same seeds as the golden baselines.
// Ported from Sources/PebbleCore/Core/Noise.swift.

using System;

namespace PebbleCore;

public sealed class SimplexNoise
{
    private static readonly double[] GRAD3 = {
        1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0,
        1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1,
        0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1,
    };

    private static readonly double F2 = 0.5 * (Math.Sqrt(3.0) - 1);
    private static readonly double G2 = (3 - Math.Sqrt(3.0)) / 6;
    private const double F3 = 1.0 / 3.0;
    private const double G3 = 1.0 / 6.0;

    private readonly int[] perm = new int[512];
    private readonly int[] permMod12 = new int[512];

    public SimplexNoise(uint seed)
    {
        var rng = new RandomX(seed);
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        int k0 = 255;
        while (k0 > 0)
        {
            int j = rng.nextInt(k0 + 1);
            (p[k0], p[j]) = (p[j], p[k0]);
            k0 -= 1;
        }
        for (int k = 0; k < 512; k++)
        {
            perm[k] = p[k & 255];
            permMod12[k] = perm[k] % 12;
        }
    }

    public double noise2(double xin, double yin)
    {
        double n0 = 0.0, n1 = 0.0, n2 = 0.0;
        double s = (xin + yin) * F2;
        int i = (int)Math.Floor(xin + s), j = (int)Math.Floor(yin + s);
        double t = (double)(i + j) * G2;
        double x0 = xin - ((double)i - t), y0 = yin - ((double)j - t);
        int i1, j1;
        if (x0 > y0) { i1 = 1; j1 = 0; } else { i1 = 0; j1 = 1; }
        double x1 = x0 - i1 + G2, y1 = y0 - j1 + G2;
        double x2 = x0 - 1 + 2 * G2, y2 = y0 - 1 + 2 * G2;
        int ii = i & 255, jj = j & 255;
        double t0 = 0.5 - x0 * x0 - y0 * y0;
        if (t0 >= 0)
        {
            int gi0 = permMod12[ii + perm[jj]] * 3;
            t0 *= t0;
            n0 = t0 * t0 * (GRAD3[gi0] * x0 + GRAD3[gi0 + 1] * y0);
        }
        double t1 = 0.5 - x1 * x1 - y1 * y1;
        if (t1 >= 0)
        {
            int gi1 = permMod12[ii + i1 + perm[jj + j1]] * 3;
            t1 *= t1;
            n1 = t1 * t1 * (GRAD3[gi1] * x1 + GRAD3[gi1 + 1] * y1);
        }
        double t2 = 0.5 - x2 * x2 - y2 * y2;
        if (t2 >= 0)
        {
            int gi2 = permMod12[ii + 1 + perm[jj + 1]] * 3;
            t2 *= t2;
            n2 = t2 * t2 * (GRAD3[gi2] * x2 + GRAD3[gi2 + 1] * y2);
        }
        return 70.14805770654148 * (n0 + n1 + n2);
    }

    public double noise3(double xin, double yin, double zin)
    {
        double n0 = 0.0, n1 = 0.0, n2 = 0.0, n3 = 0.0;
        double s = (xin + yin + zin) * F3;
        int i = (int)Math.Floor(xin + s), j = (int)Math.Floor(yin + s), k = (int)Math.Floor(zin + s);
        double t = (double)(i + j + k) * G3;
        double x0 = xin - ((double)i - t), y0 = yin - ((double)j - t), z0 = zin - ((double)k - t);
        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }
        double x1 = x0 - i1 + G3, y1 = y0 - j1 + G3, z1 = z0 - k1 + G3;
        double x2 = x0 - i2 + 2 * G3, y2 = y0 - j2 + 2 * G3, z2 = z0 - k2 + 2 * G3;
        double x3 = x0 - 1 + 3 * G3, y3 = y0 - 1 + 3 * G3, z3 = z0 - 1 + 3 * G3;
        int ii = i & 255, jj = j & 255, kk = k & 255;
        double t0 = 0.6 - x0 * x0 - y0 * y0 - z0 * z0;
        if (t0 >= 0)
        {
            int gi0 = permMod12[ii + perm[jj + perm[kk]]] * 3;
            t0 *= t0;
            n0 = t0 * t0 * (GRAD3[gi0] * x0 + GRAD3[gi0 + 1] * y0 + GRAD3[gi0 + 2] * z0);
        }
        double t1 = 0.6 - x1 * x1 - y1 * y1 - z1 * z1;
        if (t1 >= 0)
        {
            int gi1 = permMod12[ii + i1 + perm[jj + j1 + perm[kk + k1]]] * 3;
            t1 *= t1;
            n1 = t1 * t1 * (GRAD3[gi1] * x1 + GRAD3[gi1 + 1] * y1 + GRAD3[gi1 + 2] * z1);
        }
        double t2 = 0.6 - x2 * x2 - y2 * y2 - z2 * z2;
        if (t2 >= 0)
        {
            int gi2 = permMod12[ii + i2 + perm[jj + j2 + perm[kk + k2]]] * 3;
            t2 *= t2;
            n2 = t2 * t2 * (GRAD3[gi2] * x2 + GRAD3[gi2 + 1] * y2 + GRAD3[gi2 + 2] * z2);
        }
        double t3 = 0.6 - x3 * x3 - y3 * y3 - z3 * z3;
        if (t3 >= 0)
        {
            int gi3 = permMod12[ii + 1 + perm[jj + 1 + perm[kk + 1]]] * 3;
            t3 *= t3;
            n3 = t3 * t3 * (GRAD3[gi3] * x3 + GRAD3[gi3 + 1] * y3 + GRAD3[gi3 + 2] * z3);
        }
        return 32.69587493801679 * (n0 + n1 + n2 + n3);
    }
}

/// fractal brownian motion over simplex octaves
public sealed class FBM
{
    public readonly SimplexNoise[] octaves;
    private readonly double[] amps;
    private readonly double[] freqs;
    private readonly double norm;

    public FBM(uint seed, int numOctaves, double baseFreq, double lacunarity = 2, double persistence = 0.5)
    {
        var o = new SimplexNoise[numOctaves];
        var a = new double[numOctaves];
        var f = new double[numOctaves];
        double amp = 1.0, freq = baseFreq, total = 0.0;
        for (int i = 0; i < numOctaves; i++)
        {
            o[i] = new SimplexNoise(unchecked(seed + (uint)(i * 1013)));
            a[i] = amp;
            f[i] = freq;
            total += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        octaves = o;
        amps = a;
        freqs = f;
        norm = 1 / total;
    }

    public double sample2(double x, double z)
    {
        double v = 0.0;
        for (int i = 0; i < octaves.Length; i++)
            v += octaves[i].noise2(x * freqs[i], z * freqs[i]) * amps[i];
        return v * norm;
    }

    public double sample3(double x, double y, double z)
    {
        double v = 0.0;
        for (int i = 0; i < octaves.Length; i++)
        {
            double f = freqs[i];
            v += octaves[i].noise3(x * f, y * f, z * f) * amps[i];
        }
        return v * norm;
    }

    /// ridged variant: (1 - |n|)², summed
    public double ridge2(double x, double z)
    {
        double v = 0.0;
        for (int i = 0; i < octaves.Length; i++)
        {
            double n = 1 - Math.Abs(octaves[i].noise2(x * freqs[i], z * freqs[i]));
            v += n * n * amps[i];
        }
        return v * norm;
    }
}

/// piecewise smoothstep-interpolated spline (vanilla-style terrain shaping)
public readonly struct Spline
{
    private readonly double[] xs;
    private readonly double[] ys;

    public Spline((double, double)[] points)
    {
        xs = new double[points.Length];
        ys = new double[points.Length];
        for (int i = 0; i < points.Length; i++) { xs[i] = points[i].Item1; ys[i] = points[i].Item2; }
    }

    public double at(double x)
    {
        int n = xs.Length;
        if (x <= xs[0]) return ys[0];
        if (x >= xs[n - 1]) return ys[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (xs[mid] <= x) lo = mid; else hi = mid;
        }
        double t = (x - xs[lo]) / (xs[hi] - xs[lo]);
        double st = t * t * (3 - 2 * t);
        return ys[lo] + (ys[hi] - ys[lo]) * st;
    }
}

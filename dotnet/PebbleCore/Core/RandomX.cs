// Seeded RNG + position hashing — bit-deterministic 32-bit wrap semantics:
// adds wrap as UInt32, shifts are logical, multiplies truncate to 32 bits.
// Verified against goldens. Ported from Sources/PebbleCore/Core/RandomX.swift.

using System;
using System.Collections.Generic;

namespace PebbleCore;

public static class Hashing
{
    public static uint hashString(string s)
    {
        uint h = 2166136261;
        // Game strings are ASCII/BMP identifiers, so iterating UTF-16 code units
        // and masking to 16 bits matches Swift's unicodeScalars & 0xFFFF.
        foreach (char ch in s)
        {
            h ^= (uint)ch & 0xFFFF;
            h = unchecked(h * 16777619);
        }
        return h;
    }

    /// murmur3 finalizer
    public static uint mix32(uint input)
    {
        uint h = input;
        h ^= h >> 16;
        h = unchecked(h * 0x85eb_ca6b);
        h ^= h >> 13;
        h = unchecked(h * 0xc2b2_ae35);
        h ^= h >> 16;
        return h;
    }

    private static uint imul(int a, uint b) => unchecked((uint)a * b);

    /// deterministic hash of 2D coords + seed + salt → uint32
    public static uint hash2(uint seed, int x, int z, uint salt = 0)
    {
        uint h = seed ^ unchecked(salt * 0x9e37_79b9);
        h = mix32(h ^ imul(x, 0x27d4_eb2d));
        h = mix32(h ^ imul(z, 0x1656_67b1));
        return h;
    }

    public static uint hash3(uint seed, int x, int y, int z, uint salt = 0)
    {
        uint h = seed ^ unchecked(salt * 0x9e37_79b9);
        h = mix32(h ^ imul(x, 0x27d4_eb2d));
        h = mix32(h ^ imul(y, 0x85eb_ca6b));
        h = mix32(h ^ imul(z, 0x1656_67b1));
        return h;
    }

    public static double hashFloat2(uint seed, int x, int z, uint salt = 0)
        => (double)hash2(seed, x, z, salt) / 4294967296.0;

    public static double hashFloat3(uint seed, int x, int y, int z, uint salt = 0)
        => (double)hash3(seed, x, y, z, salt) / 4294967296.0;

    public static RandomX chunkRandom(uint seed, int cx, int cz, uint salt)
        => new RandomX(hash2(seed, cx, cz, salt));
}

/// mutable PRNG (sfc32) — identical sequence for any seed, on any machine.
public struct RandomX
{
    public uint debugStateA => a;
    private uint a;
    private uint b;
    private uint c;
    private uint d;

    public RandomX(uint seed)
    {
        a = Hashing.mix32(seed);
        b = Hashing.mix32(a ^ 0x9e37_79b9);
        c = Hashing.mix32(b ^ 0x85eb_ca6b);
        d = Hashing.mix32(c ^ 0xc2b2_ae35);
        for (int i = 0; i < 8; i++) _ = next();
    }

    public uint next()
    {
        uint t = unchecked(a + b + d);
        d = unchecked(d + 1);
        a = b ^ (b >> 9);
        b = unchecked(c + (c << 3));
        c = (c << 21) | (c >> 11);
        c = unchecked(c + t);
        return t;
    }

    public double nextFloat() => (double)next() / 4294967296.0;
    public double nextDouble() => nextFloat();

    /// floor(float * bound) — must match deterministic for permutation shuffles to agree
    public int nextInt(int bound) => (int)Math.Floor(nextFloat() * bound);

    public int nextIntBetween(int minInc, int maxInc) => minInc + nextInt(maxInc - minInc + 1);

    public bool nextBoolean() => (next() & 1) == 0;

    public bool chance(double p) => nextFloat() < p;

    public double nextGaussian()
    {
        double u = 0.0, v = 0.0;
        while (u == 0) u = nextFloat();
        while (v == 0) v = nextFloat();
        return Math.Sqrt(-2.0 * Math.Log(u)) * DetMath.detCos(2.0 * Math.PI * v);
    }

    /// triangular distribution used by vanilla ore placement
    public double nextTriangular(double mode, double deviation)
        => mode + deviation * (nextFloat() - nextFloat());

    public T pick<T>(IReadOnlyList<T> arr) => arr[nextInt(arr.Count)];

    public void shuffle<T>(IList<T> arr)
    {
        int i = arr.Count - 1;
        while (i > 0)
        {
            int j = nextInt(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
            i -= 1;
        }
    }

    public T pickWeighted<T>(IReadOnlyList<T> arr, Func<T, double> weightOf)
    {
        double total = 0.0;
        foreach (var t in arr) total += weightOf(t);
        double r = nextFloat() * total;
        foreach (var t in arr)
        {
            r -= weightOf(t);
            if (r <= 0) return t;
        }
        return arr[arr.Count - 1];
    }
}

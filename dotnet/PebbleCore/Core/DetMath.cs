// Portable deterministic float math — bit-reproducible reference implementation
// (fdlibm 5.3c sin/cos/atan/atan2). Simulation code calls these instead of the
// platform trig so results are bit-identical with the golden baselines: only
// IEEE-exact operations (+ - * / sqrt) are used. NEVER use Math.FusedMultiplyAdd
// here — an FMA contraction would diverge from the Swift/LLVM goldens.
//
// The sin/cos/atan/atan2 kernels below are ported from fdlibm 5.3c:
//
//   Copyright (C) 1993, 2004 by Sun Microsystems, Inc. All rights reserved.
//
//   Developed at SunSoft, a Sun Microsystems, Inc. business.
//   Permission to use, copy, modify, and distribute this
//   software is freely granted, provided that this notice
//   is preserved.
//
// Ported from Sources/PebbleCore/Core/DetMath.swift.

using System;

namespace PebbleCore;

public static class DetMath
{
    private static int HI(double x) => unchecked((int)(BitConverter.DoubleToUInt64Bits(x) >> 32));
    private static int LO(double x) => unchecked((int)(BitConverter.DoubleToUInt64Bits(x) & 0xffff_ffff));
    private static double fromWords(uint h, uint l) => BitConverter.UInt64BitsToDouble(((ulong)h << 32) | (ulong)l);
    private static double setHI(double x, uint h)
        => BitConverter.UInt64BitsToDouble(((ulong)h << 32) | (BitConverter.DoubleToUInt64Bits(x) & 0xffff_ffff));

    public static double detHyp(double a, double b) => Math.Sqrt(a * a + b * b);
    public static double detHyp3(double a, double b, double c) => Math.Sqrt(a * a + b * b + c * c);

    // -----------------------------------------------------------------------
    // __kernel_sin / __kernel_cos
    // -----------------------------------------------------------------------
    private const double half = 0.5;
    private const double one = 1.0;

    private static readonly double S1 = fromWords(0xbfc55555, 0x55555549);
    private static readonly double S2 = fromWords(0x3f811111, 0x1110f8a6);
    private static readonly double S3 = fromWords(0xbf2a01a0, 0x19c161d5);
    private static readonly double S4 = fromWords(0x3ec71de3, 0x57b1fe7d);
    private static readonly double S5 = fromWords(0xbe5ae5e6, 0x8a2b9ceb);
    private static readonly double S6 = fromWords(0x3de5d93a, 0x5acfd57c);

    private static readonly double C1 = fromWords(0x3fa55555, 0x5555554c);
    private static readonly double C2 = fromWords(0xbf56c16c, 0x16c15177);
    private static readonly double C3 = fromWords(0x3efa01a0, 0x19cb1590);
    private static readonly double C4 = fromWords(0xbe927e4f, 0x809c52ad);
    private static readonly double C5 = fromWords(0x3e21ee9e, 0xbdb4b1c4);
    private static readonly double C6 = fromWords(0xbda8fae9, 0xbe8838d4);

    private static double kernelSin(double x, double y, int iy)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix < 0x3e400000)            /* |x| < 2**-27 */
        {
            if ((int)x == 0) return x;
        }
        double z = x * x;
        double v = z * x;
        double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        if (iy == 0) return x + v * (S1 + z * r);
        return x - ((z * (half * y - v * r) - y) - v * S1);
    }

    private static double kernelCos(double x, double y)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix < 0x3e400000)            /* if |x| < 2**-27 */
        {
            if ((int)x == 0) return one;
        }
        double z = x * x;
        double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        if (ix < 0x3fd33333)            /* if |x| < 0.3 */
        {
            return one - (0.5 * z - (z * r - x * y));
        }
        double qx;
        if (ix > 0x3fe90000)            /* x > 0.78125 */
        {
            qx = 0.28125;
        }
        else
        {
            qx = fromWords((uint)(ix - 0x00200000), 0);   /* x/4 */
        }
        double hz = 0.5 * z - qx;
        double a = one - qx;
        return a - (hz - (z * r - x * y));
    }

    // -----------------------------------------------------------------------
    // __ieee754_rem_pio2 (small + medium ranges; gameplay never exceeds 2^19·π/2)
    // -----------------------------------------------------------------------
    private static readonly double invpio2 = fromWords(0x3fe45f30, 0x6dc9c883);
    private static readonly double pio2_1 = fromWords(0x3ff921fb, 0x54400000);
    private static readonly double pio2_1t = fromWords(0x3dd0b461, 0x1a626331);
    private static readonly double pio2_2 = fromWords(0x3dd0b461, 0x1a600000);
    private static readonly double pio2_2t = fromWords(0x3ba3198a, 0x2e037073);
    private static readonly double pio2_3 = fromWords(0x3ba3198a, 0x2e000000);
    private static readonly double pio2_3t = fromWords(0x397b839a, 0x252049c1);

    private static readonly int[] npio2_hw = {
        0x3ff921fb, 0x400921fb, 0x4012d97c, 0x401921fb, 0x401f6a7a, 0x4022d97c,
        0x4025fdbb, 0x402921fb, 0x402c463a, 0x402f6a7a, 0x4031475c, 0x4032d97c,
        0x40346b9c, 0x4035fdbb, 0x40378fdb, 0x403921fb, 0x403ab41b, 0x403c463a,
        0x403dd85a, 0x403f6a7a, 0x40407e4c, 0x4041475c, 0x4042106c, 0x4042d97c,
        0x4043a28c, 0x40446b9c, 0x404534ac, 0x4045fdbb, 0x4046c6cb, 0x40478fdb,
        0x404858eb, 0x404921fb,
    };

    private static (int n, double y0, double y1) remPio2(double x)
    {
        int hx = HI(x);
        int ix = hx & 0x7fffffff;
        if (ix <= 0x3fe921fb)           /* |x| ~<= pi/4, no need for reduction */
        {
            return (0, x, 0);
        }
        if (ix < 0x4002d97c)            /* |x| < 3pi/4, special case with n=+-1 */
        {
            if (hx > 0)
            {
                double z = x - pio2_1;
                double y0, y1;
                if (ix != 0x3ff921fb)   /* 33+53 bit pi is good enough */
                {
                    y0 = z - pio2_1t;
                    y1 = (z - y0) - pio2_1t;
                }
                else                    /* near pi/2, use 33+33+53 bit pi */
                {
                    z -= pio2_2;
                    y0 = z - pio2_2t;
                    y1 = (z - y0) - pio2_2t;
                }
                return (1, y0, y1);
            }
            else
            {
                double z = x + pio2_1;
                double y0, y1;
                if (ix != 0x3ff921fb)
                {
                    y0 = z + pio2_1t;
                    y1 = (z - y0) + pio2_1t;
                }
                else
                {
                    z += pio2_2;
                    y0 = z + pio2_2t;
                    y1 = (z - y0) + pio2_2t;
                }
                return (-1, y0, y1);
            }
        }
        if (ix <= 0x413921fb)           /* |x| ~<= 2^19*(pi/2), medium size */
        {
            double t = Math.Abs(x);
            int n = (int)(t * invpio2 + half);
            double fn = (double)n;
            double r = t - fn * pio2_1;
            double w = fn * pio2_1t;    /* 1st round good to 85 bit */
            double y0;
            if (n < 32 && ix != npio2_hw[n - 1])
            {
                y0 = r - w;             /* quick check no cancellation */
            }
            else
            {
                int j = ix >> 20;
                y0 = r - w;
                int high = HI(y0);
                int i = j - ((high >> 20) & 0x7ff);
                if (i > 16)             /* 2nd iteration needed, good to 118 */
                {
                    double t2 = r;
                    w = fn * pio2_2;
                    r = t2 - w;
                    w = fn * pio2_2t - ((t2 - r) - w);
                    y0 = r - w;
                    high = HI(y0);
                    i = j - ((high >> 20) & 0x7ff);
                    if (i > 49)         /* 3rd iteration, 151 bits acc */
                    {
                        t2 = r;
                        w = fn * pio2_3;
                        r = t2 - w;
                        w = fn * pio2_3t - ((t2 - r) - w);
                        y0 = r - w;
                    }
                }
            }
            double y1 = (r - y0) - w;
            if (hx < 0) return (-n, -y0, -y1);
            return (n, y0, y1);
        }
        throw new InvalidOperationException($"DetMath: |x| too large for trig reduction: {x}");
    }

    // -----------------------------------------------------------------------
    // sin / cos
    // -----------------------------------------------------------------------
    public static double detSin(double x)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return kernelSin(x, 0, 0);
        if (ix >= 0x7ff00000) return x - x;   /* NaN/Inf */
        var (n, y0, y1) = remPio2(x);
        switch (n & 3)
        {
            case 0: return kernelSin(y0, y1, 1);
            case 1: return kernelCos(y0, y1);
            case 2: return -kernelSin(y0, y1, 1);
            default: return -kernelCos(y0, y1);
        }
    }

    public static double detCos(double x)
    {
        int ix = HI(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return kernelCos(x, 0);
        if (ix >= 0x7ff00000) return x - x;
        var (n, y0, y1) = remPio2(x);
        switch (n & 3)
        {
            case 0: return kernelCos(y0, y1);
            case 1: return -kernelSin(y0, y1, 1);
            case 2: return -kernelCos(y0, y1);
            default: return kernelSin(y0, y1, 1);
        }
    }

    // -----------------------------------------------------------------------
    // atan / atan2
    // -----------------------------------------------------------------------
    private static readonly double[] atanhi = {
        fromWords(0x3fddac67, 0x0561bb4f), fromWords(0x3fe921fb, 0x54442d18),
        fromWords(0x3fef730b, 0xd281f69b), fromWords(0x3ff921fb, 0x54442d18),
    };
    private static readonly double[] atanlo = {
        fromWords(0x3c7a2b7f, 0x222f65e2), fromWords(0x3c81a626, 0x33145c07),
        fromWords(0x3c700788, 0x7af0cbbd), fromWords(0x3c91a626, 0x33145c07),
    };
    private static readonly double[] aT = {
        fromWords(0x3fd55555, 0x5555550d), fromWords(0xbfc99999, 0x9998ebc4),
        fromWords(0x3fc24924, 0x920083ff), fromWords(0xbfbc71c6, 0xfe231671),
        fromWords(0x3fb745cd, 0xc54c206e), fromWords(0xbfb3b0f2, 0xaf749a6d),
        fromWords(0x3fb10d66, 0xa0d03d51), fromWords(0xbfadde2d, 0x52defd9a),
        fromWords(0x3fa97b4b, 0x24760deb), fromWords(0xbfa2b444, 0x2c6a6c2f),
        fromWords(0x3f90ad3a, 0xe322da11),
    };
    private static readonly double hugeVal = 1.0e300;

    public static double detAtan(double xIn)
    {
        double x = xIn;
        int hx = HI(x);
        int ix = hx & 0x7fffffff;
        if (ix >= 0x44100000)           /* if |x| >= 2^66 */
        {
            int low = LO(x);
            if (ix > 0x7ff00000 || (ix == 0x7ff00000 && low != 0)) return x + x; /* NaN */
            if (hx > 0) return atanhi[3] + atanlo[3];
            return -atanhi[3] - atanlo[3];
        }
        int id;
        if (ix < 0x3fdc0000)            /* |x| < 0.4375 */
        {
            if (ix < 0x3e200000)        /* |x| < 2^-29 */
            {
                if (hugeVal + x > one) return x;
            }
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)        /* |x| < 1.1875 */
            {
                if (ix < 0x3fe60000)    /* 7/16 <= |x| < 11/16 */
                {
                    id = 0; x = (2.0 * x - one) / (2.0 + x);
                }
                else                    /* 11/16 <= |x| < 19/16 */
                {
                    id = 1; x = (x - one) / (x + one);
                }
            }
            else
            {
                if (ix < 0x40038000)    /* |x| < 2.4375 */
                {
                    id = 2; x = (x - 1.5) / (one + 1.5 * x);
                }
                else                    /* 2.4375 <= |x| < 2^66 */
                {
                    id = 3; x = -1.0 / x;
                }
            }
        }
        double z = x * x;
        double w = z * z;
        double s1 = z * (aT[0] + w * (aT[2] + w * (aT[4] + w * (aT[6] + w * (aT[8] + w * aT[10])))));
        double s2 = w * (aT[1] + w * (aT[3] + w * (aT[5] + w * (aT[7] + w * aT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        double zz = atanhi[id] - ((x * (s1 + s2) - atanlo[id]) - x);
        return hx < 0 ? -zz : zz;
    }

    private static readonly double tiny = 1.0e-300;
    private static readonly double pi_o_4 = fromWords(0x3fe921fb, 0x54442d18);
    private static readonly double pi_o_2 = fromWords(0x3ff921fb, 0x54442d18);
    private static readonly double m_pi = fromWords(0x400921fb, 0x54442d18);
    private static readonly double pi_lo = fromWords(0x3ca1a626, 0x33145c07);

    public static double detAtan2(double y, double x)
    {
        int hx = HI(x), lx = LO(x);
        int ix = hx & 0x7fffffff;
        int hy = HI(y), ly = LO(y);
        int iy = hy & 0x7fffffff;
        int lxNZ = (int)(((uint)lx | (uint)unchecked(0 - lx)) >> 31);
        int lyNZ = (int)(((uint)ly | (uint)unchecked(0 - ly)) >> 31);
        if ((ix | lxNZ) > 0x7ff00000 || (iy | lyNZ) > 0x7ff00000)  /* x or y is NaN */
        {
            return x + y;
        }
        if ((unchecked(hx - 0x3ff00000) | lx) == 0) return detAtan(y);   /* x=1.0 */
        int m = ((hy >> 31) & 1) | ((hx >> 30) & 2);                     /* 2*sign(x)+sign(y) */

        /* when y = 0 */
        if ((iy | ly) == 0)
        {
            switch (m)
            {
                case 0: case 1: return y;       /* atan(+-0,+anything)=+-0 */
                case 2: return m_pi + tiny;     /* atan(+0,-anything) = pi */
                default: return -m_pi - tiny;   /* atan(-0,-anything) =-pi */
            }
        }
        /* when x = 0 */
        if ((ix | lx) == 0) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;

        /* when x is INF */
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000)
            {
                switch (m)
                {
                    case 0: return pi_o_4 + tiny;
                    case 1: return -pi_o_4 - tiny;
                    case 2: return 3.0 * pi_o_4 + tiny;
                    default: return -3.0 * pi_o_4 - tiny;
                }
            }
            else
            {
                switch (m)
                {
                    case 0: return 0.0;
                    case 1: return -0.0;
                    case 2: return m_pi + tiny;
                    default: return -m_pi - tiny;
                }
            }
        }
        /* when y is INF */
        if (iy == 0x7ff00000) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;

        /* compute y/x */
        int k = unchecked(iy - ix) >> 20;
        double z;
        if (k > 60) z = pi_o_2 + 0.5 * pi_lo;       /* |y/x| >  2**60 */
        else if (hx < 0 && k < -60) z = 0.0;        /* |y|/x < -2**60 */
        else z = detAtan(Math.Abs(y / x));          /* safe to do y/x */
        switch (m)
        {
            case 0: return z;                                       /* atan(+,+) */
            case 1: return setHI(z, (uint)HI(z) ^ 0x80000000);      /* atan(-,+) */
            case 2: return m_pi - (z - pi_lo);                      /* atan(+,-) */
            default: return (z - pi_lo) - m_pi;                     /* atan(-,-) */
        }
    }

    // -----------------------------------------------------------------------
    // Seeded gameplay RNG — reference implementation. All state-affecting
    // randomness draws from this stream in frozen call order; cosmetic-only
    // randomness stays off-stream so golden hashes never see it.
    // -----------------------------------------------------------------------
    public static RandomX gameRng = new RandomX(0x6A57);
    public static void resetGameRng(uint seed) { gameRng = new RandomX(seed); }
}

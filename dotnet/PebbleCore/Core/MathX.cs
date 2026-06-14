// Math utilities: scalars, Vec3 (Double, for sim), Mat4 (Float, clip space
// z∈[0,1]), AABB with axis sweeps, frustum. Ported from
// Sources/PebbleCore/Core/MathX.swift. simd_* helpers are reimplemented as
// plain struct math; the Vec3/AABB/sweep paths are determinism-critical.

using System;

namespace PebbleCore;

// ---- Vec3 (Double — simulation space) -----------------------------------------
public struct Vec3 : IEquatable<Vec3>
{
    public double X, Y, Z;
    public Vec3(double x = 0, double y = 0, double z = 0) { X = x; Y = y; Z = z; }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(Vec3 a, Vec3 b) => new Vec3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

    public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object o) => o is Vec3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X}, {Y}, {Z})";
}

// ---- Vec3f / Vec4f (Float — render space) -------------------------------------
public struct Vec3f
{
    public float X, Y, Z;
    public Vec3f(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static Vec3f operator -(Vec3f a) => new Vec3f(-a.X, -a.Y, -a.Z);
}

public struct Vec4f
{
    public float X, Y, Z, W;
    public Vec4f(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    public static Vec4f operator +(Vec4f a, Vec4f b) => new Vec4f(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static Vec4f operator -(Vec4f a, Vec4f b) => new Vec4f(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    public static Vec4f operator *(Vec4f a, float s) => new Vec4f(a.X * s, a.Y * s, a.Z * s, a.W * s);
}

// ---- Mat4 (Float, column-major, clip space) -----------------------------------
public struct Mat4
{
    // columns c0..c3; element (col,row) == c{col}.{x,y,z,w}, mirroring simd m[col][row]
    public Vec4f c0, c1, c2, c3;
    public Mat4(Vec4f c0, Vec4f c1, Vec4f c2, Vec4f c3) { this.c0 = c0; this.c1 = c1; this.c2 = c2; this.c3 = c3; }

    public static readonly Mat4 Zero = new Mat4(
        new Vec4f(0, 0, 0, 0), new Vec4f(0, 0, 0, 0), new Vec4f(0, 0, 0, 0), new Vec4f(0, 0, 0, 0));
    public static readonly Mat4 Identity = new Mat4(
        new Vec4f(1, 0, 0, 0), new Vec4f(0, 1, 0, 0), new Vec4f(0, 0, 1, 0), new Vec4f(0, 0, 0, 1));
}

// ---- AABB (Double — collision space) ------------------------------------------
public struct AABB
{
    public double x0, y0, z0;
    public double x1, y1, z1;

    public AABB(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        this.x0 = x0; this.y0 = y0; this.z0 = z0;
        this.x1 = x1; this.y1 = y1; this.z1 = z1;
    }

    public AABB offset(double x, double y, double z)
        => new AABB(x0 + x, y0 + y, z0 + z, x1 + x, y1 + y, z1 + z);

    public AABB expand(double x, double y, double z)
        => new AABB(x0 - x, y0 - y, z0 - z, x1 + x, y1 + y, z1 + z);

    public bool intersects(AABB b)
        => x0 < b.x1 && x1 > b.x0 && y0 < b.y1 && y1 > b.y0 && z0 < b.z1 && z1 > b.z0;

    public bool contains(double x, double y, double z)
        => x >= x0 && x < x1 && y >= y0 && y < y1 && z >= z0 && z < z1;
}

// ---- frustum (Float, clip space) ----------------------------------------------
public struct Frustum
{
    /// 6 planes × (a,b,c,d): left, right, bottom, top, near, far
    public float[] planes;

    public static Frustum Create() => new Frustum { planes = new float[24] };

    public void setFromMatrix(Mat4 m)
    {
        // rows of the column-major matrix
        var r0 = new Vec4f(m.c0.X, m.c1.X, m.c2.X, m.c3.X);
        var r1 = new Vec4f(m.c0.Y, m.c1.Y, m.c2.Y, m.c3.Y);
        var r2 = new Vec4f(m.c0.Z, m.c1.Z, m.c2.Z, m.c3.Z);
        var r3 = new Vec4f(m.c0.W, m.c1.W, m.c2.W, m.c3.W);
        // clip: -w ≤ x,y ≤ w and 0 ≤ z ≤ w → near plane is r2 alone
        var ps = new[] { r3 + r0, r3 - r0, r3 + r1, r3 - r1, r2, r3 - r2 };
        for (int i = 0; i < ps.Length; i++)
        {
            var pl = ps[i];
            float len = MathF.Sqrt(pl.X * pl.X + pl.Y * pl.Y + pl.Z * pl.Z);
            var n = len > 0 ? pl * (1f / len) : pl;
            planes[i * 4] = n.X;
            planes[i * 4 + 1] = n.Y;
            planes[i * 4 + 2] = n.Z;
            planes[i * 4 + 3] = n.W;
        }
    }

    public bool intersectsBox(float bx0, float by0, float bz0, float bx1, float by1, float bz1)
    {
        for (int i = 0; i < 6; i++)
        {
            int o = i * 4;
            float px = planes[o] > 0 ? bx1 : bx0;
            float py = planes[o + 1] > 0 ? by1 : by0;
            float pz = planes[o + 2] > 0 ? bz1 : bz0;
            if (planes[o] * px + planes[o + 1] * py + planes[o + 2] * pz + planes[o + 3] < 0) return false;
        }
        return true;
    }
}

public static class MathX
{
    // ---- scalars ---------------------------------------------------------------
    public static double clampD(double x, double lo, double hi) => x < lo ? lo : (x > hi ? hi : x);
    public static float clampF(float x, float lo, float hi) => x < lo ? lo : (x > hi ? hi : x);
    public static double lerpD(double a, double b, double t) => a + (b - a) * t;
    public static float lerpF(float a, float b, float t) => a + (b - a) * t;
    public static double smoothstepD(double t) => t * t * (3 - 2 * t);
    public static double smootherstepD(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    public static double degToRad(double d) => d * Math.PI / 180;
    public static double radToDeg(double r) => r * 180 / Math.PI;

    public static double wrapDegrees(double input)
    {
        double d = input % 360;
        if (d >= 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    public static double approachDegrees(double cur, double target, double maxStep)
    {
        double delta = wrapDegrees(target - cur);
        return cur + clampD(delta, -maxStep, maxStep);
    }

    public static double mapRange(double x, double a0, double a1, double b0, double b1)
        => b0 + (b1 - b0) * clampD((x - a0) / (a1 - a0), 0, 1);

    public static double easeOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double easeInOutQuad(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    // ---- Vec3 helpers ----------------------------------------------------------
    public static Vec3 vec3(double x = 0, double y = 0, double z = 0) => new Vec3(x, y, z);
    public static double vLen(Vec3 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
    public static double vLenSq(Vec3 a) => a.X * a.X + a.Y * a.Y + a.Z * a.Z;
    public static double vDist(Vec3 a, Vec3 b) => vLen(a - b);
    public static double vDistSq(Vec3 a, Vec3 b) => vLenSq(a - b);
    public static double vDot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 vCross(Vec3 a, Vec3 b)
        => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vec3 vLerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

    public static Vec3 vNorm(Vec3 a)
    {
        double l = vLen(a);
        return l < 1e-9 ? new Vec3() : a / l;
    }

    // ---- Vec3f helpers (render) ------------------------------------------------
    private static float fLen(Vec3f a) => MathF.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
    private static Vec3f fNorm(Vec3f a) { float l = fLen(a); return new Vec3f(a.X / l, a.Y / l, a.Z / l); }
    private static Vec3f fCross(Vec3f a, Vec3f b)
        => new Vec3f(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static float fDot(Vec3f a, Vec3f b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // ---- Mat4 (Float, column-major, clip space) --------------------------------
    public static Mat4 mat4Identity() => Mat4.Identity;

    /// perspective with depth range z' ∈ [0, 1]
    public static Mat4 mat4Perspective(float fovYRad, float aspect, float near, float far)
    {
        float f = 1 / MathF.Tan(fovYRad / 2);
        var m = Mat4.Zero;
        m.c0.X = f / aspect;
        m.c1.Y = f;
        m.c2.Z = far / (near - far);
        m.c2.W = -1;
        m.c3.Z = (far * near) / (near - far);
        return m;
    }

    /// ortho with depth range z' ∈ [0, 1]
    public static Mat4 mat4Ortho(float l, float r, float b, float t, float n, float f)
    {
        var m = Mat4.Zero;
        m.c0.X = 2 / (r - l);
        m.c1.Y = 2 / (t - b);
        m.c2.Z = -1 / (f - n);
        m.c3.X = -(r + l) / (r - l);
        m.c3.Y = -(t + b) / (t - b);
        m.c3.Z = -n / (f - n);
        m.c3.W = 1;
        return m;
    }

    public static Mat4 mat4LookDir(Vec3f eye, Vec3f dir, Vec3f up)
    {
        var z = fNorm(-dir);
        var x = fNorm(fCross(up, z));
        var y = fCross(z, x);
        return new Mat4(
            new Vec4f(x.X, y.X, z.X, 0),
            new Vec4f(x.Y, y.Y, z.Y, 0),
            new Vec4f(x.Z, y.Z, z.Z, 0),
            new Vec4f(-fDot(x, eye), -fDot(y, eye), -fDot(z, eye), 1));
    }

    public static Mat4 mat4Translate(Mat4 m, float x, float y, float z)
    {
        var outm = m;
        outm.c3 = m.c0 * x + m.c1 * y + m.c2 * z + m.c3;
        return outm;
    }

    public static Mat4 mat4Scale(Mat4 m, float x, float y, float z)
    {
        var outm = m;
        outm.c0 = m.c0 * x;
        outm.c1 = m.c1 * y;
        outm.c2 = m.c2 * z;
        return outm;
    }

    public static Mat4 mat4RotateX(Mat4 m, float rad)
    {
        float s = MathF.Sin(rad), c = MathF.Cos(rad);
        var outm = m;
        outm.c1 = m.c1 * c + m.c2 * s;
        outm.c2 = m.c2 * c - m.c1 * s;
        return outm;
    }

    public static Mat4 mat4RotateY(Mat4 m, float rad)
    {
        float s = MathF.Sin(rad), c = MathF.Cos(rad);
        var outm = m;
        outm.c0 = m.c0 * c - m.c2 * s;
        outm.c2 = m.c0 * s + m.c2 * c;
        return outm;
    }

    public static Mat4 mat4RotateZ(Mat4 m, float rad)
    {
        float s = MathF.Sin(rad), c = MathF.Cos(rad);
        var outm = m;
        outm.c0 = m.c0 * c + m.c1 * s;
        outm.c1 = m.c1 * c - m.c0 * s;
        return outm;
    }

    // ---- AABB axis sweeps ------------------------------------------------------
    /// how far box `a` may move along X by `d` before hitting `b`
    public static double sweepX(AABB a, AABB b, double dIn)
    {
        double d = dIn;
        if (a.y1 <= b.y0 || a.y0 >= b.y1 || a.z1 <= b.z0 || a.z0 >= b.z1) return d;
        if (d > 0 && a.x1 <= b.x0) { double m = b.x0 - a.x1; if (m < d) d = m; }
        else if (d < 0 && a.x0 >= b.x1) { double m = b.x1 - a.x0; if (m > d) d = m; }
        return d;
    }

    public static double sweepY(AABB a, AABB b, double dIn)
    {
        double d = dIn;
        if (a.x1 <= b.x0 || a.x0 >= b.x1 || a.z1 <= b.z0 || a.z0 >= b.z1) return d;
        if (d > 0 && a.y1 <= b.y0) { double m = b.y0 - a.y1; if (m < d) d = m; }
        else if (d < 0 && a.y0 >= b.y1) { double m = b.y1 - a.y0; if (m > d) d = m; }
        return d;
    }

    public static double sweepZ(AABB a, AABB b, double dIn)
    {
        double d = dIn;
        if (a.x1 <= b.x0 || a.x0 >= b.x1 || a.y1 <= b.y0 || a.y0 >= b.y1) return d;
        if (d > 0 && a.z1 <= b.z0) { double m = b.z0 - a.z1; if (m < d) d = m; }
        else if (d < 0 && a.z0 >= b.z1) { double m = b.z1 - a.z0; if (m > d) d = m; }
        return d;
    }

    /// ray vs AABB; returns t or -1
    public static double rayAABB(double ox, double oy, double oz, double dx, double dy, double dz, AABB b)
    {
        double tmin = double.NegativeInfinity, tmax = double.PositiveInfinity;
        if (Math.Abs(dx) < 1e-12) { if (ox < b.x0 || ox > b.x1) return -1; }
        else
        {
            double t1 = (b.x0 - ox) / dx, t2 = (b.x1 - ox) / dx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
        }
        if (Math.Abs(dy) < 1e-12) { if (oy < b.y0 || oy > b.y1) return -1; }
        else
        {
            double t1 = (b.y0 - oy) / dy, t2 = (b.y1 - oy) / dy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
        }
        if (Math.Abs(dz) < 1e-12) { if (oz < b.z0 || oz > b.z1) return -1; }
        else
        {
            double t1 = (b.z0 - oz) / dz, t2 = (b.z1 - oz) / dz;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
        }
        if (tmax < tmin || tmax < 0) return -1;
        return tmin >= 0 ? tmin : tmax;
    }
}

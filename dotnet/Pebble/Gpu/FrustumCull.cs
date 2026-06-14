// View-frustum culling for chunk sections, shared verbatim by both GPU backends
// (Vulkan + D3D12) so the test is byte-for-byte identical on each.
//
// Convention-agnostic clip-space corner test: we transform each of a section's 8
// camera-relative AABB corners by the SAME viewProj matrix the shader uses
// (clip = viewProj applied as `viewProj * vec4(rel,1)` on the GPU; here the
// matrix is stored row-major in System.Numerics and uploaded un-transposed, so
// Vector4.Transform(v, viewProj) — which computes v*M, i.e. transpose(M)*v —
// reproduces the GPU's clip coordinate exactly). A section is culled only when
// ALL 8 corners fall outside the SAME one of the 6 clip planes
// (D3D/Vulkan [0,1] depth convention): x<-w, x>w, y<-w, y>w, z<0, z>w.
//
// The box is expanded by one block on every side (an epsilon margin) so meshes
// whose geometry pokes slightly past a 16-block section boundary are never
// wrongly culled.

using System.Numerics;

namespace Pebble.Gpu;

internal static class FrustumCull
{
    // How far (in blocks) to grow the section AABB on every side before testing.
    private const float Expand = 1.0f;

    /// Returns true if the 16×16×16 section whose camera-relative near corner is
    /// `origin` is (possibly) visible and should be drawn; false if it is fully
    /// outside the view frustum and can be skipped this frame.
    public static bool SectionVisible(Vector3 origin, in Matrix4x4 viewProj)
    {
        float x0 = origin.X - Expand, y0 = origin.Y - Expand, z0 = origin.Z - Expand;
        float x1 = origin.X + 16f + Expand, y1 = origin.Y + 16f + Expand, z1 = origin.Z + 16f + Expand;

        // Count, per clip plane, how many of the 8 corners violate it. If any plane
        // is violated by all 8 corners, the whole box is on the outside of that
        // plane → not visible.
        int outLeft = 0, outRight = 0, outBottom = 0, outTop = 0, outNear = 0, outFar = 0;
        for (int i = 0; i < 8; i++)
        {
            float cx = (i & 1) == 0 ? x0 : x1;
            float cy = (i & 2) == 0 ? y0 : y1;
            float cz = (i & 4) == 0 ? z0 : z1;

            // Same multiply the GPU performs (see file header): v * (view*proj).
            var clip = Vector4.Transform(new Vector4(cx, cy, cz, 1f), viewProj);

            if (clip.X < -clip.W) outLeft++;
            if (clip.X > clip.W) outRight++;
            if (clip.Y < -clip.W) outBottom++;
            if (clip.Y > clip.W) outTop++;
            if (clip.Z < 0f) outNear++;
            if (clip.Z > clip.W) outFar++;
        }

        if (outLeft == 8 || outRight == 8 || outBottom == 8 ||
            outTop == 8 || outNear == 8 || outFar == 8)
            return false;
        return true;
    }
}

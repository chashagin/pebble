// Chunk-local initial lighting on flat arrays — behavior pinned by the
// frozen baselines (including the frontier-guard lessons:
// this version never propagates outside its own chunk; seam stitching happens
// after adoption). Runs on background queues, no shared state.
//
// Defines static class `LocalLight` (computeLocalLight). LIGHT_OPACITY come from
// BlockCaches; lightEmitOf comes from the block helpers layer.

using System;

namespace PebbleCore;

public static class LocalLight
{
    private static readonly int[] LDX = { 0, 0, 0, 0, -1, 1 };
    private static readonly int[] LDY = { -1, 1, 0, 0, 0, 0 };
    private static readonly int[] LDZ = { 0, 0, -1, 1, 0, 0 };

    public static (byte[] sky, byte[] blk) computeLocalLight(ushort[] blocks, int height, bool hasSky)
    {
        int n = blocks.Length;
        var sky = new byte[n];
        var blk = new byte[n];
        var queue = new int[1 << 15];
        int qTail = 0;
        const int YS = 256;

        void qPush(int idx)
        {
            if (qTail >= queue.Length)
            {
                var grown = new int[queue.Length * 2];
                Array.Copy(queue, grown, queue.Length);
                queue = grown;
            }
            queue[qTail] = idx;
            qTail += 1;
        }

        if (hasSky)
        {
            // vertical fill: 15 falls straight down through transparent blocks
            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int col = z * 16 + x;
                    int level = 15;
                    int y = height - 1;
                    while (y >= 0)
                    {
                        int idx = y * YS + col;
                        int op = LIGHT_OPACITY[blocks[idx] >> 4];
                        if (op > 0) level = Math.Max(0, level - Math.Max(op, 1));
                        else if (level < 15) level = Math.Max(0, level - 1);
                        if (level == 0) break;
                        sky[idx] = (byte)level;
                        y -= 1;
                    }
                }
            }
            // seed lateral spread: lit cells with a dimmer-than-reachable neighbor
            for (int y = 0; y < height; y++)
            {
                int yb = y * YS;
                for (int z = 0; z < 16; z++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int idx = yb + z * 16 + x;
                        int v = sky[idx];
                        if (v <= 1) continue;
                        if ((x > 0 && sky[idx - 1] < v - 1) || (x < 15 && sky[idx + 1] < v - 1) ||
                            (z > 0 && sky[idx - 16] < v - 1) || (z < 15 && sky[idx + 16] < v - 1) ||
                            (y > 0 && sky[idx - YS] < v - 1))
                        {
                            qPush(idx);
                        }
                    }
                }
            }
            bfsLocal(blocks, ref sky, ref queue, qTail, height, true);
        }

        // block light from emitters
        qTail = 0;
        for (int i = 0; i < n; i++)
        {
            ushort c = blocks[i];
            if (c == 0) continue;
            int emit = lightEmitOf(c);
            if (emit > 0)
            {
                blk[i] = (byte)emit;
                qPush(i);
            }
        }
        bfsLocal(blocks, ref blk, ref queue, qTail, height, false);
        return (sky, blk);
    }

    private static void bfsLocal(ushort[] blocks, ref byte[] light, ref int[] queue, int tail, int height, bool isSky)
    {
        const int YS = 256;
        int head = 0;
        int qTail = tail;
        while (head < qTail)
        {
            int idx = queue[head];
            head += 1;
            int v = light[idx];
            if (v <= 1) continue;
            int x = idx & 15;
            int z = (idx >> 4) & 15;
            int y = idx / YS;
            for (int d = 0; d < 6; d++)
            {
                int nx = x + LDX[d], ny = y + LDY[d], nz = z + LDZ[d];
                if (nx < 0 || nx > 15 || nz < 0 || nz > 15 || ny < 0 || ny >= height) continue;
                int nIdx = ny * YS + nz * 16 + nx;
                int op = LIGHT_OPACITY[blocks[nIdx] >> 4];
                int nv;
                if (isSky && d == 0 && v == 15 && op == 0) nv = 15; // skylight falls undiminished
                else nv = v - Math.Max(1, op);
                if (nv <= 0 || light[nIdx] >= nv) continue;
                light[nIdx] = (byte)nv;
                if (qTail >= queue.Length)
                {
                    var grown = new int[queue.Length * 2];
                    Array.Copy(queue, grown, queue.Length);
                    queue = grown;
                }
                queue[qTail] = nIdx;
                qTail += 1;
            }
        }
    }
}

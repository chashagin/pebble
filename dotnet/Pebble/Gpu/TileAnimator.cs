// Backend-agnostic animated-tile driver. Ports WorldRenderer.tickTileAnimations +
// advanceAnimationTick from Sources/Pebble/WorldRenderer.swift: it advances each
// .mcmeta frame animation (water/lava/fire/portal) at 20 Hz and reports which atlas
// array slices changed this frame, with the new res*res*4 RGBA bytes to upload.
//
// The Vulkan and D3D12 backends each own one TileAnimator (built from the same
// PackAtlasResult.animations the atlas was built from) and, every frame, call
// Tick(dtMs) then drain PendingUploads to re-upload the changed slices so the
// water/lava visibly flow. Nothing here touches the GPU.

using System;
using System.Collections.Generic;

namespace Pebble.Gpu;

/// One changed atlas slice: array layer `Slice`, with `Pixels` (res*res*4 RGBA8).
public readonly struct AtlasSliceUpdate
{
    public readonly int Slice;
    public readonly byte[] Pixels;
    public AtlasSliceUpdate(int slice, byte[] pixels) { Slice = slice; Pixels = pixels; }
}

public sealed class TileAnimator
{
    private readonly List<Pebble.TileAnimation> _animations;
    // per-animation (order position, ticks into the current frame)
    private readonly (int pos, int t)[] _state;
    private double _accumMs;
    private byte[]? _scratch;                       // reused interpolation buffer
    private readonly List<AtlasSliceUpdate> _pending = new();

    public bool HasAnimations => _animations.Count > 0;

    public TileAnimator(List<Pebble.TileAnimation> animations)
    {
        _animations = animations ?? new List<Pebble.TileAnimation>();
        _state = new (int, int)[_animations.Count];
    }

    /// Advance the .mcmeta animations at 20 Hz (50 ms / tick), queueing the changed
    /// slices into PendingUploads. Capped at 4 catch-up ticks/frame; a long stall
    /// drops the backlog (matches the Swift original).
    public void Tick(double dtMs)
    {
        if (_animations.Count == 0) return;
        _accumMs += dtMs;
        int steps = 0;
        while (_accumMs >= 50 && steps < 4)
        {
            _accumMs -= 50;
            steps++;
            AdvanceTick();
        }
        if (_accumMs >= 50) _accumMs = 0;   // stalled frame: drop the backlog
    }

    private void AdvanceTick()
    {
        for (int k = 0; k < _animations.Count; k++)
        {
            var anim = _animations[k];
            if (anim.order.Count == 0 || anim.frames.Count == 0) continue;
            var (pos, t) = _state[k];
            t++;
            bool frameChanged = false;
            if (t >= anim.order[pos].ticks)
            {
                t = 0;
                pos = (pos + 1) % anim.order.Count;
                frameChanged = true;
            }
            _state[k] = (pos, t);

            if (anim.interpolate)
            {
                // vanilla-style blend toward the next frame, every tick
                var cur = anim.frames[anim.order[pos].index];
                var nxt = anim.frames[anim.order[(pos + 1) % anim.order.Count].index];
                if (_scratch == null || _scratch.Length != cur.Length)
                    _scratch = new byte[cur.Length];
                double f = (double)t / anim.order[pos].ticks;
                var dst = _scratch;
                for (int i = 0; i < cur.Length; i++)
                    dst[i] = (byte)(cur[i] + (nxt[i] - cur[i]) * f);
                // copy out so the queued bytes are stable until uploaded
                var outPx = new byte[cur.Length];
                Array.Copy(dst, outPx, cur.Length);
                _pending.Add(new AtlasSliceUpdate(anim.slice, outPx));
            }
            else if (frameChanged)
            {
                _pending.Add(new AtlasSliceUpdate(anim.slice, anim.frames[anim.order[pos].index]));
            }
        }
    }

    /// Drain the queued slice uploads (the caller blits them into the atlas this
    /// frame). Returns null when nothing changed.
    public List<AtlasSliceUpdate>? DrainPending()
    {
        if (_pending.Count == 0) return null;
        var list = new List<AtlasSliceUpdate>(_pending);
        _pending.Clear();
        return list;
    }
}

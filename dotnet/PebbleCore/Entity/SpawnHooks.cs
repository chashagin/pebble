// Late-bound mob spawning — baseline keeps spawnMobFn in the frozen baseline and binds
// it from the frozen baseline; Swift centralizes the hook here. SpawnOpts mirrors the
// `data` option-bag accepted by registry.spawnMob.
//
// Defines static class: SpawnHooksGlobals (surface spawnMobFn/bindSpawnMob via
// global using static). Cross-layer symbols (World, Entity) resolve from the rest of
// the assembly.

namespace PebbleCore;

using System;

public struct SpawnOpts
{
    public bool baby = false;
    public int? size = null;
    public bool persistent = false;
    public bool captain = false;
    public int? variant = null;

    public SpawnOpts(bool baby = false, int? size = null, bool persistent = false,
                     bool captain = false, int? variant = null)
    {
        this.baby = baby;
        this.size = size;
        this.persistent = persistent;
        this.captain = captain;
        this.variant = variant;
    }
}

public static class SpawnHooksGlobals
{
    public static Func<World, string, double, double, double, SpawnOpts?, Entity> spawnMobFn;

    public static void bindSpawnMob(Func<World, string, double, double, double, SpawnOpts?, Entity> fn)
    {
        spawnMobFn = fn;
    }
}

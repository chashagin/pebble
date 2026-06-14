// Direction constants — table order is load-bearing (pinned by goldens).
// Ported from Sources/PebbleCore/Core/Dirs.swift.

using System.Collections.Generic;

namespace PebbleCore;

public static class Dirs
{
    public static class Dir
    {
        public const int down = 0;
        public const int up = 1;
        public const int north = 2;
        public const int south = 3;
        public const int west = 4;
        public const int east = 5;
    }

    public static readonly int[] DIR_X = { 0, 0, 0, 0, -1, 1 };
    public static readonly int[] DIR_Y = { -1, 1, 0, 0, 0, 0 };
    public static readonly int[] DIR_Z = { 0, 0, -1, 1, 0, 0 };
    public static readonly int[] DIR_OPPOSITE = { 1, 0, 3, 2, 5, 4 };
    public static readonly int[] HORIZONTALS = { Dir.north, Dir.south, Dir.west, Dir.east };
    public static readonly string[] DIR_NAMES = { "down", "up", "north", "south", "west", "east" };

    /// yaw (degrees) → horizontal dir the entity FACES: 0=south,90=west,180=north,270=east
    public static int yawToDir(double yawDeg)
    {
        double a = (yawDeg % 360 + 360) % 360;
        if (a >= 315 || a < 45) return Dir.south;
        if (a < 135) return Dir.west;
        if (a < 225) return Dir.north;
        return Dir.east;
    }

    public static readonly Dictionary<int, double> DIR_YAW = new()
    {
        { Dir.south, 0 }, { Dir.west, 90 }, { Dir.north, 180 }, { Dir.east, 270 },
    };
}

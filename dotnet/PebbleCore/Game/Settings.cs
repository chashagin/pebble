// Settings — JSON files under ~/Library/Application Support/Pebble/.
// Field names, defaults, and render-distance clamps are frozen; keybinds
// keep internal key-code strings so the app's NSEvent translation layer
// and saved configs stay engine-compatible.
//
// Ported from Sources/PebbleCore/Game/Settings.swift.
// Defines static class `SettingsGlobals` (DEFAULT_KEYBINDS, defaultSettings,
// vcSupportDir, loadSettings, saveSettings, loadKeybinds, saveKeybinds).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PebbleCore;

public struct Settings
{
    // video
    public int renderDistance;
    public int fov;
    public bool fancyGraphics;
    public bool smoothLighting;
    public bool bloom;
    public bool shadows;
    public bool clouds;
    public int particles;        // 0 minimal 1 decreased 2 all
    public double gamma;          // 0..1
    public bool viewBobbing;
    public int guiScale;         // 0 auto
    public int maxFps;           // 250 = unlimited/vsync-off; opt-in, not the default
    public double entityDistance;
    // audio
    public Dictionary<string, double> volumes;
    // controls
    public double sensitivity;    // 0..1
    public bool invertY;
    // accessibility
    public bool subtitles;
    public bool autoJump;
    public bool reduceMotion;
    public bool reducedFlashes;
    public bool highContrast;
    public double darknessPulse;
    /// per-block quads instead of greedy-merged spans (GPU driver workaround)
    public bool simpleMesh;
    /// enabled resource pack file names, index 0 = highest priority.
    /// optional so settings.json files written before this field still decode
    public List<string> resourcePacks;
    /// nil = off, "ultra" = built-in ultra preset, anything else = shader pack file name
    public string shader;

    public Settings()
    {
        renderDistance = 8;
        fov = 70;
        fancyGraphics = true;
        smoothLighting = true;
        bloom = true;
        shadows = true;
        clouds = true;
        particles = 2;
        gamma = 0.5;
        viewBobbing = true;
        guiScale = 0;
        maxFps = 120;
        entityDistance = 64.0;
        volumes = new Dictionary<string, double>
        {
            { "master", 0.8 }, { "music", 0.5 }, { "blocks", 1 }, { "hostile", 1 }, { "friendly", 1 },
            { "players", 1 }, { "ambient", 1 }, { "records", 1 }, { "ui", 1 },
        };
        sensitivity = 0.5;
        invertY = false;
        subtitles = false;
        autoJump = false;
        reduceMotion = false;
        reducedFlashes = false;
        highContrast = false;
        darknessPulse = 1.0;
        simpleMesh = false;
        resourcePacks = null;
        shader = null;
    }
}

public static class SettingsGlobals
{
    public static readonly Dictionary<string, string> DEFAULT_KEYBINDS = new Dictionary<string, string>
    {
        { "forward", "KeyW" },
        { "back", "KeyS" },
        { "left", "KeyA" },
        { "right", "KeyD" },
        { "jump", "Space" },
        { "sneak", "ShiftLeft" },
        { "sprint", "ControlLeft" },
        { "inventory", "KeyE" },
        { "drop", "KeyQ" },
        { "chat", "KeyT" },
        { "command", "Slash" },
        { "perspective", "F5" },
        { "swapOffhand", "KeyF" },
    };

    public static Settings defaultSettings() { return new Settings(); }

    /// ~/Library/Application Support/Pebble — created on first touch.
    /// PEBBLE_SUPPORT_DIR overrides the location (used by headless tests so they
    /// write to a scratch DB instead of the player's real saves). Unset → the
    /// normal per-user path, identical to before.
    public static string vcSupportDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("PEBBLE_SUPPORT_DIR");
        string dir;
        if (!string.IsNullOrEmpty(overrideDir))
        {
            dir = overrideDir;
        }
        else
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            dir = Path.Combine(baseDir, "Pebble");
        }
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    private static string settingsURL => Path.Combine(vcSupportDir(), "settings.json");
    private static string keybindsURL => Path.Combine(vcSupportDir(), "keybinds.json");

    private static readonly JsonSerializerOptions decodeOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
    };

    private static readonly JsonSerializerOptions encodeOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        WriteIndented = true,
        // .sortedKeys — emit object keys in alphabetical order to match Swift's
        // JSONEncoder output (frozen on-disk layout).
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings loadSettings()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(settingsURL))
            {
                var data = File.ReadAllBytes(settingsURL);
                var saved = JsonSerializer.Deserialize<Settings>(data, decodeOptions);
                s = saved;
                // merge any volume categories added since the file was written
                if (s.volumes == null) s.volumes = new Dictionary<string, double>();
                foreach (var kv in new Settings().volumes)
                {
                    if (!s.volumes.ContainsKey(kv.Key)) s.volumes[kv.Key] = kv.Value;
                }
            }
        }
        catch { }
        // hard ceiling: above 16 the full-height chunk arrays + mesh pipeline
        // dominate memory; floor of 4 keeps the world visible
        s.renderDistance = Math.Min(16, Math.Max(4, s.renderDistance));
        return s;
    }

    public static void saveSettings(Settings s)
    {
        try
        {
            var data = SerializeSorted(s);
            File.WriteAllBytes(settingsURL, data);
        }
        catch { }
    }

    public static Dictionary<string, string> loadKeybinds()
    {
        var binds = new Dictionary<string, string>(DEFAULT_KEYBINDS);
        try
        {
            if (File.Exists(keybindsURL))
            {
                var data = File.ReadAllBytes(keybindsURL);
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(data, decodeOptions);
                if (saved != null)
                {
                    foreach (var kv in saved) binds[kv.Key] = kv.Value;
                }
            }
        }
        catch { }
        return binds;
    }

    public static void saveKeybinds(Dictionary<string, string> binds)
    {
        try
        {
            var data = SerializeSorted(binds);
            File.WriteAllBytes(keybindsURL, data);
        }
        catch { }
    }

    // Mirror Swift's JSONEncoder `.sortedKeys` + `.prettyPrinted`: object keys are
    // emitted in (UTF-8 byte) ascending order, including nested dictionaries.
    private static byte[] SerializeSorted<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, encodeOptions);
        var sorted = SortKeys(node);
        using var stream = new MemoryStream();
        var writerOptions = new JsonWriterOptions { Indented = true };
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, writerOptions))
        {
            if (sorted == null) writer.WriteNullValue();
            else sorted.WriteTo(writer);
        }
        return stream.ToArray();
    }

    private static System.Text.Json.Nodes.JsonNode SortKeys(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var keys = new List<string>();
            foreach (var kv in obj) keys.Add(kv.Key);
            keys.Sort(StringComparer.Ordinal);
            var result = new System.Text.Json.Nodes.JsonObject();
            foreach (var k in keys)
            {
                var child = obj[k];
                result[k] = child == null ? null : SortKeys(child);
            }
            return result;
        }
        if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            var result = new System.Text.Json.Nodes.JsonArray();
            var items = new List<System.Text.Json.Nodes.JsonNode>();
            foreach (var item in arr) items.Add(item);
            arr.Clear();
            foreach (var item in items) result.Add(item == null ? null : SortKeys(item));
            return result;
        }
        return node;
    }
}

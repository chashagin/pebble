// Persistence — a single SQLite database at
// ~/Library/Application Support/Pebble/pebble.db (WAL mode, fully mutexed):
//   worlds(id, json, lastPlayed)            — world metadata + global state
//   chunks(world, dim, cx, cz, data BLOB)   — modified chunks (VCK1 binary)
//   player(world, json)                     — player snapshot per world
//   advancements(world, json)               — earned advancement ids per world
// Legacy installs stored loose files under saves/; they are imported once on
// first open and the old folder is kept as saves-legacy-backup. Chunk records
// keep the VCK1 container (binary blocks + JSON tail); entity-only records
// regenerate terrain from seed and re-attach saved entities.
//
// Ported from Sources/PebbleCore/Game/Saves.swift.
// Defines static class `SavesGlobals` (PEBBLE_VERSION and the sanitizeJSON helper).
// vcSupportDir() comes from SettingsGlobals; blockDefs from Reg — both surfaced
// via global using static. Uses Microsoft.Data.Sqlite (WAL, FULLMUTEX via shared
// connection) and System.Text.Json for the JSON encode/decode paths.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PebbleCore;

public struct DimState
{
    public int time;
    public int dayTime;
    public bool raining;
    public bool thundering;
    public int weatherTimer;

    public DimState(int time = 0, int dayTime = 1000, bool raining = false,
                    bool thundering = false, int weatherTimer = 24000)
    {
        this.time = time;
        this.dayTime = dayTime;
        this.raining = raining;
        this.thundering = thundering;
        this.weatherTimer = weatherTimer;
    }
}

/// WorldMeta + the global-state extension (baseline WorldRecord extends WorldMeta)
public struct WorldRecord
{
    public string id;
    public string name;
    public int seed;             // Swift Int32
    public int gameMode;
    public int difficulty;
    public double lastPlayed;     // ms epoch, like Date.now()
    public string version;
    /// keyed by dim rawValue as a string — Swift encodes [Int:] dicts as JSON
    /// arrays, and the record should read as `{"0": {...}, "1": {...}}` on disk
    public Dictionary<string, DimState> dims;
    public int spawnX;
    public int spawnY;
    public int spawnZ;
    public Dictionary<string, double> gameRules;
    public bool dragonKilled;
    public int gatewaysSpawned;
    public int nextEntityId;

    public WorldRecord(string id, string name, int seed, int gameMode, int difficulty)
    {
        this.id = id;
        this.name = name;
        this.seed = seed;
        this.gameMode = gameMode;
        this.difficulty = difficulty;
        lastPlayed = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1.0;
        version = $"pebble-{SavesGlobals.PEBBLE_VERSION}";
        dims = new Dictionary<string, DimState>
        {
            { "0", new DimState() }, { "1", new DimState() }, { "2", new DimState() },
        };
        spawnX = 0;
        spawnY = 80;
        spawnZ = 0;
        gameRules = new Dictionary<string, double>();
        dragonKilled = false;
        gatewaysSpawned = 0;
        nextEntityId = 1;
    }
}

public struct ChunkRecord
{
    public string key;
    public string worldId;
    public int dim;
    public int cx;
    public int cz;
    /// absent on entity-only records: the chunk itself regenerates from seed
    public ushort[] blocks;
    public byte[] biomes;
    public List<BlockEntityData> blockEntities;
    public List<Dictionary<string, object>> entities;

    public ChunkRecord(string key, string worldId, int dim, int cx, int cz,
                       ushort[] blocks = null, byte[] biomes = null,
                       List<BlockEntityData> blockEntities = null,
                       List<Dictionary<string, object>> entities = null)
    {
        this.key = key;
        this.worldId = worldId;
        this.dim = dim;
        this.cx = cx;
        this.cz = cz;
        this.blocks = blocks;
        this.biomes = biomes;
        this.blockEntities = blockEntities;
        this.entities = entities ?? new List<Dictionary<string, object>>();
    }
}

public static class SavesGlobals
{
    /// single source of truth for the app version — the title screen, the F3
    /// overlay and save records all read this (Info.plist is bumped separately
    /// at packaging time)
    public const string PEBBLE_VERSION = "1.0.0";

    /// JSON can't carry NaN/Infinity (structured clone could) — scrub them so one
    /// blown-up velocity never poisons a whole chunk record
    public static object sanitizeJSON(object v)
    {
        if (v is double d) { return double.IsFinite(d) ? d : (object)0.0; }
        if (v is float f) { return double.IsFinite(f) ? (double)f : (object)0.0; }
        if (v is List<object> arr)
        {
            var outArr = new List<object>(arr.Count);
            foreach (var e in arr) outArr.Add(sanitizeJSON(e));
            return outArr;
        }
        if (v is Dictionary<string, object> dict)
        {
            var outDict = new Dictionary<string, object>(dict.Count);
            foreach (var kv in dict) outDict[kv.Key] = sanitizeJSON(kv.Value);
            return outDict;
        }
        return v;
    }
}

public sealed class SaveDB : IDisposable
{
    private SqliteConnection db;

    public SaveDB()
    {
        var url = Path.Combine(vcSupportDir(), "pebble.db");
        // SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX:
        // ReadWriteCreate on a single serialized (full-mutex) handle. SQLite's
        // default threading mode (serialized) already mutexes this lone handle.
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = url,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        db = new SqliteConnection(csb.ToString());
        try
        {
            db.Open();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"pebble.db could not be opened: {e.Message}");
        }
        exec("PRAGMA journal_mode=WAL");
        exec("PRAGMA synchronous=NORMAL");
        exec("PRAGMA busy_timeout=5000");
        exec(@"
        CREATE TABLE IF NOT EXISTS worlds(
            id TEXT PRIMARY KEY, json TEXT NOT NULL, lastPlayed REAL NOT NULL DEFAULT 0)
        ");
        exec(@"
        CREATE TABLE IF NOT EXISTS chunks(
            world TEXT NOT NULL, dim INTEGER NOT NULL, cx INTEGER NOT NULL, cz INTEGER NOT NULL,
            data BLOB NOT NULL, PRIMARY KEY(world, dim, cx, cz)) WITHOUT ROWID
        ");
        exec("CREATE TABLE IF NOT EXISTS player(world TEXT PRIMARY KEY, json TEXT NOT NULL)");
        exec("CREATE TABLE IF NOT EXISTS advancements(world TEXT PRIMARY KEY, json TEXT NOT NULL)");
        migrateLegacySaves();
    }

    public void Dispose()
    {
        db?.Close();
        db?.Dispose();
        db = null;
    }

    // Codable parity: Swift's JSONEncoder/JSONDecoder. IncludeFields so struct
    // public fields encode; nil optionals are dropped (encodeIfPresent parity).
    private static readonly JsonSerializerOptions codableEncode = new JsonSerializerOptions
    {
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions codableDecode = new JsonSerializerOptions
    {
        IncludeFields = true,
    };
    // JSONSerialization parity: emits NSNull as `null` (never omits keys). Used for
    // the chunk tail / player object graphs that mirror Foundation's loose JSON.
    private static readonly JsonSerializerOptions plainJson = new JsonSerializerOptions
    {
        IncludeFields = true,
    };

    // ---- tiny statement helpers -------------------------------------------------
    private bool exec(string sql)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[saves] exec failed: {e.Message} — {Prefix(sql, 60)}");
            return false;
        }
    }

    /// prepare + bind + step a statement; row() is called once per result row.
    /// returns false (and logs) on prepare/step errors — a silently failed
    /// write (disk full, SQLITE_ERROR) is data loss
    private bool run(string sql, Action<SqliteCommand> bind = null, Action<SqliteDataReader> row = null)
    {
        SqliteCommand cmd = null;
        try
        {
            cmd = db.CreateCommand();
            cmd.CommandText = sql;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[saves] prepare failed: {e.Message} — {Prefix(sql, 60)}");
            cmd?.Dispose();
            return false;
        }
        try
        {
            bind?.Invoke(cmd);
            if (row != null)
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) { row(reader); }
            }
            else
            {
                cmd.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[saves] step failed: {e.Message} — {Prefix(sql, 60)}");
            return false;
        }
        finally
        {
            cmd.Dispose();
        }
    }

    private static string Prefix(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    private void bindText(SqliteCommand cmd, int idx, string s)
    {
        cmd.Parameters.AddWithValue($"@p{idx}", (object)s ?? DBNull.Value);
    }
    private void bindInt(SqliteCommand cmd, int idx, int v)
    {
        cmd.Parameters.AddWithValue($"@p{idx}", v);
    }
    private void bindDouble(SqliteCommand cmd, int idx, double v)
    {
        cmd.Parameters.AddWithValue($"@p{idx}", v);
    }
    private void bindBlob(SqliteCommand cmd, int idx, byte[] v)
    {
        cmd.Parameters.AddWithValue($"@p{idx}", (object)v ?? DBNull.Value);
    }
    private string columnText(SqliteDataReader reader, int idx)
    {
        return reader.IsDBNull(idx) ? null : reader.GetString(idx);
    }

    // ---- worlds ---------------------------------------------------------------
    public List<WorldRecord> listWorlds()
    {
        var outList = new List<WorldRecord>();
        run("SELECT json FROM worlds", row: reader =>
        {
            var json = columnText(reader, 0);
            if (json != null && tryDecode<WorldRecord>(json, out var rec))
            {
                outList.Add(rec);
            }
        });
        return outList;
    }
    public WorldRecord? getWorld(string id)
    {
        WorldRecord? rec = null;
        run("SELECT json FROM worlds WHERE id=@p1", bind: cmd => bindText(cmd, 1, id), row: reader =>
        {
            var json = columnText(reader, 0);
            if (json != null && tryDecode<WorldRecord>(json, out var r)) { rec = r; }
        });
        return rec;
    }
    public void putWorld(WorldRecord rec)
    {
        string json;
        try { json = JsonSerializer.Serialize(rec, codableEncode); }
        catch { return; }
        if (json == null) return;
        run("INSERT OR REPLACE INTO worlds(id, json, lastPlayed) VALUES(@p1,@p2,@p3)", bind: cmd =>
        {
            bindText(cmd, 1, rec.id);
            bindText(cmd, 2, json);
            bindDouble(cmd, 3, rec.lastPlayed);
        });
    }
    public void deleteWorld(string id)
    {
        exec("BEGIN");
        foreach (var table in new[] { "worlds", "chunks", "player", "advancements" })
        {
            var col = table == "worlds" ? "id" : "world";
            run($"DELETE FROM {table} WHERE {col}=@p1", bind: cmd => bindText(cmd, 1, id));
        }
        exec("COMMIT");
    }

    // ---- chunks ---------------------------------------------------------------
    public string chunkKey(string worldId, int dim, int cx, int cz)
    {
        return $"{worldId}:{dim}:{cx},{cz}";
    }

    /// all saved chunk keys for a world — lets the streamer skip the DB for fresh chunks
    public HashSet<string> getChunkKeys(string worldId)
    {
        var keys = new HashSet<string>();
        run("SELECT dim, cx, cz FROM chunks WHERE world=@p1", bind: cmd => bindText(cmd, 1, worldId), row: reader =>
        {
            int dim = reader.GetInt32(0);
            int cx = reader.GetInt32(1);
            int cz = reader.GetInt32(2);
            keys.Add(chunkKey(worldId, dim, cx, cz));
        });
        return keys;
    }

    public ChunkRecord? getChunk(string worldId, int dim, int cx, int cz)
    {
        ChunkRecord? rec = null;
        run("SELECT data FROM chunks WHERE world=@p1 AND dim=@p2 AND cx=@p3 AND cz=@p4", bind: cmd =>
        {
            bindText(cmd, 1, worldId);
            bindInt(cmd, 2, dim);
            bindInt(cmd, 3, cx);
            bindInt(cmd, 4, cz);
        }, row: reader =>
        {
            if (!reader.IsDBNull(0))
            {
                var data = (byte[])reader.GetValue(0);
                rec = decodeChunk(data, chunkKey(worldId, dim, cx, cz), worldId, dim, cx, cz);
            }
        });
        return rec;
    }

    /// batch write — one transaction, mirrors the once-per-second save tick.
    /// false = the batch did not land (rolled back); callers must re-mark the
    /// chunks dirty or the edits are silently lost
    public bool putChunks(List<ChunkRecord> records)
    {
        if (records.Count == 0) { return true; }
        if (!exec("BEGIN")) { return false; }
        bool ok = true;
        foreach (var r in records)
        {
            var data = encodeChunk(r);
            if (data == null) { ok = false; continue; }
            var wrote = run("INSERT OR REPLACE INTO chunks(world, dim, cx, cz, data) VALUES(@p1,@p2,@p3,@p4,@p5)", bind: cmd =>
            {
                bindText(cmd, 1, r.worldId);
                bindInt(cmd, 2, r.dim);
                bindInt(cmd, 3, r.cx);
                bindInt(cmd, 4, r.cz);
                bindBlob(cmd, 5, data);
            });
            ok = ok && wrote;
        }
        if (ok)
        {
            ok = exec("COMMIT");
        }
        else
        {
            exec("ROLLBACK");
        }
        return ok;
    }

    // binary container: "VCK1" | u8 flags | [u32 nBlocks, u16[] LE, u32 nBiomes, u8[]] | u32 jsonLen, json
    private byte[] encodeChunk(ChunkRecord r)
    {
        var data = new List<byte>();
        data.AddRange(Encoding.UTF8.GetBytes("VCK1"));
        bool hasBlocks = r.blocks != null && r.biomes != null;
        data.Add((byte)(hasBlocks ? 1 : 0));
        void putU32(int v)
        {
            uint le = unchecked((uint)v);
            data.Add((byte)(le & 0xFF));
            data.Add((byte)((le >> 8) & 0xFF));
            data.Add((byte)((le >> 16) & 0xFF));
            data.Add((byte)((le >> 24) & 0xFF));
        }
        if (hasBlocks)
        {
            var blocks = r.blocks; var biomes = r.biomes;
            putU32(blocks.Length);
            // host LE on all Apple silicon/x86 — emit u16[] as little-endian bytes
            foreach (var b in blocks)
            {
                data.Add((byte)(b & 0xFF));
                data.Add((byte)((b >> 8) & 0xFF));
            }
            putU32(biomes.Length);
            data.AddRange(biomes);
        }
        var tail = new Dictionary<string, object>();
        var sanitizedEntities = new List<object>(r.entities.Count);
        foreach (var e in r.entities) { sanitizedEntities.Add(SavesGlobals.sanitizeJSON(e)); }
        tail["entities"] = sanitizedEntities;
        if (r.blockEntities != null)
        {
            try
            {
                byte[] enc = JsonSerializer.SerializeToUtf8Bytes(r.blockEntities, codableEncode);
                object obj = JsonSerializer.Deserialize<object>(enc);
                if (obj != null) { tail["blockEntities"] = obj; }
            }
            catch { }
        }
        byte[] json;
        try { json = JsonSerializer.SerializeToUtf8Bytes(tail, plainJson); }
        catch { return null; }
        putU32(json.Length);
        data.AddRange(json);
        return data.ToArray();
    }

    private ChunkRecord? decodeChunk(byte[] data, string key, string worldId, int dim, int cx, int cz)
    {
        var rec = new ChunkRecord(key, worldId, dim, cx, cz);
        // Swift's `off` is a 64-bit Int and readU32() returns Int(UInt32(...)) —
        // always the non-negative full 32-bit value. Use long so corrupt blobs
        // with huge lengths bounds-check identically (no signed-int overflow).
        long off = 0;
        long? readU32()
        {
            if (off + 4 > data.Length) { return null; }
            uint v = (uint)data[off]
                   | ((uint)data[off + 1] << 8)
                   | ((uint)data[off + 2] << 16)
                   | ((uint)data[off + 3] << 24);
            off += 4;
            return (long)v;
        }
        if (data.Length < 5 || !PrefixEquals(data, "VCK1")) { return null; }
        off = 4;
        byte flags = data[off]; off += 1;
        if ((flags & 1) != 0)
        {
            var nBlocksOpt = readU32();
            if (nBlocksOpt == null) { return null; }
            long nBlocks = nBlocksOpt.Value;
            if (off + nBlocks * 2 > data.Length) { return null; }
            var blocks = new ushort[nBlocks];
            for (int i = 0; i < nBlocks; i++)
            {
                blocks[i] = (ushort)((uint)data[off + i * 2] | ((uint)data[off + i * 2 + 1] << 8));
            }
            off += nBlocks * 2;
            // clamp corrupted ids — blockDefs[cell >> 4] is indexed unchecked
            // in hot paths, and one bad blob must not crash the game
            ushort maxId = (ushort)blockDefs.Count;
            for (int i = 0; i < blocks.Length; i++)
            {
                if ((blocks[i] >> 4) >= maxId) { blocks[i] = 0; }
            }
            rec.blocks = blocks;
            var nBiomesOpt = readU32();
            if (nBiomesOpt == null) { return null; }
            long nBiomes = nBiomesOpt.Value;
            if (off + nBiomes > data.Length) { return null; }
            var biomes = new byte[nBiomes];
            Array.Copy(data, off, biomes, 0, nBiomes);
            rec.biomes = biomes;
            off += nBiomes;
        }
        var jsonLenOpt = readU32();
        if (jsonLenOpt == null) { return null; }
        long jsonLen = jsonLenOpt.Value;
        if (off + jsonLen > data.Length) { return null; }
        Dictionary<string, object> tail;
        try
        {
            var span = new ReadOnlySpan<byte>(data, (int)off, (int)jsonLen);
            var node = JsonNode.Parse(span.ToArray());
            tail = (node is JsonObject) ? jsonNodeToDict((JsonObject)node) : null;
        }
        catch { tail = null; }
        if (tail == null) { return null; }
        rec.entities = (tryGet(tail, "entities") is List<object> ents)
            ? entityListFrom(ents)
            : new List<Dictionary<string, object>>();
        var rawBE = tryGet(tail, "blockEntities");
        if (rawBE != null)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(rawBE);
                var bes = JsonSerializer.Deserialize<List<BlockEntityData>>(bytes, codableDecode);
                if (bes != null) { rec.blockEntities = bes; }
            }
            catch { }
        }
        return rec;
    }

    // ---- player / advancements --------------------------------------------------
    public Dictionary<string, object> getPlayer(string worldId)
    {
        Dictionary<string, object> outDict = null;
        run("SELECT json FROM player WHERE world=@p1", bind: cmd => bindText(cmd, 1, worldId), row: reader =>
        {
            var json = columnText(reader, 0);
            if (json != null)
            {
                try
                {
                    var node = JsonNode.Parse(json);
                    outDict = (node is JsonObject) ? jsonNodeToDict((JsonObject)node) : null;
                }
                catch { outDict = null; }
            }
        });
        return outDict;
    }
    public void putPlayer(string worldId, Dictionary<string, object> data)
    {
        byte[] bytes;
        try
        {
            var sanitized = SavesGlobals.sanitizeJSON(data);
            bytes = JsonSerializer.SerializeToUtf8Bytes(sanitized);
        }
        catch { return; }
        string json;
        try { json = Encoding.UTF8.GetString(bytes); } catch { return; }
        run("INSERT OR REPLACE INTO player(world, json) VALUES(@p1,@p2)", bind: cmd =>
        {
            bindText(cmd, 1, worldId);
            bindText(cmd, 2, json);
        });
    }
    public List<string> getAdvancements(string worldId)
    {
        List<string> outList = null;
        run("SELECT json FROM advancements WHERE world=@p1", bind: cmd => bindText(cmd, 1, worldId), row: reader =>
        {
            var json = columnText(reader, 0);
            if (json != null)
            {
                try
                {
                    var node = JsonNode.Parse(json);
                    if (node is JsonArray arr)
                    {
                        var list = new List<string>();
                        bool ok = true;
                        foreach (var el in arr)
                        {
                            if (el is JsonValue jv && jv.TryGetValue<string>(out var s)) { list.Add(s); }
                            else { ok = false; break; }
                        }
                        outList = ok ? list : null;
                    }
                }
                catch { outList = null; }
            }
        });
        return outList;
    }
    public void putAdvancements(string worldId, List<string> ids)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(ids); }
        catch { return; }
        string json;
        try { json = Encoding.UTF8.GetString(bytes); } catch { return; }
        run("INSERT OR REPLACE INTO advancements(world, json) VALUES(@p1,@p2)", bind: cmd =>
        {
            bindText(cmd, 1, worldId);
            bindText(cmd, 2, json);
        });
    }

    // ---- legacy import ----------------------------------------------------------
    /// one-time import of the pre-1.0 loose-file layout (saves/worlds/*.json,
    /// saves/chunks/<id>/*.vck, …); the old folder is renamed, never deleted
    private void migrateLegacySaves()
    {
        var legacy = Path.Combine(vcSupportDir(), "saves");
        var worldsDir = Path.Combine(legacy, "worlds");
        if (!Directory.Exists(worldsDir)) { return; }
        string[] files;
        try { files = Directory.GetFiles(worldsDir); }
        catch { return; }
        if (files.Length == 0) { return; }

        int worlds = 0, chunks = 0;
        foreach (var f in files)
        {
            if (Path.GetExtension(f) != ".json") { continue; }
            WorldRecord rec;
            try
            {
                var bytes = File.ReadAllBytes(f);
                rec = JsonSerializer.Deserialize<WorldRecord>(bytes, codableDecode);
            }
            catch { continue; }
            putWorld(rec);
            worlds += 1;
            var id = rec.id;
            try
            {
                var ppath = Path.Combine(legacy, "player", $"{id}.json");
                if (File.Exists(ppath))
                {
                    var pdata = File.ReadAllBytes(ppath);
                    var node = JsonNode.Parse(pdata);
                    if (node is JsonObject pobj) { putPlayer(id, jsonNodeToDict(pobj)); }
                }
            }
            catch { }
            try
            {
                var apath = Path.Combine(legacy, "advancements", $"{id}.json");
                if (File.Exists(apath))
                {
                    var adata = File.ReadAllBytes(apath);
                    var node = JsonNode.Parse(adata);
                    if (node is JsonArray aarr)
                    {
                        var list = new List<string>();
                        bool ok = true;
                        foreach (var el in aarr)
                        {
                            if (el is JsonValue jv && jv.TryGetValue<string>(out var s)) { list.Add(s); }
                            else { ok = false; break; }
                        }
                        if (ok) { putAdvancements(id, list); }
                    }
                }
            }
            catch { }
            var cdir = Path.Combine(legacy, "chunks", id);
            string[] cfiles;
            try { cfiles = Directory.GetFiles(cdir); }
            catch { continue; }
            exec("BEGIN");
            foreach (var cf in cfiles)
            {
                if (Path.GetExtension(cf) != ".vck") { continue; }
                var parts = Path.GetFileNameWithoutExtension(cf).Split('_');
                if (parts.Length != 3) { continue; }
                if (!int.TryParse(parts[0], out var dim)) { continue; }
                if (!int.TryParse(parts[1], out var cx)) { continue; }
                if (!int.TryParse(parts[2], out var cz)) { continue; }
                byte[] cdata;
                try { cdata = File.ReadAllBytes(cf); }
                catch { continue; }
                run("INSERT OR REPLACE INTO chunks(world, dim, cx, cz, data) VALUES(@p1,@p2,@p3,@p4,@p5)", bind: cmd =>
                {
                    bindText(cmd, 1, id);
                    bindInt(cmd, 2, dim);
                    bindInt(cmd, 3, cx);
                    bindInt(cmd, 4, cz);
                    bindBlob(cmd, 5, cdata);
                });
                chunks += 1;
            }
            exec("COMMIT");
        }
        var backup = Path.Combine(vcSupportDir(), "saves-legacy-backup");
        try { Directory.Move(legacy, backup); } catch { }
        Console.WriteLine($"[saves] migrated {worlds} worlds, {chunks} chunks into pebble.db (old files kept in saves-legacy-backup)");
        Console.Out.Flush();
    }

    // ---- JSON helpers -----------------------------------------------------------
    private static bool tryDecode<T>(string json, out T value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(Encoding.UTF8.GetBytes(json), codableDecode);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private static bool PrefixEquals(byte[] data, string magic)
    {
        var m = Encoding.UTF8.GetBytes(magic);
        if (data.Length < m.Length) { return false; }
        for (int i = 0; i < m.Length; i++) { if (data[i] != m[i]) { return false; } }
        return true;
    }

    private static object tryGet(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v : null;

    private static List<Dictionary<string, object>> entityListFrom(List<object> ents)
    {
        var outList = new List<Dictionary<string, object>>(ents.Count);
        foreach (var e in ents)
        {
            if (e is Dictionary<string, object> d) { outList.Add(d); }
        }
        return outList;
    }

    // JSON tree → plain CLR object graph (Dictionary<string,object> / List<object> /
    // string / double / bool / null) mirroring Foundation's JSONSerialization output.
    private static Dictionary<string, object> jsonNodeToDict(JsonObject obj)
    {
        var d = new Dictionary<string, object>(obj.Count);
        foreach (var kv in obj) { d[kv.Key] = jsonNodeToObject(kv.Value); }
        return d;
    }

    private static object jsonNodeToObject(JsonNode node)
    {
        if (node == null) { return null; }
        if (node is JsonObject obj) { return jsonNodeToDict(obj); }
        if (node is JsonArray arr)
        {
            var list = new List<object>(arr.Count);
            foreach (var el in arr) { list.Add(jsonNodeToObject(el)); }
            return list;
        }
        if (node is JsonValue val)
        {
            var je = val.GetValue<JsonElement>();
            switch (je.ValueKind)
            {
                case JsonValueKind.String: return je.GetString();
                case JsonValueKind.Number:
                    // preserve integer-ness like Foundation's NSNumber so a
                    // round-trip doesn't turn `5` into `5.0` in the on-disk JSON
                    if (je.TryGetInt64(out var l)) { return l; }
                    return je.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                default: return null;
            }
        }
        return null;
    }
}

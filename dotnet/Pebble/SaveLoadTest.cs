// Headless SAVE -> LOAD round-trip test for block entities. Reproduces the gap
// that the earlier verification missed: createWorld + `--screen game` never
// exercised loadWorld, so a saved chunk with block entities (chest/furnace) was
// never read back. A bug in the save deep-copy used to gut every BlockEntityData
// to all-nulls (type==null), which then crashed adoptChunkBlockEntities on load.
//
// This test:
//   1. creates a world, places a chest + a furnace, fills the chest with 17
//      diamonds and the furnace with fuel + an ingredient,
//   2. saveAndFlush(synchronous) so the chunk is on disk,
//   3. constructs a FRESH GameCore and loadWorld(thatId) (the real title ->
//      Singleplayer -> click-world path) — this must NOT throw,
//   4. asserts the chest BE is back at its position with type=="container" and
//      its 17 diamonds intact, and the furnace BE survived with its items.
// Prints PASS/FAIL and returns 0/1. Cleans up the test world from the DB.

using System;
using System.IO;
using PebbleCore;

namespace Pebble;

internal static class SaveLoadTest
{
    public static int Run()
    {
        // Isolate this test in a scratch save dir so it never touches the player's
        // real worlds (and so repeated runs can't stress the live DB). The engine's
        // vcSupportDir() honours PEBBLE_SUPPORT_DIR.
        var scratch = Path.Combine(Path.GetTempPath(), "PebbleSaveLoadTest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PEBBLE_SUPPORT_DIR", scratch);
        Console.WriteLine("[saveloadtest] scratch save dir: " + scratch);

        int fails = 0;
        void check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok ? "" : "  " + detail));
            if (!ok) fails++;
        }

        string worldId = "";
        int chestX = 0, chestY = 0, chestZ = 0;
        int furnX = 0, furnY = 0, furnZ = 0;

        try
        {
            Console.WriteLine("[saveloadtest] === phase 1: create + place + save ===");
            var g1 = new GameCore();
            worldId = PlaceWorld(g1, out chestX, out chestY, out chestZ, out furnX, out furnY, out furnZ);
            if (worldId == null) { Console.WriteLine("[saveloadtest] createWorld produced no world record"); return 1; }
            Console.WriteLine($"[saveloadtest] world={worldId} chest@({chestX},{chestY},{chestZ}) furnace@({furnX},{furnY},{furnZ})");
            g1.saveAndFlush(synchronous: true);
            Console.WriteLine("[saveloadtest] saved + flushed.");
            g1.db.Dispose();   // release the SQLite handle before the fresh core opens it
        }
        catch (Exception e)
        {
            Console.WriteLine("[saveloadtest] phase 1 threw: " + e);
            return 1;
        }

        try
        {
            Console.WriteLine("[saveloadtest] === phase 2: FRESH core, loadWorld ===");
            var g2 = new GameCore();
            // This is the exact crash path: loadWorld -> enterWorld ->
            // ensureChunksLoaded -> adoptChunk -> adoptChunkBlockEntities.
            g2.loadWorld(worldId);
            check("loadWorld did not throw + entered world", g2.hasWorld());

            var w2 = g2.world;
            var chestBE = w2.getBlockEntity(chestX, chestY, chestZ);
            check("chest BE exists after load", chestBE != null,
                  $"(none at {chestX},{chestY},{chestZ})");
            if (chestBE != null)
            {
                check("chest BE type == container", chestBE.type == "container",
                      $"(type={chestBE.type ?? "<null>"})");
                bool itemsOk = chestBE.items != null && chestBE.items.Length >= 1
                    && chestBE.items[0] != null
                    && chestBE.items[0].id == ItemGlobals.iid("diamond")
                    && chestBE.items[0].count == 17;
                check("chest holds 17 diamonds in slot 0", itemsOk,
                      chestBE.items?[0] == null ? "(slot 0 empty)"
                        : $"(id={chestBE.items[0].id} want {ItemGlobals.iid("diamond")}, count={chestBE.items[0].count})");
            }

            var furnBE = w2.getBlockEntity(furnX, furnY, furnZ);
            check("furnace BE exists after load", furnBE != null,
                  $"(none at {furnX},{furnY},{furnZ})");
            if (furnBE != null)
            {
                check("furnace BE type == furnace", furnBE.type == "furnace",
                      $"(type={furnBE.type ?? "<null>"})");
                bool fuelOk = furnBE.items != null && furnBE.items.Length >= 2
                    && furnBE.items[1] != null
                    && furnBE.items[1].id == ItemGlobals.iid("coal")
                    && furnBE.items[1].count == 8;
                check("furnace fuel slot holds 8 coal", fuelOk,
                      furnBE.items?[1] == null ? "(fuel slot empty)"
                        : $"(id={furnBE.items[1].id} count={furnBE.items[1].count})");
                check("furnace burnTime survived", furnBE.burnTime == 100,
                      $"(burnTime={furnBE.burnTime})");
            }

            // tidy up: remove the throwaway world from the shared DB.
            try { g2.deleteWorld(worldId); } catch { }
            g2.db.Dispose();
        }
        catch (Exception e)
        {
            Console.WriteLine("[saveloadtest] phase 2 threw (THIS IS THE CRASH): " + e);
            return 1;
        }

        try { Directory.Delete(scratch, recursive: true); } catch { }

        Console.WriteLine(fails == 0
            ? "[saveloadtest] RESULT: PASS"
            : $"[saveloadtest] RESULT: FAIL ({fails} failed)");
        return fails == 0 ? 0 : 1;
    }

    /// Headless seeder for the app-flow check: creates + saves (does NOT delete) a
    /// world with a chest + furnace, honouring whatever PEBBLE_SUPPORT_DIR the
    /// caller set. The windowed app can then be launched with --loadlast against
    /// the same dir to exercise the real title -> Singleplayer -> load path.
    public static int Seed()
    {
        try
        {
            var g = new GameCore();
            var id = PlaceWorld(g, out int cx, out int cy, out int cz, out int fx, out int fy, out int fz);
            if (id == null) { Console.WriteLine("[seedworld] createWorld produced no record"); return 1; }
            g.saveAndFlush(synchronous: true);
            g.db.Dispose();
            Console.WriteLine($"[seedworld] seeded world id={id} chest@({cx},{cy},{cz}) furnace@({fx},{fy},{fz})");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("[seedworld] threw: " + e);
            return 1;
        }
    }

    /// Create a world, place a chest (17 diamonds) + furnace (coal fuel, raw_iron
    /// input, burnTime=100) just above spawn, and return the world id. Out params
    /// give the placed block-entity positions.
    private static string PlaceWorld(GameCore g,
        out int chestX, out int chestY, out int chestZ,
        out int furnX, out int furnY, out int furnZ)
    {
        chestX = chestY = chestZ = furnX = furnY = furnZ = 0;
        g.createWorld(name: "SaveLoadTest-" + Guid.NewGuid().ToString("N"), seedText: "saveload", mode: 0, difficulty: 2);
        if (g.worldRec == null) return null;
        var w = g.world;
        var p = g.player;

        // one block apart, just above the player's feet (inside the eagerly-loaded
        // spawn chunk so the load path reads them back).
        int bx = (int)Math.Floor(p.x), by = (int)Math.Floor(p.y), bz = (int)Math.Floor(p.z);
        chestX = bx; chestY = by + 1; chestZ = bz;
        furnX = bx + 1; furnY = by + 1; furnZ = bz;

        w.setBlock(chestX, chestY, chestZ, BlockCaches.cell(Reg.bid("chest")));
        w.setBlock(furnX, furnY, furnZ, BlockCaches.cell(Reg.bid("furnace")));

        var chest = BlockEntities.makeContainerBE(chestX, chestY, chestZ, 27);
        chest.items[0] = new ItemStack(ItemGlobals.iid("diamond"), 17);
        w.setBlockEntity(chest);

        var furn = BlockEntities.makeFurnaceBE(furnX, furnY, furnZ, "furnace");
        furn.items[0] = new ItemStack(ItemGlobals.iid("raw_iron"), 5);   // input
        furn.items[1] = new ItemStack(ItemGlobals.iid("coal"), 8);       // fuel
        furn.burnTime = 100;
        w.setBlockEntity(furn);

        return g.worldRec.Value.id;
    }
}

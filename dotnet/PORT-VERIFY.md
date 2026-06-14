# Port verification plan (graphify-derived)

Intelligence pulled from the Pebble knowledge graph (graphify-out/) + source, used to
de-risk the Swift→C# port. Consult this during the build-fix and golden-validation phases.

## Load-bearing engine init order (GameCore.swift:285-291) — MUST replicate exactly
1. registerAllBlocks()   (World/BlockRegistry)  → also finalizeBlockRegistry() → populateBlockIDs()
2. registerAllItems()     (Items/ItemRegistry)  — PRECONDITION: blockDefs non-empty
3. registerAllBiomes()    (Gen/Biomes)
4. registerAllRecipes()   (Items/Recipes)
5. registerAllLootTables()(Items/Loot)          — PRECONDITION: itemDefs non-empty
6. registerAllEntities()  (Entity/EntityRegistry) [god node: 101 edges]
7. registerAllSystems()   (Systems/Interact)
- registerAllStructures() (Gen/StructBigNetherEnd) — called lazily from Generator.swift:107
- registerAllModels()/ensureModels() (Render/EntityModels[2]) — render-side init
The C# bootstrap (and PebbleSmoke harness) must call these in this order. pebsmoke order:
blocks(111) → items(145) → biomes(181) → recipes(982) → loot(983) → entities(1174) → systems(1546).

## God nodes / cross-cutting helpers — highest port risk
- **detRound(x) = Math.Floor(x + 0.5)** — defined file-private in Gen/Terrain.swift:17, but called by
  ~20 sites across Terrain, Redstone, AtlasPainters1, Features(genCherryTree), NetherEnd(endPillars,
  spawnEndGateway,gatewayPositions), Loot(selectEnchants), Vehicles(.tickOnRail), Mesher(.cornerLight),
  Combat(.onHitBlock). → The Terrain agent puts it in a static class; **ADD `global using static` for
  that class** or all callers fail to resolve. Bit-exact `(x+0.5)` then floor — never FMA/round-to-even.
- **iid(name)->int** = itemByName[name] lookup (Items/ItemDefs.swift:190); analog of bid/B for items.
  41 callers (useItem/useBlock/dispense/loot/combat/villager offer/render drawSprites). Interning order
  pins item IDs → items-goldens depend on it. Helpers: iidOpt, itemExists, itemDef, itemName,
  **stack(name,count) = new ItemStack(iid(name), count)** → ItemStack ctor is **ItemStack(int id, int count)**.
- registerAllEntities (101), Shape (99), GameCore (86), Biome (69), World (64), UIManager (62),
  WorldRenderer (56) — verify these resolve and behave once assembled.

## Golden suites → subsystem (validation priority, all must pass for "100% functionality")
- fmath-goldens (911 probes) ✅ already bit-identical (Core done)
- random/noise ✅ done
- block & item registries (counts + id spot checks) → BlockRegistry*, ItemRegistry; tileCount==757 at
  populateBlockIDs boundary (agent already confirmed 757).
- biome-goldens (63 defs + 2000 selections) → Gen/Biomes
- terrain-goldens (full pipeline hashes, 2 seeds) → Gen/Terrain, Generator, NetherEnd
- feature-goldens (whole-chunk, 3 dims) → Gen/Features, Structures
- atlas-goldens (pixel-identical, count 757) → Render/AtlasCore, AtlasPainters1/2, Icons
- mesh-goldens (vertex/index hashes) → Render/Mesher
- worldsim-goldens (light/fluids/RNG lockstep) → World/LightEngine, Systems/Fluids, GameWorld
- items-goldens (recipes/enchants/potions/loot) → Items/*, Systems/Crafting
- entity-goldens (55-mob zoo×200 ticks, combat, scripted player physics, trades, pathfinding, spawning)
  → Entity/* (Player physics constants are DERIVED in the suite, not just asserted)
- systems-goldens (crafting probes, BE timelines, redstone contraption, explosion crater, interactions,
  portals) → Systems/*

## Post-workflow wiring checklist (main loop)
1. Collect each agent's reported staticClasses → add `global using static PebbleCore.<Name>;` for every
   one that holds engine-global free funcs (esp. Terrain's detRound class, ItemDefs' iid class).
2. Build PebbleCore; resolve cross-file mismatches (signature drift, value/ref semantics, name clashes).
3. Port the full pebsmoke harness; run all 456 checks; fix until parity.

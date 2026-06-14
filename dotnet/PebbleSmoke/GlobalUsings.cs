// Mirror PebbleCore's global usings so the test harness can call engine free
// functions unqualified, exactly as the Swift pebsmoke does via `import PebbleCore`.
global using PebbleCore;

// Core
global using static PebbleCore.DetMath;
global using static PebbleCore.Hashing;
global using static PebbleCore.MathX;
global using static PebbleCore.Dirs;

// World
global using static PebbleCore.Reg;
global using static PebbleCore.BlockData;
global using static PebbleCore.BlockRegistration;
global using static PebbleCore.LocalLight;
global using static PebbleCore.WorldGlobals;
global using static PebbleCore.BlockEntities;
global using static PebbleCore.BlockCaches;
global using static PebbleCore.BlockShapes;
global using static PebbleCore.WorldHandlers;
global using static PebbleCore.BlockIds;

// Items
global using static PebbleCore.ItemGlobals;
global using static PebbleCore.ItemRegistryGlobals;
global using static PebbleCore.EnchantsPotionsGlobals;
global using static PebbleCore.LootGlobals;
global using static PebbleCore.RecipeGlobals;

// Gen
global using static PebbleCore.BiomeGlobals;
global using static PebbleCore.StructFrameworkGlobals;
global using static PebbleCore.TerrainGlobals;
global using static PebbleCore.GenGlobals;
global using static PebbleCore.FeaturesGlobals;
global using static PebbleCore.NetherEndGlobals;
global using static PebbleCore.StructOverworldGlobals;
global using static PebbleCore.StructUndergroundGlobals;
global using static PebbleCore.StructBigNetherEndGlobals;

// Entity
global using static PebbleCore.EntityGlobals;
global using static PebbleCore.LivingGlobals;
global using static PebbleCore.AIGlobals;
global using static PebbleCore.EntityRegistryGlobals;
global using static PebbleCore.PlayerGlobals;
global using static PebbleCore.AnimalsGlobals;
global using static PebbleCore.MonstersGlobals;
global using static PebbleCore.NetherMobsGlobals;
global using static PebbleCore.VillagersGlobals;
global using static PebbleCore.ProjectilesGlobals;
global using static PebbleCore.VehiclesGlobals;
global using static PebbleCore.MiscGlobals;
global using static PebbleCore.SpawnHooksGlobals;

// Systems
global using static PebbleCore.InteractGlobals;
global using static PebbleCore.RedstoneGlobals;
global using static PebbleCore.FarmingGlobals;
global using static PebbleCore.BlockEntitySystems;
global using static PebbleCore.CombatGlobals;
global using static PebbleCore.CraftingGlobals;
global using static PebbleCore.FluidsGlobals;
global using static PebbleCore.ExplosionGlobals;
global using static PebbleCore.PortalsGlobals;
global using static PebbleCore.RaidsGlobals;
global using static PebbleCore.AdvancementsGlobals;

// Render (data side)
global using static PebbleCore.AtlasGlobals;
global using static PebbleCore.EntityModelsGlobals;
global using static PebbleCore.MesherGlobals;
global using static PebbleCore.AtlasPainters1Globals;
global using static PebbleCore.AtlasPainters2Globals;
global using static PebbleCore.IconGlobals;
global using static PebbleCore.EntityModels2Globals;

// Game
global using static PebbleCore.SettingsGlobals;
global using static PebbleCore.GameCoreGlobals;
global using static PebbleCore.SavesGlobals;

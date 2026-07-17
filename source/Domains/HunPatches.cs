using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 1 hooks (rank-bonus-design §HUN, ruled 2026-07-10; seams re-pinned against the
/// LIVE binaries 2026-07-17 — Butchering 1.13.5, PS 5.0.6, the 0.3.77 namespace lesson).
///
/// Verbs:
///   • hunting — sapi.Event.OnEntityDeath, credited to the CAUSING player (projectile kills
///     resolve through DamageSource.GetCauseEntity), only for wild huntable game (has the
///     harvestable behavior, not tamed/owned). Every kill also feeds the per-species ledger
///     that will gate the Phase 3 Hunter's Map: the hunter knows the country he has hunted.
///   • dressing — EntityBehaviorHarvestable.GenerateDrops postfix (the field harvest; the
///     same method whose yield rides animalLootDropRate).
///   • trapping — PS snares + deadfalls, owner-at-placement (the FIS trap shape: no trap BE
///     stores an owner). Credit at collection of a CATCH — retrieving your own bait does not
///     count (checked against the BE's own bait-type list).
///   • butchery — Butchering's BlockEntityButcherWorkstation.processItem (the abstract base
///     both hook and table route through), by-target ruling: cutting game is a hunter's skill.
///
/// Stats (both verified vanilla, zero-Harmony, reconcile tick): animalLootDropRate 0.9→1.15
/// and animalSeekingRange 1.15→0.75 (floored above zero — no invisible hunter, ruled).
/// </summary>
public static class HunPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    // ------------------------------------------------------------ per-species kill ledger

    /// <summary>uid -> species (entity code first part) -> lifetime kills. Phase 3's Hunter's
    /// Map knowledge gate reads this; recorded from Phase 1 day one so no one starts at zero.</summary>
    private static Dictionary<string, Dictionary<string, int>> kills = new();

    public static int KillCount(IPlayer player, string species) =>
        kills.TryGetValue(player.PlayerUID, out var per) && per.TryGetValue(species, out int n) ? n : 0;

    // ------------------------------------------------------------ trap owner side-state

    private static Dictionary<string, string> trapOwners = new();

    private static string StateFileName
    {
        get
        {
            string name = sapi?.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi?.WorldManager.SaveGame?.WorldName ?? "almanactcm_save";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(GamePaths.Saves, "AlmanacTcm", name + "-hunstate.json");
        }
    }

    private static string Key(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    // ------------------------------------------------------------ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += Load;
        api.Event.GameWorldSave += Save;
        api.Event.OnEntityDeath += OnEntityDeath;
        api.Event.RegisterGameTickListener(ReconcileHunStats, 2000);
    }

    private static void Load()
    {
        try
        {
            byte[]? data = sapi!.WorldManager.SaveGame.GetData("almanacHunKills");
            if (data != null)
                kills = SerializerUtil.Deserialize<Dictionary<string, Dictionary<string, int>>>(data) ?? new();
        }
        catch (Exception e) { TcmLog.Error(sapi, $"hun kill ledger unreadable ({e.Message}); starting empty"); }

        try
        {
            string file = StateFileName;
            if (File.Exists(file))
                trapOwners = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
            TcmLog.Cat(sapi, TcmLog.Config, $"HUN state loaded: {trapOwners.Count} owned trap(s), {kills.Count} hunter ledger(s)");
        }
        catch (Exception e) { TcmLog.Error(sapi, $"hunstate.json unreadable ({e.Message}); starting empty, NOT overwriting"); }
    }

    private static void Save()
    {
        sapi!.WorldManager.SaveGame.StoreData("almanacHunKills", SerializerUtil.Serialize(kills));
        try
        {
            string file = StateFileName;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonConvert.SerializeObject(trapOwners));
        }
        catch (Exception e) { TcmLog.Error(sapi, $"could not save HUN state: {e.Message}"); }
    }

    // ------------------------------------------------------------ hunting (the kill)

    private static void OnEntityDeath(Entity entity, DamageSource? damageSource)
    {
        if (sapi == null || entity == null || damageSource == null) return;
        if (entity.GetBehavior<EntityBehaviorHarvestable>() == null) return; // not huntable game

        // Tamed/owned beasts are ANI's world, not the hunt's (petai/wolftaming/genelib guards).
        var wa = entity.WatchedAttributes;
        if (wa != null && (wa.GetBool("domesticated") || wa.HasAttribute("ownedby") || wa.HasAttribute("owner"))) return;

        Entity? cause = damageSource.GetCauseEntity() ?? damageSource.SourceEntity;
        IPlayer? player = (cause as EntityPlayer)?.Player;
        if (player == null) return; // wolves, falls, traps-by-AI: nobody's practice

        string species = entity.Code?.FirstCodePart() ?? "unknown";
        Core?.Ledger?.Log(player, HunDomain.Code, HunDomain.TechHunting,
            HashCode.Combine(species, sapi.World.ElapsedMilliseconds / 1000));

        var per = kills.TryGetValue(player.PlayerUID, out var p) ? p : kills[player.PlayerUID] = new();
        per[species] = per.TryGetValue(species, out int n) ? n + 1 : 1;
    }

    // ------------------------------------------------------------ stat reconcile

    private static readonly Dictionary<string, (double yield, double seek)> lastStats = new();

    private static void ReconcileHunStats(float dt)
    {
        if (sapi == null) return;
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            var entity = player.Entity;
            if (entity == null) continue;
            int level = HunDomain.LevelOf(player);
            double yield = HunDomain.RankLinear(level,
                HunDomain.Knob(HunDomain.AnimalYieldUntrained, 0.70),
                HunDomain.Knob(HunDomain.AnimalYieldGm, 1.15));
            double seek = HunDomain.RankLinear(level,
                HunDomain.Knob(HunDomain.SeekRangeUntrained, 1.15),
                HunDomain.Knob(HunDomain.SeekRangeGm, 0.75));
            seek = Math.Max(0.05, seek); // ruled: floored above zero, no invisible hunter
            if (lastStats.TryGetValue(player.PlayerUID, out var prev)
                && Math.Abs(prev.yield - yield) < 0.001 && Math.Abs(prev.seek - seek) < 0.001) continue;
            entity.Stats.Set("animalLootDropRate", "almanactcm", (float)(yield - 1.0), false);
            entity.Stats.Set("animalSeekingRange", "almanactcm", (float)(seek - 1.0), false);
            lastStats[player.PlayerUID] = (yield, seek);
        }
    }

    // ------------------------------------------------------------ dressing (field harvest)

    [HarmonyPatch(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.GenerateDrops))]
    public static class DressingPatch
    {
        public static void Postfix(IPlayer byPlayer)
        {
            var world = byPlayer?.Entity?.World;
            if (world == null || world.Side != EnumAppSide.Server) return;
            Core?.Ledger?.Log(byPlayer!, HunDomain.Code, HunDomain.TechDressing,
                HashCode.Combine(HunDomain.TechDressing, world.ElapsedMilliseconds / 1000));
        }
    }

    // ------------------------------------------------------------ conditional seams

    private static readonly List<Type> trapBlockTypes = new();

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Butchering stations: one postfix on the abstract base covers hook AND table.
        if (api.ModLoader.IsModEnabled("butchering"))
        {
            var ws = AccessTools.TypeByName("Butchering.src.common.blockentity.BlockEntityButcherWorkstation");
            var m = ws == null ? null : AccessTools.Method(ws, "processItem");
            if (m == null) TcmLog.Warn(api, "butchering present but processItem not found; HUN butchery inactive");
            else
            {
                harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(ButcheryPatch), "Postfix")));
                TcmLog.Info(api, "HUN butchery hooked to Butchering (workstation base; by-target ruling)");
            }
        }

        // PS land traps: snare + deadfall, owner at placement, credit at catch collection.
        if (api.ModLoader.IsModEnabled("primitivesurvival"))
        {
            int hooked = 0;
            foreach (string block in new[] { "BlockSnare", "BlockDeadfall" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + block);
                if (t != null) trapBlockTypes.Add(t);
            }
            foreach (string be in new[] { "BESnare", "BEDeadfall" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + be);
                var m = t == null ? null : AccessTools.Method(t, "OnInteract");
                if (m == null) { TcmLog.Warn(api, $"primitivesurvival {be}.OnInteract not found; that trap is uncredited"); continue; }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(LandTrapCollectPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(LandTrapCollectPatch), "Postfix")));
                hooked++;
            }
            if (trapBlockTypes.Count > 0)
            {
                harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(TrapPlacePatch), "Postfix")));
            }
            if (hooked > 0) TcmLog.Info(api, $"HUN trapping hooked ({hooked} trap BE(s); owner at placement, catch at collection)");
        }
    }

    public static class ButcheryPatch
    {
        public static void Postfix(object __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            Core?.Ledger?.Log(byPlayer, HunDomain.Code, HunDomain.TechButchery,
                HashCode.Combine(be.Pos.X >> 3, be.Pos.Y >> 3, be.Pos.Z >> 3, be.Api.World.ElapsedMilliseconds / 1000));
        }
    }

    /// <summary>Stamps the trap owner at placement (broad seam, type check exits first).</summary>
    public static class TrapPlacePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
            foreach (Type t in trapBlockTypes)
            {
                if (t.IsInstanceOfType(__instance)) { trapOwners[Key(blockSel.Position)] = byPlayer.PlayerUID; return; }
            }
        }
    }

    /// <summary>Snare/deadfall collection: the display slot held something that is NOT on the
    /// BE's own bait list (a catch) and the interact emptied it — that is a collected catch.
    /// Taking your own bait back credits nothing.</summary>
    public static class LandTrapCollectPatch
    {
        private static (string? code, bool wasBait) Read(BlockEntity be)
        {
            if (be is not BlockEntityContainer c || c.Inventory == null || c.Inventory.Count == 0) return (null, false);
            string? path = c.Inventory[0]?.Itemstack?.Collectible?.Code?.Path;
            if (path == null) return (null, false);
            bool bait = false;
            try
            {
                if (Traverse.Create(be).Field("baitTypes").GetValue() is string[] baits)
                    foreach (string b in baits)
                        if (path.Contains(b)) { bait = true; break; }
            }
            catch { }
            return (path, bait);
        }

        public static void Prefix(object __instance, out (string? code, bool wasBait) __state)
        {
            __state = __instance is BlockEntity be && be.Api?.Side == EnumAppSide.Server ? Read(be) : (null, false);
        }

        public static void Postfix(object __instance, (string? code, bool wasBait) __state)
        {
            if (__state.code == null || __state.wasBait) return;
            if (__instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            var (now, _) = Read(be);
            if (now != null) return; // nothing left the trap

            if (!trapOwners.TryGetValue(Key(be.Pos), out string? uid) || uid == null) return;
            IPlayer? owner = be.Api.World.PlayerByUid(uid);
            if (owner == null) return; // owner offline; their catch, but practice waits for them

            Core?.Ledger?.Log(owner, HunDomain.Code, HunDomain.TechTrapping, be.Pos.GetHashCode());
        }
    }
}

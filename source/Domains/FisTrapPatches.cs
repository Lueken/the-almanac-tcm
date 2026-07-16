using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// FIS Phase 1b — the passive water-trapping verb (technique-maps §FIS #2): PS fish basket,
/// weir trap, and limb trotline, plus Ithania's fish trap (the ruled pool-widening; a distinct
/// BE class, so it needs its own hook). One verb across four apparatus.
///
/// ATTRIBUTION (FIS ruling 4): traps catch unattended and store NO owner in any of the four BEs
/// (verified in both decompiles), so ownership lives in the Almanac's persisted side map, stamped
/// pos->uid at block placement. Credit banks to the OWNER when a collection actually removes
/// fish — a stranger emptying your trap credits YOU, not the thief. Placing and baiting alone
/// grant nothing (anti-farm ruling). Traps placed before this build have no recorded owner and
/// grant nothing (the spile precedent).
///
/// COLLECTION DETECTION, per family:
///   • PS (all three BEs extend BlockEntityContainer): fish stacks in the trap inventory are the
///     ps*waterfish items. Count before/after OnInteract; a decrease = fish left the trap.
///   • Ithania: the private caughtFish list empties to zero on the empty-hand collect branch.
/// </summary>
public static class FisTrapPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    // ------------------------------------------------------------------ owner side-state

    private static Dictionary<string, string> owners = new();
    private static ICoreServerAPI? sapi;

    private static string StateFileName
    {
        get
        {
            string name = sapi?.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi?.WorldManager.SaveGame?.WorldName ?? "almanactcm_save";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(GamePaths.Saves, "AlmanacTcm", name + "-fisstate.json");
        }
    }

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        try
        {
            string file = StateFileName;
            if (File.Exists(file))
            {
                owners = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
            }
            TcmLog.Cat(api, TcmLog.Config, $"FIS state loaded: {owners.Count} owned trap(s)");
        }
        catch (Exception e)
        {
            TcmLog.Error(api, $"fisstate.json unreadable ({e.Message}); starting empty, NOT overwriting");
            owners = new();
        }
        api.Event.GameWorldSave += Save;
    }

    private static void Save()
    {
        try
        {
            string file = StateFileName;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonConvert.SerializeObject(owners));
        }
        catch (Exception e) { TcmLog.Error(sapi, $"could not save FIS state: {e.Message}"); }
    }

    private static string Key(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    // ------------------------------------------------------------------ patching

    private static readonly List<Type> trapBlockTypes = new();

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        bool ps = api.ModLoader.IsModEnabled("primitivesurvival");
        bool it = api.ModLoader.IsModEnabled("ithaniaexpandedfishing");
        if (!ps && !it) return;

        int hooked = 0;

        if (ps)
        {
            foreach (string block in new[] { "BlockFishBasket", "BlockWeirTrap", "BlockLimbTrotLineLure" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + block);
                if (t != null) trapBlockTypes.Add(t);
            }
            foreach (string be in new[] { "BEFishBasket", "BEWeirTrap", "BELimbTrotLineLure" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + be);
                var m = t == null ? null : AccessTools.Method(t, "OnInteract");
                if (m == null) { TcmLog.Warn(api, $"primitivesurvival {be}.OnInteract not found; that trap is uncredited"); continue; }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PsCollectPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PsCollectPatch), "Postfix")));
                hooked++;
            }
        }

        if (it)
        {
            var tb = AccessTools.TypeByName("IthaniaExpandedFishing.Blocks.BlockFishTrap");
            if (tb != null) trapBlockTypes.Add(tb);
            var tbe = AccessTools.TypeByName("IthaniaExpandedFishing.BlockEntities.BlockEntityFishTrap");
            var m = tbe == null ? null : AccessTools.Method(tbe, "OnInteract");
            if (m == null) { TcmLog.Warn(api, "ithania BlockEntityFishTrap.OnInteract not found; that trap is uncredited"); }
            else
            {
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(IthaniaCollectPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(IthaniaCollectPatch), "Postfix")));
                hooked++;
            }
        }

        if (trapBlockTypes.Count > 0)
        {
            harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TrapPlacePatch), "Postfix")));
        }
        TcmLog.Info(api, $"FIS trapping hooked ({hooked} trap BE(s); owner at placement, credit at collection)");
    }

    /// <summary>Stamps the trap owner at placement. Broad seam, so the type check exits first.</summary>
    public static class TrapPlacePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
            foreach (Type t in trapBlockTypes)
            {
                if (t.IsInstanceOfType(__instance)) { owners[Key(blockSel.Position)] = byPlayer.PlayerUID; return; }
            }
        }
    }

    private static void CreditOwner(BlockEntity be)
    {
        if (!owners.TryGetValue(Key(be.Pos), out string? uid) || uid == null) return; // unowned: vanilla
        IPlayer? owner = be.Api.World.PlayerByUid(uid);
        if (owner == null) return; // owner offline; their catch, but practice waits for them

        Core?.Ledger?.Log(owner, FisDomain.Code, FisDomain.TechTrapping, be.Pos.GetHashCode());
    }

    /// <summary>PS traps: fish leave the container inventory on a successful empty-hand take.</summary>
    public static class PsCollectPatch
    {
        private static int CountFish(BlockEntity be)
        {
            if (be is not BlockEntityContainer c || c.Inventory == null) return 0;
            int n = 0;
            foreach (ItemSlot slot in c.Inventory)
            {
                string? path = slot?.Itemstack?.Collectible?.Code?.Path;
                if (path != null && (path.Contains("psfreshwaterfish") || path.Contains("pssaltwaterfish"))) n++;
            }
            return n;
        }

        public static void Prefix(object __instance, out int __state)
        {
            __state = __instance is BlockEntity be && be.Api?.Side == EnumAppSide.Server ? CountFish(be) : -1;
        }

        public static void Postfix(object __instance, int __state)
        {
            if (__state <= 0 || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            if (CountFish(be) < __state) CreditOwner(be);
        }
    }

    /// <summary>Ithania trap: the private caughtFish list empties on the collect branch.</summary>
    public static class IthaniaCollectPatch
    {
        public static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            if (__instance is BlockEntity be && be.Api?.Side == EnumAppSide.Server
                && Traverse.Create(__instance).Field("caughtFish").GetValue() is IList list)
            {
                __state = list.Count;
            }
        }

        public static void Postfix(object __instance, int __state)
        {
            if (__state <= 0 || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            if (Traverse.Create(__instance).Field("caughtFish").GetValue() is IList list && list.Count < __state)
            {
                CreditOwner(be);
            }
        }
    }
}

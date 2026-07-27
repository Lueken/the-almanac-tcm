using System;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent.Mechanics;

namespace AlmanacTcm.Domains;

/// <summary>
/// ENG — the verbs that GRANT rank, the repair-EFFECTIVENESS lever, and the MILLWRIGHT'S MARK decay
/// lever (rank-bonus-design.md §ENG; technique-maps §ENG RULED). Two verbs:
///
///   • Mechanical-power assembly [vanilla] — grant at the rotor sail-rigging interact (BEBehaviorWindmillRotor.
///     OnInteract, the consume-and-grow signal). millwright's enhanced/VAWT rotors are the same verb (pool),
///     hooked reflectively where present.
///   • Mechanical maintenance [wearandtear, conditional] — grant at a successful service (PartController.
///     TryMaintenance), scale repair effectiveness (Part.DoMaintenanceFor strength) by ENG rank, and set
///     the serviced part's DecayModifier by ENG rank (the Millwright's Mark: a master's service lasts
///     longer, an under-ranked hand's later service resets it). All reflected + isolated (warns-and-skips).
///
/// UNIFY (Jeffrey ruling A, 2026-07-22): ENG owns the TryMaintenance / DoMaintenanceFor / DecayModifier
/// hooks directly. xLib is present only as a library (its PartBonuses substrate + UpdateDecay read),
/// while no live xSkills "mechanics" skill exists — so wearandtear's own ApplyHandyManBonus / UpdateForRepair
/// ability scaling is a no-op, and ENG's Harmony layer is the sole scaler (no double-scale to suppress).
/// Without xLib the decay lever's substrate is absent and it degrades gracefully (grant + repair
/// effectiveness still work).
/// </summary>
public static class EngPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    /// <summary>The tree-attribute key carrying the LAST servicer's mark "uid|name|level" on a machine's
    /// PartController — persisted with the mechanism and synced to clients.</summary>
    private const string MarkKey = "almanactcm:engby";

    /// <summary>In-memory mirror of the last servicer per live PartController (a BlockEntityBehavior),
    /// weak so it never keeps a BE alive. The mechanism's own serialization carries it to disk + clients.
    /// LAST-servicer-wins (overwritten on every service) — the living service contract, the deliberate
    /// opposite of the MAS first-carve mark: a lesser hand's later service resets both the name and the
    /// decay (the inverted repair-gate).</summary>
    private static readonly ConditionalWeakTable<BlockEntityBehavior, string> servicerByCtl = new();

    public static void RegisterServer(ICoreServerAPI api) { }

    // ------------------------------------------------------------ conditional patches

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // ---- Assembly (millwright pool rotors): same consume-and-grow verb, reflected where present.
        foreach (var typeName in new[]
        {
            "Millwright.ModSystem.BEBehaviorWindmillRotorEnhanced",
            "Millwright.ModSystem.BEBehaviorWindmillRotorUD",
        })
        {
            var t = AccessTools.TypeByName(typeName);
            var m = t == null ? null : AccessTools.DeclaredMethod(t, "OnInteract");
            if (m != null)
                harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(AssemblyPostfix))));
        }

        // ---- Maintenance (wearandtear): grant + repair effectiveness + the decay lever.
        var partCtl = AccessTools.TypeByName("WearAndTear.Code.Behaviours.PartController");
        var mTry = partCtl == null ? null : AccessTools.DeclaredMethod(partCtl, "TryMaintenance");
        var part = AccessTools.TypeByName("WearAndTear.Code.Behaviours.Part");
        var mDo = part == null ? null : AccessTools.DeclaredMethod(part, "DoMaintenanceFor");
        var bonuses = AccessTools.TypeByName("WearAndTear.Code.XLib.Containers.PartBonuses");
        var mUpd = bonuses == null ? null : AccessTools.DeclaredMethod(bonuses, "UpdateForRepair");

        if (mTry != null)
        {
            harmony.Patch(mTry, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(MaintenancePostfix))));
            TcmLog.Info(api, "ENG maintenance grant hooked (TryMaintenance completion)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ENG maintenance seam absent (wearandtear); maintenance verb inactive");

        if (mDo != null)
            harmony.Patch(mDo, prefix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(RepairStrengthPrefix))));
        if (mUpd != null)
            harmony.Patch(mUpd, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(DecayModifierPostfix))));
        if (mDo != null || mUpd != null)
            TcmLog.Info(api, "ENG repair effectiveness + Millwright's Mark decay lever hooked");
        else if (mTry == null)
            TcmLog.Cat(api, TcmLog.Config, "ENG decay/repair levers inactive (wearandtear absent)");

        // ---- "Serviced by X" provenance: carry the last servicer on the PartController's own serialization
        // (persists + syncs) and show it in the mechanism's block info. PartController overrides all three.
        var mTo = partCtl == null ? null : AccessTools.DeclaredMethod(partCtl, "ToTreeAttributes");
        var mFrom = partCtl == null ? null : AccessTools.DeclaredMethod(partCtl, "FromTreeAttributes");
        var mInfo = partCtl == null ? null : AccessTools.DeclaredMethod(partCtl, "GetBlockInfo");
        if (mTo != null) harmony.Patch(mTo, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(ToTreePostfix))));
        if (mFrom != null) harmony.Patch(mFrom, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(FromTreePostfix))));
        if (mInfo != null) harmony.Patch(mInfo, postfix: new HarmonyMethod(AccessTools.Method(typeof(EngPatches), nameof(GetBlockInfoPostfix))));
        if (mInfo != null) TcmLog.Info(api, "ENG Serviced-by provenance hooked (last-servicer, shown on the mechanism)");
    }

    // ------------------------------------------------------------ assembly (vanilla + millwright pool)

    /// <summary>Grant ENG assembly when a rotor sail-rigging interact succeeds (a sail was consumed and the
    /// sail grew). Deduped per rotor per world-minute so growing one rotor over several sails banks steadily
    /// but not per-click-spam.</summary>
    public static void AssemblyPostfix(BlockEntityBehavior __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;
        var pos = __instance?.Blockentity?.Pos;
        Core?.Ledger?.Log(byPlayer, EngDomain.Code, EngDomain.TechAssembly,
            HashCode.Combine("assembly", pos?.X ?? 0, pos?.Y ?? 0, pos?.Z ?? 0,
                (int)(byPlayer.Entity.World.ElapsedMilliseconds / 60000)));
    }

    /// <summary>Vanilla windmill rotor rigging — the guaranteed ENG floor. Same verb as the millwright
    /// pool rotors above.</summary>
    [HarmonyPatch(typeof(BEBehaviorWindmillRotor), nameof(BEBehaviorWindmillRotor.OnInteract))]
    public static class WindmillAssemblyPatch
    {
        public static void Postfix(BEBehaviorWindmillRotor __instance, IPlayer byPlayer, bool __result)
            => AssemblyPostfix(__instance, byPlayer, __result);
    }

    // ------------------------------------------------------------ maintenance grant + levers

    /// <summary>Grant ENG maintenance at a successful service, and STAMP the mechanism with this servicer
    /// (last-servicer-wins — overwrites any prior). __instance is the PartController (a BlockEntityBehavior);
    /// dedup the grant on its block position + world-minute; MarkDirty so the stamp serializes + syncs.</summary>
    public static void MaintenancePostfix(BlockEntityBehavior __instance, EntityAgent byEntity, bool __result)
    {
        if (!__result || __instance == null || byEntity?.World?.Side != EnumAppSide.Server) return;
        var player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;
        var pos = __instance.Blockentity?.Pos;
        Core?.Ledger?.Log(player, EngDomain.Code, EngDomain.TechMaintenance,
            HashCode.Combine("maintenance", pos?.X ?? 0, pos?.Y ?? 0, pos?.Z ?? 0,
                (int)(byEntity.World.ElapsedMilliseconds / 60000)));

        servicerByCtl.Remove(__instance);
        servicerByCtl.Add(__instance, $"{player.PlayerUID}|{player.PlayerName}|{EngDomain.LevelOf(player)}");
        __instance.Blockentity?.MarkDirty(true);
    }

    /// <summary>Repair effectiveness (Axis 4 + Axis 1): scale the maintenance strength by the servicing
    /// engineer's ENG rank before the part restores durability. A master restores more per repair item,
    /// a beginner less. Rides the exact seam wearandtear's own ApplyHandyManBonus scales (now a no-op).</summary>
    public static void RepairStrengthPrefix(EntityPlayer player, ref float maintenanceStrength)
    {
        if (((Entity)player)?.World?.Side != EnumAppSide.Server) return;
        maintenanceStrength *= (float)EngDomain.RepairMul(EngDomain.LevelOf(player.Player));
    }

    /// <summary>The Millwright's Mark (Axis 6): set the serviced part's DecayModifier by ENG rank, LAST
    /// (wearandtear's UpdateForRepair resets it to 1.0 then applies no-op xSkills abilities, so this
    /// postfix wins). A GM-serviced part decays slower; an Untrained fix decays faster. Persisted per-part
    /// (read in UpdateDecay), it holds until the next service overwrites it — the natural upkeep window.
    /// __instance is PartBonuses (an unreferenced wearandtear type), so set the field via Traverse.</summary>
    public static void DecayModifierPostfix(object __instance, IPlayer player)
    {
        if (player is not IServerPlayer) return;
        double mul = EngDomain.DecayMul(EngDomain.LevelOf(player));
        Traverse.Create(__instance).Field("DecayModifier").SetValue((float)mul);
    }

    // ------------------------------------------------------------ "Serviced by X" provenance

    /// <summary>Carry the last servicer on the PartController's own serialization (persists to disk +
    /// syncs to clients, where the block-info renders). __instance is the PartController (a BlockEntityBehavior).</summary>
    public static void ToTreePostfix(BlockEntityBehavior __instance, ITreeAttribute tree)
    {
        if (servicerByCtl.TryGetValue(__instance, out string? mark) && mark != null)
            tree.SetString(MarkKey, mark);
    }

    /// <summary>Read the servicer back on deserialize (both sides) into the in-memory mirror.</summary>
    public static void FromTreePostfix(BlockEntityBehavior __instance, ITreeAttribute tree)
    {
        string? mark = tree.GetString(MarkKey);
        if (string.IsNullOrEmpty(mark)) return;
        servicerByCtl.Remove(__instance);
        servicerByCtl.Add(__instance, mark);
    }

    /// <summary>Show "Serviced by X" in the mechanism's block info, tiered by the servicer's rank. Shown
    /// from JOURNEYMAN up only: a lesser hand's service drops the master's line (and worsens decay) — the
    /// absence of the mark reads as "a lesser hand was here," the standing-contract signal.</summary>
    public static void GetBlockInfoPostfix(BlockEntityBehavior __instance, StringBuilder dsc)
    {
        if (!servicerByCtl.TryGetValue(__instance, out string? packed) || packed == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int level)) return;
        string name = p[1];
        string? line =
            level >= EngDomain.ProvGm ? Lang.Get("almanactcm:eng-millwrights-mark", name)
            : level >= EngDomain.ProvMaster ? Lang.Get("almanactcm:eng-master-serviced-by", name)
            : level >= EngDomain.ProvJourneyman ? Lang.Get("almanactcm:eng-serviced-by", name)
            : null;
        if (line != null) dsc.AppendLine(line);
    }
}

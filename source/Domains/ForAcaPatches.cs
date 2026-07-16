using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// FOR tapping — the ACulinaryArtillery spile verb (technique-maps §FOR #3, ruled FOR 100 by the
/// COO demotion). ACA-conditional; loads clean without it.
///
/// SPEC CORRECTION (verified against the ACA decompile 2026-07-16): the technique map claimed
/// OnBlockPlaced "stamps the tapping player" — it does not. BlockEntitySpile stores ONLY a drip
/// timer; there is no owner anywhere, and the sap drips straight into the container below
/// (TryPutLiquid inside SapDrip), so there is no "collect the sap" interaction to credit either.
/// The honest build is the Collier's Mark shape:
///   • owner-at-placement: a postfix on Block.DoPlaceBlock, gated to ACA's BlockSpile, writes
///     pos→uid into the persisted FOR side-state (ForPatches owns the file).
///   • credit-as-it-drips: SapDrip's public `timer` field only advances when a drip interval
///     matures (hours of real season-gated accumulation), so a prefix snapshots it and the
///     postfix credits the owner when it moved. Placing a spile grants NOTHING (the anti-farm
///     ruling); credit accrues only as the tapline actually works, and only while the owner is
///     online (the BE only ticks in loaded chunks, so the owner usually is).
/// </summary>
public static class ForAcaPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    private static Type? spileBlockType;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("aculinaryartillery")) return;

        spileBlockType = AccessTools.TypeByName("ACulinaryArtillery.BlockSpile");
        var spileBe = AccessTools.TypeByName("ACulinaryArtillery.BlockEntitySpile");
        var sapDrip = spileBe == null ? null : AccessTools.Method(spileBe, "SapDrip");
        var doPlace = AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock));

        if (spileBlockType == null || sapDrip == null || doPlace == null)
        {
            TcmLog.Warn(api, "aculinaryartillery present but the spile seams were not found; FOR tapping inactive");
            return;
        }

        harmony.Patch(doPlace, postfix: new HarmonyMethod(AccessTools.Method(typeof(SpilePlacePatch), "Postfix")));
        harmony.Patch(sapDrip,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SapDripPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(SapDripPatch), "Postfix")));
        TcmLog.Info(api, "FOR tapping hooked to ACA (spile owner at placement, credit as the tapline drips)");
    }

    /// <summary>Stamps the tapper. Block.DoPlaceBlock is a broad seam, so this exits on the type
    /// check first; it only ever writes for an actual spile placement.</summary>
    public static class SpilePlacePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
            if (spileBlockType == null || !spileBlockType.IsInstanceOfType(__instance)) return;
            ForPatches.RememberTap(blockSel.Position, byPlayer.PlayerUID);
        }
    }

    /// <summary>Credits the tapline's owner when a drip interval matured. `timer` is a public
    /// field that only advances inside the maturation loop, so timer-moved == the tap worked.
    /// contextHash is the spile position: at hour-scale cadence the dedup window never bites a
    /// legitimate tapline, and a multi-spile sweep credits each tap separately.</summary>
    public static class SapDripPatch
    {
        public static void Prefix(object __instance, out double __state)
        {
            __state = Traverse.Create(__instance).Field("timer").GetValue<double>();
        }

        public static void Postfix(object __instance, double __state)
        {
            if (__instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            double now = Traverse.Create(__instance).Field("timer").GetValue<double>();
            if (now <= __state) return; // no interval matured this tick

            string? uid = ForPatches.TapOwner(be.Pos);
            if (uid == null) return; // pre-existing spile with no recorded tapper: stays vanilla
            IPlayer? owner = be.Api.World.PlayerByUid(uid);
            if (owner == null) return; // owner offline; the sap still drips, practice waits

            Core?.Ledger?.Log(owner, ForDomain.Code, ForDomain.TechTapping, be.Pos.GetHashCode());
        }
    }
}

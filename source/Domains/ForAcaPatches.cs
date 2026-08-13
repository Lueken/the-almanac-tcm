using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// FOR tapping — the ACulinaryArtillery spile verb (technique-maps §FOR #3, ruled FOR 100 by the
/// COO demotion). ACA-conditional; loads clean without it.
///
/// SPEC CORRECTION (verified against the ACA decompile 2026-07-16): the technique map claimed
/// OnBlockPlaced "stamps the tapping player" — it does not. BlockEntitySpile stores ONLY a drip
/// timer; there is no owner anywhere, so ownership lives in the FOR side-state (ForPatches owns
/// the file), the Collier's Mark pattern.
///
/// REDESIGN 2026-08-11. The build shipped through 0.4.35 credited practice whenever the spile's
/// drip timer moved, which was wrong three ways — verified in the ACA decompile of SapDrip:
///
///     while (TotalHours - timer >= dripTime) {
///         timer += dripTime;                                    // advances FIRST
///         if (rand > dripChance || !seasons.Contains(month)) break;   // season gate AFTER
///         TryPutLiquid(...);                                    // return value discarded
///     }
///
///   1. the timer advances before the season gate, so an OUT-OF-SEASON tapline (pine Dec-Feb,
///      acacia Oct-Mar) paid full practice into an empty bucket;
///   2. a FULL container paid too, because ACA discards the litres-moved return;
///   3. dripTime is in in-game hours (~2 real minutes each at default calendar speed), so a
///      single spile ticked practice every couple of minutes for as long as its owner was online.
///
/// The shape now follows the material instead of the clock (RULED 2026-08-11):
///   • TAP (place the spile) — a small credit, and only the first time anyone ever taps that
///     trunk face. Place-break-replace on one site pays once, ever.
///   • DRIP — pays NOTHING. The postfix measures how much liquid actually entered the catch
///     container and banks it as pending litres against that container's position. Out of season
///     and full buckets both produce a zero delta and therefore nothing.
///   • COLLECT — the real credit, paid to WHOEVER TAKES THE SAP OUT, scaled by litres removed.
///     Crediting the collector rather than the spile's owner matches FOR's existing Patch
///     Stewardship posture (whoever picks your patch, their hands decide what it costs); a
///     tapline inside a land claim is protected by the claim, which is the intended answer.
///
/// The owner map survives only to drive the Untrained stewardship liability on the drip rate.
/// </summary>
public static class ForAcaPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    private static Type? spileBlockType;

    /// <summary>ACA's own catch-container geometry, resolved once. SapDrip runs every 5 seconds
    /// per placed spile, so this is a hot path — no name resolution per tick.</summary>
    private static System.Reflection.MethodInfo? posForwardMethod;
    private static System.Reflection.FieldInfo? timerField;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("aculinaryartillery")) return;

        spileBlockType = AccessTools.TypeByName("ACulinaryArtillery.BlockSpile");
        var spileBe = AccessTools.TypeByName("ACulinaryArtillery.BlockEntitySpile");
        var sapDrip = spileBe == null ? null : AccessTools.Method(spileBe, "SapDrip");
        posForwardMethod = spileBe == null ? null : AccessTools.Method(spileBe, "posForward");
        timerField = spileBe == null ? null : AccessTools.Field(spileBe, "timer");

        if (posForwardMethod == null || timerField == null)
        {
            TcmLog.Warn(api, "aculinaryartillery spile internals moved (posForward/timer); FOR tapping inactive");
            return;
        }
        var doPlace = AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock));
        var interact = AccessTools.Method(typeof(BlockLiquidContainerBase),
            nameof(BlockLiquidContainerBase.OnBlockInteractStart));
        var broken = AccessTools.Method(typeof(Block), nameof(Block.OnBlockBroken));

        if (spileBlockType == null || sapDrip == null || doPlace == null || interact == null || broken == null)
        {
            TcmLog.Warn(api, "aculinaryartillery present but the spile seams were not found; FOR tapping inactive");
            return;
        }

        harmony.Patch(doPlace, postfix: new HarmonyMethod(AccessTools.Method(typeof(SpilePlacePatch), "Postfix")));
        harmony.Patch(sapDrip,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SapDripPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(SapDripPatch), "Postfix")));
        harmony.Patch(interact,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SapCollectPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(SapCollectPatch), "Postfix")));
        harmony.Patch(broken, prefix: new HarmonyMethod(AccessTools.Method(typeof(SapContainerBreakPatch), "Prefix")));

        TcmLog.Info(api, "FOR tapping hooked to ACA (credit at the tap and at the collection, never at the drip)");
    }

    private static double ReadTimer(object spileBe)
        => timerField?.GetValue(spileBe) is double t ? t : 0;

    /// <summary>Litres of sap → the rawMultiplier that collection pays.</summary>
    private static double CollectMultiplier(double litres)
    {
        double per = ForDomain.Knob(ForDomain.SapLitresPerCredit, 0.5);
        double cap = ForDomain.Knob(ForDomain.SapCollectCap, 4.0);
        if (per <= 0) return 0;
        double mult = litres / per;
        return mult > cap ? cap : mult;
    }

    /// <summary>Current litres held at a placed liquid container, or 0 if the block is gone or is
    /// not a container any more (the empty-hand pickup path removes it mid-interaction).</summary>
    private static double LitresAt(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlock(pos) is not BlockLiquidContainerBase container) return 0;
        return container.GetCurrentLitres(pos);
    }

    /// <summary>Pays the collector for sap actually removed, capped by what the tapline banked.
    /// Clearing is atomic (TakePendingSap removes), so no seam can pay a haul twice.</summary>
    private static void CreditCollection(IPlayer player, BlockPos containerPos, double litresRemoved)
    {
        if (litresRemoved <= 0) return;
        double pending = ForPatches.TakePendingSap(containerPos);
        if (pending <= 0) return;

        // Only sap counts. If the bucket also held rainwater the player poured in, the pending
        // figure is still the ceiling, because it only ever grew from a real drip delta.
        double credited = Math.Min(pending, litresRemoved);
        if (credited <= 0) return;

        // Anything they left behind stays banked for the next visit.
        double leftover = pending - credited;
        if (leftover > 0) ForPatches.AddPendingSap(containerPos, leftover);

        // contextHash folds in the REMAINING pool, not just the position. Shift-clicking empties
        // a tapline one litre at a time, and a position-only hash would dedup every take after
        // the first inside the 90s window — the player would lose the sap and the practice both.
        // Leftover strictly decreases while a pool drains, so each take hashes differently, and a
        // genuine repeat is impossible anyway: the pool is consumed atomically, and once it hits
        // zero the entry is gone and HasPendingSap stops the seam before it ever gets here.
        Core?.Ledger?.Log(player, ForDomain.Code, ForDomain.TechSapCollecting,
            HashCode.Combine(containerPos.GetHashCode(), (int)Math.Round(leftover * 1000)),
            CollectMultiplier(credited));
    }

    /// <summary>Stamps the tapper and pays the siting credit. Block.DoPlaceBlock is a broad seam,
    /// so this exits on the type check first; it only ever runs for an actual spile placement.
    /// </summary>
    public static class SpilePlacePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
            if (spileBlockType == null || !spileBlockType.IsInstanceOfType(__instance)) return;

            // First spile ever driven into this trunk face: the siting is the skilled act.
            // Re-placing on a used site pays nothing, which is what stops place-break-replace.
            if (ForPatches.IsNewTapSite(blockSel.Position))
            {
                Core?.Ledger?.Log(byPlayer, ForDomain.Code, ForDomain.TechTapping,
                    blockSel.Position.GetHashCode());
            }

            ForPatches.RememberTap(blockSel.Position, byPlayer.PlayerUID);
        }
    }

    /// <summary>Banks what the tapline actually produced. Measures the catch container's contents
    /// across SapDrip rather than trusting the drip timer, because the timer advances out of
    /// season and on a full bucket (see the class remarks). Credits no practice at all.</summary>
    public static class SapDripPatch
    {
        public struct DripState
        {
            public BlockPos? ContainerPos;
            public double Litres;
            public double Timer;
        }

        public static void Prefix(object __instance, out DripState __state)
        {
            __state = default;
            if (__instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;

            // ACA's own geometry: the catch container is one below, offset by the spile's facing.
            // Calling ACA's method rather than reimplementing it keeps us correct if they rotate.
            if (posForwardMethod?.Invoke(__instance, new object[] { 0, -1, 0 }) is not BlockPos container) return;

            __state.ContainerPos = container;
            __state.Litres = LitresAt(be.Api.World, container);
            __state.Timer = ReadTimer(__instance);
        }

        public static void Postfix(object __instance, DripState __state)
        {
            if (__instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            if (__state.ContainerPos == null) return;

            double gained = LitresAt(be.Api.World, __state.ContainerPos) - __state.Litres;
            if (gained > 0) ForPatches.AddPendingSap(__state.ContainerPos, gained);

            // Stewardship liability: an Untrained-placed tapline runs SLOW, never dry. Pushing
            // the drip timer ahead of the calendar delays future drips (~two-thirds output at
            // the default 0.5); it cannot kill the spile, the log segment, or a resin node —
            // those are worldgen-precious (ruled 2026-07-16). Keyed on the OWNER, which is the
            // one thing the owner map is still for.
            double now = ReadTimer(__instance);
            if (now <= __state.Timer) return;

            string? uid = ForPatches.TapOwner(be.Pos);
            if (uid == null) return; // pre-existing spile with no recorded tapper: stays vanilla
            IPlayer? owner = be.Api.World.PlayerByUid(uid);
            if (owner == null) return;

            if (ForDomain.LevelOf(owner) == 0)
            {
                double slow = ForDomain.Knob(ForDomain.UntrainedTapSlowdown, 0.5);
                if (slow > 0) timerField!.SetValue(__instance, now + (now - __state.Timer) * slow);
            }
        }
    }

    /// <summary>The collection credit. One seam covers both ways a player empties a tapline:
    /// transferring into a held container (BlockLiquidContainerBase takes the content directly)
    /// and right-clicking with an empty hand (falls through to the RightClickPickup behavior,
    /// which removes the block with its contents — the after-reading is then simply zero).
    /// </summary>
    public static class SapCollectPatch
    {
        public static void Prefix(BlockSelection blockSel, IWorldAccessor world, out double __state)
        {
            __state = -1;
            if (world?.Side != EnumAppSide.Server || blockSel?.Position == null) return;
            if (!ForPatches.HasPendingSap(blockSel.Position)) return; // not a tapline catch
            __state = LitresAt(world, blockSel.Position);
        }

        public static void Postfix(BlockSelection blockSel, IWorldAccessor world, IPlayer byPlayer, double __state)
        {
            if (__state < 0 || byPlayer == null || blockSel?.Position == null) return;
            double removed = __state - LitresAt(world, blockSel.Position);
            if (removed > 0) CreditCollection(byPlayer, blockSel.Position, removed);
        }
    }

    /// <summary>Breaking the catch container is a collection too — vanilla drops it with its
    /// contents intact. Prefix, because after the break there is nothing left to measure.
    /// </summary>
    public static class SapContainerBreakPatch
    {
        public static void Prefix(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || pos == null) return;
            if (!ForPatches.HasPendingSap(pos)) return;

            double litres = LitresAt(world, pos);
            if (litres > 0) CreditCollection(byPlayer, pos, litres);
        }
    }
}

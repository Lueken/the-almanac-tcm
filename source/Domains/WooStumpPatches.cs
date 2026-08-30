using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO — THE STUMP (RULED 2026-08-30; brief: projects/briefs/idg-divorce Phase 4, seam study:
/// fallingtree-seam-study.md). Felling a tree at the base leaves the butt in the ground as a
/// distinct almanactcm:stump-{wood} block: the fell set already starts at the strike point in
/// both vanilla and FallingTree, so the whole mechanic is keeping the base block from being
/// removed and swapping it in place.
///
/// Three seams plus a finalizer, all server-side:
///  - ARM: priority-first prefix on ItemAxe.OnBlockBrokenWith decides ONCE, before anything
///    breaks, whether this strike leaves a stump (true base, real tree above, stump variant
///    exists), and stashes the position.
///  - SWAP: prefix on Block.OnBlockBroken cancels the armed base's removal and SetBlocks the
///    stump in its place. One prefix covers every removal route: FallingTree's bulk loop,
///    /ftplace false's vanilla loop, and FallingTree absent entirely. Never BreakBlock here —
///    that re-enters this prefix disarmed and pays the player the base log on top of the stump.
///  - SUPPRESS (FallingTree only): prefix on ServerMain.SpawnEntity drops the base position's
///    EntityBlockFalling. Mandatory, not cosmetic: that entity's Initialize does
///    SetBlock(0, initialPos) regardless of what the break path did, which would wipe the stump
///    and then drop a free log on impact.
///  - DISARM: finalizer on ItemAxe.OnBlockBrokenWith clears all state every swing, even when
///    FallingTree's own prefix returned false (finalizers still run) or something threw mid-fell.
///
/// The WOO felling credit is untouched by design: FellSwingPatch stashes in its prefix and banks
/// in its finalizer, and FellingPracticePatch's postfix on Block.OnBlockBroken still runs when
/// our prefix returns false (Harmony runs postfixes regardless), with __instance still the log.
/// The stump block itself carries NO treeFellingGroupCode, so FindTree never sweeps it into a
/// neighbouring fell set and clearing one never pays felling practice.
/// </summary>
public static class WooStumpPatches
{
    // The break→fell chain is synchronous on the server thread (same assumption WooPatches and
    // WooFallingTreePatches document), so ThreadStatic state is safe per player.
    [ThreadStatic] private static BlockPos? swapPos;
    [ThreadStatic] private static int swapStumpId;
    [ThreadStatic] private static BlockPos? suppressPos;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var axeBreak = AccessTools.Method(typeof(ItemAxe), nameof(ItemAxe.OnBlockBrokenWith));
        var blockBroken = AccessTools.Method(typeof(Block), nameof(Block.OnBlockBroken));
        if (axeBreak == null || blockBroken == null)
        {
            TcmLog.Warn(api, "OnBlockBrokenWith/OnBlockBroken not found; WOO stumps inactive");
            return;
        }

        // Priority.First for the same reason FellSwingPatch documents: FallingTree's prefix on
        // this method runs the whole fell and returns false, and Harmony skips every prefix
        // after a false. The arm must land before the first BreakBlock does.
        harmony.Patch(axeBreak,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ArmPatch), "Prefix")) { priority = Priority.First },
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(ArmPatch), "Finalizer")));
        harmony.Patch(blockBroken,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SwapPatch), "Prefix")));
        TcmLog.Info(api, "WOO stumps hooked (felling leaves the butt standing as a stump)");

        // Vanilla felling spawns no entities; only FallingTree needs the base faller suppressed.
        if (!api.ModLoader.IsModEnabled("fallingtree")) return;

        var serverMain = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
        var spawnEntity = serverMain == null ? null
            : AccessTools.Method(serverMain, "SpawnEntity", new[] { typeof(Entity) });
        if (spawnEntity == null)
        {
            TcmLog.Warn(api, "ServerMain.SpawnEntity(Entity) not found; WOO stumps inactive with FallingTree present (the base faller would wipe the stump)");
            // Better no stump than a stump that vanishes and prints a free log: disarm the seams
            // by leaving state forever null — ArmPatch checks this flag.
            fallerSuppressed = false;
            return;
        }
        harmony.Patch(spawnEntity,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FallerSuppressPatch), "Prefix")));
        fallerSuppressed = true;
        TcmLog.Info(api, "WOO stumps: base-faller suppressor hooked (FallingTree present)");
    }

    /// <summary>False only when FallingTree is present AND its faller seam failed to resolve, in
    /// which case arming would produce a stump that the base faller immediately wipes. True when
    /// FallingTree is absent (vanilla felling spawns nothing to suppress).</summary>
    private static bool fallerSuppressed = true;

    /// <summary>Decides the whole strike up front. Every bail-out leaves state null, which makes
    /// the other two patches transparent. The wood-variant test quietly excludes bamboo, fern
    /// trees and fruit trees (color/type variants, never wood) exactly as the design ruled, and
    /// the missing-stump bail is the whole modded-species and redwood story: their base simply
    /// stays standing as the log it already is.</summary>
    public static class ArmPatch
    {
        public static void Prefix(IWorldAccessor world, Entity byEntity, BlockSelection blockSel)
        {
            swapPos = null;
            suppressPos = null;
            swapStumpId = 0;

            if (!fallerSuppressed) return;
            if (world.Side != EnumAppSide.Server || blockSel?.Position == null) return;
            if ((byEntity as EntityPlayer)?.Player == null) return;

            BlockPos pos = blockSel.Position;
            Block struck = world.BlockAccessor.GetBlock(pos);

            // Only living tree wood fells; placed/debarked/carved wood has no felling group.
            if (struck.BlockMaterial != EnumBlockMaterial.Wood) return;
            string? group = struck.Attributes?["treeFellingGroupCode"].AsString();
            if (string.IsNullOrEmpty(group)) return;

            // Not the base: the block below is part of the same tree. A mid-trunk strike fells
            // from the strike point (shipped behaviour, both pipelines) and converts nothing —
            // a stump with root flare six blocks up a trunk is nonsense.
            Block below = world.BlockAccessor.GetBlock(pos.DownCopy());
            if (below.Attributes?["treeFellingGroupCode"].AsString() == group) return;

            // Lone log: nothing of this tree stands above, so the fell set is empty and vanilla
            // falls through to a NORMAL break routed through Block.OnBlockBroken — the method the
            // swap prefix cancels. Arming here would make the block unbreakable-into-stump on a
            // strike the player meant as an ordinary break. Sharpest failure mode; bail.
            Block above = world.BlockAccessor.GetBlock(pos.UpCopy());
            if (above.BlockMaterial != EnumBlockMaterial.Wood
                || above.Attributes?["treeFellingGroupCode"].AsString() != group) return;

            // Variant lookup, not code-part indexing: works for any block declaring a wood
            // variantgroup, and returns null for bamboo/fern/fruit trees, which bail here.
            string? wood = struck.Variant["wood"];
            if (string.IsNullOrEmpty(wood)) return;
            Block stump = world.GetBlock(new AssetLocation("almanactcm", "stump-" + wood));
            if (stump == null) return;

            swapPos = pos.Copy();
            suppressPos = pos.Copy();
            swapStumpId = stump.BlockId;
            TcmLog.Cat(world.Api, TcmLog.Hooks, $"WOO stump armed: {struck.Code} at {pos} -> {stump.Code}");
        }

        /// <summary>Finalizer, not a postfix: must clear even on the FallingTree false-prefix
        /// path and on a mid-fell throw, or the next swing inherits a stale arm and eats one
        /// unrelated break. Runs after FallingTree's own postfix has spawned the fallers, so
        /// suppressPos is still live when the base faller arrives on the /ftplace false path.</summary>
        public static void Finalizer()
        {
            swapPos = null;
            suppressPos = null;
            swapStumpId = 0;
        }
    }

    /// <summary>The swap. Returning false skips SpawnDropsAndRemoveBlock entirely — no drops, no
    /// item entities, no SetBlock(0) — and the SetBlock overwrites the log in place. Disarms
    /// before writing, so a re-entrant call could at most pass through transparently. Runs before
    /// FellingPracticePatch's postfix on this same method, which still counts the base log for
    /// the felling credit (postfixes run regardless of a false prefix).</summary>
    public static class SwapPatch
    {
        public static bool Prefix(IWorldAccessor world, BlockPos pos)
        {
            if (swapPos == null || !swapPos.Equals(pos)) return true;
            if (world.Side != EnumAppSide.Server) return true;

            int stumpId = swapStumpId;
            swapPos = null;
            world.BlockAccessor.SetBlock(stumpId, pos);
            TcmLog.Cat(world.Api, TcmLog.Hooks, $"WOO stump set at {pos}");
            return false;
        }
    }

    /// <summary>Drops the base position's falling-block entity before it exists. Its Initialize
    /// runs SetBlock(0, initialPos) unconditionally (DoRemoveBlock defaults true), which would
    /// erase the stump the swap just placed, and its impact would then place a free log. Bound
    /// by index, not name: the parameter name on ServerMain.SpawnEntity is nobody's contract
    /// (the aqueducts lesson).</summary>
    public static class FallerSuppressPatch
    {
        public static bool Prefix(Entity __0)
        {
            if (suppressPos == null) return true;
            if (__0 is not EntityBlockFalling faller) return true;
            if (faller.initialPos == null || !suppressPos.Equals(faller.initialPos)) return true;

            suppressPos = null; // one base, one faller; shrink the window immediately
            TcmLog.Cat(faller.Api, TcmLog.Hooks, $"WOO stump: base faller suppressed at {faller.initialPos}");
            return false;
        }
    }
}

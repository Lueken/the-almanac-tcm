using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO always-present (vanilla) hooks: felling + planting practice and the leaf stick/sapling
/// yield (Axis 4/1 + the Axis 6 windfall). The IM axe-stamina axis rides the shared ToolFactor
/// registry (see MinConditionalPatches); the directional-felling signature lives in
/// WooFallingTreePatches. IDG processing verbs and the Collier's Mark are deferred to Phase 2/3.
/// </summary>
public static class WooPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    private static double Knob(string key, double fallback) => WooDomain.Knob(key, fallback);

    // ============================================================ practice verbs

    // ---------------------------------------------------------- the felling swing
    //
    // Vanilla's axe is not a per-block tool: ItemAxe.OnBlockBrokenWith flood-fills the whole tree
    // and breaks every position in ONE synchronous loop (up to FindTree's 2500-block cap). The
    // felling grant rides Block.OnBlockBroken, so a single swing on a large tree used to fire
    // hundreds of practice events in one tick, each one a chat packet, a toast packet (and a
    // client-side text-texture regen), and a debug write. That froze a singleplayer client outright
    // on a very large tree (LauCaRo, 2026-07-28). The swing is now ONE event: the batch counts logs
    // while vanilla's loop runs and banks once in the finalizer, scaled by WooDomain.FellMultiplier.
    //
    // The break→fell chain is synchronous on the server thread (same assumption WooFallingTreePatches
    // documents), so ThreadStatic state is safe and cannot leak between players.

    [ThreadStatic] private static bool fellBatching;
    [ThreadStatic] private static int fellLogCount;
    [ThreadStatic] private static string? fellPlayerUid;
    [ThreadStatic] private static BlockPos? fellBasePos;

    /// <summary>Wood that is part of a living tree, as vanilla itself defines it. Placed,
    /// debarked, carved and crafted wood carry no treeFellingGroupCode and never count.</summary>
    private static bool IsStandingTreeWood(Block block)
    {
        if (block.BlockMaterial != EnumBlockMaterial.Wood) return false;
        string? fellingGroup = block.Attributes?["treeFellingGroupCode"].AsString();
        return !string.IsNullOrEmpty(fellingGroup);
    }

    /// <summary>Only a STANDING TREE counts as felling (RULED 2026-07-28: "if I used wood in my
    /// house and break it, no XP, only on the tree felling").
    ///
    /// The test is vanilla's own tree marker, the `treeFellingGroupCode` attribute that
    /// ItemAxe.FindTree uses to decide what belongs to a tree. Vanilla sets it by type on the
    /// GROWN variants only (`"log-grown-*": "{wood}"`, `"*-grown-*"` for logsection), so placed
    /// and debarked wood is excluded by construction.
    ///
    /// This replaces the old `is BlockLog || code starts with "log"` test, which leaked twice:
    /// BlockLog is the class of BOTH log-grown-* and log-placed-*, and the prefix additionally
    /// matched logquad-placed-* and logsection-placed-*, so tearing down a log cabin farmed
    /// felling. It is also STRICTER and BROADER at once: a mod trunk block named anything at all
    /// now counts as long as it is a real tree, and a decorative block named "log-something"
    /// no longer does.
    ///
    /// Inside an axe swing this only COUNTS; the single coalesced grant lands in FellSwingPatch's
    /// finalizer.</summary>
    [HarmonyPatch(typeof(Block), nameof(Block.OnBlockBroken))]
    public static class FellingPracticePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null) return;
            if (!IsStandingTreeWood(__instance)) return;

            // Inside the swing that felled this tree: count, do not grant.
            if (fellBatching && fellPlayerUid == byPlayer.PlayerUID)
            {
                fellLogCount++;
                return;
            }

            // Outside a swing (hand-broken log, non-axe tool, another mod's break): unchanged.
            Core?.Ledger?.Log(byPlayer, WooDomain.Code, WooDomain.TechFelling, pos.GetHashCode());
        }
    }

    /// <summary>Wraps one axe swing so the whole tree banks as a single practice event. Always on
    /// (this is vanilla behaviour, not a mod seam); FallingTree's path runs through the same method,
    /// so it is covered too. Coexists with WooFallingTreePatches' Priority.First prefix on this
    /// method, where that one stashes the fall direction, this one counts logs.</summary>
    [HarmonyPatch(typeof(ItemAxe), nameof(ItemAxe.OnBlockBrokenWith))]
    public static class FellSwingPatch
    {
        /// <summary>Priority.First is REQUIRED, not tidiness. FallingTree replaces vanilla felling
        /// wholesale: its own prefix on this method breaks every log itself
        /// (FallingTree.AxePatch.OnBlockBrokenWith_Prefix → BlockAccessor.BreakBlock) and then
        /// returns false. Harmony skips every prefix AFTER one that returns false, so at default
        /// priority our batch would be a coin flip on patch order, and on the losing flip the
        /// whole redwood banks one grant per quarter-log again, which is the exact storm this
        /// patch exists to stop. Running first also guarantees the flag is set before the first
        /// BreakBlock lands. Same reason WooFallingTreePatches stashes at Priority.First.</summary>
        [HarmonyPriority(Priority.First)]
        public static void Prefix(IWorldAccessor world, Entity byEntity, BlockSelection blockSel)
        {
            fellBatching = false;
            fellLogCount = 0;
            fellPlayerUid = null;
            fellBasePos = null;

            if (world.Side != EnumAppSide.Server || blockSel?.Position == null) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return;

            fellBatching = true;
            fellPlayerUid = player.PlayerUID;
            fellBasePos = blockSel.Position.Copy();
        }

        /// <summary>Finalizer, not a postfix: the batch flag must be cleared and the grant banked
        /// even if vanilla or another mod throws mid-fell, or the next swing would inherit a stale
        /// batch and silently swallow its grants. Finalizers also still run when a prefix returned
        /// false, which is exactly the FallingTree path.</summary>
        public static void Finalizer(IWorldAccessor world)
        {
            if (!fellBatching) return;

            int logs = fellLogCount;
            string? uid = fellPlayerUid;
            BlockPos? basePos = fellBasePos;
            fellBatching = false;
            fellLogCount = 0;
            fellPlayerUid = null;
            fellBasePos = null;

            if (logs <= 0 || uid == null) return;
            IPlayer? player = world.PlayerByUid(uid);
            if (player == null) return;

            // One event per swing, keyed on the struck block so re-felling the same stump inside
            // the dedup window still collapses the way any repeated context does.
            Core?.Ledger?.Log(player, WooDomain.Code, WooDomain.TechFelling,
                basePos?.GetHashCode() ?? 0, WooDomain.FellMultiplier(logs));
        }
    }

    /// <summary>Planting: shift-interacting a tree seed onto ground. Low value, self-limiting
    /// (seeds are finite) — credited at the plant action per the ruling.</summary>
    [HarmonyPatch(typeof(ItemTreeSeed), nameof(ItemTreeSeed.OnHeldInteractStart))]
    public static class PlantingPracticePatch
    {
        // Only a subset of the params — Harmony injects by name and ignores the rest, so the
        // ref handHandling stays out of our signature (its mis-named ref param crashed Start once).
        public static void Postfix(EntityAgent byEntity, BlockSelection blockSel, bool firstEvent)
        {
            if (!firstEvent || blockSel == null) return;
            if (byEntity?.World?.Side != EnumAppSide.Server) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            if (player == null || !byEntity.Controls.Sneak) return; // planting is a shift-interact

            Core?.Ledger?.Log(player, WooDomain.Code, WooDomain.TechPlanting, blockSel.Position.GetHashCode());
        }
    }

    // ============================================================ Axis 4/1 + 6 — leaf yield

    /// <summary>Scales the stick/tree-seed drops from leaf blocks by WOO rank (Untrained shreds
    /// the canopy, GM a modest bonus), plus a GM-weighted windfall chance for an extra. Rides the
    /// same GetDrops path as felled leaves (verified) and coexists with FallingTree's prefix.</summary>
    [HarmonyPatch(typeof(Block), nameof(Block.GetDrops))]
    public static class LeafYieldPatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, ItemStack[] __result)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null || __result == null) return;
            if (__instance.BlockMaterial != EnumBlockMaterial.Leaves) return;

            int level = WooDomain.LevelOf(byPlayer);
            double factor = WooDomain.RankLinear(level,
                Knob(WooDomain.LeafYieldUntrained, 0.8), Knob(WooDomain.LeafYieldGm, 1.2));
            double windfall = Knob(WooDomain.WindfallGmChance, 0.15) * WooDomain.RankProgress(level);
            if (factor == 1.0 && windfall <= 0) return;

            foreach (ItemStack stack in __result)
            {
                if (!IsForageDrop(stack)) continue;
                stack.StackSize = ScaleCount(stack.StackSize, factor, world.Rand);
                if (windfall > 0 && world.Rand.NextDouble() < windfall) stack.StackSize += 1;
            }
        }

        private static bool IsForageDrop(ItemStack? stack)
        {
            if (stack?.Collectible == null) return false;
            if (stack.Collectible is ItemTreeSeed) return true;
            string path = stack.Collectible.Code?.Path ?? "";
            return path.Contains("stick") || path.Contains("sapling");
        }

        /// <summary>Expected = n·factor, floored with the fractional part rolled — so a modest
        /// multiplier reads as a chance, never a guaranteed multiple (principle 3).</summary>
        private static int ScaleCount(int n, double factor, System.Random rand)
        {
            double expected = n * factor;
            int whole = (int)expected;
            if (rand.NextDouble() < expected - whole) whole++;
            return System.Math.Max(0, whole);
        }
    }
}

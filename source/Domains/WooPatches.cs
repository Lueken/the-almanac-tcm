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
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    private static double Knob(string key, double fallback) => WooDomain.Knob(key, fallback);

    // ============================================================ practice verbs

    /// <summary>Felling: each log break (the struck trunk and every downed log you break after it
    /// falls) is a WOO/felling event. Filtered to Wood so only logs count, not planks/furniture.</summary>
    [HarmonyPatch(typeof(Block), nameof(Block.OnBlockBroken))]
    public static class FellingPracticePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null) return;
            if (__instance.BlockMaterial != EnumBlockMaterial.Wood) return;

            Core?.Ledger?.Log(byPlayer, WooDomain.Code, WooDomain.TechFelling, pos.GetHashCode());
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

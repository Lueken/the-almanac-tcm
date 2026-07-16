using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO Axis 3 — DIRECTIONAL FELLING (the signature; RULED from Jeffrey's mock 2026-07-15).
/// A felled tree lands along the STRUCK FACE, rotated by a random angle drawn from a cone
/// whose WIDTH shrinks with WOO rank and whose CENTER biases from toward-player (Untrained,
/// lethal — FallingTree's impact damage kills) to away-from-player (GM, laid exactly where
/// aimed). Positioning + face choice is the input; rank is accuracy and safety.
///
/// FallingTree-conditional. Seam: a priority-first prefix on ItemAxe.OnBlockBrokenWith stashes
/// the feller + struck face and computes the skewed direction once; a prefix on FallingTree's
/// ConfigurePivotFaller overrides the (dirX, dirZ) it bakes into every falling log of that tree.
/// </summary>
public static class WooFallingTreePatches
{
    private const double Deg2Rad = Math.PI / 180.0;

    // The break→fell→ConfigurePivotFaller chain is synchronous on the server thread.
    [ThreadStatic] private static bool skewValid;
    [ThreadStatic] private static float skewX;
    [ThreadStatic] private static float skewZ;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("fallingtree")) return;

        var axeBreak = AccessTools.Method(typeof(ItemAxe), "OnBlockBrokenWith");
        var configurePivot = AccessTools.Method(
            AccessTools.TypeByName("FallingTree.FallingEntityPatch"), "ConfigurePivotFaller");
        if (axeBreak == null || configurePivot == null)
        {
            TcmLog.Warn(api, "fallingtree present but OnBlockBrokenWith/ConfigurePivotFaller not found; WOO directional felling inactive");
            return;
        }

        // Priority.First so our stash lands BEFORE FallingTree's own prefix runs the fell and
        // calls ConfigurePivotFaller.
        harmony.Patch(axeBreak,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FellStashPatch), "Prefix")) { priority = Priority.First });
        harmony.Patch(configurePivot,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(PivotDirPatch), "Prefix")));
        TcmLog.Info(api, "WOO directional felling hooked to FallingTree (rank-skewed fall direction)");
    }

    /// <summary>Stashes the skewed fall direction from the struck face + feller rank, once per
    /// fell (all logs of the tree share it). Resets at the top; the postfix clears the feller so
    /// non-axe ConfigurePivotFaller calls (cascades) never inherit a stale skew.</summary>
    public static class FellStashPatch
    {
        public static void Prefix(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel)
        {
            skewValid = false;
            if (world.Side != EnumAppSide.Server || blockSel?.Face == null) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return;

            Vec3i n = blockSel.Face.Normali;
            if (n.X == 0 && n.Z == 0) return; // up/down face — not a side chop; leave FallingTree's default

            (skewX, skewZ) = ComputeFall(player, blockSel.Position, n.X, n.Z, world.Rand);
            skewValid = true;
            TcmLog.Cat(world.Api, TcmLog.Hooks,
                $"WOO fell: face=({n.X},{n.Z}) WOO={WooDomain.LevelOf(player)} -> dir=({skewX:0.##},{skewZ:0.##})");
        }
        // No Postfix clear: the domino cascade (TryDomino) spawns the rest of the tree on LATER
        // ticks, so the skew must survive past this call. It's reset at the top of the next fell.
    }

    /// <summary>Redirects the WHOLE tree — every log, from both the initial SpawnFallers pass and
    /// the deferred TryDomino cascade (both route through here). FallingTree ignores its dirX/dirZ
    /// args (the topple is the pivot − treeCenter offset), so we recover the center from those args
    /// and re-aim the pivot, AND overwrite the per-entity drift attributes the physics reads.</summary>
    public static class PivotDirPatch
    {
        public static void Prefix(EntityBlockFalling faller, ref double pivotX, ref double pivotZ, float dirX, float dirZ)
        {
            if (!skewValid) return;
            double centerX = pivotX - dirX * 0.5;
            double centerZ = pivotZ - dirZ * 0.5;
            pivotX = centerX + skewX * 0.5;
            pivotZ = centerZ + skewZ * 0.5;

            // The per-log horizontal drift reads these; they were set to the default dir just before
            // this call, so overwrite them to match the rewritten pivot.
            faller.WatchedAttributes.SetFloat("fallingtree:dirX", skewX);
            faller.WatchedAttributes.SetFloat("fallingtree:dirZ", skewZ);
        }
    }

    /// <summary>The cone math: base = struck-face normal; center rotated toward the player at low
    /// rank / away at high rank; half-width shrinks with rank; a uniform draw inside the cone.</summary>
    private static (float x, float z) ComputeFall(IPlayer player, BlockPos treePos, int faceX, int faceZ, Random rand)
    {
        double baseAngle = Math.Atan2(faceZ, faceX);

        // Which rotational direction is "toward the player" from the face normal.
        double px = player.Entity.ServerPos.X - (treePos.X + 0.5);
        double pz = player.Entity.ServerPos.Z - (treePos.Z + 0.5);
        double toPlayer = NormalizeAngle(Math.Atan2(pz, px) - baseAngle);
        double towardSign = toPlayer >= 0 ? 1.0 : -1.0;

        int level = WooDomain.LevelOf(player);
        double spread = Lerp(WooDomain.Knob(WooDomain.FellSpreadUntrained, 85),
            WooDomain.Knob(WooDomain.FellSpreadGm, 6), WooDomain.RankProgress(level)) * Deg2Rad;
        double bias = BiasDegrees(level) * Deg2Rad * towardSign;
        double theta = (rand.NextDouble() * 2 - 1) * spread;

        double a = baseAngle + bias + theta;
        return ((float)Math.Cos(a), (float)Math.Sin(a));
    }

    /// <summary>Cone-centre bias (RULED 2026-07-15): leans TOWARD the feller the whole way up
    /// through Journeyman, reaches exactly zero at Master I, then rotates AWAY to the GM value.
    /// A plain untrained→GM lerp would cross zero back at Journeyman II, so this is piecewise.</summary>
    private static double BiasDegrees(int level)
    {
        double untrained = WooDomain.Knob(WooDomain.FellBiasUntrained, 35);
        double gm = WooDomain.Knob(WooDomain.FellBiasGm, -22);
        int masterEntry = 3 * Leveling.Domain.SubLevelsPerTier + 1; // Master I — the zero crossing
        int max = Leveling.Domain.MaxLevelDefault;

        if (level <= 0) return untrained;
        if (level >= max) return gm;
        // Untrained → Master I: toward the feller, decaying to zero.
        if (level <= masterEntry) return untrained * (1.0 - level / (double)masterEntry);
        // Master I → GM: rotate away from the feller.
        return gm * ((level - masterEntry) / (double)(max - masterEntry));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

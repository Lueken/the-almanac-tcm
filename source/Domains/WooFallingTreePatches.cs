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
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FellStashPatch), "Prefix")) { priority = Priority.First },
            postfix: new HarmonyMethod(AccessTools.Method(typeof(FellStashPatch), "Postfix")));
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

        public static void Postfix() => skewValid = false;
    }

    /// <summary>Redirects the fall by rewriting the PIVOT offset (FallingTree ignores its dirX/dirZ
    /// args — the topple direction is pivot − treeCenter). We recover the tree center from the
    /// original dir args and re-offset it toward our skewed direction; same pivot for every log.</summary>
    public static class PivotDirPatch
    {
        public static void Prefix(ref double pivotX, ref double pivotZ, float dirX, float dirZ)
        {
            if (!skewValid) return;
            double centerX = pivotX - dirX * 0.5;
            double centerZ = pivotZ - dirZ * 0.5;
            pivotX = centerX + skewX * 0.5;
            pivotZ = centerZ + skewZ * 0.5;
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

        double t = WooDomain.RankProgress(WooDomain.LevelOf(player));
        double spread = Lerp(WooDomain.Knob(WooDomain.FellSpreadUntrained, 85), WooDomain.Knob(WooDomain.FellSpreadGm, 6), t) * Deg2Rad;
        double bias = Lerp(WooDomain.Knob(WooDomain.FellBiasUntrained, 35), WooDomain.Knob(WooDomain.FellBiasGm, -22), t) * Deg2Rad * towardSign;
        double theta = (rand.NextDouble() * 2 - 1) * spread;

        double a = baseAngle + bias + theta;
        return ((float)Math.Cos(a), (float)Math.Sin(a));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

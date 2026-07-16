using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO — THE COLLIER (technique #8 + Axis 4 pit-yield + the Axis 1 botched-burn band).
/// Charcoal burning was adopted as a WOO technique 2026-07-09: the pit is credited to its
/// IGNITER, not to whoever digs the charcoal out, because vanilla already stores exactly that
/// (`BlockEntityCharcoalPit.startedByPlayerUid`, set at ignition).
///
/// **The seam (better than the design doc assumed).** The efficiency roll is a LOCAL inside
/// `ConvertPit`, which looks unpatchable — but `ConvertPit` hands it to
/// `BlockCharcoalPit.GetFirewoodQuantity(world, pos, ref NatFloat efficiency)`, which is PUBLIC,
/// takes the band **by ref**, and has exactly ONE caller in the whole game. So a postfix there
/// rewrites the roll for that pit without touching ConvertPit's body at all.
///
/// **RULED shape: raise the FLOOR, never the ceiling.** Vanilla is uniform[0.5, 1.0] — a perfect
/// burn is already possible by luck. Rank removes bad outcomes rather than inventing good ones
/// (the MET quench-reliability logic applied to yield), so a GM collier is *consistent*, not
/// lucky, and never exceeds vanilla's own conversion ceiling. Untrained is the one rank that also
/// drops the ceiling: that is the Axis 1 penalty leg.
///
/// All-vanilla seams, so no mod gate. Verified uncontested 2026-07-09 (no live mod touches
/// ConvertPit or GetFirewoodQuantity).
/// </summary>
public static class WooColliderPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    // ConvertPit → GetFirewoodQuantity is a synchronous same-thread call, and GetFirewoodQuantity
    // has NO other caller in the game, so a thread-static stashed here cannot leak into anything
    // else. Reset at the top of the ConvertPit prefix — deliberately no finalizer (see
    // MinConditionalPatches: a finalizer rewraps a method's exception handling and cost us the
    // 0.3.43 stamina regression).
    [ThreadStatic] private static IPlayer? collier;
    [ThreadStatic] private static int scaledBlocks;
    [ThreadStatic] private static BlockPos? pitPos;

    public static void PatchAll(ICoreAPI api, Harmony harmony)
    {
        var convertPit = AccessTools.Method(typeof(BlockEntityCharcoalPit), "ConvertPit");
        var firewoodQty = AccessTools.Method(typeof(BlockCharcoalPit), "GetFirewoodQuantity");
        if (convertPit == null || firewoodQty == null)
        {
            TcmLog.Warn(api, "charcoal pit seams (ConvertPit/GetFirewoodQuantity) not found; WOO collier inactive");
            return;
        }

        harmony.Patch(convertPit,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ConvertPitPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ConvertPitPatch), "Postfix")));
        harmony.Patch(firewoodQty,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(FirewoodEfficiencyPatch), "Postfix")));
        TcmLog.Info(api, "WOO collier hooked to the charcoal pit (burning verb + rank pit-yield band)");
    }

    /// <summary>Resolves the igniter for the pit about to convert, and credits the burn once the
    /// conversion actually happened.</summary>
    public static class ConvertPitPatch
    {
        public static void Prefix(BlockEntityCharcoalPit __instance)
        {
            collier = null;
            scaledBlocks = 0;
            pitPos = null;
            if (__instance?.Api?.Side != EnumAppSide.Server) return;

            string? uid = null;
            try { uid = Traverse.Create(__instance).Field("startedByPlayerUid").GetValue<string>(); }
            catch { return; }
            if (string.IsNullOrEmpty(uid)) return; // pit lit by fire spread, no owner — leave vanilla

            collier = __instance.Api.World.PlayerByUid(uid);
            pitPos = __instance.Pos?.Copy();
        }

        public static void Postfix()
        {
            // ConvertPit early-returns when WalkPit fails (a broken pit converts nothing) and a
            // postfix cannot see that. scaledBlocks is the honest witness: it only ticks from
            // inside the WalkPit callback, so >0 means the pit really converted.
            if (collier != null && scaledBlocks > 0 && pitPos != null)
            {
                Core?.Ledger?.Log(collier, WooDomain.Code, WooDomain.TechBurning, pitPos.GetHashCode());
            }
            collier = null;
            scaledBlocks = 0;
            pitPos = null;
        }
    }

    /// <summary>Rewrites the pit's efficiency band to the collier's rank. Postfix, because
    /// GetFirewoodQuantity ASSIGNS the band itself (reading the fuel's own "efficiency" item
    /// attribute, falling back to vanilla's uniform[0.5, 1.0]) — a prefix would be overwritten.</summary>
    public static class FirewoodEfficiencyPatch
    {
        public static void Postfix(BlockPos pos, ref NatFloat efficiency)
        {
            if (collier == null || efficiency == null) return;
            scaledBlocks++;

            int level = WooDomain.LevelOf(collier);
            float min = efficiency.avg - efficiency.var;
            float max = efficiency.avg + efficiency.var;

            // Applied as a DELTA against vanilla firewood's [0.5, 1.0] rather than overwriting, so
            // a modded premium fuel that ships its own band keeps its character instead of being
            // flattened to ours. No live asset defines one today, so in practice this reduces to
            // exactly the ruled table.
            float newMin = GameMath.Clamp(min + (FloorFor(level) - 0.5f), 0f, 1f);
            float newMax = GameMath.Clamp(max + (CeilFor(level) - 1f), 0f, 1f);
            if (newMax < newMin) newMax = newMin;

            efficiency = NatFloat.createUniform((newMin + newMax) / 2f, (newMax - newMin) / 2f);

            // Axis 4b: a GM burn stamps its piles. Recorded here rather than in the ConvertPit
            // postfix because this is the only place we see each firewood column position — and
            // ConvertPit places charcoal exactly where firewood was (IsFirewoodPile(lpos)).
            // We over-record on purpose: columns that run out of charcoal become air instead of a
            // pile, and those entries simply never resolve (the mark read validates the block is a
            // live BlockCharcoalPile) and get pruned at save. Cheaper than predicting the split.
            if (pos != null && IsGrandmaster(level))
            {
                WooColliersMark.Remember(pos, collier.PlayerName);
            }

            if (scaledBlocks == 1)
            {
                TcmLog.Cat(collier.Entity?.Api, TcmLog.Hooks,
                    $"WOO pit: {collier.PlayerName} WOO={level} -> efficiency [{newMin:0.##}, {newMax:0.##}]");
            }
        }
    }

    /// <summary>Per-TIER floor, matching the ruled table exactly (not a per-level lerp): Untrained
    /// 0.35 → Novice 0.5 (vanilla) → Apprentice 0.6 → Journeyman 0.7 → Master 0.8 → GM 0.85.</summary>
    private static float FloorFor(int level)
    {
        if (level <= 0) return (float)WooDomain.Knob(WooDomain.PitFloorUntrained, 0.35);
        int tier = (level - 1) / Leveling.Domain.SubLevelsPerTier;
        return tier switch
        {
            0 => (float)WooDomain.Knob(WooDomain.PitFloorNovice, 0.5),
            1 => (float)WooDomain.Knob(WooDomain.PitFloorApprentice, 0.6),
            2 => (float)WooDomain.Knob(WooDomain.PitFloorJourneyman, 0.7),
            3 => (float)WooDomain.Knob(WooDomain.PitFloorMaster, 0.8),
            _ => (float)WooDomain.Knob(WooDomain.PitFloorGm, 0.85),
        };
    }

    /// <summary>Only Untrained drops the ceiling (the Axis 1 botched-burn leg). Every trained rank
    /// keeps vanilla's 1.0 — skill removes bad burns, it never invents magic charcoal.</summary>
    private static float CeilFor(int level)
        => level <= 0 ? (float)WooDomain.Knob(WooDomain.PitCeilUntrained, 0.85) : 1f;

    /// <summary>The Collier's Mark is Grandmaster-only (summary table: unmarked at every rank
    /// below GM). GM is the terminal level, so this is the top tier, not a range.</summary>
    private static bool IsGrandmaster(int level)
        => level > 0 && (level - 1) / Leveling.Domain.SubLevelsPerTier >= Leveling.Domain.TierCount - 1;
}

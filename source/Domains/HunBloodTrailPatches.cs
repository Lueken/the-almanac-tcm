using System;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN blood vibrancy — a rank reward on the BloodTrail mod (bloodtrail-conditional, client-side).
/// A wounded animal's trail reads more strongly the better a hunter you are, so following blood is
/// a skill payoff rather than a flat mechanic. BloodTrail's client behaviour
/// (BloodTrail.src.Client.EntityBleedingBehaviorParticles) funnels every spawned drop through small
/// private getters; we postfix them and scale the result by the LOCAL player's HUN rank.
///
/// The curve is anchored so BloodTrail's own stock config shows at Journeyman I, with the trail
/// fainter/shorter below and richer/longer above (ruled 2026-07-17):
///   factor(level) = 1 + spread * (level - 9) / 8   ->   1.0 exactly at Journeyman I (level 9,
///   the ladder midpoint). Novice I = 1 - spread, Grandmaster = 1 + spread. Untrained clamps to
///   the Novice floor. spread comes from ConfigLib (TcmClientSettings), so the swing tunes in-game.
///
/// Because the behaviour is client-only, each player spawns their own trail from the shared bleed
/// state, so this changes only the local hunter's view and never touches anyone else's.
/// </summary>
public static class HunBloodTrailPatches
{
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("bloodtrail")) return;

        var t = AccessTools.TypeByName("BloodTrail.src.Client.EntityBleedingBehaviorParticles");
        if (t == null)
        {
            TcmLog.Warn(api, "bloodtrail present but EntityBleedingBehaviorParticles not found; HUN blood vibrancy inactive");
            return;
        }

        void Hook(string method, string postfix)
        {
            var m = AccessTools.Method(t, method);
            if (m == null)
            {
                TcmLog.Warn(api, $"bloodtrail {method} not found; that facet of HUN blood vibrancy inactive");
                return;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(BloodVibrancyPatch), postfix)));
        }

        // Persistence: how long each drop lingers. Visibility: drop count (size rides half the swing
        // so density carries it and drops do not balloon).
        Hook("GetBloodDuration", "DurationPostfix");
        Hook("GetMinBloodAmount", "AmountPostfix");
        Hook("GetMaxBloodAmount", "AmountPostfix");
        Hook("GetMinBloodSize", "SizePostfix");
        Hook("GetMaxBloodSize", "SizePostfix");
        TcmLog.Info(api, "HUN blood vibrancy hooked to bloodtrail (rank-scaled trail persistence + visibility)");
    }

    public static class BloodVibrancyPatch
    {
        /// <summary>The Journeyman-anchored rank multiplier. 1.0 at Journeyman I (stock BloodTrail),
        /// 1 - spread at Novice I, 1 + spread at Grandmaster; untrained clamps to the Novice floor.</summary>
        private static float Factor(float spread)
        {
            if (spread <= 0.0001f) return 1f;         // rank effect off: stock for everyone
            int level = HunDomain.ClientLevel();
            if (level < 1) level = 1;                 // untrained sits at the Novice floor, not below
            float f = 1f + spread * (level - 9) / 8f; // level 9 = Journeyman I = the ladder midpoint = 1.0
            return f < 0.05f ? 0.05f : f;
        }

        public static void DurationPostfix(ref float __result)
            => __result *= Factor(TcmClientSettings.BloodPersistence);

        public static void AmountPostfix(ref int __result)
            => __result = Math.Max(1, (int)Math.Round(__result * Factor(TcmClientSettings.BloodVisibility)));

        public static void SizePostfix(ref float __result)
            => __result *= Factor(TcmClientSettings.BloodVisibility * 0.5f);
    }
}

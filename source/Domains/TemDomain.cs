using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// TEM — "Temporal" defaults (rank-bonus-design.md §TEM; technique-maps §TEM RULED 2026-07-08). The
/// temporal cousin of BRE: two genuinely-vanilla verbs, thin and deep. TEM is INFORMATION + RESILIENCE,
/// never power — a Storm-Warden reads the sky, weathers what a novice blacks out under, and keeps the
/// region's temporal machinery running on the fewest gears. No yield, no combat, no radar, no provenance.
///
/// Two verbs (both [vanilla], so TEM has a real floor and never goes dormant): #1 rift warding (fuel/
/// toggle a rift ward), #2 temporal-machinery mending (feed gears into a broken translocator / recharge
/// a spent teleporter). betterjonasdevices deepens both without adding a verb. `m` = 2.
///
/// The Storm-Warden (Axis 6) is a CAPABILITY signature, not a mark:
///   • Storm-Sense (the spine) — a rank-sharpened forecast on the real scheduled storm data
///     (nextStormTotalDays / nextStormStrength): a GM feels a Heavy storm coming a day-plus before the
///     village hears it; strength-distinct from Journeyman; storm-blind below Novice. No radar (the live
///     rift list is a hard REJECT).
///   • Stability-loss resistance (Axis 3) — weathers storms upright, floored (never immune).
///   • Gear economy (Axis 2) — the fewest gears per translocator repair + ward fuel that lasts longer.
///
/// SERVER-WIDE COORDINATION (why this domain is careful): the Axis-3 resistance writes the vanilla-
/// integrator stat `stabilityLossMul`, which SpecializedClasses (live on The Quire) applies via its own
/// prefix — TEM contributes only its RANK delta under the "almanactcm" source key, ADDING to SC's
/// archivist class trait, never re-scaling it (double-scale watch at beta tuning). Because that
/// resistance rides ONLY the integrator's ambient/storm loss, every DELIBERATE stability spend is exempt
/// BY CONSTRUCTION: Rustbound Magic's meditation drain (live, direct WatchedAttribute writes) and the
/// Marginalia Conjunction recipe spends (future) both bypass the integrator, so TEM never shields the
/// cost a player chose to pay. The one reserved cross-mod seam is the manifestation-resist PROC
/// (<see cref="ManifestResistChance"/>) — a chance to shrug off an INVOLUNTARY manifestation drain
/// (rust-mob, devastation-thinness), which Conjunction's involuntary drains will call when it ships.
///
/// Affinity (archivist +2 the GM door / clockmaker,quarrier +1 / six -1 ceilings) already lives in
/// AffinitySystem — no TEM affinity hole, no override needed.
/// </summary>
public static class TemDomain
{
    public const string Code = "TEM";

    public const string TechWarding = "warding";   // BlockEntityRiftWard.OnInteract (fuel/toggle)
    public const string TechRepair = "repair";      // BlockEntityStaticTranslocator.DoRepair + teleporter recharge

    // ---- Axis 2 gear economy knob keys.
    /// <summary>temporalGearTLRepairCost multiplier at Untrained — a beginner burns MORE gears repairing a
    /// translocator (&gt;1). Clears to 1.0 (vanilla 4 interactions) at Novice.</summary>
    public const string GearCostUntrained = "gearCostUntrained";
    /// <summary>temporalGearTLRepairCost at Grandmaster — the fewest gears (&lt;1), floored above zero (a
    /// translocator always costs some gears; no free transit).</summary>
    public const string GearCostGm = "gearCostGm";
    /// <summary>Ward-fuel multiplier at Untrained — a beginner's fuelling burns a little faster (&lt;1).</summary>
    public const string WardFuelUntrained = "wardFuelUntrained";
    /// <summary>Ward-fuel multiplier at Grandmaster — a master's gear fuels a ward longer (&gt;1).</summary>
    public const string WardFuelGm = "wardFuelGm";

    // ---- Axis 3 stability-loss resistance knob keys (the stabilityLossMul stat, SC-applied).
    /// <summary>stabilityLossMul at Untrained — thin-skinned to time, loses stability faster (&gt;1).</summary>
    public const string StabilityLossUntrained = "stabilityLossUntrained";
    /// <summary>stabilityLossMul at Grandmaster — weathers storms upright, loses slower (&lt;1), FLOORED so
    /// even a GM still loses stability in a Heavy storm (never immune). Kept modest for the SC archivist
    /// double-scale watch.</summary>
    public const string StabilityLossGm = "stabilityLossGm";

    /// <summary>Grandmaster chance to shrug off an INVOLUNTARY manifestation drain event entirely (a proc,
    /// per Jeffrey's ruling — not a flat resist). The reserved cross-mod seam Conjunction's rust-mob /
    /// devastation drains call; 0 below Novice, climbing to this at GM.</summary>
    public const string ManifestResistGm = "manifestResistGm";

    /// <summary>Grandmaster storm-forecast lead, in in-game days, beyond vanilla's ~0.35-day notify. 0
    /// through Novice (storm-blind), climbing to this at GM (a day-plus). Strength-distinct from Journeyman.</summary>
    public const string StormSenseLeadGm = "stormSenseLeadGm";

    /// <summary>The forecast names the storm's STRENGTH from Journeyman up; below that it is a vague
    /// "something gathers." (TEM has NO provenance — wards/translocators are world machines, not signed
    /// products — so this is the one tiered threshold the domain uses.)</summary>
    public const int StrengthKnownLevel = 9;

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 2,
        // Temporal-machinery neighbourhood: metal (gears/parts), engineering (machinery), mining (the
        // rift/drifter context temporal gears come from).
        Adjacency = new List<string> { "MET", "ENG", "MIN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Per-session upkeep (21-day ward fuel): modest raw, small K (one refuel ~banks the session).
            [TechWarding] = new() { Raw = 3, K = 10 },
            // Rare (translocators are sparse worldgen finds): small K.
            [TechRepair] = new() { Raw = 3, K = 8 },
        },
        Bonus = new Dictionary<string, double>
        {
            [GearCostUntrained] = 1.30, [GearCostGm] = 0.60,
            [WardFuelUntrained] = 0.90, [WardFuelGm] = 1.20,
            // Resistance kept modest (SC archivist +2 stacks additively; double-scale watch at tuning).
            [StabilityLossUntrained] = 1.20, [StabilityLossGm] = 0.70,
            [ManifestResistGm] = 0.35,
            [StormSenseLeadGm] = 1.40,
        },
    };

    /// <summary>The shared rank curve: <paramref name="untrained"/> below Novice, 1.0 across Novice, a
    /// gentle linear climb from Apprentice to <paramref name="gm"/> at max level.</summary>
    private static double Curve(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int novice = Leveling.Domain.SubLevelsPerTier;       // 4
        if (level <= novice) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault;           // 20
        double t = (level - novice) / (double)(max - novice);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>temporalGearTLRepairCost multiplier for the given rank (GetBlended target): &gt;1 Untrained
    /// (more gears), 1.0 Novice, down to the GM value (fewer). Floored by the vanilla repair math.</summary>
    public static double GearCost(int level) => Curve(level, Knob(GearCostUntrained, 1.30), Knob(GearCostGm, 0.60));

    /// <summary>Ward-fuel multiplier for the given rank: a master's gear fuels a ward longer.</summary>
    public static double WardFuel(int level) => Curve(level, Knob(WardFuelUntrained, 0.90), Knob(WardFuelGm, 1.20));

    /// <summary>stabilityLossMul for the given rank (GetBlended target): &gt;1 Untrained (loses faster),
    /// 1.0 Novice, down to the GM value (weathers upright). Floored above zero — never immune.</summary>
    public static double StabilityLossMul(int level) => Curve(level, Knob(StabilityLossUntrained, 1.20), Knob(StabilityLossGm, 0.70));

    /// <summary>Chance (0..1) to shrug off an involuntary manifestation drain at the given rank: 0 through
    /// Novice, climbing to the GM value. The reserved manifestation-resist proc.</summary>
    public static double ManifestResistChance(int level)
    {
        if (level <= Leveling.Domain.SubLevelsPerTier) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault;
        int novice = Leveling.Domain.SubLevelsPerTier;
        double t = (level - novice) / (double)(max - novice);
        return t * Knob(ManifestResistGm, 0.35);
    }

    /// <summary>Storm-forecast lead in in-game days for the given rank: 0 through Novice (storm-blind),
    /// climbing to the GM value (a day-plus). Added on top of vanilla's own notify, never below it.</summary>
    public static double StormSenseLead(int level)
    {
        if (level <= Leveling.Domain.SubLevelsPerTier) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault;
        int novice = Leveling.Domain.SubLevelsPerTier;
        double t = (level - novice) / (double)(max - novice);
        return t * Knob(StormSenseLeadGm, 1.40);
    }

    /// <summary>Server-side TEM level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.Instance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>A Bonus knob, falling back to the shipped default if the server dropped it.</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.Instance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }
}

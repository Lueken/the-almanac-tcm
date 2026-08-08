using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// ENG — "Engineering" defaults (rank-bonus-design.md §ENG; technique-maps §ENG RULED 2026-07-08). The
/// mechanical-power trade: an engineer rigs the wind-catching face of a power network and keeps its
/// wearing parts serviced. A thin/deep vanilla-rooted domain — the razor cut the brief's ~11 "verbs"
/// down to two real ones: assembly (rigging) and maintenance (servicing wear).
///
/// Two verbs: #1 mechanical-power assembly [vanilla] (right-click a placed windmill rotor with sail
/// cloth, consume it, grow the sail — the genuine assembly signal, not mere placement; millwright's
/// enhanced/VAWT rotors are the same verb, pool), and #2 mechanical maintenance [wearandtear] (service
/// a disengaged wearing part — sew/wax/resin/nail it back). `m` = 2 (clamps to 1 without wearandtear:
/// vanilla mechanical power has no wear system). Assembly is the guaranteed vanilla floor; maintenance
/// vanishes with wearandtear.
///
/// The identity is THE MILLWRIGHT'S MARK (Axis 6): the INVERTED MET repair-gate. Where MET sends you
/// back to a master because a lesser repair STRIPS quality, ENG sends you back because a master's
/// SERVICE LASTS LONGER — a GM-serviced part decays slower (a lower DecayModifier persisted on the
/// per-BE part), and a lesser hand's later service resets it higher. The recurring economy is native
/// (parts decay forever). Plus repair EFFECTIVENESS (Axis 4): a master restores more durability per
/// repair item. No reliability rung (no probabilistic failure step), no material gate, no stamina.
///
/// Affinity (clockmaker +3 / malefactor +1 / butcher -1) already lives in AffinitySystem. Surpass note:
/// this rides the exact TryMaintenance / PartBonuses.DecayModifier / UpdateForRepair machinery
/// wearandtear's own xLib abilities use; with no live xSkills "mechanics" skill those abilities are
/// no-ops, so ENG's Harmony layer is the sole scaler (the unify ruling A, Jeffrey 2026-07-22).
/// </summary>
public static class EngDomain
{
    public const string Code = "ENG";

    public const string TechAssembly = "assembly";       // windmill-rotor sail rigging (consume-and-grow)
    public const string TechMaintenance = "maintenance";  // wearandtear part servicing

    // ---- Axis 4 repair-effectiveness + Axis 1 penalty knob keys (rides props.Strength via DoMaintenanceFor).
    /// <summary>Repair effectiveness at the Untrained end — a beginner restores LESS durability per repair
    /// item (the penalty). Clears to 1.0 at Novice.</summary>
    public const string RepairUntrained = "repairUntrained";
    /// <summary>Repair effectiveness at Grandmaster — a master restores more durability per repair item
    /// (fewer strips/wax/resin/thread over a machine's life).</summary>
    public const string RepairGm = "repairGm";

    // ---- Axis 6 Millwright's Mark decay lever knob keys (rides PartBonuses.DecayModifier).
    /// <summary>Post-service decay multiplier at the Untrained end — a part a beginner "fixes" wears out
    /// SOONER afterward (&gt;1 = faster decay). The penalty end of the GM signature.</summary>
    public const string DecayUntrained = "decayUntrained";
    /// <summary>Post-service decay multiplier at Grandmaster — a master-serviced part decays noticeably
    /// SLOWER afterward (&lt;1). The Millwright's Mark: the inverted repair-gate.</summary>
    public const string DecayGm = "decayGm";

    // ---- Axis 3 overheat-ignition knob keys (eng-overheat-design.md, RULED 2026-08-02). Per-check
    // chances at the 500ms companion tick; vanilla's own stub number is 0.03.
    // AMENDED 2026-08-05: GM no longer has absolute immunity. The old trait ("smokes, never burns")
    // made a Grandmaster's signature a physics exemption rather than a quality of work, and it made
    // the sellable service an exemption too. GM is now simply the best rank on a monotonic curve:
    // half of Master, and tunable like every other rank.
    public const string IgniteUntrained = "igniteUntrained";
    public const string IgniteNovice = "igniteNovice";           // also unattributed machines (parity)
    public const string IgniteJourneyman = "igniteJourneyman";
    public const string IgniteMaster = "igniteMaster";
    public const string IgniteGm = "igniteGm";

    /// <summary>Overspeed scaling, added 2026-08-06. The per-rank chances above are flat, so a part
    /// 2% past the burn line rolled at exactly the same rate as one 300% past it. Real friction has
    /// no such cliff: equilibrium temperature climbs smoothly with speed. The roll is now multiplied
    /// by clamp((effective - 5.5) / 5.5, floor, cap), so creeping over the line is a slow smoulder a
    /// builder can catch and gross overdrive is quick. Safe as knobs: ignition is server-side and the
    /// readout never renders the chance, so C-3 does not apply here the way it does to FireAt.</summary>
    public const string IgniteScaleFloor = "igniteScaleFloor";
    public const string IgniteScaleCap = "igniteScaleCap";
    /// <summary>Part decay baseline on a machine RIGGED by a Grandmaster, held until first service
    /// (the orphaned optional from ENG ruling 7, substrate = the rigger stamp).</summary>
    public const string GmAssembledDecay = "gmAssembledDecay";

    /// <summary>Provenance tiers (for the "Serviced by X" line, Journeyman up). Serviced by (J) ->
    /// Master-serviced by (M) -> the Millwright's Mark of (GM). Level thresholds. (The text line is a
    /// fast-follow; the mechanical decay lever below is the signature's teeth.)</summary>
    public const int ProvJourneyman = 9, ProvMaster = 13, ProvGm = 17;

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // small-m: 2 with wearandtear (assembly + maintenance); clamps to 1 without it (assembly is the
        // vanilla floor). BRE/PAN/ANI/MEL/MAS precedent.
        M = 2,
        // Mechanical-power neighbourhood: metal (parts), wood (axles/gears), masonry (millwork housing).
        Adjacency = new List<string> { "MET", "WOO", "MAS" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Per rotor/sail-assembly action (the staple recurring build): medium.
            [TechAssembly] = new() { Raw = 2, K = 16 },
            // Per repair (durability-proportional upkeep): medium, deduped so babysitting one part dedups.
            [TechMaintenance] = new() { Raw = 2, K = 16 },
        },
        Bonus = new Dictionary<string, double>
        {
            // Repair effectiveness (MET-honing posture).
            [RepairUntrained] = 0.90, [RepairGm] = 1.15,
            // The Millwright's Mark decay lever (inverted repair-gate).
            [DecayUntrained] = 1.10, [DecayGm] = 0.85,
            // Overheat ignition per 500ms check (0.03 = vanilla's stub parity).
            [IgniteUntrained] = 0.06, [IgniteNovice] = 0.03,
            [IgniteJourneyman] = 0.02, [IgniteMaster] = 0.012, [IgniteGm] = 0.006,
            // Overspeed multiplier bounds. Floor 0.05 gives roughly 27 minutes at a GM's rate for a
            // part barely over the line; cap 3.0 keeps grossly overdriven trains from being instant.
            [IgniteScaleFloor] = 0.05, [IgniteScaleCap] = 3.0,
            [GmAssembledDecay] = 0.92,
        },
    };

    /// <summary>The shared curve: <paramref name="untrained"/> below Novice, 1.0 across Novice, a gentle
    /// linear climb from Apprentice to <paramref name="gm"/> at max level. NERF-FIRST.</summary>
    private static double Curve(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int novice = Leveling.Domain.SubLevelsPerTier;       // 4
        if (level <= novice) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault;           // 20
        double t = (level - novice) / (double)(max - novice);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Durability restored per repair item, scaled by ENG rank: 0.90 Untrained (weaker repairs),
    /// 1.0 Novice, climbing to 1.15 at Grandmaster.</summary>
    public static double RepairMul(int level) => Curve(level, Knob(RepairUntrained, 0.90), Knob(RepairGm, 1.15));

    /// <summary>Post-service decay multiplier set on the part by ENG rank: 1.10 Untrained (a beginner's
    /// fix wears out sooner), 1.0 Novice, down to 0.85 at Grandmaster (a master's service lasts longer).
    /// Read by wearandtear's UpdateDecay (num *= DecayModifier), persisted per-part.</summary>
    public static double DecayMul(int level) => Curve(level, Knob(DecayUntrained, 1.10), Knob(DecayGm, 0.85));

    /// <summary>Server-side ENG level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>A Bonus knob, falling back to the shipped default if the server dropped it.</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.ServerInstance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }
}

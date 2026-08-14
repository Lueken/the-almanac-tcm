using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// COO — "Cooking" defaults (rank-bonus-design.md §COO, RULED 2026-07-09 with the tool-gate
/// study adopted 7/7; technique-maps §COO ruled 2026-07-08). The table half of the farm-to-table
/// trio: it turns FAR's crops and ANI's stock into meals.
///
/// Phase 1 (this build): the practice-granting verbs. The vanilla floor reaches before pottery
/// or metal (direct-heat #3, quern #7, rack drying #9 — exactly m=3). Player-attributed verbs
/// (quern crank, fruit press, prep table) grant in scope; the unattended-completion vessels
/// (meal pot, oven, direct-heat firepit) stamp the loading player and bank at the finish
/// (owner-at-action / bank-at-event, the MET smelt pattern). ACA/seafarer/stonebakeoven seams
/// ride PatchConditional and warn-and-skip where absent.
/// Quern milling is the ruled COO 50 / FAR 50 split — one listener grants both halves.
/// Phase 2: Proposal B — complexity class becomes the rank-bonus ceiling (no hard tool gate),
/// delivered through the satiety-modifier seam. Phase 3+: the Cook's Mark GM signature (slower
/// spoilage + the satiety edge).
///
/// NERF-FIRST: cooking never forbids eating. Fresh-spawn C0/C1 survival cooking stays ungated;
/// rank shows in outcomes (a master's food keeps and nourishes a touch more), never in a locked door.
/// </summary>
public static class CooDomain
{
    public const string Code = "COO";

    // Meal-pot, direct-heat, oven: unattended completion (owner stamp). The rest player-attributed.
    public const string TechMealPot = "mealpot";       // BlockCookingContainer.DoSmelt
    public const string TechDirectHeat = "directheat"; // CollectibleObject.DoSmelt (+ ACA override)
    public const string TechBaking = "baking";         // BlockEntityOven.IncrementallyBake (+ stonebakeoven)
    public const string TechGriddling = "griddling";   // seafarer BlockEntityGriddleHearth
    public const string TechMixing = "mixing";         // ACA BlockEntityMixingBowl.mixInput
    public const string TechMilling = "milling";       // BlockEntityQuern.grindInput (COO 50 / FAR 50)
    public const string TechJuicing = "juicing";       // BlockEntityFruitPress.OnBlockInteractStop
    public const string TechDrying = "drying";         // ACA meat hooks + seafarer drying frame
    public const string TechSalting = "salting";       // seafarer salt pan evaporation
    public const string TechPrep = "prep";             // seafarer prep-table assembly

    // ---- Phase 2 knobs (COO ladder RULED 2026-07-09; Proposal B: complexity class is the
    // rank-bonus ceiling, never a locked door). All live-editable in ModConfig/almanactcm/COO.json.

    // The complexity-class table (C0 bare fire -> C3 chain apparatus), per verb. Ruled as config
    // so stations can be re-classed without a rebuild.
    public const string CxMealpot = "cxMealpot";
    public const string CxDirectheat = "cxDirectheat";
    public const string CxBaking = "cxBaking";
    public const string CxGriddling = "cxGriddling";
    public const string CxMixing = "cxMixing";
    public const string CxSimmering = "cxSimmering"; // ACA saucepan (provenance cx only; simmering grants no practice yet)
    public const string CxMilling = "cxMilling";
    public const string CxJuicing = "cxJuicing";
    public const string CxDrying = "cxDrying";
    public const string CxSalting = "cxSalting";
    public const string CxPrep = "cxPrep";

    /// <summary>Axis 2 fuel economy (the MET fuel analog): burn-duration factor at the ends.</summary>
    public const string FuelUntrained = "fuelUntrained";
    public const string FuelGm = "fuelGm";
    /// <summary>Axis 1/3 char clock: how fast a FINISHED bake browns toward charred while it sits
    /// in heat. Untrained burns fast; GM sits long; floored, never zero.</summary>
    public const string CharUntrained = "charUntrained";
    public const string CharGm = "charGm";
    /// <summary>Axis 1 / Axis 6: perish-rate factor on food carrying the cook stamp. The
    /// Untrained end is the penalty (spoils faster); the GM end is the Cook's Mark signature.</summary>
    public const string SpoilUntrained = "spoilUntrained";
    public const string SpoilGm = "spoilGm";
    /// <summary>Axis 4 thrift: the extra-serving proc chance at GM (0 below Apprentice, linear).</summary>
    public const string ServingProcGm = "servingProcGm";
    /// <summary>The satiety/health edge at GM on a C3 dish (scales by cx/3 and rank; ~0 on C0).</summary>
    public const string SatietyGmC3 = "satietyGmC3";
    public const string HealthGmC3 = "healthGmC3";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3, // locked; pre-pottery breadth is exactly 3 (direct-heat, quern, rack drying)
        Adjacency = new List<string> { "FAR", "FIS", "FOR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // PACING RETUNE 2026-07-29 (playtest: "cooking is a tad slow"). The original numbers
            // gave the staple kitchen verbs FREQUENT-action K values (30/25 — the same K as FAR
            // tilling and planting), but cooking is not a frequent-action verb: you till thirty
            // blocks in a session and cook three meals. xp-engine-design §3.3 is explicit that K
            // must track real cadence ("rare-action domains get small K — one real session banks
            // most of the cap"), so this is the design doc applied, not an override of it.
            //
            // Reference point: actions to half of the breadth-phase per-technique cap = K / Raw.
            //   mealpot   10 -> 3.5      baking/griddling/juicing/prep  12.5 -> 5
            //   mixing    15 -> 6        milling  30 -> 10      directheat  30 -> 13
            // Roughly a 2x on a realistic cooking day; drying/salting were already session-tuned
            // (3-4 actions) and are the calibration target the rest now sit near, so they stand.

            // Staple verbs: the everyday kitchen, now at session cadence. Direct-heat stays the
            // cheap, spammiest row — its contextHash dedup must treat "charred meat x40" as one
            // context (ruled) — so it gets the smallest lift of the group.
            [TechMealPot] = new() { Raw = 4, K = 14 },
            [TechDirectHeat] = new() { Raw = 1.5, K = 20 },
            [TechMixing] = new() { Raw = 3, K = 18 },
            // Milling is genuinely repetitive (quern cranking) AND double-pays via the ruled
            // COO 50 / FAR 50 split, so it keeps the most conservative curve of the staples.
            [TechMilling] = new() { Raw = 2, K = 20 },
            // Medium verbs (built stations, one process).
            [TechBaking] = new() { Raw = 3, K = 15 },
            [TechGriddling] = new() { Raw = 3, K = 15 },
            [TechJuicing] = new() { Raw = 3, K = 15 },
            [TechPrep] = new() { Raw = 3, K = 15 },
            // Passive per-session pair (small K): one rack/pan cycle ~ banked.
            [TechDrying] = new() { Raw = 4, K = 12 },
            [TechSalting] = new() { Raw = 3, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            // The complexity table (tool-gate study census, ruled): C0 bare heat, C1 vessel,
            // C2 station, C3 chain apparatus.
            [CxMealpot] = 1, [CxDirectheat] = 0, [CxBaking] = 2, [CxGriddling] = 2,
            [CxMixing] = 3, [CxSimmering] = 2, [CxMilling] = 2, [CxJuicing] = 2, [CxDrying] = 2,
            [CxSalting] = 2, [CxPrep] = 3,
            // The ruled illustrative ends (MET numeric posture, playtest-tuned).
            [FuelUntrained] = 0.90, [FuelGm] = 1.15,
            [CharUntrained] = 1.5, [CharGm] = 0.5,
            [SpoilUntrained] = 1.15, [SpoilGm] = 0.70,
            [ServingProcGm] = 0.25,
            [SatietyGmC3] = 0.12, [HealthGmC3] = 0.05,
        },
    };

    /// <summary>The Apprentice-and-up reward curve: 0 through Novice (vanilla is not a bonus),
    /// then linear from Apprentice I (level 5) to 1.0 at max. Proposal B's rank half.</summary>
    public static double BonusT(int level)
    {
        const int start = 5;
        if (level < start) return 0;
        int max = Leveling.Domain.MaxLevelDefault;
        return (level - start) / (double)(max - start);
    }

    /// <summary>General rank curve (shared shape). Phase 2's satiety-modifier reward curve reads it.</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        return 1.0 + (level - 1) / (double)(max - 1) * (gm - 1.0);
    }

    /// <summary>Server-side COO level for a player (0 = Untrained when unknown).</summary>
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

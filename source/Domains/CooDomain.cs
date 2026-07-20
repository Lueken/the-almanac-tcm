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

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3, // locked; pre-pottery breadth is exactly 3 (direct-heat, quern, rack drying)
        Adjacency = new List<string> { "FAR", "FIS", "FOR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Staple verbs (large K): the everyday kitchen. Direct-heat is the spammiest row —
            // its contextHash dedup must treat "charred meat x40" as one context (ruled).
            [TechMealPot] = new() { Raw = 3, K = 30 },
            [TechDirectHeat] = new() { Raw = 1, K = 30 },
            [TechMixing] = new() { Raw = 2, K = 30 },
            [TechMilling] = new() { Raw = 1, K = 30 },
            // Medium verbs (built stations, one process).
            [TechBaking] = new() { Raw = 2, K = 25 },
            [TechGriddling] = new() { Raw = 2, K = 25 },
            [TechJuicing] = new() { Raw = 2, K = 25 },
            [TechPrep] = new() { Raw = 2, K = 25 },
            // Passive per-session pair (small K): one rack/pan cycle ~ banked.
            [TechDrying] = new() { Raw = 4, K = 12 },
            [TechSalting] = new() { Raw = 3, K = 12 },
        },
    };

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

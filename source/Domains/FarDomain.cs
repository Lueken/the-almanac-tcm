using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// FAR — "Farming & Husbandry" defaults (rank-bonus-design.md §FAR, RULED 2026-07-09;
/// technique-maps §FAR ruled 2026-07-08). The daily-husbandry half of the farm-to-table
/// trio (ANI raises lineages, FAR works the crops and tends the herd, COO cooks the yield).
///
/// Phase 1 (this build): the practice-granting verbs. The vanilla floor is all
/// player-attributed interaction/break hooks (till/plant/harvest/fertilize/milk/eggs/
/// orchard/beekeeping); the trough feed loop (#5) both banks FAR practice AND writes the
/// shared `raisedBy` owner stamp the unattended ANI birth reads (the recycled-xLib
/// attribution, written by TCM itself — xSkills is a design reference, never a runtime dep).
/// Shearing rides shearlib (conditional). Deferred to a Phase 1b: the success-gated graft
/// (owner-at-placement, outcome at a delayed tick), the primitivesurvival furrow override,
/// and the ithania vermiculture maintenance loop.
/// Phase 2: the penalty band (grafts die, shears wound, harvest returns less) + the feed
/// economy. Phase 3+: the Heirloom Seed GM signature.
///
/// Ruled boundary (FAR Q1 / ANI): FAR owns the feeding-to-offspring loop; the
/// generation-raising BIRTH is ANI's verb outright. One husbandry loop, split by moment.
/// </summary>
public static class FarDomain
{
    public const string Code = "FAR";

    // The 13 ruled verbs. Phase 1 wires all but the three deferred (graft/furrow/vermiculture).
    public const string TechTilling = "tilling";       // ItemHoe.DoTill (vanilla)
    public const string TechPlanting = "planting";     // BlockEntityFarmland.TryPlant
    public const string TechHarvesting = "harvesting"; // BlockCrop.OnBlockBroken (yield-proportional)
    public const string TechFertilizing = "fertilizing"; // BlockEntitySoilNutrition.OnBlockInteract
    public const string TechFeeding = "feeding";       // BlockEntityTrough.ConsumeOnePortion (writes raisedBy)
    public const string TechMilking = "milking";       // EntityBehaviorMilkable.MilkingComplete
    public const string TechEggs = "eggs";             // BlockEntityHenBox.OnInteract (spammy, heavy dedup)
    public const string TechGrafting = "grafting";     // fruit-tree propagation (DEFERRED to 1b)
    public const string TechOrchard = "orchard";       // BlockEntityFruitTreePart.OnBlockInteractStop
    public const string TechBeekeeping = "beekeeping"; // BlockSkep.OnBlockBroken harvest
    public const string TechShearing = "shearing";     // EntityBehaviorShearable.DoShear (shearlib)
    public const string TechFurrow = "furrow";         // primitivesurvival furrow (DEFERRED to 1b)
    public const string TechVermiculture = "vermiculture"; // ithania worm bin (DEFERRED to 1b)
    /// <summary>FAR's half of the ruled COO 50 / FAR 50 quern split (technique-maps COO #7).
    /// Granted by COO's quern listener at 0.5 raw each side; flour is farm produce too.</summary>
    public const string TechMilling = "milling";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3, // locked default; passes the fresh-spawn pre-metal check (till/plant/harvest)
        // The farm-to-table trio shares one adjacency neighbourhood.
        Adjacency = new List<string> { "ANI", "COO", "FOR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Staple per-action verbs: modest raw, K large enough that a day's fieldwork banks steadily.
            [TechTilling] = new() { Raw = 1, K = 30 },
            [TechPlanting] = new() { Raw = 1, K = 30 },
            // Harvest is yield-proportional (rawMultiplier at the call site): ripe = full, penultimate
            // partial, immature seed-only ~0. The outcome is the practice signal (continuous success-gate).
            [TechHarvesting] = new() { Raw = 3, K = 30 },
            [TechFertilizing] = new() { Raw = 1, K = 20 },
            // Per-session passive husbandry: small K, one feeding session banks most of its share.
            [TechFeeding] = new() { Raw = 2, K = 15 },
            [TechMilking] = new() { Raw = 2, K = 20 },
            // The weakest, spammiest row: low raw + heavy contextHash dedup (a coop sweep = one context).
            [TechEggs] = new() { Raw = 1, K = 12 },
            [TechGrafting] = new() { Raw = 6, K = 10 },
            [TechOrchard] = new() { Raw = 2, K = 20 },
            [TechBeekeeping] = new() { Raw = 5, K = 12 },
            [TechShearing] = new() { Raw = 2, K = 20 },
            [TechFurrow] = new() { Raw = 2, K = 15 },
            [TechVermiculture] = new() { Raw = 3, K = 12 },
            // The 50-share of the quern event (COO's listener grants both halves at 0.5 raw).
            [TechMilling] = new() { Raw = 1, K = 15 },
        },
    };

    /// <summary>General rank curve: untrained at level 0, exactly 1.0 at Novice I, linear to the
    /// GM value at max level (the shared domain shape, Phase 2 penalty/feed levers read it).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        return 1.0 + (level - 1) / (double)(max - 1) * (gm - 1.0);
    }

    /// <summary>Server-side FAR level for a player (0 = Untrained when unknown).</summary>
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

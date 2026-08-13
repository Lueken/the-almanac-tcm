using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// BRE — "Brewing & Fermentation" defaults (rank-bonus-design.md §BRE, RULED 2026-07-09;
/// technique-maps §BRE ruled 2026-07-08). The first half of the consumables cluster with ALC.
///
/// Two ruled verbs, both [vanilla]: sealed-vessel fermentation (barrel + fermentaria clay
/// fermenter, one verb two BEs) and distillation (boiler + condenser -> spirits). Both open at
/// copper age (apparatus-gated); no rank gate. Both complete unattended days after the seal/
/// ignite, so the acting player is captured at the seal packet / boiler interact and banked at
/// completion (owner-at-seal / owner-at-ignite).
///
/// The output-classified grant (RULED): a barrel seal grants by OUTPUT class — alcoholic
/// ferments (cider/mead/wine/spirit) = BRE 100; non-alcoholic preserves (pickle/brine/cured/
/// vinegar/cheese/yogurt) = COO 50 / BRE 50 (the pickling split). Distillation is always BRE 100.
///
/// The signature is BRE's identity axis, and it carries THE FRAMEWORK'S ONE RULED EXCEPTION: the
/// SPOILAGE TAPER. Vanilla ferments never fail; TCM adds a rank-scaled spoilage chance at
/// completion (bad ratios ruin a ferment) that does NOT snap to zero at Novice — it tapers, full
/// while Untrained, lowered through Novice, reaching ZERO at Journeyman I. That single lever is
/// both the Axis 1 penalty and the Axis 3 reliability spine. Plus reduced portions while Untrained
/// (clears normally at Novice), and the Brewer's Mark: durable "Cured by X" provenance on SOLID
/// preserves only (liquids merge and erase a mark, ruled).
///
/// Deferred thin (ruling flags all droppable): seal-time & boiler-fuel economy (Axis 2), input-
/// waste thrift (Axis 4), the exceptional-batch proc (Axis 6 GM, inert without a variant asset).
/// </summary>
public static class BreDomain
{
    public const string Code = "BRE";

    // The 2 ruled verbs, both [vanilla].
    public const string TechFermenting = "fermenting"; // BlockEntityBarrel seal -> TryCraftNow
    public const string TechDistilling = "distilling"; // BlockEntityBoiler -> Condenser.ReceiveDistillate

    // ---- Penalty + reliability (the spoilage taper, RULED exception) + Brewer's Mark knobs.
    /// <summary>Spoilage chance on an Untrained seal (bad ratios ruin the ferment): the batch voids
    /// at completion. Tapers linearly to ZERO at Journeyman I (the ruled exception — NOT snap-at-
    /// Novice). Distillation is exempt (spirits do not spoil-fail).</summary>
    public const string SpoilUntrained = "spoilUntrained";
    /// <summary>Output portion multiplier while Untrained (a beginner's batch comes up short even
    /// when it does not spoil). Clears normally at Novice I.</summary>
    public const string PortionUntrained = "portionUntrained";

    /// <summary>Provenance tiers (a mark means something from Journeyman up): the BRE curedBy tag on
    /// SOLID preserves. Cured by (J) -> Aged by (M) -> a masterwork-preserve line (GM).</summary>
    // Rank thresholds moved to Leveling/Rank.cs (2026-08-12): this was one of ten identical
    // `Rank.Journeyman = 9, Rank.Master = 13, Rank.Grandmaster = 17` triplets. Use Rank.Journeyman etc.

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 2, // small-m: techniqueCount (seal + distill), per the locked small-m rule
        // Consumables + husbandry neighbourhood.
        Adjacency = new List<string> { "COO", "ALC", "FAR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The staple, but each seal is a multi-day session; the contextHash keys on the OUTPUT
            // ferment type so sealing ten cider barrels banks as a few contexts, not one per vessel.
            [TechFermenting] = new() { Raw = 3, K = 15 },
            // Rare apparatus, per-session.
            [TechDistilling] = new() { Raw = 3, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            [SpoilUntrained] = 0.50,   // full spoilage chance at tier 0, tapering to 0 at Journeyman
            [PortionUntrained] = 0.75, // 25% fewer portions while Untrained
        },
    };

    /// <summary>The Apprentice-and-up reward curve (0 through Novice, linear to 1.0 at max).</summary>
    public static double BonusT(int level)
    {
        const int start = 5;
        if (level < start) return 0;
        int max = Leveling.Domain.MaxLevelDefault;
        return (level - start) / (double)(max - start);
    }

    /// <summary>The spoilage taper (THE RULED EXCEPTION): full at Untrained, lowered through Novice,
    /// ZERO from Journeyman I on. Linear from the Untrained chance at tier 0 to 0 at Journeyman.</summary>
    public static double SpoilChance(int tier)
    {
        if (tier >= Rank.Journeyman) return 0.0;
        double full = Knob(SpoilUntrained, 0.50);
        // CAREFUL. The second use is a SPAN, not a threshold: Rank.Journeyman (9) is the width of
        // the ramp from Untrained to zero, so it is a divisor here. The first use above is a real
        // threshold. Both are correct in LEVEL space and must stay there.
        // If anyone ever converts this mod to tier-scale comparisons, this line does NOT convert:
        // at tier scale the divisor becomes 2 and the ramp goes negative for levels 3-8, silently.
        // (Flagged 2026-08-12; it is the one site a mechanical level-to-tier pass would break.)
        return full * (1.0 - tier / (double)Rank.Journeyman); // full at 0, 0 at Journeyman I
    }

    /// <summary>Server-side BRE level for a player (0 = Untrained when unknown).</summary>
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

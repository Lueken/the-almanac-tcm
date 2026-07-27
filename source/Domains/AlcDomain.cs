using AlmanacTcm.Config;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// ALC — "Alchemy" defaults (rank-bonus-design.md §ALC, RULED 2026-07-11; AMENDMENT
/// 2026-07-22 for alchemy 2.1.11; technique-maps §ALC ruled 2026-07-08/-11). The other
/// half of the consumables cluster with BRE, and the map's thinnest industrial verb by
/// design ("all cooking is alchemy") — but a real HEALING-PRODUCT identity.
///
/// A vanilla-floor domain (never fully hides): the practised verb is remedy crafting
/// (poultices/bandages, [vanilla]); the deep breadth verb is industrialstory wet
/// chemistry ([industrialstory], mod-conditional). Potions (alchemy mod) ride the
/// re-pointed cauldron-cook seam as a co-grant. `m` = 2 on The Quire (industrialstory
/// present); a pure-vanilla server sees only remedy crafting and should read as 1.
///
/// The identity is THE ALCHEMIST'S BRAND (<see cref="AlcBrand"/>): remedy potency &amp;
/// duration, batch-stamped at the creating act, climbing with rank NERF-FIRST (Untrained
/// below the ingredient tier, Novice = vanilla, Apprentice→GM a modest climb). At
/// Grandmaster the batch carries an EMPHASIS — Potent (deeper strength) or Lasting
/// (longer duration) — set by the player's own toggle on the Alchemy page in the Almanac
/// book (see AlcEmphasis), frozen onto the batch at the creating act. (The original
/// ingredient-quantity idea was dead: potion recipes are hard-fixed at qty 1, no
/// concentration lever.) Plus the revive-HP climb (unbranded ~22% → hard 0.80 cap, held
/// even at GM) and exhausted-on-revive (Vigor).
///
/// Reliability is N/A (ratified) — wet chemistry is deterministic, the potion path a
/// no-fail cook. No material/rank gate (apparatus + fired-brick self-gate; GLA argument).
/// Affinity (florist +2 / vintner +1 / malefactor +1) already lives in AffinitySystem.
/// </summary>
public static class AlcDomain
{
    public const string Code = "ALC";

    // The 2 verbs: #2 remedy crafting [vanilla, always]; #1 wet chemistry [industrialstory].
    public const string TechRemedy = "remedy";       // grid-craft a healing item (GridRecipe.ConsumeInput)
    public const string TechChemistry = "chemistry";  // ApparatusReactionVesselEntity.FinishReaction

    // ---- The Alchemist's Brand ladder (Axis 1 penalty + Axis 6 identity) knob keys.
    /// <summary>Remedy potency (Health / potion StrengthMul) at the Untrained end — below the
    /// ingredient tier (a beginner's draught heals less). Clears to 1.0 at Novice I.</summary>
    public const string PotencyUntrained = "potencyUntrained";
    /// <summary>Remedy potency at Grandmaster: a modest climb on top of the recipe (NERF-FIRST —
    /// a little better, never a different item).</summary>
    public const string PotencyGm = "potencyGm";
    /// <summary>Remedy duration (EffectDurationSec / potion Duration) at Untrained — shorter.</summary>
    public const string DurationUntrained = "durationUntrained";
    /// <summary>Remedy duration at Grandmaster: the modest climb, the second scalable field.</summary>
    public const string DurationGm = "durationGm";
    /// <summary>The extra GM-emphasis bump: Potent adds it to potency, Lasting to duration. The
    /// choice itself is the player's book toggle (see <see cref="AlcEmphasis"/>), not ingredient
    /// quantity — potion recipes are hard-fixed at qty 1, so there is no concentration lever to read.</summary>
    public const string EmphasisBonus = "emphasisBonus";

    // ---- Revive extension (Axis 6, RULED 2026-07-11) knob keys.
    /// <summary>Revive-HP fraction of MaxHealth an unbranded/low remedy wakes a downed player on
    /// (wounded, vulnerable). Vanilla wakes at FULL; this is the NERF-FIRST floor.</summary>
    public const string ReviveUntrained = "reviveUntrained";
    /// <summary>Revive-HP fraction at Grandmaster — a HARD CAP, held even at GM ("80 stands"): a
    /// field revival never equals walking it off.</summary>
    public const string ReviveGm = "reviveGm";

    // ---- Wet-chemistry reaction fuel economy (Axis 2) knob keys — MET numbers, shared stove.
    public const string FuelEconomyUntrained = "fuelEconomyUntrained";
    public const string FuelEconomyApprentice = "fuelEconomyApprentice";
    public const string FuelEconomyGm = "fuelEconomyGm";

    /// <summary>Herb-rack preservation (Axis 4, alchemical output): a master loses fewer bundles to
    /// over-drying. A perish-style factor at GM (&lt;1 = better), riding the COO #9 drying mechanism.</summary>
    public const string HerbRackPreserveGm = "herbRackPreserveGm";

    /// <summary>Provenance tiers (a mark means something from Journeyman up): the ALC alcBy tag.
    /// Prepared by (J) -> Compounded by (M) -> a master-remedy line (GM). Level thresholds.</summary>
    public const int ProvJourneyman = 9, ProvMaster = 13, ProvGm = 17;

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // small-m: 2 with industrialstory (remedy + chemistry); a pure-vanilla server has only the
        // remedy verb and reads as 1 (the HUN available-technique clamp). The Quire ships industrialstory.
        M = 2,
        // Consumables + ingredient neighbourhood.
        Adjacency = new List<string> { "COO", "FOR", "BRE" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The vanilla floor: per-craft, cheap and repeatable, so modest raw with a real K ceiling.
            [TechRemedy] = new() { Raw = 2, K = 20 },
            // Rare metal-gated apparatus, per-session: the contextHash keys on the reaction session.
            [TechChemistry] = new() { Raw = 3, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            // The Alchemist's Brand ladder (MET/COO numeric posture, playtest-tuned).
            [PotencyUntrained] = 0.85, [PotencyGm] = 1.15,
            [DurationUntrained] = 0.85, [DurationGm] = 1.15,
            [EmphasisBonus] = 0.10,
            // Revive HP: unbranded ~22% -> hard 0.80 cap (RULED "80 stands").
            [ReviveUntrained] = 0.22, [ReviveGm] = 0.80,
            // Reaction fuel economy (Axis 2): the MET curve, shared host stove.
            [FuelEconomyUntrained] = -0.10, [FuelEconomyApprentice] = 0.03, [FuelEconomyGm] = 0.15,
            // Herb-rack preservation (Axis 4): a master over-dries less.
            [HerbRackPreserveGm] = 0.85,
        },
    };

    /// <summary>The shared remedy factor curve: <paramref name="untrained"/> below Novice (the
    /// penalty), exactly 1.0 across Novice I-IV (vanilla), a gentle linear climb from Apprentice I
    /// to <paramref name="gm"/> at max level. NERF-FIRST — a Master's remedy is a little better,
    /// never a different item.</summary>
    private static double RemedyFactor(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int novice = Leveling.Domain.SubLevelsPerTier;       // 4 (Novice IV)
        if (level <= novice) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault;           // 20
        double t = (level - novice) / (double)(max - novice); // 0 at Novice IV .. 1 at GM IV
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Potion StrengthMul / poultice Health multiplier for a maker of the given level,
    /// with the GM Potent emphasis adding an extra bump.</summary>
    public static double PotencyMul(int level, bool potent)
    {
        double f = RemedyFactor(level, Knob(PotencyUntrained, 0.85), Knob(PotencyGm, 1.15));
        if (potent && level >= ProvGm) f *= 1.0 + Knob(EmphasisBonus, 0.10);
        return f;
    }

    /// <summary>Potion Duration / poultice EffectDurationSec multiplier, with the GM Lasting
    /// emphasis (the non-Potent default) adding the extra bump.</summary>
    public static double DurationMul(int level, bool potent)
    {
        double f = RemedyFactor(level, Knob(DurationUntrained, 0.85), Knob(DurationGm, 1.15));
        if (!potent && level >= ProvGm) f *= 1.0 + Knob(EmphasisBonus, 0.10);
        return f;
    }

    /// <summary>Revive-HP fraction of MaxHealth for a remedy of the given brand level: ~0.22
    /// unbranded, climbing linearly, HARD-CAPPED at the GM value (0.80) even above GM level.</summary>
    public static double ReviveFraction(int level)
    {
        double untrained = Knob(ReviveUntrained, 0.22), gm = Knob(ReviveGm, 0.80);
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double f = untrained + level / (double)max * (gm - untrained);
        return Math.Min(f, gm);
    }

    /// <summary>Reaction fuel-burn economy for the given level (the MET curve): −10% Untrained,
    /// 0 at Novice, +3% Apprentice I climbing to +15% at GM. A refund (or Untrained extra-consume)
    /// fraction of the tick's burn on the host stove.</summary>
    public static double FuelEconomy(int level)
    {
        double untrained = Knob(FuelEconomyUntrained, -0.10);
        double apprentice = Knob(FuelEconomyApprentice, 0.03);
        double gm = Knob(FuelEconomyGm, 0.15);
        if (level <= 0) return untrained;
        int novice = Leveling.Domain.SubLevelsPerTier; // 4
        if (level <= novice) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault;     // 20
        double t = (level - novice) / (double)(max - novice);
        return apprentice + t * (gm - apprentice);
    }

    /// <summary>Herb-rack preservation factor (perish-style, &lt;1 = fewer bundles lost) for the
    /// given level: 1.0 through Novice, down to the GM value. Rides the COO #9 drying take.</summary>
    public static double HerbRackPreserve(int level)
    {
        if (level <= Leveling.Domain.SubLevelsPerTier) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault;
        int novice = Leveling.Domain.SubLevelsPerTier;
        double t = (level - novice) / (double)(max - novice);
        return 1.0 + t * (Knob(HerbRackPreserveGm, 0.85) - 1.0);
    }

    /// <summary>Server-side ALC level for a player (0 = Untrained when unknown).</summary>
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

/// <summary>
/// The Alchemist's Brand: the per-stack maker-brand written at the creating act (grid craft,
/// cauldron cook) and read at delivery (heal / drink) to scale potency + duration. A BATCH brand
/// bound to the maker (RULED — never a per-bottle mark, so a Novice can't brew and a GM bottle to
/// steal the buff); the whole output stack inherits it and same-brand stacks merge while different
/// brands simply stay separate (never cheat up). The emphasis flag carries the GM Potent/Lasting
/// choice (ingredient-quantity driven). Mirrors MetSignature / the BRE Brewer's Mark shape.
/// </summary>
public static class AlcBrand
{
    public const string ByAttr = "almanactcm:alcby";
    public const string ByNameAttr = "almanactcm:alcbyname";
    public const string LevelAttr = "almanactcm:alclevel";
    /// <summary>1 = Potent (deeper strength), 0 = Lasting (longer duration, the base default).</summary>
    public const string PotentAttr = "almanactcm:alcpotent";

    /// <summary>Stamp the maker brand onto a freshly created remedy/potion batch. Idempotent per
    /// stack: a re-stamp only ever RAISES nothing — it writes the acting maker's live rank, which is
    /// what the creating act should record. Never stamps below Journeyman (a mark means something
    /// from J up, like the other domain provenance tiers), but ALWAYS records the level for the
    /// potency/duration read so an Apprentice's climb still applies.</summary>
    public static void Stamp(ItemStack? stack, string uid, string name, int level, bool potent)
    {
        if (stack?.Collectible == null) return;
        stack.Attributes.SetInt(LevelAttr, level);
        stack.Attributes.SetInt(PotentAttr, potent ? 1 : 0);
        // The named provenance only from Journeyman up (below that the climb is silent).
        if (level >= AlcDomain.ProvJourneyman)
        {
            stack.Attributes.SetString(ByAttr, uid);
            stack.Attributes.SetString(ByNameAttr, name);
        }
    }

    /// <summary>The brand level on a stack (0 = unbranded = Untrained-equivalent read).</summary>
    public static int LevelOf(ItemStack? stack) => stack?.Attributes.GetInt(LevelAttr, 0) ?? 0;

    /// <summary>True if the batch carries the GM Potent emphasis (else Lasting/base).</summary>
    public static bool IsPotent(ItemStack? stack) => (stack?.Attributes.GetInt(PotentAttr, 0) ?? 0) == 1;

    public static bool HasBrand(ItemStack? stack) => stack?.Attributes.HasAttribute(LevelAttr) ?? false;
}

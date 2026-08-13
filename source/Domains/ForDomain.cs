using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// FOR — "The Master Forager" defaults (rank-bonus-design.md §FOR, RULED 2026-07-10 with the
/// 2026-07-16 enrichment rulings; technique-maps.md §FOR). Server-config seeds; playtest tunes
/// in ModConfig.
///
/// Phase 1 (this build): the two vanilla gather verbs (harvesting = in-place pluck, gathering =
/// destructive uproot/break), the ACA tapping verb, the two-stat yield anchor
/// (forageDropRate + wildCropDropRate, MIN's oreDropRate shape), the Axis 1 penalty end, and
/// NOVEL-FINDS WEIGHTING (ruled 2026-07-16: practice weighted toward first-time species, so a
/// forager levels by ranging wider, not stripping one bush). Phase 2: the Forager's Memory map
/// overlay + Patch Stewardship (both read the same mycelium/bush regrow state; built together).
/// </summary>
public static class ForDomain
{
    public const string Code = "FOR";

    public const string TechHarvesting = "harvesting"; // in-place pluck: bush, resin, reeds (renewable)
    public const string TechGathering = "gathering";   // destructive: break plant/mushroom/surface litter
    /// <summary>Driving the spile into the trunk. Small, and once per trunk face for ever
    /// (re-placing on a used site pays nothing) — siting a tapline is the skilled act, running
    /// one is not.</summary>
    public const string TechTapping = "tapping";
    /// <summary>Taking the sap out of the tapline container. Credited to WHOEVER COLLECTS, not
    /// the spile's owner (ruled 2026-08-11), scaled by the litres actually removed. This is
    /// where a tapline's practice lives; the drip itself pays nothing.</summary>
    public const string TechSapCollecting = "sapcollecting";

    // Bonus knob keys (DomainConfig.Bonus).
    public const string ForageYieldUntrained = "forageYieldUntrained"; // forageDropRate (in-place)
    public const string ForageYieldGm = "forageYieldGm";
    public const string WildcropYieldUntrained = "wildcropYieldUntrained"; // wildCropDropRate (uproot)
    public const string WildcropYieldGm = "wildcropYieldGm";
    /// <summary>Raw multiplier for the FIRST harvest of a species this player has ever taken
    /// (novel-finds ruling). Later harvests of a known species earn base raw.</summary>
    public const string NovelFindMultiplier = "novelFindMultiplier";
    /// <summary>Patch Stewardship (the state-WRITE half, re-ruled 2026-07-16): the skill IS the
    /// hands. No separate verb — the HARVEST itself stewards. A ranked pick (Apprentice I+)
    /// leaves the network intact, advancing the regrow clock by this many days (Apprentice ->
    /// GM); an Untrained pick wounds it instead. Deliberately fragile on unclaimed ground:
    /// whoever picks your patch, their hands decide what it costs. Claims fence out strangers.</summary>
    public const string TendBoostDaysApprentice = "tendBoostDaysApprentice";
    public const string TendBoostDaysGm = "tendBoostDaysGm";
    /// <summary>The liability half: an UNTRAINED pick wounds the patch, delaying its regrowth by
    /// this many days. Never destroys anything; ranked hands never wound.</summary>
    public const string WoundDays = "woundDays";
    /// <summary>Untrained-placed taplines run slower: each matured drip span pushes the spile
    /// timer forward by this fraction (0.5 = roughly two-thirds output). NEVER kills the
    /// spile, the segment, or a resin node (ruled 2026-07-16: those are worldgen-precious).</summary>
    public const string UntrainedTapSlowdown = "untrainedTapSlowdown";
    /// <summary>Litres of sap that earn one full raw of sapcollecting. All four tappable liquids
    /// ship itemsPerLitre 100 and ACA moves one item per matured drip, so a drip is 10 mL: this
    /// is deliberately fractional.</summary>
    public const string SapLitresPerCredit = "sapLitresPerCredit";
    /// <summary>Ceiling on the multiplier a SINGLE collection can pay, so emptying a long-brewed
    /// barrel is a good haul rather than a level.</summary>
    public const string SapCollectCap = "sapCollectCap";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        // The forest pairs with the forester; the wild/farmed line pairs with the cultivator.
        Adjacency = new List<string> { "WOO", "FAR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The staple gather floor. Harvesting is per-pluck; gathering is the spam floor
            // (hard position-bucket contextHash on the hook side), so its raw sits lower.
            [TechHarvesting] = new() { Raw = 2, K = 60 },
            [TechGathering] = new() { Raw = 1, K = 60 },
            // Siting a tapline: paid once per trunk face, ever. Nothing accrues while it drips.
            [TechTapping] = new() { Raw = 3, K = 25 },
            // The collection itself, scaled by litres taken. Small K: a season's taplines are
            // most of the bank, and the player has to walk the circuit to earn any of it.
            [TechSapCollecting] = new() { Raw = 8, K = 40 },
        },
        Bonus = new Dictionary<string, double>
        {
            // Axis 4 / Axis 1: both vanilla stats, penalty below 1.0 at Untrained, GM ≈ x1.15
            // (the fractional part rolls as a CHANCE of a bonus unit, never doubling — vanilla
            // rounds these stats itself).
            [ForageYieldUntrained] = 0.9,
            [ForageYieldGm] = 1.15,
            [WildcropYieldUntrained] = 0.9,
            [WildcropYieldGm] = 1.15,
            // First-ever harvest of a species pays x4 raw (ruled 2026-07-16).
            [NovelFindMultiplier] = 4.0,
            // Patch Stewardship: tend boost scales Apprentice 1.0d -> GM 2.5d against regrow
            // clocks of 10-20 days; the Untrained wound costs 1.5 days; Untrained taplines run
            // at roughly two-thirds speed. Nothing in this set ever destroys a source.
            [TendBoostDaysApprentice] = 1.0,
            [TendBoostDaysGm] = 2.5,
            [WoundDays] = 1.5,
            [UntrainedTapSlowdown] = 0.5,
            // Half a litre (50 drips) pays one full raw; a 2 L haul caps out at x4.
            [SapLitresPerCredit] = 0.5,
            [SapCollectCap] = 4.0,
        }
    };

    /// <summary>Tend boost in days for a level, linear Apprentice I (5) -> GM (max).</summary>
    public static double TendBoostFor(int level)
    {
        double app = Knob(TendBoostDaysApprentice, 1.0), gm = Knob(TendBoostDaysGm, 2.5);
        int max = Leveling.Domain.MaxLevelDefault;
        double t = GameMathClamp((level - 5) / (double)(max - 5));
        return app + t * (gm - app);
    }

    private static double GameMathClamp(double t) => t < 0 ? 0 : t > 1 ? 1 : t;

    /// <summary>General rank curve: untrained value at level 0, exactly 1.0 at Novice I,
    /// linear to the GM value at max level (shared shape with MET/MIN/WOO).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Server-side FOR level for a player (0 = Untrained when unknown).</summary>
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

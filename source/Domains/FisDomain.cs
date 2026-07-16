using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// FIS — "The Steward of the Pond" defaults (rank-bonus-design.md §FIS, CLOSED 2026-07-11, plus
/// the 2026-07-16 enrichment rulings; technique-maps.md §FIS). Server-config seeds; playtest
/// tunes in ModConfig.
///
/// Phase 1 (this build): the rod verb with the full catch-moment package on TryCatchFish (the
/// rank-skewed SIZE ROLL, THE ONE THAT GOT AWAY, and the rank-scaled depletion step on vanilla's
/// own counter), plus the PS spear verb. Phase 1b: the trap-collection verb (four BEs, owner at
/// placement). Phase 2: the Angler's Read overlay, the egg-restock steward verb, fish butchery.
/// </summary>
public static class FisDomain
{
    public const string Code = "FIS";

    public const string TechAngling = "angling";   // rod and line: cast, bite, reel
    public const string TechSpearing = "spearing"; // PS fishing spear: thrust + retrieve
    public const string TechTrapping = "trapping"; // basket/weir/trotline/Ithania trap (Phase 1b)

    // Bonus knob keys (DomainConfig.Bonus).
    /// <summary>Untrained-only chance a hooked catch escapes on the reel, taking the bait
    /// (ruled 2026-07-16: the ruled fumble spent as a LOUD story moment, the MET over-strike
    /// shape). Never fires at Novice I or above.</summary>
    public const string EscapeChanceUntrained = "escapeChanceUntrained";
    /// <summary>Additive skew on the adult chance of the vanilla size roll (P(adult) is
    /// 1 - abundance, then + skew). Untrained negative (leans juvenile), GM positive: a master
    /// lands the bigger catch (ruled 2026-07-16; rides the real roll at EntityBobber age pick).</summary>
    public const string SizeSkewUntrained = "sizeSkewUntrained";
    public const string SizeSkewGm = "sizeSkewGm";
    /// <summary>Multiplier on the vanilla fish-depletion step per catch (Axis 1 + Axis 3b).
    /// Untrained overfishes (>1), Novice = vanilla 1.0, GM light-touch (floored above zero:
    /// a spot ALWAYS depletes some; never infinite free fish).</summary>
    public const string DepletionUntrained = "depletionUntrained";
    public const string DepletionGm = "depletionGm";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        // The farmhand is the fisher (the one FIS affinity anchor); the waterside gather pairs
        // with the forager.
        Adjacency = new List<string> { "FAR", "FOR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Success-gated by nature: an empty reel grants nothing (the hook only fires on a
            // catch, junk included). Vanilla depletion is the built-in anti-farm.
            [TechAngling] = new() { Raw = 4, K = 40 },
            // One fish per thrust (didattack-gated), credited at the retrieve.
            [TechSpearing] = new() { Raw = 3, K = 30 },
            // Phase 1b: per-session trapline shape, small K (one sweep is most of the bank).
            [TechTrapping] = new() { Raw = 4, K = 15 },
        },
        Bonus = new Dictionary<string, double>
        {
            [EscapeChanceUntrained] = 0.25,
            [SizeSkewUntrained] = -0.15,
            [SizeSkewGm] = 0.35,
            [DepletionUntrained] = 1.5,
            [DepletionGm] = 0.5,
        }
    };

    /// <summary>General rank curve: untrained value at level 0, exactly 1.0 at Novice I,
    /// linear to the GM value at max level (shared shape with MET/MIN/WOO/FOR).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Additive skew curve: the untrained value at level 0, ZERO at Novice I (no skew
    /// either way), linear to the GM value at max. For knobs that add to a probability rather
    /// than multiply one.</summary>
    public static double SkewFor(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return t * gm;
    }

    /// <summary>Server-side FIS level for a player (0 = Untrained when unknown).</summary>
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

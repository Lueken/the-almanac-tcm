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
    public const string TechProcessing = "processing"; // dressing the catch (PS filleting; FIS-by-target ruling)

    // Bonus knob keys (DomainConfig.Bonus).
    /// <summary>The one that got away — RE-RULED 2026-07-16 (same day): a PERMANENT risk curve,
    /// not an Untrained-only penalty. "There SHOULD be some risk of the fish getting away. Never
    /// zero, except MAYBE at GM." Untrained 0.25, snaps to the Novice value at Novice I, linear
    /// down to the GM floor (0.02 by default — set escapeChanceGm to 0 in FIS.json for a
    /// truly safe Grandmaster).</summary>
    public const string EscapeChanceUntrained = "escapeChanceUntrained";
    public const string EscapeChanceNovice = "escapeChanceNovice";
    public const string EscapeChanceGm = "escapeChanceGm";
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
    /// <summary>Roe restock (the steward verb, single-population build 2026-07-16): ovulated
    /// fish eggs thrown into water restock the VANILLA fish map. Base value for anyone; a
    /// ranked steward's roe counts for up to this multiple at GM (careful-hands shape).</summary>
    public const string RoeRestockGmMultiplier = "roeRestockGmMultiplier";

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
            // Dressing the catch: low raw, batch-crafting dedups inside the ledger window.
            [TechProcessing] = new() { Raw = 1, K = 20 },
        },
        Bonus = new Dictionary<string, double>
        {
            [EscapeChanceUntrained] = 0.25,
            [EscapeChanceNovice] = 0.10,
            [EscapeChanceGm] = 0.02,
            [SizeSkewUntrained] = -0.15,
            [SizeSkewGm] = 0.35,
            [DepletionUntrained] = 1.5,
            [DepletionGm] = 0.5,
            [RoeRestockGmMultiplier] = 2.0,
        }
    };

    /// <summary>Roe restock multiplier: 1.0 for Untrained AND Novice hands (roe always works;
    /// stewardship makes it work harder), linear to the GM multiple.</summary>
    public static double RoeMultiplierFor(int level)
    {
        if (level <= 0) return 1.0;
        double gm = Knob(RoeRestockGmMultiplier, 2.0);
        int max = Leveling.Domain.MaxLevelDefault;
        return 1.0 + (gm - 1.0) * (level - 1) / (double)(max - 1);
    }

    /// <summary>General rank curve: untrained value at level 0, exactly 1.0 at Novice I,
    /// linear to the GM value at max level (shared shape with MET/MIN/WOO/FOR).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>The escape-risk curve: untrained at level 0, the Novice value at Novice I,
    /// linear to the GM floor at max level.</summary>
    public static double EscapeChanceFor(int level)
    {
        double u = Knob(EscapeChanceUntrained, 0.25);
        double n = Knob(EscapeChanceNovice, 0.10);
        double g = Knob(EscapeChanceGm, 0.02);
        if (level <= 0) return u;
        int max = Leveling.Domain.MaxLevelDefault;
        if (level >= max) return g;
        return n + (g - n) * (level - 1) / (double)(max - 1);
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

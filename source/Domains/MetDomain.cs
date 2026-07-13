using AlmanacTcm.Config;
using System.Collections.Generic;

namespace AlmanacTcm.Domains;

/// <summary>
/// MET pilot-domain defaults (rank-bonus-design.md §162). All values are
/// server-config seeds — playtest tunes them in ModConfig, never here.
/// </summary>
public static class MetDomain
{
    public const string Code = "MET";

    public const string TechSmithing = "smithing";
    public const string TechCasting = "casting";
    public const string TechQuenching = "quenching";
    public const string TechSmelting = "smelting";
    public const string TechAlloying = "alloying";

    // Bonus knob keys (DomainConfig.Bonus)
    public const string OverStrikeChance = "overStrikeChance";
    public const string ShatterFactorUntrained = "shatterFactorUntrained";
    public const string ShatterFactorGm = "shatterFactorGm";
    public const string FuelEconomyUntrained = "fuelEconomyUntrained";
    public const string FuelEconomyApprentice = "fuelEconomyApprentice";
    public const string FuelEconomyGm = "fuelEconomyGm";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        Adjacency = new List<string> { "MIN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            [TechSmithing] = new() { Raw = 10, K = 40 },
            [TechCasting] = new() { Raw = 6, K = 30 },
            [TechQuenching] = new() { Raw = 4, K = 12 },
            // RULED 2026-07-13: single-metal smelting saturates FAST (low K — the
            // chain's most spammable step); alloying keeps headroom (real craft).
            [TechSmelting] = new() { Raw = 3, K = 12 },
            [TechAlloying] = new() { Raw = 5, K = 20 },
        },
        Bonus = new Dictionary<string, double>
        {
            [OverStrikeChance] = 0.15,
            [ShatterFactorUntrained] = 1.5,
            [ShatterFactorGm] = 0.4,
            [FuelEconomyUntrained] = -0.10,
            [FuelEconomyApprentice] = 0.03,
            [FuelEconomyGm] = 0.15,
        }
    };

    /// <summary>Axis 3 quench-shatter factor: ×1.5 at Untrained (the one penalty-band
    /// Reliability appearance), ×1.0 at Novice I, easing to the GM floor — which is
    /// NEVER ×0 (impurity, humidity, luck: the never-zero law).</summary>
    public static double ShatterFactor(int level, double untrained, double gmFloor)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gmFloor - 1.0);
    }

    /// <summary>Axis 2 fuel economy: Untrained burns MORE (−10%), Novice neutral,
    /// Apprentice I → GM IV ramps +3% → +15%.</summary>
    public static double FuelEconomy(int level, double untrained, double apprentice, double gm)
    {
        if (level <= 0) return untrained;
        int apprenticeEntry = Leveling.Domain.SubLevelsPerTier + 1;
        if (level < apprenticeEntry) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - apprenticeEntry) / (double)(max - apprenticeEntry);
        return apprentice + t * (gm - apprentice);
    }
}

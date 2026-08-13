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
    public const string TechAssembly = "assembly";

    // Bonus knob keys (DomainConfig.Bonus)
    // Axis 1 reworked 0.4.10 (RULED 2026-07-27): the ruin roll is GONE, and the
    // over-strike no longer deletes voxels. Untrained clumsiness now matches the
    // tool mode: a split's sheared bit can crumble to scale (overStrikeChance,
    // fires on Smithing+'s recovery seam), and a move can nudge one extra nearby
    // voxel (moveSlipChance). Any mishap opens a focus grace window during which
    // nothing further can roll. ruinChance keys left in old ModConfigs are inert.
    public const string OverStrikeChance = "overStrikeChance";
    public const string MoveSlipChance = "moveSlipChance";
    public const string FocusCooldownSeconds = "focusCooldownSeconds";
    public const string ShatterFactorUntrained = "shatterFactorUntrained";
    public const string ShatterFactorGm = "shatterFactorGm";
    public const string FuelEconomyUntrained = "fuelEconomyUntrained";
    public const string FuelEconomyApprentice = "fuelEconomyApprentice";
    public const string FuelEconomyGm = "fuelEconomyGm";
    public const string BitRecoveryUntrained = "bitRecoveryUntrained";
    public const string BitRecoveryGm = "bitRecoveryGm";
    /// <summary>Raw multiplier for ONE mold completed in an industrialstory casting-sand bed.
    /// Sand casting is the mass-production road: a single tap fills every connected mold in the
    /// same instant, so each mold is worth clearly less than a hand-poured tool mold while a
    /// four-tool pour still pays more than a one-tool pour (RULED 2026-07-29).</summary>
    public const string SandCastFactor = "sandCastFactor";
    public const string MoldWearUntrained = "moldWearUntrained";
    public const string MoldWearGm = "moldWearGm";
    // Axis 6 stage 2 — GM signature
    public const string GmWearSkip = "gmWearSkip";
    public const string DurableWearSkip = "durableWearSkip";
    public const string HonedArmorPierce = "honedArmorPierce";

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
            // Assembly hooks Manual Tool Crafting's completion (presence-conditional);
            // material cost (head + handle consumed) is the anti-spam, not dedup.
            [TechAssembly] = new() { Raw = 6, K = 25 },
        },
        Bonus = new Dictionary<string, double>
        {
            [OverStrikeChance] = 0.15,
            [MoveSlipChance] = 0.05,
            [FocusCooldownSeconds] = 5,
            [ShatterFactorUntrained] = 1.5,
            [ShatterFactorGm] = 0.4,
            [FuelEconomyUntrained] = -0.10,
            [FuelEconomyApprentice] = 0.03,
            [FuelEconomyGm] = 0.15,
            [BitRecoveryUntrained] = 0.7,
            [BitRecoveryGm] = 1.3,
            // 0.35 of a hand-poured mold. A typical industrial pour of 4 tools banks ~8.4 raw,
            // 8 ingots ~16.8, against the technique's own daily ceiling of Smax/m = 33.3. The
            // saturation curve is the limiter here, deliberately, rather than a per-pour cap.
            [SandCastFactor] = 0.35,
            [MoldWearUntrained] = 1.25,
            [MoldWearGm] = 0.6,
            // Axis 6 stage 2: per-hit wear-skip chance on GM work. Durable is a deeper
            // single value, NOT stacked on the universal skip; both ride on top of the
            // maker quality pool, so kept modest (GM Durable ≈ 1.4× baseline lifespan).
            [GmWearSkip] = 0.08,
            [DurableWearSkip] = 0.18,
            // Effective armor-piercing added by Honed on attack (both CO + vanilla paths).
            [HonedArmorPierce] = 1,
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

    /// <summary>General rank curve for multiplier-shaped knobs: the untrained value
    /// at level 0, exactly 1.0 at Novice I, linear to the GM value at max level.</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Axis 2 fuel economy: Untrained burns MORE (−10%), Novice neutral,
    /// Apprentice I → GM IV ramps +3% → +15%.</summary>
    public static double FuelEconomy(int level, double untrained, double apprentice, double gm)
    {
        if (level <= 0) return untrained;
        // was: int Leveling.Rank.Apprentice = Leveling.Domain.SubLevelsPerTier + 1;  (2026-08-12 -> Rank.Apprentice)
        if (level < Leveling.Rank.Apprentice) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - Leveling.Rank.Apprentice) / (double)(max - Leveling.Rank.Apprentice);
        return apprentice + t * (gm - apprentice);
    }
}

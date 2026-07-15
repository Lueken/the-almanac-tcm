using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// MIN — "The Deep-Delver" defaults (rank-bonus-design.md §MIN, RULED 2026-07-09;
/// technique-maps.md §MIN, all four questions closed; min-cavein-study.md adopted 9/9).
/// All values are server-config seeds — playtest tunes them in ModConfig, never here.
///
/// Three verbs (mining, quarrying, knapping), four live axes:
///   Axis 2 stamina economy + Axis 1 stamina-penalty + Axis 6 endurance leg — ONE
///     seam (ImmersiveMining VigorHook.TryConsume), IM-conditional.
///   Axis 4 ore-yield + Axis 1 ore-shatter + Axis 6 yield leg — ONE stat (vanilla
///     oreDropRate, zero-Harmony).
///   Axis 3 cave-in stability — two vanilla patches on BlockBehaviorUnstableRock.
/// Axis 5 (material gate): ruled OUT (triple-overlap) — nothing to build.
/// </summary>
public static class MinDomain
{
    public const string Code = "MIN";

    public const string TechMining = "mining";
    public const string TechQuarrying = "quarrying";
    public const string TechKnapping = "knapping";

    // Bonus knob keys (DomainConfig.Bonus). Curve points are Untrained / GM ends of a
    // RankLinear that passes through exactly 1.0 at Novice I (the framework floor).
    public const string StaminaUntrained = "staminaUntrained"; // Axis 2 penalty end
    public const string StaminaGm = "staminaGm";               // Axis 2 / Deep-Delver endurance floor
    public const string OreYieldUntrained = "oreYieldUntrained"; // Axis 1 ore-shatter (< 1.0)
    public const string OreYieldGm = "oreYieldGm";               // Axis 4 / Deep-Delver yield peak (> 1.0)
    public const string CaveinUntrained = "caveinUntrained";     // Axis 3 collapse-chance multiplier, Untrained
    public const string CaveinGm = "caveinGm";                   // Axis 3 collapse-chance floor (never 0)
    // Mining raw-value scaling (Q5): effective raw = base · (1 + rarity·coeff + depth·coeff);
    // stone banks a tiny fraction of the ore base on the same verb (Q1 raw-value modifier).
    public const string MiningDepthCoeff = "miningDepthCoeff";
    public const string MiningRarityCoeff = "miningRarityCoeff";
    public const string MiningStoneFraction = "miningStoneFraction";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        // Ore feeds Metalworking; stone feeds Masonry; the underground pair with
        // Panning/Prospecting (spelunker anchors both). Server-tunable spillover.
        Adjacency = new List<string> { "MET", "MAS", "PAN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The grind floor: per-block, large K so spam saturates fast. Base is the ORE
            // unit value; per-break rawMultiplier applies depth/rarity (ore) or the stone
            // fraction (rock), so one verb prices both outcomes (Q1 + Q5).
            [TechMining] = new() { Raw = 6, K = 60 },
            // Per hammer-strike but contextHash-capped to the plug network, so one quarry
            // banks a bounded amount however many strikes it takes.
            [TechQuarrying] = new() { Raw = 8, K = 30 },
            // The day-one stone-age verb; per completed piece.
            [TechKnapping] = new() { Raw = 5, K = 20 },
        },
        Bonus = new Dictionary<string, double>
        {
            // Axis 2 (illustrative, Open Q1): Untrained tires fast, GM endurance floor.
            [StaminaUntrained] = 1.15,
            [StaminaGm] = 0.85,
            // Axis 1/4: Untrained shatters ore (< 1.0), GM ore-yield peak (cap ~1.15 so it
            // never doubles even on multi-unit drops).
            [OreYieldUntrained] = 0.90,
            [OreYieldGm] = 1.15,
            // Axis 3 collapse-chance multiplier: Untrained brings the roof down, GM floor
            // 0.5 (never 0; isolated rock still falls at 100% for everyone).
            [CaveinUntrained] = 1.5,
            [CaveinGm] = 0.5,
            // Q5 resource scaling: depth is the live proxy at ship; rarity is seeded but
            // 0-effect until a per-ore table exists (documented gap, tune later).
            [MiningDepthCoeff] = 0.5,
            [MiningRarityCoeff] = 0.5,
            [MiningStoneFraction] = 0.2,
        }
    };

    /// <summary>General rank curve for multiplier-shaped knobs: the untrained value at
    /// level 0, exactly 1.0 at Novice I, linear to the GM value at max level (MET's shape).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Server-side MIN level for a player (0 = Untrained when unknown).</summary>
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

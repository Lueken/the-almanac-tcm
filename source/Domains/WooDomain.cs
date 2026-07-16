using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO — "The Forester" defaults (rank-bonus-design.md §WOO, WOO CLOSED 2026-07-10;
/// technique-maps.md §WOO). Server-config seeds — playtest tunes in ModConfig.
///
/// Phase 1 (this build): felling + planting practice, the IM axe-stamina axis (MIN's
/// twin, via the shared ToolFactor registry), the leaf stick/sapling yield, and the
/// SIGNATURE axis — Directional Felling (rank governs where a felled tree lands, riding
/// FallingTree). Deferred: IDG processing verbs (Phase 2), the Collier's Mark +
/// silviculture (Phase 3).
/// </summary>
public static class WooDomain
{
    public const string Code = "WOO";

    public const string TechFelling = "felling";
    public const string TechPlanting = "planting";

    // Bonus knob keys (DomainConfig.Bonus).
    public const string StaminaUntrained = "staminaUntrained"; // Axis 2 axe-stamina (MIN's twin)
    public const string StaminaGm = "staminaGm";
    public const string LeafYieldUntrained = "leafYieldUntrained"; // Axis 4/1 stick+sapling scale
    public const string LeafYieldGm = "leafYieldGm";
    public const string WindfallGmChance = "windfallGmChance";     // Axis 6 windfall proc (GM-weighted)
    // Axis 3 — Directional Felling cone (degrees). The tree falls along the STRUCK FACE,
    // rotated by a random angle drawn from a cone whose WIDTH shrinks with rank and whose
    // CENTER biases from toward-player (Untrained, lethal) to away-from-player (GM, safe).
    public const string FellSpreadUntrained = "fellSpreadUntrained"; // cone half-width, Untrained
    public const string FellSpreadGm = "fellSpreadGm";               // cone half-width, GM
    public const string FellBiasUntrained = "fellBiasUntrained";     // center offset toward player (+deg)
    public const string FellBiasGm = "fellBiasGm";                   // center offset away from player (−deg)
    // Axis 3 impact. FallingTree's own damage is inert on a pivoted fall (it scales
    // 18 × |motionY| × impactDamageMul, and ConfigurePivotFaller zeroes the motion because the
    // topple is driven by rotation), and it only checks the log's final 1×1×1 landing cell, so a
    // trunk sweeping through you never connects. WOO replaces both with a flat hit along the
    // swept path. Rank governs WHERE the tree lands, never how hard it hits: a Grandmaster who
    // stands in the wrong place is as flat as an Untrained one.
    public const string FellImpactDamage = "fellImpactDamage";         // flat damage per connect
    public const string FellDamageCooldownMs = "fellDamageCooldownMs"; // min gap between hits, same victim

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        // Charcoal feeds Metalworking; the forest pairs with Foraging.
        Adjacency = new List<string> { "FOR", "MET" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The staple grind floor: per log, and felling a whole tree breaks many logs, so raw
            // is LOW per block (spec: logs 0.2–0.4) — a ~10-log tree banks ~3.5, not 35. Large K.
            [TechFelling] = new() { Raw = 0.35, K = 60 },
            // Cheap and self-limiting (seeds are finite); credited at the plant action (ruled).
            [TechPlanting] = new() { Raw = 5, K = 15 },
        },
        Bonus = new Dictionary<string, double>
        {
            // Axis 2 (spec default ±0.15; MIN's was widened to ±0.30 after live tuning — WOO
            // starts at spec and we widen in playtest if the felt gap is thin).
            [StaminaUntrained] = 1.15,
            [StaminaGm] = 0.85,
            // Axis 4/1: Untrained shreds the canopy (fewer sticks/saplings), GM a modest bonus.
            [LeafYieldUntrained] = 0.8,
            [LeafYieldGm] = 1.2,
            // Axis 6 windfall: GM-weighted chance a felled leaf pays a bonus stick/sapling.
            [WindfallGmChance] = 0.15,
            // Axis 3 Directional Felling (RULED from Jeffrey's mock 2026-07-15): Untrained is a
            // wide cone biased toward the feller (real death risk); GM is a tight cone biased
            // away. Degrees.
            [FellSpreadUntrained] = 85,
            [FellSpreadGm] = 6,
            [FellBiasUntrained] = 35,   // rotate cone center TOWARD the player
            [FellBiasGm] = -22,         // rotate cone center AWAY from the player
            // Tuned against a 15 HP player: one connect is a serious wound, two inside the
            // cooldown is a corpse. The cooldown is what stops a 10-log tree landing 10 hits.
            [FellImpactDamage] = 8,
            [FellDamageCooldownMs] = 600,
        }
    };

    /// <summary>General rank curve: untrained value at level 0, exactly 1.0 at Novice I,
    /// linear to the GM value at max level (shared shape with MET/MIN).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Progress 0 (Untrained) → 1 (Grandmaster) across the whole ladder — for the
    /// cone lerp, which is not anchored at 1.0 the way the multiplier curves are.</summary>
    public static double RankProgress(int level)
    {
        int max = Leveling.Domain.MaxLevelDefault;
        if (level <= 0) return 0;
        if (level >= max) return 1;
        return level / (double)max;
    }

    /// <summary>Server-side WOO level for a player (0 = Untrained when unknown).</summary>
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

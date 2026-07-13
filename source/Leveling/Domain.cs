// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System;
using System.Collections.Generic;

namespace AlmanacTcm.Leveling;

/// <summary>
/// A trade domain (MET, COO, …) — the vendored Skill, minus abilities.
/// Level 0 = Untrained; levels 1-20 are the climbable sub-levels
/// (Novice I → Grandmaster IV, four per tier, five tiers).
/// </summary>
public class Domain
{
    public const int SubLevelsPerTier = 4;
    public const int TierCount = 5;
    public const int MaxLevelDefault = SubLevelsPerTier * TierCount;

    /// <summary>Save/identify key (the three-letter code). Must never change once shipped.</summary>
    public string Code { get; private set; }

    public string DisplayName { get; set; }

    /// <summary>Registry index, set by DomainSetTemplate.AddDomain. Not save-stable — Code is.</summary>
    public int Id { get; internal set; }

    /// <summary>Disabled domains stay level 0 and grant nothing — the conditional-registration
    /// state for feature-mod domains whose mod is absent. Set before any player joins.</summary>
    public bool Enabled { get; set; } = true;

    public int MaxLevel { get; set; } = MaxLevelDefault;

    /// <summary>Banked XP needed to COMPLETE each tier climb, Novice→GM order (§7 pacing table).
    /// Within a tier the four sub-level steps follow the quadratic shape: step s costs
    /// tierTotal·s²/30 (1+4+9+16=30), so later sub-levels cost more. Playtest-tunable.</summary>
    public IReadOnlyList<double> TierTotals => tierTotals;

    private double[] tierTotals = { 150, 500, 1400, 3200, 6500 };

    public void SetTierTotals(IList<double> totals)
    {
        if (totals == null || totals.Count != TierCount)
        {
            throw new ArgumentException($"TierTotals must have exactly {TierCount} entries.");
        }
        for (int i = 0; i < TierCount; i++) tierTotals[i] = Math.Max(totals[i], 1.0);
    }

    public Domain(string code, string? displayName = null)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        DisplayName = displayName ?? code;
        Id = -1;
    }

    /// <summary>Experience required to go from (level-1) to level. Level 1 is the first
    /// Novice step out of Untrained; levels above MaxLevel return 0 (nothing further).</summary>
    public float GetRequiredExperience(int level)
    {
        if (level <= 0 || level > MaxLevel) return 0f;
        int tier = (level - 1) / SubLevelsPerTier;
        int step = (level - 1) % SubLevelsPerTier + 1;
        return (float)(tierTotals[tier] * step * step / 30.0);
    }

    /// <summary>Tier index (0=Novice … 4=Grandmaster) for a level, or -1 for Untrained.</summary>
    public static int TierOf(int level) => level <= 0 ? -1 : (level - 1) / SubLevelsPerTier;

    /// <summary>Sub-level within the tier, 1-4 (Roman I-IV). 0 for Untrained.</summary>
    public static int SubLevelOf(int level) => level <= 0 ? 0 : (level - 1) % SubLevelsPerTier + 1;
}

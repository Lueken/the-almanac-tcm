// Derived from XLib/XLeveling by Xandu (MIT) — see THIRD-PARTY-LICENSES/XLIB-LICENSE.txt.
// Source: github.com/Xandu93/VSMods via github.com/DeadSigma/xskills-fork_xlib-fork.
using System;
using System.Collections.Generic;

namespace AlmanacTcm.Leveling;

/// <summary>
/// A trade domain (MET, COO, …) — the vendored Skill, minus abilities.
/// Level 0 = Untrained (tier -1, deliberately outside the named tiers); levels 1-16 are
/// Novice I … Master IV, four per tier; level 17 is Grandmaster, terminal and unnumbered.
/// 18 states in all, max level 17.
/// (Corrected 2026-08-12. This summary described the ORIGINAL ladder: 1-20, Novice I →
/// Grandmaster IV. The 2026-07-15 ruling below made Grandmaster terminal and resized the
/// ladder to 17; MaxLevelDefault was updated, this summary was not.)
/// </summary>
public class Domain
{
    public const int SubLevelsPerTier = 4;
    public const int TierCount = 5;

    /// <summary>Novice…Master each have four sub-levels; Grandmaster is a single terminal rank
    /// (RULED 2026-07-15: "GM is GM, no ranks within it"). So the ladder is 4 tiers × 4 + 1 GM = 17.</summary>
    public const int MaxLevelDefault = SubLevelsPerTier * (TierCount - 1) + 1;

    /// <summary>Save/identify key (the three-letter code). Must never change once shipped.</summary>
    public string Code { get; private set; }

    public string DisplayName { get; set; }

    /// <summary>Registry index, set by DomainSetTemplate.AddDomain. Not save-stable — Code is.</summary>
    public int Id { get; internal set; }

    /// <summary>Disabled domains stay level 0 and grant nothing — the conditional-registration
    /// state for feature-mod domains whose mod is absent. Set before any player joins.</summary>
    public bool Enabled { get; set; } = true;

    public int MaxLevel { get; set; } = MaxLevelDefault;

    /// <summary>Cumulative banked XP to REACH each tier entry from Untrained: Novice I,
    /// Apprentice I, Journeyman I, Master I, GM (§7 pacing table; ruled 2026-07-24 —
    /// "the road to Novice I should be significantly longer"). The first total is the
    /// whole Untrained→Novice I entry step; each later total is the milestone whose gap
    /// from the previous one spans the four steps ending at that entry, quadratic shape
    /// step s = gap·s²/30 (1+4+9+16=30). Playtest-tunable.</summary>
    public IReadOnlyList<double> TierTotals => tierTotals;

    private double[] tierTotals = { 150, 650, 2050, 5250, 11750 };

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

    /// <summary>Experience required to go from (level-1) to level. Level 1 is the whole
    /// Untrained→Novice I entry climb (tierTotals[0], no cheap first step); levels above
    /// MaxLevel return 0 (nothing further). Later levels walk four-step segments between
    /// tier-entry milestones, so cumulative cost lands exactly on each tierTotals entry
    /// and GM remains the single largest step of its segment.</summary>
    public float GetRequiredExperience(int level)
    {
        if (level <= 0 || level > MaxLevel) return 0f;
        if (level == 1) return (float)tierTotals[0];
        int segment = (level - 2) / SubLevelsPerTier;
        double gap = tierTotals[segment + 1] - tierTotals[segment];
        int step = (level - 2) % SubLevelsPerTier + 1;
        return (float)(gap * step * step / 30.0);
    }

    /// <summary>Tier index (0=Novice … 4=Grandmaster) for a level, or -1 for Untrained.</summary>
    public static int TierOf(int level) => level <= 0 ? -1 : (level - 1) / SubLevelsPerTier;

    /// <summary>Sub-level within the tier, 1-4 (Roman I-IV). 0 for Untrained.</summary>
    public static int SubLevelOf(int level) => level <= 0 ? 0 : (level - 1) % SubLevelsPerTier + 1;

    public static readonly string[] TierNames = { "Novice", "Apprentice", "Journeyman", "Master", "Grandmaster" };
    public static readonly string[] SubLevelRoman = { "", "I", "II", "III", "IV" };

    /// <summary>Display rank ("Apprentice I", "Untrained") — names are presentation,
    /// not tuned constants, so client surfaces may compute them from a synced level.</summary>
    public static string RankName(int level)
    {
        if (level <= 0) return "Untrained";
        int tier = TierOf(level);
        // Grandmaster carries no sub-level numeral; it is a single terminal rank.
        return tier >= TierCount - 1 ? TierNames[tier] : $"{TierNames[tier]} {SubLevelRoman[SubLevelOf(level)]}";
    }
}

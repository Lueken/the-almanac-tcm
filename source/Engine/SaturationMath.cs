using System;
using System.Collections.Generic;

namespace AlmanacTcm.Engine;

/// <summary>
/// The engine's pure math (xp-engine-design.md §§3-4) — no API types so every
/// rule here is unit-testable. banked(x) = Smax·x/(x+K), Michaelis-Menten:
/// diminishing returns from the first action, Smax as the asymptotic daily cap,
/// K as the half-cap rate knob.
/// </summary>
public static class SaturationMath
{
    public static double Banked(double x, double smax, double k)
    {
        if (x <= 0) return 0;
        return smax * x / (x + k);
    }

    /// <summary>Breadth phase (Untrained → Journeyman entry): the domain cap splits
    /// across techniques (Smax/m each), so touching m distinct techniques is the only
    /// route to a full day. Clamped to Smax.</summary>
    public static double BreadthBanked(
        IReadOnlyDictionary<string, double> accumulators,
        Func<string, double> kOf,
        double smax, int m)
    {
        if (m < 1) m = 1;
        double perTechniqueCap = smax / m;
        double sum = 0;
        foreach (var (technique, x) in accumulators)
        {
            sum += Banked(x, perTechniqueCap, kOf(technique));
        }
        return Math.Min(sum, smax);
    }

    /// <summary>Depth phase (Journeyman →): the dominant technique saturates against
    /// the full Smax; every other technique's saturated contribution counts at
    /// offWeight (0.25 locked). Clamped to Smax — sustained narrow repetition is now
    /// the only way to approach the cap.</summary>
    public static double DepthBanked(
        IReadOnlyDictionary<string, double> accumulators,
        Func<string, double> kOf,
        double smax, string? dominantTechnique, double offWeight)
    {
        double sum = 0;
        foreach (var (technique, x) in accumulators)
        {
            double weight = technique == dominantTechnique ? 1.0 : offWeight;
            sum += weight * Banked(x, smax, kOf(technique));
        }
        return Math.Min(sum, smax);
    }

    /// <summary>Per-technique saturated value as used inside the phase sum — the base
    /// for co-grant fan-out (the share % prices the transfer; no second cap).</summary>
    public static double TechniqueBanked(
        double x, double k, double smax, int m,
        bool depthPhase, bool isDominant, double offWeight)
    {
        if (!depthPhase) return Banked(x, smax / Math.Max(m, 1), k);
        return (isDominant ? 1.0 : offWeight) * Banked(x, smax, k);
    }

    /// <summary>Spillover fade by receiving domain's level: full through Journeyman
    /// entry, linear to zero across the Journeyman tier, none from Master on.
    /// Breadth-phase fundamentals transfer; depth is domain-specific by definition.</summary>
    public static double SpilloverFade(int level, int journeymanEntryLevel, int subLevelsPerTier)
    {
        if (level < journeymanEntryLevel) return 1.0;
        int masterEntry = journeymanEntryLevel + subLevelsPerTier;
        if (level >= masterEntry) return 0.0;
        return (masterEntry - level) / (double)subLevelsPerTier;
    }

    /// <summary>The 3am boundary index: count of consolidation-hour crossings since
    /// world start, derived ONLY from calendar time — never session events. Negative
    /// before the first crossing; monotonic forever after.</summary>
    public static long BoundaryIndex(double calendarTotalDays, int consolidationHour)
    {
        return (long)Math.Floor((calendarTotalDays * 24.0 - consolidationHour) / 24.0);
    }
}

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

    /// <summary>Banked with a tail floor (LGD-80): follow the curve while its marginal value
    /// per raw exceeds <paramref name="floorPerRaw"/>, then pay exactly the floor per raw until
    /// the cap, then stop. The switch point is where MM's slope smax·k/(x+k)² equals the floor,
    /// so the two segments meet with matching slope — no mid-day sag, no snap-back. Unlike pure
    /// MM this curve REACHES the cap, so a walled technique pays exactly zero; the ledger says
    /// so once instead of toasting +0. Floor 0 is pure MM, unchanged.</summary>
    public static double BankedFloored(double x, double smax, double k, double floorPerRaw)
    {
        if (x <= 0) return 0;
        if (floorPerRaw <= 0) return Banked(x, smax, k);
        double xStar = Math.Sqrt(smax * k / floorPerRaw) - k;
        // Floor at or above the curve's initial slope: the whole day is the flat wage.
        if (xStar <= 0) return Math.Min(smax, floorPerRaw * x);
        if (x <= xStar) return Banked(x, smax, k);
        return Math.Min(smax, Banked(xStar, smax, k) + floorPerRaw * (x - xStar));
    }

    /// <summary>Breadth phase (Untrained → Journeyman entry): the domain cap splits
    /// across techniques (Smax/m each), so touching m distinct techniques is the only
    /// route to a full day. Clamped to Smax.</summary>
    public static double BreadthBanked(
        IReadOnlyDictionary<string, double> accumulators,
        Func<string, double> kOf, Func<string, double> floorOf,
        double smax, int m)
    {
        if (m < 1) m = 1;
        double perTechniqueCap = smax / m;
        double sum = 0;
        foreach (var (technique, x) in accumulators)
        {
            sum += BankedFloored(x, perTechniqueCap, kOf(technique), floorOf(technique));
        }
        return Math.Min(sum, smax);
    }

    /// <summary>Depth phase (Journeyman →): the dominant technique saturates against
    /// the full Smax; every other technique's saturated contribution counts at
    /// offWeight (0.25 locked). Clamped to Smax — sustained narrow repetition is now
    /// the only way to approach the cap.</summary>
    public static double DepthBanked(
        IReadOnlyDictionary<string, double> accumulators,
        Func<string, double> kOf, Func<string, double> floorOf,
        double smax, string? dominantTechnique, double offWeight)
    {
        double sum = 0;
        foreach (var (technique, x) in accumulators)
        {
            // The off-weight scales the floored curve whole, so an off-dominant floor is
            // offWeight × the configured floor — the wage shrinks with the phase, deliberately.
            double weight = technique == dominantTechnique ? 1.0 : offWeight;
            sum += weight * BankedFloored(x, smax, kOf(technique), floorOf(technique));
        }
        return Math.Min(sum, smax);
    }

    /// <summary>Per-technique saturated value as used inside the phase sum — the base
    /// for co-grant fan-out (the share % prices the transfer; no second cap).</summary>
    public static double TechniqueBanked(
        double x, double k, double smax, int m,
        bool depthPhase, bool isDominant, double offWeight, double floorPerRaw = 0)
    {
        if (!depthPhase) return BankedFloored(x, smax / Math.Max(m, 1), k, floorPerRaw);
        return (isDominant ? 1.0 : offWeight) * BankedFloored(x, smax, k, floorPerRaw);
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

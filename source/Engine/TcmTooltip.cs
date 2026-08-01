namespace AlmanacTcm.Engine;

/// <summary>
/// The one voice for domain-bonus numbers in tooltips (RULED 2026-08-01, suite-wide): the
/// leading number is the TRUE delivered value, and the maker's contribution sits beside it
/// as an annotation, green for a lift, red for a penalty. Where a stat line exists, the
/// annotation is a count delta on that line ("3.7 (+0.7)"); where the effect has no vanilla
/// number (a wear-skip chance, a spoilage rate), the mark's own flavor line carries the
/// percent. Colors are vanilla's own tooltip green and red (the wearable warmth line uses
/// exactly these), so nothing reads foreign.
///
/// Every producer of these suffixes goes through here: one rounding rule, one threshold,
/// one pair of colors, so a shelf of goods from five domains reads as one system.
/// </summary>
public static class TcmTooltip
{
    public const string LiftColor = "#84ff84";
    public const string PenaltyColor = "#ff8484";

    /// <summary>Deltas smaller than this are noise, not information (a Novice-band factor
    /// of 1.0 must render exactly like vanilla).</summary>
    public const double MinDelta = 0.05;

    /// <summary>"3.7 (+0.7)": the true value leading, the delta annotated. Falls back to
    /// the bare base when the multiplier changes nothing visible.</summary>
    public static string TrueValue(double baseVal, double mul, string fmt = "F1")
    {
        double delta = baseVal * (mul - 1.0);
        if (System.Math.Abs(delta) < MinDelta) return baseVal.ToString(fmt);
        return (baseVal + delta).ToString(fmt) + DeltaSuffix(delta);
    }

    /// <summary>" (+0.7)" in green, " (-0.4)" in red. Empty under the threshold.</summary>
    public static string DeltaSuffix(double delta, string fmt = "0.#")
    {
        if (System.Math.Abs(delta) < MinDelta) return "";
        return Wrap($"({(delta >= 0 ? "+" : "")}{delta.ToString(fmt)})", delta >= 0);
    }

    /// <summary>" (+12%)" from a multiplier, green above 1, red below. Empty within a
    /// rounding point of 1.0.</summary>
    public static string PercentSuffix(double mul)
    {
        int pct = (int)System.Math.Round((mul - 1.0) * 100.0);
        if (pct == 0) return "";
        return Wrap($"({(pct > 0 ? "+" : "")}{pct}%)", pct > 0);
    }

    /// <summary>A whole annotated clause (already localized) wrapped in the scheme's color:
    /// " (spoils 30% slower)" and its kin.</summary>
    public static string Clause(string localizedText, bool lift = true)
        => Wrap($"({localizedText})", lift);

    private static string Wrap(string text, bool lift)
        => $" <font color=\"{(lift ? LiftColor : PenaltyColor)}\">{text}</font>";
}

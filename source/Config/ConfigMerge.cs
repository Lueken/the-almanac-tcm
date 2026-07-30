namespace AlmanacTcm.Config;

/// <summary>
/// The three-way merge decision, as pure logic (the SaturationMath posture: no API types, so the
/// rule is provable on its own). Given what the operator's file currently holds, what the mod
/// shipped last time it wrote that file, and what the mod ships now, decide what happens to the
/// value on upgrade.
///
/// The whole point of the baseline leg is telling "pristine" from "tuned" without asking. Live ==
/// baseline means the operator never touched the value, so it may safely follow the shipped
/// default; anything else is a deliberate divergence and is kept.
/// </summary>
public enum MergeAction
{
    /// <summary>Live already equals the new shipped value. Nothing to write but the baseline.</summary>
    AlreadyCurrent,
    /// <summary>Live still equals the old shipped value, so it was never tuned: take the new one.</summary>
    Adopt,
    /// <summary>Live diverges from the baseline: operator tuning, keep it.</summary>
    KeepTuned,
    /// <summary>No baseline recorded for an existing key, so pristineness cannot be proven.
    /// Conservative: keep what is there and start tracking from now.</summary>
    KeepUnprovable,
}

public static class ConfigMerge
{
    /// <summary>Decide one key. <paramref name="baseline"/> is null when the file has no recorded
    /// baseline for this key.</summary>
    public static MergeAction Decide(string live, string? baseline, string shipped)
    {
        if (live == shipped) return MergeAction.AlreadyCurrent;
        if (baseline == null) return MergeAction.KeepUnprovable;
        return live == baseline ? MergeAction.Adopt : MergeAction.KeepTuned;
    }
}

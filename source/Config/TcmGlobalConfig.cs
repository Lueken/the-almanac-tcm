namespace AlmanacTcm.Config;

/// <summary>
/// Global engine knobs (xp-engine-design.md §9; defaults are the LOCKED §10 rulings).
/// Loaded SERVER-SIDE ONLY from ModConfig/almanactcm/global.json and never synced to
/// clients — client sync carries state (rank, pending %), never the constants that
/// produced it. Servers may diverge from these shipped defaults invisibly.
/// </summary>
public class TcmGlobalConfig
{
    public int ConsolidationHour { get; set; } = 3;

    /// <summary>Lifetime GM-domain cap (guard #12). Lowering never revokes attained GMs.</summary>
    public int GmDomainCap { get; set; } = 2;

    /// <summary>Fraction of pending (unconsolidated) practice scattered on death.</summary>
    public double LambdaDeath { get; set; } = 0.5;

    /// <summary>Separate scatter fraction for PvP deaths (default = LambdaDeath).</summary>
    public double LambdaPvp { get; set; } = 0.5;

    /// <summary>In-game hours after a penalized death during which further deaths cost nothing.</summary>
    public double ChainDeathCooldownHours { get; set; } = 2.0;

    /// <summary>Adjacency spillover share of neighbours' banked XP.</summary>
    public double Sigma { get; set; } = 0.05;

    /// <summary>Spillover received per day caps at this % of the receiving domain's Smax.</summary>
    public double SpilloverCapPct { get; set; } = 25.0;

    /// <summary>Depth phase: saturated contribution weight of non-dominant techniques.</summary>
    public double DepthOffTechniqueWeight { get; set; } = 0.25;

    /// <summary>Rolling window (in-game days) that elects the depth-phase dominant technique.</summary>
    public int DominantWindowDays { get; set; } = 7;

    /// <summary>Identical practice contexts inside this window log zero raw practice
    /// (the place-and-rebreak guard).</summary>
    public double DedupWindowSeconds { get; set; } = 90.0;

    public int DedupRingSize { get; set; } = 64;

    /// <summary>Send each practice gain (and dedup'd repeat) to the player's Info
    /// tab. Trial instrumentation; consider off once the engine is trusted.</summary>
    public bool PracticeGainMessages { get; set; } = true;

    public bool VerboseDebugLogging { get; set; } = true;
}

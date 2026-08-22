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

    /// <summary>Emit the per-gain HUD toast packet (client decides whether and how to
    /// draw it via ConfigLib). Off = no packets leave the server at all.</summary>
    public bool PracticeGainToasts { get; set; } = true;

    public bool VerboseDebugLogging { get; set; } = true;

    /// <summary>MET material gate (§162 Axis 5): below the required MET rank a player
    /// cannot smelt, form, or cast a metal above their tier. Assembly is never gated.
    /// Server-owned and bespoke to MET so other domains get their own toggle later
    /// (materialGateALC, …). Set false to disable the MET gate entirely.</summary>
    public bool MaterialGateMET { get; set; } = true;

    /// <summary>Enforcement for a gated attempt: false = block the interaction with a
    /// warning (default, no accidental material loss); true = HARDCORE, let it through
    /// and waste the material.</summary>
    public bool MaterialGateMETHardcore { get; set; } = false;

    /// <summary>Required MET level for a metal not in the classification map (a mod-added
    /// metal). Default 0 = allow and log once, so nothing is silently locked; raise it to
    /// gate unknowns conservatively.</summary>
    public int MaterialGateMETUnmappedLevel { get; set; } = 0;

    /// <summary>Ambient storm-warning shift (ruled 2026-08-21): the first storm warning (Temporal
    /// Symphony cues, or the vanilla chat line without TS) is delivered per player by TEM rank
    /// instead of broadcast at 0.35 days. False restores stock broadcast behavior everywhere.</summary>
    public bool StormShiftTEM { get; set; } = true;

    /// <summary>TEM repair gate (third-pass ruling 2026-08-21): below this TEM level a player cannot
    /// repair a translocator or recharge a discharged teleporter. Transit is never gated; anyone
    /// steps through a working machine. Default 4 = Novice IV; 0 disables the gate; season tuning
    /// may raise it toward Apprentice I (5) if rust-mob practice trivializes the climb.</summary>
    public int RepairGateTEMLevel { get; set; } = 4;

    /// <summary>Alloy Ledger (§162 Axis 4, Apprentice unlock) access. true = only an Apprentice+ of
    /// Metalworking can open it on a crucible (the ruled default); false = any player can, for
    /// servers that want the convenience for everyone. Server-owned and synced to clients on
    /// join, since the ledger opens client-side.</summary>
    public bool AlloyLedgerGated { get; set; } = true;
}

using AlmanacTcm.Config;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// PAN — "The Surveyor" defaults (rank-bonus-design.md §PAN, RULED 2026-07-09 via the two
/// adopted studies: pan-prospect-study 7/7 + pan-heatmap-study 7/7; scope confirmed 2026-07-16:
/// prospecting is the more important half, with bettererprospecting replacing the propick and
/// ProspectTogether making shared surveys the trade good).
///
/// Phase 1 (this build): both verbs' practice (pan wash completion; every reading at the
/// DidProbe funnel + BP's non-reading modes), the Axis 1 penalty pair (empty washes below 1.0
/// pan stat; readings COARSENED IN THE DATA below Novice), and the pan-yield secondary. The
/// yield lever is vanilla's own PanningDrop.DropModbyStat, injected in memory onto every
/// stat-less drop entry at server start (vanilla only wires it on rusty gears).
/// Phase 2: the Master+ depth-band companion store, the ProspectTogether tooltip (the
/// Surveyor), and placer-tracing.
/// </summary>
public static class PanDomain
{
    public const string Code = "PAN";

    public const string TechPanning = "panning";         // a completed 3.4s wash, material consumed
    public const string TechProspecting = "prospecting"; // any propick probe: reading or search mode

    // Bonus knob keys (DomainConfig.Bonus).
    /// <summary>Multiplier on every pan drop chance (vanilla DropModbyStat path). Untrained
    /// washes come up empty more often; GM lifts odds modestly (ruled: chance-only, no
    /// low-rank doubling).</summary>
    public const string PanYieldUntrained = "panYieldUntrained";
    public const string PanYieldGm = "panYieldGm";
    /// <summary>Untrained readings are degraded IN THE RECORDED DATA (the DidProbe prefix), so
    /// the ore map and every ProspectTogether share holds the degraded read: the map remembers
    /// the skill of the surveyor. Redesigned 2026-07-17 against IOG's real density scale
    /// (workable deposits commonly read 0.1-1 permille on The Quire): the density word
    /// UNDERSTATES by this many bands (weakest lines demote to the visible trace list, nothing
    /// is hidden) and ppt keeps one significant figure. Novice+ records exactly vanilla.</summary>
    public const string UntrainedBandsDown = "untrainedBandsDown";
    /// <summary>Placer-tracing (the crown jewel, ruled): the pan reads the ore maps under the
    /// wash and biases the drop table toward what is ACTUALLY below. Strength scales Apprentice
    /// -> GM; below Master the trace is noisy (a faint signal), Master+ reads clean. Novice and
    /// below pan blind (vanilla).</summary>
    public const string TraceStrengthApprentice = "traceStrengthApprentice";
    public const string TraceStrengthGm = "traceStrengthGm";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        // The underground pairing (spelunker anchors both); MIN reciprocates in its own list.
        Adjacency = new List<string> { "MIN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // One credit per completed wash (3.4s + a gravel block each): honest pace, K caps
            // the day at a real riverbank session.
            [TechPanning] = new() { Raw = 3, K = 30 },
            // One credit per probe taken (readings and search modes alike). Re-probing the
            // same chunk column dedups inside the ledger window.
            [TechProspecting] = new() { Raw = 3, K = 20 },
        },
        Bonus = new Dictionary<string, double>
        {
            [PanYieldUntrained] = 0.85,
            [PanYieldGm] = 1.25,
            [UntrainedBandsDown] = 1.0,
            [TraceStrengthApprentice] = 0.35,
            [TraceStrengthGm] = 1.5,
        }
    };

    /// <summary>Trace strength for a level, linear Apprentice I (5) -> GM (max); 0 below.</summary>
    public static double TraceStrengthFor(int level)
    {
        if (level < 5) return 0;
        double app = Knob(TraceStrengthApprentice, 0.35), gm = Knob(TraceStrengthGm, 1.5);
        int max = Leveling.Domain.MaxLevelDefault;
        double t = Math.Min(1.0, (level - 5) / (double)(max - 5));
        return app + t * (gm - app);
    }

    /// <summary>General rank curve: untrained value at level 0, exactly 1.0 at Novice I,
    /// linear to the GM value at max level (shared shape with the other domains).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Server-side PAN level for a player (0 = Untrained when unknown).</summary>
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

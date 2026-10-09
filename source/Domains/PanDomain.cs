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
    /// <summary>The GRAVE-SIFTER (ruled 2026-07-17): pan-table entries at or below this base
    /// chance are the treasure tail (lore books, temporal gears, jewelry, gems, tuning
    /// cylinders — the bony-soil dig's real prizes), and from Master I they climb by an EXTRA
    /// multiplier on top of pan yield. A career panner is who you bring to a bonemeal dig.</summary>
    public const string TreasureChanceThreshold = "treasureChanceThreshold";
    public const string TreasureBiasMaster = "treasureBiasMaster";
    public const string TreasureBiasGm = "treasureBiasGm";
    /// <summary>Placer-tracing (the crown jewel, ruled): the pan reads the ore maps under the
    /// wash and biases the drop table toward what is ACTUALLY below. Strength scales Apprentice
    /// -> GM; below Master the trace is noisy (a faint signal), Master+ reads clean. Novice and
    /// below pan blind (vanilla).</summary>
    public const string TraceStrengthApprentice = "traceStrengthApprentice";
    public const string TraceStrengthGm = "traceStrengthGm";

    // ---- The wash whisper (RULED 2026-09-21, from ComitatensSaxoni's moon-runes question).
    // Placer-tracing worked and could not be FELT: a noisy x1.2-1.5 on drop chances below
    // Master is statistically invisible, so "the pan stops being blind" was a rung the player
    // could never experience. The whisper makes the trace legible: an occasional chat line
    // naming an ore the trace genuinely feels below. It reads the same factors the trace
    // reads, so it costs nothing new, and it NEVER fabricates: below Master it is sometimes
    // vague about ranking (names a lesser ore that is really present), never false.
    /// <summary>Chance per trace-eligible wash that the whisper speaks (cooldown still applies).</summary>
    public const string PanWhisperChance = "panWhisperChance";
    /// <summary>Minimum propick-style ore factor before an ore is worth whispering about.</summary>
    public const string PanWhisperMinFactor = "panWhisperMinFactor";

    // ---- The bore readout (knobbed 2026-10-07 jl). The two rank gates on the Master+ borehole
    // depth readout were bare literals in PanPatches (PanSurveyor.MasterLevel for "reports at
    // all", a literal 17 for "reports the exact band and records it to the shared map"), so a
    // server that wanted the depth readout off, or wanted it earlier, had no lever and needed a
    // code change. Requested by Thalius after the whisper disable: a server keeper asking to
    // turn a reading off should not be told to recompile.
    /// <summary>Rank at which the prospecting-pick borehole starts reporting ore depth at all.
    /// Default 13 = Master I, the ruled rung. Set above the GM cap (17) to disable the readout
    /// entirely; lower it to grant the bore earlier.</summary>
    public const string BoreDepthLevel = "boreDepthLevel";
    /// <summary>Rank at which the bore reports the EXACT band ("first at 34 down, runs to 48")
    /// and records it to the shared depth store the Surveyor's maps carry. Default 17 = GM, the
    /// ruled rung. Below it the readout is the coarse "first struck near 32 down" and NOTHING is
    /// recorded, so raising this above the GM cap keeps the chat readout while taking depth off
    /// shared maps.</summary>
    public const string BoreDepthExactLevel = "boreDepthExactLevel";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // m = 2, not 3 (FIXED 0.5.15, LGD-237). PAN has exactly TWO techniques, and breadth phase
        // caps each at Smax/m, so m = 3 capped a panner's theoretical maximum day at 2 x 33.33 =
        // 66.67 of a 100 Smax. A player who panned and prospected perfectly, all day, every day,
        // could not complete a day: there was no third verb to open. It also halved the slot the
        // curve saturates against, which is the "panning is maxed out at like, 2 blocks of gravel"
        // report. m must equal the verbs a practitioner can REACH, and here that is all of them.
        //
        // NOTE: m is NOT part of the three-way config merge (LedgerSystem 179-230 walks Techniques
        // and Bonus only), so this default reaches NEW installs only. Every already-booted server
        // needs 2 written into ModConfig/almanactcm/PAN.json by hand. The Quire was done
        // 2026-10-06; see PENDING.md "Not in the zip, needed at deploy time".
        M = 2,
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
            // Tail floors, 0.5 XP per act (0.5.15, LGD-236/LGD-240). Same convention as
            // WOO's shipped floors: floor = 0.5 / Raw, so the saturation tail pays a
            // visible half-point an act instead of fading to 0.14, and the day REACHES its
            // cap and says so once. Deployed by hand to The Quire 2026-10-06; these are
            // those exact values, read back from the live config rather than retyped.
            [TechPanning + Config.DomainConfig.FloorKnobSuffix] = 0.1667,
            [TechProspecting + Config.DomainConfig.FloorKnobSuffix] = 0.1667,
            [PanYieldUntrained] = 0.85,
            [PanYieldGm] = 1.25,
            [TraceStrengthApprentice] = 0.35,
            [TraceStrengthGm] = 1.5,
            // Grave-sifter: entries at/below 1% base chance count as treasure; Master I picks
            // up an extra x1.3 on them, GM x2.0 (on TOP of pan yield: GM treasure ~x2.5 net).
            [TreasureChanceThreshold] = 0.01,
            [TreasureBiasMaster] = 1.3,
            [TreasureBiasGm] = 2.0,
            // The wash whisper: ~one line per 20-30s of continuous panning at the default
            // wash length, on top of the 20s per-player cooldown in PanPatches.
            [PanWhisperChance] = 0.25,
            [PanWhisperMinFactor] = 0.05,
            // The bore gates, previously bare literals in PanPatches.BoreDepthPatch
            // (2026-10-07 jl). Values UNCHANGED from what shipped: 13 = Master I was
            // PanSurveyor.MasterLevel, 17 = GM was a literal 17. Only the lever is new.
            [BoreDepthLevel] = Leveling.Rank.Master,
            [BoreDepthExactLevel] = Leveling.Rank.Grandmaster,
        }
    };

    /// <summary>Treasure-tail bias for a level: 1.0 below Master I, linear Master -> GM.</summary>
    public static double TreasureBiasFor(int level)
    {
        if (level < 13) return 1.0;
        double m = Knob(TreasureBiasMaster, 1.3), g = Knob(TreasureBiasGm, 2.0);
        int max = Leveling.Domain.MaxLevelDefault;
        if (level >= max) return g;
        return m + (g - m) * (level - 13) / (double)(max - 13);
    }

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
        var set = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>A Bonus knob, falling back to the shipped default if the server dropped it.</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.ServerInstance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }
}

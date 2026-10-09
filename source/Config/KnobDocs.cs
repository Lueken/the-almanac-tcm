using System;
using System.Collections.Generic;
using System.Text;

namespace AlmanacTcm.Config;

/// <summary>
/// Operator-facing documentation for every server config value, stamped INTO the config files
/// themselves on load.
///
/// WHY THIS EXISTS (2026-10-07 jl). Thalius asked how to turn the panning whisper off. The answer
/// was two numbers in ModConfig/almanactcm/PAN.json, and there was no way for him to have found
/// that on his own: 275 Bonus knobs, 104 techniques and 46 global fields ship with their only
/// explanation in C# doc comments, so "what does this number do" could be answered by reading the
/// source or by asking the author, and by nothing else. A server keeper editing a config file
/// should have to do neither.
///
/// WHAT IT IS. A flat registry keyed "DOMAIN/knobKey" (plus the global config's property names),
/// read by <see cref="Stamp(DomainConfig)"/> and <see cref="Stamp(TcmGlobalConfig)"/> and written
/// into the `_readme`, `_techniqueHelp` and `_bonusHelp` properties on the model. Those three are
/// GENERATED: rebuilt from here on every load, so they cannot drift from the code, they come back
/// if an operator deletes them, and an operator who edits one loses the edit on the next boot.
///
/// WHY NOT JSON COMMENTS. Newtonsoft tolerates `//` on read, but StoreModConfig re-serialises the
/// object graph on every write and a write happens on any merge. Comments would survive until the
/// next mod update and then vanish without a word, which is worse than never having had them.
/// See the note at the top of <see cref="DomainConfig"/>.
///
/// HOUSE STYLE FOR A HELP LINE. One line, no wrapping, plain ASCII (an operator may well be
/// reading this in Notepad, and these strings land in a JSON file). Lead with the units or the
/// shape - "0-1 chance", "multiplier", "level", "seconds", "days" - then what it does, then the
/// value that switches it off if there is one. Say what happens at the ENDS of a curve rather than
/// naming the curve. Do not restate the design rationale; that is what the doc comment on the knob
/// is for, and this text is read by someone who wants to change a number, not litigate it.
///
/// ADDING A KNOB. Put a line in <c>KnobDocs.Domains.cs</c> keyed "CODE/yourKnobKey". If you forget,
/// the stamp writes <see cref="UndocumentedNote"/> naming the knob and the server log warns once
/// with the list, so a gap is loud rather than silent.
/// </summary>
public static partial class KnobDocs
{
    /// <summary>What a value with no registry entry gets, so a gap is visible in the file rather
    /// than absent from it. Also what <see cref="UndocumentedReport"/> counts.</summary>
    public const string UndocumentedNote =
        "(no description recorded yet - please report this key so it gets one)";

    /// <summary>How many gaps the boot warning names before it truncates.</summary>
    private const int ReportCap = 25;

    /// <summary>Key prefix for ARC's per-world-ritual completion pay, documented by generation
    /// rather than by eighteen registry lines. Mirrors ArcDomain's own ritualRaw* key naming.</summary>
    private const string RitualRawPrefix = "ritualRaw";

    // ---------------------------------------------------------------- the per-file readme

    /// <summary>The orientation block every domain file carries. Deliberately repeated in all 22
    /// files rather than pointed at a central document: the operator has THIS file open.</summary>
    private static List<string> DomainReadmeFor(string code) => new()
    {
        $"ModConfig/almanactcm/{code}.json - The Almanac: Trades, Callings & Mastery, the {code} domain.",
        "SERVER-SIDE ONLY. Never synced to clients, so a server may diverge from shipped defaults invisibly.",
        "Edit a value, then RESTART the server. There is no config reload command.",
        "",
        "AN UPGRADE WILL NOT OVERWRITE WHAT YOU TUNED. On load each value is compared three ways: what",
        "this file holds, what the mod shipped the last time it wrote this file (the _shipped*Baseline",
        "maps at the bottom), and what it ships now. Equal to the baseline means you never touched it,",
        "so it follows the new default. Anything else is treated as deliberate, kept, and logged as a",
        "skip. Newly shipped techniques and knobs are always added.",
        "",
        "FIELDS:",
        "  Smax         the daily practice asymptote. 100 for every domain by design; cadence",
        "               differences belong in K and Raw, not here.",
        "  M            how many techniques a full breadth-phase day needs. Each technique's daily",
        "               share is capped at Smax/M, so M must be at most the number of verbs a player",
        "               can actually reach, or a perfect day cannot complete. NOT part of the",
        "               three-way merge: a new shipped default for M reaches new installs only, so an",
        "               existing server has to change it here by hand.",
        "  TierTotals   cumulative banked XP to REACH Novice I, Apprentice I, Journeyman I, Master I",
        "               and Grandmaster. Five ascending numbers. Also NOT merged: a shipped change",
        "               reaches new worlds only.",
        "  Adjacency    domain codes this one spills practice into and receives from (the rate is",
        "               `sigma` in global.json). Hand-authored matrix; keep it reciprocal.",
        "  Techniques   the verbs. Raw is the practice logged per action before saturation. K is the",
        "               raw total at which half this technique's daily share is banked, so a large K",
        "               suits a frequent action and a small K a rare deliberate one. Raw and K are the",
        "               two numbers a retune moves; CoGrants, IfModPresent and RawScale are structural",
        "               rather than balance, and are additive-only across upgrades.",
        "  Bonus        the per-domain levers. One line each in _bonusHelp below.",
        "",
        "BREADTH AND DEPTH. Below Journeyman I a domain is in BREADTH phase: every technique is capped",
        "at Smax/M, so filling a day means practising several verbs. From Journeyman I it switches to",
        "DEPTH phase, where the technique you have practised most over the last `dominantWindowDays`",
        "carries full weight and the others count at `depthOffTechniqueWeight`. Both knobs are in",
        "global.json.",
        "",
        "RANKS, for any knob that takes a level: 0 Untrained, 1 Novice I, 5 Apprentice I,",
        "9 Journeyman I, 13 Master I, 17 Grandmaster (terminal, no sub-levels). Levels 1 to 16 run",
        "four sub-levels per tier. Setting a rank gate above 17 disables it; setting it to 0 opens it",
        "to everyone.",
        "",
        "_readme, _techniqueHelp and _bonusHelp are GENERATED. They are rewritten from the mod on",
        "every boot: edits to them are lost, deleting them brings them back, and nothing in them",
        "affects play.",
    };

    /// <summary>global.json's own orientation block.</summary>
    private static readonly string[] GlobalReadme =
    {
        "ModConfig/almanactcm/global.json - The Almanac: Trades, Callings & Mastery, engine-wide settings.",
        "SERVER-SIDE ONLY. Never synced to clients except where a field's help line says otherwise.",
        "Edit a value, then RESTART the server. There is no config reload command.",
        "",
        "THIS FILE IS NOT THREE-WAY MERGED. Unlike the per-domain files, a key that already exists here",
        "is kept exactly as written and a new shipped default NEVER reaches it. A retune of a value you",
        "have already booted once has to be made here by hand. Keys absent from the file do pick up the",
        "shipped default and are written back on the next boot.",
        "",
        "Per-domain tuning (Smax, M, TierTotals, technique Raw/K, and the bonus levers) lives in the",
        "sibling files: MIN.json, WOO.json, FAR.json and so on, one per domain, each carrying its own",
        "_readme and per-knob help.",
        "",
        "RANKS, for any field that takes a level: 0 Untrained, 1 Novice I, 5 Apprentice I,",
        "9 Journeyman I, 13 Master I, 17 Grandmaster (terminal). A rank GATE is disabled by setting it",
        "to 0 and made unreachable by setting it above 17.",
        "",
        "_readme and _help are GENERATED: rewritten from the mod on every boot, edits lost, deleting",
        "them brings them back, and nothing in them affects play.",
    };

    // ---------------------------------------------------------------- stamping

    /// <summary>Rebuilds the three generated blocks on a domain config. Returns true when anything
    /// changed, so the caller only writes the file when it must.</summary>
    public static bool Stamp(DomainConfig dc)
    {
        if (dc == null) return false;
        bool changed = false;

        var readme = DomainReadmeFor(dc.Code);
        if (!SameList(dc.Readme, readme)) { dc.Readme = readme; changed = true; }

        // The help maps are rebuilt in the SAME key order as the maps they describe, so the two
        // blocks read side by side instead of having to be cross-referenced.
        var techHelp = new Dictionary<string, string>();
        foreach (string name in dc.Techniques.Keys)
            techHelp[name] = Technique.TryGetValue(dc.Code + "/" + name, out string? t) ? t : UndocumentedNote;
        if (!SameMap(dc.TechniqueHelp, techHelp)) { dc.TechniqueHelp = techHelp; changed = true; }

        var bonusHelp = new Dictionary<string, string>();
        foreach (string key in dc.Bonus.Keys) bonusHelp[key] = BonusHelpFor(dc.Code, key);
        if (!SameMap(dc.BonusHelp, bonusHelp)) { dc.BonusHelp = bonusHelp; changed = true; }

        return changed;
    }

    /// <summary>Rebuilds the two generated blocks on global.json. Same contract as above.</summary>
    public static bool Stamp(TcmGlobalConfig cfg)
    {
        if (cfg == null) return false;
        bool changed = false;

        if (!SameList(cfg.Readme, new List<string>(GlobalReadme)))
        {
            cfg.Readme = new List<string>(GlobalReadme);
            changed = true;
        }

        // Reflection over the properties rather than a hand-kept field list: a newly added global
        // field cannot be silently left out of the help block, it turns up carrying
        // UndocumentedNote instead, and UndocumentedReport then names it in the log.
        var help = new Dictionary<string, string>();
        foreach (var prop in typeof(TcmGlobalConfig).GetProperties())
        {
            if (prop.Name is nameof(TcmGlobalConfig.Readme) or nameof(TcmGlobalConfig.Help)) continue;
            help[prop.Name] = Global.TryGetValue(prop.Name, out string? h) ? h : UndocumentedNote;
        }
        if (!SameMap(cfg.Help, help)) { cfg.Help = help; changed = true; }

        return changed;
    }

    /// <summary>The help line for one Bonus key: the registry entry, or the generated floor-knob
    /// line, or the placeholder.</summary>
    private static string BonusHelpFor(string code, string key)
    {
        if (Bonus.TryGetValue(code + "/" + key, out string? help)) return help;

        // The per-technique tail floor is formulaic across twenty-odd domains, so it is generated
        // from the key rather than listed once per technique.
        if (key.Length > DomainConfig.FloorKnobSuffix.Length
            && key.EndsWith(DomainConfig.FloorKnobSuffix, StringComparison.Ordinal))
        {
            string tech = key.Substring(0, key.Length - DomainConfig.FloorKnobSuffix.Length);
            return $"Minimum banked practice per raw unit of '{tech}' once the saturation tail falls below it, "
                 + "so the day's last acts still pay something visible and the technique REACHES its cap "
                 + "instead of trailing toward zero forever. Convention is 0.5 / Raw, i.e. half a point per "
                 + "act. 0 or absent = pure saturation curve, unchanged.";
        }

        // ARC ships one knob per Rustbound world ritual so a server can price workings by tier
        // without a code change, and a new RBM ritual adds another. Generated for the same reason
        // as the floor knobs: eighteen near-identical lines would rot, and a nineteenth would
        // arrive undocumented.
        if (code == "ARC" && key.StartsWith(RitualRawPrefix, StringComparison.Ordinal))
            return $"Raw practice banked by one completed world ritual ({key.Substring(RitualRawPrefix.Length)}). "
                 + "A working is a one-shot, resource-heavy evening, so this sits well above the laboratory raw; "
                 + "the shipped 16 is about 4x it. Priced per ritual so pay can follow ritual tier. Tuning the "
                 + "laboratory technique's Raw does not skew these, the bank-time maths divides it back out.";

        return UndocumentedNote;
    }

    /// <summary>Names the techniques and knobs with no registry entry, for the one-line boot
    /// warning. Keeps a documentation gap loud rather than silent (CONVENTIONS.md section 5).</summary>
    public static string? UndocumentedReport(IEnumerable<DomainConfig> configs)
    {
        var gaps = new List<string>();
        foreach (var dc in configs)
        {
            if (dc == null) continue;
            foreach (var (name, text) in dc.TechniqueHelp)
                if (text == UndocumentedNote) gaps.Add($"{dc.Code}/{name} (technique)");
            foreach (var (key, text) in dc.BonusHelp)
                if (text == UndocumentedNote) gaps.Add($"{dc.Code}/{key}");
        }
        if (gaps.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append(gaps.Count).Append(" config value(s) carry no help text in KnobDocs: ");
        sb.Append(string.Join(", ", gaps.Count > ReportCap ? gaps.GetRange(0, ReportCap) : gaps));
        if (gaps.Count > ReportCap) sb.Append(", ...and ").Append(gaps.Count - ReportCap).Append(" more");
        return sb.ToString();
    }

    private static bool SameList(List<string>? a, List<string> b)
    {
        if (a == null || a.Count != b.Count) return false;
        for (int i = 0; i < b.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool SameMap(Dictionary<string, string>? a, Dictionary<string, string> b)
    {
        if (a == null || a.Count != b.Count) return false;
        // ORDER MATTERS here, unlike an ordinary map compare: the help block is written to mirror
        // the order of the map it describes, so a reordered Bonus map really is a change that
        // should be rewritten rather than left looking mismatched.
        using var ea = a.GetEnumerator();
        using var eb = b.GetEnumerator();
        while (ea.MoveNext() && eb.MoveNext())
            if (ea.Current.Key != eb.Current.Key || ea.Current.Value != eb.Current.Value) return false;
        return true;
    }

    // ---------------------------------------------------------------- global.json

    /// <summary>global.json, keyed by PROPERTY name (the serialised name). Reflection walks the
    /// type, so a field missing from here is reported rather than skipped.</summary>
    private static readonly Dictionary<string, string> Global = new()
    {
        // ---- the engine
        ["ConsolidationHour"] =
            "In-game hour (0-23) at which the day's practice settles into rank. 3 = 3am, so a session that "
          + "runs past midnight banks as one day's work rather than two.",
        ["GmDomainCap"] =
            "How many domains one player may ever hold at Grandmaster. Lowering it never revokes a GM already "
          + "attained; it only stops the next one.",
        ["LambdaDeath"] =
            "0-1. Share of PENDING (not yet consolidated) practice scattered when you die. Banked rank is never "
          + "touched. 0 = death costs no practice.",
        ["LambdaPvp"] =
            "0-1. Same as lambdaDeath but for a death caused by another player, so PvP can be priced differently. "
          + "Defaults to the same value.",
        ["ChainDeathCooldownHours"] =
            "In-game hours after a penalised death during which further deaths scatter nothing, so one bad fight "
          + "cannot cascade. 0 = every death is charged.",
        ["Sigma"] =
            "0-1. Share of a domain's banked XP that spills to each of its Adjacency neighbours at consolidation. "
          + "0 = no spillover anywhere.",
        ["SpilloverCapPct"] =
            "Percent of the RECEIVING domain's Smax that spillover may contribute in one day, however many "
          + "neighbours feed it. Caps a wide adjacency row from carrying a trade on its own.",
        ["DepthOffTechniqueWeight"] =
            "0-1. In depth phase (Journeyman I and up) the dominant technique counts in full and every other one "
          + "counts at this weight. 1 = depth phase behaves like breadth with no cap.",
        ["DominantWindowDays"] =
            "Rolling window, in in-game days, used to elect the dominant technique for depth phase. Longer = a "
          + "specialism is slower to switch.",
        ["DedupWindowSeconds"] =
            "Seconds within which an IDENTICAL practice context logs zero raw: the place-and-rebreak guard. "
          + "Lower it and repetitive farming gets cheaper; raise it and honest batch production starts reading "
          + "as a repeat.",
        ["DedupRingSize"] =
            "How many recent practice contexts per player the dedup ring remembers. Raise it only alongside "
          + "dedupWindowSeconds; a ring shorter than the window's worth of distinct actions lets repeats through.",
        ["PracticeGainMessages"] =
            "true/false. Send each practice gain (and each dedup'd repeat) to the player's chat Info tab. Noisy "
          + "but it is how players learn what pays; turn off for a quiet server.",
        ["PracticeGainToasts"] =
            "true/false. Emit the per-gain HUD toast packet. The client decides whether to draw it. false = no "
          + "toast packets leave the server at all.",
        ["VerboseDebugLogging"] =
            "true/false. Category debug lines in the server log. Leave on while tuning, off for a quiet log.",

        // ---- gates
        ["MaterialGateMET"] =
            "true/false. Below the required Metalworking rank a player cannot smelt, form or cast a metal above "
          + "their tier. Assembly is never gated. false disables the gate entirely.",
        ["MaterialGateMETHardcore"] =
            "true/false. What a gated attempt does: false blocks the interaction with a warning and loses nothing "
          + "(default), true lets it through and wastes the material.",
        ["MaterialGateMETUnmappedLevel"] =
            "Required MET level for a metal the classification map does not know, i.e. a mod-added one. 0 = allow "
          + "it and log once, so nothing is locked by accident; raise it to gate unknown metals conservatively.",
        ["StormShiftTEM"] =
            "true/false. Deliver the first temporal-storm warning per player by TEM rank instead of broadcasting "
          + "it to everyone at 0.35 days. false restores stock broadcast behaviour for all.",
        ["RepairGateTEMLevel"] =
            "Minimum TEM level to repair a translocator or recharge a discharged teleporter. Stepping through a "
          + "working machine is never gated. 0 disables the gate.",
        ["Place2x2GatePOTLevel"] =
            "Minimum POT level for the clayforming 2x2 tool mode, adding and removing alike. The 1x1 stroke and "
          + "the powered wheel are never gated. 0 disables.",
        ["Place3x3GatePOTLevel"] =
            "Minimum POT level for the clayforming 3x3 tool mode. 0 disables.",
        ["OvenGateCOOLevel"] =
            "Minimum COO level to use the Stone Bake Oven at all: no cooking, no adding a pan or pot, no loading "
          + "firewood below it, because a partly usable oven just wastes fuel. The vanilla clay oven is never "
          + "gated. 0 disables.",
        ["GearGateFISLevel"] =
            "Minimum FIS level to SET refined fishing gear (place the trap, swing the net). Servicing gear already "
          + "placed is never gated, and bait, fillet knife and logbook stay open. 0 disables.",
        ["AlloyLedgerGated"] =
            "true/false. true = only an Apprentice+ of Metalworking can open the Alloy Ledger on a crucible; false "
          + "= anyone can. Synced to clients on join, since the ledger opens client-side.",

        // ---- the Grower's Eye
        ["GrowerEyeFAR"] =
            "true/false. true = farmland and crop hover info is gated by the viewer's FAR rank and their "
          + "familiarity with that crop; false = vanilla shows everything to everyone. Synced to clients on join. "
          + "The rank rungs themselves are fixed, not knobs: rough words from Novice, full figures from "
          + "Apprentice, family-wide from Journeyman.",
        ["FamAcquaintedHarvests"] =
            "Credited harvest DAYS with one crop before it reads as Acquainted (the rough hover readout).",
        ["FamVersedHarvests"] =
            "Credited harvest DAYS with one crop before it reads as Versed (the full figures).",
        ["FamMaxCreditsPerDay"] =
            "Credits a single crop can earn in one in-game day, however much of it is brought in. 1 means a "
          + "hundred tiles and one tile teach the same amount, so staggered plantings out-teach acreage. Raise it "
          + "to soften the calendar.",
        ["FamSpread"] =
            "0-1. Share of a crop's family-mates' counters that counts toward it, so knowing one legume teaches "
          + "you something about all legumes. 0 = no family knowledge at all.",
        ["FamFamilyVersedSum"] =
            "Summed family counters (the crop's own included) at which the Journeyman family-wide read opens.",
        ["FamKinCeiling"] =
            "Ceiling, in effective count, on what family knowledge ALONE can carry a crop to. Set one short of "
          + "famVersedHarvests so kin can make a crop Acquainted but never Versed. Your own experience with the "
          + "plant adds on top and pushes straight through this.",
        ["FamCountCap"] =
            "Per-crop familiarity counter ceiling, purely to bound the synced store. Far past every threshold at "
          + "the default; not a tuning lever.",

        // ---- soil sickness
        ["SoilSicknessFAR"] =
            "true/false. The reason to rotate crops. false leaves soil exactly as vanilla and the other mods left "
          + "it, which on long months means no rotation pressure of any kind.",
        ["SickAccrualPerHarvest"] =
            "Sickness level one harvest DAY of an already-sick family adds to a tile. This and the two decay knobs "
          + "are a set, and the behaviour lives in their ratio rather than in any one value: accrual should sit "
          + "between 1.5 and 3 cycles' worth of occupied decay, or a two-course rotation cleans up (too low) or a "
          + "four-course cannot keep up (too high).",
        ["SickFallowDecayPerDay"] =
            "Sickness level shed per in-game day by BARE ground.",
        ["SickOccupiedDecayFactor"] =
            "0-1. Share of the bare-ground decay rate that ground under a crop gets. Below 1 so fallow is always "
          + "the faster cure; near 1 so rotation is nearly as good AND still pays a harvest, which is what makes "
          + "rotation the right answer and fallow the last resort.",
        ["SickCleanBelow"] =
            "Nothing is felt below this level. MUST sit ABOVE sickAccrualPerHarvest, or a single harvest clears "
          + "the line and even a flawless rotation is bitten on every cycle. Above the accrual, one harvest is "
          + "always free and only repetition costs anything.",
        ["SickTiringAbove"] =
            "Level at which the ground starts SAYING it is tiring, well before anything is charged for it. Must "
          + "sit below sickCleanBelow or the warning tier never appears. Set it a little under the accrual so the "
          + "warning shows up exactly when a repeat would cost something.",
        ["SickMaxSpeedPenalty"] =
            "0-1. Worst-case growth-speed penalty on fully sick ground, multiplying with vanilla's nutrient "
          + "speed bands. Kept well above 0 on purpose: a tile that can never grow again is a dead square "
          + "someone will just re-till. 0 = no speed penalty.",
        ["SickMaxYieldPenalty"] =
            "0-1. Worst-case yield penalty on fully sick ground. Kept below the speed penalty so a sick tile is a "
          + "slow disappointment rather than two punishments for one mistake. 0 = no yield penalty.",
        ["SickFertilityDecayBonus"] =
            "How much faster the richest ground sheds sickness than the poorest, as a fraction (0.5 = up to half "
          + "again as fast). BARE GROUND ONLY: a standing crop is a host, so no rotation is affected at any "
          + "setting and only rest is sped up. 0 = fertility does not matter.",
        ["SickFertilityFloor"] =
            "Stored farmland fertility that gets no decay bonus. This is FARMLAND's own scale (verylow 5, low 25, "
          + "medium 50, compost 65, high 80), NOT the 100-300 fertilityByType numbers in soil.json. Using the "
          + "soil.json scale here would put every farmland below the floor and silently disable the whole bonus.",
        ["SickFertilityCeiling"] =
            "Stored farmland fertility at which the decay bonus is full; above it it clamps. 80 is vanilla "
          + "high-fertility soil. Same scale caveat as sickFertilityFloor.",
        ["SickBiofumigation"] =
            "true/false. Whether turning a brassica in with the hoe clears sickness. false leaves the hoe doing "
          + "exactly what vanilla does, so a server can have sickness with no cure.",
        ["SickBiofumigationClearShare"] =
            "0-1. Share of EVERY family's level that one turned-in brassica clears from that tile. A strong cure "
          + "and deliberately not a reset: at 0.8 a maxed tile comes back to 20, under the tiring line, but "
          + "ground abused twice over still is not clean in one pass.",
        ["SickBiofumigationReadRank"] =
            "Minimum FAR level at which the turn-in REPORTS itself: which families eased, and by how much. The "
          + "labour is never gated, so the ground clears for an Untrained hand exactly as it does for a Master; "
          + "rank only buys being told. 0 = everyone is told.",
    };
}

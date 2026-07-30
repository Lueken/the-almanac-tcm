using System.Collections.Generic;

namespace AlmanacTcm.Config;

/// <summary>
/// One-time bootstrap for the three-way merge (0.4.16). Config files written before 0.4.16 carry
/// no ShippedTechniqueBaseline, so on the first upgrade boot there is nothing to compare a value
/// against and the merge could not tell a pristine default from a tuned one.
///
/// The rule that fills the gap:
///   • A domain whose shipped defaults did NOT move in this release can seed its baseline from the
///     CURRENT defaults — for those domains the current default IS what the previous build shipped,
///     so a value that differs is necessarily operator tuning. That case needs no table and is
///     handled in LedgerSystem, not here.
///   • A domain whose defaults DID move needs the OLD numbers written down, or the merge would read
///     every pristine value as tuned and the retune would never land. That is what this file is.
///
/// So this table only ever lists domains retuned in the release that introduced the baseline for
/// them. Entries stay forever (a server can upgrade from 0.4.15 at any later date) but the file
/// does not grow with every release — once a domain's configs carry a baseline in the wild, later
/// retunes need no entry at all.
/// </summary>
public static class LegacyBaselines
{
    /// <summary>Domain code → (technique → "raw|k") as shipped by the build BEFORE the retune.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> TechniqueBaselines = new()
    {
        // COO as shipped through 0.4.15, before the 2026-07-29 cooking pacing retune. The staple
        // kitchen verbs carried frequent-action K values (30/25) that did not match real cooking
        // cadence; 0.4.16 moved eight of the ten.
        ["COO"] = new()
        {
            ["mealpot"] = "3|30",
            ["directheat"] = "1|30",
            ["mixing"] = "2|30",
            ["milling"] = "1|30",
            ["baking"] = "2|25",
            ["griddling"] = "2|25",
            ["juicing"] = "2|25",
            ["prep"] = "2|25",
            // Unchanged in 0.4.16, listed so the COO baseline is complete rather than partial.
            ["drying"] = "4|12",
            ["salting"] = "3|12",
        },
    };

    /// <summary>The pre-retune technique baseline for a domain, or null when the domain's defaults
    /// did not move and the caller should seed from current defaults instead.</summary>
    public static Dictionary<string, string>? For(string domainCode) =>
        TechniqueBaselines.TryGetValue(domainCode, out var b) ? b : null;
}

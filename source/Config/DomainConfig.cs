using System.Collections.Generic;
using Newtonsoft.Json;

namespace AlmanacTcm.Config;

/// <summary>
/// Per-domain engine config (xp-engine-design.md §9), one file per domain at
/// ModConfig/almanactcm/{domain}.json. SERVER-SIDE ONLY, never synced — tuned
/// values (K, raw, tier totals) are exactly the numbers a server may want to
/// keep divergent from shipped defaults.
/// </summary>
public class DomainConfig
{
    /// <summary>Three-letter domain code (MET, COO, …). Matches the filename.</summary>
    public string Code { get; set; } = "";

    /// <summary>Daily banked-practice asymptote. Normalized to 100 for every domain —
    /// frequency differences are absorbed by K and per-action raw values, never Smax.</summary>
    public double Smax { get; set; } = 100.0;

    /// <summary>Techniques needed for a full breadth-phase day (cap splits Smax/m).
    /// Single-technique domains use 1.</summary>
    public int M { get; set; } = 3;

    /// <summary>Banked XP required to complete each tier climb, Untrained→GM order.
    /// Defaults are the §7 pacing table; every domain shares one table by design.
    /// ObjectCreationHandling.Replace on every collection here: without it Json.NET
    /// APPENDS file values onto these defaults on reload (the 10-entry-TierTotals
    /// server crash of 2026-07-13).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<double> TierTotals { get; set; } = new() { 150, 500, 1400, 3200, 6500 };

    /// <summary>Adjacent domain codes for spillover (hand-authored matrix).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Adjacency { get; set; } = new();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, TechniqueConfig> Techniques { get; set; } = new();

    /// <summary>Per-domain bonus-axis knobs (over-strike chance, shatter factors,
    /// fuel-economy curve points…). Server-side only like everything here — these
    /// are exactly the numbers a server may want to quietly diverge on.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> Bonus { get; set; } = new();

    // ---------------------------------------------------------------- merge baselines
    //
    // The third leg of the three-way merge (added 0.4.16). The additive-only migration that
    // shipped before this could introduce NEW technique and knob keys but could never move a
    // value that already existed, because there was no way to tell "still the shipped default"
    // from "the operator deliberately tuned this". A balance retune therefore never reached any
    // server that had already booted once — the COO cooking pacing bug.
    //
    // These record what the MOD shipped as of the last time it wrote this file. On load:
    //   current == baseline  -> untouched by the operator, adopt the new shipped default
    //   current != baseline  -> deliberately tuned, keep it and log the skip
    // So "values the server already tuned are never touched" stays literally true, while
    // untouched values track shipped defaults across upgrades.
    //
    // Techniques are fingerprinted "raw|k" rather than stored as whole objects: those are the
    // only two balance numbers a retune moves, and it keeps the file readable. Structural fields
    // (CoGrants, IfModPresent, RawScale) are not balance knobs and stay additive-only.

    /// <summary>Technique name → "raw|k" as SHIPPED at the last write. Not a tuning surface;
    /// editing it only changes what the next upgrade considers pristine.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, string> ShippedTechniqueBaseline { get; set; } = new();

    /// <summary>Bonus knob → value as SHIPPED at the last write. Same contract as above.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> ShippedBonusBaseline { get; set; } = new();

    /// <summary>The fingerprint format the baseline stores. Invariant culture on purpose: a
    /// server on a comma-decimal locale must still match a baseline written elsewhere.</summary>
    public static string Fingerprint(TechniqueConfig t) =>
        t.Raw.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|"
        + t.K.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public class TechniqueConfig
{
    /// <summary>Raw practice at which half this technique's cap share is banked.
    /// Large K = frequent-action technique; small K = rare-session technique.</summary>
    public double K { get; set; } = 50.0;

    /// <summary>Raw practice value logged per action (before saturation).</summary>
    public double Raw { get; set; } = 1.0;

    /// <summary>Secondary-domain XP shares (domain code → fraction). Applied at
    /// consolidation OUTSIDE the receiving domain's saturation sum; never fills
    /// breadth slots (FAR Q2 ruling).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> CoGrants { get; set; } = new();

    /// <summary>Optional presence-conditioned raw scaling: when the named mod is
    /// installed, Raw is multiplied by RawScale (PAN/bettererprospecting pattern).</summary>
    public string? IfModPresent { get; set; }

    public double RawScale { get; set; } = 1.0;
}

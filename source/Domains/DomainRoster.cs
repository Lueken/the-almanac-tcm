using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// The canonical 22-domain roster, in registration order. APPEND-ONLY: client
/// and server both register from this list, so ids (assigned by index) stay in
/// lockstep across the wire; save files key by Code and survive reordering, but
/// live packets do not — never reorder or remove within a release line.
/// Display names follow the GM badge identity sheet (2026-07-12).
///
/// The Callings page groups this list by the strand table below for display.
/// That grouping is presentation only and must never be pushed back into this
/// array.
/// </summary>
public static class DomainRoster
{
    /// <summary>
    /// RequiredMod: a single modid that must be enabled for the domain to exist.
    /// RequiredAnyMods: the domain exists when ANY ONE of these is enabled, for a
    /// trade more than one mod can supply (beekeeping arrives through Oreki's
    /// Beehives or From Golden Combs, and either is enough). Both null = unconditional;
    /// a conditional domain still registers, disabled, so packet ids never shift.
    /// </summary>
    public record Entry(string Code, string DisplayName, string? RequiredMod = null, string[]? RequiredAnyMods = null)
    {
        /// <summary>Whether this domain's supporting mod (or mods) are present.</summary>
        public bool IsEnabled(ICoreAPI api)
        {
            if (RequiredMod != null && !api.ModLoader.IsModEnabled(RequiredMod)) return false;
            if (RequiredAnyMods is { Length: > 0 })
            {
                foreach (string modid in RequiredAnyMods)
                    if (api.ModLoader.IsModEnabled(modid)) return true;
                return false;
            }
            return true;
        }
    }

    public static readonly Entry[] All =
    {
        new("MIN", "Mining"),
        new("WOO", "Woodcutting & Forestry"),
        new("FAR", "Farming & Husbandry"),
        new("FIS", "Fishing"),
        new("COO", "Cooking"),
        new("MET", "Metalworking"),
        new("POT", "Pottery"),
        new("GLA", "Glassmaking", "glassmakingfork"),
        new("PAN", "Panning & Prospecting"),
        new("HUN", "Hunting"),
        new("ANI", "Animal Handling"),
        new("FOR", "Foraging"),
        new("ALC", "Alchemy"),
        new("BRE", "Brewing & Fermentation"),
        new("TAI", "Tailoring"),
        new("MAS", "Masonry"),
        new("ENG", "Engineering"),
        new("MEL", "Melee"),
        new("RAN", "Ranged"),
        new("TEM", "Temporal"),
        new("ARC", "Arcana", "rustboundmagic"),
        // Appended, never inserted at B: ids are the wire protocol. The Callings
        // page sorts it into place for the reader.
        new("BEE", "Beekeeping", RequiredAnyMods: new[] { "orekiwoofsbeehives", "fromgoldencombs" }),
    };

    /// <summary>
    /// The six calling strands in SITE order — the order thequirevs.com/callings.html
    /// prints them (grouping adopted 2026-08-19), which the index page follows.
    /// Membership is by Code and order within a strand is print order. Presentation
    /// only: ids, saves and the wire never see this table. A roster entry missing
    /// from every strand still prints (the index appends it, visibly unsorted) —
    /// a new domain should be added here the day it is added above.
    /// </summary>
    public static readonly (string Name, string[] Codes)[] Strands =
    {
        ("Field & Fold",   new[] { "WOO", "FAR", "ANI", "BEE", "FOR", "HUN" }),
        ("Forge & Kiln",   new[] { "MET", "MAS", "ENG", "POT", "GLA" }),
        ("Hearth & Cask",  new[] { "COO", "BRE", "TAI", "ALC" }),
        ("Stone & Stream", new[] { "MIN", "PAN", "FIS" }),
        ("Arms",           new[] { "MEL", "RAN" }),
        ("The Unquiet",    new[] { "ARC", "TEM" }),
    };
}

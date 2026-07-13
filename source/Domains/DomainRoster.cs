namespace AlmanacTcm.Domains;

/// <summary>
/// The canonical 21-domain roster, in registration order. APPEND-ONLY: client
/// and server both register from this list, so ids (assigned by index) stay in
/// lockstep across the wire; save files key by Code and survive reordering, but
/// live packets do not — never reorder or remove within a release line.
/// Display names follow the GM badge identity sheet (2026-07-12).
/// </summary>
public static class DomainRoster
{
    /// <summary>RequiredMod: modid that must be enabled for the domain to exist
    /// (conditional domains register disabled when it is absent).</summary>
    public record Entry(string Code, string DisplayName, string? RequiredMod = null);

    public static readonly Entry[] All =
    {
        new("MIN", "Mining"),
        new("WOO", "Woodcutting & Forestry"),
        new("FAR", "Farming & Husbandry"),
        new("FIS", "Fishing"),
        new("COO", "Cooking"),
        new("MET", "Metalworking"),
        new("POT", "Pottery"),
        new("GLA", "Glassmaking"),
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
    };
}

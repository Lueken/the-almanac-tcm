namespace AlmanacTcm.Leveling;

/// <summary>
/// The rank boundaries, as LEVELS, in one place.
///
/// WHY THIS EXISTS (2026-08-12). A rank boundary had no name, so it was spelled five different
/// ways across the mod: ten identical <c>ProvJourneyman/ProvMaster/ProvGm = 9, 13, 17</c> triplets
/// (AlcDomain, AniDomain, BreDomain, EngDomain, FarDomain, GlaDomain, MasDomain, PotDomain,
/// TaiDomain, plus COO's <c>Tier*</c> variant), four private copies in MetMaterialGate, six hand
/// re-derivations from <c>SubLevelsPerTier</c>, about eleven bare literals, and three open-coded
/// copies of <c>Domain.TierOf</c>. All ten triplets were numerically identical, so they could not
/// drift from each other, but every one of them could drift from <c>SubLevelsPerTier</c>, which is
/// the actual latent bug: changing the ladder's shape would have silently desynced them.
///
/// LEVEL, NOT TIER, AND DELIBERATELY. Level is already the spine of this mod: the savegame
/// (PlayerDomain), the wire (Packets), and every <c>LevelOf</c>/<c>ClientLevel</c> accessor carry a
/// level. Tier has no independent demand anywhere: every <c>TierOf</c> call site derives it from a
/// level the caller already held. Level is also lossless, since <c>Domain.TierOf</c> and
/// <c>Domain.SubLevelOf</c> recover the band and the numeral from it, while the reverse throws away
/// thirteen of eighteen states.
///
/// Spelling matches the house style already published in CONVENTIONS.md section 2:
/// <c>if (level &lt; Rank.Journeyman) return;</c>
///
/// The ladder these derive from (Domain.cs:15-20): level 0 is Untrained, levels 1-16 are
/// Novice I through Master IV at four per tier, and level 17 is Grandmaster, terminal and
/// unnumbered (RULED 2026-07-15, "GM is GM, no ranks within it").
/// </summary>
public static class Rank
{
    // ---------------------------------------------------------------- band entry levels
    // The level at which a player BECOMES that rank. This is the common form: a gate asks
    // "are they at least a Journeyman", which is `level >= Rank.Journeyman`.

    /// <summary>0. The floor. Not a named tier: <c>Domain.TierOf(0)</c> is -1 by design.</summary>
    public const int Untrained = 0;

    /// <summary>1. Novice I. Clears every Untrained penalty band.</summary>
    public const int Novice = 1;

    /// <summary>5. Apprentice I. (Was ApprenticeI in AffinitySystem and MetMaterialGate.)</summary>
    public const int Apprentice = Domain.SubLevelsPerTier + 1;

    /// <summary>9. Journeyman I. The provenance-mark floor for most domains.
    /// (Was ProvJourneyman in nine domains, TierJourneyman in COO, JourneymanI in
    /// MetMaterialGate, and JourneymanEntry in LedgerSystem.)</summary>
    public const int Journeyman = 2 * Domain.SubLevelsPerTier + 1;

    /// <summary>13. Master I. (Was ProvMaster / TierMaster / MasterI.)</summary>
    public const int Master = 3 * Domain.SubLevelsPerTier + 1;

    /// <summary>17. Grandmaster, terminal and unnumbered. Equals <c>Domain.MaxLevelDefault</c>.
    /// (Was ProvGm / TierGm / GmCeiling.)</summary>
    public const int Grandmaster = 4 * Domain.SubLevelsPerTier + 1;

    // ---------------------------------------------------------------- band exit levels
    // The LAST level of a band, used for ceilings ("hard-walled at the end of Journeyman")
    // rather than for thresholds. Kept separate so `Rank.Master` is unambiguously the entry.

    /// <summary>4. The last Novice level.</summary>
    public const int NoviceIV = Domain.SubLevelsPerTier;

    /// <summary>8. The last Apprentice level.</summary>
    public const int ApprenticeIV = 2 * Domain.SubLevelsPerTier;

    /// <summary>12. The last Journeyman level. (Was JourneymanIV in AffinitySystem.)</summary>
    public const int JourneymanIV = 3 * Domain.SubLevelsPerTier;

    /// <summary>16. The last Master level, i.e. the ceiling for anyone not ascending.
    /// (Was MasterIV in AffinitySystem.)</summary>
    public const int MasterIV = 4 * Domain.SubLevelsPerTier;
}

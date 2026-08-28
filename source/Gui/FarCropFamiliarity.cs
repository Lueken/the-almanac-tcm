using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using AlmanacIlluminated;
using AlmanacTcm.Domains;
using AlmanacTcm.Leveling;

namespace AlmanacTcm.Gui;

/// <summary>
/// Hands the Almanac's Crops tab what the reader has actually grown, so the catalogue
/// stops being a list of everything that exists and becomes their own record of it.
///
/// The direction of the dependency matters and is one-way: TCM knows about Illuminated,
/// Illuminated knows nothing about TCM. Everything crop familiarity means, the taxonomy,
/// the thresholds, where a family's grouping needs explaining, is decided here and handed
/// over as plain data. The book decides how any of it is drawn.
///
/// The one rule this must not break, because the whole FAR design hangs on it: familiarity
/// decides what you KNOW, rank decides what your HANDS do. So nothing here consults FAR
/// rank except the rotation memory, which is a sense in the world rather than a fact about
/// a crop, and which was ruled a Journeyman's to have.
/// </summary>
public class FarCropFamiliarity : ICropFamiliaritySource
{
    private readonly ICoreClientAPI capi;

    public FarCropFamiliarity(ICoreClientAPI capi)
    {
        this.capi = capi;
        FarFamiliarity.EnsureLoaded(capi);
    }

    public bool Enabled => FarFamiliarity.EyeEnabled(capi);

    public double Spread => FarFamiliarity.Ladder(capi).Spread;

    public CropStanding? Of(CropEntry entry)
    {
        string? id = IdOf(entry);
        if (id == null) return null;                       // not tracked: the book says so plainly

        // A perennial has no family and never will, so an absent one is expected here rather
        // than a reason to give up. For a sown crop it still is: an id with no family means the
        // taxonomy half-knows the plant, and the book should not pretend otherwise.
        string? family = FarFamiliarity.FamilyOf(id);
        if (family == null && !FarPerennials.IsPerennialId(id)) return null;

        var know = FarFamiliarity.KnowledgeOf(capi, capi.World.Player);
        var ladder = FarFamiliarity.Ladder(capi);
        double effective = FarFamiliarity.EffectiveCount(capi, know, id);

        return new CropStanding
        {
            CropId = id,
            FamilyId = family ?? "",
            OwnCount = FarFamiliarity.OwnCount(know, id),
            EffectiveCount = effective,
            AcquaintedAt = ladder.Acquainted,
            VersedAt = ladder.Versed,
            Tier = effective >= ladder.Versed ? CropTier.Versed
                 : effective >= ladder.Acquainted ? CropTier.Acquainted
                 : CropTier.Stranger,
        };
    }

    /// <summary>
    /// Which counter this entry reads from, dispatched on what the plant actually is.
    ///
    /// A fruit tree is the awkward one and needs the new TypeKey. It is built from a branch
    /// block's TypeProps rather than from a block of its own, so PlantBlock is null for it and
    /// there is nothing else on the entry to address it by. Everything else is keyed off the
    /// block: a sown crop through the taxonomy's prefix map, a vine fruit through its ripe-block
    /// map, a bush through its own type variant.
    ///
    /// Mushrooms fall out here with a null id and keep their plain card, which is correct.
    /// Nothing plants a mushroom, so there is nothing about one to learn by growing it.
    /// </summary>
    private string? IdOf(CropEntry entry)
    {
        if (entry.Kind == CropKind.FruitTree)
        {
            string[] parts = (entry.TypeKey ?? "").Split(':');
            if (parts.Length != 2) return null;
            string id = FarPerennials.TreeId(parts[0], parts[1]);
            return id.Length == 0 ? null : id;
        }

        var block = entry.PlantBlock;
        if (block == null) return null;

        if (entry.Kind == CropKind.BerryBush) return FarPerennials.BushIdOf(block);

        return FarFamiliarity.CropIdOf(capi, block) ?? FarFamiliarity.RipeFruitIdOf(capi, block);
    }

    public IReadOnlyList<CropFamilyStanding> Families
    {
        get
        {
            var know = FarFamiliarity.KnowledgeOf(capi, capi.World.Player);
            int threshold = FarFamiliarity.Ladder(capi).FamilyVersed;

            // The rotation memory is the one place rank enters this surface. Below
            // Journeyman the ground keeps its counsel however much has been grown, so the
            // page must not offer a reading the hands cannot take.
            bool journeyman = capi.World.Player != null
                && FarGrowerEye.FarLevelOf(capi, capi.World.Player) >= Rank.Journeyman;

            var list = new List<CropFamilyStanding>();
            int order = 0;
            foreach (string id in FarFamiliarity.Families)
            {
                int sum = FarFamiliarity.FamilySum(know, id);
                bool versed = sum >= threshold;
                list.Add(new CropFamilyStanding
                {
                    Id = id,
                    Name = Name(id),
                    Note = Note(id),
                    Order = order++,
                    MemberCount = FarFamiliarity.FamilySize(id),
                    Sum = sum,
                    Versed = versed,
                    MemoryAvailable = versed && journeyman,
                });
            }
            return list;
        }
    }

    private static string Name(string familyId)
    {
        string key = "almanactcm:far-family-" + familyId;
        if (Lang.HasTranslation(key)) return Lang.Get(key);
        return familyId.Length == 0 ? familyId : char.ToUpperInvariant(familyId[0]) + familyId.Substring(1);
    }

    /// <summary>
    /// Where this taxonomy is not the botany a player might expect, the page admits it
    /// rather than letting them find out by growing the wrong thing. Only the three ruled
    /// quirks carry a note; every other family speaks for itself.
    /// </summary>
    private static string? Note(string familyId)
    {
        string key = "almanactcm:far-family-note-" + familyId;
        return Lang.HasTranslation(key) ? Lang.Get(key) : null;
    }
}

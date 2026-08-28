using System;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// Familiarity identity for the plants nobody sows: fruit trees and berry bushes.
///
/// COMPUTED, NOT CONFIGURED. The design review specified a second asset file mapping ids to
/// code patterns beside crop-families.json. There is nothing for such a file to do. Every id
/// here falls out of the block code or the tree type that the call site already holds, so a
/// roster would be a hand-maintained copy of what the registry already knows, needing an edit
/// every time the pack changes and silently omitting whatever nobody remembered to add. The
/// computed rule picks up bdorchard's trees and any future mod's bushes for free and cannot
/// drift from what the game actually loaded.
///
/// WHY NOT crop-families.json. Five systems read that file and every one of them takes it as
/// the sown-crop roster: the yield table generates a row per entry, soil sickness resolves a
/// family to accrue against, biofumigation tests for brassicas, the Grower's Eye keys rotation
/// memory on family, and ForPatches decides FOR-versus-FAR routing by PRESENCE in it. Putting a
/// bush in there would redirect domain credit as a side effect of a data edit. Perennials carry
/// no family at all, which is also why they need no file: with no family there is nothing to
/// group, and FarFamiliarity.EffectiveCount already degrades to the own count when FamilyOf
/// returns null.
///
/// The counters live under the existing far-crop- prefix and the existing far-cropday- day cap,
/// so BumpHarvest, OwnCount and the once-a-day rule all work here unmodified. The tree- and
/// bush- id prefixes are what stop a perennial colliding with a sown crop of the same name.
/// </summary>
public static class FarPerennials
{
    public const string TreePrefix = "tree-";
    public const string BushPrefix = "bush-";

    /// <summary>
    /// A fruit tree's id, as tree-{domain}-{type}.
    ///
    /// The domain is the BRANCH BLOCK's, never the fruit's. bdorchard registers its own branch
    /// block with its own type table, and its fig still drops game:fruit-fig, so keying on the
    /// produce would file a modded tree under the base game and collide with any other mod that
    /// adds a fig.
    /// </summary>
    public static string TreeId(string? branchDomain, string? treeType) =>
        string.IsNullOrEmpty(treeType) ? "" : TreePrefix + (branchDomain ?? "game") + "-" + treeType;

    /// <summary>Same id from a live tree part, whose TreeType is the key its own TypeProps is
    /// indexed by.</summary>
    public static string? TreeIdOf(BlockEntityFruitTreePart? part)
    {
        string? type = part?.TreeType;
        if (string.IsNullOrEmpty(type)) return null;
        string id = TreeId(part!.Block?.Code?.Domain, type);
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// A berry bush's id, as bush-{domain}-{type}.
    ///
    /// Vanilla codes the live bush fruitingbush-{state}-{type}-{cover}, so the type variant is
    /// the species and the state (wild or grown) and the snow cover must not enter the id: one
    /// bush learned in winter and in summer is one bush. Where a block carries no type variant
    /// the code's own head stands in, which handles a mod that ships one blocktype per species
    /// rather than one with a variant group, and needs no per-mod special case.
    /// </summary>
    public static string? BushIdOf(Block? block)
    {
        if (block?.Code == null) return null;
        string? type = null;
        if (block.Variant != null && block.Variant.TryGetValue("type", out var v) && !string.IsNullOrEmpty(v))
            type = v;
        type ??= block.Code.FirstCodePart();
        if (string.IsNullOrEmpty(type)) return null;
        return BushPrefix + block.Code.Domain + "-" + type;
    }

    /// <summary>
    /// True when this bush was set by somebody rather than placed by worldgen.
    ///
    /// Vanilla's own discriminator, used the same way in two places it already matters:
    /// OnBlockPlaced gives a wild bush a random health state and leaves it null for one grown
    /// from a cutting, consumeNutrients returns early on the same test, and OnGrownFromCutting
    /// clears it at the moment a cutting becomes a bush. So a null WildBushState IS cultivation,
    /// recorded by the game rather than inferred by us.
    /// </summary>
    public static bool IsCultivated(BEBehaviorFruitingBush? bush) =>
        bush?.BState != null && bush.BState.WildBushState == null;

    /// <summary>True for an id this module minted, which is the test for "has no family and
    /// never will" wherever a caller needs to keep perennials out of a sown-crop path.</summary>
    public static bool IsPerennialId(string? id) =>
        id != null && (id.StartsWith(TreePrefix, StringComparison.Ordinal)
                    || id.StartsWith(BushPrefix, StringComparison.Ordinal));
}

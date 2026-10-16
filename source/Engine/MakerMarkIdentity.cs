using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AlmanacTcm.Engine;

/// <summary>
/// THE IDENTITY LAW, SECOND HALF (2026-10-16). The first half shipped 2026-09-03 for food
/// (<see cref="FoodProvenance"/>) and stopped at three mark families: FAR, COO and BRE. Every
/// other maker mark this mod writes was left outside it, and the same defect class kept erupting
/// in the domains nobody had got to yet.
///
/// WHY ONE LIST FIXES FOUR SYMPTOMS. A mark is an ItemStack attribute, and attributes are part of
/// stack identity at every vanilla gate. All four gates read the SAME list,
/// <c>GlobalConstants.IgnoredStackAttributes</c>:
///
///   stack merging          Collectible.GetMergableQuantity          (Collectible.cs:2558)
///   merchant and JSON      JsonItemStack.Matches                    (JsonItemStack.cs:199)
///   recipe ingredients     RecipeBase                               (RecipeBase.cs:828)
///   story quest turn-in    DialogueComponent.getIgnoreAttrs         (DialogueComponent.cs:197)
///
/// So the reports that read as five unrelated bugs are one:
///
///   LGD-362  a marked bronze pick is refused by the Resonance Archives turn-in, which BLOCKS a
///            vanilla story arc. The dialogue matcher's second arm cannot rescue it either:
///            giveStack.Satisfies(wantStack) is a subset test and a marked pick carries EXTRA
///            attributes, so it fails by construction.
///   LGD-331  pounder caps forged at Journeyman I will not stack with Journeyman II.
///   LGD-299  knitted wool fabric carries the tailor's mark, loom-woven does not, so they refuse.
///   LGD-139  marked ingots will not stack with unmarked ones.
///   LGD-158  player-made clothing cannot be sold to merchants.
///
/// Vanilla's own precedent is in that dialogue list: it appends "durability" so a WORN tool is
/// still a valid turn-in. A maker's name is the same kind of value, and belongs there for the
/// same reason.
///
/// THE LAW SHIPS AS A PAIR, exactly as the food half does. Keys in the ignore list open every
/// gate; the reconcile at vanilla's own merge point keeps an open gate from becoming a laundering
/// route, because a merge keeps the SINK's attributes and would otherwise let plain work inherit a
/// mark by being shift-clicked into a marked stack. If the merge seam is ever gone, neither half
/// activates: marked goods fall back to refusing to mix, which is annoying and safe.
///
/// Deliberately a separate registry from <see cref="FoodProvenance"/> rather than a refactor of
/// it. The food half is in production and its quern law is tuned to food; the two key sets are
/// disjoint, so two Harmony pairs on TryMergeStacks compose without touching each other, and the
/// working half is not put at risk to save an array.
/// </summary>
public static class MakerMarkIdentity
{
    /// <summary>One mark as a set of attributes that travel together, with the value that decides
    /// whether two sides are interchangeable.
    ///
    /// <paramref name="levelAttr"/> null means the mark has no grade and PRESENCE is its level:
    /// marked and marked are the same, marked and unmarked are not. That is what the workpiece
    /// stamp, the Grandmaster signatures and the tool part marks need, and it is the case that
    /// would otherwise launder silently, because an absent level reads equal to an absent level
    /// and the reconcile would skip.</summary>
    public sealed class Mark
    {
        public readonly string Domain;
        public readonly string? LevelAttr;
        public readonly string[] Keys;

        public Mark(string domain, string? levelAttr, params string[] keys)
        {
            Domain = domain; LevelAttr = levelAttr; Keys = keys;
        }
    }

    /// <summary>
    /// Every maker mark that is NOT a food mark. Food lives in FoodProvenance.Carryable and must
    /// not be repeated here; the two registries are disjoint by construction.
    ///
    /// NOT HERE, deliberately:
    ///   almanacHeirloomGen   a generation tail is a strain, not a signature. Refusing to mix
    ///                        protects the rare thing from a careless shift-click. Established
    ///                        2026-09-03 and still the right test to apply to every new mark:
    ///                        does this value say WHO made it, or WHAT it is?
    ///   almanactcm:smelt, smeltlogged, met-first-work, materialgateoverride, soilsick,
    ///                        stumpghost, is-roast-*, is-blast-*
    ///                        bookkeeping, not signatures. Some ride block entities, not stacks.
    /// </summary>
    private static readonly Mark[] All =
    {
        // The finished piece's Maker's Mark. The LEVEL is the reconcile key, not a cosmetic
        // stamp: it is frozen at creation, drives the tiered provenance line, and gates real
        // behaviour (MetSignaturePatches.cs:165 and ToolPartMarks.cs:241 both test it against
        // Grandmaster). It is therefore kept and reconciled, never dropped.
        //
        // sp:smithingQuality is Smithing+'s key, and TCM writes it at MetPatches.cs:358. It has
        // to be ignored too or a forged tool still fails every gate above on that key alone,
        // which would have left LGD-362 broken after fixing everything else.
        new Mark("MET", Domains.MetPatches.MakerLevelAttr,
                 Domains.MetPatches.MakerAttr,
                 Domains.MetPatches.MakerNameAttr,
                 Domains.MetPatches.MakerLevelAttr,
                 "almanactcm:makertier",          // superseded, still read by MarkLevel
                 Domains.MetPatches.SmithingQualityAttr),

        // The workpiece stamp: who is smithing this, carried on the unfinished item. No grade,
        // so presence is the level.
        new Mark("MET-work", null,
                 Domains.MetPatches.SmithAttr,
                 Domains.MetPatches.SmithNameAttr),

        // Honed and Durable are booleans assigned at Grandmaster (MetSignaturePatches.cs:60-61).
        // Presence is the level, and the pair moves together: half a signature is not one.
        new Mark("MET-sig", null,
                 Domains.MetSignature.HonedAttr,
                 Domains.MetSignature.DurableAttr),

        new Mark("TAI", Domains.TaiMark.LevelAttr,
                 Domains.TaiMark.ByAttr,
                 Domains.TaiMark.ByNameAttr,
                 Domains.TaiMark.LevelAttr,
                 Domains.TaiMark.EmphasisAttr),

        new Mark("ALC", Domains.AlcBrand.LevelAttr,
                 Domains.AlcBrand.ByAttr,
                 Domains.AlcBrand.ByNameAttr,
                 Domains.AlcBrand.LevelAttr,
                 Domains.AlcBrand.PotentAttr),

        new Mark("POT", Domains.PotBonusPatches.PotTierAttr,
                 Domains.PotBonusPatches.PotByAttr,
                 Domains.PotBonusPatches.PotByNameAttr,
                 Domains.PotBonusPatches.PotTierAttr),

        new Mark("GLA", Domains.GlaPatches.TolAttr,
                 Domains.GlaPatches.GlaByAttr,
                 Domains.GlaPatches.GlaByNameAttr,
                 Domains.GlaPatches.TolAttr,
                 Domains.GlaPatches.GlaProvAttr),

        // Tool part marks carry no grade: ToolPartMarks.cs:35 records that TierAttr was deleted
        // on 2026-08-12 and nothing writes almanactcm:parttier any more. Presence is therefore
        // the level. The dead key is still LISTED, because a part made before that date still
        // carries it and would otherwise refuse to stack with one made after.
        new Mark("PART", null,
                 Domains.ToolPartMarks.ByAttr,
                 Domains.ToolPartMarks.ByNameAttr,
                 Domains.ToolPartMarks.VerbAttr,
                 "almanactcm:parttier"),
    };

    // MAS (almanactcm:masby) and ENG (almanactcm:engby) are deliberately absent. Both write to a
    // BlockEntity tree, not to an ItemStack: MasPatches.cs:181 stamps BlockEntityMicroBlock's
    // ToTreeAttributes and EngPatches.cs:194 does the same for its own block entity. A value that
    // never rides a stack cannot break stack identity, so listing it here would buy nothing and
    // would imply a coverage this does not have.

    // ------------------------------------------------------------------ mark reading

    /// <summary>This mark's level on a stack, or -1 when the mark is absent.
    ///
    /// A graded mark reads its level attribute. An ungraded one reports 0 when ANY of its keys is
    /// present, so marked-against-unmarked registers as a difference and reconciles. Reading only
    /// the level attribute for an ungraded mark would return -1 on both sides and skip, which is
    /// precisely the laundering hole.</summary>
    private static int LevelOf(Mark mark, ItemStack? stack)
    {
        var attrs = stack?.Attributes;
        if (attrs == null) return -1;

        if (mark.LevelAttr != null)
            return attrs.HasAttribute(mark.LevelAttr) ? attrs.GetInt(mark.LevelAttr, -1) : -1;

        foreach (string key in mark.Keys)
            if (attrs.HasAttribute(key)) return 0;
        return -1;
    }

    private static void StripMark(Mark mark, ItemStack stack)
    {
        foreach (string key in mark.Keys) stack.Attributes.RemoveAttribute(key);
    }

    // ------------------------------------------------------------------ the ignore list

    /// <summary>Put every registered key into the list the four identity gates consult.
    ///
    /// Append, never replace: FoodProvenance has already added its own keys and other mods add
    /// theirs. Skipping keys that are present makes this safe to call twice, which singleplayer
    /// does by running Start once per side in one process.</summary>
    public static void RegisterIgnoredKeys(ICoreAPI api)
    {
        var current = Vintagestory.API.Config.GlobalConstants.IgnoredStackAttributes;
        var missing = new List<string>();
        foreach (Mark mark in All)
            foreach (string key in mark.Keys)
                if (Array.IndexOf(current, key) < 0 && !missing.Contains(key))
                    missing.Add(key);
        if (missing.Count == 0) return;

        var grown = new string[current.Length + missing.Count];
        current.CopyTo(grown, 0);
        missing.CopyTo(grown, current.Length);
        Vintagestory.API.Config.GlobalConstants.IgnoredStackAttributes = grown;
        TcmLog.Info(api, $"maker marks joined IgnoredStackAttributes ({missing.Count} keys, list now {grown.Length}): marked goods stack, sell and turn in like plain ones; TryMergeStacks reconciles");
    }

    // ------------------------------------------------------------------ the merge reconcile

    /// <summary>Each registered mark's level on both sides, captured before vanilla mutates
    /// either stack.</summary>
    public struct MergeState
    {
        public bool Reconcile;
        public int[] SrcLevels;
        public int[] SinkLevels;
    }

    public static void MergeReconcilePrefix(ItemStackMergeOperation op, out MergeState __state)
    {
        __state = default;
        ItemStack? src = op?.SourceSlot?.Itemstack;
        ItemStack? sink = op?.SinkSlot?.Itemstack;
        if (src == null || sink == null) return;

        int[]? srcLevels = null, sinkLevels = null; // allocate only when a mark is present
        for (int i = 0; i < All.Length; i++)
        {
            int s = LevelOf(All[i], src);
            int k = LevelOf(All[i], sink);
            if (s < 0 && k < 0) continue;
            if (srcLevels == null)
            {
                srcLevels = new int[All.Length];
                sinkLevels = new int[All.Length];
                for (int j = 0; j < All.Length; j++) { srcLevels[j] = -1; sinkLevels[j] = -1; }
            }
            srcLevels[i] = s; sinkLevels![i] = k;
        }
        if (srcLevels == null) return; // the common case: nothing marked, vanilla untouched
        __state = new MergeState { Reconcile = true, SrcLevels = srcLevels, SinkLevels = sinkLevels! };
    }

    /// <summary>The quern law, per mark set, same as the food half: the same level on both sides
    /// keeps the mark, anything else strips that whole set from the sink. A mixed batch goes
    /// plain. No merge can launder a mark upward, and no marked good refuses ordinary play.
    ///
    /// This is what makes LGD-331 stack: two batches of pounder caps forged at different
    /// Journeyman levels now MERGE, and the result is honestly unmarked, because a mixed stack has
    /// no single maker level to claim. Two batches at the SAME level keep the mark.
    ///
    /// Tools never reach here in practice. They are MaxStackSize 1, so nothing merges into them,
    /// which is why extending the law to tool marks costs the Maker's Mark nothing.</summary>
    public static void MergeReconcilePostfix(ItemStackMergeOperation op, MergeState __state)
    {
        if (!__state.Reconcile || op == null || op.MovedQuantity <= 0) return;
        ItemStack? sink = op.SinkSlot?.Itemstack;
        if (sink?.Collectible == null) return;

        bool changed = false;
        for (int i = 0; i < All.Length; i++)
        {
            if (__state.SrcLevels[i] == __state.SinkLevels[i]) continue;
            StripMark(All[i], sink);
            changed = true;
            if (op.World?.Side == EnumAppSide.Server)
                TcmLog.Cat(op.World.Api, "marks", $"mixed-batch merge on {sink.Collectible.Code}: {All[i].Domain} mark dropped");
        }
        if (changed) op.SinkSlot!.MarkDirty();
    }

    // ------------------------------------------------------------------ patching

    /// <summary>Registered through Try(...) from the mod system rather than by attribute, per
    /// CONVENTIONS.md section 6: a bad seam must warn and skip, never abort Start.
    ///
    /// Patched on BOTH sides, like the food half and the trough patch, so the client's merge
    /// prediction matches what the server authorizes.</summary>
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var merge = AccessTools.Method(typeof(CollectibleObject),
            nameof(CollectibleObject.TryMergeStacks));
        if (merge == null)
        {
            TcmLog.Warn(api, "maker mark identity: CollectibleObject.TryMergeStacks not found; law inactive, marked goods keep refusing to stack, sell and turn in");
            return;
        }

        harmony.Patch(merge,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MakerMarkIdentity), nameof(MergeReconcilePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MakerMarkIdentity), nameof(MergeReconcilePostfix))));
        RegisterIgnoredKeys(api);
        TcmLog.Info(api, $"maker mark identity hooked ({All.Length} mark families: uniform batch keeps the mark, mixed goes plain)");
    }
}

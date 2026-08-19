using System;
using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacTcm.Domains;

/// <summary>
/// Part provenance for the Toolsmith chain (RULED 2026-07-31): the head already carries
/// MET's maker's mark, and this closes the other two thirds of a finished tool.
///
///   Handle (WOO, "Hafted by")   Binding / grip (TAI, "Bound by" / "Wrapped by")
///
/// Stamp-only, no XP: the assembly grid grants nothing, and marks are provenance, not
/// practice (the ALC-brand / TAI-garment grid precedent). Journeyman and up carries a
/// mark, the house rule. Stamped at grid craft so a part sells marked off a shop shelf,
/// which is the point; the assembled tool then shows the whole lineage because Toolsmith
/// stores the entire part STACKS on the tool (tinkeredToolHead / Handle / Binding, whole
/// clones, verified 1.2.17), so the marks ride through assembly, disassembly and rework
/// with no help from us.
///
/// Classification asks Toolsmith's own part registries (Stats.BaseHandleParts /
/// BindingParts / GripParts) rather than duplicating regexes, so pack-added parts work
/// unseen. Commodity parts are deliberately NOT stamped: a stick or a length of flax
/// twine is also fuel and cordage, and a mark would split every commodity stack it
/// touched. Only dedicated part items (code domain other than "game") take the mark;
/// a tool bound with plain twine simply shows no "Bound by" line, which is honest.
/// </summary>
public static class ToolPartMarks
{
    public const string ByAttr = "almanactcm:partby";
    public const string ByNameAttr = "almanactcm:partbyname";
    // TierAttr ("almanactcm:parttier") deleted 2026-08-12: written on every marked part, read
    // nowhere in the repo. It was also one of only two true-tier stores, doubling the
    // level-vs-tier naming hazard for no benefit.
    public const string VerbAttr = "almanactcm:partverb";   // hafted | bound | wrapped

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("toolsmith"))
        {
            TcmLog.Cat(api, TcmLog.Config, "part marks dormant: toolsmith absent");
            return;
        }

        var created = AccessTools.Method(typeof(CollectibleObject), "OnCreatedByCrafting");
        var info = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo));
        if (created == null || info == null)
        {
            TcmLog.Warn(api, "part-mark seams not found (OnCreatedByCrafting/GetHeldItemInfo); part marks inactive");
            return;
        }

        // STAMP AT CREATION, not at ConsumeInput (0.4.24, the fix for the 0.4.19 no-stamp bug).
        // GenerateOutputStack calls OnCreatedByCrafting on every PREVIEW regeneration, and the
        // grid re-previews while ConsumeInput consumes, so a stamp deferred to ConsumeInput lands
        // on the next preview stack instead of the one the player took. The crafter is reachable
        // right here: the grid's output slot belongs to an InventoryBasePlayer. This is the same
        // moment MET's mark transfer writes, which is the one path verified working in game.
        harmony.Patch(created, postfix: new HarmonyMethod(AccessTools.Method(typeof(ToolPartMarks), nameof(CreatedStampPostfix))));
        // Tooltip patches BOTH sides, like MET's mark line: attributes sync, so the line agrees.
        harmony.Patch(info, postfix: new HarmonyMethod(AccessTools.Method(typeof(ToolPartMarks), nameof(TooltipPostfix))));
        TcmLog.Info(api, "part marks live: handles (WOO) and bindings/grips (TAI) stamp at creation; tools show the lineage");
    }

    // ------------------------------------------------------------ classification

    /// <summary>Toolsmith's Stats object, re-read per call rather than caching the
    /// dictionaries: its loader passes them BY REF and may reassign, so a cached
    /// reference could be the stale pre-load empty.</summary>
    private static object? StatsObject()
    {
        var t = AccessTools.TypeByName("Toolsmith.ToolsmithModSystem");
        if (t == null) return null;
        return AccessTools.Field(t, "Stats")?.GetValue(null)
            ?? AccessTools.Property(t, "Stats")?.GetValue(null);
    }

    private static bool InRegistry(object stats, string dictName, string codePath)
    {
        var member = Member(Traverse.Create(stats), dictName).GetValue();
        return member is System.Collections.IDictionary dict && dict.Contains(codePath);
    }

    private static Traverse Member(Traverse t, string name)
    {
        var p = t.Property(name);
        return p.PropertyExists() ? p : t.Field(name);
    }

    /// <summary>The verb a dedicated part takes, or null for everything else (including
    /// every "game"-domain commodity, by design).</summary>
    private static string? Classify(ItemStack? stack)
    {
        var code = stack?.Collectible?.Code;
        if (code == null || code.Domain == "game") return null;
        var stats = StatsObject();
        if (stats == null) return null;
        if (InRegistry(stats, "BaseHandleParts", code.Path)) return "hafted";
        if (InRegistry(stats, "BindingParts", code.Path)) return "bound";
        if (InRegistry(stats, "GripParts", code.Path)) return "wrapped";
        return null;
    }

    // ------------------------------------------------------------ stamp

    /// <summary>Stamp the part the moment its stack is generated. Fires on every preview
    /// regeneration, which is exactly right: the stack the player takes is always the last
    /// preview, and each preview is a fresh clone that gets its own stamp. Attribute-copying
    /// re-crafts (a treatment on a marked handle) arrive with the original mark already on
    /// the clone, and the stamp-if-absent guard keeps that original hand.</summary>
    public static void CreatedStampPostfix(ItemSlot outputSlot)
    {
        var stack = outputSlot?.Itemstack;
        string? verb = Classify(stack);
        if (verb == null) return;

        // The grid's output slot belongs to the crafter's InventoryBasePlayer.
        var player = (outputSlot!.Inventory as InventoryBasePlayer)?.Player;
        if (player?.Entity?.World?.Side != EnumAppSide.Server) return;

        // A re-craft of an already-marked part keeps the original hand's mark: the work
        // being credited is the making, once.
        if (stack!.Attributes.HasAttribute(ByAttr)) return;

        int level = verb == "hafted" ? WooDomain.LevelOf(player) : TaiDomain.LevelOf(player);
        if (level < Rank.Journeyman) return;   // Journeyman+ only: lesser work carries no mark

        stack.Attributes.SetString(ByAttr, player.PlayerUID);
        stack.Attributes.SetString(ByNameAttr, player.PlayerName);
        // TierAttr write deleted 2026-08-12: almanactcm:parttier was written on every marked
        // part and read by nothing. The lineage read below goes through MetPatches.MarkLevel,
        // which reads a different key entirely. Dead persisted data; not migrated, just stopped.
        stack.Attributes.SetString(VerbAttr, verb);
    }

    // ------------------------------------------------------------ tooltip

    private static string? PartLine(ItemStack? part)
    {
        var attrs = part?.Attributes;
        string? name = attrs?.GetString(ByNameAttr);
        string? verb = attrs?.GetString(VerbAttr);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(verb)) return null;
        return Lang.Get($"almanactcm:part-{verb}-by", name);
    }

    /// <summary>One postfix serves both faces of the feature: a loose part shows its own
    /// mark (the shop shelf), and an assembled tool shows the lineage read from the part
    /// stacks Toolsmith stored inside it. MET's own line stays the "forged by" third; the
    /// forged line is only supplied here when the tool itself carries no MET mark but its
    /// stored head does.</summary>
    public static void TooltipPostfix(ItemSlot inSlot, System.Text.StringBuilder dsc)
    {
        var stack = inSlot?.Itemstack;
        var attrs = stack?.Attributes;
        if (attrs == null) return;

        // The part in hand.
        string? own = PartLine(stack);
        if (own != null) dsc.AppendLine(own);

        // The assembled tool: lineage from the stored parts.
        if (!attrs.HasAttribute("tinkeredToolHandle") && !attrs.HasAttribute("tinkeredToolBinding")) return;

        // Collect the lineage as (verb, name, level) in the order of making, then fold shared
        // hands into one line each (RULED 2026-08-18: "Forged, hafted & bound by X"). A
        // Grandmaster masterwork head keeps its own line; specialness never folds. A hand with
        // a single verb keeps the original tiered wording, so nothing changes for mixed crews.
        var lineage = new List<(string Verb, string Name, int Level)>();

        if (WillFoldToolMark(stack))
        {
            // The tool carries its own MET mark and the same hand made a part: MET's maker
            // line stands down (MarkTooltipPatch checks this same predicate) and the forged
            // credit joins the lineage here, quality clause and all.
            lineage.Add(("forged", attrs.GetString(MetPatches.MakerNameAttr)!, MetPatches.MarkLevel(stack)));
        }
        else if (string.IsNullOrEmpty(attrs.GetString(MetPatches.MakerNameAttr)))
        {
            var head = attrs.GetItemstack("tinkeredToolHead");
            string? smith = head?.Attributes?.GetString(MetPatches.MakerNameAttr);
            if (!string.IsNullOrEmpty(smith))
            {
                // One mapping and one read, both in MetPatches. This was a duplicate of
                // MakerKey with bare literals (2026-08-12); the two could drift independently,
                // and this is the mod's only cross-domain read of another domain's mark.
                int forgedLevel = MetPatches.MarkLevel(head);
                if (forgedLevel >= Rank.Grandmaster)
                    dsc.AppendLine(Lang.Get(MetPatches.MakerKey(forgedLevel), smith));
                else
                    lineage.Add(("forged", smith!, forgedLevel));
            }
        }

        CollectPart(lineage, attrs.GetItemstack("tinkeredToolHandle"));
        CollectPart(lineage, attrs.GetItemstack("tinkeredToolBinding"));

        var hands = new List<string>();
        foreach ((_, string n, _) in lineage) if (!hands.Contains(n)) hands.Add(n);

        foreach (string hand in hands)
        {
            var mine = lineage.FindAll(t => t.Name == hand);
            if (mine.Count == 1)
            {
                (string v, _, int lvl) = mine[0];
                dsc.AppendLine(v == "forged"
                    ? Lang.Get(MetPatches.MakerKey(lvl), hand)
                    : Lang.Get($"almanactcm:part-{v}-by", hand));
            }
            else
            {
                string phrase = Lang.Get($"almanactcm:lineage-{mine[0].Verb}");
                for (int i = 1; i < mine.Count; i++)
                    phrase += (i == mine.Count - 1 ? " & " : ", ")
                        + Lang.Get($"almanactcm:lineage-{mine[i].Verb}").ToLowerInvariant();
                string line = Lang.Get("almanactcm:lineage-by", phrase, hand);
                // A folded forged credit keeps the awake-quality figure the maker line
                // would have carried (the numbers ruling).
                if (mine.Exists(t => t.Verb == "forged" && t.Level > 0))
                    line += MetPatches.QualityClause(stack);
                dsc.AppendLine(line);
            }
        }
    }

    /// <summary>True when the assembled tool's own MET mark should fold into the lineage
    /// line instead of rendering separately (RULED 2026-08-18): the tool carries a maker
    /// below Grandmaster, and that same hand made the handle or the binding. MetPatches'
    /// MarkTooltipPatch consults this same predicate, so the two renderers cannot both
    /// print or both stand down, whatever Harmony's postfix order.</summary>
    internal static bool WillFoldToolMark(ItemStack? stack)
    {
        var attrs = stack?.Attributes;
        if (attrs == null) return false;
        string? maker = attrs.GetString(MetPatches.MakerNameAttr);
        if (string.IsNullOrEmpty(maker)) return false;
        if (MetPatches.MarkLevel(stack) >= Rank.Grandmaster) return false;
        return maker == attrs.GetItemstack("tinkeredToolHandle")?.Attributes?.GetString(ByNameAttr)
            || maker == attrs.GetItemstack("tinkeredToolBinding")?.Attributes?.GetString(ByNameAttr);
    }

    private static void CollectPart(List<(string Verb, string Name, int Level)> lineage, ItemStack? part)
    {
        var attrs = part?.Attributes;
        string? name = attrs?.GetString(ByNameAttr);
        string? verb = attrs?.GetString(VerbAttr);
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(verb)) lineage.Add((verb!, name!, 0));
    }
}

using System;
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
    public const string TierAttr = "almanactcm:parttier";
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
        int tier = Leveling.Domain.TierOf(level);
        if (tier < 2) return;   // Journeyman+ only: lesser work carries no mark

        stack.Attributes.SetString(ByAttr, player.PlayerUID);
        stack.Attributes.SetString(ByNameAttr, player.PlayerName);
        stack.Attributes.SetInt(TierAttr, tier);
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

        if (string.IsNullOrEmpty(attrs.GetString(MetPatches.MakerNameAttr)))
        {
            var head = attrs.GetItemstack("tinkeredToolHead");
            string? smith = head?.Attributes?.GetString(MetPatches.MakerNameAttr);
            if (!string.IsNullOrEmpty(smith))
            {
                int tier = head!.Attributes.GetInt(MetPatches.MakerTierAttr, -1);
                string key = tier >= 4 ? "almanactcm:masterwork-by"
                    : tier == 3 ? "almanactcm:master-forged-by"
                    : "almanactcm:smithed-by";
                dsc.AppendLine(Lang.Get(key, smith));
            }
        }

        string? haft = PartLine(attrs.GetItemstack("tinkeredToolHandle"));
        if (haft != null) dsc.AppendLine(haft);
        string? bind = PartLine(attrs.GetItemstack("tinkeredToolBinding"));
        if (bind != null) dsc.AppendLine(bind);
    }
}

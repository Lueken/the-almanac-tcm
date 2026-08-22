using System;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// Workstream D, the part-quality levers (routed from the Charter review Part 7n, built
/// 2026-08-22): the WOO handle and the TAI/HUN binding carry their maker's quality the way MET's
/// head already does. Same banded factor as MET's Maker's Mark (+5% Journeyman, +10% Master,
/// +15% Grandmaster, flat inside each band; lesser work stays unmarked), applied to Toolsmith's
/// own per-part durability numbers so nothing double-counts and no parallel wear system exists.
///
/// Three seams, all Toolsmith-conditional (the ruled silencing posture):
/// - Handle stamp: CollectibleBehaviorToolHandle.OnCreatedByCrafting postfix. Crafting a handle
///   part stamps the crafter's WOO quality onto the PART. A plain stick that was never crafted
///   stays unmarked, which is the point.
/// - Binding stamp: the binding behavior does not override the craft hook, so the stamp rides a
///   postfix on the vanilla CollectibleBehavior base, filtered to the binding behavior type.
///   The domain splits by material: leather, hide, sinew and gut are HUN's work (tanning is
///   HUN's verb); every fibre and cloth binding is TAI's.
/// - Assembly: CollectibleBehaviorTinkeredTools.OnCreatedByCrafting postfix multiplies the
///   handle and binding durability Toolsmith just computed by each part's stamped quality.
///   Toolsmith's copy path (an existing tool among the inputs) early-returns with values that
///   were already scaled at first assembly, so that path is skipped by the same-code guard and
///   nothing ever compounds. Reassembly from parts recomputes from scratch and rescales once.
///
/// This is what gives Charter's Company a product: two domain passes, not a new system.
/// </summary>
public static class PartQualityLevers
{
    // Part attributes (stamped at part creation, read at assembly).
    public const string WooQualityAttr = "almanacWooQuality";
    public const string WooByNameAttr = "almanacWooByName";
    public const string WooLevelAttr = "almanacWooLevel";
    public const string BindQualityAttr = "almanacBindQuality";
    public const string BindByNameAttr = "almanacBindByName";
    public const string BindLevelAttr = "almanacBindLevel";
    public const string BindDomainAttr = "almanacBindDomain";

    // Tool attributes (carried onto the assembled tool for future provenance lines).
    public const string HaftByNameAttr = "almanacHaftByName";
    public const string BoundByNameAttr = "almanacBoundByName";

    // Toolsmith's own per-part durability attributes (ToolsmithAttributes, 1.2.18).
    private const string HandleMaxAttr = "tinkeredToolHandleMaxDurability";
    private const string HandleCurAttr = "tinkeredToolHandleDurability";
    private const string BindingMaxAttr = "tinkeredToolBindingMaxDurability";
    private const string BindingCurAttr = "tinkeredToolBindingDurability";

    private static Type? bindingBehaviorType;

    /// <summary>MET's banded maker-quality factor, verbatim: flat inside each band, 1.0 below
    /// Journeyman so a mark always means something.</summary>
    private static double QualityFor(int level) => level switch
    {
        >= Rank.Grandmaster => 1.15,
        >= Rank.Master => 1.10,
        >= Rank.Journeyman => 1.05,
        _ => 1.0,
    };

    private static IPlayer? CrafterOf(ItemSlot[]? slots)
    {
        if (slots == null) return null;
        foreach (var slot in slots)
            if (slot?.Inventory is InventoryBasePlayer inv && inv.Player != null) return inv.Player;
        return null;
    }

    private static int LevelOf(IPlayer player, string domainCode) =>
        AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(domainCode)?.Level ?? 0;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("toolsmith")) return;

        var handleType = AccessTools.TypeByName("Toolsmith.ToolTinkering.Behaviors.CollectibleBehaviorToolHandle");
        bindingBehaviorType = AccessTools.TypeByName("Toolsmith.ToolTinkering.Behaviors.CollectibleBehaviorToolBinding");
        var toolsType = AccessTools.TypeByName("Toolsmith.ToolTinkering.Behaviors.CollectibleBehaviorTinkeredTools");

        int hooked = 0;
        var mh = handleType == null ? null : AccessTools.DeclaredMethod(handleType, "OnCreatedByCrafting");
        if (mh != null)
        {
            harmony.Patch(mh, postfix: new HarmonyMethod(AccessTools.Method(typeof(PartQualityLevers), nameof(HandleCraftPostfix))));
            hooked++;
        }
        else TcmLog.Warn(api, "toolsmith handle craft seam not found; the WOO haft lever is inactive");

        var mb = bindingBehaviorType == null ? null
            : AccessTools.DeclaredMethod(typeof(CollectibleBehavior), nameof(CollectibleBehavior.OnCreatedByCrafting));
        if (mb != null)
        {
            harmony.Patch(mb, postfix: new HarmonyMethod(AccessTools.Method(typeof(PartQualityLevers), nameof(BindingCraftPostfix))));
            hooked++;
        }
        else TcmLog.Warn(api, "toolsmith binding behavior not found; the TAI/HUN binding lever is inactive");

        var mt = toolsType == null ? null : AccessTools.DeclaredMethod(toolsType, "OnCreatedByCrafting");
        if (mt != null)
        {
            harmony.Patch(mt, postfix: new HarmonyMethod(AccessTools.Method(typeof(PartQualityLevers), nameof(AssemblyPostfix))));
            hooked++;
        }
        else TcmLog.Warn(api, "toolsmith assembly seam not found; part quality stamps but never applies");

        if (hooked > 0)
            TcmLog.Info(api, $"part-quality levers live ({hooked}/3 seams): WOO hafts and TAI/HUN bindings carry their maker's quality");
    }

    /// <summary>Crafting a handle part stamps the crafter's WOO quality onto the part.</summary>
    public static void HandleCraftPostfix(ItemSlot[] allInputslots, ItemSlot outputSlot)
    {
        var player = CrafterOf(allInputslots);
        var stack = outputSlot?.Itemstack;
        if (player == null || stack == null || player.Entity?.Api?.Side != EnumAppSide.Server) return;

        int level = LevelOf(player, WooDomain.Code);
        if (level < Rank.Journeyman) return;

        stack.Attributes.SetFloat(WooQualityAttr, (float)QualityFor(level));
        stack.Attributes.SetString(WooByNameAttr, player.PlayerName);
        stack.Attributes.SetInt(WooLevelAttr, level);
    }

    /// <summary>Crafting a binding part stamps TAI or HUN quality by material: leather is the
    /// hunter's chain, fibre is the tailor's. Rides the behavior BASE hook (the binding behavior
    /// does not override it), so the type filter exits first.</summary>
    public static void BindingCraftPostfix(CollectibleBehavior __instance, ItemSlot[] allInputslots, ItemSlot outputSlot)
    {
        if (bindingBehaviorType == null || __instance.GetType() != bindingBehaviorType) return;
        var player = CrafterOf(allInputslots);
        var stack = outputSlot?.Itemstack;
        if (player == null || stack == null || player.Entity?.Api?.Side != EnumAppSide.Server) return;

        string path = stack.Collectible?.Code?.Path ?? "";
        bool hun = path.Contains("leather") || path.Contains("hide") || path.Contains("sinew") || path.Contains("gut");
        string domain = hun ? HunDomain.Code : TaiDomain.Code;

        int level = LevelOf(player, domain);
        if (level < Rank.Journeyman) return;

        stack.Attributes.SetFloat(BindQualityAttr, (float)QualityFor(level));
        stack.Attributes.SetString(BindByNameAttr, player.PlayerName);
        stack.Attributes.SetInt(BindLevelAttr, level);
        stack.Attributes.SetString(BindDomainAttr, domain);
    }

    /// <summary>Assembly applies the stamped part qualities to the durability Toolsmith just
    /// computed. The same-code guard skips Toolsmith's copy path (values there were scaled at
    /// their first assembly and are carried, not recomputed), so nothing compounds.</summary>
    public static void AssemblyPostfix(ItemSlot[] allInputslots, ItemSlot outputSlot)
    {
        var tool = outputSlot?.Itemstack;
        if (tool?.Collectible?.Code == null || allInputslots == null) return;

        ItemStack? handlePart = null, bindingPart = null;
        foreach (var slot in allInputslots)
        {
            var s = slot?.Itemstack;
            if (s?.Collectible?.Code == null) continue;
            if (s.Collectible.Code.Equals(tool.Collectible.Code)) return; // copy path: already scaled
            if (s.Attributes.HasAttribute(WooQualityAttr)) handlePart = s;
            if (s.Attributes.HasAttribute(BindQualityAttr)) bindingPart = s;
        }

        if (handlePart != null && tool.Attributes.HasAttribute(HandleMaxAttr))
        {
            double q = handlePart.Attributes.GetFloat(WooQualityAttr, 1f);
            if (q > 1.0)
            {
                Scale(tool, HandleMaxAttr, q);
                Scale(tool, HandleCurAttr, q);
                tool.Attributes.SetString(HaftByNameAttr, handlePart.Attributes.GetString(WooByNameAttr) ?? "");
            }
        }
        if (bindingPart != null && tool.Attributes.HasAttribute(BindingMaxAttr))
        {
            double q = bindingPart.Attributes.GetFloat(BindQualityAttr, 1f);
            if (q > 1.0)
            {
                Scale(tool, BindingMaxAttr, q);
                Scale(tool, BindingCurAttr, q);
                tool.Attributes.SetString(BoundByNameAttr, bindingPart.Attributes.GetString(BindByNameAttr) ?? "");
            }
        }
    }

    private static void Scale(ItemStack stack, string attr, double factor)
    {
        int value = stack.Attributes.GetInt(attr, 0);
        if (value > 0) stack.Attributes.SetInt(attr, (int)Math.Round(value * factor));
    }
}

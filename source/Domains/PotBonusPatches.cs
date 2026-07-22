using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// POT Phase 3 — the Potter's Mark (Axis 6, RULED 2026-07-09; pot-vessel-study adopted 7/7,
/// seams verified against 1.22.3). The container-side mirror of COO's Cook's Mark: a
/// per-instance preservation quality stamped on a fired keep-vessel by the firer's rank, read
/// off the vessel's own perish factor. One potterBy stamp, two jobs:
///   • the preservation ladder — Untrained crocks seal imperfectly (x1.10, POT's penalty band),
///     a masterwork crock keeps food (x0.85); and
///   • tiered provenance in the tooltip (Thrown by / Master-potted by / Masterwork), Journeyman up.
///
/// The perish read rides the vessel's own container modifier, which the crock OVERRIDES without
/// calling base — so the read is patched on BOTH the base BlockContainer virtual (storage vessel,
/// amphora, any inheritor) AND the BlockCrock overrides (the primary carrier), with no
/// double-apply. The two factors (this vessel factor and any COO food-side stamp) compose
/// multiplicatively in the same chain and never collide.
///
/// The lifecycle re-carry (miss one hop and the mark dies there): stamped at OnFired (PotPatches,
/// owner-at-ignite) -> placed vessel writes the mark to a persisted pos map (the BE does not
/// serialize custom attrs) -> the placed read consults that map -> the carried read consults the
/// stack attr -> pickup restores the stack attr from the map. Crock carriage is wired fully;
/// the generic storage vessel is hooked opportunistically (warns-and-skips if it does not declare
/// the hop). The carried-vessel edge works for every stamped vessel with no carriage at all.
/// </summary>
public static class PotBonusPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    public const string PotByAttr = "almanactcm:potby";
    public const string PotByNameAttr = "almanactcm:potbyname";
    public const string PotTierAttr = "almanactcm:pottier";

    /// <summary>Vessel pos -> the potter's mark, packed "uid|name|tier". Persisted: a placed crock
    /// sits through restarts, and the placed-read needs the mark the BE cannot itself carry.</summary>
    private static Dictionary<string, string> vesselMarks = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacPotVesselMarks");
                if (data != null)
                    vesselMarks = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
                TcmLog.Cat(api, TcmLog.Config, $"POT vessel marks loaded: {vesselMarks.Count} placed vessel(s)");
            }
            catch (Exception e) { TcmLog.Error(api, $"POT vessel-mark map unreadable ({e.Message}); starting empty"); }
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacPotVesselMarks",
                Vintagestory.API.Util.SerializerUtil.Serialize(vesselMarks));
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // The perish read — base virtual (storage vessel / amphora / any inheritor) AND the crock
        // overrides (the primary carrier, which does not call base). Placed and carried each.
        HookRead(api, harmony, "Vintagestory.GameContent.BlockContainer", "GetContainingTransitionModifierPlaced",
            nameof(PlacedReadPostfix), "POT preservation read (base, placed)");
        HookRead(api, harmony, "Vintagestory.GameContent.BlockContainer", "GetContainingTransitionModifierContained",
            nameof(ContainedReadPostfix), "POT preservation read (base, carried)");
        HookRead(api, harmony, "Vintagestory.GameContent.BlockCrock", "GetContainingTransitionModifierPlaced",
            nameof(PlacedReadPostfix), "POT preservation read (crock, placed)");
        HookRead(api, harmony, "Vintagestory.GameContent.BlockCrock", "GetContainingTransitionModifierContained",
            nameof(ContainedReadPostfix), "POT preservation read (crock, carried)");

        // Placed/pickup carriage — the crock (primary) fully; the generic storage vessel
        // opportunistically. The carried read needs no carriage (the stamp rides the stack).
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityCrock", "OnBlockPlaced",
            nameof(VesselPlacedPostfix), "POT mark carriage (crock placed)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockCrock", "OnPickBlock",
            nameof(VesselPickPostfix), "POT mark carriage (crock pickup)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityGenericTypedContainer", "OnBlockPlaced",
            nameof(VesselPlacedPostfix), "POT mark carriage (storage vessel placed)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockGenericTypedContainer", "OnPickBlock",
            nameof(VesselPickPostfix), "POT mark carriage (storage vessel pickup)");

        // The provenance tooltip is an attribute patch below (applied by the Start PatchAll pass).
    }

    private static void HookRead(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); inactive"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotBonusPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    private static void HookCarry(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that carriage link is inactive"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotBonusPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    // ------------------------------------------------------------ the stamp

    /// <summary>Stamp a freshly fired ware with its firer's mark (PotPatches.FiredPostfix, called
    /// per ware slot). Harmless on non-keep-vessels: bricks stack-merge and lose it, bowls carry it
    /// cosmetically; only the crock/storage-vessel/amphora keep-line reads it for preservation.</summary>
    public static void StampFired(ItemStack? stack, string uid, string name, int tier)
    {
        if (stack == null) return;
        stack.Attributes.SetString(PotByAttr, uid);
        stack.Attributes.SetString(PotByNameAttr, name);
        stack.Attributes.SetInt(PotTierAttr, tier);
    }

    private static void ApplyPacked(ItemStack? stack, string packed)
    {
        if (stack == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 3) return;
        stack.Attributes.SetString(PotByAttr, p[0]);
        stack.Attributes.SetString(PotByNameAttr, p[1]);
        if (int.TryParse(p[2], out int tier)) stack.Attributes.SetInt(PotTierAttr, tier);
    }

    // ------------------------------------------------------------ placed/pickup carriage

    /// <summary>A marked vessel placed: remember its mark by position (the BE cannot carry the
    /// custom attr through save/load). An UNMARKED vessel clears any stale entry.</summary>
    public static void VesselPlacedPostfix(BlockEntity __instance, ItemStack? byItemStack)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        string key = PosKey(__instance.Pos);
        var attrs = byItemStack?.Attributes;
        if (attrs?.HasAttribute(PotTierAttr) == true)
        {
            vesselMarks[key] = $"{attrs.GetString(PotByAttr)}|{attrs.GetString(PotByNameAttr)}|{attrs.GetInt(PotTierAttr)}";
            TcmLog.Cat(__instance.Api, "pot", $"vessel placed at {__instance.Pos} carries the mark of {attrs.GetString(PotByNameAttr)}; stored");
        }
        else vesselMarks.Remove(key);
    }

    /// <summary>Pickup rebuilds the vessel stack from BE data (custom attrs lost); restore the mark
    /// from the position store. The entry stays (pickup also fires for previews/drops).</summary>
    public static void VesselPickPostfix(IWorldAccessor world, BlockPos pos, ItemStack __result)
    {
        if (world?.Side != EnumAppSide.Server || pos == null) return;
        if (vesselMarks.TryGetValue(PosKey(pos), out string? packed) && packed != null)
            ApplyPacked(__result, packed);
    }

    // ------------------------------------------------------------ the preservation read

    /// <summary>Placed vessel: multiply its perish factor by the potter's preservation quality
    /// (x1.10 Untrained penalty ... x0.85 GM). Reads the mark from the position store.</summary>
    public static void PlacedReadPostfix(BlockPos pos, EnumTransitionType transType, ref float __result)
    {
        if (transType != EnumTransitionType.Perish || pos == null) return;
        if (!vesselMarks.TryGetValue(PosKey(pos), out string? packed) || packed == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int tier)) return;
        __result *= (float)PotDomain.PreserveFactor(tier);
    }

    /// <summary>Carried vessel: the same edge, read from the stack's own stamp attribute (no
    /// carriage needed — the fired stamp rides the stack).</summary>
    public static void ContainedReadPostfix(ItemSlot inSlot, EnumTransitionType transType, ref float __result)
    {
        if (transType != EnumTransitionType.Perish) return;
        var attrs = inSlot?.Itemstack?.Attributes;
        if (attrs?.HasAttribute(PotTierAttr) != true) return;
        __result *= (float)PotDomain.PreserveFactor(attrs.GetInt(PotTierAttr));
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Potter's Mark line (from Journeyman up, ruled): Thrown by / Master-potted by /
    /// Masterwork. Last priority so it sits at the very bottom of the tooltip, after a blank line —
    /// same placement as the Cook's Mark. Non-stacking vessels carry it durably; bricks and
    /// stackable smallware merge and never show it.</summary>
    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.GetDescription))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    public static class ProvenancePatch
    {
        public static void Postfix(ItemStack __instance, ref string __result)
        {
            var attrs = __instance?.Attributes;
            string? name = attrs?.GetString(PotByNameAttr);
            if (string.IsNullOrEmpty(name) || __result == null) return;
            int tier = attrs!.GetInt(PotTierAttr);
            string? line =
                tier >= PotDomain.ProvGm ? Lang.Get("almanactcm:masterwork-by", name)
                : tier >= PotDomain.ProvMaster ? Lang.Get("almanactcm:masterpotted-by", name)
                : tier >= PotDomain.ProvJourneyman ? Lang.Get("almanactcm:thrown-by", name)
                : null;
            if (line != null) __result = __result.TrimEnd() + "\n\n" + line + "\n";
        }
    }
}

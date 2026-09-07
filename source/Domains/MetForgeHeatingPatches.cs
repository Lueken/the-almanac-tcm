using System;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// COMPAT (verb restoration) + one deliberate balance rule. Partner to
/// met-native-metal-forge-heating.json, which gives metal bits and native-metal nuggets the
/// forgable attribute Smithing Plus's own patches never grant (its MakeForgeable only fires
/// for cast tool heads). The asset patch alone would let a heated nugget START a work item on
/// an empty anvil, recipe dialog and all, because the server runs SmithWithBits + BitsTopUp
/// and WorkableNugget resolves that to AnvilPlacementMode.Normal.
///
/// RULED 2026-09-07: a heated nugget is raw material, not smith's stock. It may be added to an
/// in-progress forgable piece and must never prompt what to make. Bits are the smith's own
/// material and keep Smithing Plus's full verbs, including starting nails and arrowheads from
/// scrap. So this postfixes WorkableNugget.PlacementMode to Present for collectibles whose
/// code path is a nugget, and touches nothing else.
///
/// One seam, resolved by name, warn-and-skip. The Present value is resolved by enum NAME so a
/// Smithing Plus reorder cannot silently turn this into a different mode.
/// </summary>
public static class MetForgeHeatingPatches
{
    private static object? _presentBoxed;

    public static class NuggetTopUpOnlyPatch
    {
        public static void Postfix(CollectibleBehavior __instance, ref object __result)
        {
            var code = __instance?.collObj?.Code;
            if (_presentBoxed != null && code != null && code.Path.StartsWith("nugget"))
            {
                __result = _presentBoxed;
            }
        }
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("smithingplus")) return;

        var behaviorType = AccessTools.TypeByName("SmithingPlus.SmithWithBits.CollectibleBehaviorWorkableNugget");
        var modeType = AccessTools.TypeByName("SmithingPlus.Common.AnvilPlacementMode");
        var getter = behaviorType == null ? null : AccessTools.PropertyGetter(behaviorType, "PlacementMode");
        if (getter == null || modeType == null)
        {
            TcmLog.Warn(api, "MET forge-heating seam not found (WorkableNugget.PlacementMode); heated nuggets keep Smithing Plus's default anvil placement this build");
            return;
        }
        try
        {
            _presentBoxed = Enum.Parse(modeType, "Present");
        }
        catch (Exception)
        {
            TcmLog.Warn(api, "MET forge-heating: AnvilPlacementMode.Present absent from this Smithing Plus build; nugget top-up-only rule inactive");
            return;
        }
        harmony.Patch(getter,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(NuggetTopUpOnlyPatch), nameof(NuggetTopUpOnlyPatch.Postfix))));
        TcmLog.Info(api, "MET forge heating live: bits + native nuggets warm on the forge; heated nuggets top up an in-progress piece only (no recipe prompt), bits keep the full Smithing Plus verbs");
    }
}

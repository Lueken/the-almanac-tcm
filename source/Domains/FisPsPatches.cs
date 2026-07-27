using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace AlmanacTcm.Domains;

/// <summary>
/// FIS spearing — the primitivesurvival fishing spear (technique-maps §FIS #3). PS-conditional.
///
/// The spear is a two-step verb (verified in the PS decompile): the thrust despawns the fish and
/// stores its code on the spear stack (`Attributes["fish"]`, didattack-gated to one fish per
/// thrust); the retrieve (`OnHeldInteractStop`, secondsUsed >= 0.35) converts the stored code to a
/// fishraw item and resets the attribute to "none". Credit lands at the RETRIEVE: the stored code
/// going set -> "none" on the server is the one unambiguous completion signal, and it is the
/// moment the player actually has the fish. It never touches Entity.Die, so RAN never sees it
/// (the ruled FIS-not-RAN boundary).
///
/// Phase 1b note: PS's per-chunk FishDepletedPercent is NOT yet rank-scaled for the spear or the
/// traps; that lands with the trap verb + the steward work.
/// </summary>
public static class FisPsPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("primitivesurvival")) return;

        var spearType = AccessTools.TypeByName("PrimitiveSurvival.ModSystem.ItemFishingSpear");
        var stop = spearType == null ? null : AccessTools.Method(spearType, "OnHeldInteractStop");
        if (stop == null)
        {
            TcmLog.Warn(api, "primitivesurvival present but ItemFishingSpear.OnHeldInteractStop not found; FIS spearing inactive");
            return;
        }

        harmony.Patch(stop,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SpearRetrievePatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(SpearRetrievePatch), "Postfix")));
        TcmLog.Info(api, "FIS spearing hooked to primitivesurvival (credit at the spear retrieve)");
    }

    public static class SpearRetrievePatch
    {
        public static void Prefix(ItemSlot slot, out string __state)
        {
            __state = slot?.Itemstack?.Attributes?.GetString("fish", "none") ?? "none";
        }

        public static void Postfix(ItemSlot slot, EntityAgent byEntity, string __state)
        {
            if (byEntity?.World?.Side != EnumAppSide.Server || __state == "none") return;
            string now = slot?.Itemstack?.Attributes?.GetString("fish", "none") ?? "none";
            if (now != "none") return; // retrieve did not complete (too quick, or no conversion)

            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return;

            Core?.Ledger?.Log(player, FisDomain.Code, FisDomain.TechSpearing,
                HashCode.Combine(__state, byEntity.World.ElapsedMilliseconds / 1000));
        }
    }
}

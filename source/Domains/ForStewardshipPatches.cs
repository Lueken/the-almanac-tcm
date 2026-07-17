using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// FOR Patch Stewardship — the ACTIVE tending verb (ruled 2026-07-16). The verb is the vanilla
/// watering can: pour water on one of YOUR worked patches (the Forager's Memory already knows
/// which ground is yours) and its regrow clock advances, scaled by rank, once per patch per
/// in-game day. The seam is BlockWateringCan.OnHeldInteractStep, which vanilla runs server-side
/// for ANY watered block (it is how farmland, fires, and cave art react to water), so no custom
/// interaction plumbing is needed: watering near a recorded patch IS the tend.
///
/// The tend fires once per pour, after ~1.5s of sustained watering — long enough to be a
/// deliberate act, short enough that a tending circuit stays pleasant.
/// </summary>
public static class ForStewardshipPatches
{
    [HarmonyPatch(typeof(BlockWateringCan), nameof(BlockWateringCan.OnHeldInteractStep))]
    public static class WateringTendPatch
    {
        // Per-player last-seen secondsUsed, to detect the 1.5s threshold crossing exactly once
        // per pour. secondsUsed restarting from ~0 = a new pour began.
        private static readonly Dictionary<string, float> lastSeconds = new();
        private const float TendAfterSeconds = 1.5f;

        public static void Postfix(bool __result, float secondsUsed, ItemSlot slot,
            EntityAgent byEntity, BlockSelection blockSel)
        {
            if (!__result || blockSel == null || byEntity?.World?.Side != EnumAppSide.Server) return;
            if (slot?.Itemstack == null || slot.Itemstack.TempAttributes.GetInt("refilled") > 0) return;
            if (byEntity is not EntityPlayer eplayer) return;
            IPlayer? player = byEntity.World.PlayerByUid(eplayer.PlayerUID);
            if (player == null) return;

            lastSeconds.TryGetValue(player.PlayerUID, out float prev);
            if (secondsUsed < prev) prev = 0; // new pour
            lastSeconds[player.PlayerUID] = secondsUsed;

            if (prev < TendAfterSeconds && secondsUsed >= TendAfterSeconds)
            {
                Overlay.AlmanacSpotsLayer.Instance?.TendAt(player, blockSel.Position);
            }
        }
    }
}

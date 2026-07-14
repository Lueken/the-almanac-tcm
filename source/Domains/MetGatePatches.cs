using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// MET material-gate seams (rank-bonus-design.md §162 Axis 5): block SMELTING and
/// FORGING a metal above the player's MET tier. Casting is gated inside MetPatches'
/// existing pour patch (one seam, one patch); assembly (hafting a head) is never gated.
/// Every hook no-ops client-side, when the gate is disabled, and for stacks that carry
/// no resolvable metal (fuel, hammers, non-metal) — see <see cref="MetMaterialGate.Blocks"/>.
/// industrialstory furnaces and the firepit small-smelt land in a later increment.
/// </summary>
public static class MetGatePatches
{
    /// <summary>Bloomery charge: the ore is taken off the active hotbar slot (decompiled
    /// TryAdd). Block adding a gated ore; charcoal and other stacks resolve to no metal
    /// and pass through untouched.</summary>
    [HarmonyPatch(typeof(BlockEntityBloomery), nameof(BlockEntityBloomery.TryAdd))]
    public static class BloomeryGatePatch
    {
        public static bool Prefix(BlockEntityBloomery __instance, IPlayer byPlayer, ref bool __result)
        {
            if (__instance.Api?.Side != EnumAppSide.Server) return true;
            ItemStack? ore = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (MetMaterialGate.Blocks(__instance.Api, byPlayer, ore))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>Anvil forge: block the interaction when a gated metal is in play — the
    /// held hot ingot/nugget being placed (the point a work item is born), or a gated
    /// work item already on the anvil (defense). A hammer or non-metal resolves to no
    /// metal, so striking legitimate work is untouched.</summary>
    [HarmonyPatch(typeof(BlockEntityAnvil), "OnPlayerInteract")]
    public static class AnvilGatePatch
    {
        public static bool Prefix(BlockEntityAnvil __instance, IPlayer byPlayer, ref bool __result)
        {
            if (__instance.Api?.Side != EnumAppSide.Server) return true;
            ItemStack? metalStack = __instance.WorkItemStack
                ?? byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (MetMaterialGate.Blocks(__instance.Api, byPlayer, metalStack))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}

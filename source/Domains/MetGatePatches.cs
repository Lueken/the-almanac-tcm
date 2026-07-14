using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
            ItemStack? ore = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (MetMaterialGate.Blocks(__instance.Api, byPlayer, ore))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>Forge heating: shift-click adds the held item to heat it. Block adding a
    /// gated ingot — the earliest real "work the metal" step (you can't even get it hot),
    /// which makes the anvil question moot. Only the add path (shift) is gated; taking a
    /// heated item out, and adding fuel (coal resolves to no metal), pass through.</summary>
    [HarmonyPatch(typeof(BlockEntityForge), nameof(BlockEntityForge.OnPlayerInteract))]
    public static class ForgeGatePatch
    {
        public static bool Prefix(BlockEntityForge __instance, IPlayer byPlayer, ref bool __result)
        {
            if ((byPlayer?.Entity as EntityAgent)?.Controls?.ShiftKey != true) return true;
            ItemStack? held = byPlayer!.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (MetMaterialGate.Blocks(__instance.Api, byPlayer, held))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>Anvil forge: block the interaction when a gated metal is in play — a gated
    /// hot ingot being placed, or a gated work item already on the anvil (defense). In
    /// practice the forge gate stops a gated ingot from ever heating, so this rarely
    /// fires; a hammer or non-metal resolves to no metal, so legitimate work is untouched.</summary>
    [HarmonyPatch(typeof(BlockEntityAnvil), "OnPlayerInteract")]
    public static class AnvilGatePatch
    {
        public static bool Prefix(BlockEntityAnvil __instance, IPlayer byPlayer, ref bool __result)
        {
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

    // ---- Conditional: industrialstory smelting / ore processing (graceful-degrade) ----

    /// <summary>Wire the gate into industrialstory's furnaces and ore crushing when that
    /// mod is present. Roasting is skipped on purpose (its roastable ores, sphalerite and
    /// galena, smelt to ungated tier-I zinc/lead); the taps are covered by gating the
    /// charge. Every bind degrades to nothing if the mod renames a target.</summary>
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("industrialstory")) return;

        var crush = AccessTools.Method(
            AccessTools.TypeByName("IndustrialStory.BehaviorAdvancedGroundProcessable"), "OnContainedInteractStart");
        if (crush != null)
            harmony.Patch(crush, prefix: new HarmonyMethod(AccessTools.Method(typeof(MetGatePatches), nameof(CrushGatePrefix))));
        else
            TcmLog.Warn(api, "industrialstory present but AdvancedGroundProcessable.OnContainedInteractStart not found; ore-crush gate inactive");

        var addPrefix = new HarmonyMethod(AccessTools.Method(typeof(MetGatePatches), nameof(SmelterAddGatePrefix)));
        int hooked = 0;
        foreach (string type in new[] { "IndustrialStory.BlockEntitySmallSmelter", "IndustrialStory.BlockEntityRetortSmelter" })
        {
            var m = AccessTools.Method(AccessTools.TypeByName(type), "TryAdd");
            if (m != null) { harmony.Patch(m, prefix: addPrefix); hooked++; }
        }
        TcmLog.Info(api, $"MET material gate hooked to industrialstory (crush {(crush != null ? "on" : "off")}, {hooked} furnace charges)");
    }

    /// <summary>Ore crush start: block grinding an ore whose metal is gated (the slot
    /// carries the ore; its SmeltedStack resolves the metal). The acting entity supplies
    /// the side-correct api so this gates on the client too.</summary>
    public static bool CrushGatePrefix(ItemSlot slot, IPlayer byPlayer, ref bool __result)
    {
        if (MetMaterialGate.Blocks(byPlayer?.Entity?.Api, byPlayer, slot?.Itemstack))
        {
            __result = false;
            return false;
        }
        return true;
    }

    /// <summary>Furnace charge (small smelter / retort): block adding a gated ore, taken
    /// off the active hotbar exactly as the smelter's own TryAdd reads it.</summary>
    public static bool SmelterAddGatePrefix(BlockEntity __instance, IPlayer byPlayer, ref bool __result)
    {
        ItemStack? ore = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        if (MetMaterialGate.Blocks(__instance?.Api, byPlayer, ore))
        {
            __result = false;
            return false;
        }
        return true;
    }
}

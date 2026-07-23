using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// TAI — the READ side of the Tailor's Mark (rank-bonus-design.md §TAI Axis 6). The mark minted at
/// creation (TaiPatches / the grid-stamp below) is read at WEAR to lift what the garment does, NERF-
/// FIRST, on the verified vanilla-clothing fields — plus the grid-craft stamp and the repair-gate:
///
///   • Warmth [vanilla] — CollectibleBehaviorWearable.GetWarmth scaled by the mark (Warm emphasis
///     included). Unmarked garments (loot / pre-update) read as vanilla.
///   • Wear [vanilla] — ChangeCondition's condition LOSS scaled by the mark (Lasting emphasis
///     included): a master's seams wear slower, a beginner's faster. Only the loss (negative change)
///     is scaled — a repair (positive) is left alone.
///   • Cooling [HoD, conditional] — GetCooling scaled by the mark (Cool emphasis included), reflected
///     so it is inert without Hot or Dead.
///   • Grid stamp + sewing/repair [vanilla] — grid-crafting a garment stamps the mark (stamp-only, no
///     XP); a vanilla clothing-repair recipe grants the sew verb and runs the REPAIR-GATE: an under-
///     ranked repair strips the mark (the master's hand is undone), an equal-or-higher repair keeps it.
///
/// Warmth/cooling scale on BOTH sides (the factor is derived purely from stack attributes, which sync
/// to the client), so tooltip and effect stay consistent. Wear is server-authoritative. Provenance is
/// a bottom-of-tooltip maker line (Journeyman up).
/// </summary>
public static class TaiMarkPatches
{
    /// <summary>The real garment stack captured at OnCreatedByCrafting (runs before ConsumeInput in
    /// CraftSingle), stamped/gated once the crafter is in scope at ConsumeInput. Single-threaded server.</summary>
    private static ItemStack? pendingWearableStack;
    private static bool pendingWearableRepair;

    // ------------------------------------------------------------ cooling read (HoD, conditional)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Hot or Dead adds a cooling read to the wearable behavior (absent on a no-HoD server). Try the
        // vanilla behavior first (HoD extends it), then a HoD-specific type. Reflected + isolated.
        var method = AccessTools.Method(typeof(CollectibleBehaviorWearable), "GetCooling")
                  ?? FindHodCooling();
        if (method != null && method.ReturnType == typeof(float))
        {
            harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(TaiMarkPatches), nameof(CoolingPostfix))));
            TcmLog.Info(api, "TAI cooling read hooked (HoD; mark scales garment cooling)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "TAI cooling seam not found (Hot or Dead absent); Cool emphasis inactive (warmth/wear unaffected)");
    }

    private static System.Reflection.MethodInfo? FindHodCooling()
    {
        foreach (var name in new[] { "HotDry.CollectibleBehaviorCooling", "HotOrDead.CoolingBehavior" })
        {
            var t = AccessTools.TypeByName(name);
            var m = t == null ? null : AccessTools.Method(t, "GetCooling");
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>Scale a garment's cooling by its Tailor's Mark (Cool emphasis included). The first
    /// argument of GetCooling is the ItemSlot on every HoD shape we target; read the mark off it.</summary>
    public static void CoolingPostfix(ItemSlot inslot, ref float __result)
    {
        var stack = inslot?.Itemstack;
        if (!TaiMark.HasMark(stack)) return;
        __result *= (float)TaiDomain.CoolingMul(TaiMark.LevelOf(stack), TaiMark.EmphasisOf(stack));
    }

    // ------------------------------------------------------------ warmth read (vanilla)

    /// <summary>Lift a garment's warmth by its Tailor's Mark (Warm emphasis included). Both sides — the
    /// factor is stack-derived, so tooltip and body-temp effect agree.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorWearable), nameof(CollectibleBehaviorWearable.GetWarmth))]
    public static class WarmthReadPatch
    {
        public static void Postfix(ItemSlot inslot, ref float __result)
        {
            var stack = inslot?.Itemstack;
            if (!TaiMark.HasMark(stack)) return;
            __result *= (float)TaiDomain.WarmthMul(TaiMark.LevelOf(stack), TaiMark.EmphasisOf(stack));
        }
    }

    // ------------------------------------------------------------ wear read (vanilla)

    /// <summary>Scale a garment's condition LOSS by its Tailor's Mark (Lasting emphasis included): a
    /// master's seams hold, a beginner's fray. Only the loss (changeVal &lt; 0) is scaled — a repair
    /// (changeVal &gt; 0) restores at face value.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorWearable), nameof(CollectibleBehaviorWearable.ChangeCondition))]
    public static class WearReadPatch
    {
        public static void Prefix(ItemSlot slot, ref float changeVal)
        {
            if (changeVal >= 0f) return;
            var stack = slot?.Itemstack;
            if (!TaiMark.HasMark(stack)) return;
            changeVal *= (float)TaiDomain.WearMul(TaiMark.LevelOf(stack), TaiMark.EmphasisOf(stack));
        }
    }

    // ------------------------------------------------------------ grid stamp + repair-gate (vanilla)

    /// <summary>Capture the real garment stack at OnCreatedByCrafting (runs before ConsumeInput) and
    /// whether this is a repair recipe, to stamp/gate once the crafter is in scope at ConsumeInput.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorWearable), nameof(CollectibleBehaviorWearable.OnCreatedByCrafting))]
    public static class WearableCapturePatch
    {
        public static void Postfix(ItemSlot outputSlot, IRecipeBase byRecipe)
        {
            pendingWearableStack = outputSlot?.Itemstack;
            pendingWearableRepair = byRecipe?.Name?.Path?.Contains("repair") ?? false;
        }
    }

    /// <summary>The vanilla TAI floor: grid-crafting a garment stamps the maker's mark (stamp-only, no
    /// XP — the assembly grid grants nothing). A clothing-repair recipe grants the sew verb and runs the
    /// repair-gate: an under-ranked repair strips the mark, an equal-or-higher one keeps it. Server-only,
    /// real take only (ConsumeInput is the real-craft seam, never a preview).</summary>
    [HarmonyPatch(typeof(GridRecipe), nameof(GridRecipe.ConsumeInput))]
    public static class WearableCraftPatch
    {
        public static void Postfix(IPlayer byPlayer, bool __result)
        {
            var stack = pendingWearableStack;
            bool repair = pendingWearableRepair;
            pendingWearableStack = null;
            pendingWearableRepair = false;

            if (!__result || stack == null || byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;

            int playerLevel = TaiDomain.LevelOf(byPlayer);

            if (repair)
            {
                // Sew verb grant + repair-gate. Under-ranked repair undoes the master's hand.
                AlmanacTcmModSystem.Instance?.Ledger?.Log(byPlayer, TaiDomain.Code, TaiDomain.TechSew,
                    HashCode.Combine("repair", stack.Collectible.Id, byPlayer.Entity.World.ElapsedMilliseconds / 1000));

                if (TaiMark.HasMark(stack) && playerLevel < TaiMark.LevelOf(stack))
                    TaiMark.Strip(stack);
                return;
            }

            // New garment: stamp-only (no XP). The crafter's live rank + book emphasis. The output slot
            // is synced by the surrounding CraftSingle flow (the ALC remedy-stamp pattern).
            TaiMark.Stamp(stack, byPlayer.PlayerUID, byPlayer.PlayerName, playerLevel, TaiEmphasis.EmphasisOf(byPlayer));
        }
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Tailor's Mark maker line (Journeyman up), bottom of the tooltip after a blank line,
    /// like the other domain marks. Reads the taiBy tag written on marked garments.</summary>
    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.GetDescription))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    public static class ProvenancePatch
    {
        public static void Postfix(ItemStack __instance, ref string __result)
        {
            var attrs = __instance?.Attributes;
            string? name = attrs?.GetString(TaiMark.ByNameAttr);
            if (string.IsNullOrEmpty(name) || __result == null) return;
            int level = attrs!.GetInt(TaiMark.LevelAttr);
            string? line =
                level >= TaiDomain.ProvGm ? Lang.Get("almanactcm:tai-master-by", name)
                : level >= TaiDomain.ProvMaster ? Lang.Get("almanactcm:tai-tailored-by", name)
                : level >= TaiDomain.ProvJourneyman ? Lang.Get("almanactcm:tai-sewn-by", name)
                : null;
            if (line != null) __result = __result.TrimEnd() + "\n\n" + line + "\n";
        }
    }
}

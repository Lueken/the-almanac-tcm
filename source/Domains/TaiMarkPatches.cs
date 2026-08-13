using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

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

    /// <summary>Stamp or repair-gate the garment the moment its stack is generated (0.4.24: the
    /// stamp moved here from ConsumeInput, the fix for the preview-regeneration staleness that
    /// silently dropped every deferred stamp; see ToolPartMarks.CreatedStampPostfix). The crafter
    /// comes from the grid's own InventoryBasePlayer. Fires per preview regeneration, each on a
    /// fresh clone, so the taken stack always carries the write. Stamp-only, no XP here: XP on a
    /// preview would pay for rearranging a grid.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorWearable), nameof(CollectibleBehaviorWearable.OnCreatedByCrafting))]
    public static class WearableCapturePatch
    {
        public static void Postfix(ItemSlot outputSlot, IRecipeBase byRecipe)
        {
            var stack = outputSlot?.Itemstack;
            if (stack == null) return;
            var player = (outputSlot!.Inventory as InventoryBasePlayer)?.Player;
            if (player?.Entity?.World?.Side != EnumAppSide.Server) return;

            int playerLevel = TaiDomain.LevelOf(player);
            bool repair = byRecipe?.Name?.Path?.Contains("repair") ?? false;

            if (repair)
            {
                // The repair-gate: an under-ranked repair undoes the master's hand.
                if (TaiMark.HasMark(stack) && playerLevel < TaiMark.LevelOf(stack))
                    TaiMark.Strip(stack);
                return;
            }

            // New garment: the crafter's live rank + book emphasis.
            TaiMark.Stamp(stack, player.PlayerUID, player.PlayerName, playerLevel, TaiEmphasis.EmphasisOf(player));
        }
    }

    /// <summary>The sew verb pays at the REAL take (ConsumeInput never runs for a preview): a
    /// clothing-repair recipe grants TAI sewing. Recipe and output are read off the consumed
    /// recipe itself, so no state is carried between the preview and the take.</summary>
    [HarmonyPatch(typeof(GridRecipe), nameof(GridRecipe.ConsumeInput))]
    public static class WearableCraftPatch
    {
        public static void Postfix(GridRecipe __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;
            if (!(__instance?.Name?.Path?.Contains("repair") ?? false)) return;
            var outStack = __instance?.Output?.ResolvedItemStack;
            if (outStack?.Collectible?.HasBehavior<CollectibleBehaviorWearable>() != true) return;

            AlmanacTcmModSystem.ServerInstance?.Ledger?.Log(byPlayer, TaiDomain.Code, TaiDomain.TechSew,
                HashCode.Combine("repair", outStack.Collectible.Id, byPlayer.Entity.World.ElapsedMilliseconds / 1000));
        }
    }

    // ------------------------------------------------------------ warmth delta annotation

    /// <summary>The suite-wide numbers ruling (2026-08-01) on the warmth line. Vanilla already
    /// prints the TRUE current warmth (its renderer calls GetWarmth, which the mark scales), in
    /// exactly the green/red this suite uses, so the leading number needs no help; only the
    /// maker's share is invisible. This postfix runs LAST, reconstructs the exact warmth
    /// fragment vanilla just appended (same Lang key, same color branch, same value), and
    /// replaces its final occurrence with fragment + delta. If another mod rewrote the line,
    /// the fragment won't match and nothing changes: fail-open, never garble.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorWearable), nameof(CollectibleBehaviorWearable.GetHeldItemInfo))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    public static class WarmthDeltaPatch
    {
        public static void Postfix(CollectibleBehaviorWearable __instance, ItemSlot inSlot, System.Text.StringBuilder dsc)
        {
            var stack = inSlot?.Itemstack;
            if (!TaiMark.HasMark(stack)) return;
            double mul = TaiDomain.WarmthMul(TaiMark.LevelOf(stack), TaiMark.EmphasisOf(stack));
            if (mul == 1.0) return;

            float warmth = __instance.GetWarmth(inSlot);   // already mark-scaled (true value)
            double delta = warmth - warmth / mul;
            string suffix = Engine.TcmTooltip.DeltaSuffix(delta);
            if (suffix.Length == 0) return;

            // Vanilla's exact composition for the current-warmth fragment (color branch at 0.05).
            string color = (double)warmth < 0.05 ? Engine.TcmTooltip.PenaltyColor : Engine.TcmTooltip.LiftColor;
            string fragment = "<font color=\"" + color + "\">" + Lang.Get("+{0:0.#}°C", warmth) + "</font>";

            string text = dsc.ToString();
            int at = text.LastIndexOf(fragment, System.StringComparison.Ordinal);
            if (at < 0) return;
            dsc.Remove(at, fragment.Length).Insert(at, fragment + suffix);
        }
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Tailor's Mark maker line (Journeyman up), bottom of the tooltip after a blank line,
    /// like the other domain marks. Reads the taiBy tag written on marked garments. Carries the
    /// wear-rate percent (the effect with no vanilla number): a master's seams wear slower, and
    /// the line now says by how much.</summary>
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
                level >= Rank.Grandmaster ? Lang.Get("almanactcm:tai-master-by", name)
                : level >= Rank.Master ? Lang.Get("almanactcm:tai-tailored-by", name)
                : level >= Rank.Journeyman ? Lang.Get("almanactcm:tai-sewn-by", name)
                : null;
            if (line == null) return;

            // The line prints Journeyman-up only, where the wear factor is never a penalty.
            double wearMul = TaiDomain.WearMul(level, TaiMark.EmphasisOf(__instance));
            int pct = (int)System.Math.Round((1.0 - wearMul) * 100.0);
            if (pct > 0) line += Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-wears-slower", pct));

            __result = __result.TrimEnd() + "\n\n" + line + "\n";
        }
    }
}

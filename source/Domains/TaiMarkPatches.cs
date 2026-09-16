using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
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

    /// <summary>The grid half of Tailoring, at the REAL take (ConsumeInput never runs for a
    /// preview). Until 0.5.12 this paid for clothing REPAIR only, which left the trade's entire
    /// vanilla chain unpaid: flax fibres become twine, twine becomes linen, linen becomes cloth
    /// and cloth becomes a garment, and every one of those is a GRID recipe. The three station
    /// verbs all live behind the Spinning Wheel mod, so a vanilla player earned TAI from nothing
    /// but repairs and dye baths, and nobody on any install was ever paid for making the garment
    /// itself: the mod stamped their Tailor's Mark on a piece and granted them no practice for
    /// having made it. Same seam ALC uses for remedies and MET for grid assembly.
    ///
    /// The hand path pays the SAME verbs at the SAME rate as the stations on purpose. The wheel
    /// and loom already earn their keep in material economy (fibre thrift, the steady fibre
    /// economy), so they do not also need an XP edge, and giving the hand path its own techniques
    /// would inflate the technique count that small-m is measured against.
    ///
    /// Spin and weave carry the stations' per-real-minute bucket, because that bucket is the
    /// anti-grind guard and the hand path must not be the way around it. Garments bucket per
    /// SECOND instead: a garment is singular and expensive, so its material cost is the limiter.
    /// </summary>
    [HarmonyPatch(typeof(GridRecipe), nameof(GridRecipe.ConsumeInput))]
    public static class WearableCraftPatch
    {
        /// <summary>What Tailoring recognises as its own material. Vanilla armour is `Wearable`
        /// at every material from linen to STEEL, so "the output is wearable" is not a test for
        /// tailoring: it would pay a smith TAI for a plate cuirass. The trade is defined by the
        /// material worked, not by the slot the result fills.</summary>
        private static readonly string[] TextileMarkers =
        {
            "linen", "cloth", "flax", "twine", "yarn", "wool", "silk", "canvas", "felt",
            "cotton", "hemp", "fabric", "thread", "leather", "hide", "pelt", "fur", "bolt",
        };

        private static bool IsTextile(CraftingRecipeIngredient ing)
        {
            // Wildcard recipes (vanilla clothes take code "*" with name "leather" and a variant
            // list) carry nothing useful in Code, so read the resolved stack first and fall back
            // to the ingredient's own name.
            string a = ing.ResolvedItemStack?.Collectible?.Code?.Path ?? "";
            string b = ing.Code?.Path ?? "";
            string c = ing.Name ?? "";
            foreach (var m in TextileMarkers)
                if (a.Contains(m) || b.Contains(m) || c.Contains(m)) return true;
            return false;
        }

        /// <summary>Consumed TEXTILE material: quantities of every ingredient that is cloth or hide,
        /// is not a tool, and is not handed back. The sewing kit is a returnedStack, so it is
        /// correctly not counted as cloth spent. Zero means this was not a tailoring craft.</summary>
        private static int ConsumedUnits(GridRecipe recipe)
        {
            var ings = recipe.ResolvedIngredients;
            if (ings == null) return 0;
            int n = 0;
            foreach (var ing in ings)
            {
                if (ing == null || ing.IsTool || ing.ReturnedStack != null) continue;
                if (!IsTextile(ing)) continue;
                n += Math.Max(1, ing.Quantity);
            }
            return n;
        }

        public static void Postfix(GridRecipe __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;
            var outStack = __instance?.Output?.ResolvedItemStack;
            var coll = outStack?.Collectible;
            if (coll == null) return;

            long ms = byPlayer.Entity.World.ElapsedMilliseconds;
            string path = coll.Code?.Path ?? "";
            var ledger = AlmanacTcmModSystem.ServerInstance?.Ledger;
            if (ledger == null) return;

            // --- the garment itself: making and mending are the same verb.
            if (coll.HasBehavior<CollectibleBehaviorWearable>())
            {
                bool repair = __instance!.Name?.Path?.Contains("repair") ?? false;
                // Material scaling (ruled 2026-09-16): a gambeson out of eight pieces is more
                // tailoring than a cuff out of two. Linear in consumed units against a two-unit
                // baseline, clamped so neither a trivial craft nor a bulk recipe distorts the
                // ledger. A repair consumes the patch material only, so it scales itself down
                // honestly without needing a separate rule.
                int units = ConsumedUnits(__instance);
                if (units <= 0) return;   // wearable, but no cloth or hide in it: not tailoring
                double mult = GameMath.Clamp(units / 2.0, 0.5, 2.0);
                ledger.Log(byPlayer, TaiDomain.Code, TaiDomain.TechSew,
                    HashCode.Combine(repair ? "repair" : "garment", coll.Id, ms / 1000), mult);
                return;
            }

            // --- the cloth chain. Vanilla: 4 flaxfibers -> flaxtwine -> 4 twine -> linen -> cloth.
            string? verb =
                path.Contains("twine") ? TaiDomain.TechSpin
                : (path.StartsWith("linen") || path.StartsWith("cloth")) ? TaiDomain.TechWeave
                : null;
            if (verb == null) return;

            ledger.Log(byPlayer, TaiDomain.Code, verb,
                HashCode.Combine("grid", verb, coll.Id, (int)(ms / 60000)));
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

    /// <summary>The Tailor's Mark maker line (Journeyman up). Reads the taiBy tag written on
    /// marked garments and carries the wear-rate percent (an effect with no vanilla number of its
    /// own): a master's seams wear slower, and the line says by how much. Placement, order and
    /// spacing belong to <see cref="Engine.ProvenanceLine"/>; this only decides what TAI has to
    /// say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(TaiMark.ByNameAttr);
        if (string.IsNullOrEmpty(name)) return null;
        int level = attrs!.GetInt(TaiMark.LevelAttr);
        string? line =
            level >= Rank.Grandmaster ? Lang.Get("almanactcm:tai-master-by", name)
            : level >= Rank.Master ? Lang.Get("almanactcm:tai-tailored-by", name)
            : level >= Rank.Journeyman ? Lang.Get("almanactcm:tai-sewn-by", name)
            : null;
        if (line == null) return null;

        // The line prints Journeyman-up only, where the wear factor is never a penalty.
        double wearMul = TaiDomain.WearMul(level, TaiMark.EmphasisOf(stack));
        int pct = (int)System.Math.Round((1.0 - wearMul) * 100.0);
        if (pct > 0) line += Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-wears-slower", pct));
        return line;
    }
}

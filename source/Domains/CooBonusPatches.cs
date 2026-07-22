using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// COO Phase 2 (rank-bonus-design §COO, RULED 2026-07-09; built 2026-07-21). Proposal B
/// throughout: no locked doors — the dish's complexity class sets how much rank can express.
///
/// ONE cookedBy STAMP, THREE JOBS (the Axis 2 unification ruling): the stamp written at meal
/// completion carries {uid, name, tier, complexity} and drives
///   • Axis 1 — the Untrained penalty: stamped food perishes faster (GetTransitionRateMul);
///   • Axis 6 — the Cook's Mark: tiered provenance in the tooltip (Cooked by / Prepared by /
///     Signature dish by) and the GM slow-spoil signature on the same perish postfix;
///   • the satiety/health edge (ruling 4): GetNutritionHealthMul scaled by tier x complexity,
///     capped ~+12%/+5% at GM on a C3 dish, ~0 on C0.
/// Axis 2 fuel economy: igniteWithFuel (:58643) scaled by the pit's lastCook rank — more meals
/// per fuel unit, never faster cooking. Axis 3 reliability: the char clock — a FINISHED bake
/// sitting in oven heat browns toward charred at a rank-scaled rate (IncrementallyBake dt,
/// scaled ONLY on the perfect stage so bake speed itself is untouched); Untrained x1.5, GM x0.5
/// floor, never zero. Axis 4 thrift: the extra-serving proc at meal completion (chance only,
/// GM-weighted — a master occasionally stretches a pot, never reliably).
///
/// Honest v1 scope: the stamp lands on MEAL-POT output (where the satiety virtual lives).
/// Mixing-bowl output stamping + EF expandedSats scaling need the bowl's output-slot layout
/// verified first; oven goods carry no stamp yet (bread has no meal-nutrition virtual; its
/// spoilage mark is a v2 nicety). Vanilla meal-pot/simmer cannot burn (verified) — the char
/// clock lives on the oven path only this build.
/// </summary>
public static class CooBonusPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    public const string CookByAttr = "almanactcm:cookby";
    public const string CookByNameAttr = "almanactcm:cookbyname";
    public const string CookTierAttr = "almanactcm:cooktier";
    public const string CookCxAttr = "almanactcm:cookcx";

    /// <summary>Placed-vessel stamp carriage (the 0.3.150 playtest gap): a PLACED pot serves
    /// bowls through its own BE method with no pot stack in sight, and a picked-up pot's stack
    /// is rebuilt without custom attrs. This pos-keyed map (packed "uid|name|tier|cx") carries
    /// the mark across both. Persisted: a placed pot can sit through restarts.</summary>
    private static Dictionary<string, string> mealStamps = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacCooMealStamps");
                if (data != null)
                    mealStamps = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
            }
            catch (Exception e) { TcmLog.Error(api, $"meal stamp map unreadable ({e.Message}); starting empty"); }
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacCooMealStamps",
                Vintagestory.API.Util.SerializerUtil.Serialize(mealStamps));
    }

    /// <summary>Provenance thresholds (ruled: a mark means something from Journeyman up).</summary>
    private const int TierJourneyman = 9, TierMaster = 13, TierGm = 17;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Axis 2 — fuel economy on the firepit family (vanilla oven burns its own wood and the
        // stonebake controller subclasses the firepit; the pit is the family root we hook).
        var t = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityFirepit");
        var m = t == null ? null : AccessTools.DeclaredMethod(t, "igniteWithFuel");
        if (m == null) TcmLog.Warn(api, "COO fuel economy seam not found (igniteWithFuel); axis inactive");
        else
        {
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), nameof(FuelPostfix))));
            TcmLog.Info(api, "COO fuel economy hooked (igniteWithFuel, lastCook-attributed)");
        }

        // Axis 1/3 — the char clock on the oven bake tick (dt prefix, perfect-stage only).
        var to = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityOven");
        var mo = to == null ? null : AccessTools.DeclaredMethod(to, "IncrementallyBake");
        if (mo == null) TcmLog.Warn(api, "COO char-clock seam not found (IncrementallyBake); axis inactive");
        else
        {
            harmony.Patch(mo, prefix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), nameof(CharClockPrefix))));
            TcmLog.Info(api, "COO char clock hooked (IncrementallyBake dt, perfect stage only)");
        }

        // The stamp must TRAVEL with the food (Jeffrey's serving-path walkthrough, 2026-07-21):
        // a meal is stamped on the POT, but what is eaten is a BOWL served from it (or a crock
        // stored from it). All three common serving paths funnel through ServeIntoStack (the
        // firepit bowl right-click :69567, the placed-pot fill, and the in-inventory held-bowl
        // click via TryMergeStacks :65239), and BlockCrock inherits the same base, so pot->crock
        // rides it too. One postfix propagates the stamp source -> destination.
        var tb = AccessTools.TypeByName("Vintagestory.GameContent.BlockCookedContainerBase");
        var mb = tb == null ? null : AccessTools.DeclaredMethod(tb, "ServeIntoStack");
        if (mb == null) TcmLog.Warn(api, "COO stamp propagation seam not found (ServeIntoStack); bowls will not carry the Cook's Mark");
        else
        {
            harmony.Patch(mb, postfix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), nameof(ServePostfix))));
            TcmLog.Info(api, "COO stamp propagation hooked (ServeIntoStack: pot -> bowl/crock)");
        }

        // Placed-vessel carriage (the 0.3.150 gap): store the stamp by POSITION when a marked
        // vessel is placed; re-apply when its stack is rebuilt at pickup; and mark the bowl
        // directly when the PLACED vessel's own BE serve runs (no pot stack exists there).
        HookDeclared2(api, harmony, "Vintagestory.GameContent.BlockEntityCookedContainer", "OnBlockPlaced",
            nameof(VesselPlacedPostfix), "COO stamp carriage (pot placed)");
        HookDeclared2(api, harmony, "Vintagestory.GameContent.BlockCookedContainer", "OnPickBlock",
            nameof(VesselPickPostfix), "COO stamp carriage (pot pickup)");
        HookDeclared2(api, harmony, "Vintagestory.GameContent.BlockEntityCookedContainer", "ServeInto",
            nameof(BeServePostfix), "COO stamp carriage (placed-pot serve)");
        HookDeclared2(api, harmony, "Vintagestory.GameContent.BlockEntityCrock", "ServeInto",
            nameof(BeServePostfix), "COO stamp carriage (placed-crock serve)");
        HookDeclared2(api, harmony, "Vintagestory.GameContent.BlockEntityCrock", "OnBlockPlaced",
            nameof(VesselPlacedPostfix), "COO stamp carriage (crock placed)");

        // The perish postfix (Axis 1 penalty + Axis 6 GM signature) and the provenance tooltip
        // are attribute patches below, applied by the Start PatchAll pass.
    }

    private static void HookDeclared2(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var ht = AccessTools.TypeByName(typeName);
        var hm = ht == null ? null : AccessTools.DeclaredMethod(ht, method);
        if (hm == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that carriage link is inactive"); return; }
        harmony.Patch(hm, postfix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
    }

    // ------------------------------------------------------------ placed-vessel carriage

    /// <summary>A marked vessel placed: remember the stamp at this position. An UNMARKED vessel
    /// placed clears any stale entry (a fresh pot on an old spot must not inherit a dead mark).</summary>
    public static void VesselPlacedPostfix(BlockEntity __instance, ItemStack? byItemStack)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        string key = PosKey(__instance.Pos);
        var attrs = byItemStack?.Attributes;
        if (attrs?.HasAttribute(CookTierAttr) == true)
        {
            mealStamps[key] = $"{attrs.GetString(CookByAttr)}|{attrs.GetString(CookByNameAttr)}|{attrs.GetInt(CookTierAttr)}|{attrs.GetInt(CookCxAttr)}";
            TcmLog.Cat(__instance.Api, "coo", $"vessel placed at {__instance.Pos} carries the mark of {attrs.GetString(CookByNameAttr)}; stored");
        }
        else mealStamps.Remove(key);
    }

    private static void ApplyPacked(ItemStack? stack, string packed)
    {
        if (stack == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 4) return;
        stack.Attributes.SetString(CookByAttr, p[0]);
        stack.Attributes.SetString(CookByNameAttr, p[1]);
        if (int.TryParse(p[2], out int tier)) stack.Attributes.SetInt(CookTierAttr, tier);
        if (int.TryParse(p[3], out int cx)) stack.Attributes.SetInt(CookCxAttr, cx);
    }

    /// <summary>Pickup rebuilds the vessel stack from BE data (custom attrs lost); restore the
    /// mark from the position store. The entry stays until overwritten or served empty: pickup
    /// is also called for drops and previews, so consuming it here would strip real pickups.</summary>
    public static void VesselPickPostfix(IWorldAccessor world, BlockPos pos, ItemStack __result)
    {
        if (world?.Side != EnumAppSide.Server || pos == null) return;
        if (mealStamps.TryGetValue(PosKey(pos), out string? packed) && packed != null)
            ApplyPacked(__result, packed);
    }

    /// <summary>The PLACED vessel's own serve: no pot stack exists in this path at all, so mark
    /// the served bowl straight from the position store.</summary>
    public static void BeServePostfix(BlockEntity __instance, ItemSlot slot)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || slot?.Itemstack == null) return;
        if (!mealStamps.TryGetValue(PosKey(__instance.Pos), out string? packed) || packed == null) return;
        if (slot.Itemstack.Block is not IBlockMealContainer) return; // only a filled meal container earns the mark
        ApplyPacked(slot.Itemstack, packed);
        slot.MarkDirty();
        TcmLog.Cat(__instance.Api, "coo", $"placed-vessel serve at {__instance.Pos}: mark applied to {slot.Itemstack.Collectible?.Code?.Path}");
    }

    // ------------------------------------------------------------ stamp propagation

    /// <summary>A serving moved pot -> bowl (or pot -> crock): the mark travels with the food.
    /// Known gap this build: a pot that was PLACED and picked back up loses its stamp on the
    /// roundtrip (the BE keeps only vanilla meal attrs) — BE-tree carriage is the P2b item, so
    /// serve off the firepit or from the carried pot to keep the mark.</summary>
    public static void ServePostfix(ItemSlot bowlSlot, ItemSlot potslot, IWorldAccessor world, bool __result)
    {
        if (!__result || world?.Side != EnumAppSide.Server) return; // the client early-returns without building (:65397)
        var src = potslot?.Itemstack?.Attributes;
        var dstStack = bowlSlot?.Itemstack;
        if (src == null || !src.HasAttribute(CookTierAttr))
        {
            TcmLog.Cat(world.Api, "coo", $"serve: pot carries no cook stamp ({potslot?.Itemstack?.Collectible?.Code?.Path ?? "null"}); nothing to propagate");
            return;
        }
        if (dstStack == null)
        {
            TcmLog.Cat(world.Api, "coo", "serve: pot is stamped but the destination slot is EMPTY post-serve; the filled bowl went elsewhere — propagation missed");
            return;
        }
        dstStack.Attributes.SetString(CookByAttr, src.GetString(CookByAttr) ?? "");
        dstStack.Attributes.SetString(CookByNameAttr, src.GetString(CookByNameAttr) ?? "");
        dstStack.Attributes.SetInt(CookTierAttr, src.GetInt(CookTierAttr));
        dstStack.Attributes.SetInt(CookCxAttr, src.GetInt(CookCxAttr));
        // The vanilla MarkDirty fired BEFORE this postfix ran; flag again so the stamped attrs
        // are what actually serialize to the client.
        bowlSlot!.MarkDirty();
        TcmLog.Cat(world.Api, "coo", $"serve: cook stamp propagated -> {dstStack.Collectible?.Code?.Path} (tier {src.GetInt(CookTierAttr)})");
    }

    // ------------------------------------------------------------ the stamp

    /// <summary>Write the cook stamp at a completion sink. Called by CooPatches with the verb's
    /// complexity knob; tier is the cook's COO level at that moment (frozen, like MET's mark).</summary>
    public static void StampCooked(ItemStack? stack, IPlayer cook, int cxClass)
    {
        if (stack == null || cook == null) return;
        stack.Attributes.SetString(CookByAttr, cook.PlayerUID);
        stack.Attributes.SetString(CookByNameAttr, cook.PlayerName);
        stack.Attributes.SetInt(CookTierAttr, CooDomain.LevelOf(cook));
        stack.Attributes.SetInt(CookCxAttr, cxClass);
    }

    // ------------------------------------------------------------ Axis 2 — fuel economy

    /// <summary>One shot per fuel load: scale the just-set burn duration by the pit cook's rank
    /// (Untrained 0.90 wastes fuel, GM 1.15 stretches it). Never touches cooking speed.</summary>
    public static void FuelPostfix(BlockEntity __instance)
    {
        if (__instance is not BlockEntityFirepit || __instance.Api?.Side != EnumAppSide.Server) return;
        IPlayer? cook = CooPatches.CookAtPublic(__instance.Api.World, __instance.Pos);
        if (cook == null) return;
        double f = CooDomain.RankLinear(CooDomain.LevelOf(cook),
            CooDomain.Knob(CooDomain.FuelUntrained, 0.90), CooDomain.Knob(CooDomain.FuelGm, 1.15));
        if (Math.Abs(f - 1.0) < 0.001) return;
        var tv = Traverse.Create(__instance);
        tv.Field("fuelBurnTime").SetValue((float)(tv.Field("fuelBurnTime").GetValue<float>() * f));
        tv.Field("maxFuelBurnTime").SetValue((float)(tv.Field("maxFuelBurnTime").GetValue<float>() * f));
    }

    // ------------------------------------------------------------ Axis 1/3 — the char clock

    /// <summary>Scale the browning tick ONLY when the slot holds a FINISHED bake (not dough, not
    /// partbaked, not already charred): that dt is purely the march toward charring, so baking
    /// speed stays vanilla while an Untrained cook's bread chars fast (x1.5) and a master's sits
    /// long (x0.5 floor — even a GM can burn wildly neglected food).</summary>
    public static void CharClockPrefix(BlockEntity __instance, ref float dt, int slotIndex)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        string? code = inv != null && slotIndex < inv.Count ? inv[slotIndex]?.Itemstack?.Collectible?.Code?.Path : null;
        if (code == null || code.StartsWith("dough") || code.Contains("partbaked") || code.Contains("charred")) return;

        IPlayer? cook = CooPatches.CookAtPublic(__instance.Api.World, __instance.Pos);
        if (cook == null) return;
        dt *= (float)CooDomain.RankLinear(CooDomain.LevelOf(cook),
            CooDomain.Knob(CooDomain.CharUntrained, 1.5), CooDomain.Knob(CooDomain.CharGm, 0.5));
    }

    // ------------------------------------------------------------ Axis 1 + 6 — the perish factor

    /// <summary>Stamped food perishes by its cook: the Untrained end is the penalty (their
    /// cooking spoils faster), the GM end is the Cook's Mark signature (a GM's rations keep).
    /// Mid ranks are vanilla — this is a two-ended lever by ruling, not a curve.</summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetTransitionRateMul))]
    public static class PerishPatch
    {
        public static void Postfix(ItemSlot inSlot, EnumTransitionType transType, ref float __result)
        {
            if (transType != EnumTransitionType.Perish) return;
            var attrs = inSlot?.Itemstack?.Attributes;
            if (attrs?.HasAttribute(CookTierAttr) != true) return;
            int tier = attrs.GetInt(CookTierAttr);
            if (tier <= 0) __result *= (float)CooDomain.Knob(CooDomain.SpoilUntrained, 1.15);
            else if (tier >= TierGm) __result *= (float)CooDomain.Knob(CooDomain.SpoilGm, 0.70);
        }
    }

    // ------------------------------------------------------------ the satiety/health edge

    /// <summary>Ruling 4: the complexity-weighted reward curve. edge = rankT x (cx/3) x GM cap —
    /// a GM's touch shows most on chain dishes, never on charred meat. Applied to the whole
    /// meal's satiety and, smaller, its heal-on-eat (one lever, two effects).</summary>
    [HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetNutritionHealthMul))]
    public static class NutritionPatch
    {
        public static void Postfix(ItemSlot slot, ref float[] __result)
        {
            var attrs = slot?.Itemstack?.Attributes;
            if (attrs?.HasAttribute(CookTierAttr) != true || __result is not { Length: >= 2 }) return;
            double t = CooDomain.BonusT(attrs.GetInt(CookTierAttr));
            if (t <= 0) return;
            double cxWeight = Math.Clamp(attrs.GetInt(CookCxAttr) / 3.0, 0, 1);
            __result[0] *= (float)(1.0 + t * cxWeight * CooDomain.Knob(CooDomain.SatietyGmC3, 0.12));
            __result[1] *= (float)(1.0 + t * cxWeight * CooDomain.Knob(CooDomain.HealthGmC3, 0.05));
        }
    }

    // ------------------------------------------------------------ Axis 6 — provenance tooltip

    /// <summary>The Cook's Mark line (from Journeyman up, ruled): Cooked by / Prepared by /
    /// Signature dish by. The GM line also names the keep (the slow-spoil signature).</summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo))]
    public static class ProvenancePatch
    {
        public static void Postfix(ItemSlot inSlot, System.Text.StringBuilder dsc)
        {
            var attrs = inSlot?.Itemstack?.Attributes;
            string? name = attrs?.GetString(CookByNameAttr);
            if (name == null) return;
            int tier = attrs!.GetInt(CookTierAttr);
            if (tier >= TierGm) dsc.AppendLine(Lang.Get("almanactcm:signature-by", name));
            else if (tier >= TierMaster) dsc.AppendLine(Lang.Get("almanactcm:prepared-by", name));
            else if (tier >= TierJourneyman) dsc.AppendLine(Lang.Get("almanactcm:cooked-by", name));
        }
    }
}

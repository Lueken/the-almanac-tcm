using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

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
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
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
    // Rank thresholds moved to Leveling/Rank.cs (2026-08-12): was COO's private
    // `Rank.Journeyman/Rank.Master/Rank.Grandmaster` variant of the same 9/13/17 triplet.

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

        // The stone bake oven's baking top REIMPLEMENTS IncrementallyBake on its own class
        // (verified 1.3.8: BlockEntityOvenBakingTop : BlockEntityDisplay, nothing shared with
        // the clay oven), so the clock has to be hung on it separately. Same prefix, second oven:
        // the signature matches by parameter name, and the cook lookup rides the same lastCook
        // map, which the take-stamp pair (hooked in CooPatches) writes at the top's own pos.
        if (api.ModLoader.IsModEnabled("stonebakeoven"))
        {
            var ts = AccessTools.TypeByName("StoneBakeOven.BlockEntityOvenBakingTop");
            var ms = ts == null ? null : AccessTools.DeclaredMethod(ts, "IncrementallyBake");
            if (ms == null) TcmLog.Warn(api, "COO char-clock seam not found on the stone baking top (StoneBakeOven.BlockEntityOvenBakingTop.IncrementallyBake); stone bakes never char-scale");
            else
            {
                harmony.Patch(ms, prefix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), nameof(CharClockPrefix))));
                TcmLog.Info(api, "COO char clock hooked (stone baking top, same clock)");
            }
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
            harmony.Patch(mb,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), nameof(ServePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CooBonusPatches), nameof(ServePostfix))));
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

        // The perish postfix (Axis 1 penalty + Axis 6 GM signature) is an attribute patch below,
        // applied by the Start PatchAll pass. The Cook's Mark LINE is not: it is contributed to
        // Engine.ProvenanceLine (see MarkLine below), which owns the provenance block.
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
        // The three propagation hops (serve to bowl, placed-vessel serve, vessel pickup) all land
        // here, so the displacement covers them without four separate call sites.
        Engine.FoodProvenance.CookDisplacesGrower(stack, sapi);
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

    /// <summary>Read the pot's mark BEFORE vanilla can destroy it (fixed 2026-08-13; see
    /// ServePostfix for the failure this repairs). Also captures the pot's collectible so the
    /// postfix can tell a drained pot from one that still holds food.</summary>
    public static void ServePrefix(ItemSlot potslot, IWorldAccessor world, out (string? Mark, bool FromStack, CollectibleObject? Pot) __state)
    {
        __state = (null, false, potslot?.Itemstack?.Collectible);
        if (world?.Side != EnumAppSide.Server) return;

        // The pot stack's own attrs first; failing that the POSITION store, via the slot's owning
        // inventory (the firepit's InventorySmelting carries its pos), which survives the pack's
        // cooked-pot stack conversion. Ground-stored pots have neither an InventorySmelting nor a
        // position entry, which is exactly why the stack read has to happen here and not after.
        var src = potslot?.Itemstack?.Attributes;
        bool fromStack = src?.HasAttribute(CookTierAttr) == true;
        string? packed = fromStack
            ? $"{src!.GetString(CookByAttr)}|{src.GetString(CookByNameAttr)}|{src.GetInt(CookTierAttr)}|{src.GetInt(CookCxAttr)}"
            : null;
        if (packed == null && (potslot?.Inventory as InventorySmelting)?.pos is BlockPos invPos
            && mealStamps.TryGetValue(PosKey(invPos), out string? stored))
            packed = stored;

        __state = (packed, fromStack, potslot?.Itemstack?.Collectible);
    }

    /// <summary>A serving moved pot -> bowl (or pot -> crock): the mark travels with the food.
    ///
    /// FIXED 2026-08-13, and the shape is worth remembering because it is the second time this
    /// exact bug has appeared (the quern merge was the first). The read used to live HERE, and it
    /// failed whenever the serving drained the pot, because vanilla's SetServingsMaybeEmpty
    /// (BlockCookedContainerBase.cs:106) REPLACES potslot.Itemstack with a brand-new stack of the
    /// emptied block (attributes and all) inside ServeIntoStack, before any postfix runs. It
    /// looked like a crock-vs-bowl bug in play, but it is a capacity bug: a crock takes 4 servings
    /// and a bowl takes 1, so the crock empties a pot that the bowl leaves standing. A bowl served
    /// from a pot holding its last serving loses the mark the same way. The read is now a prefix,
    /// and vanilla cannot get there first.</summary>
    public static void ServePostfix(ItemSlot bowlSlot, ItemSlot potslot, IWorldAccessor world, bool __result,
        (string? Mark, bool FromStack, CollectibleObject? Pot) __state)
    {
        if (!__result || world?.Side != EnumAppSide.Server) return; // the client early-returns without building (:65397)
        var dstStack = bowlSlot?.Itemstack;

        if (__state.Mark is not string packed)
        {
            TcmLog.Cat(world.Api, "coo", $"serve: no cook stamp on pot ({__state.Pot?.Code?.Path ?? "null"}) and none stored for its position; nothing to propagate");
            return;
        }
        if (dstStack == null)
        {
            TcmLog.Cat(world.Api, "coo", "serve: stamp resolved but the destination slot is EMPTY post-serve; propagation missed");
            return;
        }
        ApplyPacked(dstStack, packed);
        bowlSlot!.MarkDirty(); // vanilla's dirty flag fired before this postfix; re-flag so the attrs ship

        // Heal the pot's own stack when the conversion stripped its mark, so taking and placing it
        // later carries the mark forward. Only while it still HOLDS something: a pot vanilla just
        // swapped for the emptied block is a different object with no cook, and stamping that would
        // hand a clean pot somebody else's provenance.
        var pot = potslot?.Itemstack;
        if (!__state.FromStack && pot != null && pot.Collectible == __state.Pot
            && !pot.Attributes.HasAttribute(CookTierAttr))
        {
            ApplyPacked(pot, packed);
            potslot!.MarkDirty();
        }
        TcmLog.Cat(world.Api, "coo", $"serve: cook stamp -> {dstStack.Collectible?.Code?.Path} ({packed.Split('|')[1]}, via {(__state.FromStack ? "stack" : "position store")})");
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
        // The cook now owns it, if it is something a player eats (RULED 2026-08-13).
        Engine.FoodProvenance.CookDisplacesGrower(stack, sapi);
    }

    /// <summary>Anchor the completion stamp to the VESSEL'S POSITION as well (the firepit at
    /// completion). The 0.3.151 playtest proved the pack converts a fresh-cooked pot into a
    /// differently-coded stack seconds after DoSmelt, discarding stack attrs — so identity
    /// cannot be trusted; the position can. The serve heals from this store.</summary>
    public static void StoreStampAt(BlockPos? pos, IPlayer cook, int cxClass)
    {
        if (pos == null || cook == null) return;
        mealStamps[PosKey(pos)] = $"{cook.PlayerUID}|{cook.PlayerName}|{CooDomain.LevelOf(cook)}|{cxClass}";
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
            if (Engine.Attribution.Suppressed) return; // the freshness-line probe is measuring vanilla
            if (transType != EnumTransitionType.Perish) return;
            var attrs = inSlot?.Itemstack?.Attributes;
            if (attrs?.HasAttribute(CookTierAttr) != true) return;
            int tier = attrs.GetInt(CookTierAttr);
            if (tier <= 0) __result *= (float)CooDomain.Knob(CooDomain.SpoilUntrained, 1.15);
            else if (tier >= Rank.Grandmaster) __result *= (float)CooDomain.Knob(CooDomain.SpoilGm, 0.70);
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
            // PIES NEVER, by ruling (2026-08-13): "pies would be pushed too far into broken
            // territory... upwards of 1700 satiety between all 4 pieces." Today this guard is
            // unreachable, because BlockPie overrides GetNutritionHealthMul without calling the
            // BlockMeal base this patch rides. It exists so the ruling survives a vanilla update
            // that adds a base call, instead of silently breaking the day pies start stamping.
            if (slot?.Itemstack?.Block is BlockPie) return;
            var attrs = slot?.Itemstack?.Attributes;
            if (attrs?.HasAttribute(CookTierAttr) != true || __result is not { Length: >= 2 }) return;
            double t = CooDomain.BonusT(attrs.GetInt(CookTierAttr));
            if (t <= 0) return;
            double cxWeight = Math.Clamp(attrs.GetInt(CookCxAttr) / 3.0, 0, 1);
            __result[0] *= (float)(1.0 + t * cxWeight * CooDomain.Knob(CooDomain.SatietyGmC3, 0.12));
            __result[1] *= (float)(1.0 + t * cxWeight * CooDomain.Knob(CooDomain.HealthGmC3, 0.05));
        }
    }

    // ------------------------------------------------------------ nutrition facts deltas

    /// <summary>The suite-wide numbers ruling (2026-08-01) on the Nutrition Facts block. The
    /// displayed satiety and health are already TRUE (the tooltip multiplies through
    /// GetNutritionHealthMul, which the cook's stamp scales); this annotates each line with
    /// the cook's share. Reconstruction is bit-exact: the same props builder is called twice
    /// with the same argument shapes vanilla used, once at the full multipliers (matching the
    /// rendered numbers) and once at the multipliers with the cook's edge divided back out
    /// (the baseline). Any line that fails to match verbatim is left alone: fail-open.</summary>
    [HarmonyPatch(typeof(BlockMeal), nameof(BlockMeal.GetContentNutritionFacts),
        new[] { typeof(IWorldAccessor), typeof(ItemSlot), typeof(ItemStack[]), typeof(EntityAgent), typeof(bool), typeof(float), typeof(float) })]
    public static class NutritionFactsDeltaPatch
    {
        public static void Postfix(BlockMeal __instance, IWorldAccessor world, ItemSlot inSlotorFirstSlot,
            ItemStack[] contentStacks, EntityAgent? forEntity, bool mulWithStacksize,
            float nutritionMul, float healthMul, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            var attrs = inSlotorFirstSlot?.Itemstack?.Attributes;
            if (attrs?.HasAttribute(CookTierAttr) != true) return;

            double t = CooDomain.BonusT(attrs.GetInt(CookTierAttr));
            double cxWeight = Math.Clamp(attrs.GetInt(CookCxAttr) / 3.0, 0, 1);
            if (t <= 0 || cxWeight <= 0) return;
            double satEdge = 1.0 + t * cxWeight * CooDomain.Knob(CooDomain.SatietyGmC3, 0.12);
            double healthEdge = 1.0 + t * cxWeight * CooDomain.Knob(CooDomain.HealthGmC3, 0.05);
            if (satEdge == 1.0 && healthEdge == 1.0) return;

            var full = Tally(__instance, world, inSlotorFirstSlot!, contentStacks, forEntity, mulWithStacksize, nutritionMul, healthMul);
            var baseline = Tally(__instance, world, inSlotorFirstSlot!, contentStacks, forEntity, mulWithStacksize,
                (float)(nutritionMul / satEdge), (float)(healthMul / healthEdge));

            string text = __result;
            foreach (var kv in full.satByCat)
            {
                baseline.satByCat.TryGetValue(kv.Key, out float baseVal);
                string suffix = Engine.TcmTooltip.DeltaSuffix(kv.Value - baseVal, "0");
                if (suffix.Length == 0) continue;
                string line = Lang.Get("nutrition-facts-line-satiety",
                    Lang.Get("foodcategory-" + kv.Key.ToString().ToLowerInvariant()), Math.Round(kv.Value));
                int at = text.IndexOf(line, StringComparison.Ordinal);
                if (at >= 0) text = text.Remove(at, line.Length).Insert(at, line + suffix);
            }
            if (full.health != 0f)
            {
                string hSuffix = Engine.TcmTooltip.DeltaSuffix(full.health - baseline.health);
                if (hSuffix.Length > 0)
                {
                    string hLine = "- " + Lang.Get("Health: {0}{1} hp", (full.health > 0f) ? "+" : "", full.health);
                    int at = text.IndexOf(hLine, StringComparison.Ordinal);
                    if (at >= 0) text = text.Remove(at, hLine.Length).Insert(at, hLine + hSuffix);
                }
            }
            __result = text;
        }

        /// <summary>Vanilla's own accumulation loop, verbatim in shape, so the full-multiplier
        /// pass reproduces the rendered numbers exactly.</summary>
        private static (Dictionary<Vintagestory.API.Common.EnumFoodCategory, float> satByCat, float health) Tally(
            BlockMeal meal, IWorldAccessor world, ItemSlot slot, ItemStack[] stacks, EntityAgent? forEntity,
            bool mulWithStacksize, float nutritionMul, float healthMul)
        {
            var props = BlockMeal.GetContentNutritionProperties(world, slot, stacks, forEntity, mulWithStacksize, nutritionMul, healthMul);
            var byCat = new Dictionary<Vintagestory.API.Common.EnumFoodCategory, float>();
            float health = 0f;
            foreach (var p in props)
            {
                if (p == null) continue;
                byCat.TryGetValue(p.FoodCategory, out float v);
                health += p.Health;
                byCat[p.FoodCategory] = v + p.Satiety;
            }
            return (byCat, health);
        }
    }

    // ------------------------------------------------------------ Axis 6 — provenance tooltip

    /// <summary>The Cook's Mark line (from Journeyman up, ruled): Cooked by / Prepared by /
    /// Signature dish by. The block sits at the very bottom of the tooltip because meal tooltips
    /// are dense (gourmand, nutrition facts) and a mark is a signature, not a stat (Jeffrey's
    /// placement ruling, 2026-07-21). That placement is why the arbiter rides
    /// ItemStack.GetDescription, the OUTERMOST aggregator, rather than GetHeldItemInfo, which
    /// would land mid-tooltip: a meal's own override calls the base midway through its text.
    /// Placement, order and spacing belong to <see cref="Engine.ProvenanceLine"/>; this only
    /// decides what COO has to say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(CookByNameAttr);
        if (string.IsNullOrEmpty(name)) return null;
        // CookTierAttr is LEVEL-valued (StampCooked writes CooDomain.LevelOf); the key name
        // predates the level/tier cleanup and is kept for save compatibility.
        int level = attrs!.GetInt(CookTierAttr);
        string? line =
            level >= Rank.Grandmaster ? Lang.Get("almanactcm:signature-by", name)
            : level >= Rank.Master ? Lang.Get("almanactcm:prepared-by", name)
            : level >= Rank.Journeyman ? Lang.Get("almanactcm:cooked-by", name)
            // The Untrained penalty names ITSELF (RULED 2026-08-12). It is the only band
            // below Journeyman that gets a line, because it is the only one that changes the
            // rules: PerishPatch multiplies the perish rate at exactly this level. A penalty
            // the player cannot see is hidden state, and hidden state that changes the rules
            // has to publish its reason. Guarded on the knob so a server that tunes the
            // penalty away does not keep the accusation.
            : level <= Rank.Untrained && CooDomain.Knob(CooDomain.SpoilUntrained, 1.15) > 1.0
                ? $"<font color=\"{Engine.TcmTooltip.PenaltyColor}\">"
                  + Lang.Get("almanactcm:careless-cooking") + "</font>"
            : null;

        // SUPERSEDED 2026-08-12: the spoilage number is no longer stated here.
        //
        // Both this domain and FAR postfix the same CollectibleObject.GetTransitionRateMul, so
        // their factors COMPOSE. A per-domain clause here stated one factor as if it were the
        // whole effect: two GM marks printed "spoils 30% slower" twice for a real ~51%, and an
        // Untrained cook on a GM grower's produce printed "15% faster" AND "30% slower" for a
        // real ~19.5% slower. The old clauses were: tip-spoils-slower at GM, tip-spoils-faster
        // at Untrained (which also REPLACED this line, since the mark itself is Journeyman-up
        // by ruling).
        //
        // Vanilla's own freshness line already carries the composed truth, because
        // AppendPerishableInfoText divides FreshHoursLeft by the fully composed rate.
        // Engine.PerishAttributionPatch now annotates that line with TCM's true delta, red or
        // green automatically. The effect is stated once, on the line where it cannot lie.
        //
        // The split that replaces it: this line carries the REASON ("Carelessly cooked"),
        // vanilla's freshness line carries the composed MAGNITUDE ("Fresh for 8.7 days
        // (-1.1)" in red). Neither repeats the other, and only the composed one states a
        // number, so nothing here can contradict what the item actually does.

        return line;
    }
}

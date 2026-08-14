using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// COO hooks (rank-bonus-design §COO, ruled 2026-07-09; technique-maps §COO; seams verified
/// against the LIVE 1.22.3 binaries 2026-07-21).
///
/// The core problem this file solves: cooking COMPLETES UNATTENDED. DoSmelt (meal pot and
/// direct-heat), IncrementallyBake (oven) and grindInput (quern) all fire with no player in
/// scope — verified. So attribution is the xSkills-Ownable shape the technique maps ruled:
/// stamp the cook at their last player-attributed touch, credit at the completion event.
///
///   • lastCook stamp — the firepit GUI open (BlockEntityOpenableContainer.OnReceivedClientPacket,
///     guarded to firepits: the packet fires when a player opens the pit to load it) and the oven
///     load interact (BlockEntityOven.OnInteract, byPlayer in scope). Keyed by BE pos, in-memory
///     (a restart mid-cook only costs that one credit).
///   • meal-pot — BlockCookingContainer.DoSmelt postfix; success-gated by an output transform
///     (a null recipe returns early leaving slots unchanged, so burned/invalid pots bank nothing).
///   • direct-heat — the base CollectibleObject.DoSmelt (fires only for collectibles that do NOT
///     override it, so crucibles/pots route to their own patches); gated on SmeltingType
///     Cook/Bake so ores and ceramics never bank COO. ACA's ItemExpandedRawFood overrides DoSmelt
///     (the Harmony override rule) and gets its own conditional hook.
///   • oven baking — IncrementallyBake prefix/postfix pair detects the slot's stack transform
///     (per-tick calls bank nothing until the bake actually converts); credits the stamped loader.
///   • quern milling — grindInput postfix reads the BE's own playersGrinding dict (the game
///     tracks the cranking players); RULED COO 50 / FAR 50, one listener grants both halves.
///     The automated (mechanized) quern credits nobody — automation stays vanilla, the PAN
///     machine precedent.
///   • juicing / prep-table — player-attributed direct postfixes (unchanged from 1a).
///
/// Still unwired (seafarer/ACA decompiles pending, the verify-before-hook rule): griddling,
/// bowl mixing, rack drying, salt evaporation.
/// </summary>
public static class CooPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    /// <summary>BE pos -> the last player to open/load that cooking apparatus (the cook).
    /// In-memory: the durable record is the banked practice itself.</summary>
    private static readonly Dictionary<string, string> lastCook = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // --- the cook stamp ---------------------------------------------------------------
        // The physical right-click on the firepit block (BlockFirepit.OnBlockInteractStart,
        // declared :69496) — opening the pit to load it IS the cook's touch. (The 0.3.136 attempt
        // used the openable-container packet handler, which turned out to be declared only on
        // BlockEntity itself, so that hook warn-skipped at boot and nothing ever stamped.)
        Hook(api, harmony, "Vintagestory.GameContent.BlockFirepit", "OnBlockInteractStart", nameof(FirepitInteractPostfix), "COO firepit cook-stamp");
        // Oven baking — credit at PICKUP of the finished good (RULED 2026-07-21, replacing the
        // in-oven transform crediting which paid per stage and only tagged the first loaf). The
        // picker is in scope, so no cook-stamp is needed: the interact pair diffs the oven's
        // slots and credits each finished bake taken (dough/partbaked back out = nothing,
        // charred = the logged refusal).
        HookPairDeclared(api, harmony, "Vintagestory.GameContent.BlockEntityOven", "OnInteract",
            nameof(OvenTakePrefix), nameof(OvenTakePostfix), "COO oven baking (credit at pickup)");

        // --- the completion sinks ---------------------------------------------------------
        HookPair(api, harmony, "Vintagestory.GameContent.BlockCookingContainer", "DoSmelt",
            nameof(SmeltPrefix), nameof(MealPotPostfix), "COO meal-pot");
        HookPair(api, harmony, "Vintagestory.API.Common.CollectibleObject", "DoSmelt",
            nameof(SmeltPrefix), nameof(DirectHeatPostfix), "COO direct-heat");
        if (api.ModLoader.IsModEnabled("aculinaryartillery"))
            HookPair(api, harmony, "ACulinaryArtillery.ItemExpandedRawFood", "DoSmelt",
                nameof(SmeltPrefix), nameof(DirectHeatPostfix), "COO direct-heat (ACA)");
        // Prefix as well as postfix since 0.4.38: the grind CONSUMES the input, so the grower's
        // mark has to be captured before it is gone and re-applied to the flour after.
        // Priority.First on the PREFIX, and it is load-bearing whenever ACA is installed.
        //
        // ACA registers its own bool-returning prefix on this same method
        // (BlockEntityQuernPatch.grindInputWIthInheritedAttributes, verified against 2.0.0-dev.21).
        // When the ground output is an IExpandedFood it runs EF's own attribute inheritance, then
        // repeats vanilla's merge-or-spawn dance itself, then returns false to skip the original.
        // Our prefix has to lift the mark off the output slot BEFORE that runs, or ACA's merge
        // compares marked flour against plain and takes the spawn branch, ejecting it unmarked.
        // That is precisely the bug fixed on 2026-08-13, and load order alone would bring it back.
        // Neither patch declared a priority, so the order was whatever Harmony happened to pick.
        HookPairDeclared(api, harmony, "Vintagestory.GameContent.BlockEntityQuern", "grindInput",
            nameof(QuernPrefix), nameof(QuernPostfix), "COO+FAR quern milling", Priority.First);

        // --- player-attributed verbs (1a, unchanged) ---------------------------------------
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFruitPress", "OnBlockInteractStop", nameof(JuicePostfix), "COO juicing");

        // --- the seafarer stations (all seams verified against seafarer 0.5.15, 2026-07-21) ---
        if (api.ModLoader.IsModEnabled("seafarer"))
        {
            Hook(api, harmony, "Seafarer.BlockEntityPrepTable", "OnInteract", nameof(PrepPostfix), "COO prep-table");
            // Griddle: player-attributed load (TryPutFood :868) stamps the cook; the hearth tick
            // completes unattended (CompleteCooking :1015) and credits the stamp.
            HookDeclared(api, harmony, "Seafarer.BlockEntityGriddleHearth", "TryPutFood", nameof(GriddleLoadPostfix), "COO griddle cook-stamp");
            HookDeclared(api, harmony, "Seafarer.BlockEntityGriddleHearth", "CompleteCooking", nameof(GriddleCompletePostfix), "COO griddling");
            // Rack drying: credit at take-out of a TRANSITIONED stack (placement grants nothing,
            // the anti-farm ruling; a fresh item taken straight back is still pending its dry).
            HookPairDeclared(api, harmony, "Seafarer.BlockEntityDryingFrame", "TryTake",
                nameof(DryTakePrefix), nameof(DryTakePostfix), "COO rack drying (frame)");
            // Salt evaporation: TryHarvest (:2244) is CanHarvest-gated, player in scope.
            HookDeclared(api, harmony, "Seafarer.BlockEntitySaltPan", "TryHarvest", nameof(SaltHarvestPostfix), "COO salt evaporation");
        }

        // --- the ACA stations (verified against ACA 2.0.0-dev.21, 2026-07-21) ---------------
        if (api.ModLoader.IsModEnabled("aculinaryartillery"))
        {
            // Mixing bowl: the quern shape exactly — the BE tracks its cranking players
            // (playersMixing :778) and an automated flag; mixInput (:1034) fires on completion.
            HookPairDeclared(api, harmony, "ACulinaryArtillery.BlockEntityMixingBowl", "mixInput",
                nameof(MixingPrefix), nameof(MixingPostfix), "COO bowl mixing");
            // Meat hooks: the second rack-drying BE (one verb, two racks — the casting-merge
            // precedent). Same TryTake signature as the seafarer frame, one shared pair.
            HookPairDeclared(api, harmony, "ACulinaryArtillery.BlockEntityMeatHooks", "TryTake",
                nameof(DryTakePrefix), nameof(DryTakePostfix), "COO rack drying (meat hooks)");
            // Saucepan simmering: the pan rides the firepit input slot, ingredients ride the
            // firepit's cooking slots, and BlockSaucepan.DoSmelt builds the result as a clone of
            // the recipe's SmeltedStack (verified dev.21), so provenance dies there without this.
            HookPairDeclared(api, harmony, "ACulinaryArtillery.BlockSaucepan", "DoSmelt",
                nameof(SimmerPrefix), nameof(SimmerPostfix), "COO saucepan simmering (provenance)");
            // The EXPANDED OVEN needs nothing, verified against dev.21 and worth recording so
            // nobody re-audits it: BlockEntityExpandedOven overrides only OnBurnTick and
            // IncrementallyBake, and both scale dt then CALL BASE, so the char clock (patched on
            // the vanilla base method) still runs inside the base call, and the take-stamp rides
            // OnInteract, which it does not override at all.
        }
    }

    private static void Hook(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.Method(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName}.{method}); that verb is inactive this build"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    private static void HookPair(ICoreAPI api, Harmony harmony, string typeName, string method, string prefix, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.Method(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName}.{method}); that verb is inactive this build"); return; }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    /// <summary>DECLARED-strict single postfix (the trough lesson: never let AccessTools walk
    /// silently up the hierarchy on an override-sensitive seam).</summary>
    private static void HookDeclared(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
    }

    /// <param name="prefixPriority">Harmony priority for the PREFIX only. Defaults to Normal.
    /// Raise it when another mod prefixes the same method and our prefix has to win the race, which
    /// is the quern's situation: see the ACA note at the grind hook.</param>
    private static void HookPairDeclared(ICoreAPI api, Harmony harmony, string typeName, string method, string prefix, string postfix, string label,
        int prefixPriority = Priority.Normal)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), prefix)) { priority = prefixPriority },
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared{(prefixPriority == Priority.Normal ? "" : $", prefix priority {prefixPriority}")})");
    }

    // ------------------------------------------------------------ the cook stamp

    /// <summary>Right-clicking a firepit (opening it to load, adding fuel) marks the player as
    /// the pit's cook. Keyed by the firepit BE's position — the same pos InventorySmelting
    /// carries into DoSmelt, so the completion lookup matches.</summary>
    public static void FirepitInteractPostfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (byPlayer == null || blockSel == null || world?.Side != EnumAppSide.Server) return;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityFirepit be) return;
        string key = PosKey(be.Pos);
        bool changed = !lastCook.TryGetValue(key, out string? prev) || prev != byPlayer.PlayerUID;
        lastCook[key] = byPlayer.PlayerUID;
        if (changed) // one line per cook change, not one per click
            TcmLog.Cat(world.Api, "coo", $"firepit cook stamp: {be.Pos} -> {byPlayer.PlayerName}");
    }

    // ------------------------------------------------------------ oven baking (credit at pickup)

    /// <summary>Snapshot the oven's slots before the interact so the postfix can see what left.</summary>
    public static void OvenTakePrefix(BlockEntity __instance, IPlayer byPlayer, out string?[] __state)
    {
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        __state = new string?[inv?.Count ?? 0];
        for (int i = 0; i < __state.Length; i++)
            __state[i] = inv![i]?.Itemstack?.Collectible?.Code?.Path;

        // Stamp the finished loaves HERE, before the interaction can carry one off. The postfix
        // fires after the take, by which point the bread is in the player's hands and finding it
        // again means scanning inventories. In the prefix it is still sitting in a known slot.
        //
        // This also closes COO's documented v1 gap ("oven goods carry no stamp yet"): bread has
        // real nutritionProps, so it is FOOD, so the baker owns it and the grower's mark is
        // displaced (RULED 2026-08-13, docs/design/food-provenance-chain.md).
        //
        // The baker, not the taker. XP is credited at pickup by an earlier ruling, but the MARK
        // is provenance: it belongs to whoever loaded and fired the oven, which is exactly what
        // the lastCook map records and what the fuel economy and char clock already read.
        if (__instance?.Api?.Side != EnumAppSide.Server || inv == null || byPlayer == null) return;

        // THE OVEN HAD NO COOK STAMP AT ALL until 0.4.38, and nothing said so. lastCook was
        // written only by the firepit interact and the seafarer griddle load, on the reasoning
        // (line 65-71) that oven XP credits the picker, who is already in scope. True for XP, but
        // it left CookAt returning null for every oven, which silently disabled TWO things:
        // this stamp, and COO's char clock, which patches BlockEntityOven.IncrementallyBake and
        // looks the cook up exactly the same way. The Axis 3 browning lever has never fired.
        //
        // Read BEFORE writing, deliberately. The stamp on a finished loaf belongs to whoever
        // loaded and fired the oven, which is the PREVIOUS interact, not this one. So a loaf baked
        // by A and collected by B still reads as A's work, while B becomes the cook of record for
        // whatever is loaded next. XP keeps crediting the taker under the 2026-07-21 ruling; a
        // mark is provenance, which is a different question.
        IPlayer? baker = CookAt(__instance.Api.World, __instance.Pos) ?? byPlayer;

        string key = PosKey(__instance.Pos);
        bool changed = !lastCook.TryGetValue(key, out string? prev) || prev != byPlayer.PlayerUID;
        lastCook[key] = byPlayer.PlayerUID;
        if (changed)
            TcmLog.Cat(__instance.Api, "coo", $"oven cook stamp: {__instance.Pos} -> {byPlayer.PlayerName}");

        int cx = (int)CooDomain.Knob(CooDomain.CxBaking, 1);
        for (int i = 0; i < __state.Length && i < inv.Count; i++)
        {
            string? code = __state[i];
            if (code == null) continue;
            if (code.StartsWith("dough") || code.Contains("partbaked")) continue; // not finished work
            if (code.Contains("-raw")) continue;                                  // an unbaked pie is not finished work either
            if (code.Contains("charred")) continue;                               // ruined, unsigned

            ItemStack? loaf = inv[i]?.Itemstack;
            if (loaf == null) continue;
            // FOOD ONLY, and it must be the guard rather than the code-path filter above. The
            // oven's inventory includes its FUEL slot, so without this the firewood got stamped
            // too, and since attributes are part of stack identity, attributed firewood stopped
            // merging with plain firewood: you could load one log at a time instead of six.
            // Regression found in play 2026-08-13, same day it shipped.
            //
            // PIES pass a different way (added 2026-08-13). A pie's nutrition is computed from its
            // CONTENTS via a GetNutritionProperties override, so the NutritionProps FIELD that
            // IsDirectlyEdible reads is null and pies were silently never signed. A baked pie is a
            // finished dish a player eats, so the baker owns it, same as bread. The satiety ruling
            // ("pies would be pushed too far") holds: BlockPie.GetNutritionHealthMul builds its own
            // array and never calls the BlockMeal base our satiety patch rides, and the patch now
            // guards against pies explicitly anyway.
            bool signable = Engine.FoodProvenance.IsDirectlyEdible(loaf.Collectible)
                || loaf.Block is BlockPie;
            if (!signable) continue;
            if (loaf.Attributes.HasAttribute(CooBonusPatches.CookTierAttr)) continue; // already signed
            CooBonusPatches.StampCooked(loaf, baker, cx);
            inv[i].MarkDirty();
        }
    }

    /// <summary>RULED 2026-07-21: baking XP fires on PICKUP of the finished good, one credit per
    /// loaf taken. Dough or a par-baked loaf taken back out is unfinished work (nothing); a
    /// charred pickup is the ruin (logged, nothing). Batches are batch-friendly by construction:
    /// each slot's take is its own context.</summary>
    public static void OvenTakePostfix(BlockEntity __instance, IPlayer byPlayer, string?[] __state)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        if (inv == null) return;

        for (int i = 0; i < __state.Length && i < inv.Count; i++)
        {
            string? before = __state[i];
            if (before == null || inv[i]?.Itemstack?.Collectible?.Code?.Path == before) continue; // nothing left this slot

            if (before.StartsWith("dough") || before.Contains("partbaked")) continue; // unfinished work back out
            if (before.Contains("charred"))
            {
                TcmLog.Cat(__instance.Api, "coo", $"charred pickup at {__instance.Pos} slot {i}: {before} — ruined, no practice");
                continue;
            }
            Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechBaking,
                HashCode.Combine("bake", __instance.Pos.X, __instance.Pos.Z, i, __instance.Api.World.ElapsedMilliseconds / 2000));
        }
    }

    /// <summary>Mark flour the quern ejected because its output slot could not take it. The
    /// expected output collectible comes from the input's own grinding properties, so this only
    /// ever looks at the exact item this grind produced.</summary>
    private static void MarkSpilledGrind(BlockEntity be, ItemStack? source)
    {
        if (be?.Api == null || source?.Collectible == null) return;
        var ground = source.Collectible.GrindingProps?.GroundStack?.ResolvedItemstack?.Collectible;
        if (ground == null) return;

        ICoreAPI api = be.Api;
        int collId = ground.Id;
        var centre = be.Pos.ToVec3d().Add(0.5, 0.5, 0.5);

        api.Event.RegisterCallback(_ =>
        {
            int marked = 0;
            foreach (var e in api.World.GetEntitiesAround(centre, 2f, 2f,
                         ent => ent is EntityItem ei
                                && ei.Itemstack?.Collectible?.Id == collId))
            {
                var stack = (e as EntityItem)?.Itemstack;
                if (stack == null) continue;
                // Carry is idempotent: it skips a stack that already holds the mark, so a second
                // grind cannot re-stamp the same ejected pile.
                Engine.FoodProvenance.Carry(new[] { source }, stack, api);
                marked++;
            }
            if (marked > 0)
                TcmLog.Cat(api, "far", $"quern spill at {be.Pos}: {marked} ejected stack(s) carried the grower's mark");
        }, 100);
    }

    private static IPlayer? CookAt(IWorldAccessor world, BlockPos? pos)
    {
        if (pos == null || !lastCook.TryGetValue(PosKey(pos), out string? uid) || uid == null) return null;
        return world.PlayerByUid(uid);
    }

    /// <summary>The Phase 2 levers (CooBonusPatches: fuel economy, char clock) read the same
    /// lastCook stamp this file writes — one attribution, shared.</summary>
    public static IPlayer? CookAtPublic(IWorldAccessor world, BlockPos? pos) => CookAt(world, pos);

    // ------------------------------------------------------------ smelt completion (pot + direct)

    public readonly record struct SmeltState(BlockPos? Pos, int InId, int InSize, int OutId, int OutSize, bool Cookable);

    /// <summary>Shared DoSmelt prefix. The provider the firepit passes is its INVENTORY, not the
    /// BE (smeltItems :58732 hands over InventorySmelting — the 0.3.136 null-pos bug), so the pos
    /// comes from InventorySmelting.pos (:106652), with the BE cast kept for providers that do
    /// pass a block entity. Both slots are captured: the normal meal lands in the OUTPUT slot
    /// (:142621) but the CooksInto path returns it in the INPUT slot (:142612).</summary>
    public static void SmeltPrefix(ISlotProvider cookingSlotsProvider, ItemSlot inputSlot, ItemSlot outputSlot, out SmeltState __state)
    {
        var props = inputSlot?.Itemstack?.Collectible?.CombustibleProps;
        bool cookable = props != null && (props.SmeltingType == EnumSmeltType.Cook || props.SmeltingType == EnumSmeltType.Bake);
        BlockPos? pos = (cookingSlotsProvider as BlockEntity)?.Pos ?? (cookingSlotsProvider as InventorySmelting)?.pos;
        __state = new SmeltState(pos,
            inputSlot?.Itemstack?.Collectible?.Id ?? -1, inputSlot?.Itemstack?.StackSize ?? 0,
            outputSlot?.Itemstack?.Collectible?.Id ?? -1, outputSlot?.Itemstack?.StackSize ?? 0, cookable);
    }

    /// <summary>Meal pot: the recipe path returns early on a null/burned/invalid match leaving the
    /// slots untouched, so a slot transform IS the success gate (ruled: burned banks nothing).</summary>
    public static void MealPotPostfix(IWorldAccessor world, ItemSlot inputSlot, ItemSlot outputSlot, SmeltState __state)
    {
        if (world?.Side != EnumAppSide.Server || !Changed(inputSlot, outputSlot, __state)) return;
        IPlayer? cook = CookAt(world, __state.Pos);
        if (cook == null)
        {
            TcmLog.Cat(world.Api, "coo", $"meal-pot completed at {__state.Pos?.ToString() ?? "unknown pos"} but no cook stamped; uncredited");
            return;
        }
        TcmLog.Cat(world.Api, "coo", $"meal-pot completed at {__state.Pos} -> {cook.PlayerName}");
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechMealPot,
            HashCode.Combine("mealpot", __state.Pos!.X, __state.Pos.Z, world.ElapsedMilliseconds / 30000));

        // Phase 2: the cook stamp (one stamp, three jobs) + the Axis 4 extra-serving proc.
        // The meal is the stack in whichever slot CHANGED: vanilla finishes a meal into the
        // OUTPUT slot (:142621) on the normal path but into the INPUT slot (:142612) on the
        // CooksInto path — and a blind output-first pick stamps whatever junk happened to sit
        // in output while the real pot goes unstamped (the 0.3.149 playtest miss).
        ItemStack? meal = null;
        if ((outputSlot?.Itemstack?.Collectible?.Id ?? -1) != __state.OutId
            || (outputSlot?.Itemstack?.StackSize ?? 0) != __state.OutSize) meal = outputSlot?.Itemstack;
        if (meal == null && ((inputSlot?.Itemstack?.Collectible?.Id ?? -1) != __state.InId
            || (inputSlot?.Itemstack?.StackSize ?? 0) != __state.InSize)) meal = inputSlot?.Itemstack;
        CooBonusPatches.StampCooked(meal, cook, (int)CooDomain.Knob(CooDomain.CxMealpot, 1));
        // Anchor the stamp to the firepit's position too: the pack converts fresh-cooked pots
        // into differently-coded stacks (attrs discarded), so the serve heals from this store.
        CooBonusPatches.StoreStampAt(__state.Pos, cook, (int)CooDomain.Knob(CooDomain.CxMealpot, 1));
        if (meal != null)
            TcmLog.Cat(world.Api, "coo", $"cook stamp -> {meal.Collectible?.Code?.Path} (tier {CooDomain.LevelOf(cook)}) + position store");
        double procT = CooDomain.BonusT(CooDomain.LevelOf(cook));
        if (meal != null && procT > 0
            && world.Rand.NextDouble() < procT * CooDomain.Knob(CooDomain.ServingProcGm, 0.25))
        {
            float servings = (float)meal.Attributes.GetDecimal("quantityServings", 0.0);
            if (servings >= 1)
            {
                meal.Attributes.SetFloat("quantityServings", servings + 1f);
                TcmLog.Cat(world.Api, "coo", $"exceptional batch at {__state.Pos}: {servings} -> {servings + 1} servings for {cook.PlayerName}");
                (cook as IServerPlayer)?.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                    Vintagestory.API.Config.Lang.Get("almanactcm:pot-stretched"), EnumChatType.Notification);
            }
        }
    }

    /// <summary>Direct-heat: only the kitchen class (Cook/Bake input) banks — an ore nugget or a
    /// fired ceramic riding some future collectible's base DoSmelt must never credit COO. The
    /// charred gate applies here too (ruled 2026-07-21): "charred" is NOT vanilla for meats —
    /// ExpandedFoods redirects open-flame meat to its own redmeat-charred, the deliberately
    /// inferior lazy product. Charring food over a bare flame is not cooking practice; the
    /// domain pays at the pot, the oven, and the griddle.</summary>
    public static void DirectHeatPostfix(IWorldAccessor world, ItemSlot inputSlot, ItemSlot outputSlot, SmeltState __state)
    {
        if (world?.Side != EnumAppSide.Server || !__state.Cookable || !Changed(inputSlot, outputSlot, __state)) return;

        string? outCode = outputSlot?.Itemstack?.Collectible?.Code?.Path ?? inputSlot?.Itemstack?.Collectible?.Code?.Path;
        if (outCode?.Contains("charred") == true)
        {
            TcmLog.Cat(world.Api, "coo", $"direct-heat at {__state.Pos?.ToString() ?? "?"} produced {outCode} — charred, no practice");
            return;
        }

        IPlayer? cook = CookAt(world, __state.Pos);
        if (cook == null)
        {
            TcmLog.Cat(world.Api, "coo", $"direct-heat completed at {__state.Pos?.ToString() ?? "unknown pos"} but no cook stamped; uncredited");
            return;
        }
        // The spammiest verb in the domain: one wide bucket so a stack of cooked roots is ONE context.
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechDirectHeat,
            HashCode.Combine("direct", __state.Pos!.X, __state.Pos.Z, world.ElapsedMilliseconds / 60000));
    }

    private static bool Changed(ItemSlot? inputSlot, ItemSlot? outputSlot, SmeltState s) =>
        (inputSlot?.Itemstack?.Collectible?.Id ?? -1) != s.InId || (inputSlot?.Itemstack?.StackSize ?? 0) != s.InSize
        || (outputSlot?.Itemstack?.Collectible?.Id ?? -1) != s.OutId || (outputSlot?.Itemstack?.StackSize ?? 0) != s.OutSize;

    // ------------------------------------------------------------ quern (the ruled 50/50 split)

    /// <summary>One grind completion: credit every player currently cranking (the BE's own
    /// playersGrinding dict) HALF a share in COO milling and HALF in FAR milling (RULED
    /// 2026-07-08 COO Q3). The automated quern credits nobody.</summary>
    /// <summary>Snapshot the grain before the grind eats it, AND lift any mark off the output slot
    /// so vanilla's merge sees plain flour against plain flour.
    ///
    /// The order matters and is the whole point. Vanilla creates the flour and merges it into the
    /// output slot inside grindInput, so marking the slot from a postfix alone guarantees the NEXT
    /// merge fails on mismatched attributes and ejects that flour unmarked. That broke the ordinary
    /// way a quern is used (a stack in, collected later) even when every grain came from one
    /// farmer. Found in play 2026-08-13.
    ///
    /// Flour is an INGREDIENT (no nutritionProps of its own, verified 1.22.5), so milling carries
    /// the grower's mark rather than handing it to the cook: the farmer keeps it to the oven door.
    /// See docs/design/food-provenance-chain.md.</summary>
    public static void QuernPrefix(BlockEntity __instance, out (ItemStack? input, Engine.FoodProvenance.PendingMerge merge) __state)
    {
        __state = (null, default);
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        if (inv == null) return;

        // Quern inventory is [0] input, [1] output. Clone: the real stack is consumed below us.
        ItemStack? input = inv.Count > 0 ? inv[0]?.Itemstack?.Clone() : null;
        var merge = Engine.FoodProvenance.TakeForMerge(inv.Count > 1 ? inv[1]?.Itemstack : null);
        __state = (input, merge);
    }

    public static void QuernPostfix(BlockEntity __instance, (ItemStack? input, Engine.FoodProvenance.PendingMerge merge) __state)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;

        {
            var inv = (__instance as BlockEntityContainer)?.Inventory;
            ItemStack? ground = inv != null && inv.Count > 1 ? inv[1]?.Itemstack : null;
            Engine.FoodProvenance.RestoreAfterMerge(__state.merge, __state.input, ground, __instance.Api);

            // THE SPILL PATH. With the output slot deliberately blocked (a scrap block in the
            // slot so everything ejects into hoppers, which is how an automated mill is actually
            // built) vanilla never puts the flour in the slot at all, so the restore above cannot
            // reach it. Scan the ejected items instead. Same shape as MET's completion stamp,
            // and the same accepted tradeoff: a tight radius and a short window, so unmarked
            // flour a player happened to drop beside the quern in that instant could be caught.
            MarkSpilledGrind(__instance, __state.input);
        }

        if (Traverse.Create(__instance).Field("automated").GetValue<bool>()) return;
        if (Traverse.Create(__instance).Field("playersGrinding").GetValue() is not Dictionary<string, long> grinding
            || grinding.Count == 0) return;

        // Per-grind bucket (a hand grind takes several seconds): each completed grind is its own
        // context and the K cap does the daily throttling. The 0.3.136 30s bucket collapsed a
        // 5-grain session into one credit, which under-read a per-action verb.
        int ctx = HashCode.Combine("quern", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 4000);
        foreach (string uid in grinding.Keys)
        {
            IPlayer? player = __instance.Api.World.PlayerByUid(uid);
            if (player == null) continue;
            Core?.Ledger?.Log(player, CooDomain.Code, CooDomain.TechMilling, ctx, 0.5);
            Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechMilling, ctx, 0.5);
        }
    }

    // ------------------------------------------------------------ juicing

    /// <summary>The press: ONE patch, routed by what is in the mash (RULED 2026-07-28/30,
    /// bee-domain-design.md; the mash decides the domain, not the machine).
    ///
    ///  - Honeycomb: the keeper rendering their own harvest. BEE #4 when the BEE domain is
    ///    live, else the FAR beekeeping fallback (which covers both vanilla acts).
    ///  - Anything else juiceable: undetermined at the press (drunk, cooked, or fermented),
    ///    so COO 50 / BRE 50, a true halving, the pickling shape. This supersedes the
    ///    2026-07-08 "juicing stays COO 100" ruling.</summary>
    public static void JuicePostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        int cx = HashCode.Combine("juice", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 20000);

        string mash = (__instance as Vintagestory.GameContent.BlockEntityFruitPress)?
            .MashSlot?.Itemstack?.Collectible?.Code?.Path ?? "";
        if (mash.Contains("honeycomb"))
        {
            if (BeePatches.Active)
                Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechRendering, cx);
            else
                Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechBeekeeping, cx);
            return;
        }

        Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechJuicing, cx, 0.5);
        Core?.Ledger?.Log(byPlayer, BreDomain.Code, BreDomain.TechFermenting, cx, 0.5);
    }

    // ------------------------------------------------------------ prep-table (seafarer)

    public static void PrepPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechPrep,
            HashCode.Combine("prep", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 10000));
    }

    // ------------------------------------------------------------ griddle (seafarer)

    /// <summary>Laying food on the griddle stamps the cook; the hearth tick finishes unattended.</summary>
    public static void GriddleLoadPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        lastCook[PosKey(__instance.Pos)] = byPlayer.PlayerUID;
    }

    /// <summary>Per-SLOT context (playtest 2026-07-21: a four-slot griddle finishes its slots
    /// near-simultaneously, and a shared pos bucket collapsed the batch to one credit — batches
    /// must fit through; the K cap is the real governor).
    ///
    /// Also the provenance hop, added 2026-08-13. Seafarer's CompleteCooking builds its output as
    /// `new ItemStack(val, recipe.Output.Quantity)` and hand-copies ONLY the "contents" tree
    /// (decompiled from 0.5.15), so every other attribute is dropped and a griddled Grandmaster's
    /// fish landed anonymous. The method hands us the inputStack directly, which makes this the
    /// cleanest seam of the set: no snapshot, no merge dance, the source is a parameter.</summary>
    public static void GriddleCompletePostfix(BlockEntity __instance, int slotIndex, ItemStack inputStack)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        IPlayer? cook = CookAt(__instance.Api.World, __instance.Pos);

        var inv = (__instance as BlockEntityContainer)?.Inventory;
        ItemStack? cooked = inv != null && slotIndex >= 0 && slotIndex < inv.Count ? inv[slotIndex]?.Itemstack : null;
        if (cooked != null && !cooked.Attributes.HasAttribute(CooBonusPatches.CookTierAttr))
        {
            // Griddled output is eaten as it comes off, so it belongs to the cook. An unattended
            // finish with no recorded cook still carries the grower forward rather than losing it.
            if (cook != null && Engine.FoodProvenance.IsDirectlyEdible(cooked.Collectible))
                CooBonusPatches.StampCooked(cooked, cook, (int)CooDomain.Knob(CooDomain.CxGriddling, 2));
            else
                Engine.FoodProvenance.Carry(new[] { inputStack }, cooked, __instance.Api);
            inv?[slotIndex]?.MarkDirty();
        }

        if (cook == null) return;
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechGriddling,
            HashCode.Combine("griddle", __instance.Pos.X, __instance.Pos.Z, slotIndex, __instance.Api.World.ElapsedMilliseconds / 10000));
    }

    // ------------------------------------------------------------ saucepan simmering (ACA)

    /// <summary>Snapshot the firepit's cooking slots before DoSmelt consumes them (it nulls every
    /// slot on success). Clones, for the same reason the quern clones.</summary>
    public static void SimmerPrefix(IWorldAccessor world, ISlotProvider cookingSlotsProvider, out ItemStack?[] __state)
    {
        __state = System.Array.Empty<ItemStack?>();
        if (world?.Side != EnumAppSide.Server || cookingSlotsProvider?.Slots == null) return;
        var slots = cookingSlotsProvider.Slots;
        var snapshot = new ItemStack?[slots.Length];
        for (int i = 0; i < slots.Length; i++) snapshot[i] = slots[i]?.Itemstack?.Clone();
        __state = snapshot;
    }

    /// <summary>The simmer result landed in the firepit's output slot: sign it or carry onto it.
    ///
    /// Three outcomes, decided by what the output IS rather than by recipe list:
    ///   • a LIQUID (syrup, clarified butter): vanilla pours it into the pan and moves the PAN to
    ///     the output slot, so the slot holds a container, not food. The liquid ruling excludes
    ///     portions anyway (pooling beats provenance), so this skips.
    ///   • directly edible food (breaded nuggets, cooked pasta): the cook's work, stamped, and the
    ///     stamp displaces any grower's mark.
    ///   • an ingredient (something still bound for another dish): the growers' marks carry.
    /// The cook is the firepit's cook of record (lastCook via the pit's own GUI-open stamp), read
    /// off the ISlotProvider the same way the meal-pot path reads it.</summary>
    public static void SimmerPostfix(IWorldAccessor world, ISlotProvider cookingSlotsProvider, ItemSlot outputSlot, ItemStack?[] __state)
    {
        if (world?.Side != EnumAppSide.Server || __state.Length == 0) return;
        ItemStack? made = outputSlot?.Itemstack;
        if (made?.Collectible == null) return;
        if (made.Collectible is Vintagestory.GameContent.BlockLiquidContainerBase) return; // the pan came through: liquid branch
        if (Engine.FoodProvenance.IsLiquidPortion(made)) return;
        if (made.Attributes.HasAttribute(CooBonusPatches.CookTierAttr)) return; // already signed

        BlockPos? pos = (cookingSlotsProvider as BlockEntity)?.Pos ?? (cookingSlotsProvider as InventorySmelting)?.pos;
        IPlayer? cook = CookAt(world, pos);

        // Same BlockMeal extension as the mixing bowl: a meal-block output is the cook's dish
        // even though its NutritionProps field is null (content-derived nutrition).
        if (cook != null && (Engine.FoodProvenance.IsDirectlyEdible(made.Collectible)
            || made.Block is BlockMeal))
            CooBonusPatches.StampCooked(made, cook, (int)CooDomain.Knob(CooDomain.CxSimmering, 2));
        else
            Engine.FoodProvenance.Carry(__state, made, world.Api);
        outputSlot!.MarkDirty();
    }

    // ------------------------------------------------------------ rack drying (seafarer frame + ACA meat hooks)

    /// <summary>Capture the stack in the clicked rack slot BEFORE the take.</summary>
    public static void DryTakePrefix(BlockEntity __instance, BlockSelection blockSel, out ItemStack? __state)
    {
        __state = null;
        int idx = blockSel?.SelectionBoxIndex ?? -1;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        if (inv != null && idx >= 0 && idx < inv.Count) __state = inv[idx]?.Itemstack;
    }

    /// <summary>Credit only a TRANSITIONED retrieval: a stack still carrying a pending dry/cure
    /// transition is the fresh item taken straight back, which is not preservation practice.
    /// One shared pair for both racks (the casting-merge precedent: one verb, two BEs).</summary>
    public static void DryTakePostfix(BlockEntity __instance, IPlayer byPlayer, bool __result, ItemStack? __state)
    {
        if (!__result || byPlayer == null || __state == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        var props = __state.Collectible?.GetTransitionableProperties(__instance.Api.World, __state, null);
        if (props != null)
            foreach (var p in props)
                if (p?.Type == EnumTransitionType.Dry || p?.Type == EnumTransitionType.Cure) return; // still fresh
        Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechDrying,
            HashCode.Combine("dry", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 30000));
    }

    // ------------------------------------------------------------ salt evaporation (seafarer)

    public static void SaltHarvestPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechSalting,
            HashCode.Combine("salt", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 30000));
    }

    // ------------------------------------------------------------ bowl mixing (ACA)

    /// <summary>Snapshot the ingredients before mixInput consumes them, and lift any mark off the
    /// output slot so vanilla's merge compares plain against plain.
    ///
    /// The quern's bug, waiting to happen a second time. ACA's mixInput (decompiled from
    /// 2.0.0-dev.21) builds the output and then does
    /// `OutputSlot.Itemstack.StackSize += val.StackSize` after a GetMergableQuantity check, all
    /// inside the method body. Mark the slot from a postfix alone and the NEXT mix fails that
    /// check on mismatched attributes and takes the third branch, which SpawnItemEntity's the
    /// result onto the floor. Same shape, same fix: take the mark off before, put it back after.
    ///
    /// ACA's inventory is [0] the container, [1] the output, and the ingredients are a separate
    /// IngredSlots view, so the snapshot reads the whole inventory and lets Carry pick the
    /// highest-ranked per domain rather than guessing at slot indices.</summary>
    public static void MixingPrefix(BlockEntity __instance, out (ItemStack?[] inputs, Engine.FoodProvenance.PendingMerge merge) __state)
    {
        __state = (System.Array.Empty<ItemStack?>(), default);
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        if (inv == null) return;

        var snapshot = new List<ItemStack?>(inv.Count);
        for (int i = 0; i < inv.Count; i++)
        {
            if (i == MixingOutputSlot) continue;      // the destination is not an ingredient
            snapshot.Add(inv[i]?.Itemstack?.Clone()); // clone: mixInput consumes the originals
        }
        var merge = Engine.FoodProvenance.TakeForMerge(inv.Count > MixingOutputSlot ? inv[MixingOutputSlot]?.Itemstack : null);
        __state = (snapshot.ToArray(), merge);
    }

    /// <summary>ACA's mixing bowl inventory: [0] container, [1] output, ingredients beyond.</summary>
    private const int MixingOutputSlot = 1;

    /// <summary>One mix completion: carry the provenance onto what came out, then credit every
    /// cranking player (the BE's own playersMixing dict, the quern shape). The mechanized bowl
    /// credits nobody (automation stays vanilla; the ENG co-grant waits for the ENG domain).
    ///
    /// The bowl is the case that killed the heat test. Jeffrey: "what about the mixing bowl, this
    /// needs to be included as well since there is zero heat applied and can make salads." So the
    /// split here is the ruled property test, not a temperature: a salad is directly edible and
    /// belongs to the COOK, while dough is an ingredient and keeps carrying the grower's mark to
    /// whoever bakes it.</summary>
    public static void MixingPostfix(BlockEntity __instance, (ItemStack?[] inputs, Engine.FoodProvenance.PendingMerge merge) __state)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;

        var inv = (__instance as BlockEntityContainer)?.Inventory;
        ItemStack? mixed = inv != null && inv.Count > MixingOutputSlot ? inv[MixingOutputSlot]?.Itemstack : null;
        Engine.FoodProvenance.RestoreAfterMerge(__state.merge, null, mixed, __instance.Api);

        bool automated = Traverse.Create(__instance).Field("automated").GetValue<bool>();
        var mixing = Traverse.Create(__instance).Field("playersMixing").GetValue() as Dictionary<string, long>;

        // The cook, if a person actually turned the crank. An automated bowl has no cook, so its
        // output carries the growers forward and nobody's rank touches it.
        IPlayer? cook = null;
        if (!automated && mixing != null)
        {
            foreach (string uid in mixing.Keys)
            {
                IPlayer? p = __instance.Api.World.PlayerByUid(uid);
                if (p != null && (cook == null || CooDomain.LevelOf(p) > CooDomain.LevelOf(cook))) cook = p;
            }
        }

        if (mixed != null && !mixed.Attributes.HasAttribute(CooBonusPatches.CookTierAttr))
        {
            // BlockMeal alongside IsDirectlyEdible, and the bowl is exactly why: a salad comes out
            // as a MEAL block, and every meal block's NutritionProps FIELD is null because its
            // nutrition is computed from contents. The field test alone would have carried the
            // grower onto salads instead of stamping the cook, on the very station Jeffrey named
            // when he killed the heat test. A meal is a finished dish; the cook signs it.
            if (cook != null && (Engine.FoodProvenance.IsDirectlyEdible(mixed.Collectible)
                || mixed.Block is BlockMeal))
                CooBonusPatches.StampCooked(mixed, cook, (int)CooDomain.Knob(CooDomain.CxMixing, 3));
            else
                Engine.FoodProvenance.Carry(__state.inputs, mixed, __instance.Api);
            inv?[MixingOutputSlot]?.MarkDirty();
        }

        if (automated || mixing == null || mixing.Count == 0) return;
        int ctx = HashCode.Combine("mix", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 4000);
        foreach (string uid in mixing.Keys)
        {
            IPlayer? player = __instance.Api.World.PlayerByUid(uid);
            if (player != null) Core?.Ledger?.Log(player, CooDomain.Code, CooDomain.TechMixing, ctx);
        }
    }
}

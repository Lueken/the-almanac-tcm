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
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityQuern", "grindInput", nameof(QuernPostfix), "COO+FAR quern milling");

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
            HookDeclared(api, harmony, "ACulinaryArtillery.BlockEntityMixingBowl", "mixInput", nameof(MixingPostfix), "COO bowl mixing");
            // Meat hooks: the second rack-drying BE (one verb, two racks — the casting-merge
            // precedent). Same TryTake signature as the seafarer frame, one shared pair.
            HookPairDeclared(api, harmony, "ACulinaryArtillery.BlockEntityMeatHooks", "TryTake",
                nameof(DryTakePrefix), nameof(DryTakePostfix), "COO rack drying (meat hooks)");
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

    private static void HookPairDeclared(ICoreAPI api, Harmony harmony, string typeName, string method, string prefix, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
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
    public static void OvenTakePrefix(BlockEntity __instance, out string?[] __state)
    {
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        __state = new string?[inv?.Count ?? 0];
        for (int i = 0; i < __state.Length; i++)
            __state[i] = inv![i]?.Itemstack?.Collectible?.Code?.Path;
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
    public static void QuernPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
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
    /// must fit through; the K cap is the real governor).</summary>
    public static void GriddleCompletePostfix(BlockEntity __instance, int slotIndex)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        IPlayer? cook = CookAt(__instance.Api.World, __instance.Pos);
        if (cook == null) return;
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechGriddling,
            HashCode.Combine("griddle", __instance.Pos.X, __instance.Pos.Z, slotIndex, __instance.Api.World.ElapsedMilliseconds / 10000));
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

    /// <summary>One mix completion: credit every cranking player (the BE's own playersMixing
    /// dict, the quern shape). The mechanized bowl credits nobody (automation stays vanilla;
    /// the ENG co-grant waits for the ENG domain).</summary>
    public static void MixingPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (Traverse.Create(__instance).Field("automated").GetValue<bool>()) return;
        if (Traverse.Create(__instance).Field("playersMixing").GetValue() is not Dictionary<string, long> mixing
            || mixing.Count == 0) return;

        int ctx = HashCode.Combine("mix", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 4000);
        foreach (string uid in mixing.Keys)
        {
            IPlayer? player = __instance.Api.World.PlayerByUid(uid);
            if (player != null) Core?.Ledger?.Log(player, CooDomain.Code, CooDomain.TechMixing, ctx);
        }
    }
}

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
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
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
        // Firepit GUI open: the openable-container packet handler is declared on the vsapi base
        // (BlockEntityFirepit does not override it, verified); the postfix guards to firepits so
        // chests and other containers never grow the map.
        HookFirst(api, harmony,
            new[] { "Vintagestory.API.Common.BlockEntityOpenableContainer", "Vintagestory.GameContent.BlockEntityOpenableContainer" },
            "OnReceivedClientPacket", nameof(FirepitOpenPostfix), "COO firepit cook-stamp");
        // Oven load/take interact (byPlayer in scope, verified :144284).
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityOven", "OnInteract", nameof(OvenInteractPostfix), "COO oven cook-stamp");

        // --- the completion sinks ---------------------------------------------------------
        HookPair(api, harmony, "Vintagestory.GameContent.BlockCookingContainer", "DoSmelt",
            nameof(SmeltPrefix), nameof(MealPotPostfix), "COO meal-pot");
        HookPair(api, harmony, "Vintagestory.API.Common.CollectibleObject", "DoSmelt",
            nameof(SmeltPrefix), nameof(DirectHeatPostfix), "COO direct-heat");
        if (api.ModLoader.IsModEnabled("aculinaryartillery"))
            HookPair(api, harmony, "ACulinaryArtillery.ItemExpandedRawFood", "DoSmelt",
                nameof(SmeltPrefix), nameof(DirectHeatPostfix), "COO direct-heat (ACA)");
        HookPair(api, harmony, "Vintagestory.GameContent.BlockEntityOven", "IncrementallyBake",
            nameof(BakePrefix), nameof(BakePostfix), "COO oven baking");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityQuern", "grindInput", nameof(QuernPostfix), "COO+FAR quern milling");

        // --- player-attributed verbs (1a, unchanged) ---------------------------------------
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFruitPress", "OnBlockInteractStop", nameof(JuicePostfix), "COO juicing");
        if (api.ModLoader.IsModEnabled("seafarer"))
            Hook(api, harmony, "Seafarer.BlockEntityPrepTable", "OnInteract", nameof(PrepPostfix), "COO prep-table");
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

    private static void HookFirst(ICoreAPI api, Harmony harmony, string[] typeNames, string method, string postfix, string label)
    {
        foreach (string tn in typeNames)
        {
            var t = AccessTools.TypeByName(tn);
            var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
            if (m == null) continue;
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(CooPatches), postfix)));
            TcmLog.Info(api, $"{label} hooked ({tn}.{method})");
            return;
        }
        TcmLog.Warn(api, $"{label} seam not found ({method} on any candidate); unattended firepit cooking is uncredited this build");
    }

    // ------------------------------------------------------------ the cook stamp

    /// <summary>Any GUI packet from a player to a FIREPIT marks them the pit's cook. Guard first:
    /// this method is shared by every openable container and must stay cheap for chests.</summary>
    public static void FirepitOpenPostfix(BlockEntity __instance, IPlayer player)
    {
        if (__instance is not BlockEntityFirepit || player == null || __instance.Api?.Side != EnumAppSide.Server) return;
        lastCook[PosKey(__instance.Pos)] = player.PlayerUID;
    }

    public static void OvenInteractPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        lastCook[PosKey(__instance.Pos)] = byPlayer.PlayerUID;
    }

    private static IPlayer? CookAt(IWorldAccessor world, BlockPos? pos)
    {
        if (pos == null || !lastCook.TryGetValue(PosKey(pos), out string? uid) || uid == null) return null;
        return world.PlayerByUid(uid);
    }

    // ------------------------------------------------------------ smelt completion (pot + direct)

    public readonly record struct SmeltState(BlockPos? Pos, int OutId, int OutSize, bool Cookable);

    /// <summary>Shared DoSmelt prefix: capture the provider BE's pos, the output slot's state, and
    /// whether the INPUT is kitchen-class (SmeltingType Cook/Bake) before the smelt consumes it.</summary>
    public static void SmeltPrefix(ISlotProvider cookingSlotsProvider, ItemSlot inputSlot, ItemSlot outputSlot, out SmeltState __state)
    {
        var props = inputSlot?.Itemstack?.Collectible?.CombustibleProps;
        bool cookable = props != null && (props.SmeltingType == EnumSmeltType.Cook || props.SmeltingType == EnumSmeltType.Bake);
        __state = new SmeltState((cookingSlotsProvider as BlockEntity)?.Pos,
            outputSlot?.Itemstack?.Collectible?.Id ?? -1, outputSlot?.Itemstack?.StackSize ?? 0, cookable);
    }

    /// <summary>Meal pot: the recipe path returns early on a null/burned/invalid match leaving the
    /// slots untouched, so an output transform IS the success gate (ruled: burned banks nothing).</summary>
    public static void MealPotPostfix(IWorldAccessor world, ItemSlot outputSlot, SmeltState __state)
    {
        if (world?.Side != EnumAppSide.Server || !Changed(outputSlot, __state)) return;
        IPlayer? cook = CookAt(world, __state.Pos);
        if (cook == null) return;
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechMealPot,
            HashCode.Combine("mealpot", __state.Pos!.X, __state.Pos.Z, world.ElapsedMilliseconds / 30000));
    }

    /// <summary>Direct-heat: only the kitchen class (Cook/Bake input) banks — an ore nugget or a
    /// fired ceramic riding some future collectible's base DoSmelt must never credit COO.</summary>
    public static void DirectHeatPostfix(IWorldAccessor world, ItemSlot outputSlot, SmeltState __state)
    {
        if (world?.Side != EnumAppSide.Server || !__state.Cookable || !Changed(outputSlot, __state)) return;
        IPlayer? cook = CookAt(world, __state.Pos);
        if (cook == null) return;
        // The spammiest verb in the domain: one wide bucket so "charred meat x40" is ONE context.
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechDirectHeat,
            HashCode.Combine("direct", __state.Pos!.X, __state.Pos.Z, world.ElapsedMilliseconds / 60000));
    }

    private static bool Changed(ItemSlot? outputSlot, SmeltState s) =>
        (outputSlot?.Itemstack?.Collectible?.Id ?? -1) != s.OutId || (outputSlot?.Itemstack?.StackSize ?? 0) != s.OutSize;

    // ------------------------------------------------------------ oven baking

    public readonly record struct BakeState(int CollId);

    /// <summary>IncrementallyBake runs per-tick; the bake only "happens" on the tick where the
    /// slot's stack converts (dough -> partbaked -> baked). Capture the slot's collectible.</summary>
    public static void BakePrefix(BlockEntity __instance, int slotIndex, out BakeState __state)
    {
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        __state = new BakeState(inv != null && slotIndex < inv.Count ? inv[slotIndex]?.Itemstack?.Collectible?.Id ?? -1 : -1);
    }

    public static void BakePostfix(BlockEntity __instance, int slotIndex, BakeState __state)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        int now = inv != null && slotIndex < inv.Count ? inv[slotIndex]?.Itemstack?.Collectible?.Id ?? -1 : -1;
        if (now == __state.CollId) return; // still baking, no transform this tick

        IPlayer? cook = CookAt(__instance.Api.World, __instance.Pos);
        if (cook == null) return;
        // Minute bucket: a multi-stage bake (partbaked then baked) collapses to one context.
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechBaking,
            HashCode.Combine("bake", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 60000));
    }

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

        int ctx = HashCode.Combine("quern", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 30000);
        foreach (string uid in grinding.Keys)
        {
            IPlayer? player = __instance.Api.World.PlayerByUid(uid);
            if (player == null) continue;
            Core?.Ledger?.Log(player, CooDomain.Code, CooDomain.TechMilling, ctx, 0.5);
            Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechMilling, ctx, 0.5);
        }
    }

    // ------------------------------------------------------------ juicing

    public static void JuicePostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechJuicing,
            HashCode.Combine("juice", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 20000));
    }

    // ------------------------------------------------------------ prep-table (seafarer)

    public static void PrepPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(byPlayer, CooDomain.Code, CooDomain.TechPrep,
            HashCode.Combine("prep", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 10000));
    }
}

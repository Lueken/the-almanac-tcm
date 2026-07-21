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
        // The physical right-click on the firepit block (BlockFirepit.OnBlockInteractStart,
        // declared :69496) — opening the pit to load it IS the cook's touch. (The 0.3.136 attempt
        // used the openable-container packet handler, which turned out to be declared only on
        // BlockEntity itself, so that hook warn-skipped at boot and nothing ever stamped.)
        Hook(api, harmony, "Vintagestory.GameContent.BlockFirepit", "OnBlockInteractStart", nameof(FirepitInteractPostfix), "COO firepit cook-stamp");
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

    // ------------------------------------------------------------ the cook stamp

    /// <summary>Right-clicking a firepit (opening it to load, adding fuel) marks the player as
    /// the pit's cook. Keyed by the firepit BE's position — the same pos InventorySmelting
    /// carries into DoSmelt, so the completion lookup matches.</summary>
    public static void FirepitInteractPostfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (byPlayer == null || blockSel == null || world?.Side != EnumAppSide.Server) return;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityFirepit be) return;
        lastCook[PosKey(be.Pos)] = byPlayer.PlayerUID;
        TcmLog.Cat(world.Api, "coo", $"firepit cook stamp: {be.Pos} -> {byPlayer.PlayerName}");
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
    }

    /// <summary>Direct-heat: only the kitchen class (Cook/Bake input) banks — an ore nugget or a
    /// fired ceramic riding some future collectible's base DoSmelt must never credit COO.</summary>
    public static void DirectHeatPostfix(IWorldAccessor world, ItemSlot inputSlot, ItemSlot outputSlot, SmeltState __state)
    {
        if (world?.Side != EnumAppSide.Server || !__state.Cookable || !Changed(inputSlot, outputSlot, __state)) return;
        IPlayer? cook = CookAt(world, __state.Pos);
        if (cook == null)
        {
            TcmLog.Cat(world.Api, "coo", $"direct-heat completed at {__state.Pos?.ToString() ?? "unknown pos"} but no cook stamped; uncredited");
            return;
        }
        // The spammiest verb in the domain: one wide bucket so "charred meat x40" is ONE context.
        Core?.Ledger?.Log(cook, CooDomain.Code, CooDomain.TechDirectHeat,
            HashCode.Combine("direct", __state.Pos!.X, __state.Pos.Z, world.ElapsedMilliseconds / 60000));
    }

    private static bool Changed(ItemSlot? inputSlot, ItemSlot? outputSlot, SmeltState s) =>
        (inputSlot?.Itemstack?.Collectible?.Id ?? -1) != s.InId || (inputSlot?.Itemstack?.StackSize ?? 0) != s.InSize
        || (outputSlot?.Itemstack?.Collectible?.Id ?? -1) != s.OutId || (outputSlot?.Itemstack?.StackSize ?? 0) != s.OutSize;

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
        var nowStack = inv != null && slotIndex < inv.Count ? inv[slotIndex]?.Itemstack : null;
        if ((nowStack?.Collectible?.Id ?? -1) == __state.CollId) return; // still baking, no transform this tick

        // The ruin gate (ruled: burned output grants nothing). The bake ladder is raw ->
        // partbaked -> perfect -> charred (:82013); a transform INTO the charred stage is an
        // overbake, the oven's failure state — the opposite of practice. Charred here is not the
        // firepit's "charred meat" (which IS the successful open-flame cook product).
        if (nowStack?.Collectible?.Code?.Path?.Contains("charred") == true)
        {
            TcmLog.Cat(__instance.Api, "coo", $"oven overbake at {__instance.Pos}: {nowStack.Collectible.Code.Path} — ruined, no practice");
            return;
        }

        IPlayer? cook = CookAt(__instance.Api.World, __instance.Pos);
        if (cook == null) return;
        // Minute bucket: a multi-stage bake (partbaked then perfect) collapses to one context.
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

using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// COO Phase 1a hooks (rank-bonus-design §COO, ruled 2026-07-09; technique-maps §COO). The
/// player-attributed cooking verbs — the ones where the cook is in scope at the practice moment.
///
/// Live this build:
///   juicing (BlockEntityFruitPress interact — fully player-attributed, no owner stamp needed),
///   prep-table assembly (seafarer BlockEntityPrepTable.OnInteract, conditional).
///
/// Deferred to Phase 1b — the UNATTENDED-completion sinks. Meal-pot (BlockCookingContainer.DoSmelt),
/// direct-heat (CollectibleObject.DoSmelt), oven baking (BlockEntityOven.IncrementallyBake),
/// griddling, bowl mixing, rack drying and salt evaporation all fire with NO player in scope. They
/// share ONE mechanism: stamp the loading player onto the vessel/firepit at interaction, credit at
/// the completion event. That owner-stamp sink is built once, carefully (and carries the ruled
/// COO 50 / FAR 50 quern split), rather than bolted on per verb as a fragile nearest-player guess.
/// Every seam is resolved by name and warns-and-skips on a miss (the 0.3.85 isolation lesson).
/// </summary>
public static class CooPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Juicing — vanilla fruit press, fully player-attributed at the interact stop.
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFruitPress", "OnBlockInteractStop", nameof(JuicePostfix), "COO juicing");

        // Prep-table cold assembly — seafarer, player-attributed.
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

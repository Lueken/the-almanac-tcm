using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// MIN hooks into OPTIONAL mods: ImmersiveMining (the stamina axis — Axis 2 + the
/// Axis 1 penalty end + the Deep-Delver endurance leg, all one seam) and StoneQuarry
/// (the quarrying practice verb). Every target type is resolved by name at runtime, so
/// an install without these mods loads cleanly and the axis is simply inert
/// (graceful-degradation law — the same posture as MetConditionalPatches).
/// </summary>
public static class MinConditionalPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

    // OnDurHit → TryConsume is a 1:1 synchronous call on the server thread, and TryConsume
    // is internal-static called from nowhere else, so a thread-static factor set in the
    // OnDurHit prefix is live for exactly its TryConsume. The prefix RESETS this state at its
    // top on every call, so no finalizer is needed to clear it — and NOT using a finalizer is
    // load-bearing: a Harmony finalizer rewraps the whole method's exception handling and was
    // the cause of the 0.3.43 regression where every tool (knife included) stopped draining.
    [ThreadStatic] private static double imPendingFactor;
    [ThreadStatic] private static bool imScaled;
    [ThreadStatic] private static IPlayer? sqQuarrier;

    /// <summary>Per-tool rank→stamina-factor, populated by each domain that meters an IM tool
    /// (MIN→Pickaxe, WOO→Axe). One shared IM hook serves all of them, gated by this map so the
    /// knife/shovel and any unmapped tool pass through untouched.</summary>
    public static readonly Dictionary<EnumTool, System.Func<IServerPlayer, double>> ToolFactor = new();

    /// <summary>Kill-switch for the IM stamina hook (whole axis is IM-conditional anyway).</summary>
    public static bool EnableStaminaAxis = true;

    public static void PatchAllPresent(ICoreAPI api, Harmony harmony)
    {
        if (EnableStaminaAxis) PatchImmersiveMiningStamina(api, harmony);
        else TcmLog.Info(api, "MIN stamina axis disabled");
        PatchStoneQuarry(api, harmony);
    }

    // ---------------------------------------------- ImmersiveMining stamina (Axes 2/1/6)

    /// <summary>Looks up the hit tool in the ToolFactor map (Pickaxe→MIN, Axe→WOO, …) and sets
    /// the rank factor for the TryConsume OnDurHit is about to make. Unmapped tools pass through.
    /// Reset at the top so no finalizer is needed (the finalizer broke every tool in 0.3.43).</summary>
    public static class DurHitGatePatch
    {
        public static void Prefix(IServerPlayer fromPlayer, object pkt)
        {
            imPendingFactor = 1.0;
            imScaled = false;
            if (fromPlayer == null || pkt == null) return;

            EnumTool tool;
            try { tool = Traverse.Create(pkt).Property<EnumTool>("Tool").Value; }
            catch { return; }
            if (!ToolFactor.TryGetValue(tool, out var factorOf)) return;

            imScaled = true;
            imPendingFactor = factorOf(fromPlayer);
        }
    }

    /// <summary>Scales the flat per-hit stamina IM hands Vigor for a metered tool — a master
    /// swings for less, an Untrained one for more. Never touches mining speed or durability.
    /// Silent per-hit; unmapped tools pass through untouched.</summary>
    public static class VigorConsumePatch
    {
        public static void Prefix(ref float amount)
        {
            if (imScaled && imPendingFactor != 1.0) amount *= (float)imPendingFactor;
        }
    }

    private static void PatchImmersiveMiningStamina(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("immersivemining")) return;

        var onDurHit = AccessTools.Method(AccessTools.TypeByName("ImmersiveMining.ImmersiveMiningServer"), "OnDurHit");
        var tryConsume = AccessTools.Method(AccessTools.TypeByName("ImmersiveMining.VigorHook"), "TryConsume");
        if (onDurHit == null || tryConsume == null)
        {
            TcmLog.Warn(api, "immersivemining present but OnDurHit/VigorHook.TryConsume not found; tool-stamina axes inactive");
            return;
        }

        harmony.Patch(onDurHit,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(DurHitGatePatch), "Prefix")));
        harmony.Patch(tryConsume,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(VigorConsumePatch), "Prefix")));
        TcmLog.Info(api, "tool-stamina axis hooked to ImmersiveMining (per-tool via ToolFactor)");
    }

    // ------------------------------------------------------- StoneQuarry quarrying verb

    /// <summary>The plug strike carries the player only at the block-interact seam, so we
    /// stash it there for the TryHitPlug that the interact is about to call.</summary>
    public static class QuarryInteractPatch
    {
        public static void Prefix(IPlayer byPlayer) => sqQuarrier = byPlayer;
        public static void Finalizer() => sqQuarrier = null;
    }

    /// <summary>Credits a quarrying strike that advanced the work. contextHash is the plug
    /// network anchor, so one quarry banks a bounded amount however many strikes it takes.</summary>
    public static class QuarryHitPatch
    {
        public static void Postfix(Vintagestory.API.Common.BlockEntity __instance, bool __result)
        {
            if (!__result || sqQuarrier == null) return;
            if (__instance?.Api?.Side != EnumAppSide.Server) return;

            Core?.Ledger?.Log(sqQuarrier, MinDomain.Code, MinDomain.TechQuarrying, __instance.Pos.GetHashCode());
        }
    }

    private static void PatchStoneQuarry(ICoreAPI api, Harmony harmony)
    {
        // The live pack runs the "Repacked" fork (modid stonequarryrepckfipil); fall back to
        // the original modid so either build wires the verb.
        if (!api.ModLoader.IsModEnabled("stonequarryrepckfipil") && !api.ModLoader.IsModEnabled("stonequarry")) return;

        var interact = AccessTools.Method(AccessTools.TypeByName("StoneQuarry.BlockPlugAndFeather"), "OnBlockInteractStart");
        var hitPlug = AccessTools.Method(AccessTools.TypeByName("StoneQuarry.BEPlugAndFeather"), "TryHitPlug");
        if (interact == null || hitPlug == null)
        {
            TcmLog.Warn(api, "stonequarry present but PlugAndFeather seams not found; MIN quarrying verb inactive");
            return;
        }

        harmony.Patch(interact,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(QuarryInteractPatch), "Prefix")),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(QuarryInteractPatch), "Finalizer")));
        harmony.Patch(hitPlug,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(QuarryHitPatch), "Postfix")));
        TcmLog.Info(api, "MIN quarrying verb hooked to StoneQuarry");
    }
}

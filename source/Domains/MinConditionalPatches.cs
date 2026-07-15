using System;
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
    // top on every call, so no finalizer is needed to clear it — and dropping the finalizer
    // is the prime suspect for the 0.3.43 knife regression (a finalizer rewraps the whole
    // method's exception handling; a bare prefix does not).
    [ThreadStatic] private static double imPendingFactor;
    [ThreadStatic] private static bool imPickaxeHit;
    [ThreadStatic] private static string? imMinerName;
    [ThreadStatic] private static string? imToolName;
    [ThreadStatic] private static IPlayer? sqQuarrier;

    /// <summary>Last pickaxe-impact timestamp (ms), for measuring swing cadence. Single-miner
    /// diagnostic — interleaves harmlessly with multiple miners; trimmed once cadence is known.</summary>
    private static long lastPickHitMs;

    /// <summary>Captured at patch time so the static stamina prefix can log without an api hop.</summary>
    private static ICoreAPI? logApi;

    /// <summary>Master switch for the IM stamina hook. Diagnostic pass re-enables it with the
    /// finalizer removed + full per-tool logging to confirm the knife path survives.</summary>
    public static bool EnableStaminaAxis = true;

    public static void PatchAllPresent(ICoreAPI api, Harmony harmony)
    {
        if (EnableStaminaAxis) PatchImmersiveMiningStamina(api, harmony);
        else TcmLog.Info(api, "MIN stamina axis DISABLED (diagnostic — IM/Vigor drain check)");
        PatchStoneQuarry(api, harmony);
    }

    // ---------------------------------------------- ImmersiveMining stamina (Axes 2/1/6)

    /// <summary>Gates the scale to PICKAXE hits only — axe is Woodcutting, shovel is
    /// domainless digging, knife is not mining. Sets the rank factor for the TryConsume
    /// that OnDurHit is about to make.</summary>
    public static class DurHitGatePatch
    {
        public static void Prefix(IServerPlayer fromPlayer, object pkt)
        {
            imPendingFactor = 1.0;
            imPickaxeHit = false;
            imMinerName = null;
            imToolName = "?";
            if (fromPlayer == null || pkt == null) return;

            EnumTool tool;
            try { tool = Traverse.Create(pkt).Property<EnumTool>("Tool").Value; }
            catch { return; }
            imToolName = tool.ToString();
            if (tool != EnumTool.Pickaxe) return;

            imPickaxeHit = true;
            imMinerName = fromPlayer.PlayerName;
            imPendingFactor = MinDomain.RankLinear(MinDomain.LevelOf(fromPlayer),
                MinDomain.Knob(MinDomain.StaminaUntrained, 1.15),
                MinDomain.Knob(MinDomain.StaminaGm, 0.85));

            // Cadence probe: ms between consecutive pickaxe impacts — the number that lets us
            // set the Vigor cooldown to the minimum that still suppresses regen mid-dig.
            long now = fromPlayer.Entity?.World?.ElapsedMilliseconds ?? 0;
            if (lastPickHitMs > 0 && now > lastPickHitMs && logApi != null)
                TcmLog.Cat(logApi, TcmLog.Hooks,
                    $"MIN cadence: {now - lastPickHitMs}ms since last pickaxe impact ({imMinerName})");
            if (now > 0) lastPickHitMs = now;
        }
    }

    /// <summary>Scales the flat per-hit stamina amount IM hands Vigor for a PICKAXE. A master
    /// swings for less, an Untrained miner for more; never touches mining speed or durability.
    /// Logs EVERY tool's consume this pass (base -> scaled + factor) so we can confirm the knife
    /// path still fires with the finalizer gone — the 0.3.43 regression check.</summary>
    public static class VigorConsumePatch
    {
        public static void Prefix(ref float amount)
        {
            float baseAmt = amount;
            if (imPickaxeHit && imPendingFactor != 1.0) amount *= (float)imPendingFactor;

            if (logApi != null)
                TcmLog.Cat(logApi, TcmLog.Hooks,
                    $"MIN stamina: tool={imToolName ?? "?"} base={baseAmt:0.##} -> {amount:0.##} " +
                    $"(x{(imPickaxeHit ? imPendingFactor : 1.0):0.###})");
        }
    }

    private static void PatchImmersiveMiningStamina(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("immersivemining")) return;
        logApi = api;

        var onDurHit = AccessTools.Method(AccessTools.TypeByName("ImmersiveMining.ImmersiveMiningServer"), "OnDurHit");
        var tryConsume = AccessTools.Method(AccessTools.TypeByName("ImmersiveMining.VigorHook"), "TryConsume");
        if (onDurHit == null || tryConsume == null)
        {
            TcmLog.Warn(api, "immersivemining present but OnDurHit/VigorHook.TryConsume not found; MIN stamina axis inactive");
            return;
        }

        harmony.Patch(onDurHit,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(DurHitGatePatch), "Prefix")));
        harmony.Patch(tryConsume,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(VigorConsumePatch), "Prefix")));
        TcmLog.Info(api, "MIN stamina axis hooked to ImmersiveMining (pickaxe hits; finalizer-free + cadence probe)");
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

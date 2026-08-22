using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The public-release integrator fallback (0.5 third-pass ruling, 2026-08-21; closes the stated
/// TODO from TemPatches). TEM's Axis-3 resistance writes the <c>stabilityLossMul</c> stat, which
/// only SpecializedClasses reads — without SC the entire resilience spine, Untrained penalty
/// included, was silently inert off The Quire. This patch applies the rank curve to the vanilla
/// integrator DIRECTLY, and is wired ONLY when SC is absent, so it can never double-scale beside
/// SC's own prefix.
///
/// Seam: <c>EntityBehaviorTemporalStabilityAffected.OnGameTick</c> moves <c>OwnStability</c> by a
/// per-tick gain. We capture the value before the tick and rescale only NEGATIVE deltas (ambient
/// and storm loss) by <see cref="TemDomain.StabilityLossMul"/>. Deliberate spends (RBM meditation,
/// future Conjunction recipes) write the WatchedAttribute directly, outside this tick, so they
/// stay exempt by construction — the same contract as the SC path.
///
/// Runs on BOTH sides: the behavior ticks client-side too, and scaling only the server would let
/// the client's local computation drift between WatchedAttribute syncs. Client-side we scale only
/// the LOCAL player (synced TEM level via the MET-gate pattern); other players' entities stay
/// server-authoritative. Client knob values fall back to shipped defaults when the server retunes
/// them — the accepted MET-gate limitation, and the server's synced value wins on every update.
/// </summary>
public static class TemStabilityFallback
{
    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (api.ModLoader.IsModEnabled("specializedclasses"))
        {
            TcmLog.Cat(api, TcmLog.Config,
                "TEM stability fallback dormant: SpecializedClasses applies stabilityLossMul");
            return;
        }

        var m = AccessTools.DeclaredMethod(typeof(EntityBehaviorTemporalStabilityAffected),
            nameof(EntityBehaviorTemporalStabilityAffected.OnGameTick));
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(TemStabilityFallback), nameof(Prefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(TemStabilityFallback), nameof(Postfix))));
        TcmLog.Info(api,
            "TEM stability fallback live: SpecializedClasses absent, TCM scales stability loss directly");
    }

    public static void Prefix(EntityBehaviorTemporalStabilityAffected __instance, out double __state)
        => __state = __instance.OwnStability;

    public static void Postfix(EntityBehaviorTemporalStabilityAffected __instance, double __state)
    {
        double delta = __instance.OwnStability - __state;
        if (delta >= 0) return;                          // only losses are scaled, never recovery

        if (__instance.entity is not EntityPlayer eplr) return;
        var api = __instance.entity.World?.Api;
        var player = eplr.Player;
        if (api == null || player == null) return;

        // Client side: local player only. Everyone else's stability is the server's to write.
        if (api.Side == EnumAppSide.Client
            && (api as ICoreClientAPI)?.World?.Player?.PlayerUID != player.PlayerUID) return;

        double mul = TemDomain.StabilityLossMul(TemRepairGate.TemLevelOf(api, player));
        if (System.Math.Abs(mul - 1.0) < 1e-6) return;

        __instance.OwnStability = GameMath.Clamp(__state + delta * mul, 0.0, 1.0);
    }
}

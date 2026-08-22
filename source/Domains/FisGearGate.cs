using System;
using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacTcm.Domains;

/// <summary>
/// The FIS gear gate (0.5 ruling, 2026-08-22): below the gate rank a player cannot SET Ithania's
/// refined fishing gear — place the fish trap, or swing the fish net. Servicing an already-placed
/// trap (baiting, harvesting) is never gated: a Master sets the trap line, a Novice runs the
/// collection rounds (the TEM repair-gated/transit-free inversion, applied to tackle). The bait
/// economy (worm bin, compost bin), the fillet knife (TechProcessing practice must stay climbable),
/// and the logbook/tags (discovery never gates) all stay open at any rank.
///
/// The trap gate sits on <c>Block.TryPlaceBlock</c>, NOT on DoPlaceBlock: TryPlaceBlock refuses
/// before the stack is consumed, so the trap stays in hand. Ithania's BlockFishTrap does not
/// override it, so the patch rides the base seam with a cached-type check first (the
/// FisTrapPatches.TrapPlacePatch precedent). The net gate sits on OnHeldAttackStart, the sole
/// entry to the scoop.
///
/// Targets resolve by reflection (TCM carries no Ithania reference); absent mod, absent method,
/// warn and skip. Server-owned level <see cref="Config.TcmGlobalConfig.GearGateFISLevel"/>
/// (default Apprentice I; 0 disables). Runs on BOTH sides so the client never mispredicts
/// (the MET-gate pattern), with the same accepted retune-divergence limitation.
/// </summary>
public static class FisGearGate
{
    private static Type? trapType;

    private static int fisDomainId = -2;

    private static int FisDomainId()
    {
        if (fisDomainId != -2) return fisDomainId;
        fisDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == FisDomain.Code) { fisDomainId = i; break; }
        return fisDomainId;
    }

    /// <summary>The player's FIS level from whichever side is live: the server ledger, or
    /// (client) the synced state of the local player.</summary>
    public static int FisLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(FisDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = FisDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static readonly Dictionary<string, long> lastWarn = new();
    private static readonly object errorSender = new();

    /// <summary>True = block this attempt: the gate is enabled and the player sits below the gate
    /// rank. Sends the throttled diegetic warning as a side effect.</summary>
    public static bool Blocks(ICoreAPI? api, IPlayer? player)
    {
        if (api == null || player == null) return false;
        var cfg = (api.Side == EnumAppSide.Server
            ? AlmanacTcmModSystem.ServerInstance
            : AlmanacTcmModSystem.ClientInstance)?.GlobalConfig;
        int gate = cfg?.GearGateFISLevel ?? 5;
        if (gate <= 0) return false;
        if (FisLevelOf(api, player) >= gate) return false;

        Warn(api, player, gate);
        return true;
    }

    private static void Warn(ICoreAPI api, IPlayer player, int gateLevel)
    {
        long now = api.World.ElapsedMilliseconds;
        if (lastWarn.TryGetValue(player.PlayerUID, out long last) && now - last < 2000) return;
        lastWarn[player.PlayerUID] = now;

        if (api is ICoreClientAPI capi)
            capi.TriggerIngameError(errorSender, "fisgeargate",
                Lang.Get("almanactcm:fis-gate-blocked", Domain.RankName(gateLevel)));
        else
            TcmLog.Cat(api, TcmLog.Hooks,
                $"FIS gate: {player.PlayerName} blocked from setting refined tackle (needs {Domain.RankName(gateLevel)})");
    }

    // ------------------------------------------------------------------ patching

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("ithaniaexpandedfishing")) return;

        int hooked = 0;

        trapType = AccessTools.TypeByName("IthaniaExpandedFishing.Blocks.BlockFishTrap");
        if (trapType != null)
        {
            harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.TryPlaceBlock)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TrapPlaceGatePatch), "Prefix")));
            hooked++;
        }
        else TcmLog.Warn(api, "ithania BlockFishTrap not found; the FIS trap gate is inactive");

        var net = AccessTools.TypeByName("IthaniaExpandedFishing.Common.Items.ItemFishNet");
        var netStart = net == null ? null : AccessTools.Method(net, "OnHeldAttackStart");
        if (netStart != null)
        {
            harmony.Patch(netStart, prefix: new HarmonyMethod(AccessTools.Method(typeof(NetGatePatch), "Prefix")));
            hooked++;
        }
        else TcmLog.Warn(api, "ithania ItemFishNet.OnHeldAttackStart not found; the FIS net gate is inactive");

        if (hooked > 0)
            TcmLog.Info(api, $"FIS gear gate live ({hooked} seam(s)): trap placement and net use open at the gate rank; servicing placed traps stays free");
    }

    /// <summary>Refuse trap placement below the gate rank, before the stack is consumed. Broad
    /// seam, so the type check exits first.</summary>
    public static class TrapPlaceGatePatch
    {
        public static bool Prefix(Block __instance, IWorldAccessor world, IPlayer byPlayer, ref bool __result)
        {
            if (trapType == null || !trapType.IsInstanceOfType(__instance)) return true;
            if (!Blocks(world?.Api, byPlayer)) return true;

            __result = false;   // placement refused, the trap stays in hand
            return false;
        }
    }

    /// <summary>Refuse the net swing below the gate rank. Client-side, only the local player is
    /// judged: remote entities pass through so a synced animation is never blocked on the wrong
    /// player's level (the client can only read its own).</summary>
    public static class NetGatePatch
    {
        public static bool Prefix(EntityAgent byEntity, ref EnumHandHandling handling)
        {
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            ICoreAPI? api = byEntity?.Api;
            if (player == null || api == null) return true;
            if (api is ICoreClientAPI capi && capi.World?.Player?.PlayerUID != player.PlayerUID) return true;
            if (!Blocks(api, player)) return true;

            handling = EnumHandHandling.PreventDefault;
            return false;
        }
    }
}

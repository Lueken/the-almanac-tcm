using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The TEM repair gate (0.5 third-pass ruling, 2026-08-21): below the gate rank a player cannot
/// REPAIR a translocator or recharge a discharged teleporter. Transit is never gated — anyone
/// steps through a working machine; mending one takes the trade. This INVERTED the Part 7d
/// teleport-access gate the same day it became buildable: standing on a working teleporter takes
/// no skill, and the temporal-kill co-grant (wired `01caf9c`) plus warding give two ungated roads
/// to the gate rank, so the repair loop deferring costs nothing.
///
/// The gate must sit on <c>OnBlockInteractStart</c>, NOT on <c>DoRepair</c>: the block code
/// consumes the gear/parts around the DoRepair call, so skipping DoRepair alone would eat
/// materials for nothing. Blocking the interact start keeps the player's materials.
///
/// Server-owned level <see cref="Config.TcmGlobalConfig.RepairGateTEMLevel"/> (default Novice IV;
/// 0 disables). Runs on BOTH sides so the client never mispredicts a blocked repair (the MET
/// material-gate pattern); the client reads its shipped config default, so a server that retunes
/// the level diverges client-side until the config sync refinement lands — same accepted
/// limitation as MET's toggle.
/// </summary>
public static class TemRepairGate
{
    private static int temDomainId = -2;

    private static int TemDomainId()
    {
        if (temDomainId != -2) return temDomainId;
        temDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == TemDomain.Code) { temDomainId = i; break; }
        return temDomainId;
    }

    /// <summary>The player's TEM level from whichever side is live: the server ledger, or
    /// (client) the synced state of the local player. Shared with the stability fallback.</summary>
    public static int TemLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(TemDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = TemDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static readonly Dictionary<string, long> lastWarn = new();
    private static readonly object errorSender = new();

    /// <summary>True = block this repair attempt: the gate is enabled and the player sits below
    /// the gate rank. Sends the throttled diegetic warning as a side effect.</summary>
    public static bool Blocks(ICoreAPI? api, IPlayer? player)
    {
        if (api == null || player == null) return false;
        var cfg = (api.Side == EnumAppSide.Server
            ? AlmanacTcmModSystem.ServerInstance
            : AlmanacTcmModSystem.ClientInstance)?.GlobalConfig;
        int gate = cfg?.RepairGateTEMLevel ?? 4;
        if (gate <= 0) return false;
        if (TemLevelOf(api, player) >= gate) return false;

        Warn(api, player, gate);
        return true;
    }

    private static void Warn(ICoreAPI api, IPlayer player, int gateLevel)
    {
        long now = api.World.ElapsedMilliseconds;
        if (lastWarn.TryGetValue(player.PlayerUID, out long last) && now - last < 2000) return;
        lastWarn[player.PlayerUID] = now;

        // Red ingame-error on the CLIENT, where the acting player sees it (MET-gate reasoning:
        // once the client cancels the interact, the packet may never reach the server).
        if (api is ICoreClientAPI capi)
            capi.TriggerIngameError(errorSender, "temrepairgate",
                Lang.Get("almanactcm:tem-gate-blocked", Domain.RankName(gateLevel)));
        else
            TcmLog.Cat(api, TcmLog.Hooks,
                $"TEM gate: {player.PlayerName} blocked from temporal repair (needs {Domain.RankName(gateLevel)})");
    }

    /// <summary>Intercept both vanilla repair shapes before any material is consumed: the
    /// metal-parts state swap on the broken variant, and the temporal-gear feed on a
    /// not-fully-repaired machine. Everything else (transit, empty-handed looks) passes through.</summary>
    [HarmonyPatch(typeof(BlockStaticTranslocator), nameof(BlockStaticTranslocator.OnBlockInteractStart))]
    public static class TranslocatorRepairGatePatch
    {
        public static bool Prefix(BlockStaticTranslocator __instance, IWorldAccessor world,
            IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
        {
            var slot = byPlayer?.InventoryManager?.ActiveHotbarSlot;
            if (slot?.Itemstack == null) return true;

            bool repairAttempt;
            if (!__instance.Repaired)
            {
                repairAttempt = slot.Itemstack.Collectible.Code.Path == "metal-parts" && slot.StackSize >= 2;
            }
            else
            {
                var be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityStaticTranslocator;
                repairAttempt = be != null && !be.FullyRepaired && slot.Itemstack.Collectible is ItemTemporalGear;
            }

            if (!repairAttempt || !Blocks(world.Api, byPlayer)) return true;

            __result = true;   // handled: the click lands, nothing is consumed, nothing advances
            return false;
        }
    }
}

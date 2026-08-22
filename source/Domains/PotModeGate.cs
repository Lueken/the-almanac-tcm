using System;
using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The POT stroke ladder (0.5 B-walk ruling 2026-08-21, built 2026-08-22): the broad clayforming
/// strokes are earned, the copy stroke scales, and single-voxel work stays free at every rank so
/// POT keeps its day-one reachability (the domain's ruled boundary was never "no gates", it was
/// "the foundation stays open", and the 1x1 stroke IS the foundation).
///
/// - The 2x2 stroke opens at Apprentice I, the 3x3 at Journeyman I (the age-ladder rungs), both
///   for adding and removing: the MODE is what is gated, not the direction, and 1x1 correction
///   is always available. Below the rung the click lands, nothing moves, and a word says why.
/// - The duplicate-layer stroke is never gated, it is SCALED: vanilla copies a flat 4 voxels per
///   click; here an Untrained hand manages 2, Novice I restores vanilla's 4, and the count climbs
///   to 6 at Master I and holds (PotDomain.CopyVoxelsFor). Mirror-prefix of vanilla's own
///   OnCopyLayer, byte-faithful but for the quantity.
/// - The powered pottery wheel is the mass-production path and the low-rank accessibility option,
///   config-tuned pack-side (voxel-added 3, 2.0x powered) and untouched here: its entity does not
///   ride BlockEntityClayForm, and the wheel type is excluded defensively anyway.
///
/// Both patches run on BOTH sides (the MET-gate pattern) so the client never mispredicts and the
/// use-over packet is never even sent for a refused stroke.
/// </summary>
public static class PotModeGate
{
    private static Type? wheelType;
    private static bool wheelResolved;

    private static bool IsWheel(object be)
    {
        if (!wheelResolved)
        {
            wheelType = AccessTools.TypeByName("SimplePotteryWheel.ClayWheelEntity");
            wheelResolved = true;
        }
        return wheelType != null && wheelType.IsInstanceOfType(be);
    }

    private static int potDomainId = -2;

    private static int PotDomainId()
    {
        if (potDomainId != -2) return potDomainId;
        potDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == PotDomain.Code) { potDomainId = i; break; }
        return potDomainId;
    }

    /// <summary>The player's POT level from whichever side is live: the server ledger, or
    /// (client) the synced state of the local player.</summary>
    public static int PotLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(PotDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = PotDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static readonly Dictionary<string, long> lastWarn = new();
    private static readonly object errorSender = new();

    private static void Warn(ICoreAPI api, IPlayer player, string stroke, int gateLevel)
    {
        long now = api.World.ElapsedMilliseconds;
        if (lastWarn.TryGetValue(player.PlayerUID, out long last) && now - last < 2000) return;
        lastWarn[player.PlayerUID] = now;

        if (api is ICoreClientAPI capi)
            capi.TriggerIngameError(errorSender, "potmodegate",
                Lang.Get("almanactcm:pot-gate-blocked", stroke, Domain.RankName(gateLevel)));
        else
            TcmLog.Cat(api, TcmLog.Hooks,
                $"POT gate: {player.PlayerName} blocked from the {stroke} stroke (needs {Domain.RankName(gateLevel)})");
    }

    /// <summary>Refuse the broad strokes below their rung, before the packet or a voxel moves.
    /// Tool modes: 0 = 1x1 (never gated), 1 = 2x2, 2 = 3x3, 3 = duplicate (scaled, not gated).</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.OnUseOver),
        new Type[] { typeof(IPlayer), typeof(Vec3i), typeof(BlockFacing), typeof(bool) })]
    public static class BroadStrokeGatePatch
    {
        public static bool Prefix(BlockEntityClayForm __instance, IPlayer byPlayer)
        {
            if (__instance?.Api == null || byPlayer == null || IsWheel(__instance)) return true;

            var slot = byPlayer.InventoryManager?.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible == null) return true;

            int toolMode = slot.Itemstack.Collectible.GetToolMode(slot, byPlayer,
                new BlockSelection() { Position = __instance.Pos });
            if (toolMode != 1 && toolMode != 2) return true;

            var cfg = (__instance.Api.Side == EnumAppSide.Server
                ? AlmanacTcmModSystem.ServerInstance
                : AlmanacTcmModSystem.ClientInstance)?.GlobalConfig;
            int gate = toolMode == 1 ? (cfg?.Place2x2GatePOTLevel ?? Rank.Apprentice)
                                     : (cfg?.Place3x3GatePOTLevel ?? Rank.Journeyman);
            if (gate <= 0 || PotLevelOf(__instance.Api, byPlayer) >= gate) return true;

            Warn(__instance.Api, byPlayer, toolMode == 1 ? "2x2" : "3x3", gate);
            return false;
        }
    }

    /// <summary>The copy stroke, rank-scaled: vanilla's OnCopyLayer reproduced byte for byte,
    /// except the flat 4-voxel quantity becomes PotDomain.CopyVoxelsFor(level). The last player
    /// context OnUseOver saw is not passed in, so the level rides a one-frame handoff set by
    /// the gate prefix above; the wheel never enters (excluded there and not derived anyway).</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), "OnCopyLayer")]
    public static class CopyStrokeScalePatch
    {
        /// <summary>Set by OnUseOverContextPatch just before vanilla dispatches to OnCopyLayer;
        /// -1 means unknown (fall back to vanilla behavior).</summary>
        internal static int pendingLevel = -1;

        public static bool Prefix(BlockEntityClayForm __instance, int layer, ref bool __result)
        {
            if (pendingLevel < 0 || IsWheel(__instance)) return true;
            int quantity = PotDomain.CopyVoxelsFor(pendingLevel);
            pendingLevel = -1;
            if (quantity >= 16 * 16) return true;

            __result = false;
            if (layer <= 0 || layer > 15) return false;

            var voxels = __instance.Voxels;
            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    if (voxels[x, layer - 1, z] && !voxels[x, layer, z])
                    {
                        quantity--;
                        voxels[x, layer, z] = true;
                        __instance.AvailableVoxels--;
                        __result = true;
                    }
                    if (quantity == 0) return false;
                }
            }
            return false;
        }
    }

    /// <summary>Hands the acting player's POT level to the copy-stroke prefix for the duration
    /// of one OnUseOver call. Postfix clears it even when vanilla never reached the copy.</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.OnUseOver),
        new Type[] { typeof(IPlayer), typeof(Vec3i), typeof(BlockFacing), typeof(bool) })]
    public static class OnUseOverContextPatch
    {
        [HarmonyPriority(Priority.Low)] // after the gate prefix, and __runOriginal goes false on refusal
        public static void Prefix(BlockEntityClayForm __instance, IPlayer byPlayer, bool __runOriginal)
        {
            if (!__runOriginal || __instance?.Api == null || byPlayer == null || IsWheel(__instance)) return;
            CopyStrokeScalePatch.pendingLevel = PotLevelOf(__instance.Api, byPlayer);
        }

        public static void Postfix()
        {
            CopyStrokeScalePatch.pendingLevel = -1;
        }
    }
}

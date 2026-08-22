using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AlmanacTcm.Domains;

/// <summary>
/// The COO oven gate (ruled in the LR chat, confirmed in the 0.5 fifth pass, built v1
/// 2026-08-22 per the gate-ladder stock-take): below Journeyman I Cooking the Stone Bake Oven
/// is a full interaction block. No cooking, no adding a pan or cook pot, no loading firewood.
/// Rationale as ruled: a partially usable oven wastes the fuel, so the gate stops the
/// resource going in at all. The vanilla clay oven stays free at any rank (the stone-age
/// baking rung underneath), which is what makes gating the station safe.
///
/// Ladder placement (2026-08-22_gate-ladder-stocktake.md): ranks map onto the material ages,
/// and Journeyman is the iron-age rung where work starts bearing your name; the brick oven is
/// the settlement-scale bakehouse that belongs to it. v1 gates all three interactive
/// surfaces identically; the fuel-and-light split (master fires the oven, anyone bakes) is
/// the recorded feedback tweak if the full block plays too hard.
///
/// Three seams, one per SBO block class that overrides OnBlockInteractStart: the controller
/// (fuel and ignition, extends BlockFirepit), the baking top (extends BlockClayOven), and the
/// cooking top (pan/pot surface with its dialog). The grill is a passive heat source with no
/// interaction, so it needs no gate. Resolved by reflection, warn-and-skip per seam.
/// Runs on BOTH sides so the client never mispredicts (the MET-gate pattern), same accepted
/// retune-divergence limitation as the other gates.
/// </summary>
public static class CooOvenGate
{
    private static int cooDomainId = -2;

    private static int CooDomainId()
    {
        if (cooDomainId != -2) return cooDomainId;
        cooDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == CooDomain.Code) { cooDomainId = i; break; }
        return cooDomainId;
    }

    /// <summary>The player's COO level from whichever side is live: the server ledger, or
    /// (client) the synced state of the local player.</summary>
    public static int CooLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(CooDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = CooDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static readonly Dictionary<string, long> lastWarn = new();
    private static readonly object errorSender = new();

    /// <summary>True = block this interaction: the gate is enabled and the player sits below
    /// the gate rank. Sends the throttled diegetic warning as a side effect.</summary>
    public static bool Blocks(ICoreAPI? api, IPlayer? player)
    {
        if (api == null || player == null) return false;
        var cfg = (api.Side == EnumAppSide.Server
            ? AlmanacTcmModSystem.ServerInstance
            : AlmanacTcmModSystem.ClientInstance)?.GlobalConfig;
        int gate = cfg?.OvenGateCOOLevel ?? Rank.Journeyman;
        if (gate <= 0) return false;
        if (CooLevelOf(api, player) >= gate) return false;

        Warn(api, player, gate);
        return true;
    }

    private static void Warn(ICoreAPI api, IPlayer player, int gateLevel)
    {
        long now = api.World.ElapsedMilliseconds;
        if (lastWarn.TryGetValue(player.PlayerUID, out long last) && now - last < 2000) return;
        lastWarn[player.PlayerUID] = now;

        if (api is ICoreClientAPI capi)
            capi.TriggerIngameError(errorSender, "cooovengate",
                Lang.Get("almanactcm:coo-gate-blocked", Domain.RankName(gateLevel)));
        else
            TcmLog.Cat(api, TcmLog.Hooks,
                $"COO gate: {player.PlayerName} blocked from the brick oven (needs {Domain.RankName(gateLevel)})");
    }

    // ------------------------------------------------------------------ patching

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("stonebakeoven")) return;

        int hooked = 0;
        foreach (string cls in new[] { "BlockOvenController", "BlockOvenBakingTop", "BlockOvenCookingTop" })
        {
            var t = AccessTools.TypeByName("StoneBakeOven." + cls);
            var m = t == null ? null : AccessTools.DeclaredMethod(t, "OnBlockInteractStart");
            if (m == null)
            {
                TcmLog.Warn(api, $"stonebakeoven {cls}.OnBlockInteractStart not found; that surface is ungated");
                continue;
            }
            harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(OvenGatePatch), "Prefix")));
            hooked++;
        }
        if (hooked > 0)
            TcmLog.Info(api, $"COO oven gate live ({hooked} surface(s)): the brick oven opens at {Domain.RankName(Rank.Journeyman)} Cooking; the clay oven stays free");
    }

    /// <summary>Full interaction block below the gate rank, as ruled: the click lands,
    /// nothing is consumed, nothing opens, nothing advances.</summary>
    public static class OvenGatePatch
    {
        public static bool Prefix(IWorldAccessor world, IPlayer byPlayer, ref bool __result)
        {
            if (!Blocks(world?.Api, byPlayer)) return true;

            __result = true;
            return false;
        }
    }
}

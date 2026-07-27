using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// TEM — the verbs that GRANT rank and the three lever families (rank-bonus-design §TEM; technique-maps
/// §TEM RULED). Careful by design: TEM is the one domain that touches the per-tick, every-player temporal
/// integrator, and it coordinates a stat SpecializedClasses owns.
///
///   • Rift warding [vanilla] — grant at the ward fuel/toggle interact (BlockEntityRiftWard.OnInteract;
///     betterjonas wards inherit the same hook), plus the Axis-2 ward-fuel lever (a master's gear fuels a
///     ward longer) applied there.
///   • Machinery mending [vanilla] — grant at the translocator repair (BlockEntityStaticTranslocator.
///     DoRepair); the Axis-2 gear economy rides the temporalGearTLRepairCost stat the repair math reads.
///   • Axis 2 gear economy + Axis 3 stability resistance — written per player by rank on a 2s reconcile
///     (the FOR/MIN stat pattern): temporalGearTLRepairCost (SC-free) and stabilityLossMul (SC-applied,
///     "almanactcm" source key ADDS to SC's archivist trait — never re-scales it).
///   • Axis 6 Storm-Sense — a rank-scaled early storm forecast on the real scheduled data, delivered as a
///     diegetic notification (strength-distinct from Journeyman; storm-blind below Novice). No radar.
///
/// The reserved cross-mod seam is <see cref="TemManifestResist"/> — a rank-weighted proc to shrug off an
/// INVOLUNTARY manifestation drain, which Marginalia Conjunction's rust-mob / devastation drains will call
/// when they ship. Deliberate spends (RBM meditation, Conjunction recipes) use direct writes and are exempt
/// by construction — TEM never reaches them.
/// </summary>
public static class TemPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;
    private static SystemTemporalStability? temporal;

    /// <summary>Per-player last-written (gearCost, stabilityLoss) so the reconcile writes only on change
    /// (WatchedAttributes stay quiet between rank-ups). The FOR/MIN pattern.</summary>
    private static readonly Dictionary<string, (double gear, double loss)> lastStats = new();
    /// <summary>Per-player the nextStormTotalDays we last forecast, so each storm warns once.</summary>
    private static readonly Dictionary<string, double> lastForecast = new();

    // ------------------------------------------------------------ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        temporal = api.ModLoader.GetModSystem<SystemTemporalStability>(true);
        // Gear-cost + stability-loss stats (rank -> stat), and the Storm-Sense forecast, on one reconcile.
        api.Event.RegisterGameTickListener(_ => Reconcile(api), 2000);
        TcmLog.Info(api, "TEM hooks live (warding + repair grants, gear/stability stats, Storm-Sense forecast)");

        // The Axis-3 resistance rides stabilityLossMul, which SpecializedClasses applies. Without SC the
        // stat is inert (vanilla reads no stability stats); the public-release integrator fallback is a
        // noted TODO (The Quire ships SC, so the live path is the stat write above).
        if (!api.ModLoader.IsModEnabled("specializedclasses"))
            TcmLog.Cat(api, TcmLog.Config, "TEM: SpecializedClasses absent -> stabilityLossMul is inert; stability resistance needs the public-release integrator fallback (not yet wired)");
    }

    private static void Reconcile(ICoreServerAPI api)
    {
        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            var entity = player?.Entity;
            if (entity == null) continue;
            int level = TemDomain.LevelOf(player);

            double gear = TemDomain.GearCost(level);
            double loss = TemDomain.StabilityLossMul(level);
            if (!lastStats.TryGetValue(player!.PlayerUID, out var prev)
                || Math.Abs(prev.gear - gear) > 1e-4 || Math.Abs(prev.loss - loss) > 1e-4)
            {
                // Delta convention (base 1.0; traits.json + FOR/MIN precedent). SC blends its archivist
                // trait under its own key with this one — additive, never re-scaled.
                entity.Stats.Set("temporalGearTLRepairCost", "almanactcm", (float)(gear - 1.0), false);
                entity.Stats.Set("stabilityLossMul", "almanactcm", (float)(loss - 1.0), false);
                lastStats[player.PlayerUID] = (gear, loss);
            }

            Forecast(player, level);
        }
    }

    // ------------------------------------------------------------ Axis 6 — Storm-Sense forecast

    /// <summary>Deliver the rank-scaled early storm forecast, once per scheduled storm. Reads the real
    /// nextStormTotalDays / nextStormStrength; a GM feels a Heavy storm a day-plus before vanilla's notify,
    /// strength-distinct from Journeyman. Diegetic notification (no HUD, no command). Storm-blind below Novice.</summary>
    private static void Forecast(IServerPlayer player, int level)
    {
        double lead = TemDomain.StormSenseLead(level);
        if (lead <= 0 || temporal?.StormData == null) return;
        var data = temporal.StormData;
        if (data.nowStormActive) return;

        double daysUntil = data.nextStormTotalDays - sapi!.World.Calendar.TotalDays;
        if (daysUntil <= 0 || daysUntil > lead) return;

        if (lastForecast.TryGetValue(player.PlayerUID, out double warned)
            && Math.Abs(warned - data.nextStormTotalDays) < 1e-6) return;  // already warned for this storm
        lastForecast[player.PlayerUID] = data.nextStormTotalDays;

        string msg = level >= TemDomain.StrengthKnownLevel
            ? Lang.Get("almanactcm:tem-forecast", StrengthWord(data.nextStormStrength), TimePhrase(daysUntil))
            : Lang.Get("almanactcm:tem-forecast-vague");
        player.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
    }

    private static string StrengthWord(EnumTempStormStrength s) => s switch
    {
        EnumTempStormStrength.Heavy => Lang.Get("almanactcm:tem-storm-heavy"),
        EnumTempStormStrength.Medium => Lang.Get("almanactcm:tem-storm-medium"),
        _ => Lang.Get("almanactcm:tem-storm-light"),
    };

    private static string TimePhrase(double days) =>
        days < 0.5 ? Lang.Get("almanactcm:tem-lead-soon")
        : days < 1.0 ? Lang.Get("almanactcm:tem-lead-lessday")
        : Lang.Get("almanactcm:tem-lead-days", Math.Round(days, 1));

    // ------------------------------------------------------------ conditional patches (betterjonas)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // betterjonas discharged base-return teleporter recharge = the same gear-mending verb.
        var t = AccessTools.TypeByName("BetterJonasDevices.BlockDischargedBaseReturnTeleporter")
             ?? AccessTools.TypeByName("BetterJonasDevicesFixed.BlockDischargedBaseReturnTeleporter");
        var m = t == null ? null : AccessTools.DeclaredMethod(t, "OnBlockInteractStart");
        if (m != null)
        {
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(TemPatches), nameof(TeleporterRechargePostfix))));
            TcmLog.Info(api, "TEM betterjonas teleporter recharge hooked (repair grant)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "TEM betterjonas teleporter seam absent; recharge grant inactive (translocator repair unaffected)");
    }

    public static void TeleporterRechargePostfix(IWorldAccessor world, IPlayer byPlayer, bool __result)
    {
        if (!__result || world?.Side != EnumAppSide.Server || byPlayer == null) return;
        GrantRepair(byPlayer, byPlayer.Entity.Pos.AsBlockPos.X, byPlayer.Entity.Pos.AsBlockPos.Z);
    }

    private static void GrantRepair(IPlayer player, int x, int z)
    {
        Core?.Ledger?.Log(player, TemDomain.Code, TemDomain.TechRepair,
            HashCode.Combine("temrepair", x, z, (int)(player.Entity.World.ElapsedMilliseconds / 60000)));
    }

    // ------------------------------------------------------------ warding (grant + ward-fuel lever)

    /// <summary>Grant TEM at a rift-ward fuel/toggle, and apply the Axis-2 ward-fuel lever: if this interact
    /// ADDED fuel, extend the fuel window by the fueller's rank (a master's gear fuels a ward longer). The
    /// protected fuelDays is read/written via Traverse. betterjonas wards ride the inherited method.</summary>
    [HarmonyPatch(typeof(BlockEntityRiftWard), nameof(BlockEntityRiftWard.OnInteract))]
    public static class WardInteractPatch
    {
        public static void Prefix(BlockEntityRiftWard __instance, out double __state)
            => __state = Traverse.Create(__instance).Field("fuelDays").GetValue<double>();

        public static void Postfix(BlockEntityRiftWard __instance, IPlayer byPlayer, bool __result, double __state)
        {
            if (!__result || __instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            int level = TemDomain.LevelOf(byPlayer);

            var fuelField = Traverse.Create(__instance).Field("fuelDays");
            double after = fuelField.GetValue<double>();
            if (after > __state)   // fuel was added this interact
            {
                double extra = (after - __state) * (TemDomain.WardFuel(level) - 1.0);
                if (Math.Abs(extra) > 1e-6) fuelField.SetValue(after + extra);
            }

            var pos = __instance.Pos;
            Core?.Ledger?.Log(byPlayer, TemDomain.Code, TemDomain.TechWarding,
                HashCode.Combine("warding", pos.X, pos.Y, pos.Z,
                    (int)(byPlayer.Entity.World.ElapsedMilliseconds / 60000)));
        }
    }

    // ------------------------------------------------------------ translocator repair (grant)

    /// <summary>Grant TEM at a translocator repair interaction. The Axis-2 gear economy is the
    /// temporalGearTLRepairCost stat (written in the reconcile) that DoRepair's own math reads — no scaling
    /// needed here, just the grant. Deduped per translocator per world-minute.</summary>
    [HarmonyPatch(typeof(BlockEntityStaticTranslocator), nameof(BlockEntityStaticTranslocator.DoRepair))]
    public static class TranslocatorRepairPatch
    {
        public static void Postfix(BlockEntityStaticTranslocator __instance, IPlayer byPlayer)
        {
            if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            var pos = __instance.Pos;
            Core?.Ledger?.Log(byPlayer, TemDomain.Code, TemDomain.TechRepair,
                HashCode.Combine("temrepair", pos.X, pos.Y, pos.Z,
                    (int)(byPlayer.Entity.World.ElapsedMilliseconds / 60000)));
        }
    }
}

/// <summary>
/// TEM's reserved cross-mod seam (Jeffrey ruling 2, 2026-07-22): a rank-weighted PROC to shrug off an
/// INVOLUNTARY temporal-stability drain (a Marginalia manifestation — rust-mob strike, devastation
/// thinness). Marginalia Conjunction's involuntary drains call this before applying; deliberate spends
/// (RBM meditation, Conjunction recipes) do NOT — they are the cost a player chose to pay. Callable today,
/// with no caller yet (Conjunction wires it when its manifestation drains ship).
/// </summary>
public static class TemManifestResist
{
    /// <summary>Roll the Storm-Warden's chance to shrug off one involuntary manifestation drain entirely.
    /// True = the drain is negated for this event. Server-only; false (no resist) off the server.</summary>
    public static bool TryShrugOff(IPlayer? player)
    {
        var world = player?.Entity?.World;
        if (world?.Side != EnumAppSide.Server) return false;
        double chance = TemDomain.ManifestResistChance(TemDomain.LevelOf(player));
        return chance > 0 && world.Rand.NextDouble() < chance;
    }
}

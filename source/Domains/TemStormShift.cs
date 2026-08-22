using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The ambient storm-warning shift (2026-08-21 rulings; full teardown in
/// docs/design/2026-08-21_storm-warning-shift-investigation.md). The first warning moves per
/// player by TEM rank: an Untrained player's warning lands with barely time for the bells to
/// finish (UntrainedLeadRealSeconds), Novice IV gets the stock 0.35-day baseline, and above that
/// the lead grows with the Storm-Sense curve to nearly two days at GM. Two environments:
///
/// WITH Temporal Symphony (The Quire): TS's own server tick still runs untouched, but its
/// type-0 (approaching) and type-1 (imminent) broadcasts are suppressed at TriggerWarning; TCM
/// re-delivers the SAME WarningPacket per player through TS's own channel, so TS's client code
/// renders everything (sound, strength-counted bells, fog/shake ramp, bass, Thunderlord) with
/// zero TS changes. The bell keeps tolling strength+1 at every rank BY RULING: predictable, and
/// lets the player gauge "can I survive this outside" themselves. Pre-imminent quakes: TS's own
/// broadcasts cover the stock (0.02, 0.35) window and the CLIENT swallows them until the local
/// player's own window opens; for leads beyond 0.35, the server rolls TS's own hourly chance per
/// high-rank player and sends them personal TempQuakePackets. The type-1 imminent lightning
/// volley TS fired from TriggerWarning is re-fired here once, world-scale, at the 0.02 crossing.
/// Waning (type 2) passes through untouched. Known cost: TS's /tempsym debug subcommands route
/// through the suppressed broadcast and go quiet while the shift is enabled.
///
/// WITHOUT Temporal Symphony (public release): the vanilla chat warnings get the same treatment.
/// onTempStormTick's notify counter is pinned exactly the way TS pins it, and TCM sends the
/// vanilla lang lines per player at their own lead and at the universal imminent floor.
///
/// A player whose whole lead fits inside the imminent window (Untrained) gets ONE cue, the
/// approaching warning, and then the sky; a separate imminent cue behind their own bells would
/// arrive out of order and is skipped. Everything here degrades to stock behavior: seams are
/// resolved by name, warn-and-skip, verified against TS 2.3.2 only.
/// </summary>
public static class TemStormShift
{
    private static ICoreServerAPI? sapi;
    private static ICoreClientAPI? capi;
    private static SystemTemporalStability? temporal;
    private static bool tsPresent;

    // Reflection handles into TS, resolved at patch time, null = seam absent (degrade).
    private static Type? warningPacketType, quakePacketType;
    private static FieldInfo? warningTypeField, warningStrengthField, quakeBucketField;
    private static MethodInfo? imminentLightning;
    private static PropertyInfo? tsQuakeChanceProp;
    private static object? serverChannel;      // IServerNetworkChannel, resolved lazily
    private static MethodInfo? sendWarningPacket, sendQuakePacket;   // closed SendPacket<T>

    private static bool Enabled =>
        AlmanacTcmModSystem.ServerInstance?.GlobalConfig?.StormShiftTEM
        ?? AlmanacTcmModSystem.ClientInstance?.GlobalConfig?.StormShiftTEM ?? true;

    // ------------------------------------------------------------ registration

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        tsPresent = api.ModLoader.IsModEnabled("temporalsymphony");
        if (api is ICoreClientAPI c) capi = c;

        if (!tsPresent)
        {
            // Vanilla-text path: pin the notify counter (TS's own trick) and own the chat per
            // player. Server-side method; the prefix guards side implicitly via sapi.
            var tick = AccessTools.Method(typeof(SystemTemporalStability), "onTempStormTick");
            if (tick == null) { TcmLog.Cat(api, TcmLog.Config, "TEM shift: onTempStormTick seam absent; vanilla warnings stay stock"); return; }
            harmony.Patch(tick, prefix: new HarmonyMethod(AccessTools.Method(typeof(TemStormShift), nameof(VanillaNotifyPinPrefix))));
            TcmLog.Info(api, "TEM shift live (vanilla path): storm chat warnings delivered per player by rank");
            return;
        }

        var modSysType = AccessTools.TypeByName("TemporalCall.TemporalCallModSystem");
        var quakeSysType = AccessTools.TypeByName("TemporalCall.TemporalStormQuakeSystem");
        warningPacketType = AccessTools.TypeByName("TemporalCall.WarningPacket");
        quakePacketType = AccessTools.TypeByName("TemporalCall.TempQuakePacket");
        var riftWatcher = AccessTools.TypeByName("TemporalCall.RiftActivityWatcher");
        var tsServerCfg = AccessTools.TypeByName("TemporalCall.Config.TemporalSymphonyServerConfig");

        var trigger = modSysType == null ? null : AccessTools.DeclaredMethod(modSysType, "TriggerWarning");
        var quakeEntry = quakeSysType == null ? null : AccessTools.DeclaredMethod(quakeSysType, "TriggerDebugQuake");
        warningTypeField = warningPacketType?.GetField("Type");
        warningStrengthField = warningPacketType?.GetField("Strength");
        quakeBucketField = quakePacketType?.GetField("BucketMs");
        imminentLightning = riftWatcher == null ? null : AccessTools.DeclaredMethod(riftWatcher, "TriggerImminentLightning");
        tsQuakeChanceProp = tsServerCfg?.GetProperty("PreImminentTempQuakeHourlyChance");

        if (trigger == null || quakeEntry == null || warningPacketType == null || quakePacketType == null
            || warningTypeField == null || warningStrengthField == null || quakeBucketField == null)
        {
            TcmLog.Cat(api, TcmLog.Config, "TEM shift: Temporal Symphony seams not as verified (2.3.2); shift inactive, TS runs stock");
            return;
        }

        harmony.Patch(trigger, prefix: new HarmonyMethod(AccessTools.Method(typeof(TemStormShift), nameof(WarningSuppressPrefix))));
        harmony.Patch(quakeEntry, prefix: new HarmonyMethod(AccessTools.Method(typeof(TemStormShift), nameof(QuakeGatePrefix))));
        TcmLog.Info(api, "TEM shift live (Temporal Symphony path): warnings and quakes delivered per player by rank");
    }

    /// <summary>Server wiring, called from TemPatches.RegisterServer alongside the reconcile.</summary>
    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        temporal = api.ModLoader.GetModSystem<SystemTemporalStability>(true);
        api.Event.RegisterGameTickListener(_ => Tick(api), 2000);
    }

    // ------------------------------------------------------------ Harmony prefixes

    /// <summary>Suppress TS's approaching (0) and imminent (1) broadcasts; TCM re-delivers both
    /// per player. Waning (2) stays TS's own broadcast.</summary>
    public static bool WarningSuppressPrefix(byte type)
        => !Enabled || type >= 2;

    /// <summary>Client-side: swallow a pre-storm quake the local player has not earned yet. TCM's
    /// own personal sends always land inside the player's window, so they pass by construction.</summary>
    public static bool QuakeGatePrefix()
    {
        if (!Enabled || capi == null) return true;
        var data = capi.ModLoader.GetModSystem<SystemTemporalStability>()?.StormData;
        var player = capi.World?.Player;
        if (data == null || player == null || data.nowStormActive) return true;

        double daysLeft = data.nextStormTotalDays - capi.World!.Calendar.TotalDays;
        if (daysLeft <= 0) return true;
        double lead = TemDomain.ApproachLeadDays(
            TemRepairGate.TemLevelOf(capi, player),
            RealSecondsToDays(capi, TemDomain.UntrainedLeadRealSeconds));
        return daysLeft <= lead;
    }

    /// <summary>No-TS path: pin the vanilla notify counter every tick, exactly TS's trick, so the
    /// stock broadcasts never fire while the shift owns delivery.</summary>
    public static void VanillaNotifyPinPrefix(SystemTemporalStability __instance)
    {
        if (!Enabled) return;
        var data = __instance.StormData;
        if (data != null && data.stormDayNotify >= 0) data.stormDayNotify = -1;
    }

    // ------------------------------------------------------------ the per-player delivery tick

    /// <summary>Per player per storm: 0 nothing sent, 1 approaching sent, 2 imminent sent.</summary>
    private static readonly Dictionary<string, byte> sentPhase = new();
    private static double trackedStorm = -1;
    private static bool lightningFired;
    private static readonly Dictionary<string, long> lastQuakeHour = new();
    private static readonly Dictionary<string, int> lastQuakeBucket = new();

    private static void Tick(ICoreServerAPI api)
    {
        if (!Enabled || temporal?.StormData == null) return;
        var data = temporal.StormData;
        if (data.nowStormActive) return;

        double daysLeft = data.nextStormTotalDays - api.World.Calendar.TotalDays;
        if (daysLeft <= 0) return;

        if (Math.Abs(data.nextStormTotalDays - trackedStorm) > 1e-9)
        {
            trackedStorm = data.nextStormTotalDays;
            sentPhase.Clear();
            lightningFired = false;
        }

        byte strength = (byte)data.nextStormStrength;
        double untrainedDays = RealSecondsToDays(api, TemDomain.UntrainedLeadRealSeconds);

        // The world-scale imminent lightning volley TS fired from its suppressed broadcast.
        if (tsPresent && !lightningFired && daysLeft <= TemDomain.ImminentDays)
        {
            lightningFired = true;
            try { imminentLightning?.Invoke(null, null); } catch (Exception e) { TcmLog.Error(api, $"TEM shift: imminent lightning re-fire failed ({e.Message})"); }
        }

        foreach (var p in api.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer plr || plr.ConnectionState != EnumClientState.Playing) continue;
            double lead = TemDomain.ApproachLeadDays(TemDomain.LevelOf(plr), untrainedDays);
            sentPhase.TryGetValue(plr.PlayerUID, out byte sent);

            if (sent < 1 && daysLeft <= lead)
            {
                SendWarning(plr, 0, strength);
                sentPhase[plr.PlayerUID] = sent = 1;
            }
            // A lead inside the imminent window means the bells ARE the whole warning; a second
            // cue behind them would arrive out of order and is skipped.
            if (sent == 1 && lead > TemDomain.ImminentDays && daysLeft <= TemDomain.ImminentDays)
            {
                SendWarning(plr, 1, strength);
                sentPhase[plr.PlayerUID] = 2;
            }
            // Early quakes for leads beyond the stock window; TS's own broadcasts cover the rest.
            if (tsPresent && daysLeft <= lead && daysLeft > TemDomain.BaselineLeadDays)
                MaybeRollQuake(api, plr);
        }
    }

    /// <summary>TS's own pre-imminent roll, applied per high-rank player: once per in-game hour,
    /// the server's configured chance, buckets 15/20/25s without immediate repeats.</summary>
    private static void MaybeRollQuake(ICoreServerAPI api, IServerPlayer plr)
    {
        long hour = (long)Math.Floor(api.World.Calendar.TotalDays * 24.0);
        if (lastQuakeHour.TryGetValue(plr.PlayerUID, out long prev) && prev == hour) return;
        lastQuakeHour[plr.PlayerUID] = hour;

        double chance = 0.2;
        try { chance = Convert.ToDouble(tsQuakeChanceProp?.GetValue(null) ?? 0.2); } catch { }
        if (api.World.Rand.NextDouble() >= chance) return;

        int bucket = 15000 + api.World.Rand.Next(3) * 5000;
        if (lastQuakeBucket.TryGetValue(plr.PlayerUID, out int last) && bucket == last)
            bucket = 15000 + (bucket - 15000 + 5000) % 15000;
        lastQuakeBucket[plr.PlayerUID] = bucket;

        var pkt = Activator.CreateInstance(quakePacketType!);
        quakeBucketField!.SetValue(pkt, bucket);
        SendTsPacket(pkt!, quakePacketType!, ref sendQuakePacket, plr);
    }

    // ------------------------------------------------------------ delivery

    private static void SendWarning(IServerPlayer plr, byte type, byte strength)
    {
        if (tsPresent)
        {
            var pkt = Activator.CreateInstance(warningPacketType!);
            warningTypeField!.SetValue(pkt, type);
            warningStrengthField!.SetValue(pkt, strength);
            SendTsPacket(pkt!, warningPacketType!, ref sendWarningPacket, plr);
            return;
        }

        // Vanilla path: the stock lang lines, per player instead of broadcast.
        string word = ((EnumTempStormStrength)strength) switch
        {
            EnumTempStormStrength.Heavy => "heavy",
            EnumTempStormStrength.Medium => "medium",
            _ => "light",
        };
        string msg = Lang.Get(type == 0
            ? $"A {word} temporal storm is approaching"
            : $"A {word} temporal storm is imminent");
        plr.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
    }

    /// <summary>Send one of TS's own packets to one player over TS's own channel. The channel and
    /// the closed generic SendPacket are resolved once and cached; any failure logs and no-ops.</summary>
    private static void SendTsPacket(object pkt, Type pktType, ref MethodInfo? cachedSend, IServerPlayer plr)
    {
        try
        {
            serverChannel ??= sapi?.Network.GetChannel("temporalsymphony");
            if (serverChannel == null) return;
            cachedSend ??= typeof(IServerNetworkChannel).GetMethod("SendPacket")!.MakeGenericMethod(pktType);
            cachedSend.Invoke(serverChannel, new object[] { pkt, new IServerPlayer[] { plr } });
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi!, $"TEM shift: TS packet send failed ({e.Message}); shift delivery degraded");
        }
    }

    private static double RealSecondsToDays(ICoreAPI api, double realSeconds)
    {
        var cal = api.World.Calendar;
        double gameSecPerRealSec = cal.SpeedOfTime * cal.CalendarSpeedMul;
        if (gameSecPerRealSec <= 0) gameSecPerRealSec = 30.0;   // stock 60 x 0.5
        return realSeconds * gameSecPerRealSec / (cal.HoursPerDay * 3600.0);
    }
}

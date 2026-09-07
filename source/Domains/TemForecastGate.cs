using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The storm-forecast data gate (2026-09-06 sub-brief: docs/design/2026-09-06_storm-forecast-gate.md).
/// TemStormShift owns the cues and TemPatches.Forecast owns the written line, but vanilla still
/// syncs the raw schedule — TemporalStormRunTimeData.nextStormTotalDays — to every client at join
/// (TemporalStability.cs:331) and on storm transitions (:377, :415), so any clock HUD hands every
/// rank a Grandmaster forecast for free. This gate overwrites that field per player with a
/// far-future placeholder until the storm crosses their own ApproachLeadDays window, then sends
/// the truth once, so HUD mods light in the same breath as the bells and never sooner.
///
/// Mechanics: vanilla's client handler is a bare "this.data = data" (last packet wins), and the
/// scheduling RNG is server-side, so a corrective send on vanilla's own "temporalstability"
/// channel is the whole trick — no Harmony, no client component. Every other field stays
/// truthful: nowStormActive and stormGlitchStrength drive the visuals, stormActiveTotalDays the
/// waning bell, and nextStormStrength stays real by the bell ruling (it tolls strength+1 at every
/// rank). Verified against TS 2.3.2: its client never reads nextStormTotalDays, so the gate is
/// invisible to Temporal Symphony's rendering.
///
/// Ticks from TemPatches.Reconcile (same 2s cadence that delivers the written forecast); joins
/// are corrected on PlayerNowPlaying, which fires after vanilla's truthful join send. Rides the
/// same StormShiftTEM flag as the warning shift — the shift and the gate are one design — and
/// toggling off restores the true schedule to everyone online, so it degrades to stock.
/// </summary>
public static class TemForecastGate
{
    /// <summary>The placeholder lead, in days. Far enough that no HUD window ever opens on it
    /// (HudClock and vanilla both treat 0.35 days as "approaching"; statushud hides beyond its
    /// own window), and never rendered anywhere: HudClock's far-away state is a static string.</summary>
    private const double RedactDays = 9999.0;

    private static ICoreServerAPI? sapi;
    private static SystemTemporalStability? temporal;
    private static IServerNetworkChannel? channel;

    /// <summary>Players who have been sent ANY corrective packet this storm cycle. A joiner not
    /// yet in this set holds vanilla's truthful join packet and must be corrected either way.</summary>
    private static readonly HashSet<string> synced = new();
    /// <summary>Players currently holding the truthful schedule (their window is open).
    /// Always a subset of synced.</summary>
    private static readonly HashSet<string> revealed = new();
    private static double trackedStorm = -1;
    private static bool prevEnabled = true;

    private static bool Enabled =>
        AlmanacTcmModSystem.ServerInstance?.GlobalConfig?.StormShiftTEM ?? true;

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        temporal = api.ModLoader.GetModSystem<SystemTemporalStability>(true);
        api.Event.PlayerNowPlaying += OnNowPlaying;
        api.Event.PlayerDisconnect += plr => { synced.Remove(plr.PlayerUID); revealed.Remove(plr.PlayerUID); };
        TcmLog.Info(api, "TEM forecast gate live: synced storm schedule redacted per player until their Storm-Sense window opens");
    }

    /// <summary>Close the join gap: vanilla sent the truthful schedule on PlayerJoin; overwrite it
    /// before the first HUD frame can matter. The Reconcile tick would catch it within 2s anyway.</summary>
    private static void OnNowPlaying(IServerPlayer plr)
    {
        if (!Enabled || sapi == null || temporal?.StormData == null) return;
        var data = temporal.StormData;
        if (data.nowStormActive) return;   // active-storm data must stay truthful for the visuals

        double daysLeft = data.nextStormTotalDays - sapi.World.Calendar.TotalDays;
        if (daysLeft <= 0) return;
        SendCorrective(plr, data, daysLeft <= LeadOf(plr));
    }

    /// <summary>Called from TemPatches.Reconcile, right after TrackStorm, every 2s.</summary>
    public static void Tick(ICoreServerAPI api)
    {
        if (temporal?.StormData == null) return;
        var data = temporal.StormData;

        if (!Enabled)
        {
            // Live-toggled off: put the true schedule back on every client, then go inert.
            if (prevEnabled)
            {
                prevEnabled = false;
                foreach (var p in api.World.AllOnlinePlayers)
                    if (p is IServerPlayer plr && plr.ConnectionState == EnumClientState.Playing)
                        Send(plr, data);
                synced.Clear(); revealed.Clear(); trackedStorm = -1;
                TcmLog.Cat(api, TcmLog.Hooks, "TEM forecast gate: disabled, true schedule restored to all clients");
            }
            return;
        }
        prevEnabled = true;

        if (data.nowStormActive) return;   // schedule is expired mid-storm; nothing to hide
        double daysLeft = data.nextStormTotalDays - api.World.Calendar.TotalDays;
        if (daysLeft <= 0) return;

        // A fresh schedule means vanilla just broadcast the truth to everyone (storm end,
        // fast-forward, or first tick after enable): sweep every client back to their own side.
        if (Math.Abs(data.nextStormTotalDays - trackedStorm) > 1e-9)
        {
            trackedStorm = data.nextStormTotalDays;
            synced.Clear(); revealed.Clear();
        }

        foreach (var p in api.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer plr || plr.ConnectionState != EnumClientState.Playing) continue;
            bool inWindow = daysLeft <= LeadOf(plr);
            string uid = plr.PlayerUID;

            if (!synced.Contains(uid))
            {
                SendCorrective(plr, data, inWindow);
            }
            else if (inWindow && !revealed.Contains(uid))
            {
                Send(plr, data);
                revealed.Add(uid);
                TcmLog.Cat(api, TcmLog.Hooks, $"TEM forecast gate: window open for {plr.PlayerName}, true schedule revealed");
            }
            else if (!inWindow && revealed.Contains(uid))
            {
                // Rank lost or the GM knob retuned downward mid-approach: take the page back.
                Send(plr, RedactedCopy(data));
                revealed.Remove(uid);
                TcmLog.Cat(api, TcmLog.Hooks, $"TEM forecast gate: window closed for {plr.PlayerName}, schedule redacted again");
            }
        }
    }

    private static double LeadOf(IServerPlayer plr) =>
        TemDomain.ApproachLeadDays(TemDomain.LevelOf(plr),
            TemStormShift.RealSecondsToDays(sapi!, TemDomain.NoviceILeadRealSeconds));

    /// <summary>First corrective packet of the cycle for this player: truth if their window is
    /// already open (a high rank joining mid-approach), the placeholder otherwise.</summary>
    private static void SendCorrective(IServerPlayer plr, TemporalStormRunTimeData data, bool inWindow)
    {
        Send(plr, inWindow ? data : RedactedCopy(data));
        synced.Add(plr.PlayerUID);
        if (inWindow) revealed.Add(plr.PlayerUID);
    }

    private static TemporalStormRunTimeData RedactedCopy(TemporalStormRunTimeData data) => new()
    {
        spawnPatternCode = data.spawnPatternCode,
        nowStormActive = data.nowStormActive,
        stormDayNotify = data.stormDayNotify,
        stormGlitchStrength = data.stormGlitchStrength,
        stormActiveTotalDays = data.stormActiveTotalDays,
        nextStormTotalDays = sapi!.World.Calendar.TotalDays + RedactDays,
        nextStormStrength = data.nextStormStrength,
        nextStormStrDouble = data.nextStormStrDouble,
        rareSpawnCount = data.rareSpawnCount,
    };

    private static void Send(IServerPlayer plr, TemporalStormRunTimeData packet)
    {
        try
        {
            channel ??= sapi?.Network.GetChannel("temporalstability");
            channel?.SendPacket(packet, plr);
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi!, $"TEM forecast gate: corrective send failed ({e.Message}); client keeps vanilla schedule");
        }
    }
}

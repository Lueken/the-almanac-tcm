using System;
using System.Collections.Generic;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent.Mechanics;

namespace AlmanacTcm.Domains;

/// <summary>
/// GROSS SOURCE TORQUE, the deadlock tell for the ENG machine reading (2026-08-07).
///
/// The instrument panel reads vanilla's <c>NetworkTorque</c>, which is a SIGNED sum:
/// <c>updateNetwork</c> accumulates <c>gearedRatio * GetTorque(...)</c> over every node
/// (MechanicalNetwork.updateNetwork, 1.22.5). Two water wheels rigged against each other
/// therefore cancel to about zero and print exactly what a dead shaft prints. The machine is
/// straining under full power and the panel calls it idle.
///
/// Gross is the same walk with the signs removed: the sum of the MAGNITUDES every source
/// contributes. Against net it separates the two states. Matching means one direction of
/// drive; a gap means sources are cancelling each other, and the size of the gap is the size
/// of the fight.
///
/// WHY IT IS CAPTURED AND NOT COMPUTED. Per-source torque exists nowhere but inside that one
/// loop, and <c>BEBehaviorMPRotor.GetTorque</c> MUTATES the rotor while answering (it advances
/// <c>capableSpeed</c> toward its target every call, BEBehaviorMPRotor.GetTorque line 85).
/// Asking a second time would spin the machine up faster than the world does. So we ride the
/// real pass: a prefix on <c>updateNetwork</c> opens a window, a postfix on the rotor's
/// <c>GetTorque</c> adds each answer's magnitude, and the <c>updateNetwork</c> postfix
/// publishes the finished sum. Nothing is called twice and nothing vanilla computes is changed.
///
/// WHAT COUNTS. Base <c>BEBehaviorMPBase.GetTorque</c> returns zero torque (machines are
/// resistance only), and <c>BEBehaviorMPRotor</c> holds the only override in VSSurvivalMod, so
/// this one seam covers windmill, water wheel and creative rotor alike. A mod whose source
/// reimplements <c>GetTorque</c> on a different base is NOT counted, and gross then reads low
/// against net. Millwright's vertical-axis rotor is the known case. Accepted for v1: the
/// figure is honest about vanilla's sources, which is where the fighting-wheels problem lives.
///
/// WHY IT IS SYNCED (C-3). The panel renders CLIENT-side on a dedicated server, where none of
/// this exists: <c>updateNetwork</c> runs on the server only. So the finished sums ride their
/// own channel on a one second cadence, lighter than vanilla's own per-network broadcast at
/// every 40 ticks. The client holds them by network id and forgets any that stops arriving,
/// so a dismantled machine's last number cannot linger on a shaft that no longer turns.
/// </summary>
public static class EngGrossTorque
{
    private const string ChannelName = "almanactcmeng";

    /// <summary>Server broadcast cadence. Vanilla's own MechNetworkPacket goes out every 40
    /// ticks (800ms) per network; one packet per second for the whole set is cheaper than that.</summary>
    private const int BroadcastMs = 1000;

    /// <summary>How long a client-held figure stays readable after its last packet. Ten seconds
    /// is long enough to ride a hitch and short enough that an unloaded network goes quiet
    /// rather than freezing its last reading onto the panel.</summary>
    private const long StaleMs = 10000;

    [ProtoContract]
    public class EngGrossPacket
    {
        [ProtoMember(1)] public long[] NetworkIds = Array.Empty<long>();
        [ProtoMember(2)] public float[] Gross = Array.Empty<float>();
        [ProtoMember(3)] public float[] CapAbs = Array.Empty<float>();
        [ProtoMember(4)] public float[] CapEff = Array.Empty<float>();
    }

    // ------------------------------------------------------------ state

    private static IServerNetworkChannel? serverChannel;
    private static ICoreClientAPI? capi;

    /// <summary>Server truth, drained into every broadcast. Written on the server tick thread
    /// (vanilla's mech power tick is a plain game tick listener, MechanicalPowerMod.Start) and
    /// read on the same thread by the broadcast listener, so it needs no lock.</summary>
    private static readonly Dictionary<long, (float Gross, float CapAbs, float CapEff)> serverGross = new();

    /// <summary>Client mirror: network id to the last figures and when they landed.</summary>
    private static readonly Dictionary<long, (float Gross, float CapAbs, float CapEff, long AtMs)> clientGross = new();

    // Capacity reads three protected members off the rotor during the same pass. capableSpeed is
    // the rotor's smoothed capability follower, TorqueFactor its strength, and propagationDir
    // against OutFacingForNetworkDiscovery is the same frame test GetTorque itself signs by.
    // Mod interplay note: Ingenium flips a reversed-flow water wheel's propagationDir for the
    // duration of GetTorque and restores it in a Finalizer, which runs AFTER postfixes, so the
    // frame this postfix reads is the frame the original actually used. That is the correct one.
    private static readonly AccessTools.FieldRef<BEBehaviorMPRotor, double> CapableSpeed =
        AccessTools.FieldRefAccess<BEBehaviorMPRotor, double>("capableSpeed");
    private static readonly System.Func<BEBehaviorMPRotor, float> TorqueFactorOf =
        AccessTools.MethodDelegate<System.Func<BEBehaviorMPRotor, float>>(
            AccessTools.PropertyGetter(typeof(BEBehaviorMPRotor), "TorqueFactor"));
    private static readonly AccessTools.FieldRef<BEBehaviorMPBase, BlockFacing> PropagationDir =
        AccessTools.FieldRefAccess<BEBehaviorMPBase, BlockFacing>("propagationDir");

    /// <summary>The network whose real updateNetwork pass is open right now, and the magnitude
    /// sum built during it. ThreadStatic so an off-thread caller can never fold its rotors into
    /// someone else's total.</summary>
    [ThreadStatic] private static MechanicalNetwork? capturing;
    [ThreadStatic] private static float accum;
    [ThreadStatic] private static float capAbsAccum;
    [ThreadStatic] private static float capSignedAccum;

    // ------------------------------------------------------------ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        serverGross.Clear();                       // a fresh world never inherits the last one's ids
        serverChannel = api.Network.RegisterChannel(ChannelName).RegisterMessageType<EngGrossPacket>();
        api.Event.RegisterGameTickListener(OnBroadcastTick, BroadcastMs);
    }

    public static void RegisterClient(ICoreClientAPI api)
    {
        capi = api;
        clientGross.Clear();
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<EngGrossPacket>()
            .SetMessageHandler<EngGrossPacket>(OnGrossPacket);
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var update = AccessTools.DeclaredMethod(typeof(MechanicalNetwork), "updateNetwork", new[] { typeof(long) });
        var torque = AccessTools.DeclaredMethod(typeof(BEBehaviorMPRotor), "GetTorque",
            new[] { typeof(long), typeof(float), typeof(float).MakeByRefType() });

        if (update == null || torque == null)
        {
            TcmLog.Cat(api, TcmLog.Config,
                "ENG gross-torque seam absent (MechanicalNetwork.updateNetwork / BEBehaviorMPRotor.GetTorque); "
                + "the panel keeps its net figures and drops the gross line");
            return;
        }

        harmony.Patch(update,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(EngGrossTorque), nameof(UpdateNetworkPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(EngGrossTorque), nameof(UpdateNetworkPostfix))));
        harmony.Patch(torque,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(EngGrossTorque), nameof(RotorTorquePostfix))));

        TcmLog.Info(api, "ENG gross-torque capture hooked (rides vanilla's own updateNetwork pass, synced on "
            + ChannelName + ")");
    }

    // ------------------------------------------------------------ the capture (server)

    /// <summary>Opens the window. Resets unconditionally, so a pass that throws before its
    /// postfix can only cost its own reading: the next pass starts from zero either way.</summary>
    public static void UpdateNetworkPrefix(MechanicalNetwork __instance)
    {
        capturing = __instance;
        accum = 0f;
        capAbsAccum = 0f;
        capSignedAccum = 0f;
    }

    /// <summary>Publishes the finished sum. Only a pass that completed gets here, so a partial
    /// walk is never shown as a total.</summary>
    public static void UpdateNetworkPostfix(MechanicalNetwork __instance)
    {
        if (ReferenceEquals(capturing, __instance))
            serverGross[__instance.networkId] = (accum, capAbsAccum, Math.Abs(capSignedAccum));
        capturing = null;
    }

    /// <summary>One source's answer, sign discarded. Reads <c>__result</c> after the original
    /// has run, so it sees the torque the network actually receives however many other mods
    /// have their own hands on this method.
    ///
    /// The network check is what makes the window safe: a GetTorque called from anywhere other
    /// than the open pass belongs to a different network (or to no pass at all) and is dropped
    /// rather than added to someone else's total.</summary>
    public static void RotorTorquePostfix(BEBehaviorMPRotor __instance, float __result)
    {
        if (capturing == null || !ReferenceEquals(__instance?.Network, capturing)) return;
        accum += Math.Abs(__instance!.GearedRatio * __result);

        // Capacity: what this source could deliver at stall, in the network frame, with the sign
        // the frame test would give it. The absolute sum is total capacity; the magnitude of the
        // signed sum is what survives opposition. Both are stable through transients, which is
        // what makes them readable where instantaneous torque is not.
        try
        {
            float cap = (float)CapableSpeed(__instance) * TorqueFactorOf(__instance) * __instance.GearedRatio;
            BlockFacing pd = PropagationDir(__instance);
            float num = (pd != null && pd == __instance.OutFacingForNetworkDiscovery) ? 1f : -1f;
            capAbsAccum += Math.Abs(cap);
            capSignedAccum += cap * num;
        }
        catch { /* reflection miss on a future build: capacity reads 0, gross still works */ }
    }

    // ------------------------------------------------------------ the wire

    /// <summary>Drain and send. Every live network refills its entry every 5 ticks (100ms), so
    /// clearing here costs nothing and means a network that stopped being ticked at all, because
    /// its chunks unloaded or its last node came out, simply stops appearing.</summary>
    private static void OnBroadcastTick(float dt)
    {
        if (serverChannel == null || serverGross.Count == 0) return;

        var ids = new long[serverGross.Count];
        var vals = new float[serverGross.Count];
        var caps = new float[serverGross.Count];
        var effs = new float[serverGross.Count];
        int i = 0;
        foreach (var kv in serverGross)
        {
            ids[i] = kv.Key;
            vals[i] = kv.Value.Gross;
            caps[i] = kv.Value.CapAbs;
            effs[i] = kv.Value.CapEff;
            i++;
        }
        serverGross.Clear();

        serverChannel.BroadcastPacket(new EngGrossPacket { NetworkIds = ids, Gross = vals, CapAbs = caps, CapEff = effs });
    }

    private static void OnGrossPacket(EngGrossPacket packet)
    {
        if (capi == null || packet?.NetworkIds == null || packet.Gross == null) return;

        long now = capi.World.ElapsedMilliseconds;
        int n = Math.Min(packet.NetworkIds.Length, packet.Gross.Length);
        bool hasCaps = packet.CapAbs != null && packet.CapAbs.Length >= n && packet.CapEff != null && packet.CapEff.Length >= n;
        for (int i = 0; i < n; i++)
            clientGross[packet.NetworkIds[i]] = (packet.Gross[i],
                hasCaps ? packet.CapAbs![i] : 0f, hasCaps ? packet.CapEff![i] : 0f, now);

        List<long>? gone = null;
        foreach (var kv in clientGross)
            if (now - kv.Value.AtMs > StaleMs) (gone ??= new List<long>()).Add(kv.Key);
        if (gone != null) foreach (long id in gone) clientGross.Remove(id);
    }

    // ------------------------------------------------------------ the read (client)

    /// <summary>The last gross figure for this network, or false when none has arrived recently.
    /// False for the first second after a join and for any network the server has stopped
    /// ticking; the panel drops the line rather than printing a number it cannot stand behind.</summary>
    public static bool TryGetReadout(long networkId, out float gross, out float capAbs, out float capEff)
    {
        gross = 0f; capAbs = 0f; capEff = 0f;
        if (capi == null || !clientGross.TryGetValue(networkId, out var entry)) return false;
        if (capi.World.ElapsedMilliseconds - entry.AtMs > StaleMs) return false;
        gross = entry.Gross; capAbs = entry.CapAbs; capEff = entry.CapEff;
        return true;
    }
}

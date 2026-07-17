using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// PAN Phase 2 — THE SURVEYOR (rank-bonus-design §PAN Axis 3 Master+ / Axis 6, ruled
/// 2026-07-09). The 2-D reading withholds what the ore map knows: how DEEP each ore runs.
/// From Master I, every reading a surveyor takes also records the per-ore depth band
/// (DepositVariant.GeneratorInst.GetYMinMax — the deposit's own placement math), keyed to the
/// same 32-block chunk column ProspectTogether uses.
///
/// The band lives in a Copybook companion store (PT's protobuf model is closed) and travels
/// with the SAME privacy semantics as PT itself:
///   • the surveyor's own client gets it immediately (and on rejoin),
///   • it rides to teammates only when the reading is actually SHARED through PT (postfix on
///     ServerStorage.PlayerSharedProspectingData, same recipients),
///   • late joiners pulling group data get the shared bands too (PlayerRequestsInfoForGroup).
/// The PT map tooltip surfaces it via a GetMessage postfix: the shared survey IS the signed
/// artifact — a village hires the GM Surveyor precisely because the map they leave behind
/// says "iron: 40 to 70 blocks down" and nobody else's does.
/// </summary>
public static class PanSurveyor
{
    /// <summary>Master I: the rank where the strata start speaking (ruled ladder).</summary>
    public const int MasterLevel = 13;
    private const double MentionThreshold = 0.025;
    private const int MaxBandsPerReading = 5;

    private static ICoreServerAPI? sapi;
    private static ICoreClientAPI? capi;
    private static IServerNetworkChannel? serverChannel;

    // ------------------------------------------------------------ wire + persistence model

    [ProtoContract]
    public class PanOreBand
    {
        [ProtoMember(1)] public string OreKey = "";
        [ProtoMember(2)] public int MinDepth; // blocks below the probed surface
        [ProtoMember(3)] public int MaxDepth;
        /// <summary>True when the band could not be narrowed and spans most of the column —
        /// phrased honestly rather than printing a uselessly precise-looking range.</summary>
        [ProtoMember(4)] public bool Wide;
    }

    [ProtoContract]
    public class PanChunkDepth
    {
        [ProtoMember(1)] public int Cx;
        [ProtoMember(2)] public int Cz;
        [ProtoMember(3)] public List<PanOreBand> Bands = new();
        [ProtoMember(4)] public string ProberUid = "";
        [ProtoMember(5)] public bool Shared;
    }

    [ProtoContract]
    public class PanDepthPacket
    {
        [ProtoMember(1)] public List<PanChunkDepth> Chunks = new();
    }

    // Server truth, keyed by packed chunk coords; client mirror of what this client may see.
    private static readonly Dictionary<long, PanChunkDepth> serverDepth = new();
    private static readonly Dictionary<long, PanChunkDepth> clientDepth = new();

    private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    // ------------------------------------------------------------ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network.RegisterChannel("almanactcmpan").RegisterMessageType<PanDepthPacket>();
        api.Event.SaveGameLoaded += Load;
        api.Event.GameWorldSave += Save;
        api.Event.PlayerJoin += OnPlayerJoin;
    }

    public static void RegisterClient(ICoreClientAPI api)
    {
        capi = api;
        api.Network.RegisterChannel("almanactcmpan").RegisterMessageType<PanDepthPacket>()
            .SetMessageHandler<PanDepthPacket>(OnDepthPacket);
    }

    private static void Load()
    {
        try
        {
            byte[]? data = sapi!.WorldManager.SaveGame.GetData("almanacPanDepth");
            if (data == null) return;
            foreach (var cd in SerializerUtil.Deserialize<List<PanChunkDepth>>(data) ?? new())
                serverDepth[Key(cd.Cx, cd.Cz)] = cd;
            TcmLog.Cat(sapi, TcmLog.Config, $"PAN depth store loaded: {serverDepth.Count} surveyed column(s)");
        }
        catch (Exception e) { TcmLog.Error(sapi, $"pan depth store unreadable ({e.Message}); starting empty"); }
    }

    private static void Save()
    {
        sapi!.WorldManager.SaveGame.StoreData("almanacPanDepth",
            SerializerUtil.Serialize(new List<PanChunkDepth>(serverDepth.Values)));
    }

    private static void OnPlayerJoin(IServerPlayer player)
    {
        var mine = new List<PanChunkDepth>();
        foreach (var cd in serverDepth.Values)
            if (cd.Shared || cd.ProberUid == player.PlayerUID) mine.Add(cd);
        if (mine.Count > 0) serverChannel?.SendPacket(new PanDepthPacket { Chunks = mine }, player);
    }

    private static void OnDepthPacket(PanDepthPacket packet)
    {
        foreach (var cd in packet.Chunks) clientDepth[Key(cd.Cx, cd.Cz)] = cd;
    }

    // ------------------------------------------------------------ the depth read (server)

    /// <summary>Called for every recorded reading (PanPatches.DidProbePatch). From Master I,
    /// asks each read ore's own deposit generator for its Y range at this column and records
    /// the band as depth-below-surface. Also answers the surveyor in chat: the read should
    /// FEEL like rank, PT or not.</summary>
    public static void OnReading(PropickReading results, IServerPlayer splr)
    {
        if (sapi == null || results?.Position == null) return;
        if (PanDomain.LevelOf(splr) < MasterLevel) return;

        var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(sapi, "propickworkspace");
        if (ppws?.depositsByCode == null) return;

        int surfaceY = (int)results.Position.Y;
        var pos = new BlockPos((int)results.Position.X, surfaceY, (int)results.Position.Z);
        int[]? column = null;
        try { column = ppws.GetRockColumn(pos.X, pos.Z); } catch { }
        var bands = new List<PanOreBand>();

        foreach (var kv in results.OreReadings)
        {
            if (bands.Count >= MaxBandsPerReading) break;
            if (kv.Value.TotalFactor <= MentionThreshold) continue;
            if (!ppws.depositsByCode.TryGetValue(kv.Key, out var variant) || variant?.GeneratorInst == null) continue;
            try
            {
                variant.GeneratorInst.GetYMinMax(pos, out double miny, out double maxy);
                if (miny > maxy) continue; // generator answered with its "unknown" sentinel
                int yLo = (int)Math.Max(0, miny);
                int yHi = (int)Math.Min(surfaceY, maxy);
                if (yHi < yLo) continue;

                // IOG's "anywhere" discs span half the world in raw Y range — useless as a
                // band. Its generators expose their bearing rocks, so narrow the range to the
                // rows of THIS column that can actually carry the ore: the local truth.
                var bearingMethod = AccessTools.Method(variant.GeneratorInst.GetType(), "GetBearingBlocks");
                if (bearingMethod != null && column != null
                    && bearingMethod.Invoke(variant.GeneratorInst, null) is int[] bearing && bearing.Length > 0)
                {
                    var set = new HashSet<int>(bearing);
                    int lo = -1, hi = -1;
                    for (int y = yLo; y <= Math.Min(yHi, column.Length - 1); y++)
                    {
                        if (!set.Contains(column[y])) continue;
                        if (lo < 0) lo = y;
                        hi = y;
                    }
                    if (lo < 0) continue; // the carrying rock never crosses this column here
                    yLo = lo; yHi = hi;
                }

                int minDepth = Math.Max(0, surfaceY - yHi);
                int maxDepth = Math.Max(minDepth, surfaceY - yLo);
                if (maxDepth <= 0) continue;
                bool wide = maxDepth - minDepth > surfaceY * 0.8;
                bands.Add(new PanOreBand { OreKey = kv.Key, MinDepth = minDepth, MaxDepth = maxDepth, Wide = wide });
            }
            catch { /* a generator without placement math (throws NotImplemented); honest skip */ }
        }
        if (bands.Count == 0) return;

        int cx = (int)results.Position.X / 32, cz = (int)results.Position.Z / 32; // PT's own chunk mapping
        long key = Key(cx, cz);
        bool wasShared = serverDepth.TryGetValue(key, out var prev) && prev.Shared;
        var record = new PanChunkDepth { Cx = cx, Cz = cz, Bands = bands, ProberUid = splr.PlayerUID, Shared = wasShared };
        serverDepth[key] = record;

        serverChannel?.SendPacket(new PanDepthPacket { Chunks = new List<PanChunkDepth> { record } }, splr);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Lang.GetL(splr.LanguageCode, "almanactcm:depth-title"));
        foreach (var b in bands)
        {
            string ore = Lang.GetL(splr.LanguageCode, "ore-" + b.OreKey);
            sb.AppendLine(b.Wide
                ? Lang.GetL(splr.LanguageCode, "almanactcm:depth-read-wide", ore)
                : Lang.GetL(splr.LanguageCode, "almanactcm:depth-read", ore, b.MinDepth, b.MaxDepth));
        }
        splr.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString().TrimEnd(), EnumChatType.Notification);
        TcmLog.Cat(sapi, TcmLog.Hooks, $"PAN surveyor: {splr.PlayerName} recorded {bands.Count} depth band(s) at chunk {cx},{cz}");
    }

    // ------------------------------------------------------------ ProspectTogether bridge

    private static int ptAllGroupId = -1;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("prospecttogether")) return;

        var storage = AccessTools.TypeByName("ProspectTogether.Server.ServerStorage");
        var info = AccessTools.TypeByName("ProspectTogether.Shared.ProspectInfo");
        var constants = AccessTools.TypeByName("ProspectTogether.Shared.Constants");
        var shared = storage == null ? null : AccessTools.Method(storage, "PlayerSharedProspectingData");
        var request = storage == null ? null : AccessTools.Method(storage, "PlayerRequestsInfoForGroup");
        var getMsg = info == null ? null : AccessTools.Method(info, "GetMessage");

        if (shared == null || getMsg == null)
        {
            TcmLog.Warn(api, "prospecttogether present but its seams were not found; depth bands stay chat+own-map only");
            return;
        }
        try { ptAllGroupId = (int)(AccessTools.Field(constants, "ALL_GROUP_ID")?.GetValue(null) ?? -1); } catch { }

        harmony.Patch(shared, postfix: new HarmonyMethod(AccessTools.Method(typeof(PtSharePatch), "Postfix")));
        if (request != null) harmony.Patch(request, postfix: new HarmonyMethod(AccessTools.Method(typeof(PtRequestPatch), "Postfix")));
        harmony.Patch(getMsg, postfix: new HarmonyMethod(AccessTools.Method(typeof(PtTooltipPatch), "Postfix")));
        TcmLog.Info(api, "PAN Surveyor bridged to ProspectTogether (depth bands ride shares; tooltip shows them)");
    }

    /// <summary>A PT share carries our depth bands to the exact same recipients: everyone for
    /// the all-players group, the group's online members otherwise. Marks the columns shared
    /// so late joiners get them too.</summary>
    public static class PtSharePatch
    {
        public static void Postfix(IServerPlayer fromPlayer, object packet)
        {
            if (sapi == null || serverChannel == null || packet == null) return;
            var tr = Traverse.Create(packet);
            int groupId = tr.Field("GroupId").GetValue<int>();
            if (tr.Field("Data").GetValue() is not IEnumerable data) return;

            var carried = new List<PanChunkDepth>();
            foreach (object infoObj in data)
            {
                var chunk = Traverse.Create(infoObj).Field("Chunk");
                int cx = chunk.Field("X").GetValue<int>(), cz = chunk.Field("Z").GetValue<int>();
                if (serverDepth.TryGetValue(Key(cx, cz), out var cd))
                {
                    cd.Shared = true;
                    carried.Add(cd);
                }
            }
            if (carried.Count == 0) return;

            var pkt = new PanDepthPacket { Chunks = carried };
            if (groupId == ptAllGroupId)
            {
                serverChannel.BroadcastPacket(pkt);
            }
            else if (sapi.Groups.PlayerGroupsById.TryGetValue(groupId, out var group))
            {
                foreach (var member in group.OnlinePlayers)
                    if (member is IServerPlayer sp) serverChannel.SendPacket(pkt, sp);
            }
        }
    }

    /// <summary>A late joiner pulling group data gets every SHARED depth column.</summary>
    public static class PtRequestPatch
    {
        public static void Postfix(IServerPlayer fromPlayer)
        {
            if (serverChannel == null || fromPlayer == null) return;
            var sharedChunks = new List<PanChunkDepth>();
            foreach (var cd in serverDepth.Values) if (cd.Shared) sharedChunks.Add(cd);
            if (sharedChunks.Count > 0)
                serverChannel.SendPacket(new PanDepthPacket { Chunks = sharedChunks }, fromPlayer);
        }
    }

    /// <summary>The PT map tooltip grows the depth lines when this client holds the band for
    /// that column (own survey, or one that was shared to it).</summary>
    public static class PtTooltipPatch
    {
        public static void Postfix(object __instance, ref string __result)
        {
            if (capi == null || __instance == null) return;
            var chunk = Traverse.Create(__instance).Field("Chunk");
            int cx = chunk.Field("X").GetValue<int>(), cz = chunk.Field("Z").GetValue<int>();
            if (!clientDepth.TryGetValue(Key(cx, cz), out var cd) || cd.Bands.Count == 0) return;

            var sb = new System.Text.StringBuilder(__result.TrimEnd());
            sb.AppendLine();
            sb.AppendLine(Lang.Get("almanactcm:depth-title"));
            foreach (var b in cd.Bands)
            {
                string ore = Lang.Get("ore-" + b.OreKey);
                sb.AppendLine(b.Wide
                    ? Lang.Get("almanactcm:depth-read-wide", ore)
                    : Lang.Get("almanactcm:depth-read", ore, b.MinDepth, b.MaxDepth));
            }
            __result = sb.ToString();
        }
    }
}

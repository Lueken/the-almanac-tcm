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
    /// <summary>Master I: the rank where the drill starts speaking (ruled ladder).</summary>
    public const int MasterLevel = 13;

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

    /// <summary>Records MEASURED depth bands from a GM bore (the mode-workflow ruling
    /// 2026-07-17: the survey estimates, the drill measures — depth on the shared map is
    /// something a Grandmaster physically drilled, not a worldgen estimate). Keyed to PT's
    /// chunk mapping; rides shares exactly like before.</summary>
    public static void RecordBoreBands(IServerPlayer splr, BlockPos pos, List<PanOreBand> bands)
    {
        if (sapi == null || splr == null || bands == null || bands.Count == 0) return;

        int cx = pos.X / 32, cz = pos.Z / 32; // PT's own chunk mapping
        long key = Key(cx, cz);
        bool wasShared = serverDepth.TryGetValue(key, out var prev) && prev.Shared;
        var record = new PanChunkDepth { Cx = cx, Cz = cz, Bands = bands, ProberUid = splr.PlayerUID, Shared = wasShared };
        serverDepth[key] = record;

        serverChannel?.SendPacket(new PanDepthPacket { Chunks = new List<PanChunkDepth> { record } }, splr);
        TcmLog.Cat(sapi, TcmLog.Hooks, $"PAN surveyor: {splr.PlayerName} bored {bands.Count} depth band(s) at chunk {cx},{cz}");
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

    // ------------------------------------------------------------ map de-duplication (client)

    private const string VanillaOreLayerSetting = "almanactcmVanillaOreLayer";

    /// <summary>The Quire runs ProspectTogether as THE prospecting surface (it carries the
    /// fidelity-gated readings, the depth bands, and the trade economy), so the vanilla ore
    /// layer starts HIDDEN when PT is present: the two displays double every tooltip and can
    /// flatly contradict each other (PT heatmap red over vanilla's bright-green go-dig marker,
    /// live feedback 2026-07-17). The Prospecting map tab still works, and an explicit
    /// re-enable is remembered across sessions (vanilla's own tab toggles reset every launch).</summary>
    [HarmonyPatch(typeof(OreMapLayer), MethodType.Constructor, typeof(ICoreAPI), typeof(IWorldMapManager))]
    public static class HideVanillaOreLayerPatch
    {
        public static void Postfix(OreMapLayer __instance, ICoreAPI api)
        {
            if (api.Side != EnumAppSide.Client) return;
            if (!api.ModLoader.IsModEnabled("prospecttogether")) return;
            if (((ICoreClientAPI)api).Settings.Bool[VanillaOreLayerSetting]) return; // player opted back in
            __instance.Active = false;
        }
    }

    /// <summary>Remembers the player's explicit choice on the vanilla Prospecting tab, so an
    /// opt-in (or a later re-hide) survives relaunches.</summary>
    [HarmonyPatch(typeof(GuiDialogWorldMap), "OnTabClicked")]
    public static class RememberOreLayerTogglePatch
    {
        public static void Postfix(GuiDialogWorldMap __instance, int arg1, GuiTab tab)
        {
            if (capi == null) return;
            var tabnames = Traverse.Create(__instance).Field("tabnames").GetValue<List<string>>();
            if (tabnames == null || arg1 < 0 || arg1 >= tabnames.Count) return;
            if (tabnames[arg1] != "prospecting") return;
            capi.Settings.Bool[VanillaOreLayerSetting] = tab.Active;
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

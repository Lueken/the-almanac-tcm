using System;
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

namespace AlmanacTcm.Overlay;

/// <summary>
/// THE WORKED-GROUND OVERLAY — the shared identity spine of FOR (the Forager's Memory) and FIS
/// (the Angler's Read), ruled 2026-07-10/11 and built as one system because they are the same
/// architecture: a PRIVATE, per-player map of spots the player has actually worked, whose
/// fidelity climbs with rank, reading live hidden state the game already computes.
///
/// Built on the vanilla ORE MAP pattern (OreMapLayer): a MarkerMapLayer with DataSide=Server,
/// per-player spot lists persisted in savegame data, resent privately through the map system's
/// own SendMapDataToClient channel. No custom network channel; another player can never receive
/// your spots.
///
/// THE FIDELITY RULE (the whole point): the server DEGRADES each spot to the viewer's rank
/// before it ever leaves the server — a fuzzed centre and fat radius at Apprentice, tightening
/// through Journeyman/Master, exact with a regrow/recovery ETA at GM. Below Apprentice nothing
/// is sent at all (spots are still recorded; the map is the earned capability). Hidden values
/// never reach the client (the T1.0 hidden-values rule).
///
/// Divination audit (ruled ACCEPTED for both domains): only spots the player personally worked,
/// reading real regrow/depletion state, never a radar for unvisited ground.
/// </summary>
public class AlmanacSpotsLayer : MarkerMapLayer
{
    // ------------------------------------------------------------ data model

    public enum SpotKind { Mushroom = 0, Bush = 1, Water = 2 }

    /// <summary>Server-side truth: exact position, recorded once per patch/spot.</summary>
    [ProtoContract]
    public class WorkedSpot
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public int Kind;
    }

    /// <summary>What the client is allowed to see: already fuzzed + fidelity-trimmed.</summary>
    [ProtoContract]
    public class SpotView
    {
        [ProtoMember(1)] public double X;
        [ProtoMember(2)] public double Y;
        [ProtoMember(3)] public double Z;
        [ProtoMember(4)] public int Kind;
        [ProtoMember(5)] public float RadiusBlocks;
        /// <summary>0 = not shown at this rank; 1 = resting/regrowing/low; 2 = ready/healthy.</summary>
        [ProtoMember(6)] public int State;
        /// <summary>Fish stock percent 0..100, or -1 when hidden at this rank.</summary>
        [ProtoMember(7)] public float StockPct = -1;
        /// <summary>Days until ready/recovered, or -1 when hidden (GM only).</summary>
        [ProtoMember(8)] public float EtaDays = -1;
    }

    // ------------------------------------------------------------ server state

    private readonly Dictionary<string, List<WorkedSpot>> spotsByPlayer = new();
    private ICoreServerAPI? sapi;
    private const int MaxSpotsPerPlayer = 300;

    // Client state
    private List<SpotView> ownSpots = new();
    private readonly List<MapComponent> comps = new();
    private ICoreClientAPI? capi;
    public LoadedTexture? circleTexture;
    public MeshRef? quadModel;

    public override bool RequireChunkLoaded => false;
    public override string Title => Lang.Get("almanactcm:maplayer-workedground");
    public override EnumMapAppSide DataSide => EnumMapAppSide.Server;
    public override string LayerGroupCode => "almanacworkedground";

    public AlmanacSpotsLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        if (api.Side == EnumAppSide.Server)
        {
            sapi = (ICoreServerAPI)api;
            sapi.Event.GameWorldSave += OnSave;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
            Instance = this;
        }
        else
        {
            capi = (ICoreClientAPI)api;
            quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
            var iconAsset = api.Assets.Get("textures/icons/worldmap/0-circle.svg");
            int size = (int)Math.Ceiling(64 * RuntimeEnv.GUIScale);
            circleTexture = capi.Gui.LoadSvg(iconAsset.Location, size, size, size, size, ColorUtil.WhiteArgb);
        }
    }

    /// <summary>Server-side singleton for the recorder statics (set in ctor, one layer per side).</summary>
    public static AlmanacSpotsLayer? Instance { get; private set; }

    // ------------------------------------------------------------ recording (server)

    /// <summary>Records a worked spot once. Dedup: water by the vanilla 8-block depletion cell,
    /// mushrooms by the mycelium grow range (7), bushes by exact block.</summary>
    public void Record(IPlayer player, BlockPos pos, SpotKind kind)
    {
        if (sapi == null || player == null) return;
        var list = GetOrLoad(player.PlayerUID);

        int dedupe = kind == SpotKind.Bush ? 1 : kind == SpotKind.Mushroom ? 7 : 8;
        foreach (var s in list)
        {
            if (s.Kind != (int)kind) continue;
            if (Math.Abs(s.X - pos.X) <= dedupe && Math.Abs(s.Z - pos.Z) <= dedupe && Math.Abs(s.Y - pos.Y) <= 4)
                return; // already known
        }

        // Mushrooms: anchor the patch on the hidden mycelium BE when it can be found nearby, so
        // the GM read points at the true regrow source, not the one cap that got picked.
        if (kind == SpotKind.Mushroom)
        {
            BlockPos? root = FindMyceliumNear(pos);
            if (root != null) pos = root;
        }

        list.Add(new WorkedSpot { X = pos.X, Y = pos.Y, Z = pos.Z, Kind = (int)kind });
        if (list.Count > MaxSpotsPerPlayer) list.RemoveAt(0);
    }

    private BlockPos? FindMyceliumNear(BlockPos pos)
    {
        var ba = sapi!.World.BlockAccessor;
        BlockPos probe = new(0);
        for (int dy = -2; dy <= 1; dy++)
            for (int dx = -7; dx <= 7; dx++)
                for (int dz = -7; dz <= 7; dz++)
                {
                    probe.Set(pos.X + dx, pos.Y + dy, pos.Z + dz);
                    if (ba.GetBlockEntity(probe) is BlockEntityMycelium) return probe.Copy();
                }
        return null;
    }

    private List<WorkedSpot> GetOrLoad(string uid)
    {
        if (spotsByPlayer.TryGetValue(uid, out var list)) return list;
        byte[]? data = sapi!.WorldManager.SaveGame.GetData("almanacSpots-" + uid);
        return spotsByPlayer[uid] = data == null
            ? new List<WorkedSpot>()
            : SerializerUtil.Deserialize<List<WorkedSpot>>(data);
    }

    private void OnSave()
    {
        foreach (var kv in spotsByPlayer)
            sapi!.WorldManager.SaveGame.StoreData("almanacSpots-" + kv.Key, SerializerUtil.Serialize(kv.Value));
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        try
        {
            if (spotsByPlayer.TryGetValue(player.PlayerUID, out var list))
            {
                sapi!.WorldManager.SaveGame.StoreData("almanacSpots-" + player.PlayerUID, SerializerUtil.Serialize(list));
                spotsByPlayer.Remove(player.PlayerUID);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------ fidelity + sync (server)

    public override void OnViewChangedServer(IServerPlayer fromPlayer, int x1, int z1, int x2, int z2)
    {
        if (sapi == null) return;
        var views = BuildViews(fromPlayer);
        mapSink.SendMapDataToClient(this, fromPlayer, SerializerUtil.Serialize(views));
    }

    /// <summary>Tier thresholds: below Apprentice I (level 5) a domain's spots are not sent at
    /// all. The curve is the ruled ladder: fuzzy circle -> tightens + state -> exact + ETA.</summary>
    private List<SpotView> BuildViews(IServerPlayer player)
    {
        var result = new List<SpotView>();
        int forLevel = Domains.ForDomain.LevelOf(player);
        int fisLevel = Domains.FisDomain.LevelOf(player);
        double nowDays = sapi!.World.Calendar.TotalDays;

        foreach (var spot in GetOrLoad(player.PlayerUID))
        {
            var kind = (SpotKind)spot.Kind;
            int level = kind == SpotKind.Water ? fisLevel : forLevel;
            if (level < 5) continue; // pre-Apprentice: the memory has not formed yet

            int tier = (level - 1) / 4; // 1=Apprentice ... 4=GM(terminal)
            var view = new SpotView { Kind = spot.Kind, Y = spot.Y };

            // Centre fuzz + radius by tier. The fuzz is DETERMINISTIC per spot so the circle
            // does not wander between map opens.
            float radius; int fuzz;
            switch (tier)
            {
                case 1: radius = kind == SpotKind.Water ? 24 : 20; fuzz = 10; break;
                case 2: radius = kind == SpotKind.Water ? 14 : 12; fuzz = 4; break;
                case 3: radius = kind == SpotKind.Water ? 9 : 8; fuzz = 1; break;
                default: radius = kind == SpotKind.Water ? 8 : (kind == SpotKind.Mushroom ? 7 : 4); fuzz = 0; break;
            }
            int hash = HashCode.Combine(spot.X, spot.Y, spot.Z, player.PlayerUID);
            view.X = spot.X + 0.5 + (fuzz == 0 ? 0 : (hash % (fuzz * 2 + 1)) - fuzz);
            view.Z = spot.Z + 0.5 + (fuzz == 0 ? 0 : ((hash / 31) % (fuzz * 2 + 1)) - fuzz);
            view.RadiusBlocks = radius;

            // State / stock / ETA by tier and kind.
            if (kind == SpotKind.Water)
            {
                (int state, float stockPct, float etaDays) = ReadWater(spot, nowDays);
                view.State = state; // Apprentice+ gets the coarse verdict (the ruled read)
                if (tier >= 2) view.StockPct = stockPct;
                if (tier >= 4) view.EtaDays = etaDays;
            }
            else
            {
                if (tier >= 2) view.State = ReadPatchState(spot, out float eta);
                if (tier >= 4 && view.State == 1)
                {
                    ReadPatchState(spot, out float eta2);
                    view.EtaDays = eta2;
                }
            }

            result.Add(view);
        }
        return result;
    }

    // ---- live state reads (server, at resend time only) ----

    private static readonly AccessTools.FieldRef<object, Vec3i[]>? offsetsRef =
        TryFieldRef<Vec3i[]>("grownMushroomOffsets");
    private static readonly AccessTools.FieldRef<object, double>? diedDaysRef =
        TryFieldRef<double>("mushroomsDiedTotalDays");
    private static readonly AccessTools.FieldRef<object, double>? growingDaysRef =
        TryFieldRef<double>("growingDays");

    private static AccessTools.FieldRef<object, T>? TryFieldRef<T>(string field)
    {
        try { return AccessTools.FieldRefAccess<T>(typeof(BlockEntityMycelium), field); }
        catch { return null; }
    }

    /// <summary>Patch state: 2 = ready (caps up / bush ripe), 1 = resting/regrowing. ETA is only
    /// meaningful for mushrooms (bush ripeness is vanilla-visible on hover, ruled redundant).</summary>
    private int ReadPatchState(WorkedSpot spot, out float etaDays)
    {
        etaDays = -1;
        var ba = sapi!.World.BlockAccessor;
        var pos = new BlockPos(spot.X, spot.Y, spot.Z);

        if (spot.Kind == (int)SpotKind.Mushroom)
        {
            if (ba.GetBlockEntity(pos) is not BlockEntityMycelium myc) return 1;
            if (offsetsRef != null && offsetsRef(myc).Length > 0) return 2;
            if (diedDaysRef != null && growingDaysRef != null)
            {
                double eta = growingDaysRef(myc) - (sapi.World.Calendar.TotalDays - diedDaysRef(myc));
                etaDays = (float)Math.Max(0, Math.Min(eta, 60));
            }
            return 1;
        }

        // Bush: ready when any fruiting-bush behavior at the spot reads Ripe.
        var be = ba.GetBlockEntity(pos);
        var bush = be?.GetBehavior<BEBehaviorFruitingBush>();
        if (bush?.BState == null) return 1;
        return bush.BState.Growthstate == EnumFruitingBushGrowthState.Ripe ? 2 : 1;
    }

    private static readonly AccessTools.FieldRef<ModSystemFishDepletion, Dictionary<BlockPos, CreatureHarvest>>? fishDictRef =
        TryFishDict();

    private static AccessTools.FieldRef<ModSystemFishDepletion, Dictionary<BlockPos, CreatureHarvest>>? TryFishDict()
    {
        try { return AccessTools.FieldRefAccess<ModSystemFishDepletion, Dictionary<BlockPos, CreatureHarvest>>("harvestedLocations"); }
        catch { return null; }
    }

    /// <summary>Water state off the vanilla depletion counter: stock% = 1 - quantity/cap; the
    /// GM ETA is the 14-day rest clock minus days since the last harvest there.</summary>
    private (int state, float stockPct, float etaDays) ReadWater(WorkedSpot spot, double nowDays)
    {
        var depletion = sapi!.ModLoader.GetModSystem<ModSystemFishDepletion>();
        var pos = new BlockPos(spot.X, spot.Y, spot.Z);
        float harvested = depletion?.GetHarvestAmount(pos) ?? 0;
        float stock = GameMath.Clamp(1f - harvested / ModSystemFishDepletion.MaxHarvestablePerLocation, 0f, 1f);

        float eta = 0;
        if (harvested > 0 && depletion != null && fishDictRef != null)
        {
            var dict = fishDictRef(depletion);
            if (dict.TryGetValue(pos / depletion.Scale, out var harvest))
            {
                eta = (float)Math.Max(0, ModSystemFishDepletion.RestoreFishAfterDays - (nowDays - harvest.TotalDays));
            }
        }
        return (stock < 0.5f ? 1 : 2, stock * 100f, eta);
    }

    // ------------------------------------------------------------ client

    public override void OnDataFromServer(byte[] data)
    {
        ownSpots = SerializerUtil.Deserialize<List<SpotView>>(data) ?? new List<SpotView>();
        RebuildMapComponents();
    }

    public override void OnMapOpenedClient() => RebuildMapComponents();

    private void RebuildMapComponents()
    {
        if (!mapSink.IsOpened || capi == null) return;
        foreach (var c in comps) c.Dispose();
        comps.Clear();
        foreach (var view in ownSpots) comps.Add(new SpotMapComponent(view, this, capi));
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active) return;
        foreach (var c in comps) c.Render(mapElem, dt);
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, System.Text.StringBuilder hoverText)
    {
        if (!Active) return;
        foreach (var c in comps) c.OnMouseMove(args, mapElem, hoverText);
    }

    public override void Dispose()
    {
        circleTexture?.Dispose();
        quadModel?.Dispose();
        base.Dispose();
    }
}

/// <summary>One translucent circle: colour by kind+state, pixel radius derived from the world
/// radius at the current zoom, quiet alpha so the map stays readable underneath.</summary>
public class SpotMapComponent : MapComponent
{
    private readonly AlmanacSpotsLayer.SpotView view;
    private readonly AlmanacSpotsLayer layer;
    private readonly Vec3d worldPos;
    private readonly Vec4f color = new();
    private Vec2f viewPos = new();
    private Vec2f edgePos = new();
    private readonly Matrixf mvMat = new();

    public SpotMapComponent(AlmanacSpotsLayer.SpotView view, AlmanacSpotsLayer layer, ICoreClientAPI capi) : base(capi)
    {
        this.view = view;
        this.layer = layer;
        worldPos = new Vec3d(view.X, view.Y, view.Z);

        // FOR greens/ambers on paper; FIS blue/red. Ready/healthy = the fuller colour.
        int rgb = view.Kind == (int)AlmanacSpotsLayer.SpotKind.Water
            ? (view.State == 1 ? ColorUtil.ToRgba(255, 60, 90, 170) : ColorUtil.ToRgba(255, 200, 140, 60))
            : (view.State == 2 ? ColorUtil.ToRgba(255, 80, 160, 80) : ColorUtil.ToRgba(255, 90, 150, 190));
        ColorUtil.ToRGBAVec4f(rgb, ref color);
        color.W = 0.38f;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        map.TranslateWorldPosToViewPos(worldPos, ref viewPos);
        if (viewPos.X < -300 || viewPos.Y < -300
            || viewPos.X > map.Bounds.OuterWidth + 300 || viewPos.Y > map.Bounds.OuterHeight + 300) return;

        // Pixel radius from the world radius: translate a point radius-blocks east and diff.
        map.TranslateWorldPosToViewPos(new Vec3d(view.X + view.RadiusBlocks, view.Y, view.Z), ref edgePos);
        float pixelRadius = Math.Max(6, Math.Abs(edgePos.X - viewPos.X));

        float x = (float)(map.Bounds.renderX + viewPos.X);
        float y = (float)(map.Bounds.renderY + viewPos.Y);

        var api = map.Api;
        var prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("rgbaIn", color);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.Uniform("noTexture", 0f);

        var tex = layer.circleTexture;
        if (tex == null || layer.quadModel == null) return;
        prog.BindTexture2D("tex2d", tex.TextureId, 0);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        mvMat
            .Set(api.Render.CurrentModelviewMatrix)
            .Translate(x, y, 60)
            .Scale(pixelRadius * 2, pixelRadius * 2, 0)
            .Scale(0.5f, 0.5f, 0);
        prog.UniformMatrix("modelViewMatrix", mvMat.Values);
        api.Render.RenderMesh(layer.quadModel);
    }

    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, System.Text.StringBuilder hoverText)
    {
        mapElem.TranslateWorldPosToViewPos(worldPos, ref viewPos);
        double dx = args.X - (viewPos.X + mapElem.Bounds.renderX);
        double dy = args.Y - (viewPos.Y + mapElem.Bounds.renderY);
        mapElem.TranslateWorldPosToViewPos(new Vec3d(view.X + view.RadiusBlocks, view.Y, view.Z), ref edgePos);
        float pixelRadius = Math.Max(6, Math.Abs(edgePos.X - viewPos.X));
        if (dx * dx + dy * dy > pixelRadius * pixelRadius) return;

        string line;
        if (view.Kind == (int)AlmanacSpotsLayer.SpotKind.Water)
        {
            line = view.State == 1 ? Lang.Get("almanactcm:spot-water-low") : Lang.Get("almanactcm:spot-water-healthy");
            if (view.StockPct >= 0) line += " " + Lang.Get("almanactcm:spot-stock", (int)view.StockPct);
            if (view.EtaDays > 0) line += " " + Lang.Get("almanactcm:spot-eta", (int)Math.Ceiling(view.EtaDays));
        }
        else
        {
            string what = view.Kind == (int)AlmanacSpotsLayer.SpotKind.Mushroom
                ? Lang.Get("almanactcm:spot-mushrooms") : Lang.Get("almanactcm:spot-berries");
            line = view.State == 2 ? Lang.Get("almanactcm:spot-ready", what)
                 : view.State == 1 ? Lang.Get("almanactcm:spot-regrowing", what)
                 : Lang.Get("almanactcm:spot-somewhere", what);
            if (view.EtaDays > 0) line += " " + Lang.Get("almanactcm:spot-eta", (int)Math.Ceiling(view.EtaDays));
        }
        hoverText.AppendLine(line);
    }
}

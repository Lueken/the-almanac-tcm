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
        /// <summary>Display names of what was picked here (a folded patch can hold several).
        /// The world keeps the promise: mycelium regrows its own species, bushes their own berry.</summary>
        [ProtoMember(5)] public List<string>? Names;
        /// <summary>Horizontal footprint (blocks from anchor) learned from mycelium fruiting
        /// offsets and folded repeat picks, so precise circles cover the TRUE patch, not a dot.</summary>
        [ProtoMember(6)] public int Reach;
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
        /// <summary>Fidelity tier the server rendered this at (1 Apprentice .. 4 GM), so the
        /// client can phrase the hover without ever knowing the underlying values.</summary>
        [ProtoMember(9)] public int Tier;
        /// <summary>What was picked here. No tier gate: the player picked it, they remember it.
        /// The LOCATION precision stays the rank reward.</summary>
        [ProtoMember(10)] public List<string>? Names;
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
    /// mushrooms by the mycelium grow range (7), bushes by exact block. Repeat picks inside a
    /// known patch still teach it something: new species names fold in and the footprint widens
    /// to cover where the pick actually happened.</summary>
    public void Record(IPlayer player, BlockPos pos, SpotKind kind, string? name = null)
    {
        if (sapi == null || player == null) return;
        var list = GetOrLoad(player.PlayerUID);

        int dedupe = kind == SpotKind.Bush ? 6 : kind == SpotKind.Mushroom ? 7 : 8;
        foreach (var s in list)
        {
            if (s.Kind != (int)kind) continue;
            if (Math.Abs(s.X - pos.X) <= dedupe && Math.Abs(s.Z - pos.Z) <= dedupe && Math.Abs(s.Y - pos.Y) <= 4)
            {
                AddName(s, name);
                s.Reach = Math.Max(s.Reach, Math.Max(Math.Abs(s.X - pos.X), Math.Abs(s.Z - pos.Z)));
                return; // already known
            }
        }

        // Mushrooms: anchor the patch on the hidden mycelium BE when it can be found nearby, so
        // the GM read points at the true regrow source, not the one cap that got picked. The
        // network's current fruiting offsets seed the footprint.
        int reach = 0;
        if (kind == SpotKind.Mushroom)
        {
            BlockPos? root = FindMyceliumNear(pos);
            if (root != null)
            {
                reach = Math.Max(Math.Abs(root.X - pos.X), Math.Abs(root.Z - pos.Z));
                if (offsetsRef != null && sapi.World.BlockAccessor.GetBlockEntity(root) is BlockEntityMycelium myc)
                {
                    foreach (var off in offsetsRef(myc))
                        reach = Math.Max(reach, Math.Max(Math.Abs(off.X), Math.Abs(off.Z)));
                }
                pos = root;
            }
        }

        var spot = new WorkedSpot { X = pos.X, Y = pos.Y, Z = pos.Z, Kind = (int)kind, Reach = reach };
        AddName(spot, name);
        list.Add(spot);
        if (list.Count > MaxSpotsPerPlayer) list.RemoveAt(0);
    }

    private static void AddName(WorkedSpot spot, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        spot.Names ??= new List<string>();
        if (spot.Names.Count < 6 && !spot.Names.Contains(name)) spot.Names.Add(name);
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
        var loaded = data == null ? new List<WorkedSpot>() : SerializerUtil.Deserialize<List<WorkedSpot>>(data);
        CollapseNearDuplicates(loaded); // one-time cleanup of pre-patch-dedupe recordings
        return spotsByPlayer[uid] = loaded;
    }

    private static void CollapseNearDuplicates(List<WorkedSpot> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            var a = list[i];
            int dedupe = a.Kind == (int)SpotKind.Bush ? 6 : a.Kind == (int)SpotKind.Mushroom ? 7 : 8;
            for (int j = 0; j < i; j++)
            {
                var b = list[j];
                if (a.Kind == b.Kind && Math.Abs(a.X - b.X) <= dedupe && Math.Abs(a.Z - b.Z) <= dedupe
                    && Math.Abs(a.Y - b.Y) <= 4)
                {
                    b.Reach = Math.Max(b.Reach, Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z)));
                    if (a.Names != null) foreach (var nm in a.Names) AddName(b, nm);
                    list.RemoveAt(i);
                    break;
                }
            }
        }
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
            var view = new SpotView { Kind = spot.Kind, Y = spot.Y, Names = spot.Names };

            // State / stock / ETA by tier and kind. Read BEFORE the radius so the mushroom
            // state read's live footprint refresh (mycelium fruiting offsets) lands this rebuild.
            if (kind == SpotKind.Water)
            {
                (int state, float stockPct, float etaDays) = ReadWater(spot, nowDays);
                view.State = state; // Apprentice+ gets the coarse verdict (the ruled read)
                if (tier >= 2) view.StockPct = stockPct;
                // The GM forecast only matters when the water is actually worn down; a near-full
                // spot nagging "ready in 14 days" reads as a warning it is not (live feedback).
                if (tier >= 4 && stockPct <= 40f) view.EtaDays = etaDays;
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
            // Precision never lies: a tight high-rank circle still covers the patch's TRUE
            // footprint (mycelium fruiting spread / folded picks), instead of converging on the
            // anchor and leaving the fringe caps outside (live feedback at Master+).
            radius = Math.Max(radius, spot.Reach + 2);
            // Deterministic fuzz keyed to the 16-block AREA + player, not the exact spot: two
            // patches a few blocks apart drift the SAME way instead of flying apart, and the
            // magnitude is capped so the true spot always sits inside the circle.
            fuzz = Math.Min(fuzz, Math.Max(0, (int)radius - 6));
            int hash = Math.Abs(HashCode.Combine(spot.X >> 4, spot.Z >> 4, player.PlayerUID));
            view.X = spot.X + 0.5 + (fuzz == 0 ? 0 : (hash % (fuzz * 2 + 1)) - fuzz);
            view.Z = spot.Z + 0.5 + (fuzz == 0 ? 0 : ((hash / 31) % (fuzz * 2 + 1)) - fuzz);
            view.RadiusBlocks = radius;
            view.Tier = tier;

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
            if (offsetsRef != null)
            {
                // Opportunistic footprint learning: the network's fruiting spread IS the patch.
                foreach (var off in offsetsRef(myc))
                    spot.Reach = Math.Max(spot.Reach, Math.Max(Math.Abs(off.X), Math.Abs(off.Z)));
                if (offsetsRef(myc).Length > 0) return 2;
            }
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
        foreach (var (view, count) in Cluster(ownSpots)) comps.Add(new SpotMapComponent(view, count, this, capi));
    }

    /// <summary>Merges same-kind circles that overlap into one blob (ruled from live feedback:
    /// several worked patches in an area should read as one larger stretch of known ground, not
    /// stacked discs). Higher rank means smaller circles, fewer overlaps, more defined spots —
    /// the fidelity ladder expressed spatially for free.</summary>
    private static List<(SpotView view, int count)> Cluster(List<SpotView> views)
    {
        int n = views.Count;
        int[] group = new int[n];
        for (int i = 0; i < n; i++) group[i] = i;
        int Find(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (views[i].Kind != views[j].Kind) continue;
                double dx = views[i].X - views[j].X, dz = views[i].Z - views[j].Z;
                double reach = views[i].RadiusBlocks + views[j].RadiusBlocks;
                if (dx * dx + dz * dz <= reach * reach) group[Find(i)] = Find(j);
            }

        var byGroup = new Dictionary<int, List<SpotView>>();
        for (int i = 0; i < n; i++)
        {
            int g = Find(i);
            (byGroup.TryGetValue(g, out var l) ? l : byGroup[g] = new List<SpotView>()).Add(views[i]);
        }

        var result = new List<(SpotView, int)>();
        foreach (var members in byGroup.Values)
        {
            if (members.Count == 1) { result.Add((members[0], 1)); continue; }
            var blob = new SpotView { Kind = members[0].Kind, Tier = members[0].Tier, StockPct = -1, EtaDays = -1 };
            foreach (var m in members) { blob.X += m.X; blob.Y += m.Y; blob.Z += m.Z; }
            blob.X /= members.Count; blob.Y /= members.Count; blob.Z /= members.Count;
            float radius = 0;
            foreach (var m in members)
            {
                double dx = m.X - blob.X, dz = m.Z - blob.Z;
                radius = Math.Max(radius, (float)Math.Sqrt(dx * dx + dz * dz) + m.RadiusBlocks);
                blob.State = Math.Max(blob.State, m.State); // any ready patch marks the blob ready
                blob.Tier = Math.Max(blob.Tier, m.Tier);
                if (m.StockPct >= 0) blob.StockPct = blob.StockPct < 0 ? m.StockPct : Math.Min(blob.StockPct, m.StockPct);
                blob.EtaDays = Math.Max(blob.EtaDays, m.EtaDays);
                if (m.Names != null)
                {
                    blob.Names ??= new List<string>();
                    foreach (var nm in m.Names)
                        if (blob.Names.Count < 6 && !blob.Names.Contains(nm)) blob.Names.Add(nm);
                }
            }
            blob.RadiusBlocks = radius;
            result.Add((blob, members.Count));
        }
        return result;
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
    private readonly int count;
    private readonly Vec3d worldPos;
    private readonly Vec4f color = new();
    private Vec2f viewPos = new();
    private Vec2f edgePos = new();
    private readonly Matrixf mvMat = new();

    public SpotMapComponent(AlmanacSpotsLayer.SpotView view, int count, AlmanacSpotsLayer layer, ICoreClientAPI capi) : base(capi)
    {
        this.view = view;
        this.count = count;
        this.layer = layer;
        worldPos = new Vec3d(view.X, view.Y, view.Z);

        // ONE learnable colour per kind (live feedback ruling): berries crimson, mushrooms
        // amber, waters blue. State rides intensity, never hue: ready/healthy is full-strength,
        // resting/thin is the same colour sat down, so the legend stays one-glance learnable.
        int rgb = view.Kind switch
        {
            (int)AlmanacSpotsLayer.SpotKind.Bush => ColorUtil.ToRgba(255, 215, 40, 80),      // berry crimson
            (int)AlmanacSpotsLayer.SpotKind.Mushroom => ColorUtil.ToRgba(255, 240, 150, 40), // cap amber
            _ => ColorUtil.ToRgba(255, 45, 130, 230),                                        // water blue
        };
        ColorUtil.ToRGBAVec4f(rgb, ref color);
        color.W = view.State == 2 ? 0.60f : view.State == 1 ? 0.40f : 0.50f;
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
            if (view.EtaDays > 0) line += " " + Lang.Get("almanactcm:spot-water-eta", (int)Math.Ceiling(view.EtaDays));
            if (view.Tier >= 3 && view.EtaDays <= 0 && view.StockPct >= 0)
                line += " " + Lang.Get("almanactcm:spot-precise");
        }
        else
        {
            string what = view.Kind == (int)AlmanacSpotsLayer.SpotKind.Mushroom
                ? Lang.Get("almanactcm:spot-mushrooms") : Lang.Get("almanactcm:spot-berries");
            // The phrasing itself climbs with rank, so the hover visibly changes as you level
            // even when the patch state does not.
            line = view.Tier switch
            {
                1 => Lang.Get("almanactcm:spot-somewhere", what),
                2 => view.State == 2 ? Lang.Get("almanactcm:spot-ready", what) : Lang.Get("almanactcm:spot-regrowing", what),
                3 => (view.State == 2 ? Lang.Get("almanactcm:spot-ready", what) : Lang.Get("almanactcm:spot-regrowing", what))
                     + " " + Lang.Get("almanactcm:spot-precise"),
                _ => (view.State == 2 ? Lang.Get("almanactcm:spot-ready", what) : Lang.Get("almanactcm:spot-resting-exact", what)),
            };
            if (view.EtaDays > 0) line += " " + Lang.Get("almanactcm:spot-eta", (int)Math.Ceiling(view.EtaDays));

            // What was picked here, no tier gate: you picked it, you remember it.
            if (view.Names != null && view.Names.Count > 0)
            {
                var shown = view.Names.Count > 3 ? view.Names.GetRange(0, 3) : view.Names;
                string joined = string.Join(", ", shown);
                if (view.Names.Count > 3) joined += " " + Lang.Get("almanactcm:spot-names-more");
                line += " " + Lang.Get("almanactcm:spot-names", joined);
            }
        }
        if (count > 1) line += " " + Lang.Get("almanactcm:spot-count", count);
        hoverText.AppendLine(line);
    }
}

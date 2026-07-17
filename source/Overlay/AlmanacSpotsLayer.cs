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
        /// <summary>Packed pick positions relative to the anchor ((dx+512)&lt;&lt;10 | dz+512),
        /// recorded for bushes and waters: the GM outline is built from where the work actually
        /// happened. Mushroom outlines read the live network instead.</summary>
        [ProtoMember(7)] public List<int>? Cells;
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
        /// <summary>GM outline cells, absolute cell indices packed (cx&lt;&lt;32 | (uint)cz) at
        /// CellSize blocks per cell. Null below GM or when the live read was unavailable.</summary>
        [ProtoMember(11)] public List<long>? Cells;
        [ProtoMember(12)] public int CellSize = 4;
        /// <summary>Caps verified standing in the world right now (GM mushrooms), -1 = not sent.</summary>
        [ProtoMember(13)] public int CapsStanding = -1;
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
                if (kind != SpotKind.Mushroom) AddCell(s, pos);
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
        if (kind != SpotKind.Mushroom) AddCell(spot, pos);
        list.Add(spot);
        if (list.Count > MaxSpotsPerPlayer) list.RemoveAt(0);
    }

    private static void AddName(WorkedSpot spot, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        spot.Names ??= new List<string>();
        if (spot.Names.Count < 6 && !spot.Names.Contains(name)) spot.Names.Add(name);
    }

    private static void AddCell(WorkedSpot spot, BlockPos pos)
    {
        int dx = pos.X - spot.X, dz = pos.Z - spot.Z;
        if (dx < -511 || dx > 511 || dz < -511 || dz > 511) return;
        int packed = ((dx + 512) << 10) | (dz + 512);
        spot.Cells ??= new List<int>();
        if (spot.Cells.Count < 48 && !spot.Cells.Contains(packed)) spot.Cells.Add(packed);
    }

    private static long PackCell(int cx, int cz) => ((long)cx << 32) | (uint)cz;

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
                    if (a.Cells != null)
                    {
                        foreach (int packed in a.Cells)
                        {
                            int ax = a.X + ((packed >> 10) & 0x3ff) - 512;
                            int az = a.Z + (packed & 0x3ff) - 512;
                            AddCell(b, new BlockPos(ax, a.Y, az));
                        }
                    }
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
                if (tier >= 4)
                {
                    // The forecast only matters when the water is actually worn down; a near-full
                    // spot nagging "ready in 14 days" reads as a warning it is not (live feedback).
                    if (stockPct <= 40f) view.EtaDays = etaDays;
                    // The GM outline for water IS the vanilla depletion bucket: the true 8-block
                    // cell the game tracks stock in.
                    view.CellSize = 8;
                    view.Cells = new List<long> { PackCell(spot.X >> 3, spot.Z >> 3) };
                }
            }
            else if (kind == SpotKind.Mushroom)
            {
                if (tier >= 2)
                {
                    (int state, int caps, float eta, List<long>? cells) = ReadMushroom(spot);
                    view.State = state;
                    if (tier >= 4)
                    {
                        view.CapsStanding = caps;
                        if (state == 1 && eta >= 0) view.EtaDays = eta;
                        view.Cells = cells;
                    }
                }
            }
            else // bush
            {
                if (tier >= 2) view.State = ReadBush(spot);
                if (tier >= 4 && spot.Cells != null)
                {
                    var cells = new List<long>();
                    foreach (int packed in spot.Cells)
                    {
                        int ax = spot.X + ((packed >> 10) & 0x3ff) - 512;
                        int az = spot.Z + (packed & 0x3ff) - 512;
                        long cell = PackCell(ax >> 2, az >> 2);
                        if (!cells.Contains(cell)) cells.Add(cell);
                    }
                    view.Cells = cells;
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
    private static readonly AccessTools.FieldRef<object, double>? growingDaysRef =
        TryFieldRef<double>("growingDays");
    private static readonly AccessTools.FieldRef<object, double>? growingProgressRef =
        TryFieldRef<double>("mushroomsGrowingDays");
    private static readonly AccessTools.FieldRef<object, AssetLocation>? mushroomCodeRef =
        TryFieldRef<AssetLocation>("mushroomBlockCode");

    private static AccessTools.FieldRef<object, T>? TryFieldRef<T>(string field)
    {
        try { return AccessTools.FieldRefAccess<T>(typeof(BlockEntityMycelium), field); }
        catch { return null; }
    }

    /// <summary>Mushroom truth pass. Vanilla prunes its offsets list LAZILY (a scan at most every
    /// 0.1 game-days, skipped entirely while any offset chunk is unloaded), so a freshly picked
    /// patch keeps claiming caps for minutes. Every claimed cap is therefore verified against the
    /// actual world block: ready means standing RIGHT NOW, and the verified caps are the GM
    /// outline cells. The regrow ETA reads the network's growing-progress clock (10-20 warm days
    /// per network; cold days pause it), NOT the died-day stamp, which vanilla only writes on
    /// natural death and never on player picks.</summary>
    private (int state, int caps, float etaDays, List<long>? cells) ReadMushroom(WorkedSpot spot)
    {
        var ba = sapi!.World.BlockAccessor;
        var pos = new BlockPos(spot.X, spot.Y, spot.Z);
        if (ba.GetBlockEntity(pos) is not BlockEntityMycelium myc) return (1, -1, -1, null);

        var cells = new List<long> { PackCell(spot.X >> 2, spot.Z >> 2) };
        int caps = 0;
        if (offsetsRef != null)
        {
            AssetLocation? code = mushroomCodeRef?.Invoke(myc);
            var mpos = new BlockPos(0);
            foreach (var off in offsetsRef(myc))
            {
                // Opportunistic footprint learning: the network's fruiting spread IS the patch.
                spot.Reach = Math.Max(spot.Reach, Math.Max(Math.Abs(off.X), Math.Abs(off.Z)));
                mpos.Set(spot.X + off.X, spot.Y + off.Y, spot.Z + off.Z);
                if (code != null && code.Equals(ba.GetBlock(mpos).Code))
                {
                    caps++;
                    long cell = PackCell(mpos.X >> 2, mpos.Z >> 2);
                    if (!cells.Contains(cell)) cells.Add(cell);
                }
            }
        }

        float eta = -1;
        if (caps == 0 && growingDaysRef != null && growingProgressRef != null)
            eta = (float)GameMath.Clamp(growingDaysRef(myc) - growingProgressRef(myc), 0, 60);
        return (caps > 0 ? 2 : 1, caps, eta, cells);
    }

    /// <summary>Bush: ready when any fruiting-bush behavior at the spot reads Ripe.</summary>
    private int ReadBush(WorkedSpot spot)
    {
        var be = sapi!.World.BlockAccessor.GetBlockEntity(new BlockPos(spot.X, spot.Y, spot.Z));
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
        foreach (var members in Cluster(ownSpots)) comps.Add(new SpotMapComponent(members, this, capi));
    }

    /// <summary>Groups same-kind circles that overlap into one cluster (ruled from live
    /// feedback: several worked patches in an area should read as one larger stretch of known
    /// ground, not stacked discs). The component renders the cluster as a lumpy union of its
    /// member circles below GM and as the blocky cell outline at GM — the fidelity ladder
    /// expressed spatially.</summary>
    private static List<List<SpotView>> Cluster(List<SpotView> views)
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
        return new List<List<SpotView>>(byGroup.Values);
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active) return;
        foreach (var c in comps) c.Render(mapElem, dt);
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, System.Text.StringBuilder hoverText)
    {
        if (!Active) return;
        // First hit wins: overlapping clusters must not stack their tooltips (live feedback).
        foreach (var c in comps)
        {
            int len = hoverText.Length;
            c.OnMouseMove(args, mapElem, hoverText);
            if (hoverText.Length != len) return;
        }
    }

    public override void Dispose()
    {
        circleTexture?.Dispose();
        quadModel?.Dispose();
        base.Dispose();
    }
}

/// <summary>One cluster of same-kind worked ground. The shape ladder (ruled from live feedback,
/// Jeffrey's mockup): below GM every member renders as its own soft translucent circle, so
/// overlapping patches read as one lumpy blob instead of a single fat circle; at GM the cluster
/// renders the blocky cell outline of the TRUTH (verified standing caps, recorded picks, the
/// vanilla depletion bucket). Hover climbs the same ladder: vague line, state line, then one
/// line per patch at Master+.</summary>
public class SpotMapComponent : MapComponent
{
    private readonly List<AlmanacSpotsLayer.SpotView> members;
    private readonly AlmanacSpotsLayer layer;
    private readonly int kind;
    private int tier, state;
    private readonly int cellSize;
    private readonly List<string> names = new();
    private float stockPct = -1, etaDays = -1;
    private readonly Vec3d center = new();
    private float enclosingRadius;
    private readonly List<(int cx, int cz, int cstate)> cells = new();
    private readonly Vec4f color = new();
    private Vec2f viewPos = new();
    private Vec2f edgePos = new();
    private readonly Matrixf mvMat = new();

    public SpotMapComponent(List<AlmanacSpotsLayer.SpotView> members, AlmanacSpotsLayer layer, ICoreClientAPI capi) : base(capi)
    {
        this.members = members;
        this.layer = layer;
        kind = members[0].Kind;
        cellSize = Math.Max(1, members[0].CellSize);

        foreach (var m in members)
        {
            center.X += m.X; center.Y += m.Y; center.Z += m.Z;
            tier = Math.Max(tier, m.Tier);
            state = Math.Max(state, m.State); // any ready patch marks the cluster ready
            if (m.StockPct >= 0) stockPct = stockPct < 0 ? m.StockPct : Math.Min(stockPct, m.StockPct);
            etaDays = Math.Max(etaDays, m.EtaDays);
            if (m.Names != null)
                foreach (var nm in m.Names)
                    if (names.Count < 6 && !names.Contains(nm)) names.Add(nm);
        }
        center.X /= members.Count; center.Y /= members.Count; center.Z /= members.Count;
        foreach (var m in members)
        {
            double dx = m.X - center.X, dz = m.Z - center.Z;
            enclosingRadius = Math.Max(enclosingRadius, (float)Math.Sqrt(dx * dx + dz * dz) + m.RadiusBlocks);
        }

        // GM: the blocky truth. Server-sent cells are exact; a member without cells (unloaded
        // chunk, pre-outline recording) falls back to rasterizing its circle so it still shows.
        if (tier >= 4)
        {
            foreach (var m in members)
            {
                if (m.Cells != null)
                    foreach (long packed in m.Cells) AddCell((int)(packed >> 32), (int)packed, m.State);
                else
                    RasterizeCircle(m);
            }
        }

        // ONE learnable colour per kind (live feedback ruling): berries crimson, mushrooms
        // amber, waters blue. State rides intensity, never hue.
        int rgb = kind switch
        {
            (int)AlmanacSpotsLayer.SpotKind.Bush => ColorUtil.ToRgba(255, 215, 40, 80),      // berry crimson
            (int)AlmanacSpotsLayer.SpotKind.Mushroom => ColorUtil.ToRgba(255, 240, 150, 40), // cap amber
            _ => ColorUtil.ToRgba(255, 45, 130, 230),                                        // water blue
        };
        ColorUtil.ToRGBAVec4f(rgb, ref color);
    }

    private void AddCell(int cx, int cz, int cstate)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].cx == cx && cells[i].cz == cz)
            {
                if (cstate > cells[i].cstate) cells[i] = (cx, cz, cstate);
                return;
            }
        }
        cells.Add((cx, cz, cstate));
    }

    private void RasterizeCircle(AlmanacSpotsLayer.SpotView m)
    {
        int c0x = (int)Math.Floor((m.X - m.RadiusBlocks) / cellSize), c1x = (int)Math.Floor((m.X + m.RadiusBlocks) / cellSize);
        int c0z = (int)Math.Floor((m.Z - m.RadiusBlocks) / cellSize), c1z = (int)Math.Floor((m.Z + m.RadiusBlocks) / cellSize);
        for (int cx = c0x; cx <= c1x; cx++)
            for (int cz = c0z; cz <= c1z; cz++)
            {
                double dx = cx * cellSize + cellSize * 0.5 - m.X;
                double dz = cz * cellSize + cellSize * 0.5 - m.Z;
                if (dx * dx + dz * dz <= m.RadiusBlocks * m.RadiusBlocks) AddCell(cx, cz, m.State);
            }
    }

    private static float AlphaFor(int st) => st == 2 ? 0.60f : st == 1 ? 0.40f : 0.50f;

    private bool HitsVisibleShape(MouseEvent args, GuiElementMap mapElem)
    {
        var probe = new Vec3d();
        var p1 = new Vec2f();
        var p2 = new Vec2f();

        if (tier >= 4 && cells.Count > 0)
        {
            foreach (var (cx, cz, _) in cells)
            {
                probe.Set(cx * (double)cellSize, center.Y, cz * (double)cellSize);
                mapElem.TranslateWorldPosToViewPos(probe, ref p1);
                probe.Set((cx + 1) * (double)cellSize, center.Y, (cz + 1) * (double)cellSize);
                mapElem.TranslateWorldPosToViewPos(probe, ref p2);
                double x1 = mapElem.Bounds.renderX + Math.Min(p1.X, p2.X), x2 = mapElem.Bounds.renderX + Math.Max(p1.X, p2.X);
                double y1 = mapElem.Bounds.renderY + Math.Min(p1.Y, p2.Y), y2 = mapElem.Bounds.renderY + Math.Max(p1.Y, p2.Y);
                if (args.X >= x1 && args.X <= x2 && args.Y >= y1 && args.Y <= y2) return true;
            }
            return false;
        }

        foreach (var m in members)
        {
            probe.Set(m.X, m.Y, m.Z);
            mapElem.TranslateWorldPosToViewPos(probe, ref p1);
            probe.Set(m.X + m.RadiusBlocks, m.Y, m.Z);
            mapElem.TranslateWorldPosToViewPos(probe, ref p2);
            float r = Math.Max(6, Math.Abs(p2.X - p1.X));
            double mdx = args.X - (p1.X + mapElem.Bounds.renderX);
            double mdy = args.Y - (p1.Y + mapElem.Bounds.renderY);
            if (mdx * mdx + mdy * mdy <= r * r) return true;
        }
        return false;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        map.TranslateWorldPosToViewPos(center, ref viewPos);
        map.TranslateWorldPosToViewPos(new Vec3d(center.X + enclosingRadius, center.Y, center.Z), ref edgePos);
        float pixelReach = Math.Abs(edgePos.X - viewPos.X) + 50;
        if (viewPos.X < -pixelReach || viewPos.Y < -pixelReach
            || viewPos.X > map.Bounds.OuterWidth + pixelReach || viewPos.Y > map.Bounds.OuterHeight + pixelReach) return;

        var api = map.Api;
        var prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        var tex = layer.circleTexture;
        if (tex == null || layer.quadModel == null) return;

        if (tier >= 4 && cells.Count > 0)
        {
            // Blocky truth cells: flat untextured quads, disjoint so the union stays one flat tone.
            prog.Uniform("noTexture", 1f);
            prog.BindTexture2D("tex2d", tex.TextureId, 0);
            var corner = new Vec3d();
            var cornerPos = new Vec2f();
            foreach (var (cx, cz, cstate) in cells)
            {
                corner.Set(cx * (double)cellSize, center.Y, cz * (double)cellSize);
                map.TranslateWorldPosToViewPos(corner, ref cornerPos);
                corner.Set((cx + 1) * (double)cellSize, center.Y, (cz + 1) * (double)cellSize);
                map.TranslateWorldPosToViewPos(corner, ref edgePos);
                float w = Math.Max(2, edgePos.X - cornerPos.X), h = Math.Max(2, edgePos.Y - cornerPos.Y);
                float x = (float)(map.Bounds.renderX + cornerPos.X + w / 2);
                float y = (float)(map.Bounds.renderY + cornerPos.Y + h / 2);

                color.W = AlphaFor(cstate);
                prog.Uniform("rgbaIn", color);
                mvMat.Set(api.Render.CurrentModelviewMatrix)
                    .Translate(x, y, 60)
                    .Scale(w, h, 0)
                    .Scale(0.5f, 0.5f, 0);
                prog.UniformMatrix("modelViewMatrix", mvMat.Values);
                api.Render.RenderMesh(layer.quadModel);
            }
            return;
        }

        // Below GM: every member circle renders, so overlaps read as one lumpy blob.
        prog.Uniform("noTexture", 0f);
        prog.BindTexture2D("tex2d", tex.TextureId, 0);
        var mpos = new Vec3d();
        var mview = new Vec2f();
        foreach (var m in members)
        {
            mpos.Set(m.X, m.Y, m.Z);
            map.TranslateWorldPosToViewPos(mpos, ref mview);
            mpos.Set(m.X + m.RadiusBlocks, m.Y, m.Z);
            map.TranslateWorldPosToViewPos(mpos, ref edgePos);
            float pixelRadius = Math.Max(6, Math.Abs(edgePos.X - mview.X));
            float x = (float)(map.Bounds.renderX + mview.X);
            float y = (float)(map.Bounds.renderY + mview.Y);

            color.W = AlphaFor(m.State);
            prog.Uniform("rgbaIn", color);
            mvMat.Set(api.Render.CurrentModelviewMatrix)
                .Translate(x, y, 60)
                .Scale(pixelRadius * 2, pixelRadius * 2, 0)
                .Scale(0.5f, 0.5f, 0);
            prog.UniformMatrix("modelViewMatrix", mvMat.Values);
            api.Render.RenderMesh(layer.quadModel);
        }
    }

    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, System.Text.StringBuilder hoverText)
    {
        // Cheap reject against the cluster's enclosing circle, then the REAL test: the pointer
        // must be inside a visible shape — a member circle below GM, an outline cell at GM —
        // not merely inside the invisible bounding disc (live feedback).
        mapElem.TranslateWorldPosToViewPos(center, ref viewPos);
        double dx = args.X - (viewPos.X + mapElem.Bounds.renderX);
        double dy = args.Y - (viewPos.Y + mapElem.Bounds.renderY);
        mapElem.TranslateWorldPosToViewPos(new Vec3d(center.X + enclosingRadius, center.Y, center.Z), ref edgePos);
        float pixelRadius = Math.Max(6, Math.Abs(edgePos.X - viewPos.X));
        if (dx * dx + dy * dy > pixelRadius * pixelRadius) return;
        if (!HitsVisibleShape(args, mapElem)) return;

        if (kind == (int)AlmanacSpotsLayer.SpotKind.Water)
        {
            string line = state == 1 ? Lang.Get("almanactcm:spot-water-low") : Lang.Get("almanactcm:spot-water-healthy");
            if (stockPct >= 0) line += " " + Lang.Get("almanactcm:spot-stock", (int)stockPct);
            if (etaDays > 0) line += " " + Lang.Get("almanactcm:spot-water-eta", (int)Math.Ceiling(etaDays));
            if (members.Count > 1) line += " " + Lang.Get("almanactcm:spot-count", members.Count);
            hoverText.AppendLine(line);
            return;
        }

        string noun = kind == (int)AlmanacSpotsLayer.SpotKind.Mushroom
            ? Lang.Get("almanactcm:spot-mushrooms") : Lang.Get("almanactcm:spot-berries");

        // Apprentice/Journeyman: one summary line for the stretch.
        if (tier <= 2)
        {
            string line = tier <= 1
                ? Lang.Get("almanactcm:spot-somewhere", noun)
                : state == 2 ? Lang.Get("almanactcm:spot-ready", noun) : Lang.Get("almanactcm:spot-regrowing", noun);
            if (names.Count > 0)
            {
                var shown = names.Count > 3 ? names.GetRange(0, 3) : names;
                string joined = string.Join(", ", shown);
                if (names.Count > 3) joined += " " + Lang.Get("almanactcm:spot-names-more");
                line += " " + Lang.Get("almanactcm:spot-names", joined);
            }
            if (members.Count > 1) line += " " + Lang.Get("almanactcm:spot-count", members.Count);
            hoverText.AppendLine(line);
            return;
        }

        // Master+: one line per patch, no flavor. GM adds verified counts and the regrow clock.
        int listed = 0;
        foreach (var m in members)
        {
            if (listed == 6 && members.Count > 7)
            {
                hoverText.AppendLine(Lang.Get("almanactcm:spot-more", members.Count - listed));
                break;
            }
            string what = m.Names != null && m.Names.Count > 0 ? string.Join(", ", m.Names) : noun;
            string mline;
            if (m.State == 2)
            {
                mline = m.CapsStanding > 1 ? Lang.Get("almanactcm:spot-line-ready-caps", what, m.CapsStanding)
                    : m.CapsStanding == 1 ? Lang.Get("almanactcm:spot-line-ready-onecap", what)
                    : Lang.Get("almanactcm:spot-line-ready", what);
            }
            else
            {
                mline = m.EtaDays >= 0 && m.EtaDays < 1.5f ? Lang.Get("almanactcm:spot-line-soon", what)
                    : m.EtaDays >= 1.5f ? Lang.Get("almanactcm:spot-line-eta", what, (int)Math.Round(m.EtaDays))
                    : Lang.Get("almanactcm:spot-line-resting", what);
            }
            hoverText.AppendLine(mline);
            listed++;
        }
    }
}

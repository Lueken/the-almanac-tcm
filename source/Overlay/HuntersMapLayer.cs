using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace AlmanacTcm.Overlay;

/// <summary>
/// THE HUNTER'S MAP (HUN Phase 3, scope 2026-07-18) — the Master-Hunter capstone: a world-map
/// overlay painting WHERE a species can live (its worldgen habitat/range), never where an animal is.
///
/// Built on the same spine as the worked-ground overlay: a MarkerMapLayer, DataSide=Server, degraded
/// per viewer before send through the map system's own per-player SendMapDataToClient channel (no
/// custom network, so one hunter's range-lore never reaches another).
///
/// Two gates (P1 builds the coarse merged layer; the fidelity ladder is P2):
///   - KNOWLEDGE: only species the viewer has personally killed >= N (=3) times appear. Fed by the
///     HunPatches per-species kill ledger, which has recorded since Phase 1.
///   - RANK: below Journeyman I (level 9) nothing shows; the reading is the earned capability.
///
/// The habitat is worldgen truth, computed the way the engine's own creature spawner tests it: sample
/// the region ClimateMap/ForestMap/ShrubMap at a reference height and test each species'
/// ClimateSpawnCondition envelope (MatchesClimate + MatchesForestation). Only generated terrain has
/// region maps, so the map never spoils unexplored country.
///
/// Divination audit (ruled): habitat only, never live entity positions.
/// </summary>
public class HuntersMapLayer : MarkerMapLayer
{
    /// <summary>What the client receives: the merged habitat as a set of packed grid cells.</summary>
    [ProtoContract]
    public class HabitatView
    {
        [ProtoMember(1)] public int CellSize = P1CellSize;
        [ProtoMember(2)] public List<long>? Cells;
    }

    private const int JourneymanLevel = 9; // fidelity climb starts here (Jeffrey, 2026-07-18)
    private const int KnowledgeN = 3;       // kills per species before its habitat is knowable
    private const int P1CellSize = 32;      // coarse single-fidelity cell (P2 varies this by rank)
    private const int MaxCellsPerView = 4000;
    private const int MaxViewSpan = 3072;   // clip a zoomed-out view so the server sample stays bounded

    // server
    private ICoreServerAPI? sapi;
    private readonly Dictionary<string, ClimateSpawnCondition?> envelopeCache = new();

    // client
    private ICoreClientAPI? capi;
    private HabitatView? view;
    public MeshRef? quadModel;
    private readonly Vec4f color = new();

    public override bool RequireChunkLoaded => false;
    public override string Title => Lang.Get("almanactcm:huntersmap-title");
    public override EnumMapAppSide DataSide => EnumMapAppSide.Server;
    public override string LayerGroupCode => "almanachuntersmap";

    public HuntersMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        TcmLog.Info(api, $"hunter's map layer constructed ({api.Side}), group={LayerGroupCode}");
        if (api.Side == EnumAppSide.Server)
        {
            sapi = (ICoreServerAPI)api;
        }
        else
        {
            capi = (ICoreClientAPI)api;
            quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
            // One neutral "game country" tone for P1; per-species hue is a P3 (Master+) refinement.
            ColorUtil.ToRGBAVec4f(ColorUtil.ToRgba(255, 95, 155, 90), ref color);
        }
    }

    // ------------------------------------------------------------ server: build + private send

    public override void OnViewChangedServer(IServerPlayer fromPlayer, int x1, int z1, int x2, int z2)
    {
        if (sapi == null) return;
        var v = BuildView(fromPlayer, x1, z1, x2, z2);
        mapSink.SendMapDataToClient(this, fromPlayer, SerializerUtil.Serialize(v));
    }

    private HabitatView BuildView(IServerPlayer player, int x1, int z1, int x2, int z2)
    {
        var v = new HabitatView { CellSize = P1CellSize, Cells = new List<long>() };

        int level = Domains.HunDomain.LevelOf(player);
        if (level < JourneymanLevel)
        {
            TcmLog.Cat(sapi!, "hun", $"hunter's map: HUN level {level} is below Journeyman {JourneymanLevel}, nothing sent");
            return v; // reading not yet learned
        }

        var envs = new List<ClimateSpawnCondition>();
        var known = new List<string>();
        foreach (string species in Domains.HunPatches.KnownSpecies(player, KnowledgeN))
        {
            known.Add(species);
            var env = ResolveEnvelope(species);
            if (env != null) envs.Add(env);
            else TcmLog.Cat(sapi!, "hun", $"hunter's map: no spawn envelope resolved for species '{species}'");
        }
        if (envs.Count == 0)
        {
            TcmLog.Cat(sapi!, "hun", $"hunter's map: level {level} ok, but 0 usable envelopes (known>={KnowledgeN}: [{string.Join(", ", known)}])");
            return v; // nothing hunted enough yet
        }

        // The map system hands this rect in CHUNK coords, not block coords. The worked-ground layer
        // never noticed because it ignores the rect entirely and sends every recorded spot; this is
        // the first layer that actually samples the viewed area, so it must convert. Reading them as
        // blocks sampled a ~30x20 patch near world origin (ungenerated, null region, zero cells).
        int chunkSize = GlobalConstants.ChunkSize;
        x1 *= chunkSize; z1 *= chunkSize; x2 *= chunkSize; z2 *= chunkSize;

        // Clip an over-wide view so a zoomed-out map does not sample the whole world in one pass.
        if (x2 - x1 > MaxViewSpan) { int m = (x1 + x2) / 2; x1 = m - MaxViewSpan / 2; x2 = m + MaxViewSpan / 2; }
        if (z2 - z1 > MaxViewSpan) { int m = (z1 + z2) / 2; z1 = m - MaxViewSpan / 2; z2 = m + MaxViewSpan / 2; }

        var ba = sapi!.World.BlockAccessor;
        int regionSize = ba.RegionSize;
        int seaLevel = sapi.World.SeaLevel;
        int refY = (int)(seaLevel * 1.09);      // the reference height the engine spawner reads climate at
        int distToSea = refY - seaLevel;

        int cs = P1CellSize;
        for (int cx = (int)Math.Floor((double)x1 / cs); cx <= x2 / cs && v.Cells.Count < MaxCellsPerView; cx++)
        {
            for (int cz = (int)Math.Floor((double)z1 / cs); cz <= z2 / cs && v.Cells.Count < MaxCellsPerView; cz++)
            {
                int bx = cx * cs + cs / 2;
                int bz = cz * cs + cs / 2;
                var cc = WorldGenClimateAt(ba, regionSize, seaLevel, distToSea, bx, bz);
                if (cc == null) continue; // ungenerated region: no habitat truth to paint
                foreach (var env in envs)
                {
                    if (env.MatchesClimate(cc) && env.MatchesForestation(cc))
                    {
                        v.Cells.Add(((long)cx << 32) | (uint)cz);
                        break;
                    }
                }
            }
        }
        TcmLog.Cat(sapi!, "hun",
            $"hunter's map: level={level} species=[{string.Join(", ", known)}] envelopes={envs.Count} " +
            $"cells={v.Cells.Count} rect=({x1},{z1})-({x2},{z2}) cellSize={cs}");
        return v;
    }

    /// <summary>Worldgen climate at a column, read straight off the region maps the way
    /// ServerWorldMap.getWorldGenClimateAt + AddWorldGenForestShrub do. Null if the region is not
    /// generated. Temperature is read at the engine's spawner reference height (distToSea), rainfall
    /// and forest/shrub off the region's own maps.</summary>
    private static ClimateCondition? WorldGenClimateAt(IBlockAccessor ba, int regionSize, int seaLevel, int distToSea, int bx, int bz)
    {
        int rX = (int)Math.Floor((double)bx / regionSize);
        int rZ = (int)Math.Floor((double)bz / regionSize);
        IMapRegion? region = ba.GetMapRegion(rX, rZ);
        if (region?.ClimateMap == null) return null;

        float nx = (float)((((bx % regionSize) + regionSize) % regionSize) / (double)regionSize);
        float nz = (float)((((bz % regionSize) + regionSize) % regionSize) / (double)regionSize);

        int climateInt = region.ClimateMap.GetUnpaddedColorLerpedForNormalizedPos(nx, nz);
        float temp = Climate.GetScaledAdjustedTemperatureFloat((climateInt >> 16) & 0xFF, distToSea);
        float rain = Climate.GetRainFall((climateInt >> 8) & 0xFF, seaLevel) / 255f;

        var cc = new ClimateCondition
        {
            Temperature = temp,
            WorldGenTemperature = temp,
            Rainfall = rain,
            WorldgenRainfall = rain
        };
        if (region.ForestMap != null)
            cc.ForestDensity = region.ForestMap.GetUnpaddedColorLerpedForNormalizedPos(nx, nz) / 255f;
        if (region.ShrubMap != null)
            cc.ShrubDensity = (region.ShrubMap.GetUnpaddedColorLerpedForNormalizedPos(nx, nz) & 0xFF) / 255f;
        return cc;
    }

    /// <summary>The species' habitat envelope. Ledger keys are the entity code first part ("wolf");
    /// find any registered entity of that species with a worldgen (or runtime) spawn envelope. The
    /// Climate block merges into Worldgen/Runtime on deserialize, so either carries the climate.</summary>
    private ClimateSpawnCondition? ResolveEnvelope(string species)
    {
        if (envelopeCache.TryGetValue(species, out var cached)) return cached;

        ClimateSpawnCondition? found = null;
        foreach (EntityProperties t in sapi!.World.EntityTypes)
        {
            if (t?.Code == null || t.Code.FirstCodePart() != species) continue;
            var sc = t.Server?.SpawnConditions;
            ClimateSpawnCondition? env = (ClimateSpawnCondition?)sc?.Worldgen ?? sc?.Runtime;
            if (env != null) { found = env; break; }
        }
        envelopeCache[species] = found;
        return found;
    }

    // ------------------------------------------------------------ client: receive + render

    public override void OnDataFromServer(byte[] data)
    {
        view = SerializerUtil.Deserialize<HabitatView>(data);
    }

    public override void OnMapOpenedClient() { }

    public override void Render(GuiElementMap map, float dt)
    {
        if (!Active || view?.Cells == null || quadModel == null) return;

        var api = map.Api;
        var prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.Uniform("noTexture", 1f);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        color.W = 0.32f;
        prog.Uniform("rgbaIn", color);

        int cs = view.CellSize;
        var corner = new Vec3d();
        var cornerPos = new Vec2f();
        var edgePos = new Vec2f();
        var mvMat = new Matrixf();

        foreach (long packed in view.Cells)
        {
            int cx = (int)(packed >> 32), cz = (int)packed;
            corner.Set(cx * (double)cs, 0, cz * (double)cs);
            map.TranslateWorldPosToViewPos(corner, ref cornerPos);
            corner.Set((cx + 1) * (double)cs, 0, (cz + 1) * (double)cs);
            map.TranslateWorldPosToViewPos(corner, ref edgePos);

            float w = Math.Max(1, edgePos.X - cornerPos.X), h = Math.Max(1, edgePos.Y - cornerPos.Y);
            float x = (float)(map.Bounds.renderX + cornerPos.X + w / 2);
            float y = (float)(map.Bounds.renderY + cornerPos.Y + h / 2);
            if (x < -w || y < -h || x > map.Bounds.OuterWidth + w || y > map.Bounds.OuterHeight + h) continue;

            mvMat.Set(api.Render.CurrentModelviewMatrix)
                .Translate(x, y, 60)
                .Scale(w, h, 0)
                .Scale(0.5f, 0.5f, 0);
            prog.UniformMatrix("modelViewMatrix", mvMat.Values);
            api.Render.RenderMesh(quadModel);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, System.Text.StringBuilder hoverText)
    {
        if (!Active || view?.Cells == null) return;

        int cs = view.CellSize;
        var corner = new Vec3d();
        var cornerPos = new Vec2f();
        var edgePos = new Vec2f();
        foreach (long packed in view.Cells)
        {
            int cx = (int)(packed >> 32), cz = (int)packed;
            corner.Set(cx * (double)cs, 0, cz * (double)cs);
            mapElem.TranslateWorldPosToViewPos(corner, ref cornerPos);
            corner.Set((cx + 1) * (double)cs, 0, (cz + 1) * (double)cs);
            mapElem.TranslateWorldPosToViewPos(corner, ref edgePos);
            double x1 = mapElem.Bounds.renderX + Math.Min(cornerPos.X, edgePos.X);
            double x2 = mapElem.Bounds.renderX + Math.Max(cornerPos.X, edgePos.X);
            double y1 = mapElem.Bounds.renderY + Math.Min(cornerPos.Y, edgePos.Y);
            double y2 = mapElem.Bounds.renderY + Math.Max(cornerPos.Y, edgePos.Y);
            if (args.X >= x1 && args.X <= x2 && args.Y >= y1 && args.Y <= y2)
            {
                hoverText.AppendLine(Lang.Get("almanactcm:huntersmap-country"));
                return;
            }
        }
    }

    public override void Dispose()
    {
        quadModel?.Dispose();
        base.Dispose();
    }
}

using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Overlay;

/// SHELVED 2026-07-18. The layer is fully wired and does paint habitat, but two problems were not
/// solved and Jeffrey parked it to move on. Registration is commented out in
/// AlmanacTcmModSystem.Start (plus the envelope sync in HunPatches.RegisterServer/RegisterClient);
/// re-enable those three call sites to bring it back. The kill ledger keeps recording meanwhile.
///
/// OPEN PROBLEM 1 — the border. The painted country is still cut at a hard edge on the leading side
/// of the view, and it survived every architecture: server per-view build, client-side accumulation,
/// and the full client-side per-chunk model copied from ProspectTogether. Leading hypothesis, not
/// yet proven: OnViewChangedClient only reports chunks the map considers fully visible, so the
/// outermost ring is never handed to us and the frontier always lags the viewport by a chunk or two.
/// Next thing to try: ignore the view-change list entirely and evaluate chunks from the map's own
/// current block view bounds (GuiElementMap.CurrentBlockViewBounds) during Render, or walk the
/// client's loaded map regions directly rather than waiting to be told what is visible.
///
/// OPEN PROBLEM 2 — coverage is far too broad. Four merged species with permissive envelopes blanket
/// most of the map, which also buries other overlays. The fix is the planned P2 work: per-species
/// cell data plus a filter dropdown so one beast's country shows at a time, and per-species hues at
/// Master+. Consider also tightening the test beyond climate/forest.
///
/// THE HUNTER'S MAP (HUN Phase 3) — the Master-Hunter capstone: a world-map overlay painting WHERE
/// a species can live (its worldgen habitat/range), never where an animal is.
///
/// CLIENT-SIDE, modelled directly on ProspectTogether's ProspectorOverlayLayer: DataSide=Client, a
/// per-chunk result cache that ACCUMULATES and is never rebuilt from a view rectangle. Chunks are
/// evaluated as they enter the map view (OnViewChangedClient hands us chunk coords) and the result
/// is kept, so the painted country persists as you pan and simply fills in where you have been.
///
/// This replaced a server-side per-view build (0.3.103-0.3.108). That approach recomputed the
/// visible rect each time and shipped it wholesale, so anything outside the current rect vanished
/// and partially-visible edge chunks were never sampled at all: the hard border on the right and
/// bottom of the map. It also put the whole habitat scan on the server every time anyone panned.
///
/// The habitat itself is worldgen truth, tested the way the engine's own creature spawner tests a
/// spawn: sample the region ClimateMap/ForestMap and match each species' ClimateSpawnCondition.
/// The client receives ClimateMap and ForestMap in its map-region packet, but NOT ShrubMap, so
/// shrub constraints are skipped client-side rather than being allowed to wrongly exclude a species.
///
/// Two gates: HUN rank >= Journeyman, and per-species knowledge (>= 3 lifetime kills, synced from
/// the server ledger by HunPatches). Divination audit: habitat only, never live positions.
/// </summary>
public class HuntersMapLayer : MapLayer
{
    private const int JourneymanLevel = 9;
    private const int CellSize = 32;          // one cell == one chunk, so results cache per chunk
    private const int MaxChunksPerTick = 256; // bound the per-view-change evaluation

    private readonly ICoreClientAPI? capi;
    /// <summary>Chunks already evaluated (whether or not they turned out to be habitat).</summary>
    private readonly HashSet<long> tested = new();
    /// <summary>Chunks that ARE habitat for at least one known species. Accumulated, never cleared
    /// except when the hunter's knowledge or rank changes.</summary>
    private readonly HashSet<long> habitat = new();
    private List<Domains.HunPatches.SpeciesEnvelope> activeEnvelopes = new();
    private int builtForKnownVersion = -1;
    private int builtForLevel = -1;

    public MeshRef? quadModel;
    private readonly Vec4f color = new();

    public override string Title => Lang.Get("almanactcm:huntersmap-title");
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override string LayerGroupCode => "almanachuntersmap";
    public override bool RequireChunkLoaded => false;

    public HuntersMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        if (api.Side != EnumAppSide.Client) return;
        capi = (ICoreClientAPI)api;
        quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        ColorUtil.ToRGBAVec4f(ColorUtil.ToRgba(255, 95, 155, 90), ref color);
    }

    // ------------------------------------------------------------ evaluation (per chunk)

    /// <summary>Chunks entering the view get evaluated once and remembered. Nothing is dropped when
    /// they leave the view: that is what keeps the painted country from being cut at the edge.</summary>
    public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        if (capi == null) return;
        if (!RefreshKnowledge()) return;

        int budget = MaxChunksPerTick;
        foreach (var c in nowVisible)
        {
            if (budget-- <= 0) break;
            Evaluate(c.X, c.Y);
        }
    }

    public override void OnMapOpenedClient()
    {
        RefreshKnowledge();
    }

    /// <summary>Rebuilds the active envelope set when rank or known species change, discarding the
    /// cached habitat so it recomputes. Returns false when the map should show nothing at all.</summary>
    private bool RefreshKnowledge()
    {
        int level = Domains.HunDomain.ClientLevel();
        int version = Domains.HunPatches.ClientKnownVersion;
        if (level == builtForLevel && version == builtForKnownVersion) return activeEnvelopes.Count > 0;

        builtForLevel = level;
        builtForKnownVersion = version;
        tested.Clear();
        habitat.Clear();
        activeEnvelopes = level < JourneymanLevel
            ? new List<Domains.HunPatches.SpeciesEnvelope>()   // the reading is not yet learned
            : Domains.HunPatches.ClientEnvelopes;

        TcmLog.Cat(capi!, "hun",
            $"hunter's map: level={level} envelopes={activeEnvelopes.Count} " +
            $"[{string.Join(", ", activeEnvelopes.ConvertAll(e => e.Species ?? "?"))}]");
        return activeEnvelopes.Count > 0;
    }

    private void Evaluate(int chunkX, int chunkZ)
    {
        long key = ((long)chunkX << 32) | (uint)chunkZ;
        if (!tested.Add(key)) return; // already decided

        var cc = WorldGenClimateAt(chunkX * CellSize + CellSize / 2, chunkZ * CellSize + CellSize / 2, out bool shrubKnown);
        if (cc == null) { tested.Remove(key); return; } // region not loaded yet: retry when it is

        foreach (var env in activeEnvelopes)
        {
            if (Matches(env, cc, shrubKnown))
            {
                habitat.Add(key);
                return;
            }
        }
    }

    /// <summary>The habitat test, mirroring ClimateSpawnCondition.MatchesClimate + MatchesForestation
    /// but tolerating the client's missing ShrubMap: temp, rain and forest are always checked, shrub
    /// constraints only when that data is actually present.</summary>
    private static bool Matches(Domains.HunPatches.SpeciesEnvelope env, ClimateCondition cc, bool shrubKnown)
    {
        if (env.MinTemp > cc.WorldGenTemperature || env.MaxTemp < cc.WorldGenTemperature) return false;
        if (env.MinRain > cc.Rainfall || env.MaxRain < cc.Rainfall) return false;
        if (env.MinForest > cc.ForestDensity || env.MaxForest < cc.ForestDensity) return false;
        if (shrubKnown)
        {
            if (env.MinShrubs > cc.ShrubDensity || env.MaxShrubs < cc.ShrubDensity) return false;
            if (env.MinForestOrShrubs > Math.Max(cc.ForestDensity, cc.ShrubDensity)) return false;
        }
        else if (env.MinForestOrShrubs > cc.ForestDensity) return false;
        return true;
    }

    /// <summary>Worldgen climate for a column, read off the client's own map region the way
    /// ServerWorldMap.getWorldGenClimateAt + AddWorldGenForestShrub do. Null if the region is not
    /// loaded client-side, which is exactly the "country I have actually been to" boundary.</summary>
    private ClimateCondition? WorldGenClimateAt(int bx, int bz, out bool shrubKnown)
    {
        shrubKnown = false;
        var ba = capi!.World.BlockAccessor;
        int regionSize = ba.RegionSize;
        int rX = (int)Math.Floor((double)bx / regionSize);
        int rZ = (int)Math.Floor((double)bz / regionSize);
        IMapRegion? region = ba.GetMapRegion(rX, rZ);
        if (region?.ClimateMap == null) return null;

        float nx = (float)((((bx % regionSize) + regionSize) % regionSize) / (double)regionSize);
        float nz = (float)((((bz % regionSize) + regionSize) % regionSize) / (double)regionSize);

        int seaLevel = capi.World.SeaLevel;
        int refY = (int)(seaLevel * 1.09);   // the reference height the engine spawner reads climate at
        int climateInt = region.ClimateMap.GetUnpaddedColorLerpedForNormalizedPos(nx, nz);
        float temp = Climate.GetScaledAdjustedTemperatureFloat((climateInt >> 16) & 0xFF, refY - seaLevel);
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
        {
            cc.ShrubDensity = (region.ShrubMap.GetUnpaddedColorLerpedForNormalizedPos(nx, nz) & 0xFF) / 255f;
            shrubKnown = true;
        }
        return cc;
    }


    // ------------------------------------------------------------ render

    public override void Render(GuiElementMap map, float dt)
    {
        if (!Active || habitat.Count == 0 || quadModel == null) return;

        var api = map.Api;
        var prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.Uniform("noTexture", 1f);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        color.W = 0.20f;
        prog.Uniform("rgbaIn", color);

        var corner = new Vec3d();
        var cornerPos = new Vec2f();
        var edgePos = new Vec2f();
        var mvMat = new Matrixf();

        foreach (long packed in habitat)
        {
            int cx = (int)(packed >> 32), cz = (int)packed;
            corner.Set(cx * (double)CellSize, 0, cz * (double)CellSize);
            map.TranslateWorldPosToViewPos(corner, ref cornerPos);
            corner.Set((cx + 1) * (double)CellSize, 0, (cz + 1) * (double)CellSize);
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
        if (!Active || habitat.Count == 0) return;

        var corner = new Vec3d();
        var cornerPos = new Vec2f();
        var edgePos = new Vec2f();
        foreach (long packed in habitat)
        {
            int cx = (int)(packed >> 32), cz = (int)packed;
            corner.Set(cx * (double)CellSize, 0, cz * (double)CellSize);
            mapElem.TranslateWorldPosToViewPos(corner, ref cornerPos);
            corner.Set((cx + 1) * (double)CellSize, 0, (cz + 1) * (double)CellSize);
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

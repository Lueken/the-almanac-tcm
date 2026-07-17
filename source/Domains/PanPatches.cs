using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace AlmanacTcm.Domains;

/// <summary>
/// PAN Phase 1 hooks (rank-bonus-design §PAN, ruled 2026-07-09; scope confirmed 2026-07-16:
/// bettererprospecting IS the propick on The Quire and ProspectTogether shares whatever the
/// map records — which is why fidelity gates at READING time, in the data).
///
/// Seams:
///   • Panning practice: BlockPan.OnHeldInteractStop (wash completes at 3.4s with material;
///     the material code must be captured in the prefix because the method clears it).
///   • Pan yield: vanilla's own PanningDrop.DropModbyStat multiplier. Vanilla assets only wire
///     it on rusty gears, so at server start we inject our stat name onto every drop entry
///     that has none (in-memory tweak on the parsed table — no JSON patch, no hot-path
///     Harmony), then drive the entity stat by rank on the reconcile tick.
///   • Prospecting practice + Untrained coarsening: ModSystemOreMap.DidProbe — the one funnel
///     every density/stone reading passes through (vanilla and BP 3.4.3 both, verified). The
///     prefix quantizes Untrained readings BEFORE they are recorded, so the ore map and every
///     ProspectTogether share carry the garbled read permanently.
///   • BP search modes (node / proximity / borehole) write no readings but are real
///     prospecting work: conditional postfixes credit practice only.
/// </summary>
public static class PanPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    private const string PanStatName = "almanacPanningRate";

    // ------------------------------------------------------------ registration

    private static readonly AccessTools.FieldRef<BlockPan, Dictionary<string, PanningDrop[]>>? panDropsRef =
        TryPanDrops();

    private static AccessTools.FieldRef<BlockPan, Dictionary<string, PanningDrop[]>>? TryPanDrops()
    {
        try { return AccessTools.FieldRefAccess<BlockPan, Dictionary<string, PanningDrop[]>>("dropsBySourceMat"); }
        catch { return null; }
    }

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, InjectPanDropStat);
        api.Event.RegisterGameTickListener(ReconcilePanYield, 2000);
    }

    /// <summary>Gives every stat-less pan drop entry our stat name so the vanilla CreateDrop
    /// multiplier path applies to the whole table (rusty gears keep their own stat).</summary>
    private static void InjectPanDropStat()
    {
        if (sapi == null || panDropsRef == null) return;
        int touched = 0;
        foreach (Block block in sapi.World.Blocks)
        {
            if (block is not BlockPan pan) continue;
            var table = panDropsRef(pan);
            if (table == null) continue;
            foreach (var drops in table.Values)
                foreach (var drop in drops)
                    if (drop.DropModbyStat == null) { drop.DropModbyStat = PanStatName; touched++; }
        }
        TcmLog.Info(sapi, $"PAN yield stat injected onto {touched} pan drop entries ({PanStatName})");
    }

    private static readonly Dictionary<string, double> lastPanFactor = new();

    private static void ReconcilePanYield(float dt)
    {
        if (sapi == null) return;
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            var entity = player.Entity;
            if (entity == null) continue;
            double factor = PanDomain.RankLinear(PanDomain.LevelOf(player),
                PanDomain.Knob(PanDomain.PanYieldUntrained, 0.85),
                PanDomain.Knob(PanDomain.PanYieldGm, 1.25));
            if (lastPanFactor.TryGetValue(player.PlayerUID, out double prev) && Math.Abs(prev - factor) < 0.001) continue;
            entity.Stats.Set(PanStatName, "almanactcm", (float)(factor - 1.0), false);
            lastPanFactor[player.PlayerUID] = factor;
        }
    }

    // ------------------------------------------------------------ panning practice

    [HarmonyPatch(typeof(BlockPan), nameof(BlockPan.OnHeldInteractStop))]
    public static class PanWashPatch
    {
        public static void Prefix(BlockPan __instance, float secondsUsed, ItemSlot slot, out bool __state)
        {
            // The method itself clears the material; remember whether this stop completes a
            // real wash (same guard as vanilla: 3.4s and material present).
            __state = secondsUsed >= 3.4f && __instance.GetBlockMaterialCode(slot?.Itemstack) != null;
        }

        public static void Postfix(EntityAgent byEntity, bool __state)
        {
            if (!__state || byEntity?.World?.Side != EnumAppSide.Server) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return;
            Core?.Ledger?.Log(player, PanDomain.Code, PanDomain.TechPanning,
                HashCode.Combine(PanDomain.TechPanning, byEntity.World.ElapsedMilliseconds / 1000));
        }
    }

    // ------------------------------------------------------------ prospecting: the funnel

    [HarmonyPatch(typeof(ModSystemOreMap), nameof(ModSystemOreMap.DidProbe))]
    public static class DidProbePatch
    {
        /// <summary>Axis 1, the data end: an Untrained reading quantizes to a coarse grid
        /// before it is recorded, so the map (and every share of it) remembers that an
        /// unpracticed hand took it. Novice I+ records exactly vanilla.</summary>
        public static void Prefix(PropickReading results, IServerPlayer splr)
        {
            if (results?.OreReadings == null || splr == null) return;
            if (PanDomain.LevelOf(splr) > 0) return;

            double fStep = PanDomain.Knob(PanDomain.CoarsenFactorStep, 0.1);
            double pStep = PanDomain.Knob(PanDomain.CoarsenPptStep, 0.5);
            foreach (var reading in results.OreReadings.Values)
            {
                if (fStep > 0) reading.TotalFactor = Math.Round(reading.TotalFactor / fStep) * fStep;
                if (pStep > 0) reading.PartsPerThousand = Math.Round(reading.PartsPerThousand / pStep) * pStep;
            }
        }

        /// <summary>Every recorded reading is prospecting practice, whatever tool took it
        /// (vanilla propick, BP density, BP stone scan). Context = the chunk column, so
        /// re-reading the same ground dedups inside the window. Master+ readings also record
        /// the depth band (the Surveyor, Phase 2).</summary>
        public static void Postfix(PropickReading results, IServerPlayer splr)
        {
            if (splr == null || results?.Position == null) return;
            Core?.Ledger?.Log(splr, PanDomain.Code, PanDomain.TechProspecting,
                HashCode.Combine((int)results.Position.X >> 5, (int)results.Position.Z >> 5));
            PanSurveyor.OnReading(results, splr);
        }
    }

    // ------------------------------------------------------------ placer-tracing (Phase 2)

    /// <summary>Samples the region ore maps at a column, exactly the way GenProbeResults does
    /// (the medieval inference loop runs on the SAME deterministic data the propick reads).</summary>
    internal static Dictionary<string, double>? SampleOreFactors(ICoreServerAPI api, BlockPos atPos)
    {
        var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");
        if (ppws?.depositsByCode == null) return null;
        var ba = api.World.BlockAccessor;
        int regsize = ba.RegionSize;
        var reg = ba.GetMapRegion(atPos.X / regsize, atPos.Z / regsize);
        if (reg?.OreMaps == null) return null;

        int lx = atPos.X % regsize, lz = atPos.Z % regsize;
        var pos = atPos.Copy();
        pos.Y = ba.GetTerrainMapheightAt(pos);
        int[] blockColumn = ppws.GetRockColumn(pos.X, pos.Z);

        var factors = new Dictionary<string, double>();
        foreach (var val in reg.OreMaps)
        {
            var map = val.Value;
            int noiseSize = map.InnerSize;
            int oreDist = map.GetUnpaddedColorLerped((float)lx / regsize * noiseSize, (float)lz / regsize * noiseSize);
            if (!ppws.depositsByCode.TryGetValue(val.Key, out var variant)) continue;
            variant.GetPropickReading(pos, oreDist, blockColumn, out _, out double totalFactor);
            if (totalFactor > 0) factors[val.Key] = totalFactor;
        }
        return factors;
    }

    /// <summary>Placer-tracing, the ruled crown jewel: at wash time the pan drop table is
    /// biased toward the ores ACTUALLY in the ore maps below the pan — walk a valley and feel
    /// the copper strengthen as you near the lode. Novice pans blind (vanilla); Apprentice/
    /// Journeyman get a faint NOISY trace; Master+ reads clean. Implemented as a temporary
    /// chance mutation restored in the postfix, so vanilla's own roll logic runs untouched and
    /// other callers (the Panning Machine's entity has no rank) see the stock table.</summary>
    [HarmonyPatch(typeof(BlockPan), "CreateDrop")]
    public static class PlacerTracePatch
    {
        [ThreadStatic] private static List<(PanningDrop drop, float origAvg)>? mutated;

        public static void Prefix(BlockPan __instance, EntityAgent byEntity, string fromBlockCode)
        {
            Restore(); // safety: never stack a stale mutation
            if (byEntity?.World?.Side != EnumAppSide.Server || sapi == null || panDropsRef == null) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            int level = PanDomain.LevelOf(player);
            double strength = PanDomain.TraceStrengthFor(level);
            if (strength <= 0) return;

            var factors = SampleOreFactors(sapi, byEntity.Pos.AsBlockPos);
            if (factors == null || factors.Count == 0) return;

            // Below Master the signal wavers: the trace term rolls 30-100% each wash.
            if (level < PanSurveyor.MasterLevel) strength *= 0.3 + sapi.World.Rand.NextDouble() * 0.7;

            var table = panDropsRef(__instance);
            if (table == null) return;
            foreach (var kv in table)
            {
                if (!WildcardUtil.Match(kv.Key, fromBlockCode)) continue;
                foreach (var drop in kv.Value)
                {
                    string? path = drop.Code?.Path;
                    if (path == null) continue;
                    foreach (var ore in factors)
                    {
                        if (!path.Contains(ore.Key)) continue;
                        double mult = 1.0 + strength * Math.Min(1.0, ore.Value / 0.25);
                        (mutated ??= new()).Add((drop, drop.Chance.avg));
                        drop.Chance.avg *= (float)mult;
                        break;
                    }
                }
            }
        }

        public static void Postfix() => Restore();

        private static void Restore()
        {
            if (mutated == null) return;
            foreach (var (drop, orig) in mutated) drop.Chance.avg = orig;
            mutated = null;
        }
    }

    // ------------------------------------------------------------ BP search modes (conditional)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("bettererprospecting")) return;

        var pick = AccessTools.TypeByName("BetterErProspecting.ItemBetterErProspectingPick");
        int hooked = 0;
        foreach (string mode in new[] { "ProbeNode", "ProbeProximity", "ProbeBorehole" })
        {
            var m = pick == null ? null : AccessTools.Method(pick, mode);
            if (m == null) continue;
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(BpSearchModePatch), "Postfix")));
            hooked++;
        }
        if (hooked == 0)
        {
            TcmLog.Warn(api, "bettererprospecting present but its probe modes were not found; search-mode practice inactive (density/stone still credit via DidProbe)");
            return;
        }
        TcmLog.Info(api, $"PAN prospecting hooked to bettererprospecting ({hooked} search mode(s); density/stone credit via the DidProbe funnel)");
    }

    /// <summary>Node/proximity/borehole write no readings but are real prospecting work.
    /// Position-bucket context: hammering one spot dedups, walking a survey line earns.</summary>
    public static class BpSearchModePatch
    {
        public static void Postfix(IServerPlayer serverPlayer, BlockSelection blockSel)
        {
            if (serverPlayer == null || blockSel?.Position == null) return;
            Core?.Ledger?.Log(serverPlayer, PanDomain.Code, PanDomain.TechProspecting,
                HashCode.Combine(blockSel.Position.X >> 4, blockSel.Position.Z >> 4));
        }
    }
}

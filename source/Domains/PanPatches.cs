using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
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
        /// <summary>The CLARITY LADDER (ruled 2026-07-17, the mode-workflow ruling): density is
        /// the chunk survey and rank gates how truly you read it. BP's density is a real block
        /// census, so degradation always UNDERSTATES — it never invents richness and never
        /// hides an ore outright (weakest lines demote to the visible traces list at worst).
        ///   Untrained: two density words down, ppt to one significant figure.
        ///   Novice: one word down, one significant figure.
        ///   Apprentice: the true word, ppt still one significant figure.
        ///   Journeyman+: the full census, exactly as the mod reports it.
        /// The recorded data carries the degradation, so every PT share remembers the skill of
        /// the surveyor who took it.</summary>
        public static void Prefix(PropickReading results, IServerPlayer splr)
        {
            if (results?.OreReadings == null || splr == null) return;
            int level = PanDomain.LevelOf(splr);
            if (level >= 9) return; // Journeyman I+: full truth

            int bandsDown = level <= 0 ? 2 : level <= 4 ? 1 : 0;
            const double band = 1.0 / 7.5; // one density-word band (names index = tf * 7.5)
            foreach (var reading in results.OreReadings.Values)
            {
                if (bandsDown > 0)
                    reading.TotalFactor = Math.Max(0.003, reading.TotalFactor - bandsDown * band);
                reading.PartsPerThousand = OneSigFig(reading.PartsPerThousand);
            }
        }

        private static double OneSigFig(double v)
        {
            if (v <= 0) return 0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            return Math.Round(v / mag) * mag;
        }

        /// <summary>Every recorded reading is prospecting practice, whatever tool took it
        /// (vanilla propick, BP density, BP stone scan). Context = the chunk column, so
        /// re-reading the same ground dedups inside the window. Depth information moved to
        /// the BORE (the mode-workflow ruling): the drill measures, the survey estimates.</summary>
        public static void Postfix(PropickReading results, IServerPlayer splr)
        {
            if (splr == null || results?.Position == null) return;
            Core?.Ledger?.Log(splr, PanDomain.Code, PanDomain.TechProspecting,
                HashCode.Combine((int)results.Position.X >> 5, (int)results.Position.Z >> 5));
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

        // Mode order = the taught workflow (ruled 2026-07-17): survey the chunk, drill before
        // you dig, then the pick tools. Dispatch is name-based, so reordering the SkillItem
        // array is safe; the postfix re-applies whenever BP regenerates its modes.
        var regen = pick == null ? null : AccessTools.Method(pick, "RegenerateToolModes");
        if (regen != null)
            harmony.Patch(regen, postfix: new HarmonyMethod(AccessTools.Method(typeof(ModeOrderPatch), "Postfix")));

        // The bore is the depth tool (the mode-workflow ruling): Master+ measures the drilled
        // column and reads how deep each ore actually sits.
        var bore = pick == null ? null : AccessTools.Method(pick, "ProbeBorehole");
        BoreDepthPatch.isOreMethod = pick == null ? null : AccessTools.Method(pick, "IsOre",
            new[] { typeof(Block), typeof(Dictionary<string, string>), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() });
        if (bore != null && BoreDepthPatch.isOreMethod != null)
            harmony.Patch(bore, postfix: new HarmonyMethod(AccessTools.Method(typeof(BoreDepthPatch), "Postfix")));

        TcmLog.Info(api, $"PAN prospecting hooked to bettererprospecting ({hooked} search mode(s); mode order = workflow; Master+ bore measures depth)");
    }

    /// <summary>Reorders the propick modes to the taught workflow: density (survey), borehole
    /// (drill before digging), node search and proximity (at the face), stone scan last.</summary>
    public static class ModeOrderPatch
    {
        private static readonly string[] order = { "density", "borehole", "node", "proximity", "stone" };

        public static void Postfix(object __instance)
        {
            if (Traverse.Create(__instance).Field("toolModes").GetValue() is not SkillItem[] modes || modes.Length < 2) return;
            var sorted = modes.OrderBy(m =>
            {
                int i = Array.IndexOf(order, m?.Code?.Path);
                return i < 0 ? 99 : i;
            }).ToArray();
            for (int i = 0; i < modes.Length; i++) modes[i] = sorted[i]; // in place: field and cache share this array
        }
    }

    /// <summary>Master+ bore depth: rescans the same cylinder the drill walked, recording the
    /// depth of every ore it passed. Master hears a coarse first strike ("near 32 down"); GM
    /// reads the exact band and it is RECORDED to the chunk's depth store — the Surveyor's
    /// shared maps carry depths a GM physically measured, not worldgen estimates.</summary>
    public static class BoreDepthPatch
    {
        internal static System.Reflection.MethodInfo? isOreMethod;

        public static void Postfix(object __instance, IServerPlayer serverPlayer, BlockSelection blockSel)
        {
            if (sapi == null || serverPlayer == null || blockSel?.Position == null || isOreMethod == null) return;
            if (blockSel.Face != BlockFacing.UP) return; // the drill itself refused sideways bores
            int level = PanDomain.LevelOf(serverPlayer);
            if (level < PanSurveyor.MasterLevel) return;

            int radius = 1;
            try
            {
                var cfg = Traverse.Create(__instance).Field("config").GetValue();
                if (cfg != null)
                {
                    var t = Traverse.Create(cfg);
                    radius = t.Field("BoreholeRadius").FieldExists() ? t.Field("BoreholeRadius").GetValue<int>()
                        : t.Property("BoreholeRadius").GetValue<int>();
                }
            }
            catch { }
            radius = Math.Max(1, radius);

            var ba = sapi.World.BlockAccessor;
            int startX = blockSel.Position.X, startY = blockSel.Position.Y, startZ = blockSel.Position.Z;
            var cache = new Dictionary<string, string>();
            var found = new Dictionary<string, (int min, int max)>();
            var args = new object?[4];
            var probe = new BlockPos(0);
            for (int y = startY; y > 0; y--)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (dx * dx + dz * dz > radius * radius) continue;
                        probe.Set(startX + dx, y, startZ + dz);
                        var block = ba.GetBlock(probe);
                        if (block == null) continue;
                        args[0] = block; args[1] = cache; args[2] = null; args[3] = null;
                        if (isOreMethod.Invoke(null, args) is not true) continue;
                        string? oreKey = args[3] as string;
                        if (string.IsNullOrEmpty(oreKey)) continue;
                        int depth = startY - y;
                        found[oreKey!] = found.TryGetValue(oreKey!, out var band)
                            ? (Math.Min(band.min, depth), Math.Max(band.max, depth))
                            : (depth, depth);
                    }
                }
            }
            if (found.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "almanactcm:bore-title"));
            var stored = new List<PanSurveyor.PanOreBand>();
            int listed = 0;
            foreach (var kv in found)
            {
                if (listed++ >= 5) break;
                string ore = Lang.GetL(serverPlayer.LanguageCode, "ore-" + kv.Key);
                if (level >= 17)
                {
                    sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "almanactcm:bore-exact", ore, kv.Value.min, kv.Value.max));
                    stored.Add(new PanSurveyor.PanOreBand { OreKey = kv.Key, MinDepth = kv.Value.min, MaxDepth = kv.Value.max });
                }
                else
                {
                    int near = Math.Max(4, (int)Math.Round(kv.Value.min / 8.0) * 8);
                    sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "almanactcm:bore-near", ore, near));
                }
            }
            serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString().TrimEnd(), EnumChatType.Notification);
            if (stored.Count > 0) PanSurveyor.RecordBoreBands(serverPlayer, blockSel.Position, stored);
        }
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

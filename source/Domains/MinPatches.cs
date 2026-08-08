using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// MIN always-present (vanilla) hooks: the mining/knapping practice verbs, the
/// cave-in stability axis (Axis 3), and the ore-yield stat reconcile (Axis 4/1).
/// Optional-mod seams (ImmersiveMining stamina, StoneQuarry quarrying) live in
/// <see cref="MinConditionalPatches"/>. Quarrying and stamina are inert without
/// their mods; mining, knapping and cave-ins are pure vanilla and always live.
/// </summary>
public static class MinPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    private static double Knob(string key, double fallback) => MinDomain.Knob(key, fallback);

    // ---- cave-in attribution state (server thread, synchronous break→check→roll chain)
    [ThreadStatic] private static IPlayer? currentMiner;
    [ThreadStatic] private static int checkDepth;

    private static MethodInfo? searchCollapsibleMI;

    // ============================================================ Axis 4/1 — ore-yield stat

    /// <summary>Registers the server-side, zero-Harmony bonuses: the knapping practice
    /// listener and the oreDropRate reconcile. Called from StartServerSide.</summary>
    private static IServerWorldAccessor? serverWorld;

    public static void RegisterServer(ICoreServerAPI sapi)
    {
        serverWorld = sapi.World;
        sapi.Event.RegisterEventBusListener(OnItemKnapped, 0.5, "onitemknapped");
        sapi.Event.RegisterGameTickListener(_ => ReconcileOreYield(sapi), 2000);
        TcmLog.Info(sapi, "MIN hooks live (mining, knapping, cave-in stability, ore-yield)");
    }

    private static readonly Dictionary<string, double> lastOreYield = new();

    /// <summary>Sets each online player's vanilla oreDropRate stat by MIN rank. Robust to
    /// /tcm setlevel (a tick reconcile, not an event on the grant path), and only writes
    /// when the target actually changed, so WatchedAttributes stay quiet between rank-ups.</summary>
    private static void ReconcileOreYield(ICoreServerAPI sapi)
    {
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            EntityPlayer? entity = player?.Entity;
            if (entity == null) continue;

            double factor = MinDomain.RankLinear(MinDomain.LevelOf(player),
                Knob(MinDomain.OreYieldUntrained, 0.90), Knob(MinDomain.OreYieldGm, 1.15));

            if (lastOreYield.TryGetValue(player!.PlayerUID, out double prev) && Math.Abs(prev - factor) < 1e-4)
                continue;

            // oreDropRate is WeightedSum with a base of 1.0, so a modifier of (factor-1)
            // makes GetBlended == factor (verified against BlockOre.OnBlockBroken).
            entity.Stats.Set("oreDropRate", "almanactcm", (float)(factor - 1.0), false);
            lastOreYield[player.PlayerUID] = factor;
        }
    }

    // ============================================================ practice verbs

    private static void OnItemKnapped(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (data is not ITreeAttribute tree || serverWorld == null) return;

        IPlayer? player = (serverWorld.GetEntityById(tree.GetLong("byentityid")) as EntityPlayer)?.Player;
        if (player == null) return;

        ItemStack? stack = tree.GetItemstack("itemstack");
        Core?.Ledger?.Log(player, MinDomain.Code, MinDomain.TechKnapping, stack?.Id ?? 0);
    }

    /// <summary>Stone breaks (plain Block, Stone material): a tiny flat value on the
    /// mining verb (Q1 — stone stays a flat outcome of the same swing). Ore is handled
    /// separately because BlockOre overrides OnBlockBroken without calling base.
    /// The pickaxe requirement is load-bearing: material Stone alone leaks, because VS
    /// defaults blocks with NO declared blockmaterial to Stone — the vanilla carcass
    /// block (the skeleton left after butchering a deer) paid mining XP when broken
    /// (the 2026-07-25 leak; Cartwright's cart carcass has the same latent default).
    /// The verb is a pickaxe swing per the technique map, so require the pickaxe.</summary>
    [HarmonyPatch(typeof(Block), nameof(Block.OnBlockBroken))]
    public static class MiningStonePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null) return;
            if (__instance.BlockMaterial != EnumBlockMaterial.Stone) return;
            if (byPlayer.InventoryManager?.ActiveTool != EnumTool.Pickaxe) return;

            // rock-* only (0.4.35, LauCaRo's report): material Stone alone also paid for
            // cobblestone and every other craftable stone block, which reads as being paid
            // to demolish your own build, the log-cabin problem again. Cobble has no
            // placed/worldgen variant split, so ruin cobble is the accepted casualty.
            // Pristine-rock replace-and-rebury remains technically farmable and is accepted:
            // strictly worse than mining fresh rock (7 placed per re-mine, same tiny credit,
            // day-curve capped). Do not re-litigate per report.
            if (!__instance.Code.Path.StartsWith("rock-")) return;

            Core?.Ledger?.Log(byPlayer, MinDomain.Code, MinDomain.TechMining,
                pos.GetHashCode(), Knob(MinDomain.MiningStoneFraction, 0.2));
        }
    }

    /// <summary>Ore breaks: the mining verb scaled by depth (Q5). Rarity term is seeded
    /// in config but 0-effect until a per-ore table exists — documented gap.</summary>
    [HarmonyPatch(typeof(BlockOre), nameof(BlockOre.OnBlockBroken))]
    public static class MiningOrePatch
    {
        public static void Postfix(BlockOre __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null) return;

            double depth = DepthProxy(world, pos);
            double mult = 1.0 + depth * Knob(MinDomain.MiningDepthCoeff, 0.5);
            Core?.Ledger?.Log(byPlayer, MinDomain.Code, MinDomain.TechMining, pos.GetHashCode(), mult);
        }
    }

    /// <summary>0 at/above sea level, →1 at bedrock: deeper rock is richer practice.</summary>
    private static double DepthProxy(IWorldAccessor world, BlockPos pos)
    {
        int sea = world.SeaLevel;
        if (sea <= 0) return 0;
        return GameMath.Clamp((sea - pos.Y) / (double)sea, 0, 1);
    }

    // ============================================================ Axis 3 — cave-in stability

    /// <summary>Stashes the miner around the synchronous break→check chain and, for an
    /// Untrained miner, fires an extra neighbour sweep (the penalty end). Cascades,
    /// explosions and placements never pass through here, so they stay vanilla.</summary>
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock), nameof(BlockBehaviorUnstableRock.OnBlockBroken))]
    public static class CaveInBreakPatch
    {
        public static void Prefix(IPlayer byPlayer) => currentMiner = byPlayer;

        public static void Postfix(BlockBehaviorUnstableRock __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            try
            {
                if (world.Side != EnumAppSide.Server || byPlayer == null) return;
                if (!__instance.CaveIns || !__instance.AllowFallingBlocks) return;

                double f = CaveFactor(byPlayer);
                if (f > 1.0 && world.Rand.NextDouble() < f - 1.0)
                    NeighbourSweep(__instance, world, pos);
            }
            finally { currentMiner = null; }
        }
    }

    /// <summary>The "the cut held" save, applied ONLY to the miner's own top-level checks
    /// (checkDepth 0) on CONNECTED rock. Isolated (Unconnected) rock always falls, and any
    /// check with no stashed miner (cascade/explosion/placement) runs vanilla untouched.</summary>
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock), nameof(BlockBehaviorUnstableRock.CheckCollapsible))]
    public static class CaveInRollPatch
    {
        public static bool Prefix(BlockBehaviorUnstableRock __instance, IWorldAccessor world, BlockPos pos, ref bool __result)
        {
            bool topLevel = checkDepth == 0;
            checkDepth++;
            if (!topLevel) return true;

            IPlayer? miner = currentMiner;
            if (miner == null) return true;
            if (!__instance.CaveIns || !__instance.AllowFallingBlocks) return true;

            double f = CaveFactor(miner);
            if (f >= 1.0) return true; // Untrained handled by the extra sweep; Novice = vanilla

            try
            {
                searchCollapsibleMI ??= AccessTools.Method(typeof(BlockBehaviorUnstableRock), "searchCollapsible");
                var res = searchCollapsibleMI?.Invoke(__instance, new object[] { pos, false }) as CollapsibleSearchResult;
                if (res != null && !res.Unconnected && world.Rand.NextDouble() < 1.0 - f)
                {
                    __result = false;
                    return false; // the cut held
                }
            }
            catch { /* reflection failure must never break mining — fall through to vanilla */ }
            return true;
        }

        // Finalizer runs even if the original throws, so the depth counter can't leak.
        public static void Finalizer()
        {
            if (checkDepth > 0) checkDepth--;
        }
    }

    /// <summary>Replicates vanilla checkCollapsibleNeighbours (private) using the public
    /// CheckCollapsible, so the Untrained penalty fires extra rolls at vanilla odds.</summary>
    private static void NeighbourSweep(BlockBehaviorUnstableRock bh, IWorldAccessor world, BlockPos pos)
    {
        var faces = (BlockFacing[])BlockFacing.ALLFACES.Clone();
        GameMath.Shuffle(world.Rand, faces);
        for (int i = 0; i < faces.Length && i < 3; i++)
        {
            if (bh.CheckCollapsible(world, pos.AddCopy(faces[i]))) break;
        }
    }

    /// <summary>Optional flavor (D4): sharpen the look-at instability readout by MIN rank.
    /// Runs client-side, so it reads the synced client rank. Untrained/Novice see a coarse
    /// band; Apprentice/Journeyman a rough percent; Master/GM the exact figure.</summary>
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock), nameof(BlockBehaviorUnstableRock.GetPlacedBlockInfo))]
    public static class CaveInReadoutPatch
    {
        public static void Postfix(BlockBehaviorUnstableRock __instance, BlockPos pos, IPlayer forPlayer, ref string __result)
        {
            if (forPlayer == null || !__instance.CaveIns || !__instance.AllowFallingBlocks) return;

            double inst;
            try { inst = __instance.getInstability(pos); }
            catch { return; }

            int tier = Leveling.Domain.TierOf(ClientMinLevel());
            if (tier <= 0)
            {
                // Coarse band, no number — an untrained eye can't read the rock.
                string key = inst < 0.34 ? "almanactcm:instability-steady"
                    : inst < 0.67 ? "almanactcm:instability-uneasy"
                    : "almanactcm:instability-dangerous";
                __result = Lang.Get(key);
            }
            else if (tier <= 2)
            {
                // Rough percent, rounded to the nearest 10 — a working read.
                __result = Lang.Get("instability-percent", Math.Round(inst * 10) * 10);
            }
            // Master/GM (tier >= 3): leave vanilla's exact percent as-is.
        }
    }

    private static int ClientMinLevel()
    {
        // Client read (the instability tooltip), so it resolves the client instance, not Core.
        var core = AlmanacTcmModSystem.ClientInstance;
        int id = core?.Template?.FindDomain(MinDomain.Code)?.Id ?? -1;
        if (id < 0 || core?.Client == null) return 0;
        return core.Client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static double CaveFactor(IPlayer player) => MinDomain.RankLinear(MinDomain.LevelOf(player),
        Knob(MinDomain.CaveinUntrained, 1.5), Knob(MinDomain.CaveinGm, 0.5));
}

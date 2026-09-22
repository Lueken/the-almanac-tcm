using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// Wilderlands Stonebound wiring (RULED 2026-09-22, designed with Thalius on Discord; verified
/// against a decompile of WStonebound 1.1.6, not the mod page).
///
/// Stonebound's flow: breaking stone or ore converts the block to a four-layer rubble block;
/// each layer is worked down with a pick or shovel and each removal is a loot roll off a
/// per-rock drop table (rare); the spoil that comes out is panned for more. The FIRST break
/// already paid mining. The rest paid nothing, and it could not have paid by accident: all
/// three rubble classes OVERRIDE OnBlockBroken without calling base, and a Harmony patch on
/// the base method never sees an override, so TCM's own mining seam was structurally blind
/// to them.
///
/// The ruling (Jeffrey, Discord 2026-09-21): a layer worked down is base mining at a fraction
/// of a clean block's swing, plus a prospecting co-grant ONLY when the loot roll procs, since
/// a proc is the ground telling you something. Spoil panning needed no wiring at all:
/// Stonebound patches its spoil into the vanilla pan's own drop table, and TCM's wash grant,
/// pan-yield stat, grave-sifter and placer trace are all source-agnostic, so the whole PAN kit
/// was already live on spoil the moment both mods loaded.
///
/// Proc detection is a before/after count of item entities near the block, captured in the
/// prefix and compared in the postfix. The drop table spawns entities directly (no GetDrops
/// path to read), so counting the world is the only seam that does not re-implement their
/// table. A player standing in a pile of dropped items could in principle mask a proc or fake
/// one by throwing something mid-swing; both cost more than the co-grant pays.
/// </summary>
public static class MinStoneboundPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    /// <summary>The three layered rubble classes, verified in the 1.1.6 assembly. The fourth
    /// (BlockRubbleBituCoal) is deliberately absent: it extends the vanilla ore chain and calls
    /// base.OnBlockBroken, so the ordinary ore seam already pays it.</summary>
    private static readonly string[] RubbleTypes =
    {
        "WStonebound.RockRubbleClass",
        "WStonebound.BlockOreRubbleGraded",
        "WStonebound.BlockOreRubbleUngraded",
    };

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("wstonebound")) return;

        int hooked = 0;
        foreach (string typeName in RubbleTypes)
        {
            var t = AccessTools.TypeByName(typeName);
            var m = t == null ? null : AccessTools.DeclaredMethod(t, "OnBlockBroken");
            if (m == null)
            {
                TcmLog.Warn(api, $"wstonebound present but {typeName}.OnBlockBroken not found; that rubble class pays nothing");
                continue;
            }
            harmony.Patch(m,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(RubblePatch), nameof(RubblePatch.Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(RubblePatch), nameof(RubblePatch.Postfix))));
            hooked++;
        }
        if (hooked > 0)
            TcmLog.Info(api, $"MIN/PAN hooked to Stonebound rubble ({hooked} class(es): layer = mining fraction, loot proc = prospecting co-grant); spoil panning rides the vanilla pan seam untouched");
    }

    public static class RubblePatch
    {
        public readonly struct State
        {
            public readonly int BlockIdBefore;
            public readonly int ItemsBefore;
            public State(int blockId, int items) { BlockIdBefore = blockId; ItemsBefore = items; }
        }

        private static int NearbyItems(IWorldAccessor world, BlockPos pos) =>
            world.GetEntitiesAround(pos.ToVec3d().Add(0.5, 0.5, 0.5), 2f, 2f,
                e => e is EntityItem).Length;

        public static void Prefix(IWorldAccessor world, BlockPos pos, out State __state)
        {
            __state = world.Side != EnumAppSide.Server
                ? default
                : new State(world.BlockAccessor.GetBlockId(pos), NearbyItems(world, pos));
        }

        public static void Postfix(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, State __state)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null || __state.BlockIdBefore == 0) return;
            // The layer only moves on the valid-tool branch (their gate: pick or shovel), so
            // "the block changed" is both the success signal and the tool check for free.
            if (world.BlockAccessor.GetBlockId(pos) == __state.BlockIdBefore) return;

            // Each layer is its own block id, so the context is naturally per-layer and the
            // 90s ring still kills a place-and-rebreak of the same one.
            Core?.Ledger?.Log(byPlayer, MinDomain.Code, MinDomain.TechMining,
                HashCode.Combine("sbrubble", pos.X, pos.Y, pos.Z, __state.BlockIdBefore),
                MinDomain.Knob(MinDomain.RubbleLayerFraction, 0.25));

            // The loot proc: the ground answered, and reading that answer is prospecting.
            // 0.5 co-grant, the ruled TEM/ARC co-grant fraction, sharing the layer's position.
            if (NearbyItems(world, pos) > __state.ItemsBefore)
            {
                Core?.Ledger?.Log(byPlayer, PanDomain.Code, PanDomain.TechProspecting,
                    HashCode.Combine("sbproc", pos.X, pos.Y, pos.Z, __state.BlockIdBefore), 0.5);
                TcmLog.Cat(world.Api, "pan", $"Stonebound loot proc at {pos} -> prospecting co-grant for {byPlayer.PlayerName}");
            }
        }
    }
}

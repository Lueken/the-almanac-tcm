using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// WOO — THE STUMP (RULED 2026-08-30; brief: projects/briefs/idg-divorce Phase 4, seam study:
/// fallingtree-seam-study.md). Felling a tree at the base leaves the butt in the ground as a
/// distinct almanactcm:stump-{wood} block: the fell set already starts at the strike point in
/// both vanilla and FallingTree, so the whole mechanic is keeping the base block from being
/// removed and swapping it in place.
///
/// Always-on seams (vanilla felling too), all server-side:
///  - ARM: priority-first prefix on ItemAxe.OnBlockBrokenWith decides ONCE, before anything
///    breaks, whether this strike leaves a stump (true base, real tree above, stump variant
///    exists), and stashes the position.
///  - SWAP: prefix on Block.OnBlockBroken cancels the armed base's removal and SetBlocks the
///    stump in its place. One prefix covers every removal route. Never BreakBlock here — that
///    re-enters this prefix disarmed and pays the player the base log on top of the stump.
///  - DISARM: finalizer on ItemAxe.OnBlockBrokenWith clears all state every swing, even when
///    FallingTree's own prefix returned false (finalizers still run) or something threw.
///
/// FallingTree-conditional seams (first live test 2026-08-29 reshaped all three):
///  - GHOST: the base position's EntityBlockFalling is not cancelled (v1 did, and the felled
///    trunk floated one block above the stump with its bottom log missing). It spawns as a
///    GHOST: DoRemoveBlock=false so its Initialize doesn't SetBlock(0) the freshly set stump
///    on either side (the field serializes to the client, whose own Initialize would repeat
///    the wipe locally), and a watched attribute marks it for the landing patch. The trunk
///    visibly shears off the stump and falls whole.
///  - GHOST LANDING: priority-first prefix on EntityBlockFalling.OnFallToGround despawns the
///    ghost before FallingTree's own prefix can place it as a log or vanilla can drop it.
///    The base log yields nothing, exactly the ruled yield tax; without this the tree pays
///    full logs AND a stump.
///  - NEVER FLY: any EntityBlockFalling whose block IS a stump is refused at SpawnEntity.
///    FallingTree's domino sweep knocks non-log wood loose as a falling entity that lands and
///    drops the block itself — live test: felling beside a stump popped it off the ground and
///    a "Pine stump" item landed in the feller's hotbar. Refusing the spawn also skips the
///    entity's Initialize, so the stump is never removed. It reads blockCode by field ref:
///    the Block property walks World, which is null before Initialize (the v1 NRE lesson —
///    same lesson as logging with the entity's Api at spawn time).
///  - NO LANDING THEFT: FallingTree's CanReplaceWith allows a landing log to claim any cell
///    whose occupant's Replaceable >= its own (0 >= 0 included), and a log coming to rest on
///    a 6/16-tall stump has its feet inside the stump's cell. Forced false for stumps; the
///    resolver then tries the cell above, so the trunk lies ON the stump instead of eating it.
///
/// The WOO felling credit is untouched by design: FellSwingPatch stashes in its prefix and
/// banks in its finalizer, and FellingPracticePatch's postfix on Block.OnBlockBroken still
/// runs when our prefix returns false, with __instance still the log. The stump block itself
/// carries NO treeFellingGroupCode, so FindTree never sweeps it into a neighbouring fell set
/// and clearing one never pays felling practice.
/// </summary>
public static class WooStumpPatches
{
    private const string GhostAttr = "almanactcm:stumpghost";

    // The break→fell chain is synchronous on the server thread (same assumption WooPatches and
    // WooFallingTreePatches document), so ThreadStatic state is safe per player.
    [ThreadStatic] private static BlockPos? swapPos;
    [ThreadStatic] private static int swapStumpId;
    [ThreadStatic] private static BlockPos? ghostPos;

    /// <summary>For logging from seams where the entity's own Api/World is not built yet
    /// (SpawnEntity runs before Initialize). Captured once in PatchConditional.</summary>
    private static ICoreAPI? logApi;

    /// <summary>blockCode is private and the Block property needs World (null pre-Initialize),
    /// so the never-fly guard reads the field directly.</summary>
    private static readonly AccessTools.FieldRef<EntityBlockFalling, AssetLocation> BlockCodeRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, AssetLocation>("blockCode");

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        logApi = api;
        var axeBreak = AccessTools.Method(typeof(ItemAxe), nameof(ItemAxe.OnBlockBrokenWith));
        var blockBroken = AccessTools.Method(typeof(Block), nameof(Block.OnBlockBroken));
        if (axeBreak == null || blockBroken == null)
        {
            TcmLog.Warn(api, "OnBlockBrokenWith/OnBlockBroken not found; WOO stumps inactive");
            return;
        }

        // Priority.First for the same reason FellSwingPatch documents: FallingTree's prefix on
        // this method runs the whole fell and returns false, and Harmony skips every prefix
        // after a false. The arm must land before the first BreakBlock does.
        harmony.Patch(axeBreak,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ArmPatch), "Prefix")) { priority = Priority.First },
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(ArmPatch), "Finalizer")));
        harmony.Patch(blockBroken,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(SwapPatch), "Prefix")));
        TcmLog.Info(api, "WOO stumps hooked (felling leaves the butt standing as a stump)");

        // Vanilla felling spawns no entities; the ghost, the landing kill, the never-fly guard
        // and the landing-theft guard only exist while FallingTree does.
        if (!api.ModLoader.IsModEnabled("fallingtree")) return;

        var serverMain = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
        var spawnEntity = serverMain == null ? null
            : AccessTools.Method(serverMain, "SpawnEntity", new[] { typeof(Entity) });
        var fallToGround = AccessTools.Method(typeof(EntityBlockFalling), nameof(EntityBlockFalling.OnFallToGround));
        var ftPatch = AccessTools.TypeByName("FallingTree.FallingEntityPatch");
        var canReplace = ftPatch == null ? null : AccessTools.Method(ftPatch, "CanReplaceWith");
        if (spawnEntity == null || fallToGround == null || canReplace == null)
        {
            TcmLog.Warn(api, "SpawnEntity/OnFallToGround/CanReplaceWith not all found; WOO stumps inactive with FallingTree present (an unguarded faller would wipe or fly the stump)");
            // Better no stump than one the fallers erase or launch: ArmPatch checks this flag.
            fallerSeamsLive = false;
            return;
        }

        harmony.Patch(spawnEntity,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GhostPatch), "Prefix")));
        harmony.Patch(fallToGround,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GhostLandingPatch), "Prefix")) { priority = Priority.First });
        harmony.Patch(canReplace,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(NoLandingTheftPatch), "Prefix")));
        fallerSeamsLive = true;
        TcmLog.Info(api, "WOO stumps: ghost faller + never-fly + landing-theft guards hooked (FallingTree present)");
    }

    /// <summary>False only when FallingTree is present AND its faller seams failed to resolve,
    /// in which case arming would produce a stump the fallers immediately destroy. True when
    /// FallingTree is absent (vanilla felling spawns nothing to guard against).</summary>
    private static bool fallerSeamsLive = true;

    private static bool IsStumpBlockCode(AssetLocation? code)
        => code != null && code.Domain == "almanactcm" && code.PathStartsWith("stump-");

    /// <summary>Decides the whole strike up front. Every bail-out leaves state null, which makes
    /// the other patches transparent. The wood-variant test quietly excludes bamboo, fern trees
    /// and fruit trees (color/type variants, never wood) exactly as the design ruled, and the
    /// missing-stump bail is the whole modded-species and redwood story: their base simply
    /// stays standing as the log it already is.</summary>
    public static class ArmPatch
    {
        public static void Prefix(IWorldAccessor world, Entity byEntity, BlockSelection blockSel)
        {
            swapPos = null;
            ghostPos = null;
            swapStumpId = 0;

            if (!fallerSeamsLive) return;
            if (world.Side != EnumAppSide.Server || blockSel?.Position == null) return;
            if ((byEntity as EntityPlayer)?.Player == null) return;

            BlockPos pos = blockSel.Position;
            Block struck = world.BlockAccessor.GetBlock(pos);

            // Only living tree wood fells; placed/debarked/carved wood has no felling group.
            if (struck.BlockMaterial != EnumBlockMaterial.Wood) return;
            string? group = struck.Attributes?["treeFellingGroupCode"].AsString();
            if (string.IsNullOrEmpty(group)) return;

            // Not the base: the block below is part of the same tree. A mid-trunk strike fells
            // from the strike point (shipped behaviour, both pipelines) and converts nothing —
            // a stump with root flare six blocks up a trunk is nonsense.
            Block below = world.BlockAccessor.GetBlock(pos.DownCopy());
            if (below.Attributes?["treeFellingGroupCode"].AsString() == group) return;

            // Lone log: nothing of this tree stands above, so the fell set is empty and vanilla
            // falls through to a NORMAL break routed through Block.OnBlockBroken — the method the
            // swap prefix cancels. Arming here would make the block unbreakable-into-stump on a
            // strike the player meant as an ordinary break. Sharpest failure mode; bail.
            Block above = world.BlockAccessor.GetBlock(pos.UpCopy());
            if (above.BlockMaterial != EnumBlockMaterial.Wood
                || above.Attributes?["treeFellingGroupCode"].AsString() != group) return;

            // Variant lookup, not code-part indexing: works for any block declaring a wood
            // variantgroup, and returns null for bamboo/fern/fruit trees, which bail here.
            string? wood = struck.Variant["wood"];
            if (string.IsNullOrEmpty(wood)) return;
            Block? stump = world.GetBlock(new AssetLocation("almanactcm", "stump-" + wood));
            if (stump == null) return;

            swapPos = pos.Copy();
            ghostPos = pos.Copy();
            swapStumpId = stump.BlockId;
            TcmLog.Cat(world.Api, TcmLog.Hooks, $"WOO stump armed: {struck.Code} at {pos} -> {stump.Code}");
        }

        /// <summary>Finalizer, not a postfix: must clear even on the FallingTree false-prefix
        /// path and on a mid-fell throw, or the next swing inherits a stale arm and eats one
        /// unrelated break. Runs after FallingTree's own postfix has spawned the fallers, so
        /// ghostPos is still live when the base faller arrives on the /ftplace false path.</summary>
        public static void Finalizer()
        {
            swapPos = null;
            ghostPos = null;
            swapStumpId = 0;
        }
    }

    /// <summary>The swap. Returning false skips SpawnDropsAndRemoveBlock entirely — no drops, no
    /// item entities, no SetBlock(0) — and the SetBlock overwrites the log in place. Disarms
    /// before writing, so a re-entrant call could at most pass through transparently. Runs before
    /// FellingPracticePatch's postfix on this same method, which still counts the base log for
    /// the felling credit (postfixes run regardless of a false prefix).</summary>
    public static class SwapPatch
    {
        public static bool Prefix(IWorldAccessor world, BlockPos pos)
        {
            if (swapPos == null || !swapPos.Equals(pos)) return true;
            if (world.Side != EnumAppSide.Server) return true;

            int stumpId = swapStumpId;
            swapPos = null;
            world.BlockAccessor.SetBlock(stumpId, pos);
            TcmLog.Cat(world.Api, TcmLog.Hooks, $"WOO stump set at {pos}");
            return false;
        }
    }

    /// <summary>Two rules at the server's entity spawn, both about EntityBlockFalling and both
    /// bound by index (the parameter name is nobody's contract — the aqueducts lesson):
    /// a stump block never flies, and the base faller spawns as a ghost instead of dying.</summary>
    public static class GhostPatch
    {
        public static bool Prefix(Entity __0)
        {
            if (__0 is not EntityBlockFalling faller) return true;

            // Never fly: refuse the spawn outright (the domino toss). Initialize never runs,
            // so the stump block is never removed either — it simply stays in the ground.
            if (IsStumpBlockCode(BlockCodeRef(faller)))
            {
                if (logApi != null) TcmLog.Cat(logApi, TcmLog.Hooks,
                    $"WOO stump: refused a flying stump at {faller.initialPos}");
                return false;
            }

            // The base faller becomes the ghost: it flies so the trunk visibly shears off the
            // stump, but it must not wipe the stump on either side (DoRemoveBlock serializes)
            // and the landing patch will despawn it before it can pay out.
            if (ghostPos != null && faller.initialPos != null && ghostPos.Equals(faller.initialPos))
            {
                ghostPos = null; // one base, one faller; shrink the window immediately
                faller.DoRemoveBlock = false;
                faller.WatchedAttributes.SetBool(GhostAttr, true);
                if (logApi != null) TcmLog.Cat(logApi, TcmLog.Hooks,
                    $"WOO stump: base faller ghosted at {faller.initialPos}");
            }
            return true;
        }
    }

    /// <summary>The ghost's exit: despawn at landing, before FallingTree's own prefix places it
    /// as a log (their place mode) and before vanilla drops it. Priority.First so it always
    /// outruns theirs. Client side swallows the call too, so the client never locally places a
    /// phantom log while waiting for the despawn to sync.</summary>
    public static class GhostLandingPatch
    {
        public static bool Prefix(EntityBlockFalling __instance)
        {
            if (!__instance.WatchedAttributes.GetBool(GhostAttr)) return true;
            if (__instance.Api?.World?.Side == EnumAppSide.Server)
            {
                __instance.Die(EnumDespawnReason.Death, null);
                if (logApi != null) TcmLog.Cat(logApi, TcmLog.Hooks,
                    $"WOO stump: ghost landed and despawned at {__instance.initialPos}");
            }
            return false;
        }
    }

    /// <summary>FallingTree's CanReplaceWith(occupant, target) lets a landing log claim any cell
    /// whose Replaceable >= its own — and 0 >= 0 means an occupied stump cell qualifies, because
    /// a log resting on a 6/16 stump has its feet inside that cell. Forced false for stumps; the
    /// landing resolver then tries the cell above, so the trunk lies on the stump.</summary>
    public static class NoLandingTheftPatch
    {
        public static bool Prefix(Block __0, ref bool __result)
        {
            if (!IsStumpBlockCode(__0?.Code)) return true;
            __result = false;
            return false;
        }
    }
}

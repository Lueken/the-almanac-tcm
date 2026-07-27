using System;
using System.Runtime.CompilerServices;
using System.Text;
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
/// MAS — the verbs that GRANT rank, the quarry-dress YIELD lever, and the MASON'S MARK (rank-bonus-design
/// §MAS; technique-maps §MAS RULED). Three verbs:
///
///   • Mortared construction [medievalarchitecture, conditional] — grant at the staged-build completion
///     (BlockBehaviorArchway.TryComplete / BlockBehaviorArchwayFrame.TryCompleteArch, player in scope).
///     Reflected + isolated (warns-and-skips), so it is inert without the mod.
///   • Stone dressing [stonequarry, conditional] — grant at the slab-dress output (StoneSlabInventory.
///     GetContent, server-side, player in scope) AND apply the Axis-4 yield lever there (scale the dressed
///     stack: a master wrings more units from the same slab). Plus the rubble hammer (ItemRubbleHammer.
///     OnHeldAttackStep) as the merged aggregate sub-verb.
///   • Chiseling [vanilla, always] — the freeform carve has no completion event, so a tiny net-new-voxel
///     grant at BlockEntityChisel.SetVoxel, plus the MASON'S MARK: the block is stamped with its FIRST
///     carver and NEVER overwritten (Jeffrey 2026-07-22: know who initially carved it, and don't let a
///     later hand steal the work). The stamp rides the block-entity's OWN synced tree attributes (so it
///     persists to disk AND syncs to clients, where the block-info box renders); held in memory via a
///     ConditionalWeakTable keyed on the BE instance, written on serialize / read on deserialize.
/// </summary>
public static class MasPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    /// <summary>The tree-attribute key carrying the first carver's mark "uid|name|level" on a chiseled
    /// block entity — persisted with the block and synced to clients.</summary>
    private const string MarkKey = "almanactcm:masby";

    /// <summary>In-memory mirror of the mark per live BE instance (weak, so it never keeps a BE alive).
    /// Populated server-side at the first carve and on deserialize (both sides); the block's own
    /// serialization carries it to disk and to clients.</summary>
    private static readonly ConditionalWeakTable<BlockEntityMicroBlock, string> markByBe = new();

    // ------------------------------------------------------------ conditional patches (mod verbs)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // ---- Mortared construction (medievalarchitecture): grant at the staged completion.
        var arch = AccessTools.TypeByName("MedievalArchitecture.BlockBehaviorArchway");
        var frame = AccessTools.TypeByName("MedievalArchitecture.BlockBehaviorArchwayFrame");
        var mArch = arch == null ? null : AccessTools.DeclaredMethod(arch, "TryComplete");
        var mFrame = frame == null ? null : AccessTools.DeclaredMethod(frame, "TryCompleteArch");
        if (mArch != null || mFrame != null)
        {
            var post = new HarmonyMethod(AccessTools.Method(typeof(MasPatches), nameof(MortarCompletePostfix)));
            if (mArch != null) harmony.Patch(mArch, postfix: post);
            if (mFrame != null) harmony.Patch(mFrame, postfix: post);
            TcmLog.Info(api, "MAS mortared construction hooked (staged-build completion grant)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "MAS mortar seam absent (medievalarchitecture); mortared-construction verb inactive");

        // ---- Stone dressing (stonequarry): grant + the yield lever at the dressed output.
        var slabInv = AccessTools.TypeByName("StoneQuarry.StoneSlabInventory");
        var mGet = slabInv == null ? null : AccessTools.DeclaredMethod(slabInv, "GetContent");
        if (mGet != null)
        {
            harmony.Patch(mGet, postfix: new HarmonyMethod(AccessTools.Method(typeof(MasPatches), nameof(DressContentPostfix))));
            TcmLog.Info(api, "MAS stone dressing hooked (slab-dress grant + dress-yield lever)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "MAS slab-dress seam absent (stonequarry); dressing grant + yield lever inactive");

        var rubble = AccessTools.TypeByName("StoneQuarry.ItemRubbleHammer");
        var mRub = rubble == null ? null : AccessTools.DeclaredMethod(rubble, "OnHeldAttackStep");
        if (mRub != null)
        {
            harmony.Patch(mRub, postfix: new HarmonyMethod(AccessTools.Method(typeof(MasPatches), nameof(RubbleStepPostfix))));
            TcmLog.Info(api, "MAS rubble hammer hooked (aggregate-conversion grant)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "MAS rubble-hammer seam absent (stonequarry); aggregate sub-verb inactive");
    }

    // ------------------------------------------------------------ mortared construction

    /// <summary>Grant MAS at a staged mortared-construction completion (the arch/frame closes). Player in
    /// scope at the completion; no failure roll, so bank straight at completion.</summary>
    public static void MortarCompletePostfix(IPlayer player)
    {
        if (player?.Entity?.World?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(player, MasDomain.Code, MasDomain.TechMortar,
            HashCode.Combine("mortar", (int)(player.Entity.World.ElapsedMilliseconds / 1000)));
    }

    // ------------------------------------------------------------ stone dressing + yield lever

    /// <summary>Slab dressing produces a stack via GetContent (server-side, on a successful dress). Grant
    /// the dress verb to the mason AND apply the Axis-4 yield lever: scale the dressed stack by MAS rank,
    /// the fractional part rolling as a chance of an extra unit (a master gets more bricks per slab).</summary>
    public static void DressContentPostfix(IPlayer byPlayer, ItemStack __result)
    {
        if (__result == null || byPlayer is not IServerPlayer sp) return;
        var world = sp.Entity?.World;
        if (world == null) return;

        Core?.Ledger?.Log(byPlayer, MasDomain.Code, MasDomain.TechDress,
            HashCode.Combine("dress", __result.Collectible?.Id ?? 0, (int)(world.ElapsedMilliseconds / 60000)));

        double mult = MasDomain.DressYield(MasDomain.LevelOf(byPlayer));
        if (mult == 1.0) return;
        double scaled = __result.StackSize * mult;
        int whole = (int)scaled;
        double frac = scaled - whole;
        if (frac > 0 && world.Rand.NextDouble() < frac) whole += 1;
        __result.StackSize = Math.Max(1, whole);
    }

    /// <summary>The rubble hammer's aggregate conversion (rock -> gravel -> sand) is the merged dressing
    /// sub-verb. OnHeldAttackStep returns false on the convert path (TryGetConvertedBlock succeeded); grant
    /// there, deduped per block-pos per world-minute so continuous hammering banks once.</summary>
    public static void RubbleStepPostfix(EntityAgent byEntity, BlockSelection blockSel, bool __result)
    {
        if (__result || blockSel == null || byEntity?.World?.Side != EnumAppSide.Server) return;
        var player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;
        Core?.Ledger?.Log(player, MasDomain.Code, MasDomain.TechDress,
            HashCode.Combine("rubble", blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z,
                (int)(byEntity.World.ElapsedMilliseconds / 60000)));
    }

    // ------------------------------------------------------------ chiseling (grant + Mason's Mark)

    /// <summary>The freeform carve: tiny net-new-voxel XP (add only) + the immutable first-carve stamp.
    /// SetVoxel(add=true) is a net-new voxel; grant deduped per block-pos per world-minute (a tiny raw,
    /// so carving detail banks slowly). The Mason's Mark is written ONCE at the first carve of a block and
    /// never overwritten — an under-handed re-carve can never steal the author.</summary>
    [HarmonyPatch(typeof(BlockEntityChisel), nameof(BlockEntityChisel.SetVoxel),
        new[] { typeof(Vec3i), typeof(bool), typeof(IPlayer), typeof(byte) })]
    public static class ChiselCarvePatch
    {
        public static void Postfix(BlockEntityChisel __instance, bool add, IPlayer byPlayer, bool __result)
        {
            if (!__result || __instance == null || byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;

            // The Mason's Mark: first carver wins, forever (write only if absent), and MarkDirty so the
            // stamp serializes to disk and syncs to clients (where the block-info box renders).
            if (!markByBe.TryGetValue(__instance, out _))
            {
                markByBe.Add(__instance, $"{byPlayer.PlayerUID}|{byPlayer.PlayerName}|{MasDomain.LevelOf(byPlayer)}");
                __instance.MarkDirty(true);
            }

            // XP only on net-new voxel (adding material), deduped per block per minute.
            if (!add) return;
            Core?.Ledger?.Log(byPlayer, MasDomain.Code, MasDomain.TechChisel,
                HashCode.Combine("chisel", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                    (int)(byPlayer.Entity.World.ElapsedMilliseconds / 60000)));
        }
    }

    /// <summary>Carry the mark on the block entity's own serialization: write it into the synced tree so it
    /// persists to disk and reaches clients.</summary>
    [HarmonyPatch(typeof(BlockEntityMicroBlock), nameof(BlockEntityMicroBlock.ToTreeAttributes))]
    public static class ChiselToTreePatch
    {
        public static void Postfix(BlockEntityMicroBlock __instance, ITreeAttribute tree)
        {
            if (markByBe.TryGetValue(__instance, out string? mark) && mark != null)
                tree.SetString(MarkKey, mark);
        }
    }

    /// <summary>Read the mark back on deserialize (both sides) into the in-memory mirror.</summary>
    [HarmonyPatch(typeof(BlockEntityMicroBlock), nameof(BlockEntityMicroBlock.FromTreeAttributes))]
    public static class ChiselFromTreePatch
    {
        public static void Postfix(BlockEntityMicroBlock __instance, ITreeAttribute tree)
        {
            string? mark = tree.GetString(MarkKey);
            if (string.IsNullOrEmpty(mark)) return;
            markByBe.Remove(__instance);
            markByBe.Add(__instance, mark);
        }
    }

    /// <summary>The Mason's Mark in the block info box (look at the carved block). Reads the in-memory
    /// mirror (populated on deserialize, so it is present client-side where this renders); tiered by the
    /// frozen carve-rank. Shown at every rank (a carving is a unique authored artifact, not a stacking
    /// item — knowing who carved it is the point), the wording rising with mastery.</summary>
    [HarmonyPatch(typeof(BlockEntityMicroBlock), nameof(BlockEntityMicroBlock.GetBlockInfo))]
    public static class ChiselInfoPatch
    {
        public static void Postfix(BlockEntityMicroBlock __instance, StringBuilder dsc)
        {
            if (__instance == null || !markByBe.TryGetValue(__instance, out string? packed) || packed == null) return;
            string[] p = packed.Split('|');
            if (p.Length < 3 || !int.TryParse(p[2], out int level)) return;
            string name = p[1];
            string line =
                level >= MasDomain.ProvGm ? Lang.Get("almanactcm:mas-master-by", name)
                : level >= MasDomain.ProvMaster ? Lang.Get("almanactcm:mas-dressed-by", name)
                : Lang.Get("almanactcm:mas-carved-by", name);
            dsc.AppendLine(line);
        }
    }
}

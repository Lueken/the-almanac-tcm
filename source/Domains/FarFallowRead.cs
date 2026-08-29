using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// The fallow read (beta feedback 2026-08-30). Involved Farming stands a Fallow block on every
/// harvested square and says how to handle it only in the handbook, which nobody opens in the
/// field: re-till with the hoe for a clean removal, break it by hand and 5% of every nutrient
/// spills, and while it stands the farmland's fertility ceiling runs 10% high. Testers were
/// breaking it and eating the spill without knowing there was a choice. This hover puts the
/// choice on the block itself.
///
/// Rank-free and always on, deliberately: it is guidance about a foreign mod's block, not
/// earned knowledge, so it does not ride the familiarity ladder. Invfarming-conditional. The
/// block's class is plain Block, so the seam is the base Block.GetPlacedBlockInfo with an O(1)
/// block-id gate up front; the id resolves lazily because block ids are not assigned when
/// PatchConditional runs.
/// </summary>
public static class FarFallowRead
{
    private static int fallowBlockId = -2;   // -2 unresolved, -1 absent

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("invfarming")) return;

        harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.GetPlacedBlockInfo)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(FallowReadPatch), nameof(FallowReadPatch.Postfix))));
        TcmLog.Info(api, "FAR fallow read hooked (Involved Farming guidance on the block hover)");
    }

    public static class FallowReadPatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, ref string __result)
        {
            if (world?.Api == null || __instance?.BlockId == null) return;
            if (fallowBlockId == -2)
                fallowBlockId = world.GetBlock(new AssetLocation("invfarming", "soilfallow"))?.BlockId ?? -1;
            if (fallowBlockId < 0 || __instance.BlockId != fallowBlockId) return;

            __result += "\n" + Lang.Get("almanactcm:far-fallow-resting")
                      + "\n" + Lang.Get("almanactcm:far-fallow-retill");
        }
    }
}

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// COMPAT (perf). Kills the client freeze on first anvil look. Not a TCM mechanic and not
/// balance: TCM hard-depends on Smithing Plus, so every install carries this bug, and it is
/// worst on exactly the heavily-modded packs TCM is written for.
///
/// The path is client-only: vanilla's anvil interaction help calls GetRequiredAnvilTier once
/// per workable handbook stack (CollectibleObject.GetHandBookStacks takes an ICoreClientAPI).
/// For Smithing Plus's cast-tool behavior each call resolves the tool's metal through
/// SmithingPlus.Util.CollectibleExtensions.GetGridRecipesAsIngredient, which LINQ-scans the
/// ENTIRE grid recipe registry per call. Smithing Plus's own metal-material cache never stores
/// null results (CacheHelper.GetOrAdd returns early on null) and its ItemStack overload bypasses
/// the collectible cache for behavior-based workables, so the scan repeats forever.
///
/// Cost is the product of two pack-size terms — cast-tool stack count times registry size — so
/// it scales from a hitch on a light install to a lockup on a heavy one. Profiled on The Quire
/// (194 mods, VS 1.22.5, Smithing Plus 1.9.0-rc.1) on 2026-08-02: 51039ms to build the tier-4
/// anvil help, 50989ms of it in 1792 GetRequiredAnvilTier calls, 1703 of them cast tools at
/// ~30ms each. Everything outside the cast-tool behavior totalled under 100ms.
///
/// Two seams, both resolved by name, warn-and-skip:
///  1. GetGridRecipesAsIngredient — served from a one-time index of resolved ingredient code
///     to recipes. Duplicates preserved: the original yields a recipe once per matching
///     ingredient. The index rebuilds if the recipe count changes, which covers recipes
///     arriving late on the client.
///  2. CollectibleBehaviorCastToolHead.GetRequiredAnvilTier — memoized per collectible code, so
///     the residual smithing-recipe scan runs once per tool rather than once per anvil-tier
///     build.
///
/// The index is held PER SIDE. Singleplayer runs a client and a server ModSystem in one process
/// sharing statics (which is why Start guards on `harmony == null`), and both sides carry the
/// same recipe count, so a single static index would never trip its own staleness check and
/// whichever side built first would hand its GridRecipe references to the other. That is the
/// same class of bug as docs/design/singleplayer-instance-bug.md, which cost every singleplayer
/// player their XP until 0.4.x side-split the ModSystem statics. The tier memo is deliberately
/// NOT per side: it stores an anvil tier, an int that is identical on both sides.
///
/// PINNED to: smithingplus 1.9.0-rc.1. Both seams are resolved by name and skip with a warning
/// if renamed. Reported upstream with the fix offered as a PR (2026-08-02); when Smithing Plus
/// fixes it at source these become redundant, not wrong — delete them then.
///
/// Migrated from The Quire server patches (thequire 0.1.25) on 2026-08-27: the freeze reaches
/// singleplayer players, who never load a server patch mod.
/// </summary>
public static class MetSmithingPlusAnvilLag
{
    /// <summary>One index per side. Keyed by EnumAppSide rather than shared, see class remarks.</summary>
    private sealed class SideIndex
    {
        public readonly object Lock = new();
        public Dictionary<AssetLocation, List<GridRecipe>>? Index;
        public int IndexedRecipeCount = -1;
    }

    private static readonly ConcurrentDictionary<EnumAppSide, SideIndex> Indexes = new();

    /// <summary>Anvil tier per collectible code. Side-independent (an int), so one map serves
    /// both sides. Cleared whenever any side rebuilds its index.</summary>
    private static readonly ConcurrentDictionary<AssetLocation, int> TierCache = new();

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("smithingplus")) return;   // hard dep, but guard anyway

        var extClass = AccessTools.TypeByName("SmithingPlus.Util.CollectibleExtensions");
        var castHead = AccessTools.TypeByName("SmithingPlus.CastingTweaks.CollectibleBehaviorCastToolHead");
        var mGrid = extClass == null ? null : AccessTools.DeclaredMethod(extClass, "GetGridRecipesAsIngredient");
        var mTier = castHead == null ? null : AccessTools.DeclaredMethod(castHead, "GetRequiredAnvilTier");
        if (mGrid == null && mTier == null)
        {
            TcmLog.Warn(api, "smithingplus present but neither anvil-lag seam found; anvil-look freeze fix inactive this build");
            return;
        }

        if (mGrid != null)
        {
            harmony.Patch(mGrid,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(GridRecipeIndexPatch), nameof(GridRecipeIndexPatch.Prefix))));
        }
        if (mTier != null)
        {
            harmony.Patch(mTier,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TierMemoPatch), nameof(TierMemoPatch.Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TierMemoPatch), nameof(TierMemoPatch.Postfix))));
        }
        TcmLog.Info(api, $"anvil-look freeze fix hooked to Smithing+ ({(mGrid != null ? "grid-recipe ingredient index" : "")}"
            + $"{(mGrid != null && mTier != null ? " + " : "")}{(mTier != null ? "cast-tool tier memo" : "")})");
    }

    /// <summary>Cleared on Dispose so a second world loaded in the same singleplayer process
    /// cannot be served an index built from the first. Recipe counts can match across worlds,
    /// so the staleness check alone does not cover this.</summary>
    public static void ClearCaches()
    {
        Indexes.Clear();
        TierCache.Clear();
    }

    public static class GridRecipeIndexPatch
    {
        /// <summary>Same result set as the original LINQ scan (a recipe repeated once per
        /// matching ingredient), served from an index built in a single pass.</summary>
        public static bool Prefix(CollectibleObject collObj, ICoreAPI api, ref IEnumerable<GridRecipe> __result)
        {
            var recipes = api?.World?.GridRecipes;
            var code = collObj?.Code;
            if (recipes == null || code == null) return true;   // fall through to the original

            var side = Indexes.GetOrAdd(api!.Side, _ => new SideIndex());
            var index = side.Index;
            if (index == null || recipes.Count != side.IndexedRecipeCount)
            {
                lock (side.Lock)
                {
                    if (side.Index == null || recipes.Count != side.IndexedRecipeCount)
                    {
                        side.Index = Build(recipes);
                        side.IndexedRecipeCount = recipes.Count;
                        TierCache.Clear();
                    }
                    index = side.Index;
                }
            }

            __result = index.TryGetValue(code, out var list) ? list : Enumerable.Empty<GridRecipe>();
            return false;
        }

        private static Dictionary<AssetLocation, List<GridRecipe>> Build(List<GridRecipe> recipes)
        {
            var index = new Dictionary<AssetLocation, List<GridRecipe>>();
            for (int i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes[i];
                var ingredients = (recipe as IRecipeBase)?.RecipeIngredients;
                if (ingredients == null) continue;
                foreach (var ing in ingredients)
                {
                    var ingCode = ing?.ResolvedItemStack?.Collectible?.Code;
                    if (ingCode == null) continue;
                    if (!index.TryGetValue(ingCode, out var list)) index[ingCode] = list = new List<GridRecipe>();
                    list.Add(recipe);
                }
            }
            return index;
        }
    }

    public static class TierMemoPatch
    {
        /// <summary>__state true means "the original ran, so record what it returned".</summary>
        public static bool Prefix(ItemStack stack, ref int __result, out bool __state)
        {
            var code = stack?.Collectible?.Code;
            if (code != null && TierCache.TryGetValue(code, out var tier))
            {
                __result = tier;
                __state = false;
                return false;
            }
            __state = true;
            return true;
        }

        public static void Postfix(ItemStack stack, int __result, bool __state)
        {
            if (!__state) return;
            var code = stack?.Collectible?.Code;
            if (code != null) TierCache[code] = __result;
        }
    }
}

using System;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// The POT stroke ladder. Originally the B-walk ruling's hard gate (2026-08-21): below the rung
/// the broad clayforming strokes were refused outright. Rebuilt 2026-09-14 as THE CLEAN STROKE
/// after the LauCaRo/BAR thread, because the gate was buying tedium rather than mastery.
///
/// WHY THE GATE WAS WRONG. Vanilla clamps a broad stroke to LayerBounds(layer) — the BOUNDING BOX
/// of the recipe's voxels on that layer, not the recipe outline. So vanilla itself drops clay into
/// cells the recipe does not want and leaves the player to pick them out by hand. Measured across
/// all 31 vanilla clay recipes, the gap between shape and bounding box runs 6% (flat tool molds,
/// near-solid rectangles) to 62% (fourcrock, clayplanter), median 31%, and NOT ONE recipe is zero.
/// The old gate refused the tool on top of that, so a low-rank player got vanilla's mess-making
/// with vanilla's fast strokes taken away. Removal was gated too, and removal REFUNDS (vanilla's
/// OnRemove does AvailableVoxels++), so gating it risked nothing at all and cost only clicks.
///
/// THE MODEL NOW. Nothing is refused, at any rank. What rank buys is PRECISION:
///
/// - Below the rung a broad stroke is BLUNT: vanilla behaviour, filling its whole footprint inside
///   the bounding box, strays and all. The player cleans up after it, or learns to walk the
///   outline at 1x1 and fill the interior broad. That is a technique, learnable in one pot.
/// - At the rung the stroke is CLEAN: adding places ONLY voxels the recipe wants and skips the
///   rest; removing strips ONLY voxels the recipe does NOT want and leaves the shape standing.
///   The 2x2 cleans at Place2x2GatePOTLevel (Apprentice I), the 3x3 at Place3x3GatePOTLevel
///   (Journeyman I) — the same ruled rungs, the same knobs, no longer locks.
/// - The 1x1 stroke is exact at every rank, as it always was: it places where you click.
/// - The duplicate-layer stroke is still SCALED, not gated (PotDomain.CopyVoxelsFor).
/// - The powered pottery wheel is excluded, as before; it does not ride BlockEntityClayForm.
///
/// This is DETERMINISTIC on purpose. The same stroke gives the same result every time, the miss
/// is visible the instant it happens, and it is overcome by changing how you work rather than by
/// waiting for a level. A failure roll was considered and rejected: it would have had to be
/// server-authoritative to avoid desync, and it would have reproduced the exact complaint the
/// fermentation thread is making — a loss the player can neither see coming nor answer.
///
/// Mastery reads better this way too. The old gate said "you may not hold the big tool." This
/// says "your big tool stops making messes," which is what a potter's hands actually buy.
///
/// All three stroke patches run on BOTH sides. Being deterministic they cannot disagree, so the
/// client predicts exactly what the server will do — the property the old refusal gate needed
/// the same both-sides treatment to fake.
/// </summary>
public static class PotModeGate
{
    private static Type? wheelType;
    private static bool wheelResolved;

    private static bool IsWheel(object be)
    {
        if (!wheelResolved)
        {
            wheelType = AccessTools.TypeByName("SimplePotteryWheel.ClayWheelEntity");
            wheelResolved = true;
        }
        return wheelType != null && wheelType.IsInstanceOfType(be);
    }

    private static int potDomainId = -2;

    private static int PotDomainId()
    {
        if (potDomainId != -2) return potDomainId;
        potDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == PotDomain.Code) { potDomainId = i; break; }
        return potDomainId;
    }

    /// <summary>The player's POT level from whichever side is live: the server ledger, or
    /// (client) the synced state of the local player.</summary>
    public static int PotLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(PotDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = PotDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    /// <summary>The POT level at which a stroke of this radius runs clean. Radius IS the tool mode
    /// in vanilla's own call — OnUseOver passes toolMode straight through as radius, so 0 = 1x1,
    /// 1 = 2x2, 2 = 3x3. A rung of 0 disables the ladder, meaning clean from Untrained. Anything
    /// that is not one of the two broad strokes returns -1: not our business.</summary>
    private static int CleanRungFor(ICoreAPI api, int radius)
    {
        if (radius != 1 && radius != 2) return -1;
        var cfg = (api.Side == EnumAppSide.Server
            ? AlmanacTcmModSystem.ServerInstance
            : AlmanacTcmModSystem.ClientInstance)?.GlobalConfig;
        return radius == 1 ? (cfg?.Place2x2GatePOTLevel ?? Rank.Apprentice)
                           : (cfg?.Place3x3GatePOTLevel ?? Rank.Journeyman);
    }

    /// <summary>Whether this stroke runs clean for the player currently acting. False means blunt,
    /// blunt means vanilla, and vanilla means the prefix stands aside entirely.</summary>
    private static bool StrokeRunsClean(BlockEntityClayForm? be, int radius)
    {
        if (be?.Api == null || IsWheel(be) || be.SelectedRecipe == null) return false;
        int level = StrokeContext.Level;
        if (level < 0) return false;                       // no actor in scope: vanilla
        int rung = CleanRungFor(be.Api, radius);
        if (rung < 0) return false;                        // the 1x1 and the copy stroke
        return rung <= 0 || level >= rung;
    }

    /// <summary>True when the recipe asks for nothing on this layer. Vanilla's LayerBounds
    /// collapses to a point there and the clean rule would have nothing to place, so both stroke
    /// prefixes stand aside and leave whatever vanilla does today untouched.</summary>
    private static bool RecipeEmptyOnLayer(ClayFormingRecipe recipe, int layer)
    {
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
                if (recipe.Voxels[x, layer, z]) return false;
        return true;
    }

    // Vanilla's footprint, reproduced exactly: for radius r the loop runs dx from -Ceiling(r/2)
    // to r/2 inclusive. r=0 gives one cell, r=1 gives 2x2, r=2 gives 3x3.
    private static int FootFrom(int radius) => -(int)Math.Ceiling(radius / 2f);
    private static int FootTo(int radius) => radius / 2;

    /// <summary>The clean ADD: place only what the recipe wants. Mirror-prefix of vanilla's own
    /// three-argument OnAdd, faithful but for the recipe test, so the voxel accounting, the
    /// already-filled skip and the return contract all match. Vanilla's bounds check is redundant
    /// here: a cell the recipe wants is inside the bounding box by construction.</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), "OnAdd", new Type[] { typeof(int), typeof(Vec3i), typeof(int) })]
    public static class CleanAddPatch
    {
        public static bool Prefix(BlockEntityClayForm __instance, int layer, Vec3i voxelPos, int radius, ref bool __result)
        {
            if (voxelPos == null || !StrokeRunsClean(__instance, radius)) return true;
            if (layer < 0 || layer >= 16 || voxelPos.Y != layer) return true;

            var recipe = __instance.SelectedRecipe;
            if (RecipeEmptyOnLayer(recipe, layer)) return true;

            var voxels = __instance.Voxels;
            bool didadd = false;

            for (int dx = FootFrom(radius); dx <= FootTo(radius); dx++)
            {
                int x = voxelPos.X + dx;
                if (x < 0 || x > 15) continue;

                for (int dz = FootFrom(radius); dz <= FootTo(radius); dz++)
                {
                    int z = voxelPos.Z + dz;
                    if (z < 0 || z > 15) continue;

                    if (!recipe.Voxels[x, layer, z]) continue;   // THE CLEAN RULE: skip the strays
                    if (voxels[x, layer, z]) continue;

                    voxels[x, layer, z] = true;
                    __instance.AvailableVoxels--;
                    didadd = true;
                }
            }

            __result = didadd;
            return false;
        }
    }

    /// <summary>The clean REMOVE: strip only what the recipe does NOT want, so one broad sweep
    /// clears the strays a blunt stroke left and the piece is left standing. Taking out a voxel
    /// the recipe DOES want stays 1x1 work, and that costs nothing real: a wanted voxel is never
    /// a mistake, because the target shape does not change while you work it.
    ///
    /// Mirror-prefix of vanilla's OnRemove, refund included (AvailableVoxels++).</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), "OnRemove")]
    public static class CleanRemovePatch
    {
        public static bool Prefix(BlockEntityClayForm __instance, int layer, Vec3i voxelPos, int radius, ref bool __result)
        {
            if (voxelPos == null || !StrokeRunsClean(__instance, radius)) return true;
            if (layer < 0 || layer >= 16 || voxelPos.Y != layer) return true;

            var recipe = __instance.SelectedRecipe;
            if (RecipeEmptyOnLayer(recipe, layer)) return true;

            var voxels = __instance.Voxels;
            bool didremove = false;

            for (int dx = FootFrom(radius); dx <= FootTo(radius); dx++)
            {
                int x = voxelPos.X + dx;
                if (x < 0 || x > 15) continue;

                for (int dz = FootFrom(radius); dz <= FootTo(radius); dz++)
                {
                    int z = voxelPos.Z + dz;
                    if (z < 0 || z > 15) continue;

                    if (!voxels[x, layer, z]) continue;
                    if (recipe.Voxels[x, layer, z]) continue;   // THE CLEAN RULE: leave the shape

                    voxels[x, layer, z] = false;
                    __instance.AvailableVoxels++;
                    didremove = true;
                }
            }

            __result = didremove;
            return false;
        }
    }

    /// <summary>The copy stroke, rank-scaled: vanilla's OnCopyLayer reproduced byte for byte,
    /// except the flat 4-voxel quantity becomes PotDomain.CopyVoxelsFor(level). The player
    /// context is not passed in, so the level rides StrokeContext; the wheel never enters
    /// (excluded here and not derived from this block entity anyway).</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), "OnCopyLayer")]
    public static class CopyStrokeScalePatch
    {
        public static bool Prefix(BlockEntityClayForm __instance, int layer, ref bool __result)
        {
            int level = StrokeContext.Level;
            if (level < 0 || IsWheel(__instance)) return true;
            int quantity = PotDomain.CopyVoxelsFor(level);
            if (quantity >= 16 * 16) return true;

            __result = false;
            if (layer <= 0 || layer > 15) return false;

            var voxels = __instance.Voxels;
            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    if (voxels[x, layer - 1, z] && !voxels[x, layer, z])
                    {
                        quantity--;
                        voxels[x, layer, z] = true;
                        __instance.AvailableVoxels--;
                        __result = true;
                    }
                    if (quantity == 0) return false;
                }
            }
            return false;
        }
    }

    /// <summary>Hands the acting player's POT level to the stroke prefixes for the duration of one
    /// OnUseOver call. Vanilla's OnAdd, OnRemove and OnCopyLayer are all reached from here and not
    /// one of them carries an IPlayer, so this one-frame handoff is the only way to know whose
    /// hands are on the clay.
    ///
    /// -1 means unknown, and every prefix falls back to vanilla on it. The Finalizer clears the
    /// value even when the original throws, which a Postfix alone does not: a level left standing
    /// would silently apply to the next player through the method. ThreadStatic because the
    /// integrated server and the client run this type in one process on two threads.</summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.OnUseOver),
        new Type[] { typeof(IPlayer), typeof(Vec3i), typeof(BlockFacing), typeof(bool) })]
    public static class StrokeContext
    {
        [ThreadStatic] private static int level;

        internal static int Level => level == 0 ? -1 : level - 1;

        public static void Prefix(BlockEntityClayForm __instance, IPlayer byPlayer)
        {
            level = 0;
            if (__instance?.Api == null || byPlayer == null || IsWheel(__instance)) return;
            level = PotLevelOf(__instance.Api, byPlayer) + 1;   // biased by one: 0 is "unset"
        }

        public static void Finalizer()
        {
            level = 0;
        }
    }
}

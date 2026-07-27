using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// FIS SINGLE POPULATION (ruled 2026-07-16: "the new fish population mechanic is the vanilla
/// counter, but leveraging a tweaked recovery of PS"). Before this build there were two
/// unrelated fish oceans: rod fishing + Ithania traps ran on vanilla's per-8-block depletion
/// map (binary 14-day cliff recovery), while PS baskets/weirs/trotlines ran on PS's own
/// per-chunk percent (gradual self-recovery, roe-boostable) and kept catching at vanilla 1%.
///
/// After this build there is ONE population, the vanilla map, with PS-style recovery:
///   • GRADUAL RECOVERY replaces the cliff: the pond regains MaxHarvestable/RestoreDays fish
///     per day (~0.86/day at defaults) continuously. A catch on "day 13" now costs the pond
///     roughly one day of recovery instead of restarting a fortnight — the bad-luck-cast
///     problem is gone by design, for everyone, rank-free (ecology, not skill).
///   • PS READS REROUTE: FishDepletedPercent returns the vanilla bucket's depletion, so traps
///     genuinely slow as the pond empties, whoever emptied it.
///   • PS WRITES REROUTE: trap catches deplete the vanilla map (percent converted at
///     MaxHarvestable fish = 100%); PS's private counter and its separate timed self-repletion
///     are retired (skipped) so recovery has exactly one clock.
///   • ROE RESTOCKS VANILLA: an ovulated egg dissolving in water gives fish back to the real
///     map. Anyone's roe works at base value; a ranked steward's roe counts for up to 2x at GM
///     (thrower identified via EntityItem.ByPlayerUid, careful-hands shape, silent).
///   • PROCESSING: dressing PS fish at the grid credits FIS (the FIS-by-target butchery
///     ruling; the Butchering mod has zero fish content, verified in its decompile).
/// </summary>
public static class FisEcologyPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    public static void RegisterServer(ICoreServerAPI api) => sapi = api;

    private static readonly AccessTools.FieldRef<ModSystemFishDepletion, Dictionary<BlockPos, CreatureHarvest>>? dictRef = TryDict();

    private static AccessTools.FieldRef<ModSystemFishDepletion, Dictionary<BlockPos, CreatureHarvest>>? TryDict()
    {
        try { return AccessTools.FieldRefAccess<ModSystemFishDepletion, Dictionary<BlockPos, CreatureHarvest>>("harvestedLocations"); }
        catch { return null; }
    }

    /// <summary>Fish regained per in-game day (12 over 14 days at vanilla defaults).</summary>
    private static double RegenPerDay =>
        ModSystemFishDepletion.MaxHarvestablePerLocation / Math.Max(0.01, ModSystemFishDepletion.RestoreFishAfterDays);

    private static int Probabilistic(double v, IWorldAccessor world)
    {
        int n = (int)v;
        if (world.Rand.NextDouble() < v - n) n++;
        return n;
    }

    // ------------------------------------------------------------ gradual recovery (vanilla)

    /// <summary>Replaces vanilla's all-or-nothing restore (delete the record 14 days after the
    /// LAST catch) with continuous linear recovery at the same total rate. TotalDays advances
    /// as fish return, so partial progress is never lost to the tick cadence.</summary>
    [HarmonyPatch(typeof(ModSystemFishDepletion), "restoreFish")]
    public static class GradualRecoveryPatch
    {
        public static bool Prefix(ModSystemFishDepletion __instance)
        {
            if (sapi == null || dictRef == null) return true; // no server yet: vanilla behavior
            var dict = dictRef(__instance);
            if (dict.Count == 0) return false;

            double now = sapi.World.Calendar.TotalDays;
            double rate = RegenPerDay;
            var keys = new List<BlockPos>(dict.Keys);
            foreach (var pos in keys)
            {
                var h = dict[pos];
                int whole = (int)Math.Floor((now - h.TotalDays) * rate);
                if (whole < 1) continue;
                if (h.Quantity - whole <= 0) { dict.Remove(pos); continue; }
                dict[pos] = new CreatureHarvest { TotalDays = h.TotalDays + whole / rate, Quantity = h.Quantity - whole };
            }
            return false;
        }
    }

    // ------------------------------------------------------------ PS bridge (conditional)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("primitivesurvival")) return;

        var psSystem = AccessTools.TypeByName("PrimitiveSurvival.ModSystem.PrimitiveSurvivalSystem");
        var read = psSystem == null ? null : AccessTools.Method(psSystem, "FishDepletedPercent");
        var write = psSystem == null ? null : AccessTools.Method(psSystem, "UpdateChunkInDictionary");
        var replete = psSystem == null ? null : AccessTools.Method(psSystem, "RepleteFishStocks");
        var eggs = AccessTools.TypeByName("PrimitiveSurvival.ModSystem.ItemFishEggs");
        var eggIdle = eggs == null ? null : AccessTools.Method(eggs, "OnGroundIdle");
        var psFish = AccessTools.TypeByName("PrimitiveSurvival.ModSystem.ItemPSFish");
        var fishCraft = psFish == null ? null : AccessTools.Method(psFish, "OnConsumedByCrafting");

        if (read == null || write == null)
        {
            TcmLog.Warn(api, "primitivesurvival present but the fish-chunk seams were not found; the single-population bridge is inactive");
            return;
        }

        harmony.Patch(read, prefix: new HarmonyMethod(AccessTools.Method(typeof(PsReadPatch), "Prefix")));
        harmony.Patch(write, prefix: new HarmonyMethod(AccessTools.Method(typeof(PsWritePatch), "Prefix")));
        if (replete != null) harmony.Patch(replete, prefix: new HarmonyMethod(AccessTools.Method(typeof(SkipPsRepletionPatch), "Prefix")));
        if (eggIdle != null) harmony.Patch(eggIdle,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RoeContextPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(RoeContextPatch), "Postfix")));
        if (fishCraft != null) harmony.Patch(fishCraft, postfix: new HarmonyMethod(AccessTools.Method(typeof(FishProcessingPatch), "Postfix")));

        TcmLog.Info(api, "FIS single population live: PS traps read+write the vanilla fish map, PS self-repletion retired, roe restocks vanilla, filleting credits FIS");
    }

    /// <summary>PS traps ask "how depleted is this chunk" — answer from the vanilla bucket.</summary>
    public static class PsReadPatch
    {
        public static bool Prefix(ICoreServerAPI api, BlockPos pos, ref int __result)
        {
            var dep = api?.ModLoader?.GetModSystem<ModSystemFishDepletion>();
            __result = dep == null ? 0
                : (int)GameMath.Clamp(dep.GetHarvestAmount(pos) * 100f / ModSystemFishDepletion.MaxHarvestablePerLocation, 0f, 100f);
            return false;
        }
    }

    /// <summary>Every PS counter write becomes a vanilla-map write. Positive rate = a trap
    /// catch depleting; negative = roe restocking (PS's timed self-repletion is skipped
    /// separately, so recovery has exactly one clock: the gradual vanilla one).</summary>
    public static class PsWritePatch
    {
        public static bool Prefix(ICoreServerAPI api, BlockPos pos, int rate)
        {
            if (api == null || pos == null || rate == 0) return false;
            var dep = api.ModLoader.GetModSystem<ModSystemFishDepletion>();
            if (dep == null || dictRef == null) return false;

            double fish = Math.Abs(rate) * ModSystemFishDepletion.MaxHarvestablePerLocation / 100.0;
            if (rate > 0)
            {
                int n = Probabilistic(fish, api.World);
                if (n > 0) dep.AddHarvest(pos, n);
                return false;
            }

            // Restock (roe). A ranked steward's roe counts for more; anyone's works.
            double mult = 1.0;
            string? uid = RoeContextPatch.ThrowerUid;
            if (uid != null) mult = FisDomain.RoeMultiplierFor(FisDomain.LevelOf(api.World.PlayerByUid(uid)));
            int give = Probabilistic(fish * mult, api.World);
            if (give <= 0) return false;

            var dict = dictRef(dep);
            var key = pos / dep.Scale;
            if (!dict.TryGetValue(key, out var h)) return false; // full pond: the eggs just sink
            if (h.Quantity - give <= 0) dict.Remove(key);
            else dict[key] = new CreatureHarvest { TotalDays = h.TotalDays, Quantity = h.Quantity - give };
            TcmLog.Cat(api, TcmLog.Hooks, $"FIS roe restock: +{give} fish at {pos} (mult {mult:0.##})");
            return false;
        }
    }

    /// <summary>PS's separate timed self-repletion is retired: one population, one recovery.</summary>
    public static class SkipPsRepletionPatch
    {
        public static bool Prefix() => false;
    }

    /// <summary>Ambient thrower for the roe restock (EntityItem remembers who dropped it).
    /// Prefix-set, postfix-cleared; never a finalizer (the 0.3.43 lesson).</summary>
    public static class RoeContextPatch
    {
        [ThreadStatic] public static string? ThrowerUid;
        public static void Prefix(EntityItem entityItem) => ThrowerUid = entityItem?.ByPlayerUid;
        public static void Postfix() => ThrowerUid = null;
    }

    /// <summary>Dressing the catch: PS fish consumed at the crafting grid (filleting) credit
    /// FIS/processing. contextHash is a 1s bucket, so one batch-craft is one log.</summary>
    public static class FishProcessingPatch
    {
        public static void Postfix(IPlayer byPlayer)
        {
            var world = byPlayer?.Entity?.World;
            if (world == null || world.Side != EnumAppSide.Server) return;
            Core?.Ledger?.Log(byPlayer!, FisDomain.Code, FisDomain.TechProcessing,
                HashCode.Combine(FisDomain.TechProcessing, world.ElapsedMilliseconds / 1000));
        }
    }
}

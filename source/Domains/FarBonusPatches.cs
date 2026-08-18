using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// FAR Axis 6 — the Cultivator's Mark (RULED 2026-07-09; seams verified 1.22.3). Three parts on
/// one grownBy stamp: tiered provenance (Grown by / Cultivated by / Heirloom of), the GM
/// slow-spoil signature, and the HEIRLOOM SEED — the recurring economy.
///
/// The Heirloom lifecycle (the FAR repair-gate analog):
///   • A GM's harvest of ANY crop mints fresh heirloom seeds: grownBy=GM, tier=GM,
///     heirloomGen=3 (config). The mastery is in the seed processing.
///   • Planting an heirloom seed carries {grownBy, tier, gen} into a PERSISTED position map
///     keyed by the crop pos (farmland.Up()) — NOT the farmland BE, which does not serialize
///     custom attrs (the ruled build-time risk, sidestepped with the graft-owner pattern).
///   • Harvesting an heirloom crop gives a flat yield bonus REGARDLESS of who planted it, and
///     the descendant SEEDS inherit the mark with gen-1. At gen 0 they are ordinary seeds — the
///     tail that sends buyers back to the Grandmaster for fresh stock.
///
/// Crop geometry (verified): TryPlant sets the crop at farmland.Up() (:36633); GetDrops reads
/// farmland at crop.Down() (:67918). So the map keys on the crop pos, shared by plant and harvest.
/// </summary>
public static class FarBonusPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    public const string GrownByAttr = "almanacGrownBy";
    public const string GrownTierAttr = "almanacGrownTier";
    public const string HeirloomGenAttr = "almanacHeirloomGen";

    /// <summary>Crop pos -> the planted seed's mark, packed "grownBy|tier|heirloomGen". Persisted:
    /// a crop grows over many days, across restarts. Consumed at harvest.</summary>
    private static Dictionary<string, string> plantedMarks = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacFarPlantedMarks");
                if (data != null)
                    plantedMarks = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
                TcmLog.Cat(api, TcmLog.Config, $"FAR heirloom marks loaded: {plantedMarks.Count} planted crop(s)");
            }
            catch (Exception e) { TcmLog.Error(api, $"heirloom mark map unreadable ({e.Message}); starting empty"); }
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacFarPlantedMarks",
                Vintagestory.API.Util.SerializerUtil.Serialize(plantedMarks));
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Planting: carry an heirloom seed's mark into the position map (crop pos = farmland.Up()).
        var tf = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityFarmland");
        var mp = tf == null ? null : AccessTools.DeclaredMethod(tf, "TryPlant");
        if (mp != null)
        {
            harmony.Patch(mp, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarBonusPatches), nameof(PlantMarkPostfix))));
            TcmLog.Info(api, "FAR heirloom carry hooked (TryPlant -> crop pos map)");
        }
        else TcmLog.Warn(api, "FAR heirloom carry seam not found (BlockEntityFarmland.TryPlant)");

        // Harvest: read the mark, apply the yield bonus + stamp provenance + descendant heirloom.
        var tc = AccessTools.TypeByName("Vintagestory.GameContent.BlockCrop");
        var md = tc == null ? null : AccessTools.DeclaredMethod(tc, "GetDrops");
        if (md != null)
        {
            harmony.Patch(md, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarBonusPatches), nameof(HarvestDropsPostfix))));
            TcmLog.Info(api, "FAR Cultivator's Mark hooked (BlockCrop.GetDrops)");
        }
        else TcmLog.Warn(api, "FAR Cultivator's Mark seam not found (BlockCrop.GetDrops)");
        // The perish signature is an attribute patch below. The Cultivator's Mark LINE is not:
        // it is contributed to Engine.ProvenanceLine (see MarkLine below).
    }

    // ------------------------------------------------------------ planting: carry the mark

    public static void PlantMarkPostfix(BlockEntity __instance, ItemSlot itemslot, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server) return;
        var attrs = itemslot?.Itemstack?.Attributes;
        if (attrs?.HasAttribute(HeirloomGenAttr) != true && attrs?.HasAttribute(GrownTierAttr) != true) return;

        string packed = $"{attrs!.GetString(GrownByAttr)}|{attrs.GetInt(GrownTierAttr)}|{attrs.GetInt(HeirloomGenAttr)}";
        plantedMarks[PosKey(__instance.Pos.UpCopy())] = packed; // the crop sits at farmland.Up()
    }

    // ------------------------------------------------------------ harvest: apply + stamp

    public static void HarvestDropsPostfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref ItemStack[] __result)
    {
        if (world.Side != EnumAppSide.Server || __result == null || __result.Length == 0) return;

        // The mark that GROWS here (from the planted seed), if any.
        string key = PosKey(pos);
        plantedMarks.TryGetValue(key, out string? planted);
        plantedMarks.Remove(key);

        string? grownBy = null; int grownTier = 0; int seedGen = 0;
        double yieldBonus = 0;
        if (planted != null)
        {
            var p = planted.Split('|');
            grownBy = p[0];
            int.TryParse(p.Length > 1 ? p[1] : "0", out grownTier);
            int.TryParse(p.Length > 2 ? p[2] : "0", out int gen);
            if (gen > 0) // grown from a live heirloom seed: the yield bonus + the decrementing tail
            {
                yieldBonus = FarDomain.Knob(FarDomain.HeirloomYield, 0.25);
                seedGen = gen - 1;
            }
        }

        // No inherited mark: the HARVESTER's own rank sets the provenance, and a GM harvest MINTS
        // a fresh heirloom (the standing commission).
        int harvTier = byPlayer == null ? 0 : FarDomain.LevelOf(byPlayer);
        if (grownBy == null && harvTier >= Rank.Journeyman)
        {
            grownBy = byPlayer!.PlayerName;
            grownTier = harvTier;
            if (harvTier >= Rank.Grandmaster) seedGen = (int)FarDomain.Knob(FarDomain.HeirloomGenerations, 3);
        }
        if (grownBy == null && yieldBonus <= 0) return; // ordinary crop, ordinary hand — nothing to mark

        for (int i = 0; i < __result.Length; i++)
        {
            var stack = __result[i];
            if (stack?.Collectible == null) continue;
            bool isSeed = stack.Collectible.Code?.Path?.Contains("seed") == true;

            if (yieldBonus > 0 && stack.StackSize >= 1) // the heirloom yield: honest fractional
            {
                double extra = stack.StackSize * yieldBonus;
                int whole = (int)extra;
                if (world.Rand.NextDouble() < extra - whole) whole++;
                stack.StackSize += whole;
            }

            if (grownBy != null) // the provenance tag rides produce AND seed (display + spoilage)
            {
                stack.Attributes.SetString(GrownByAttr, grownBy);
                stack.Attributes.SetInt(GrownTierAttr, grownTier);
            }
            if (isSeed && seedGen > 0) // only seeds carry the replant tail
                stack.Attributes.SetInt(HeirloomGenAttr, seedGen);
        }

        // Exceptional-harvest proc (Thrift + signature): a rank chance of one bonus unit.
        double proc = FarDomain.BonusT(harvTier) * FarDomain.Knob(FarDomain.HarvestProcGm, 0.20);
        if (proc > 0 && world.Rand.NextDouble() < proc && __result[0]?.StackSize >= 1)
            __result[0].StackSize += 1;

        if (grownBy != null)
            TcmLog.Cat(world.Api, "far", $"harvest at {pos} marked {(seedGen > 0 ? $"Heirloom gen {seedGen}" : "provenance")} of {grownBy} (tier {grownTier}){(yieldBonus > 0 ? $", +{yieldBonus:P0} yield" : "")}");
    }

    // ------------------------------------------------------------ GM slow-spoil signature

    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetTransitionRateMul))]
    public static class GrownPerishPatch
    {
        public static void Postfix(ItemSlot inSlot, EnumTransitionType transType, ref float __result)
        {
            if (Engine.Attribution.Suppressed) return; // the freshness-line probe is measuring vanilla
            if (transType != EnumTransitionType.Perish) return;
            var attrs = inSlot?.Itemstack?.Attributes;
            if (attrs?.HasAttribute(GrownTierAttr) != true) return;
            // The cook's hand governs the dish's numbers (RULED 2026-08-18: the grower's NAME
            // stays on cooked food, the grower's EFFECT does not follow it into the dish; a bad
            // cook still ruins a great crop). This guard IS the rule now, not a backstop.
            if (attrs.HasAttribute(CooBonusPatches.CookTierAttr)) return;
            if (attrs.GetInt(GrownTierAttr) >= Rank.Grandmaster)
                __result *= (float)FarDomain.Knob(FarDomain.SpoilGrownGm, 0.70); // a GM's produce keeps
        }
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Cultivator's Mark line (Journeyman up). Placement, order and spacing belong
    /// to <see cref="Engine.ProvenanceLine"/>; this only decides what FAR has to say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(GrownByAttr);
        if (string.IsNullOrEmpty(name)) return null;
        // Both names render (RULED 2026-08-18): the ordered block puts grown-by above
        // cooked-by, and this line carries no effect clause, so it stays honest on a dish.
        // When the same hand grew and cooked, COO renders the fold and FAR stands down.
        if (Engine.FoodProvenance.SameHandGrownAndCooked(stack)) return null;
        int tier = attrs.GetInt(GrownTierAttr);
        int gen = attrs.GetInt(HeirloomGenAttr);
        string? line =
            tier >= Rank.Grandmaster ? Lang.Get("almanactcm:heirloom-of", name)
            : tier >= Rank.Master ? Lang.Get("almanactcm:cultivated-by", name)
            : tier >= Rank.Journeyman ? Lang.Get("almanactcm:grown-by", name)
            : null;
        if (line == null) return null;
        if (gen > 0) line += " " + Lang.Get("almanactcm:heirloom-tail", gen);

        // SUPERSEDED 2026-08-12 (was: the numbers ruling of 2026-08-01, "the line says by how
        // much"). FAR and COO both postfix the same CollectibleObject.GetTransitionRateMul, so
        // their factors COMPOSE and a per-domain clause here stated one factor as the whole
        // effect. Vanilla's freshness line already carries the composed truth, and
        // Engine.PerishAttributionPatch annotates it with TCM's true delta.
        //
        // The 2026-08-01 ruling's intent is preserved: the spoil rate still has no stat line of
        // its own, so the number rides vanilla's freshness line instead of this one.

        return line;
    }
}

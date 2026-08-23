using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// The Grower's Eye (FAR 0.5, RULED 2026-08-22): vanilla shows farmland moisture, nutrients
/// and crop state to everyone for free; the gate takes that away and sells it back (the TEM
/// warning precedent, yaro's takeaway made explicit). One choke point covers both hovers:
/// BlockCrop.GetPlacedBlockInfo delegates a crop hover to the farmland below (BlockCrop.cs:210
/// verified 1.22), and the farmland hover IS BlockEntityFarmland.GetBlockInfo — so one postfix
/// there rewrites everything the player is told about soil and crop.
///
/// The ladder (rank is the CEILING, per-crop familiarity decides what you see of THIS crop):
///  - Untrained: nothing. The soil keeps its counsel.
///  - Novice+: rough soil words (parched/damp/soaked, dominant nutrient poor/fair/rich);
///    the crop in rough words (young/grown/ready) only if Acquainted, else a stranger line.
///  - Apprentice+: bare farmland reads in full vanilla figures (reading the GROUND is a skill
///    of the eyes — the one sanctioned rank-only information touch point, division-of-labor
///    ruling). A planted crop still reads rough until the crop is Versed; Versed restores the
///    full vanilla readout, demand letter included.
///  - Journeyman+ with the family Versed: the farmland remembers for you — the rotation
///    memory line ("last bore a K-hungry crop"), earned rather than free. Stored in the
///    farmland's own CropAttributes tree (BEFarmland.cs:351/368: serialized AND synced, the
///    sanctioned bag; no serialization patches).
///
/// v1 approximation, recorded: in the Apprentice-and-up not-yet-Versed band the soil figures
/// are rebuilt from the farmland's synced state (nutrient values, moisture), which drops
/// vanilla's colored growth-speed line — deliberately, since how THIS crop responds to this
/// soil is exactly crop-property knowledge — and any fertilizer-overlay detail from the base
/// info, which returns in full once the crop is Versed.
/// </summary>
public static class FarGrowerEye
{
    public const string LastBoreIdAttr = "almanacLastBoreId";
    public const string LastBoreNutrientAttr = "almanacLastBoreNutrient";

    private static int farDomainId = -2;

    private static int FarDomainId()
    {
        if (farDomainId != -2) return farDomainId;
        farDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == FarDomain.Code) { farDomainId = i; break; }
        return farDomainId;
    }

    /// <summary>The viewer's FAR level from whichever side is live (the BreBarrelRead shape).</summary>
    public static int FarLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return FarDomain.LevelOf(player);

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = FarDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    [HarmonyPatch(typeof(BlockEntityFarmland), nameof(BlockEntityFarmland.GetBlockInfo))]
    public static class FarmlandReadPatch
    {
        public static void Postfix(BlockEntityFarmland __instance, IPlayer forPlayer, StringBuilder dsc)
        {
            var api = __instance?.Api;
            if (api == null || api.Side != EnumAppSide.Client || forPlayer == null || dsc == null) return;
            if (!FarFamiliarity.EyeEnabled(api)) return;

            IFarmlandBlockEntity farmland = __instance!;
            int level = FarLevelOf(api, forPlayer);

            // The soil keeps its counsel from an Untrained hand.
            if (level < Rank.Novice)
            {
                dsc.Clear();
                dsc.AppendLine(Lang.Get("almanactcm:far-eye-blind"));
                return;
            }

            Block? cropBlock = api.World.BlockAccessor.GetBlock(farmland.UpPos);
            bool hasCrop = cropBlock?.CropProps != null;
            string? cropId = hasCrop ? FarFamiliarity.CropIdOf(api, cropBlock) : null;
            var know = FarFamiliarity.KnowledgeOf(api, forPlayer);

            if (level < Rank.Apprentice)
            {
                dsc.Clear();
                AppendRoughSoil(dsc, farmland);
                if (hasCrop) AppendCropLine(api, dsc, cropBlock!, cropId, know, roughOnly: true);
                return;
            }

            // Apprentice and up. Bare ground reads in full; a planted crop the viewer is not
            // Versed with pulls the readout back to figures-plus-rough-crop.
            bool versed = cropId != null && FarFamiliarity.IsVersed(api, know, cropId);
            if (hasCrop && !versed)
            {
                dsc.Clear();
                AppendFullSoil(dsc, farmland);
                AppendCropLine(api, dsc, cropBlock!, cropId, know, roughOnly: true);
            }
            // else: the vanilla readout stands untouched (bare ground, or a Versed crop).

            // The rotation memory (Journeyman+, the last-borne crop's family Versed).
            if (level >= Rank.Journeyman)
            {
                string? lastId = farmland.CropAttributes?.GetString(LastBoreIdAttr);
                string? lastNutrient = farmland.CropAttributes?.GetString(LastBoreNutrientAttr);
                if (!string.IsNullOrEmpty(lastId) && !string.IsNullOrEmpty(lastNutrient))
                {
                    string? family = FarFamiliarity.FamilyOf(lastId!);
                    if (family != null && FarFamiliarity.IsFamilyVersed(api, know, family))
                        dsc.AppendLine(Lang.Get("almanactcm:far-eye-lastbore", lastNutrient));
                }
            }
        }

        private static void AppendRoughSoil(StringBuilder dsc, IFarmlandBlockEntity farmland)
        {
            float moist = farmland.MoistureLevel;
            string moistWord = Lang.Get(moist < 0.25f ? "almanactcm:far-eye-w-parched"
                : moist < 0.75f ? "almanactcm:far-eye-w-damp" : "almanactcm:far-eye-w-soaked");

            float[] n = farmland.Nutrients;
            int dom = 0;
            for (int i = 1; i < 3; i++) if (n[i] > n[dom]) dom = i;
            string letter = dom == 0 ? "N" : dom == 1 ? "P" : "K";
            string richWord = Lang.Get(n[dom] < 33 ? "almanactcm:far-eye-w-poor"
                : n[dom] < 66 ? "almanactcm:far-eye-w-fair" : "almanactcm:far-eye-w-rich");

            dsc.AppendLine(Lang.Get("almanactcm:far-eye-rough", moistWord, richWord, letter));
        }

        private static void AppendFullSoil(StringBuilder dsc, IFarmlandBlockEntity farmland)
        {
            float[] n = farmland.Nutrients;
            dsc.AppendLine(Lang.Get("almanactcm:far-eye-soil-full",
                (int)n[0], (int)n[1], (int)n[2], (int)(farmland.MoistureLevel * 100)));
        }

        private static void AppendCropLine(ICoreAPI api, StringBuilder dsc, Block cropBlock,
            string? cropId, IReadOnlyDictionary<string, int>? know, bool roughOnly)
        {
            if (cropId == null || !FarFamiliarity.IsAcquainted(api, know, cropId))
            {
                dsc.AppendLine(Lang.Get("almanactcm:far-eye-stranger"));
                return;
            }
            double frac = 1.0;
            if (int.TryParse(cropBlock.LastCodePart(), out int stage) && cropBlock.CropProps!.GrowthStages > 0)
                frac = stage / (double)cropBlock.CropProps.GrowthStages;
            dsc.AppendLine(Lang.Get(frac < 0.5 ? "almanactcm:far-eye-crop-young"
                : frac < 1.0 ? "almanactcm:far-eye-crop-grown" : "almanactcm:far-eye-crop-ready"));
        }
    }

    /// <summary>
    /// Vine-fruit familiarity (pumpkin and the bdcrop melons/squash): their harvest breaks a
    /// plain Block, never reaching the BlockCrop seam, so the bump rides the base
    /// Block.OnBlockBroken with an O(1) block-id probe up front. The registered set resolves
    /// lazily from crop-families.json ripeBlocks on first server break and only RIPE fruit
    /// stages are registered, so an immature pick never counts. Row crops carry ids outside
    /// this set and cannot double-count here.
    /// </summary>
    [HarmonyPatch(typeof(Block), nameof(Block.OnBlockBroken))]
    public static class VineFruitFamiliarityPatch
    {
        private static HashSet<int>? ripeIds;
        private static ICoreAPI? builtFor;
        private static readonly Dictionary<int, string> idToCrop = new();

        public static void Postfix(Block __instance, IWorldAccessor world, Vintagestory.API.MathTools.BlockPos pos, IPlayer byPlayer)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || __instance?.Code == null) return;
            var api = world.Api;
            if (api is not Vintagestory.API.Server.ICoreServerAPI sapi) return;

            if (ripeIds == null || !ReferenceEquals(builtFor, api)) Build(sapi);
            if (!ripeIds!.Contains(__instance.BlockId)) return;

            if (idToCrop.TryGetValue(__instance.BlockId, out string? cropId))
                FarFamiliarity.BumpHarvest(sapi, byPlayer, cropId);
        }

        private static void Build(Vintagestory.API.Server.ICoreServerAPI sapi)
        {
            ripeIds = new HashSet<int>();
            idToCrop.Clear();
            builtFor = sapi;
            FarFamiliarity.EnsureLoaded(sapi);
            foreach (var (code, cropId) in FarFamiliarity.RipeBlockCodes())
            {
                Block? block = sapi.World.GetBlock(new AssetLocation(code));
                if (block == null || block.BlockId == 0) continue; // mod absent: entry stays inert
                ripeIds.Add(block.BlockId);
                idToCrop[block.BlockId] = cropId;
            }
            TcmLog.Cat(sapi, "far", $"vine-fruit familiarity: {ripeIds.Count} ripe fruit block(s) registered");
        }
    }

    /// <summary>
    /// The seed in the hand (found in play 2026-08-23: the farmland hover was gated but this
    /// was not, and it is the LEAKIER of the two). Vanilla's ItemPlantableSeed.GetHeldItemInfo
    /// unconditionally appends the crop's required nutrient, its exact consumption, its growth
    /// time in days, and both frost limits, to any hand holding any seed. That is precisely
    /// the crop-property knowledge the Grower's Eye sells, handed over before the player has
    /// ever planted the thing.
    ///
    /// Gated on FAMILIARITY rather than rank, and deliberately so: a seed tells you about the
    /// SPECIES, and the ruling is that familiarity decides what you know while rank decides
    /// what your hands do. Rank appears only as the Untrained floor, because a hand that
    /// cannot read soil cannot read a seed either.
    ///
    /// Implementation note: the five lines are stripped by matching their rendered Lang
    /// prefixes rather than by truncating the buffer, because the method calls base first and
    /// a length-based truncation would also eat the item's ordinary tooltip. If the crop block
    /// cannot be resolved (a modded seed whose code does not follow the vanilla pattern), the
    /// tooltip is left exactly as vanilla wrote it: silence beats a wrong readout.
    /// </summary>
    [HarmonyPatch(typeof(ItemPlantableSeed), nameof(ItemPlantableSeed.GetHeldItemInfo))]
    public static class SeedReadPatch
    {
        public static void Postfix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
        {
            var api = world?.Api;
            if (api == null || api.Side != EnumAppSide.Client || dsc == null) return;
            if (!FarFamiliarity.EyeEnabled(api)) return;

            var player = (api as Vintagestory.API.Client.ICoreClientAPI)?.World?.Player;
            if (player == null) return;

            var stack = inSlot?.Itemstack;
            string? type = stack?.Collectible?.Variant?["type"];
            if (type == null) return;

            // Same resolution vanilla uses, so we gate exactly the block it described.
            Block? cropBlock = world.GetBlock(stack!.Collectible.CodeWithPath("crop-" + type + "-1"));
            if (cropBlock?.CropProps == null) return;

            string? cropId = FarFamiliarity.CropIdOf(api, cropBlock);
            if (cropId == null) return; // unknown crop: leave vanilla's text alone

            var know = FarFamiliarity.KnowledgeOf(api, player);
            bool versed = FarFamiliarity.IsVersed(api, know, cropId);
            if (versed) return; // earned it: vanilla's full figures stand

            int level = FarLevelOf(api, player);
            bool acquainted = level >= Rank.Novice && FarFamiliarity.IsAcquainted(api, know, cropId);

            // Strip the five crop-property lines vanilla appended, leaving the item's own
            // ordinary tooltip (name, satiety, freshness) untouched.
            string[] prefixes =
            {
                Lang.Get("soil-nutrition-requirement"),
                Lang.Get("soil-nutrition-consumption"),
                Lang.Get("soil-growth-time"),
                Lang.Get("crop-coldresistance", 0).Split('{')[0].TrimEnd(),
                Lang.Get("crop-heatresistance", 0).Split('{')[0].TrimEnd(),
            };
            var kept = new List<string>();
            foreach (string line in dsc.ToString().Split('\n'))
            {
                bool drop = false;
                foreach (string p in prefixes)
                {
                    if (p.Length > 0 && line.TrimStart().StartsWith(p, System.StringComparison.Ordinal)) { drop = true; break; }
                }
                if (!drop) kept.Add(line);
            }
            dsc.Clear();
            dsc.Append(string.Join("\n", kept).TrimEnd('\n'));
            dsc.AppendLine();

            if (level < Rank.Novice)
            {
                dsc.AppendLine(Lang.Get("almanactcm:far-seed-blind"));
                return;
            }
            if (!acquainted)
            {
                dsc.AppendLine(Lang.Get("almanactcm:far-seed-stranger"));
                return;
            }

            // Acquainted: the shape of the crop's needs, in words, without the figures.
            dsc.AppendLine(Lang.Get("almanactcm:far-seed-hunger", cropBlock.CropProps.RequiredNutrient.ToString()));
            float cold = cropBlock.CropProps.ColdDamageBelow;
            dsc.AppendLine(Lang.Get(cold <= -8 ? "almanactcm:far-seed-hardy"
                : cold >= 2 ? "almanactcm:far-seed-tender" : "almanactcm:far-seed-middling"));
            double months = cropBlock.CropProps.TotalGrowthMonths;
            dsc.AppendLine(Lang.Get(months <= 0.75 ? "almanactcm:far-seed-quick"
                : months >= 2.0 ? "almanactcm:far-seed-slow" : "almanactcm:far-seed-season"));
        }
    }
}

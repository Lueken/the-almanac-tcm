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
/// TWO CHANNELS, INDEPENDENT (RULED by Jeffrey 2026-08-23, superseding the single rank-ceiling
/// ladder this shipped with). The readout is two separate things stacked in one tooltip, and
/// neither gates the other:
///
///  - THE GROUND is read by FAR RANK. Health, fertility and hydration are a farmer's skill of
///    the eyes, and no amount of familiarity with a plant teaches them.
///      Untrained: nothing. Novice+: rough words (parched/damp/soaked, dominant nutrient
///      poor/fair/rich). Apprentice+: the figures.
///
///  - THE PLANT is read by FAMILIARITY, with NO rank in it at all. Which nutrient it wants,
///    what cold and heat it stands, how long it holds the ground, and how far along it is.
///      Stranger: a stranger line. Acquainted: the same words the seed in the hand gives, so
///      the two surfaces cannot contradict each other. Versed: the figures.
///
/// The rule this keeps: familiarity decides what you KNOW, rank decides what your HANDS do.
/// Reading soil is a trained hand. Recognising a plant you have grown a hundred times is not.
/// The earlier version let rank cap the crop lines, which is how an Untrained hand that had
/// grown garlic five times could read it in the book and not in the world.
///
///  - Journeyman+ with the family Versed: the rotation memory line ("last bore a K-hungry
///    crop"). This one IS a ground reading, which is why it is the single rank gate left on
///    anything crop-shaped. Stored in the farmland's own CropAttributes tree (BEFarmland.cs:
///    351/368: serialized AND synced, the sanctioned bag; no serialization patches).
///
/// Vanilla's untouched text is left alone only when BOTH channels are at full, because that is
/// exactly what vanilla already says. Anywhere else the tooltip is rebuilt from the farmland's
/// synced state, which drops vanilla's coloured growth-speed line (deliberately: how THIS crop
/// responds to THIS soil is crop-property knowledge) and any fertilizer-overlay detail.
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

            Block? cropBlock = api.World.BlockAccessor.GetBlock(farmland.UpPos);
            bool hasCrop = cropBlock?.CropProps != null;
            string? cropId = hasCrop ? FarFamiliarity.CropIdOf(api, cropBlock) : null;
            var know = FarFamiliarity.KnowledgeOf(api, forPlayer);

            // Two channels, neither gating the other: the ground by rank, the plant by
            // familiarity. Bare ground counts as a full plant reading, since there is no plant.
            bool groundFull = level >= Rank.Apprentice;
            bool plantFull = !hasCrop || (cropId != null && FarFamiliarity.IsVersed(api, know, cropId));

            if (!(groundFull && plantFull))
            {
                dsc.Clear();

                if (groundFull) AppendFullSoil(dsc, farmland);
                else if (level >= Rank.Novice) AppendRoughSoil(dsc, farmland);
                else dsc.AppendLine(Lang.Get("almanactcm:far-eye-blind"));

                if (hasCrop) AppendCrop(api, dsc, cropBlock!, cropId, know);
            }
            // else: vanilla's own readout already says both channels in full; leave it.

            // Soil sickness (RULED 2026-08-24). A GROUND reading, so it rides the rank channel,
            // and it is appended outside the compose block above so a fully-read tile still shows
            // it rather than falling through to vanilla's silence.
            //
            // The words tier matters more than the figures here. Sickness is the one thing in the
            // domain that punishes without being asked about, so the effect has to be legible
            // before the number is: a Novice sees that the ground is tired of this crop, and only
            // an Apprentice gets to measure how tired.
            // READS THE CLIENT MIRROR, NOT THE STORE. This block asked `api is ICoreServerAPI`
            // until 2026-08-24, inside a postfix that has already returned unless the side is
            // Client. Those interfaces are disjoint, so it was dead from the day it shipped and
            // sickness punished in total silence. The store is server-only by design, so the fix
            // is the synced snapshot rather than a widened type test.
            if (level >= Rank.Novice)
            {
                var sick = FarSoilSickness.ClientRead(__instance!.Pos);
                // Ground can be sick with several families at once. Name the one standing in it
                // if that is the sick one, because it explains the crop in front of the reader;
                // otherwise name the worst, because that is what the ground most needs said.
                string? sickFam = null;
                double lvl = 0;
                // Selection runs on Notable, not on Bites (RULED 2026-08-24). A tile reading 34
                // in brassicas said nothing, which is what "nothing is felt below the clean line"
                // promises, but one more turnip lands it at 68 and deep into a penalty. The
                // farmer was being asked to plant blind onto ground that looked the same at 2 as
                // at 39. The repeat stays free; the blindness goes.
                if (sick != null && sick.Count > 0)
                {
                    string? plantedFam = cropId == null ? null : FarFamiliarity.FamilyOf(cropId);
                    if (plantedFam != null && sick.TryGetValue(plantedFam, out double pf)
                        && FarSoilSickness.Notable(pf)) { sickFam = plantedFam; lvl = pf; }
                    else
                        foreach (var kv in sick)
                            if (kv.Value > lvl && FarSoilSickness.Notable(kv.Value))
                            { sickFam = kv.Key; lvl = kv.Value; }
                }
                if (sickFam != null)
                {
                    string famName = Lang.HasTranslation("almanactcm:far-family-" + sickFam)
                        ? Lang.Get("almanactcm:far-family-" + sickFam).ToLowerInvariant()
                        : sickFam;
                    bool felt = FarSoilSickness.Bites(lvl);
                    // The tiring line quotes no multipliers on purpose: nothing is being charged
                    // yet, and printing x1.00 twice would read as a penalty that is not there.
                    // It quotes the line instead, so a reader can measure their own margin.
                    dsc.AppendLine(level >= Rank.Apprentice
                        ? (felt
                            ? Lang.Get("almanactcm:far-eye-sick-full", famName, (int)lvl,
                                       FarSoilSickness.SpeedMul(lvl).ToString("0.00"),
                                       FarSoilSickness.YieldMul(lvl).ToString("0.00"))
                            : Lang.Get("almanactcm:far-eye-sick-tiring-full", famName, (int)lvl,
                                       FarSoilSickness.CleanLine))
                        : Lang.Get(felt ? "almanactcm:far-eye-sick-rough"
                                        : "almanactcm:far-eye-sick-tiring-rough", famName));

                    // The cure, offered only where there is something to cure and only to a hand
                    // that has earned the knowing. The labour itself is open to everyone (the
                    // stabiliser scope's "gate the knowledge, never the labour"), so this line is
                    // the whole of what rank buys: a farmer below it who has been told the trick
                    // by another player can still do it, and it still works.
                    if (level >= FarBiofumigation.ReadRank && FarBiofumigation.IsCandidate(api, cropBlock))
                        dsc.AppendLine(Lang.Get("almanactcm:far-eye-biofum"));
                }
            }

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

        /// <summary>
        /// The plant channel. No rank anywhere in here by ruling: what a grower knows about a
        /// crop comes from having grown it. The Acquainted lines are deliberately the SAME
        /// strings the seed in the hand prints, because a player who can read a garlic seed
        /// must not then find the growing garlic illegible.
        /// </summary>
        private static void AppendCrop(ICoreAPI api, StringBuilder dsc, Block cropBlock,
            string? cropId, IReadOnlyDictionary<string, int>? know)
        {
            if (cropId == null || !FarFamiliarity.IsAcquainted(api, know, cropId))
            {
                dsc.AppendLine(Lang.Get("almanactcm:far-eye-stranger"));
                return;
            }

            var cp = cropBlock.CropProps!;
            bool versed = FarFamiliarity.IsVersed(api, know, cropId);
            int.TryParse(cropBlock.LastCodePart(), out int stage);

            // What this plant gives at this stage, read live from the drop tables. Null means the
            // ladder could not be walked, and then we fall back to vanilla's meaning of ripe
            // rather than guess: a wrong ripeness claim is worse than a vague one.
            var curve = FarYieldCurve.Of(api, cropBlock);

            // How far along it is: a fact about the plant in front of you, so familiarity.
            //
            // For a BOLTING crop this is measured against the PEAK, not the last stage. Art of
            // Growing's roots and leaves peak mid-life and then decline into a stage that drops no
            // food at all, so reporting ripeness against the last stage would aim the farmer at
            // the one moment that feeds nobody. Grains are unaffected and keep the vanilla reading.
            if (curve != null && curve.TurnsOver && stage > 0)
            {
                dsc.AppendLine(Lang.Get(
                      stage >= curve.FinalStage
                          ? (curve.Bolts ? "almanactcm:far-eye-crop-bolted"
                                         : "almanactcm:far-eye-crop-turned")
                    : stage >= curve.GoingOverStage ? "almanactcm:far-eye-crop-goingover"
                    : stage >= curve.PeakStage ? "almanactcm:far-eye-crop-ready"
                    : stage / (double)curve.PeakStage < 0.5 ? "almanactcm:far-eye-crop-young"
                    : "almanactcm:far-eye-crop-grown"));

                // The SHAPE of its life, not only where it stands in it. Ruled 2026-08-24: a
                // grower who knows the crop should be able to see the three phases coming
                // (growing, then the harvest window, then the seed head) rather than discovering
                // the third one by losing a harvest to it. Plain words at this tier; Versed gets
                // the same fact as stage figures below and does not need it said twice.
                if (!versed)
                    dsc.AppendLine(Lang.Get(curve.Bolts ? "almanactcm:far-eye-life-bolt"
                                                        : "almanactcm:far-eye-life-turn"));
            }
            else
            {
                double frac = 1.0;
                if (stage > 0 && cp.GrowthStages > 0)
                    frac = stage / (double)cp.GrowthStages;
                dsc.AppendLine(Lang.Get(frac < 0.5 ? "almanactcm:far-eye-crop-young"
                    : frac < 1.0 ? "almanactcm:far-eye-crop-grown" : "almanactcm:far-eye-crop-ready"));
            }

            // The seed economy. On these crops a harvest for the table returns NO seed, so a
            // farmer who always lifts at the peak empties the seed bin with nothing explaining
            // why. Naming it is the single most valuable line here.
            //
            // Acquainted hears it only once the plant is worth taking, because before that the
            // decision is not live and the line is noise on every seedling. Versed gets the
            // figures at any stage, because that tier is planning rather than deciding.
            if (curve != null && curve.Bolts && curve.FoodOrSeedNeverBoth)
            {
                if (versed)
                    dsc.AppendLine(Lang.Get("almanactcm:far-eye-crop-seedfork",
                        curve.PeakStage, curve.FinalStage,
                        (int)System.Math.Round(curve.PeakFood)));
                else if (stage >= curve.PeakStage)
                    dsc.AppendLine(Lang.Get("almanactcm:far-eye-crop-seedcost"));
            }
            else if (curve != null && curve.Transforms && versed)
            {
                // The leaf-then-head crops keep giving food at the end, so there is no seed cost
                // to warn about. What a Versed grower needs is where the leaf window closes.
                dsc.AppendLine(Lang.Get("almanactcm:far-eye-crop-turnfork",
                    curve.PeakStage, curve.FinalStage));
            }

            if (versed)
            {
                double days = cp.TotalGrowthMonths > 0
                    ? cp.TotalGrowthMonths * api.World.Calendar.DaysPerMonth
                    : cp.TotalGrowthDays;
                dsc.AppendLine(Lang.Get("almanactcm:far-eye-crop-figures",
                    cp.RequiredNutrient.ToString(),
                    (int)cp.NutrientConsumption,
                    (int)cp.ColdDamageBelow,
                    (int)cp.HeatDamageAbove,
                    (int)System.Math.Round(days)));
                return;
            }

            dsc.AppendLine(Lang.Get("almanactcm:far-seed-hunger", cp.RequiredNutrient.ToString()));
            dsc.AppendLine(Lang.Get(cp.ColdDamageBelow <= -8 ? "almanactcm:far-seed-hardy"
                : cp.ColdDamageBelow >= 2 ? "almanactcm:far-seed-tender" : "almanactcm:far-seed-middling"));
            double months = cp.TotalGrowthMonths;
            dsc.AppendLine(Lang.Get(months <= 0.75 ? "almanactcm:far-seed-quick"
                : months >= 2.0 ? "almanactcm:far-seed-slow" : "almanactcm:far-seed-season"));
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
    /// what your hands do. There is NO rank in this surface at all (RULED 2026-08-23, replacing
    /// the Untrained floor this shipped with): a seed you have grown is a seed you recognise,
    /// whatever your hands can do with soil. far-seed-blind is retired by that ruling and kept
    /// only so an older translation does not break.
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

            bool acquainted = FarFamiliarity.IsAcquainted(api, know, cropId);

            // Strip the five crop-property lines vanilla appended, leaving the item's own
            // ordinary tooltip (name, satiety, freshness) untouched.
            // The first three keys take no argument, so Lang.Get returns the bare label and
            // matching is direct. The two resistance keys DO take one, and Lang.Get
            // substitutes it, so asking for the template back and splitting on '{' returns
            // the whole rendered string and matches nothing (found in play 2026-08-23: the
            // frost lines survived the strip). Render them with a sentinel instead and cut
            // at it, which yields the true literal prefix whatever the translation says.
            const string mark = "￿";
            string[] prefixes =
            {
                Lang.Get("soil-nutrition-requirement"),
                Lang.Get("soil-nutrition-consumption"),
                Lang.Get("soil-growth-time"),
                Lang.Get("crop-coldresistance", mark).Split(mark)[0],
                Lang.Get("crop-heatresistance", mark).Split(mark)[0],
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

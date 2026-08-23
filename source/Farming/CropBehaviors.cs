using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace AlmanacTcm.Farming;

/// <summary>
/// The two FAR crop behaviors (RULED 2026-08-22, reality baseline: docs/design/
/// 2026-08-22_far-crop-reality-baseline.md). Both ride the engine's own per-crop extension
/// point (vsapi CropBehavior, attached via cropProps.behaviors in asset patches, registered
/// in the ModSystem), so every crop mod in the adoption set gets them from its blocktype
/// regardless of class: AoG's AOGBlockCrop, bdcrop's and DAR's plain BlockCrop alike.
///
/// Registered as "AlmanacNitrogenFixing" and "AlmanacSecondaryNutrients". The attachment
/// patches live in assets/almanactcm/patches/far-crop-behaviors.json, dependsOn-gated per
/// source mod so entries stay inert until that mod is present.
/// </summary>
public class CropBehaviorNitrogenFixing : CropBehavior
{
    // Per-stage N added to the farmland below, and the ceiling it will never push N past.
    // The cap is OURS to enforce: BESoilNutrition.ConsumeNutrients floors at zero with no
    // ceiling (BESoilNutrition.cs:420), so uncapped fixation would be a soil pump.
    private float fixPerStage = 1.0f;
    private float nCap = 70f;

    public CropBehaviorNitrogenFixing(Block block) : base(block) { }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);
        fixPerStage = properties["fixPerStage"].AsFloat(1.0f);
        nCap = properties["nCap"].AsFloat(70f);
    }

    public override bool TryGrowCrop(ICoreAPI api, IFarmlandBlockEntity farmland,
        double currentTotalHours, int newGrowthStage, ref EnumHandling handling)
    {
        handling = EnumHandling.PassThrough; // never touches the growth roll itself
        if (api.Side != EnumAppSide.Server) return false;
        float[]? nutrients = farmland?.Nutrients;
        if (nutrients == null || nutrients.Length < 1) return false;

        float cur = nutrients[0];
        if (cur >= nCap) return false; // fixation restores, it never super-charges
        nutrients[0] = Math.Min(nCap, cur + fixPerStage);
        (farmland as BlockEntity)?.MarkDirty(true);
        return false;
    }
}

/// <summary>
/// Secondary nutrient draw: vanilla crops consume exactly one nutrient (requiredNutrient,
/// which stays the dominant letter the Grower's Eye reveals); real crops also draw on the
/// others. Vanilla itself already does three-nutrient draw for fruiting bushes
/// (BEBehaviorFruitingBush.cs:201-203), so this only brings row crops up to the same
/// honesty. Properties n/p/k are TOTAL draw across the whole grow (the nutrientConsumption
/// convention); each growth tick takes total/(growthStages-1). Values never touch the
/// dominant nutrient's own vanilla consumption — set the property for the OTHER letters only.
/// </summary>
public class CropBehaviorSecondaryNutrients : CropBehavior
{
    private float totalN, totalP, totalK;

    public CropBehaviorSecondaryNutrients(Block block) : base(block) { }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);
        totalN = properties["n"].AsFloat(0f);
        totalP = properties["p"].AsFloat(0f);
        totalK = properties["k"].AsFloat(0f);
    }

    public override bool TryGrowCrop(ICoreAPI api, IFarmlandBlockEntity farmland,
        double currentTotalHours, int newGrowthStage, ref EnumHandling handling)
    {
        handling = EnumHandling.PassThrough;
        if (api.Side != EnumAppSide.Server) return false;
        float[]? nutrients = farmland?.Nutrients;
        if (nutrients == null || nutrients.Length < 3) return false;

        int stages = Math.Max(1, (block?.CropProps?.GrowthStages ?? 1) - 1);
        // Direct array draw with the vanilla floor; the farmland's visual fertility update
        // rides the dominant nutrient's own consumption each stage, so skipping
        // UpdateFarmlandBlock here costs nothing visible.
        if (totalN > 0) nutrients[0] = Math.Max(0, nutrients[0] - totalN / stages);
        if (totalP > 0) nutrients[1] = Math.Max(0, nutrients[1] - totalP / stages);
        if (totalK > 0) nutrients[2] = Math.Max(0, nutrients[2] - totalK / stages);
        (farmland as BlockEntity)?.MarkDirty(true);
        return false;
    }
}

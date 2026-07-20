using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// FAR Phase 1a hooks (rank-bonus-design §FAR, ruled 2026-07-09; technique-maps §FAR). The
/// player-attributed vanilla-floor verbs plus the shared trough->birth attribution spine.
///
/// Every seam is resolved by name and patched manually inside <see cref="PatchConditional"/> so a
/// signature drift on any one verb WARNs and skips that verb alone, never aborting the mod (the
/// 0.3.85 isolation lesson). Patch-method params are named to match each original's params —
/// Harmony binds by name.
///
/// Verbs live this build:
///   tilling (ItemHoe.DoTill), planting (BlockEntityFarmland.TryPlant), harvesting
///   (BlockCrop.OnBlockBroken, yield-proportional by crop stage), milking
///   (EntityBehaviorMilkable.MilkingComplete), eggs (BlockEntityHenBox.OnInteract), orchard
///   (BlockEntityFruitTreePart.OnBlockInteractStop), beekeeping (BlockSkep.OnBlockBroken), and
///   the feeding loop: the trough owner is stamped at fill and, at ConsumeOnePortion, banks FAR
///   feeding AND writes the durable `raisedBy` stamp onto the eating animal that the unattended
///   ANI birth reads (TCM writes its own stamp — xSkills is a design reference, not a runtime dep).
///   Shearing rides shearlib (conditional).
///
/// Deferred to Phase 1b (their own careful increment): the success-gated graft (owner-at-
/// placement, outcome at a delayed grow tick), the primitivesurvival furrow override, the ithania
/// vermiculture maintenance loop, and the unattended cooking-completion sinks (COO side).
/// </summary>
public static class FarPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;

    /// <summary>Trough pos -> the uid of whoever last filled it. Transient (in-memory): the
    /// durable half of feed attribution is the `raisedBy` stamp written onto the animal, which
    /// persists on its WatchedAttributes. A restart forgetting a trough's filler only costs the
    /// next portion's credit until someone tops it up again.</summary>
    private static readonly Dictionary<string, string> troughOwners = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
    }

    // ------------------------------------------------------------ seam wiring

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        Hook(api, harmony, "Vintagestory.GameContent.ItemHoe", "DoTill", nameof(TillPostfix), "FAR tilling");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFarmland", "TryPlant", nameof(PlantPostfix), "FAR planting");
        Hook(api, harmony, "Vintagestory.GameContent.BlockCrop", "OnBlockBroken", nameof(HarvestPostfix), "FAR harvesting");
        Hook(api, harmony, "Vintagestory.GameContent.EntityBehaviorMilkable", "MilkingComplete", nameof(MilkPostfix), "FAR milking");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityHenBox", "OnInteract", nameof(EggPostfix), "FAR eggs");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFruitTreePart", "OnBlockInteractStop", nameof(OrchardPostfix), "FAR orchard");
        Hook(api, harmony, "Vintagestory.GameContent.BlockSkep", "OnBlockBroken", nameof(BeekeepPostfix), "FAR beekeeping");

        // The feeding loop: two hooks on the trough — store the filler, then credit + stamp at
        // the portion consume.
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityTrough", "OnInteract", nameof(TroughFillPostfix), "FAR trough fill");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityTrough", "ConsumeOnePortion", nameof(TroughConsumePostfix), "FAR feeding");

        // Shearing — shearlib's library verb (success-gated: a clean shear only; the damaging
        // shear = zero XP lever is Phase 2).
        if (api.ModLoader.IsModEnabled("shearlib"))
            Hook(api, harmony, "ShearLib.EntityBehaviorShearable", "DoShear", nameof(ShearPostfix), "FAR shearing");
    }

    /// <summary>Resolve a type+method by name and patch a postfix onto it, warning-and-skipping
    /// on any miss. The whole call is inside the Start Try wrapper, so a throw here is contained.</summary>
    private static void Hook(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.Method(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName}.{method}); that verb is inactive this build"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    private static IPlayer? PlayerOf(EntityAgent? agent) => (agent as EntityPlayer)?.Player;

    private static bool ServerSide(IWorldAccessor? world) => world?.Side == EnumAppSide.Server;

    /// <summary>EntityBehavior.entity is protected; read it by reflection (the HunPatches
    /// DressingPatch pattern) so a behaviour-typed patch can reach its owning entity.</summary>
    private static Entity? BehaviorEntity(EntityBehavior beh) =>
        AccessTools.Field(typeof(EntityBehavior), "entity")?.GetValue(beh) as Entity;

    // ------------------------------------------------------------ tilling

    public static void TillPostfix(EntityAgent byEntity, BlockSelection blockSel)
    {
        IPlayer? player = PlayerOf(byEntity);
        if (player == null || blockSel == null || !ServerSide(byEntity?.World)) return;
        Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechTilling,
            HashCode.Combine(blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z));
    }

    // ------------------------------------------------------------ planting

    public static void PlantPostfix(BlockEntity __instance, EntityAgent byEntity, bool __result)
    {
        if (!__result) return;
        IPlayer? player = PlayerOf(byEntity);
        if (player == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechPlanting,
            HashCode.Combine(__instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z));
    }

    // ------------------------------------------------------------ harvesting (yield-proportional)

    /// <summary>The outcome is the practice signal (ruled): raw scales with the crop stage —
    /// ripe = full, penultimate = partial, an immature break ~ nothing. Stage parses from the
    /// block's last code part; the total comes from CropProps.GrowthStages by reflection so this
    /// stays a Block-typed patch (BlockCrop need not be a compile-time reference).</summary>
    public static void HarvestPostfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        if (byPlayer == null || !ServerSide(world) || __instance?.Code == null) return;

        double ripeFrac = 1.0;
        if (int.TryParse(__instance.LastCodePart(), out int stage))
        {
            object? stages = Traverse.Create(__instance).Property("CropProps").Property("GrowthStages").GetValue();
            if (stages is int total && total > 0) ripeFrac = Math.Min(1.0, stage / (double)total);
        }
        if (ripeFrac <= 0.01) return; // an immature seed-only break banks nothing (anti-farm)

        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechHarvesting,
            HashCode.Combine("crop", pos.X >> 2, pos.Z >> 2, world.ElapsedMilliseconds / 1000), ripeFrac);
    }

    // ------------------------------------------------------------ milking

    public static void MilkPostfix(EntityBehavior __instance, EntityAgent byEntity)
    {
        IPlayer? player = PlayerOf(byEntity);
        Entity? cow = __instance == null ? null : BehaviorEntity(__instance);
        if (player == null || cow?.World?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechMilking,
            HashCode.Combine("milk", cow.EntityId, cow.World.ElapsedMilliseconds / 60000));
    }

    // ------------------------------------------------------------ eggs (spammy: heavy dedup)

    public static void EggPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        // Coop sweep dedup: bucket by a wide area + a minute so a row of hen boxes collapses to
        // one context (the henbox is the weakest, most farmable row — ruled).
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechEggs,
            HashCode.Combine("egg", __instance.Pos.X >> 3, __instance.Pos.Z >> 3, __instance.Api.World.ElapsedMilliseconds / 60000));
    }

    // ------------------------------------------------------------ orchard (pick + prune)

    public static void OrchardPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechOrchard,
            HashCode.Combine("orchard", __instance.Pos.X >> 2, __instance.Pos.Z >> 2, __instance.Api.World.ElapsedMilliseconds / 30000));
    }

    // ------------------------------------------------------------ beekeeping (harvest a full skep)

    public static void BeekeepPostfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        if (byPlayer == null || !ServerSide(world)) return;
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechBeekeeping,
            HashCode.Combine("bee", pos.X >> 2, pos.Z >> 2, world.ElapsedMilliseconds / 30000));
    }

    // ------------------------------------------------------------ feeding + the raisedBy stamp

    /// <summary>A player filling a trough is the feed owner for the animals that eat from it.</summary>
    public static void TroughFillPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        troughOwners[PosKey(__instance.Pos)] = byPlayer.PlayerUID;
    }

    /// <summary>An animal eats a portion: bank FAR feeding to the trough's owner AND stamp the
    /// animal with `raisedBy` = that owner, the durable attribution the unattended ANI birth reads.</summary>
    public static void TroughConsumePostfix(BlockEntity __instance, Entity entity)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || entity == null) return;
        if (!troughOwners.TryGetValue(PosKey(__instance.Pos), out string? uid) || uid == null) return;

        entity.WatchedAttributes?.SetString(AniDomain.RaisedByAttr, uid);
        IPlayer? owner = __instance.Api.World.PlayerByUid(uid);
        if (owner == null) return; // owner offline; the stamp still landed, their feeding waits for them
        Core?.Ledger?.Log(owner, FarDomain.Code, FarDomain.TechFeeding,
            HashCode.Combine("feed", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 60000));
    }

    // ------------------------------------------------------------ shearing (shearlib)

    /// <summary>DoShear fires on a shear; Phase 2 adds the damaging-shear = zero XP gate. For now
    /// a shear banks FAR shearing, regrowth-gated by the animal itself (you cannot reshear at will).</summary>
    public static void ShearPostfix(EntityBehavior __instance, EntityAgent byEntity)
    {
        IPlayer? player = PlayerOf(byEntity);
        Entity? animal = __instance == null ? null : BehaviorEntity(__instance);
        if (player == null || animal?.World?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechShearing,
            HashCode.Combine("shear", animal.EntityId, animal.World.ElapsedMilliseconds / 60000));
    }
}

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
        // The graft owner map is the one FAR store that MUST persist: the take-or-die roll can
        // land days after placement, across restarts, with the placer offline (agent report
        // far-graft-seams-1.22.3.md; the HunPatches savegame pattern).
        api.Event.SaveGameLoaded += LoadGraftOwners;
        api.Event.GameWorldSave += SaveGraftOwners;
    }

    // ------------------------------------------------------------ graft owner persistence

    /// <summary>Cutting pos -> placer uid. Persisted in the savegame; ExchangeBlock at success
    /// keeps the same BE at the same pos, so pos-keying is stable placement-to-outcome.</summary>
    private static Dictionary<string, string> graftOwners = new();

    private static void LoadGraftOwners()
    {
        try
        {
            byte[]? data = sapi!.WorldManager.SaveGame.GetData("almanacFarGraftOwners");
            if (data != null)
                graftOwners = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
            TcmLog.Cat(sapi, TcmLog.Config, $"FAR graft owners loaded: {graftOwners.Count} pending cutting(s)");
        }
        catch (Exception e) { TcmLog.Error(sapi, $"graft owner map unreadable ({e.Message}); starting empty"); }
    }

    private static void SaveGraftOwners()
    {
        sapi!.WorldManager.SaveGame.StoreData("almanacFarGraftOwners",
            Vintagestory.API.Util.SerializerUtil.Serialize(graftOwners));
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

        // The feeding loop: the filler is stamped at the trough BLOCK interaction, then credited +
        // stamped onto the animal at the portion consume on the BE. OnBlockInteractStart is
        // declared on BlockTrough (:87370, small) and BlockTroughDoubleBlock (:87506, large) —
        // NOT on BlockTroughBase (the 0.3.137 bug: the base hook silently resolved up to the
        // global Block method, which both trough classes override, so the small trough never
        // stamped). DECLARED-strict so a future signature move warns instead of silently
        // patching the wrong method.
        HookDeclared(api, harmony, "Vintagestory.GameContent.BlockTrough", "OnBlockInteractStart", nameof(TroughFillPostfix), "FAR trough fill (small)");
        HookDeclared(api, harmony, "Vintagestory.GameContent.BlockTroughDoubleBlock", "OnBlockInteractStart", nameof(TroughFillPostfix), "FAR trough fill (large)");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityTrough", "ConsumeOnePortion", nameof(TroughConsumePostfix), "FAR feeding");

        // Shearing — shearlib's library verb (success-gated: a clean shear only; the damaging
        // shear = zero XP lever is Phase 2).
        if (api.ModLoader.IsModEnabled("shearlib"))
            Hook(api, harmony, "ShearLib.EntityBehaviorShearable", "DoShear", nameof(ShearPostfix), "FAR shearing");

        if (api.ModLoader.IsModEnabled("primitivesurvival"))
        {
            // PS replaces every hoe's item class with ItemHoeExtended, whose DoTill is an
            // OVERRIDE (:14941, verified PS 5.0.6) — so the vanilla ItemHoe.DoTill hook above
            // never fires on a PS-hoed field (the override rule again). One postfix covers both
            // of the override's branches: the result block tells us whether this till made
            // farmland (tilling) or a furrow channel (the furrow verb).
            HookDeclared(api, harmony, "PrimitiveSurvival.ModSystem.ItemHoeExtended", "DoTill", nameof(TillExtendedPostfix), "FAR tilling/furrow (PS hoe)");

            // Furrow maintenance: clearing debris keeps the channels watering (recurring raw).
            HookDeclared(api, harmony, "PrimitiveSurvival.ModSystem.BEFurrowedLand", "OnInteract", nameof(FurrowMaintainPostfix), "FAR furrow maintenance");
        }

        // Grafting — the pass's headline success-gate (seams re-verified 2026-07-21, agent report
        // far-graft-seams-1.22.3.md): owner stored at TryPlaceBlock (:160762, byPlayer in scope
        // there and ONLY there), outcome at FruitTreeGrowingBranchBH.TryGrow's cutting case —
        // one roll against CuttingRootingChance (ground, 0.25) or CuttingGraftChance (graft,
        // 0.5); win exchanges to a live Branch, loss sets FoliageState.Dead. XP only on the
        // take; a dead cutting banks zero.
        HookDeclared(api, harmony, "Vintagestory.GameContent.BlockFruitTreeBranch", "TryPlaceBlock", nameof(GraftPlacePostfix), "FAR graft placement");
        HookPairDeclared(api, harmony, "Vintagestory.GameContent.FruitTreeGrowingBranchBH", "TryGrow",
            nameof(GraftGrowPrefix), nameof(GraftGrowPostfix), "FAR grafting outcome");

        // Vermiculture — ithania's worm bin (verified V1.1.1, 2026-07-21): the owner is whoever
        // last maintained the bin (bedding/seed/feed via OnInteract, watering via WaterStep);
        // the colony mints worms unattended (TrySpawnWorm from the server tick) and credits the
        // maintainer. The trough shape: owner-at-action, bank-at-unattended-event.
        if (api.ModLoader.IsModEnabled("ithaniaexpandedfishing"))
        {
            HookDeclared(api, harmony, "IthaniaExpandedFishing.BlockEntities.BlockEntityWormBin", "OnInteract", nameof(WormTouchPostfix), "FAR worm-bin maintain");
            HookDeclared(api, harmony, "IthaniaExpandedFishing.BlockEntities.BlockEntityWormBin", "WaterStep", nameof(WormTouchPostfix), "FAR worm-bin watering");
            HookDeclared(api, harmony, "IthaniaExpandedFishing.BlockEntities.BlockEntityWormBin", "TrySpawnWorm", nameof(WormMintPostfix), "FAR vermiculture");
        }

        // Beekeeping under From Golden Combs: FGC swaps populated skeps to its own BE and makes
        // apiculture a repeatable HARVEST-BY-INTERACTION (ceramic pots, Langstroth frames, frame
        // racks), so the vanilla BlockSkep break below only fires on the public-release fallback.
        // Credit the tending interaction on each FGC hive BE (heavy dedup: one credit per hive per
        // ~30s, so idle clicks collapse to a tending session). Verified against FGC 2.0.8.
        if (api.ModLoader.IsModEnabled("fromgoldencombs"))
        {
            // The four interactive hive BEs. BEFGCBeehive (the populated skep) declares no
            // OnInteract — FGC skeps still harvest by BREAKING, which the vanilla
            // BlockSkep.OnBlockBroken hook above already credits (boot-log verified 0.3.142).
            foreach (string be in new[] { "BECeramicBroodPot", "BEFrameRack", "BELangstrothStack", "BELangstrothSuper" })
                Hook(api, harmony, "FromGoldenCombs.BlockEntities." + be, "OnInteract", nameof(FgcHivePostfix), "FAR beekeeping (FGC)");
        }
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

    /// <summary>Like Hook, but the method must be DECLARED on the named type. For override-
    /// sensitive seams: AccessTools.Method silently walks up the hierarchy, and patching an
    /// inherited base method misses every subclass override (the trough lesson).</summary>
    private static void HookDeclared(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
    }

    private static void HookPairDeclared(ICoreAPI api, Harmony harmony, string typeName, string method, string prefix, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
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

    /// <summary>The PS hoe override, both branches: after the till, the block on the ground says
    /// which work was done — a furrow channel banks the furrow verb, farmland banks tilling.</summary>
    public static void TillExtendedPostfix(EntityAgent byEntity, BlockSelection blockSel)
    {
        IPlayer? player = PlayerOf(byEntity);
        if (player == null || blockSel == null || !ServerSide(byEntity?.World)) return;
        Block? now = byEntity!.World.BlockAccessor.GetBlock(blockSel.Position);
        string tech = now?.Code?.Path?.StartsWith("furrowedland") == true ? FarDomain.TechFurrow : FarDomain.TechTilling;
        Core?.Ledger?.Log(player, FarDomain.Code, tech,
            HashCode.Combine(blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z));
    }

    // ------------------------------------------------------------ furrow maintenance (PS)

    public static void FurrowMaintainPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        // Debris clearing along a channel network: one wide bucket per stretch and minute.
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechFurrow,
            HashCode.Combine("furrowfix", __instance.Pos.X >> 3, __instance.Pos.Z >> 3, __instance.Api.World.ElapsedMilliseconds / 60000));
    }

    // ------------------------------------------------------------ grafting (success-gated)

    /// <summary>Owner at placement: byPlayer is in scope HERE and only here (the outcome fires
    /// on an unattended tick, possibly days and restarts later). Persisted map, pos-keyed.</summary>
    public static void GraftPlacePostfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, bool __result)
    {
        if (!__result || byPlayer == null || blockSel == null || !ServerSide(world)) return;
        graftOwners[PosKey(blockSel.Position)] = byPlayer.PlayerUID;
        TcmLog.Cat(world.Api, "far", $"cutting placed at {blockSel.Position} by {byPlayer.PlayerName}; take-or-die pending (silent until the outcome)");
    }

    public readonly record struct GraftState(bool WasCutting, BlockPos? Pos);

    /// <summary>TryGrow fires for every tree part; only a LIVE CUTTING entering it faces the
    /// take-or-die roll. Capture that state so the postfix reads the verdict.</summary>
    public static void GraftGrowPrefix(BlockEntityBehavior __instance, out GraftState __state)
    {
        var be = __instance?.Blockentity as Vintagestory.GameContent.BlockEntityFruitTreeBranch;
        bool wasCutting = be != null
            && be.PartType == Vintagestory.GameContent.EnumTreePartType.Cutting
            && be.FoliageState != Vintagestory.GameContent.EnumFoliageState.Dead;
        __state = new GraftState(wasCutting, be?.Pos);
    }

    /// <summary>The verdict (ruled, the pass's headline success-gate): Cutting -> Branch is the
    /// take, credit the stored placer; FoliageState.Dead is the loss, zero practice, entry
    /// dropped. Still-waiting ticks do nothing.</summary>
    public static void GraftGrowPostfix(BlockEntityBehavior __instance, GraftState __state)
    {
        if (!__state.WasCutting || __state.Pos == null) return;
        var be = __instance?.Blockentity as Vintagestory.GameContent.BlockEntityFruitTreeBranch;
        if (be == null || be.Api?.Side != EnumAppSide.Server) return;

        string key = PosKey(__state.Pos);
        if (be.PartType == Vintagestory.GameContent.EnumTreePartType.Branch)
        {
            graftOwners.TryGetValue(key, out string? uid);
            graftOwners.Remove(key);
            IPlayer? owner = uid == null ? null : be.Api.World.PlayerByUid(uid);
            if (owner == null)
            {
                TcmLog.Cat(be.Api, "far", $"cutting TOOK at {__state.Pos} but the placer is unknown or offline; uncredited");
                return;
            }
            TcmLog.Cat(be.Api, "far", $"cutting TOOK at {__state.Pos} -> grafting credit for {owner.PlayerName}");
            Core?.Ledger?.Log(owner, FarDomain.Code, FarDomain.TechGrafting,
                HashCode.Combine("graft", __state.Pos.X, __state.Pos.Y, __state.Pos.Z));
        }
        else if (be.FoliageState == Vintagestory.GameContent.EnumFoliageState.Dead)
        {
            graftOwners.Remove(key);
            TcmLog.Cat(be.Api, "far", $"cutting DIED at {__state.Pos}; no practice (success-gated by ruling)");
        }
    }

    // ------------------------------------------------------------ vermiculture (ithania)

    /// <summary>Bin pos -> last maintainer. In-memory (the trough pattern): a restart forgets the
    /// maintainer until the next touch, and only the interim mints go uncredited.</summary>
    private static readonly Dictionary<string, string> wormBinOwners = new();

    public static void WormTouchPostfix(BlockEntity __instance, IPlayer byPlayer, bool __result)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        wormBinOwners[PosKey(__instance.Pos)] = byPlayer.PlayerUID;
    }

    /// <summary>A worm minted by the maintained colony: bank to the last maintainer. Bucketed per
    /// hour so a healthy bin is steady, small practice, not a ticker.</summary>
    public static void WormMintPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (!wormBinOwners.TryGetValue(PosKey(__instance.Pos), out string? uid) || uid == null) return;
        IPlayer? owner = __instance.Api.World.PlayerByUid(uid);
        if (owner == null) return;
        Core?.Ledger?.Log(owner, FarDomain.Code, FarDomain.TechVermiculture,
            HashCode.Combine("worm", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 60000));
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

        // CROP GUARD (verified 1.22.3): BlockCrop does NOT override OnBlockBroken, so this patch
        // resolves to the inherited Block.OnBlockBroken and fires on EVERY block break. Only a real
        // crop carries CropProps — without this guard, breaking rock/wood would bank FAR harvesting
        // (the 0.3.134 bug). No CropProps -> not a crop -> not our practice.
        object? cropProps = Traverse.Create(__instance).Property("CropProps").GetValue();
        if (cropProps == null) return;

        double ripeFrac = 1.0;
        if (int.TryParse(__instance.LastCodePart(), out int stage))
        {
            object? stages = Traverse.Create(cropProps).Property("GrowthStages").GetValue();
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

    public static void OrchardPostfix(BlockEntity __instance, float secondsUsed, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        // The vanilla harvest branch requires a held interact past ~1s (the same threshold FGC's
        // pollination prefix checks); a tap-and-release stop is not a pick and banks nothing.
        if (secondsUsed <= 1.1f) return;
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

    /// <summary>A player interacting with a trough (the fill seam) is the feed owner for the
    /// animals that eat from it. Hooked on BOTH declared overrides (BlockTroughBase for the small
    /// trough, BlockTroughDoubleBlock for the large). The key must be the BE's own position —
    /// ConsumeOnePortion reads __instance.Pos — but on a double trough the player can click the
    /// half WITHOUT the BE, so normalize through OtherPartPos before stamping.</summary>
    public static void TroughFillPostfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (byPlayer == null || world?.Side != EnumAppSide.Server || blockSel == null) return;
        if (__instance?.Code?.Path?.Contains("trough") != true) return;

        BlockPos pos = blockSel.Position;
        if (world.BlockAccessor.GetBlockEntity(pos) is not Vintagestory.GameContent.BlockEntityTrough)
        {
            var otherPart = AccessTools.Method(__instance.GetType(), "OtherPartPos");
            if (otherPart?.Invoke(__instance, new object[] { pos }) is BlockPos mate
                && world.BlockAccessor.GetBlockEntity(mate) is Vintagestory.GameContent.BlockEntityTrough)
                pos = mate;
            else return; // no trough BE reachable from this click; nothing to stamp
        }
        string key = PosKey(pos);
        bool changed = !troughOwners.TryGetValue(key, out string? prev) || prev != byPlayer.PlayerUID;
        troughOwners[key] = byPlayer.PlayerUID;
        if (changed) // one line per ownership change, not one per click (the 8-lines-a-fill spam)
            TcmLog.Cat(world.Api, "far", $"trough feed-owner stamp: {pos} -> {byPlayer.PlayerName} (silent by design; credit lands when an animal eats)");
    }

    // ------------------------------------------------------------ beekeeping (FGC re-point)

    /// <summary>Tending an FGC hive (ceramic pot, Langstroth frame/rack, or a populated skep's FGC
    /// BE) banks FAR beekeeping. FGC OnInteract fires on any interaction, so a wide pos+time bucket
    /// collapses idle clicks and the harvest itself into one tending credit per hive per ~30s.</summary>
    public static void FgcHivePostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechBeekeeping,
            HashCode.Combine("fgchive", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 30000));
    }

    /// <summary>An animal eats a portion: bank FAR feeding to the trough's owner AND stamp the
    /// animal with `raisedBy` = that owner, the durable attribution the unattended ANI birth reads.</summary>
    public static void TroughConsumePostfix(BlockEntity __instance, Entity entity)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || entity == null) return;
        if (!troughOwners.TryGetValue(PosKey(__instance.Pos), out string? uid) || uid == null)
        {
            // The diagnostic half of the spine: an eat with no stamped filler is the exact
            // symptom of a dead fill hook (or a restart since the last fill). Loud on purpose.
            TcmLog.Cat(__instance.Api, "far", $"trough portion eaten by {entity.Code?.FirstCodePart()} #{entity.EntityId} at {__instance.Pos} but NO feed-owner stamped; uncredited");
            return;
        }

        entity.WatchedAttributes?.SetString(AniDomain.RaisedByAttr, uid);
        IPlayer? owner = __instance.Api.World.PlayerByUid(uid);
        if (owner == null) return; // owner offline; the stamp still landed but this portion's credit is lost
        TcmLog.Cat(__instance.Api, "far", $"trough portion eaten by {entity.Code?.FirstCodePart()} #{entity.EntityId} -> feeding credit + raisedBy stamp for {owner.PlayerName}");
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

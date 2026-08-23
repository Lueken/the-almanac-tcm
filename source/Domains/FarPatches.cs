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
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
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
        // Harvest: the Phase 2 Untrained dock rides a prefix on the same declared override
        // (dropQuantityMultiplier is passed by ref into the drop roll).
        HookPairDeclared(api, harmony, "Vintagestory.GameContent.BlockCrop", "OnBlockBroken",
            nameof(HarvestDockPrefix), nameof(HarvestPostfix), "FAR harvesting");
        // Fertilizing (the 1a-missing verb, arriving with its Phase 2 thrift): the consume
        // branch takes one item at :51929; the pair credits the application and rolls the
        // rank thrift refund.
        HookPairDeclared(api, harmony, "Vintagestory.GameContent.BlockEntitySoilNutrition", "OnBlockInteract",
            nameof(FertilizePrefix), nameof(FertilizePostfix), "FAR fertilizing");
        Hook(api, harmony, "Vintagestory.GameContent.EntityBehaviorMilkable", "MilkingComplete", nameof(MilkPostfix), "FAR milking");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityHenBox", "OnInteract", nameof(EggPostfix), "FAR eggs");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFruitTreePart", "OnBlockInteractStop", nameof(OrchardPostfix), "FAR orchard");
        // RouteBeekeeping (RULED 2026-07-30): with the BEE domain enabled these seams belong
        // to BeePatches, which grants at finer grain (per-frame contexts, verb-split hiving/
        // combwork/wintering). One presence test decides the owner, so nothing double-grants.
        if (!BeeDomain.Enabled(api))
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
        // The mark-blind fill (LauCaRo's report, 2026-08-21): marked produce could not enter a
        // trough at all. Prefix on the BE interact, both sides like the vanilla body it mirrors.
        HookPrefixDeclared(api, harmony, "Vintagestory.GameContent.BlockEntityTrough", "OnInteract", nameof(TroughMarkBlindPrefix), "FAR trough mark-blind fill");

        // Shearing — shearlib's library verb, success-gated + the Untrained penalty (Phase 2,
        // FAR ruling 4; shearlib 1.3.0 decompiled). DoShear rolls EntityWouldBeDamaged with its
        // OWN Rand draw (:303), so predicting the wound with a second roll (the 0.3.155 bug)
        // reads a different coin: instead we watch ReceiveShearDamage, which fires ONLY on a
        // real wound (:346). The prefix also raises scratchChance for an Untrained hand (the
        // ruled "beginner's shears wound" widening) and restores it in the postfix.
        if (api.ModLoader.IsModEnabled("shearlib"))
        {
            HookPairDeclared(api, harmony, "ShearLib.EntityBehaviorShearable", "DoShear",
                nameof(ShearPrefix), nameof(ShearPostfix), "FAR shearing");
            HookDeclared(api, harmony, "ShearLib.EntityBehaviorShearable", "ReceiveShearDamage",
                nameof(ShearWoundedPostfix), "FAR shear wound-watch");
        }

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
        if (api.ModLoader.IsModEnabled("fromgoldencombs") && !BeeDomain.Enabled(api))
        {
            // Unreachable in practice (fromgoldencombs present IS BeeDomain.Enabled), kept so
            // the stand-down reads explicitly at both beekeeping seam sites.
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

    private static void HookPrefixDeclared(ICoreAPI api, Harmony harmony, string typeName, string method, string prefix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), prefix)));
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

    public readonly record struct GraftState(bool WasCutting, BlockPos? Pos, int FoliageBefore);

    /// <summary>TryGrow fires for every tree part; only a LIVE CUTTING entering it faces the
    /// take-or-die roll. Capture that state (and the foliage, for the Phase 2 death-revert)
    /// so the postfix reads the verdict.</summary>
    public static void GraftGrowPrefix(BlockEntityBehavior __instance, out GraftState __state)
    {
        var be = __instance?.Blockentity as Vintagestory.GameContent.BlockEntityFruitTreeBranch;
        bool wasCutting = be != null
            && be.PartType == Vintagestory.GameContent.EnumTreePartType.Cutting
            && be.FoliageState != Vintagestory.GameContent.EnumFoliageState.Dead;
        __state = new GraftState(wasCutting, be?.Pos, (int)(be?.FoliageState ?? 0));
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
            // Phase 2, the agent-designed resilience retry (vanilla-floored by construction):
            // a ranked owner's dying cutting may cling to life — the death is REVERTED and
            // vanilla re-rolls its own unmodified chance on a later tick. No single graft is
            // ever easier than vanilla, and none is ever certain; the owner entry stays for
            // the next verdict.
            graftOwners.TryGetValue(key, out string? ownerUid);
            IPlayer? keeper = ownerUid == null ? null : be.Api.World.PlayerByUid(ownerUid);
            double retry = keeper == null ? 0
                : FarDomain.BonusT(FarDomain.LevelOf(keeper)) * FarDomain.Knob(FarDomain.GraftRetryGm, 0.50);
            if (retry > 0 && be.Api.World.Rand.NextDouble() < retry)
            {
                be.FoliageState = (Vintagestory.GameContent.EnumFoliageState)__state.FoliageBefore;
                RevertTreeState(be);
                be.MarkDirty(true);
                TcmLog.Cat(be.Api, "far", $"cutting at {__state.Pos} was dying but clung to life ({keeper!.PlayerName}'s tended stock); vanilla rolls again later");
                return;
            }
            graftOwners.Remove(key);
            TcmLog.Cat(be.Api, "far", $"cutting DIED at {__state.Pos}; no practice (success-gated by ruling)");
        }
    }

    /// <summary>Undo the failure branch's tree-state kill (:161525 sets the root's per-type
    /// State to Dead alongside the foliage): find the root BE's FruitTreeRootBH and set the
    /// cutting's tree type back to Young so the growth machinery keeps ticking.</summary>
    private static void RevertTreeState(Vintagestory.GameContent.BlockEntityFruitTreeBranch be)
    {
        try
        {
            var rootBe = be.Api.World.BlockAccessor.GetBlockEntity(be.Pos.AddCopy(be.RootOff))
                as Vintagestory.GameContent.BlockEntityFruitTreeBranch ?? be;
            foreach (var beh in rootBe.Behaviors)
            {
                if (beh.GetType().Name != "FruitTreeRootBH") continue;
                if (Traverse.Create(beh).Field("propsByType").GetValue() is not System.Collections.IDictionary props) return;
                if (be.TreeType != null && props.Contains(be.TreeType))
                {
                    var entry = props[be.TreeType];
                    Traverse.Create(entry).Property("State").SetValue(Vintagestory.GameContent.EnumFruitTreeState.Young);
                }
                return;
            }
        }
        catch (Exception e) { TcmLog.Error(be.Api, $"graft retry tree-state revert failed ({e.Message}); the cutting may still read dead"); }
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
    /// REAL hour so a healthy bin is steady, small practice, not a ticker. (Was /60000, one real
    /// minute, which paid up to 60x the intended cadence — the 2026-07-25 vermiculture ping spam.)</summary>
    public static void WormMintPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (!wormBinOwners.TryGetValue(PosKey(__instance.Pos), out string? uid) || uid == null) return;
        IPlayer? owner = __instance.Api.World.PlayerByUid(uid);
        if (owner == null) return;
        Core?.Ledger?.Log(owner, FarDomain.Code, FarDomain.TechVermiculture,
            HashCode.Combine("worm", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 3600000),
            announceRepeat: false);
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
    /// <summary>The yield hand (RULED 2026-08-22, supersedes the plain Untrained dock): the
    /// per-crop per-rank table from ModConfig/almanactcm/FAR-yields.json rides the by-ref
    /// drop multiplier the base drop roll consumes, COMPOSING with Specialized Classes' own
    /// crop-yield multipliers (both multiply, neither fights). A crop without a table row
    /// falls back to the legacy dock (a level-0 hand bruises the harvest, Novice+ untouched);
    /// the table's master switch off means TCM touches yield not at all.</summary>
    public static void HarvestDockPrefix(Block __instance, IPlayer byPlayer, ref float dropQuantityMultiplier)
    {
        if (byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;
        int level = FarDomain.LevelOf(byPlayer);

        double? tabled = FarYieldTable.MultiplierFor(byPlayer.Entity.World.Api, __instance, level);
        if (tabled != null)
        {
            dropQuantityMultiplier *= (float)tabled.Value;
            return;
        }
        if (level > 0) return;
        dropQuantityMultiplier *= (float)FarDomain.Knob(FarDomain.HarvestDockUntrained, 0.85);
    }

    // ------------------------------------------------------------ fertilizing (verb + thrift)

    public readonly record struct FertState(int HeldId, int HeldSize);

    public static void FertilizePrefix(IPlayer byPlayer, out FertState __state)
    {
        var held = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        __state = new FertState(held?.Collectible?.Id ?? -1, held?.StackSize ?? 0);
    }

    /// <summary>The consume branch took an item: credit the application; then the Phase 2
    /// thrift — from Apprentice up, a chance (to 20% at GM) the application costs nothing
    /// (the spared item is handed back, the powder-thrift shape).</summary>
    public static void FertilizePostfix(BlockEntity __instance, IPlayer byPlayer, bool __result, FertState __state)
    {
        if (!__result || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        var held = byPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        bool consumed = (held?.Collectible?.Id ?? -1) != __state.HeldId || (held?.StackSize ?? 0) < __state.HeldSize;
        if (!consumed) return;

        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechFertilizing,
            HashCode.Combine("fert", __instance.Pos.X >> 2, __instance.Pos.Z >> 2, __instance.Api.World.ElapsedMilliseconds / 30000));

        double chance = FarDomain.BonusT(FarDomain.LevelOf(byPlayer)) * FarDomain.Knob(FarDomain.FertThriftGm, 0.20);
        if (chance > 0 && __instance.Api.World.Rand.NextDouble() < chance
            && held != null && held.Collectible?.Id == __state.HeldId)
        {
            // Refund one of the held fertilizer (the common from-a-stack case; a fully consumed
            // last item just misses its thrift roll — rare and harmless).
            if (byPlayer.InventoryManager!.TryGiveItemstack(new ItemStack(held.Collectible, 1), true))
                TcmLog.Cat(__instance.Api, "far", $"fertilizer thrift: {byPlayer.PlayerName} spared one {held.Collectible?.Code?.Path}");
        }
    }

    public static void HarvestPostfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        if (byPlayer == null || !ServerSide(world) || __instance?.Code == null) return;

        // CROP GUARD. History (worth keeping): at 0.3.134 this seam was hooked NON-declared, so it
        // resolved to the inherited Block.OnBlockBroken and fired on every block break — hence a
        // reflective CropProps probe to tell crops from rock. Two things have since changed:
        //   1. BlockCrop DOES declare OnBlockBroken (verified 1.22.2 BlockCrop.cs:199) and the hook
        //      is now HookPairDeclared, so we only ever bind to the crop override. Rock cannot reach
        //      this method any more.
        //   2. The probe was BROKEN regardless: Block.CropProps is a FIELD (vsapi Block.cs:399), not
        //      a property, so Traverse.Property() returned a zero-traverse and GetValue() gave null
        //      on EVERY harvest — the method returned here every time and FAR harvesting silently
        //      banked nothing since the seam was made declared. Same for BlockCropProperties
        //      .GrowthStages (a field too), which would have pinned ripeFrac at 1.0 and defeated
        //      both the yield-proportional ruling and the immature-break anti-farm guard below.
        // A typed cast now does the guard's job under the compiler, so a future revert to a
        // non-declared hook fails loudly at build time instead of re-crediting rock mining.
        if (__instance is not Vintagestory.GameContent.BlockCrop crop || crop.CropProps == null) return;

        // Unparseable stage or a crop with no declared GrowthStages cannot be scored, so it falls
        // through at full credit rather than being silently dropped — a real harvest of an oddly
        // coded crop should still pay. Every vanilla and modded crop we have seen parses.
        double ripeFrac = 1.0;
        if (int.TryParse(crop.LastCodePart(), out int stage) && crop.CropProps.GrowthStages > 0)
            ripeFrac = Math.Min(1.0, stage / (double)crop.CropProps.GrowthStages);
        // Anti-farm floor: below it the break is not husbandry and banks nothing. See
        // FarDomain.HarvestRipeFloor for why the old 0.01 literal could never fire.
        if (ripeFrac < FarDomain.Knob(FarDomain.HarvestRipeFloor, 0.50)) return;

        // The scythe premium (ruled 2026-08-08): a swing that has to be read and timed pays more
        // per crop than a hand pull. Read the held tool rather than the break path, so a modded
        // scythe that reaches this seam by any route still earns it. An edge case: if the scythe's
        // last durability point breaks it mid-swing, the final crop of that swing sees an empty
        // hand and misses the premium — one crop, and the swing that killed the tool.
        double practice = ripeFrac;
        if (UsingScythe(byPlayer)) practice *= FarDomain.Knob(FarDomain.HarvestScytheBonus, 1.15);

        // PER-CROP CONTEXT (was pos.X >> 2 / pos.Z >> 2, a 4x4 column). The scythe multibreaks the
        // struck crop plus up to five neighbours in one tick (ItemShears.OnBlockBrokenWith,
        // MultiBreakQuantity 5), so a 3x3 of ripe grain landed in one or two dedup buckets inside
        // the same 1s slice and banked one credit for six crops. Exact position gives every crop
        // its own context; the time bucket stays so a tile re-sown and re-harvested later still
        // pays. Two breaks of one position inside a second cannot happen — the first one took it.
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechHarvesting,
            HashCode.Combine("crop", pos.X, pos.Y, pos.Z, world.ElapsedMilliseconds / 1000), practice);

        // Crop familiarity + rotation memory (the Grower's Eye data layer, RULED 2026-08-22).
        // Rides AFTER the ripeness floor above, so only a real harvest teaches: one count per
        // crop per break into the synced Knowledge store (far-crop-<id>), and the farmland
        // below remembers what it last bore in its own CropAttributes tree (serialized and
        // synced by vanilla — BEFarmland.cs:351/368 — so the Journeyman read needs no
        // serialization patches). Unknown crops honestly stay strangers.
        string? cropId = FarFamiliarity.CropIdOf(world.Api, crop);
        if (cropId != null && world.Api is Vintagestory.API.Server.ICoreServerAPI sapi)
        {
            FarFamiliarity.BumpHarvest(sapi, byPlayer, cropId);

            if (world.BlockAccessor.GetBlockEntity(pos.DownCopy()) is Vintagestory.GameContent.BlockEntityFarmland farmlandBe)
            {
                farmlandBe.CropAttributes.SetString(FarGrowerEye.LastBoreIdAttr, cropId);
                farmlandBe.CropAttributes.SetString(FarGrowerEye.LastBoreNutrientAttr,
                    crop.CropProps.RequiredNutrient.ToString());
                farmlandBe.MarkDirty(true);
            }
        }
    }

    /// <summary>True when the player's active hand is a scythe. Checks the declared tool tag first
    /// (vanilla scythe.json carries tool: "Scythe") and falls back to the class, which catches a
    /// modded scythe that subclasses ItemScythe without declaring the tag.</summary>
    private static bool UsingScythe(IPlayer byPlayer)
    {
        var held = byPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible;
        if (held == null) return false;
        return held.Tool == EnumTool.Scythe || held is Vintagestory.GameContent.ItemScythe;
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

    /// <summary>The mark-blind trough fill (LauCaRo's report, 2026-08-21; root cause read from
    /// the 1.22.5 decompile of BlockEntityTrough.OnInteract and ItemSlotTrough).
    ///
    /// THE DEFECT. Vanilla's trough compares stacks with GlobalConstants.IgnoredStackAttributes
    /// at three gates, and TCM's food marks are not in that list. Every vanilla trough
    /// contentConfig is an EXACT item code (grain-rye, vegetable-cabbage, ...), so a marked
    /// grain failed getContentConfig's Equals and could not enter an EMPTY trough at all, and
    /// on a filled trough the merge Equals refused marked-vs-unmarked in either direction.
    /// Which crops failed therefore depended on the player's rank when each was harvested and
    /// on what the trough already held: "certain crops harvested before don't work now."
    ///
    /// THE RULE. Feed loses its mark at the trough. The refuses-to-mix law exists for
    /// PROCESSING, where merging marked into plain would launder the mark; a trough is a
    /// consumption sink, the animal reads no tooltip, and the feed economy keys on the
    /// FILLER's rank, never the crop's mark. Only the PORTION that enters the trough is
    /// stripped: the spoilage value on the player's remaining stack is real and stays.
    ///
    /// SHAPE. Fast path: no marks in hand or trough, vanilla untouched. Otherwise this prefix
    /// mirrors the vanilla body (verified 1.22.5, ~25 lines, stable for years) with clean-clone
    /// comparisons, heals already-marked trough contents (live-server troughs filled before
    /// this fix), and skips the original. Runs BOTH sides, mutating exactly where vanilla does,
    /// so the client-side interaction predicts the same result the server authorizes. The
    /// fill-owner stamp is unaffected: it rides the BLOCK method's postfix, not this one.</summary>
    public static bool TroughMarkBlindPrefix(Vintagestory.GameContent.BlockEntityTrough __instance, IPlayer byPlayer, ref bool __result)
    {
        ItemSlot? hand = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        var world = __instance?.Api?.World;
        if (hand == null || hand.Empty || world == null) return true;

        ItemSlot content = __instance!.Inventory[0];
        bool handMarked = Engine.FoodProvenance.HasFoodMarks(hand.Itemstack);
        bool contentsMarked = Engine.FoodProvenance.HasFoodMarks(content.Itemstack);
        if (!handMarked && !contentsMarked) return true;

        // Heal first: a trough filled with marked feed before this fix keeps refusing plain
        // top-ups until its contents go clean, so clean them on the first touch.
        if (contentsMarked)
        {
            Engine.FoodProvenance.StripFoodMarks(content.Itemstack);
            content.MarkDirty();
        }

        ItemStack cleanHand = hand.Itemstack.Clone();
        Engine.FoodProvenance.StripFoodMarks(cleanHand);
        var cfg = Vintagestory.GameContent.ItemSlotTrough.getContentConfig(
            world, __instance.contentConfigs, new DummySlot(cleanHand));
        if (cfg == null) return true; // not feed; vanilla declines it the same way

        ItemStack[] nonEmpty = __instance.GetNonEmptyContentStacks();
        if (nonEmpty.Length == 0)
        {
            if (hand.StackSize >= cfg.QuantityPerFillLevel)
            {
                ItemStack taken = hand.TakeOut(cfg.QuantityPerFillLevel);
                Engine.FoodProvenance.StripFoodMarks(taken);
                content.Itemstack = taken;
                content.MarkDirty();
                if (world.Side == EnumAppSide.Server)
                    TcmLog.Cat(world.Api, "far", $"trough fill accepted marked feed (mark stripped): {byPlayer!.PlayerName}, {taken.Collectible?.Code}");
                __result = true;
                return false;
            }
            __result = false;
            return false;
        }

        if (cleanHand.Equals(world, nonEmpty[0], Vintagestory.API.Config.GlobalConstants.IgnoredStackAttributes)
            && hand.StackSize >= cfg.QuantityPerFillLevel
            && nonEmpty[0].StackSize < cfg.QuantityPerFillLevel * cfg.MaxFillLevels)
        {
            hand.TakeOut(cfg.QuantityPerFillLevel);
            content.Itemstack!.StackSize += cfg.QuantityPerFillLevel;
            content.MarkDirty();
            __result = true;
            return false;
        }
        __result = false;
        return false;
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
    public static void TroughConsumePostfix(BlockEntity __instance, Entity entity, ref float __result)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || entity == null) return;
        // Phase 2 feed economy (the MET fuel analog): the satiety an animal draws per portion
        // scales with the FILLER's rank, so a master's trough feeds to the same satiety on
        // fewer portions and an Untrained hand's feed partly goes to waste.
        if (troughOwners.TryGetValue(PosKey(__instance.Pos), out string? feedUid) && feedUid != null)
        {
            IPlayer? filler = __instance.Api.World.PlayerByUid(feedUid);
            if (filler != null)
                __result *= (float)FarDomain.RankLinear(FarDomain.LevelOf(filler),
                    FarDomain.Knob(FarDomain.FeedUntrained, 0.90), FarDomain.Knob(FarDomain.FeedGm, 1.25));
        }
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
        AniDomain.StampProvenance(entity, owner); // the Master's Line mark, upgrade-only by the tender's ANI tier
        TcmLog.Cat(__instance.Api, "far", $"trough portion eaten by {entity.Code?.FirstCodePart()} #{entity.EntityId} -> feeding credit + raisedBy stamp for {owner.PlayerName}");
        Core?.Ledger?.Log(owner, FarDomain.Code, FarDomain.TechFeeding,
            HashCode.Combine("feed", __instance.Pos.X, __instance.Pos.Z, __instance.Api.World.ElapsedMilliseconds / 60000));
    }

    // ------------------------------------------------------------ shearing (shearlib)

    /// <summary>DoShear fires on a shear; Phase 2 adds the damaging-shear = zero XP gate. For now
    /// a shear banks FAR shearing, regrowth-gated by the animal itself (you cannot reshear at will).</summary>
    /// <summary>A shear is in progress, and whether it wounded. Shears run synchronously on the
    /// server main thread (player interaction), so a single static pair is safe — the same
    /// reasoning as the ANI breeder context.</summary>
    private static bool shearInProgress;
    private static bool shearWounded;

    /// <summary>Start the shear watch and, for an Untrained hand, raise shearlib's own
    /// scratchChance so the beginner wounds the animal more often (the ruled penalty). Captured
    /// original is restored in the postfix.</summary>
    public static void ShearPrefix(EntityBehavior __instance, EntityAgent byEntity, out double __state)
    {
        __state = -1;
        shearInProgress = true;
        shearWounded = false;
        IPlayer? player = PlayerOf(byEntity);
        if (player == null || __instance == null || BehaviorEntity(__instance)?.World?.Side != EnumAppSide.Server) return;
        if (FarDomain.LevelOf(player) > 0) return; // penalty is Untrained-only

        var f = Traverse.Create(__instance).Field("scratchChance");
        if (!f.FieldExists()) return;
        __state = f.GetValue<double>();
        f.SetValue(__state * FarDomain.Knob(FarDomain.ShearScratchUntrained, 1.5));
    }

    /// <summary>Fires only when DoShear actually wounds (:346). Marks the in-flight shear.</summary>
    public static void ShearWoundedPostfix()
    {
        if (shearInProgress) shearWounded = true;
    }

    public static void ShearPostfix(EntityBehavior __instance, EntityAgent byEntity, double __state)
    {
        shearInProgress = false;
        if (__state >= 0 && __instance != null) // restore the widened scratchChance
            Traverse.Create(__instance).Field("scratchChance").SetValue(__state);

        IPlayer? player = PlayerOf(byEntity);
        Entity? animal = __instance == null ? null : BehaviorEntity(__instance);
        if (player == null || animal?.World?.Side != EnumAppSide.Server) return;
        if (shearWounded)
        {
            // FAR ruling 4 (Phase 2): a damaging shear yields 1/3 wool and wounds the animal —
            // the outcome is the practice signal, and this outcome is a botch. Zero XP.
            TcmLog.Cat(animal.World.Api, "far", $"damaging shear on {animal.Code?.FirstCodePart()} #{animal.EntityId} by {player.PlayerName}: wounded, no practice");
            return;
        }
        Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechShearing,
            HashCode.Combine("shear", animal.EntityId, animal.World.ElapsedMilliseconds / 60000));
    }
}

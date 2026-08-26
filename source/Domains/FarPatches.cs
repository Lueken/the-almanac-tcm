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

    /// <summary>Cutting pos -> the SCION's familiarity id, stored beside the owner and for the
    /// same reason: the take fires on an unattended tick where neither the player nor the stack
    /// is in scope. On a graft onto existing rootstock the species learned is the scion's,
    /// because the scion is what will fruit, and that is the same string vanilla reads at
    /// BlockFruitTreeBranch.cs:118 to decide the graft.
    ///
    /// Its own savegame key rather than a widened value, so an existing world loads with the
    /// owner map intact and only the scion map empty. A cutting already in the ground when this
    /// shipped still pays its grafting practice; what it cannot do is open a tree's page, which
    /// is the right way round for a gate.</summary>
    private static Dictionary<string, string> graftScions = new();

    /// <summary>Bush cutting pos -> planter uid. The bush half of the same problem the graft
    /// maps solve: a cutting roots on an unattended tick months later, where nobody is in scope.
    ///
    /// Its own map rather than a share of graftOwners, because the two have different lifetimes.
    /// A graft entry deliberately SURVIVES a death when a ranked owner's stock clings to life
    /// and vanilla re-rolls; a bush cutting has no such retry and its entry is claimed once and
    /// dropped. Folding them together would put two lifecycles behind one key.</summary>
    private static Dictionary<string, string> bushPlanters = new();

    private static void LoadGraftOwners()
    {
        try
        {
            byte[]? data = sapi!.WorldManager.SaveGame.GetData("almanacFarGraftOwners");
            if (data != null)
                graftOwners = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
            byte[]? scions = sapi!.WorldManager.SaveGame.GetData("almanacFarGraftScions");
            if (scions != null)
                graftScions = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(scions) ?? new();
            byte[]? bushes = sapi!.WorldManager.SaveGame.GetData("almanacFarBushPlanters");
            if (bushes != null)
                bushPlanters = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(bushes) ?? new();
            TcmLog.Cat(sapi, TcmLog.Config, $"FAR graft owners loaded: {graftOwners.Count} pending cutting(s), {graftScions.Count} with a known scion; {bushPlanters.Count} bush cutting(s) rooting");
        }
        catch (Exception e) { TcmLog.Error(sapi, $"graft owner map unreadable ({e.Message}); starting empty"); }
    }

    private static void SaveGraftOwners()
    {
        sapi!.WorldManager.SaveGame.StoreData("almanacFarGraftOwners",
            Vintagestory.API.Util.SerializerUtil.Serialize(graftOwners));
        sapi!.WorldManager.SaveGame.StoreData("almanacFarGraftScions",
            Vintagestory.API.Util.SerializerUtil.Serialize(graftScions));
        sapi!.WorldManager.SaveGame.StoreData("almanacFarBushPlanters",
            Vintagestory.API.Util.SerializerUtil.Serialize(bushPlanters));
    }

    // ------------------------------------------------------------ seam wiring

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        Hook(api, harmony, "Vintagestory.GameContent.ItemHoe", "DoTill", nameof(TillPostfix), "FAR tilling");

        // Biofumigation (soil-stabilisers scope 2026-08-24). A hoe held on a ripe brassica turns
        // the plant into the ground instead of harvesting it, and the sickness on that tile goes
        // with it. Three seams, because vanilla's hoe declines the swing before it ever reaches
        // DoTill:
        //   1. BlockCrop stands aside, so a crop wearing the Harvestable behaviour does not eat
        //      the click before the held item is offered one. Eruca is the brassica this matters
        //      for today (far-lifecycle-harvestable.json).
        //   2. OnHeldInteractStart claims the swing, which vanilla only does for a block coded
        //      "soil" (ItemHoe.cs:70). Without this the animation never starts and DoTill is
        //      never called.
        //   3. DoTill itself is intercepted, since vanilla's own body returns immediately on
        //      anything that is not soil.
        HookPrefixDeclared(api, harmony, "Vintagestory.GameContent.BlockCrop", "OnBlockInteractStart",
            nameof(CropStandAsidePrefix), "FAR biofumigation (crop stands aside)");
        HookDeclared(api, harmony, "Vintagestory.GameContent.ItemHoe", "OnHeldInteractStart",
            nameof(HoeStartPostfix), "FAR biofumigation (hoe claims the swing)");
        HookPrefixDeclared(api, harmony, "Vintagestory.GameContent.ItemHoe", "DoTill",
            nameof(HoeTurnInPrefix), "FAR biofumigation (turn-in)");
        Hook(api, harmony, "Vintagestory.GameContent.BlockEntityFarmland", "TryPlant", nameof(PlantPostfix), "FAR planting");
        // Harvest: the Phase 2 Untrained dock rides a prefix on the same declared override
        // (dropQuantityMultiplier is passed by ref into the drop roll).
        HookPairDeclared(api, harmony, "Vintagestory.GameContent.BlockCrop", "OnBlockBroken",
            nameof(HarvestDockPrefix), nameof(HarvestPostfix), "FAR harvesting");
        // The cut-and-come-again pick (found 2026-08-24). far-lifecycle-harvestable.json hangs
        // vanilla's Harvestable behaviour on seven cultivated crops so a ripe plant can be picked
        // by hand and grow back. That behaviour hands the drops over ITSELF and then SetBlocks the
        // regrown stage (BehaviorHarvestable.cs:188-225). The block is never broken, so
        // BlockCrop.OnBlockBroken and every FAR seam docked to it are skipped end to end. The pick
        // was therefore falling through to FOR's own postfix on this same method: a sown bed paid
        // Foraging, taught no crop familiarity, and left the ground untired. Pair the seam here so
        // a pick is farming; ForPatches stands down on any block FAR claims as a crop.
        // Trio, not a pair: the restore rides a FINALIZER so borrowed drop quantities go back even
        // when the original throws. The behaviour instance is shared by every plant of the crop in
        // the world, so a stranded haircut would tax the whole species for the session.
        HookTrioDeclared(api, harmony, "Vintagestory.GameContent.BlockBehaviorHarvestable", "OnBlockInteractStop",
            nameof(PickPrefix), nameof(PickPostfix), nameof(PickRestoreFinalizer), "FAR cut-and-come-again pick");
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

            // Biofumigation through the PS hoe. DoTill is an OVERRIDE, so the prefix on the
            // vanilla method above never runs on a PS-hoed field and the turn-in has to be bound
            // here too. OnHeldInteractStart is a different case: PS may or may not override it,
            // and INHERITING it is the normal, working outcome, so an absent declaration is
            // reported as information rather than warned about.
            HookPrefixDeclared(api, harmony, "PrimitiveSurvival.ModSystem.ItemHoeExtended", "DoTill",
                nameof(HoeTurnInPrefix), "FAR biofumigation (PS hoe turn-in)");
            HookDeclaredOptional(api, harmony, "PrimitiveSurvival.ModSystem.ItemHoeExtended", "OnHeldInteractStart",
                nameof(HoeStartPostfix), "FAR biofumigation (PS hoe claims the swing)");

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

        // The bush half of the same verb, and the same owner-at-action, bank-at-unattended-event
        // shape. Rooting has no take-or-die roll: OnGrownFromCutting is called only when a
        // cutting has matured, so arriving there IS the success. DECLARED-strict on both, so a
        // future signature move warns instead of silently binding something else. The taking
        // half lives in ForPatches beside the other BEBehaviorFruitingBush seams.
        HookDeclared(api, harmony, "Vintagestory.GameContent.BlockBehaviorFruitingBushCutting", "CanPlaceBlock",
            nameof(BushCuttingPlacePostfix), "FAR bush cutting placement");
        HookDeclared(api, harmony, "Vintagestory.GameContent.BEBehaviorFruitingBush", "OnGrownFromCutting",
            nameof(BushRootedPostfix), "FAR bush rooting outcome");

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

    /// <summary>HookDeclared for a seam where NOT declaring the method is the normal case: a
    /// subclass that inherits the base implementation is already covered by the base patch, so
    /// silence there is success and a warning would be noise that trains people to ignore the
    /// real ones.</summary>
    private static void HookDeclaredOptional(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Info(api, $"{label}: {typeName} does not override {method}; the base hook covers it"); return; }
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

    /// <summary>HookPairDeclared plus a finalizer, for a seam that BORROWS shared state and must
    /// give it back even when the original throws. A postfix is skipped on a throw; a finalizer
    /// is not.</summary>
    private static void HookTrioDeclared(ICoreAPI api, Harmony harmony, string typeName, string method,
        string prefix, string postfix, string finalizer, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that verb is inactive this build"); return; }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), postfix)),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(FarPatches), finalizer)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared, with restore finalizer)");
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

        if (!TillWorthCrediting(byEntity!.World, blockSel.Position)) return;

        Core?.Ledger?.Log(player, FarDomain.Code, FarDomain.TechTilling,
            HashCode.Combine(blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z));
    }

    /// <summary>
    /// OUTCOME, NOT ATTEMPT. Vanilla's DoTill returns without doing anything on a block that is
    /// not soil, and a postfix runs regardless, so this seam used to bank tilling for any hoe
    /// swing that reached the method. That was harmless only because vanilla declines the swing
    /// outright on anything but soil (ItemHoe.cs:70) — a guarantee biofumigation deliberately
    /// breaks, since a turn-in has to reach DoTill to be intercepted.
    ///
    /// Three ways a swing can reach DoTill, and each is answered here:
    ///
    ///  - SOIL becomes farmland. The block entity lands at the struck position. Credit.
    ///  - A FALLOW block is cleared, if Involved Farming is loaded. Its block is deliberately
    ///    coded `soilfallow` so vanilla's own PathStartsWith("soil") test lets the hoe swing on
    ///    it, and it sits in the CROP slot, so the farmland is one below and the struck position
    ///    is left empty. That is real tilling labour and, under that mod, a mandatory step before
    ///    every sowing, so it credits. Without the mod this branch simply never fires.
    ///  - A BIOFUMIGATION TURN-IN, which also leaves the struck position empty over farmland. It
    ///    DOES pay the tilling verb (RULED 2026-08-25), but it banks that credit itself inside
    ///    FarBiofumigation.TurnIn, with its own context key, so this seam must stand down or the
    ///    same swing would pay twice. Excluded by the flag rather than by geometry, because
    ///    geometry cannot tell a turn-in from clearing fallow.
    ///
    /// Nothing else reaches the method. A hoe on a standing crop that is not a turn-in candidate,
    /// or on farmland already tilled, never gets past OnHeldInteractStart at all.
    /// </summary>
    private static bool TillWorthCrediting(IWorldAccessor world, BlockPos pos)
    {
        if (turnedInThisSwing) { turnedInThisSwing = false; return false; }
        if (world.BlockAccessor.GetBlockEntity(pos) is Vintagestory.GameContent.BlockEntityFarmland) return true;
        return world.BlockAccessor.GetBlockEntity(pos.DownCopy()) is Vintagestory.GameContent.BlockEntityFarmland
            && world.BlockAccessor.GetBlock(pos)?.BlockId == 0;
    }

    /// <summary>Set by the turn-in prefix and consumed by whichever till postfix runs next. A
    /// single static is safe here for the same reason the shear pair is: DoTill is dispatched
    /// from the held-interaction step on the server main thread, synchronously, one swing at a
    /// time. The postfix always runs even when the prefix skipped the original, so it is always
    /// cleared.</summary>
    private static bool turnedInThisSwing;

    // ------------------------------------------------------------ biofumigation (the hoe turn-in)

    /// <summary>
    /// The crop declines the click so the hoe in hand can have it. Returns __result false and
    /// skips the original, which is exactly what the original would have returned for a hoe on a
    /// plain crop anyway — the difference is that it also skips the block BEHAVIOURS underneath,
    /// and Harvestable is one of them. Without this a ripe eruca would be picked by the
    /// cut-and-come-again seam before the hoe was ever offered the interaction.
    ///
    /// Deliberately narrow: only a hoe, only a crop biofumigation would actually accept, only on
    /// farmland. Anything else and vanilla runs untouched.
    /// </summary>
    public static bool CropStandAsidePrefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        if (world == null || byPlayer == null || blockSel == null) return true;
        if (!FarBiofumigation.TurnInIntent(byPlayer.Entity)) return true;
        if (!FarBiofumigation.HoeInHand(byPlayer)) return true;
        if (!FarBiofumigation.IsCandidate(world.Api, world.BlockAccessor.GetBlock(blockSel.Position))) return true;
        if (FarBiofumigation.FarmlandUnder(world, blockSel.Position) == null) return true;

        __result = false;
        return false;
    }

    /// <summary>
    /// Claims the swing. Vanilla sets PreventDefault only for a block coded "soil", so without
    /// this the hoe animation never starts on a crop and DoTill is never reached. Runs on both
    /// sides, as held interactions do, so the client plays the swing the server is going to act
    /// on. The "didtill" latch is re-armed for the same reason vanilla arms it: the step handler
    /// reads it to fire DoTill exactly once per swing.
    /// </summary>
    public static void HoeStartPostfix(EntityAgent byEntity, BlockSelection blockSel, ref EnumHandHandling handHandling)
    {
        if (byEntity == null || blockSel == null) return;
        if (handHandling == EnumHandHandling.PreventDefault) return;   // vanilla already took it
        if (!FarBiofumigation.TurnInIntent(byEntity)) return;

        var world = byEntity.World;
        if (world == null) return;
        if (!FarBiofumigation.IsCandidate(world.Api, world.BlockAccessor.GetBlock(blockSel.Position))) return;
        if (FarBiofumigation.FarmlandUnder(world, blockSel.Position) == null) return;
        if (!FarBiofumigation.HoeInHand(PlayerOf(byEntity))) return;

        byEntity.Attributes.SetInt("didtill", 0);
        handHandling = EnumHandHandling.PreventDefault;
    }

    /// <summary>
    /// The turn-in. Returning false skips vanilla's till, which on a crop block would have done
    /// nothing at all; returning true on anything this is not means every ordinary till runs
    /// exactly as before.
    ///
    /// Bound to the vanilla method AND to Primitive Survival's override, because DoTill is
    /// virtual and a prefix on the base never fires for a subclass that overrides it.
    /// </summary>
    public static bool HoeTurnInPrefix(EntityAgent byEntity, BlockSelection blockSel)
    {
        if (byEntity == null || blockSel == null || !ServerSide(byEntity.World)) return true;
        if (byEntity.World.Api is not ICoreServerAPI sapi) return true;
        IPlayer? player = PlayerOf(byEntity);
        if (player == null) return true;

        // Flagged for the till postfix, which runs whether or not this skips the original and
        // otherwise cannot tell a turn-in from clearing a fallow block: both leave the struck
        // position empty over farmland.
        bool turnedIn = FarBiofumigation.TurnIn(sapi, player, blockSel.Position);
        if (turnedIn) turnedInThisSwing = true;
        return !turnedIn;
    }

    /// <summary>The PS hoe override, both branches: after the till, the block on the ground says
    /// which work was done — a furrow channel banks the furrow verb, farmland banks tilling.</summary>
    public static void TillExtendedPostfix(EntityAgent byEntity, BlockSelection blockSel)
    {
        IPlayer? player = PlayerOf(byEntity);
        if (player == null || blockSel == null || !ServerSide(byEntity?.World)) return;
        Block? now = byEntity!.World.BlockAccessor.GetBlock(blockSel.Position);
        bool furrow = now?.Code?.Path?.StartsWith("furrowedland") == true;
        // Outcome, not attempt — see TillWorthCrediting. A furrow is its own verb and settles
        // here; anything else has to have actually tilled something.
        if (furrow) turnedInThisSwing = false;
        else if (!TillWorthCrediting(byEntity.World, blockSel.Position)) return;
        string tech = furrow ? FarDomain.TechFurrow : FarDomain.TechTilling;
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
    public static void GraftPlacePostfix(Block __instance, IWorldAccessor world, IPlayer byPlayer,
        ItemStack itemstack, BlockSelection blockSel, bool __result)
    {
        if (!__result || byPlayer == null || blockSel == null || !ServerSide(world)) return;
        string key = PosKey(blockSel.Position);
        graftOwners[key] = byPlayer.PlayerUID;

        // The scion, captured here because it is the only point where the stack exists. Harmony
        // binds `itemstack` by parameter name off TryPlaceBlock's own signature, so this is a
        // signature addition rather than new machinery.
        string? scion = itemstack?.Attributes?.GetString("type");
        string scionId = FarPerennials.TreeId(__instance?.Code?.Domain, scion);
        if (scionId.Length > 0) graftScions[key] = scionId;

        TcmLog.Cat(world.Api, "far", $"cutting placed at {blockSel.Position} by {byPlayer.PlayerName}" +
            $"{(scionId.Length > 0 ? $" (scion {scionId})" : " (scion unknown)")}; take-or-die pending (silent until the outcome)");
    }

    // ---------------------------------------------------- bush cuttings: rooting (the outcome)

    /// <summary>
    /// The planter of a berry bush cutting, stored the same way and for the same reason as a
    /// graft's: rooting happens on an unattended tick two to four months later, where neither
    /// the player nor the stack is in scope.
    ///
    /// CanPlaceBlock is a permission check rather than a confirmed placement, and that is a
    /// deliberate trade. The alternatives were worse: OnGrownFromCutting has no player,
    /// BEBehaviorFruitingBushCutting.OnBlockPlaced takes only an ItemStack, and a postfix on
    /// Block.TryPlaceBlock would fire for every block placed in the game to catch one. This
    /// method exists ONLY on the cutting block, so the patch surface is exactly the feature.
    ///
    /// What the trade costs, stated plainly: a check that never became a placement leaves an
    /// entry nothing will ever claim, because OnGrownFromCutting only fires where a cutting
    /// actually grew. Last writer wins, so if two players check the same spot the one who
    /// placed is the one credited. The only way to mis-credit is for something other than a
    /// player placement to put a cutting at a position somebody checked, which nothing in the
    /// game does.
    /// </summary>
    public static void BushCuttingPlacePostfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, bool __result)
    {
        if (!__result || byPlayer == null || blockSel?.Position == null || !ServerSide(world)) return;
        bushPlanters[PosKey(blockSel.Position)] = byPlayer.PlayerUID;
        TcmLog.Cat(world.Api, "far", $"bush cutting set at {blockSel.Position} by {byPlayer.PlayerName}; rooting pending");
    }

    /// <summary>
    /// The verdict. Unlike a graft there is no take-or-die roll: vanilla calls this only when a
    /// cutting has matured into a bush, so reaching here IS the success and there is no death
    /// branch to filter. It is also the exact moment the bush becomes cultivated, since this is
    /// where vanilla clears WildBushState.
    ///
    /// Paid as grafting rather than as its own verb: rooting a bush and taking a graft are the
    /// same trade skill applied to two plants, and splitting them would say otherwise. Taking
    /// the cutting was the cheaper, riskless half and has its own verb over in ForPatches.
    ///
    /// No familiarity here. The bush ladder is paid on cuttings TAKEN off a raised bush, and
    /// this is the act that first makes such a bush exist. Paying a mark here as well would
    /// credit one propagation cycle twice.
    /// </summary>
    public static void BushRootedPostfix(Vintagestory.GameContent.BEBehaviorFruitingBush __instance)
    {
        var api = __instance?.Api;
        if (api == null || api.Side != EnumAppSide.Server) return;
        var pos = __instance!.Pos;
        if (pos == null) return;

        string key = PosKey(pos);
        if (!bushPlanters.TryGetValue(key, out string? uid)) return;
        bushPlanters.Remove(key);

        IPlayer? planter = api.World.PlayerByUid(uid);
        if (planter == null)
        {
            TcmLog.Cat(api, "far", $"bush cutting ROOTED at {pos} but the planter is unknown or offline; uncredited");
            return;
        }
        Core?.Ledger?.Log(planter, FarDomain.Code, FarDomain.TechGrafting,
            HashCode.Combine("bushroot", pos.X, pos.Y, pos.Z));
        TcmLog.Cat(api, "far", $"bush cutting ROOTED at {pos} -> grafting credit for {planter.PlayerName}");
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
            graftScions.TryGetValue(key, out string? scionId);
            graftOwners.Remove(key);
            graftScions.Remove(key);
            IPlayer? owner = uid == null ? null : be.Api.World.PlayerByUid(uid);
            if (owner == null)
            {
                TcmLog.Cat(be.Api, "far", $"cutting TOOK at {__state.Pos} but the placer is unknown or offline; uncredited");
                return;
            }
            TcmLog.Cat(be.Api, "far", $"cutting TOOK at {__state.Pos} -> grafting credit for {owner.PlayerName}");
            Core?.Ledger?.Log(owner, FarDomain.Code, FarDomain.TechGrafting,
                HashCode.Combine("graft", __state.Pos.X, __state.Pos.Y, __state.Pos.Z));

            // The tree's FIRST familiarity mark, and the only act that can pay it. Picking a
            // ripe branch is worth nothing on a species whose counter still stands at zero, so
            // this is the gate: an orchard is learned by planting it, never by finding it.
            // Vanilla worldgen scatters every tree type across the map, so without this a wild
            // grove would carry a player to Versed having never put anything in the ground,
            // which no sown crop can do because no wheat field regrows on its own.
            if (scionId != null && be.Api is ICoreServerAPI graftApi)
            {
                FarFamiliarity.BumpHarvest(graftApi, owner, scionId);
                TcmLog.Cat(be.Api, "far", $"{scionId} opens for {owner.PlayerName}: a rooted cutting is what begins a tree's page");
            }
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
            graftScions.Remove(key);
            TcmLog.Cat(be.Api, "far", $"cutting DIED at {__state.Pos}; no practice and no page (success-gated by ruling)");
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

        // Close the fallow span before the practice credit, because it is the tile's business
        // rather than the player's: the ground has just stopped being bare and is owed the
        // bare-ground decay rate for every day it was.
        if (__instance.Api is ICoreServerAPI sickApi)
            FarSoilSickness.NotePlanted(sickApi, __instance.Pos);

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
    public static void HarvestDockPrefix(Block __instance, BlockPos pos, IPlayer byPlayer, ref float dropQuantityMultiplier)
    {
        if (byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;
        int level = FarDomain.LevelOf(byPlayer);

        // The soil's haircut rides ahead of the hand's, because they answer different
        // questions: the table asks how well this farmer harvests, sickness asks what this
        // ground had left to give. Both multiply; neither fights.
        if (pos != null && byPlayer.Entity.World.Api is ICoreServerAPI sickApi)
        {
            string? sickId = FarFamiliarity.CropIdOf(sickApi, __instance);
            string? sickFam = sickId == null ? null : FarFamiliarity.FamilyOf(sickId);
            if (sickFam != null)
            {
                double sick = FarSoilSickness.LevelFor(sickApi, pos.DownCopy(), sickFam);
                if (sick > 0) dropQuantityMultiplier *= (float)FarSoilSickness.YieldMul(sick);
            }
        }

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

                // Soil sickness accrues on the GROUND, not on this block entity, so breaking and
                // replacing the tilled block cannot wipe it (RULED 2026-08-24). See FarSoilSickness.
                string? family = FarFamiliarity.FamilyOf(cropId);
                if (family != null) FarSoilSickness.NoteHarvest(sapi, farmlandBe.Pos, family);
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

    // ------------------------------------------------------------ cut-and-come-again pick

    /// <summary>What a pick prefix hands its postfix: the crop this behaviour belongs to, its
    /// family, and the drop quantities the haircut borrowed so they can be put back exactly as
    /// found. A null CropId means this is not a FAR crop and the postfix has nothing to do.</summary>
    public readonly record struct PickState(string? CropId, string? Family, NatFloat?[]? Borrowed);

    /// <summary>
    /// The soil's haircut on a pick. Vanilla computes its drop rate as a LOCAL and passes it
    /// straight to GetNextItemStack (BehaviorHarvestable.cs:190-199), so there is no by-ref
    /// multiplier to ride the way BlockCrop.OnBlockBroken offers one. Scaling avg and var on each
    /// drop's NatFloat is the same arithmetic by another door: nextFloat returns
    /// offset + multiplier * (avg + rnd * 2 * var) (NatFloat.cs:247-262), and these drop entries
    /// declare no offset. The quantities are BORROWED for the length of one call and handed back
    /// by the FINALIZER, because a BlockBehavior instance is shared by every placement of that
    /// block: a haircut left behind would tax the whole field forever. A postfix was not enough,
    /// and that is the whole reason the finalizer exists. See PickRestoreFinalizer.
    ///
    /// BOTH HANDS RIDE HERE (RULED 2026-08-25). The per-rank yield table now applies to a pick as
    /// well as to a break. It was left off while the question was open, which meant a Master
    /// picking cabbage took a rank-blind harvest while breaking the same plant paid the full
    /// table: the same crop, the same farmer, two different answers depending on which button
    /// they pressed. The two multipliers compose exactly as they do on the break seam, sickness
    /// first because it asks what the ground had left to give and the table asks how well this
    /// farmer takes it.
    /// </summary>
    /// <summary>
    /// How much of the plant's life a pick just took, as a share of the whole ladder. A chive
    /// ripens at stage 6 and regrows from stage 4, so a pick costs two of six and returns 1/3.
    ///
    /// Read from the two blocks themselves, before and after, rather than from the behaviour's
    /// private harvestedBlockCode. That keeps it free of reflection AND correct when another mod
    /// redirects the regrowth: whatever is standing there now IS the answer.
    ///
    /// Falls back to a full share whenever the ladder cannot be read, which is the conservative
    /// direction: an unreadable crop tires the ground exactly as it did before this existed,
    /// rather than quietly becoming free to farm.
    /// </summary>
    private static double LifeTakenByPick(Block? ripe, Block? regrown)
    {
        int stages = ripe?.CropProps?.GrowthStages ?? 0;
        if (stages <= 0 || regrown == null) return 1.0;
        if (!int.TryParse(ripe!.LastCodePart(), out int from)) return 1.0;
        if (!int.TryParse(regrown.LastCodePart(), out int to)) return 1.0;
        int taken = from - to;
        if (taken <= 0) return 1.0;
        return Math.Min(1.0, taken / (double)stages);
    }

    /// <summary>
    /// Hands the borrowed drop quantities back, whatever happened inside.
    ///
    /// WHY A FINALIZER AND NOT THE POSTFIX. A Harmony postfix does not run when the original
    /// throws; a finalizer does. The object being mutated here is the BlockBehavior INSTANCE,
    /// which is shared by every plant of that crop in the entire world and outlives the call by
    /// the length of the session. So a single exception anywhere inside
    /// BehaviorHarvestable.OnBlockInteractStop, in vanilla or in any other mod patching the same
    /// method, would strand a sickness-and-rank haircut on the whole species permanently, and it
    /// would look like a balance decision rather than a bug. Precedent in this codebase:
    /// AlcBrandPatches.RestoreFinalizer, added for the same class of failure.
    ///
    /// Deliberately total: no side test, no null-instance shortcut beyond what it takes not to
    /// throw, and it swallows nothing (returning void from a finalizer leaves any exception to
    /// propagate exactly as it would have).
    /// </summary>
    public static void PickRestoreFinalizer(Vintagestory.GameContent.BlockBehaviorHarvestable __instance,
        PickState __state)
    {
        if (__state.Borrowed == null || __instance?.harvestedStacks == null) return;
        var stacks = __instance.harvestedStacks;
        int n = Math.Min(stacks.Length, __state.Borrowed.Length);
        for (int i = 0; i < n; i++)
            if (__state.Borrowed[i] != null && stacks[i] != null) stacks[i]!.Quantity = __state.Borrowed[i]!;
    }

    public static void PickPrefix(Vintagestory.GameContent.BlockBehaviorHarvestable __instance,
        IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, out PickState __state)
    {
        __state = default;
        if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
        if (world.Api is not ICoreServerAPI sapi) return;

        // The owner test. A wild harvestable (resin, reeds, herbarium) carries no crop id and is
        // left entirely to FOR; only something crop-families.json names is farming.
        string? cropId = FarFamiliarity.CropIdOf(sapi, __instance.block);
        if (cropId == null) return;
        string? family = FarFamiliarity.FamilyOf(cropId);
        __state = new PickState(cropId, family, null);

        if (__instance.harvestedStacks == null) return;

        // The ground's answer. A crop with no family (nothing in the taxonomy) simply skips it.
        float haircut = 1f;
        if (family != null)
        {
            double sick = FarSoilSickness.LevelFor(sapi, blockSel.Position.DownCopy(), family);
            if (sick > 0) haircut *= (float)FarSoilSickness.YieldMul(sick);
        }

        // The hand's answer, the same ladder the break seam reads: the per-crop per-rank table
        // when the crop has a row, the legacy Untrained dock when it does not.
        int level = FarDomain.LevelOf(byPlayer);
        double? tabled = FarYieldTable.MultiplierFor(sapi, __instance.block, level);
        if (tabled != null) haircut *= (float)tabled.Value;
        else if (level <= 0) haircut *= (float)FarDomain.Knob(FarDomain.HarvestDockUntrained, 0.85);

        if (haircut >= 0.9999f) return;   // nothing to borrow, so nothing to restore

        var stacks = __instance.harvestedStacks;
        var borrowed = new NatFloat?[stacks.Length];
        for (int i = 0; i < stacks.Length; i++)
        {
            NatFloat? original = stacks[i]?.Quantity;
            if (original == null) continue;
            borrowed[i] = original;
            NatFloat cut = original.Clone();
            cut.avg *= haircut;
            cut.var *= haircut;
            stacks[i]!.Quantity = cut;
        }
        __state = new PickState(cropId, family, borrowed);
    }

    /// <summary>
    /// The pick, credited as the harvest verb it is. Success is read as the block TRANSITION the
    /// pick itself performs, the ripe stage being replaced by the regrown one at this position,
    /// for the same reason the fruiting-bush seam does it: it sidesteps the behaviour's private
    /// harvest-time arithmetic entirely, so a released-too-early pick banks nothing without this
    /// method having to re-derive vanilla's guard. A Harvestable with no harvestedBlockCode never
    /// changes the block and so never credits, which is correct: with nothing to grow back, it is
    /// not a cut-and-come-again crop.
    /// </summary>
    public static void PickPostfix(Vintagestory.GameContent.BlockBehaviorHarvestable __instance,
        IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, PickState __state)
    {
        // The restore is NOT here. It is in PickRestoreFinalizer, which runs even when the
        // original throws; a postfix does not.
        if (__state.CropId == null || byPlayer == null || blockSel == null) return;
        if (world?.Side != EnumAppSide.Server || world.Api is not ICoreServerAPI sapi) return;
        if (world.BlockAccessor.GetBlock(blockSel.Position)?.BlockId == __instance?.block?.BlockId) return;

        // A pick is always taken at the ripe stage (the behaviour is only attached to that stage)
        // and always by hand, so it banks what a ripe hand-pulled break banks: no ripeness scaling
        // to apply, no scythe premium to earn. Context is exact position plus the 1s bucket the
        // break seam uses. Two picks of one plant inside a second cannot happen because the first
        // one takes the ripe stage away, and the same plant picked again after regrowth pays again.
        Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechHarvesting,
            HashCode.Combine("pick", blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z,
                world.ElapsedMilliseconds / 1000));

        // Familiarity and soil sickness both carry their OWN once-per-in-game-day cap inside
        // BumpHarvest and NoteHarvest, so a bed picked four times in an afternoon is still the one
        // crop standing in that ground. Nothing extra is needed at this seam to hold that line.
        FarFamiliarity.BumpHarvest(sapi, byPlayer, __state.CropId);

        // The farmland's rotation memory (LastBore) is deliberately NOT stamped here. It records
        // what the ground last bore once the crop is GONE, and after a pick the plant is still
        // standing; the eventual break writes it through HarvestPostfix.
        if (__state.Family != null
            && world.BlockAccessor.GetBlockEntity(blockSel.Position.DownCopy()) is Vintagestory.GameContent.BlockEntityFarmland farmland)
            FarSoilSickness.NoteHarvest(sapi, farmland.Pos, __state.Family,
                LifeTakenByPick(__instance?.block, world.BlockAccessor.GetBlock(blockSel.Position)));
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

        // Familiarity, but only on a species the picker has already rooted or grafted for
        // themselves. The counter standing at zero IS the gate, so this costs no extra storage:
        // GraftGrowPostfix is the only thing that can move a tree id off zero, and everything
        // after that is the ordinary one-mark-a-day rule.
        //
        // Practice above is unconditional and stays that way. Picking a wild grove is real work
        // and pays as such; what it is not is knowledge of how to grow the thing.
        string? id = FarPerennials.TreeIdOf(__instance as Vintagestory.GameContent.BlockEntityFruitTreePart);
        if (id == null || __instance.Api is not ICoreServerAPI sapi) return;
        var know = FarFamiliarity.KnowledgeOf(__instance.Api, byPlayer);
        if (FarFamiliarity.OwnCount(know, id) <= 0) return;
        FarFamiliarity.BumpHarvest(sapi, byPlayer, id);
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

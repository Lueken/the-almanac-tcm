using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// ALC — the verbs that GRANT rank and MINT the Alchemist's Brand (rank-bonus-design.md §ALC;
/// AMENDMENT 2026-07-22 for alchemy 2.1.11 / industrialstory 0.7.1). Three layers:
///
///   • Remedy crafting [vanilla, always] — the pure-vanilla floor. Grid-crafting a poultice/bandage
///     (any CollectibleBehaviorHealingItem output) grants ALC at the real take (GridRecipe.ConsumeInput,
///     the PF craft-XP seam) and stamps the maker's Brand on the output batch. The real crafted stack is
///     captured at OnCreatedByCrafting (which runs first in CraftSingle) and branded at ConsumeInput
///     (where the crafter is finally in scope). Emphasis is the crafter's book choice (AlcEmphasis).
///
///   • Potion cauldron cook [alchemy, conditional] — the re-pointed potion seam (2.1.11 moved potions
///     barrel-seal -> cauldron-cook). Owner captured at the cauldron interact (owner-at-tend, persisted),
///     grant + Brand stamp at the unattended firepit completion (BlockEntityFirepit.smeltItems, filtered
///     to potion output). Emphasis is the tender's book choice, frozen at tend time. POT pit-kiln model:
///     grant if the tender is online, else the credit is lost but the Brand still stamps.
///
///   • Wet chemistry [industrialstory, conditional] — the deep metal-gated breadth verb + the Axis 2
///     fuel economy. Grant at the standalone reaction-vessel completion (owner-at-charge); reaction fuel
///     economy on the host stove's burn tick (owner-at-reaction-interact), guarded to REACTION burns so
///     it never touches COO/MET cooking on the shared stove. NOTE: industrialstory 0.7.1 restructured the
///     apparatus (stove-hosted vessels); every seam here is isolated (warns-and-skips), so a moved path
///     simply logs inactive and is refined at playtest — it never breaks patch load.
///
///   • Herb-rack drying [alchemy, conditional] — the alchemy mod's rack is the COO #9 drying mechanism
///     for alchemical ingredients; taking a dried bundle grants ALC (the rack's outputs are alchemical),
///     and a master's rack preserves the dried contents a little longer (Axis 4, the perish-slow rung).
///     THE HONEST-TAKE RULE (0.4.21, the Lamp exploit): the grant fires only when the take removes
///     something that actually DRIED into an alchemical product on that rack. Expanded Foods patches
///     its meats and sausages herbrackable, which turned every charcuterie pull into remedy practice
///     and let a place/remove loop farm the day's share dry. Four gates now stand: a placed record
///     must exist for the slot (tracked at TryPut, persisted), the taken code must differ from the
///     placed code (something transitioned here), the placed collectible must carry a Dry transition
///     whose output is the taken item, and the taken item must have no nutrition (dried sausage also
///     rides a Dry transition; edible output is cooking's world, not alchemy's).
/// </summary>
public static class AlcPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;
    private static IWorldAccessor? serverWorld;

    /// <summary>Cauldron pos -> the tender's frozen mark "uid|name|level" (owner-at-tend). Refreshed on
    /// each interact, consumed-read at each cook completion (kept so repeat cooks credit too), dropped on
    /// break. Persisted: a cook can complete across a restart.</summary>
    private static Dictionary<string, string> cauldronOwners = new();
    /// <summary>Reaction-vessel pos -> the charger's mark (owner-at-charge). Grant at FinishReaction.</summary>
    private static Dictionary<string, string> reactionOwners = new();
    /// <summary>Stove pos -> the reaction-interact owner's UID (fuel-economy attribution). Transient is
    /// fine (a stove burn is short and re-captured on interact); persisted for restart continuity.</summary>
    private static Dictionary<string, string> stoveOwners = new();
    /// <summary>Herb-rack pos -> the last placer's mark (the rack's alchemist, for the perish-slow rung).</summary>
    private static Dictionary<string, string> herbRackOwners = new();
    /// <summary>Herb-rack pos/slot -> the collectible code placed there (the honest-take rule's
    /// evidence). Written at TryPut, popped at TryTake; persisted so a cutting that dries across a
    /// restart still pays. A slot with no record (pre-0.4.21 contents) never grants.</summary>
    private static Dictionary<string, string> herbRackPlaced = new();

    /// <summary>The real remedy stack captured at OnCreatedByCrafting (runs before ConsumeInput in
    /// CraftSingle), branded once the crafter is in scope. Single-threaded server craft -> one at a time.</summary>
    private static ItemStack? pendingRemedyStack;

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    private static bool IsHealingItem(ItemStack? stack) =>
        stack?.Collectible?.HasBehavior<CollectibleBehaviorHealingItem>() ?? false;

    // ------------------------------------------------------------ save/load + registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        serverWorld = api.World;

        api.Event.SaveGameLoaded += () =>
        {
            cauldronOwners = LoadMap(api, "almanacAlcCauldronOwners");
            reactionOwners = LoadMap(api, "almanacAlcReactionOwners");
            stoveOwners = LoadMap(api, "almanacAlcStoveOwners");
            herbRackOwners = LoadMap(api, "almanacAlcHerbRackOwners");
            herbRackPlaced = LoadMap(api, "almanacAlcHerbRackPlaced");
            TcmLog.Cat(api, TcmLog.Config,
                $"ALC owner maps loaded: {cauldronOwners.Count} cauldron / {reactionOwners.Count} reaction / {herbRackOwners.Count} rack");
        };
        api.Event.GameWorldSave += () =>
        {
            SaveMap(api, "almanacAlcCauldronOwners", cauldronOwners);
            SaveMap(api, "almanacAlcReactionOwners", reactionOwners);
            SaveMap(api, "almanacAlcStoveOwners", stoveOwners);
            SaveMap(api, "almanacAlcHerbRackOwners", herbRackOwners);
            SaveMap(api, "almanacAlcHerbRackPlaced", herbRackPlaced);
        };
    }

    private static Dictionary<string, string> LoadMap(ICoreServerAPI api, string key)
    {
        try
        {
            byte[]? data = api.WorldManager.SaveGame.GetData(key);
            if (data != null) return SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
        }
        catch (Exception e) { TcmLog.Error(api, $"ALC map {key} unreadable ({e.Message}); starting empty"); }
        return new();
    }

    private static void SaveMap(ICoreServerAPI api, string key, Dictionary<string, string> map) =>
        api.WorldManager.SaveGame.StoreData(key, SerializerUtil.Serialize(map));

    // ------------------------------------------------------------ conditional patches

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // ---- Potion cauldron (alchemy): owner-at-tend. The smeltItems completion grant is annotated
        // (vanilla firepit seam), filtered to potion output — so it is inert without alchemy.
        var tc = AccessTools.TypeByName("Alchemy.BlockEntityCauldronFirepit");
        var mci = tc == null ? null : AccessTools.DeclaredMethod(tc, "OnPlayerRightClick");
        if (mci != null)
        {
            harmony.Patch(mci, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(CauldronInteractPostfix))));
            var mcb = AccessTools.DeclaredMethod(tc, "OnBlockBroken");
            if (mcb != null) harmony.Patch(mcb, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(CauldronBrokenPostfix))));
            TcmLog.Info(api, "ALC potion cauldron hooked (owner-at-tend; grant/stamp at cook completion)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ALC potion cauldron absent (alchemy); potion co-grant inactive (vanilla floor unaffected)");

        // ---- Wet chemistry (industrialstory): grant at the standalone reaction completion.
        var tv = AccessTools.TypeByName("IndustrialStory.ApparatusReactionVesselEntity")
              ?? AccessTools.TypeByName("Industrialstory.ApparatusReactionVesselEntity");
        var mvc = tv == null ? null : AccessTools.DeclaredMethod(tv, "OnPlayerRightClick");
        var mvf = tv == null ? null : AccessTools.DeclaredMethod(tv, "FinishReaction");
        if (mvc != null && mvf != null)
        {
            harmony.Patch(mvc, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(ReactionChargePostfix))));
            harmony.Patch(mvf, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(ReactionFinishPostfix))));
            var mvb = AccessTools.DeclaredMethod(tv, "OnBlockBroken");
            if (mvb != null) harmony.Patch(mvb, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(ReactionBrokenPostfix))));
            TcmLog.Info(api, "ALC wet chemistry hooked (reaction-vessel charge owner + FinishReaction grant)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ALC wet-chemistry vessel seam not found (industrialstory 0.7.1 restructure?); chemistry grant inactive");

        // ---- Reaction fuel economy (industrialstory host stove): reaction burns only.
        var ts = AccessTools.TypeByName("IndustrialStory.BlockEntityStove")
              ?? AccessTools.TypeByName("Industrialstory.BlockEntityStove");
        var msi = ts == null ? null : AccessTools.DeclaredMethod(ts, "OnPlayerRightClick");
        var mst = ts == null ? null : AccessTools.DeclaredMethod(ts, "OnGameTick");
        if (msi != null && mst != null)
        {
            harmony.Patch(msi, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(StoveInteractPostfix))));
            harmony.Patch(mst, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(StoveTickPostfix))));
            TcmLog.Info(api, "ALC reaction fuel economy hooked (host stove burn, reaction-mode only)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ALC host-stove seam not found (industrialstory); reaction fuel economy inactive");

        // ---- Herb-rack drying (alchemy): grant at take + master perish-slow.
        var tr = AccessTools.TypeByName("Alchemy.BlockEntityHerbRacks");
        var mrt = tr == null ? null : AccessTools.DeclaredMethod(tr, "TryTake");
        var mru = tr == null ? null : AccessTools.DeclaredMethod(tr, "TryPut");
        var mrp = tr == null ? null : AccessTools.DeclaredMethod(tr, "OnInteract");
        var mrs = tr == null ? null : AccessTools.DeclaredMethod(tr, "Inventory_OnAcquireTransitionSpeed");
        if (mrt != null && mru != null)
        {
            harmony.Patch(mrt,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(HerbTakePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(HerbTakePostfix))));
            harmony.Patch(mru, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(HerbPutPostfix))));
            if (mrp != null) harmony.Patch(mrp, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(HerbInteractPostfix))));
            if (mrs != null) harmony.Patch(mrs, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlcPatches), nameof(HerbTransitionPostfix))));
            TcmLog.Info(api, "ALC herb-rack drying hooked (honest-take grant + master perish-slow)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ALC herb rack seams not found (alchemy TryTake/TryPut); herb-rack rung inactive");
    }

    // ------------------------------------------------------------ potion cauldron

    /// <summary>Freeze the tender's identity + rank on any cauldron interact (loading, lighting,
    /// stirring). The last tender is credited at the unattended cook completion.</summary>
    public static void CauldronInteractPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        cauldronOwners[PosKey(__instance.Pos)] =
            $"{byPlayer.PlayerUID}|{byPlayer.PlayerName}|{AlcDomain.LevelOf(byPlayer)}|{(AlcEmphasis.IsPotent(byPlayer) ? 1 : 0)}";
    }

    /// <summary>Grant + Brand stamp at the unattended firepit cook completion, filtered to potion
    /// output (the cauldron rides the vanilla BlockEntityFirepit.smeltItems). POT pit-kiln model: the
    /// Brand always stamps from the frozen tend-rank; the ALC grant lands only if the tender is online.
    /// Called from the annotated PotionCookPatch below.</summary>
    private static void OnPotionCooked(BlockEntityFirepit fp)
    {
        if (fp.Api?.Side != EnumAppSide.Server) return;
        ItemStack? outStack = fp.outputStack;
        if (outStack?.Collectible?.Code?.Path?.Contains("potion") != true) return;
        if (!cauldronOwners.TryGetValue(PosKey(fp.Pos), out string? packed) || packed == null) return;

        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int level)) return;

        // Emphasis is the tender's book choice, frozen at tend time (Potent/Lasting), so a cook that
        // completes offline still honors it. Only bites at Grandmaster (the read gates on level).
        bool potent = p.Length >= 4 && p[3] == "1";

        AlcBrand.Stamp(outStack, p[0], p[1], level, potent);
        fp.MarkDirty(true);

        IPlayer? owner = sapi?.World.PlayerByUid(p[0]);
        if (owner == null)
        {
            TcmLog.Cat(fp.Api, "alc", $"potion cooked at {fp.Pos}: {p[1]} offline; ALC credit lost (Brand stamped, {(potent ? "Potent" : "Lasting")})");
            return;
        }
        Core?.Ledger?.Log(owner, AlcDomain.Code, AlcDomain.TechRemedy,
            HashCode.Combine("potioncook", outStack.Collectible.Code.Path,
                (int)((serverWorld?.ElapsedMilliseconds ?? 0) / 60000)));
        TcmLog.Cat(fp.Api, "alc", $"potion cooked at {fp.Pos} -> ALC for {p[1]} (level {level}, {(potent ? "Potent" : "Lasting")})");
    }

    /// <summary>A destroyed cauldron drops its tender entry (the cook's contents are lost as vanilla).</summary>
    public static void CauldronBrokenPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side == EnumAppSide.Server) cauldronOwners.Remove(PosKey(__instance.Pos));
    }

    // ------------------------------------------------------------ wet chemistry

    /// <summary>Freeze the charger at the reaction-vessel interact (owner-at-charge). Completion is
    /// unattended, so this is banked back at FinishReaction if the charger is still online.</summary>
    public static void ReactionChargePostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        reactionOwners[PosKey(__instance.Pos)] =
            $"{byPlayer.PlayerUID}|{byPlayer.PlayerName}|{AlcDomain.LevelOf(byPlayer)}";
    }

    /// <summary>Grant the chemistry verb to the charger at the unattended reaction completion (if online).
    /// The industrial outputs are fungible (no Brand — acids carry no per-stack quality, ruled).</summary>
    public static void ReactionFinishPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (!reactionOwners.TryGetValue(PosKey(__instance.Pos), out string? packed) || packed == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 1) return;
        IPlayer? owner = sapi?.World.PlayerByUid(p[0]);
        if (owner == null) return; // offline: chemistry credit lost (unattended completion)
        Core?.Ledger?.Log(owner, AlcDomain.Code, AlcDomain.TechChemistry,
            HashCode.Combine("reaction", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (int)((serverWorld?.ElapsedMilliseconds ?? 0) / 60000)));
    }

    public static void ReactionBrokenPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side == EnumAppSide.Server) reactionOwners.Remove(PosKey(__instance.Pos));
    }

    // ---- reaction fuel economy (host stove, reaction burns only)

    /// <summary>Capture the alchemist on a REACTION-mode stove interact only. A cooking/smelting burn
    /// (COO/MET's use of the shared stove) is left alone.</summary>
    public static void StoveInteractPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        bool reactionMode = Traverse.Create(__instance).Property("InReactionMode").GetValue<bool>();
        if (!reactionMode) return;
        stoveOwners[PosKey(__instance.Pos)] = byPlayer.PlayerUID;
    }

    /// <summary>Reaction fuel economy: refund (or Untrained extra-consume) a fraction of this tick's
    /// burn under the alchemist's reaction, the MET FuelEconomyPatch shape. Reaction burns only — a
    /// cooking/smelting stove has no reaction owner captured, so it is untouched.</summary>
    public static void StoveTickPostfix(BlockEntity __instance, float dt)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        var tv = Traverse.Create(__instance);
        if (!tv.Property("IsBurning").GetValue<bool>()) return;
        if (!tv.Property("InReactionMode").GetValue<bool>() || !tv.Property("HasVessel").GetValue<bool>()) return;
        if (!stoveOwners.TryGetValue(PosKey(__instance.Pos), out string? uid)) return;
        IPlayer? owner = sapi?.World.PlayerByUid(uid);
        if (owner == null) return;

        double economy = AlcDomain.FuelEconomy(AlcDomain.LevelOf(owner));
        if (economy == 0) return;
        var fbt = tv.Field("fuelBurnTime");
        fbt.SetValue(fbt.GetValue<float>() + dt * (float)economy);
    }

    // ------------------------------------------------------------ herb-rack drying

    /// <summary>Placing a bundle marks the rack's alchemist (for the perish-slow rung).</summary>
    public static void HerbInteractPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        herbRackOwners[PosKey(__instance.Pos)] =
            $"{byPlayer.PlayerUID}|{byPlayer.PlayerName}|{AlcDomain.LevelOf(byPlayer)}";
    }

    /// <summary>Record what was placed in the slot: the honest-take rule's evidence. Overwrites any
    /// stale record for the slot (the previous occupant left by some other route).</summary>
    public static void HerbPutPostfix(BlockEntity __instance, BlockSelection blockSel, bool __result)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server || blockSel == null) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        var code = inv?[blockSel.SelectionBoxIndex]?.Itemstack?.Collectible?.Code;
        if (code == null) return;
        herbRackPlaced[$"{PosKey(__instance.Pos)}/{blockSel.SelectionBoxIndex}"] = code.ToString();
    }

    /// <summary>Capture the outgoing stack before TakeOut empties the slot, for the postfix's rule.</summary>
    public static void HerbTakePrefix(BlockEntity __instance, BlockSelection blockSel, out ItemStack? __state)
    {
        __state = null;
        if (__instance?.Api?.Side != EnumAppSide.Server || blockSel == null) return;
        var inv = (__instance as BlockEntityContainer)?.Inventory;
        __state = inv?[blockSel.SelectionBoxIndex]?.Itemstack;
    }

    /// <summary>The honest-take rule (0.4.21): taking from the alchemy herb rack grants ALC only when
    /// the item actually DRIED into an alchemical product on that rack. Four gates: a placed record
    /// exists for the slot, the taken code differs from what was placed, the placed collectible dries
    /// into the taken item (a Dry transition, matched by output code), and the result is not food
    /// (Expanded Foods meats and sausages are herbrackable and some also dry; charcuterie is cooking,
    /// not alchemy). Deduped per rack per minute as before.</summary>
    public static void HerbTakePostfix(BlockEntity __instance, IPlayer byPlayer, BlockSelection blockSel,
        bool __result, ItemStack? __state)
    {
        if (!__result || __instance?.Api?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
        var taken = __state?.Collectible;
        if (taken?.Code == null) return;

        string key = $"{PosKey(__instance.Pos)}/{blockSel.SelectionBoxIndex}";
        if (!herbRackPlaced.TryGetValue(key, out string? placedCode) || placedCode == null) return;
        herbRackPlaced.Remove(key);

        if (taken.Code.ToString() == placedCode) return;   // left the rack as it arrived: no work done
        if (taken.NutritionProps != null) return;          // edible output: preservation, not alchemy

        CollectibleObject? placed = serverWorld?.GetItem(new AssetLocation(placedCode))
            ?? (CollectibleObject?)serverWorld?.GetBlock(new AssetLocation(placedCode));
        var props = placed?.TransitionableProps;
        if (props == null) return;
        bool driedHere = false;
        foreach (var p in props)
        {
            if (p.Type == EnumTransitionType.Dry && taken.Code.Equals(p.TransitionedStack?.Code))
            {
                driedHere = true;
                break;
            }
        }
        if (!driedHere) return;

        Core?.Ledger?.Log(byPlayer, AlcDomain.Code, AlcDomain.TechRemedy,
            HashCode.Combine("herbrack", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (int)((serverWorld?.ElapsedMilliseconds ?? 0) / 60000)));
    }

    /// <summary>The perish-slow rung (Axis 4): a master's rack over-dries less, slowing the Perish
    /// transition of its dried contents. Rides the rack's own transition-speed delegate.</summary>
    public static void HerbTransitionPostfix(BlockEntity __instance, EnumTransitionType transType, ref float __result)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || transType != EnumTransitionType.Perish) return;
        if (!herbRackOwners.TryGetValue(PosKey(__instance.Pos), out string? packed) || packed == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int level)) return;
        __result *= (float)AlcDomain.HerbRackPreserve(level);
    }

    // ------------------------------------------------------------ remedy grant + Brand (annotated)

    /// <summary>Capture the real crafted remedy stack. OnCreatedByCrafting runs FIRST in CraftSingle
    /// (before ConsumeInput), and the outputSlot here holds the very stack the player receives, so we
    /// stash it and brand it once the crafter is in scope at ConsumeInput.</summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.OnCreatedByCrafting))]
    public static class RemedyCapturePatch
    {
        public static void Postfix(ItemSlot outputSlot)
        {
            var stack = outputSlot?.Itemstack;
            pendingRemedyStack = IsHealingItem(stack) ? stack : null;
        }
    }

    /// <summary>The vanilla ALC floor: a real grid-craft of a healing item grants ALC and stamps the
    /// maker's Brand on the captured output batch, with the crafter's book emphasis. Server-only, real
    /// take only (never previews — ConsumeInput is the PF craft seam).</summary>
    [HarmonyPatch(typeof(GridRecipe), nameof(GridRecipe.ConsumeInput))]
    public static class RemedyGrantPatch
    {
        public static void Postfix(GridRecipe __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || byPlayer?.Entity?.World?.Side != EnumAppSide.Server) return;
            if (!IsHealingItem(__instance?.Output?.ResolvedItemStack)) { pendingRemedyStack = null; return; }

            Core?.Ledger?.Log(byPlayer, AlcDomain.Code, AlcDomain.TechRemedy,
                HashCode.Combine("remedy", __instance!.Output!.ResolvedItemStack.Collectible.Id,
                    byPlayer.Entity.World.ElapsedMilliseconds / 1000));

            if (IsHealingItem(pendingRemedyStack))
                AlcBrand.Stamp(pendingRemedyStack, byPlayer.PlayerUID, byPlayer.PlayerName,
                    AlcDomain.LevelOf(byPlayer), AlcEmphasis.IsPotent(byPlayer));
            pendingRemedyStack = null;
        }
    }

    /// <summary>The potion cook completion (vanilla firepit seam; the alchemy cauldron rides it). Filters
    /// to potion output inside OnPotionCooked, so a normal firepit meal/smelt is ignored and the whole
    /// patch is inert without alchemy.</summary>
    [HarmonyPatch(typeof(BlockEntityFirepit), "smeltItems")]
    public static class PotionCookPatch
    {
        public static void Postfix(BlockEntityFirepit __instance) => OnPotionCooked(__instance);
    }
}

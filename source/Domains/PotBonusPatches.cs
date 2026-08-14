using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// POT Phase 3 — the Potter's Mark (Axis 6, RULED 2026-07-09; pot-vessel-study adopted 7/7,
/// seams verified against 1.22.3). The container-side mirror of COO's Cook's Mark: a
/// per-instance preservation quality stamped on a fired keep-vessel by the firer's rank, read
/// off the vessel's own perish factor. One potterBy stamp, two jobs:
///   • the preservation ladder — Untrained crocks seal imperfectly (x1.10, POT's penalty band),
///     a masterwork crock keeps food (x0.85); and
///   • tiered provenance in the tooltip (Thrown by / Master-potted by / Masterwork), Journeyman up.
///
/// The perish read rides the vessel's own container modifier, which the crock OVERRIDES without
/// calling base — so the read is patched on BOTH the base BlockContainer virtual (storage vessel,
/// amphora, any inheritor) AND the BlockCrock overrides (the primary carrier), with no
/// double-apply. The two factors (this vessel factor and any COO food-side stamp) compose
/// multiplicatively in the same chain and never collide.
///
/// The lifecycle re-carry (miss one hop and the mark dies there): stamped at CLAYFORMING on the
/// raw piece (PotPatches, the former's rank -- RULED 2026-08-13, was owner-at-ignite until then)
/// -> the firing carry moves it onto the fired ware, because vanilla OnFired clones the
/// SmeltedStack and drops custom attrs -> placed vessel writes the mark to a persisted pos map
/// (the BE does not serialize custom attrs either) -> the placed read consults that map -> the
/// carried read consults the stack attr -> pickup restores the stack attr from the map. Crock
/// carriage is wired fully; the generic storage vessel is hooked opportunistically (warns-and-skips
/// if it does not declare the hop). The carried-vessel edge works with no carriage at all.
///
/// TWO LINEAGES, TWO MECHANISMS (confirmed in vanilla source 2026-08-13). The four reads above
/// only ever reach the crock: GetContainingTransitionModifierPlaced has exactly one call site in
/// the whole game, BECrock.Inv_OnAcquireTransitionSpeed (BECrock.cs:46), nothing calls the
/// BlockContainer base, and BlockGenericTypedContainer does not descend from BlockContainer at
/// all. Until 0.4.38 a placed storage vessel therefore stored its mark, PRINTED its mark and its
/// percentage, and preserved exactly nothing. The vessel now scales its own
/// InventoryGeneric.TransitionableSpeedMulByType instead (see ScaleVesselInventory), which is the
/// one value both the block-info panel and GetTransitionSpeedMul read.
///
/// STILL OPEN: that covers the PLACED vessel. A CARRIED vessel holding contents has no reachable
/// per-instance hook found so far, so its tooltip percentage is a placed-only claim. The crock
/// has both.
///
/// NAMING DEBT: PotTierAttr holds a LEVEL (PotDomain.LevelOf), and PreserveFactor's "tier"
/// parameter feeds RankLinear, which is also level-based. The behaviour is right; only the word is
/// wrong. Renaming the attribute key would orphan every mark already in a world for no behaviour
/// change, so it waits for a wipe.
/// </summary>
public static class PotBonusPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    public const string PotByAttr = "almanactcm:potby";
    public const string PotByNameAttr = "almanactcm:potbyname";
    public const string PotTierAttr = "almanactcm:pottier";

    /// <summary>Vessel pos -> the potter's mark, packed "uid|name|tier". Persisted: a placed crock
    /// sits through restarts, and the placed-read needs the mark the BE cannot itself carry.</summary>
    private static Dictionary<string, string> vesselMarks = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacPotVesselMarks");
                if (data != null)
                    vesselMarks = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
                TcmLog.Cat(api, TcmLog.Config, $"POT vessel marks loaded: {vesselMarks.Count} placed vessel(s)");
            }
            catch (Exception e) { TcmLog.Error(api, $"POT vessel-mark map unreadable ({e.Message}); starting empty"); }
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacPotVesselMarks",
                Vintagestory.API.Util.SerializerUtil.Serialize(vesselMarks));
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // The perish read — base virtual (storage vessel / amphora / any inheritor) AND the crock
        // overrides (the primary carrier, which does not call base). Placed and carried each.
        HookRead(api, harmony, "Vintagestory.GameContent.BlockContainer", "GetContainingTransitionModifierPlaced",
            nameof(PlacedReadPostfix), "POT preservation read (base, placed)");
        HookRead(api, harmony, "Vintagestory.GameContent.BlockContainer", "GetContainingTransitionModifierContained",
            nameof(ContainedReadPostfix), "POT preservation read (base, carried)");
        HookRead(api, harmony, "Vintagestory.GameContent.BlockCrock", "GetContainingTransitionModifierPlaced",
            nameof(PlacedReadPostfix), "POT preservation read (crock, placed)");
        HookRead(api, harmony, "Vintagestory.GameContent.BlockCrock", "GetContainingTransitionModifierContained",
            nameof(ContainedReadPostfix), "POT preservation read (crock, carried)");

        // The storage vessel's placed read (added 2026-08-13, reshaped the same day; see
        // ScaleVesselInventory for why the first shape was invisible). None of the four hooks
        // above ever reach a vessel: BlockGenericTypedContainer does not descend from
        // BlockContainer, and GetContainingTransitionModifierPlaced has exactly one call site in
        // the whole game (BECrock.Inv_OnAcquireTransitionSpeed, BECrock.cs:46).
        //
        // Three hooks, because the factor has to be re-applied wherever the inventory is rebuilt
        // and wherever the mark arrives, and those are different moments on the two sides:
        //   Initialize      - server, placed vessel on chunk load (mark from the position store)
        //   FromTreeAttributes - CLIENT, and the server on world load (mark from the BE tree)
        //   OnBlockPlaced   - server, fresh placement (already hooked below for the store)
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityGenericTypedContainer", "Initialize",
            nameof(VesselInitPostfix), "POT preservation read (storage vessel, placed)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityGenericTypedContainer", "ToTreeAttributes",
            nameof(VesselToTreePostfix), "POT vessel mark sync (write)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityGenericTypedContainer", "FromTreeAttributes",
            nameof(VesselFromTreePostfix), "POT vessel mark sync (read)");

        // Placed/pickup carriage — the crock (primary) fully; the generic storage vessel
        // opportunistically. The carried read needs no carriage (the stamp rides the stack).
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityCrock", "OnBlockPlaced",
            nameof(VesselPlacedPostfix), "POT mark carriage (crock placed)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockCrock", "OnPickBlock",
            nameof(VesselPickPostfix), "POT mark carriage (crock pickup)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockEntityGenericTypedContainer", "OnBlockPlaced",
            nameof(VesselPlacedPostfix), "POT mark carriage (storage vessel placed)");
        HookCarry(api, harmony, "Vintagestory.GameContent.BlockGenericTypedContainer", "OnPickBlock",
            nameof(VesselPickPostfix), "POT mark carriage (storage vessel pickup)");

        // The Potter's Mark line is contributed to Engine.ProvenanceLine (see MarkLine below),
        // which orders vessels last in the block.
    }

    private static void HookRead(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); inactive"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotBonusPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    private static void HookCarry(ICoreAPI api, Harmony harmony, string typeName, string method, string postfix, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null) { TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that carriage link is inactive"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotBonusPatches), postfix)));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method})");
    }

    // ------------------------------------------------------------ the stamp

    /// <summary>Can this collectible hold food, and therefore mean anything by "it keeps what it
    /// holds"? Vanilla has two container lineages that never meet, so both have to be named:
    /// BlockContainer, which declares GetContainingTransitionModifier{Placed,Contained} (crock,
    /// meal pot, jug), and the "Container" BLOCK behaviour, which is how BlockGenericTypedContainer
    /// stores things without descending from BlockContainer at all (storage vessel).
    ///
    /// Mind the registry: "Container" in a blocktype's `behaviors` list is BlockBehaviorContainer
    /// (Core.cs:659), NOT CollectibleBehaviorContainer. Collectible.HasBehavior&lt;T&gt; searches
    /// only the collectible list and answers false here, which cost two play tests to find.
    ///
    /// ONE predicate, TWO questions, and the difference is the whole point. PotPatches asks it of
    /// what a raw piece FIRES INTO, to decide whether to stamp. MarkLine asks it of the stack
    /// ITSELF, to decide whether the preservation clause is true yet. Raw clay answers yes to the
    /// first and no to the second, which is exactly right: the potter earned the mark when they
    /// shaped it, and it keeps nothing until the kiln has been.</summary>
    public static bool HoldsFood(CollectibleObject? coll)
        => coll is Vintagestory.GameContent.BlockContainer
           || (coll as Block)?.GetBehavior(typeof(Vintagestory.GameContent.BlockBehaviorContainer), true) != null;

    /// <summary>Stamp a freshly FORMED raw piece with its former's mark (PotPatches.TryStampFormed,
    /// which owns the gate on what is worth stamping). RULED 2026-08-13: the Potter's Mark belongs
    /// to whoever shaped the clay, not whoever lit the kiln, so this fires at clayforming and the
    /// firing carry moves it onto the fired ware.</summary>
    public static void StampFormed(ItemStack? stack, string uid, string name, int level)
    {
        if (stack == null) return;
        stack.Attributes.SetString(PotByAttr, uid);
        stack.Attributes.SetString(PotByNameAttr, name);
        stack.Attributes.SetInt(PotTierAttr, level);
    }

    /// <summary>Lift a stamped mark off a stack as "uid|name|level", or null if unmarked. The
    /// firing carry needs this: vanilla OnFired replaces the slot with a clone of the SmeltedStack,
    /// which drops every custom attribute, so the mark has to be read before and written after.</summary>
    public static string? PackOf(ItemStack? stack)
    {
        var attrs = stack?.Attributes;
        if (attrs?.HasAttribute(PotTierAttr) != true) return null;
        return $"{attrs.GetString(PotByAttr)}|{attrs.GetString(PotByNameAttr)}|{attrs.GetInt(PotTierAttr)}";
    }

    public static void ApplyPacked(ItemStack? stack, string packed)
    {
        if (stack == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 3) return;
        stack.Attributes.SetString(PotByAttr, p[0]);
        stack.Attributes.SetString(PotByNameAttr, p[1]);
        if (int.TryParse(p[2], out int tier)) stack.Attributes.SetInt(PotTierAttr, tier);
    }

    // ------------------------------------------------------------ placed/pickup carriage

    /// <summary>A marked vessel placed: remember its mark by position (the BE cannot carry the
    /// custom attr through save/load). An UNMARKED vessel clears any stale entry.</summary>
    public static void VesselPlacedPostfix(BlockEntity __instance, ItemStack? byItemStack)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        string key = PosKey(__instance.Pos);
        var attrs = byItemStack?.Attributes;
        if (attrs?.HasAttribute(PotTierAttr) == true)
        {
            vesselMarks[key] = $"{attrs.GetString(PotByAttr)}|{attrs.GetString(PotByNameAttr)}|{attrs.GetInt(PotTierAttr)}";
            TcmLog.Cat(__instance.Api, "pot", $"vessel placed at {__instance.Pos} carries the mark of {attrs.GetString(PotByNameAttr)}; stored");
        }
        else vesselMarks.Remove(key);

        // Fresh placement never passes through FromTreeAttributes, so scale here too. An unmarked
        // vessel scales by 1.0, which also UNDOES a previous mark if this position used to hold a
        // marked vessel and the block entity was reused.
        ScaleVesselInventory(__instance, MarkedLevel(__instance.Pos));
        __instance.MarkDirty(true); // ship the tree, so the client scales its copy as well
    }

    /// <summary>Pickup rebuilds the vessel stack from BE data (custom attrs lost); restore the mark
    /// from the position store. The entry stays (pickup also fires for previews/drops).</summary>
    public static void VesselPickPostfix(IWorldAccessor world, BlockPos pos, ItemStack __result)
    {
        if (world?.Side != EnumAppSide.Server || pos == null) return;
        if (vesselMarks.TryGetValue(PosKey(pos), out string? packed) && packed != null)
            ApplyPacked(__result, packed);
    }

    // ------------------------------------------------------------ the preservation read

    /// <summary>Placed vessel: multiply its perish factor by the potter's preservation quality
    /// (x1.10 Untrained penalty ... x0.85 GM). Reads the mark from the position store.</summary>
    public static void PlacedReadPostfix(BlockPos pos, EnumTransitionType transType, ref float __result)
    {
        if (transType != EnumTransitionType.Perish || pos == null) return;
        if (!vesselMarks.TryGetValue(PosKey(pos), out string? packed) || packed == null) return;
        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int tier)) return;
        __result *= (float)PotDomain.PreserveFactor(tier);
    }

    // ------------------------------------------------------------ the storage vessel's factor

    /// <summary>Which factor each inventory has already been scaled by, so a re-apply corrects
    /// rather than compounds. Weak on the inventory because InitInventory builds a NEW
    /// InventoryGeneric every time it runs, and the old one should be collectable.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InventoryBase,
        System.Runtime.CompilerServices.StrongBox<float>> vesselScaled = new();

    /// <summary>Fold the potter's factor into the vessel's own TransitionableSpeedMulByType.
    ///
    /// SHAPE RULED 2026-08-13, and the reason is the whole point. The first attempt subscribed to
    /// Inventory.OnAcquireTransitionSpeed, which does drive real spoilage, and was invisible: the
    /// block-info panel a player actually reads is built by BEContainer.GetBlockInfo (:133) out of
    /// GetPerishRate x TransitionableSpeedMulByType x PerishableFactorByFoodCategory, and never
    /// calls GetTransitionSpeedMul at all. A bonus nobody can see is not a bonus. Writing the
    /// factor into the dictionary instead puts it in the ONE value both the display and
    /// InventoryGeneric.GetTransitionSpeedMul (:178) read, so the number on screen and the number
    /// the game uses cannot drift apart. That is the same rule 0.4.38 applied to the freshness
    /// line when COO and FAR each stated a partial figure.
    ///
    /// Safe to mutate: InitInventory deserializes a fresh dictionary per block entity
    /// (BEGenericTypedContainer.cs:222), so nothing here is shared between vessels.</summary>
    private static void ScaleVesselInventory(BlockEntity? be, int level)
    {
        if (be is not Vintagestory.GameContent.BlockEntityGenericTypedContainer vessel) return;
        if (vessel.Inventory is not InventoryGeneric inv) return;

        // A negative level means UNMARKED, which is neutral. Untrained is not neutral: it is the
        // penalty band. Keeping those apart is the whole reason MarkedLevel returns -1 rather
        // than 0 for a vessel nobody signed.
        float factor = level < 0 ? 1f : (float)PotDomain.PreserveFactor(level);
        var applied = vesselScaled.GetOrCreateValue(inv);
        if (System.Math.Abs(applied.Value - factor) < 0.0001f) return; // already carrying exactly this

        inv.TransitionableSpeedMulByType ??= new Dictionary<EnumTransitionType, float>();
        if (!inv.TransitionableSpeedMulByType.TryGetValue(EnumTransitionType.Perish, out float current) || current <= 0)
            current = 1f;
        if (applied.Value > 0) current /= applied.Value; // back out the previous factor first
        inv.TransitionableSpeedMulByType[EnumTransitionType.Perish] = current * factor;
        applied.Value = factor;
    }

    /// <summary>The level stored for a position, or -1. -1 and Untrained are NOT the same thing:
    /// an unmarked vessel is neutral, an Untrained potter's vessel carries the penalty band.</summary>
    private static int MarkedLevel(BlockPos? pos)
    {
        if (pos == null || !vesselMarks.TryGetValue(PosKey(pos), out string? packed) || packed == null) return -1;
        string[] p = packed.Split('|');
        return p.Length >= 3 && int.TryParse(p[2], out int level) ? level : -1;
    }

    /// <summary>Human-readable mark for /tcm perish.</summary>
    public static string DescribeMark(BlockPos pos)
    {
        if (!vesselMarks.TryGetValue(PosKey(pos), out string? packed) || packed == null) return "none (neutral)";
        string[] p = packed.Split('|');
        if (p.Length < 3 || !int.TryParse(p[2], out int level)) return $"unreadable ({packed})";
        return $"{p[1]} at level {level}, factor x{PotDomain.PreserveFactor(level):0.###}";
    }

    /// <summary>Placed vessel coming back on chunk load: the mark lives in the position store on
    /// this side, and Pos is guaranteed valid by Initialize (it is not during InitInventory, which
    /// also runs from FromTreeAttributes).</summary>
    public static void VesselInitPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        ScaleVesselInventory(__instance, MarkedLevel(__instance.Pos));
    }

    /// <summary>Put the level in the BE tree so the CLIENT can scale its own copy. Without this the
    /// block-info panel computes an unmarked rate and reports it confidently, which is exactly the
    /// disagreement this whole shape exists to prevent.</summary>
    public static void VesselToTreePostfix(BlockEntity __instance, ITreeAttribute tree)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || tree == null) return;
        int level = MarkedLevel(__instance.Pos);
        if (level >= 0) tree.SetInt(PotTierAttr, level);
    }

    /// <summary>The client's only source for the mark, and the server's on world load for a vessel
    /// saved before the position store knew about it.</summary>
    public static void VesselFromTreePostfix(BlockEntity __instance, ITreeAttribute tree)
    {
        if (tree?.HasAttribute(PotTierAttr) != true) return;
        ScaleVesselInventory(__instance, tree.GetInt(PotTierAttr));
    }

    /// <summary>Carried vessel: the same edge, read from the stack's own stamp attribute (no
    /// carriage needed — the fired stamp rides the stack).</summary>
    public static void ContainedReadPostfix(ItemSlot inSlot, EnumTransitionType transType, ref float __result)
    {
        if (transType != EnumTransitionType.Perish) return;
        var attrs = inSlot?.Itemstack?.Attributes;
        if (attrs?.HasAttribute(PotTierAttr) != true) return;
        __result *= (float)PotDomain.PreserveFactor(attrs.GetInt(PotTierAttr));
    }

    // ------------------------------------------------------------ provenance tooltip

    /// <summary>The Potter's Mark line (from Journeyman up, ruled): Thrown by / Master-potted by /
    /// Masterwork. Non-stacking vessels carry it durably; bricks and stackable smallware merge and
    /// never show it. Placement, order and spacing belong to <see cref="Engine.ProvenanceLine"/>,
    /// which puts vessels last: a crock of stew is a stew first. This only decides what POT has
    /// to say.</summary>
    public static string? MarkLine(ItemStack stack)
    {
        var attrs = stack?.Attributes;
        string? name = attrs?.GetString(PotByNameAttr);
        if (string.IsNullOrEmpty(name)) return null;
        int tier = attrs!.GetInt(PotTierAttr);

        // Is this thing a vessel YET? Since 0.4.38 the mark lands at clayforming, so it rides raw
        // clay that cannot hold anything until it has been fired. Saying "it keeps what it holds"
        // and quoting a percentage on a lump of wet clay is a claim about a thing that does not
        // exist. The provenance is still real and still shown; only the promise waits for the kiln.
        bool fired = HoldsFood(stack?.Collectible);

        string? line =
            tier >= Rank.Grandmaster
                ? Lang.Get(fired ? "almanactcm:masterwork-by" : "almanactcm:masterwork-by-raw", name)
            : tier >= Rank.Master ? Lang.Get("almanactcm:masterpotted-by", name)
            : tier >= Rank.Journeyman ? Lang.Get("almanactcm:thrown-by", name)
            : null;
        if (line == null) return null;

        // The numbers ruling (2026-08-01): "it keeps what it holds" now says by how much. Only
        // once there is something it can hold.
        int pct = (int)System.Math.Round((1.0 - PotDomain.PreserveFactor(tier)) * 100.0);
        if (fired && pct > 0) line += Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-contents-keep", pct));

        // REVISIT (noted 2026-08-12, deliberately NOT changed yet).
        //
        // This is the last isolated spoilage percentage in the mod. COO and FAR both lost
        // theirs in 0.4.38 because they postfix the SAME method (GetTransitionRateMul) and
        // their factors composed, so each one stating its own factor was a lie. POT is not
        // that bug: it rides GetContainingTransitionModifierPlaced/Contained instead, a
        // different vanilla seam, so it does not compose with COO/FAR and it cancels cleanly
        // out of Engine.Attribution's probe. The number here is currently TRUE.
        //
        // What is worth deciding later is whether it is COMPLETE. A player reading a crock
        // sees POT's figure on the crock and the composed COO+FAR figure on the food inside,
        // from two surfaces that never reference each other, and nothing states the whole
        // effect on the thing they are about to eat. If a second contributor is ever added
        // to the container seam (a TEM cellar, an ALC preservative), this line becomes the
        // same bug COO and FAR just had, on a different method.
        //
        // Two options when it comes up: extend the Attribution probe to the container seam
        // and annotate there the way the freshness line is annotated, or drop this clause
        // and let the contained food's freshness line carry everything. Do not add a second
        // container-seam contributor without picking one first.
        //
        // UPDATE 2026-08-13: the vantage point now exists. Engine.ProvenanceLine sees every
        // domain's mark on a stack at once, so a rule spanning POT and COO has somewhere to
        // live. That is a decision about what to say, not a missing seam any more.

        return line;
    }
}

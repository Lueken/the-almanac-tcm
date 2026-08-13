using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// POT Phase 1 — the two practice-granting verbs (technique-maps §POT, both [vanilla]).
///
///   • Clayforming (#1): a prefix/postfix pair on BlockEntityClayForm.CheckIfFinished,
///     which grants the verb and stamps the Potter's Mark on the raw ware. The pottery-wheel
///     variant is a conditional reduced-raw postfix on the private
///     SimplePotteryWheel.ClayWheelEntity.CheckIfFinished (warns-and-skips if absent).
///   • Pit firing (#2): a prefix/postfix pair on the unattended BlockEntityPitKiln.OnFired,
///     which converts ware to its SmeltedStack ONLY when IsValidPitKiln still holds, so the
///     grant is success-gated for free (a rained-out or breached firing skips the convert and
///     banks nothing). OnFired carries no player, so the igniter is captured at TryIgnite into
///     a persisted pos->owner map (the graft-owner pattern) and read back at completion. The
///     igniter gets the firing VERB; the mark is not theirs (see below).
///
/// TWO CORRECTIONS, both 2026-08-13.
///
/// 1. Clayforming moved off the event bus. It used to be one listener on `onitemclayformed`,
///    which had the virtue of touching no vanilla code and the fatal flaw of never firing.
///    CheckIfFinished pushes that event only from its final drop-loop path, and it returns
///    before reaching that path for every GroundStorable output (BEClayForm.cs:246-297). Every
///    raw clay blocktype in vanilla is GroundStorable (bowl, crock, crucible, flowerpot,
///    ingotmold, jug, oillamp, planter, pot, storagevessel, toolmold, wateringcan), so the
///    listener granted practice for nothing a potter actually makes. It is still registered,
///    but only to stamp the drop path (which no vanilla recipe reaches) and to carry the grant
///    if the patch seam ever goes missing.
///
/// 2. The Potter's Mark belongs to the FORMER, not the firer (RULED: "the pot mark should fire
///    once the last piece of clay is placed, regardless of who fires the raw vessel or crock").
///    So it is stamped on the raw piece here, and the firing hook carries it across the
///    conversion rather than minting it. Practice still splits the way the verbs do: shaping
///    credits the former, lighting the kiln credits the igniter.
/// </summary>
public static class PotPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;
    private static IWorldAccessor? serverWorld;

    /// <summary>Kiln pos -> the igniter's frozen mark, packed "uid|name|tier" (owner-at-ignite).
    /// OnFired fires unattended up to a full burn later, possibly across a restart, so this
    /// persists. Consumed at OnFired (grant + Potter's Mark stamp), then removed.</summary>
    private static Dictionary<string, string> kilnOwners = new();

    /// <summary>Did the CheckIfFinished seam take? Decides whether the legacy event listener still
    /// has to carry the clayforming grant (it only sees non-ground-storable ware, so this is a
    /// degraded fallback, not a second path).</summary>
    private static bool clayformPatched;

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        serverWorld = api.World;

        api.Event.SaveGameLoaded += () =>
        {
            try
            {
                byte[]? data = api.WorldManager.SaveGame.GetData("almanacPotKilnOwners");
                if (data != null)
                    kilnOwners = Vintagestory.API.Util.SerializerUtil.Deserialize<Dictionary<string, string>>(data) ?? new();
                TcmLog.Cat(api, TcmLog.Config, $"POT kiln owners loaded: {kilnOwners.Count} lit kiln(s)");
            }
            catch (Exception e) { TcmLog.Error(api, $"POT kiln-owner map unreadable ({e.Message}); starting empty"); }
        };
        api.Event.GameWorldSave += () =>
            api.WorldManager.SaveGame.StoreData("almanacPotKilnOwners",
                Vintagestory.API.Util.SerializerUtil.Serialize(kilnOwners));

        // The legacy event listener, demoted (see the class doc): it only sees ware that reaches
        // CheckIfFinished's drop loop, which no vanilla clay recipe does. It stamps that path and
        // carries the grant only if the CheckIfFinished patch failed to land.
        api.Event.RegisterEventBusListener(OnClayFormed, 0.5, "onitemclayformed");
        TcmLog.Cat(api, TcmLog.Config, clayformPatched
            ? "POT clayforming drop-path listener registered (stamp only; the grant rides CheckIfFinished)"
            : "POT clayforming drop-path listener registered CARRYING THE GRANT (CheckIfFinished seam missing; ground-storable ware banks nothing)");
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Clayforming (#1): grant the verb and stamp the Potter's Mark where the ware is actually
        // produced. See the class doc for why this replaced the event-bus listener.
        var tc = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityClayForm");
        var mc = tc == null ? null : AccessTools.DeclaredMethod(tc, "CheckIfFinished");
        if (mc != null)
        {
            harmony.Patch(mc,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(FormPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(FormPostfix))));
            clayformPatched = true;
            TcmLog.Info(api, "POT clayforming hooked (BlockEntityClayForm.CheckIfFinished: grant + Potter's Mark)");
        }
        else TcmLog.Warn(api, "POT clayforming seam not found (BlockEntityClayForm.CheckIfFinished); falling back to the onitemclayformed listener, which never fires for ground-storable ware");

        // Pit firing (#2): capture the igniter at TryIgnite, grant at OnFired. Both are DECLARED on
        // BlockEntityPitKiln (the override rule does not bite here, but stay explicit). The OnFired
        // pair also carries the former's mark across vanilla's SmeltedStack clone.
        var tk = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityPitKiln");
        var mi = tk == null ? null : AccessTools.DeclaredMethod(tk, "TryIgnite");
        var mf = tk == null ? null : AccessTools.DeclaredMethod(tk, "OnFired");
        if (mi != null && mf != null)
        {
            harmony.Patch(mi, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(IgnitePostfix))));
            harmony.Patch(mf,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(FiredPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(FiredPostfix))));
            TcmLog.Info(api, "POT pit firing hooked (TryIgnite owner capture + OnFired success-gated grant, mark carried)");
        }
        else TcmLog.Warn(api, "POT pit-firing seam not found (BlockEntityPitKiln TryIgnite/OnFired); firing verb inactive");

        // Pottery-wheel variant (#1 conditional): the wheel does not push onitemclayformed, so
        // grant its completion directly at a reduced raw. Private method -> DeclaredMethod; the
        // whole thing warns-and-skips if simplepotterywheel is absent.
        var tw = AccessTools.TypeByName("SimplePotteryWheel.ClayWheelEntity");
        var mw = tw == null ? null : AccessTools.DeclaredMethod(tw, "CheckIfFinished");
        if (mw != null)
        {
            harmony.Patch(mw,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(WheelPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(WheelPostfix))));
            TcmLog.Info(api, "POT pottery-wheel variant hooked (ClayWheelEntity.CheckIfFinished, reduced raw)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "POT pottery-wheel absent; wheel variant inactive (vanilla clayforming unaffected)");
    }

    // ------------------------------------------------------------ clayforming

    /// <summary>CheckIfFinished returns void and nulls SelectedRecipe on completion, so capture the
    /// recipe's output before the call; a non-null capture that comes back with SelectedRecipe null
    /// is the completion signal (the same shape the pottery-wheel variant already uses).</summary>
    public static void FormPrefix(BlockEntity __instance, out ItemStack? __state)
    {
        __state = __instance?.Api?.Side != EnumAppSide.Server
            ? null
            : (__instance as BlockEntityClayForm)?.SelectedRecipe?.Output?.ResolvedItemstack;
    }

    /// <summary>The completed piece: grant the verb to the shaper and stamp their Potter's Mark on
    /// the ware. Every vanilla clay recipe lands in a groundstorage BE at the clayform's own
    /// position, so that is where the stamp goes; the drop path is OnClayFormed's (it holds the
    /// stack before it is handed over) and the single-block SetBlock path leaves no stack at
    /// all.</summary>
    public static void FormPostfix(BlockEntity __instance, IPlayer byPlayer, ItemStack? __state)
    {
        if (__state == null || byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        if (__instance is not BlockEntityClayForm form || form.SelectedRecipe != null) return; // unfinished

        CreditClayform(byPlayer, __state.Collectible?.Code?.ToString() ?? "clay", 1.0);

        if (__instance.Api.World.BlockAccessor.GetBlockEntity<BlockEntityGroundStorage>(__instance.Pos)
            is not BlockEntityGroundStorage store) return;
        bool stamped = false;
        for (int i = 0; i < store.Inventory.Count; i++)
        {
            var slot = store.Inventory[i];
            if (!TryStampFormed(slot?.Itemstack, byPlayer, __instance.Api.World)) continue;
            slot!.MarkDirty();
            stamped = true;
        }
        if (!stamped) return;
        store.MarkDirty(true);
        TcmLog.Cat(__instance.Api, "pot", $"{byPlayer.PlayerName} (POT {PotDomain.LevelOf(byPlayer)}) formed {__state.Collectible?.Code}; Potter's Mark stamped at {__instance.Pos}");
    }

    /// <summary>Stamp one raw piece with its former's mark, if the mark will ever mean anything on
    /// it. The gate is a property test, not a list: the mark's only mechanical effect is the
    /// preservation multiplier, so the question is whether the fired ware is something that HOLDS
    /// food. Two ways vanilla says yes, because it has two container lineages that do not meet:
    ///   • BlockContainer, which declares GetContainingTransitionModifier{Placed,Contained} (the
    ///     crock, the meal pot, the jug);
    ///   • the "Container" BLOCK behaviour, which is how BlockGenericTypedContainer says it stores
    ///     things without descending from BlockContainer at all (the storage vessel).
    /// Note the registry: "Container" in a blocktype's `behaviors` list resolves to
    /// BlockBehaviorContainer (Core.cs:659), NOT CollectibleBehaviorContainer, and the two live in
    /// separate registries reached by separate accessors. Collectible.HasBehavior&lt;T&gt; only ever
    /// sees the collectible list, so it answers false here; Block.GetBehavior(Type, bool) is the
    /// block-behaviour accessor and is what this uses.
    /// Across vanilla's fired clay that resolves to exactly the crock and the storage vessel, the
    /// keep-vessel line. Molds, tiles, shingles, bullets, empty bowls and flowerpots fire into
    /// plain Blocks, and stamping those would buy a decorative attribute at the price of their
    /// stacking, since attributes are part of stack identity. Returns whether anything was
    /// written.</summary>
    private static bool TryStampFormed(ItemStack? raw, IPlayer former, IWorldAccessor world)
    {
        if (raw?.Collectible == null) return false;
        var fired = raw.Collectible.GetCombustibleProperties(world, raw, null)?.SmeltedStack?.ResolvedItemstack?.Collectible;
        if (fired == null) return false;
        if (fired is not BlockContainer
            && (fired as Block)?.GetBehavior(typeof(BlockBehaviorContainer), true) == null)
        {
            // Say WHY, once per piece. This gate has now been wrong twice (BlockContainer alone
            // missed the storage vessel; the collectible-behaviour registry never sees "Container"),
            // and a silent false is what made both cost a play-test round trip.
            TcmLog.Cat(world.Api, "pot", $"no mark on {raw.Collectible.Code}: fires into {fired.Code}, which is neither a BlockContainer ({fired.GetType().Name}) nor a Container-behaviour block");
            return false;
        }
        PotBonusPatches.StampFormed(raw, former.PlayerUID, former.PlayerName, PotDomain.LevelOf(former));
        return true;
    }

    /// <summary>The demoted drop-path listener. Vanilla pushes this event from CheckIfFinished's
    /// final loop, which no ground-storable ware ever reaches, so in practice this fires only for
    /// modded clay ware that skips ground storage.</summary>
    private static void OnClayFormed(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (serverWorld == null || data is not ITreeAttribute tree) return;
        long eid = tree.GetLong("byentityid");
        if (serverWorld.GetEntityById(eid) is not EntityPlayer ep || ep.Player == null) return;
        var stack = tree.GetItemstack("itemstack");

        // Pushed after the stack is built and before it is handed over, so what we mark here is
        // what the player receives.
        TryStampFormed(stack, ep.Player, serverWorld);

        // The grant belongs to FormPostfix, which sees every path. Only stand in for it if the
        // seam is missing entirely.
        if (!clayformPatched)
            CreditClayform(ep.Player, stack?.Collectible?.Code?.ToString() ?? "clay", 1.0);
    }

    /// <summary>One completion is the staple grant (K is the ceiling); the contextHash keys on the
    /// output + a 1s bucket, so a four-piece recipe banks once rather than four times, and a
    /// genuine double-fire dedups.</summary>
    private static void CreditClayform(IPlayer byPlayer, string outputCode, double mult)
    {
        if (serverWorld == null || byPlayer == null) return;
        Core?.Ledger?.Log(byPlayer, PotDomain.Code, PotDomain.TechClayforming,
            HashCode.Combine("clayform", outputCode, serverWorld.ElapsedMilliseconds / 1000), mult);
    }

    // ------------------------------------------------------------ pottery-wheel variant

    /// <summary>The wheel completion is void and early-returns when unfinished, so capture whether
    /// a recipe was selected BEFORE the call; the postfix grants only if it cleared (completed).</summary>
    public static void WheelPrefix(BlockEntity __instance, out bool __state)
    {
        __state = __instance != null && Traverse.Create(__instance).Property("SelectedRecipe").GetValue() != null;
    }

    public static void WheelPostfix(BlockEntity __instance, IPlayer byPlayer, bool __state)
    {
        if (!__state || __instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        // Completed iff the selected recipe was consumed (ResetClayWheel nulls it on completion).
        if (Traverse.Create(__instance).Property("SelectedRecipe").GetValue() != null) return;
        CreditClayform(byPlayer, "wheel", PotConst.WheelRawFactor); // lower skill expression, reduced raw
    }

    // ------------------------------------------------------------ pit firing

    /// <summary>Freeze the igniter's identity + rank at ignition (owner-at-ignite ruling). A null
    /// igniter (an auto-relight on load) leaves any existing mark untouched.</summary>
    public static void IgnitePostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
        kilnOwners[PosKey(__instance.Pos)] =
            $"{byPlayer.PlayerUID}|{byPlayer.PlayerName}|{PotDomain.LevelOf(byPlayer)}";
        TcmLog.Cat(__instance.Api, "pot", $"kiln lit at {__instance.Pos} by {byPlayer.PlayerName} (POT {PotDomain.LevelOf(byPlayer)})");
    }

    /// <summary>Vanilla OnFired REPLACES each ware slot with a clone of its SmeltedStack, and a
    /// clone of a resolved recipe stack carries none of our attributes, so the former's mark has
    /// to be lifted off the raw ware before the call and put back after it. Capturing the raw
    /// collectible at the same time gives the postfix an exact conversion test, which replaces a
    /// code-prefix guess that read "crock-tan-raw" as already fired.</summary>
    public static void FiredPrefix(BlockEntity __instance, out (CollectibleObject? Raw, string? Mark)[]? __state)
    {
        __state = null;
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        if (Traverse.Create(__instance).Field("inventory").GetValue() is not IInventory inv) return;

        int n = Math.Min(4, inv.Count); // vanilla fires slots 0..3
        var captured = new (CollectibleObject?, string?)[n];
        for (int i = 0; i < n; i++)
        {
            var stack = inv[i]?.Itemstack;
            captured[i] = (stack?.Collectible, PotBonusPatches.PackOf(stack));
        }
        __state = captured;
    }

    /// <summary>Unattended completion. Vanilla only converts ware when IsValidPitKiln held, so if
    /// nothing fired we bank nothing (success gate for free). Two separate things happen here and
    /// they no longer share an owner: the FORMER's mark rides across the conversion, and the
    /// IGNITER is credited the firing verb.</summary>
    public static void FiredPostfix(BlockEntity __instance, (CollectibleObject? Raw, string? Mark)[]? __state)
    {
        if (__state == null || __instance?.Api?.Side != EnumAppSide.Server) return;

        // A slot converted iff its collectible changed. The mark travels with the ware regardless
        // of who lit the kiln (RULED 2026-08-13), so this runs before the igniter is even looked up.
        var inv = Traverse.Create(__instance).Field("inventory").GetValue() as IInventory;
        bool converted = false;
        if (inv != null)
        {
            for (int i = 0; i < __state.Length && i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack?.Collectible == null || __state[i].Raw == null) continue;
                if (stack.Collectible == __state[i].Raw) continue; // still raw: this slot did not fire
                converted = true;
                if (__state[i].Mark is string mark) PotBonusPatches.ApplyPacked(stack, mark);
            }
            if (converted) __instance.MarkDirty(true);
        }

        string key = PosKey(__instance.Pos);
        kilnOwners.TryGetValue(key, out string? packed);
        kilnOwners.Remove(key);

        if (!converted)
        {
            TcmLog.Cat(__instance.Api, "pot", $"kiln at {__instance.Pos} produced no fired ware (invalid/rained-out); nothing banked");
            return;
        }

        // Grant the firing verb to the igniter if still online (owner-at-ignite). Offline = lost,
        // like an unattended ANI birth whose owner logged off. An unknown igniter (an auto-relight
        // on load) costs the credit and nothing else; the marks are already carried.
        string[] p = packed?.Split('|') ?? Array.Empty<string>();
        IPlayer? owner = p.Length >= 3 ? sapi?.World.PlayerByUid(p[0]) : null;
        if (owner == null)
        {
            TcmLog.Cat(__instance.Api, "pot", $"kiln fired at {__instance.Pos}: igniter {(p.Length >= 2 ? p[1] : "unknown")} offline or unrecorded; firing credit lost (marks carried)");
            return;
        }
        Core?.Ledger?.Log(owner, PotDomain.Code, PotDomain.TechFiring,
            HashCode.Combine("firing", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (int)((serverWorld?.ElapsedMilliseconds ?? 0) / 600000)));
        TcmLog.Cat(__instance.Api, "pot", $"kiln fired at {__instance.Pos} -> firing credit for {owner.PlayerName}; formed marks carried");
    }
}

/// <summary>POT tuning constants that are not per-server config knobs.</summary>
internal static class PotConst
{
    /// <summary>The wheel-thrown clayforming co-grant factor: the wheel removes the voxel-by-voxel
    /// skill expression, so it banks a fraction of the freehand raw (xSkills helve precedent).</summary>
    public const double WheelRawFactor = 0.35;
}

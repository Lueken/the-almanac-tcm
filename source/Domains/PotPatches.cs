using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// POT Phase 1 — the two practice-granting verbs (technique-maps §POT, both [vanilla]).
///
///   • Clayforming (#1): a plain listener on the vanilla `onitemclayformed` event bus
///     (pushed once per produced piece in BlockEntityClayForm.CheckIfFinished) — zero
///     Harmony on the vanilla path. The pottery-wheel variant does NOT push that event,
///     so it gets a conditional reduced-raw postfix on the private
///     SimplePotteryWheel.ClayWheelEntity.CheckIfFinished (warns-and-skips if the mod
///     is absent).
///   • Pit firing (#2): a postfix on the unattended BlockEntityPitKiln.OnFired, which
///     converts ware to its SmeltedStack ONLY when IsValidPitKiln still holds — so the
///     grant is success-gated for free (a rained-out or breached firing skips the
///     convert and banks nothing). OnFired carries no player, so the igniter is captured
///     at TryIgnite into a persisted pos->owner map (the graft-owner pattern) and read
///     back at completion. The same owner+rank stamps the Potter's Mark (Phase 3).
/// </summary>
public static class PotPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static ICoreServerAPI? sapi;
    private static IWorldAccessor? serverWorld;

    /// <summary>Kiln pos -> the igniter's frozen mark, packed "uid|name|tier" (owner-at-ignite).
    /// OnFired fires unattended up to a full burn later, possibly across a restart, so this
    /// persists. Consumed at OnFired (grant + Potter's Mark stamp), then removed.</summary>
    private static Dictionary<string, string> kilnOwners = new();

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

        // Clayforming (#1, vanilla path): one event-bus listener, no patch. Fires once per
        // produced piece with the shaper's entity id.
        api.Event.RegisterEventBusListener(OnClayFormed, 0.5, "onitemclayformed");
        TcmLog.Info(api, "POT clayforming hooked (onitemclayformed event listener)");
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Pit firing (#2): capture the igniter at TryIgnite, grant + stamp at OnFired. Both are
        // DECLARED on BlockEntityPitKiln (the override rule does not bite here, but stay explicit).
        var tk = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityPitKiln");
        var mi = tk == null ? null : AccessTools.DeclaredMethod(tk, "TryIgnite");
        var mf = tk == null ? null : AccessTools.DeclaredMethod(tk, "OnFired");
        if (mi != null && mf != null)
        {
            harmony.Patch(mi, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(IgnitePostfix))));
            harmony.Patch(mf, postfix: new HarmonyMethod(AccessTools.Method(typeof(PotPatches), nameof(FiredPostfix))));
            TcmLog.Info(api, "POT pit firing hooked (TryIgnite owner capture + OnFired success-gated grant)");
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

    // ------------------------------------------------------------ clayforming (event bus)

    private static void OnClayFormed(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (serverWorld == null || data is not ITreeAttribute tree) return;
        long eid = tree.GetLong("byentityid");
        if (serverWorld.GetEntityById(eid) is not EntityPlayer ep || ep.Player == null) return;
        string code = tree.GetItemstack("itemstack")?.Collectible?.Code?.ToString() ?? "clay";
        CreditClayform(ep.Player, code, 1.0);
    }

    /// <summary>Each formed piece is the staple grant (K is the ceiling); the contextHash keys on
    /// the output + a 1s bucket so only a genuine double-fire dedups.</summary>
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

    /// <summary>Unattended completion. Vanilla only converts ware when IsValidPitKiln held, so if
    /// nothing fired we bank nothing (success gate for free). The igniter is credited the firing
    /// verb, and every fired keep-vessel takes the Potter's Mark (Phase 3) from the frozen rank.</summary>
    public static void FiredPostfix(BlockEntity __instance)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server) return;
        string key = PosKey(__instance.Pos);
        if (!kilnOwners.TryGetValue(key, out string? packed) || packed == null) return;
        kilnOwners.Remove(key);

        string[] p = packed.Split('|');
        if (p.Length < 3) return;
        string uid = p[0], name = p[1];
        int.TryParse(p[2], out int tier);

        // Did anything actually convert? Read the ware slots; a fired vessel is stampable, and a
        // still-raw slot (rained-out firing) means the grant is skipped.
        var inv = Traverse.Create(__instance).Field("inventory").GetValue() as IInventory;
        bool converted = false;
        if (inv != null)
        {
            for (int i = 0; i < 4 && i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack?.Collectible == null) continue;
                // A fired ware no longer has combustible SmeltedStack pointing onward from raw clay;
                // simplest honest signal that this slot converted: it is not a raw clay item.
                if (stack.Collectible.Code?.Path?.StartsWith("clay") == true) continue;
                converted = true;
                PotBonusPatches.StampFired(stack, uid, name, tier); // Potter's Mark (harmless on non-vessels)
            }
            if (converted) __instance.MarkDirty(true);
        }
        if (!converted)
        {
            TcmLog.Cat(__instance.Api, "pot", $"kiln at {__instance.Pos} produced no fired ware (invalid/rained-out); {name} banks nothing");
            return;
        }

        // Grant the firing verb to the igniter if still online (owner-at-ignite). Offline = lost,
        // like an unattended ANI birth whose owner logged off.
        IPlayer? owner = sapi?.World.PlayerByUid(uid);
        if (owner == null)
        {
            TcmLog.Cat(__instance.Api, "pot", $"kiln fired at {__instance.Pos}: igniter {name} offline; firing credit lost (mark still applied)");
            return;
        }
        Core?.Ledger?.Log(owner, PotDomain.Code, PotDomain.TechFiring,
            HashCode.Combine("firing", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (int)((serverWorld?.ElapsedMilliseconds ?? 0) / 600000)));
        TcmLog.Cat(__instance.Api, "pot", $"kiln fired at {__instance.Pos} -> firing credit for {name}; ware marked (tier {tier})");
    }
}

/// <summary>POT tuning constants that are not per-server config knobs.</summary>
internal static class PotConst
{
    /// <summary>The wheel-thrown clayforming co-grant factor: the wheel removes the voxel-by-voxel
    /// skill expression, so it banks a fraction of the freehand raw (xSkills helve precedent).</summary>
    public const double WheelRawFactor = 0.35;
}

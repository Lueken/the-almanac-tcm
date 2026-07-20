using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// MEL Phase 3 + 4 — the timed parry: rank-widened catch (P3) and the perfect-parry-to-pierce
/// (P4 Journeyman rung, RULED 2026-07-20). dependsOn combatoverhaulfork.
///
/// The parry window on the server is CurrentDamageBlock on PlayerDamageModelBehavior — set when
/// the client's startParry animation callback packet arrives, cleared when stopParry does. It is
/// a plain server-side property (OLC:35384 `{ get; set; }`), NOT a WatchedAttribute, so it is
/// never networked to any client. That is what makes rank-widening safe under gotcha #14: we do
/// NOT keep the client's parry state alive or re-drive it (that would strand the client FSM the
/// way the 0.3.118 firearm bug did). We only make a SERVER-SIDE acceptance decision — "a blow
/// that lands a rank-scaled beat after the parry closed still counts as parried" — by transiently
/// restoring the just-closed parry's stats for the single synchronous ApplyBlock resolution and
/// clearing them again in the postfix, before anything syncs.
///
/// P3 widen: Novice = the vanilla window (zero grace, per the anchor ruling); the grace climbs
/// to a modest cap at GM. The directional cone is NEVER touched — facing the blow stays the skill.
///
/// P4 perfect-pierce: a GENUINE in-window parry (never a graced late-catch) where the blow lands
/// within a FIXED tight window of the parry opening — a reactive just-frame — stamps CO's
/// per-stack armorPiercingBonus on the held weapon for the riposte window. The riposte then lands
/// as if a tier sharper (ArmorPiercingTier adds to the effective attack tier in the resist lookup,
/// OLC:34571), cutting through the foe's resist. NERF-FIRST clean: no damage stat, CO's own lever.
/// Rank scales the pierce DEPTH; the window is fixed (mastery is precision). Server-side timing:
/// the animation sync is confirmed good, so impact time ~ the visible strike; a private coop
/// server's low ping makes the parry-packet latency negligible, and it errs forgiving.
/// </summary>
public static class MelParryPatches
{
    private static ICoreServerAPI? sapi;

    private static Type? behType;          // PlayerDamageModelBehavior
    private static PropertyInfo? blockProp; // .CurrentDamageBlock
    private static FieldInfo? entityField;  // EntityBehavior.entity
    private static FieldInfo? kindField;    // DamageBlockStats.Kind

    /// <summary>defender entityId -> (just-closed parry stats, close time). One-shot: a closed
    /// parry graces at most one blow.</summary>
    private static readonly Dictionary<long, (object stats, long ms)> parryClosed = new();

    /// <summary>defender entityId -> the ms the current Parry window opened (for the perfect
    /// just-frame measure: blow lands soon after opening = reactive = perfect).</summary>
    private static readonly Dictionary<long, long> parryOpened = new();

    /// <summary>uid -> the pierce stamp riding a perfect riposte, so it can be reverted when the
    /// riposte window closes (never left on the weapon to leak into a later ordinary hit).</summary>
    private static readonly Dictionary<string, (ItemSlot slot, ItemStack stack, int original, long expiry)> pierceActive = new();

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.RegisterGameTickListener(ClearExpiredPierce, 100);
    }

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("combatoverhaulfork")) return;

        behType = AccessTools.TypeByName("CombatOverhaul.DamageSystems.PlayerDamageModelBehavior");
        var blockSys = AccessTools.TypeByName("CombatOverhaul.DamageSystems.MeleeBlockSystemServer");
        var startPkt = AccessTools.TypeByName("CombatOverhaul.DamageSystems.DamageBlockPacket");
        var stopPkt = AccessTools.TypeByName("CombatOverhaul.DamageSystems.DamageStopBlockPacket");
        var statsType = AccessTools.TypeByName("CombatOverhaul.DamageSystems.DamageBlockStats");

        var applyBlock = behType == null ? null : AccessTools.Method(behType, "ApplyBlock");
        var handleStart = blockSys == null || startPkt == null ? null
            : AccessTools.Method(blockSys, "HandlePacket", new[] { typeof(IServerPlayer), startPkt });
        var handleStop = blockSys == null || stopPkt == null ? null
            : AccessTools.Method(blockSys, "HandlePacket", new[] { typeof(IServerPlayer), stopPkt });

        if (applyBlock == null || handleStart == null || handleStop == null || statsType == null)
        {
            TcmLog.Warn(api, "CO present but parry seam not found; MEL parry-widen/pierce inactive");
            return;
        }

        blockProp = AccessTools.Property(behType, "CurrentDamageBlock");
        entityField = AccessTools.Field(typeof(EntityBehavior), "entity");
        kindField = AccessTools.Field(statsType, "Kind");
        if (blockProp == null || entityField == null || kindField == null)
        {
            TcmLog.Warn(api, "CO present but parry field shapes changed; MEL parry-widen/pierce inactive");
            return;
        }

        harmony.Patch(handleStart, postfix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "CaptureOpen")));
        harmony.Patch(handleStop, prefix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "CaptureClose")));
        harmony.Patch(applyBlock,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "GracePrefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "GracePostfix")));
        TcmLog.Info(api, "MEL parry hooked (rank-widen grace + perfect-parry pierce)");
    }

    /// <summary>The parry-catch grace by defender rank: 0 at Novice I (the vanilla window), linear
    /// to the GM cap. Below Novice gets 0 too (the Untrained NARROW penalty is a separate rung).</summary>
    private static long GraceMs(int level) =>
        (long)MelDomain.NoviceDelta(level, 0, MelDomain.Knob("parryGraceGmMs", 180));

    // --- capture: a Parry window just OPENED (its start packet set CurrentDamageBlock); stamp
    // the time so the perfect just-frame can measure how soon the blow lands after it.
    public static void CaptureOpen(IServerPlayer player)
    {
        if (sapi == null || player?.Entity == null) return;
        object? beh = FindBehavior(player.Entity);
        object? cur = beh == null ? null : blockProp!.GetValue(beh);
        if (cur == null || kindField!.GetValue(cur)?.ToString() != "Parry") return;
        parryOpened[player.Entity.EntityId] = sapi.World.ElapsedMilliseconds;
    }

    // --- capture: a Parry window is about to be cleared by its stop packet; remember it.
    public static void CaptureClose(IServerPlayer player)
    {
        if (sapi == null || player?.Entity == null) return;
        object? beh = FindBehavior(player.Entity);
        if (beh == null) return;
        object? cur = blockProp!.GetValue(beh);
        if (cur == null) return;
        if (kindField!.GetValue(cur)?.ToString() != "Parry") return; // parries only
        parryClosed[player.Entity.EntityId] = (cur, sapi.World.ElapsedMilliseconds);
    }

    // --- grace: the blow lands with the window already closed; restore it if within grace.
    public static void GracePrefix(object __instance, out bool __state)
    {
        __state = false;
        if (sapi == null || behType == null || !behType.IsInstanceOfType(__instance)) return;
        if (blockProp!.GetValue(__instance) != null) return; // window still open: a normal parry

        if (entityField!.GetValue(__instance) is not Entity ent) return;
        if (!parryClosed.TryGetValue(ent.EntityId, out var closed)) return;

        int level = MelDomain.LevelOf((ent as EntityPlayer)?.Player);
        long grace = GraceMs(level);
        if (grace <= 0 || sapi.World.ElapsedMilliseconds - closed.ms > grace) return;

        parryClosed.Remove(ent.EntityId);       // one-shot
        blockProp.SetValue(__instance, closed.stats); // transient, cleared in the postfix
        __state = true;
    }

    public static void GracePostfix(object __instance, bool __state, float damage)
    {
        if (__state)
        {
            // A graced late-catch is never "perfect" (it is a rank-forgiven save, the opposite
            // of a reactive just-frame). Just revert the transient restore and stop.
            blockProp!.SetValue(__instance, null);
            return;
        }
        TryPerfectParry(__instance, damage);
    }

    /// <summary>A genuine in-window parry (not graced) that fully caught the blow, within the
    /// fixed perfect window of the parry opening, at Journeyman I+, stamps armor-pierce on the
    /// held weapon for the riposte window.</summary>
    private static void TryPerfectParry(object beh, float damageAfter)
    {
        if (sapi == null || damageAfter > 0.01f) return;      // blow got through: not a clean catch
        object? cur = blockProp!.GetValue(beh);
        if (cur == null || kindField!.GetValue(cur)?.ToString() != "Parry") return; // parries only

        if (entityField!.GetValue(beh) is not EntityPlayer ep || ep.Player is not IServerPlayer player) return;
        if (!parryOpened.TryGetValue(ep.EntityId, out long opened)) return;

        long dt = sapi.World.ElapsedMilliseconds - opened;
        long window = (long)MelDomain.Knob(MelDomain.PerfectWindowMs, 150);
        if (dt < 0 || dt > window) return;                    // too slow: an ordinary parry

        int level = MelDomain.LevelOf(player);
        int pierce = MelDomain.PierceDepth(level);
        if (pierce <= 0) return;                              // below Journeyman I: not learned

        var slot = player.InventoryManager?.ActiveHotbarSlot;
        var stack = slot?.Itemstack;
        if (stack?.Collectible == null) return;               // nothing to riposte with

        int original = stack.Attributes.GetInt("armorPiercingBonus", 0);
        stack.Attributes.SetInt("armorPiercingBonus", original + pierce);
        slot!.MarkDirty();
        long expiry = sapi.World.ElapsedMilliseconds + (long)MelDomain.Knob(MelDomain.RiposteWindowMs, 300);
        pierceActive[player.PlayerUID] = (slot, stack, original, expiry);

        parryOpened.Remove(ep.EntityId);                      // one perfect per parry
        player.SendMessage(GlobalConstants.InfoLogChatGroup,
            Lang.GetL(player.LanguageCode, "almanactcm:perfect-parry"), EnumChatType.Notification);
        TcmLog.Cat(sapi, "combat", $"{player.PlayerName}: PERFECT parry ({dt}ms) -> riposte pierces +{pierce} (MEL {level})");
    }

    /// <summary>Reverts the perfect-pierce stamp when the riposte window closes, so it never
    /// lingers onto a later ordinary strike. Restores the stack's original value (not forced 0)
    /// in case a weapon or another buff carried a base armorPiercingBonus.</summary>
    private static void ClearExpiredPierce(float dt)
    {
        if (sapi == null || pierceActive.Count == 0) return;
        long now = sapi.World.ElapsedMilliseconds;
        List<string>? done = null;
        foreach (var kv in pierceActive)
        {
            if (now < kv.Value.expiry) continue;
            var (slot, stack, original, _) = kv.Value;
            if (slot?.Itemstack == stack && stack != null)
            {
                stack.Attributes.SetInt("armorPiercingBonus", original);
                slot.MarkDirty();
            }
            (done ??= new()).Add(kv.Key);
        }
        if (done != null) foreach (var uid in done) pierceActive.Remove(uid);
    }

    private static object? FindBehavior(Entity entity)
    {
        foreach (var b in entity.SidedProperties?.Behaviors ?? new List<EntityBehavior>())
            if (behType!.IsInstanceOfType(b)) return b;
        return null;
    }
}

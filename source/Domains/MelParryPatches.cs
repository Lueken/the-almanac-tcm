using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// MEL Phase 3 — the timed-parry catch window widened by rank (rank-bonus-design §MEL CO layer
/// Axis 3, RULED 2026-07-11). dependsOn combatoverhaulfork.
///
/// The parry window on the server is CurrentDamageBlock on PlayerDamageModelBehavior — set when
/// the client's startParry animation callback packet arrives, cleared when stopParry does. It is
/// a plain server-side property (OLC:35384 `{ get; set; }`), NOT a WatchedAttribute, so it is
/// never networked to any client. That is what makes rank-widening safe under gotcha #14: we do
/// NOT keep the client's parry state alive or re-drive it (that would strand the client FSM the
/// way the 0.3.118 firearm bug did). We only make a SERVER-SIDE acceptance decision — "a blow
/// that lands a rank-scaled beat after the parry closed still counts as parried" — by transiently
/// restoring the just-closed parry's stats for the single synchronous ApplyBlock resolution and
/// clearing them again in the postfix, before anything syncs. The client already played its parry
/// animation; it neither sees nor cares that the server counted a slightly-late blow.
///
/// Novice = the vanilla window (zero grace, per the anchor ruling); the grace climbs to a modest
/// cap at GM. The directional cone is NEVER touched — facing the blow stays the skill.
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

    public static void RegisterServer(ICoreServerAPI api) => sapi = api;

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("combatoverhaulfork")) return;

        behType = AccessTools.TypeByName("CombatOverhaul.DamageSystems.PlayerDamageModelBehavior");
        var blockSys = AccessTools.TypeByName("CombatOverhaul.DamageSystems.MeleeBlockSystemServer");
        var stopPkt = AccessTools.TypeByName("CombatOverhaul.DamageSystems.DamageStopBlockPacket");
        var statsType = AccessTools.TypeByName("CombatOverhaul.DamageSystems.DamageBlockStats");

        var applyBlock = behType == null ? null : AccessTools.Method(behType, "ApplyBlock");
        var handleStop = blockSys == null || stopPkt == null ? null
            : AccessTools.Method(blockSys, "HandlePacket", new[] { typeof(IServerPlayer), stopPkt });

        if (applyBlock == null || handleStop == null || statsType == null)
        {
            TcmLog.Warn(api, "CO present but parry seam not found; MEL parry-widen inactive");
            return;
        }

        blockProp = AccessTools.Property(behType, "CurrentDamageBlock");
        entityField = AccessTools.Field(typeof(EntityBehavior), "entity");
        kindField = AccessTools.Field(statsType, "Kind");
        if (blockProp == null || entityField == null || kindField == null)
        {
            TcmLog.Warn(api, "CO present but parry field shapes changed; MEL parry-widen inactive");
            return;
        }

        harmony.Patch(handleStop, prefix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "CaptureClose")));
        harmony.Patch(applyBlock,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "GracePrefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MelParryPatches), "GracePostfix")));
        TcmLog.Info(api, "MEL parry-widen hooked (server-side acceptance grace)");
    }

    /// <summary>The parry-catch grace by defender rank: 0 at Novice I (the vanilla window), linear
    /// to the GM cap. Below Novice gets 0 too (the Untrained NARROW penalty is a separate rung).</summary>
    private static long GraceMs(int level) =>
        (long)MelDomain.NoviceDelta(level, 0, MelDomain.Knob("parryGraceGmMs", 180));

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

    public static void GracePostfix(object __instance, bool __state)
    {
        if (__state) blockProp!.SetValue(__instance, null);
    }

    private static object? FindBehavior(Entity entity)
    {
        foreach (var b in entity.SidedProperties?.Behaviors ?? new List<EntityBehavior>())
            if (behType!.IsInstanceOfType(b)) return b;
        return null;
    }
}

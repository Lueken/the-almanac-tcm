using System;
using System.Collections.Generic;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>Server -> client "you are in combat with this creature" ping, driving the Duelist's
/// Eye learn-over-time (the overlay reveals a foe you actually FIGHT, not one you glance at
/// through terrain). Sent on any damage exchanged either direction.</summary>
[ProtoContract]
public class MelEngagedPacket
{
    [ProtoMember(1)] public long MobId;
}

/// <summary>
/// The shared MEL/RAN combat kill listener (technique-maps §MEL/§RAN, ruled 2026-07-08;
/// combat-gates-verification.md). One death hook, one classifier, both domains.
///
/// Classifier (shape-based, verified against BOTH vanilla and Combat Overhaul):
///   killer = damageSource.GetCauseEntity() ?? SourceEntity. When the killer is a player,
///   SourceEntity == killer means a direct blow = MEL (vanilla melee: SourceEntity=attacker,
///   CauseEntity=null, vsapi:134421; CO melee: both = attacker, OLC:32846). SourceEntity being
///   a DIFFERENT entity means a projectile carried the hit = RAN (vanilla: SourceEntity=arrow,
///   CauseEntity=FiredBy, vssurvival:95106; CO: SourceEntity=ProjectileEntity, CauseEntity=
///   attacker, OLC:27328 — CO's ProjectileEntity is NOT vanilla EntityProjectile, so a type
///   check would misclassify every shot on The Quire; the shape check cannot).
///
/// Ruled fences, all enforced here:
///   • PvP zero (MEL ruling 4): a player victim banks nothing, independent of AllowPvP.
///   • Livestock zero (MEL ruling 5): OwnerId/owned attrs OR domesticated OR generation >= 2.
///     (The captive-birth stamp joins the predicate when ANI's birth listener builds.)
///   • Bleed/DoT attribution (MEL ruling 6): a stored last-player-attacker at ReceiveDamage,
///     credited at death when the killing source has no player cause. Bleed is ON on The
///     Quire (BloodTrail), so bleed-out kills are a normal path, not an edge case.
///   • Difficulty-scaled raw (MEL ruling 1): drifter tier ladder, locusts cheap, bells dear —
///     the xSkills xpByType shape as rawMultiplier, base raw stays in config.
///   • Spawner fence: contextHash = target type + 64-block area, so a camp collapses to a
///     few contexts inside the dedup window while a roaming hunt banks each new ground.
///
/// Both branches live: RAN since 0.3.112, MEL since 0.3.122 (MelDomain registration). The
/// difficulty table is shared and reads the RAN knobs — one xpByType shape for both halves
/// of the combat pair.
/// </summary>
public static class MelRanKillPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    /// <summary>Bleed-out window: a stored player hit older than this no longer claims the
    /// kill (BloodTrail's longest stock bleed runs about a minute; rank-stretched trails are
    /// observer-side cosmetics and do not lengthen the actual bleed).</summary>
    private const long LastAttackerWindowMs = 90_000;

    private readonly record struct LastHit(string Uid, bool Ranged, long Ms);

    /// <summary>victim entityId -> the last player hit it took (uid, weapon shape, when).
    /// Cleaned by the slow prune sweep only — death handlers never remove entries, so
    /// every OnEntityDeath subscriber (HUN's hunting grant included) can read the store
    /// regardless of handler registration order.</summary>
    private static readonly Dictionary<long, LastHit> lastAttacker = new();

    /// <summary>The bleed-out attribution, shared: the last player to wound this entity
    /// inside the window, if any. HUN's kill handler uses this so a bleed-out kill still
    /// banks hunting practice and counts toward the species ledger.</summary>
    public static bool TryPeekLastAttacker(long entityId, out string uid)
    {
        if (sapi != null && lastAttacker.TryGetValue(entityId, out LastHit hit)
            && sapi.World.ElapsedMilliseconds - hit.Ms <= LastAttackerWindowMs)
        {
            uid = hit.Uid;
            return true;
        }
        uid = "";
        return false;
    }

    private static IServerNetworkChannel? engagedChannel;

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.OnEntityDeath += OnEntityDeath;
        api.Event.RegisterGameTickListener(PruneLastAttackers, 30_000);
        engagedChannel = api.Network.RegisterChannel("almanactcmmel").RegisterMessageType<MelEngagedPacket>();
    }

    private static void SendEngaged(IPlayer? player, long mobId)
    {
        if (player is IServerPlayer sp && engagedChannel != null)
            engagedChannel.SendPacket(new MelEngagedPacket { MobId = mobId }, sp);
    }

    // ------------------------------------------------------------ bleed attribution store

    /// <summary>Records the last player to land real damage on any creature. Postfix on the
    /// base Entity.ReceiveDamage — every override in the game calls down into it (verified
    /// vsapi:134500/:135043/:135959 all return base.ReceiveDamage), so one patch sees every
    /// delivery path, CO's included.</summary>
    [HarmonyPatch(typeof(Entity), nameof(Entity.ReceiveDamage))]
    public static class LastAttackerPatch
    {
        public static void Postfix(Entity __instance, DamageSource damageSource, float damage, bool __result)
        {
            if (!__result || damage <= 0f) return;
            if (__instance.World?.Side != EnumAppSide.Server) return;

            Entity? cause = damageSource?.GetCauseEntity() ?? damageSource?.SourceEntity;

            // Mob -> player: the hit player is now in combat with that creature.
            if (__instance is EntityPlayer victim && cause is EntityAgent aggressor && aggressor is not EntityPlayer)
            {
                SendEngaged(victim.Player, aggressor.EntityId);
                return;
            }

            if (__instance is not EntityAgent) return;
            if (cause is not EntityPlayer attacker || attacker.PlayerUID == null) return;

            // Player -> mob: record the wound (bleed attribution) AND engage the hitter.
            bool ranged = damageSource!.SourceEntity != null && damageSource.SourceEntity != cause;
            lastAttacker[__instance.EntityId] =
                new LastHit(attacker.PlayerUID, ranged, __instance.World.ElapsedMilliseconds);
            SendEngaged(attacker.Player, __instance.EntityId);
        }
    }

    private static void PruneLastAttackers(float dt)
    {
        if (sapi == null || lastAttacker.Count == 0) return;
        long now = sapi.World.ElapsedMilliseconds;
        List<long>? stale = null;
        foreach (var kv in lastAttacker)
            if (now - kv.Value.Ms > LastAttackerWindowMs) (stale ??= new()).Add(kv.Key);
        if (stale != null) foreach (long id in stale) lastAttacker.Remove(id);
    }

    // ------------------------------------------------------------ the kill

    private static void OnEntityDeath(Entity entity, DamageSource? damageSource)
    {
        if (sapi == null || entity == null) return;

        bool hadStored = lastAttacker.TryGetValue(entity.EntityId, out LastHit stored);

        if (entity is EntityPlayer) return;          // PvP zero, by construction (ruling 4)
        if (entity is not EntityAgent) return;       // falling blocks, item stacks: not combat
        if (IsCombatExcluded(entity)) return;        // livestock predicate (ruling 5)

        IPlayer? player = null;
        bool ranged = false;
        string via = "direct";

        Entity? cause = damageSource?.GetCauseEntity() ?? damageSource?.SourceEntity;
        if (cause is EntityPlayer killer)
        {
            player = killer.Player;
            ranged = damageSource!.SourceEntity != null && damageSource.SourceEntity != cause;
        }
        else if (cause == null && hadStored
            && sapi.World.ElapsedMilliseconds - stored.Ms <= LastAttackerWindowMs)
        {
            // Bleed-out / unattributed DoT: the killing source carries NO entity at all (the
            // bleed tick ships SourceEntity=null, CauseEntity=null), but a player landed the
            // wound inside the window — theirs (ruling 6), with the weapon shape of the hit
            // that started it. The cause==null gate matters: a wolf finishing a player-wounded
            // animal has cause=wolf, and that kill is the wolf's, never the player's.
            player = sapi.World.PlayerByUid(stored.Uid);
            ranged = stored.Ranged;
            via = "bleed-fallback";
        }
        if (player == null)
        {
            TcmLog.Cat(sapi, "combat", $"kill unattributed: {entity.Code?.FirstCodePart()} #{entity.EntityId}, " +
                $"cause={(cause == null ? "null" : cause.Code?.ToString() ?? "?")}, stored={(hadStored ? $"{stored.Uid} {(sapi.World.ElapsedMilliseconds - stored.Ms) / 1000}s ago" : "none")}");
            return; // wolves, falls, wild-on-wild: nobody's practice
        }
        TcmLog.Cat(sapi, "combat", $"kill: {entity.Code?.FirstCodePart()} #{entity.EntityId} -> " +
            $"{player.PlayerName} ({(ranged ? "RAN" : "MEL")}, {via})");

        double mult = DifficultyMult(entity);
        string species = entity.Code?.FirstCodePart() ?? "unknown";
        int ctx = HashCode.Combine(species,
            (int)(entity.ServerPos.X / 64), (int)(entity.ServerPos.Z / 64));

        if (ranged)
            Core?.Ledger?.Log(player, RanDomain.Code, RanDomain.TechShooting, ctx, mult);
        else
            Core?.Ledger?.Log(player, MelDomain.Code, MelDomain.TechFighting, ctx, mult);
    }

    // ------------------------------------------------------------ ruled fences

    /// <summary>The livestock exclusion (MEL ruling 5 / combat-gates-verification B6): owned or
    /// tamed, a domesticated variant, or an established captive lineage (gen 2+ — wild herds
    /// breed unaided to gen 1, so gen 1 proves nothing and stays fair game).</summary>
    private static bool IsCombatExcluded(Entity entity)
    {
        var wa = entity.WatchedAttributes;
        if (wa == null) return false;
        if (wa.GetBool("domesticated") || wa.HasAttribute("ownedby") || wa.HasAttribute("owner")) return true;
        return wa.GetInt("generation", 0) >= 2;
    }

    /// <summary>Quality-of-practice raw multiplier by target (MEL ruling 1, the xSkills
    /// xpByType shape). Wildlife and unlisted hostiles sit at 1.0; drifters climb by tier;
    /// locusts are chaff; bells are dear. Playtest tunes via the RAN.json knobs.</summary>
    private static double DifficultyMult(Entity entity)
    {
        string first = entity.Code?.FirstCodePart() ?? "";
        if (first == "drifter")
        {
            double step = RanDomain.Knob(RanDomain.RawDrifterTierStep, 0.5);
            int tier = entity.Code!.Path.Contains("double-headed") ? 5
                : entity.Code.Path.Contains("nightmare") ? 4
                : entity.Code.Path.Contains("corrupt") ? 3
                : entity.Code.Path.Contains("tainted") ? 2
                : entity.Code.Path.Contains("deep") ? 1
                : 0;
            return 1.0 + step * tier;
        }
        if (first.StartsWith("locust")) return RanDomain.Knob(RanDomain.RawLocustMul, 0.75);
        if (first.StartsWith("bell")) return RanDomain.Knob(RanDomain.RawBellMul, 2.0);
        return 1.0;
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

            bool ranged = damageSource!.SourceEntity != null && damageSource.SourceEntity != cause;

            // The training yard (RULED 2026-09-25, LGD-86): a straw dummy is equipment, not an
            // opponent, so a hit on one is a drill, never a wound. Routed BEFORE attribution and
            // engagement on purpose: no bleed credit, no combat-music engage, no kill setup.
            if (IsTrainingDummy(__instance))
            {
                TrainingHit(attacker.Player, ranged);
                return;
            }

            // Player -> mob: record the wound (bleed attribution) AND engage the hitter.
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
        if (IsTrainingDummy(entity)) return;         // equipment, not quarry (LGD-86): hits paid the drill; the death is pure loss

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

        // BRACES ARE LOAD-BEARING. Adding the counters below to an unbraced if/else silently
        // reparsed this whole block: the else bound to the inner distance test, so every melee
        // kill bumped "ran-kills", and MEL fighting was granted on any kill under 60 blocks,
        // paying a ranged kill BOTH callings. It compiled clean. Do not unbrace it.
        if (ranged)
        {
            Core?.Ledger?.Log(player, RanDomain.Code, RanDomain.TechShooting, ctx, mult);
            // The Marksman's book (0.5, the TEM storm-ledger shape): kill classification lived
            // only at this grant, so the ascension proof (a witnessed long kill) had nothing
            // durable to read. Two synced Knowledge counters, silent, no XP: every ranged kill,
            // and the long kill, past the Marksman's Eye's own reach (60 blocks).
            double dist = player.Entity.Pos.DistanceTo(entity.Pos.XYZ);
            BumpCounter(player, "ran-kills");
            if (dist >= LongKillBlocks) BumpCounter(player, "ran-long-kills");
        }
        else
        {
            Core?.Ledger?.Log(player, MelDomain.Code, MelDomain.TechFighting, ctx, mult);
        }

        // The temporal-kill co-grant (ruled 2026-08-21, A7 of the 0.5 gap pass): a rust-mob
        // kill pays the method its full practice above AND TEM half, sharing ctx so the two
        // credits dedup in step. The 0.5 rides mult, so DifficultyMult carries through:
        // a deep-tier drifter teaches more temporal awareness than a surface one, by ruling.
        // Direct call rather than a CoGrants config entry because the fan is conditional on
        // the TARGET, and CoGrants fans per technique, blind to what died.
        if (IsTemporalCreature(species))
            Core?.Ledger?.Log(player, TemDomain.Code, TemDomain.TechTemporalKill, ctx, mult * 0.5);

        // The arcane-kill co-grant (ruled 2026-09-19), the same shape one domain over: what
        // Rustbound Magic put in the world teaches the school that studies it, at TEM's ruled 50%
        // beside the method's full practice, sharing ctx so the two credits dedup in step. The
        // band multiplier is ARC's own (ArcaneKillMult), NOT DifficultyMult: a titan should be
        // worth more Arcana than a ravager without changing what a sword swing earns either of
        // them. No mod-present gate is needed here - nothing carries an rustboundmagic: code
        // unless RBM is loaded, so the predicate is false by construction without it.
        if (IsArcaneCreature(entity))
        {
            double band = ArcDomain.ArcaneKillMult(species);
            Core?.Ledger?.Log(player, ArcDomain.Code, ArcDomain.TechArcaneKill, ctx, mult * 0.5 * band);
            TcmLog.Cat(sapi, "combat", $"arcane kill: {species} -> {player.PlayerName} " +
                $"ARC/arcanekill x{mult * 0.5 * band:0.00} (band x{band:0.0})");
        }
    }

    /// <summary>The temporal bestiary, by first code part: the set DifficultyMult tiers plus
    /// the hostiles HunPatches excludes from the hunt (drifter/shiver/bowtorn/bell/locust).
    /// Wildlife and modded beasts are never temporal quarry.</summary>
    private static bool IsTemporalCreature(string first) =>
        first == "drifter" || first.StartsWith("shiver") || first.StartsWith("bowtorn")
        || first.StartsWith("bell") || first.StartsWith("locust");

    // ------------------------------------------------------------ the training yard (LGD-86)

    /// <summary>Vanilla's straw dummy, by code. Deliberately vanilla-only for now: a modded
    /// "dummy" earns its way onto this list by being reviewed, not by its name.</summary>
    private static bool IsTrainingDummy(Entity entity) =>
        entity?.Code?.FirstCodePart() == "strawdummy";

    /// <summary>Once-per-day-per-calling throttle for the graduation line.</summary>
    private static readonly Dictionary<string, int> dummyNothingSaid = new();

    /// <summary>The drill: each landed hit pays the calling's staple verb at
    /// dummyTrainMul x the training fade x the say-nay curve. Fade runs on the HITTING
    /// calling's own rank (a Novice archer still drills even if her sword arm is Master), and
    /// the say-nay scopes are separate pools per calling. Context is per-hit by elapsed ms:
    /// a drill IS repetition, so the 90s ring must not eat it; the day pool is the governor.
    /// When the fade has ended and the player keeps swinging, the Almanac says why, once a
    /// day: the straw has nothing left to teach.</summary>
    private static void TrainingHit(IPlayer? player, bool ranged)
    {
        if (player == null || sapi == null) return;
        string domain = ranged ? RanDomain.Code : MelDomain.Code;
        int level = ranged ? RanDomain.LevelOf(player) : MelDomain.LevelOf(player);
        double fade = MelDomain.DummyFade(level);
        int day = (int)sapi.World.Calendar.TotalDays;

        if (fade <= 0)
        {
            string key = player.PlayerUID + "|" + domain;
            if (dummyNothingSaid.TryGetValue(key, out int last) && last == day) return;
            dummyNothingSaid[key] = day;
            (player as IServerPlayer)?.SendMessage(GlobalConstants.InfoLogChatGroup,
                Lang.GetL((player as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:dummy-nothing-left"),
                EnumChatType.Notification);
            return;
        }

        double mult = MelDomain.Knob(MelDomain.DummyTrainMul, 0.5) * fade
            * Engine.RepeatDecay.Mult(player.PlayerUID, domain + ":dummy", day,
                (int)MelDomain.Knob(MelDomain.DummyTrainFree, 30), 0.1);

        Core?.Ledger?.Log(player, domain,
            ranged ? RanDomain.TechShooting : MelDomain.TechFighting,
            HashCode.Combine("dummy", sapi.World.ElapsedMilliseconds), mult, announceRepeat: false);
    }

    /// <summary>The arcane bestiary: the seven Rustbound Magic creatures that spawn hostile in the
    /// world, verified against RBM 4.0.4's own assets (all seven carry spawnconditions and
    /// "group": "hostile"; health runs ravager 10, sentry 14, watcher 14, drone 16, colossus 100,
    /// titan 100, sentinel 500).
    ///
    /// An ALLOWLIST, and domain-scoped, for two reasons. RBM also ships things that die and must
    /// never pay: twelve companion variants (all sharing code "companion"), the wisp familiar, and
    /// polymorphedentity, which is an ordinary creature wearing a spell rather than a creature of
    /// RBM's own. And FirstCodePart() drops the domain, so a bare name test would hand ARC practice
    /// to any other mod that happens to name something "elementaldrone".</summary>
    private static bool IsArcaneCreature(Entity entity)
    {
        var code = entity.Code;
        if (code?.Domain != "rustboundmagic") return false;
        string first = code.FirstCodePart();
        return first == "elementalcolossus" || first == "elementaldrone"
            || first == "elementalravager" || first == "elementalsentinel"
            || first == "elementalsentry" || first == "elementaltitan"
            || first == "entitywatcher";
    }

    // ------------------------------------------------------------ ruled fences

    /// <summary>The livestock exclusion (MEL ruling 5 / combat-gates-verification B6): owned or
    /// tamed, a domesticated variant, or an established captive lineage (gen 2+ — wild herds
    /// breed unaided to gen 1, so gen 1 proves nothing and stays fair game).
    /// INTERNAL since 0.5 (2026-08-21): HUN's kill credit reuses this exact fence, because the
    /// two ledgers read the same death event and a beast that is not combat quarry is not hunt
    /// quarry either. One definition, or the two fences drift (the pre-0.5 state: HUN checked
    /// only domesticated/ownedby/owner, so penned gen-2 slaughter banked wild-kill practice).</summary>
    /// <summary>Long-kill threshold in blocks: past the Marksman's Eye's own 60-block reach,
    /// so no aiming aid can have helped the shot. (42 was wrong: that is HUNTING's GM scan
    /// ceiling, not the Eye's; corrected 2026-08-22 before anything shipped.)</summary>
    private const double LongKillBlocks = 60.0;

    /// <summary>Increment a synced Knowledge-store counter by one, silently (the TEM pattern).</summary>
    private static void BumpCounter(IPlayer player, string key)
    {
        var server = AlmanacTcmModSystem.ServerInstance?.Server;
        var set = server?.GetDomainSet(player);
        if (server == null || set == null) return;
        int cur = set.Knowledge.TryGetValue(key, out int v) ? v : 0;
        server.SetKnowledge(player, key, cur + 1);
    }

    internal static bool IsCombatExcluded(Entity entity)
    {
        var wa = entity.WatchedAttributes;
        if (wa == null) return false;
        if (wa.GetBool("domesticated") || wa.HasAttribute("ownedby") || wa.HasAttribute("owner")) return true;
        // Rustbound Magic keeps ownership under its own keys, so the vanilla three never saw it
        // (found 2026-09-19 while wiring the arcane-kill grant, by reading RBM 4.0.4's assembly:
        // "companionownerplayeruid_rm" and "entity-watchedattribute-petowner-id_rm"). Until now a
        // player could kill their own summoned skeleton and bank MEL or RAN for it. A minion is
        // somebody's, exactly like a tamed animal, so it belongs on this fence rather than on a
        // new one, and putting it here closes the hole for every ledger that reads this death.
        if (wa.HasAttribute("companionownerplayeruid_rm")
            || wa.HasAttribute("entity-watchedattribute-petowner-id_rm")) return true;
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

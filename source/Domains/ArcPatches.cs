using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// ARC — Stage 1 (arc-design-study.md annex §8 items 1-2): the RE-ROOT + the casting/meditation grants.
/// This is the fundamental change — RBM's own casting-XP mana grind is FROZEN, and the mana pool becomes
/// a function of ARC rank.
///
///   • The re-root [reconcile, 2s] — per mage, write `playermaxmana_rm` (the base pool) to the ARC-rank
///     FLOOR (§2), freeze `currentexptonextmaxmanalevel_rm` at 0 so RBM never levels the base from casting,
///     and recompute the effective `totalmaxmana_rm` = floor + `researchedmaxmana_rm` + `armormaxmana_rm`
///     (research + gear STACK ON TOP, untouched — verified RBM 3.2.5 pool structure). Current mana is
///     clamped down if it now exceeds the total.
///   • Casting grant [SpellBase.ConsumeManaForSpell postfix] — the single server-side cast point (skips
///     scroll casts, so scroll-buyers never earn ARC). Grants the school verb (evocation/alteration/
///     incantation/conjuration, else foundational) weighted by spell tier, and re-wipes the cast's XP add.
///   • Meditation trickle [reconcile] — a small ARC practice grant while `meditation-active_rm` is set.
///
/// Conditional on rustboundmagic (live on The Quire); the reconcile no-ops without it, the cast patch is
/// reflected + isolated. Later stages add the tier-gate + backfire, the meditation ladder, school-depth
/// stats, the laboratory + inscription verbs, and the scroll/aliasing guards.
/// </summary>
public static class ArcPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;
    private static bool rbmPresent;

    /// <summary>Per-player last ARC level applied to the rank stats (manaregen), so those rewrite only on
    /// rank change (the FOR/TEM cache pattern).</summary>
    private static readonly Dictionary<string, int> lastLevel = new();

    /// <summary>Per-player-per-school last familiarity rank applied to the RBM cost stat ("uid|technique"
    /// → rank), so the cost discount rewrites only when a school's rank actually changes.</summary>
    private static readonly Dictionary<string, int> lastSchoolRank = new();

    // ------------------------------------------------------------ registration + reconcile

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        rbmPresent = api.ModLoader.IsModEnabled("rustboundmagic");
        if (!rbmPresent)
        {
            TcmLog.Cat(api, TcmLog.Config, "ARC: rustboundmagic absent -> domain dormant (no re-root, no grants)");
            return;
        }
        api.Event.RegisterGameTickListener(_ => Reconcile(api), 2000);

        // Snap the re-root the moment a player's domain data is ready (join/load), then again 3s later to
        // land after RBM finishes initialising the mana attribute low — otherwise the pool sits at the
        // level-0 floor until a later 2s tick happens to win the load race.
        var server = AlmanacTcmModSystem.ServerInstance?.Server;
        if (server != null)
        {
            server.DomainSetReady += (sp, _) =>
            {
                ApplyReRoot(sp);
                api.Event.RegisterCallback(_ => ApplyReRoot(sp), 3000);
            };
        }
        TcmLog.Info(api, "ARC hooks live (mana re-root to ARC rank, cast grants, meditation trickle)");
    }

    /// <summary>Pin one mage's base mana pool to the ARC-rank floor and freeze the casting XP. The core
    /// re-root, factored out so it can run ON DEMAND (domain-ready on join, /tcm setlevel) and snap the
    /// pool to the rank immediately, instead of only on the 2s tick — which on load loses a race with RBM
    /// re-initialising the mana attribute low, so the pool sat at the level-0 floor until a later tick
    /// happened to win (the "41 on load / set it and wait a minute" bug). Returns false when the player is
    /// not a mage yet (no mana attribute), so the tick can skip the rest of the per-mage work.
    ///
    /// Writes ONLY playermaxmana_rm (the base). RBM owns the effective total = base + 9 + gear + research;
    /// writing the total ourselves (and missing the +9) was the 0.3.172 flicker, and RBM does not fight the
    /// base per-tick (it only writes it on discrete events), so a bare base write is safe and sticks.</summary>
    public static bool ApplyReRoot(IServerPlayer player)
    {
        var wa = player?.Entity?.WatchedAttributes;
        if (wa == null || !wa.HasAttribute(ArcDomain.AttrPlayerMaxMana)) return false;  // not a mage yet

        int basePool = ArcDomain.BasePool(ArcDomain.LevelOf(player));  // = ManaFloor(level) - RBM's +9

        // Freeze the casting XP so RBM never levels the base from casting (the ONLY thing we throttle;
        // Meditation-Insight research is exploration-earned and left alone, stacking on top like gear).
        if (wa.GetFloat(ArcDomain.AttrXpToNextLevel, 0f) != 0f)
        {
            wa.SetFloat(ArcDomain.AttrXpToNextLevel, 0f);
            wa.MarkPathDirty(ArcDomain.AttrXpToNextLevel);
        }
        if (wa.GetInt(ArcDomain.AttrPlayerMaxMana, 0) != basePool)
        {
            wa.SetInt(ArcDomain.AttrPlayerMaxMana, basePool);
            wa.MarkPathDirty(ArcDomain.AttrPlayerMaxMana);
        }
        return true;
    }

    // Hardening (§8 #9) — the aliasing bug. Delirium1/Immobilize1/Terror1 are Tier-3 CC spells, but RBM's
    // constructors read the LOWER-tier DAZE1/ROOT1/FEAR1 cost keys (copy-paste bug), so they cast at the
    // cheap aliased cost instead of the intended 160 — a "T3 CC at half price" exploit. RULED: hard-set to
    // 160. One-shot correction of the three FullSpellDictionary entries; retried from the reconcile until
    // RBM has populated the dict, so it is immune to mod load-order. Fixes the server dict, which is what
    // both RBM's cost deduction and our tier-gate read (client tooltip may still show the stale number).
    public const int AliasedT3Cost = 160;
    private static readonly string[] AliasedT3Spells = { "delirium1", "immobilize1", "terror1" };
    private static bool aliasCostFixed;

    private static void ApplyAliasCostFix(ICoreAPI api)
    {
        if (aliasCostFixed) return;
        var rbmMain = AccessTools.TypeByName("rustboundmagic.src.RustboundMagic");
        var dictField = rbmMain == null ? null : AccessTools.Field(rbmMain, "FullSpellDictionary");
        if (dictField?.GetValue(null) is not System.Collections.IDictionary dict) return;  // RBM not ready — retry next tick
        if (!dict.Contains(AliasedT3Spells[0])) return;                                     // dict not populated yet — retry

        int corrected = 0;
        foreach (string key in AliasedT3Spells)
        {
            if (!dict.Contains(key) || dict[key] is not object spell) continue;
            var mc = Traverse.Create(spell).Property("ManaCost");
            if (mc.GetValue<int>() != AliasedT3Cost) { mc.SetValue(AliasedT3Cost); corrected++; }
        }
        aliasCostFixed = true;
        TcmLog.Info(api, $"ARC aliasing-bug fix applied ({corrected} T3 CC spells -> {AliasedT3Cost} mana)");
    }

    /// <summary>The re-root + meditation trickle, per mage, every 2s (the steady-state maintainer; the
    /// instant application lives in ApplyReRoot, fired on join + setlevel).</summary>
    private static void Reconcile(ICoreServerAPI api)
    {
        ApplyAliasCostFix(api);   // one-shot once RBM's spell dict is ready (§8 #9)
        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            if (!ApplyReRoot(player)) continue;  // base + XP-freeze; false = not a mage yet

            var wa = player.Entity.WatchedAttributes;
            int level = ArcDomain.LevelOf(player);

            // Stage 2a — the meditation regen ladder: a master recovers mana faster (the manaregen stat,
            // which RBM's client-side regen reads). Only on rank change. We SET our key when the bonus is
            // positive and REMOVE it otherwise — never Set 0, which could zero regen if the stat has no
            // base (RBM's regen = (int)GetBlended("manaregen") + focusItem).
            if (!lastLevel.TryGetValue(player.PlayerUID, out int prev) || prev != level)
            {
                double regen = ArcDomain.ManaRegenBonus(level);
                if (regen > 0) player.Entity.Stats.Set("manaregen", "almanactcm", (float)regen, persistent: true);
                else player.Entity.Stats.Remove("manaregen", "almanactcm");
                lastLevel[player.PlayerUID] = level;
            }

            // Stage 2b — per-school cost discount by familiarity rank. Write only when a school's rank
            // changes (Set the negative delta when there is a discount; Remove otherwise so an untrained /
            // Novice school stays RBM's exact 1.0 identity — never Set 0, which would still register a
            // contribution). RBM sums our delta onto the 1.0 base and floors the modified cost at 1.
            var dset = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
            foreach (string tech in ArcDomain.SchoolTechniques)
            {
                int channeled = 0;
                dset?.Knowledge.TryGetValue(ArcDomain.SchoolFamKey(tech), out channeled);
                int rank = ArcDomain.SchoolFamRank(channeled);
                string cacheKey = player.PlayerUID + "|" + tech;
                if (lastSchoolRank.TryGetValue(cacheKey, out int prevRank) && prevRank == rank) continue;
                lastSchoolRank[cacheKey] = rank;

                string stat = ArcDomain.SchoolCostStat(tech);
                double delta = ArcDomain.SchoolCostDelta(rank);
                if (delta < 0) player.Entity.Stats.Set(stat, "almanactcm", (float)delta, persistent: true);
                else player.Entity.Stats.Remove(stat, "almanactcm");
            }

            // Meditation trickle: while the trance is active, a small steady ARC practice grant (deduped
            // per world-minute so it is a slow drip, not per-tick spam).
            // announceRepeat:false — this passive drip fires every 2s but only earns once per real-minute
            // (the hash is minute-bucketed), so without this the Info tab floods with "nothing new learned".
            // The real +0.6/minute gain still shows; only the throttled dupes are silenced.
            if (wa.GetBool(ArcDomain.AttrMeditationActive, false))
                Core?.Ledger?.Log(player, ArcDomain.Code, ArcDomain.TechMeditation,
                    HashCode.Combine("meditate", (int)(api.World.ElapsedMilliseconds / 60000)), 0.6, announceRepeat: false);
        }
    }

    // ------------------------------------------------------------ casting grant (conditional)

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        var spellBase = AccessTools.TypeByName("rustboundmagic.src.system.interfaces.SpellBase");
        // Disambiguate the (EntityPlayer, string, bool) overload from the (EntityPlayer, string, int) one.
        var cast = spellBase == null ? null : AccessTools.Method(spellBase, "ConsumeManaForSpell",
            new[] { typeof(EntityPlayer), typeof(string), typeof(bool) });
        if (cast != null)
        {
            harmony.Patch(cast, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(CastPostfix))));
            TcmLog.Info(api, "ARC casting grant hooked (ConsumeManaForSpell, by school/tier; scrolls no-op)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ARC cast seam absent (rustboundmagic); casting grant inactive");

        // Stage 2b — the meditation ladder's drain-DOWN half (§4): reduce a ranked mage's temporal-stability
        // drain at the single server-side drain-packet chokepoint (the design's named seam). This is meditation-
        // dominant but also eases magic's other stability costs — a master is more temporally grounded. Floored.
        var netType = AccessTools.TypeByName("rustboundmagic.src.system.NetworkMessageRM");
        var drainHandler = netType == null ? null : AccessTools.Method(netType, "ServerPacketPlayerTemporalStabilityDrain");
        if (drainHandler != null)
        {
            harmony.Patch(drainHandler, prefix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(DrainReducePrefix))));
            TcmLog.Info(api, "ARC meditation drain-reduction hooked (temporal-stability drain scales down by rank)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ARC drain-packet seam absent (rustboundmagic); drain-per-rank inactive");

        // Stage 2b — the INSCRIPTION verb (§5): scribing a scroll is ARC's producer/market act. Grant on the
        // single scroll-creation point (RunCreateSpellcastingLacrima, one caller, runs only when an inscription
        // completes, with the scriber in hand). Scroll-USE still earns nothing (that path is ConsumeManaForSpell
        // with isspellscroll=true, which CastPostfix already skips) — only the producer earns ARC.
        var spellTool = AccessTools.TypeByName("rustboundmagic.src.common.item.tool.ItemToolSpellcastingRM");
        var inscribe = spellTool == null ? null : AccessTools.Method(spellTool, "RunCreateSpellcastingLacrima",
            new[] { typeof(EntityPlayer) });
        if (inscribe != null)
        {
            harmony.Patch(inscribe, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(InscribePostfix))));
            TcmLog.Info(api, "ARC inscription grant hooked (scroll scribing -> inscription verb)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ARC inscription seam absent (rustboundmagic); inscription grant inactive");

        // Stage 2b — the LABORATORY verb (§5): station ritual work. The one clean, real-gameplay ritual is
        // the Spellforge spell-DISCOVERY (the 90/75/50 research bench). Grant on the ATTEMPT — the labor IS
        // the practice; the RNG is the design's native risk, not a practice gate. Tier-weighted, chunky.
        // (Oculus grimoire synthesis turned out to be the admin tool; Foundry processing has no player at
        // completion — so the research bench carries the verb for now.)
        var spellforge = AccessTools.TypeByName("rustboundmagic.src.common.blockentity.station.BlockEntityStationSpellforgeRM");
        var discover = spellforge == null ? null : AccessTools.Method(spellforge, "RunAttemptToDiscoverSpell",
            new[] { typeof(EntityPlayer), typeof(string), typeof(int), typeof(string) });
        if (discover != null)
        {
            harmony.Patch(discover, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(DiscoverPostfix))));
            TcmLog.Info(api, "ARC laboratory grant hooked (Spellforge spell-discovery)");
        }
        else TcmLog.Cat(api, TcmLog.Config, "ARC laboratory seam absent (rustboundmagic); laboratory grant inactive");
    }

    /// <summary>Grant the ARC laboratory verb for Spellforge research (a spell-discovery attempt). Fires on
    /// the attempt regardless of the 90/75/50 outcome — the ritual labor is the practice. Tier-weighted.</summary>
    public static void DiscoverPostfix(EntityPlayer playerIn, int tierIn)
    {
        if (playerIn?.World?.Side != EnumAppSide.Server) return;
        var player = playerIn.Player;
        if (player == null) return;
        Core?.Ledger?.Log(player, ArcDomain.Code, ArcDomain.TechLaboratory,
            System.HashCode.Combine("labdiscover", tierIn, (int)(playerIn.World.ElapsedMilliseconds / 1000)),
            ArcDomain.CastWeight(tierIn));
    }

    /// <summary>Grant the ARC inscription verb when a mage scribes a scroll (the producer-side market act).
    /// Chunky per-scroll (the config Raw carries the weight); the per-second context lets a scribing run of
    /// several scrolls each register while collapsing sub-second dupes.</summary>
    public static void InscribePostfix(EntityPlayer playerIn)
    {
        if (playerIn?.World?.Side != EnumAppSide.Server) return;
        var player = playerIn.Player;
        if (player == null) return;
        Core?.Ledger?.Log(player, ArcDomain.Code, ArcDomain.TechInscription,
            System.HashCode.Combine("inscribe", (int)(playerIn.World.ElapsedMilliseconds / 1000)));
    }

    /// <summary>Scale down the temporal-stability drain a ranked mage takes (meditation trance + magic's
    /// stability costs) by ARC rank, at RBM's drain-packet chokepoint. The packet carries only the amount,
    /// so this eases all RBM stability drains for the mage — meditation-dominant, floored (never free).</summary>
    public static void DrainReducePrefix(IPlayer fromPlayer, object networkMessage)
    {
        if (fromPlayer?.Entity?.World?.Side != EnumAppSide.Server || networkMessage == null) return;
        double mul = ArcDomain.DrainMul(ArcDomain.LevelOf(fromPlayer));
        if (mul >= 1.0) return;
        var f = Traverse.Create(networkMessage).Field("inputDoubleTSToDrain");
        f.SetValue(f.GetValue<double>() * mul);
    }

    /// <summary>Grant ARC at a real cast (not a scroll). __instance is the SpellBase being cast — read its
    /// School (string) + Tier off it directly. The school picks the verb, the tier weights the grant. Also
    /// re-wipe the cast's XP add so nothing accumulates toward an RBM level-up between reconciles.</summary>
    public static void CastPostfix(object __instance, EntityPlayer byPlayer, bool isspellscrollIn)
    {
        if (isspellscrollIn || byPlayer?.World?.Side != EnumAppSide.Server) return;
        var player = byPlayer.Player;
        if (player == null || __instance == null) return;

        var t = Traverse.Create(__instance);
        string? school = t.Property("School").GetValue<string>();
        int tier = t.Property("Tier").GetValue<int>();
        int cost = t.Property("ManaCost").GetValue<int>();

        string tech = ArcDomain.TechniqueForSchool(school);
        int level = ArcDomain.LevelOf(player);
        Core?.Ledger?.Log(player, ArcDomain.Code, tech,
            HashCode.Combine("cast", school ?? "", tier, (int)(byPlayer.World.ElapsedMilliseconds / 1000)),
            ArcDomain.CastWeight(tier));

        // Stage 2b — school familiarity: channel this cast's mana into THAT school's cumulative practice
        // (the four real schools only). Stored in the synced Knowledge store (writes + persists + syncs in
        // one SetKnowledge call, so the Codex reads it for free); capped at the Master threshold. A rank-up
        // pings the caster. The reconcile turns the resulting rank into that school's cost discount.
        string famKey = ArcDomain.SchoolFamKey(tech);
        var server = Core?.Server;
        if (famKey.Length > 0 && cost > 0 && server != null)
        {
            var set = server.GetDomainSet(player);
            if (set != null)
            {
                int prev = set.Knowledge.TryGetValue(famKey, out int cur) ? cur : 0;
                int cap = ArcDomain.SchoolFamThreshold(ArcDomain.SchoolFamMaxRank);
                int next = System.Math.Min(prev + cost, cap);
                if (next != prev)
                {
                    server.SetKnowledge(player, famKey, next);
                    int prevRank = ArcDomain.SchoolFamRank(prev);
                    int nextRank = ArcDomain.SchoolFamRank(next);
                    if (nextRank > prevRank)
                        (player as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                            Lang.Get("almanactcm:arc-school-rankup",
                                Lang.Get("almanactcm:arc-school-" + tech), ArcDomain.SchoolRankName(nextRank)),
                            EnumChatType.Notification);
                }
            }
        }

        // Stage 2a — the tier-gate BACKFIRE (§3): reaching above your rank-tier does not REFUSE the cast
        // (the spell may even fire) — the overreach tears at time. An over-tier cast rolls a backfire; on a
        // hit, drain the caster's temporal stability, scaled by how far over they reached. "An apprentice
        // can reach for the storm, and the storm reaches back." The 450 ultimates keep a GM residual.
        var wa = byPlayer.WatchedAttributes;
        int overBy = ArcDomain.RequiredRankTier(tier, cost) - ArcDomain.PlayerRankTier(level);
        bool ultimate = cost >= ArcDomain.UltimateCostThreshold;
        double chance = ArcDomain.BackfireChance(overBy, ultimate);
        if (chance > 0 && byPlayer.World.Rand.NextDouble() < chance)
        {
            double drain = ArcDomain.Knob(ArcDomain.BackfireDrainPerTier, 0.08) * System.Math.Max(1, overBy);
            double stab = wa.GetDouble("temporalStability", 1.0);
            wa.SetDouble("temporalStability", System.Math.Max(0.0, stab - drain));
            wa.MarkPathDirty("temporalStability");
            (player as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("almanactcm:arc-backfire"), EnumChatType.Notification);
        }

        // Belt-and-suspenders freeze: undo this cast's XP add immediately (the reconcile also wipes it).
        if (wa.GetFloat(ArcDomain.AttrXpToNextLevel, 0f) != 0f)
        {
            wa.SetFloat(ArcDomain.AttrXpToNextLevel, 0f);
            wa.MarkPathDirty(ArcDomain.AttrXpToNextLevel);
        }
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
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
///   • The meditation trance [reconcile] — an OUTCOME-normalized practice grant while
///     `meditation-active_rm` is set, paid for the mana the trance actually restored as a fraction of
///     the pool (TickTrance), not for time spent sitting.
///   • The laboratory verb [Spellforge research + the world-magic XP choke point + the Thaumic
///     Foundry] — station work, credited wherever RBM leaves a player in scope (PatchLaboratoryStations).
///
/// Conditional on rustboundmagic (live on The Quire); the reconcile no-ops without it, the cast patch is
/// reflected + isolated. Later stages add the scroll guards and whatever the trance ladder still owes.
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
        // Drop a leaver's trance accounting so the map cannot grow across a long uptime, and so a
        // rejoin re-baselines its mana reading instead of trusting a stale one.
        api.Event.PlayerDisconnect += p => tranceStates.Remove(p.PlayerUID);

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

            TickTrance(api, player, wa);
        }
    }

    // ------------------------------------------------------------ the meditation trance (§4)

    /// <summary>How often a trance banks what it has restored. Long enough that one grant is a real
    /// chunk of the pool, short enough that a player sees the Info tab move inside a sitting.</summary>
    private const int TranceGrantIntervalMs = 20000;

    /// <summary>Hard ceiling on the trance multiplier. The math can only exceed 1 whole pool per 20s
    /// if RBM's mana attributes glitch (a pool that shrinks mid-trance, an attribute reinitialised
    /// low); this makes such a glitch a capped over-grant instead of a rank.</summary>
    private const double TranceMultiplierCeiling = 10.0;

    /// <summary>One meditating player's trance accounting. Kept in memory only: a trance interrupted by
    /// a restart simply starts over, which is the same thing the player experiences anyway.</summary>
    private sealed class TranceState
    {
        /// <summary>Last observed current-mana, re-baselined EVERY tick (trance or not) so entering a
        /// trance never books the mana recovered before it as one enormous first delta.</summary>
        public int LastMana;
        /// <summary>Mana restored during the trance since the last bank. Only positive deltas land
        /// here — spending mana mid-trance must not subtract the practice already earned.</summary>
        public double Restored;
        public long LastGrantMs;
        /// <summary>Monotonic grant counter, mixed into the context hash. The 20s bucket alone is not
        /// enough: a trance that ends moments after a scheduled grant banks its remainder inside the
        /// SAME bucket, and the ledger's dedup ring would silently zero it. Each bank is a distinct
        /// real event, so each gets a distinct context.</summary>
        public int Grants;
    }

    private static readonly Dictionary<string, TranceState> tranceStates = new();

    /// <summary>The meditation grant, outcome-normalized (§4). The old shape paid a flat 0.6 raw per
    /// real minute while `meditation-active_rm` was set — a number that, against K=40, no player could
    /// feel, and that paid the same whether the trance actually restored anything or the player sat at
    /// a full pool. This pays for the RESULT: the mana the trance put back, as a fraction of the pool.
    ///
    ///   multiplier = (restored since the last bank / effective max mana) * MeditationTranceRaw
    ///
    /// With Raw=1 on the technique, a full empty-to-full trance therefore banks ~25 raw against K=40 —
    /// a visible step, at ANY rank, because the fraction self-scales as the pool grows. No dynamic K,
    /// no per-rank table. A mage sitting at a full pool earns nothing, which is correct: there is no
    /// trance to have.
    ///
    /// The denominator is the EFFECTIVE pool (`totalmaxmana_rm`), not the base `playermaxmana_rm` ARC
    /// re-roots: current mana fills toward the effective total (RBM clamps to it at :24928/:25728), and
    /// dividing by the base would over-report the fraction by exactly the gear + research + starting-9
    /// stack a well-equipped mage carries. The base + RbmStartingMana is the fallback for the tick
    /// before RBM has computed a total.</summary>
    private static void TickTrance(ICoreServerAPI api, IServerPlayer player, ITreeAttribute wa)
    {
        int current = wa.GetInt(ArcDomain.AttrCurrentMana, 0);
        long now = api.World.ElapsedMilliseconds;

        if (!tranceStates.TryGetValue(player.PlayerUID, out TranceState? st))
            tranceStates[player.PlayerUID] = st = new TranceState { LastMana = current, LastGrantMs = now };

        int delta = current - st.LastMana;
        st.LastMana = current;

        if (!wa.GetBool(ArcDomain.AttrMeditationActive, false))
        {
            // Trance over. Bank the remainder rather than dropping it — otherwise ending a trance
            // nineteen seconds into a window throws that recovery away.
            BankTrance(api, player, st, wa);
            st.LastGrantMs = now;   // the next trance's first bank is a full interval away
            return;
        }

        if (delta > 0) st.Restored += delta;
        if (now - st.LastGrantMs < TranceGrantIntervalMs) return;
        st.LastGrantMs = now;
        BankTrance(api, player, st, wa);
    }

    /// <summary>Convert this window's restored mana into one practice grant and clear the accumulator.
    /// announceRepeat:false — the trance banks on a fixed cadence whether or not the ledger has
    /// anything new to say, and the old drip taught us that letting it speak floods the Info tab.</summary>
    private static void BankTrance(ICoreServerAPI api, IServerPlayer player, TranceState st, ITreeAttribute wa)
    {
        if (st.Restored <= 0) return;
        double restored = st.Restored;
        st.Restored = 0;

        // The pool current mana actually fills toward; base + RBM's starting constant is the fallback.
        double pool = wa.GetInt(ArcDomain.AttrTotalMaxMana, 0);
        if (pool <= 0) pool = wa.GetInt(ArcDomain.AttrPlayerMaxMana, 0) + ArcDomain.RbmStartingMana;
        if (pool <= 0) return;

        double mul = System.Math.Min(TranceMultiplierCeiling,
            restored / pool * ArcDomain.Knob(ArcDomain.MeditationTranceRaw, 25.0));
        if (mul <= 0) return;

        st.Grants++;
        Core?.Ledger?.Log(player, ArcDomain.Code, ArcDomain.TechMeditation,
            HashCode.Combine("meditate", player.PlayerUID, st.Grants,
                (int)(api.World.ElapsedMilliseconds / TranceGrantIntervalMs)),
            mul, announceRepeat: false);
        TcmLog.Cat(api, "arc", $"trance bank for {player.PlayerName}: {restored:0} mana restored of a {pool:0} pool -> x{mul:0.##} on meditation (grant #{st.Grants})");
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

        PatchLaboratoryStations(api, harmony);
        PatchMemoryCrystal(api, harmony);
    }

    // ------------------------------------------------------------ the Crystallized Memory (RULED)

    /// <summary>RBM's Crystallized Memory is a tradeable lump of magic XP: hold it, channel five
    /// seconds, and it adds ITEMCRYSTALLIZEDMEMORY_BASE_EXPGAIN (10%) of a level to your mana XP bar.
    /// That is precisely the thing the annex forbids — progression bound to an ITEM, a rank you can buy
    /// off another player — and ARC's freeze already reduces its exp write to nothing, so as shipped it
    /// is a dead item on the loot tables. RULED (Jeffrey): repurpose rather than delete. The crystal
    /// becomes a ONE-SHOT MANA BURST, and the tooltip says so honestly.
    ///
    /// Verified RBM 3.2.5: `rustboundmagic.src.common.item.resource.ItemCrystallizedMemoriesRM : Item`
    /// (:95021). The grant lives in `OnHeldInteractStep(float, ItemSlot, EntityAgent, BlockSelection,
    /// EntitySelection)` (:95142) — both branches of the rust-mage config lock write the exp attribute
    /// and then `RightHandItemSlot.TakeOut(1)` (:95194-95199 and :95204-95209). The tooltip is a LANG
    /// key, not hardcoded text: `GetHeldItemInfo` (:95067) renders
    /// "rustboundmagic:tooltip-item-crystallizedmemories-1" with the config percent as {0} (:95077).</summary>
    private static void PatchMemoryCrystal(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("rustboundmagic")) return;

        var crystal = AccessTools.TypeByName("rustboundmagic.src.common.item.resource.ItemCrystallizedMemoriesRM");
        var step = crystal == null ? null : AccessTools.DeclaredMethod(crystal, "OnHeldInteractStep",
            new[] { typeof(float), typeof(ItemSlot), typeof(EntityAgent), typeof(BlockSelection), typeof(EntitySelection) });
        if (step != null)
        {
            harmony.Patch(step, prefix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(MemoryCrystalPrefix))));
            TcmLog.Info(api, "ARC memory-crystal repurpose hooked (exp grant replaced by a one-shot mana burst)");
        }
        else TcmLog.Warn(api, "ARC memory-crystal seam not found (ItemCrystallizedMemoriesRM.OnHeldInteractStep); the crystal keeps RBM's (frozen, inert) exp behaviour this build");

        // Tooltip enforcement. The lang override in assets/rustboundmagic/lang/en.json is the primary
        // fix, but which mod wins a shared lang key depends on asset load order, which we do not
        // control. This postfix makes the outcome deterministic: if RBM's line survived the merge, it
        // is rewritten here; if our override won, there is nothing to find and this is a no-op.
        var info = crystal == null ? null : AccessTools.DeclaredMethod(crystal, "GetHeldItemInfo",
            new[] { typeof(ItemSlot), typeof(System.Text.StringBuilder), typeof(IWorldAccessor), typeof(bool) });
        if (info != null)
            harmony.Patch(info, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(MemoryCrystalInfoPostfix))));
        else TcmLog.Warn(api, "ARC memory-crystal tooltip seam not found (ItemCrystallizedMemoriesRM.GetHeldItemInfo); relying on the lang override alone");
    }

    /// <summary>The repurposed crystal. Intercepts ONLY the terminal branch — every gate RBM checks
    /// before granting is re-checked here, and any miss returns true so the original runs untouched and
    /// still sends its own "you moved" / "keep sneaking" messages and channels its five seconds. That
    /// keeps the item's whole feel intact and confines us to the one branch we are replacing.
    ///
    /// Skipping the original is what makes this a REPLACEMENT rather than a bonus: the exp write and
    /// any level-up side effects riding on it never execute at all, instead of executing and being
    /// wiped a tick later by the reconcile.
    ///
    /// The mana write is server-side only (WatchedAttributes are server-authoritative), but the consume
    /// mirrors the original on BOTH sides — RBM calls TakeOut(1) unconditionally, and matching that
    /// keeps the client's predicted stack in step with the server's.</summary>
    public static bool MemoryCrystalPrefix(float secondsUsed, EntityAgent byEntity, ref bool __result)
    {
        if (byEntity is not EntityPlayer ep) return true;
        if (secondsUsed < 5f) return true;                                   // still channeling
        if (ep.Controls.TriesToMove || ep.Controls.Jump) return true;        // RBM sends the interrupt
        if (!ep.Controls.Sneak) return true;                                 // RBM sends the sneak hint

        var wa = ep.WatchedAttributes;
        if (!wa.GetBool(RbmMagicUnlockedAttr, false)) return true;
        if (!wa.HasAttribute(ArcDomain.AttrXpToNextLevel)) return true;      // RBM's own guard
        // The rust-mage class lock (RBM config LOCK_ALL_MAGIC_TO_RUSTMAGE_ONLY, default false): when it
        // is on, a non-rustmage gets nothing from the crystal, so we must not burst for them either.
        if (RustMageLockActive() && wa.GetString(RbmCharacterClassAttr, "commoner") != RbmRustMageClass) return true;

        // Server writes the burst; both sides consume, exactly as RBM does.
        if (ep.World?.Side == EnumAppSide.Server)
        {
            double pool = wa.GetInt(ArcDomain.AttrTotalMaxMana, 0);
            if (pool <= 0) pool = wa.GetInt(ArcDomain.AttrPlayerMaxMana, 0) + ArcDomain.RbmStartingMana;
            if (pool > 0)
            {
                int current = wa.GetInt(ArcDomain.AttrCurrentMana, 0);
                int restored = (int)System.Math.Round(pool * ArcDomain.Knob(ArcDomain.MemoryCrystalManaFrac, 0.25));
                int next = System.Math.Min((int)pool, current + System.Math.Max(0, restored));
                if (next != current)
                {
                    wa.SetInt(ArcDomain.AttrCurrentMana, next);
                    wa.MarkPathDirty(ArcDomain.AttrCurrentMana);
                }
                TcmLog.Cat(ep.World.Api, "arc",
                    $"memory crystal consumed by {ep.Player?.PlayerName}: mana {current} -> {next} of {pool:0} (burst {restored}, no exp, no practice)");
            }
        }

        ep.RightHandItemSlot?.TakeOut(1);
        ep.RightHandItemSlot?.MarkDirty();

        __result = false;   // RBM returns false on the terminal step; the use ends here
        return false;       // skip the original entirely
    }

    // RBM attribute/class literals the crystal's gate chain reads (verified 3.2.5 :76057-76059, :95075).
    private const string RbmMagicUnlockedAttr = "entitybehavior-player-ismagicunlocked_rm";
    private const string RbmCharacterClassAttr = "characterClass";
    private const string RbmRustMageClass = "rustmage";

    /// <summary>Read RBM's LOCK_ALL_MAGIC_TO_RUSTMAGE_ONLY (default false). Reflected off the static
    /// config so a server that turned the lock ON keeps the crystal mage-only, as RBM intends.</summary>
    private static bool RustMageLockActive()
    {
        var rbmMain = AccessTools.TypeByName("rustboundmagic.src.RustboundMagic");
        object? cfg = rbmMain == null ? null : AccessTools.Field(rbmMain, "config")?.GetValue(null);
        if (cfg == null) return false;
        return Traverse.Create(cfg).Field("LOCK_ALL_MAGIC_TO_RUSTMAGE_ONLY").GetValue<bool>();
    }

    /// <summary>Belt-and-suspenders for the tooltip: if RBM's exp line survived the lang merge, swap it
    /// for the honest one. Matched on the distinctive "magic exp" fragment of
    /// "A rare item valued by all magic users. Grants +{0}% magic exp."</summary>
    public static void MemoryCrystalInfoPostfix(System.Text.StringBuilder dsc)
    {
        if (dsc == null || dsc.Length == 0) return;
        string text = dsc.ToString();
        if (text.IndexOf("magic exp", System.StringComparison.OrdinalIgnoreCase) < 0) return;

        string replacement = Lang.Get("almanactcm:arc-memorycrystal");
        var kept = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            if (line.IndexOf("magic exp", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kept.Add(replacement);
                continue;
            }
            kept.Add(line.TrimEnd('\r'));
        }
        dsc.Clear();
        dsc.Append(string.Join("\n", kept));
    }

    // ------------------------------------------------------------ laboratory verb, stage 2c

    /// <summary>The rest of the LABORATORY verb (§5): everything a mage's stations actually PRODUCE.
    /// Stage 2b left the verb carrying only the Spellforge research bench because the other two
    /// candidates each looked unreachable — the ritual triggers are seventeen near-identical methods,
    /// and the Thaumic Foundry completes with no player anywhere in scope. Both turned out to have a
    /// seam; this stage takes them.
    ///
    ///   • The world-magic choke point — every ritual (17 TriggerRitualOf* methods) AND the Oculus
    ///     pedestal's essence-consume funnel their XP through ONE private method,
    ///     ModSystemWorldMagic.ApplyPlayerMagicExpGain(EntityPlayer, int) (RBM 3.2.5 :24231, 25 call
    ///     sites verified). Postfixing that one method credits all of them, with the player in scope.
    ///     Casting does NOT reach here — ConsumeManaForSpell writes the XP attribute INLINE (:72551,
    ///     :72567), as do the wand/staff held-interact paths (:93484, :95195), so CastPostfix and this
    ///     postfix can never both fire for one action.
    ///   • The Thaumic Foundry — the owner-at-action shape (the FAR trough precedent). The completion,
    ///     BlockEntityStationThaumicFoundryCoreRM.RunThaumicFoundryCreateItem(IWorldAccessor) (:152175),
    ///     takes only a world: the foundry is fed by ITEMS THROWN INTO ITS PORTAL (an EntityItem
    ///     OnEntityInside handler, :123340), so no player is ever in scope at the product. The single
    ///     player-facing seam is OnInteract (:151699, reached from the block at :122231) — where the
    ///     tablet goes in. Whoever last touched the foundry owns its next product.
    ///
    /// Conditional exactly like the rest of the file: a name that does not resolve warns and disables
    /// that one grant, never throws. The explicit mod-presence guard mirrors RegisterServer so the
    /// resolution work is skipped outright on a world without RBM.</summary>
    private static void PatchLaboratoryStations(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("rustboundmagic")) return;

        // The single XP choke point for rituals + oculus. PRIVATE, hence DeclaredMethod; the seam is
        // one method with a fixed (EntityPlayer, int) shape, so no overload disambiguation is needed.
        var worldMagic = AccessTools.TypeByName("rustboundmagic.src.system.ModSystemWorldMagic");
        var expGain = worldMagic == null ? null : AccessTools.DeclaredMethod(worldMagic, "ApplyPlayerMagicExpGain",
            new[] { typeof(EntityPlayer), typeof(int) });
        if (expGain != null)
        {
            harmony.Patch(expGain, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(RitualExpPostfix))));
            TcmLog.Info(api, "ARC laboratory grant hooked (world-magic XP choke point: rituals + oculus)");
        }
        else TcmLog.Warn(api, "ARC ritual/oculus seam not found (ModSystemWorldMagic.ApplyPlayerMagicExpGain); those laboratory grants are inactive this build");

        // The foundry pair: stamp the owner at the interaction, bank at the unattended completion.
        var foundry = AccessTools.TypeByName("rustboundmagic.src.common.blockentity.station.BlockEntityStationThaumicFoundryCoreRM");
        var touch = foundry == null ? null : AccessTools.DeclaredMethod(foundry, "OnInteract",
            new[] { typeof(IPlayer), typeof(BlockSelection) });
        var create = foundry == null ? null : AccessTools.DeclaredMethod(foundry, "RunThaumicFoundryCreateItem",
            new[] { typeof(IWorldAccessor) });
        if (touch != null && create != null)
        {
            harmony.Patch(touch, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(FoundryTouchPostfix))));
            harmony.Patch(create, postfix: new HarmonyMethod(AccessTools.Method(typeof(ArcPatches), nameof(FoundryCreatePostfix))));
            TcmLog.Info(api, "ARC laboratory grant hooked (Thaumic Foundry: owner at OnInteract, credit at CreateItem)");
        }
        // BOTH halves or neither — a stamp with no bank is dead weight, and a bank with no stamp
        // credits nobody, so one missing seam disables the pair rather than half-wiring it.
        else TcmLog.Warn(api, "ARC foundry seam pair not found (BlockEntityStationThaumicFoundryCoreRM.OnInteract/RunThaumicFoundryCreateItem); the foundry laboratory grant is inactive this build");
    }

    /// <summary>Foundry owner-at-action, in memory only: pos key -> player uid. The FAR troughOwners
    /// precedent — a server restart loses the stamp, and the next product goes uncredited until
    /// someone touches the foundry again. Accepted: the stamp is cheap to re-earn (open the station)
    /// and a persisted map would need its own save hooks for a verb that fires a handful of times a
    /// session.</summary>
    private static readonly Dictionary<string, string> foundryOwners = new();

    private static string PosKey(BlockPos pos) => pos.X + "/" + pos.Y + "/" + pos.Z;

    /// <summary>Rituals and Oculus grimoire synthesis, credited at RBM's one XP choke point. Also
    /// re-wipes the XP this call just added — the same belt-and-suspenders freeze CastPostfix does, so
    /// nothing accumulates toward an RBM mana level-up between reconciles (ARC owns the pool).
    ///
    /// <paramref name="expIn"/> is RBM's own quality signal, so it scales the grant — but only as
    /// headroom: every one of the 25 call sites in 3.2.5 passes a literal 1 (16 TriggerRitualOf* sites
    /// and the grimoire infusion pass exptierIn=1; the oculus hard-codes expIn=1). Clamping to [1,2]
    /// therefore means "1.0 today, at most double if a future RBM starts tiering its rituals" — it
    /// cannot silently deflate the configured Raw=4 the way a divisor would, and cannot spike it.</summary>
    public static void RitualExpPostfix(EntityPlayer playerIn, int expIn)
    {
        if (playerIn?.World?.Side != EnumAppSide.Server) return;
        var player = playerIn.Player;
        if (player == null) return;

        double weight = System.Math.Clamp(expIn, 1, 2);
        Core?.Ledger?.Log(player, ArcDomain.Code, ArcDomain.TechLaboratory,
            HashCode.Combine("labritual", player.PlayerUID, (int)(playerIn.World.ElapsedMilliseconds / 30000)),
            weight);
        TcmLog.Cat(playerIn.World.Api, "arc", $"laboratory credit at the world-magic choke point for {player.PlayerName} (rbm exp {expIn} -> x{weight:0.##})");

        var wa = playerIn.WatchedAttributes;
        if (wa.GetFloat(ArcDomain.AttrXpToNextLevel, 0f) != 0f)
        {
            wa.SetFloat(ArcDomain.AttrXpToNextLevel, 0f);
            wa.MarkPathDirty(ArcDomain.AttrXpToNextLevel);
        }
    }

    /// <summary>Anyone who opens the foundry (tablet in, upgrade in, or a bare-handed take) becomes its
    /// owner. Stamped unconditionally rather than on __result, because a refused interaction is still
    /// the tell of who is running this station — same reading as the trough fill.</summary>
    public static void FoundryTouchPostfix(BlockEntity __instance, IPlayer byPlayer)
    {
        if (byPlayer == null || __instance?.Api?.Side != EnumAppSide.Server) return;
        string key = PosKey(__instance.Pos);
        bool changed = !foundryOwners.TryGetValue(key, out string? prev) || prev != byPlayer.PlayerUID;
        foundryOwners[key] = byPlayer.PlayerUID;
        if (changed)  // one line per ownership change, not one per click
            TcmLog.Cat(__instance.Api, "arc", $"foundry owner stamp: {__instance.Pos} -> {byPlayer.PlayerName} (silent by design; credit lands when the foundry mints a product)");
    }

    /// <summary>A foundry completes a synthesis: bank the laboratory verb to its stamped owner. The
    /// 10s context bucket collapses a same-tick double-mint (the portal's ingredient loop can reach
    /// the create twice in one pass) while leaving genuinely separate crafts — which cost a full
    /// charge cycle each — as distinct practice.</summary>
    public static void FoundryCreatePostfix(BlockEntity __instance, IWorldAccessor worldIn)
    {
        if (__instance?.Api?.Side != EnumAppSide.Server || worldIn == null) return;
        if (!foundryOwners.TryGetValue(PosKey(__instance.Pos), out string? uid) || uid == null)
        {
            // The diagnostic half of the spine (the trough lesson): a product with no stamped owner is
            // the exact symptom of a dead OnInteract hook, or of a restart since the last touch.
            TcmLog.Cat(__instance.Api, "arc", $"foundry at {__instance.Pos} minted a product but NO owner stamped; uncredited");
            return;
        }
        IPlayer? owner = worldIn.PlayerByUid(uid);
        if (owner == null) return;  // owner offline; this product's credit is lost, the stamp survives
        TcmLog.Cat(__instance.Api, "arc", $"foundry at {__instance.Pos} minted a product -> laboratory credit for {owner.PlayerName}");
        Core?.Ledger?.Log(owner, ArcDomain.Code, ArcDomain.TechLaboratory,
            HashCode.Combine("labfoundry", __instance.Pos.X, __instance.Pos.Y, __instance.Pos.Z,
                (int)(worldIn.ElapsedMilliseconds / 10000)));
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

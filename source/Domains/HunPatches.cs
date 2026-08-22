using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using ProtoBuf;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN Phase 1 hooks (rank-bonus-design §HUN, ruled 2026-07-10; seams re-pinned against the
/// LIVE binaries re-verified 2026-08-21 — Butchering 1.13.6, PS 5.1.0, the 0.3.77 namespace lesson).
///
/// Verbs:
///   • hunting — sapi.Event.OnEntityDeath, credited to the CAUSING player (projectile kills
///     resolve through DamageSource.GetCauseEntity), only for wild huntable game (has the
///     harvestable behavior, not tamed/owned). Every kill also feeds the per-species ledger
///     that will gate the Phase 3 Hunter's Map: the hunter knows the country he has hunted.
///   • dressing — EntityBehaviorHarvestable.GenerateDrops postfix (the field harvest; the
///     same method whose yield rides animalLootDropRate).
///   • trapping — PS snares + deadfalls, owner-at-placement (the FIS trap shape: no trap BE
///     stores an owner). REDESIGNED 2026-08-21: PS traps hold no catch item, so the old
///     collection credit could never fire and trapping never paid. The catch is the KILL: the
///     mirrored collide credits the owner when the trap kills game, scales the bait-stolen and
///     tripped-empty rolls by the owner's rank (floored above zero, never a sure trap), and a
///     GM-weighted proc leaves the trap set after a kill, bait kept (the trapline yield).
///   • butchery — Butchering's BlockEntityButcherWorkstation.processItem (the abstract base
///     both hook and table route through), by-target ruling: cutting game is a hunter's skill.
///
/// Stats (both verified vanilla, zero-Harmony, reconcile tick): animalLootDropRate 0.9→1.15
/// and animalSeekingRange 1.15→0.75 (floored above zero — no invisible hunter, ruled).
/// </summary>
public static class HunPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    // ------------------------------------------------------------ per-species kill ledger

    /// <summary>uid -> species (entity code first part) -> lifetime kills. Phase 3's Hunter's
    /// Map knowledge gate reads this; recorded from Phase 1 day one so no one starts at zero.</summary>
    private static Dictionary<string, Dictionary<string, int>> kills = new();

    public static int KillCount(IPlayer player, string species) =>
        kills.TryGetValue(player.PlayerUID, out var per) && per.TryGetValue(species, out int n) ? n : 0;

    /// <summary>Species this player has hunted at least <paramref name="minKills"/> times. The
    /// Hunter's Map knowledge gate (N=3) reads this to decide whose habitat the viewer may see.</summary>
    public static IEnumerable<string> KnownSpecies(IPlayer player, int minKills)
    {
        if (!kills.TryGetValue(player.PlayerUID, out var per)) yield break;
        foreach (var kv in per)
            if (kv.Value >= minKills) yield return kv.Key;
    }

    // ---- known-species sync: the ledger is server-side, but the Hunter's Map now computes its
    // habitat on the CLIENT (off the client's own map regions), so the viewer needs to know which
    // species they have earned. Small packet on join and whenever a species crosses the threshold.

    public const int HuntersMapKnowledgeN = 3;

    /// <summary>A species' habitat envelope, flattened for the wire. SpawnConditions hang off
    /// EntityProperties.Server, which is server config and is NOT populated client-side, so the
    /// client cannot resolve these itself. The server resolves them once and ships the numbers;
    /// the habitat computation still happens entirely on the client.</summary>
    [ProtoContract]
    public class SpeciesEnvelope
    {
        [ProtoMember(1)] public string? Species;
        [ProtoMember(2)] public float MinTemp;
        [ProtoMember(3)] public float MaxTemp;
        [ProtoMember(4)] public float MinRain;
        [ProtoMember(5)] public float MaxRain;
        [ProtoMember(6)] public float MinForest;
        [ProtoMember(7)] public float MaxForest;
        [ProtoMember(8)] public float MinShrubs;
        [ProtoMember(9)] public float MaxShrubs;
        [ProtoMember(10)] public float MinForestOrShrubs;
    }

    [ProtoContract]
    public class HunKnownPacket
    {
        [ProtoMember(1)] public List<SpeciesEnvelope>? Envelopes;
    }

    private static IServerNetworkChannel? hunChannel;

    /// <summary>Client mirror of the envelopes this player has earned. Bumped version lets the map
    /// layer discard its cached habitat and recompute when the set changes.</summary>
    public static List<SpeciesEnvelope> ClientEnvelopes { get; private set; } = new();
    public static int ClientKnownVersion { get; private set; }

    public static void RegisterClient(ICoreClientAPI api)
    {
        api.Network.RegisterChannel("almanactcmhun").RegisterMessageType<HunKnownPacket>()
            .SetMessageHandler<HunKnownPacket>(p =>
            {
                ClientEnvelopes = p.Envelopes ?? new List<SpeciesEnvelope>();
                ClientKnownVersion++;
                TcmLog.Cat(api, "hun", $"hunter's map: {ClientEnvelopes.Count} species envelope(s) synced: " +
                    $"[{string.Join(", ", ClientEnvelopes.ConvertAll(e => e.Species ?? "?"))}]");
            });
    }

    private static void SendKnown(IServerPlayer player)
    {
        var list = new List<SpeciesEnvelope>();
        foreach (string species in KnownSpecies(player, HuntersMapKnowledgeN))
        {
            var env = ResolveEnvelope(species);
            if (env == null) continue;
            list.Add(new SpeciesEnvelope
            {
                Species = species,
                MinTemp = env.MinTemp, MaxTemp = env.MaxTemp,
                MinRain = env.MinRain, MaxRain = env.MaxRain,
                MinForest = env.MinForest, MaxForest = env.MaxForest,
                MinShrubs = env.MinShrubs, MaxShrubs = env.MaxShrubs,
                MinForestOrShrubs = env.MinForestOrShrubs
            });
        }
        TcmLog.Cat(sapi!, "hun", $"hunter's map: sending {list.Count} envelope(s) to {player.PlayerName}");
        hunChannel?.SendPacket(new HunKnownPacket { Envelopes = list }, player);
    }

    private static readonly Dictionary<string, ClimateSpawnCondition?> envelopeCache = new();

    /// <summary>Server-side envelope lookup: any registered entity of that species carrying a
    /// worldgen (or runtime) spawn envelope. Only the server has EntityProperties.Server.</summary>
    private static ClimateSpawnCondition? ResolveEnvelope(string species)
    {
        if (envelopeCache.TryGetValue(species, out var cached)) return cached;
        ClimateSpawnCondition? found = null;
        foreach (EntityProperties t in sapi!.World.EntityTypes)
        {
            if (t?.Code == null || t.Code.FirstCodePart() != species) continue;
            var sc = t.Server?.SpawnConditions;
            ClimateSpawnCondition? env = (ClimateSpawnCondition?)sc?.Worldgen ?? sc?.Runtime;
            if (env != null) { found = env; break; }
        }
        envelopeCache[species] = found;
        return found;
    }

    // ------------------------------------------------------------ trap owner side-state

    private static Dictionary<string, string> trapOwners = new();

    private static string StateFileName
    {
        get
        {
            string name = sapi?.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi?.WorldManager.SaveGame?.WorldName ?? "almanactcm_save";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(GamePaths.Saves, "AlmanacTcm", name + "-hunstate.json");
        }
    }

    private static string Key(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    // ------------------------------------------------------------ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += Load;
        api.Event.GameWorldSave += Save;
        api.Event.OnEntityDeath += OnEntityDeath;
        api.Event.RegisterGameTickListener(ReconcileHunStats, 2000);

        // Hunter's Map envelope sync — dormant with the shelved layer (see AlmanacTcmModSystem.Start).
        // The per-species kill ledger above keeps recording either way, so nothing is lost while it
        // is shelved and the knowledge gate will be satisfied the moment it comes back.
        // hunChannel = api.Network.RegisterChannel("almanactcmhun").RegisterMessageType<HunKnownPacket>();
        // api.Event.PlayerJoin += SendKnown;
    }

    private static void Load()
    {
        try
        {
            byte[]? data = sapi!.WorldManager.SaveGame.GetData("almanacHunKills");
            if (data != null)
                kills = SerializerUtil.Deserialize<Dictionary<string, Dictionary<string, int>>>(data) ?? new();
        }
        catch (Exception e) { TcmLog.Error(sapi, $"hun kill ledger unreadable ({e.Message}); starting empty"); }

        try
        {
            string file = StateFileName;
            if (File.Exists(file))
                trapOwners = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
            TcmLog.Cat(sapi, TcmLog.Config, $"HUN state loaded: {trapOwners.Count} owned trap(s), {kills.Count} hunter ledger(s)");
        }
        catch (Exception e) { TcmLog.Error(sapi, $"hunstate.json unreadable ({e.Message}); starting empty, NOT overwriting"); }
    }

    private static void Save()
    {
        sapi!.WorldManager.SaveGame.StoreData("almanacHunKills", SerializerUtil.Serialize(kills));
        try
        {
            string file = StateFileName;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonConvert.SerializeObject(trapOwners));
        }
        catch (Exception e) { TcmLog.Error(sapi, $"could not save HUN state: {e.Message}"); }
    }

    // ------------------------------------------------------------ hunting (the kill)

    /// <summary>Wild game only: harvestable AND carries a creatureDiet. Animals eat; temporal
    /// hostiles (drifter/shiver/bowtorn/bell) do not, and although CO makes them carvable they
    /// are COMBAT quarry (MEL/RAN), never the hunt. Matches the Tracker's Eye IsAnimal test.
    /// (Fix 2026-07-20: rust-mob kills+carving were wrongly banking HUN.)</summary>
    private static bool IsHuntableGame(Entity e) =>
        e.GetBehavior<EntityBehaviorHarvestable>() != null
        && e.Properties?.Attributes?["creatureDiet"]?.Exists == true;

    private static void OnEntityDeath(Entity entity, DamageSource? damageSource)
    {
        if (sapi == null || entity == null || damageSource == null) return;
        if (!IsHuntableGame(entity)) return; // wild game only, never rust monsters

        // Tamed/owned beasts are ANI's world, not the hunt's, and an established captive
        // lineage (gen 2+) is husbandry even when nobody holds the deed. Same fence MEL/RAN
        // apply to this same death event (MelRanKillPatches.IsCombatExcluded), adopted here
        // 2026-08-21 (verb-review blocker 1): before this, HUN checked only the ownership
        // attributes, so slaughtering bred stock banked wild-kill practice and inflated the
        // per-species kill ledger, the exact state a HUN ascension proof will read.
        if (MelRanKillPatches.IsCombatExcluded(entity)) return;

        Entity? cause = damageSource.GetCauseEntity() ?? damageSource.SourceEntity;
        IPlayer? player = (cause as EntityPlayer)?.Player;
        // Bleed-out fallback (2026-07-18): a wounded animal that bleeds out carries no player
        // cause at death, but the shared combat store knows who wounded it — that hunter's
        // kill, banked and counted toward the species ledger like any other. Gated on a fully
        // unattributed source (the bleed-tick shape): a wolf finishing a player-wounded animal
        // has cause=wolf, and that kill is the wolf's.
        if (player == null && cause == null
            && MelRanKillPatches.TryPeekLastAttacker(entity.EntityId, out string lastUid))
            player = sapi.World.PlayerByUid(lastUid);
        if (player == null) return; // wolves, falls, traps-by-AI: nobody's practice

        // Species + 64-block region, the MEL/RAN shape for this same death event (adopted
        // 2026-08-21, verb-review blocker 1). The old hash was species + a 1-second bucket:
        // no position, so a pen of animals killed one per second each re-banked, farmable at
        // exactly the cadence slaughter runs at. Region dedup collapses that to one credit
        // per window; the cost, two wild kills of one species in the same region inside the
        // dedup window merging, is the same trade MEL/RAN accepted deliberately.
        string species = entity.Code?.FirstCodePart() ?? "unknown";
        Core?.Ledger?.Log(player, HunDomain.Code, HunDomain.TechHunting,
            HashCode.Combine(species,
                (int)(entity.ServerPos.X / 64), (int)(entity.ServerPos.Z / 64)));

        var per = kills.TryGetValue(player.PlayerUID, out var p) ? p : kills[player.PlayerUID] = new();
        per[species] = per.TryGetValue(species, out int n) ? n + 1 : 1;

        // The moment a species crosses the knowledge gate, push the new set so the Hunter's Map
        // can paint its country without waiting for a relog.
        if (per[species] == HuntersMapKnowledgeN && player is IServerPlayer splr) SendKnown(splr);
    }

    // ------------------------------------------------------------ stat reconcile

    private static readonly Dictionary<string, (double yield, double seek)> lastStats = new();

    private static void ReconcileHunStats(float dt)
    {
        if (sapi == null) return;
        foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
        {
            var entity = player.Entity;
            if (entity == null) continue;
            int level = HunDomain.LevelOf(player);
            double yield = HunDomain.RankLinear(level,
                HunDomain.Knob(HunDomain.AnimalYieldUntrained, 0.70),
                HunDomain.Knob(HunDomain.AnimalYieldGm, 1.15));
            double seek = HunDomain.RankLinear(level,
                HunDomain.Knob(HunDomain.SeekRangeUntrained, 1.15),
                HunDomain.Knob(HunDomain.SeekRangeGm, 0.75));
            seek = Math.Max(0.05, seek); // ruled: floored above zero, no invisible hunter
            if (lastStats.TryGetValue(player.PlayerUID, out var prev)
                && Math.Abs(prev.yield - yield) < 0.001 && Math.Abs(prev.seek - seek) < 0.001) continue;
            entity.Stats.Set("animalLootDropRate", "almanactcm", (float)(yield - 1.0), false);
            entity.Stats.Set("animalSeekingRange", "almanactcm", (float)(seek - 1.0), false);
            lastStats[player.PlayerUID] = (yield, seek);
        }
    }

    // ------------------------------------------------------------ dressing (field harvest)

    [HarmonyPatch(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.GenerateDrops))]
    public static class DressingPatch
    {
        public static void Postfix(EntityBehaviorHarvestable __instance, IPlayer byPlayer)
        {
            var world = byPlayer?.Entity?.World;
            if (world == null || world.Side != EnumAppSide.Server) return;
            // Carving a rust monster is not dressing game (fix 2026-07-20): gate on the same
            // wild-game test as the kill grant, so only animals bank HUN dressing.
            var carcass = HarmonyLib.AccessTools.Field(typeof(EntityBehavior), "entity")?.GetValue(__instance) as Entity;
            if (carcass == null || !IsHuntableGame(carcass)) return;

            // The raised-animal split (ruled 2026-08-21, A7): butchering a beast a player
            // RAISED (the trough-feed raisedBy stamp, ANI's attribution spine) is husbandry's
            // harvest as much as the knife's, so the act's practice splits 50/50 between HUN
            // dressing and FAR butchery, same total as a wild dressing. Wild game, no stamp,
            // pays full HUN exactly as before. The butcher gets both credits, whoever raised
            // it: the condition is that a player did, not that this player did.
            bool raised = carcass.WatchedAttributes?.HasAttribute(AniDomain.RaisedByAttr) == true;
            int ctx = HashCode.Combine(HunDomain.TechDressing, world.ElapsedMilliseconds / 1000);
            if (raised)
            {
                Core?.Ledger?.Log(byPlayer!, HunDomain.Code, HunDomain.TechDressing, ctx, 0.5);
                Core?.Ledger?.Log(byPlayer!, FarDomain.Code, FarDomain.TechButchery, ctx, 0.5);
            }
            else
            {
                Core?.Ledger?.Log(byPlayer!, HunDomain.Code, HunDomain.TechDressing, ctx);
            }
        }
    }

    // ------------------------------------------------------------ conditional seams

    private static readonly List<Type> trapBlockTypes = new();

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Butchering stations (rewired 2026-08-21, HUN slot): the grant moved from the abstract
        // base to the hook and table OVERRIDES, because the hook clone-swaps the stage item and
        // the table empties the slot BEFORE calling base.processItem, so only a subclass prefix
        // ever sees the carcass it is about to work. Three seams, each warn-and-skip:
        // processItem x2 (grant + raised split), cloneStack (stamp survives the skinning stage),
        // Butcherable.OnInteract (stamp carried from the dead beast onto the carcass item).
        if (api.ModLoader.IsModEnabled("butchering"))
        {
            var hook = AccessTools.TypeByName("Butchering.src.common.blockentity.BlockEntityButcherHook");
            var table = AccessTools.TypeByName("Butchering.src.common.blockentity.BlockEntityButcherTable");
            int wired = 0;
            foreach (var t in new[] { hook, table })
            {
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "processItem");
                if (m == null) continue;
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ButcheryPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ButcheryPatch), "Postfix")));
                wired++;
            }
            if (wired == 0) TcmLog.Warn(api, "butchering present but no processItem override found; HUN butchery inactive");
            else TcmLog.Info(api, $"HUN butchery hooked to Butchering ({wired} station type(s); raised split live)");

            var cs = hook == null ? null : AccessTools.DeclaredMethod(hook, "cloneStack");
            if (cs != null)
                harmony.Patch(cs, postfix: new HarmonyMethod(AccessTools.Method(typeof(CarcassCloneCarryPatch), "Postfix")));
            else TcmLog.Cat(api, TcmLog.Config,
                "butchering cloneStack seam absent; raised stamp dies at the skinning stage (later steps pay as wild)");

            var conv = AccessTools.TypeByName("Butchering.src.common.entitybehavior.EntityBehaviorButcherable");
            var cv = conv == null ? null : AccessTools.DeclaredMethod(conv, "OnInteract");
            if (cv != null)
            {
                harmony.Patch(cv,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CarcassStampPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CarcassStampPatch), "Postfix")));
                TcmLog.Info(api, "HUN carcass pickup hooked (raisedBy carried onto the carcass item)");
            }
            else TcmLog.Cat(api, TcmLog.Config,
                "butchering Butcherable.OnInteract seam absent; raised stamp not carried, workstation butchery pays as wild");
        }

        // BloodTrail vibrancy (Phase 2, ruled 2026-07-17): the blood particle colour is chosen
        // client-side per observer, so a ranked hunter reads a more vivid trail. Patch GetColor
        // on the client behaviour. Server has no client behaviour, so this only takes on clients.
        if (api.Side == EnumAppSide.Client && api.ModLoader.IsModEnabled("bloodtrail"))
        {
            var t = AccessTools.TypeByName("BloodTrail.src.Client.EntityBleedingBehaviorParticles");
            var m = t == null ? null : AccessTools.Method(t, "GetColor");
            if (m == null) TcmLog.Warn(api, "bloodtrail present but GetColor not found; HUN blood vibrancy inactive");
            else
            {
                harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(BloodVibrancyPatch), "Postfix")));
                TcmLog.Info(api, "HUN blood vibrancy hooked to BloodTrail (per-observer rank tint)");
            }
        }

        // PS land traps (REDESIGNED 2026-08-21; HUN walk findings in the 0.5 plan doc): PS traps
        // hold no catch item, so the old collection patch waited on a slot that only ever holds
        // bait and trapping never paid; trap kills paid nobody (playerless damage source). The
        // catch IS the animal dying beside the trap. Owner still stamps at placement; the
        // collide is MIRRORED (trough-fix precedent, verified against PS 5.1.0) so the owner's
        // rank scales the two real failure rolls, the kill credits the owner, and the GM proc
        // leaves the trap set after a kill. The 5.1.0 deadfall quirk is mirrored, not fixed:
        // its steal and trip-empty branches cast to BESnare, so a deadfall no-ops there at
        // every rank, exactly vanilla. If a seam is missing the mirror stands down to a
        // credit-only kill patch, so the verb pays even when the axes cannot. Offline owners
        // get vanilla rolls and bank nothing, the collect-era posture kept.
        if (api.ModLoader.IsModEnabled("primitivesurvival"))
        {
            foreach (string block in new[] { "BlockSnare", "BlockDeadfall" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + block);
                if (t != null) trapBlockTypes.Add(t);
            }
            if (trapBlockTypes.Count > 0)
            {
                harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(TrapPlacePatch), "Postfix")));
            }

            var beSnare = AccessTools.TypeByName("PrimitiveSurvival.ModSystem.BESnare");
            var beDeadfall = AccessTools.TypeByName("PrimitiveSurvival.ModSystem.BEDeadfall");
            TrapCollideMirror.SnareBeType = beSnare;
            TrapCollideMirror.DeadfallBeType = beDeadfall;
            TrapCollideMirror.SnareStealBait = beSnare == null ? null : AccessTools.Method(beSnare, "StealBait", new[] { typeof(BlockPos) });
            TrapCollideMirror.SnareTripTrap = beSnare == null ? null : AccessTools.Method(beSnare, "TripTrap", new[] { typeof(BlockPos) });
            TrapCollideMirror.DeadfallTripTrap = beDeadfall == null ? null : AccessTools.Method(beDeadfall, "TripTrap", new[] { typeof(BlockPos) });
            // Loaded is read lazily per collide: PS may not have populated it before TCM starts.
            TrapCollideMirror.ConfigLoadedProp = AccessTools.TypeByName("PrimitiveSurvival.ModConfig.ModConfig")?.GetProperty("Loaded");

            bool mirrorReady = TrapCollideMirror.SnareStealBait != null && TrapCollideMirror.SnareTripTrap != null
                && TrapCollideMirror.DeadfallTripTrap != null && TrapCollideMirror.ConfigLoadedProp != null;

            int hooked = 0;
            foreach (Type t in trapBlockTypes)
            {
                var m = AccessTools.DeclaredMethod(t, "OnEntityCollide");
                if (m == null) { TcmLog.Warn(api, $"primitivesurvival {t.Name}.OnEntityCollide not found; that trap is uncredited"); continue; }
                if (mirrorReady)
                {
                    string pre = t.Name == "BlockSnare" ? nameof(TrapCollideMirror.SnarePrefix) : nameof(TrapCollideMirror.DeadfallPrefix);
                    harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(TrapCollideMirror), pre)));
                }
                else
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(TrapKillCreditPatch), "Prefix")),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(TrapKillCreditPatch), "Postfix")));
                }
                hooked++;
            }
            if (hooked > 0) TcmLog.Info(api, mirrorReady
                ? $"HUN trapping live ({hooked} trap(s); rank-scaled rolls, kill credit, stay-set proc)"
                : $"HUN trapping degraded ({hooked} trap(s); PS seams shifted, kill credit only)");
        }
    }

    /// <summary>Boosts blood-particle vibrancy by the LOCAL client's HUN rank (ruled: the trail
    /// reads clearer to a skilled tracker). Below Apprentice, no change — an untrained eye sees
    /// the dull default. The colour is packed ARGB; we lift saturation and value in HSV so
    /// dark specks become vivid crimson without changing hue (rainbow/confetti modes untouched
    /// enough — they still ramp, just brighter).</summary>
    public static class BloodVibrancyPatch
    {
        public static void Postfix(ref int __result)
        {
            int level = HunDomain.ClientLevel();
            if (level < 5) return;
            float f = Math.Min(1f, (level - 4) / 13f); // Apprentice I ~0.08 -> GM 1.0

            int a = (__result >> 24) & 0xFF, r = (__result >> 16) & 0xFF, g = (__result >> 8) & 0xFF, b = __result & 0xFF;
            float rr = r / 255f, gg = g / 255f, bb = b / 255f;

            float max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
            float v = max, delta = max - min;
            float s = max <= 0 ? 0 : delta / max;
            float hue;
            if (delta < 1e-4f) hue = 0;
            else if (max == rr) hue = ((gg - bb) / delta % 6f);
            else if (max == gg) hue = (bb - rr) / delta + 2f;
            else hue = (rr - gg) / delta + 4f;
            hue *= 60f; if (hue < 0) hue += 360f;

            s = Math.Min(1f, s + 0.35f * f);
            v = Math.Min(1f, v + 0.45f * f);

            float c = v * s, x = c * (1 - Math.Abs((hue / 60f) % 2f - 1f)), mBase = v - c;
            float r2, g2, b2;
            if (hue < 60) { r2 = c; g2 = x; b2 = 0; }
            else if (hue < 120) { r2 = x; g2 = c; b2 = 0; }
            else if (hue < 180) { r2 = 0; g2 = c; b2 = x; }
            else if (hue < 240) { r2 = 0; g2 = x; b2 = c; }
            else if (hue < 300) { r2 = x; g2 = 0; b2 = c; }
            else { r2 = c; g2 = 0; b2 = x; }

            int nr = (int)((r2 + mBase) * 255f), ng = (int)((g2 + mBase) * 255f), nb = (int)((b2 + mBase) * 255f);
            __result = (a << 24) | (nr << 16) | (ng << 8) | nb;
        }
    }

    /// <summary>Workstation butchery grant plus the raised split's workstation half (completes
    /// the A7 arc, HUN slot 2026-08-21). Patched on the hook and table processItem OVERRIDES:
    /// the prefix reads the carcass in slot 0 before the body clone-swaps or consumes it. A
    /// raised carcass (the carried almanacRaisedBy ITEM stamp) splits 50/50 HUN/FAR butchery,
    /// same total, mirroring the vanilla-path DressingPatch; wild pays full HUN as ever.</summary>
    public static class ButcheryPatch
    {
        public static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (__instance is not BlockEntityContainer c || c.Api?.Side != EnumAppSide.Server) return;
            __state = c.Inventory?[0]?.Itemstack?.Attributes?.HasAttribute(AniDomain.RaisedByAttr) == true;
        }

        public static void Postfix(object __instance, IPlayer byPlayer, bool __result, bool __state)
        {
            if (!__result || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            int ctx = HashCode.Combine(be.Pos.X >> 3, be.Pos.Y >> 3, be.Pos.Z >> 3, be.Api.World.ElapsedMilliseconds / 1000);
            if (__state)
            {
                Core?.Ledger?.Log(byPlayer, HunDomain.Code, HunDomain.TechButchery, ctx, 0.5);
                Core?.Ledger?.Log(byPlayer, FarDomain.Code, FarDomain.TechButchery, ctx, 0.5);
            }
            else
            {
                Core?.Ledger?.Log(byPlayer, HunDomain.Code, HunDomain.TechButchery, ctx);
            }
        }
    }

    /// <summary>The raised stamp survives the hook's skinning stage. Butchering's cloneStack
    /// rebuilds the stage item copying only its own three attributes (the GLA clone-wipe
    /// pattern), so without this carry the stamp died at the first knife stroke.</summary>
    public static class CarcassCloneCarryPatch
    {
        public static void Postfix(ItemStack oldStack, ItemStack __result)
        {
            string? uid = oldStack?.Attributes?.GetString(AniDomain.RaisedByAttr);
            if (uid != null && __result != null) __result.Attributes.SetString(AniDomain.RaisedByAttr, uid);
        }
    }

    /// <summary>Carries the raisedBy stamp across Butchering's entity-to-item conversion (the
    /// carcass pickup). The mod copies generation, animalWeight and the drop table onto the
    /// item but not our attribution, so the stamp died at pickup and no workstation could ever
    /// see it. The new stack is a local inside the mod's method, so the carry is a
    /// snapshot-diff: the prefix records every stack in the player's inventories by identity,
    /// the postfix stamps the ONE new stack bearing the conversion's own animalWeight or
    /// AnimalDrops fingerprint. If the give merges into an identical unstamped stack no new
    /// object appears and the carcass pays as wild, the safe direction.</summary>
    public static class CarcassStampPatch
    {
        public static void Prefix(EntityBehavior __instance, EntityAgent byEntity,
            out (string? uid, HashSet<ItemStack>? seen, IPlayer? player) __state)
        {
            __state = (null, null, null);
            var ent = __instance?.entity;
            if (ent == null || ent.World?.Side != EnumAppSide.Server || ent.Alive) return;
            string? uid = ent.WatchedAttributes?.GetString(AniDomain.RaisedByAttr);
            if (uid == null) return;
            var player = (byEntity as EntityPlayer)?.Player;
            if (player?.InventoryManager?.Inventories == null) return;

            var seen = new HashSet<ItemStack>();
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv == null) continue;
                foreach (var slot in inv) if (slot?.Itemstack != null) seen.Add(slot.Itemstack);
            }
            __state = (uid, seen, player);
        }

        public static void Postfix((string? uid, HashSet<ItemStack>? seen, IPlayer? player) __state)
        {
            if (__state.uid == null || __state.seen == null || __state.player == null) return;
            foreach (var inv in __state.player.InventoryManager.Inventories.Values)
            {
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    var stack = slot?.Itemstack;
                    if (stack == null || __state.seen.Contains(stack)) continue;
                    if (!stack.Attributes.HasAttribute("animalWeight")
                        && !stack.Attributes.HasAttribute("AnimalDrops")) continue;
                    stack.Attributes.SetString(AniDomain.RaisedByAttr, __state.uid);
                    slot!.MarkDirty();
                    return;
                }
            }
        }
    }

    /// <summary>Stamps the trap owner at placement (broad seam, type check exits first).</summary>
    public static class TrapPlacePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;
            foreach (Type t in trapBlockTypes)
            {
                if (t.IsInstanceOfType(__instance)) { trapOwners[Key(blockSel.Position)] = byPlayer.PlayerUID; return; }
            }
        }
    }

    /// <summary>The mirrored snare/deadfall collide (PS 5.1.0 body, rank-scaled). Retired the
    /// collection-based credit 2026-08-21: PS land traps never hold a catch item, so that
    /// condition was unsatisfiable and trapping never paid. Server-side only; the client keeps
    /// vanilla prediction exactly as PS itself does (both sides always rolled independently).
    /// At Novice rank every number and branch matches vanilla, including the deadfall's
    /// BESnare-cast quirk in the steal and trip-empty branches. Config values are read lazily
    /// from PS's own Loaded config with the 5.1.0 shipped defaults as fallback.</summary>
    public static class TrapCollideMirror
    {
        internal static Type? SnareBeType, DeadfallBeType;
        internal static MethodInfo? SnareStealBait, SnareTripTrap, DeadfallTripTrap;
        internal static PropertyInfo? ConfigLoadedProp;
        private static readonly AssetLocation TickSound = new("game", "tick");

        private static double Cfg(string name, double fallback)
        {
            try
            {
                object? cfg = ConfigLoadedProp?.GetValue(null);
                if (cfg != null)
                {
                    var tr = Traverse.Create(cfg).Property(name);
                    if (tr.PropertyExists()) return Convert.ToDouble(tr.GetValue());
                }
            }
            catch { }
            return fallback;
        }

        public static bool SnarePrefix(Block __instance, IWorldAccessor world, Entity entity, BlockPos pos, bool isImpact)
            => Collide(__instance, world, entity, pos, isImpact, snare: true);

        public static bool DeadfallPrefix(Block __instance, IWorldAccessor world, Entity entity, BlockPos pos, bool isImpact)
            => Collide(__instance, world, entity, pos, isImpact, snare: false);

        private static void Invoke(MethodInfo? m, Type? beType, IWorldAccessor world, BlockPos pos)
        {
            var be = world.BlockAccessor.GetBlockEntity(pos);
            if (m != null && beType != null && be != null && beType.IsInstanceOfType(be))
                m.Invoke(be, new object[] { pos });
        }

        private static bool Collide(Block block, IWorldAccessor world, Entity entity, BlockPos pos, bool isImpact, bool snare)
        {
            if (world.Side != EnumAppSide.Server) return true;
            if (entity.Code.Path.StartsWith("butterfly") || !isImpact) return false;

            string trapState = block.FirstCodePart(1);

            // The owner's rank scales the failure rolls; unowned or offline = vanilla numbers.
            IPlayer? owner = null;
            int level = 0;
            double failMul = 1.0;
            if (trapOwners.TryGetValue(Key(pos), out string? uid) && uid != null)
            {
                owner = world.PlayerByUid(uid);
                if (owner != null)
                {
                    level = HunDomain.LevelOf(owner);
                    failMul = HunDomain.RankLinear(level,
                        HunDomain.Knob(HunDomain.TrapFailUntrained, 1.35),
                        HunDomain.Knob(HunDomain.TrapFailGm, 0.55));
                }
            }

            string p = snare ? "Snare" : "Deadfall";
            int stolenPct = (int)Math.Round(Cfg(p + "BaitStolenPercent", 10) * failMul);
            int trippedPct = (int)Math.Round(Cfg(p + "TrippedPercent", 10) * failMul);
            double maxHeight = Cfg(p + "MaxAnimalHeight", snare ? 0.8 : 0.7);
            int maxDmg = (int)Cfg(p + (trapState == "set" ? "MaxDamageSet" : "MaxDamageBaited"),
                snare ? (trapState == "set" ? 12 : 24) : (trapState == "set" ? 10 : 20));

            var rnd = world.Rand;
            if (rnd.Next(100) < stolenPct && entity.Code.Path != "player")
            {
                // 5.1.0 quirk mirrored: BOTH blocks route this branch through BESnare, so a
                // deadfall changes nothing here. Fixing it would alter vanilla behavior.
                Invoke(SnareStealBait, SnareBeType, world, pos);
                return false;
            }
            if (rnd.Next(100) < trippedPct)
            {
                Invoke(SnareTripTrap, SnareBeType, world, pos);   // same quirk on the deadfall
                world.PlaySoundAt(TickSound, entity.Pos.X, entity.Pos.Y, entity.Pos.Z, null, true, 32f, 1f);
                return false;
            }
            if (trapState == "tripped") return false;

            int dmg = 3;
            if (entity.Properties.EyeHeight < maxHeight) dmg = rnd.Next(snare ? 6 : 5, maxDmg);
            bool wasAlive = entity.Alive;
            entity.ReceiveDamage(new DamageSource { SourceEntity = null, Type = (EnumDamageType)2 }, dmg);

            // The catch: the trap killed game. Credit the owner's craft, and roll the trapline
            // proc: a master's set survives its kill, bait kept, the line still working.
            bool staysSet = false;
            if (wasAlive && !entity.Alive && owner != null && IsHuntableGame(entity))
            {
                Core?.Ledger?.Log(owner, HunDomain.Code, HunDomain.TechTrapping,
                    HashCode.Combine("trapkill", pos.X, pos.Y, pos.Z, entity.EntityId));
                staysSet = rnd.NextDouble() < HunDomain.TrapStaySetChance(level);
            }
            if (!staysSet)
                Invoke(snare ? SnareTripTrap : DeadfallTripTrap, snare ? SnareBeType : DeadfallBeType, world, pos);
            world.PlaySoundAt(TickSound, entity.Pos.X, entity.Pos.Y, entity.Pos.Z, null, true, 32f, 1f);
            return false;
        }
    }

    /// <summary>The stand-down: if PS's seams shifted and the mirror cannot apply, the verb
    /// still pays. Prefix remembers whether the beast lived; postfix credits the trap's owner
    /// when the collide killed game. No scaling, no proc, vanilla behavior throughout.</summary>
    public static class TrapKillCreditPatch
    {
        public static void Prefix(Entity entity, out bool __state) => __state = entity?.Alive == true;

        public static void Postfix(IWorldAccessor world, Entity entity, BlockPos pos, bool __state)
        {
            if (world?.Side != EnumAppSide.Server || !__state || entity.Alive || !IsHuntableGame(entity)) return;
            if (!trapOwners.TryGetValue(Key(pos), out string? uid) || uid == null) return;
            IPlayer? owner = world.PlayerByUid(uid);
            if (owner == null) return;
            Core?.Ledger?.Log(owner, HunDomain.Code, HunDomain.TechTrapping,
                HashCode.Combine("trapkill", pos.X, pos.Y, pos.Z, entity.EntityId));
        }
    }
}

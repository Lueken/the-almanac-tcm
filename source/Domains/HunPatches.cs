using System;
using System.Collections.Generic;
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
/// LIVE binaries 2026-07-17 — Butchering 1.13.5, PS 5.0.6, the 0.3.77 namespace lesson).
///
/// Verbs:
///   • hunting — sapi.Event.OnEntityDeath, credited to the CAUSING player (projectile kills
///     resolve through DamageSource.GetCauseEntity), only for wild huntable game (has the
///     harvestable behavior, not tamed/owned). Every kill also feeds the per-species ledger
///     that will gate the Phase 3 Hunter's Map: the hunter knows the country he has hunted.
///   • dressing — EntityBehaviorHarvestable.GenerateDrops postfix (the field harvest; the
///     same method whose yield rides animalLootDropRate).
///   • trapping — PS snares + deadfalls, owner-at-placement (the FIS trap shape: no trap BE
///     stores an owner). Credit at collection of a CATCH — retrieving your own bait does not
///     count (checked against the BE's own bait-type list).
///   • butchery — Butchering's BlockEntityButcherWorkstation.processItem (the abstract base
///     both hook and table route through), by-target ruling: cutting game is a hunter's skill.
///
/// Stats (both verified vanilla, zero-Harmony, reconcile tick): animalLootDropRate 0.9→1.15
/// and animalSeekingRange 1.15→0.75 (floored above zero — no invisible hunter, ruled).
/// </summary>
public static class HunPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
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

        // Tamed/owned beasts are ANI's world, not the hunt's (petai/wolftaming/genelib guards).
        var wa = entity.WatchedAttributes;
        if (wa != null && (wa.GetBool("domesticated") || wa.HasAttribute("ownedby") || wa.HasAttribute("owner"))) return;

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

        string species = entity.Code?.FirstCodePart() ?? "unknown";
        Core?.Ledger?.Log(player, HunDomain.Code, HunDomain.TechHunting,
            HashCode.Combine(species, sapi.World.ElapsedMilliseconds / 1000));

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
            Core?.Ledger?.Log(byPlayer!, HunDomain.Code, HunDomain.TechDressing,
                HashCode.Combine(HunDomain.TechDressing, world.ElapsedMilliseconds / 1000));
        }
    }

    // ------------------------------------------------------------ conditional seams

    private static readonly List<Type> trapBlockTypes = new();

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Butchering stations: one postfix on the abstract base covers hook AND table.
        if (api.ModLoader.IsModEnabled("butchering"))
        {
            var ws = AccessTools.TypeByName("Butchering.src.common.blockentity.BlockEntityButcherWorkstation");
            var m = ws == null ? null : AccessTools.Method(ws, "processItem");
            if (m == null) TcmLog.Warn(api, "butchering present but processItem not found; HUN butchery inactive");
            else
            {
                harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(ButcheryPatch), "Postfix")));
                TcmLog.Info(api, "HUN butchery hooked to Butchering (workstation base; by-target ruling)");
            }
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

        // PS land traps: snare + deadfall, owner at placement, credit at catch collection.
        if (api.ModLoader.IsModEnabled("primitivesurvival"))
        {
            int hooked = 0;
            foreach (string block in new[] { "BlockSnare", "BlockDeadfall" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + block);
                if (t != null) trapBlockTypes.Add(t);
            }
            foreach (string be in new[] { "BESnare", "BEDeadfall" })
            {
                var t = AccessTools.TypeByName("PrimitiveSurvival.ModSystem." + be);
                // BESnare/BEDeadfall both declare a one-arg OnInteract(IPlayer); the untyped
                // AccessTools.Method resolves the name across the hierarchy and threw
                // "Ambiguous match" (0.3.85 crash: aborted TCM's whole Start phase, half-loading
                // the mod client+server). Pin the exact signature.
                var m = t == null ? null : AccessTools.Method(t, "OnInteract", new[] { typeof(IPlayer) });
                if (m == null) { TcmLog.Warn(api, $"primitivesurvival {be}.OnInteract(IPlayer) not found; that trap is uncredited"); continue; }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(LandTrapCollectPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(LandTrapCollectPatch), "Postfix")));
                hooked++;
            }
            if (trapBlockTypes.Count > 0)
            {
                harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(TrapPlacePatch), "Postfix")));
            }
            if (hooked > 0) TcmLog.Info(api, $"HUN trapping hooked ({hooked} trap BE(s); owner at placement, catch at collection)");
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

    public static class ButcheryPatch
    {
        public static void Postfix(object __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            Core?.Ledger?.Log(byPlayer, HunDomain.Code, HunDomain.TechButchery,
                HashCode.Combine(be.Pos.X >> 3, be.Pos.Y >> 3, be.Pos.Z >> 3, be.Api.World.ElapsedMilliseconds / 1000));
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

    /// <summary>Snare/deadfall collection: the display slot held something that is NOT on the
    /// BE's own bait list (a catch) and the interact emptied it — that is a collected catch.
    /// Taking your own bait back credits nothing.</summary>
    public static class LandTrapCollectPatch
    {
        private static (string? code, bool wasBait) Read(BlockEntity be)
        {
            if (be is not BlockEntityContainer c || c.Inventory == null || c.Inventory.Count == 0) return (null, false);
            string? path = c.Inventory[0]?.Itemstack?.Collectible?.Code?.Path;
            if (path == null) return (null, false);
            bool bait = false;
            try
            {
                if (Traverse.Create(be).Field("baitTypes").GetValue() is string[] baits)
                    foreach (string b in baits)
                        if (path.Contains(b)) { bait = true; break; }
            }
            catch { }
            return (path, bait);
        }

        public static void Prefix(object __instance, out (string? code, bool wasBait) __state)
        {
            __state = __instance is BlockEntity be && be.Api?.Side == EnumAppSide.Server ? Read(be) : (null, false);
        }

        public static void Postfix(object __instance, (string? code, bool wasBait) __state)
        {
            if (__state.code == null || __state.wasBait) return;
            if (__instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            var (now, _) = Read(be);
            if (now != null) return; // nothing left the trap

            if (!trapOwners.TryGetValue(Key(be.Pos), out string? uid) || uid == null) return;
            IPlayer? owner = be.Api.World.PlayerByUid(uid);
            if (owner == null) return; // owner offline; their catch, but practice waits for them

            Core?.Ledger?.Log(owner, HunDomain.Code, HunDomain.TechTrapping, be.Pos.GetHashCode());
        }
    }
}

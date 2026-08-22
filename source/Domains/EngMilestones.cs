using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;
using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// The assembly milestone and the powered-station rig gate (fifth-pass rulings, 2026-08-22).
///
/// MILESTONE (the A7 replacement: automation use grants no practice, building it does). First
/// construction of a machine TYPE pays a large one-time credit, halving per repeat of that type
/// regardless of time, floored at 10 percent, per player, persisted in the world save. Paid at
/// first POWER DELIVERED (consumers) or first OUTPUT (generators), never at last-block-placed:
/// a decorative or misassembled machine pays nothing. Types are powered DEVICES, never parts:
/// windmill (vanilla + millwright pool, one type), waterwheel, helve hammer, pulverizer,
/// mechanized quern (a hand-cranked quern never pays), IW chopper, IW sawmill, and
/// conditionally IndustrialStory's reverberatory furnace, whose controller placer is the
/// builder and which pays at first heat received with the structure complete, read
/// reflectively off its own state, no IS patch at all (Jeffrey's pointer: the multiblock scans
/// for completion once the controller is placed).
///
/// Attribution is the BUILDER: the rigger for rotors (the sail-rigging grant seam), the placer
/// for placed devices, the part-completer for the IW multiblocks. A machine paid once and later
/// rebuilt on the same position pays nothing again (the paid set is per position); building the
/// next machine anywhere else pays the halved repeat. That is the cheap anti-farm, accepted and
/// documented. An offline builder's milestone waits in pending until they return.
///
/// RIG GATE (Workstream C, Apprentice I, "we can always tune it later"): the IW automated
/// stations require ENG Apprentice I to ASSEMBLE, gated at TryAddPart and ONLY while the
/// station is incomplete. A complete station's part swap is maintenance by the A7 ruling and
/// passes at any rank; feeding logs and taking outputs are never touched. Both sides, MET-gate
/// pattern, so the client never mispredicts a blocked part placement.
///
/// Also home to the documented-but-previously-fictional m-clamp: without wearandtear the
/// maintenance verb does not exist, so ENG's breadth math must not expect two techniques.
/// </summary>
public static class EngMilestones
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;
    private static ICoreServerAPI? sapi;

    // ------------------------------------------------------------ state (world save)

    /// <summary>Machines built but not yet powered: "x/y/z|type|builderUid|mode" (mode n =
    /// network, f = firing). Bounded by machines actually built and never run.</summary>
    private static readonly List<(BlockPos Pos, string Type, string Uid, bool Firing)> pending = new();
    /// <summary>uid -> machine type -> constructions already paid (drives the halving).</summary>
    private static Dictionary<string, Dictionary<string, int>> counts = new();
    /// <summary>Positions already paid, so a standing machine can never pay twice.</summary>
    private static HashSet<string> paid = new();

    private static string PosKey(BlockPos p) => $"{p.X}/{p.Y}/{p.Z}";

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.SaveGameLoaded += Load;
        api.Event.GameWorldSave += Save;
        api.Event.RegisterGameTickListener(Scan, 5000);

        // The m-clamp the EngDomain doc always claimed (verb-review defect, made real
        // 2026-08-22): no wearandtear, no maintenance verb, so M drops to 1. Applied on a
        // short delay so the ledger's config load is certainly done.
        if (!api.ModLoader.IsModEnabled("wearandtear"))
            api.Event.RegisterCallback(_ =>
            {
                var cfgs = Core?.Ledger?.DomainConfigs;
                if (cfgs != null && cfgs.TryGetValue(EngDomain.Code, out var dc) && dc.M > 1)
                {
                    dc.M = 1;
                    TcmLog.Cat(api, TcmLog.Config, "ENG m clamped to 1: wearandtear absent, maintenance verb inert");
                }
            }, 5000);
    }

    private static void Load()
    {
        try
        {
            byte[]? p = sapi!.WorldManager.SaveGame.GetData("almanacEngMilePending");
            byte[]? c = sapi.WorldManager.SaveGame.GetData("almanacEngMileCounts");
            byte[]? d = sapi.WorldManager.SaveGame.GetData("almanacEngMilePaid");
            pending.Clear();
            if (p != null)
                foreach (string line in SerializerUtil.Deserialize<List<string>>(p) ?? new())
                {
                    string[] f = line.Split('|');
                    if (f.Length != 4) continue;
                    string[] xyz = f[0].Split('/');
                    pending.Add((new BlockPos(int.Parse(xyz[0]), int.Parse(xyz[1]), int.Parse(xyz[2])), f[1], f[2], f[3] == "f"));
                }
            if (c != null) counts = SerializerUtil.Deserialize<Dictionary<string, Dictionary<string, int>>>(c) ?? new();
            if (d != null) paid = new HashSet<string>(SerializerUtil.Deserialize<List<string>>(d) ?? new());
            TcmLog.Cat(sapi, TcmLog.Config,
                $"ENG milestones loaded: {pending.Count} pending, {counts.Count} builder ledger(s), {paid.Count} paid position(s)");
        }
        catch (Exception e) { TcmLog.Error(sapi!, $"ENG milestone state unreadable ({e.Message}); starting empty"); }
    }

    private static void Save()
    {
        var p = new List<string>();
        foreach (var e in pending) p.Add($"{PosKey(e.Pos)}|{e.Type}|{e.Uid}|{(e.Firing ? "f" : "n")}");
        sapi!.WorldManager.SaveGame.StoreData("almanacEngMilePending", SerializerUtil.Serialize(p));
        sapi.WorldManager.SaveGame.StoreData("almanacEngMileCounts", SerializerUtil.Serialize(counts));
        sapi.WorldManager.SaveGame.StoreData("almanacEngMilePaid", SerializerUtil.Serialize(new List<string>(paid)));
    }

    // ------------------------------------------------------------ registration of a build

    /// <summary>A machine was constructed; watch it until it first runs. Idempotent per
    /// position, and a position that already paid never re-enters.</summary>
    public static void RegisterPending(BlockPos? pos, string type, IPlayer? player, bool firing = false)
    {
        if (sapi == null || pos == null || player == null) return;
        if (paid.Contains(PosKey(pos))) return;
        foreach (var e in pending) if (e.Pos.Equals(pos)) return;
        pending.Add((pos.Copy(), type, player.PlayerUID, firing));
        TcmLog.Cat(sapi, TcmLog.Hooks, $"ENG milestone pending: {type} at {pos} by {player.PlayerName}");
    }

    // ------------------------------------------------------------ the payment scan

    private static void Scan(float dt)
    {
        if (sapi == null || pending.Count == 0) return;
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var (pos, type, uid, firing) = pending[i];
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(pos) == null) continue;   // unloaded: wait
            var be = sapi.World.BlockAccessor.GetBlockEntity(pos);
            if (be == null) { pending.RemoveAt(i); continue; }                        // demolished unrun

            bool powered;
            if (firing)
            {
                // The reverberatory pays when it is genuinely running: structure complete AND
                // receiving heat, both read off IS's own state.
                try
                {
                    var tr = Traverse.Create(be);
                    powered = tr.Property("StructureComplete").GetValue<bool>()
                           && tr.Field("receivesHeat").GetValue<bool>();
                }
                catch { powered = false; }
            }
            else
            {
                var mp = be.GetBehavior<BEBehaviorMPBase>();
                if (mp == null) { pending.RemoveAt(i); continue; }                    // not that machine anymore
                powered = Math.Abs(mp.Network?.Speed ?? 0f) > 0.001f;
            }
            if (!powered) continue;

            var player = sapi.World.PlayerByUid(uid);
            if (player == null) continue;   // builder offline; their milestone waits for them

            int n = counts.TryGetValue(uid, out var per) && per.TryGetValue(type, out int cnt) ? cnt : 0;
            double weight = Math.Max(0.1, Math.Pow(0.5, n));
            Core?.Ledger?.Log(player, EngDomain.Code, EngDomain.TechMilestone,
                HashCode.Combine("engmilestone", pos.X, pos.Y, pos.Z), weight);
            if (per == null) counts[uid] = per = new Dictionary<string, int>();
            per[type] = n + 1;
            paid.Add(PosKey(pos));
            pending.RemoveAt(i);
            TcmLog.Cat(sapi, TcmLog.Hooks,
                $"ENG milestone paid: {player.PlayerName}'s {type} #{n + 1} (weight {weight:0.##})");
        }
    }

    // ------------------------------------------------------------ placement seams (vanilla + IS)

    /// <summary>Placed devices enter pending at placement, matched by their own block entity
    /// (type-safe for vanilla, name-matched for IS). The rotor family registers at the rigging
    /// grant instead, and the IW multiblocks at part completion.</summary>
    public static class MilestonePlacePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world?.Side != EnumAppSide.Server || byPlayer == null || blockSel == null) return;

            if (__instance is BlockWaterWheel)
            {
                RegisterPending(blockSel.Position, "waterwheel", byPlayer);
                return;
            }
            var be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
            string? type = be switch
            {
                BEHelveHammer => "helve",
                BEPulverizer => "pulverizer",
                BlockEntityQuern => "quern",
                _ => be != null && be.GetType().Name == "BlockEntityReverberatoryFurnace" ? "reverberatoryfurnace" : null,
            };
            if (type != null) RegisterPending(blockSel.Position, type, byPlayer, type == "reverberatoryfurnace");
        }
    }

    // ------------------------------------------------------------ IW: rig gate + completion

    internal static Type? ChopperBeType, SawmillBeType;

    private static int engDomainId = -2;

    private static int EngDomainId()
    {
        if (engDomainId != -2) return engDomainId;
        engDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == EngDomain.Code) { engDomainId = i; break; }
        return engDomainId;
    }

    /// <summary>Side-aware ENG level (the MET-gate pattern): server ledger, or the client's
    /// synced local-player state, so a blocked part placement is never mispredicted.</summary>
    private static int EngLevelOf(ICoreAPI api, IPlayer player)
    {
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(EngDomain.Code)?.Level ?? 0;
        var client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = EngDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    private static readonly Dictionary<string, long> lastWarn = new();
    private static readonly object errorSender = new();

    private static void Warn(ICoreAPI api, IPlayer player)
    {
        long now = api.World.ElapsedMilliseconds;
        if (lastWarn.TryGetValue(player.PlayerUID, out long last) && now - last < 2000) return;
        lastWarn[player.PlayerUID] = now;
        if (api is ICoreClientAPI capi)
            capi.TriggerIngameError(errorSender, "engriggate",
                Lang.Get("almanactcm:eng-gate-blocked", Domain.RankName(Rank.Apprentice)));
        else
            TcmLog.Cat(api, TcmLog.Hooks,
                $"ENG gate: {player.PlayerName} blocked from rigging a powered station (needs {Domain.RankName(Rank.Apprentice)})");
    }

    /// <summary>TryAddPart on chopper and sawmill: below Apprentice I an INCOMPLETE station
    /// refuses the part (kept in hand); a complete station's part swap is maintenance and
    /// passes at any rank. The postfix watches for the completion edge and registers the
    /// milestone to the completing hand.</summary>
    public static class IwRigPatch
    {
        public static bool Prefix(object __instance, IPlayer byPlayer, ref bool __result, out bool __state)
        {
            __state = false;
            if (__instance is not BlockEntity be || be.Api == null || byPlayer == null) return true;
            bool complete;
            try { complete = Traverse.Create(__instance).Property("IsComplete").GetValue<bool>(); }
            catch { return true; }
            __state = complete;
            if (complete) return true;                                   // maintenance, never gated
            if (EngLevelOf(be.Api, byPlayer) >= Rank.Apprentice) return true;

            Warn(be.Api, byPlayer);
            __result = false;
            return false;
        }

        public static void Postfix(object __instance, IPlayer byPlayer, bool __result, bool __state)
        {
            if (!__result || __state) return;                            // no add, or was already complete
            if (__instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
            bool complete;
            try { complete = Traverse.Create(__instance).Property("IsComplete").GetValue<bool>(); }
            catch { return; }
            if (!complete) return;                                       // still missing parts
            string type = ChopperBeType?.IsInstanceOfType(__instance) == true ? "chopper" : "sawmill";
            RegisterPending(be.Pos, type, byPlayer);
        }
    }

    // ------------------------------------------------------------ patch wiring

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        // Placement seam is vanilla-typed and always on; IS matching inside it is by name.
        harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.DoPlaceBlock)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(MilestonePlacePatch), "Postfix")));

        if (api.ModLoader.IsModEnabled("immersivewoodworking"))
        {
            ChopperBeType = AccessTools.TypeByName("ImmersiveWoodworking.BlockEntityChopper");
            SawmillBeType = AccessTools.TypeByName("ImmersiveWoodworking.BlockEntitySawmill");
            int wired = 0;
            foreach (var t in new[] { ChopperBeType, SawmillBeType })
            {
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "TryAddPart");
                if (m == null) continue;
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(IwRigPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(IwRigPatch), "Postfix")));
                wired++;
            }
            if (wired > 0) TcmLog.Info(api, $"ENG rig gate + milestone hooked to IW ({wired} station type(s), Apprentice I)");
            else TcmLog.Cat(api, TcmLog.Config, "immersivewoodworking present but TryAddPart seams absent; rig gate and IW milestones inactive");
        }

        TcmLog.Info(api, "ENG milestones live (windmill, waterwheel, helve, pulverizer, quern"
            + (api.ModLoader.IsModEnabled("industrialstory") ? ", reverberatory furnace" : "")
            + (api.ModLoader.IsModEnabled("immersivewoodworking") ? ", chopper, sawmill" : "") + ")");
    }
}

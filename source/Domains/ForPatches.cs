using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// FOR always-present (vanilla) hooks: the two gather verbs and the two-stat yield anchor.
///
/// Verb seams (technique-maps §FOR, verified against source 2026-07-16):
///   • harvesting — ZERO-Harmony: BlockBehaviorHarvestable pushes the `onitemcollected` bus event
///     (itemstack + byentityid) on every successful in-place pluck, INCLUDING herbarium's
///     tool-gated subclass (it overrides Start/Step only, so base Stop still fires). One listener
///     covers the whole family.
///   • gathering — Block.OnBlockBroken postfix filtered to wild flora (BlockPlant covers
///     mushroom/reeds by inheritance; BlockLooseRock covers loose stones; BlockLooseOres = flint
///     and surface ore bits) plus wild BlockCrop (no farmland beneath — vanilla's own FAR/FOR
///     line). Hard position-bucket contextHash: this is the spam floor, and strip-picking one
///     meadow cell dedups inside the window.
///
/// Yield (Axis 4 + the Axis 1 penalty end): forageDropRate (in-place) + wildCropDropRate (wild
/// uproot) set per player by rank on a reconcile tick — the exact MIN oreDropRate shape, both
/// stats WeightedSum base 1.0 (verified at EntityPlayer stat registration).
///
/// NOVEL-FINDS (ruled 2026-07-16): the first-ever harvest of a species pays a raw multiplier.
/// The seen-species set persists per player in the FOR side-state file.
/// </summary>
public static class ForPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;
    private static IServerWorldAccessor? serverWorld;

    private static double Knob(string key, double fallback) => ForDomain.Knob(key, fallback);

    // ============================================================ persisted side-state

    /// <summary>Per-player seen species (novel-finds) + spile-owner map (tapping). The spile BE
    /// stores no owner (verified: only a drip timer), so ownership lives here — the Collier's
    /// Mark pattern. Loaded in RegisterServer, saved on GameWorldSave.</summary>
    private sealed class ForState
    {
        public Dictionary<string, HashSet<string>> Seen { get; set; } = new();
        public Dictionary<string, string> Taps { get; set; } = new();
    }

    private static ForState state = new();
    private static ICoreServerAPI? sapi;

    private static string StateFileName
    {
        get
        {
            string name = sapi?.World.Config.GetString("AlmanacTcmSaveFile")
                ?? sapi?.WorldManager.SaveGame?.WorldName ?? "almanactcm_save";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(GamePaths.Saves, "AlmanacTcm", name + "-forstate.json");
        }
    }

    private static void LoadState()
    {
        try
        {
            string file = StateFileName;
            if (!File.Exists(file)) return;
            state = JsonConvert.DeserializeObject<ForState>(File.ReadAllText(file)) ?? new ForState();
            TcmLog.Cat(sapi, TcmLog.Config,
                $"FOR state loaded: {state.Seen.Count} foragers' species memory, {state.Taps.Count} tapline(s)");
        }
        catch (Exception e)
        {
            TcmLog.Error(sapi, $"forstate.json unreadable ({e.Message}); starting empty, NOT overwriting");
            state = new ForState();
        }
    }

    private static void SaveState()
    {
        try
        {
            string file = StateFileName;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonConvert.SerializeObject(state));
        }
        catch (Exception e) { TcmLog.Error(sapi, $"could not save FOR state: {e.Message}"); }
    }

    internal static string? TapOwner(BlockPos pos)
        => state.Taps.TryGetValue($"{pos.X}/{pos.Y}/{pos.Z}", out string? uid) ? uid : null;

    internal static void RememberTap(BlockPos pos, string uid) => state.Taps[$"{pos.X}/{pos.Y}/{pos.Z}"] = uid;

    /// <summary>True exactly once per player per species code. The multiplier that rewards
    /// ranging wider instead of stripping one bush (novel-finds ruling).</summary>
    private static bool IsNovel(IPlayer player, string speciesCode)
    {
        if (!state.Seen.TryGetValue(player.PlayerUID, out var seen))
        {
            seen = state.Seen[player.PlayerUID] = new HashSet<string>();
        }
        return seen.Add(speciesCode);
    }

    // ============================================================ registration

    public static void RegisterServer(ICoreServerAPI api)
    {
        sapi = api;
        serverWorld = api.World;
        LoadState();
        api.Event.GameWorldSave += SaveState;
        api.Event.RegisterGameTickListener(_ => ReconcileForageYield(api), 2000);
        TcmLog.Info(api, "FOR hooks live (harvesting, gathering, forage/wildcrop yield, novel-finds)");
    }

    // ============================================================ Axis 4/1 — the two yield stats

    private static readonly Dictionary<string, (double forage, double wildcrop)> lastYield = new();

    /// <summary>Sets forageDropRate + wildCropDropRate per player by FOR rank. A tick reconcile
    /// (robust to /tcm setlevel), writing only on change so WatchedAttributes stay quiet.</summary>
    private static void ReconcileForageYield(ICoreServerAPI api)
    {
        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            EntityPlayer? entity = player?.Entity;
            if (entity == null) continue;

            int level = ForDomain.LevelOf(player);
            double forage = ForDomain.RankLinear(level,
                Knob(ForDomain.ForageYieldUntrained, 0.9), Knob(ForDomain.ForageYieldGm, 1.15));
            double wildcrop = ForDomain.RankLinear(level,
                Knob(ForDomain.WildcropYieldUntrained, 0.9), Knob(ForDomain.WildcropYieldGm, 1.15));

            if (lastYield.TryGetValue(player!.PlayerUID, out var prev)
                && Math.Abs(prev.forage - forage) < 1e-4 && Math.Abs(prev.wildcrop - wildcrop) < 1e-4)
                continue;

            // Both stats are WeightedSum with base 1.0 (EntityPlayer registration), so a modifier
            // of (factor-1) makes GetBlended == factor — the verified oreDropRate arithmetic.
            entity.Stats.Set("forageDropRate", "almanactcm", (float)(forage - 1.0), false);
            entity.Stats.Set("wildCropDropRate", "almanactcm", (float)(wildcrop - 1.0), false);
            lastYield[player.PlayerUID] = (forage, wildcrop);
        }
    }

    // ============================================================ harvesting (the two pluck seams)

    // LESSON (0.3.57 -> 0.3.58): the technique map's "zero-Harmony" option — the `onitemcollected`
    // bus event — is NOT a harvest event. It is the tutorial system's generic "player received an
    // item" signal, pushed from FIVE sites including plain ground pickup (BehaviorCollectEntities)
    // and right-click pickup, so listening to it credited harvesting for every item a player
    // collected, including the drops of their own gathering breaks (live repro: catmint gave both
    // verbs). Harvesting therefore patches the two REAL pluck-completion seams instead:
    // BlockBehaviorHarvestable (resin, reeds, herbarium) and BEBehaviorFruitingBush (berry bushes).

    private static void CreditHarvest(IPlayer byPlayer, Block? block)
    {
        if (serverWorld == null || byPlayer == null) return;
        string species = block?.Code?.ToString() ?? "unknown";
        double mult = IsNovel(byPlayer, species) ? Knob(ForDomain.NovelFindMultiplier, 4.0) : 1.0;

        // contextHash = species + a 1s bucket: a berry run credits every bush (K is the ceiling);
        // the bucket only swallows a genuine double-fire.
        Core?.Ledger?.Log(byPlayer, ForDomain.Code, ForDomain.TechHarvesting,
            HashCode.Combine(species, serverWorld.ElapsedMilliseconds / 1000), mult);
    }

    /// <summary>In-place pluck on the block-behavior family (resin, reeds, herbarium's tool-gated
    /// plants — its subclass overrides Start/Step only, so this base Stop still runs). The postfix
    /// replicates the method's own success guard exactly: tool match, harvest time reached,
    /// harvested stacks present, server side.</summary>
    [HarmonyPatch(typeof(BlockBehaviorHarvestable), nameof(BlockBehaviorHarvestable.OnBlockInteractStop))]
    public static class HarvestablePluckPatch
    {
        private static readonly AccessTools.FieldRef<BlockBehaviorHarvestable, float> harvestTimeRef =
            AccessTools.FieldRefAccess<BlockBehaviorHarvestable, float>("harvestTime");

        public static void Postfix(BlockBehaviorHarvestable __instance, float secondsUsed,
            IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null) return;
            if (__instance.Tool != null && byPlayer.InventoryManager.ActiveTool != __instance.Tool) return;
            if (__instance.harvestedStacks == null || secondsUsed <= harvestTimeRef(__instance) - 0.05f) return;

            CreditHarvest(byPlayer, world.BlockAccessor.GetBlock(blockSel.Position));
        }
    }

    /// <summary>Berry/fruiting bushes (the OTHER pluck seam — bushes are block entities, not the
    /// harvestable block behavior). Success is detected as the growth-state transition the harvest
    /// itself performs (ripe -> Mature inside this one call), which sidesteps the method's private
    /// harvest-time multiplier math entirely.</summary>
    [HarmonyPatch(typeof(BEBehaviorFruitingBush), nameof(BEBehaviorFruitingBush.OnBlockInteractStop))]
    public static class FruitingBushPluckPatch
    {
        public static void Prefix(BEBehaviorFruitingBush __instance, out int __state)
        {
            __state = (int)(__instance.BState?.Growthstate ?? 0);
        }

        public static void Postfix(BEBehaviorFruitingBush __instance, IWorldAccessor world,
            IPlayer byPlayer, int __state)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null || __instance.BState == null) return;
            int now = (int)__instance.BState.Growthstate;
            if (now == __state || __instance.BState.Growthstate != EnumFruitingBushGrowthState.Mature) return;

            CreditHarvest(byPlayer, __instance.Block);
        }
    }

    // ============================================================ gathering (break-collect)

    /// <summary>Destructive wild gather: plants, mushrooms, reeds, loose surface litter, and
    /// wild (off-farmland) crops. The FOR spam floor — raw is small and the contextHash is an
    /// 8-block position bucket, so stripping one meadow cell dedups inside the window while a
    /// real ranging circuit keeps earning.</summary>
    [HarmonyPatch(typeof(Block), nameof(Block.OnBlockBroken))]
    public static class GatheringPracticePatch
    {
        public static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server || byPlayer == null) return;
            if (!IsWildGather(__instance, world, pos)) return;

            string species = __instance.Code?.ToString() ?? "unknown";
            double mult = IsNovel(byPlayer, species) ? Knob(ForDomain.NovelFindMultiplier, 4.0) : 1.0;

            Core?.Ledger?.Log(byPlayer, ForDomain.Code, ForDomain.TechGathering,
                HashCode.Combine(pos.X >> 3, pos.Y >> 3, pos.Z >> 3), mult);
        }

        private static bool IsWildGather(Block block, IWorldAccessor world, BlockPos pos)
        {
            // RULED 2026-07-16: grass is terrain clearing, not foraging — no credit, and a stray
            // grass swipe must not spend a cell's dedup window. Vanilla tallgrass is a plain
            // BlockPlant (code "tallgrass"), so it is excluded by code, not class.
            if (block.Code?.FirstCodePart() == "tallgrass") return false;
            // BlockPlant covers BlockMushroom + BlockReeds by inheritance; BlockLooseRock covers
            // loose stones; BlockLooseOres is surface flint/ore bits.
            if (block is BlockPlant || block is BlockLooseRock || block is BlockLooseOres) return true;
            // A crop with no farmland beneath is a WILD crop — vanilla's own FAR/FOR boundary
            // (BlockCrop applies wildCropDropRate on exactly this test). Farmland crops are FAR's.
            if (block is BlockCrop)
            {
                return world.BlockAccessor.GetBlockEntity(pos.DownCopy()) is not BlockEntityFarmland;
            }
            return false;
        }
    }
}

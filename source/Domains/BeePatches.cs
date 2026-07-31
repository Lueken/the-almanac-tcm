using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// BEE practice seams (bee-domain-design.md, seams appendix; every hook verified against
/// fresh decompiles of Oreki 2.0.0, FGC 2.0.8 and vanilla 1.22 on 2026-07-30).
///
/// Patch-time routing: these seams only hook when the BEE domain is enabled, and FarPatches
/// skips its beekeeping seams under the same test, so each seam has exactly one owner.
///
/// Oreki is taken through ONE prefix/postfix pair on BlockReusableBeehive.OnBlockInteractStart
/// with a before/after snapshot (population + the eight frame slots) rather than four patches
/// on its private slot methods: the snapshot diff yields the verb AND the per-slot context in
/// one place, and survives upstream refactors of the private helpers.
///
/// Deliberately silent surfaces, so future passes do not re-audit them:
///  - BELangstrothStack.TryTake is BOX disassembly (supers off a stack), not comb work: the
///    decompile shows frames never leave through it. Ruled combwork 2026-07-30 on the FrameRack
///    and the Super, where frames actually move; the stack take banks nothing.
///  - Oreki swarm arrival/settling has no player in scope anywhere (world behaviour end to
///    end), so the "caught swarm" half of hiving has no seam in Oreki 2.0.0 and grants nothing.
///  - The sting has no observable outcome at the patch site (vanilla rolls internally), so the
///    focus grace is armed by crushed comb only.
/// </summary>
public static class BeePatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    /// <summary>True once the seams hooked this process; CooPatches reads it at grant time to
    /// route the honeycomb mash (BEE rendering vs the FAR fallback).</summary>
    public static bool Active { get; private set; }

    // A1 focus grace (the 0.4.10 anvil precedent): after a crushed comb, a few seconds in
    // which the penalty band stands down, so one bad moment cannot cascade through a harvest.
    private static readonly Dictionary<string, long> focusUntil = new();

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!BeeDomain.Enabled(api))
        {
            TcmLog.Cat(api, TcmLog.Config,
                "BEE dormant: neither orekiwoofsbeehives nor fromgoldencombs present (beekeeping stays FAR #10; banked progress preserved)");
            return;
        }

        int hooked = 0;

        // ---- vanilla skep (also FGC's populated skep: BEFGCBeehive inherits BlockEntityBeehive)
        var skepDrops = AccessTools.Method(typeof(BlockSkep), nameof(BlockSkep.GetDrops));
        if (skepDrops != null)
        {
            harmony.Patch(skepDrops, postfix: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), nameof(SkepDropsPostfix))));
            hooked++;
        }
        else TcmLog.Warn(api, "BEE: BlockSkep.GetDrops not found; skep combwork inactive this build");

        var skepBreak = AccessTools.Method(typeof(BlockSkep), nameof(BlockSkep.OnBlockBroken));
        if (skepBreak != null)
        {
            harmony.Patch(skepBreak,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), nameof(StingPrefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), nameof(StingFinalizer))));
            hooked++;
        }
        else TcmLog.Warn(api, "BEE: BlockSkep.OnBlockBroken not found; the sting is inactive this build");

        // ---- oreki: the umbrella interact (populate + frame slots, one snapshot)
        if (api.ModLoader.IsModEnabled(BeeDomain.ModOreki))
        {
            var t = AccessTools.TypeByName("OrekiWoofsBeehives.Blocks.BlockReusableBeehive");
            var m = t == null ? null : AccessTools.DeclaredMethod(t, "OnBlockInteractStart");
            if (m != null)
            {
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), nameof(OrekiInteractPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), nameof(OrekiInteractPostfix))));
                hooked++;
            }
            else TcmLog.Warn(api, "BEE: BlockReusableBeehive.OnBlockInteractStart not found; oreki verbs inactive this build");
        }

        // ---- fgc: the brood pot (hiving branches + the harvest take), and the two
        //      surfaces frames actually leave through
        if (api.ModLoader.IsModEnabled(BeeDomain.ModFgc))
        {
            HookFgc(api, harmony, "FromGoldenCombs.BlockEntities.BECeramicBroodPot", "OnInteract",
                nameof(BroodPotPrefix), nameof(BroodPotPostfix), ref hooked);
            HookFgc(api, harmony, "FromGoldenCombs.BlockEntities.BEFrameRack", "TryTake",
                nameof(FgcTakePrefix), nameof(FgcTakePostfix), ref hooked);
            HookFgc(api, harmony, "FromGoldenCombs.BlockEntities.BELangstrothSuper", "TryTake",
                nameof(FgcTakePrefix), nameof(FgcTakePostfix), ref hooked);
        }

        Active = hooked > 0;
        TcmLog.Info(api, $"BEE live: {hooked} seam(s) hooked (RouteBeekeeping: FAR's beekeeping seams stand down)");
    }

    private static void HookFgc(ICoreAPI api, Harmony harmony, string typeName, string method,
        string prefix, string postfix, ref int hooked)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null)
        {
            TcmLog.Warn(api, $"BEE seam not found ({typeName}.{method}); that verb is inactive this build");
            return;
        }
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), prefix)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(BeePatches), postfix)));
        hooked++;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Property first, then field: Harmony's Traverse resolves only its own member
    /// kind and a miss returns null SILENTLY (the FAR harvesting lesson, WooIdgPatches).</summary>
    private static Traverse Member(Traverse t, string name)
    {
        var p = t.Property(name);
        return p.PropertyExists() ? p : t.Field(name);
    }

    private static string PathOf(ItemStack? stack) => stack?.Collectible?.Code?.Path ?? "";

    private static bool ServerSide(IWorldAccessor world) => world.Side == EnumAppSide.Server;

    private static void StartFocusGrace(ICoreAPI api, string uid)
        => focusUntil[uid] = api.World.ElapsedMilliseconds
            + (long)(BeeDomain.Knob(BeeDomain.FocusCooldownSeconds, 5) * 1000);

    private static bool InFocusGrace(ICoreAPI api, string uid)
        => focusUntil.TryGetValue(uid, out long t) && api.World.ElapsedMilliseconds < t;

    // ------------------------------------------------------------ vanilla skep

    /// <summary>Combwork at the ripe-skep harvest, and the Untrained crushed comb. The
    /// harvestable gate excludes the empty-skep-block return branch; the handbook path calls
    /// getHarvestableDrops directly and never lands here with a real pos.</summary>
    public static void SkepDropsPostfix(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ItemStack[]? __result)
    {
        if (byPlayer == null || !ServerSide(world)) return;
        if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBeehive beh || !beh.Harvestable) return;

        Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechCombwork,
            HashCode.Combine("beeskep", pos.X, pos.Y, pos.Z, world.ElapsedMilliseconds / 10000));

        // A1 crushed comb: an Untrained pull mishandles the frame and a portion is lost,
        // only on the strike that fails, never below one comb (principle 5: recoverable).
        if (__result == null || BeeDomain.LevelOf(byPlayer) > 0) return;
        var api = (beh as BlockEntity)?.Api;
        if (api == null || InFocusGrace(api, byPlayer.PlayerUID)) return;
        if (world.Rand.NextDouble() >= BeeDomain.Knob(BeeDomain.CrushChanceUntrained, 0.35)) return;

        foreach (var stack in __result)
        {
            if (stack == null || !PathOf(stack).Contains("honeycomb") || stack.StackSize < 2) continue;
            stack.StackSize -= 1;
            StartFocusGrace(api, byPlayer.PlayerUID);
            (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:bee-crush"),
                EnumChatType.Notification);
            TcmLog.Cat(api, TcmLog.Hooks, $"{byPlayer.PlayerName} crushed a comb at the skep (Untrained)");
            break;
        }
    }

    /// <summary>A1, the sting: an Untrained keeper works the hive clumsily and provokes the
    /// beemob roll more often. Rescale the private chance for the duration of the call and
    /// restore it in a finalizer (the MoldWear read-before/write-after precedent; the
    /// single-threaded server tick makes the window safe, the finalizer makes it exception-safe).</summary>
    public static void StingPrefix(BlockSkep __instance, IWorldAccessor world, IPlayer byPlayer, out float __state)
    {
        var chance = Traverse.Create(__instance).Field<float>("beemobSpawnChance");
        __state = chance.Value;
        if (byPlayer == null || !ServerSide(world) || BeeDomain.LevelOf(byPlayer) > 0) return;
        chance.Value = (float)Math.Min(1.0, __state * BeeDomain.Knob(BeeDomain.StingUntrained, 1.75));
    }

    public static void StingFinalizer(BlockSkep __instance, float __state)
    {
        Traverse.Create(__instance).Field<float>("beemobSpawnChance").Value = __state;
    }

    // ------------------------------------------------------------ oreki

    public class OrekiSnapshot
    {
        public double Population;
        public string?[] Slots = new string?[8];
        public BlockEntityContainer? Be;
    }

    public static void OrekiInteractPrefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, out OrekiSnapshot? __state)
    {
        __state = null;
        if (byPlayer == null || blockSel == null || !ServerSide(world)) return;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityContainer be) return;
        if (be.GetType().Name != "BlockEntityReusableBeehive") return;

        var snap = new OrekiSnapshot { Be = be };
        snap.Population = Member(Traverse.Create(be), "BeePopulation").GetValue<double>();
        for (int i = 0; i < 8 && i < be.Inventory.Count; i++)
            snap.Slots[i] = PathOf(be.Inventory[i].Itemstack);
        __state = snap;
    }

    public static void OrekiInteractPostfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, bool __result, OrekiSnapshot? __state)
    {
        if (__state?.Be == null || !__result) return;
        var be = __state.Be;
        var pos = blockSel.Position;

        // Hiving: the populate branch is the only pop-raising player path in this method
        // (a populated skep installed into a below-threshold hive).
        double popNow = Member(Traverse.Create(be), "BeePopulation").GetValue<double>();
        if (popNow > __state.Population)
        {
            Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechHiving,
                HashCode.Combine("beehiving", pos.X, pos.Y, pos.Z));
            return;
        }

        // Slot diff: a filled (non-feed) frame leaving is combwork; a feed frame arriving
        // is wintering. Context is hive position PLUS slot, the ruled dedup shape: an
        // eight-frame pull is eight contexts, a re-clicked empty slot is none.
        for (int i = 0; i < 8 && i < be.Inventory.Count; i++)
        {
            string before = __state.Slots[i] ?? "";
            string after = PathOf(be.Inventory[i].Itemstack);
            if (before == after) continue;

            bool wasFilledComb = before.Contains("beehiveframe-filled") && !before.Contains("-feed");
            bool nowFeed = after.Contains("beehiveframe-filled-feed");

            if (wasFilledComb && !after.Contains("beehiveframe-filled"))
                Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechCombwork,
                    HashCode.Combine("beeframe", pos.X, pos.Y, pos.Z, i));
            else if (nowFeed && !before.Contains("-feed"))
                Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechWintering,
                    HashCode.Combine("beefeed", pos.X, pos.Y, pos.Z, i));
        }
    }

    // ------------------------------------------------------------ fgc

    public class BroodPotSnapshot
    {
        public bool HadHarvest;
        public bool HandPopulatedSkep;
        public bool HandEmptySkep;
        public bool WasActiveHive;
        public BlockPos? Pos;
    }

    /// <summary>The brood pot's OnInteract runs three player verbs through one method: take the
    /// harvest (combwork), install a populated skep (hiving), and draw the colony back into an
    /// empty skep to move stock (hiving, the retrieval half). Snapshot enough to tell them apart.</summary>
    public static void BroodPotPrefix(object __instance, IPlayer byPlayer, out BroodPotSnapshot? __state)
    {
        __state = null;
        if (byPlayer == null || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;

        var snap = new BroodPotSnapshot { Pos = be.Pos };
        var tr = Traverse.Create(__instance);
        snap.WasActiveHive = Member(tr, "isActiveHive").GetValue<bool>();
        var inv = Member(tr, "inv").GetValue() as Vintagestory.API.Common.IInventory;
        snap.HadHarvest = inv != null && inv.Count > 0 && !inv[0].Empty;

        string hand = PathOf(byPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack);
        snap.HandPopulatedSkep = hand.StartsWith("skep-") && hand.Contains("-populated");
        snap.HandEmptySkep = hand.StartsWith("skep-") && hand.Contains("-empty");
        __state = snap;
    }

    public static void BroodPotPostfix(IPlayer byPlayer, bool __result, BroodPotSnapshot? __state)
    {
        if (__state?.Pos == null || !__result) return;
        var pos = __state.Pos;

        if (__state.HadHarvest)
            Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechCombwork,
                HashCode.Combine("beepot", pos.X, pos.Y, pos.Z,
                    (byPlayer.Entity?.World?.ElapsedMilliseconds ?? 0) / 10000));
        else if (__state.HandPopulatedSkep && !__state.WasActiveHive)
            Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechHiving,
                HashCode.Combine("beehiving", pos.X, pos.Y, pos.Z));
        else if (__state.HandEmptySkep && __state.WasActiveHive)
            Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechHiving,
                HashCode.Combine("beehiving-out", pos.X, pos.Y, pos.Z));
    }

    /// <summary>FrameRack / LangstrothSuper frame pulls. Both TryTake(IPlayer, BlockSelection)
    /// per-slot; the prefix captures the outgoing stack, the postfix grants only when a
    /// HARVESTABLE frame actually left (empty-frame shuffling banks nothing, the yield gate).</summary>
    public static void FgcTakePrefix(object __instance, IPlayer byPlayer, BlockSelection blockSel, out string __state)
    {
        __state = "";
        if (byPlayer == null || __instance is not BlockEntity be || be.Api?.Side != EnumAppSide.Server) return;
        var inv = Member(Traverse.Create(__instance), "inv").GetValue() as Vintagestory.API.Common.IInventory;
        int slot = blockSel?.SelectionBoxIndex ?? 0;
        if (inv != null && slot >= 0 && slot < inv.Count)
            __state = PathOf(inv[slot].Itemstack);
    }

    public static void FgcTakePostfix(object __instance, IPlayer byPlayer, BlockSelection blockSel, bool __result, string __state)
    {
        if (!__result || byPlayer == null || __instance is not BlockEntity be) return;
        if (!__state.Contains("harvestable")) return;
        Core?.Ledger?.Log(byPlayer, BeeDomain.Code, BeeDomain.TechCombwork,
            HashCode.Combine("beeframe", be.Pos.X, be.Pos.Y, be.Pos.Z, blockSel?.SelectionBoxIndex ?? 0));
    }
}

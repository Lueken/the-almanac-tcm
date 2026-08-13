using System.Collections;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace AlmanacTcm.Domains;

/// <summary>
/// Bespoke milestone detectors for Industrial Story's roasting heap and blast furnace.
///
/// The declarative mint (assets/almanactcm/almanac/triggers/*.json, Engine.KnowledgeTriggers)
/// only sees a block placed, used or broken. These six moments live in block-entity STATE: a
/// heap reaching its cap, a fire actually taking, ash paying out, a charge group being
/// swallowed, molten metal leaving the tower. Nothing in the trigger schema can see any of
/// that, so they are hand-cut seams.
///
/// Every key here is QUIET: minted with no toastLangKey, so it raises no discovery banner. The
/// quest-step toast is their only surface, and it takes its words from the guide checklist item
/// through Illuminated, so none of these keys needs a lang entry of its own.
///
/// Seams verified against the DECOMPILED IndustrialStory 0.7.5 assembly (2026-08-08), cited per
/// hook below. Every one is resolved by name and patched inside <see cref="PatchConditional"/>,
/// so a signature drift on any single seam WARNs and skips that seam alone rather than aborting
/// the mod (the 0.3.85 isolation lesson). Foreign members are read through Traverse for the
/// same reason: nothing here may appear in a [HarmonyPatch] attribute or a build without
/// Industrial Story would fail to load the class.
/// </summary>
public static class IsMilestonePatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    // The heap keys. `is` is the Industrial Story namespace already used by the declarative
    // triggers (almanactcm:is-first-roast and friends), kept for consistency.
    public const string RoastLoadedKey = "almanactcm:is-roast-loaded";
    public const string RoastLitKey = "almanactcm:is-roast-lit";
    public const string RoastCollectedKey = "almanactcm:is-roast-collected";

    // The blast furnace keys.
    public const string BlastChargedKey = "almanactcm:is-blast-charged";
    public const string BlastLitKey = "almanactcm:is-blast-lit";
    public const string BlastTappedKey = "almanactcm:is-blast-tapped";

    private const string HeapBlock = "IndustrialStory.BlockRoastingHeap";
    private const string HeapBe = "IndustrialStory.BlockEntityRoastingHeap";
    private const string BlastBlock = "IndustrialStory.BlockBlastFurnace";
    private const string BlastBe = "IndustrialStory.BlockEntityBlastFurnace";

    /// <summary>A charge group is 40 ore items, and OreUnits counts five units to the item
    /// (BlockEntityBlastFurnace.OreUnits => oreItemCount * 5, :99), so one whole group moves
    /// OreUnits by 200. The absorb takes groups or nothing (num8 whole groups at :620-648), and
    /// the fuel top-up that follows it (:649) moves no ore at all, which is exactly why this
    /// watches ore units rather than the operation's own "something happened" flag.</summary>
    private const int UnitsPerChargeGroup = 200;

    /// <summary>Blocks from the furnace within which a player is credited for a charge group.
    /// See the ChargeAbsorbPatch note: the absorb has no player in scope at all.</summary>
    private const double ChargeCreditRange = 10.0;

    // ------------------------------------------------------------------ seam wiring

    public static void PatchConditional(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("industrialstory")) return;

        // The heap's private roastables list is the only way to know whether a heap holds
        // anything: the block entity exposes CanAddRoastable (count < 24) and nothing that
        // separates empty from part-full. Resolved once here so the two seams that need it
        // skip loudly rather than misjudging an empty heap as loaded.
        var heapBeType = AccessTools.TypeByName(HeapBe);
        bool heapCount = heapBeType != null && AccessTools.Field(heapBeType, "roastables") != null;
        if (!heapCount)
            TcmLog.Warn(api, $"IS milestone: {HeapBe}.roastables not found; the heap ignite and collect milestones are inactive this build");

        // Heap load: TryAddRoastable is called from the block's own interact override
        // (BlockRoastingHeap.OnBlockInteractStart :28), which is where the inserting player
        // is. DECLARED because it is an override; the inherited Block method would miss it.
        HookPair(api, harmony, HeapBlock, "OnBlockInteractStart",
            typeof(HeapLoadPatch), "IS heap load");

        if (heapCount)
        {
            // Heap ignite: the block hands ignition to the entity (BlockRoastingHeap
            // .OnTryIgniteBlockOver :70 -> BlockEntityRoastingHeap.TryIgnite :258, which flips
            // `burning` only when CanIgnite :152). The entity method has no player; the block
            // method has the igniting EntityAgent.
            HookPair(api, harmony, HeapBlock, "OnTryIgniteBlockOver",
                typeof(HeapIgnitePatch), "IS heap ignite");

            // Heap collect: the payout runs inside the block's break override
            // (BlockRoastingHeap.OnBlockBroken :49 -> DropContents :343, which spawns every
            // roastable at :376-382). A prefix, because after the break there is no entity left
            // to read.
            HookPrefix(api, harmony, HeapBlock, "OnBlockBroken",
                typeof(HeapCollectPatch), "IS heap collect");
        }

        // Blast charge: BlockEntityBlastFurnace.ChargeFromColumn :537, private, called once a
        // second from OnServerTick1s :318.
        HookPair(api, harmony, BlastBe, "ChargeFromColumn",
            typeof(ChargeAbsorbPatch), "IS blast charge");

        // Blast ignite: BlockBlastFurnace.OnTryIgniteBlockOver :120 -> BlockEntityBlastFurnace
        // .TryIgnite :375, which sets IsBurning only when CanIgnite() :366 and only server-side.
        HookPair(api, harmony, BlastBlock, "OnTryIgniteBlockOver",
            typeof(BlastIgnitePatch), "IS blast ignite");

        // Blast tap: BlockEntityBlastFurnace.TryTapMoltenMetal :846, public and player-carrying.
        // It returns true on several paths that move nothing (the client-side early out at :879
        // among them), so the milestone watches MoltenUnits actually fall.
        HookPair(api, harmony, BlastBe, "TryTapMoltenMetal",
            typeof(TapPatch), "IS blast tap");
    }

    /// <summary>Patch a DECLARED method with the given nested class's Prefix and Postfix.
    /// Declared-strict throughout: every heap and furnace seam here is either an override or a
    /// private method, and AccessTools.Method silently walking up the hierarchy is how a patch
    /// ends up on the wrong body (the trough lesson).</summary>
    private static void HookPair(ICoreAPI api, Harmony harmony, string typeName, string method,
        System.Type patchClass, string label)
    {
        var m = Seam(api, typeName, method, label);
        if (m == null) return;
        harmony.Patch(m,
            prefix: new HarmonyMethod(AccessTools.Method(patchClass, "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(patchClass, "Postfix")));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
    }

    private static void HookPrefix(ICoreAPI api, Harmony harmony, string typeName, string method,
        System.Type patchClass, string label)
    {
        var m = Seam(api, typeName, method, label);
        if (m == null) return;
        harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(patchClass, "Prefix")));
        TcmLog.Info(api, $"{label} hooked ({typeName}.{method}, declared)");
    }

    private static System.Reflection.MethodInfo? Seam(ICoreAPI api, string typeName, string method, string label)
    {
        var t = AccessTools.TypeByName(typeName);
        var m = t == null ? null : AccessTools.DeclaredMethod(t, method);
        if (m == null)
            TcmLog.Warn(api, $"{label} seam not found ({typeName} does not DECLARE {method}); that milestone is inactive this build");
        return m;
    }

    // ------------------------------------------------------------------ shared helpers

    private static IPlayer? PlayerOf(EntityAgent? agent) => (agent as EntityPlayer)?.Player;

    private static bool ServerSide(IWorldAccessor? world) => world?.Side == EnumAppSide.Server;

    /// <summary>True when this player already carries the key. Checked before any measuring
    /// work so a heap that has been filled a hundred times costs nothing after the first.</summary>
    private static bool Has(IPlayer? player, string key)
    {
        if (player == null) return true;   // nobody to credit: treat as done, do no work
        var set = Core?.Server?.GetDomainSet(player);
        return set == null || set.Knowledge.ContainsKey(key);
    }

    /// <summary>Mint the key, quietly. No toastLangKey, so no banner: the quest-step toast is
    /// the whole surface. SetKnowledge is idempotent and syncs the client itself.</summary>
    private static void Mint(IPlayer? player, string key)
    {
        if (player == null || Has(player, key)) return;
        Core?.Server?.SetKnowledge(player, key, 1);
    }

    private static object? BeAt(IWorldAccessor? world, BlockPos? pos, string typeName)
    {
        if (world == null || pos == null) return null;
        object? be = world.BlockAccessor.GetBlockEntity(pos);
        var t = AccessTools.TypeByName(typeName);
        return t != null && t.IsInstanceOfType(be) ? be : null;
    }

    private static bool Flag(object? be, string property) =>
        be != null && Traverse.Create(be).Property(property).GetValue() is bool b && b;

    private static int Num(object? be, string property) =>
        be != null && Traverse.Create(be).Property(property).GetValue() is int n ? n : -1;

    /// <summary>How many roastables a heap holds, or -1 when the field cannot be read. The two
    /// seams that call this are only patched when the field resolved at startup.</summary>
    private static int Roastables(object? be) =>
        be != null && Traverse.Create(be).Field("roastables").GetValue() is ICollection c ? c.Count : -1;

    // ------------------------------------------------------------------ the heap

    /// <summary>A heap reaches its full cap on a player's insert.
    ///
    /// RULED: detect the FULL heap, not a tally of insertions, so a heap topped up across three
    /// sessions still counts. The cap is 24 (BlockEntityRoastingHeap.MaxRoastables :116) even
    /// though the renderer only draws one model per two roastables and stops at twelve
    /// (RoastablesPerMeshItem :122). The visual is not the truth here.
    ///
    /// The test is CanAddRoastable (:164, `!burning &amp;&amp; !burnt &amp;&amp; count &lt; 24`) flipping true to
    /// false across the interact. Only TryAddRoastable can move that count during an interact,
    /// and ignition takes a different path entirely, so a flip means the heap just filled.</summary>
    public static class HeapLoadPatch
    {
        public static void Prefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, out bool __state)
        {
            __state = false;
            if (!ServerSide(world) || Has(byPlayer, RoastLoadedKey)) return;
            __state = Flag(BeAt(world, blockSel?.Position, HeapBe), "CanAddRoastable");
        }

        public static void Postfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, bool __state)
        {
            if (!__state) return;
            object? be = BeAt(world, blockSel?.Position, HeapBe);
            if (be == null || Flag(be, "CanAddRoastable")) return;
            // Room ran out for one of three reasons; only the first is a full heap.
            if (Flag(be, "Burning") || Flag(be, "Burnt")) return;
            Mint(byPlayer, RoastLoadedKey);
        }
    }

    /// <summary>A loaded heap takes the torch. Credit the igniting player.</summary>
    public static class HeapIgnitePatch
    {
        public static void Prefix(EntityAgent byEntity, BlockPos pos, out bool __state)
        {
            __state = false;
            IPlayer? player = PlayerOf(byEntity);
            if (player == null || !ServerSide(byEntity?.World) || Has(player, RoastLitKey)) return;
            object? be = BeAt(byEntity?.World, pos, HeapBe);
            // Loaded and not yet alight. An empty pile lighting is a bonfire, not a roast.
            __state = be != null && !Flag(be, "Burning") && Roastables(be) >= 1;
        }

        public static void Postfix(EntityAgent byEntity, BlockPos pos, bool __state)
        {
            if (!__state) return;
            if (Flag(BeAt(byEntity?.World, pos, HeapBe), "Burning")) Mint(PlayerOf(byEntity), RoastLitKey);
        }
    }

    /// <summary>A burnt heap is broken open and pays out its roasted ore. Prefix only: the block
    /// entity is gone by the time a postfix would run.
    ///
    /// The creative-mode condition mirrors the original's own (BlockRoastingHeap.OnBlockBroken
    /// :47), because that is the branch that decides whether DropContents runs at all. No drop,
    /// no milestone.</summary>
    public static class HeapCollectPatch
    {
        public static void Prefix(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            if (!ServerSide(world) || byPlayer == null) return;
            if (byPlayer.WorldData?.CurrentGameMode == EnumGameMode.Creative) return;
            if (Has(byPlayer, RoastCollectedKey)) return;
            object? be = BeAt(world, pos, HeapBe);
            // Burnt means ConvertToAsh :304 already swapped every stack for its roasted form,
            // so a burnt heap holding stacks is a payout of roasted ore by definition.
            if (be == null || !Flag(be, "Burnt") || Roastables(be) < 1) return;
            Mint(byPlayer, RoastCollectedKey);
        }
    }

    // ------------------------------------------------------------------ the blast furnace

    /// <summary>The furnace swallows a whole charge group: 40 ore, 2 fuel, 1 lime
    /// (BlockEntityBlastFurnace :617-619, where BatchFuelItems(200) is 2 and BatchFluxItems(200)
    /// is 1). Measured as OreUnits rising by a group's worth, which is the one signal the
    /// fuel-only top-up path cannot fake.
    ///
    /// ATTRIBUTION COMPROMISE, and it is a real one: the absorb is a once-a-second scan over
    /// thrown item entities in the shaft, so no player is in scope at any point. Nobody
    /// interacted; someone stood on the roof and dropped ore down a hole a second ago. The
    /// credit therefore goes to the nearest player within ten blocks, and to nobody at all when
    /// the furnace is unattended. That misfires in exactly one situation: two players at one
    /// furnace, where the one standing closer takes the tick. Accepted as the honest cost of a
    /// seam with no player on it; the alternative is no milestone here at all.</summary>
    public static class ChargeAbsorbPatch
    {
        public static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            if (__instance is BlockEntity be && be.Api?.Side == EnumAppSide.Server)
                __state = Num(__instance, "OreUnits");
        }

        public static void Postfix(object __instance, int __state)
        {
            if (__state < 0 || __instance is not BlockEntity be) return;
            if (Num(__instance, "OreUnits") - __state < UnitsPerChargeGroup) return;

            BlockPos pos = be.Pos;
            double cx = pos.X + 0.5, cy = pos.Y + 0.5, cz = pos.Z + 0.5;
            IPlayer? nearest = be.Api?.World?.NearestPlayer(cx, cy, cz);
            EntityPos? at = nearest?.Entity?.Pos;
            if (at == null) return;
            double dx = at.X - cx, dy = at.Y - cy, dz = at.Z - cz;
            if (dx * dx + dy * dy + dz * dz > ChargeCreditRange * ChargeCreditRange) return;
            Mint(nearest, BlastChargedKey);
        }
    }

    /// <summary>The tower takes the torch. Credit the igniting player.</summary>
    public static class BlastIgnitePatch
    {
        public static void Prefix(EntityAgent byEntity, BlockPos pos, out bool __state)
        {
            __state = false;
            IPlayer? player = PlayerOf(byEntity);
            if (player == null || !ServerSide(byEntity?.World) || Has(player, BlastLitKey)) return;
            object? be = BeAt(byEntity?.World, pos, BlastBe);
            __state = be != null && !Flag(be, "IsBurning");
        }

        public static void Postfix(EntityAgent byEntity, BlockPos pos, bool __state)
        {
            if (!__state) return;
            if (Flag(BeAt(byEntity?.World, pos, BlastBe), "IsBurning")) Mint(PlayerOf(byEntity), BlastLitKey);
        }
    }

    /// <summary>A tap that actually moves metal. TryTapMoltenMetal returns true from the client
    /// side without pouring anything (:879), and its pour loop can break having moved nothing
    /// (:889), so the return value alone is not evidence. MoltenUnits falling is.</summary>
    public static class TapPatch
    {
        public static void Prefix(object __instance, IPlayer byPlayer, out int __state)
        {
            __state = -1;
            if (__instance is BlockEntity be && be.Api?.Side == EnumAppSide.Server
                && !Has(byPlayer, BlastTappedKey))
            {
                __state = Num(__instance, "MoltenUnits");
            }
        }

        public static void Postfix(object __instance, IPlayer byPlayer, bool __result, int __state)
        {
            if (!__result || __state < 0) return;
            int after = Num(__instance, "MoltenUnits");
            if (after >= 0 && after < __state) Mint(byPlayer, BlastTappedKey);
        }
    }
}

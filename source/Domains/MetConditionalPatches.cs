using System;
using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AlmanacTcm.Domains;

/// <summary>
/// MET hooks into OPTIONAL mods (Toolsmith, Smithing+, WearAndTear). Every target
/// type is resolved by name at runtime; none may appear in a [HarmonyPatch]
/// attribute or installs without that mod would fail to load the patch class.
/// Each hook degrades to nothing when its mod is absent (graceful-degradation law).
/// </summary>
public static class MetConditionalPatches
{
    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    private static int MetLevel(IPlayer player)
    {
        var domainSet = Core?.Server?.GetDomainSet(player);
        return domainSet?.FindDomain(MetDomain.Code)?.Level ?? 0;
    }

    private static double Knob(string key, double fallback)
    {
        var configs = Core?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(MetDomain.Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }

    public static void PatchAllPresent(ICoreAPI api, Harmony harmony)
    {
        PatchToolsmithWorkbench(api, harmony);
        PatchToolsmithHeldCraft(api, harmony);
        PatchSmithingPlusBits(api, harmony);
        PatchWearAndTearMolds(api, harmony);
        PatchIndustrialStorySmelting(api, harmony);
    }

    // -------------------------------------- industrialstory smelting (practice seams)
    //
    // The vanilla smelting grant rides the held-crucible pour (MetPatches.PourContextPatch),
    // and industrialstory routes almost never touch it: the pit furnace spawns the mass
    // directly, and the taller furnaces tap molten metal into channels. Without these seams
    // MET smelting practice is nearly unearnable on an industrialstory world. Two shapes:
    //
    // - Pit furnace (the stage-I first-copper road): the skilled act is working the
    //   blowpipe, so the last blower is captured per pit and the grant lands when
    //   FinishSmelting actually produces masses (the POT pit-firing pattern: owner at
    //   the act, success-gated grant at completion).
    // - Tap furnaces (small smelter, retort, blast, reverberatory): the tap IS the
    //   player act, so a successful TryTapMoltenMetal grants directly.

    /// <summary>Last blowpipe worker per pit furnace position. Transient by design: a
    /// pit firing runs minutes, and losing attribution across a restart only skips one
    /// grant (the trough-filler precedent, not the persisted kiln-owner one).</summary>
    private static readonly Dictionary<string, string> pitBlowers = new();

    private static string PosKey(BlockPos pos) => $"{pos.X}/{pos.Y}/{pos.Z}";

    public static class PitBlowPatch
    {
        public static void Postfix(object __instance, IPlayer player)
        {
            if (player == null) return;
            var be = __instance as BlockEntity;
            if (be?.Api?.Side != EnumAppSide.Server || be.Pos == null) return;
            ItemStack? held = player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (held?.Collectible?.Code?.Path?.Contains("blowpipe") != true) return;
            pitBlowers[PosKey(be.Pos)] = player.PlayerUID;
        }
    }

    public static class PitFinishPatch
    {
        public static void Prefix(object __instance, out int __state)
        {
            // Output size is decided from the ore slot BEFORE FinishSmelting clears it.
            __state = 0;
            var oreSlot = Traverse.Create(__instance).Property<ItemSlot>("OreSlot").Value;
            if (oreSlot?.Itemstack != null) __state = oreSlot.StackSize / 20;
        }

        public static void Postfix(object __instance, int __state)
        {
            if (__state <= 0) return;   // burned out with too little ore: nothing made
            var be = __instance as BlockEntity;
            if (be?.Api is not ICoreServerAPI sapi || be.Pos == null) return;
            if (!pitBlowers.TryGetValue(PosKey(be.Pos), out string? uid)) return;
            pitBlowers.Remove(PosKey(be.Pos));

            IPlayer? blower = sapi.World.PlayerByUid(uid);
            if (blower == null) return;   // logged off mid-firing: the grant is forfeit
            Core?.Ledger?.Log(blower, MetDomain.Code, MetDomain.TechSmelting,
                HashCode.Combine("pitfurnace", PosKey(be.Pos), sapi.World.Calendar.TotalDays));
            TcmLog.Cat(sapi, TcmLog.Hooks, $"{blower.PlayerName} pit-smelted {__state} mass(es), smelting credited");
        }
    }

    public static class TapPatch
    {
        // Bool-returning taps gate on success; the void retort tap grants on the call
        // (the dedup ring bounds repeat swings at the same retort).
        public static void BoolPostfix(object __instance, IPlayer byPlayer, bool __result)
        {
            if (__result) Grant(__instance, byPlayer);
        }

        public static void VoidPostfix(object __instance, IPlayer byPlayer)
        {
            Grant(__instance, byPlayer);
        }

        public static void ReverbPostfix(object __instance, IPlayer byPlayer, bool __result)
        {
            if (__result) Grant(__instance, byPlayer);
        }

        private static void Grant(object instance, IPlayer? byPlayer)
        {
            var be = instance as BlockEntity;
            if (byPlayer == null || be?.Api?.Side != EnumAppSide.Server || be.Pos == null) return;
            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechSmelting,
                HashCode.Combine("tap", PosKey(be.Pos), be.Api.World.ElapsedMilliseconds / 60000));
        }
    }

    private static void PatchIndustrialStorySmelting(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("industrialstory")) return;

        int hooked = 0;

        var pitType = AccessTools.TypeByName("IndustrialStory.BlockEntityPitFurnace");
        var blow = pitType == null ? null : AccessTools.Method(pitType, "OnInteract");
        var finish = pitType == null ? null : AccessTools.Method(pitType, "FinishSmelting");
        if (blow != null && finish != null)
        {
            harmony.Patch(blow, postfix: new HarmonyMethod(AccessTools.Method(typeof(PitBlowPatch), "Postfix")));
            harmony.Patch(finish,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PitFinishPatch), "Prefix")),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PitFinishPatch), "Postfix")));
            hooked++;
        }
        else
        {
            TcmLog.Warn(api, "industrialstory present but PitFurnace OnInteract/FinishSmelting not found; pit smelting practice inactive");
        }

        foreach (var (type, postfix) in new[]
        {
            ("IndustrialStory.BlockEntitySmallSmelter", "BoolPostfix"),
            ("IndustrialStory.BlockEntityBlastFurnace", "BoolPostfix"),
            ("IndustrialStory.BlockEntityReverberatoryFurnace", "ReverbPostfix"),
            ("IndustrialStory.BlockEntityRetortSmelter", "VoidPostfix"),
        })
        {
            var m = AccessTools.Method(AccessTools.TypeByName(type), "TryTapMoltenMetal");
            if (m == null) continue;
            harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(TapPatch), postfix)));
            hooked++;
        }

        TcmLog.Info(api, $"MET smelting practice hooked to industrialstory ({hooked} seam(s): pit blowpipe + molten taps)");
    }

    // ------------------------------------------------ Toolsmith workbench (assembly)

    public static class WorkbenchCraftPatch
    {
        public static void Prefix(object __instance, out bool __state)
        {
            // Completion only counts when the third strike lands: hits were already
            // >=3 going in. Earlier calls are the wiggle build-up and also return true.
            __state = Traverse.Create(__instance).Field<int>("craftingHitsCount").Value >= 3;
        }

        public static void Postfix(IWorldAccessor world, IPlayer byPlayer, bool __result, bool __state)
        {
            if (!__state || !__result || byPlayer == null) return;
            if (world.Side != EnumAppSide.Server) return;
            // Covers both the fresh craft and the reforge-merge branch: both produce
            // a finished usable tool on the bench.
            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechAssembly,
                HashCode.Combine("workbench", world.ElapsedMilliseconds / 1000));
        }
    }

    private static void PatchToolsmithWorkbench(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("toolsmith")) return;
        var method = AccessTools.Method(
            AccessTools.TypeByName("Toolsmith.ToolTinkering.Blocks.BlockEntityWorkbench"), "AttemptToCraft");
        if (method == null)
        {
            TcmLog.Warn(api, "toolsmith present but BlockEntityWorkbench.AttemptToCraft not found; workbench assembly inactive");
            return;
        }
        harmony.Patch(method,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(WorkbenchCraftPatch), "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(WorkbenchCraftPatch), "Postfix")));
        TcmLog.Info(api, "assembly verb hooked to Toolsmith workbench");
    }

    // ------------------------------- Toolsmith HELD craft (assembly, 4th path)

    /// <summary>Toolsmith's in-hand flow (live-trial find: the path Jeffrey actually
    /// uses): head+handle hold → parts BUNDLE (intermediate, not counted) → second
    /// hold → AssembleFullTool = the finished usable tool. Postfix logs only when
    /// the slot really became a tool (the method is void; the slot check is the
    /// success signal).</summary>
    public static class HeldCraftPatch
    {
        public static void Postfix(ItemSlot bundleSlot, EntityAgent byEntity)
        {
            if (byEntity?.World?.Side != EnumAppSide.Server) return;
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            ItemStack? stack = bundleSlot?.Itemstack;
            if (player == null || stack?.Collectible?.Tool == null) return;
            if (stack.Collectible.ToolTier < 2) return;

            Core?.Ledger?.Log(player, MetDomain.Code, MetDomain.TechAssembly,
                HashCode.Combine(stack.Collectible.Id, byEntity.World.ElapsedMilliseconds / 1000));
        }
    }

    private static void PatchToolsmithHeldCraft(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("toolsmith")) return;
        var method = AccessTools.Method(
            AccessTools.TypeByName("Toolsmith.ToolTinkering.TinkeringUtility"), "AssembleFullTool");
        if (method == null)
        {
            TcmLog.Warn(api, "toolsmith present but TinkeringUtility.AssembleFullTool not found; held-craft assembly inactive");
            return;
        }
        harmony.Patch(method,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HeldCraftPatch), "Postfix")));
        TcmLog.Info(api, "assembly verb hooked to Toolsmith held craft (AssembleFullTool)");
    }

    // ------------------------------------------- Smithing+ bit recovery (Axis 4)

    public static class BitRecoveryPatch
    {
        /// <summary>Seeds the sp:splitCount accumulator with a rank-scaled delta
        /// BEFORE Smithing+'s own +1/VoxelsPerBit and crossing logic run unchanged
        /// (the dive-recommended seam: no ordering games on OnUseOver, no touching
        /// their shared config singleton). Helve-hammer path stays stock (no player).</summary>
        public static void Prefix(IPlayer byPlayer, ItemStack workItemStack)
        {
            if (byPlayer == null || workItemStack == null) return;
            double scale = MetDomain.RankLinear(MetLevel(byPlayer),
                Knob(MetDomain.BitRecoveryUntrained, 0.7),
                Knob(MetDomain.BitRecoveryGm, 1.3));
            if (scale == 1.0) return;

            float perSplit = 1f / VoxelsPerBit();
            float seeded = workItemStack.TempAttributes.GetFloat("sp:splitCount")
                + (float)((scale - 1.0) * perSplit);
            workItemStack.TempAttributes.SetFloat("sp:splitCount", Math.Max(seeded, 0f));
        }

        private static float cachedVoxelsPerBit;

        private static float VoxelsPerBit()
        {
            if (cachedVoxelsPerBit > 0) return cachedVoxelsPerBit;
            try
            {
                object? config = AccessTools.Property(AccessTools.TypeByName("SmithingPlus.Core"), "Config")
                    ?.GetValue(null);
                object? value = config == null ? null
                    : AccessTools.Property(config.GetType(), "VoxelsPerBit")?.GetValue(config)
                      ?? AccessTools.Field(config.GetType(), "VoxelsPerBit")?.GetValue(config);
                cachedVoxelsPerBit = value is float f && f > 0 ? f : 2.1f;
            }
            catch (Exception)
            {
                cachedVoxelsPerBit = 2.1f;
            }
            return cachedVoxelsPerBit;
        }
    }

    private static void PatchSmithingPlusBits(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("smithingplus")) return;
        var method = AccessTools.Method(
            AccessTools.TypeByName("SmithingPlus.BitsRecovery.BitsRecoveryPatches"), "RecoverBitsFromWorkItem");
        if (method == null)
        {
            TcmLog.Warn(api, "smithingplus present but RecoverBitsFromWorkItem not found; bit-recovery scaling inactive");
            return;
        }
        harmony.Patch(method,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(BitRecoveryPatch), "Prefix")));
        TcmLog.Info(api, "Axis 4 bit-recovery scaling hooked to Smithing+");
    }

    // --------------------------------------------- WearAndTear mold wear (Axis 4)

    public static class MoldWearPatch
    {
        /// <summary>Wear applies on TAKE-CONTENTS (per-use, no time decay — dive
        /// verified). Rescale the applied delta by rank: masters' molds last longer,
        /// Untrained chews them. WearAndTear's own xSkills branches go dead without
        /// xlib, so this is the sole rank lever at ship.</summary>
        public static void Prefix(object __instance, out float __state)
        {
            __state = Traverse.Create(__instance).Property<float>("Durability").Value;
        }

        public static void Postfix(object __instance, IPlayer byPlayer, float __state)
        {
            if (byPlayer == null) return;
            var durability = Traverse.Create(__instance).Property<float>("Durability");
            float applied = __state - durability.Value;
            if (applied <= 0) return;

            double scale = MetDomain.RankLinear(MetLevel(byPlayer),
                Knob(MetDomain.MoldWearUntrained, 1.25),
                Knob(MetDomain.MoldWearGm, 0.6));
            if (scale == 1.0) return;
            durability.Value = __state - (float)(applied * scale);
        }
    }

    private static void PatchWearAndTearMolds(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("wearandtear")) return;
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(MoldWearPatch), "Prefix"));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(MoldWearPatch), "Postfix"));
        int hooked = 0;
        foreach (string typeName in new[]
        {
            "WearAndTear.Code.Behaviours.Parts.MoldPart",
            "WearAndTear.Code.Behaviours.Parts.IngotMoldPart",
        })
        {
            var method = AccessTools.Method(AccessTools.TypeByName(typeName), "Damage");
            if (method == null) continue;
            harmony.Patch(method, prefix: prefix, postfix: postfix);
            hooked++;
        }
        if (hooked > 0) TcmLog.Info(api, $"Axis 4 mold-wear scaling hooked to WearAndTear ({hooked} part types)");
        else TcmLog.Warn(api, "wearandtear present but MoldPart.Damage not found; mold-wear scaling inactive");
    }
}

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

    // ------------------------------------ industrialstory casting sand (TechCasting)
    //
    // Sand casting is the mass-production road, and it fills nothing like a vanilla tool mold:
    // BlockEntitySmallSmelter.TryTapMoltenMetal (and the blast, reverb and retort equivalents)
    // runs the WHOLE pour synchronously, and BlockEntityCastingSand.ReceiveLiquidMetal routes the
    // stream through connected channels to every mold it can reach. One hammer strike therefore
    // completes every connected mold in the same instant.
    //
    // RULED 2026-07-29: that simultaneity must NOT cost the player anything. Each completed mold
    // is its own practice event keyed on its own position, so the dedup ring sees distinct
    // contexts and collapses none of them; a four-mold pour banks four times a single mold. The
    // per-mold value is scaled down instead (MetDomain.SandCastFactor), which is what makes bulk
    // casting worth less per item than a hand-poured mold without punishing the batch.
    //
    // Attribution follows the pour or the tap, never whoever collects the casting later: the tap
    // stashes its player for the duration of the call, and a crucible tipped into a bed by hand
    // is read from MetPatches.PouringPlayer. Both are ambient-context reads with no stored-owner
    // chain, because the entire fill is synchronous inside the acting player's own interaction.

    /// <summary>Tapping player for the duration of one TryTapMoltenMetal call. ThreadStatic and
    /// finalizer-cleared: the pour is synchronous on the server thread, so this cannot leak
    /// between players or survive an exception mid-pour.</summary>
    [ThreadStatic] private static string? tappingUid;

    public static class TapContextPatch
    {
        public static void Prefix(IPlayer byPlayer) => tappingUid = byPlayer?.PlayerUID;

        public static void Finalizer() => tappingUid = null;
    }

    public static class SandCastFillPatch
    {
        /// <summary>Fullness BEFORE the metal lands, so only the pour that COMPLETES a mold
        /// counts. A mold topped up across two taps pays once, at the tap that finished it.</summary>
        public static void Prefix(object __instance, out bool __state)
        {
            __state = true;
            try { __state = Traverse.Create(__instance).Property<bool>("IsFull").Value; }
            catch (Exception) { }
        }

        public static void Postfix(object __instance, bool __state)
        {
            if (__state) return;   // already full when the metal arrived
            var be = __instance as BlockEntity;
            if (be?.Api?.Side != EnumAppSide.Server || be.Pos == null) return;

            var probe = Traverse.Create(__instance);
            // Channels route metal but hold no casting; only a mold that just filled is practice.
            if (!probe.Property<bool>("IsMold").Value) return;
            if (!probe.Property<bool>("IsFull").Value) return;

            IPlayer? caster = tappingUid != null ? be.Api.World.PlayerByUid(tappingUid) : null;
            caster ??= MetPatches.PouringPlayer;
            if (caster == null) return;   // unattended flow with nobody to credit

            Core?.Ledger?.Log(caster, MetDomain.Code, MetDomain.TechCasting,
                HashCode.Combine("sandcast", PosKey(be.Pos)),
                Knob(MetDomain.SandCastFactor, 0.35));
        }
    }

    // ------------------------------------ industrialstory brick furnace (TechAlloying)
    //
    // Under industrialstory the brick furnace is the ONLY way to alloy, so without this seam
    // TechAlloying is unearnable on the whole server. The completion, BlockEntityBrickFurnace
    // .smeltItems, is unattended: it fires on a cook tick with no player in scope, so this uses
    // the established owner-at-the-act shape (the pit furnace above, the POT kiln, the BRE seal).
    // Whoever last tended the furnace is stamped per position and credited when the melt lands.
    //
    // Alloy versus plain melt is classified the same way the vanilla crucible path does it, by
    // asking the smelting container for a matching alloy, so melting a single metal in the brick
    // furnace banks smelting rather than alloying.

    /// <summary>Last player to handle a brick furnace, by position. Transient by design: a melt
    /// runs minutes, and losing the stamp across a restart costs one grant, which is the
    /// trough-filler precedent rather than the persisted kiln-owner one.</summary>
    private static readonly Dictionary<string, string> furnaceTenders = new();

    public static class BrickFurnaceTendPatch
    {
        public static void Postfix(object __instance, IPlayer byPlayer)
        {
            if (byPlayer == null) return;
            var be = __instance as BlockEntity;
            if (be?.Api?.Side != EnumAppSide.Server || be.Pos == null) return;
            if (furnaceTenders.Count > 128) furnaceTenders.Clear();
            furnaceTenders[PosKey(be.Pos)] = byPlayer.PlayerUID;
        }
    }

    public static class BrickFurnaceSmeltPatch
    {
        /// <summary>Classify BEFORE the smelt consumes the inputs: true = the crucible held a
        /// real alloy recipe, false = a single metal being melted.</summary>
        public static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            try
            {
                var be = __instance as BlockEntity;
                if (be?.Api == null) return;
                var inv = Traverse.Create(__instance).Field("inventory").GetValue() as IInventory;
                var input = inv?[1]?.Itemstack;
                if (input?.Collectible is not Vintagestory.GameContent.BlockSmeltingContainer container) return;
                if (__instance is not ISlotProvider slots) return;
                ItemStack[] stacks = container.GetIngredients(be.Api.World, slots);
                __state = container.GetMatchingAlloy(be.Api.World, stacks) != null;
            }
            catch (Exception) { }
        }

        public static void Postfix(object __instance, bool __state)
        {
            var be = __instance as BlockEntity;
            if (be?.Api?.Side != EnumAppSide.Server || be.Pos == null) return;
            if (!furnaceTenders.TryGetValue(PosKey(be.Pos), out string? uid)) return;

            IPlayer? tender = be.Api.World.PlayerByUid(uid);
            if (tender == null) return;   // tender logged off mid-melt: the grant is forfeit

            Core?.Ledger?.Log(tender, MetDomain.Code,
                __state ? MetDomain.TechAlloying : MetDomain.TechSmelting,
                HashCode.Combine("brickfurnace", PosKey(be.Pos), be.Api.World.Calendar.TotalHours));
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
            // Prefix stashes the tapper so the casting-sand molds this pour fills can be credited;
            // the finalizer clears it even if the pour throws partway down the channel run.
            harmony.Patch(m,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TapContextPatch), "Prefix")),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TapPatch), postfix)),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(TapContextPatch), "Finalizer")));
            hooked++;
        }

        // Casting sand: one grant per mold COMPLETED, credited to the tapper or the pourer.
        var sandFill = AccessTools.Method(
            AccessTools.TypeByName("IndustrialStory.BlockEntityCastingSand"), "ReceiveLiquidMetal");
        if (sandFill != null)
        {
            harmony.Patch(sandFill,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SandCastFillPatch), "Prefix")),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(SandCastFillPatch), "Postfix")));
            hooked++;
            TcmLog.Info(api, "MET casting practice hooked to industrialstory casting sand (per mold filled)");
        }
        else
        {
            TcmLog.Warn(api, "industrialstory present but BlockEntityCastingSand.ReceiveLiquidMetal not found; sand-casting practice inactive");
        }

        // Brick furnace: the only alloying route industrialstory offers, so TechAlloying lives
        // or dies with this seam. Tend stamp + unattended completion.
        var furnaceType = AccessTools.TypeByName("IndustrialStory.BlockEntityBrickFurnace");
        var furnaceSmelt = furnaceType == null ? null : AccessTools.Method(furnaceType, "smeltItems");
        var furnaceTend = furnaceType == null ? null : AccessTools.Method(furnaceType, "OnPlayerRightClick");
        if (furnaceSmelt != null && furnaceTend != null)
        {
            harmony.Patch(furnaceTend,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BrickFurnaceTendPatch), "Postfix")));
            harmony.Patch(furnaceSmelt,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BrickFurnaceSmeltPatch), "Prefix")),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BrickFurnaceSmeltPatch), "Postfix")));
            hooked++;
            TcmLog.Info(api, "MET alloying practice hooked to industrialstory brick furnace (tended melt)");
        }
        else
        {
            TcmLog.Warn(api, "industrialstory present but BrickFurnace smeltItems/OnPlayerRightClick not found; alloying practice inactive");
        }

        TcmLog.Info(api, $"MET smelting practice hooked to industrialstory ({hooked} seam(s): pit blowpipe + molten taps + casting sand + brick furnace)");
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
        /// their shared config singleton). Helve-hammer path stays stock (no player).
        /// Doubles as the Axis-1 crumble seam (0.4.10): when the strike prefix rolled
        /// an Untrained over-strike for this split, the sheared bit crumbles to scale,
        /// skip the recovery entirely, no bit and no split-count credit.</summary>
        public static bool Prefix(IPlayer byPlayer, ItemStack workItemStack)
        {
            if (byPlayer != null && MetPatches.ConsumeCrumble(byPlayer)) return false;
            if (byPlayer == null || workItemStack == null) return true;
            double scale = MetDomain.RankLinear(MetLevel(byPlayer),
                Knob(MetDomain.BitRecoveryUntrained, 0.7),
                Knob(MetDomain.BitRecoveryGm, 1.3));
            if (scale == 1.0) return true;

            float perSplit = 1f / VoxelsPerBit();
            float seeded = workItemStack.TempAttributes.GetFloat("sp:splitCount")
                + (float)((scale - 1.0) * perSplit);
            workItemStack.TempAttributes.SetFloat("sp:splitCount", Math.Max(seeded, 0f));
            return true;
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

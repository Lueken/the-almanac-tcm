using System;
using System.Collections.Generic;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AlmanacTcm.Domains;

/// <summary>
/// MET pilot hooks (rank-bonus-design.md §162), all riding real vanilla seams:
/// anvil completion = smithing practice + the smith-stamp; anvil strike =
/// Untrained over-strike penalty; quench = practice + Axis-3 shatter scaling;
/// tool-mold fill = casting practice; firepit tick = Axis-2 fuel economy for
/// stamped workpieces. Every patch no-ops client-side; Smithing+/Toolsmith patch
/// some of the same methods — postfix-only discipline here, watch in the pack.
/// </summary>
public static class MetPatches
{
    /// <summary>Workpiece stamp: who is smithing this item. Doubles as the Maker's
    /// Mark seed at completion (one stamp, both jobs — RULED 2026-07-09).</summary>
    public const string SmithAttr = "almanactcm:smithuid";
    public const string SmithNameAttr = "almanactcm:smithname";

    /// <summary>The Maker's Mark on a finished piece (uid + display name).</summary>
    public const string MakerAttr = "almanactcm:maker";
    public const string MakerNameAttr = "almanactcm:makername";

    /// <summary>Smelt classification written at DoSmelt (no player there); read and
    /// converted to practice at first pour, where the pourer IS the attributable smith.</summary>
    public const string SmeltAttr = "almanactcm:smelt";
    public const string SmeltLoggedAttr = "almanactcm:smeltlogged";

    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.Instance;

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

    // ---------------------------------------------------------------- anvil

    [HarmonyPatch(typeof(BlockEntityAnvil), "OnUseOver",
        typeof(IPlayer), typeof(Vec3i), typeof(BlockSelection))]
    public static class AnvilStrikePatch
    {
        public static void Postfix(BlockEntityAnvil __instance, IPlayer byPlayer, Vec3i voxelPos)
        {
            if (__instance.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            if (__instance.SelectedRecipe == null || __instance.WorkItemStack == null) return;

            // The stamp rides the work item through reheats to completion.
            __instance.WorkItemStack.Attributes.SetString(SmithAttr, byPlayer.PlayerUID);
            __instance.WorkItemStack.Attributes.SetString(SmithNameAttr, byPlayer.PlayerName);

            // Axis 1 — over-strike: an Untrained smith's hammer sometimes bites one
            // voxel too deep, failing the exact-match completion and forcing a
            // reheat + top-up. Snaps to zero at Novice I.
            if (MetLevel(byPlayer) > 0) return;
            if (voxelPos == null) return;
            if (__instance.Api.World.Rand.NextDouble() >= Knob(MetDomain.OverStrikeChance, 0.15)) return;

            if (RemoveAdjacentMetalVoxel(__instance, voxelPos))
            {
                (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:overstrike"),
                    EnumChatType.Notification);
                TcmLog.Cat(__instance.Api, TcmLog.Hooks, $"{byPlayer.PlayerName} over-struck at {voxelPos}");
            }
        }

        private static bool RemoveAdjacentMetalVoxel(BlockEntityAnvil anvil, Vec3i pos)
        {
            var rand = anvil.Api.World.Rand;
            int[] order = { 0, 1, 2, 3 };
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            foreach (int i in order)
            {
                int x = pos.X + dx[i], z = pos.Z + dz[i];
                if (x < 0 || x > 15 || z < 0 || z > 15) continue;
                if (anvil.Voxels[x, pos.Y, z] == (byte)EnumVoxelMaterial.Metal)
                {
                    anvil.Voxels[x, pos.Y, z] = (byte)EnumVoxelMaterial.Empty;
                    AccessTools.Method(typeof(BlockEntityAnvil), "RegenMeshAndSelectionBoxes")
                        ?.Invoke(anvil, null);
                    anvil.MarkDirty();
                    anvil.Api.World.BlockAccessor.MarkBlockDirty(anvil.Pos);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Pending Maker's Mark for the synchronous CheckIfFinished→OnItemPickedUp
    /// window: the finished stack is a fresh recipe-output clone, so the workpiece
    /// stamp must be re-applied to it by ambient context.</summary>
    private static (string uid, string name)? pendingMaker;

    [HarmonyPatch(typeof(BlockEntityAnvil), nameof(BlockEntityAnvil.CheckIfFinished))]
    public static class AnvilFinishPatch
    {
        public static void Prefix(BlockEntityAnvil __instance, out int __state)
        {
            __state = __instance.SelectedRecipeId;
            string? uid = __instance.WorkItemStack?.Attributes.GetString(SmithAttr);
            string? name = __instance.WorkItemStack?.Attributes.GetString(SmithNameAttr);
            pendingMaker = uid == null ? null : (uid, name ?? "");

            if (__instance.Api?.Side == EnumAppSide.Server && __state != -1)
            {
                TcmLog.Cat(__instance.Api, TcmLog.Hooks,
                    $"anvil finish prefix: recipe={__state}, workitem={(__instance.WorkItemStack == null ? "null" : "present")}, stamp={(uid ?? "NONE")}");
            }
        }

        public static void Postfix(BlockEntityAnvil __instance, IPlayer byPlayer, int __state)
        {
            if (__instance.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            // Completion = a recipe was selected going in and vanilla reset it (output taken).
            if (__state == -1 || __instance.SelectedRecipeId != -1) return;

            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechSmithing,
                HashCode.Combine(__state, __instance.Pos));
        }

        public static void Finalizer()
        {
            pendingMaker = null;
        }
    }

    /// <summary>Maker's Mark v1: vanilla hands the EXACT finished stack to
    /// OnItemPickedUp inside CheckIfFinished's success branch; the ambient pending
    /// stamp becomes the permanent mark. Known gap: a full inventory spawns the item
    /// as an entity instead and skips this seam — that piece goes unmarked.</summary>
    [HarmonyPatch(typeof(ModSystemSubTongsDurability), nameof(ModSystemSubTongsDurability.OnItemPickedUp))]
    public static class MakersMarkPatch
    {
        public static void Postfix(Entity byEntity, ItemStack? stack)
        {
            if (pendingMaker == null || stack == null) return;
            var maker = pendingMaker.Value;
            stack.Attributes.SetString(MakerAttr, maker.uid);
            stack.Attributes.SetString(MakerNameAttr, maker.name);
            if (byEntity?.Api == null) return;
            TcmLog.Cat(byEntity.Api, TcmLog.Hooks,
                $"maker's mark applied to {stack.Collectible?.Code} for {maker.name}");

            // Post-processors (Smithing+ transpiler / Toolsmith sharpness init) can
            // rebuild the output stack and discard this instance (live-trial find:
            // mark applied, gone by inspect). Re-stamp whatever instance actually
            // survived in the smith's inventory shortly after the dust settles.
            int collectibleId = stack.Collectible?.Id ?? 0;
            IPlayer? smith = (byEntity as EntityPlayer)?.Player;
            if (collectibleId == 0 || smith == null) return;

            byEntity.Api.Event.RegisterCallback(_ =>
            {
                var inv = smith.InventoryManager;
                foreach (var slot in inv.GetHotbarInventory())
                {
                    ReStamp(byEntity.Api, slot, collectibleId, maker);
                }
                var backpack = inv.GetOwnInventory(GlobalConstants.backpackInvClassName);
                if (backpack != null)
                {
                    foreach (var slot in backpack) ReStamp(byEntity.Api, slot, collectibleId, maker);
                }
            }, 500);
        }

        private static void ReStamp(ICoreAPI api, ItemSlot slot, int collectibleId, (string uid, string name) maker)
        {
            ItemStack? s = slot?.Itemstack;
            if (s?.Collectible?.Id != collectibleId) return;
            if (s.Attributes.HasAttribute(MakerAttr)) return;
            s.Attributes.SetString(MakerAttr, maker.uid);
            s.Attributes.SetString(MakerNameAttr, maker.name);
            slot!.MarkDirty();
            TcmLog.Cat(api, TcmLog.Hooks, $"maker's mark re-stamped on surviving {s.Collectible.Code}");
        }
    }

    /// <summary>The mark on the tooltip, both sides (client patches too).</summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo))]
    public static class MarkTooltipPatch
    {
        public static void Postfix(ItemSlot inSlot, System.Text.StringBuilder dsc)
        {
            string? maker = inSlot?.Itemstack?.Attributes.GetString(MakerNameAttr);
            if (!string.IsNullOrEmpty(maker))
            {
                dsc.AppendLine(Lang.Get("almanactcm:made-by", maker));
            }
        }
    }

    // -------------------------------------------------------------- smelting

    /// <summary>Classifies the completed smelt on the container (no player exists at
    /// DoSmelt); the pour patch converts it to attributed practice.</summary>
    [HarmonyPatch(typeof(BlockSmeltingContainer), nameof(BlockSmeltingContainer.DoSmelt))]
    public static class SmeltCompletePatch
    {
        public static void Prefix(BlockSmeltingContainer __instance, IWorldAccessor world,
            ISlotProvider cookingSlotsProvider, out bool __state)
        {
            ItemStack[] stacks = __instance.GetIngredients(world, cookingSlotsProvider);
            __state = __instance.GetMatchingAlloy(world, stacks) != null;
        }

        public static void Postfix(IWorldAccessor world, ItemSlot outputSlot, bool __state)
        {
            if (world.Side != EnumAppSide.Server) return;
            ItemStack? smelted = outputSlot?.Itemstack;
            if (smelted?.Block is not BlockSmeltedContainer) return;
            smelted.Attributes.SetString(SmeltAttr, __state ? "alloy" : "single");
        }
    }

    // ---------------------------------------------------------------- quench

    /// <summary>Ambient quenching player, set for the duration of IsGettingCooled so
    /// the parameterless GetShatterChance seam can know whose hands hold the tongs.</summary>
    private static IPlayer? quenchingPlayer;

    [HarmonyPatch(typeof(CollectibleBehaviorQuenchable), "IsGettingCooled")]
    public static class QuenchContextPatch
    {
        public static void Prefix(IWorldAccessor world, ItemSlot slot, Vec3d pos)
        {
            if (world.Side != EnumAppSide.Server) return;
            quenchingPlayer = (slot.Inventory as InventoryBasePlayer)?.Player;

            if (quenchingPlayer != null && slot.Itemstack != null)
            {
                Core?.Ledger?.Log(quenchingPlayer, MetDomain.Code, MetDomain.TechQuenching,
                    HashCode.Combine(slot.Itemstack.Collectible.Id,
                        (int)pos.X / 4, (int)pos.Y / 4, (int)pos.Z / 4));
            }
        }

        public static void Finalizer()
        {
            quenchingPlayer = null;
        }
    }

    [HarmonyPatch(typeof(CollectibleBehaviorQuenchable), nameof(CollectibleBehaviorQuenchable.GetShatterChance))]
    public static class ShatterChancePatch
    {
        public static void Postfix(ref float __result)
        {
            if (quenchingPlayer == null) return;
            double factor = MetDomain.ShatterFactor(MetLevel(quenchingPlayer),
                Knob(MetDomain.ShatterFactorUntrained, 1.5),
                Knob(MetDomain.ShatterFactorGm, 0.4));
            __result = (float)(__result * factor);
        }
    }

    // --------------------------------------------------------------- casting

    private static IPlayer? pouringPlayer;

    [HarmonyPatch(typeof(BlockSmeltedContainer), nameof(BlockSmeltedContainer.OnHeldInteractStep))]
    public static class PourContextPatch
    {
        public static bool Prefix(ItemSlot slot, EntityAgent byEntity, ref bool __result)
        {
            IPlayer? player = (byEntity as EntityPlayer)?.Player;
            ItemStack? crucible = slot?.Itemstack;
            if (player == null || crucible == null) return true;

            // MATERIAL GATE (§162 Axis 5), BOTH SIDES: casting is working the metal, so
            // block pouring a gated molten metal. Client-side too, so the pour isn't
            // mispredicted. Blocked pours log no practice (the check runs first).
            ItemStack? molten = (crucible.Collectible as BlockSmeltedContainer)?.GetContents(byEntity.World, crucible).Key;
            if (MetMaterialGate.Blocks(byEntity.Api, player, molten))
            {
                __result = false;
                return false;
            }

            // Server-only: smelting practice, attributed to the pourer, on FIRST pour of a
            // freshly smelted crucible (attr guard). pouringPlayer feeds ToolMoldFillPatch.
            if (byEntity.World.Side != EnumAppSide.Server) return true;
            pouringPlayer = player;
            string? kind = crucible.Attributes.GetString(SmeltAttr);
            if (kind == null || crucible.Attributes.GetBool(SmeltLoggedAttr)) return true;

            crucible.Attributes.SetBool(SmeltLoggedAttr, true);
            Core?.Ledger?.Log(pouringPlayer, MetDomain.Code,
                kind == "alloy" ? MetDomain.TechAlloying : MetDomain.TechSmelting,
                HashCode.Combine(crucible.Id, byEntity.World.ElapsedMilliseconds / 1000));
            return true;
        }

        public static void Finalizer()
        {
            pouringPlayer = null;
        }
    }

    /// <summary>Casters recorded per mold position at fill; the mark lands on the
    /// cast head when the hardened contents are taken (RULED: cast heads carry
    /// their maker like forged ones). Session-scoped memory — a restart between
    /// pour and take loses the attribution, accepted for v1.</summary>
    private static readonly Dictionary<string, (string uid, string name)> moldCasters = new();

    [HarmonyPatch(typeof(BlockEntityToolMold), nameof(BlockEntityToolMold.ReceiveLiquidMetal))]
    public static class ToolMoldFillPatch
    {
        public static void Prefix(BlockEntityToolMold __instance, out bool __state)
        {
            __state = __instance.IsFull;
        }

        public static void Postfix(BlockEntityToolMold __instance, bool __state)
        {
            if (__instance.Api?.Side != EnumAppSide.Server || pouringPlayer == null) return;
            // Practice lands when the pour COMPLETES the cast, once per mold fill.
            if (__state || !__instance.IsFull) return;

            if (moldCasters.Count > 128) moldCasters.Clear();
            moldCasters[__instance.Pos.ToString()] = (pouringPlayer.PlayerUID, pouringPlayer.PlayerName);

            Core?.Ledger?.Log(pouringPlayer, MetDomain.Code, MetDomain.TechCasting,
                HashCode.Combine(__instance.Pos));
        }
    }

    [HarmonyPatch(typeof(BlockEntityToolMold), nameof(BlockEntityToolMold.GetStateAwareMoldedStacks))]
    public static class CastMarkPatch
    {
        public static void Postfix(BlockEntityToolMold __instance, ItemStack[]? __result)
        {
            if (__result == null || __instance.Api?.Side != EnumAppSide.Server) return;
            if (!moldCasters.TryGetValue(__instance.Pos.ToString(), out var caster)) return;

            foreach (ItemStack stack in __result)
            {
                if (stack == null || stack.Attributes.HasAttribute(MakerAttr)) continue;
                stack.Attributes.SetString(MakerAttr, caster.uid);
                stack.Attributes.SetString(MakerNameAttr, caster.name);
            }
        }
    }

    // -------------------------------------------------------------- assembly

    /// <summary>MARK TRANSFER, not practice: OnCreatedByCrafting fires on grid
    /// PREVIEWS and inside Toolsmith's held assembly (dummy inventory, no player),
    /// so it's the wrong seam for XP but the perfect one for provenance — every
    /// path that builds a tool from a head passes through here with the input
    /// slots visible. The head's Maker's Mark rides onto the finished tool
    /// (RULED 2026-07-13: forged or cast, the head's maker marks the tool).</summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.OnCreatedByCrafting))]
    public static class MarkTransferPatch
    {
        public static void Postfix(ItemSlot[] allInputSlots, ItemSlot outputSlot)
        {
            ItemStack? output = outputSlot?.Itemstack;
            if (output?.Collectible?.Tool == null || allInputSlots == null) return;
            if (output.Attributes.HasAttribute(MakerAttr)) return;

            foreach (ItemSlot input in allInputSlots)
            {
                string? maker = input?.Itemstack?.Attributes.GetString(MakerAttr);
                if (maker == null) continue;
                output.Attributes.SetString(MakerAttr, maker);
                output.Attributes.SetString(MakerNameAttr,
                    input!.Itemstack!.Attributes.GetString(MakerNameAttr) ?? "");
                return;
            }
        }
    }

    /// <summary>PRACTICE for grid and MTC assembly: GridRecipe.ConsumeInput runs
    /// exactly once per real take (never on previews) with the player as a
    /// parameter — the same seam Progression Framework trusts for craft XP.
    /// Toolsmith's held/workbench paths use ConsumeCraftingIngredients instead,
    /// so no double-count; they keep their own seams.</summary>
    [HarmonyPatch(typeof(GridRecipe), nameof(GridRecipe.ConsumeInput))]
    public static class GridTakePatch
    {
        public static void Postfix(GridRecipe __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || byPlayer == null) return;
            if (byPlayer.Entity?.World?.Side != EnumAppSide.Server) return;

            ItemStack? output = __instance?.Output?.ResolvedItemStack;
            if (output?.Collectible?.Tool == null) return;
            if (output.Collectible.ToolTier < 2) return;

            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechAssembly,
                HashCode.Combine(output.Collectible.Id, byPlayer.Entity.World.ElapsedMilliseconds / 1000));
        }
    }

    // --------------------------------------------------------------- firepit

    [HarmonyPatch(typeof(BlockEntityFirepit), "OnBurnTick")]
    public static class FuelEconomyPatch
    {
        public static void Postfix(BlockEntityFirepit __instance, float dt)
        {
            if (__instance.Api?.Side != EnumAppSide.Server) return;
            if (!__instance.IsBurning) return;

            string? smithUid = __instance.inputSlot?.Itemstack?.Attributes.GetString(SmithAttr);
            if (smithUid == null) return;

            IPlayer? smith = __instance.Api.World.PlayerByUid(smithUid);
            if (smith == null) return;

            double economy = MetDomain.FuelEconomy(MetLevel(smith),
                Knob(MetDomain.FuelEconomyUntrained, -0.10),
                Knob(MetDomain.FuelEconomyApprentice, 0.03),
                Knob(MetDomain.FuelEconomyGm, 0.15));
            if (economy == 0) return;

            // Refund (or extra-consume, Untrained) a fraction of this tick's burn:
            // the master's coal simply lasts longer under HIS workpiece.
            __instance.fuelBurnTime += dt * (float)economy;
        }
    }
}

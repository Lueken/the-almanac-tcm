using System;
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

    [HarmonyPatch(typeof(BlockEntityAnvil), nameof(BlockEntityAnvil.CheckIfFinished))]
    public static class AnvilFinishPatch
    {
        public static void Prefix(BlockEntityAnvil __instance, out int __state)
        {
            __state = __instance.SelectedRecipeId;
        }

        public static void Postfix(BlockEntityAnvil __instance, IPlayer byPlayer, int __state)
        {
            if (__instance.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            // Completion = a recipe was selected going in and vanilla reset it (output taken).
            if (__state == -1 || __instance.SelectedRecipeId != -1) return;

            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechSmithing,
                HashCode.Combine(__state, __instance.Pos));
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
        public static void Prefix(EntityAgent byEntity)
        {
            if (byEntity?.World?.Side != EnumAppSide.Server) return;
            pouringPlayer = (byEntity as EntityPlayer)?.Player;
        }

        public static void Finalizer()
        {
            pouringPlayer = null;
        }
    }

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

            Core?.Ledger?.Log(pouringPlayer, MetDomain.Code, MetDomain.TechCasting,
                HashCode.Combine(__instance.Pos));
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

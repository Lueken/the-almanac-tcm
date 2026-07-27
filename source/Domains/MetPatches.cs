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

    /// <summary>Maker's MET tier frozen at creation (2=Journeyman … 4=Grandmaster). Drives
    /// the tiered provenance line; the tool stays what its maker was even if they later
    /// rank up, down, or log off (§162 Axis 6). PERMANENT — never stripped.</summary>
    public const string MakerTierAttr = "almanactcm:makertier";

    /// <summary>Smithing+'s own per-tool durability-quality attribute. We stamp it with the
    /// maker-quality multiplier at creation and Smithing+'s GetMaxDurability postfix applies
    /// it (RepairableToolDurabilityMultiplier defaults to 1.0), so we reuse its math instead
    /// of a parallel postfix — no double-count, and forge + cast are covered uniformly.
    /// Smithing+ is a hard dep. Separate from the permanent MakerTierAttr, so the repair-gate
    /// (stage 2) can restamp/strip the buff while the provenance line stays intact.</summary>
    public const string SmithingQualityAttr = "sp:smithingQuality";

    /// <summary>Smelt classification written at DoSmelt (no player there); read and
    /// converted to practice at first pour, where the pourer IS the attributable smith.</summary>
    public const string SmeltAttr = "almanactcm:smelt";
    public const string SmeltLoggedAttr = "almanactcm:smeltlogged";

    /// <summary>Knowledge key set the first time a player finishes an anvil work of their
    /// own beginning (their stamp on the workpiece). The smithing guide's capstone reveal.</summary>
    public const string FirstWorkKey = "almanactcm:met-first-work";

    private static AlmanacTcmModSystem? Core => AlmanacTcmModSystem.ServerInstance;

    private static int MetLevel(IPlayer player)
    {
        var domainSet = Core?.Server?.GetDomainSet(player);
        return domainSet?.FindDomain(MetDomain.Code)?.Level ?? 0;
    }

    /// <summary>Maker's MET tier by uid at mark time. Returns -1 below Journeyman (tier 2):
    /// Novice/Apprentice work is unmarked, so a mark always means something. Offline or
    /// unknown smith is also -1 (nothing to freeze).</summary>
    private static int MakerTierOf(ICoreAPI? api, string? uid)
    {
        if (api == null || uid == null) return -1;
        IPlayer? p = api.World.PlayerByUid(uid);
        if (p == null) return -1;
        int tier = Leveling.Domain.TierOf(MetLevel(p));
        return tier >= 2 ? tier : -1;
    }

    /// <summary>Provenance lang key for a maker tier: Smithed (Journeyman), Master-forged
    /// (Master), Masterwork (Grandmaster).</summary>
    private static string MakerKey(int tier) => tier switch
    {
        >= 4 => "almanactcm:masterwork-by",
        3 => "almanactcm:master-forged-by",
        _ => "almanactcm:smithed-by",
    };

    /// <summary>Durability multiplier for the maker-quality tier (§162 Axis 6): a modest,
    /// tier-scaling bump to the HEAD's pool. Below Journeyman (or a stripped buff) = ×1.
    /// Handle and binding stay stock — they take their own quality from WOO / TAI-HUN later,
    /// stacking per-part into the pinnacle tool.</summary>
    private static double QualityFactor(int qualityTier) => qualityTier switch
    {
        >= 4 => 1.15,
        3 => 1.10,
        2 => 1.05,
        _ => 1.0,
    };

    /// <summary>Top the head durability off to its NEW max after the quality buff raises it.
    /// Toolsmith reads head current-durability from the vanilla "durability" attribute; a fresh
    /// forge sets it to the BASE max, so once our sp:smithingQuality lifts GetMaxDurability the
    /// tool would otherwise be born partly worn (e.g. 5000/5750). Call right after stamping quality.</summary>
    private static void RefreshHeadDurability(ItemStack? stack, ICoreAPI? api)
    {
        if (stack?.Collectible == null) return;
        // Best-effort and never-throw: GetMaxDurability runs other mods' postfixes (Toolsmith's
        // can NRE on a bare head), and this call sits in the maker's-mark flow BEFORE the
        // re-stamp registers — an escaping exception here would abort the whole mark. So it is
        // fully guarded; a failure just skips the top-up, it never breaks the mark.
        try
        {
            int max = stack.Collectible.GetMaxDurability(stack);
            if (max > 0) stack.Attributes.SetInt("durability", max);
        }
        catch (System.Exception e)
        {
            if (api != null) TcmLog.Cat(api, TcmLog.Hooks, $"head durability top-up skipped ({stack.Collectible.Code}): {e.Message}");
        }
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

            // Axis 1 rare ruin: a very low chance the clumsy piece cracks and is lost
            // outright (the harsher, rarer sibling of over-striking). Untrained only.
            if (__instance.Api.World.Rand.NextDouble() < Knob(MetDomain.RuinChance, 0.008))
            {
                RuinWorkpiece(__instance);
                (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:ruin"),
                    EnumChatType.Notification);
                TcmLog.Cat(__instance.Api, TcmLog.Hooks, $"{byPlayer.PlayerName} ruined a workpiece (Untrained)");
                return;
            }

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

        /// <summary>Spoil the piece outright: drop the work item and clear the anvil's
        /// voxels so nothing is recoverable. Reflection reaches the private backing field
        /// (only a public getter exists); the same RegenMesh call the over-strike uses
        /// refreshes the now-empty anvil.</summary>
        private static void RuinWorkpiece(BlockEntityAnvil anvil)
        {
            AccessTools.Field(typeof(BlockEntityAnvil), "workItemStack")?.SetValue(anvil, null);
            System.Array.Clear(anvil.Voxels, 0, anvil.Voxels.Length);
            AccessTools.Method(typeof(BlockEntityAnvil), "RegenMeshAndSelectionBoxes")?.Invoke(anvil, null);
            anvil.MarkDirty(true);
            anvil.Api.World.BlockAccessor.MarkBlockDirty(anvil.Pos);
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
    private static (string uid, string name, int tier)? pendingMaker;

    /// <summary>Collectible id of the recipe output about to complete, captured in the prefix so
    /// the completion postfix can find that exact head in inventory or on the ground.</summary>
    private static int pendingOutputId;

    [HarmonyPatch(typeof(BlockEntityAnvil), nameof(BlockEntityAnvil.CheckIfFinished))]
    public static class AnvilFinishPatch
    {
        public static void Prefix(BlockEntityAnvil __instance, out int __state)
        {
            __state = __instance.SelectedRecipeId;
            string? uid = __instance.WorkItemStack?.Attributes.GetString(SmithAttr);
            string? name = __instance.WorkItemStack?.Attributes.GetString(SmithNameAttr);
            pendingMaker = uid == null ? null : (uid, name ?? "", MakerTierOf(__instance.Api, uid));
            pendingOutputId = __instance.SelectedRecipe?.Output?.ResolvedItemstack?.Collectible?.Id ?? 0;

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

            // First finished work of the player's OWN beginning: the workpiece stamp
            // (written at their first strike) matches the finisher. Bought, gifted, or
            // someone-else-started pieces never match, which is the whole point — this
            // is the earned-knowledge capstone the smithing guide reveals on.
            if (pendingMaker is { } pm && pm.uid == byPlayer.PlayerUID)
            {
                var domainSet = Core?.Server?.GetDomainSet(byPlayer);
                if (domainSet != null && !domainSet.Knowledge.ContainsKey(FirstWorkKey))
                    Core?.Server?.SetKnowledge(byPlayer, FirstWorkKey, 1);
            }

            // Mark the finished head HERE, at completion — robust to how vanilla delivers it.
            // If the inventory has room the head is given (and OnItemPickedUp marks it inside the
            // pending window); if it is FULL the head is dropped as an entity and that seam is
            // skipped entirely, so the head is only picked up later with no pending (the live bug).
            // Scanning both inventory and nearby drops at completion catches every path.
            if (pendingMaker is { } m && m.tier >= 2 && pendingOutputId != 0)
                StampCompletedOutput(__instance, byPlayer, m, pendingOutputId);
        }

        public static void Finalizer()
        {
            pendingMaker = null;
        }
    }

    /// <summary>Find the just-completed head (by collectible id) in the smith's inventory OR
    /// among items dropped near the anvil, and stamp the maker's mark on it. Runs shortly after
    /// completion so post-processors (Toolsmith/Smithing+ rebuilds) have settled.</summary>
    private static void StampCompletedOutput(BlockEntityAnvil anvil, IPlayer byPlayer,
        (string uid, string name, int tier) maker, int collId)
    {
        ICoreAPI api = anvil.Api;
        api.Event.RegisterCallback(_ =>
        {
            int marked = 0;
            foreach (var slot in byPlayer.InventoryManager.GetHotbarInventory())
                if (StampIfMatch(api, slot?.Itemstack, collId, maker)) marked++;
            var backpack = byPlayer.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpack != null)
                foreach (var slot in backpack)
                    if (StampIfMatch(api, slot?.Itemstack, collId, maker)) marked++;

            foreach (var e in api.World.GetEntitiesAround(anvil.Pos.ToVec3d().Add(0.5, 0.5, 0.5), 4f, 4f,
                         ent => ent is EntityItem ei && ei.Itemstack?.Collectible?.Id == collId))
                if (StampIfMatch(api, (e as EntityItem)?.Itemstack, collId, maker)) marked++;

            if (marked > 0) TcmLog.Cat(api, TcmLog.Hooks, $"maker's mark stamped {marked}x at completion (collectible {collId})");
        }, 150);
    }

    private static bool StampIfMatch(ICoreAPI api, ItemStack? s, int collId, (string uid, string name, int tier) maker)
    {
        if (s?.Collectible?.Id != collId || s.Attributes.HasAttribute(MakerAttr)) return false;
        s.Attributes.SetString(MakerAttr, maker.uid);
        s.Attributes.SetString(MakerNameAttr, maker.name);
        s.Attributes.SetInt(MakerTierAttr, maker.tier);
        s.Attributes.SetFloat(SmithingQualityAttr, (float)QualityFactor(maker.tier));
        RefreshHeadDurability(s, api);
        MetSignature.Assign(s, maker.tier);
        return true;
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
            // The immediate path: when a completed head is handed straight to inventory, vanilla
            // calls this inside CheckIfFinished with the pending maker still set. The full-inventory
            // (dropped-entity) and delayed-pickup cases are covered by StampCompletedOutput instead.
            if (pendingMaker == null || stack == null) return;
            var maker = pendingMaker.Value;
            if (maker.tier < 2) return;   // Journeyman+ only: lesser work carries no mark
            stack.Attributes.SetString(MakerAttr, maker.uid);
            stack.Attributes.SetString(MakerNameAttr, maker.name);
            stack.Attributes.SetInt(MakerTierAttr, maker.tier);
            stack.Attributes.SetFloat(SmithingQualityAttr, (float)QualityFactor(maker.tier));
            RefreshHeadDurability(stack, byEntity?.Api);
            // GM signature (Axis 6 stage 2): a directly-forged weapon/tool is classifiable
            // here; a bare Toolsmith head is not and takes its edge at assembly instead.
            MetSignature.Assign(stack, maker.tier);
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

        private static void ReStamp(ICoreAPI api, ItemSlot slot, int collectibleId, (string uid, string name, int tier) maker)
        {
            ItemStack? s = slot?.Itemstack;
            if (s?.Collectible?.Id != collectibleId) return;
            if (s.Attributes.HasAttribute(MakerAttr)) return;
            s.Attributes.SetString(MakerAttr, maker.uid);
            s.Attributes.SetString(MakerNameAttr, maker.name);
            s.Attributes.SetInt(MakerTierAttr, maker.tier);
            s.Attributes.SetFloat(SmithingQualityAttr, (float)QualityFactor(maker.tier));
            RefreshHeadDurability(s, api);
            MetSignature.Assign(s, maker.tier);
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
            var attrs = inSlot?.Itemstack?.Attributes;
            string? maker = attrs?.GetString(MakerNameAttr);
            if (string.IsNullOrEmpty(maker)) return;
            // Tiered provenance from the frozen maker tier; legacy tools (no tier) fall
            // back to the flat line.
            int tier = attrs!.GetInt(MakerTierAttr, -1);
            dsc.AppendLine(Lang.Get(tier >= 2 ? MakerKey(tier) : "almanactcm:made-by", maker));

            // The GM signature (Axis 6 stage 2), a quiet line under the provenance.
            if (MetSignature.IsHoned(inSlot!.Itemstack))
                dsc.AppendLine(Lang.Get("almanactcm:honed-mark"));
            else if (MetSignature.IsDurable(inSlot.Itemstack))
                dsc.AppendLine(Lang.Get("almanactcm:durable-mark"));
        }
    }

    // Maker-quality durability is delegated to Smithing+ (hard dep): we stamp its
    // sp:smithingQuality attribute at creation and its own GetMaxDurability postfix
    // applies the bump — no parallel postfix here, so nothing double-counts.

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
    private static readonly Dictionary<string, (string uid, string name, int tier)> moldCasters = new();

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
            moldCasters[__instance.Pos.ToString()] =
                (pouringPlayer.PlayerUID, pouringPlayer.PlayerName, MakerTierOf(__instance.Api, pouringPlayer.PlayerUID));

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
            if (caster.tier < 2) return;   // Journeyman+ only, same as forged work

            foreach (ItemStack stack in __result)
            {
                if (stack == null || stack.Attributes.HasAttribute(MakerAttr)) continue;
                stack.Attributes.SetString(MakerAttr, caster.uid);
                stack.Attributes.SetString(MakerNameAttr, caster.name);
                stack.Attributes.SetInt(MakerTierAttr, caster.tier);
                stack.Attributes.SetFloat(SmithingQualityAttr, (float)QualityFactor(caster.tier));
                RefreshHeadDurability(stack, __instance.Api);
                MetSignature.Assign(stack, caster.tier);
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
                output.Attributes.SetInt(MakerTierAttr,
                    input.Itemstack.Attributes.GetInt(MakerTierAttr, -1));
                // Quality carries the head's CURRENT buff state (a stripped head passes a
                // stripped tool), keeping the head-for-life model intact through assembly.
                output.Attributes.SetFloat(SmithingQualityAttr,
                    input.Itemstack.Attributes.GetFloat(SmithingQualityAttr, 1f));
                RefreshHeadDurability(output, outputSlot?.Inventory?.Api);
                // GM signature: keep an already-marked head's edge; otherwise the bare head
                // finally becomes a classifiable tool here, so assign by the finished type.
                if (!MetSignature.CopySignature(input.Itemstack, output))
                    MetSignature.Assign(output, input.Itemstack.Attributes.GetInt(MakerTierAttr, -1));
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

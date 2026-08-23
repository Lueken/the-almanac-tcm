using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
/// Untrained clumsiness matched to the tool mode (split = the sheared bit can
/// crumble, move = a small chance to nudge one extra voxel, any mishap opens a
/// short focus grace, reworked 0.4.10, ruin roll removed); quench = practice +
/// Axis-3 shatter scaling; tool-mold fill = casting practice; firepit tick =
/// Axis-2 fuel economy for stamped workpieces. Every patch no-ops client-side;
/// Smithing+/Toolsmith patch some of the same methods, so postfix-only discipline
/// here, watch in the pack.
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

    /// <summary>Maker's MET LEVEL frozen at creation (9=Journeyman I … 17=Grandmaster). Drives
    /// the tiered provenance line; the tool stays what its maker was even if they later
    /// rank up, down, or log off (§162 Axis 6). PERMANENT: never stripped.
    ///
    /// LEVEL, not tier, since 2026-08-12. MET was the mod's last store of a collapsed tier:
    /// every other persisted rank (PlayerDomain, the wire, FAR's grownTier) carries a level,
    /// and a tier throws away which of the four sub-levels the smith actually held. Read it
    /// through <see cref="MarkLevel(ItemStack?)"/>, never raw, so the legacy fallback stays
    /// in one place.</summary>
    public const string MakerLevelAttr = "almanactcm:makerlevel";

    /// <summary>The superseded tier-valued key (2/3/4). READ ONLY, and only by
    /// <see cref="MarkLevel(ItemStack?)"/>; nothing writes it any more. No other mod reads it
    /// either (verified 2026-08-12 against Smithing+ 1.9.0-rc.1 and Toolsmith 1.2.17/1.2.18:
    /// zero hits in any encoding), so retiring the key is ours alone to do.
    ///
    /// KEEP UNTIL 0.5.0 (RULED 2026-08-12). The Quire's world was wiped, so the SERVER holds
    /// no tier-stamped tools. That is not the whole population: closed-beta testers run
    /// SINGLEPLAYER worlds that were never wiped and can still be holding tools that carry
    /// this key and its quality buff. They are the live consumer, and the reason the "no
    /// server data, therefore no data" argument does not hold.
    ///
    /// REMOVAL PLAN. Announce it in the patch notes of the releases leading up to 0.5.0, then
    /// delete this const and the fallback branch in MarkLevel as part of 0.5.0 itself.
    /// Deleting it early does not crash anything: MarkLevel returns -1, the tool drops to the
    /// flat "made-by" line, and it keeps its durability because that rides a separate float.
    /// But a Grandmaster piece SILENTLY loses its masterwork line and its wear skip, and a
    /// silent downgrade on someone's best tool is exactly what the advance warning is for.</summary>
    private const string LegacyMakerTierAttr = "almanactcm:makertier";

    /// <summary>Smithing+'s own per-tool durability-quality attribute. We stamp it with the
    /// maker-quality multiplier at creation and Smithing+'s GetMaxDurability postfix applies
    /// it (RepairableToolDurabilityMultiplier defaults to 1.0), so we reuse its math instead
    /// of a parallel postfix — no double-count, and forge + cast are covered uniformly.
    /// Smithing+ is a hard dep. Separate from the permanent MakerLevelAttr, so the repair-gate
    /// (stage 2) can restamp/strip the buff while the provenance line stays intact.</summary>
    public const string SmithingQualityAttr = "sp:smithingQuality";

    /// <summary>Whether toolsmith is loaded (set at patch time). With it absent there is no
    /// bench, so the fitting rule below never applies and quality carries as it always did.</summary>
    public static bool ToolsmithLoaded;

    /// <summary>True only inside BlockEntityWorkbench.AttemptToCraft (set by the workbench
    /// patch, cleared in its finalizer). MarkTransferPatch reads it to decide whether the
    /// head's quality WAKES on the assembled tool: the bench-fitting rule (RULED 2026-07-31).
    /// ThreadStatic on principle; the server tick is single-threaded today.</summary>
    [System.ThreadStatic] public static bool BenchAssemblyContext;

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

    /// <summary>Maker's MET level by uid at mark time. Returns -1 below Journeyman I:
    /// Novice/Apprentice work is unmarked, so a mark always means something. Offline or
    /// unknown smith is also -1 (nothing to freeze).</summary>
    private static int MakerLevelOf(ICoreAPI? api, string? uid)
    {
        if (api == null || uid == null) return -1;
        IPlayer? p = api.World.PlayerByUid(uid);
        if (p == null) return -1;
        int level = MetLevel(p);
        return level >= Rank.Journeyman ? level : -1;
    }

    /// <summary>THE read for a stack's frozen maker level. -1 means unmarked. Every consumer
    /// goes through here (MET's own tooltip and signature patches, and ToolPartMarks' lineage
    /// line) so the legacy conversion below has exactly one home.</summary>
    public static int MarkLevel(ItemStack? stack)
    {
        var attrs = stack?.Attributes;
        if (attrs == null) return -1;
        if (attrs.HasAttribute(MakerLevelAttr)) return attrs.GetInt(MakerLevelAttr, -1);
        if (!attrs.HasAttribute(LegacyMakerTierAttr)) return -1;

        // A pre-2026-08-12 stamp holds a TIER. Convert to the band-ENTRY level: the lowest
        // level consistent with what was recorded. Deliberately conservative: the old stamp
        // genuinely does not say whether that Journeyman was I or IV, so we must not invent a
        // higher one. Tier is fully recoverable from the result, so nothing visible regresses.
        //
        // This branch is KEPT UNTIL 0.5.0 for singleplayer testers whose worlds were never
        // wiped. See LegacyMakerTierAttr above for the ruling and the removal plan.
        return attrs.GetInt(LegacyMakerTierAttr, -1) switch
        {
            >= 4 => Rank.Grandmaster,
            3 => Rank.Master,
            2 => Rank.Journeyman,
            _ => -1,
        };
    }

    /// <summary>Provenance lang key for a maker level: Smithed (Journeyman), Master-forged
    /// (Master), Masterwork (Grandmaster).</summary>
    // met-masterwork-by, NOT masterwork-by: that key belongs to POT, whose "vessel that
    // keeps what it holds" read absurdly on a Grandmaster pickaxe (caught in play 2026-08-01).
    // internal, not private, since 2026-08-12: ToolPartMarks reads a head's mark for the
    // assembled-tool lineage line and had its own copy of this mapping.
    internal static string MakerKey(int level) => level switch
    {
        >= Rank.Grandmaster => "almanactcm:met-masterwork-by",
        >= Rank.Master => "almanactcm:master-forged-by",
        _ => "almanactcm:smithed-by",
    };

    /// <summary>The awake-quality percent clause of the maker line, or empty. Shared with
    /// ToolPartMarks so a folded lineage line (RULED 2026-08-18) keeps the figure the
    /// numbers ruling promised.</summary>
    internal static string QualityClause(ItemStack? stack)
    {
        float quality = stack?.Attributes?.GetFloat(SmithingQualityAttr, 0f) ?? 0f;
        if (quality <= 1f) return "";
        return Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-durability",
            (int)System.Math.Round((quality - 1f) * 100f)));
    }

    /// <summary>Durability multiplier for the maker's rank (§162 Axis 6): a modest,
    /// band-scaling bump to the HEAD's pool. Below Journeyman (or a stripped buff) = ×1.
    /// Handle and binding stay stock — they take their own quality from WOO / TAI-HUN later,
    /// stacking per-part into the pinnacle tool.
    ///
    /// Still bands, not per-level: the factor is a design ruling about what a Journeyman's
    /// work is worth, and four distinct multipliers inside one band would be noise a player
    /// cannot read. The STORAGE moved to level; the curve did not.</summary>
    // The banded maker-quality steps, named so the Callings book quotes the same values
    // the seam applies (DomainFigures.MetFigures — the 2026-08-22 figures ruling).
    public const double QualityJourneyman = 1.05, QualityMaster = 1.10, QualityGrandmaster = 1.15;

    private static double QualityFactor(int makerLevel) => makerLevel switch
    {
        >= Rank.Grandmaster => QualityGrandmaster,
        >= Rank.Master => QualityMaster,
        >= Rank.Journeyman => QualityJourneyman,
        _ => 1.0,
    };

    /// <summary>Apply the full Maker's Mark to a freshly-made stack: provenance, frozen level,
    /// the Smithing+ quality stamp, the durability top-up, and the GM signature. One body, four
    /// callers (forge-immediate, forge-rescan, forge-restamp, cast). They were four copies of
    /// this until 2026-08-12, which is what let the level conversion have four places to go
    /// wrong.</summary>
    private static void ApplyMark(ItemStack stack, (string uid, string name, int level) maker, ICoreAPI? api)
    {
        stack.Attributes.SetString(MakerAttr, maker.uid);
        stack.Attributes.SetString(MakerNameAttr, maker.name);
        stack.Attributes.SetInt(MakerLevelAttr, maker.level);
        stack.Attributes.SetFloat(SmithingQualityAttr, (float)QualityFactor(maker.level));
        RefreshHeadDurability(stack, api);
        MetSignature.Assign(stack, maker.level);
    }

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

    /// <summary>MET's Bonus-knob accessor (public since 2026-08-22: DomainFigures quotes
    /// the same live values this file's seams run on).</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = Core?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(MetDomain.Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }

    // ---------------------------------------------------------------- anvil

    /// <summary>Focus grace per player (uid → ElapsedMilliseconds deadline): after any
    /// Untrained mishap the smith "resets their stance" and nothing further can roll
    /// until the window passes. Kills the old death spiral where one penalty bred
    /// corrective strikes that each rolled again.</summary>
    private static readonly Dictionary<string, long> focusUntil = new();

    internal static bool InFocusGrace(ICoreAPI api, string uid)
        => focusUntil.TryGetValue(uid, out long until) && api.World.ElapsedMilliseconds < until;

    internal static void StartFocusGrace(ICoreAPI api, string uid)
        => focusUntil[uid] = api.World.ElapsedMilliseconds
            + (long)(Knob(MetDomain.FocusCooldownSeconds, 5) * 1000);

    /// <summary>One-shot flag for the strike in flight: the prefix rolled a split-bit
    /// crumble for this player. Consumed by BitRecoveryPatch (Smithing+'s recovery
    /// seam), cleared by the finalizer if the seam never ran this strike.</summary>
    internal static string? PendingCrumbleUid;

    /// <summary>The sheared bit crumbles to scale: consume the pending flag, message,
    /// open the focus grace. Returns true when Smithing+'s recovery should be skipped
    /// entirely for this split (no bit, no split-count credit).</summary>
    internal static bool ConsumeCrumble(IPlayer byPlayer)
    {
        if (PendingCrumbleUid == null || PendingCrumbleUid != byPlayer.PlayerUID) return false;
        PendingCrumbleUid = null;
        ICoreAPI? api = byPlayer.Entity?.Api;
        if (api != null) StartFocusGrace(api, byPlayer.PlayerUID);
        (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
            Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:overstrike-crumble"),
            EnumChatType.Notification);
        if (api != null) TcmLog.Cat(api, TcmLog.Hooks, $"{byPlayer.PlayerName} crumbled a split bit (Untrained)");
        return true;
    }

    /// <summary>The hammer's current tool mode, read straight off the stack attribute
    /// (vanilla GetToolMode does the same; avoids needing a BlockSelection). 0 = heavy
    /// hit, 1-4 = upsets, 5 = split, 6+ = Smithing+ extras (flip).</summary>
    private static int ToolModeOf(IPlayer byPlayer)
    {
        ItemStack? held = byPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        return held == null ? -1 : held.Attributes.GetInt("toolMode");
    }

    [HarmonyPatch(typeof(BlockEntityAnvil), "OnUseOver",
        typeof(IPlayer), typeof(Vec3i), typeof(BlockSelection))]
    public static class AnvilStrikePatch
    {
        public static void Prefix(BlockEntityAnvil __instance, IPlayer byPlayer, Vec3i voxelPos, out byte __state)
        {
            // Struck voxel material BEFORE the strike lands: the productive-strike gate.
            __state = voxelPos == null ? (byte)0
                : __instance.Voxels[voxelPos.X, voxelPos.Y, voxelPos.Z];

            if (__instance.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            if (__instance.SelectedRecipe == null || __instance.WorkItemStack == null) return;
            if (__state != (byte)EnumVoxelMaterial.Metal || !__instance.CanWorkCurrent) return;
            if (MetLevel(byPlayer) > 0) return;
            if (InFocusGrace(__instance.Api, byPlayer.PlayerUID)) return;

            // Axis 1, split half, the over-strike: an Untrained split sometimes bites
            // too deep and DESTROYS the sheared bit instead of shearing it clean. The
            // voxel comes off exactly as intended (no double punishment); only Smithing+'s
            // bit return is forfeit. Decided HERE because the recovery runs in Smithing+'s
            // own OnUseOver postfix, whose order against ours is undefined, so the flag must
            // exist before any postfix does.
            if (ToolModeOf(byPlayer) == 5
                && __instance.Api.World.Rand.NextDouble() < Knob(MetDomain.OverStrikeChance, 0.15))
            {
                PendingCrumbleUid = byPlayer.PlayerUID;
            }
        }

        public static void Postfix(BlockEntityAnvil __instance, IPlayer byPlayer, Vec3i voxelPos, byte __state)
        {
            if (__instance.Api?.Side != EnumAppSide.Server || byPlayer == null) return;
            if (__instance.SelectedRecipe == null || __instance.WorkItemStack == null) return;

            // The stamp rides the work item through reheats to completion.
            __instance.WorkItemStack.Attributes.SetString(SmithAttr, byPlayer.PlayerUID);
            __instance.WorkItemStack.Attributes.SetString(SmithNameAttr, byPlayer.PlayerName);

            // Axis 1, move half, the slip: on the move modes (heavy hit + the four
            // upsets) an Untrained blow occasionally lands wide and nudges ONE extra
            // nearby voxel a step it was not meant to take. Nothing is destroyed; the
            // piece needs correcting. Productive strikes only (the struck voxel was
            // metal), never inside the focus grace, snaps to zero at Novice I.
            if (MetLevel(byPlayer) > 0) return;
            if (voxelPos == null || __state != (byte)EnumVoxelMaterial.Metal) return;
            int mode = ToolModeOf(byPlayer);
            if (mode < 0 || mode > 4) return;
            if (InFocusGrace(__instance.Api, byPlayer.PlayerUID)) return;
            if (__instance.Api.World.Rand.NextDouble() >= Knob(MetDomain.MoveSlipChance, 0.05)) return;

            if (SlipAdjacentVoxel(__instance, voxelPos))
            {
                StartFocusGrace(__instance.Api, byPlayer.PlayerUID);
                (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.GetL((byPlayer as IServerPlayer)?.LanguageCode ?? "en", "almanactcm:overstrike-slip"),
                    EnumChatType.Notification);
                TcmLog.Cat(__instance.Api, TcmLog.Hooks, $"{byPlayer.PlayerName} slipped a voxel near {voxelPos} (Untrained)");
            }
        }

        public static void Finalizer()
        {
            PendingCrumbleUid = null;
        }

        /// <summary>Nudge one random metal voxel adjacent to the strike point one step
        /// in a random direction, using vanilla's own OnUpset so the move obeys every
        /// vanilla rule (blocked moves no-op). Returns true only if the grid actually
        /// changed: a slip that moved nothing costs the player nothing.</summary>
        private static bool SlipAdjacentVoxel(BlockEntityAnvil anvil, Vec3i pos)
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
                if (anvil.Voxels[x, pos.Y, z] != (byte)EnumVoxelMaterial.Metal) continue;

                byte[,,] before = (byte[,,])anvil.Voxels.Clone();
                anvil.OnUpset(new Vec3i(x, pos.Y, z), BlockFacing.HORIZONTALS[rand.Next(4)]);
                if (VoxelsEqual(before, anvil.Voxels)) continue;

                AccessTools.Method(typeof(BlockEntityAnvil), "RegenMeshAndSelectionBoxes")
                    ?.Invoke(anvil, null);
                anvil.MarkDirty();
                anvil.Api.World.BlockAccessor.MarkBlockDirty(anvil.Pos);
                return true;
            }
            return false;
        }

        private static bool VoxelsEqual(byte[,,] a, byte[,,] b)
        {
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 6; y++)
                    for (int z = 0; z < 16; z++)
                        if (a[x, y, z] != b[x, y, z]) return false;
            return true;
        }
    }

    /// <summary>Pending Maker's Mark for the synchronous CheckIfFinished→OnItemPickedUp
    /// window: the finished stack is a fresh recipe-output clone, so the workpiece
    /// stamp must be re-applied to it by ambient context.</summary>
    private static (string uid, string name, int level)? pendingMaker;

    /// <summary>Collectible id of the recipe output about to complete, captured in the prefix so
    /// the completion postfix can find that exact head in inventory or on the ground.</summary>
    private static int pendingOutputId;

    /// <summary>Code path of that same output, for the first-work ruling: refining a mass or
    /// bloom into an INGOT is not a finished work, only an actual smithed piece is.</summary>
    private static string? pendingOutputCode;

    [HarmonyPatch(typeof(BlockEntityAnvil), nameof(BlockEntityAnvil.CheckIfFinished))]
    public static class AnvilFinishPatch
    {
        public static void Prefix(BlockEntityAnvil __instance, out int __state)
        {
            __state = __instance.SelectedRecipeId;
            string? uid = __instance.WorkItemStack?.Attributes.GetString(SmithAttr);
            string? name = __instance.WorkItemStack?.Attributes.GetString(SmithNameAttr);
            pendingMaker = uid == null ? null : (uid, name ?? "", MakerLevelOf(__instance.Api, uid));
            pendingOutputId = __instance.SelectedRecipe?.Output?.ResolvedItemstack?.Collectible?.Id ?? 0;
            pendingOutputCode = __instance.SelectedRecipe?.Output?.ResolvedItemstack?.Collectible?.Code?.Path;

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
            // Ingot outputs are excluded by ruling (2026-07-27): hammering a mass or a
            // bloom into an ingot is refinement, the same bar everyone makes; the
            // capstone is the first actual PIECE (a toolhead, a blade) smithed from it.
            if (pendingMaker is { } pm && pm.uid == byPlayer.PlayerUID
                && pendingOutputCode?.StartsWith("ingot") != true)
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
            if (pendingMaker is { } m && m.level >= Rank.Journeyman && pendingOutputId != 0)
                StampCompletedOutput(__instance, byPlayer, m, pendingOutputId);
        }

        public static void Finalizer()
        {
            pendingMaker = null;
            pendingOutputCode = null;
        }
    }

    /// <summary>Find the just-completed head (by collectible id) in the smith's inventory OR
    /// among items dropped near the anvil, and stamp the maker's mark on it. Runs shortly after
    /// completion so post-processors (Toolsmith/Smithing+ rebuilds) have settled.</summary>
    private static void StampCompletedOutput(BlockEntityAnvil anvil, IPlayer byPlayer,
        (string uid, string name, int level) maker, int collId)
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

    private static bool StampIfMatch(ICoreAPI api, ItemStack? s, int collId, (string uid, string name, int level) maker)
    {
        if (s?.Collectible?.Id != collId || s.Attributes.HasAttribute(MakerAttr)) return false;
        ApplyMark(s, maker, api);
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
            if (maker.level < Rank.Journeyman) return;   // Journeyman+ only: lesser work carries no mark
            // ApplyMark also assigns the GM signature: a directly-forged weapon/tool is
            // classifiable here; a bare Toolsmith head is not and takes its edge at assembly.
            ApplyMark(stack, maker, byEntity?.Api);
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

        private static void ReStamp(ICoreAPI api, ItemSlot slot, int collectibleId, (string uid, string name, int level) maker)
        {
            ItemStack? s = slot?.Itemstack;
            if (s?.Collectible?.Id != collectibleId) return;
            if (s.Attributes.HasAttribute(MakerAttr)) return;
            ApplyMark(s, maker, api);
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
            ItemStack? stack = inSlot?.Itemstack;
            var attrs = stack?.Attributes;
            string? maker = attrs?.GetString(MakerNameAttr);
            if (attrs == null || string.IsNullOrEmpty(maker)) return;
            // Tiered provenance from the frozen maker level; tools stamped before the mark
            // carried a rank at all fall back to the flat line.
            int level = MarkLevel(stack);
            bool honed = MetSignature.IsHoned(stack);
            bool durable = MetSignature.IsDurable(stack);

            // When one hand forged the tool AND made a part of it, ToolPartMarks folds the
            // forged credit into its lineage line (RULED 2026-08-18) and this maker line
            // stands down. Below Grandmaster only; a masterwork never folds. The quality
            // clause travels with the fold via QualityClause. The unfitted and signature
            // lines below still render either way.
            if (!ToolPartMarks.WillFoldToolMark(stack))
            {
                string makerLine = Lang.Get(level >= Rank.Journeyman ? MakerKey(level) : "almanactcm:made-by", maker);

                // The numbers ruling (2026-08-01): the maker line carries the quality's percent
                // when the work is awake (the attribute is the multiplier Smithing+ applies), and
                // the generic GM wear-skip rides here when no Durable line will claim its own.
                makerLine += QualityClause(stack);
                if (level >= Rank.Grandmaster && !durable)
                {
                    int skipPct = (int)System.Math.Round(Knob(MetDomain.GmWearSkip, 0.08) * 100.0);
                    if (skipPct > 0)
                        makerLine += Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-wear-skip", skipPct));
                }
                dsc.AppendLine(makerLine);
            }

            // The fitting rule's face: an assembled tool whose maker's work is dormant says
            // so, and says what to do about it, or the rule reads as a silent nerf.
            if (ToolsmithLoaded && level >= Rank.Journeyman
                && attrs.HasAttribute("tinkeredToolHead")
                && !attrs.HasAttribute(SmithingQualityAttr))
                dsc.AppendLine(Lang.Get("almanactcm:unfitted-mark"));

            // The GM signature (Axis 6 stage 2), a quiet line under the provenance, its
            // effect quantified per the numbers ruling.
            if (honed)
                dsc.AppendLine(Lang.Get("almanactcm:honed-mark")
                    + Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-armor-pierce",
                        (int)Knob(MetDomain.HonedArmorPierce, 1))));
            else if (durable)
                dsc.AppendLine(Lang.Get("almanactcm:durable-mark")
                    + Engine.TcmTooltip.Clause(Lang.Get("almanactcm:tip-wear-skip",
                        (int)System.Math.Round(Knob(MetDomain.DurableWearSkip, 0.18) * 100.0))));
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

    /// <summary>One pending quench, remembered from the cooling tick that saw the tongs
    /// until the settle that decides whether the work survived. Keyed weakly on the stack
    /// itself: a shattered piece has its stack nulled by vanilla, so its entry becomes
    /// unreachable and collectable without any cleanup pass of ours.</summary>
    private sealed class QuenchAttempt
    {
        internal string Uid = "";
        internal int Cx;
    }

    private static readonly ConditionalWeakTable<ItemStack, QuenchAttempt> quenchAttempts = new();

    [HarmonyPatch(typeof(CollectibleBehaviorQuenchable), "IsGettingCooled")]
    public static class QuenchContextPatch
    {
        public static void Prefix(IWorldAccessor world, ItemSlot slot, Vec3d pos)
        {
            if (world.Side != EnumAppSide.Server) return;
            quenchingPlayer = (slot.Inventory as InventoryBasePlayer)?.Player;

            // The tongs are noted here, but NOT paid here (RULED 2026-08-19). This method
            // runs on every cooling tick and only ROLLS the break; the piece can still
            // shatter afterwards, and a shatter must teach nothing. The credit is handed
            // over in QuenchSettledPatch, which vanilla reaches only on a quench that held.
            if (quenchingPlayer != null && slot.Itemstack != null)
            {
                var attempt = new QuenchAttempt
                {
                    Uid = quenchingPlayer.PlayerUID,
                    Cx = HashCode.Combine(slot.Itemstack.Collectible.Id,
                        (int)pos.X / 4, (int)pos.Y / 4, (int)pos.Z / 4),
                };
                quenchAttempts.Remove(slot.Itemstack);
                quenchAttempts.Add(slot.Itemstack, attempt);
            }
        }

        public static void Finalizer()
        {
            quenchingPlayer = null;
        }
    }

    /// <summary>The quench that held. Vanilla calls applyQuenchedStats from trySettleWorkItem
    /// only when a piece settles after real time in the quench range, and a shattered piece
    /// never gets there (IsGettingCooled nulls the slot stack first), so this is the seam
    /// where the work is actually finished. Practice is paid once, against the context the
    /// attempt recorded, so the ledger repeat rules read it exactly as before.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorQuenchable), "applyQuenchedStats")]
    public static class QuenchSettledPatch
    {
        public static void Postfix(IWorldAccessor world, ItemStack itemstack)
        {
            if (world.Side != EnumAppSide.Server || itemstack == null) return;
            if (!quenchAttempts.TryGetValue(itemstack, out QuenchAttempt? attempt)) return;
            quenchAttempts.Remove(itemstack);

            // Resolved by UID rather than held as a reference: the table outlives the tick,
            // and a smith who logs out mid-cool should not be kept alive by a work item.
            IPlayer? smith = world.PlayerByUid(attempt.Uid);
            if (smith == null) return;
            Core?.Ledger?.Log(smith, MetDomain.Code, MetDomain.TechQuenching, attempt.Cx);
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

    /// <summary>The player mid-pour from a held crucible, for seams outside this file. The
    /// industrialstory casting-sand fill has no player of its own, and a crucible tipped straight
    /// into a bed runs inside this same interaction, so the sand seam reads the pourer here.</summary>
    internal static IPlayer? PouringPlayer => pouringPlayer;

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
    private static readonly Dictionary<string, (string uid, string name, int level)> moldCasters = new();

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
                (pouringPlayer.PlayerUID, pouringPlayer.PlayerName, MakerLevelOf(__instance.Api, pouringPlayer.PlayerUID));

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
            if (caster.level < Rank.Journeyman) return;   // Journeyman+ only, same as forged work

            foreach (ItemStack stack in __result)
            {
                if (stack == null || stack.Attributes.HasAttribute(MakerAttr)) continue;
                ApplyMark(stack, caster, __instance.Api);
            }
        }
    }

    // -------------------------------------------------------------- assembly

    /// <summary>MARK TRANSFER, not practice: OnCreatedByCrafting fires on grid
    /// PREVIEWS and inside Toolsmith's held assembly (dummy inventory, no player),
    /// so it's the wrong seam for XP but the perfect one for provenance — every
    /// path that builds a tool from a head passes through here with the input
    /// slots visible. The head's Maker's Mark rides onto the finished tool
    /// (RULED 2026-07-13: forged or cast, the head's maker marks the tool).
    ///
    /// THE FITTING RULE (RULED 2026-07-31, toolsmith only): the mark always rides,
    /// but the head's QUALITY and the GM signature wake only when the tool is
    /// assembled at a workbench. A head hafted in the field works, and the maker's
    /// work in it lies dormant until the tool is taken apart and fitted properly;
    /// a later hand-rework puts it back to sleep the same way. The anvil makes the
    /// head, the bench makes the tool. Direct-forged and cast whole tools are
    /// untouched: no assembly, nothing to fit.</summary>
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
                int headLevel = MarkLevel(input.Itemstack);
                output.Attributes.SetInt(MakerLevelAttr, headLevel);

                // The fitting rule: with a bench in the world, only the bench wakes the
                // maker's work. Provenance above always rides; the buffs below are earned
                // by assembling properly. Without toolsmith there is no bench to ask for.
                if (ToolsmithLoaded && !BenchAssemblyContext) return;

                // Quality is COPIED, never recomputed from headLevel, and that is deliberate.
                // The attribute is the head's CURRENT buff state, which the repair gate may
                // have stripped or re-stamped since the mark was frozen; recomputing would
                // hand a stripped head a full-quality tool and quietly undo the gate. The
                // frozen level says who made it; this float says what condition it is in.
                output.Attributes.SetFloat(SmithingQualityAttr,
                    input.Itemstack.Attributes.GetFloat(SmithingQualityAttr, 1f));
                RefreshHeadDurability(output, outputSlot?.Inventory?.Api);
                // GM signature: keep an already-marked head's edge; otherwise the bare head
                // finally becomes a classifiable tool here, so assign by the finished type.
                if (!MetSignature.CopySignature(input.Itemstack, output))
                    MetSignature.Assign(output, headLevel);
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

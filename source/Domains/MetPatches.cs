using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
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
    /// KEEP PERMANENTLY (ruled 2026-08-12 as "until 0.5.0", made permanent 2026-08-27). The
    /// Quire's world was wiped, so the SERVER holds no tier-stamped tools. That is not the whole
    /// population: closed-beta testers run SINGLEPLAYER worlds that were never wiped and can
    /// still be holding tools that carry this key and its quality buff. They are the live
    /// consumer, and the reason the "no server data, therefore no data" argument does not hold.
    /// Nothing migrates them, either: ReStamp refuses any stack that already has MakerAttr, and
    /// every legacy stack has one, so a tool is never upgraded in place. This branch is the only
    /// thing that still knows what those saved ints mean.
    ///
    /// REMOVAL ABANDONED 2026-08-27. This shim is permanent. Three reasons, in weight order.
    ///
    /// THE ANNOUNCEMENT NEVER HAPPENED. The 2026-08-12 plan required the releases leading up to
    /// 0.5.0 to carry a reminder. The deprecation appears exactly once in PATCHNOTES.md, inside
    /// 0.4.38's own notes; 0.4.39 and 0.4.40 say nothing, and no changelog ships inside the zip
    /// (dll, pdb, deps, modinfo and assets, nothing else). The testers holding the affected
    /// singleplayer worlds were never actually told. Removing on the strength of a warning that
    /// was not given is not a thing we get to do.
    ///
    /// THE DELETE IS COMPILE-CLEAN BUT NOT CONSEQUENCE-CLEAN. MarkLevel returning -1 diverges
    /// from five consumers that do not gate on it, and two of those are worse than the flat
    /// fallback 0.4.38 promised. MakerKey has no unmarked branch, so a stored legacy head renders
    /// through ToolPartMarks as "Smithed by", asserting a Journeyman rank the stack never held.
    /// WillFoldToolMark flips true the moment MarkLevel drops to -1, and MetPatches wraps the
    /// ENTIRE maker-line block in it, so on a folding tool the promised "Made by" line does not
    /// appear at all. Beyond those: the folded quality clause gates on Level rather than the
    /// float, and the unfitted-mark warning disappears, reinstating the silent nerf the comment
    /// beside it exists to prevent. Honouring the promise costs four more changes across three
    /// files, in provenance rendering, immediately before a release.
    ///
    /// MIGRATION WAS CONSIDERED AND DOES NOT WORK. A write-through here (convert once, stamp
    /// MakerLevelAttr, drop the legacy key) would upgrade a stack on first read and make a later
    /// delete free. But the dominant reader is MarkTooltipPatch on CollectibleObject
    /// .GetHeldItemInfo, which runs CLIENT side, and a client-side attribute write never reaches
    /// the server's copy of the stack. It would appear to work and quietly persist nothing.
    ///
    /// The cost of keeping it is one const and a four-line branch that nothing else touches.</summary>
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

    // ---------------------------------------------- what may carry a mark (RULED 2026-09-19)

    /// <summary>Collectibles CONSUMED by a grid recipe whose output is damageable: heads, blades
    /// and every other part that becomes a tool. Built once, lazily, and cleared on Dispose.
    /// Keyed on (class, id) rather than id alone because block ids and item ids are separate
    /// ranges, so an int on its own is not unique across the two.</summary>
    private static HashSet<(EnumItemClass, int)>? toolPartIds;

    /// <summary>Drop the lazily-built part index. Called from the ModSystem's Dispose for the
    /// reason the anvil-lag index is cleared there: a second world in the same singleplayer
    /// process reassigns collectible ids, and a set built against the previous one would mark
    /// whatever now happens to sit at those numbers.</summary>
    internal static void ClearCaches() => toolPartIds = null;

    /// <summary>Does this stack own a durability pool the maker's bonus can land in? The vanilla
    /// test, which is `GetMaxDurability > 1` (Collectible.cs:3211, ItemSlotTrade.cs:82), with the
    /// cheap static field tried first: `Durability` resolves `durabilitybytype` at load, so every
    /// vanilla tool, weapon and armour piece answers without entering another mod's postfix.
    /// The threshold is 1, not 0: `CollectibleObject.Durability` DEFAULTS to 1.</summary>
    private static bool IsDamageable(ItemStack? stack)
    {
        var coll = stack?.Collectible;
        if (coll == null) return false;
        if (coll.Durability > 1) return true;
        // Guarded for the reason RefreshHeadDurability is guarded: this runs other mods'
        // postfixes and Toolsmith's can NRE on a bare head. A throw means "cannot tell", and
        // the part index below is the second chance, so failing false here costs nothing.
        try { return coll.GetMaxDurability(stack) > 1; }
        catch { return false; }
    }

    /// <summary>THE gate on the Maker's Mark (RULED 2026-09-19, from Elitephoenix's report of
    /// ingots that would not stack).
    ///
    /// The mark writes four attributes plus the GM signature, and attributes are part of stack
    /// identity, so a marked stack never merges with an unmarked one. Until now nothing tested
    /// WHAT was being marked, and vanilla ships two smithing recipes whose output is an ingot
    /// (`ingot.json` -> ingot-iron, `steel.json` -> ingot-steel, both maxstacksize 16 and neither
    /// damageable). Every stackable non-tool output was hit the same way: nails, rod, hoop,
    /// bracket, boss, plate, scale, chain, arrowhead. POT has carried the equivalent gate since
    /// the beginning and its comment names the hazard exactly; MET never got one.
    ///
    /// Two tests, because neither alone covers the roster:
    ///   1. It has a durability pool. Tools, weapons, armour, and under Toolsmith or Smithing+
    ///      the heads and parts too.
    ///   2. It is consumed by a grid recipe whose output has one. This is what keeps a vanilla
    ///      head marked on a server running NEITHER of those mods, where a bare axehead has
    ///      durability 1 and the head-marks-the-tool lineage (RULED 2026-07-13) would otherwise
    ///      go quiet. Computed from live recipes rather than from a name list, so it follows
    ///      whatever parts a mod adds without being told about them.
    ///
    /// An ingot is an input to no damageable recipe output, so it fails both and stacks again.
    /// The same reasoning already lives twelve lines below in the first-work capstone, which has
    /// excluded ingot outputs since 2026-07-27: hammering a bloom into an ingot is refinement,
    /// not a piece. That ruling simply never reached the mark.</summary>
    internal static bool CarriesMakersMark(ItemStack? stack, ICoreAPI? api)
    {
        var coll = stack?.Collectible;
        if (coll == null) return false;
        if (IsDamageable(stack)) return true;
        if (api == null) return false;
        toolPartIds ??= BuildToolPartIds(api);
        return toolPartIds.Contains((coll.ItemClass, coll.Id));
    }

    /// <summary>Walk every grid recipe once; for each whose output is damageable, remember what it
    /// eats. Wildcard ingredients are collected as patterns and matched in ONE pass over the
    /// collectible list at the end rather than a pass per recipe, which is the difference between
    /// thousands of Match calls and millions.</summary>
    private static HashSet<(EnumItemClass, int)> BuildToolPartIds(ICoreAPI api)
    {
        var ids = new HashSet<(EnumItemClass, int)>();
        var patterns = new List<(AssetLocation Code, EnumItemClass Type)>();
        int unexpandable = 0;

        foreach (var recipe in api.World.GridRecipes)
        {
            if (!IsDamageable(recipe?.Output?.ResolvedItemStack)) continue;
            var ingredients = recipe!.ResolvedIngredients;
            if (ingredients == null) continue;

            foreach (var ing in ingredients)
            {
                if (ing?.Code == null) continue;
                switch (ing.MatchingType)
                {
                    case EnumRecipeMatchType.Exact:
                        var c = ing.ResolvedItemStack?.Collectible;
                        if (c != null) ids.Add((c.ItemClass, c.Id));
                        break;
                    case EnumRecipeMatchType.Wildcard:
                    case EnumRecipeMatchType.NamedWildcard:
                    case EnumRecipeMatchType.AdvancedWildcard:
                        patterns.Add((ing.Code, ing.Type));
                        break;
                    default:
                        // Regex and TagsOnly cannot be expanded by WildcardUtil, so they are
                        // counted and named rather than silently dropped: an ingredient missed
                        // here is a part that loses its mark, and that must not be invisible.
                        unexpandable++;
                        break;
                }
            }
        }

        if (patterns.Count > 0)
        {
            foreach (var coll in api.World.Collectibles)
            {
                if (coll?.Code == null) continue;
                var key = (coll.ItemClass, coll.Id);
                if (ids.Contains(key)) continue;
                foreach (var (code, type) in patterns)
                {
                    if (coll.ItemClass != type) continue;
                    if (WildcardUtil.Match(code, coll.Code)) { ids.Add(key); break; }
                }
            }
        }

        TcmLog.Cat(api, TcmLog.Config,
            $"MET mark gate: {ids.Count} collectible(s) are parts of a damageable grid output " +
            $"({patterns.Count} wildcard ingredient pattern(s) expanded" +
            (unexpandable > 0 ? $", {unexpandable} regex/tag ingredient(s) NOT expanded" : "") + ")");
        return ids;
    }

    /// <summary>Apply the full Maker's Mark to a freshly-made stack: provenance, frozen level,
    /// the Smithing+ quality stamp, the durability top-up, and the GM signature. One body, four
    /// callers (forge-immediate, forge-rescan, forge-restamp, cast). They were four copies of
    /// this until 2026-08-12, which is what let the level conversion have four places to go
    /// wrong.</summary>
    private static bool ApplyMark(ItemStack stack, (string uid, string name, int level) maker, ICoreAPI? api)
    {
        // The gate, in the one place all four callers pass through (CarriesMakersMark explains it).
        // Say WHY when it refuses: a silent false here is what made the POT version of this cost a
        // play-test round trip, and a mark that quietly stops appearing reads as broken.
        if (!CarriesMakersMark(stack, api))
        {
            if (api != null)
                TcmLog.Cat(api, TcmLog.Hooks, $"no maker's mark on {stack.Collectible?.Code}: " +
                    "no durability pool and not a part of any damageable grid output, so a mark " +
                    "would only cost it its stacking");
            return false;
        }

        // The max BEFORE the quality stamp, so the top-up below knows the exact factor the
        // bonus moved it by. Read first: once sp:smithingQuality lands, this number is gone.
        int maxBefore = SafeMaxDurability(stack);

        stack.Attributes.SetString(MakerAttr, maker.uid);
        stack.Attributes.SetString(MakerNameAttr, maker.name);
        stack.Attributes.SetInt(MakerLevelAttr, maker.level);
        stack.Attributes.SetFloat(SmithingQualityAttr, (float)QualityFactor(maker.level));
        RefreshHeadDurability(stack, api, maxBefore);
        MetSignature.Assign(stack, maker.level);
        return true;
    }

    // Toolsmith's own tool-PART wear pair (ToolsmithAttributes, 1.2.18 - the same scheme
    // PartQualityLevers reads for handles and bindings). A bare head keeps its wear here and
    // ignores vanilla "durability" entirely; a FINISHED tool is the other way round, because
    // Toolsmith's SetToolheadCurrentDurability is literally Collectible.SetDurability. Scaling
    // whichever pair is present covers both without having to know which one we were handed.
    private const string PartCurAttr = "toolPartCurrentDurability";
    private const string PartMaxAttr = "toolPartMaxDurability";

    /// <summary>GetMaxDurability, guarded. It runs every other mod's postfixes and Toolsmith's
    /// can NRE on a bare head, so a throw means "cannot tell" and answers 0.</summary>
    private static int SafeMaxDurability(ItemStack? stack)
    {
        if (stack?.Collectible == null) return 0;
        try { return stack.Collectible.GetMaxDurability(stack); }
        catch { return 0; }
    }

    /// <summary>Carry the stored wear numbers across a change in max durability, so the quality
    /// bonus neither leaves a tool born worn nor quietly repairs one (LGD-139, rebuilt 2026-10-16).
    ///
    /// Toolsmith and SmithingPlus report a tool's condition as current/max where max is COMPUTED
    /// live through GetMaxDurability and current is a STORED integer. The instant our
    /// sp:smithingQuality lifts max, every stored current is stale by exactly that factor: a fresh
    /// Journeyman tool reads 5000/5250 and is 5% damaged before its first swing. So multiply the
    /// stored numbers by the same factor the max just moved by. Percentage is preserved, which is
    /// the whole point: filling to max instead (what this did until now) also silently repaired any
    /// worn head that passed back through a re-stamp.
    ///
    /// TWO INVARIANTS, both learned the hard way:
    ///
    /// 1. CREATE THE ATTRIBUTE ONLY WHERE IT IS LOAD-BEARING, which is any stack that really has a
    ///    pool. On those, stack size is 1, so writing it costs no merging, and withholding it is
    ///    actively dangerous: Toolsmith reads an absent "durability" on an assembled tool as a NULL
    ///    HEAD and calls ResetNullHead, whose own log warns it "might result in fallback data of a
    ///    Candle being assigned" (ItemStackExtensions.GetToolheadCurrentDurability). A cast or
    ///    direct-forged tool that vanilla left unstamped is exactly that case. Verified live
    ///    2026-10-16 (Chanka: a fresh assembled iron pickaxe reads 5250/5250), so this path works
    ///    and must not be broken on the way past it.
    /// 2. NOTHING AT ALL on a stack with no durability pool (max &lt;= 1, the IsDamageable threshold:
    ///    CollectibleObject.Durability DEFAULTS to 1). This is the still-live half of LGD-139.
    ///    GetMaxDurability answers 1 for an ingot and for a vanilla toolhead, and the old `max > 0`
    ///    test wrote durability=1 onto both. That one integer is why hand-forged ingots have never
    ///    stacked with swage-table or helve-hammer ingots, and it does the same to a vanilla axehead
    ///    (maxstacksize 4). The mark keys themselves stack again as of 0.6.0's identity law; this
    ///    attribute was the part the ignore list cannot cover.</summary>
    private static void RefreshHeadDurability(ItemStack? stack, ICoreAPI? api, int maxBefore)
    {
        if (stack?.Collectible == null) return;
        int maxAfter = SafeMaxDurability(stack);

        // Invariant 2: no pool, no business writing wear state onto it.
        if (maxAfter <= 1 || maxBefore <= 1) return;
        if (maxAfter == maxBefore) return;

        double factor = (double)maxAfter / maxBefore;

        // A real pool with nothing stored anywhere: vanilla reads absent as full, but Toolsmith
        // reads it as a null head (invariant 1), so stamp it. Absent already meant full, so
        // maxAfter IS the carried value and there is nothing to scale - writing it and then
        // scaling it would apply the bonus twice. This is NOT the old fill-to-max either: that
        // overwrote a STORED number, and in doing so repaired any worn head that passed back
        // through a re-stamp.
        if (!stack.Attributes.HasAttribute("durability") && !stack.Attributes.HasAttribute(PartMaxAttr))
            stack.Attributes.SetInt("durability", maxAfter);
        else
            ScaleWear(stack, "durability", factor);   // vanilla pool, and Toolsmith's finished tool

        ScaleWear(stack, PartCurAttr, factor);    // Toolsmith's bare part: current
        ScaleWear(stack, PartMaxAttr, factor);    // Toolsmith's bare part: its stored max

        if (api != null)
            TcmLog.Cat(api, TcmLog.Hooks, $"durability carried on {stack.Collectible.Code}: "
                + $"max {maxBefore} -> {maxAfter} (x{factor:0.###}), stored wear scaled to match");
    }

    /// <summary>Scale one stored wear integer. Absent stays absent (invariant 1): an absent
    /// durability already means full, and creating one would cost the stack its stacking.</summary>
    private static void ScaleWear(ItemStack stack, string attr, double factor)
    {
        if (!stack.Attributes.HasAttribute(attr)) return;
        int value = stack.Attributes.GetInt(attr, 0);
        if (value <= 0) return;
        stack.Attributes.SetInt(attr, Math.Max(1, (int)Math.Round(value * factor)));
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

    /// <summary>Monotonic salt for the two MET completion grants, smithing and casting (0.5.15,
    /// LGD-238). Both contexts previously keyed on position alone (plus the recipe id for smithing),
    /// so repeating the same piece at the same anvil or the same mold inside the 90s dedup window
    /// banked zero. Both grants are edge-triggered on completion, so one increment is one finished
    /// piece. Shared by the two nested patch classes; Interlocked because block entities tick off
    /// the main thread.</summary>
    private static int metActSeq;

    [HarmonyPatch(typeof(BlockEntityAnvil), nameof(BlockEntityAnvil.CheckIfFinished))]
    public static class AnvilFinishPatch
    {
        public static void Prefix(BlockEntityAnvil __instance, IPlayer byPlayer, out int __state)
        {
            __state = __instance.SelectedRecipeId;
            string? uid = __instance.WorkItemStack?.Attributes.GetString(SmithAttr);
            string? name = __instance.WorkItemStack?.Attributes.GetString(SmithNameAttr);
            pendingMaker = uid == null ? null : (uid, name ?? "", MakerLevelOf(__instance.Api, uid));
            pendingOutputId = __instance.SelectedRecipe?.Output?.ResolvedItemstack?.Collectible?.Id ?? 0;
            pendingOutputCode = __instance.SelectedRecipe?.Output?.ResolvedItemstack?.Collectible?.Code?.Path;

            // byPlayer != null is the helve-hammer guard (0.5.16). Vanilla calls
            // CheckIfFinished from three places and one of them is onHelveHitSuccess
            // (BlockEntityAnvil:547 in 1.22.7), which passes null on EVERY automated
            // hit. A powered helve therefore emitted one line per strike, several a
            // second, and the Postfix below discards exactly those calls because it
            // early-returns on a null player. The log was recording work that was
            // then thrown away. A real player's strike still traces.
            if (__instance.Api?.Side == EnumAppSide.Server && __state != -1 && byPlayer != null)
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

            // Context UNIQUE per finished piece (0.5.15, LGD-238). It was (recipeId, anvilPos), so a
            // second knife at the same anvil inside the 90s dedup window banked exactly zero, which is
            // Chanka's report verbatim: "forged shears between two knives but didn't get XP for that
            // second knife. went too fast." An anvil is not a machine and a second knife is real work,
            // so this pays at the full rate; the daily curve and the metal cost are the governors.
            // Safe to make unique because this postfix is edge-triggered on COMPLETION (a recipe was
            // selected going in and vanilla reset it), so it fires once per piece, never per strike.
            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechSmithing,
                HashCode.Combine(__state, __instance.Pos, System.Threading.Interlocked.Increment(ref metActSeq)));

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
        return ApplyMark(s, maker, api);
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
            // Refused (an ingot, a rod, a handful of nails) means there is nothing to re-stamp
            // either, so the whole sweep below is skipped with it.
            if (!ApplyMark(stack, maker, byEntity?.Api)) return;
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
            if (!ApplyMark(s, maker, api)) return;
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

    /// <summary>Vanilla's own TempAttributes key for the cached break roll
    /// (CollectibleBehaviorQuenchable.IsGettingCooled, 1.22.7).</summary>
    private const string WillBreakAttr = "willbreak";

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

    /// <summary>How far from a dropped workpiece a smith can stand and still be the one
    /// quenching it. Deliberately short: this is a last resort, and a generous radius would
    /// start crediting bystanders for other people's work.</summary>
    private const double QuenchProximityRange = 6.0;

    /// <summary>
    /// Who is quenching this piece (LGD-356, fixed 2026-10-16).
    ///
    /// THE BUG THIS EXISTS FOR. This used to be one line:
    ///
    ///     quenchingPlayer = (slot.Inventory as InventoryBasePlayer)?.Player;
    ///
    /// which resolves a player ONLY while the hot piece sits in his own inventory. Dropping the
    /// work into water is the ordinary way to quench it, and a dropped stack is not in an
    /// InventoryBasePlayer, so the whole MET quench layer silently switched off on the route
    /// players actually use. Three things hang off this one value and all three failed together:
    ///
    ///   1. the shatter reduction (the old ShatterChancePatch returned early on null), so a Master rolled
    ///      at VANILLA odds instead of shatterFactorGm. Chanka lost iron pounder caps to this on
    ///      2026-10-14 at roughly two and a half times the rate the mod promises him;
    ///   2. the QuenchAttempt record, so the quench earned no practice either;
    ///   3. nothing logged, which is why it took a player noticing his credits did not add up.
    ///
    /// It also explains LGD-334 without any tooltip work. The tooltip was honest: on that route
    /// the chance really was vanilla.
    ///
    /// GROUND STORAGE IS A REAL SECOND ROUTE, not a hypothetical: BEGroundStorage is one of
    /// vanilla's own Quenchable call sites, and its slots are not player inventory slots either.
    ///
    /// ATTRIBUTION ORDER, and why. The smith stamp is preferred over proximity because it names
    /// the person who actually forged the piece rather than whoever happens to be standing there.
    /// Proximity is the last resort, kept short, and it only ever applies to work nobody signed.
    /// An offline smith resolves to null by PlayerByUid, which falls through to proximity: his
    /// level cannot be read while he is gone, and refusing to quench would be worse than a
    /// neighbour finishing the job.
    ///
    /// THE STAMP STEP WAS DEAD UNTIL 0.6.3. It read SmithAttr, which only ever lives on the anvil
    /// WORK ITEM (OnUseOver stamps it at the first strike). Vanilla builds the finished piece as a
    /// fresh clone of the recipe output (BEAnvil.cs:583 in 1.22.7) and carries nothing across but
    /// the temperature, so no finished knife or chisel ever had SmithAttr and every tossed piece
    /// fell through to proximity. The finished piece carries MakerAttr (ApplyMark), so that is read
    /// first now. The maker must also be within the same short leash: a mark travels with traded
    /// work, and a Master in another town must not lend his factor, or bank practice, for a
    /// stranger's quench.
    /// </summary>
    private static IPlayer? ResolveQuencher(IWorldAccessor world, ItemSlot? slot, Vec3d pos)
    {
        // 1. Held or carried: the original path, and still the common one.
        var own = (slot?.Inventory as InventoryBasePlayer)?.Player;
        if (own != null) return own;

        // 2. Dropped, or in ground storage: ask the piece who made it, if he is at hand.
        var attrs = slot?.Itemstack?.Attributes;
        string? smithUid = attrs?.GetString(MakerAttr) ?? attrs?.GetString(SmithAttr);
        if (smithUid != null && world.PlayerByUid(smithUid) is { } smith && WithinQuenchReach(smith, pos))
            return smith;

        // 3. Unsigned work, or a smith who is gone or far off. Nearest player, short leash.
        return world.NearestPlayer(pos.X, pos.Y, pos.Z) is { } near && WithinQuenchReach(near, pos)
            ? near
            : null;
    }

    private static bool WithinQuenchReach(IPlayer player, Vec3d pos)
        => player.Entity?.Pos != null && player.Entity.Pos.DistanceTo(pos) <= QuenchProximityRange;

    [HarmonyPatch(typeof(CollectibleBehaviorQuenchable), "IsGettingCooled")]
    public static class QuenchContextPatch
    {
        public static void Prefix(CollectibleBehaviorQuenchable __instance, IWorldAccessor world,
            ItemSlot slot, Vec3d pos, float temperature)
        {
            if (world.Side != EnumAppSide.Server) return;
            IPlayer? smith = ResolveQuencher(world, slot, pos);
            if (smith == null || slot.Itemstack == null) return;

            // The tongs are noted here, but NOT paid here (RULED 2026-08-19). This method
            // runs on every cooling tick and only ROLLS the break; the piece can still
            // shatter afterwards, and a shatter must teach nothing. The credit is handed
            // over in QuenchSettledPatch, which vanilla reaches only on a quench that held.
            var attempt = new QuenchAttempt
            {
                Uid = smith.PlayerUID,
                Cx = HashCode.Combine(slot.Itemstack.Collectible.Id,
                    (int)pos.X / 4, (int)pos.Y / 4, (int)pos.Z / 4),
            };
            quenchAttempts.Remove(slot.Itemstack);
            quenchAttempts.Add(slot.Itemstack, attempt);

            RollShatter(__instance, world, slot.Itemstack, temperature, smith);
        }
    }

    /// <summary>
    /// Roll the break ourselves, one step ahead of vanilla (0.6.3).
    ///
    /// Vanilla rolls once per quench inside IsGettingCooled and caches the outcome in the stack's
    /// TempAttributes under "willbreak" until the piece settles, and it only rolls while that key
    /// is absent. Writing the key first, from the smith's own odds, makes vanilla skip its roll
    /// and run everything else (the break, the settle, the quenched stats) untouched.
    ///
    /// WHY NOT A POSTFIX ON GetShatterChance, which is what this replaced. That getter is a
    /// one-line attribute read, the shape the JIT inlines, and Harmony's own docs say a patch on
    /// an inlined method never runs. MonoMod marks a method no-inline only when it patches THAT
    /// method, and TCM applied the IsGettingCooled patch first, which compiles Harmony's
    /// replacement body on the spot, fully optimized, while the getter was still inlinable.
    /// Chanka's quench log (45 quenches at Master, 2026-10-23) read nearer vanilla than his
    /// factor on both the toss route and the right-click route, and right-click always resolved
    /// him from his hotbar, so attribution could not explain it. Inlining is the suspected cause,
    /// read from engine, Harmony and MonoMod source and not yet seen in a running game when this
    /// was written (PENDING, 0.6.3, holds the check). A roll written here has no seam for the JIT
    /// to fold away either way, whatever order the patches land in.
    ///
    /// The conditions mirror vanilla's, in vanilla's order: state first, then the temperature
    /// read, which can settle the piece exactly as vanilla's own read can.
    /// </summary>
    private static void RollShatter(CollectibleBehaviorQuenchable quenchable, IWorldAccessor world,
        ItemStack stack, float temperature, IPlayer smith)
    {
        if (stack.TempAttributes.HasAttribute(WillBreakAttr)) return;
        string state = quenchable.GetState(stack);
        if (state != "quench" && state != "overheat") return;
        if (!(temperature < stack.Collectible.GetTemperature(world, stack) - 2)) return;

        float vanilla = quenchable.GetShatterChance(world, stack);
        int level = MetLevel(smith);
        double factor = MetDomain.ShatterFactor(level,
            Knob(MetDomain.ShatterFactorUntrained, 1.5),
            Knob(MetDomain.ShatterFactorGm, 0.4));
        double chance = vanilla * factor;
        bool willBreak = world.Rand.NextDouble() < chance;
        stack.TempAttributes.SetBool(WillBreakAttr, willBreak);

        // LGD-356 asked for this. Once per quench now, not per cooling tick, so no throttle, and
        // the OUTCOME is ours to report: a shatter on the server log is a roll, not a guess.
        var api = smith.Entity?.Api;
        if (api == null) return;
        TcmLog.Cat(api, "met",
            $"quench roll for {smith.PlayerName}: {stack.Collectible.Code} vanilla {vanilla:0.###} x{factor:0.##} -> {chance:0.###} (MET level {level}), {(willBreak ? "BREAKS" : "holds")}");
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

    /// <summary>MET's domain id in the roster, for the client-side level read. Resolved once;
    /// the roster is fixed at build time.</summary>
    private static int metDomainId = -2;

    private static int MetDomainId()
    {
        if (metDomainId != -2) return metDomainId;
        metDomainId = -1;
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == MetDomain.Code) { metDomainId = i; break; }
        return metDomainId;
    }

    /// <summary>MET level from whichever side is live: the server ledger, or (client) the synced
    /// state of the local player. Same shape as FisGearGate.FisLevelOf and for the same reason -
    /// the tooltip below runs on the client, where the ledger does not exist.</summary>
    public static int MetLevelOf(ICoreAPI? api, IPlayer? player)
    {
        if (api == null || player == null) return 0;
        if (api.Side == EnumAppSide.Server)
            return AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player)?.FindDomain(MetDomain.Code)?.Level ?? 0;

        Leveling.LevelingClient? client = AlmanacTcmModSystem.ClientInstance?.Client;
        int id = MetDomainId();
        return client != null && id >= 0 && client.Domains.TryGetValue(id, out var st) ? st.Level : 0;
    }

    /// <summary>Show the smith their OWN quench odds (LGD-334).
    ///
    /// Vanilla prints `quenchable-shatter-chance` straight from GetShatterChance, which is the
    /// UNREDUCED number: the smith's factor is applied only inside the roll (RollShatter), never
    /// to the getter. While the reduction was broken that was honest, which is why LGD-334 read
    /// as "I cannot tell if it is applied". Fixing the reduction is exactly what turns the same
    /// line into a wrong number, so it has to be answered here.
    ///
    /// ADDITIVE, not a rewrite: vanilla's line stays as the base and this appends the smith's own
    /// figure beside it, so the two together read as base and earned rather than as a correction.
    /// Nothing here touches GetShatterChance on the client. That was the tempting shortcut and it
    /// is wrong: SetTemperature runs client-side too, and vanilla's applyQuenchedStats and
    /// applyTemperedStats both READ that getter and WRITE the result back into the stack's
    /// `shatterchance` attribute, so a client-side factor would bake a predicted bonus into the
    /// item and then drift against the server's copy on every sync.</summary>
    [HarmonyPatch(typeof(CollectibleBehaviorQuenchable), nameof(CollectibleBehaviorQuenchable.GetHeldItemInfo))]
    public static class QuenchTooltipPatch
    {
        public static void Postfix(CollectibleBehaviorQuenchable __instance,
            ItemSlot inSlot, System.Text.StringBuilder dsc, IWorldAccessor world)
        {
            if (world?.Side != EnumAppSide.Client) return;
            var stack = inSlot?.Itemstack;
            if (stack == null || dsc == null) return;

            IPlayer? me = (world as Vintagestory.API.Client.IClientWorldAccessor)?.Player;
            if (me == null) return;

            int level = MetLevelOf(me.Entity?.Api, me);
            double factor = MetDomain.ShatterFactor(level,
                Knob(MetDomain.ShatterFactorUntrained, 1.5),
                Knob(MetDomain.ShatterFactorGm, 0.4));

            // Novice I sits exactly on vanilla. A line saying "x1.00, no change" is noise, and the
            // vanilla line above already says it.
            if (System.Math.Abs(factor - 1.0) < 0.005) return;

            float vanilla;
            try { vanilla = __instance.GetShatterChance(world, stack); }
            catch { return; }
            if (vanilla <= 0f) return;

            dsc.AppendLine(Vintagestory.API.Config.Lang.Get("almanactcm:met-quench-shatter",
                vanilla * factor, Leveling.Domain.RankName(level), factor));
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

            // Context UNIQUE per completed cast (0.5.15, LGD-238). It was the mold position ALONE, so a
            // second pour into the same mold inside the 90s window banked zero whatever the metal and
            // whatever the tool. Edge-triggered on the not-full to full transition, so one grant per
            // fill; the smelt cost and the daily curve are the governors.
            //
            // PAID BY THE METAL, NOT BY THE MOLD (0.6.0, LGD-316). The grant was a flat 1.0 per
            // completed cast, so a mold that takes twice the metal paid exactly what a small one
            // did: the same defect shape as the station blackout, counting the act instead of the
            // work. BlockEntityToolMold sizes itself from its own blocktype (requiredUnits,
            // default 100), and at the not-full to full edge FillLevel has reached that size, so
            // FillLevel is the units actually cast and needs no access to the protected field.
            // A 100-unit mold stays exactly where it was.
            double units = Math.Clamp(__instance.FillLevel / 100.0, 0.25, 4.0);
            Core?.Ledger?.Log(pouringPlayer, MetDomain.Code, MetDomain.TechCasting,
                HashCode.Combine(__instance.Pos, System.Threading.Interlocked.Increment(ref metActSeq)),
                units);
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
                // Same read-before-stamp as ApplyMark. Toolsmith has already finished by the
                // time this postfix runs (its behavior hook is INSIDE the method we postfix) and
                // it set the tool's current durability from a max that did not yet carry quality,
                // so this is the factor its number is short by.
                int maxBefore = SafeMaxDurability(output);
                output.Attributes.SetFloat(SmithingQualityAttr,
                    input.Itemstack.Attributes.GetFloat(SmithingQualityAttr, 1f));
                RefreshHeadDurability(output, outputSlot?.Inventory?.Api, maxBefore);
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
        // Every craft is its own act. A shift-click take calls ConsumeInput once PER CRAFT in a
        // burst of milliseconds, so a context bucketed on time hashes identically across the whole
        // batch and the dedup ring pays exactly one of them: the normal way of crafting is punished
        // and the one-at-a-time clicker rewarded. A counter keeps each craft distinct. Nothing is
        // lost by it, because material is the real gate and saturation is the mathematical one
        // (banked = ceiling * x/(x+K), so the second batch is worth a fraction of the first).
        private static int seq;

        public static void Postfix(GridRecipe __instance, IPlayer byPlayer, bool __result)
        {
            if (!__result || byPlayer == null) return;
            if (byPlayer.Entity?.World?.Side != EnumAppSide.Server) return;

            ItemStack? output = __instance?.Output?.ResolvedItemStack;
            if (output?.Collectible?.Tool == null) return;
            if (output.Collectible.ToolTier < 2) return;

            // The say-nay curve (2026-09-22) rides ON TOP of the seq counter: seq keeps every
            // craft in a shift-click batch a distinct context (the bulk-craft fix), the curve
            // makes the day's fifth assembly and beyond decay, so craft-and-uncraft stops paying.
            double mult = Engine.RepeatDecay.Mult(byPlayer.PlayerUID, "MET:assembly",
                (int)byPlayer.Entity.World.Calendar.TotalDays,
                (int)Knob(MetDomain.AssemblyRepeatFree, 4), Knob(MetDomain.AssemblyRepeatDecay, 0.1));
            Core?.Ledger?.Log(byPlayer, MetDomain.Code, MetDomain.TechAssembly,
                HashCode.Combine(output.Collectible.Id, System.Threading.Interlocked.Increment(ref seq)), mult);
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

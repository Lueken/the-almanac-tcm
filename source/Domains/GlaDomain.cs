using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// GLA — "Glassmaking" defaults (rank-bonus-design.md §GLA, RULED 2026-07-09 with the
/// gla-glass-study adopted 9/9; technique-maps §GLA ruled 2026-07-08). The other half of
/// the crafting-station cluster with POT, and the first fully-mod domain: vanilla ships
/// ZERO glassmaking verbs, so the whole domain is conditional on glassmakingfork and sits
/// dormant (excluded from breadth/affinity, banked progress preserved) when it is absent.
///
/// The five ruled verbs, all [glassmakingfork]: melt preparation (smeltery charge), glass
/// blowing (pipe + mold), ladle casting, workbench cold-working, annealing. Every seam is a
/// conditional patch (AccessTools.TypeByName + warn-skip), so a missing mod deactivates the
/// domain rather than crashing the mod.
///
/// The signature (Axis 3 reliability + the GM Glassmaker's Mark, one mechanic): the THERMAL
/// WINDOW. Shatter is a deterministic thermal deadline (GlassShatter.ShouldShatter returns
/// temp &lt; 100C, a hardcoded literal), not a probability — so there is nothing to scale, and
/// the rank lever is the *threshold* itself. A per-piece tolerance stamped by the maker's rank
/// (piece-stamped, Option A) widens or narrows the window: an Untrained hand's glass shatters
/// sooner (~120C, the penalty band), a master's tolerates cooler (~80C, never immune — the
/// felt GM signature "a master's glass doesn't crack on you"). Read via one postfix on the
/// static ShouldShatter, the single funnel BOTH shatter sites call — including the ownerless
/// annealer, which is exactly why the window must be a property of the object.
///
/// Provenance (the tradeable token): the pipeline is clone-based at every conversion, so the
/// maker mark is re-stamped across the annealer's output-clone (snapshot/restore around
/// OnCommonTick) and shown in the tooltip (Blown by / Master-blown by), non-stacking pieces.
///
/// Ruled OUT (study-verified): no thrift rung (the mod already recovers full shards on shatter),
/// no material/rank gate (apparatus + copper-age tools + mod-presence already triple-gate), no
/// flawless-variant proc (zero quality variants exist). Deferred thin: firebox fuel economy
/// (durationModifier is interface-sourced, not cleanly per-player) and blend thrift.
/// </summary>
public static class GlaDomain
{
    public const string Code = "GLA";

    /// <summary>The modid that must be present for GLA to exist (the fork keeps its own id).</summary>
    public const string RequiredMod = "glassmakingfork";

    // The 5 ruled verbs, all conditional on glassmakingfork.
    public const string TechMelting = "melting";       // BlockEntityGlassSmeltery.TryAdd (owner-at-charge)
    public const string TechBlowing = "blowing";       // GlasspipeRecipeBehavior / BlockEntityGlassBlowingMold.TakeGlass
    public const string TechCasting = "casting";       // BlockEntityGlassCastingMold.TryTakeContents (collect)
    public const string TechWorkbench = "workbench";   // BlockEntityWorkbench.TryCompleteStep
    public const string TechAnnealing = "annealing";   // BlockEntityAnnealer.TryInteract (credit at retrieval)

    // ---- The thermal window (Axis 3 reliability + the GM signature, RULED). Additive offsets
    // on the vanilla 100C shatter deadline, live in ModConfig/almanactcm/GLA.json.
    /// <summary>Degrees ADDED to the shatter threshold while Untrained: their raw glass shatters
    /// at ~120C (a narrower window). The NERF-FIRST penalty band; clears to 100C at Novice.</summary>
    public const string WindowUntrained = "windowUntrained";
    /// <summary>Degrees SUBTRACTED from the threshold at Grandmaster: a master's glass tolerates
    /// down to ~80C (a wider window), never immune (annealing is never optional). The felt GM
    /// signature — window-mastery IS the Glassmaker's Mark.</summary>
    public const string WindowGm = "windowGm";

    /// <summary>Provenance tiers (a mark means something from Journeyman up): the shared GLA
    /// glaBy tag. Blown by (J) -> Master-blown by (M) -> a flawless-work line (GM).</summary>
    public const int ProvJourneyman = 9, ProvMaster = 13, ProvGm = 17;

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3, // locked default; the melt -> shape -> anneal pipeline fills a breadth day
        // Fire-craft + fire-brick-apparatus neighbourhood.
        Adjacency = new List<string> { "MET", "POT" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Per-session: one melt batch banks its share (contextHash keys on the smeltery + a bucket).
            [TechMelting] = new() { Raw = 3, K = 12 },
            // The staple shaping verbs: medium K.
            [TechBlowing] = new() { Raw = 3, K = 20 },
            [TechCasting] = new() { Raw = 3, K = 20 },
            [TechWorkbench] = new() { Raw = 2, K = 20 },
            // Per-batch: one annealer load banks (contextHash keys on the annealer + a bucket).
            [TechAnnealing] = new() { Raw = 2, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            // The thermal window (degrees off the 100C deadline), playtest-tuned.
            [WindowUntrained] = 20.0, // +20 -> shatters at 120C (narrower)
            [WindowGm] = 20.0,        // -20 at max -> tolerates to 80C (wider)
        },
    };

    /// <summary>The Apprentice-and-up reward curve (0 through Novice, linear to 1.0 at max).</summary>
    public static double BonusT(int level)
    {
        const int start = 5;
        if (level < start) return 0;
        int max = Leveling.Domain.MaxLevelDefault;
        return (level - start) / (double)(max - start);
    }

    /// <summary>The shatter threshold for a piece stamped by a maker of the given tier (frozen at
    /// the annealer): 120C Untrained (the penalty), 100C Novice (vanilla, the snap), down to 80C
    /// at GM (never immune). ShouldShatter returns temp &lt; this.</summary>
    public static float ShatterThreshold(int tier)
    {
        const double baseTemp = 100.0;
        if (tier <= 0) return (float)(baseTemp + Knob(WindowUntrained, 20.0));
        int max = Leveling.Domain.MaxLevelDefault;
        double frac = (tier - 1) / (double)(max - 1); // 0 at Novice I, 1 at max
        return (float)(baseTemp - frac * Knob(WindowGm, 20.0));
    }

    /// <summary>Server-side GLA level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.Instance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>A Bonus knob, falling back to the shipped default if the server dropped it.</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.Instance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }
}

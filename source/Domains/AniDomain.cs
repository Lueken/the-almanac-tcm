using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// ANI — "Animal Handling" defaults (rank-bonus-design.md §ANI, RULED 2026-07-10 with the
/// ani-line verification study adopted 8/8; technique-maps §ANI ruled 2026-07-08). The
/// lineage half of the farm-to-table trio: it makes wild animals tame and breeds them up.
///
/// A genuinely thin, deep domain — two verbs, m=2 (the small-m clamp, BRE/PAN precedent):
///   • Gen-raising (#1): the unattended BIRTH, one generation higher than the dam. No IPlayer
///     is in scope, so it reads the shared `raisedBy` owner stamp that FAR's trough feed writes
///     onto the eating animal (TCM writes it itself — the recycled-xLib attribution). Raw scaled
///     by the offspring's generation (capped): climbing to high-gen stock is the skill (ruled Q3).
///   • Taming (#2): one verb, two completion hooks (casting-merge precedent) — the petai
///     feed-to-domesticate transition and the vanilla saddle-break convert. Banked at the
///     WILD/feral -> TAME transition only; partial progress banks nothing.
///
/// Phase 2+: genetics (inbreeding-clean lines), litter depth, reduced saddle self-injury, and
/// the genetics-founded Master's Line GM signature. Note the affinity GM door is butcher +1
/// rancher alone (the ruled override for a domain with zero positive affinity cells).
/// </summary>
public static class AniDomain
{
    public const string Code = "ANI";

    /// <summary>The gen-raise birth (vanilla EntityBehaviorMultiply.GiveBirth; genelib overrides
    /// it). Unattended — attributed via the FAR trough `raisedBy` stamp on the dam.</summary>
    public const string TechGenRaising = "genraising";
    /// <summary>Domestication: petai feed-to-tame OR vanilla saddle-break, one verb two hooks.</summary>
    public const string TechTaming = "taming";

    /// <summary>The shared owner stamp FAR's trough feed writes on an animal and ANI's birth
    /// reads. A durable WatchedAttributes string (survives to the unattended birth), copied to
    /// the newborn so a bred line carries its raiser. Named to avoid clashing with petai OwnerId
    /// and vanilla ownedby (those are ownership; this is who does the husbandry work).</summary>
    public const string RaisedByAttr = "almanacRaisedBy";

    // Bonus knob keys.
    /// <summary>Gen-raise raw multiplier per generation of the newborn, capped (ruled Q3: quality
    /// of practice over bulk, mirroring MIN depth/rarity). rawMult = min(cap, 1 + step*(gen-1)).</summary>
    public const string GenRaiseStep = "genRaiseStep";
    public const string GenRaiseCap = "genRaiseCap";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 2, // small-m clamp: 2 rare, late techniques — depth over breadth is the honest shape
        Adjacency = new List<string> { "HUN", "FAR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Both rare and late; small K (one birth / one domestication ~ banked). The gen-raise
            // raw is scaled up at the call site by the newborn's generation (ruled Q3).
            [TechGenRaising] = new() { Raw = 8, K = 12 },
            [TechTaming] = new() { Raw = 8, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            [GenRaiseStep] = 0.35, // +35% raw per generation above the first...
            [GenRaiseCap] = 3.0,   // ...capped at 3x so a high-gen line still tapers.
        },
    };

    /// <summary>The generation-scaled raw multiplier for a birth: 1.0 for a gen-1 newborn, rising
    /// by GenRaiseStep per generation to the GenRaiseCap ceiling. Climbing lineages is the skill.</summary>
    public static double GenRaiseMult(int generation)
    {
        double step = Knob(GenRaiseStep, 0.35), cap = Knob(GenRaiseCap, 3.0);
        return System.Math.Min(cap, 1.0 + step * System.Math.Max(0, generation - 1));
    }

    /// <summary>Server-side ANI level for a player (0 = Untrained when unknown).</summary>
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

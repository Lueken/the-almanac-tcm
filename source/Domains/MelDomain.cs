using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// MEL — "Melee" defaults (rank-bonus-design.md §MEL + CO layer, RULED 2026-07-11;
/// technique-maps §MEL ruled 2026-07-08; parity ruled 2026-07-20: NOVICE anchor kept —
/// vanilla parry timing is genuinely hard, unlike RAN's comfortable vanilla aim).
///
/// Phase 1 (this build): the two verbs. Fighting = the swing that kills, classified at the
/// shared combat death hook (SourceEntity == killer; the branch has been dark in
/// MelRanKillPatches since 0.3.112 and goes live with this registration — all fences,
/// difficulty scaling, and bleed attribution inherited). Blocking = a successful absorb of a
/// hostile blow: under CO, the block/parry event (MeleeBlockSystemServer.EmitDamageBlocked);
/// vanilla floor, the shield absorb postfix. Fenced to hostile aggressors with the context
/// hash on the attacker, so tanking one caged mob banks nothing.
/// Phase 2: penalty band + Master-at-Arms stats. Phase 3: the timed-parry window + defensive
/// block tier. Phase 4: the Duelist's Eye.
///
/// NERF-FIRST bites hardest here (Thalius's warning was literally combat power creep):
/// meleeWeaponsDamage appears ONLY as an Untrained dock, and no bonus-band rung ever adds
/// damage, swing speed, or an offensive tier.
/// </summary>
public static class MelDomain
{
    public const string Code = "MEL";

    /// <summary>The swing verb: a melee kill (blade/spear/blunt are pool, ruled).</summary>
    public const string TechFighting = "fighting";
    /// <summary>The defensive verb (ADOPTED by ruling: "this will help promote the use of
    /// it"): a successful block or parry of a hostile blow.</summary>
    public const string TechBlocking = "blocking";

    // Bonus knob keys (DomainConfig.Bonus).
    /// <summary>A caught PARRY banks more than a passive block: the timed catch is the
    /// skill act (quality-of-practice, the kill-difficulty logic on the defensive verb).</summary>
    public const string RawParryMul = "rawParryMul";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 2,
        // The combat pair: the swing and the shot share one adjacency row.
        Adjacency = new List<string> { "RAN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Per kill, a whole encounter per event (the RAN shooting shape).
            [TechFighting] = new() { Raw = 4, K = 30 },
            // Per absorbed blow; small K per the breadth ruling (one fight banks most of it),
            // dedup keyed on the attacker so a caged mob collapses to one context.
            [TechBlocking] = new() { Raw = 2, K = 15 },
        },
        Bonus = new Dictionary<string, double>
        {
            [RawParryMul] = 1.5,
        }
    };

    /// <summary>Server-side MEL level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.Instance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>Client-side MEL level from the synced domain state (0 when unknown); the
    /// Duelist's Eye (Phase 4) reads it.</summary>
    public static int ClientLevel()
    {
        var core = AlmanacTcmModSystem.Instance;
        var dom = core?.Template?.FindDomain(Code);
        if (dom == null || core?.Client == null) return 0;
        return core.Client.Domains.TryGetValue(dom.Id, out var st) ? st.Level : 0;
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

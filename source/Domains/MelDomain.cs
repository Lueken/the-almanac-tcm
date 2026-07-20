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

    // Phase 2 curve knobs. ALL MEL curves anchor vanilla at NOVICE I (ruled 2026-07-20 —
    // vanilla parry timing is genuinely hard, so parity is not itself an earned rank the way
    // RAN's comfortable aim was). Untrained is the only sub-vanilla band.
    /// <summary>meleeWeaponsDamage at Untrained — the ONLY appearance of the damage lever in
    /// all of MEL, penalty-only (NERF-FIRST: it never climbs above 1.0). Clears at Novice I.</summary>
    public const string DamageUntrained = "damageUntrained";
    /// <summary>Master-at-Arms: the armor-affectedness DELTA at Untrained (positive = the
    /// beginner wears armor clumsily, more drag than vanilla) and at GM (negative = the veteran
    /// sheds the drag toward the unarmored baseline, never past it). Applied to
    /// armorWalkSpeedAffectedness and, under CO, armorManipulation/HungerRateAffectedness.</summary>
    public const string ArmorUntrained = "armorUntrained";
    public const string ArmorGm = "armorGm";

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
            [DamageUntrained] = 0.85,
            [ArmorUntrained] = 0.30,
            [ArmorGm] = -0.50,
        }
    };

    /// <summary>The Novice-anchored FACTOR curve: untrained at level 0, exactly 1.0 from
    /// Novice I (level 1) on, then linear to gm at max. For a penalty-only lever gm stays 1.0.</summary>
    public static double NoviceFactor(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        return 1.0 + (gm - 1.0) * (level - 1) / (double)(max - 1);
    }

    /// <summary>The Novice-anchored DELTA curve: untrained delta at level 0, exactly 0 at
    /// Novice I, then linear to the gm delta at max. Used for the armor-affectedness eases,
    /// which are additive contributions (not factors) stacking on CO's class traits.</summary>
    public static double NoviceDelta(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        return gm * (level - 1) / (double)(max - 1);
    }

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

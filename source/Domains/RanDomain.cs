using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// RAN — "The Ranged" defaults (rank-bonus-design.md §RAN + CO-suite layer, RULED 2026-07-11;
/// technique-maps §RAN ruled 2026-07-08). Server-config seeds; playtest tunes.
///
/// Phase 1 (this build): the single verb — shooting, kill-gated (a loose that kills, never the
/// loose itself), classified at the shared combat death hook by damage-source shape (the
/// projectile pattern holds across vanilla ItemBow/sling/spear AND Combat Overhaul's own
/// ProjectileEntity: killer = GetCauseEntity(), RAN when SourceEntity is a separate projectile
/// entity). PvP kills and owned/domesticated/gen2+ livestock bank nothing (ruled predicates).
/// Phase 2: the rank levers — steadyAim/reloadSpeed anchored vanilla at APPRENTICE I (ruled
/// 2026-07-18: vanilla aim is already comfortable; Novice parity would make GM a laser), arrow
/// recovery riding each arrow's own material break chance. Phase 3: firearms misfire + powder
/// thrift. Phase 4: the Marksman's Eye.
/// </summary>
public static class RanDomain
{
    public const string Code = "RAN";

    /// <summary>The one verb (m=1, ruled): draw-aim-loose terminating in a kill. Bow, sling,
    /// thrown spear, crossbow, and firearm are all pool — one loose loop, difficulty priced
    /// through the raw multiplier, never breadth.</summary>
    public const string TechShooting = "shooting";

    // Bonus knob keys (DomainConfig.Bonus).
    /// <summary>Extra raw per drifter tier step above surface (deep, tainted, corrupt,
    /// nightmare, double-headed) — the xSkills xpByType shape, ours to tune.</summary>
    public const string RawDrifterTierStep = "rawDrifterTierStep";
    /// <summary>Locust kills are cheap practice (swarm chaff).</summary>
    public const string RawLocustMul = "rawLocustMul";
    /// <summary>Resonating bells guard the swarm; downing one is real practice.</summary>
    public const string RawBellMul = "rawBellMul";

    // Phase 2 curve knobs. ALL RAN curves anchor vanilla (1.0) at APPRENTICE I, not Novice
    // (ruled 2026-07-18: vanilla aim is already comfortable, so parity is itself an earned
    // rank; Novice parity would make GM a laser). Untrained is the deep dock, the penalty
    // fades across Novice, and the above-vanilla band runs Apprentice I -> GM, modest.
    /// <summary>CO aim steadiness (drift/twitch divide by steadyAim squared, engine-clamped
    /// so sway never vanishes). Untrained shakes; GM is steady, never still.</summary>
    public const string SteadyAimUntrained = "steadyAimUntrained";
    public const string SteadyAimGm = "steadyAimGm";
    /// <summary>Nock/draw/reload cadence (per-stack CO reloadSpeed; vanilla floor
    /// rangedWeaponsSpeed). Ranged-only by construction. GM ~low-teens % (ruled cap).</summary>
    public const string ReloadUntrained = "reloadUntrained";
    public const string ReloadGm = "reloadGm";
    /// <summary>Ammo recovery: multiplies each projectile's OWN material drop chance (a
    /// flint arrow still breaks more than steel at every rank), absolute-capped below
    /// certainty — some arrows always shatter.</summary>
    public const string RecoveryUntrained = "recoveryUntrained";
    public const string RecoveryGm = "recoveryGm";
    public const string RecoveryCap = "recoveryCap";
    /// <summary>Vanilla-floor accuracy stats when CO is absent (kept conservative: the
    /// rangedWeaponsAcc -> aimingAccuracy read site is client-core, unverified in-assembly).</summary>
    public const string VanAccUntrained = "vanAccUntrained";
    public const string VanAccGm = "vanAccGm";
    public const string VanDrawGm = "vanDrawGm";

    // Phase 3 firearms knobs (dependsOn firearmsfork). The misfire is an INTRODUCED failure
    // (FA guns fire deterministically, stated plainly per the penalty-band pattern), ruled
    // KEPT even at GM as a never-zero floor: period and modern firearms misfire on bad luck.
    /// <summary>Flash-in-the-pan chance at level 0 (the Untrained fumble, the felt half).</summary>
    public const string MisfireUntrained = "misfireUntrained";
    /// <summary>Misfire at Apprentice I, the curve's knee: the worst is over by parity.</summary>
    public const string MisfireApprentice = "misfireApprentice";
    /// <summary>The GM floor, never zero (ruled 2026-07-11).</summary>
    public const string MisfireGm = "misfireGm";
    /// <summary>Powder/wadding thrift: chance a reload spends nothing. Zero through
    /// Apprentice I (nothing above vanilla below parity), climbing to this GM cap,
    /// capped well below certainty.</summary>
    public const string ThriftGm = "thriftGm";
    /// <summary>The penalty half of the powder economy (ruled 2026-07-20): chance an
    /// Untrained reload spills an extra powder unit (the clumsy overpour), fading to
    /// zero at Apprentice I. The felt story is the beginner wasting, not just the
    /// master saving.</summary>
    public const string SpillUntrained = "spillUntrained";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 1,
        // The combat pair plus the wild-country neighbour: the shot is RAN, the harvest is HUN.
        Adjacency = new List<string> { "MEL", "HUN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Per kill; a kill is a whole encounter so the per-event share is real (medium K).
            // Dedup keys on target type + coarse area, so a spawner camp collapses to a few
            // contexts inside the window while a roaming hunt banks each new ground.
            [TechShooting] = new() { Raw = 4, K = 30 },
        },
        Bonus = new Dictionary<string, double>
        {
            [RawDrifterTierStep] = 0.5,
            [RawLocustMul] = 0.75,
            [RawBellMul] = 2.0,
            // 0.50 squared hits CO's 0.25 clamp floor: a full 4x drift/twitch for the
            // Untrained hand (ruled 2026-07-19: 0.80 was barely felt — CO's per-weapon base
            // sway is small, so the dock has to be deep before the multiplier shows).
            // NOTE: under CO the steadyAim curve runs CLIENT-side off these compile defaults
            // (the client cannot read RAN.json); these two knobs govern only the vanilla
            // floor until a knob-sync ships.
            [SteadyAimUntrained] = 0.50,
            [SteadyAimGm] = 1.35,
            [ReloadUntrained] = 0.75,
            [ReloadGm] = 1.12,
            [RecoveryUntrained] = 0.80,
            [RecoveryGm] = 1.50,
            [RecoveryCap] = 0.90,
            [VanAccUntrained] = 0.90,
            [VanAccGm] = 1.05,
            [VanDrawGm] = 1.05,
            [MisfireUntrained] = 0.08,
            [MisfireApprentice] = 0.03,
            [MisfireGm] = 0.01,
            [ThriftGm] = 0.25,
            [SpillUntrained] = 0.25,
        }
    };

    /// <summary>The misfire curve: the Untrained fumble falls fast across Novice to the
    /// Apprentice-I knee, then eases slowly to the GM floor. Never zero at any rank.</summary>
    public static double MisfireChance(int level)
    {
        double u = Knob(MisfireUntrained, 0.08);
        double a = Knob(MisfireApprentice, 0.03);
        double g = Knob(MisfireGm, 0.01);
        const int anchor = 5;
        int max = Leveling.Domain.MaxLevelDefault;
        if (level <= 0) return u;
        if (level < anchor) return u + (a - u) * level / anchor;
        return a + (g - a) * (level - anchor) / (double)(max - anchor);
    }

    /// <summary>The thrift curve: zero through Apprentice I, linear to the GM cap.</summary>
    public static double ThriftChance(int level)
    {
        const int anchor = 5;
        int max = Leveling.Domain.MaxLevelDefault;
        if (level <= anchor) return 0;
        return Knob(ThriftGm, 0.25) * (level - anchor) / (double)(max - anchor);
    }

    /// <summary>The spill curve, thrift's mirror: full at Untrained, fading linearly to
    /// zero at Apprentice I. The two bands never overlap.</summary>
    public static double SpillChance(int level)
    {
        const int anchor = 5;
        if (level >= anchor) return 0;
        double u = Knob(SpillUntrained, 0.25);
        return level <= 0 ? u : u * (1.0 - level / (double)anchor);
    }

    /// <summary>The RAN curve (ruled 2026-07-18): untrained at level 0, penalty fading
    /// linearly across Novice, exactly 1.0 at APPRENTICE I (level 5), then linear to the
    /// GM value at max level. Contrast HunDomain.RankLinear's Novice anchor.</summary>
    public static double ApprenticeAnchored(int level, double untrained, double gm)
    {
        const int anchor = 5; // Apprentice I
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        if (level < anchor) return untrained + (1.0 - untrained) * level / anchor;
        return 1.0 + (gm - 1.0) * (level - anchor) / (double)(max - anchor);
    }

    /// <summary>Server-side RAN level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>Client-side RAN level from the synced domain state (0 when unknown). The
    /// steadyAim write runs on the client — CO registers and reads the stat there, and its
    /// register call wipes anything the server synced in earlier (the 0.3.113 lesson).</summary>
    public static int ClientLevel()
    {
        var core = AlmanacTcmModSystem.ClientInstance;
        var dom = core?.Template?.FindDomain(Code);
        if (dom == null || core?.Client == null) return 0;
        return core.Client.Domains.TryGetValue(dom.Id, out var st) ? st.Level : 0;
    }

    /// <summary>A Bonus knob, falling back to the shipped default if the server dropped it.</summary>
    public static double Knob(string key, double fallback)
    {
        var configs = AlmanacTcmModSystem.ServerInstance?.Ledger?.DomainConfigs;
        if (configs != null && configs.TryGetValue(Code, out var dc)
            && dc.Bonus.TryGetValue(key, out double v)) return v;
        return fallback;
    }
}

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

    // Bonus knob keys (DomainConfig.Bonus) — Phase 1 ships the difficulty-scaling knobs; the
    // Phase 2 stat-curve knobs join them when the levers build.
    /// <summary>Extra raw per drifter tier step above surface (deep, tainted, corrupt,
    /// nightmare, double-headed) — the xSkills xpByType shape, ours to tune.</summary>
    public const string RawDrifterTierStep = "rawDrifterTierStep";
    /// <summary>Locust kills are cheap practice (swarm chaff).</summary>
    public const string RawLocustMul = "rawLocustMul";
    /// <summary>Resonating bells guard the swarm; downing one is real practice.</summary>
    public const string RawBellMul = "rawBellMul";

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
        }
    };

    /// <summary>Server-side RAN level for a player (0 = Untrained when unknown).</summary>
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

using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// HUN — "The Master Hunter" defaults (rank-bonus-design.md §HUN, RULED 2026-07-10 with
/// hun-tracking-study adopted 7/7; technique-maps §HUN). Server-config seeds; playtest tunes.
///
/// Phase 1 (this build): the four verbs (hunting = player kills of wild game, dressing = the
/// field harvest, trapping = PS snares/deadfalls owner-at-placement, butchery = the Butchering
/// stations, by-target ruling), the two verified vanilla stat anchors (animalLootDropRate
/// yield, animalSeekingRange stealth/spook), and the PER-SPECIES KILL LEDGER recorded from day
/// one so the Phase 3 Hunter's Map knowledge gate has history the moment it ships.
/// Phase 2: the Tracker's Eye + blood-read. Phase 3: the Hunter's Map (habitat layer).
/// </summary>
public static class HunDomain
{
    public const string Code = "HUN";

    public const string TechHunting = "hunting";   // a wild kill, credited to the causing player
    public const string TechDressing = "dressing"; // the field harvest at the carcass
    public const string TechTrapping = "trapping"; // PS snare/deadfall (owner-at-placement)
    public const string TechButchery = "butchery"; // Butchering stations (by-target ruling)
    /// <summary>Leather-making — the sealed barrel tanning chain (soak -> prepare -> tan -> dye).
    /// Folded into HUN 2026-07-22 (the leatherworking-domain question): the crafts that USE leather
    /// are grid crafts that grant nothing, so tanning is the only earnable leather verb, and it is
    /// the natural end of HUN's carcass chain (kill -> skin -> butcher -> tan). Rides the shared
    /// barrel-seal hook, classified in BrePatches. May re-home when TAI is built.</summary>
    public const string TechTanning = "tanning";

    // Bonus knob keys (DomainConfig.Bonus).
    /// <summary>Axis 1 + 4: the vanilla per-player harvest yield stat. Untrained wastes hide
    /// and meat to clumsy dressing; GM ~x1.15 rolls the fraction as a bonus-cut chance.</summary>
    public const string AnimalYieldUntrained = "animalYieldUntrained";
    public const string AnimalYieldGm = "animalYieldGm";
    /// <summary>The Stalker curve: the vanilla stat animal AI reads to decide how far away it
    /// notices you. Untrained above 1.0 (game flees the beginner from further out); GM floored
    /// ABOVE zero (ruled: no invisible hunter).</summary>
    public const string SeekRangeUntrained = "seekRangeUntrained";
    public const string SeekRangeGm = "seekRangeGm";

    // ---- Trap axes (rank-bonus §HUN Axes 1/3/4; REDESIGNED 2026-08-21 at the real seam: PS
    // land traps hold no catch item, the catch is the animal dying beside the trap, so the
    // levers ride the collide rolls and the kill, not a collection hook that never existed).
    /// <summary>Multiplier on BOTH failure rolls (bait stolen, tripped empty) at Untrained: a
    /// green hand's set fails more often than vanilla (Axis 1's botched traps).</summary>
    public const string TrapFailUntrained = "trapFailUntrained";
    /// <summary>Failure-roll multiplier at Grandmaster: the floor, deliberately above zero.
    /// No trap is ever a sure thing.</summary>
    public const string TrapFailGm = "trapFailGm";
    /// <summary>Grandmaster chance a trap STAYS SET after a successful kill, bait kept, the
    /// line still working (Axis 4's trapline yield, reinterpreted 2026-08-21: no catch item
    /// exists to bonus, so the bonus is the next catch coming sooner). 0 through Novice.</summary>
    public const string TrapStaySetGm = "trapStaySetGm";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 3,
        // The wild-country pairing: the forager shares the ground, the handler shares the beasts.
        Adjacency = new List<string> { "FOR", "ANI" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The marquee verb; species+second bucketed so a pack kill credits once.
            [TechHunting] = new() { Raw = 4, K = 30 },
            // Per-carcass; the dedup window absorbs double-fires, K caps the day.
            [TechDressing] = new() { Raw = 2, K = 30 },
            // Per-session trapline shape, small K (one sweep is most of the bank).
            [TechTrapping] = new() { Raw = 4, K = 15 },
            // Station work: low raw, batch processing dedups inside the ledger window.
            [TechButchery] = new() { Raw = 1, K = 20 },
            // Tanning: a multi-day sealed barrel chain, each stage a seal; modest K, deduped by
            // the tanning output so the soak/prepare/tan stages bank without farming.
            [TechTanning] = new() { Raw = 2, K = 15 },
        },
        Bonus = new Dictionary<string, double>
        {
            // Ruled 2026-07-17: Untrained widened to 0.70 (from 0.9) — Butchering's own drop
            // formula stacks stationTier (0.8-1.2) x animalWeight x THIS stat, so a green hand
            // needs a real penalty to feel it against a weight-inflated haul. GM stays a modest
            // 1.15 so the multiplicative ceiling (advanced table x heavy carcass x rank) stays
            // sane. Same normal hook: Untrained ~3-4 pelts, GM ~6.
            [AnimalYieldUntrained] = 0.70,
            [AnimalYieldGm] = 1.15,
            [SeekRangeUntrained] = 1.15,
            [SeekRangeGm] = 0.75,
            // Trap axes: at PS defaults (10% stolen, 10% tripped-empty per impact) Untrained
            // fails ~27% of impacts, GM ~11%, never zero. Stay-set: 1 in 4 GM kills keep the
            // trap armed and baited.
            [TrapFailUntrained] = 1.35,
            [TrapFailGm] = 0.55,
            [TrapStaySetGm] = 0.25,
        }
    };

    /// <summary>General rank curve: untrained value at level 0, exactly 1.0 at Novice I,
    /// linear to the GM value at max level (shared shape with the other domains).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        double t = (level - 1) / (double)(max - 1);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Chance (0..1) a trap survives its kill still set and baited: 0 through Novice,
    /// linear to the <see cref="TrapStaySetGm"/> knob at max level (the trapline proc).</summary>
    public static double TrapStaySetChance(int level)
    {
        int novice = Leveling.Domain.SubLevelsPerTier;
        if (level <= novice) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault;
        return (level - novice) / (double)(max - novice) * Knob(TrapStaySetGm, 0.25);
    }

    /// <summary>Server-side HUN level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
    }

    /// <summary>Client-side HUN level from the synced domain state (0 when unknown). The
    /// Tracker's Eye and blood vibrancy run on the client, which never sees server rank state
    /// directly — only the level packets in LevelingClient.</summary>
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

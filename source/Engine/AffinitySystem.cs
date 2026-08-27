using System.Collections.Generic;
using AlmanacTcm.Leveling;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace AlmanacTcm.Engine;

/// <summary>
/// The class-affinity layer (vocation-affinity-map.md Grid 2, RULED). Scores are
/// server config; the score→band translation is design law and lives in code:
///   +3 Apprentice-I start, +20% Smax
///   +2/+1 Novice-I start, +13%/+7%
///    0 no start bonus, no Smax change
///   −1 −10% Smax, ceiling Master I · −2 −20% Smax, ceiling Journeyman IV
/// PRACTICE STOPS AT MASTER IV FOR EVERYONE (ruled 2026-08-19). Grandmaster is a
/// DECLARED ascension: a trader commission, a surrendered masterwork, a goods turn-in.
/// Nobody grinds into it, not even the class born to the trade. The three positive
/// bands carried Rank.Grandmaster as their practice ceiling until that ruling, which
/// let a favoured class level straight past the deed with no commission and no cap
/// while neutral classes correctly stopped. Positive affinity buys a head start and a
/// faster Smax, never the door itself; negative affinity is walled lower still and
/// never reaches the gate at all.
/// A ceiling only ever discards incoming XP (LedgerSystem.ClampToCeiling): an attained
/// rank is never revoked, so anyone already at Grandmaster keeps it.
/// Keys on the characterClass attribute, so vanilla and SC versions of the same
/// class code (malefactor!) get identical treatment, and SC needs no detection at all.
/// </summary>
public class AffinitySystem
{
    public class AffinityConfig
    {
        /// <summary>class code → domain code → score (−2 … +3). Unlisted = 0.</summary>
        public Dictionary<string, Dictionary<string, int>> Classes { get; set; } = new();
    }

    public readonly struct Band
    {
        public readonly int StartLevel;
        public readonly int CeilingLevel;
        public readonly double SmaxScale;

        public Band(int start, int ceiling, double smax)
        {
            StartLevel = start; CeilingLevel = ceiling; SmaxScale = smax;
        }
    }

    // Six hand re-derivations from SubLevelsPerTier deleted 2026-08-12; use Leveling/Rank.cs.

    public static Band BandFor(int score) => score switch
    {
        >= 3 => new Band(Rank.Apprentice, Rank.MasterIV, 1.20),
        2 => new Band(Rank.Novice, Rank.MasterIV, 1.13),
        1 => new Band(Rank.Novice, Rank.MasterIV, 1.07),
        -1 => new Band(0, Rank.Master, 0.90),
        <= -2 => new Band(0, Rank.JourneymanIV, 0.80),
        _ => new Band(0, Rank.MasterIV, 1.00),
    };

    private readonly ICoreServerAPI sapi;
    private readonly LevelingServer leveling;
    private AffinityConfig config = new();

    public AffinitySystem(ICoreServerAPI sapi, LevelingServer leveling, LedgerSystem ledger)
    {
        this.sapi = sapi;
        this.leveling = leveling;
        LoadConfig();

        leveling.DomainSetReady += OnDomainSetReady;
        ledger.ClassCeilingProvider = (player, domain) => ResolveBand(player, domain.Code).CeilingLevel;
        ledger.SmaxScaleProvider = (player, domain) => ResolveBand(player, domain.Code).SmaxScale;
    }

    private void OnDomainSetReady(IServerPlayer player, PlayerDomainSet domainSet)
    {
        ApplyStartLevels(player, domainSet);
        SyncAffinities(player, domainSet);

        // Mid-session class changes (charsel) must re-apply starts AND re-sync bands
        // live, not wait for the next relog. Entity is fresh each session, so one
        // listener per join.
        player.Entity.WatchedAttributes.RegisterModifiedListener("characterClass", () =>
        {
            ApplyStartLevels(player, domainSet);
            SyncAffinities(player, domainSet);
        });
    }

    /// <summary>Sends every enabled domain's resolved band to the client, for the
    /// per-domain "why you started here" line. State, not the grid.</summary>
    private void SyncAffinities(IServerPlayer player, PlayerDomainSet domainSet)
    {
        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            if (!playerDomain.Domain.Enabled) continue;
            leveling.SyncAffinity(player, playerDomain.Domain.Id, ScoreFor(player, playerDomain.Domain.Code));
        }
    }

    public int ScoreFor(IPlayer player, string domainCode)
    {
        string? classCode = player.Entity?.WatchedAttributes.GetString("characterClass");
        if (classCode == null) return 0;
        if (config.Classes.TryGetValue(classCode, out var domains)
            && domains.TryGetValue(domainCode, out int score))
        {
            return score;
        }
        return 0;
    }

    public Band ResolveBand(IPlayer player, string domainCode) => BandFor(ScoreFor(player, domainCode));

    /// <summary>Affinity start tiers: raise-only, applied whenever the set loads,
    /// idempotent because a met start level raises nothing. A positive start also
    /// reveals the domain (the class grew up around this trade).</summary>
    private void ApplyStartLevels(IServerPlayer player, PlayerDomainSet domainSet)
    {
        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            if (!playerDomain.Domain.Enabled) continue;
            Band band = ResolveBand(player, playerDomain.Domain.Code);
            if (band.StartLevel > playerDomain.Level)
            {
                playerDomain.Level = band.StartLevel;
                playerDomain.Hidden = false;
                leveling.SyncDomain(player, playerDomain);
                TcmLog.Cat(sapi, TcmLog.Affinity,
                    $"{player.PlayerName} {playerDomain.Domain.Code}: affinity start level {band.StartLevel}");
            }
        }
    }

    /// <summary>The one rule behind both the count and the names. There is deliberately NO
    /// Enabled check here: a Grandmaster earned in a domain whose supporting mod was later
    /// removed still spends one of the two lifetime slots (ruled 2026-08-20). Keeping the
    /// count and the name list on one predicate is what stops them disagreeing.</summary>
    private static bool IsGreatWork(PlayerDomain pd) => pd.Level >= Rank.Grandmaster;

    /// <summary>Guard #12 stub: lifetime GM count vs the configured cap. Real
    /// enforcement lives in the ascension flow (Track 2); lowering the cap never
    /// revokes attained GMs: this only gates future ascensions.</summary>
    public static int GmCount(PlayerDomainSet domainSet)
    {
        // was: int Rank.Grandmaster = 4 * Domain.SubLevelsPerTier + 1;  (2026-08-12 -> Rank.Grandmaster)
        int count = 0;
        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            if (IsGreatWork(playerDomain)) count++;
        }
        return count;
    }

    /// <summary>The domains BEHIND that count, in roster order, for display. Shares the
    /// predicate above, so a name list can never disagree with the number beside it.
    ///
    /// This exists because /tcm status listed domains through an Enabled and Hidden filter
    /// while the count above passes neither. A Grandmaster in a domain gone dormant (ARC
    /// without Rustbound Magic, say) therefore spent a lifetime slot while appearing nowhere
    /// on the page, and the reader had a number with nothing behind it.</summary>
    public static List<string> GreatWorkNames(PlayerDomainSet domainSet)
    {
        var names = new List<string>();
        foreach (PlayerDomain playerDomain in domainSet.PlayerDomains)
        {
            if (IsGreatWork(playerDomain)) names.Add(playerDomain.Domain.DisplayName);
        }
        return names;
    }

    public static bool CanAscend(PlayerDomainSet domainSet, int gmDomainCap)
        => GmCount(domainSet) < gmDomainCap;

    private void LoadConfig()
    {
        const string path = "almanactcm/affinity.json";
        try
        {
            config = sapi.LoadModConfig<AffinityConfig>(path) ?? DefaultGrid();
        }
        catch (System.Exception e)
        {
            TcmLog.Error(sapi, $"{path} unreadable ({e.Message}), using Grid 2 defaults, NOT overwriting");
            config = DefaultGrid();
            return;
        }
        sapi.StoreModConfig(config, path);
    }

    /// <summary>Grid 2 verbatim (vocation-affinity-map.md, all rulings applied
    /// through 2026-07-12). Commoner is deliberately absent: the zero baseline.</summary>
    private static AffinityConfig DefaultGrid() => new()
    {
        Classes = new Dictionary<string, Dictionary<string, int>>
        {
            ["archivist"] = new() { ["FAR"] = -2, ["TEM"] = 2, ["TAI"] = -1, ["ARC"] = 1 },
            ["blackguard"] = new() { ["FAR"] = -2, ["MET"] = 3, ["POT"] = 1, ["MEL"] = 2, ["RAN"] = 1, ["TAI"] = -1, ["FOR"] = -1, ["GLA"] = 1 },
            ["brickmaker"] = new() { ["WOO"] = 1, ["POT"] = 3, ["MEL"] = 1, ["MAS"] = 1, ["FOR"] = -1 },
            ["butcher"] = new() { ["FAR"] = -2, ["COO"] = 3, ["MEL"] = 1, ["RAN"] = -1, ["TAI"] = -1, ["ENG"] = -1, ["HUN"] = 3, ["ANI"] = 1 },
            ["clockmaker"] = new() { ["WOO"] = 1, ["FAR"] = -2, ["MET"] = 2, ["RAN"] = 1, ["TEM"] = 1, ["MAS"] = 1, ["ENG"] = 3 },
            ["farmhand"] = new() { ["MIN"] = -1, ["FAR"] = 3, ["FIS"] = 2, ["TEM"] = -1, ["FOR"] = 2 },
            ["florist"] = new() { ["MIN"] = -1, ["FAR"] = 1, ["MEL"] = -1, ["RAN"] = -1, ["TEM"] = -1, ["ALC"] = 2, ["ANI"] = 1, ["FOR"] = 2, ["BEE"] = 2 },  // BEE ruled 2026-08-08: the grid predated the domain; florist ANI+1 was always bee flavour
            ["forester"] = new() { ["MIN"] = -1, ["WOO"] = 3, ["MET"] = 1, ["MEL"] = 1, ["TEM"] = -1 },
            ["hunter"] = new() { ["MIN"] = -1, ["RAN"] = 3, ["TEM"] = -1, ["TAI"] = 1, ["HUN"] = 3 },
            ["malefactor"] = new() { ["FAR"] = -2, ["RAN"] = 1, ["ALC"] = 1, ["TAI"] = -1, ["ENG"] = 1 },
            ["messenger"] = new() { ["MAS"] = 1, ["HUN"] = -1, ["FOR"] = -1 },
            ["quarrier"] = new() { ["MIN"] = 3, ["TEM"] = 1, ["MAS"] = 2, ["FOR"] = -1 },
            ["spelunker"] = new() { ["MIN"] = 3, ["FAR"] = -2, ["MEL"] = 1, ["PAN"] = 3, ["TAI"] = -1, ["FOR"] = -1 },
            ["tailor"] = new() { ["MIN"] = -1, ["FAR"] = 1, ["TAI"] = 3, ["FOR"] = 2, ["TEM"] = -1 },
            ["vintner"] = new() { ["MIN"] = -1, ["FAR"] = 3, ["COO"] = 1, ["MEL"] = 1, ["RAN"] = 1, ["TEM"] = -1, ["ALC"] = 1, ["BRE"] = 3, ["FOR"] = 3 },
        }
    };
}

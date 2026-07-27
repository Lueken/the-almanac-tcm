using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// POT — "Pottery" defaults (rank-bonus-design.md §POT, RULED 2026-07-09 with the
/// pot-vessel-study adopted 7/7; technique-maps §POT ruled 2026-07-08). The other
/// half of the fire-craft pair with MET, and the maker of the keep-vessels the
/// farm-to-table trio fills.
///
/// Phase 1 (this build): the two ruled verbs, both [vanilla], both day-one/zero-tool
/// (POT has the best reachability in the whole map, which is exactly why it needs no
/// gate). Clayforming rides the vanilla `onitemclayformed` event bus (one listener,
/// no patch); the pottery-wheel variant is a conditional reduced-raw postfix. Pit
/// firing grants once per kiln burn, success-gated on `IsValidPitKiln` actually
/// converting (a rained-out or breached firing banks nothing), owner-at-ignite.
///
/// Phase 3 — the Potter's Mark (Axis 6, the domain's one axis with real depth): a
/// per-instance preservation quality stamped on fired keep-vessels by the firer's
/// rank. Untrained crocks seal imperfectly (x1.10 perish), a master's crock keeps
/// food (x0.85) — the exact container-side mirror of COO's food-side Cook's Mark,
/// riding the same perish chain from the other end. The Untrained x1.10 end IS POT's
/// NERF-FIRST penalty band, delivered on the vessel rather than as a fragile
/// firing-botch (Axis 1 [BUILD]). The two thin axes the ruling flags droppable — the
/// staged-fuel discount (Axis 2) and the rain-resist rung (Axis 3, which rides the
/// shared BEBehaviorBurning precipitation roll) — are deferred, tracked on the board.
///
/// Ruled boundary: no material/rank gate (foundational, day-one); storage size stays
/// out; premium-goods economy (vessels never wear out, so a masterwork crock is a
/// one-time heirloom, not a repair loop).
/// </summary>
public static class PotDomain
{
    public const string Code = "POT";

    // The 2 ruled verbs. The wheel is a variant of #1 (reduced raw), not a third verb.
    public const string TechClayforming = "clayforming"; // BlockEntityClayForm -> onitemclayformed
    public const string TechFiring = "firing";           // BlockEntityPitKiln.OnFired (success-gated)

    // ---- The Potter's Mark (Axis 6, GM signature, RULED 2026-07-09). Per-instance
    // preservation quality on fired keep-vessels + tiered provenance.
    /// <summary>Perish factor a keep-vessel carries at the Untrained end (a clumsy crock seals
    /// imperfectly): >1 = worse than vanilla. Clears to 1.0 at Novice. This is POT's penalty band.</summary>
    public const string PreserveUntrained = "preserveUntrained";
    /// <summary>Perish factor at Grandmaster (a masterwork crock keeps food): a masterwork sealed
    /// meal goes from vanilla x0.10 to x0.10*this. Modest per NERF-FIRST.</summary>
    public const string PreserveGm = "preserveGm";

    /// <summary>Provenance tiers (a mark means something from Journeyman up): the shared POT
    /// potterBy tag. Thrown by (J) -> Master-potted by (M) -> Masterwork (GM).</summary>
    public const int ProvJourneyman = 9, ProvMaster = 13, ProvGm = 17;

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 2, // small-m: techniqueCount, per the locked small-m rule (form -> fire pipeline)
        // Fire-craft + vessel neighbourhood.
        Adjacency = new List<string> { "MET", "COO", "MAS" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // The staple per-piece verb: modest raw, K large enough that a pottery day banks steadily.
            // The wheel path co-grants this same row at a reduced raw (lower skill expression).
            [TechClayforming] = new() { Raw = 1, K = 30 },
            // Per-session: one kiln burn banks most of its share regardless of batch size (the
            // contextHash keys on the firing session, not per-piece, so a big load never farms).
            [TechFiring] = new() { Raw = 3, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            // The Potter's Mark preservation ladder (MET/COO numeric posture, playtest-tuned).
            [PreserveUntrained] = 1.10, [PreserveGm] = 0.85,
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

    /// <summary>General rank curve: untrained at level 0, exactly 1.0 at Novice I, linear to the
    /// GM value at max level (the shared domain shape; the preservation factor reads it).</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        return 1.0 + (level - 1) / (double)(max - 1) * (gm - 1.0);
    }

    /// <summary>The keep-vessel perish factor for a potter of the given tier (frozen at firing):
    /// x1.10 Untrained (the penalty), x1.0 Novice, down to x0.85 at GM. Multiplies the vessel's
    /// existing modifier, composing with sealing and any COO food-side stamp.</summary>
    public static double PreserveFactor(int tier)
        => RankLinear(tier, Knob(PreserveUntrained, 1.10), Knob(PreserveGm, 0.85));

    /// <summary>Server-side POT level for a player (0 = Untrained when unknown).</summary>
    public static int LevelOf(IPlayer? player)
    {
        if (player == null) return 0;
        var set = AlmanacTcmModSystem.ServerInstance?.Server?.GetDomainSet(player);
        return set?.FindDomain(Code)?.Level ?? 0;
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

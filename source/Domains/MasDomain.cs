using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// MAS — "Masonry" defaults (rank-bonus-design.md §MAS, fresh pass 2026-07-10; technique-maps §MAS
/// RULED FINAL 2026-07-08; CON dropped 2026-07-11 and absorbed here). MAS owns STONE whole — mortar,
/// dress, and carve — the stonecraft cousin of MET (metal) and POT (clay). Its identity rests on two
/// earned things: a real quarry-dress YIELD lever (Axis 4, a verified NatFloat drop roll — the mason
/// visibly wrings more building units from the same slab) and the MASON'S MARK (Axis 6, the historical
/// banker-mark, a durable author stamp on a carved block).
///
/// Three verbs: #1 staged mortared construction [medievalarchitecture] (frame -> rim -> mortar ->
/// complete, a real process with a completion hook), #2 stone dressing [stonequarry] (work a quarried
/// slab into blocks/bricks, or hammer rock down the aggregate chain), #3 chiseling [vanilla] (freeform
/// voxel carving — no completion event, so a tiny net-new-voxel grant). The two mod verbs vanish with
/// their mods (banked progress dormant); chiseling is the vanilla floor. `m` = 2 (clamps to available:
/// 1 with one MAS mod, hidden with none — but chiseling keeps a vanilla floor).
///
/// No efficiency lever (mortar quantity is recipe-fixed; stamina-on-hammer is pending upstream), no
/// reliability (mortaring is deterministic, dressing has no destruction roll), no material gate (any
/// mason dresses any stone; metal-gated by vanilla tools). Affinity (quarrier +2 / brickmaker +1 /
/// clockmaker +1, zero negatives) already lives in AffinitySystem. Surpass-only — xSkills has no
/// masonry skill, so MAS is a Copybook-original domain.
/// </summary>
public static class MasDomain
{
    public const string Code = "MAS";

    public const string TechMortar = "mortar";   // medievalarchitecture staged construction completion
    public const string TechDress = "dress";      // stonequarry slab dressing + rubble hammer
    public const string TechChisel = "chisel";    // vanilla BlockEntityChisel.SetVoxel (freeform carve)

    // ---- Axis 1 penalty + Axis 4 dress-yield knob keys (the one real numeric lever).
    /// <summary>Dress-yield multiplier at the Untrained end — below the slab's rated drop (fewer bricks
    /// off a wedge-slab, less stone off a rubble-hammer). MAS's one penalty tooth. Clears to 1.0 at Novice.</summary>
    public const string DressYieldUntrained = "dressYieldUntrained";
    /// <summary>Dress-yield multiplier at Grandmaster: a master wrings more building units from the same
    /// quarried slab (GM ~x1.15, never doubling). The MIN oreDropRate / FOR forageDropRate shape.</summary>
    public const string DressYieldGm = "dressYieldGm";

    /// <summary>Provenance tiers (the Mason's Mark shows from Journeyman up): the MAS carver tag on a
    /// chiseled block. Carved by (J) -> Dressed by Master (M) -> a work of Master Mason (GM). Level
    /// thresholds. Below Journeyman the carve still records its author (Jeffrey: know who carved it, and
    /// never let it be overridden), shown plainly.</summary>
    // Rank thresholds moved to Leveling/Rank.cs (2026-08-12): this was one of ten identical
    // `Rank.Journeyman = 9, Rank.Master = 13, Rank.Grandmaster = 17` triplets. Use Rank.Journeyman etc.

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // small-m: 2 with the full pack (mortar + dress, chisel is the vanilla floor); clamps to
        // available techniques (BRE/PAN/ANI/MEL precedent).
        M = 2,
        // Stone/build neighbourhood: mining (extraction), pottery (brick firing sibling), metal (tools).
        Adjacency = new List<string> { "MIN", "POT", "MET" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Per completed archway/structure (a multi-stage build): medium raw, real ceiling.
            [TechMortar] = new() { Raw = 3, K = 14 },
            // Per dressed slab / per rubble conversion: the recurring stonecraft, medium.
            [TechDress] = new() { Raw = 2, K = 18 },
            // Freeform voxel carving, no completion: tiny raw, high K (so babysitting one block dedups out).
            [TechChisel] = new() { Raw = 1, K = 24 },
        },
        Bonus = new Dictionary<string, double>
        {
            // The dress-yield lever (MIN oreDropRate posture, playtest-tuned).
            [DressYieldUntrained] = 0.85, [DressYieldGm] = 1.15,
        },
    };

    /// <summary>The dress-yield multiplier for a mason of the given level: <paramref name="level"/>-scaled
    /// on the verified stone-dressing drop roll. 0.85 Untrained (wasted stone, the penalty), 1.0 across
    /// Novice (vanilla), a gentle climb to 1.15 at Grandmaster. NERF-FIRST — a master makes more from the
    /// same slab, never a different block. The fractional part rolls as a chance of an extra unit.</summary>
    public static double DressYield(int level)
    {
        double untrained = Knob(DressYieldUntrained, 0.85), gm = Knob(DressYieldGm, 1.15);
        if (level <= 0) return untrained;
        int novice = Leveling.Domain.SubLevelsPerTier;       // 4
        if (level <= novice) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault;           // 20
        double t = (level - novice) / (double)(max - novice);
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Server-side MAS level for a player (0 = Untrained when unknown).</summary>
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

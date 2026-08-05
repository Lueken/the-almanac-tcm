using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// ARC — "Arcana" defaults (arc-design-study.md, RULED/CLOSED 2026-07-12; the annex §8 is the build
/// contract). The 21st, conditional domain (dependsOn rustboundmagic — live on The Quire). ARC does
/// something no other domain does: it FUNDAMENTALLY re-roots how Rustbound Magic plays. RBM normally
/// grows your max mana by a casting-XP grind; ARC FREEZES that XP and makes your mana pool a function of
/// ARC RANK instead (a per-rank floor), with robe gear and Meditation-Insight research stacking on top as
/// the earned-infrastructure layer. Magic capacity becomes an Almanac progression, not an RBM grind.
///
/// Eight verbs (Skyrim-shaped, §5): foundational casting (innate/general), the four SCHOOLS (evocation /
/// alteration / incantation / conjuration — grant at the single server-side ConsumeManaForSpell, which
/// no-ops for scroll casts so scroll-buyers never earn ARC), meditation (the practice trickle), the
/// laboratory (station ritual work), and inscription (scroll scribing, the market verb). `m` = 5 (the
/// attunement cores already force one-school-at-a-time, a native breadth limiter). NO Axis-6 signature —
/// the GM mark was deliberately SHELVED (Jeffrey); ARC's identity is the curve + gates + school depth +
/// meditation, not a Maker's-Mark. Affinity: archivist +1 only (the scholar), zero negatives, no main door
/// (nobody is born a mage; the rusty-dust initiation is open to all).
///
/// The mana-curve is RATIFIED (§2): utilities trivialize as the mage climbs (intended), but ultimates
/// carry weight even at GM — the GM floor (380) sits deliberately BELOW the 450 ultimates, so the endgame
/// conjure needs GM rank AND earned extras, and regen pacing still prices every big cast in minutes of
/// vulnerable, stability-draining recovery.
/// </summary>
public static class ArcDomain
{
    public const string Code = "ARC";

    public const string TechFoundational = "foundational";  // innate/general spells (no school key)
    public const string TechEvocation = "evocation";
    public const string TechAlteration = "alteration";
    public const string TechIncantation = "incantation";
    public const string TechConjuration = "conjuration";
    public const string TechMeditation = "meditation";       // the practice trickle (meditation-active_rm)
    public const string TechLaboratory = "laboratory";        // station rituals: Spellforge research, world rituals, oculus, foundry
    public const string TechInscription = "inscription";      // scroll scribing (the market verb)

    // RBM watched-attribute keys the re-root reads/writes (verified RBM 3.2.5).
    public const string AttrPlayerMaxMana = "entitybehavior-resource-playermaxmana_rm";       // the base pool (ARC re-roots this)
    public const string AttrTotalMaxMana = "entitybehavior-resource-totalmaxmana_rm";          // the effective pool (clamp target)
    public const string AttrCurrentMana = "entitybehavior-resource-currentmana_rm";
    public const string AttrXpToNextLevel = "entitybehavior-resource-currentexptonextmaxmanalevel_rm"; // frozen at 0
    public const string AttrResearchedMana = "entitybehavior-resource-researchedmaxmana_rm";   // research (stacks on top)
    public const string AttrArmorMana = "entitybehavior-resource-armormaxmana_rm";              // gear (stacks on top)
    public const string AttrMeditationActive = "entitybehavior-meditation-active_rm";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // m = 5 of 8 (attunement cores force one-school-at-a-time; the map respects, not duplicates, it).
        M = 5,
        // Temporal + alchemical + rift-mining kinship (the occult neighbourhood).
        Adjacency = new List<string> { "TEM", "ALC", "MIN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            [TechFoundational] = new() { Raw = 1, K = 30 },
            [TechEvocation] = new() { Raw = 2, K = 22 },
            [TechAlteration] = new() { Raw = 2, K = 22 },
            [TechIncantation] = new() { Raw = 2, K = 22 },
            [TechConjuration] = new() { Raw = 2, K = 22 },
            // The trance: Raw 1 is the UNIT the outcome-normalized multiplier scales (a full
            // empty->full trance pays MeditationTranceRaw = ~25 against this K=40).
            [TechMeditation] = new() { Raw = 1, K = 40 },
            // Chunky per station act (Spellforge research, a world ritual, an oculus/foundry product).
            [TechLaboratory] = new() { Raw = 4, K = 10 },
            [TechInscription] = new() { Raw = 3, K = 14 },
        },
        Bonus = new Dictionary<string, double>
        {
            // Stage 2a: meditation regen ladder + school-cost discount + backfire GM residual.
            [RegenGm] = 3.0,           // +mana/regen-tick at GM (Novice +0); dialed 4->3 (felt too generous)
            [DrainGm] = 0.6,            // temporal-stability drain x0.6 at GM (base 0.05/s -> ~0.03/s)
            [SchoolDiscountGm] = 0.15,  // -15% school mana cost at GM (floor-1 protected by RBM)
            [BackfireGmResidual] = 0.025,  // ~2.5% residual on the 450 ultimates even at GM
            [BackfireDrainPerTier] = 0.08, // temporal-stability drain per tier over your rank
            [SchoolMasteryChannel] = 40000, // cumulative mana channeled to Master a school (the pace knob)
            [MeditationTranceRaw] = 25.0,   // raw a FULL empty->full trance banks (vs meditation K=40)
            [MemoryCrystalManaFrac] = 0.25, // fraction of the pool a crystallized memory restores
            // Stage 2c: RAW practice per completed world ritual (RULED 2026-08-03) — one knob per
            // working so pay can follow ritual tier server-side without a code change. 16 = 4x the
            // lab Raw, ~62% of a day's laboratory curve at K=10: an evening's centerpiece. The
            // choke-point patch divides by the CONFIGURED lab Raw at bank time, so tuning lab Raw
            // does not skew ritual pay. Key list mirrors RitualKnobByTrigger below.
            ["ritualRawDecay"] = 16.0,
            ["ritualRawWorldEssence"] = 16.0,
            ["ritualRawPurification"] = 16.0,
            ["ritualRawCorruption"] = 16.0,
            ["ritualRawMinorCreation"] = 16.0,
            ["ritualRawMinorDestruction"] = 16.0,
            ["ritualRawMinorBalance"] = 16.0,
            ["ritualRawRunicPower1"] = 16.0,
            ["ritualRawRunicPower2"] = 16.0,
            ["ritualRawRunicPower3"] = 16.0,
            ["ritualRawTheRust"] = 16.0,
            ["ritualRawPyrolysis"] = 16.0,
            ["ritualRawOrebringer"] = 16.0,
            ["ritualRawWarding"] = 16.0,
            ["ritualRawGrace"] = 16.0,
            ["ritualRawResonance"] = 16.0,
            ["ritualRawElementalInfusion"] = 16.0,
        },
    };

    // ---- Stage 2a knob keys.
    /// <summary>Bonus mana per regen tick at Grandmaster (the manaregen stat delta; 0 below Novice).</summary>
    public const string RegenGm = "regenGm";
    /// <summary>Temporal-stability drain multiplier at Grandmaster (&lt;1 = a master's meditation/magic
    /// costs less stability). Floored — the trance always costs the dial.</summary>
    public const string DrainGm = "drainGm";
    /// <summary>School mana-cost discount at Grandmaster (all schools; RBM floors the modified cost at 1).</summary>
    public const string SchoolDiscountGm = "schoolDiscountGm";
    /// <summary>Residual backfire chance on the 450 ultimates even at GM (the Great Work is never routine).</summary>
    public const string BackfireGmResidual = "backfireGmResidual";
    /// <summary>Temporal-stability drain per tier over your rank when an over-tier cast backfires.</summary>
    public const string BackfireDrainPerTier = "backfireDrainPerTier";
    /// <summary>Raw practice a FULL empty-to-full meditation trance banks — the OUTCOME-normalized
    /// trance payoff (§4). The old drip paid a flat 0.6/real-minute, which against K=40 was a number
    /// nobody could feel; this pays for the mana actually restored, as a fraction of the pool. Because
    /// the fraction self-scales with the pool, one full trance is worth the same 25 raw at Novice and at
    /// Grandmaster — the trance stays meaningful as the pool grows, with no dynamic K.</summary>
    public const string MeditationTranceRaw = "meditationTranceRaw";
    /// <summary>Fraction of the effective mana pool a Crystallized Memory restores when consumed.
    /// RBM shipped the crystal as a tradeable lump of magic XP; under the annex that is exactly the
    /// thing ARC forbids (item-bound progression — a rank you can BUY, guard 5), and the annex already
    /// freezes its exp write to nothing. Rather than leave a dead item on the loot tables, it is
    /// repurposed into a one-shot mana burst: a stranger's practice is not transferable, but the
    /// reserves in the stone are. Fraction, not a flat number, so it stays useful at every rank.</summary>
    public const string MemoryCrystalManaFrac = "memoryCrystalManaFrac";

    // ---- Stage 2c: per-ritual completion pay (RULED 2026-08-03). A completed world ritual is a
    // large, resource-heavy, ONE-SHOT working (sixteen chalked runes, torches, an anchor, reagents,
    // and a casting), yet RBM's XP choke point passes the same literal 1 for a finished ritual as
    // for an oculus pulse — so 0.4.29 paid the floor. Each trigger method gets its own Bonus knob
    // (defaults above) holding the RAW practice a completed working banks.
    /// <summary>RBM ModSystemWorldMagic trigger-method name -> the Bonus knob holding that
    /// working's completion raw. Method names verified against the 3.2.5 decompile; a name that
    /// stops resolving warns and leaves that working on the floor weight (never throws).</summary>
    public static readonly Dictionary<string, string> RitualKnobByTrigger = new()
    {
        ["TriggerRitualOfDecay"] = "ritualRawDecay",
        ["TriggerRitualOfWorldEssence"] = "ritualRawWorldEssence",
        ["TriggerRitualOfPurification"] = "ritualRawPurification",
        ["TriggerRitualOfCorruption"] = "ritualRawCorruption",
        ["TriggerRitualOfCreation1"] = "ritualRawMinorCreation",
        ["TriggerRitualOfDestruction1"] = "ritualRawMinorDestruction",
        ["TriggerRitualOfBalance1"] = "ritualRawMinorBalance",
        ["TriggerRitualOfRunicPower1"] = "ritualRawRunicPower1",
        ["TriggerRitualOfRunicPower2"] = "ritualRawRunicPower2",
        ["TriggerRitualOfRunicPower3"] = "ritualRawRunicPower3",
        ["TriggerRitualOfTheRust1"] = "ritualRawTheRust",
        ["TriggerRitualOfPyrolysis1"] = "ritualRawPyrolysis",
        ["TriggerRitualOfOrebringer1"] = "ritualRawOrebringer",
        ["TriggerRitualOfWarding1"] = "ritualRawWarding",
        ["TriggerRitualOfGrace1"] = "ritualRawGrace",
        ["TriggerRitualOfResonance1"] = "ritualRawResonance",
        ["TriggerWorldMagicGrimoireElementalInfusion"] = "ritualRawElementalInfusion",
    };

    /// <summary>Shipped raw per completed working, mirrored by every ritualRaw* default above.</summary>
    public const double RitualRawDefault = 16.0;

    // ---- Tier gate (§3, RULED with amendment): the mana-cost thresholds that push a T3 into GM-only.
    /// <summary>T3 spells at/above this cost are the "storm/ultimate" class — GM-gated (not Master).</summary>
    public const int StormCostThreshold = 250;
    /// <summary>The 450 ultimates — carry the GM residual backfire.</summary>
    public const int UltimateCostThreshold = 450;

    /// <summary>Bonus mana/regen-tick for the given rank (the manaregen stat delta): 0 through Novice,
    /// climbing to the GM value. A master recovers mana faster — the meditation ladder's felt payoff.</summary>
    public static double ManaRegenBonus(int level)
    {
        if (level <= Leveling.Domain.SubLevelsPerTier) return 0.0;
        int max = Leveling.Domain.MaxLevelDefault, novice = Leveling.Domain.SubLevelsPerTier;
        return (level - novice) / (double)(max - novice) * Knob(RegenGm, 3.0);
    }

    /// <summary>Temporal-stability drain multiplier for the given rank: 1.0 through Novice (full drain),
    /// climbing DOWN to the GM value (a master meditates / casts at a lower stability cost). Floored — the
    /// trance always costs something. Applied at the RBM drain-packet chokepoint (§4 meditation ladder).</summary>
    public static double DrainMul(int level)
    {
        if (level <= Leveling.Domain.SubLevelsPerTier) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault, novice = Leveling.Domain.SubLevelsPerTier;
        double t = (level - novice) / (double)(max - novice);
        return 1.0 + t * (Knob(DrainGm, 0.6) - 1.0);
    }

    // ---- School familiarity (Stage 2b, RULED 2026-07-23): the second axis — efficiency WITHIN a school,
    // orthogonal to ARC rank (rank = capacity + safety; familiarity = how fluent you are in the school).
    // COST-ONLY for now: familiarity drives that school's mana-cost discount (replacing the old flat-by-
    // ARC-rank placeholder). Earned by MANA CHANNELED through the school (the cast's mana cost) — practice
    // is channeling, naturally paced by the mana economy (regen-limited, so a T3 storm is worth far more
    // than cantrip-spam). A coarse 5-step ladder (Novice..Master), stored per school in the synced
    // Knowledge store; the Codex derives the same rank client-side. The 3-of-4 mastery cap is DEFERRED
    // (design doc) until rank 5 grants a signature perk. Tier-gate stays on ARC rank: familiarity only
    // PRICES spells, never gates them.

    public const int SchoolFamMaxRank = 5;  // Novice(1) .. Master(5); rank 0 = untrained (never cast)

    /// <summary>The four schools that earn familiarity (the reconcile writes their cost stats).</summary>
    public static readonly string[] SchoolTechniques = { TechEvocation, TechAlteration, TechIncantation, TechConjuration };

    /// <summary>Cumulative mana channeled to Master a school (rank 5). The primary pace knob — mastering a
    /// school is ~a full domain climb of channeling. Playtest-tunable via Bonus (schoolMasteryChannel).</summary>
    public const string SchoolMasteryChannel = "schoolMasteryChannel";

    private static readonly string[] SchoolRankNames = { "", "Novice", "Apprentice", "Journeyman", "Adept", "Master" };

    /// <summary>Display name for a school familiarity rank (Novice..Master; "" for untrained).</summary>
    public static string SchoolRankName(int rank) => SchoolRankNames[System.Math.Clamp(rank, 0, SchoolFamMaxRank)];

    /// <summary>The synced Knowledge-store key holding a school's cumulative channeled mana, or "" for a
    /// technique that earns no familiarity (foundational / meditation / laboratory / inscription).</summary>
    public static string SchoolFamKey(string technique) => technique switch
    {
        TechEvocation or TechAlteration or TechIncantation or TechConjuration => "arc-fam-" + technique,
        _ => "",
    };

    /// <summary>The RBM per-school mana-cost modifier stat for a school technique (verified 3.2.5:
    /// additive-delta onto a 1.0 base, floor-1, stacks with gear), or "" for a non-school technique.</summary>
    public static string SchoolCostStat(string technique) => technique switch
    {
        TechEvocation => "evocationmodifiermanacost",
        TechAlteration => "alterationmodifiermanacost",
        TechIncantation => "incantationmodifiermanacost",
        TechConjuration => "conjurationmodifiermanacost",
        _ => "",
    };

    // Cumulative-channel thresholds as fractions of SchoolMasteryChannel, shaped like the domain tier
    // curve (each rank harder than the last). Index = rank; [0] unused. rank1 is ~first cast (Novice).
    private static readonly double[] SchoolFamFraction = { 0, 0.0001, 0.055, 0.174, 0.447, 1.0 };

    /// <summary>Cumulative channeled-mana needed to REACH the given school rank (1..5).</summary>
    public static int SchoolFamThreshold(int rank)
    {
        if (rank <= 0) return 0;
        if (rank > SchoolFamMaxRank) rank = SchoolFamMaxRank;
        return (int)System.Math.Round(Knob(SchoolMasteryChannel, 40000) * SchoolFamFraction[rank]);
    }

    /// <summary>School familiarity rank (0 untrained .. 5 Master) for a cumulative-channel total.</summary>
    public static int SchoolFamRank(int channeled)
    {
        int rank = 0;
        for (int r = 1; r <= SchoolFamMaxRank; r++)
        {
            if (channeled >= SchoolFamThreshold(r)) rank = r; else break;
        }
        return rank;
    }

    /// <summary>School mana-cost modifier CONTRIBUTION (the delta from RBM's 1.0 base) for a familiarity
    /// rank: 0 at Novice (rank &lt;= 1), down to -SchoolDiscountGm at Master (rank 5). RBM sums this onto
    /// the 1.0 base and floors the modified cost at 1. Written per school by the reconcile.</summary>
    public static double SchoolCostDelta(int famRank)
    {
        if (famRank <= 1) return 0.0;
        return -(famRank - 1) / (double)(SchoolFamMaxRank - 1) * Knob(SchoolDiscountGm, 0.15);
    }

    /// <summary>The ARC rank-tier a spell requires to cast cleanly (§3): T0-T1 = Novice(0), T2 =
    /// Apprentice(1), regular T3 = Master(3) (Journeyman does NOT reach T3 — the amendment), storm/ultimate
    /// T3 (cost >= StormCostThreshold) = GM(4).</summary>
    public static int RequiredRankTier(int spellTier, int cost)
    {
        if (spellTier <= 1) return 0;
        if (spellTier == 2) return 1;
        return cost >= StormCostThreshold ? 4 : 3;
    }

    /// <summary>The player's ARC rank-tier (0 Novice .. 4 GM; Untrained clamps to 0 for the ceiling).</summary>
    public static int PlayerRankTier(int level) => System.Math.Max(0, Leveling.Domain.TierOf(level));

    /// <summary>Backfire chance for a cast <paramref name="overBy"/> tiers above your rank (§3): 1 over =
    /// 50%, 2+ over = 90%; at/under tier = 0, except the GM residual on the 450 ultimates. "An apprentice
    /// can reach for the storm, and the storm reaches back."</summary>
    public static double BackfireChance(int overBy, bool ultimate)
    {
        if (overBy >= 2) return 0.90;
        if (overBy == 1) return 0.50;
        return ultimate ? Knob(BackfireGmResidual, 0.025) : 0.0;
    }

    // ---- The rank -> mana-pool FLOOR curve (§2 RATIFIED). Anchors sit at each tier's END level for the
    // 17-level ladder (Novice IV=4, Apprentice IV=8, Journeyman IV=12, Master IV=16, GM=17 terminal);
    // sub-levels I->IV interpolate. gear + research stack ON TOP of this floor. Playtest-tuned magnitudes.
    //   Untrained 10 | Novice 10->25 | Apprentice 25->60 | Journeyman 60->110 | Master 110->235 | GM =380
    // GM (17) is a single hard terminal leap (235->380), mirroring the XP curve's one-big-jump GM step.
    private static readonly (int level, int floor)[] Anchors =
    {
        (0, 10), (4, 25), (8, 60), (12, 110), (16, 235), (17, 380),
    };

    /// <summary>RBM's config STARTINGPLAYERMANA (default 9): RBM ALWAYS computes the effective pool as
    /// playermaxmana + armor + researched + THIS. So the §2 floor (the effective total we want) maps to a
    /// base write of floor - 9. A fresh mage's base is 1 (-> 1+9 = 10 effective, the initiation's 17
    /// cantrips). Re-verify if The Quire changes ENTITYBEHAVIOR_MAXMANA_STARTINGPLAYERMANA.</summary>
    public const int RbmStartingMana = 9;

    /// <summary>The value to write into `playermaxmana_rm` (the BASE component) so the effective pool lands
    /// on the §2 rank floor: floor - the RBM starting constant, floored at 1 (RBM's true minimum base). RBM
    /// then computes the effective total = this + 9 + gear + research on its own tick — we never write the
    /// total ourselves (that competing write, missing the +9, was the 0.3.172 mana-flicker bug).</summary>
    public static int BasePool(int level) => System.Math.Max(1, ManaFloor(level) - RbmStartingMana);

    /// <summary>The ARC-rank mana-pool floor (the EFFECTIVE total we want) for the given level (0 =
    /// Untrained). Piecewise-linear between the ratified rank anchors. gear/research add on top.</summary>
    public static int ManaFloor(int level)
    {
        if (level <= Anchors[0].level) return Anchors[0].floor;
        for (int i = 1; i < Anchors.Length; i++)
        {
            if (level <= Anchors[i].level)
            {
                var (l0, f0) = Anchors[i - 1];
                var (l1, f1) = Anchors[i];
                double t = (level - l0) / (double)(l1 - l0);
                return (int)System.Math.Round(f0 + t * (f1 - f0));
            }
        }
        return Anchors[^1].floor;
    }

    /// <summary>XP-weight for a cast by spell tier: a T3 storm-cast is worth more practice than a cantrip.
    /// Modest so casting stays a steady income, not a spike. Scales the config raw for the one grant.</summary>
    public static double CastWeight(int tier) => tier switch
    {
        <= 0 => 0.75,   // T0 innate cantrips
        1 => 1.0,
        2 => 1.5,
        _ => 2.0,        // T3+
    };

    /// <summary>Map an RBM spell school name to the ARC technique verb; unknown/none -> foundational.</summary>
    public static string TechniqueForSchool(string? school)
    {
        if (string.IsNullOrEmpty(school)) return TechFoundational;
        return school.ToLowerInvariant() switch
        {
            "evocation" => TechEvocation,
            "alteration" => TechAlteration,
            "incantation" => TechIncantation,
            "conjuration" => TechConjuration,
            _ => TechFoundational,
        };
    }

    /// <summary>Server-side ARC level for a player (0 = Untrained when unknown).</summary>
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

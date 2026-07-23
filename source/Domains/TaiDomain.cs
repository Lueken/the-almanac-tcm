using AlmanacTcm.Config;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// TAI — "Tailoring" defaults (rank-bonus-design.md §TAI; technique-maps §TAI; LEA ruled
/// not-warranted 2026-07-22, tanning stays in HUN). The fibre-to-cloth calling: the player
/// takes a fibre (flax, wool, cotton) up through twine, cloth, and finished garments, and a
/// tailor's hand shows in what they make.
///
/// A mod-breadth domain on The Quire: the practised verbs are spinning + weaving (spinningwheel,
/// station-gated) and knitting (knitting mod, handheld), with sewing/repair the vanilla floor
/// (grid-craft + the vanilla clothing-repair recipes). `m` = 3 (three of the four verbs already
/// bank full breadth — the spinning wheel gives spin AND weave from one station, so requiring all
/// four would over-grind). A pure-vanilla server sees only sewing/repair and reads as 1.
///
/// The identity is THE TAILOR'S MARK (<see cref="TaiMark"/>): a maker-mark stamped at the creating
/// act onto every garment a Journeyman+ tailor makes (knitted = grant+stamp; grid clothes =
/// stamp-only, since the XP is earned at spin/weave/knit, not the assembly grid). The mark reads at
/// wear to lift the garment QUALITY, NERF-FIRST — a little warmer, a little slower to wear through,
/// a little cooler in the heat (the Cool read is HoD-conditional). At Grandmaster the mark carries
/// an EMPHASIS — Warm, Lasting, or Cool — set by the player's own toggle on the Tailoring page in
/// the Almanac book (see <see cref="TaiEmphasis"/>), frozen onto the garment at the creating act.
/// A repair by an under-ranked tailor strips the mark (the master's hand is undone). Plus the fibre
/// thrift bonus at spin/weave (Axis 2/4): a master gets the occasional extra length of twine/cloth.
///
/// Affinity (tailor +3 / hunter +1 / archivist,blackguard,butcher,malefactor,spelunker -1) already
/// lives in AffinitySystem. Reliability is N/A (deterministic verbs); no material/rank gate (the
/// station + fibre self-gate).
/// </summary>
public static class TaiDomain
{
    public const string Code = "TAI";

    // The verbs. Spin/weave/knit are the earning acts; sew is the vanilla grid-repair floor.
    public const string TechSpin = "spin";    // spindle ExtractTwine + wheel SpinInput
    public const string TechWeave = "weave";  // loom WeaveInput
    public const string TechKnit = "knit";    // ItemKnittingNeedles.OnHeldInteractStop
    public const string TechSew = "sew";       // vanilla clothing-repair recipe (GridRecipe.ConsumeInput)

    // ---- The Tailor's Mark ladder (Axis 1 penalty + Axis 6 identity) knob keys.
    /// <summary>Warmth multiplier at the Untrained end — below the pattern (a beginner's coat holds less
    /// heat, the Axis-1 penalty). From Novice up warmth sits at flat vanilla: the RANK climb is longevity
    /// (WearMul), not warmth. Warmth-above-vanilla is the EARNED GM Warm choice only — that is what makes
    /// "warm for the north" a Grandmaster capability rather than a passive perk (design Axis 6, realigned
    /// 2026-07-22).</summary>
    public const string WarmthUntrained = "warmthUntrained";
    /// <summary>Condition-loss multiplier at Untrained (&gt;1 = wears faster — sloppy seams).</summary>
    public const string WearUntrained = "wearUntrained";
    /// <summary>Condition-loss multiplier at Grandmaster (&lt;1 = wears slower — a master's seams hold).
    /// This is the maker's-quality that CLIMBS with rank (design Axis 6): the coat holds its warmth
    /// longer. Lasting deepens it at GM.</summary>
    public const string WearGm = "wearGm";
    /// <summary>Cooling multiplier at Untrained (HoD): below the pattern. Flat vanilla from Novice up;
    /// cooling-above-vanilla is the earned GM Cool choice only (the warmth mirror).</summary>
    public const string CoolingUntrained = "coolingUntrained";
    /// <summary>The extra GM-emphasis bump: Warm lifts warmth, Lasting deepens the wear reduction, Cool
    /// lifts cooling. The choice itself is the player's book toggle (see <see cref="TaiEmphasis"/>).</summary>
    public const string EmphasisBonus = "emphasisBonus";

    /// <summary>Fibre economy (Axis 2, steady): the per-fibre yield multiplier — a master draws MORE
    /// twine/cloth per fibre unit, an Untrained draws LESS (the Axis-1 fibre-waste penalty). Untrained
    /// value, climbing to the GM value; applied steadily at the spindle (batch output) and as its
    /// fractional proc at the powered wheel/loom (discrete per-cycle output).</summary>
    public const string FiberEconomyUntrained = "fiberEconomyUntrained";
    public const string FiberEconomyGm = "fiberEconomyGm";

    /// <summary>Provenance tiers (a mark means something from Journeyman up): the TAI taiBy tag.
    /// Sewn by (J) -> Tailored by (M) -> Master-tailored by (GM). Level thresholds.</summary>
    public const int ProvJourneyman = 9, ProvMaster = 13, ProvGm = 17;

    // Emphasis codes (per-player book choice, frozen onto the mark at the creating act).
    public const int EmphLasting = 0;   // the neutral default — a durable garment
    public const int EmphWarm = 1;       // deeper warmth
    public const int EmphCool = 2;        // deeper cooling (HoD)

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        // small-m: 3 with the fibre mods (spin/weave/knit/sew, three bank full breadth); a pure-
        // vanilla server has only sew/repair and reads as 1 (the available-technique clamp).
        M = 3,
        // Fibre neighbourhood: foraging (retted fibre), farming (flax), hunting (the leather sibling).
        Adjacency = new List<string> { "FOR", "FAR", "HUN" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Repeatable station work: modest raw, real K ceiling (the remedy shape).
            [TechSpin] = new() { Raw = 2, K = 20 },
            [TechWeave] = new() { Raw = 2, K = 18 },
            [TechKnit] = new() { Raw = 2, K = 18 },
            // Repair is rarer than making cloth: a lower ceiling.
            [TechSew] = new() { Raw = 2, K = 14 },
        },
        Bonus = new Dictionary<string, double>
        {
            // The Tailor's Mark ladder (MET/COO numeric posture, playtest-tuned). Warmth/cooling are
            // the Untrained penalty only — flat vanilla mid-rank, lifted only by the GM Warm/Cool choice.
            [WarmthUntrained] = 0.90,
            [WearUntrained] = 1.10, [WearGm] = 0.85,
            [CoolingUntrained] = 0.90,
            [EmphasisBonus] = 0.08,
            // Fibre economy (Axis 2): a master draws more per fibre, an Untrained less.
            [FiberEconomyUntrained] = 0.90, [FiberEconomyGm] = 1.15,
        },
    };

    /// <summary>The shared quality curve: <paramref name="untrained"/> below Novice (the penalty),
    /// exactly 1.0 across Novice, a gentle linear climb from Apprentice to <paramref name="gm"/> at
    /// max level. NERF-FIRST — a Master's garment is a little better, never a different item.</summary>
    private static double QualityFactor(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int novice = Leveling.Domain.SubLevelsPerTier;       // 4 (Novice IV)
        if (level <= novice) return 1.0;
        int max = Leveling.Domain.MaxLevelDefault;           // 20
        double t = (level - novice) / (double)(max - novice); // 0 at Novice IV .. 1 at GM IV
        return 1.0 + t * (gm - 1.0);
    }

    /// <summary>Warmth multiplier for a garment of the given mark level: the Untrained penalty (colder),
    /// flat vanilla from Novice up (the rank climb is longevity, not warmth), and lifted above vanilla
    /// ONLY by the earned GM Warm choice. That is what makes "warm for the north" a Grandmaster capability
    /// rather than a passive rank perk (design Axis 6, realigned 2026-07-22).</summary>
    public static double WarmthMul(int level, int emphasis)
    {
        double f = level <= 0 ? Knob(WarmthUntrained, 0.90) : 1.0;
        if (emphasis == EmphWarm && level >= ProvGm) f *= 1.0 + Knob(EmphasisBonus, 0.08);
        return f;
    }

    /// <summary>Condition-loss multiplier (applied to wear; &lt;1 = slower wear) for the given mark level.
    /// This is the maker's-quality that CLIMBS with rank (a Master's coat holds its warmth longer), with
    /// the GM Lasting emphasis pulling it a little further down.</summary>
    public static double WearMul(int level, int emphasis)
    {
        double f = QualityFactor(level, Knob(WearUntrained, 1.10), Knob(WearGm, 0.85));
        if (emphasis == EmphLasting && level >= ProvGm) f *= 1.0 - Knob(EmphasisBonus, 0.08);
        return f;
    }

    /// <summary>Cooling multiplier (HoD) for the given mark level: the warmth mirror — Untrained penalty,
    /// flat vanilla from Novice up, lifted above vanilla ONLY by the earned GM Cool choice.</summary>
    public static double CoolingMul(int level, int emphasis)
    {
        double f = level <= 0 ? Knob(CoolingUntrained, 0.90) : 1.0;
        if (emphasis == EmphCool && level >= ProvGm) f *= 1.0 + Knob(EmphasisBonus, 0.08);
        return f;
    }

    /// <summary>Fibre economy (Axis 2, steady): the per-fibre yield multiplier — Untrained draws less
    /// (0.90, the penalty), 1.0 across Novice, climbing to the GM value (1.15). A reliable expected-value
    /// lever, not a proc; the caller applies its fractional part as a proc where output is discrete.</summary>
    public static double FiberEconomy(int level) =>
        QualityFactor(level, Knob(FiberEconomyUntrained, 0.90), Knob(FiberEconomyGm, 1.15));

    /// <summary>Server-side TAI level for a player (0 = Untrained when unknown).</summary>
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

/// <summary>
/// The Tailor's Mark: the per-stack maker-mark written at the creating act (spin/weave/knit, grid
/// garment craft) and read at wear to lift the garment's quality (warmth, durability, cooling). A
/// per-GARMENT mark bound to the maker (each finished piece carries its own), unlike the ALC BATCH
/// brand — a garment is a single durable object, not a fungible stack. The emphasis flag carries the
/// GM Warm/Lasting/Cool choice. Mirrors the AlcBrand shape.
/// </summary>
public static class TaiMark
{
    public const string ByAttr = "almanactcm:taiby";
    public const string ByNameAttr = "almanactcm:taibyname";
    public const string LevelAttr = "almanactcm:tailevel";
    /// <summary>0 = Lasting (durable, the base default), 1 = Warm, 2 = Cool.</summary>
    public const string EmphasisAttr = "almanactcm:taiemph";

    /// <summary>Stamp the maker mark onto a freshly made garment. Writes the acting maker's live rank
    /// (the creating act should record it) and their book emphasis. Never stamps the named provenance
    /// below Journeyman (a mark means something from J up, like the other domain marks), but ALWAYS
    /// records the level for the quality read so an Apprentice's climb still applies.</summary>
    public static void Stamp(ItemStack? stack, string uid, string name, int level, int emphasis)
    {
        if (stack?.Collectible == null) return;
        stack.Attributes.SetInt(LevelAttr, level);
        stack.Attributes.SetInt(EmphasisAttr, emphasis);
        if (level >= TaiDomain.ProvJourneyman)
        {
            stack.Attributes.SetString(ByAttr, uid);
            stack.Attributes.SetString(ByNameAttr, name);
        }
    }

    /// <summary>Strip the mark entirely (an under-ranked repair undoes the master's hand).</summary>
    public static void Strip(ItemStack? stack)
    {
        var a = stack?.Attributes;
        if (a == null) return;
        a.RemoveAttribute(LevelAttr);
        a.RemoveAttribute(EmphasisAttr);
        a.RemoveAttribute(ByAttr);
        a.RemoveAttribute(ByNameAttr);
    }

    /// <summary>The mark level on a stack (0 = unmarked = Untrained-equivalent read).</summary>
    public static int LevelOf(ItemStack? stack) => stack?.Attributes.GetInt(LevelAttr, 0) ?? 0;

    /// <summary>The mark's GM emphasis (0 Lasting / 1 Warm / 2 Cool).</summary>
    public static int EmphasisOf(ItemStack? stack) => stack?.Attributes.GetInt(EmphasisAttr, 0) ?? 0;

    public static bool HasMark(ItemStack? stack) => stack?.Attributes.HasAttribute(LevelAttr) ?? false;
}

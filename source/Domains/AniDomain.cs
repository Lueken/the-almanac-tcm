using AlmanacTcm.Config;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AlmanacTcm.Domains;

/// <summary>
/// ANI — "Animal Handling" defaults (rank-bonus-design.md §ANI, RULED 2026-07-10 with the
/// ani-line verification study adopted 8/8; technique-maps §ANI ruled 2026-07-08). The
/// lineage half of the farm-to-table trio: it makes wild animals tame and breeds them up.
///
/// A genuinely thin, deep domain — two verbs, m=2 (the small-m clamp, BRE/PAN precedent):
///   • Gen-raising (#1): the unattended BIRTH, one generation higher than the dam. No IPlayer
///     is in scope, so it reads the shared `raisedBy` owner stamp that FAR's trough feed writes
///     onto the eating animal (TCM writes it itself — the recycled-xLib attribution). Raw scaled
///     by the offspring's generation (capped): climbing to high-gen stock is the skill (ruled Q3).
///   • Taming (#2): one verb, two completion hooks (casting-merge precedent) — the petai
///     feed-to-domesticate transition and the vanilla saddle-break convert. Banked at the
///     WILD/feral -> TAME transition only; partial progress banks nothing.
///
/// Phase 2+: genetics (inbreeding-clean lines), litter depth, reduced saddle self-injury, and
/// the genetics-founded Master's Line GM signature. Note the affinity GM door is butcher +1
/// rancher alone (the ruled override for a domain with zero positive affinity cells).
/// </summary>
public static class AniDomain
{
    public const string Code = "ANI";

    /// <summary>The gen-raise birth (vanilla EntityBehaviorMultiply.GiveBirth; genelib overrides
    /// it). Unattended — attributed via the FAR trough `raisedBy` stamp on the dam.</summary>
    public const string TechGenRaising = "genraising";
    /// <summary>Domestication: petai feed-to-tame OR vanilla saddle-break, one verb two hooks.</summary>
    public const string TechTaming = "taming";

    /// <summary>The shared owner stamp FAR's trough feed (and the taming hooks) write on an
    /// animal and ANI's birth reads. A durable WatchedAttributes string, so it survives to the
    /// unattended birth AND rides the WatchedAttributes clone when taming replaces the entity.
    /// It lives on the animal it was earned on: a newborn earns its own stamp when it is later
    /// fed or tamed. Named to avoid clashing with petai OwnerId and vanilla ownedby (those are
    /// ownership; this is who does the husbandry work).</summary>
    public const string RaisedByAttr = "almanacRaisedBy";

    // ---- The Master's Line (Axis 6, GM signature + provenance, RULED 2026-07-10). The economy
    // itself is emergent (calm high-gen stock from the gen-raise loop; genetic health from the
    // bloodline purge). What is authored is the tiered provenance stamp below.
    /// <summary>The name and the peak ANI tier of whoever raised this animal — the Master's Line
    /// mark. Upgrade-only (never downgrades when a lesser hand later tends it), so a GM's stock
    /// keeps advertising its pedigree through a sale. Tiered display from Journeyman up.</summary>
    public const string ProvNameAttr = "almanacProvName";
    public const string ProvTierAttr = "almanacProvTier";

    /// <summary>The provenance tier thresholds (a mark means something from Journeyman up).</summary>
    public const int ProvJourneyman = 9, ProvMaster = 13, ProvGm = 17;

    /// <summary>Stamp (or UPGRADE) an animal's Master's Line provenance from an owner's current
    /// ANI tier. Upgrade-only: a GM-raised animal stays a Master's Line even after a novice buyer
    /// feeds it. Below Journeyman leaves no mark. Server-side; rides WatchedAttributes to the
    /// client for the hover tooltip.</summary>
    public static void StampProvenance(Vintagestory.API.Common.Entities.Entity? animal, Vintagestory.API.Common.IPlayer? owner)
    {
        if (animal?.WatchedAttributes == null || owner == null) return;
        int tier = LevelOf(owner);
        if (tier < ProvJourneyman) return;
        if (animal.WatchedAttributes.GetInt(ProvTierAttr, 0) >= tier) return; // never downgrade
        animal.WatchedAttributes.SetInt(ProvTierAttr, tier);
        animal.WatchedAttributes.SetString(ProvNameAttr, owner.PlayerName);
    }

    // Bonus knob keys.
    /// <summary>Gen-raise raw multiplier per generation of the newborn, capped (ruled Q3: quality
    /// of practice over bulk, mirroring MIN depth/rarity). rawMult = min(cap, 1 + step*(gen-1)).</summary>
    public const string GenRaiseStep = "genRaiseStep";
    public const string GenRaiseCap = "genRaiseCap";

    // ---- Phase 2 knobs (ANI ladder RULED 2026-07-10, genetics-founded per the ani-line study).
    /// <summary>Bloodline hygiene (the headline): extra allele-purge resistance a GM breeder's
    /// newborns enjoy, ADDED to genelib's InbreedingResistance (0.6 stock) during their spawn
    /// finalize; hard-capped below 1 so loss never reaches zero (principle 3).</summary>
    public const string PurgeBonusGm = "purgeBonusGm";
    /// <summary>Litter depth: chance at GM of +1 offspring, capped at the species' own
    /// SpawnQuantityMax — within genelib's range, never doubling at low rank (ruled).</summary>
    public const string LitterProcGm = "litterProcGm";
    /// <summary>Treat economy (the MET fuel analog): progress-per-treat factor at the ends —
    /// a master reaches DOMESTICATED on fewer treats.</summary>
    public const string TreatUntrained = "treatUntrained";
    public const string TreatGm = "treatGm";
    /// <summary>Reduced saddle-break self-injury (the ruled correction: no failure roll exists,
    /// so the honest lever is being thrown softer): fraction of throw damage healed back at GM.</summary>
    public const string ThrowHealGm = "throwHealGm";
    /// <summary>The predator-taming gate (ruled Open Q4): minimum ANI level to INITIATE taming a
    /// wild predator. 0 = ungated. Wolf at Journeyman I, fox at Apprentice I by default.</summary>
    public const string GateWolf = "gateWolf";
    public const string GateFox = "gateFox";

    public static DomainConfig Defaults() => new()
    {
        Code = Code,
        Smax = 100,
        M = 2, // small-m clamp: 2 rare, late techniques — depth over breadth is the honest shape
        Adjacency = new List<string> { "HUN", "FAR" },
        Techniques = new Dictionary<string, TechniqueConfig>
        {
            // Both rare and late; small K (one birth / one domestication ~ banked). The gen-raise
            // raw is scaled up at the call site by the newborn's generation (ruled Q3).
            [TechGenRaising] = new() { Raw = 8, K = 12 },
            [TechTaming] = new() { Raw = 8, K = 12 },
        },
        Bonus = new Dictionary<string, double>
        {
            [GenRaiseStep] = 0.35, // +35% raw per generation above the first...
            [GenRaiseCap] = 3.0,   // ...capped at 3x so a high-gen line still tapers.
            // Phase 2 (MET numeric posture, playtest-tuned).
            [PurgeBonusGm] = 0.30,   // resistance 0.6 stock -> up to 0.9 for a GM's newborns
            [LitterProcGm] = 0.35,
            [TreatUntrained] = 0.90, [TreatGm] = 1.40,
            [ThrowHealGm] = 0.70,
            [GateWolf] = 9, [GateFox] = 5,
        },
    };

    /// <summary>The Apprentice-and-up reward curve (0 through Novice, linear to 1.0 at max) —
    /// the shared Phase 2 shape.</summary>
    public static double BonusT(int level)
    {
        const int start = 5;
        if (level < start) return 0;
        int max = Leveling.Domain.MaxLevelDefault;
        return (level - start) / (double)(max - start);
    }

    /// <summary>General rank curve: untrained at 0, vanilla at Novice I, linear to gm at max.</summary>
    public static double RankLinear(int level, double untrained, double gm)
    {
        if (level <= 0) return untrained;
        int max = Leveling.Domain.MaxLevelDefault;
        return 1.0 + (level - 1) / (double)(max - 1) * (gm - 1.0);
    }

    /// <summary>The generation-scaled raw multiplier for a birth: 1.0 for a gen-1 newborn, rising
    /// by GenRaiseStep per generation to the GenRaiseCap ceiling. Climbing lineages is the skill.</summary>
    public static double GenRaiseMult(int generation)
    {
        double step = Knob(GenRaiseStep, 0.35), cap = Knob(GenRaiseCap, 3.0);
        return System.Math.Min(cap, 1.0 + step * System.Math.Max(0, generation - 1));
    }

    /// <summary>Server-side ANI level for a player (0 = Untrained when unknown).</summary>
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

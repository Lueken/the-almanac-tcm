using System;
using System.Collections.Generic;
using System.Globalization;
using AlmanacTcm.Leveling;

namespace AlmanacTcm.Domains;

/// <summary>
/// The display figures the Callings book quotes, computed SERVER-SIDE through the very same
/// knob reads and curve helpers the gameplay patches use (WooPatches:179-181 is the model),
/// then synced to clients as pre-formatted strings via <see cref="FiguresPacket"/>.
///
/// WHY THIS EXISTS (ruled by Jeffrey 2026-08-22): the book's rung copy quotes numbers, and a
/// number printed from prose is a number that lies the moment a knob is tuned. The ModDB
/// transcription proved it — its Novice IV felling lean says 24 degrees where this code
/// computes 21.6. So prose lives in assets/almanactcm/almanac/rungs.json with {token}
/// placeholders, and every figure resolves HERE, through the live DomainConfig. Tune a knob,
/// restart, and every quoted number in the book is already correct.
///
/// This deliberately extends the AffinityPacket precedent, not the hidden-values rule: raw
/// knobs and curves never cross the wire — only RESOLVED display strings do, and only the
/// ones the book actually quotes.
///
/// One provider per domain, registered in <see cref="Providers"/>. WOO ships first as the
/// model; the remaining domains join as their rung copy is tokenized.
/// </summary>
public static class DomainFigures
{
    /// <summary>Per-domain figure providers. Key = roster code. Compute on the SERVER only
    /// (providers read live DomainConfig through each domain's Knob helper; on a client the
    /// knob read falls back to compiled defaults, which is exactly the lie this pipeline
    /// exists to avoid).</summary>
    public static readonly Dictionary<string, Func<Dictionary<string, string>>> Providers = new()
    {
        [WooDomain.Code] = WooFigures,
        [FarDomain.Code] = FarFigures,
        [AniDomain.Code] = AniFigures,
        [BeeDomain.Code] = BeeFigures,
        [ForDomain.Code] = ForFigures,
        [HunDomain.Code] = HunFigures,
        [MetDomain.Code] = MetFigures,
        [MasDomain.Code] = MasFigures,
        [EngDomain.Code] = EngFigures,
        [PotDomain.Code] = PotFigures,
        [GlaDomain.Code] = GlaFigures,
        [CooDomain.Code] = CooFigures,
        [BreDomain.Code] = BreFigures,
        [TaiDomain.Code] = TaiFigures,
        [AlcDomain.Code] = AlcFigures,
        [MinDomain.Code] = MinFigures,
        [PanDomain.Code] = PanFigures,
        [FisDomain.Code] = FisFigures,
        [MelDomain.Code] = MelFigures,
        [RanDomain.Code] = RanFigures,
        [ArcDomain.Code] = ArcFigures,
        [TemDomain.Code] = TemFigures,
    };

    /// <summary>Figures that live CLIENT-side — compiled feature constants (the Tracker's
    /// Eye ranges) and ConfigLib feel values (blood-trail spread, focus delay) — computed
    /// at render time on the client, so a ConfigLib tune shows on the very next open.
    /// Merged UNDER the server-synced dictionary: a synced key always wins.</summary>
    public static readonly Dictionary<string, Func<Dictionary<string, string>>> ClientProviders = new()
    {
        [HunDomain.Code] = HunClientFigures,
    };

    /// <summary>The full figure set for one domain as the renderer should see it:
    /// server-synced figures over the client-computed ones.</summary>
    public static Dictionary<string, string> Merged(string code, Dictionary<string, string>? synced)
    {
        Dictionary<string, string> merged = ClientProviders.TryGetValue(code, out var cp)
            ? cp() : new Dictionary<string, string>();
        if (synced != null) foreach (var (k, v) in synced) merged[k] = v;
        return merged;
    }

    // Rank-entry/exit levels the copy quotes (Rank.cs is the ladder authority).
    private const int N1 = 1, N2 = 2, N4 = Rank.NoviceIV;
    private const int A1 = Rank.Apprentice, A4 = Rank.ApprenticeIV;
    private const int J1 = Rank.Journeyman, J4 = Rank.JourneymanIV;
    private const int M1 = Rank.Master, M2 = Rank.Master + 1, M4 = Rank.MasterIV;
    private const int GM = Rank.Grandmaster;

    /// <summary>The shared "opens at Apprentice I" reward shape (FarDomain.BonusT and its
    /// siblings): 0 through Novice, linear from Apprentice I to 1.0 at Grandmaster.</summary>
    private static double BonusT(int level)
        => level < A1 ? 0 : (level - A1) / (double)(GM - A1);

    /// <summary>The shared multiplier shape (each domain's RankLinear): untrained at 0,
    /// exactly 1.0 at Novice I, linear to gm at Grandmaster.</summary>
    private static double RankLinear(int level, double untrained, double gm)
        => level <= 0 ? untrained : 1.0 + (level - 1) / (double)(GM - 1) * (gm - 1.0);

    // ---------------------------------------------------------------- WOO

    /// <summary>Woodcutting's quoted figures. Every computation is the patch's own:
    /// cone/lean via RankProgress lerp (WooFallingTreePatches:256-271), leaf yield and
    /// stamina via RankLinear (WooPatches:179, WooPatches stamina twin), windfall via
    /// WindfallGmChance x RankProgress (WooPatches:181), pit floors as direct knobs
    /// (WooColliderPatches:140-158).</summary>
    private static Dictionary<string, string> WooFigures()
    {
        double Knob(string key, double fallback) => WooDomain.Knob(key, fallback);

        double spreadU = Knob(WooDomain.FellSpreadUntrained, 85);
        double spreadGm = Knob(WooDomain.FellSpreadGm, 6);
        double biasU = Knob(WooDomain.FellBiasUntrained, 35);
        double biasGm = Knob(WooDomain.FellBiasGm, -22);
        double leafU = Knob(WooDomain.LeafYieldUntrained, 0.8);
        double leafGm = Knob(WooDomain.LeafYieldGm, 1.2);
        double stamU = Knob(WooDomain.StaminaUntrained, 1.15);
        double stamGm = Knob(WooDomain.StaminaGm, 0.85);
        double windGm = Knob(WooDomain.WindfallGmChance, 0.15);

        double Spread(int level) => Lerp(spreadU, spreadGm, WooDomain.RankProgress(level));
        double Bias(int level) => Lerp(biasU, biasGm, WooDomain.RankProgress(level));
        double LeafPct(int level) => (WooDomain.RankLinear(level, leafU, leafGm) - 1) * 100;
        double StamCheaperPct(int level) => (1 - WooDomain.RankLinear(level, stamU, stamGm)) * 100;
        double WindPct(int level) => windGm * WooDomain.RankProgress(level) * 100;

        var f = new Dictionary<string, string>();

        // The felling cone: half-width in degrees at each quoted level, and the lean as a
        // directional PHRASE — the lean's sign crosses zero mid-ladder (Journeyman II at
        // shipped defaults), so prose that hard-coded "toward you" would start lying the
        // moment the reader climbs. The phrase carries its own direction.
        f["spreadU"] = F(spreadU, 0);
        f["biasU"] = LeanPhrase(biasU);
        f["spreadN1"] = F(Spread(Rank.Novice), 1);
        f["biasN1"] = LeanPhrase(Bias(Rank.Novice));
        f["spreadN4"] = F(Spread(Rank.NoviceIV), 1);
        f["biasN4"] = LeanPhrase(Bias(Rank.NoviceIV));
        f["spreadA1"] = F(Spread(Rank.Apprentice), 1);
        f["biasA1"] = LeanPhrase(Bias(Rank.Apprentice));
        f["spreadA4"] = F(Spread(Rank.ApprenticeIV), 1);
        f["biasA4"] = LeanPhrase(Bias(Rank.ApprenticeIV));
        f["spreadJ1"] = F(Spread(Rank.Journeyman), 1);
        f["biasJ1"] = LeanPhrase(Bias(Rank.Journeyman));
        f["spreadJ4"] = F(Spread(Rank.JourneymanIV), 1);
        f["biasJ4"] = LeanPhrase(Bias(Rank.JourneymanIV));
        f["spreadM1"] = F(Spread(Rank.Master), 1);
        f["biasM1"] = LeanPhrase(Bias(Rank.Master));
        f["spreadM2"] = F(Spread(Rank.Master + 1), 1);
        f["biasM2"] = LeanPhrase(Bias(Rank.Master + 1));
        f["spreadM4"] = F(Spread(Rank.MasterIV), 1);
        f["biasM4"] = LeanPhrase(Bias(Rank.MasterIV));
        f["spreadGm"] = F(spreadGm, 0);
        f["biasGm"] = LeanPhrase(biasGm);

        // Impact: flat, rank never touches it (the design point the copy makes).
        f["impactDmg"] = F(Knob(WooDomain.FellImpactDamage, 8), 0);
        f["impactCooldownMs"] = F(Knob(WooDomain.FellDamageCooldownMs, 600), 0);

        // Leaf yield: the Untrained multiplier, the per-level step in points, and the
        // percent-over-vanilla at each quoted level.
        f["leafU"] = F(leafU, 2);
        f["leafPerLevelPts"] = F((leafGm - 1) * 100 / (Domain.MaxLevelDefault - 1), 2);
        f["leafN2"] = F(LeafPct(Rank.Novice + 1), 2);
        f["leafN4"] = F(LeafPct(Rank.NoviceIV), 2);
        f["leafA1"] = F(LeafPct(Rank.Apprentice), 2);
        f["leafA4"] = F(LeafPct(Rank.ApprenticeIV), 2);
        f["leafJ1"] = F(LeafPct(Rank.Journeyman), 2);
        f["leafJ4"] = F(LeafPct(Rank.JourneymanIV), 2);
        f["leafM1"] = F(LeafPct(Rank.Master), 2);
        f["leafM4"] = F(LeafPct(Rank.MasterIV), 2);
        f["leafGm"] = F(LeafPct(Rank.Grandmaster), 2);

        // Axe stamina (Immersive Mining axis): percent dearer than vanilla when Untrained,
        // percent cheaper at each quoted level after.
        f["stamU"] = F((stamU - 1) * 100, 2);
        f["stamN2"] = F(StamCheaperPct(Rank.Novice + 1), 2);
        f["stamN4"] = F(StamCheaperPct(Rank.NoviceIV), 2);
        f["stamA1"] = F(StamCheaperPct(Rank.Apprentice), 2);
        f["stamA4"] = F(StamCheaperPct(Rank.ApprenticeIV), 2);
        f["stamJ1"] = F(StamCheaperPct(Rank.Journeyman), 2);
        f["stamJ4"] = F(StamCheaperPct(Rank.JourneymanIV), 2);
        f["stamM1"] = F(StamCheaperPct(Rank.Master), 2);
        f["stamM4"] = F(StamCheaperPct(Rank.MasterIV), 2);
        f["stamGm"] = F((1 - stamGm) * 100, 2);

        // The windfall chance at each quoted level.
        f["windN1"] = F(WindPct(Rank.Novice), 2);
        f["windN4"] = F(WindPct(Rank.NoviceIV), 2);
        f["windA1"] = F(WindPct(Rank.Apprentice), 2);
        f["windA4"] = F(WindPct(Rank.ApprenticeIV), 2);
        f["windJ1"] = F(WindPct(Rank.Journeyman), 2);
        f["windJ4"] = F(WindPct(Rank.JourneymanIV), 2);
        f["windM1"] = F(WindPct(Rank.Master), 2);
        f["windM4"] = F(WindPct(Rank.MasterIV), 2);
        f["windGm"] = F(windGm * 100, 2);

        // The collier's pit: floor per tier, ceiling (Untrained's is the only lowered one),
        // and the average burn each band implies.
        double pitFloorU = Knob(WooDomain.PitFloorUntrained, 0.35);
        double pitCeilU = Knob(WooDomain.PitCeilUntrained, 0.85);
        f["pitFloorU"] = F(pitFloorU, 2);
        f["pitCeilU"] = F(pitCeilU, 2);
        f["pitAvgU"] = F((pitFloorU + pitCeilU) / 2, 3);
        void Pit(string suffix, string knobKey, double fallback)
        {
            double floor = Knob(knobKey, fallback);
            f["pitFloor" + suffix] = F(floor, 2);
            f["pitAvg" + suffix] = F((floor + 1.0) / 2, 3);
        }
        Pit("N", WooDomain.PitFloorNovice, 0.5);
        Pit("A", WooDomain.PitFloorApprentice, 0.6);
        Pit("J", WooDomain.PitFloorJourneyman, 0.7);
        Pit("M", WooDomain.PitFloorMaster, 0.8);
        Pit("Gm", WooDomain.PitFloorGm, 0.85);

        // The Collier's Mark, the named grant.
        f["markTemp"] = F(Knob(WooDomain.MarkBurnTempBonus, 100), 0);
        f["markLonger"] = F((Knob(WooDomain.MarkBurnDurationMul, 1.2) - 1) * 100, 1);

        return f;
    }

    // ---------------------------------------------------------------- FAR

    /// <summary>Farming's quoted figures: feed via RankLinear (FarPatches' shape), the
    /// Apprentice-and-up levers via FarDomain.BonusT × the GM knob, marks and heirloom
    /// straight off the knobs.</summary>
    private static Dictionary<string, string> FarFigures()
    {
        double K(string k, double d) => FarDomain.Knob(k, d);
        double feedU = K(FarDomain.FeedUntrained, 0.90), feedGm = K(FarDomain.FeedGm, 1.25);
        double thriftGm = K(FarDomain.FertThriftGm, 0.20);
        double graftGm = K(FarDomain.GraftRetryGm, 0.50);
        double procGm = K(FarDomain.HarvestProcGm, 0.20);

        var f = new Dictionary<string, string>
        {
            ["harvestDockU"] = F(K(FarDomain.HarvestDockUntrained, 0.85) * 100, 0),
            ["shearScratchU"] = F(K(FarDomain.ShearScratchUntrained, 1.5), 2),
            ["feedULessPct"] = F((1 - feedU) * 100, 1),
            ["feedStepPct"] = F((feedGm - 1) * 100 / (GM - 1), 2),
            ["feedGmX"] = F(feedGm, 2),
            ["heirloomYield"] = F(K(FarDomain.HeirloomYield, 0.25) * 100, 0),
            ["heirloomGens"] = F(K(FarDomain.HeirloomGenerations, 3), 0),
            ["spoilGrownGm"] = F(K(FarDomain.SpoilGrownGm, 0.70), 2),
            ["thriftGm"] = F(thriftGm * 100, 1),
            ["graftGm"] = F(graftGm * 100, 1),
            ["procGm"] = F(procGm * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["feed" + suf] = F((RankLinear(lv, feedU, feedGm) - 1) * 100, 2);
            f["thrift" + suf] = F(thriftGm * BonusT(lv) * 100, 2);
            f["graft" + suf] = F(graftGm * BonusT(lv) * 100, 2);
            f["proc" + suf] = F(procGm * BonusT(lv) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- ANI

    /// <summary>Animal Handling: treats via RankLinear, the Apprentice-and-up levers via
    /// BonusT, the predator gates straight off their knobs. The genelib 0.6 stock
    /// resistance and the 0.95 hard cap are AniPatches' constants, restated here.</summary>
    private static Dictionary<string, string> AniFigures()
    {
        double K(string k, double d) => AniDomain.Knob(k, d);
        double treatU = K(AniDomain.TreatUntrained, 0.90), treatGm = K(AniDomain.TreatGm, 1.40);
        double purgeGm = K(AniDomain.PurgeBonusGm, 0.30);
        double litterGm = K(AniDomain.LitterProcGm, 0.35);
        double throwGm = K(AniDomain.ThrowHealGm, 0.70);
        const double PurgeBase = 0.6;   // genelib's stock InbreedingResistance (external fact)

        var f = new Dictionary<string, string>
        {
            ["treatU"] = F(treatU, 2),
            ["treatStepPct"] = F((treatGm - 1) * 100 / (GM - 1), 2),
            ["treatGmX"] = F(treatGm, 2),
            ["gateFox"] = F(K(AniDomain.GateFox, 5), 0),
            ["gateWolf"] = F(K(AniDomain.GateWolf, 9), 0),
            ["throwGm"] = F(throwGm * 100, 1),
            ["purgeBase"] = F(PurgeBase, 2),
            ["purgeGmR"] = F(PurgeBase + purgeGm, 2),
            ["litterGm"] = F(litterGm * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["treat" + suf] = F(RankLinear(lv, treatU, treatGm), 3);
            f["throw" + suf] = F(throwGm * BonusT(lv) * 100, 2);
            f["purge" + suf] = F(PurgeBase + purgeGm * BonusT(lv), 3);
            f["litter" + suf] = F(litterGm * BonusT(lv) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- BEE

    /// <summary>Beekeeping: the Axis 1 penalty band is the whole numeric surface of this
    /// build (the bonus band above Novice is deliberately unwritten).</summary>
    private static Dictionary<string, string> BeeFigures() => new()
    {
        ["stingU"] = F(BeeDomain.Knob(BeeDomain.StingUntrained, 1.75), 2),
        ["crushU"] = F(BeeDomain.Knob(BeeDomain.CrushChanceUntrained, 0.35) * 100, 0),
        ["focusGrace"] = F(BeeDomain.Knob(BeeDomain.FocusCooldownSeconds, 5), 0),
    };

    // ---------------------------------------------------------------- FOR

    /// <summary>Foraging: gather yield via RankLinear, stewardship as the ruled
    /// Apprentice-to-GM day lerp, wound/novel-find straight off the knobs.</summary>
    private static Dictionary<string, string> ForFigures()
    {
        double K(string k, double d) => ForDomain.Knob(k, d);
        double yieldU = K(ForDomain.ForageYieldUntrained, 0.9), yieldGm = K(ForDomain.ForageYieldGm, 1.15);
        double tendA = K(ForDomain.TendBoostDaysApprentice, 1.0), tendGm = K(ForDomain.TendBoostDaysGm, 2.5);

        var f = new Dictionary<string, string>
        {
            ["yieldULessPct"] = F((1 - yieldU) * 100, 1),
            ["yieldGmPct"] = F((yieldGm - 1) * 100, 1),
            ["novelFind"] = F(K(ForDomain.NovelFindMultiplier, 4.0), 1),
            ["woundDays"] = F(K(ForDomain.WoundDays, 1.5), 1),
            ["tendGmDays"] = F(tendGm, 2),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["yield" + suf] = F((RankLinear(lv, yieldU, yieldGm) - 1) * 100, 2);
            f["tend" + suf] = F(Lerp(tendA, tendGm, BonusT(lv)), 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- HUN (server side)

    /// <summary>Hunting's server figures: dressing yield and detection distance via
    /// RankLinear, the 2026-08-21 trap redesign straight off its knobs. The Tracker's Eye
    /// and blood-trail figures are CLIENT figures (HunClientFigures).</summary>
    private static Dictionary<string, string> HunFigures()
    {
        double K(string k, double d) => HunDomain.Knob(k, d);
        double dressU = K(HunDomain.AnimalYieldUntrained, 0.70), dressGm = K(HunDomain.AnimalYieldGm, 1.15);
        double seekU = K(HunDomain.SeekRangeUntrained, 1.15), seekGm = K(HunDomain.SeekRangeGm, 0.75);

        var f = new Dictionary<string, string>
        {
            ["dressULessPct"] = F((1 - dressU) * 100, 0),
            ["dressGmPct"] = F((dressGm - 1) * 100, 1),
            ["seekUFartherPct"] = F((seekU - 1) * 100, 0),
            ["seekGmCloserPct"] = F((1 - seekGm) * 100, 0),
            ["trapFailU"] = F(K(HunDomain.TrapFailUntrained, 1.35), 2),
            ["trapFailGm"] = F(K(HunDomain.TrapFailGm, 0.55), 2),
            ["trapStaySetGm"] = F(K(HunDomain.TrapStaySetGm, 0.25) * 100, 0),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["dress" + suf] = F((RankLinear(lv, dressU, dressGm) - 1) * 100, 2);
            f["seek" + suf] = F((1 - RankLinear(lv, seekU, seekGm)) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- HUN (client side)

    /// <summary>The Tracker's Eye geometry (compiled feature constants, hoisted to
    /// HunTrackerEye) and the blood-trail curve (BloodTrail patch formula around the
    /// ConfigLib spreads) — read live on the client, so a ConfigLib tune shows on the
    /// next book open.</summary>
    private static Dictionary<string, string> HunClientFigures()
    {
        // factor(level) = 1 + spread * (level - 9) / 8, Untrained clamped to the Novice
        // floor (HunBloodTrailPatches' anchored curve, stock at Journeyman I).
        double spread = TcmClientSettings.BloodVisibility;
        double Blood(int level) => 1 + spread * (System.Math.Max(1, level) - 9) / 8.0;

        var f = new Dictionary<string, string>
        {
            ["eyeHold"] = F(TcmClientSettings.FocusDelay, 1),
            ["eyeRangeA"] = F(HunTrackerEye.RangeApprentice, 0),
            ["eyeRangeJ"] = F(HunTrackerEye.RangeJourneyman, 0),
            ["eyeRangeM"] = F(HunTrackerEye.RangeMaster, 0),
            ["eyeRangeGm"] = F(HunTrackerEye.RangeGm, 0),
            ["senseA"] = F(HunTrackerEye.RangeApprentice + HunTrackerEye.SenseBonus, 0),
            ["senseJ"] = F(HunTrackerEye.RangeJourneyman + HunTrackerEye.SenseBonus, 0),
            ["senseM"] = F(HunTrackerEye.RangeMaster + HunTrackerEye.SenseBonus, 0),
            ["senseGm"] = F(HunTrackerEye.RangeGm + HunTrackerEye.SenseBonus, 0),
            ["coneDeg"] = F(HunTrackerEye.ConeDegrees, 1),
            ["bloodN1"] = F(Blood(N1), 2),
            ["bloodN4"] = F(Blood(N4), 2),
            ["bloodA1"] = F(Blood(A1), 2),
            ["bloodA4"] = F(Blood(A4), 2),
            ["bloodJ4"] = F(Blood(J4), 2),
            ["bloodM1"] = F(Blood(M1), 2),
            ["bloodM4"] = F(Blood(M4), 2),
            ["bloodGm"] = F(Blood(GM), 2),
        };
        return f;
    }

    // ---------------------------------------------------------------- MET

    /// <summary>Metalworking: the three Novice-anchored curves (shatter, bit recovery,
    /// mold wear) via RankLinear, the fuel economy via the Apprentice-and-up lerp, the
    /// banded maker-quality off MetPatches' own constants, signatures off the knobs.</summary>
    private static Dictionary<string, string> MetFigures()
    {
        double K(string k, double d) => MetPatches.Knob(k, d);
        double shatU = K(MetDomain.ShatterFactorUntrained, 1.5), shatGm = K(MetDomain.ShatterFactorGm, 0.4);
        double bitU = K(MetDomain.BitRecoveryUntrained, 0.7), bitGm = K(MetDomain.BitRecoveryGm, 1.3);
        double moldU = K(MetDomain.MoldWearUntrained, 1.25), moldGm = K(MetDomain.MoldWearGm, 0.6);
        double fuelA = K(MetDomain.FuelEconomyApprentice, 0.03), fuelGm = K(MetDomain.FuelEconomyGm, 0.15);

        var f = new Dictionary<string, string>
        {
            ["overStrike"] = F(K(MetDomain.OverStrikeChance, 0.15) * 100, 0),
            ["moveSlip"] = F(K(MetDomain.MoveSlipChance, 0.05) * 100, 0),
            ["focusGrace"] = F(K(MetDomain.FocusCooldownSeconds, 5), 0),
            ["shatterU"] = F(shatU, 2),
            ["shatterGm"] = F(shatGm, 2),
            ["fuelUMorePct"] = F(-K(MetDomain.FuelEconomyUntrained, -0.10) * 100, 0),
            ["fuelGmPct"] = F(fuelGm * 100, 1),
            ["bitULessPct"] = F((1 - bitU) * 100, 0),
            ["bitGmPct"] = F((bitGm - 1) * 100, 1),
            ["moldUFasterPct"] = F((moldU - 1) * 100, 0),
            ["moldGm"] = F(moldGm, 2),
            ["qualJ"] = F((MetPatches.QualityJourneyman - 1) * 100, 0),
            ["qualM"] = F((MetPatches.QualityMaster - 1) * 100, 0),
            ["qualGm"] = F((MetPatches.QualityGrandmaster - 1) * 100, 0),
            ["gmWearSkip"] = F(K(MetDomain.GmWearSkip, 0.08) * 100, 0),
            ["durableWearSkip"] = F(K(MetDomain.DurableWearSkip, 0.18) * 100, 0),
            ["honedAP"] = F(K(MetDomain.HonedArmorPierce, 1), 0),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["shatter" + suf] = F(RankLinear(lv, shatU, shatGm), 2);
            f["bit" + suf] = F((RankLinear(lv, bitU, bitGm) - 1) * 100, 2);
            f["mold" + suf] = F(RankLinear(lv, moldU, moldGm), 3);
            f["fuel" + suf] = F(Lerp(fuelA, fuelGm, BonusT(lv)) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- MAS

    /// <summary>Masonry: one curve, quoted straight from MasDomain.DressYield — the very
    /// function the drop roll runs on.</summary>
    private static Dictionary<string, string> MasFigures()
    {
        var f = new Dictionary<string, string>
        {
            ["dressU"] = F(MasDomain.DressYield(0) * 100, 0),
            ["dressUWaste"] = F((1 - MasDomain.DressYield(0)) * 100, 0),
            ["dressGmX"] = F(MasDomain.DressYield(GM), 2),
            ["dressGmPct"] = F((MasDomain.DressYield(GM) - 1) * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
            f["dress" + suf] = F((MasDomain.DressYield(lv) - 1) * 100, 2);
        // The per-level step across the climb (Apprentice I to Grandmaster).
        f["dressStepPct"] = F((MasDomain.DressYield(GM) - MasDomain.DressYield(GM - 1)) * 100, 2);
        return f;
    }

    // ---------------------------------------------------------------- ENG

    /// <summary>Engineering: repair and decay quoted from EngDomain.RepairMul/DecayMul —
    /// the functions wearandtear reads — and ignition per tier off the knobs.</summary>
    private static Dictionary<string, string> EngFigures()
    {
        double K(string k, double d) => EngDomain.Knob(k, d);
        var f = new Dictionary<string, string>
        {
            ["repairULessPct"] = F((1 - EngDomain.RepairMul(0)) * 100, 0),
            ["decayU"] = F(EngDomain.DecayMul(0), 2),
            ["repairGmPct"] = F((EngDomain.RepairMul(GM) - 1) * 100, 0),
            ["decayGm"] = F(EngDomain.DecayMul(GM), 2),
            ["igniteU"] = F(K(EngDomain.IgniteUntrained, 0.06) * 100, 1),
            ["igniteN"] = F(K(EngDomain.IgniteNovice, 0.03) * 100, 1),
            ["igniteJ"] = F(K(EngDomain.IgniteJourneyman, 0.02) * 100, 1),
            ["igniteM"] = F(K(EngDomain.IgniteMaster, 0.012) * 100, 1),
            ["igniteGm"] = F(K(EngDomain.IgniteGm, 0.006) * 100, 1),
            ["igniteScaleFloor"] = F(K(EngDomain.IgniteScaleFloor, 0.05), 2),
            ["igniteScaleCap"] = F(K(EngDomain.IgniteScaleCap, 3.0), 1),
            ["gmAssembledDecay"] = F(K(EngDomain.GmAssembledDecay, 0.92), 2),
        };
        foreach (var (suf, lv) in new[] { ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["repair" + suf] = F((EngDomain.RepairMul(lv) - 1) * 100, 2);
            f["decay" + suf] = F(EngDomain.DecayMul(lv), 3);
        }
        return f;
    }

    // ---------------------------------------------------------------- POT

    /// <summary>Pottery: the keep-vessel preservation curve via RankLinear around its two
    /// knobs (quoted as percent-longer-keeping, the way the mark line prints it).</summary>
    private static Dictionary<string, string> PotFigures()
    {
        double preU = PotDomain.Knob(PotDomain.PreserveUntrained, 1.10);
        double preGm = PotDomain.Knob(PotDomain.PreserveGm, 0.85);
        var f = new Dictionary<string, string>
        {
            ["preUFasterPct"] = F((preU - 1) * 100, 0),
            ["preStepPct"] = F((1 - preGm) * 100 / (GM - 1), 2),
            ["preGmPct"] = F((1 - preGm) * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
            f["pre" + suf] = F((1 - RankLinear(lv, preU, preGm)) * 100, 2);
        return f;
    }

    // ---------------------------------------------------------------- GLA

    /// <summary>Glassmaking: every quoted temperature comes from GlaDomain.ShatterThreshold —
    /// the function ShouldShatter tests — so the book and the crack always agree.</summary>
    private static Dictionary<string, string> GlaFigures()
    {
        var f = new Dictionary<string, string>
        {
            ["glaU"] = F(GlaDomain.ShatterThreshold(0), 2),
            ["glaStep"] = F(GlaDomain.ShatterThreshold(1) - GlaDomain.ShatterThreshold(2), 2),
            ["glaGm"] = F(GlaDomain.ShatterThreshold(GM), 2),
        };
        foreach (var (suf, lv) in new[] { ("N1", N1), ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
            f["gla" + suf] = F(GlaDomain.ShatterThreshold(lv), 2);
        return f;
    }

    // ---------------------------------------------------------------- COO

    /// <summary>Cooking: fuel and charring via CooDomain's own RankLinear, the dish edge
    /// and extra serving via its BonusT, class fractions from the Cx table's thirds.</summary>
    private static Dictionary<string, string> CooFigures()
    {
        double K(string k, double d) => CooDomain.Knob(k, d);
        double fuelU = K(CooDomain.FuelUntrained, 0.90), fuelGm = K(CooDomain.FuelGm, 1.15);
        double charU = K(CooDomain.CharUntrained, 1.5), charGm = K(CooDomain.CharGm, 0.5);
        double satGm = K(CooDomain.SatietyGmC3, 0.12), healGm = K(CooDomain.HealthGmC3, 0.05);
        double servGm = K(CooDomain.ServingProcGm, 0.25);

        var f = new Dictionary<string, string>
        {
            ["spoilUFasterPct"] = F((K(CooDomain.SpoilUntrained, 1.15) - 1) * 100, 0),
            ["fuelULessPct"] = F((1 - fuelU) * 100, 0),
            ["charUFasterPct"] = F((charU - 1) * 100, 0),
            ["fuelStepPct"] = F((fuelGm - 1) * 100 / (GM - 1), 2),
            ["charStepPct"] = F((1 - charGm) * 100 / (GM - 1), 2),
            ["cookMarkPct"] = F((1 - K(CooDomain.SpoilGm, 0.70)) * 100, 0),
            ["fuelGmPct"] = F((fuelGm - 1) * 100, 1),
            ["charGmPct"] = F((1 - charGm) * 100, 0),
            ["servingGm"] = F(servGm * 100, 1),
            ["satGm"] = F(satGm * 100, 1),
            ["healGm"] = F(healGm * 100, 1),
            ["satGmC2"] = F(satGm * 2 / 3 * 100, 1),
            ["healGmC2"] = F(healGm * 2 / 3 * 100, 1),
            ["satGmC1"] = F(satGm / 3 * 100, 1),
            ["healGmC1"] = F(healGm / 3 * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["fuel" + suf] = F((CooDomain.RankLinear(lv, fuelU, fuelGm) - 1) * 100, 2);
            f["char" + suf] = F((1 - CooDomain.RankLinear(lv, charU, charGm)) * 100, 2);
            f["serving" + suf] = F(servGm * CooDomain.BonusT(lv) * 100, 2);
            f["sat" + suf] = F(satGm * CooDomain.BonusT(lv) * 100, 2);
            f["heal" + suf] = F(healGm * CooDomain.BonusT(lv) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- BRE

    /// <summary>Brewing: the spoilage taper straight from BreDomain.SpoilChance (the ruled
    /// exception that fades rather than clears), the rest off the knobs — including the
    /// GM good-measure roll, which postdates the website transcription.</summary>
    private static Dictionary<string, string> BreFigures()
    {
        double K(string k, double d) => BreDomain.Knob(k, d);
        var f = new Dictionary<string, string>
        {
            ["spoilU"] = F(BreDomain.SpoilChance(0) * 100, 0),
            ["portionULessPct"] = F((1 - K(BreDomain.PortionUntrained, 0.75)) * 100, 0),
            ["spoilStepPts"] = F(BreDomain.SpoilChance(0) * 100 / Rank.Journeyman, 2),
            ["measureGm"] = F(K(BreDomain.MeasureChanceGm, 0.25) * 100, 0),
            ["measureBonusPct"] = F(K(BreDomain.MeasureBonusFraction, 0.10) * 100, 0),
        };
        foreach (var (suf, lv) in new[] { ("N1", N1), ("N4", N4), ("A1", A1), ("A4", A4) })
            f["spoil" + suf] = F(BreDomain.SpoilChance(lv) * 100, 1);
        return f;
    }

    // ---------------------------------------------------------------- TAI

    /// <summary>Tailoring: wear/warmth/cooling quoted from TaiDomain's own multiplier
    /// functions (emphasis included), fibre from its economy curve.</summary>
    private static Dictionary<string, string> TaiFigures()
    {
        double K(string k, double d) => TaiDomain.Knob(k, d);
        double fibU = K(TaiDomain.FiberEconomyUntrained, 0.90), fibGm = K(TaiDomain.FiberEconomyGm, 1.15);
        // The fibre curve shares WearMul's shape (flat Novice, Apprentice climb); quote it
        // through the same anchors.
        double Fib(int lv) => lv <= 0 ? fibU : lv <= N4 ? 1.0 : 1.0 + (lv - N4) / (double)(GM - N4) * (fibGm - 1.0);

        var f = new Dictionary<string, string>
        {
            ["warmthULessPct"] = F((1 - TaiDomain.WarmthMul(0, TaiDomain.EmphLasting)) * 100, 0),
            ["wearUFasterPct"] = F((TaiDomain.WearMul(0, TaiDomain.EmphWarm) - 1) * 100, 0),
            ["coolULessPct"] = F((1 - TaiDomain.CoolingMul(0, TaiDomain.EmphLasting)) * 100, 0),
            ["fibreULessPct"] = F((1 - fibU) * 100, 0),
            ["wearGmPct"] = F((1 - TaiDomain.WearMul(GM, TaiDomain.EmphWarm)) * 100, 1),
            ["fibreGmPct"] = F((fibGm - 1) * 100, 1),
            ["warmEmphPct"] = F((TaiDomain.WarmthMul(GM, TaiDomain.EmphWarm) - 1) * 100, 1),
            ["lastingWearPct"] = F((1 - TaiDomain.WearMul(GM, TaiDomain.EmphLasting)) * 100, 1),
            ["coolEmphPct"] = F((TaiDomain.CoolingMul(GM, TaiDomain.EmphCool) - 1) * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["wear" + suf] = F((1 - TaiDomain.WearMul(lv, TaiDomain.EmphWarm)) * 100, 2);
            f["fibre" + suf] = F((Fib(lv) - 1) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- ALC

    /// <summary>Alchemy: every curve quoted from AlcDomain's own public functions —
    /// PotencyMul/DurationMul, ReviveFraction (hard-capped), FuelEconomy, and
    /// HerbRackPreserve — so the book and the batch always agree.</summary>
    private static Dictionary<string, string> AlcFigures()
    {
        var f = new Dictionary<string, string>
        {
            ["potULessPct"] = F((1 - AlcDomain.PotencyMul(0, false)) * 100, 0),
            ["potU"] = F(AlcDomain.PotencyMul(0, false), 2),
            ["reviveU"] = F(AlcDomain.ReviveFraction(0) * 100, 0),
            ["fuelUMorePct"] = F(-AlcDomain.FuelEconomy(0) * 100, 0),
            ["potGmPct"] = F((AlcDomain.PotencyMul(GM, false) - 1) * 100, 1),
            ["refundGmPct"] = F(AlcDomain.FuelEconomy(GM) * 100, 1),
            ["herbGmPct"] = F((1 - AlcDomain.HerbRackPreserve(GM)) * 100, 1),
            ["reviveGm"] = F(AlcDomain.ReviveFraction(GM) * 100, 0),
            ["emphBonusX"] = F(1 + AlcDomain.Knob(AlcDomain.EmphasisBonus, 0.10), 2),
            ["emphChosenPct"] = F((AlcDomain.PotencyMul(GM, true) - 1) * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("N1", N1), ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["pot" + suf] = F((AlcDomain.PotencyMul(lv, false) - 1) * 100, 2);
            f["revive" + suf] = F(AlcDomain.ReviveFraction(lv) * 100, 1);
            f["refund" + suf] = F(AlcDomain.FuelEconomy(lv) * 100, 2);
            f["herb" + suf] = F((1 - AlcDomain.HerbRackPreserve(lv)) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- MIN

    /// <summary>Mining: all three Deep-Delver curves plus the cave-in factor via
    /// MinDomain.RankLinear — the same call MinPatches' CaveFactor makes.</summary>
    private static Dictionary<string, string> MinFigures()
    {
        double K(string k, double d) => MinDomain.Knob(k, d);
        double oreU = K(MinDomain.OreYieldUntrained, 0.90), oreGm = K(MinDomain.OreYieldGm, 1.15);
        double stU = K(MinDomain.StaminaUntrained, 1.3), stGm = K(MinDomain.StaminaGm, 0.7);
        double cvU = K(MinDomain.CaveinUntrained, 1.5), cvGm = K(MinDomain.CaveinGm, 0.5);

        var f = new Dictionary<string, string>
        {
            ["oreU"] = F(oreU, 2),
            ["caveUExtraPct"] = F((cvU - 1) * 100, 0),
            ["stamUMorePct"] = F((stU - 1) * 100, 0),
            ["oreGmPct"] = F((oreGm - 1) * 100, 0),
            ["stamGmPct"] = F((1 - stGm) * 100, 0),
            ["saveGm"] = F((1 - MinDomain.RankLinear(GM, cvU, cvGm)) * 100, 0),
            ["stamRatioX"] = F(stU / stGm, 2),
        };
        foreach (var (suf, lv) in new[] { ("N2", N2), ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["ore" + suf] = F((MinDomain.RankLinear(lv, oreU, oreGm) - 1) * 100, 2);
            f["stam" + suf] = F((1 - MinDomain.RankLinear(lv, stU, stGm)) * 100, 2);
            f["save" + suf] = F((1 - MinDomain.RankLinear(lv, cvU, cvGm)) * 100, 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- PAN

    /// <summary>Panning: yield via PanDomain.RankLinear, the placer trace and treasure
    /// tail via the domain's own TraceStrengthFor/TreasureBiasFor.</summary>
    private static Dictionary<string, string> PanFigures()
    {
        double K(string k, double d) => PanDomain.Knob(k, d);
        double yU = K(PanDomain.PanYieldUntrained, 0.85), yGm = K(PanDomain.PanYieldGm, 1.25);

        var f = new Dictionary<string, string>
        {
            ["yieldU"] = F(yU, 2),
            ["yieldULessPct"] = F((1 - yU) * 100, 0),
            ["yieldGmPct"] = F((yGm - 1) * 100, 0),
            ["yieldStepPts"] = F((yGm - 1) * 100 / (GM - 1), 2),
            ["traceGm"] = F(PanDomain.TraceStrengthFor(GM) * 100, 0),
            ["treasThresholdPct"] = F(K(PanDomain.TreasureChanceThreshold, 0.01) * 100, 0),
            ["treasM1"] = F(PanDomain.TreasureBiasFor(M1), 2),
            ["treasM4"] = F(PanDomain.TreasureBiasFor(M4), 2),
            ["treasGm"] = F(PanDomain.TreasureBiasFor(GM), 2),
            ["treasNetM4"] = F(PanDomain.TreasureBiasFor(M4) * PanDomain.RankLinear(M4, yU, yGm), 2),
            ["treasNetGm"] = F(PanDomain.TreasureBiasFor(GM) * PanDomain.RankLinear(GM, yU, yGm), 2),
        };
        foreach (var (suf, lv) in new[] { ("N2", N2), ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["yield" + suf] = F((PanDomain.RankLinear(lv, yU, yGm) - 1) * 100, 2);
            f["trace" + suf] = F(PanDomain.TraceStrengthFor(lv) * 100, 1);
        }
        return f;
    }

    // ---------------------------------------------------------------- FIS

    /// <summary>Fishing: every curve via FisDomain's own functions — EscapeChanceFor,
    /// SkewFor, RoeMultiplierFor, and RankLinear for the depletion step.</summary>
    private static Dictionary<string, string> FisFigures()
    {
        double K(string k, double d) => FisDomain.Knob(k, d);
        double skU = K(FisDomain.SizeSkewUntrained, -0.15), skGm = K(FisDomain.SizeSkewGm, 0.35);
        double depU = K(FisDomain.DepletionUntrained, 1.5), depGm = K(FisDomain.DepletionGm, 0.5);

        var f = new Dictionary<string, string>
        {
            ["escU"] = F(FisDomain.EscapeChanceFor(0) * 100, 0),
            ["escN1"] = F(FisDomain.EscapeChanceFor(N1) * 100, 1),
            ["escGm"] = F(FisDomain.EscapeChanceFor(GM) * 100, 1),
            ["skewULessPts"] = F(-skU * 100, 0),
            ["skewGmPts"] = F(skGm * 100, 0),
            ["depU"] = F(depU, 2),
            ["depGm"] = F(depGm, 2),
            ["roeGm"] = F(FisDomain.RoeMultiplierFor(GM), 2),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["esc" + suf] = F(FisDomain.EscapeChanceFor(lv) * 100, 1);
            f["skew" + suf] = F(FisDomain.SkewFor(lv, skU, skGm) * 100, 1);
            f["dep" + suf] = F(FisDomain.RankLinear(lv, depU, depGm), 2);
            f["roe" + suf] = F(FisDomain.RoeMultiplierFor(lv), 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- MEL

    /// <summary>Melee: armor and damage via MelDomain's Novice-anchored curves, the parry
    /// grace TRUNCATED exactly as MelParryPatches' (long) cast does, pierce depth and tier
    /// bonus from the domain's own integer functions.</summary>
    private static Dictionary<string, string> MelFigures()
    {
        double K(string k, double d) => MelDomain.Knob(k, d);
        double armU = K(MelDomain.ArmorUntrained, 0.30), armGm = K(MelDomain.ArmorGm, -0.50);
        double graceGm = K(MelDomain.ParryGraceGmMs, 180);
        double GraceMs(int lv) => Math.Floor(MelDomain.NoviceDelta(lv, 0, graceGm));

        var f = new Dictionary<string, string>
        {
            ["dmgU"] = F(K(MelDomain.DamageUntrained, 0.85) * 100, 0),
            ["armorU"] = F(1 + armU, 2),
            ["armorUMorePct"] = F(armU * 100, 0),
            ["armorGm"] = F(1 + armGm, 2),
            ["graceGm"] = F(graceGm, 0),
            ["perfectWindow"] = F(K(MelDomain.PerfectWindowMs, 150), 0),
            ["riposteWindow"] = F(K(MelDomain.RiposteWindowMs, 300), 0),
            ["pierceJ1"] = F(MelDomain.PierceDepth(J1), 0),
            ["pierceJ4"] = F(MelDomain.PierceDepth(J4), 0),
            ["pierceM1"] = F(MelDomain.PierceDepth(M1), 0),
            ["pierceM4"] = F(MelDomain.PierceDepth(M4), 0),
            ["pierceGm"] = F(MelDomain.PierceDepth(GM), 0),
            ["blockTierBonus"] = F(K(MelDomain.BlockTierBonus, 1), 0),
            ["blockTierRank"] = Domain.RankName((int)K(MelDomain.BlockTierLevel, 13)),
        };
        foreach (var (suf, lv) in new[] { ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["armor" + suf] = F(1 + MelDomain.NoviceDelta(lv, armU, armGm), 2);
            f["grace" + suf] = F(GraceMs(lv), 0);
        }
        return f;
    }

    // ---------------------------------------------------------------- RAN

    /// <summary>Ranged: everything through RanDomain's own ApprenticeAnchored curve and the
    /// misfire/thrift/spill functions — vanilla parity is Apprentice here, by ruling.</summary>
    private static Dictionary<string, string> RanFigures()
    {
        double K(string k, double d) => RanDomain.Knob(k, d);
        double stU = K(RanDomain.SteadyAimUntrained, 0.50), stGm = K(RanDomain.SteadyAimGm, 1.35);
        double reU = K(RanDomain.ReloadUntrained, 0.75), reGm = K(RanDomain.ReloadGm, 1.12);
        double rcU = K(RanDomain.RecoveryUntrained, 0.80), rcGm = K(RanDomain.RecoveryGm, 1.50);

        var f = new Dictionary<string, string>
        {
            ["steadyU"] = F(stU, 2),
            ["handleU"] = F(reU, 2),
            ["handleULessPct"] = F((1 - reU) * 100, 0),
            ["recovU"] = F(rcU, 2),
            ["vanAccULessPct"] = F((1 - K(RanDomain.VanAccUntrained, 0.90)) * 100, 0),
            ["vanAccGm"] = F(K(RanDomain.VanAccGm, 1.05), 2),
            ["steadyGm"] = F(stGm, 2),
            ["handleGm"] = F(reGm, 2),
            ["recovGm"] = F(rcGm, 2),
            ["recovCapPct"] = F(K(RanDomain.RecoveryCap, 0.90) * 100, 0),
            ["misfireU"] = F(RanDomain.MisfireChance(0) * 100, 0),
            ["misfireA1"] = F(RanDomain.MisfireChance(A1) * 100, 1),
            ["misfireGm"] = F(RanDomain.MisfireChance(GM) * 100, 1),
            ["spillU"] = F(RanDomain.SpillChance(0) * 100, 0),
            ["spillN1"] = F(RanDomain.SpillChance(N1) * 100, 0),
            ["spillN4"] = F(RanDomain.SpillChance(N4) * 100, 0),
            ["thriftGm"] = F(RanDomain.ThriftChance(GM) * 100, 0),
        };
        foreach (var (suf, lv) in new[] { ("N1", N1), ("N4", N4), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["steady" + suf] = F(RanDomain.ApprenticeAnchored(lv, stU, stGm), 2);
            f["handle" + suf] = F(RanDomain.ApprenticeAnchored(lv, reU, reGm), 2);
            f["recov" + suf] = F(RanDomain.ApprenticeAnchored(lv, rcU, rcGm), 2);
            f["misfire" + suf] = F(RanDomain.MisfireChance(lv) * 100, 1);
            f["thrift" + suf] = F(RanDomain.ThriftChance(lv) * 100, 1);
        }
        return f;
    }

    // ---------------------------------------------------------------- ARC

    /// <summary>Arcana: pools straight from ArcDomain.ManaFloor (the ratified anchors),
    /// regen/drain/school-familiarity from the domain's own functions.</summary>
    private static Dictionary<string, string> ArcFigures()
    {
        var f = new Dictionary<string, string>
        {
            ["poolU"] = F(ArcDomain.ManaFloor(0), 0),
            ["poolGm"] = F(ArcDomain.ManaFloor(GM), 0),
            ["regenGm"] = F(ArcDomain.ManaRegenBonus(GM), 1),
            ["drainGm"] = F(ArcDomain.DrainMul(GM), 2),
            ["famStep2"] = F(ArcDomain.SchoolFamThreshold(2), 0),
            ["famMaster"] = F(ArcDomain.SchoolFamThreshold(5), 0),
            ["famStepPct"] = F(-ArcDomain.SchoolCostDelta(2) * 100, 2),
            ["famMasterPct"] = F(-ArcDomain.SchoolCostDelta(5) * 100, 1),
            ["backfireDrainPct"] = F(ArcDomain.Knob(ArcDomain.BackfireDrainPerTier, 0.08) * 100, 0),
            ["backfireResidualPct"] = F(ArcDomain.Knob(ArcDomain.BackfireGmResidual, 0.025) * 100, 1),
        };
        foreach (var (suf, lv) in new[] { ("N1", N1), ("N4", N4), ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["pool" + suf] = F(ArcDomain.ManaFloor(lv), 0);
            f["regen" + suf] = F(ArcDomain.ManaRegenBonus(lv), 1);
            f["drain" + suf] = F(ArcDomain.DrainMul(lv), 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- TEM

    /// <summary>Temporal: gear cost, ward fuel and stability through TemDomain's own curves;
    /// the retuned storm cue (2026-08-21: the ladder IS the warning) in real seconds per the
    /// live calendar constant; ward days against vanilla's 21-day gear.</summary>
    private static Dictionary<string, string> TemFigures()
    {
        const double VanillaWardDays = 21.0;   // one temporal gear's stock warding
        double cueGmDays = TemDomain.Knob(TemDomain.StormCueLeadGm, 0.35);
        // In-game day = 48 real minutes at stock calendar; the cue ladder is authored in
        // real seconds (TemDomain.NoviceILeadRealSeconds .. the GM knob).
        double cueGmRealMin = cueGmDays * 48.0;
        double cueStepRealSec = (cueGmRealMin * 60.0 - TemDomain.NoviceILeadRealSeconds) / (GM - 1);

        int repairGate = AlmanacTcmModSystem.ServerInstance?.GlobalConfig?.RepairGateTEMLevel ?? 0;

        var f = new Dictionary<string, string>
        {
            ["gearCostU"] = F(TemDomain.GearCost(0), 2),
            ["gearCostGm"] = F(TemDomain.GearCost(GM), 2),
            ["wardDaysU"] = F(VanillaWardDays * TemDomain.WardFuel(0), 1),
            ["wardDaysGm"] = F(VanillaWardDays * TemDomain.WardFuel(GM), 1),
            ["stabU"] = F(TemDomain.StabilityLossMul(0), 2),
            ["stabUMorePct"] = F((TemDomain.StabilityLossMul(0) - 1) * 100, 0),
            ["stabGm"] = F(TemDomain.StabilityLossMul(GM), 2),
            ["manifestResistGm"] = F(TemDomain.ManifestResistChance(GM) * 100, 0),
            ["cueN1RealSec"] = F(TemDomain.NoviceILeadRealSeconds, 0),
            ["cueStepRealSec"] = F(cueStepRealSec, 0),
            ["cueGmRealMin"] = F(cueGmRealMin, 1),
            ["repairGateRank"] = repairGate > 0 ? Domain.RankName(repairGate) : "open to all",
        };
        foreach (var (suf, lv) in new[] { ("A1", A1), ("A4", A4), ("J1", J1), ("J4", J4), ("M1", M1), ("M4", M4) })
        {
            f["ward" + suf] = F(VanillaWardDays * TemDomain.WardFuel(lv), 1);
            f["stab" + suf] = F(TemDomain.StabilityLossMul(lv), 2);
            f["gearCost" + suf] = F(TemDomain.GearCost(lv), 2);
        }
        return f;
    }

    // ---------------------------------------------------------------- helpers

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>The felling lean as a directional phrase ("21.6 degrees toward you",
    /// "5.2 degrees away from you", "dead level"). Positive = toward the feller, matching
    /// the knob's sign convention.</summary>
    private static string LeanPhrase(double deg)
    {
        if (Math.Abs(deg) < 0.05) return "dead level";
        return deg > 0
            ? $"{F(deg, 1)} degrees toward you"
            : $"{F(-deg, 1)} degrees away from you";
    }

    /// <summary>Full-precision-then-trim formatting (user decision: never round away real
    /// precision; maxDecimals bounds noise, trailing zeros drop). Invariant culture — these
    /// strings ride the wire.</summary>
    private static string F(double v, int maxDecimals)
    {
        string format = maxDecimals <= 0 ? "0" : "0." + new string('#', maxDecimals);
        return Math.Round(v, maxDecimals).ToString(format, CultureInfo.InvariantCulture);
    }
}

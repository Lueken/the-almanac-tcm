namespace AlmanacTcm.Config;

/// <summary>
/// Global engine knobs (xp-engine-design.md §9; defaults are the LOCKED §10 rulings).
/// Loaded SERVER-SIDE ONLY from ModConfig/almanactcm/global.json and never synced to
/// clients — client sync carries state (rank, pending %), never the constants that
/// produced it. Servers may diverge from these shipped defaults invisibly.
/// </summary>
public class TcmGlobalConfig
{
    public int ConsolidationHour { get; set; } = 3;

    /// <summary>Lifetime GM-domain cap (guard #12). Lowering never revokes attained GMs.</summary>
    public int GmDomainCap { get; set; } = 2;

    /// <summary>Fraction of pending (unconsolidated) practice scattered on death.</summary>
    public double LambdaDeath { get; set; } = 0.5;

    /// <summary>Separate scatter fraction for PvP deaths (default = LambdaDeath).</summary>
    public double LambdaPvp { get; set; } = 0.5;

    /// <summary>In-game hours after a penalized death during which further deaths cost nothing.</summary>
    public double ChainDeathCooldownHours { get; set; } = 2.0;

    /// <summary>Adjacency spillover share of neighbours' banked XP.</summary>
    public double Sigma { get; set; } = 0.05;

    /// <summary>Spillover received per day caps at this % of the receiving domain's Smax.</summary>
    public double SpilloverCapPct { get; set; } = 25.0;

    /// <summary>Depth phase: saturated contribution weight of non-dominant techniques.</summary>
    public double DepthOffTechniqueWeight { get; set; } = 0.25;

    /// <summary>Rolling window (in-game days) that elects the depth-phase dominant technique.</summary>
    public int DominantWindowDays { get; set; } = 7;

    /// <summary>Identical practice contexts inside this window log zero raw practice
    /// (the place-and-rebreak guard).</summary>
    public double DedupWindowSeconds { get; set; } = 90.0;

    public int DedupRingSize { get; set; } = 64;

    /// <summary>Send each practice gain (and dedup'd repeat) to the player's Info
    /// tab. Trial instrumentation; consider off once the engine is trusted.</summary>
    public bool PracticeGainMessages { get; set; } = true;

    /// <summary>Emit the per-gain HUD toast packet (client decides whether and how to
    /// draw it via ConfigLib). Off = no packets leave the server at all.</summary>
    public bool PracticeGainToasts { get; set; } = true;

    public bool VerboseDebugLogging { get; set; } = true;

    /// <summary>MET material gate (§162 Axis 5): below the required MET rank a player
    /// cannot smelt, form, or cast a metal above their tier. Assembly is never gated.
    /// Server-owned and bespoke to MET so other domains get their own toggle later
    /// (materialGateALC, …). Set false to disable the MET gate entirely.</summary>
    public bool MaterialGateMET { get; set; } = true;

    /// <summary>Enforcement for a gated attempt: false = block the interaction with a
    /// warning (default, no accidental material loss); true = HARDCORE, let it through
    /// and waste the material.</summary>
    public bool MaterialGateMETHardcore { get; set; } = false;

    /// <summary>Required MET level for a metal not in the classification map (a mod-added
    /// metal). Default 0 = allow and log once, so nothing is silently locked; raise it to
    /// gate unknowns conservatively.</summary>
    public int MaterialGateMETUnmappedLevel { get; set; } = 0;

    /// <summary>Ambient storm-warning shift (ruled 2026-08-21): the first storm warning (Temporal
    /// Symphony cues, or the vanilla chat line without TS) is delivered per player by TEM rank
    /// instead of broadcast at 0.35 days. False restores stock broadcast behavior everywhere.</summary>
    public bool StormShiftTEM { get; set; } = true;

    /// <summary>TEM repair gate (third-pass ruling 2026-08-21): below this TEM level a player cannot
    /// repair a translocator or recharge a discharged teleporter. Transit is never gated; anyone
    /// steps through a working machine. Default 4 = Novice IV; 0 disables the gate; season tuning
    /// may raise it toward Apprentice I (5) if rust-mob practice trivializes the climb.</summary>
    public int RepairGateTEMLevel { get; set; } = 4;

    /// <summary>POT broad-stroke gates (B-walk ruling 2026-08-21): below these POT levels the
    /// clayforming 2x2 and 3x3 tool modes are refused (adding and removing alike); the 1x1
    /// stroke and the powered wheel are never gated, and the duplicate-layer stroke is scaled
    /// by rank rather than gated. Defaults 5 = Apprentice I and 9 = Journeyman I; 0 disables.</summary>
    public int Place2x2GatePOTLevel { get; set; } = 5;
    public int Place3x3GatePOTLevel { get; set; } = 9;

    /// <summary>COO oven gate (ruled in the LR chat; built 2026-08-22): below this COO level
    /// the Stone Bake Oven is a full interaction block. No cooking, no adding a pan or cook
    /// pot, no loading firewood; a partially usable oven wastes the fuel. The vanilla clay
    /// oven is never gated. Default 9 = Journeyman I (the iron-age rung); 0 disables.</summary>
    public int OvenGateCOOLevel { get; set; } = 9;

    /// <summary>FIS gear gate (ruled 2026-08-22): below this FIS level a player cannot SET
    /// Ithania's refined fishing gear (place the fish trap, swing the net). Servicing an
    /// already-placed trap is never gated, and the bait economy, fillet knife, and logbook stay
    /// open. Default 5 = Apprentice I (the ENG rig-gate rank); 0 disables the gate.</summary>
    public int GearGateFISLevel { get; set; } = 5;

    /// <summary>Alloy Ledger (§162 Axis 4, Apprentice unlock) access. true = only an Apprentice+ of
    /// Metalworking can open it on a crucible (the ruled default); false = any player can, for
    /// servers that want the convenience for everyone. Server-owned and synced to clients on
    /// join, since the ledger opens client-side.</summary>
    public bool AlloyLedgerGated { get; set; } = true;

    /// <summary>The Grower's Eye master toggle (FAR ruled 2026-08-22). true = farmland and crop
    /// hover info is gated by the viewer's FAR rank and per-crop familiarity (Untrained sees
    /// nothing, rough words from Novice, full figures from Apprentice with the crop Versed);
    /// false = vanilla shows everything to everyone. Synced to clients on join, since the
    /// readout renders client-side. The rank rungs themselves are the Rank constants, not
    /// knobs: Novice rough, Apprentice full, Journeyman family-wide.</summary>
    public bool GrowerEyeFAR { get; set; } = true;

    /// <summary>Crop familiarity thresholds (the Grower's Eye data layer, ruled 2026-08-22;
    /// RECALIBRATED 2026-08-24): credited harvest DAYS with a crop before it is Acquainted
    /// (rough readout) and Versed (full readout). Counters live in the synced Knowledge store
    /// as far-crop-(id).
    ///
    /// The unit changed from tiles to days, and that is the whole point. Counting tiles meant a
    /// wild find that dropped five seeds taught you a crop in one growing cycle while a find
    /// that dropped three took two, for identical effort: the pace was set by seed luck rather
    /// than by anything a player could reason about. Harvesting ten tiles in one afternoon is
    /// one observation of one lifecycle, not ten. See FamMaxCreditsPerDay.
    ///
    /// Calibration on The Quire's 30-day months: garlic runs 1.8 months (54 days) with an
    /// Apr-Sep window, so about three cycles an in-game year. A grower who staggers plantings
    /// harvests across several days and earns three or four credits a cycle where one sown all
    /// at once earns one, so Versed lands around a year of focused growing for the former and
    /// two or three for the latter. That gap is deliberate: staggering is better practice, and
    /// this is the one thing that rewards it. Acreage rewards nothing.</summary>
    public int FamAcquaintedHarvests { get; set; } = 2;
    public int FamVersedHarvests { get; set; } = 8;

    /// <summary>Credits a single crop can earn in one in-game day, however much of it is brought
    /// in. One mark on the page is one day you brought that crop in, which is why a hundred
    /// tiles and one tile teach the same amount. Raise it to soften the calendar; it can never
    /// make bed size matter beyond this many tiles.</summary>
    public int FamMaxCreditsPerDay { get; set; } = 1;

    /// <summary>Family knowledge: effective familiarity with a crop = its own harvest count plus
    /// FamSpread times the summed counts of its family-mates (crop-families.json taxonomy).
    /// Knowledge of one legume teaches you something about all legumes, never everything.
    /// The family-wide Journeyman read opens when the family's summed counters (own included)
    /// reach FamFamilyVersedSum.</summary>
    public double FamSpread { get; set; } = 0.5;
    public int FamFamilyVersedSum { get; set; } = 16;

    /// <summary>Ceiling on what kin alone can carry, in effective count. "Never everything" was
    /// in the ruling and was not in the arithmetic: at the shipped thresholds a family summing
    /// to fifty made every crop in it Versed for a player who had never planted one. Capping
    /// the kin term one short of Versed keeps the good half (kin can make a crop Acquainted, so
    /// the family spread is still the mechanic worth finding) and closes the bad half (the
    /// exact figures stay something you earn on that plant). Any experience of your own adds on
    /// top of the cap and pushes straight through it.</summary>
    public int FamKinCeiling { get; set; } = 7;

    /// <summary>Familiarity counter ceiling per crop, purely to bound the synced store; far past
    /// every threshold at the default.</summary>
    public int FamCountCap { get; set; } = 500;

    // ---------------------------------------------------------------- soil sickness (RULED 2026-08-24)

    /// <summary>The reason to rotate. False leaves soil exactly as vanilla and the mods left it,
    /// which on 30-day months means no rotation pressure of any kind.</summary>
    public bool SoilSicknessFAR { get; set; } = true;

    /// <summary>Level added to a tile by one harvest DAY of the family already sick in it.
    ///
    /// The three constants below are a set, and the ruling lives in their ratio rather than in
    /// any one of them. Accrual must sit between one and a half and three cycles' worth of
    /// occupied decay: below that a two-course rotation cleans up and there is no reason to run
    /// three or four, above it a four-course rotation cannot keep up and rotation stops working
    /// at all. At the shipped values, on 54-day garlic cycles: monoculture is felt inside two
    /// cycles and maxed within a year; A-B-A-B creeps about six points a pair and starts biting
    /// after roughly eight cycles; A-B-C and A-B-C-D decline and stay clean forever.</summary>
    public double SickAccrualPerHarvest { get; set; } = 34;

    /// <summary>Level shed per in-game day by bare ground.</summary>
    public double SickFallowDecayPerDay { get; set; } = 0.35;

    /// <summary>Share of the bare-ground decay rate that ground under a crop gets. Below 1 so
    /// fallow is always the faster cure, near 1 so rotation is nearly as good AND pays a
    /// harvest, which is what makes rotation the right answer and fallow the last resort.</summary>
    public double SickOccupiedDecayFactor { get; set; } = 0.75;

    /// <summary>
    /// Nothing is felt below this, and it MUST sit above SickAccrualPerHarvest. That is not a
    /// style preference, it is the constraint that makes rotation work at all: at 20 against an
    /// accrual of 34, a single harvest cleared the line, so even a flawless four-course rotation
    /// was bitten on every one of its A cycles. Above the accrual, one harvest is always free and
    /// only repetition costs anything.
    ///
    /// Verified on 54-day cycles at the shipped constants: monoculture first bites on cycle two
    /// and maxes by five; A-B-A-B first bites on cycle five and reaches the ceiling over roughly
    /// three in-game years; A-B-C and A-B-C-D peak at 34 and never bite at all; crop-then-fallow
    /// creeps slowest of the repeating patterns and first bites around cycle fifteen.
    /// </summary>
    public double SickCleanBelow { get; set; } = 40;

    /// <summary>Worst-case growth-speed penalty, multiplying with vanilla's nutrient speed bands.
    /// Capped well above zero on purpose: a tile that can never grow anything again is a dead
    /// square on someone's farm forever, and they will simply re-till and move on.</summary>
    public double SickMaxSpeedPenalty { get; set; } = 0.40;

    /// <summary>Worst-case yield penalty. Deliberately gentler than the speed penalty, so a sick
    /// tile is a slow disappointment rather than two punishments for one mistake.</summary>
    public double SickMaxYieldPenalty { get; set; } = 0.25;
}

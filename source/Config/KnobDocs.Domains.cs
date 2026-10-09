using System.Collections.Generic;

namespace AlmanacTcm.Config;

/// <summary>
/// The per-domain half of <see cref="KnobDocs"/>: one operator-facing line per technique and per
/// Bonus knob, keyed "CODE/key". Split from KnobDocs.cs so the infrastructure stays readable and
/// this file can be reviewed as what it is - prose.
///
/// Both maps are keyed by the SHIPPED STRING, not the C# const, because that is what an operator
/// sees in the JSON and what <see cref="KnobDocs.Stamp(DomainConfig)"/> looks up. When you rename
/// a knob key, rename it here too; the boot warning will tell you if you forget.
///
/// Two families are documented by GENERATION rather than by an entry here, in
/// <c>KnobDocs.BonusHelpFor</c>: the per-technique `*FloorPerRaw` tail floors (18 of them, same
/// text with the technique name swapped) and ARC's per-ritual `ritualRaw*` pay (18 more). Adding a
/// technique floor or an RBM ritual therefore needs nothing here.
///
/// House style for a line is at the top of KnobDocs.cs. Keep them one line, plain ASCII, units
/// first, off-value last.
/// </summary>
public static partial class KnobDocs
{
    /// <summary>Technique -> the act that earns the credit. Keyed "CODE/technique".</summary>
    private static readonly Dictionary<string, string> Technique = new()
    {
        // ---------------------------------------------------------------- ALC, Alchemy
        ["ALC/remedy"] = "Grid-crafting a healing item: poultices, bandages. The vanilla floor, cheap and repeatable.",
        ["ALC/chemistry"] = "Finishing a wet-chemistry reaction in an industrialstory reaction vessel. Per session, not per output.",
        ["ALC/reagentwork"] = "Processing alchemical matter at a quern or in a pot (anything tagged tcmCraftDomain=ALC). Always ALC, never COO.",

        // ---------------------------------------------------------------- ANI, Animal Handling
        ["ANI/genraising"] = "An unattended BIRTH one generation above the dam. Credited to whoever last fed or tamed the mother, and the raw is scaled by the newborn's generation - see genRaiseStep.",
        ["ANI/taming"] = "Taking an animal from wild or feral to TAME: the petai feed-to-domesticate transition, or the vanilla saddle-break. Partial progress banks nothing.",

        // ---------------------------------------------------------------- ARC, Arcana
        ["ARC/foundational"] = "Casting an innate or general spell with no school of its own. Scroll casts never earn ARC, so buying scrolls teaches nothing.",
        ["ARC/evocation"] = "Casting an Evocation spell. Credited at the mana spend, so cost is the practice.",
        ["ARC/alteration"] = "Casting an Alteration spell. Credited at the mana spend.",
        ["ARC/incantation"] = "Casting an Incantation spell. Credited at the mana spend.",
        ["ARC/conjuration"] = "Casting a Conjuration spell. Credited at the mana spend.",
        ["ARC/meditation"] = "Meditating. Raw 1 is the UNIT that the outcome-scaled payoff multiplies, so tune the trance through meditationTranceRaw rather than through this Raw.",
        ["ARC/laboratory"] = "Station ritual work: Spellforge research, a world ritual, an oculus or foundry product. A completed world ritual is paid separately through its own ritualRaw* knob.",
        ["ARC/inscription"] = "Scribing a scroll. The market verb: what a mage sells to people who cannot cast.",
        ["ARC/arcanekill"] = "Killing a Rustbound creature, as a 50% co-grant beside the MEL or RAN credit for the same corpse. Greater hostiles pay more, see arcaneKillGreaterMul.",

        // ---------------------------------------------------------------- BEE, Beekeeping
        ["BEE/hiving"] = "Putting a colony into a box: populating a hive, or retrieving stock. Rare and high-judgment, so one real act banks most of the share.",
        ["BEE/wintering"] = "Feeding frames in before the cold (Oreki hives only). Rare and seasonal.",
        ["BEE/combwork"] = "The practice spine: filled frames out, ripe skeps down. Larger K so an eight-frame pull saturates rather than paying eight times over.",
        ["BEE/rendering"] = "Running honeycomb through the fruit press. The vanilla-floored end of the chain.",

        // ---------------------------------------------------------------- BRE, Brewing & Fermentation
        ["BRE/fermenting"] = "Sealing a barrel or clay fermenter. Credited to whoever sealed it, banked when it completes days later. Contexts key on the OUTPUT type, so ten cider barrels bank as a few contexts rather than ten.",
        ["BRE/distilling"] = "Running a boiler and condenser to spirits. Credited at the ignite, banked at the distillate.",

        // ---------------------------------------------------------------- COO, Cooking
        ["COO/mealpot"] = "A cooking pot finishing a meal. Credited to whoever loaded it.",
        ["COO/directheat"] = "Cooking straight over heat, a chop on a stick. The one verb every player of every class does daily at zero investment, which is why its Raw is the lowest on the table.",
        ["COO/mixing"] = "An ACA mixing bowl completing its mix.",
        ["COO/milling"] = "Cranking a quern. Pays COO 50 / FAR 50 at half raw each side, so flour credits the cook and the farmer. A windmill-driven quern earns NOTHING: nobody is attending it.",
        ["COO/baking"] = "An oven completing a bake (vanilla clay oven or the Stone Bake Oven).",
        ["COO/griddling"] = "A seafarer griddle hearth completing its cook.",
        ["COO/juicing"] = "Working a fruit press.",
        ["COO/prep"] = "Assembling on a seafarer prep table.",
        ["COO/pastry"] = "Closing a pie with its top crust. Credited ONCE at the crust, not per filling, so it cannot be farmed by adding and removing ingredients.",
        ["COO/simmering"] = "An ACA saucepan completing its simmer.",
        ["COO/drying"] = "A drying rack or ACA meat hook completing a cycle. Per session: one rack load is most of the share.",
        ["COO/salting"] = "A seafarer salt pan completing its evaporation. Per session.",

        // ---------------------------------------------------------------- ENG, Engineering
        ["ENG/assembly"] = "Rigging sail cloth onto a placed windmill rotor: the cloth is consumed and the sail grows. The genuine assembly signal, not mere placement. Millwright rotors pool into the same verb.",
        ["ENG/milestone"] = "First construction of a machine TYPE, paid at its first power or output. Repeats of a type halve, so Raw 20 is the full first-of-type credit and K only guards a same-day building spree.",
        ["ENG/maintenance"] = "Servicing a disengaged wearing part (wearandtear): sewing, waxing, resining, nailing it back. Vanishes with the mod.",

        // ---------------------------------------------------------------- FAR, Farming & Husbandry
        ["FAR/tilling"] = "Working ground with a hoe.",
        ["FAR/planting"] = "Putting a seed into farmland.",
        ["FAR/harvesting"] = "Breaking a crop, paid in proportion to its ripeness. Below harvestRipeFloor it banks nothing at all, and a scythe pays a premium - see harvestScytheBonus.",
        ["FAR/fertilizing"] = "Applying fertiliser to farmland.",
        ["ANI/feeding"] = "A trough consuming a portion. Moved from FAR (LGD-122): feeding is the handler's act. Also writes the raisedBy owner stamp that the unattended birth reads, which is how the handler gets credited for a birth they were not present for.",
        ["ANI/riding"] = "Ground actually covered in the saddle, paid per rideCreditBlocks of real displacement (LGD-176). A horse pushed against a wall moves nowhere and earns nothing; teleports and lag snaps pay zero. Controlling seat only.",
        ["FAR/milking"] = "Completing a milking.",
        ["FAR/eggs"] = "Collecting from a hen box. The weakest and spammiest row, with heavy dedup so a coop sweep reads as one act.",
        ["FAR/grafting"] = "Propagating a fruit tree from a graft. Risky, deliberate and annual.",
        ["FAR/cuttings"] = "Taking a cutting off a berry bush. Its own verb at half a graft's raw, because taking one is riskless where rooting one is not; vanilla's once-per-bush-per-year gate is the anti-farm.",
        ["FAR/orchard"] = "Working a fruit tree part: pruning, picking at the branch.",
        ["FAR/beekeeping"] = "Harvesting a skep. Stands down automatically when the BEE domain is active, so the two can never double-grant.",
        ["FAR/shearing"] = "Shearing an animal (shearlib). An Untrained hand wounds more often, see shearScratchUntrained.",
        ["FAR/butchery"] = "Dressing an animal the player raised, as an even 50/50 split with HUN dressing.",
        ["FAR/furrow"] = "Working a primitivesurvival furrow.",
        ["FAR/vermiculture"] = "Maintaining an Ithania worm bin.",
        ["FAR/milling"] = "FAR's half of the quern split. COO's listener grants both sides at half raw each; flour is farm produce too.",

        // ---------------------------------------------------------------- FIS, Fishing
        ["FIS/angling"] = "Rod and line: a cast that lands a catch, junk included. An empty reel pays nothing, and vanilla's own depletion of the spot is the built-in anti-farm.",
        ["FIS/spearing"] = "Landing a fish with a fishing spear, credited at the retrieve.",
        ["FIS/trapping"] = "Collecting from a basket, weir, trotline or Ithania trap. Credited to whoever PLACED it. Per session: one sweep of a trapline is most of the share.",
        ["FIS/processing"] = "Dressing the catch, filleting. Batch work dedups inside the window.",

        // ---------------------------------------------------------------- FOR, Foraging
        ["FOR/harvesting"] = "An in-place pluck that leaves the plant standing: berry bush, resin, reeds.",
        ["FOR/gathering"] = "A destructive break: uprooting a plant, a mushroom, surface litter. The spam floor, so its raw is the lowest here, with a hard position bucket and the repeat decay on top.",
        ["FOR/tapping"] = "Driving a spile into a trunk. Paid once per trunk face FOR EVER: siting a tapline is the skilled act, running one is not. Nothing accrues while it drips.",
        ["FOR/sapcollecting"] = "Taking sap out of a tapline container, scaled by the litres actually removed. Credited to whoever COLLECTS, not the spile's owner. See sapLitresPerCredit and sapCollectCap.",

        // ---------------------------------------------------------------- GLA, Glassmaking
        ["GLA/melting"] = "Charging a glass smeltery. Per melt batch, keyed on the smeltery, so one batch banks its share.",
        ["GLA/blowing"] = "Blowing glass on the pipe, or taking a piece from a blowing mold.",
        ["GLA/casting"] = "Collecting a piece from a ladle casting mold.",
        ["GLA/workbench"] = "Completing a cold-working step at the glass workbench.",
        ["GLA/annealing"] = "Retrieving a load from the annealer. Per batch, keyed on the annealer.",

        // ---------------------------------------------------------------- HUN, Hunting
        ["HUN/hunting"] = "A wild kill, credited to the player who caused it. Bucketed by species and second, so one pack kill credits once.",
        ["HUN/dressing"] = "The field harvest at a carcass. On a player-raised animal it splits 50/50 with FAR butchery.",
        ["HUN/trapping"] = "A snare or deadfall catching, credited to whoever SET it. Per session, trapline-shaped.",
        ["HUN/butchery"] = "Station work at a butchering block. Low raw, batch processing dedups inside the window.",
        ["HUN/tanning"] = "The sealed-barrel leather chain: soak, prepare, tan, dye. Each stage is a seal. The only earnable leather verb, since the crafts that USE leather are grid crafts that grant nothing.",

        // ---------------------------------------------------------------- MAS, Masonry
        ["MAS/mortar"] = "Completing a staged mortared structure (medievalarchitecture): frame, rim, mortar, complete. One credit per finished archway or structure.",
        ["MAS/dress"] = "Working a quarried slab into blocks or bricks, or hammering rock down the aggregate chain (stonequarry). Also grinding stone to lime at the quern (limestone/chalk/marble, LGD-140). The recurring stonecraft.",
        ["MAS/chisel"] = "Freeform voxel carving. No completion event exists, so this pays per net new voxel with a per-position per-minute dedup; high K so babysitting one block saturates.",

        // ---------------------------------------------------------------- MEL, Melee
        ["MEL/fighting"] = "A melee kill. Blade, spear and blunt all pool into this one verb. A whole encounter per event.",
        ["MEL/blocking"] = "Absorbing a hostile blow with a block or parry. Fenced to hostile aggressors and keyed on the attacker, so tanking one caged mob banks nothing. A caught PARRY pays more, see rawParryMul.",

        // ---------------------------------------------------------------- MET, Metalworking
        ["MET/smithing"] = "Working metal on the anvil. Pays in full for every finished piece: a second knife is real work, and the metal cost plus the daily curve are governor enough.",
        ["MET/casting"] = "Pouring a mold. Pays in full per pour; a sand-cast bed pays less per mold, see sandCastFactor.",
        ["MET/quenching"] = "Quenching hot work. Carries the shatter risk, see shatterFactorUntrained.",
        ["MET/smelting"] = "Single-metal smelting. The chain's most spammable step, so it saturates fast by design (low K).",
        ["MET/alloying"] = "Mixing an alloy in a crucible. Real craft, so it keeps headroom above smelting.",
        ["MET/assembly"] = "Fitting a tool head to a handle, at the grid, the bench or in hand. Craft-and-uncraft is answered by assemblyRepeatFree, not by dedup, because disassembly returns the parts.",

        // ---------------------------------------------------------------- MIN, Mining
        ["MIN/mining"] = "Breaking a block at the face. Raw is the ORE unit value, scaled per break by depth and rarity for ore, or down to miningStoneFraction for plain rock, so one verb prices both outcomes.",
        ["MIN/quarrying"] = "Hammer-striking a quarry. Capped per plug network, so one quarry banks a bounded amount however many strikes it takes.",
        ["MIN/knapping"] = "Knapping a piece of flint or stone. The day-one verb, priced to START the ladder rather than climb it: see knapRepeatFree for the say-nay curve past a day-one kit.",

        // ---------------------------------------------------------------- PAN, Panning & Prospecting
        ["PAN/panning"] = "One completed 3.4 second wash with material in the pan. Panning hauled gravel or Stonebound spoil counts: the pan has never cared what is in it.",
        ["PAN/prospecting"] = "Any propick act: a density or stone reading, or one of the search modes. Keyed to the chunk column, so re-reading the same ground dedups inside the window.",

        // ---------------------------------------------------------------- POT, Pottery
        ["POT/clayforming"] = "Finishing a clay-formed piece. The per-act raw is scaled by the piece's filled voxels, so Raw here prices the REFERENCE piece - see clayformVoxelReference. The pottery wheel pays this same row at reduced raw.",
        ["POT/firing"] = "A pit kiln converting a piece, success-gated: a rained-out or breached firing banks nothing. Paid per PIECE, so a full four-slot kiln pays what four single-piece kilns pay. Credited to whoever lit it.",

        // ---------------------------------------------------------------- RAN, The Ranged
        ["RAN/shooting"] = "A loose that KILLS, never the loose itself. Bow, sling, thrown spear, crossbow and firearm all pool here; difficulty is priced through the raw multipliers below, never through breadth. PvP kills and owned or high-generation livestock bank nothing.",

        // ---------------------------------------------------------------- TAI, Tailoring
        ["TAI/spin"] = "Drawing twine: a hand spindle, or a spinning wheel cycle. An ATTENDED wheel pays stationRawMult of the hand rate on every cycle; a hand spindle pays full.",
        ["TAI/weave"] = "Weaving cloth on the loom. Attended loom cycles pay stationRawMult of the hand rate, every cycle.",
        ["TAI/knit"] = "Knitting with needles in hand (knitting mod).",
        ["TAI/sew"] = "Grid-crafting or repairing a garment, or sewing a bag or pack (LGD-159) — anything whose craft consumed cloth or hide, scaled by the textile units spent. The pure-vanilla tailoring verb.",
        ["TAI/dye"] = "Sealing a dye barrel. Consumes real dye per repetition, so cost is the anti-farm.",

        // ---------------------------------------------------------------- TEM, Temporal
        ["TEM/warding"] = "Fuelling or toggling a rift ward. Per session: a ward runs 21 days, so one refuel banks most of the share.",
        ["TEM/repair"] = "Feeding gears into a broken translocator, or recharging a spent teleporter. Rare, since translocators are sparse worldgen.",
        ["TEM/temporalkill"] = "Killing a rust mob, as a 50% co-grant beside the MEL or RAN credit. Raw and K mirror the combat kill rows so the share lands at exactly half.",
        ["TEM/wayfaring"] = "Returning home alive from a frontier excursion (Conjunction homecoming), scaled by weighted distance. Rate-limited by travel time rather than by dedup.",

        // ---------------------------------------------------------------- WOO, Woodcutting & Forestry
        ["WOO/felling"] = "One axe swing that fells a tree. Raw is per LOG and deliberately tiny, then the whole swing is coalesced into ONE event whose multiplier is set by the log count - see fellLogKnee and fellLogCap. A ten-log tree banks about 3.5, not 35.",
        ["WOO/planting"] = "Planting a sapling. Cheap and self-limiting, because seeds are finite.",
        ["WOO/chopping"] = "Chopping at a sawbuck or block (indappledgroves). Per deliberate hold, not per block like felling, and the other staple grind floor alongside it.",
        ["WOO/sawing"] = "Sawing stock (indappledgroves). Copper- or workstation-gated, so lower volume than chopping.",
        ["WOO/hewing"] = "Hewing stock (indappledgroves). Planing is merged into this verb.",
        ["WOO/pounding"] = "Pounding stock (indappledgroves).",
        ["WOO/burning"] = "A charcoal pit completing its burn. Credited to whoever LIT it, not whoever digs the charcoal out. Flat regardless of pit size.",
    };

    /// <summary>Bonus knob -> what it does. Keyed "CODE/knobKey". The 18 `*FloorPerRaw` tail floors
    /// and ARC's 18 `ritualRaw*` keys are generated in <c>BonusHelpFor</c> and are not listed.</summary>
    private static readonly Dictionary<string, string> Bonus = new()
    {
        // ---------------------------------------------------------------- ALC, Alchemy
        ["ALC/potencyUntrained"] = "Multiplier on remedy strength (poultice Health, potion StrengthMul) at Untrained. Below 1 = a beginner's draught heals less. Snaps to 1.0 across Novice. 1.0 = no penalty.",
        ["ALC/potencyGm"] = "Multiplier on remedy strength at Grandmaster. Climbs from 1.0 at Novice IV. 1.0 = no bonus.",
        ["ALC/durationUntrained"] = "Multiplier on remedy duration at Untrained. Below 1 = shorter effects. 1.0 = no penalty.",
        ["ALC/durationGm"] = "Multiplier on remedy duration at Grandmaster. 1.0 = no bonus.",
        ["ALC/emphasisBonus"] = "Extra fraction a GRANDMASTER's chosen emphasis adds: Potent deepens potency, Lasting deepens duration. The choice is the player's own toggle on the Alchemy page. 0 = emphasis does nothing.",
        ["ALC/reviveUntrained"] = "0-1. Fraction of max health a downed player wakes on from an unbranded remedy. Vanilla wakes at FULL, so this is a penalty by default. 1.0 = vanilla behaviour.",
        ["ALC/reviveGm"] = "0-1. Fraction of max health a Grandmaster's remedy revives on, and a HARD CAP held even above GM: a field revival should never equal walking it off. 1.0 removes the cap.",
        ["ALC/fuelEconomyUntrained"] = "Fraction of the reaction's fuel burn ADDED at Untrained. Negative = a beginner burns more. 0 = neutral.",
        ["ALC/fuelEconomyApprentice"] = "Fuel-burn fraction REFUNDED at Apprentice I, where the above-vanilla band starts. 0 = neutral.",
        ["ALC/fuelEconomyGm"] = "Fuel-burn fraction refunded at Grandmaster. 0 = no saving.",
        ["ALC/herbRackPreserveGm"] = "Perish multiplier on herb-rack output for a Grandmaster. Below 1 = a master over-dries fewer bundles. 1.0 = no bonus.",

        // ---------------------------------------------------------------- ANI, Animal Handling
        ["ANI/genRaiseStep"] = "Extra raw fraction per generation of the newborn above the first, so climbing a lineage is the skill rather than volume. 0 = every birth pays the same.",
        ["ANI/genRaiseCap"] = "Ceiling on the generation multiplier, so a high-generation line still tapers. 1.0 = no generation scaling at all.",
        ["ANI/purgeBonusGm"] = "Allele-purge resistance ADDED to genelib's own (0.6 stock) for a Grandmaster's newborns, hard-capped below 1 so loss never reaches zero. 0 = no bonus.",
        ["ANI/litterProcGm"] = "0-1 chance at Grandmaster of one extra offspring, capped at the species' own maximum. 0 = no bonus.",
        ["ANI/treatUntrained"] = "Multiplier on taming progress per treat at Untrained. Below 1 = a beginner needs more treats. 1.0 = no penalty.",
        ["ANI/treatGm"] = "Multiplier on taming progress per treat at Grandmaster: a master domesticates on fewer treats. 1.0 = no bonus.",
        ["ANI/throwHealGm"] = "0-1. Fraction of saddle-break throw damage healed back at Grandmaster. There is no failure roll to scale, so being thrown SOFTER is the honest lever. 0 = no bonus.",
        ["ANI/gateWolf"] = "Minimum ANI level to INITIATE taming a wolf. 0 = ungated.",
        ["ANI/gateFox"] = "Minimum ANI level to INITIATE taming a fox. 0 = ungated.",

        // ---------------------------------------------------------------- ARC, Arcana
        ["ARC/regenGm"] = "Bonus mana per regen tick at Grandmaster. 0 through Novice, climbing from there. 0 = no bonus.",
        ["ARC/drainGm"] = "Multiplier on temporal-stability drain from meditation and magic at Grandmaster. Below 1 = a master pays less stability. Floored by the game, so the trance always costs something. 1.0 = no bonus.",
        ["ARC/schoolDiscountGm"] = "0-1. Fraction off a school's mana cost at Grandmaster. Rustbound floors the modified cost at 1. 0 = no discount.",
        ["ARC/backfireGmResidual"] = "0-1 chance an ULTIMATE spell backfires even for a Grandmaster, so the Great Work is never routine. 0 = a Grandmaster never backfires.",
        ["ARC/backfireDrainPerTier"] = "Temporal-stability drain per spell tier above your rank when an over-tier cast backfires. 0 = backfire costs no stability.",
        ["ARC/arcaneKillGreaterMul"] = "Raw multiplier for the greater Rustbound hostiles (colossus, titan, sentinel) on the arcane-kill co-grant; lesser ones sit at 1.0. ARC-side only: it never changes what MEL or RAN pay for the same corpse. 1.0 = one flat band.",
        ["ARC/schoolMasteryChannel"] = "Cumulative mana channeled through one school to reach Master familiarity in it. The pace knob for school fluency, which PRICES spells (cost discount) and never gates them. Lower = faster fluency.",
        ["ARC/meditationTranceRaw"] = "Raw practice banked by one FULL empty-to-full trance, against the meditation technique's own K. Because it pays for the fraction of the pool actually restored, one full trance is worth the same at Novice and at Grandmaster.",
        ["ARC/memoryCrystalManaFrac"] = "0-1. Fraction of the effective mana pool a Crystallized Memory restores when consumed. The crystal's vanilla XP write is frozen (rank is never purchasable), so it is a one-shot mana burst instead. 0 = the crystal does nothing.",

        // ---------------------------------------------------------------- BEE, Beekeeping
        ["BEE/stingUntrained"] = "Multiplier on the bee-mob spawn roll when an UNTRAINED keeper breaks a skep (vanilla chance 0.4). Above 1 = the beginner gets stung more. Clears at Novice I and never rises again. 1.0 = no penalty.",
        ["BEE/crushChanceUntrained"] = "0-1 chance an Untrained skep harvest crushes the comb and loses one honeycomb, never below one. Clears at Novice I. 0 = no penalty.",
        ["BEE/focusCooldownSeconds"] = "Seconds after a crushed comb during which the penalty cannot roll again, so one bad moment does not cascade through a harvest. 0 = every frame can roll.",

        // ---------------------------------------------------------------- BRE, Brewing & Fermentation
        ["BRE/spoilUntrained"] = "RETIRED FROM GAMEPLAY. Was the Untrained spoilage chance before The Turn replaced it; still read for the figures printed in the Almanac guide. Changing it affects the book, not play.",
        ["BRE/turnSafeUntrained"] = "0-1. Fraction of a barrel an Untrained brewer can seal before it owes a turn. Each further safe-fraction's worth owes one more turn. 1.0 = a full barrel never owes a turn.",
        ["BRE/turnSafeNovice"] = "0-1. Safe fraction of a barrel at Novice. 1.0 = never owes a turn.",
        ["BRE/turnSafeApprentice"] = "0-1. Safe fraction of a barrel at Apprentice. 1.0 = never owes a turn.",
        ["BRE/turnSafeJourneyman"] = "0-1. Safe fraction of a barrel at Journeyman. Master I and up seal and forget regardless. 1.0 = never owes a turn.",
        ["BRE/turnWindowUntrained"] = "In-game hours of SEALER-ONLINE time an Untrained brewer has to answer a turn. Larger = more forgiving.",
        ["BRE/turnWindowNovice"] = "In-game hours of sealer-online time to answer a turn at Novice.",
        ["BRE/turnWindowApprentice"] = "In-game hours of sealer-online time to answer a turn at Apprentice.",
        ["BRE/turnWindowJourneyman"] = "In-game hours of sealer-online time to answer a turn at Journeyman.",
        ["BRE/turnTeachingUntrained"] = "Turns every Untrained seal owes at minimum, however small the batch: the teaching turn. 0 disables it, so a small Untrained batch can owe nothing.",
        ["BRE/turnWindowMaxBurnPerTick"] = "Ceiling, in in-game hours, on how much of a turn's window one barrel tick may consume. Stops an admin time-skip or a chunk reloading after days away from eating the window whole. At 0.25 it never binds in normal play.",
        ["BRE/portionUntrained"] = "Output portion multiplier while Untrained: a beginner's batch comes up short even when it does not fail. Clears at Novice I. 1.0 = no penalty.",
        ["BRE/measureChanceGm"] = "0-1 chance a GRANDMASTER's completed ferment pays over its rated count. Fires at the top only. 0 = no bonus.",
        ["BRE/measureBonusFraction"] = "Fraction of the rated count added when the measure fires, minimum one unit. 0 = the proc adds the minimum one.",

        // ---------------------------------------------------------------- COO, Cooking
        ["COO/cxMealpot"] = "Complexity class of the cooking pot, 0-3 (0 bare heat, 1 vessel, 2 station, 3 chain apparatus). Class is the CEILING on rank bonuses for that verb, never a locked door. Re-class a station here without a rebuild.",
        ["COO/cxDirectheat"] = "Complexity class of cooking straight over heat, 0-3. 0 = bare fire, the lowest bonus ceiling.",
        ["COO/cxBaking"] = "Complexity class of oven baking, 0-3.",
        ["COO/cxGriddling"] = "Complexity class of the griddle hearth, 0-3.",
        ["COO/cxMixing"] = "Complexity class of the mixing bowl, 0-3.",
        ["COO/cxSimmering"] = "Complexity class of the saucepan, 0-3.",
        ["COO/cxMilling"] = "Complexity class of quern milling, 0-3.",
        ["COO/cxJuicing"] = "Complexity class of the fruit press, 0-3.",
        ["COO/cxDrying"] = "Complexity class of rack drying, 0-3.",
        ["COO/cxSalting"] = "Complexity class of the salt pan, 0-3.",
        ["COO/cxPrep"] = "Complexity class of the prep table, 0-3.",
        ["COO/cxPastry"] = "Complexity class of pie assembly, 0-3. Uses no apparatus, but every part going in is itself a chain product, so it classes with prep.",
        ["COO/fuelUntrained"] = "Multiplier on fuel burn duration at Untrained. Below 1 = a beginner wastes fuel. 1.0 = no penalty.",
        ["COO/fuelGm"] = "Multiplier on fuel burn duration at Grandmaster. 1.0 = no bonus.",
        ["COO/charUntrained"] = "Multiplier on how fast a FINISHED bake browns toward charred while it sits in heat, at Untrained. Above 1 = burns fast. 1.0 = no penalty.",
        ["COO/charGm"] = "Char-clock multiplier at Grandmaster: a master's bake sits longer before it chars. Floored, never zero. 1.0 = no bonus.",
        ["COO/spoilUntrained"] = "Perish-rate multiplier on food carrying an Untrained cook's stamp. Above 1 = spoils faster. 1.0 = no penalty.",
        ["COO/spoilGm"] = "Perish-rate multiplier on food stamped by a Grandmaster: the Cook's Mark. Below 1 = keeps longer. 1.0 = no bonus.",
        ["COO/servingProcGm"] = "0-1 chance at Grandmaster of an extra serving from the same ingredients. 0 through Apprentice. 0 = no bonus.",
        ["COO/satietyGmC3"] = "Extra satiety fraction a Grandmaster's dish carries, at complexity 3. Scales down by class, so it is near zero on a C0 dish. 0 = no bonus.",
        ["COO/healthGmC3"] = "Extra health fraction a Grandmaster's dish carries, at complexity 3. Scales down by class. 0 = no bonus.",

        // ---------------------------------------------------------------- ENG, Engineering
        ["ENG/repairUntrained"] = "Multiplier on durability restored per repair item at Untrained. Below 1 = a beginner's repairs are weaker. Clears to 1.0 across Novice. 1.0 = no penalty.",
        ["ENG/repairGm"] = "Multiplier on durability restored per repair item at Grandmaster: fewer strips, wax, resin and thread over a machine's life. 1.0 = no bonus.",
        ["ENG/decayUntrained"] = "Post-service decay multiplier persisted on the part at Untrained. Above 1 = a part a beginner fixed wears out SOONER afterwards. 1.0 = no penalty.",
        ["ENG/decayGm"] = "Post-service decay multiplier at Grandmaster: the Millwright's Mark. Below 1 = a master's service lasts noticeably longer, which is why you go back to them. 1.0 = no bonus.",
        ["ENG/igniteUntrained"] = "0-1 chance per overheat check (every 500ms) that an Untrained engineer's overdriven part ignites. Vanilla's own stub number is 0.03. 0 = never ignites.",
        ["ENG/igniteNovice"] = "0-1 ignition chance per check at Novice. Also what an UNATTRIBUTED machine uses, so a machine nobody built is priced as a Novice's. 0 = never.",
        ["ENG/igniteJourneyman"] = "0-1 ignition chance per check at Journeyman. 0 = never.",
        ["ENG/igniteMaster"] = "0-1 ignition chance per check at Master. 0 = never.",
        ["ENG/igniteGm"] = "0-1 ignition chance per check at Grandmaster. Deliberately NOT zero: the top rank is the best on the curve, not a physics exemption. 0 = immune.",
        ["ENG/igniteScaleFloor"] = "Smallest overspeed multiplier on the ignition roll, for a part barely over the burn line. Stops a creeping overdrive being as dangerous as a gross one. At 0.05 a barely-over part takes roughly half an hour at a Grandmaster's rate.",
        ["ENG/igniteScaleCap"] = "Largest overspeed multiplier on the ignition roll, so a grossly overdriven train burns quickly but not instantly.",
        ["ENG/gmAssembledDecay"] = "Decay multiplier a part carries when RIGGED by a Grandmaster, held until its first service. Below 1 = a master's build starts out longer-lived. 1.0 = no bonus.",

        // ---------------------------------------------------------------- FAR, Farming & Husbandry
        ["FAR/harvestDockUntrained"] = "Multiplier on crop drops at Untrained. Below 1 = a beginner's harvest returns less. Penalty only, clears at Novice, no rank bonus above it. 1.0 = no penalty.",
        ["FAR/harvestRipeFloor"] = "0-1. Minimum ripeness (current stage over growth stages) a crop break must reach to count as practice AT ALL. Below it the break banks nothing: an immature seed-only break is not husbandry. The lowest ripeness any real crop can produce is about 0.125, so a value under that disables the floor.",
        ["FAR/harvestScytheBonus"] = "Raw multiplier for harvesting with a scythe over pulling by hand. A two-handed swing that has to be read and timed earns a small premium; throughput is already its own reward. 1.0 = no premium.",
        ["ANI/feedUntrained"] = "Multiplier on satiety an animal draws per trough portion, by the FILLER's ANI rank, at Untrained. Below 1 = a beginner's feed goes to waste. Moved from FAR with the feeding verb (LGD-122). 1.0 = no penalty.",
        ["ANI/feedGm"] = "Satiety multiplier per trough portion at Grandmaster: a master handler feeds the herd to the same satiety on fewer portions. 1.0 = no bonus.",
        ["ANI/rideCreditBlocks"] = "Blocks of real ridden displacement per riding credit. Lower = riding banks faster. 0 disables the riding grant entirely.",
        ["FAR/fertThriftGm"] = "0-1 chance at Grandmaster that applying fertiliser costs no item. 0 through Apprentice. 0 = no bonus.",
        ["FAR/graftRetryGm"] = "0-1 chance at Grandmaster that a DYING cutting clings on: the death is reverted and vanilla re-rolls its own unmodified chance later. No single graft is ever easier than vanilla, and none is ever certain. 0 = no bonus.",
        ["FAR/shearScratchUntrained"] = "Multiplier on shearlib's scratch chance for an Untrained hand. Above 1 = clumsy hands wound the animal more often. Clears at Novice; no rank bonus above it. 1.0 = no penalty.",
        ["FAR/heirloomYield"] = "Yield bonus fraction a live Heirloom seed grants its harvest, REGARDLESS of who plants it: the mastery is bred into the seed, not the hands. 0 = heirloom seeds carry nothing.",
        ["FAR/heirloomGenerations"] = "How many harvest generations a Grandmaster's fresh seed carries the heirloom bonus for, counting down the line, after which it is an ordinary seed. 0 = no heirloom tail.",
        ["FAR/spoilGrownGm"] = "Perish multiplier on produce carrying a Grandmaster's grownBy stamp. Below 1 = own-grown produce keeps longer. 1.0 = no bonus.",
        ["FAR/harvestProcGm"] = "0-1 chance at Grandmaster of a bonus seed or unit from a harvest. 0 through Apprentice. 0 = no bonus.",

        // ---------------------------------------------------------------- FIS, Fishing
        ["FIS/escapeChanceUntrained"] = "0-1 chance the fish gets away, at Untrained. 0 = never escapes.",
        ["FIS/escapeChanceNovice"] = "0-1 escape chance at Novice I, where the curve snaps to after Untrained. This is a PERMANENT risk curve, not an Untrained-only penalty.",
        ["FIS/escapeChanceGm"] = "0-1 escape chance at Grandmaster: the floor of the risk curve. Set to 0 for a truly safe Grandmaster.",
        ["FIS/sizeSkewUntrained"] = "Additive skew on the adult chance of the size roll, at Untrained. Negative leans juvenile. 0 = no skew.",
        ["FIS/sizeSkewGm"] = "Additive skew on the adult chance at Grandmaster: a master lands the bigger catch. 0 = no skew.",
        ["FIS/depletionUntrained"] = "Multiplier on the vanilla fish-depletion step per catch, at Untrained. Above 1 = a beginner overfishes a spot. 1.0 = vanilla.",
        ["FIS/depletionGm"] = "Depletion multiplier at Grandmaster: a light touch on the stock. Floored above zero, so a spot ALWAYS depletes some and there is never infinite free fish. 1.0 = vanilla.",
        ["FIS/roeRestockGmMultiplier"] = "How much more a Grandmaster steward's ovulated roe counts for when thrown into water to restock the fish map. Roe always works for anyone at 1.0; stewardship makes it work harder. 1.0 = rank does not matter.",

        // ---------------------------------------------------------------- FOR, Foraging
        ["FOR/forageYieldUntrained"] = "Multiplier on in-place forage drops at Untrained. Below 1 = a beginner wastes the plant. 1.0 = no penalty.",
        ["FOR/forageYieldGm"] = "Multiplier on in-place forage drops at Grandmaster. The fractional part rolls as a CHANCE of one bonus unit rather than multiplying the stack. 1.0 = no bonus.",
        ["FOR/wildcropYieldUntrained"] = "Multiplier on destructive wild-crop drops at Untrained. 1.0 = no penalty.",
        ["FOR/wildcropYieldGm"] = "Multiplier on destructive wild-crop drops at Grandmaster. 1.0 = no bonus.",
        ["FOR/novelFindMultiplier"] = "Raw multiplier for the FIRST harvest of a species this player has ever taken, so a forager levels by ranging wider rather than stripping one bush. Later harvests of a known species pay base raw. 1.0 = novelty pays nothing extra.",
        ["FOR/gatherRepeatFree"] = "Breaks of the SAME species that pay full raw each in-game day, per player. Past this the decay below takes over. Raise it to be more forgiving of a big single-species haul.",
        ["FOR/gatherRepeatDecay"] = "0-1. Per-break multiplier past the free count, compounding: the n-th break past it pays decay to the power n. Lower bites harder. 1.0 = no repeat decay at all.",
        ["FOR/tendBoostDaysApprentice"] = "In-game days an Apprentice's pick advances a patch's regrow clock, against regrow clocks of 10-20 days. The HARVEST itself stewards; there is no separate tending verb. 0 = no boost.",
        ["FOR/tendBoostDaysGm"] = "In-game days a Grandmaster's pick advances a patch's regrow clock. 0 = no boost.",
        ["FOR/woundDays"] = "In-game days an UNTRAINED pick DELAYS the patch's regrowth by. Ranked hands never wound, and nothing here ever destroys a source. 0 = no wound.",
        ["FOR/untrainedTapSlowdown"] = "Fraction of a matured drip span that an Untrained-placed tapline pushes its own timer forward by, so it runs slower. Never kills the spile, the segment or a resin node. 0 = no slowdown.",
        ["FOR/sapLitresPerCredit"] = "Litres of sap that earn one full raw of sapcollecting. Deliberately fractional: one matured drip is about 10 mL, so the default half-litre is roughly 50 drips. Lower = more generous.",
        ["FOR/sapCollectCap"] = "Ceiling on the multiplier a SINGLE collection can pay, so emptying a long-brewed barrel is a good haul rather than a level.",

        // ---------------------------------------------------------------- GLA, Glassmaking
        ["GLA/windowUntrained"] = "Degrees ADDED to the 100C shatter deadline for glass made by an Untrained hand, narrowing their working window (20 = shatters at 120C). Stamped on the piece at the annealer, so it travels with the glass. Clears to the vanilla 100C at Novice. 0 = no penalty.",
        ["GLA/windowGm"] = "Degrees SUBTRACTED from the shatter deadline at Grandmaster, widening the window (20 = tolerates down to 80C). Never immune, so annealing is never optional. This IS the Glassmaker's Mark. 0 = no bonus.",

        // ---------------------------------------------------------------- HUN, Hunting
        ["HUN/animalYieldUntrained"] = "Multiplier on the per-player harvest yield stat at Untrained. Below 1 = clumsy dressing wastes hide and meat. Note the butchering drop formula also multiplies station tier and animal weight, so a green hand needs a real penalty to feel it. 1.0 = no penalty.",
        ["HUN/animalYieldGm"] = "Harvest yield multiplier at Grandmaster. The fractional part rolls as a bonus-cut chance. Kept modest because the whole formula is multiplicative. 1.0 = no bonus.",
        ["HUN/seekRangeUntrained"] = "Multiplier on the stat animal AI reads to decide how far off it notices you, at Untrained. Above 1 = game flees the beginner from further out. 1.0 = no penalty.",
        ["HUN/seekRangeGm"] = "Seek-range multiplier at Grandmaster: the Stalker. Floored ABOVE zero, because there is no invisible hunter. 1.0 = no bonus.",
        ["HUN/trapFailUntrained"] = "Multiplier on BOTH trap failure rolls (bait stolen, tripped empty) at Untrained. Above 1 = a green hand's set fails more often than vanilla. 1.0 = no penalty.",
        ["HUN/trapFailGm"] = "Trap failure multiplier at Grandmaster: the floor, deliberately above zero, because no trap is ever a sure thing. 1.0 = no bonus.",
        ["HUN/trapStaySetGm"] = "0-1 chance at Grandmaster that a trap STAYS SET after a successful kill, bait kept and the line still working. Land traps hold no catch item, so the trapline bonus is the next catch coming sooner. 0 through Novice. 0 = no bonus.",

        // ---------------------------------------------------------------- MAS, Masonry
        ["MAS/dressYieldUntrained"] = "Multiplier on the stone-dressing drop roll at Untrained: fewer bricks off a wedge-slab, less stone off a rubble hammer. Clears to 1.0 at Novice. 1.0 = no penalty.",
        ["MAS/dressYieldGm"] = "Dress-yield multiplier at Grandmaster: a master wrings more building units out of the same quarried slab, never a different block. The fractional part rolls as a chance of one extra unit. 1.0 = no bonus.",

        // ---------------------------------------------------------------- MEL, Melee
        ["MEL/dummyTrainMul"] = "Raw multiplier per straw-dummy hit while Untrained. The dummy is the on-ramp to Novice: hits pay, kills on it never do, and the value fades to zero at Apprentice I because straw cannot teach what a live opponent teaches. RAN reads this same knob. 0 = dummies teach nothing.",
        ["MEL/dummyTrainFree"] = "Full-pay dummy hits per in-game day, per calling. An archer and a swordsman each get their own full drill. Past this the say-nay curve takes over.",
        ["MEL/killRepeatFree"] = "Full-pay kills of one creature species per in-game day, one pool across melee and ranged. Past this the say-nay curve takes over. Replaced the dedup blackout that paid zero for same-species kills near each other (LGD-249).",
        ["MEL/killRepeatDecay"] = "Multiplier per kill past killRepeatFree of a species in a day: the nth extra kill pays decay^n. TEM and ARC co-grants taper in step. 0 = hard stop at the free count; 1 = no decay.",
        ["MEL/rawParryMul"] = "Raw multiplier for a caught PARRY over a passive block: the timed catch is the skill act. 1.0 = both pay the same.",
        ["MEL/damageUntrained"] = "Melee weapon damage multiplier at Untrained. This is the ONLY appearance of the damage lever anywhere in MEL and it is penalty-only: no rank ever adds damage. Clears at Novice I. 1.0 = no penalty.",
        ["MEL/armorUntrained"] = "Additive DELTA on armour affectedness at Untrained. Positive = the beginner wears armour clumsily, more drag than vanilla. Applied to walk speed, and under Combat Overhaul to manipulation and hunger rate too. 0 = no penalty.",
        ["MEL/armorGm"] = "Additive delta on armour affectedness at Grandmaster. Negative = the veteran sheds the drag toward the unarmoured baseline, never past it. 0 = no bonus.",
        ["MEL/parryGraceGmMs"] = "Milliseconds of extra parry catch-window at Grandmaster. 0 at Novice. 0 = no grace at any rank.",
        ["MEL/blockTierLevel"] = "Level at which a guard gains the defensive block-tier bonus below. DEFENSIVE only: what you can stop, never weapon or armour tier. Set above 17 to disable.",
        ["MEL/blockTierBonus"] = "Block tiers added at and above blockTierLevel, so a master fully stops what a novice only partly blocks. 0 = no bonus.",
        ["MEL/perfectWindowMs"] = "Milliseconds from the parry opening to the blow landing that count as a PERFECT catch. FIXED at every rank, because mastery is always precision; rank scales the depth below, not the window.",
        ["MEL/pierceGm"] = "Armour-pierce tiers a perfect riposte cuts at Grandmaster. Journeyman I starts at 1 and climbs to this. Rides Combat Overhaul's own pierce lever rather than adding damage. 0 = no pierce.",
        ["MEL/riposteWindowMs"] = "Milliseconds the stamped pierce lasts after a perfect parry, so it rides the riposte strike. Combat Overhaul's own riposte window is 300ms.",

        // ---------------------------------------------------------------- MET, Metalworking
        ["MET/assemblyRepeatFree"] = "Tool assemblies per in-game day that pay full raw, counted as ONE scope across the grid, the bench and in-hand alike, so moving where you loop buys nothing. Disassembly returns the parts, which is why this exists and dedup cannot do the job.",
        ["MET/assemblyRepeatDecay"] = "0-1. Per-assembly multiplier past the free count, compounding. 1.0 = no repeat decay.",
        ["MET/overStrikeChance"] = "0-1 chance an Untrained split's sheared bit crumbles to scale instead of being recovered. Fires on the Smithing+ recovery seam. 0 = no penalty.",
        ["MET/moveSlipChance"] = "0-1 chance an Untrained move nudges one extra nearby voxel. Does not delete voxels. 0 = no penalty.",
        ["MET/focusCooldownSeconds"] = "Seconds after any smithing mishap during which nothing further can roll, so one slip does not cascade. 0 = every strike can roll.",
        ["MET/shatterFactorUntrained"] = "Multiplier on quench-shatter chance at Untrained. Above 1 = a beginner cracks more work. 1.0 = vanilla.",
        ["MET/shatterFactorGm"] = "Quench-shatter multiplier at Grandmaster. NEVER zero: impurity, humidity and luck stay in the game. 1.0 = vanilla.",
        ["MET/fuelEconomyUntrained"] = "Fraction of forge fuel burn ADDED at Untrained. Negative = a beginner burns more. 0 = neutral.",
        ["MET/fuelEconomyApprentice"] = "Fuel-burn fraction refunded at Apprentice I, where the above-vanilla band starts. 0 = neutral.",
        ["MET/fuelEconomyGm"] = "Fuel-burn fraction refunded at Grandmaster. 0 = no saving.",
        ["MET/bitRecoveryUntrained"] = "Multiplier on Smithing+ bit recovery from a split, at Untrained. Below 1 = a beginner recovers less metal. 1.0 = no penalty.",
        ["MET/bitRecoveryGm"] = "Bit-recovery multiplier at Grandmaster: more metal back off the same splits. 1.0 = no bonus.",
        ["MET/sandCastFactor"] = "Raw multiplier for ONE mold filled in an industrialstory casting-sand bed. Sand casting fills every connected mold in a single tap, so each mold is worth clearly less than a hand pour while a four-tool pour still beats a one-tool pour. 1.0 = priced the same as a hand pour.",
        ["MET/moldWearUntrained"] = "Multiplier on mold wear applied per use, at Untrained. Above 1 = a beginner chews through molds. 1.0 = no penalty.",
        ["MET/moldWearGm"] = "Mold wear multiplier at Grandmaster: a master's molds last longer. 1.0 = no bonus.",
        ["MET/gmWearSkip"] = "0-1 chance per hit that an item made by a Grandmaster takes no wear. Rides on top of the maker quality pool, so kept modest. 0 = no bonus.",
        ["MET/durableWearSkip"] = "0-1 wear-skip chance for a DURABLE-emphasis piece. A deeper single value, NOT stacked on the universal skip above. 0 = no bonus.",
        ["MET/honedArmorPierce"] = "Effective armour-piercing tiers added by a HONED piece on attack, on both the Combat Overhaul and vanilla paths. 0 = Honed adds no pierce.",

        // ---------------------------------------------------------------- MIN, Mining
        ["MIN/staminaUntrained"] = "Multiplier on stamina consumed per swing at Untrained (ImmersiveMining). Above 1 = the beginner tires fast. 1.0 = no penalty.",
        ["MIN/staminaGm"] = "Stamina multiplier at Grandmaster: the Deep-Delver's endurance floor. At the shipped pair a Grandmaster works about 1.9x longer at the face than an Untrained miner. 1.0 = no bonus.",
        ["MIN/oreYieldUntrained"] = "Multiplier on ore drops at Untrained. Below 1 = the beginner shatters ore. 1.0 = no penalty.",
        ["MIN/oreYieldGm"] = "Ore-drop multiplier at Grandmaster. Capped modestly so it never doubles even on a multi-unit drop. 1.0 = no bonus.",
        ["MIN/caveinUntrained"] = "Multiplier on unstable-rock collapse chance at Untrained. Above 1 = the beginner brings the roof down. 1.0 = vanilla.",
        ["MIN/caveinGm"] = "Collapse-chance multiplier at Grandmaster. Never 0, and isolated rock still falls at 100% for everyone regardless. 1.0 = vanilla.",
        ["MIN/miningDepthCoeff"] = "How much depth raises the raw practice of an ore break: effective raw is base times (1 + rarity term + depth term). 0 = depth does not matter.",
        ["MIN/miningRarityCoeff"] = "How much ore rarity raises the raw practice of a break. SEEDED BUT INERT at present: there is no per-ore rarity table yet, so changing this does nothing until one exists. 0 = rarity does not matter.",
        ["MIN/miningStoneFraction"] = "0-1. Fraction of the ore raw that breaking plain stone pays on the same verb, so one technique prices both outcomes. 0 = stone pays nothing.",
        ["MIN/knapRepeatFree"] = "Knapped pieces per in-game day that pay full raw, counted across ALL outputs so rotating recipes buys nothing. Sized as a real day-one kit (axe, knife, spears, shovel, hoe, some arrowheads), not a tax on the stone age.",
        ["MIN/knapRepeatDecay"] = "0-1. Per-knap multiplier past the free count, compounding. 1.0 = no repeat decay.",
        ["MIN/rubbleLayerFraction"] = "0-1. Fraction of the mining raw that working one Wilderlands Stonebound rubble layer pays, so the four layers make a whole block. Inactive without the mod. 0 = rubble pays nothing.",

        // ---------------------------------------------------------------- PAN, Panning & Prospecting
        ["PAN/panYieldUntrained"] = "Multiplier on every pan drop chance at Untrained. Below 1 = untrained washes come up empty more often. 1.0 = no penalty.",
        ["PAN/panYieldGm"] = "Pan drop-chance multiplier at Grandmaster. Chance only, never a doubled stack. 1.0 = no bonus.",
        ["PAN/traceStrengthApprentice"] = "Placer-trace strength at Apprentice I, where the pan stops being blind: the drop table is biased toward the ores actually in the ore maps BELOW the wash (never toward what is in the pan, so hauled gravel reads the ground it stands on). Below Master the signal wavers per wash. Set this AND traceStrengthGm to 0 to disable the trace and the wash whisper outright.",
        ["PAN/traceStrengthGm"] = "Placer-trace strength at Grandmaster, where the trace reads clean. Set this and traceStrengthApprentice both to 0 to switch off the trace and the whisper together.",
        ["PAN/treasureChanceThreshold"] = "Pan-table entries at or below this base drop chance count as the treasure tail (lore books, temporal gears, jewelry, gems, tuning cylinders) and climb on the separate bias below. 0 = nothing is treasure.",
        ["PAN/treasureBiasMaster"] = "Extra multiplier on treasure-tail entries at Master I, on TOP of pan yield. 1.0 = no bonus.",
        ["PAN/treasureBiasGm"] = "Extra multiplier on treasure-tail entries at Grandmaster, on top of pan yield. 1.0 = no bonus.",
        ["PAN/panWhisperChance"] = "0-1 chance per trace-eligible wash that the pan speaks a chat line naming an ore it feels below (a fixed 20 second per-player cooldown also applies). It never invents: every ore it can name is genuinely in the ground under the wash. 0 = SILENT, the trace still works.",
        ["PAN/panWhisperMinFactor"] = "Minimum propick-style ore factor the ground must carry before it is worth a word. Raise it so only rich ground speaks; set it above any achievable factor (1000) to silence the whisper while leaving the trace alone.",
        ["PAN/boreDepthLevel"] = "Minimum PAN level at which the prospecting-pick borehole reports ore DEPTH at all. Set above the Grandmaster cap (17) to switch the depth readout off entirely; lower it to grant the bore earlier.",
        ["PAN/boreDepthExactLevel"] = "Minimum PAN level at which the bore reports the EXACT band and records it to the shared depth store the Surveyor's maps carry. Below it the readout is a coarse first-strike depth and nothing is recorded, so raising this above 17 keeps the chat readout while taking depth off shared maps.",

        // ---------------------------------------------------------------- POT, Pottery
        ["POT/preserveUntrained"] = "Perish multiplier a keep-vessel carries when fired by an Untrained hand. Above 1 = a clumsy crock seals imperfectly. Clears to 1.0 at Novice. 1.0 = no penalty.",
        ["POT/preserveGm"] = "Perish multiplier on a vessel fired by a Grandmaster: the Potter's Mark. Below 1 = a masterwork crock keeps food. Multiplies the vessel's existing modifier, so it composes with sealing and any cook stamp. 1.0 = no bonus.",
        ["POT/copyVoxelsUntrained"] = "Duplicate-layer voxels per click at Untrained. Vanilla's own value is 4, so below that is the penalty. Novice I restores 4.",
        ["POT/copyVoxelsMaster"] = "Duplicate-layer voxels per click from Master I up, and the ceiling. The powered wheel is the mass-production path past it.",
        ["POT/clayformVoxelReference"] = "Filled voxels that pay exactly the configured clayforming Raw. Scaling is LINEAR in voxels on purpose: vanilla's four-at-once recipes are each exactly 4x their single, so linear is the only curve under which batching is neither punished nor rewarded. The default sits near a claypot (161 voxels).",
        ["POT/clayformVoxelMin"] = "Floor on the per-piece voxel multiplier, so the smallest ware still pays something.",
        ["POT/clayformVoxelMax"] = "Ceiling on the per-piece voxel multiplier. Binds only on outliers a potter builds once (a clay oven is 1748 voxels); the saturation K still owns the daily cap above it.",

        // ---------------------------------------------------------------- RAN, The Ranged
        ["RAN/rawDrifterTierStep"] = "Extra raw per drifter tier step above surface (deep, tainted, corrupt, nightmare, double-headed), so harder kills teach more. 0 = all tiers pay the same.",
        ["RAN/rawLocustMul"] = "Raw multiplier for a locust kill: swarm chaff is cheap practice. 1.0 = priced like anything else.",
        ["RAN/rawBellMul"] = "Raw multiplier for downing a resonating bell, which guards the swarm. 1.0 = priced like anything else.",
        ["RAN/steadyAimUntrained"] = "Aim steadiness at Untrained. Drift and twitch divide by this SQUARED, and the engine clamps it, so sway never vanishes and the dock has to be deep before it shows. NOTE: under Combat Overhaul this curve runs CLIENT-side off the compiled defaults, because the client cannot read RAN.json; editing it here only moves the vanilla floor. 1.0 = no penalty.",
        ["RAN/steadyAimGm"] = "Aim steadiness at Grandmaster: steady, never still. Same client-side caveat as steadyAimUntrained. 1.0 = no bonus.",
        ["RAN/reloadUntrained"] = "Nock, draw and reload cadence multiplier at Untrained. Below 1 = slower. 1.0 = no penalty.",
        ["RAN/reloadGm"] = "Reload cadence multiplier at Grandmaster. Ranged-only by construction. 1.0 = no bonus.",
        ["RAN/recoveryUntrained"] = "Multiplier on each projectile's OWN material drop chance at Untrained, so a flint arrow still breaks more than a steel one at every rank. Below 1 = fewer arrows back. 1.0 = no penalty.",
        ["RAN/recoveryGm"] = "Ammo recovery multiplier at Grandmaster. 1.0 = no bonus.",
        ["RAN/recoveryCap"] = "0-1 absolute ceiling on recovery chance after the multiplier, below certainty: some arrows always shatter. 1.0 removes the ceiling.",
        ["RAN/vanAccUntrained"] = "Vanilla-floor accuracy multiplier at Untrained, used when Combat Overhaul is absent. Kept conservative because the read site is client-core and unverified. 1.0 = no penalty.",
        ["RAN/vanAccGm"] = "Vanilla-floor accuracy multiplier at Grandmaster. 1.0 = no bonus.",
        ["RAN/vanDrawGm"] = "Vanilla-floor draw-speed multiplier at Grandmaster. 1.0 = no bonus.",
        ["RAN/misfireUntrained"] = "0-1 flash-in-the-pan chance at Untrained (firearms only, needs firearmsfork). An INTRODUCED failure: stock firearms fire deterministically. 0 = never misfires.",
        ["RAN/misfireApprentice"] = "0-1 misfire chance at Apprentice I, the knee of the curve where the worst is over. 0 = never.",
        ["RAN/misfireGm"] = "0-1 misfire chance at Grandmaster: the floor, deliberately never zero, because period and modern firearms both misfire on bad luck. 0 = immune.",
        ["RAN/thriftGm"] = "0-1 chance at Grandmaster that a reload spends no powder or wadding. Zero through Apprentice I, since nothing climbs above vanilla below parity. 0 = no bonus.",
        ["RAN/spillUntrained"] = "0-1 chance an Untrained reload spills an extra powder unit: the clumsy overpour. Fades to zero at Apprentice I, so the penalty and thrift bands never overlap. 0 = no penalty.",

        // ---------------------------------------------------------------- TAI, Tailoring
        ["TAI/warmthUntrained"] = "Warmth multiplier on a garment made by an Untrained tailor. Below 1 = a beginner's coat holds less heat. From Novice up warmth sits at flat vanilla: the rank climb is LONGEVITY, not warmth, and warmth above vanilla is the earned Grandmaster Warm choice only. 1.0 = no penalty.",
        ["TAI/wearUntrained"] = "Condition-loss multiplier on a garment made by an Untrained tailor. Above 1 = sloppy seams wear faster. 1.0 = no penalty.",
        ["TAI/wearGm"] = "Condition-loss multiplier on a Grandmaster's garment. Below 1 = a master's seams hold, so the coat keeps its warmth longer. This is the quality that CLIMBS with rank. 1.0 = no bonus.",
        ["TAI/coolingUntrained"] = "Cooling multiplier on an Untrained tailor's garment (needs a heat-of-day mod). Flat vanilla from Novice up; cooling above vanilla is the earned Grandmaster Cool choice only. 1.0 = no penalty.",
        ["TAI/emphasisBonus"] = "Extra fraction a GRANDMASTER's chosen emphasis adds: Warm lifts warmth, Lasting deepens the wear reduction, Cool lifts cooling. The choice is the player's own toggle on the Tailoring page, frozen onto the garment when it is made. 0 = emphasis does nothing.",
        ["TAI/fiberEconomyUntrained"] = "Per-fibre yield multiplier at Untrained. Below 1 = a beginner draws less twine and cloth from the same fibre. 1.0 = no penalty.",
        ["TAI/fiberEconomyGm"] = "Per-fibre yield multiplier at Grandmaster. Applied steadily at the hand spindle's batch output and as a fractional proc on a powered wheel or loom cycle. 1.0 = no bonus.",
        ["TAI/stationRawMult"] = "Raw multiplier on an ATTENDED station cycle (the mounted wheel, the mounted loom) against the same verb by hand. The hand spindle and the grid stay at 1.0. Every cycle registers and pays this fraction, rather than most cycles reading as repeats and paying nothing. 1.0 = a machine pays the hand rate.",

        // ---------------------------------------------------------------- TEM, Temporal
        ["TEM/gearCostUntrained"] = "Multiplier on temporal gears per translocator repair at Untrained. Above 1 = a beginner burns more gears. Clears to 1.0 at Novice. 1.0 = no penalty.",
        ["TEM/gearCostGm"] = "Gear-cost multiplier at Grandmaster: the fewest gears. Floored by the repair maths above zero, so a translocator always costs gears and there is no free transit. 1.0 = no bonus.",
        ["TEM/wardFuelUntrained"] = "Ward-fuel duration multiplier at Untrained. Below 1 = a beginner's fuelling burns faster. 1.0 = no penalty.",
        ["TEM/wardFuelGm"] = "Ward-fuel duration multiplier at Grandmaster: a master's gear fuels a ward longer. 1.0 = no bonus.",
        ["TEM/stabilityLossUntrained"] = "Multiplier on AMBIENT and STORM stability loss at Untrained. Above 1 = thin-skinned to time. Every DELIBERATE stability spend (meditation drain, Conjunction recipes) is exempt by construction, so this never shields a cost the player chose. 1.0 = no penalty.",
        ["TEM/stabilityLossGm"] = "Ambient and storm stability-loss multiplier at Grandmaster: weathers storms upright. Floored, so even a Grandmaster still loses stability in a Heavy storm. Kept modest because a class mod's own archivist trait adds to this rather than being re-scaled by it. 1.0 = no bonus.",
        ["TEM/manifestResistGm"] = "0-1 chance at Grandmaster to shrug off an INVOLUNTARY manifestation drain entirely (rust mob, devastation thinness). 0 through Novice. A reserved cross-mod seam: inert until a mod calls it. 0 = no bonus.",
        ["TEM/stormCueLeadGm"] = "In-game days of storm warning a Grandmaster gets: the top of the personal warning ladder. Untrained gets NOTHING, Novice I gets about 32 real seconds, and it is a straight line between. The default 0.35 is exactly the stock warning vanilla used to broadcast to everybody, so a Storm-Warden's mastery is keeping what everyone else lost. Needs stormShiftTEM on in global.json.",

        // ---------------------------------------------------------------- WOO, Woodcutting & Forestry
        ["WOO/staminaUntrained"] = "Multiplier on stamina consumed per axe swing at Untrained (ImmersiveMining). Above 1 = tires fast. 1.0 = no penalty.",
        ["WOO/staminaGm"] = "Axe-stamina multiplier at Grandmaster. 1.0 = no bonus.",
        ["WOO/leafYieldUntrained"] = "Multiplier on stick and sapling drops from felled leaves, at Untrained. Below 1 = the beginner shreds the canopy. 1.0 = no penalty.",
        ["WOO/leafYieldGm"] = "Stick and sapling drop multiplier at Grandmaster. 1.0 = no bonus.",
        ["WOO/windfallGmChance"] = "0-1 chance, weighted toward Grandmaster, that a felled leaf pays a bonus stick or sapling. 0 = no bonus.",
        ["WOO/fellSpreadUntrained"] = "Half-width in DEGREES of the cone a felled tree may fall into, at Untrained. Wide = unpredictable. The tree falls along the struck face, rotated by a random angle drawn from this cone.",
        ["WOO/fellSpreadGm"] = "Cone half-width in degrees at Grandmaster. Tight = the tree goes where the master aimed it.",
        ["WOO/fellBiasUntrained"] = "Degrees the fall cone's CENTRE is rotated TOWARD the feller at Untrained. Positive = real danger to the beginner. 0 = no bias.",
        ["WOO/fellBiasGm"] = "Degrees the fall cone's centre is rotated at Grandmaster. Negative = away from the feller. 0 = no bias.",
        ["WOO/fellImpactDamage"] = "Flat damage a falling trunk deals when it connects along its swept path. Replaces the stock damage, which is inert on a pivoted fall and only ever checked the log's final landing cell, so a trunk sweeping through you never connected. Rank governs WHERE the tree lands, never how hard it hits. 0 = trees are harmless.",
        ["WOO/fellDamageCooldownMs"] = "Minimum milliseconds between falling-trunk hits on the same victim. This is what stops a ten-log tree landing ten hits.",
        ["WOO/pitFloorUntrained"] = "Floor of the charcoal-pit efficiency roll at Untrained (vanilla rolls uniform 0.5 to 1.0). Skill raises the FLOOR, never the ceiling: a perfect burn is already possible by luck, so a Grandmaster is CONSISTENT rather than lucky.",
        ["WOO/pitCeilUntrained"] = "Ceiling of the pit efficiency roll at Untrained. The only rank that lowers the ceiling at all: this is the botched-burn penalty band. 1.0 = vanilla ceiling.",
        ["WOO/pitFloorNovice"] = "Floor of the pit efficiency roll at Novice. 0.5 restores vanilla exactly.",
        ["WOO/pitFloorApprentice"] = "Floor of the pit efficiency roll at Apprentice.",
        ["WOO/pitFloorJourneyman"] = "Floor of the pit efficiency roll at Journeyman.",
        ["WOO/pitFloorMaster"] = "Floor of the pit efficiency roll at Master.",
        ["WOO/pitFloorGm"] = "Floor of the pit efficiency roll at Grandmaster. Yield still never exceeds vanilla's own ceiling, so there is no magic charcoal.",
        ["WOO/markBurnTempBonus"] = "Degrees added to burn temperature by Grandmaster charcoal: the Collier's Mark. Honoured at the firepit for whoever burns it, not for the collier. 0 = no bonus.",
        ["WOO/markBurnDurationMul"] = "Burn-duration multiplier on Grandmaster charcoal. 1.0 = no bonus.",
        ["WOO/fellLogKnee"] = "Logs in one swing up to which the felling multiplier is LINEAR, so an ordinary tree banks exactly what per-log grants used to. Above it the curve is square-root compressed. Set near an ordinary tree's log count.",
        ["WOO/fellLogCap"] = "Hard ceiling on the felling multiplier for one swing, so a monster tree is worth meaningfully more without being a day's practice in a single swing.",
    };
}

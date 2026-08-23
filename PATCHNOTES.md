# Patch notes

The Almanac: Trades, Callings & Mastery.

---

## 0.5.0 (in development)

The gap pass: every domain reviewed and the weak ones lifted toward the bar Metalworking
sets. Groundwork first: the pre-ascension fix list from the 2026-08-17 domain verb review,
because Grandmaster work builds directly on this state.

- The farmer learns each crop by name. Knowledge of a crop is now earned by harvesting it,
  not granted for existing: every real harvest teaches you that crop a little, and what you
  know of one crop spreads partway through its family, never all the way. Vanilla used to
  show farmland moisture, nutrient figures, and every crop's demands to anyone who looked.
  Now an untrained eye reads nothing in the soil; a Novice reads it in rough words; an
  Apprentice reads bare ground in full figures, but a planted crop stays a stranger until
  you have grown it (or its kin) enough times to know it; and from Journeyman, once a
  family is truly known, the ground itself remembers for you what it last bore. Rank
  decides what your hands do; familiarity decides what you know. They never cross.

- The legumes give back. Peas, fava beans, lentils, soybean, peanut, alfalfa, and licorice
  now return nitrogen to the farmland they grow in, each to its own measure and never past
  a cap: fava the strongest of the annuals, alfalfa the strongest of all, and soybean
  barely at all, because its harvest carries away more than the plant banks. Crop rotation
  stops being lore and starts being practice.

- Some crops are picked, not pulled. Chives, leaf lettuce, eruca, estragon, cucumber,
  tomato, and alfalfa can now be harvested by hand at their bearing stage: the plant gives
  a small pick and falls back to regrow on the farmland's own clock. Breaking the plant
  remains the full and final harvest. Keep picking small, or pull the plant; that is the
  standing trade.

- Yield answers to the crop, not to a single curve. The flat Untrained harvest dock is
  replaced by a per-crop, per-rank yield table in server config, generated at the old
  behaviour on first run so nothing changes until it is deliberately tuned, with a master
  switch that removes TCM's hand from yield entirely.

- The kill ledger stops counting the barn. Hunting credit for a kill now applies the same
  livestock fence as combat: owned, tamed, domesticated, or an established captive lineage
  (generation 2 and up) is husbandry, not the hunt. Before this, only the ownership checks
  applied, so slaughtering bred stock banked wild-kill practice. The kill's repeat guard
  also moves from a once-per-second window to the same species-and-area shape combat uses,
  so pen slaughter collapses to a single credit instead of paying at exactly the cadence
  slaughter runs at. The per-species kill tally behind the Hunter's Map reads honestly now,
  which matters more than the practice: that tally is the record a Hunting ascension will
  one day be judged against.

- One fire no longer shields the rest of the line. When an overdriven wooden part ignited,
  the burning part was torn out of the running machinery mid-inspection, which quietly
  abandoned the rest of that tick's sweep: every other overheating part skipped its check
  and its discharge until the next half second, making ignitions self-throttling on a busy
  line. The sweep now takes its notes first and lights its fires after, so every part over
  the line gets its roll on every tick.

- The Glassmaker's Mark names the glassmaker. The maker stamp and the thermal window used
  to be written when a piece was LOADED into the annealer, by whoever held it: the mark
  could name the wrong hand, a Grandmaster could stamp a novice's work by doing the
  loading, and the ranked shatter window did not exist during the carry from the mold to
  the annealer, which is exactly when a cooling piece is in danger. The stamp now lands
  the moment a blown or cast piece reaches its maker's hands, survives the workbench's
  cold-working steps, and the annealer only stamps a piece that arrives with no mark at
  all, naming the holder, as it always did for pieces from before this change. A freehand
  piece annealed straight off the pipe still carries no mark; that gap is known and stays.

- A rank-up banner survives a disconnect. The ceremony held for a player inside login
  protection was queued in memory only, so logging out within a few seconds of an
  overnight consolidation discarded the banner permanently. The rank itself was always
  safe; the moment was not. Held ceremonies now ride the world save and greet the player
  on their next login, gated by the same grace and protection rules as a fresh one. This
  is groundwork as much as courtesy: the ceremony pipeline is what a Grandmaster
  ascension will one day extend, and it had to be durable first.

- A Lasting brand outlives a relog. A branded potion's extended duration was applied at
  the drink and forgotten: logging out and back mid-effect rebuilt the effect at its base
  duration and clamped the remaining time down to it, so the Grandmaster's headline
  emphasis quietly died on every reconnect. The brand's multiplier now rides the effect's
  own save record, written at the drink and read back at the restore, so the effect
  resumes with exactly the time it had left. Potency was never affected; only time was.

- A rust-mob kill wakes the temporal sense. Killing a drifter, shiver, bowtorn, bell or
  locust now banks Temporal practice at half the kill's worth, alongside the full credit
  to the weapon's own calling. The tier scaling rides along: a deep drifter teaches more
  than a surface one. This is the practice loop the temporal repair gate stands on.
- Butchering a raised animal pays the barn and the knife alike. Dressing a beast that a
  player raised (the trough's own attribution) now splits its practice evenly between
  Hunting's dressing and a new Farming butchery verb, same total as before. Wild game is
  untouched: no stamp, full Hunting credit, exactly as it was.

- The fighter's deeds go on record. A perfect parry, a ranged kill, and the long kill
  from past the Marksman's Eye's own reach, where no aiming aid can have helped the
  shot, now each write a silent tally to the fighter's own knowledge, the same book
  the Storm-Warden's deeds already keep.
  Nothing pays practice and nothing shows off; these are the ledgers a Melee or
  Marksmanship ascension will one day be judged against, and until now both callings
  detected the feats and forgot them in the same breath.

- The drying rack pays whichever trade the drying served. Dried herbs off the alchemy
  rack pay Alchemy exactly as before. Dried charcuterie off the same rack, meats and
  sausages hung there thanks to Expanded Foods, now pays Cooking's drying credit, the
  same one the meat hooks and drying frames pay, where before it passed every honesty
  check and paid no one at all. Same anti-farm gates, same once-per-rack-per-minute
  pace; nothing changed for herbs.

- The refined tackle asks for Apprentice hands. Ithania's fish trap and fish net now
  open at Apprentice I Fishing: below that, the trap refuses to be set (it stays in
  your hand) and the net refuses the swing. Everything around them stays free at any
  rank: baiting and emptying a trap someone else set, the worm bin and compost bin,
  the fillet knife, the logbook and tags. A Master sets the trap line and a Novice can
  run the collection rounds, the same division the temporal repair gate draws. The
  primitive gear, basket, trotline, weir and spear, remains the road that gets you
  there, and all of it now wants bait: an unbaited basket or trotline catches nothing
  on this server, with the weir left as the one baitless exception, slow and patient
  by design.

- The brick oven asks for a Journeyman. The Stone Bake Oven, the settlement-scale
  bakehouse, now opens at Journeyman I Cooking: below that rank it refuses every
  interaction. No firewood goes in, no pan or cook pot lands on the top, no loaf
  enters the chamber, so no fuel is ever wasted on a bake its cook cannot finish.
  Everything beneath it stays free at any rank: the firepit, the clay pot, the clay
  oven, the quern, the mixing bowl, the whole stone and clay kitchen. Journeyman is
  the rank where work starts bearing your name, and the oven that feeds a village
  belongs to it.

- The potter earns the broader stroke. Clayforming's 2x2 mode now opens at Apprentice I
  Pottery and the 3x3 at Journeyman I, adding and removing alike; below the rung the
  click lands, nothing moves, and a word says why. The single-voxel stroke is never
  gated at any rank, because pottery's day-one reachability is the point of the
  calling. The duplicate-layer stroke is not gated but learned: where the game always
  copied a flat four voxels per click, an untrained hand now manages two, Novice I
  restores the four, and the count climbs to six by Master and holds. The powered
  pottery wheel is untouched: it remains the accessibility option at low rank and the
  mass-production path when driven.

- The tool remembers every hand that made it. A crafted handle now carries its
  woodworker's quality, and a crafted binding its maker's, leather bindings by the
  hunter's hand, fibre and cloth by the tailor's, the same banded figures the smith's
  head has always carried: five percent more part durability from a Journeyman's
  work, ten from a Master's, fifteen from a Grandmaster's. The quality is stamped on
  the part when it is made and settles into the tool when it is assembled, through
  the tinkering system's own part durability, so nothing double counts. A plain
  stick was never made by anyone and stays plain, and work below Journeyman stays
  unmarked, because a mark always means something. One tool can now carry four
  names: the smith at the head, the woodworker at the haft, the tailor or hunter at
  the binding, and the engineer who keeps it serviced.

- The brewer learns to read the dark. A sealed barrel has always been a black box; now
  your Brewing rank lights it up when you look at one. An Apprentice reads roughly how
  many days the seal still needs. A Journeyman reads what the batch is turning toward
  and the time to the day. A Master reads the count it should give when it opens.
  Below Apprentice the dark keeps its counsel, exactly as before; nothing vanilla
  showed was taken away, because vanilla showed nothing at all.

- The Brewmaster's measure: a Grandmaster's seal can pay over the rating. One sealed
  ferment in four from a Grandmaster's hands comes out a tenth over its rated count,
  never less than one portion extra, barrel and clay fermenter alike. It fires at the
  top rather than climbing to it, the same shape as the Cook's Mark, and it lives
  entirely in the count, so no liquid ever carries an attribute the barrel could
  erase. Spirits, preserves and brews all qualify; the measure is the first thing the
  calling gives its Grandmaster beyond the words on the label.

- No remedy without a maker outdoes the best maker's work. An unbranded healing item,
  loot, trader stock, anything that never knew an alchemist's hands, used to wake a
  downed player at FULL health, straight past the hard 80 percent ceiling a
  Grandmaster's own work respects. It now wakes them at the unbranded floor, roughly a
  fifth of their health, exactly as the remedy ladder always claimed. Whose remedy you
  carry matters most at the moment it matters most.

- The herb rack remembers its alchemist, not its last visitor. Taking dried herbs off a
  rack used to re-mark the rack with the taker's own rank, so a Master's preserving
  touch vanished the moment anyone else collected from it. Only placing herbs marks the
  rack now; taking never does.

- An annealer batch banks once, as its own book always said. Retrieving a finished batch
  across several seconds used to pay the annealing credit again for each second the
  unloading took; now loading the annealer arms a single credit and the first finished
  piece taken collects it. Nothing else about annealing changed.

- Building the machine teaches the builder; running it teaches no one. The mill was ruled
  to grant nothing for use, and this is the other half of that ruling: the first time a
  machine you built genuinely runs, power delivered to it or its first output turned, the
  builder banks a large one-time Engineering credit. Every machine type pays its own
  first: the first windmill, waterwheel, helve hammer, pulverizer, mechanized quern,
  chopper and sawmill, and with IndustrialStory the first reverberatory furnace that
  takes heat with its structure whole. Repeats of a type pay half of the one before,
  never quite reaching nothing. A decorative or misassembled machine pays nothing at
  all: the credit waits until the thing runs, however long that takes, and it waits for
  its builder, not for whoever stands near. A hand-cranked quern never counts; the same
  quern first turned by an axle does.

- Rigging an automated station takes the trade. The Immersive Woodworking chopper and
  sawmill now ask Apprentice I in Engineering to assemble: below it, an incomplete
  station refuses the part and the part stays in your hand. A complete station is
  another matter entirely: swapping a worn head is maintenance and passes at any rank,
  and feeding logs or taking lumber was never gated and still is not.

- Trapping pays for the first time, and the trap answers to its setter's hands. The catch
  in a snare or deadfall was never an item sitting in the trap: it is the animal dying
  beside it, and nothing ever credited that, so the trapping ledger has been empty since
  the day it opened. Now a trap kill banks trapping practice for the trap's owner. The
  owner's rank works the trap itself: a green hand's set loses its bait and springs empty
  more often than the stock numbers, a master's set fails not much more than half as
  often, and no rank ever makes a trap certain. At the top, one kill in four leaves the
  trap still set with its bait untouched, the line still working. Unowned traps, and
  traps whose owner is offline, behave exactly as stock and bank nothing.

- The raised split follows the carcass to the workstation. Butchering a player-raised
  beast at the hook or the table now pays the barn and the knife alike, half Hunting and
  half Farming butchery, the same split the field knife has used since it landed.
  Before this the attribution died twice on the way to the station: the carcass pickup
  copied the animal's weight, generation and drop table onto the item but not who
  raised it, and the skinning stage rebuilt the item keeping only the mod's own three
  attributes. The stamp now survives the pickup and every stage of the work. Wild game
  at the station pays full Hunting exactly as before.

- Mending temporal machinery takes the trade; riding it never did. Repairing a
  translocator or recharging a spent teleporter now asks Novice IV in Temporal. Anyone
  still steps through a working machine, exactly as before: standing on a teleporter
  takes no skill, restoring one is the whole point of the calling. Below the gate the
  interaction stops before a single gear or part is spent, with a word about why. The
  rung is a server config value, and warding plus rust-mob kills are two ungated roads
  up to it.

- The Storm-Warden's deeds go on record. Surviving a temporal storm from its first gust
  to its last without dying, sheltered or not, now writes a per-strength tally to the
  player's own knowledge, and finishing a translocator repair or a teleporter recharge
  writes another. Nothing pays practice here and nothing shows off; these are the
  ledgers a Temporal ascension will one day be judged against, and until now the domain
  kept no book at all.

- The storm's first warning now finds each player by their rank, and the ladder is the
  warning. The approaching cues, Temporal Symphony's warning call, bells, fog and
  tremors, or the plain chat line on servers without it, no longer land on everyone at
  the same fixed moment. An Untrained player gets nothing at all: too out of tune with
  the rust and the unbound to feel the signs, the sky simply breaks, and one line
  afterward says the attuned felt it coming. The first rank of Temporal buys the bells
  with seven seconds to spare once the last toll fades, every level after buys about a
  minute more, and a Grandmaster tops out at the familiar quarter hour the game used to
  hand everyone for free. The bell still counts out the storm's strength for every
  rank, by ruling: the toll is predictable, and reading "can I survive this outside"
  from it is the player's own call. The written storm sense arrives in the same breath
  as the bells now, naming the strength from Journeyman up, instead of running a day
  ahead. Temporal Symphony itself is untouched and renders every cue exactly as before;
  its own timings simply arrive addressed to you now. One config switch returns the
  whole thing to stock.

- Storm resilience works off The Quire. The Temporal rank curve that slows stability
  loss (and speeds it for the untrained) was applied through a stat only
  SpecializedClasses reads, so without that mod the whole resilience line silently did
  nothing. Servers without SpecializedClasses now get the same curve applied directly
  to the vanilla stability tick, losses only, never the recovery; deliberate stability
  spends stay exempt exactly as before. With SpecializedClasses present nothing
  changes, and the two paths can never stack.

## 0.4.40 (hotfix, released 2026-08-21)

One fix, nothing else. The trough breakage was reported against the 0.4.39 build and
its fix landed hours after 0.4.39 deployed.

- Marked produce feeds troughs again. The trough compares feed with the game's standard
  ignore-list, which does not know our marks, and every vanilla trough recipe names an exact
  item. So the moment your rank put a "Grown by" line on your harvest, that harvest stopped
  fitting the trough: marked grain bounced off an empty trough, and marked and plain crops of
  the same kind refused to share a filled one, in both directions. Which crops failed depended
  on your rank when each was picked and on what the trough already held, which is why it looked
  arbitrary (reported by LauCaRo, 2026-08-21). The trough is mark-blind now, and feed sheds its
  mark as it goes in: a trough launders nothing, the animal reads no tooltip, and feeding has
  always been paid by the FILLER's hand, never the crop's pedigree. Only the portion that enters
  loses its mark; the stack in your hand keeps its keeping. Troughs filled with marked feed
  before this fix clean themselves the first time anyone touches them.

## 0.4.39 (released 2026-08-21)

- Finishing a knit no longer ends your session. The Tailor's Mark stamp looks through your
  inventories for the garment you just made, and it looked through all of them, the creative
  inventory included. On a dedicated server that one has no tab built, so asking it how many
  slots it holds throws, and an exception thrown inside a Harmony postfix takes the player's
  connection with it ("Threw an exception at the server"). Observed live on The Quire
  2026-08-20. The creative inventory is skipped outright now, and any other inventory that
  refuses to be read is logged and passed over: a cosmetic mark must never cost a session.

- The retort finally teaches. `is-first-retort` targeted `retortchamber*`, but Industrial
  Story registers the block as `retortsmelter` (the blocktype FILE is retort-chamber.json;
  the code field inside is what counts), so the trigger never fired and the smelters-yard
  retort chapter never revealed for anyone. Both patterns corrected.
- Cookware no longer earns a cook's mark. On the cooksInto path (candles, potash, glue,
  and modded pot chemistry) vanilla parks the VESSEL in the firepit input slot and the
  product in cooking slot 0; the meal-pot stamp picked the changed slot and marked the
  pot itself ("Carelessly cooked" dirty pots, surfaced by Conjunction's rust-touched pot
  2026-08-17). The stamp now redirects from any cooking container to the actual product.
- Alchemical matter is ALC, never COO (ruled 2026-08-17). Item types may declare
  `attributes.tcmCraftDomain: "ALC"` (Conjunction's reagents do); grinding them at the
  quern and working them at the pot then banks the new ALC `reagentwork` technique and
  abstains from COO/FAR entirely — no milling credit, no cook's mark, no serving proc.
  ALC small-m rises to 3 with an ALC-matter supplier installed; the available-technique
  clamp keeps other servers unchanged.

- A dish keeps both names now (ruled 2026-08-18, superseding 0.4.38's cook-displaces
  rule). The grower's line renders above the cook's in the ordered block, so a plate says
  who grew it and who cooked it. The numbers do not move: the cook's hand still governs
  how a dish keeps and feeds, and the grower's effect stays on raw produce and ingredients.
- One hand, one line. When the same player grew and cooked, the two lines fold into
  "Grown & cooked by". Assembled tools fold the same way: a tool forged, hafted and bound
  by one maker reads "Forged, hafted & bound by" instead of three lines. Grandmaster forms
  never fold: Heirloom of, Signature dish by and a masterwork head each keep their own line.

- The fold reaches the common tool. An assembled tool usually carries its maker's own
  mark, which used to render separately from the part lineage, so one maker throughout
  still read as two lines. When the tool's maker also made the handle or the binding
  (and the mark is below Grandmaster), the forged credit now folds into the lineage
  line, quality figure and all: "Forged, hafted & bound by X (+12% durability)". A
  masterwork still keeps its own line.

- The Grandmaster signature promises nothing. "Signature dish by {name}. It will keep."
  loses its second sentence: the freshness line directly above already states the composed
  keeping figure, so the prose could only repeat it or, on a pie, contradict it. One line
  now serves every dish and the separate pie wording is retired.

- A shattered quench taught you nothing, and now it really does not. Practice was paid
  from the cooling tick, which runs before the piece is out of danger, so a blade that
  burst in the barrel still banked the work (and said so, since practice notes are on by
  default). The credit moved to the settle, which vanilla reaches only on a quench that
  held. Same amount, same repeat rules, paid on success.

- Practice stops at Master IV, for everyone. A class born to a trade could climb that
  trade all the way to Grandmaster on practice alone, silently, with no commission, no
  masterwork, no teaching and no cap, while every other class stopped at Master IV. That
  is the design inverted: Grandmaster is a declared ascension and nobody skips it, least
  of all the class born to the trade. Positive affinity keeps what it was always for, an
  earlier start and a faster daily fill. No rank already attained is touched. A ceiling
  only discards incoming practice, so anyone standing at Grandmaster stays there.

- A dormant calling keeps what it earned. Three callings exist only while another mod
  does: Glassmaking, Arcana and Beekeeping. If that mod went missing, loading your save
  put the rank and the banked practice through guards written for gameplay, which zeroed
  both and wrote the zeroes back to disk. One login without Rustbound Magic and an
  Arcana rank was gone for good, whether or not the mod ever came back. Restoring a save
  is not the same act as earning, so it no longer takes the earning path. The guards
  themselves are unchanged: a dormant calling still banks nothing and still cannot climb.
  A rank already lost to this cannot be recovered, since the save on disk holds nothing
  to restore.

## 0.4.38 (released 2026-08-16)

**Living document.** Updated as work lands, not written at release. Everything below is
measured against 0.4.37, the build currently live on The Quire.

### Added

**Provenance follows food all the way to the plate.**

A Grandmaster's rice used to lose everything the moment it became flour. Marks now survive
processing: milling at a quern, baking in a clay oven, and any recipe on the crafting grid
carry the maker's mark onto whatever comes out, so a loaf remembers the field it grew in and
the hands that shaped the dough. Where several marked ingredients go in, the highest-ranked
of each kind comes out, one name per trade.

Two rules keep it honest. The cook's mark **replaces** the grower's on anything edible: no
matter how well a crop was raised, a bad cook still ruins it and a great one gets more out of
it, so a finished dish carries the cook and nothing else. And a marked stack still stacks
with itself, which was the whole reason milling a full sack of grain had to work.

**A skilled cook's meals feed you better.**

Meals carry a satiety bonus alongside the slower spoilage.

**Pies made a different bargain.** A pie's high satiety has always been priced by how fast it
rots, so pies take the satiety bonus in full and give up the slow-spoil signature: a master's
pie feeds more and rots honestly, at its natural rate. Careless baking still ruins one, the
same as any other dish. Pastries and other single-serving foods carry the mark and the
spoilage effects but no satiety bonus.

### Fixed

**The Potter's Mark belonged to whoever lit the kiln.**

Shaping a crock and handing it to somebody else to fire gave *them* the mark. It now belongs
to the potter who shaped the clay, stamped when the last piece is placed and carried intact
through the firing, whoever lights it. Lighting the kiln still earns the firing practice; the
two are separate.

The mark is also now limited to ware that can actually use it, meaning the vessels that hold
food: the crock and the storage vessel. Molds, tiles, shingles, bullets, bowls and flowerpots
no longer carry a decorative line, which also keeps them stacking with everybody else's.

**A masterwork storage vessel preserved nothing.**

It said "it keeps what it holds" and it did not. Storage vessels and crocks are built on two
different foundations in the game and only the crock's was ever wired up, so a vessel showed
the potter's line and the percentage while its contents spoiled at the ordinary rate.

The vessel now preserves for real, and its own "Stored food perish speed" panel moves with it.
Rice flour in a pair of identical vessels, measured: 83 days in a Grandmaster's, 64 days in an
Untrained potter's.

Worth knowing which way that cuts. An UNMARKED vessel is neutral. A vessel thrown by an
Untrained potter is worse than neutral, because sealing badly is the Untrained penalty, and the
mark now lands when the clay is shaped rather than when the kiln is lit. Early vessels will
carry it.

**Clayforming never granted a single point of practice.**

The Pottery skill listened for an event the game only raises for ware that is handed straight
to you. Every clay item in the game is set down on the ground instead, so bowls, crocks,
crucibles, flowerpots, jugs, lamps, planters, pots, storage vessels, molds and watering cans
all granted nothing. Pottery's main verb has been dead since it shipped. It is now measured
where the ware is actually produced.

**A cook's mark vanished when the serving emptied the pot.**

Filling a crock from a pot lost the mark, while filling a bowl from the same pot kept it. It
looked like the crock was at fault; it was the pot. A crock takes four servings and a bowl
takes one, so the crock drained the pot dry, and an emptied pot is swapped for a fresh empty
one that remembers nothing. A bowl filled from a pot holding its last serving lost the mark
the same way. The mark is now read before the pot can be emptied.

**Spoilage figures on marked food were telling you the wrong number.**

When more than one Almanac mark sat on the same item, each domain printed its own spoilage
effect as though it were the whole story. A dish both grown and cooked by Grandmasters
showed "spoils 30% slower" twice, when the two effects multiply and the real figure was
about 51%. Worse in the other direction: an Untrained cook working a Grandmaster grower's
produce showed "spoils 15% faster" and "spoils 30% slower" at the same time, when the true
result was roughly 19.5% slower.

The per-mark spoilage clauses are gone. The number now rides the game's own freshness line,
which has always carried the combined truth because it divides the remaining fresh hours by
the fully composed rate. That line is annotated with what the Almanac contributed, so you
see one honest figure instead of two misleading ones.

**Untrained cooking says so again.**

Food cooked by someone with no Cooking rank spoils faster, and for a short while that
penalty applied with nothing on the item to explain it. Untrained dishes now read
"Carelessly cooked" in red.

It deliberately carries no percentage. The mark line tells you *why* the food is worse; the
freshness line above it tells you *by how much*, and that one is the composed figure across
every Almanac effect on the item, so the two can never disagree. If a server tunes the
Untrained penalty away entirely, the line does not appear.

**Reaching the top rank announced "Grandmaster I".**

Grandmaster has been terminal and unnumbered since the 2026-07-15 ruling, but an older copy
of the rank-naming code still appended a sub-level numeral to it. The rank-up line and the
ledger now both read "Grandmaster".

**Items carrying more than one maker's mark read as one block.**

A crock thrown by one Grandmaster and filled by another carries two marks. Those used to
render as two separate paragraphs with a blank line stranded between them, in an order
neither you nor the mod chose. They now sit together as a single block, in a fixed order
that follows the chain of making: what it was grown or made from, then what was done to it,
then the vessel holding it. A crock of stew is a stew first.

### Changed under the hood

Nothing in this section changes how anything plays. It is recorded because one item has a
deprecation attached to it.

**Metalworking maker's marks now store a rank level rather than a rank band.**

A finished piece froze its maker's standing as a band (Journeyman, Master, Grandmaster),
which threw away which of the four steps inside that band the smith actually held. It now
stores the level itself, so a Journeyman IV's work is distinguishable from a Journeyman I's.

Nothing visible changes: the provenance line, the durability bonus and the Grandmaster edge
all read the same as before, because those are still decided per band by design.

**Deprecation.** Marks written before 0.4.38 use the old attribute and are read through a
compatibility path, so tools in existing singleplayer worlds keep their provenance and their
buffs. That compatibility path will be **removed in 0.5.0**. Releases leading up to 0.5.0
will carry a reminder. After removal, a tool stamped before 0.4.38 falls back to a plain
"Made by" line and keeps its durability bonus, but a Grandmaster piece loses its masterwork
line and its wear resistance. Reforging it under 0.4.38 or later re-stamps it in the new
format.

**One arbiter owns the provenance block.** Seven domains each carried their own patch on the
same tooltip method, every one claiming last place in the queue. With nothing to break the
tie, their order came from the order the runtime happened to load the classes, which is
stable for a given build and can shift when an unrelated class is added. Each also added its
own blank line, so the spacing grew with the number of marks. They are now seven plain
functions feeding a single arbiter that owns order and spacing, which also gives the mod its
first vantage point on all of an item's marks at once. That matters for the one preservation
figure still stated per domain, on crocks, which now has somewhere a rule could live.

**Other internal work.** Rank boundaries were spelled a dozen different ways across the mod
and now resolve from a single definition, so changing the shape of the ladder can no longer
desync one domain from another. The ladder's own documentation was corrected in code and in
five design documents; it describes 18 states topping out at level 17, which is what the
engine has always done. A leftover attribute that was written on every marked tool part and
read by nothing is no longer written. One redundant rank test in the Engineering overheat
readout was removed.

### Known, not yet fixed in this build

- **Overheat ignition stops early.** When an overdriven wooden part ignites, the rest of
  that tick's sweep is abandoned, so other overheating parts skip their check and their
  discharge until the next half second. It does not crash and it does not spam the log; on a
  busy line it makes ignitions quietly self-throttling.
- **Rank-up banner can be lost.** Disconnecting within five seconds of an overnight
  consolidation discards the banner permanently. Ranks themselves are correct and persisted;
  only the ceremony is missed.
- **Install guidance is stale.** The mod description advertises Illuminated 0.0.14 or newer,
  but the code requires 0.1.4. Anything between satisfies the description and then trips a
  version error in the log. *FIXED in 0.4.39: modinfo now states 0.1.4 and twenty-two domains,
  matching the runtime gate.*

### Open questions

- Which other Untrained penalties across the domains are currently invisible on the item
  they affect. Cooking is done; the rest get looked at as they come up. The standing rule is
  that a penalty which changes the rules has to name itself on the item, and that the reason
  and the magnitude live on separate lines so neither can contradict the other.

---

## 0.4.37 and earlier

Not yet backfilled. Release history lives in `Releases/` and in the commit log.

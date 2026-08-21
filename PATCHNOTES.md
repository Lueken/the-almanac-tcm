# Patch notes

The Almanac: Trades, Callings & Mastery.

---

## 0.4.39 (in development)

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

- Finishing a knit no longer ends your session. The Tailor's Mark stamp looks through your
  inventories for the garment you just made, and it looked through all of them, the creative
  inventory included. On a dedicated server that one has no tab built, so asking it how many
  slots it holds throws, and an exception thrown inside a Harmony postfix takes the player's
  connection with it ("Threw an exception at the server"). Observed live on The Quire
  2026-08-20. The creative inventory is skipped outright now, and any other inventory that
  refuses to be read is logged and passed over: a cosmetic mark must never cost a session.

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

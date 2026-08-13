# Patch notes

The Almanac: Trades, Callings & Mastery.

---

## 0.4.38 (in development)

**Living document.** Updated as work lands, not written at release. Everything below is
measured against 0.4.37, the build currently live on The Quire.

### Fixed

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
  version error in the log.

### Open questions

- Which other Untrained penalties across the domains are currently invisible on the item
  they affect. Cooking is done; the rest get looked at as they come up. The standing rule is
  that a penalty which changes the rules has to name itself on the item, and that the reason
  and the magnitude live on separate lines so neither can contradict the other.

---

## 0.4.37 and earlier

Not yet backfilled. Release history lives in `Releases/` and in the commit log.

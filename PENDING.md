# PENDING: almanactcm 0.5.8 (unreleased)

Everything staged for the next release, and nothing else. **Read this before building a zip or
deploying.** The tree may hold work you did not put there, put in by a session you cannot see.

Append as work lands; do not rewrite history here. When 0.5.8 ships, empty this file and set the
heading to the next version.

Convention: `CONVENTIONS.md` § 10 (workshop root).

---

## Staged in 0.5.8

- **Butchering crash.** 2026-09-10, committed `44ceefb` on `main`. Reaching for the carcass of an
  animal that carried a raiser's mark raised a server-side error and disconnected the player. The
  carcass credit check walked the creative palette, which has no open tab on a server.

- **Oven credit exploits, three of them.** 2026-09-10, uncommitted on `main`. The oven decided
  whether something taken out of it was a finished bake by matching substrings on the code path, so
  firewood taken back out of an unlit oven paid a full baking credit every two seconds, and so did a
  raw pie and an ExpandedFoods doughball. Replaced with a classifier that walks the item's own
  baking chain. Verified against all 29 real chain states in the pack.

- **COO cooking XP retune (A through E).** 2026-09-10, uncommitted on `main`. Direct-heat cut to
  `1.0/25`; pie assembly credited as a new `pastry` technique at the crust; oven credit doubled for
  a `LargeItem`; simmering granted practice for the first time; COO `TierTotals` raised 25% to
  `[188, 625, 1750, 4000, 8125]`. Casual cooking down 13%, dedicated cooking up 8%.
  Audit and reasoning: `docs/design/2026-09-10_coo-cooking-xp-audit.md`.

- **FAR Stockman's Eye.** 2026-09-10, uncommitted on `main`. **Not my work** — another session
  landed `source/Domains/FarStockmansEye.cs` plus ten `far-stock-*` lang keys at 12:18 while this
  file was being written, and rebuilt the 0.5.8 zip over mine at the same minute. Its own header
  describes it as the Grower's Eye applied to livestock: it makes vanilla's hidden milking
  `aggroChance` legible by rank instead of changing it, prompted by a report from Vinni on
  2026-09-09. I have not read it beyond that header and have not verified it. Whoever owns it
  should replace this entry with a real one.

  The zip on disk at 403,953 bytes is that session's build. It does contain all of the above,
  checked by symbol: the oven classifier, the pie seam, `pastry`, `simmering` and `cxPastry` are
  all in it, alongside `FarStockmansEye`. So the artefact is complete, it is simply not the one
  I built, and it now carries work nobody has listed anywhere but here.

## Not in the zip, needed at deploy time

- **COO `TierTotals` does not propagate.** It is not part of the three-way config merge (only
  `Techniques` and `Bonus` are), so an already-booted server keeps its old ladder no matter what
  ships. The Quire's `ModConfig/almanactcm/COO.json` needs the new array written by hand at deploy,
  and so does any other existing server.

## Deployed to The Quire

Nothing from this version. The server is on **0.5.7**, config untouched.

> 0.5.9 was built and deployed on 2026-09-10 and rolled back the same hour; it never existed as a
> release and the number is not burned. Server and client were both restored and verified against
> their pre-deploy state. That rollback is why § 10 exists.

## Untested

All of 0.5.8. The oven classifier is verified against the real chain data offline and the pie seam
was traced through `BEPie.OnInteract` by hand, but nothing here has been run in game.

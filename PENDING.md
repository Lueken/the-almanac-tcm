# PENDING: almanactcm 0.5.9 (unreleased)

Everything staged for the next release, and nothing else. **Read this before building a zip or
deploying.** The tree may hold work you did not put there, put in by a session you cannot see.

Append as work lands; do not rewrite history here. When 0.5.8 ships, empty this file and set the
heading to the next version.

Convention: `CONVENTIONS.md` § 10 (workshop root).

---

## Staged in 0.5.9

Nothing yet.

## Not in the zip, needed at deploy time

- **COO `TierTotals` does not propagate.** It is not part of the three-way config merge (only
  `Techniques` and `Bonus` are), so an already-booted server keeps its old ladder no matter what
  ships. Any server other than The Quire still needs the 0.5.8 array written into its
  `ModConfig/almanactcm/COO.json` by hand.

## Last shipped

**0.5.8, deployed to The Quire 2026-09-10.** Five items: the butchering crash, the oven credit
exploits, the COO cooking XP retune (A through E), FAR's Stockman's Eye, and the WOO tree-seed
planting fix. The Quire's `COO.json` carries the new `TierTotals`.

Zip `sha256 2d616a95813b77c3`, verified identical on the server, in the client profile and in
`Releases/`. An earlier 0.5.8 build (`00353add...`) was deployed a few hours before the planting
fix landed; the drift it caused is resolved and it never loaded, because the server had not
restarted in between.

**The server still has not been restarted**, so none of 0.5.8 is live yet, and COO direct-heat
adopts `1.5/20` to `1.0/25` at that boot. ModDB upload is Jeffrey's to post, from the
`2d616a95` zip.

Nothing in 0.5.8 was run in game before shipping.

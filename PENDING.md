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

**0.5.8, deployed to The Quire 2026-09-10.** Four items: the butchering crash, the oven credit
exploits, the COO cooking XP retune (A through E), and FAR's Stockman's Eye. The Quire's
`COO.json` was given the new `TierTotals` at deploy. **The server has not been restarted yet**, so
none of it is live until it is, and the direct-heat adopt (`1.5/20` to `1.0/25`) happens at that
boot. ModDB upload is Jeffrey's to post.

Nothing in 0.5.8 was run in game before shipping.

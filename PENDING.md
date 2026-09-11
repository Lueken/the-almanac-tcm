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

## DRIFT: the server is holding an older 0.5.8

The Quire has `almanactcm_0.5.8.zip` at sha `00353add...`; the repo now builds `2d616a95...`. The
difference is the WOO tree-seed planting fix, committed `e15eedd` after the deploy. Same version
number, different bytes, which is the drift § 10 exists to stop, and it is recorded here rather
than tolerated quietly.

It is harmless so far only because **the server has never restarted since the deploy**, so no
build of 0.5.8 has ever been loaded. Resolve it by redeploying before that restart. Do not post to
ModDB from the older zip.

## Last shipped

**0.5.8, deployed to The Quire 2026-09-10.** Four items: the butchering crash, the oven credit
exploits, the COO cooking XP retune (A through E), and FAR's Stockman's Eye. The Quire's
`COO.json` was given the new `TierTotals` at deploy. **The server has not been restarted yet**, so
none of it is live until it is, and the direct-heat adopt (`1.5/20` to `1.0/25`) happens at that
boot. ModDB upload is Jeffrey's to post.

Nothing in 0.5.8 was run in game before shipping.

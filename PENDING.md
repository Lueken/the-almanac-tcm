# PENDING: almanactcm 0.5.9 (unreleased)

Everything staged for the next release, and nothing else. **Read this before building a zip or
deploying.** The tree may hold work you did not put there, put in by a session you cannot see.

Append as work lands; do not rewrite history here. When 0.5.8 ships, empty this file and set the
heading to the next version.

Convention: `CONVENTIONS.md` § 10 (workshop root).

---

## Staged in 0.5.9

- **Stranded consolidation marker (hotfix, the reason 0.5.9 exists).**
  `LedgerSystem.TryConsolidate` now clamps `LastConsolidatedBoundary` down to the current
  boundary when it sits ahead of the calendar, and says so in the server log. Ahead is never a
  legitimate state, and it was permanently fatal: the guard returned on every tick, so the
  player accrued practice they could never bank and nothing said why. The ledger file lives
  outside the world save (`Saves/AlmanacTcm/{world}-ledger.json`), so a backup restore, a
  regenerate under a reused world name, a folder carried between worlds, or a backwards time
  command all strand it. Diagnosed live 2026-09-11 on Vintage Story Industrial
  (Elitephoenix): every technique "settling at rest", zero banked in every ranked domain,
  across multiple unbroken in-game days. Clamping to *now* means the next real boundary brings
  the work in; nothing accrued is discarded. Self-healing, so anyone already stranded is
  repaired by updating.
- **Same stranding on the death marker.** `OnPlayerDeath` clears `LastDeath` when it is ahead
  of the world clock. It rides the player entity, so a world rolled back beneath it held the
  chain-death grace window open permanently and every death scattered nothing. Same player
  reported dying with no loss at all.
- **`/tcm nextday [player]`** takes an optional online player name, so an admin can repair a
  stranded ledger from outside instead of only their own, and it runs from the console when a
  name is given. The clamp heals; this forces the consolidation immediately rather than at the
  next boundary.

Files: `source/Engine/LedgerSystem.cs`, `source/Engine/TcmCommands.cs`. Branch: none, committed
straight to `main` as a hotfix.

Zip `Releases/almanactcm_0.5.9.zip`, `sha256 43a1acd2de8a90ae`, 25 entries (identical entry list
to 0.5.8, code-only). Built against 1.22.7. All three changes verified present in the shipped DLL
by decompile, not by trusting the build.

**NOT deployed and NOT run in game.** The Quire is still on 0.5.8 on disk and still has not
restarted, so 0.5.8 is not live either. A restart now brings up 0.5.9 if it is deployed first,
or 0.5.8 if it is not.

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

**LIVE as of the 2026-09-11 17:16 restart.** Boot verified clean: `almanactcm 0.5.8` loaded, zero
fatals, zero TCM errors, every domain configured, ledgers saving on schedule (16 of them). COO
direct-heat took its `1.5/20` to `1.0/25` adoption at that boot. ModDB upload is Jeffrey's to
post, from the `2d616a95` zip.

Nothing in 0.5.8 was run in game before shipping, so it is being exercised live now.

The Quire's ledger was audited against the 0.5.9 stranded-marker bug on 2026-09-11 and is
**clean**: 16 players, markers from 123 to 254, none ahead of the calendar. The Quire was never
exposed to it. 0.5.9 is not urgent here.

# Contributing to The Almanac: Trades, Callings & Mastery

Thanks for looking. This is a Vintage Story mod, MIT licensed, built by Venah.

## Read these two first

1. **`CONVENTIONS.md`** (workshop root), the nine engineering conventions every mod here follows.
   They are short, and most review comments you would otherwise get are already answered there.
2. **`docs/design/rank-bonus-design.md`** § *Framework* and § *Governing principles*, the design
   rules for domain ladders. The governing principles are marked **do not re-litigate**; they were
   settled with reasons and reopening them is not a code review conversation.

## Before you open a PR

- **A changed number brings its baseline.** State the vanilla value it replaces, which direction it
  moves, and sign and date the line. See `CONVENTIONS.md` § 1. A bare float will be sent back.
- **Guard and label any new Harmony patch.** Use the existing `Try(label, patch)` pattern in
  `source/AlmanacTcmModSystem.cs`. No bare `PatchAll`. See § 6.
- **New abstractions need a second shipping consumer.** If a seam has one caller, inline it. See § 4.
- **Third-party code carries its grant** in `THIRD-PARTY-LICENSES/` plus a header on the file. See § 8.
- **Domain rosters are append-only.** `source/Domains/DomainRoster.cs` says so and means it: the
  roster is transmitted by index, so reordering it corrupts live saves.
- **Server-authoritative.** This mod runs on a public multiplayer server. Anything the client asserts
  about its own progression is a bug.

## What is likely to be accepted

Bug fixes with a reproduction. Compatibility patches for mods TCM already touches. Localisation.
Documentation. Small, self-contained domain tuning that comes with the reasoning.

## What to discuss before building

New domains, new axes on an existing ladder, and anything that changes the Grandmaster cap or the
rank curve. These are design decisions with a long paper trail in `docs/design/`, and a PR is the
wrong place to have that conversation for the first time.

Balance changes land at season boundaries on The Quire, not mid-season, unless something is
gamebreaking.

## Reporting a bug

Say what you did, what happened, what you expected, and the mod list you were running. TCM ships
verbose categorised logging on by default; the lines are prefixed `[almanac:` and the relevant ones
are worth more than a description.

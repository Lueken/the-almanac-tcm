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

**DEPLOYED to The Quire 2026-09-11, not yet live.** Server `data/Mods` carries
`almanactcm_0.5.9.zip`, sha256 readback verified identical to the repo zip; 0.5.8 renamed aside
to `almanactcm_0.5.8.zip.pre059-bak`. Jeffrey's client profile synced the same way. It loads at
the next restart, and 0.5.8 is live until then. Pushed to GitHub (`ba7f1a6`). Zip also staged in
`~/Downloads` for the ModDB upload, which is Jeffrey's to post.

NOT run in game.

## Staged in 0.5.10

- **TEM learns at the edge of itself** (ruled 2026-09-11, Conjunction stability-toolkit
  session; applies to ALL TEM practice). `LedgerSystem.Log` scales TEM practice
  continuously by the practitioner's temporal stability: `raw *= 1 + (1 - stability) *
  temFrayXpBonus`, knob `temFrayXpBonus` in TEM's `Bonus` config (shipped default 1.0,
  so a whole meter is today's rates and an empty one doubles them). Reads the vanilla
  attribute - no Conjunction dependency; its draughts, ring drain and wayfaring all move
  the same number. Tree versions (modinfo + assembly) bumped to 0.5.10 with this entry;
  the deployed-awaiting-restart 0.5.9 zip is unaffected.

  Dev build verified on Ingenium-test 2026-09-11/12.

  **DEPLOYED to The Quire 2026-09-12, not yet live.** Release zip
  `Releases/almanactcm_0.5.10.zip`, sha256 c3708c2ed04d6151, 25 entries, sha readback
  from the server verified identical. 0.5.9 renamed aside to
  `almanactcm_0.5.9.zip.pre0510-bak` WITHOUT ever loading (deployed 09-11, no restart
  since), so the next restart takes the server 0.5.8 -> 0.5.10 in one step and 0.5.9's
  ledger fixes arrive inside it. Jeffrey's client profile synced. Patchnotes on GitHub
  (7d16eb0); ModDB post is Jeffrey's, from the c3708c2e zip.

## Staged in 0.5.11

- **The two Industrial Story guide chapters now gate on Industrial Story.** `smelters-yard.json`
  and `tall-fires.json` carried `"gate": "almanactcm"`, so they loaded into the book for every
  TCM user whether or not the mod they document was installed. 41 of Smelter's Yard's 63 blocks
  and 18 of Tall Fires' 24 carry `requires: ["industrialstory"]` (plus Toolsmith on
  `the-workbench`, SmithingPlus on `working-the-metal`), and the renderer has no empty-section
  suppression: a section whose every block is gated out still lays out its title, and the five
  with a `revealedBy` also printed a hint line promising content that could never arrive,
  because the key behind it can only be earned at an IS machine. Both now read
  `"gate": "industrialstory"` and go dormant as whole chapters, tab included.

  Tall Fires was already effectively hidden by its pack-level `revealedBy:
  almanactcm:is-first-smelter` (`GuideLibrary.IsVisible`); the gate makes the intent explicit
  and points the Industrial Story row of a `contents` block at it. Smelter's Yard had no pack
  reveal and was the chapter actually leaking.

  Checked and needing nothing: `arcana.json` already gates `rustboundmagic` (same modid
  `ArcPatches.cs:54` gates on, so it was never visible without RBM); `engineering`, `mastery`,
  `trades` and `the-turned-earth` have no mod-gated blocks and document TCM's own mechanics
  (the overspeed smoke is `EngOverheatPatches`, not Ingenium).

  No orphaned references: the only inbound `almanac://chapter/` links to either chapter are
  between the two of them, so they appear and disappear together, and `QuestStepIndex` builds
  from the post-gating pack list, so no quest-step toast can fire for a step in a chapter the
  player cannot open.

  Cost, stated plainly: a TCM user without Industrial Story now sees no metallurgy chapter at
  all. The 22 blocks that survived without IS were mostly framing prose around IS mechanics,
  but they also carried the MET domain's milestone structure. If TCM-only players should have
  metal documentation, it wants its own short vanilla chapter, not this one half-lit.

  Files: `assets/almanactcm/almanac/guides/smelters-yard.json`,
  `assets/almanactcm/almanac/guides/tall-fires.json`. One line each. Tree versions NOT bumped;
  no zip built, nothing deployed.

- **POT: the hard broad-stroke gate becomes THE CLEAN STROKE** (`source/Domains/PotModeGate.cs`,
  rewritten). Raised by LauCaRo/BAR on Discord 2026-09-13 ("clayforming feels miserable during the
  early game"), ruled here 2026-09-14.

  The gate refused the 2x2 below Apprentice I and the 3x3 below Journeyman I, adding and removing
  alike. Two findings killed it. First, vanilla clamps a broad stroke to `LayerBounds(layer)` — the
  BOUNDING BOX of the recipe's voxels on that layer, not the outline — so vanilla itself scatters
  clay into cells the recipe does not want; measured across all 31 vanilla clay recipes the gap
  runs 6% (flat tool molds) to 62% (fourcrock, clayplanter), median 31%, and not one recipe is
  zero. The gate took the fast strokes away on top of vanilla's own mess-making. Second, vanilla's
  `OnRemove` REFUNDS (`AvailableVoxels++`), so gating removal risked nothing whatsoever and cost
  only clicks. LauCaRo was right and the Discord answer ("would require creating a tool mode") was
  wrong: `mouseBreakMode` is already a parameter of the patched `OnUseOver`.

  Nothing is refused now, at any rank. Below the rung a broad stroke is BLUNT (vanilla, strays and
  all); at the rung it is CLEAN — adding places only voxels the recipe wants, removing strips only
  voxels it does not and leaves the shape standing. `Place2x2GatePOTLevel` (5) and
  `Place3x3GatePOTLevel` (9) keep their names, values and ruled rungs, and stop being locks. NOTE
  the names now read wrong for what they do; renaming was skipped deliberately, because a renamed
  key silently resets any server that tuned it. Rename at a wipe if ever.

  Deterministic on purpose, and that is the ruling. A failure roll was considered (LauCaRo's own
  second suggestion, "gambling with your piece") and rejected on two grounds: it would have to be
  server-authoritative to avoid desync, and it reproduces the exact complaint Vinni_Pukh is making
  about the BRE spoilage taper in the same thread — a loss the player can neither see coming nor
  answer. Blunt versus clean is visible the instant it happens and is overcome by technique
  (outline at 1x1, interior broad) rather than by waiting for a level.

  **This also puts POT back inside the framework's own law** ("Untrained hurts you. Novice removes
  the penalty. Everything above raises you."). The old gate was a penalty running to Journeyman I,
  in breach of it. Under the clean stroke nobody is ever below vanilla: blunt IS vanilla, clean is
  above it, and POT's Axis 1 penalty is carried where it always was and where it belongs — the copy
  stroke, 2 voxels Untrained, vanilla's 4 restored at Novice I, climbing to 6 at Master I.

  Measured effect, greedy fill of one layer, blunt versus clean 3x3: solid base layers barely move
  (bowl L0 7 -> 5, storagevessel L0 21 -> 17) because a slab is all interior. Wall layers collapse:
  a 1-thick wall admits NO safe broad click at all, so blunt degrades to one click per voxel.
  claypot L1 24 -> 8, crock L1 16 -> 5, storagevessel L2 44 -> 15, fourcrock L1 64 -> 18. Between
  66% and 72% fewer clicks on exactly the hollow vessels the complaint was about.

  Harmony targets verified by reflection against the real 1.22.7 `VSSurvivalMod.dll` (the local
  `vssurvivalmod/` source tree is 1.22.2, so its signatures are NOT authority): private
  `OnAdd(int, Vec3i, int)` — two overloads, so the explicit `Type[]` is required — private
  `OnRemove(int, Vec3i, BlockFacing, int)`, private `OnCopyLayer(int)`, and the four-argument
  `OnUseOver`. A missed target throws inside `harmony.PatchAll` at `AlmanacTcmModSystem.cs:104`,
  which is NOT inside a `Try()`, so it would take TCM's whole startup with it.

  The one-frame player handoff (`StrokeContext`) now carries a `Finalizer` as well as clearing on
  entry, because a Postfix alone does not run when the original throws and a stale level would
  apply to the next player through the method. `[ThreadStatic]`: integrated server and client run
  this type on two threads in one process.

  **Dev build, NOT deployed anywhere.** Tree versions bumped to 0.5.11 (modinfo + assembly).
  `bin/almanactcm_0.5.11_clean-stroke-dev1.zip`, sha256 `907d5d273c224fea`, 25 entries, built
  against 1.22.7, installed to the Ingenium-test profile only; the previous
  `almanactcm_0.5.10_tem-fray-xp-dev1.zip` there was renamed `.retired` rather than deleted, since
  two copies of one modid is an "Assembly already loaded" boot failure. The Quire is untouched and
  still on 0.5.10.

- **BRE: the ferment classifier inverts to an ALLOWLIST** (`source/Domains/BrePatches.cs`;
  the Vinni_Pukh and MasterKjed Discord reports, built 2026-09-14). The old classifier was a
  denylist: tanning, dye baths, and reagent prep were excluded and EVERYTHING else fell through
  to "this is brewing", taking the BRE grant, the spoilage taper, and the Brewer's Mark. Written
  against vanilla's recipe list, it swallowed the modded pack whole: 495 distinct sealed barrel
  outputs on The Quire, of which the true ferments are about forty. Confirmed casualties:
  composting (could spoil INTO rot at half stack), wool washing, offal rinsing, plank/hay aging,
  hive parts, canopies, corroded copper, dye production from flowers, and Conjunction's own
  draughts, all rolling up to 50% rot for a BRE-untrained sealer.

  Now a sealed recipe must be positively recognised as a ferment: the existing preserve
  substrings (plus `limeegg`, `fermentedfish`) or the beverage list (`ciderportion`,
  `neolithicwine`, `yeastwater`, built from the enumerated Quire set). Everything unrecognised
  is INERT: no grant, no sealOwners stamp, so no spoilage or Mark can ever reach it, and one
  log line per code per session names it for future enumeration. A denylist's misses punish
  players; an allowlist's misses under-pay, visibly.

  Rode along: (1) `IsDyeing` extended for the MasterKjed case: tannin and mordant baths on
  textile outputs (twine/cloth/fiber/wool) now route to TAI like dye-portion baths, so the wool
  mod's browns, mordanting and such pay the Tailor instead of rolling ferment dice; washing and
  bleaching stay unmatched on purpose (prep, earns nothing, can no longer rot). (2) `IsTanning`
  no longer matches "spelt" via "pelt": spelt cider and Fermentaria's spelt wines had been
  paying HUN tanning instead of BRE since the classifier was written (found by simulating the
  classifier over all 495 outputs; the simulation is how the allowlist was validated too:
  24 beverage / 16 preserve / 31 tanning / 260 TAI dye / 2 non-earning / 131 inert).

  Behaviour change on The Quire worth announcing: wool washing, offal rinsing and composting
  stop paying BRE XP (they were never brewing). Dye PRODUCTION (flowers to dye portions,
  pigments) is inert rather than TAI for now; whether pigment-making should pay TAI is unruled.

  The spoilage TAPER itself is untouched by this entry; its replacement (the deterministic
  "turn" intervention system, discussed same day) is designed but NOT built, pending Jeffrey's
  numbers: safe fractions per rank, window widths, the mercy rule. Key clarifications ruled in
  discussion: turns are separate mandatory events (miss any one, lose the batch), count = one
  per safe-fraction beyond the first, window burns only while the SEALER is online, zero turns
  from Journeyman I.

  Dev build 2: `bin/almanactcm_0.5.11_clean-stroke-dev2.zip`, sha256 `3adaec6dd559bc43`,
  25 entries, in Ingenium-test (dev1 retired in place). Untested in game.

- **BRE: THE TURN ships** (`source/Domains/BreTurnSystem.cs` new; `BrePatches.cs`,
  `BreDomain.cs`, `BreBarrelRead.cs`, `lang/en.json`; ruled and built 2026-09-14). The spoilage
  taper's blind completion roll is retired from gameplay. In its place: a sealed ferment can
  TURN partway through, the cask says so, and answering in time saves the batch. The only path
  to rot is being online, told, and absent.

  The shape as ruled: turns per batch = one per safe-fraction beyond the first (safe 1/4
  Untrained and Novice, 1/2 Apprentice, 3/4 Journeyman, everything at Master I: the "always
  SOME need to check until Master" ruling, so Master I finally feels like something in BRE).
  Untrained always owes at least one, the teaching turn. Fire points drawn per seal from world
  RNG in the 35-75% band, min gap 5%, STORED, so no cask repeats another's rhythm (the variance
  ruling) but a sealed cask is deterministic and the Apprentice+ read can honestly foretell it.
  Window burns ONLY while the SEALER is online (6/12/18/24 in-game hours by band, knobs);
  expiry marks the batch and it completes as rot at half stack through the old path. A ferment
  reaching its end with a turn outstanding HOLDS sealed ("stands ready, wants tending"), never
  rots waiting, never collects untended: the log-out dodge postpones, it does not evade. Any
  player may tend, not only the sealer.

  The tend rides the sealed barrel's otherwise-dead right-click: beverages want an empty hand
  (the skim), preserves want one salt, consumed (the classifier's existing beverage/preserve
  split IS the verb split). Wrong hand gets an ingame error naming what it wants. Mouseover
  state is UNIVERSAL (rank buys the window, never the knowledge); Apprentice+ additionally
  reads when a quiet cask will next want tending.

  Plumbing: state lives in the barrel's own tree attribute (`almanactcmTurn`, packed string)
  via To/FromTreeAttributes postfixes, so persistence, client sync and broken-barrel cleanup
  all ride vanilla. The craft hold works by the existing OnEvery3Second prefix (now bool)
  skipping vanilla's tick while a turn stands at completion. All patch targets verified
  declared on 1.22.7 by reflection. Knobs in BRE Bonus: turnSafe*/turnWindow* per band,
  turnTeachingUntrained.

  Known edges, accepted or deferred: (1) Fermentaria fermenter has NO turns in v1 (barrel-read
  precedent) and the taper is retired there too, so clay-fermenter ferments are risk-free until
  the follow-up: strictly kinder than the dice they had. (2) A cask that slept through several
  fire points serves them serially on return, one tend each. (3) No login ping for a turn that
  fired while offline; the mouseover and the hold cover it. Candidate polish. (4)
  `DomainFigures` still publishes the OLD taper's percentages and BreDomain.SpoilChance stays
  for that reason alone; revise figures and any prose before 0.5.11 ships. (5) `spoilUntrained`
  remains in shipped defaults marked retired, so existing server configs do not churn.

  **TESTED LIVE 2026-09-15 on conjunction-test (dev5/dev6). The mechanic works.** Validated:
  the Apprentice foresight line ("It will want tending in about 5 days" on a 14 day pickle seal,
  inside the 35-75% band); the salt tend, which caught a turning carrot batch; and the expiry
  path, where a missed window gave the chat line and 50 carrots completed as 25 rot, the
  half-stack rule exactly. **Whole-batch rot CONFIRMED as intended** (Jeffrey: "that's how
  pickles work if you don't catch and correct it") - the yield-dock alternative is dead. The dye
  route was also exercised and did not rot, which is the allowlist holding.

  **BOOT CRASH, fixed in dev5.** `FromTreePatch.Postfix` named its parameter
  `worldAccessForResolve`; the real one is `worldForResolving`. Harmony binds by NAME, so
  `PatchAll` threw and took TCM's whole `Start` down - the exact hazard flagged when the clean
  stroke was written, walked into anyway because the barrel parameter names were read off the
  stale 1.22.2 source tree instead of the shipped DLL. All seven barrel patch parameters have
  now been verified by reflection against 1.22.7; that was the only mismatch. A build-time
  validator over every `[HarmonyPatch]` in the assembly would make this class of failure
  impossible and is worth having, given `PatchAll` failing is total.

  **WINDOW CLAMP, fixed in dev6.** The tend window burned on the raw calendar delta, so any JUMP
  consumed it in one tick: an admin `/time set` (how it surfaced) and, the one that matters in
  play, a chunk reloading after the sealer wandered off for days - which would rot a batch
  without the player ever seeing the turn, the exact blind loss this system exists to abolish.
  Burn is now clamped to `turnWindowMaxBurnPerTick` (0.25 in-game hours, ~20x a normal 3-second
  tick, so it never binds in real play). Turn FIRING still keys off calendar progress, so a cask
  whose fire point passed while unloaded turns the moment it loads. New board item
  `bre-turn-window-clamp` carries the regression test.

  Dev build 3: `bin/almanactcm_0.5.11_clean-stroke-dev3.zip`, sha256 `5ae6cd05a6442392`,
  25 entries, in Ingenium-test (dev2 retired). Untested in game. Test script: seal a quarter
  batch at BRE 0 (owes exactly the teaching turn), watch mouseover through the 35-75% band,
  tend it (cider empty-hand, pickle with salt), let one expire for the rot path, and check a
  Master-set player seals with no state at all (`/tcm setlevel BRE 13`).

- **Pacing: both XP ladders doubled** (`source/Config/DomainConfig.cs`, `source/Domains/CooDomain.cs`;
  ruled 2026-09-14). Default `TierTotals` `{150, 500, 1400, 3200, 6500}` -> `{300, 1000, 2800,
  6400, 13000}`; COO's own ladder `{188, 625, 1750, 4000, 8125}` -> `{376, 1250, 3500, 8000,
  16250}`, keeping its 1.25x ratio exactly.

  WHY. The ladder was costed in "days" while a consolidation boundary is an in-game DAY, 48 real
  minutes at the stock calendar. A three-hour session therefore collected about four daily caps,
  and Journeyman I (the iron gate, `MetMaterialGate`) arrived in roughly 11 real hours of play
  against a design intent of real-world weeks. Found by walking RileyVangurd's Journeyman IV on
  a four-day-old server: legitimate play, ~3.5 h/day, the curve was simply that fast. Doubling
  is the honest lever. Cutting Smax would have paid for the same pacing by shrinking every
  number the player reads, which is the complaint that drove the 0.4.x COO retune.

  WHAT IT BUYS, at the shipped default:

  | | Novice I | Apprentice I | Journeyman I (IRON) | Master I | Grandmaster |
  |---|---|---|---|---|---|
  | singleplayer, 48-min days | 2.4 h | 8 h | 22 h | 51 h | 104 h |
  | The Quire, CSM 0.25 (96-min days) | 4.8 h | 16 h | 45 h | 102 h | 208 h |

  A server slowing its calendar scales on top of this for free, which is why The Quire needs no
  override: CSM 0.25 (set live 2026-09-14, ruled "I like that dilation better") doubles it again.
  A 5-month season at 2 h/day is ~187 boundaries against 130 for a Grandmaster, so a season
  yields about one GM, or two or three Masters, or broad Journeyman competence across the
  22-domain roster. That is the intended shape and it makes the lifetime cap of two GMs mean
  something.

  CONFIRMED SAFE, by decompiling the shipped assemblies rather than inferring: vanilla hunger
  (`EntityBehaviorHunger.OnGameTick`: `num *= Calendar.SpeedOfTime * Calendar.CalendarSpeedMul
  / 30f`) and HydrateOrDiedrate thirst (`EntityBehaviorThirst.CalculateMultiplierPerGameSecond`:
  `SpeedOfTime/60 * CalendarSpeedMul/0.5`) are BOTH calendar-scaled, and food spoilage
  (`Collectible.cs` transitions) and crop growth (`BEFarmland.cs:202`) run on calendar hours. So
  changing CSM keeps consumption, spoilage and growth in lockstep per in-game day; it was safe
  to slow The Quire's clock without touching `playerHungerSpeed`.

  Also corrected in the same pass: a longer in-game day does NOT widen a dead zone after the
  daily cap, because `Smax` is PER DOMAIN. Longer days give more real time to work several
  domains inside one boundary, so CSM 0.25 actually suits a 22-domain roster. And
  `TryConsolidate(..., atLogin: true)` fires on `PlayerNowPlaying`, so a short session that
  never crosses 3am banks at next login and loses nothing.

  **DEPLOYMENT, and this is the whole trap.** `TierTotals` is NOT part of the three-way config
  merge (only `Techniques` and `Bonus` are). Shipping this reaches NEW worlds only. The Quire
  keeps `[150, 500, 1400, 3200, 6500]` in `ModConfig/almanactcm/*.json` until the arrays are
  written in by hand, or the config folder is deleted and regenerated. Before deleting that
  folder: audit it against the code defaults first, because COO's 0.5.8 `TierTotals` was
  hand-written there and anything else tuned since launch goes with it. Diff not yet run.

  **WIPE-BOUNDARY WORK.** Doubling revalues every ledger already earned; Riley's Journeyman IV
  becomes roughly Journeyman I against the new curve. Rides the next season wipe (cadence
  leaning 4-6 months, unruled).

  **RULED OUT, same day, do not build: the season-age floor on the iron tier.** Proposed as a
  calendar backstop (iron also requires the season to be N real days old) to deliver "nobody
  gets iron in the first month no matter how much effort". Dumped on the right argument: the
  rank gate stops a player WORKING iron, never HAVING it, so a late joiner buys or trades for
  iron tools from a smith who earned the rung. A calendar floor would have blocked the smith
  while doing nothing about access, and it silently assumed everyone starts on day one of a
  season. Pacing rests on the ladder and the server's calendar instead. Accepted consequence:
  a 6 h/day player can still reach Journeyman I in about a week at CSM 0.25.

  Dev build 6 (current): `bin/almanactcm_0.5.11_clean-stroke-dev6.zip`, sha256 `eb8664396abe04bb`,
  25 entries, in BOTH conjunction-test and Ingenium-test (dev4 and dev5 retired in place).

  **RULED 2026-09-14, not doing: firing time never varies by potter rank.** Raised by Vinni_Pukh
  in the same thread (slower firing untrained / faster at rank). Jeffrey ruled it out. A pit kiln
  is a fire, not a hand; rank belongs to what the hands do to the clay, not to how the fire behaves
  afterwards.

  **Untested in game.** Wants: a hollow vessel (crock or claypot) worked at Untrained and again
  above the rung; a flat tool mold, which should feel identical either way; the copy stroke, which
  must be unchanged; and the pottery wheel, which must be untouched.
  UPDATE 2026-09-14: the clean stroke was tested live by Jeffrey and PASSES — a 3x3 on a vessel
  wall placed only into recipe voxels, hollow left untouched. Removal half and the tool-mold
  case still unverified.

- **Test plan written for the turn system**: `docs/testing/2026-09-14_bre-turn-system-test-plan.md`,
  15 cases with expected outcomes, paste-ready for the trial checklist Doc. Flags two things the
  recipes exposed while writing it: every real ferment is long enough (cider 168 in-game hours,
  pickles 336, cured meat 480) that testing REQUIRES advancing the calendar, and cured meat has
  no liquid ingredient at all, so `BatchFraction` falls back to a full barrel and draws maximum
  turns for any size. The fallback is conservative rather than wrong, but it wants confirming.

- **Fermentaria's hooks never wired, for the life of the feature (bug, found 2026-09-15).**
  `BrePatches.PatchConditional` looked up `Fermentaria.BESimpleFermentingContainer` and
  `FermentariaForked.BESimpleFermentingContainer`. The real type is
  `Fermentaria.BlockEntities.BESimpleFermentingContainer` (verified by decompiling
  `FermentariaForked.dll` 1.0.4). Harmony's `TypeByName` compares its fallback against
  `Type.Name` using the WHOLE string, so a wrong namespace returns null rather than
  near-missing. Both candidates failed, `tf` was null, and the else-branch then logged
  "BRE fermentaria absent" with the mod plainly installed — which is why this survived: the
  only evidence it produced was a line that read like the correct, expected outcome. Every
  clay-fermenter seal has therefore banked no BRE practice and carried no Brewer's Mark. The
  else-branch now distinguishes absent from present-but-unhooked and WARNS on the latter.

- **The Turn now runs on the clay fermenter** (ruled 2026-09-15; closes the scope gap the drift
  audit turned up). `BreTurnSystem` was typed to `BlockEntityBarrel` throughout; it now takes
  `BlockEntity` and reads the four members that matter through a small guarded vessel layer
  (`VesselSealed` / `VesselRecipe` / `VesselInv` / `VesselCapacity`), direct-cast for the barrel
  and reflective for the fermenter. Two real differences, handled rather than assumed:
  the fermenter runs FIVE inventory slots to the barrel's two, so `BatchFraction` now finds the
  batch liquid by SCANNING slots instead of trusting index 1; and it holds 30 litres to the
  barrel's 50, so the same absolute batch is a larger fraction of it and rightly owes more
  tending. Tend and tree-persistence were extracted to shared entry points (`TryTend`,
  `WriteTree`, `ReadTree`) so both vessels answer a turn identically and neither can drift from
  the other. All four new Harmony bind points were verified against decompiled metadata BEFORE
  building — `OnBlockInteractStart(world, byPlayer, blockSel)`, `ToTreeAttributes(tree)`,
  `FromTreeAttributes(tree, worldForResolving)`, `OnEvery3Second(float)` — which is the check
  whose absence caused the 0.5.11 boot crash.
  **VERIFIED 2026-09-16 (boot, Ingenium-test against fermentaria-forked 1.0.4):** the type lookup
  resolves and the log reads `BRE fermentaria clay-fermenter hooked (seal grant + completion + the
  Turn + tend)`, which is the line that read "fermentaria absent" for the life of the bug.
  **STILL UNVERIFIED IN PLAY:** a sealed clay fermenter carried through a turn, a tend, a miss,
  and a chunk unload/reload. The hook firing at boot is not the same as the mechanic working.

- **`TurnSafeApprentice` 0.50 -> 0.25** (ruled 2026-09-15), in all three places that carry the
  default: the knob, `BreTurnSystem.SafeFractionFor`, and `DomainFigures.BreFigures`. A full
  vessel now asks 3 turns at Untrained, Novice AND Apprentice; those rungs separate on the
  WINDOW (6/12/18 in-game hours), not on the fraction. Journeyman stays 0.75 (1 turn, 24h) and
  Master is still called by nothing. The Apprentice rung page was rewritten to match: it had
  read "Half a barrel ferments unattended", which the change made false, and it now says plainly
  that the fraction does not move and what the rung buys is time.
  **Config note (corrected 2026-09-15 by reading the live server, not by assuming).** The Quire's
  `BRE.json` dates from Aug 28 and predates the Turn entirely: its `Bonus` block holds only
  `spoilUntrained`, `portionUntrained`, `measureChanceGm`, `measureBonusFraction`. There is no
  `turnSafeApprentice` key to win, and `LedgerSystem` adds an absent knob at the shipped value,
  so 0.25 lands on the next boot with no hand edit. The earlier warning here was wrong.

- **HUN trap bullets credited to Primitive Survival** (drift audit, 2026-09-15). The Untrained
  penalty (`{trapFailU}`) and the Grandmaster proc (`{trapFailGm}` / `{trapStaySetGm}`) both
  describe snares and deadfalls, which exist ONLY with Primitive Survival: `TrapPlacePatch`
  registers only `PrimitiveSurvival.ModSystem.BlockSnare` and `BlockDeadfall`, and the axes are
  consumed inside `TrapCollideMirror`. A TCM-only reader was being shown a penalty that cannot
  bite and a capstone that cannot fire, at both ends of the ladder. Same class as the TEM
  manifest-resist bug fixed earlier in 0.5.11.

- **`rungs.json` restored to house formatting.** The BRE and TEM rewrites had been written with
  `json.dumps(indent="\t")`, which expanded all 354 compact one-line bullet objects and buried
  ~20 real lines of change under 1,177 phantom insertions. Re-emitted with a serializer matching
  the file's own style (leaf objects with keys `{text}` or `{text, with}` stay on one line);
  verified against the committed blob, whose only other deviation is four hand-authored grants
  that had `"with"` tacked onto the end of the `"text"` line, now normalised. The real diff is
  ~30 lines. **Any future script touching this file must use the same serializer**
  (`scratchpad/housefmt.py`) or it will do this again.

- **Drift audit run across all 22 domains, 2026-09-15 — results, so it is not re-run blind.**
  Three signals: verbs in code never named in the prose, mods gated on in code with no `with`
  credit, and tokens with no figure. Only the middle one found anything real. Of 10 flagged mod
  gates, 2 were genuine (the HUN trap bullets above) and 8 were correctly silent: GLA was a
  false positive (the script compared the `GlassMaking.*` NAMESPACE against modids; the roster
  already gates the whole domain on `glassmakingfork`); FAR/Primitive Survival, COO/ACA,
  MET/Industrial Story, ENG/Industrial Story and MIN/Stone Quarry are earning routes or compat
  shims for verbs the book already promises, not separate claims; MET/Combat Overhaul is a dual
  path (`TryPatchHonedCombatOverhaul` falls back to `PatchHonedVanilla`), so the Honed bullet is
  true either way; HUN/Butchering adds stations while the dressing penalty the page describes
  hooks vanilla `EntityBehaviorHarvestable.GenerateDrops`. Two known blind spots in the script
  itself: it never collected `with` from GRANTS (so ENG, MEL and RAN credits were invisible to
  it), and it never scanned client-side figure providers such as `HunClientFigures`. The other
  two signals are noise by construction — rung pages describe rank EFFECTS, not the list of
  earnable verbs, so "unnamed verb" is measuring the wrong thing, and its stemmer used Python's
  `rstrip("ing")`, which strips trailing characters rather than the suffix ("digging" -> "d").
  **The class of drift that actually bit twice this release — prose that is stale while every
  token still resolves — is invisible to all three signals and needs reading, not regex.**

- **THE PACING RULING DOES NOT REACH THE QUIRE. Read this before shipping 0.5.11.** Live config
  pulled and diffed 2026-09-15: all 22 domain files pin the OLD 1x ladder
  (`[150, 500, 1400, 3200, 6500]`, and COO `[188, 625, 1750, 4000, 8125]`), every one of them
  exactly 2.00x away from what 0.5.11 ships. `TierTotals` is NOT part of the three-way merge —
  only `Techniques` and `Bonus` are — so the file wins permanently and shipping the doubled
  arrays changes nothing at all on the live server. The whole point of that work (iron out of
  reach for a real-world month) is silently defeated unless the arrays are written into
  `ModConfig/almanactcm/*.json` by hand, or those files are deleted so they regenerate.
  The existing COO note below said as much for one domain; it is in fact all 22.

- **The Quire's three hand-tunes PROMOTED to shipped defaults (ruled 2026-09-15),** so the live
  server and a fresh install now agree and a delete-and-regenerate costs nothing:
  - `POT` `clayforming` Raw|K 1|30 to **3|14** (the faster clayforming grind, as played)
  - `POT` `firing` Raw|K 3|12 to **5|8**
  - `RAN` `steadyAimUntrained` 0.50 to **0.80**, moved in all THREE places that carry the
    default (`RanDomain`, `DomainFigures`, `RanPatches`), because a value present in only some
    of them answers differently depending on whether the config key exists: the trap
    `TurnSafeApprentice` had.
  Neither POT rate is surfaced as a figure or named in any rung page, so nothing to rewrite
  there. `steadyAimUntrained` WAS. The RAN Untrained page read "steadiness **halved** to
  **{steadyU}**", which has been rendering as "halved to 0.80" on the live server ever since the
  hand-tune, because the token updates and the adjective does not. It now reads "steadiness down
  to **{steadyU}** of a common hand's", prose that stays true at any value the knob takes.
  Worth generalising: an adjective describing a tunable magnitude is drift waiting to happen.

- **`ALC` `M` is stale on the live server: 2, where code ships 3 (found 2026-09-15).** Under the
  locked small-m rule `M` is the technique count, and ALC carries three techniques (`remedy`,
  `chemistry`, `reagentwork`) in BOTH code and the live file. `reagentwork` was added later and
  arrived through the merge, because `Techniques` IS merged, but `M` is not, exactly like
  `TierTotals`, so it never moved. ALC's saturation has been running on a count one short of its
  own technique list. Same failure shape as the ladder: a non-merged SCALAR going stale when the
  merged COLLECTION beside it grows. Worth checking whether any other non-merged scalar is
  derived from a merged one.

- **Config diff coverage, so the next pass knows where the holes are.** Verified against code on
  2026-09-15: `TierTotals` (all 22, all stale by 2x), `Bonus` and `Techniques` against their own
  shipped baselines (3 divergences, now promoted), `Smax` (all match), `M` (one stale: ALC), and
  `global.json` key by key (46 keys, IDENTICAL, nothing hand-tuned there, including the
  `Place2x2/3x3GatePOTLevel` stroke gates). **NOT verified:** `affinity.json` and
  `FAR-yields.json`, neither of which carries a `Shipped...Baseline` record, so there is nothing
  cheap to diff them against and a generated reference would be needed; and `Adjacency`, where
  the throwaway checker parsed zero domains out of the source and so returned a vacuous pass
  rather than a real one. Do not read those three as clean.

- **Clean-stroke gates verified against the live server.** `global.json` carries
  `Place2x2GatePOTLevel: 5` and `Place3x3GatePOTLevel: 9`, identical to the code defaults in
  `TcmGlobalConfig.cs`, so the clean stroke gates at Apprentice I and Journeyman I on The Quire
  as designed. No drift, nothing to do.

- **Smithing++ made TCM refuse to load at all (field report, Thalius, 2026-09-15). FIXED.**
  `modinfo.json` hard-depended on modid `smithingplus`. Smithing++ is the maintained continuation
  by the same author under modid `smithingplusplus`, so the dependency went unsatisfied, VS
  declined to load TCM, and NO TCM code ran. That is the whole explanation for every symptom
  reported: the mod doing nothing rather than doing part of something, `Saves/AlmanacTcm/` never
  reappearing (it is created on the ledger's first WRITE, `LedgerSystem.cs:1015`, which never
  happened), and creating the folder by hand changing nothing. The folder is also `AlmanacTcm`,
  not `AlmanacTCM`, which matters on a Linux server. A hard dependency turns an unsupported fork
  into a fatal one, and this is live on ModDB, so anyone on Smithing++ has had a dead TCM.
  The dependency was never justified: MET's core smithing verb hooks VANILLA
  `BlockEntityAnvil.OnUseOver` / `CheckIfFinished`, and all four Smithing Plus integrations were
  already guarded with warn-and-skip (two even carry the comment "hard dep, but guard anyway").
  Only the manifest made absence fatal. Changes:
  1. `smithingplus` dropped from `dependencies`; description reworded to say TCM integrates with
     either and runs without them.
  2. The four runtime gates now go through `MetConditionalPatches.SmithingPlusPresent(api)`,
     which tests BOTH modids. Verified by decompiling 1.10.3: same assembly name, same
     `SmithingPlus.*` namespace, and all SEVEN types plus the four members TCM resolves
     (`RecoverBitsFromWorkItem`, `OnSmithingFinished`, `GetGridRecipesAsIngredient`,
     `Core.Config`) present and identically named. Only the modid differs.
  3. `met-native-metal-forge-heating.json` entries DUPLICATED for the fork. This one is not a
     hook toggle, it is the data half: it is what puts `forgable` on metal bits and the four
     native nuggets. Left alone, the code would wake under Smithing++ and the items still would
     not be forgable, which is worse than off because it looks present. VS evaluates `dependsOn`
     as AND (`ModJsonPatchLoader`: `flag = flag && (present ^ invert)`), verified by decompile,
     so a second entry is the only way to express "either".

- **STILL OPEN: one guide paragraph stays hidden under Smithing++.**
  `assets/almanactcm/almanac/guides/smelters-yard.json` gates the bit-recovery paragraph with
  `"requires": ["smithingplus"]`. Illuminated's `ChapterRenderer.Gated` is AND over that array,
  so `["smithingplus", "smithingplusplus"]` would demand BOTH and is not the fix. The FEATURE
  works on the fork after the changes above; only the paragraph explaining it is hidden. Three
  ways out, and it is a design call: duplicate the paragraph with one gate each (works today,
  but shows twice in the freak case where both mods are installed), add a `requiresAny` to
  Illuminated's `GuideBlock` (the general fix, since forks keep happening, but it is an engine
  change with its own version bump), or drop the gate. Not chosen, not changed.

- **`harmony.PatchAll` is no longer all-or-nothing.** It is now the same walk Harmony does
  internally (`GetTypesFromAssembly` -> `CreateClassProcessor(type).Patch()`) with each class in
  its own try/catch, plus a `ReflectionTypeLoadException` guard. Before this, the first of 82
  annotated classes that failed to resolve threw, `Start` aborted, and the mod sat loaded-but-dead
  with one line in `server-main.log` as the only evidence: no ledger, no save folder, no domain
  doing anything, indistinguishable from not being installed. That is how 0.5.11-dev4 died (one
  wrong parameter name on `BlockEntityBarrel.FromTreeAttributes`) and it produces exactly the
  field-report signature above. The conditional patches had been isolated since the 0.3.85
  lesson; the annotated ones never were. A failure now costs its own class, and the summary line
  says plainly that the mod is "running with gaps, not dead".

- **The one real API difference between Smithing Plus 1.9 and Smithing++ 1.10.3, handled.**
  `CollectibleExtensions.GetGridRecipesAsIngredient` returns `IEnumerable<GridRecipe>` in 1.9 and
  `IReadOnlyList<GridRecipe>` in 1.10.3. The other two patched members are byte-identical.
  `GridRecipeIndexPatch.Prefix` declares `ref IEnumerable<GridRecipe> __result`, and it BINDS
  against both: Harmony's rule (verified by decompiling `MethodCreatorTools`, the
  `!type.IsAssignableFrom(returnType)` throw) is that `__result` must be assignable FROM the
  return type, and `IEnumerable<T>` is assignable from `IReadOnlyList<T>`. The risk was the
  write-back: the value goes through a byref into a local typed as the REAL return type, so on
  the fork whatever is assigned must itself be an `IReadOnlyList<GridRecipe>`. Indexed values are
  `List<GridRecipe>` and qualify; `Enumerable.Empty<T>()` guarantees nothing. Swapped for a static
  empty `List<GridRecipe>`, so one build is correct against both signatures. The file's "PINNED to
  1.9.0-rc.1" header is now a verified-against-both note.
  **Still worth an in-game check on the fork**, since this is reasoned from IL rules, not observed.

- **POLICY (ruled 2026-09-15): Almanac mods hard-require only Jeffrey's own mods.** Every
  third-party mod is silent awaken/disable: a runtime probe, warn-and-skip on a missing seam,
  `dependsOn` on asset patches. A hard dependency does not degrade, it refuses, and VS resolves
  dependencies before any mod code runs, so the mod never loads and the symptom reads as "does
  nothing" rather than as an error. Audited all 9 manifests in vs-workshop: only `thequire`
  still carries a third-party dep (`immersivewoodworking`), and that one is deliberate, it is
  VS's only way to force LOAD ORDER, which its stick-behavior-index patch needs. Its patches are
  independently `dependsOn`-gated, so the dep buys ordering at the cost of total failure if IW
  ever leaves the pack. Jeffrey's server, Jeffrey's call. NOTE: `almanacilluminated` has no
  `modinfo.json` in the repo (only built copies under bin/), so it was NOT audited.

- **CORRECTION to the Smithing++ entry above.** Smithing++ 1.10.3 ships
  `SmithingPlus.Common.LegacyModIdPatch`, which postfixes `ModLoader.IsModEnabled` so that
  `IsModEnabled("smithingplus")` answers TRUE under the fork (config flag `AnswerToLegacyModId`).
  So the four runtime gates and the guide's `requires` would have woken anyway; the earlier claim
  that they went silently inactive was wrong. The HARD DEPENDENCY was the only real breakage,
  because that shim is a Harmony patch applied in the fork's own Start and cannot reach a
  dependency check that happens before any mod code runs. Two consequences worth keeping:
  the asset-patch duplication WAS still necessary (`ModJsonPatchLoader` tests a modid HashSet,
  not `IsModEnabled`, so the shim does not reach it), and the widened gates are still right,
  because relying on another mod's courtesy shim, which a user can switch off in config, is not
  a foundation.

- **The anvil-lag grid-recipe seam now STANDS DOWN on Smithing++.** 1.10.3 replaced the LINQ scan
  with its own index: `GetGridRecipesAsIngredient` is one line, `GridRecipeIndex.RecipesAsIngredient`.
  That is the fix reported upstream on 2026-08-02, adopted. TCM's prefix returns false and
  REPLACES the implementation, so left in place it would shadow a working index with a duplicate
  of TCM's own, the same registry indexed twice per side for no gain, with TCM's duplicate-
  preserving semantics imposed over theirs. `PatchConditional` now probes for the
  `GridRecipeIndex` TYPE and skips that seam when present, which also covers the original mod
  adopting the fix later. The tier memo is unaffected and stays: `GetRequiredAnvilTier` is
  identical in both builds apart from a null guard.

- **Swapping The Quire to Smithing++ is asset-safe.** Both zips carry the SAME asset domain
  (`smithingplus`, 63 files) and byte-identical asset path lists: 0 files unique to either side,
  72 shared, and identical `itemtypes`/`blocktypes`. Every item and block code is unchanged, so
  placed blocks and inventory survive the swap. What to watch instead: the modid changes, so any
  OTHER mod hard-depending on `smithingplus` will refuse to load (`TorchHolderSmithingPlus` is in
  the pack and wants checking), and Smithing Plus's own ModConfig may regenerate under a new name.

## Staged in 0.5.11 (Tailoring pass, folded in 2026-09-16)

- **TAI small-m 3 -> 2 (ruled 2026-09-16, beta feedback: "egregiously slow").** The reporter
  believed Tailoring only paid for loom, spinning wheel and dyeing. It actually has FIVE earning
  techniques (spin, weave, knit, sew, dye), so part of the complaint was not knowing about `sew`
  (vanilla clothing REPAIR, no station needed) and `knit` (needs the `Knitting` mod). But the
  measured part was real. Every domain shares `Smax = 100`/day, so the ceiling is identical
  everywhere; what differs is the work to reach it, and TAI needed **198 reps/day** to fill its
  cap, 19th of 22 and exactly double MET's 99. At m=3 a technique caps at 33.3, so the natural
  tailor loop of wheel + loom could never exceed **66.7 of a 100-point day** however hard it was
  worked. That ceiling, not the curve, is what "egregiously slow" was describing.
  At m=2 the two-station loop fills the day: **+50% banked for the same work** (55.7/day -> 83.5
  at 48 reps each), with Journeyman I still near 50 in-game days of dedicated craft on the 0.5.11
  ladder. Deliberately did NOT lower `spin`/`weave` K alongside: modelled, it moves the same case
  by only **~6%**, because K governs the approach to the ceiling and a real session is already far
  past it (x = 96 against K = 18). Lowering both would reach 94/day, which stops the domain feeling
  constrained at all and probably overshoots. Reconsider only after the two fixes below land.

- **The grid chain now pays, and the garment with it (built 2026-09-16).** See PATCHNOTES.
  What was open and is now CLOSED:
  1. **Crafting a new garment paid NOTHING.** `TaiMarkPatches.WearableCraftPatch` gates the `sew`
     grant on `__instance.Name.Path.Contains("repair")`, so only repair recipes pay. The code
     immediately above it stamps a new garment with the maker's Tailor's Mark, so the mod credits
     you as the maker and pays you nothing for the making. The headline activity of the trade is
     not an earning verb. Fixing it needs thought about the dedup key and whether every wearable
     should count, so it is not the one-line change it looks like. FIXED: `WearableCraftPatch`
     is now a router. Making and mending share `sew`, and the grant scales with CONSUMED material
     (0.5x-2.0x against a two-unit baseline; tools and returned stacks excluded, so the sewing kit
     is not counted as cloth). A repair consumes only its patch material and so scales itself down.
  2. **The per-minute bucket on the two main techniques.** RULED: it is the anti-grind guard and
     STAYS. The new grid `spin`/`weave` grants carry the same per-real-minute bucket so the hand
     path cannot be used to dodge it. Garments bucket per SECOND instead, because material cost is
     their limiter; that makes bulky garments the cheapest route to the cap (32 reps at the 2.0
     multiplier against weave's 81), accepted on the grounds that 32 bulky garments is several
     hundred flax fibres of farming. If that ever reads wrong, the lever is one character:
     `ms / 1000` to `ms / 60000` in the garment branch.
  OLD TEXT, for the record: `weave` keys on
     (loom position, real MINUTE) and wheel `spin` on (wheel position, real minute), so each pays
     **at most once per real minute per station**. Weave needs 81 reps to fill its share of a day;
     at one per minute that is 81 real minutes at one loom, against The Quire's 96 real minutes per
     in-game day. A tailor with one wheel and one loom therefore CANNOT reach the cap regardless of
     fibre. `knit` and `sew` bucket per real SECOND, so the throttle is specific to the two primary
     verbs. Find out what it was guarding before loosening it: the loom already consumes twine per
     operation, so cost may already be the limiter.

- **The M change will NOT reach The Quire on its own.** `M` is not part of the three-way merge
  (only `Techniques` and `Bonus` are), exactly like `TierTotals`, so the live `TAI.json` keeps
  `M: 3` forever. It needs hand-editing or regenerating with the ladder fix.
  Worth noting for calibration: The Quire also still runs the 1x `TierTotals`
  (`[150, 500, 1400, 3200, 6500]`), so its ranks already come twice as fast as a fresh 0.5.11
  install. A player finding TAI egregiously slow **on the halved ladder** is strong evidence that
  the m=3 ceiling was the binding constraint rather than the ladder length.

- **What 0.5.11 has actually been tested for, as of 2026-09-16.** Recorded because "it built" and
  "it works" are different claims and this release carried a lot of untested change.

  **VERIFIED in Ingenium-test** (194 mods, smithingplusplus 1.10.3, fermentaria-forked 1.0.4):
  - Boot: `annotated patches applied (80 class(es))`, no FAILED line. The per-class PatchAll
    rewrite works, which was the riskiest change in the release.
  - `Saves/AlmanacTcm/` written. The mod survives startup and reaches its ledger.
  - Smithing++ end to end: bit-recovery, reforge-strip, Honed via Combat Overhaul, forge heating
    (which proves the duplicated asset patch applied), and the cast-tool tier memo all hooked.
  - The grid-recipe seam STANDS DOWN on the fork, as designed.
  - Zero `[Error]` lines from any Almanac mod across 1,528 in the log; all belong to third parties.
  - TAI bulk grid crafting pays the batch (Jeffrey, 2026-09-16).
  - ALC remedy and MET grid-assembly batches likewise (Jeffrey, 2026-09-16).
  - The clean stroke's PLACE half (Jeffrey, 2026-09-14).

  **NOT verified, and should not be read as working:**
  - The Turn on a clay fermenter, end to end. Only the hook is confirmed.
  - The clean stroke's REMOVAL half, and the flat tool-mold case.
  - The metal-armour negative: crafting armour must grant NO Tailoring. This is the false positive
    fixed blind in `824d8b9` (vanilla armour is `Wearable` at every material, steel included), so
    it is the single highest-value check left.
  - `TurnSafeApprentice` at 0.25, and the RAN/POT promoted defaults, in play rather than on paper.
  - The guide re-gating in a world WITHOUT Industrial Story.

- **One crash seen, diagnosed, and not ours.** 2026-09-16 10:15, client-side:
  `InvalidOperationException: Collection was modified` in
  `WorldMapManager.<OnLvlFinalize>b__22_0` (VSEssentials line 214). That lambda walks
  `MapLayers`, a plain unsynchronised `List<>`, in a tight loop on a background thread, so any mod
  adding or removing a layer after level finalize throws. TCM registers into the TYPE registry
  during `Start()` and never touches `MapLayers`; the same build had booted clean two hours
  earlier. It is the FastMap race already recorded in memory, and a vanilla robustness bug.

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

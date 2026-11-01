# Changelog

All notable changes to CraftQueue, newest first. Dates are the Forge release
dates. Versions are written for players; the technical detail lives in the
commit messages and in `PROJECT-HANDOFF.md`.

CraftQueue runs in two lines: **2.x targets SPT 4.1**, **1.2.x targets SPT
4.0.13**. The 4.0 line is frozen and only touched for serious bugs.

## [Unreleased]

## [2.4.3]

### Forge version notes
- If the queue gets stuck because the game or the server stops answering, CraftQueue now writes a warning to the BepInEx log after 60 seconds that names the step it is waiting on, and a note once it continues. That log line shows where the problem is, for example when another mod blocks the game's connection to the server.

### Added
- A stall warning in the client. One queue cycle (collect, poll, web commands, start) runs at a time, and EFT's own hideout calls as well as SPT's backend route have no deadline of CraftQueue's: SPT's client allows 100 s per try, with retries. A call that never returns paused the queue without a trace. After 60 s the client now logs a warning with the current step (for example "waiting for the game to hand over the finished craft …") and an info line with the duration once the cycle finishes. Behaviour is unchanged otherwise: the waiting cycle is not abandoned, since a second cycle beside it could start the same craft twice.

## [2.4.2]

### Forge version notes
- Fixed: with some quest mods installed, the server log showed "Failed to prepare the crafting recipe cache at startup. Collection was modified" during startup. CraftQueue now waits until every mod has finished loading before it prepares its recipe list, so it no longer reads another mod's quest texts while that mod is still adding them.

### Fixed
- The recipe cache warmup started while SPT was still loading the remaining mods. Reading item names builds SPT's cached locale table and runs the locale transformers other mods registered (custom quests through SPT's `CustomQuestService`, which reads the quest mod's own dictionary). A quest mod that was still filling that dictionary made the warmup fail. In other timings, the locale table could also be cached before such a mod's texts were complete. The warmup now starts on the server's first update tick, after every mod's `OnLoad`. Item names in the recipe list are then complete for items that mods add late, too.
- CraftQueue's web server is already up during that load phase. Until the first update tick its API now answers "CraftQueue is still starting." (HTTP 503), so opening the page within the first seconds cannot build the recipe list early either. The page retries by itself.

## [2.4.1]

### Forge version notes
- Fixed: the Scav Case runs and the booze generator's moonshine were offered before you had built that station, and a queued scav case run even started there. The recipe browser now only lists stations you have built.
- Entries queued for a station you have not built yet now wait with the note "Waiting for construction" and start once you build it.

### Fixed
- Unbuilt stations offered recipes. EFT lists a recipe that names no level of its own station under level 0, which covers all five scav case runs and the booze generator's moonshine. The client only left out quest-locked recipes, and neither of SPT's start operations checks the station, so such a run really started and paid out. The client now skips stations below level 1 for the recipe list, for queueing and before starting.
- The server holds an entry whose station the profile has not built (or not upgraded far enough) instead of handing it out or starting it during a raid, and refuses to queue one. Runs that already started at such a station are still shown and collected.

## [2.4.0]

### Forge version notes
- New: queue Scav Case runs like crafts. Pick the 2,500 / 15,000 / 95,000 rouble, moonshine or intelligence folder case in the recipe browser under "Scav Case". CraftQueue starts the next run as soon as the case is free and collects the loot, during raids too.
- With the Crafting skill at elite the scav case runs two at once, like every other station. The same kind of run never runs twice at a time.
- Elite Charisma's discount on the rouble cases is applied, so you pay what the game charges.
- A running scav case cannot be aborted, just like in the game.

### Added
- Scav case runs (`scavRecipes`) as queueable recipes at the Scav Case station: shown with their cost per run and "random loot", grouped under "Scav Case".
- The client starts a run through the game's scav case operation and waits for the server's answer. It spends the payment locally first and rolls it back on failure, as the game's own scav case window does - SPT does not report a partly spent money stack back, which would otherwise leave the client planning with money that is gone.
- During a raid the server starts runs itself and charges the elite Charisma discount exactly as the game computes it; SPT charges whatever the start request names without checking it against the recipe.
- A start-confirmation hook for scav case runs, so a queued run gets its start-ledger record and the raid-rollback protection like a craft.
- The station report includes the scav case, so elite Crafting's second slot is used there as well.
- Per-profile requirement counts in the state, so affordability in the panel and in the in-raid queue check uses the discounted price.

### Changed
- Aborting a running scav case is refused: the game offers no abort, and SPT would keep the payment.

## [2.3.0]

### Forge version notes
- With the Crafting skill at elite, every ordinary hideout station can run two crafts at once. CraftQueue now fills both slots, outside raids and during them. Without elite Crafting nothing changes.
- The same recipe never runs twice at a time. If the next queued craft is one that is already running, the craft behind it takes the free slot instead of leaving it empty.
- A second craft that needs a tool the first one is using waits for it instead of failing and retrying.
- Fixed: queued crafts started during a raid could disappear when another mod put the pre-raid profile back afterwards, as some arena and practice mods do. CraftQueue now notices and returns them to the queue. If it cannot tell whether such a craft may already have finished, it asks you to review it instead of crafting it twice.
- Fixed: the workbench, medstation and nutrition unit no longer start crafts during a raid while the generator is off. The game itself does not allow that, and the crafts then ran at a crawl.
- Fixed: a station without power no longer shows its queued craft as "still occupied" and no longer pushes it into slower and slower retries. The entry now says it is waiting for power and starts as soon as the generator runs.

### Added
- Support for the second production slot per station that elite Crafting grants (`SkillsSettings.Crafting.EliteExtraProductions`). Outside raids the client reports every station's slot count and occupancy straight from the game; during raids the server derives the slot count from the profile's Crafting skill.
- A station holding two finished crafts has both collected, not only the first.

### Changed
- A queued craft whose recipe is still running, paused or waiting to be collected is never started over it; the next entry takes the slot instead. SPT would otherwise replace the running craft.
- An extra slot is only filled with a craft whose tools are in the stash. A failed tool check is repeated at most every 30 seconds.
- New start refusal `recipe_busy`, retried like a busy station.

### Fixed
- Crafts the raid executor starts, or that are confirmed while a raid runs, keep their start-ledger record until the raid is over; it is then checked against the final profile. A craft that is gone before it could have finished was rolled back (for example by a mod that restores the pre-raid profile after a match) and returns to the front of the queue; one that may have finished returns as Needs Review. The previous raid is settled before a new one starts, so back-to-back raids cannot resolve a record against a different craft of the same recipe.
- The raid executor honours a station's `NeedsFuel`: without a running generator nothing starts there, as in the game. The entry waits with a visible reason and no retry penalty.
- The client refuses a start at an unpowered station as `generator_unavailable` instead of `area_busy`, and reports the game's `CanStart` per station, so the server does not claim entries for such a station at all.

## [2.2.1] — SPT 4.1

### Fixed
- CraftQueue now notices when another mod changes a hideout recipe while the
  server is running. **HideoutCraftModifier** does exactly that, and until now
  its edits were invisible here: the panel kept showing the original craft time,
  yield and prices until the next server restart. Worse, the have/need numbers
  next to each ingredient were counted from the *edited* recipe while the
  ingredient list came from the old one, so a craft could sit greyed out as
  "not enough materials" with a full stash — or offer itself and then fail.
- Crafts that cost no materials at all are no longer offered. Only the two
  continuous producers look like that, and one of them is the bitcoin farm:
  if a mod turns its payout into an ordinary recipe, starting it through the
  queue would have wiped the coins the farm was holding.

## [2.2.0] — SPT 4.1

### Added
- The recipe browser now shows what a craft is **worth**: estimated material
  cost, what the yield sells for, the profit, and the profit per hour of craft
  time — so a two-minute craft can be compared with a two-day one. Two new sort
  orders put the worthwhile recipes on top.
- Every entry now states **how many items a craft yields** (`×5`, `×120`).
  That was never visible before; you had to know it. It shows in the queue and
  on running crafts too, and long names no longer push it out of view.
- Prices are rough market estimates (flea price, falling back to the handbook).
  Tools are left out of the cost, because a craft gives them back. Products
  without a market price — quest items, mostly — show their cost and say so
  instead of pretending to be a huge loss.

## [2.1.0] — SPT 4.1

### Added
- Crafts now spend **plain materials before found-in-raid ones** when both would
  do. FiR items are what quests ask for, so they stay in the stash. Recipes that
  explicitly require found-in-raid materials are unaffected and still get them.

### Fixed
- With the in-game panel open you can look around again. The panel covers the
  middle of the picture, and that is the exact point the game reads mouse
  movement from while you turn — so the movement went to the panel and the
  player stood still, with nothing anywhere to say why. The mouse now reaches
  the game whenever the game is the window in front; click the panel to use it
  again, or press F9. Needs Anvil-WebOverlay 1.8.8 or newer, and without it the
  panel behaves as before rather than disappearing.
- The cursor is released while the panel is in front, so it can be pointed at
  at all. This needs Anvil-WebOverlay 1.7.0, which is now the minimum for the
  in-game panel; an older library falls back to the external browser and says
  so once.

## [2.0.3] — SPT 4.1

### Changed
- The **CRAFT QUEUE** button in the bottom menu bar has its own icon now: a
  hexagon with a work list, in the muted gold of the bar. Before it showed the
  hideout glyph it was cloned from, which made it easy to mistake for a second
  HIDEOUT entry — the gold tint it was supposed to have never survived the
  button's own animation.

## [2.0.2] — SPT 4.1

### Fixed
- **Co-op (Fika):** while a raid was running, the queue could disturb other
  players' item transfers on a shared server. Nothing was ever lost, but on a
  server several people play on this one is worth updating for. Single-player
  installs are unaffected.

## [2.0.1] — SPT 4.1 — 2026-08-17

### Fixed
- On installs with mods that add their own hideout recipes, CraftQueue could
  fail to build its recipe list at startup and end up doing nothing at all. It
  now reads that list safely and also picks up recipes other mods add while the
  server is still loading.

## [2.0.0] — SPT 4.1 — 2026-08-09

### Changed
- Runs on SPT 4.1. Staying on 4.0.13? Version 1.2.2 remains available.

### Fixed
- Several crafts needing the same tool (a Leatherman, for example) can be
  queued again while the first one is already running. The tool comes back when
  that craft finishes, so the queue no longer counts it as missing.
- A rare conflict with mods built on WTT-ClientCommonLib: without
  Anvil-WebOverlay installed, item stack sizes could show as 1 (magazines
  showing a single round). Purely visual, nothing was lost.

## [1.2.2] — SPT 4.0.13 — 2026-08-06

Final release of the 4.0 line. Same two fixes as 2.0.0 above (same-tool
queueing, the WTT-ClientCommonLib conflict).

## [1.2.1] — SPT 4.0.13 — 2026-08-05

### Fixed
- The CPU spikes on heavily modded installs: the server no longer freezes for a
  moment whenever a craft starts or finishes. Crafts stay just as safe as before.
- The web interface feels noticeably snappier, especially with a big stash.
- No more alarming-looking error lines in the server console.

### Added
- The web interface port can be changed (`webPort` in `config/config.json`) when
  something else already uses 38473. Only the server needs to know — EFT asks it
  for the port.

## [1.2.0] — SPT 4.0.13 — 2026-08-02

### Added
- **In-game overlay:** the web UI opens as a window over the game, with a dark
  game-toned title bar that can also be turned off entirely. Needs the free
  [Anvil-WebOverlay](https://github.com/maschine34675/WebOverlay) library;
  without it everything opens in the external browser as before.
- A **CRAFT QUEUE** button in the bottom menu bar next to HIDEOUT (can be
  disabled in the config).

### Fixed
- The external-browser shortcut works now (left Shift+F9, configurable) — it
  silently never triggered before.
- The overlay's close key follows a rebound open key.

### Changed
- Routine log lines moved to debug level.

## [1.1.0] — SPT 4.0.13 — 2026-08-01

### Added
- Recipe browser with collapsible groups, selectable grouping (by station or
  category) and sorting.
- Material availability at a glance: recipes you cannot craft are greyed out,
  like in the hideout.
- Failed crafts can be dismissed.

## [1.0.0] — SPT 4.0.13 — 2026-07-31

First public release.

- Queue hideout crafts across all stations; each station works through its own
  list, all stations in parallel.
- Crafts auto-chain: finished items are collected and the next craft starts by
  itself — including **during raids**, where the server takes over and hands
  everything back cleanly on extract.
- Live overview of every running craft with names, icons, remaining time and a
  *paused* marker while the generator is off.
- Cancelling a craft returns its tools to the stash (vanilla destroys them).
- Entries are validated against the real stash and simply wait if materials go
  missing; the queue survives server restarts.
- Personal per-profile web links, so every player on a co-op server manages only
  their own queue.
- Works around a vanilla SPT 4.0.13 server bug that crashed every hideout craft
  cancellation.

[2.0.4]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.4
[2.2.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.2.0
[2.1.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.1.0
[2.0.3]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.3
[2.0.2]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.2
[2.0.1]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.1
[2.0.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.0
[1.2.2]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.2.2
[1.2.1]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.2.1
[1.2.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.2.0
[1.1.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.1.0
[1.0.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.0.0

# Changelog

All notable changes to CraftQueue, newest first. Dates are the Forge release
dates. Versions are written for players; the technical detail lives in the
commit messages and in `PROJECT-HANDOFF.md`.

CraftQueue runs in two lines: **2.x targets SPT 4.1**, **1.2.x targets SPT
4.0.13**. The 4.0 line is frozen and only touched for serious bugs.

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

[2.0.3]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.3
[2.0.2]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.2
[2.0.1]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.1
[2.0.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v2.0.0
[1.2.2]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.2.2
[1.2.1]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.2.1
[1.2.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.2.0
[1.1.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.1.0
[1.0.0]: https://github.com/maschine34675/CraftQueue/releases/tag/v1.0.0

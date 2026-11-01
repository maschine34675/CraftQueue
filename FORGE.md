# SPT-Forge page content

Version notes for the Forge version field are not kept here: copy the `### Forge version notes` block of that version from `CHANGELOG.md`.

Teaser (max 100 characters):

```text
Queue hideout crafts across all stations and manage them from a web UI - even while you raid.
```

Description (markdown):

---

**CraftQueue** moves hideout crafting into one local web page. Queue as many crafts as you like - every station works through its own list, all stations in parallel.

## What it does

- **Chains your crafts:** finished items are collected and the next craft starts by itself - also **during raids**.
- **Scav case on repeat:** queue the rouble, moonshine or intelligence folder case; it keeps running and the loot is collected.
- **Elite Crafting:** every station runs two of your queued crafts at once.
- **Live overview** of all running crafts with their remaining time - also while EFT loads, sits in a menu or is closed.
- **Recipe browser** with search, showing only what your profile has unlocked, with material availability, cost, profit and profit per hour.
- **In-game overlay:** with the free [Anvil-WebOverlay](https://github.com/maschine34675/WebOverlay) library, F9 or the CRAFT QUEUE button opens the page as a window over the game. Without it, the page opens in your browser.
- **Safe:** entries are checked against your real stash and wait if materials are missing. Aborting a craft returns its tools (vanilla destroys them). The queue survives server restarts, and anything unclear becomes a visible *Needs Review* entry instead of a silent duplicate or loss.

## Usage

1. Extract the archive into your SPT folder (`BepInEx` + `SPT_Runtime`). Optional, for the overlay: **Anvil-WebOverlay**.
2. Start the server and EFT, then press **F9** or click **CRAFT QUEUE** in the menu bar. Left Shift+F9 forces the browser; all keys are configurable.

The page runs on the server machine and is reachable over LAN. Every player gets a personal link that only sees their own profile; the host link in the server console manages all profiles.

## Playing with friends (Fika or a shared server)

The page has its own port, **TCP 38473**, separate from SPT's port. Each player's game opens it at the same address it uses for the SPT server, so that port must be reachable from every player's PC:

- Allow inbound **TCP 38473** in the firewall of the server machine.
- For players outside your home network, also **forward TCP 38473** on your router to the server machine, the same way as SPT's own port.

If the port is blocked, the queue itself keeps working, but the page and the F9 overlay do not load for those players ("took too long to respond"). To use a different port, set `webPort` in `SPT_Runtime/user/mods/CraftQueue/config/config.json` and open that one instead.

## Notes

- Requires SPT 4.1.x. (For SPT 4.0.13, use version 1.2.2.)
- **Fika-compatible** (tested): each player manages only their own queue, and the plugin is inert on a headless host. Open the web port for your friends (see above).
- Compatible with **Hideout In Progress**. **Not compatible with Hideout Automation's production stacking** - a second craft queue fighting over the same starts. Run only one of the two.
- Outside raids the queue only advances while EFT is running: crafts go through the game's own transactions, which is what keeps stash and profile in sync. Crafts that finish with EFT closed are collected at the next start.
- The water collector, bitcoin farm and cultist circle are not queued. A running scav case cannot be aborted, as in the game.
